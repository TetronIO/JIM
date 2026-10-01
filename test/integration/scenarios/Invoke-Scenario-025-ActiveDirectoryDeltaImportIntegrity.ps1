# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 025: Active Directory Delta Import Integrity (Recycle Bin on and off; restore from backup)

.DESCRIPTION
    Proves two things about the LDAP Connector's Delta Import that only a real domain controller can show
    (PRD functional requirements 18 and 19, Examples 3):

      RecycleBin         A user created out of band is imported by a Delta Import as one add; when it is deleted,
                         the next Delta Import reports exactly one deletion and marks its Connected System Object
                         Obsolete (JIM's deletion model: it stays Obsolete until a synchronisation run deletes
                         it); and the Delta Import after that reports nothing, so the deletion is reported ONCE.
                         It runs against Primary (dc-primary, PANOPLY.LOCAL) with the Active Directory Recycle
                         Bin ON and against Source (dc-source, RESURGAM.LOCAL) with it OFF, because the two
                         delete an object differently (a deleted object that keeps its attributes and can be
                         restored, against a tombstone stripped of them) and a Delta Import must report each
                         once. The scenario reads the Recycle Bin's state from the directory first, so a lab built
                         the other way round fails there and not later as a confusing count.

      RestoreFromBackup  Every domain controller reset in the lab is a Hyper-V production checkpoint revert,
                         which Active Directory treats as a restore from backup: the domain controller is issued
                         a new invocationId, and the change numbers (USNs) a Delta Import reads from restart.
                         A USN watermark recorded before the revert would silently skip or repeat changes after
                         it. The scenario records a watermark with a Full Import and a clean Delta Import,
                         reverts Primary to its baseline checkpoint in the middle of the run, and asserts that
                         the next Delta Import FAILS FAST, naming the change of invocationId (both the previous
                         and the current) and the remedy, importing nothing; that a Full Import then succeeds and
                         reconciles what the restore lost; and that the Delta Import after it is clean.

    Both steps use their own Connected System, built by Setup-Scenario-025.ps1 from nothing (an import with no
    Synchronisation Rules), so a count here is a count of what the import reported and nothing else.

    Why the failing Delta Import is read differently from every other run: the worker logs an Error line when a
    run fails (a failure it expects, but a log line of that level all the same), the runner's error watcher turns
    such a line into an abort of the very wait that would read the result, and Assert-NoWorkerErrors would fail the
    scenario at the end. The failing run is therefore started without -Wait and polled here, the watcher is
    drained after it (and the lines it held checked to be the expected refusal and nothing else), and the final
    log scan is told about the one run whose Error line is expected.

    Active Directory lab only. Primary and Source are reverted to their baseline checkpoint by the runner before
    the scenario starts; the users the scenario creates are gone with the next revert.

.PARAMETER Step
    Which part to execute (RecycleBin, RestoreFromBackup, All). The steps are independent: each builds its own
    Connected System.

.PARAMETER Template
    Accepted for runner compatibility and not used: the scenario creates and deletes its own users.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario needs nothing populated beyond the lab's baseline.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary.
    The Source configuration is read by the scenario itself.

.EXAMPLE
    ./Invoke-Scenario-025-ActiveDirectoryDeltaImportIntegrity.ps1 -ApiKey "jim_..."
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Template, WaitSeconds and SkipPopulate are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("RecycleBin", "RestoreFromBackup", "All")]
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

# Helpers. Directory-Helpers is what creates and deletes the users (New-DirectoryUser, Remove-DirectoryUser) and
# reads the directory back; Invoke-LabControl and ActiveDirectoryLab-Helpers are what revert the domain controller.
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"
. "$PSScriptRoot/../utils/Invoke-LabControl.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
}

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

if ($DirectoryConfig.DirectoryType -ne "ActiveDirectory") {
    throw "Scenario 025 requires the Active Directory lab. It asserts what a Delta Import does with a real domain " +
          "controller's Recycle Bin, Deleted Objects container and invocationId, and $($DirectoryConfig.ConnectedSystemName) " +
          "is a '$($DirectoryConfig.DirectoryType)' directory. Run-IntegrationTests.ps1 should have rejected this " +
          "combination before this script was invoked."
}

$primaryConfig = $DirectoryConfig
$sourceConfig = $null
if ($Step -in @("RecycleBin", "All")) {
    $sourceConfig = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source
}

