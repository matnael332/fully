using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Processing;

/// <summary>
/// The ordering evidence of one change: what the source said about its order, never when it was received.
/// </summary>
/// <param name="SourceRevision">The provider's revision or sequence for the object, if any.</param>
/// <param name="SourceUpdatedAtUtc">The source's update time for the object, if any.</param>
/// <param name="OccurredAtUtc">When the change occurred, if the source said.</param>
/// <param name="SourceEventId">The provider's event id, if any; it tells two events at one revision apart.</param>
public sealed record OrderingEvidence(
    long? SourceRevision,
    DateTimeOffset? SourceUpdatedAtUtc,
    DateTimeOffset? OccurredAtUtc,
    string? SourceEventId)
{
    /// <summary>Gets a value indicating whether the evidence can order the change at all.</summary>
    public bool HasOrderingSignal => SourceRevision is not null || SourceUpdatedAtUtc is not null || OccurredAtUtc is not null;
}

/// <summary>How one change's evidence compares with the last accepted change of the same field.</summary>
public enum OrderingComparison
{
    /// <summary>Unspecified. Never returned.</summary>
    None = 0,

    /// <summary>The change is newer.</summary>
    Newer = 1,

    /// <summary>The change is older.</summary>
    Older = 2,

    /// <summary>The evidence says the two are at the same point.</summary>
    Equal = 3,

    /// <summary>The two share no signal that could order them.</summary>
    NotComparable = 4,
}

/// <summary>The signal that decided a comparison, in precedence order.</summary>
public enum OrderingSignal
{
    /// <summary>No signal decided.</summary>
    None = 0,

    /// <summary>The provider revision or sequence.</summary>
    SourceRevision = 1,

    /// <summary>The source's update time for the object.</summary>
    SourceUpdatedTime = 2,

    /// <summary>The occurrence time.</summary>
    OccurrenceTime = 3,
}

/// <summary>A comparison and the signal that decided it.</summary>
/// <param name="Comparison">How the change compares.</param>
/// <param name="Signal">The signal that decided it.</param>
public sealed record OrderingVerdict(OrderingComparison Comparison, OrderingSignal Signal);

/// <summary>What processing decided for one field of an event.</summary>
public enum FieldOrderingDecision
{
    /// <summary>Unspecified. Never recorded.</summary>
    None = 0,

    /// <summary>The change is newer than what is accepted, or the first one: it advances the field.</summary>
    Advance = 1,

    /// <summary>The change is older than what is accepted: it never overwrites the field.</summary>
    Stale = 2,

    /// <summary>The order cannot be decided: nothing is guessed and the object needs reconciliation.</summary>
    Ambiguous = 3,

    /// <summary>The order is impossible: the event is quarantined.</summary>
    Impossible = 4,

    /// <summary>
    /// The field was not applied because the event as a whole was quarantined or held for reconciliation.
    /// </summary>
    Withheld = 5,
}

/// <summary>
/// The frozen ordering precedence (architecture §3): provider revision, then the authoritative source's update
/// time, then the occurrence time; if that is still ambiguous, nothing is overwritten and the object is
/// reconciled.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is per object and field, never global, and receipt time never orders anything (invariants 12 and
/// 18). A revision decides only when both sides have one; equal revisions mean the source says both describe
/// the same revision, so timestamps are not consulted to break that tie. Equal or missing timestamps fall
/// through to the next signal.
/// </para>
/// <para>
/// Receipt time appears only in the plausibility check: a source that claims a change after the product
/// received it is describing an impossible order, not a later one.
/// </para>
/// </remarks>
public static class OrderingPolicy
{
    /// <summary>Compares a change's evidence with the evidence of the last accepted change of the same field.</summary>
    /// <exception cref="ArgumentNullException">Either evidence is null.</exception>
    public static OrderingVerdict Compare(OrderingEvidence incoming, OrderingEvidence current)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(current);

