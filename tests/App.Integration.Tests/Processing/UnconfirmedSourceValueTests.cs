using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>
/// The Gate 3D extension of the Gate 2 pipeline, provider-neutral, against the real local PostgreSQL: an event whose
/// values the source has not confirmed is held for reconciliation and never ordered or projected, whatever its
/// evidence; authority and impossible order are still judged first; a confirmed event is processed exactly as before.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UnconfirmedSourceValueTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Unconfirmed")]
    [InlineData("Unspecified")]
    [InlineData("99")]
    public async Task An_event_whose_values_are_not_confirmed_is_held_for_reconciliation_and_never_projected(string confirmation)
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // Every ordering signal is present: only the confirmation keeps the value out.
        var delivered = await host.SubmitAsync(
            fixture,
            tenant,
            connection,
            new SyntheticEvent { Revision = 1, UpdatedAt = T0, OccurredAt = T0.AddSeconds(-5), ValueConfirmation = confirmation },
            "evt-unconfirmed");

        var result = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(ProcessingErrors.UnconfirmedSourceValues.Code, result.ReasonCode);
        Assert.Equal("processing.source.unconfirmed", result.ReasonCode);
        Assert.False(result.TryGetProjection(out _));
        var field = Assert.Single(result.Fields);
        Assert.Equal(FieldOrderingDecision.Withheld, field.Decision);
        Assert.Equal(new OrderingEvidence(1, T0, T0.AddSeconds(-5), "evt-unconfirmed"), field.Incoming);

        var entry = Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Equal(("ReconciliationRequired", "processing.source.unconfirmed", (string?)null), (entry.Outcome, entry.ReasonCode, entry.QuarantineReason));
        Assert.Equal([("status", "TaskStatus", "Withheld")], await ProcessingHarness.LedgerFieldsAsync(fixture, entry.EventId));

        // Nothing was ordered and nothing reached the Snapshot; the receipt is settled.
        Assert.Empty(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal("Processed", await ProcessingHarness.ReceiptStateAsync(fixture, delivered.ReceiptId));
    }

    [Fact]
    public async Task A_confirmed_value_is_never_overwritten_by_a_later_unconfirmed_signal()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var confirmed = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1, UpdatedAt = T0 }, "evt-confirmed");
        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, (await host.ProcessSettledAsync(confirmed.Envelope)).Outcome);

        // Newer by every signal, yet unconfirmed: it advances nothing.
        var signal = await host.SubmitAsync(
            fixture,
            tenant,
            connection,
            new SyntheticEvent
            {
                Revision = 2,
                UpdatedAt = T0.AddMinutes(30),
                OccurredAt = T0.AddMinutes(29),
                Changes = [new("status", "TaskStatus", "done", "blocked")],
                ValueConfirmation = "Unconfirmed",
            },
            "evt-signal");
        var held = await host.ProcessSettledAsync(signal.Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, held.Outcome);
        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("status", "done"), (fact.Field, fact.Value));
        Assert.Equal(EventIdentity.ForReceipt(confirmed.ReceiptId).Value, fact.LastEventId);
        var state = Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
        Assert.Equal((1L, T0), (state.SourceRevision, state.SourceUpdatedAtUtc));
    }

    [Fact]
    public async Task Authority_and_impossible_order_are_still_judged_before_confirmation()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        var unauthoritative = await host.SubmitAsync(
            fixture,
            tenant,
            connection,
            new SyntheticEvent { UpdatedAt = T0, Changes = [new("active_app", "PcActivity", null, "editor")], ValueConfirmation = "Unconfirmed" },
            "evt-unauthoritative");
        var impossible = await host.SubmitAsync(
            fixture,
            tenant,
            connection,
            new SyntheticEvent { ObjectId = "1002", UpdatedAt = DateTimeOffset.UtcNow.AddDays(2), ValueConfirmation = "Unconfirmed" },
            "evt-impossible");

        var refused = await host.ProcessSettledAsync(unauthoritative.Envelope);
        var quarantined = await host.ProcessSettledAsync(impossible.Envelope);

        Assert.Equal((EventProcessingOutcome.Quarantined, QuarantineReason.AuthorityConflict), (refused.Outcome, refused.QuarantineReason));
        Assert.Equal((EventProcessingOutcome.Quarantined, QuarantineReason.ImpossibleOrdering), (quarantined.Outcome, quarantined.QuarantineReason));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
    }

    [Fact]
    public async Task Processing_a_held_event_again_is_a_duplicate_that_changes_nothing()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { UpdatedAt = T0, ValueConfirmation = "Unconfirmed" }, "evt-held-twice");

        var first = await host.ProcessSettledAsync(delivered.Envelope);
        var again = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, first.Outcome);
        Assert.Equal((EventProcessingOutcome.Duplicate, EventProcessingOutcome.ReconciliationRequired), (again.Outcome, again.PreviousOutcome));
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant));
        Assert.Empty(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
    }

    [Fact]
    public async Task A_confirmed_event_is_ordered_and_projected_exactly_as_before()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(
            fixture, tenant, connection, new SyntheticEvent { UpdatedAt = T0, ValueConfirmation = "Confirmed" }, "evt-explicitly-confirmed");

        var result = await host.ProcessSettledAsync(delivered.Envelope);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);
        Assert.Null(result.ReasonCode);
        var fact = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
        Assert.Equal(("status", "done"), (fact.Field, fact.Value));
    }
}
