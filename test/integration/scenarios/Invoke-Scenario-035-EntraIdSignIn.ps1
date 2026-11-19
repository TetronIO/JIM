# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 035: Entra ID sign-in (people sign in to JIM through Microsoft Entra ID)

.DESCRIPTION
    Signs people in to JIM through the JIM test tenant in Microsoft Entra ID (tetronjim.onmicrosoft.com), end to end,
    with JIM set up exactly as a customer sets it up (#2367). Before the scenario, the runner (Step 4f) registered JIM in
    the tenant with Register-JIMIdentityProvider -Provider EntraId, as the CI app that owns the registration, and
    restarted JIM matching Entra ID's oid claim to the built-in Object Identifier attribute, with jim-test-admin as its
    initial administrator. The scenario runs the checks from the LDAP toolbox container, the lab's stand-in for an
    administrator's workstation (AdfsSignIn-Helpers.ps1), over plain HTTP with no browser:

      1  Portal        The administrator signs in to the portal through the OpenID Connect code flow.
      2  Initial admin JIM provisioned them from the oid claim, matched to Object Identifier, as an Administrator.
      3  Connect-JIM   Connect-JIM signs them in interactively through PKCE (the same application, as a public client).
      4  Bearer        Its version 2 access token is accepted on /api, Administrator APIs included.
      5  Same person   The portal and Connect-JIM resolve them to the same Metaverse Object.
      6  No role       jim-test-user, who has no JIM identity, signs in to the portal and with Connect-JIM, is told
                       who they are, and is refused every Administrator API.
      7  Try It        The API reference's Try It signs the administrator in as a browser on JIM's page does, with
                       Entra ID's own endpoints and the access_as_user scope, through the single-page application
                       redirect, and its token is accepted on /api as the same person.
      8  Registration  Register-JIMIdentityProvider run again finds nothing to change: the registration is idempotent.

    It is reported as its own commit status, jim-sso-entra, by ad-lab.yml, which runs it apart from the LDAP scenarios
    (Test-ScenarioRunsOnItsOwn). Active Directory lab only: the tenant's credentials are the lab runner's.

.PARAMETER Step
    Accepted for runner compatibility; the checks depend on each other, so only All is meaningful.

.PARAMETER Template
    Accepted for runner compatibility and not used.

.PARAMETER JIMUrl
    The URL of the JIM instance the runner reaches (default: http://localhost:5200), for reading Metaverse Objects.

.PARAMETER ApiKey
    The infrastructure API key.

.PARAMETER ContinueOnError
    Continue executing remaining checks even if one fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario populates nothing.

.PARAMETER DirectoryConfig
    dc-primary's configuration, to confirm the run is the Active Directory lab's.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 35
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Step, Template, WaitSeconds and SkipPopulate are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"
. "$PSScriptRoot/../utils/AdfsSignIn-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdLab-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
if (-not $DirectoryConfig -or "$($DirectoryConfig.DirectoryType)" -ne 'ActiveDirectory') {
    throw "Scenario 035 runs against the Active Directory lab only: run it with Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 35."
}
foreach ($name in 'JIM_AD_LAB_ENTRA_CLIENT_ID', 'JIM_AD_LAB_ENTRA_AUTHORITY', 'JIM_SSO_ENTRA_TENANT_ID', 'JIM_SSO_ENTRA_CI_CLIENT_ID',
    'JIM_SSO_ENTRA_ADMIN_OBJECT_ID', 'JIM_SSO_ENTRA_ADMIN_PASSWORD', 'JIM_SSO_ENTRA_USER_PASSWORD') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 035 needs $name, which the jim-ad-lab environment and Run-IntegrationTests.ps1 (Step 4f) set. Run it through Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 35."
    }
}

