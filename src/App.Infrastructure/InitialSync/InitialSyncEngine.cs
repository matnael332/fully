using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Infrastructure.Reconciliation;

namespace WorkContinuity.Infrastructure.InitialSync;

/// <summary>The bounds of initial sync: the worker's lease, the retry backoff, the bound of unusable answers and the scan.</summary>
public sealed class InitialSyncOptions
{
    /// <summary>
    /// Gets or sets how long a claim lasts before another worker may take the generation over. It is renewed before every
    /// provider call and between replay batches.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how many consecutive attempts of a scope may end with an unusable answer (the resource missing, or a
    /// response the reader does not understand) before the scope is held. Transient failures and missing access are never held.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Gets or sets the first retry delay; each later attempt doubles it, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the longest backoff; a longer wait the provider asks for is still respected.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Gets or sets the longest wait a provider's answer may impose; a longer request is cut to it.</summary>
    public TimeSpan MaxProviderWait { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Gets or sets the most pages one scan of a scope reads: a listing that never ends is an unusable answer, not a loop.</summary>
    public int MaxPagesPerScan { get; set; } = 10_000;

    /// <summary>Gets or sets how many held receipts one replay batch takes.</summary>
    public int ReplayBatchSize { get; set; } = 100;

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClaimLease, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BaseRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, BaseRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxProviderWait, MaxRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPagesPerScan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReplayBatchSize, 1);
    }
}

/// <summary>
/// Initial sync (Gate 3F): brings one connection from NotStarted to Live by the frozen initial-connection flow, in durable
/// generations. Provider-neutral: the source is reached only through its <see cref="IInitialSyncReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffer start (D3F-10).</b> One transaction takes the connection row's exclusive lock, so every receipt ingress was
/// storing for the connection has committed and none is stored until it commits; checks the tenant, the connection and its
/// authorization locally; snapshots the authority (the delegated subject and when the connection was last authorized);
/// records the ingress fence; and fixes the generation's scopes: the enabled configured ones. From its commit, every
/// receipt above the replay floor is held by processing (<see cref="InitialSyncGate"/>), and only then is the source read.
/// A restarted generation keeps the replay floor, so nothing a non-Live connection held is ever skipped (DoD 22).
/// </para>
/// <para>
/// <b>Baseline (D3F-6 to D3F-8).</b> A run claims the generation with a lease; every transition compares the generation and
/// the claim, so a stale worker records nothing (D3F-13). Before every provider call the claim is renewed and access is
/// checked locally against the generation's authority. Each object of a page is applied in its own transaction, which
/// checks the claim and the authority again, then confirms each field through the one 3E path
/// (<see cref="SourceReconciler.ConfirmAsync"/>) and records it as a known 3E target. A field known in another scope is held
/// as a scope conflict and never overwritten; a reading that does not bind is held with its outcome; an object the source
/// places in another scope is never made known in this one.
/// </para>
/// <para>
/// <b>Failures (D3F-14, D3F-15).</b> As in 3E: transient failures wait on the capped backoff and are never held; missing
/// access waits with nothing sent; an unusable answer is retried a bounded number of times, then held; what retrying cannot
/// change is held at once. A held scope fails the generation, which never reaches Live. A reauthorization makes the
/// generation stale: it must restart, and the next run starts a new one under the current authority (D3F-5). A cursor the
/// source refuses restarts that scope's scan from its first page.
/// </para>
/// <para>
/// <b>Replay and cutover (D3F-11, D3F-12).</b> Once every scope completed, held receipts are replayed in ingress order, each
/// through the processing core in its own transaction, as the hints they are. The cutover then takes the connection row's
/// and the generation's exclusive locks, records the cutover fence, replays every receipt above the replay floor still
/// unprocessed up to it, and enters Live in the same transaction: no receipt falls between the last replay and Live, and
/// every later receipt is processed as it arrives. The ingress sequence decides membership only; business order is decided
/// by the processing core from the source's own evidence.
/// </para>
/// <para>
/// Logs carry ids, counts, fences and outcome codes only: never a value, a label, a continuation or a credential.
/// </para>
/// </remarks>
internal sealed class InitialSyncEngine : IInitialSync
{
    // Replay passes before the cutover, each up to the connection's latest fence, so the cutover itself replays little.
    private const int CatchUpPasses = 2;

    private static readonly IReadOnlyDictionary<SyncScopeOutcome, int> NoOutcomes = new Dictionary<SyncScopeOutcome, int>();

    private readonly ITenantScopedDatabase _database;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IReadOnlyDictionary<string, IInitialSyncReader> _readers;
    private readonly EventProcessor _processor;
    private readonly IClock _clock;
    private readonly InitialSyncOptions _options;
    private readonly EventProcessingOptions _processing;
    private readonly ILogger<InitialSyncEngine> _logger;

    /// <exception cref="InvalidOperationException">Two readers serve one source system.</exception>
    public InitialSyncEngine(
        ITenantScopedDatabase database,
        ITenantContextAccessor tenantContextAccessor,
        IEnumerable<IInitialSyncReader> readers,
        EventProcessor processor,
        IClock clock,
        InitialSyncOptions options,
        EventProcessingOptions processing,
        ILogger<InitialSyncEngine> logger)
    {
        var bySource = new Dictionary<string, IInitialSyncReader>(StringComparer.Ordinal);
        foreach (var reader in readers)
        {
            if (!bySource.TryAdd(reader.SourceSystem, reader))
            {
                throw new InvalidOperationException("More than one initial-sync reader is registered for one source system.");
            }
        }

        _database = database;
        _tenantContextAccessor = tenantContextAccessor;
        _readers = bySource;
        _processor = processor;
        _clock = clock;
        _options = options;
        _processing = processing;
        _logger = logger;
    }

