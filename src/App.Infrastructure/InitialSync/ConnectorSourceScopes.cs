using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Infrastructure.Reconciliation;

namespace WorkContinuity.Infrastructure.InitialSync;

/// <summary>
/// Configures the source scopes of connections (Gate 3F, decision D3F-1): the explicit resources initial sync may read,
/// in the trusted tenant only.
/// </summary>
/// <remarks>
/// <para>
/// A scope is configured on a connection of the trusted tenant that serves the scope's source; under forced row-level
/// security another tenant's connection is not even visible. Once written, a scope's tenant, connection, source and
/// resource never change, which the runtime role's column grants enforce: only whether it is enabled does. Nothing here
/// discovers a resource, reads the source or starts a sync.
/// </para>
/// <para>
/// Configuration and realtime intake are fenced by the same locks as a buffer start and a cutover: one transaction takes
/// the connection row's exclusive lock, then the sync row's, so every receipt being stored or decided for the connection
/// has committed, and none is stored or decided until this commits. When the enabled scope set changes, the connection's
/// first enabled scope makes it take part in initial sync, NotStarted, holding every receipt above the exact ingress fence
/// from then on; any later change raises the scope-set version and makes a Live, running or failed generation restart over
/// the new set, in the same transaction. A generation's own scope set is never rewritten, and the replay floor never moves.
/// </para>
/// </remarks>
internal sealed class ConnectorSourceScopes(
    ITenantScopedDatabase database,
    ITenantContextAccessor tenantContextAccessor,
    IClock clock) : IConnectorSourceScopes
{
    /// <inheritdoc />
    public async Task<Result<ConnectorSourceScope>> ConfigureAsync(ConnectorSourceScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsTrusted(scope.Tenant))
        {
            return Result<ConnectorSourceScope>.Failure(InitialSyncErrors.TenantMismatch);
        }

        // Configurations of one connection take its row's lock in turn, so a second one finds a new scope instead of
        // inserting it again; a duplicate insert is still retried once.
        for (var attempt = 0; ; attempt++)
        {
            var configured = await database.ExecuteAsync((dbContext, token) => ConfigureAsync(dbContext, scope, token), cancellationToken);
            if (configured.IsSuccess)
            {
                return configured.Value is { } refusal
                    ? Result<ConnectorSourceScope>.Failure(refusal)
                    : Result<ConnectorSourceScope>.Success(scope);
            }

            if (configured.Error != PersistenceErrors.Duplicate || attempt > 0)
            {
                return Result<ConnectorSourceScope>.Failure(InitialSyncErrors.Unavailable);
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ConnectorSourceScope>>> ListAsync(TenantId tenant, Guid connectionId, CancellationToken cancellationToken = default)
    {
        if (!IsTrusted(tenant))
        {
            return Result<IReadOnlyList<ConnectorSourceScope>>.Failure(InitialSyncErrors.TenantMismatch);
        }

        var listed = await database.ExecuteAsync(
            (dbContext, token) => dbContext.ConnectorSourceScopes
                .AsNoTracking()
                .Where(scope => scope.TenantId == tenant && scope.ConnectionId == connectionId)
                .Select(scope => new { scope.SourceSystem, scope.ResourceId, scope.Enabled })
                .ToListAsync(token),
            cancellationToken);
        if (listed.IsFailure)
        {
            return Result<IReadOnlyList<ConnectorSourceScope>>.Failure(InitialSyncErrors.Unavailable);
        }

        return Result<IReadOnlyList<ConnectorSourceScope>>.Success(listed.Value
            .OrderBy(scope => scope.ResourceId, StringComparer.Ordinal)
            .Select(scope => new ConnectorSourceScope(tenant, connectionId, scope.SourceSystem, scope.ResourceId, scope.Enabled))
            .ToList());
    }

    private bool IsTrusted(TenantId tenant) =>
        tenantContextAccessor.Current.TryGetTenantId(out var trusted) && trusted == tenant;

    private async Task<Error?> ConfigureAsync(WorkContinuityDbContext dbContext, ConnectorSourceScope scope, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        // The connection row exclusively, then its sync row, as a buffer start and a cutover take them.
        if (!await InitialSyncGate.LockConnectionAsync(dbContext, scope.Tenant, scope.ConnectionId, cancellationToken))
        {
            return InitialSyncErrors.ConnectionNotFound;
        }

        // The connection must be the tenant's own and serve the scope's source.
        var serves = await dbContext.Connections
            .AsNoTracking()
            .AnyAsync(
                connection => connection.TenantId == scope.Tenant
                    && connection.ConnectionId == scope.ConnectionId
                    && connection.SourceSystem == scope.SourceSystem,
                cancellationToken);
        if (!serves)
        {
            return InitialSyncErrors.ConnectionNotFound;
        }

        var sync = await InitialSyncGate.LockSyncAsync(dbContext, scope.Tenant, scope.ConnectionId, cancellationToken);
        var scopes = await dbContext.ConnectorSourceScopes
            .Where(candidate => candidate.TenantId == scope.Tenant && candidate.ConnectionId == scope.ConnectionId)
            .ToListAsync(cancellationToken);
        var before = scopes.Where(candidate => candidate.Enabled).Select(candidate => candidate.ResourceId).ToHashSet(StringComparer.Ordinal);

        var now = clock.UtcNow;
        var existing = scopes.SingleOrDefault(candidate => string.Equals(candidate.ResourceId, scope.ResourceId, StringComparison.Ordinal));
        if (existing is null)
        {
            dbContext.ConnectorSourceScopes.Add(ConnectorSourceScopeRecord.Configure(
                scope.Tenant, scope.ConnectionId, scope.SourceSystem, scope.ResourceId, scope.Enabled, now));
        }
        else
        {
            existing.SetEnabled(scope.Enabled, now);
        }

        // Only a change of the enabled scope set changes what a baseline must represent.
        var after = new HashSet<string>(before, StringComparer.Ordinal);
        if (scope.Enabled)
        {
            after.Add(scope.ResourceId);
        }
        else
        {
            after.Remove(scope.ResourceId);
        }

        if (after.SetEquals(before))
        {
            return null;
        }

        if (sync is not null)
        {
            sync.ChangeScopeSet(SyncScopeOutcomes.CodeOf(SyncScopeOutcome.ScopeSetChanged), now);
        }
        else
        {
            // The connection takes part from now: every receipt above this exact fence is held until a baseline is established.
            // Enabled scopes with no sync row were written outside this service and have held every receipt, so all of them
            // are replayed, even when this change disables the last of them.
            var fence = await InitialSyncGate.IngressFenceAsync(dbContext, scope.Tenant, ConnectionRecord.ReferenceFor(scope.ConnectionId), cancellationToken);
            sync = ConnectionSyncRecord.Configure(scope.Tenant, scope.ConnectionId, scope.SourceSystem, before.Count == 0 ? fence : 0, fence, now);
            dbContext.ConnectionSyncs.Add(sync);
        }

        // A scope enabled again, or for the first time, may already hold state: none of it is current work until a baseline
        // read of the scope confirms it. A disabled scope lost its baseline in SetEnabled, in this same transaction.
        if (scope.Enabled)
        {
            await ReconciliationTargets.AwaitBaselineAsync(
                dbContext, scope.Tenant, scope.SourceSystem, ConnectionRecord.ReferenceFor(scope.ConnectionId), scope.ResourceId, sync.ScopeVersion, cancellationToken);
        }

        return null;
    }
}
