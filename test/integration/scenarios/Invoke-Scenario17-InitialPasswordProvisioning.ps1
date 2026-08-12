# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 17: Initial Password Provisioning

.DESCRIPTION
    Proves that the Initial Password JIM sets on an account it provisions is one the account holder can
    actually use, and that the options chosen alongside it are honoured by the directory.

    Everything else in JIM's password coverage stops at the connector's outgoing bytes: the unit tests
    in LdapConnectorPasswordTests assert against a mocked LDAP executor, so they prove JIM *emits* a
    quoted UTF-16LE unicodePwd write and a pwdLastSet of zero, and prove nothing about whether a
    directory accepts either. This scenario closes that gap by taking the credential JIM set and
    signing in with it.

    The chain, in order, and why each link is needed:

      1. Provision an account through the ordinary path (HR CSV, Metaverse, Create export to Samba AD).
      2. Read the account back: it must be enabled, and must carry pwdLastSet = 0.
      3. Bind as the account holder with the Initial Password. Active Directory answers a correct
         password on a must-change account with result 49 and sub-code 773, which is a *success*
         signal here: the credential is right and the directory is insisting on a change.
      4. Bind with a deliberately wrong password. This must answer 49 sub-code 52e. Without this
         contrast, step 3 proves nothing: both are result code 49, and a scenario that only checked
         "the bind failed" would pass just as happily against a password JIM never set.
      5. Change the password as the account holder, authenticating with the Initial Password. This is
         the flow a new starter is actually put through, and the only step that proves the credential
         is usable rather than merely recognised.
      6. Bind with the newly chosen password. It must succeed outright.
      7. Confirm JIM's own record agrees: nothing parked, nothing expired.

    Samba AD only. "Must change at next sign-in" is an Active Directory behaviour; JIM reports it as a
    downgrade on every other directory, so step 3's central assertion has nothing to bite on there.

.PARAMETER Step
    Which part to execute (Provision, Credential, All)

.PARAMETER Template
    Accepted for runner compatibility and deliberately not used for sizing. This scenario asserts
    against a single account, so a larger template would only lengthen the export for no added
    coverage; it always provisions at Micro.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. This scenario provisions the accounts it asserts against and
    needs no pre-populated directory data.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig

.EXAMPLE
    ./Invoke-Scenario17-InitialPasswordProvisioning.ps1 -ApiKey "jim_..." -Template Micro
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Provision", "Credential", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

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

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

if ($DirectoryConfig.UserObjectClass -ne "user") {
    throw "Scenario 17 requires Samba AD. 'Must change at next sign-in' has no portable equivalent on " +
          "$($DirectoryConfig.ConnectedSystemName), so this scenario's central assertion cannot hold there."
}

# The password the account holder chooses when put through the change. Distinct from the Initial
# Password in every character class so that a bind succeeding with it cannot be confused with a bind
# succeeding with the one JIM set.
$chosenPassword = 'Foxglove-9-Harbour!'

# One account is what this scenario asserts against, so it always provisions at Micro no matter what
# the runner passed. A larger template would lengthen the export and prove nothing further.
$effectiveTemplate = "Micro"

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
        Write-Host "  ✓ PASSED: $Name" -ForegroundColor Green
    }
    else {
        Write-Host "  ✗ FAILED: $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor Yellow }
        if (-not $ContinueOnError) {
            throw "Assertion failed: $Name. $Detail"
        }
    }
}

Write-TestSection "Scenario 17: Initial Password Provisioning"
Write-Host "Directory:  $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.ContainerName))" -ForegroundColor Gray
Write-Host "Template:   $effectiveTemplate (the -Template value is not used for sizing)" -ForegroundColor Gray
Write-Host "Step:       $Step" -ForegroundColor Gray
Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 0: Configure JIM
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Step 0: Configuring JIM"

Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $effectiveTemplate -OutputPath "$PSScriptRoot/../../test-data"
Write-Host "  ✓ CSV test data reset to baseline" -ForegroundColor Green

