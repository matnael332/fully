using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G Stage B1, owner decisions B-2, B-8 and B-9 of 2026-10-08: <c>eng/harness-vault.ps1</c>, the owner-run path that
/// alone previews or deploys the DEV/TEST harness vault's entry point, read as text. <see cref="HarnessVaultDeployScriptRunTests"/>
/// runs it. It is inert without -Approve, its target is always explicit, and it deploys nothing but the harness entry point.
/// </summary>
public sealed class HarnessVaultDeployScriptTests
{
    internal const string Script = "eng/harness-vault.ps1";

    [Fact]
    public void It_previews_or_deploys_only_the_harness_entry_point()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Equal(1, Count(code, "[ValidateSet(\"whatif\", \"deploy\")]"));
        Assert.Equal(1, Count(code, "$Template = Join-Path $Root \"infra/bicep/harness-vault.bicep\""));
        Assert.Equal(1, Count(code, "$Location = \"northeurope\""));
        Assert.Equal(2, Count(code, "\"--template-file\", $Template,"));
        Assert.Equal(1, Count(code, "\"deployment\", \"sub\", \"what-if\","));
        Assert.Equal(1, Count(code, "\"deployment\", \"sub\", \"create\","));
        Assert.DoesNotMatch(new Regex(@"main\.bicep|\.bicepparam|environment\.bicep|--mode|deployment group|""group""|""tenant""|""mg""|""delete""|""validate"""), code);

        // The deployment asks before anything changes, and its parameters are exactly the principal and the address.
        var create = code.IndexOf("\"deployment\", \"sub\", \"create\",", StringComparison.Ordinal);
        Assert.True(code.IndexOf("\"--confirm-with-what-if\"", StringComparison.Ordinal) > create);
        Assert.Equal(1, Count(code, "\"--confirm-with-what-if\""));
        Assert.Equal(
            ["allowedIpv4Address", "runtimePrincipalObjectId"],
            Regex.Matches(code, @"\$parameters \+= ""(\w+)=").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_reaches_azure_before_every_value_check_and_the_approval()
    {
        var code = HarnessScriptText.Code(Script);
        var calls = Regex.Matches(code, @"& az @(arguments|Arguments)").Select(match => match.Index).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, Count(code, "& az"));
        var firstUse = code.IndexOf("Invoke-AzureCli @(", StringComparison.Ordinal);
        Assert.True(firstUse > 0);

        // Every value, the template, the plan and the approval come before the first use of the Azure CLI.
        var approval = code.IndexOf("if (-not $Approve) {", StringComparison.Ordinal);
        Assert.True(approval > 0 && approval < firstUse);
        foreach (var step in new[]
                 {
                     "Assert-Guid \"SubscriptionId\" $SubscriptionId", "Assert-Guid \"RuntimePrincipalObjectId\" $RuntimePrincipalObjectId",
                     "-cnotmatch $Ipv4Pattern", "Test-Path -LiteralPath $Template", "Write-Host \"HARNESS VAULT PLAN ($Command)\"",
                 })
        {
            var at = code.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > 0 && at < approval, step);
        }

        Assert.Contains(
            "if (-not $Approve) {\n    Stop-Run \"REFUSED: $Command contacts Azure and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent to Azure.\"\n}",
            code,
            StringComparison.Ordinal);

