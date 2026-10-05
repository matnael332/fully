using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Evidence;
using WorkContinuity.Harness.Monday.Provider;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.InitialSync;
using WorkContinuity.Integration.Tests.Reconciliation;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the harness fails closed. Its defaults hold no secret and send nothing, a missing fake ends at
/// an intercepting transport and never at the network, the standalone entry point does not run, and the harness refuses
/// a privileged database role. Every provider request in these tests is intercepted in process; none is a live call.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessFailClosedTests(DatabaseFixture fixture)
{
    private const string Board = "1771812698";
    private const string Item = "1772099001";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_secret_storage_no_authorization_starts_no_webhook_is_verified_and_nothing_is_sent()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { WithSecrets = false });
        var monday = Assert.IsType<FakeMonday>(harness.Monday);

        // The capability is valid, but the verifier cannot be stored: the browser is sent to the terminal page, never to
        // the provider, and holds no binding.
        var answer = await harness.ConsoleAsync("authorize");
        var start = answer.Split('\n', StringSplitOptions.TrimEntries).Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        using var started = await harness.GetOperatorAsync(new Uri(start).PathAndQuery);
        Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
        Assert.Equal("/operator/done", started.Headers.Location!.OriginalString);
        Assert.False(started.Headers.Contains("Set-Cookie"));

        var diagnostics = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(1, diagnostics.AuthorizationStarts[AuthorizationStartOutcome.StorageFailed]);
        Assert.Empty(diagnostics.ProviderCalls);
        Assert.Empty(monday.Requests);
        Assert.Empty(await harness.ConnectionsAsync());

        // Intake cannot verify a token without the Signing Secret: a signed delivery is refused as retryable, and nothing is stored.
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        using var delivery = await harness.PostWebhookAsync(
            connection.Connection,
            HarnessTestHost.WebhookBody(Item, "Done", WebhookData.NewTrigger(), Board),
            WebhookTestTokens.Mint(connection.Connection, connection.Account, harness.Clock.UtcNow));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, delivery.StatusCode);
        Assert.Empty(await ReconciliationData.ReceiptsAsync(fixture, connection.Tenant));
    }

    [Fact]
    public async Task Without_a_fake_provider_a_code_exchange_ends_at_the_refusing_transport_and_authorizes_nothing()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { WithProvider = false });
        Assert.Null(harness.Monday);

        // The start needs no provider: the verifier is stored and the browser is sent on with its binding.
        var started = await harness.StartAuthorizationAsync();
        Assert.Empty(harness.Host.Diagnostics.Snapshot().ProviderCalls);

        // The callback's exchange is the first provider request, and it is intercepted: counted once, sent nowhere.
        using var callback = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        var diagnostics = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(
            new Dictionary<(ProviderEndpoint, ProviderCallOutcome), long> { [(ProviderEndpoint.Token, ProviderCallOutcome.RefusedNoTransport)] = 1 },
            diagnostics.ProviderCalls);
        Assert.Equal(1, diagnostics.AuthorizationCompletions[AuthorizationCompletionOutcome.ExchangeFailed]);

        // No connection exists, and no token pair was stored.
        Assert.Empty(await harness.ConnectionsAsync());
        Assert.DoesNotContain(Assert.IsType<MemorySecrets>(harness.Secrets).Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_fake_provider_a_read_ends_at_the_refusing_transport_and_never_reaches_another_instances_fake()
    {
        // One instance, with the fake provider, authorizes a connection and configures its board.
        var secrets = new MemorySecrets();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var authorized = await HarnessTestHost.StartAsync(fixture, new HarnessTestSetup { Secrets = secrets, Clock = clock });
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        authorized.Account1.AddBoard(Board);
        authorized.Account1.AddItem(Item, Board, "Done", at);
        var connection = await authorized.AuthorizeAsync();
        Assert.Contains("enabled=True", await authorized.ConsoleAsync($"scope {connection:D} {Board} enable"), StringComparison.Ordinal);
        var asked = Assert.IsType<FakeMonday>(authorized.Monday).Requests.Count;

        // A second instance for the same tenant holds the same stored credential but was given no provider transport.
        await using var bare = await HarnessTestHost.StartAsync(
            fixture,
            new HarnessTestSetup { Tenant = authorized.Tenant, User = authorized.User, Secrets = secrets, Clock = clock, WithProvider = false });
        Assert.Contains("state=Buffering", await bare.ConsoleAsync($"sync-start {connection:D}"), StringComparison.Ordinal);
        var run = await bare.ConsoleAsync($"sync-run {connection:D}");

        // Its reads end at its own refusing transport: counted, never sent, and the generation does not go live.
        Assert.DoesNotContain("state=Live", run, StringComparison.Ordinal);
        var calls = bare.Host.Diagnostics.Snapshot().ProviderCalls;
        Assert.True(calls[(ProviderEndpoint.Api, ProviderCallOutcome.RefusedNoTransport)] >= 1);
        Assert.All(calls.Keys, key => Assert.Equal(ProviderCallOutcome.RefusedNoTransport, key.Outcome));
        Assert.Equal(asked, authorized.Monday!.Requests.Count);
        Assert.Empty(authorized.Account1.Answered);
        Assert.Empty((await bare.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields);
    }

    [Fact]
    public async Task The_guarded_path_refuses_every_address_that_is_not_one_of_the_three_fixed_endpoints_before_the_transport()
    {
        var diagnostics = new HarnessDiagnostics();
        var recording = new FakeMonday();
        recording.ThenToken(FakeMonday.Tokens("a", "r"));
        using var guarded = new GuardedProviderTransport(recording, diagnostics);
        using var invoker = new HttpMessageInvoker(guarded);

        foreach (var foreign in new[]
                 {
                     "https://example.invalid/v2",
                     "http://127.0.0.1:1/oauth_ms/oauth/token",
                     MondayEndpoints.Api.AbsoluteUri + "/extra",
                     MondayEndpoints.Token.AbsoluteUri.Replace("https://", "http://", StringComparison.Ordinal),
                     MondayEndpoints.Authorize.AbsoluteUri,
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, foreign);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, Token));
            Assert.Equal(HttpRequestError.ConnectionError, refused.HttpRequestError);
        }

        Assert.Empty(recording.Requests);
        Assert.Equal(5, diagnostics.Snapshot().ProviderCalls[(ProviderEndpoint.Other, ProviderCallOutcome.RefusedForeignAddress)]);

        // The fixed endpoints pass and are counted by endpoint and status class only.
        using (var token = new HttpRequestMessage(HttpMethod.Post, MondayEndpoints.Token))
        using (var answered = await invoker.SendAsync(token, Token))
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        Assert.Equal(1, diagnostics.Snapshot().ProviderCalls[(ProviderEndpoint.Token, ProviderCallOutcome.Success)]);
        Assert.Single(recording.Requests);
    }

    [Fact]
    public async Task The_default_transport_and_the_default_secret_store_refuse_everything()
    {
        var diagnostics = new HarnessDiagnostics();
        var refusing = new RefusingProviderTransport();
        using var guarded = new GuardedProviderTransport(refusing, diagnostics);
        using var invoker = new HttpMessageInvoker(guarded);
        foreach (var endpoint in new[] { MondayEndpoints.Token, MondayEndpoints.Revoke, MondayEndpoints.Api })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, Token));

            // The form the connector reads as "the request never left".
            Assert.Equal(HttpRequestError.ConnectionError, refused.HttpRequestError);
        }

        Assert.Equal(3, refusing.Refused);
        Assert.All(diagnostics.Snapshot().ProviderCalls, call => Assert.Equal((ProviderCallOutcome.RefusedNoTransport, 1L), (call.Key.Outcome, call.Value)));

        var store = new RefusingSecretStore();
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(MondaySecretName.ClientSecret, new byte[] { 1 }, Token));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(MondaySecretName.SigningSecret, Token)).Status);
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.DeleteAsync(MondaySecretName.ClientSecret, Token));
    }

    [Fact]
    public async Task The_standalone_entry_point_does_not_run_whatever_it_is_configured_with()
    {
        // Nothing configured: refused at the configuration boundary.
        using var input = new StringReader("authorize\n");
        using (var output = new StringWriter())
        {
            Assert.Equal(HarnessProgram.NotConfigured, await HarnessProgram.RunAsync(new ConfigurationBuilder().Build(), input, output, Token));
            Assert.Equal("harness: not started (TenantMissingOrMalformed)" + Environment.NewLine, output.ToString());
        }

        // Fully configured, for the suite's own database and for any other name: no database is approved for a
        // standalone run, so it stops before it opens a listener or a connection. The second address answers nothing,
        // and the answer is the same.
        foreach (var connectionString in new[]
                 {
                     fixture.ApplicationConnectionString,
                     "Host=127.0.0.1;Port=1;Database=wce_harness_standalone;Username=workcontinuity_app;Timeout=1",
                 })
        {
            using var output = new StringWriter();
            var configuration = HarnessTestHost.ConfigurationFor(connectionString, TenantId.New(), UserId.New(), HarnessTestHost.FreePort());
            Assert.Equal(HarnessProgram.NotApproved, await HarnessProgram.RunAsync(configuration, input, output, Token));
            Assert.Equal("harness: not started (StandaloneDatabaseNotApproved)" + Environment.NewLine, output.ToString());
            Assert.False(HarnessStandaloneGate.Permits(Assert.IsType<HarnessOptions>(HarnessOptions.Read(configuration).Options)));
        }
    }

    [Fact]
    public async Task The_composition_does_not_start_without_a_certificate_or_a_local_transport()
    {
        var options = HarnessTestHost.OptionsFor(fixture.ApplicationConnectionString, TenantId.New(), UserId.New(), HarnessTestHost.FreePort());

        // The standalone entry point's seams: none. The operator listener is TLS only and takes no certificate from the machine.
        Assert.Equal(HarnessRefusal.OperatorCertificateUnavailable, (await HarnessHost.StartAsync(options, HarnessSeams.None, Token)).Refusal);
        Assert.Equal(
            HarnessRefusal.OperatorCertificateUnavailable,
            (await HarnessHost.StartAsync(options, new HarnessSeams { RunBackboneLoops = true }, Token)).Refusal);

        // A certificate without its private key is no certificate for a listener: refused the same way, before anything starts.
        using var publicOnly = X509CertificateLoader.LoadCertificate(LoopbackCertificate.Instance.RawData);
        Assert.False(publicOnly.HasPrivateKey);
        Assert.Equal(
            HarnessRefusal.OperatorCertificateUnavailable,
            (await HarnessHost.StartAsync(options, new HarnessSeams { OperatorCertificate = publicOnly, TransportPublisher = new InMemoryTransport() }, Token)).Refusal);

        // With a certificate but no broker: only the local emulator may be configured, and none is.
        Assert.Equal(
            HarnessRefusal.TransportNotTheLocalEmulator,
            (await HarnessHost.StartAsync(options, new HarnessSeams { OperatorCertificate = LoopbackCertificate.Instance }, Token)).Refusal);
    }

    [Fact]
    public async Task The_composition_refuses_the_schema_owner_and_an_unreachable_database()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var seams = new HarnessSeams { OperatorCertificate = LoopbackCertificate.Instance, TransportPublisher = new InMemoryTransport(), LogSink = new CapturingHarnessSink() };

        // The schema owner owns the tables and would step around row-level security: the harness holds the runtime role only.
        var asOwner = HarnessTestHost.OptionsFor(fixture.AdministrativeConnectionString, tenant, user, HarnessTestHost.FreePort());
        Assert.Equal(HarnessRefusal.PrivilegedDatabaseRole, (await HarnessHost.StartAsync(asOwner, seams, Token)).Refusal);

        // Positive control: the same composition starts as the runtime role.
        var asRuntime = HarnessTestHost.OptionsFor(fixture.ApplicationConnectionString, tenant, user, HarnessTestHost.FreePort());
        var started = await HarnessHost.StartAsync(asRuntime, seams, Token);
        Assert.Equal(HarnessRefusal.None, started.Refusal);
        await Assert.IsType<HarnessHost>(started.Host).DisposeAsync();

        var unreachable = HarnessTestHost.OptionsFor(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", tenant, user, HarnessTestHost.FreePort());
        Assert.Equal(HarnessRefusal.DatabaseUnavailable, (await HarnessHost.StartAsync(unreachable, seams, Token)).Refusal);
    }

    [Fact]
    public async Task The_role_check_reads_each_privilege_by_itself_and_any_one_of_them_makes_the_role_privileged()
    {
        // The decision: any one of the three is enough, and only a role with none of them passes.
        foreach (var superuser in new[] { false, true })
        {
            foreach (var bypasses in new[] { false, true })
            {
                foreach (var owns in new[] { false, true })
                {
                    Assert.Equal(superuser || bypasses || owns, new DatabaseRoleFacts(superuser, bypasses, owns).IsPrivileged);
                }
            }
        }

        // A role the catalog does not answer about is assumed to have every privilege.
        Assert.Equal(new DatabaseRoleFacts(true, true, true), DatabaseRoleFacts.Unknown);

        // The catalog, read as each of the two roles the local environment has. The runtime role has none of the three.
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        Assert.Equal(new DatabaseRoleFacts(false, false, false), await RoleFactsAsync(fixture.ApplicationConnectionString, tenant));

        // The local schema owner is the instance's own superuser: each of the three is read as true by itself. No local
        // role has exactly one of them, and this test creates none.
        Assert.Equal(new DatabaseRoleFacts(true, true, true), await RoleFactsAsync(fixture.AdministrativeConnectionString, tenant));
    }

    // Reads the catalog through the harness's own check, on a flow of its own that runs as the tenant.
    private Task<DatabaseRoleFacts> RoleFactsAsync(string connectionString, TenantId tenant) =>
        Task.Run(
            async () =>
            {
                var accessor = new TenantContextAccessor();
                accessor.SetResolved(TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
                await using var context = new WorkContinuityDbContext(
                    new DbContextOptionsBuilder<WorkContinuityDbContext>().UseNpgsql(connectionString).Options);
                var database = new TenantScopedDatabase(context, accessor, NullLogger<TenantScopedDatabase>.Instance);
                var read = await new DatabaseRoleCheck(database).ReadAsync(Token);
                Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
                return read.Value;
            },
            Token);
}