    private enum Step
    {
        Continue,
        Waiting,
        Failed,
        RequiresRestart,
        Lost,
        Unavailable,
    }

    private enum ClaimKind
    {
        Held,
        Idle,
        Busy,
        Restart,
    }

    private enum ScanKind
    {
        Completed,
        Failed,
        Lost,
        Unavailable,
    }

    private enum Replay
    {
        Replayed,
        AlreadyDecided,
        Skipped,
        Lost,
    }

    /// <inheritdoc />
    public async Task<Result<InitialSyncSummary>> StartAsync(InitialSyncRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsTrusted(run.Tenant))
        {
            return Result<InitialSyncSummary>.Failure(InitialSyncErrors.TenantMismatch);
        }

        if (!_readers.ContainsKey(run.SourceSystem))
        {
            return Result<InitialSyncSummary>.Failure(InitialSyncErrors.ReaderMissing);
        }

        var started = await _database.ExecuteAsync((dbContext, token) => BufferStartAsync(dbContext, run, restartOnly: false, token), cancellationToken);
        if (started.IsFailure)
        {
            LogUnrecorded(run, started.Error);
            return Result<InitialSyncSummary>.Failure(InitialSyncErrors.Unavailable);
        }

        if (started.Value.Refusal is { } refusal)
        {
            return Result<InitialSyncSummary>.Failure(refusal);
        }

