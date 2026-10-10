param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("create", "add-credential", "remove-credential", "verify")]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [int]$ValidityHours,
    [switch]$Approve
)

# The owner-run identity script of the Gate 3G DEV/TEST harness (Stage B1; owner decision B-9, approved 2026-10-08, for
# the runtime identity of owner decision B-7: a certificate-credential service principal). It is the one supported way to
# create the harness's Entra application, its service principal and its certificate credential; Azure resources stay in
# Bicep. For this harness only, the product owner accepts this reviewed, versioned script as satisfying invariant 69.
#
#   create             finds or creates the single-tenant application and its service principal, then reads both back.
#   add-credential     creates a certificate with a non-exportable key in the current user's store and registers its
#                      public key as the application's only credential, valid for -ValidityHours (1 to 24: the run
#                      window), then reads both back.
#   remove-credential  removes every application credential and deletes the local certificate and its key, then reads
#                      both back. When to use it after a run belongs to the post-run procedure, a separate decision.
#   verify             reads the application and the service principal back and checks them, writing nothing.
#
# Every command contacts Microsoft Entra ID, so every command refuses without -Approve: without it the plan is shown and
# nothing is sent. The tenant is always explicit: -TenantId has no default, and the Azure CLI must already be signed in
# to that tenant, as a user; this script never signs in. It reaches Entra only through the Azure CLI (az account show,
# and az rest against Microsoft Graph v1.0), loads no Graph module and starts no other program. The application is
# single-tenant, with no API permission, redirect URI, app role, exposed API or client secret; the service principal has
# no credential of its own, no group membership, no directory role, no ownership and no permission grant. The
# certificate's private key is created non-exportable on this machine and never leaves it: only the public certificate
# is sent. Every write is read back, and the run fails when the result is not exactly as intended. Every refusal is one
# printed line and a non-zero exit: a refusal before -Approve sends nothing, and a refusal after it writes nothing.
# Nothing in the repository runs this script: running it is a separately approved step.
#
#   create|remove-credential|verify -TenantId <guid> [-Approve]
#   add-credential -TenantId <guid> -ValidityHours <1-24> [-Approve]

$ErrorActionPreference = "Stop"
$Graph = "https://graph.microsoft.com/v1.0"
$DisplayName = "wce-dev-harness-keyvault-runtime"
$SignInAudience = "AzureADMyOrg"
$Notes = "Gate 3G DEV/TEST harness runtime identity (owner decisions B-7 and B-9). Single-tenant; no API permission, redirect URI, app role or client secret."
$CertificateSubject = "CN=wce-dev-harness-keyvault-runtime"
$CertificateStore = "Cert:\CurrentUser\My"
$CredentialPrefix = "wce-dev-harness-keyvault-runtime-"
$KeyLength = 3072
$MaximumValidityHours = 24

# Every pattern is applied case-sensitively and anchored at both ends; \z also refuses a trailing line break.
$GuidPattern = '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z'
$NilGuid = "00000000-0000-0000-0000-000000000000"
$OnWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

function Stop-Run([string]$Message) {
    # One line for the operator, unwrapped, and then the error that ends the run with a non-zero exit code.
    Write-Host $Message -ForegroundColor Red
    throw $Message
}

function Invoke-AzureCli([string[]]$Arguments) {
    # One Azure CLI call. Its JSON answer, if any, is returned parsed; the Azure CLI's own messages go to the console,
    # never into the answer.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = & az @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $text = (@($lines) -join "`n").Trim()
    $json = $null
    $parsed = $false
    if ($code -eq 0 -and $text.Length -gt 0) {
        try {
            $json = $text | ConvertFrom-Json
            $parsed = $true
        }
        catch {
            $parsed = $false
        }
    }

    return @{ Code = $code; Json = $json; Parsed = $parsed; Empty = ($text.Length -eq 0) }
}

function Get-Graph([string]$Path, [string[]]$Query) {
    # A read from Microsoft Graph, which must answer with JSON.
    $arguments = @("rest", "--method", "get", "--url", "$Graph/$Path", "--output", "json")
    if ($Query.Count -gt 0) {
        $arguments += "--uri-parameters"
        $arguments += $Query
    }

    $answer = Invoke-AzureCli $arguments
    if ($answer.Code -ne 0 -or -not $answer.Parsed) {
        Stop-Run "STOPPED: reading $Path from Microsoft Graph failed (Azure CLI exit $($answer.Code)). The script is idempotent: re-run it."
    }

    return $answer.Json
}

