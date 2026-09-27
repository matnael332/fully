param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("build", "lint", "validate", "whatif", "deploy")]
    [string]$Command,

    # Required for anything that talks to Azure. Selects the parameter file and the data boundary.
    [ValidateSet("dev", "synthetic")]
    [string]$Environment,

    # Required for anything that talks to Azure, so the target is always stated, never inherited from
    # whatever `az login` happened to leave selected.
    [string]$SubscriptionId,

    # `deploy` refuses to run without this. Typing it is the explicit approval step.
    [switch]$Approve
)

# Azure infrastructure commands.
#
#   build     compile every entry point and parameter file          (local, no Azure)
#   lint      lint every entry point                                (local, no Azure)
#   validate  ask Azure to validate the deployment                  (read-only, needs login)
#   whatif    show what a deployment would change                   (read-only, needs login)
#   deploy    create or update the environment                      (needs login AND -Approve)
#
# Deployments are incremental and subscription-scoped: they create or update the resources in the
# template and never delete resources that are absent from it. Complete mode is not offered here.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$BicepRoot = Join-Path $Root "infra/bicep"
$MainTemplate = Join-Path $BicepRoot "main.bicep"
# Location of the subscription-level deployment metadata. Resource locations come from the parameter files.
$Location = "northeurope"

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. Install the Azure CLI, then run 'az bicep install'."
    }
}

function Get-ParameterFile([string]$EnvironmentName) {
    if (-not $EnvironmentName) {
        throw "-Environment dev|synthetic is required for this command."
    }
    return Join-Path $BicepRoot "parameters/main.$EnvironmentName.bicepparam"
}

function Require-Secrets([string]$EnvironmentName) {
    # The parameter files read these at deployment time. They are checked here so a missing value
    # fails before any request reaches Azure, rather than surfacing as an opaque deployment error.
    $prefix = "WCE_$($EnvironmentName.ToUpperInvariant())"
    foreach ($suffix in @("POSTGRES_ADMIN_PASSWORD", "POSTGRES_RUNTIME_ROLE_PASSWORD")) {
        $name = "${prefix}_${suffix}"
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not $value -or $value.Length -lt 12) {
            throw "Environment variable $name must be set to a strong value (12+ characters) before '$Command'. It is never stored in the repository."
        }
    }
}

function Require-Subscription {
    if (-not $SubscriptionId) {
        throw "-SubscriptionId is required for this command, so the target is always explicit."
    }

    $account = az account show --output json 2>$null | ConvertFrom-Json
    if (-not $account) {
        throw "Not logged in to Azure. Run 'az login' and re-run. Nothing was sent to Azure."
    }

    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) { throw "Could not select subscription $SubscriptionId." }

    $selected = az account show --output json | ConvertFrom-Json
    Write-Host ""
    Write-Host "Target subscription : $($selected.name) ($($selected.id))" -ForegroundColor Cyan
    Write-Host "Target tenant       : $($selected.tenantId)" -ForegroundColor Cyan
    Write-Host "Target environment  : $Environment" -ForegroundColor Cyan
    Write-Host "Target region       : $Location" -ForegroundColor Cyan
    Write-Host ""
}

