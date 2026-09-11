# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 21: Run Profile Safeguards

.DESCRIPTION
    Validates all five per-Run-Profile safeguards (#1618): the export limits (Max creates, Max updates,
    Max deletes) and the Full Import deletion-detection limits (Max detected deletions as a count and as
    a share of the Connector Space). Reuses the Scenario 1 fixture (CSV HR source, LDAP target). A run
    that would exceed a limit does none of that kind of change; it never processes a head of the queue,
    which on a frequent Schedule would only delay a wrong mass change.

    Test 1: Export limits, one change type at a time
        Creates
        - Full Import and Full Synchronisation of the CSV baseline stage a create to LDAP per user
        - Max creates 1 with more pending: no create is attempted; the Activity is CompleteWithWarning,
          its warning names the limit and the pending count, its counter carries every create as
          withheld, nothing succeeded, everything is still pending
        - Max creates equal to the pending count: everything is created, the Activity is Complete
        - Clear the limit with -MaxCreates $null (an explicit null clears; an omitted parameter leaves
          the value alone) and confirm the exports with an LDAP Full Import
        Updates
        - Change every user's title in the CSV, Full Import and Full Synchronisation: one update per user
        - Max updates 1: none attempted, same assertions; Max updates equal to the count: all attempted
        Deletes
        - Give the User type an authoritative-source Deletion Rule with no grace period, remove every
          employee but one from the CSV, Full Import and Full Synchronisation: the departed Metaverse
          Objects are deleted at once and a delete export is staged to LDAP for each
        - Max deletes 1: none attempted, same assertions; Max deletes equal to the count: all attempted
        - Restores the CSV, the User type's Deletion Rule and the LDAP population afterwards

    Test 2: Full Import deletion-detection limits, share and count
        - Full Import and Full Synchronisation of the CSV baseline
        - Max detected deletions percent 10; remove one employee (33% on the Nano template) and run the
          Full Import: refused, nothing marked, the warning names the count, the share and the limit, and
          LastSuccessfulFullImportCompletedAt does not move (the #1605 gate stays shut)
        - Max detected deletions 0 with the share limit cleared, run again: refused on the count
        - Max detected deletions 1, run again: at the limit the detection is applied, the departed
          employee is marked as deleted, and the timestamp moves
        - Restores the CSV, clears both limits and re-imports, so later scenarios are unaffected

.PARAMETER Step
    Which test step to execute

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 30)