function Get-GraphCollection([string]$Path, [string[]]$Query) {
    # The items of a collection, always as an array, even when there are none.
    $answer = Get-Graph $Path $Query
    if (-not ($answer.PSObject.Properties.Name -contains "value")) {
        Stop-Run "STOPPED: Microsoft Graph answered $Path without a value collection. The script is idempotent: re-run it."
    }

    return , @($answer.value)
}

function Send-Graph([string]$Method, [string]$Path, $Body) {
    # A write to Microsoft Graph. The body holds no secret: identifiers, names and at most a public certificate. It is
    # passed in a file only because Windows PowerShell cannot pass JSON quotes to a native command intact, and the file is
    # deleted as soon as the call returns.
    $file = Join-Path ([System.IO.Path]::GetTempPath()) ("wce-harness-identity-" + [System.Guid]::NewGuid().ToString("N") + ".json")
    [System.IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 6 -Compress), (New-Object System.Text.UTF8Encoding $false))
    try {
        $answer = Invoke-AzureCli @("rest", "--method", $Method, "--url", "$Graph/$Path", "--headers", "Content-Type=application/json", "--body", "@$file", "--output", "json")
    }
    finally {
        Remove-Item -LiteralPath $file -Force
    }

    if ($answer.Code -ne 0) {
        Stop-Run "STOPPED: writing $Path to Microsoft Graph failed (Azure CLI exit $($answer.Code)). The script is idempotent: re-run it."
    }

    return $answer.Json
}

function Get-Count($Value) {
    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function ConvertTo-UtcSecond($Value) {
    # A Graph date, which PowerShell 7 already converts and Windows PowerShell leaves as text, to the UTC second. A
    # missing date is the empty text, which never equals a real one.
    if ($null -eq $Value -or ([string]$Value).Length -eq 0) {
        return ""
    }

    if ($Value -is [DateTime]) {
        $instant = $Value.ToUniversalTime()
    }
    else {
        $instant = [DateTime]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]"AdjustToUniversal, AssumeUniversal")
    }

    return $instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Find-Application {
    # The one application with the harness's display name, or null; more than one is never guessed between.
    $found = Get-GraphCollection "applications" @("`$filter=displayName eq '$DisplayName'", "`$select=id,appId,displayName")
    if ($found.Count -gt 1) {
        Stop-Run "REFUSED: $($found.Count) applications are named $DisplayName. Resolve this by hand; nothing was written."
    }

    if ($found.Count -eq 1) {
        return $found[0]
    }

    return $null
}

function Find-ServicePrincipal([string]$ApplicationId) {
    $found = Get-GraphCollection "servicePrincipals" @("`$filter=appId eq '$ApplicationId'", "`$select=id,appId")
    if ($found.Count -gt 1) {
        Stop-Run "REFUSED: $($found.Count) service principals belong to application $ApplicationId. Resolve this by hand; nothing was written."
    }

    if ($found.Count -eq 1) {
        return $found[0]
    }

    return $null
}

function Read-State {
    # Everything the checks look at, read back from Microsoft Graph in a fixed order.
    $found = Find-Application
    if ($null -eq $found) {
        Stop-Run "NOT VERIFIED: no application is named $DisplayName. Run create first; nothing was written."
    }

    $application = Get-Graph "applications/$($found.id)" @()
    $foundPrincipal = Find-ServicePrincipal $application.appId
    if ($null -eq $foundPrincipal) {
        Stop-Run "NOT VERIFIED: application $($application.appId) has no service principal. Run create; nothing was written."
    }

    $principal = Get-Graph "servicePrincipals/$($foundPrincipal.id)" @()
    return @{
        Application = $application
        Principal = $principal
        MemberOf = Get-GraphCollection "servicePrincipals/$($principal.id)/transitiveMemberOf" @("`$select=id")
        Owned = Get-GraphCollection "servicePrincipals/$($principal.id)/ownedObjects" @("`$select=id")
        AppRoleAssignments = Get-GraphCollection "servicePrincipals/$($principal.id)/appRoleAssignments" @()
        PermissionGrants = Get-GraphCollection "servicePrincipals/$($principal.id)/oauth2PermissionGrants" @()
    }
}

