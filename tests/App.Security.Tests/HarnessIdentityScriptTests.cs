using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G Stage B1, owner decision B-9 of 2026-10-08 (for the runtime identity of B-7): <c>eng/harness-identity.ps1</c>,
/// the one reviewed, idempotent script that creates the harness's Entra application, its service principal and its
/// certificate credential, read as text. <see cref="HarnessIdentityScriptRunTests"/> runs it, but never past a point that
/// would create a certificate: no certificate store is ever opened by these tests.
/// </summary>
public sealed class HarnessIdentityScriptTests
{
    internal const string Script = "eng/harness-identity.ps1";

    private static readonly string[] Checks =
    [
        "application.display_name", "application.single_tenant", "application.no_api_permission", "application.no_redirect_uri",
        "application.no_app_role", "application.no_exposed_api", "application.no_client_secret", "service_principal.of_the_application",
        "service_principal.no_own_credential", "service_principal.no_group_or_directory_role", "service_principal.owns_nothing",
        "service_principal.no_application_permission", "service_principal.no_delegated_permission",
    ];

    [Fact]
    public void It_reaches_entra_only_through_the_azure_cli_and_microsoft_graph_v1()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Equal(1, Count(code, "& az @Arguments"));
        Assert.Equal(1, Count(code, "& az"));
        Assert.Equal(1, Count(code, "$Graph = \"https://graph.microsoft.com/v1.0\""));
        Assert.Equal(["https://graph.microsoft.com/v1.0"], Regex.Matches(code, @"https?://[^\s""']+").Select(match => match.Value).Distinct(StringComparer.Ordinal));