.EXAMPLE
    ./Invoke-Scenario21-RunProfileSafeguards.ps1 -Step All -Template Nano -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario21-RunProfileSafeguards.ps1 -Step ExportLimit -Template Nano -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("ExportLimit", "ImportLimit", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 30,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Hard-fail: the long-tail templates are Scenario 8 only.
$longTailTemplates = @("Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
if ($Template -in $longTailTemplates) {
    throw "Template '$Template' is only valid for Scenario 8 (Cross-Domain Entitlement Sync). Use 'Scale100k50Groups' or smaller for this scenario."
}

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"

Write-TestSection "Scenario 21: Run Profile Safeguards"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Run Profile Safeguards"
    Template = $Template
    StartTime = (Get-Date).ToString("o")
    Steps = @()
    Success = $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Full Import and Full Synchronisation of the CSV source
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ImportAndSync {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    Write-Host "  Running import cycle ($Name)..." -ForegroundColor Gray

    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import ($Name)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Full Sync ($Name)"

    return @{
        ImportActivityId = $importResult.activityId
        SyncActivityId = $syncResult.activityId
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: The number an export Activity's message reports as succeeded ("Export complete: 1 succeeded, ...")
# -----------------------------------------------------------------------------------------------------------------
function Get-ExportSucceededCount {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId
    )

    $activity = Get-JIMActivity -Id $ActivityId
    if ($activity.message -match '([\d,]+) succeeded') {
        return [int]($Matches[1] -replace ',', '')
    }
    throw "Export Activity $ActivityId message does not report a succeeded count: $($activity.message)"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: How many Pending Exports the LDAP system carries. Counts every status, including exports awaiting
# confirmation, so it is only zero once a confirming import has run (see Invoke-LdapConfirmingImport).
# -----------------------------------------------------------------------------------------------------------------
function Get-LdapPendingExportCount {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    return [int](Get-JIMConnectedSystem -Id $Config.LDAPSystemId).pendingExportCount
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Confirm the LDAP exports so their Pending Exports are reconciled away, leaving the pending count exact
# for the next test step. A Full Import rather than a Delta Import, so the confirmation never depends on the
# directory's change tracking having caught up.
# -----------------------------------------------------------------------------------------------------------------
function Invoke-LdapConfirmingImport {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    Start-Sleep -Seconds 5
    $confirmResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $confirmResult.activityId -Name "LDAP Full Import ($Name, confirming)"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Set the LDAP Export Run Profile's safeguards and read them back
# -----------------------------------------------------------------------------------------------------------------
function Get-LdapExportProfile {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    $profile = Get-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId | Where-Object { $_.id -eq $Config.LDAPExportProfileId }
    Assert-NotNull -Value $profile -Message "The LDAP Export Run Profile is readable"
    return $profile
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Run the LDAP export expecting a change type to be withheld in full, and assert everything about it
# -----------------------------------------------------------------------------------------------------------------
function Assert-ExportWithheld {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [ValidateSet("creates", "updates", "deletes")]
        [string]$ChangeType,

        [Parameter(Mandatory=$true)]
        [int]$Limit,

        [Parameter(Mandatory=$true)]
        [int]$Pending,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($Name)" -AllowWarnings
    $activity = Get-JIMActivity -Id $exportResult.activityId
    Assert-Equal -Expected "CompleteWithWarning" -Actual ([string]$activity.status) -Message "The withheld export ($ChangeType) completed with a warning"

    $expectedCounters = @{ creates = 0; updates = 0; deletes = 0 }
    $expectedCounters[$ChangeType] = $Pending
    Assert-Equal -Expected $expectedCounters.creates -Actual ([int]$activity.exportCreatesWithheld) -Message "Creates withheld is $($expectedCounters.creates) ($Name)"
    Assert-Equal -Expected $expectedCounters.updates -Actual ([int]$activity.exportUpdatesWithheld) -Message "Updates withheld is $($expectedCounters.updates) ($Name)"
    Assert-Equal -Expected $expectedCounters.deletes -Actual ([int]$activity.exportDeletesWithheld) -Message "Deletes withheld is $($expectedCounters.deletes) ($Name)"

    Assert-Condition -Condition ($activity.warningMessage -like "*Max $ChangeType is $Limit, but $Pending $ChangeType were pending*") -Message "The warning names the limit and the pending count (got: $($activity.warningMessage))"
    Assert-Condition -Condition ($activity.warningMessage -like "*none were attempted and all $Pending remain pending*") -Message "The warning states nothing was attempted (got: $($activity.warningMessage))"
    Assert-Equal -Expected 0 -Actual (Get-ExportSucceededCount -ActivityId $exportResult.activityId) -Message "No export succeeded ($Name)"
    Assert-Equal -Expected $Pending -Actual (Get-LdapPendingExportCount -Config $Config) -Message "Every $ChangeType export is still pending after the withheld run ($Name)"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Run the LDAP export expecting everything to be attempted, and assert the counters are clean
# -----------------------------------------------------------------------------------------------------------------
function Assert-ExportAppliedInFull {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [int]$Expected,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($Name)"
    $activity = Get-JIMActivity -Id $exportResult.activityId
    Assert-Equal -Expected 0 -Actual ([int]$activity.exportCreatesWithheld + [int]$activity.exportUpdatesWithheld + [int]$activity.exportDeletesWithheld) -Message "Nothing was withheld ($Name)"
    Assert-Equal -Expected $Expected -Actual (Get-ExportSucceededCount -ActivityId $exportResult.activityId) -Message "Every pending export was attempted and succeeded ($Name)"
}

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Seed CSV test data before Setup-Scenario1.ps1 runs: its schema-import step opens hr-users.csv
    # to discover columns, so the file must exist before setup. Each scenario seeds its own data so
    # scenario ordering is irrelevant.
    Write-Host "Seeding CSV test data..." -ForegroundColor Gray
    & "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  ✓ CSV test data seeded" -ForegroundColor Green

    # Setup scenario configuration (reuse the Scenario 1 fixture: CSV source, LDAP target)
    $setupParams = @{ JIMUrl = $JIMUrl; ApiKey = $ApiKey; Template = $Template }
    if ($DirectoryConfig) { $setupParams.DirectoryConfig = $DirectoryConfig }
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" @setupParams

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    # Import PowerShell module and connect
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray
    Write-Host "  ✓ JIM configured for Scenario 21" -ForegroundColor Green

    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

    # =============================================================================================================
    # Test 1: Export limits, one change type at a time
    # =============================================================================================================
    if ($Step -eq "ExportLimit" -or $Step -eq "All") {
        Write-TestSection "Test 1: Export Limits (Max creates, Max updates, Max deletes)"

        # The baseline CSV is captured before anything edits it, so the restore at the end is exact.
        $baselineCsvRows = @(Import-Csv $csvPath)
        Assert-Condition -Condition ($baselineCsvRows.Count -ge 3) -Message "Baseline CSV has at least three rows, so removing all but one leaves two deletes to withhold (got $($baselineCsvRows.Count))"
        $userCount = $baselineCsvRows.Count

        # ---- Creates ----------------------------------------------------------------------------------------
        Write-TestStep "1a" "Import and synchronise the CSV baseline (stages the LDAP creates)"
        $baselineCycle = Invoke-ImportAndSync -Config $config -Name "Test 1 baseline"
        $baselineStats = Get-JIMActivityStats -ActivityId $baselineCycle.ImportActivityId
        Assert-Condition -Condition ($baselineStats.totalCsoAdds -gt 0) -Message "Baseline import created CSOs (got $($baselineStats.totalCsoAdds) adds)"
        $pendingCreates = Get-LdapPendingExportCount -Config $config
        Assert-Equal -Expected $userCount -Actual $pendingCreates -Message "One create is pending to LDAP per user"
        Write-Host "  Pending creates to LDAP: $pendingCreates" -ForegroundColor Gray

        Write-TestStep "1b" "Set Max creates 1 on the LDAP Export Run Profile"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxCreates 1 | Out-Null
        $exportProfile = Get-LdapExportProfile -Config $config
        Assert-Equal -Expected 1 -Actual ([int]$exportProfile.safeguards.maxCreates) -Message "Get-JIMRunProfile reports Max creates 1"
        Assert-Condition -Condition ($null -eq $exportProfile.safeguards.maxUpdates -and $null -eq $exportProfile.safeguards.maxDeletes) -Message "Max updates and Max deletes remain unset"

        Write-TestStep "1c" "Run the export (the limit would be exceeded, so no create is attempted)"
        Assert-ExportWithheld -Config $config -ChangeType creates -Limit 1 -Pending $pendingCreates -Name "Test 1 creates withheld"

        Write-TestStep "1d" "Set Max creates equal to the pending count and run the export (at the limit runs in full)"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxCreates $pendingCreates | Out-Null
        Assert-ExportAppliedInFull -Config $config -Expected $pendingCreates -Name "Test 1 creates at the limit"

        Write-TestStep "1e" "Clear Max creates with an explicit null and confirm the creates"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxCreates $null | Out-Null
        $clearedProfile = Get-LdapExportProfile -Config $config
        Assert-Condition -Condition ($null -eq $clearedProfile.safeguards.maxCreates) -Message "Max creates is cleared"
        Invoke-LdapConfirmingImport -Config $config -Name "Test 1 creates"
        Assert-Equal -Expected 0 -Actual (Get-LdapPendingExportCount -Config $config) -Message "No export is pending to LDAP once the creates are confirmed"

        # ---- Updates ----------------------------------------------------------------------------------------
        Write-TestStep "1f" "Change every user's title in the CSV, import and synchronise (stages the LDAP updates)"
        $updatedCsv = @($baselineCsvRows | ForEach-Object {
            $row = $_.PSObject.Copy()
            $row.title = "$($_.title) (Safeguards)"
            $row
        })
        $updatedCsv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Invoke-ImportAndSync -Config $config -Name "Test 1 updates" | Out-Null
        $pendingUpdates = Get-LdapPendingExportCount -Config $config
        Assert-Equal -Expected $userCount -Actual $pendingUpdates -Message "One update is pending to LDAP per user"

        Write-TestStep "1g" "Set Max updates 1 and run the export (no update is attempted)"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxUpdates 1 | Out-Null
        $updatesProfile = Get-LdapExportProfile -Config $config
        Assert-Equal -Expected 1 -Actual ([int]$updatesProfile.safeguards.maxUpdates) -Message "Get-JIMRunProfile reports Max updates 1"
        Assert-Condition -Condition ($null -eq $updatesProfile.safeguards.maxCreates) -Message "Max creates stayed cleared while Max updates was set"
        Assert-ExportWithheld -Config $config -ChangeType updates -Limit 1 -Pending $pendingUpdates -Name "Test 1 updates withheld"

        Write-TestStep "1h" "Set Max updates equal to the pending count, run the export, clear it and confirm"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxUpdates $pendingUpdates | Out-Null
        Assert-ExportAppliedInFull -Config $config -Expected $pendingUpdates -Name "Test 1 updates at the limit"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxUpdates $null | Out-Null
        Assert-Condition -Condition ($null -eq (Get-LdapExportProfile -Config $config).safeguards.maxUpdates) -Message "Max updates is cleared"
        Invoke-LdapConfirmingImport -Config $config -Name "Test 1 updates"
        Assert-Equal -Expected 0 -Actual (Get-LdapPendingExportCount -Config $config) -Message "No export is pending to LDAP once the updates are confirmed"

        # ---- Deletes ----------------------------------------------------------------------------------------
        # Deletes reach the export through a Deletion Rule: the User type is given an authoritative-source
        # rule on the CSV system with no grace period, so removing an employee from the CSV deletes the
        # Metaverse Object at the next synchronisation and stages a delete export to LDAP. The type's
        # current settings are captured first and restored at the end, whatever the fixture configured.
        Write-TestStep "1i" "Remove every employee but one from the CSV and synchronise with a no-grace Deletion Rule (stages the LDAP deletes)"
        $userType = Get-JIMMetaverseObjectType -Name "User"
        Assert-NotNull -Value $userType -Message "The built-in User Metaverse Object Type exists"
        $userTypeBefore = Get-JIMMetaverseObjectType -Id $userType.id
        Assert-NotNull -Value $userTypeBefore.deletionRule -Message "The User type's current Deletion Rule is readable for later restoration"
        Set-JIMMetaverseObjectType -Id $userType.id `
            -DeletionRule WhenAuthoritativeSourceDisconnected `
            -DeletionTriggerConnectedSystemIds $config.CSVSystemId `
            -DeletionTriggerMode AllSourcesDisconnect `
            -DeletionGracePeriod ([TimeSpan]::Zero) | Out-Null

        $survivor = $updatedCsv[0]
        @($survivor) | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        $expectedDeletes = $userCount - 1
        Write-Host "  Kept employeeId $($survivor.employeeId); removed $expectedDeletes" -ForegroundColor Gray
        Invoke-ImportAndSync -Config $config -Name "Test 1 deletes" | Out-Null
        $pendingDeletes = Get-LdapPendingExportCount -Config $config
        Assert-Equal -Expected $expectedDeletes -Actual $pendingDeletes -Message "One delete is pending to LDAP per departed employee"

        Write-TestStep "1j" "Set Max deletes 1 and run the export (no delete is attempted)"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxDeletes 1 | Out-Null
        $deletesProfile = Get-LdapExportProfile -Config $config
        Assert-Equal -Expected 1 -Actual ([int]$deletesProfile.safeguards.maxDeletes) -Message "Get-JIMRunProfile reports Max deletes 1"
        Assert-ExportWithheld -Config $config -ChangeType deletes -Limit 1 -Pending $pendingDeletes -Name "Test 1 deletes withheld"

        Write-TestStep "1k" "Set Max deletes equal to the pending count, run the export, clear it and confirm"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxDeletes $pendingDeletes | Out-Null
        Assert-ExportAppliedInFull -Config $config -Expected $pendingDeletes -Name "Test 1 deletes at the limit"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxDeletes $null | Out-Null
        $finalProfile = Get-LdapExportProfile -Config $config
        Assert-Condition -Condition ($null -eq $finalProfile.safeguards.maxCreates -and $null -eq $finalProfile.safeguards.maxUpdates -and $null -eq $finalProfile.safeguards.maxDeletes) -Message "All three export limits are cleared"
        Invoke-LdapConfirmingImport -Config $config -Name "Test 1 deletes"
        Assert-Equal -Expected 0 -Actual (Get-LdapPendingExportCount -Config $config) -Message "No export is pending to LDAP once the deletes are confirmed"

        # ---- Restore ----------------------------------------------------------------------------------------
        Write-TestStep "1l" "Restore the CSV, the User type's Deletion Rule and the LDAP population"
        $baselineCsvRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        $restoreParams = @{
            Id           = $userType.id
            DeletionRule = [string]$userTypeBefore.deletionRule
        }
        if ($null -ne $userTypeBefore.deletionGracePeriod) { $restoreParams.DeletionGracePeriod = [TimeSpan]$userTypeBefore.deletionGracePeriod }
        if ($userTypeBefore.deletionTriggerConnectedSystemIds) { $restoreParams.DeletionTriggerConnectedSystemIds = [int[]]$userTypeBefore.deletionTriggerConnectedSystemIds }
        if ($null -ne $userTypeBefore.deletionTriggerMode) { $restoreParams.DeletionTriggerMode = [string]$userTypeBefore.deletionTriggerMode }
        Set-JIMMetaverseObjectType @restoreParams | Out-Null
        Invoke-ImportAndSync -Config $config -Name "Test 1 restore" | Out-Null
        $pendingAfterRestore = Get-LdapPendingExportCount -Config $config
        if ($pendingAfterRestore -gt 0) {
            Assert-ExportAppliedInFull -Config $config -Expected $pendingAfterRestore -Name "Test 1 restore"
            Invoke-LdapConfirmingImport -Config $config -Name "Test 1 restore"
        }

        $testResults.Steps += @{ Name = "Export limits (creates, updates, deletes)"; Success = $true }
        Write-Host "  ✓ Test 1 passed" -ForegroundColor Green
    }

    # =============================================================================================================
    # Test 2: Full Import deletion-detection limits (share and count)
    # =============================================================================================================
    if ($Step -eq "ImportLimit" -or $Step -eq "All") {
        Write-TestSection "Test 2: Full Import Deletion-Detection Limits"

        # Step 2a: Baseline, and pick the departing employee deterministically (the first row)
        Write-TestStep "2a" "Import and synchronise the CSV baseline"
        $baselineCycle2 = Invoke-ImportAndSync -Config $config -Name "Test 2 baseline"
        $baselineStats2 = Get-JIMActivityStats -ActivityId $baselineCycle2.ImportActivityId
        Assert-Condition -Condition ($baselineStats2.totalCsoAdds -ge 0) -Message "Baseline import ran (got $($baselineStats2.totalCsoAdds) adds)"

        $baselineCsvRows2 = @(Import-Csv $csvPath)
        Assert-Condition -Condition ($baselineCsvRows2.Count -ge 2) -Message "Baseline CSV has at least two rows (got $($baselineCsvRows2.Count))"
        $departingEmployeeId = $baselineCsvRows2[0].employeeId
        $expectedSharePercent = [int][Math]::Floor(100.0 / $baselineCsvRows2.Count)
        Assert-Condition -Condition ($expectedSharePercent -gt 10) -Message "Removing one row exceeds a 10% limit on this template (one row is about $expectedSharePercent%)"
        Write-Host "  Departing employeeId: $departingEmployeeId (about $expectedSharePercent% of $($baselineCsvRows2.Count))" -ForegroundColor Gray

        $csvSystemBefore = Get-JIMConnectedSystem -Id $config.CSVSystemId
        $lastSuccessfulImportBefore = $csvSystemBefore.lastSuccessfulFullImportCompletedAt
        Assert-NotNull -Value $lastSuccessfulImportBefore -Message "The baseline Full Import stamped LastSuccessfulFullImportCompletedAt"

        # Step 2b: Put Max detected deletions percent 10 on the CSV Full Import Run Profile
        Write-TestStep "2b" "Set Max detected deletions percent 10 on the CSV Full Import Run Profile"
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletionsPercent 10 | Out-Null
        $importProfile = Get-JIMRunProfile -ConnectedSystemId $config.CSVSystemId | Where-Object { $_.id -eq $config.CSVImportProfileId }
        Assert-Equal -Expected 10 -Actual ([int]$importProfile.safeguards.maxDetectedDeletionsPercent) -Message "Get-JIMRunProfile reports Max detected deletions percent 10"
        Assert-Condition -Condition ($null -eq $importProfile.safeguards.maxDetectedDeletions) -Message "Max detected deletions remains unset"

        # Step 2c: Remove one employee and run the Full Import; the detection is refused on the share
        Write-TestStep "2c" "Remove one employee from the CSV and run the Full Import (refused on the share)"
        $partialCsv = @($baselineCsvRows2 | Where-Object { $_.employeeId -ne $departingEmployeeId })
        $partialCsv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        $refusedImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $refusedImportResult.activityId -Name "CSV Full Import (Test 2 refused on share)" -AllowWarnings
        $refusedImportActivity = Get-JIMActivity -Id $refusedImportResult.activityId
        Assert-Equal -Expected "CompleteWithWarning" -Actual ([string]$refusedImportActivity.status) -Message "The refused Full Import completed with a warning"
        Assert-Equal -Expected 1 -Actual ([int]$refusedImportActivity.detectedDeletionsWithheld) -Message "The Activity records one detected deletion withheld"
        $refusedImportStats = Get-JIMActivityStats -ActivityId $refusedImportResult.activityId
        Assert-Equal -Expected 0 -Actual ([int]$refusedImportStats.totalCsoDeletes) -Message "Nothing was marked as deleted"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*Deletion detection found 1 object*') -Message "The warning names the count (got: $($refusedImportActivity.warningMessage))"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*limit of 10%*') -Message "The warning names the share limit (got: $($refusedImportActivity.warningMessage))"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*none were marked as deleted*') -Message "The warning states nothing was marked (got: $($refusedImportActivity.warningMessage))"

        $csvSystemAfterRefusal = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Equal -Expected ([string]$lastSuccessfulImportBefore) -Actual ([string]$csvSystemAfterRefusal.lastSuccessfulFullImportCompletedAt) -Message "A refused Full Import does not move LastSuccessfulFullImportCompletedAt (the #1605 gate stays shut)"

        # Step 2d: Clear the share limit, set the count limit to zero and run again; refused on the count
        Write-TestStep "2d" "Clear the share limit, set Max detected deletions 0 and run the Full Import (refused on the count)"
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletionsPercent $null -MaxDetectedDeletions 0 | Out-Null
        $countProfile = Get-JIMRunProfile -ConnectedSystemId $config.CSVSystemId | Where-Object { $_.id -eq $config.CSVImportProfileId }
        Assert-Equal -Expected 0 -Actual ([int]$countProfile.safeguards.maxDetectedDeletions) -Message "Get-JIMRunProfile reports Max detected deletions 0"
        Assert-Condition -Condition ($null -eq $countProfile.safeguards.maxDetectedDeletionsPercent) -Message "Max detected deletions percent is cleared"

        $refusedOnCountResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $refusedOnCountResult.activityId -Name "CSV Full Import (Test 2 refused on count)" -AllowWarnings
        $refusedOnCountActivity = Get-JIMActivity -Id $refusedOnCountResult.activityId
        Assert-Equal -Expected "CompleteWithWarning" -Actual ([string]$refusedOnCountActivity.status) -Message "The Full Import refused on the count completed with a warning"
        Assert-Equal -Expected 1 -Actual ([int]$refusedOnCountActivity.detectedDeletionsWithheld) -Message "The Activity records one detected deletion withheld (count limit)"
        Assert-Condition -Condition ($refusedOnCountActivity.warningMessage -like '*limit of 0;*') -Message "The warning names the count limit (got: $($refusedOnCountActivity.warningMessage))"
        $refusedOnCountStats = Get-JIMActivityStats -ActivityId $refusedOnCountResult.activityId
        Assert-Equal -Expected 0 -Actual ([int]$refusedOnCountStats.totalCsoDeletes) -Message "Nothing was marked as deleted (count limit)"
        $csvSystemAfterCountRefusal = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Equal -Expected ([string]$lastSuccessfulImportBefore) -Actual ([string]$csvSystemAfterCountRefusal.lastSuccessfulFullImportCompletedAt) -Message "A Full Import refused on the count does not move LastSuccessfulFullImportCompletedAt either"

        # Step 2e: At the count limit the detection is applied
        Write-TestStep "2e" "Set Max detected deletions 1 and run the Full Import again (at the limit, detection applied)"
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletions 1 | Out-Null
        $appliedImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $appliedImportResult.activityId -Name "CSV Full Import (Test 2 applied)"
        $appliedImportActivity = Get-JIMActivity -Id $appliedImportResult.activityId
        Assert-Equal -Expected 0 -Actual ([int]$appliedImportActivity.detectedDeletionsWithheld) -Message "Nothing was withheld once the limit allows the departure"
        $appliedImportStats = Get-JIMActivityStats -ActivityId $appliedImportResult.activityId
        Assert-Equal -Expected 1 -Actual ([int]$appliedImportStats.totalCsoDeletes) -Message "The departed employee was marked as deleted"

        $csvSystemAfterApplied = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Condition -Condition (([string]$csvSystemAfterApplied.lastSuccessfulFullImportCompletedAt) -ne ([string]$lastSuccessfulImportBefore)) -Message "A Full Import that applied its detection moves LastSuccessfulFullImportCompletedAt"

        # Step 2f: Restore the CSV and the Run Profile, and re-import, so later scenarios see the full population
        Write-TestStep "2f" "Restore the CSV, clear both limits and re-import"
        $baselineCsvRows2 | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletions $null -MaxDetectedDeletionsPercent $null | Out-Null
        $restoredProfile = Get-JIMRunProfile -ConnectedSystemId $config.CSVSystemId | Where-Object { $_.id -eq $config.CSVImportProfileId }
        Assert-Condition -Condition ($null -eq $restoredProfile.safeguards.maxDetectedDeletions -and $null -eq $restoredProfile.safeguards.maxDetectedDeletionsPercent) -Message "Both deletion-detection limits are cleared"
        $restoreCycle = Invoke-ImportAndSync -Config $config -Name "Test 2 restore"
        Assert-NotNull -Value $restoreCycle.ImportActivityId -Message "The restore import ran"

        $testResults.Steps += @{ Name = "Full Import deletion-detection limits (share, count)"; Success = $true }
        Write-Host "  ✓ Test 2 passed" -ForegroundColor Green
    }

    # =============================================================================================================
    # Summary
    # =============================================================================================================
    Write-TestSection "Results"
    $testResults.Success = $true
    $testResults.EndTime = (Get-Date).ToString("o")

    # Wrapped in @(): a single passing step is one hashtable, whose .Count is its key count, not 1.
    $passedCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = @($testResults.Steps).Count
    Write-Host "Passed: $passedCount / $totalCount" -ForegroundColor Green

    return $testResults
}
catch {
    Write-Host "`n✗ Scenario 21 FAILED" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host "Stack trace: $($_.ScriptStackTrace)" -ForegroundColor DarkGray

    $testResults.Success = $false
    $testResults.Error = $_.ToString()
    $testResults.EndTime = (Get-Date).ToString("o")

    return $testResults
}