        if (incoming.SourceRevision is { } incomingRevision && current.SourceRevision is { } currentRevision)
        {
            return new OrderingVerdict(Direction(incomingRevision.CompareTo(currentRevision)), OrderingSignal.SourceRevision);
        }

        var updated = CompareInstants(incoming.SourceUpdatedAtUtc, current.SourceUpdatedAtUtc);
        if (updated is OrderingComparison.Newer or OrderingComparison.Older)
        {
            return new OrderingVerdict(updated, OrderingSignal.SourceUpdatedTime);
        }

        var occurred = CompareInstants(incoming.OccurredAtUtc, current.OccurredAtUtc);
        if (occurred is OrderingComparison.Newer or OrderingComparison.Older)
        {
            return new OrderingVerdict(occurred, OrderingSignal.OccurrenceTime);
        }

        if (updated == OrderingComparison.Equal)
        {
            return new OrderingVerdict(OrderingComparison.Equal, OrderingSignal.SourceUpdatedTime);
        }

        return occurred == OrderingComparison.Equal
            ? new OrderingVerdict(OrderingComparison.Equal, OrderingSignal.OccurrenceTime)
            : new OrderingVerdict(OrderingComparison.NotComparable, OrderingSignal.None);
    }

    /// <summary>
    /// Decides one field: advance when newer (or the first evidenced change), stale when older, impossible when a
    /// different provider event claims the accepted revision, and ambiguous otherwise.
    /// </summary>
    /// <param name="incoming">The change's evidence.</param>
    /// <param name="current">The last accepted change's evidence, or null when the field has none yet.</param>
    /// <exception cref="ArgumentNullException">The incoming evidence is null.</exception>
    public static FieldOrderingDecision Decide(OrderingEvidence incoming, OrderingEvidence? current)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (current is null)
        {
            return incoming.HasOrderingSignal ? FieldOrderingDecision.Advance : FieldOrderingDecision.Ambiguous;
        }

        var verdict = Compare(incoming, current);
        return verdict.Comparison switch
        {
            OrderingComparison.Newer => FieldOrderingDecision.Advance,
            OrderingComparison.Older => FieldOrderingDecision.Stale,
            OrderingComparison.Equal when verdict.Signal == OrderingSignal.SourceRevision
                && incoming.SourceEventId is { } incomingEvent
                && current.SourceEventId is { } currentEvent
                && !string.Equals(incomingEvent, currentEvent, StringComparison.Ordinal) => FieldOrderingDecision.Impossible,
            _ => FieldOrderingDecision.Ambiguous,
        };
    }

    /// <summary>
    /// Checks that the evidence describes a possible order: no source time later than the receipt, and no
    /// occurrence later than the object's reported update, each beyond the tolerated clock skew.
    /// </summary>
    /// <returns>Null when plausible, otherwise the stable reason the order is impossible.</returns>
    /// <exception cref="ArgumentNullException">The evidence is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tolerance is negative.</exception>
    public static Error? ImpossibilityOf(OrderingEvidence evidence, DateTimeOffset receivedAtUtc, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);

        var latestPossible = receivedAtUtc + tolerance;
        if (evidence.SourceUpdatedAtUtc > latestPossible || evidence.OccurredAtUtc > latestPossible)
        {
            return ProcessingErrors.SourceTimeAfterReceipt;
        }

        if (evidence.OccurredAtUtc is { } occurred
            && evidence.SourceUpdatedAtUtc is { } updated
            && occurred > updated + tolerance)
        {
            return ProcessingErrors.OccurredAfterUpdate;
        }

        return null;
    }

    private static OrderingComparison Direction(int sign) => sign switch
    {
        > 0 => OrderingComparison.Newer,
        < 0 => OrderingComparison.Older,
        _ => OrderingComparison.Equal,
    };

    private static OrderingComparison CompareInstants(DateTimeOffset? incoming, DateTimeOffset? current) =>
        incoming is { } left && current is { } right
            ? Direction(left.CompareTo(right))
            : OrderingComparison.NotComparable;
}