        // The only Azure CLI commands: the session read and az rest, with the four Graph methods it needs and no other.
        Assert.Equal(1, Count(code, "Invoke-AzureCli @(\"account\", \"show\", \"--output\", \"json\")"));
        Assert.Equal(2, Count(code, "@(\"rest\", \"--method\", "));
        Assert.Equal(["patch", "post"], Regex.Matches(code, @"Send-Graph ""(\w+)""").Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.DoesNotMatch(
            new Regex(
                @"""login""|""logout""|account set|""ad""|credential reset|addPassword|addKey|Connect-MgGraph|Microsoft\.Graph|Import-Module|Install-Module|Invoke-RestMethod|Invoke-WebRequest|WebClient|HttpClient|Invoke-Expression|\biex\b|Start-Process|Invoke-Command|Start-Job|Add-Type|SetEnvironmentVariable|\$env:",
                RegexOptions.IgnoreCase),
            code);
    }

    [Fact]
    public void Nothing_reaches_entra_before_every_value_check_the_approval_and_the_session_check()
    {
        var code = HarnessScriptText.Code(Script);
        var main = code.IndexOf("\nif (-not $TenantId -or $TenantId -cnotmatch $GuidPattern", StringComparison.Ordinal);
        Assert.True(main > 0);
        var firstCall = code.IndexOf("Invoke-AzureCli @(", main, StringComparison.Ordinal);
        var approval = code.IndexOf("if (-not $Approve) {", main, StringComparison.Ordinal);
        var platform = code.IndexOf("-and -not $OnWindows) {", main, StringComparison.Ordinal);
        var execution = code.IndexOf("\nswitch ($Command) {\n    \"create\" {\n        $application = Find-Application", StringComparison.Ordinal);
        Assert.True(main < approval && approval < platform && platform < firstCall && firstCall < execution);

        // The validity window is checked before the plan; the tenant and the user before anything is read or written.
        foreach (var step in new[] { "$ValidityHours -gt $MaximumValidityHours", "\"REFUSED: -ValidityHours belongs to add-credential only. Nothing was sent.\"" })
        {
            var at = code.IndexOf(step, main, StringComparison.Ordinal);
            Assert.True(at > main && at < approval, step);
        }

        foreach (var step in new[] { "$session.Json.tenantId -cne $TenantId", "$session.Json.user.type -cne \"user\"" })
        {
            var at = code.IndexOf(step, main, StringComparison.Ordinal);
            Assert.True(at > firstCall && at < execution, step);
        }

        Assert.Contains(
            "if (-not $Approve) {\n    Stop-Run \"REFUSED: $Command contacts Microsoft Entra ID and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent.\"\n}",
            code,
            StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(code, @"\bthrow\b"));
        Assert.Contains("    Write-Host $Message -ForegroundColor Red\n    throw $Message\n", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_is_created_single_tenant_with_a_name_only_and_the_principal_with_its_application_only()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Equal(1, Count(code, "$SignInAudience = \"AzureADMyOrg\""));
        Assert.Equal(1, Count(code, "$DisplayName = \"wce-dev-harness-keyvault-runtime\""));

        // Exactly four writes: the application, its service principal, and the application's key credentials, set or emptied.
        var writes = Regex.Matches(code, @"Send-Graph ""(\w+)"" ""([^""]+)"" \((.*?)\)\n", RegexOptions.Singleline).Select(match => match.Value).ToList();
        Assert.Equal(4, writes.Count);
        Assert.Equal(
            1,
            Count(code, "Send-Graph \"post\" \"applications\" ([ordered]@{ displayName = $DisplayName; signInAudience = $SignInAudience; notes = $Notes })"));
        Assert.Equal(1, Count(code, "Send-Graph \"post\" \"servicePrincipals\" ([ordered]@{ appId = $application.appId })"));
        Assert.Equal(2, Count(code, "Send-Graph \"patch\" \"applications/$("));
        Assert.Equal(1, Count(code, "([ordered]@{ keyCredentials = @() })"));
        Assert.Equal(1, Count(code, "keyCredentials = @([ordered]@{ type = \"AsymmetricX509Cert\"; usage = \"Verify\"; key = $publicCertificate; displayName = $credentialName })"));

        // No write ever carries a permission, a redirect address, a role, an exposed API, an owner or a secret.
        foreach (var write in writes)
        {
            Assert.DoesNotMatch(
                new Regex(@"requiredResourceAccess|redirectUris|appRoles|identifierUris|oauth2|passwordCredentials|owners|memberOf|web|spa|publicClient|api\b", RegexOptions.IgnoreCase),
                write);
        }
    }

    [Fact]
    public void Every_rule_of_b9_is_read_back_and_checked_after_every_write()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Equal(Checks, Regex.Matches(code, @"""((?:application|service_principal)\.[a-z_]+)"" = ").Select(match => match.Groups[1].Value));

        // The service principal's relationships are read through the transitive membership, ownership and grants.
        foreach (var relationship in new[] { "transitiveMemberOf", "ownedObjects", "appRoleAssignments", "oauth2PermissionGrants" })
        {
            Assert.Equal(1, Count(code, "\"servicePrincipals/$($principal.id)/" + relationship + "\""));
        }

        // Every command that writes reads back and checks before it reports completion.
        foreach (var (label, rule) in new[] { ("CREATE", "at-most-one"), ("ADD-CREDENTIAL", "exactly-expected") })
        {
            var completion = code.IndexOf("\"" + label + ": COMPLETE (verified by read-back)\"", StringComparison.Ordinal);
            var check = code.LastIndexOf("Write-Checks (Test-State $state \"" + rule + "\"", completion, StringComparison.Ordinal);
            var read = code.LastIndexOf("$state = Read-State", check, StringComparison.Ordinal);
            var write = code.LastIndexOf("Send-Graph ", read, StringComparison.Ordinal);
            Assert.True(write > 0 && write < read && read < check && check < completion, label);
        }

        var removal = code.IndexOf("Send-Graph \"patch\" \"applications/$($application.id)\" ([ordered]@{ keyCredentials = @() })", StringComparison.Ordinal);
        var reread = code.IndexOf("if ((Get-Count $application.keyCredentials) -ne 0) {", removal, StringComparison.Ordinal);
        Assert.True(removal > 0 && reread > removal);
    }

    [Fact]
    public void The_private_key_is_created_non_exportable_in_the_current_user_store_and_never_exported()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Equal(1, Count(code, "$CertificateStore = \"Cert:\\CurrentUser\\My\""));
        Assert.Equal(1, Count(code, "$KeyLength = 3072"));
        Assert.Equal(1, Count(code, "$MaximumValidityHours = 24"));

        // One certificate, created with exactly these parameters and nothing else.
        Assert.Equal(1, Count(code, "New-SelfSignedCertificate"));
        var creation = Regex.Match(code, @"New-SelfSignedCertificate ((?:[^\n`]*`\n)*[^\n]*)\n");
        Assert.True(creation.Success);
        Assert.Equal(
            [
                "-Type Custom", "-Subject $CertificateSubject", "-CertStoreLocation $CertificateStore", "-KeyAlgorithm RSA", "-KeyLength $KeyLength",
                "-HashAlgorithm SHA256", "-KeyExportPolicy NonExportable", "-Provider \"Microsoft Software Key Storage Provider\"",
                "-KeyUsage DigitalSignature", "-KeyUsageProperty Sign", "-NotBefore $now.AddMinutes(-5)", "-NotAfter $now.AddHours($ValidityHours)",
            ],
            Regex.Matches(creation.Groups[1].Value, @"-\w+ (?:""[^""]*""|\S+)").Select(match => match.Value));

        // Only the public certificate ever leaves it, and the key is checked non-exportable before anything is sent.
        Assert.Equal(1, Count(code, ".Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)"));
        Assert.Equal(1, Count(code, "$key.Key.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None"));
        var verified = code.IndexOf("-and (Test-NonExportableKey $certificate)", StringComparison.Ordinal);
        var deleted = code.IndexOf("if (-not $keyVerified) {\n            Remove-LocalCertificate $certificate.Thumbprint\n            Stop-Run ", StringComparison.Ordinal);
        var sent = code.IndexOf(".Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)", StringComparison.Ordinal);
        Assert.True(verified > 0 && verified < deleted && deleted < sent);
        Assert.Equal(1, Count(code, "$keyVerified = $false\n        try {\n"));
        Assert.DoesNotMatch(
            new Regex(
                @"Pfx|Pkcs12|Export-Certificate|ExportParameters|ExportPkcs8|ExportRSAPrivateKey|ExportEncrypted|ExportCspBlob|ExportSubjectPublicKeyInfo|\.PrivateKey\b|LocalMachine|\\Root\b|TrustedPeople|\\CA\b|dev-certs|certutil|Import-Certificate",
                RegexOptions.IgnoreCase),
            code);

        // The export policies that would let the key leave, as PowerShell names them; NonExportable is the one allowed.
        Assert.DoesNotMatch(new Regex(@"\b(Exportable|ExportableEncrypted|AllowExport|AllowPlaintextExport)\b"), code);

        // Deletion: the request-body file, and one certificate by exact thumbprint from the current user's store.
        Assert.Equal(
            ["Remove-Item -LiteralPath $file -Force", "Remove-Item -LiteralPath (Join-Path $CertificateStore $Thumbprint) -DeleteKey"],
            Regex.Matches(code, @"Remove-Item [^\n]+").Select(match => match.Value.Trim()));
        Assert.Equal(1, Count(code, "Get-ChildItem -LiteralPath $CertificateStore | Where-Object { $_.Subject -ceq $CertificateSubject }"));
        Assert.Equal(1, Count(code, "Get-ChildItem"));
    }

    [Fact]
    public void Only_add_credential_takes_a_validity_and_it_is_bounded_by_the_run_window()
    {
        var code = HarnessScriptText.Code(Script);
        Assert.Contains(
            "if ($Command -eq \"add-credential\") {\n    if (-not $PSBoundParameters.ContainsKey(\"ValidityHours\") -or $ValidityHours -lt 1 -or $ValidityHours -gt $MaximumValidityHours) {",
            code,
            StringComparison.Ordinal);
        Assert.Contains("elseif ($PSBoundParameters.ContainsKey(\"ValidityHours\")) {", code, StringComparison.Ordinal);
        Assert.Equal(1, Count(code, "-NotAfter $now.AddHours($ValidityHours)"));
    }

    private static int Count(string text, string fragment) => Regex.Count(text, Regex.Escape(fragment));
}

/// <summary>
/// <c>eng/harness-identity.ps1</c> run by PowerShell in a sandbox (<see cref="HarnessAzureCliSandbox"/>), whose stand-in az
/// answers as Microsoft Graph would. Every refusal comes before the stand-in is called; create writes exactly what was
/// reviewed and nothing when everything exists; verify finds every deviation from B-9. add-credential and remove-credential
/// are run only up to their refusals, because past them they would open the certificate store of the machine.
/// </summary>
public sealed class HarnessIdentityScriptRunTests
{
    private const string Tenant = "0199b001-0000-7000-8000-00000000c001";
    private const string ApplicationObject = "0199b001-0000-7000-8000-00000000c002";
    private const string ApplicationId = "0199b001-0000-7000-8000-00000000c003";
    private const string PrincipalObject = "0199b001-0000-7000-8000-00000000c004";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string NotApproved = "REFUSED: {0} contacts Microsoft Entra ID and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent.";

