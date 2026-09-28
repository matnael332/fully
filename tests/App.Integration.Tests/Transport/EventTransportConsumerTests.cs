using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>
/// Gate 2C transport-level consumer on the deterministic in-memory broker: only validated messages reach the
/// handler, each in the tenant scope restored from its validated envelope; transient failures are retried in
/// session order with bounded backoff and then dead-lettered; permanent failures are dead-lettered at once;
/// nothing is settled before the handler reports; and the logs carry ids and codes only.
/// </summary>
public sealed class EventTransportConsumerTests
{
    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_message_reaches_the_handler_in_its_restored_tenant_scope_and_is_completed()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var tenant = TenantId.New();
        var envelope = Envelope(tenant);
        await host.Publisher.PublishAsync(envelope, Token);

        var delivery = Assert.IsType<TransportDelivery>(await transport.DeliverNextAsync(host.Consumer, Token));

        Assert.Equal(TransportSettlementKind.Complete, delivery.Settlement.Kind);
        var handled = Assert.Single(host.Handler.Handled);
        Assert.True(handled.TenantResolved);
        Assert.Equal(TenantResolutionSource.TrustedServiceIdentity, handled.Source);
        Assert.Equal(tenant, handled.Tenant);
        Assert.Equal(envelope.OutboxMessageId, handled.Envelope.OutboxMessageId);
        Assert.Equal(envelope.CorrelationId, handled.Envelope.CorrelationId);
        Assert.Equal(envelope.CausationId, handled.Envelope.CausationId);
        Assert.Equal(envelope.MessageId, handled.Envelope.MessageId);

