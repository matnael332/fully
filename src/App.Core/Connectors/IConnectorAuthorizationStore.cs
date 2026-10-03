using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Connectors;

/// <summary>
/// Durable, tenant-scoped storage of connector authorizations and of the authorizations being started.
/// </summary>
/// <remarks>
/// <para>
/// Every operation acts in the trusted tenant of the current scope; no operation accepts a tenant, and none reads
/// across tenants. The store holds only what decision D3B-2 allows in ordinary fields: ids, hashes, times, scope names,
/// states, the credential generation, an opaque material version and the provider's non-secret identifiers. It never
/// holds a token, a client secret or a verifier.
/// </para>
/// <para>
/// Each state change is a compare-and-swap on durable state, so concurrent instances cannot both win: a transaction is
/// consumed once, a refresh is claimed by one caller, and a completed refresh advances the generation only from the
/// generation it started from. A <see langword="false"/> result means the expected state no longer held and nothing
/// changed; a failed result means the store could not be reached or refused the write.
/// </para>
/// </remarks>
public interface IConnectorAuthorizationStore
{
    /// <summary>
    /// Opens a transaction whose creation and expiry the database clock stamps. Returns <see langword="false"/> when
    /// the canonical user is unknown or inactive in the tenant.
    /// </summary>
    /// <param name="transaction">The transaction to open.</param>
    /// <param name="lifetime">How long it stays usable: positive and at most <see cref="ConnectorAuthorizationTransaction.MaxLifetime"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> OpenTransactionAsync(
        ConnectorAuthorizationTransaction transaction,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks a transaction in order (it exists, has not expired, belongs to the user, matches the state hash, was not
    /// consumed) and consumes it atomically when every check passes. A refusal changes nothing.
    /// </summary>
    /// <param name="transactionId">The transaction the state names.</param>
    /// <param name="canonicalUser">The trusted canonical user completing the authorization.</param>
    /// <param name="stateHash">The SHA-256 of the state's secret part, compared in constant time.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<ConnectorTransactionConsumption>> ConsumeTransactionAsync(
        Guid transactionId,
        UserId canonicalUser,
        ReadOnlyMemory<byte> stateHash,
        CancellationToken cancellationToken);

    /// <summary>Closes up to <paramref name="limit"/> expired, unconsumed transactions and returns their ids.</summary>
    /// <param name="limit">The most transactions to close: 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<IReadOnlyList<Guid>>> CloseExpiredTransactionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists up to <paramref name="limit"/> transactions, oldest first, whose PKCE verifier may still be stored but can
    /// no longer be used: closed unconsumed after their expiry, or consumed at least
    /// <see cref="ConnectorAuthorizationTransaction.MaxLifetime"/> ago, the provider's code validity, so no code issued
    /// for them can still be exchanged. A transaction stays listed until the verifier's deletion is recorded.
    /// </summary>
    /// <param name="limit">The most transactions to list: 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<IReadOnlyList<Guid>>> ListVerifierCleanupAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a closed or consumed transaction's PKCE verifier is gone from secret storage. Returns
    /// <see langword="false"/> when the transaction is unknown in the tenant, still open, or already recorded.
    /// </summary>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> RecordVerifierDeletedAsync(Guid transactionId, CancellationToken cancellationToken);

    /// <summary>Reads a connection's authorization, or <see langword="null"/> when it has none in the tenant.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<ConnectorAuthorization?>> ReadAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a new, enabled connection and its active authorization at generation 1. Returns
    /// <see langword="false"/> when the canonical user is unknown or inactive.
    /// </summary>
    /// <param name="grant">The verified grant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> ActivateAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken);

    /// <summary>
    /// Restores an authorization that requires reauthorization, advancing its generation. Succeeds only for the same
    /// canonical user and provider identity, while the connection is enabled and the user active.
    /// </summary>
    /// <param name="grant">The verified grant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> ReauthorizeAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken);

    /// <summary>Claims the renewal of an active authorization at the given generation, when no renewal holds a claim.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="generation">The generation the renewal starts from.</param>
    /// <param name="claim">The renewal's id; it also names the renewal's stored credential.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> ClaimRefreshAsync(Guid connectionId, long generation, Guid claim, CancellationToken cancellationToken);

    /// <summary>
    /// Completes the renewal holding <paramref name="claim"/> from <paramref name="generation"/>: the generation
    /// advances by one, the credential stored under the claim becomes current, and the claim is released.
    /// </summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="generation">The generation the renewal started from.</param>
    /// <param name="claim">The renewal's claim.</param>
    /// <param name="accessExpiresAt">When the new access credential expires.</param>
    /// <param name="grantedScopes">The scope names granted with it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> CompleteRefreshAsync(
        Guid connectionId,
        long generation,
        Guid claim,
        DateTimeOffset accessExpiresAt,
        string grantedScopes,
        CancellationToken cancellationToken);

    /// <summary>Releases a claim without changing the credential, after a renewal that provably changed nothing.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="claim">The renewal's claim.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> ReleaseRefreshAsync(Guid connectionId, Guid claim, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an active authorization at <paramref name="generation"/>, holding exactly <paramref name="claim"/> (none
    /// when null), to <see cref="ConnectorAuthorizationState.ReauthorizationRequired"/> and releases the claim.
    /// </summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="generation">The generation that can no longer be trusted or renewed.</param>
    /// <param name="claim">The claim the authorization must hold, or null for none.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> RequireReauthorizationAsync(
        Guid connectionId,
        long generation,
        Guid? claim,
        CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects in one transaction: the authorization becomes revoked and the connection disabled, before anything
    /// is sent to the provider. Only the canonical user who authorized the connection may disconnect it.
    /// </summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="requestedBy">The trusted canonical user asking.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<ConnectorDisconnection>> DisconnectAsync(
        Guid connectionId,
        UserId requestedBy,
        CancellationToken cancellationToken);

    /// <summary>Records whether the provider confirmed the revocation of a disconnected connection's credentials.</summary>
    /// <param name="connectionId">The disconnected connection.</param>
    /// <param name="confirmed">Whether every revocation was confirmed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<Result<bool>> RecordProviderRevocationAsync(Guid connectionId, bool confirmed, CancellationToken cancellationToken);
}
