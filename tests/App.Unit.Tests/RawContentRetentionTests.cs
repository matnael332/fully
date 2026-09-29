using WorkContinuity.Core.Retention;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The retention rule for raw work-event content: purged from a fixed 28 days, never kept past the fixed 30-day
/// limit, both measured by the database's clock, which the purge's own statements prove at the microsecond. Lateness
/// is classified exactly at the overdue age and the limit. The hourly schedule's normal maximum age is derived from
/// its stated assumptions and stays below both. A purge run's outcome fails whenever any step failed and is partial
/// whenever due content may remain.
/// </summary>
public sealed class RawContentRetentionTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    [Fact]
    public void The_limit_the_lead_margin_the_purge_age_and_the_overdue_age_are_fixed()
    {
        Assert.Equal(TimeSpan.FromDays(30), RawContentRetention.Window);
        Assert.Equal(TimeSpan.FromHours(720), RawContentRetention.Window);
        Assert.Equal(TimeSpan.FromHours(48), RawContentRetention.PurgeLeadMargin);
        Assert.Equal(TimeSpan.FromDays(28), RawContentRetention.PurgeAge);
        Assert.Equal(TimeSpan.FromHours(672), RawContentRetention.PurgeAge);
        Assert.Equal(TimeSpan.FromHours(6), RawContentRetention.OverdueGrace);
        Assert.Equal(TimeSpan.FromHours(678), RawContentRetention.OverdueAge);
    }

    [Fact]
    public void Lateness_is_classified_exactly_at_the_overdue_age_and_at_the_thirty_day_limit()
    {
        Assert.Equal(RawContentRetentionStatus.WithinLimit, RawContentRetention.StatusOf(null));
        Assert.Equal(RawContentRetentionStatus.WithinLimit, RawContentRetention.StatusOf(RawContentRetention.PurgeAge));
        Assert.Equal(RawContentRetentionStatus.WithinLimit, RawContentRetention.StatusOf(RawContentRetention.OverdueAge - Tick));
        Assert.Equal(RawContentRetentionStatus.Overdue, RawContentRetention.StatusOf(RawContentRetention.OverdueAge));
        Assert.Equal(RawContentRetentionStatus.Overdue, RawContentRetention.StatusOf(RawContentRetention.Window - Tick));
        Assert.Equal(RawContentRetentionStatus.LimitExceeded, RawContentRetention.StatusOf(RawContentRetention.Window));
        Assert.Equal(RawContentRetentionStatus.LimitExceeded, RawContentRetention.StatusOf(TimeSpan.FromDays(400)));
    }

    [Fact]
    public void The_normal_maximum_age_is_derived_from_the_schedule_assumptions_and_stays_below_overdue_and_the_limit()
    {
        Assert.Equal(TimeSpan.FromHours(1), RawContentPurgeSchedule.Cadence);
        Assert.Equal(TimeSpan.FromMinutes(5), RawContentPurgeSchedule.AllowedStartDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), RawContentPurgeSchedule.MaxRunDuration);
        Assert.Equal(TimeSpan.FromMinutes(1), RawContentPurgeSchedule.AllowedClockSkew);

        // Purge age + cadence + start delay + run duration + clock skew: 28 days, 1 hour and 21 minutes.
        Assert.Equal(
            RawContentRetention.PurgeAge
                + RawContentPurgeSchedule.Cadence
                + RawContentPurgeSchedule.AllowedStartDelay
                + RawContentPurgeSchedule.MaxRunDuration
                + RawContentPurgeSchedule.AllowedClockSkew,
            RawContentPurgeSchedule.NormalMaximumAge);
        Assert.Equal(TimeSpan.FromDays(28) + TimeSpan.FromMinutes(81), RawContentPurgeSchedule.NormalMaximumAge);

        // Normal operation never looks overdue, and overdue content is reported well before the limit.
        Assert.True(RawContentPurgeSchedule.NormalMaximumAge < RawContentRetention.OverdueAge);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, RawContentRetention.StatusOf(RawContentPurgeSchedule.NormalMaximumAge));
        Assert.True(RawContentRetention.OverdueAge < RawContentRetention.Window);
    }

    [Fact]
    public void A_run_fails_when_any_step_failed_and_is_partial_whenever_due_content_may_remain()
    {
        var completed = Run();

        Assert.Equal(RawContentPurgeOutcome.Completed, completed.Outcome);
        Assert.Equal(RawContentPurgeOutcome.Partial, (completed with { DueContentRemaining = true }).Outcome);
        Assert.Equal(RawContentPurgeOutcome.Failed, (completed with { TenantFailures = 1 }).Outcome);
        Assert.Equal(RawContentPurgeOutcome.Failed, (completed with { DiscoveryFailed = true, DueContentRemaining = true }).Outcome);
        Assert.Equal(RawContentPurgeOutcome.Failed, (completed with { TenantFailures = 2, DueContentRemaining = true }).Outcome);
    }

    [Fact]
    public void A_run_reports_how_late_the_content_it_purged_was_apart_from_how_late_the_content_it_left_is()
    {
        var lateButCleared = Run() with { OldestPurgedAge = RawContentRetention.Window + TimeSpan.FromHours(3) };
        Assert.Equal(RawContentRetentionStatus.LimitExceeded, lateButCleared.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.WithinLimit, lateButCleared.RemainingStatus);

        var stillHeld = Run() with { DueContentRemaining = true, OldestRemainingAge = RawContentRetention.OverdueAge };
        Assert.Equal(RawContentRetentionStatus.WithinLimit, stillHeld.PurgedStatus);
        Assert.Equal(RawContentRetentionStatus.Overdue, stillHeld.RemainingStatus);
    }

    [Fact]
    public void A_run_counts_every_purged_receipt_once_whatever_its_processing_state()
    {
        Assert.Equal(12, (Run() with { ProcessedPurged = 7, QuarantinedPurged = 3, UnprocessedPurged = 2 }).ReceiptsPurged);
        Assert.Equal(0, Run().ReceiptsPurged);
    }

    [Fact]
    public void A_run_reports_ids_of_nothing_and_no_application_clock_time()
    {
        // Counts, ages, flags, statuses and a duration: no member could carry a tenant, a receipt, content or a time
        // read from an application clock.
        Assert.All(
            typeof(RawContentPurgeRun).GetProperties(),
            property => Assert.Contains(
                property.PropertyType,
                new[]
                {
                    typeof(int), typeof(bool), typeof(TimeSpan), typeof(TimeSpan?),
                    typeof(RawContentPurgeOutcome), typeof(RawContentRetentionStatus),
                }));
    }

    private static RawContentPurgeRun Run() => new(
        TenantsVisited: 0,
        TenantFailures: 0,
        DiscoveryFailed: false,
        ProcessedPurged: 0,
        QuarantinedPurged: 0,
        UnprocessedPurged: 0,
        DueContentRemaining: false,
        OldestPurgedAge: null,
        OldestRemainingAge: null,
        Duration: TimeSpan.Zero);
}
