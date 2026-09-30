using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Connectors.Monday;

/// <summary>A token pair monday issued, with its expiry and granted scopes. Never rendered.</summary>
internal sealed class MondayIssuedTokens
{
    internal MondayIssuedTokens(string accessToken, string refreshToken, DateTimeOffset accessExpiresAt, string grantedScopes)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessExpiresAt = accessExpiresAt.ToUniversalTime();
        GrantedScopes = grantedScopes;
    }

    internal string AccessToken { get; }

    internal string RefreshToken { get; }

    internal DateTimeOffset AccessExpiresAt { get; }

    internal string GrantedScopes { get; }

    /// <summary>Identifies the pair without its tokens.</summary>
    public override string ToString() => "MondayIssuedTokens(tokens redacted)";

    /// <summary>Whether the text can be a token: 1–8192 visible ASCII characters, so it can never break a header.</summary>
    internal static bool IsToken(string? value) =>
        value is { Length: > 0 and <= MondayAccessCredential.MaxTokenLength }
        && value.All(character => character is >= '!' and <= '~');
}

/// <summary>How a token call ended.</summary>
internal enum MondayTokenOutcomeKind
{
    /// <summary>Unspecified. Never a real outcome.</summary>
    Unspecified = 0,

    /// <summary>A complete, usable pair was issued.</summary>
    Issued = 1,

    /// <summary>monday answered success, but the pair is incomplete or its expiry unusable. Whatever was issued must be revoked.</summary>
    Unusable = 2,

    /// <summary>monday refused the request (a 4xx other than 429). Nothing was issued.</summary>
    Rejected = 3,

    /// <summary>The request provably changed nothing: throttled (429), or never sent.</summary>
    Transient = 4,

    /// <summary>The request may have been processed but its answer was lost or not understood (a timeout, a 5xx, a broken response).</summary>
    Ambiguous = 5,
}

/// <summary>The outcome of one token call.</summary>
internal sealed class MondayTokenOutcome
{
    private MondayTokenOutcome(
        MondayTokenOutcomeKind kind,
        MondayIssuedTokens? tokens,
        string? issuedRefreshToken,
        string? issuedAccessToken,
        ConnectorFailure? failure)
    {
        Kind = kind;
        Tokens = tokens;
        IssuedRefreshToken = issuedRefreshToken;
        IssuedAccessToken = issuedAccessToken;
        Failure = failure;
    }

    internal MondayTokenOutcomeKind Kind { get; }

    /// <summary>The usable pair, when <see cref="Kind"/> is <see cref="MondayTokenOutcomeKind.Issued"/>.</summary>
    internal MondayIssuedTokens? Tokens { get; }

    /// <summary>The refresh token the answer carried, usable or not: what must be revoked when the answer is discarded.</summary>
    internal string? IssuedRefreshToken { get; }

    /// <summary>The access token the answer carried, usable or not.</summary>
    internal string? IssuedAccessToken { get; }

    /// <summary>The transient failure to report, when <see cref="Kind"/> is <see cref="MondayTokenOutcomeKind.Transient"/>.</summary>
    internal ConnectorFailure? Failure { get; }

    /// <summary>Identifies the outcome without any token.</summary>
    public override string ToString() => $"MondayTokenOutcome({Kind})";

    internal static MondayTokenOutcome Issued(MondayIssuedTokens tokens) =>
        new(MondayTokenOutcomeKind.Issued, tokens, tokens.RefreshToken, tokens.AccessToken, failure: null);

    internal static MondayTokenOutcome Unusable(string? issuedRefreshToken, string? issuedAccessToken) =>
        new(MondayTokenOutcomeKind.Unusable, tokens: null, issuedRefreshToken, issuedAccessToken, failure: null);

    internal static MondayTokenOutcome Of(MondayTokenOutcomeKind kind, ConnectorFailure? failure = null) =>
        new(kind, tokens: null, issuedRefreshToken: null, issuedAccessToken: null, failure);
}

