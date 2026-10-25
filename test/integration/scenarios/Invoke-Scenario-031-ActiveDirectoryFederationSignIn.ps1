# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 031: AD FS sign-in (people sign in to JIM through the lab's AD FS server)

.DESCRIPTION
    Signs people in to JIM through the Active Directory lab's AD FS server, adfs-primary, end to end, with JIM set up
    exactly as a customer sets it up (#2355, #2441). Before the scenario, the runner (Step 4e) registered JIM on AD FS
    with Register-JIMIdentityProvider, created the sign-in administrator jim-sso-admin in dc-primary, created the
    Object GUID attribute, and started JIM matching AD FS's oid claim to Object GUID with that account as its initial
    administrator. The scenario then:

      Trust       trusts AD FS's certificate for sign-in (Add-JIMCertificate -FromIdentityProvider), as Scenario 030
                  proves in detail;
      Employee    creates an ordinary employee in OU=Users,OU=Corp and synchronises Active Directory into JIM
                  (Setup-Scenario-031.ps1), so they have a Metaverse Object, matched by objectGUID;

    and runs the checks from the LDAP toolbox container, the lab's stand-in for an administrator's workstation
    (AdfsSignIn-Helpers.ps1), over plain HTTP with no browser:

      1  Portal        The administrator signs in to the portal through the OpenID Connect code flow.
      2  Initial admin JIM provisioned them from the oid claim, matched to Object GUID, as an Administrator.
      3  Bearer        A bearer token AD FS issued is accepted on /api, Administrator APIs included.
      4  Connect-JIM   Connect-JIM signs them in interactively through PKCE (the module's public client).
      5  Same person   The portal and Connect-JIM resolve them to the same Metaverse Object.
      6  Renamed       The employee, renamed in Active Directory (sAMAccountName and userPrincipalName), still signs
                       in as the same Metaverse Object.
      7  No role       The employee, who holds no JIM role, signs in to the portal and with Connect-JIM, is told who
                       they are, and is refused every Administrator API.

    It is reported as its own commit status, jim-ad-lab-adfs, by ad-lab.yml, which runs it apart from the LDAP
    scenarios (Test-ScenarioRunsOnItsOwn). Active Directory lab only.

.PARAMETER Step
    Accepted for runner compatibility; the checks depend on each other, so only All is meaningful.

.PARAMETER Template
    Accepted for runner compatibility and not used.

.PARAMETER JIMUrl
    The URL of the JIM instance the runner reaches (default: http://localhost:5200), for setup with the API key.

.PARAMETER ApiKey
    The infrastructure API key, for setup and for reading Metaverse Objects.

.PARAMETER ContinueOnError
    Continue executing remaining checks even if one fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario populates what it needs.

.PARAMETER DirectoryConfig
    dc-primary's configuration.

.PARAMETER CertificateDirectory
    Where the runner saved the AD FS server's certificate. Defaults to test/integration/ad-lab-certs.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 31
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
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [string]$CertificateDirectory = (Join-Path $PSScriptRoot '../ad-lab-certs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"
. "$PSScriptRoot/../utils/Invoke-LabControl.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"
. "$PSScriptRoot/../utils/AdfsSignIn-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
if (-not $DirectoryConfig -or "$($DirectoryConfig.DirectoryType)" -ne 'ActiveDirectory') {
    throw "Scenario 031 runs against the Active Directory lab only: run it with Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 31."
}
foreach ($name in 'JIM_AD_LAB_ADFS_ADMIN_UPN', 'JIM_AD_LAB_ADFS_ADMIN_PASSWORD', 'JIM_AD_LAB_ADFS_INITIAL_ADMIN') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 031 needs $name, which Run-IntegrationTests.ps1 sets when it turns AD FS sign-in on (Step 4e). Run it through Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 31."
    }
}

$federationConfig = Get-ActiveDirectoryLabFederationConfig
$certificatePath = Join-Path $CertificateDirectory "$($federationConfig.VmName).pem"
if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw "Scenario 031 needs the AD FS server's certificate the runner saves while waiting for it ($certificatePath)."
}
$jimCertificatePath = Join-Path $PSScriptRoot '..' 'results' 'jim-web-tls' 'tls.crt'
$expectedThumbprint = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath).Thumbprint
$signInUrl = Get-ActiveDirectoryLabJimUrl