        // The restored tenant never reaches the caller, and the message is gone from the queue.
        Assert.False(host.TenantContextAccessor.Current.IsResolved);
        Assert.Equal(0, transport.ActiveCount);
    }

    [Fact]
    public async Task Malformed_or_unsupported_messages_are_dead_lettered_and_never_reach_the_handler()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);

        var cases = new List<(string Case, Error Expected)>();
        void Forge(string name, Error expected, Func<Dictionary<string, object>, (string? MessageId, string? SessionId, string? CorrelationId, byte[]? Body)> change)
        {
            var wire = Envelope(TenantId.New()).ToWire();
            var properties = new Dictionary<string, object>(wire.ApplicationProperties, StringComparer.Ordinal);
            var (messageId, sessionId, correlationId, body) = change(properties);
            transport.SendRaw(
                messageId ?? wire.MessageId,
                sessionId ?? wire.SessionId,
                correlationId ?? wire.CorrelationId,
                wire.ContentType,
                properties,
                body ?? wire.Body.ToArray());
            cases.Add((name, expected));
        }

        Forge("unknown contract", TransportErrors.ContractUnsupported, properties => { properties[TransportProperties.Contract] = "2"; return default; });
        Forge("unknown type", TransportErrors.MessageTypeUnsupported, properties => { properties[TransportProperties.MessageType] = "workcontinuity.other"; return default; });
        Forge("tenant as text", TransportErrors.TenantInvalid, properties => { properties[TransportProperties.Tenant] = Guid.NewGuid().ToString("D"); return default; });
        Forge("no identity", TransportErrors.IdentityInvalid, properties => { properties.Remove(TransportProperties.OutboxMessage); return default; });
        Forge("forged message id", TransportErrors.MessageIdMismatch, _ => (Guid.NewGuid().ToString("D"), null, null, null));
        Forge("tenant moved to another session", TransportErrors.SessionMismatch, properties => { properties[TransportProperties.Tenant] = Guid.NewGuid(); return default; });
        Forge("forged correlation", TransportErrors.CorrelationInvalid, _ => (null, null, "bad correlation", null));
        Forge("body with content", TransportErrors.BodyInvalid, properties =>
            (null, null, null, Encoding.UTF8.GetBytes($$"""{"receiptId":"{{(Guid)properties[TransportProperties.Causation]:D}}","note":"content"}""")));

        var deliveries = await transport.DrainAsync(host.Consumer);

        Assert.Equal(cases.Count, deliveries.Count);
        foreach (var (delivery, (name, expected)) in deliveries.Zip(cases))
        {
            Assert.True(TransportSettlementKind.DeadLetter == delivery.Settlement.Kind, name);
            Assert.True(expected.Code == delivery.Settlement.DeadLetterReason!.Code, $"{name}: {delivery.Settlement.DeadLetterReason.Code}");
        }

        Assert.Empty(host.Handler.Handled);

        var dead = await transport.PeekAsync(100, Token);
        Assert.Equal(cases.Select(item => item.Expected.Code), dead.Select(message => message.Reason));
        Assert.All(dead, message => Assert.Null(message.Envelope));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_in_session_order_with_backoff_and_dead_lettered_at_the_attempt_limit()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var tenant = TenantId.New();
        var failing = Envelope(tenant);
        var behind = Envelope(tenant);
        await host.Publisher.PublishAsync(failing, Token);
        await host.Publisher.PublishAsync(behind, Token);

        host.Handler.Respond = (envelope, _) => envelope.OutboxMessageId == failing.OutboxMessageId
            ? TransportHandlingResult.RetryLater(Error.Infrastructure("processing.store.unavailable", "The store is unavailable."))
            : TransportHandlingResult.Succeeded;

        var deliveries = await transport.DrainAsync(host.Consumer);

        // The failing message holds its session: the one behind it waits until it has left.
        var order = host.Handler.Handled.Select(item => item.Envelope.OutboxMessageId).ToList();
        Assert.Equal(Enumerable.Repeat(failing.OutboxMessageId, 5).Append(behind.OutboxMessageId), order);
        Assert.Equal(
            [TransportSettlementKind.Abandon, TransportSettlementKind.Abandon, TransportSettlementKind.Abandon, TransportSettlementKind.Abandon, TransportSettlementKind.DeadLetter, TransportSettlementKind.Complete],
            deliveries.Select(delivery => delivery.Settlement.Kind));
        Assert.Equal([1, 2, 3, 4, 5, 1], deliveries.Select(delivery => delivery.Message.DeliveryCount));

        // Growing, bounded, jittered backoff: attempt n waits within [half, all] of min(1 s x 2^(n-1), 30 s).
        var backoffs = transport.Backoffs;
        Assert.Equal(4, backoffs.Count);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var ceiling = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            Assert.InRange(backoffs[attempt - 1], ceiling / 2, ceiling);
        }

        var dead = Assert.Single(await transport.PeekAsync(10, Token));
        Assert.Equal(TransportErrors.RetriesExhausted.Code, dead.Reason);
        Assert.Equal(failing.OutboxMessageId, dead.Envelope!.OutboxMessageId);
        Assert.Equal(5, dead.DeliveryCount);
    }

    [Fact]
    public async Task A_handler_that_throws_is_retried_and_its_exception_message_is_never_logged()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var envelope = Envelope(TenantId.New());
        await host.Publisher.PublishAsync(envelope, Token);

        host.Handler.Respond = (_, attempt) => attempt == 1
            ? throw new InvalidOperationException("Failed on work item secret-marker for customer Acme")
            : TransportHandlingResult.Succeeded;

        var deliveries = await transport.DrainAsync(host.Consumer);

        Assert.Equal([TransportSettlementKind.Abandon, TransportSettlementKind.Complete], deliveries.Select(delivery => delivery.Settlement.Kind));
        Assert.Contains(host.Logs.Entries, entry => entry.Text.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains("secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_permanent_failure_is_dead_lettered_at_once_under_the_handlers_code_and_a_fixed_description()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var envelope = Envelope(TenantId.New());
        await host.Publisher.PublishAsync(envelope, Token);

        host.Handler.Respond = (_, _) =>
            TransportHandlingResult.Rejected(Error.Validation("processing.receipt.missing", "Receipt secret-marker is missing"));

        var delivery = Assert.Single(await transport.DrainAsync(host.Consumer));

        Assert.Equal(TransportSettlementKind.DeadLetter, delivery.Settlement.Kind);
        Assert.Equal(1, host.Handler.AttemptsFor(envelope.OutboxMessageId));
        var dead = Assert.Single(await transport.PeekAsync(10, Token));
        Assert.Equal("processing.receipt.missing", dead.Reason);
        Assert.Equal([TransportErrors.HandlerRejected.Message], transport.DeadLetterDescriptions());
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains("secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_consumer_refuses_to_run_inside_a_tenant_scope()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var message = Received(Envelope(TenantId.New()));

        await Task.Run(
            async () =>
            {
                host.TenantContextAccessor.SetResolved(
                    TenantContext.Resolved(TenantId.New(), TenantResolutionSource.TrustedServiceIdentity));
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Consumer.ConsumeAsync(message, Token));
            },
            Token);

        Assert.Empty(host.Handler.Handled);
    }

    [Fact]
    public async Task Messages_of_different_tenants_never_share_a_scope_even_when_consumed_concurrently()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var envelopes = Enumerable.Range(0, 16).Select(_ => Envelope(TenantId.New())).ToList();

        var settlements = await Task.WhenAll(envelopes.Select(envelope =>
            Task.Run(() => host.Consumer.ConsumeAsync(Received(envelope), Token), Token)));

        Assert.All(settlements, settlement => Assert.Equal(TransportSettlementKind.Complete, settlement.Kind));
        Assert.Equal(envelopes.Count, host.Handler.Handled.Count);
        Assert.All(host.Handler.Handled, handled => Assert.Equal(handled.Envelope.TenantId, handled.Tenant));
    }

    [Fact]
    public async Task A_duplicate_delivery_reaches_the_handler_again_with_the_same_identity()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var message = Received(Envelope(TenantId.New()));

        var first = await host.Consumer.ConsumeAsync(message, Token);
        var second = await host.Consumer.ConsumeAsync(message, Token);

        Assert.Equal(TransportSettlementKind.Complete, first.Kind);
        Assert.Equal(TransportSettlementKind.Complete, second.Kind);
        var handled = host.Handler.Handled;
        Assert.Equal(2, handled.Count);
        Assert.Equal(handled[0].Envelope.OutboxMessageId, handled[1].Envelope.OutboxMessageId);
        Assert.Equal(handled[0].Envelope.MessageId, handled[1].Envelope.MessageId);
    }

    [Fact]
    public async Task Consumer_logs_carry_ids_and_codes_but_never_forged_text_or_a_body()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport);
        var valid = Envelope(TenantId.New());
        await host.Publisher.PublishAsync(valid, Token);
        var forged = Envelope(TenantId.New()).ToWire();
        transport.SendRaw(
            "forged-marker-message",
            "forged-marker-session",
            "forged-marker-correlation",
            forged.ContentType,
            forged.ApplicationProperties,
            Encoding.UTF8.GetBytes("""{"note":"forged-marker-body"}"""));

        await transport.DrainAsync(host.Consumer);

        Assert.Contains(host.Logs.Entries, entry => entry.Text.Contains(valid.MessageId, StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, entry => entry.Level == LogLevel.Error
            && entry.Text.Contains(TransportErrors.MessageIdMismatch.Code, StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains("forged-marker", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains("receiptId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_while_handling_leaves_the_message_unsettled_for_redelivery()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(
            fixture: null,
            transport,
            handlers: services => services.AddSingleton<ITransportMessageHandler, WaitingHandler>());
        await host.Publisher.PublishAsync(Envelope(TenantId.New()), Token);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.DeliverNextAsync(host.Consumer, stop.Token));

        Assert.Equal(1, transport.ActiveCount);
        Assert.Empty(await transport.PeekAsync(10, Token));
    }

    [Fact]
    public async Task Without_a_downstream_handler_a_message_is_never_completed()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture: null, transport, handlers: _ => { });
        await host.Publisher.PublishAsync(Envelope(TenantId.New()), Token);

        var delivery = Assert.IsType<TransportDelivery>(await transport.DeliverNextAsync(host.Consumer, Token));

        Assert.Equal(TransportSettlementKind.Abandon, delivery.Settlement.Kind);
        Assert.Equal(1, transport.ActiveCount);
    }

    [Fact]
    public async Task The_service_bus_consumer_host_refuses_to_start_without_a_downstream_handler()
    {
        // No connection is opened: the host checks for a handler before it creates a processor.
        await using var host = TransportHost.ServiceBus(fixture: null, EmulatorConnectionString, handlers: _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConsumerHost.StartAsync(Token));
    }

    internal static TransportEnvelope Envelope(TenantId tenant)
    {
        var built = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(Guid.CreateVersion7()), CorrelationId.New());
        Assert.True(built.IsSuccess);
        return built.Value;
    }

    private static ReceivedTransportMessage Received(TransportEnvelope envelope)
    {
        var wire = envelope.ToWire();
        return new ReceivedTransportMessage(
            wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body, DeliveryCount: 1, SequenceNumber: 1);
    }

    private sealed class WaitingHandler : ITransportMessageHandler
    {
        public async Task<TransportHandlingResult> HandleAsync(TransportEnvelope envelope, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return TransportHandlingResult.Succeeded;
        }
    }
}