    /// <summary>One deviation from B-9 at a time, and the check that must fail.</summary>
    public static TheoryData<string, string> Deviations => new()
    {
        { "multi-tenant", "application.single_tenant" },
        { "api-permission", "application.no_api_permission" },
        { "web-redirect", "application.no_redirect_uri" },
        { "spa-redirect", "application.no_redirect_uri" },
        { "public-client-redirect", "application.no_redirect_uri" },
        { "app-role", "application.no_app_role" },
        { "identifier-uri", "application.no_exposed_api" },
        { "exposed-scope", "application.no_exposed_api" },
        { "client-secret", "application.no_client_secret" },
        { "principal-credential", "service_principal.no_own_credential" },
        { "group-or-role", "service_principal.no_group_or_directory_role" },
        { "owned-object", "service_principal.owns_nothing" },
        { "application-permission", "service_principal.no_application_permission" },
        { "delegated-permission", "service_principal.no_delegated_permission" },
        { "two-credentials", "application.credential_at_most_one_certificate" },
        { "foreign-credential", "application.credential_at_most_one_certificate" },
    };

    [Theory]
    [InlineData("create")]
    [InlineData("verify")]
    [InlineData("remove-credential")]
    [InlineData("add-credential")]
    public async Task Without_approve_the_plan_is_shown_and_nothing_is_sent(string command)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        string[] arguments = command == "add-credential" ? [command, "-TenantId", Tenant, "-ValidityHours", "8"] : [command, "-TenantId", Tenant];
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, arguments);

