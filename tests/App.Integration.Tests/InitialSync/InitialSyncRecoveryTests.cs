using Npgsql;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Results;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Reconciliation;
using static WorkContinuity.Integration.Tests.InitialSync.InitialSyncData;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F's failure and recovery behaviour against the real local PostgreSQL: transient failures and missing access wait
/// and resume, never held; a reauthorization makes the generation stale and the next run starts a new one under the new
/// authority, while a token renewal changes nothing; revoked access sends nothing; a database failure mid-page leaves no
/// partial write and the next run converges; a stale or competing worker records nothing; and a failed generation's held
/// receipts are replayed by the next.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class InitialSyncRecoveryTests(DatabaseFixture fixture)
{
    private const string Board = InitialSyncStack.Board;
    private const string ItemA = "1772099301";
    private const string ItemB = "1772099302";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_transient_outage_on_page_two_waits_and_the_next_run_completes_without_duplicate_facts()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture, options => options.MaxAttempts = 1);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 1;
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, Board, "Stuck", at);
        var outage = true;
        stack.Account.Intercept = (operation, _, _) =>
            Task.FromResult(operation == "next" && outage ? FakeMondayWorkspace.Error("Internal Server Error", 500) : null);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // Retried past the bound of one attempt: a transient failure is never held.
        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);
            var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
            Assert.Equal(("Pending", (string?)"initial_sync.provider.unavailable", run + 1), (scope.State, scope.LastOutcome, scope.Attempts));
            var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
            Assert.Null(sync.ClaimId);

            // Not due before its backoff: a run in between reads nothing.
            var answered = stack.Account.Answered.Count;
            Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);
            Assert.Equal(answered, stack.Account.Answered.Count);
            stack.Clock.UtcNow = sync.NextAttemptAtUtc!.Value;
        }

        outage = false;
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);

        // Page one was applied four times over; the Snapshot and the ledger hold one fact per item.
        Assert.Equal([ItemA, ItemB], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        var done = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal(("Completed", 0), (done.State, done.Attempts));
    }

    [Fact]
    public async Task A_credential_that_cannot_be_obtained_for_a_while_sends_nothing_waits_and_recovers()
    {
        SwitchableMondayCredentials? credentials = null;
        await using var stack = await InitialSyncStack.StartAsync(
            fixture,
            options => options.MaxAttempts = 1,
            credentials: (connection, clock) => credentials = new SwitchableMondayCredentials(connection.Tenant, connection.Connection, clock) { Unobtainable = true });
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        for (var run = 0; run < 2; run++)
        {
            Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);
            Assert.Equal("initial_sync.credential.unavailable", Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).LastOutcome);
            stack.Clock.UtcNow = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.NextAttemptAtUtc!.Value;
        }

        Assert.Empty(stack.Account.Answered);
        credentials!.Unobtainable = false;
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
    }

    [Fact]
    public async Task A_reauthorization_during_the_scan_restarts_the_generation_under_the_new_authority_and_keeps_its_held_receipts()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Working on it", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var first = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        var (held, _) = await stack.DeliverAsync(ItemA, "Done");

        // The page is read under the authority the generation started with; the user authorizes the connection again
        // before it is applied, and monday's state moves on.
        stack.Account.AfterAnswer = async (operation, before, _) =>
        {
            if (operation == "page" && before == 0)
            {
                await ReauthorizeAsync(fixture, stack.Connection);
                stack.Account.SetStatus(ItemA, "Done", at.AddMinutes(1));
            }
        };

        // D3F-5: the stale read is discarded, never applied, and the generation must restart.
        Assert.Equal(InitialSyncState.RequiresRestart, (await stack.RunOnceAsync()).State);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        var stale = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("RequiresRestart", (string?)"initial_sync.authority.changed", (Guid?)null), (stale.State, stale.LastOutcome, stale.ClaimId));

        // The next run starts generation 2 under the current authority, keeping the replay floor, and reaches Live.
        var run = await stack.RunOnceAsync();
        Assert.Equal((InitialSyncState.Live, 2L), (run.State, run.Generation));
        var live = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.True(live.AuthorizedAtUtc > first.AuthorizedAtUtc);
        Assert.Equal(first.ReplayFloor, live.ReplayFloor);
        Assert.True(live.BufferStartFence >= first.BufferStartFence);
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);

        // DoD 22: what generation 1 held is replayed by generation 2.
        Assert.Equal("Processed", (await ReceiptAsync(fixture, held)).State);
        Assert.Equal(
            [(1L, "Pending"), (2L, "Completed")],
            (await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).Select(scope => (scope.Generation, scope.State)));
    }

    [Fact]
    public async Task A_token_renewal_during_the_scan_does_not_restart_the_generation()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var before = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;

        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                await using var scoped = ConnectorAuthorizationData.Store(fixture, stack.Tenant);
                var current = (await scoped.Store.ReadAsync(stack.Connection.Connection, Token)).Value!;
                var claim = Guid.CreateVersion7();
                Assert.True((await scoped.Store.ClaimRefreshAsync(stack.Connection.Connection, current.Generation, claim, Token)).Value);
                Assert.True((await scoped.Store.CompleteRefreshAsync(stack.Connection.Connection, current.Generation, claim, DateTimeOffset.UtcNow.AddHours(2), ConnectorAuthorizationData.Scopes, Token)).Value);
            }
        };

        var run = await stack.RunOnceAsync();

        Assert.Equal((InitialSyncState.Live, 1L), (run.State, run.Generation));
        Assert.Equal(before.AuthorizedAtUtc, (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.AuthorizedAtUtc);
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);
    }

    public static TheoryData<string, string, string> RevokedAccess => new()
    {
        { "UPDATE connections SET status = 'Disabled' WHERE tenant_id = @tenant;", "UPDATE connections SET status = 'Active' WHERE tenant_id = @tenant;", "initial_sync.connection.unavailable" },
        { "UPDATE tenants SET status = 'Suspended' WHERE tenant_id = @tenant;", "UPDATE tenants SET status = 'Active' WHERE tenant_id = @tenant;", "initial_sync.tenant.inactive" },
        { "UPDATE connector_authorizations SET state = 'ReauthorizationRequired', reauthorization_required_at_utc = now() WHERE tenant_id = @tenant;", "UPDATE connector_authorizations SET state = 'Active' WHERE tenant_id = @tenant;", "initial_sync.connection.unavailable" },
    };

    [Theory]
    [MemberData(nameof(RevokedAccess))]
    public async Task Access_that_does_not_stand_sends_nothing_waits_and_resumes(string revoke, string restore, string code)
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture, options => options.MaxAttempts = 1);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        await AdministerAsync(fixture, revoke, ("tenant", stack.Tenant.Value));

        // DoD 28: checked locally before every call; nothing is sent, nothing is held, however long it lasts.
        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);
            var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
            Assert.Equal((code, run + 1), (sync.LastOutcome, sync.Attempts));
            stack.Clock.UtcNow = sync.NextAttemptAtUtc!.Value;
        }

        Assert.Empty(stack.Account.Answered);
        Assert.Equal("Pending", Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection)).State);

        await AdministerAsync(fixture, restore, ("tenant", stack.Tenant.Value));
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
    }

    [Fact]
    public async Task Access_revoked_mid_scan_stops_before_the_next_call_and_applies_nothing_more()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.PageSize = 1;
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, Board, "Done", at);
        stack.Account.AfterAnswer = async (operation, _, _) =>
        {
            if (operation == "page")
            {
                await AdministerAsync(fixture, "UPDATE connections SET status = 'Disabled' WHERE tenant_id = @tenant;", ("tenant", stack.Tenant.Value));
            }
        };
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        Assert.Equal(InitialSyncState.Reading, (await stack.RunOnceAsync()).State);

        // The page read before the revocation is not applied; the next page is never asked for.
        Assert.Equal(["board", "page"], stack.Account.Answered);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal("initial_sync.connection.unavailable", (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.LastOutcome);
    }

    [Fact]
    public async Task A_database_failure_mid_page_leaves_no_partial_write_and_the_next_run_converges()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        stack.Account.AddItem(ItemB, Board, "Stuck", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // The Snapshot write of the second item fails after its evidence receipt and ledger entry were written.
        var failure = "test_fail_snapshot_" + Guid.NewGuid().ToString("N");
        await AdministerAsync(
            fixture,
            $"""
            CREATE FUNCTION {failure}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.object_id = '{ItemB}' THEN RAISE EXCEPTION 'simulated database failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {failure} BEFORE INSERT ON snapshot_fields FOR EACH ROW EXECUTE FUNCTION {failure}();
            """);
        try
        {
            Assert.Equal(InitialSyncErrors.Unavailable, (await stack.RunSyncAsync()).Error);
        }
        finally
        {
            await AdministerAsync(fixture, $"DROP TRIGGER {failure} ON snapshot_fields; DROP FUNCTION {failure}();");
        }

        // Only the first item committed: the failed transaction left no receipt, ledger entry or target behind.
        Assert.Equal([ItemA], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId));
        Assert.Single(await ReceiptsAsync(fixture, stack.Tenant));
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Equal([ItemA], (await TargetsAsync(fixture, stack.Tenant)).Select(target => target.ObjectId));

        // The run's claim lapses with its lease; the next run reads the board again and converges.
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddMinutes(6);
        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal([ItemA, ItemB], (await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Select(field => field.ObjectId).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
        Assert.Equal(2, (await ReceiptsAsync(fixture, stack.Tenant)).Count);
    }

    [Fact]
    public async Task A_second_worker_cannot_take_a_live_claim_and_a_stale_worker_records_nothing_and_never_marks_live()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Working on it", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // Worker A stalls inside its page read, holding the generation's claim.
        var stalledA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Account.AfterAnswer = async (operation, before, _) =>
        {
            if (operation == "page" && before == 0)
            {
                stalledA.SetResult();
                await releaseA.Task;
            }
            else if (operation == "page" && before == 1)
            {
                stalledC.SetResult();
                await releaseC.Task;
            }
        };
        var workerA = stack.RunSyncAsync();
        await stalledA.Task;

        // D3F-13: while A's lease is live, no other worker owns the generation.
        Assert.Equal(InitialSyncErrors.InProgress, (await stack.RunSyncAsync()).Error);

        // A's lease lapses; worker C takes the generation over, with monday's state moved on, and stalls in its own read.
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddMinutes(6);
        stack.Account.SetStatus(ItemA, "Done", at.AddMinutes(1));
        var workerC = stack.RunSyncAsync();
        await stalledC.Task;

        // A wakes with its stale page while C holds the generation: it applies nothing and records nothing.
        releaseA.SetResult();
        Assert.Equal(InitialSyncErrors.ClaimLost, (await workerA).Error);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        var reading = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal("Reading", reading.State);
        Assert.NotNull(reading.ClaimId);

        // C completes the generation it holds; the stale worker never marked it Live.
        releaseC.SetResult();
        Assert.Equal(InitialSyncState.Live, (await workerC).Value.State);
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal("Done", field.Value);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_stale_worker_held_at_the_cutover_never_marks_live_and_the_worker_that_took_over_does()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // A receipt being stored keeps the cutover waiting: worker A reaches it and waits there.
        var inFlight = Guid.CreateVersion7();
        var (runtime, transaction) = await BeginIngressAsync(fixture, stack.Connection, inFlight, ItemA, "Stuck");
        Result<InitialSyncSummary> resultA;
        Result<InitialSyncSummary> resultC;
        await using (runtime)
        {
            await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
            var workerA = stack.RunSyncAsync();
            var waitingA = await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, runtime.ProcessID, workerA);
            Assert.NotNull(waitingA);

            // A's lease lapses; worker C takes the generation over and reaches the cutover too.
            stack.Clock.UtcNow = stack.Clock.UtcNow.AddMinutes(6);
            var workerC = stack.RunSyncAsync();
            await WaitForSecondWaiterAsync(monitor, runtime.ProcessID, waitingA.Value, workerC);

            await transaction.CommitAsync(Token);
            resultA = await workerA;
            resultC = await workerC;
        }

        // D3F-13: the stale worker records nothing and cannot mark Live; the worker holding the claim does.
        Assert.Equal(InitialSyncErrors.ClaimLost, resultA.Error);
        Assert.Equal(InitialSyncState.Live, resultC.Value.State);
        var sync = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(("Live", (Guid?)null), (sync.State, sync.ClaimId));
        var (sequence, state) = await ReceiptAsync(fixture, inFlight);
        Assert.Equal("Processed", state);
        Assert.True(sequence <= sync.CutoverFence);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.ReceiptId == inFlight);
    }

    [Fact]
    public async Task A_read_under_another_delegated_identity_than_the_generations_is_held_and_never_applied()
    {
        // The connection's authorization is the delegated user 58201774; the credential reads as another user.
        await using var stack = await InitialSyncStack.StartAsync(
            fixture,
            credentials: (connection, clock) => new FixedMondayCredentials(connection.Tenant, connection.Connection, clock, "99999999"));
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        stack.Account.AddBoard(Board);
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        // The authorization did not change, so this is a stable mismatch: held, and the generation fails.
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        var scope = Assert.Single(await ScopesAsync(fixture, stack.Tenant, stack.Connection.Connection));
        Assert.Equal(("Held", (string?)"initial_sync.identity.mismatch"), (scope.State, scope.LastOutcome));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Empty(await TargetsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_failed_generation_keeps_its_held_receipts_for_the_next_which_replays_them()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        var at = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-10);
        var board = stack.Account.AddBoard(Board);
        board.State = "archived";
        stack.Account.AddItem(ItemA, Board, "Done", at);
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();
        var (heldInFirst, _) = await stack.DeliverAsync(ItemA, "Stuck");
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        var failed = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;

        // While Failed, the connection is not Live: receipts keep being held, never processed ahead of a baseline.
        var (heldWhileFailed, _) = await stack.DeliverAsync(ItemA, "Working on it");
        Assert.Equal("Received", (await ReceiptAsync(fixture, heldWhileFailed)).State);

        // The board is restored and a new generation started: its fence is later, its replay floor is not.
        board.State = "active";
        Assert.Equal(2L, (await stack.StartedAsync()).Generation);
        var restarted = (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!;
        Assert.Equal(failed.ReplayFloor, restarted.ReplayFloor);
        Assert.True(restarted.BufferStartFence > failed.BufferStartFence);

        Assert.Equal(InitialSyncState.Live, (await stack.RunOnceAsync()).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, heldInFirst)).State);
        Assert.Equal("Processed", (await ReceiptAsync(fixture, heldWhileFailed)).State);
    }

    // Waits until a second backend, besides the first waiter, waits behind the receipt being stored or behind the first.
    private async Task WaitForSecondWaiterAsync(NpgsqlConnection monitor, int blockingPid, int firstWaiter, Task finished)
    {
        for (var probe = 0; probe < 400; probe++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE pid <> @first AND (@blocking = ANY(pg_blocking_pids(pid)) OR @first = ANY(pg_blocking_pids(pid)));",
                monitor);
            command.Parameters.AddWithValue("blocking", blockingPid);
            command.Parameters.AddWithValue("first", firstWaiter);
            if ((long)(await command.ExecuteScalarAsync(Token))! > 0)
            {
                return;
            }

            Assert.False(finished.IsCompleted, "The second worker finished without ever waiting at the cutover.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        Assert.Fail("No second worker ever waited at the cutover.");
    }

    [Fact]
    public async Task A_restart_is_refused_while_the_generation_runs_and_a_failed_one_is_never_resumed_by_a_run()
    {
        await using var stack = await InitialSyncStack.StartAsync(fixture);
        stack.Account.AddBoard(Board).Hierarchy = "multi_level";
        await stack.ConfigureAsync(Board);
        await stack.StartedAsync();

        Assert.Equal(InitialSyncErrors.InProgress, (await stack.StartSyncAsync()).Error);
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        Assert.Equal(InitialSyncState.Failed, (await stack.RunOnceAsync()).State);
        Assert.Equal(1L, (await SyncAsync(fixture, stack.Tenant, stack.Connection.Connection))!.Generation);
    }
}