$script:TestResults = @()
$startTime = Get-Date

# The activity id of the Delta Import the scenario expects to fail, so that the one Error line it causes in the
# worker log can be told apart from any other.
$script:ExpectedFailureActivityId = $null

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

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"

function Connect-JimSession {
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
}

function Disconnect-JimSession {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

function Initialize-LabConnectedSystem {
    <#
    .SYNOPSIS
        Builds a fresh Connected System for one domain controller and returns its ids.

    .DESCRIPTION
        Setup-Scenario-025.ps1 loads the JIM PowerShell module and unloads it again when it finishes, which would
        take this scenario's own session with it, so the session is closed around the call and opened again after.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Config)

    Disconnect-JimSession
    $built = @(& "$PSScriptRoot/../Setup-Scenario-025.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -DirectoryConfig $Config) |
        Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
    Connect-JimSession

    if (-not $built) {
        throw "Setup-Scenario-025.ps1 returned no configuration for $($Config.ConnectedSystemName)"
    }
    return $built
}

function Get-PropertyText {
    <#
    .SYNOPSIS
        A property of an API object as text, or an empty string when the API left it out.

    .DESCRIPTION
        The API omits a property whose value is null, and Set-StrictMode turns reading a missing property into a
        terminating error, which would hide the assertion that was about to say what was wrong. An Activity that
        did not fail has no errorMessage, and this scenario reads it to find out whether one did.
    #>
    param(
        [Parameter(Mandatory=$true)]$Object,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return "" }
    return "$($property.Value)"
}

function Get-ImportChange {
    <#
    .SYNOPSIS
        What an import Activity reported: objects added, updated and deleted, and the total of every change.
    #>
    param([Parameter(Mandatory=$true)][string]$ActivityId)

    $stats = Get-JIMActivityStats -Id $ActivityId
    return @{
        Added   = [int]$stats.totalCsoAdds
        Updated = [int]$stats.totalCsoUpdates
        Deleted = [int]$stats.totalCsoDeletes
        Total   = [int]$stats.totalObjectChangeCount
    }
}

function Format-ImportChange {
    param([Parameter(Mandatory=$true)][hashtable]$Change)
    return "added $($Change.Added), updated $($Change.Updated), deleted $($Change.Deleted), all changes $($Change.Total)"
}

function Invoke-ImportRun {
    <#
    .SYNOPSIS
        Runs a Full Import or a Delta Import, requires it to complete cleanly, and returns what it reported.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$System,
        [Parameter(Mandatory=$true)][ValidateSet("Full", "Delta")][string]$Kind,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $profileId = if ($Kind -eq "Full") { $System.FullImportProfileId } else { $System.DeltaImportProfileId }
    $run = Start-JIMRunProfile -ConnectedSystemId $System.ConnectedSystemId -RunProfileId $profileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $run.activityId -Name $Name
    return Get-ImportChange -ActivityId $run.activityId
}

function Test-RecycleBinEnabled {
    <#
    .SYNOPSIS
        Whether the Active Directory Recycle Bin is on, read from the directory itself.

    .DESCRIPTION
        The Recycle Bin Feature object lists, in its back link msDS-EnabledFeatureBL, the scope it was enabled
        for; the attribute is absent until it is enabled. Read as the domain administrator. These forests are
        single-domain, so the Configuration partition sits directly under the base DN.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Config)

    $featureDn = "CN=Recycle Bin Feature,CN=Optional Features,CN=Directory Service,CN=Windows NT,CN=Services,CN=Configuration,$($Config.BaseDN)"
    $feature = Get-DirectoryEntry -DirectoryConfig $Config -Dn $featureDn -Attributes 'msDS-EnabledFeatureBL'
    if ($null -eq $feature) {
        throw "The Recycle Bin Feature object was not found at '$featureDn' on $($Config.VmName), so the state of the Recycle Bin cannot be read."
    }
    return $feature.ContainsKey('msDS-EnabledFeatureBL')
}

function Get-DomainControllerInvocationId {
    <#
    .SYNOPSIS
        The invocationId of the domain controller's NTDS Settings object, read from the directory.

    .DESCRIPTION
        The oracle the assertions compare JIM's error text against. Each forest has one domain controller, so the
        only nTDSDSA object in the Configuration partition is its NTDS Settings. invocationId is a binary GUID,
        which ldapsearch prints in base64.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Config)

    $raw = Invoke-LDAPSearch -DirectoryConfig $Config -BaseDN "CN=Configuration,$($Config.BaseDN)" `
        -BindDN $Config.BindDN -BindPassword $Config.BindPassword `
        -Filter "(objectClass=nTDSDSA)" -Attributes @("invocationId")
    if (-not $raw) {
        throw "Could not read the NTDS Settings object of $($Config.VmName), so its invocationId is unknown."
    }

    foreach ($line in (Expand-LDIFFoldedLine -RawLdif ($raw -join "`n"))) {
        if ($line -match '^invocationId:: (?<value>\S+)$') {
            return [guid]::new([System.Convert]::FromBase64String($Matches['value']))
        }
    }
    throw "The NTDS Settings object of $($Config.VmName) carried no invocationId. Read: $(($raw | Out-String).Trim())"
}

