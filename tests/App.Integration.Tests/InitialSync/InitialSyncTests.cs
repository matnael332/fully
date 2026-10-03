using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using static WorkContinuity.Integration.Tests.InitialSync.InitialSyncData;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F end to end against the real local PostgreSQL: only explicitly configured, enabled boards are read; each
/// supported status becomes a confirmed Snapshot fact through the unchanged processing core, with the 3E evidence naming
/// the initial-sync read, and a known, resolved 3E target; anything that does not bind is held explicitly; every enabled
/// board must complete before the connection is Live; pagination survives monday's cursors without ever storing one; and
/// nothing crosses a tenant or reaches a log.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InitialSyncTests(DatabaseFixture fixture)
{
    private const string Board = InitialSyncStack.Board;
    private const string OtherBoard = InitialSyncStack.OtherBoard;
    private const string ItemA = "1772099001";
    private const string ItemB = "1772099002";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_configured_board_reaches_live_with_each_status_confirmed_from_the_api_and_known_to_reconciliation()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);

        Assert.Equal(1L, (await stack.StartedAsync()).Generation);
        var run = await stack.RunOnceAsync();

        Assert.Equal((InitialSyncState.Live, 1L, 1, 0, 1), (run.State, run.Generation, run.FieldsApplied, run.FieldsHeld, run.Count(SyncScopeOutcome.Completed)));

        // D3F-6: the Snapshot holds the API value, ordered by the status value's own time, from one confirmed event.
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal((ItemA, "status", "TaskStatus", "Done", (DateTimeOffset?)at), (field.ObjectId, field.Field, field.DataType, field.Value, field.SourceUpdatedAtUtc));
        Assert.Null(field.SourceRevision);
        Assert.Null(field.SourceEventId);
        var entry = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Equal(("AcceptedForProjection", field.LastEventId), (entry.Outcome, entry.EventId));

        // D3F-7: its receipt is the 3E evidence of exactly the values consumed, naming the initial-sync read, with no
        // outbox message: no board title, item name or cursor.
        var evidence = Assert.Single(await ReceiptsAsync(fixture, stack.Tenant));
        Assert.Equal((MondayStatusReader.EvidenceSchemaVersion, "Processed", false, entry.ReceiptId), (evidence.SchemaVersion, evidence.ProcessingState, evidence.HasOutboxMessage, evidence.ReceiptId));
        Assert.Contains("\"operation\": \"" + MondayOperations.ReadSyncStatusPage.Name + "\"", evidence.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", evidence.Payload!, StringComparison.Ordinal);

        // D3F-8: the field is a known target, resolved by initial sync, scoped to its board.
        var target = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(
            (ItemA, "status", "TaskStatus", (string?)Board, "Resolved", 1L, (long?)1L, "Sync", "reconciliation.repaired", (Guid?)field.LastEventId),
            (target.ObjectId, target.Field, target.DataType, target.SourceScope, target.State, target.Generation, target.ResolvedGeneration, target.RequestKind, target.LastOutcome, target.ResolvedByEventId));
        Assert.Null(target.RequestedByEventId);
        Assert.NotNull(target.VerifiedAtUtc);

        // The watermark record: the generation, its authority, its fences, its completed scope and its Live transition.
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("Live", 1L, "58201774", (Guid?)null, (string?)null), (sync.State, sync.Generation, sync.ProviderSubject, sync.ClaimId, sync.LastOutcome));
        Assert.Equal(sync.BufferStartFence, sync.ReplayFloor);
        Assert.True(sync.ReplayFence >= sync.BufferStartFence && sync.CutoverFence >= sync.ReplayFence, $"{sync.BufferStartFence} {sync.ReplayFence} {sync.CutoverFence}");
        Assert.NotNull(sync.LiveAtUtc);
        var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal((1L, Board, "Completed", 1, 1, 1, (string?)"initial_sync.scope.completed"), (scope.Generation, scope.ResourceId, scope.State, scope.PagesRead, scope.ObjectsRead, scope.FieldsApplied, scope.LastOutcome));

        // Exactly the configured board: its validation and one page.
        Assert.Equal(["board", "page"], stack.Account.Answered);
        Assert.Equal([Board, Board], stack.Account.BoardsAsked);

        // Live is final for the generation: another run changes nothing and reads nothing.
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal(2, stack.Account.Answered.Count);
        Assert.Equal(InitialSyncErrors.AlreadyLive, (await stack.StartSyncAsync()).Error);
    }

    [Fact]
    public async Task Only_enabled_configured_boards_are_read_and_an_accessible_unconfigured_board_never_is()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        const string Disabled = "1771812701";
        foreach (var board in new[] { Board, OtherBoard, Disabled })
        {
            stack.Account.AddBoard(board);
        }

        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, OtherBoard, "Stuck", at);
        stack.Account.AddItem("1772099003", Disabled, "Stuck", at);
        await stack.ConfigureAsync(Board);
        await stack.ConfigureAsync(Disabled);
        await stack.ConfigureAsync(Disabled, enabled: false);

        // Another monday connection of the same tenant has its own configured board: it is that connection's scope only.
        var (_, sibling) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, stack.Tenant, Token);
        var siblingScope = await stack.InTenantAsync(stack.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(stack.Tenant, sibling.ConnectionId, MondayWebhooks.SourceSystem, OtherBoard, enabled: true), Token));
        Assert.True(siblingScope.IsSuccess);

        await stack.SyncToLiveAsync();

        // D3F-1: the unconfigured board, accessible to the same user, the disabled one and another connection's are never
        // asked for.
        Assert.Equal([Board, Board], stack.Account.BoardsAsked);
        Assert.Equal([ItemA], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId));
        Assert.Equal([Board], (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Select(scope => scope.ResourceId));
    }

    [Fact]
    public async Task A_connection_with_no_enabled_scope_starts_nothing_and_holds_nothing()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        stack.Account.AddBoard(Board);
        await stack.ConfigureAsync(Board, enabled: false);

        Assert.Equal(InitialSyncErrors.NoScopes, (await stack.StartSyncAsync()).Error);
        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal(InitialSyncState.NotStarted, (await stack.RunOnceAsync()).State);
        Assert.Empty(stack.Account.Answered);

        // NotStarted: realtime receipts are processed as before, held for no sync.
        var (receipt, settlement) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal(TransportSettlementKind.Complete, settlement);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, receipt)).State);
    }

    [Fact]
    public async Task Several_items_and_status_columns_each_become_their_own_confirmed_field_with_clear_unicode_and_bounded_labels()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        var longest = new string('L', Core.Events.EventText.MaxValueLength);
        stack.Account.AddBoard(Board, ("name", "name"), ("status", "status"), ("status_1", "status"), ("text0", "text"));
        stack.Account.AddItem(ItemA, Board, "Erledigt ✓ בוצע", at).Statuses["status_1"] = (longest, at.AddMinutes(1));
        stack.Account.AddItem(ItemB, Board, null, at).Statuses["status_1"] = ("Stuck", at);
        await stack.ConfigureAsync(Board);

        var run = await stack.SyncToLiveAsync();

        Assert.Equal((4, 0), (run.FieldsApplied, run.FieldsHeld));
        var fields = (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).ToDictionary(field => (field.ObjectId, field.Field));
        Assert.Equal(4, fields.Count);
        Assert.Equal("Erledigt ✓ בוצע", fields[(ItemA, "status")].Value);
        Assert.Equal(longest, fields[(ItemA, "status_1")].Value);
        Assert.Equal(at.AddMinutes(1), fields[(ItemA, "status_1")].SourceUpdatedAtUtc);

        // A cleared status is the source's state too: a confirmed empty value, with its own time.
        Assert.Null(fields[(ItemB, "status")].Value);
        Assert.Equal(at, fields[(ItemB, "status")].SourceUpdatedAtUtc);
        Assert.Equal("Stuck", fields[(ItemB, "status_1")].Value);

        // Each distinct column id is its own field and its own known target; the text column is never read.
        Assert.Equal(4, (await TargetsAsync(fixture, stack.Tenant)).Count(target => target.State == "Resolved" && target.RequestKind == "Sync"));
        Assert.DoesNotContain(fields.Keys, key => key.Field == "text0" || key.Field == "name");
    }

    [Fact]
    public async Task An_item_or_column_that_does_not_bind_is_held_explicitly_and_the_board_still_completes()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem("1772099010", Board, "Done", at).RawColumns["status"] = "{\"id\":\"status\",\"type\":\"status\"}";
        stack.Account.AddItem("1772099011", Board, "Done", at).RawColumns["status"] = "{\"id\":\"status\",\"type\":\"text\"}";
        stack.Account.AddItem("1772099012", Board, "Done\nDone", at);
        await stack.ConfigureAsync(Board);

        var run = await stack.SyncToLiveAsync();

        Assert.Equal((1, 3), (run.FieldsApplied, run.FieldsHeld));
        Assert.Equal([ItemA], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId));
        var targets = (await TargetsAsync(fixture, stack.Tenant)).ToDictionary(target => target.ObjectId);
        Assert.Equal(("Held", "reconciliation.field.unsupported"), (targets["1772099010"].State, targets["1772099010"].LastOutcome));
        Assert.Equal(("Held", "reconciliation.field.unsupported"), (targets["1772099011"].State, targets["1772099011"].LastOutcome));
        Assert.Equal(("Held", "reconciliation.value.invalid"), (targets["1772099012"].State, targets["1772099012"].LastOutcome));
        Assert.All(targets.Values, target => Assert.Equal(("Sync", (string?)Board), (target.RequestKind, target.SourceScope)));
    }

    [Fact]
    public async Task An_item_monday_answers_on_another_board_is_neither_applied_nor_made_known_in_this_board()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);

        // Listed on the configured board, while monday's own answer names another: the item moved under the scan.
        stack.Account.AddItem(ItemB, Board, "Stuck", at).AnsweredBoard = OtherBoard;
        await stack.ConfigureAsync(Board);

        var run = await stack.SyncToLiveAsync();

        // The 3E binding rule: nothing is remapped, and no target claims the item for a board monday says it is not on.
        Assert.Equal((1, 1), (run.FieldsApplied, run.FieldsHeld));
        Assert.Equal([ItemA], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId));
        Assert.Equal([ItemA], (await TargetsAsync(fixture, stack.Tenant)).Select(target => target.ObjectId));
    }

    [Fact]
    public async Task A_field_already_known_in_another_board_is_held_as_a_scope_conflict_never_overwritten()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, Board, "Stuck", at);

        // Before any sync, a webhook from another board made ItemB's status a known target of that board.
        var (_, settlement) = await stack.DeliverAsync(ItemB, "Working on it", OtherBoard);
        Assert.Equal(TransportSettlementKind.Complete, settlement);
        await stack.ConfigureAsync(Board);

        var run = await stack.SyncToLiveAsync();

        Assert.Equal((1, 1), (run.FieldsApplied, run.FieldsHeld));
        Assert.Equal([ItemA], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId));
        var known = Assert.Single(await TargetsAsync(fixture, stack.Tenant), target => target.ObjectId == ItemB);
        Assert.Equal(((string?)OtherBoard, "Held", 2L, "Sync", (string?)"reconciliation.scope.conflict"), (known.SourceScope, known.State, known.Generation, known.RequestKind, known.LastOutcome));
    }

    public static TheoryData<string, string> UnsyncableBoards => new()
    {
        { "archived", "initial_sync.scope.inactive" },
        { "deleted", "initial_sync.scope.inactive" },
        { "multi_level", "initial_sync.scope.unsupported" },
        { "document", "initial_sync.scope.unsupported" },
        { "no status column", "initial_sync.scope.no_supported_field" },
    };

    [Theory]
    [MemberData(nameof(UnsyncableBoards))]
    public async Task A_configured_board_that_cannot_be_synced_fails_the_generation_and_never_reaches_live(string kind, string code)
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        var board = stack.Account.AddBoard(Board);
        switch (kind)
        {
            case "archived" or "deleted":
                board.State = kind;
                break;
            case "multi_level":
                board.Hierarchy = kind;
                break;
            case "document":
                board.Type = kind;
                break;
            default:
                board.Columns.RemoveAll(column => column.Type == "status");
                break;
        }

        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var held = await stack.DeliverAsync(ItemA, "Done");

        var run = await stack.RunOnceAsync();

        // D3F-15: no partial Live; the board is held with its explicit outcome, never silently dropped.
        Assert.Equal(InitialSyncState.Failed, run.State);
        var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal(("Held", (string?)code), (scope.State, scope.LastOutcome));
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("Failed", (string?)code, (long?)null), (sync.State, sync.LastOutcome, sync.CutoverFence));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // The receipt held while the connection is not Live stays held, and a failed generation is never run again.
        Assert.Equal("Received", (await ReceiptAsync(fixture, held.ReceiptId)).State);
        var answered = stack.Account.Answered.Count;
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        Assert.Equal(answered, stack.Account.Answered.Count);
    }

    [Fact]
    public async Task A_board_missing_is_retried_up_to_the_bound_then_held_and_the_generation_fails()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture, options => options.MaxAttempts = 3);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var run = await stack.RunOnceAsync();
            var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
            Assert.Equal((attempt, (string?)"initial_sync.scope.missing"), (scope.Attempts, scope.LastOutcome));
            if (attempt < 3)
            {
                Assert.Equal((InitialSyncState.Reading, "Pending"), (run.State, scope.State));
                stack.Clock.UtcNow = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.NextAttemptAtUtc!.Value;
            }
            else
            {
                Assert.Equal((InitialSyncState.Failed, "Held"), (run.State, scope.State));
            }
        }
    }

    [Fact]
    public async Task A_board_that_completes_and_one_that_fails_never_make_the_connection_live()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddBoard(OtherBoard).State = "archived";
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.ConfigureAsync(OtherBoard);
        await stack.StartedAsync();

        var run = await stack.RunOnceAsync();

        Assert.Equal(InitialSyncState.Failed, run.State);
        Assert.Equal(
            [(Board, "Completed"), (OtherBoard, "Held")],
            (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Select(scope => (scope.ResourceId, scope.State)));
        Assert.Equal("Failed", (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.State);
    }

    [Fact]
    public async Task More_than_one_page_of_items_is_read_page_by_page_with_one_fact_each_and_no_cursor_kept()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        var items = Enumerable.Range(1, MondayOperations.MaxPageSize + 1).Select(index => (1772100000 + index).ToString(CultureInfo.InvariantCulture)).ToList();
        foreach (var item in items)
        {
            stack.Account.AddItem(item, Board, "Done", at);
        }

        await stack.ConfigureAsync(Board);

        // DoD 8: monday's cursor is never stored, not even while the scan is using it.
        var storedWhileScanning = false;
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "next")
            {
                storedWhileScanning |= await AnyRowMentionsAsync(stack.Tenant, "cursor-");
            }
        };

        var run = await stack.SyncToLiveAsync();

        Assert.False(storedWhileScanning);
        Assert.Equal(items.Count, run.FieldsApplied);
        Assert.Equal(["board", "page", "next"], stack.Account.Answered);
        Assert.Equal(items.Order(StringComparer.Ordinal), (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId).Order(StringComparer.Ordinal));
        var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal((2, items.Count, items.Count), (scope.PagesRead, scope.ObjectsRead, scope.FieldsApplied));

        // DoD 8: monday's cursor is never stored anywhere, and never a fence.
        var cursor = Assert.Single(stack.Account.IssuedCursors);
        Assert.False(await AnyRowMentionsAsync(stack.Tenant, cursor));
    }

    [Fact]
    public async Task An_item_answered_on_two_pages_is_applied_once()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 2;
        stack.Account.AddItem("1772099101", Board, "Done", at);
        stack.Account.AddItem("1772099102", Board, "Done", at);
        stack.Account.AddItem("1772099103", Board, "Done", at);

        // A new item listed ahead of the cursor's position shifts the listing, so the second page repeats an item.
        stack.Account.AfterAnswer = (operation, before, _) =>
        {
            if (operation == "page" && before == 0)
            {
                stack.Account.AddItem("1772099100", Board, "Stuck", at);
            }

            return Task.CompletedTask;
        };
        await stack.ConfigureAsync(Board);

        var run = await stack.SyncToLiveAsync();

        // 1772099102 was on both pages; the second read found the Snapshot already holding it: one fact, one event.
        Assert.Equal(["1772099101", "1772099102", "1772099103"], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId).Order(StringComparer.Ordinal));
        Assert.Equal(3, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        Assert.Equal(3, (await ReceiptsAsync(fixture, stack.Tenant)).Count);
        Assert.Equal(4, run.FieldsApplied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expired_cursor_restarts_the_board_from_its_first_page_and_converges(bool answersNull)
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 2;
        stack.Account.ExpiredCursorAnswersNull = answersNull;
        foreach (var item in new[] { "1772099101", "1772099102", "1772099103" })
        {
            stack.Account.AddItem(item, Board, "Done", at);
        }

        // The first scan's cursor expires before its next page is asked for, as a cursor does after 60 minutes.
        stack.Account.AfterAnswer = (operation, before, _) =>
        {
            if (operation == "page" && before == 0)
            {
                stack.Account.ExpireCursors();
            }

            return Task.CompletedTask;
        };
        await stack.ConfigureAsync(Board);

        await stack.SyncToLiveAsync();

        // DoD 9 and 30: the board was read again from its first page in the same run; reapplying changed nothing.
        Assert.Equal(["board", "page", "next", "page", "next"], stack.Account.Answered);
        Assert.Equal(3, (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Count);
        Assert.Equal(3, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal(("Completed", 2, 3), (scope.State, scope.PagesRead, scope.ObjectsRead));
    }

    [Fact]
    public async Task A_cursor_monday_keeps_refusing_waits_and_never_holds_the_scope()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture, options => options.MaxAttempts = 2);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 1;
        stack.Account.AddItem("1772099101", Board, "Done", at);
        stack.Account.AddItem("1772099102", Board, "Done", at);
        var refusing = true;
        stack.Account.Intercept = (operation, _, _) =>
            Task.FromResult(operation == "next" && refusing ? FakeMondayWorkspace.Error("InvalidArgumentException", 400) : null);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // Rejected again after the immediate restart: the scope waits on the backoff, as often as it takes, never held.
        for (var run = 0; run < 4; run++)
        {
            Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);
            var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
            Assert.Equal(("Pending", (string?)"initial_sync.provider.continuation_rejected"), (scope.State, scope.LastOutcome));
            stack.Clock.UtcNow = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.NextAttemptAtUtc!.Value;
        }

        refusing = false;
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal(2, (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Count);
    }

    [Fact]
    public async Task A_synced_field_is_a_known_target_a_sweep_rereads_and_repairs_while_a_new_item_is_never_discovered()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Working on it", at);
        await stack.ConfigureAsync(Board);
        await stack.SyncToLiveAsync();

        // After Live, monday changes the known item without a webhook, and a new item appears on the board. A Live
        // connection is never scanned again, so nothing discovers it.
        stack.Account.SetStatus(ItemA, "Done", at.AddMinutes(5));
        stack.Account.AddItem(ItemB, Board, "Stuck", at);
        Assert.Equal(InitialSyncErrors.AlreadyLive, (await stack.StartSyncAsync()).Error);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        Assert.Equal(1, await stack.SweepAsync());
        var reconciled = await stack.ReconcileAsync();

        Assert.Equal((1, 1), (reconciled.Claimed, reconciled.Count(ReconciliationOutcome.Repaired)));
        Assert.Equal([(ItemA, "Done")], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => (field.ObjectId, field.Value)));
        Assert.Equal([ItemA], FakeMondayWorkspace.RereadItemIds(stack.Monday));
        Assert.DoesNotContain(await TargetsAsync(fixture, stack.Tenant), target => target.ObjectId == ItemB);
    }

    [Fact]
    public async Task Scopes_are_configured_and_listed_only_on_the_trusted_tenants_own_connections()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var other = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await stack.ConfigureAsync(Board);

        // The other tenant can neither configure nor list a scope of this tenant's connection, nor this tenant theirs.
        var foreign = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(other.Tenant, stack.Connection.Connection, MondayWebhooks.SourceSystem, OtherBoard, enabled: true), Token));
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, foreign.Error);
        var listed = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ListAsync(other.Tenant, stack.Connection.Connection, Token));
        Assert.True(listed.IsSuccess && listed.Value.Count == 0);

        // A scope naming a tenant other than the trusted one is refused before anything is read.
        var mismatched = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(stack.Tenant, stack.Connection.Connection, MondayWebhooks.SourceSystem, OtherBoard, enabled: true), Token));
        Assert.Equal(InitialSyncErrors.TenantMismatch, mismatched.Error);

        // A scope is only ever of its own source: a connection does not serve another.
        var wrongSource = await stack.InTenantAsync(stack.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(stack.Tenant, stack.Connection.Connection, "synthetic", OtherBoard, enabled: true), Token));
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, wrongSource.Error);

        var own = await stack.InTenantAsync(stack.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ListAsync(stack.Tenant, stack.Connection.Connection, Token));
        Assert.Equal([(Board, true)], own.Value.Select(scope => (scope.ResourceId, scope.Enabled)));
    }

    [Fact]
    public async Task A_run_for_another_tenant_cannot_start_claim_or_advance_the_sync()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var other = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        stack.Account.AddBoard(Board);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // The run names this tenant while the trusted context is another: refused before anything is touched.
        var mismatched = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IInitialSync>().RunAsync(stack.Run, Token));
        Assert.Equal(InitialSyncErrors.TenantMismatch, mismatched.Error);

        // The other tenant naming this tenant's connection as its own finds nothing to start or run.
        var foreignRun = new InitialSyncRun(other.Tenant, MondayWebhooks.SourceSystem, stack.Connection.Connection);
        var started = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IInitialSync>().StartAsync(foreignRun, Token));
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, started.Error);
        var ran = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IInitialSync>().RunAsync(foreignRun, Token));
        Assert.Equal(InitialSyncState.NotStarted, ran.Value.State);

        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("Buffering", (Guid?)null), (sync.State, sync.ClaimId));
        Assert.Empty(stack.Account.Answered);
    }

    [Fact]
    public async Task Logs_name_ids_counts_and_codes_only_never_a_label_a_cursor_a_title_or_a_token()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 1;
        stack.Account.AddItem(ItemA, Board, "Label SENTINEL", at);
        stack.Account.AddItem(ItemB, Board, "Other SENTINEL", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        await stack.DeliverAsync(ItemA, "Webhook SENTINEL");

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        var records = stack.Processing.Logs.Entries.Select(entry => entry.Text).ToList();
        Assert.Contains(records, record => record.Contains("Live", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "SENTINEL", WorkContinuity.Integration.Tests.Reconciliation.FixedMondayCredentials.AccessToken, "cursor-", FakeMondayWorkspace.BoardTitle, FakeMondayWorkspace.ItemName })
        {
            Assert.DoesNotContain(records, record => record.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private async Task<bool> AnyRowMentionsAsync(TenantId tenant, string text)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM connection_syncs s WHERE s.tenant_id = @tenant AND row_to_json(s)::text LIKE '%' || @text || '%')
                OR EXISTS (SELECT 1 FROM connection_sync_scopes s WHERE s.tenant_id = @tenant AND row_to_json(s)::text LIKE '%' || @text || '%')
                OR EXISTS (SELECT 1 FROM reconciliation_targets t WHERE t.tenant_id = @tenant AND row_to_json(t)::text LIKE '%' || @text || '%')
                OR EXISTS (SELECT 1 FROM raw_receipts r WHERE r.tenant_id = @tenant AND row_to_json(r)::text LIKE '%' || @text || '%')
                OR EXISTS (SELECT 1 FROM event_ledger l WHERE l.tenant_id = @tenant AND row_to_json(l)::text LIKE '%' || @text || '%');
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("text", text);
        return (bool)(await command.ExecuteScalarAsync(Token))!;
    }
}
