param(
    [ValidateSet("migrate", "status", "reset")]
    [string]$Command = "status"
)

# Local database operations.
#
# `migrate` and `status` are safe anywhere. `reset` destroys the local PostgreSQL volume and is
# guarded accordingly: schema change for the Human Pilot and production is an explicit deployment
# step, and nothing here is intended to run against a shared database.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $Root "infra/local/docker-compose.yml"
$EnvFile = Join-Path $Root ".env"
$InfrastructureProject = Join-Path $Root "src/App.Infrastructure"

function Read-DotEnv([string]$Path) {
    if (-not (Test-Path $Path)) {
        throw ".env does not exist. Run ./eng/dev.ps1 up first to create it from .env.example."
    }

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

function Get-AdminConnectionString {
    $envValues = Read-DotEnv $EnvFile

    $dbHost = "localhost"
    $port = if ($envValues["POSTGRES_PORT"]) { $envValues["POSTGRES_PORT"] } else { "5432" }
    $database = $envValues["POSTGRES_DB"]
    $user = $envValues["POSTGRES_USER"]
    $password = $envValues["POSTGRES_PASSWORD"]

    if (-not $database -or -not $user -or -not $password) {
        throw "POSTGRES_DB, POSTGRES_USER and POSTGRES_PASSWORD must all be set in .env."
    }

    return "Host=$dbHost;Port=$port;Database=$database;Username=$user;Password=$password"
}

function Invoke-Ef([string[]]$EfArgs) {
    $env:ConnectionStrings__PostgresAdmin = Get-AdminConnectionString

    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

    # The command must precede its options, otherwise `dotnet ef` prints help and exits 0.
    $options = @(
        "--project", $InfrastructureProject
        "--startup-project", $InfrastructureProject
        "--context", "AdministrativeDbContext"
    )

    dotnet ef @EfArgs @options
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef $($EfArgs -join ' ') failed" }
}

switch ($Command) {
    "migrate" {
        # Applies pending migrations only. Never drops or recreates anything.
        Invoke-Ef @("database", "update")
        Write-Host "DATABASE MIGRATE: PASS" -ForegroundColor Green
    }
    "status" {
        Invoke-Ef @("migrations", "list")
    }
    "reset" {
        Write-Host "This destroys the LOCAL PostgreSQL volume and all local data." -ForegroundColor Yellow

        docker compose --env-file $EnvFile -f $ComposeFile down
        if ($LASTEXITCODE -ne 0) { throw "docker compose down failed" }

        # Named explicitly so this can never remove a volume belonging to another project.
        docker volume rm work-continuity-local_postgres-data
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Volume was already absent." -ForegroundColor DarkGray
        }

        Write-Host "LOCAL DATABASE RESET: done. Run ./eng/dev.ps1 up to recreate it." -ForegroundColor Green
    }
}