$jimCertificatePath = Join-Path $PSScriptRoot '..' 'results' 'jim-web-tls' 'tls.crt'
$signInUrl = Get-ActiveDirectoryLabJimUrl
$tenantDomain = 'tetronjim.onmicrosoft.com'
$clientId = $env:JIM_AD_LAB_ENTRA_CLIENT_ID
$administrator = @{
    UserName = "jim-test-admin@$tenantDomain"; Password = $env:JIM_SSO_ENTRA_ADMIN_PASSWORD; ObjectId = $env:JIM_SSO_ENTRA_ADMIN_OBJECT_ID
}
$person = @{ UserName = "jim-test-user@$tenantDomain"; Password = $env:JIM_SSO_ENTRA_USER_PASSWORD }

$script:TestResults = @()
$startTime = Get-Date

function Add-TestResult {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][bool]$Passed,
        [Parameter(Mandatory=$false)][string]$Detail = ""
    )
    $script:TestResults += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
    if ($Passed) {
        Write-Host "  OK PASSED: $Name" -ForegroundColor Green
    }
    else {
        Write-Host "  FAILED: $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor Yellow }
        if (-not $ContinueOnError) {
            throw "Assertion failed: $Name. $Detail"
        }
    }
}

function Format-SignInResult {
    # What a check reported, for a failure's detail: never a password, which no result carries.
    param([Parameter(Mandatory=$true)][object]$Result)
    return ($Result | ConvertTo-Json -Depth 6 -Compress)
}

function Get-ObjectIdentifierValue {
    # A Metaverse Object's Object Identifier, read with the API key.
    param([Parameter(Mandatory=$true)][guid]$MetaverseObjectId)
    $mvo = Get-JIMMetaverseObject -Id $MetaverseObjectId -ErrorAction Stop
    $value = @($mvo.attributeValues | Where-Object { $_.attributeName -ceq 'Object Identifier' -and $_.stringValue }) | Select-Object -First 1
    if ($value) { return [string]$value.stringValue } else { return $null }
}

