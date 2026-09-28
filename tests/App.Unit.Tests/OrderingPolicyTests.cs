using WorkContinuity.Core.Processing;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 2D ordering precedence (architecture §3): provider revision, then source update time, then
/// occurrence time; equal or incomparable evidence is ambiguous and never overwrites; receipt time never
/// orders; impossible evidence is recognised deterministically.
/// </summary>
public sealed class OrderingPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static OrderingEvidence Evidence(long? revision = null, int? updatedMinutes = null, int? occurredMinutes = null, string? eventId = null) =>
        new(revision, updatedMinutes is { } u ? T0.AddMinutes(u) : null, occurredMinutes is { } o ? T0.AddMinutes(o) : null, eventId);

    [Fact]
    public void A_revision_decides_when_both_sides_have_one_even_against_the_timestamps()
    {
        // The newer revision carries the older timestamps: the revision still decides.
        var verdict = OrderingPolicy.Compare(Evidence(revision: 8, updatedMinutes: 1, occurredMinutes: 1), Evidence(revision: 7, updatedMinutes: 9, occurredMinutes: 9));

        Assert.Equal(new OrderingVerdict(OrderingComparison.Newer, OrderingSignal.SourceRevision), verdict);
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Older, OrderingSignal.SourceRevision),
            OrderingPolicy.Compare(Evidence(revision: 6, updatedMinutes: 9), Evidence(revision: 7, updatedMinutes: 1)));
    }

    [Fact]
    public void Equal_revisions_are_equal_and_timestamps_do_not_break_the_tie()
    {
        var verdict = OrderingPolicy.Compare(Evidence(revision: 7, updatedMinutes: 9), Evidence(revision: 7, updatedMinutes: 1));

        Assert.Equal(new OrderingVerdict(OrderingComparison.Equal, OrderingSignal.SourceRevision), verdict);
    }

    [Fact]
    public void Without_comparable_revisions_the_source_update_time_decides_then_the_occurrence_time()
    {
        // One side lacks a revision, so revisions are not comparable.
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Newer, OrderingSignal.SourceUpdatedTime),
            OrderingPolicy.Compare(Evidence(revision: 3, updatedMinutes: 5), Evidence(updatedMinutes: 4)));
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Older, OrderingSignal.SourceUpdatedTime),
            OrderingPolicy.Compare(Evidence(updatedMinutes: 3, occurredMinutes: 9), Evidence(updatedMinutes: 4, occurredMinutes: 1)));

        // Equal update times fall through to the occurrence time.
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Newer, OrderingSignal.OccurrenceTime),
            OrderingPolicy.Compare(Evidence(updatedMinutes: 4, occurredMinutes: 3), Evidence(updatedMinutes: 4, occurredMinutes: 2)));

        // Missing update times on one side also fall through to the occurrence time.
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Older, OrderingSignal.OccurrenceTime),
            OrderingPolicy.Compare(Evidence(occurredMinutes: 1), Evidence(updatedMinutes: 4, occurredMinutes: 2)));
    }

    [Fact]
    public void Equal_or_missing_evidence_is_equal_or_not_comparable_never_a_guess()
    {
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Equal, OrderingSignal.SourceUpdatedTime),
            OrderingPolicy.Compare(Evidence(updatedMinutes: 4), Evidence(updatedMinutes: 4)));
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.Equal, OrderingSignal.OccurrenceTime),
            OrderingPolicy.Compare(Evidence(occurredMinutes: 4), Evidence(updatedMinutes: 2, occurredMinutes: 4)));
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.NotComparable, OrderingSignal.None),
            OrderingPolicy.Compare(Evidence(revision: 5), Evidence(updatedMinutes: 4)));
        Assert.Equal(
            new OrderingVerdict(OrderingComparison.NotComparable, OrderingSignal.None),
            OrderingPolicy.Compare(Evidence(updatedMinutes: 4), Evidence(occurredMinutes: 4)));
    }

    [Fact]
    public void Decisions_advance_newer_keep_older_stale_and_hold_everything_else()
    {
        Assert.Equal(FieldOrderingDecision.Advance, OrderingPolicy.Decide(Evidence(revision: 2), current: null));
        Assert.Equal(FieldOrderingDecision.Advance, OrderingPolicy.Decide(Evidence(revision: 2), Evidence(revision: 1)));
        Assert.Equal(FieldOrderingDecision.Stale, OrderingPolicy.Decide(Evidence(revision: 1), Evidence(revision: 2)));
        Assert.Equal(FieldOrderingDecision.Stale, OrderingPolicy.Decide(Evidence(updatedMinutes: 1), Evidence(updatedMinutes: 2)));
        Assert.Equal(FieldOrderingDecision.Ambiguous, OrderingPolicy.Decide(Evidence(updatedMinutes: 2), Evidence(updatedMinutes: 2)));
        Assert.Equal(FieldOrderingDecision.Ambiguous, OrderingPolicy.Decide(Evidence(revision: 2), Evidence(updatedMinutes: 2)));

        // Evidence without any signal can never be accepted, not even as the first change.
        Assert.Equal(FieldOrderingDecision.Ambiguous, OrderingPolicy.Decide(Evidence(), current: null));
    }

    [Fact]
    public void A_different_provider_event_at_an_accepted_revision_is_impossible_but_an_unidentified_one_is_ambiguous()
    {
        Assert.Equal(
            FieldOrderingDecision.Impossible,
            OrderingPolicy.Decide(Evidence(revision: 5, eventId: "evt-b"), Evidence(revision: 5, eventId: "evt-a")));
        Assert.Equal(
            FieldOrderingDecision.Ambiguous,
            OrderingPolicy.Decide(Evidence(revision: 5), Evidence(revision: 5, eventId: "evt-a")));
        Assert.Equal(
            FieldOrderingDecision.Ambiguous,
            OrderingPolicy.Decide(Evidence(revision: 5, eventId: "evt-a"), Evidence(revision: 5, eventId: "evt-a")));

        // Equal timestamps with different provider events are not a revision claim: ambiguous, not impossible.
        Assert.Equal(
            FieldOrderingDecision.Ambiguous,
            OrderingPolicy.Decide(Evidence(updatedMinutes: 5, eventId: "evt-b"), Evidence(updatedMinutes: 5, eventId: "evt-a")));
    }

    [Fact]
    public void A_source_time_after_the_receipt_or_an_occurrence_after_the_update_is_impossible_beyond_the_tolerance()
    {
        var received = T0.AddMinutes(10);
        var tolerance = TimeSpan.FromMinutes(5);

        Assert.Null(OrderingPolicy.ImpossibilityOf(Evidence(updatedMinutes: 14, occurredMinutes: 13), received, tolerance));
        Assert.Null(OrderingPolicy.ImpossibilityOf(Evidence(updatedMinutes: 15), received, tolerance));
        Assert.Null(OrderingPolicy.ImpossibilityOf(Evidence(revision: 3), received, tolerance));

        Assert.Equal(ProcessingErrors.SourceTimeAfterReceipt, OrderingPolicy.ImpossibilityOf(Evidence(updatedMinutes: 16), received, tolerance));
        Assert.Equal(ProcessingErrors.SourceTimeAfterReceipt, OrderingPolicy.ImpossibilityOf(Evidence(occurredMinutes: 16), received, tolerance));
        Assert.Equal(ProcessingErrors.OccurredAfterUpdate, OrderingPolicy.ImpossibilityOf(Evidence(updatedMinutes: 0, occurredMinutes: 6), received, tolerance));
        Assert.Null(OrderingPolicy.ImpossibilityOf(Evidence(updatedMinutes: 0, occurredMinutes: 5), received, tolerance));

        Assert.Throws<ArgumentOutOfRangeException>(() => OrderingPolicy.ImpossibilityOf(Evidence(revision: 1), received, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Evidence_without_revision_or_source_times_has_no_ordering_signal()
    {
        Assert.False(Evidence(eventId: "evt-1").HasOrderingSignal);
        Assert.True(Evidence(revision: 0).HasOrderingSignal);
        Assert.True(Evidence(updatedMinutes: 0).HasOrderingSignal);
        Assert.True(Evidence(occurredMinutes: 0).HasOrderingSignal);
    }
}
