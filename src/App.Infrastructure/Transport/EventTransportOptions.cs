using WorkContinuity.Core.Transport;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// Limits and intervals for the outbox relay, the transport consumer and the dead-letter queue. The
/// defaults suit production; every value is bounded so a misconfiguration fails at composition.
/// </summary>
public sealed class EventTransportOptions
{
    /// <summary>
    /// The most tenants one relay pass visits, oldest pending work first. The default is the discovery
    /// function's own cap, so tenants whose pending rows cannot be relayed can crowd others out of a pass only
    /// if there are a hundred of them.
    /// </summary>
    public int TenantsPerPass { get; set; } = 100;

    /// <summary>The most rows one relay transaction locks and publishes.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>The most batches one tenant gets per pass, so every tenant with work is served each pass.</summary>
    public int MaxBatchesPerTenant { get; set; } = 4;

    /// <summary>The pause between relay passes when no work remains.</summary>
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest pause after consecutive failed relay passes.</summary>
    public TimeSpan MaxFailureBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The consumer's bounded retry policy for transient downstream failures.</summary>
    public TransportRetryPolicy RetryPolicy { get; set; } = TransportRetryPolicy.Default;

    /// <summary>The most dead-lettered messages one re-drive request examines.</summary>
    public int MaxDeadLetterScan { get; set; } = 100;

    /// <summary>The most sessions the Service Bus consumer processes at once; one message at a time in each.</summary>
    public int MaxConcurrentSessions { get; set; } = 8;

    /// <summary>How long the Service Bus consumer waits on an idle session before moving to another.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the Service Bus consumer keeps renewing a message lock while a handler runs.</summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Restricts the Service Bus consumer to these sessions, for scoped diagnostics and tests. Empty, the
    /// default, means every session.
    /// </summary>
    public IList<string> ConsumerSessionIds { get; } = new List<string>();

    /// <summary>Checks every value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    /// <exception cref="ArgumentNullException">The retry policy is missing.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(TenantsPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TenantsPerPass, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchesPerTenant, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFailureBackoff, IdleInterval);
        ArgumentNullException.ThrowIfNull(RetryPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDeadLetterScan, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDeadLetterScan, 5000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SessionIdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxAutoLockRenewalDuration, TimeSpan.Zero);
    }
}
