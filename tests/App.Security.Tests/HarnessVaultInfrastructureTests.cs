using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G Stage B1, owner decisions B-2, B-3, B-4, B-8 and B-9 of 2026-10-08: the DEV/TEST harness vault's Bicep, read as
/// text. These tests pin the separate entry point and its dedicated resource group, the vault's SKU, authorization,
/// retention, purge protection and closed firewall, its audit trail in a workspace of its own, the custom role's exact
/// three data actions with its resource-group assignable scope, the role's one assignment at the vault, and that nothing
/// of Dev or Synthetic references or deploys any of it. Nothing here compiles a template or reaches Azure.
/// </summary>
public sealed class HarnessVaultInfrastructureTests
{
    internal const string EntryPoint = "infra/bicep/harness-vault.bicep";
    internal const string Module = "infra/bicep/modules/harnessVault.bicep";

    private static readonly string[] RuntimeDataActions =
    [
        "Microsoft.KeyVault/vaults/secrets/getSecret/action",
        "Microsoft.KeyVault/vaults/secrets/setSecret/action",
        "Microsoft.KeyVault/vaults/secrets/delete",
    ];

    [Fact]
    public void It_is_a_separate_subscription_scope_entry_point_that_dev_and_synthetic_never_reference()
    {
        var entry = Bicep(EntryPoint);

        // B-2: one dedicated resource group in North Europe, fixed in the template rather than taken as a parameter.
        Assert.Equal(1, Count(entry, "targetScope = 'subscription'"));
        Assert.Equal(1, Count(entry, "var location = 'northeurope'"));
        Assert.Equal(1, Count(entry, "var resourceGroupName = 'rg-wce-harness-neu'"));
        Assert.Equal(1, Regex.Count(entry, @"'Microsoft\.Resources/resourceGroups@"));
        Assert.Equal(1, Count(entry, "module harnessVault 'modules/harnessVault.bicep' = {\n  scope: harnessResourceGroup\n"));

        // Its only parameters are the principal and the admitted address, both empty by default.
        Assert.Equal(
            ["allowedIpv4Address string = ''", "runtimePrincipalObjectId string = ''"],
            Regex.Matches(entry, @"^param (.+)$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Contains("@maxLength(36)\nparam runtimePrincipalObjectId string = ''", entry, StringComparison.Ordinal);
        Assert.Contains("@maxLength(15)\nparam allowedIpv4Address string = ''", entry, StringComparison.Ordinal);

        // Nothing of Dev or Synthetic names the harness, and only the entry point names its module.
        var others = BicepFiles().Where(path => path != EntryPoint && path != Module).ToList();
        Assert.Contains("infra/bicep/main.bicep", others);
        Assert.Contains("infra/bicep/parameters/main.dev.bicepparam", others);
        Assert.Contains("infra/bicep/parameters/main.synthetic.bicepparam", others);
        foreach (var path in others)
        {
            Assert.DoesNotContain("harness", Bicep(path), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal([EntryPoint], BicepFiles().Where(path => Bicep(path).Contains("harnessVault.bicep", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_runtime_role_holds_exactly_the_three_secret_data_actions_and_nothing_else()
    {
        // Exactly one custom role exists in the repository's Bicep, and it is the harness's.
        Assert.Equal([EntryPoint], BicepFiles().Where(path => Bicep(path).Contains("Microsoft.Authorization/roleDefinitions@", StringComparison.Ordinal)));
        var role = Block(Bicep(EntryPoint), "runtimeRole", "Microsoft.Authorization/roleDefinitions");

        // B-8: exactly the three data actions; no control-plane action, exclusion, wildcard or condition.
        var permissions = Regex.Matches(role, @"^ *permissions: \[", RegexOptions.Multiline);
        Assert.Single(permissions);
        Assert.Equal(1, Count(role, "        actions: []\n"));
        Assert.Equal(1, Count(role, "        notActions: []\n"));
        Assert.Equal(1, Count(role, "        notDataActions: []\n"));
        var dataActions = Regex.Match(role, @"dataActions: \[\n(.*?)\n\s*\]", RegexOptions.Singleline);
        Assert.True(dataActions.Success);
        Assert.Equal(RuntimeDataActions, Regex.Matches(dataActions.Groups[1].Value, @"'([^']*)'").Select(match => match.Groups[1].Value));
        Assert.Equal(RuntimeDataActions.Length, Regex.Count(role, @"'Microsoft\.[^']*'"));
        Assert.DoesNotContain("*", role, StringComparison.Ordinal);
        Assert.DoesNotContain("condition", role, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("    type: 'CustomRole'\n", role, StringComparison.Ordinal);

        // Assignable inside the dedicated resource group only.
        Assert.Contains("    assignableScopes: [\n      harnessResourceGroup.id\n    ]\n", role, StringComparison.Ordinal);
        Assert.Equal(1, Count(role, "assignableScopes"));
    }

    [Fact]
    public void The_runtime_role_is_assigned_once_at_the_vault_to_the_principal_parameter_only()
    {
        // Exactly one role assignment in the harness's Bicep, in the module, at the vault.
        var entry = Bicep(EntryPoint);
        var module = Bicep(Module);
        Assert.DoesNotContain("Microsoft.Authorization/roleAssignments@", entry, StringComparison.Ordinal);
        Assert.Equal(1, Count(module, "Microsoft.Authorization/roleAssignments@"));
        Assert.Equal(
            1,
            Count(module, "resource runtimeAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(runtimePrincipalObjectId)) {\n  scope: vault\n"));

        var assignment = Block(module, "runtimeAssignment", "Microsoft.Authorization/roleAssignments");
        Assert.Contains("    roleDefinitionId: runtimeRoleDefinitionId\n", assignment, StringComparison.Ordinal);
        Assert.Contains("    principalId: runtimePrincipalObjectId\n", assignment, StringComparison.Ordinal);
        Assert.Contains("    principalType: 'ServicePrincipal'\n", assignment, StringComparison.Ordinal);
        Assert.Equal(1, Count(assignment, "scope:"));
        Assert.DoesNotContain("condition", assignment, StringComparison.OrdinalIgnoreCase);

        // The entry point hands the module exactly its own role and the principal parameter.
        var call = Regex.Match(entry, @"module harnessVault '[^']+' = \{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("    runtimeRoleDefinitionId: runtimeRole.id\n", call, StringComparison.Ordinal);
        Assert.Contains("    runtimePrincipalObjectId: runtimePrincipalObjectId\n", call, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vault_is_standard_rbac_with_seven_day_retention_no_purge_protection_and_a_closed_firewall()
    {
        var entry = Bicep(EntryPoint);
        var module = Bicep(Module);
        Assert.Equal(1, Count(module, "Microsoft.KeyVault/vaults@"));
        var vault = Block(module, "vault", "Microsoft.KeyVault/vaults");

        // B-2: Standard SKU, Azure RBAC, no access policy.
        Assert.Contains("      family: 'A'\n      name: 'standard'\n", vault, StringComparison.Ordinal);
        Assert.Contains("    enableRbacAuthorization: true\n", vault, StringComparison.Ordinal);
        foreach (var text in new[] { entry, module })
        {
            Assert.DoesNotContain("accessPolicies", text, StringComparison.Ordinal);
            Assert.DoesNotContain("createMode", text, StringComparison.Ordinal);
        }

        // B-3: seven days, a constant of the template.
        Assert.Contains("    enableSoftDelete: true\n    softDeleteRetentionInDays: softDeleteRetentionInDays\n", vault, StringComparison.Ordinal);
        Assert.Equal(1, Count(module, "var softDeleteRetentionInDays = 7\n"));
        Assert.DoesNotContain("param softDeleteRetentionInDays", module, StringComparison.Ordinal);

        // B-4: purge protection is off because the property is left out; Azure accepts no false for it.
        Assert.DoesNotMatch(new Regex("enablePurgeProtection", RegexOptions.IgnoreCase), entry + module);

        // B-2: the firewall denies by default, with no trusted-service bypass and at most the one admitted address.
        Assert.Contains("    publicNetworkAccess: 'Enabled'\n", vault, StringComparison.Ordinal);
        Assert.Contains("      bypass: 'None'\n      defaultAction: 'Deny'\n", vault, StringComparison.Ordinal);
        Assert.Contains("      ipRules: empty(allowedIpv4Address) ? [] : [\n        {\n          value: allowedIpv4Address\n        }\n      ]\n", vault, StringComparison.Ordinal);
        Assert.Contains("      virtualNetworkRules: []\n", vault, StringComparison.Ordinal);
        foreach (var flag in new[] { "enabledForDeployment", "enabledForDiskEncryption", "enabledForTemplateDeployment" })
        {
            Assert.Contains("    " + flag + ": false\n", vault, StringComparison.Ordinal);
        }

        // No secret, and no existing resource of another environment, is touched by the harness's Bicep.
        foreach (var text in new[] { entry, module })
        {
            Assert.DoesNotContain("'Microsoft.KeyVault/vaults/secrets@", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\bexisting\b"), text);
            Assert.DoesNotMatch(new Regex(@"(rg|kv|log|appi|psql|sb|cae)-wce-(dev|synthetic)"), text);
        }
    }

    [Fact]
    public void The_vault_audit_events_go_only_to_the_dedicated_workspace_in_its_resource_group()
    {
        var module = Bicep(Module);
        Assert.Equal(1, Count(module, "Microsoft.Insights/diagnosticSettings@"));
        var audit = Block(module, "auditEvents", "Microsoft.Insights/diagnosticSettings");
        Assert.Contains("  scope: vault\n", audit, StringComparison.Ordinal);
        Assert.Contains("    workspaceId: auditWorkspace.outputs.id\n", audit, StringComparison.Ordinal);
        Assert.Contains("        category: 'AuditEvent'\n        enabled: true\n", audit, StringComparison.Ordinal);
        Assert.Equal(1, Count(audit, "category:"));

        // B-2: a workspace of its own, deployed into the module's own resource group by the existing workspace module.
        var workspace = Regex.Match(module, @"module auditWorkspace 'logAnalytics\.bicep' = \{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(workspace.Success);
        Assert.DoesNotContain("scope:", workspace.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("  auditWorkspace: 'log-wce-harness-neu'\n", module, StringComparison.Ordinal);
        Assert.Contains("  keyVault: 'kv-wce-harness-${uniqueToken}'\n", module, StringComparison.Ordinal);
    }

    [Fact]
    public void No_template_uses_a_bicep_extension_or_reaches_microsoft_graph()
    {
        // B-9: the Microsoft Graph Bicep extension is not approved; Entra objects belong to the owner-run identity script.
        foreach (var path in BicepFiles())
        {
            var text = Bicep(path);
            Assert.DoesNotMatch(new Regex(@"^\s*(extension|import|provider)\s", RegexOptions.Multiline), text);
            Assert.DoesNotContain("Microsoft.Graph", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            Directory.EnumerateFiles(RepositoryLayout.Root.FullName, "bicepconfig.json", SearchOption.AllDirectories),
            path => !path.Replace('\\', '/').Contains("/.git/", StringComparison.Ordinal));
    }

    [Fact]
    public void Infra_ps1_builds_and_lints_the_harness_entry_point_and_never_deploys_it()
    {
        var infra = HarnessScriptText.Code("eng/infra.ps1");
        Assert.Equal(1, Count(infra, "$HarnessTemplate = Join-Path $BicepRoot \"harness-vault.bicep\""));
        Assert.Equal(2, Count(infra, "foreach ($template in @($MainTemplate, $HarnessTemplate)) {"));
        Assert.Equal(3, Count(infra, "$HarnessTemplate"));

        // Validate, what-if and deploy still take main.bicep and nothing else.
        Assert.Equal(3, Count(infra, "--template-file $MainTemplate `"));
        Assert.Equal(3, Count(infra, "--template-file"));

        // Only the two owner-run deployment paths ever invoke az deployment.
        var deployers = HarnessScriptText.RepositoryFiles(".ps1", ".psm1", ".sh", ".cmd", ".bat", ".py", ".yml", ".yaml")
            .Where(path => HarnessScriptText.Read(path).Contains("\"deployment\"", StringComparison.Ordinal)
                || HarnessScriptText.Read(path).Contains("az deployment", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        Assert.Equal(["eng/harness-vault.ps1", "eng/infra.ps1"], deployers);
    }

    private static IEnumerable<string> BicepFiles() => HarnessScriptText.RepositoryFiles(".bicep", ".bicepparam");

    private static string Bicep(string relativePath) => HarnessScriptText.Read(relativePath);

    private static string Block(string text, string name, string type)
    {
        var match = Regex.Match(text, @"resource " + name + @" '" + Regex.Escape(type) + @"@[^']+' = (?:if \([^\n]*\) )?\{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(match.Success, $"resource '{name}' of type {type} was not found");
        return match.Groups[1].Value;
    }

    private static int Count(string text, string fragment) => Regex.Count(text, Regex.Escape(fragment));
}

/// <summary>Reading the repository's scripts and templates as text, for the Stage B1 tests.</summary>
internal static class HarnessScriptText
{
    /// <summary>A repository file's text with LF line breaks.</summary>
    internal static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>A PowerShell script without its comment lines.</summary>
    internal static string Code(string relativePath) =>
        string.Join("\n", Read(relativePath).Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>The repository's files with the given extensions, outside git, local tooling and build output.</summary>
    internal static IEnumerable<string> RepositoryFiles(params string[] extensions) =>
        Directory.EnumerateFiles(RepositoryLayout.Root.FullName, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RepositoryLayout.Root.FullName, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith(".git/", StringComparison.Ordinal)
                && !path.StartsWith(".claude/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/TestResults/", StringComparison.Ordinal))
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
}
