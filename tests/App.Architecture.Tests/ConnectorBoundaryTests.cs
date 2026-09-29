using System.Text.RegularExpressions;

namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Gate 3A source rules for the connector: the monday API version and host are each written in exactly one place, the
/// Core connector contract stays provider-neutral, and the connector's source names no GraphQL write except in the guard
/// that refuses one.
/// </summary>
public sealed class ConnectorBoundaryTests
{
    private const string VersionFile = "src/App.Connectors.Monday/MondayApiVersion.cs";
    private const string EndpointFile = "src/App.Connectors.Monday/MondayEndpoints.cs";
    private const string GuardFile = "src/App.Connectors.Monday/MondayQueryGuard.cs";

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
