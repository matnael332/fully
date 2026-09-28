using WorkContinuity.Core.Events;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One work object in the Snapshot: its version and the accepted event that produced that version.
/// </summary>
/// <remarks>
/// <para>
/// The version counts the accepted events projected onto the object: 1 for the first, plus 1 for each later
/// one, however many fields it advanced. It comes only from accepted projections, never from a clock, so the
/// same sequence of accepted events always yields the same versions.
/// </para>
/// <para>
/// Projection locks this row for the length of its transaction, so concurrent events of one object are
/// projected one after the other and get distinct, gap-free versions.
/// </para>
/// </remarks>
public sealed class SnapshotObjectRecord : ITenantOwned
{
    private SnapshotObjectRecord()
    {
        // Materialization only.
    }

    private SnapshotObjectRecord(TenantId tenantId, ObjectReference workObject)
    {
        TenantId = tenantId;
        ObjectSystem = workObject.SourceSystem;
        ObjectConnection = workObject.ConnectionReference;
        ObjectType = workObject.ObjectType;
        ObjectId = workObject.SourceObjectId;
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

    /// <summary>Gets the number of accepted events projected onto the object.</summary>
    public long Version { get; private set; }

    /// <summary>Gets the accepted event that produced the current version.</summary>
    public Guid LastEventId { get; private set; }

    /// <summary>Gets when that event was processed.</summary>
    public DateTimeOffset ProcessedAtUtc { get; private set; }

    /// <summary>Records the object's first accepted event, at version 1.</summary>
    /// <exception cref="ArgumentException">A component is missing.</exception>
    public static SnapshotObjectRecord Create(
        TenantId tenantId,
        ObjectReference workObject,
        CanonicalEventId eventId,
        DateTimeOffset processedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(workObject);
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A Snapshot object requires a tenant.", nameof(tenantId));
        }

        var record = new SnapshotObjectRecord(tenantId, workObject);
        record.Advance(eventId, processedAtUtc);
        return record;
    }

    /// <summary>Records one more accepted event: exactly one new version.</summary>
    /// <exception cref="ArgumentException">The event is missing.</exception>
    /// <exception cref="InvalidOperationException">The event already produced the current version.</exception>
    public void Advance(CanonicalEventId eventId, DateTimeOffset processedAtUtc)
    {
        if (eventId.IsEmpty)
        {
            throw new ArgumentException("A Snapshot version names its event.", nameof(eventId));
        }

        // Idempotency is the ledger's, one entry per receipt; this refuses the one repeat that would still
        // reach here, the same event projected twice in a row, instead of counting it as a second version.
        if (eventId.Value == LastEventId)
        {
            throw new InvalidOperationException("An event is projected onto an object at most once.");
        }

        Version = checked(Version + 1);
        LastEventId = eventId.Value;
        ProcessedAtUtc = processedAtUtc.ToUniversalTime();
    }
}
