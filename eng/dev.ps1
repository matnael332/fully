param(
    [ValidateSet("up", "check", "down", "logs")]
    [string]$Command = "up"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $Root "infra/local/docker-compose.yml"
$EnvFile = Join-Path $Root ".env"
$ExampleEnv = Join-Path $Root ".env.example"

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. Install Docker Desktop and ensure Docker Compose is available."
    }
}

function Read-DotEnv([string]$Path) {
    $values = @{}
    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
            $parts = $line.Split("=", 2)
            $values[$parts[0].Trim()] = $parts[1].Trim()
        }
    }
    return $values
}

function Validate-LocalSecrets {
    if (-not (Test-Path $EnvFile)) {
        Copy-Item $ExampleEnv $EnvFile
        throw "Created .env from .env.example. Replace placeholder passwords and set ACCEPT_EULA=Y after reviewing the Microsoft emulator/SQL Server license terms, then run again."
    }

    $envValues = Read-DotEnv $EnvFile
    if ($envValues["ACCEPT_EULA"] -ne "Y") {
        throw "ACCEPT_EULA must be explicitly set to Y in .env before starting the Service Bus emulator."
    }

    foreach ($key in @("POSTGRES_PASSWORD", "POSTGRES_APP_PASSWORD", "MSSQL_SA_PASSWORD")) {
        $value = $envValues[$key]
        if (-not $value -or $value.Contains("CHANGE_ME")) {
            throw "$key still contains a placeholder. Use a local-only strong password in .env."
        }
    }
}

function Wait-Http([string]$Uri, [string]$Name, [int]$Attempts = 30, [int]$DelaySeconds = 2) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            if ($i -eq $Attempts) { throw }
        }
        Start-Sleep -Seconds $DelaySeconds
    }
    throw "$Name did not become healthy at $Uri"
}

Require-Command "docker"

switch ($Command) {
    "up" {
        Validate-LocalSecrets
        docker compose --env-file $EnvFile -f $ComposeFile up -d --build
        if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
        & $PSCommandPath check
    }
    "check" {
        Validate-LocalSecrets
        $envValues = Read-DotEnv $EnvFile
        docker compose --env-file $EnvFile -f $ComposeFile ps
        if ($LASTEXITCODE -ne 0) { throw "docker compose ps failed" }

        $workerId = docker compose --env-file $EnvFile -f $ComposeFile ps -q worker
        if ($LASTEXITCODE -ne 0 -or -not $workerId) { throw "Worker container is not running" }

        # The one-shot migrator must have completed cleanly; compose gates web/worker on it, so a
        # non-zero exit here means the schema is not current.
        $migratorId = docker compose --env-file $EnvFile -f $ComposeFile ps -aq db-migrator
        if ($LASTEXITCODE -ne 0 -or -not $migratorId) { throw "Migration container did not run" }
        $migratorExit = docker inspect --format '{{.State.ExitCode}}' $migratorId
        if ($LASTEXITCODE -ne 0 -or $migratorExit -ne "0") {
            throw "Database migration failed (exit $migratorExit). Inspect: ./eng/dev.ps1 logs"
        }

        # The bootstrap service must also have completed cleanly: it is the same entry point the Azure
        # bootstrap job runs, so a failure here would be a failure there.
        $bootstrapId = docker compose --env-file $EnvFile -f $ComposeFile ps -aq db-bootstrap
        if ($LASTEXITCODE -ne 0 -or -not $bootstrapId) { throw "Bootstrap container did not run" }
        $bootstrapExit = docker inspect --format '{{.State.ExitCode}}' $bootstrapId
        if ($LASTEXITCODE -ne 0 -or $bootstrapExit -ne "0") {
            throw "Runtime-role bootstrap failed (exit $bootstrapExit). Inspect: ./eng/dev.ps1 logs"
        }

        $sbPort = if ($envValues["EMULATOR_HTTP_PORT"]) { $envValues["EMULATOR_HTTP_PORT"] } else { "5300" }
        $webPort = if ($envValues["WEB_HTTP_PORT"]) { $envValues["WEB_HTTP_PORT"] } else { "8080" }

        Wait-Http "http://localhost:$sbPort/health" "Service Bus emulator"
        Wait-Http "http://localhost:$webPort/health/ready" "Web readiness"

        Write-Host "LOCAL DEV CHECK: PASS" -ForegroundColor Green
        Write-Host "- Runtime role bootstrapped (same entry point as the Azure job)"
        Write-Host "- Database migrations applied"
        Write-Host "- PostgreSQL reachable through Web readiness"
        Write-Host "- Service Bus emulator healthy"
        Write-Host "- Web API ready"
        Write-Host "- Worker container running"
    }
    "down" {
        if (-not (Test-Path $EnvFile)) { throw ".env does not exist" }
        docker compose --env-file $EnvFile -f $ComposeFile down
    }
    "logs" {
        if (-not (Test-Path $EnvFile)) { throw ".env does not exist" }
        docker compose --env-file $EnvFile -f $ComposeFile logs -f --tail=200
    }
}
