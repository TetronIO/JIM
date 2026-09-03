# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 21: Run Profile Safeguards

.DESCRIPTION
    Validates the per-Run-Profile safeguards (#1618): the export limits (Max creates, Max updates,
    Max deletes) and the Full Import deletion-detection limits (Max detected deletions as a count
    and as a share of the Connector Space). Reuses the Scenario 1 fixture (CSV HR source, LDAP
    target).

    Test 1: Export limit (Max creates)
        - Full Import and Full Synchronisation of the CSV baseline stage a create to LDAP per user
        - Put Max creates 1 on the LDAP Export Run Profile with Set-JIMRunProfile
        - Run the export: exactly one create is attempted, the rest are withheld
        - Assert: the Activity is CompleteWithWarning, its warning names the limit and what remains,
          its counters carry the withheld creates and zero updates and deletes, and its message
          reports one success
        - Clear the limit with -MaxCreates $null (an explicit null clears; an omitted parameter
          leaves the value alone), run the export again
        - Assert: everything withheld is created this time, and the counter is zero

    Test 2: Full Import deletion-detection limit (Max detected deletions percent)
        - Full Import and Full Synchronisation of the CSV baseline
        - Put Max detected deletions percent 10 on the CSV Full Import Run Profile
        - Remove one employee from the CSV (the Nano template's 3 users make this 33%) and run the
          Full Import
        - Assert: the Activity is CompleteWithWarning, its warning names the count, the share and
          the limit, nothing was marked as deleted, and the Connected System's
          LastSuccessfulFullImportCompletedAt did not move (the #1605 gate stays shut)
        - Raise the limit to 50 and run the Full Import again
        - Assert: the departed employee is now marked as deleted, the counter is zero and the
          timestamp moved
        - Restores the CSV, clears the limit and re-imports, so later scenarios are unaffected

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
    # Test 1: Export limit (Max creates)
    # =============================================================================================================
    if ($Step -eq "ExportLimit" -or $Step -eq "All") {
        Write-TestSection "Test 1: Export Limit (Max creates)"

        # Step 1a: Stage a create to LDAP for every user in the CSV
        Write-TestStep "1a" "Import and synchronise the CSV baseline (stages the LDAP creates)"
        $baselineCycle = Invoke-ImportAndSync -Config $config -Name "Test 1 baseline"
        $baselineStats = Get-JIMActivityStats -ActivityId $baselineCycle.ImportActivityId
        Assert-Condition -Condition ($baselineStats.totalCsoAdds -gt 0) -Message "Baseline import created CSOs (got $($baselineStats.totalCsoAdds) adds)"

        $ldapSystemBefore = Get-JIMConnectedSystem -Id $config.LDAPSystemId
        $pendingBefore = [int]$ldapSystemBefore.pendingExportCount
        Assert-Condition -Condition ($pendingBefore -ge 2) -Message "At least two exports are pending to LDAP, so a limit of one withholds something (got $pendingBefore)"
        Write-Host "  Pending Exports to LDAP before the capped export: $pendingBefore" -ForegroundColor Gray

        # Step 1b: Put Max creates 1 on the LDAP Export Run Profile
        Write-TestStep "1b" "Set Max creates 1 on the LDAP Export Run Profile"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxCreates 1 | Out-Null
        $exportProfile = Get-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId | Where-Object { $_.id -eq $config.LDAPExportProfileId }
        Assert-NotNull -Value $exportProfile -Message "The LDAP Export Run Profile is readable"
        Assert-Equal -Expected 1 -Actual ([int]$exportProfile.safeguards.maxCreates) -Message "Get-JIMRunProfile reports Max creates 1"
        Assert-Condition -Condition ($null -eq $exportProfile.safeguards.maxUpdates -and $null -eq $exportProfile.safeguards.maxDeletes) -Message "Max updates and Max deletes remain unset"

        # Step 1c: Run the capped export
        Write-TestStep "1c" "Run the export (one create attempted, the rest withheld)"
        $cappedExportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $cappedExportResult.activityId -Name "LDAP Export (Test 1 capped)" -AllowWarnings
        $cappedExportActivity = Get-JIMActivity -Id $cappedExportResult.activityId
        Assert-Equal -Expected "CompleteWithWarning" -Actual ([string]$cappedExportActivity.status) -Message "The capped export completed with a warning"
        $expectedWithheld = $pendingBefore - 1
        Assert-Equal -Expected $expectedWithheld -Actual ([int]$cappedExportActivity.exportCreatesWithheld) -Message "The Activity records $expectedWithheld creates withheld"
        Assert-Equal -Expected 0 -Actual ([int]$cappedExportActivity.exportUpdatesWithheld) -Message "No updates were withheld"
        Assert-Equal -Expected 0 -Actual ([int]$cappedExportActivity.exportDeletesWithheld) -Message "No deletes were withheld"
        Assert-Condition -Condition ($cappedExportActivity.warningMessage -like '*Stopped processing creates after 1,*') -Message "The warning names the limit (got: $($cappedExportActivity.warningMessage))"
        Assert-Condition -Condition ($cappedExportActivity.warningMessage -like "*$expectedWithheld create*remain*pending*") -Message "The warning states what remains pending (got: $($cappedExportActivity.warningMessage))"
        Assert-Equal -Expected 1 -Actual (Get-ExportSucceededCount -ActivityId $cappedExportResult.activityId) -Message "Exactly one export succeeded"

        # Step 1d: Clear the limit with an explicit null
        Write-TestStep "1d" "Clear Max creates with an explicit null"
        Set-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -MaxCreates $null | Out-Null
        $clearedProfile = Get-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId | Where-Object { $_.id -eq $config.LDAPExportProfileId }
        Assert-Condition -Condition ($null -eq $clearedProfile.safeguards.maxCreates) -Message "Max creates is cleared"

        # Step 1e: Run the export again; the withheld creates go through without any reset
        Write-TestStep "1e" "Run the export again (the withheld creates are attempted)"
        $resumedExportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ExportSuccess -ActivityId $resumedExportResult.activityId -Name "LDAP Export (Test 1 resumed)"
        $resumedExportActivity = Get-JIMActivity -Id $resumedExportResult.activityId
        Assert-Equal -Expected 0 -Actual ([int]$resumedExportActivity.exportCreatesWithheld) -Message "Nothing was withheld on the resumed export"
        Assert-Equal -Expected $expectedWithheld -Actual (Get-ExportSucceededCount -ActivityId $resumedExportResult.activityId) -Message "The resumed export created everything the capped run withheld"

        $testResults.Steps += @{ Name = "Export limit (Max creates)"; Success = $true }
        Write-Host "  ✓ Test 1 passed" -ForegroundColor Green
    }

    # =============================================================================================================
    # Test 2: Full Import deletion-detection limit (Max detected deletions percent)
    # =============================================================================================================
    if ($Step -eq "ImportLimit" -or $Step -eq "All") {
        Write-TestSection "Test 2: Full Import Deletion-Detection Limit"

        # Step 2a: Baseline, and pick the departing employee deterministically (the first row)
        Write-TestStep "2a" "Import and synchronise the CSV baseline"
        $baselineCycle2 = Invoke-ImportAndSync -Config $config -Name "Test 2 baseline"
        $baselineStats2 = Get-JIMActivityStats -ActivityId $baselineCycle2.ImportActivityId
        Assert-Condition -Condition ($baselineStats2.totalCsoAdds -ge 0) -Message "Baseline import ran (got $($baselineStats2.totalCsoAdds) adds)"

        $baselineCsvRows = @(Import-Csv $csvPath)
        Assert-Condition -Condition ($baselineCsvRows.Count -ge 2) -Message "Baseline CSV has at least two rows (got $($baselineCsvRows.Count))"
        $departingEmployeeId = $baselineCsvRows[0].employeeId
        $expectedSharePercent = [int][Math]::Floor(100.0 / $baselineCsvRows.Count)
        Assert-Condition -Condition ($expectedSharePercent -gt 10) -Message "Removing one row exceeds a 10% limit on this template (one row is about $expectedSharePercent%)"
        Write-Host "  Departing employeeId: $departingEmployeeId (about $expectedSharePercent% of $($baselineCsvRows.Count))" -ForegroundColor Gray

        $csvSystemBefore = Get-JIMConnectedSystem -Id $config.CSVSystemId
        $lastSuccessfulImportBefore = $csvSystemBefore.lastSuccessfulFullImportCompletedAt
        Assert-NotNull -Value $lastSuccessfulImportBefore -Message "The baseline Full Import stamped LastSuccessfulFullImportCompletedAt"

        # Step 2b: Put Max detected deletions percent 10 on the CSV Full Import Run Profile
        Write-TestStep "2b" "Set Max detected deletions percent 10 on the CSV Full Import Run Profile"
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletionsPercent 10 | Out-Null
        $importProfile = Get-JIMRunProfile -ConnectedSystemId $config.CSVSystemId | Where-Object { $_.id -eq $config.CSVImportProfileId }
        Assert-Equal -Expected 10 -Actual ([int]$importProfile.safeguards.maxDetectedDeletionsPercent) -Message "Get-JIMRunProfile reports Max detected deletions percent 10"

        # Step 2c: Remove one employee and run the Full Import; the detection is refused
        Write-TestStep "2c" "Remove one employee from the CSV and run the Full Import (detection refused)"
        $partialCsv = @($baselineCsvRows | Where-Object { $_.employeeId -ne $departingEmployeeId })
        $partialCsv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        $refusedImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $refusedImportResult.activityId -Name "CSV Full Import (Test 2 refused)" -AllowWarnings
        $refusedImportActivity = Get-JIMActivity -Id $refusedImportResult.activityId
        Assert-Equal -Expected "CompleteWithWarning" -Actual ([string]$refusedImportActivity.status) -Message "The refused Full Import completed with a warning"
        Assert-Equal -Expected 1 -Actual ([int]$refusedImportActivity.detectedDeletionsWithheld) -Message "The Activity records one detected deletion withheld"
        Assert-Equal -Expected 0 -Actual ([int]$refusedImportActivity.totalDeleted) -Message "Nothing was marked as deleted"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*Deletion detection found 1 object*') -Message "The warning names the count (got: $($refusedImportActivity.warningMessage))"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*limit of 10%*') -Message "The warning names the limit (got: $($refusedImportActivity.warningMessage))"
        Assert-Condition -Condition ($refusedImportActivity.warningMessage -like '*none were marked as deleted*') -Message "The warning states nothing was marked (got: $($refusedImportActivity.warningMessage))"

        $csvSystemAfterRefusal = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Equal -Expected ([string]$lastSuccessfulImportBefore) -Actual ([string]$csvSystemAfterRefusal.lastSuccessfulFullImportCompletedAt) -Message "A refused Full Import does not move LastSuccessfulFullImportCompletedAt (the #1605 gate stays shut)"

        # Step 2d: Raise the limit and run again; the departure is now applied
        Write-TestStep "2d" "Raise the limit to 50 and run the Full Import again (detection applied)"
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletionsPercent 50 | Out-Null
        $appliedImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $appliedImportResult.activityId -Name "CSV Full Import (Test 2 applied)"
        $appliedImportActivity = Get-JIMActivity -Id $appliedImportResult.activityId
        Assert-Equal -Expected 0 -Actual ([int]$appliedImportActivity.detectedDeletionsWithheld) -Message "Nothing was withheld once the limit allows the departure"
        Assert-Equal -Expected 1 -Actual ([int]$appliedImportActivity.totalDeleted) -Message "The departed employee was marked as deleted"

        $csvSystemAfterApplied = Get-JIMConnectedSystem -Id $config.CSVSystemId
        Assert-Condition -Condition (([string]$csvSystemAfterApplied.lastSuccessfulFullImportCompletedAt) -ne ([string]$lastSuccessfulImportBefore)) -Message "A Full Import that applied its detection moves LastSuccessfulFullImportCompletedAt"

        # Step 2e: Restore the CSV and the Run Profile, and re-import, so later scenarios see the full population
        Write-TestStep "2e" "Restore the CSV, clear the limit and re-import"
        $baselineCsvRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Set-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -MaxDetectedDeletionsPercent $null | Out-Null
        $restoredProfile = Get-JIMRunProfile -ConnectedSystemId $config.CSVSystemId | Where-Object { $_.id -eq $config.CSVImportProfileId }
        Assert-Condition -Condition ($null -eq $restoredProfile.safeguards.maxDetectedDeletionsPercent) -Message "Max detected deletions percent is cleared"
        $restoreCycle = Invoke-ImportAndSync -Config $config -Name "Test 2 restore"
        Assert-NotNull -Value $restoreCycle.ImportActivityId -Message "The restore import ran"

        $testResults.Steps += @{ Name = "Full Import deletion-detection limit"; Success = $true }
        Write-Host "  ✓ Test 2 passed" -ForegroundColor Green
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
    Write-Host "`n✗ Scenario 21 FAILED" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host "Stack trace: $($_.ScriptStackTrace)" -ForegroundColor DarkGray

    $testResults.Success = $false
    $testResults.Error = $_.ToString()
    $testResults.EndTime = (Get-Date).ToString("o")

    return $testResults
}
