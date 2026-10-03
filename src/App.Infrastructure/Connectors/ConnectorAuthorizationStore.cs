using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Connectors;

/// <summary>
/// The PostgreSQL implementation of <see cref="IConnectorAuthorizationStore"/> (Gate 3B).
/// </summary>
/// <remarks>
/// <para>
/// Every operation runs through <see cref="ITenantScopedDatabase"/>, so each is one transaction in the trusted tenant
/// under forced row-level security; the tenant written into a new row comes from the same trusted context, and the
/// tenant policy's check refuses any other. There is no tenant-less path.
/// </para>
/// <para>
/// Each state change is a single conditional statement, a compare-and-swap on durable state: it changes one row only
/// when the expected state, generation and claim still hold, and reports whether it did. Consuming a transaction and
/// disconnecting read their row under <c>FOR UPDATE</c> first, so concurrent callers queue and the second sees the
/// first's result. Every instant the store records is the database's own clock. All values travel as parameters.
/// </para>
/// </remarks>
public sealed class ConnectorAuthorizationStore(
    ITenantScopedDatabase database,
    ITenantContextAccessor tenantContextAccessor) : IConnectorAuthorizationStore
{
    /// <summary>The most transactions one call closes.</summary>
    public const int MaxClosedPerCall = 100;

    /// <inheritdoc />
    public Task<Result<bool>> OpenTransactionAsync(
        ConnectorAuthorizationTransaction transaction,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (lifetime <= TimeSpan.Zero || lifetime > ConnectorAuthorizationTransaction.MaxLifetime)
        {
            return Task.FromResult(Result<bool>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        if (!TryGetTenant(out var tenant))
        {
            return Task.FromResult(Result<bool>.Failure(PersistenceErrors.TenantContextMissing));
        }

        var stateHash = transaction.StateHash.ToArray();
        var redirectHash = transaction.RedirectHash.ToArray();
        var purpose = transaction.Purpose.ToString();

        return database.ExecuteAsync(
            async (context, token) =>
            {
                if (!await IsActiveUserAsync(context, transaction.CanonicalUser, token))
                {
                    return false;
                }

                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO public.connector_authorization_transactions
                        (transaction_id, tenant_id, canonical_user_id, connection_id, source_system, purpose, state_hash,
                         redirect_hash, requested_scopes, created_at_utc, expires_at_utc, consumed_at_utc)
                    VALUES ({transaction.TransactionId}, {tenant.Value}, {transaction.CanonicalUser.Value},
                            {transaction.ConnectionId}, {transaction.SourceSystem}, {purpose}, {stateHash},
                            {redirectHash}, {transaction.RequestedScopes}, now(), now() + {lifetime}, NULL)
                    """,
                    token);

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ConnectorTransactionConsumption>> ConsumeTransactionAsync(
        Guid transactionId,
        UserId canonicalUser,
        ReadOnlyMemory<byte> stateHash,
        CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty || canonicalUser.IsEmpty || stateHash.Length != ConnectorAuthorizationTransaction.HashLength)
        {
            return Task.FromResult(Result<ConnectorTransactionConsumption>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        var presented = stateHash.ToArray();

        return database.ExecuteAsync(
            async (context, token) =>
            {
                // The lock queues a concurrent second callback until this one commits; it then sees the consumption.
                var rows = await context.Database.SqlQuery<TransactionRow>(
                        $"""
                        SELECT transaction_id AS "TransactionId", canonical_user_id AS "CanonicalUserId",
                               connection_id AS "ConnectionId", source_system AS "SourceSystem", purpose AS "Purpose",
                               state_hash AS "StateHash", redirect_hash AS "RedirectHash",
                               requested_scopes AS "RequestedScopes", expires_at_utc <= now() AS "Expired",
                               consumed_at_utc IS NOT NULL AS "Consumed"
                        FROM public.connector_authorization_transactions
                        WHERE transaction_id = {transactionId}
                        FOR UPDATE
                        """)
                    .ToListAsync(token);

                if (rows.Count != 1)
                {
                    return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.NotFound);
                }

                var row = rows[0];
                if (row.Expired)
                {
                    return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.Expired);
                }

                if (row.CanonicalUserId != canonicalUser.Value)
                {
                    return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.BindingMismatch);
                }

                if (!CryptographicOperations.FixedTimeEquals(row.StateHash, presented))
                {
                    return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.StateMismatch);
                }

                if (row.Consumed)
                {
                    return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.AlreadyConsumed);
                }

                await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public.connector_authorization_transactions
                    SET consumed_at_utc = now()
                    WHERE transaction_id = {transactionId} AND consumed_at_utc IS NULL
                    """,
                    token);

                return ConnectorTransactionConsumption.Consumed(new ConnectorAuthorizationTransaction(
                    row.TransactionId,
                    UserId.From(row.CanonicalUserId),
                    row.ConnectionId,
                    row.SourceSystem,
                    Enum.Parse<ConnectorAuthorizationPurpose>(row.Purpose),
                    row.StateHash,
                    row.RedirectHash,
                    row.RequestedScopes));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Guid>>> CloseExpiredTransactionsAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxClosedPerCall)
        {
            return Result<IReadOnlyList<Guid>>.Failure(ConnectorAuthorizationErrors.InvalidRequest);
        }

        // The batch is chosen once, in a CTE, as the retention purge does: a locking subquery inside IN may be evaluated
        // more than once and so exceed the limit.
        var closed = await database.ExecuteAsync(
            (context, token) => context.Database.SqlQuery<Guid>(
                    $"""
                    WITH due AS (
                        SELECT o.transaction_id
                        FROM public.connector_authorization_transactions AS o
                        WHERE o.consumed_at_utc IS NULL AND o.expires_at_utc <= now()
                        ORDER BY o.expires_at_utc, o.transaction_id
                        LIMIT {limit}
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE public.connector_authorization_transactions AS t
                    SET consumed_at_utc = now()
                    FROM due
                    WHERE t.transaction_id = due.transaction_id
                    RETURNING t.transaction_id AS "Value"
                    """)
                .ToListAsync(token),
            cancellationToken);

        return closed.IsSuccess
            ? Result<IReadOnlyList<Guid>>.Success(closed.Value)
            : Result<IReadOnlyList<Guid>>.Failure(closed.Error);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Guid>>> ListVerifierCleanupAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxClosedPerCall)
        {
            return Result<IReadOnlyList<Guid>>.Failure(ConnectorAuthorizationErrors.InvalidRequest);
        }

        // A transaction consumed at or after its expiry was closed unconsumed, since consuming refuses an expired one:
        // no completion ever owned it. One consumed before its expiry belongs to its completion until no code issued
        // for it can be exchanged any more.
        var validity = ConnectorAuthorizationTransaction.MaxLifetime;
        var listed = await database.ExecuteAsync(
            (context, token) => context.Database.SqlQuery<Guid>(
                    $"""
                    SELECT transaction_id AS "Value"
                    FROM public.connector_authorization_transactions
                    WHERE verifier_deleted_at_utc IS NULL
                      AND consumed_at_utc IS NOT NULL
                      AND (consumed_at_utc >= expires_at_utc OR consumed_at_utc <= now() - {validity})
                    ORDER BY consumed_at_utc, transaction_id
                    LIMIT {limit}
                    """)
                .ToListAsync(token),
            cancellationToken);

        return listed.IsSuccess
            ? Result<IReadOnlyList<Guid>>.Success(listed.Value)
            : Result<IReadOnlyList<Guid>>.Failure(listed.Error);
    }

    /// <inheritdoc />
    public Task<Result<bool>> RecordVerifierDeletedAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty)
        {
            return Task.FromResult(Result<bool>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        return database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorization_transactions
                SET verifier_deleted_at_utc = now()
                WHERE transaction_id = {transactionId} AND consumed_at_utc IS NOT NULL AND verifier_deleted_at_utc IS NULL
                """,
                token) == 1,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ConnectorAuthorization?>> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty)
        {
            return Task.FromResult(Result<ConnectorAuthorization?>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        return database.ExecuteAsync(
            async (context, token) =>
            {
                var rows = await context.Database.SqlQuery<AuthorizationRow>(
                        $"""
                        SELECT a.tenant_id AS "TenantId", a.connection_id AS "ConnectionId",
                               c.source_system AS "SourceSystem", c.status AS "ConnectionStatus",
                               a.canonical_user_id AS "CanonicalUserId", u.is_active AS "UserActive", a.state AS "State",
                               a.provider_subject AS "ProviderSubject", a.provider_account AS "ProviderAccount",
                               a.generation AS "Generation", a.material_version AS "MaterialVersion",
                               a.access_expires_at_utc AS "AccessExpiresAtUtc", a.granted_scopes AS "GrantedScopes",
                               a.refresh_claim_id AS "RefreshClaimId",
                               now() - a.refresh_claimed_at_utc AS "RefreshClaimAge"
                        FROM public.connector_authorizations AS a
                        JOIN public.connections AS c ON c.tenant_id = a.tenant_id AND c.connection_id = a.connection_id
                        JOIN public.users AS u ON u.tenant_id = a.tenant_id AND u.user_id = a.canonical_user_id
                        WHERE a.connection_id = {connectionId}
                        """)
                    .ToListAsync(token);

                return rows.Count == 1 ? ToAuthorization(rows[0]) : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ActivateAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);

        if (!TryGetTenant(out var tenant))
        {
            return Task.FromResult(Result<bool>.Failure(PersistenceErrors.TenantContextMissing));
        }

        return database.ExecuteAsync(
            async (context, token) =>
            {
                if (!await IsActiveUserAsync(context, grant.CanonicalUser, token))
                {
                    return false;
                }

                // A connection id that already exists fails the insert, and the whole transaction with it.
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO public.connections (connection_id, tenant_id, source_system, status, created_at_utc)
                    VALUES ({grant.ConnectionId}, {tenant.Value}, {grant.SourceSystem}, 'Active', now())
                    """,
                    token);

                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO public.connector_authorizations
                        (connection_id, tenant_id, canonical_user_id, state, provider_subject, provider_account, generation,
                         material_version, access_expires_at_utc, granted_scopes, refresh_claim_id, refresh_claimed_at_utc,
                         authorized_at_utc, refreshed_at_utc, reauthorization_required_at_utc, revoked_at_utc,
                         provider_revocation_confirmed)
                    VALUES ({grant.ConnectionId}, {tenant.Value}, {grant.CanonicalUser.Value}, 'Active',
                            {grant.ProviderSubject}, {grant.ProviderAccount}, 1, {grant.MaterialVersion},
                            {grant.AccessExpiresAt}, {grant.GrantedScopes}, NULL, NULL, now(), NULL, NULL, NULL, NULL)
                    """,
                    token);

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ReauthorizeAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorizations AS a
                SET state = 'Active', generation = a.generation + 1, material_version = {grant.MaterialVersion},
                    access_expires_at_utc = {grant.AccessExpiresAt}, granted_scopes = {grant.GrantedScopes},
                    refresh_claim_id = NULL, refresh_claimed_at_utc = NULL, authorized_at_utc = now(),
                    reauthorization_required_at_utc = NULL
                WHERE a.connection_id = {grant.ConnectionId}
                  AND a.state = 'ReauthorizationRequired'
                  AND a.canonical_user_id = {grant.CanonicalUser.Value}
                  AND a.provider_subject = {grant.ProviderSubject}
                  AND a.provider_account = {grant.ProviderAccount}
                  AND EXISTS (SELECT 1 FROM public.connections AS c
                              WHERE c.tenant_id = a.tenant_id AND c.connection_id = a.connection_id
                                AND c.status = 'Active' AND c.source_system = {grant.SourceSystem})
                  AND EXISTS (SELECT 1 FROM public.users AS u
                              WHERE u.tenant_id = a.tenant_id AND u.user_id = a.canonical_user_id AND u.is_active)
                """,
                token) == 1,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ClaimRefreshAsync(Guid connectionId, long generation, Guid claim, CancellationToken cancellationToken) =>
        database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorizations
                SET refresh_claim_id = {claim}, refresh_claimed_at_utc = now()
                WHERE connection_id = {connectionId} AND state = 'Active' AND generation = {generation}
                  AND refresh_claim_id IS NULL
                """,
                token) == 1,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<bool>> CompleteRefreshAsync(
        Guid connectionId,
        long generation,
        Guid claim,
        DateTimeOffset accessExpiresAt,
        string grantedScopes,
        CancellationToken cancellationToken)
    {
        if (!ConnectorScopes.IsWellFormed(grantedScopes))
        {
            return Task.FromResult(Result<bool>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        var expiresAt = accessExpiresAt.ToUniversalTime();

        return database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorizations
                SET generation = generation + 1, material_version = {claim}, access_expires_at_utc = {expiresAt},
                    granted_scopes = {grantedScopes}, refresh_claim_id = NULL, refresh_claimed_at_utc = NULL,
                    refreshed_at_utc = now()
                WHERE connection_id = {connectionId} AND state = 'Active' AND generation = {generation}
                  AND refresh_claim_id = {claim}
                """,
                token) == 1,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ReleaseRefreshAsync(Guid connectionId, Guid claim, CancellationToken cancellationToken) =>
        database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorizations
                SET refresh_claim_id = NULL, refresh_claimed_at_utc = NULL
                WHERE connection_id = {connectionId} AND refresh_claim_id = {claim}
                """,
                token) == 1,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<bool>> RequireReauthorizationAsync(
        Guid connectionId,
        long generation,
        Guid? claim,
        CancellationToken cancellationToken) =>
        database.ExecuteAsync(
            async (context, token) => claim is { } held
                ? await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public.connector_authorizations
                    SET state = 'ReauthorizationRequired', reauthorization_required_at_utc = now(),
                        refresh_claim_id = NULL, refresh_claimed_at_utc = NULL
                    WHERE connection_id = {connectionId} AND state = 'Active' AND generation = {generation}
                      AND refresh_claim_id = {held}
                    """,
                    token) == 1
                : await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public.connector_authorizations
                    SET state = 'ReauthorizationRequired', reauthorization_required_at_utc = now()
                    WHERE connection_id = {connectionId} AND state = 'Active' AND generation = {generation}
                      AND refresh_claim_id IS NULL
                    """,
                    token) == 1,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<ConnectorDisconnection>> DisconnectAsync(
        Guid connectionId,
        UserId requestedBy,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty || requestedBy.IsEmpty)
        {
            return Task.FromResult(Result<ConnectorDisconnection>.Failure(ConnectorAuthorizationErrors.InvalidRequest));
        }

        return database.ExecuteAsync(
            async (context, token) =>
            {
                var rows = await context.Database.SqlQuery<DisconnectRow>(
                        $"""
                        SELECT canonical_user_id AS "CanonicalUserId", state AS "State",
                               material_version AS "MaterialVersion", refresh_claim_id AS "RefreshClaimId"
                        FROM public.connector_authorizations
                        WHERE connection_id = {connectionId}
                        FOR UPDATE
                        """)
                    .ToListAsync(token);

                if (rows.Count != 1)
                {
                    return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.NotFound);
                }

                var row = rows[0];
                if (row.CanonicalUserId != requestedBy.Value)
                {
                    return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.NotPermitted);
                }

                if (row.State == nameof(ConnectorAuthorizationState.Revoked))
                {
                    return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.AlreadyDisconnected);
                }

                // Unusable locally first, in one transaction, before anything is sent to the provider.
                await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public.connector_authorizations
                    SET state = 'Revoked', revoked_at_utc = now(), refresh_claim_id = NULL, refresh_claimed_at_utc = NULL
                    WHERE connection_id = {connectionId}
                    """,
                    token);

                await context.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE public.connections SET status = 'Disabled' WHERE connection_id = {connectionId}
                    """,
                    token);

                return ConnectorDisconnection.Disconnected(row.MaterialVersion, row.RefreshClaimId);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<bool>> RecordProviderRevocationAsync(Guid connectionId, bool confirmed, CancellationToken cancellationToken) =>
        database.ExecuteAsync(
            async (context, token) => await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public.connector_authorizations
                SET provider_revocation_confirmed = {confirmed}
                WHERE connection_id = {connectionId} AND state = 'Revoked'
                """,
                token) == 1,
            cancellationToken);

    private static async Task<bool> IsActiveUserAsync(WorkContinuityDbContext context, UserId user, CancellationToken token)
    {
        // Row-level security shows this tenant's users only; the share lock keeps the membership stable meanwhile.
        var active = await context.Database.SqlQuery<bool>(
                $"""
                SELECT is_active AS "Value" FROM public.users WHERE user_id = {user.Value} FOR SHARE
                """)
            .ToListAsync(token);

        return active is [true];
    }

    private static ConnectorAuthorization ToAuthorization(AuthorizationRow row) => new(
        TenantId.From(row.TenantId),
        row.ConnectionId,
        row.SourceSystem,
        connectionEnabled: row.ConnectionStatus == "Active",
        UserId.From(row.CanonicalUserId),
        canonicalUserActive: row.UserActive,
        Enum.Parse<ConnectorAuthorizationState>(row.State),
        row.ProviderSubject,
        row.ProviderAccount,
        row.Generation,
        row.MaterialVersion,
        row.AccessExpiresAtUtc,
        row.GrantedScopes,
        row.RefreshClaimId,
        row.RefreshClaimAge);

    private bool TryGetTenant(out TenantId tenant) => tenantContextAccessor.Current.TryGetTenantId(out tenant);

    private sealed class TransactionRow
    {
        public Guid TransactionId { get; init; }

        public Guid CanonicalUserId { get; init; }

        public Guid ConnectionId { get; init; }

        public string SourceSystem { get; init; } = string.Empty;

        public string Purpose { get; init; } = string.Empty;

        public byte[] StateHash { get; init; } = [];

        public byte[] RedirectHash { get; init; } = [];

        public string RequestedScopes { get; init; } = string.Empty;

        public bool Expired { get; init; }

        public bool Consumed { get; init; }
    }

    private sealed class AuthorizationRow
    {
        public Guid TenantId { get; init; }

        public Guid ConnectionId { get; init; }

        public string SourceSystem { get; init; } = string.Empty;

        public string ConnectionStatus { get; init; } = string.Empty;

        public Guid CanonicalUserId { get; init; }

        public bool UserActive { get; init; }

        public string State { get; init; } = string.Empty;

        public string ProviderSubject { get; init; } = string.Empty;

        public string ProviderAccount { get; init; } = string.Empty;

        public long Generation { get; init; }

        public Guid MaterialVersion { get; init; }

        public DateTimeOffset AccessExpiresAtUtc { get; init; }

        public string GrantedScopes { get; init; } = string.Empty;

        public Guid? RefreshClaimId { get; init; }

        public TimeSpan? RefreshClaimAge { get; init; }
    }

    private sealed class DisconnectRow
    {
        public Guid CanonicalUserId { get; init; }

        public string State { get; init; } = string.Empty;

        public Guid MaterialVersion { get; init; }

        public Guid? RefreshClaimId { get; init; }
    }
}