        // The subscription and the signed-in user are confirmed before the deployment call.
        var deployment = code.IndexOf("& az @arguments", StringComparison.Ordinal);
        foreach (var step in new[] { "$account.id -cne $SubscriptionId", "$account.user.type -cne \"user\"" })
        {
            var at = code.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > firstUse && at < deployment, step);
        }

        // Every refusal ends the run the same way: one printed line, then the one throw.
        Assert.Equal(1, Regex.Count(code, @"\bthrow\b"));
        Assert.Contains("    Write-Host $Message -ForegroundColor Red\n    throw $Message\n", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_target_is_explicit_and_the_signed_in_session_is_never_changed()
    {
        var code = HarnessScriptText.Code(Script);
        var parameters = code[..code.IndexOf("\n)\n", StringComparison.Ordinal)];
        Assert.Contains("    [Parameter(Mandatory = $true)]\n    [string]$SubscriptionId,\n", parameters, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\$SubscriptionId\s*="), code);

        // The subscription is named on every call: the session check and the deployment.
        Assert.Equal(3, Count(code, "\"--subscription\", $SubscriptionId"));
        Assert.DoesNotMatch(
            new Regex(@"""login""|""logout""|account set|""set""|config set|SetEnvironmentVariable|\$env:|Connect-Az|Set-AzContext", RegexOptions.IgnoreCase),
            code);
    }

    [Fact]
    public void It_deletes_nothing_writes_no_file_and_starts_no_other_program()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.DoesNotMatch(
            new Regex(
                @"Remove-Item|Set-Content|Add-Content|Out-File|New-Item|Copy-Item|Move-Item|Rename-Item|Clear-Content|WriteAll|\]::(Write|Append|Delete|Copy|Move)\w*\(|Set-Location|Push-Location|Invoke-Expression|\biex\b|Start-Process|Invoke-Command|Start-Job|Add-Type|Import-Module|Invoke-RestMethod|Invoke-WebRequest",
                RegexOptions.IgnoreCase),
            code);
        Assert.DoesNotMatch(new Regex(@"\.(ps1|psm1|psd1|py|sh|cmd|bat|exe)\b"), code);
    }

    [Fact]
    public void Nothing_in_the_repository_runs_the_owner_scripts()
    {
        // Only their own tests name the two Stage B1 owner scripts: no workflow, compose file, template or other script.
        foreach (var script in new[] { "harness-vault.ps1", "harness-identity.ps1" })
        {
            var holders = HarnessScriptText.RepositoryFiles(".cs", ".ps1", ".psm1", ".sh", ".cmd", ".bat", ".yml", ".yaml", ".json", ".py", ".bicep", ".bicepparam", ".sql", ".csproj", ".props", ".targets", ".toml", "")
                .Where(path => !path.EndsWith("/" + script, StringComparison.Ordinal))
                .Where(path => HarnessScriptText.Read(path).Contains(script, StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(holders);
            Assert.All(holders, path => Assert.StartsWith("tests/App.Security.Tests/Harness", path, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_owner_scripts_are_plain_ascii_with_lf_line_breaks()
    {
        // Windows PowerShell reads a file without a byte order mark in the system code page.
        foreach (var script in new[] { Script, HarnessIdentityScriptTests.Script })
        {
            var bytes = File.ReadAllBytes(Path.Combine(RepositoryLayout.Root.FullName, script));
            Assert.DoesNotContain(bytes, value => value > 127);
            Assert.DoesNotContain(bytes, value => value == '\r');
        }
    }

    private static int Count(string text, string fragment) => Regex.Count(text, Regex.Escape(fragment));
}

/// <summary>
/// <c>eng/harness-vault.ps1</c> run by PowerShell in a sandbox (<see cref="HarnessAzureCliSandbox"/>): every refusal comes
/// before the stand-in az is ever called, and an approved run calls it exactly as reviewed. No Azure endpoint is reached.
/// </summary>
public sealed class HarnessVaultDeployScriptRunTests
{
    private const string Subscription = "0199b001-0000-7000-8000-00000000b001";
    private const string Tenant = "0199b001-0000-7000-8000-00000000b002";
    private const string Principal = "0199b001-0000-7000-8000-00000000b003";
    private const string PublicAddress = "8.8.8.8";

    private const string NotApproved = "REFUSED: {0} contacts Azure and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent to Azure.";
    private const string NotAnAddress = "REFUSED: -AllowedIpv4Address must be one public IPv4 address in dotted-decimal form, without a prefix length. Nothing was sent to Azure.";

    /// <summary>Addresses the firewall must never admit, and malformed ones.</summary>
    public static TheoryData<string> RefusedAddresses => new()
    {
        "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "127.0.0.1", "169.254.1.1", "100.64.0.1", "0.1.2.3", "224.0.0.1",
        "255.255.255.255", "8.8.8.8/32", "8.8.8", "8.8.8.256", "08.8.8.8", " 8.8.8.8", "2001:db8::1", string.Empty,
    };

    [Theory]
    [InlineData("whatif")]
    [InlineData("deploy")]
    public async Task Without_approve_the_plan_is_shown_and_nothing_is_sent(string command)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, command, "-SubscriptionId", Subscription, "-RuntimePrincipalObjectId", Principal, "-AllowedIpv4Address", PublicAddress);

        AssertRefused(run, string.Format(System.Globalization.CultureInfo.InvariantCulture, NotApproved, command));
        Assert.Contains("  subscription      : " + Subscription, run.Lines);
        Assert.Contains("  resource group    : rg-wce-harness-neu (northeurope)", run.Lines);
        Assert.Contains("  runtime principal : " + Principal, run.Lines);
        Assert.Contains("  firewall          : default Deny, no trusted-service bypass; admitted address: " + PublicAddress, run.Lines);
    }

    [Theory]
    [InlineData("SubscriptionId", "0199B001-0000-7000-8000-00000000B001")]
    [InlineData("SubscriptionId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("SubscriptionId", "subscription-1")]
    [InlineData("RuntimePrincipalObjectId", "0199B001-0000-7000-8000-00000000B003")]
    [InlineData("RuntimePrincipalObjectId", "")]
    public async Task An_invalid_identifier_is_refused_before_anything_runs(string parameter, string value)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        var arguments = new List<string> { "deploy", "-SubscriptionId", parameter == "SubscriptionId" ? value : Subscription };
        if (parameter == "RuntimePrincipalObjectId")
        {
            arguments.Add("-RuntimePrincipalObjectId");
            arguments.Add(value);
        }

        arguments.Add("-Approve");
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, [.. arguments]);
        AssertRefused(run, "REFUSED: -" + parameter + " must be a lower-case GUID other than the nil GUID. Nothing was sent to Azure.");
    }

    [Theory]
    [MemberData(nameof(RefusedAddresses))]
    public async Task Only_one_public_ipv4_address_is_ever_admitted(string address)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, "deploy", "-SubscriptionId", Subscription, "-AllowedIpv4Address", address, "-Approve");
        AssertRefused(run, NotAnAddress);
    }

    [Fact]
    public async Task Without_the_template_nothing_is_sent()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script);
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, "whatif", "-SubscriptionId", Subscription, "-Approve");
        AssertRefused(run, "REFUSED: infra/bicep/harness-vault.bicep was not found. Nothing was sent to Azure.");
    }

    [Theory]
    [InlineData("not-signed-in")]
    [InlineData("another-subscription")]
    [InlineData("service-principal")]
    public async Task The_session_must_be_a_signed_in_user_with_the_subscription(string session)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        var expected = "REFUSED: the Azure CLI is not signed in, or subscription " + Subscription + " is not available to it. Sign in yourself; this script never signs in. Nothing was sent to Azure.";
        switch (session)
        {
            case "not-signed-in":
                sandbox.Answer(1, string.Empty, exitCode: 1);
                break;
            case "another-subscription":
                sandbox.Answer(1, Account("0199b001-0000-7000-8000-00000000beef", "user"));
                break;
            default:
                sandbox.Answer(1, Account(Subscription, "servicePrincipal"));
                expected = "REFUSED: the Azure CLI is not signed in as a user. This script runs only as the product owner's own account. Nothing was sent to Azure.";
                break;
        }

        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, "deploy", "-SubscriptionId", Subscription, "-Approve");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(expected, run.Lines);
        var only = Assert.Single(run.Invocations);
        Assert.Equal(["account", "show", "--subscription", Subscription, "--output", "json"], HarnessAzureCliSandbox.Tokens(only));
        Assert.DoesNotContain("owner@example.invalid", run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_what_if_previews_exactly_the_harness_entry_point()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        sandbox.Answer(1, Account(Subscription, "user"));
        sandbox.Answer(2, "the preview");
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, "whatif", "-SubscriptionId", Subscription, "-Approve");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, run.Invocations.Count);
        var tokens = HarnessAzureCliSandbox.Tokens(run.Invocations[1]);
        Assert.Equal(["deployment", "sub", "what-if", "--name", "wce-harness-whatif", "--location", "northeurope", "--template-file"], tokens[..8]);
        Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Root, HarnessVaultInfrastructureTests.EntryPoint)), Path.GetFullPath(tokens[8]));
        Assert.Equal(["--subscription", Subscription], tokens[9..]);
        Assert.Contains("WHATIF: COMPLETE (preview only; nothing was deployed)", run.Lines);
        Assert.Contains("  target            : Sandbox subscription (" + Subscription + "), tenant " + Tenant, run.Lines);
        Assert.DoesNotContain("owner@example.invalid", run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_deployment_asks_against_its_what_if_and_passes_exactly_the_two_parameters()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        sandbox.Answer(1, Account(Subscription, "user"));
        sandbox.Answer(2, "deployed");
        var run = await sandbox.RunAsync(
            HarnessVaultDeployScriptTests.Script, "deploy", "-SubscriptionId", Subscription, "-RuntimePrincipalObjectId", Principal, "-AllowedIpv4Address", PublicAddress, "-Approve");

        Assert.Equal(0, run.ExitCode);
        var tokens = HarnessAzureCliSandbox.Tokens(run.Invocations[1]);
        Assert.Equal(["deployment", "sub", "create", "--name"], tokens[..4]);
        Assert.Matches(@"\Awce-harness-[0-9]{8}-[0-9]{6}\z", tokens[4]);
        Assert.Equal(["--location", "northeurope", "--template-file"], tokens[5..8]);
        Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Root, HarnessVaultInfrastructureTests.EntryPoint)), Path.GetFullPath(tokens[8]));
        Assert.Equal(
            ["--subscription", Subscription, "--confirm-with-what-if", "--parameters", "runtimePrincipalObjectId=" + Principal, "allowedIpv4Address=" + PublicAddress],
            tokens[9..]);
        Assert.Contains("DEPLOY: COMPLETE (rg-wce-harness-neu)", run.Lines);
    }

    [Fact]
    public async Task A_failed_or_declined_deployment_is_reported_as_not_completed()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessVaultDeployScriptTests.Script, HarnessVaultInfrastructureTests.EntryPoint);
        sandbox.Answer(1, Account(Subscription, "user"));
        sandbox.Answer(2, string.Empty, exitCode: 1);
        var run = await sandbox.RunAsync(HarnessVaultDeployScriptTests.Script, "deploy", "-SubscriptionId", Subscription, "-Approve");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(2, run.Invocations.Count);
        Assert.Contains("DEPLOY: NOT COMPLETED (Azure CLI exit 1): the deployment failed or was declined.", run.Lines);
        Assert.DoesNotContain(run.Lines, line => line.StartsWith("DEPLOY: COMPLETE", StringComparison.Ordinal));
        Assert.Equal(["--subscription", Subscription, "--confirm-with-what-if"], HarnessAzureCliSandbox.Tokens(run.Invocations[1])[9..]);
    }

    private static string Account(string subscription, string userType) =>
        "{\"id\":\"" + subscription + "\",\"tenantId\":\"" + Tenant + "\",\"name\":\"Sandbox subscription\",\"user\":{\"name\":\"owner@example.invalid\",\"type\":\"" + userType + "\"}}";

    private static void AssertRefused(HarnessScriptRun run, string reason)
    {
        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Invocations);
        Assert.Contains(reason, run.Lines);
    }
}
