using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>
/// Gate 2E Snapshot projection against the real local PostgreSQL, through the runtime role. Only an accepted
/// result's advanced fields become facts, with exactly the provenance 2D accepted them on; versions move once per
/// accepted event and never otherwise; a multi-field event is atomic; crashes before and after the commit
/// converge; tenants never see each other's facts; and nothing of an event but the fact itself is kept.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SnapshotProjectionTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    // A processing clock later than the receipt, so the observed and processed times are told apart.
    private static readonly DateTimeOffset Later = IngressHost.Now.AddMinutes(3);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_accepted_field_creates_the_object_and_the_fact_at_version_one()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var accepted = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 7,
            UpdatedAt = T0,
            OccurredAt = T0.AddMinutes(-2),
            Changes = [new("status", "TaskStatus", null, "done")],
        }, "evt-first")).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, accepted.Outcome);
        Assert.Equal(
            new SnapshotObjectRow("1001", 1, accepted.EventId.Value, IngressHost.Now),
            Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)));
        Assert.Equal(
            new SnapshotFieldRow(
                "1001", "status", "TaskStatus", "done", "SourceTaskSystem", "SaasConnector", SyntheticSource.System, ConnectionRecord.ReferenceFor(connection),
                accepted.EventId.Value, "evt-first", 7, T0, T0.AddMinutes(-2), IngressHost.Now, IngressHost.Now, FieldVersion: 1, ObjectVersion: 1),
            Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)));
    }

    [Fact]
    public async Task Every_fact_carries_exactly_the_ledger_entry_and_the_ordering_evidence_it_was_accepted_on()
    {
        await using var ingress = ProcessingHarness.Host(fixture, new InMemoryTransport());
        await using var processing = LaterHost();
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // Revisions, source update times and occurrence times; a multi-field event with a stale field; no provider id.
        await processing.ProcessSettledAsync((await ingress.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            UpdatedAt = T0,
            Changes = [new("status", "TaskStatus", null, "working"), new("due_date", "Deadline", null, "2026-10-01")],
        }, "evt-p1")).Envelope);
        await processing.ProcessSettledAsync((await ingress.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 3,
            Changes = [new("due_date", "Deadline", "2026-10-01", "2026-10-03")],
        }, "evt-p3")).Envelope);
        var mixed = await processing.ProcessSettledAsync((await ingress.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            OccurredAt = T0.AddMinutes(1),
            Changes = [new("status", "TaskStatus", "working", "done"), new("due_date", "Deadline", "2026-10-03", "2026-10-02")],
        }, "evt-p2")).Envelope);
        await processing.ProcessSettledAsync((await ingress.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            ObjectId = "2002",
            UpdatedAt = T0.AddMinutes(5),
            OccurredAt = T0.AddMinutes(4),
        }, sourceEventId: null)).Envelope);

        Assert.Equal(
            [("status", FieldOrderingDecision.Advance), ("due_date", FieldOrderingDecision.Stale)],
            mixed.Fields.Select(field => (field.Field, field.Decision)));

        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, fact => Assert.Equal((IngressHost.Now, Later), (fact.ObservedAtUtc, fact.ProcessedAtUtc)));
        Assert.Equal(facts.Count, await CountFactsMatchingTheirLedgerAndOrderingEvidenceAsync(tenant));
    }

    [Fact]
    public async Task A_newer_accepted_change_replaces_the_fact_and_advances_both_versions()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Changes = [new("status", "TaskStatus", null, "working")],
        }, "evt-n1")).Envelope);

        var newer = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            Changes = [new("status", "TaskStatus", "working", "done")],
        }, "evt-n2")).Envelope);

        Assert.Equal((2L, newer.EventId.Value), Project(Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant))));
        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("done", newer.EventId.Value, "evt-n2", 2L, 2L, 2L), (fact.Value, fact.LastEventId, fact.SourceEventId, fact.SourceRevision, fact.FieldVersion, fact.ObjectVersion));
    }

    [Fact]
    public async Task A_multi_field_event_projects_every_advanced_field_at_one_object_version()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        SyntheticChange[] fields =
        [
            new("status", "TaskStatus", null, "working"),
            new("due_date", "Deadline", null, "2026-10-01"),
            new("start_date", "Deadline", null, "2026-09-29"),
        ];
        var first = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, Changes = fields }, "evt-m1")).Envelope);
        var second = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            Changes = [.. fields.Select(field => field with { Before = field.After, After = field.After + "-v2" })],
        }, "evt-m2")).Envelope);

        Assert.Equal(3, first.Fields.Count);
        Assert.Equal((2L, second.EventId.Value), Project(Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant))));
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Equal(["due_date", "start_date", "status"], facts.Select(fact => fact.Field));
        Assert.All(facts, fact =>
        {
            Assert.Equal((second.EventId.Value, 2L, 2L), (fact.LastEventId, fact.FieldVersion, fact.ObjectVersion));
            Assert.EndsWith("-v2", fact.Value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Stale_fields_of_an_accepted_event_keep_their_fact_provenance_and_version()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-s1")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 3,
            Changes = [new("due_date", "Deadline", null, "2026-10-01")],
        }, "evt-d3")).Envelope);
        var dueBefore = (await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)).Single(fact => fact.Field == "due_date");

        var mixed = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            Changes = [new("status", "TaskStatus", "working", "blocked"), new("due_date", "Deadline", "2026-10-01", "2026-09-30")],
        }, "evt-mixed-2")).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, mixed.Outcome);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Equal(dueBefore, facts.Single(fact => fact.Field == "due_date"));
        var status = facts.Single(fact => fact.Field == "status");
        Assert.Equal(("blocked", mixed.EventId.Value, 2L, 3L), (status.Value, status.LastEventId, status.FieldVersion, status.ObjectVersion));
        Assert.Equal(3L, Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).Version);
    }

    [Fact]
    public async Task Stale_ambiguous_and_unevidenced_events_leave_the_snapshot_exactly_as_it_was()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 5 }, "evt-u5")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "3003", UpdatedAt = T0 }, "evt-u-time")).Envelope);
        var before = await SnapshotAsync(tenant);

        var stale = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 3,
            Changes = [new("status", "TaskStatus", "done", "working")],
        }, "evt-u3")).Envelope);
        var ambiguous = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            ObjectId = "3003",
            UpdatedAt = T0,
            Changes = [new("status", "TaskStatus", "done", "reopened")],
        }, "evt-u-ambiguous")).Envelope);
        var unevidenced = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "4004" }, "evt-u-none")).Envelope);

        Assert.Equal(
            [(EventProcessingOutcome.Stale, null), (EventProcessingOutcome.ReconciliationRequired, ProcessingErrors.OrderingAmbiguous.Code), (EventProcessingOutcome.ReconciliationRequired, ProcessingErrors.NoOrderingEvidence.Code)],
            new[] { stale, ambiguous, unevidenced }.Select(result => (result.Outcome, result.ReasonCode)));
        Assert.False(ambiguous.TryGetProjection(out _));
        await AssertSnapshotIsAsync(before, tenant);
    }

    [Fact]
    public async Task Quarantined_events_never_reach_the_snapshot()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var cases = new (Delivered Delivered, QuarantineReason Reason, string Code)[]
        {
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                ObjectId = "5001",
                Revision = 1,
                Changes = [new("status", "TaskStatus", "working", "done"), new("active_app", "PcActivity", null, "editor")],
            }, "evt-q-authority"), QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                ObjectId = "5002",
                Revision = 1,
                Home = (SyntheticSource.System, ConnectionRecord.ReferenceFor(Guid.CreateVersion7())),
            }, "evt-q-foreign"), QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                ObjectId = "5003",
                Revision = 1,
                UpdatedAt = IngressHost.Now.AddHours(1),
            }, "evt-q-future"), QuarantineReason.ImpossibleOrdering, ProcessingErrors.SourceTimeAfterReceipt.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                ObjectId = "5004",
                Revision = 1,
                Changes = [new("status", "TaskStatus", null, new string('v', EventText.MaxValueLength + 1))],
            }, "evt-q-oversized"), QuarantineReason.BadSchema, CanonicalEventErrors.InvalidValue.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                ObjectId = "5005",
                Revision = 1,
                Changes = [new("status", "TaskStatus", null, "done\nand more")],
            }, "evt-q-multiline"), QuarantineReason.BadSchema, CanonicalEventErrors.InvalidValue.Code),
            (await host.SubmitRawAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "5006", Revision = 1 }.ToPayload(), "evt-q-v9", schemaVersion: "9"),
                QuarantineReason.UnknownSchemaVersion, ProcessingErrors.NormalizerMissing.Code),
        };

        foreach (var (delivered, reason, code) in cases)
        {
            var result = await host.ProcessSettledAsync(delivered.Envelope);
            Assert.True(EventProcessingOutcome.Quarantined == result.Outcome, code);
            Assert.True(reason == result.QuarantineReason, code);
            Assert.Equal(code, result.ReasonCode);
            Assert.False(result.TryGetProjection(out _));
        }

        Assert.Equal(cases.Length, (await ProcessingHarness.LedgerAsync(fixture, tenant)).Count);
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
    }

    [Fact]
    public async Task Redelivering_an_accepted_event_changes_nothing_and_advances_no_version()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-redelivered");
        var first = await host.ProcessSettledAsync(delivered.Envelope);
        var projected = await SnapshotAsync(tenant);

        var again = await host.ProcessSettledAsync(delivered.Envelope);
        var settlement = await host.Consumer.ConsumeAsync(ProcessingHarness.Received(delivered.Envelope, deliveryCount: 3), Token);

        Assert.Equal((EventProcessingOutcome.Duplicate, EventProcessingOutcome.AcceptedForProjection), (again.Outcome, again.PreviousOutcome));
        Assert.False(again.TryGetProjection(out _));
        Assert.Equal(TransportSettlementKind.Complete, settlement.Kind);
        await AssertSnapshotIsAsync(projected, tenant);
        Assert.Equal((1L, first.EventId.Value), Project(Assert.Single(projected.Objects)));
    }

    [Fact]
    public async Task A_cleared_field_and_a_repeated_value_are_new_versions_of_the_fact()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-c1")).Envelope);

        var repeated = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 2 }, "evt-c2")).Envelope);
        var afterRepeat = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("done", repeated.EventId.Value, 2L, 2L), (afterRepeat.Value, afterRepeat.LastEventId, afterRepeat.SourceRevision, afterRepeat.FieldVersion));

        var cleared = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 3,
            Changes = [new("status", "TaskStatus", "done", null)],
        }, "evt-c3")).Envelope);
        var afterClear = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Null(afterClear.Value);
        Assert.Equal((cleared.EventId.Value, 3L, 3L, 3L), (afterClear.LastEventId, afterClear.SourceRevision, afterClear.FieldVersion, afterClear.ObjectVersion));
    }

    [Fact]
    public async Task An_unresolved_actor_neither_changes_the_fact_nor_is_kept_with_it()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();

        var withActor = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            ObjectId = "6001",
            Revision = 1,
            Actor = "u-unmapped-" + marker,
        }, "evt-actor")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "6002", Revision = 1 }, "evt-no-actor")).Envelope);

        Assert.True(withActor.TryGetProjection(out var handoff));
        Assert.False(handoff!.Event.Content.Actor!.IsResolved);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        var neutral = facts.Select(fact => fact with { ObjectId = string.Empty, LastEventId = Guid.Empty, SourceEventId = null }).Distinct().ToList();
        Assert.Single(neutral);
        Assert.Equal((0L, 0L, 0L), await CountInSnapshotAsync(marker));
    }

    [Fact]
    public async Task No_payload_can_make_itself_the_source_or_the_authority_of_a_fact()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            ObjectId = "7001",
            Revision = 1,
            ClaimedSource = "ai-model",
        }, "evt-claims-ai")).Envelope);
        var claimsEmployee = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            ObjectId = "7002",
            Revision = 1,
            ClaimedSource = "employee",
            Changes = [new("approval", "ApprovalDecision", null, "approved")],
        }, "evt-claims-employee")).Envelope);

        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("7001", "SaasConnector", "SourceTaskSystem"), (fact.ObjectId, fact.SourceKind, fact.Authority));
        Assert.Equal(QuarantineReason.AuthorityConflict, claimsEmployee.QuarantineReason);
        Assert.Equal("7001", Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).ObjectId);
    }

    [Fact]
    public async Task The_snapshot_keeps_the_fact_itself_and_nothing_else_of_the_event()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var fact = IngressData.NewMarker();
        var content = IngressData.NewMarker();

        var result = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Actor = "actor-" + content,
            Reference = "reference-" + content,
            ClaimedSource = "claimed-" + content,
            Changes = [new("status", "TaskStatus", "before-" + content, "after-" + fact)],
        }, "evt-content-2e")).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);
        Assert.Equal((0L, 1L, 1L), await CountInSnapshotAsync(fact));
        Assert.Equal((0L, 0L, 0L), await CountInSnapshotAsync(content));
        Assert.Equal("after-" + fact, Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)).Value);

        // Only the expiring receipt still holds the rest of the event: that is raw payload retention, not Snapshot.
        Assert.Equal(1L, await IngressData.CountStoredContentAsync(fixture, content, Token));
    }

    [Fact]
    public async Task Projection_logs_carry_ids_and_the_version_but_never_a_value()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();

        var result = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Changes = [new("status", "TaskStatus", "before-" + marker, "after-" + marker)],
        }, "evt-log-2e")).Envelope);

        Assert.Contains(host.Logs.Entries, entry =>
            entry.Text.Contains(result.EventId.Value.ToString("D"), StringComparison.Ordinal)
            && entry.Text.Contains("Snapshot object version 1", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tenants_keep_separate_facts_and_versions_for_the_same_object()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1, Changes = [new("status", "TaskStatus", null, "a-working")] }, "evt-shared-1")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 2, Changes = [new("status", "TaskStatus", "a-working", "a-done")] }, "evt-shared-2")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantB, connectionB, new SyntheticEvent { Revision = 1, Changes = [new("status", "TaskStatus", null, "b-working")] }, "evt-shared-1")).Envelope);

        var (versionsA, valuesA) = await VisibleSnapshotAsync(tenantA);
        var (versionsB, valuesB) = await VisibleSnapshotAsync(tenantB);
        Assert.Equal([2L], versionsA);
        Assert.Equal(["a-done"], valuesA);
        Assert.Equal([1L], versionsB);
        Assert.Equal(["b-working"], valuesB);
    }

    [Fact]
    public async Task A_forged_message_naming_another_tenants_receipt_projects_nothing_anywhere()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, _) = await IngressData.NewConnectionAsync(fixture, Token);
        var deliveredA = await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-forged-2e");
        var forged = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), tenantB, IngressMessageTypes.ReceiptReceived, TransportData.Marker(deliveredA.ReceiptId), CorrelationId.New()).Value;

        var processed = await host.ProcessAsync(forged, Token);

        Assert.Equal(ProcessingErrors.ReceiptNotVisible, processed.Error);
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenantA));
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenantB));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenantB));
    }

    [Fact]
    public async Task A_crash_at_the_commit_after_the_snapshot_was_written_leaves_nothing_and_redelivery_projects_once()
    {
        var crash = new CrashAtCommit();
        await using var crashing = ProcessingHarness.Host(fixture, new InMemoryTransport(), services =>
        {
            services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(crash));
        });
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await crashing.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Changes = [new("status", "TaskStatus", null, "working"), new("due_date", "Deadline", null, "2026-10-01")],
        }, "evt-crash-at-commit");

        crash.Arm();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crashing.ProcessAsync(delivered.Envelope, Token));

        // The object and both facts had been written inside the transaction when the process died at the commit.
        Assert.Equal(3L, crash.SnapshotRowsSeenAtCommit);

        // None of it survived, and neither did the decision it belonged to.
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));

        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var redelivered = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, redelivered.Outcome);
        Assert.Equal((1L, redelivered.EventId.Value), Project(Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant))));
        Assert.All(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant), fact => Assert.Equal((1L, 1L), (fact.FieldVersion, fact.ObjectVersion)));
    }

    [Fact]
    public async Task A_crash_after_the_commit_and_before_completion_leaves_the_snapshot_and_its_versions_unchanged()
    {
        var transport = new InMemoryTransport();
        await using var host = ProcessingHarness.Host(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-crash-after-commit-2e");
        var wire = delivered.Envelope.ToWire();
        transport.SendRaw(wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DeliverNextAsync(new CrashAfterConsuming(host.Consumer), Token));
        var projected = await SnapshotAsync(tenant);
        Assert.Equal(1L, Assert.Single(projected.Objects).Version);

        var redelivery = Assert.IsType<TransportDelivery>(await transport.DeliverNextAsync(host.Consumer, Token));

        Assert.Equal((2, TransportSettlementKind.Complete), (redelivery.Message.DeliveryCount, redelivery.Settlement.Kind));
        await AssertSnapshotIsAsync(projected, tenant);
    }

    [Fact]
    public async Task Concurrent_events_on_different_fields_of_one_object_take_distinct_gap_free_versions()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var deliveries = new List<Delivered>();
        for (var index = 1; index <= 5; index++)
        {
            deliveries.Add(await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                Revision = 1,
                Changes = [new($"field_{index}", "TaskStatus", null, $"value-{index}")],
            }, $"evt-fields-{index}"));
        }

        // Every delivery is retried until it settles, as the transport would; lost races only ever retry.
        await Task.WhenAll(deliveries.Select(delivered => Task.Run(
            async () =>
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    if ((await host.ProcessAsync(delivered.Envelope, Token)).IsSuccess)
                    {
                        return;
                    }
                }

                Assert.Fail("A delivery never settled.");
            },
            Token)));

        Assert.Equal(5L, Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).Version);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], facts.Select(fact => fact.ObjectVersion).Order());
        Assert.All(facts, fact => Assert.Equal(1L, fact.FieldVersion));
    }

    [Fact]
    public async Task An_object_being_projected_stays_locked_until_commit_so_no_version_is_lost()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var first = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-object-1")).Envelope);
        var status = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 2 }, "evt-object-status");
        var due = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Changes = [new("due_date", "Deadline", null, "2026-10-01")],
        }, "evt-object-due");

        // The status event is parked after it projected, holding the object; the due-date event, on another field,
        // must wait for the object instead of reading the version the status event is about to replace.
        await using var blocker = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var parked = await blocker.BeginTransactionAsync(Token);
        await ProcessingHarness.ParkLedgerInsertAsync(blocker, parked, first.EventId.Value, status, "evt-object-status");

        await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
        var statusTask = host.ProcessAsync(status.Envelope, Token);
        var statusPid = Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, blocker.ProcessID));
        var dueTask = host.ProcessAsync(due.Envelope, Token);
        await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, statusPid, dueTask);
        await parked.RollbackAsync(Token);

        var results = await Task.WhenAll(statusTask, dueTask);
        Assert.All(results, result => Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Value.Outcome));
        Assert.Equal(3L, Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).Version);
        Assert.Equal(
            [("due_date", 3L), ("status", 2L)],
            (await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)).Select(fact => (fact.Field, fact.ObjectVersion)));
    }

    [Fact]
    public async Task A_snapshot_that_disagrees_with_the_ordering_state_is_never_overwritten()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-guard-1")).Envelope);
        var elsewhere = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { ObjectId = "8008", Revision = 1 }, "evt-guard-other")).Envelope);

        // The fact of object 1001 now names an event 2D did not accept for it: the Snapshot and the ordering state disagree.
        await IngressData.ExecuteAdministrativeAsync(
            fixture,
            $"UPDATE snapshot_fields SET last_event_id = '{elsewhere.EventId.Value}' WHERE tenant_id = '{tenant.Value}' AND object_id = '1001';",
            Token);
        var diverged = await SnapshotAsync(tenant);
        var newer = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 2 }, "evt-guard-2");

        var processed = await host.ProcessAsync(newer.Envelope, Token);

        Assert.Equal(ProcessingErrors.Unavailable, processed.Error);
        await AssertSnapshotIsAsync(diverged, tenant);
        Assert.DoesNotContain(await ProcessingHarness.LedgerAsync(fixture, tenant), entry => entry.ReceiptId == newer.ReceiptId);
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, newer.ReceiptId));
    }

    private static (long Version, Guid LastEventId) Project(SnapshotObjectRow row) => (row.Version, row.LastEventId);

    private TransportHost LaterHost() =>
        ProcessingHarness.Host(fixture, new InMemoryTransport(), services =>
        {
            services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            services.AddSingleton<IClock>(new FixedClock(Later));
        });

    private async Task<(IReadOnlyList<SnapshotObjectRow> Objects, IReadOnlyList<SnapshotFieldRow> Fields)> SnapshotAsync(TenantId tenant)
    {
        var objects = await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant);
        var fields = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        return (objects, fields);
    }

    // Row by row: every value, every piece of provenance and every version is exactly what it was.
    private async Task AssertSnapshotIsAsync((IReadOnlyList<SnapshotObjectRow> Objects, IReadOnlyList<SnapshotFieldRow> Fields) expected, TenantId tenant)
    {
        var actual = await SnapshotAsync(tenant);
        Assert.NotEmpty(expected.Objects);
        Assert.Equal(expected.Objects, actual.Objects);
        Assert.Equal(expected.Fields, actual.Fields);
    }

    private async Task<(long[] Versions, string?[] Values)> VisibleSnapshotAsync(TenantId tenant)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var visible = await database.ExecuteAsync(
                async (dbContext, token) => (
                    Versions: await dbContext.SnapshotObjects.AsNoTracking().Select(row => row.Version).ToArrayAsync(token),
                    Values: await dbContext.SnapshotFields.AsNoTracking().Select(row => row.Value).ToArrayAsync(token)),
                Token);

            Assert.True(visible.IsSuccess);
            return visible.Value;
        }
    }

    private async Task<long> CountFactsMatchingTheirLedgerAndOrderingEvidenceAsync(TenantId tenant)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM snapshot_fields s
            JOIN event_ledger l ON l.tenant_id = s.tenant_id AND l.event_id = s.last_event_id
            JOIN event_ledger_fields f ON f.tenant_id = s.tenant_id AND f.event_id = s.last_event_id AND f.field = s.field
            JOIN field_ordering_states o ON o.tenant_id = s.tenant_id AND o.object_system = s.object_system
                AND o.object_connection = s.object_connection AND o.object_type = s.object_type
                AND o.object_id = s.object_id AND o.field = s.field
            JOIN snapshot_objects so ON so.tenant_id = s.tenant_id AND so.object_system = s.object_system
                AND so.object_connection = s.object_connection AND so.object_type = s.object_type AND so.object_id = s.object_id
            WHERE s.tenant_id = @tenant
              AND l.outcome = 'AcceptedForProjection' AND f.decision = 'Advance' AND f.data_type = s.data_type
              AND o.last_event_id = s.last_event_id
              AND (l.object_system, l.object_connection, l.object_type, l.object_id) = (s.object_system, s.object_connection, s.object_type, s.object_id)
              AND (l.source_kind, l.source_system, l.connection_reference) = (s.source_kind, s.source_system, s.connection_reference)
              AND l.source_event_id IS NOT DISTINCT FROM s.source_event_id AND o.source_event_id IS NOT DISTINCT FROM s.source_event_id
              AND l.source_revision IS NOT DISTINCT FROM s.source_revision AND o.source_revision IS NOT DISTINCT FROM s.source_revision
              AND l.source_updated_at_utc IS NOT DISTINCT FROM s.source_updated_at_utc
              AND o.source_updated_at_utc IS NOT DISTINCT FROM s.source_updated_at_utc
              AND l.occurred_at_utc IS NOT DISTINCT FROM s.occurred_at_utc AND o.occurred_at_utc IS NOT DISTINCT FROM s.occurred_at_utc
              AND l.received_at_utc = s.observed_at_utc AND l.processed_at_utc = s.processed_at_utc
              AND s.object_version <= so.version;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<(long Objects, long Fields, long Values)> CountInSnapshotAsync(string marker)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM snapshot_objects o WHERE strpos(o::text, @marker) > 0),
                   (SELECT count(*) FROM snapshot_fields f WHERE strpos(f::text, @marker) > 0),
                   (SELECT count(*) FROM snapshot_fields f WHERE strpos(f.value, @marker) > 0);
            """,
            connection);
        command.Parameters.AddWithValue("marker", marker);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>A consumer that processes and commits, then dies before its settlement can be applied.</summary>
    private sealed class CrashAfterConsuming(IEventTransportConsumer inner) : IEventTransportConsumer
    {
        public async Task<TransportSettlement> ConsumeAsync(ReceivedTransportMessage message, CancellationToken cancellationToken = default)
        {
            await inner.ConsumeAsync(message, cancellationToken);
            throw new InvalidOperationException("The process died after the commit.");
        }
    }

    /// <summary>
    /// Kills the process at the next commit once armed, after counting what the transaction wrote to the Snapshot,
    /// so the crash provably falls after the projection and before anything is durable.
    /// </summary>
    private sealed class CrashAtCommit : DbTransactionInterceptor
    {
        private int _armed;

        public long SnapshotRowsSeenAtCommit { get; private set; } = -1;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                await using var command = transaction.Connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT (SELECT count(*) FROM snapshot_objects) + (SELECT count(*) FROM snapshot_fields);";
                SnapshotRowsSeenAtCommit = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                throw new OperationCanceledException("The process died at the commit.");
            }

            return result;
        }
    }
}