$config = & "$PSScriptRoot/../Setup-Scenario17.ps1" `
    -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $effectiveTemplate -DirectoryConfig $DirectoryConfig

if (-not $config) {
    throw "Failed to set up Scenario 17 configuration"
}

$initialPassword = $config.InitialPassword
Write-Host "  ✓ JIM configured; the Export Synchronisation Rule sets an Initial Password" -ForegroundColor Green

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Step 1: Provision accounts, which is what causes the Initial Password to be set
    # ─────────────────────────────────────────────────────────────────────────────────────────
    if ($Step -in @("Provision", "All")) {
        Write-TestSection "Step 1: Provisioning accounts into $($DirectoryConfig.ConnectedSystemName)"

        Write-Host "  [1/5] HR CSV Full Import..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import"

        Write-Host "  [2/5] HR CSV Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Delta Sync"

        # The Create exports land here, and each one stages a Pending Initial Password that the
        # delivery pass then sets through the Connector's password channel.
        Write-Host "  [3/5] Directory Export (accounts created, Initial Passwords set)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export"

        # A Full Import, not a Delta Import. This is the Connected System's first import, so there is no
        # persisted baseline for a delta to compare against and the Connector refuses it outright. Scenario 1
        # can use a delta here only because its own flow has already run a full import by that point.
        Write-Host "  [4/5] Directory Full Import (confirms the exports)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Full Import"

        Write-Host "  [5/5] Directory Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Delta Sync"

        Write-Host "  ✓ Provisioning complete" -ForegroundColor Green
    }

    if ($Step -eq "Provision") {
        Write-Host "`nProvision step complete. Re-run with -Step Credential to assert against the accounts." -ForegroundColor Cyan
        return
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Step 2: Find an account JIM provisioned and left in the must-change state
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 2: Selecting a provisioned account"

    # Searching on pwdLastSet = 0 within the managed container finds exactly the accounts JIM
    # provisioned and applied "must change at next sign-in" to. Scoping to the container keeps the
    # directory's own built-in accounts (krbtgt, Guest) out of the result.
    $searchOutput = Invoke-LDAPSearch `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.UserContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter "(&(objectClass=user)(pwdLastSet=0))" `
        -Attributes @("sAMAccountName", "userAccountControl", "pwdLastSet")

    if (-not $searchOutput) {
        throw "No provisioned account carries pwdLastSet = 0 in $($DirectoryConfig.UserContainer). " +
              "Either the export provisioned nothing, or the Initial Password was never set. Check the " +
              "Directory Export Activity and the Synchronisation Rule's parked count."
    }

    # Take the first entry from the LDIF result set.
    $lines = Expand-LDIFFoldedLine -RawLdif ($searchOutput -join "`n")
    $account = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*#') { continue }
        if ($line -match "^(dn|sAMAccountName|userAccountControl|pwdLastSet):\s*(.+)$") {
            $key = $matches[1]
            if ($account.ContainsKey($key)) { break }   # second entry begins; one account is enough
            $account[$key] = $matches[2]
        }
    }

    if (-not $account.ContainsKey('dn') -or -not $account.ContainsKey('sAMAccountName')) {
        throw "Could not parse a provisioned account from the directory search result."
    }

    $accountDn = $account['dn']
    $accountName = $account['sAMAccountName']
    Write-Host "  Account under test: $accountName" -ForegroundColor Cyan
    Write-Host "  Distinguished Name: $accountDn" -ForegroundColor Gray

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 1: the directory holds the state JIM asked for
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 1: The directory holds the requested account state"

    Add-TestResult -Name "pwdLastSet is 0 (must change at next sign-in)" `
        -Passed ($account['pwdLastSet'] -eq '0') `
        -Detail "pwdLastSet was '$($account['pwdLastSet'])'"

    # Bit 0x2 is ACCOUNTDISABLE. JIM was asked to enable the account once the password landed, and
    # Active Directory refuses to enable an account that holds no policy-compliant password, so this
    # assertion is also an independent check that the password write really happened.
    $uac = [int]$account['userAccountControl']
    Add-TestResult -Name "The account is enabled (ACCOUNTDISABLE is clear)" `
        -Passed (($uac -band 0x2) -eq 0) `
        -Detail "userAccountControl was $uac"

    # 0x10000 is DONT_EXPIRE_PASSWORD, which contradicts "must change at next sign-in". JIM clears it
    # whenever an expiring behaviour is chosen.
    Add-TestResult -Name "DONT_EXPIRE_PASSWORD is clear" `
        -Passed (($uac -band 0x10000) -eq 0) `
        -Detail "userAccountControl was $uac"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 2: the account holder's credential is the one JIM set
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 2: Signing in with the Initial Password"

    $bindWithInitial = Test-LDAPBind -BindDN $accountDn -BindPassword $initialPassword -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The directory recognises the Initial Password and requires a change" `
        -Passed ($bindWithInitial.Outcome -eq 'MustChangePassword') `
        -Detail "Expected MustChangePassword, got '$($bindWithInitial.Outcome)'. Directory said: $($bindWithInitial.Output)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 3: the contrast that gives Test 2 its meaning
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 3: A wrong password is refused differently"

    $bindWithWrong = Test-LDAPBind -BindDN $accountDn -BindPassword 'DeliberatelyWrong-1!' -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "A wrong password is refused as invalid credentials, not as must-change" `
        -Passed ($bindWithWrong.Outcome -eq 'InvalidCredentials') `
        -Detail "Expected InvalidCredentials, got '$($bindWithWrong.Outcome)'. Directory said: $($bindWithWrong.Output)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 4: the account holder can complete the change they are being forced into
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 4: The account holder changes their own password"

    $change = Set-LDAPUserPasswordAsAccountHolder `
        -AccountName $accountName `
        -CurrentPassword $initialPassword `
        -NewPassword $chosenPassword `
        -DirectoryConfig $DirectoryConfig

    Add-TestResult -Name "The account holder changes their password using the Initial Password" `
        -Passed $change.Success `
        -Detail "Exit code $($change.ExitCode). Directory said: $($change.Output)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 5: the account is usable afterwards
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 5: Signing in with the newly chosen password"

    $bindWithChosen = Test-LDAPBind -BindDN $accountDn -BindPassword $chosenPassword -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The account holder signs in with the password they chose" `
        -Passed ($bindWithChosen.Outcome -eq 'Success') `
        -Detail "Expected Success, got '$($bindWithChosen.Outcome)'. Directory said: $($bindWithChosen.Output)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 6: JIM's own record agrees with the directory
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 6: JIM's record of the delivery"

    $initialPasswordConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId

    # A parked account is one the target refused; an expired one is one that was never given a
    # password inside its time to live. Either would mean an account provisioned without a usable
    # credential, which is the failure this whole feature exists to avoid.
    Add-TestResult -Name "No account was parked by the target refusing the Initial Password" `
        -Passed ($initialPasswordConfig.parkedAccountCount -eq 0) `
        -Detail "parkedAccountCount was $($initialPasswordConfig.parkedAccountCount); reasons: $(($initialPasswordConfig.parkedReasons | ForEach-Object { $_.reason }) -join ', ')"

    Add-TestResult -Name "No account expired waiting for an Initial Password" `
        -Passed ($initialPasswordConfig.expiredAccountCount -eq 0) `
        -Detail "expiredAccountCount was $($initialPasswordConfig.expiredAccountCount)"

    # The worker logs everything it does through the password channel; an error there means a
    # delivery that failed quietly behind a green Activity.
    Assert-NoWorkerErrors -Since $startTime
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────────────────────
$duration = (Get-Date) - $startTime
$passed = @($script:TestResults | Where-Object { $_.Passed }).Count
$failed = @($script:TestResults | Where-Object { -not $_.Passed }).Count

Write-TestSection "Scenario 17 Summary"
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
Write-Host "✓ The Initial Password JIM set is one the account holder can sign in with, and the" -ForegroundColor Green
Write-Host "  change it forces at first sign-in completes and leaves the account usable." -ForegroundColor Green
exit 0
