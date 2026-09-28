using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Transport;

/// <summary>A message exactly as a transport adapter must send it; every value is fixed by the envelope.</summary>
/// <param name="QueueName">The queue the message type is carried on.</param>
/// <param name="MessageId">The deterministic broker message id.</param>
/// <param name="SessionId">The broker session.</param>
/// <param name="CorrelationId">The correlation id, carried in the broker's own correlation field.</param>
/// <param name="ContentType">The body's content type.</param>
/// <param name="ApplicationProperties">The typed envelope properties.</param>
/// <param name="Body">The UTF-8 body.</param>
public sealed record TransportWireMessage(
    string QueueName,
    string MessageId,
    string SessionId,
    string CorrelationId,
    string ContentType,
    IReadOnlyDictionary<string, object> ApplicationProperties,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>Identifies the message without its properties or body.</summary>
    public override string ToString() => $"TransportWireMessage({MessageId})";
}

/// <summary>
/// A message as a transport adapter received it. Untrusted: any member may be missing, malformed or forged
/// until <see cref="TransportEnvelope.Read"/> has validated it.
/// </summary>
/// <param name="MessageId">The broker message id.</param>
/// <param name="SessionId">The broker session.</param>
/// <param name="CorrelationId">The broker correlation field.</param>
/// <param name="ContentType">The body's content type.</param>
/// <param name="ApplicationProperties">The application properties as received.</param>
/// <param name="Body">The body as received.</param>
/// <param name="DeliveryCount">How many times the broker has delivered the message, starting at 1.</param>
/// <param name="SequenceNumber">The broker's sequence number, a safe diagnostic reference.</param>
public sealed record ReceivedTransportMessage(
    string? MessageId,
    string? SessionId,
    string? CorrelationId,
    string? ContentType,
    IReadOnlyDictionary<string, object> ApplicationProperties,
    ReadOnlyMemory<byte> Body,
    int DeliveryCount,
    long SequenceNumber)
{
    /// <summary>Identifies the delivery without echoing any received text.</summary>
    public override string ToString() => $"ReceivedTransportMessage(sequence {SequenceNumber}, delivery {DeliveryCount})";
}

