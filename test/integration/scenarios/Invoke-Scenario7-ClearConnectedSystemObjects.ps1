# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 7: Clear Connected System Objects

.DESCRIPTION
    Validates the Clear Connected System Objects feature, which removes all CSOs from a
    connected system's connector space. Tests both the deleteChangeHistory=true (default)
    and deleteChangeHistory=false modes.

    Test 1: Clear with deleteChangeHistory=true (default)
        - Import CSV data to create CSOs with change history
        - Clear connector space (default: deleteChangeHistory=true)
        - Assert: CSOs are deleted (objectCount=0 on re-import shows all new adds)
        - Assert: Change history is deleted (changeRecordCount=0)

    Test 2: Clear with deleteChangeHistory=false (KeepChangeHistory)
        - Re-import CSV data to recreate CSOs with change history
        - Clear connector space with -KeepChangeHistory
        - Assert: CSOs are deleted (objectCount=0 on re-import shows all new adds)
        - Assert: Change history is preserved (changeRecordCount > 0)

    Test 3: Edge cases
        - Clear an already-empty connector space (should succeed without error)
        - Verify clearing one CS does not affect CSOs in another CS

    Test 4: Post-clear reconciliation after clear-then-partial-re-import (#1605 layers 1 and 2)
        - Import and synchronise a full baseline
        - Set the User Metaverse Object Type's Deletion Rule to WhenAuthoritativeSourceDisconnected,
          listing the CSV system as the sole authoritative source in AllSourcesDisconnect mode, keeping
          its existing 7-day grace period: this makes the rule fire on the CSV system's disconnection
          even though the departing employee's already-provisioned LDAP/Cross-Domain target joins remain
          (WhenLastConnectorDisconnected, this scenario's default, never would - see PRD Scenario 3)
        - Clear the connector space (queued, -Wait): arms the sweep
        - Run a Full Synchronisation BEFORE any re-import: assert the sweep stays armed and the
          Activity states it was skipped, because no Full Import has completed since the clear
        - Remove one employee from the CSV (the Nano template's 3 users make this a 33% shortfall,
          above the default 10% threshold), Full Import, then Full Synchronisation: the #1605
          Functional Requirement 9 shortfall check refuses the reconciliation
        - Assert: the Full Synchronisation Activity Message reports "Stranded-value sweep refused"
          and the arming is still set
        - Raise Sync.PostClearReconciliation.MaxMissingPercent to 50 and run another Full
          Synchronisation: the shortfall no longer refuses, so the sweep executes
        - Assert: the Full Synchronisation Activity Message reports "Stranded-value sweep executed",
          the arming is cleared and LastSuccessfulFullImportCompletedAt is stamped
        - Assert: the departed employee's Metaverse Object still exists, is pending deletion (marked
          by the Deletion Rule evaluation, the CSV system named as trigger, its 7-day grace period
          honoured) and its values are preserved as before
        - Assert: a surviving employee's Metaverse Object and values are unaffected
        - Assert: a further Full Synchronisation does not report another sweep (arming cleared)
        - Restores the threshold setting and the User type's Deletion Rule (captured before the
          change) afterwards, so later scenarios are unaffected

.PARAMETER Step
    Which test step to execute

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 30)

.EXAMPLE
    ./Invoke-Scenario7-ClearConnectedSystemObjects.ps1 -Step All -Template Nano -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario7-ClearConnectedSystemObjects.ps1 -Step DeleteHistory -Template Nano -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("DeleteHistory", "KeepHistory", "EdgeCases", "StrandedSweep", "All")]
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

