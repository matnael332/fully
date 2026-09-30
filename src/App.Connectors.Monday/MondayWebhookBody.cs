using System.Text;
using System.Text.Json;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;

namespace WorkContinuity.Connectors.Monday;

/// <summary>What a webhook body is.</summary>
public enum MondayWebhookBodyKind
{
    /// <summary>Not a body monday sends. It is refused, and nothing is stored.</summary>
    Malformed = 0,

    /// <summary>An address-verification challenge. It is answered and never stored.</summary>
    Challenge = 1,

    /// <summary>An event delivery.</summary>
    Event = 2,
}

/// <summary>
/// One webhook body, read strictly: an address-verification challenge, an event delivery, or neither.
/// </summary>
/// <remarks>
/// <para>
/// The body is untrusted provider content, read only far enough to tell a challenge from an event and to find the
/// provider event id. It must be at most <see cref="MondayWebhooks.MaxBodyBytes"/> bytes of well-formed UTF-8 without a
/// byte-order mark, holding JSON without duplicate keys, comments or trailing commas, nested at most 64 levels.
/// </para>
/// <para>
/// A challenge is an object with exactly one property, <c>challenge</c>, whose value is 1 to
/// <see cref="MaxChallengeLength"/> visible ASCII characters other than a quotation mark or a backslash. An event is an
/// object with an <c>event</c> object and no <c>challenge</c>. The event's <c>triggerUuid</c> is the provider event id
/// when monday supplies it as a valid identifier; when it is absent or null there is none, and none is synthesized; any
/// other value makes the body malformed. Nothing else in the body is interpreted, and nothing in it selects a tenant, a
/// user or a policy. monday's times and <c>originalTriggerUuid</c> stay in the stored body for normalization.
/// </para>
/// <para>
/// The type never renders content: its string form names the kind only.
/// </para>
/// </remarks>
public sealed class MondayWebhookBody
{
    /// <summary>The longest accepted challenge value, in characters.</summary>
    public const int MaxChallengeLength = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions Parsing = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    private static readonly MondayWebhookBody MalformedBody = new(MondayWebhookBodyKind.Malformed, null, null, null);

    private readonly string? _challenge;
    private readonly string? _payload;

    private MondayWebhookBody(MondayWebhookBodyKind kind, string? challenge, string? sourceEventId, string? payload)
    {
        Kind = kind;
        _challenge = challenge;
        SourceEventId = sourceEventId;
        _payload = payload;
    }

    /// <summary>Gets what the body is.</summary>
    public MondayWebhookBodyKind Kind { get; }

    /// <summary>Gets an event's provider event id: monday's <c>triggerUuid</c> when supplied, otherwise null.</summary>
    public string? SourceEventId { get; }

    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>Reads a body.</summary>
    /// <param name="body">The body's bytes, exactly as received.</param>
    /// <returns>The body, which is <see cref="MondayWebhookBodyKind.Malformed"/> unless it is a challenge or an event.</returns>
    public static MondayWebhookBody Read(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body.Length > MondayWebhooks.MaxBodyBytes || body.StartsWith(ByteOrderMark))
        {
            return MalformedBody;
        }

        try
        {
            var text = StrictUtf8.GetString(body);
            using var document = JsonDocument.Parse(text, Parsing);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MalformedBody;
            }

            if (root.TryGetProperty("challenge", out var challenge))
            {
                // A challenge is the challenge and nothing else: an extra field, an event included, is refused.
                return root.EnumerateObject().Count() == 1 && IsChallenge(challenge)
                    ? new MondayWebhookBody(MondayWebhookBodyKind.Challenge, challenge.GetString(), null, null)
                    : MalformedBody;
            }

            if (!root.TryGetProperty("event", out var delivery)
                || delivery.ValueKind != JsonValueKind.Object
                || !TryReadSourceEventId(delivery, out var sourceEventId))
            {
                return MalformedBody;
            }

            return new MondayWebhookBody(MondayWebhookBodyKind.Event, null, sourceEventId, text);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or InvalidOperationException)
        {
            // Not UTF-8, not JSON, or text that cannot be decoded.
            return MalformedBody;
        }
    }

    /// <summary>The answer to a challenge: <c>{"challenge": …}</c> with the same value, as UTF-8 JSON.</summary>
    /// <returns>The response body.</returns>
    /// <exception cref="InvalidOperationException">The body is not a challenge.</exception>
    public byte[] ChallengeResponse()
    {
        if (Kind != MondayWebhookBodyKind.Challenge)
        {
            throw new InvalidOperationException("Only a challenge has a challenge response.");
        }

        // The value holds visible ASCII only, without a quotation mark or a backslash, so it needs no escaping.
        return Encoding.ASCII.GetBytes("{\"challenge\":\"" + _challenge + "\"}");
    }

    /// <summary>
    /// The ingress submission of an authenticated event: the body exactly as received, under the connector's source and
    /// schema version, with monday's <c>triggerUuid</c> as the provider event id when supplied, and the verified token as
    /// the proof, which ingress never stores.
    /// </summary>
    /// <param name="authentication">The event's verified authentication, which also names the connection.</param>
    /// <param name="correlationId">The request's correlation id.</param>
    /// <returns>The submission.</returns>
    /// <exception cref="InvalidOperationException">The body is not an event, or the authentication is not verified.</exception>
    public IngressSubmission ToSubmission(MondayWebhookAuthentication authentication, CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        if (Kind != MondayWebhookBodyKind.Event || !authentication.IsVerified || _payload is null)
        {
            throw new InvalidOperationException("Only an authenticated event becomes a submission.");
        }

        return new IngressSubmission(
            authentication.ConnectionId,
            MondayWebhooks.SourceSystem,
            SourceEventId,
            MondayWebhooks.SchemaVersion,
            _payload,
            correlationId,
            authentication.Token);
    }

    /// <summary>Names the kind only; the body is never rendered.</summary>
    /// <returns>The kind of body.</returns>
    public override string ToString() => $"MondayWebhookBody({Kind})";

    private static bool IsChallenge(JsonElement challenge)
    {
        if (challenge.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = challenge.GetString();
        return value is { Length: > 0 and <= MaxChallengeLength }
            && value.All(character => character is >= '!' and <= '~' and not '"' and not '\\');
    }

    private static bool TryReadSourceEventId(JsonElement delivery, out string? sourceEventId)
    {
        sourceEventId = null;
        if (!delivery.TryGetProperty("triggerUuid", out var trigger) || trigger.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (trigger.ValueKind != JsonValueKind.String || !EventText.IsIdentifier(trigger.GetString()))
        {
            return false;
        }

        sourceEventId = trigger.GetString();
        return true;
    }
}
