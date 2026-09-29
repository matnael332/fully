namespace WorkContinuity.Core.Retention;

/// <summary>
/// The retention rule for raw work-event content (PRD §12, Gate 2 owner decision 5): never kept longer than 30
/// days after the product durably received it, and purged from 28 days, so that the limit holds between purge
/// runs.
/// </summary>
/// <remarks>
/// <para>
/// Raw content is a receipt's payload and its payload fingerprint. Its age is the database's own clock less the
/// receipt time, which the database itself stamps when it stores the receipt; the database clock is the only clock
/// that decides when content is due and how late it is, and neither an application clock nor a time the provider
/// states decides anything here, so this rule offers no application-clock cutoff.
/// </para>
/// <para>
/// <see cref="Window"/> is the hard limit, never a purge setting: purging starts <see cref="PurgeLeadMargin"/>
/// earlier, at <see cref="PurgeAge"/>, and content still held past <see cref="OverdueAge"/> or past the limit is
/// reported. Every value is a fixed rule, not a setting, so nothing can be configured to purge earlier or to keep
/// content longer. The Snapshot and the event ledger's metadata are not raw content and are not subject to it.
/// </para>
/// </remarks>
public static class RawContentRetention
{
    /// <summary>Gets the hard limit: raw content must never be older than this.</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromDays(30);

    /// <summary>Gets how long before the limit raw content is purged.</summary>
    public static TimeSpan PurgeLeadMargin { get; } = TimeSpan.FromHours(48);

    /// <summary>Gets the age at which raw content becomes due for purge: the limit less the lead margin, 28 days.</summary>
    public static TimeSpan PurgeAge { get; } = Window - PurgeLeadMargin;

    /// <summary>Gets how long content may stay past its due age before it counts as overdue.</summary>
    public static TimeSpan OverdueGrace { get; } = TimeSpan.FromHours(6);

    /// <summary>Gets the age from which content still held is overdue: 28 days and 6 hours.</summary>
    public static TimeSpan OverdueAge { get; } = PurgeAge + OverdueGrace;

    /// <summary>How late content of the given database-clock age is; no content is within the limit.</summary>
    public static RawContentRetentionStatus StatusOf(TimeSpan? age) => age switch
    {
        { } value when value >= Window => RawContentRetentionStatus.LimitExceeded,
        { } value when value >= OverdueAge => RawContentRetentionStatus.Overdue,
        _ => RawContentRetentionStatus.WithinLimit,
    };
}

/// <summary>How late raw content is relative to its purge age and the hard limit.</summary>
public enum RawContentRetentionStatus
{
    /// <summary>Unspecified. Never reported.</summary>
    None = 0,

    /// <summary>Younger than the overdue age: as the hourly purge leaves it.</summary>
    WithinLimit = 1,

    /// <summary>At or past the overdue age but younger than the limit: purges are late.</summary>
    Overdue = 2,

    /// <summary>At or past the 30-day limit: the retention rule was broken.</summary>
    LimitExceeded = 3,
}

/// <summary>
/// The schedule the purge must run on once it is activated, and the age it implies. These are operating
/// assumptions that the activation must meet and its alerts must watch, not settings; nothing schedules the purge
/// yet.
/// </summary>
/// <remarks>
/// Content stored at <c>t</c> by the database's clock is due when that clock reaches <c>t + PurgeAge</c>: the database
/// stamps the receipt time and measures the age, so no application clock plays a part, and the clock skew budgets
/// for the database clock itself being corrected by up to that much while content ages. The next scheduled start
/// after the due moment is less than one cadence away, it may start up to the start delay late, and it clears the
/// content within the run duration. Every run succeeding, content is therefore never older than
/// <see cref="NormalMaximumAge"/>.
/// </remarks>
public static class RawContentPurgeSchedule
{
    /// <summary>Gets the interval between scheduled purge runs.</summary>
    public static TimeSpan Cadence { get; } = TimeSpan.FromHours(1);

