using System.Net;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.InitialSync;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Reconciliation;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the harness composes the existing connector parts and runs the whole monday path against
/// deterministic fakes, on the real local PostgreSQL as the runtime role. No real monday is contacted, and nothing a
/// fake answers is evidence of how monday behaves.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessCompositionTests(DatabaseFixture fixture)
{
    private const string Board = "1771812698";
    private const string Item = "1772099001";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_offline_composition_runs_authorization_intake_processing_sync_and_reconciliation_against_fakes()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var monday = Assert.IsType<Connectors.FakeMonday>(harness.Monday);
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        harness.Account1.AddBoard(Board);
        harness.Account1.AddItem(Item, Board, "Working on it", at);

        // The authorization lifecycle through the existing authorizer: one exchange, one identity read, an active connection.
        var connection = await harness.AuthorizeAsync();
        Assert.Equal(1, monday.TokenCalls);
        Assert.Equal(1, monday.ApiCalls);
        var evidence = (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value;
        var authorization = Assert.Single(evidence.Authorizations);
        Assert.Equal((connection, "Active", 1L), (authorization.ConnectionId, authorization.State, authorization.Generation));
        Assert.Equal((0L, 0L, 1L), (evidence.Transactions.Open, evidence.Transactions.VerifierDue, evidence.Transactions.VerifierDeleted));

        // An explicit source scope, typed by the operator: the connection now takes part in initial sync, not started.
        Assert.Contains($"scope: resource={Board} enabled=True", await harness.ConsoleAsync($"scope {connection:D} {Board} enable"), StringComparison.Ordinal);

        // Webhook intake through the existing endpoint and verifier, then relay, consumer and processing: the delivery is
        // stored and held for the baseline, and it is current work nowhere.
        await harness.DeliverAsync(connection, Item, "Stuck", Board);
        Assert.Equal([TransportSettlementKind.Complete], await harness.RelayAndPumpAsync());
        Assert.Empty((await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields);

        // Initial sync, started and run by the operator, one step each: the baseline comes from the authenticated API
        // read, never from the webhook, whose label was only a signal.
        Assert.Contains("state=Buffering", await harness.ConsoleAsync($"sync-start {connection:D}"), StringComparison.Ordinal);
        Assert.Contains("state=Live", await harness.ConsoleAsync($"sync-run {connection:D}"), StringComparison.Ordinal);
        var baselined = Assert.Single((await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields);
        Assert.Equal((Item, "status", "Working on it", (DateTimeOffset?)at), (baselined.ObjectId, baselined.Field, baselined.Value, baselined.SourceUpdatedAtUtc));

        // Reconciliation, run by the operator: the replayed delivery asked for a reread, which finds the same value.
        Assert.Contains("reconcile: claimed=1", await harness.ConsoleAsync($"reconcile {connection:D}"), StringComparison.Ordinal);
        Assert.Equal("Working on it", Assert.Single((await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields).Value);

        // A change at the source and its webhook: unconfirmed, so current work does not move until the reread confirms it.
        harness.Account1.SetStatus(Item, "Done", at.AddMinutes(5));
        await harness.DeliverAsync(connection, Item, "Done", Board);
        Assert.Equal([TransportSettlementKind.Complete], await harness.RelayAndPumpAsync());
        Assert.Equal("Working on it", Assert.Single((await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields).Value);
        Assert.Contains("reconcile: claimed=1", await harness.ConsoleAsync($"reconcile {connection:D}"), StringComparison.Ordinal);
        var repaired = Assert.Single((await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields);
        Assert.Equal(("Done", (DateTimeOffset?)at.AddMinutes(5)), (repaired.Value, repaired.SourceUpdatedAtUtc));

        // A sweep is a request, bounded and explicit; the next reconciliation rereads what it asked for.
        Assert.Contains("sweep: requested=1", await harness.ConsoleAsync($"sweep {connection:D}"), StringComparison.Ordinal);
        Assert.Contains("reconcile: claimed=1", await harness.ConsoleAsync($"reconcile {connection:D}"), StringComparison.Ordinal);

        // What the harness reads agrees with the product's own records, read independently by the schema owner.
        var view = Assert.Single(await InitialSyncData.ActiveFieldsAsync(fixture, harness.Tenant));
        Assert.Equal((harness.Tenant.Value, Item, "status", "Done"), (view.Tenant, view.ObjectId, view.Field, view.Value));
        var ledger = await ProcessingHarness.LedgerAsync(fixture, harness.Tenant);
        Assert.Contains(ledger, entry => entry.Outcome == "ReconciliationRequired");
        Assert.Contains(ledger, entry => entry.Outcome == "AcceptedForProjection");

        // The operator's evidence: states, counts and fixed codes.
        var shown = await harness.ConsoleAsync("evidence");
        Assert.Contains($"sync {connection:D} generation=1 state=Live", shown, StringComparison.Ordinal);
        Assert.Contains($"source-scope {connection:D} resource={Board} enabled=True baseline-generation=1", shown, StringComparison.Ordinal);
        Assert.Contains($"authorization {connection:D} state=Active generation=1", shown, StringComparison.Ordinal);
        Assert.Contains("authorization-transactions open=0 verifier-due=0 verifier-deleted=1", shown, StringComparison.Ordinal);
        Assert.Contains("cleanup: deleted=0 remaining=0", await harness.ConsoleAsync("cleanup"), StringComparison.Ordinal);

        // Current work at the console: the object, the field and the length of the value, never the value.
        var work = await harness.ConsoleAsync("work");
        Assert.Contains("work: objects=1 fields=1", work, StringComparison.Ordinal);
        Assert.Contains($"{Item} status type=TaskStatus value-length=4", work, StringComparison.Ordinal);
        Assert.DoesNotContain("Done", work, StringComparison.Ordinal);

        // Diagnostics: closed values and counts.
        var diagnostics = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(1, diagnostics.ProviderCalls[(ProviderEndpoint.Token, ProviderCallOutcome.Success)]);
        Assert.True(diagnostics.ProviderCalls[(ProviderEndpoint.Api, ProviderCallOutcome.Success)] >= 4);
        Assert.Equal(1, diagnostics.AuthorizationStarts[AuthorizationStartOutcome.Started]);
        Assert.Equal(1, diagnostics.AuthorizationCompletions[AuthorizationCompletionOutcome.Completed]);
        Assert.Equal(
            2,
            diagnostics.WebhookRequests[new WebhookRequestShape(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.HS256, WebhookTokenType.Jwt, WebhookResponseClass.Accepted)]);

        // Disconnect: unusable locally first, then each held token revoked at the fake provider; intake then answers 404.
        Assert.Contains(
            "disconnect: already-disconnected=False provider-revocation-confirmed=True",
            await harness.ConsoleAsync($"disconnect {connection:D}"),
            StringComparison.Ordinal);
        Assert.Equal(2, monday.RevokeCalls);
        using var afterDisconnect = await harness.PostWebhookAsync(
            connection,
            HarnessTestHost.WebhookBody(Item, "Done", Connectors.WebhookData.NewTrigger(), Board),
            Connectors.WebhookTestTokens.Mint(connection, HarnessTestHost.Account, harness.Clock.UtcNow));
        Assert.Equal(HttpStatusCode.NotFound, afterDisconnect.StatusCode);
    }

    [Fact]
    public async Task Current_work_is_what_the_active_views_show_and_a_disabled_scopes_state_is_not_current_work()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        harness.Account1.AddBoard(Board);
        harness.Account1.AddItem(Item, Board, "Working on it", at);
        var connection = await harness.AuthorizeAsync();
        await harness.ConsoleAsync($"scope {connection:D} {Board} enable");
        await harness.ConsoleAsync($"sync-start {connection:D}");
        Assert.Contains("state=Live", await harness.ConsoleAsync($"sync-run {connection:D}"), StringComparison.Ordinal);

        // Enabled and baselined: the board's state is current work.
        var work = (await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value;
        Assert.Equal((1, 1), (work.Objects.Count, work.Fields.Count));
        Assert.Contains("work: objects=1 fields=1", await harness.ConsoleAsync("work"), StringComparison.Ordinal);

        // The operator disables the scope. What the Snapshot holds for it stays where it is and is no longer current work.
        Assert.Contains($"scope: resource={Board} enabled=False", await harness.ConsoleAsync($"scope {connection:D} {Board} disable"), StringComparison.Ordinal);
        work = (await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value;
        Assert.Empty(work.Fields);
        Assert.Empty(work.Objects);
        Assert.Contains("work: objects=0 fields=0", await harness.ConsoleAsync("work"), StringComparison.Ordinal);

        // Positive control: the raw Snapshot still holds the object and its field, as the schema owner reads them. Only the
        // active views leave them out, so a reader of the tables beneath the views would have shown them.
        var held = Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, harness.Tenant));
        Assert.Equal((Item, "status", "Working on it"), (held.ObjectId, held.Field, held.Value));
        Assert.Equal(Item, Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, harness.Tenant)).ObjectId);
        Assert.Empty(await InitialSyncData.ActiveFieldsAsync(fixture, harness.Tenant));
    }

    [Fact]
    public async Task Sync_reconciliation_sweep_and_scope_configuration_happen_only_when_the_operator_asks_and_one_step_at_a_time()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        harness.Account1.AddBoard(Board);
        harness.Account1.AddItem(Item, Board, "Working on it", at);
        var connection = await harness.AuthorizeAsync();
        Assert.Contains("enabled=True", await harness.ConsoleAsync($"scope {connection:D} {Board} enable"), StringComparison.Ordinal);

        // Nothing schedules a sync: several cleanup ticks later the connection is still not started and nothing was read.
        await harness.TickCleanupAsync();
        await harness.TickCleanupAsync();
        Assert.Equal(3, harness.Host.Diagnostics.Snapshot().VerifierCleanup.Runs);
        Assert.Empty(harness.Account1.Answered);
        var sync = await InitialSyncData.SyncAsync(fixture, harness.Tenant, connection);
        Assert.Equal("NotStarted", sync!.State);

        // Starting reads nothing; one run does the bounded work of the one configured board.
        Assert.Contains("state=Buffering", await harness.ConsoleAsync($"sync-start {connection:D}"), StringComparison.Ordinal);
        Assert.Empty(harness.Account1.Answered);
        Assert.Contains("state=Live", await harness.ConsoleAsync($"sync-run {connection:D}"), StringComparison.Ordinal);
        Assert.Equal(["board", "page"], harness.Account1.Answered);
        Assert.Equal([Board, Board], harness.Account1.BoardsAsked);

        // No reconciliation runs by itself either: ticks pass, and the fake account is asked nothing more.
        await harness.TickCleanupAsync();
        Assert.Equal(2, harness.Account1.Answered.Count);

        // A sweep and a run are each bounded to the documented maximum.
        Assert.Equal(100, WorkContinuity.Harness.Monday.Operator.OperatorActions.MaxTargetsPerAction);
        Assert.Contains("sweep: requested=1", await harness.ConsoleAsync($"sweep {connection:D}"), StringComparison.Ordinal);
        Assert.Equal(2, harness.Account1.Answered.Count);
        Assert.Contains("reconcile: claimed=1", await harness.ConsoleAsync($"reconcile {connection:D}"), StringComparison.Ordinal);
        Assert.Equal(["board", "page", "reread"], harness.Account1.Answered);
    }

    [Fact]
    public async Task The_console_takes_only_its_closed_commands_and_never_echoes_what_was_typed()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);

        foreach (var line in new[]
                 {
                     "authorise",
                     "scope not-a-connection 123 enable",
                     $"scope {Guid.CreateVersion7():D} not-a-board enable",
                     $"scope {Guid.CreateVersion7():D} 123 maybe",
                     $"sync-start {Guid.Empty:D}",
                     $"reconcile {Guid.CreateVersion7():N}",
                     "disconnect",
                     "work extra",
                     "SENTINEL-mistyped-secret",
                 })
        {
            Assert.Equal("Not a command. Type help." + Environment.NewLine, await harness.ConsoleAsync(line));
        }

        // A well-formed connection id that is no connection of the configured tenant: refused by the product, by a fixed code.
        var unknown = Guid.CreateVersion7();
        Assert.Contains("scope: failed ", await harness.ConsoleAsync($"scope {unknown:D} 123 enable"), StringComparison.Ordinal);
        Assert.Contains("sync-start: failed ", await harness.ConsoleAsync($"sync-start {unknown:D}"), StringComparison.Ordinal);
        Assert.Contains("disconnect: failed connector.authorization.connection_not_found", await harness.ConsoleAsync($"disconnect {unknown:D}"), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", harness.Transcript, StringComparison.Ordinal);

        var help = await harness.ConsoleAsync("help");
        Assert.Equal(WorkContinuity.Harness.Monday.Operator.OperatorConsole.Commands, help.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
