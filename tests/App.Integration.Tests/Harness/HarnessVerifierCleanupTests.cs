using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the harness hosts the PKCE verifier cleanup as the owner's hosting decision of 2026-10-03
/// requires: once at start-up, then every five minutes while it is active, only under the trusted tenant context. The
/// cadence is tested with a timer the test fires; the period the harness asks for stays five minutes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessVerifierCleanupTests(DatabaseFixture fixture)
{
    private const string VerifierPrefix = "wce-monday-pkce-";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_start_up_run_deletes_a_verifier_a_completion_left_behind_before_the_harness_reports_started()
    {
        var secrets = new MemorySecrets();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        TenantId tenant;
        Core.Identity.UserId user;
        string verifier;

        // A first run of the harness starts an authorization; its completion consumes the transaction and then the
        // process stops before the verifier is deleted.
        await using (var first = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets, Clock = clock }))
        {
            (tenant, user) = (first.Tenant, first.User);
            var started = await first.StartAuthorizationAsync();
            verifier = Assert.Single(secrets.Names, name => name.StartsWith(VerifierPrefix, StringComparison.Ordinal));
            await ConsumeAsync(tenant, user, started.State);
            await AgeTransactionsAsync(tenant, TimeSpan.FromMinutes(10));
        }

        Assert.Contains(verifier, secrets.Names);

        // A new process, sharing only the database and the secret storage. By the time it has started, with no tick, no
        // callback and no operator action, the orphan is gone and its deletion recorded.
        await using var restarted = await HarnessTestHost.StartAsync(
            fixture, new HarnessTestSetup { Tenant = tenant, User = user, Secrets = secrets, Clock = clock });

        Assert.DoesNotContain(verifier, secrets.Names);
        var cleanup = restarted.Host.Diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((true, 1L, CleanupOutcome.Succeeded, 1, 0, 1L), (cleanup.StartupRunCompleted, cleanup.Runs, cleanup.LastOutcome, cleanup.LastDeleted, cleanup.LastRemaining, cleanup.TotalDeleted));
        var transactions = (await restarted.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions;
        Assert.Equal((0L, 0L, 1L), (transactions.Open, transactions.VerifierDue, transactions.VerifierDeleted));
    }

    [Fact]
    public async Task The_cleanup_then_runs_on_a_five_minute_timer_and_only_when_it_ticks()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), VerifierCleanupLoop.Interval);

        var secrets = new MemorySecrets();
        await using var harness = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets });
        await HarnessTestHost.WaitForAsync(() => harness.Time.Running == 1);

        // Exactly one timer, asked for exactly five minutes, first due after five minutes.
        Assert.Equal([(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))], harness.Time.Requested);
        Assert.Equal(1L, harness.Host.Diagnostics.Snapshot().VerifierCleanup.Runs);

        // A verifier becomes due while the harness is active.
        var started = await harness.StartAuthorizationAsync();
        var verifier = Assert.Single(secrets.Names, name => name.StartsWith(VerifierPrefix, StringComparison.Ordinal));
        await ConsumeAsync(harness.Tenant, harness.User, started.State);
        await AgeTransactionsAsync(harness.Tenant, TimeSpan.FromMinutes(10));

        // Without a tick nothing runs, however long the test waits in real time.
        await Task.Delay(TimeSpan.FromMilliseconds(300), Token);
        Assert.Equal(1L, harness.Host.Diagnostics.Snapshot().VerifierCleanup.Runs);
        Assert.Contains(verifier, secrets.Names);

        // One tick, one cleanup, under the trusted tenant: the verifier is deleted and recorded.
        await harness.TickCleanupAsync();
        var cleanup = harness.Host.Diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((2L, CleanupOutcome.Succeeded, 1, 0), (cleanup.Runs, cleanup.LastOutcome, cleanup.LastDeleted, cleanup.LastRemaining));
        Assert.DoesNotContain(verifier, secrets.Names);

        // Each tick runs again, and a run with nothing due deletes nothing.
        await harness.TickCleanupAsync();
        cleanup = harness.Host.Diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((3L, CleanupOutcome.Succeeded, 0, 0, 1L), (cleanup.Runs, cleanup.LastOutcome, cleanup.LastDeleted, cleanup.LastRemaining, cleanup.TotalDeleted));
        Assert.Equal(1, harness.Time.Running);
    }

    [Fact]
    public async Task A_deletion_the_secret_store_refuses_stays_due_and_the_next_tick_retries_it()
    {
        var secrets = new MemorySecrets();
        await using var harness = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets });
        var started = await harness.StartAuthorizationAsync();
        var verifier = Assert.Single(secrets.Names, name => name.StartsWith(VerifierPrefix, StringComparison.Ordinal));
        await ConsumeAsync(harness.Tenant, harness.User, started.State);
        await AgeTransactionsAsync(harness.Tenant, TimeSpan.FromMinutes(10));

        // The store refuses the deletion, as an outage would: the cleanup reports it as still due and records nothing.
        secrets.DeleteOverride = name => name.Value.StartsWith(VerifierPrefix, StringComparison.Ordinal) ? MondaySecretStoreStatus.Unavailable : null;
        await harness.TickCleanupAsync();
        var refused = harness.Host.Diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((CleanupOutcome.Succeeded, 0, 1), (refused.LastOutcome, refused.LastDeleted, refused.LastRemaining));
        Assert.Contains(verifier, secrets.Names);
        Assert.Equal(1L, (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.VerifierDue);

        // The store recovers: the next tick, five minutes later, deletes and records it.
        secrets.DeleteOverride = _ => null;
        await harness.TickCleanupAsync();
        var retried = harness.Host.Diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((CleanupOutcome.Succeeded, 1, 0), (retried.LastOutcome, retried.LastDeleted, retried.LastRemaining));
        Assert.DoesNotContain(verifier, secrets.Names);
        Assert.Equal(0L, (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.VerifierDue);
    }

    [Fact]
    public async Task A_cleanup_that_fails_or_throws_is_counted_logged_by_its_type_only_and_the_next_tick_still_runs()
    {
        const string Sentinel = "SENTINEL-inside-a-cleanup-failure";
        var time = new ManualTimeProvider();
        var sink = new CapturingHarnessSink();
        var diagnostics = new HarnessDiagnostics();
        using var loggers = new HarnessLoggerFactory(sink, diagnostics, TenantId.New(), new TenantContextAccessor());

        // The loop itself, given cleanups that end in every way one can: a success, a failure, a throw, a success.
        var answers = new Queue<Func<Result<MondayVerifierCleanup>>>(
        [
            () => Result<MondayVerifierCleanup>.Success(new MondayVerifierCleanup(0, 0)),
            () => Result<MondayVerifierCleanup>.Failure(ConnectorAuthorizationErrors.StorageFailed),
            () => throw new InvalidOperationException(Sentinel),
            () => Result<MondayVerifierCleanup>.Success(new MondayVerifierCleanup(2, 1)),
        ]);
        var loop = new VerifierCleanupLoop(_ => Task.FromResult(answers.Dequeue()()), time, diagnostics, loggers);

        await loop.RunStartupAsync(Token);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = loop.RunPeriodicallyAsync(stopping.Token);
        await HarnessTestHost.WaitForAsync(() => time.Running == 1);
        Assert.Equal([(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))], time.Requested);

        // A cleanup that reports a failure, then one that throws: each is counted, and the timer keeps running.
        await TickAsync(time, diagnostics);
        Assert.Equal((2L, CleanupOutcome.Failed), (diagnostics.Snapshot().VerifierCleanup.Runs, diagnostics.Snapshot().VerifierCleanup.LastOutcome));
        await TickAsync(time, diagnostics);
        Assert.Equal((3L, CleanupOutcome.Faulted), (diagnostics.Snapshot().VerifierCleanup.Runs, diagnostics.Snapshot().VerifierCleanup.LastOutcome));
        Assert.Equal(1, time.Running);

        // The next tick, five minutes later, runs as scheduled.
        await TickAsync(time, diagnostics);
        var recovered = diagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((4L, CleanupOutcome.Succeeded, 2, 1, 2L), (recovered.Runs, recovered.LastOutcome, recovered.LastDeleted, recovered.LastRemaining, recovered.TotalDeleted));

        // The log names a failure's fixed code and an exception's type, and nothing of the exception's text.
        Assert.Contains(sink.Records, record => record.Message == $"PKCE verifier cleanup failed (start-up: False): {ConnectorAuthorizationErrors.StorageFailed.Code}.");
        Assert.Contains(sink.Records, record => record.Message == "PKCE verifier cleanup threw InvalidOperationException (start-up: False).");
        Assert.DoesNotContain(Sentinel, sink.Text, StringComparison.Ordinal);
        Assert.All(sink.Records, record => Assert.Null(record.ExceptionType));

        await stopping.CancelAsync();
        await running;
        Assert.Equal(0, time.Running);

        // A start-up run that throws does not stop the harness from starting either: it is counted and logged the same way.
        var startupDiagnostics = new HarnessDiagnostics();
        var startup = new VerifierCleanupLoop(_ => throw new InvalidOperationException(Sentinel), new ManualTimeProvider(), startupDiagnostics, loggers);
        Assert.Null(await Record.ExceptionAsync(() => startup.RunStartupAsync(Token)));
        var faulted = startupDiagnostics.Snapshot().VerifierCleanup;
        Assert.Equal((true, 1L, CleanupOutcome.Faulted), (faulted.StartupRunCompleted, faulted.Runs, faulted.LastOutcome));
        Assert.DoesNotContain(Sentinel, sink.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cleanup_acts_for_the_configured_tenant_only_and_leaves_another_tenants_verifier_alone()
    {
        // Two harnesses, two tenants, one secret store: each has a verifier that is due.
        var secrets = new MemorySecrets();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var other = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets, Clock = clock });
        var otherStarted = await other.StartAuthorizationAsync();
        var otherVerifier = Assert.Single(secrets.Names, name => name.StartsWith(VerifierPrefix, StringComparison.Ordinal));
        await ConsumeAsync(other.Tenant, other.User, otherStarted.State);
        await AgeTransactionsAsync(other.Tenant, TimeSpan.FromMinutes(10));

        await using var harness = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets, Clock = clock });
        var started = await harness.StartAuthorizationAsync();
        var ownVerifier = Assert.Single(secrets.Names, name => name.StartsWith(VerifierPrefix, StringComparison.Ordinal) && name != otherVerifier);
        await ConsumeAsync(harness.Tenant, harness.User, started.State);
        await AgeTransactionsAsync(harness.Tenant, TimeSpan.FromMinutes(10));

        // The harness's start-up run and ticks never touched the other tenant's verifier, and a tick deletes only its own.
        await harness.TickCleanupAsync();
        Assert.DoesNotContain(ownVerifier, secrets.Names);
        Assert.Contains(otherVerifier, secrets.Names);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().VerifierCleanup.LastDeleted);
        Assert.Equal(1L, (await other.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.VerifierDue);

        // Positive control: the other tenant's own harness, under its own tenant, deletes its own.
        await other.TickCleanupAsync();
        Assert.DoesNotContain(otherVerifier, secrets.Names);
    }

    [Fact]
    public async Task Stopping_the_harness_stops_the_timer_and_no_cleanup_runs_afterwards()
    {
        var harness = await HarnessTestHost.StartAsync(fixture);
        await HarnessTestHost.WaitForAsync(() => harness.Time.Running == 1);
        var diagnostics = harness.Host.Diagnostics;
        var time = harness.Time;

        await harness.DisposeAsync();

        Assert.Equal(0, time.Running);
        time.Tick();
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.Equal(1L, diagnostics.Snapshot().VerifierCleanup.Runs);
    }

    // Fires the timer once, as five minutes passing would, and waits for the run it starts to be counted.
    private static async Task TickAsync(ManualTimeProvider time, HarnessDiagnostics diagnostics)
    {
        var before = diagnostics.Snapshot().VerifierCleanup.Runs;
        time.Tick();
        await HarnessTestHost.WaitForAsync(() => diagnostics.Snapshot().VerifierCleanup.Runs > before);
    }

    // The completion's first step, as the authorizer takes it: the transaction is consumed, and then nothing more happens.
    private async Task ConsumeAsync(TenantId tenant, Core.Identity.UserId user, string state)
    {
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var consumed = await scoped.Store.ConsumeTransactionAsync(
            Guid.ParseExact(state[..32], "N"), user, ConnectorAuthorizationData.Hash(state[33..]), Token);
        Assert.Equal(ConnectorTransactionOutcome.Consumed, consumed.Value.Outcome);
    }

    // Ages the tenant's transactions as the passing of time would: creation, expiry and consumption all move back.
    private async Task AgeTransactionsAsync(TenantId tenant, TimeSpan age)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            UPDATE connector_authorization_transactions
            SET created_at_utc = created_at_utc - @age, expires_at_utc = expires_at_utc - @age, consumed_at_utc = consumed_at_utc - @age
            WHERE tenant_id = @tenant;
            """,
            admin);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }
}