function Test-State($State, [string]$CredentialRule, [string]$ExpectedCredentialName, [string]$ExpectedEnd) {
    # Each check by its closed name, PASS or FAIL. The credential rule is one of: at-most-one, none, exactly-expected.
    $application = $State.Application
    $principal = $State.Principal
    $credentials = @()
    if ($null -ne $application.keyCredentials) {
        $credentials = @($application.keyCredentials)
    }

    $redirectUris = (Get-Count $application.web.redirectUris) + (Get-Count $application.spa.redirectUris) + (Get-Count $application.publicClient.redirectUris)
    $checks = [ordered]@{
        "application.display_name" = ($application.displayName -ceq $DisplayName)
        "application.single_tenant" = ($application.signInAudience -ceq $SignInAudience)
        "application.no_api_permission" = ((Get-Count $application.requiredResourceAccess) -eq 0)
        "application.no_redirect_uri" = ($redirectUris -eq 0)
        "application.no_app_role" = ((Get-Count $application.appRoles) -eq 0)
        "application.no_exposed_api" = (((Get-Count $application.api.oauth2PermissionScopes) + (Get-Count $application.identifierUris)) -eq 0)
        "application.no_client_secret" = ((Get-Count $application.passwordCredentials) -eq 0)
        "service_principal.of_the_application" = ($principal.appId -ceq $application.appId)
        "service_principal.no_own_credential" = (((Get-Count $principal.passwordCredentials) + (Get-Count $principal.keyCredentials)) -eq 0)
        "service_principal.no_group_or_directory_role" = ($State.MemberOf.Count -eq 0)
        "service_principal.owns_nothing" = ($State.Owned.Count -eq 0)
        "service_principal.no_application_permission" = ($State.AppRoleAssignments.Count -eq 0)
        "service_principal.no_delegated_permission" = ($State.PermissionGrants.Count -eq 0)
    }

    $wellFormed = $true
    foreach ($credential in $credentials) {
        if ($credential.type -cne "AsymmetricX509Cert" -or $credential.usage -cne "Verify" -or -not ([string]$credential.displayName).StartsWith($CredentialPrefix, [System.StringComparison]::Ordinal)) {
            $wellFormed = $false
        }
    }

    switch ($CredentialRule) {
        "at-most-one" {
            $checks["application.credential_at_most_one_certificate"] = ($credentials.Count -le 1 -and $wellFormed)
        }
        "none" {
            $checks["application.credential_none"] = ($credentials.Count -eq 0)
        }
        "exactly-expected" {
            $exact = $credentials.Count -eq 1 -and $wellFormed -and ([string]$credentials[0].displayName -ceq $ExpectedCredentialName) -and
                ((ConvertTo-UtcSecond $credentials[0].endDateTime) -ceq $ExpectedEnd)
            $checks["application.credential_exactly_the_new_certificate"] = $exact
        }
    }

    return $checks
}

function Write-Checks($Checks, [string]$Label, [string]$Advice) {
    $failed = @()
    foreach ($name in $Checks.Keys) {
        $status = "FAIL"
        if ($Checks[$name]) {
            $status = "PASS"
        }
        else {
            $failed += $name
        }

        Write-Host ("  {0,-52} {1}" -f $name, $status)
    }

    if ($failed.Count -gt 0) {
        Stop-Run ("${Label}: NOT VERIFIED. Failed: " + ($failed -join ", ") + "." + $Advice)
    }
}

function Get-LocalCertificates {
    # The harness certificates in the current user's personal store, matched by their exact subject.
    return , @(Get-ChildItem -LiteralPath $CertificateStore | Where-Object { $_.Subject -ceq $CertificateSubject })
}

function Remove-LocalCertificate([string]$Thumbprint) {
    # The certificate and its key, by exact thumbprint, from the current user's personal store only.
    Remove-Item -LiteralPath (Join-Path $CertificateStore $Thumbprint) -DeleteKey
}

function Test-NonExportableKey($Certificate) {
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        return ($key -is [System.Security.Cryptography.RSACng]) -and ($key.KeySize -eq $KeyLength) -and
            ($key.Key.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None)
    }
    finally {
        if ($null -ne $key) {
            $key.Dispose()
        }
    }
}

# The values are checked first, the plan is shown, and nothing is sent without -Approve.
if (-not $TenantId -or $TenantId -cnotmatch $GuidPattern -or $TenantId -eq $NilGuid) {
    Stop-Run "REFUSED: -TenantId must be a lower-case GUID other than the nil GUID. Nothing was sent."
}

