using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The credential source of an authorized monday connection (Gate 3B): access-time checks before every attempt, and
/// single-flight, crash-safe renewal of the rotating OAuth 2.1 token pair.
/// </summary>
/// <remarks>
/// <para>
/// Every acquisition rereads the connection's authorization in the trusted tenant and fails closed, before anything is
/// sent, when the connection is disabled or revoked, when reauthorization is required, when the delegating user is no
/// longer active, or when the stored state or credential belongs to another tenant, connection or identity. A usable
/// credential always carries the connection's delegated identity (decision D3B-1).
/// </para>
/// <para>
/// A credential is renewed when fewer than the configured margin (5 minutes by default) remain, as monday recommends,
/// or after monday rejected it. Renewal is single-flight across every instance: a caller first claims it by a
/// compare-and-swap on the authorization's generation, and only the winner calls monday. The winner writes the new pair
/// to secret storage under the claim's id before anything points at it, then advances the generation by a second
/// compare-and-swap, then deletes the old version. Other callers keep using the current token while it stays valid,
/// and otherwise wait briefly for the winner.
/// </para>
/// <para>
/// monday rotates the refresh token on every refresh, so an old refresh token is never used again once a refresh may
/// have happened. Only an answer that provably changed nothing (a 429, or a request that never left) releases the claim.
/// A rejection, an ambiguous outcome (a timeout after sending, a 5xx, an unreadable success), a grant beyond the
/// read-only scopes, or a new pair that cannot be stored moves the connection to reauthorization required. A claim older
/// than its timeout belongs to a renewal that stopped: if that renewal stored its new pair, the pair is adopted, since
/// it is the latest monday issued; otherwise the connection requires reauthorization. Once a renewal is claimed it runs
/// to a durable end, so the caller's cancellation no longer applies to it. Nothing here logs, and no failure carries a
/// token or provider text.
/// </para>
/// </remarks>
public sealed class MondayConnectionCredentialSource : IMondayCredentialSource
{
    /// <summary>The most reads of the authorization one acquisition makes, waits for another renewal included.</summary>
    internal const int MaxReads = 8;

    /// <summary>How long a caller waits between reads while another caller renews.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The least validity for which a caller keeps using the current token while another caller renews.</summary>
    internal static readonly TimeSpan MinimumRemaining = TimeSpan.FromSeconds(30);

    private readonly IConnectorAuthorizationStore _store;
    private readonly MondayTokenKeeper _keeper;
    private readonly MondayOAuthClient _oauth;
    private readonly MondayOAuthOptions _options;
    private readonly IClock _clock;
    private readonly IMondayDelay _delay;

