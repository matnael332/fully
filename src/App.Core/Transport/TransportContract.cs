using WorkContinuity.Core.Ingress;

namespace WorkContinuity.Core.Transport;

/// <summary>
/// Version 1 of the broker wire contract: the queue, the message types it carries, the property names
/// and the limits every transport adapter applies the same way.
/// </summary>
/// <remarks>
/// <para>
/// Service Bus is durable transport only; the database stays operational truth (invariant 63). A broker
/// message therefore carries identities and references, never content: the one message type is the
/// content-free receipt marker, so neither the queue nor its dead-letter sub-queue ever holds raw work
/// data that would have to expire with it (Gate 2 owner decision 5).
/// </para>
/// <para>
/// Identity is carried as typed values (<see cref="Guid"/> and <see cref="int"/> application
/// properties), never as text that would have to be parsed back into a tenant.
/// </para>
/// </remarks>
public static class TransportContract
{
    /// <summary>The envelope contract version this code writes and accepts.</summary>
    public const string Version = "1";

    /// <summary>The content type of every message body.</summary>
    public const string ContentType = "application/json";

    /// <summary>The canonical queue for inbound event processing (architecture §9).</summary>
    public const string EventProcessingQueue = "event-processing";

    /// <summary>The largest body accepted. The receipt marker is about fifty bytes.</summary>
    public const int MaxBodyBytes = 1024;

    /// <summary>How many times one message may be re-driven from the dead-letter queue.</summary>
    public const int MaxRedriveCount = 3;

    /// <summary>Separates the outbox identity from the re-drive number in a re-driven message id.</summary>
    public const string RedriveSeparator = ":redrive:";

    /// <summary>The longest dead-letter reason that is reported as-is.</summary>
    public const int MaxReasonLength = 100;

    /// <summary>The reason reported for a dead-lettered message whose reason is not a safe token.</summary>
    public const string UnrecognizedReason = "unrecognized";

    /// <summary>The message types this contract carries, each on its queue.</summary>
    public static IReadOnlyDictionary<string, string> MessageTypeQueues { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IngressMessageTypes.ReceiptReceived] = EventProcessingQueue,
        };

    /// <summary>
    /// Whether a reason is safe to log or report: 1-100 ASCII letters, digits, '.', '_', '-' or ':'. Stable
    /// codes, and the broker's own reasons such as <c>MaxDeliveryCountExceeded</c>, pass; free text does not.
    /// </summary>
    public static bool IsSafeReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return false;
        }

        foreach (var character in reason)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The application property names of contract version 1.</summary>
public static class TransportProperties
{
    /// <summary>The envelope contract version, a string.</summary>
    public const string Contract = "wce-contract";

    /// <summary>The message type, a string.</summary>
    public const string MessageType = "wce-message-type";

    /// <summary>The owning tenant, a <see cref="Guid"/>.</summary>
    public const string Tenant = "wce-tenant";

    /// <summary>The outbox message id, the message's stable identity, a <see cref="Guid"/>.</summary>
    public const string OutboxMessage = "wce-outbox-message";

    /// <summary>What caused the message (for a receipt marker, the receipt), a <see cref="Guid"/>.</summary>
    public const string Causation = "wce-causation";

    /// <summary>How many times the message was re-driven, an <see cref="int"/>.</summary>
    public const string RedriveCount = "wce-redrive-count";
}
