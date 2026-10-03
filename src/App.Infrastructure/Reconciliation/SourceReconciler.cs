using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Ingress;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;

namespace WorkContinuity.Infrastructure.Reconciliation;

/// <summary>The bounds of reconciliation: how long a claim lasts and how failures are retried.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>Gets or sets how long a claim lasts before another worker may take the target.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how many consecutive attempts may end with an unusable answer (the object missing, or a response the
    /// reader does not understand) before the target is held. Transient failures and missing access are never held.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Gets or sets the first retry delay; each later attempt doubles it, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the longest backoff; a longer wait the provider asks for is still respected.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the longest wait a provider's answer may impose; a longer request is cut to it, so no answer parks a
    /// target indefinitely.
    /// </summary>
    public TimeSpan MaxProviderWait { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClaimLease, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BaseRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, BaseRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxProviderWait, MaxRetryDelay);
    }
}

/// <summary>
/// Reconciles known source objects with their source of truth (Gate 3E): claims due targets of one tenant and connection,
/// rereads them through the source's authoritative reader, and applies each read through the Gate 2 processing core.
/// </summary>
/// <remarks>
/// <para>
/// A claim is a short transaction: due targets are taken with <c>FOR UPDATE SKIP LOCKED</c>, each with this run's claim
/// id, its current generation and a lease, so two workers never hold one target. Access is then checked locally: while
/// the tenant is not active, or the connection, its authorization or its user does not stand, no credential is asked for
/// and nothing is sent to the source; each claimed target records the refusal and waits on the backoff, and its next
/// attempt checks again, so it resumes by itself once access stands, with no new request. The read happens outside any
/// transaction. Each target is then applied in its own transaction, which locks the target and checks again that the
/// claim is still this run's, that the tenant is active, and that the connection is active and its authorization active
/// and held by the delegated identity the read used; a lost claim records nothing. The authorization must also still be
/// the one access found before the read: authorized again while the source was read, the read is stale, discarded and
/// retried under the current authorization; unchanged, a read under another identity is a stable mismatch, held.
/// </para>
/// <para>
/// Failures are retried by what retrying can change (owner review of 3E). A transient failure and missing access may
/// outlast any number of attempts and end by themselves, so they wait on the capped backoff for as long as they last and
/// are never held; an answer the source gave that cannot be used is retried a bounded number of consecutive times; and
/// what retrying cannot change, such as an inactive object, another scope or ambiguous ordering, is held at once.
/// </para>
/// <para>
/// A read equal to what the Snapshot holds, the same value at the same source update time, resolves the target with no
/// event. Any other read is stored as an evidence receipt, the exact provider values consumed, under its own schema
/// version and the raw-content retention, and decided by the unchanged processing core as a <c>Confirmed</c> event:
/// ordering, plausibility and the Snapshot projection decide the write, and reconciliation never writes the Snapshot
/// itself (D3E-7). The target's resolution, hold or retry is written in that same transaction, and resolution compares
/// the claimed generation with the latest, so a newer request is never closed by older work (D3E-6).
/// </para>
/// <para>
/// Logs carry ids, counts and outcome codes only: never a value, a label, evidence or a credential.
/// </para>
/// </remarks>
internal sealed class SourceReconciler : ISourceReconciler
{
    private readonly ITenantScopedDatabase _database;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IReadOnlyDictionary<string, IAuthoritativeSourceReader> _readers;
    private readonly IClock _clock;
    private readonly ReconciliationOptions _options;
    private readonly EventProcessingOptions _processing;
    private readonly ILogger<SourceReconciler> _logger;