Write-TestSection "Scenario 035: Entra ID sign-in"
Write-Host "Identity provider: $($env:JIM_AD_LAB_ENTRA_AUTHORITY) (application $clientId)" -ForegroundColor Gray
Write-Host "JIM, as signed in to: $signInUrl (from the LDAP toolbox container)" -ForegroundColor Gray
Write-Host "Administrator:     $($administrator.UserName) (object ID $($administrator.ObjectId))" -ForegroundColor Gray
Write-Host ""

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    Write-Host "  [setup] Preparing the LDAP toolbox to sign in as a workstation would" -ForegroundColor DarkGray
    Initialize-AdfsSignInToolbox -RepositoryRoot (Join-Path $PSScriptRoot '..' '..' '..') -JimCertificatePath $jimCertificatePath |
        ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

    # ---------------------------------------------------------------------------------------------------------------
    # 1 and 2: the administrator signs in to the portal, and is the initial administrator JIM provisioned from oid
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [1/8] The administrator signs in to the portal through Entra ID" -ForegroundColor DarkGray
    $adminPortal = Invoke-AdfsSignInCheck -Check Portal -JimUrl $signInUrl -UserName $administrator.UserName -Password $administrator.Password
    Add-TestResult -Name "The administrator signs in to the portal through the OpenID Connect code flow" `
        -Passed ($adminPortal.ok -and $adminPortal.userInfo.authorised) -Detail (Format-SignInResult $adminPortal)

    Write-Host "  [2/8] JIM provisioned them from oid, matched to Object Identifier" -ForegroundColor DarkGray
    $adminMvoId = if ($adminPortal.ok -and $adminPortal.userInfo.metaverseObjectId) { [guid]$adminPortal.userInfo.metaverseObjectId } else { $null }
    $adminObjectId = if ($adminMvoId) { Get-ObjectIdentifierValue -MetaverseObjectId $adminMvoId } else { $null }
    Add-TestResult -Name "JIM provisioned the initial administrator from oid, matched to Object Identifier, as an Administrator" `
        -Passed ("$adminObjectId" -eq $administrator.ObjectId -and $adminPortal.userInfo.isAdministrator -and $adminPortal.administratorApiStatus -eq 200) `
        -Detail "Metaverse Object $adminMvoId holds Object Identifier '$adminObjectId' (expected '$($administrator.ObjectId)'); $(Format-SignInResult $adminPortal)"

    # ---------------------------------------------------------------------------------------------------------------
    # 3, 4 and 5: Connect-JIM through PKCE, its bearer token on /api, and the same person as the portal's
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [3/8] Connect-JIM signs the administrator in through PKCE" -ForegroundColor DarkGray
    $adminShell = Invoke-AdfsSignInCheck -Check ConnectJim -JimUrl $signInUrl -UserName $administrator.UserName -Password $administrator.Password
    Add-TestResult -Name "Connect-JIM signs the administrator in interactively through PKCE" `
        -Passed ($adminShell.ok -and $adminShell.connection.connected -and "$($adminShell.connection.authMethod)" -eq 'OAuth') -Detail (Format-SignInResult $adminShell)

    Write-Host "  [4/8] Its version 2 access token is accepted on /api" -ForegroundColor DarkGray
    Add-TestResult -Name "A version 2 access token Entra ID issued is accepted on /api, Administrator APIs included" `
        -Passed ($adminShell.ok -and $adminShell.userInfo.authorised -and $adminShell.administratorApiStatus -eq 200) -Detail (Format-SignInResult $adminShell)

    Write-Host "  [5/8] The portal and Connect-JIM resolve the same person" -ForegroundColor DarkGray
    $shellMvoId = if ($adminShell.ok -and $adminShell.userInfo.metaverseObjectId) { [guid]$adminShell.userInfo.metaverseObjectId } else { $null }
    Add-TestResult -Name "The portal and Connect-JIM resolve the administrator to the same Metaverse Object" `
        -Passed ($null -ne $adminMvoId -and $shellMvoId -eq $adminMvoId) -Detail "Portal: $adminMvoId; Connect-JIM: $shellMvoId"

    # ---------------------------------------------------------------------------------------------------------------
    # 6: a person with no JIM identity
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [6/8] A person with no JIM identity signs in and is refused every Administrator API" -ForegroundColor DarkGray
    $personPortal = Invoke-AdfsSignInCheck -Check Portal -JimUrl $signInUrl -UserName $person.UserName -Password $person.Password
    Add-TestResult -Name "A person with no JIM identity signs in to the portal, is told so, and holds no role" `
        -Passed ($personPortal.ok -and -not $personPortal.userInfo.authorised -and -not $personPortal.userInfo.isAdministrator -and
            @($personPortal.userInfo.roles).Count -eq 0) -Detail (Format-SignInResult $personPortal)
    Add-TestResult -Name "The portal refuses them an Administrator API" `
        -Passed ($personPortal.ok -and $personPortal.administratorApiStatus -eq 403) -Detail (Format-SignInResult $personPortal)
    $personShell = Invoke-AdfsSignInCheck -Check ConnectJim -JimUrl $signInUrl -UserName $person.UserName -Password $person.Password
    Add-TestResult -Name "Connect-JIM signs them in, and /api refuses them an Administrator API" `
        -Passed ($personShell.ok -and $personShell.connection.connected -and $personShell.administratorApiStatus -eq 403) -Detail (Format-SignInResult $personShell)

    # ---------------------------------------------------------------------------------------------------------------
    # 7: the API reference's Try It signs the administrator in, as a browser on JIM's page would (#2594)
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [7/8] The API reference's Try It signs the administrator in through Entra ID" -ForegroundColor DarkGray
    $tryIt = Invoke-AdfsSignInCheck -Check TryIt -JimUrl $signInUrl -UserName $administrator.UserName -Password $administrator.Password
    $tryItSettings = if ($tryIt.ok) { $tryIt.tryIt } else { $null }
    $entraBase = "https://login.microsoftonline.com/$($env:JIM_SSO_ENTRA_TENANT_ID)/oauth2/v2.0"
    Add-TestResult -Name "The API reference offers Try It Entra ID's own endpoints, the application and the access_as_user scope" `
        -Passed ($null -ne $tryItSettings -and "$($tryItSettings.authorizationUrl)" -eq "$entraBase/authorize" -and "$($tryItSettings.tokenUrl)" -eq "$entraBase/token" -and
            "$($tryItSettings.clientId)" -eq $clientId -and "$($tryItSettings.apiScope)" -eq "api://$clientId/access_as_user") `
        -Detail (Format-SignInResult $tryIt)
    # A browser refuses the token request unless the page's Content Security Policy lets it connect there; this client
    # does not enforce that, so the policy is checked instead.
    $tokenOrigin = if ($null -ne $tryItSettings -and $tryItSettings.tokenUrl) { ([uri]$tryItSettings.tokenUrl).GetLeftPart([UriPartial]::Authority) } else { $null }
    Add-TestResult -Name "The API reference's Content Security Policy lets it connect to Entra ID's token endpoint, and nowhere else outside JIM" `
        -Passed ($null -ne $tokenOrigin -and @($tryItSettings.connectSources) -contains $tokenOrigin -and
            @($tryItSettings.connectSources | Where-Object { $_ -ne "'self'" -and $_ -ne $tokenOrigin }).Count -eq 0) `
        -Detail (Format-SignInResult $tryIt)
    # Entra ID answers a single-page application's token request with CORS for any origin.
    Add-TestResult -Name "Entra ID lets JIM's page read Try It's token (CORS), and /api accepts it as the same administrator" `
        -Passed ($null -ne $tryItSettings -and "$($tryItSettings.allowedOrigin)" -in "$($tryItSettings.origin)", '*' -and
            "$($tryIt.userInfo.metaverseObjectId)" -eq "$adminMvoId" -and $tryIt.administratorApiStatus -eq 200) `
        -Detail (Format-SignInResult $tryIt)

    # ---------------------------------------------------------------------------------------------------------------
    # 8: the registration is idempotent
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [8/8] Register-JIMIdentityProvider run again finds nothing to change" -ForegroundColor DarkGray
    $graphToken = Get-EntraIdLabGraphToken -TenantId $env:JIM_SSO_ENTRA_TENANT_ID -ClientId $env:JIM_SSO_ENTRA_CI_CLIENT_ID
    $again = Register-JIMIdentityProvider -Provider EntraId -Url (Get-EntraIdLabJimUrl) -TenantId $env:JIM_SSO_ENTRA_TENANT_ID -AccessToken $graphToken -Confirm:$false
    Add-TestResult -Name "Register-JIMIdentityProvider finds JIM's Entra ID registration already right, and changes nothing" `
        -Passed ($null -ne $again -and @($again.Changes).Count -eq 0 -and "$($again.ClientId)" -eq $clientId -and -not $again.ClientSecret) `
        -Detail "Changes: $(@($again.Changes) -join '; ')"

    Assert-NoWorkerErrors -Since $startTime
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
$duration = (Get-Date) - $startTime
$passed = @($script:TestResults | Where-Object { $_.Passed }).Count
$failed = @($script:TestResults | Where-Object { -not $_.Passed }).Count

Write-TestSection "Scenario 035 Summary"
Write-Host "Duration: $([math]::Round($duration.TotalSeconds, 1))s" -ForegroundColor Gray
Write-Host "Passed:   $passed" -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host "Failed:   $failed" -ForegroundColor Red
    foreach ($result in $script:TestResults | Where-Object { -not $_.Passed }) {
        Write-Host "  - $($result.Name)" -ForegroundColor Red
    }
    exit 1
}

Write-Host ""
Write-Host "OK People sign in to JIM through Microsoft Entra ID: the portal, Connect-JIM, /api and Try It, matched by" -ForegroundColor Green
Write-Host "   oid, with only the access their JIM roles give them, on a registration Register-JIMIdentityProvider keeps right." -ForegroundColor Green
exit 0
