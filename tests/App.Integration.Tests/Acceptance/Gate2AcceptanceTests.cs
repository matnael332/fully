using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Observability;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Acceptance;

/// <summary>
/// The Gate 2 acceptance criterion on the whole path, from ingress through the outbox relay, the broker and the
/// transport consumer to processing, the ledger and the Snapshot, against the real local PostgreSQL through the runtime
/// role and the deterministic in-memory broker: duplicate, delayed and out-of-order events do not corrupt the Snapshot,
/// and no event is silently lost. Every scenario states the Snapshot the source's own ordering evidence implies and
/// requires exactly that, with its provenance, whatever the order, duplication, delay or redelivery.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class Gate2AcceptanceTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    // The only outcomes a delivery order can produce when every event carries a distinct revision.
    private static readonly string[] OrderOutcomes = ["AcceptedForProjection", "Stale"];

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Duplicates_at_every_hop_change_the_snapshot_exactly_once()
    {
        await using var path = await WholePath.StartAsync(fixture);
        using var metrics = new MetricsRecorder(path.Processing.Services.GetRequiredService<EventBackboneMetrics>());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var stated = Revision("1001", 1, Status("done"));

        // The source sends its event twice: ingress stores it once and answers the repeat with that receipt.
        var first = await path.SubmitAsync(connection, stated);
        var repeat = await path.SubmitAsync(connection, stated);
        Assert.Equal((IngressStatus.Accepted, IngressStatus.Duplicate), (first.Status, repeat.Status));
        Assert.Equal(first.ReceiptId, repeat.ReceiptId);
        var receipt = first.ReceiptId!.Value;
        var messageId = (await TransportData.OutboxIdForReceiptAsync(fixture, receipt, Token)).ToString("D");

        // The same provider event id with altered content is the same event: the first delivery stands.
        var altered = await path.SubmitAsync(connection, stated with { Event = stated.Event with { Changes = [Status("altered")] } });
        Assert.Equal((IngressStatus.Duplicate, (Guid?)receipt), (altered.Status, altered.ReceiptId));

        // The relay dies after the send and before its mark, inside the broker's duplicate-detection window: the
        // republication under the same message id is absorbed by the broker.
        Assert.True(await path.RelayUntilCrashAsync(envelope => envelope.CausationId == receipt, SendFault.CrashAfterSend));
        await path.RelayAsync();
        Assert.Equal(2, path.Broker.Sends.Count(wire => wire.MessageId == messageId));
        Assert.Single(path.Broker.Accepted, wire => wire.MessageId == messageId);

        // The consumer processes and commits, then dies before the broker learns the settlement: the broker delivers
        // the message again, and processing recognizes the receipt.
        await Assert.ThrowsAsync<InvalidOperationException>(() => path.DeliverAsync(new SettlementLost(path.Processing.Consumer)));
        var redelivered = Assert.IsType<TransportDelivery>(await path.DeliverAsync());
        Assert.Equal((2, TransportSettlementKind.Complete), (redelivered.Message.DeliveryCount, redelivered.Settlement.Kind));

        // Settled, the message arrives yet again, as a republication outside the window would deliver it.
        path.DeliverAgain(redelivered.Message);
        var again = Assert.Single(await path.DrainAsync());
        Assert.Equal(TransportSettlementKind.Complete, again.Settlement.Kind);

        // One receipt, one marker, one ledger entry and one Snapshot version: every other copy was a recognized no-op.
        Assert.Equal(1, metrics.Sum("workcontinuity.processing.events", ("outcome", "accepted_for_projection")));
        Assert.Equal(2, metrics.Sum("workcontinuity.processing.events", ("outcome", "duplicate")));
        await path.AssertFactsAsync(tenant, GroundTruth.Of([stated]));
        await path.AssertProvenanceAsync(tenant);
        Assert.Equal((1L, 1L), (Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).Version, Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant)).FieldVersion));
        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, [receipt, repeat.ReceiptId!.Value, altered.ReceiptId!.Value]);
        Assert.Empty(accounting.DeadLetters);
    }

    [Fact]
    public async Task A_repeat_without_a_provider_event_id_is_flagged_as_a_duplicate_candidate_and_never_changes_the_snapshot()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var payload = new SyntheticEvent { ObjectId = "1001", UpdatedAt = T0, Changes = [Status("done")] }.ToPayload();

        // A source without event ids sends one event twice. Ingress cannot tell a repeat from a new event with the same
        // content, so it stores both.
        var first = await path.SubmitAsync(SyntheticSource.Delivery(connection, sourceEventId: null, payload));
        var second = await path.SubmitAsync(SyntheticSource.Delivery(connection, sourceEventId: null, payload));
        Assert.Equal((IngressStatus.Accepted, IngressStatus.Accepted), (first.Status, second.Status));
        Assert.NotEqual(first.ReceiptId, second.ReceiptId);

        await path.FlushAsync();

        // Processing flags the second as a candidate duplicate of the first and, unable to order identical evidence, holds
        // it for reconciliation instead of guessing: the Snapshot holds the first event's fact, once.
        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        var accepted = Assert.Single(ledger, entry => entry.ReceiptId == first.ReceiptId);
        var candidate = Assert.Single(ledger, entry => entry.ReceiptId == second.ReceiptId);
        Assert.Equal("AcceptedForProjection", accepted.Outcome);
        Assert.Equal(
            ("ReconciliationRequired", ProcessingErrors.OrderingAmbiguous.Code, (Guid?)accepted.EventId),
            (candidate.Outcome, candidate.ReasonCode, candidate.DuplicateCandidateOf));
        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("done", accepted.EventId, 1L, 1L), (fact.Value, fact.LastEventId, fact.FieldVersion, fact.ObjectVersion));
        await path.AssertProvenanceAsync(tenant);
        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, [first.ReceiptId!.Value, second.ReceiptId!.Value]);
        Assert.Empty(accounting.DeadLetters);
    }

    [Fact]
    public async Task Delayed_and_out_of_order_events_converge_on_the_source_truth_and_undecidable_ones_change_nothing()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // Revisions of one object, the newest first. On another object, ordering is per field: r5 arrives after r7 but
        // is the first change of its field, so it applies, and r6 is newer for one field and older for the other.
        var r1 = Revision("1001", 1, Status("working"), Due("2026-10-01"));
        var r2 = Revision("1001", 2, Status("review"));
        var r3 = Revision("1001", 3, Status("done"), Due("2026-10-05"));
        var m5 = Revision("1002", 5, Status("blocked"));
        var m6 = Revision("1002", 6, Status("working"), Due("2026-10-20"));
        var m7 = Revision("1002", 7, Due("2026-11-01"));

        // Source update times only, and occurrence times only.
        var u1 = Updated("evt-1003-u1", T0.AddMinutes(1), "a");
        var u2 = Updated("evt-1003-u2", T0.AddMinutes(2), "b");
        var u3 = Updated("evt-1003-u3", T0.AddMinutes(3), "c");
        var o1 = new SourceEvent("evt-1004-o1", new SyntheticEvent { ObjectId = "1004", OccurredAt = T0.AddSeconds(10), Changes = [Status("x")] });
        var o2 = new SourceEvent("evt-1004-o2", new SyntheticEvent { ObjectId = "1004", OccurredAt = T0.AddSeconds(20), Changes = [Status("y")] });

        // Conflicting signals, where the precedence decides: a higher revision stated at an earlier update time than a
        // lower one, and a later update time for an earlier occurrence.
        var p8 = new SourceEvent("evt-1006-r8", new SyntheticEvent { ObjectId = "1006", Revision = 8, UpdatedAt = T0.AddMinutes(1), Changes = [Status("p")] });
        var p7 = new SourceEvent("evt-1006-r7", new SyntheticEvent { ObjectId = "1006", Revision = 7, UpdatedAt = T0.AddMinutes(30), Changes = [Status("q")] });
        var s5 = new SourceEvent("evt-1007-u5", new SyntheticEvent { ObjectId = "1007", UpdatedAt = T0.AddMinutes(5), OccurredAt = T0.AddMinutes(1), Changes = [Status("s")] });
        var s4 = new SourceEvent("evt-1007-u4", new SyntheticEvent { ObjectId = "1007", UpdatedAt = T0.AddMinutes(4), OccurredAt = T0.AddMinutes(3), Changes = [Status("t")] });

        // Undecidable: the same update time as the accepted change with another value, and no evidence at all.
        var tie = Updated("evt-1003-tie", T0.AddMinutes(3), "z");
        var unevidenced = new SourceEvent("evt-1005-none", new SyntheticEvent { ObjectId = "1005", Changes = [Status("lost")] });

        // Delivered in waves: the newest changes first, the older ones delayed behind them.
        var receipts = new List<Guid>();
        foreach (var wave in new SourceEvent[][] { [r3, m7, u3, o2, p8, s5], [r1, m5, u1, o1, p7, s4], [r2, m6, u2, tie, unevidenced] })
        {
            foreach (var stated in wave)
            {
                receipts.Add(await path.SubmitAcceptedAsync(connection, stated));
            }

            Assert.All(await path.FlushAsync(), delivery => Assert.Equal(TransportSettlementKind.Complete, delivery.Settlement.Kind));
        }

        await path.AssertFactsAsync(
            tenant,
            [
                r3.FactFor("status"), r3.FactFor("due_date"), m6.FactFor("status"), m7.FactFor("due_date"),
                u3.FactFor("status"), o2.FactFor("status"), p8.FactFor("status"), s5.FactFor("status"),
            ]);
        await path.AssertProvenanceAsync(tenant);
        await path.AssertVersionsAsync(tenant);

        const string Accepted = "AcceptedForProjection", Stale = "Stale", Reconcile = "ReconciliationRequired";
        Assert.Equal(
            new (string, string, string?)[]
            {
                (m5.Id, Accepted, null), (m6.Id, Accepted, null), (m7.Id, Accepted, null),
                (r1.Id, Stale, null), (r2.Id, Stale, null), (r3.Id, Accepted, null),
                (tie.Id, Reconcile, ProcessingErrors.OrderingAmbiguous.Code),
                (u1.Id, Stale, null), (u2.Id, Stale, null), (u3.Id, Accepted, null),
                (o1.Id, Stale, null), (o2.Id, Accepted, null),
                (p7.Id, Stale, null), (p8.Id, Accepted, null), (s4.Id, Stale, null), (s5.Id, Accepted, null),
                (unevidenced.Id, Reconcile, ProcessingErrors.NoOrderingEvidence.Code),
            }.OrderBy(item => item.Item1, StringComparer.Ordinal),
            (await ProcessingHarness.LedgerAsync(fixture, tenant))
                .Select(entry => (entry.SourceEventId!, entry.Outcome, entry.ReasonCode))
                .OrderBy(item => item.Item1, StringComparer.Ordinal));

        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, receipts);
        Assert.Empty(accounting.DeadLetters);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_to_success_and_one_past_the_retry_limit_is_dead_lettered_then_re_driven_after_the_fix()
    {
        var faults = new CommitFaults();
        await using var path = await WholePath.StartAsync(fixture, processing: faults.Register);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // An older and a newer change of one field, and a change of another object.
        var older = Revision("2001", 1, Status("working"));
        var newer = Revision("2001", 2, Status("done"));
        var other = Revision("2002", 1, Status("done"));
        var olderReceipt = await path.SubmitAcceptedAsync(connection, older);
        var newerReceipt = await path.SubmitAcceptedAsync(connection, newer);
        var otherReceipt = await path.SubmitAcceptedAsync(connection, other);
        faults.FailProcessing(olderReceipt, times: int.MaxValue);
        faults.FailProcessing(newerReceipt, times: 2);
        faults.FailProcessing(otherReceipt, times: int.MaxValue);

        var deliveries = await path.FlushAsync();
        IEnumerable<TransportSettlementKind> SettlementsOf(IEnumerable<TransportDelivery> of, Guid receipt) =>
            of.Where(delivery => WholePath.CausationOf(delivery) == receipt).Select(delivery => delivery.Settlement.Kind);

        // A transient failure is retried after a bounded backoff until it succeeds; one that persists is dead-lettered at
        // the attempt limit, visibly and with its identity, and does not hold up the messages behind it.
        const TransportSettlementKind Abandon = TransportSettlementKind.Abandon;
        Assert.Equal([Abandon, Abandon, TransportSettlementKind.Complete], SettlementsOf(deliveries, newerReceipt));
        Assert.Equal([Abandon, Abandon, Abandon, Abandon, TransportSettlementKind.DeadLetter], SettlementsOf(deliveries, olderReceipt));
        Assert.Equal([Abandon, Abandon, Abandon, Abandon, TransportSettlementKind.DeadLetter], SettlementsOf(deliveries, otherReceipt));
        Assert.Equal(4 + 2 + 4, path.Broker.Backoffs.Count);
        Assert.All(path.Broker.Backoffs, backoff => Assert.InRange(backoff, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30)));
        var dead = await path.DeadLettersAsync(tenant);
        Assert.Equal(new[] { olderReceipt, otherReceipt }.Order(), dead.Select(letter => letter.Envelope!.CausationId).Order());
        Assert.All(dead, letter => Assert.Equal((TransportErrors.RetriesExhausted.Code, 0), (letter.Reason, letter.Envelope!.RedriveCount)));

        // Nothing is lost even now: the dead letters account for their receipts, and the Snapshot holds the newer change.
        await path.AssertNothingSilentlyLostAsync(tenant, [olderReceipt, newerReceipt, otherReceipt]);
        await path.AssertFactsAsync(tenant, GroundTruth.Of([newer]));

        // After the fix, both are re-driven under their identities. The older change now arrives after the newer one and
        // never overwrites it; the other object's change applies.
        faults.Clear(olderReceipt);
        faults.Clear(otherReceipt);
        foreach (var letter in dead)
        {
            Assert.Equal(RedriveStatus.Redriven, (await path.Broker.RedriveAsync(letter.Envelope!.OutboxMessageId, Token)).Status);
        }

        var redriven = await path.DrainAsync();
        Assert.Equal([TransportSettlementKind.Complete], SettlementsOf(redriven, olderReceipt));
        Assert.Equal([TransportSettlementKind.Complete], SettlementsOf(redriven, otherReceipt));
        Assert.All(redriven, delivery => Assert.Equal(1, (int)delivery.Message.ApplicationProperties[TransportProperties.RedriveCount]));

        Assert.Equal(5 + 2 + 5, faults.Failures);
        await path.AssertFactsAsync(tenant, GroundTruth.Of([older, newer, other]));
        await path.AssertProvenanceAsync(tenant);
        await path.AssertVersionsAsync(tenant);
        Assert.Equal(
            new[] { (olderReceipt, "Stale"), (newerReceipt, "AcceptedForProjection"), (otherReceipt, "AcceptedForProjection") }.Order(),
            (await ProcessingHarness.LedgerAsync(fixture, tenant)).Select(entry => (entry.ReceiptId, entry.Outcome)).Order());
        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, [olderReceipt, newerReceipt, otherReceipt]);
        Assert.Empty(accounting.DeadLetters);
    }

    [Fact]
    public async Task Invalid_and_untrusted_input_is_refused_or_quarantined_and_never_reaches_the_snapshot()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (_, disabled) = await IngressData.NewConnectionAsync(fixture, Token, connectionStatus: ConnectionStatus.Disabled);
        var (_, suspended) = await IngressData.NewConnectionAsync(fixture, Token, tenantStatus: "Suspended");
        var marker = IngressData.NewMarker();
        var carrying = new SyntheticEvent { ObjectId = "3009", Revision = 1, Changes = [Status("refused-" + marker)] }.ToPayload();

        // Refused by ingress: nothing is stored, nothing is acknowledged, and nothing can reach the broker.
        (string Case, IngressSubmission Delivery, Error Expected)[] refusals =
        [
            ("unknown connection", SyntheticSource.Delivery(Guid.CreateVersion7(), "evt-refused-1", carrying), IngressErrors.UnknownConnection),
            ("disabled connection", SyntheticSource.Delivery(disabled, "evt-refused-2", carrying), IngressErrors.UnknownConnection),
            ("suspended tenant", SyntheticSource.Delivery(suspended, "evt-refused-3", carrying), IngressErrors.TenantInactive),
            ("another source", SyntheticSource.Delivery(connection, "evt-refused-4", carrying, sourceSystem: "other-source"), IngressErrors.SourceMismatch),
            ("failed verification", SyntheticSource.Delivery(connection, "evt-refused-5", carrying, proof: SyntheticSource.Sign(connection, "other")), IngressErrors.SourceNotVerified),
            ("malformed", SyntheticSource.Delivery(connection, "evt-refused-6", "status " + marker), IngressErrors.PayloadNotStorable),
        ];
        foreach (var (name, delivery, expected) in refusals)
        {
            var refused = await path.SubmitAsync(delivery);
            Assert.True(refused.Status == IngressStatus.Rejected && Equals(expected, refused.Error), $"{name}: {refused.Status} {refused.Error?.Code}");
            Assert.False(refused.IsAcknowledged, name);
        }

        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        // Stored, then quarantined by processing with its canonical reason: never retried, never projected.
        var afterReceipt = (await IngressData.DatabaseNowAsync(fixture, Token)).AddHours(1);
        SyntheticChange Content(string field = "status", string dataType = "TaskStatus") => new(field, dataType, null, "quarantined-" + marker);
        var valid = Revision("3001", 4, Status("done"));
        (string Case, string Payload, string Schema, QuarantineReason Reason, string Code)[] quarantines =
        [
            ("not the source's schema", IngressData.PayloadWith(marker), SyntheticSource.SchemaVersion, QuarantineReason.BadSchema, ProcessingErrors.NormalizerRejected.Code),
            ("unknown source schema version", EventOn("3002", Content()).ToPayload(), "9", QuarantineReason.UnknownSchemaVersion, ProcessingErrors.NormalizerMissing.Code),
            ("unknown canonical schema version", (EventOn("3003", Content()) with { SchemaVersion = "2" }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.UnknownSchemaVersion, CanonicalEventErrors.UnknownSchemaVersion.Code),
            ("invalid field name", EventOn("3004", new SyntheticChange("Status", "TaskStatus", null, "quarantined-" + marker)).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.BadSchema, CanonicalEventErrors.InvalidChange.Code),
            ("source time after the receipt", (EventOn("3005", Content()) with { UpdatedAt = afterReceipt }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.ImpossibleOrdering, ProcessingErrors.SourceTimeAfterReceipt.Code),
            ("occurred after the update", (EventOn("3006", Content()) with { UpdatedAt = T0, OccurredAt = T0.AddMinutes(10) }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.ImpossibleOrdering, ProcessingErrors.OccurredAfterUpdate.Code),
            ("not authoritative for a field", EventOn("3007", Content(), Content("active_app", "PcActivity")).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code),
            ("an object of another connection", (EventOn("3008", Content()) with { Home = (SyntheticSource.System, ConnectionRecord.ReferenceFor(Guid.CreateVersion7())) }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code),
            ("claims to be the employee", (EventOn("3010", Content("approval", "ApprovalDecision")) with { ClaimedSource = "employee" }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code),
            ("an accepted revision claimed again", (EventOn("3001", Content()) with { Revision = 4 }).ToPayload(), SyntheticSource.SchemaVersion, QuarantineReason.ImpossibleOrdering, ProcessingErrors.RevisionClaimedTwice.Code),
        ];

        var validReceipt = await path.SubmitAcceptedAsync(connection, valid);
        var quarantined = new Dictionary<Guid, (string Case, QuarantineReason Reason, string Code)>();
        for (var index = 0; index < quarantines.Length; index++)
        {
            var (name, payload, schema, reason, code) = quarantines[index];
            var stored = await path.SubmitAsync(SyntheticSource.Delivery(connection, $"evt-quarantined-{index}", payload, schemaVersion: schema));
            Assert.True(stored.Status == IngressStatus.Accepted, $"{name}: {stored.Status} {stored.Error?.Code}");
            quarantined[stored.ReceiptId!.Value] = (name, reason, code);
        }

        var deliveries = await path.FlushAsync();

        Assert.Equal(quarantines.Length + 1, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal(TransportSettlementKind.Complete, delivery.Settlement.Kind));
        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        Assert.Equal("AcceptedForProjection", Assert.Single(ledger, entry => entry.ReceiptId == validReceipt).Outcome);
        foreach (var (receipt, (name, reason, code)) in quarantined)
        {
            var entry = Assert.Single(ledger, candidate => candidate.ReceiptId == receipt);
            Assert.True(
                entry.Outcome == "Quarantined" && entry.QuarantineReason == reason.ToString() && entry.ReasonCode == code,
                $"{name}: {entry.Outcome} {entry.QuarantineReason} {entry.ReasonCode}");
        }

        // Only the valid event reached the Snapshot; no quarantined content is anywhere in it, or in any log.
        await path.AssertFactsAsync(tenant, [valid.FactFor("status")]);
        Assert.Equal("3001", Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant)).ObjectId);
        Assert.Equal(0, await path.CountSnapshotRowsContainingAsync(marker));
        Assert.DoesNotContain(path.Logs, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
        var accounting = await path.AssertNothingSilentlyLostAsync(tenant, [validReceipt, .. quarantined.Keys]);
        Assert.Empty(accounting.DeadLetters);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [InlineData(90210)]
    public async Task Seeded_reordering_duplication_delay_and_faults_converge_on_the_ground_truth(int seed)
    {
        var random = new Random(seed);
        var faults = new CommitFaults { FailFirstProcessingCommit = _ => random.NextDouble() < 0.15 };
        await using var path = await WholePath.StartAsync(fixture, processing: faults.Register);
        var marker = IngressData.NewMarker();

        // Two tenants with the same object ids and the same provider event ids, and each its own values.
        var tenants = new List<(TenantId Tenant, Guid Connection, IReadOnlyList<SourceEvent> Events)>();
        foreach (var label in new[] { "a", "b" })
        {
            var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
            tenants.Add((tenant, connection, Scenario(random, $"{marker}-{label}")));
        }

        var plan = Plan(random, tenants);
        var acknowledged = tenants.ToDictionary(item => item.Tenant, _ => new List<Guid>());
        var disturbances = new Disturbances { SourceRepeats = plan.Count - tenants.Sum(item => item.Events.Count) };
        for (var next = 0; next < plan.Count;)
        {
            var take = Math.Min(random.Next(1, 7), plan.Count - next);
            foreach (var (tenant, connection, stated) in plan.Skip(next).Take(take))
            {
                var outcome = await path.SubmitAsync(connection, stated);
                Assert.True(outcome.IsAcknowledged, $"seed {seed}: {stated.Id} was {outcome.Status}");
                acknowledged[tenant].Add(outcome.ReceiptId!.Value);
            }

            next += take;
            await RelayStepAsync(path, random, disturbances);
            await DeliveryStepAsync(path, random, disturbances);
        }

        await path.FlushAsync();
        disturbances.CommitFailures = faults.Failures;
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}: {disturbances}");
        Assert.True(disturbances.SourceRepeats > 0 && disturbances.Faults > 0, $"seed {seed} disturbed nothing: {disturbances}");

        foreach (var (tenant, _, events) in tenants)
        {
            await path.AssertFactsAsync(tenant, GroundTruth.Of(events));
            await path.AssertProvenanceAsync(tenant);
            await path.AssertVersionsAsync(tenant);
            var accounting = await path.AssertNothingSilentlyLostAsync(tenant, acknowledged[tenant]);
            Assert.Empty(accounting.DeadLetters);
            Assert.Equal(events.Count, accounting.Ledger.Count);
            Assert.All(accounting.Ledger, entry => Assert.Contains(entry.Outcome, OrderOutcomes));
        }

        Assert.DoesNotContain(path.Logs, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
    }

    private static SyntheticChange Status(string value) => new("status", "TaskStatus", null, value);

    private static SyntheticChange Due(string value) => new("due_date", "Deadline", null, value);

    private static SyntheticEvent EventOn(string objectId, params SyntheticChange[] changes) =>
        new() { ObjectId = objectId, Revision = 1, Changes = changes };

    private static SourceEvent Revision(string objectId, long revision, params SyntheticChange[] changes) =>
        new($"evt-{objectId}-r{revision}", new SyntheticEvent { ObjectId = objectId, Revision = revision, UpdatedAt = T0.AddMinutes(revision), Changes = changes });

    private static SourceEvent Updated(string id, DateTimeOffset updatedAt, string status) =>
        new(id, new SyntheticEvent { ObjectId = "1003", UpdatedAt = updatedAt, Changes = [Status(status)] });

    // Three objects, each with five revisions that change a random non-empty subset of three fields, every value
    // distinct and carrying the test's marker, and times consistent with the revisions.
    private static IReadOnlyList<SourceEvent> Scenario(Random random, string valuePrefix)
    {
        (string Field, string DataType)[] fields = [("status", "TaskStatus"), ("due_date", "Deadline"), ("start_date", "Deadline")];
        var events = new List<SourceEvent>();
        foreach (var objectId in new[] { "1001", "1002", "1003" })
        {
            for (var revision = 1; revision <= 5; revision++)
            {
                var changed = fields.Where(_ => random.NextDouble() < 0.5).ToList();
                if (changed.Count == 0)
                {
                    changed.Add(fields[random.Next(fields.Length)]);
                }

                var updated = T0.AddMinutes(revision);
                events.Add(new SourceEvent(
                    $"evt-{objectId}-r{revision}",
                    new SyntheticEvent
                    {
                        ObjectId = objectId,
                        Revision = revision,
                        UpdatedAt = updated,
                        OccurredAt = updated.AddSeconds(-10),
                        Changes = [.. changed.Select(field => new SyntheticChange(field.Field, field.DataType, null, $"{valuePrefix}-{objectId}-{field.Field}-r{revision}"))],
                    }));
            }
        }

        return events;
    }

    // Both tenants' events in a random order, with about three in ten sent again by the source at a later point.
    private static List<(TenantId Tenant, Guid Connection, SourceEvent Stated)> Plan(
        Random random,
        IEnumerable<(TenantId Tenant, Guid Connection, IReadOnlyList<SourceEvent> Events)> tenants)
    {
        var shuffled = tenants.SelectMany(item => item.Events.Select(stated => (item.Tenant, item.Connection, Stated: stated))).ToArray();
        random.Shuffle(shuffled);
        var plan = new List<(TenantId Tenant, Guid Connection, SourceEvent Stated)>(shuffled);
        foreach (var item in shuffled)
        {
            if (random.NextDouble() < 0.3)
            {
                plan.Insert(random.Next(plan.IndexOf(item) + 1, plan.Count + 1), item);
            }
        }

        return plan;
    }

    // Most rounds relay everything; some crash the relay after or before a send, and some leave the relay late.
    private static async Task RelayStepAsync(WholePath path, Random random, Disturbances disturbances)
    {
        var roll = random.NextDouble();
        if (roll < 0.15)
        {
            // Half the time the broker's duplicate window has passed by the republication, so it delivers twice.
            if (await path.RelayUntilCrashAsync(_ => true, SendFault.CrashAfterSend))
            {
                disturbances.RelayCrashesAfterSend++;
                if (random.NextDouble() < 0.5)
                {
                    path.Broker.Now += TimeSpan.FromMinutes(10);
                    disturbances.WindowsPassed++;
                }
            }
        }
        else if (roll < 0.25)
        {
            if (await path.RelayUntilCrashAsync(_ => true, SendFault.CrashBeforeSend))
            {
                disturbances.RelayCrashesBeforeSend++;
            }
        }
        else if (roll < 0.8)
        {
            await path.RelayAsync();
        }
    }

    // Delivers some of what waits: a few consumers die before settling, and a few settled messages come back.
    private static async Task DeliveryStepAsync(WholePath path, Random random, Disturbances disturbances)
    {
        var deliveries = random.Next(0, path.Broker.ActiveCount + 1);
        for (var index = 0; index < deliveries; index++)
        {
            if (random.NextDouble() < 0.12)
            {
                try
                {
                    await path.DeliverAsync(new SettlementLost(path.Processing.Consumer, disturbances.LostSettlements));
                }
                catch (InvalidOperationException exception) when (exception.Message == SettlementLost.Death)
                {
                    // The consumer died before its settlement reached the broker.
                }

                continue;
            }

            if (await path.DeliverAsync() is not { } delivery)
            {
                return;
            }

            if (delivery.Settlement.Kind == TransportSettlementKind.Complete && random.NextDouble() < 0.1)
            {
                path.DeliverAgain(delivery.Message);
                disturbances.RedeliveredAfterSettlement++;
            }
        }
    }

    /// <summary>What a seeded run did to the deliveries, reported with the run and required to be more than nothing.</summary>
    private sealed class Disturbances
    {
        public int SourceRepeats { get; init; }

        public int RelayCrashesAfterSend { get; set; }

        public int WindowsPassed { get; set; }

        public int RelayCrashesBeforeSend { get; set; }

        public HashSet<long> LostSettlements { get; } = [];

        public int RedeliveredAfterSettlement { get; set; }

        public int CommitFailures { get; set; }

        public int Faults => RelayCrashesAfterSend + RelayCrashesBeforeSend + LostSettlements.Count + RedeliveredAfterSettlement + CommitFailures;

        public override string ToString() =>
            $"{SourceRepeats} source repeats, {RelayCrashesAfterSend} relay crashes after a send ({WindowsPassed} past the duplicate window), "
            + $"{RelayCrashesBeforeSend} before a send, {LostSettlements.Count} lost settlements, {RedeliveredAfterSettlement} redeliveries after "
            + $"settlement, {CommitFailures} failed processing commits";
    }
}
