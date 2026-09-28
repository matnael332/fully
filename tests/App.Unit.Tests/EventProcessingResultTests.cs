using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 2D handoff: only an accepted result can reach projection, and only with its advanced fields; every
/// other outcome has no handoff by construction. Canonical event ids derived from receipts are deterministic.
/// </summary>
public sealed class EventProcessingResultTests
{
    private static readonly Guid ReceiptId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly OrderingEvidence Incoming = new(5, null, null, "evt-5");

    [Fact]
    public void An_accepted_result_hands_over_the_event_and_only_its_advanced_fields()
    {
        var canonical = Event("status", "due_date");
        var fields = new[]
        {
            Decision("status", FieldOrderingDecision.Advance),
            Decision("due_date", FieldOrderingDecision.Stale),
        };

        var result = EventProcessingResult.Accepted(canonical, ReceiptId, fields, duplicateCandidateOf: null);

        Assert.Equal(EventProcessingOutcome.AcceptedForProjection, result.Outcome);
        Assert.True(result.TryGetProjection(out var handoff));
        Assert.Same(canonical, handoff!.Event);
        Assert.Equal(["status"], handoff.AdvancedFields.Select(field => field.Field));
        Assert.Equal(2, result.Fields.Count);
    }

    [Fact]
    public void No_other_outcome_can_produce_a_projection_handoff()
    {
        var canonical = Event("status");
        var results = new[]
        {
            EventProcessingResult.Duplicate(canonical.Metadata.EventId, ReceiptId, EventProcessingOutcome.AcceptedForProjection),
            EventProcessingResult.Stale(canonical, ReceiptId, [Decision("status", FieldOrderingDecision.Stale)], duplicateCandidateOf: null),
            EventProcessingResult.ReconciliationRequired(canonical, ReceiptId, ProcessingErrors.OrderingAmbiguous.Code, [Decision("status", FieldOrderingDecision.Ambiguous)], duplicateCandidateOf: null),
            EventProcessingResult.Quarantined(canonical.Metadata.EventId, ReceiptId, QuarantineReason.AuthorityConflict, ProcessingErrors.AuthorityConflict.Code, canonical.Metadata.WorkObject, [Decision("status", FieldOrderingDecision.Withheld)]),
        };

        Assert.All(results, result =>
        {
            Assert.False(result.TryGetProjection(out var handoff));
            Assert.Null(handoff);
        });
    }

    [Fact]
    public void An_accepted_result_must_advance_a_field_and_carry_nothing_held()
    {
        var canonical = Event("status", "due_date");

        Assert.Throws<ArgumentException>(() => EventProcessingResult.Accepted(
            canonical, ReceiptId, [Decision("status", FieldOrderingDecision.Stale)], duplicateCandidateOf: null));
        Assert.Throws<ArgumentException>(() => EventProcessingResult.Accepted(
            canonical, ReceiptId, [Decision("status", FieldOrderingDecision.Advance), Decision("due_date", FieldOrderingDecision.Ambiguous)], duplicateCandidateOf: null));
        Assert.Throws<ArgumentException>(() => EventProcessingResult.Accepted(
            canonical, ReceiptId, [Decision("priority", FieldOrderingDecision.Advance)], duplicateCandidateOf: null));
    }

    [Fact]
    public void A_quarantine_needs_a_canonical_reason_and_a_code()
    {
        var canonical = Event("status");

        Assert.Throws<ArgumentException>(() => EventProcessingResult.Quarantined(
            canonical.Metadata.EventId, ReceiptId, QuarantineReason.None, "processing.x", workObject: null, []));
        Assert.Throws<ArgumentException>(() => EventProcessingResult.Quarantined(
            canonical.Metadata.EventId, ReceiptId, QuarantineReason.BadSchema, " ", workObject: null, []));
    }

    [Fact]
    public void The_string_form_carries_ids_and_the_outcome_only()
    {
        var canonical = Event("status");
        var result = EventProcessingResult.Accepted(canonical, ReceiptId, [Decision("status", FieldOrderingDecision.Advance)], duplicateCandidateOf: null);

        Assert.DoesNotContain("secret-after-value", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-before-value", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_always_derives_the_same_canonical_event_id_and_different_receipts_differ()
    {
        var first = EventIdentity.ForReceipt(ReceiptId);
        var again = EventIdentity.ForReceipt(ReceiptId);
        var other = EventIdentity.ForReceipt(Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(ReceiptId, first.Value);

        // An RFC 9562 version 8 identifier with the RFC variant.
        var text = first.Value.ToString("D");
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");

        Assert.Throws<ArgumentException>(() => EventIdentity.ForReceipt(Guid.Empty));
    }

    private static FieldProcessingDecision Decision(string field, FieldOrderingDecision decision) =>
        new(field, FactDataType.TaskStatus, decision, Incoming, Previous: null, PreviousEventId: null);

    private static CanonicalEvent Event(params string[] fields)
    {
        var connection = "0199a1b2-c3d4-7e5f-8a9b-aaaaaaaaaaaa";
        var created = CanonicalEvent.Create(
            new CanonicalEventMetadata(
                EventIdentity.ForReceipt(ReceiptId),
                TenantId.From(Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-bbbbbbbbbbbb")),
                CanonicalEventSchemas.ObjectChangedType,
                CanonicalEventSchemas.ObjectChangedVersion1,
                new EventSource(EventSourceKind.SaasConnector, "synthetic", connection),
                new ObjectReference("synthetic", connection, "task", "1001"),
                "evt-5",
                5,
                occurredAt: null,
                sourceUpdatedAt: null,
                new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 28, 12, 0, 1, TimeSpan.Zero),
                CorrelationId.New(),
                ReceiptId),
            new CanonicalEventContent(
                fields.Select(field => new FieldChange(field, FactDataType.TaskStatus, "secret-before-value", "secret-after-value")),
                actor: null,
                sourceReference: null));
        Assert.True(created.IsSuccess, created.IsSuccess ? string.Empty : created.Error.Code);
        return created.Value;
    }
}
