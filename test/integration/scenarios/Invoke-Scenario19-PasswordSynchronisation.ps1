# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 19: Password Synchronisation

.DESCRIPTION
    Proves that a password change recorded against an identity reaches the account that identity holds in a
    Connected System, by signing in to the directory with it.

    Everything else in JIM's Password Synchronisation coverage stops short of a directory. The unit tests assert
    against a mocked LDAP executor and an in-memory queue, so they prove JIM *emits* the right write and moves
    the right rows; they prove nothing about whether a directory accepts the result. This scenario closes that
    gap the way Scenario 17 does for the Initial Password: it takes the password JIM delivered and binds with it.

    Four questions, in the order they matter, and each with the contrast that gives its answer meaning:

      1. **Does a switched-off system accumulate, or discard?** Password Synchronisation is configured on the
         directory and switched off. Password changes are recorded for three people, and the queue must hold
         them, marked as held rather than due, with the directory still answering the password it had before.
         Requirement 2, and the failure this whole feature exists to prevent: a maintenance window during which
         every password change is silently lost for one system.
      2. **Does coalescing keep the newest password?** One person's password is changed three times while the
         system is off. Exactly one queued change must remain, and after delivery the directory must hold the
         *third* password, not the first. A test that only counted rows would pass on a queue that kept the
         oldest.
      3. **Does enabling deliver what accumulated, unaided?** The system is switched on and nothing else is
         done: no retry, no run profile, no restart. Every held change must be delivered on JIM's own initiative
         (requirement 3), and the accounts must then sign in with their new passwords and refuse their old ones.
      4. **Does an ordinary change reach the directory once the system is live?** A fourth password change, with
         the system enabled throughout, must be delivered without anybody doing anything.

    Two invariants are asserted throughout rather than as a step: no password value appears in any JIM log, and
    no queue response carries one. They are the reason the feature is allowed to hold passwords at all.

    Samba AD only. Provisioning enables each account as its Initial Password lands, which is an Active Directory
    operation; an account left disabled cannot be signed in as, and signing in is the whole proof here.

.PARAMETER Step
    Which part to execute (Provision, Synchronise, All)

.PARAMETER Template
    Accepted for runner compatibility and deliberately not used for sizing. This scenario asserts against four
    accounts, so a larger template would only lengthen the export for no added coverage; it always provisions at
    Micro.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. This scenario provisions the accounts it asserts against and needs no
    pre-populated directory data.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig

.EXAMPLE
    ./Invoke-Scenario19-PasswordSynchronisation.ps1 -ApiKey "jim_..." -Template Micro
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Provision", "Synchronise", "All")]
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

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

if ($DirectoryConfig.UserObjectClass -ne "user") {
    throw "Scenario 19 requires Samba AD. Provisioning enables each account as its Initial Password lands, which " +
          "is an Active Directory operation with no equivalent on $($DirectoryConfig.ConnectedSystemName); an " +
          "account left disabled cannot be signed in as, and signing in is how this scenario proves a " +
          "synchronised password arrived."
}

<#
    The passwords this scenario sends.

    Each satisfies a stock Active Directory complexity rule on its own merits, even though the test domain is
    provisioned with NOCOMPLEXITY=true: a password that only passes because complexity is switched off would make
    the scenario prove less than it appears to.

    They share no token with the account names the HR template generates, because Active Directory refuses a
    password containing the sAMAccountName or a three-character-or-longer piece of the display name, and a
    scenario that tripped that would fail for a reason nobody was testing.

    They are also pairwise unlike each other, so a bind succeeding with one cannot be confused with a bind
    succeeding with another. The three Coalesced values matter most here: the point of that assertion is which of
    the three the directory ended up holding.
#>
$passwords = @{
    Held           = 'Ravenscroft-4-Lantern!'
    Coalesced      = @('Windlass-1-Thicket!', 'Pinnacle-2-Ferrous!', 'Saltmarsh-3-Quiver!')
    WhileEnabled   = 'Kingfisher-8-Bracken!'
}

# Every password value this scenario puts into JIM, for the never-log sweep. Flattened once here so the sweep
# cannot drift from the list above by someone adding a password and forgetting the assertion.
$allPasswords = @($passwords.Held) + $passwords.Coalesced + @($passwords.WhileEnabled)

# Four accounts are what this scenario asserts against, so it always provisions at Micro no matter what the
# runner passed. A larger template would lengthen the export and prove nothing further.
$effectiveTemplate = "Micro"

