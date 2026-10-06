using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G Stage B3, owner decisions of 2026-10-06 (the B-14 seed clarification and the owner-run tool structure):
/// <c>eng/harness-standalone.ps1</c>, the one supported entry point to the two reviewed payloads of the future persistent
/// standalone harness database, which are not entry points themselves. These tests read its text and the payloads';
/// <see cref="HarnessStandaloneWrapperRefusalTests"/> and <see cref="HarnessStandaloneWrapperRunTests"/> run it. Nothing
/// here reaches a database, a container or the network, and the seed is never sent to PostgreSQL.
/// </summary>
public sealed class HarnessStandaloneWrapperTests
{
    internal const string WrapperFile = "eng/harness-standalone.ps1";
    internal const string SeedFile = "eng/harness-standalone-seed.sql";
    internal const string EvidenceFile = "eng/harness-standalone-evidence.sql";

    [Fact]
    public void It_is_the_one_entry_point_and_names_exactly_the_two_approved_payloads()
    {
        var code = Code();

        // Exactly the two payloads, each named once, beside the wrapper, and read only to be hashed and run.
        Assert.Equal(
            ["harness-standalone-evidence.sql", "harness-standalone-seed.sql"],
            Regex.Matches(code, @"[\w.-]+\.sql\b").Select(match => match.Value).Order(StringComparer.Ordinal));
        Assert.Equal(1, Count(code, "$SeedPayload = Join-Path $PSScriptRoot \"harness-standalone-seed.sql\""));
        Assert.Equal(1, Count(code, "$EvidencePayload = Join-Path $PSScriptRoot \"harness-standalone-evidence.sql\""));
        Assert.Equal(1, Count(code, "$payload = Read-Payload $SeedPayload"));
        Assert.Equal(1, Count(code, "$payload = Read-Payload $EvidencePayload"));

        // Two commands, and no other script, module or program is started.
        Assert.Equal(1, Count(code, "[ValidateSet(\"seed\", \"evidence\")]"));
        Assert.DoesNotMatch(new Regex(@"\.(ps1|psm1|psd1|py|sh|cmd|bat|exe)\b"), code);
        Assert.DoesNotMatch(
            new Regex(@"Invoke-Expression|\biex\b|Start-Process|Invoke-Command|Start-Job|Add-Type|Import-Module|\bcmd\b|\bbash\b|\bpwsh\b|\bpowershell\b", RegexOptions.IgnoreCase),
            code);
    }

    [Fact]
    public void The_database_is_mandatory_has_no_default_and_is_never_taken_from_env()
    {
        var code = Code();
        var parameters = code[..code.IndexOf("\n)\n", StringComparison.Ordinal)];

        // Mandatory and without a default, so PowerShell refuses to start without it; and never assigned afterwards.
        Assert.Contains("    [Parameter(Mandatory = $true)]\n    [string]$Database,\n", parameters, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\$Database\s*="), code);

        // Of .env exactly two keys are read: the suite database, only to refuse it, and the schema owner's role name.
        Assert.Equal(["POSTGRES_DB", "POSTGRES_USER"], Regex.Matches(code, @"Read-DotEnvValue ""(\w+)""").Select(match => match.Groups[1].Value));
        Assert.Equal(3, Count(code, "$suiteDatabase"));
        Assert.Equal(1, Count(code, "if ($Database -eq $suiteDatabase) {"));

        // psql always connects to -Database itself, and to nothing else.
        Assert.Equal(2, Count(code, "\"--dbname\", $Database,"));
        Assert.Equal(2, Count(code, "\"--dbname\""));
    }

    [Fact]
    public void Only_the_approved_pattern_is_accepted_and_the_shared_suite_database_is_refused_first()
    {
        var code = Code();

        // The payloads' own rule, applied case-sensitively and anchored at both ends.
        Assert.Equal(1, Count(code, @"$DatabasePattern = '\Awce_harness_[a-z0-9_]{1,51}\z'"));
        Assert.Equal(1, Count(code, "if ($Database -cnotmatch $DatabasePattern) {"));
        foreach (var payload in new[] { SeedFile, EvidenceFile })
        {
            Assert.Contains("'^wce_harness_[a-z0-9_]{1,51}$'", Read(payload), StringComparison.Ordinal);
        }

        // The shared suite database is refused before the name's form is even looked at.
        var shared = code.IndexOf("if ($Database -eq $suiteDatabase) {", StringComparison.Ordinal);
        var pattern = code.IndexOf("if ($Database -cnotmatch $DatabasePattern) {", StringComparison.Ordinal);
        Assert.True(shared > 0 && shared < pattern);

        // The rule as PowerShell applies it, which .NET shares: the shared suite database, the maintenance and template
        // databases, the name proposed for a temporary test database and every malformed variant are refused.
        var rule = new Regex(@"\Awce_harness_[a-z0-9_]{1,51}\z", RegexOptions.CultureInvariant);
        foreach (var refused in HarnessStandaloneWrapperRefusalTests.UnapprovedNameList.Append("workcontinuity").Append("wce_harness_live\n"))
        {
            Assert.DoesNotMatch(rule, refused);
        }

        Assert.Matches(rule, WrapperSandbox.Standalone);
        Assert.Matches(rule, "wce_harness_" + new string('a', 51));
    }

    [Fact]
    public void Nothing_reaches_docker_before_every_refusal_and_the_seed_reaches_it_only_after_approve()
    {
        var code = Code();
        var calls = Regex.Matches(code, Regex.Escape("$payload.Text | & docker @arguments")).Select(match => match.Index).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, Count(code, "& docker"));

        // The target is refused before anything else runs, whichever the command.
        var target = code.IndexOf("\nAssert-Target\n", StringComparison.Ordinal);
        Assert.True(target > 0 && target < calls.Min());

        // The seed: every value, the owner, the plan, the approval and the local engine, all before its one call.
        var seed = code.IndexOf("    \"seed\" {", StringComparison.Ordinal);
        var evidence = code.IndexOf("    \"evidence\" {", StringComparison.Ordinal);
        Assert.True(seed > target && evidence > seed);
        var seedCall = Assert.Single(calls, index => index > seed && index < evidence);
        foreach (var step in new[]
                 {
                     "Assert-Guid \"TenantId\" $TenantId", "Assert-Guid \"UserId\" $UserId", "-cnotmatch $IdentityProviderPattern",
                     "-cnotmatch $ProviderSubjectPattern", "Read-DotEnvValue \"POSTGRES_USER\"", "if (-not $Approve) {", "Assert-LocalEngine",
                 })
        {
            var at = code.IndexOf(step, seed, StringComparison.Ordinal);
            Assert.True(at > seed && at < seedCall, step);
        }

        Assert.Contains(
            "if (-not $Approve) {\n            Stop-Run \"REFUSED: seeding requires -Approve. Review the plan above, then re-run with -Approve. Nothing was written.\"\n        }",
            code,
            StringComparison.Ordinal);

        // The evidence: its tenant, no seed value, no approval and the local engine, all before its one call.
        var evidenceCall = Assert.Single(calls, index => index > evidence);
        foreach (var step in new[] { "Assert-Guid \"TenantId\" $TenantId", "$PSBoundParameters.ContainsKey($seedValue)", "if ($Approve) {", "Assert-LocalEngine" })
        {
            var at = code.IndexOf(step, evidence, StringComparison.Ordinal);
            Assert.True(at > evidence && at < evidenceCall, step);
        }

        // Every refusal ends the run the same way: one printed line, then the one throw.
        Assert.Equal(1, Regex.Count(code, @"\bthrow\b"));
        Assert.Contains("    Write-Host $Message -ForegroundColor Red\n    throw $Message\n", code, StringComparison.Ordinal);
    }

