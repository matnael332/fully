using System.Net;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Harness.Monday.Secrets;

namespace WorkContinuity.Harness.Monday.Live;

/// <summary>
/// Stage B2 (owner decisions B-1, B-10 and B-11 of 2026-10-06): the pinned network transport for the three destinations a
/// future approved live run would reach, each behind a destination guard of its own. Authored offline only.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the harness composes this type: no host, entry point, option, gate or environment variable names it, so the
/// default executable still cannot reach a network. Its only callers are the offline tests, which never send through the
/// network handler it builds. Enabling it is a later, separately approved and reviewed code change, never configuration.
/// </para>
/// <para>
/// Every destination gets its own handler, so connections, and any credential carried on them, never cross from one
/// destination to another. The handler is pinned: no redirect is followed, no cookie is kept, no proxy and no ambient
/// credential is used, nothing is pre-authenticated, the platform validates certificates as it always does, a connection
/// attempt is bounded, and no trace header is propagated to an external host.
/// </para>
/// </remarks>
public static class LiveTransport
{
    /// <summary>The bound of a connection attempt.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The bound of a response's headers, in kilobytes.</summary>
    public const int MaxResponseHeadersKilobytes = 64;

    /// <summary>The pinned transport to the Entra token endpoint of one tenant, behind its destination guard.</summary>
    /// <param name="tenant">The Entra tenant id.</param>
    public static HttpMessageHandler ForEntraToken(Guid tenant) => new EntraTokenDestination(tenant, CreatePinnedHandler());

    /// <summary>The pinned transport to one Key Vault, behind its destination guard.</summary>
    /// <param name="vault">The vault's origin, as <see cref="KeyVaultSecretStore.IsVaultOrigin"/> accepts it.</param>
    public static HttpMessageHandler ForKeyVault(Uri vault) => new KeyVaultDestination(vault, CreatePinnedHandler());

    /// <summary>The pinned transport to the connector's three fixed monday endpoints, behind its destination guard.</summary>
    public static HttpMessageHandler ForMonday() => new MondayDestination(CreatePinnedHandler());

    // The one place a network handler is built.
    internal static SocketsHttpHandler CreatePinnedHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = MaxResponseHeadersKilobytes,
        ActivityHeadersPropagator = null,
    };
}

/// <summary>
/// A destination guard: it passes a request to its transport only when the request is exactly what its destination
/// accepts, and refuses everything else before the transport sees it, as a connection failure that sent nothing.
/// </summary>
internal abstract class DestinationGuard : DelegatingHandler
{
    private protected DestinationGuard(HttpMessageHandler transport)
        : base(transport)
    {
    }

    /// <inheritdoc />
    protected sealed override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Admits(request))
        {
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(HttpRequestError.ConnectionError, "The request is not one this destination accepts; nothing was sent."));
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Never names an address, a header or a value.</summary>
    public sealed override string ToString() => GetType().Name;

    /// <summary>Whether the request is exactly one this destination accepts.</summary>
    private protected abstract bool Admits(HttpRequestMessage request);

    // HTTPS on the default port, with no user information or fragment, and no cookie on the request.
    private protected static bool IsPlainHttpsRequest(HttpRequestMessage request) =>
        request.RequestUri is { IsAbsoluteUri: true } address
        && address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort
        && address.Port == 443
        && address.UserInfo.Length == 0
        && address.Fragment.Length == 0
        && !request.Headers.Contains("Cookie");
}

/// <summary>Entra: only the token request of one tenant, a form POST that carries no Authorization header.</summary>
internal sealed class EntraTokenDestination : DestinationGuard
{
    private readonly string _endpoint;

    internal EntraTokenDestination(Guid tenant, HttpMessageHandler transport)
        : base(transport)
    {
        if (tenant == Guid.Empty)
        {
            throw new ArgumentException("The tenant id must not be empty.", nameof(tenant));
        }

        _endpoint = EntraCertificateTokenSource.TokenEndpointFor(tenant).AbsoluteUri;
    }

    private protected override bool Admits(HttpRequestMessage request) =>
        IsPlainHttpsRequest(request)
        && request.Method == HttpMethod.Post
        && string.Equals(request.RequestUri!.AbsoluteUri, _endpoint, StringComparison.Ordinal)
        && request.RequestUri.Query.Length == 0
        && request.Headers.Authorization is null
        && request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded";
}

/// <summary>
/// Key Vault: only the adapter's get, set and delete of one secret name on one vault, with the pinned API version and a
/// bearer credential. A form POST, the shape of a token request, can never pass.
/// </summary>
internal sealed partial class KeyVaultDestination : DestinationGuard
{
    private const string Query = "?api-version=" + KeyVaultSecretStore.ApiVersion;

    private readonly string _host;

    internal KeyVaultDestination(Uri vault, HttpMessageHandler transport)
        : base(transport)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!KeyVaultSecretStore.IsVaultOrigin(vault))
        {
            throw new ArgumentException("The vault address must be https://{vault-name}.vault.azure.net/ and nothing more.", nameof(vault));
        }

        _host = vault.Host;
    }

    private protected override bool Admits(HttpRequestMessage request)
    {
        if (!IsPlainHttpsRequest(request)
            || !string.Equals(request.RequestUri!.Host, _host, StringComparison.Ordinal)
            || !string.Equals(request.RequestUri.Query, Query, StringComparison.Ordinal)
            || request.Headers.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 })
        {
            return false;
        }

        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Get)
        {
            return request.Content is null && ReadPath().IsMatch(path);
        }

        if (request.Method == HttpMethod.Delete)
        {
            return request.Content is null && WritePath().IsMatch(path);
        }

        return request.Method == HttpMethod.Put
            && WritePath().IsMatch(path)
            && request.Content?.Headers.ContentType?.MediaType == "application/json";
    }

    [GeneratedRegex(@"^/secrets/[a-z0-9-]{1,127}/$", RegexOptions.CultureInvariant)]
    private static partial Regex ReadPath();

    [GeneratedRegex(@"^/secrets/[a-z0-9-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex WritePath();
}

/// <summary>monday: only POSTs to the connector's three fixed endpoints, as the connector itself sends them.</summary>
internal sealed class MondayDestination : DestinationGuard
{
    internal MondayDestination(HttpMessageHandler transport)
        : base(transport)
    {
    }

    private protected override bool Admits(HttpRequestMessage request) =>
        IsPlainHttpsRequest(request)
        && request.Method == HttpMethod.Post
        && request.RequestUri!.Query.Length == 0
        && (IsExactly(request.RequestUri, MondayEndpoints.Token)
            || IsExactly(request.RequestUri, MondayEndpoints.Revoke)
            || IsExactly(request.RequestUri, MondayEndpoints.Api));

    private static bool IsExactly(Uri address, Uri endpoint) => string.Equals(address.AbsoluteUri, endpoint.AbsoluteUri, StringComparison.Ordinal);
}
