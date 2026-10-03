using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using static WorkContinuity.Integration.Tests.InitialSync.InitialSyncData;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F's configuration lifecycle (owner review): a connection takes part in initial sync from the moment its trusted
/// configuration first enables a scope, and from then on no realtime receipt is decided until a generation's baseline is
/// established; and every change to its enabled scope set raises the scope-set version and, in the same locked
/// transaction, makes a Live, running or failed generation restart, so the baseline always represents the configured set,
/// nothing held is lost, and no stale generation becomes Live.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InitialSyncConfigurationTests(DatabaseFixture fixture)
{
    private const string Board = InitialSyncStack.Board;
    private const string OtherBoard = InitialSyncStack.OtherBoard;
    private const string ItemA = "1772099401";
    private const string ItemB = "1772099402";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_configured_connection_holds_realtime_receipts_before_its_first_generation_and_replays_them_once_live()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        var (earlier, _) = await stack.DeliverAsync(ItemA, "Working on it");

        await stack.ConfigureAsync(Board);

        // Configuring the first enabled scope makes the connection take part: NotStarted, with the exact hold boundary.
        var configured = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("NotStarted", 0L, 1L, (long?)null, (string?)null), (configured.State, configured.Generation, configured.ScopeVersion, configured.GenerationScopeVersion, configured.ProviderSubject));
        Assert.Equal(configured.BufferStartFence, configured.ReplayFloor);
        Assert.True((await ReceiptAsync(fixture, earlier)).Sequence <= configured.ReplayFloor);

        // D3F-12: stored and acknowledged, never decided, and its message completes every time it is delivered.
        var ledger = (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count;
        var (held, settlement) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal(TransportSettlementKind.Complete, settlement);
        for (var redelivery = 0; redelivery < 3; redelivery++)
        {
            Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(held));
        }

        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);
        Assert.Equal(ledger, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // A run with no started generation leaves it held: starting is the host's decision, and waiting for it is safe.
        Assert.Equal(InitialSyncState.NotStarted, (await stack.RunOnceAsync()).State);
        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);
        Assert.Empty(stack.Account.Answered);

        // Once the first generation is Live, the held receipt was replayed and realtime receipts are processed again.
        await stack.StartedAsync();
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == held);
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);
        var live = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal((configured.ReplayFloor, 1L, 1L, (long?)1L), (live.ReplayFloor, live.Generation, live.ScopeVersion, live.GenerationScopeVersion));
        var (afterLive, liveSettlement) = await stack.DeliverAsync(ItemA, "Done");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (liveSettlement, (await ReceiptAsync(fixture, afterLive)).State));
    }

    [Fact]
    public async Task Enabled_scopes_without_a_sync_row_still_hold_and_the_first_generation_replays_everything_unresolved()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        var (legacy, _) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal("Processed", (await ReceiptAsync(fixture, legacy)).State);

        // A scope written outside the configuration service: trusted configuration all the same, but no sync row.
        await InsertScopeDirectlyAsync(fixture, stack.Tenant, stack.Connection.Connection, Board);
        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));

        var (held, settlement) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal((TransportSettlementKind.Complete, "Received"), (settlement, (await ReceiptAsync(fixture, held)).State));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // No hold boundary was recorded, so the first generation replays every unresolved receipt; decided ones stay as they are.
        await stack.StartedAsync();
        Assert.Equal(0L, (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.ReplayFloor);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == legacy);
    }

    [Fact]
    public async Task A_receipt_being_stored_while_the_first_scope_is_configured_is_waited_for_and_falls_before_the_hold_boundary()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10));

        // Ingress caught mid-write while the first scope is configured: the configuration waits for it, under the connection's lock.
        var inFlight = Guid.CreateVersion7();
        var (runtime, transaction) = await BeginIngressAsync(fixture, stack.Connection, inFlight, ItemA, "Stuck");
        await using (runtime)
        {
            var configuring = stack.ConfigureAsAsync(stack.Tenant, stack.Connection.Connection, Board);
            await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
            Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, runtime.ProcessID, configuring));
            Assert.False(configuring.IsCompleted);
            Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));
            await transaction.CommitAsync(Token);
            Assert.True((await configuring).IsSuccess);
        }

        // Stored under the earlier configuration, it is exactly the boundary and is decided as before; the next one is held.
        var configured = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(((await ReceiptAsync(fixture, inFlight)).Sequence, "NotStarted"), (configured.ReplayFloor, configured.State));
        Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(inFlight));
        Assert.Equal("Processed", (await ReceiptAsync(fixture, inFlight)).State);
        var (held, _) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);
    }

    [Fact]
    public async Task A_receipt_decided_while_the_first_scope_is_configured_is_decided_by_the_new_row_and_never_stranded_below_its_floor()
    {
        var between = new BeforeScopeRead();
        await using var stack = await InitialSyncStack.StartAsync(
            fixture,
            more: services => services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(between)));
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10));
        var receipt = await stack.PostAsync(ItemA, "Stuck");

        // Its decision finds no sync row to lock, and the first configuration commits before it reads the configuration.
        between.Arm(async () =>
        {
            Task<Result<ConnectorSourceScope>> configuring;
            using (ExecutionContext.SuppressFlow())
            {
                configuring = stack.ConfigureAsAsync(stack.Tenant, stack.Connection.Connection, Board);
            }

            Assert.True((await configuring).IsSuccess);
        });
        Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(receipt));
        Assert.False(between.IsArmed);

        // Stored before the configuration, it lies at or below the new row's floor: decided as before, never held where no
        // generation would replay it.
        var configured = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal("NotStarted", configured.State);
        Assert.True((await ReceiptAsync(fixture, receipt)).Sequence <= configured.ReplayFloor);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, receipt)).State);
    }

    [Fact]
    public async Task Disabling_the_last_scope_written_without_a_sync_row_keeps_what_it_held_for_the_next_generation()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        await InsertScopeDirectlyAsync(fixture, stack.Tenant, stack.Connection.Connection, Board);
        var (held, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);

        // Disabling it through the service changes the enabled set all the same: the connection keeps taking part, from floor 0.
        await stack.ConfigureAsync(Board, enabled: false);
        var configured = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("NotStarted", 0L), (configured.State, configured.ReplayFloor));

        // Its first generation reads nothing, replays what was held and goes Live.
        await stack.StartedAsync();
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Empty(stack.Account.Answered);
    }

    [Fact]
    public async Task A_connection_without_scopes_or_with_only_disabled_ones_keeps_processing_as_before()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);

        // No configured scope: the pre-3F behaviour, for unrelated Gate 3C use.
        var (bare, bareSettlement) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (bareSettlement, (await ReceiptAsync(fixture, bare)).State));

        // Only disabled scopes: no baseline is required, so nothing takes part and nothing is held.
        await stack.ConfigureAsync(Board, enabled: false);
        await stack.ConfigureAsync(OtherBoard, enabled: false);
        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));
        var (disabled, disabledSettlement) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (disabledSettlement, (await ReceiptAsync(fixture, disabled)).State));
        Assert.Equal(InitialSyncErrors.NoScopes, (await stack.StartSyncAsync()).Error);
    }

    [Fact]
    public async Task Another_tenants_scope_never_gates_this_connection()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var other = await WebhookData.NewMondayConnectionAsync(fixture, Token);

        // The other tenant configures its own connection, then tries this one by the service and directly.
        Assert.True((await stack.ConfigureAsAsync(other.Tenant, other.Connection, Board)).IsSuccess);
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, (await stack.ConfigureAsAsync(other.Tenant, stack.Connection.Connection, Board)).Error);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => InsertScopeDirectlyAsync(fixture, other.Tenant, stack.Connection.Connection, Board));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, direct.SqlState);

        // This connection takes no part: no sync row, and its receipts are processed as before.
        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.NotNull(await SyncAsync(fixture, other.Tenant, other.Connection));
        var (receipt, settlement) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (settlement, (await ReceiptAsync(fixture, receipt)).State));
    }

    [Fact]
    public async Task Another_connections_scope_in_the_same_tenant_never_gates_this_connection()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);

        // The same tenant's second monday connection is configured; this one is not.
        var grant = ConnectorAuthorizationData.Grant(stack.Connection.User, account: "9002");
        await using (var scoped = ConnectorAuthorizationData.Store(fixture, stack.Tenant))
        {
            var activated = await scoped.Store.ActivateAsync(grant, Token);
            Assert.True(activated.IsSuccess && activated.Value, activated.IsSuccess ? "not activated" : activated.Error.Code);
        }

        Assert.True((await stack.ConfigureAsAsync(stack.Tenant, grant.ConnectionId, Board)).IsSuccess);
        Assert.Equal("NotStarted", (await SyncAsync(fixture, stack.Tenant, grant.ConnectionId))!.State);
        Assert.Null(await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection));

        var (receipt, settlement) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal((TransportSettlementKind.Complete, "Processed"), (settlement, (await ReceiptAsync(fixture, receipt)).State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_or_enabling_a_board_while_live_invalidates_live_atomically_and_the_next_run_restarts_over_the_new_set(bool previouslyDisabled)
    {
        await using var stack = await LiveOnBoardAsync();
        stack.Account.AddBoard(OtherBoard);
        stack.Account.AddItem(ItemB, OtherBoard, "Stuck", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10));
        if (previouslyDisabled)
        {
            await stack.ConfigureAsync(OtherBoard, enabled: false);
            Assert.Equal(("Live", 1L), await StateAsync(stack));
        }

        var before = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        await stack.ConfigureAsync(OtherBoard);

        // Atomically: the scope set's version rose, and Live under the old baseline is gone.
        var invalidated = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("RequiresRestart", 2L, (long?)1L, (string?)"initial_sync.scope_set.changed"), (invalidated.State, invalidated.ScopeVersion, invalidated.GenerationScopeVersion, invalidated.LastOutcome));
        Assert.Null(invalidated.ClaimId);
        Assert.Null(invalidated.LiveAtUtc);
        Assert.Equal(before.ReplayFloor, invalidated.ReplayFloor);

        // From now, realtime receipts are held for the next generation.
        var (held, settlement) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal((TransportSettlementKind.Complete, "Received"), (settlement, (await ReceiptAsync(fixture, held)).State));

        // The next run restarts by itself, with no new webhook, over exactly the enabled scopes now configured.
        var run = await stack.RunOnceAsync();
        Assert.Equal((InitialSyncState.Live, 2L), (run.State, run.Generation));
        var restarted = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal((2L, (long?)2L, before.ReplayFloor), (restarted.ScopeVersion, restarted.GenerationScopeVersion, restarted.ReplayFloor));
        var scopes = await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection);
        Assert.Equal([(1L, Board, "Completed"), (2L, Board, "Completed"), (2L, OtherBoard, "Completed")], scopes.Select(scope => (scope.Generation, scope.ResourceId, scope.State)));
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Contains(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant), field => field.ObjectId == ItemB);
    }

    [Fact]
    public async Task Disabling_an_enabled_board_while_live_invalidates_live_and_the_next_baseline_excludes_it()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddBoard(OtherBoard);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, OtherBoard, "Stuck", at);
        await stack.ConfigureAsync(Board);
        await stack.ConfigureAsync(OtherBoard);
        await stack.SyncToLiveAsync();
        Assert.Equal(2L, (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.ScopeVersion);

        await stack.ConfigureAsync(OtherBoard, enabled: false);

        Assert.Equal(("RequiresRestart", 3L), await StateVersionAsync(stack));
        var (held, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        var generation = (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Where(scope => scope.Generation == 2).Select(scope => scope.ResourceId);
        Assert.Equal([Board], generation);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
    }

    [Fact]
    public async Task Disabling_every_scope_starts_an_empty_generation_that_replays_what_was_held_and_goes_live()
    {
        await using var stack = await LiveOnBoardAsync();

        await stack.ConfigureAsync(Board, enabled: false);
        Assert.Equal(("RequiresRestart", 2L), await StateVersionAsync(stack));
        var (held, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Received", (await ReceiptAsync(fixture, held)).State);

        // The new baseline is the empty set: nothing is read, what was held is replayed, and realtime resumes.
        var answered = stack.Account.Answered.Count;
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal(answered, stack.Account.Answered.Count);
        Assert.DoesNotContain(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection), scope => scope.Generation == 2);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        var (live, _) = await stack.DeliverAsync(ItemA, "Done");
        Assert.Equal("Processed", (await ReceiptAsync(fixture, live)).State);
    }

    [Fact]
    public async Task A_configuration_call_that_changes_no_enabled_scope_leaves_live_and_its_version_alone()
    {
        await using var stack = await LiveOnBoardAsync();

        await stack.ConfigureAsync(Board);
        await stack.ConfigureAsync(OtherBoard, enabled: false);
        await stack.ConfigureAsync(OtherBoard, enabled: false);

        Assert.Equal(("Live", 1L), await StateVersionAsync(stack));
        var (receipt, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Processed", (await ReceiptAsync(fixture, receipt)).State);
    }

    [Fact]
    public async Task A_receipt_being_stored_during_a_scope_change_is_waited_for_and_held_for_the_new_generation()
    {
        await using var stack = await LiveOnBoardAsync();
        stack.Account.AddBoard(OtherBoard);

        // Ingress caught mid-write while the scope set changes: the change waits for it, under the connection's lock.
        var inFlight = Guid.CreateVersion7();
        var (runtime, transaction) = await BeginIngressAsync(fixture, stack.Connection, inFlight, ItemA, "Stuck");
        await using (runtime)
        {
            var changing = stack.ConfigureAsAsync(stack.Tenant, stack.Connection.Connection, OtherBoard);
            await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
            Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, runtime.ProcessID, changing));
            Assert.False(changing.IsCompleted);
            Assert.Equal(("Live", 1L), await StateVersionAsync(stack));
            await transaction.CommitAsync(Token);
            Assert.True((await changing).IsSuccess);
        }

        // Committed before the change: never lost. Its delivery now is held, and the new generation replays it.
        Assert.Equal(("RequiresRestart", 2L), await StateVersionAsync(stack));
        Assert.Equal(TransportSettlementKind.Complete, await stack.ConsumeAsync(inFlight));
        Assert.Equal("Received", (await ReceiptAsync(fixture, inFlight)).State);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, inFlight)).State);
    }

    [Fact]
    public async Task A_receipt_being_decided_when_the_scope_set_changes_is_waited_for_and_decided_under_the_old_generation()
    {
        var decision = new BeforeReceiptLock();
        await using var stack = await LiveOnBoardAsync(
            services => services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(decision)));
        stack.Account.AddBoard(OtherBoard);
        var receipt = await stack.PostAsync(ItemA, "Stuck");

        // Its decision holds the sync row's share lock when the scope set changes: the change waits for it.
        decision.Arm();
        var consuming = stack.ConsumeAsync(receipt);
        var deciding = await decision.Paused;
        var changing = stack.ConfigureAsAsync(stack.Tenant, stack.Connection.Connection, OtherBoard);
        await using (var monitor = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, deciding, changing));
        }

        Assert.False(changing.IsCompleted);
        Assert.Equal(("Live", 1L), await StateVersionAsync(stack));
        decision.Release();
        Assert.Equal(TransportSettlementKind.Complete, await consuming);
        Assert.True((await changing).IsSuccess);

        // Decided under the old Live generation, before the boundary; the next generation does not decide it again.
        Assert.Equal("Processed", (await ReceiptAsync(fixture, receipt)).State);
        Assert.Equal(("RequiresRestart", 2L), await StateVersionAsync(stack));
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == receipt);
    }

    [Fact]
    public async Task A_stale_generation_never_returns_to_live_after_the_scope_set_changes()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddBoard(OtherBoard);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, OtherBoard, "Stuck", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // Generation 1 stalls in its page read; meanwhile the scope set changes.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Account.AfterAnswer = async (operation, before, _) =>
        {
            if (operation == "page" && before == 0)
            {
                stalled.SetResult();
                await release.Task;
            }
        };
        var stale = stack.RunSyncAsync();
        await stalled.Task;
        await stack.ConfigureAsync(OtherBoard);

        // At once, before the stale worker does anything more: it no longer holds a generation that can become Live.
        Assert.Equal(("RequiresRestart", 2L), await StateVersionAsync(stack));
        release.SetResult();
        Assert.Equal(InitialSyncErrors.ClaimLost, (await stale).Error);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("RequiresRestart", 2L), await StateVersionAsync(stack));

        // Generation 1's frozen scope set is untouched; generation 2 captures version 2 and reaches Live.
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal(
            [(1L, Board, "Pending"), (2L, Board, "Completed"), (2L, OtherBoard, "Completed")],
            (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Select(scope => (scope.Generation, scope.ResourceId, scope.State)));
        Assert.Equal((long?)2L, (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.GenerationScopeVersion);
    }

    [Fact]
    public async Task A_failed_generation_restarted_by_a_scope_change_keeps_the_earliest_replay_floor()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddBoard(OtherBoard).State = "archived";
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.ConfigureAsync(OtherBoard);
        await stack.StartedAsync();
        var (heldInFirst, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        var failed = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;

        // Disabling the archived board is the fix: the failed generation must restart, and the next run does so by itself.
        await stack.ConfigureAsync(OtherBoard, enabled: false);
        Assert.Equal(("RequiresRestart", 3L), await StateVersionAsync(stack));
        var (heldAfter, _) = await stack.DeliverAsync(ItemA, "Working on it");

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        var live = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal((failed.ReplayFloor, 2L), (live.ReplayFloor, live.Generation));
        Assert.Equal("Processed", (await ReceiptAsync(fixture, heldInFirst)).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, heldAfter)).State);
    }

    [Fact]
    public async Task Another_tenants_scope_change_cannot_invalidate_this_connection()
    {
        await using var stack = await LiveOnBoardAsync();
        var other = await WebhookData.NewMondayConnectionAsync(fixture, Token);

        // The other tenant changes its own connection's scopes freely; this connection it can neither find nor name.
        Assert.True((await stack.ConfigureAsAsync(other.Tenant, other.Connection, OtherBoard)).IsSuccess);
        Assert.Equal(InitialSyncErrors.ConnectionNotFound, (await stack.ConfigureAsAsync(other.Tenant, stack.Connection.Connection, OtherBoard)).Error);
        var mismatched = await stack.InTenantAsync(other.Tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(stack.Tenant, stack.Connection.Connection, MondayWebhooks.SourceSystem, OtherBoard, enabled: true), Token));
        Assert.Equal(InitialSyncErrors.TenantMismatch, mismatched.Error);

        Assert.Equal(("Live", 1L), await StateVersionAsync(stack));
        var (receipt, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal("Processed", (await ReceiptAsync(fixture, receipt)).State);
    }

    private async Task<InitialSyncStack> LiveOnBoardAsync(Action<IServiceCollection>? more = null)
    {
        var stack = await InitialSyncStack.StartAsync(fixture, more: more);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10));
        await stack.ConfigureAsync(Board);
        await stack.SyncToLiveAsync();
        Assert.Equal(("Live", 1L), await StateVersionAsync(stack));
        return stack;
    }

    private async Task<(string State, long Generation)> StateAsync(InitialSyncStack stack)
    {
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        return (sync.State, sync.Generation);
    }

    private async Task<(string State, long ScopeVersion)> StateVersionAsync(InitialSyncStack stack)
    {
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        return (sync.State, sync.ScopeVersion);
    }

    // Once armed, pauses the next decision just before it locks its receipt, after the gate's share lock on the sync row,
    // and gives the backend that holds it.
    private sealed class BeforeReceiptLock : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<int> _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task<int> Paused => _paused.Task;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM public.raw_receipts WHERE receipt_id", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _paused.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
                await _released.Task;
            }

            return result;
        }
    }

    // Once armed, runs its work just before the next command that reads source scopes: in the gate, between its lock that
    // found no sync row and its reading of the configuration.
    private sealed class BeforeScopeRead : DbCommandInterceptor
    {
        private Func<Task>? _armed;

        public bool IsArmed => Volatile.Read(ref _armed) is not null;

        public void Arm(Func<Task> work) => Volatile.Write(ref _armed, work);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("connector_source_scopes", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, null) is { } work)
            {
                await work();
            }

            return result;
        }
    }
}
