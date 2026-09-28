using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One processed receipt in the durable event ledger: the canonical event's identity, provenance, ordering and
/// deduplication metadata, and what processing decided.
/// </summary>
/// <remarks>
/// <para>
/// The ledger outlives raw work content (Gate 2 owner decision 5). It therefore holds identifiers, tokens,
/// times, codes and the outcome only: no value, actor, reference, payload or fingerprint. Raw content stays in
/// <see cref="RawReceiptRecord"/> and expires with it.
/// </para>
/// <para>
/// One row per receipt (unique per tenant): it is the persistent idempotency key of processing. The provider
/// event id is unique per tenant and connection, as it is for receipts. A receipt that never became a valid
/// event is recorded with its receipt-level provenance only, and only as a quarantine.
/// </para>
/// <para>
/// The ledger is append-only: restrictive row-level security policies refuse every update and delete.
/// </para>
/// </remarks>
public sealed class EventLedgerRecord : ITenantOwned
{
    /// <summary>The maximum stored length of an identifier or a type.</summary>
    public const int MaxTextLength = 200;

    private EventLedgerRecord()
    {
        // Materialization only.
    }

    private EventLedgerRecord(
        Guid eventId,
        TenantId tenantId,
        Guid receiptId,
        EventSourceKind sourceKind,
        string sourceSystem,
        string connectionReference,
        string? sourceEventId,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset processedAtUtc,
        CorrelationId correlationId,
        EventProcessingOutcome outcome,
        QuarantineReason? quarantineReason,
        string? reasonCode)
    {
        EventId = eventId;
        TenantId = tenantId;
        ReceiptId = receiptId;
        SourceKind = sourceKind;
        SourceSystem = sourceSystem;
        ConnectionReference = connectionReference;
        SourceEventId = sourceEventId;
        ReceivedAtUtc = receivedAtUtc;
        ProcessedAtUtc = processedAtUtc;
        CorrelationId = correlationId;
        Outcome = outcome;
        QuarantineReason = quarantineReason;
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the canonical event id, derived from the receipt.</summary>
    public Guid EventId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the receipt the event was normalised from: its causation, and the idempotency key.</summary>
    public Guid ReceiptId { get; private set; }

    /// <summary>Gets the kind of source the event came from.</summary>
    public EventSourceKind SourceKind { get; private set; }

    /// <summary>Gets the source system token.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the connection the event arrived through.</summary>
    public string ConnectionReference { get; private set; } = string.Empty;

    /// <summary>Gets the provider's event id, when the source supplied one.</summary>
    public string? SourceEventId { get; private set; }

    /// <summary>Gets the canonical event type, or null when the receipt never became a valid event.</summary>
    public string? EventType { get; private set; }

    /// <summary>Gets the canonical schema version, or null when the receipt never became a valid event.</summary>
    public string? SchemaVersion { get; private set; }

    /// <summary>Gets the object's home system, or null when the receipt never became a valid event.</summary>
    public string? ObjectSystem { get; private set; }

    /// <summary>Gets the object's home connection, or null when the receipt never became a valid event.</summary>
    public string? ObjectConnection { get; private set; }

    /// <summary>Gets the object type token, or null when the receipt never became a valid event.</summary>
    public string? ObjectType { get; private set; }

    /// <summary>Gets the provider's object id, or null when the receipt never became a valid event.</summary>
    public string? ObjectId { get; private set; }

    /// <summary>Gets the provider's revision, if any.</summary>
    public long? SourceRevision { get; private set; }

    /// <summary>Gets the source's update time for the object, if any.</summary>
    public DateTimeOffset? SourceUpdatedAtUtc { get; private set; }

    /// <summary>Gets when the change occurred, if the source said.</summary>
    public DateTimeOffset? OccurredAtUtc { get; private set; }

    /// <summary>Gets when the receipt was durably received.</summary>
    public DateTimeOffset ReceivedAtUtc { get; private set; }

    /// <summary>Gets when processing decided.</summary>
    public DateTimeOffset ProcessedAtUtc { get; private set; }

    /// <summary>Gets the correlation id of the operation, from ingress onwards.</summary>
    public CorrelationId CorrelationId { get; private set; }

    /// <summary>Gets what processing decided. Never <see cref="EventProcessingOutcome.Duplicate"/>.</summary>
    public EventProcessingOutcome Outcome { get; private set; }

    /// <summary>Gets the canonical quarantine reason; present exactly when the event is quarantined.</summary>
    public QuarantineReason? QuarantineReason { get; private set; }

    /// <summary>Gets the stable reason code of a quarantine or a reconciliation, otherwise null.</summary>
    public string? ReasonCode { get; private set; }

    /// <summary>Gets an earlier event with the same payload fingerprint on the same connection, if any.</summary>
    public Guid? DuplicateCandidateOf { get; private set; }

    /// <summary>Records a valid canonical event and its outcome.</summary>
    /// <exception cref="ArgumentException">The outcome, reason and code do not fit together.</exception>
    public static EventLedgerRecord ForEvent(
        CanonicalEvent canonicalEvent,
        EventProcessingOutcome outcome,
        QuarantineReason? quarantineReason,
        string? reasonCode,
        Guid? duplicateCandidateOf)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        RequireConsistent(outcome, quarantineReason, reasonCode);

        var metadata = canonicalEvent.Metadata;
        return new EventLedgerRecord(
            metadata.EventId.Value,
            metadata.TenantId,
            metadata.CausationId ?? throw new ArgumentException("A canonical event from a receipt names its receipt.", nameof(canonicalEvent)),
            metadata.Source.Kind,
            metadata.Source.SourceSystem,
            metadata.Source.ConnectionReference,
            metadata.SourceEventId,
            metadata.ReceivedAtUtc,
            metadata.ProcessedAtUtc ?? throw new ArgumentException("A processed event has a processing time.", nameof(canonicalEvent)),
            metadata.CorrelationId,
            outcome,
            quarantineReason,
            reasonCode)
        {
            EventType = metadata.Type,
            SchemaVersion = metadata.SchemaVersion,
            ObjectSystem = metadata.WorkObject.SourceSystem,
            ObjectConnection = metadata.WorkObject.ConnectionReference,
            ObjectType = metadata.WorkObject.ObjectType,
            ObjectId = metadata.WorkObject.SourceObjectId,
            SourceRevision = metadata.SourceRevision,
            SourceUpdatedAtUtc = metadata.SourceUpdatedAtUtc,
            OccurredAtUtc = metadata.OccurredAtUtc,
            DuplicateCandidateOf = duplicateCandidateOf,
        };
    }

