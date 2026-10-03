using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkContinuity.Connectors.Monday;

/// <summary>Where to send the user's browser to authorize a monday connection, and the connection it is for.</summary>
/// <remarks>
/// The address carries the state and the PKCE challenge, which the browser must see; the verifier and the state's
/// hash never leave the server. The string form names only the connection, so the state is never written anywhere by
/// accident.
/// </remarks>
public sealed record MondayAuthorizationRedirect
{
    internal MondayAuthorizationRedirect(Uri authorizationUri, Guid connectionId)
    {
        AuthorizationUri = authorizationUri;
        ConnectionId = connectionId;
    }

    /// <summary>Gets monday's authorization address with this authorization's parameters.</summary>
    public Uri AuthorizationUri { get; }

    /// <summary>Gets the connection being authorized: a new one, or the one being restored.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Identifies the redirect by its connection only.</summary>
    public override string ToString() => $"MondayAuthorizationRedirect(connection {ConnectionId:D})";
}

/// <summary>
/// The parameters monday's redirect brought back to the callback, exactly as received: untrusted input.
/// </summary>
/// <remarks>
/// The code, the state and the rest are held privately: nothing here renders, serializes or exposes them, so they can
/// reach no log or response. The authorizer validates every one before anything is exchanged.
/// </remarks>
[JsonConverter(typeof(MondayAuthorizationCallbackRefusingConverter))]
public sealed class MondayAuthorizationCallback
{
    /// <summary>Captures a callback's query parameters.</summary>
    /// <param name="state">The <c>state</c> parameter.</param>
    /// <param name="code">The <c>code</c> parameter.</param>
    /// <param name="error">The <c>error</c> parameter, present when the authorization was not granted.</param>
    /// <param name="issuer">The <c>iss</c> parameter, when monday sends one.</param>
    public MondayAuthorizationCallback(string? state, string? code, string? error, string? issuer)
    {
        State = state;
        Code = code;
        Error = error;
        Issuer = issuer;
    }

    internal string? State { get; }

    internal string? Code { get; }

    internal string? Error { get; }

    internal string? Issuer { get; }

    /// <summary>Never renders a parameter.</summary>
    public override string ToString() => "MondayAuthorizationCallback(redacted)";
}

/// <summary>The result of disconnecting a monday connection.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="WasAlreadyDisconnected">Whether it had already been disconnected, so nothing changed.</param>
/// <param name="ProviderRevocationConfirmed">Whether monday confirmed the revocation of every token that was held.</param>
public sealed record MondayDisconnection(Guid ConnectionId, bool WasAlreadyDisconnected, bool ProviderRevocationConfirmed);

/// <summary>What one PKCE verifier cleanup did, by counts only.</summary>
/// <param name="Deleted">Verifiers confirmed gone and recorded.</param>
/// <param name="Remaining">Verifiers whose deletion failed or was not recorded; the next cleanup retries them.</param>
public sealed record MondayVerifierCleanup(int Deleted, int Remaining);

/// <summary>Refuses to serialize or deserialize a callback, so a code or state can never be written by accident.</summary>
internal sealed class MondayAuthorizationCallbackRefusingConverter : JsonConverter<MondayAuthorizationCallback>
{
    public override MondayAuthorizationCallback Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("An authorization callback is never deserialized.");

    public override void Write(Utf8JsonWriter writer, MondayAuthorizationCallback value, JsonSerializerOptions options) =>
        throw new NotSupportedException("An authorization callback is never serialized.");
}
