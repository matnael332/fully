using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Privilege boundaries pinned in the Bicep sources: the continuous-delivery identity can reach only
/// the registry and the two apps; only the jobs identity can read the administrative credential and
/// the runtime-role password; the bootstrap and migration jobs run as that identity with exactly
/// their own secrets; and the readiness probe is parameter-gated with a timeout that covers the check.
/// </summary>
public sealed class InfrastructureBoundaryTests
{
    private const string RoleAssignmentPattern =
        @"resource\s+(\w+)\s+'Microsoft\.Authorization/roleAssignments@[^']+'\s*=\s*\{(.*?)\n\}";

    [Fact]
    public void The_deployment_identity_holds_exactly_three_narrow_role_assignments()
    {
        var text = Bicep("infra/bicep/modules/deploymentIdentity.bicep");
        var assignments = Regex.Matches(text, RoleAssignmentPattern, RegexOptions.Singleline);

        Assert.Equal(3, assignments.Count);

        var scopes = assignments
            .Select(match => Regex.Match(match.Groups[2].Value, @"scope:\s*(\w+)").Groups[1].Value)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["registry", "webApp", "workerApp"], scopes);

        var roles = assignments
            .Select(match => Regex.Match(match.Groups[2].Value, @"roles\.(\w+)").Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(roles.SetEquals(["acrPush", "containerAppsContributor"]), string.Join(",", roles));

        // Owner, Contributor and Managed Identity Operator, by their stable definition ids.
        foreach (var forbidden in new[]
                 {
                     "8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
                     "b24988ac-6180-42a0-ab88-20f7382dd24c",
                     "f1a07417-d97a-45cb-824c-7a7467783830",
                     "Microsoft.App/jobs",
                     "resourceGroup()",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_the_jobs_identity_may_read_the_administrative_and_runtime_role_secrets()
    {
        var text = Bicep("infra/bicep/modules/roleAssignments.bicep");

        var assignments = Regex.Matches(text, RoleAssignmentPattern, RegexOptions.Singleline)
            .Select(match => (
                Name: match.Groups[1].Value,
                Scope: Regex.Match(match.Groups[2].Value, @"scope:\s*(\w+)").Groups[1].Value,
                Principal: Regex.Match(match.Groups[2].Value, @"principalId:\s*(\w+)").Groups[1].Value))
            .ToList();

        Assert.Equal(13, assignments.Count);

        var privileged = assignments
            .Where(assignment => assignment.Scope is "administrativeConnectionSecret" or "runtimeRolePasswordSecret")
            .ToList();
        Assert.Equal(2, privileged.Count);
        Assert.All(privileged, assignment => Assert.Equal("jobsPrincipalId", assignment.Principal));

        // No vault-wide grant exists; every Key Vault grant is scoped to one secret.
        Assert.DoesNotContain(assignments, assignment => assignment.Scope == "vault");

        // The jobs identity reaches nothing but the registry and its two secrets.
        var jobsScopes = assignments
            .Where(assignment => assignment.Principal == "jobsPrincipalId")
            .Select(assignment => assignment.Scope)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["administrativeConnectionSecret", "registry", "runtimeRolePasswordSecret"], jobsScopes);
    }

    [Fact]
    public void The_bootstrap_and_migration_jobs_run_as_the_jobs_identity_with_only_their_own_secrets()
    {
        var text = Bicep("infra/bicep/environment.bicep");
        var bootstrap = ModuleBlock(text, "bootstrapJob");
        var migration = ModuleBlock(text, "migrationJob");
        var web = ModuleBlock(text, "webApp");
        var worker = ModuleBlock(text, "workerApp");

        Assert.Contains("identityId: jobsIdentity.outputs.id", bootstrap, StringComparison.Ordinal);
        Assert.Contains("'--bootstrap-runtime-role'", bootstrap, StringComparison.Ordinal);
        Assert.Contains("triggerType: 'Manual'", bootstrap, StringComparison.Ordinal);
        Assert.Contains("secretNames.administrativeConnectionString", bootstrap, StringComparison.Ordinal);
        Assert.Contains("secretNames.runtimeRolePassword", bootstrap, StringComparison.Ordinal);

        Assert.Contains("identityId: jobsIdentity.outputs.id", migration, StringComparison.Ordinal);
        Assert.Contains("'--migrate-database'", migration, StringComparison.Ordinal);
        Assert.Contains("triggerType: 'Manual'", migration, StringComparison.Ordinal);
        Assert.Contains("secretNames.administrativeConnectionString", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeRolePassword", migration, StringComparison.Ordinal);

        foreach (var app in new[] { web, worker })
        {
            Assert.DoesNotContain("administrativeConnectionString", app, StringComparison.Ordinal);
            Assert.DoesNotContain("runtimeRolePassword", app, StringComparison.Ordinal);
            Assert.DoesNotContain("jobsIdentity", app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_web_readiness_probe_is_parameter_gated_and_its_timeout_covers_the_check()
    {
        var environment = Bicep("infra/bicep/environment.bicep");
        Assert.Contains("param webReadinessProbeEnabled bool", environment, StringComparison.Ordinal);
        Assert.Matches(@"webReadinessPath\s*=\s*\(webUsesPlaceholderImage\s*\|\|\s*!webReadinessProbeEnabled\)", environment);

        var app = Bicep("infra/bicep/modules/containerApp.bicep");
        var readiness = Regex.Match(app, @"type:\s*'Readiness'(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        var timeout = int.Parse(Regex.Match(readiness, @"timeoutSeconds:\s*(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // Two dependencies, up to five seconds each, plus overhead.
        Assert.True(timeout >= 12, $"readiness timeoutSeconds is {timeout}; it must cover both dependency checks");
    }

    private static string Bicep(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ModuleBlock(string text, string name)
    {
        var match = Regex.Match(text, @"module\s+" + name + @"\s+'[^']+'\s*=\s*\{(.*?)\r?\n\}", RegexOptions.Singleline);
        Assert.True(match.Success, $"module '{name}' was not found");
        return match.Groups[1].Value;
    }
}
