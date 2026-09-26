namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Compile-time boundary rules for the modular monolith. These mirror
/// <c>docs/architecture/dependency-rules.md</c> and the <c>eng/verify-architecture.py</c> gate
/// check, so a boundary violation fails the build as well as the script.
/// </summary>
public sealed class DependencyBoundaryTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        ["App.Core"] = [],
        ["App.Infrastructure"] = ["App.Core"],
        ["App.Connectors.Monday"] = ["App.Core"],
        ["App.Agent.Windows"] = ["App.Core"],
        ["App.Simulator"] = ["App.Core"],
        ["App.Web"] = ["App.Core", "App.Infrastructure", "App.Connectors.Monday"],
        ["App.Worker"] = ["App.Core", "App.Infrastructure", "App.Connectors.Monday"],
    };

    [Fact]
    public void Every_expected_production_project_exists_and_nothing_extra_appeared()
    {
        var onDisk = RepositoryLayout.ProductionProjectFiles
            .Select(RepositoryLayout.ProjectName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expected = AllowedReferences.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, onDisk);
    }

    [Fact]
    public void Every_production_project_references_exactly_what_it_is_allowed_to()
    {
        var violations = new List<string>();

        foreach (var projectFile in RepositoryLayout.ProductionProjectFiles)
        {
            var name = RepositoryLayout.ProjectName(projectFile);
            var allowed = AllowedReferences[name].ToHashSet(StringComparer.Ordinal);
            var actual = RepositoryLayout.ProjectReferences(projectFile);

            foreach (var forbidden in actual.Except(allowed, StringComparer.Ordinal))
            {
                violations.Add($"{name} must not reference {forbidden}");
            }

            foreach (var missing in allowed.Except(actual, StringComparer.Ordinal))
            {
                violations.Add($"{name} is missing its required reference to {missing}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Core_references_no_other_project()
    {
        var core = Single("App.Core");

        Assert.Empty(RepositoryLayout.ProjectReferences(core));
    }

    [Fact]
    public void Core_references_no_external_package()
    {
        // Core stays on the base class library, so it cannot acquire a web, Azure, database, AI or
        // SaaS dependency by transitive accident.
        var core = Single("App.Core");

        Assert.Empty(RepositoryLayout.PackageReferences(core));
    }

    [Fact]
    public void Core_source_imports_no_infrastructure_web_azure_or_provider_namespace()
    {
        string[] forbiddenUsings =
        [
            "using Microsoft.AspNetCore",
            "using Microsoft.Extensions",
            "using Microsoft.Azure",
            "using Azure",
            "using Npgsql",
            "using WorkContinuity.Infrastructure",
            "using WorkContinuity.Web",
            "using WorkContinuity.Worker",
            "using WorkContinuity.Connectors",
            "using WorkContinuity.Agent",
            "using WorkContinuity.Simulator",
        ];

        var violations = new List<string>();

        foreach (var file in RepositoryLayout.CoreSourceFiles)
        {
            var text = File.ReadAllText(file.FullName);

            foreach (var forbidden in forbiddenUsings)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                {
                    violations.Add($"{RepositoryLayout.RelativePath(file)} imports '{forbidden}'");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void No_production_project_references_a_test_project()
    {
        var violations = new List<string>();

        foreach (var projectFile in RepositoryLayout.ProductionProjectFiles)
        {
            foreach (var include in RepositoryLayout.RawProjectReferencePaths(projectFile))
            {
                if (include.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                    || include.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("Tests.csproj", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(
                        $"{RepositoryLayout.ProjectName(projectFile)} references test project {include}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Infrastructure_does_not_reference_a_composition_root_or_a_connector()
    {
        var references = RepositoryLayout.ProjectReferences(Single("App.Infrastructure"));

        Assert.DoesNotContain("App.Web", references);
        Assert.DoesNotContain("App.Worker", references);
        Assert.DoesNotContain("App.Connectors.Monday", references);
    }

    [Fact]
    public void The_windows_agent_references_neither_infrastructure_nor_a_saas_connector()
    {
        // The agent is a limited sensor and workspace executor, not a business authority.
        var references = RepositoryLayout.ProjectReferences(Single("App.Agent.Windows"));

        Assert.Equal(["App.Core"], references.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_simulator_is_not_a_production_dependency()
    {
        var dependents = RepositoryLayout.ProductionProjectFiles
            .Where(file => RepositoryLayout.ProjectReferences(file).Contains("App.Simulator"))
            .Select(RepositoryLayout.ProjectName)
            .ToList();

        Assert.Empty(dependents);
    }

    [Fact]
    public void No_production_project_references_an_ai_or_cloud_provider_package_yet()
    {
        string[] forbiddenPackagePrefixes =
        [
            "Azure.",
            "Microsoft.Azure.",
            "Npgsql",
            "OpenAI",
            "Anthropic",
            "Microsoft.SemanticKernel",
            "Microsoft.Extensions.AI",
        ];

        var violations = new List<string>();

        foreach (var projectFile in RepositoryLayout.ProductionProjectFiles)
        {
            foreach (var package in RepositoryLayout.PackageReferences(projectFile))
            {
                foreach (var prefix in forbiddenPackagePrefixes)
                {
                    if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(
                            $"{RepositoryLayout.ProjectName(projectFile)} references {package}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void No_production_source_introduces_ai_or_source_of_truth_logic_yet()
    {
        // Gate 1C is platform plumbing. AI gateways, prompts and source-of-truth resolution belong
        // to later Gates and must not appear early.
        string[] forbiddenFragments =
        [
            "OpenAI",
            "Anthropic",
            "SemanticKernel",
            "ChatCompletion",
            "SourceOfTruthResolver",
            "AiGateway",
        ];

        var violations = new List<string>();
        var productionSources = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var path = file.FullName.Replace('\\', '/');
                return !path.Contains("/bin/", StringComparison.Ordinal)
                    && !path.Contains("/obj/", StringComparison.Ordinal);
            });

        foreach (var file in productionSources)
        {
            var text = File.ReadAllText(file.FullName);

            foreach (var fragment in forbiddenFragments)
            {
                if (text.Contains(fragment, StringComparison.Ordinal))
                {
                    violations.Add($"{RepositoryLayout.RelativePath(file)} mentions '{fragment}'");
                }
            }
        }

        Assert.Empty(violations);
    }

    private static FileInfo Single(string projectName) =>
        RepositoryLayout.ProductionProjectFiles
            .Single(file => RepositoryLayout.ProjectName(file) == projectName);
}
