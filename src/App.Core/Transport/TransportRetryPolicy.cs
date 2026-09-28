using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Transport;

/// <summary>
/// The bounded retry policy for transient downstream failures: exponential backoff with jitter, and the
/// dead-letter queue once the attempt limit is reached.
/// </summary>
/// <remarks>
/// <para>
/// The attempt is the broker's delivery count, which also counts deliveries lost to a crash or an expired
/// lock, so no message is retried without bound. The limit must not exceed the queue's own maximum delivery
/// count (5 in the local emulator, 10 in Azure): the consumer, not the broker, then decides, and records
/// why, a message was dead-lettered.
/// </para>
/// <para>
/// A permanent failure is never retried. The handler's reason code becomes the dead-letter reason only when
/// it is a safe token, and the dead-letter description is always the transport's own fixed text, so no
/// handler can put content into the dead-letter queue.
/// </para>
/// </remarks>
public sealed class TransportRetryPolicy
{
    /// <summary>The default attempt limit.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Creates a policy.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public TransportRetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
    }

    /// <summary>Gets the default policy: 5 attempts, backoff from 1 second doubling to at most 30 seconds.</summary>
    public static TransportRetryPolicy Default { get; } =
        new(DefaultMaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    /// <summary>Gets how many deliveries a message gets before a transient failure dead-letters it.</summary>
    public int MaxAttempts { get; }

    /// <summary>Gets the backoff after the first failed attempt, before jitter.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>Gets the largest backoff, before jitter.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// The backoff after a failed attempt: <see cref="BaseDelay"/> doubled per earlier attempt, capped at
    /// <see cref="MaxDelay"/>, of which the upper half is jittered so that retries spread out.
    /// </summary>
    /// <param name="attempt">The failed attempt, starting at 1.</param>
    /// <param name="jitter">A uniform sample in [0, 1).</param>
    /// <exception cref="ArgumentOutOfRangeException">The attempt or the sample is out of range.</exception>
    public TimeSpan DelayAfter(int attempt, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (!(jitter >= 0 && jitter < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), "The jitter sample must be in [0, 1).");
        }

        var ceiling = Math.Min(BaseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 30)), MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)((ceiling / 2) + (ceiling / 2 * jitter)));
    }

    /// <summary>Decides how a delivery is settled once the handler has reported.</summary>
    /// <param name="result">The handler's outcome.</param>
    /// <param name="deliveryCount">The broker's delivery count for this delivery, starting at 1.</param>
    /// <param name="jitter">A uniform sample in [0, 1) for the backoff.</param>
    /// <exception cref="ArgumentException">The outcome is unspecified.</exception>
    public TransportSettlement Settle(TransportHandlingResult result, int deliveryCount, double jitter)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            TransportHandlingStatus.Succeeded => TransportSettlement.Complete,
            TransportHandlingStatus.Rejected => TransportSettlement.DeadLetter(RejectionReason(result.Reason)),
            TransportHandlingStatus.RetryLater => deliveryCount >= MaxAttempts
                ? TransportSettlement.DeadLetter(TransportErrors.RetriesExhausted)
                : TransportSettlement.Abandon(DelayAfter(Math.Max(deliveryCount, 1), jitter)),
            _ => throw new ArgumentException("A handler outcome must be reported.", nameof(result)),
        };
    }

    // The handler's code is kept when it is a safe token; its message never is.
    private static Error RejectionReason(Error? reason) =>
        reason is not null && TransportContract.IsSafeReason(reason.Code)
            ? new Error(TransportErrors.HandlerRejected.Category, reason.Code, TransportErrors.HandlerRejected.Message)
            : TransportErrors.HandlerRejected;
}