function Get-RecordedInvocationId {
    <#
    .SYNOPSIS
        The invocationId JIM shows for a Connected System (the "Invocation Id" detected capability), or $null.
    #>
    param([Parameter(Mandatory=$true)][int]$ConnectedSystemId)

    $capability = @(Get-JIMConnectedSystemCapability -ConnectedSystemId $ConnectedSystemId) |
        Where-Object { $_.name -eq "Invocation Id" } | Select-Object -First 1
    if ($null -eq $capability) { return $null }
    return "$($capability.value)"
}

function Get-ObjectByDisplayName {
    <#
    .SYNOPSIS
        A Connected System Object's header, found by display name, optionally only if it has the given status.
    #>
    param(
        [Parameter(Mandatory=$true)][int]$ConnectedSystemId,
        [Parameter(Mandatory=$true)][string]$DisplayName,
        [Parameter(Mandatory=$false)][ValidateSet("Normal", "Obsolete")][string]$Status
    )

    $query = @{ ConnectedSystemId = $ConnectedSystemId; Search = $DisplayName; PageSize = 10 }
    if ($Status) { $query.Status = $Status }
    return @(Get-JIMConnectedSystemObject @query) |
        Where-Object { (Get-PropertyText -Object $_ -Name "displayName") -eq $DisplayName } | Select-Object -First 1
}

function Wait-ActivityTerminal {
    <#
    .SYNOPSIS
        Polls an Activity until it reaches a terminal status and returns it. Does not consult the error watcher.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$ActivityId,
        [Parameter(Mandatory=$false)][int]$TimeoutSeconds = 300
    )

    $terminalStatuses = @("Complete", "CompleteWithWarning", "CompleteWithError", "FailedWithError", "Cancelled")
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $activity = Get-JIMActivity -Id $ActivityId
        $status = if ($activity) { Get-PropertyText -Object $activity -Name "status" } else { "" }
        if ($status -in $terminalStatuses) { return $activity }
        if ((Get-Date) -gt $deadline) {
            throw "Activity $ActivityId had not finished $TimeoutSeconds seconds after it started (status: '$status')."
        }
        Start-Sleep -Seconds 2
    }
}

