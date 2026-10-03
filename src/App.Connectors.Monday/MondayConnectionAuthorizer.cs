using System.Security.Cryptography;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The monday OAuth 2.1 connection lifecycle (Gate 3B): starting an authorization, completing it from monday's
/// callback, and disconnecting.
/// </summary>
/// <remarks>
/// <para>
/// A connection acts through the delegated identity of the canonical user who authorized it (decision D3B-1): the chain
/// is the trusted tenant, the authenticated canonical user, the connection, and the monday user that monday's own
/// <c>me</c> query reports for the issued token. Nothing about that chain is taken from the browser: the tenant comes from
/// the trusted server context, the user from the authenticated principal, and the connection, redirect address and
/// scopes from server state and configuration.
/// </para>
/// <para>
/// Starting opens a server-owned transaction bound to the tenant, the user, the connection, the redirect address and the
/// scopes, usable once and for at most 10 minutes; its state secret and PKCE verifier come from the CSPRNG, the database
/// keeps only the state's hash, and the verifier lives only in secret storage. Completing checks, in order, that the
/// state names a transaction of this tenant, that it has not expired, that it belongs to this user, that the state
/// matches, and that it was never consumed, consuming it atomically; then that monday approved, that an <c>iss</c>, when
/// present, is monday's, that a code is present, and that the configured redirect address is the one the transaction
/// recorded. Only then is the code exchanged, once, never retried. The grant must be complete, expire sanely, hold only
/// allowlisted read scopes, and identify a monday user; for a reauthorization it must be the connection's recorded user
/// and account. The new pair is stored before any database row points at it, and any failure after monday issued
/// tokens revokes them and fails closed.
/// </para>
/// <para>
/// A transaction's PKCE verifier is deleted durably. The completion that consumed the transaction deletes it when it
/// finishes and records the deletion; until a deletion is recorded the database keeps the transaction due for cleanup,
/// so a completion whose process died, or whose deletion failed, leaves no verifier behind for good. A cleanup lists
/// only verifiers that can no longer be used, those of transactions closed unconsumed after their expiry and of
/// transactions consumed at least the provider's 10-minute code validity ago, so it never removes one a completion may
/// still read; it deletes each by its own transaction's name and records only a deletion the storage confirmed, so a
/// failure is retried by the next cleanup. Starting an authorization runs a cleanup, and a host can run one on its own.
/// Neither touches whether a transaction was consumed: no code becomes usable again, and no binding changes.
/// </para>
/// <para>
/// Disconnecting first makes the connection unusable in one database transaction (authorization revoked, connection
/// disabled), then revokes each held token at monday once and destroys every stored version. A monday outage leaves the
/// connection just as unusable and is recorded as an unconfirmed revocation. Nothing re-enables a disconnected
/// connection. Nothing here logs, and no error carries a code, state, verifier, token, secret or provider text.
/// </para>
/// </remarks>
public sealed class MondayConnectionAuthorizer
{
    /// <summary>The source system token of monday connections.</summary>
    public const string SourceSystem = "monday";

    /// <summary>The longest authorization code accepted.</summary>
    internal const int MaxCodeLength = 2048;

    /// <summary>How many expired transactions each start closes, and how many due verifiers it deletes.</summary>
    internal const int CleanedPerStart = 20;

    /// <summary>How many expired transactions a host's cleanup closes, and how many due verifiers it deletes.</summary>
    internal const int CleanedPerCleanup = 100;

    private readonly IConnectorAuthorizationStore _store;
    private readonly IMondaySecretStore _secrets;
    private readonly MondayOAuthOptions _options;
    private readonly MondayOAuthClient _oauth;
    private readonly MondayTokenKeeper _keeper;
    private readonly MondayReadClient _identityReader;
    private readonly ITenantContextAccessor _tenantContext;

