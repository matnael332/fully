param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("whatif", "deploy")]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [string]$RuntimePrincipalObjectId,
    [string]$AllowedIpv4Address,
    [switch]$Approve
)

# The owner-run deployment path of the Gate 3G DEV/TEST harness vault (Stage B1; owner decisions B-2, B-3, B-4 and B-8,
# approved 2026-10-08, and B-9: the product owner is the infrastructure administrator and Azure resources stay in Bicep).
# It is the only path that previews or deploys infra/bicep/harness-vault.bicep, and it deploys nothing else: never
# main.bicep, never Dev or Synthetic.
#
#   whatif   previews the deployment, read-only.
#   deploy   creates or updates the harness resource group, vault, audit workspace and runtime role and, when
#            -RuntimePrincipalObjectId is given, the runtime role's one assignment at the vault. The Azure CLI shows
#            the what-if and asks for confirmation before anything changes.
#
# Both commands contact Azure, so both refuse without -Approve: without it the plan is shown and nothing is sent. The
# target is always explicit: -SubscriptionId has no default and is passed to every Azure CLI call, never taken from or
# changed in the signed-in session. This script never signs in, never changes the Azure CLI's selected subscription,
# never deletes anything, never takes or prints a secret, and runs only as a signed-in user, never as a service
# principal. Deployments are incremental: a resource absent from the template is never deleted. Every refusal is one
# printed line and a non-zero exit, and nothing has been sent. Nothing in the repository runs this script: running it
# is a separately approved step.
#
#   whatif|deploy -SubscriptionId <guid> [-RuntimePrincipalObjectId <guid>] [-AllowedIpv4Address <public IPv4>] [-Approve]

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Template = Join-Path $Root "infra/bicep/harness-vault.bicep"
$Location = "northeurope"
$ResourceGroup = "rg-wce-harness-neu"

# Every pattern is applied case-sensitively and anchored at both ends; \z also refuses a trailing line break.
$GuidPattern = '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z'
$Ipv4Pattern = '\A(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z'
$NilGuid = "00000000-0000-0000-0000-000000000000"

function Stop-Run([string]$Message) {
    # One line for the operator, unwrapped, and then the error that ends the run with a non-zero exit code.
    Write-Host $Message -ForegroundColor Red
    throw $Message
}

function Assert-Guid([string]$Name, [string]$Value) {
    if (-not $Value -or $Value -cnotmatch $GuidPattern -or $Value -eq $NilGuid) {
        Stop-Run "REFUSED: -$Name must be a lower-case GUID other than the nil GUID. Nothing was sent to Azure."
    }
}

function Test-PublicIpv4([string]$Address) {
    # The firewall admits public IPv4 addresses only: no private, shared, loopback, link-local, multicast or reserved one.
    $octets = @($Address.Split(".") | ForEach-Object { [int]$_ })
    $first = $octets[0]
    $second = $octets[1]
    if ($first -eq 0 -or $first -eq 10 -or $first -eq 127 -or $first -ge 224) {
        return $false
    }

    if (($first -eq 100 -and $second -ge 64 -and $second -le 127) -or ($first -eq 169 -and $second -eq 254) -or
        ($first -eq 172 -and $second -ge 16 -and $second -le 31) -or ($first -eq 192 -and $second -eq 168)) {
        return $false
    }

    return $true
}

function Invoke-AzureCli([string[]]$Arguments) {
    # One Azure CLI call, its answer returned. The Azure CLI's own messages go to the console, not into the answer.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = & az @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return @{ Code = $code; Text = (@($lines) -join "`n").Trim() }
}

Assert-Guid "SubscriptionId" $SubscriptionId
if ($PSBoundParameters.ContainsKey("RuntimePrincipalObjectId")) {
    Assert-Guid "RuntimePrincipalObjectId" $RuntimePrincipalObjectId
}

if ($PSBoundParameters.ContainsKey("AllowedIpv4Address")) {
    if (-not $AllowedIpv4Address -or $AllowedIpv4Address -cnotmatch $Ipv4Pattern -or -not (Test-PublicIpv4 $AllowedIpv4Address)) {
        Stop-Run "REFUSED: -AllowedIpv4Address must be one public IPv4 address in dotted-decimal form, without a prefix length. Nothing was sent to Azure."
    }
}

