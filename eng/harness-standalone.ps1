param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("seed", "evidence")]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [string]$TenantId,
    [string]$UserId,
    [string]$IdentityProvider,
    [string]$ProviderSubject,
    [switch]$Approve
)

# The owner-run tool of the future persistent standalone harness database (Gate 3G Stage B3). On 2026-10-06 the product
# owner approved it as the one supported entry point to two reviewed payloads, which are not entry points themselves,
# together with the clarification of owner decision B-14 that the seed prepares only one fresh tenant and one fresh
# canonical user:
#
#   seed      runs eng/harness-standalone-seed.sql as the local schema owner: one fresh tenant and one fresh canonical
#             user, and nothing else. It never seeds a monday connection, a connector authorization, an OAuth
#             transaction, token material, a PKCE verifier or a delegated provider identity; the single fresh monday
#             connection is created later by the product's own Connect lifecycle, during a separately approved run.
#             Without -Approve it shows the plan and the payload's SHA-256 and runs nothing.
#   evidence  runs eng/harness-standalone-evidence.sql as the runtime role, read-only, and succeeds only when every check
#             of the procedure is reported exactly once, and as PASS.
#
# The target is always explicit: -Database has no default, is never taken from .env, must match the approved standalone
# naming pattern, and is refused when it is the shared suite database that .env names. Of .env this tool uses only
# POSTGRES_DB, to refuse it, and POSTGRES_USER, the local schema owner's role name. psql runs only inside the local
# compose PostgreSQL service, over its local socket, with every value passed as an argument of its own; no command line
# is ever built from text. This tool never creates or drops a database, never runs a migration, never creates, alters,
# grants or revokes a role or a privilege, never takes or prints a password, a connection string, a token or any other
# secret, and prints only closed values. Every refusal is one printed line and a non-zero exit, and nothing has run.
# Nothing in the repository runs this tool: running it is a separately approved step.
#
#   seed      -Database <name> -TenantId <guid> -UserId <guid> -IdentityProvider <key> -ProviderSubject <subject> [-Approve]
#   evidence  -Database <name> -TenantId <guid>

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $Root "infra/local/docker-compose.yml"
$EnvFile = Join-Path $Root ".env"
$SeedPayload = Join-Path $PSScriptRoot "harness-standalone-seed.sql"
$EvidencePayload = Join-Path $PSScriptRoot "harness-standalone-evidence.sql"

# Every pattern is applied case-sensitively and anchored at both ends; \z also refuses a trailing line break. The
# database and value rules are the payloads' own; the subject rule is stricter, so that nothing in a value could mean
# anything to a shell or to psql.
$DatabasePattern = '\Awce_harness_[a-z0-9_]{1,51}\z'
$GuidPattern = '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z'
$IdentityProviderPattern = '\A[a-z0-9._-]{1,64}\z'
$ProviderSubjectPattern = '\A[A-Za-z0-9._-]{1,200}\z'
$RoleNamePattern = '\A[a-z_][a-z0-9_]{0,62}\z'
$NilGuid = "00000000-0000-0000-0000-000000000000"
$RuntimeRole = "workcontinuity_app"

# Every check the evidence procedure reports, in its order. Each must be reported exactly once, as PASS.
$EvidenceChecks = @(
    "transaction.read_only",
    "identity.expected_database",
    "identity.standalone_harness_database_name",
    "identity.session_user_is_current_user",
    "identity.current_user_is_runtime_role",
    "role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit",
    "role.owns_no_table_view_or_sequence",
    "role.owns_neither_database_nor_schema",
    "role.member_of_no_role",
    "rls.every_tenant_table_enabled_and_forced",
    "rls.only_the_migration_history_has_no_tenant",
    "rls.every_tenant_table_has_its_isolation_policy",
    "rls.no_other_permissive_policy_applies_to_the_runtime_role",
    "r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update",
    "r3b2.initial_sync_tables_exactly_insert_select",
    "r3b2.initial_sync_update_columns_exact",
    "r3b2.current_work_views_select_only",
    "runtime.no_administrative_path_beyond_the_approved_functions",
    "isolation.without_tenant_no_row_is_visible",
    "isolation.fresh_tenant_sees_no_other_tenant_row",
    "isolation.fresh_tenant_present_once_with_one_active_user",
    "isolation.other_tenant_sees_no_fresh_tenant_row"
)

# The evidence procedure switches the tenant context three times, and psql answers each switch with a single t.
$TenantContextSteps = 3

function Stop-Run([string]$Message) {
    # One line for the operator, unwrapped, and then the error that ends the run with a non-zero exit code.
    Write-Host $Message -ForegroundColor Red
    throw $Message
}

