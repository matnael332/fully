using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Processing;

/// <summary>
/// One durable receipt as a source normalizer sees it: the raw payload and what the source said about it.
/// </summary>
/// <remarks>
/// The payload is untrusted data (invariant 25). There is deliberately no tenant: a normalizer can neither
/// read nor choose the tenant, which the pipeline takes from the receipt under row-level security. The
/// payload never appears in the string form, so logging a payload object cannot leak it.
/// </remarks>
public sealed class SourcePayload
{
    /// <summary>Initializes a new instance of the <see cref="SourcePayload"/> class.</summary>
    /// <exception cref="ArgumentNullException">A required component is null.</exception>
    public SourcePayload(
        string sourceSystem,
        string connectionReference,
        string schemaVersion,
        string? sourceEventId,
        string payload)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(connectionReference);
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(payload);

        SourceSystem = sourceSystem;
        ConnectionReference = connectionReference;
        SchemaVersion = schemaVersion;
        SourceEventId = sourceEventId;
        Payload = payload;
    }

    /// <summary>Gets the source system token the receipt arrived from.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the connection the receipt arrived through.</summary>
    public string ConnectionReference { get; }

    /// <summary>Gets the source's payload schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the provider's event id, when the source supplied one.</summary>
    public string? SourceEventId { get; }

    /// <summary>Gets the raw payload as received. Untrusted data.</summary>
    public string Payload { get; }

    /// <summary>Identifies the payload without its content.</summary>
    public override string ToString() => $"SourcePayload({SourceSystem}, schema {SchemaVersion})";
}

/// <summary>Whether a normalized event's values are the source's state or only a signal that it changed.</summary>
/// <remarks>
/// Some deliveries prove who sent them but not the values they carry: a webhook body, for example, whose signature
/// covers the sender and not each field. Such an event tells that an object's fields changed, and its values are only
/// hints until an authoritative read of the source confirms them (Gate 3D). Only confirmed values are ever ordered or
/// projected; anything else holds the object for reconciliation (invariants 13 and 17).
/// </remarks>
public enum SourceValueConfirmation
{
    /// <summary>Unspecified. Never valid; treated as unconfirmed.</summary>
    Unspecified = 0,

    /// <summary>The values are the source's state as read from it, and may advance the Snapshot.</summary>
    Confirmed = 1,

    /// <summary>
    /// A change signal: the values are untrusted hints, nothing advances, and the object is held until an authoritative
    /// read of the source confirms its state.
    /// </summary>
    Unconfirmed = 2,
}

/// <summary>The source-derived parts of a canonical event, as a normalizer extracted them.</summary>
/// <remarks>
/// Only what the source itself states: the canonical type and schema version, the work object, the provider's
/// revision and source times, the content, and whether those values are confirmed. The tenant, the event source, the
/// provider event id, the receipt time, the correlation and the causation are absent: the pipeline sets them from the
/// receipt and the envelope, so a normalizer cannot assert any of them.
/// </remarks>
public sealed class NormalizedSourceEvent
{
    /// <summary>Initializes a new instance of the <see cref="NormalizedSourceEvent"/> class.</summary>
    /// <exception cref="ArgumentNullException">A required component is null.</exception>
    public NormalizedSourceEvent(
        string type,
        string schemaVersion,
        ObjectReference workObject,
        long? sourceRevision,
        DateTimeOffset? occurredAt,
        DateTimeOffset? sourceUpdatedAt,
        CanonicalEventContent content,
        SourceValueConfirmation valueConfirmation)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(workObject);
        ArgumentNullException.ThrowIfNull(content);

        Type = type;
        SchemaVersion = schemaVersion;
        WorkObject = workObject;
        SourceRevision = sourceRevision;
        OccurredAt = occurredAt;
        SourceUpdatedAt = sourceUpdatedAt;
        Content = content;
        ValueConfirmation = valueConfirmation;
    }

    /// <summary>Gets the canonical event type.</summary>
    public string Type { get; }

    /// <summary>Gets the canonical schema version of the type.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the work object the change is about, identified by its home system.</summary>
    public ObjectReference WorkObject { get; }

    /// <summary>Gets the provider's revision or sequence for the object, when it supplied one.</summary>
    public long? SourceRevision { get; }

    /// <summary>Gets when the change occurred, when the source said.</summary>
    public DateTimeOffset? OccurredAt { get; }

    /// <summary>Gets the source's update time for the object, when the source said.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; }

    /// <summary>Gets the expiring work content: the changes, the actor and the reference.</summary>
    public CanonicalEventContent Content { get; }

    /// <summary>
    /// Gets whether the values are the source's confirmed state. Anything but <see cref="SourceValueConfirmation.Confirmed"/>
    /// is held for reconciliation and never reaches the Snapshot.
    /// </summary>
    public SourceValueConfirmation ValueConfirmation { get; }

    /// <summary>Identifies the event without its content.</summary>
    public override string ToString() => $"NormalizedSourceEvent({Type})";
}

/// <summary>
/// Maps one source system's raw payloads onto canonical events (architecture §2: an adapter "maps provider
/// objects/events to canonical domain objects/events").
/// </summary>
/// <remarks>
/// <para>
/// A normalizer is pure and deterministic: the same payload always yields the same result, so reprocessing a
/// receipt after a crash reaches the same decision. A payload it cannot map is a failed result with a stable,
/// content-free code, never an exception.
/// </para>
/// <para>
/// One normalizer serves one source system. Gate 2 registers none in production: the monday normalizer
/// belongs to its connector Gate (3D), and until one is registered every receipt of a source is quarantined as an
/// unknown schema version. A normalizer states whether its values are confirmed; one whose source delivers only change
/// signals declares them unconfirmed, and processing then holds the object for reconciliation.
/// </para>
/// </remarks>
public interface ISourceEventNormalizer
{
    /// <summary>Gets the source system token this normalizer serves.</summary>
    string SourceSystem { get; }

    /// <summary>Whether this normalizer understands the source's payload schema version.</summary>
    bool Supports(string schemaVersion);

    /// <summary>Extracts the source-derived parts of the canonical event from one payload.</summary>
    Result<NormalizedSourceEvent> Normalize(SourcePayload payload);
}