        AssertRefused(run, string.Format(System.Globalization.CultureInfo.InvariantCulture, NotApproved, command));
        Assert.Contains("  tenant              : " + Tenant, run.Lines);
        Assert.Contains("  application         : wce-dev-harness-keyvault-runtime (single-tenant; no API permission, redirect URI, app role or client secret)", run.Lines);
    }

    [Theory]
    [InlineData("0199B001-0000-7000-8000-00000000C001")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("tenant-1")]
    public async Task An_invalid_tenant_is_refused_before_anything_runs(string tenant)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "verify", "-TenantId", tenant, "-Approve");
        AssertRefused(run, "REFUSED: -TenantId must be a lower-case GUID other than the nil GUID. Nothing was sent.");
    }

    [Theory]
    [InlineData("add-credential", null)]
    [InlineData("add-credential", "0")]
    [InlineData("add-credential", "25")]
    [InlineData("verify", "8")]
    [InlineData("create", "8")]
    [InlineData("remove-credential", "1")]
    public async Task Only_add_credential_takes_a_validity_of_one_to_twenty_four_hours(string command, string? hours)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        var arguments = new List<string> { command, "-TenantId", Tenant };
        if (hours is not null)
        {
            arguments.Add("-ValidityHours");
            arguments.Add(hours);
        }

        arguments.Add("-Approve");
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, [.. arguments]);
        AssertRefused(
            run,
            command == "add-credential"
                ? "REFUSED: add-credential needs -ValidityHours from 1 to 24, the run window. Nothing was sent."
                : "REFUSED: -ValidityHours belongs to add-credential only. Nothing was sent.");
    }

    [Theory]
    [InlineData("not-signed-in", "REFUSED: the Azure CLI is not signed in. Sign in yourself; this script never signs in. Nothing was sent.")]
    [InlineData("another-tenant", "REFUSED: the Azure CLI is signed in to another tenant than -TenantId. Nothing was sent.")]
    [InlineData("service-principal", "REFUSED: the Azure CLI is not signed in as a user. This script runs only as the product owner's own account. Nothing was sent.")]
    public async Task The_session_must_be_a_signed_in_user_of_the_tenant(string session, string reason)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        switch (session)
        {
            case "not-signed-in":
                sandbox.Answer(1, string.Empty, exitCode: 1);
                break;
            case "another-tenant":
                sandbox.Answer(1, Account("0199b001-0000-7000-8000-00000000beef", "user"));
                break;
            default:
                sandbox.Answer(1, Account(Tenant, "servicePrincipal"));
                break;
        }

        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "create", "-TenantId", Tenant, "-Approve");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(reason, run.Lines);
        Assert.Equal("account show --output json", HarnessAzureCliSandbox.Unquote(Assert.Single(run.Invocations)));
        Assert.DoesNotContain("owner@example.invalid", run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_reads_everything_back_and_establishes_a_clean_identity()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        var state = new GraphState();
        state.Answer(sandbox, 1);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "verify", "-TenantId", Tenant, "-Approve");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("VERIFY: ESTABLISHED (read-back only; nothing was written)", run.Lines);
        Assert.Contains("  application (client) id     : " + ApplicationId, run.Lines);
        Assert.Contains("  service principal object id : " + PrincipalObject, run.Lines);
        Assert.Equal(GraphState.Reads, run.Invocations.Skip(1).Select(HarnessAzureCliSandbox.Unquote));
        Assert.DoesNotContain(run.Invocations, call => Regex.IsMatch(call, "--method (post|patch|put|delete)"));
        Assert.DoesNotContain("owner@example.invalid", run.All, StringComparison.Ordinal);
        Assert.Equal(14, run.Lines.Count(line => line.EndsWith(" PASS", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Deviations))]
    public async Task Verify_finds_every_deviation_from_b9(string deviation, string failedCheck)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        var state = new GraphState();
        state.Deviate(deviation);
        state.Answer(sandbox, 1);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "verify", "-TenantId", Tenant, "-Approve");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("VERIFY: NOT VERIFIED. Failed: " + failedCheck + ".", run.Lines);
        Assert.DoesNotContain(run.Lines, line => line.StartsWith("VERIFY: ESTABLISHED", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Invocations, call => Regex.IsMatch(call, "--method (post|patch|put|delete)"));
    }

    [Theory]
    [InlineData(0, "NOT VERIFIED: no application is named wce-dev-harness-keyvault-runtime. Run create first; nothing was written.")]
    [InlineData(2, "REFUSED: 2 applications are named wce-dev-harness-keyvault-runtime. Resolve this by hand; nothing was written.")]
    public async Task Verify_never_guesses_which_application_is_meant(int applications, string reason)
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        sandbox.Answer(1, Account(Tenant, "user"));
        sandbox.Answer(2, Collection(Enumerable.Range(0, applications).Select(_ => ApplicationSummary()).ToArray()));
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "verify", "-TenantId", Tenant, "-Approve");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(reason, run.Lines);
        Assert.Equal(2, run.Invocations.Count);
    }

    [Fact]
    public async Task Create_writes_exactly_the_reviewed_application_and_principal_and_reads_them_back()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        sandbox.Answer(1, Account(Tenant, "user"));
        sandbox.Answer(2, Collection());
        sandbox.Answer(3, ApplicationSummary().ToJsonString());
        sandbox.Answer(4, Collection());
        sandbox.Answer(5, new JsonObject { ["id"] = PrincipalObject, ["appId"] = ApplicationId }.ToJsonString());
        new GraphState().Answer(sandbox, 5);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "create", "-TenantId", Tenant, "-Approve");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("CREATE: COMPLETE (verified by read-back)", run.Lines);
        Assert.Contains("  application         : created", run.Lines);
        Assert.Contains("  service principal   : created", run.Lines);
        Assert.Equal(13, run.Invocations.Count);

        // The two writes, each a POST of exactly the reviewed body; the body file is gone afterwards.
        var calls = run.Invocations.Select(HarnessAzureCliSandbox.Unquote).ToList();
        Assert.StartsWith("rest --method post --url " + Graph + "/applications --headers Content-Type=application/json --body @", calls[2], StringComparison.Ordinal);
        Assert.StartsWith("rest --method post --url " + Graph + "/servicePrincipals --headers Content-Type=application/json --body @", calls[4], StringComparison.Ordinal);
        Assert.Equal(
            "{\"displayName\":\"wce-dev-harness-keyvault-runtime\",\"signInAudience\":\"AzureADMyOrg\",\"notes\":\"Gate 3G DEV/TEST harness runtime identity (owner decisions B-7 and B-9). Single-tenant; no API permission, redirect URI, app role or client secret.\"}",
            sandbox.Body(3));
        Assert.Equal("{\"appId\":\"" + ApplicationId + "\"}", sandbox.Body(5));
        Assert.Equal(2, calls.Count(call => call.StartsWith("rest --method post", StringComparison.Ordinal)));
        Assert.DoesNotContain(calls, call => Regex.IsMatch(call, "--method (patch|put|delete)"));
        Assert.Empty(sandbox.LeftoverBodies());
        Assert.Equal(GraphState.Reads, calls.Skip(5));
    }

    [Fact]
    public async Task Create_writes_nothing_when_the_application_and_principal_exist()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        sandbox.Answer(1, Account(Tenant, "user"));
        sandbox.Answer(2, Collection(ApplicationSummary()));
        sandbox.Answer(3, Collection(new JsonObject { ["id"] = PrincipalObject, ["appId"] = ApplicationId }));
        new GraphState().Answer(sandbox, 3);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "create", "-TenantId", Tenant, "-Approve");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("CREATE: COMPLETE (verified by read-back)", run.Lines);
        Assert.Equal(11, run.Invocations.Count);
        Assert.DoesNotContain(run.Invocations, call => Regex.IsMatch(call, "--method (post|patch|put|delete)"));
        Assert.DoesNotContain(run.Lines, line => line.EndsWith(": created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_never_repairs_an_existing_application_that_deviates_from_b9()
    {
        using var sandbox = new HarnessAzureCliSandbox(HarnessIdentityScriptTests.Script);
        sandbox.Answer(1, Account(Tenant, "user"));
        sandbox.Answer(2, Collection(ApplicationSummary()));
        sandbox.Answer(3, Collection(new JsonObject { ["id"] = PrincipalObject, ["appId"] = ApplicationId }));
        var state = new GraphState();
        state.Deviate("web-redirect");
        state.Answer(sandbox, 3);
        var run = await sandbox.RunAsync(HarnessIdentityScriptTests.Script, "create", "-TenantId", Tenant, "-Approve");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("CREATE: NOT VERIFIED. Failed: application.no_redirect_uri.", run.Lines);
        Assert.DoesNotContain(run.Invocations, call => Regex.IsMatch(call, "--method (post|patch|put|delete)"));
    }

    private static string Account(string tenant, string userType) =>
        "{\"id\":\"0199b001-0000-7000-8000-00000000c0aa\",\"tenantId\":\"" + tenant + "\",\"name\":\"Sandbox subscription\",\"user\":{\"name\":\"owner@example.invalid\",\"type\":\"" + userType + "\"}}";

    private static JsonObject ApplicationSummary() =>
        new() { ["id"] = ApplicationObject, ["appId"] = ApplicationId, ["displayName"] = "wce-dev-harness-keyvault-runtime" };

    private static string Collection(params JsonNode[] items) => new JsonObject { ["value"] = new JsonArray(items) }.ToJsonString();

    private static void AssertRefused(HarnessScriptRun run, string reason)
    {
        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Invocations);
        Assert.Contains(reason, run.Lines);
    }

    /// <summary>What Microsoft Graph holds for the harness identity, as the stand-in az answers it.</summary>
    private sealed class GraphState
    {
        /// <summary>The read-back calls, in the script's order, without the quotes PowerShell adds.</summary>
        internal static readonly string[] Reads =
        [
            "rest --method get --url " + Graph + "/applications --output json --uri-parameters $filter=displayName eq 'wce-dev-harness-keyvault-runtime' $select=id,appId,displayName",
            "rest --method get --url " + Graph + "/applications/" + ApplicationObject + " --output json",
            "rest --method get --url " + Graph + "/servicePrincipals --output json --uri-parameters $filter=appId eq '" + ApplicationId + "' $select=id,appId",
            "rest --method get --url " + Graph + "/servicePrincipals/" + PrincipalObject + " --output json",
            "rest --method get --url " + Graph + "/servicePrincipals/" + PrincipalObject + "/transitiveMemberOf --output json --uri-parameters $select=id",
            "rest --method get --url " + Graph + "/servicePrincipals/" + PrincipalObject + "/ownedObjects --output json --uri-parameters $select=id",
            "rest --method get --url " + Graph + "/servicePrincipals/" + PrincipalObject + "/appRoleAssignments --output json",
            "rest --method get --url " + Graph + "/servicePrincipals/" + PrincipalObject + "/oauth2PermissionGrants --output json",
        ];

        private readonly JsonObject _application = new()
        {
            ["id"] = ApplicationObject,
            ["appId"] = ApplicationId,
            ["displayName"] = "wce-dev-harness-keyvault-runtime",
            ["signInAudience"] = "AzureADMyOrg",
            ["identifierUris"] = new JsonArray(),
            ["requiredResourceAccess"] = new JsonArray(),
            ["appRoles"] = new JsonArray(),
            ["passwordCredentials"] = new JsonArray(),
            ["keyCredentials"] = new JsonArray(),
            ["api"] = new JsonObject { ["oauth2PermissionScopes"] = new JsonArray() },
            ["web"] = new JsonObject { ["redirectUris"] = new JsonArray() },
            ["spa"] = new JsonObject { ["redirectUris"] = new JsonArray() },
            ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray() },
        };

        private readonly JsonObject _principal = new()
        {
            ["id"] = PrincipalObject,
            ["appId"] = ApplicationId,
            ["passwordCredentials"] = new JsonArray(),
            ["keyCredentials"] = new JsonArray(),
        };

        private readonly JsonArray _memberOf = [];
        private readonly JsonArray _owned = [];
        private readonly JsonArray _appRoleAssignments = [];
        private readonly JsonArray _permissionGrants = [];

        /// <summary>Applies one deviation from B-9.</summary>
        internal void Deviate(string deviation)
        {
            static JsonObject Certificate(string name) => new() { ["type"] = "AsymmetricX509Cert", ["usage"] = "Verify", ["displayName"] = name, ["endDateTime"] = "2026-10-09T08:00:00Z" };

            switch (deviation)
            {
                case "multi-tenant":
                    _application["signInAudience"] = "AzureADMultipleOrgs";
                    break;
                case "api-permission":
                    _application["requiredResourceAccess"] = new JsonArray(new JsonObject { ["resourceAppId"] = "00000003-0000-0000-c000-000000000000" });
                    break;
                case "web-redirect":
                    _application["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://127.0.0.1:47443/operator/callback") };
                    break;
                case "spa-redirect":
                    _application["spa"] = new JsonObject { ["redirectUris"] = new JsonArray("https://127.0.0.1/spa") };
                    break;
                case "public-client-redirect":
                    _application["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray("http://localhost") };
                    break;
                case "app-role":
                    _application["appRoles"] = new JsonArray(new JsonObject { ["value"] = "role" });
                    break;
                case "identifier-uri":
                    _application["identifierUris"] = new JsonArray("api://harness");
                    break;
                case "exposed-scope":
                    _application["api"] = new JsonObject { ["oauth2PermissionScopes"] = new JsonArray(new JsonObject { ["value"] = "scope" }) };
                    break;
                case "client-secret":
                    _application["passwordCredentials"] = new JsonArray(new JsonObject { ["displayName"] = "secret" });
                    break;
                case "principal-credential":
                    _principal["keyCredentials"] = new JsonArray(Certificate("principal"));
                    break;
                case "group-or-role":
                    _memberOf.Add(new JsonObject { ["id"] = "0199b001-0000-7000-8000-00000000d001" });
                    break;
                case "owned-object":
                    _owned.Add(new JsonObject { ["id"] = "0199b001-0000-7000-8000-00000000d002" });
                    break;
                case "application-permission":
                    _appRoleAssignments.Add(new JsonObject { ["id"] = "assignment" });
                    break;
                case "delegated-permission":
                    _permissionGrants.Add(new JsonObject { ["id"] = "grant" });
                    break;
                case "two-credentials":
                    _application["keyCredentials"] = new JsonArray(
                        Certificate("wce-dev-harness-keyvault-runtime-0000000000000000000000000000000000000001"),
                        Certificate("wce-dev-harness-keyvault-runtime-0000000000000000000000000000000000000002"));
                    break;
                case "foreign-credential":
                    _application["keyCredentials"] = new JsonArray(Certificate("someone-else"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(deviation), deviation, "Unknown deviation.");
            }
        }

        /// <summary>Answers the session read at call 1 and the eight read-back calls that follow call <paramref name="after"/>.</summary>
        internal void Answer(HarnessAzureCliSandbox sandbox, int after)
        {
            sandbox.Answer(1, Account(Tenant, "user"));
            string[] answers =
            [
                Collection(ApplicationSummary()),
                _application.ToJsonString(),
                Collection(new JsonObject { ["id"] = PrincipalObject, ["appId"] = ApplicationId }),
                _principal.ToJsonString(),
                new JsonObject { ["value"] = _memberOf.DeepClone() }.ToJsonString(),
                new JsonObject { ["value"] = _owned.DeepClone() }.ToJsonString(),
                new JsonObject { ["value"] = _appRoleAssignments.DeepClone() }.ToJsonString(),
                new JsonObject { ["value"] = _permissionGrants.DeepClone() }.ToJsonString(),
            ];
            for (var index = 0; index < answers.Length; index++)
            {
                sandbox.Answer(after + 1 + index, answers[index]);
            }
        }
    }
}
