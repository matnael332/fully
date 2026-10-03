using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using static WorkContinuity.Integration.Tests.InitialSync.InitialSyncData;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F's fences and ordering races against the real local PostgreSQL, with real ingress and the real transport
/// consumer: membership of the buffer is decided by the durable ingress sequence under the connection's locks, never by a
/// time; a receipt being stored when the buffer starts or at the cutover is waited for; everything held up to the cutover
/// is replayed before Live and everything after is processed live; and whatever order monday's changes and deliveries
/// arrive in, the Snapshot converges to monday's current state without ever regressing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InitialSyncFenceTests(DatabaseFixture fixture)
{
    private const string Board = InitialSyncStack.Board;
    private const string ItemA = "1772099201";
    private const string ItemB = "1772099202";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_receipt_before_the_buffer_start_is_processed_as_before_and_one_after_it_is_held_until_the_baseline_whatever_the_clock_says()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);

        // Stored before the connection took part in initial sync, which its first enabled scope begins: processed as before.
        var (before, beforeSettlement) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (beforeSettlement, (await ReceiptAsync(fixture, before)).State));
        var ledger = (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count;

        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var fence = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.BufferStartFence;
        Assert.True((await ReceiptAsync(fixture, before)).Sequence <= fence);

        // D3F-12: acknowledged and durable, but nothing is decided: no ledger entry, no Snapshot, no reconciliation request.
        var (after, settlement) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal(TransportSettlementKind.Complete, settlement);
        var held = await ReceiptAsync(fixture, after);
        Assert.Equal("Received", held.State);
        Assert.True(held.Sequence > fence);
        Assert.Equal(ledger, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // DoD 6: no time decides membership. On a clock a day before the buffer started, the receipt is still held.
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddDays(-1);
        Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(after));
        Assert.Equal("Received", (await ReceiptAsync(fixture, after)).State);
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddDays(1);

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // Replayed after the baseline, as the unconfirmed hint it is: it requested reconciliation and applied nothing.
        Assert.Equal("Processed", (await ReceiptAsync(fixture, after)).State);
        var replayed = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == after);
        Assert.Equal(("ReconciliationRequired", (string?)ProcessingErrors.UnconfirmedSourceValues.Code), (replayed.Outcome, replayed.ReasonCode));
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);
    }

    [Fact]
    public async Task A_receipt_still_being_stored_when_the_buffer_starts_is_waited_for_and_lies_within_the_fence()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        stack.Account.AddBoard(Board);
        await stack.ConfigureAsync(Board);

        // Ingress caught mid-write for the connection's very first generation, with a later receipt committed meanwhile.
        var inFlight = Guid.CreateVersion7();
        var (runtime, transaction) = await BeginIngressAsync(fixture, stack.Connection, inFlight, ItemA, "Done");
        await using (runtime)
        {
            var later = await stack.PostAsync(ItemB, "Stuck");
            var starting = stack.StartSyncAsync();

            // D3F-10: the buffer start waits for the receipt being stored, so its fence is exact.
            await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
            Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, runtime.ProcessID, starting));
            Assert.False(starting.IsCompleted);
            await transaction.CommitAsync(Token);
            Assert.True((await starting).IsSuccess);

            var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
            Assert.True((await ReceiptAsync(fixture, inFlight)).Sequence <= sync.BufferStartFence);
            Assert.True((await ReceiptAsync(fixture, later)).Sequence <= sync.BufferStartFence);

            // Stored after the connection took part, both lie above its replay floor: held, for this generation to replay.
            Assert.True((await ReceiptAsync(fixture, inFlight)).Sequence > sync.ReplayFloor);
            Assert.True((await ReceiptAsync(fixture, later)).Sequence > sync.ReplayFloor);
            Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(inFlight));
            Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(later));
            Assert.Equal(("Received", "Received"), ((await ReceiptAsync(fixture, inFlight)).State, (await ReceiptAsync(fixture, later)).State));
        }

        // So is one stored after the buffer started.
        var (next, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Received", (await ReceiptAsync(fixture, next)).State);
    }

    [Fact]
    public async Task A_crash_after_the_buffer_start_and_before_any_call_loses_nothing()
    {
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        Guid held;
        Connectors.SeededConnection connection;
        await using (var first = await InitialSyncStack.StartAsync(fixture))
        {
            first.Account.AddBoard(Board);
            await first.ConfigureAsync(Board);
            await first.StartedAsync();
            (held, _) = await first.DeliverAsync(ItemA, "Done");
            connection = first.Connection;
            Assert.Empty(first.Account.Answered);
        }

        // Another process, with nothing in memory, continues from the durable state alone.
        await using var second = await InitialSyncStack.StartAsync(fixture, connection: connection);
        second.Account.AddBoard(Board);
        second.Account.AddItem(ItemA, Board, "Done", at);

        Assert.Equal(InitialSyncState.Live, (await second.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, second.Tenant)).Value);
    }

    [Fact]
    public async Task Every_receipt_held_up_to_the_cutover_is_replayed_once_and_one_after_it_is_processed_live()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        var (first, _) = await stack.DeliverAsync(ItemA, "Working on it");
        Guid duringBaseline = Guid.Empty;
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                (duringBaseline, _) = await stack.DeliverAsync(ItemA, "Stuck");
            }
        };

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // DoD 18: everything in (buffer start, cutover] was replayed, and lies within the fences.
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        foreach (var receipt in new[] { first, duringBaseline })
        {
            var (sequence, state) = await ReceiptAsync(fixture, receipt);
            Assert.Equal("Processed", state);
            Assert.True(sequence > sync.ReplayFloor && sequence <= sync.CutoverFence, $"{sync.ReplayFloor} < {sequence} <= {sync.CutoverFence}");
            Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == receipt);
        }

        // A held message delivered again after Live is a no-op through the ledger.
        var ledger = await ProcessingHarness.LedgerAsync(fixture, stack.Tenant);
        Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(first));
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));

        // DoD 19: after the cutover, a receipt is processed as it arrives.
        var (live, settlement) = await stack.DeliverAsync(ItemA, "Done");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (settlement, (await ReceiptAsync(fixture, live)).State));
        Assert.True((await ReceiptAsync(fixture, live)).Sequence > sync.CutoverFence);
    }

    [Fact]
    public async Task A_receipt_still_being_stored_at_the_cutover_is_waited_for_and_replayed_before_live()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // Ingress caught mid-write after the baseline read: the cutover will meet it.
        var inFlight = Guid.CreateVersion7();
        var opened = new TaskCompletionSource<(Npgsql.NpgsqlConnection Connection, Npgsql.NpgsqlTransaction Transaction)>(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                opened.SetResult(await BeginIngressAsync(fixture, stack.Connection, inFlight, ItemA, "Stuck"));
            }
        };

        var running = stack.RunSyncAsync();
        var (runtime, transaction) = await opened.Task;
        await using (runtime)
        {
            // D3F-11: the cutover waits for it, so no receipt falls between the last replay and Live.
            await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
            Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, runtime.ProcessID, running));
            Assert.False(running.IsCompleted);
            await transaction.CommitAsync(Token);
        }

        Assert.Equal(InitialSyncState.Live, (await running).Value.State);

        // Replayed by the cutover itself: processed without its message ever being delivered, within the cutover fence.
        var (sequence, state) = await ReceiptAsync(fixture, inFlight);
        Assert.Equal("Processed", state);
        Assert.True(sequence <= (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.CutoverFence);
    }

    [Fact]
    public async Task Race_A_a_change_after_the_baseline_read_is_replayed_as_a_hint_and_reconciliation_applies_it()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Working on it", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // monday read the old status for the page; the user changes it right after, and the webhook arrives.
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                stack.Account.SetStatus(ItemA, "Done", at.AddMinutes(2));
                await stack.DeliverAsync(ItemA, "Done");
            }
        };

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Working on it", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);

        // The replayed hint requested reconciliation, which rereads the current state and applies it.
        Assert.Equal("Pending", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
        Assert.Equal(1, (await stack.ReconcileAsync()).Count(ReconciliationOutcome.Repaired));
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("Done", (DateTimeOffset?)at.AddMinutes(2)), (field.Value, field.SourceUpdatedAtUtc));
    }

    [Fact]
    public async Task Race_B_a_buffered_hint_older_than_the_baseline_never_regresses_the_snapshot()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);

        // monday already holds the newer state; an older change's webhook is delivered late, during the buffer.
        stack.Account.AddItem(ItemA, Board, "Done", at.AddMinutes(5));
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var (older, _) = await stack.DeliverAsync(ItemA, "Working on it");

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        var baseline = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal("Done", baseline.Value);

        // The hint was replayed in ingress order, and only requested reconciliation; it carried no value into the Snapshot.
        var hint = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == older);
        Assert.Equal("ReconciliationRequired", hint.Outcome);

        // Reconciliation rereads monday: the Snapshot already holds its current state, and nothing changes.
        Assert.Equal(1, (await stack.ReconcileAsync()).Count(ReconciliationOutcome.Current));
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("Done", baseline.LastEventId, baseline.FieldVersion), (field.Value, field.LastEventId, field.FieldVersion));
    }

    [Fact]
    public async Task Race_C_several_changes_during_the_scan_converge_to_mondays_current_state()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-20);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 1;
        stack.Account.AddItem(ItemA, Board, "Working on it", at);
        stack.Account.AddItem(ItemB, Board, "Working on it", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                stack.Account.SetStatus(ItemA, "Stuck", at.AddMinutes(1));
                await stack.DeliverAsync(ItemA, "Stuck");
                stack.Account.SetStatus(ItemA, "Done", at.AddMinutes(2));
                stack.Account.SetStatus(ItemB, "Stuck", at.AddMinutes(2));
                await stack.DeliverAsync(ItemA, "Done");
                await stack.DeliverAsync(ItemB, "Stuck");
            }
            else if (operation == "next")
            {
                stack.Account.SetStatus(ItemB, "Done", at.AddMinutes(3));
                await stack.DeliverAsync(ItemB, "Done");
            }
        };

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        await stack.ReconcileAsync();

        Assert.Equal(
            [(ItemA, "Done"), (ItemB, "Done")],
            (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => (field.ObjectId, field.Value!)).Order());
        Assert.All(await TargetsAsync(fixture, stack.Tenant), target => Assert.Equal("Resolved", target.State));
    }
}