        LogStarted(run, started.Value);
        return Result<InitialSyncSummary>.Success(new InitialSyncSummary(started.Value.Generation, InitialSyncState.Buffering, NoOutcomes, 0, 0, 0));
    }

    /// <inheritdoc />
    public async Task<Result<InitialSyncSummary>> RunAsync(InitialSyncRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsTrusted(run.Tenant))
        {
            return Result<InitialSyncSummary>.Failure(InitialSyncErrors.TenantMismatch);
        }

        if (!_readers.TryGetValue(run.SourceSystem, out var reader))
        {
            return Result<InitialSyncSummary>.Failure(InitialSyncErrors.ReaderMissing);
        }

        var progress = new Progress();
        var claimId = Guid.CreateVersion7();
        var claim = await ClaimAsync(run, claimId, cancellationToken);
        if (claim is { Kind: ClaimKind.Restart })
        {
            // Authorized again during the generation: a new one starts under the current authority before anything is read.
            var restarted = await _database.ExecuteAsync((dbContext, token) => BufferStartAsync(dbContext, run, restartOnly: true, token), cancellationToken);
            if (restarted.IsFailure)
            {
                LogUnrecorded(run, restarted.Error);
                return Result<InitialSyncSummary>.Failure(InitialSyncErrors.Unavailable);
            }

            if (restarted.Value.Refusal is { } refusal)
            {
                _logger.LogInformation(
                    "Initial sync of connection {ConnectionId} for tenant {TenantId} must start a new generation after {Generation} and cannot yet: {ErrorCode}; nothing was read.",
                    run.ConnectionId,
                    run.Tenant,
                    claim.Generation,
                    refusal.Code);
                if (refusal == InitialSyncErrors.AccessRefused || refusal == InitialSyncErrors.NoScopes)
                {
                    return Result<InitialSyncSummary>.Success(Summary(claim.Generation, InitialSyncState.RequiresRestart, progress));
                }
            }
            else
            {
                LogStarted(run, restarted.Value);
            }

            claim = await ClaimAsync(run, claimId, cancellationToken);
        }

        switch (claim)
        {
            case null:
                return Result<InitialSyncSummary>.Failure(InitialSyncErrors.Unavailable);
            case { Kind: ClaimKind.Busy }:
                return Result<InitialSyncSummary>.Failure(InitialSyncErrors.InProgress);
            case { Kind: ClaimKind.Idle or ClaimKind.Restart }:
                return Result<InitialSyncSummary>.Success(Summary(claim.Generation, claim.State, progress));
        }

        var ended = await AdvanceAsync(reader, run, claimId, claim.Holding!, progress, cancellationToken);
        _logger.LogInformation(
            "Initial sync of connection {ConnectionId} for tenant {TenantId}, generation {Generation}: {State}; scopes {Outcomes}; {FieldsApplied} fields applied, {FieldsHeld} held; {ReceiptsReplayed} receipts replayed{Error} (correlation {CorrelationId}).",
            run.ConnectionId,
            run.Tenant,
            ended.Generation,
            ended.State,
            string.Join(", ", progress.Outcomes.OrderBy(pair => pair.Key).Select(pair => SyncScopeOutcomes.CodeOf(pair.Key) + "=" + pair.Value)),
            progress.FieldsApplied,
            progress.FieldsHeld,
            progress.ReceiptsReplayed,
            ended.Error is { } error ? "; " + error.Code : string.Empty,
            progress.Correlation);

        return ended.Error is { } failure
            ? Result<InitialSyncSummary>.Failure(failure)
            : Result<InitialSyncSummary>.Success(Summary(ended.Generation, ended.State, progress));
    }

    private bool IsTrusted(TenantId tenant) =>
        _tenantContextAccessor.Current.TryGetTenantId(out var trusted) && trusted == tenant;

    private static string Reference(InitialSyncRun run) => ConnectionRecord.ReferenceFor(run.ConnectionId);

    private static InitialSyncSummary Summary(long generation, InitialSyncState state, Progress progress) =>
        new(generation, state, progress.Outcomes, progress.FieldsApplied, progress.FieldsHeld, progress.ReceiptsReplayed);

    // The authority a read must still find: the generation's subject, not authorized again since it started.
    private static bool SameAuthority(SourceReconciler.Standing standing, Holding holding) =>
        string.Equals(standing.Subject, holding.Subject, StringComparison.Ordinal) && standing.AuthorizedAt == holding.AuthorizedAt;

    private static SyncScopeOutcome AccessOutcome(ReconciliationOutcome refusal) =>
        refusal == ReconciliationOutcome.TenantInactive ? SyncScopeOutcome.TenantInactive : SyncScopeOutcome.ConnectionUnavailable;

    private static Task<ConnectionSyncRecord?> LockSyncAsync(WorkContinuityDbContext dbContext, InitialSyncRun run, CancellationToken cancellationToken) =>
        InitialSyncGate.LockSyncAsync(dbContext, run.Tenant, run.ConnectionId, cancellationToken);

    // The generation as this run's claim must still find it, share-locked so it cannot change until the caller commits.
    private static async Task<bool> StillHeldAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        Guid claimId,
        long generation,
        InitialSyncState state,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<HeldRow>(
                $"""
                SELECT generation AS "Generation", state AS "State", claim_id AS "ClaimId"
                FROM public.connection_syncs
                WHERE tenant_id = {run.Tenant.Value} AND connection_id = {run.ConnectionId}
                FOR SHARE
                """)
            .ToListAsync(cancellationToken);
        return rows.Count == 1 && rows[0].Generation == generation && rows[0].ClaimId == claimId && rows[0].State == state.ToString();
    }

    // The buffer start (D3F-10). Nothing is read from the source before it commits.
    private async Task<Start> BufferStartAsync(WorkContinuityDbContext dbContext, InitialSyncRun run, bool restartOnly, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        // The connection row first, exclusively: ingress storing a receipt for it has committed, and none starts until this
        // commits. The row exists before the first generation, so the fence is exact from the start.
        if (!await InitialSyncGate.LockConnectionAsync(dbContext, run.Tenant, run.ConnectionId, cancellationToken))
        {
            return Start.Refused(InitialSyncErrors.ConnectionNotFound);
        }

        var sync = await LockSyncAsync(dbContext, run, cancellationToken);
        if (sync is not null && (sync.State == InitialSyncState.Live || !string.Equals(sync.SourceSystem, run.SourceSystem, StringComparison.Ordinal)))
        {
            return Start.Refused(sync.State == InitialSyncState.Live ? InitialSyncErrors.AlreadyLive : InitialSyncErrors.ConnectionNotFound);
        }

        if (sync is null ? restartOnly : sync.IsRunning || (restartOnly && sync.State != InitialSyncState.RequiresRestart))
        {
            return Start.Refused(InitialSyncErrors.InProgress);
        }

        // Access first, locally: a tenant, connection or authorization that does not stand starts nothing (D3F-5).
        var standing = await SourceReconciler.StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, cancellationToken);
        if (standing.Refusal != ReconciliationOutcome.None || standing.Subject is null || standing.AuthorizedAt is not { } authorizedAt)
        {
            return Start.Refused(InitialSyncErrors.AccessRefused);
        }

        // Only the explicitly configured, enabled scopes of this connection and source (D3F-1); nothing is discovered. A
        // connection that never took part has nothing to start without one; one that did starts a generation over its scope
        // set as it is now, an empty one included, so what it held is replayed.
        var scopes = (await dbContext.ConnectorSourceScopes
                .AsNoTracking()
                .Where(scope => scope.TenantId == run.Tenant
                    && scope.ConnectionId == run.ConnectionId
                    && scope.SourceSystem == run.SourceSystem
                    && scope.Enabled)
                .Select(scope => scope.ResourceId)
                .ToListAsync(cancellationToken))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (sync is null && scopes.Count == 0)
        {
            return Start.Refused(InitialSyncErrors.NoScopes);
        }

        var fence = await InitialSyncGate.IngressFenceAsync(dbContext, run.Tenant, Reference(run), cancellationToken);
        var now = _clock.UtcNow;
        if (sync is null)
        {
            // Scopes written outside the configuration service: no hold boundary was recorded, and the gate has held every
            // receipt since, so every unresolved receipt of the connection is replayed.
            sync = ConnectionSyncRecord.Begin(run.Tenant, run.ConnectionId, run.SourceSystem, standing.Subject, authorizedAt, replayFloor: 0, fence, now);
            dbContext.ConnectionSyncs.Add(sync);
        }
        else
        {
            sync.Restart(standing.Subject, authorizedAt, fence, now);
        }

        foreach (var resource in scopes)
        {
            dbContext.ConnectionSyncScopes.Add(ConnectionSyncScopeRecord.Plan(run.Tenant, run.ConnectionId, sync.Generation, resource));
        }

        return new Start(null, sync.Generation, fence, sync.ReplayFloor, scopes.Count);
    }

    private async Task<Claim?> ClaimAsync(InitialSyncRun run, Guid claimId, CancellationToken cancellationToken)
    {
        var claimed = await _database.ExecuteAsync(
            async (dbContext, token) =>
            {
                dbContext.ChangeTracker.Clear();
                var sync = await LockSyncAsync(dbContext, run, token);
                if (sync is null || !string.Equals(sync.SourceSystem, run.SourceSystem, StringComparison.Ordinal))
                {
                    return new Claim(ClaimKind.Idle, InitialSyncState.NotStarted, 0, null);
                }

                if (sync.State == InitialSyncState.RequiresRestart)
                {
                    return new Claim(ClaimKind.Restart, sync.State, sync.Generation, null);
                }

                var now = _clock.UtcNow;
                if (!sync.IsRunning || sync.NextAttemptAtUtc > now)
                {
                    return new Claim(ClaimKind.Idle, sync.State, sync.Generation, null);
                }

                if (sync.ClaimId is not null && sync.ClaimedUntilUtc > now)
                {
                    return new Claim(ClaimKind.Busy, sync.State, sync.Generation, null);
                }

                sync.Claim(claimId, now + _options.ClaimLease, now);
                return new Claim(
                    ClaimKind.Held,
                    sync.State,
                    sync.Generation,
                    new Holding(sync.Generation, sync.State, sync.ProviderSubject!, sync.AuthorizedAtUtc!.Value, sync.ReplayFloor, sync.ReplayFence));
            },
            cancellationToken);
        if (claimed.IsFailure)
        {
            LogUnrecorded(run, claimed.Error);
            return null;
        }

        return claimed.Value;
    }

    private async Task<Ended> AdvanceAsync(IInitialSyncReader reader, InitialSyncRun run, Guid claimId, Holding holding, Progress progress, CancellationToken cancellationToken)
    {
        if (holding.State == InitialSyncState.Buffering)
        {
            var reading = await TransitionAsync(
                run,
                claimId,
                holding.Generation,
                (_, sync, now, _) =>
                {
                    sync.BeginReading(claimId, now);
                    return Task.FromResult(Step.Continue);
                },
                cancellationToken);
            if (reading != Step.Continue)
            {
                return Ended.From(reading, holding);
            }

            holding = holding with { State = InitialSyncState.Reading };
        }

        if (holding.State == InitialSyncState.Reading)
        {
            if (await ReadBaselineAsync(reader, run, claimId, holding, progress, cancellationToken) is { } stopped)
            {
                return stopped;
            }

            // Every scope completed: the replay starts, first up to the fence the connection has reached now.
            long replayFence = 0;
            var replaying = await TransitionAsync(
                run,
                claimId,
                holding.Generation,
                async (dbContext, sync, now, token) =>
                {
                    if (sync.State != InitialSyncState.Reading)
                    {
                        return Step.Lost;
                    }

                    replayFence = await InitialSyncGate.IngressFenceAsync(dbContext, run.Tenant, Reference(run), token);
                    sync.BeginReplay(claimId, replayFence, now);
                    return Step.Continue;
                },
                cancellationToken);
            if (replaying != Step.Continue)
            {
                return Ended.From(replaying, holding);
            }

            holding = holding with { State = InitialSyncState.Replaying, ReplayFence = replayFence };
        }

        return await ReplayAndCutOverAsync(run, claimId, holding, progress, cancellationToken);
    }

    // One transition of the generation this run claimed, in its own transaction with the row locked: a lost claim or a newer
    // generation records nothing.
    private async Task<Step> TransitionAsync(
        InitialSyncRun run,
        Guid claimId,
        long generation,
        Func<WorkContinuityDbContext, ConnectionSyncRecord, DateTimeOffset, CancellationToken, Task<Step>> apply,
        CancellationToken cancellationToken)
    {
        var result = await _database.ExecuteAsync(
            async (dbContext, token) =>
            {
                dbContext.ChangeTracker.Clear();
                var sync = await LockSyncAsync(dbContext, run, token);
                if (sync is null || sync.Generation != generation || !sync.IsClaimedBy(claimId))
                {
                    return Step.Lost;
                }

                return await apply(dbContext, sync, _clock.UtcNow, token);
            },
            cancellationToken);
        if (result.IsFailure)
        {
            LogUnrecorded(run, result.Error);
            return Step.Unavailable;
        }

        return result.Value;
    }

    private async Task<Ended?> ReadBaselineAsync(IInitialSyncReader reader, InitialSyncRun run, Guid claimId, Holding holding, Progress progress, CancellationToken cancellationToken)
    {
        var pending = await _database.ExecuteAsync(
            (dbContext, token) => dbContext.ConnectionSyncScopes
                .AsNoTracking()
                .Where(scope => scope.TenantId == run.Tenant
                    && scope.ConnectionId == run.ConnectionId
                    && scope.Generation == holding.Generation
                    && scope.State == SyncScopeState.Pending)
                .Select(scope => scope.ResourceId)
                .ToListAsync(token),
            cancellationToken);
        if (pending.IsFailure)
        {
            LogUnrecorded(run, pending.Error);
            return Ended.Unavailable(holding);
        }

        foreach (var resource in pending.Value.Order(StringComparer.Ordinal))
        {
            var scan = await ScanAsync(reader, run, claimId, holding, resource, progress, cancellationToken);
            if (await RecordAsync(run, claimId, holding, resource, scan, progress, cancellationToken) is { } stopped)
            {
                return stopped;
            }
        }

        return null;
    }

    // One scan of one scope: the validation, then every page, each object applied in its own transaction.
    private async Task<Scan> ScanAsync(IInitialSyncReader reader, InitialSyncRun run, Guid claimId, Holding holding, string resource, Progress progress, CancellationToken cancellationToken)
    {
        var request = new ScopeReadRequest(run.Tenant, run.ConnectionId, Reference(run), run.SourceSystem, resource, progress.Correlation);
        if (await BeforeCallAsync(run, claimId, holding, cancellationToken) is { } refused)
        {
            return refused;
        }

        var validation = await ValidateAsync(reader, request, run, cancellationToken);
        if (validation.Refusal != SyncScopeOutcome.None)
        {
            return Scan.Failed(validation.Refusal, validation.RetryAfter);
        }

        if (await ForeignReadAsync(run, holding, validation.Identity, cancellationToken) is { } foreign)
        {
            return foreign;
        }

        var restarted = false;
        var (pages, objects, applied) = (0, 0, 0);
        ScopeContinuation? continuation = null;
        for (var read = 0; ; read++)
        {
            if (read >= _options.MaxPagesPerScan)
            {
                return Scan.Failed(SyncScopeOutcome.UnsupportedResponse);
            }

            if (await BeforeCallAsync(run, claimId, holding, cancellationToken) is { } stopped)
            {
                return stopped;
            }

            var page = await ReadPageAsync(reader, new ScopePageRequest(request, validation.ObjectType!, validation.Fields, continuation), run, cancellationToken);
            if (page.Failure == SyncScopeOutcome.ContinuationRejected && !restarted)
            {
                // The source refused the continuation, as an expired cursor: the scope is read again from its first page,
                // once per run. What was applied applies again as current.
                restarted = true;
                continuation = null;
                (pages, objects, applied) = (0, 0, 0);
                continue;
            }

            if (page.Failure != SyncScopeOutcome.None)
            {
                return Scan.Failed(page.Failure, page.RetryAfter);
            }

            if (await ForeignReadAsync(run, holding, page.Identity, cancellationToken) is { } foreignPage)
            {
                return foreignPage;
            }

            if (!IsBound(page, request, validation))
            {
                return Scan.Failed(SyncScopeOutcome.BindingMismatch);
            }

            foreach (var synced in page.Objects)
            {
                var result = await _database.ExecuteAsync(
                    (dbContext, token) => ApplyAsync(dbContext, run, claimId, holding, resource, page.Identity!, synced, progress.Correlation, token),
                    cancellationToken);
                if (result.IsFailure)
                {
                    LogUnrecorded(run, result.Error);
                    return Scan.Unavailable;
                }

                if (result.Value.Stopped is { } stop)
                {
                    return stop;
                }

                applied += result.Value.Applied;
                progress.FieldsApplied += result.Value.Applied;
                progress.FieldsHeld += result.Value.Held;
            }

            pages++;
            objects += page.Objects.Count;
            if (page.Next is null)
            {
                return Scan.Completed(pages, objects, applied);
            }

            continuation = page.Next;
        }
    }

    // Every object is homed at the run's connection with the validated type, and carries exactly the validated fields.
    private static bool IsBound(ScopePage page, ScopeReadRequest request, ScopeValidation validation) =>
        page.Objects.All(synced =>
            string.Equals(synced.WorkObject.SourceSystem, request.SourceSystem, StringComparison.Ordinal)
            && string.Equals(synced.WorkObject.ConnectionReference, request.ConnectionReference, StringComparison.Ordinal)
            && string.Equals(synced.WorkObject.ObjectType, validation.ObjectType, StringComparison.Ordinal)
            && synced.Fields.Count == validation.Fields.Count
            && validation.Fields.All(synced.Fields.ContainsKey));

    // Before every provider call: the claim still holds and is renewed, and access stands locally under the generation's
    // authority. Otherwise nothing is sent.
    private async Task<Scan?> BeforeCallAsync(InitialSyncRun run, Guid claimId, Holding holding, CancellationToken cancellationToken)
    {
        Scan? refusal = null;
        var step = await TransitionAsync(
            run,
            claimId,
            holding.Generation,
            async (dbContext, sync, now, token) =>
            {
                if (sync.State != InitialSyncState.Reading)
                {
                    return Step.Lost;
                }

                var standing = await SourceReconciler.StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, token);
                if (standing.Refusal != ReconciliationOutcome.None)
                {
                    refusal = Scan.Failed(AccessOutcome(standing.Refusal));
                }
                else if (!SameAuthority(standing, holding))
                {
                    refusal = Scan.Failed(SyncScopeOutcome.AuthorityChanged);
                }
                else
                {
                    sync.Renew(claimId, now + _options.ClaimLease, now);
                }

                return Step.Continue;
            },
            cancellationToken);
        return step switch
        {
            Step.Continue => refusal,
            Step.Lost => Scan.Lost,
            _ => Scan.Unavailable,
        };
    }

    // A read is the connection's own only under its delegated identity, with the subject the generation started with. Under
    // any other, it is stale when the connection was authorized again meanwhile, or a stable mismatch, which is held.
    private async Task<Scan?> ForeignReadAsync(InitialSyncRun run, Holding holding, ConnectorIdentity? identity, CancellationToken cancellationToken)
    {
        if (identity is { Kind: ConnectorIdentityKind.DelegatedUser }
            && identity.Serves(run.Tenant, run.ConnectionId)
            && string.Equals(identity.Subject, holding.Subject, StringComparison.Ordinal))
        {
            return null;
        }

        var standing = await _database.ExecuteAsync(
            (dbContext, token) => SourceReconciler.StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, token),
            cancellationToken);
        if (standing.IsFailure)
        {
            LogUnrecorded(run, standing.Error);
            return Scan.Unavailable;
        }

        if (standing.Value.Refusal != ReconciliationOutcome.None)
        {
            return Scan.Failed(AccessOutcome(standing.Value.Refusal));
        }

        return Scan.Failed(SameAuthority(standing.Value, holding) ? SyncScopeOutcome.IdentityMismatch : SyncScopeOutcome.AuthorityChanged);
    }

    private async Task<ScopeValidation> ValidateAsync(IInitialSyncReader reader, ScopeReadRequest request, InitialSyncRun run, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ValidateScopeAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReaderFailure(run, exception);
            return ScopeValidation.Refused(SyncScopeOutcome.ProviderUnavailable, identity: null);
        }
    }

    private async Task<ScopePage> ReadPageAsync(IInitialSyncReader reader, ScopePageRequest request, InitialSyncRun run, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadPageAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReaderFailure(run, exception);
            return ScopePage.Failed(SyncScopeOutcome.ProviderUnavailable, identity: null);
        }
    }

    // One object of a page, in its own transaction: the claim and the authority are checked again, then each field is
    // confirmed through the one 3E path and recorded as a known target.
    private async Task<ObjectResult> ApplyAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        Guid claimId,
        Holding holding,
        string resource,
        ConnectorIdentity identity,
        SyncedObject synced,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        if (!await StillHeldAsync(dbContext, run, claimId, holding.Generation, InitialSyncState.Reading, cancellationToken))
        {
            return ObjectResult.Stop(Scan.Lost);
        }

        // A read made under an authority since replaced, or while access stopped standing, is never applied.
        var standing = await SourceReconciler.StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, cancellationToken);
        if (standing.Refusal != ReconciliationOutcome.None)
        {
            return ObjectResult.Stop(Scan.Failed(AccessOutcome(standing.Refusal)));
        }

        if (!SameAuthority(standing, holding))
        {
            return ObjectResult.Stop(Scan.Failed(SyncScopeOutcome.AuthorityChanged));
        }

        var now = _clock.UtcNow;
        var (applied, held) = (0, 0);
        foreach (var (field, reading) in synced.Fields.OrderBy(pair => pair.Key.Field, StringComparer.Ordinal))
        {
            // The configured scope the reader bound the object to; a field known in another scope is never remapped.
            var target = new ReconciliationTarget(synced.WorkObject, field.Field, field.DataType, resource);
            var known = await ReconciliationTargets.LockAsync(dbContext, run.Tenant, target, cancellationToken);
            if (known is not null && !known.Matches(target))
            {
                await ReconciliationTargets.ConflictAsync(dbContext, run.Tenant, target, now, cancellationToken);
                held++;
                continue;
            }

            // An object the source places in another scope is not made known in this one.
            if (known is null && reading.Refusal == ReconciliationOutcome.BindingMismatch)
            {
                held++;
                continue;
            }

            var (outcome, resolvingEventId) = await SourceReconciler.ConfirmAsync(
                dbContext, run.Tenant, run.ConnectionId, target, reading, identity, standing.Subject, correlation, now, _processing.ClockSkewTolerance, cancellationToken);
            await ReconciliationTargets.EstablishAsync(dbContext, run.Tenant, target, outcome, resolvingEventId, now, cancellationToken);
            if (ReconciliationOutcomes.DispositionOf(outcome) == ReconciliationDisposition.Resolve)
            {
                applied++;
            }
            else
            {
                held++;
            }
        }

        return ObjectResult.Done(applied, held);
    }

    // Records how a scope's scan ended, in a transition of the claimed generation: completed, waiting, held (which fails the
    // generation) or stale (which must restart).
    private async Task<Ended?> RecordAsync(InitialSyncRun run, Guid claimId, Holding holding, string resource, Scan scan, Progress progress, CancellationToken cancellationToken)
    {
        switch (scan.Kind)
        {
            case ScanKind.Lost:
                return Ended.Lost(holding);
            case ScanKind.Unavailable:
                return Ended.Unavailable(holding);
        }

        var outcome = scan.Kind == ScanKind.Completed ? SyncScopeOutcome.Completed : scan.Outcome;
        progress.Count(outcome);
        var code = SyncScopeOutcomes.CodeOf(outcome);
        var step = await TransitionAsync(
            run,
            claimId,
            holding.Generation,
            async (dbContext, sync, now, token) =>
            {
                if (sync.State != InitialSyncState.Reading)
                {
                    return Step.Lost;
                }

                if (outcome == SyncScopeOutcome.Completed)
                {
                    (await ScopeAsync(dbContext, run, holding.Generation, resource, token)).Complete(scan.Pages, scan.Objects, scan.Applied, now);
                    sync.Renew(claimId, now + _options.ClaimLease, now);
                    return Step.Continue;
                }

                if (outcome == SyncScopeOutcome.AuthorityChanged)
                {
                    // Authorized again while the source was read: what was read is stale, and the generation must restart.
                    sync.RequireRestart(claimId, code, now);
                    return Step.RequiresRestart;
                }

                if (SyncScopeOutcomes.DispositionOf(outcome) == ReconciliationDisposition.AwaitAccess)
                {
                    // Access does not stand: the generation waits with nothing read, for as long as it lasts.
                    var waits = sync.AttemptsWith(outcome);
                    sync.Wait(claimId, code, waits, now + RetryDelay(waits, providerWait: null), now);
                    return Step.Waiting;
                }

                var scope = await ScopeAsync(dbContext, run, holding.Generation, resource, token);
                var attempts = scope.RecordFailure(outcome, _options.MaxAttempts);
                if (scope.State == SyncScopeState.Held)
                {
                    // D3F-15: a configured scope that cannot complete fails the generation; it never reaches Live.
                    sync.Fail(claimId, code, now);
                    return Step.Failed;
                }

                sync.Wait(claimId, code, attempts, now + RetryDelay(attempts, scan.RetryAfter), now);
                return Step.Waiting;
            },
            cancellationToken);

        return step == Step.Continue ? null : Ended.From(step, holding);
    }

    private static Task<ConnectionSyncScopeRecord> ScopeAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        long generation,
        string resource,
        CancellationToken cancellationToken) =>
        dbContext.ConnectionSyncScopes.SingleAsync(
            scope => scope.TenantId == run.Tenant && scope.ConnectionId == run.ConnectionId && scope.Generation == generation && scope.ResourceId == resource,
            cancellationToken);

    private async Task<Ended> ReplayAndCutOverAsync(InitialSyncRun run, Guid claimId, Holding holding, Progress progress, CancellationToken cancellationToken)
    {
        // Held receipts are replayed in ingress order, each in its own transaction, up to the fence the replay began at,
        // then up to the connection's later fences while new receipts keep arriving, a bounded number of times.
        var position = holding.ReplayFloor;
        var through = holding.ReplayFence ?? holding.ReplayFloor;
        for (var pass = 0; ; pass++)
        {
            if (await ReplayRangeAsync(run, claimId, holding, position, through, progress, cancellationToken) is { } stopped)
            {
                return stopped;
            }

            position = through;
            if (pass >= CatchUpPasses)
            {
                break;
            }

            var latest = await _database.ExecuteAsync(
                (dbContext, token) => InitialSyncGate.IngressFenceAsync(dbContext, run.Tenant, Reference(run), token),
                cancellationToken);
            if (latest.IsFailure)
            {
                LogUnrecorded(run, latest.Error);
                return Ended.Unavailable(holding);
            }

            if (latest.Value <= through)
            {
                break;
            }

            through = latest.Value;
        }

        var cutover = await _database.ExecuteAsync(
            (dbContext, token) => CutOverAsync(dbContext, run, claimId, holding, progress.Correlation, token),
            cancellationToken);
        if (cutover.IsFailure)
        {
            LogUnrecorded(run, cutover.Error);
            return Ended.Unavailable(holding);
        }

        if (cutover.Value is not { } live)
        {
            return Ended.Lost(holding);
        }

        progress.ReceiptsReplayed += live.Replayed;
        _logger.LogInformation(
            "Initial sync of connection {ConnectionId} for tenant {TenantId} reached Live at generation {Generation}: replay floor {ReplayFloor}, cutover fence {CutoverFence}.",
            run.ConnectionId,
            run.Tenant,
            holding.Generation,
            holding.ReplayFloor,
            live.Fence);
        return new Ended(holding.Generation, InitialSyncState.Live, null);
    }

    private async Task<Ended?> ReplayRangeAsync(InitialSyncRun run, Guid claimId, Holding holding, long after, long through, Progress progress, CancellationToken cancellationToken)
    {
        var position = after;
        while (position < through)
        {
            var batch = await _database.ExecuteAsync(
                (dbContext, token) => HeldReceiptsAsync(dbContext, run, position, through, _options.ReplayBatchSize, token),
                cancellationToken);
            if (batch.IsFailure)
            {
                LogUnrecorded(run, batch.Error);
                return Ended.Unavailable(holding);
            }

            if (batch.Value.Count == 0)
            {
                return null;
            }

            foreach (var receipt in batch.Value)
            {
                var replayed = await _database.ExecuteAsync(
                    (dbContext, token) => ReplayOneAsync(dbContext, run, claimId, holding, receipt.ReceiptId, progress.Correlation, token),
                    cancellationToken);
                if (replayed.IsFailure)
                {
                    LogUnrecorded(run, replayed.Error);
                    return Ended.Unavailable(holding);
                }

                if (replayed.Value == Replay.Lost)
                {
                    return Ended.Lost(holding);
                }

                if (replayed.Value == Replay.Replayed)
                {
                    progress.ReceiptsReplayed++;
                }

                position = receipt.IngressSequence;
            }

            var renewed = await TransitionAsync(
                run,
                claimId,
                holding.Generation,
                (_, sync, now, _) =>
                {
                    if (sync.State != InitialSyncState.Replaying)
                    {
                        return Task.FromResult(Step.Lost);
                    }

                    sync.Renew(claimId, now + _options.ClaimLease, now);
                    return Task.FromResult(Step.Continue);
                },
                cancellationToken);
            if (renewed != Step.Continue)
            {
                return Ended.From(renewed, holding);
            }
        }

        return null;
    }

    // One held receipt, replayed through the processing core under the generation's share lock, taken before the receipt's
    // as processing takes them: a stale worker replays nothing.
    private async Task<Replay> ReplayOneAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        Guid claimId,
        Holding holding,
        Guid receiptId,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        if (!await StillHeldAsync(dbContext, run, claimId, holding.Generation, InitialSyncState.Replaying, cancellationToken))
        {
            return Replay.Lost;
        }

        var result = await _processor.ReplayHeldAsync(dbContext, receiptId, correlation, run.Tenant, cancellationToken);
        return result is null
            ? Replay.Skipped
            : result.Outcome == EventProcessingOutcome.Duplicate ? Replay.AlreadyDecided : Replay.Replayed;
    }

    // The cutover (D3F-11): under the connection row's and the generation's exclusive locks, every receipt being stored or
    // decided for the connection has committed and none is until this commits. Every receipt above the replay floor still
    // unprocessed up to the fence is replayed, the whole range again, since a lower position can commit after a higher one,
    // and the generation enters Live in the same transaction.
    private async Task<CutOver?> CutOverAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        Guid claimId,
        Holding holding,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        if (!await InitialSyncGate.LockConnectionAsync(dbContext, run.Tenant, run.ConnectionId, cancellationToken))
        {
            return null;
        }

        var sync = await LockSyncAsync(dbContext, run, cancellationToken);
        if (sync is null || sync.Generation != holding.Generation || !sync.IsClaimedBy(claimId) || sync.State != InitialSyncState.Replaying)
        {
            return null;
        }

        var fence = await InitialSyncGate.IngressFenceAsync(dbContext, run.Tenant, Reference(run), cancellationToken);
        var replayed = 0;
        var position = sync.ReplayFloor;
        while (true)
        {
            var batch = await HeldReceiptsAsync(dbContext, run, position, fence, _options.ReplayBatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var receipt in batch)
            {
                var result = await _processor.ReplayHeldAsync(dbContext, receipt.ReceiptId, correlation, run.Tenant, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                if (result is not null && result.Outcome != EventProcessingOutcome.Duplicate)
                {
                    replayed++;
                }

                position = receipt.IngressSequence;
            }
        }

        // The generation's row again, tracked anew after the replays, enters Live.
        dbContext.ChangeTracker.Clear();
        sync = await LockSyncAsync(dbContext, run, cancellationToken);
        var now = _clock.UtcNow;
        sync!.GoLive(claimId, fence, now);

        // In the same transition, every enabled scope the generation read in full becomes the connection's current baseline.
        var read = await dbContext.ConnectionSyncScopes
            .AsNoTracking()
            .Where(scope => scope.TenantId == run.Tenant && scope.ConnectionId == run.ConnectionId && scope.Generation == sync.Generation)
            .Select(scope => scope.ResourceId)
            .ToListAsync(cancellationToken);
        var established = await dbContext.ConnectorSourceScopes
            .Where(scope => scope.TenantId == run.Tenant
                && scope.ConnectionId == run.ConnectionId
                && scope.SourceSystem == run.SourceSystem
                && scope.Enabled
                && read.Contains(scope.ResourceId))
            .ToListAsync(cancellationToken);
        foreach (var scope in established)
        {
            scope.EstablishBaseline(sync.Generation, now);
        }

        return new CutOver(fence, replayed);
    }

    // The connection's unprocessed receipts in (after, through], in ingress order: the held ones and those whose content
    // expired before they could be replayed, which the replay skips by position.
    private static Task<List<HeldReceipt>> HeldReceiptsAsync(
        WorkContinuityDbContext dbContext,
        InitialSyncRun run,
        long after,
        long through,
        int limit,
        CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<HeldReceipt>(
                $"""
                SELECT receipt_id AS "ReceiptId", ingress_sequence AS "IngressSequence"
                FROM public.raw_receipts
                WHERE tenant_id = {run.Tenant.Value} AND connection_reference = {Reference(run)}
                  AND ingress_sequence > {after} AND ingress_sequence <= {through} AND processing_state = 'Received'
                ORDER BY ingress_sequence
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

    // Exponential from the base over the run of like failures, capped; a longer wait the provider asked for is respected up
    // to its own bound, as in 3E.
    private TimeSpan RetryDelay(int attempts, TimeSpan? providerWait)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var backoff = TimeSpan.FromTicks(Math.Min(_options.MaxRetryDelay.Ticks, _options.BaseRetryDelay.Ticks * (1L << exponent)));
        if (providerWait is not { } wait)
        {
            return backoff;
        }

        var bounded = wait < _options.MaxProviderWait ? wait : _options.MaxProviderWait;
        return bounded > backoff ? bounded : backoff;
    }

    private void LogStarted(InitialSyncRun run, Start start) =>
        _logger.LogInformation(
            "Initial sync of connection {ConnectionId} for tenant {TenantId} started generation {Generation} with {Scopes} scopes at ingress fence {Fence}, replay floor {ReplayFloor}.",
            run.ConnectionId,
            run.Tenant,
            start.Generation,
            start.Scopes,
            start.Fence,
            start.ReplayFloor);

    private void LogUnrecorded(InitialSyncRun run, Error error) =>
        _logger.LogWarning(
            "Initial sync of connection {ConnectionId} for tenant {TenantId} could not record its progress: {ErrorCode}.",
            run.ConnectionId,
            run.Tenant,
            error.Code);

    // Only the type is logged: a reader's exception text could quote the source.
    private void LogReaderFailure(InitialSyncRun run, Exception exception) =>
        _logger.LogWarning(
            "The {SourceSystem} initial-sync reader failed with {ExceptionType} for connection {ConnectionId}; the scope is retried.",
            run.SourceSystem,
            exception.GetType().Name,
            run.ConnectionId);

    private sealed record Start(Error? Refusal, long Generation, long Fence, long ReplayFloor, int Scopes)
    {
        public static Start Refused(Error refusal) => new(refusal, 0, 0, 0, 0);
    }

    // The generation a run holds: its number, state, authority snapshot and fences.
    private sealed record Holding(long Generation, InitialSyncState State, string Subject, DateTimeOffset AuthorizedAt, long ReplayFloor, long? ReplayFence);

    private sealed record Claim(ClaimKind Kind, InitialSyncState State, long Generation, Holding? Holding);

    private sealed record Scan(ScanKind Kind, SyncScopeOutcome Outcome = SyncScopeOutcome.None, TimeSpan? RetryAfter = null, int Pages = 0, int Objects = 0, int Applied = 0)
    {
        public static Scan Lost { get; } = new(ScanKind.Lost);

        public static Scan Unavailable { get; } = new(ScanKind.Unavailable);

        public static Scan Failed(SyncScopeOutcome outcome, TimeSpan? retryAfter = null) => new(ScanKind.Failed, outcome, retryAfter);

        public static Scan Completed(int pages, int objects, int applied) => new(ScanKind.Completed, Pages: pages, Objects: objects, Applied: applied);
    }

    private sealed record ObjectResult(Scan? Stopped, int Applied, int Held)
    {
        public static ObjectResult Stop(Scan scan) => new(scan, 0, 0);

        public static ObjectResult Done(int applied, int held) => new(null, applied, held);
    }

    private sealed record CutOver(long Fence, int Replayed);

    // Where a run left the generation: its state, or the error that ended the run with nothing more recorded.
    private sealed record Ended(long Generation, InitialSyncState State, Error? Error)
    {
        public static Ended Lost(Holding holding) => new(holding.Generation, holding.State, InitialSyncErrors.ClaimLost);

        public static Ended Unavailable(Holding holding) => new(holding.Generation, holding.State, InitialSyncErrors.Unavailable);

        public static Ended From(Step step, Holding holding) => step switch
        {
            Step.Waiting => new(holding.Generation, holding.State, null),
            Step.Failed => new(holding.Generation, InitialSyncState.Failed, null),
            Step.RequiresRestart => new(holding.Generation, InitialSyncState.RequiresRestart, null),
            Step.Lost => Lost(holding),
            _ => Unavailable(holding),
        };
    }

    private sealed class Progress
    {
        private readonly Dictionary<SyncScopeOutcome, int> _outcomes = [];

        public CorrelationId Correlation { get; } = CorrelationId.New();

        public IReadOnlyDictionary<SyncScopeOutcome, int> Outcomes => _outcomes;

        public int FieldsApplied { get; set; }

        public int FieldsHeld { get; set; }

        public int ReceiptsReplayed { get; set; }

        public void Count(SyncScopeOutcome outcome) => _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + 1;
    }

    private sealed class HeldRow
    {
        public long Generation { get; init; }

        public string State { get; init; } = string.Empty;

        public Guid? ClaimId { get; init; }
    }

    private sealed class HeldReceipt
    {
        public Guid ReceiptId { get; init; }

        public long IngressSequence { get; init; }
    }
}