function Add-ScenarioUser {
    <#
    .SYNOPSIS
        Creates the user the scenario imports and deletes, as the domain administrator, in the Connected System's Container.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$SamAccountName,
        [Parameter(Mandatory=$true)][string]$GivenName,
        [Parameter(Mandatory=$true)][string]$Surname
    )

    $displayName = "$GivenName $Surname"
    $created = New-DirectoryUser -DirectoryConfig $Config `
        -Dn "CN=$displayName,$($Config.UserContainer)" -SamAccountName $SamAccountName `
        -Password (Get-ActiveDirectoryFixturePassword -Avoid @($GivenName, $Surname, $SamAccountName)) `
        -Attributes @{ givenName = $GivenName; sn = $Surname; displayName = $displayName }

    if ($created.Outcome -ne "Created") {
        throw "Could not create '$displayName' ($SamAccountName) on $($Config.VmName): $($created.Outcome). " +
              "An 'AlreadyExists' means the domain controller was not reverted to its baseline checkpoint before this run. Output: $($created.Output)"
    }
    return $displayName
}

# ---------------------------------------------------------------------------------------------------------------
# RecycleBin: one domain controller
# ---------------------------------------------------------------------------------------------------------------
function Invoke-RecycleBinTest {
    param(
        [Parameter(Mandatory=$true)][string]$Label,
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][bool]$ExpectRecycleBin,
        [Parameter(Mandatory=$true)][string]$SamAccountName,
        [Parameter(Mandatory=$true)][string]$GivenName,
        [Parameter(Mandatory=$true)][string]$Surname
    )

    $recycleBinWord = if ($ExpectRecycleBin) { "on" } else { "off" }
    Write-TestSection "RecycleBin: $Label ($($Config.VmName)), Active Directory Recycle Bin $recycleBinWord"

    $recycleBinIsOn = Test-RecycleBinEnabled -Config $Config
    Add-TestResult -Name "$Label has the Active Directory Recycle Bin $recycleBinWord" `
        -Passed ($recycleBinIsOn -eq $ExpectRecycleBin) `
        -Detail "The lab is built with the Recycle Bin on for dc-primary and off for dc-source (test/integration/ad-lab/README.md). Read from the directory: $(if ($recycleBinIsOn) { 'on' } else { 'off' })."

    $system = Initialize-LabConnectedSystem -Config $Config
    $displayName = "$GivenName $Surname"

    Write-Host "  [1/6] Full Import (establishes the watermark)..." -ForegroundColor DarkGray
    Invoke-ImportRun -System $system -Kind Full -Name "$Label Full Import" | Out-Null

    Write-Host "  [2/6] Delta Import (nothing has changed)..." -ForegroundColor DarkGray
    $quiet = Invoke-ImportRun -System $system -Kind Delta -Name "$Label Delta Import (nothing changed)"
    Add-TestResult -Name "$Label Delta Import reports nothing when nothing has changed" `
        -Passed ($quiet.Total -eq 0 -and $quiet.Added -eq 0 -and $quiet.Updated -eq 0 -and $quiet.Deleted -eq 0) `
        -Detail "Reported: $(Format-ImportChange -Change $quiet)"

    Write-Host "  [3/6] Creating $displayName ($SamAccountName) out of band, then Delta Import..." -ForegroundColor DarkGray
    Add-ScenarioUser -Config $Config -SamAccountName $SamAccountName -GivenName $GivenName -Surname $Surname | Out-Null
    $afterCreate = Invoke-ImportRun -System $system -Kind Delta -Name "$Label Delta Import (user created)"
    Add-TestResult -Name "$Label Delta Import reports exactly one add for the user created out of band" `
        -Passed ($afterCreate.Added -eq 1 -and $afterCreate.Updated -eq 0 -and $afterCreate.Deleted -eq 0) `
        -Detail "Expected one add and nothing else. Reported: $(Format-ImportChange -Change $afterCreate)"

    $imported = Get-ObjectByDisplayName -ConnectedSystemId $system.ConnectedSystemId -DisplayName $displayName -Status Normal
    Add-TestResult -Name "$Label holds a Connected System Object for '$displayName' with the status Normal" `
        -Passed ($null -ne $imported) `
        -Detail "No Normal Connected System Object named '$displayName' was found after the Delta Import."

    Write-Host "  [4/6] Deleting $displayName, then Delta Import..." -ForegroundColor DarkGray
    $removed = Remove-DirectoryUser -DirectoryConfig $Config -SamAccountName $SamAccountName
    if ($removed.Outcome -ne "Deleted") {
        throw "Could not delete $SamAccountName on $($Config.VmName): $($removed.Outcome). Output: $($removed.Output)"
    }

    $afterDelete = Invoke-ImportRun -System $system -Kind Delta -Name "$Label Delta Import (user deleted)"
    Add-TestResult -Name "$Label Delta Import reports exactly one deletion (Recycle Bin $recycleBinWord)" `
        -Passed ($afterDelete.Deleted -eq 1 -and $afterDelete.Added -eq 0 -and $afterDelete.Updated -eq 0) `
        -Detail "Expected one deletion and nothing else. Reported: $(Format-ImportChange -Change $afterDelete)"

    # JIM's deletion model: an import that finds an object gone marks its Connected System Object Obsolete, and it
    # stays that way until a synchronisation run on the Connected System deletes it. Nothing here runs one.
    $obsolete = Get-ObjectByDisplayName -ConnectedSystemId $system.ConnectedSystemId -DisplayName $displayName -Status Obsolete
    Add-TestResult -Name "$Label marks the Connected System Object for '$displayName' Obsolete" `
        -Passed ($null -ne $obsolete) `
        -Detail "No Obsolete Connected System Object named '$displayName' was found after the deletion was imported."

    Write-Host "  [5/6] Delta Import again (the deletion must not be reported twice)..." -ForegroundColor DarkGray
    $again = Invoke-ImportRun -System $system -Kind Delta -Name "$Label Delta Import (after the deletion)"
    Add-TestResult -Name "$Label Delta Import reports nothing after the deletion: the deletion is reported once" `
        -Passed ($again.Total -eq 0 -and $again.Added -eq 0 -and $again.Updated -eq 0 -and $again.Deleted -eq 0) `
        -Detail "Reported: $(Format-ImportChange -Change $again). A second deletion here is the Recycle Bin double report the PRD names, or a deletion that was not remembered."

    $stillObsolete = Get-ObjectByDisplayName -ConnectedSystemId $system.ConnectedSystemId -DisplayName $displayName -Status Obsolete
    Add-TestResult -Name "$Label still holds the one Obsolete Connected System Object for '$displayName'" `
        -Passed ($null -ne $stillObsolete) `
        -Detail "The Obsolete Connected System Object for '$displayName' was gone after a Delta Import that reported no changes."

    Write-Host "  [6/6] $Label complete" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------------------------------------------
# RestoreFromBackup: Primary
# ---------------------------------------------------------------------------------------------------------------
function Invoke-RestoreFromBackupTest {
    param([Parameter(Mandatory=$true)][hashtable]$Config)

    Write-TestSection "RestoreFromBackup: $($Config.ConnectedSystemName) ($($Config.VmName)) reverted to its baseline checkpoint mid-scenario"

    $system = Initialize-LabConnectedSystem -Config $Config
    $label = $Config.ConnectedSystemName

    # One user that exists before the revert and not after it (the baseline checkpoint predates it), so that the
    # Full Import that follows the revert has something to reconcile: this is what a restore from backup does to
    # data made since the backup.
    $givenName = "Corin"
    $surname = "Ashbourne"
    $displayName = Add-ScenarioUser -Config $Config -SamAccountName "s25restore" -GivenName $givenName -Surname $surname

    Write-Host "  [1/7] Full Import (records the watermark and the domain controller's invocationId)..." -ForegroundColor DarkGray
    $baseline = Invoke-ImportRun -System $system -Kind Full -Name "$label Full Import (before the revert)"
    Add-TestResult -Name "$label Full Import before the revert imports the user created for it" `
        -Passed ($baseline.Added -eq 1) `
        -Detail "Expected one add. Reported: $(Format-ImportChange -Change $baseline)"

    Write-Host "  [2/7] Delta Import (clean, on the same domain controller)..." -ForegroundColor DarkGray
    $clean = Invoke-ImportRun -System $system -Kind Delta -Name "$label Delta Import (before the revert)"
    Add-TestResult -Name "$label Delta Import before the revert is clean" `
        -Passed ($clean.Total -eq 0 -and $clean.Added -eq 0 -and $clean.Updated -eq 0 -and $clean.Deleted -eq 0) `
        -Detail "Reported: $(Format-ImportChange -Change $clean)"

    $previousInvocationId = Get-DomainControllerInvocationId -Config $Config
    $recordedBefore = Get-RecordedInvocationId -ConnectedSystemId $system.ConnectedSystemId
    Add-TestResult -Name "$label records the domain controller's invocationId at the Full Import" `
        -Passed ($null -ne $recordedBefore -and $recordedBefore -eq "$previousInvocationId") `
        -Detail "The directory says $previousInvocationId; JIM's 'Invocation Id' capability says '$recordedBefore'. Nothing recorded means a Delta Import cannot tell a restored domain controller from the one it read."
    Write-Host "  invocationId before the revert: $previousInvocationId" -ForegroundColor Gray

    Write-Host "  [3/7] Reverting $($Config.VmName) to '$($Config.CheckpointBaseline)' and waiting for it..." -ForegroundColor DarkGray
    Invoke-ActiveDirectoryLabRestore -Plan @(@{ Instance = "Primary"; VmName = $Config.VmName; Checkpoint = $Config.CheckpointBaseline; NeedsPopulation = $false })
    & "$PSScriptRoot/../Wait-ActiveDirectoryReady.ps1" -DirectoryConfig $Config -TimeoutSeconds 600
    if ($LASTEXITCODE -ne 0) {
        throw "$($Config.VmName) did not become ready after it was reverted to '$($Config.CheckpointBaseline)'."
    }

    $currentInvocationId = Get-DomainControllerInvocationId -Config $Config
    Write-Host "  invocationId after the revert:  $currentInvocationId" -ForegroundColor Gray

    # Not subject to -ContinueOnError. If the revert kept the invocationId (a standard checkpoint, or a revert
    # that did not happen) there is no restore for the rest of the scenario to assert anything about.
    if ($currentInvocationId -eq $previousInvocationId) {
        throw "RESTORE NOT PROVEN: $($Config.VmName) kept the invocationId $previousInvocationId across its revert to " +
              "'$($Config.CheckpointBaseline)'. A Hyper-V production checkpoint revert issues a new one (Active Directory's " +
              "safe restore); a standard checkpoint, or a revert that did not take effect, does not. Nothing below can show " +
              "how a Delta Import treats a restored domain controller."
    }
    Add-TestResult -Name "$label reverting the checkpoint gave the domain controller a new invocationId, as a restore from backup does" `
        -Passed $true

    Write-Host "  [4/7] Delta Import after the revert (must fail fast)..." -ForegroundColor DarkGray

    # Started without -Wait, and polled here: the worker logs an Error line for a failed run, the runner's error
    # watcher would abort a -Wait on it, and the result being asserted would never be read. See this script's help.
    $failedRun = Start-JIMRunProfile -ConnectedSystemId $system.ConnectedSystemId -RunProfileId $system.DeltaImportProfileId -PassThru
    $script:ExpectedFailureActivityId = "$($failedRun.activityId)"
    $failedActivity = Wait-ActivityTerminal -ActivityId "$($failedRun.activityId)"

    # Give the watcher time to write the Error line the failure logged, then take it: a sentinel left non-empty
    # would abort the next Run Profile wait. What it held must be the expected refusal and nothing else.
    Start-Sleep -Seconds 5
    $errorLines = @(Clear-JimErrorWatcher)
    $unexpectedLines = @($errorLines | Where-Object { $_ -notmatch [regex]::Escape($script:ExpectedFailureActivityId) -and $_ -notmatch 'invocationId' })

    $failedStatus = Get-PropertyText -Object $failedActivity -Name "status"
    $errorText = Get-PropertyText -Object $failedActivity -Name "errorMessage"
    $warningText = Get-PropertyText -Object $failedActivity -Name "warningMessage"

    Add-TestResult -Name "$label Delta Import after the revert FAILS FAST instead of importing" `
        -Passed ($failedStatus -in @("FailedWithError", "CompleteWithError")) `
        -Detail "Expected the run to fail. Its status was '$failedStatus' (warning: '$warningText'). A Delta Import that completes after a restore reads a USN watermark from a domain controller that no longer has that history."

    Add-TestResult -Name "$label failure names the change of invocationId" `
        -Passed ($errorText -match 'invocationId has changed') `
        -Detail "Expected the run's error to say the domain controller's invocationId has changed. Its error was: '$errorText'"

    Add-TestResult -Name "$label failure names the previous and the current invocationId" `
        -Passed ($errorText -match [regex]::Escape("$previousInvocationId") -and $errorText -match [regex]::Escape("$currentInvocationId")) `
        -Detail "Expected both $previousInvocationId (before the revert) and $currentInvocationId (after it) in the run's error. Its error was: '$errorText'"

    Add-TestResult -Name "$label failure names the remedy, a Full Import" `
        -Passed ($errorText -match 'Full Import') `
        -Detail "Expected the run's error to tell the administrator to run a Full Import. Its error was: '$errorText'"

    $importedNothing = $false
    $importedDetail = ""
    try {
        $failedChange = Get-ImportChange -ActivityId "$($failedRun.activityId)"
        $importedNothing = ($failedChange.Total -eq 0)
        $importedDetail = "Reported: $(Format-ImportChange -Change $failedChange)"
    }
    catch {
        $importedDetail = "The failed run's statistics could not be read: $($_.Exception.Message)"
    }
    Add-TestResult -Name "$label failed Delta Import changed nothing in JIM" `
        -Passed $importedNothing -Detail $importedDetail

    Add-TestResult -Name "$label failed Delta Import logged nothing but the expected refusal as an Error" `
        -Passed ($unexpectedLines.Count -eq 0) `
        -Detail "$($unexpectedLines.Count) other Error/Fatal line(s) reached JIM's logs during the run: $(($unexpectedLines | Select-Object -First 3) -join ' | ')"

    Write-Host "  [5/7] Full Import (re-establishes the baseline)..." -ForegroundColor DarkGray
    $reconcile = Invoke-ImportRun -System $system -Kind Full -Name "$label Full Import (after the revert)"
    Add-TestResult -Name "$label Full Import after the revert succeeds and reconciles what the restore lost" `
        -Passed ($reconcile.Deleted -eq 1 -and $reconcile.Added -eq 0) `
        -Detail "The user '$displayName' was created after the checkpoint, so the revert removed it: expected one deletion detected and no adds. Reported: $(Format-ImportChange -Change $reconcile)"

    $recordedAfter = Get-RecordedInvocationId -ConnectedSystemId $system.ConnectedSystemId
    Add-TestResult -Name "$label Full Import records the new invocationId as the baseline" `
        -Passed ($null -ne $recordedAfter -and $recordedAfter -eq "$currentInvocationId") `
        -Detail "The directory says $currentInvocationId; JIM's 'Invocation Id' capability says '$recordedAfter'."

    Write-Host "  [6/7] Delta Import (clean again)..." -ForegroundColor DarkGray
    $cleanAgain = Invoke-ImportRun -System $system -Kind Delta -Name "$label Delta Import (after the Full Import)"
    Add-TestResult -Name "$label Delta Import after the Full Import is clean" `
        -Passed ($cleanAgain.Total -eq 0 -and $cleanAgain.Added -eq 0 -and $cleanAgain.Updated -eq 0 -and $cleanAgain.Deleted -eq 0) `
        -Detail "Reported: $(Format-ImportChange -Change $cleanAgain)"

    Write-Host "  [7/7] $label complete" -ForegroundColor DarkGray
}

Write-TestSection "Scenario 025: Active Directory Delta Import Integrity"
Write-Host "Primary:  $($primaryConfig.ConnectedSystemName) ($($primaryConfig.VmName)), Recycle Bin on" -ForegroundColor Gray
if ($sourceConfig) {
    Write-Host "Source:   $($sourceConfig.ConnectedSystemName) ($($sourceConfig.VmName)), Recycle Bin off" -ForegroundColor Gray
}
Write-Host "Step:     $Step (the steps are independent)" -ForegroundColor Gray
Write-Host ""

Connect-JimSession

try {
    if ($Step -in @("RecycleBin", "All")) {
        Invoke-RecycleBinTest -Label "Primary" -Config $primaryConfig -ExpectRecycleBin $true `
            -SamAccountName "s25wren" -GivenName "Wren" -Surname "Halloway"
        Invoke-RecycleBinTest -Label "Source" -Config $sourceConfig -ExpectRecycleBin $false `
            -SamAccountName "s25sable" -GivenName "Sable" -Surname "Ashdown"
    }

    if ($Step -in @("RestoreFromBackup", "All")) {
        Invoke-RestoreFromBackupTest -Config $primaryConfig
    }

    # The worker logs everything it does; an Error there means a failure that hid behind a green Activity. The one
    # run this scenario expects to fail logs one, and only that one is allowed.
    $allowedErrors = if ($script:ExpectedFailureActivityId) { [regex]::Escape($script:ExpectedFailureActivityId) + '|invocationId' } else { '' }
    Assert-NoWorkerErrors -Since $startTime -AllowPattern $allowedErrors
}
finally {
    Disconnect-JimSession
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
$duration = (Get-Date) - $startTime
$passed = @($script:TestResults | Where-Object { $_.Passed }).Count
$failed = @($script:TestResults | Where-Object { -not $_.Passed }).Count

Write-TestSection "Scenario 025 Summary"
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
Write-Host "OK A Delta Import reports a deletion once with the Recycle Bin on and off, and refuses to read a" -ForegroundColor Green
Write-Host "   watermark from before a restore, telling the administrator why and what to do." -ForegroundColor Green
exit 0