if ($Command -eq "add-credential") {
    if (-not $PSBoundParameters.ContainsKey("ValidityHours") -or $ValidityHours -lt 1 -or $ValidityHours -gt $MaximumValidityHours) {
        Stop-Run "REFUSED: add-credential needs -ValidityHours from 1 to $MaximumValidityHours, the run window. Nothing was sent."
    }
}
elseif ($PSBoundParameters.ContainsKey("ValidityHours")) {
    Stop-Run "REFUSED: -ValidityHours belongs to add-credential only. Nothing was sent."
}

Write-Host "HARNESS IDENTITY PLAN ($Command)" -ForegroundColor Cyan
Write-Host "  tenant              : $TenantId"
Write-Host "  application         : $DisplayName (single-tenant; no API permission, redirect URI, app role or client secret)"
switch ($Command) {
    "create" {
        Write-Host "  writes              : the application and its service principal, each only when missing"
    }
    "add-credential" {
        Write-Host "  writes              : a certificate with a non-exportable $KeyLength-bit RSA key in $CertificateStore, valid $ValidityHours hour(s)"
        Write-Host "                        its public key as the application's only credential; the private key never leaves this machine"
    }
    "remove-credential" {
        Write-Host "  writes              : removes every application credential, then deletes the local certificate and its key"
    }
    "verify" {
        Write-Host "  writes              : nothing"
    }
}

if (-not $Approve) {
    Stop-Run "REFUSED: $Command contacts Microsoft Entra ID and requires -Approve. Review the plan above, then re-run with -Approve. Nothing was sent."
}

if (($Command -eq "add-credential" -or $Command -eq "remove-credential") -and -not $OnWindows) {
    Stop-Run "REFUSED: $Command needs the Windows certificate store of the product owner's machine. Nothing was sent."
}

if (-not (Get-Command "az" -ErrorAction SilentlyContinue)) {
    Stop-Run "REFUSED: the required command 'az' was not found. Nothing was sent."
}

# Read from the Azure CLI's own session: it must already be signed in, as a user, to exactly this tenant.
$session = Invoke-AzureCli @("account", "show", "--output", "json")
if ($session.Code -ne 0 -or -not $session.Parsed) {
    Stop-Run "REFUSED: the Azure CLI is not signed in. Sign in yourself; this script never signs in. Nothing was sent."
}

if ($session.Json.tenantId -cne $TenantId) {
    Stop-Run "REFUSED: the Azure CLI is signed in to another tenant than -TenantId. Nothing was sent."
}

if ($null -eq $session.Json.user -or $session.Json.user.type -cne "user") {
    Stop-Run "REFUSED: the Azure CLI is not signed in as a user. This script runs only as the product owner's own account. Nothing was sent."
}

