using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3D end to end against the real local PostgreSQL: a signed monday webhook enters through the real 3C endpoint,
/// and the real processor, with the monday normalizer, handles its durable receipt. A status change becomes a held
/// task-status change that 3E can reread and the Snapshot never sees; every other event is quarantined explicitly with
/// its receipt kept; arrival order decides nothing; and no provider text, token or secret reaches a log.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MondayNormalizationTests(DatabaseFixture fixture)
{
    private const string ChangedAt = "1633944017.473193";
    private const string TriggerTime = "2021-10-11T09:20:18.022Z";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_status_change_becomes_a_held_task_status_change_and_the_snapshot_is_untouched()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        await using var processing = MondayProcessing();
        var label = "Done " + IngressData.NewMarker();
        var trigger = WebhookData.NewTrigger();

        Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, StatusChange("1772099344", label, trigger))).StatusCode);
        var receipt = Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        var result = await processing.ProcessSettledAsync(await EnvelopeAsync(connection.Tenant, receipt.ReceiptId));

        // D3D-1: a webhook's values are hints; the object is held for the authoritative reread and nothing advances.
        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(ProcessingErrors.UnconfirmedSourceValues.Code, result.ReasonCode);
        Assert.False(result.TryGetProjection(out _));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, connection.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, connection.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, connection.Tenant));

        // What 3E needs to reread the item, from stable ids and the source's own evidence: no value, no text.
        var entry = Assert.Single(await HeldAsync(connection.Tenant));
        Assert.Equal("monday", entry.SourceSystem);
        Assert.Equal(receipt.ConnectionReference, entry.ConnectionReference);
        Assert.Equal(trigger, entry.SourceEventId);
        Assert.Equal((CanonicalEventSchemas.ObjectChangedType, CanonicalEventSchemas.ObjectChangedVersion1), (entry.EventType, entry.SchemaVersion));
        Assert.Equal(("monday", receipt.ConnectionReference, "item", "1772099344"), (entry.ObjectSystem, entry.ObjectConnection, entry.ObjectType, entry.ObjectId));
        Assert.Null(entry.SourceRevision);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks((1633944017L * TimeSpan.TicksPerSecond) + 4731930), entry.SourceUpdatedAtUtc);
        Assert.Equal(new DateTimeOffset(2021, 10, 11, 9, 20, 18, 22, TimeSpan.Zero), entry.OccurredAtUtc);
        Assert.Equal(("ReconciliationRequired", "processing.source.unconfirmed"), (entry.Outcome, entry.ReasonCode));
        Assert.Equal([("status", "TaskStatus", "Withheld")], await ProcessingHarness.LedgerFieldsAsync(fixture, entry.EventId));

        // The raw receipt stays the only copy of the body: the label hint was copied nowhere else.
        Assert.Equal("Processed", await ProcessingHarness.ReceiptStateAsync(fixture, receipt.ReceiptId));
        Assert.Equal(["raw_receipts.payload holds sentinel #1"], await WebhookData.FindAnywhereAsync(fixture, [label], Token));
    }

    [Fact]
    public async Task Every_unsupported_event_is_quarantined_explicitly_with_its_receipt_kept_and_nothing_projected()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        await using var processing = MondayProcessing();
        var marker = IngressData.NewMarker();

        string[] unsupported =
        [
            """{"event":{"userId":9603417,"originalTriggerUuid":null,"boardId":1771812698,"pulseId":1772099344,"pulseName":"MARKER","groupId":"topics","groupName":"Group Title","groupColor":"#579bfc","isTopGroup":true,"columnValues":{},"app":"monday","type":"create_pulse","triggerTime":"2021-10-11T09:07:28.210Z","subscriptionId":73759690,"triggerUuid":"TRIGGER"}}""",
            """{"event":{"userId":9603417,"originalTriggerUuid":null,"boardId":1771812698,"groupId":"topics","pulseId":1771812728,"pulseName":"MARKER","columnId":"date4","columnType":"date","columnTitle":"Date","value":{"date":"2021-10-11","icon":null,"time":null},"previousValue":null,"changedAt":1633943701.9457765,"isTopGroup":true,"app":"monday","type":"update_column_value","triggerTime":"2021-10-11T09:15:03.429Z","subscriptionId":73760484,"triggerUuid":"TRIGGER"}}""",
            """{"event":{"userId":9603417,"originalTriggerUuid":null,"boardId":1771812698,"pulseId":1771812728,"body":"<p>MARKER</p>","textBody":"MARKER","updateId":1190616585,"replyId":null,"app":"monday","type":"create_update","triggerTime":"2021-10-11T09:18:57.368Z","subscriptionId":73760983,"triggerUuid":"TRIGGER"}}""",
            StatusChange("1772139123", "MARKER", "TRIGGER").Replace("\"isTopGroup\":true", "\"isTopGroup\":true,\"parentItemId\":\"1771812716\"", StringComparison.Ordinal),
            StatusChange("1772099344", "MARKER" + new string('x', 1030), "TRIGGER"),
            StatusChange("1772099344", "MARKER", "TRIGGER").Replace("update_column_value", "archive_pulse", StringComparison.Ordinal),
        ];

        foreach (var template in unsupported)
        {
            var body = template.Replace("MARKER", marker, StringComparison.Ordinal).Replace("TRIGGER", WebhookData.NewTrigger(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, body)).StatusCode);
        }

        var receipts = await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token);
        Assert.Equal(unsupported.Length, receipts.Count);
        foreach (var receipt in receipts)
        {
            var result = await processing.ProcessSettledAsync(await EnvelopeAsync(connection.Tenant, receipt.ReceiptId));

            Assert.Equal(EventProcessingOutcome.Quarantined, result.Outcome);
            Assert.Equal(QuarantineReason.BadSchema, result.QuarantineReason);
            Assert.Equal(ProcessingErrors.NormalizerRejected.Code, result.ReasonCode);
            Assert.Equal("Quarantined", await ProcessingHarness.ReceiptStateAsync(fixture, receipt.ReceiptId));
        }

        // Not lost: every receipt is still there with its body, and the ledger names each one. Nothing was projected.
        var kept = await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token);
        Assert.All(kept, receipt => Assert.Contains(marker, receipt.Payload, StringComparison.Ordinal));
        Assert.Equal(unsupported.Length, (await ProcessingHarness.LedgerAsync(fixture, connection.Tenant)).Count);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, connection.Tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, connection.Tenant));
    }

    [Fact]
    public async Task Arrival_order_is_never_provider_order()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        await using var processing = MondayProcessing();
        var (earlierTrigger, laterTrigger) = (WebhookData.NewTrigger(), WebhookData.NewTrigger());

        // The later change arrives first and is processed first; the earlier arrives and is processed second.
        Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, StatusChange("1772099344", "Done", laterTrigger, "1633944100.5", "2021-10-11T09:21:40.500Z"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, StatusChange("1772099344", "Working", earlierTrigger, "1633944000.25", "2021-10-11T09:20:00.250Z"))).StatusCode);
        foreach (var receipt in await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token))
        {
            Assert.Equal(EventProcessingOutcome.ReconciliationRequired, (await processing.ProcessSettledAsync(await EnvelopeAsync(connection.Tenant, receipt.ReceiptId))).Outcome);
        }

        // Each event keeps exactly its own evidence, whatever the arrival; no order was established and nothing applied.
        var held = (await HeldAsync(connection.Tenant)).ToDictionary(entry => entry.SourceEventId!);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks((1633944100L * TimeSpan.TicksPerSecond) + 5000000), held[laterTrigger].SourceUpdatedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks((1633944000L * TimeSpan.TicksPerSecond) + 2500000), held[earlierTrigger].SourceUpdatedAtUtc);
        Assert.All(held.Values, entry => Assert.Null(entry.SourceRevision));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, connection.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, connection.Tenant));
    }

    [Fact]
    public async Task Impossible_timestamps_follow_the_existing_quarantine_path_instead_of_being_corrected()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        await using var processing = MondayProcessing();
        var future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, StatusChange("1772099344", "Done", WebhookData.NewTrigger(), future))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await webhooks.PostEventAsync(connection, StatusChange("1772099345", "Done", WebhookData.NewTrigger(), ChangedAt, "2021-10-12T09:20:18.022Z"))).StatusCode);

        foreach (var receipt in await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token))
        {
            var result = await processing.ProcessSettledAsync(await EnvelopeAsync(connection.Tenant, receipt.ReceiptId));
            Assert.Equal((EventProcessingOutcome.Quarantined, QuarantineReason.ImpossibleOrdering), (result.Outcome, result.QuarantineReason));
        }

        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, connection.Tenant));
    }

    [Fact]
    public async Task No_provider_text_token_or_secret_reaches_any_log_of_intake_or_processing()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        await using var processing = MondayProcessing();
        var marker = IngressData.NewMarker();
        var token = WebhookTestTokens.Mint(connection.Connection, connection.Account, webhooks.Clock.UtcNow);

        foreach (var body in new[]
                 {
                     StatusChange("1772099344", "Label " + marker, WebhookData.NewTrigger()),
                     StatusChange("1772099344", "Label " + marker + new string('y', 1030), WebhookData.NewTrigger()),
                     StatusChange("1772099344", "Label " + marker, WebhookData.NewTrigger()).Replace("\"columnType\":\"color\"", "\"columnType\":\"text\"", StringComparison.Ordinal),
                 })
        {
            Assert.Equal(HttpStatusCode.OK, (await webhooks.PostAsync(MondayWebhookHost.PathOf(connection.Connection), body, token)).StatusCode);
        }

        foreach (var receipt in await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token))
        {
            await processing.ProcessSettledAsync(await EnvelopeAsync(connection.Tenant, receipt.ReceiptId));
        }

        var entries = webhooks.Logs.Entries.Concat(processing.Logs.Entries).ToList();
        Assert.Contains(entries, entry => entry.Category.StartsWith("WorkContinuity.Infrastructure.Processing", StringComparison.Ordinal));
        Assert.All(entries, entry =>
        {
            foreach (var sentinel in new[] { marker, token, WebhookTestTokens.SigningSecret, "short-lived-token-marker", "Name marker", "pulseName" })
            {
                Assert.DoesNotContain(sentinel, entry.Text, StringComparison.Ordinal);
            }
        });
    }

    // A documented status change for one item, with its label, trigger and times chosen by the test.
    private static string StatusChange(string itemId, string label, string trigger, string changedAt = ChangedAt, string triggerTime = TriggerTime) =>
        "{\"event\":{\"userId\":9603417,\"originalTriggerUuid\":null,\"boardId\":1771812698,\"groupId\":\"topics\",\"pulseId\":" + itemId
        + ",\"pulseName\":\"Name marker\",\"columnId\":\"status\",\"columnType\":\"color\",\"columnTitle\":\"Status\",\"value\":{\"label\":{\"index\":3,\"text\":\""
        + label + "\",\"style\":{\"color\":\"#0086c0\"}},\"post_id\":null},\"previousValue\":{\"label\":{\"index\":1,\"text\":\"Working on it\"},\"post_id\":null},\"changedAt\":"
        + changedAt + ",\"isTopGroup\":true,\"app\":\"monday\",\"type\":\"update_column_value\",\"triggerTime\":\"" + triggerTime
        + "\",\"subscriptionId\":73761176,\"triggerUuid\":\"" + trigger + "\"}}";

    // The real pipeline, with the production monday normalizer as the only one.
    private TransportHost MondayProcessing() =>
        ProcessingHarness.Host(fixture, new InMemoryTransport(), services => services.AddSingleton<ISourceEventNormalizer, MondayWebhookNormalizer>());

    // The message the relay would publish for a receipt: its outbox marker, in the receipt's tenant.
    private async Task<TransportEnvelope> EnvelopeAsync(TenantId tenant, Guid receiptId)
    {
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, Token);
        var marker = Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token), candidate => candidate.OutboxMessageId == outboxId);
        Assert.True(CorrelationId.TryParse(marker.CorrelationId, out var correlation));
        var envelope = TransportEnvelope.FromOutbox(outboxId, tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptId), correlation);
        Assert.True(envelope.IsSuccess);
        return envelope.Value;
    }

    private async Task<IReadOnlyList<HeldEntry>> HeldAsync(TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, source_system, connection_reference, source_event_id, event_type, schema_version, object_system,
                   object_connection, object_type, object_id, source_revision, source_updated_at_utc, occurred_at_utc,
                   outcome, reason_code
            FROM event_ledger WHERE tenant_id = @tenant ORDER BY event_id;
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        var entries = new List<HeldEntry>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            entries.Add(new HeldEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return entries;
    }

    private sealed record HeldEntry(
        Guid EventId,
        string SourceSystem,
        string ConnectionReference,
        string? SourceEventId,
        string EventType,
        string SchemaVersion,
        string ObjectSystem,
        string ObjectConnection,
        string ObjectType,
        string ObjectId,
        long? SourceRevision,
        DateTimeOffset? SourceUpdatedAtUtc,
        DateTimeOffset? OccurredAtUtc,
        string Outcome,
        string? ReasonCode);
}
