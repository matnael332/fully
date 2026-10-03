using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// Gate 3F's records on their own: a generation changes only for the worker holding its claim and only along the state
/// machine; a claim is exclusive while its lease lasts; fences never go backwards; a restarted generation keeps its replay
/// floor; and a scope is held only for what retrying cannot change, never for a transient failure.
/// </summary>
public sealed class InitialSyncRecordTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly Guid Connection = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_configured_connection_takes_part_not_started_holding_above_its_fence_at_scope_version_one()
    {
        var sync = ConnectionSyncRecord.Configure(Tenant, Connection, "monday", replayFloor: 41, fence: 41, Now);

        Assert.Equal((0L, InitialSyncState.NotStarted, 41L, 41L, 1L), (sync.Generation, sync.State, sync.ReplayFloor, sync.BufferStartFence, sync.ScopeVersion));
        Assert.Null(sync.GenerationScopeVersion);
        Assert.Null(sync.ProviderSubject);
        Assert.Null(sync.AuthorizedAtUtc);
        Assert.Null(sync.StartedAtUtc);
        Assert.False(sync.IsRunning);
        Assert.Throws<InvalidOperationException>(() => sync.Claim(Guid.CreateVersion7(), Now.AddMinutes(5), Now));

        // Its first generation reads the current scope set under the current authority, and keeps the floor.
        sync.ChangeScopeSet("initial_sync.scope_set.changed", Now);
        sync.Restart("58201774", Now.AddDays(-1), 50, Now.AddMinutes(1));

        Assert.Equal((1L, InitialSyncState.Buffering, 41L, 50L, 2L, (long?)2L), (sync.Generation, sync.State, sync.ReplayFloor, sync.BufferStartFence, sync.ScopeVersion, sync.GenerationScopeVersion));
        Assert.Equal(("58201774", (DateTimeOffset?)Now.AddDays(-1), (DateTimeOffset?)Now.AddMinutes(1)), (sync.ProviderSubject, sync.AuthorizedAtUtc, sync.StartedAtUtc));
    }

    [Fact]
    public void A_first_generation_with_no_configuration_row_replays_from_the_floor_it_is_given()
    {
        var sync = Begin(fence: 41);

        Assert.Equal((1L, InitialSyncState.Buffering, 41L, 0L, 1L, (long?)1L), (sync.Generation, sync.State, sync.BufferStartFence, sync.ReplayFloor, sync.ScopeVersion, sync.GenerationScopeVersion));
        Assert.Equal(("58201774", (DateTimeOffset?)Now.AddDays(-1)), (sync.ProviderSubject, sync.AuthorizedAtUtc));
        Assert.Null(sync.ClaimId);
        Assert.Null(sync.CutoverFence);
        Assert.True(sync.IsRunning);
    }

    [Fact]
    public void A_scope_set_change_raises_the_version_and_makes_a_live_running_or_failed_generation_restart_keeping_the_floor()
    {
        foreach (var reach in new[] { "Buffering", "Reading", "Replaying", "Live", "Failed", "RequiresRestart" })
        {
            var sync = Begin(fence: 10);
            var claim = Claimed(sync);
            switch (reach)
            {
                case "Reading":
                    sync.BeginReading(claim, Now);
                    break;
                case "Replaying":
                    sync.BeginReading(claim, Now);
                    sync.BeginReplay(claim, 12, Now);
                    break;
                case "Live":
                    sync.BeginReading(claim, Now);
                    sync.BeginReplay(claim, 12, Now);
                    sync.GoLive(claim, 15, Now);
                    break;
                case "Failed":
                    sync.BeginReading(claim, Now);
                    sync.Fail(claim, "initial_sync.scope.inactive", Now);
                    break;
                case "RequiresRestart":
                    sync.BeginReading(claim, Now);
                    sync.RequireRestart(claim, "initial_sync.authority.changed", Now);
                    break;
            }

            sync.ChangeScopeSet("initial_sync.scope_set.changed", Now.AddMinutes(1));

            Assert.True(
                (InitialSyncState.RequiresRestart, 2L, (long?)1L, "initial_sync.scope_set.changed", 0L) == (sync.State, sync.ScopeVersion, sync.GenerationScopeVersion, sync.LastOutcome, sync.ReplayFloor),
                reach);
            Assert.Null(sync.ClaimId);
            Assert.Null(sync.CutoverFence);
            Assert.Null(sync.LiveAtUtc);

            // The worker that held the generation records nothing more, and it can never become Live.
            Assert.Throws<InvalidOperationException>(() => sync.GoLive(claim, 20, Now.AddMinutes(2)));
            Assert.Throws<InvalidOperationException>(() => sync.Renew(claim, Now.AddMinutes(9), Now.AddMinutes(2)));
        }
    }

    [Fact]
    public void A_generation_that_read_an_earlier_scope_set_never_becomes_live()
    {
        var sync = Begin(fence: 10);
        var claim = Claimed(sync);
        sync.BeginReading(claim, Now);
        sync.BeginReplay(claim, 12, Now);

        // The version rises while the generation still holds its claim in Replaying, as only a broken caller could make it.
        typeof(ConnectionSyncRecord).GetProperty(nameof(ConnectionSyncRecord.ScopeVersion))!.SetValue(sync, 2L);

        Assert.Throws<InvalidOperationException>(() => sync.GoLive(claim, 15, Now));
        Assert.Equal(InitialSyncState.Replaying, sync.State);
    }

    [Fact]
    public void A_claim_is_exclusive_while_its_lease_lasts_and_may_be_taken_over_once_it_ends()
    {
        var sync = Begin();
        var first = Guid.CreateVersion7();
        sync.Claim(first, Now.AddMinutes(5), Now);

        Assert.Throws<InvalidOperationException>(() => sync.Claim(Guid.CreateVersion7(), Now.AddMinutes(6), Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => sync.Claim(Guid.Empty, Now.AddMinutes(6), Now.AddMinutes(6)));

        var second = Guid.CreateVersion7();
        sync.Claim(second, Now.AddMinutes(11), Now.AddMinutes(6));
        Assert.True(sync.IsClaimedBy(second));
        Assert.False(sync.IsClaimedBy(first));

        // The worker whose lease ended records nothing more.
        Assert.Throws<InvalidOperationException>(() => sync.BeginReading(first, Now.AddMinutes(7)));
        Assert.Throws<InvalidOperationException>(() => sync.Renew(first, Now.AddMinutes(12), Now.AddMinutes(7)));
        Assert.Throws<InvalidOperationException>(() => sync.Wait(first, "initial_sync.provider.unavailable", 1, Now.AddMinutes(8), Now.AddMinutes(7)));
        Assert.Equal(InitialSyncState.Buffering, sync.State);
    }

    [Fact]
    public void A_generation_reaches_live_only_along_its_states_with_fences_that_never_go_back_and_live_releases_the_claim()
    {
        var sync = Begin(fence: 10, floor: 10);
        var claim = Claimed(sync);

        // Replay and Live only after reading.
        Assert.Throws<InvalidOperationException>(() => sync.BeginReplay(claim, 12, Now));
        Assert.Throws<InvalidOperationException>(() => sync.GoLive(claim, 12, Now));
        sync.BeginReading(claim, Now);
        Assert.Throws<InvalidOperationException>(() => sync.GoLive(claim, 12, Now));

        // No fence below the floor, no cutover below the replay fence.
        Assert.Throws<InvalidOperationException>(() => sync.BeginReplay(claim, 9, Now));
        sync.BeginReplay(claim, 12, Now);
        Assert.Throws<InvalidOperationException>(() => sync.GoLive(claim, 11, Now));
        Assert.Throws<InvalidOperationException>(() => sync.GoLive(Guid.CreateVersion7(), 15, Now));

        sync.GoLive(claim, 15, Now.AddMinutes(1));

        Assert.Equal((InitialSyncState.Live, (long?)12L, (long?)15L, (DateTimeOffset?)Now.AddMinutes(1)), (sync.State, sync.ReplayFence, sync.CutoverFence, sync.LiveAtUtc));
        Assert.Null(sync.ClaimId);
        Assert.False(sync.IsRunning);

        // Live is final for 3F: no claim, no transition, no restart.
        Assert.Throws<InvalidOperationException>(() => sync.Claim(Guid.CreateVersion7(), Now.AddMinutes(10), Now.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => sync.Restart("58201774", Now, 20, Now));
    }

    [Fact]
    public void A_restarted_generation_keeps_the_replay_floor_and_starts_over_under_its_new_authority()
    {
        var sync = Begin(fence: 10, floor: 10);
        var claim = Claimed(sync);
        sync.BeginReading(claim, Now);
        Assert.Throws<InvalidOperationException>(() => sync.Restart("58201774", Now, 30, Now));
        sync.RequireRestart(claim, "initial_sync.authority.changed", Now);
        Assert.Null(sync.ClaimId);

        sync.Restart("58201774", Now.AddHours(1), 30, Now.AddHours(1));

        Assert.Equal((2L, InitialSyncState.Buffering, 10L, 30L), (sync.Generation, sync.State, sync.ReplayFloor, sync.BufferStartFence));
        Assert.Equal(Now.AddHours(1), sync.AuthorizedAtUtc);
        Assert.Null(sync.ReplayFence);
        Assert.Null(sync.LastOutcome);

        // A failed generation restarts the same way: the floor stays where the earliest generation put it.
        var restartClaim = Claimed(sync);
        sync.BeginReading(restartClaim, Now);
        sync.Fail(restartClaim, "initial_sync.scope.inactive", Now);
        sync.Restart("58201774", Now.AddHours(1), 50, Now.AddHours(2));
        Assert.Equal((3L, 10L, 50L), (sync.Generation, sync.ReplayFloor, sync.BufferStartFence));
    }

    [Fact]
    public void A_wait_records_its_run_of_like_outcomes_and_releases_the_claim()
    {
        var sync = Begin();
        var claim = Claimed(sync);
        sync.BeginReading(claim, Now);
        Assert.Equal(1, sync.AttemptsWith(SyncScopeOutcome.ConnectionUnavailable));

        sync.Wait(claim, "initial_sync.connection.unavailable", 1, Now.AddMinutes(1), Now);
        Assert.Equal((1, (DateTimeOffset?)Now.AddMinutes(1), (Guid?)null), (sync.Attempts, sync.NextAttemptAtUtc, sync.ClaimId));
        Assert.Equal(2, sync.AttemptsWith(SyncScopeOutcome.TenantInactive));
        Assert.Equal(1, sync.AttemptsWith(SyncScopeOutcome.ProviderUnavailable));
    }

    [Fact]
    public void A_scope_is_held_only_for_what_retrying_cannot_change_never_for_a_transient_failure()
    {
        var transient = Plan();
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            Assert.Equal(attempt, transient.RecordFailure(SyncScopeOutcome.ProviderUnavailable, maxAttempts: 3));
            Assert.Equal(SyncScopeState.Pending, transient.State);
        }

        // An unusable answer is held once its run of attempts reaches the bound; a change of kind starts the run again.
        var unusable = Plan();
        Assert.Equal(1, unusable.RecordFailure(SyncScopeOutcome.ScopeMissing, maxAttempts: 3));
        Assert.Equal(2, unusable.RecordFailure(SyncScopeOutcome.UnsupportedResponse, maxAttempts: 3));
        Assert.Equal(1, unusable.RecordFailure(SyncScopeOutcome.RateLimited, maxAttempts: 3));
        Assert.Equal(1, unusable.RecordFailure(SyncScopeOutcome.ScopeMissing, maxAttempts: 3));
        Assert.Equal(2, unusable.RecordFailure(SyncScopeOutcome.ScopeMissing, maxAttempts: 3));
        Assert.Equal(SyncScopeState.Pending, unusable.State);
        Assert.Equal(3, unusable.RecordFailure(SyncScopeOutcome.ScopeMissing, maxAttempts: 3));
        Assert.Equal((SyncScopeState.Held, "initial_sync.scope.missing"), (unusable.State, unusable.LastOutcome));

        // What retrying cannot change is held at once, and a held scope never changes again.
        var inactive = Plan();
        inactive.RecordFailure(SyncScopeOutcome.ScopeInactive, maxAttempts: 8);
        Assert.Equal(SyncScopeState.Held, inactive.State);
        Assert.Throws<InvalidOperationException>(() => inactive.Complete(1, 1, 1, Now));
        Assert.Throws<InvalidOperationException>(() => inactive.RecordFailure(SyncScopeOutcome.ProviderUnavailable, maxAttempts: 8));

        var completed = Plan();
        completed.Complete(2, 501, 501, Now);
        Assert.Equal((SyncScopeState.Completed, 0, "initial_sync.scope.completed", 2, 501), (completed.State, completed.Attempts, completed.LastOutcome, completed.PagesRead, completed.ObjectsRead));
        Assert.Throws<InvalidOperationException>(() => completed.RecordFailure(SyncScopeOutcome.ProviderUnavailable, maxAttempts: 8));
    }

    [Fact]
    public void Only_whether_a_scope_is_enabled_and_its_baseline_ever_change()
    {
        var scope = ConnectorSourceScopeRecord.Configure(Tenant, Connection, "monday", "9001", enabled: true, Now);
        Assert.Null(scope.BaselineGeneration);

        Assert.False(scope.SetEnabled(true, Now.AddMinutes(1)));
        Assert.Equal(Now, scope.UpdatedAtUtc);
        scope.EstablishBaseline(3, Now.AddMinutes(2));
        Assert.Equal((long?)3, scope.BaselineGeneration);

        // Disabling drops the baseline; enabling again restores none, and only a generation establishes one.
        Assert.True(scope.SetEnabled(false, Now.AddMinutes(3)));
        Assert.Equal((false, (long?)null, Now.AddMinutes(3), "9001", Connection), (scope.Enabled, scope.BaselineGeneration, scope.UpdatedAtUtc, scope.ResourceId, scope.ConnectionId));
        Assert.Throws<InvalidOperationException>(() => scope.EstablishBaseline(4, Now.AddMinutes(4)));
        Assert.True(scope.SetEnabled(true, Now.AddMinutes(5)));
        Assert.Null(scope.BaselineGeneration);
        Assert.Throws<InvalidOperationException>(() => scope.EstablishBaseline(0, Now.AddMinutes(6)));
        scope.EstablishBaseline(5, Now.AddMinutes(7));
        Assert.Equal((long?)5, scope.BaselineGeneration);
    }

    private static ConnectionSyncRecord Begin(long fence = 0, long floor = 0) =>
        ConnectionSyncRecord.Begin(Tenant, Connection, "monday", "58201774", Now.AddDays(-1), floor, fence, Now);

    private static ConnectionSyncScopeRecord Plan() => ConnectionSyncScopeRecord.Plan(Tenant, Connection, 1, "9001");

    private static Guid Claimed(ConnectionSyncRecord sync)
    {
        var claim = Guid.CreateVersion7();
        sync.Claim(claim, Now.AddMinutes(5), Now);
        return claim;
    }
}
