using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Events;

/// <summary>
/// The retained half of a canonical event: identity, provenance, ordering and deduplication metadata.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is work content. Raw work-event content expires by the 30-day limit (PRD §12 and Gate 2
/// owner decision 5), while this metadata may outlive it because deduplication, ordering and Snapshot
/// provenance still need it after the content is gone. The content lives only in
/// <see cref="CanonicalEventContent"/>, so the two lifecycles never share a member.
/// </para>
/// <para>
/// A missing source time stays missing. Receipt time is never substituted for an occurrence or source
/// update time, because receipt time alone never determines business ordering (invariant 12). Every
/// instant is normalised to UTC.
/// </para>
/// <para>
/// Construction only guards against null; <see cref="CanonicalEvent.Create"/> decides validity.
/// </para>
/// </remarks>
public sealed class CanonicalEventMetadata
{
    /// <summary>Initializes a new instance of the <see cref="CanonicalEventMetadata"/> class.</summary>
    /// <exception cref="ArgumentNullException">A required reference component is null.</exception>
    public CanonicalEventMetadata(
        CanonicalEventId eventId,
        TenantId tenantId,
        string type,
        string schemaVersion,
        EventSource source,
        ObjectReference workObject,
        string? sourceEventId,
        long? sourceRevision,
        DateTimeOffset? occurredAt,
        DateTimeOffset? sourceUpdatedAt,
        DateTimeOffset receivedAt,
        DateTimeOffset? processedAt,
        CorrelationId correlationId,
        Guid? causationId)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workObject);

        EventId = eventId;
        TenantId = tenantId;
        Type = type;
        SchemaVersion = schemaVersion;
        Source = source;
        WorkObject = workObject;
        SourceEventId = sourceEventId;
        SourceRevision = sourceRevision;
        OccurredAtUtc = occurredAt?.ToUniversalTime();
        SourceUpdatedAtUtc = sourceUpdatedAt?.ToUniversalTime();
        ReceivedAtUtc = receivedAt.ToUniversalTime();
        ProcessedAtUtc = processedAt?.ToUniversalTime();
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    /// <summary>Gets the canonical event id (CloudEvents <c>id</c>).</summary>
    public CanonicalEventId EventId { get; }

    /// <summary>Gets the owning tenant. Every event belongs to a tenant (invariant 1).</summary>
    public TenantId TenantId { get; }

    /// <summary>Gets the canonical event type (CloudEvents <c>type</c>).</summary>
    public string Type { get; }

    /// <summary>Gets the schema version of the event type.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets where the event came from (CloudEvents <c>source</c>).</summary>
    public EventSource Source { get; }

    /// <summary>Gets the work object the event is about (CloudEvents <c>subject</c>).</summary>
    public ObjectReference WorkObject { get; }

    /// <summary>
    /// Gets the provider's event id when the source supplies one. Deduplication prefers it within a
    /// tenant and connection.
    /// </summary>
    public string? SourceEventId { get; }

    /// <summary>
    /// Gets the provider's revision or sequence for the object when the source supplies one. Ordering
    /// prefers it over any timestamp.
    /// </summary>
    public long? SourceRevision { get; }

    /// <summary>Gets when the change occurred, if the source said (CloudEvents <c>time</c>).</summary>
    public DateTimeOffset? OccurredAtUtc { get; }

    /// <summary>Gets the authoritative source's update time for the object, if the source said.</summary>
    public DateTimeOffset? SourceUpdatedAtUtc { get; }

    /// <summary>Gets when the product durably received the underlying payload.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>Gets when the product processed the event, once it has.</summary>
    public DateTimeOffset? ProcessedAtUtc { get; }

    /// <summary>Gets the correlation id tying the event to its wider operation.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>
    /// Gets the id of what caused this event, for example the durable receipt it was normalised from.
    /// </summary>
    public Guid? CausationId { get; }
}
