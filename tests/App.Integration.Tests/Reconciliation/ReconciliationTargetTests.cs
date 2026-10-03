using Npgsql;
using WorkContinuity.Core.Processing;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;
using static WorkContinuity.Integration.Tests.Reconciliation.ReconciliationData;

namespace WorkContinuity.Integration.Tests.Reconciliation;

/// <summary>
/// Gate 3E's provider-neutral request side, against the real local PostgreSQL with the Gate 2 synthetic source: every
/// <c>ReconciliationRequired</c> outcome writes its fields' targets in the transaction that records it, with the event's
/// source scope; a newer request raises the generation; a target's identity is never replaced; nothing else requests one;
/// and no column of a target can hold a value.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReconciliationTargetTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hold_writes_its_target_with_the_events_scope_and_its_request()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var held = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1"), "evt-held-1")).Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, held.Outcome);
        var target = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal(("1001", "status", "TaskStatus", "scope-1"), (target.ObjectId, target.Field, target.DataType, target.SourceScope));
        Assert.Equal(("Pending", 1L, "Event", (Guid?)held.EventId.Value, 0), (target.State, target.Generation, target.RequestKind, target.RequestedByEventId, target.Attempts));
        Assert.Null(target.ClaimId);
        Assert.Null(target.ResolvedGeneration);

        // A source without a scope requests its target without one.
        var unscoped = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed(null, objectId: "1002"), "evt-held-2")).Envelope);
        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, unscoped.Outcome);
        Assert.Null(Assert.Single(await TargetsAsync(fixture, tenant), row => row.ObjectId == "1002").SourceScope);
    }

    [Fact]
    public async Task A_crash_between_the_hold_and_its_target_commits_neither_and_the_redelivery_commits_both()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1"), "evt-held-1");

        // Another transaction holds the target's key, uncommitted, so the hold's request waits after its ledger entry.
        await using var blocker = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var parked = await blocker.BeginTransactionAsync(Token);
        await using (var occupy = new NpgsqlCommand(
            """
            INSERT INTO reconciliation_targets (tenant_id, object_system, object_connection, object_type, object_id, field, data_type,
                source_scope, state, generation, request_kind, requested_at_utc, attempts, created_at_utc)
            VALUES (@tenant, 'synthetic', @connection, 'task', '1001', 'status', 'TaskStatus', 'scope-1', 'Pending', 1, 'Sweep', now(), 0, now());
            """,
            blocker,
            parked))
        {
            occupy.Parameters.AddWithValue("tenant", tenant.Value);
            occupy.Parameters.AddWithValue("connection", connection.ToString("D"));
            await occupy.ExecuteNonQueryAsync(Token);
        }

        await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
        using var crash = new CancellationTokenSource();
        var dying = host.ProcessAsync(delivered.Envelope, crash.Token);
        Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, blocker.ProcessID, dying));
        await crash.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dying);
        await parked.RollbackAsync(Token);

        // The process died between the two: neither the hold nor its target exists, and the receipt is unsettled.
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await TargetsAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));

        // The redelivery commits both together.
        var held = await host.ProcessSettledAsync(delivered.Envelope);
        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, held.Outcome);
        var target = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal((held.EventId.Value, "scope-1", 1L), (target.RequestedByEventId!.Value, target.SourceScope, target.Generation));
    }

    [Fact]
    public async Task A_newer_hold_raises_the_generation_reopens_the_target_and_resets_its_attempts()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1"), "evt-held-1")).Envelope);
        await AdministerAsync(
            fixture,
            "UPDATE reconciliation_targets SET state = 'Held', attempts = 5, last_outcome = 'reconciliation.object.inactive' WHERE tenant_id = @tenant;",
            ("tenant", tenant.Value));

        var newer = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1", label: "other"), "evt-held-2")).Envelope);

        var target = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal(("Pending", 2L, 0, (Guid?)newer.EventId.Value), (target.State, target.Generation, target.Attempts, target.RequestedByEventId));
        Assert.Null(target.NextAttemptAtUtc);
    }

    [Fact]
    public async Task A_hold_naming_another_scope_or_data_type_never_replaces_the_targets_identity()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1"), "evt-held-1")).Envelope);

        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-2"), "evt-held-2")).Envelope);
        var conflicted = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal(("Held", "scope-1", "TaskStatus", 2L, "reconciliation.scope.conflict"), (conflicted.State, conflicted.SourceScope, conflicted.DataType, conflicted.Generation, conflicted.LastOutcome));

        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1", dataType: "Deadline"), "evt-held-3")).Envelope);
        var retyped = Assert.Single(await TargetsAsync(fixture, tenant));
        Assert.Equal(("Held", "scope-1", "TaskStatus", 3L), (retyped.State, retyped.SourceScope, retyped.DataType, retyped.Generation));

        // A request naming the stored identity again reopens it.
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1"), "evt-held-4")).Envelope);
        Assert.Equal("Pending", Assert.Single(await TargetsAsync(fixture, tenant)).State);
    }

    [Fact]
    public async Task Every_kind_of_hold_requests_reconciliation_and_no_other_outcome_does()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // Accepted, then stale: no request.
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, (await Process(host, tenant, connection, new SyntheticEvent { ObjectId = "2001", UpdatedAt = T0 }, "evt-1")).Outcome);
        Assert.Equal(EventProcessingOutcome.Stale, (await Process(host, tenant, connection, new SyntheticEvent { ObjectId = "2001", UpdatedAt = T0.AddMinutes(-5) }, "evt-2")).Outcome);

        // Quarantined: no request.
        Assert.Equal(
            EventProcessingOutcome.Quarantined,
            (await Process(host, tenant, connection, new SyntheticEvent { ObjectId = "2002", UpdatedAt = DateTimeOffset.UtcNow.AddDays(2) }, "evt-3")).Outcome);
        Assert.Empty(await TargetsAsync(fixture, tenant));

        // Ambiguous order, no ordering evidence, and unconfirmed values: each requests its fields' targets.
        var ambiguous = await Process(host, tenant, connection, new SyntheticEvent { ObjectId = "2001", UpdatedAt = T0, Changes = [new("status", "TaskStatus", null, "other")] }, "evt-4");
        var unevidenced = await Process(host, tenant, connection, new SyntheticEvent { ObjectId = "2003" }, "evt-5");
        var unconfirmed = await Process(host, tenant, connection, Unconfirmed("scope-9", objectId: "2004"), "evt-6");
        Assert.Equal(
            [ProcessingErrors.OrderingAmbiguous.Code, ProcessingErrors.NoOrderingEvidence.Code, ProcessingErrors.UnconfirmedSourceValues.Code],
            new[] { ambiguous, unevidenced, unconfirmed }.Select(result => result.ReasonCode));

        var targets = await TargetsAsync(fixture, tenant);
        Assert.Equal(["2001", "2003", "2004"], targets.Select(target => target.ObjectId));
        Assert.Equal(
            [ambiguous.EventId.Value, unevidenced.EventId.Value, unconfirmed.EventId.Value],
            targets.Select(target => target.RequestedByEventId!.Value));
    }

    [Fact]
    public async Task No_column_of_a_target_can_hold_a_value_and_none_does()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var hint = "hint-" + IngressData.NewMarker();
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Unconfirmed("scope-1", label: hint), "evt-held-1")).Envelope);

        // The table's columns are identifiers, the scope and the lifecycle: nothing named or able to be a value. Gate 3F adds
        // only a scope-set version: the version at which the field awaits a baseline.
        await using (var admin = await fixture.OpenAdministrativeConnectionAsync(Token))
        await using (var columns = new NpgsqlCommand(
            "SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_name = 'reconciliation_targets';",
            admin))
        {
            Assert.Equal(
                "attempts,awaiting_baseline_version,claim_id,claimed_generation,claimed_until_utc,created_at_utc,data_type,field,generation,"
                + "last_attempted_at_utc,last_outcome,next_attempt_at_utc,object_connection,object_id,object_system,object_type,request_kind,"
                + "requested_at_utc,requested_by_event_id,resolved_at_utc,resolved_by_event_id,resolved_generation,source_scope,state,tenant_id,"
                + "verified_at_utc",
                (string)(await columns.ExecuteScalarAsync(Token))!);
        }

        // The hint is in the raw receipt, its only copy; never in a target.
        Assert.Equal(["raw_receipts.payload holds sentinel #1"], await WebhookData.FindAnywhereAsync(fixture, [hint], Token));
    }

    private static SyntheticEvent Unconfirmed(string? scope, string objectId = "1001", string label = "hint", string dataType = "TaskStatus") =>
        new()
        {
            ObjectId = objectId,
            UpdatedAt = T0,
            ValueConfirmation = "Unconfirmed",
            SourceScope = scope,
            Changes = [new SyntheticChange("status", dataType, null, label)],
        };

    private async Task<EventProcessingResult> Process(TransportHost host, Core.Tenancy.TenantId tenant, Guid connection, SyntheticEvent synthetic, string sourceEventId) =>
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, synthetic, sourceEventId)).Envelope);
}