# How long delivery is given once a pass has been asked for. Generous: a delivery pass is raised the moment work
# is queued or a system is enabled, so this is a bound on a directory write and a queue read, not on a poll
# interval. Exceeded, it means delivery is not happening at all, which is what the failure should say.
$deliveryTimeoutSeconds = 180

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

<#
.SYNOPSIS
    Parses the accounts out of an LDIF search result, newest-parser-wins over Scenario 17's inline one.

.DESCRIPTION
    Scenario 17 parses the first entry only, inline. This scenario needs several, so it parses properly: entries
    are separated by a blank line in LDIF, and a dn line starts one.
#>
function Get-LDIFAccounts {
    # AllowEmptyString, because LDIF separates entries with a blank line and PowerShell validates every
    # element of a Mandatory [string[]]: without it, binding fails with "argument is an empty string" on
    # the first entry separator, which reads as though the search returned nothing.
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string[]]$RawLines)

    $accounts = @()
    $current = $null

    foreach ($line in $RawLines) {
        if ($line -match '^\s*#') { continue }

        if ($line -match '^dn:\s*(.+)$') {
            if ($current -and $current.ContainsKey('sAMAccountName')) { $accounts += $current }
            $current = @{ dn = $matches[1].Trim() }
            continue
        }

        if ($null -ne $current -and $line -match '^(sAMAccountName|userAccountControl):\s*(.+)$') {
            $current[$matches[1]] = $matches[2].Trim()
        }
    }

    if ($current -and $current.ContainsKey('sAMAccountName')) { $accounts += $current }
    return $accounts
}

<#
.SYNOPSIS
    Waits until nothing is left on the queue for the given identities, and reports what remains if not.
#>
function Wait-ForQueueToDrain {
    param(
        [Parameter(Mandatory=$true)][guid[]]$MetaverseObjectIds,
        [Parameter(Mandatory=$true)][string]$Description
    )

    $drained = Wait-ForCondition -Description $Description -TimeoutSeconds $deliveryTimeoutSeconds -IntervalSeconds 5 -Condition {
        $outstanding = 0
        foreach ($id in $MetaverseObjectIds) {
            $outstanding += @(Get-JIMPendingPasswordChange -MetaverseObjectId $id).Count
        }
        return $outstanding -eq 0
    }

    if (-not $drained) {
        # The rows themselves say far more than "it timed out": a Parked row names the target's refusal, and a
        # Held one means the system is still switched off, which would be this scenario's own mistake.
        foreach ($id in $MetaverseObjectIds) {
            foreach ($change in @(Get-JIMPendingPasswordChange -MetaverseObjectId $id)) {
                Write-Host ("      still queued: {0} on {1}, status {2}, held {3}, attempts {4}, {5}" -f `
                    $change.metaverseObjectDisplayName, $change.connectedSystemName, $change.status, `
                    $change.held, $change.attemptCount, $change.targetMessage) -ForegroundColor Yellow
            }
        }
    }

    return $drained
}

Write-TestSection "Scenario 19: Password Synchronisation"
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

