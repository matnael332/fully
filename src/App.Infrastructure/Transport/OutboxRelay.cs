using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// The outbox relay (architecture §9: outbox processing publishes to Service Bus).
/// </summary>
/// <remarks>
/// <para>
/// A pass first asks the one approved tenant-less function which tenants have pending messages. Each tenant
/// is then relayed in its own dependency scope, as a trusted-service tenant context, and in its own
/// transactions under forced row-level security: a batch of that tenant's pending rows is locked with
/// <c>FOR UPDATE SKIP LOCKED</c>, each row is published, and each is marked published only after the broker
/// accepted it. The marks commit while the locks are held. Only committed rows are ever visible to the
/// claim, and concurrent relays skip each other's locked rows instead of claiming them twice.
/// </para>
/// <para>
/// Crash safety: a failure or crash before the commit rolls the marks back and releases the locks, so every
/// unmarked row stays publishable; a message sent just before such a crash is republished with the same
/// message id, which the broker's duplicate detection absorbs within its window and which is safe by
/// contract outside it. A failed send stops that tenant's batch, preserving order, and leaves the rest
/// pending. A row that cannot form a valid envelope is never published or marked; it is logged with its id
/// and reason on every pass and skipped, so it cannot block the rows behind it.
/// </para>
/// <para>
/// Hygiene: logs carry ids, codes and counts only, never a payload or an exception message.
/// </para>
/// </remarks>
internal sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantContextAccessor,
    IEventTransportPublisher publisher,
    IClock clock,
    EventTransportOptions options,
    ILogger<OutboxRelay> logger) : IOutboxRelay
{
    /// <inheritdoc />
    public async Task<OutboxRelayPass> RelayPendingAsync(CancellationToken cancellationToken = default)
    {
        if (tenantContextAccessor.Current.IsResolved)
        {
            // Each tenant is set per relay scope. Running inside a scope that already carries a tenant would
            // let that tenant stand in for the ones the relay works for.
            throw new InvalidOperationException(
                "The outbox relay must run in a scope without a resolved tenant; it sets each tenant itself.");
        }

        IReadOnlyList<TenantId> tenants;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            tenants = await scope.ServiceProvider
                .GetRequiredService<OutboxTenantDiscovery>()
                .FindAsync(options.TenantsPerPass, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "The outbox relay could not find pending work: {ExceptionType}; nothing was published this pass.",
                exception.GetType().Name);
            return new OutboxRelayPass(0, 0, 0, 0, 0, DiscoveryFailed: true, MoreWork: false);
        }

        int published = 0, unrelayable = 0, publishFailures = 0, commitFailures = 0;
        var limitReached = tenants.Count >= options.TenantsPerPass;
        foreach (var tenant in tenants)
        {
            var relayed = await RelayTenantAsync(tenant, cancellationToken);
            published += relayed.Published;
            unrelayable += relayed.Unrelayable;
            publishFailures += relayed.PublishFailed ? 1 : 0;
            commitFailures += relayed.CommitFailed ? 1 : 0;
            limitReached |= relayed.MoreWork;
        }

        // An immediate next pass is worth it only when this one made progress. A pass that published nothing,
        // because only unrelayable rows were pending, waits the idle interval instead of spinning.
        return new OutboxRelayPass(
            tenants.Count,
            published,
            unrelayable,
            publishFailures,
            commitFailures,
            DiscoveryFailed: false,
            MoreWork: limitReached && published > 0);
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            OutboxRelayPass? pass = null;
            try
            {
                pass = await RelayPendingAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("An outbox relay pass failed: {ExceptionType}.", exception.GetType().Name);
            }

            TimeSpan pause;
            if (pass is null || pass.HasFailures)
            {
                consecutiveFailures++;
                pause = FailureBackoff(consecutiveFailures);
            }
            else
            {
                consecutiveFailures = 0;
                pause = pass.MoreWork ? TimeSpan.Zero : options.IdleInterval;
            }

            if (pause > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(pause, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<TenantRelayed> RelayTenantAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        // A fresh dependency scope, and this method's own asynchronous flow, per tenant: its own context,
        // connection, transactions and tenant, none of which the caller or the next tenant ever sees.
        await using var scope = scopeFactory.CreateAsyncScope();
        tenantContextAccessor.SetResolved(
            TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
        var database = scope.ServiceProvider.GetRequiredService<ITenantScopedDatabase>();

        var skipped = new HashSet<Guid>();
        var published = 0;
        for (var batch = 0; batch < options.MaxBatchesPerTenant; batch++)
        {
            var committed = await database.ExecuteAsync(
                (dbContext, token) => RelayBatchAsync(dbContext, tenant, skipped, token),
                cancellationToken);

            if (committed.IsFailure)
            {
                logger.LogError(
                    "The outbox relay could not commit its marks for tenant {TenantId}: {ErrorCode}; messages sent in that batch stay pending and will be republished with the same ids.",
                    tenant,
                    committed.Error.Code);
                return new TenantRelayed(published, skipped.Count, PublishFailed: false, CommitFailed: true, MoreWork: false);
            }

            published += committed.Value.Published;
            if (committed.Value.PublishFailed)
            {
                return new TenantRelayed(published, skipped.Count, PublishFailed: true, CommitFailed: false, MoreWork: false);
            }

            if (committed.Value.Claimed < options.BatchSize)
            {
                return new TenantRelayed(published, skipped.Count, PublishFailed: false, CommitFailed: false, MoreWork: false);
            }
        }

        return new TenantRelayed(published, skipped.Count, PublishFailed: false, CommitFailed: false, MoreWork: true);
    }

    private async Task<BatchRelayed> RelayBatchAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        HashSet<Guid> skipped,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        // Forced row-level security limits this to the scope's own tenant. SKIP LOCKED leaves rows another
        // relay holds to that relay, and the row lock, held until this transaction ends, is the claim.
        var skippedIds = skipped.ToArray();
        var batchSize = options.BatchSize;
        var claimed = await dbContext.OutboxMessages
            .FromSql($"""
                SELECT outbox_message_id, tenant_id, message_type, payload, occurred_at_utc, correlation_id, published_at_utc
                FROM public.outbox_messages
                WHERE published_at_utc IS NULL
                  AND NOT (outbox_message_id = ANY ({skippedIds}))
                ORDER BY occurred_at_utc, outbox_message_id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var record in claimed)
        {
            var envelope = record.TenantId == tenant
                ? TransportEnvelope.FromOutbox(
                    record.OutboxMessageId, record.TenantId, record.MessageType, record.Payload, record.CorrelationId)
                : Result<TransportEnvelope>.Failure(TransportErrors.TenantInvalid);

            if (envelope.IsFailure)
            {
                skipped.Add(record.OutboxMessageId);
                logger.LogError(
                    "Outbox message {OutboxMessageId} for tenant {TenantId} cannot be relayed: {ErrorCode}; it stays pending and unpublished.",
                    record.OutboxMessageId,
                    tenant,
                    envelope.Error.Code);
                continue;
            }

            var sent = await publisher.PublishAsync(envelope.Value, cancellationToken);
            if (sent.IsFailure)
            {
                logger.LogWarning(
                    "The broker did not accept outbox message {OutboxMessageId} for tenant {TenantId}: {ErrorCode}; it and the rest of its batch stay pending (correlation {CorrelationId}).",
                    record.OutboxMessageId,
                    tenant,
                    sent.Error.Code,
                    record.CorrelationId);
                return new BatchRelayed(claimed.Count, published, PublishFailed: true);
            }

            record.MarkPublished(clock.UtcNow);
            published++;
            logger.LogInformation(
                "Published outbox message {OutboxMessageId} for tenant {TenantId} as {MessageId} (correlation {CorrelationId}).",
                record.OutboxMessageId,
                tenant,
                envelope.Value.MessageId,
                record.CorrelationId);
        }

        return new BatchRelayed(claimed.Count, published, PublishFailed: false);
    }

    private TimeSpan FailureBackoff(int consecutiveFailures)
    {
        var ceiling = Math.Min(
            options.IdleInterval.Ticks * Math.Pow(2, Math.Min(consecutiveFailures - 1, 30)),
            options.MaxFailureBackoff.Ticks);
        return TimeSpan.FromTicks((long)((ceiling / 2) + (ceiling / 2 * Random.Shared.NextDouble())));
    }

    private sealed record BatchRelayed(int Claimed, int Published, bool PublishFailed);

    private sealed record TenantRelayed(int Published, int Unrelayable, bool PublishFailed, bool CommitFailed, bool MoreWork);
}
