using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.Processing;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Processing;

/// <summary>
/// Projects an accepted processing result onto the Snapshot (architecture §3: snapshot projection).
/// </summary>
/// <remarks>
/// <para>
/// It runs inside the processing transaction, after 2D has decided and before anything commits, so the ledger
/// entry, the ordering state and the Snapshot commit together or not at all. Its only input is the projection
/// handoff, which only an accepted result can produce: a duplicate, stale, ambiguous, reconciliation-required
/// or quarantined result never reaches it. It re-decides neither authority nor ordering; it applies the fields
/// 2D advanced, with 2D's evidence, and nothing else.
/// </para>
/// <para>
/// The object row is locked first, so concurrent events of one object are projected one after the other and
/// each accepted event takes exactly one new object version. The field rows need no lock of their own: 2D
/// holds the ordering-state row of every field it advanced until the commit, and every write to a field row
/// comes through here, after that lock.
/// </para>
/// </remarks>
internal static class SnapshotProjection
{
    /// <summary>Projects the advanced fields of an accepted event and returns the object's new version.</summary>
    /// <exception cref="InvalidOperationException">
    /// The Snapshot disagrees with the ordering state the event was decided against, so nothing may be overwritten.
    /// </exception>
    public static async Task<long> ProjectAsync(
        WorkContinuityDbContext dbContext,
        ProjectionHandoff projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(projection);

        var canonical = projection.Event;
        var metadata = canonical.Metadata;
        var workObject = metadata.WorkObject;
        var tenant = canonical.TenantId;
        var processedAt = metadata.ProcessedAtUtc
            ?? throw new InvalidOperationException("A projected event has a processing time.");

        var snapshotObject = (await dbContext.SnapshotObjects
            .FromSql($"""
                SELECT * FROM public.snapshot_objects
                WHERE tenant_id = {tenant.Value}
                  AND object_system = {workObject.SourceSystem}
                  AND object_connection = {workObject.ConnectionReference}
                  AND object_type = {workObject.ObjectType}
                  AND object_id = {workObject.SourceObjectId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (snapshotObject is null)
        {
            snapshotObject = SnapshotObjectRecord.Create(tenant, workObject, metadata.EventId, processedAt);
            dbContext.SnapshotObjects.Add(snapshotObject);
        }
        else
        {
            snapshotObject.Advance(metadata.EventId, processedAt);
        }

        var fields = projection.AdvancedFields.Select(field => field.Field).ToArray();
        var current = await dbContext.SnapshotFields
            .Where(row => row.TenantId == tenant
                && row.ObjectSystem == workObject.SourceSystem
                && row.ObjectConnection == workObject.ConnectionReference
                && row.ObjectType == workObject.ObjectType
                && row.ObjectId == workObject.SourceObjectId
                && fields.Contains(row.Field))
            .ToDictionaryAsync(row => row.Field, StringComparer.Ordinal, cancellationToken);

        foreach (var advanced in projection.AdvancedFields)
        {
            var change = canonical.Content.Changes.Single(candidate => string.Equals(candidate.Field, advanced.Field, StringComparison.Ordinal));
            if (current.TryGetValue(advanced.Field, out var row))
            {
                // The Snapshot must hold exactly the change 2D compared against; a divergent Snapshot is never
                // overwritten, so nothing commits and the message is retried, then dead-lettered.
                if (row.LastEventId != advanced.PreviousEventId?.Value)
                {
                    throw new InvalidOperationException("The Snapshot disagrees with the ordering state the event was decided against.");
                }

                row.Apply(canonical, change, advanced.Incoming, snapshotObject.Version);
            }
            else
            {
                dbContext.SnapshotFields.Add(SnapshotFieldRecord.Create(canonical, change, advanced.Incoming, snapshotObject.Version));
            }
        }

        return snapshotObject.Version;
    }
}
