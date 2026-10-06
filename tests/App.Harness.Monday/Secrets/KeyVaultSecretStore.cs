using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Harness.Monday.Secrets;

/// <summary>
/// An offline Key Vault adapter for the connector's secret storage (owner decisions S-1 to S-5 of 2026-10-05).
/// </summary>
/// <remarks>
/// <para>
/// It speaks the documented Key Vault REST interface over an injected handler, with an injected access-token source, an
/// injected delay between attempts and an injected time source. Nothing in the harness constructs it: it is not wired,
/// and the default executable still has no usable secret store. It is exercised by tests against a fake vault only.
/// </para>
/// <para>
/// It sends three requests and no other: get the latest version of a name, set a name, and delete a name, always to the
/// one configured vault, with one pinned API version. It never names a secret version and never lists, recovers, backs
/// up, restores, updates or permanently removes anything.
/// </para>
/// <para>
/// Statuses (S-4, S-5). A 200 with a well-formed body is success. Only a 404 carrying a well-formed Key Vault error
/// body is "not found", and only for get and delete: it means no active secret existed under the name when the vault
/// answered. A delete's success means the name has left the vault's active namespace and no longer answers an ordinary
/// get; it does not mean the value is destroyed, because the vault may keep it recoverable. Everything else is
/// unavailable: a refusal, a conflict, a malformed answer, throttling or a server failure once the attempts are spent, a
/// connection failure, a timeout, and any ambiguity that no later attempt settled. A timeout is never taken as proof that
/// a write did not happen.
/// </para>
/// <para>
/// Attempts (S-5). At most three per operation, retried only for 429, 500, 502, 503, 504, a connection failure or an
/// attempt timeout, each attempt bounded to five seconds and the whole operation to ten seconds for a get and fifteen
/// for a set or a delete. A 409 is never retried. Retry-After is honored only up to two seconds and only within what is
/// left of the budget; a longer one ends the operation as unavailable. A retried set sends the identical bytes to the
/// same name. The caller's cancellation propagates and is never turned into a status.
/// </para>
/// <para>
/// It keeps no value and no absence between calls, logs nothing, and no failure it reports quotes a value, a token or
/// anything the vault answered.
/// </para>
/// </remarks>
public sealed class KeyVaultSecretStore : IMondaySecretStore
{
    /// <summary>The pinned API version of the Key Vault secrets interface.</summary>
    public const string ApiVersion = "2025-07-01";

    /// <summary>The most attempts an operation makes.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The bound of one attempt.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The bound of a whole get.</summary>
    public static readonly TimeSpan GetBudget = TimeSpan.FromSeconds(10);

    /// <summary>The bound of a whole set or delete.</summary>
    public static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(15);

    /// <summary>The longest Retry-After the adapter waits for.</summary>
    public static readonly TimeSpan RetryAfterCap = TimeSpan.FromSeconds(2);

    private const string ApiVersionQuery = "?api-version=" + ApiVersion;
    private const string VaultSuffix = ".vault.azure.net";
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxAccessTokenLength = 16 * 1024;