    [Fact]
    public void psql_runs_only_inside_the_local_compose_postgres_service_with_every_value_as_its_own_argument()
    {
        var code = Code();

        // Both calls: the local stack's own compose and environment files, the postgres service, and psql over its socket.
        Assert.Equal(1, Count(code, "$ComposeFile = Join-Path $Root \"infra/local/docker-compose.yml\""));
        Assert.Equal(1, Count(code, "$EnvFile = Join-Path $Root \".env\""));
        Assert.Equal(2, Count(code, "$arguments = @("));
        Assert.Equal(2, Count(code, "\"compose\", \"--env-file\", $EnvFile, \"-f\", $ComposeFile,"));
        Assert.Equal(1, Count(code, "\"exec\", \"-T\", \"postgres\","));
        Assert.Equal(
            1,
            Count(code, "\"exec\", \"-T\",\n            \"-e\", \"PGOPTIONS=-c wce_evidence.expected_database=$Database -c wce_evidence.tenant_id=$TenantId\",\n            \"postgres\","));
        Assert.Equal(2, Count(code, "\"psql\", \"--no-psqlrc\", \"--set\", \"ON_ERROR_STOP=1\", \"--quiet\""));
        Assert.Equal(2, Count(code, "\"--file\", \"-\""));

        // The only psql values are ON_ERROR_STOP and the five the seed requires, each an argument of its own.
        Assert.Equal(
            [
                "ON_ERROR_STOP=1", "target_database=$Database", "tenant_id=$TenantId", "user_id=$UserId", "identity_provider=$IdentityProvider",
                "provider_subject=$ProviderSubject", "ON_ERROR_STOP=1",
            ],
            Regex.Matches(code, @"""--set"", ""([^""]+)""").Select(match => match.Groups[1].Value));

        // No host, port, inline command or output file, and no other docker operation.
        Assert.DoesNotMatch(new Regex(@"""(--host|-h|--port|-p|--command|-c|--variable|-v|--output|-o|--log-file|-L|--single-transaction)"""), code);
        Assert.DoesNotMatch(new Regex(@"""(run|cp|up|down|start|stop|restart|rm|build|pull|push|volume|network|context|kill)"""), code);

        // No other engine, context or compose project, and nothing else from the environment.
        Assert.Equal(1, Count(code, "foreach ($variable in @(\"DOCKER_HOST\", \"DOCKER_CONTEXT\", \"COMPOSE_PROJECT_NAME\")) {"));
        Assert.Equal(1, Count(code, "[Environment]::GetEnvironmentVariable("));
        Assert.DoesNotContain("$env:", code, StringComparison.Ordinal);
    }

    [Fact]
    public void It_can_neither_create_nor_drop_a_database_nor_change_a_role_or_a_privilege_nor_migrate()
    {
        var code = Code();

        // The wrapper holds no SQL at all: psql is given a payload, read from disk and hashed, and nothing else.
        Assert.DoesNotMatch(new Regex(@"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|CREATE|DROP|ALTER|GRANT|REVOKE|TRUNCATE|BEGIN|COMMIT|ROLLBACK|VACUUM|REASSIGN)\b"), code);
        // Nor any way the repository migrates or bootstraps a database: dotnet ef, the migrator's and the bootstrap's flags,
        // or the local database script.
        Assert.DoesNotMatch(
            new Regex(
                @"\b(createdb|dropdb|createuser|dropuser|pg_dump|pg_restore|pg_ctl|dotnet|ef)\b|database\s+update|--migrate|MigrateAsync|bootstrap|db\.ps1|dev\.ps1|infra\.ps1",
                RegexOptions.IgnoreCase),
            code);
        Assert.Equal(2, Count(code, "$payload.Text | & docker @arguments"));

        // Neither payload can create or drop a database, change a role or a privilege, or define privileged code.
        foreach (var payload in new[] { SeedFile, EvidenceFile })
        {
            Assert.DoesNotMatch(
                new Regex(
                    @"\b(CREATE|DROP|ALTER)\s+(DATABASE|ROLE|USER|SCHEMA|TABLE|POLICY|FUNCTION|EXTENSION)\b|\b(GRANT|REVOKE|REASSIGN)\b|\bSET\s+(SESSION\s+)?ROLE\b|\bSECURITY\s+DEFINER\b",
                    RegexOptions.IgnoreCase),
                SqlCode(payload));
        }
    }