    /// <exception cref="InvalidOperationException">Two readers serve one source system.</exception>
    public SourceReconciler(
        ITenantScopedDatabase database,
        ITenantContextAccessor tenantContextAccessor,
        IEnumerable<IAuthoritativeSourceReader> readers,
        IClock clock,
        ReconciliationOptions options,
        EventProcessingOptions processing,
        ILogger<SourceReconciler> logger)
    {
        var bySource = new Dictionary<string, IAuthoritativeSourceReader>(StringComparer.Ordinal);
        foreach (var reader in readers)
        {
            if (!bySource.TryAdd(reader.SourceSystem, reader))
            {
                throw new InvalidOperationException("More than one authoritative reader is registered for one source system.");
            }
        }

        _database = database;
        _tenantContextAccessor = tenantContextAccessor;
        _readers = bySource;
        _clock = clock;
        _options = options;
        _processing = processing;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ReconciliationRunSummary>> RunAsync(ReconciliationRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsTrusted(run.Tenant))
        {
            return Result<ReconciliationRunSummary>.Failure(ReconciliationErrors.TenantMismatch);
        }

        if (!_readers.TryGetValue(run.SourceSystem, out var reader))
        {
            return Result<ReconciliationRunSummary>.Failure(ReconciliationErrors.ReaderMissing);
        }

        var connectionReference = ConnectionRecord.ReferenceFor(run.ConnectionId);
        var claimId = Guid.CreateVersion7();
        var claimedAt = _clock.UtcNow;
        var claimed = await _database.ExecuteAsync(
            (dbContext, token) => ClaimAsync(dbContext, run, connectionReference, claimId, claimedAt, token),
            cancellationToken);
        if (claimed.IsFailure)
        {
            _logger.LogWarning(
                "Reconciliation of connection {ConnectionId} for tenant {TenantId} could not claim targets: {ErrorCode}.",
                run.ConnectionId,
                run.Tenant,
                claimed.Error.Code);
            return Result<ReconciliationRunSummary>.Failure(ReconciliationErrors.Unavailable);
        }

        // Access first, locally: while it does not stand, nothing is read and no credential is asked for. When it stands,
        // the authorization it found is the one every read of this run must still find when it is applied.
        var access = ReconciliationOutcome.None;
        var atRead = new Standing(ReconciliationOutcome.None, Subject: null);
        if (claimed.Value.Count > 0)
        {
            var standing = await _database.ExecuteAsync(
                (dbContext, token) => StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, token),
                cancellationToken);
            if (standing.IsFailure)
            {
                _logger.LogWarning(
                    "Reconciliation of connection {ConnectionId} for tenant {TenantId} could not check access: {ErrorCode}.",
                    run.ConnectionId,
                    run.Tenant,
                    standing.Error.Code);
                return Result<ReconciliationRunSummary>.Failure(ReconciliationErrors.Unavailable);
            }

            access = standing.Value.Refusal;
            atRead = standing.Value;
        }

        var correlation = CorrelationId.New();
        var outcomes = new Dictionary<ReconciliationOutcome, int>();
        var notApplied = 0;
        foreach (var chunk in Chunks(claimed.Value, reader.MaxObjectsPerRead))
        {
            var read = access == ReconciliationOutcome.None ? await ReadAsync(reader, run, correlation, chunk, cancellationToken) : null;
            foreach (var target in chunk)
            {
                var applied = await _database.ExecuteAsync(
                    (dbContext, token) => read is null
                        ? AwaitAccessAsync(dbContext, run, claimId, target, access, token)
                        : ApplyAsync(dbContext, run, claimId, target, read, atRead, correlation, token),
                    cancellationToken);
                if (applied.IsFailure || applied.Value == ReconciliationOutcome.None)
                {
                    notApplied++;
                    continue;
                }

                outcomes[applied.Value] = outcomes.GetValueOrDefault(applied.Value) + 1;
            }
        }

