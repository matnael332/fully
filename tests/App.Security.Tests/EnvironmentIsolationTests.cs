using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Dev and Synthetic are separate data and secret boundaries. Gate 1E proved it by compiling both
/// parameter files and diffing the result by hand; this is that proof as a test, read straight from
/// the parameter files so it needs no Azure tooling.
/// </summary>
public sealed class EnvironmentIsolationTests
{
    private static readonly IReadOnlyDictionary<string, ParameterValue> Dev =
        Parse("infra/bicep/parameters/main.dev.bicepparam");

    private static readonly IReadOnlyDictionary<string, ParameterValue> Synthetic =
        Parse("infra/bicep/parameters/main.synthetic.bicepparam");

    private static string DevText => Text("infra/bicep/parameters/main.dev.bicepparam");

    private static string SyntheticText => Text("infra/bicep/parameters/main.synthetic.bicepparam");

    [Fact]
    public void Dev_and_synthetic_are_different_environments_in_different_address_spaces()
    {
        Assert.Equal("dev", Dev["environmentName"].Literal);
        Assert.Equal("synthetic", Synthetic["environmentName"].Literal);

        foreach (var parameter in new[] { "vnetAddressPrefix", "appsSubnetPrefix", "postgresSubnetPrefix" })
        {
            Assert.NotNull(Dev[parameter].Literal);
            Assert.NotEqual(Dev[parameter].Literal, Synthetic[parameter].Literal);
        }

        // The resource group name is derived from both values, so the pair must differ.
        Assert.NotEqual(
            (Dev["environmentName"].Literal, Dev["regionToken"].Literal),
            (Synthetic["environmentName"].Literal, Synthetic["regionToken"].Literal));
    }

    [Fact]
    public void Secrets_come_from_environment_specific_variables_with_no_literal_value()
    {
        foreach (var (parameters, prefix) in new[] { (Dev, "WCE_DEV_"), (Synthetic, "WCE_SYNTHETIC_") })
        {
            foreach (var secret in new[] { "postgresAdministratorPassword", "postgresRuntimeRolePassword" })
            {
                var value = parameters[secret];
                Assert.NotNull(value.EnvironmentVariable);
                Assert.StartsWith(prefix, value.EnvironmentVariable, StringComparison.Ordinal);
                Assert.Equal(string.Empty, value.Literal);
            }
        }
    }

    [Fact]
    public void Every_environment_variable_a_parameter_file_reads_is_prefixed_for_its_own_environment()
    {
        var devVariables = Dev.Values.Select(value => value.EnvironmentVariable).OfType<string>().ToList();
        var syntheticVariables = Synthetic.Values.Select(value => value.EnvironmentVariable).OfType<string>().ToList();

        Assert.NotEmpty(devVariables);
        Assert.NotEmpty(syntheticVariables);
        Assert.All(devVariables, variable => Assert.StartsWith("WCE_DEV_", variable, StringComparison.Ordinal));
        Assert.All(syntheticVariables, variable => Assert.StartsWith("WCE_SYNTHETIC_", variable, StringComparison.Ordinal));
        Assert.Empty(devVariables.Intersect(syntheticVariables, StringComparer.Ordinal));
    }

    [Fact]
    public void Synthetic_receives_no_continuous_delivery_identity()
    {
        Assert.False(string.IsNullOrEmpty(Dev["githubRepository"].Literal));
        Assert.True(
            !Synthetic.TryGetValue("githubRepository", out var repository) || string.IsNullOrEmpty(repository.Literal),
            "Synthetic must not name a GitHub repository, which would create a deployment identity for it");
    }

    [Fact]
    public void Data_boundary_tags_name_their_own_environment()
    {
        Assert.Matches(@"dataBoundary:\s*'dev'", DevText);
        Assert.Matches(@"dataBoundary:\s*'synthetic'", SyntheticText);
    }

    [Fact]
    public void The_environment_name_is_constrained_and_drives_the_resource_group_name()
    {
        var main = Text("infra/bicep/main.bicep");

        Assert.Matches(@"@allowed\(\[\s*'dev'\s*'synthetic'\s*\]\)\s*param environmentName string", main);
        Assert.Contains("var resourceGroupName = 'rg-wce-${environmentName}-${regionToken}'", main, StringComparison.Ordinal);
    }

    [Fact]
    public void No_parameter_file_contains_a_literal_credential()
    {
        foreach (var text in new[] { DevText, SyntheticText })
        {
            foreach (var fragment in new[] { "Password=", "SharedAccessKey", "AccountKey", "sv=", "sig=" })
            {
                Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string Text(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyDictionary<string, ParameterValue> Parse(string relativePath)
    {
        var result = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(Text(relativePath), @"^param\s+(\w+)\s*=\s*(.+?)\s*$", RegexOptions.Multiline))
        {
            var raw = match.Groups[2].Value.TrimEnd('\r');
            var environment = Regex.Match(raw, @"readEnvironmentVariable\('([^']+)'\s*,\s*'([^']*)'\)");
            var literal = Regex.Match(raw, @"^'([^']*)'$");

            result[match.Groups[1].Value] = new ParameterValue(
                raw,
                environment.Success ? environment.Groups[1].Value : null,
                environment.Success ? environment.Groups[2].Value : literal.Success ? literal.Groups[1].Value : null);
        }

        return result;
    }

    private sealed record ParameterValue(string Raw, string? EnvironmentVariable, string? Literal);
}
