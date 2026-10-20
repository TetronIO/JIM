# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 029: AD FS identity provider trust (JIM trusts the lab's AD FS server for sign-in)

.DESCRIPTION
    The first scenario that signs in through the Active Directory lab's AD FS server, adfs-primary (#2355). The
    runner starts JIM with AD FS as its identity provider (docker-compose.ad-lab-adfs.yml: JIM_SSO_AUTHORITY is
    https://adfs.panoply.local/adfs), reverts adfs-primary after dc-primary, and waits until it publishes its discovery
    document. AD FS's HTTPS certificate is self-signed, so JIM starts out unable to trust it, exactly as on a customer's
    first install against an AD FS farm with an internal certificate authority (#2363). The scenario proves that path
    against a real AD FS server:

      Untrusted         JIM reports the identity provider Untrusted, names the service host, and suggests trusting the
                        certificate AD FS presents: the one the runner read off the wire
                        (test/integration/ad-lab-certs/<VmName>.pem), compared by thumbprint.
      Trust             Add-JIMCertificate -FromIdentityProvider, with that thumbprint, trusts it for sign-in.
      Trusted           JIM then reports the identity provider Trusted, by the JIM certificate store.
      AlreadyTrusted    Trusting it again changes nothing, and says so.

    Nobody signs in here: JIM's registration on AD FS comes with the registration command (#2364). The later AD FS
    checks (signing in, the admin's identifier and profile claims) build on this scenario's trust.

    Active Directory lab only. dc-primary and adfs-primary are reverted to their baseline checkpoints by the runner
    before the scenario starts, and Reset-JIMSystem is never called: the trust is the scenario's result.

.PARAMETER Step
    Accepted for runner compatibility; the steps depend on each other, so only All is meaningful.

.PARAMETER Template
    Accepted for runner compatibility and not used.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication. The infrastructure key: nobody can sign in until JIM trusts its identity provider.

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario needs nothing populated.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. The AD FS server's config is read by the scenario itself
    (Get-ActiveDirectoryLabFederationConfig).

.PARAMETER CertificateDirectory
    Where the runner saved the AD FS server's certificate. Defaults to test/integration/ad-lab-certs.

.EXAMPLE
    ./Invoke-Scenario-029-ActiveDirectoryFederationTrust.ps1 -ApiKey "jim_..."
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Step, Template, WaitSeconds, SkipPopulate and DirectoryConfig are accepted for the runner, which passes the same parameters to every scenario.')]
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

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

$federationConfig = Get-ActiveDirectoryLabFederationConfig
$certificatePath = Join-Path $CertificateDirectory "$($federationConfig.VmName).pem"
if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw "Scenario 029 needs the AD FS server's certificate the runner saves while waiting for it ($certificatePath), and it " +
          "is not there. Run the scenario through Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario 29."
}
# The thumbprint JIM shows is SHA-1 in upper-case hexadecimal, as X509Certificate2 gives it.
$expectedThumbprint = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath).Thumbprint

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

function Get-TrustStatusUntil {
    <#
    .SYNOPSIS
        Read JIM's identity provider trust status until it is the one wanted, or the time is up.

    .DESCRIPTION
        JIM checks its identity provider shortly after starting and then at most every few seconds, so a status read
        straight after a change can still show the one before it. Returns the last status read.
    #>
    param(
        [Parameter(Mandatory=$true)][string[]]$State,
        [Parameter(Mandatory=$false)][int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $status = Get-JIMIdentityProviderTrust
        if ("$($status.state)" -in $State -or (Get-Date) -gt $deadline) {
            return $status
        }
        Start-Sleep -Seconds 5
    }
}

function Format-TrustStatus {
    param([Parameter(Mandatory=$true)][object]$Status)
    return "state '$($Status.state)', host '$($Status.host)', suggested '$($Status.suggestedThumbprint)': $($Status.message)"
}

Write-TestSection "Scenario 029: AD FS identity provider trust"
Write-Host "Identity provider: $($federationConfig.Authority) ($($federationConfig.VmName))" -ForegroundColor Gray
Write-Host "Certificate:       $expectedThumbprint, read off the wire by the runner" -ForegroundColor Gray
Write-Host ""

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # ---------------------------------------------------------------------------------------------------------------
    # Untrusted: what an administrator sees on a first install against AD FS with an internal certificate authority
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [1/4] JIM reports the AD FS server Untrusted" -ForegroundColor DarkGray
    $before = Get-TrustStatusUntil -State 'Untrusted', 'Trusted', 'NameMismatch', 'Unreachable', 'NotHttps'

    Add-TestResult -Name "JIM reports AD FS Untrusted before anyone trusts it" `
        -Passed ("$($before.state)" -eq 'Untrusted') -Detail (Format-TrustStatus -Status $before)

    Add-TestResult -Name "JIM names the AD FS service host it connects to" `
        -Passed ("$($before.host)" -eq $federationConfig.Host) -Detail (Format-TrustStatus -Status $before)

    $presented = @($before.presentedChain)
    $presentedThumbprint = if ($presented.Count -gt 0) { "$($presented[0].thumbprint)" } else { '' }
    Add-TestResult -Name "AD FS presents the certificate the runner read off the wire, and JIM suggests trusting it" `
        -Passed ($presentedThumbprint -eq $expectedThumbprint -and "$($before.suggestedThumbprint)" -eq $expectedThumbprint) `
        -Detail "Presented '$presentedThumbprint', suggested '$($before.suggestedThumbprint)', expected '$expectedThumbprint'"

    # ---------------------------------------------------------------------------------------------------------------
    # Trust: as an administrator would, naming the thumbprint they checked
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [2/4] Trusting the certificate AD FS presents, for sign-in" -ForegroundColor DarkGray
    $trust = Add-JIMCertificate -FromIdentityProvider -Thumbprint $expectedThumbprint -ChangeReason "Scenario 029" -PassThru

    Add-TestResult -Name "Add-JIMCertificate -FromIdentityProvider trusts the AD FS certificate" `
        -Passed ("$($trust.outcome)" -eq 'Trusted') -Detail "Outcome '$($trust.outcome)': $($trust.message)"

    $stored = $trust.certificate
    Add-TestResult -Name "The certificate is in the JIM store, trusted for sign-in" `
        -Passed ($null -ne $stored -and "$($stored.thumbprint)" -eq $expectedThumbprint -and "$($stored.trustedFor)" -match 'SignIn') `
        -Detail $(if ($stored) { "Stored '$($stored.thumbprint)', trusted for '$($stored.trustedFor)'" } else { 'No certificate in the response' })

    # ---------------------------------------------------------------------------------------------------------------
    # Trusted: JIM's own check now passes, by the store
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [3/4] JIM reports the AD FS server Trusted" -ForegroundColor DarkGray
    $after = Get-TrustStatusUntil -State 'Trusted'

    Add-TestResult -Name "JIM reports AD FS Trusted, by the JIM certificate store" `
        -Passed ("$($after.state)" -eq 'Trusted' -and "$($after.trustedBy)" -eq 'TrustedByJimCertificateStore') `
        -Detail "$(Format-TrustStatus -Status $after), trusted by '$($after.trustedBy)'"

    # ---------------------------------------------------------------------------------------------------------------
    # AlreadyTrusted: a second attempt changes nothing
    # ---------------------------------------------------------------------------------------------------------------
    Write-Host "  [4/4] Trusting it again" -ForegroundColor DarkGray
    $again = Add-JIMCertificate -FromIdentityProvider -Thumbprint $expectedThumbprint -PassThru

    Add-TestResult -Name "Trusting the AD FS certificate again changes nothing, and says so" `
        -Passed ("$($again.outcome)" -eq 'AlreadyTrusted') -Detail "Outcome '$($again.outcome)': $($again.message)"

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

Write-TestSection "Scenario 029 Summary"
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
Write-Host "OK JIM trusts the lab's AD FS server for sign-in the way an administrator would: shown Untrusted with" -ForegroundColor Green
Write-Host "   the certificate it presents, trusted by thumbprint, then reported Trusted." -ForegroundColor Green
exit 0