    [Fact]
    public void It_never_takes_or_prints_a_secret_and_prints_only_closed_values()
    {
        var code = Code();
        Assert.DoesNotMatch(
            new Regex(
                @"password|pgpass|passfile|connection\s*string|credential|securestring|token|Get-Content|Set-Content|Add-Content|Out-File|Tee-Object|Write-Output|Write-Information|Export-|ConvertTo-",
                RegexOptions.IgnoreCase),
            code);

        // Every value printed is a validated input, the payload's name or hash, a check's closed result or a closed message.
        Assert.Equal(
            ["Database", "EvidenceChecks", "IdentityProvider", "Message", "ProviderSubject", "TenantId", "UserId", "count", "expected", "payload", "status"],
            Variables(code, @"Write-Host [^\n]*"));
        Assert.Equal(2, Count(code, "$($payload.Name)"));
        Assert.Equal(2, Count(code, "$($payload.Hash)"));
        Assert.DoesNotMatch(new Regex(@"Write-Host[^\n]*payload\.Text"), code);

        // A refusal names no secret and no row: only parameter and variable names, an exit code and closed results.
        Assert.Equal(["LASTEXITCODE", "Name", "Path", "problems", "seedValue", "variable"], Variables(code, @"Stop-Run [^\n]*"));
        Assert.Equal(
            ["TenantContextSteps", "exitCode", "expected", "problems", "status", "steps", "withheld"],
            Variables(code, @"\$problems \+= [^\n]*"));
    }

    [Fact]
    public void The_evidence_command_is_read_only_runs_as_the_runtime_role_and_requires_every_check()
    {
        var code = Code();
        Assert.Equal(1, Count(code, "$RuntimeRole = \"workcontinuity_app\""));
        Assert.Equal(1, Count(code, "\"--username\", $RuntimeRole, \"--dbname\", $Database,"));
        Assert.Equal(1, Count(code, "\"--username\", $owner, \"--dbname\", $Database,"));
        Assert.Equal(1, Count(code, "\"--quiet\", \"--no-align\", \"--tuples-only\","));
        Assert.Equal(1, Count(code, "if ($Approve) {\n            Stop-Run \"REFUSED: evidence is read-only and takes no -Approve. Nothing was run.\""));

        // The procedure is one read-only transaction, rolled back, and its comments name the one entry point.
        var procedure = SqlCode(EvidenceFile);
        Assert.StartsWith("BEGIN TRANSACTION READ ONLY;\n", procedure, StringComparison.Ordinal);
        Assert.EndsWith("\nROLLBACK;", procedure, StringComparison.Ordinal);
        Assert.Equal(1, Count(Read(EvidenceFile), "./eng/harness-standalone.ps1 evidence"));
        Assert.DoesNotMatch(new Regex(@"psql --|PGOPTIONS=|Example shape"), Read(EvidenceFile));

        // The wrapper requires exactly the procedure's checks, in its order, and exactly its three tenant switches.
        var start = code.IndexOf("$EvidenceChecks = @(", StringComparison.Ordinal);
        var list = code[start..code.IndexOf("\n)\n", start, StringComparison.Ordinal)];
        var expected = ProcedureChecks().ToList();
        Assert.Equal(22, expected.Count);
        Assert.Equal(expected, Regex.Matches(list, @"""([a-z0-9_.]+)""").Select(match => match.Groups[1].Value));
        Assert.Equal(1, Count(code, "$TenantContextSteps = 3"));
        Assert.Equal(3, Regex.Count(procedure, @"SELECT set_config\('app\.tenant_id'"));

        // Established only when psql succeeds and every check is reported once, in order, as PASS; anything else is withheld.
        foreach (var rule in new[]
                 {
                     "if ($exitCode -ne 0) {",
                     "if ($withheld -gt 0) {",
                     "if ($steps -ne $TenantContextSteps) {",
                     "if (($reported -join \",\") -cne ($EvidenceChecks -join \",\")) {",
                     "if ($status -cne \"PASS\") {",
                     "if ($problems.Count -gt 0) {",
                     @"$match = [regex]::Match($line, '\A([a-z0-9_]+\.[a-z0-9_]+)\|(PASS|FAIL)\|([0-9]{0,18})\z')",
                 })
        {
            Assert.Equal(1, Count(code, rule));
        }
    }

    [Fact]
    public void The_seed_payload_writes_only_a_tenant_and_a_canonical_user_and_never_a_connection_or_an_authorization()
    {
        var seed = SqlCode(SeedFile);
        Assert.Equal(["public.tenants", "public.users"], Regex.Matches(seed, @"INSERT INTO ([a-z_.]+)").Select(match => match.Groups[1].Value));

        // No statement names an authorization, OAuth or credential table or column; quoted text, such as the latest
        // migration's name, is compared, never written.
        Assert.DoesNotMatch(
            new Regex(@"connector_|oauth|token|verifier|pkce|provider_account|state_hash|material_version|granted_scopes|INSERT INTO public\.connections", RegexOptions.IgnoreCase),
            Regex.Replace(seed, @"'[^']*'", "''"));
        Assert.Equal(1, Count(seed, "OR (SELECT count(*) FROM public.connections) <> 0 THEN"));
        Assert.Equal(1, Count(seed, "\\echo 'seed: done (one tenant, one canonical user; no connection, no authorization)'"));

        // Its comments say what the owner decided, name its one entry point and offer no other way to run it.
        var text = Read(SeedFile);
        foreach (var statement in new[]
                 {
                     "It never seeds a monday connection, a connector authorization, an OAuth transaction, token material, a PKCE",
                     "product's existing Connect / MondayConnectionAuthorizer lifecycle, during a separately approved run.",
                     "A payload only, not a supported operator entry point (owner decision, 2026-10-06).",
                     "./eng/harness-standalone.ps1 seed",
                 })
        {
            Assert.Equal(1, Count(text, statement));
        }

        Assert.DoesNotMatch(new Regex(@"psql --|-f eng/|Example shape"), text);
    }