/// <summary>
/// Gate 3G, offline slice: the reviewed configuration boundary. Everything the harness is told is validated here and
/// fails closed, and nothing a refusal says quotes a configured value.
/// </summary>
public sealed class HarnessOptionsTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId User = UserId.New();

    [Fact]
    public void A_complete_configuration_yields_the_trusted_chain_and_a_loopback_operator_address()
    {
        var read = HarnessOptions.Read(Configuration());

        var options = Assert.IsType<HarnessOptions>(read.Options);
        Assert.Equal((Tenant, User, "wce_harness_database", "wce-client"), (options.Tenant, options.CanonicalUser, options.DatabaseName, options.ClientId));
        Assert.Equal(("127.0.0.1:8443", "https://127.0.0.1:8443", 8443), (options.OperatorAuthority, options.OperatorOrigin, options.OperatorPort));
        Assert.Equal("https://127.0.0.1:8443/operator/callback", options.RedirectUri.AbsoluteUri);
        Assert.Null(options.EmulatorConnectionString);
        Assert.Equal("HarnessOptions(redacted)", options.ToString());
    }

    [Theory]
    [InlineData(HarnessOptions.TenantKey, null, HarnessRefusal.TenantMissingOrMalformed)]
    [InlineData(HarnessOptions.TenantKey, "not-a-guid", HarnessRefusal.TenantMissingOrMalformed)]
    [InlineData(HarnessOptions.TenantKey, "00000000-0000-0000-0000-000000000000", HarnessRefusal.TenantMissingOrMalformed)]
    [InlineData(HarnessOptions.CanonicalUserKey, null, HarnessRefusal.CanonicalUserMissingOrMalformed)]
    [InlineData(HarnessOptions.CanonicalUserKey, "someone@example.test", HarnessRefusal.CanonicalUserMissingOrMalformed)]
    [InlineData(HarnessOptions.DatabaseKey, null, HarnessRefusal.DatabaseMissingOrMalformed)]
    [InlineData(HarnessOptions.DatabaseKey, "Host=localhost;Username=workcontinuity_app", HarnessRefusal.DatabaseMissingOrMalformed)]
    [InlineData(HarnessOptions.DatabaseKey, "Host=localhost;Database=wce", HarnessRefusal.DatabaseMissingOrMalformed)]
    [InlineData(HarnessOptions.DatabaseKey, "this is not a connection string", HarnessRefusal.DatabaseMissingOrMalformed)]
    [InlineData(HarnessOptions.ClientIdKey, null, HarnessRefusal.ClientIdMissingOrMalformed)]
    [InlineData(HarnessOptions.ClientIdKey, "has a space", HarnessRefusal.ClientIdMissingOrMalformed)]
    [InlineData(HarnessOptions.RedirectKey, null, HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "http://127.0.0.1:8443/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://localhost:8443/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://0.0.0.0:8443/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://harness.example.test:8443/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://127.0.0.1/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://127.0.0.1:8443/operator/other", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://127.0.0.1:8443/operator/callback?next=1", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.RedirectKey, "https://user@127.0.0.1:8443/operator/callback", HarnessRefusal.RedirectMissingOrNotLoopbackHttps)]
    [InlineData(HarnessOptions.WebhookBaseAddressKey, null, HarnessRefusal.WebhookBaseAddressMissingOrNotHttps)]
    [InlineData(HarnessOptions.WebhookBaseAddressKey, "http://fully.example.test", HarnessRefusal.WebhookBaseAddressMissingOrNotHttps)]
    [InlineData(HarnessOptions.WebhookBaseAddressKey, "https://fully.example.test/?x=1", HarnessRefusal.WebhookBaseAddressMissingOrNotHttps)]
    [InlineData(HarnessOptions.WebhookPortKey, null, HarnessRefusal.WebhookPortMissingOrMalformed)]
    [InlineData(HarnessOptions.WebhookPortKey, "-1", HarnessRefusal.WebhookPortMissingOrMalformed)]
    [InlineData(HarnessOptions.WebhookPortKey, "70000", HarnessRefusal.WebhookPortMissingOrMalformed)]
    [InlineData(HarnessOptions.WebhookPortKey, "8443", HarnessRefusal.WebhookPortMissingOrMalformed)]
    [InlineData(HarnessOptions.TransportKey, "Endpoint=sb://wce.servicebus.windows.net/;SharedAccessKeyName=n;SharedAccessKey=k", HarnessRefusal.TransportNotTheLocalEmulator)]
    [InlineData(HarnessOptions.TransportKey, "Endpoint=sb://wce.servicebus.windows.net/;SharedAccessKeyName=n;SharedAccessKey=k;UseDevelopmentEmulator=true", HarnessRefusal.TransportNotTheLocalEmulator)]
    [InlineData(HarnessOptions.TransportKey, "Endpoint=sb://localhost;SharedAccessKeyName=n;SharedAccessKey=k", HarnessRefusal.TransportNotTheLocalEmulator)]
    [InlineData(HarnessOptions.TransportNamespaceKey, "wce.servicebus.windows.net", HarnessRefusal.TransportNotTheLocalEmulator)]
    public void An_incomplete_or_invalid_configuration_is_refused_by_a_closed_reason(string key, string? value, HarnessRefusal expected)
    {
        var read = HarnessOptions.Read(Configuration((key, value)));

        Assert.Null(read.Options);
        Assert.Equal(expected, read.Refusal);
    }

    [Fact]
    public void Only_the_local_emulator_is_accepted_as_a_transport()
    {
        const string Emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

        var options = Assert.IsType<HarnessOptions>(HarnessOptions.Read(Configuration((HarnessOptions.TransportKey, Emulator))).Options);

        Assert.Equal(Emulator, options.EmulatorConnectionString);
    }

    [Fact]
    public void No_database_is_approved_for_a_standalone_run()
    {
        foreach (var database in new[] { "workcontinuity", "wce_harness_database", "postgres", "wce_it_fresh" })
        {
            var options = Assert.IsType<HarnessOptions>(HarnessOptions.Read(Configuration(
                (HarnessOptions.DatabaseKey, $"Host=localhost;Database={database};Username=workcontinuity_app"))).Options);

            Assert.False(HarnessStandaloneGate.Permits(options));
        }
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        {
            [HarnessOptions.TenantKey] = Tenant.ToString(),
            [HarnessOptions.CanonicalUserKey] = User.ToString(),
            [HarnessOptions.DatabaseKey] = "Host=localhost;Port=5432;Database=wce_harness_database;Username=workcontinuity_app",
            [HarnessOptions.ClientIdKey] = "wce-client",
            [HarnessOptions.RedirectKey] = "https://127.0.0.1:8443/operator/callback",
            [HarnessOptions.WebhookPortKey] = "0",
            [HarnessOptions.WebhookBaseAddressKey] = "https://fully.example.test",
        };
        foreach (var (key, value) in changes)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
