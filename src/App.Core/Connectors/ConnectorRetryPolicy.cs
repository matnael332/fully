namespace WorkContinuity.Core.Connectors;

/// <summary>What a connector does after a failed attempt.</summary>
public readonly record struct ConnectorRetryDecision
{
    private ConnectorRetryDecision(bool retry, TimeSpan delay)
    {
        Retry = retry;
        Delay = delay;
    }

    /// <summary>Gets the decision to stop: the failure is returned to the caller.</summary>
    public static ConnectorRetryDecision Stop { get; } = new(retry: false, TimeSpan.Zero);

    /// <summary>Gets a value indicating whether to make another attempt.</summary>
    public bool Retry { get; }

    /// <summary>Gets how long to wait before the next attempt.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Retries after the given delay.</summary>
    public static ConnectorRetryDecision After(TimeSpan delay) => new(retry: true, delay);
}

/// <summary>
/// The bounded retry policy of a connector operation: only transient failures are retried, the provider's own timing
/// is honoured, and every limit is fixed in advance.
/// </summary>
/// <remarks>
/// <para>
/// Throttling, temporary provider failures and network failures are retried with exponential backoff and jitter, up to
/// <see cref="MaxAttempts"/> attempts in all. When the provider says how long to wait, the next attempt waits at least
/// that long. A provider wait longer than <see cref="MaxProviderDelay"/> is not slept through: the call stops and hands
/// the wait back to its caller, which reschedules, so a daily limit never holds a worker for hours and a throttled
/// provider is never hammered.
/// </para>
/// <para>
/// Every other failure is permanent and is returned after its first attempt. A descriptor allows more than one attempt
/// only for a read-only operation.
/// </para>
/// </remarks>
public sealed class ConnectorRetryPolicy
{
    /// <summary>The largest number of attempts any policy allows.</summary>
    public const int AttemptLimit = 10;

    /// <summary>Creates a policy.</summary>
    /// <param name="maxAttempts">The attempts in all, including the first; 1 means no retry.</param>
    /// <param name="baseDelay">The backoff after the first failed attempt, before jitter.</param>
    /// <param name="maxDelay">The largest backoff, before jitter.</param>
    /// <param name="maxProviderDelay">The longest provider-requested wait the connector sleeps through.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public ConnectorRetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan maxProviderDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, AttemptLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProviderDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxProviderDelay, TimeSpan.FromMinutes(5));

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        MaxProviderDelay = maxProviderDelay;
    }

    /// <summary>Gets the policy that never retries.</summary>
    public static ConnectorRetryPolicy None { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    /// Gets the default policy for read operations: 4 attempts, backoff from 1 second doubling to at most 30 seconds,
    /// and provider-requested waits of up to 60 seconds honoured in place.
    /// </summary>
    public static ConnectorRetryPolicy DefaultRead { get; } =
        new(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    /// <summary>Gets the attempts in all, including the first.</summary>
    public int MaxAttempts { get; }

    /// <summary>Gets the backoff after the first failed attempt, before jitter.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>Gets the largest backoff, before jitter.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>Gets the longest provider-requested wait the connector sleeps through.</summary>
    public TimeSpan MaxProviderDelay { get; }

    /// <summary>
    /// The backoff after a failed attempt: <see cref="BaseDelay"/> doubled per earlier attempt, capped at
    /// <see cref="MaxDelay"/>, of which the upper half is jittered.
    /// </summary>
    /// <param name="attempt">The failed attempt, starting at 1.</param>
    /// <param name="jitter">A uniform sample in [0, 1).</param>
    /// <exception cref="ArgumentOutOfRangeException">The attempt or the sample is out of range.</exception>
    public TimeSpan BackoffAfter(int attempt, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (!(jitter >= 0 && jitter < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), "The jitter sample must be in [0, 1).");
        }

        var ceiling = Math.Min(BaseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 30)), MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)((ceiling / 2) + (ceiling / 2 * jitter)));
    }

    /// <summary>Decides what follows a failed attempt.</summary>
    /// <param name="failure">The attempt's failure.</param>
    /// <param name="attempt">The failed attempt, starting at 1.</param>
    /// <param name="jitter">A uniform sample in [0, 1) for the backoff.</param>
    /// <exception cref="ArgumentOutOfRangeException">The attempt or the sample is out of range.</exception>
    public ConnectorRetryDecision Decide(ConnectorFailure failure, int attempt, double jitter)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var backoff = BackoffAfter(attempt, jitter);

        if (!failure.IsTransient || attempt >= MaxAttempts)
        {
            return ConnectorRetryDecision.Stop;
        }

        if (failure.RetryAfter is { } requested)
        {
            if (requested > MaxProviderDelay)
            {
                return ConnectorRetryDecision.Stop;
            }

            return ConnectorRetryDecision.After(requested > backoff ? requested : backoff);
        }

        return ConnectorRetryDecision.After(backoff);
    }
}