switch ($Command) {
    "create" {
        $application = Find-Application
        if ($null -eq $application) {
            $application = Send-Graph "post" "applications" ([ordered]@{ displayName = $DisplayName; signInAudience = $SignInAudience; notes = $Notes })
            if ($null -eq $application -or [string]$application.appId -cnotmatch $GuidPattern) {
                Stop-Run "STOPPED: Microsoft Graph did not answer the new application's id. Re-run create: it finds the application if it exists."
            }

            Write-Host "  application         : created"
        }

        $principal = Find-ServicePrincipal $application.appId
        if ($null -eq $principal) {
            $null = Send-Graph "post" "servicePrincipals" ([ordered]@{ appId = $application.appId })
            Write-Host "  service principal   : created"
        }

        $state = Read-State
        Write-Checks (Test-State $state "at-most-one" "" "") "CREATE" ""
        Write-Host "  application (client) id     : $($state.Application.appId)"
        Write-Host "  service principal object id : $($state.Principal.id)"
        Write-Host "CREATE: COMPLETE (verified by read-back)" -ForegroundColor Green
    }
    "verify" {
        $state = Read-State
        Write-Checks (Test-State $state "at-most-one" "" "") "VERIFY" ""
        Write-Host "  application (client) id     : $($state.Application.appId)"
        Write-Host "  service principal object id : $($state.Principal.id)"
        Write-Host "  application credentials     : $(Get-Count $state.Application.keyCredentials)"
        Write-Host "VERIFY: ESTABLISHED (read-back only; nothing was written)" -ForegroundColor Green
    }
    "add-credential" {
        $state = Read-State
        Write-Checks (Test-State $state "none" "" "") "ADD-CREDENTIAL" " Run remove-credential if the application already has a credential; nothing was written."
        if ((Get-LocalCertificates).Count -gt 0) {
            Stop-Run "REFUSED: a certificate for $CertificateSubject is already in $CertificateStore. Run remove-credential first. Nothing was written."
        }

        $now = Get-Date
        try {
            $certificate = New-SelfSignedCertificate -Type Custom -Subject $CertificateSubject -CertStoreLocation $CertificateStore `
                -KeyAlgorithm RSA -KeyLength $KeyLength -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
                -Provider "Microsoft Software Key Storage Provider" -KeyUsage DigitalSignature -KeyUsageProperty Sign `
                -NotBefore $now.AddMinutes(-5) -NotAfter $now.AddHours($ValidityHours)
        }
        catch {
            Stop-Run "ADD-CREDENTIAL: STOPPED. The certificate could not be created. Nothing was sent."
        }

        # The local read-back: a key that is not proven non-exportable is deleted at once, before anything is sent.
        $keyVerified = $false
        try {
            $keyVerified = $certificate.HasPrivateKey -and ($certificate.Subject -ceq $CertificateSubject) -and (Test-NonExportableKey $certificate)
        }
        catch {
            $keyVerified = $false
        }

        if (-not $keyVerified) {
            Remove-LocalCertificate $certificate.Thumbprint
            Stop-Run "ADD-CREDENTIAL: STOPPED. The new key is not a non-exportable $KeyLength-bit RSA key; the certificate and its key were deleted. Nothing was sent."
        }

        $thumbprint = $certificate.Thumbprint.ToLowerInvariant()
        $credentialName = $CredentialPrefix + $thumbprint
        $expectedEnd = ConvertTo-UtcSecond $certificate.NotAfter
        $publicCertificate = [System.Convert]::ToBase64String($certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $null = Send-Graph "patch" "applications/$($state.Application.id)" ([ordered]@{
                keyCredentials = @([ordered]@{ type = "AsymmetricX509Cert"; usage = "Verify"; key = $publicCertificate; displayName = $credentialName })
            })

        $state = Read-State
        Write-Checks (Test-State $state "exactly-expected" $credentialName $expectedEnd) "ADD-CREDENTIAL" " Run remove-credential to clean up."
        $hash = [System.Security.Cryptography.SHA256]::Create()
        try {
            $fingerprint = -join ($hash.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString("x2") })
        }
        finally {
            $hash.Dispose()
        }

        Write-Host "  application (client) id     : $($state.Application.appId)"
        Write-Host "  service principal object id : $($state.Principal.id)"
        Write-Host "  certificate thumbprint      : $thumbprint (SHA-1, $CertificateStore)"
        Write-Host "  certificate SHA-256         : $fingerprint"
        Write-Host "  valid until (UTC)           : $expectedEnd"
        Write-Host "ADD-CREDENTIAL: COMPLETE (verified by read-back)" -ForegroundColor Green
    }
    "remove-credential" {
        $found = Find-Application
        if ($null -eq $found) {
            Stop-Run "REFUSED: no application is named $DisplayName. Nothing was written."
        }

        $application = Get-Graph "applications/$($found.id)" @()
        $removed = Get-Count $application.keyCredentials
        if ($removed -gt 0) {
            $null = Send-Graph "patch" "applications/$($application.id)" ([ordered]@{ keyCredentials = @() })
        }

        $application = Get-Graph "applications/$($found.id)" @()
        if ((Get-Count $application.keyCredentials) -ne 0) {
            Stop-Run "REMOVE-CREDENTIAL: NOT VERIFIED. The application still has a credential."
        }

        $local = Get-LocalCertificates
        foreach ($certificate in $local) {
            Remove-LocalCertificate $certificate.Thumbprint
        }

        if ((Get-LocalCertificates).Count -ne 0) {
            Stop-Run "REMOVE-CREDENTIAL: NOT VERIFIED. A certificate for $CertificateSubject is still in $CertificateStore."
        }

        Write-Host "  application credentials removed : $removed"
        Write-Host "  local certificates removed      : $($local.Count)"
        Write-Host "REMOVE-CREDENTIAL: COMPLETE (verified by read-back)" -ForegroundColor Green
    }
}
