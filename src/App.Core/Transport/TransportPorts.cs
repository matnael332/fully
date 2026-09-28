using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Transport;

/// <summary>Sends validated envelopes to the broker.</summary>
/// <remarks>
/// Success means the broker accepted the message durably; only then may the outbox row be marked
/// published. A failure is returned, never thrown, as a stable reason code with no SDK message in it.
/// </remarks>
public interface IEventTransportPublisher
{
    /// <summary>Publishes one message to its queue.</summary>
    /// <param name="envelope">The validated envelope.</param>
    /// <param name="cancellationToken">Cancels the send; the outcome is then unknown, which republication absorbs.</param>
    /// <returns>Success once the broker accepted the message.</returns>
    Task<Result> PublishAsync(TransportEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>What a downstream handler reports for one message.</summary>
public enum TransportHandlingStatus
{
    /// <summary>Unspecified. Never reported.</summary>
    None = 0,

    /// <summary>The message is fully handled; it may be completed.</summary>
    Succeeded = 1,

    /// <summary>A transient failure; the message is retried within the attempt limit.</summary>
    RetryLater = 2,

    /// <summary>A permanent failure; the message is dead-lettered and never retried automatically.</summary>
    Rejected = 3,
}

/// <summary>A downstream handler's outcome for one message.</summary>
public sealed record TransportHandlingResult
{
    private TransportHandlingResult(TransportHandlingStatus status, Error? reason)
    {
        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the outcome.</summary>
    public TransportHandlingStatus Status { get; }

    /// <summary>Gets the reason for a retry or a rejection, otherwise null.</summary>
    public Error? Reason { get; }

    /// <summary>Gets the success outcome.</summary>
    public static TransportHandlingResult Succeeded { get; } = new(TransportHandlingStatus.Succeeded, reason: null);

    /// <summary>A transient failure: retry the message later.</summary>
    public static TransportHandlingResult RetryLater(Error reason) =>
        new(TransportHandlingStatus.RetryLater, reason ?? throw new ArgumentNullException(nameof(reason)));

    /// <summary>A permanent failure: dead-letter the message. The reason code becomes the dead-letter reason.</summary>
    public static TransportHandlingResult Rejected(Error reason) =>
        new(TransportHandlingStatus.Rejected, reason ?? throw new ArgumentNullException(nameof(reason)));
}

/// <summary>
/// The downstream handler the transport consumer hands each validated message to. Processing belongs to
/// Gate 2D; the transport only guarantees that a handler sees validated messages, in a tenant scope
/// restored from the validated envelope, and that nothing is settled before it reports.
/// </summary>
/// <remarks>
/// Delivery is at least once: the same envelope, with the same identity, can arrive again after a crash or
/// a republication, so a handler must be idempotent on <see cref="TransportEnvelope.OutboxMessageId"/>.
/// </remarks>
public interface ITransportMessageHandler
{
    /// <summary>Handles one validated message inside its tenant scope.</summary>
    Task<TransportHandlingResult> HandleAsync(TransportEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>How a transport adapter must settle a received message.</summary>
public enum TransportSettlementKind
{
    /// <summary>Unspecified. Never returned.</summary>
    None = 0,

    /// <summary>Remove the message: it is fully handled.</summary>
    Complete = 1,

    /// <summary>Wait <see cref="TransportSettlement.RetryAfter"/>, then release the message for redelivery.</summary>
    Abandon = 2,

    /// <summary>Move the message to the dead-letter queue with the given reason.</summary>
    DeadLetter = 3,
}

/// <summary>The settlement the consumer decided for one received message.</summary>
public sealed record TransportSettlement
{
    private TransportSettlement(TransportSettlementKind kind, TimeSpan retryAfter, Error? deadLetterReason)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        DeadLetterReason = deadLetterReason;
    }

    /// <summary>Gets how to settle.</summary>
    public TransportSettlementKind Kind { get; }

    /// <summary>Gets the backoff before an abandon; zero otherwise.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>
    /// Gets the dead-letter reason: its code is the broker's dead-letter reason and its message the fixed
    /// description. Null unless dead-lettering.
    /// </summary>
    public Error? DeadLetterReason { get; }

    /// <summary>Gets the completion settlement.</summary>
    public static TransportSettlement Complete { get; } = new(TransportSettlementKind.Complete, TimeSpan.Zero, deadLetterReason: null);

    /// <summary>Release the message for redelivery after a backoff.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The backoff is negative.</exception>
    public static TransportSettlement Abandon(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryAfter, TimeSpan.Zero);
        return new TransportSettlement(TransportSettlementKind.Abandon, retryAfter, deadLetterReason: null);
    }

    /// <summary>Dead-letter the message.</summary>
    /// <exception cref="ArgumentException">The reason code is not a safe token.</exception>
    public static TransportSettlement DeadLetter(Error reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!TransportContract.IsSafeReason(reason.Code))
        {
            throw new ArgumentException("A dead-letter reason must be a safe token.", nameof(reason));
        }

        return new TransportSettlement(TransportSettlementKind.DeadLetter, TimeSpan.Zero, reason);
    }
}

/// <summary>
/// The transport-level consumer: validates a received message, restores its tenant and hands it to the
/// downstream handler, then decides how the adapter settles it.
/// </summary>
/// <remarks>
/// Transport adapters (Service Bus, or the in-memory test transport) call this for every received message
/// and apply the returned settlement exactly; nothing is completed before the handler has reported.
/// </remarks>
public interface IEventTransportConsumer
{
    /// <summary>Consumes one received message and returns its settlement.</summary>
    Task<TransportSettlement> ConsumeAsync(ReceivedTransportMessage message, CancellationToken cancellationToken = default);
}
