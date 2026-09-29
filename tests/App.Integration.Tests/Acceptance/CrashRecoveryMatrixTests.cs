using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Retention;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Observability;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Retention;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Acceptance;

/// <summary>
/// The Gate 2 crash and recovery matrix on the whole path: every material durable boundary is interrupted, before and
/// after its commit where both exist, and the pipeline then recovers from the database and the broker alone. Each row
/// must converge on the fault-free ledger and Snapshot, or fail visibly, unacknowledged to the source or dead-lettered
/// with its reason, and never lose or double an effect.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrashRecoveryMatrixTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ingress_failing_at_its_commit_acknowledges_nothing_keeps_neither_receipt_nor_marker_and_the_retry_converges()
    {
        var faults = new CommitFaults();
        await using var path = await WholePath.StartAsync(fixture, source: faults.Register);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-ingress-commit", "1001", 1, "done");
        faults.FailIngress(stated.Id, times: 1);

        var failed = await path.SubmitAsync(connection, stated);

        // The receipt and its outbox marker had both been written when the commit failed, and neither survived it.
        Assert.Equal((IngressStatus.Failed, false), (failed.Status, failed.IsAcknowledged));
        Assert.Equal(2L, faults.RowsWrittenAtLastFailure);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, tenant, Token));
        Assert.Empty(await IngressData.MarkersAsync(fixture, tenant, Token));

        // The source retries what was never acknowledged: it is stored once and converges.
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        await path.FlushAsync();
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_lost_ingress_acknowledgement_is_answered_as_a_duplicate_and_the_event_is_processed_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-lost-ack", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        // The acknowledgement never reached the source, so it delivers the same event again.
        var again = await path.SubmitAsync(connection, stated);

        Assert.Equal((IngressStatus.Duplicate, (Guid?)receipt), (again.Status, again.ReceiptId));
        await path.FlushAsync();
        Assert.Single(path.Broker.Accepted);
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_relay_crash_before_the_send_leaves_the_marker_pending_and_the_event_is_processed_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-relay-before-send", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        Assert.True(await path.RelayUntilCrashAsync(envelope => envelope.CausationId == receipt, SendFault.CrashBeforeSend));

        Assert.Empty(path.Broker.Sends);
        Assert.Null(Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token)).PublishedAtUtc);
        await path.FlushAsync();
        Assert.Single(path.Broker.Sends);
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_broker_refusing_a_send_leaves_the_marker_pending_and_the_event_is_processed_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-send-refused", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        path.Broker.Fault = _ => SendFault.Refuse;
        var refused = await path.Source.Relay.RelayPendingAsync(Token);
        path.Broker.Fault = _ => SendFault.None;

        Assert.Equal((0, 1), (refused.Published, refused.PublishFailures));
        Assert.Empty(path.Broker.Accepted);
        Assert.Null(Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token)).PublishedAtUtc);
        await path.FlushAsync();
        Assert.Single(path.Broker.Accepted);
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_relay_crash_after_the_send_inside_the_duplicate_window_is_absorbed_by_the_broker()
    {
        await using var path = await WholePath.StartAsync(fixture);
        using var metrics = new MetricsRecorder(path.Processing.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-relay-inside-window", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        Assert.True(await path.RelayUntilCrashAsync(envelope => envelope.CausationId == receipt, SendFault.CrashAfterSend));
        Assert.Null(Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token)).PublishedAtUtc);
        await path.FlushAsync();

        Assert.Equal(2, path.Broker.Sends.Count);
        Assert.Single(path.Broker.Accepted);
        Assert.Equal(1, path.Broker.DuplicatesSuppressed);
        Assert.Equal(0, metrics.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_relay_crash_after_the_send_outside_the_duplicate_window_delivers_twice_and_processing_absorbs_the_copy()
    {
        await using var path = await WholePath.StartAsync(fixture);
        using var metrics = new MetricsRecorder(path.Processing.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-relay-outside-window", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        Assert.True(await path.RelayUntilCrashAsync(envelope => envelope.CausationId == receipt, SendFault.CrashAfterSend));
        path.Broker.Now += TimeSpan.FromMinutes(10);
        var deliveries = await path.FlushAsync();

        // The same identity twice on the broker: safe by contract, and a no-op the second time.
        Assert.Equal(2, path.Broker.Accepted.Count);
        Assert.Equal([TransportSettlementKind.Complete, TransportSettlementKind.Complete], deliveries.Select(delivery => delivery.Settlement.Kind));
        Assert.Equal(1, metrics.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_relay_commit_lost_after_the_send_republishes_the_same_message_id_and_the_event_is_processed_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-relay-commit-lost", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);

        // The relay's database connection dies between the send and the commit of its mark.
        var killed = false;
        path.Broker.AfterSend = async envelope =>
        {
            if (envelope.CausationId == receipt && !killed)
            {
                killed = true;
                await TerminateIdleRuntimeTransactionsAsync();
            }
        };
        var lost = await path.Source.Relay.RelayPendingAsync(Token);
        path.Broker.AfterSend = null;

        Assert.True(killed);
        Assert.Equal(1, lost.CommitFailures);
        Assert.Null(Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token)).PublishedAtUtc);
        await path.FlushAsync();
        Assert.Equal(2, path.Broker.Sends.Count);
        Assert.Single(path.Broker.Accepted);
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_duplicate_broker_delivery_before_processing_is_a_no_op()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-broker-duplicate", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        await path.RelayAsync();

        var wire = Assert.Single(path.Broker.Accepted);
        path.Broker.SendRaw(wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body.ToArray());
        var deliveries = await path.DrainAsync();

        Assert.Equal([TransportSettlementKind.Complete, TransportSettlementKind.Complete], deliveries.Select(delivery => delivery.Settlement.Kind));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
        Assert.Contains(path.Processing.Logs.Entries, entry => entry.Text.Contains("already processed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_consumer_lost_before_the_processing_commit_leaves_nothing_and_a_restarted_consumer_processes_the_event_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-lost-before-commit", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        await path.RelayAsync();

        // The Worker dies mid-processing, after every decision and before the commit: its lock is lost, unsettled.
        using (var crash = new CancellationTokenSource())
        {
            await using var dying = path.NewProcessingHost(normalizer: new CrashingNormalizer(crash));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => path.Broker.DeliverNextAsync(dying.Consumer, crash.Token));
        }

        Assert.Equal(1, path.Broker.ActiveCount);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, receipt));

        // A restarted Worker receives it again and processes it exactly once.
        var redelivered = Assert.IsType<TransportDelivery>(await path.DeliverAsync());
        Assert.Equal((2, TransportSettlementKind.Complete), (redelivered.Message.DeliveryCount, redelivered.Settlement.Kind));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_settlement_lost_after_the_processing_commit_redelivers_as_a_no_op_that_changes_no_version()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-lost-after-commit", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        await path.RelayAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => path.DeliverAsync(new SettlementLost(path.Processing.Consumer)));
        Assert.Equal(1, path.Broker.ActiveCount);
        var objects = await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Single(facts);

        var redelivered = Assert.IsType<TransportDelivery>(await path.DeliverAsync());

        Assert.Equal((2, TransportSettlementKind.Complete), (redelivered.Message.DeliveryCount, redelivered.Settlement.Kind));
        Assert.Equal(objects, await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Equal(facts, await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task Settlements_lost_up_to_the_broker_delivery_limit_dead_letter_a_processed_message_visibly_and_its_re_drive_is_a_no_op()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Event("evt-broker-limit", "1001", 1, "done");
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        await path.RelayAsync();

        // The consumer handles the message every time and loses every settlement, as repeated lock losses would.
        var dying = new SettlementLost(path.Processing.Consumer);
        for (var delivery = 1; delivery <= path.Broker.MaxDeliveryCount; delivery++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => path.DeliverAsync(dying));
        }

        // The broker's own limit dead-lettered it, visibly and with the broker's reason, although the first delivery
        // had already processed its receipt.
        var dead = Assert.Single(await path.DeadLettersAsync(tenant));
        Assert.Equal(("MaxDeliveryCountExceeded", receipt), (dead.Reason, dead.Envelope!.CausationId));
        Assert.Equal("AcceptedForProjection", Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant)).Outcome);
        var objects = await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);

        // Its re-drive is recognized as done and completes without a second effect.
        Assert.Equal(RedriveStatus.Redriven, (await path.Broker.RedriveAsync(dead.Envelope.OutboxMessageId, Token)).Status);
        var redriven = Assert.Single(await path.DrainAsync());

        Assert.Equal(TransportSettlementKind.Complete, redriven.Settlement.Kind);
        Assert.Equal(objects, await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Equal(facts, await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_processing_commit_failing_after_the_snapshot_was_written_keeps_nothing_and_the_redelivery_projects_once()
    {
        var faults = new CommitFaults();
        await using var path = await WholePath.StartAsync(fixture, processing: faults.Register);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = new SourceEvent("evt-commit-after-snapshot", new SyntheticEvent
        {
            ObjectId = "1001",
            Revision = 1,
            Changes = [new("status", "TaskStatus", null, "working"), new("due_date", "Deadline", null, "2026-10-01")],
        });
        var receipt = await path.SubmitAcceptedAsync(connection, stated);
        faults.FailProcessing(receipt, times: 1);
        await path.RelayAsync();

        var failed = Assert.IsType<TransportDelivery>(await path.DeliverAsync());

        // The object and both facts had been written inside the transaction when its commit failed; none of it, and
        // none of the decision it belonged to, survived, and the message waits to be retried.
        Assert.Equal(TransportSettlementKind.Abandon, failed.Settlement.Kind);
        Assert.Equal(3L, faults.RowsWrittenAtLastFailure);
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, receipt));

        var retried = Assert.IsType<TransportDelivery>(await path.DeliverAsync());

        Assert.Equal((2, TransportSettlementKind.Complete), (retried.Message.DeliveryCount, retried.Settlement.Kind));
        Assert.All(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant), fact => Assert.Equal((1L, 1L), (fact.FieldVersion, fact.ObjectVersion)));
        await AssertConvergedAsync(path, tenant, [stated], [receipt]);
    }

    [Fact]
    public async Task A_restart_resumes_from_the_database_and_the_broker_alone_and_converges()
    {
        var working = Event("evt-restart-1", "1001", 1, "working");
        var review = Event("evt-restart-2", "1001", 2, "review");
        var other = Event("evt-restart-3", "1002", 1, "done");
        var done = Event("evt-restart-4", "1001", 3, "done");
        InMemoryTransport broker;
        TenantId tenant;
        var receipts = new List<Guid>();

        await using (var before = await WholePath.StartAsync(fixture))
        {
            broker = before.Broker;
            (tenant, var connection) = await IngressData.NewConnectionAsync(fixture, Token);

            // The first event settles; the second is processed, but the process dies before it is settled; the third
            // waits on the broker; the fourth waits, committed, in the outbox.
            receipts.Add(await before.SubmitAcceptedAsync(connection, working));
            receipts.Add(await before.SubmitAcceptedAsync(connection, review));
            await before.RelayAsync();
            Assert.Equal(TransportSettlementKind.Complete, Assert.IsType<TransportDelivery>(await before.DeliverAsync()).Settlement.Kind);
            await Assert.ThrowsAsync<InvalidOperationException>(() => before.DeliverAsync(new SettlementLost(before.Processing.Consumer)));
            receipts.Add(await before.SubmitAcceptedAsync(connection, other));
            await before.RelayAsync();
            receipts.Add(await before.SubmitAcceptedAsync(connection, done));
        }

        // Every host is gone. New ones start with nothing but the database and the broker.
        await using var after = await WholePath.StartAsync(fixture, broker: broker);
        using var metrics = new MetricsRecorder(after.Processing.Services.GetRequiredService<EventBackboneMetrics>());
        var deliveries = await after.FlushAsync();

        Assert.Equal(3, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal(TransportSettlementKind.Complete, delivery.Settlement.Kind));
        Assert.Equal(1, metrics.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
        await AssertConvergedAsync(after, tenant, [working, review, other, done], receipts);
    }

    [Fact]
    public async Task Content_purged_before_processing_is_refused_and_dead_lettered_visibly_while_another_tenant_is_untouched()
    {
        await using var path = await WholePath.StartAsync(fixture, processing: services => services.AddRawContentRetention());
        await ClearDueContentAsync(path);
        var (expiredTenant, expiredConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (freshTenant, freshConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var expired = Event("evt-purged-unprocessed", "1001", 1, "done");
        var fresh = Event("evt-purged-unprocessed", "1001", 1, "done");
        var expiredReceipt = await path.SubmitAcceptedAsync(expiredConnection, expired);
        var freshReceipt = await path.SubmitAcceptedAsync(freshConnection, fresh);

        // The first receipt is 28 days and an hour old by the database's clock before anything processed it; the
        // second, another tenant's with the same ids, is new.
        await RetentionHarness.AgeReceiptAsync(fixture, expiredReceipt, TimeSpan.FromDays(28) + TimeSpan.FromHours(1));
        var run = await PurgeAsync(path);
        Assert.Equal((0, 0, 1), (run.ProcessedPurged, run.QuarantinedPurged, run.UnprocessedPurged));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, expiredTenant, expiredReceipt));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, freshTenant, freshReceipt));

        await path.FlushAsync();

        // Refused and dead-lettered with its reason, never guessed or discarded: its receipt stays, intact and unsettled.
        var dead = Assert.Single(await path.DeadLettersAsync(expiredTenant));
        Assert.Equal((ProcessingErrors.ReceiptContentExpired.Code, expiredReceipt), (dead.Reason, dead.Envelope!.CausationId));
        await path.AssertNothingSilentlyLostAsync(expiredTenant, [expiredReceipt]);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, expiredTenant));
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, expiredTenant));

        // A re-drive cannot bring the content back: it is refused again and stays visible with its reason.
        Assert.Equal(RedriveStatus.Redriven, (await path.Broker.RedriveAsync(dead.Envelope.OutboxMessageId, Token)).Status);
        await path.DrainAsync();
        var again = Assert.Single(await path.DeadLettersAsync(expiredTenant));
        Assert.Equal((ProcessingErrors.ReceiptContentExpired.Code, 1), (again.Reason, again.Envelope!.RedriveCount));

        // The other tenant's delivery was processed as usual.
        await AssertConvergedAsync(path, freshTenant, [fresh], [freshReceipt]);
    }

    private static SourceEvent Event(string id, string objectId, long revision, string status) =>
        new(id, new SyntheticEvent
        {
            ObjectId = objectId,
            Revision = revision,
            UpdatedAt = T0.AddMinutes(revision),
            Changes = [new("status", "TaskStatus", null, status)],
        });

    // The fault-free outcome: the ground truth with its provenance and versions, one ledger entry per receipt, and
    // nothing dead-lettered, pending or in flight.
    private static async Task AssertConvergedAsync(WholePath path, TenantId tenant, IReadOnlyList<SourceEvent> events, IReadOnlyCollection<Guid> receipts)
    {
        await path.AssertFactsAsync(tenant, GroundTruth.Of(events));
        await path.AssertProvenanceAsync(tenant);
        await path.AssertVersionsAsync(tenant);
        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, receipts);
        Assert.Empty(accounting.DeadLetters);
        Assert.Equal(receipts.Distinct().Count(), accounting.Ledger.Count);
    }

    private static Task<RawContentPurgeRun> PurgeAsync(WholePath path) =>
        Task.Run(
            () => path.Processing.Services.GetRequiredService<IRawContentPurge>().PurgeExpiredAsync(TestContext.Current.CancellationToken),
            CancellationToken.None);

    // Earlier tests may have left due content; purge it first, so this test's run counts only its own receipts.
    private static async Task ClearDueContentAsync(WholePath path)
    {
        for (var run = 0; run < 50; run++)
        {
            var cleared = await PurgeAsync(path);
            Assert.NotEqual(RawContentPurgeOutcome.Failed, cleared.Outcome);
            if (cleared.Outcome == RawContentPurgeOutcome.Completed)
            {
                return;
            }
        }

        Assert.Fail("Due content left by earlier tests was not cleared within 50 runs.");
    }

    private async Task TerminateIdleRuntimeTransactionsAsync()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(pg_terminate_backend(pid))
            FROM pg_stat_activity
            WHERE usename = @role AND state = 'idle in transaction' AND pid <> pg_backend_pid();
            """,
            connection);
        command.Parameters.AddWithValue("role", PersistenceOptions.RuntimeRoleName);
        var terminated = (long)(await command.ExecuteScalarAsync(Token))!;
        Assert.True(terminated >= 1, "The relay's transaction was not found.");
    }
}