/// <summary>
/// monday's OAuth 2.1 token and revocation endpoints: the code exchange, the refresh and the revocation.
/// </summary>
/// <remarks>
/// <para>
/// Each method makes exactly one attempt; nothing here retries. Requests go only to the fixed endpoints in
/// <see cref="MondayEndpoints"/>, as JSON bodies, which is how monday documents them; the client secret travels in the
/// body (<c>client_secret_post</c> form) and nowhere else. An answer from any other address, as after a redirect, is
/// refused. Each call is bounded in time and each response in size.
/// </para>
/// <para>
/// The outcome says what the caller may safely do. <see cref="MondayTokenOutcomeKind.Transient"/> is reported only when
/// the request provably changed nothing: monday throttled it (429), or it never left because no connection could be
/// established. Everything else that is not a clear answer is <see cref="MondayTokenOutcomeKind.Ambiguous"/>, because a
/// refresh may already have rotated the token. Error bodies are never read into anything: monday does not document the
/// token endpoint's error codes, and provider text never becomes an error. Nothing is logged.
/// </para>
/// </remarks>
internal sealed class MondayOAuthClient
{
    /// <summary>The largest response body read.</summary>
    internal const int MaxResponseBytes = 64 * 1024;

    /// <summary>The longest access-token lifetime accepted as sane; a later expiry is treated as unusable.</summary>
    internal static readonly TimeSpan LongestAccessLifetime = TimeSpan.FromDays(366);

    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly TimeSpan _timeout;

    internal MondayOAuthClient(HttpClient http, IClock clock, TimeSpan timeout)
    {
        _http = http;
        _clock = clock;
        _timeout = timeout;
    }