    /// <summary>Creates the source.</summary>
    /// <param name="store">The tenant-scoped authorization store.</param>
    /// <param name="secrets">The connector's secret storage.</param>
    /// <param name="options">The OAuth configuration.</param>
    /// <param name="authorizationHttp">The HTTP client for monday's token and revocation endpoints; its handler must not follow redirects.</param>
    /// <param name="clock">The clock that decides when a credential needs renewal.</param>
    /// <param name="delay">The wait between reads while another caller renews.</param>
    public MondayConnectionCredentialSource(
        IConnectorAuthorizationStore store,
        IMondaySecretStore secrets,
        MondayOAuthOptions options,
        HttpClient authorizationHttp,
        IClock clock,
        IMondayDelay delay)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizationHttp);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delay);

        _store = store;
        _options = options;
        _clock = clock;
        _delay = delay;
        _oauth = new MondayOAuthClient(authorizationHttp, clock, options.HttpTimeout);
        _keeper = new MondayTokenKeeper(secrets, _oauth, options.ClientId);
    }

    /// <inheritdoc />
    public ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken) =>
        AcquireAsync(context, rejectedGeneration: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
        MondayRequestContext context,
        MondayAccessCredential rejected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        return AcquireAsync(context, rejected.Generation, cancellationToken);
    }

    private async ValueTask<MondayCredentialResult> AcquireAsync(
        MondayRequestContext context,
        long? rejectedGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var read = 1; read <= MaxReads; read++)
        {
            var loaded = await _store.ReadAsync(context.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure || loaded.Value is not { } authorization)
            {
                return MondayCredentialResult.Unavailable();
            }

            if (authorization.Tenant != context.Tenant
                || authorization.ConnectionId != context.ConnectionId
                || authorization.SourceSystem != MondayConnectionAuthorizer.SourceSystem)
            {
                return MondayCredentialResult.Mismatch();
            }

            if (Refusal(authorization) is { } refused)
            {
                return refused;
            }

            // After a rejection, renew unless another caller already moved past the rejected generation. A rejected
            // credential of unknown generation is taken to be the one current at the first read.
            if (rejectedGeneration == 0)
            {
                rejectedGeneration = authorization.Generation;
            }

            var now = _clock.UtcNow;
            var renewalDue = rejectedGeneration is { } rejected
                ? authorization.Generation <= rejected
                : authorization.AccessExpiresAt - now <= _options.RefreshMargin;

            if (!renewalDue)
            {
                if (await CurrentAsync(authorization, cancellationToken).ConfigureAwait(false) is { } current)
                {
                    return current;
                }

                continue;
            }

            if (authorization.RefreshClaim is { } claim)
            {
                if (authorization.RefreshClaimAge >= _options.RefreshClaimTimeout)
                {
                    if (await ResolveAbandonedAsync(authorization, claim).ConfigureAwait(false) is { } undecided)
                    {
                        return undecided;
                    }

                    continue;
                }

                // Another caller is renewing: the current token serves while it stays valid; otherwise wait for the renewal.
                if (rejectedGeneration is null && authorization.AccessExpiresAt - now > MinimumRemaining)
                {
                    if (await CurrentAsync(authorization, cancellationToken).ConfigureAwait(false) is { } current)
                    {
                        return current;
                    }

                    continue;
                }

                if (read < MaxReads)
                {
                    await _delay.DelayAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var newClaim = Guid.CreateVersion7();
            var claimed = await _store
                .ClaimRefreshAsync(authorization.ConnectionId, authorization.Generation, newClaim, cancellationToken)
                .ConfigureAwait(false);
            if (claimed.IsFailure)
            {
                return MondayCredentialResult.Unavailable();
            }

            // A lost claim means another caller got there first or the state moved: read again.
            if (claimed.Value && await RenewClaimedAsync(authorization, newClaim).ConfigureAwait(false) is { } renewed)
            {
                return renewed;
            }
        }

        return MondayCredentialResult.Unavailable();
    }

    // The access-time checks, in the order they are reported.
    private static MondayCredentialResult? Refusal(ConnectorAuthorization authorization) =>
        authorization.State == ConnectorAuthorizationState.Revoked ? MondayCredentialResult.Revoked()
        : !authorization.ConnectionEnabled ? MondayCredentialResult.ConnectionDisabled()
        : authorization.State != ConnectorAuthorizationState.Active ? MondayCredentialResult.ReauthorizationRequired()
        : !authorization.CanonicalUserActive ? MondayCredentialResult.Revoked()
        : null;

    // The current credential, or null when the authorization moved underneath and must be read again.
    private async ValueTask<MondayCredentialResult?> CurrentAsync(ConnectorAuthorization authorization, CancellationToken cancellationToken)
    {
        var (status, material) = await _keeper
            .ReadTokensAsync(authorization.Tenant, authorization.ConnectionId, authorization.MaterialVersion, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case MondaySecretStoreStatus.Unavailable:
                return MondayCredentialResult.Unavailable();

            case MondaySecretStoreStatus.NotFound:
                // Either a renewal just replaced this version, or the credential is gone. Only the second makes the
                // compare-and-swap succeed, and then nothing is left to use.
                var required = await _store
                    .RequireReauthorizationAsync(authorization.ConnectionId, authorization.Generation, authorization.RefreshClaim, CancellationToken.None)
                    .ConfigureAwait(false);
                return required.IsSuccess && required.Value ? MondayCredentialResult.ReauthorizationRequired() : null;
        }

        if (material is null
            || material.Subject != authorization.ProviderSubject
            || material.Account != authorization.ProviderAccount)
        {
            return MondayCredentialResult.Mismatch();
        }

        return MondayCredentialResult.Acquired(Credential(authorization, material.Tokens.AccessToken, authorization.AccessExpiresAt, authorization.Generation));
    }

    // Runs a claimed renewal to a durable end. Null when the authorization moved underneath and must be read again.
    private async ValueTask<MondayCredentialResult?> RenewClaimedAsync(ConnectorAuthorization authorization, Guid claim)
    {
        var none = CancellationToken.None;

        var (status, material) = await _keeper
            .ReadTokensAsync(authorization.Tenant, authorization.ConnectionId, authorization.MaterialVersion, none)
            .ConfigureAwait(false);
        if (status == MondaySecretStoreStatus.Unavailable)
        {
            await ReleaseAsync(authorization, claim).ConfigureAwait(false);
            return MondayCredentialResult.Unavailable();
        }

        if (material is null
            || material.Subject != authorization.ProviderSubject
            || material.Account != authorization.ProviderAccount)
        {
            // Nothing trustworthy is left to renew from.
            await RequireReauthorizationAsync(authorization, claim).ConfigureAwait(false);
            return status == MondaySecretStoreStatus.NotFound
                ? MondayCredentialResult.ReauthorizationRequired()
                : MondayCredentialResult.Mismatch();
        }

        var clientSecret = await _keeper.ReadClientSecretAsync(none).ConfigureAwait(false);
        if (clientSecret is null)
        {
            await ReleaseAsync(authorization, claim).ConfigureAwait(false);
            return MondayCredentialResult.Unavailable();
        }

        var outcome = await _oauth
            .RefreshAsync(_options.ClientId, clientSecret, material.Tokens.RefreshToken, none)
            .ConfigureAwait(false);

        if (outcome.Kind == MondayTokenOutcomeKind.Transient)
        {
            // Provably unchanged: the current pair stays valid, and a later call may renew.
            await ReleaseAsync(authorization, claim).ConfigureAwait(false);
            return MondayCredentialResult.Deferred(outcome.Failure!);
        }

        if (outcome.Kind != MondayTokenOutcomeKind.Issued || !MondayScopePolicy.IsPermittedGrant(outcome.Tokens!.GrantedScopes))
        {
            // Rejected, ambiguous, unusable, or beyond the read-only scopes: the old refresh token may already be
            // rotated and is never used again. Anything this answer issued is discarded.
            await _keeper.RevokeIssuedAsync(clientSecret, outcome).ConfigureAwait(false);
            await RequireReauthorizationAsync(authorization, claim).ConfigureAwait(false);
            return MondayCredentialResult.ReauthorizationRequired();
        }

        return await StoreRenewalAsync(authorization, claim, outcome.Tokens, clientSecret).ConfigureAwait(false);
    }

    private async ValueTask<MondayCredentialResult?> StoreRenewalAsync(
        ConnectorAuthorization authorization,
        Guid claim,
        MondayIssuedTokens tokens,
        string clientSecret)
    {
        var none = CancellationToken.None;
        var material = new MondayTokenMaterial(
            authorization.Tenant, authorization.ConnectionId, claim, authorization.ProviderSubject, authorization.ProviderAccount, tokens);

        // The new pair is kept before anything points at it.
        if (!await _keeper.WriteTokensAsync(material, none).ConfigureAwait(false))
        {
            // The old pair is rotated and the new one cannot be kept: nothing usable remains.
            await _keeper.RevokePairAsync(clientSecret, tokens.RefreshToken, tokens.AccessToken).ConfigureAwait(false);
            await _keeper.DeleteTokensAsync(authorization.Tenant, authorization.ConnectionId, claim).ConfigureAwait(false);
            await RequireReauthorizationAsync(authorization, claim).ConfigureAwait(false);
            return MondayCredentialResult.ReauthorizationRequired();
        }

        for (var completion = 1; completion <= 2; completion++)
        {
            var completed = await _store
                .CompleteRefreshAsync(authorization.ConnectionId, authorization.Generation, claim, tokens.AccessExpiresAt, tokens.GrantedScopes, none)
                .ConfigureAwait(false);
            if (completed.IsSuccess && completed.Value)
            {
                await _keeper.DeleteTokensAsync(authorization.Tenant, authorization.ConnectionId, authorization.MaterialVersion).ConfigureAwait(false);
                return MondayCredentialResult.Acquired(Credential(authorization, tokens.AccessToken, tokens.AccessExpiresAt, authorization.Generation + 1));
            }

            // The row did not move as expected, or whether it moved is unknown: only a fresh read decides.
            var reread = await _store.ReadAsync(authorization.ConnectionId, none).ConfigureAwait(false);
            if (reread.IsFailure || reread.Value is not { } current)
            {
                // The pair may be current or adoptable; it is kept, and nothing is claimed about it.
                return MondayCredentialResult.Unavailable();
            }

            if (current.MaterialVersion == claim)
            {
                // The completion committed after all, or another caller adopted this renewal's pair.
                return Refusal(current)
                    ?? MondayCredentialResult.Acquired(Credential(current, tokens.AccessToken, current.AccessExpiresAt, current.Generation));
            }

            if (completed.IsSuccess
                || current.State != ConnectorAuthorizationState.Active
                || current.Generation != authorization.Generation
                || current.RefreshClaim != claim)
            {
                // The row moved away from this renewal (disconnected, reauthorization required, or taken over), so the
                // pair will never be current: destroy it, then decide from the current state.
                await _keeper.RevokePairAsync(clientSecret, tokens.RefreshToken, tokens.AccessToken).ConfigureAwait(false);
                await _keeper.DeleteTokensAsync(authorization.Tenant, authorization.ConnectionId, claim).ConfigureAwait(false);
                return null;
            }

            // Nothing moved: the completion failed before it committed, and the claim is still this renewal's. Once more.
        }

        // The pair stays stored under the claim, so whoever finds the claim abandoned adopts it; it is never destroyed here.
        return MondayCredentialResult.Unavailable();
    }

    // A claim older than its timeout belongs to a renewal that stopped. Null when the state was settled and must be read
    // again; a result when nothing could be decided.
    private async ValueTask<MondayCredentialResult?> ResolveAbandonedAsync(ConnectorAuthorization authorization, Guid claim)
    {
        var none = CancellationToken.None;
        var (status, material) = await _keeper
            .ReadTokensAsync(authorization.Tenant, authorization.ConnectionId, claim, none)
            .ConfigureAwait(false);

        if (status == MondaySecretStoreStatus.Unavailable)
        {
            return MondayCredentialResult.Unavailable();
        }

        if (material is not null
            && material.Subject == authorization.ProviderSubject
            && material.Account == authorization.ProviderAccount
            && MondayScopePolicy.IsPermittedGrant(material.Tokens.GrantedScopes))
        {
            // The stopped renewal stored its new pair: it is the latest monday issued, so it is adopted.
            var adopted = await _store
                .CompleteRefreshAsync(
                    authorization.ConnectionId, authorization.Generation, claim, material.Tokens.AccessExpiresAt, material.Tokens.GrantedScopes, none)
                .ConfigureAwait(false);
            if (adopted.IsSuccess && adopted.Value)
            {
                await _keeper.DeleteTokensAsync(authorization.Tenant, authorization.ConnectionId, authorization.MaterialVersion).ConfigureAwait(false);
            }

            return null;
        }

        // Whether that renewal rotated the token is unknown, so the old refresh token is never used again.
        await RequireReauthorizationAsync(authorization, claim).ConfigureAwait(false);
        if (status == MondaySecretStoreStatus.Succeeded)
        {
            await _keeper.DeleteTokensAsync(authorization.Tenant, authorization.ConnectionId, claim).ConfigureAwait(false);
        }

        return null;
    }

    private async ValueTask ReleaseAsync(ConnectorAuthorization authorization, Guid claim) =>
        _ = await _store.ReleaseRefreshAsync(authorization.ConnectionId, claim, CancellationToken.None).ConfigureAwait(false);

    private async ValueTask RequireReauthorizationAsync(ConnectorAuthorization authorization, Guid claim) =>
        _ = await _store
            .RequireReauthorizationAsync(authorization.ConnectionId, authorization.Generation, claim, CancellationToken.None)
            .ConfigureAwait(false);

    private static MondayAccessCredential Credential(
        ConnectorAuthorization authorization,
        string accessToken,
        DateTimeOffset expiresAt,
        long generation) =>
        new(accessToken, authorization.DelegatedIdentity, expiresAt) { Generation = generation };
}