$config = & "$PSScriptRoot/../Setup-Scenario19.ps1" `
    -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $effectiveTemplate -DirectoryConfig $DirectoryConfig

if (-not $config) {
    throw "Failed to set up Scenario 19 configuration"
}

$initialPassword = $config.InitialPassword
$ldapSystemId = $config.LDAPSystemId

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Step 1: Provision the accounts this scenario changes passwords for
    # ─────────────────────────────────────────────────────────────────────────────────────────
    if ($Step -in @("Provision", "All")) {
        Write-TestSection "Step 1: Provisioning accounts into $($DirectoryConfig.ConnectedSystemName)"

        Write-Host "  [1/5] HR CSV Full Import..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import"

        Write-Host "  [2/5] HR CSV Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Delta Sync"

        Write-Host "  [3/5] Directory Export (accounts created, Initial Passwords set)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $ldapSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export"

        # A Full Import, not a Delta Import: this is the Connected System's first import, so there is no
        # persisted baseline for a delta to compare against and the Connector refuses it outright.
        Write-Host "  [4/5] Directory Full Import (confirms the exports)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $ldapSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Full Import"

        Write-Host "  [5/5] Directory Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $ldapSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Delta Sync"

        Write-Host "  ✓ Provisioning complete" -ForegroundColor Green
    }

    if ($Step -eq "Provision") {
        Write-Host "`nProvision step complete. Re-run with -Step Synchronise to assert against the accounts." -ForegroundColor Cyan
        return
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Step 2: Choose the people this scenario changes passwords for
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 2: Selecting provisioned accounts and their identities"

    $searchOutput = Invoke-LDAPSearch `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.UserContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter "(&(objectClass=user)(sAMAccountName=*))" `
        -Attributes @("sAMAccountName", "userAccountControl")

    if (-not $searchOutput) {
        throw "The directory search returned nothing under $($DirectoryConfig.UserContainer). Either the export " +
              "provisioned nothing, or the container is wrong; check the Directory Export Activity."
    }

    $lines = Expand-LDIFFoldedLine -RawLdif ($searchOutput -join "`n")

    # Enabled accounts only. Bit 0x2 is ACCOUNTDISABLE, and a disabled account cannot be bound as whatever
    # password it holds, so one would fail every assertion below for a reason that is not the one under test.
    # The HR template marks some people Archived, and Setup-Scenario1's userAccountControl expression disables
    # exactly those.
    $accounts = @(Get-LDIFAccounts -RawLines $lines |
        Where-Object { $_.ContainsKey('userAccountControl') -and (([int]$_.userAccountControl) -band 0x2) -eq 0 })

    if ($accounts.Count -lt 3) {
        throw "Only $($accounts.Count) enabled account(s) found under $($DirectoryConfig.UserContainer); this " +
              "scenario needs at least three. Check the Directory Export Activity and the Initial Password's " +
              "parked count: an account whose password was refused is left disabled."
    }

    # Sorted so a re-run picks the same people, which makes a failure reproducible rather than a lottery.
    $chosen = @($accounts | Sort-Object { $_.sAMAccountName } | Select-Object -First 3)

    $people = @()
    foreach ($account in $chosen) {
        $mvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Account Name" `
            -AttributeValue $account.sAMAccountName -PageSize 5) | Select-Object -First 1

        if (-not $mvo) {
            throw "No Metaverse Object holds Account Name '$($account.sAMAccountName)', though the directory " +
                  "holds an account with it. The account was provisioned by a Synchronisation Rule, so its " +
                  "identity must exist; check the Directory Delta Sync Activity."
        }

        $people += [PSCustomObject]@{
            AccountName = $account.sAMAccountName
            Dn          = $account.dn
            MvoId       = [guid]$mvo.id
        }
    }

    $held = $people[0]
    $coalesced = $people[1]
    $whileEnabled = $people[2]

    Write-Host "  Accumulate-while-off : $($held.AccountName)" -ForegroundColor Cyan
    Write-Host "  Coalescing           : $($coalesced.AccountName)" -ForegroundColor Cyan
    Write-Host "  Delivered live       : $($whileEnabled.AccountName)" -ForegroundColor Cyan

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 1: the baseline every later assertion is read against
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 1: The accounts sign in with the password they were provisioned with"

    foreach ($person in $people) {
        $bind = Test-LDAPBind -BindDN $person.Dn -BindPassword $initialPassword -DirectoryConfig $DirectoryConfig
        Add-TestResult -Name "$($person.AccountName) signs in with its Initial Password" `
            -Passed ($bind.Outcome -eq 'Success') `
            -Detail "Expected Success, got '$($bind.Outcome)'. Directory said: $($bind.Output). Nothing below can be interpreted until this passes: every later assertion is 'the password changed from this one'."
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 2: a switched-off system accumulates rather than discarding (requirement 2)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 2: A switched-off Connected System accumulates password changes"

    $heldSecure = ConvertTo-SecureString -String $passwords.Held -AsPlainText -Force
    $queueResult = Sync-JIMMetaverseObjectPassword -Id $held.MvoId -Password $heldSecure -Force

    Add-TestResult -Name "The change is queued for the switched-off Connected System" `
        -Passed ((-not $queueResult.queuedForNoSystems) -and @($queueResult.targets).Count -eq 1) `
        -Detail "queuedForNoSystems was '$($queueResult.queuedForNoSystems)' with $(@($queueResult.targets).Count) target(s). A system that is configured but switched off must still be a target; discarding the change here is what requirement 2 forbids."

    $target = @($queueResult.targets) | Select-Object -First 1
    Add-TestResult -Name "The response says the change is held rather than on its way" `
        -Passed ($null -ne $target -and $target.enabled -eq $false) `
        -Detail "The target reported enabled='$($target.enabled)'. 'Queued' alone reads as 'delivered soon'; an administrator has to be able to tell that this one is waiting on somebody switching the system on."

    # Three changes for one person, so coalescing has something to coalesce.
    foreach ($password in $passwords.Coalesced) {
        $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
        Sync-JIMMetaverseObjectPassword -Id $coalesced.MvoId -Password $secure -Force | Out-Null
    }

    $coalescedQueue = @(Get-JIMPendingPasswordChange -MetaverseObjectId $coalesced.MvoId)
    Add-TestResult -Name "Three password changes for one person leave one queued change" `
        -Passed ($coalescedQueue.Count -eq 1) `
        -Detail "The queue holds $($coalescedQueue.Count) change(s) for $($coalesced.AccountName). Only the newest password should ever be sent, so a second change replaces an undelivered first rather than queueing behind it."

    $heldQueue = @(Get-JIMPendingPasswordChange -MetaverseObjectId $held.MvoId)
    Add-TestResult -Name "A queued change for a switched-off system is held, not due" `
        -Passed ($heldQueue.Count -eq 1 -and $heldQueue[0].held -eq $true -and $heldQueue[0].due -eq $false) `
        -Detail "held='$($heldQueue[0].held)', due='$($heldQueue[0].due)'. A delivery pass steps over a switched-off system, so reporting the change as due would put 'Due now' against a row nothing will attempt."

    $summary = Get-JIMPendingPasswordChange -Summary
    Add-TestResult -Name "The queue summary counts held changes as waiting, and none as due" `
        -Passed ($summary.waitingCount -ge 2 -and $summary.dueCount -eq 0) `
        -Detail "waitingCount=$($summary.waitingCount), dueCount=$($summary.dueCount). A large due count is meant to read as 'the queue is not being drained'; held changes must not produce that reading."

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 3: nothing reached the directory while the system was off
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 3: Nothing was delivered while the system was switched off"

    $bindWithNew = Test-LDAPBind -BindDN $held.Dn -BindPassword $passwords.Held -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The directory does not yet hold the queued password" `
        -Passed ($bindWithNew.Outcome -eq 'InvalidCredentials') `
        -Detail "Expected InvalidCredentials, got '$($bindWithNew.Outcome)'. Delivery to a switched-off system would make the accumulate assertion above meaningless."

    $bindWithOld = Test-LDAPBind -BindDN $held.Dn -BindPassword $initialPassword -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The account still signs in with the password it had" `
        -Passed ($bindWithOld.Outcome -eq 'Success') `
        -Detail "Expected Success, got '$($bindWithOld.Outcome)'. Holding a change must leave the account exactly as it was, not part-way through anything."

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 4: enabling delivers what accumulated, unaided (requirement 3)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 4: Switching Password Synchronisation on delivers what accumulated"

    Set-JIMConnectedSystemPasswordSynchronisation `
        -Id $ldapSystemId `
        -Enabled $true `
        -ChangeReason "Scenario 19: the change window has closed" | Out-Null

    Write-Host "  Password Synchronisation switched on; nothing else will be done." -ForegroundColor Gray

    $drained = Wait-ForQueueToDrain -MetaverseObjectIds @($held.MvoId, $coalesced.MvoId) `
        -Description "the queued password changes to be delivered without further intervention"

    Add-TestResult -Name "Enabling the system delivers what accumulated, with no further intervention" `
        -Passed $drained `
        -Detail "The queue still held changes after $deliveryTimeoutSeconds seconds. Requirement 3 is that enabling a system processes its queued changes automatically: no retry, no run profile, no restart."

    $bindDelivered = Test-LDAPBind -BindDN $held.Dn -BindPassword $passwords.Held -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The account signs in with the password queued while the system was off" `
        -Passed ($bindDelivered.Outcome -eq 'Success') `
        -Detail "Expected Success, got '$($bindDelivered.Outcome)'. Directory said: $($bindDelivered.Output)"

    $bindSuperseded = Test-LDAPBind -BindDN $held.Dn -BindPassword $initialPassword -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The password it was provisioned with no longer works" `
        -Passed ($bindSuperseded.Outcome -eq 'InvalidCredentials') `
        -Detail "Expected InvalidCredentials, got '$($bindSuperseded.Outcome)'. Without this contrast the assertion above proves only that some password works, not that JIM's did."

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 5: coalescing kept the newest password, not the first
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 5: Of three password changes, the directory holds the newest"

    $newest = $passwords.Coalesced[-1]
    $oldest = $passwords.Coalesced[0]

    $bindNewest = Test-LDAPBind -BindDN $coalesced.Dn -BindPassword $newest -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The account signs in with the third of the three passwords" `
        -Passed ($bindNewest.Outcome -eq 'Success') `
        -Detail "Expected Success, got '$($bindNewest.Outcome)'. Directory said: $($bindNewest.Output)"

    $bindOldest = Test-LDAPBind -BindDN $coalesced.Dn -BindPassword $oldest -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The first of the three no longer works" `
        -Passed ($bindOldest.Outcome -eq 'InvalidCredentials') `
        -Detail "Expected InvalidCredentials, got '$($bindOldest.Outcome)'. A queue that coalesced to one row but kept the oldest password would pass the row count assertion and fail the person."

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 6: an ordinary change against a live system
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 6: A password change with the system enabled throughout"

    $liveSecure = ConvertTo-SecureString -String $passwords.WhileEnabled -AsPlainText -Force
    $liveResult = Sync-JIMMetaverseObjectPassword -Id $whileEnabled.MvoId -Password $liveSecure -Force

    $liveTarget = @($liveResult.targets) | Select-Object -First 1
    Add-TestResult -Name "The response says the change is on its way rather than held" `
        -Passed ($null -ne $liveTarget -and $liveTarget.enabled -eq $true) `
        -Detail "The target reported enabled='$($liveTarget.enabled)'."

    $liveDrained = Wait-ForQueueToDrain -MetaverseObjectIds @($whileEnabled.MvoId) `
        -Description "the password change to be delivered to the live Connected System"

    Add-TestResult -Name "The change is delivered without anybody doing anything" `
        -Passed $liveDrained `
        -Detail "The change was still queued after $deliveryTimeoutSeconds seconds."

    $bindLive = Test-LDAPBind -BindDN $whileEnabled.Dn -BindPassword $passwords.WhileEnabled -DirectoryConfig $DirectoryConfig
    Add-TestResult -Name "The account signs in with the synchronised password" `
        -Passed ($bindLive.Outcome -eq 'Success') `
        -Detail "Expected Success, got '$($bindLive.Outcome)'. Directory said: $($bindLive.Output)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 7: delivery leaves nothing behind, and nothing parked
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 7: What the queue holds afterwards"

    $finalSummary = Get-JIMPendingPasswordChange -Summary

    Add-TestResult -Name "Nothing was parked by the directory refusing a password" `
        -Passed ($finalSummary.parkedCount -eq 0) `
        -Detail "parkedCount was $($finalSummary.parkedCount). A parked change is one the target refused; the passwords this scenario sends are chosen to satisfy a stock complexity rule, so a refusal is a genuine finding."

    Add-TestResult -Name "Nothing expired waiting to be delivered" `
        -Passed ($finalSummary.expiredCount -eq 0) `
        -Detail "expiredCount was $($finalSummary.expiredCount)."

    Add-TestResult -Name "A delivered change leaves nothing behind" `
        -Passed ($finalSummary.waitingCount -eq 0) `
        -Detail "waitingCount was $($finalSummary.waitingCount). Nothing is kept once the target has the password: there is no value worth retaining and every reason not to."

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 8: the invariant that lets JIM hold passwords at all
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test 8: No password value reached a log or an API response"

    # Read from the containers rather than from a file, so this covers everything JIM wrote at every level,
    # including anything a library wrote on its behalf. The window starts before the scenario did.
    $since = $startTime.AddMinutes(-1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $leaked = @()

    foreach ($container in @('jim.web', 'jim.worker', 'jim.scheduler')) {
        $logText = (docker logs --since $since $container 2>&1 | Out-String)
        foreach ($password in $allPasswords) {
            if ($logText -match [regex]::Escape($password)) {
                $leaked += "$container logged a password value"
            }
        }
    }

    Add-TestResult -Name "No password value appears in any JIM log" `
        -Passed ($leaked.Count -eq 0) `
        -Detail ($leaked -join '; ')

    # The queue is the one API surface that reads rows which hold an encrypted password, so it is the one worth
    # asserting against by shape rather than by inspection: the type the surfaces bind to has nowhere to put a
    # password, and this fails if that ever stops being true.
    $queueRows = @(Get-JIMPendingPasswordChange -PageSize 50)
    $passwordProperties = @()
    foreach ($row in $queueRows) {
        $passwordProperties += @($row.PSObject.Properties.Name | Where-Object { $_ -match '(?i)password' -and $_ -notmatch '(?i)^pendingpassword' })
    }

    Add-TestResult -Name "No queue response carries a password value" `
        -Passed ($passwordProperties.Count -eq 0) `
        -Detail "Properties matching 'password': $(($passwordProperties | Select-Object -Unique) -join ', ')"

    # The worker logs everything it does through the password channel; an error there means a delivery that
    # failed quietly behind a green Activity.
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

Write-TestSection "Scenario 19 Summary"
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
Write-Host "✓ A password change reaches the account it belongs to: held while the system was off," -ForegroundColor Green
Write-Host "  delivered the moment it was switched on, newest password only, and never logged." -ForegroundColor Green
exit 0
