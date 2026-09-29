using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Retention;

/// <summary>
/// Purges raw work-event content that is due (PRD §12; Gate 2 owner decision 5; Gate 2F).
/// </summary>
/// <remarks>
/// <para>
/// The database's own clock is the only retention clock. Content is due once the database's clock shows it at least
/// 672 hours old, the fixed 28-day purge age two days before the 30-day limit, measured from the receipt time the
/// database itself stamped when it stored the receipt; neither an ingress host's clock nor a time the provider states
/// ever decides. Every age the run reports is measured by that same clock. The purge reads no application clock, so a
/// Worker clock running behind cannot delay a purge or make late content look younger, and one running ahead cannot
/// purge early; the Worker's clock only times the run.
/// </para>
/// <para>
/// A run first asks the one approved tenant-less retention function which tenants hold due content. Each tenant is
/// then purged in its own dependency scope, as a trusted-service tenant context, and in its own transactions under
/// forced row-level security: a batch of that tenant's due receipts, oldest first, is locked with
/// <c>FOR UPDATE SKIP LOCKED</c>, and the same statement clears their payload and fingerprint. The receipt row stays
/// with every identifier, its receipt time and its state, because it is the durable duplicate key and the ledger's
/// causation. Nothing else is read or written: not the outbox, the ledger or the Snapshot.
/// </para>
/// <para>
/// Idempotent and retry-safe: cleared content never matches again, so a rerun, or a retry after a failure, clears
/// only what is left, and a failed batch rolls back whole. Concurrent runs skip each other's locked rows instead
/// of waiting or clearing twice, and a receipt that processing holds is left to a later run. Every step is
/// bounded: tenants per run, receipts per batch and batches per tenant.
/// </para>
/// <para>
/// Lateness is reported, never hidden. Content the run purged, or found left afterwards, at or past the overdue
/// age is logged as a warning, and at or past the 30-day limit as critical, and both are counted. Only content left
/// behind still needs attention. A run that cannot measure what is left fails, because it cannot show the limit
/// holds.
/// </para>
/// <para>
/// Content purged before processing settled its receipt means that event is lost; it is counted apart and
/// logged as a warning, and processing then refuses the receipt visibly. Hygiene: logs carry tenant ids, codes,
/// counts and ages only; the run's result and its metrics carry counts, ages and durations only.
/// </para>
/// </remarks>
internal sealed class RawContentPurge(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantContextAccessor,
    RawContentPurgeOptions options,
    EventBackboneMetrics metrics,
    ILogger<RawContentPurge> logger) : IRawContentPurge
{
    // Row-level security limits both statements to the scope's own tenant. The database's own clock alone decides
    // what is due, 672 hours after the receipt time, and measures the age of what the statement clears.
    private const string PurgeBatchSql = """
        WITH due AS (
            SELECT receipt_id
            FROM public.raw_receipts
            WHERE payload IS NOT NULL
              AND received_at_utc <= now() - interval '672 hours'
            ORDER BY received_at_utc, receipt_id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
        )
        UPDATE public.raw_receipts AS receipt
        SET payload = NULL, payload_fingerprint = NULL
        FROM due
        WHERE receipt.receipt_id = due.receipt_id
        RETURNING receipt.processing_state, now() - receipt.received_at_utc
        """;

    private const string OldestContentAgeSql =
        "SELECT now() - min(received_at_utc) FROM public.raw_receipts WHERE payload IS NOT NULL";

    /// <inheritdoc />
    public async Task<RawContentPurgeRun> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var run = await PurgeRunAsync(cancellationToken);
        metrics.PurgeRun(run);
        return run;
    }

    private async Task<RawContentPurgeRun> PurgeRunAsync(CancellationToken cancellationToken)
    {
        if (tenantContextAccessor.Current.IsResolved)
        {
            // Each tenant is set per purge scope. Running inside a scope that already carries a tenant would let
            // that tenant stand in for the ones the purge works for.
            throw new InvalidOperationException(
                "The raw-content purge must run in a scope without a resolved tenant; it sets each tenant itself.");
        }

        var started = Stopwatch.GetTimestamp();

        IReadOnlyList<TenantId> tenants;
        try
        {
            tenants = await DiscoverAsync(options.TenantsPerRun, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Raw-content retention could not find due content: {ExceptionType}; nothing was purged this run.",
                exception.GetType().Name);
            return Finish(new Purged(), visited: 0, discoveryFailed: true, remaining: true, oldestRemaining: null, started);
        }

        var purged = new Purged();
        foreach (var tenant in tenants)
        {
            purged.Add(await PurgeTenantAsync(tenant, cancellationToken));
        }

        // What is left: tenants beyond this run's reach, a tenant whose budget ran out or failed, or rows that
        // processing or another run held. The function lists the tenant with the oldest due content first, so its
        // oldest content is the oldest anywhere.
        IReadOnlyList<TenantId> remaining;
        try
        {
            remaining = await DiscoverAsync(1, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Raw-content retention could not check for due content left after purging: {ExceptionType}.",
                exception.GetType().Name);
            return Finish(purged, tenants.Count, discoveryFailed: true, remaining: true, oldestRemaining: null, started);
        }

        if (remaining.Count == 0)
        {
            return Finish(purged, tenants.Count, discoveryFailed: false, remaining: false, oldestRemaining: null, started);
        }

        var oldest = await OldestContentAgeAsync(remaining[0], cancellationToken);
        if (oldest.IsFailure)
        {
            logger.LogError(
                "Raw-content retention could not measure the oldest due content left for tenant {TenantId}: {ErrorCode}; the run cannot show the limit holds.",
                remaining[0],
                oldest.Error.Code);
            return Finish(purged, tenants.Count, discoveryFailed: true, remaining: true, oldestRemaining: null, started);
        }

        return Finish(
            purged,
            tenants.Count,
            discoveryFailed: false,
            remaining: true,
            oldestRemaining: oldest.Value is { } age ? (remaining[0], age) : null,
            started);
    }

    private async Task<IReadOnlyList<TenantId>> DiscoverAsync(int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<RetentionTenantDiscovery>()
            .FindAsync(limit, cancellationToken);
    }

    private Task<TenantPurged> PurgeTenantAsync(TenantId tenant, CancellationToken cancellationToken) =>
        InTenantScopeAsync(tenant, database => PurgeBatchesAsync(database, tenant, cancellationToken));

    private async Task<TenantPurged> PurgeBatchesAsync(
        ITenantScopedDatabase database,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        var purged = new TenantPurged();
        for (var batch = 0; batch < options.MaxBatchesPerTenant; batch++)
        {
            var cleared = await database.ExecuteAsync(
                (dbContext, token) => PurgeBatchAsync(dbContext, token),
                cancellationToken);

            if (cleared.IsFailure)
            {
                logger.LogError(
                    "Raw-content retention could not purge a batch for tenant {TenantId}: {ErrorCode}; the batch was rolled back and stays for a later run.",
                    tenant,
                    cleared.Error.Code);
                purged.Failed = true;
                break;
            }

            purged.Add(cleared.Value);
            if (cleared.Value.Count < options.BatchSize)
            {
                break;
            }
        }

        if (purged.Unprocessed > 0)
        {
            logger.LogWarning(
                "Raw-content retention purged {UnprocessedPurged} receipts of tenant {TenantId} that processing had not settled; those events can no longer be processed.",
                purged.Unprocessed,
                tenant);
        }

        logger.LogInformation(
            "Raw-content retention purged {ReceiptsPurged} receipts of tenant {TenantId} ({ProcessedPurged} processed, {QuarantinedPurged} quarantined, {UnprocessedPurged} unprocessed).",
            purged.Processed + purged.Quarantined + purged.Unprocessed,
            tenant,
            purged.Processed,
            purged.Quarantined,
            purged.Unprocessed);
        return purged;
    }

    private async Task<IReadOnlyList<PurgedReceipt>> PurgeBatchAsync(
        WorkContinuityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dbContext, PurgeBatchSql);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = options.BatchSize });

        var cleared = new List<PurgedReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cleared.Add(new PurgedReceipt(
                Enum.TryParse<RawReceiptState>(reader.GetString(0), ignoreCase: false, out var state) ? state : RawReceiptState.None,
                reader.GetFieldValue<TimeSpan>(1)));
        }

        return cleared;
    }

    private Task<Result<TimeSpan?>> OldestContentAgeAsync(TenantId tenant, CancellationToken cancellationToken) =>
        InTenantScopeAsync(tenant, database => database.ExecuteAsync(
            async (dbContext, token) =>
            {
                await using var command = CreateCommand(dbContext, OldestContentAgeSql);
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) && !reader.IsDBNull(0)
                    ? reader.GetFieldValue<TimeSpan>(0)
                    : (TimeSpan?)null;
            },
            cancellationToken));

    /// <summary>The purge's only tenant assignment: one tenant, set from the retention function's ids alone.</summary>
    private async Task<T> InTenantScopeAsync<T>(TenantId tenant, Func<ITenantScopedDatabase, Task<T>> work)
    {
        // A fresh dependency scope, and this method's own asynchronous flow, per call: its own context, connection,
        // transactions and tenant, none of which the caller or the next tenant ever sees.
        await using var scope = scopeFactory.CreateAsyncScope();
        tenantContextAccessor.SetResolved(
            TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
        return await work(scope.ServiceProvider.GetRequiredService<ITenantScopedDatabase>());
    }

    private static DbCommand CreateCommand(WorkContinuityDbContext dbContext, string sql)
    {
        // The statement runs on the scoped database's own connection and inside its transaction, after the tenant
        // was set for that transaction.
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Retention statements run only inside a tenant-scoped transaction.");
        return command;
    }

    private RawContentPurgeRun Finish(
        Purged purged,
        int visited,
        bool discoveryFailed,
        bool remaining,
        (TenantId Tenant, TimeSpan Age)? oldestRemaining,
        long started)
    {
        var run = new RawContentPurgeRun(
            visited,
            purged.TenantFailures,
            discoveryFailed,
            purged.Processed,
            purged.Quarantined,
            purged.Unprocessed,
            remaining,
            purged.OldestAge,
            oldestRemaining?.Age,
            Stopwatch.GetElapsedTime(started));

        LogLateness(run, oldestRemaining?.Tenant);
        logger.Log(
            run.Outcome == RawContentPurgeOutcome.Completed ? LogLevel.Information : LogLevel.Warning,
            "Raw-content retention run {Outcome}: {TenantsVisited} tenants visited, {TenantFailures} failed, {ReceiptsPurged} receipts purged ({ProcessedPurged} processed, {QuarantinedPurged} quarantined, {UnprocessedPurged} unprocessed), due content remaining {DueContentRemaining}, remaining content {RemainingStatus}, in {DurationMilliseconds} ms.",
            run.Outcome,
            run.TenantsVisited,
            run.TenantFailures,
            run.ReceiptsPurged,
            run.ProcessedPurged,
            run.QuarantinedPurged,
            run.UnprocessedPurged,
            run.DueContentRemaining,
            run.RemainingStatus,
            (long)run.Duration.TotalMilliseconds);
        return run;
    }

    private void LogLateness(RawContentPurgeRun run, TenantId? oldestRemainingTenant)
    {
        if (run.PurgedStatus == RawContentRetentionStatus.LimitExceeded)
        {
            logger.LogCritical(
                "Raw-content retention purged content {OldestPurgedAgeHours:F1} hours old, past the {LimitHours:F0}-hour retention limit: raw content was kept longer than retention allows.",
                run.OldestPurgedAge!.Value.TotalHours,
                RawContentRetention.Window.TotalHours);
        }
        else if (run.PurgedStatus == RawContentRetentionStatus.Overdue)
        {
            logger.LogWarning(
                "Raw-content retention purged content {OldestPurgedAgeHours:F1} hours old, past the {OverdueAgeHours:F0}-hour overdue age: purges ran late.",
                run.OldestPurgedAge!.Value.TotalHours,
                RawContentRetention.OverdueAge.TotalHours);
        }

        if (run.RemainingStatus == RawContentRetentionStatus.LimitExceeded)
        {
            logger.LogCritical(
                "Raw-content retention left content of tenant {TenantId} {OldestRemainingAgeHours:F1} hours old, past the {LimitHours:F0}-hour retention limit: raw content is kept longer than retention allows.",
                oldestRemainingTenant,
                run.OldestRemainingAge!.Value.TotalHours,
                RawContentRetention.Window.TotalHours);
        }
        else if (run.RemainingStatus == RawContentRetentionStatus.Overdue)
        {
            logger.LogWarning(
                "Raw-content retention left due content of tenant {TenantId} {OldestRemainingAgeHours:F1} hours old, past the {OverdueAgeHours:F0}-hour overdue age: it still needs purging.",
                oldestRemainingTenant,
                run.OldestRemainingAge!.Value.TotalHours,
                RawContentRetention.OverdueAge.TotalHours);
        }
    }

    /// <summary>A cleared receipt: its processing state and its age by the database's clock when it was cleared.</summary>
    private sealed record PurgedReceipt(RawReceiptState State, TimeSpan Age);

    /// <summary>What one tenant's batches cleared.</summary>
    private sealed class TenantPurged
    {
        public int Processed { get; private set; }

        public int Quarantined { get; private set; }

        public int Unprocessed { get; private set; }

        public TimeSpan? OldestAge { get; private set; }

        public bool Failed { get; set; }

        public void Add(IReadOnlyList<PurgedReceipt> cleared)
        {
            foreach (var receipt in cleared)
            {
                switch (receipt.State)
                {
                    case RawReceiptState.Processed:
                        Processed++;
                        break;
                    case RawReceiptState.Quarantined:
                        Quarantined++;
                        break;
                    default:
                        // Received, or a state this code does not know: either way nothing settled it.
                        Unprocessed++;
                        break;
                }

                if (OldestAge is not { } oldest || receipt.Age > oldest)
                {
                    OldestAge = receipt.Age;
                }
            }
        }
    }

    /// <summary>What the whole run cleared.</summary>
    private sealed class Purged
    {
        public int Processed { get; private set; }

        public int Quarantined { get; private set; }

        public int Unprocessed { get; private set; }

        public int TenantFailures { get; private set; }

        public TimeSpan? OldestAge { get; private set; }

        public void Add(TenantPurged tenant)
        {
            Processed += tenant.Processed;
            Quarantined += tenant.Quarantined;
            Unprocessed += tenant.Unprocessed;
            TenantFailures += tenant.Failed ? 1 : 0;
            if (tenant.OldestAge is { } oldest && (OldestAge is not { } current || oldest > current))
            {
                OldestAge = oldest;
            }
        }
    }
}