Write-TestSection "Scenario 7: Clear Connected System Objects"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Clear Connected System Objects"
    Template = $Template
    StartTime = (Get-Date).ToString("o")
    Steps = @()
    Success = $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Import CSV data into the connected system and run a full sync cycle
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ImportAndSync {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    Write-Host "  Running import cycle ($Name)..." -ForegroundColor Gray

    # Full Import
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import ($Name)"

    # Full Sync (creates change history entries)
    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Full Sync ($Name)"

    return @{
        ImportActivityId = $importResult.activityId
        SyncActivityId = $syncResult.activityId
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Assert that a Clear-JIMConnectedSystem tracking object carries a real Activity id
# -----------------------------------------------------------------------------------------------------------------
function Assert-ValidActivityId {
    param(
        [Parameter(Mandatory=$true)]
        $ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Message
    )

    $parsedActivityId = [guid]::Empty
    $isValidGuid = [guid]::TryParse([string]$ActivityId, [ref]$parsedActivityId)
    Assert-Condition -Condition ($isValidGuid -and $parsedActivityId -ne [guid]::Empty) -Message $Message
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Look up a User Metaverse Object by its Employee ID and return the full detail object
# (AttributeValues, ConnectedSystemObjects), not the lightweight search/list header shape.
# -----------------------------------------------------------------------------------------------------------------
function Get-JIMMetaverseObjectByEmployeeId {
    param(
        [Parameter(Mandatory=$true)]
        [string]$EmployeeId
    )

    $header = Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Employee ID' -AttributeValue $EmployeeId
    if (-not $header) {
        return $null
    }
    return Get-JIMMetaverseObject -Id $header.id
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Count live ConnectedSystemObjects rows joined to a Metaverse Object, optionally restricted to
# one Connected System, via the Metaverse Object detail endpoint (GET /api/v1/metaverse/objects/{id}),
# whose ConnectedSystemObjects field is now populated by MetaverseRepository.GetMetaverseObjectWithProvenanceAsync
# (fixed under #1606; it previously always returned an empty array).
# -----------------------------------------------------------------------------------------------------------------
function Get-JoinedConnectedSystemObjectCount {
    param(
        [Parameter(Mandatory=$true)]
        [guid]$MvoId,

        [Parameter(Mandatory=$false)]
        [int]$ConnectedSystemId
    )

    $mvo = Get-JIMMetaverseObject -Id $MvoId
    $connectedSystemObjects = @($mvo.connectedSystemObjects)

    if ($PSBoundParameters.ContainsKey('ConnectedSystemId')) {
        $connectedSystemObjects = @($connectedSystemObjects | Where-Object { $_.connectedSystemId -eq $ConnectedSystemId })
    }

    return $connectedSystemObjects.Count
}

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Seed CSV test data before Setup-Scenario1.ps1 runs — its schema-import step
    # opens hr-users.csv to discover columns, so the file must exist before setup.
    # Each scenario seeds its own data so scenario ordering is irrelevant; prior
    # to this the suite was relying on files leaking across scenarios through the
    # shared jim-connector-files-volume.
    Write-Host "Seeding CSV test data..." -ForegroundColor Gray
    & "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  ✓ CSV test data seeded" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup for CSV connected system)
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
    Write-Host "  ✓ JIM configured for Scenario 7" -ForegroundColor Green

    # =============================================================================================================
    # Test 1: Clear with deleteChangeHistory=true (default)
    # =============================================================================================================
    if ($Step -eq "DeleteHistory" -or $Step -eq "All") {
        Write-TestSection "Test 1: Clear with deleteChangeHistory=true"

        # Step 1a: Import CSV data to populate CSOs and generate change history
        Write-TestStep "1a" "Import CSV data to create CSOs"
        $importCycle = Invoke-ImportAndSync -Config $config -Name "Test 1 initial import"

        # Verify CSOs were created (import activity should show adds)
        $importStats = Get-JIMActivityStats -ActivityId $importCycle.ImportActivityId
        Assert-Condition -Condition ($importStats.totalCsoAdds -gt 0) -Message "CSOs were created during import (got $($importStats.totalCsoAdds) adds)"
        $initialCsoCount = $importStats.totalCsoAdds
        Write-Host "  Created $initialCsoCount CSOs" -ForegroundColor Gray

        # Verify change history exists
        $historyBefore = Get-JIMHistoryCount -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($historyBefore.changeRecordCount -gt 0) -Message "Change history exists before clear (got $($historyBefore.changeRecordCount) records)"

        # Step 1b: Clear connector space with deleteChangeHistory=true (default)
        Write-TestStep "1b" "Clear connector space (deleteChangeHistory=true)"
        $clearResult1 = Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $clearResult1.ActivityId -Message "Clear (deleteChangeHistory=true) returned a valid Activity id"
        Write-Host "  ✓ Clear operation completed" -ForegroundColor Green

        # Step 1c: Verify CSOs are deleted by re-importing — all should be new adds
        Write-TestStep "1c" "Verify CSOs deleted and change history removed"
        $reimportCycle = Invoke-ImportAndSync -Config $config -Name "Test 1 re-import verification"
        $reimportStats = Get-JIMActivityStats -ActivityId $reimportCycle.ImportActivityId
        Assert-Equal -Expected $initialCsoCount -Actual $reimportStats.totalCsoAdds -Message "Re-import creates same number of CSOs as initial import (all were deleted)"

        # Verify change history was deleted — after re-import we should only have new records
        # The history count should be from the re-import only, not include the original import
        $historyAfterClear = Get-JIMHistoryCount -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($historyAfterClear.changeRecordCount -le $historyBefore.changeRecordCount) -Message "Change history count is not accumulated (before: $($historyBefore.changeRecordCount), after re-import: $($historyAfterClear.changeRecordCount))"

        Write-Host "  ✓ Test 1 PASSED: Clear with deleteChangeHistory=true works correctly" -ForegroundColor Green
        $testResults.Steps += @{ Name = "DeleteHistory"; Success = $true }
    }

    # =============================================================================================================
    # Test 2: Clear with deleteChangeHistory=false (KeepChangeHistory)
    # =============================================================================================================
    if ($Step -eq "KeepHistory" -or $Step -eq "All") {
        Write-TestSection "Test 2: Clear with deleteChangeHistory=false"

        # If we're running all tests, CSOs already exist from Test 1's re-import.
        # If running standalone, we need to import first.
        if ($Step -ne "All") {
            Write-TestStep "2a" "Import CSV data to create CSOs"
            Invoke-ImportAndSync -Config $config -Name "Test 2 initial import"
        }

        # Record change history count before clear
        $historyBefore = Get-JIMHistoryCount -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($historyBefore.changeRecordCount -gt 0) -Message "Change history exists before clear (got $($historyBefore.changeRecordCount) records)"
        $historyCountBefore = $historyBefore.changeRecordCount

        # Step 2b: Clear connector space with -KeepChangeHistory (deleteChangeHistory=false)
        Write-TestStep "2b" "Clear connector space with -KeepChangeHistory"
        $clearResult2 = Clear-JIMConnectedSystem -Id $config.CSVSystemId -KeepChangeHistory -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $clearResult2.ActivityId -Message "Clear (KeepChangeHistory) returned a valid Activity id"
        Write-Host "  ✓ Clear operation completed (change history preserved)" -ForegroundColor Green

        # Step 2c: Verify CSOs are deleted
        Write-TestStep "2c" "Verify CSOs deleted but change history preserved"
        $reimportCycle = Invoke-ImportAndSync -Config $config -Name "Test 2 re-import verification"
        $reimportStats = Get-JIMActivityStats -ActivityId $reimportCycle.ImportActivityId
        Assert-Condition -Condition ($reimportStats.totalCsoAdds -gt 0) -Message "Re-import creates new CSOs (confirming old ones were deleted, got $($reimportStats.totalCsoAdds) adds)"

        # Verify change history was preserved
        $historyAfterClear = Get-JIMHistoryCount -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($historyAfterClear.changeRecordCount -ge $historyCountBefore) -Message "Change history preserved after clear (before: $historyCountBefore, after: $($historyAfterClear.changeRecordCount))"

        Write-Host "  ✓ Test 2 PASSED: Clear with -KeepChangeHistory preserves audit trail" -ForegroundColor Green
        $testResults.Steps += @{ Name = "KeepHistory"; Success = $true }
    }

    # =============================================================================================================
    # Test 3: Edge cases
    # =============================================================================================================
    if ($Step -eq "EdgeCases" -or $Step -eq "All") {
        Write-TestSection "Test 3: Edge Cases"

        # Step 3a: Clear an already-empty connector space
        Write-TestStep "3a" "Clear an already-empty connector space"

        # First clear to ensure empty
        $preClearResult = Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $preClearResult.ActivityId -Message "Pre-clear returned a valid Activity id"
        Write-Host "  Pre-cleared connector space" -ForegroundColor Gray

        # Clear again — should succeed without error
        $emptyClearResult = Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $emptyClearResult.ActivityId -Message "Clearing an already-empty connector space returned a valid Activity id"
        Write-Host "  ✓ Clearing empty connector space succeeded without error" -ForegroundColor Green

        # Step 3b: Verify clearing one CS does not affect another
        Write-TestStep "3b" "Verify cross-system isolation"

        # Import data into CSV system
        $null = Invoke-ImportAndSync -Config $config -Name "Test 3 CSV import"

        # Get LDAP system's current state (it has CSOs from Scenario 1 setup if any were provisioned)
        $ldapHistoryBefore = Get-JIMHistoryCount -ConnectedSystemId $config.LDAPSystemId

        # Clear the CSV system
        $isolationClearResult = Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $isolationClearResult.ActivityId -Message "Cross-system isolation clear returned a valid Activity id"
        Write-Host "  Cleared CSV system" -ForegroundColor Gray

        # Verify LDAP system is unaffected
        $ldapHistoryAfter = Get-JIMHistoryCount -ConnectedSystemId $config.LDAPSystemId
        Assert-Equal -Expected $ldapHistoryBefore.changeRecordCount -Actual $ldapHistoryAfter.changeRecordCount -Message "LDAP system change history unaffected by CSV clear"

        Write-Host "  ✓ Test 3 PASSED: Edge cases handled correctly" -ForegroundColor Green
        $testResults.Steps += @{ Name = "EdgeCases"; Success = $true }
    }

    # =============================================================================================================
    # Test 4: Stranded-value sweep after clear-then-partial-re-import
    # =============================================================================================================
    if ($Step -eq "StrandedSweep" -or $Step -eq "All") {
        Write-TestSection "Test 4: Post-Clear Reconciliation After Clear-Then-Partial-Re-Import"

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $maxMissingPercentKey = "Sync.PostClearReconciliation.MaxMissingPercent"
        $userTypeGracePeriod = [TimeSpan]::FromDays(7)

        # Step 4a: Establish a known baseline (works whether the connector space is already
        # empty, e.g. after Test 3, or this test is run standalone via -Step StrandedSweep)
        Write-TestStep "4a" "Import and synchronise a full baseline"
        $baselineCycle = Invoke-ImportAndSync -Config $config -Name "Test 4 baseline"
        $baselineStats = Get-JIMActivityStats -ActivityId $baselineCycle.ImportActivityId
        Assert-Condition -Condition ($baselineStats.totalCsoAdds -gt 0) -Message "Baseline import created CSOs (got $($baselineStats.totalCsoAdds) adds)"

        # Capture the full set of employeeIds present in the CSV, and pick the departing
        # employee deterministically (the first row) so the removal is reproducible across runs.
        $baselineCsvRows = @(Import-Csv $csvPath)
        Assert-Condition -Condition ($baselineCsvRows.Count -ge 2) -Message "Baseline CSV has at least two rows, so removing one still leaves a survivor (got $($baselineCsvRows.Count))"
        $departingEmployeeId = $baselineCsvRows[0].employeeId
        $survivingEmployeeId = $baselineCsvRows[1].employeeId
        Write-Host "  Departing employeeId: $departingEmployeeId; surviving employeeId: $survivingEmployeeId" -ForegroundColor Gray

        # Snapshot both Metaverse Objects' attribute values before the clear, so the post-sweep
        # assertions compare against known values rather than assuming CSV content.
        $departingMvoBefore = Get-JIMMetaverseObjectByEmployeeId -EmployeeId $departingEmployeeId
        Assert-NotNull -Value $departingMvoBefore -Message "Departing employee's Metaverse Object exists before clear"
        $departingMvoId = $departingMvoBefore.id
        $departingDisplayNameBefore = ($departingMvoBefore.attributeValues | Where-Object { $_.attributeName -eq 'Display Name' }).stringValue
        Assert-Condition -Condition (-not [string]::IsNullOrWhiteSpace($departingDisplayNameBefore)) -Message "Departing employee has a Display Name value before clear"

        $survivingMvoBefore = Get-JIMMetaverseObjectByEmployeeId -EmployeeId $survivingEmployeeId
        Assert-NotNull -Value $survivingMvoBefore -Message "Surviving employee's Metaverse Object exists before clear"
        $survivingMvoId = $survivingMvoBefore.id
        $survivingDisplayNameBefore = ($survivingMvoBefore.attributeValues | Where-Object { $_.attributeName -eq 'Display Name' }).stringValue

        # Step 4b: Give the User type an authoritative-source rule that fires on the CSV system's
        # disconnection alone, even though the departing employee's already-provisioned LDAP and
        # Cross-Domain target joins remain (WhenLastConnectorDisconnected, this type's default per
        # Setup-Scenario1, never would - see PRD Scenario 3). Keep its existing 7-day grace period.
        Write-TestStep "4b" "Set the User type's Deletion Rule to fire on the CSV system alone"
        $userType = Get-JIMMetaverseObjectType -Name "User"
        Assert-NotNull -Value $userType -Message "The built-in User Metaverse Object Type exists"
        # Capture the type's current deletion settings so step 4n restores exactly what the scenario
        # setup configured, whatever that is (Setup-Scenario1 has changed its baseline before, #1614).
        $userTypeBefore = Get-JIMMetaverseObjectType -Id $userType.id
        Assert-NotNull -Value $userTypeBefore.deletionRule -Message "The User type's current Deletion Rule is readable for later restoration"
        Set-JIMMetaverseObjectType -Id $userType.id `
            -DeletionRule WhenAuthoritativeSourceDisconnected `
            -DeletionTriggerConnectedSystemIds $config.CSVSystemId `
            -DeletionTriggerMode AllSourcesDisconnect `
            -DeletionGracePeriod $userTypeGracePeriod | Out-Null

        # Step 4c: Clear the connector space (queued, -Wait) - arms the sweep
        Write-TestStep "4c" "Clear connector space (arms the post-clear reconciliation sweep)"
        $clearResult4 = Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force -Wait -Timeout 300
        Assert-ValidActivityId -ActivityId $clearResult4.ActivityId -Message "Clear returned a valid Activity id"

        # Step 4d: A Full Synchronisation run BEFORE any re-import must find the #1605 gate closed
        # (no Full Import of this system has completed successfully since the clear): the sweep
        # must not run, must stay armed, and the Activity must say why.
        Write-TestStep "4d" "Full Synchronisation before any re-import (gate closed, sweep skipped)"
        $gatedSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $gatedSyncResult.activityId -Name "CSV Full Sync (Test 4, before re-import, gate closed)"
        $gatedSyncActivity = Get-JIMActivity -Id $gatedSyncResult.activityId
        Assert-Condition -Condition ($gatedSyncActivity.message -like '*skipped: no Full Import*') -Message "Full Synchronisation Activity Message reports the sweep was skipped (got: $($gatedSyncActivity.message))"
        $csvSystemAfterGatedSync = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-NotNull -Value $csvSystemAfterGatedSync.strandedValueSweepArmedAt -Message "The sweep remains armed after a Full Synchronisation run before any re-import"

        # Step 4e: Remove the departing employee from the source CSV, so re-import returns everyone
        # else. The Nano template's 3 users make this a 33% shortfall, above the default 10% threshold.
        Write-TestStep "4e" "Remove one employee from the source CSV"
        $partialCsv = @($baselineCsvRows | Where-Object { $_.employeeId -ne $departingEmployeeId })
        Assert-Equal -Expected ($baselineCsvRows.Count - 1) -Actual $partialCsv.Count -Message "Partial CSV has one fewer row than baseline"
        $partialCsv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Write-Host "  Removed employeeId $departingEmployeeId from CSV" -ForegroundColor Gray

        # Step 4f: Full Import + Full Sync of the partial CSV - the #1605 Functional Requirement 9
        # gate is now open (a Full Import has completed successfully since the clear), so the
        # re-join shortfall check runs and, at 33% missing against the default 10% threshold, refuses.
        Write-TestStep "4f" "Full Import and Full Synchronisation of the partial CSV (shortfall refuses)"
        $partialImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $partialImportResult.activityId -Name "CSV Full Import (Test 4 partial re-import)"
        $partialImportStats = Get-JIMActivityStats -ActivityId $partialImportResult.activityId
        Assert-Equal -Expected ($baselineCsvRows.Count - 1) -Actual $partialImportStats.totalCsoAdds -Message "Partial re-import created a CSO for every surviving employee (got $($partialImportStats.totalCsoAdds) adds)"

        $refusedSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $refusedSyncResult.activityId -Name "CSV Full Sync (Test 4 partial re-import, sweep refused)"

        # Step 4g: The Full Synchronisation Activity reports the refusal, and the arming is untouched.
        Write-TestStep "4g" "Verify the Full Synchronisation Activity reports the refusal"
        $refusedSyncActivity = Get-JIMActivity -Id $refusedSyncResult.activityId
        Assert-Condition -Condition ($refusedSyncActivity.message -like '*Stranded-value sweep refused*') -Message "Full Synchronisation Activity Message reports the refusal (got: $($refusedSyncActivity.message))"
        $csvSystemAfterRefusal = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-NotNull -Value $csvSystemAfterRefusal.strandedValueSweepArmedAt -Message "The sweep remains armed after a refused reconciliation"

        # Step 4h: Raise the shortfall threshold so the same 33% missing no longer refuses.
        Write-TestStep "4h" "Raise the re-join shortfall threshold to 50%"
        Set-JIMServiceSetting -Key $maxMissingPercentKey -Value "50" | Out-Null

        # Step 4i: Run the Full Synchronisation again - the #1605 gate is still open and the shortfall
        # no longer refuses, so the sweep executes: value recall (#1549/#1570) and Deletion Rule
        # evaluation (#1605 Functional Requirement 7) both run.
        Write-TestStep "4i" "Full Synchronisation again (sweep executes)"
        $partialSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $partialSyncResult.activityId -Name "CSV Full Sync (Test 4 partial re-import, sweep run)"

        # Step 4j: The Full Synchronisation Activity reports the sweep, and the Connected System's
        # gate state confirms it: the arming is cleared, and the successful partial re-import's
        # Full Import is recorded as the reason the gate opened.
        Write-TestStep "4j" "Verify the Full Synchronisation Activity reports the sweep"
        $partialSyncActivity = Get-JIMActivity -Id $partialSyncResult.activityId
        Assert-Condition -Condition ($partialSyncActivity.message -like '*Stranded-value sweep executed*') -Message "Full Synchronisation Activity Message reports the sweep (got: $($partialSyncActivity.message))"
        $csvSystemAfterSweep = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Condition -Condition ($null -eq $csvSystemAfterSweep.strandedValueSweepArmedAt) -Message "The sweep's arming is cleared once the gate opens and the sweep runs"
        Assert-NotNull -Value $csvSystemAfterSweep.lastSuccessfulFullImportCompletedAt -Message "The successful partial re-import's Full Import stamped LastSuccessfulFullImportCompletedAt"

        # Step 4k: The departing employee's Metaverse Object - topology and Deletion Rule check.
        #
        # This scenario's CSV system ("HR CSV Source") is the ONLY Connected System that ever
        # IMPORTS into these Metaverse Objects. Setup-Scenario1 also creates export
        # Synchronisation Rules to LDAP ("Panoply AD") and "Cross-Domain Export", both
        # ProvisionToConnectedSystem, and ordinary Full Synchronisation provisions those target
        # Connected System Objects as soon as an object is in scope: this happens at Sync time,
        # independently of whether the LDAP/Cross-Domain Export Run Profiles ever actually run
        # (confirmed via psql: after Test 4's baseline sync, every user already carries a
        # provisioned CSO in both target systems). Scenario 7 never clears those systems, so
        # they survive every CSV-only clear this scenario performs.
        #
        # After the clear + partial re-import, the departing Metaverse Object therefore has NO
        # remaining CSV (import) join, but STILL holds its LDAP and Cross-Domain provisioned
        # target joins. Values are preserved as last known state (#1570) exactly as before, because
        # neither target system carries an enabled IMPORT Synchronisation Rule for the User type.
        # But because step 4b made the CSV system the type's sole authoritative source in
        # AllSourcesDisconnect mode, the Deletion Rule now fires on the CSV system's disconnection
        # regardless of the remaining target joins (#1605 Functional Requirement 7): the object is
        # marked pending deletion, with its 7-day grace period honoured and the CSV system recorded
        # as the trigger.
        Write-TestStep "4k" "Verify the departing employee's values were preserved and the object is pending deletion"
        $departingMvoAfter = Get-JIMMetaverseObject -Id $departingMvoId
        Assert-NotNull -Value $departingMvoAfter -Message "Departing employee's Metaverse Object still exists after the sweep (marked, not deleted, per the grace period)"
        $departingDisplayNameAfter = ($departingMvoAfter.attributeValues | Where-Object { $_.attributeName -eq 'Display Name' }).stringValue
        Assert-Equal -Expected $departingDisplayNameBefore -Actual $departingDisplayNameAfter -Message "Departing employee's Display Name value was preserved as last known state"
        $departingCsvJoinCount = Get-JoinedConnectedSystemObjectCount -MvoId $departingMvoId -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($departingCsvJoinCount -eq 0) -Message "Departing employee's Metaverse Object has no joined CSV Connected System Object (no import source remains; got $departingCsvJoinCount)"
        $departingTotalJoinCount = Get-JoinedConnectedSystemObjectCount -MvoId $departingMvoId
        Assert-Condition -Condition ($departingTotalJoinCount -gt 0) -Message "Departing employee's Metaverse Object still has joined provisioned target Connected System Object(s) (got $departingTotalJoinCount)"
        Assert-Condition -Condition ($departingMvoAfter.isPendingDeletion -eq $true) -Message "Departing employee's Metaverse Object is pending deletion (got isPendingDeletion=$($departingMvoAfter.isPendingDeletion))"
        Assert-NotNull -Value $departingMvoAfter.deletionEligibleDate -Message "Departing employee's Metaverse Object has a deletion-eligible date from the 7-day grace period"
        Assert-Equal -Expected "HR CSV Source" -Actual $departingMvoAfter.deletionTriggeredBySystemName -Message "The CSV system is recorded as the Deletion Rule trigger (got: $($departingMvoAfter.deletionTriggeredBySystemName))"

        # Step 4l: The surviving employee's Metaverse Object is untouched, and rejoined to the CSV system
        Write-TestStep "4l" "Verify the surviving employee's values are intact"
        $survivingMvoAfter = Get-JIMMetaverseObject -Id $survivingMvoId
        Assert-NotNull -Value $survivingMvoAfter -Message "Surviving employee's Metaverse Object still exists"
        $survivingCsvJoinCount = Get-JoinedConnectedSystemObjectCount -MvoId $survivingMvoId -ConnectedSystemId $config.CSVSystemId
        Assert-Condition -Condition ($survivingCsvJoinCount -eq 1) -Message "Surviving employee's Metaverse Object rejoined exactly one CSV Connected System Object (got $survivingCsvJoinCount; confirms the Object Matching Rule rejoined the re-imported CSO to the existing Metaverse Object rather than projecting a duplicate)"
        $survivingDisplayNameAfter = ($survivingMvoAfter.attributeValues | Where-Object { $_.attributeName -eq 'Display Name' }).stringValue
        Assert-Equal -Expected $survivingDisplayNameBefore -Actual $survivingDisplayNameAfter -Message "Surviving employee's Display Name value is unchanged"
        Assert-Condition -Condition ($survivingMvoAfter.isPendingDeletion -eq $false) -Message "Surviving employee's Metaverse Object is not pending deletion"

        # Step 4m: A further Full Synchronisation does NOT report another sweep (arming cleared)
        Write-TestStep "4m" "Verify a further Full Synchronisation does not re-run the sweep"
        $finalSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $finalSyncResult.activityId -Name "CSV Full Sync (Test 4, post-sweep run)"
        $finalSyncActivity = Get-JIMActivity -Id $finalSyncResult.activityId
        Assert-Condition -Condition ($finalSyncActivity.message -notlike '*Stranded-value sweep executed*') -Message "Further Full Synchronisation Activity Message does not report the sweep (arming was cleared)"

        # Step 4n: Restore the threshold setting and the User type's Deletion Rule, so later
        # scenarios (and a re-run of this one) are unaffected.
        Write-TestStep "4n" "Restore the threshold setting and the User type's Deletion Rule"
        Reset-JIMServiceSetting -Key $maxMissingPercentKey | Out-Null
        $restoreParams = @{
            Id           = $userType.id
            DeletionRule = [string]$userTypeBefore.deletionRule
        }
        if ($null -ne $userTypeBefore.deletionGracePeriod) { $restoreParams.DeletionGracePeriod = [TimeSpan]$userTypeBefore.deletionGracePeriod }
        if ($userTypeBefore.deletionTriggerConnectedSystemIds) { $restoreParams.DeletionTriggerConnectedSystemIds = [int[]]$userTypeBefore.deletionTriggerConnectedSystemIds }
        if ($null -ne $userTypeBefore.deletionTriggerMode) { $restoreParams.DeletionTriggerMode = [string]$userTypeBefore.deletionTriggerMode }
        Set-JIMMetaverseObjectType @restoreParams | Out-Null

        Write-Host "  ✓ Test 4 PASSED: post-clear reconciliation refuses on a shortfall, then recalls values and applies Deletion Rules once re-imported" -ForegroundColor Green
        $testResults.Steps += @{ Name = "StrandedSweep"; Success = $true }
    }

    # =============================================================================================================
    # Summary
    # =============================================================================================================
    Write-TestSection "Results"
    $testResults.Success = $true
    $testResults.EndTime = (Get-Date).ToString("o")

    $passedCount = ($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = $testResults.Steps.Count
    Write-Host "Passed: $passedCount / $totalCount" -ForegroundColor Green

    return $testResults
}
catch {
    Write-Host "`n✗ Scenario 7 FAILED" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host "Stack trace: $($_.ScriptStackTrace)" -ForegroundColor DarkGray

    $testResults.Success = $false
    $testResults.Error = $_.ToString()
    $testResults.EndTime = (Get-Date).ToString("o")

    return $testResults
}
