using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.SourceOfTruth;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One field of a work object in the Snapshot: its current fact value and the provenance that makes it a fact.
/// </summary>
/// <remarks>
/// <para>
/// The provenance answers, for the current value, which authority rule made its source authoritative, which
/// source and object produced it, which canonical event it came from, when the source said it changed, when
/// the product observed and processed it, and the ordering evidence it was accepted on (architecture §3,
/// invariant 14). Freshness is derived from those times; no threshold is stored.
/// </para>
/// <para>
/// The value is the source's canonical text exactly as the accepted event stated it, or null when the source
/// cleared the field. Nothing else of the event is kept: no before value, actor, reference or payload.
/// </para>
/// <para>
/// The field version counts the accepted changes projected onto the field, a change that repeats the current
/// value with newer evidence included; the object version is the object's version at which the field last
/// changed. Only an accepted event's advanced field is ever applied, so neither moves otherwise.
/// </para>
/// </remarks>
public sealed class SnapshotFieldRecord : ITenantOwned
{
    private SnapshotFieldRecord()
    {
        // Materialization only.
    }

    private SnapshotFieldRecord(TenantId tenantId, ObjectReference workObject, string field)
    {
        TenantId = tenantId;
        ObjectSystem = workObject.SourceSystem;
        ObjectConnection = workObject.ConnectionReference;
        ObjectType = workObject.ObjectType;
        ObjectId = workObject.SourceObjectId;
        Field = field;
    }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the object's home system.</summary>
    public string ObjectSystem { get; private set; } = string.Empty;

    /// <summary>Gets the object's home connection.</summary>
    public string ObjectConnection { get; private set; } = string.Empty;

    /// <summary>Gets the object type token.</summary>
    public string ObjectType { get; private set; } = string.Empty;

    /// <summary>Gets the provider's object id.</summary>
    public string ObjectId { get; private set; } = string.Empty;

    /// <summary>Gets the field name token.</summary>
    public string Field { get; private set; } = string.Empty;

    /// <summary>Gets the field's data type, which decided its authority.</summary>
    public FactDataType DataType { get; private set; }

    /// <summary>Gets the current fact value, or null when the source cleared the field.</summary>
    public string? Value { get; private set; }

    /// <summary>Gets the authority rule (PRD §5) under which the source was authoritative for the value.</summary>
    public AuthorityBasis Authority { get; private set; }

    /// <summary>Gets the kind of source that stated the value.</summary>
    public EventSourceKind SourceKind { get; private set; }

    /// <summary>Gets the source system that stated the value.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the connection the value arrived through.</summary>
    public string ConnectionReference { get; private set; } = string.Empty;

    /// <summary>Gets the canonical event that produced the value.</summary>
    public Guid LastEventId { get; private set; }

    /// <summary>Gets that event's provider event id, if any.</summary>
    public string? SourceEventId { get; private set; }

    /// <summary>Gets that event's provider revision, if any.</summary>
    public long? SourceRevision { get; private set; }

    /// <summary>Gets when the source said the object was updated, if it did.</summary>
    public DateTimeOffset? SourceUpdatedAtUtc { get; private set; }

    /// <summary>Gets when the source said the change occurred, if it did.</summary>
    public DateTimeOffset? OccurredAtUtc { get; private set; }

    /// <summary>Gets when the product durably received the change.</summary>
    public DateTimeOffset ObservedAtUtc { get; private set; }

    /// <summary>Gets when the product processed the change.</summary>
    public DateTimeOffset ProcessedAtUtc { get; private set; }

    /// <summary>Gets the number of accepted changes projected onto the field.</summary>
    public long FieldVersion { get; private set; }

    /// <summary>Gets the object's version at which the field last changed.</summary>
    public long ObjectVersion { get; private set; }

    /// <summary>Records the first accepted change of a field.</summary>
    /// <exception cref="ArgumentException">The change, its evidence or its versions do not fit together.</exception>
    public static SnapshotFieldRecord Create(
        CanonicalEvent canonicalEvent,
        FieldChange change,
        OrderingEvidence evidence,
        long objectVersion)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        ArgumentNullException.ThrowIfNull(change);

        var record = new SnapshotFieldRecord(canonicalEvent.TenantId, canonicalEvent.Metadata.WorkObject, change.Field);
        record.Apply(canonicalEvent, change, evidence, objectVersion);
        return record;
    }

    /// <summary>Records a newer accepted change of the field: its value and all of its provenance.</summary>
    /// <exception cref="ArgumentException">The change, its evidence or its versions do not fit together.</exception>
    /// <exception cref="InvalidOperationException">The event already produced the current value.</exception>
    public void Apply(CanonicalEvent canonicalEvent, FieldChange change, OrderingEvidence evidence, long objectVersion)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(evidence);

        var metadata = canonicalEvent.Metadata;
        var workObject = metadata.WorkObject;
        var sameField = canonicalEvent.TenantId == TenantId
            && string.Equals(workObject.SourceSystem, ObjectSystem, StringComparison.Ordinal)
            && string.Equals(workObject.ConnectionReference, ObjectConnection, StringComparison.Ordinal)
            && string.Equals(workObject.ObjectType, ObjectType, StringComparison.Ordinal)
            && string.Equals(workObject.SourceObjectId, ObjectId, StringComparison.Ordinal)
            && string.Equals(change.Field, Field, StringComparison.Ordinal)
            && canonicalEvent.Content.Changes.Contains(change);
        if (!sameField)
        {
            throw new ArgumentException("A change is applied only to its own tenant, object and field.", nameof(change));
        }

        // The ordering provenance is the evidence the change was accepted on, and it must be the event's own.
        var ownEvidence = evidence.SourceRevision == metadata.SourceRevision
            && evidence.SourceUpdatedAtUtc == metadata.SourceUpdatedAtUtc
            && evidence.OccurredAtUtc == metadata.OccurredAtUtc
            && string.Equals(evidence.SourceEventId, metadata.SourceEventId, StringComparison.Ordinal);
        if (!ownEvidence || !evidence.HasOrderingSignal)
        {
            throw new ArgumentException("A projected change carries its own event's ordering evidence.", nameof(evidence));
        }

        var authority = AuthorityTable.BasisFor(change.DataType);
        if (authority == AuthorityBasis.None || (change.After is not null && !EventText.IsValue(change.After)))
        {
            throw new ArgumentException("Only a well-formed value of a data type with an authority rule is a fact.", nameof(change));
        }

        if (objectVersion <= ObjectVersion)
        {
            throw new ArgumentException("A field changes only at a newer object version.", nameof(objectVersion));
        }

        if (metadata.EventId.Value == LastEventId)
        {
            throw new InvalidOperationException("An event is projected onto a field at most once.");
        }

        DataType = change.DataType;
        Value = change.After;
        Authority = authority;
        SourceKind = metadata.Source.Kind;
        SourceSystem = metadata.Source.SourceSystem;
        ConnectionReference = metadata.Source.ConnectionReference;
        LastEventId = metadata.EventId.Value;
        SourceEventId = evidence.SourceEventId;
        SourceRevision = evidence.SourceRevision;
        SourceUpdatedAtUtc = evidence.SourceUpdatedAtUtc?.ToUniversalTime();
        OccurredAtUtc = evidence.OccurredAtUtc?.ToUniversalTime();
        ObservedAtUtc = metadata.ReceivedAtUtc;
        ProcessedAtUtc = metadata.ProcessedAtUtc
            ?? throw new ArgumentException("A projected event has a processing time.", nameof(canonicalEvent));
        FieldVersion = checked(FieldVersion + 1);
        ObjectVersion = objectVersion;
    }
}