    [Fact]
    public void The_standalone_gate_and_the_harness_are_left_alone_and_nothing_on_the_machine_is_changed()
    {
        var code = Code();
        Assert.DoesNotMatch(new Regex(@"HarnessStandaloneGate|ApprovedDatabases|HarnessOptions|App\.Harness|Harness\.Monday|WorkContinuity\.|tests/"), code);
        Assert.DoesNotMatch(
            new Regex(
                @"Set-Content|Add-Content|Out-File|New-Item|Copy-Item|Move-Item|Remove-Item|Rename-Item|Clear-Content|WriteAll|\]::(Write|Append|Delete|Copy|Move)\w*\(|Set-Item|Set-ExecutionPolicy|Set-Location|Push-Location|\bsetx\b",
                RegexOptions.IgnoreCase),
            code);

        var options = Read("tests/App.Harness.Monday/Configuration/HarnessOptions.cs");
        Assert.Equal(1, Count(options, "private static readonly string[] ApprovedDatabases = [];"));
        Assert.Equal(1, Count(options, "return ApprovedDatabases.Contains(options.DatabaseName, StringComparer.Ordinal);"));
    }

    [Fact]
    public void Nothing_in_the_repository_runs_the_wrapper()
    {
        // Only its payloads, which name their entry point, and the tests name it.
        var holders = RepositoryFiles(".cs", ".ps1", ".psm1", ".sh", ".cmd", ".bat", ".yml", ".yaml", ".json", ".py", ".bicep", ".bicepparam", ".sql", ".csproj", ".props", ".targets", ".toml", "")
            .Where(path => path != WrapperFile)
            .Where(path => Read(path).Contains("harness-standalone.ps1", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        Assert.Equal([EvidenceFile, SeedFile, "tests/App.Security.Tests/HarnessStandaloneSeedTests.cs", "tests/App.Security.Tests/HarnessStandaloneWrapperTests.cs"], holders);

        // No workflow, image, compose file, local server script or other engineering script names it or a payload.
        foreach (var path in RepositoryFiles(".yml", ".yaml", ".sh", ".ps1", "").Where(path => path != WrapperFile))
        {
            Assert.DoesNotContain("harness-standalone", Read(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_wrapper_is_plain_ascii_so_that_windows_powershell_reads_it_as_written()
    {
        // Windows PowerShell reads a file without a byte order mark in the system code page.
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryLayout.Root.FullName, WrapperFile));
        Assert.DoesNotContain(bytes, value => value > 127);
    }

    internal static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // A payload's statements: its lines without comments and blank lines.
    internal static string SqlCode(string relativePath) =>
        string.Join(
            "\n",
            Read(relativePath).Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal) && line.Trim().Length > 0));

    // The checks of the evidence procedure, in its order.
    internal static IEnumerable<string> ProcedureChecks() =>
        Regex.Matches(SqlCode(EvidenceFile), @"'((?:transaction|identity|role|rls|r3b2|runtime|isolation)\.[a-z_]+)'").Select(match => match.Groups[1].Value);

    // The wrapper without its comment lines.
    private static string Code() =>
        string.Join("\n", Read(WrapperFile).Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static int Count(string text, string fragment) => Regex.Count(text, Regex.Escape(fragment));

    // The variables named on the lines that match: each $name, including those inside $( ... ).
    private static List<string> Variables(string code, string lines) =>
        [.. Regex.Matches(code, lines)
            .SelectMany(line => Regex.Matches(line.Value, @"\$(\w+)").Select(variable => variable.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> RepositoryFiles(params string[] extensions) =>
        Directory.EnumerateFiles(RepositoryLayout.Root.FullName, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RepositoryLayout.Root.FullName, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith(".git/", StringComparison.Ordinal)
                && !path.StartsWith(".claude/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/TestResults/", StringComparison.Ordinal))
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The wrapper run by PowerShell in a sandbox (<see cref="WrapperSandbox"/>): everything it must refuse, it refuses before
/// it starts anything, with one printed reason and a non-zero exit code, and docker is never called.
/// </summary>
public sealed class HarnessStandaloneWrapperRefusalTests
{
    private const string NotAStandaloneName =
        "REFUSED: -Database must be the exact name of a standalone harness database: wce_harness_ and then 1 to 51 of a-z, 0-9 and _. Nothing was run.";

    private const string SharedSuiteDatabase = "REFUSED: -Database names the shared suite database, which is never a target of this tool. Nothing was run.";

    private const string NotAGuid = "REFUSED: -TenantId must be a lower-case GUID other than the nil GUID. Nothing was run.";

    private const string NotASubject =
        "REFUSED: -ProviderSubject must be a synthetic subject of 1 to 200 of A-Z, a-z, 0-9, '.', '_' and '-'. Nothing was run.";

    /// <summary>Database names that are not approved standalone harness names, the temporary test database's included.</summary>
    internal static readonly string[] UnapprovedNameList =
    [
        "postgres",
        "template1",
        "wce_it_b3_isolated_20261006",
        "wce_harness_",
        "WCE_HARNESS_LIVE",
        "wce_harness_live;drop",
        "x_wce_harness_live",
        "wce_harness_live-1",
        "wce_harness_" + new string('a', 52),
    ];

    /// <summary>The same names, one theory row each.</summary>
    public static TheoryData<string> UnapprovedNames
    {
        get
        {
            var rows = new TheoryData<string>();
            foreach (var name in UnapprovedNameList)
            {
                rows.Add(name);
            }

            return rows;
        }
    }

    /// <summary>One invalid seed value at a time: the parameter, its value (null when it is left out) and the refusal.</summary>
    public static TheoryData<string, string?, string> InvalidSeedValues => new()
    {
        { "TenantId", "0199B000-0000-7000-8000-00000000000A", NotAGuid },
        { "TenantId", "00000000-0000-0000-0000-000000000000", NotAGuid },
        { "TenantId", "tenant-1", NotAGuid },
        { "UserId", "<the tenant>", "REFUSED: -TenantId and -UserId must differ. Nothing was run." },
        { "IdentityProvider", "Local Demo", "REFUSED: -IdentityProvider must be 1 to 64 of a-z, 0-9, '.', '_' and '-'. Nothing was run." },
        { "ProviderSubject", "has space", NotASubject },
        { "ProviderSubject", "semi;colon", NotASubject },
        { "ProviderSubject", null, NotASubject },
    };

    [Theory]
    [InlineData("seed")]
    [InlineData("evidence")]
    public async Task Without_a_database_it_does_not_start(string command)
    {
        using var sandbox = new WrapperSandbox();

        var run = await sandbox.RunAsync(null, command, "-TenantId", sandbox.Tenant);

        // PowerShell itself refuses, before any line of the wrapper runs.
        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Invocations);
        Assert.Contains("mandatoryparameters:Database", WrapperSandbox.Squash(run.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workcontinuity", "workcontinuity")]
    [InlineData("workcontinuity", "WorkContinuity")]
    [InlineData("wce_harness_suite", "wce_harness_suite")]
    [InlineData("wce_harness_suite", "WCE_HARNESS_SUITE")]
    public async Task The_shared_suite_database_is_refused_in_any_spelling_even_with_approve(string suiteDatabase, string target)
    {
        using var sandbox = new WrapperSandbox(suiteDatabase);

        WrapperSandbox.AssertRefused(await sandbox.RunAsync(null, sandbox.Seed(target, approve: true)), sandbox, SharedSuiteDatabase);
        WrapperSandbox.AssertRefused(await sandbox.RunAsync(null, sandbox.Evidence(target)), sandbox, SharedSuiteDatabase);
    }

    [Theory]
    [MemberData(nameof(UnapprovedNames))]
    public async Task A_malformed_or_unapproved_name_is_refused_even_with_approve(string target)
    {
        using var sandbox = new WrapperSandbox();

        WrapperSandbox.AssertRefused(await sandbox.RunAsync(null, sandbox.Seed(target, approve: true)), sandbox, NotAStandaloneName);
    }

    [Theory]
    [MemberData(nameof(InvalidSeedValues))]
    public async Task An_invalid_seed_value_is_refused_before_anything_runs(string parameter, string? value, string reason)
    {
        using var sandbox = new WrapperSandbox();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TenantId"] = sandbox.Tenant,
            ["UserId"] = sandbox.User,
            ["IdentityProvider"] = WrapperSandbox.IdentityProvider,
            ["ProviderSubject"] = WrapperSandbox.ProviderSubject,
        };
        values[parameter] = value == "<the tenant>" ? sandbox.Tenant : value;

        var arguments = new List<string> { "seed", "-Database", WrapperSandbox.Standalone };
        foreach (var name in new[] { "TenantId", "UserId", "IdentityProvider", "ProviderSubject" })
        {
            if (values[name] is { } given)
            {
                arguments.Add("-" + name);
                arguments.Add(given);
            }
        }

        arguments.Add("-Approve");
        WrapperSandbox.AssertRefused(await sandbox.RunAsync(null, [.. arguments]), sandbox, reason);
    }

    [Theory]
    [InlineData("DOCKER_HOST", "tcp://wrapper-tests.invalid:2375")]
    [InlineData("DOCKER_CONTEXT", "wrapper-tests-remote")]
    [InlineData("COMPOSE_PROJECT_NAME", "wrapper-tests-other")]
    public async Task Another_docker_engine_context_or_compose_project_is_refused(string variable, string value)
    {
        using var sandbox = new WrapperSandbox();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { [variable] = value };
        var reason = "REFUSED: " + variable
            + " is set, so the target might not be the local stack. This tool runs only against the local compose PostgreSQL service. Nothing was run.";

        WrapperSandbox.AssertRefused(await sandbox.RunAsync(environment, sandbox.Seed(WrapperSandbox.Standalone, approve: true)), sandbox, reason);
        WrapperSandbox.AssertRefused(await sandbox.RunAsync(environment, sandbox.Evidence(WrapperSandbox.Standalone)), sandbox, reason);
    }

    [Fact]
    public async Task Evidence_takes_no_seed_value_and_no_approve()
    {
        using var sandbox = new WrapperSandbox();

        WrapperSandbox.AssertRefused(
            await sandbox.RunAsync(null, [.. sandbox.Evidence(WrapperSandbox.Standalone), "-UserId", sandbox.User]),
            sandbox,
            "REFUSED: -UserId is a seed value; evidence takes only -Database and -TenantId. Nothing was run.");
        WrapperSandbox.AssertRefused(
            await sandbox.RunAsync(null, [.. sandbox.Evidence(WrapperSandbox.Standalone), "-Approve"]),
            sandbox,
            "REFUSED: evidence is read-only and takes no -Approve. Nothing was run.");
    }

    [Fact]
    public async Task Without_env_or_without_its_suite_database_nothing_runs()
    {
        using (var withoutEnv = new WrapperSandbox(suiteDatabase: null))
        {
            WrapperSandbox.AssertRefused(
                await withoutEnv.RunAsync(null, withoutEnv.Evidence(WrapperSandbox.Standalone)),
                withoutEnv,
                "REFUSED: .env was not found, so the shared suite database cannot be identified and refused. Nothing was run.");
        }

        using var withoutSuiteDatabase = new WrapperSandbox(suiteDatabase: string.Empty);
        WrapperSandbox.AssertRefused(
            await withoutSuiteDatabase.RunAsync(null, withoutSuiteDatabase.Seed(WrapperSandbox.Standalone, approve: true)),
            withoutSuiteDatabase,
            "REFUSED: POSTGRES_DB is not set in .env, so the shared suite database cannot be identified and refused. Nothing was run.");
    }

    [Fact]
    public async Task The_seed_never_runs_as_the_runtime_role()
    {
        using var sandbox = new WrapperSandbox(owner: "workcontinuity_app");

        WrapperSandbox.AssertRefused(
            await sandbox.RunAsync(null, sandbox.Seed(WrapperSandbox.Standalone, approve: true)),
            sandbox,
            "REFUSED: POSTGRES_USER in .env must name the local schema owner, which is not the runtime role. Nothing was run.");
    }
}

/// <summary>
/// The wrapper run in the sandbox up to its one call of the stand-in docker: what it shows, what it sends to psql, and
/// which answers of the evidence procedure establish the evidence. No database is reached and the seed never runs.
/// </summary>
public sealed class HarnessStandaloneWrapperRunTests
{
    /// <summary>Answers of the evidence procedure that must not establish the evidence.</summary>
    public static TheoryData<string> Disagreements => new()
    {
        "one check fails",
        "a check is missing",
        "a check is repeated",
        "the checks are out of order",
        "an unexpected line",
        "a tenant switch is missing",
        "psql fails",
    };

    [Fact]
    public async Task Without_approve_the_seed_shows_its_plan_and_the_payload_hash_and_runs_nothing()
    {
        using var sandbox = new WrapperSandbox();

        var run = await sandbox.RunAsync(null, sandbox.Seed(WrapperSandbox.Standalone, approve: false));

        WrapperSandbox.AssertRefused(run, sandbox, "REFUSED: seeding requires -Approve. Review the plan above, then re-run with -Approve. Nothing was written.");
        var lines = WrapperSandbox.Lines(run.Output);
        foreach (var line in new[]
                 {
                     "STANDALONE SEED PLAN",
                     "  target database   : " + WrapperSandbox.Standalone,
                     "  tenant id         : " + sandbox.Tenant,
                     "  canonical user id : " + sandbox.User,
                     "  identity provider : " + WrapperSandbox.IdentityProvider,
                     "  provider subject  : " + WrapperSandbox.ProviderSubject,
                     "  payload           : eng/harness-standalone-seed.sql",
                     "  payload SHA-256   : " + sandbox.Sha256(HarnessStandaloneWrapperTests.SeedFile),
                     "  writes            : one tenant and one canonical user, in one transaction; no connection, no authorization",
                 })
        {
            Assert.Contains(line, lines);
        }
    }

    [Fact]
    public async Task With_approve_the_seed_sends_exactly_its_payload_as_the_schema_owner_to_psql_in_the_local_compose_service()
    {
        using var sandbox = new WrapperSandbox();

        var run = await sandbox.RunAsync(null, sandbox.Seed(WrapperSandbox.Standalone, approve: true));

        Assert.Equal(0, run.ExitCode);
        var tokens = WrapperSandbox.Tokens(Assert.Single(run.Invocations));
        Assert.Equal(Path.GetFullPath(sandbox.EnvFile), Path.GetFullPath(tokens[2]));
        Assert.Equal(Path.GetFullPath(sandbox.ComposeFile), Path.GetFullPath(tokens[4]));
        Assert.Equal(
            [
                "compose", "--env-file", tokens[2], "-f", tokens[4], "exec", "-T", "postgres",
                "psql", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--quiet", "--username", "sandbox_owner", "--dbname", WrapperSandbox.Standalone,
                "--set", "target_database=" + WrapperSandbox.Standalone, "--set", "tenant_id=" + sandbox.Tenant, "--set", "user_id=" + sandbox.User,
                "--set", "identity_provider=" + WrapperSandbox.IdentityProvider, "--set", "provider_subject=" + WrapperSandbox.ProviderSubject,
                "--file", "-",
            ],
            tokens);

        // The bytes psql was given are exactly the seed payload.
        Assert.Equal(sandbox.Payload(HarnessStandaloneWrapperTests.SeedFile), WrapperSandbox.Normalize(run.Stdin));
        Assert.Contains("  payload SHA-256   : " + sandbox.Sha256(HarnessStandaloneWrapperTests.SeedFile), WrapperSandbox.Lines(run.Output));
        Assert.Contains("SEED: COMPLETE (one tenant, one canonical user; no connection, no authorization)", WrapperSandbox.Lines(run.Output));
        Assert.DoesNotContain(sandbox.Sentinel, run.All, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Sentinel, run.Invocations[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_seed_is_reported_as_not_completed()
    {
        using var sandbox = new WrapperSandbox();
        sandbox.Answer(string.Empty, exitCode: 3);

        var run = await sandbox.RunAsync(null, sandbox.Seed(WrapperSandbox.Standalone, approve: true));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Single(run.Invocations);
        Assert.Contains(
            "SEED: NOT COMPLETED (psql exit 3). The payload writes in one transaction, which commits only if it completes.",
            WrapperSandbox.Lines(run.Output));
        Assert.DoesNotContain(WrapperSandbox.Lines(run.Output), line => line.StartsWith("SEED: COMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_sends_its_read_only_payload_as_the_runtime_role_and_is_established_when_every_check_passes()
    {
        using var sandbox = new WrapperSandbox();
        sandbox.Answer(WrapperSandbox.PassingAnswer());

        var run = await sandbox.RunAsync(null, sandbox.Evidence(WrapperSandbox.Standalone));

        Assert.Equal(0, run.ExitCode);
        var invocation = Assert.Single(run.Invocations).Replace("\"", string.Empty, StringComparison.Ordinal);
        Assert.StartsWith("compose --env-file ", invocation, StringComparison.Ordinal);
        Assert.EndsWith(
            " exec -T -e PGOPTIONS=-c wce_evidence.expected_database=" + WrapperSandbox.Standalone + " -c wce_evidence.tenant_id=" + sandbox.Tenant
            + " postgres psql --no-psqlrc --set ON_ERROR_STOP=1 --quiet --no-align --tuples-only --username workcontinuity_app --dbname "
            + WrapperSandbox.Standalone + " --file -",
            invocation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("target_database", invocation, StringComparison.Ordinal);

        // The bytes psql was given are exactly the evidence payload, and every check is shown with its closed status.
        Assert.Equal(sandbox.Payload(HarnessStandaloneWrapperTests.EvidenceFile), WrapperSandbox.Normalize(run.Stdin));
        var lines = WrapperSandbox.Lines(run.Output);
        foreach (var check in HarnessStandaloneWrapperTests.ProcedureChecks())
        {
            Assert.Single(lines, line => line.StartsWith("  " + check + " ", StringComparison.Ordinal) && Regex.IsMatch(line, @" PASS( \(\d+\))?$"));
        }

        Assert.Contains("EVIDENCE: ESTABLISHED (22 of 22 checks PASS)", lines);
        Assert.DoesNotContain(sandbox.Sentinel, run.All, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Disagreements))]
    public async Task Evidence_is_not_established_by_any_other_answer(string disagreement)
    {
        using var sandbox = new WrapperSandbox();
        var answer = WrapperSandbox.PassingAnswerLines();
        var exitCode = 0;
        switch (disagreement)
        {
            case "one check fails":
                answer[answer.FindIndex(line => line.StartsWith("r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update|", StringComparison.Ordinal))] =
                    "r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update|FAIL|";
                break;
            case "a check is missing":
                answer.RemoveAll(line => line.StartsWith("r3b2.current_work_views_select_only|", StringComparison.Ordinal));
                break;
            case "a check is repeated":
                answer.Insert(3, answer[2]);
                break;
            case "the checks are out of order":
                (answer[0], answer[1]) = (answer[1], answer[0]);
                break;
            case "an unexpected line":
                answer.Insert(5, "row-value-" + sandbox.Sentinel);
                break;
            case "a tenant switch is missing":
                Assert.True(answer.Remove("t"));
                break;
            default:
                Assert.Equal("psql fails", disagreement);
                exitCode = 3;
                break;
        }

        sandbox.Answer(string.Join("\n", answer) + "\n", exitCode);

        var run = await sandbox.RunAsync(null, sandbox.Evidence(WrapperSandbox.Standalone));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Single(run.Invocations);
        var lines = WrapperSandbox.Lines(run.Output);
        Assert.Contains(lines, line => line.StartsWith("EVIDENCE: NOT ESTABLISHED. ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("EVIDENCE: ESTABLISHED", StringComparison.Ordinal));
        Assert.DoesNotContain(sandbox.Sentinel, run.All, StringComparison.Ordinal);
    }
}

/// <summary>What one run of the wrapper printed, and what the stand-in docker recorded.</summary>
/// <param name="ExitCode">The wrapper's exit code.</param>
/// <param name="Output">Its standard output.</param>
/// <param name="Errors">Its standard error.</param>
/// <param name="Invocations">One line per call of the stand-in docker, with its arguments.</param>
/// <param name="Stdin">What the last call was given on its standard input, if it was called.</param>
internal sealed record WrapperRun(int ExitCode, string Output, string Errors, IReadOnlyList<string> Invocations, string? Stdin)
{
    /// <summary>Gets everything the wrapper printed.</summary>
    internal string All => Output + "\n" + Errors;
}

/// <summary>
/// A throwaway copy of the wrapper and its two payloads, beside a synthetic <c>.env</c> and an empty stand-in for the
/// compose file, with a stand-in <c>docker</c> first on the PATH that records its arguments and its standard input,
/// answers what a test gives it and does nothing else. The <c>.env</c> holds a sentinel where the local secrets would be,
/// so that a test can prove none is ever printed or passed on. No database, container or network is ever reached.
/// </summary>
internal sealed class WrapperSandbox : IDisposable
{
    internal const string Standalone = "wce_harness_live_20261006";
    internal const string IdentityProvider = "wce-dev-harness";
    internal const string ProviderSubject = "wce-harness-operator";

    private static readonly string[] EngineVariables = ["DOCKER_HOST", "DOCKER_CONTEXT", "COMPOSE_PROJECT_NAME"];
    private static readonly Lazy<string> PowerShell = new(FindPowerShell);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wce-wrapper-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the sandbox.</summary>
    /// <param name="suiteDatabase">The shared suite database .env names; empty for none, null for no .env at all.</param>
    /// <param name="owner">The schema owner .env names.</param>
    internal WrapperSandbox(string? suiteDatabase = "workcontinuity", string owner = "sandbox_owner")
    {
        Assert.False(_root.Any(char.IsWhiteSpace), "The sandbox path must hold no whitespace: " + _root);
        Directory.CreateDirectory(Path.Combine(_root, "eng"));
        Directory.CreateDirectory(Path.Combine(_root, "infra", "local"));
        Directory.CreateDirectory(ShimDirectory);
        foreach (var file in new[] { HarnessStandaloneWrapperTests.WrapperFile, HarnessStandaloneWrapperTests.SeedFile, HarnessStandaloneWrapperTests.EvidenceFile })
        {
            File.Copy(Path.Combine(RepositoryLayout.Root.FullName, file), Path.Combine(_root, file));
        }

        File.WriteAllText(ComposeFile, "# The wrapper tests' sandbox: not a compose project.\n");
        if (suiteDatabase is not null)
        {
            var lines = new List<string> { "# Synthetic values of the wrapper tests; the sentinel stands where local secrets would be." };
            if (suiteDatabase.Length > 0)
            {
                lines.Add("POSTGRES_DB=" + suiteDatabase);
            }

            lines.Add("POSTGRES_USER=" + owner);
            lines.Add("POSTGRES_APP_USER=workcontinuity_app");
            lines.Add("POSTGRES_" + "PASSWORD=" + Sentinel);
            lines.Add("POSTGRES_APP_" + "PASSWORD=" + Sentinel);
            File.WriteAllText(EnvFile, string.Join("\n", lines) + "\n");
        }

        WriteShim();
    }

    /// <summary>Gets the value that must never appear in anything the wrapper prints or passes on.</summary>
    internal string Sentinel { get; } = "sandbox-sentinel-" + Guid.NewGuid().ToString("N");

    /// <summary>Gets a fresh synthetic tenant id.</summary>
    internal string Tenant { get; } = Guid.NewGuid().ToString("D");

    /// <summary>Gets a fresh synthetic canonical user id.</summary>
    internal string User { get; } = Guid.NewGuid().ToString("D");

    /// <summary>Gets the sandbox's .env.</summary>
    internal string EnvFile => Path.Combine(_root, ".env");

    /// <summary>Gets the sandbox's stand-in compose file.</summary>
    internal string ComposeFile => Path.Combine(_root, "infra", "local", "docker-compose.yml");

    private string ShimDirectory => Path.Combine(_root, "shim");

    private string InvocationsFile => Path.Combine(ShimDirectory, "invocations.txt");

    private string StdinFile => Path.Combine(ShimDirectory, "stdin.txt");

    /// <summary>Removes the throwaway directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a process that has just ended: the temporary directory is left to the system.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>The lines of a text, without line-break characters.</summary>
    internal static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>A text without any whitespace, so that PowerShell's own line wrapping does not matter.</summary>
    internal static string Squash(string text) => Regex.Replace(text, @"\s+", string.Empty);

    /// <summary>A text with LF line breaks and without trailing ones.</summary>
    internal static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    /// <summary>The arguments of a recorded call; none of the arguments compared this way holds a space.</summary>
    internal static List<string> Tokens(string invocation) => [.. invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>What psql answers when every check of the evidence procedure passes, in the procedure's order.</summary>
    internal static List<string> PassingAnswerLines()
    {
        var lines = new List<string>();
        foreach (var check in HarnessStandaloneWrapperTests.ProcedureChecks())
        {
            // Each of the procedure's three tenant switches answers t before its isolation check.
            if (check is "isolation.without_tenant_no_row_is_visible" or "isolation.fresh_tenant_sees_no_other_tenant_row" or "isolation.other_tenant_sees_no_fresh_tenant_row")
            {
                lines.Add("t");
            }

            var counted = check.StartsWith("isolation.", StringComparison.Ordinal) && check != "isolation.fresh_tenant_present_once_with_one_active_user";
            lines.Add(check + "|PASS|" + (counted ? "16" : string.Empty));
        }

        return lines;
    }

    /// <summary>The passing answer as psql prints it.</summary>
    internal static string PassingAnswer() => string.Join("\n", PassingAnswerLines()) + "\n";

    /// <summary>Asserts that the run was refused for the reason given, before docker was ever called.</summary>
    internal static void AssertRefused(WrapperRun run, WrapperSandbox sandbox, string reason)
    {
        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Invocations);
        Assert.Contains(reason, Lines(run.Output));
        Assert.DoesNotContain(sandbox.Sentinel, run.All, StringComparison.Ordinal);
    }

    /// <summary>The arguments of a seed run against the given database, with or without -Approve.</summary>
    internal string[] Seed(string database, bool approve)
    {
        var arguments = new List<string>
        {
            "seed", "-Database", database, "-TenantId", Tenant, "-UserId", User, "-IdentityProvider", IdentityProvider, "-ProviderSubject", ProviderSubject,
        };
        if (approve)
        {
            arguments.Add("-Approve");
        }

        return [.. arguments];
    }

    /// <summary>The arguments of an evidence run against the given database.</summary>
    internal string[] Evidence(string database) => ["evidence", "-Database", database, "-TenantId", Tenant];

    /// <summary>Makes the stand-in docker print the given text and end with the given exit code.</summary>
    internal void Answer(string standardOutput, int exitCode = 0)
    {
        File.WriteAllText(Path.Combine(ShimDirectory, "stdout.txt"), standardOutput);
        File.WriteAllText(Path.Combine(ShimDirectory, "exit.txt"), exitCode.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The SHA-256 of a sandbox file, as the wrapper prints it.</summary>
    internal string Sha256(string relativePath) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, relativePath))));

    /// <summary>A sandbox payload's text, normalized as <see cref="Normalize"/> does.</summary>
    internal string Payload(string relativePath) => Normalize(File.ReadAllText(Path.Combine(_root, relativePath)));

    /// <summary>Runs the wrapper with PowerShell in the sandbox.</summary>
    /// <param name="environment">Variables to set for the run, or null.</param>
    /// <param name="arguments">The wrapper's arguments.</param>
    /// <returns>What it printed, and what the stand-in docker recorded.</returns>
    internal async Task<WrapperRun> RunAsync(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(PowerShell.Value)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
        }

        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(_root, "eng", "harness-standalone.ps1"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The stand-in comes first on the PATH, and nothing of the caller's environment can point docker anywhere else.
        start.Environment["PATH"] = ShimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (var variable in EngineVariables)
        {
            start.Environment.Remove(variable);
        }

        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["POWERSHELL_UPDATECHECK"] = "Off";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        process.StandardInput.Close();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var errors = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var invocations = File.Exists(InvocationsFile) ? await File.ReadAllLinesAsync(InvocationsFile, bounded.Token) : [];
        var stdin = File.Exists(StdinFile) ? await File.ReadAllTextAsync(StdinFile, bounded.Token) : null;
        return new WrapperRun(process.ExitCode, await output, await errors, invocations, stdin);
    }

    private static string FindPowerShell()
    {
        // Windows PowerShell, the owner's shell, on Windows; PowerShell 7 elsewhere, as CI has it.
        if (OperatingSystem.IsWindows())
        {
            var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell))
            {
                return windowsPowerShell;
            }
        }

        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No PowerShell was found, so the owner-run wrapper cannot be run: CI has pwsh, and Windows has Windows PowerShell.");
    }

    private void WriteShim()
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(ShimDirectory, "docker.cmd"),
                string.Join(
                    "\r\n",
                    "@echo off",
                    "setlocal",
                    ">>\"%~dp0invocations.txt\" echo(%*",
                    "findstr \"^\" >\"%~dp0stdin.txt\"",
                    "if exist \"%~dp0stdout.txt\" type \"%~dp0stdout.txt\"",
                    "set \"code=0\"",
                    "if exist \"%~dp0exit.txt\" set /p code=<\"%~dp0exit.txt\"",
                    "exit /b %code%",
                    string.Empty));
        }
        else
        {
            var shim = Path.Combine(ShimDirectory, "docker");
            File.WriteAllText(
                shim,
                string.Join(
                    "\n",
                    "#!/bin/sh",
                    "here=$(dirname \"$0\")",
                    "printf '%s\\n' \"$*\" >> \"$here/invocations.txt\"",
                    "cat > \"$here/stdin.txt\"",
                    "if [ -f \"$here/stdout.txt\" ]; then cat \"$here/stdout.txt\"; fi",
                    "code=0",
                    "if [ -f \"$here/exit.txt\" ]; then code=$(cat \"$here/exit.txt\"); fi",
                    "exit \"$code\"",
                    string.Empty));
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
