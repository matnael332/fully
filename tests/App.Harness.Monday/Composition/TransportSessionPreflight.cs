using Azure.Messaging.ServiceBus;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Configuration;

namespace WorkContinuity.Harness.Monday.Composition;

/// <summary>
/// The shared-emulator check the harness runs before it publishes or consumes anything (owner decision B-15): the
/// configured tenant's own session on the event-processing queue must be empty.
/// </summary>
/// <remarks>
/// <para>
/// The harness shares the local emulator with the suite, so a run may start only on a session that holds nothing: a
/// message already waiting there was not sent by this run, and the run's evidence could not be told apart from it. The
/// check takes exactly the configured tenant's session, never another and never the next available one, peeks it, and
/// lets it go. Peeking locks no message, settles none and changes nothing in the queue. A session another receiver holds
/// cannot be taken, and the harness refuses to start then too: a run never shares its session.
/// </para>
/// <para>
/// What it does not prove, because no client of the emulator can: that the tenant was never used before, and that no
/// full-suite run is going on at the same time. Those stay operator preconditions of B-15. The harness's own consumer is
/// limited to the same session by its composition, so this check is about what was there before the run, not about
/// what the consumer may take.
/// </para>
/// </remarks>
internal static class TransportSessionPreflight
{
    /// <summary>The longest the check waits for the emulator before it refuses.</summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>None when the tenant's own session is empty; otherwise the closed reason the harness does not start.</summary>
    /// <param name="client">The client of the local emulator the harness was composed with.</param>
    /// <param name="tenant">The configured tenant, whose session is the only one taken.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    internal static async Task<HarnessRefusal> CheckAsync(ServiceBusClient client, TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(Bound);
        try
        {
            await using var session = await client.AcceptSessionAsync(
                TransportContract.EventProcessingQueue,
                TransportEnvelope.SessionIdFor(tenant),
                new ServiceBusSessionReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = 0 },
                bounded.Token);
            var waiting = await session.PeekMessageAsync(cancellationToken: bounded.Token);
            return waiting is null ? HarnessRefusal.None : HarnessRefusal.TransportSessionNotEmpty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HarnessRefusal.TransportSessionUnavailable;
        }
        catch (ServiceBusException)
        {
            return HarnessRefusal.TransportSessionUnavailable;
        }
    }
}
