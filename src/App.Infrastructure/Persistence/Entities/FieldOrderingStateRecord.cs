using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// The ordering evidence of the last accepted change to one field of one work object: which event, and the
/// provider revision and source times it carried.
/// </summary>
/// <remarks>
/// <para>
/// This is ordering state, not Snapshot state: it holds no value. It is what lets a delayed older event be
/// recognised as older after the fact, across crashes and restarts, so it never overwrites newer state
/// (architecture §3 ordering, invariant 18).
/// </para>
/// <para>
/// One row per tenant, object and field. Processing locks the rows it compares against for the length of its
/// transaction, so two events of one object can never both advance a field from the same evidence.
/// </para>
/// </remarks>
public sealed class FieldOrderingStateRecord : ITenantOwned
{
    private FieldOrderingStateRecord()
    {
        // Materialization only.
    }

    private FieldOrderingStateRecord(TenantId tenantId, ObjectReference workObject, string field)
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

    /// <summary>Gets the last accepted event for the field.</summary>
    public Guid LastEventId { get; private set; }

    /// <summary>Gets that event's provider revision, if any.</summary>
    public long? SourceRevision { get; private set; }

    /// <summary>Gets that event's source update time, if any.</summary>
    public DateTimeOffset? SourceUpdatedAtUtc { get; private set; }

    /// <summary>Gets that event's occurrence time, if any.</summary>
    public DateTimeOffset? OccurredAtUtc { get; private set; }

    /// <summary>Gets that event's provider event id, if any.</summary>
    public string? SourceEventId { get; private set; }

    /// <summary>Gets when the change was accepted.</summary>
    public DateTimeOffset AcceptedAtUtc { get; private set; }

    /// <summary>Gets the accepted change's ordering evidence.</summary>
    public OrderingEvidence Evidence => new(SourceRevision, SourceUpdatedAtUtc, OccurredAtUtc, SourceEventId);

    /// <summary>Records the first accepted change of a field.</summary>
    /// <exception cref="ArgumentException">A component is missing.</exception>
    public static FieldOrderingStateRecord Accept(
        TenantId tenantId,
        ObjectReference workObject,
        string field,
        CanonicalEventId eventId,
        OrderingEvidence evidence,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(workObject);
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("Ordering state requires a tenant.", nameof(tenantId));
        }

        if (!EventText.IsToken(field))
        {
            throw new ArgumentException("A field name must be a token.", nameof(field));
        }

        var state = new FieldOrderingStateRecord(tenantId, workObject, field);
        state.Advance(eventId, evidence, acceptedAtUtc);
        return state;
    }

    /// <summary>Records a newer accepted change of the field.</summary>
    /// <exception cref="ArgumentException">The event or the evidence is missing.</exception>
    public void Advance(CanonicalEventId eventId, OrderingEvidence evidence, DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (eventId.IsEmpty || !evidence.HasOrderingSignal)
        {
            throw new ArgumentException("An accepted change names its event and carries ordering evidence.", nameof(evidence));
        }

        LastEventId = eventId.Value;
        SourceRevision = evidence.SourceRevision;
        SourceUpdatedAtUtc = evidence.SourceUpdatedAtUtc;
        OccurredAtUtc = evidence.OccurredAtUtc;
        SourceEventId = evidence.SourceEventId;
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
    }
}
