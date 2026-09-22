# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 17: Initial Password Provisioning

.DESCRIPTION
    Proves that the Initial Password JIM sets on an account it provisions is one the account holder can
    actually use, that the options chosen alongside it are honoured by the directory, and that a target's
    refusal of it parks the account rather than losing it, recoverably, by correcting the rule.

    The chain, in order, and why each link is needed:

      1. Provision accounts through the ordinary path (HR CSV, Metaverse, Create export to Samba AD),
         with the Synchronisation Rule's Initial Password deliberately set to a value the domain refuses.
         The export queues an Initial Password rather than setting it directly, and the Password Delivery
         Service parks every one of them: the target's own refusal, in its own words, is recorded against
         both the queue rows and the Synchronisation Rule.
      2. Correct the rule to the password the rest of this scenario uses, and prove that saving it is
         what releases the parked accounts: no re-export, no retry, no restart. The Password Delivery
         Service delivers every one of them within seconds of the save.
      3. Read an account back: it must be enabled, and must carry pwdLastSet = 0.
      4. Bind as the account holder with the Initial Password. Active Directory answers a correct
         password on a must-change account with result 49 and sub-code 773, which is a *success*
         signal here: the credential is right and the directory is insisting on a change.
      5. Bind with a deliberately wrong password. This must answer 49 sub-code 52e. Without this
         contrast, step 4 proves nothing: both are result code 49, and a scenario that only checked
         "the bind failed" would pass just as happily against a password JIM never set.
      6. Change the password as the account holder, authenticating with the Initial Password. This is
         the flow a new starter is actually put through, and the only step that proves the credential
         is usable rather than merely recognised.
      7. Bind with the newly chosen password. It must succeed outright.
      8. Confirm JIM's own record agrees: nothing parked, nothing expired.

    Everything else in JIM's password coverage stops at the connector's outgoing bytes: the unit tests
    in LdapConnectorPasswordTests assert against a mocked LDAP executor, so they prove JIM *emits* the
    right writes and prove nothing about whether a directory accepts them, still less about what happens
    when it refuses. This scenario closes both gaps: it takes the credential JIM set and signs in with
    it, and it drives a real target refusal through to a real recovery.

    Samba AD only. "Must change at next sign-in" is an Active Directory behaviour; JIM reports it as a
    downgrade on every other directory, so step 4's central assertion has nothing to bite on there.

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

        # Stage a password the target will refuse, before the accounts it applies to even exist.
        #
        # A too-short value alone will not reach the target: Set-JIMSyncRuleInitialPassword assesses a
        # Static password against the password policy JIM already discovered on this Connected System
        # (read during Setup-Scenario1's schema import) and refuses to save one that policy would reject
        # outright, precisely so an unsatisfiable configuration cannot park every account it touches.
        # Confirmed empirically: a six-character value is refused at save time with "This Connected
        # System requires at least 7 characters", never reaching the directory at all.
        #
        # To get a *genuine* target refusal instead of a save-time one, this widens the gap between what
        # JIM's cached policy still believes and what the domain actually enforces: raising the domain's
        # real minimum length leaves JIM's stale, already-discovered figure looking more permissive than
        # reality, exactly as it would the day after an administrator tightens a real directory's policy
        # and before JIM's next schema refresh notices. A password between the two lengths clears JIM's
        # save-time check on the old figure and is then refused for real when the export tries to set it.
        # docker exec's output is captured as a string per line; joined to one string before matching, because
        # -match against an array filters elements rather than setting $matches (PowerShell's array/scalar
        # -match split), which would otherwise misjudge the array as a non-match and throw below regardless.
        $domainMinPwdLengthOutput = (docker exec $DirectoryConfig.ContainerName samba-tool domain passwordsettings show 2>&1 | Out-String)
        if ($domainMinPwdLengthOutput -notmatch 'Minimum password length:\s*(\d+)') {
            throw "Could not read the domain's current minimum password length from samba-tool. Output: $domainMinPwdLengthOutput"
        }
        $originalMinPwdLength = [int]$matches[1]
        $temporaryMinPwdLength = $originalMinPwdLength + 3

        Write-Host "  Raising the domain's minimum password length from $originalMinPwdLength to $temporaryMinPwdLength, so JIM's already-discovered policy is stale..." -ForegroundColor DarkGray
        docker exec $DirectoryConfig.ContainerName samba-tool domain passwordsettings set --min-pwd-length=$temporaryMinPwdLength 2>&1 | Out-Null

        # One character short of the domain's new real minimum, but at or above the figure JIM cached
        # when it last discovered the policy: this is what makes the save succeed and the export fail.
        # Complexity is switched off on this domain, so content is irrelevant; only length distinguishes
        # what JIM will accept from what the domain will. The source pattern mixes character classes
        # anyway, so the same value would still be refused on its own merits if complexity were ever on.
        $refusedPasswordSource = 'Vt-Zkq9!Rmx-Ln4-Bwc7-Pd3-Qs5-Tuv8'
        $refusedPasswordLength = $temporaryMinPwdLength - 1
        if ($refusedPasswordSource.Length -lt $refusedPasswordLength) {
            throw "The refused-password source pattern is too short for a domain minimum length of $temporaryMinPwdLength. Lengthen `$refusedPasswordSource."
        }
        $refusedPassword = $refusedPasswordSource.Substring(0, $refusedPasswordLength)
        Write-Host "  Staging an Initial Password the domain will refuse ('$refusedPassword', $($refusedPassword.Length) characters: passes JIM's cached policy, fails the domain's raised one)..." -ForegroundColor DarkGray
        Set-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId -Source Static `
            -StaticPassword (ConvertTo-SecureString -String $refusedPassword -AsPlainText -Force) `
            -ChangeReason "Scenario 17: stage a password the target refuses, to prove parking before release" | Out-Null

        # The Create exports land here, and each one stages a Pending Initial Password. The Activity
        # completing only means the export wrote the account; delivery is the Password Delivery
        # Service's job, done asynchronously through the Connector's password channel, so a completed
        # Activity says nothing about whether the password landed, still less about the refusal below.
        Write-Host "  [3/5] Directory Export (accounts created, Initial Passwords refused and parked)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export"

        # -PassThru only carries ActivityId and TaskId, so the export's own count of objects it wrote
        # (Creates, on this the Connected System's first export) is read back from the Activity itself.
        $provisionedCount = [int](Get-JIMActivity -Id $r.activityId).executionStats.totalExported
        Write-Host "  Export provisioned $provisionedCount account(s)" -ForegroundColor Gray

        # ─────────────────────────────────────────────────────────────────────────────────────
        # Test: the refused Initial Password parks every account, naming the target's refusal
        # ─────────────────────────────────────────────────────────────────────────────────────
        Write-TestSection "Test: the refused Initial Password parks every provisioned account"

        $parkStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $parkTimeoutSeconds = 30
        $parkedRows = @()
        while ($parkStopwatch.Elapsed.TotalSeconds -lt $parkTimeoutSeconds) {
            $parkedRows = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Parked)
            if ($provisionedCount -gt 0 -and $parkedRows.Count -eq $provisionedCount) { break }
            Start-Sleep -Milliseconds 250
        }

        Add-TestResult -Name "Every provisioned account is parked by the target's refusal of the Initial Password" `
            -Passed ($provisionedCount -gt 0 -and $parkedRows.Count -eq $provisionedCount) `
            -Detail "Expected $provisionedCount parked row(s), one per account this export provisioned; found $($parkedRows.Count) after $parkTimeoutSeconds seconds."

        $wrongOrigin = @($parkedRows | Where-Object { $_.origin -ne 'Provisioned' -or $_.syncRuleId -ne $config.ExportSyncRuleId })
        Add-TestResult -Name "The parked rows are Provisioned, staged against this Synchronisation Rule" `
            -Passed ($wrongOrigin.Count -eq 0) `
            -Detail "$($wrongOrigin.Count) row(s) carried an Origin other than Provisioned, or a SyncRuleId other than $($config.ExportSyncRuleId)."

        $parkedInitialPasswordConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId
        Add-TestResult -Name "The Synchronisation Rule reports the same parked count and names the target's refusal" `
            -Passed ($parkedInitialPasswordConfig.parkedAccountCount -eq $provisionedCount -and `
                     @($parkedInitialPasswordConfig.parkedReasons).Count -gt 0 -and `
                     -not [string]::IsNullOrWhiteSpace(($parkedInitialPasswordConfig.parkedReasons | Select-Object -First 1).targetMessage)) `
            -Detail "parkedAccountCount was $($parkedInitialPasswordConfig.parkedAccountCount) (expected $provisionedCount); parkedReasons had $(@($parkedInitialPasswordConfig.parkedReasons).Count) entr(y/ies)."

        $parkedReason = $parkedInitialPasswordConfig.parkedReasons | Select-Object -First 1
        if ($parkedReason) {
            Write-Host "  Target's refusal: $($parkedReason.targetMessage)" -ForegroundColor Yellow
        }

        # ─────────────────────────────────────────────────────────────────────────────────────
        # Test: correcting the rule releases every parked account to the Password Delivery Service
        # ─────────────────────────────────────────────────────────────────────────────────────
        Write-TestSection "Test: correcting the Synchronisation Rule releases the parked accounts"

        Write-Host "  Correcting the Initial Password to the value the rest of this scenario uses..." -ForegroundColor DarkGray
        Set-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId -Source Static `
            -StaticPassword (ConvertTo-SecureString -String $initialPassword -AsPlainText -Force) `
            -ChangeReason "Scenario 17: correct the rule so the parked accounts release" | Out-Null

        $releaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $releaseTimeoutSeconds = 30
        while ($releaseStopwatch.Elapsed.TotalSeconds -lt $releaseTimeoutSeconds) {
            $outstanding = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Pending) +
                           @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Parked)
            if ($outstanding.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        }
        $releasedAfter = $releaseStopwatch.Elapsed
        Write-Host ("Parked initial passwords released and delivered {0:N1} s after the rule was saved" -f $releasedAfter.TotalSeconds) -ForegroundColor Cyan

        $stillParked = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Parked)
        $stillExpired = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Expired)

        Add-TestResult -Name "Nothing remains Parked once the Synchronisation Rule is corrected" `
            -Passed ($stillParked.Count -eq 0) `
            -Detail "$($stillParked.Count) row(s) still parked after $releaseTimeoutSeconds seconds."

        Add-TestResult -Name "Nothing expired while the accounts waited for the rule to be corrected" `
            -Passed ($stillExpired.Count -eq 0) `
            -Detail "$($stillExpired.Count) row(s) expired."

        $releasedInitialPasswordConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId
        Add-TestResult -Name "The Synchronisation Rule's parked count returns to zero once corrected" `
            -Passed ($releasedInitialPasswordConfig.parkedAccountCount -eq 0) `
            -Detail "parkedAccountCount was $($releasedInitialPasswordConfig.parkedAccountCount)."

        # The export's Activity said the account was created and its (deliberately refused) Initial
        # Password was later corrected; it says nothing about whether the corrected password has reached
        # the account yet. This wait is expected to pass instantly: the release loop above already drained
        # the queue, so it is kept as the belt-and-braces check the rest of this scenario relies on before
        # doing anything that depends on the password being live, the LDAP bind steps below included.
        $exportFinishedAt = [System.Diagnostics.Stopwatch]::StartNew()
        $drainTimeoutSeconds = 30
        $drained = $false
        while ($exportFinishedAt.Elapsed.TotalSeconds -lt $drainTimeoutSeconds) {
            $stillQueued = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Pending)
            if ($stillQueued.Count -eq 0) {
                $drained = $true
                break
            }
            Start-Sleep -Milliseconds 250
        }
        $drainedAfter = $exportFinishedAt.Elapsed

        if ($drained) {
            Write-Host ("  Initial passwords delivered {0:N1} s after the export finished" -f $drainedAfter.TotalSeconds) -ForegroundColor Cyan
        }
        else {
            # A row's own status, held flag, attempts and target message say far more than "it timed
            # out": a Parked row names the target's refusal, a Held one means Password Synchronisation is
            # switched off on the Connected System, which would be this scenario's own misconfiguration.
            foreach ($change in @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Pending)) {
                Write-Host ("      still queued: {0} on {1}, status {2}, held {3}, attempts {4}, {5}" -f `
                    $change.metaverseObjectDisplayName, $change.connectedSystemName, $change.status, `
                    $change.held, $change.attemptCount, $change.targetMessage) -ForegroundColor Yellow
            }
        }

        Add-TestResult -Name "Initial passwords are delivered by the Password Delivery Service within $drainTimeoutSeconds seconds of the export" `
            -Passed $drained `
            -Detail "The Password Synchronisation queue for $($DirectoryConfig.ConnectedSystemName) still held Pending or Delivering rows after $drainTimeoutSeconds seconds. See the rows printed above for status, held state and target message."

        # A refusal or an expiry here is the same failure Test 6 checks for later, surfaced now: without
        # this, the scenario would carry on to bind against an account whose Initial Password never
        # arrived and fail opaquely at the LDAP bind step instead of naming the real cause.
        $exportParked = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Parked)
        Add-TestResult -Name "No Initial Password was parked by the target refusing it" `
            -Passed ($exportParked.Count -eq 0) `
            -Detail "$($exportParked.Count) row(s) parked. Reasons: $(($exportParked | ForEach-Object { $_.failureReason }) -join ', ')"

        $exportExpired = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Expired)
        Add-TestResult -Name "No Initial Password expired waiting for delivery" `
            -Passed ($exportExpired.Count -eq 0) `
            -Detail "$($exportExpired.Count) row(s) expired."

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
        -Detail "parkedAccountCount was $($initialPasswordConfig.parkedAccountCount); reasons: $(($initialPasswordConfig.parkedReasons | ForEach-Object { $_.targetMessage }) -join ', ')"

    Add-TestResult -Name "No account expired waiting for an Initial Password" `
        -Passed ($initialPasswordConfig.expiredAccountCount -eq 0) `
        -Detail "expiredAccountCount was $($initialPasswordConfig.expiredAccountCount)"

    # The worker logs everything it does through the password channel; an error there means a
    # delivery that failed quietly behind a green Activity.
    Assert-NoWorkerErrors -Since $startTime
}
finally {
    # Restore the domain to the minimum length this scenario found it at, if it got as far as raising
    # it. Nothing later in this scenario depends on the restore (the chosen and corrected passwords both
    # clear the original figure by a wide margin), but leaving a test domain's policy altered is a
    # needless surprise for whoever inspects it next; run from `finally` so a failure anywhere between
    # raising it and here still restores the domain rather than leaving it stuck at the temporary value.
    if ($null -ne $originalMinPwdLength) {
        Write-Host "  Restoring the domain's minimum password length to $originalMinPwdLength..." -ForegroundColor DarkGray
        docker exec $DirectoryConfig.ContainerName samba-tool domain passwordsettings set --min-pwd-length=$originalMinPwdLength 2>&1 | Out-Null
    }

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
