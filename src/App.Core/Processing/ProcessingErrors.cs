using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Processing;

/// <summary>
/// The stable processing error codes. Codes are safe tokens recorded in the ledger and the logs; messages are
/// fixed text that never echoes a payload, a value or a provider message.
/// </summary>
public static class ProcessingErrors
{
    /// <summary>The receipt the message names is not visible in the message's tenant: forged or cross-tenant.</summary>
    public static Error ReceiptNotVisible { get; } =
        Error.NotFound("processing.receipt.not_visible", "The receipt the message names is not visible in its tenant.");

    /// <summary>The message's tenant is not the tenant of the processing scope.</summary>
    public static Error TenantMismatch { get; } =
        Error.Forbidden("processing.tenant.mismatch", "The message's tenant is not the processing scope's tenant.");

    /// <summary>The event could not be processed now. Transient: nothing was committed and it is retried.</summary>
    public static Error Unavailable { get; } =
        Error.Infrastructure("processing.unavailable", "The event could not be processed now; it will be retried.");

    /// <summary>No normalizer understands the source's payload schema version.</summary>
    public static Error NormalizerMissing { get; } =
        Error.Validation("processing.normalizer.missing", "No normalizer supports the source payload schema version.");

    /// <summary>The normalizer refused the payload.</summary>
    public static Error NormalizerRejected { get; } =
        Error.Validation("processing.normalizer.rejected", "The source payload could not be normalized.");

    /// <summary>The normalizer failed instead of reporting a result.</summary>
    public static Error NormalizerFailed { get; } =
        Error.Validation("processing.normalizer.failed", "The normalizer failed on the source payload.");

    /// <summary>The source is not authoritative for a field the event changes.</summary>
    public static Error AuthorityConflict { get; } =
        Error.Conflict("processing.authority.conflict", "The source is not authoritative for a field the event changes.");

    /// <summary>A source time is later than the receipt beyond the tolerated clock skew.</summary>
    public static Error SourceTimeAfterReceipt { get; } =
        Error.Conflict("processing.ordering.source_time_after_receipt", "A source time is later than the receipt beyond the tolerated clock skew.");

    /// <summary>The change occurred after the object's reported update beyond the tolerated clock skew.</summary>
    public static Error OccurredAfterUpdate { get; } =
        Error.Conflict("processing.ordering.occurred_after_update", "The change occurred after the object's reported update.");

    /// <summary>A different provider event claims a revision already accepted for the field.</summary>
    public static Error RevisionClaimedTwice { get; } =
        Error.Conflict("processing.ordering.revision_conflict", "A different provider event claims an already accepted revision.");

    /// <summary>The event's order relative to the accepted state cannot be decided.</summary>
    public static Error OrderingAmbiguous { get; } =
        Error.Conflict("processing.ordering.ambiguous", "The event's order relative to the accepted state cannot be decided.");

    /// <summary>
    /// The event signals a change whose values the source has not confirmed: an authoritative read of the source must
    /// confirm them before anything is applied (Gate 3D).
    /// </summary>
    public static Error UnconfirmedSourceValues { get; } =
        Error.Conflict("processing.source.unconfirmed", "The change must be confirmed by an authoritative read of the source before it is applied.");

    /// <summary>The event carries no revision, source update time or occurrence time.</summary>
    public static Error NoOrderingEvidence { get; } =
        Error.Conflict("processing.ordering.no_evidence", "The event carries no revision, source update time or occurrence time.");

    /// <summary>
    /// The receipt's raw content reached the end of its retention window before it was processed. Permanent: the
    /// content no longer exists, so the message cannot be processed as delivered.
    /// </summary>
    public static Error ReceiptContentExpired { get; } =
        Error.NotFound("processing.receipt.content_expired", "The receipt's raw content expired before it was processed.");

    /// <summary>
    /// The receipt's connection is in initial sync and not Live, and the receipt lies above its replay floor (Gate 3F,
    /// decision D3F-12): nothing was decided, the receipt stays durable and unprocessed, and the sync generation replays it
    /// before its cutover. A handled outcome: the transport completes the message, so nothing is redelivered.
    /// </summary>
    public static Error HeldForInitialSync { get; } =
        Error.Conflict("processing.receipt.held_for_initial_sync", "The receipt is held until its connection's initial sync replays it.");
}
