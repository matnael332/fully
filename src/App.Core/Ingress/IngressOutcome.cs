using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Ingress;

/// <summary>What happened to a delivery.</summary>
public enum IngressStatus
{
    /// <summary>Unspecified. Never returned.</summary>
    None = 0,

    /// <summary>Durably stored with its outbox marker. Acknowledge the source.</summary>
    Accepted = 1,

    /// <summary>
    /// Already durably stored by an earlier delivery of the same source event. Acknowledge the source;
    /// nothing new was written.
    /// </summary>
    Duplicate = 2,

    /// <summary>Refused permanently, before any payload was persisted. Do not retry.</summary>
    Rejected = 3,

    /// <summary>Not stored. Do not acknowledge; the source must retry.</summary>
    Failed = 4,
}

/// <summary>
/// The final result of one delivery. It is returned only after the outcome is settled: an
/// acknowledged outcome means the receipt and its outbox marker are committed.
/// </summary>
/// <remarks>
/// The outcome carries identifiers and codes only, never the payload or the proof, so it is safe to log
/// or return to a caller.
/// </remarks>
public sealed record IngressOutcome
{
    private IngressOutcome(
        IngressStatus status,
        Guid? receiptId,
        QuarantineReason rejectionReason,
        Error? error,
        CorrelationId correlationId)
    {
        Status = status;
        ReceiptId = receiptId;
        RejectionReason = rejectionReason;
        Error = error;
        CorrelationId = correlationId;
    }

    /// <summary>Gets what happened.</summary>
    public IngressStatus Status { get; }

    /// <summary>Gets the durable receipt, for an acknowledged outcome.</summary>
    public Guid? ReceiptId { get; }

    /// <summary>Gets the canonical reason for a rejection, otherwise <see cref="QuarantineReason.None"/>.</summary>
    public QuarantineReason RejectionReason { get; }

    /// <summary>Gets the error code for a rejection or failure, otherwise null.</summary>
    public Error? Error { get; }

    /// <summary>Gets the correlation id the delivery was processed under.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>
    /// Gets a value indicating whether the source may be acknowledged: only when the delivery is durably
    /// stored, now or by an earlier delivery.
    /// </summary>
    public bool IsAcknowledged => Status is IngressStatus.Accepted or IngressStatus.Duplicate;

    /// <summary>The delivery was durably stored with its outbox marker.</summary>
    /// <exception cref="ArgumentException">The receipt id or correlation id is empty.</exception>
    public static IngressOutcome Accepted(Guid receiptId, CorrelationId correlationId) =>
        new(IngressStatus.Accepted, RequireReceipt(receiptId), QuarantineReason.None, error: null, RequireCorrelation(correlationId));

    /// <summary>The delivery repeats a source event that is already durably stored.</summary>
    /// <exception cref="ArgumentException">The receipt id or correlation id is empty.</exception>
    public static IngressOutcome Duplicate(Guid existingReceiptId, CorrelationId correlationId) =>
        new(IngressStatus.Duplicate, RequireReceipt(existingReceiptId), QuarantineReason.None, error: null, RequireCorrelation(correlationId));

    /// <summary>The delivery was refused permanently before any payload was persisted.</summary>
    /// <exception cref="ArgumentException">The error is not an ingress rejection, or the correlation id is empty.</exception>
    public static IngressOutcome Rejected(Error error, CorrelationId correlationId) =>
        new(IngressStatus.Rejected, receiptId: null, IngressErrors.RejectionReasonFor(error), error, RequireCorrelation(correlationId));

    /// <summary>The delivery was not stored and must be retried by the source.</summary>
    /// <exception cref="ArgumentException">The correlation id is empty.</exception>
    public static IngressOutcome Failed(CorrelationId correlationId) =>
        new(IngressStatus.Failed, receiptId: null, QuarantineReason.None, IngressErrors.Unavailable, RequireCorrelation(correlationId));

    private static Guid RequireReceipt(Guid receiptId) => receiptId == Guid.Empty
        ? throw new ArgumentException("An acknowledged outcome requires a receipt id.", nameof(receiptId))
        : receiptId;

    private static CorrelationId RequireCorrelation(CorrelationId correlationId) => correlationId.IsEmpty
        ? throw new ArgumentException("An outcome requires a correlation id.", nameof(correlationId))
        : correlationId;
}
