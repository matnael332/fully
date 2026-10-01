using System.Text.RegularExpressions;

namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Gate 3A, 3B and 3C source rules for the connector: the monday API version and hosts are each written in exactly one
/// place, only the OAuth client calls the token and revocation endpoints, the Core contract and the authorization
/// store stay provider-neutral, the connector's source names no GraphQL write except in the guard that refuses one,
/// neither Core nor the connector takes a package, and the connector does not log. Webhook intake (3C) stays in its own
/// files, its route is written once, it reaches storage only through the Gate 2 ports, no webhook subscription is created
/// or deleted, and the Web host runs nothing else of the Event Backbone. Normalization (3D) lives only in the monday
/// normalizer, and Core's processing contract names no provider.
/// </summary>
public sealed class ConnectorBoundaryTests
{
    private const string VersionFile = "src/App.Connectors.Monday/MondayApiVersion.cs";
    private const string EndpointFile = "src/App.Connectors.Monday/MondayEndpoints.cs";
    private const string GuardFile = "src/App.Connectors.Monday/MondayQueryGuard.cs";
    private const string OAuthClientFile = "src/App.Connectors.Monday/MondayOAuthClient.cs";
    private const string AuthorizerFile = "src/App.Connectors.Monday/MondayConnectionAuthorizer.cs";
    private const string ScopePolicyFile = "src/App.Connectors.Monday/MondayScopePolicy.cs";
    private const string SecretStoreFile = "src/App.Connectors.Monday/IMondaySecretStore.cs";
    private const string WebhooksFile = "src/App.Connectors.Monday/MondayWebhooks.cs";
    private const string VerifierFile = "src/App.Connectors.Monday/MondayWebhookVerifier.cs";
    private const string NormalizerFile = "src/App.Connectors.Monday/MondayWebhookNormalizer.cs";
    private const string WebEndpointFile = "src/App.Web/Connectors/MondayWebhookEndpoint.cs";
    private const string WebRegistrationFile = "src/App.Web/Connectors/MondayWebhookRegistration.cs";

    private static readonly string[] WebhookFiles =
    [
        WebhooksFile,
        VerifierFile,
        "src/App.Connectors.Monday/MondayWebhookAuthenticator.cs",
        "src/App.Connectors.Monday/MondayWebhookBody.cs",
        "src/App.Connectors.Monday/MondayWebhookOptions.cs",
        "src/App.Connectors.Monday/MondayWebhookToken.cs",
        NormalizerFile,
    ];

    [Fact]
    public void The_monday_api_version_and_its_header_are_written_in_exactly_one_place()
    {
        // A monday version is a quarter string such as "2026-07"; any such literal in the connector is a version pin.
        var versionLiteral = new Regex("\"20\\d{2}-(01|04|07|10)\"", RegexOptions.CultureInvariant);

        var versionHolders = MondaySources()
            .Where(file => versionLiteral.IsMatch(File.ReadAllText(file.FullName)))
            .Select(RepositoryLayout.RelativePath)
            .ToList();
        var headerHolders = ProductionSources()
            .Where(file => File.ReadAllText(file.FullName).Contains("\"API-Version\"", StringComparison.OrdinalIgnoreCase))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Equal(new[] { VersionFile }, versionHolders);
        Assert.Equal(new[] { VersionFile }, headerHolders);
    }

    [Fact]
    public void The_monday_api_host_is_written_in_exactly_one_place()
    {
        var holders = ProductionSources()
            .Where(file => File.ReadAllText(file.FullName).Contains("api.monday.com", StringComparison.OrdinalIgnoreCase))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Equal(new[] { EndpointFile }, holders);
    }

