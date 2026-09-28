using WorkContinuity.Core.Events;

namespace WorkContinuity.Core.Processing;

/// <summary>What processing decided for one receipt.</summary>
public enum EventProcessingOutcome
{
    /// <summary>Unspecified. Never returned or recorded.</summary>
    None = 0,

    /// <summary>At least one field advanced: the handoff carries the event for projection.</summary>
    AcceptedForProjection = 1,

    /// <summary>The receipt was already processed; nothing was done again.</summary>
    Duplicate = 2,

    /// <summary>Every field is older than what is accepted; nothing advanced.</summary>
    Stale = 3,

    /// <summary>The order is ambiguous or unevidenced; nothing advanced and the object needs reconciliation.</summary>
    ReconciliationRequired = 4,

    /// <summary>The event is invalid, unauthoritative or impossibly ordered, and never reaches the Snapshot.</summary>
    Quarantined = 5,
}

/// <summary>One field's decision and the ordering evidence behind it.</summary>
/// <param name="Field">The field name token.</param>
/// <param name="DataType">The field's data type, which decided its authority.</param>
/// <param name="Decision">What processing decided for the field.</param>
/// <param name="Incoming">The event's ordering evidence.</param>
/// <param name="Previous">The last accepted change's evidence, or null when the field had none.</param>
/// <param name="PreviousEventId">The last accepted change's event, or null when the field had none.</param>
public sealed record FieldProcessingDecision(
    string Field,
    FactDataType DataType,
    FieldOrderingDecision Decision,
    OrderingEvidence Incoming,
    OrderingEvidence? Previous,
    CanonicalEventId? PreviousEventId);

/// <summary>
/// What Gate 2E may project: an accepted event and the fields it advanced, with their ordering evidence.
/// </summary>
/// <remarks>
/// The only way to obtain one is an accepted <see cref="EventProcessingResult"/>, so a quarantined, stale,
/// ambiguous or duplicate event can never reach projection. The event's content is work content that expires;
/// it is held here in memory only.
/// </remarks>
public sealed class ProjectionHandoff
{
    internal ProjectionHandoff(CanonicalEvent canonicalEvent, IReadOnlyList<FieldProcessingDecision> advancedFields)
    {
        Event = canonicalEvent;
        AdvancedFields = advancedFields;
    }

    /// <summary>Gets the accepted canonical event.</summary>
    public CanonicalEvent Event { get; }

    /// <summary>Gets the fields the event advanced; only these may be projected.</summary>
    public IReadOnlyList<FieldProcessingDecision> AdvancedFields { get; }
}

/// <summary>
/// The transport-independent result of processing one receipt: the outcome, its reason, the object and every
/// field's decision with its ordering evidence, and, only for an accepted event, the projection handoff.
/// </summary>
/// <remarks>
/// The result carries identifiers, codes and evidence only; the event's content is reachable solely through
/// <see cref="TryGetProjection"/> of an accepted result. The string form never includes it.
/// </remarks>
public sealed class EventProcessingResult
{
    private readonly ProjectionHandoff? _projection;

    private EventProcessingResult(
        EventProcessingOutcome outcome,
        CanonicalEventId eventId,
        Guid receiptId,
        EventProcessingOutcome previousOutcome,
        QuarantineReason quarantineReason,
        string? reasonCode,
        ObjectReference? workObject,
        IReadOnlyList<FieldProcessingDecision> fields,
        CanonicalEventId? duplicateCandidateOf,
        ProjectionHandoff? projection)
    {
        Outcome = outcome;
        EventId = eventId;
        ReceiptId = receiptId;
        PreviousOutcome = previousOutcome;
        QuarantineReason = quarantineReason;
        ReasonCode = reasonCode;
        WorkObject = workObject;
        Fields = fields;
        DuplicateCandidateOf = duplicateCandidateOf;
        _projection = projection;
    }

    /// <summary>Gets what processing decided.</summary>
    public EventProcessingOutcome Outcome { get; }

    /// <summary>Gets the canonical event id: the ledger's identity for the receipt.</summary>
    public CanonicalEventId EventId { get; }

    /// <summary>Gets the durable receipt the event was normalised from: its causation.</summary>
    public Guid ReceiptId { get; }

    /// <summary>Gets, for a duplicate, the outcome recorded when the receipt was first processed.</summary>
    public EventProcessingOutcome PreviousOutcome { get; }

    /// <summary>Gets the canonical quarantine reason, or <see cref="QuarantineReason.None"/>.</summary>
    public QuarantineReason QuarantineReason { get; }

    /// <summary>Gets the stable reason code for a quarantine or a reconciliation, otherwise null.</summary>
    public string? ReasonCode { get; }

    /// <summary>Gets the normalised object, or null when the receipt never became a valid event.</summary>
    public ObjectReference? WorkObject { get; }

    /// <summary>Gets every changed field's decision with its ordering evidence.</summary>
    public IReadOnlyList<FieldProcessingDecision> Fields { get; }