function Read-DotEnvValue([string]$Key) {
    # One key of .env, or null. Nothing read here is ever printed.
    $value = $null
    foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) {
        $trimmed = $line.Trim()
        $separator = $trimmed.IndexOf("=")
        if ($trimmed.Length -gt 0 -and -not $trimmed.StartsWith("#") -and $separator -gt 0) {
            if ($trimmed.Substring(0, $separator).Trim() -ceq $Key) {
                $value = $trimmed.Substring($separator + 1).Trim()
            }
        }
    }

    return $value
}

function Assert-Target {
    # The shared suite database is refused first, whatever its name looks like.
    if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
        Stop-Run "REFUSED: .env was not found, so the shared suite database cannot be identified and refused. Nothing was run."
    }

    $suiteDatabase = Read-DotEnvValue "POSTGRES_DB"
    if (-not $suiteDatabase) {
        Stop-Run "REFUSED: POSTGRES_DB is not set in .env, so the shared suite database cannot be identified and refused. Nothing was run."
    }

    # Compared without regard to case on purpose: no spelling of the shared suite database is ever a target.
    if ($Database -eq $suiteDatabase) {
        Stop-Run "REFUSED: -Database names the shared suite database, which is never a target of this tool. Nothing was run."
    }

    if ($Database -cnotmatch $DatabasePattern) {
        Stop-Run "REFUSED: -Database must be the exact name of a standalone harness database: wce_harness_ and then 1 to 51 of a-z, 0-9 and _. Nothing was run."
    }
}

function Assert-Guid([string]$Name, [string]$Value) {
    if (-not $Value -or $Value -cnotmatch $GuidPattern -or $Value -eq $NilGuid) {
        Stop-Run "REFUSED: -$Name must be a lower-case GUID other than the nil GUID. Nothing was run."
    }
}

function Assert-LocalEngine {
    # Only the local Docker engine and the local stack exactly as ./eng/dev.ps1 addresses it: no other engine, no other
    # context and no other compose project.
    foreach ($variable in @("DOCKER_HOST", "DOCKER_CONTEXT", "COMPOSE_PROJECT_NAME")) {
        if ([Environment]::GetEnvironmentVariable($variable)) {
            Stop-Run "REFUSED: $variable is set, so the target might not be the local stack. This tool runs only against the local compose PostgreSQL service. Nothing was run."
        }
    }

    if (-not (Test-Path -LiteralPath $ComposeFile -PathType Leaf)) {
        Stop-Run "REFUSED: infra/local/docker-compose.yml was not found. Nothing was run."
    }

    if (-not (Get-Command "docker" -ErrorAction SilentlyContinue)) {
        Stop-Run "REFUSED: the required command 'docker' was not found. Nothing was run."
    }
}

function Read-Payload([string]$Path) {
    # The bytes hashed are the bytes run.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-Run "REFUSED: the payload eng/$(Split-Path -Leaf $Path) was not found. Nothing was run."
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $sha256.Dispose()
    }

    return @{ Name = "eng/$(Split-Path -Leaf $Path)"; Hash = $hash; Text = [System.Text.Encoding]::UTF8.GetString($bytes) }
}

# psql reads the payload from its standard input, as UTF-8 without a byte order mark.
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

Assert-Target

