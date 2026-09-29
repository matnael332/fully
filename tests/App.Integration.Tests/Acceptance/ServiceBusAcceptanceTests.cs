using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Observability;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Acceptance;

/// <summary>
/// The Gate 2 whole path over the local Service Bus emulator, with the real consumer host and processing as its
/// downstream handler: out-of-order events converge on the source's truth, a republished message id is absorbed, and a
/// forged message is dead-lettered and changes no tenant.
/// </summary>
/// <remarks>
/// The emulator runs locally under <c>./eng/dev.ps1 up</c> but not in CI, where no emulator licence is accepted (Gate 2
/// owner decision 8); these tests skip there with an explicit reason. Older pending outbox work is first relayed aside, so
/// only the test's own messages reach the emulator; each test uses fresh tenants, so its sessions are its own, and it
/// removes the dead-lettered messages of those sessions afterwards.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ServiceBusAcceptanceTests(DatabaseFixture fixture)
{
    private const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EmulatorConnectionString =>
        LocalEnvironment.Read().GetValueOrDefault("SERVICEBUS_TEST_CONNECTION_STRING", DefaultEmulatorConnectionString);

    [Fact]
    public async Task Out_of_order_events_converge_over_the_emulator_and_a_republished_message_id_is_absorbed()
    {
        RequireEmulator();
        await WholePath.DrainOlderPendingWorkAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var session = TransportEnvelope.SessionIdFor(tenant);
        await using var host = Host(session);
        using var metrics = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var working = Stated("evt-emulator-1", "1001", 1, "working");
        var review = Stated("evt-emulator-2", "1001", 2, "review");
        var done = Stated("evt-emulator-3", "1001", 3, "done");
        var follower = Stated("evt-emulator-follower", "1002", 1, "done");

        try
        {
            // The newest revision first, the older ones delayed behind it.
            var receipts = new List<Guid>();
            foreach (var stated in new[] { done, working, review })
            {
                receipts.Add(await SubmitAsync(host, connection, stated));
            }

            await host.RelayUntilPublishedAsync(fixture, await OutboxIdsAsync(receipts), Token);

            // The newest one is republished under its message id, as the relay does after dying before its mark.
            Assert.True((await host.Publisher.PublishAsync(await EnvelopeForAsync(tenant, receipts[0]), Token)).IsSuccess);

            // One more message behind it: once that one is processed, nothing ahead of it in the session is waiting.
            receipts.Add(await SubmitAsync(host, connection, follower));
            await host.RelayUntilPublishedAsync(fixture, await OutboxIdsAsync([receipts[^1]]), Token);

            await host.ConsumerHost.StartAsync(Token);
            await WaitUntilAsync(async () => (await ProcessingHarness.LedgerAsync(fixture, tenant)).Count == receipts.Count);

            await WholePath.AssertFactsAsync(fixture, tenant, [done.FactFor("status"), follower.FactFor("status")]);
            await WholePath.AssertProvenanceAsync(fixture, tenant);
            Assert.Equal(
                new[] { (done.Id, "AcceptedForProjection"), (working.Id, "Stale"), (review.Id, "Stale"), (follower.Id, "AcceptedForProjection") }.Order(),
                (await ProcessingHarness.LedgerAsync(fixture, tenant)).Select(entry => (entry.SourceEventId!, entry.Outcome)).Order());
            Assert.Equal(0, metrics.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
            Assert.Empty(await DeadLettersAsync(host, tenant));
        }
        finally
        {
            await host.ConsumerHost.StopAsync(CancellationToken.None);
            await RemoveDeadLettersAsync(session);
        }
    }

    [Fact]
    public async Task A_forged_message_on_the_emulator_is_dead_lettered_and_changes_no_tenant()
    {
        RequireEmulator();
        await WholePath.DrainOlderPendingWorkAsync(fixture);
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var sessionA = TransportEnvelope.SessionIdFor(tenantA);
        var sessionB = TransportEnvelope.SessionIdFor(tenantB);
        await using var host = Host(sessionA, sessionB);
        var a = Stated("evt-emulator-victim", "1001", 1, "a-done");
        var b = Stated("evt-emulator-victim", "1001", 1, "b-done");

        try
        {
            var receiptA = await SubmitAsync(host, connectionA, a);
            await host.RelayUntilPublishedAsync(fixture, await OutboxIdsAsync([receiptA]), Token);

            // Tenant B's identity naming A's receipt, sent by someone with send rights, ahead of B's own event.
            var forged = TransportEnvelope.FromOutbox(
                Guid.CreateVersion7(), tenantB, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptA), CorrelationId.New()).Value;
            Assert.True((await host.Publisher.PublishAsync(forged, Token)).IsSuccess);
            var receiptB = await SubmitAsync(host, connectionB, b);
            await host.RelayUntilPublishedAsync(fixture, await OutboxIdsAsync([receiptB]), Token);

            await host.ConsumerHost.StartAsync(Token);
            await WaitUntilAsync(async () =>
                (await ProcessingHarness.LedgerAsync(fixture, tenantA)).Count == 1
                && (await ProcessingHarness.LedgerAsync(fixture, tenantB)).Count == 1
                && (await DeadLettersAsync(host, tenantB)).Count == 1);

            var dead = Assert.Single(await DeadLettersAsync(host, tenantB));
            Assert.Equal((ProcessingErrors.ReceiptNotVisible.Code, forged.OutboxMessageId, receiptA), (dead.Reason, dead.Envelope!.OutboxMessageId, dead.Envelope.CausationId));

            // Each tenant holds exactly its own event, with the same ids in both.
            await WholePath.AssertFactsAsync(fixture, tenantA, [a.FactFor("status")]);
            await WholePath.AssertFactsAsync(fixture, tenantB, [b.FactFor("status")]);
            Assert.Equal(receiptA, Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenantA)).ReceiptId);
            Assert.Equal(receiptB, Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenantB)).ReceiptId);
            Assert.Empty(await DeadLettersAsync(host, tenantA));
        }
        finally
        {
            await host.ConsumerHost.StopAsync(CancellationToken.None);
            await RemoveDeadLettersAsync(sessionA);
            await RemoveDeadLettersAsync(sessionB);
        }
    }

    private static void RequireEmulator()
    {
        if (!LocalEnvironment.EmulatorExpected)
        {
            Assert.Skip(
                "No Service Bus emulator is expected here (.env has no ACCEPT_EULA=Y and "
                + "SERVICEBUS_TEST_CONNECTION_STRING is unset). Run './eng/dev.ps1 up' locally to execute this test.");
        }
    }

    private static SourceEvent Stated(string id, string objectId, long revision, string status) =>
        new(id, new SyntheticEvent
        {
            ObjectId = objectId,
            Revision = revision,
            UpdatedAt = T0.AddMinutes(revision),
            Changes = [new("status", "TaskStatus", null, status)],
        });

    // Ingress, the relay and the consumer host on the emulator, with processing as the downstream handler, consuming
    // only the test's own sessions.
    private TransportHost Host(params string[] sessions) =>
        TransportHost.ServiceBus(
            fixture,
            EmulatorConnectionString,
            options =>
            {
                foreach (var session in sessions)
                {
                    options.ConsumerSessionIds.Add(session);
                }

                options.SessionIdleTimeout = TimeSpan.FromSeconds(2);
                options.MaxDeadLetterScan = 5000;
            },
            handlers: services =>
            {
                services.AddEventProcessing();
                services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            });

    private async Task<Guid> SubmitAsync(TransportHost host, Guid connection, SourceEvent stated)
    {
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, stated.Id, stated.Event.ToPayload()));
        Assert.True(outcome.Status == IngressStatus.Accepted, $"{stated.Id}: {outcome.Status} {outcome.Error?.Code}");
        return outcome.ReceiptId!.Value;
    }

    private async Task<IReadOnlyList<Guid>> OutboxIdsAsync(IEnumerable<Guid> receipts)
    {
        var ids = new List<Guid>();
        foreach (var receipt in receipts)
        {
            ids.Add(await TransportData.OutboxIdForReceiptAsync(fixture, receipt, Token));
        }

        return ids;
    }

    // The envelope the relay built for the receipt's marker, with its own correlation.
    private async Task<TransportEnvelope> EnvelopeForAsync(TenantId tenant, Guid receipt)
    {
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receipt, Token);
        var marker = Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token), candidate => candidate.OutboxMessageId == outboxId);
        Assert.True(CorrelationId.TryParse(marker.CorrelationId, out var correlation));
        var envelope = TransportEnvelope.FromOutbox(outboxId, tenant, marker.MessageType, TransportData.Marker(receipt), correlation);
        Assert.True(envelope.IsSuccess);
        return envelope.Value;
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The emulator did not reach the expected state in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Token);
        }
    }

    private async Task<IReadOnlyList<DeadLetteredMessage>> DeadLettersAsync(TransportHost host, TenantId tenant) =>
        [.. (await host.ServiceBusDeadLetters.PeekAsync(5000, Token)).Where(message => message.Envelope?.TenantId == tenant)];

    // Removes only the dead-lettered messages of this test's own session; everything else is released untouched.
    private static async Task RemoveDeadLettersAsync(string sessionId)
    {
        await using var client = new ServiceBusClient(EmulatorConnectionString);
        await using var receiver = client.CreateReceiver(
            TransportContract.EventProcessingQueue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var others = new List<ServiceBusReceivedMessage>();
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2), CancellationToken.None);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var message in batch)
            {
                if (string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
                {
                    await receiver.CompleteMessageAsync(message, CancellationToken.None);
                }
                else
                {
                    others.Add(message);
                }
            }
        }

        foreach (var message in others)
        {
            await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
    }
}