    [Fact]
    public void The_core_connector_contract_is_provider_neutral()
    {
        var connectorContract = RepositoryLayout.CoreSourceFiles
            .Where(file => RepositoryLayout.RelativePath(file).StartsWith("src/App.Core/Connectors/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(connectorContract);
        Assert.All(connectorContract, file =>
            Assert.DoesNotContain("monday", File.ReadAllText(file.FullName), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_connector_source_names_no_graphql_write_outside_the_guard_that_refuses_one()
    {
        var write = new Regex(@"\b(mutation|subscription)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var violations = MondaySources()
            .Where(file => RepositoryLayout.RelativePath(file) != GuardFile)
            .Where(file => write.IsMatch(File.ReadAllText(file.FullName)))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Empty(violations);
        Assert.Matches(write, File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, GuardFile)));
    }

    [Fact]
    public void The_monday_authorization_host_is_written_in_exactly_one_place()
    {
        var holders = ProductionSources()
            .Where(file => File.ReadAllText(file.FullName).Contains("auth.monday.com", StringComparison.OrdinalIgnoreCase))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Equal(new[] { EndpointFile }, holders);
    }

    [Fact]
    public void Only_the_oauth_client_calls_the_token_and_revocation_endpoints_and_only_the_authorizer_builds_an_authorization_address()
    {
        Assert.Equal(new[] { OAuthClientFile }, Holders("MondayEndpoints.Token"));
        Assert.Equal(new[] { OAuthClientFile }, Holders("MondayEndpoints.Revoke"));
        Assert.Equal(new[] { AuthorizerFile }, Holders("MondayEndpoints.Authorize"));
    }

    [Fact]
    public void Neither_core_nor_the_connector_takes_a_package()
    {
        foreach (var project in new[] { "src/App.Core/App.Core.csproj", "src/App.Connectors.Monday/App.Connectors.Monday.csproj" })
        {
            var text = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, project));
            Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_authorization_store_its_records_and_its_migration_are_provider_neutral()
    {
        var infrastructure = Path.Combine(RepositoryLayout.Root.FullName, "src", "App.Infrastructure");
        var files = new DirectoryInfo(Path.Combine(infrastructure, "Connectors")).EnumerateFiles("*.cs")
            .Concat(new DirectoryInfo(Path.Combine(infrastructure, "Persistence", "Entities")).EnumerateFiles("ConnectorAuthorization*.cs"))
            .Concat(new DirectoryInfo(Path.Combine(infrastructure, "Persistence", "Migrations")).EnumerateFiles("*_ConnectorAuthorizations.cs"))
            .ToList();

        Assert.Equal(4, files.Count);
        Assert.All(files, file => Assert.DoesNotContain("monday", File.ReadAllText(file.FullName), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Webhook_code_stays_in_its_own_files_and_normalization_lives_only_in_the_normalizer()
    {
        // Gate 3C brought webhook intake and 3D its normalization. Every connector file that mentions a webhook is an
        // intake or normalization file, the scope allowlist (monday's read scope names one) or the secret-name catalogue.
        var webhook = MondaySources()
            .Where(file => File.ReadAllText(file.FullName).Contains("webhook", StringComparison.OrdinalIgnoreCase))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(WebhookFiles.Concat([ScopePolicyFile, SecretStoreFile]).Order(StringComparer.Ordinal), webhook);

        // The connector supplies the source verifier and the normalizer, each in its own file, and builds submissions.
        // It never calls ingress or the connection check itself.
        Assert.Equal(new[] { VerifierFile }, MondayHolders("ISourceVerifier"));
        Assert.Equal(new[] { NormalizerFile }, MondayHolders("ISourceEventNormalizer"));
        Assert.Equal(new[] { NormalizerFile }, MondayHolders("NormalizedSourceEvent"));
        Assert.Equal(new[] { NormalizerFile }, MondayHolders("SourcePayload"));

        foreach (var port in new[] { "IEventIngress", "IIngressConnectionCheck", "IEventProcessor", "EventProcessingResult", "ProjectionHandoff", "SnapshotProjection", "SnapshotField", "SnapshotObject" })
        {
            Assert.DoesNotContain(MondaySources(), file => File.ReadAllText(file.FullName).Contains(port, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_core_processing_contract_names_no_provider_or_provider_field()
    {
        var processing = RepositoryLayout.CoreSourceFiles
            .Where(file => RepositoryLayout.RelativePath(file).StartsWith("src/App.Core/Processing/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(processing);
        Assert.All(processing, file =>
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var specific in new[] { "Monday", "pulse", "columnId", "triggerUuid", "changedAt", "triggerTime", "Jwt", "HS256", "label" })
            {
                Assert.DoesNotContain(specific, text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void The_webhook_route_is_written_in_exactly_one_place()
    {
        // The address the host serves and the audience a connection's tokens carry come from this one constant.
        Assert.Equal(new[] { WebhooksFile }, Holders("/api/connectors/monday/webhooks"));
    }

    [Fact]
    public void No_webhook_subscription_is_created_or_deleted_and_no_webhook_write_scope_exists()
    {
        // Decision D3C-1: intake only. Creating or deleting a real webhook needs webhooks:write, an owner boundary.
        Assert.Empty(Holders("create_webhook"));
        Assert.Empty(Holders("delete_webhook"));
        Assert.Empty(Holders("webhooks:write"));
    }

    [Fact]
    public void The_connector_does_not_log()
    {
        foreach (var file in MondaySources())
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var logging in new[] { "ILogger", "Microsoft.Extensions.Logging", "Console.", "Debug.Write", "Trace.Write" })
            {
                Assert.DoesNotContain(logging, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_web_endpoint_reaches_storage_only_through_the_gate_2_ingress_ports()
    {
        var files = WebSources()
            .Where(file => file.FullName.Replace('\\', '/').Contains("/src/App.Web/Connectors/", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { WebEndpointFile, WebRegistrationFile }, files);

        foreach (var file in files)
        {
            var text = Read(file);
            foreach (var forbidden in new[]
                     {
                         "DbContext", "ITenantScopedDatabase", "RawReceipt", "OutboxMessage", "raw_receipts", "outbox_messages",
                         "Npgsql", "resolve_ingress_connection", "SetResolved", "TenantId", "TenantContext.Resolved",
                     })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }

        var endpoint = Read(WebEndpointFile);
        Assert.Contains("GetRequiredService<IEventIngress>()", endpoint, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<IIngressConnectionCheck>()", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_host_composes_webhook_intake_and_runs_nothing_else_of_the_event_backbone()
    {
        var program = Read("src/App.Web/Program.cs");
        Assert.Contains("builder.Services.AddMondayWebhookIntake(builder.Configuration);", program, StringComparison.Ordinal);
        Assert.Contains("app.MapMondayWebhooks();", program, StringComparison.Ordinal);

        // The in-process ingress is all of the Event Backbone the Web host may run: no transport, relay, consumer,
        // processing, purge or migration, and no background service at all.
        foreach (var file in WebSources())
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var runtime in new[]
                     {
                         "AddEventTransport", "AddServiceBusEventTransport", "AddEventProcessing", "AddRawContentRetention",
                         "AddDatabaseMigrator", "AddHostedService", "BackgroundService", "IHostedService",
                     })
            {
                Assert.DoesNotContain(runtime, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_core_ingress_contract_names_no_provider_or_token_format()
    {
        var ingress = RepositoryLayout.CoreSourceFiles
            .Where(file => RepositoryLayout.RelativePath(file).StartsWith("src/App.Core/Ingress/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(ingress);
        Assert.All(ingress, file =>
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var specific in new[] { "Monday", "Jwt", "JWT", "HS256", "Signing", "accountId", "triggerUuid", "Bearer" })
            {
                Assert.DoesNotContain(specific, text, StringComparison.Ordinal);
            }
        });
    }

    private static List<string> Holders(string text) =>
        ProductionSources()
            .Where(file => File.ReadAllText(file.FullName).Contains(text, StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

    private static IEnumerable<FileInfo> ProductionSources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static List<string> MondayHolders(string text) =>
        MondaySources()
            .Where(file => File.ReadAllText(file.FullName).Contains(text, StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

    private static IEnumerable<FileInfo> WebSources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", "App.Web"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));

    private static IEnumerable<FileInfo> MondaySources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", "App.Connectors.Monday"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(FileInfo file)
    {
        var path = file.FullName.Replace('\\', '/');
        return path.Contains("/bin/", StringComparison.Ordinal) || path.Contains("/obj/", StringComparison.Ordinal);
    }
}