if (-not (Test-Path -LiteralPath $Template -PathType Leaf)) {
    Stop-Run "REFUSED: infra/bicep/harness-vault.bicep was not found. Nothing was sent to Azure."
}

$principal = "none: no role assignment"
$parameters = @()
if ($RuntimePrincipalObjectId) {
    $principal = $RuntimePrincipalObjectId
    $parameters += "runtimePrincipalObjectId=$RuntimePrincipalObjectId"
}

$admitted = "none: every data-plane request is refused"
if ($AllowedIpv4Address) {
    $admitted = $AllowedIpv4Address
    $parameters += "allowedIpv4Address=$AllowedIpv4Address"
}

Write-Host "HARNESS VAULT PLAN ($Command)" -ForegroundColor Cyan
Write-Host "  template          : infra/bicep/harness-vault.bicep"
Write-Host "  subscription      : $SubscriptionId"
Write-Host "  resource group    : $ResourceGroup ($Location)"
Write-Host "  runtime principal : $principal"
Write-Host "  firewall          : default Deny, no trusted-service bypass; admitted address: $admitted"
Write-Host "  deletes           : nothing (incremental deployment)"

if (-not $Approve) {
    Stop-Run "REFUSED: $Command contacts Azure and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent to Azure."
}

if (-not (Get-Command "az" -ErrorAction SilentlyContinue)) {
    Stop-Run "REFUSED: the required command 'az' was not found. Nothing was sent to Azure."
}

# Read from the Azure CLI's own session, which this script never changes: the subscription must already be available
# to a signed-in user.
$session = Invoke-AzureCli @("account", "show", "--subscription", $SubscriptionId, "--output", "json")
$account = $null
if ($session.Code -eq 0 -and $session.Text.Length -gt 0) {
    try {
        $account = $session.Text | ConvertFrom-Json
    }
    catch {
        $account = $null
    }
}

if ($null -eq $account -or $account.id -cne $SubscriptionId) {
    Stop-Run "REFUSED: the Azure CLI is not signed in, or subscription $SubscriptionId is not available to it. Sign in yourself; this script never signs in. Nothing was sent to Azure."
}

if ($null -eq $account.user -or $account.user.type -cne "user") {
    Stop-Run "REFUSED: the Azure CLI is not signed in as a user. This script runs only as the product owner's own account. Nothing was sent to Azure."
}

Write-Host "  target            : $($account.name) ($($account.id)), tenant $($account.tenantId)" -ForegroundColor Cyan

if ($Command -eq "whatif") {
    $arguments = @(
        "deployment", "sub", "what-if",
        "--name", "wce-harness-whatif",
        "--location", $Location,
        "--template-file", $Template,
        "--subscription", $SubscriptionId
    )
}
else {
    # --confirm-with-what-if shows the change set and asks before anything is created, so the approval is given against
    # the actual planned changes.
    $arguments = @(
        "deployment", "sub", "create",
        "--name", ("wce-harness-" + (Get-Date -Format "yyyyMMdd-HHmmss")),
        "--location", $Location,
        "--template-file", $Template,
        "--subscription", $SubscriptionId,
        "--confirm-with-what-if"
    )
}

if ($parameters.Count -gt 0) {
    $arguments += "--parameters"
    $arguments += $parameters
}

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & az @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
}

if ($Command -eq "whatif") {
    if ($exitCode -ne 0) {
        Stop-Run "WHATIF: NOT COMPLETED (Azure CLI exit $exitCode). Nothing was deployed."
    }

    Write-Host "WHATIF: COMPLETE (preview only; nothing was deployed)" -ForegroundColor Green
}
else {
    if ($exitCode -ne 0) {
        Stop-Run "DEPLOY: NOT COMPLETED (Azure CLI exit $exitCode): the deployment failed or was declined."
    }

    Write-Host "DEPLOY: COMPLETE ($ResourceGroup)" -ForegroundColor Green
}
