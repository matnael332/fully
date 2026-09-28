using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Transport;

/// <summary>
/// The stable transport error codes. Each doubles as a dead-letter reason, so every code is a safe token
/// and every message is fixed text that never echoes a message, its body or an SDK error.
/// </summary>
public static class TransportErrors
{
    /// <summary>The message names a contract version this code does not accept.</summary>
    public static Error ContractUnsupported { get; } =
        Error.Validation("transport.envelope.contract_unsupported", "The message uses an unsupported transport contract version.");

    /// <summary>The message type is not one this contract carries.</summary>
    public static Error MessageTypeUnsupported { get; } =
        Error.Validation("transport.envelope.type_unsupported", "The message type is not carried by this transport.");

    /// <summary>The content type is not the contract's.</summary>
    public static Error ContentTypeInvalid { get; } =
        Error.Validation("transport.envelope.content_type_invalid", "The message content type is not supported.");

    /// <summary>The tenant is missing, empty or not typed as an identifier.</summary>
    public static Error TenantInvalid { get; } =
        Error.Validation("transport.envelope.tenant_invalid", "The message does not carry a valid tenant.");

    /// <summary>The outbox identity is missing, empty or not typed as an identifier.</summary>
    public static Error IdentityInvalid { get; } =
        Error.Validation("transport.envelope.identity_invalid", "The message does not carry a valid identity.");

    /// <summary>The re-drive count is missing, negative, above the limit or not typed as a number.</summary>
    public static Error RedriveCountInvalid { get; } =
        Error.Validation("transport.envelope.redrive_count_invalid", "The message carries an invalid re-drive count.");

    /// <summary>The broker message id is not the one the identity and re-drive count determine.</summary>
    public static Error MessageIdMismatch { get; } =
        Error.Validation("transport.envelope.message_id_mismatch", "The message id does not match the message identity.");

    /// <summary>The session is not the one the tenant determines.</summary>
    public static Error SessionMismatch { get; } =
        Error.Validation("transport.envelope.session_mismatch", "The session does not match the message tenant.");

    /// <summary>The correlation id is missing or malformed.</summary>
    public static Error CorrelationInvalid { get; } =
        Error.Validation("transport.envelope.correlation_invalid", "The message does not carry a valid correlation id.");

    /// <summary>The causation id is missing, empty or not typed as an identifier.</summary>
    public static Error CausationInvalid { get; } =
        Error.Validation("transport.envelope.causation_invalid", "The message does not carry a valid causation id.");

    /// <summary>The body is too large, is not the exact content-free shape of its type, or disagrees with it.</summary>
    public static Error BodyInvalid { get; } =
        Error.Validation("transport.envelope.body_invalid", "The message body does not match its message type.");

    /// <summary>The downstream handler kept failing transiently until the attempt limit.</summary>
    public static Error RetriesExhausted { get; } =
        Error.Infrastructure("transport.retries_exhausted", "The downstream handler did not succeed within the attempt limit.");

    /// <summary>The downstream handler threw instead of reporting an outcome. Treated as transient.</summary>
    public static Error HandlerFailed { get; } =
        Error.Infrastructure("transport.handler.failed", "The downstream handler failed unexpectedly.");

    /// <summary>The downstream handler refused the message permanently without a safe reason code of its own.</summary>
    public static Error HandlerRejected { get; } =
        Error.Validation("transport.handler.rejected", "The downstream handler refused the message permanently.");

    /// <summary>The broker did not accept a message. The outbox row stays pending.</summary>
    public static Error PublishFailed { get; } =
        Error.Infrastructure("transport.publish.failed", "The broker did not accept the message; it stays pending.");

    /// <summary>The message was already re-driven the maximum number of times.</summary>
    public static Error RedriveLimitReached { get; } =
        Error.Conflict("transport.redrive.limit_reached", "The message was already re-driven the maximum number of times.");

    /// <summary>Gets every envelope validation error: each is permanent, so the message is dead-lettered.</summary>
    public static IReadOnlyList<Error> EnvelopeErrors { get; } =
    [
        ContractUnsupported,
        MessageTypeUnsupported,
        ContentTypeInvalid,
        TenantInvalid,
        IdentityInvalid,
        RedriveCountInvalid,
        MessageIdMismatch,
        SessionMismatch,
        CorrelationInvalid,
        CausationInvalid,
        BodyInvalid,
    ];
}
