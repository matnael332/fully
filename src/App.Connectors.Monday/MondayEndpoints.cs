namespace WorkContinuity.Connectors.Monday;

/// <summary>The monday endpoints this connector calls.</summary>
/// <remarks>
/// Every endpoint is a fixed constant, never read from configuration or from provider data, so no request can be steered
/// to another host. An architecture test keeps this the only place the monday hosts are written. The OAuth 2.1
/// endpoints and the issuer are those of monday's authorization server metadata
/// (<c>/oauth_ms/.well-known/oauth-authorization-server</c>), reverified on 2026-09-30.
/// </remarks>
public static class MondayEndpoints
{
    /// <summary>monday's OAuth 2.1 issuer identifier, compared exactly with an <c>iss</c> authorization-response parameter.</summary>
    public const string Issuer = "https://auth.monday.com";

    /// <summary>Gets monday's GraphQL API endpoint.</summary>
    public static Uri Api { get; } = new("https://api.monday.com/v2", UriKind.Absolute);

    /// <summary>Gets monday's OAuth 2.1 authorization endpoint, where the user's browser is sent.</summary>
    public static Uri Authorize { get; } = new("https://auth.monday.com/oauth2/authorize", UriKind.Absolute);

    /// <summary>Gets monday's OAuth 2.1 token endpoint, for the code exchange and the refresh.</summary>
    public static Uri Token { get; } = new("https://auth.monday.com/oauth_ms/oauth/token", UriKind.Absolute);

    /// <summary>Gets monday's OAuth 2.1 revocation endpoint.</summary>
    public static Uri Revoke { get; } = new("https://auth.monday.com/oauth_ms/oauth/revoke", UriKind.Absolute);
}
