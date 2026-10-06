using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WorkContinuity.Harness.Monday.Secrets;

namespace WorkContinuity.Harness.Monday.Live;

/// <summary>
/// Stage B2 (owner decisions B-1, B-7 and B-10 of 2026-10-06): the Key Vault access token of the certificate-credential
/// service principal, from the Microsoft identity platform's client-credentials grant. Authored offline only.
/// </summary>
/// <remarks>
/// <para>
/// The credential is explicit and nothing else is ever consulted: one tenant id, one client id and one certificate with its
/// private key, all given by the caller. There is no client secret, no environment, no developer tool, no managed identity
/// and no other credential chain. The request goes only to that tenant's v2.0 token endpoint, through the transport the
/// caller gives (in a future run, <see cref="LiveTransport.ForEntraToken"/>), and asks only for the Key Vault scope.
/// </para>
/// <para>
/// The answer is read fail-closed: a 200, a JSON body within the bound, exactly one <c>token_type</c> that is Bearer, one
/// <c>access_token</c> of visible ASCII within the adapter's bound, and one numeric <c>expires_in</c> longer than the
/// refresh margin and at most a day. Anything else, a failed or timed-out request, or a certificate that cannot sign now,
/// gives no token. The caller's cancellation propagates and is never turned into "no token".
/// </para>
/// <para>
/// A token is kept in memory only, in this instance, until five minutes before it expires; nothing is written anywhere.
/// One request is made at a time. Nothing here logs, and no failure quotes a token, an assertion or an answer.
/// </para>
/// </remarks>
public sealed class EntraCertificateTokenSource : IKeyVaultAccessTokenSource, IDisposable
{
    /// <summary>The Microsoft identity platform's authority host.</summary>
    public const string AuthorityHost = "https://login.microsoftonline.com/";

    /// <summary>The only scope asked for: Key Vault.</summary>
    public const string Scope = "https://vault.azure.net/.default";

    /// <summary>The grant.</summary>
    public const string GrantType = "client_credentials";

    /// <summary>The client assertion type of a certificate credential.</summary>
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>How long before its expiry a kept token is no longer used.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>The bound of one token request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxAccessTokenLength = 16 * 1024;
    private const long MaxLifetimeSeconds = 24 * 60 * 60;

    private readonly Uri _endpoint;
    private readonly Guid _client;
    private readonly X509Certificate2 _certificate;
    private readonly HttpMessageHandler _transport;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KeptToken? _kept;

    /// <summary>Creates the source.</summary>
    /// <param name="tenant">The Entra tenant id.</param>
    /// <param name="client">The service principal's client (application) id.</param>
    /// <param name="certificate">Its certificate, with an RSA private key of at least 2,048 bits. Owned by the caller.</param>
    /// <param name="transport">The handler requests are sent through. Supplied by the caller; never built here.</param>
    /// <param name="time">The clock for assertions, the request bound and the kept token.</param>
    /// <exception cref="ArgumentException">An id is empty or the certificate cannot sign assertions.</exception>
    public EntraCertificateTokenSource(Guid tenant, Guid client, X509Certificate2 certificate, HttpMessageHandler transport, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        if (tenant == Guid.Empty || client == Guid.Empty)
        {
            throw new ArgumentException("The tenant id and the client id must not be empty.");
        }

        if (!EntraClientAssertion.IsSuitable(certificate))
        {
            throw new ArgumentException("The certificate must hold an RSA private key of at least 2,048 bits.", nameof(certificate));
        }

        _endpoint = TokenEndpointFor(tenant);
        _client = client;
        _certificate = certificate;
        _transport = transport;
        _time = time;
    }

    /// <summary>The v2.0 token endpoint of one tenant, which is also the assertion's audience.</summary>
    /// <param name="tenant">The Entra tenant id.</param>
    public static Uri TokenEndpointFor(Guid tenant) => new(AuthorityHost + tenant.ToString("D") + "/oauth2/v2.0/token", UriKind.Absolute);

    /// <inheritdoc />
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_kept is { } kept && now < kept.UsableUntil)
            {
                return kept.Token;
            }

            _kept = await RequestAsync(now, cancellationToken).ConfigureAwait(false);
            return _kept?.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the kept token. The certificate belongs to the caller and is not disposed.</summary>
    public void Dispose()
    {
        _kept = null;
        _gate.Dispose();
    }

    /// <summary>Names the type only; never an id, a token or an answer.</summary>
    public override string ToString() => nameof(EntraCertificateTokenSource);

    private async Task<KeptToken?> RequestAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var assertion = EntraClientAssertion.Create(_endpoint, _client, _certificate, now, Guid.NewGuid());
        if (assertion is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new("client_id", _client.ToString("D")),
                new("scope", Scope),
                new("client_assertion_type", AssertionType),
                new("client_assertion", assertion),
                new("grant_type", GrantType),
            ]),
        };

        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var invoker = new HttpMessageInvoker(_transport, disposeHandler: false);
        try
        {
            using var response = await invoker.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
            {
                return null;
            }

            var body = await ReadBodyAsync(response, linked.Token).ConfigureAwait(false);
            return Read(body, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed, refused or timed-out request, or an unreadable answer: no token, and nothing quoted.
            return null;
        }
    }

    // The answer, fail-closed. Each of the three fields must appear exactly once.
    private static KeptToken? Read(byte[]? body, DateTimeOffset requested)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? type = null, token = null, lifetime = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "token_type" when type is null:
                    type = property.Value;
                    break;
                case "access_token" when token is null:
                    token = property.Value;
                    break;
                case "expires_in" when lifetime is null:
                    lifetime = property.Value;
                    break;
                case "token_type" or "access_token" or "expires_in":
                    return null;
            }
        }

        if (type is not { ValueKind: JsonValueKind.String } typeValue
            || !string.Equals(typeValue.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || token is not { ValueKind: JsonValueKind.String } tokenValue
            || lifetime is not { ValueKind: JsonValueKind.Number } lifetimeValue
            || !lifetimeValue.TryGetInt64(out var seconds)
            || seconds <= (long)RefreshMargin.TotalSeconds
            || seconds > MaxLifetimeSeconds)
        {
            return null;
        }

        var accessToken = tokenValue.GetString();
        if (accessToken is not { Length: > 0 and <= MaxAccessTokenLength } || !accessToken.All(character => character is >= '!' and <= '~'))
        {
            return null;
        }

        // The lifetime counts from before the request was sent, so the kept token is never used past its real expiry.
        return new KeptToken(accessToken, requested + TimeSpan.FromSeconds(seconds) - RefreshMargin);
    }

    // Reads at most the bound; a longer or declared-longer body is read as none.
    private static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > MaxResponseBytes ? null : buffer[..total];
    }

    // A kept token. Its rendering never shows the token.
    private sealed class KeptToken(string token, DateTimeOffset usableUntil)
    {
        internal string Token { get; } = token;

        internal DateTimeOffset UsableUntil { get; } = usableUntil;

        public override string ToString() => nameof(KeptToken);
    }
}
