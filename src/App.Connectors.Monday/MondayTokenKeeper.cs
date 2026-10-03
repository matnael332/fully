using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The secret-storage and revocation steps the authorizer and the credential source share: reading the client secret,
/// keeping and destroying credential versions, and revoking tokens.
/// </summary>
/// <remarks>
/// Deleting and revoking are best effort and never throw: they run on paths that are already failing closed, where a
/// second failure must not hide the first. A secret is never returned in anything but the connector's own internal
/// types, and nothing here logs.
/// </remarks>
internal sealed class MondayTokenKeeper
{
    private readonly IMondaySecretStore _secrets;
    private readonly MondayOAuthClient _oauth;
    private readonly string _clientId;

    internal MondayTokenKeeper(IMondaySecretStore secrets, MondayOAuthClient oauth, string clientId)
    {
        _secrets = secrets;
        _oauth = oauth;
        _clientId = clientId;
    }

    /// <summary>The monday app's client secret, or null when it is missing, unreadable or malformed.</summary>
    internal async ValueTask<string?> ReadClientSecretAsync(CancellationToken cancellationToken)
    {
        var lookup = await _secrets.GetAsync(MondaySecretName.ClientSecret, cancellationToken).ConfigureAwait(false);
        return lookup.Status == MondaySecretStoreStatus.Succeeded ? MondayClientSecret.Read(lookup.Value) : null;
    }

    /// <summary>
    /// Reads one credential version. A value that exists but is malformed or bound elsewhere comes back as
    /// <see cref="MondaySecretStoreStatus.Succeeded"/> with no material.
    /// </summary>
    internal async ValueTask<(MondaySecretStoreStatus Status, MondayTokenMaterial? Material)> ReadTokensAsync(
        TenantId tenant,
        Guid connectionId,
        Guid version,
        CancellationToken cancellationToken)
    {
        var lookup = await _secrets
            .GetAsync(MondaySecretName.ForTokens(tenant, connectionId, version), cancellationToken)
            .ConfigureAwait(false);

        return lookup.Status == MondaySecretStoreStatus.Succeeded
            ? (lookup.Status, MondayTokenMaterial.Read(lookup.Value, tenant, connectionId, version))
            : (lookup.Status == MondaySecretStoreStatus.NotFound ? MondaySecretStoreStatus.NotFound : MondaySecretStoreStatus.Unavailable, null);
    }

    /// <summary>Stores one credential version. True only when storage confirms it.</summary>
    internal async ValueTask<bool> WriteTokensAsync(MondayTokenMaterial material, CancellationToken cancellationToken) =>
        await _secrets
            .SetAsync(MondaySecretName.ForTokens(material.Tenant, material.ConnectionId, material.Version), material.ToBytes(), cancellationToken)
            .ConfigureAwait(false) == MondaySecretStoreStatus.Succeeded;

    /// <summary>Deletes one credential version, best effort.</summary>
    internal ValueTask DeleteTokensAsync(TenantId tenant, Guid connectionId, Guid version) =>
        DeleteQuietlyAsync(MondaySecretName.ForTokens(tenant, connectionId, version));

    /// <summary>
    /// Deletes a secret and says whether it is confirmed gone: deleted now, or already absent. A storage failure,
    /// raised or reported, is never confirmation, so the caller keeps the deletion due.
    /// </summary>
    internal async ValueTask<bool> DeleteConfirmedAsync(MondaySecretName name)
    {
        try
        {
            return await _secrets.DeleteAsync(name, CancellationToken.None).ConfigureAwait(false)
                is MondaySecretStoreStatus.Succeeded or MondaySecretStoreStatus.NotFound;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Deletes a secret, best effort: a failure leaves an unreferenced value and is never raised.</summary>
    internal async ValueTask DeleteQuietlyAsync(MondaySecretName name)
    {
        try
        {
            _ = await _secrets.DeleteAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort on an already failing path; the value stays unreferenced.
        }
    }

    /// <summary>
    /// Revokes a pair at monday, the refresh token first, one attempt each. True only when monday confirms both.
    /// </summary>
    internal async ValueTask<bool> RevokePairAsync(string clientSecret, string? refreshToken, string? accessToken)
    {
        var refreshRevoked = refreshToken is null || await RevokeQuietlyAsync(clientSecret, refreshToken, "refresh_token").ConfigureAwait(false);
        var accessRevoked = accessToken is null || await RevokeQuietlyAsync(clientSecret, accessToken, "access_token").ConfigureAwait(false);
        return refreshRevoked && accessRevoked;
    }

    /// <summary>Revokes whatever tokens a discarded answer carried, best effort.</summary>
    internal async ValueTask RevokeIssuedAsync(string clientSecret, MondayTokenOutcome outcome) =>
        _ = await RevokePairAsync(clientSecret, outcome.IssuedRefreshToken, outcome.IssuedAccessToken).ConfigureAwait(false);

    private async ValueTask<bool> RevokeQuietlyAsync(string clientSecret, string token, string tokenTypeHint)
    {
        try
        {
            return await _oauth.RevokeAsync(_clientId, clientSecret, token, tokenTypeHint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
