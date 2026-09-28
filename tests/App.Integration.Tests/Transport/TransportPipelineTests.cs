using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>
/// Gate 2C end to end on the deterministic in-memory broker (owner decision 8), from ingress through the
/// relay and the transport to a stand-in downstream handler, against the real local PostgreSQL: tenant,
/// correlation and causation survive the whole path; the restored tenant drives row-level security, so a
/// forged tenant finds nothing and is dead-lettered instead of retried; a poison message is dead-lettered,
/// visible and re-drivable with its identity intact; and re-drive stops at its limit.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TransportPipelineTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delivery_travels_from_ingress_to_the_handler_with_its_tenant_correlation_and_causation()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var correlation = CorrelationId.New();

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-pipeline", IngressData.PayloadWith(IngressData.NewMarker()), correlation));
        var receiptId = Assert.NotNull(outcome.ReceiptId);
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, Token);

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);
        await transport.DrainAsync(host.Consumer);

        var handled = Assert.Single(host.Handler.Handled, item => item.Envelope.OutboxMessageId == outboxId);
        Assert.True(handled.TenantResolved);
        Assert.Equal(TenantResolutionSource.TrustedServiceIdentity, handled.Source);
        Assert.Equal(tenant, handled.Tenant);
        Assert.Equal(tenant, handled.Envelope.TenantId);
        Assert.Equal(correlation, handled.Envelope.CorrelationId);
        Assert.Equal(receiptId, handled.Envelope.CausationId);
        Assert.Equal(0, handled.Envelope.RedriveCount);
    }

    [Fact]
    public async Task The_restored_tenant_drives_row_level_security_so_a_forged_tenant_finds_nothing_and_is_dead_lettered()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(
            fixture,
            transport,
            handlers: services => services.AddScoped<ITransportMessageHandler, ReceiptReadingHandler>());
        var (_, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, _) = await IngressData.NewConnectionAsync(fixture, Token);

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connectionA, "evt-forged", IngressData.PayloadWith(IngressData.NewMarker())));
        var receiptA = Assert.NotNull(outcome.ReceiptId);
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptA, Token);
        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        // A well-formed message that claims tenant B for tenant A's receipt, as a forger with send rights could.
        var forged = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), tenantB, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptA), CorrelationId.New()).Value.ToWire();
        transport.SendRaw(forged.MessageId, forged.SessionId, forged.CorrelationId, forged.ContentType, forged.ApplicationProperties, forged.Body.ToArray());

        var deliveries = await transport.DrainAsync(host.Consumer);

        var genuine = Assert.Single(deliveries, delivery => delivery.Message.MessageId == outboxId.ToString("D"));
        Assert.Equal(TransportSettlementKind.Complete, genuine.Settlement.Kind);

        var refused = Assert.Single(deliveries, delivery => delivery.Message.MessageId == forged.MessageId);
        Assert.Equal(TransportSettlementKind.DeadLetter, refused.Settlement.Kind);
        Assert.Equal(ReceiptReadingHandler.NotVisible.Code, refused.Settlement.DeadLetterReason!.Code);
        Assert.Equal(1, refused.Message.DeliveryCount);
    }

    [Fact]
    public async Task A_poison_message_is_dead_lettered_visible_and_re_driven_after_the_fix_with_its_identity_intact()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var correlation = CorrelationId.New();
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-poison", IngressData.PayloadWith(IngressData.NewMarker()), correlation));
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, outcome.ReceiptId!.Value, Token);

        var broken = true;
        host.Handler.Respond = (envelope, _) => broken && envelope.OutboxMessageId == outboxId
            ? TransportHandlingResult.Rejected(Error.Validation("processing.schema.unknown", "Unknown schema."))
            : TransportHandlingResult.Succeeded;

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);
        await transport.DrainAsync(host.Consumer);

        // Visible as safe metadata only.
        var dead = Assert.Single(await transport.PeekAsync(1000, Token), message => message.Envelope?.OutboxMessageId == outboxId);
        Assert.Equal("processing.schema.unknown", dead.Reason);
        Assert.Equal(tenant, dead.Envelope!.TenantId);
        Assert.Equal(correlation, dead.Envelope.CorrelationId);
        Assert.Equal(outcome.ReceiptId, dead.Envelope.CausationId);
        Assert.Equal(0, dead.Envelope.RedriveCount);

        // Nothing brings it back on its own.
        await transport.DrainAsync(host.Consumer);
        Assert.Equal(1, host.Handler.AttemptsFor(outboxId));

        broken = false;
        var redrive = await transport.RedriveAsync(outboxId, Token);
        Assert.Equal(RedriveStatus.Redriven, redrive.Status);
        Assert.Equal($"{outboxId:D}:redrive:1", redrive.MessageId);

        await transport.DrainAsync(host.Consumer);

        var handled = host.Handler.Handled.Where(item => item.Envelope.OutboxMessageId == outboxId).ToList();
        Assert.Equal(2, handled.Count);
        Assert.Equal(1, handled[1].Envelope.RedriveCount);
        Assert.Equal(tenant, handled[1].Tenant);
        Assert.Equal(correlation, handled[1].Envelope.CorrelationId);
        Assert.Equal(outcome.ReceiptId, handled[1].Envelope.CausationId);
        Assert.DoesNotContain(await transport.PeekAsync(1000, Token), message => message.Envelope?.OutboxMessageId == outboxId);
    }

    [Fact]
    public async Task Retries_exhausted_reach_the_dead_letter_queue_and_re_drive_stops_at_its_limit()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-exhausted", IngressData.PayloadWith(IngressData.NewMarker())));
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, outcome.ReceiptId!.Value, Token);

        host.Handler.Respond = (envelope, _) => envelope.OutboxMessageId == outboxId
            ? TransportHandlingResult.RetryLater(Error.Infrastructure("processing.store.unavailable", "Unavailable."))
            : TransportHandlingResult.Succeeded;

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);
        await transport.DrainAsync(host.Consumer);

        for (var redrive = 1; redrive <= TransportContract.MaxRedriveCount; redrive++)
        {
            var dead = Assert.Single(await transport.PeekAsync(1000, Token), message => message.Envelope?.OutboxMessageId == outboxId);
            Assert.Equal(TransportErrors.RetriesExhausted.Code, dead.Reason);
            Assert.Equal(redrive - 1, dead.Envelope!.RedriveCount);

            Assert.Equal(RedriveStatus.Redriven, (await transport.RedriveAsync(outboxId, Token)).Status);
            await transport.DrainAsync(host.Consumer);
        }

        // Every attempt was bounded: 5 deliveries for the original and for each of the 3 re-drives.
        Assert.Equal(5 * (TransportContract.MaxRedriveCount + 1), host.Handler.AttemptsFor(outboxId));

        var refused = await transport.RedriveAsync(outboxId, Token);
        Assert.Equal(RedriveStatus.LimitReached, refused.Status);
        Assert.Null(refused.MessageId);
        var final = Assert.Single(await transport.PeekAsync(1000, Token), message => message.Envelope?.OutboxMessageId == outboxId);
        Assert.Equal(TransportContract.MaxRedriveCount, final.Envelope!.RedriveCount);

        Assert.Equal(RedriveStatus.NotFound, (await transport.RedriveAsync(Guid.CreateVersion7(), Token)).Status);
    }

    [Fact]
    public async Task Logs_and_dead_letters_along_the_whole_path_never_carry_the_payload()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-pipeline-logs", IngressData.PayloadWith(marker)));
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, outcome.ReceiptId!.Value, Token);

        host.Handler.Respond = (envelope, attempt) => envelope.OutboxMessageId != outboxId
            ? TransportHandlingResult.Succeeded
            : attempt switch
            {
                1 => throw new InvalidOperationException("handler saw " + marker),
                2 => TransportHandlingResult.RetryLater(Error.Infrastructure("processing.store.unavailable", "Unavailable " + marker)),
                3 => TransportHandlingResult.Rejected(Error.Validation("processing.rejected", "Rejected " + marker)),
                _ => TransportHandlingResult.Succeeded,
            };

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);
        await transport.DrainAsync(host.Consumer);
        await transport.RedriveAsync(outboxId, Token);
        await transport.DrainAsync(host.Consumer);

        Assert.Equal(4, host.Handler.AttemptsFor(outboxId));
        Assert.NotEmpty(host.Logs.Entries);
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(transport.DeadLetterDescriptions(), description => description.Contains(marker, StringComparison.Ordinal));
        Assert.All(transport.Accepted, wire => Assert.True(wire.Body.Length < 100, "A broker message is only the content-free marker."));
    }

    /// <summary>
    /// A stand-in for Gate 2D processing that reads the receipt a message names, through the tenant-scoped
    /// database of the message's own scope: under the restored tenant's row-level security.
    /// </summary>
    private sealed class ReceiptReadingHandler(ITenantScopedDatabase database) : ITransportMessageHandler
    {
        public static readonly Error NotVisible = Error.NotFound("processing.receipt.not_visible", "The receipt is not visible in the message's tenant.");

        public async Task<TransportHandlingResult> HandleAsync(TransportEnvelope envelope, CancellationToken cancellationToken)
        {
            var visible = await database.ExecuteAsync(
                (dbContext, token) => dbContext.RawReceipts.AsNoTracking().AnyAsync(receipt => receipt.ReceiptId == envelope.CausationId, token),
                cancellationToken);

            if (visible.IsFailure)
            {
                return TransportHandlingResult.RetryLater(visible.Error);
            }

            return visible.Value ? TransportHandlingResult.Succeeded : TransportHandlingResult.Rejected(NotVisible);
        }
    }
}
