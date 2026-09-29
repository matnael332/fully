using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// A monday access credential for one attempt of one call, together with the identity it acts for.
/// </summary>
/// <remarks>
/// <para>
/// The access token never leaves this type except into the <c>Authorization</c> header of a monday request: there is
/// no public accessor, the string form is redacted, and serializing the credential throws. It is created by the
/// credential source only, and only for the connection that owns it, so a token cannot reach a Core contract, an event,
/// a log, a prompt or an Agent (Premium architecture §20).
/// </para>
/// <para>
/// The token must be printable ASCII without spaces, so it can be placed in a header without any possibility of
/// injecting a second header or line.
/// </para>
/// </remarks>
[JsonConverter(typeof(MondayAccessCredentialRefusingConverter))]
public sealed class MondayAccessCredential
{
    /// <summary>The longest accepted access token.</summary>
    public const int MaxTokenLength = 8192;

    private readonly string _accessToken;

    /// <summary>Creates a credential.</summary>
    /// <param name="accessToken">The access token, as the provider issued it.</param>
    /// <param name="identity">The identity the token acts for.</param>
    /// <param name="expiresAt">When the token expires, when known.</param>
    /// <exception cref="ArgumentException">The token is malformed or the identity is missing.</exception>
    public MondayAccessCredential(string accessToken, ConnectorIdentity identity, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrEmpty(accessToken) || accessToken.Length > MaxTokenLength)
        {
            throw new ArgumentException("A monday access token is required and must be bounded.", nameof(accessToken));
        }

        foreach (var character in accessToken)
        {
            // Visible ASCII only: no space, control character or line break can reach the header.
            if (character is < '!' or > '~')
            {
                throw new ArgumentException("A monday access token must be visible ASCII.", nameof(accessToken));
            }
        }

        _accessToken = accessToken;
        Identity = identity;
        ExpiresAt = expiresAt?.ToUniversalTime();
    }

    /// <summary>Gets the identity the credential acts for.</summary>
    public ConnectorIdentity Identity { get; }

    /// <summary>Gets when the credential expires, when known.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Whether the credential has expired at the given instant.</summary>
    public bool HasExpiredAt(DateTimeOffset instant) => ExpiresAt is { } expiry && expiry <= instant;

    /// <summary>Identifies the credential by its identity; the token is never rendered.</summary>
    public override string ToString() => $"MondayAccessCredential({Identity.Kind}, connection {Identity.ConnectionId:D}, token redacted)";

    /// <summary>Places the token in the request's Authorization header, in the form monday documents.</summary>
    internal void ApplyTo(HttpRequestHeaders headers) =>
        headers.TryAddWithoutValidation("Authorization", _accessToken);
}

/// <summary>Refuses to serialize or deserialize a credential, so a token can never be written by accident.</summary>
internal sealed class MondayAccessCredentialRefusingConverter : JsonConverter<MondayAccessCredential>
{
    public override MondayAccessCredential Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A monday credential is never deserialized.");

    public override void Write(Utf8JsonWriter writer, MondayAccessCredential value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A monday credential is never serialized.");
}
