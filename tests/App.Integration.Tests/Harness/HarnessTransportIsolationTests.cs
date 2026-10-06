using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G Stage B3, area D (owner decision B-15): the harness on the shared local emulator. Before it publishes or
/// consumes anything, the configured tenant's own session must be empty and must be the harness's to take; the
/// composed consumer is limited to that one session; and a broker that cannot be reached refuses the start.
/// </summary>
/// <remarks>
/// These tests show what the harness can check mechanically. They do not and cannot show the operator preconditions of
/// B-15 that no client of the emulator can observe: that the tenant GUID was never used before, and that no full-suite
/// run shares the emulator during a run. The emulator tests send to sessions of tenants that exist only in the test and
/// remove exactly the messages they sent; they never reset, drain or reconfigure the emulator.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessTransportIsolationTests(DatabaseFixture fixture)
{
    private const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    // A loopback address on which nothing listens: valid for the configuration boundary, unreachable for the client.
    private const string UnreachableEmulator =
        "Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EmulatorConnectionString =>
        LocalEnvironment.Read().GetValueOrDefault("SERVICEBUS_TEST_CONNECTION_STRING", DefaultEmulatorConnectionString);

    [Fact]
    public async Task The_composed_consumer_is_limited_to_exactly_the_configured_tenants_session()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);

        // One session, the configured tenant's: never empty, which would mean every session, and never another tenant's.
        Assert.Equal([TransportEnvelope.SessionIdFor(harness.Tenant)], harness.Host.ConsumerSessions);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_refuses_the_start_within_the_bound()
    {
        var (tenant, options) = await OptionsAsync(UnreachableEmulator);

        var clock = Stopwatch.StartNew();
        var started = await HarnessHost.StartAsync(options, Seams(), Token);

        Assert.Null(started.Host);
        Assert.Equal(HarnessRefusal.TransportSessionUnavailable, started.Refusal);
        Assert.True(clock.Elapsed < TransportSessionPreflight.Bound + TimeSpan.FromSeconds(15), clock.Elapsed.ToString());
        Assert.NotEqual(Guid.Empty, tenant.Value);
    }

    [Fact]
    public async Task Over_the_local_emulator_a_message_already_in_the_own_session_refuses_the_start_and_is_left_untouched()
    {
        RequireEmulator();
        var (tenant, options) = await OptionsAsync(EmulatorConnectionString);
        var waiting = Envelope(tenant);

        await using var client = new ServiceBusClient(EmulatorConnectionString);
        await SendAsync(client, waiting);
        try
        {
            var started = await HarnessHost.StartAsync(options, Seams(), Token);

            Assert.Null(started.Host);
            Assert.Equal(HarnessRefusal.TransportSessionNotEmpty, started.Refusal);
        }
        finally
        {
            // The check only peeked: the message was never locked, so this is its first delivery. This test removes it.
            Assert.Equal(1, await RemoveAsync(client, tenant, waiting.MessageId));
        }

        // Once the session is empty again, the same configuration starts.
        var again = await HarnessHost.StartAsync(options, Seams(), Token);
        await using (Assert.IsType<HarnessHost>(again.Host))
        {
            Assert.Equal(HarnessRefusal.None, again.Refusal);
        }
    }

    [Fact]
    public async Task Over_the_local_emulator_another_tenants_waiting_message_does_not_matter_and_is_left_untouched()
    {
        RequireEmulator();
        var (tenant, options) = await OptionsAsync(EmulatorConnectionString);
        var foreignTenant = TenantId.New();
        var foreign = Envelope(foreignTenant);

        await using var client = new ServiceBusClient(EmulatorConnectionString);
        await SendAsync(client, foreign);
        try
        {
            var started = await HarnessHost.StartAsync(options, Seams(), Token);
            await using (Assert.IsType<HarnessHost>(started.Host))
            {
                Assert.Equal(HarnessRefusal.None, started.Refusal);
                Assert.Equal([TransportEnvelope.SessionIdFor(tenant)], started.Host!.ConsumerSessions);
            }
        }
        finally
        {
            // Never taken by the check or the harness: still undelivered, and this test removes exactly it.
            Assert.Equal(1, await RemoveAsync(client, foreignTenant, foreign.MessageId));
        }
    }

    [Fact]
    public async Task Over_the_local_emulator_an_own_session_another_receiver_holds_refuses_the_start()
    {
        RequireEmulator();
        var (tenant, options) = await OptionsAsync(EmulatorConnectionString);

        await using var client = new ServiceBusClient(EmulatorConnectionString);
        await using (var holder = await client.AcceptSessionAsync(TransportContract.EventProcessingQueue, TransportEnvelope.SessionIdFor(tenant), cancellationToken: Token))
        {
            var started = await HarnessHost.StartAsync(options, Seams(), Token);

            Assert.Null(started.Host);
            Assert.Equal(HarnessRefusal.TransportSessionUnavailable, started.Refusal);
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

    private static HarnessSeams Seams() => new()
    {
        OperatorCertificate = LoopbackCertificate.Instance,
        LogSink = new CapturingHarnessSink(),
    };

    private async Task<(TenantId Tenant, HarnessOptions Options)> OptionsAsync(string emulator)
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        return (tenant, HarnessTestHost.OptionsFor(fixture.ApplicationConnectionString, tenant, user, HarnessTestHost.FreePort(), emulator));
    }

    private static TransportEnvelope Envelope(TenantId tenant) =>
        TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(),
            tenant,
            IngressMessageTypes.ReceiptReceived,
            TransportData.Marker(Guid.CreateVersion7()),
            CorrelationId.New()).Value;

    private static async Task SendAsync(ServiceBusClient client, TransportEnvelope envelope)
    {
        var wire = envelope.ToWire();
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

        await using var sender = client.CreateSender(TransportContract.EventProcessingQueue);
        await sender.SendMessageAsync(message, Token);
    }

    // Takes the tenant's session, completes exactly the named message, and returns its delivery count.
    private static async Task<int> RemoveAsync(ServiceBusClient client, TenantId tenant, string messageId)
    {
        await using var session = await client.AcceptSessionAsync(
            TransportContract.EventProcessingQueue, TransportEnvelope.SessionIdFor(tenant), cancellationToken: CancellationToken.None);
        var message = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(message);
        Assert.Equal(messageId, message.MessageId);
        var deliveries = message.DeliveryCount;
        await session.CompleteMessageAsync(message, CancellationToken.None);
        return deliveries;
    }
}
