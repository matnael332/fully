using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>
/// Gate 2D processing pipeline against the real local PostgreSQL, through the runtime role: first-time events
/// are accepted exactly once; duplicates are deterministic no-ops; ordering follows the frozen precedence per
/// field and never guesses; impossible order and authority conflicts are quarantined whole; tenant and forged
/// messages stay isolated; crashes before and after the commit converge; and the ledger holds no content.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventProcessingTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_time_event_is_accepted_exactly_once_with_its_ledger_entry_and_ordering_state()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 1, UpdatedAt = T0, OccurredAt = T0.AddSeconds(-5) }, "evt-first");

        var result = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);
        Assert.Equal(EventIdentity.ForReceipt(delivered.ReceiptId), result.EventId);
        Assert.True(result.TryGetProjection(out var handoff));

        // Every trusted part was set by the pipeline from the receipt and the envelope.
        var metadata = handoff!.Event.Metadata;
        Assert.Equal(tenant, metadata.TenantId);
        Assert.Equal(delivered.Correlation, metadata.CorrelationId);
        Assert.Equal(delivered.ReceiptId, metadata.CausationId);
        Assert.Equal(new EventSource(EventSourceKind.SaasConnector, SyntheticSource.System, ConnectionRecord.ReferenceFor(connection)), metadata.Source);
        Assert.Equal("evt-first", metadata.SourceEventId);
        Assert.Equal(IngressHost.Now, metadata.ReceivedAtUtc);
        Assert.Equal(1, metadata.SourceRevision);
        Assert.Equal(["status"], handoff.AdvancedFields.Select(field => field.Field));

        var entry = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Equal(result.EventId.Value, entry.EventId);
        Assert.Equal(delivered.ReceiptId, entry.ReceiptId);
        Assert.Equal("AcceptedForProjection", entry.Outcome);
        Assert.Equal("SaasConnector", entry.SourceKind);
        Assert.Equal("evt-first", entry.SourceEventId);
        Assert.Equal("1001", entry.ObjectId);
        Assert.Equal(1L, entry.SourceRevision);
        Assert.Equal(delivered.Correlation.Value, entry.CorrelationId);
        Assert.Null(entry.QuarantineReason);
        Assert.Null(entry.ReasonCode);
        Assert.Equal([("status", "TaskStatus", "Advance")], await ProcessingHarness.LedgerFieldsAsync(fixture, entry.EventId));

        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal("status", state.Field);
        Assert.Equal(entry.EventId, state.LastEventId);
        Assert.Equal(1L, state.SourceRevision);
        Assert.Equal("Processed", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));
    }

    [Fact]
    public async Task An_exact_duplicate_delivery_is_a_deterministic_no_op()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, UpdatedAt = T0 }, "evt-dup-delivery");

        var first = await host.ProcessSettledAsync(delivered.Envelope);
        var stateBefore = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        var second = await host.ProcessSettledAsync(delivered.Envelope);
        var settlement = await host.Consumer.ConsumeAsync(ProcessingHarness.Received(delivered.Envelope, deliveryCount: 3), Token);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, first.Outcome);
        Assert.Equal(EventProcessingOutcome.Duplicate, second.Outcome);
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, second.PreviousOutcome);
        Assert.Equal(first.EventId, second.EventId);
        Assert.False(second.TryGetProjection(out _));
        Assert.Equal(TransportSettlementKind.Complete, settlement.Kind);

        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Equal(stateBefore, Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant)));
    }

    [Fact]
    public async Task A_repeated_provider_event_id_is_never_processed_twice()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, UpdatedAt = T0 }, "evt-repeated");

        // The repeat never becomes a second receipt or message (2B), so it can never be processed twice.
        var repeat = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-repeated", new SyntheticEvent { Revision = 1, UpdatedAt = T0 }.ToPayload()));
        Assert.Equal(IngressStatus.Duplicate, repeat.Status);
        Assert.Equal(delivered.ReceiptId, repeat.ReceiptId);
        await host.ProcessSettledAsync(delivered.Envelope);

        // And the ledger refuses a second entry with the same provider event id on the same connection.
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var second = await database.ExecuteAsync(
                (dbContext, _) =>
                {
                    var fabricated = RawReceiptRecord.Receive(
                        Guid.CreateVersion7(), tenant, ConnectionRecord.ReferenceFor(connection), SyntheticSource.System,
                        "evt-repeated", SyntheticSource.SchemaVersion, IngressHost.Now, "{}", "fingerprint");
                    dbContext.EventLedger.Add(EventLedgerRecord.ForUnnormalizedReceipt(
                        EventIdentity.ForReceipt(fabricated.ReceiptId), fabricated, EventSourceKind.SaasConnector, IngressHost.Now,
                        CorrelationId.New(), QuarantineReason.BadSchema, "test.duplicate"));
                    return Task.CompletedTask;
                },
                Token);

            Assert.True(second.IsFailure);
            Assert.Equal(PersistenceErrors.Duplicate, second.Error);
        }

        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
    }

    [Fact]
    public async Task Identical_content_without_a_provider_event_id_is_only_flagged_as_a_duplicate_candidate()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var synthetic = new SyntheticEvent { UpdatedAt = T0 };

        var first = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, synthetic, sourceEventId: null)).Envelope);
        var second = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, synthetic, sourceEventId: null)).Envelope);
        var different = await host.ProcessSettledAsync(
            (await host.SubmitAsync(fixture, tenant, connection, synthetic with { UpdatedAt = T0.AddMinutes(1) }, sourceEventId: null)).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, first.Outcome);
        Assert.Null(first.DuplicateCandidateOf);

        // Not discarded: recorded, flagged with the earlier event, and, since its order cannot be decided
        // against the same evidence, held for reconciliation instead of guessed.
        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, second.Outcome);
        Assert.Equal(first.EventId, second.DuplicateCandidateOf);
        Assert.Equal(ProcessingErrors.OrderingAmbiguous.Code, second.ReasonCode);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, different.Outcome);
        Assert.Null(different.DuplicateCandidateOf);

        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        Assert.Equal(3, ledger.Count);
        Assert.Equal(first.EventId.Value, ledger.Single(row => row.EventId == second.EventId.Value).DuplicateCandidateOf);
        Assert.Equal(different.EventId.Value, Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant)).LastEventId);
    }

    [Fact]
    public async Task A_delayed_older_revision_never_overwrites_the_newer_one()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var newer = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 5, UpdatedAt = T0 }, "evt-rev-5")).Envelope);

        var older = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 3, UpdatedAt = T0.AddMinutes(9) }, "evt-rev-3")).Envelope);

        Assert.Equal(EventProcessingOutcome.Stale, older.Outcome);
        Assert.False(older.TryGetProjection(out _));
        var field = Assert.Single(older.Fields);
        Assert.Equal(FieldOrderingDecision.Stale, field.Decision);
        Assert.Equal(5L, field.Previous!.SourceRevision);
        Assert.Equal(newer.EventId, field.PreviousEventId);

        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal(newer.EventId.Value, state.LastEventId);
        Assert.Equal(5L, state.SourceRevision);
        Assert.Equal([("status", "TaskStatus", "Stale")], await ProcessingHarness.LedgerFieldsAsync(fixture, older.EventId.Value));
    }

    [Fact]
    public async Task A_delayed_older_source_update_time_never_overwrites_the_newer_one()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var newer = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { UpdatedAt = T0.AddMinutes(10) }, "evt-late")).Envelope);

        var older = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { UpdatedAt = T0.AddMinutes(5) }, "evt-early")).Envelope);

        Assert.Equal(EventProcessingOutcome.Stale, older.Outcome);
        Assert.Equal(newer.EventId.Value, Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant)).LastEventId);
    }

    [Fact]
    public async Task An_event_newer_for_one_field_and_older_for_another_advances_only_the_newer_field()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var status1 = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-s1")).Envelope);
        var due3 = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 3,
            Changes = [new("due_date", "Deadline", null, "2026-10-01")],
        }, "evt-d3")).Envelope);

        var mixed = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            Changes = [new("status", "TaskStatus", "working", "done"), new("due_date", "Deadline", "2026-10-01", "2026-09-30")],
        }, "evt-mixed-2")).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, mixed.Outcome);
        Assert.Equal(
            [("status", FieldOrderingDecision.Advance), ("due_date", FieldOrderingDecision.Stale)],
            mixed.Fields.Select(field => (field.Field, field.Decision)));
        Assert.True(mixed.TryGetProjection(out var handoff));
        Assert.Equal(["status"], handoff!.AdvancedFields.Select(field => field.Field));

        var states = await ProcessingHarness.FieldStatesAsync(fixture, tenant);
        Assert.Equal(due3.EventId.Value, states.Single(state => state.Field == "due_date").LastEventId);
        Assert.Equal(mixed.EventId.Value, states.Single(state => state.Field == "status").LastEventId);
        Assert.NotEqual(status1.EventId.Value, states.Single(state => state.Field == "status").LastEventId);
    }

    [Fact]
    public async Task Ambiguous_ordering_advances_nothing_and_requires_reconciliation()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var accepted = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { UpdatedAt = T0 }, "evt-a")).Envelope);

        // Same source update time, different value, and a second field that on its own would advance.
        var ambiguous = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            UpdatedAt = T0,
            Changes = [new("status", "TaskStatus", "working", "blocked"), new("due_date", "Deadline", null, "2026-10-02")],
        }, "evt-b")).Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, ambiguous.Outcome);
        Assert.Equal(ProcessingErrors.OrderingAmbiguous.Code, ambiguous.ReasonCode);
        Assert.False(ambiguous.TryGetProjection(out _));
        Assert.Equal(
            [("status", FieldOrderingDecision.Ambiguous), ("due_date", FieldOrderingDecision.Withheld)],
            ambiguous.Fields.Select(field => (field.Field, field.Decision)));

        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal(accepted.EventId.Value, state.LastEventId);
        var entry = (await ProcessingHarness.LedgerAsync(fixture, tenant)).Single(row => row.EventId == ambiguous.EventId.Value);
        Assert.Equal(("ReconciliationRequired", ProcessingErrors.OrderingAmbiguous.Code), (entry.Outcome, entry.ReasonCode));
        Assert.Equal("Processed", await ProcessingHarness.ReceiptStateAsync(fixture, entry.ReceiptId));
    }

    [Fact]
    public async Task An_event_without_ordering_evidence_is_held_for_reconciliation_and_receipt_time_never_orders()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var unevidenced = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent(), "evt-no-evidence")).Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, unevidenced.Outcome);
        Assert.Equal(ProcessingErrors.NoOrderingEvidence.Code, unevidenced.ReasonCode);
        Assert.All(unevidenced.Fields, field => Assert.Equal(FieldOrderingDecision.Withheld, field.Decision));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
    }

    [Fact]
    public async Task Impossible_ordering_is_quarantined()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var accepted = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 4 }, "evt-rev-4-a")).Envelope);

        var future = await host.ProcessSettledAsync((await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 9, UpdatedAt = IngressHost.Now.AddHours(1) }, "evt-future")).Envelope);
        var afterUpdate = await host.ProcessSettledAsync((await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 9, UpdatedAt = T0, OccurredAt = T0.AddMinutes(10) }, "evt-after-update")).Envelope);
        var claimedTwice = await host.ProcessSettledAsync((await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 4, Changes = [new("status", "TaskStatus", "working", "archived")] }, "evt-rev-4-b")).Envelope);

        Assert.Equal(
            [ProcessingErrors.SourceTimeAfterReceipt.Code, ProcessingErrors.OccurredAfterUpdate.Code, ProcessingErrors.RevisionClaimedTwice.Code],
            new[] { future, afterUpdate, claimedTwice }.Select(result => result.ReasonCode));
        foreach (var result in new[] { future, afterUpdate, claimedTwice })
        {
            Assert.Equal(EventProcessingOutcome.Quarantined, result.Outcome);
            Assert.Equal(QuarantineReason.ImpossibleOrdering, result.QuarantineReason);
            Assert.False(result.TryGetProjection(out _));
            Assert.Equal("Quarantined", await ProcessingHarness.ReceiptStateAsync(fixture, result.ReceiptId));
        }

        Assert.Equal(FieldOrderingDecision.Impossible, Assert.Single(claimedTwice.Fields).Decision);
        Assert.Equal(accepted.EventId.Value, Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant)).LastEventId);
    }

    [Fact]
    public async Task An_authority_conflict_quarantines_the_whole_event_and_projects_no_subset()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var otherConnection = Guid.CreateVersion7();

        // A SaaS connector is authoritative for task status, never for PC activity.
        var mixed = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Changes = [new("status", "TaskStatus", "working", "done"), new("active_app", "PcActivity", null, "editor")],
        }, "evt-mixed-authority")).Envelope);

        // A report about an object whose home is another connection is not authoritative either.
        var foreign = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Home = (SyntheticSource.System, ConnectionRecord.ReferenceFor(otherConnection)),
        }, "evt-foreign-object")).Envelope);

        foreach (var result in new[] { mixed, foreign })
        {
            Assert.Equal(EventProcessingOutcome.Quarantined, result.Outcome);
            Assert.Equal(QuarantineReason.AuthorityConflict, result.QuarantineReason);
            Assert.Equal(ProcessingErrors.AuthorityConflict.Code, result.ReasonCode);
            Assert.False(result.TryGetProjection(out _));
            Assert.All(result.Fields, field => Assert.Equal(FieldOrderingDecision.Withheld, field.Decision));
        }

        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
    }

    [Fact]
    public async Task An_unresolved_actor_is_preserved_and_never_guessed()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var result = await host.ProcessSettledAsync((await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 1, Actor = "u-unmapped-17" }, "evt-actor")).Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);
        Assert.True(result.TryGetProjection(out var handoff));
        var actor = Assert.IsType<EventActor>(handoff!.Event.Content.Actor);
        Assert.Equal("u-unmapped-17", actor.SourceActorId);
        Assert.False(actor.IsResolved);
        Assert.Null(actor.CanonicalUserId);
    }

    [Fact]
    public async Task No_payload_can_make_itself_authoritative_or_name_its_own_source()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // A claim to be an AI model changes nothing: the source is the connection, and authority is from code.
        var claimsAi = await host.ProcessSettledAsync((await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { Revision = 1, ClaimedSource = "ai-model" }, "evt-claims-ai")).Envelope);
        Assert.True(claimsAi.TryGetProjection(out var handoff));
        Assert.Equal(EventSourceKind.SaasConnector, handoff!.Event.Metadata.Source.Kind);

        // A claim to be the employee cannot make an approval decision authoritative through a SaaS connection.
        var claimsEmployee = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 2,
            ClaimedSource = "employee",
            Changes = [new("approval", "ApprovalDecision", null, "approved")],
        }, "evt-claims-employee")).Envelope);
        Assert.Equal(QuarantineReason.AuthorityConflict, claimsEmployee.QuarantineReason);
    }

    [Fact]
    public async Task Tenants_keep_separate_ledgers_and_ordering_state_for_the_same_object()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);

        var a = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 9 }, "evt-shared")).Envelope);
        var b = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantB, connectionB, new SyntheticEvent { Revision = 1 }, "evt-shared")).Envelope);

        // Tenant A's newer revision does not make tenant B's older one stale: nothing is shared.
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, a.Outcome);
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, b.Outcome);

        var (database, context) = fixture.CreateTenantScoped(tenantA);
        await using (context)
        {
            var visible = await database.ExecuteAsync(
                async (dbContext, token) => (
                    Ledger: await dbContext.EventLedger.AsNoTracking().Select(entry => entry.EventId).ToListAsync(token),
                    Fields: await dbContext.EventLedgerFields.AsNoTracking().Select(field => field.EventId).ToListAsync(token),
                    States: await dbContext.FieldOrderingStates.AsNoTracking().Select(state => state.LastEventId).ToListAsync(token)),
                Token);

            Assert.True(visible.IsSuccess);
            Assert.Equal([a.EventId.Value], visible.Value.Ledger);
            Assert.Equal([a.EventId.Value], visible.Value.Fields);
            Assert.Equal([a.EventId.Value], visible.Value.States);
        }
    }

    [Fact]
    public async Task A_forged_message_naming_another_tenants_receipt_finds_nothing_and_is_dead_lettered()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, _) = await IngressData.NewConnectionAsync(fixture, Token);
        var deliveredA = await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-victim");

        var forged = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(), tenantB, IngressMessageTypes.ReceiptReceived, TransportData.Marker(deliveredA.ReceiptId), CorrelationId.New()).Value;

        var processed = await host.ProcessAsync(forged, Token);
        var settlement = await host.Consumer.ConsumeAsync(ProcessingHarness.Received(forged), Token);

        Assert.True(processed.IsFailure);
        Assert.Equal(ProcessingErrors.ReceiptNotVisible, processed.Error);
        Assert.Equal(TransportSettlementKind.DeadLetter, settlement.Kind);
        Assert.Equal(ProcessingErrors.ReceiptNotVisible.Code, settlement.DeadLetterReason!.Code);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenantB));
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenantA));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, deliveredA.ReceiptId));
    }

    [Fact]
    public async Task A_message_processed_in_another_tenants_scope_is_refused()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-scope");

        var processed = await Task.Run(
            async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                host.TenantContextAccessor.SetResolved(TenantContext.Resolved(TenantId.New(), TenantResolutionSource.TrustedServiceIdentity));
                return await scope.ServiceProvider.GetRequiredService<IEventProcessor>().ProcessAsync(delivered.Envelope, Token);
            },
            Token);

        Assert.Equal(ProcessingErrors.TenantMismatch, processed.Error);
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenantA));
    }

    [Fact]
    public async Task A_crash_before_the_commit_leaves_nothing_and_the_redelivery_is_processed_once()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        Delivered accepted, quarantined;
        using (var crash = new CancellationTokenSource())
        {
            await using var crashing = ProcessingHarness.Host(
                fixture, new InMemoryTransport(), services => services.AddSingleton<ISourceEventNormalizer>(new CrashingNormalizer(crash)));
            accepted = await crashing.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-crash-accept");
            quarantined = await crashing.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
            {
                Revision = 2,
                Changes = [new("active_app", "PcActivity", null, "editor")],
            }, "evt-crash-quarantine");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crashing.ProcessAsync(accepted.Envelope, crash.Token));
        }

        using (var crash = new CancellationTokenSource())
        {
            await using var crashing = ProcessingHarness.Host(
                fixture, new InMemoryTransport(), services => services.AddSingleton<ISourceEventNormalizer>(new CrashingNormalizer(crash)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crashing.ProcessAsync(quarantined.Envelope, crash.Token));
        }

        // Nothing committed: no ledger entry, no ordering state, and both receipts still waiting.
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, accepted.ReceiptId));
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, quarantined.ReceiptId));

        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, (await host.ProcessSettledAsync(accepted.Envelope)).Outcome);
        Assert.Equal(EventProcessingOutcome.Quarantined, (await host.ProcessSettledAsync(quarantined.Envelope)).Outcome);
        Assert.Equal(2, (await ProcessingHarness.LedgerAsync(fixture, tenant)).Count);
    }

    [Fact]
    public async Task A_crash_after_the_commit_and_before_completion_turns_the_redelivery_into_a_no_op()
    {
        var transport = new InMemoryTransport();
        await using var host = ProcessingHarness.Host(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-crash-after-commit");
        var wire = delivered.Envelope.ToWire();
        transport.SendRaw(wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body.ToArray());

        // The consumer processes and commits, then the process dies before the broker learns the settlement.
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DeliverNextAsync(new CrashAfterConsuming(host.Consumer), Token));
        Assert.Equal(1, transport.ActiveCount);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));

        var redelivery = Assert.IsType<TransportDelivery>(await transport.DeliverNextAsync(host.Consumer, Token));

        Assert.Equal(2, redelivery.Message.DeliveryCount);
        Assert.Equal(TransportSettlementKind.Complete, redelivery.Settlement.Kind);
        Assert.Equal(0, transport.ActiveCount);
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Contains(host.Logs.Entries, entry => entry.Text.Contains("already processed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_receipt_process_it_exactly_once()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-concurrent-receipt");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.ProcessAsync(delivered.Envelope, Token)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Value.Outcome == EventProcessingOutcome.AcceptedForProjection);
        Assert.Equal(5, results.Count(result => result.Value.Outcome == EventProcessingOutcome.Duplicate));
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
    }

    [Fact]
    public async Task Concurrent_events_of_one_field_converge_on_the_newest_whatever_the_interleaving()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var deliveries = new List<Delivered>();
        for (var revision = 1; revision <= 6; revision++)
        {
            deliveries.Add(await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = revision }, $"evt-race-{revision}"));
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

        var newest = deliveries[^1];
        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal(EventIdentity.ForReceipt(newest.ReceiptId).Value, state.LastEventId);
        Assert.Equal(6L, state.SourceRevision);
        Assert.Equal(6, (await ProcessingHarness.LedgerAsync(fixture, tenant)).Count);
    }

    [Fact]
    public async Task A_field_read_for_ordering_stays_locked_until_commit_so_a_newer_event_is_never_overwritten()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var first = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-hold-1")).Envelope);
        var older = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 3 }, "evt-hold-3");
        var newer = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 5 }, "evt-hold-5");

        // The schema owner parks the older event between reading the field and committing: an uncommitted row
        // with the older event's ids makes its ledger insert wait. This interleaving is forced, not hoped for.
        await using var blocker = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var parked = await blocker.BeginTransactionAsync(Token);
        await ProcessingHarness.ParkLedgerInsertAsync(blocker, parked, first.EventId.Value, older, "evt-hold-3");

        await using var monitor = await fixture.OpenAdministrativeConnectionAsync(Token);
        var olderTask = host.ProcessAsync(older.Envelope, Token);
        var olderPid = Assert.NotNull(await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, blocker.ProcessID));

        // The newer event must wait for the field the older one read. Without that lock it would commit now, and
        // the older event, still holding what it read before, would then overwrite it.
        var newerTask = host.ProcessAsync(newer.Envelope, Token);
        await ProcessingHarness.WaitForBackendBlockedByAsync(monitor, olderPid, newerTask);
        await parked.RollbackAsync(Token);

        var results = await Task.WhenAll(olderTask, newerTask);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.All(results, result => Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Value.Outcome));
        Assert.Equal(1L, Assert.Single(results[0].Value.Fields).Previous!.SourceRevision);
        Assert.Equal(3L, Assert.Single(results[1].Value.Fields).Previous!.SourceRevision);

        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal((EventIdentity.ForReceipt(newer.ReceiptId).Value, 5L), (state.LastEventId, state.SourceRevision));

        // The Snapshot followed the same order: the newer event's fact is current, and each event took one version.
        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal((EventIdentity.ForReceipt(newer.ReceiptId).Value, 5L, 3L), (fact.LastEventId, fact.SourceRevision, fact.FieldVersion));
        Assert.Equal(3L, Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).Version);
    }

    [Fact]
    public async Task Receipts_that_never_become_valid_events_are_quarantined_with_their_canonical_reason()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        await using var throwingHost = ProcessingHarness.Host(fixture, new InMemoryTransport(), services => services.AddSingleton<ISourceEventNormalizer, ThrowingNormalizer>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var cases = new (Delivered Delivered, TransportHost Host, QuarantineReason Reason, string Code)[]
        {
            (await host.SubmitRawAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }.ToPayload(), "evt-v9", schemaVersion: "9"), host, QuarantineReason.UnknownSchemaVersion, ProcessingErrors.NormalizerMissing.Code),
            (await host.SubmitRawAsync(fixture, tenant, connection, IngressData.PayloadWith(IngressData.NewMarker()), "evt-garbage"), host, QuarantineReason.BadSchema, ProcessingErrors.NormalizerRejected.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, Changes = [new("Status", "TaskStatus", null, "done")] }, "evt-bad-field"), host, QuarantineReason.BadSchema, CanonicalEventErrors.InvalidChange.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, SchemaVersion = "2" }, "evt-canonical-v2"), host, QuarantineReason.UnknownSchemaVersion, CanonicalEventErrors.UnknownSchemaVersion.Code),
            (await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-throws"), throwingHost, QuarantineReason.BadSchema, ProcessingErrors.NormalizerFailed.Code),
        };

        foreach (var (delivered, processingHost, reason, code) in cases)
        {
            var result = await processingHost.ProcessSettledAsync(delivered.Envelope);
            Assert.True(EventProcessingOutcome.Quarantined == result.Outcome, code);
            Assert.True(reason == result.QuarantineReason, code);
            Assert.Equal(code, result.ReasonCode);
            Assert.Null(result.WorkObject);
            Assert.False(result.TryGetProjection(out _));
            Assert.Equal("Quarantined", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));
        }

        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        Assert.Equal(cases.Length, ledger.Count);
        Assert.All(ledger, entry =>
        {
            Assert.Equal("Quarantined", entry.Outcome);
            Assert.Null(entry.ObjectId);
        });
        Assert.DoesNotContain(throwingHost.Logs.Entries, entry => entry.Text.Contains("Could not map", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ledger_keeps_no_raw_content_while_the_expiring_receipt_still_holds_it()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();

        var result = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent
        {
            Revision = 1,
            Actor = "actor-" + marker,
            Reference = "reference-" + marker,
            Changes = [new("status", "TaskStatus", "before-" + marker, "after-" + marker)],
        }, "evt-content")).Envelope);
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);

        await using var connectionToDatabase = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM event_ledger l WHERE strpos(l::text, @marker) > 0)
                 + (SELECT count(*) FROM event_ledger_fields f WHERE strpos(f::text, @marker) > 0)
                 + (SELECT count(*) FROM field_ordering_states s WHERE strpos(s::text, @marker) > 0),
                   (SELECT count(*) FROM raw_receipts r WHERE strpos(r.payload::text, @marker) > 0);
            """,
            connectionToDatabase);
        command.Parameters.AddWithValue("marker", marker);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));

        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Every_outcome_settles_the_transport_message_as_designed()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var accepted = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-settle-accepted");
        var quarantined = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 2, Changes = [new("active_app", "PcActivity", null, "x")] }, "evt-settle-quarantined");

        Assert.Equal(TransportSettlementKind.Complete, (await host.Consumer.ConsumeAsync(ProcessingHarness.Received(accepted.Envelope), Token)).Kind);
        Assert.Equal(TransportSettlementKind.Complete, (await host.Consumer.ConsumeAsync(ProcessingHarness.Received(quarantined.Envelope), Token)).Kind);
        Assert.Equal(TransportSettlementKind.Complete, (await host.Consumer.ConsumeAsync(ProcessingHarness.Received(accepted.Envelope, 2), Token)).Kind);

        // A commit that fails is retried, never completed.
        await using var failing = ProcessingHarness.Host(fixture, new InMemoryTransport(), services =>
        {
            services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            services.AddScoped<IEventProcessor, UnavailableProcessor>();
        });
        var retried = await failing.Consumer.ConsumeAsync(ProcessingHarness.Received(accepted.Envelope), Token);
        Assert.Equal(TransportSettlementKind.Abandon, retried.Kind);
    }

    [Fact]
    public async Task Processing_logs_carry_ids_and_codes_but_never_content()
    {
        var transport = new InMemoryTransport();
        await using var host = ProcessingHarness.Host(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        SyntheticEvent Content(long? revision) => new()
        {
            Revision = revision,
            UpdatedAt = T0,
            Actor = "actor-" + marker,
            Reference = "reference-" + marker,
            Changes = [new("status", "TaskStatus", "before-" + marker, "after-" + marker)],
        };

        var accepted = await host.SubmitAsync(fixture, tenant, connection, Content(1), "evt-log-1");
        await host.ProcessSettledAsync(accepted.Envelope);
        await host.ProcessSettledAsync(accepted.Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Content(null), "evt-log-ambiguous")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, Content(2) with { Changes = [new("active_app", "PcActivity", "before-" + marker, "after-" + marker)] }, "evt-log-conflict")).Envelope);
        await host.ProcessSettledAsync((await host.SubmitRawAsync(fixture, tenant, connection, IngressData.PayloadWith(marker), "evt-log-garbage")).Envelope);

        Assert.NotEmpty(host.Logs.Entries);
        Assert.Contains(host.Logs.Entries, entry => entry.Text.Contains(accepted.ReceiptId.ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
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

    /// <summary>A processor whose commit always fails, as a lost database connection would.</summary>
    private sealed class UnavailableProcessor : IEventProcessor
    {
        public Task<Result<EventProcessingResult>> ProcessAsync(TransportEnvelope envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<EventProcessingResult>.Failure(ProcessingErrors.Unavailable));
    }
}
