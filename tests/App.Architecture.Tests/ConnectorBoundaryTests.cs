using System.Text.RegularExpressions;

namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Gate 3A and 3B source rules for the connector: the monday API version and hosts are each written in exactly one
/// place, only the OAuth client calls the token and revocation endpoints, the Core contract and the authorization
/// store stay provider-neutral, the connector's source names no GraphQL write except in the guard that refuses one,
/// neither Core nor the connector takes a package, and no later sub-gate's webhook or normalization code has arrived.
/// </summary>
public sealed class ConnectorBoundaryTests
{
    private const string VersionFile = "src/App.Connectors.Monday/MondayApiVersion.cs";
    private const string EndpointFile = "src/App.Connectors.Monday/MondayEndpoints.cs";
    private const string GuardFile = "src/App.Connectors.Monday/MondayQueryGuard.cs";
    private const string OAuthClientFile = "src/App.Connectors.Monday/MondayOAuthClient.cs";
    private const string AuthorizerFile = "src/App.Connectors.Monday/MondayConnectionAuthorizer.cs";
    private const string ScopePolicyFile = "src/App.Connectors.Monday/MondayScopePolicy.cs";

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
    public void No_webhook_or_normalization_code_precedes_its_sub_gate()
    {
        // Gate 3B is the connection lifecycle only; webhook intake is 3C and normalization 3D. The only mention of a
        // webhook is the name of monday's read scope in the allowlist.
        var webhook = MondaySources()
            .Where(file => File.ReadAllText(file.FullName).Contains("webhook", StringComparison.OrdinalIgnoreCase))
            .Select(RepositoryLayout.RelativePath)
            .ToList();
        Assert.Equal(new[] { ScopePolicyFile }, webhook);

        foreach (var port in new[] { "ISourceVerifier", "ISourceEventNormalizer", "IEventIngress", "NormalizedSourceEvent" })
        {
            Assert.DoesNotContain(MondaySources(), file => File.ReadAllText(file.FullName).Contains(port, StringComparison.Ordinal));
        }
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