    /// <summary>Exchanges an authorization code with its PKCE verifier. Never retried.</summary>
    internal Task<MondayTokenOutcome> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        Uri redirectUri,
        string verifier,
        CancellationToken cancellationToken) =>
        RequestTokensAsync(
            StoredJson.Write(writer =>
            {
                writer.WriteString("grant_type", "authorization_code");
                writer.WriteString("client_id", clientId);
                writer.WriteString("client_secret", clientSecret);
                writer.WriteString("code", code);
                writer.WriteString("redirect_uri", redirectUri.AbsoluteUri);
                writer.WriteString("code_verifier", verifier);
            }),
            cancellationToken);

    /// <summary>Exchanges a refresh token for a new pair. Never retried: a lost answer may already have rotated the token.</summary>
    internal Task<MondayTokenOutcome> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken) =>
        RequestTokensAsync(
            StoredJson.Write(writer =>
            {
                writer.WriteString("grant_type", "refresh_token");
                writer.WriteString("client_id", clientId);
                writer.WriteString("client_secret", clientSecret);
                writer.WriteString("refresh_token", refreshToken);
            }),
            cancellationToken);

    /// <summary>Revokes one token. True only when monday confirms with <c>{"success": true}</c>.</summary>
    internal async Task<bool> RevokeAsync(
        string clientId,
        string clientSecret,
        string token,
        string tokenTypeHint,
        CancellationToken cancellationToken)
    {
        var answer = await PostAsync(
                MondayEndpoints.Revoke,
                StoredJson.Write(writer =>
                {
                    writer.WriteString("token", token);
                    writer.WriteString("client_id", clientId);
                    writer.WriteString("client_secret", clientSecret);
                    writer.WriteString("token_type_hint", tokenTypeHint);
                }),
                cancellationToken)
            .ConfigureAwait(false);

        if (answer.Status != 200 || answer.Body is null)
        {
            return false;
        }

        using var document = ParseObject(answer.Body);
        return document is not null
            && document.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True;
    }

    /// <summary>Reads the expiry of a JWT access token from its <c>exp</c> claim. False when there is no usable one.</summary>
    /// <remarks>
    /// The token's signature is not checked: the connector is a client, and monday, which issued the token, enforces it.
    /// The claim only tells the client when to renew. A token that is not a three-part JWT, or whose <c>exp</c> is not a
    /// number of seconds, has no usable expiry, and none is invented.
    /// </remarks>
    internal static bool TryReadExpiry(string accessToken, out DateTimeOffset expiry)
    {
        expiry = default;
        var parts = accessToken.Split('.');
        if (parts.Length != 3 || parts[1].Length is 0 or > 8192)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var document = ParseObject(payload);
        if (document is null
            || !document.RootElement.TryGetProperty("exp", out var exp)
            || exp.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        long seconds;
        if (exp.TryGetInt64(out var whole))
        {
            seconds = whole;
        }
        else if (exp.TryGetDouble(out var fractional) && double.IsFinite(fractional) && fractional is >= 0 and < long.MaxValue)
        {
            seconds = (long)Math.Floor(fractional);
        }
        else
        {
            return false;
        }

        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private async Task<MondayTokenOutcome> RequestTokensAsync(byte[] body, CancellationToken cancellationToken)
    {
        var answer = await PostAsync(MondayEndpoints.Token, body, cancellationToken).ConfigureAwait(false);

        if (answer.NotSent)
        {
            return MondayTokenOutcome.Of(
                MondayTokenOutcomeKind.Transient,
                new ConnectorFailure(ConnectorFailureKind.NetworkTransient, ConnectorErrors.NetworkFailure));
        }

        return answer.Status switch
        {
            200 when answer.Body is not null => ReadTokens(answer.Body, answer.IsJson),
            429 => MondayTokenOutcome.Of(
                MondayTokenOutcomeKind.Transient,
                new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, answer.RetryAfter)),
            >= 400 and < 500 => MondayTokenOutcome.Of(MondayTokenOutcomeKind.Rejected),
            _ => MondayTokenOutcome.Of(MondayTokenOutcomeKind.Ambiguous),
        };
    }

    private MondayTokenOutcome ReadTokens(byte[] body, bool isJson)
    {
        using var document = isJson ? ParseObject(body) : null;
        if (document is null)
        {
            return MondayTokenOutcome.Unusable(issuedRefreshToken: null, issuedAccessToken: null);
        }

        var root = document.RootElement;

        // Whatever was issued must stay revocable, even when the rest of the answer is unusable.
        var access = StringOf(root, "access_token") is { } accessText && MondayIssuedTokens.IsToken(accessText) ? accessText : null;
        var refresh = StringOf(root, "refresh_token") is { } refreshText && MondayIssuedTokens.IsToken(refreshText) ? refreshText : null;

        var now = _clock.UtcNow;
        if (access is null
            || refresh is null
            || !string.Equals(StringOf(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase)
            || MondayScopePolicy.CanonicalGrant(StringOf(root, "scope")) is not { } scopes
            || !TryReadExpiry(access, out var expiry)
            || expiry <= now
            || expiry > now + LongestAccessLifetime
            || !TryBoundByExpiresIn(root, now, ref expiry))
        {
            return MondayTokenOutcome.Unusable(refresh, access);
        }

        return MondayTokenOutcome.Issued(new MondayIssuedTokens(access, refresh, expiry, scopes));
    }

    // monday documents expires_in only for its legacy-token migration endpoint. When a response carries it anyway, it may
    // only shorten the JWT's expiry, and a malformed value makes the whole answer unusable.
    private static bool TryBoundByExpiresIn(JsonElement root, DateTimeOffset now, ref DateTimeOffset expiry)
    {
        if (!root.TryGetProperty("expires_in", out var expiresIn))
        {
            return true;
        }

        if (expiresIn.ValueKind != JsonValueKind.Number
            || !expiresIn.TryGetInt64(out var seconds)
            || seconds <= 0
            || seconds > (long)LongestAccessLifetime.TotalSeconds)
        {
            return false;
        }

        var bound = now.AddSeconds(seconds);
        if (bound < expiry)
        {
            expiry = bound;
        }

        return true;
    }

    private async Task<Answer> PostAsync(Uri endpoint, byte[] body, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);

            if (response.RequestMessage?.RequestUri is { } answered && answered != endpoint)
            {
                // Redirected elsewhere: nothing from another address is trusted, and whether monday acted is unknown.
                return Answer.Unclear;
            }

            var retryAfter = MondayResponseClassifier.WaitFromHeaders(response.Headers, _clock.UtcNow);
            var content = await MondayHttpContent.ReadBoundedAsync(response.Content, MaxResponseBytes, limit.Token).ConfigureAwait(false);
            return new Answer((int)response.StatusCode, content, MondayHttpContent.IsJson(response.Content), retryAfter, NotSent: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Answer.Unclear;
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError)
        {
            // No connection was established, so the request never reached monday.
            return Answer.Unsent;
        }
        catch (HttpRequestException)
        {
            return Answer.Unclear;
        }
        catch (IOException)
        {
            return Answer.Unclear;
        }
    }

    private static JsonDocument? ParseObject(byte[] body)
    {
        try
        {
            var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private readonly record struct Answer(int? Status, byte[]? Body, bool IsJson, TimeSpan? RetryAfter, bool NotSent)
    {
        internal static Answer Unclear => new(Status: null, Body: null, IsJson: false, RetryAfter: null, NotSent: false);

        internal static Answer Unsent => new(Status: null, Body: null, IsJson: false, RetryAfter: null, NotSent: true);
    }
}