switch ($Command) {
    "seed" {
        Assert-Guid "TenantId" $TenantId
        Assert-Guid "UserId" $UserId
        if ($TenantId -eq $UserId) {
            Stop-Run "REFUSED: -TenantId and -UserId must differ. Nothing was run."
        }

        if (-not $IdentityProvider -or $IdentityProvider -cnotmatch $IdentityProviderPattern) {
            Stop-Run "REFUSED: -IdentityProvider must be 1 to 64 of a-z, 0-9, '.', '_' and '-'. Nothing was run."
        }

        if (-not $ProviderSubject -or $ProviderSubject -cnotmatch $ProviderSubjectPattern) {
            Stop-Run "REFUSED: -ProviderSubject must be a synthetic subject of 1 to 200 of A-Z, a-z, 0-9, '.', '_' and '-'. Nothing was run."
        }

        # The schema owner: the one local role that sees every row, so that the payload can prove the target empty.
        $owner = Read-DotEnvValue "POSTGRES_USER"
        if (-not $owner -or $owner -cnotmatch $RoleNamePattern -or $owner -ceq $RuntimeRole) {
            Stop-Run "REFUSED: POSTGRES_USER in .env must name the local schema owner, which is not the runtime role. Nothing was run."
        }

        $payload = Read-Payload $SeedPayload
        Write-Host "STANDALONE SEED PLAN" -ForegroundColor Cyan
        Write-Host "  target database   : $Database"
        Write-Host "  tenant id         : $TenantId"
        Write-Host "  canonical user id : $UserId"
        Write-Host "  identity provider : $IdentityProvider"
        Write-Host "  provider subject  : $ProviderSubject"
        Write-Host "  payload           : $($payload.Name)"
        Write-Host "  payload SHA-256   : $($payload.Hash)"
        Write-Host "  writes            : one tenant and one canonical user, in one transaction; no connection, no authorization"
        Write-Host "  runs as           : the local schema owner, inside the local compose PostgreSQL service"

        if (-not $Approve) {
            Stop-Run "REFUSED: seeding requires -Approve. Review the plan above, then re-run with -Approve. Nothing was written."
        }

        Assert-LocalEngine
        $arguments = @(
            "compose", "--env-file", $EnvFile, "-f", $ComposeFile,
            "exec", "-T", "postgres",
            "psql", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--quiet",
            "--username", $owner, "--dbname", $Database,
            "--set", "target_database=$Database",
            "--set", "tenant_id=$TenantId",
            "--set", "user_id=$UserId",
            "--set", "identity_provider=$IdentityProvider",
            "--set", "provider_subject=$ProviderSubject",
            "--file", "-"
        )
        $payload.Text | & docker @arguments
        if ($LASTEXITCODE -ne 0) {
            Stop-Run "SEED: NOT COMPLETED (psql exit $LASTEXITCODE). The payload writes in one transaction, which commits only if it completes."
        }

        Write-Host "SEED: COMPLETE (one tenant, one canonical user; no connection, no authorization)" -ForegroundColor Green
    }
    "evidence" {
        Assert-Guid "TenantId" $TenantId
        foreach ($seedValue in @("UserId", "IdentityProvider", "ProviderSubject")) {
            if ($PSBoundParameters.ContainsKey($seedValue)) {
                Stop-Run "REFUSED: -$seedValue is a seed value; evidence takes only -Database and -TenantId. Nothing was run."
            }
        }

        if ($Approve) {
            Stop-Run "REFUSED: evidence is read-only and takes no -Approve. Nothing was run."
        }

        Assert-LocalEngine
        $payload = Read-Payload $EvidencePayload
        Write-Host "STANDALONE EVIDENCE (read-only, as the runtime role)" -ForegroundColor Cyan
        Write-Host "  target database   : $Database"
        Write-Host "  tenant id         : $TenantId"
        Write-Host "  payload           : $($payload.Name)"
        Write-Host "  payload SHA-256   : $($payload.Hash)"

        $arguments = @(
            "compose", "--env-file", $EnvFile, "-f", $ComposeFile,
            "exec", "-T",
            "-e", "PGOPTIONS=-c wce_evidence.expected_database=$Database -c wce_evidence.tenant_id=$TenantId",
            "postgres",
            "psql", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--quiet", "--no-align", "--tuples-only",
            "--username", $RuntimeRole, "--dbname", $Database,
            "--file", "-"
        )
        $answer = @($payload.Text | & docker @arguments)
        $exitCode = $LASTEXITCODE

        # Only closed values are taken from the answer, and only they are printed: anything else is counted, never shown.
        $statuses = @{}
        $observed = @{}
        $reported = New-Object System.Collections.Generic.List[string]
        $steps = 0
        $withheld = 0
        foreach ($entry in $answer) {
            $line = ([string]$entry).Trim()
            if ($line.Length -eq 0) {
                continue
            }

            if ($line -ceq "t") {
                $steps++
                continue
            }

            $match = [regex]::Match($line, '\A([a-z0-9_]+\.[a-z0-9_]+)\|(PASS|FAIL)\|([0-9]{0,18})\z')
            if (-not $match.Success) {
                $withheld++
                continue
            }

            $check = $match.Groups[1].Value
            $reported.Add($check)
            if ($statuses.ContainsKey($check)) {
                $statuses[$check] = "REPEATED"
            }
            else {
                $statuses[$check] = $match.Groups[2].Value
                $observed[$check] = $match.Groups[3].Value
            }
        }

        $problems = @()
        if ($exitCode -ne 0) {
            $problems += "psql exit $exitCode"
        }

        if ($withheld -gt 0) {
            $problems += "$withheld unexpected line(s), withheld"
        }

        if ($steps -ne $TenantContextSteps) {
            $problems += "$steps tenant-context step(s) instead of $TenantContextSteps"
        }

        if (($reported -join ",") -cne ($EvidenceChecks -join ",")) {
            $problems += "the reported checks are not exactly the procedure's, each once and in order"
        }

        foreach ($expected in $EvidenceChecks) {
            $status = "MISSING"
            if ($statuses.ContainsKey($expected)) {
                $status = $statuses[$expected]
            }

            if ($status -cne "PASS") {
                $problems += "$expected is $status"
            }

            $count = ""
            if ($observed.ContainsKey($expected) -and $observed[$expected]) {
                $count = " (" + $observed[$expected] + ")"
            }

            Write-Host ("  {0,-70} {1}{2}" -f $expected, $status, $count)
        }

        if ($problems.Count -gt 0) {
            Stop-Run ("EVIDENCE: NOT ESTABLISHED. " + ($problems -join "; ") + ".")
        }

        Write-Host "EVIDENCE: ESTABLISHED ($($EvidenceChecks.Count) of $($EvidenceChecks.Count) checks PASS)" -ForegroundColor Green
    }
}