    /// <summary>Gets an earlier event with the same payload fingerprint on the same connection, if any.</summary>
    /// <remarks>A candidate only: without a provider event id, identical content does not prove identity.</remarks>
    public CanonicalEventId? DuplicateCandidateOf { get; }

    /// <summary>An accepted event, handed to projection with the fields it advanced.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    /// <exception cref="ArgumentException">No field advanced, or a decision names a field the event does not change.</exception>
    public static EventProcessingResult Accepted(
        CanonicalEvent canonicalEvent,
        Guid receiptId,
        IReadOnlyList<FieldProcessingDecision> fields,
        CanonicalEventId? duplicateCandidateOf)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        ArgumentNullException.ThrowIfNull(fields);

        var changed = canonicalEvent.Content.Changes.Select(change => change.Field).ToHashSet(StringComparer.Ordinal);
        if (fields.Any(field => !changed.Contains(field.Field)))
        {
            throw new ArgumentException("Every decision must name a field the event changes.", nameof(fields));
        }

        var advanced = fields.Where(field => field.Decision == FieldOrderingDecision.Advance).ToArray();
        if (advanced.Length == 0)
        {
            throw new ArgumentException("An accepted event advances at least one field.", nameof(fields));
        }

        if (fields.Any(field => field.Decision is not (FieldOrderingDecision.Advance or FieldOrderingDecision.Stale)))
        {
            throw new ArgumentException("An accepted event has only advanced and stale fields.", nameof(fields));
        }

        return new EventProcessingResult(
            EventProcessingOutcome.AcceptedForProjection,
            canonicalEvent.Metadata.EventId,
            receiptId,
            EventProcessingOutcome.None,
            QuarantineReason.None,
            reasonCode: null,
            canonicalEvent.Metadata.WorkObject,
            fields,
            duplicateCandidateOf,
            new ProjectionHandoff(canonicalEvent, Array.AsReadOnly(advanced)));
    }

    /// <summary>A receipt that was already processed: a deterministic no-op.</summary>
    public static EventProcessingResult Duplicate(CanonicalEventId eventId, Guid receiptId, EventProcessingOutcome previousOutcome) =>
        new(EventProcessingOutcome.Duplicate, eventId, receiptId, previousOutcome, QuarantineReason.None, reasonCode: null, workObject: null, [], duplicateCandidateOf: null, projection: null);

    /// <summary>An event older than what is accepted for every field it changes.</summary>
    public static EventProcessingResult Stale(
        CanonicalEvent canonicalEvent,
        Guid receiptId,
        IReadOnlyList<FieldProcessingDecision> fields,
        CanonicalEventId? duplicateCandidateOf)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        ArgumentNullException.ThrowIfNull(fields);
        return new EventProcessingResult(
            EventProcessingOutcome.Stale,
            canonicalEvent.Metadata.EventId,
            receiptId,
            EventProcessingOutcome.None,
            QuarantineReason.None,
            reasonCode: null,
            canonicalEvent.Metadata.WorkObject,
            fields,
            duplicateCandidateOf,
            projection: null);
    }

    /// <summary>An event whose order cannot be decided: nothing advanced and the object needs reconciliation.</summary>
    public static EventProcessingResult ReconciliationRequired(
        CanonicalEvent canonicalEvent,
        Guid receiptId,
        string reasonCode,
        IReadOnlyList<FieldProcessingDecision> fields,
        CanonicalEventId? duplicateCandidateOf)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(fields);
        return new EventProcessingResult(
            EventProcessingOutcome.ReconciliationRequired,
            canonicalEvent.Metadata.EventId,
            receiptId,
            EventProcessingOutcome.None,
            QuarantineReason.None,
            reasonCode,
            canonicalEvent.Metadata.WorkObject,
            fields,
            duplicateCandidateOf,
            projection: null);
    }

    /// <summary>An event that never reaches the Snapshot, with its canonical reason.</summary>
    /// <exception cref="ArgumentException">The reason is unspecified or the reason code is blank.</exception>
    public static EventProcessingResult Quarantined(
        CanonicalEventId eventId,
        Guid receiptId,
        QuarantineReason reason,
        string reasonCode,
        ObjectReference? workObject,
        IReadOnlyList<FieldProcessingDecision> fields)
    {
        if (reason == QuarantineReason.None || !Enum.IsDefined(reason))
        {
            throw new ArgumentException("A quarantine needs a canonical reason.", nameof(reason));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(fields);
        return new EventProcessingResult(
            EventProcessingOutcome.Quarantined, eventId, receiptId, EventProcessingOutcome.None, reason, reasonCode, workObject, fields, duplicateCandidateOf: null, projection: null);
    }

    /// <summary>Gets the projection handoff; only an accepted result has one.</summary>
    public bool TryGetProjection(out ProjectionHandoff? projection)
    {
        projection = _projection;
        return projection is not null;
    }

    /// <summary>Identifies the result by ids and outcome only.</summary>
    public override string ToString() => $"EventProcessingResult({EventId}, {Outcome})";
}
