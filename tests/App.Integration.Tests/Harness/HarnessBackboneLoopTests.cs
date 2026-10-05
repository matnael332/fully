using Azure.Messaging.ServiceBus;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the harness's continuous loops. The relay and the consumer are the product's own, started on
/// flows that carry no tenant, so each resolves its tenants itself exactly as in production, and the consumer takes the
/// configured tenant's session only. Provider traffic stays fake over the in-memory broker and over the local Service
/// Bus emulator alike.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessBackboneLoopTests(DatabaseFixture fixture)
{
    private const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Board = "1771812698";
    private const string Item = "1772099001";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EmulatorConnectionString =>
        LocalEnvironment.Read().GetValueOrDefault("SERVICEBUS_TEST_CONNECTION_STRING", DefaultEmulatorConnectionString);

    [Fact]
    public async Task Started_from_a_flow_that_carries_another_tenant_the_loops_and_the_cleanup_still_run_on_flows_of_their_own()
    {
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var accessor = new TenantContextAccessor();

        // The harness is started by a caller whose flow already carries a tenant. Were any loop, the start-up cleanup or
        // the role check to inherit it, the relay would refuse every pass and the trusted flow could not assign its own.
        var harness = await Task.Run(
            async () =>
            {
                accessor.SetResolved(TenantContext.Resolved(foreign.Tenant, TenantResolutionSource.AuthenticatedPrincipal));
                return await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { RunLoops = true });
            },
            Token);
        await using var started = harness;

        Assert.True(harness.Host.Diagnostics.Snapshot().VerifierCleanup.StartupRunCompleted);
        var connection = await harness.AuthorizeAsync();

        // No sync is configured, so the delivery is processed as it arrives: the continuous relay publishes it with no
        // operator action, and the consumer hands it to processing in the harness tenant's scope.
        await harness.DeliverAsync(connection, Item, "Done", Board);
        var session = TransportEnvelope.SessionIdFor(harness.Tenant);
        await WaitUntilAsync(() => Task.FromResult(harness.Transport.Accepted.Any(wire => wire.SessionId == session)));
        Assert.Equal([TransportSettlementKind.Complete], await harness.PumpOwnAsync());

        var entry = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, harness.Tenant));
        Assert.Equal("ReconciliationRequired", entry.Outcome);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, foreign.Tenant));
        Assert.DoesNotContain(harness.Logs.Records, record => record.Message.Contains("relay pass failed", StringComparison.Ordinal));

        // A tick of the cleanup timer runs under the harness's own tenant too.
        await harness.TickCleanupAsync();
        Assert.Equal(2, harness.Host.Diagnostics.Snapshot().VerifierCleanup.Runs);
    }

    [Fact]
    public async Task Over_the_local_emulator_the_harness_publishes_its_own_delivery_and_consumes_its_own_session_only()
    {
        RequireEmulator();
        await DrainOlderPendingWorkAsync();

        var foreignTenant = TenantId.New();
        var foreignSession = TransportEnvelope.SessionIdFor(foreignTenant);
        var foreignEnvelope = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(),
            foreignTenant,
            Core.Ingress.IngressMessageTypes.ReceiptReceived,
            TransportData.Marker(Guid.CreateVersion7()),
            Core.Correlation.CorrelationId.New()).Value;

        await using var client = new ServiceBusClient(EmulatorConnectionString);
        try
        {
            await using var harness = await HarnessTestHost.StartAsync(
                fixture, new HarnessTestSetup { RunLoops = true, EmulatorConnectionString = EmulatorConnectionString });
            Assert.True(harness.Host.Diagnostics.Snapshot().Backbone.ConsumerStarted);
            var connection = await harness.AuthorizeAsync();

            // Another tenant's message is on the same queue, in its own session.
            var wire = foreignEnvelope.ToWire();
            await using (var sender = client.CreateSender(TransportContract.EventProcessingQueue))
            {
                var message = new ServiceBusMessage(wire.Body)
                {
                    MessageId = wire.MessageId,
                    SessionId = wire.SessionId,
                    CorrelationId = wire.CorrelationId,
                    ContentType = wire.ContentType,
                };
                foreach (var property in wire.ApplicationProperties)
                {
                    message.ApplicationProperties[property.Key] = property.Value;
                }

                await sender.SendMessageAsync(message, Token);
            }

            // The harness's own delivery travels the whole way with no operator action: intake, the relay, the emulator,
            // the consumer host and processing. The provider stays the fake.
            await harness.DeliverAsync(connection, Item, "Done", Board);
            await WaitUntilAsync(async () => (await ProcessingHarness.LedgerAsync(fixture, harness.Tenant)).Count == 1);
            Assert.Equal("ReconciliationRequired", Assert.Single(await ProcessingHarness.LedgerAsync(fixture, harness.Tenant)).Outcome);
            Assert.Equal(0L, await harness.PendingOutboxAsync());
            Assert.All(harness.Host.Diagnostics.Snapshot().ProviderCalls.Keys, key => Assert.NotEqual(WorkContinuity.Harness.Monday.Diagnostics.ProviderEndpoint.Other, key.Endpoint));
        }
        finally
        {
            // The other tenant's message was never taken: it is still there, undelivered, and this test removes exactly it.
            await using var session = await client.AcceptSessionAsync(TransportContract.EventProcessingQueue, foreignSession, cancellationToken: CancellationToken.None);
            var left = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (left is not null)
            {
                var deliveries = left.DeliveryCount;
                var messageId = left.MessageId;
                await session.CompleteMessageAsync(left, CancellationToken.None);
                Assert.Equal((1, foreignEnvelope.MessageId), (deliveries, messageId));
            }

            Assert.NotNull(left);
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

    // Relays every older pending outbox message into an in-memory broker, as the suite's own emulator tests do, so only
    // this test's own message reaches the emulator.
    private async Task DrainOlderPendingWorkAsync()
    {
        var sink = new InMemoryTransport();
        await using var drainer = TransportHost.InMemory(fixture, sink);
        for (var pass = 0; pass < 500; pass++)
        {
            var relayed = await drainer.Relay.RelayPendingAsync(Token);
            if (relayed.Tenants == 0 || (relayed.Published == 0 && !relayed.MoreWork))
            {
                return;
            }
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
        }

        Assert.Fail("The condition did not hold in time.");
    }
}
