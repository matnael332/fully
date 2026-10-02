using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.SourceOfTruth;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Reconciliation;

namespace WorkContinuity.Infrastructure.Processing;

/// <summary>
/// The processing pipeline (architecture §3: normalization, schema validation, deduplication, identity,
/// authority, ordering and conflict check, canonical event).
/// </summary>
/// <remarks>
/// <para>
/// Everything happens in one transaction of the tenant the transport restored from the validated envelope, under
/// forced row-level security. The receipt is locked first, so concurrent deliveries of one receipt queue behind
/// each other, and the ledger then decides idempotency: a receipt already in the ledger is a no-op. The
/// normalizer supplies only source-derived parts; the tenant, the source, the provider event id, the receipt
/// time, the correlation and the causation are set here from the receipt and the envelope.
/// </para>
/// <para>
/// Authority is resolved per change, and any change the source is not authoritative for quarantines the whole
/// event. Ordering is decided per object and field against the locked ordering state; ambiguity advances
/// nothing and is recorded for reconciliation; impossibility is quarantined. The ledger entry, its fields, the
/// ordering state and the receipt's state commit together or not at all, so a crash leaves either nothing, and
/// the redelivered message reprocesses, or the full outcome, and the redelivered message is a no-op.
/// </para>
/// <para>
/// An accepted result is then projected onto the Snapshot (Gate 2E) in the same transaction, from its handoff
/// alone: the decision above is the projection's only input, so projection can neither re-decide it nor run for
/// any other outcome, and the ledger, the ordering state and the Snapshot commit together or not at all.
/// </para>
/// <para>
/// A receipt whose raw content retention cleared before it was ever processed (Gate 2F) is refused after the
/// idempotency check, and nothing is written: nothing is left to normalize, so the message is dead-lettered
/// with its reason instead of becoming a guessed event. A receipt already in the ledger needs no content, so
/// its redelivery stays a no-op after the purge.
/// </para>
/// <para>
/// Hygiene: logs carry ids, outcomes, reason codes, counts and the Snapshot version only, never the payload or
/// a value. Metrics count each outcome by its canonical reason and reason code, with its duration and lag; a
/// projection counts only once its transaction committed.
/// </para>
/// </remarks>
internal sealed class EventProcessor : IEventProcessor
{
    private readonly ITenantScopedDatabase _database;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IReadOnlyDictionary<string, ISourceEventNormalizer> _normalizers;
    private readonly IClock _clock;
    private readonly EventProcessingOptions _options;
    private readonly EventBackboneMetrics _metrics;
    private readonly ILogger<EventProcessor> _logger;

