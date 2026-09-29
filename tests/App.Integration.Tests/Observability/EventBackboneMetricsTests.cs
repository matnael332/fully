using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Events;
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
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Retention;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Observability;

/// <summary>
/// Gate 2F operational metrics, measured on the real pipeline: ingress, the outbox relay, transport settlement,
/// processing, Snapshot projection and retention each report what happened, by outcome and canonical reason, with
/// durations, lags and delivery attempts, and every tag comes from a bounded vocabulary that never holds an id or a
/// value.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventBackboneMetricsTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_pipeline_reports_every_stage_by_outcome_and_reason_with_bounded_tags_only()
    {
        var transport = new InMemoryTransport();
        await using var host = ProcessingHarness.Host(fixture, transport);
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        var synthetic = new SyntheticEvent { Revision = 1, Actor = marker };

        var accepted = await host.SubmitAsync(fixture, tenant, connection, synthetic, "evt-metrics-accepted");
        var quarantined = await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 2, Changes = [new("active_app", "PcActivity", null, marker)] }, "evt-metrics-quarantined");
        var reconcile = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "1002" }, "evt-metrics-reconcile");
        Assert.Equal(IngressStatus.Duplicate, (await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-metrics-accepted", synthetic.ToPayload()))).Status);
        Assert.Equal(
            IngressStatus.Rejected,
            (await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-metrics-forged", IngressData.PayloadWith(marker), proof: "not-the-proof"))).Status);

        // The relay publishes the markers, older pending work from other tests included, and measures each.
        await host.RelayUntilPublishedAsync(fixture, [accepted.OutboxId, quarantined.OutboxId, reconcile.OutboxId], Token);

        // The consumer hands this test's messages to processing as the broker would deliver them; a redelivery is a
        // duplicate, and a message that fails envelope validation is dead-lettered before any handler.
        foreach (var delivered in new[] { accepted, quarantined, reconcile })
        {
            Assert.Equal(TransportSettlementKind.Complete, (await host.Consumer.ConsumeAsync(ProcessingHarness.Received(delivered.Envelope), Token)).Kind);
        }

        Assert.Equal(
            TransportSettlementKind.Complete,
            (await host.Consumer.ConsumeAsync(ProcessingHarness.Received(accepted.Envelope, deliveryCount: 2), Token)).Kind);
        var invalid = ProcessingHarness.Received(accepted.Envelope) with { ContentType = "text/plain" };
        Assert.Equal(TransportSettlementKind.DeadLetter, (await host.Consumer.ConsumeAsync(invalid, Token)).Kind);

        // Ingress: accepted, repeated and rejected deliveries, a rejection by its canonical reason.
        Assert.Equal(3, recorder.Sum("workcontinuity.ingress.deliveries", ("outcome", "accepted"), ("reason", "none")));
        Assert.Equal(1, recorder.Sum("workcontinuity.ingress.deliveries", ("outcome", "duplicate")));
        Assert.Equal(
            1,
            recorder.Sum("workcontinuity.ingress.deliveries", ("outcome", "rejected"), ("reason", EventBackboneMetrics.Name(QuarantineReason.InvalidAuthentication))));

        // The relay: at least this test's three publications, each with its lag, and each pass by outcome and backlog.
        Assert.True(recorder.Sum("workcontinuity.outbox.messages", ("outcome", "published")) >= 3);
        Assert.True(recorder.Of("workcontinuity.outbox.relay_lag").Count >= 3);
        Assert.All(recorder.Of("workcontinuity.outbox.relay_lag"), lag => Assert.True(lag.Value >= 0));
        Assert.NotEmpty(recorder.Of("workcontinuity.outbox.relay_passes"));
        Assert.All(recorder.Of("workcontinuity.outbox.relay_passes"), pass =>
        {
            Assert.Contains(pass.Tags["outcome"], new[] { "completed", "failed", "discovery_failed" });
            Assert.Contains(pass.Tags["backlog"], new[] { "true", "false" });
        });
        Assert.NotEmpty(recorder.Of("workcontinuity.outbox.pass_tenants"));

        // Transport: four completions and one dead-letter by its catalogued reason, with every delivery attempt.
        Assert.Equal(4, recorder.Sum("workcontinuity.transport.settlements", ("settlement", "complete"), ("reason_code", "none")));
        Assert.Equal(
            1,
            recorder.Sum("workcontinuity.transport.settlements", ("settlement", "dead_letter"), ("reason_code", TransportErrors.ContentTypeInvalid.Code)));
        Assert.Equal([1d, 1d, 1d, 2d, 1d], recorder.Of("workcontinuity.transport.delivery_count").Select(measurement => measurement.Value));

        // Processing: each outcome by its canonical reason and reason code, timed, and lagged unless it was a duplicate.
        Assert.Equal(
            1,
            recorder.Sum("workcontinuity.processing.events", ("outcome", "accepted_for_projection"), ("reason", "none"), ("reason_code", "none")));
        Assert.Equal(
            1,
            recorder.Sum(
                "workcontinuity.processing.events",
                ("outcome", "quarantined"),
                ("reason", EventBackboneMetrics.Name(QuarantineReason.AuthorityConflict)),
                ("reason_code", ProcessingErrors.AuthorityConflict.Code)));
        Assert.Equal(
            1,
            recorder.Sum(
                "workcontinuity.processing.events",
                ("outcome", "reconciliation_required"),
                ("reason_code", ProcessingErrors.NoOrderingEvidence.Code)));
        Assert.Equal(1, recorder.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
        Assert.Equal(4, recorder.Of("workcontinuity.processing.duration").Count);
        Assert.Equal(3, recorder.Of("workcontinuity.processing.lag").Count);
        Assert.DoesNotContain(recorder.Of("workcontinuity.processing.lag"), lag => lag.Has("outcome", "duplicate"));

        // The Snapshot: the accepted event and its one field, timed, with the lag from receipt to fact.
        Assert.Equal(1, recorder.Sum("workcontinuity.snapshot.projected_events"));
        Assert.Equal(1, recorder.Sum("workcontinuity.snapshot.projected_fields"));
        Assert.Single(recorder.Of("workcontinuity.snapshot.projection_duration"));
        Assert.Single(recorder.Of("workcontinuity.snapshot.lag"));
        Assert.Empty(recorder.Of("workcontinuity.snapshot.projection_failures"));

        // No tag anywhere holds an id, a correlation or content.
        recorder.AssertBoundedTags(
            tenant.ToString(),
            connection.ToString("D"),
            accepted.ReceiptId.ToString("D"),
            accepted.OutboxId.ToString("D"),
            accepted.Correlation.Value,
            marker,
            "1001");
    }

    [Fact]
    public async Task A_projection_that_fails_is_counted_once_and_its_event_is_retried_not_counted_as_projected()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-metrics-guard-1")).Envelope);
        var elsewhere = await host.ProcessSettledAsync(
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "8008", Revision = 1 }, "evt-metrics-guard-other")).Envelope);

        // The Snapshot and the ordering state disagree, so the projection of the next accepted event refuses.
        await IngressData.ExecuteAdministrativeAsync(
            fixture,
            $"UPDATE snapshot_fields SET last_event_id = '{elsewhere.EventId.Value}' WHERE tenant_id = '{tenant.Value}' AND object_id = '1001';",
            Token);
        var newer = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 2 }, "evt-metrics-guard-2");

        Assert.Equal(ProcessingErrors.Unavailable, (await host.ProcessAsync(newer.Envelope, Token)).Error);

        Assert.Equal(1, recorder.Sum("workcontinuity.snapshot.projection_failures"));
        Assert.Equal(2, recorder.Sum("workcontinuity.snapshot.projected_events"));
        Assert.Equal(1, recorder.Sum("workcontinuity.processing.events", ("outcome", "unavailable"), ("reason_code", ProcessingErrors.Unavailable.Code)));
        Assert.Equal(2, recorder.Sum("workcontinuity.processing.events", ("outcome", "accepted_for_projection")));
        recorder.AssertBoundedTags(tenant.ToString(), newer.ReceiptId.ToString("D"));
    }

    [Fact]
    public async Task A_projection_counts_only_once_its_transaction_committed()
    {
        var failing = new FailingCommit();
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport(), services =>
        {
            services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(failing));
        });
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-metrics-commit");

        failing.Arm();
        Assert.Equal(ProcessingErrors.Unavailable, (await host.ProcessAsync(delivered.Envelope, Token)).Error);

        // The projection ran inside the transaction, but nothing it wrote committed, so nothing was projected.
        Assert.True(failing.Failed);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Empty(recorder.Of("workcontinuity.snapshot.projected_events"));
        Assert.Empty(recorder.Of("workcontinuity.snapshot.projection_failures"));
        Assert.Equal(1, recorder.Sum("workcontinuity.processing.events", ("outcome", "unavailable")));

        // The redelivery commits, and only then is it measured.
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, (await host.ProcessSettledAsync(delivered.Envelope)).Outcome);
        Assert.Equal(1, recorder.Sum("workcontinuity.snapshot.projected_events"));
        Assert.Equal(1, recorder.Sum("workcontinuity.processing.events", ("outcome", "accepted_for_projection")));
    }

    [Fact]
    public async Task Refused_processing_is_counted_by_its_reason_code_and_never_as_an_outcome()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (otherTenant, _) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAgedAsync(
            fixture, clock, RawContentRetention.PurgeAge + TimeSpan.FromHours(1), tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-metrics-refused");
        Assert.Equal(1, (await host.PurgeAsync(clock)).ReceiptsPurged);

        Assert.Equal(ProcessingErrors.ReceiptContentExpired, (await host.ProcessAsync(delivered.Envelope, Token)).Error);
        var forged = TransportEnvelope.FromOutbox(
            delivered.OutboxId, otherTenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(delivered.ReceiptId), delivered.Correlation).Value;
        Assert.Equal(ProcessingErrors.ReceiptNotVisible, (await host.ProcessAsync(forged, Token)).Error);

        Assert.Equal(
            1,
            recorder.Sum("workcontinuity.processing.events", ("outcome", "refused"), ("reason_code", ProcessingErrors.ReceiptContentExpired.Code)));
        Assert.Equal(
            1,
            recorder.Sum("workcontinuity.processing.events", ("outcome", "refused"), ("reason_code", ProcessingErrors.ReceiptNotVisible.Code)));
        Assert.Equal(2, recorder.Of("workcontinuity.processing.duration").Count(measurement => measurement.Has("outcome", "refused")));
        Assert.Empty(recorder.Of("workcontinuity.processing.lag"));
        recorder.AssertBoundedTags(tenant.ToString(), otherTenant.ToString(), delivered.ReceiptId.ToString("D"));
    }

    [Fact]
    public async Task Retention_runs_report_their_outcome_the_receipts_purged_by_state_the_duration_and_the_ages()
    {
        // Ages are the database's, stamped before its own now, so a Worker clock a month behind changes none of them.
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var workerBehind = DateTimeOffset.UtcNow.AddDays(-30);
        var due = RawContentRetention.PurgeAge + TimeSpan.FromMinutes(10);
        await using (var cleaner = RetentionHarness.Host(fixture, new InMemoryTransport(), clock))
        {
            await cleaner.ClearDueAsync(clock);
        }

        await using var host = RetentionHarness.Host(
            fixture, new InMemoryTransport(), clock, purge: options =>
            {
                options.BatchSize = 1;
                options.MaxBatchesPerTenant = 2;
            });
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var processed = await host.SubmitAgedAsync(fixture, clock, due, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-metrics-kept");
        var quarantined = await host.SubmitAgedAsync(
            fixture, clock, due - TimeSpan.FromMinutes(1), tenant, connection,
            new SyntheticEvent { Revision = 2, Changes = [new("active_app", "PcActivity", null, "editor")] }, "evt-metrics-quarantine");
        await host.SubmitAgedAsync(fixture, clock, due - TimeSpan.FromMinutes(2), tenant, connection, new SyntheticEvent { Revision = 3 }, "evt-metrics-unprocessed");
        await host.ProcessSettledAsync(processed.Envelope);
        await host.ProcessSettledAsync(quarantined.Envelope);

        var partial = await host.PurgeAsync(clock, workerBehind);
        var completed = await host.PurgeAsync(clock, workerBehind);
        using var failedMetrics = new EventBackboneMetrics();
        using var failedRecorder = new MetricsRecorder(failedMetrics);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var failed = await RawContentPurgeEntryPoint.RunOnceAsync(TransportHost.UnusedDatabase, loggerFactory, failedMetrics, cancellationToken: Token);

        Assert.Equal(RawContentPurgeOutcome.Partial, partial.Outcome);
        Assert.Equal(RawContentPurgeOutcome.Completed, completed.Outcome);
        Assert.Equal(RawContentPurgeOutcome.Failed, failed.Outcome);

        Assert.Equal(1, recorder.Sum("workcontinuity.retention.runs", ("outcome", "partial")));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.runs", ("outcome", "completed")));
        Assert.Equal(1, failedRecorder.Sum("workcontinuity.retention.runs", ("outcome", "failed")));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.purged", ("state", "processed")));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.purged", ("state", "quarantined")));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.purged", ("state", "unprocessed")));
        Assert.Equal(2, recorder.Of("workcontinuity.retention.duration").Count);
        var purgedAges = recorder.Of("workcontinuity.retention.oldest_purged_age");
        Assert.Equal(2, purgedAges.Count);
        RetentionHarness.AssertAge(due, TimeSpan.FromSeconds(purgedAges[0].Value));
        RetentionHarness.AssertAge(due - TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(purgedAges[1].Value));
        var remainingAge = Assert.Single(recorder.Of("workcontinuity.retention.oldest_remaining_age"));
        RetentionHarness.AssertAge(due - TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(remainingAge.Value));

        // Purged on time, nothing was late.
        Assert.Empty(recorder.Of("workcontinuity.retention.overdue"));
        recorder.AssertBoundedTags(tenant.ToString());
        failedRecorder.AssertBoundedTags();
    }

    /// <summary>Fails the next commit once armed, as a lost connection at the commit would.</summary>
    private sealed class FailingCommit : DbTransactionInterceptor
    {
        private int _armed;

        public bool Failed { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Failed = true;
                throw new InvalidOperationException("The commit failed.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
