using System.Net;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Reconciliation;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the harness acts for its configured tenant and canonical user only. Every operator action
/// and cleanup has an execution flow and a scope of its own, no flow sees another's tenant, the relay and the consumer
/// keep resolving their own, another tenant's connection is refused before any provider call, nothing a request
/// carries can name a tenant or a user, and nothing the harness shows identifies another tenant, whose work the
/// product's own relay and intake still do.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessTenantIsolationTests(DatabaseFixture fixture)
{
    private const string Board = "1771812698";
    private const string Item = "1772099001";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_operator_action_leaves_no_tenant_on_the_callers_flow_and_takes_none_from_it()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var accessor = new TenantContextAccessor();

        // The caller's flow carries no tenant before or after.
        Assert.False(accessor.Current.IsResolved);
        Assert.True((await harness.Host.Operator.ReadEvidenceAsync(Token)).IsSuccess);
        Assert.True((await harness.Host.Operator.CleanUpVerifiersAsync(Token)).IsSuccess);
        Assert.False(accessor.Current.IsResolved);

        // A caller that carries another tenant: the action still runs as the configured tenant, neither inherits the
        // caller's tenant nor fails on it, and the caller's flow is exactly as it was afterwards.
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var own = await harness.AuthorizeAsync();
        await Task.Run(
            async () =>
            {
                accessor.SetResolved(TenantContext.Resolved(foreign.Tenant, TenantResolutionSource.AuthenticatedPrincipal));

                var evidence = await harness.Host.Operator.ReadEvidenceAsync(Token);
                Assert.Equal([own], evidence.Value.Authorizations.Select(authorization => authorization.ConnectionId));

                Assert.True(accessor.Current.TryGetTenantId(out var still));
                Assert.Equal(foreign.Tenant, still);

                // The relay and the consumer refuse to run on a flow that carries a tenant; the harness starts each on
                // a flow of its own, so neither sees this one.
                var pass = await harness.Host.RelayOnceAsync(Token);
                Assert.False(pass.DiscoveryFailed);
                var foreignMessage = HarnessTestHost.Received(Envelope(foreign.Tenant).ToWire(), 1);
                Assert.Null(await harness.Host.ConsumeOwnSessionAsync(foreignMessage, Token));
                Assert.True(accessor.Current.TryGetTenantId(out still));
                Assert.Equal(foreign.Tenant, still);
            },
            Token);
        Assert.False(accessor.Current.IsResolved);
    }

    [Fact]
    public async Task Concurrent_actions_of_two_harnesses_each_see_only_their_own_tenant()
    {
        await using var first = await HarnessTestHost.StartAsync(fixture);
        await using var second = await HarnessTestHost.StartAsync(fixture);
        var firstConnection = await first.AuthorizeAsync();
        var secondConnection = await second.AuthorizeAsync();

        // Interleaved on the same thread pool, sharing the process-wide flow-scoped accessor: each run sees its own tenant.
        var runs = Enumerable.Range(0, 24).Select(async index =>
        {
            var harness = index % 2 == 0 ? first : second;
            var evidence = await harness.Host.Operator.ReadEvidenceAsync(Token);
            return (index, Connections: evidence.Value.Authorizations.Select(authorization => authorization.ConnectionId).ToArray());
        });

        foreach (var (index, connections) in await Task.WhenAll(runs))
        {
            Assert.Equal([index % 2 == 0 ? firstConnection : secondConnection], connections);
        }
    }

    [Fact]
    public async Task Another_tenants_connection_is_refused_by_every_operator_action_with_no_provider_call_and_no_change()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var monday = Assert.IsType<FakeMonday>(harness.Monday);
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var before = await AuthorizationRowAsync(foreign.Connection);

        Assert.Contains("scope: failed ", await harness.ConsoleAsync($"scope {foreign.Connection:D} {Board} enable"), StringComparison.Ordinal);
        Assert.Contains("sync-start: failed ", await harness.ConsoleAsync($"sync-start {foreign.Connection:D}"), StringComparison.Ordinal);
        Assert.DoesNotContain("state=Live", await harness.ConsoleAsync($"sync-run {foreign.Connection:D}"), StringComparison.Ordinal);
        Assert.Contains("reconcile: claimed=0", await harness.ConsoleAsync($"reconcile {foreign.Connection:D}"), StringComparison.Ordinal);
        Assert.Contains("sweep: requested=0", await harness.ConsoleAsync($"sweep {foreign.Connection:D}"), StringComparison.Ordinal);
        Assert.Contains(
            "disconnect: failed connector.authorization.connection_not_found",
            await harness.ConsoleAsync($"disconnect {foreign.Connection:D}"),
            StringComparison.Ordinal);

        // Restoring it is refused too: the capability is spent on a start that opens nothing.
        var answer = await harness.ConsoleAsync($"reauthorize {foreign.Connection:D}");
        var start = answer.Split('\n', StringSplitOptions.TrimEntries).Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        using (var started = await harness.GetOperatorAsync(new Uri(start).PathAndQuery))
        {
            Assert.Equal("/operator/done", started.Headers.Location!.OriginalString);
        }

        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationStarts[AuthorizationStartOutcome.NotReauthorizable]);

        // Nothing was sent to the provider, and the other tenant's rows are exactly as they were.
        Assert.Empty(monday.Requests);
        Assert.Empty(harness.Host.Diagnostics.Snapshot().ProviderCalls);
        Assert.Equal(before, await AuthorizationRowAsync(foreign.Connection));
        Assert.Equal(0L, await CountAsync("connector_source_scopes", foreign.Tenant));
        Assert.Equal(0L, await CountAsync("connection_syncs", foreign.Tenant));
    }

    [Fact]
    public async Task Evidence_and_current_work_show_the_configured_tenant_only()
    {
        // The other tenant has an authorization, a baselined board and current work of its own.
        await using var other = await HarnessTestHost.StartAsync(fixture);
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        other.Account1.AddBoard(Board);
        other.Account1.AddItem(Item, Board, "Done", at);
        var otherConnection = await other.AuthorizeAsync();
        await other.ConsoleAsync($"scope {otherConnection:D} {Board} enable");
        await other.ConsoleAsync($"sync-start {otherConnection:D}");
        Assert.Contains("state=Live", await other.ConsoleAsync($"sync-run {otherConnection:D}"), StringComparison.Ordinal);
        Assert.Single((await other.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields);

        // The harness under test sees none of it: not in current work, not in any evidence list or count.
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var work = (await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value;
        Assert.Empty(work.Fields);
        Assert.Empty(work.Objects);
        var evidence = (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value;
        Assert.Empty(evidence.Authorizations);
        Assert.Empty(evidence.Syncs);
        Assert.Empty(evidence.SyncScopes);
        Assert.Empty(evidence.SourceScopes);
        Assert.Empty(evidence.Receipts);
        Assert.Empty(evidence.Ledger);
        Assert.Empty(evidence.Targets);
        Assert.Null(evidence.HighestIngressSequence);
        Assert.Equal((0L, 0L, 0L), (evidence.Transactions.Open, evidence.Transactions.VerifierDue, evidence.Transactions.VerifierDeleted));
        Assert.Contains("work: objects=0 fields=0", await harness.ConsoleAsync("work"), StringComparison.Ordinal);

        // Positive control: the rows exist, and the schema owner sees them.
        Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, other.Tenant));
        Assert.Equal(1L, await CountAsync("connector_authorizations", other.Tenant));
    }

    [Fact]
    public async Task Nothing_a_request_carries_names_the_tenant_or_the_user_of_an_authorization()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var started = await harness.StartAuthorizationAsync();
        harness.ScriptGrant();

        // The callback asks, in every way a request can, to be another tenant and another user.
        var extra = $"&tenant={foreign.Tenant}&tenant_id={foreign.Tenant}&user={foreign.User}&user_id={foreign.User}&connection={foreign.Connection:D}";
        using var callback = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding, extra, request =>
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", foreign.Tenant.ToString());
            request.Headers.TryAddWithoutValidation("X-User-Id", foreign.User.ToString());
            request.Headers.TryAddWithoutValidation("Cookie", "tenant=" + foreign.Tenant);
        });
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);

        // The authorization belongs to the configured tenant and canonical user, and the other tenant gained nothing.
        var connection = Assert.Single(await harness.ConnectionsAsync());
        Assert.Equal((harness.Tenant.Value, harness.User.Value), await OwnerOfAsync(connection));
        Assert.Equal(1L, await CountAsync("connector_authorizations", foreign.Tenant));
        Assert.Equal(5, harness.Host.Diagnostics.Snapshot().CallbackParameters[CallbackParameter.Other]);
    }

    [Fact]
    public async Task The_harness_consumes_only_its_own_tenants_session()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);

        // A message of another tenant's session is left untouched: not consumed, not settled, nothing recorded.
        var message = HarnessTestHost.Received(Envelope(foreign.Tenant).ToWire(), 1);
        Assert.Null(await harness.Host.ConsumeOwnSessionAsync(message, Token));
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, foreign.Tenant));

        // Its own session is consumed: an envelope naming a receipt that does not exist is settled by the real consumer.
        var own = HarnessTestHost.Received(Envelope(harness.Tenant).ToWire(), 2);
        var settlement = await harness.Host.ConsumeOwnSessionAsync(own, Token);
        Assert.Equal(TransportSettlementKind.DeadLetter, Assert.IsType<TransportSettlement>(settlement).Kind);
    }

    [Fact]
    public async Task Another_tenants_work_passing_through_the_harness_leaves_none_of_its_identifiers_in_the_log_the_console_or_the_diagnostics()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var own = await harness.AuthorizeAsync();
        var foreign = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var foreignSession = TransportEnvelope.SessionIdFor(foreign.Tenant);

        // The configured tenant's own delivery first: the positive control for everything the log is searched for below.
        await harness.DeliverAsync(own, Item, "Done", Board);
        var before = harness.Host.Diagnostics.Snapshot().WithheldLogRecords;

        // A delivery for another tenant's connection arrives at the harness's webhook listener. The listener is the
        // product's own endpoint, unchanged: it takes the tenant from the connection and stores the delivery under it.
        var token = WebhookTestTokens.Mint(foreign.Connection, foreign.Account, harness.Clock.UtcNow);
        using (var accepted = await harness.PostWebhookAsync(
                   foreign.Connection, HarnessTestHost.WebhookBody(Item, "Done", WebhookData.NewTrigger(), Board), token))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var stored = Assert.Single(await ReconciliationData.ReceiptsAsync(fixture, foreign.Tenant));
        Assert.True(stored.HasOutboxMessage);
        var (outboxMessage, correlation) = await PendingOutboxRowAsync(foreign.Tenant);

        // The product wrote its records of that delivery, and the harness withheld them: they were not simply absent.
        var afterDelivery = harness.Host.Diagnostics.Snapshot().WithheldLogRecords;
        Assert.True(afterDelivery.GetValueOrDefault(LogRecordOwner.OtherTenant) > before.GetValueOrDefault(LogRecordOwner.OtherTenant));
        Assert.True(afterDelivery.GetValueOrDefault(LogRecordOwner.Unattributed) > before.GetValueOrDefault(LogRecordOwner.Unattributed));

        // The relay is the product's own too, and publishes every tenant's pending rows: the other tenant's among them.
        for (var pass = 0; pass < 100 && !harness.Transport.Accepted.Any(wire => wire.SessionId == foreignSession); pass++)
        {
            await harness.Host.RelayOnceAsync(Token);
        }

        var relayed = Assert.Single(harness.Transport.Accepted, wire => wire.SessionId == foreignSession);
        var afterRelay = harness.Host.Diagnostics.Snapshot().WithheldLogRecords;
        Assert.True(afterRelay.GetValueOrDefault(LogRecordOwner.OtherTenant) > afterDelivery.GetValueOrDefault(LogRecordOwner.OtherTenant));

        // The consumer is the configured tenant's only: its own message is processed, the other tenant's is not taken.
        Assert.Equal([TransportSettlementKind.Complete], await harness.RelayAndPumpAsync());
        Assert.Null(await harness.Host.ConsumeOwnSessionAsync(HarnessTestHost.Received(relayed, 99), Token));
        Assert.Empty(await ProcessingHarness.LedgerAsync(fixture, foreign.Tenant));
        Assert.Single(await ProcessingHarness.LedgerAsync(fixture, harness.Tenant));

        foreach (var command in new[] { "relay", "evidence", "work", "diagnostics" })
        {
            await harness.ConsoleAsync(command);
        }

        // Nothing the harness shows identifies the other tenant or anything of its work.
        var identifiers = new (string Name, string Value)[]
        {
            ("tenant", foreign.Tenant.ToString()),
            ("tenant, compact", foreign.Tenant.Value.ToString("N")),
            ("user", foreign.User.ToString()),
            ("connection", foreign.Connection.ToString("D")),
            ("receipt", stored.ReceiptId.ToString("D")),
            ("outbox message", outboxMessage.ToString("D")),
            ("correlation", correlation),
            ("transport message", relayed.MessageId),
        };
        Assert.All(identifiers, identifier => Assert.True(identifier.Value.Length >= 16, identifier.Name));
        foreach (var (where, text) in new[]
                 {
                     ("the harness log", harness.Logs.Text),
                     ("the operator console", harness.Transcript),
                     ("the diagnostics", string.Join('\n', harness.Host.Diagnostics.Render())),
                 })
        {
            var found = identifiers.Where(identifier => text.Contains(identifier.Value, StringComparison.OrdinalIgnoreCase)).Select(identifier => identifier.Name).ToList();
            Assert.True(found.Count == 0, $"{where} holds the other tenant's: {string.Join(", ", found)}");
        }

        // Positive control: the same pipeline shows the configured tenant's own records of the same two product parts.
        Assert.Contains(harness.Logs.Records, record =>
            record.Category == "WorkContinuity.Infrastructure.Ingress.EventIngressService"
            && record.Message.Contains(harness.Tenant.ToString(), StringComparison.Ordinal)
            && record.Message.Contains(own.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Records, record =>
            record.Category == "WorkContinuity.Infrastructure.Transport.OutboxRelay"
            && record.Message.Contains(harness.Tenant.ToString(), StringComparison.Ordinal));

        // And the other tenant's delivery is its own: invisible to the harness's evidence, which counts one receipt.
        var evidence = (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value;
        Assert.Equal(1L, evidence.Receipts.Sum(count => count.Count));
        Assert.Equal([own], evidence.Authorizations.Select(authorization => authorization.ConnectionId));
    }

    [Fact]
    public async Task The_log_shows_a_product_record_only_when_it_is_the_configured_tenants()
    {
        const string Sentinel = "SENTINEL-other-tenants-record";
        var configured = TenantId.New();
        var other = TenantId.New();
        var sink = new CapturingHarnessSink();
        var diagnostics = new HarnessDiagnostics();
        var accessor = new TenantContextAccessor();
        using var factory = new HarnessLoggerFactory(sink, diagnostics, configured, accessor);
        var product = factory.CreateLogger("WorkContinuity.Infrastructure.Transport.OutboxRelay");
        var own = factory.CreateLogger("WorkContinuity.Harness.Monday.Host");

        // On a flow that carries no tenant, as the webhook endpoint and the relay's discovery run.
        Assert.False(accessor.Current.IsResolved);
        product.LogInformation("shown-1 for tenant {TenantId}.", configured);
        product.LogInformation("shown-2 for tenant {TenantId}.", configured.Value);
        product.LogInformation("shown-3 for tenant {Tenant}.", configured.ToString().ToUpperInvariant());
        own.LogInformation("shown-4 by the harness, {Count} counted.", 3);
        product.LogInformation("{Marker} for tenant {TenantId}.", Sentinel, other);
        product.LogInformation("{Marker} for tenant {TenantId}.", Sentinel, other.Value);
        product.LogInformation("{Marker} for tenant {Tenant}.", Sentinel, other.ToString());
        product.LogInformation("{Marker} for a tenant in a form that cannot be compared, {TenantId}.", Sentinel, 42);
        product.LogInformation("{Marker} naming {Owner} beside the configured {TenantId}.", Sentinel, other, configured);
        own.LogInformation("{Marker} by the harness, naming {TenantId}.", Sentinel, other);
        product.LogInformation("{Marker} with no tenant, receipt {ReceiptId}.", Sentinel, Guid.CreateVersion7());
        product.LogInformation(Sentinel + " with no value at all.");

        // On a flow that runs as the configured tenant, as an operator action does.
        await Task.Run(
            () =>
            {
                accessor.SetResolved(TenantContext.Resolved(configured, TenantResolutionSource.TrustedServiceIdentity));
                product.LogInformation("shown-5 with no tenant named, connection {ConnectionId}.", Guid.CreateVersion7());
                product.LogInformation("{Marker} naming {TenantId} on the configured tenant's flow.", Sentinel, other);
            },
            Token);

        // On a flow that runs as another tenant, as the relay does for each tenant with pending work.
        await Task.Run(
            () =>
            {
                accessor.SetResolved(TenantContext.Resolved(other, TenantResolutionSource.TrustedServiceIdentity));
                product.LogInformation("{Marker} with no tenant named.", Sentinel);
                product.LogInformation("{Marker} naming the configured {TenantId}.", Sentinel, configured);
                own.LogInformation("shown-6 by the harness, whatever flow it is on: {Count}.", 1);
            },
            Token);

        Assert.Equal(
            ["shown-1", "shown-2", "shown-3", "shown-4", "shown-5", "shown-6"],
            sink.Records.Select(record => record.Message[..7]).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Sentinel, sink.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(other.ToString(), sink.Text, StringComparison.OrdinalIgnoreCase);

        // Each withheld record left exactly a count, by whose it was.
        Assert.Equal(
            new Dictionary<LogRecordOwner, long> { [LogRecordOwner.OtherTenant] = 9, [LogRecordOwner.Unattributed] = 2 },
            diagnostics.Snapshot().WithheldLogRecords);
        Assert.Contains("log-record-withheld OtherTenant = 9", diagnostics.Render());
        Assert.Contains("log-record-withheld Unattributed = 2", diagnostics.Render());

        // The decision itself, case by case.
        var unresolved = TenantContext.Unresolved;
        Assert.Equal(LogRecordOwner.Unattributed, HarnessLogPolicy.OwnerOf("WorkContinuity.Web.Connectors.MondayWebhookEndpoint", configured, unresolved, null));
        Assert.Equal(LogRecordOwner.Harness, HarnessLogPolicy.OwnerOf("WorkContinuity.Harness.Monday.Operator", configured, unresolved, null));
        Assert.Equal(LogRecordOwner.Unattributed, HarnessLogPolicy.OwnerOf("WorkContinuity.HarnessLookalike", configured, unresolved, null));
        Assert.True(HarnessLogPolicy.Shows(LogRecordOwner.Harness) && HarnessLogPolicy.Shows(LogRecordOwner.ConfiguredTenant));
        Assert.False(HarnessLogPolicy.Shows(LogRecordOwner.OtherTenant) || HarnessLogPolicy.Shows(LogRecordOwner.Unattributed));
        Assert.Throws<ArgumentException>(() => new HarnessLoggerFactory(sink, diagnostics, default, accessor));
    }

    private static TransportEnvelope Envelope(TenantId tenant)
    {
        var receipt = Guid.CreateVersion7();
        var envelope = TransportEnvelope.FromOutbox(
            Guid.CreateVersion7(),
            tenant,
            Core.Ingress.IngressMessageTypes.ReceiptReceived,
            Transport.TransportData.Marker(receipt),
            Core.Correlation.CorrelationId.New());
        Assert.True(envelope.IsSuccess);
        return envelope.Value;
    }

    private async Task<(Guid Tenant, Guid User)> OwnerOfAsync(Guid connection)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT tenant_id, canonical_user_id FROM connector_authorizations WHERE connection_id = @connection;", admin);
        command.Parameters.AddWithValue("connection", connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    private async Task<string> AuthorizationRowAsync(Guid connection)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT row_to_json(a)::text FROM connector_authorizations AS a WHERE connection_id = @connection;", admin);
        command.Parameters.AddWithValue("connection", connection);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    // The one outbox row of a tenant that has a single delivery: its id and its correlation id, read by the schema owner.
    private async Task<(Guid OutboxMessage, string Correlation)> PendingOutboxRowAsync(TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT outbox_message_id, correlation_id::text FROM outbox_messages WHERE tenant_id = @tenant;", admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        var row = (reader.GetGuid(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync(Token));
        return row;
    }

    private async Task<long> CountAsync(string table, TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @tenant;", admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}
