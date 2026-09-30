using WorkContinuity.Core.Time;

namespace WorkContinuity.Connectors.Monday;

/// <summary>How checking a webhook request's token ended.</summary>
public enum MondayWebhookAuthenticationStatus
{
    /// <summary>Unspecified. Never a verification.</summary>
    Unspecified = 0,

    /// <summary>The token is a valid monday webhook token for the connection.</summary>
    Verified = 1,

    /// <summary>The token is missing, malformed or not valid for the connection. Refused permanently.</summary>
    Refused = 2,

    /// <summary>
    /// Nothing can be verified now: the Signing Secret or the public base address is not available. Nothing is stored,
    /// and the source retries.
    /// </summary>
    Unavailable = 3,
}

/// <summary>
/// The result of checking a webhook request's <c>Authorization</c> for one connection. It never renders the token.
/// </summary>
public sealed class MondayWebhookAuthentication
{
    private MondayWebhookAuthentication(MondayWebhookAuthenticationStatus status, Guid connectionId, string? token)
    {
        Status = status;
        ConnectionId = connectionId;
        Token = token;
    }

    /// <summary>Gets how the check ended.</summary>
    public MondayWebhookAuthenticationStatus Status { get; }

    /// <summary>Gets the connection the token was checked for.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets a value indicating whether the token is verified for the connection.</summary>
    public bool IsVerified => Status == MondayWebhookAuthenticationStatus.Verified;

    /// <summary>Gets the verified token, for the connector only: it becomes the ingress proof, which is never stored.</summary>
    internal string? Token { get; }

    /// <summary>Names the status only.</summary>
    /// <returns>The status.</returns>
    public override string ToString() => $"MondayWebhookAuthentication({Status})";

    internal static MondayWebhookAuthentication Verified(Guid connectionId, string token) =>
        new(MondayWebhookAuthenticationStatus.Verified, connectionId, token);

    internal static MondayWebhookAuthentication NotVerified(MondayWebhookAuthenticationStatus status, Guid connectionId) =>
        new(status == MondayWebhookAuthenticationStatus.Verified ? MondayWebhookAuthenticationStatus.Refused : status, connectionId, null);
}

/// <summary>
/// Checks the JWT of monday webhook deliveries with the app's Signing Secret, read from the connector's secret storage
/// (Gate 3C).
/// </summary>
/// <remarks>
/// <para>
/// The Signing Secret is read through <see cref="IMondaySecretStore"/> under <see cref="MondaySecretName.SigningSecret"/>,
/// at most once per instance, which a host scopes to one request, and is used only as the HMAC key. It never leaves this
/// class: not in a result, an error or a string form, and a caller can never supply it. It must be 1 to 1,024 visible
/// ASCII characters.
/// </para>
/// <para>
/// A token is valid only for the audience of the connection it is presented for
/// (<see cref="MondayWebhookOptions.AudienceFor"/>). Without options, or without a readable, well-formed Signing Secret,
/// nothing verifies, and the answer is <see cref="MondayWebhookAuthenticationStatus.Unavailable"/>. Nothing here logs.
/// </para>
/// <para>
/// monday documents that the token travels in the <c>Authorization</c> header, but not whether the value is the bare
/// token or <c>Bearer</c> and the token. Accepting exactly those two forms is a 3C compatibility decision, confirmed in
/// 3G. The parser is strict: the value is either exactly the token, or exactly <c>Bearer</c>, one space and the token,
/// with the scheme in that case. A different scheme or case, extra whitespace, a list, or a header given more than once
/// is refused.
/// </para>
/// </remarks>
public sealed class MondayWebhookAuthenticator
{
    private const string BearerPrefix = "Bearer ";
    private const int MaxSecretLength = 1024;

    private readonly IMondaySecretStore _secrets;
    private readonly IClock _clock;
    private readonly MondayWebhookOptions? _options;
    private MondaySecretLookup? _signingSecret;

    /// <summary>Creates the authenticator.</summary>
    /// <param name="secrets">The connector's secret storage, which holds the Signing Secret.</param>
    /// <param name="clock">The clock the token's times are checked against.</param>
    /// <param name="options">The webhook options, or null when intake is not configured, so nothing verifies.</param>
    public MondayWebhookAuthenticator(IMondaySecretStore secrets, IClock clock, MondayWebhookOptions? options)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);

        _secrets = secrets;
        _clock = clock;
        _options = options;
    }

    /// <summary>Checks the <c>Authorization</c> value of an event delivery on one connection.</summary>
    /// <param name="connectionId">The connection the request names.</param>
    /// <param name="authorization">The request's single <c>Authorization</c> value, or null when it has none.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The result; verified only for a valid token of this connection.</returns>
    public async ValueTask<MondayWebhookAuthentication> AuthenticateAsync(
        Guid connectionId,
        string? authorization,
        CancellationToken cancellationToken)
    {
        var token = TokenOf(authorization);
        var (status, _) = await VerifyAsync(connectionId, token, cancellationToken).ConfigureAwait(false);
        return status == MondayWebhookAuthenticationStatus.Verified && token is not null
            ? MondayWebhookAuthentication.Verified(connectionId, token)
            : MondayWebhookAuthentication.NotVerified(status, connectionId);
    }

    /// <summary>Verifies a token for one connection and returns its claims when it is valid.</summary>
    internal async ValueTask<(MondayWebhookAuthenticationStatus Status, MondayWebhookClaims? Claims)> VerifyAsync(
        Guid connectionId,
        string? token,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty || string.IsNullOrEmpty(token))
        {
            return (MondayWebhookAuthenticationStatus.Refused, null);
        }

        if (_options is null)
        {
            return (MondayWebhookAuthenticationStatus.Unavailable, null);
        }

        _signingSecret ??= await _secrets.GetAsync(MondaySecretName.SigningSecret, cancellationToken).ConfigureAwait(false)
            ?? MondaySecretLookup.Unavailable();
        if (_signingSecret.Status != MondaySecretStoreStatus.Succeeded || !IsWellFormedSecret(_signingSecret.Value))
        {
            return (MondayWebhookAuthenticationStatus.Unavailable, null);
        }

        var claims = MondayWebhookToken.Verify(token, _signingSecret.Value, _options.AudienceFor(connectionId), _clock.UtcNow);
        return claims is null
            ? (MondayWebhookAuthenticationStatus.Refused, null)
            : (MondayWebhookAuthenticationStatus.Verified, claims);
    }

    private static string? TokenOf(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization) || authorization.Length > MondayWebhooks.MaxAuthorizationLength)
        {
            return null;
        }

        // Whatever follows must itself be a well-formed token; the token check refuses any whitespace or separator.
        return authorization.StartsWith(BearerPrefix, StringComparison.Ordinal)
            ? authorization[BearerPrefix.Length..]
            : authorization;
    }

    private static bool IsWellFormedSecret(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > MaxSecretLength)
        {
            return false;
        }

        foreach (var octet in value)
        {
            if (octet is < (byte)'!' or > (byte)'~')
            {
                return false;
            }
        }

        return true;
    }
}