    private static readonly TimeSpan[] DefaultWaits = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)];

    private readonly Uri _vault;
    private readonly HttpMessageHandler _transport;
    private readonly IKeyVaultAccessTokenSource _tokens;
    private readonly IMondayDelay _delay;
    private readonly TimeProvider _time;

    /// <summary>Creates the adapter.</summary>
    /// <param name="vault">The vault's address: exactly <c>https://{vault-name}.vault.azure.net/</c>.</param>
    /// <param name="transport">The handler requests are sent through. Supplied by the caller; never built here.</param>
    /// <param name="tokens">The source of the bearer credential.</param>
    /// <param name="delay">Waits between attempts.</param>
    /// <param name="time">Bounds attempts and the operation's budget.</param>
    /// <exception cref="ArgumentException">The vault address is not a vault origin.</exception>
    public KeyVaultSecretStore(
        Uri vault,
        HttpMessageHandler transport,
        IKeyVaultAccessTokenSource tokens,
        IMondayDelay delay,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(time);

        if (!IsVaultOrigin(vault))
        {
            throw new ArgumentException("The vault address must be https://{vault-name}.vault.azure.net/ and nothing more.", nameof(vault));
        }

        _vault = vault;
        _transport = transport;
        _tokens = tokens;
        _delay = delay;
        _time = time;
    }

    private enum Outcome
    {
        Unavailable = 0,
        Ok = 1,
        NotFound = 2,
    }

    /// <inheritdoc />
    public async ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsSecretName(name.Value))
        {
            return MondaySecretLookup.Unavailable();
        }

        var (outcome, body) = await SendAsync(HttpMethod.Get, ItemPath(name) + "/", content: null, GetBudget, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            Outcome.Ok => ReadValue(name, body),
            Outcome.NotFound => MondaySecretLookup.Missing(),
            _ => MondaySecretLookup.Unavailable(),
        };
    }

    /// <inheritdoc />
    public async ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsSecretName(name.Value) || KeyVaultSecretCodec.Encode(value.Span) is not { } text)
        {
            // Refused before any request: the value is outside the supported subset, or the name is not a vault name.
            return MondaySecretStoreStatus.Unavailable;
        }

        // Built once, so every attempt sends the identical bytes.
        var content = SetBody(text);
        var (outcome, _) = await SendAsync(HttpMethod.Put, ItemPath(name), content, WriteBudget, cancellationToken).ConfigureAwait(false);
        return outcome == Outcome.Ok ? MondaySecretStoreStatus.Succeeded : MondaySecretStoreStatus.Unavailable;
    }

    /// <inheritdoc />
    public async ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsSecretName(name.Value))
        {
            return MondaySecretStoreStatus.Unavailable;
        }

        var (outcome, _) = await SendAsync(HttpMethod.Delete, ItemPath(name), content: null, WriteBudget, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            Outcome.Ok => MondaySecretStoreStatus.Succeeded,
            Outcome.NotFound => MondaySecretStoreStatus.NotFound,
            _ => MondaySecretStoreStatus.Unavailable,
        };
    }

    /// <summary>Names the type only; never the vault's answers or any value.</summary>
    public override string ToString() => nameof(KeyVaultSecretStore);

    /// <summary>
    /// Whether an address is exactly a vault origin, by meaning rather than spelling: HTTPS, the effective default port,
    /// a host that is one vault label under the vault domain, and no user information, path, query or fragment. Host
    /// names are case-insensitive and the root path may be implied, so those spellings are the same origin; every request
    /// is built from the parsed address, whose host is normalized.
    /// </summary>
    /// <param name="vault">The address.</param>
    public static bool IsVaultOrigin(Uri vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!vault.IsAbsoluteUri
            || vault.Scheme != Uri.UriSchemeHttps
            || !vault.IsDefaultPort
            || vault.HostNameType != UriHostNameType.Dns
            || vault.UserInfo.Length > 0
            || vault.Query.Length > 0
            || vault.Fragment.Length > 0
            || vault.AbsolutePath != "/"
            || !vault.Host.EndsWith(VaultSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var label = vault.Host[..^VaultSuffix.Length];
        return label.Length is >= 3 and <= 24
            && label.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
    }

    private static string ItemPath(MondaySecretName name) => "secrets/" + name.Value;

    private static bool IsSecretName(string value) =>
        value.Length is > 0 and <= 127 && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static bool IsAccessToken(string? token) =>
        token is { Length: > 0 and <= MaxAccessTokenLength } && token.All(character => character is >= '!' and <= '~');

    private static byte[] SetBody(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("value", text);
            writer.WriteString("contentType", KeyVaultSecretCodec.ContentType);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // A get's answer: an object whose value is a string and whose content type, when present, is a string or null. Text
    // that is not valid UTF-16 once unescaped is unavailable, never an exception.
    private static MondaySecretLookup ReadValue(MondaySecretName name, byte[]? body)
    {
        try
        {
            return ReadValueStrictly(name, body);
        }
        catch (InvalidOperationException)
        {
            return MondaySecretLookup.Unavailable();
        }
    }

    private static MondaySecretLookup ReadValueStrictly(MondaySecretName name, byte[]? body)
    {
        using var document = ParseObject(body);
        if (document is null
            || !document.RootElement.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return MondaySecretLookup.Unavailable();
        }

        var present = false;
        string? contentType = null;
        if (document.RootElement.TryGetProperty("contentType", out var type))
        {
            switch (type.ValueKind)
            {
                case JsonValueKind.Null:
                    break;
                case JsonValueKind.String:
                    present = true;
                    contentType = type.GetString();
                    break;
                default:
                    return MondaySecretLookup.Unavailable();
            }
        }

        return KeyVaultSecretCodec.Decode(name, value.GetString()!, present, contentType);
    }

    // A Key Vault error body: an object whose "error" is an object with a non-empty string "code".
    private static bool IsKeyVaultError(byte[]? body)
    {
        using var document = ParseObject(body);
        return document is not null
            && document.RootElement.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(code.GetString());
    }

    private static JsonDocument? ParseObject(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    // Runs one operation's attempts within its budget. Returns the settled outcome and, for success, the answer's body.
    private async Task<(Outcome Outcome, byte[]? Body)> SendAsync(
        HttpMethod method,
        string path,
        byte[]? content,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = _time.GetTimestamp();

        string? token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (Outcome.Unavailable, null);
        }

        if (!IsAccessToken(token))
        {
            return (Outcome.Unavailable, null);
        }

        var address = new Uri(_vault, path + ApiVersionQuery);
        for (var attempt = 1; ; attempt++)
        {
            var remaining = budget - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return (Outcome.Unavailable, null);
            }

            var result = await AttemptAsync(method, address, token!, content, remaining < AttemptTimeout ? remaining : AttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Retryable)
            {
                return (result.Outcome, result.Body);
            }

            if (attempt == MaxAttempts)
            {
                return (Outcome.Unavailable, null);
            }

            var wait = result.RetryAfter ?? DefaultWaits[attempt - 1];
            if (wait > RetryAfterCap || wait >= budget - _time.GetElapsedTime(started))
            {
                return (Outcome.Unavailable, null);
            }

            await _delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AttemptResult> AttemptAsync(
        HttpMethod method,
        Uri address,
        string token,
        byte[]? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var attemptTimeout = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
        using var invoker = new HttpMessageInvoker(_transport, disposeHandler: false);
        try
        {
            using var response = await invoker.SendAsync(request, linked.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, linked.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => AttemptResult.Settled(IsJsonObject(body) ? Outcome.Ok : Outcome.Unavailable, body),
                HttpStatusCode.NotFound => AttemptResult.Settled(IsKeyVaultError(body) ? Outcome.NotFound : Outcome.Unavailable, null),
                HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout => AttemptResult.Retry(RetryAfterOf(response)),
                _ => AttemptResult.Settled(Outcome.Unavailable, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
        {
            // The attempt's own timeout: whether the request was applied is unknown.
            return AttemptResult.Retry(null);
        }
        catch (HttpRequestException)
        {
            // A connection failure: whether the request was applied is unknown.
            return AttemptResult.Retry(null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AttemptResult.Settled(Outcome.Unavailable, null);
        }
    }

    private static bool IsJsonObject(byte[]? body)
    {
        using var document = ParseObject(body);
        return document is not null;
    }

    // Reads at most the bound; a longer or declared-longer body is malformed and read as none.
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

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private readonly record struct AttemptResult(bool Retryable, Outcome Outcome, byte[]? Body, TimeSpan? RetryAfter)
    {
        internal static AttemptResult Settled(Outcome outcome, byte[]? body) => new(false, outcome, body, null);

        internal static AttemptResult Retry(TimeSpan? retryAfter) => new(true, Outcome.Unavailable, null, retryAfter);
    }
}
