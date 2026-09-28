using Azure.Messaging.ServiceBus;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>
/// Gate 2C against the real Service Bus emulator: the relay's messages arrive on <c>event-processing</c>
/// under the contract's message id, session and properties; the consumer host completes, retries and
/// dead-letters them; the dead-letter queue reads back as safe metadata; re-drive returns a message under a
/// new message id with its identity intact; and duplicate detection absorbs a republished message id.
/// </summary>
/// <remarks>
/// The emulator runs locally under <c>./eng/dev.ps1 up</c> but not in CI, where no emulator licence is
/// accepted (Gate 2 owner decision 8); these tests skip there with an explicit reason. Each test uses fresh
/// tenants, so its sessions are its own, and removes the dead-lettered messages it created by exact id.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ServiceBusTransportTests(DatabaseFixture fixture)
{
    private const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EmulatorConnectionString =>
        LocalEnvironment.Read().GetValueOrDefault("SERVICEBUS_TEST_CONNECTION_STRING", DefaultEmulatorConnectionString);

    [Fact]
    public async Task The_relay_publishes_to_the_emulator_under_the_contract_identity_and_session()
    {
        RequireEmulator();
        await DrainOlderPendingWorkAsync();

        await using var host = TransportHost.ServiceBus(fixture, EmulatorConnectionString);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var correlation = CorrelationId.New();
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-emulator-relay", IngressData.PayloadWith(IngressData.NewMarker()), correlation));
        var receiptId = Assert.NotNull(outcome.ReceiptId);
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, Token);

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        await using var client = new ServiceBusClient(EmulatorConnectionString);
        await using var session = await client.AcceptSessionAsync(
            TransportContract.EventProcessingQueue, TransportEnvelope.SessionIdFor(tenant), cancellationToken: Token);
        var message = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(30), Token);

        Assert.NotNull(message);
        Assert.Equal(outboxId.ToString("D"), message.MessageId);
        Assert.Equal(tenant.Value.ToString("D"), message.SessionId);
        Assert.Equal(correlation.Value, message.CorrelationId);
        Assert.Equal(TransportContract.ContentType, message.ContentType);
        Assert.Equal(tenant.Value, Assert.IsType<Guid>(message.ApplicationProperties[TransportProperties.Tenant]));
        Assert.Equal(outboxId, Assert.IsType<Guid>(message.ApplicationProperties[TransportProperties.OutboxMessage]));
        Assert.Equal(receiptId, Assert.IsType<Guid>(message.ApplicationProperties[TransportProperties.Causation]));
        Assert.Equal(0, Assert.IsType<int>(message.ApplicationProperties[TransportProperties.RedriveCount]));
        Assert.Equal(TransportData.Marker(receiptId), message.Body.ToString());

        // What arrived is a valid envelope in the consumer's eyes, too.
        var read = TransportEnvelope.Read(Received(message));
        Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
        Assert.Equal(receiptId, read.Value.CausationId);

        await session.CompleteMessageAsync(message, Token);
    }

    [Fact]
    public async Task The_consumer_host_completes_retries_dead_letters_reads_back_and_re_drives_against_the_emulator()
    {
        RequireEmulator();
        var tenant = TenantId.New();
        var session = TransportEnvelope.SessionIdFor(tenant);
        await using var host = TransportHost.ServiceBus(fixture: null, EmulatorConnectionString, options =>
        {
            options.ConsumerSessionIds.Add(session);
            options.SessionIdleTimeout = TimeSpan.FromSeconds(2);
            options.MaxDeadLetterScan = 5000;
        });

        var succeeds = EventTransportConsumerTests.Envelope(tenant);
        var retried = EventTransportConsumerTests.Envelope(tenant);
        var rejected = EventTransportConsumerTests.Envelope(tenant);
        var fixedHandler = false;
        host.Handler.Respond = (envelope, attempt) =>
            envelope.OutboxMessageId == retried.OutboxMessageId && attempt == 1
                ? TransportHandlingResult.RetryLater(Error.Infrastructure("processing.store.unavailable", "Unavailable."))
                : envelope.OutboxMessageId == rejected.OutboxMessageId && !fixedHandler
                    ? TransportHandlingResult.Rejected(Error.Validation("processing.receipt.missing", "Missing."))
                    : TransportHandlingResult.Succeeded;

        try
        {
            foreach (var envelope in new[] { succeeds, retried, rejected })
            {
                Assert.True((await host.Publisher.PublishAsync(envelope, Token)).IsSuccess);
            }

            await host.ConsumerHost.StartAsync(Token);
            await WaitUntilAsync(() => host.Handler.AttemptsFor(succeeds.OutboxMessageId) >= 1
                && host.Handler.AttemptsFor(retried.OutboxMessageId) >= 2
                && host.Handler.AttemptsFor(rejected.OutboxMessageId) >= 1);
            await WaitUntilAsync(async () => (await DeadLettersAsync(host, rejected.OutboxMessageId)).Count == 1);

            // Completed once, retried once after an abandon, dead-lettered once; each seen in its tenant scope.
            Assert.Equal(1, host.Handler.AttemptsFor(succeeds.OutboxMessageId));
            Assert.Equal(2, host.Handler.AttemptsFor(retried.OutboxMessageId));
            Assert.Equal(1, host.Handler.AttemptsFor(rejected.OutboxMessageId));
            Assert.All(host.Handler.Handled, handled => Assert.Equal(tenant, handled.Tenant));
            Assert.Equal(
                [succeeds.OutboxMessageId, retried.OutboxMessageId, retried.OutboxMessageId, rejected.OutboxMessageId],
                host.Handler.Handled.Select(handled => handled.Envelope.OutboxMessageId));

            // The dead-letter queue reads back as safe metadata only.
            var dead = Assert.Single(await DeadLettersAsync(host, rejected.OutboxMessageId));
            Assert.Equal("processing.receipt.missing", dead.Reason);
            Assert.Equal(tenant, dead.Envelope!.TenantId);
            Assert.Equal(rejected.CorrelationId, dead.Envelope.CorrelationId);
            Assert.Equal(rejected.CausationId, dead.Envelope.CausationId);
            Assert.Equal(0, dead.Envelope.RedriveCount);

            // After the fix, an explicit re-drive returns it under a new message id with its identity intact.
            fixedHandler = true;
            var redrive = await host.ServiceBusDeadLetters.RedriveAsync(rejected.OutboxMessageId, Token);
            Assert.Equal(RedriveStatus.Redriven, redrive.Status);
            Assert.Equal($"{rejected.OutboxMessageId:D}:redrive:1", redrive.MessageId);

            await WaitUntilAsync(() => host.Handler.AttemptsFor(rejected.OutboxMessageId) >= 2);
            var redriven = host.Handler.Handled.Last(handled => handled.Envelope.OutboxMessageId == rejected.OutboxMessageId);
            Assert.Equal(1, redriven.Envelope.RedriveCount);
            Assert.Equal(rejected.CorrelationId, redriven.Envelope.CorrelationId);
            Assert.Equal(rejected.CausationId, redriven.Envelope.CausationId);
            Assert.Equal(tenant, redriven.Tenant);
            await WaitUntilAsync(async () => (await DeadLettersAsync(host, rejected.OutboxMessageId)).Count == 0);
        }
        finally
        {
            await host.ConsumerHost.StopAsync(CancellationToken.None);
            await RemoveDeadLettersAsync(session);
        }
    }

    [Fact]
    public async Task A_malformed_message_on_the_emulator_is_dead_lettered_and_never_reaches_the_handler()
    {
        RequireEmulator();
        var tenant = TenantId.New();
        var session = TransportEnvelope.SessionIdFor(tenant);
        await using var host = TransportHost.ServiceBus(fixture: null, EmulatorConnectionString, options =>
        {
            options.ConsumerSessionIds.Add(session);
            options.SessionIdleTimeout = TimeSpan.FromSeconds(2);
        });

        var messageId = Guid.CreateVersion7().ToString("D");
        await using var client = new ServiceBusClient(EmulatorConnectionString);
        try
        {
            await using (var sender = client.CreateSender(TransportContract.EventProcessingQueue))
            {
                // The right session, but no envelope at all: a forged or foreign message.
                await sender.SendMessageAsync(
                    new ServiceBusMessage(BinaryData.FromString("""{"note":"not an envelope"}"""))
                    {
                        MessageId = messageId,
                        SessionId = session,
                        ContentType = TransportContract.ContentType,
                    },
                    Token);
            }

            await host.ConsumerHost.StartAsync(Token);
            ServiceBusReceivedMessage? dead = null;
            await WaitUntilAsync(async () =>
            {
                dead = (await PeekDeadLettersAsync(client)).FirstOrDefault(message => message.MessageId == messageId);
                return dead is not null;
            });

            Assert.Equal(TransportErrors.ContractUnsupported.Code, dead!.DeadLetterReason);
            Assert.Equal(TransportErrors.ContractUnsupported.Message, dead.DeadLetterErrorDescription);
            Assert.Empty(host.Handler.Handled);

            var described = Assert.Single(
                await host.ServiceBusDeadLetters.PeekAsync(5000, Token),
                message => message.SequenceNumber == dead.SequenceNumber);
            Assert.Equal(TransportErrors.ContractUnsupported.Code, described.Reason);
            Assert.Null(described.Envelope);
        }
        finally
        {
            await host.ConsumerHost.StopAsync(CancellationToken.None);
            await RemoveDeadLettersAsync(session);
        }
    }

    [Fact]
    public async Task Duplicate_detection_on_the_emulator_absorbs_a_republished_message_id()
    {
        RequireEmulator();
        var tenant = TenantId.New();
        var session = TransportEnvelope.SessionIdFor(tenant);
        await using var host = TransportHost.ServiceBus(fixture: null, EmulatorConnectionString, options =>
        {
            options.ConsumerSessionIds.Add(session);
            options.SessionIdleTimeout = TimeSpan.FromSeconds(2);
        });

        var envelope = EventTransportConsumerTests.Envelope(tenant);
        var follower = EventTransportConsumerTests.Envelope(tenant);
        try
        {
            // The same message id twice, as a relay republishes after a crash, then one more message.
            Assert.True((await host.Publisher.PublishAsync(envelope, Token)).IsSuccess);
            Assert.True((await host.Publisher.PublishAsync(envelope, Token)).IsSuccess);
            Assert.True((await host.Publisher.PublishAsync(follower, Token)).IsSuccess);

            await host.ConsumerHost.StartAsync(Token);
            await WaitUntilAsync(() => host.Handler.AttemptsFor(follower.OutboxMessageId) >= 1);

            // The session delivers in order, so once the follower is handled nothing else is waiting ahead of it.
            Assert.Equal(1, host.Handler.AttemptsFor(envelope.OutboxMessageId));
        }
        finally
        {
            await host.ConsumerHost.StopAsync(CancellationToken.None);
            await RemoveDeadLettersAsync(session);
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

    private static ReceivedTransportMessage Received(ServiceBusReceivedMessage message) => new(
        message.MessageId,
        message.SessionId,
        message.CorrelationId,
        message.ContentType,
        message.ApplicationProperties,
        message.Body.ToMemory(),
        message.DeliveryCount,
        message.SequenceNumber);

    private async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The emulator did not reach the expected state in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Token);
        }
    }

    private async Task<IReadOnlyList<DeadLetteredMessage>> DeadLettersAsync(TransportHost host, Guid outboxMessageId) =>
        [.. (await host.ServiceBusDeadLetters.PeekAsync(5000, Token))
            .Where(message => message.Envelope?.OutboxMessageId == outboxMessageId)];

    private async Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekDeadLettersAsync(ServiceBusClient client)
    {
        await using var receiver = client.CreateReceiver(
            TransportContract.EventProcessingQueue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var messages = new List<ServiceBusReceivedMessage>();
        long? from = null;
        while (true)
        {
            var page = await receiver.PeekMessagesAsync(250, from, Token);
            if (page.Count == 0)
            {
                return messages;
            }

            messages.AddRange(page);
            from = page[^1].SequenceNumber + 1;
        }
    }

    // Removes only the dead-lettered messages of this test's own session; everything else is released untouched.
    private async Task RemoveDeadLettersAsync(string sessionId)
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

    // Relays every older pending outbox message into an in-memory broker, so only this test's own message
    // reaches the emulator.
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
}