/// <summary>
/// A validated broker message of contract version 1: who it belongs to, what it is, what caused it and
/// which operation it is part of.
/// </summary>
/// <remarks>
/// <para>
/// An envelope is obtainable only through validation: <see cref="FromOutbox"/> on the relay side and
/// <see cref="Read"/> on the consumer side, so a consumer never acts on a message it has not checked.
/// </para>
/// <para>
/// Identity is deterministic. <see cref="MessageId"/> is the outbox message id, so republishing a row
/// after a crash yields the same broker message id; after the n-th controlled re-drive it is that id with
/// a fixed suffix, so the broker's duplicate detection cannot mistake a re-drive for a duplicate.
/// <see cref="SessionId"/> is the tenant: a receipt's work object is unknown until processing normalizes
/// it, and the tenant is the narrowest key certain to hold every event of every object of that tenant, so
/// per-object order is preserved and two tenants never share a session.
/// </para>
/// <para>
/// The body is the content-free receipt marker in canonical form. Any other type or body shape is refused,
/// so no raw work content can reach the broker or its dead-letter queue.
/// </para>
/// </remarks>
public sealed class TransportEnvelope
{
    private const string ReceiptIdProperty = "receiptId";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions BodyParsing = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };

    private TransportEnvelope(
        string messageType,
        TenantId tenantId,
        Guid outboxMessageId,
        CorrelationId correlationId,
        Guid causationId,
        int redriveCount)
    {
        MessageType = messageType;
        TenantId = tenantId;
        OutboxMessageId = outboxMessageId;
        CorrelationId = correlationId;
        CausationId = causationId;
        RedriveCount = redriveCount;
    }

    /// <summary>Gets the message type.</summary>
    public string MessageType { get; }

    /// <summary>Gets the tenant the message belongs to.</summary>
    public TenantId TenantId { get; }

    /// <summary>Gets the outbox message id: the message's stable identity across republication and re-drive.</summary>
    public Guid OutboxMessageId { get; }

    /// <summary>Gets the correlation id of the operation the message is part of, from ingress onwards.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>
    /// Gets what caused the message. For a receipt marker it is the durable receipt the message announces,
    /// and the one its processing starts from.
    /// </summary>
    public Guid CausationId { get; }

    /// <summary>Gets how many times the message has been re-driven from the dead-letter queue.</summary>
    public int RedriveCount { get; }

    /// <summary>Gets the queue the message type is carried on.</summary>
    public string QueueName => TransportContract.MessageTypeQueues[MessageType];

    /// <summary>Gets the deterministic broker message id.</summary>
    public string MessageId => MessageIdFor(OutboxMessageId, RedriveCount);

    /// <summary>Gets the broker session: the tenant.</summary>
    public string SessionId => SessionIdFor(TenantId);

    /// <summary>Gets the canonical content-free body.</summary>
    public string Body => ReceiptMarker(CausationId);

    /// <summary>The broker message id for an outbox message after a number of re-drives.</summary>
    public static string MessageIdFor(Guid outboxMessageId, int redriveCount) => redriveCount == 0
        ? outboxMessageId.ToString("D")
        : outboxMessageId.ToString("D") + TransportContract.RedriveSeparator + redriveCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>The broker session for a tenant.</summary>
    public static string SessionIdFor(TenantId tenantId) => tenantId.Value.ToString("D");

    /// <summary>
    /// Builds the envelope for an outbox row. Fails, with the reason a consumer would give, for a type this
    /// contract does not carry or a payload that is not exactly the content-free marker of its type.
    /// </summary>
    public static Result<TransportEnvelope> FromOutbox(
        Guid outboxMessageId,
        TenantId tenantId,
        string messageType,
        string payload,
        CorrelationId correlationId)
    {
        if (messageType is null || !TransportContract.MessageTypeQueues.ContainsKey(messageType))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.MessageTypeUnsupported);
        }

        if (tenantId.IsEmpty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.TenantInvalid);
        }

        if (outboxMessageId == Guid.Empty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.IdentityInvalid);
        }

        if (correlationId.IsEmpty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.CorrelationInvalid);
        }

        if (!TryEncode(payload, out var body) || !TryReadReceiptMarker(body, out var receiptId))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.BodyInvalid);
        }

        return Result<TransportEnvelope>.Success(
            new TransportEnvelope(messageType, tenantId, outboxMessageId, correlationId, receiptId, redriveCount: 0));
    }

    /// <summary>
    /// Validates a received message. Every member is checked before any is trusted, and the first problem
    /// found is returned as a stable, content-free error that doubles as the dead-letter reason.
    /// </summary>
    public static Result<TransportEnvelope> Read(ReceivedTransportMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var properties = message.ApplicationProperties;

        if (Property(properties, TransportProperties.Contract) is not string contract
            || !string.Equals(contract, TransportContract.Version, StringComparison.Ordinal))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.ContractUnsupported);
        }

        if (Property(properties, TransportProperties.MessageType) is not string messageType
            || !TransportContract.MessageTypeQueues.ContainsKey(messageType))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.MessageTypeUnsupported);
        }

        if (!string.Equals(message.ContentType, TransportContract.ContentType, StringComparison.Ordinal))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.ContentTypeInvalid);
        }

        if (Property(properties, TransportProperties.Tenant) is not Guid tenant || tenant == Guid.Empty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.TenantInvalid);
        }

        if (Property(properties, TransportProperties.OutboxMessage) is not Guid outboxMessageId
            || outboxMessageId == Guid.Empty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.IdentityInvalid);
        }

        if (Property(properties, TransportProperties.RedriveCount) is not int redriveCount
            || redriveCount is < 0 or > TransportContract.MaxRedriveCount)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.RedriveCountInvalid);
        }

        if (!string.Equals(message.MessageId, MessageIdFor(outboxMessageId, redriveCount), StringComparison.Ordinal))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.MessageIdMismatch);
        }

        var tenantId = TenantId.From(tenant);
        if (!string.Equals(message.SessionId, SessionIdFor(tenantId), StringComparison.Ordinal))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.SessionMismatch);
        }

        if (!CorrelationId.TryParse(message.CorrelationId, out var correlationId))
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.CorrelationInvalid);
        }

        if (Property(properties, TransportProperties.Causation) is not Guid causationId || causationId == Guid.Empty)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.CausationInvalid);
        }

        if (!TryReadReceiptMarker(message.Body, out var receiptId) || receiptId != causationId)
        {
            return Result<TransportEnvelope>.Failure(TransportErrors.BodyInvalid);
        }

        return Result<TransportEnvelope>.Success(
            new TransportEnvelope(messageType, tenantId, outboxMessageId, correlationId, causationId, redriveCount));
    }

    /// <summary>
    /// The envelope of the next controlled re-drive: the same tenant, session, correlation, causation and
    /// identity, with the re-drive count, and therefore the message id, advanced. Fails at the limit.
    /// </summary>
    public Result<TransportEnvelope> ForRedrive() => RedriveCount >= TransportContract.MaxRedriveCount
        ? Result<TransportEnvelope>.Failure(TransportErrors.RedriveLimitReached)
        : Result<TransportEnvelope>.Success(
            new TransportEnvelope(MessageType, TenantId, OutboxMessageId, CorrelationId, CausationId, RedriveCount + 1));

    /// <summary>The message as every transport adapter must send it.</summary>
    public TransportWireMessage ToWire()
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [TransportProperties.Contract] = TransportContract.Version,
            [TransportProperties.MessageType] = MessageType,
            [TransportProperties.Tenant] = TenantId.Value,
            [TransportProperties.OutboxMessage] = OutboxMessageId,
            [TransportProperties.Causation] = CausationId,
            [TransportProperties.RedriveCount] = RedriveCount,
        };

        return new TransportWireMessage(
            QueueName,
            MessageId,
            SessionId,
            CorrelationId.Value,
            TransportContract.ContentType,
            new ReadOnlyDictionary<string, object>(properties),
            StrictUtf8.GetBytes(Body));
    }

    /// <summary>Identifies the message by ids only.</summary>
    public override string ToString() => $"TransportEnvelope({MessageId}, tenant {TenantId})";

    private static object? Property(IReadOnlyDictionary<string, object>? properties, string name) =>
        properties is not null && properties.TryGetValue(name, out var value) ? value : null;

    private static string ReceiptMarker(Guid receiptId) =>
        "{\"" + ReceiptIdProperty + "\":\"" + receiptId.ToString("D") + "\"}";

    private static bool TryEncode(string? payload, out ReadOnlyMemory<byte> body)
    {
        body = default;
        if (payload is null)
        {
            return false;
        }

        try
        {
            body = StrictUtf8.GetBytes(payload);
            return true;
        }
        catch (EncoderFallbackException)
        {
            // Malformed UTF-16, such as a lone surrogate, is not a marker.
            return false;
        }
    }

    // The marker is exactly one property, "receiptId", holding a non-empty id in canonical form. Anything
    // more could carry content, so anything more is refused.
    private static bool TryReadReceiptMarker(ReadOnlyMemory<byte> body, out Guid receiptId)
    {
        receiptId = Guid.Empty;
        if (body.IsEmpty || body.Length > TransportContract.MaxBodyBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body, BodyParsing);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? text = null;
            var count = 0;
            foreach (var property in root.EnumerateObject())
            {
                count++;
                if (count > 1
                    || !string.Equals(property.Name, ReceiptIdProperty, StringComparison.Ordinal)
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                text = property.Value.GetString();
            }

            if (text is not null
                && Guid.TryParseExact(text, "D", out var parsed)
                && parsed != Guid.Empty
                && string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal))
            {
                receiptId = parsed;
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not JSON, or a string that does not decode as well-formed UTF-8.
            return false;
        }
    }
}
