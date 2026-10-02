using System.Net;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.Reconciliation;

/// <summary>
/// Gate 3E end to end against the real local PostgreSQL. A signed webhook holds an item's status for reconciliation;
/// the reconciler rereads it through the real monday reader and 3A client from a fake of monday's API, binds the answer
/// to the item, board, column and status type, and applies the source's current state through the unchanged processing
/// core. Only a bound read confirms anything; the current state wins over the webhook; nothing about another board,
/// object or identity is ever remapped; and every failure leaves the target retryable or held, never resolved.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MondayReconciliationTests(DatabaseFixture fixture)
{
    private const string Item = "1772099344";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_held_webhook_is_repaired_from_the_current_api_state_never_from_the_webhook_value()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var held = await stack.HoldAsync(Item, label: "Working on it");

        // D3E-11: the hold wrote its target, with the webhook's board as the scope and the hold as its request.
        var target = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal((Item, "status", "TaskStatus", ReconciliationStack.Board), (target.ObjectId, target.Field, target.DataType, target.SourceScope));
        Assert.Equal(("Pending", 1L, (long?)null, "Event", (Guid?)held.EventId.Value), (target.State, target.Generation, target.ResolvedGeneration, target.RequestKind, target.RequestedByEventId));

        // D3E-4: before the reread, monday's state moved on to Done; the reconciler applies that, not the webhook's value.
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-3);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        var summary = await stack.RunAsync();

        Assert.Equal((1, 1, 0), (summary.Claimed, summary.Count(ReconciliationOutcome.Repaired), summary.NotApplied));
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal((Item, "status", "TaskStatus", "Done"), (field.ObjectId, field.Field, field.DataType, field.Value));

        // D3E-3: the status value's own update time orders it; no revision, no occurrence, no provider event id.
        Assert.Equal(updatedAt, field.SourceUpdatedAtUtc);
        Assert.Null(field.SourceRevision);
        Assert.Null(field.OccurredAtUtc);
        Assert.Null(field.SourceEventId);

        // The ledger holds the webhook's hold and the confirmed read, whose receipt is the read's own evidence.
        var ledger = await ProcessingHarness.LedgerAsync(fixture, stack.Tenant);
        Assert.Equal(["AcceptedForProjection", "ReconciliationRequired"], ledger.Select(entry => entry.Outcome).Order(StringComparer.Ordinal));
        Assert.Contains(ledger, entry => entry.EventId == held.EventId.Value && entry.ReasonCode == ProcessingErrors.UnconfirmedSourceValues.Code);
        var confirmed = Assert.Single(await ReadsAsync(stack));
        Assert.Equal(field.LastEventId, confirmed.EventId);
        Assert.Null(confirmed.SourceEventId);
        Assert.Equal([("status", "TaskStatus", "Advance")], await ProcessingHarness.LedgerFieldsAsync(fixture, confirmed.EventId));

        var evidence = Assert.Single(await ReceiptsAsync(fixture, stack.Tenant), receipt => receipt.ReceiptId == confirmed.ReceiptId);
        Assert.Equal((MondayStatusReader.EvidenceSchemaVersion, (string?)null, "Processed", false), (evidence.SchemaVersion, evidence.SourceEventId, evidence.ProcessingState, evidence.HasOutboxMessage));
        AssertEvidence(evidence.Payload!, Item, ReconciliationStack.Board, "Done", updatedAt);

        // Resolved at the generation it claimed, by the confirmed event, with its claim released.
        var resolved = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Resolved", (long?)1L, "reconciliation.repaired", (Guid?)confirmed.EventId), (resolved.State, resolved.ResolvedGeneration, resolved.LastOutcome, resolved.ResolvedByEventId));
        Assert.Null(resolved.ClaimId);
        Assert.NotNull(resolved.VerifiedAtUtc);

        // One read of exactly the held item's status column, with the connection's delegated credential.
        Assert.Equal([Item], RequestedItemIds(stack.Monday));
        var request = Assert.Single(stack.Monday.Requests, candidate => candidate.Uri == MondayEndpoints.Api);
        Assert.Equal(FixedMondayCredentials.AccessToken, request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(MondayQueries.ReadItemStatusesById, body.RootElement.GetProperty("query").GetString());
        Assert.Equal(["status"], body.RootElement.GetProperty("variables").GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Fact]
    public async Task A_read_equal_to_the_snapshot_resolves_the_target_with_no_new_event_or_receipt()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-3);
        await RepairAsync(stack, Item, "Done", updatedAt);
        var ledger = await ProcessingHarness.LedgerAsync(fixture, stack.Tenant);
        var receipts = await ReceiptsAsync(fixture, stack.Tenant);

        // The same source state, read again: idempotent, and nothing meaningless is recorded.
        Assert.Equal(1, await stack.SweepAsync());
        Assert.Equal(("Pending", 2L, "Sweep"), Single(await TargetsAsync(fixture, stack.Tenant)));
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        var summary = await stack.RunAsync();

        Assert.Equal(1, summary.Count(ReconciliationOutcome.Current));
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Equal(receipts.Count, (await ReceiptsAsync(fixture, stack.Tenant)).Count);
        var target = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Resolved", (long?)2L, "reconciliation.current"), (target.State, target.ResolvedGeneration, target.LastOutcome));
        Assert.Equal(Assert.Single(await ReadsAsync(stack)).EventId, target.ResolvedByEventId);
    }

    [Fact]
    public async Task A_missed_webhook_is_repaired_by_sweeping_known_objects_only_and_nothing_is_discovered()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var now = Seconds(await DatabaseNowAsync(fixture));
        await RepairAsync(stack, Item, "Done", now.AddMinutes(-10));

        // monday changed twice without a webhook reaching Fully; another item on the board was never known to it.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Stuck", now.AddMinutes(-2)), new ItemRead("1772099999", "Done", now))));
        Assert.Equal(1, await stack.SweepAsync());
        var summary = await stack.RunAsync();

        Assert.Equal(1, summary.Count(ReconciliationOutcome.UnsupportedResponse));
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);

        // monday answering an item nobody asked for binds nothing; asked again, for the known item only, it is repaired.
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddHours(2);
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Stuck", now.AddMinutes(-2)))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));

        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("Stuck", now.AddMinutes(-2)), (field.Value, field.SourceUpdatedAtUtc));
        Assert.All(RequestedItemIds(stack.Monday), id => Assert.Equal(Item, id));
        Assert.DoesNotContain(await TargetsAsync(fixture, stack.Tenant), target => target.ObjectId == "1772099999");
        Assert.DoesNotContain(await ProcessingHarness.SnapshotObjectsAsync(fixture, stack.Tenant), snapshot => snapshot.ObjectId == "1772099999");
    }

    [Fact]
    public async Task The_same_item_on_another_board_fails_closed_and_is_never_remapped()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync(Item);

        // The item moved: monday answers it, with a "status" column of the same id, on another board.
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1), Board: "1771899999")));
        var summary = await stack.RunAsync();

        Assert.Equal(1, summary.Count(ReconciliationOutcome.BindingMismatch));
        var target = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", ReconciliationStack.Board, "reconciliation.binding.mismatch"), (target.State, target.SourceScope, target.LastOutcome));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(["ReconciliationRequired"], (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Select(entry => entry.Outcome));
        Assert.DoesNotContain(await ReceiptsAsync(fixture, stack.Tenant), receipt => receipt.SchemaVersion == MondayStatusReader.EvidenceSchemaVersion);

        // A sweep verifies only targets already reconciled: a held one is not swept back into work.
        Assert.Equal(0, await stack.SweepAsync());
        Assert.Equal("Held", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);

        // A webhook from the new board is a new request, but the stored board is never replaced by it either.
        await stack.HoldAsync(Item, board: "1771899999");
        var conflicted = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", ReconciliationStack.Board, 2L, "reconciliation.scope.conflict"), (conflicted.State, conflicted.SourceScope, conflicted.Generation, conflicted.LastOutcome));
        Assert.Equal(0, (await stack.RunAsync()).Claimed);
    }

    [Fact]
    public async Task A_newer_request_during_a_read_is_never_closed_by_the_older_generation()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync(Item, label: "Working on it");
        var now = Seconds(await DatabaseNowAsync(fixture));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Monday.ThenApi(async (_, cancel) =>
        {
            await release.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-2))));
        });

        // The worker claims generation 1 and reads; webhook B arrives meanwhile and requests generation 2.
        var running = stack.RunAsync();
        await WaitUntilAsync(() => stack.Monday.ApiCalls == 1);
        await stack.HoldAsync(Item, label: "Stuck");
        var during = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal((2L, (long?)1L), (during.Generation, during.ClaimedGeneration));
        release.SetResult();
        var first = await running;

        // The current state still advanced the Snapshot through ordering, but generation 2 stays open.
        Assert.Equal(1, first.Count(ReconciliationOutcome.Repaired));
        Assert.Equal("Done", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value);
        var open = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Pending", 2L, (long?)null, (Guid?)null), (open.State, open.Generation, open.ResolvedGeneration, open.ClaimId));

        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-2))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Current));
        Assert.Equal(("Resolved", (long?)2L), (Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State, Assert.Single(await TargetsAsync(fixture, stack.Tenant)).ResolvedGeneration));
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_target_and_each_field_is_confirmed_once()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var items = Enumerable.Range(0, 6).Select(index => "17720993" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var item in items)
        {
            await stack.HoldAsync(item);
        }

        var now = Seconds(await DatabaseNowAsync(fixture));
        stack.Monday.AnswerApi(body =>
        {
            using var request = JsonDocument.Parse(body);
            var asked = request.RootElement.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead([.. asked.Select(id => new ItemRead(id, "Done", now.AddMinutes(-1)))]));
        });

        var runs = await Task.WhenAll(stack.RunAsync(), stack.RunAsync());

        Assert.Equal(items.Length, runs.Sum(run => run.Claimed));
        Assert.Equal(items.Length, runs.Sum(run => run.Count(ReconciliationOutcome.Repaired)));
        Assert.Equal(items.Order(), RequestedItemIds(stack.Monday).Order());
        var confirmed = (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Where(entry => entry.Outcome == "AcceptedForProjection").ToList();
        Assert.Equal(items.Order(), confirmed.Select(entry => entry.ObjectId!).Order());
        Assert.All(await TargetsAsync(fixture, stack.Tenant), target => Assert.Equal(("Resolved", (long?)1L), (target.State, target.ResolvedGeneration)));
    }

    [Fact]
    public async Task A_lapsed_claim_is_taken_over_and_the_late_worker_records_nothing()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.ClaimLease = TimeSpan.FromMinutes(5));
        await stack.HoldAsync(Item);
        var now = Seconds(await DatabaseNowAsync(fixture));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Monday.ThenApi(async (_, cancel) =>
        {
            await release.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Late", now.AddMinutes(-2))));
        });
        var late = stack.RunAsync();
        await WaitUntilAsync(() => stack.Monday.ApiCalls == 1);

        // The first worker stalls past its lease; a second takes the target over and finishes it.
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddMinutes(6);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        release.SetResult();
        var stalled = await late;

        Assert.Equal((1, 1), (stalled.Claimed, stalled.NotApplied));
        Assert.Empty(stalled.Outcomes);
        var field = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("Done", 1L), (field.Value, field.FieldVersion));
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.Outcome == "AcceptedForProjection");
    }

    [Fact]
    public async Task A_crash_inside_the_apply_commits_nothing_and_the_retry_converges()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.ClaimLease = TimeSpan.FromMinutes(5));
        var now = Seconds(await DatabaseNowAsync(fixture));
        await RepairAsync(stack, Item, "Done", now.AddMinutes(-10));
        var ledger = await ProcessingHarness.LedgerAsync(fixture, stack.Tenant);
        var receipts = await ReceiptsAsync(fixture, stack.Tenant);
        Assert.Equal(1, await stack.SweepAsync());

        // The apply stops inside projection, after its receipt and ledger entry, and the process dies there.
        await using var blocker = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var held = await blocker.BeginTransactionAsync(Token);
        await using (var lockObject = new Npgsql.NpgsqlCommand("SELECT 1 FROM snapshot_objects WHERE tenant_id = @tenant FOR UPDATE;", blocker, held))
        {
            lockObject.Parameters.AddWithValue("tenant", stack.Tenant.Value);
            await lockObject.ExecuteNonQueryAsync(Token);
        }

        await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Stuck", now.AddMinutes(-2))));
        using var crash = new CancellationTokenSource();
        var dying = stack.RunAsAsync(stack.Tenant, stack.Connection.Connection, 100, crash.Token);
        Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, blocker.ProcessID, dying));
        await crash.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dying);
        await held.RollbackAsync(Token);

        // Nothing of the apply survived: no receipt, no ledger entry, no Snapshot change; the claim simply lapses.
        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Equal(receipts.Count, (await ReceiptsAsync(fixture, stack.Tenant)).Count);
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        var claimed = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Pending", (long?)2L), (claimed.State, claimed.ClaimedGeneration));

        stack.Clock.UtcNow = stack.Clock.UtcNow.AddMinutes(6);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Stuck", now.AddMinutes(-2))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal(("Stuck", 2L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal(ledger.Count + 1, (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).Count);
    }

    [Fact]
    public async Task A_target_left_pending_after_its_state_was_applied_converges_without_a_second_event()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-4);
        await RepairAsync(stack, Item, "Done", updatedAt);
        var ledger = await ProcessingHarness.LedgerAsync(fixture, stack.Tenant);

        // As if the Snapshot had been repaired and the target's resolution lost: the target is pending again.
        await AdministerAsync(
            fixture,
            "UPDATE reconciliation_targets SET state = 'Pending', generation = generation + 1, request_kind = 'Sweep', requested_by_event_id = NULL WHERE tenant_id = @tenant;",
            ("tenant", stack.Tenant.Value));
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Current));

        Assert.Equal(ledger, await ProcessingHarness.LedgerAsync(fixture, stack.Tenant));
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
    }

    [Fact]
    public async Task Duplicate_holds_of_one_field_share_one_target_and_converge_on_one_confirmed_state()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync(Item, label: "Working on it");
        await stack.HoldAsync(Item, label: "Working on it");
        await stack.HoldAsync(Item, label: "Stuck");

        var target = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal((3L, "Pending", 0), (target.Generation, target.State, target.Attempts));

        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        var summary = await stack.RunAsync();

        Assert.Equal((1, 1), (summary.Claimed, summary.Count(ReconciliationOutcome.Repaired)));
        Assert.Equal([Item], RequestedItemIds(stack.Monday));
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal(("Resolved", (long?)3L), (Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State, Assert.Single(await TargetsAsync(fixture, stack.Tenant)).ResolvedGeneration));
    }

    [Fact]
    public async Task A_status_label_renamed_without_a_newer_update_time_is_not_forced_into_the_snapshot()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-4);
        await RepairAsync(stack, Item, "Done", updatedAt);
        Assert.Equal(1, await stack.SweepAsync());

        // Another label at the same update time: ordering cannot call it newer, so nothing is forced (approved fail-closed).
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Completed", updatedAt)));
        var summary = await stack.RunAsync();

        Assert.Equal(1, summary.Count(ReconciliationOutcome.OrderingAmbiguous));
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        var last = (await ReadsAsync(stack))[^1];
        Assert.Equal(("ReconciliationRequired", ProcessingErrors.OrderingAmbiguous.Code), (last.Outcome, last.ReasonCode));

        // Held at the sweep's generation: reconciliation's own hold is recorded on its target, never requested again.
        var held = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", "reconciliation.ordering.ambiguous", 2L, "Sweep"), (held.State, held.LastOutcome, held.Generation, held.RequestKind));
    }

    [Fact]
    public async Task A_read_without_an_update_time_is_held_and_never_given_one()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync(Item);

        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", UpdatedAt: null)));
        var summary = await stack.RunAsync();

        Assert.Equal(1, summary.Count(ReconciliationOutcome.OrderingUnevidenced));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, stack.Tenant));
        var last = Assert.Single(await ReadsAsync(stack));
        Assert.Equal(("ReconciliationRequired", ProcessingErrors.NoOrderingEvidence.Code), (last.Outcome, last.ReasonCode));
        Assert.Equal("Held", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
    }

    [Fact]
    public async Task The_five_minute_clock_skew_rule_applies_unchanged_to_the_source_update_time()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync("1772099301");
        await stack.HoldAsync("1772099302");

        // Against the database's receipt time: four minutes ahead is tolerated, six minutes ahead is impossible.
        var now = Seconds(await DatabaseNowAsync(fixture));
        stack.Monday.ThenApi(
            HttpStatusCode.OK,
            StatusRead(new ItemRead("1772099301", "Done", now.AddMinutes(4)), new ItemRead("1772099302", "Done", now.AddMinutes(6))));
        var summary = await stack.RunAsync();

        Assert.Equal((1, 1), (summary.Count(ReconciliationOutcome.Repaired), summary.Count(ReconciliationOutcome.OrderingImpossible)));
        Assert.Equal("1772099301", Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).ObjectId);
        var quarantined = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, stack.Tenant), entry => entry.Outcome == "Quarantined");
        Assert.Equal(("ImpossibleOrdering", ProcessingErrors.SourceTimeAfterReceipt.Code), (quarantined.QuarantineReason, quarantined.ReasonCode));
        var targets = await TargetsAsync(fixture, stack.Tenant);
        Assert.Equal(["Resolved", "Held"], targets.Select(target => target.State));
        Assert.Equal("reconciliation.ordering.impossible", targets[1].LastOutcome);
    }

    [Fact]
    public async Task A_read_older_than_the_snapshot_is_superseded_and_changes_nothing()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var now = Seconds(await DatabaseNowAsync(fixture));
        await RepairAsync(stack, Item, "Done", now.AddMinutes(-2));
        Assert.Equal(1, await stack.SweepAsync());

        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Working on it", now.AddMinutes(-9))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Superseded));

        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal("Stale", (await ReadsAsync(stack))[^1].Outcome);
        Assert.Equal(("Resolved", "reconciliation.superseded"), (Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State, Assert.Single(await TargetsAsync(fixture, stack.Tenant)).LastOutcome));
    }

    [Fact]
    public async Task Archived_deleted_missing_and_unsupported_items_are_never_applied_or_guessed()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        string[] items = ["1772099311", "1772099312", "1772099313", "1772099314", "1772099315"];
        foreach (var item in items)
        {
            await stack.HoldAsync(item);
        }

        var now = Seconds(await DatabaseNowAsync(fixture));
        stack.Monday.ThenApi(
            HttpStatusCode.OK,
            "{\"data\":{\"items\":["
            + "{\"id\":\"1772099311\",\"state\":\"archived\",\"board\":{\"id\":\"1771812698\"},\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"label\":\"Done\",\"index\":1,\"updated_at\":\"" + Iso(now) + "\"}]},"
            + "{\"id\":\"1772099312\",\"state\":\"deleted\",\"board\":{\"id\":\"1771812698\"},\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"label\":\"Done\",\"index\":1,\"updated_at\":\"" + Iso(now) + "\"}]},"
            + "{\"id\":\"1772099314\",\"state\":\"active\",\"board\":{\"id\":\"1771812698\"},\"column_values\":[]},"
            + "{\"id\":\"1772099315\",\"state\":\"active\",\"board\":{\"id\":\"1771812698\"},\"column_values\":[{\"id\":\"status\",\"type\":\"status\"}]}"
            + "]},\"account_id\":9001}");
        var summary = await stack.RunAsync();

        // Archived and deleted are provider evidence, held; absence is retried, never read as deletion; a rollup monday
        // omits and a status its fragment did not match are held, never taken for a status.
        Assert.Equal(2, summary.Count(ReconciliationOutcome.ObjectInactive));
        Assert.Equal(1, summary.Count(ReconciliationOutcome.ObjectMissing));
        Assert.Equal(1, summary.Count(ReconciliationOutcome.FieldMissing));
        Assert.Equal(1, summary.Count(ReconciliationOutcome.FieldUnsupported));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        var targets = (await TargetsAsync(fixture, stack.Tenant)).ToDictionary(target => target.ObjectId);
        Assert.Equal(("Held", "reconciliation.object.inactive"), (targets["1772099311"].State, targets["1772099311"].LastOutcome));
        Assert.Equal(("Held", "reconciliation.object.inactive"), (targets["1772099312"].State, targets["1772099312"].LastOutcome));
        Assert.Equal(("Pending", "reconciliation.object.missing"), (targets["1772099313"].State, targets["1772099313"].LastOutcome));
        Assert.NotNull(targets["1772099313"].NextAttemptAtUtc);
        Assert.Equal(("Held", "reconciliation.field.missing"), (targets["1772099314"].State, targets["1772099314"].LastOutcome));
        Assert.Equal(("Held", "reconciliation.field.unsupported"), (targets["1772099315"].State, targets["1772099315"].LastOutcome));
    }

    public static TheoryData<ReconciliationOutcome, string> TransientFailures => new()
    {
        { ReconciliationOutcome.RateLimited, "reconciliation.provider.rate_limited" },
        { ReconciliationOutcome.ProviderUnavailable, "reconciliation.provider.unavailable" },
        { ReconciliationOutcome.CredentialUnavailable, "reconciliation.credential.unavailable" },
    };

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task A_transient_failure_past_the_attempt_bound_stays_retryable_and_the_snapshot_is_repaired_once_it_ends(
        ReconciliationOutcome failure,
        string code)
    {
        SwitchableMondayCredentials? credentials = null;
        await using var stack = await ReconciliationStack.StartAsync(
            fixture,
            options => options.MaxAttempts = 8,
            credentials: (connection, clock, _) => credentials = new SwitchableMondayCredentials(connection.Tenant, connection.Connection, clock));
        await stack.HoldAsync(Item);

        // monday throttles or fails, or the credential cannot be read just now: twelve times running, past the bound of
        // eight, and the target is still pending each time, never held.
        credentials!.Unobtainable = failure == ReconciliationOutcome.CredentialUnavailable;
        stack.Monday.AnswerApi(_ => failure == ReconciliationOutcome.RateLimited
            ? FakeMonday.Json(HttpStatusCode.OK, RateLimitedAnswer(retryInSeconds: 120))
            : FakeMonday.Json(HttpStatusCode.ServiceUnavailable, string.Empty));
        await PendingAfterAsync(stack, failure, code, attempts: 12);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // The failure ends: the next due attempt reads monday's current state and repairs the Snapshot, with no new request.
        credentials.Unobtainable = false;
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1);
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal(("Done", updatedAt), Current(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        var resolved = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Resolved", 1L, 0), (resolved.State, resolved.Generation, resolved.Attempts));
    }

    [Fact]
    public async Task A_two_day_provider_outage_is_waited_out_and_the_snapshot_is_repaired_when_monday_returns()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.MaxAttempts = 8);
        await stack.HoldAsync(Item);
        var start = stack.Clock.UtcNow;

        // monday is down for two days: every due attempt fails, and the target keeps waiting, never more than an hour.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.ServiceUnavailable, string.Empty));
        var attempts = 0;
        while (stack.Clock.UtcNow - start < TimeSpan.FromDays(2))
        {
            Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.ProviderUnavailable));
            attempts++;
            var waiting = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
            Assert.Equal(("Pending", attempts), (waiting.State, waiting.Attempts));
            Assert.InRange(waiting.NextAttemptAtUtc!.Value - stack.Clock.UtcNow, TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1));
            stack.Clock.UtcNow = waiting.NextAttemptAtUtc.Value;
        }

        // Thirty seconds doubling: seven attempts take 3,810 seconds, and every later one waits the hour's cap, so two days
        // take 54 attempts.
        Assert.Equal(54, attempts);
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));

        // monday is back: the next due attempt repairs the Snapshot from its current state.
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1);
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal(("Done", updatedAt), Current(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
    }

    [Fact]
    public async Task Retry_delays_double_from_the_base_to_the_cap_keep_a_longer_provider_wait_within_its_bound_and_never_loop()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.MaxProviderWait = TimeSpan.FromHours(3));
        await stack.HoldAsync(Item);
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.ServiceUnavailable, string.Empty));

        var delays = new List<int>();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            delays.Add(await FailOnceAsync(stack, ReconciliationOutcome.ProviderUnavailable));
        }

        // Thirty seconds, doubling, capped at an hour: never sooner than the base, never later than the cap.
        Assert.Equal([30, 60, 120, 240, 480, 960, 1920, 3600, 3600, 3600], delays);

        // monday asks for two hours, longer than the backoff: its wait is kept.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, RateLimitedAnswer(retryInSeconds: 7200)));
        Assert.Equal(7200, await FailOnceAsync(stack, ReconciliationOutcome.RateLimited));

        // monday asks for twelve hours: no provider's wait parks a target beyond the bound, three hours here.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, RateLimitedAnswer(retryInSeconds: 43200)));
        Assert.Equal(10800, await FailOnceAsync(stack, ReconciliationOutcome.RateLimited));
    }

    [Fact]
    public async Task A_connection_revoked_disabled_or_needing_authorization_and_a_suspended_tenant_fail_closed()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await stack.HoldAsync(Item);
        var now = Seconds(await DatabaseNowAsync(fixture));

        // Revoked during the reread: the read succeeded, but its result is never applied.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Monday.ThenApi(async (_, cancel) =>
        {
            await release.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        });
        var running = stack.RunAsync();
        await WaitUntilAsync(() => stack.Monday.ApiCalls == 1);
        await WebhookData.DisconnectAsync(fixture, stack.Connection, Token);
        release.SetResult();

        Assert.Equal(1, (await running).Count(ReconciliationOutcome.ConnectionUnavailable));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal(("Pending", "reconciliation.connection.unavailable"), (Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State, Assert.Single(await TargetsAsync(fixture, stack.Tenant)).LastOutcome));

        // A connection needing authorization again, and a suspended tenant, also confirm nothing; the first is not even read.
        await using var second = await ReconciliationStack.StartAsync(fixture);
        await second.HoldAsync(Item);
        await WebhookData.RequireReauthorizationAsync(fixture, second.Connection, Token);
        second.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        Assert.Equal(1, (await second.RunAsync()).Count(ReconciliationOutcome.ConnectionUnavailable));
        Assert.Equal(0, second.Monday.ApiCalls);
        Assert.Equal(("Pending", "reconciliation.connection.unavailable"), (Assert.Single(await TargetsAsync(fixture, second.Tenant)).State, Assert.Single(await TargetsAsync(fixture, second.Tenant)).LastOutcome));

        // The tenant is suspended while monday is being read: the read's result is never applied.
        await using var third = await ReconciliationStack.StartAsync(fixture);
        await third.HoldAsync(Item);
        var suspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        third.Monday.ThenApi(async (_, cancel) =>
        {
            await suspend.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        });
        var reading = third.RunAsync();
        await WaitUntilAsync(() => third.Monday.ApiCalls == 1);
        await AdministerAsync(fixture, "UPDATE tenants SET status = 'Suspended' WHERE tenant_id = @tenant;", ("tenant", third.Tenant.Value));
        suspend.SetResult();
        Assert.Equal(1, (await reading).Count(ReconciliationOutcome.TenantInactive));
        Assert.Equal(("Pending", "reconciliation.tenant.inactive"), (Assert.Single(await TargetsAsync(fixture, third.Tenant)).State, Assert.Single(await TargetsAsync(fixture, third.Tenant)).LastOutcome));

        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, second.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, third.Tenant));
    }

    [Fact]
    public async Task While_access_does_not_stand_no_credential_is_acquired_and_nothing_is_sent_to_monday()
    {
        // monday would confirm a value if it were asked.
        var answer = StatusRead(new ItemRead(Item, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1)));

        // The connection needs authorization again: ten due attempts, past the bound of eight, each wait without even
        // asking for a credential.
        FixedMondayCredentials? credentials = null;
        await using var stack = await ReconciliationStack.StartAsync(
            fixture,
            options => options.MaxAttempts = 8,
            credentials: (connection, clock, _) => credentials = new FixedMondayCredentials(connection.Tenant, connection.Connection, clock, "58201774"));
        await stack.HoldAsync(Item);
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, answer));
        await WebhookData.RequireReauthorizationAsync(fixture, stack.Connection, Token);
        await PendingAfterAsync(stack, ReconciliationOutcome.ConnectionUnavailable, "reconciliation.connection.unavailable", attempts: 10);
        Assert.Equal((0, 0), (credentials!.Acquired, stack.Monday.ApiCalls));

        // Likewise for a disconnected connection.
        FixedMondayCredentials? disconnectedCredentials = null;
        await using var disconnected = await ReconciliationStack.StartAsync(
            fixture,
            options => options.MaxAttempts = 8,
            credentials: (connection, clock, _) => disconnectedCredentials = new FixedMondayCredentials(connection.Tenant, connection.Connection, clock, "58201774"));
        await disconnected.HoldAsync(Item);
        disconnected.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, answer));
        await WebhookData.DisconnectAsync(fixture, disconnected.Connection, Token);
        await PendingAfterAsync(disconnected, ReconciliationOutcome.ConnectionUnavailable, "reconciliation.connection.unavailable", attempts: 10);
        Assert.Equal((0, 0), (disconnectedCredentials!.Acquired, disconnected.Monday.ApiCalls));

        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, disconnected.Tenant));
    }

    [Fact]
    public async Task Work_waiting_for_reauthorization_resumes_by_itself_once_the_connection_is_authorized_again()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var secrets = new MemorySecrets();
        secrets.Put(MondaySecretName.ClientSecret, "client-credential-not-a-secret");
        var monday = new FakeMonday();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var store = ConnectorAuthorizationData.Store(fixture, tenant);
        var options = new MondayOAuthOptions("wce-test-client", new Uri("https://app.example.test/connectors/monday/callback"));
        var http = new HttpClient(monday);
        var credentials = new MondayConnectionCredentialSource(store.Store, secrets, options, http, clock, new NoWait());
        var authorizer = new MondayConnectionAuthorizer(store.Store, secrets, options, http, new MondayReadClient(http, credentials, clock, new NoWait(), () => 0.5), store.Accessor, clock);
        var principal = Core.Identity.Principal.Authenticated(user, new Core.Identity.ExternalIdentity(Core.Identity.IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var (connectionId, _) = await AuthorizeAsync(authorizer, principal, monday, clock, reauthorize: null, marker: "1");

        await using var stack = await ReconciliationStack.StartAsync(
            fixture,
            reconciliation => reconciliation.MaxAttempts = 8,
            connection: new SeededConnection(tenant, user, connectionId, "9001"),
            credentials: (_, _, _) => credentials,
            monday: monday,
            clock: clock);
        await stack.HoldAsync(Item);

        // Renewing before the reread, 3B finds its refresh token rejected: the connection needs its user to authorize it again.
        // That attempt and nine more, past the bound of eight, all wait; after the rejected renewal nothing reaches monday.
        clock.UtcNow = clock.UtcNow.AddMinutes(56);
        monday.ThenToken(_ => Task.FromResult(FakeMonday.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")));
        await PendingAfterAsync(stack, ReconciliationOutcome.ConnectionUnavailable, "reconciliation.connection.unavailable", attempts: 10);
        Assert.Equal((2, 1), (monday.TokenCalls, monday.ApiCalls));

        // The user authorizes the connection again: the waiting work resumes at its next attempt, with no new webhook.
        var (restored, token) = await AuthorizeAsync(authorizer, principal, monday, clock, reauthorize: connectionId, marker: "2");
        Assert.Equal(connectionId, restored);
        var receipts = (await ReceiptsAsync(fixture, tenant)).Count;
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1);
        monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));

        Assert.Equal(("Done", updatedAt), Current(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)));
        Assert.Equal(token, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);
        Assert.Equal([MondayStatusReader.EvidenceSchemaVersion], (await ReceiptsAsync(fixture, tenant)).Skip(receipts).Select(receipt => receipt.SchemaVersion));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, tenant)).State);
    }

    [Fact]
    public async Task Work_waiting_for_a_suspended_tenant_resumes_by_itself_once_the_tenant_is_active_again()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.MaxAttempts = 8);
        await stack.HoldAsync(Item);

        // Suspended: ten due attempts, past the bound of eight, wait; nothing is sent to monday.
        await AdministerAsync(fixture, "UPDATE tenants SET status = 'Suspended' WHERE tenant_id = @tenant;", ("tenant", stack.Tenant.Value));
        await PendingAfterAsync(stack, ReconciliationOutcome.TenantInactive, "reconciliation.tenant.inactive", attempts: 10);
        Assert.Equal(0, stack.Monday.ApiCalls);

        // Active again: the waiting work resumes at its next attempt, with no new webhook.
        await AdministerAsync(fixture, "UPDATE tenants SET status = 'Active' WHERE tenant_id = @tenant;", ("tenant", stack.Tenant.Value));
        var receipts = (await ReceiptsAsync(fixture, stack.Tenant)).Count;
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));

        Assert.Equal(("Done", updatedAt), Current(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal([MondayStatusReader.EvidenceSchemaVersion], (await ReceiptsAsync(fixture, stack.Tenant)).Skip(receipts).Select(receipt => receipt.SchemaVersion));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
    }

    [Fact]
    public async Task Semantic_holds_stay_held_and_neither_time_nor_a_sweep_nor_a_later_run_reopens_them()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        foreach (var item in new[] { "1772099321", "1772099322", "1772099323" })
        {
            await stack.HoldAsync(item);
        }

        // Archived, moved to another board, and without ordering evidence: retrying cannot change any of them.
        var now = Seconds(await DatabaseNowAsync(fixture));
        stack.Monday.ThenApi(
            HttpStatusCode.OK,
            StatusRead(
                new ItemRead("1772099321", "Done", now.AddMinutes(-1), State: "archived"),
                new ItemRead("1772099322", "Done", now.AddMinutes(-1), Board: "1771812699"),
                new ItemRead("1772099323", "Done", UpdatedAt: null)));
        var summary = await stack.RunAsync();
        Assert.Equal((1, 1, 1), (summary.Count(ReconciliationOutcome.ObjectInactive), summary.Count(ReconciliationOutcome.BindingMismatch), summary.Count(ReconciliationOutcome.OrderingUnevidenced)));
        var held = await TargetsAsync(fixture, stack.Tenant);
        Assert.All(held, target => Assert.Equal(("Held", (DateTimeOffset?)null), (target.State, target.NextAttemptAtUtc)));

        // Three days on, a run claims nothing and asks monday nothing, and a sweep verifies only resolved targets.
        var calls = stack.Monday.ApiCalls;
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddDays(3);
        Assert.Equal(0, (await stack.RunAsync()).Claimed);
        Assert.Equal(0, await stack.SweepAsync());
        Assert.Equal(0, (await stack.RunAsync()).Claimed);

        Assert.Equal(calls, stack.Monday.ApiCalls);
        Assert.Equal(held, await TargetsAsync(fixture, stack.Tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_missing_item_is_held_after_the_bound_of_consecutive_missing_answers_and_an_outage_spends_none_of_it()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.MaxAttempts = 8);
        await stack.HoldAsync(Item);

        // First an outage: ten failed attempts, none of them an answer without the item.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.ServiceUnavailable, string.Empty));
        await PendingAfterAsync(stack, ReconciliationOutcome.ProviderUnavailable, "reconciliation.provider.unavailable", attempts: 10);

        // Then monday answers without the item: seven such answers keep it pending, counted from one, and the eighth holds it.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, NoItems));
        await PendingAfterAsync(stack, ReconciliationOutcome.ObjectMissing, "reconciliation.object.missing", attempts: 7);
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.ObjectMissing));

        var held = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", 8, "reconciliation.object.missing", (DateTimeOffset?)null), (held.State, held.Attempts, held.LastOutcome, held.NextAttemptAtUtc));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_structurally_unsupported_answer_is_held_after_the_bound_of_consecutive_such_answers()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, options => options.MaxAttempts = 8);
        await stack.HoldAsync(Item);

        // monday answers with a shape the reader does not understand: seven times pending, and the eighth holds it.
        stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, """{"data":{"items":{"id":"1772099344"}},"account_id":9001}"""));
        await PendingAfterAsync(stack, ReconciliationOutcome.UnsupportedResponse, "reconciliation.provider.unsupported_response", attempts: 7);
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.UnsupportedResponse));

        var held = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", 8, "reconciliation.provider.unsupported_response", (DateTimeOffset?)null), (held.State, held.Attempts, held.LastOutcome, held.NextAttemptAtUtc));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_read_under_any_identity_but_the_connections_current_delegated_one_confirms_nothing()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, credentialSubject: "11111111");
        await stack.HoldAsync(Item);

        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-1))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.IdentityMismatch));

        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
        Assert.Equal("Held", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
    }

    [Fact]
    public async Task A_read_overtaken_by_a_reauthorization_is_discarded_and_the_next_attempt_rereads_under_the_new_authorization()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var secrets = new MemorySecrets();
        secrets.Put(MondaySecretName.ClientSecret, "client-credential-not-a-secret");
        var monday = new FakeMonday();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var store = ConnectorAuthorizationData.Store(fixture, tenant);
        var options = new MondayOAuthOptions("wce-test-client", new Uri("https://app.example.test/connectors/monday/callback"));
        var http = new HttpClient(monday);
        var credentials = new MondayConnectionCredentialSource(store.Store, secrets, options, http, clock, new NoWait());
        var authorizer = new MondayConnectionAuthorizer(store.Store, secrets, options, http, new MondayReadClient(http, credentials, clock, new NoWait(), () => 0.5), store.Accessor, clock);
        var principal = Core.Identity.Principal.Authenticated(user, new Core.Identity.ExternalIdentity(Core.Identity.IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var (connectionId, tokenA) = await AuthorizeAsync(authorizer, principal, monday, clock, reauthorize: null, marker: "1");
        var connection = new SeededConnection(tenant, user, connectionId, "9001");

        await using var stack = await ReconciliationStack.StartAsync(fixture, connection: connection, credentials: (_, _, _) => credentials, monday: monday, clock: clock);
        await stack.HoldAsync(Item);
        var receipts = (await ReceiptsAsync(fixture, tenant)).Count;

        // The reread starts under authorization A. While monday answers, the connection needs authorizing again and its
        // user authorizes it again, through the real 3B flow: authorization B.
        var stale = "Stale " + Ingress.IngressData.NewMarker();
        var now = Seconds(await DatabaseNowAsync(fixture));
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monday.ThenApi(async (_, cancel) =>
        {
            await answer.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, stale, now.AddMinutes(-2))));
        });
        var running = stack.RunAsync();
        await WaitUntilAsync(() => monday.ApiCalls == 2);
        Assert.Equal(tokenA, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);
        await WebhookData.RequireReauthorizationAsync(fixture, connection, Token);
        var (_, tokenB) = await AuthorizeAsync(authorizer, principal, monday, clock, reauthorize: connectionId, marker: "2");
        answer.SetResult();

        // A's result is discarded: nothing applied, nothing confirmed, no evidence kept anywhere, and the target pending.
        Assert.Equal(1, (await running).Count(ReconciliationOutcome.AuthorityChanged));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(receipts, (await ReceiptsAsync(fixture, tenant)).Count);
        Assert.Empty(await WebhookData.FindAnywhereAsync(fixture, [stale], Token));
        var waiting = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal(("Pending", "reconciliation.authority.changed"), (waiting.State, waiting.LastOutcome));

        // The next due attempt rereads under B, with no new webhook, and B's authoritative state repairs the Snapshot.
        clock.UtcNow = waiting.NextAttemptAtUtc!.Value;
        var updatedAt = now.AddMinutes(-1);
        monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal(tokenB, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);
        Assert.Equal(("Done", updatedAt), Current(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)));
        Assert.Equal([MondayStatusReader.EvidenceSchemaVersion], (await ReceiptsAsync(fixture, tenant)).Skip(receipts).Select(receipt => receipt.SchemaVersion));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, tenant)).State);
    }

    [Fact]
    public async Task A_wrong_identity_is_retried_only_when_the_authorization_changed_during_the_read_and_held_once_it_did_not()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture, credentialSubject: "11111111");
        await stack.HoldAsync(Item);
        var now = Seconds(await DatabaseNowAsync(fixture));

        // The connection is authorized again while monday answers a read made under another identity: the read is stale
        // whatever it reports, so it is discarded and retried, never held and never applied.
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Monday.ThenApi(async (_, cancel) =>
        {
            await answer.Task.WaitAsync(cancel);
            return FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        });
        var running = stack.RunAsync();
        await WaitUntilAsync(() => stack.Monday.ApiCalls == 1);
        await ReauthorizeAsync(fixture, stack.Connection);
        answer.SetResult();
        Assert.Equal(1, (await running).Count(ReconciliationOutcome.AuthorityChanged));
        var waiting = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Pending", "reconciliation.authority.changed"), (waiting.State, waiting.LastOutcome));

        // Nothing changes during the next read, yet it reports the same wrong identity: a stable mismatch, held, and
        // monday is never asked again.
        stack.Clock.UtcNow = waiting.NextAttemptAtUtc!.Value;
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", now.AddMinutes(-1))));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.IdentityMismatch));
        var held = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal(("Held", "reconciliation.identity.mismatch", (DateTimeOffset?)null), (held.State, held.LastOutcome, held.NextAttemptAtUtc));

        var calls = stack.Monday.ApiCalls;
        stack.Clock.UtcNow = stack.Clock.UtcNow.AddDays(3);
        Assert.Equal(0, (await stack.RunAsync()).Claimed);
        Assert.Equal(0, await stack.SweepAsync());
        Assert.Equal((2, calls), (calls, stack.Monday.ApiCalls));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
    }

    [Fact]
    public async Task A_credential_bound_to_another_connection_or_tenant_sends_nothing_and_confirms_nothing()
    {
        // A credential for another connection, of another monday account, and one for the same connection id under another
        // tenant: the client refuses both before anything is sent, at every attempt.
        foreach (var foreign in new Func<SeededConnection, MovingClock, IMondayCredentialSource>[]
                 {
                     (connection, clock) => new FixedMondayCredentials(connection.Tenant, Guid.CreateVersion7(), clock, "58201774"),
                     (connection, clock) => new FixedMondayCredentials(Core.Tenancy.TenantId.New(), connection.Connection, clock, "58201774"),
                 })
        {
            await using var stack = await ReconciliationStack.StartAsync(fixture, credentials: (connection, clock, _) => foreign(connection, clock));
            await stack.HoldAsync(Item);
            stack.Monday.AnswerApi(_ => FakeMonday.Json(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", Seconds(DateTimeOffset.UtcNow).AddMinutes(-1)))));
            await PendingAfterAsync(stack, ReconciliationOutcome.CredentialUnavailable, "reconciliation.credential.unavailable", attempts: 3);

            Assert.Equal(0, stack.Monday.ApiCalls);
            Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant));
            Assert.DoesNotContain(await ReceiptsAsync(fixture, stack.Tenant), receipt => receipt.SchemaVersion == MondayStatusReader.EvidenceSchemaVersion);
        }
    }

    [Fact]
    public async Task A_target_without_its_scope_is_refused_before_any_call()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        await AdministerAsync(
            fixture,
            """
            INSERT INTO reconciliation_targets (tenant_id, object_system, object_connection, object_type, object_id, field, data_type,
                source_scope, state, generation, request_kind, requested_at_utc, attempts, created_at_utc)
            VALUES (@tenant, 'monday', @connection, 'item', '1772099399', 'status', 'TaskStatus', NULL, 'Pending', 1, 'Sweep', now(), 0, now());
            """,
            ("tenant", stack.Tenant.Value),
            ("connection", stack.Connection.Connection.ToString("D")));

        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.ScopeMissing));
        Assert.Equal(0, stack.Monday.ApiCalls);
        Assert.Equal(("Held", "reconciliation.scope.missing"), (Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State, Assert.Single(await TargetsAsync(fixture, stack.Tenant)).LastOutcome));
    }

    [Fact]
    public async Task Another_tenant_can_neither_claim_nor_change_a_tenants_targets()
    {
        await using var owner = await ReconciliationStack.StartAsync(fixture);
        await owner.HoldAsync(Item);
        await using var other = await ReconciliationStack.StartAsync(fixture);
        var before = await TargetsAsync(fixture, owner.Tenant);

        // In its own trusted scope, the other tenant naming the owner's connection finds nothing to claim.
        var foreign = await other.RunAsAsync(other.Tenant, owner.Connection.Connection, 100, Token);
        Assert.True(foreign.IsSuccess);
        Assert.Equal(0, foreign.Value.Claimed);

        // A run that names a tenant other than the trusted one is refused outright.
        var mismatched = await Task.Run(async () =>
        {
            await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(other.Processing.Services);
            other.Processing.TenantContextAccessor.SetResolved(Core.Tenancy.TenantContext.Resolved(other.Tenant, Core.Tenancy.TenantResolutionSource.TrustedServiceIdentity));
            return await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ISourceReconciler>(scope.ServiceProvider)
                .RunAsync(new ReconciliationRun(owner.Tenant, "monday", owner.Connection.Connection, 100), Token);
        });
        Assert.Equal(ReconciliationErrors.TenantMismatch, mismatched.Error);

        Assert.Equal(0, other.Monday.ApiCalls);
        Assert.Equal(before, await TargetsAsync(fixture, owner.Tenant));
        Assert.Empty(await TargetsAsync(fixture, other.Tenant));
    }

    [Fact]
    public async Task An_evidence_receipt_can_never_pass_for_a_webhook_delivery()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-2);
        await RepairAsync(stack, Item, "Done", updatedAt);
        var evidence = Assert.Single(await ReceiptsAsync(fixture, stack.Tenant), receipt => receipt.SchemaVersion == MondayStatusReader.EvidenceSchemaVersion);

        // A delivery of the settled evidence receipt is a no-op: the ledger already holds its decision.
        var replay = Core.Transport.TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), stack.Tenant, Core.Ingress.IngressMessageTypes.ReceiptReceived, Transport.TransportData.Marker(evidence.ReceiptId), Core.Correlation.CorrelationId.New());
        Assert.Equal(EventProcessingOutcome.Duplicate, (await stack.Processing.ProcessSettledAsync(replay.Value)).Outcome);

        // A receipt claiming the evidence schema but delivered like a webhook is never confirmed: no normalizer reads it.
        var forged = Guid.CreateVersion7();
        await AdministerAsync(
            fixture,
            """
            INSERT INTO raw_receipts (receipt_id, tenant_id, connection_reference, source_system, source_event_id, schema_version, payload,
                payload_fingerprint, processing_state)
            VALUES (@receipt, @tenant, @connection, 'monday', NULL, 'monday.api-read.v1', @payload::jsonb, 'forged-fingerprint', 'Received');
            """,
            ("receipt", forged),
            ("tenant", stack.Tenant.Value),
            ("connection", stack.Connection.Connection.ToString("D")),
            ("payload", evidence.Payload!.Replace("Done", "Forged", StringComparison.Ordinal)));
        var delivery = Core.Transport.TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), stack.Tenant, Core.Ingress.IngressMessageTypes.ReceiptReceived, Transport.TransportData.Marker(forged), Core.Correlation.CorrelationId.New());
        var result = await stack.Processing.ProcessSettledAsync(delivery.Value);

        Assert.Equal((EventProcessingOutcome.Quarantined, QuarantineReason.UnknownSchemaVersion), (result.Outcome, result.QuarantineReason));
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)));
        Assert.Equal("Resolved", Assert.Single(await TargetsAsync(fixture, stack.Tenant)).State);
        Assert.False(new MondayWebhookNormalizer().Supports(MondayStatusReader.EvidenceSchemaVersion));
    }

    [Fact]
    public async Task No_label_token_or_evidence_reaches_a_log_and_no_value_reaches_the_targets()
    {
        await using var stack = await ReconciliationStack.StartAsync(fixture);
        var label = "Done " + Ingress.IngressData.NewMarker();
        var hint = "Hint " + Ingress.IngressData.NewMarker();
        await stack.HoldAsync(Item, label: hint);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-2);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, label, updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));

        // The capture is not vacuous: the run's own summary is there, by codes and counts.
        var logs = stack.Processing.Logs.Entries.Concat(stack.Webhooks.Logs.Entries).ToList();
        Assert.Contains(logs, entry => entry.Text.Contains("reconciliation.repaired=1", StringComparison.Ordinal));
        foreach (var sentinel in new[] { label, hint, FixedMondayCredentials.AccessToken, "api_version", Iso(updatedAt) })
        {
            Assert.DoesNotContain(logs, entry => entry.Text.Contains(sentinel, StringComparison.Ordinal));
        }

        // The label lives in the Snapshot and in the receipts that carried it, never in the targets.
        Assert.Equal(
            ["raw_receipts.payload holds sentinel #1", "raw_receipts.payload holds sentinel #2", "snapshot_fields.value holds sentinel #1"],
            (await WebhookData.FindAnywhereAsync(fixture, [label, hint], Token)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Real_3b_credentials_are_acquired_for_every_reread_renewed_when_due_and_refused_once_revoked()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var secrets = new MemorySecrets();
        secrets.Put(MondaySecretName.ClientSecret, "client-credential-not-a-secret");
        var monday = new FakeMonday();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var store = ConnectorAuthorizationData.Store(fixture, tenant);
        var options = new MondayOAuthOptions("wce-test-client", new Uri("https://app.example.test/connectors/monday/callback"));
        var http = new HttpClient(monday);
        var credentials = new MondayConnectionCredentialSource(store.Store, secrets, options, http, clock, new NoWait());
        var authorizer = new MondayConnectionAuthorizer(store.Store, secrets, options, http, new MondayReadClient(http, credentials, clock, new NoWait(), () => 0.5), store.Accessor, clock);
        var principal = Core.Identity.Principal.Authenticated(user, new Core.Identity.ExternalIdentity(Core.Identity.IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var redirect = (await authorizer.StartAsync(principal, null, Token)).Value;
        var state = Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);
        var firstToken = FakeMonday.Jwt(clock.UtcNow.AddHours(1), "access-1");
        monday.ThenToken(FakeMonday.Tokens(firstToken, "refresh-1"));
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        Assert.True((await authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "code-1", null, null), Token)).IsSuccess);

        var connection = new SeededConnection(tenant, user, redirect.ConnectionId, "9001");
        await using var stack = await ReconciliationStack.StartAsync(fixture, connection: connection, credentials: (_, _, _) => credentials, monday: monday, clock: clock);
        await stack.HoldAsync(Item);
        var updatedAt = Seconds(await DatabaseNowAsync(fixture)).AddMinutes(-2);
        monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal(firstToken, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);

        // Within the refresh margin, 3B renews before the reread, and the reread uses the renewed token.
        clock.UtcNow = clock.UtcNow.AddMinutes(56);
        var renewedToken = FakeMonday.Jwt(clock.UtcNow.AddHours(1), "access-2");
        monday.ThenToken(FakeMonday.Tokens(renewedToken, "refresh-2"));
        Assert.Equal(1, await stack.SweepAsync());
        monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(Item, "Done", updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Current));
        Assert.Equal(renewedToken, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);

        // Once disconnected, the next reread sends nothing to monday and confirms nothing.
        Assert.True((await authorizer.DisconnectAsync(principal, redirect.ConnectionId, Token)).IsSuccess);
        var calls = monday.ApiCalls;
        Assert.Equal(1, await stack.SweepAsync());
        var summary = await stack.RunAsync();
        Assert.Equal(1, summary.Count(ReconciliationOutcome.ConnectionUnavailable));
        Assert.Equal(calls, monday.ApiCalls);
        Assert.Equal(("Done", 1L), Value(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)));
    }

    // The ledger entries of the confirmed reads, in the order the database received their evidence.
    private async Task<List<LedgerRow>> ReadsAsync(ReconciliationStack stack)
    {
        var ledger = (await ProcessingHarness.LedgerAsync(fixture, stack.Tenant)).ToDictionary(entry => entry.ReceiptId);
        return (await ReceiptsAsync(fixture, stack.Tenant))
            .Where(receipt => receipt.SchemaVersion == MondayStatusReader.EvidenceSchemaVersion)
            .Select(receipt => ledger[receipt.ReceiptId])
            .ToList();
    }

    // monday's answer without any of the requested items.
    private const string NoItems = """{"data":{"items":[]},"account_id":9001}""";

    // monday's complexity budget exhausted, asking the caller to wait the given number of seconds.
    private static string RateLimitedAnswer(int retryInSeconds) =>
        "{\"errors\":[{\"message\":\"Budget exhausted\",\"extensions\":{\"code\":\"ComplexityException\",\"retry_in_seconds\":"
        + retryInSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}],\"account_id\":9001}";

    // Runs the given number of due attempts that each end with the outcome, checking that the target stays pending with
    // its count of like attempts rising from one; the clock is left at the next attempt.
    private async Task PendingAfterAsync(ReconciliationStack stack, ReconciliationOutcome outcome, string code, int attempts)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var summary = await stack.RunAsync();
            Assert.Equal((1, 1), (summary.Claimed, summary.Count(outcome)));
            var waiting = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
            Assert.Equal(("Pending", attempt, code), (waiting.State, waiting.Attempts, waiting.LastOutcome));
            stack.Clock.UtcNow = waiting.NextAttemptAtUtc!.Value;
        }
    }

    // Runs one due attempt that fails as given, checks that a run a second before the next attempt claims and sends
    // nothing, and returns the delay before the next attempt in whole seconds; the clock is left at the next attempt.
    private async Task<int> FailOnceAsync(ReconciliationStack stack, ReconciliationOutcome failure)
    {
        var attemptedAt = stack.Clock.UtcNow;
        Assert.Equal(1, (await stack.RunAsync()).Count(failure));
        var waiting = Assert.Single(await TargetsAsync(fixture, stack.Tenant));
        Assert.Equal("Pending", waiting.State);
        var next = waiting.NextAttemptAtUtc!.Value;

        var calls = stack.Monday.ApiCalls;
        stack.Clock.UtcNow = next.AddSeconds(-1);
        Assert.Equal(0, (await stack.RunAsync()).Claimed);
        Assert.Equal(calls, stack.Monday.ApiCalls);

        stack.Clock.UtcNow = next;
        return (int)Math.Round((next - attemptedAt).TotalSeconds);
    }

    // Authorizes a monday connection through 3B's real flow, or authorizes an existing one again; returns the connection
    // and the access token monday issued for it.
    private static async Task<(Guid Connection, string AccessToken)> AuthorizeAsync(
        MondayConnectionAuthorizer authorizer,
        Core.Identity.Principal principal,
        FakeMonday monday,
        MovingClock clock,
        Guid? reauthorize,
        string marker)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var redirect = (await authorizer.StartAsync(principal, reauthorize, cancellationToken)).Value;
        var state = Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);
        var accessToken = FakeMonday.Jwt(clock.UtcNow.AddHours(1), "access-" + marker);
        monday.ThenToken(FakeMonday.Tokens(accessToken, "refresh-" + marker));
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        Assert.True((await authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "code-" + marker, null, null), cancellationToken)).IsSuccess);
        return (redirect.ConnectionId, accessToken);
    }

    private static (string? Value, DateTimeOffset? SourceUpdatedAt) Current(IReadOnlyList<SnapshotFieldRow> fields)
    {
        var field = Assert.Single(fields);
        return (field.Value, field.SourceUpdatedAtUtc);
    }

    private static (string State, long Generation, string RequestKind) Single(IReadOnlyList<TargetRow> targets)
    {
        var target = Assert.Single(targets);
        return (target.State, target.Generation, target.RequestKind);
    }

    private static (string? Value, long Version) Value(IReadOnlyList<SnapshotFieldRow> fields)
    {
        var field = Assert.Single(fields);
        return (field.Value, field.FieldVersion);
    }

    private static void AssertEvidence(string payload, string item, string board, string label, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(["api_version", "item", "operation"], root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(MondayOperations.ReadItemStatusesById.Name, root.GetProperty("operation").GetString());
        Assert.Equal(MondayApiVersion.Pinned, root.GetProperty("api_version").GetString());
        var observed = root.GetProperty("item");
        Assert.Equal(["board", "column_values", "id", "state"], observed.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal((item, "active", board), (observed.GetProperty("id").GetString(), observed.GetProperty("state").GetString(), observed.GetProperty("board").GetProperty("id").GetString()));
        var column = Assert.Single(observed.GetProperty("column_values").EnumerateArray());
        Assert.Equal(["id", "index", "label", "type", "updated_at"], column.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("status", "status", label, Iso(updatedAt)), (column.GetProperty("id").GetString(), column.GetProperty("type").GetString(), column.GetProperty("label").GetString(), column.GetProperty("updated_at").GetString()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var probe = 0; probe < 400 && !condition(); probe++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition never held.");
    }

    // Holds the item through a webhook and repairs it from the given current state.
    private async Task RepairAsync(ReconciliationStack stack, string item, string label, DateTimeOffset updatedAt)
    {
        await stack.HoldAsync(item);
        stack.Monday.ThenApi(HttpStatusCode.OK, StatusRead(new ItemRead(item, label, updatedAt)));
        Assert.Equal(1, (await stack.RunAsync()).Count(ReconciliationOutcome.Repaired));
        Assert.Equal((label, updatedAt), (Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).Value, Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, stack.Tenant)).SourceUpdatedAtUtc));
    }
}
