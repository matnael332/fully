using WorkContinuity.Core.InitialSync;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using static WorkContinuity.Integration.Tests.InitialSync.InitialSyncData;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F's current work view (final owner review): state is current only while the source scope it was read from is
/// enabled and its baseline established. A disabled scope's state leaves the view in the transaction that disables it,
/// before any new generation runs; a scope enabled again shows nothing until a generation that read it in full reaches
/// Live, and then only what that read confirmed. Nothing is deleted or rewritten: the Snapshot rows, the ledger, the
/// evidence receipts and the reconciliation targets all stay, and only what the view presents as current changes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InitialSyncActiveStateTests(DatabaseFixture fixture)
{
    private const string BoardA = InitialSyncStack.Board;
    private const string BoardB = InitialSyncStack.OtherBoard;
    private const string BoardC = "1771812800";
    private const string ItemA1 = "1772099501";
    private const string ItemB1 = "1772099502";
    private const string ItemB2 = "1772099503";
    private const string ItemB3 = "1772099504";
    private const string ItemC1 = "1772099505";
    private const string ItemB4 = "1772099507";

    [Fact]
    public async Task A_disabled_scope_leaves_the_current_view_at_once_and_returns_only_after_a_new_baseline_reaches_live()
    {
        await using var stack = await LiveOnBothBoardsAsync();

        // 1: both boards contribute current state.
        Assert.Equal([(ItemA1, "Done"), (ItemB1, "Stuck"), (ItemB2, "Working on it")], await CurrentAsync(stack));
        Assert.Equal([ItemA1, ItemB1, ItemB2], await ActiveObjectsAsync(fixture, stack.Tenant));
        var history = await HistoryOfBAsync(stack);

        // 2 and 3: disabling B ends Live, and B's state leaves the view in that same transaction, before any run.
        await stack.ConfigureAsync(BoardB, enabled: false);
        Assert.Equal("RequiresRestart", (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.State);
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));
        Assert.Equal([ItemA1], await ActiveObjectsAsync(fixture, stack.Tenant));

        // 4 to 6: the new generation excludes B; once it is Live, B stays out of the view and A stays in it.
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal([BoardA], (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Where(scope => scope.Generation == 2).Select(scope => scope.ResourceId));
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));

        // 7: nothing B contributed was deleted or rewritten.
        Assert.Equal(history, await HistoryOfBAsync(stack));

        // 8: enabling B again shows none of its earlier state, not even once the next generation has read B's first item.
        stack.Account.SetStatus(ItemB2, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-5));
        await stack.ConfigureAsync(BoardB);
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));
        IReadOnlyList<(string, string?)>? duringRead = null;
        stack.Account.PageSize = 1;
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "next" && duringRead is null)
            {
                duringRead = await CurrentAsync(stack);
            }
        };

        // 9: once the generation that read B in full is Live, B is current again, as monday holds it now.
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal([(ItemA1, "Done")], duringRead);
        Assert.Equal([(ItemA1, "Done"), (ItemB1, "Stuck"), (ItemB2, "Done")], await CurrentAsync(stack));
        Assert.Equal([ItemA1, ItemB1, ItemB2], await ActiveObjectsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task What_a_disabled_scope_lost_or_cannot_confirm_meanwhile_never_returns_with_its_stale_state()
    {
        await using var stack = await LiveOnBothBoardsAsync(moreItems: true);
        Assert.Equal(5, (await CurrentAsync(stack)).Count);
        await stack.ConfigureAsync(BoardB, enabled: false);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // Meanwhile in monday, B2 is deleted, B3 archived and B4 given a value that cannot be confirmed; then B is enabled
        // again and read in full.
        stack.Account.Items.Remove(ItemB2);
        stack.Account.Items[ItemB3].State = "archived";
        stack.Account.SetStatus(ItemB4, " Stuck ", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-5));
        await stack.ConfigureAsync(BoardB);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // Only what the new baseline read and confirmed is current; B4 was read and held, not confirmed; and the Snapshot
        // still holds the rest as history.
        Assert.Equal([(ItemA1, "Done"), (ItemB1, "Stuck")], await CurrentAsync(stack));
        Assert.Equal([ItemA1, ItemB1], await ActiveObjectsAsync(fixture, stack.Tenant));
        Assert.Contains(await TargetsAsync(fixture, stack.Tenant), target => target.ObjectId == ItemB4 && target.State == "Held");
        Assert.Equal(
            [ItemA1, ItemB1, ItemB2, ItemB3, ItemB4],
            (await ProcessingHarness.SnapshotObjectsAsync(fixture, stack.Tenant)).Select(item => item.ObjectId));
    }

    [Fact]
    public async Task Realtime_changes_read_for_a_disabled_scope_stay_out_of_the_view_until_its_baseline_is_established_again()
    {
        await using var stack = await LiveOnBothBoardsAsync();
        await stack.ConfigureAsync(BoardB, enabled: false);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // Live again without B: a change on B arrives and reconciliation reads it. The Snapshot holds it; the view does not.
        stack.Account.SetStatus(ItemB1, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-5));
        await stack.DeliverAsync(ItemB1, "Done", BoardB);
        await stack.ReconcileAsync();
        Assert.Contains(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant), field => field.ObjectId == ItemB1 && field.Value == "Done");
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));

        // Enabled again, B still shows nothing until the generation that reads it reaches Live.
        await stack.ConfigureAsync(BoardB);
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal([(ItemA1, "Done"), (ItemB1, "Done"), (ItemB2, "Working on it")], await CurrentAsync(stack));
    }

    [Fact]
    public async Task State_from_a_board_that_is_no_enabled_scope_is_never_current_for_a_connection_that_takes_part()
    {
        await using var stack = await LiveOnBothBoardsAsync();
        stack.Account.AddBoard(BoardC);
        stack.Account.AddItem(ItemC1, BoardC, "Stuck", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-5));

        // A change on board C, never configured, is read by reconciliation into the Snapshot, but it is no current work.
        await stack.DeliverAsync(ItemC1, "Stuck", BoardC);
        await stack.ReconcileAsync();
        Assert.Contains(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant), field => field.ObjectId == ItemC1);
        Assert.Equal([(ItemA1, "Done"), (ItemB1, "Stuck"), (ItemB2, "Working on it")], await CurrentAsync(stack));

        // Configured, C becomes current only through its own baseline: the state reconciliation read before does not count.
        await stack.ConfigureAsync(BoardC);
        Assert.DoesNotContain(await CurrentAsync(stack), field => field.ObjectId == ItemC1);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Contains((ItemC1, "Stuck"), await CurrentAsync(stack));
    }

    [Fact]
    public async Task A_connection_that_never_took_part_keeps_all_its_state_current()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        stack.Account.AddBoard(BoardA);
        stack.Account.AddItem(ItemA1, BoardA, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-5));

        await stack.DeliverAsync(ItemA1, "Done");
        await stack.ReconcileAsync();

        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(stack));
        Assert.Equal([ItemA1], await ActiveObjectsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task Another_tenants_scope_changes_never_hide_or_reveal_this_tenants_state()
    {
        await using var mine = await LiveOnBothBoardsAsync();
        await using var theirs = await LiveOnBothBoardsAsync();
        var current = await ActiveFieldsAsync(fixture, mine.Tenant);
        Assert.Equal(3, current.Count);

        // The other tenant disables and enables its own board B, of the same id: this tenant's view never changes.
        await theirs.ConfigureAsync(BoardB, enabled: false);
        Assert.Equal(current, await ActiveFieldsAsync(fixture, mine.Tenant));
        Assert.Equal([(ItemA1, "Done")], await CurrentAsync(theirs));
        await theirs.ConfigureAsync(BoardB);
        Assert.Equal(current, await ActiveFieldsAsync(fixture, mine.Tenant));

        // It can neither change this tenant's scope nor read this tenant's state through the view.
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, (await theirs.ConfigureAsAsync(theirs.Tenant, mine.Connection.Connection, BoardB, enabled: false)).Error);
        Assert.Equal(current, await ActiveFieldsAsync(fixture, mine.Tenant));
        Assert.All(await ActiveFieldsAsync(fixture, theirs.Tenant), field => Assert.Equal(theirs.Tenant.Value, field.Tenant));
        Assert.All(current, field => Assert.Equal(mine.Tenant.Value, field.Tenant));
    }

    private async Task<InitialSyncStack> LiveOnBothBoardsAsync(bool moreItems = false)
    {
        var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(BoardA);
        stack.Account.AddBoard(BoardB);
        stack.Account.AddItem(ItemA1, BoardA, "Done", at);
        stack.Account.AddItem(ItemB1, BoardB, "Stuck", at);
        stack.Account.AddItem(ItemB2, BoardB, "Working on it", at);
        if (moreItems)
        {
            stack.Account.AddItem(ItemB3, BoardB, "Done", at);
            stack.Account.AddItem(ItemB4, BoardB, "Working on it", at);
        }

        await stack.ConfigureAsync(BoardA);
        await stack.ConfigureAsync(BoardB);
        await stack.SyncToLiveAsync();
        return stack;
    }

    private async Task<IReadOnlyList<(string ObjectId, string? Value)>> CurrentAsync(InitialSyncStack stack) =>
        (await ActiveFieldsAsync(fixture, stack.Tenant)).Select(field => (field.ObjectId, field.Value)).ToList();

    // Everything board B contributed, as history: its Snapshot rows, its targets, its ledger entries and every receipt.
    private async Task<string> HistoryOfBAsync(InitialSyncStack stack)
    {
        string[] items = [ItemB1, ItemB2];
        var fields = (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant))
            .Where(field => items.Contains(field.ObjectId))
            .Select(field => $"{field.ObjectId}/{field.Field}={field.Value}@{field.LastEventId}#{field.FieldVersion}");
        var targets = (await TargetsAsync(fixture, stack.Tenant))
            .Where(target => items.Contains(target.ObjectId))
            .Select(target => $"{target.ObjectId}/{target.Field}:{target.SourceScope}:{target.State}:{target.ResolvedByEventId}");
        var ledger = (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant))
            .Where(entry => entry.ObjectId is { } id && items.Contains(id))
            .Select(entry => $"{entry.EventId}:{entry.Outcome}");
        var receipts = (await ReceiptsAsync(fixture, stack.Tenant))
            .Where(receipt => receipt.Payload is { } payload && items.Any(payload.Contains))
            .Select(receipt => $"{receipt.ReceiptId}:{receipt.SchemaVersion}");
        var history = string.Join(" | ", fields.Concat(targets).Concat(ledger).Concat(receipts));
        Assert.Contains($"{ItemB1}/", history, StringComparison.Ordinal);
        Assert.Contains("monday.api-read.v1", history, StringComparison.Ordinal);
        return history;
    }
}
