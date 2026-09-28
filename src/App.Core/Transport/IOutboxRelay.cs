namespace WorkContinuity.Core.Transport;

/// <summary>
/// Publishes committed outbox messages to the broker and marks each published only after the broker
/// accepted it.
/// </summary>
/// <remarks>
/// Delivery is at least once. A crash before a send leaves the row pending; a crash after the send and
/// before the mark republishes it with the same message id, which is safe by contract. No outbox row is
/// ever marked published without having been accepted, and none is dropped.
/// </remarks>
public interface IOutboxRelay
{
    /// <summary>Runs one relay pass over the tenants that have pending messages.</summary>
    Task<OutboxRelayPass> RelayPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs passes until cancelled: again at once while work remains, after the idle interval when none
    /// does, and after a bounded, growing backoff while passes fail.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one relay pass did. Counts only.</summary>
/// <param name="Tenants">Tenants with pending messages that the pass visited.</param>
/// <param name="Published">Messages the broker accepted and the pass marked published.</param>
/// <param name="Unrelayable">Rows that cannot form a valid envelope; never published, never marked.</param>
/// <param name="PublishFailures">Tenants whose batch stopped at a failed send.</param>
/// <param name="CommitFailures">Tenants whose marks could not be committed; their messages are republished later.</param>
/// <param name="DiscoveryFailed">Whether finding the tenants with pending work failed.</param>
/// <param name="MoreWork">Whether a tenant reached the pass's batch limit, so work may remain.</param>
public sealed record OutboxRelayPass(
    int Tenants,
    int Published,
    int Unrelayable,
    int PublishFailures,
    int CommitFailures,
    bool DiscoveryFailed,
    bool MoreWork)
{
    /// <summary>Gets a value indicating whether any part of the pass failed and should be retried after a backoff.</summary>
    public bool HasFailures => DiscoveryFailed || PublishFailures > 0 || CommitFailures > 0;
}