function Protect-Images([string]$EnvironmentName) {
    # Image-drift guard (Gate 1F). The parameter file reads the Web, Worker and migration-job images from
    # WCE_<ENV>_WEB_IMAGE, WCE_<ENV>_WORKER_IMAGE and WCE_<ENV>_MIGRATION_JOB_IMAGE, defaulting to the
    # placeholder. When a variable is unset and the live resource already runs a real (non-placeholder)
    # image, the live image is adopted for this process, so a routine redeploy can never silently revert
    # an app or the job to the placeholder. An explicitly set variable always wins. Runs after
    # Require-Subscription because it reads live state.
    $prefix = "WCE_$($EnvironmentName.ToUpperInvariant())"
    $placeholder = 'mcr.microsoft.com/k8se/quickstart'

    $compiled = az bicep build-params --file (Get-ParameterFile $EnvironmentName) --stdout | ConvertFrom-Json
    $parameters = ($compiled.parametersJson | ConvertFrom-Json).parameters
    $token = $parameters.regionToken.value
    $resourceGroup = "rg-wce-$EnvironmentName-$token"
    $base = "wce-$EnvironmentName-$token"

    # Variable suffix -> Microsoft.App resource (type/name). Apps and jobs share the template.containers shape.
    $targets = [ordered]@{
        WEB_IMAGE           = "containerApps/ca-$base-web"
        WORKER_IMAGE        = "containerApps/ca-$base-worker"
        MIGRATION_JOB_IMAGE = "jobs/caj-$base-migrate"
    }

    foreach ($entry in $targets.GetEnumerator()) {
        $variable = "${prefix}_$($entry.Key)"
        $resource = $entry.Value
        $name = $resource.Split('/')[-1]
        $live = az rest --method get --url "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.App/${resource}?api-version=2024-03-01" --query 'properties.template.containers[0].image' --output tsv 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $live) {
            Write-Host "Image guard : $name not deployed yet; $variable left as is" -ForegroundColor Cyan
            continue
        }
        $explicit = [Environment]::GetEnvironmentVariable($variable)
        if ($explicit) {
            Write-Host "Image guard : $variable is set explicitly ($explicit); live image is $live" -ForegroundColor Cyan
        }
        elseif ($live -like "$placeholder*") {
            Write-Host "Image guard : $name runs the placeholder; $variable unset, placeholder stays" -ForegroundColor Cyan
        }
        else {
            [Environment]::SetEnvironmentVariable($variable, $live)
            Write-Host "Image guard : $variable unset; adopting live image $live so this deployment cannot revert $name" -ForegroundColor Yellow
        }
    }
}

function Invoke-Build {
    Write-Host "Building $MainTemplate"
    az bicep build --file $MainTemplate --stdout | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "bicep build failed for main.bicep" }

    foreach ($parameterFile in Get-ChildItem (Join-Path $BicepRoot "parameters") -Filter *.bicepparam) {
        Write-Host "Building $($parameterFile.FullName)"
        az bicep build-params --file $parameterFile.FullName --stdout | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "bicep build-params failed for $($parameterFile.Name)" }
    }

    Write-Host "BICEP BUILD: PASS" -ForegroundColor Green
}

function Invoke-Lint {
    Write-Host "Linting $MainTemplate"
    az bicep lint --file $MainTemplate
    if ($LASTEXITCODE -ne 0) { throw "bicep lint failed for main.bicep" }
    Write-Host "BICEP LINT: PASS" -ForegroundColor Green
}

Require-Command "az"

switch ($Command) {
    "build" {
        Invoke-Build
    }
    "lint" {
        Invoke-Lint
    }
    "validate" {
        $parameterFile = Get-ParameterFile $Environment
        Require-Secrets $Environment
        Require-Subscription
        Protect-Images $Environment

        # Server-side validation only. Creates nothing.
        az deployment sub validate `
            --name "wce-$Environment-validate" `
            --location $Location `
            --template-file $MainTemplate `
            --parameters $parameterFile
        if ($LASTEXITCODE -ne 0) { throw "Azure validation failed" }
        Write-Host "AZURE VALIDATE: PASS" -ForegroundColor Green
    }
    "whatif" {
        $parameterFile = Get-ParameterFile $Environment
        Require-Secrets $Environment
        Require-Subscription
        Protect-Images $Environment

        # Read-only preview of the changes a deployment would make. Creates nothing.
        az deployment sub what-if `
            --name "wce-$Environment-whatif" `
            --location $Location `
            --template-file $MainTemplate `
            --parameters $parameterFile
        if ($LASTEXITCODE -ne 0) { throw "Azure what-if failed" }
    }
    "deploy" {
        $parameterFile = Get-ParameterFile $Environment
        Require-Secrets $Environment
        Require-Subscription
        Protect-Images $Environment

        if (-not $Approve) {
            throw "Deployment requires -Approve. Run 'whatif' first, review it, then re-run with -Approve. Nothing was deployed."
        }

        Write-Host "Deploying '$Environment' to the subscription above. Incremental mode: nothing is deleted." -ForegroundColor Yellow

        # --confirm-with-what-if shows the change set and asks for interactive confirmation before
        # anything is created, so the approval is given against the actual planned changes.
        az deployment sub create `
            --name "wce-$Environment-$(Get-Date -Format 'yyyyMMdd-HHmmss')" `
            --location $Location `
            --template-file $MainTemplate `
            --parameters $parameterFile `
            --confirm-with-what-if
        if ($LASTEXITCODE -ne 0) { throw "Azure deployment failed or was declined" }
        Write-Host "AZURE DEPLOY ($Environment): COMPLETE" -ForegroundColor Green
    }
}