    /// <summary>Gets how late a scheduled run may start.</summary>
    public static TimeSpan AllowedStartDelay { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long one run may take; the job's timeout enforces it.</summary>
    public static TimeSpan MaxRunDuration { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets how far the database's clock may be corrected, for example by time synchronization, while content ages.</summary>
    public static TimeSpan AllowedClockSkew { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets the oldest raw content can become while every scheduled run succeeds: the purge age plus one cadence, the
    /// start delay, the run duration and the clock skew, 28 days, 1 hour and 21 minutes.
    /// </summary>
    public static TimeSpan NormalMaximumAge { get; } =
        RawContentRetention.PurgeAge + Cadence + AllowedStartDelay + MaxRunDuration + AllowedClockSkew;
}

/// <summary>How a purge run ended.</summary>
public enum RawContentPurgeOutcome
{
    /// <summary>Unspecified. Never reported.</summary>
    None = 0,

    /// <summary>Every tenant found was purged and no due content remains.</summary>
    Completed = 1,

    /// <summary>
    /// Nothing failed, but due content remains for a later run: more tenants than one run visits, a tenant's budget
    /// ran out, or rows were held by processing or another run.
    /// </summary>
    Partial = 2,

    /// <summary>Discovery, a measurement or at least one tenant failed; nothing that failed was changed.</summary>
    Failed = 3,
}

/// <summary>
/// What one purge run did: counts, ages by the database's clock and a duration, never an id of a receipt or a tenant.
/// </summary>
/// <param name="TenantsVisited">Tenants holding due content that the run visited, failed ones included.</param>
/// <param name="TenantFailures">Tenants whose purge failed part-way; what failed was rolled back for a later run.</param>
/// <param name="DiscoveryFailed">
/// Whether finding the tenants with due content, before or after purging, or measuring what remains failed.
/// </param>
/// <param name="ProcessedPurged">Receipts purged after processing settled them.</param>
/// <param name="QuarantinedPurged">Receipts purged after processing quarantined them.</param>
/// <param name="UnprocessedPurged">Receipts purged before any processing settled them: those events are lost.</param>
/// <param name="DueContentRemaining">
/// Whether due content may remain after the run: true unless the run proved none remains.
/// </param>
/// <param name="OldestPurgedAge">The database-clock age of the oldest content the run purged, if it purged any.</param>
/// <param name="OldestRemainingAge">
/// The database-clock age of the oldest due content left after the run, if any is known to remain.
/// </param>
/// <param name="Duration">How long the run took.</param>
public sealed record RawContentPurgeRun(
    int TenantsVisited,
    int TenantFailures,
    bool DiscoveryFailed,
    int ProcessedPurged,
    int QuarantinedPurged,
    int UnprocessedPurged,
    bool DueContentRemaining,
    TimeSpan? OldestPurgedAge,
    TimeSpan? OldestRemainingAge,
    TimeSpan Duration)
{
    /// <summary>Gets the number of receipts whose raw content the run purged.</summary>
    public int ReceiptsPurged => ProcessedPurged + QuarantinedPurged + UnprocessedPurged;

    /// <summary>Gets how the run ended.</summary>
    public RawContentPurgeOutcome Outcome => DiscoveryFailed || TenantFailures > 0
        ? RawContentPurgeOutcome.Failed
        : DueContentRemaining ? RawContentPurgeOutcome.Partial : RawContentPurgeOutcome.Completed;

    /// <summary>Gets how late the oldest content the run purged was: a late purge is reported even once it is cleared.</summary>
    public RawContentRetentionStatus PurgedStatus => RawContentRetention.StatusOf(OldestPurgedAge);

    /// <summary>Gets how late the oldest content left after the run is: this is what still needs attention.</summary>
    public RawContentRetentionStatus RemainingStatus => RawContentRetention.StatusOf(OldestRemainingAge);
}

/// <summary>
/// Purges raw work-event content that is due: one bounded, idempotent run per call, safe to repeat, to retry after
/// a failure and to run alongside another run.
/// </summary>
public interface IRawContentPurge
{
    /// <summary>Runs one bounded purge of the content that is due now.</summary>
    Task<RawContentPurgeRun> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
