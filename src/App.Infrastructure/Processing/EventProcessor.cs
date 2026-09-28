using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.SourceOfTruth;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

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
/// Hygiene: logs carry ids, outcomes and reason codes only, never the payload or a value.
/// </para>
/// </remarks>
internal sealed class EventProcessor : IEventProcessor
{
    private readonly ITenantScopedDatabase _database;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IReadOnlyDictionary<string, ISourceEventNormalizer> _normalizers;
    private readonly IClock _clock;
    private readonly EventProcessingOptions _options;
    private readonly ILogger<EventProcessor> _logger;

    /// <exception cref="InvalidOperationException">Two normalizers serve one source system.</exception>
    public EventProcessor(
        ITenantScopedDatabase database,
        ITenantContextAccessor tenantContextAccessor,
        IEnumerable<ISourceEventNormalizer> normalizers,
        IClock clock,
        EventProcessingOptions options,
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
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EventProcessingResult>> ProcessAsync(
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_tenantContextAccessor.Current.TryGetTenantId(out var tenant) || tenant != envelope.TenantId)
        {
            _logger.LogError(
                "Processing refused message {MessageId}: {ReasonCode} (correlation {CorrelationId}).",
                envelope.MessageId,
                ProcessingErrors.TenantMismatch.Code,
                envelope.CorrelationId);
            return Result<EventProcessingResult>.Failure(ProcessingErrors.TenantMismatch);
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
            return Result<EventProcessingResult>.Failure(ProcessingErrors.Unavailable);
        }

        if (processed.Value is not { } result)
        {
            _logger.LogError(
                "Processing found no receipt {ReceiptId} in tenant {TenantId}: {ReasonCode}; the message cannot be processed (correlation {CorrelationId}).",
                envelope.CausationId,
                tenant,
                ProcessingErrors.ReceiptNotVisible.Code,
                envelope.CorrelationId);
            return Result<EventProcessingResult>.Failure(ProcessingErrors.ReceiptNotVisible);
        }

        Log(result, tenant, envelope);
        return Result<EventProcessingResult>.Success(result);
    }

    private async Task<EventProcessingResult?> ProcessReceiptAsync(
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

        var recorded = await dbContext.EventLedger
            .AsNoTracking()
            .Where(entry => entry.ReceiptId == receiptId)
            .Select(entry => new { entry.EventId, entry.Outcome })
            .SingleOrDefaultAsync(cancellationToken);
        if (recorded is not null)
        {
            return EventProcessingResult.Duplicate(CanonicalEventId.From(recorded.EventId), receiptId, recorded.Outcome);
        }

        var eventId = EventIdentity.ForReceipt(receiptId);
        var processedAt = _clock.UtcNow;

        if (!_normalizers.TryGetValue(receipt.SourceSystem, out var normalizer) || !normalizer.Supports(receipt.SchemaVersion))
        {
            return QuarantineReceipt(dbContext, receipt, eventId, envelope, processedAt, QuarantineReason.UnknownSchemaVersion, ProcessingErrors.NormalizerMissing);
        }

        Result<NormalizedSourceEvent> normalized;
        try
        {
            normalized = normalizer.Normalize(new SourcePayload(
                receipt.SourceSystem, receipt.ConnectionReference, receipt.SchemaVersion, receipt.SourceEventId, receipt.Payload));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type is logged: a normalizer's exception message could quote the payload.
            _logger.LogWarning(
                "The {SourceSystem} normalizer failed with {ExceptionType} on receipt {ReceiptId}.",
                receipt.SourceSystem,
                exception.GetType().Name,
                receiptId);
            return QuarantineReceipt(dbContext, receipt, eventId, envelope, processedAt, QuarantineReason.BadSchema, ProcessingErrors.NormalizerFailed);
        }

        if (normalized.IsFailure)
        {
            return QuarantineReceipt(dbContext, receipt, eventId, envelope, processedAt, QuarantineReason.BadSchema, ProcessingErrors.NormalizerRejected);
        }

        // Every trusted part comes from the receipt and the envelope; the normalizer supplied only what the
        // source itself states.
        var parts = normalized.Value;
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
                envelope.CorrelationId,
                receiptId),
            parts.Content);
        if (created.IsFailure)
        {
            return QuarantineReceipt(
                dbContext, receipt, eventId, envelope, processedAt, CanonicalEventErrors.QuarantineReasonFor(created.Error), created.Error);
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

        if (OrderingPolicy.ImpossibilityOf(incoming, canonical.Metadata.ReceivedAtUtc, _options.ClockSkewTolerance) is { } impossible)
        {
            return QuarantineEvent(dbContext, receipt, canonical, QuarantineReason.ImpossibleOrdering, impossible, Withheld(canonical, incoming));
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
        TransportEnvelope envelope,
        DateTimeOffset processedAt,
        QuarantineReason reason,
        Error cause)
    {
        dbContext.EventLedger.Add(EventLedgerRecord.ForUnnormalizedReceipt(
            eventId, receipt, EventSourceKind.SaasConnector, processedAt, envelope.CorrelationId, reason, cause.Code));
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

    private void Log(EventProcessingResult result, TenantId tenant, TransportEnvelope envelope)
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
}
