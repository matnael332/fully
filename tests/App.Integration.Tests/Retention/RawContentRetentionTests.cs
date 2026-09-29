using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Retention;

/// <summary>
/// Gate 2F raw-content retention against the real local PostgreSQL, through the runtime role. The database's clock
/// alone decides: it stamps every receipt time, content is due once the database shows it 28 days old and must never
/// be older than the 30-day hard limit, whatever the ingress host's or the purge Worker's clock says and whatever time
/// the provider states; every age a run reports, and so every overdue or limit-exceeded finding, its metrics and its
/// logs, is the database's. Lateness is reported
/// whenever it is seen, while only overdue content left behind makes a run exit with 3. Purging clears a receipt's
/// payload and fingerprint and keeps the row, the ledger, the ordering state, the Snapshot and the outbox marker
/// whole; ingress and processing idempotency hold after the purge; content purged unprocessed is refused visibly;
/// and a run is bounded, repeatable, retry-safe and safe alongside another run or processing. The exact 28-day and
/// 30-day boundaries are proven to the microsecond inside single transactions in
/// <c>RetentionDatabaseSecurityTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RawContentRetentionTests(DatabaseFixture fixture)
{
    // The product rule itself (PRD §12 and the Gate 2F owner decisions), not the constants under test, so a changed
    // purge age or limit fails here too: content is due at 28 days and must never be older than 30.
    private static readonly TimeSpan PurgeAge = TimeSpan.FromDays(28);
    private static readonly TimeSpan Limit = TimeSpan.FromDays(30);
    private static readonly TimeSpan OverdueAge = PurgeAge + TimeSpan.FromHours(6);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // Due with an hour to spare, and an hour short of due, by the database's clock.
    private static readonly TimeSpan Due = PurgeAge + Hour;
    private static readonly TimeSpan NotYetDue = PurgeAge - Hour;

    private static TimeSpan Microsecond => RetentionHarness.Microsecond;

    // A purge Worker whose clock runs two months behind, or two months ahead of, the database's.
    private static DateTimeOffset WorkerBehind => DateTimeOffset.UtcNow.AddDays(-60);

    private static DateTimeOffset WorkerAhead => DateTimeOffset.UtcNow.AddDays(60);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Content_becomes_due_at_twenty_eight_days_by_the_database_clock_and_younger_content_remains()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var due = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { ObjectId = "b-1", Revision = 1 }, "evt-boundary-due");
        var notYet = await host.SubmitAgedAsync(fixture, clock, NotYetDue, tenant, connection, new SyntheticEvent { ObjectId = "b-2", Revision = 1 }, "evt-boundary-not-yet");
        foreach (var delivered in new[] { due, notYet })
        {
            Assert.Equal(EventProcessingOutcome.AcceptedForProjection, (await host.ProcessSettledAsync(delivered.Envelope)).Outcome);
        }

        var run = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal((1, 0, 0), (run.ProcessedPurged, run.QuarantinedPurged, run.UnprocessedPurged));
        RetentionHarness.AssertAge(Due, run.OldestPurgedAge);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, due.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, notYet.ReceiptId));

        // Due is not late: the run purged within the limit and left nothing overdue.
        Assert.Equal(RawContentRetentionStatus.WithinLimit, run.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, run.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));

        // 28 days stays the purge age and 30 days the hard limit.
        Assert.Equal(PurgeAge, RawContentRetention.PurgeAge);
        Assert.Equal(Limit, RawContentRetention.Window);
    }

    [Fact]
    public async Task A_worker_clock_running_behind_cannot_delay_a_due_purge()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var due = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { ObjectId = "w-1", Revision = 1 }, "evt-behind-due");
        var notYet = await host.SubmitAgedAsync(fixture, clock, NotYetDue, tenant, connection, new SyntheticEvent { ObjectId = "w-2", Revision = 1 }, "evt-behind-not-yet");

        // By the Worker's clock this content is two months short of due; by the database's it is due.
        var run = await host.PurgeAsync(clock, WorkerBehind);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal(1, run.ReceiptsPurged);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, due.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, notYet.ReceiptId));
        RetentionHarness.AssertAge(Due, run.OldestPurgedAge);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));
    }

    [Fact]
    public async Task A_worker_clock_running_ahead_cannot_purge_early()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (youngTenant, youngConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var due = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { ObjectId = "a-1", Revision = 1 }, "evt-ahead-due");
        var notYet = await host.SubmitAgedAsync(fixture, clock, NotYetDue, tenant, connection, new SyntheticEvent { ObjectId = "a-2", Revision = 1 }, "evt-ahead-not-yet");
        var young = await host.SubmitAgedAsync(fixture, clock, Hour, tenant, connection, new SyntheticEvent { ObjectId = "a-3", Revision = 1 }, "evt-ahead-young");
        var onlyYoung = await host.SubmitAgedAsync(
            fixture, clock, NotYetDue, youngTenant, youngConnection, new SyntheticEvent { ObjectId = "a-4", Revision = 1 }, "evt-ahead-only-young");

        // By the Worker's clock all of it is long past 30 days; by the database's only one receipt is due. The run
        // visits that receipt's tenant and clears it alone, and never visits the tenant holding only younger content.
        var run = await host.PurgeAsync(clock, WorkerAhead);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal(1, run.ReceiptsPurged);
        Assert.Equal(1, run.TenantsVisited);
        RetentionHarness.AssertAge(Due, run.OldestPurgedAge);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, due.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, notYet.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, young.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, youngTenant, onlyYoung.ReceiptId));
    }

    [Fact]
    public async Task An_ingress_clock_far_ahead_cannot_delay_a_purge()
    {
        // Ingress believes it is ten years from now. The database stamps the receipt time with its own clock, so the
        // content does not look ten years younger, and once the database shows it 28 days old it is purged.
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        clock.UtcNow = DateTimeOffset.UtcNow.AddYears(10);
        var before = await IngressData.DatabaseNowAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-ingress-ahead");
        var after = await IngressData.DatabaseNowAsync(fixture, Token);
        Assert.InRange(await IngressData.ReceiptTimeAsync(fixture, delivered.ReceiptId, Token), before, after);

        // 28 days and an hour pass by the database's clock.
        await RetentionHarness.AgeReceiptAsync(fixture, delivered.ReceiptId, Due);
        var run = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal(1, run.ReceiptsPurged);
        RetentionHarness.AssertAge(Due, run.OldestPurgedAge);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, delivered.ReceiptId));
    }

    [Fact]
    public async Task An_ingress_clock_far_behind_cannot_cause_an_early_purge()
    {
        // Ingress believes it is ten years ago. The database stamps the receipt time with its own clock, so the content
        // does not look ten years older: an hour short of 28 days by the database's clock it stays, and it is purged
        // only once the database shows it due.
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        clock.UtcNow = DateTimeOffset.UtcNow.AddYears(-10);
        var before = await IngressData.DatabaseNowAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-ingress-behind");
        var after = await IngressData.DatabaseNowAsync(fixture, Token);
        Assert.InRange(await IngressData.ReceiptTimeAsync(fixture, delivered.ReceiptId, Token), before, after);

        await RetentionHarness.AgeReceiptAsync(fixture, delivered.ReceiptId, NotYetDue);
        var early = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, early.Outcome);
        Assert.Equal(0, early.ReceiptsPurged);
        Assert.Equal(0, early.TenantsVisited);
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, delivered.ReceiptId));

        // Two more hours pass by the database's clock, and now it is due.
        await RetentionHarness.AgeReceiptAsync(fixture, delivered.ReceiptId, TimeSpan.FromHours(2));
        var due = await host.PurgeAsync(clock);

        Assert.Equal(1, due.ReceiptsPurged);
        RetentionHarness.AssertAge(NotYetDue + TimeSpan.FromHours(2), due.OldestPurgedAge);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, delivered.ReceiptId));
    }

    [Fact]
    public async Task Only_the_server_side_receipt_time_decides_expiry_never_a_time_the_provider_states()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var future = DateTimeOffset.UtcNow.AddYears(5);
        var ancient = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Received past the purge age, though the payload claims its change happens years from now.
        var receivedLongAgo = await host.SubmitAgedAsync(
            fixture, clock, Due, tenant, connection,
            new SyntheticEvent { ObjectId = "p-1", Revision = 1, UpdatedAt = future, OccurredAt = future }, "evt-provider-future");

        // Received recently, though the payload claims its change is decades old.
        var receivedRecently = await host.SubmitAgedAsync(
            fixture, clock, PurgeAge - TimeSpan.FromDays(3), tenant, connection,
            new SyntheticEvent { ObjectId = "p-2", Revision = 1, UpdatedAt = ancient, OccurredAt = ancient }, "evt-provider-ancient");

        var run = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal((0, 0, 1), (run.ProcessedPurged, run.QuarantinedPurged, run.UnprocessedPurged));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, receivedLongAgo.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, receivedRecently.ReceiptId));
    }

    [Fact]
    public async Task The_snapshot_the_ledger_the_ordering_state_and_the_outbox_stay_whole_and_provenance_still_resolves()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var deliveries = new[]
        {
            await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-keep-1"),
            await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(1), tenant, connection,
                new SyntheticEvent { Revision = 2, Changes = [new("status", "TaskStatus", "done", "working")] }, "evt-keep-2"),
            await host.SubmitAgedAsync(fixture, clock, Due - TimeSpan.FromMinutes(2), tenant, connection, new SyntheticEvent { ObjectId = "1002" }, "evt-keep-reconcile"),
            await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(3), tenant, connection,
                new SyntheticEvent { Revision = 3, Changes = [new("active_app", "PcActivity", null, "editor")] }, "evt-keep-quarantine"),
        };
        var outcomes = new List<EventProcessingOutcome>();
        foreach (var delivered in deliveries)
        {
            outcomes.Add((await host.ProcessSettledAsync(delivered.Envelope)).Outcome);
        }

        Assert.Equal(
            [
                EventProcessingOutcome.AcceptedForProjection,
                EventProcessingOutcome.AcceptedForProjection,
                EventProcessingOutcome.ReconciliationRequired,
                EventProcessingOutcome.Quarantined,
            ],
            outcomes);

        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        var ledgerFields = await LedgerFieldsAsync(ledger);
        var states = await ProcessingHarness.FieldStatesAsync(fixture, tenant);
        var objects = await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        var markers = await IngressData.MarkersAsync(fixture, tenant, Token);
        var receipts = await RetentionHarness.ContentAsync(fixture, tenant);
        Assert.Equal(4, ledger.Count);
        Assert.NotEmpty(facts);
        Assert.All(receipts, receipt => Assert.True(receipt.HasPayload && receipt.HasFingerprint));

        var run = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal((3, 1, 0), (run.ProcessedPurged, run.QuarantinedPurged, run.UnprocessedPurged));

        // Nothing but the content changed: the ledger and its fields, the ordering state, the Snapshot and the
        // outbox markers are exactly as they were.
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Equal(ledgerFields, await LedgerFieldsAsync(ledger));
        Assert.Equal(states, await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal(objects, await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Equal(facts, await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(markers, await IngressData.MarkersAsync(fixture, tenant, Token));

        // Every receipt row stays with every identifier, its receipt time and its state; only the content is gone.
        var purged = await RetentionHarness.ContentAsync(fixture, tenant);
        Assert.Equal(receipts.Select(receipt => receipt with { HasPayload = false, HasFingerprint = false }), purged);

        // Provenance still resolves: each ledger entry names a receipt that exists with the same identity, and
        // each Snapshot fact names a ledger event.
        foreach (var entry in ledger)
        {
            var receipt = Assert.Single(purged, row => row.ReceiptId == entry.ReceiptId);
            Assert.Equal(entry.ConnectionReference, receipt.ConnectionReference);
            Assert.Equal(entry.SourceEventId, receipt.SourceEventId);
        }

        Assert.All(facts, fact => Assert.Contains(ledger, entry => entry.EventId == fact.LastEventId));
    }

    [Fact]
    public async Task Ingress_still_recognizes_a_repeated_provider_event_after_its_content_expired()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var synthetic = new SyntheticEvent { Revision = 1 };

        var first = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, synthetic, "evt-repeat-after-expiry");
        await host.ProcessSettledAsync(first.Envelope);
        Assert.Equal(1, (await host.PurgeAsync(clock)).ReceiptsPurged);

        clock.UtcNow = DateTimeOffset.UtcNow;
        var repeat = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-repeat-after-expiry", synthetic.ToPayload()));

        Assert.Equal(IngressStatus.Duplicate, repeat.Status);
        Assert.Equal(first.ReceiptId, repeat.ReceiptId);

        // Nothing was stored again: the retained receipt row decided, and its content stays expired.
        var receipt = Assert.Single(await RetentionHarness.ContentAsync(fixture, tenant));
        Assert.Equal(first.ReceiptId, receipt.ReceiptId);
        Assert.False(receipt.HasPayload);
        Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token));
        Assert.Contains(host.Logs.Entries, log =>
            log.Message.Contains("stored content has expired", StringComparison.Ordinal)
            && log.Message.Contains(first.ReceiptId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_processed_receipt_redelivered_after_the_purge_is_still_a_ledger_no_op()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var delivered = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-redelivered-after-purge");
        var accepted = await host.ProcessSettledAsync(delivered.Envelope);
        Assert.Equal(1, (await host.PurgeAsync(clock)).ReceiptsPurged);
        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);

        var redelivered = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.Duplicate, redelivered.Outcome);
        Assert.Equal(accepted.EventId, redelivered.EventId);
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, redelivered.PreviousOutcome);
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Equal(facts, await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));

        // Through the transport as well: the redelivered message completes.
        var settlement = await host.Consumer.ConsumeAsync(ProcessingHarness.Received(delivered.Envelope, deliveryCount: 2), Token);
        Assert.Equal(TransportSettlementKind.Complete, settlement.Kind);
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, tenant));
    }

    [Fact]
    public async Task A_receipt_whose_content_expired_before_processing_is_refused_and_dead_lettered_never_silently()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-expired-unprocessed");

        var run = await host.PurgeAsync(clock);

        // The loss is counted apart and logged as a warning, never folded into the ordinary count.
        Assert.Equal((0, 0, 1), (run.ProcessedPurged, run.QuarantinedPurged, run.UnprocessedPurged));
        Assert.Contains(host.Logs.Entries, log =>
            log.Level == LogLevel.Warning
            && log.Message.Contains("processing had not settled", StringComparison.Ordinal)
            && log.Message.Contains(tenant.ToString(), StringComparison.Ordinal));

        var processed = await host.ProcessAsync(delivered.Envelope, Token);
        Assert.True(processed.IsFailure);
        Assert.Equal(ProcessingErrors.ReceiptContentExpired, processed.Error);

        var settlement = await host.Consumer.ConsumeAsync(ProcessingHarness.Received(delivered.Envelope), Token);
        Assert.Equal(TransportSettlementKind.DeadLetter, settlement.Kind);
        Assert.Equal(ProcessingErrors.ReceiptContentExpired.Code, settlement.DeadLetterReason!.Code);

        // Nothing was recorded or guessed, and the receipt stays unsettled with its identity.
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));
        Assert.Contains(host.Logs.Entries, log =>
            log.Level == LogLevel.Error
            && log.Message.Contains(ProcessingErrors.ReceiptContentExpired.Code, StringComparison.Ordinal)
            && log.Message.Contains(delivered.ReceiptId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeating_a_run_purges_nothing_more_and_changes_nothing()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        for (var index = 0; index < 3; index++)
        {
            await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(index), tenant, connection, new SyntheticEvent { ObjectId = $"r-{index}", Revision = 1 }, $"evt-rerun-{index}");
        }

        await host.SubmitAgedAsync(fixture, clock, NotYetDue, tenant, connection, new SyntheticEvent { ObjectId = "r-young", Revision = 1 }, "evt-rerun-young");

        var first = await host.PurgeAsync(clock);
        Assert.Equal(RawContentPurgeOutcome.Completed, first.Outcome);
        Assert.Equal(3, first.ReceiptsPurged);
        Assert.Equal(1, first.TenantsVisited);
        var state = await RetentionHarness.ContentAsync(fixture, tenant);

        var second = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, second.Outcome);
        Assert.Equal(0, second.ReceiptsPurged);
        Assert.Equal(0, second.TenantsVisited);
        Assert.Null(second.OldestPurgedAge);
        Assert.False(second.DueContentRemaining);
        Assert.Equal(state, await RetentionHarness.ContentAsync(fixture, tenant));
        Assert.Single(state, receipt => receipt.HasPayload);
    }

    [Fact]
    public async Task Batches_and_the_per_tenant_budget_bound_one_run_and_the_next_run_finishes_the_rest()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using (var cleaner = RetentionHarness.Host(fixture, new InMemoryTransport(), clock))
        {
            await cleaner.ClearDueAsync(clock);
        }

        await using var host = RetentionHarness.Host(
            fixture, new InMemoryTransport(), clock, purge: options =>
            {
                options.BatchSize = 2;
                options.MaxBatchesPerTenant = 2;
            });
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        for (var index = 0; index < 5; index++)
        {
            await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(index), tenant, connection, new SyntheticEvent { ObjectId = $"q-{index}", Revision = 1 }, $"evt-batch-{index}");
        }

        var first = await host.PurgeAsync(clock);

        // Two batches of two, oldest first; the fifth receipt waits for the next run, which is reported.
        Assert.Equal(RawContentPurgeOutcome.Partial, first.Outcome);
        Assert.Equal(4, first.ReceiptsPurged);
        Assert.True(first.DueContentRemaining);
        RetentionHarness.AssertAge(Due, first.OldestPurgedAge);
        RetentionHarness.AssertAge(Due - TimeSpan.FromMinutes(4), first.OldestRemainingAge);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(first));
        Assert.Equal([false, false, false, false, true], (await RetentionHarness.ContentAsync(fixture, tenant)).Select(receipt => receipt.HasPayload));

        var second = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, second.Outcome);
        Assert.Equal(1, second.ReceiptsPurged);
        Assert.False(second.DueContentRemaining);
        Assert.Null(second.OldestRemainingAge);
        Assert.All(await RetentionHarness.ContentAsync(fixture, tenant), receipt => Assert.False(receipt.HasPayload));
    }

    [Fact]
    public async Task A_run_visits_a_bounded_number_of_tenants_oldest_expired_content_first()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using (var cleaner = RetentionHarness.Host(fixture, new InMemoryTransport(), clock))
        {
            await cleaner.ClearDueAsync(clock);
        }

        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock, purge: options => options.TenantsPerRun = 1);
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var newer = await host.SubmitAgedAsync(fixture, clock, Due, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-tenants-a");
        var older = await host.SubmitAgedAsync(fixture, clock, Due + TimeSpan.FromMinutes(1), tenantB, connectionB, new SyntheticEvent { Revision = 1 }, "evt-tenants-b");

        var first = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Partial, first.Outcome);
        Assert.Equal(1, first.TenantsVisited);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenantB, older.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenantA, newer.ReceiptId));
        RetentionHarness.AssertAge(Due, first.OldestRemainingAge);

        var second = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, second.Outcome);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenantA, newer.ReceiptId));
    }

    [Fact]
    public async Task Each_tenant_is_purged_in_its_own_scope_and_a_tenant_without_expired_content_is_never_visited()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (expiredTenant, expiredConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (youngTenant, youngConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var expired = await host.SubmitAgedAsync(fixture, clock, Due, expiredTenant, expiredConnection, new SyntheticEvent { Revision = 1 }, "evt-scope-expired");
        var young = await host.SubmitAgedAsync(fixture, clock, NotYetDue, youngTenant, youngConnection, new SyntheticEvent { Revision = 1 }, "evt-scope-young");

        var run = await host.PurgeAsync(clock);

        Assert.Equal(1, run.TenantsVisited);
        Assert.Equal(1, run.ReceiptsPurged);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, expiredTenant, expired.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, youngTenant, young.ReceiptId));

        var purgeLogs = host.Logs.Entries.Where(log => log.Category.EndsWith("RawContentPurge", StringComparison.Ordinal)).ToList();
        Assert.Contains(purgeLogs, log => log.Message.Contains(expiredTenant.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(purgeLogs, log => log.Text.Contains(youngTenant.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_receipt_held_by_processing_or_another_run_is_skipped_without_waiting_and_purged_by_a_later_run()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var deliveries = new List<Delivered>();
        for (var index = 0; index < 3; index++)
        {
            deliveries.Add(await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(index), tenant, connection, new SyntheticEvent { ObjectId = $"h-{index}", Revision = 1 }, $"evt-held-{index}"));
        }

        var held = deliveries[0].ReceiptId;
        RawContentPurgeRun whileHeld;
        await using (var holder = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            // The row lock processing takes on a receipt, held open here by another transaction.
            await using var hold = await HoldAsync(holder, held);
            whileHeld = await host.PurgeAsync(clock).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, held));
            await hold.RollbackAsync(Token);
        }

        Assert.Equal(RawContentPurgeOutcome.Partial, whileHeld.Outcome);
        Assert.Equal(2, whileHeld.ReceiptsPurged);
        Assert.True(whileHeld.DueContentRemaining);
        RetentionHarness.AssertAge(Due, whileHeld.OldestRemainingAge);

        // Due content left within the limit is not a failure: the next run takes it.
        Assert.Equal(RawContentRetentionStatus.WithinLimit, whileHeld.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(whileHeld));

        var afterRelease = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, afterRelease.Outcome);
        Assert.Equal(1, afterRelease.ReceiptsPurged);
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, held));
    }

    [Fact]
    public async Task Concurrent_runs_purge_every_expired_receipt_exactly_once()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var hostA = RetentionHarness.Host(fixture, new InMemoryTransport(), clock, purge: options => options.BatchSize = 3);
        await using var hostB = RetentionHarness.Host(fixture, new InMemoryTransport(), clock, purge: options => options.BatchSize = 3);
        await hostA.ClearDueAsync(clock);
        var tenants = new[] { await IngressData.NewConnectionAsync(fixture, Token), await IngressData.NewConnectionAsync(fixture, Token) };
        for (var index = 0; index < 20; index++)
        {
            var (tenant, connection) = tenants[index % 2];
            await hostA.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromSeconds(index), tenant, connection, new SyntheticEvent { ObjectId = $"c-{index}", Revision = 1 }, $"evt-concurrent-{index}");
        }

        var purges = new[] { hostA, hostB }
            .Select(host => Task.Run(() => host.Services.GetRequiredService<IRawContentPurge>().PurgeExpiredAsync(Token), CancellationToken.None))
            .ToArray();
        var runs = await Task.WhenAll(purges);

        Assert.All(runs, run => Assert.NotEqual(RawContentPurgeOutcome.Failed, run.Outcome));
        Assert.Equal(20, runs.Sum(run => run.ReceiptsPurged));
        foreach (var (tenant, _) in tenants)
        {
            Assert.All(await RetentionHarness.ContentAsync(fixture, tenant), receipt => Assert.False(receipt.HasPayload));
        }

        var settled = await hostA.PurgeAsync(clock);
        Assert.Equal(RawContentPurgeOutcome.Completed, settled.Outcome);
        Assert.Equal(0, settled.ReceiptsPurged);
    }

    [Fact]
    public async Task The_purge_refuses_to_run_inside_a_tenant_scope()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var expired = await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-refused-scope");

        var refused = await Task.Run(
            async () =>
            {
                host.TenantContextAccessor.SetResolved(
                    TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
                return await Assert.ThrowsAsync<InvalidOperationException>(
                    () => host.Services.GetRequiredService<IRawContentPurge>().PurgeExpiredAsync(Token));
            },
            CancellationToken.None);

        // Refused before anything was read or changed, even the caller's own tenant's expired content.
        Assert.Contains("must run in a scope without a resolved tenant", refused.Message, StringComparison.Ordinal);
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, expired.ReceiptId));
    }

    [Fact]
    public async Task A_failed_batch_rolls_back_whole_the_run_reports_the_failure_and_a_retry_completes()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var failing = new FailingCommit();
        await using var host = RetentionHarness.Host(
            fixture,
            new InMemoryTransport(),
            clock,
            more: services => services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(failing)));
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        for (var index = 0; index < 3; index++)
        {
            await host.SubmitAgedAsync(
                fixture, clock, Due - TimeSpan.FromMinutes(index), tenant, connection, new SyntheticEvent { ObjectId = $"f-{index}", Revision = 1 }, $"evt-failed-batch-{index}");
        }

        failing.Arm();
        var failed = await host.PurgeAsync(clock);

        // The batch had cleared all three inside its transaction when the commit failed; none of it survived.
        Assert.Equal(3L, failing.ClearedSeenAtCommit);
        Assert.Equal(RawContentPurgeOutcome.Failed, failed.Outcome);
        Assert.Equal(1, failed.TenantFailures);
        Assert.Equal(0, failed.ReceiptsPurged);
        Assert.True(failed.DueContentRemaining);
        Assert.Equal(1, RawContentPurgeEntryPoint.ExitCodeFor(failed));
        Assert.All(await RetentionHarness.ContentAsync(fixture, tenant), receipt => Assert.True(receipt.HasPayload && receipt.HasFingerprint));

        var retried = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, retried.Outcome);
        Assert.Equal(3, retried.ReceiptsPurged);
        Assert.All(await RetentionHarness.ContentAsync(fixture, tenant), receipt => Assert.False(receipt.HasPayload || receipt.HasFingerprint));
    }

    [Fact]
    public async Task A_run_that_cannot_reach_the_database_reports_a_failure_and_purges_nothing()
    {
        using var metrics = new EventBackboneMetrics();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));

        var run = await RawContentPurgeEntryPoint.RunOnceAsync(TransportHost.UnusedDatabase, loggerFactory, metrics, cancellationToken: Token);

        Assert.True(run.DiscoveryFailed);
        Assert.Equal(RawContentPurgeOutcome.Failed, run.Outcome);
        Assert.Equal((0, 0, 0), (run.TenantsVisited, run.ReceiptsPurged, run.TenantFailures));
        Assert.True(run.DueContentRemaining);
        Assert.Equal(1, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.Contains(logs.Entries, log => log.Level == LogLevel.Error && log.Message.Contains("could not find due content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_entry_point_purges_as_the_runtime_role_with_the_callers_logging_and_no_clock_of_its_own()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.SubmitAgedAsync(fixture, clock, Due, tenant, connection, new SyntheticEvent { ObjectId = "e-1", Revision = 1 }, "evt-entry-1");
        await host.SubmitAgedAsync(fixture, clock, Due - TimeSpan.FromMinutes(1), tenant, connection, new SyntheticEvent { ObjectId = "e-2", Revision = 1 }, "evt-entry-2");

        using var metrics = new EventBackboneMetrics();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));

        var run = await RawContentPurgeEntryPoint.RunOnceAsync(fixture.ApplicationConnectionString, loggerFactory, metrics, cancellationToken: Token);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal(2, run.ReceiptsPurged);
        RetentionHarness.AssertAge(Due, run.OldestPurgedAge);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.All(await RetentionHarness.ContentAsync(fixture, tenant), receipt => Assert.False(receipt.HasPayload));
        Assert.Contains(logs.Entries, log => log.Message.Contains("Raw-content retention run Completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purging_leaves_the_content_nowhere_and_its_logs_carry_ids_and_counts_only()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        var delivered = await host.SubmitAgedAsync(
            fixture, clock, Due, tenant, connection, new SyntheticEvent { Revision = 1, Actor = marker, Reference = marker }, "evt-content-nowhere");
        Assert.Equal(1L, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        var run = await host.PurgeAsync(clock);

        Assert.Equal(1, run.ReceiptsPurged);
        Assert.Equal(0L, await IngressData.CountStoredContentAsync(fixture, marker, Token));
        Assert.DoesNotContain(host.Logs.Entries, log => log.Text.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, log =>
            log.Category.EndsWith("RawContentPurge", StringComparison.Ordinal)
            && log.Message.Contains(tenant.ToString(), StringComparison.Ordinal)
            && log.Message.Contains("1 unprocessed", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, log => log.Text.Contains(delivered.ReceiptId.ToString(), StringComparison.Ordinal)
            && log.Category.EndsWith("RawContentPurge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_run_purges_all_content_the_database_shows_due_however_far_past_and_normal_lateness_reports_nothing()
    {
        // Whatever has reached the purge age by the database's clock, the next run clears, however long ago it became
        // due: that is what keeps content under the schedule's normal maximum age. Up to the overdue age, a purge that
        // waited for its slot is normal and reports nothing.
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        TimeSpan[] dueAges =
        [
            PurgeAge + TimeSpan.FromMinutes(2),
            PurgeAge + RawContentPurgeSchedule.Cadence + RawContentPurgeSchedule.AllowedStartDelay,
            RawContentPurgeSchedule.NormalMaximumAge,
            OverdueAge - TimeSpan.FromMinutes(10),
        ];
        var due = new List<Guid>();
        for (var index = 0; index < dueAges.Length; index++)
        {
            due.Add((await host.SubmitAgedAsync(
                fixture, clock, dueAges[index], tenant, connection, new SyntheticEvent { ObjectId = $"n-{index}", Revision = 1 }, $"evt-normal-{index}")).ReceiptId);
        }

        var notYet = await host.SubmitAgedAsync(fixture, clock, NotYetDue, tenant, connection, new SyntheticEvent { ObjectId = "n-young", Revision = 1 }, "evt-normal-young");

        var run = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        Assert.Equal(dueAges.Length, run.ReceiptsPurged);
        foreach (var receiptId in due)
        {
            Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, receiptId));
        }

        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, notYet.ReceiptId));
        RetentionHarness.AssertAge(OverdueAge - TimeSpan.FromMinutes(10), run.OldestPurgedAge);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, run.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, run.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.True(RawContentPurgeSchedule.NormalMaximumAge < OverdueAge);
    }

    [Fact]
    public async Task Overdue_content_that_a_late_run_clears_is_reported_at_its_database_age_but_the_run_exits_zero()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var late = OverdueAge + Hour;
        var delayed = await host.SubmitAgedAsync(fixture, clock, late, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-overdue-cleared");

        // By the Worker's clock, a month behind, this content would not even be due; the database's age decides.
        var run = await host.PurgeAsync(clock, DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(RawContentPurgeOutcome.Completed, run.Outcome);
        RetentionHarness.AssertAge(late, run.OldestPurgedAge);
        Assert.Equal(RawContentRetentionStatus.Overdue, run.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, run.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, delayed.ReceiptId));

        // The delay is observable all the same, at its database age, in the metrics and the log.
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.overdue", ("level", "overdue"), ("state", "purged")));
        Assert.DoesNotContain(recorder.Of("workcontinuity.retention.overdue"), measurement => measurement.Has("state", "remaining"));
        RetentionHarness.AssertAge(late, TimeSpan.FromSeconds(Assert.Single(recorder.Of("workcontinuity.retention.oldest_purged_age")).Value));
        Assert.Contains(host.Logs.Entries, log => log.Level == LogLevel.Warning && log.Message.Contains("purges ran late", StringComparison.Ordinal));
        recorder.AssertBoundedTags(tenant.ToString(), delayed.ReceiptId.ToString("D"));
    }

    [Fact]
    public async Task Overdue_content_still_left_is_classified_by_its_database_age_whatever_the_worker_clock_and_the_run_exits_three()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var late = OverdueAge + Hour;
        var stuck = await host.SubmitAgedAsync(fixture, clock, late, tenant, connection, new SyntheticEvent { ObjectId = "o-1", Revision = 1 }, "evt-overdue-left");

        // Younger content of the same tenant must never stand in for the oldest.
        var young = await host.SubmitAgedAsync(fixture, clock, Hour, tenant, connection, new SyntheticEvent { ObjectId = "o-2", Revision = 1 }, "evt-overdue-young");

        RawContentPurgeRun workerBehind;
        RawContentPurgeRun workerAhead;
        await using (var holder = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            await using var hold = await HoldAsync(holder, stuck.ReceiptId);
            workerBehind = await host.PurgeAsync(clock, WorkerBehind).WaitAsync(TimeSpan.FromSeconds(30), Token);
            workerAhead = await host.PurgeAsync(clock, WorkerAhead).WaitAsync(TimeSpan.FromSeconds(30), Token);
            await hold.RollbackAsync(Token);
        }

        // Neither a Worker clock two months behind nor one two months ahead moves the finding: overdue, at its
        // database age, never younger and never past the limit.
        foreach (var run in new[] { workerBehind, workerAhead })
        {
            Assert.Equal(RawContentPurgeOutcome.Partial, run.Outcome);
            RetentionHarness.AssertAge(late, run.OldestRemainingAge);
            Assert.Equal(RawContentRetentionStatus.Overdue, run.RemainingStatus);
            Assert.Equal(3, RawContentPurgeEntryPoint.ExitCodeFor(run));
        }

        // The metrics and the log say the same, at the same database age.
        Assert.Equal(2, recorder.Sum("workcontinuity.retention.overdue", ("level", "overdue"), ("state", "remaining")));
        var remainingAges = recorder.Of("workcontinuity.retention.oldest_remaining_age");
        Assert.Equal(2, remainingAges.Count);
        Assert.All(remainingAges, measurement => RetentionHarness.AssertAge(late, TimeSpan.FromSeconds(measurement.Value)));
        Assert.Equal(2, host.Logs.Entries.Count(log =>
            log.Level == LogLevel.Warning
            && log.Message.Contains("still needs purging", StringComparison.Ordinal)
            && log.Message.Contains(tenant.ToString(), StringComparison.Ordinal)));

        // Once the row is free, the next run clears it: the late purge is still reported, and that run exits 0.
        var cleared = await host.PurgeAsync(clock);

        Assert.Equal(RawContentPurgeOutcome.Completed, cleared.Outcome);
        Assert.Equal(RawContentRetentionStatus.Overdue, cleared.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, cleared.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(cleared));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, tenant, stuck.ReceiptId));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, young.ReceiptId));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.overdue", ("level", "overdue"), ("state", "purged")));
        recorder.AssertBoundedTags(tenant.ToString(), stuck.ReceiptId.ToString("D"));
    }

    [Fact]
    public async Task Content_left_past_the_thirty_day_limit_by_its_database_age_is_critical_and_exits_three_and_its_late_purge_stays_on_record()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var host = RetentionHarness.Host(fixture, new InMemoryTransport(), clock);
        await host.ClearDueAsync(clock);
        using var recorder = new MetricsRecorder(host.Services.GetRequiredService<EventBackboneMetrics>());
        var (nearTenant, nearConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (pastTenant, pastConnection) = await IngressData.NewConnectionAsync(fixture, Token);
        var monthBehind = DateTimeOffset.UtcNow.AddDays(-30);
        var near = await host.SubmitAgedAsync(fixture, clock, Limit - Hour, nearTenant, nearConnection, new SyntheticEvent { Revision = 1 }, "evt-limit-near");

        RawContentPurgeRun beforeLimit;
        RawContentPurgeRun pastLimit;
        Delivered past;
        await using (var nearHolder = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            await using var pastHolder = await fixture.OpenAdministrativeConnectionAsync(Token);
            await using var holdNear = await HoldAsync(nearHolder, near.ReceiptId);

            // An hour short of 30 days by the database's clock the content is overdue, though by the Worker's clock,
            // a month behind, it would not even be due.
            beforeLimit = await host.PurgeAsync(clock, monthBehind).WaitAsync(TimeSpan.FromSeconds(30), Token);

            // An hour past 30 days by the database's clock the limit is exceeded, whatever the Worker's clock says.
            past = await host.SubmitAgedAsync(fixture, clock, Limit + Hour, pastTenant, pastConnection, new SyntheticEvent { Revision = 1 }, "evt-limit-past");
            await using var holdPast = await HoldAsync(pastHolder, past.ReceiptId);
            pastLimit = await host.PurgeAsync(clock, monthBehind).WaitAsync(TimeSpan.FromSeconds(30), Token);

            await holdPast.RollbackAsync(Token);
            await holdNear.RollbackAsync(Token);
        }

        RetentionHarness.AssertAge(Limit - Hour, beforeLimit.OldestRemainingAge);
        Assert.Equal(RawContentRetentionStatus.Overdue, beforeLimit.RemainingStatus);
        Assert.Equal(3, RawContentPurgeEntryPoint.ExitCodeFor(beforeLimit));
        RetentionHarness.AssertAge(Limit + Hour, pastLimit.OldestRemainingAge);
        Assert.Equal(RawContentRetentionStatus.LimitExceeded, pastLimit.RemainingStatus);
        Assert.Equal(3, RawContentPurgeEntryPoint.ExitCodeFor(pastLimit));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.overdue", ("level", "overdue"), ("state", "remaining")));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.overdue", ("level", "limit_exceeded"), ("state", "remaining")));
        Assert.Contains(host.Logs.Entries, log =>
            log.Level == LogLevel.Critical
            && log.Message.Contains("is kept longer than retention allows", StringComparison.Ordinal)
            && log.Message.Contains(pastTenant.ToString(), StringComparison.Ordinal));

        // Cleared late, the breach stays on record at its database age, and the run that cleared it exits 0.
        var cleared = await host.PurgeAsync(clock, monthBehind);

        Assert.Equal(RawContentPurgeOutcome.Completed, cleared.Outcome);
        Assert.Equal(2, cleared.ReceiptsPurged);
        RetentionHarness.AssertAge(Limit + Hour, cleared.OldestPurgedAge);
        Assert.Equal(RawContentRetentionStatus.LimitExceeded, cleared.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, cleared.RemainingStatus);
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(cleared));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, nearTenant, near.ReceiptId));
        Assert.False(await RetentionHarness.HoldsContentAsync(fixture, pastTenant, past.ReceiptId));
        Assert.Equal(1, recorder.Sum("workcontinuity.retention.overdue", ("level", "limit_exceeded"), ("state", "purged")));
        Assert.Contains(host.Logs.Entries, log =>
            log.Level == LogLevel.Critical && log.Message.Contains("was kept longer than retention allows", StringComparison.Ordinal));
        recorder.AssertBoundedTags(nearTenant.ToString(), pastTenant.ToString(), past.ReceiptId.ToString("D"));
    }

    [Fact]
    public async Task A_run_that_cannot_measure_the_content_it_left_fails_rather_than_report_that_the_limit_holds()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var failing = new FailNthCommit();
        await using var host = RetentionHarness.Host(
            fixture,
            new InMemoryTransport(),
            clock,
            more: services => services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(failing)));
        await host.ClearDueAsync(clock);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stuck = await host.SubmitAgedAsync(fixture, clock, OverdueAge + Hour, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-unmeasured");

        RawContentPurgeRun run;
        await using (var holder = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            await using var hold = await HoldAsync(holder, stuck.ReceiptId);

            // The tenant's batch commits first and finds the row held; measuring what is left commits second, and fails.
            failing.Arm(2);
            run = await host.PurgeAsync(clock).WaitAsync(TimeSpan.FromSeconds(30), Token);
            await hold.RollbackAsync(Token);
        }

        Assert.True(run.DiscoveryFailed);
        Assert.Equal(RawContentPurgeOutcome.Failed, run.Outcome);
        Assert.Null(run.OldestRemainingAge);
        Assert.Equal(1, RawContentPurgeEntryPoint.ExitCodeFor(run));
        Assert.Contains(host.Logs.Entries, log =>
            log.Level == LogLevel.Error && log.Message.Contains("cannot show the limit holds", StringComparison.Ordinal));
        Assert.True(await RetentionHarness.HoldsContentAsync(fixture, tenant, stuck.ReceiptId));
    }

    [Fact]
    public void Only_a_failure_exits_one_and_only_overdue_content_left_behind_exits_three()
    {
        var healthy = new RawContentPurgeRun(1, 0, false, 1, 0, 0, false, PurgeAge, null, TimeSpan.Zero);

        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(healthy));
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { OldestPurgedAge = Limit + TimeSpan.FromDays(3) }));
        Assert.Equal(0, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { DueContentRemaining = true, OldestRemainingAge = OverdueAge - Microsecond }));
        Assert.Equal(3, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { DueContentRemaining = true, OldestRemainingAge = OverdueAge }));
        Assert.Equal(3, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { DueContentRemaining = true, OldestRemainingAge = Limit }));

        // A failure keeps its own exit code whatever else is true.
        Assert.Equal(1, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { TenantFailures = 1, DueContentRemaining = true, OldestRemainingAge = Limit }));
        Assert.Equal(1, RawContentPurgeEntryPoint.ExitCodeFor(healthy with { DiscoveryFailed = true, DueContentRemaining = true }));
    }

    /// <summary>Takes the row lock processing would hold on a receipt, until the returned transaction ends.</summary>
    private async Task<NpgsqlTransaction> HoldAsync(NpgsqlConnection holder, Guid receiptId)
    {
        var hold = await holder.BeginTransactionAsync(Token);
        await using var take = new NpgsqlCommand("SELECT receipt_id FROM raw_receipts WHERE receipt_id = @receipt FOR UPDATE;", holder, hold);
        take.Parameters.AddWithValue("receipt", receiptId);
        Assert.Equal(receiptId, await take.ExecuteScalarAsync(Token));
        return hold;
    }

    private async Task<List<IReadOnlyList<(string Field, string DataType, string Decision)>>> LedgerFieldsAsync(IReadOnlyList<LedgerRow> ledger)
    {
        var fields = new List<IReadOnlyList<(string Field, string DataType, string Decision)>>();
        foreach (var entry in ledger)
        {
            fields.Add(await ProcessingHarness.LedgerFieldsAsync(fixture, entry.EventId));
        }

        return fields;
    }

    /// <summary>Fails the next commit once armed, after counting what the batch had cleared inside its transaction.</summary>
    private sealed class FailingCommit : DbTransactionInterceptor
    {
        private int _armed;

        public long ClearedSeenAtCommit { get; private set; } = -1;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                // Inside the tenant's own transaction, so row-level security limits this to that tenant's rows.
                await using var command = transaction.Connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT count(*) FROM raw_receipts WHERE payload IS NULL;";
                ClearedSeenAtCommit = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                throw new InvalidOperationException("The commit failed.");
            }

            return result;
        }
    }

    /// <summary>Fails exactly the n-th commit after it is armed, and no other.</summary>
    private sealed class FailNthCommit : DbTransactionInterceptor
    {
        private int _remaining = -1;

        public void Arm(int nth) => Interlocked.Exchange(ref _remaining, nth);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                throw new InvalidOperationException("The commit failed.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
