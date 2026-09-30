using System.Security.Cryptography;
using System.Text;
using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The server-side configuration of the monday OAuth 2.1 connection lifecycle (the "New OAuth Flow").
/// </summary>
/// <remarks>
/// <para>
/// Everything here is server configuration, never browser input: the redirect address a callback must return to, the
/// scopes requested, and the timings. The client id is public; the client secret is not here and never in
/// configuration: it is read from the connector's secret storage when a token call needs it (decision D3B-2).
/// </para>
/// <para>
/// The redirect address must be an absolute HTTPS address without user information or fragment, exactly as registered
/// in the monday app. The transaction lifetime can never exceed monday's 10-minute code validity.
/// </para>
/// </remarks>
public sealed class MondayOAuthOptions
{
    /// <summary>The longest accepted client id.</summary>
    public const int MaxClientIdLength = 200;

    private readonly byte[] _redirectHash;

    /// <summary>Creates the options.</summary>
    /// <param name="clientId">The monday app's client id.</param>
    /// <param name="redirectUri">The redirect address registered in the monday app.</param>
    /// <param name="scopes">The scopes to request; <see cref="MondayScopePolicy.DefaultScopes"/> when omitted.</param>
    /// <param name="transactionLifetime">How long a started authorization stays usable: 1 to 10 minutes; 10 by default.</param>
    /// <param name="refreshMargin">How long before expiry a credential is renewed: 1 to 30 minutes; 5 by default.</param>
    /// <param name="refreshClaimTimeout">When a renewal's claim counts as abandoned: 30 seconds to 10 minutes; 2 by default.</param>
    /// <param name="httpTimeout">The bound of one OAuth call: 1 to 60 seconds; 30 by default.</param>
    /// <exception cref="ArgumentException">A value is missing, malformed or out of range.</exception>
    public MondayOAuthOptions(
        string clientId,
        Uri redirectUri,
        string? scopes = null,
        TimeSpan? transactionLifetime = null,
        TimeSpan? refreshMargin = null,
        TimeSpan? refreshClaimTimeout = null,
        TimeSpan? httpTimeout = null)
    {
        if (string.IsNullOrEmpty(clientId)
            || clientId.Length > MaxClientIdLength
            || clientId.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("The monday client id must be 1-200 visible ASCII characters.", nameof(clientId));
        }

        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsAbsoluteUri
            || redirectUri.Scheme != Uri.UriSchemeHttps
            || redirectUri.UserInfo.Length > 0
            || redirectUri.Fragment.Length > 0
            || string.IsNullOrEmpty(redirectUri.Host))
        {
            throw new ArgumentException(
                "The redirect address must be an absolute HTTPS address without user information or fragment.",
                nameof(redirectUri));
        }

        var requested = scopes ?? MondayScopePolicy.DefaultScopes;
        if (!MondayScopePolicy.IsPermittedRequest(requested))
        {
            throw new ArgumentException(
                "The requested scopes must be allowlisted monday read scopes including the required ones.",
                nameof(scopes));
        }

        TransactionLifetime = InRange(
            transactionLifetime ?? ConnectorAuthorizationTransaction.MaxLifetime,
            TimeSpan.FromMinutes(1),
            ConnectorAuthorizationTransaction.MaxLifetime,
            nameof(transactionLifetime));
        RefreshMargin = InRange(refreshMargin ?? TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), nameof(refreshMargin));
        RefreshClaimTimeout = InRange(
            refreshClaimTimeout ?? TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(10),
            nameof(refreshClaimTimeout));
        HttpTimeout = InRange(httpTimeout ?? TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), nameof(httpTimeout));

        if (RefreshClaimTimeout <= HttpTimeout)
        {
            throw new ArgumentException(
                "A renewal's claim must outlive one OAuth call, or a live renewal could be judged abandoned.",
                nameof(refreshClaimTimeout));
        }

        ClientId = clientId;
        RedirectUri = redirectUri;
        Scopes = ConnectorScopes.Normalize(ConnectorScopes.Parse(requested));
        _redirectHash = SHA256.HashData(Encoding.UTF8.GetBytes(redirectUri.AbsoluteUri));
    }

    /// <summary>Gets the monday app's client id.</summary>
    public string ClientId { get; }

    /// <summary>Gets the registered redirect address.</summary>
    public Uri RedirectUri { get; }

    /// <summary>Gets the canonical list of requested scopes.</summary>
    public string Scopes { get; }

    /// <summary>Gets how long a started authorization stays usable.</summary>
    public TimeSpan TransactionLifetime { get; }

    /// <summary>Gets how long before expiry a credential is renewed.</summary>
    public TimeSpan RefreshMargin { get; }

    /// <summary>Gets when a renewal's claim counts as abandoned.</summary>
    public TimeSpan RefreshClaimTimeout { get; }

    /// <summary>Gets the bound of one OAuth call.</summary>
    public TimeSpan HttpTimeout { get; }

    /// <summary>Gets the SHA-256 of the redirect address, as a started authorization records it.</summary>
    internal ReadOnlySpan<byte> RedirectHash => _redirectHash;

    private static TimeSpan InRange(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentException($"The value must lie between {minimum} and {maximum}.", name);
}