$administrator = @{
    UserName   = $env:JIM_AD_LAB_ADFS_ADMIN_UPN
    Password   = $env:JIM_AD_LAB_ADFS_ADMIN_PASSWORD
    ObjectGuid = [guid]$env:JIM_AD_LAB_ADFS_INITIAL_ADMIN
}

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

function Get-ObjectGuidValue {
    # A Metaverse Object's Object GUID, read with the API key.
    param([Parameter(Mandatory=$true)][guid]$MetaverseObjectId)
    $mvo = Get-JIMMetaverseObject -Id $MetaverseObjectId -ErrorAction Stop
    $value = @($mvo.attributeValues | Where-Object { $_.attributeName -ceq 'Object GUID' -and $_.guidValue }) | Select-Object -First 1
    if ($value) { return [guid]$value.guidValue } else { return $null }
}

Write-TestSection "Scenario 031: AD FS sign-in"
Write-Host "Identity provider: $($federationConfig.Authority) ($($federationConfig.VmName))" -ForegroundColor Gray
Write-Host "JIM, as signed in to: $signInUrl (from the LDAP toolbox container)" -ForegroundColor Gray
Write-Host "Administrator:     $($administrator.UserName) (objectGUID $($administrator.ObjectGuid))" -ForegroundColor Gray
Write-Host ""

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # ---------------------------------------------------------------------------------------------------------------
    # Trust: JIM must trust AD FS's certificate before it can redeem a sign-in (Scenario 030 proves this in detail)
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [setup] Trusting AD FS's certificate for sign-in" -ForegroundColor DarkGray
    $deadline = (Get-Date).AddSeconds(120)
    do {
        $status = Get-JIMIdentityProviderTrust
        if ("$($status.state)" -in 'Untrusted', 'Trusted') { break }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    if ("$($status.state)" -ne 'Trusted') {
        $trust = Add-JIMCertificate -FromIdentityProvider -Thumbprint $expectedThumbprint -ChangeReason "Scenario 031" -PassThru
        if ("$($trust.outcome)" -notin 'Trusted', 'AlreadyTrusted') { throw "Could not trust AD FS's certificate: $($trust.outcome): $($trust.message)" }
    }

    # ---------------------------------------------------------------------------------------------------------------
    # Employee: an ordinary person, synchronised from Active Directory, holding no JIM role
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [setup] Creating an employee in $($DirectoryConfig.UserContainer) and synchronising Active Directory" -ForegroundColor DarkGray
    $employeeSam = 'sso.employee'
    $employeeDn = "CN=Sso Employee,$($DirectoryConfig.UserContainer)"
    $employeePassword = Get-ActiveDirectoryFixturePassword -Avoid @('sso', 'employee', 'renamed')
    Protect-ActiveDirectoryLabSecret -Secret $employeePassword
    $created = New-DirectoryUser -DirectoryConfig $DirectoryConfig -Dn $employeeDn -SamAccountName $employeeSam -Password $employeePassword `
        -Attributes ([ordered]@{ givenName = 'Sso'; sn = 'Employee'; displayName = 'Sso Employee' })
    if ($created.Outcome -ne 'Created') { throw "Could not create the employee '$employeeDn': $($created.Outcome)" }
    $employeeGuid = Get-ActiveDirectoryObjectGuid -DirectoryConfig $DirectoryConfig -Dn $employeeDn
    $employeeUpn = "$employeeSam@$(([string]$DirectoryConfig.Domain).ToLowerInvariant())"

    $config = & "$PSScriptRoot/../Setup-Scenario-031.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -DirectoryConfig $DirectoryConfig
    # The setup script connects afresh; connect again in case it left a different connection behind.
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    foreach ($run in @(
            @{ Profile = $config.FullImportProfileId; Name = 'Full Import' },
            @{ Profile = $config.FullSyncProfileId; Name = 'Full Synchronisation' }
        )) {
        $activity = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $run.Profile -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $activity.activityId -Name $run.Name
    }
    $employeeMvo = @(Get-JIMMetaverseObject -AttributeName 'Account Name' -AttributeValue $employeeSam) | Select-Object -First 1
    if (-not $employeeMvo) { throw "Synchronisation made no Metaverse Object for $employeeSam." }
    $employeeMvoGuid = Get-ObjectGuidValue -MetaverseObjectId $employeeMvo.id
    if ($employeeMvoGuid -ne $employeeGuid) {
        throw "The employee's Metaverse Object holds Object GUID '$employeeMvoGuid', not their objectGUID '$employeeGuid'."
    }
    Write-Host "  [setup] $employeeUpn is Metaverse Object $($employeeMvo.id) (Object GUID $employeeGuid)" -ForegroundColor DarkGray

    Write-Host "  [setup] Preparing the LDAP toolbox to sign in as a workstation would" -ForegroundColor DarkGray
    Initialize-AdfsSignInToolbox -RepositoryRoot (Join-Path $PSScriptRoot '..' '..' '..') -FederationCertificateName "$($federationConfig.VmName).pem" `
        -JimCertificatePath $jimCertificatePath | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

    # ---------------------------------------------------------------------------------------------------------------
    # 1 and 2: the administrator signs in to the portal, and is the initial administrator JIM provisioned from oid
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [1/7] The administrator signs in to the portal through AD FS" -ForegroundColor DarkGray
    $adminPortal = Invoke-AdfsSignInCheck -Check Portal -JimUrl $signInUrl -UserName $administrator.UserName -Password $administrator.Password
    Add-TestResult -Name "The administrator signs in to the portal through the OpenID Connect code flow" `
        -Passed ($adminPortal.ok -and $adminPortal.userInfo.authorised) -Detail (Format-SignInResult $adminPortal)

    Write-Host "  [2/7] JIM provisioned them from oid, matched to Object GUID" -ForegroundColor DarkGray
    $adminMvoId = if ($adminPortal.ok -and $adminPortal.userInfo.metaverseObjectId) { [guid]$adminPortal.userInfo.metaverseObjectId } else { $null }
    $adminMvoGuid = if ($adminMvoId) { Get-ObjectGuidValue -MetaverseObjectId $adminMvoId } else { $null }
    Add-TestResult -Name "JIM provisioned the initial administrator from oid, matched to Object GUID, as an Administrator" `
        -Passed ($adminMvoGuid -eq $administrator.ObjectGuid -and $adminPortal.userInfo.isAdministrator -and $adminPortal.administratorApiStatus -eq 200) `
        -Detail "Metaverse Object $adminMvoId holds Object GUID '$adminMvoGuid' (expected '$($administrator.ObjectGuid)'); $(Format-SignInResult $adminPortal)"

    # ---------------------------------------------------------------------------------------------------------------
    # 3, 4 and 5: Connect-JIM through PKCE, its bearer token on /api, and the same person as the portal's
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [3/7] Connect-JIM signs the administrator in through PKCE" -ForegroundColor DarkGray
    $adminShell = Invoke-AdfsSignInCheck -Check ConnectJim -JimUrl $signInUrl -UserName $administrator.UserName -Password $administrator.Password
    Add-TestResult -Name "Connect-JIM signs the administrator in interactively through PKCE" `
        -Passed ($adminShell.ok -and $adminShell.connection.connected -and "$($adminShell.connection.authMethod)" -eq 'OAuth') -Detail (Format-SignInResult $adminShell)

    Write-Host "  [4/7] Its bearer token is accepted on /api" -ForegroundColor DarkGray
    Add-TestResult -Name "A bearer token AD FS issued is accepted on /api, Administrator APIs included" `
        -Passed ($adminShell.ok -and $adminShell.userInfo.authorised -and $adminShell.administratorApiStatus -eq 200) -Detail (Format-SignInResult $adminShell)

    Write-Host "  [5/7] The portal and Connect-JIM resolve the same person" -ForegroundColor DarkGray
    $shellMvoId = if ($adminShell.ok -and $adminShell.userInfo.metaverseObjectId) { [guid]$adminShell.userInfo.metaverseObjectId } else { $null }
    Add-TestResult -Name "The portal and Connect-JIM resolve the administrator to the same Metaverse Object" `
        -Passed ($null -ne $adminMvoId -and $shellMvoId -eq $adminMvoId) -Detail "Portal: $adminMvoId; Connect-JIM: $shellMvoId"

    # ---------------------------------------------------------------------------------------------------------------
    # 7: the employee, who holds no JIM role
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [6/7] The employee, who holds no JIM role, signs in and is refused every Administrator API" -ForegroundColor DarkGray
    $employeePortal = Invoke-AdfsSignInCheck -Check Portal -JimUrl $signInUrl -UserName $employeeUpn -Password $employeePassword
    $employeeRoles = @(if ($employeePortal.ok) { $employeePortal.userInfo.roles })
    Add-TestResult -Name "The employee signs in to the portal as their synchronised Metaverse Object, with the User role only" `
        -Passed ($employeePortal.ok -and $employeePortal.userInfo.authorised -and -not $employeePortal.userInfo.isAdministrator -and
            "$($employeePortal.userInfo.metaverseObjectId)" -eq "$($employeeMvo.id)" -and ($employeeRoles -join ',') -eq 'User') `
        -Detail (Format-SignInResult $employeePortal)
    Add-TestResult -Name "The portal refuses the employee an Administrator API" `
        -Passed ($employeePortal.ok -and $employeePortal.administratorApiStatus -eq 403) -Detail (Format-SignInResult $employeePortal)

    $employeeShell = Invoke-AdfsSignInCheck -Check ConnectJim -JimUrl $signInUrl -UserName $employeeUpn -Password $employeePassword
    Add-TestResult -Name "Connect-JIM signs the employee in, and /api refuses them an Administrator API" `
        -Passed ($employeeShell.ok -and "$($employeeShell.userInfo.metaverseObjectId)" -eq "$($employeeMvo.id)" -and $employeeShell.administratorApiStatus -eq 403) `
        -Detail (Format-SignInResult $employeeShell)

    # ---------------------------------------------------------------------------------------------------------------
    # 6: renamed in Active Directory, the employee is still the same person to JIM
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [7/7] The employee, renamed in Active Directory, signs in as the same person" -ForegroundColor DarkGray
    $renamedSam = 'sso.renamed'
    $renamedUpn = "$renamedSam@$(([string]$DirectoryConfig.Domain).ToLowerInvariant())"
    $rename = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Operation modify -Ldif (@(
            "dn: $employeeDn", 'changetype: modify',
            'replace: sAMAccountName', "sAMAccountName: $renamedSam", '-',
            'replace: userPrincipalName', "userPrincipalName: $renamedUpn"
        ) -join "`n")
    if (-not $rename.Success) { throw "Could not rename the employee: $($rename.Output)" }
    $renamedPortal = Invoke-AdfsSignInCheck -Check Portal -JimUrl $signInUrl -UserName $renamedUpn -Password $employeePassword
    Add-TestResult -Name "Renamed in Active Directory (sAMAccountName and userPrincipalName), the employee signs in as the same Metaverse Object" `
        -Passed ($renamedPortal.ok -and "$($renamedPortal.userInfo.metaverseObjectId)" -eq "$($employeeMvo.id)") -Detail (Format-SignInResult $renamedPortal)

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

Write-TestSection "Scenario 031 Summary"
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
Write-Host "OK People sign in to JIM through the lab's AD FS server: the portal, Connect-JIM and /api, matched by" -ForegroundColor Green
Write-Host "   objectGUID, through a rename, and with only the access their JIM roles give them." -ForegroundColor Green
exit 0