    /// <summary>
    /// Records a receipt that never became a valid canonical event. Only a quarantine can be recorded this way,
    /// and only with the receipt's own provenance.
    /// </summary>
    /// <exception cref="ArgumentException">A component is missing, or the reason is not a quarantine reason.</exception>
    public static EventLedgerRecord ForUnnormalizedReceipt(
        CanonicalEventId eventId,
        RawReceiptRecord receipt,
        EventSourceKind sourceKind,
        DateTimeOffset processedAtUtc,
        CorrelationId correlationId,
        QuarantineReason quarantineReason,
        string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (eventId.IsEmpty)
        {
            throw new ArgumentException("A ledger entry requires an event id.", nameof(eventId));
        }

        if (correlationId.IsEmpty)
        {
            throw new ArgumentException("A ledger entry requires a correlation id.", nameof(correlationId));
        }

        RequireConsistent(EventProcessingOutcome.Quarantined, quarantineReason, reasonCode);
        return new EventLedgerRecord(
            eventId.Value,
            receipt.TenantId,
            receipt.ReceiptId,
            sourceKind,
            receipt.SourceSystem,
            receipt.ConnectionReference,
            receipt.SourceEventId,
            receipt.ReceivedAtUtc,
            processedAtUtc.ToUniversalTime(),
            correlationId,
            EventProcessingOutcome.Quarantined,
            quarantineReason,
            reasonCode);
    }

    private static void RequireConsistent(EventProcessingOutcome outcome, QuarantineReason? quarantineReason, string? reasonCode)
    {
        if (outcome is not (EventProcessingOutcome.AcceptedForProjection or EventProcessingOutcome.Stale
            or EventProcessingOutcome.ReconciliationRequired or EventProcessingOutcome.Quarantined))
        {
            throw new ArgumentException("Only a settled processing outcome is recorded.", nameof(outcome));
        }

        var reasonIsCanonical = quarantineReason is { } reason
            && reason != Core.Events.QuarantineReason.None
            && Enum.IsDefined(reason);
        if (outcome == EventProcessingOutcome.Quarantined ? !reasonIsCanonical : quarantineReason is not null)
        {
            throw new ArgumentException("A quarantine, and only a quarantine, carries a canonical reason.", nameof(quarantineReason));
        }

        var explained = outcome is EventProcessingOutcome.Quarantined or EventProcessingOutcome.ReconciliationRequired;
        if (explained == string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException("A quarantine or a reconciliation, and only those, carries a reason code.", nameof(reasonCode));
        }
    }
}