        _logger.LogInformation(
            "Reconciliation of connection {ConnectionId} for tenant {TenantId} claimed {Claimed} targets: {Outcomes}; {NotApplied} not applied (correlation {CorrelationId}).",
            run.ConnectionId,
            run.Tenant,
            claimed.Value.Count,
            string.Join(", ", outcomes.OrderBy(pair => pair.Key).Select(pair => ReconciliationOutcomes.CodeOf(pair.Key) + "=" + pair.Value)),
            notApplied,
            correlation);
        return Result<ReconciliationRunSummary>.Success(new ReconciliationRunSummary(claimed.Value.Count, outcomes, notApplied));
    }

    /// <inheritdoc />
    public async Task<Result<int>> RequestSweepAsync(ReconciliationRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsTrusted(run.Tenant))
        {
            return Result<int>.Failure(ReconciliationErrors.TenantMismatch);
        }

        var connectionReference = ConnectionRecord.ReferenceFor(run.ConnectionId);
        var requestedAt = _clock.UtcNow;

        // Only targets already reconciled: the sweep raises their generation and never inserts one, so it can only reread
        // objects Fully already knows (D3E-5); the least recently verified first, so successive sweeps cover all of them.
        var swept = await _database.ExecuteAsync(
            (dbContext, token) => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE public.reconciliation_targets AS t
                SET generation = t.generation + 1, state = 'Pending', request_kind = 'Sweep', requested_at_utc = {requestedAt},
                    requested_by_event_id = NULL, attempts = 0, next_attempt_at_utc = NULL
                FROM (
                    SELECT tenant_id, object_system, object_connection, object_type, object_id, field
                    FROM public.reconciliation_targets
                    WHERE tenant_id = {run.Tenant.Value} AND object_system = {run.SourceSystem}
                      AND object_connection = {connectionReference} AND state = 'Resolved' AND claim_id IS NULL
                    ORDER BY verified_at_utc NULLS FIRST, object_type, object_id, field
                    LIMIT {run.MaxTargets}
                    FOR UPDATE SKIP LOCKED) AS due
                WHERE t.tenant_id = due.tenant_id AND t.object_system = due.object_system
                  AND t.object_connection = due.object_connection AND t.object_type = due.object_type
                  AND t.object_id = due.object_id AND t.field = due.field
                """,
                token),
            cancellationToken);
        if (swept.IsFailure)
        {
            return Result<int>.Failure(ReconciliationErrors.Unavailable);
        }

        _logger.LogInformation(
            "Sweep of connection {ConnectionId} for tenant {TenantId} requested {Requested} known targets.",
            run.ConnectionId,
            run.Tenant,
            swept.Value);
        return Result<int>.Success(swept.Value);
    }

    private bool IsTrusted(TenantId tenant) =>
        _tenantContextAccessor.Current.TryGetTenantId(out var trusted) && trusted == tenant;

    private async Task<List<ReconciliationTarget>> ClaimAsync(
        WorkContinuityDbContext dbContext,
        ReconciliationRun run,
        string connectionReference,
        Guid claimId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var leaseUntil = now + _options.ClaimLease;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
                $"""
                UPDATE public.reconciliation_targets AS t
                SET claim_id = {claimId}, claimed_generation = t.generation, claimed_until_utc = {leaseUntil},
                    attempts = t.attempts + 1, last_attempted_at_utc = {now}
                FROM (
                    SELECT tenant_id, object_system, object_connection, object_type, object_id, field
                    FROM public.reconciliation_targets
                    WHERE tenant_id = {run.Tenant.Value} AND object_system = {run.SourceSystem}
                      AND object_connection = {connectionReference} AND state = 'Pending'
                      AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
                      AND (claimed_until_utc IS NULL OR claimed_until_utc <= {now})
                    ORDER BY requested_at_utc, object_type, object_id, field
                    LIMIT {run.MaxTargets}
                    FOR UPDATE SKIP LOCKED) AS due
                WHERE t.tenant_id = due.tenant_id AND t.object_system = due.object_system
                  AND t.object_connection = due.object_connection AND t.object_type = due.object_type
                  AND t.object_id = due.object_id AND t.field = due.field
                RETURNING t.object_type AS "ObjectType", t.object_id AS "ObjectId", t.field AS "Field",
                          t.data_type AS "DataType", t.source_scope AS "SourceScope"
                """)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new ReconciliationTarget(
                new ObjectReference(run.SourceSystem, connectionReference, row.ObjectType, row.ObjectId),
                row.Field,
                Enum.Parse<FactDataType>(row.DataType),
                row.SourceScope))
            .OrderBy(target => target.WorkObject.SourceObjectId, StringComparer.Ordinal)
            .ThenBy(target => target.Field, StringComparer.Ordinal)
            .ToList();
    }

    // One read addresses at most the reader's bound of distinct objects, and keeps every field of an object together.
    private static IEnumerable<IReadOnlyList<ReconciliationTarget>> Chunks(IReadOnlyList<ReconciliationTarget> targets, int maxObjects) =>
        targets
            .GroupBy(target => (target.WorkObject.ObjectType, target.WorkObject.SourceObjectId))
            .Chunk(Math.Max(1, maxObjects))
            .Select(objects => (IReadOnlyList<ReconciliationTarget>)objects.SelectMany(group => group).ToList());

    private async Task<AuthoritativeReadResult> ReadAsync(
        IAuthoritativeSourceReader reader,
        ReconciliationRun run,
        CorrelationId correlation,
        IReadOnlyList<ReconciliationTarget> targets,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(new AuthoritativeReadRequest(run.Tenant, run.ConnectionId, correlation, targets), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type is logged: a reader's exception text could quote the source.
            _logger.LogWarning(
                "The {SourceSystem} reader failed with {ExceptionType} for connection {ConnectionId}; the targets are retried.",
                run.SourceSystem,
                exception.GetType().Name,
                run.ConnectionId);
            return AuthoritativeReadResult.Failed(ReconciliationOutcome.ProviderUnavailable, identity: null);
        }
    }

    // Access does not stand: the target records the refusal, with nothing read, and waits on the backoff; its next attempt
    // checks access again.
    private async Task<ReconciliationOutcome> AwaitAccessAsync(
        WorkContinuityDbContext dbContext,
        ReconciliationRun run,
        Guid claimId,
        ReconciliationTarget target,
        ReconciliationOutcome refusal,
        CancellationToken cancellationToken)
    {
        var row = await LockClaimedAsync(dbContext, run, claimId, target, cancellationToken);
        return row is null
            ? ReconciliationOutcome.None
            : Record(row, claimId, refusal, resolvingEventId: null, _clock.UtcNow, retryAfter: null);
    }

    private async Task<ReconciliationOutcome> ApplyAsync(
        WorkContinuityDbContext dbContext,
        ReconciliationRun run,
        Guid claimId,
        ReconciliationTarget target,
        AuthoritativeReadResult read,
        Standing atRead,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        var row = await LockClaimedAsync(dbContext, run, claimId, target, cancellationToken);
        if (row is null)
        {
            // The claim lapsed and another worker took the target: this run records nothing for it.
            return ReconciliationOutcome.None;
        }

        var now = _clock.UtcNow;
        var standing = await StandingAsync(dbContext, run.Tenant, run.ConnectionId, run.SourceSystem, cancellationToken);
        if (standing.Refusal != ReconciliationOutcome.None)
        {
            return Record(row, claimId, standing.Refusal, resolvingEventId: null, now, retryAfter: null);
        }

        // The authorization the read began under must still be the connection's: one authorized again, or by another
        // identity, while the source was read makes the read stale, whatever it reports. It is discarded, never applied,
        // and the next attempt reads again under the current authorization (owner review of 3E).
        if (!standing.SameAuthorizationAs(atRead))
        {
            return Record(row, claimId, ReconciliationOutcome.AuthorityChanged, resolvingEventId: null, now, retryAfter: null);
        }

        var reading = read.ReadingFor(target);
        var confirmed = await ConfirmAsync(
            dbContext, run.Tenant, run.ConnectionId, target, reading, read.Identity, standing.Subject, correlation, now, _processing.ClockSkewTolerance, cancellationToken);
        return Record(row, claimId, confirmed.Outcome, confirmed.ResolvingEventId, now, reading.State is null ? read.RetryAfter : null);
    }

    /// <summary>
    /// Confirms one authoritative reading of one field in the caller's transaction, as reconciliation applies a read and as
    /// initial sync (Gate 3F) applies its baseline: one path, so a value becomes <c>Confirmed</c> nowhere else.
    /// </summary>
    /// <remarks>
    /// A refused reading is its own outcome. A reading bound to a state must come from the connection's delegated identity,
    /// with the subject access found. A state the Snapshot already holds records nothing. Any other state is stored as its
    /// evidence receipt and decided by the unchanged processing core as a <c>Confirmed</c> event, which alone may reach the
    /// Snapshot. The caller holds the field's target, or the right to establish it, and has checked that access stands.
    /// </remarks>
    /// <returns>The outcome, and the confirmed ledger event when the outcome resolves the field.</returns>
    internal static async Task<(ReconciliationOutcome Outcome, Guid? ResolvingEventId)> ConfirmAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        ReconciliationTarget target,
        TargetReading reading,
        ConnectorIdentity? readIdentity,
        string? subject,
        CorrelationId correlation,
        DateTimeOffset now,
        TimeSpan clockSkewTolerance,
        CancellationToken cancellationToken)
    {
        if (reading.State is not { } state)
        {
            return (reading.Refusal, null);
        }

        // Only a read under the connection's current delegated identity may confirm a value (D3E-8).
        if (readIdentity is not { Kind: ConnectorIdentityKind.DelegatedUser } identity
            || !identity.Serves(tenant, connectionId)
            || !string.Equals(identity.Subject, subject, StringComparison.Ordinal))
        {
            return (ReconciliationOutcome.IdentityMismatch, null);
        }

        if (await IsCurrentAsync(dbContext, tenant, target, state, cancellationToken))
        {
            return (ReconciliationOutcome.Current, null);
        }

        // The read's evidence becomes its receipt, settled in this transaction: the database stamps its time, and no
        // outbox message is written, so the transport never sees it.
        var workObject = target.WorkObject;
        var receipt = RawReceiptRecord.Receive(
            Guid.CreateVersion7(),
            tenant,
            workObject.ConnectionReference,
            workObject.SourceSystem,
            sourceEventId: null,
            state.Evidence.SchemaVersion,
            state.Evidence.Document,
            EventIngressService.Fingerprint(state.Evidence.Document));
        dbContext.RawReceipts.Add(receipt);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Only here does a value become Confirmed: from a bound read of the source, never from a delivery (D3E-1). The
        // processing core decides it and projects it; reconciliation never writes the Snapshot itself (D3E-7).
        var confirmed = new NormalizedSourceEvent(
            CanonicalEventSchemas.ObjectChangedType,
            CanonicalEventSchemas.ObjectChangedVersion1,
            workObject,
            sourceRevision: null,
            occurredAt: null,
            sourceUpdatedAt: state.SourceUpdatedAt,
            new CanonicalEventContent([new FieldChange(target.Field, target.DataType, before: null, after: state.Value)], actor: null, sourceReference: null),
            target.SourceScope,
            SourceValueConfirmation.Confirmed);
        var result = await EventProcessor.DecideAndProjectAsync(
            dbContext, receipt, confirmed, correlation, tenant, now, clockSkewTolerance, cancellationToken);

        // The ledger entry before the target names it.
        await dbContext.SaveChangesAsync(cancellationToken);
        var outcome = result.Outcome switch
        {
            EventProcessingOutcome.AcceptedForProjection => ReconciliationOutcome.Repaired,
            EventProcessingOutcome.Stale => ReconciliationOutcome.Superseded,
            EventProcessingOutcome.ReconciliationRequired when result.ReasonCode == ProcessingErrors.NoOrderingEvidence.Code
                => ReconciliationOutcome.OrderingUnevidenced,
            EventProcessingOutcome.ReconciliationRequired => ReconciliationOutcome.OrderingAmbiguous,
            EventProcessingOutcome.Quarantined when result.QuarantineReason == QuarantineReason.ImpossibleOrdering
                => ReconciliationOutcome.OrderingImpossible,
            _ => ReconciliationOutcome.Quarantined,
        };

        return (outcome, ReconciliationOutcomes.DispositionOf(outcome) == ReconciliationDisposition.Resolve ? result.EventId.Value : null);
    }

    // The target locked, if this run's claim on it still holds; null when the claim lapsed and another worker took it.
    private static async Task<ReconciliationTargetRecord?> LockClaimedAsync(
        WorkContinuityDbContext dbContext,
        ReconciliationRun run,
        Guid claimId,
        ReconciliationTarget target,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var workObject = target.WorkObject;
        var row = (await dbContext.ReconciliationTargets
            .FromSql($"""
                SELECT * FROM public.reconciliation_targets
                WHERE tenant_id = {run.Tenant.Value} AND object_system = {workObject.SourceSystem}
                  AND object_connection = {workObject.ConnectionReference} AND object_type = {workObject.ObjectType}
                  AND object_id = {workObject.SourceObjectId} AND field = {target.Field}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken)).SingleOrDefault();
        return row is not null && row.IsClaimedBy(claimId) ? row : null;
    }

    /// <summary>
    /// Whether access stands, checked locally with no provider call, as reconciliation and initial sync (Gate 3F) both check
    /// it: the tenant must be active, and the connection active, of the source, with an active authorization of an active
    /// user, the identity whose subject a read must have used. Anything else waits for access to stand again.
    /// </summary>
    internal static async Task<Standing> StandingAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<StandingRow>(
                $"""
                SELECT t.status AS "TenantStatus", c.status AS "ConnectionStatus", c.source_system AS "SourceSystem",
                       a.state AS "AuthorizationState", a.provider_subject AS "ProviderSubject", u.is_active AS "UserActive",
                       a.authorized_at_utc AS "AuthorizedAtUtc"
                FROM public.tenants AS t
                LEFT JOIN public.connections AS c ON c.tenant_id = t.tenant_id AND c.connection_id = {connectionId}
                LEFT JOIN public.connector_authorizations AS a ON a.tenant_id = c.tenant_id AND a.connection_id = c.connection_id
                LEFT JOIN public.users AS u ON u.tenant_id = a.tenant_id AND u.user_id = a.canonical_user_id
                WHERE t.tenant_id = {tenant.Value}
                """)
            .ToListAsync(cancellationToken);

        if (rows.Count != 1 || rows[0].TenantStatus != nameof(TenantStatus.Active))
        {
            return new Standing(ReconciliationOutcome.TenantInactive, Subject: null);
        }

        var row = rows[0];
        if (row.ConnectionStatus != nameof(ConnectionStatus.Active) || row.SourceSystem != sourceSystem)
        {
            return new Standing(ReconciliationOutcome.ConnectionUnavailable, Subject: null);
        }

        // An authorization revoked or needing authorization again, or a user no longer active, leaves the connection unusable
        // until 3B or an administrator restores it.
        return row.AuthorizationState != nameof(ConnectorAuthorizationState.Active) || row.UserActive != true || row.ProviderSubject is null
            ? new Standing(ReconciliationOutcome.ConnectionUnavailable, Subject: null)
            : new Standing(ReconciliationOutcome.None, row.ProviderSubject, row.AuthorizedAtUtc);
    }

    // The Snapshot already holds exactly this: the same value, accepted at the same source update time. The field's
    // ordering state is locked first, as processing locks it, so nothing advances it before this transaction ends.
    private static async Task<bool> IsCurrentAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        ReconciliationTarget target,
        AuthoritativeFieldState state,
        CancellationToken cancellationToken)
    {
        if (state.SourceUpdatedAt is not { } updatedAt)
        {
            return false;
        }

        var workObject = target.WorkObject;
        var ordering = (await dbContext.FieldOrderingStates
            .FromSql($"""
                SELECT * FROM public.field_ordering_states
                WHERE tenant_id = {tenant.Value} AND object_system = {workObject.SourceSystem}
                  AND object_connection = {workObject.ConnectionReference} AND object_type = {workObject.ObjectType}
                  AND object_id = {workObject.SourceObjectId} AND field = {target.Field}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (ordering is null || ordering.SourceRevision is not null || ordering.SourceUpdatedAtUtc != updatedAt)
        {
            return false;
        }

        return await SnapshotProjection.HoldsAsync(
            dbContext, tenant, workObject, target.Field, state.Value, ordering.LastEventId, cancellationToken);
    }

    private ReconciliationOutcome Record(
        ReconciliationTargetRecord row,
        Guid claimId,
        ReconciliationOutcome outcome,
        Guid? resolvingEventId,
        DateTimeOffset now,
        TimeSpan? retryAfter)
    {
        switch (ReconciliationOutcomes.DispositionOf(outcome))
        {
            case ReconciliationDisposition.Resolve:
                row.Resolve(claimId, outcome, resolvingEventId, now);
                break;
            case ReconciliationDisposition.Retry:
            case ReconciliationDisposition.AwaitAccess:
            case ReconciliationDisposition.RetryThenHold:
                // Retried on the backoff of its run of like failures; only an unusable answer is ever held for its attempts.
                row.Retry(claimId, outcome, now + RetryDelay(row.AttemptsWith(outcome), retryAfter), _options.MaxAttempts);
                break;
            default:
                row.Hold(claimId, outcome);
                break;
        }

        return outcome;
    }

    // Exponential from the base over the run of like failures, capped, so no attempt comes sooner than the base. A longer
    // wait the provider asked for is respected, never shortened, up to its own bound, so no answer parks a target for good.
    private TimeSpan RetryDelay(int attempts, TimeSpan? providerWait)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var backoff = TimeSpan.FromTicks(Math.Min(_options.MaxRetryDelay.Ticks, _options.BaseRetryDelay.Ticks * (1L << Math.Min(exponent, 20))));
        if (providerWait is not { } wait)
        {
            return backoff;
        }

        var bounded = wait < _options.MaxProviderWait ? wait : _options.MaxProviderWait;
        return bounded > backoff ? bounded : backoff;
    }

    /// <summary>
    /// Whether access stands, and when it does, the authorization it found: the delegated identity's subject and when the
    /// connection was last authorized. 3B sets that time only when the connection is authorized or authorized again (a
    /// renewal records its own time and keeps it), and only the connection's own delegating user may authorize it again.
    /// </summary>
    internal sealed record Standing(ReconciliationOutcome Refusal, string? Subject, DateTimeOffset? AuthorizedAt = null)
    {
        // The same authorization: the same identity, not authorized again since.
        public bool SameAuthorizationAs(Standing other) =>
            string.Equals(Subject, other.Subject, StringComparison.Ordinal) && AuthorizedAt == other.AuthorizedAt;
    }

    private sealed class ClaimRow
    {
        public string ObjectType { get; init; } = string.Empty;

        public string ObjectId { get; init; } = string.Empty;

        public string Field { get; init; } = string.Empty;

        public string DataType { get; init; } = string.Empty;

        public string? SourceScope { get; init; }
    }

    private sealed class StandingRow
    {
        public string? TenantStatus { get; init; }

        public string? ConnectionStatus { get; init; }

        public string? SourceSystem { get; init; }

        public string? AuthorizationState { get; init; }

        public string? ProviderSubject { get; init; }

        public bool? UserActive { get; init; }

        public DateTimeOffset? AuthorizedAtUtc { get; init; }
    }
}