    /// <exception cref="InvalidOperationException">Two normalizers serve one source system.</exception>
    public EventProcessor(
        ITenantScopedDatabase database,
        ITenantContextAccessor tenantContextAccessor,
        IEnumerable<ISourceEventNormalizer> normalizers,
        IClock clock,
        EventProcessingOptions options,
        EventBackboneMetrics metrics,
        ILogger<EventProcessor> logger)
    {
        var bySource = new Dictionary<string, ISourceEventNormalizer>(StringComparer.Ordinal);
        foreach (var normalizer in normalizers)
        {
            if (!bySource.TryAdd(normalizer.SourceSystem, normalizer))
            {
                throw new InvalidOperationException("More than one normalizer is registered for one source system.");
            }
        }

        _database = database;
        _tenantContextAccessor = tenantContextAccessor;
        _normalizers = bySource;
        _clock = clock;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EventProcessingResult>> ProcessAsync(
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();

        if (!_tenantContextAccessor.Current.TryGetTenantId(out var tenant) || tenant != envelope.TenantId)
        {
            _logger.LogError(
                "Processing refused message {MessageId}: {ReasonCode} (correlation {CorrelationId}).",
                envelope.MessageId,
                ProcessingErrors.TenantMismatch.Code,
                envelope.CorrelationId);
            return Refuse(ProcessingErrors.TenantMismatch, started);
        }

        var processed = await _database.ExecuteAsync(
            (dbContext, token) => ProcessReceiptAsync(dbContext, envelope, tenant, token),
            cancellationToken);

        if (processed.IsFailure)
        {
            // Nothing committed: a lost race, a lost connection or a lock timeout. The message is retried.
            _logger.LogWarning(
                "Processing of receipt {ReceiptId} for tenant {TenantId} did not commit: {ErrorCode}; it will be retried (correlation {CorrelationId}).",
                envelope.CausationId,
                tenant,
                processed.Error.Code,
                envelope.CorrelationId);
            _metrics.ProcessingFailed(ProcessingErrors.Unavailable, transient: true, Stopwatch.GetElapsedTime(started));
            return Result<EventProcessingResult>.Failure(ProcessingErrors.Unavailable);
        }

        if (processed.Value is not { } outcome)
        {
            _logger.LogError(
                "Processing found no receipt {ReceiptId} in tenant {TenantId}: {ReasonCode}; the message cannot be processed (correlation {CorrelationId}).",
                envelope.CausationId,
                tenant,
                ProcessingErrors.ReceiptNotVisible.Code,
                envelope.CorrelationId);
            return Refuse(ProcessingErrors.ReceiptNotVisible, started);
        }

        if (outcome.Result is not { } result)
        {
            _logger.LogError(
                "Processing refused receipt {ReceiptId} of tenant {TenantId}: {ReasonCode}; its raw content expired before it was processed, and nothing was recorded (correlation {CorrelationId}).",
                envelope.CausationId,
                tenant,
                ProcessingErrors.ReceiptContentExpired.Code,
                envelope.CorrelationId);
            return Refuse(ProcessingErrors.ReceiptContentExpired, started);
        }

        // The outcome committed; only now is it measured.
        _metrics.Processed(result, Stopwatch.GetElapsedTime(started), outcome.Lag);
        if (outcome.Projection is { } projected)
        {
            _metrics.Projected(projected.Fields, projected.Duration, _clock.UtcNow - outcome.ReceivedAtUtc);
        }

        Log(result, outcome.Projection?.SnapshotVersion, tenant, envelope);
        return Result<EventProcessingResult>.Success(result);
    }

    private Result<EventProcessingResult> Refuse(Error reason, long started)
    {
        _metrics.ProcessingFailed(reason, transient: false, Stopwatch.GetElapsedTime(started));
        return Result<EventProcessingResult>.Failure(reason);
    }

    private async Task<Processed?> ProcessReceiptAsync(
        WorkContinuityDbContext dbContext,
        TransportEnvelope envelope,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        var receiptId = envelope.CausationId;

        // Row-level security shows this tenant's receipt only; the lock queues concurrent deliveries of it.
        var receipt = (await dbContext.RawReceipts
            .FromSql($"SELECT * FROM public.raw_receipts WHERE receipt_id = {receiptId} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (receipt is null)
        {
            return null;
        }

        // The ledger decides idempotency from ids alone, so a redelivery stays a no-op after its content expired.
        var recorded = await dbContext.EventLedger
            .AsNoTracking()
            .Where(entry => entry.ReceiptId == receiptId)
            .Select(entry => new { entry.EventId, entry.Outcome })
            .SingleOrDefaultAsync(cancellationToken);
        if (recorded is not null)
        {
            return new Processed(
                EventProcessingResult.Duplicate(CanonicalEventId.From(recorded.EventId), receiptId, recorded.Outcome),
                receipt.ReceivedAtUtc,
                Lag: null,
                Projection: null);
        }

        // Retention cleared the content of a receipt that was never processed: nothing is left to decide from.
        if (!receipt.HasContent)
        {
            return new Processed(Result: null, receipt.ReceivedAtUtc, Lag: null, Projection: null);
        }

        var processedAt = _clock.UtcNow;
        var result = await DecideAsync(dbContext, receipt, envelope, tenant, processedAt, cancellationToken);

        // Only an accepted result has a handoff, and it is projected in this same transaction.
        Projected? projected = null;
        if (result.TryGetProjection(out var projection))
        {
            var projecting = Stopwatch.GetTimestamp();
            try
            {
                var snapshotVersion = await SnapshotProjection.ProjectAsync(dbContext, projection!, cancellationToken);
                projected = new Projected(snapshotVersion, projection!.AdvancedFields.Count, Stopwatch.GetElapsedTime(projecting));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The whole transaction rolls back, and the message is retried.
                _metrics.ProjectionFailed();
                throw;
            }
        }

        return new Processed(result, receipt.ReceivedAtUtc, processedAt - receipt.ReceivedAtUtc, projected);
    }

    private async Task<EventProcessingResult> DecideAsync(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        TransportEnvelope envelope,
        TenantId tenant,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        var receiptId = receipt.ReceiptId;
        var eventId = EventIdentity.ForReceipt(receiptId);
        var payload = receipt.Payload
            ?? throw new InvalidOperationException("A receipt whose content expired is refused before it is decided.");

        if (!_normalizers.TryGetValue(receipt.SourceSystem, out var normalizer) || !normalizer.Supports(receipt.SchemaVersion))
        {
            return QuarantineReceipt(dbContext, receipt, eventId, envelope.CorrelationId, processedAt, QuarantineReason.UnknownSchemaVersion, ProcessingErrors.NormalizerMissing);
        }

        Result<NormalizedSourceEvent> normalized;
        try
        {
            normalized = normalizer.Normalize(new SourcePayload(
                receipt.SourceSystem, receipt.ConnectionReference, receipt.SchemaVersion, receipt.SourceEventId, payload));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type is logged: a normalizer's exception message could quote the payload.
            _logger.LogWarning(
                "The {SourceSystem} normalizer failed with {ExceptionType} on receipt {ReceiptId}.",
                receipt.SourceSystem,
                exception.GetType().Name,
                receiptId);
            return QuarantineReceipt(dbContext, receipt, eventId, envelope.CorrelationId, processedAt, QuarantineReason.BadSchema, ProcessingErrors.NormalizerFailed);
        }

        if (normalized.IsFailure)
        {
            return QuarantineReceipt(dbContext, receipt, eventId, envelope.CorrelationId, processedAt, QuarantineReason.BadSchema, ProcessingErrors.NormalizerRejected);
        }

        return await DecideNormalizedAsync(
            dbContext,
            receipt,
            normalized.Value,
            envelope.CorrelationId,
            tenant,
            processedAt,
            _options.ClockSkewTolerance,
            requestReconciliation: true,
            cancellationToken);
    }

    /// <summary>
    /// Decides one normalized event of one receipt and records it, in the caller's transaction: canonical event, authority,
    /// plausibility, confirmation, ordering, ledger. The transport path calls it for every delivery, and reconciliation
    /// (Gate 3E) for every confirmed read, so both go through the one decision.
    /// </summary>
    /// <remarks>
    /// With <paramref name="requestReconciliation"/>, a <c>ReconciliationRequired</c> outcome also writes or updates the
    /// reconciliation target of every field it holds, with the event's source scope, in this same transaction (decision
    /// D3E-11): no such outcome commits without the identifiers that execute it. Reconciliation itself records the outcome
    /// on the target it holds, so it does not request one.
    /// </remarks>
    internal static async Task<EventProcessingResult> DecideNormalizedAsync(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        NormalizedSourceEvent parts,
        CorrelationId correlationId,
        TenantId tenant,
        DateTimeOffset processedAt,
        TimeSpan clockSkewTolerance,
        bool requestReconciliation,
        CancellationToken cancellationToken)
    {
        var result = await DecideCanonicalAsync(dbContext, receipt, parts, correlationId, tenant, processedAt, clockSkewTolerance, cancellationToken);
        if (requestReconciliation && result.Outcome == EventProcessingOutcome.ReconciliationRequired)
        {
            // The ledger entry first: the target names the event that requested it.
            await dbContext.SaveChangesAsync(cancellationToken);
            await ReconciliationTargets.RequestAsync(dbContext, tenant, result, parts.SourceScope, processedAt, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Decides a confirmed read of reconciliation (Gate 3E) through the same decision, and projects it exactly as a
    /// delivery is projected: only an accepted result's handoff reaches the Snapshot, in the caller's transaction.
    /// Reconciliation never writes the Snapshot itself (decision D3E-7).
    /// </summary>
    internal static async Task<EventProcessingResult> DecideAndProjectAsync(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        NormalizedSourceEvent parts,
        CorrelationId correlationId,
        TenantId tenant,
        DateTimeOffset processedAt,
        TimeSpan clockSkewTolerance,
        CancellationToken cancellationToken)
    {
        var result = await DecideNormalizedAsync(
            dbContext, receipt, parts, correlationId, tenant, processedAt, clockSkewTolerance, requestReconciliation: false, cancellationToken);
        if (result.TryGetProjection(out var projection))
        {
            await SnapshotProjection.ProjectAsync(dbContext, projection!, cancellationToken);
        }

        return result;
    }

    private static async Task<EventProcessingResult> DecideCanonicalAsync(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        NormalizedSourceEvent parts,
        CorrelationId correlationId,
        TenantId tenant,
        DateTimeOffset processedAt,
        TimeSpan clockSkewTolerance,
        CancellationToken cancellationToken)
    {
        var receiptId = receipt.ReceiptId;
        var eventId = EventIdentity.ForReceipt(receiptId);

        // Every trusted part comes from the receipt and the caller; the normalizer, or the reader, supplied only what the
        // source itself states.
        var created = CanonicalEvent.Create(
            new CanonicalEventMetadata(
                eventId,
                tenant,
                parts.Type,
                parts.SchemaVersion,
                new EventSource(EventSourceKind.SaasConnector, receipt.SourceSystem, receipt.ConnectionReference),
                parts.WorkObject,
                receipt.SourceEventId,
                parts.SourceRevision,
                parts.OccurredAt,
                parts.SourceUpdatedAt,
                receipt.ReceivedAtUtc,
                processedAt,
                correlationId,
                receiptId),
            parts.Content);
        if (created.IsFailure)
        {
            return QuarantineReceipt(
                dbContext, receipt, eventId, correlationId, processedAt, CanonicalEventErrors.QuarantineReasonFor(created.Error), created.Error);
        }

        var canonical = created.Value;
        var candidateOf = receipt.SourceEventId is null
            ? await FindDuplicateCandidateAsync(dbContext, receipt, cancellationToken)
            : null;
        var incoming = new OrderingEvidence(
            canonical.Metadata.SourceRevision,
            canonical.Metadata.SourceUpdatedAtUtc,
            canonical.Metadata.OccurredAtUtc,
            canonical.Metadata.SourceEventId);

        // Authority is decided per change, from code only; one unauthoritative change quarantines the event.
        var authority = SourceOfTruthResolver.ResolveChanges(canonical);
        if (authority.Any(decision => decision != AuthorityDecision.Authoritative))
        {
            return QuarantineEvent(dbContext, receipt, canonical, QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict, Withheld(canonical, incoming));
        }

        if (OrderingPolicy.ImpossibilityOf(incoming, canonical.Metadata.ReceivedAtUtc, clockSkewTolerance) is { } impossible)
        {
            return QuarantineEvent(dbContext, receipt, canonical, QuarantineReason.ImpossibleOrdering, impossible, Withheld(canonical, incoming));
        }

        // A change signal whose values the source has not confirmed never advances anything and never reaches the
        // Snapshot (invariant 13): the object is held for reconciliation, which reads the source's authoritative state
        // (invariant 17). Only an event that declares confirmed values is ordered at all (Gate 3D).
        if (parts.ValueConfirmation != SourceValueConfirmation.Confirmed)
        {
            return Record(
                dbContext,
                receipt,
                canonical,
                EventProcessingResult.ReconciliationRequired(canonical, receipt.ReceiptId, ProcessingErrors.UnconfirmedSourceValues.Code, Withheld(canonical, incoming), candidateOf),
                candidateOf);
        }

        if (!incoming.HasOrderingSignal)
        {
            return Record(
                dbContext,
                receipt,
                canonical,
                EventProcessingResult.ReconciliationRequired(canonical, receipt.ReceiptId, ProcessingErrors.NoOrderingEvidence.Code, Withheld(canonical, incoming), candidateOf),
                candidateOf);
        }

        var states = await LockOrderingStatesAsync(dbContext, tenant, canonical, cancellationToken);
        var decisions = canonical.Content.Changes
            .Select(change =>
            {
                states.TryGetValue(change.Field, out var state);
                return new FieldProcessingDecision(
                    change.Field,
                    change.DataType,
                    OrderingPolicy.Decide(incoming, state?.Evidence),
                    incoming,
                    state?.Evidence,
                    state is null ? null : CanonicalEventId.From(state.LastEventId));
            })
            .ToList();

        if (decisions.Any(decision => decision.Decision == FieldOrderingDecision.Impossible))
        {
            return QuarantineEvent(
                dbContext, receipt, canonical, QuarantineReason.ImpossibleOrdering, ProcessingErrors.RevisionClaimedTwice, Hold(decisions, FieldOrderingDecision.Impossible));
        }

        if (decisions.Any(decision => decision.Decision == FieldOrderingDecision.Ambiguous))
        {
            return Record(
                dbContext,
                receipt,
                canonical,
                EventProcessingResult.ReconciliationRequired(canonical, receipt.ReceiptId, ProcessingErrors.OrderingAmbiguous.Code, Hold(decisions, FieldOrderingDecision.Ambiguous), candidateOf),
                candidateOf);
        }

        foreach (var decision in decisions.Where(decision => decision.Decision == FieldOrderingDecision.Advance))
        {
            if (states.TryGetValue(decision.Field, out var state))
            {
                state.Advance(canonical.Metadata.EventId, incoming, processedAt);
            }
            else
            {
                dbContext.FieldOrderingStates.Add(FieldOrderingStateRecord.Accept(
                    tenant, canonical.Metadata.WorkObject, decision.Field, canonical.Metadata.EventId, incoming, processedAt));
            }
        }

        var result = decisions.Any(decision => decision.Decision == FieldOrderingDecision.Advance)
            ? EventProcessingResult.Accepted(canonical, receipt.ReceiptId, decisions, candidateOf)
            : EventProcessingResult.Stale(canonical, receipt.ReceiptId, decisions, candidateOf);
        return Record(dbContext, receipt, canonical, result, candidateOf);
    }

    // Fields not advanced because the event as a whole is held keep the decision that holds it; every other
    // field is withheld, except a stale one, which stays stale: that is a fact about its order either way.
    private static List<FieldProcessingDecision> Hold(IEnumerable<FieldProcessingDecision> decisions, FieldOrderingDecision holding) =>
        decisions
            .Select(decision => decision.Decision == holding || decision.Decision == FieldOrderingDecision.Stale
                ? decision
                : decision with { Decision = FieldOrderingDecision.Withheld })
            .ToList();

    private static List<FieldProcessingDecision> Withheld(CanonicalEvent canonical, OrderingEvidence incoming) =>
        canonical.Content.Changes
            .Select(change => new FieldProcessingDecision(change.Field, change.DataType, FieldOrderingDecision.Withheld, incoming, Previous: null, PreviousEventId: null))
            .ToList();

    private static async Task<Dictionary<string, FieldOrderingStateRecord>> LockOrderingStatesAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        CanonicalEvent canonical,
        CancellationToken cancellationToken)
    {
        var workObject = canonical.Metadata.WorkObject;
        var fields = canonical.Content.Changes.Select(change => change.Field).ToArray();
        var rows = await dbContext.FieldOrderingStates
            .FromSql($"""
                SELECT * FROM public.field_ordering_states
                WHERE tenant_id = {tenant.Value}
                  AND object_system = {workObject.SourceSystem}
                  AND object_connection = {workObject.ConnectionReference}
                  AND object_type = {workObject.ObjectType}
                  AND object_id = {workObject.SourceObjectId}
                  AND field = ANY ({fields})
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Field, StringComparer.Ordinal);
    }

    // A candidate only: identical content on the same connection without a provider event id may be a
    // redelivery or a genuinely new event with the same content, so it is flagged, never discarded.
    private static async Task<CanonicalEventId?> FindDuplicateCandidateAsync(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        CancellationToken cancellationToken)
    {
        var candidate = await (
                from other in dbContext.RawReceipts
                join entry in dbContext.EventLedger on other.ReceiptId equals entry.ReceiptId
                where other.ConnectionReference == receipt.ConnectionReference
                    && other.PayloadFingerprint == receipt.PayloadFingerprint
                    && other.SourceEventId == null
                    && other.ReceiptId != receipt.ReceiptId
                orderby entry.ProcessedAtUtc, entry.EventId
                select (Guid?)entry.EventId)
            .FirstOrDefaultAsync(cancellationToken);

        return candidate is { } eventId ? CanonicalEventId.From(eventId) : null;
    }

    private static EventProcessingResult Record(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        CanonicalEvent canonical,
        EventProcessingResult result,
        CanonicalEventId? candidateOf)
    {
        dbContext.EventLedger.Add(EventLedgerRecord.ForEvent(
            canonical,
            result.Outcome,
            result.Outcome == EventProcessingOutcome.Quarantined ? result.QuarantineReason : null,
            result.ReasonCode,
            candidateOf?.Value));
        AddFields(dbContext, canonical.Metadata.EventId, canonical.TenantId, result.Fields);

        if (result.Outcome == EventProcessingOutcome.Quarantined)
        {
            receipt.MarkQuarantined();
        }
        else
        {
            receipt.MarkProcessed();
        }

        return result;
    }

    private static EventProcessingResult QuarantineEvent(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        CanonicalEvent canonical,
        QuarantineReason reason,
        Error cause,
        IReadOnlyList<FieldProcessingDecision> fields) =>
        Record(
            dbContext,
            receipt,
            canonical,
            EventProcessingResult.Quarantined(canonical.Metadata.EventId, receipt.ReceiptId, reason, cause.Code, canonical.Metadata.WorkObject, fields),
            candidateOf: null);

    private static EventProcessingResult QuarantineReceipt(
        WorkContinuityDbContext dbContext,
        RawReceiptRecord receipt,
        CanonicalEventId eventId,
        CorrelationId correlationId,
        DateTimeOffset processedAt,
        QuarantineReason reason,
        Error cause)
    {
        dbContext.EventLedger.Add(EventLedgerRecord.ForUnnormalizedReceipt(
            eventId, receipt, EventSourceKind.SaasConnector, processedAt, correlationId, reason, cause.Code));
        receipt.MarkQuarantined();
        return EventProcessingResult.Quarantined(eventId, receipt.ReceiptId, reason, cause.Code, workObject: null, []);
    }

    private static void AddFields(
        WorkContinuityDbContext dbContext,
        CanonicalEventId eventId,
        TenantId tenant,
        IEnumerable<FieldProcessingDecision> fields)
    {
        foreach (var field in fields)
        {
            dbContext.EventLedgerFields.Add(EventLedgerFieldRecord.Record(eventId.Value, tenant, field.Field, field.DataType, field.Decision));
        }
    }

    private void Log(EventProcessingResult result, long? snapshotVersion, TenantId tenant, TransportEnvelope envelope)
    {
        switch (result.Outcome)
        {
            case EventProcessingOutcome.Duplicate:
                _logger.LogInformation(
                    "Receipt {ReceiptId} of tenant {TenantId} was already processed as event {EventId} ({PreviousOutcome}); nothing was done again (correlation {CorrelationId}).",
                    result.ReceiptId,
                    tenant,
                    result.EventId,
                    result.PreviousOutcome,
                    envelope.CorrelationId);
                break;

            case EventProcessingOutcome.Quarantined:
                _logger.LogWarning(
                    "Receipt {ReceiptId} of tenant {TenantId} was quarantined as event {EventId}: {QuarantineReason} ({ReasonCode}) (correlation {CorrelationId}).",
                    result.ReceiptId,
                    tenant,
                    result.EventId,
                    result.QuarantineReason,
                    result.ReasonCode,
                    envelope.CorrelationId);
                break;

            case EventProcessingOutcome.ReconciliationRequired:
                _logger.LogWarning(
                    "Receipt {ReceiptId} of tenant {TenantId} became event {EventId}, which needs reconciliation: {ReasonCode} (correlation {CorrelationId}).",
                    result.ReceiptId,
                    tenant,
                    result.EventId,
                    result.ReasonCode,
                    envelope.CorrelationId);
                break;

            case EventProcessingOutcome.AcceptedForProjection:
                _logger.LogInformation(
                    "Receipt {ReceiptId} of tenant {TenantId} became event {EventId}: {Outcome}, {AdvancedFields} of {Fields} fields advanced and projected at Snapshot object version {SnapshotVersion} (correlation {CorrelationId}).",
                    result.ReceiptId,
                    tenant,
                    result.EventId,
                    result.Outcome,
                    result.Fields.Count(field => field.Decision == FieldOrderingDecision.Advance),
                    result.Fields.Count,
                    snapshotVersion,
                    envelope.CorrelationId);
                break;

            default:
                _logger.LogInformation(
                    "Receipt {ReceiptId} of tenant {TenantId} became event {EventId}: {Outcome}, {AdvancedFields} of {Fields} fields advanced (correlation {CorrelationId}).",
                    result.ReceiptId,
                    tenant,
                    result.EventId,
                    result.Outcome,
                    result.Fields.Count(field => field.Decision == FieldOrderingDecision.Advance),
                    result.Fields.Count,
                    envelope.CorrelationId);
                break;
        }
    }

    /// <summary>
    /// What one transaction settled: an outcome, or none for a receipt whose content expired; the receipt's time,
    /// the lag to a fresh decision, and the projection of an accepted outcome.
    /// </summary>
    private sealed record Processed(EventProcessingResult? Result, DateTimeOffset ReceivedAtUtc, TimeSpan? Lag, Projected? Projection);

    /// <summary>A projection: the Snapshot object version it produced, the fields it wrote and how long it took.</summary>
    private sealed record Projected(long SnapshotVersion, int Fields, TimeSpan Duration);
}
