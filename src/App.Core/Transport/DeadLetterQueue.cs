using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Transport;

/// <summary>
/// One dead-lettered message as safe metadata: ids, tenant, type, reason code, counts and times. Never the
/// body, and never the free-text description the broker or a handler may have attached.
/// </summary>
/// <param name="SequenceNumber">The broker's sequence number in the dead-letter queue.</param>
/// <param name="Reason">The dead-letter reason when it is a safe token, otherwise <see cref="TransportContract.UnrecognizedReason"/>.</param>
/// <param name="DeliveryCount">How many times the broker delivered the message before it was dead-lettered.</param>
/// <param name="EnqueuedAtUtc">When the message was originally enqueued.</param>
/// <param name="Envelope">The validated envelope, or null when the message never formed one.</param>
public sealed record DeadLetteredMessage(
    long SequenceNumber,
    string Reason,
    int DeliveryCount,
    DateTimeOffset EnqueuedAtUtc,
    DeadLetteredEnvelope? Envelope)
{
    /// <summary>Describes a dead-lettered delivery from its broker metadata, validating the envelope again.</summary>
    public static DeadLetteredMessage From(ReceivedTransportMessage message, string? reason, DateTimeOffset enqueuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(message);

        var read = TransportEnvelope.Read(message);
        return new DeadLetteredMessage(
            message.SequenceNumber,
            TransportContract.IsSafeReason(reason) ? reason! : TransportContract.UnrecognizedReason,
            message.DeliveryCount,
            enqueuedAtUtc.ToUniversalTime(),
            read.IsSuccess ? DeadLetteredEnvelope.From(read.Value) : null);
    }
}

/// <summary>The identity of a dead-lettered message whose envelope is valid. Ids only.</summary>
/// <param name="OutboxMessageId">The stable identity, which names the message for a re-drive.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="MessageType">The message type.</param>
/// <param name="CorrelationId">The correlation id.</param>
/// <param name="CausationId">The causation id.</param>
/// <param name="RedriveCount">How many times it was already re-driven.</param>
public sealed record DeadLetteredEnvelope(
    Guid OutboxMessageId,
    TenantId TenantId,
    string MessageType,
    CorrelationId CorrelationId,
    Guid CausationId,
    int RedriveCount)
{
    /// <summary>The identity of a validated envelope.</summary>
    public static DeadLetteredEnvelope From(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new DeadLetteredEnvelope(
            envelope.OutboxMessageId,
            envelope.TenantId,
            envelope.MessageType,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.RedriveCount);
    }
}

/// <summary>What a re-drive request did.</summary>
public enum RedriveStatus
{
    /// <summary>Unspecified. Never returned.</summary>
    None = 0,

    /// <summary>A copy was sent back to the queue and the dead-lettered original removed.</summary>
    Redriven = 1,

    /// <summary>No dead-lettered message with a valid envelope has that identity within the scan limit.</summary>
    NotFound = 2,

    /// <summary>The message was already re-driven the maximum number of times; it stays dead-lettered.</summary>
    LimitReached = 3,
}

/// <summary>The outcome of one re-drive request.</summary>
/// <param name="Status">What happened.</param>
/// <param name="MessageId">The re-driven copy's broker message id, when one was sent.</param>
public sealed record RedriveOutcome(RedriveStatus Status, string? MessageId);

/// <summary>
/// Reads the dead-letter queue and re-drives one message on explicit request. Nothing calls
/// <see cref="RedriveAsync"/> automatically, so there is no re-drive loop.
/// </summary>
public interface IDeadLetterQueue
{
    /// <summary>Reads up to <paramref name="maxMessages"/> dead-lettered messages without locking or removing them.</summary>
    Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(int maxMessages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-drives the dead-lettered message with this identity: validates it, refuses at the re-drive limit,
    /// sends a copy with the same tenant, session, correlation, causation and identity and the next
    /// re-drive count, and only then removes the original.
    /// </summary>
    Task<RedriveOutcome> RedriveAsync(Guid outboxMessageId, CancellationToken cancellationToken = default);
}
