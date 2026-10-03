using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Processing;

/// <summary>
/// The durable gate between realtime intake and a connection's initial sync (Gate 3F, decisions D3F-10 to D3F-12).
/// </summary>
/// <remarks>
/// <para>
/// Ingress takes a key-share lock on the connection's own row before it inserts a receipt, and processing takes a share
/// lock on the connection's sync row before it locks one. A buffer start and a cutover take the exclusive lock of the
/// connection row, then of the sync row. So each waits until every receipt being stored or decided for the connection has
/// committed, and nothing for the connection is stored or decided again until it commits: the ingress fence it records is
/// exact, even for the first generation, before any sync row exists, and no receipt falls between the last replay and the
/// transition to Live. Locks are always taken in the order connection row, sync row, receipt.
/// </para>
/// <para>
/// A connection takes part in initial sync from the moment its trusted configuration first enables a scope, which creates
/// its sync row, NotStarted, under the same exclusive locks. A receipt of a connection whose row is not Live is held when it
/// lies above the replay floor, where the connection began holding; one at or below it arrived before, and is processed as
/// it would have been. A connection with no row and no enabled scope never took part, and its receipts are processed as
/// before 3F; enabled scopes with no row, written outside the configuration service, still hold every receipt, since no
/// baseline was ever established for them. Nothing is inferred from a payload. The ingress sequence decides membership
/// only; it never orders business state.
/// </para>
/// </remarks>
internal static class InitialSyncGate
{
    private static readonly string Live = nameof(InitialSyncState.Live);

    /// <summary>
    /// Takes the share lock on the connection's sync row, in the caller's transaction, and says whether a receipt at this
    /// ingress position must be held for the connection's initial sync.
    /// </summary>
    internal static async Task<bool> HoldsAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        string connectionReference,
        long ingressSequence,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(connectionReference, "D", out var connectionId))
        {
            return false;
        }

        var sync = await ShareSyncAsync(dbContext, tenant, connectionId, cancellationToken);
        if (sync is null)
        {
            // No sync row to lock. Whether one exists now and whether the configuration enables a scope are read in one
            // snapshot, since a first configuration creates both at once: one that committed since is decided by its row.
            var configuration = (await dbContext.Database.SqlQuery<GateConfiguration>(
                    $"""
                    SELECT EXISTS (
                               SELECT 1 FROM public.connection_syncs
                               WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId}) AS "Takes",
                           EXISTS (
                               SELECT 1 FROM public.connector_source_scopes
                               WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId} AND enabled) AS "Configured"
                    """)
                .ToListAsync(cancellationToken)).Single();
            if (!configuration.Takes)
            {
                // Only an enabled scope of the connection's own configuration makes it take part.
                return configuration.Configured;
            }

            sync = await ShareSyncAsync(dbContext, tenant, connectionId, cancellationToken)
                ?? throw new InvalidOperationException("A connection's sync row is never removed.");
        }

        return !string.Equals(sync.State, Live, StringComparison.Ordinal) && ingressSequence > sync.ReplayFloor;
    }

    /// <summary>
    /// Locks the connection's sync row for a change, in the caller's transaction, after the connection row's exclusive lock:
    /// a buffer start, a cutover, a generation's transition or a change of the scope set.
    /// </summary>
    /// <returns>The tracked row, or null when the connection does not take part in initial sync.</returns>
    internal static async Task<ConnectionSyncRecord?> LockSyncAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        (await dbContext.ConnectionSyncs
            .FromSql($"""
                SELECT * FROM public.connection_syncs
                WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken)).SingleOrDefault();

    /// <summary>
    /// Takes the key-share lock on the connection's row before ingress stores a receipt, so a buffer start or a cutover that
    /// holds the row waits for this receipt, and this receipt waits for it. The row exists from the connection's creation, so
    /// this holds before the connection's first sync generation too; a key-share lock never blocks an ordinary update.
    /// </summary>
    internal static Task LockForIngressAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM public.connections
                WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId}
                FOR KEY SHARE
                """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Takes the exclusive lock on the connection's row for a buffer start or a cutover, in the caller's transaction: every
    /// receipt ingress was storing for the connection has committed, and none is stored until the caller commits.
    /// </summary>
    /// <returns>Whether the trusted tenant has the connection.</returns>
    internal static async Task<bool> LockConnectionAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM public.connections
                WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken)).Count == 1;

    /// <summary>
    /// The ingress fence of the connection: the highest ingress position among its committed receipts, or 0 when it has none.
    /// Exact only under <see cref="LockConnectionAsync"/>, which the caller holds.
    /// </summary>
    internal static async Task<long> IngressFenceAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        string connectionReference,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<long>(
                $"""
                SELECT COALESCE(MAX(ingress_sequence), 0) AS "Value"
                FROM public.raw_receipts
                WHERE tenant_id = {tenant.Value} AND connection_reference = {connectionReference}
                """)
            .ToListAsync(cancellationToken)).Single();

    private static async Task<GateRow?> ShareSyncAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<GateRow>(
                $"""
                SELECT state AS "State", replay_floor AS "ReplayFloor"
                FROM public.connection_syncs
                WHERE tenant_id = {tenant.Value} AND connection_id = {connectionId}
                FOR SHARE
                """)
            .ToListAsync(cancellationToken)).SingleOrDefault();

    private sealed class GateRow
    {
        public string State { get; init; } = string.Empty;

        public long ReplayFloor { get; init; }
    }

    private sealed class GateConfiguration
    {
        public bool Takes { get; init; }

        public bool Configured { get; init; }
    }
}