    /// <summary>Creates the authorizer.</summary>
    /// <param name="store">The tenant-scoped authorization store.</param>
    /// <param name="secrets">The connector's secret storage.</param>
    /// <param name="options">The OAuth configuration.</param>
    /// <param name="authorizationHttp">The HTTP client for monday's token and revocation endpoints; its handler must not follow redirects.</param>
    /// <param name="identityReader">The monday read client, which verifies the identity a new token acts for.</param>
    /// <param name="tenantContext">The trusted server-side tenant context.</param>
    /// <param name="clock">The clock that judges a grant's expiry.</param>
    public MondayConnectionAuthorizer(
        IConnectorAuthorizationStore store,
        IMondaySecretStore secrets,
        MondayOAuthOptions options,
        HttpClient authorizationHttp,
        MondayReadClient identityReader,
        ITenantContextAccessor tenantContext,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizationHttp);
        ArgumentNullException.ThrowIfNull(identityReader);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _secrets = secrets;
        _options = options;
        _identityReader = identityReader;
        _tenantContext = tenantContext;
        _oauth = new MondayOAuthClient(authorizationHttp, clock, options.HttpTimeout);
        _keeper = new MondayTokenKeeper(secrets, _oauth, options.ClientId);
    }

    /// <summary>Starts authorizing a new connection, or restoring one that requires reauthorization.</summary>
    /// <param name="principal">The authenticated caller; the canonical user who will own the delegation.</param>
    /// <param name="reauthorizeConnectionId">The connection to restore, or null for a new connection.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The address to send the user's browser to, or why none may be issued.</returns>
    public async Task<Result<MondayAuthorizationRedirect>> StartAsync(
        Principal principal,
        Guid? reauthorizeConnectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!_tenantContext.Current.TryGetTenantId(out var tenant))
        {
            return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.TenantUnresolved);
        }

        if (!principal.IsAuthenticated)
        {
            return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.UserNotEligible);
        }

        var user = principal.UserId;
        _ = await CleanUpVerifiersAsync(tenant, CleanedPerStart, cancellationToken).ConfigureAwait(false);

        var purpose = ConnectorAuthorizationPurpose.Connect;
        var connectionId = Guid.CreateVersion7();
        if (reauthorizeConnectionId is { } existing)
        {
            if (existing == Guid.Empty)
            {
                return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.InvalidRequest);
            }

            var read = await _store.ReadAsync(existing, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure)
            {
                return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.StorageFailed);
            }

            if (read.Value is not { } authorization || !IsReauthorizable(authorization, user))
            {
                return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.NotReauthorizable);
            }

            purpose = ConnectorAuthorizationPurpose.Reauthorize;
            connectionId = existing;
        }

        var transactionId = Guid.CreateVersion7();
        var (state, stateHash) = MondayAuthorizationState.Create(transactionId);
        var verifier = MondayPkce.CreateVerifier();

        // The transaction first: a verifier then only ever exists for a transaction, which expiry closes and cleans up.
        var opened = await _store
            .OpenTransactionAsync(
                new ConnectorAuthorizationTransaction(
                    transactionId, user, connectionId, SourceSystem, purpose, stateHash, _options.RedirectHash.ToArray(), _options.Scopes),
                _options.TransactionLifetime,
                cancellationToken)
            .ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        if (!opened.Value)
        {
            return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.UserNotEligible);
        }

        var stored = await _secrets
            .SetAsync(MondaySecretName.ForVerifier(tenant, transactionId), new MondayVerifierMaterial(tenant, transactionId, verifier).ToBytes(), cancellationToken)
            .ConfigureAwait(false);
        if (stored != MondaySecretStoreStatus.Succeeded)
        {
            // Without its verifier the transaction can never complete; expiry closes it.
            return Result<MondayAuthorizationRedirect>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        return Result<MondayAuthorizationRedirect>.Success(
            new MondayAuthorizationRedirect(AuthorizationAddress(state, MondayPkce.ChallengeOf(verifier)), connectionId));
    }

    /// <summary>Completes an authorization from monday's callback.</summary>
    /// <param name="principal">The authenticated caller; it must be the user who started the authorization.</param>
    /// <param name="callback">The callback's query parameters, untrusted.</param>
    /// <param name="cancellationToken">Cancels the validation; once the transaction is consumed, completion runs to its end.</param>
    /// <returns>The connection's verified delegated identity, or why the authorization failed.</returns>
    public async Task<Result<ConnectorIdentity>> CompleteAsync(
        Principal principal,
        MondayAuthorizationCallback callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(callback);

        if (!_tenantContext.Current.TryGetTenantId(out var tenant))
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.TenantUnresolved);
        }

        if (!principal.IsAuthenticated)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.UserNotEligible);
        }

        // A malformed state names no transaction, and nothing about it reaches the database.
        if (!MondayAuthorizationState.TryParse(callback.State, out var transactionId, out var stateHash))
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.TransactionNotFound);
        }

        var consumed = await _store
            .ConsumeTransactionAsync(transactionId, principal.UserId, stateHash, cancellationToken)
            .ConfigureAwait(false);
        if (consumed.IsFailure)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        var verifierName = MondaySecretName.ForVerifier(tenant, transactionId);
        switch (consumed.Value.Outcome)
        {
            case ConnectorTransactionOutcome.NotFound:
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.TransactionNotFound);

            case ConnectorTransactionOutcome.Expired:
                await _keeper.DeleteQuietlyAsync(verifierName).ConfigureAwait(false);
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.TransactionExpired);

            // A wrong user or state leaves the transaction, and its verifier, to the user who started it.
            case ConnectorTransactionOutcome.BindingMismatch:
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.BindingMismatch);

            case ConnectorTransactionOutcome.StateMismatch:
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.StateMismatch);

            case ConnectorTransactionOutcome.AlreadyConsumed:
                // A replay never touches the verifier: the completion that consumed the transaction may still be about to
                // read it, and removes it itself when it finishes.
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.Replayed);
        }

        try
        {
            return await CompleteConsumedAsync(tenant, principal.UserId, consumed.Value.Transaction!, callback, verifierName)
                .ConfigureAwait(false);
        }
        finally
        {
            // This completion owned the verifier and needs it no more. A deletion not confirmed, or not recorded,
            // leaves the transaction due, and a cleanup finishes it.
            _ = await DeleteVerifierAsync(tenant, transactionId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the PKCE verifiers of the trusted tenant's closed or consumed transactions that may still be stored but
    /// can no longer be used, and records each deletion. One whose deletion fails stays due, and the next cleanup
    /// retries it. For a host to run on its own, so no verifier waits for another authorization to start.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cleanup between verifiers.</param>
    /// <returns>How many verifiers were deleted, and how many remain due.</returns>
    public async Task<Result<MondayVerifierCleanup>> CleanUpVerifiersAsync(CancellationToken cancellationToken)
    {
        if (!_tenantContext.Current.TryGetTenantId(out var tenant))
        {
            return Result<MondayVerifierCleanup>.Failure(ConnectorAuthorizationErrors.TenantUnresolved);
        }

        return await CleanUpVerifiersAsync(tenant, CleanedPerCleanup, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disconnects a connection: unusable locally first, then its tokens revoked at monday and destroyed.</summary>
    /// <param name="principal">The authenticated caller; in Gate 3B it must be the user who authorized the connection.</param>
    /// <param name="connectionId">The connection.</param>
    /// <param name="cancellationToken">Cancels before the connection is disabled; afterwards the disconnect runs to its end.</param>
    /// <returns>What happened, or why nothing did.</returns>
    public async Task<Result<MondayDisconnection>> DisconnectAsync(
        Principal principal,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!_tenantContext.Current.TryGetTenantId(out var tenant))
        {
            return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.TenantUnresolved);
        }

        if (!principal.IsAuthenticated)
        {
            return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.UserNotEligible);
        }

        if (connectionId == Guid.Empty)
        {
            return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.InvalidRequest);
        }

        var disconnected = await _store.DisconnectAsync(connectionId, principal.UserId, cancellationToken).ConfigureAwait(false);
        if (disconnected.IsFailure)
        {
            return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        var disconnection = disconnected.Value;
        switch (disconnection.Outcome)
        {
            case ConnectorDisconnectOutcome.NotFound:
                return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.ConnectionNotFound);
            case ConnectorDisconnectOutcome.NotPermitted:
                return Result<MondayDisconnection>.Failure(ConnectorAuthorizationErrors.NotPermitted);
            case ConnectorDisconnectOutcome.AlreadyDisconnected:
                return Result<MondayDisconnection>.Success(new MondayDisconnection(connectionId, WasAlreadyDisconnected: true, ProviderRevocationConfirmed: false));
        }

        // The connection is already unusable. From here each step runs to its end, whatever the caller does.
        var confirmed = await RevokeAndDestroyAsync(tenant, connectionId, disconnection.MaterialVersion!.Value, current: true).ConfigureAwait(false);
        if (disconnection.PendingMaterialVersion is { } pending)
        {
            // A renewal in progress may have stored a newer pair; if it did, that pair holds the live refresh token.
            confirmed &= await RevokeAndDestroyAsync(tenant, connectionId, pending, current: false).ConfigureAwait(false);
        }

        _ = await _store.RecordProviderRevocationAsync(connectionId, confirmed, CancellationToken.None).ConfigureAwait(false);
        return Result<MondayDisconnection>.Success(new MondayDisconnection(connectionId, WasAlreadyDisconnected: false, confirmed));
    }

    private async Task<Result<ConnectorIdentity>> CompleteConsumedAsync(
        TenantId tenant,
        UserId user,
        ConnectorAuthorizationTransaction transaction,
        MondayAuthorizationCallback callback,
        MondaySecretName verifierName)
    {
        // The transaction is consumed: completion runs to a durable end, whatever the caller does.
        var none = CancellationToken.None;

        if (callback.Error is not null)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.Denied);
        }

        if (callback.Issuer is not null && !string.Equals(callback.Issuer, MondayEndpoints.Issuer, StringComparison.Ordinal))
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.IssuerMismatch);
        }

        if (!IsCode(callback.Code))
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.CodeMissing);
        }

        if (!CryptographicOperations.FixedTimeEquals(transaction.RedirectHash.Span, _options.RedirectHash))
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.RedirectMismatch);
        }

        if (transaction.SourceSystem != SourceSystem)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.InvalidRequest);
        }

        ConnectorAuthorization? existing = null;
        if (transaction.Purpose == ConnectorAuthorizationPurpose.Reauthorize)
        {
            var read = await _store.ReadAsync(transaction.ConnectionId, none).ConfigureAwait(false);
            if (read.IsFailure)
            {
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.StorageFailed);
            }

            if (read.Value is not { } authorization || !IsReauthorizable(authorization, user))
            {
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.NotReauthorizable);
            }

            existing = authorization;
        }

        var lookup = await _secrets.GetAsync(verifierName, none).ConfigureAwait(false);
        var verifier = lookup.Status == MondaySecretStoreStatus.Succeeded
            ? MondayVerifierMaterial.Read(lookup.Value, tenant, transaction.TransactionId)
            : null;
        if (verifier is null)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        var clientSecret = await _keeper.ReadClientSecretAsync(none).ConfigureAwait(false);
        if (clientSecret is null)
        {
            return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.ClientUnavailable);
        }

        var outcome = await _oauth
            .ExchangeCodeAsync(_options.ClientId, clientSecret, callback.Code!, _options.RedirectUri, verifier.Verifier, none)
            .ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case MondayTokenOutcomeKind.Rejected:
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.ExchangeRejected);
            case MondayTokenOutcomeKind.Transient or MondayTokenOutcomeKind.Ambiguous:
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.ExchangeFailed);
            case MondayTokenOutcomeKind.Unusable:
                await _keeper.RevokeIssuedAsync(clientSecret, outcome).ConfigureAwait(false);
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.GrantUnusable);
        }

        var tokens = outcome.Tokens!;
        if (!MondayScopePolicy.IsPermittedGrant(tokens.GrantedScopes))
        {
            return await DiscardAsync(clientSecret, tokens, ConnectorAuthorizationErrors.ScopeRefused).ConfigureAwait(false);
        }

        var (identity, _) = await _identityReader.ReadAuthorizingIdentityAsync(tokens.AccessToken, none).ConfigureAwait(false);
        if (identity is null)
        {
            return await DiscardAsync(clientSecret, tokens, ConnectorAuthorizationErrors.IdentityUnverified).ConfigureAwait(false);
        }

        if (existing is not null
            && (existing.ProviderSubject != identity.UserId || existing.ProviderAccount != identity.AccountId))
        {
            return await DiscardAsync(clientSecret, tokens, ConnectorAuthorizationErrors.IdentityMismatch).ConfigureAwait(false);
        }

        // The pair is stored before any row points at it.
        var version = Guid.CreateVersion7();
        var material = new MondayTokenMaterial(tenant, transaction.ConnectionId, version, identity.UserId, identity.AccountId, tokens);
        if (!await _keeper.WriteTokensAsync(material, none).ConfigureAwait(false))
        {
            await _keeper.DeleteTokensAsync(tenant, transaction.ConnectionId, version).ConfigureAwait(false);
            return await DiscardAsync(clientSecret, tokens, ConnectorAuthorizationErrors.StorageFailed).ConfigureAwait(false);
        }

        var grant = new ConnectorAuthorizationGrant(
            transaction.ConnectionId, SourceSystem, user, identity.UserId, identity.AccountId, version, tokens.AccessExpiresAt, tokens.GrantedScopes);
        var activated = existing is null
            ? await _store.ActivateAsync(grant, none).ConfigureAwait(false)
            : await _store.ReauthorizeAsync(grant, none).ConfigureAwait(false);

        var delegated = new ConnectorIdentity(tenant, transaction.ConnectionId, ConnectorIdentityKind.DelegatedUser, identity.UserId);
        if (activated.IsSuccess && activated.Value)
        {
            if (existing is not null)
            {
                await _keeper.DeleteTokensAsync(tenant, existing.ConnectionId, existing.MaterialVersion).ConfigureAwait(false);
            }

            return Result<ConnectorIdentity>.Success(delegated);
        }

        if (activated.IsFailure)
        {
            // Whether the write committed is unknown: only a fresh read decides whether the pair became current.
            var reread = await _store.ReadAsync(transaction.ConnectionId, none).ConfigureAwait(false);
            if (reread.IsFailure)
            {
                // The pair may be current; it is kept, and nothing is claimed about it.
                return Result<ConnectorIdentity>.Failure(ConnectorAuthorizationErrors.StorageFailed);
            }

            if (reread.Value is { } current && current.MaterialVersion == version && current.CanonicalUser == user)
            {
                if (existing is not null)
                {
                    await _keeper.DeleteTokensAsync(tenant, existing.ConnectionId, existing.MaterialVersion).ConfigureAwait(false);
                }

                return Result<ConnectorIdentity>.Success(delegated);
            }
        }

        await _keeper.DeleteTokensAsync(tenant, transaction.ConnectionId, version).ConfigureAwait(false);
        return await DiscardAsync(
                clientSecret,
                tokens,
                activated.IsFailure ? ConnectorAuthorizationErrors.StorageFailed
                : existing is null ? ConnectorAuthorizationErrors.UserNotEligible
                : ConnectorAuthorizationErrors.NotReauthorizable)
            .ConfigureAwait(false);
    }

    // Revokes a pair that will not be kept, then reports why.
    private async Task<Result<ConnectorIdentity>> DiscardAsync(string clientSecret, MondayIssuedTokens tokens, Error error)
    {
        _ = await _keeper.RevokePairAsync(clientSecret, tokens.RefreshToken, tokens.AccessToken).ConfigureAwait(false);
        return Result<ConnectorIdentity>.Failure(error);
    }

    // Revokes and destroys one stored version. True when there was nothing to revoke, or monday confirmed it.
    private async Task<bool> RevokeAndDestroyAsync(TenantId tenant, Guid connectionId, Guid version, bool current)
    {
        var (status, material) = await _keeper.ReadTokensAsync(tenant, connectionId, version, CancellationToken.None).ConfigureAwait(false);

        bool confirmed;
        if (material is not null)
        {
            var clientSecret = await _keeper.ReadClientSecretAsync(CancellationToken.None).ConfigureAwait(false);
            confirmed = clientSecret is not null
                && await _keeper.RevokePairAsync(clientSecret, material.Tokens.RefreshToken, material.Tokens.AccessToken).ConfigureAwait(false);
        }
        else
        {
            // A pending version that was never written held nothing; a missing, unreadable or malformed current version
            // leaves the revocation unconfirmed.
            confirmed = status == MondaySecretStoreStatus.NotFound && !current;
        }

        await _keeper.DeleteTokensAsync(tenant, connectionId, version).ConfigureAwait(false);
        return confirmed;
    }

    // Closes the tenant's expired, unconsumed transactions, then deletes the verifiers the store lists as due, each by
    // its own transaction's name, recording each confirmed deletion.
    private async Task<Result<MondayVerifierCleanup>> CleanUpVerifiersAsync(TenantId tenant, int limit, CancellationToken cancellationToken)
    {
        _ = await _store.CloseExpiredTransactionsAsync(limit, cancellationToken).ConfigureAwait(false);
        var due = await _store.ListVerifierCleanupAsync(limit, cancellationToken).ConfigureAwait(false);
        if (due.IsFailure)
        {
            return Result<MondayVerifierCleanup>.Failure(ConnectorAuthorizationErrors.StorageFailed);
        }

        var deleted = 0;
        foreach (var transactionId in due.Value)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (await DeleteVerifierAsync(tenant, transactionId).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return Result<MondayVerifierCleanup>.Success(new MondayVerifierCleanup(deleted, due.Value.Count - deleted));
    }

    // Deletes one transaction's verifier and records the deletion once the storage confirms the verifier is gone.
    // Whether the transaction was consumed never changes here.
    private async Task<bool> DeleteVerifierAsync(TenantId tenant, Guid transactionId)
    {
        if (!await _keeper.DeleteConfirmedAsync(MondaySecretName.ForVerifier(tenant, transactionId)).ConfigureAwait(false))
        {
            return false;
        }

        var recorded = await _store.RecordVerifierDeletedAsync(transactionId, CancellationToken.None).ConfigureAwait(false);
        return recorded.IsSuccess;
    }

    private Uri AuthorizationAddress(string state, string challenge)
    {
        var query = string.Join(
            '&',
            Parameter("response_type", "code"),
            Parameter("client_id", _options.ClientId),
            Parameter("redirect_uri", _options.RedirectUri.AbsoluteUri),
            Parameter("scope", _options.Scopes),
            Parameter("state", state),
            Parameter("code_challenge", challenge),
            Parameter("code_challenge_method", MondayPkce.ChallengeMethod));

        return new Uri(MondayEndpoints.Authorize.AbsoluteUri + "?" + query, UriKind.Absolute);

        static string Parameter(string name, string value) => name + "=" + Uri.EscapeDataString(value);
    }

    private static bool IsReauthorizable(ConnectorAuthorization authorization, UserId user) =>
        authorization.State == ConnectorAuthorizationState.ReauthorizationRequired
        && authorization.CanonicalUser == user
        && authorization.ConnectionEnabled
        && authorization.CanonicalUserActive
        && authorization.SourceSystem == SourceSystem;

    private static bool IsCode(string? code) =>
        code is { Length: > 0 and <= MaxCodeLength } && code.All(character => character is >= '!' and <= '~');
}
