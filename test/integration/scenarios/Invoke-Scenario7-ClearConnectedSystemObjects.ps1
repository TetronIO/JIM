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

.PARAMETER Step
    Which test step to execute

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

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
    [ValidateSet("DeleteHistory", "KeepHistory", "EdgeCases", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

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
        Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force
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
        Clear-JIMConnectedSystem -Id $config.CSVSystemId -KeepChangeHistory -Force
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
        Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force
        Write-Host "  Pre-cleared connector space" -ForegroundColor Gray

        # Clear again — should succeed without error
        Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force
        Write-Host "  ✓ Clearing empty connector space succeeded without error" -ForegroundColor Green

        # Step 3b: Verify clearing one CS does not affect another
        Write-TestStep "3b" "Verify cross-system isolation"

        # Import data into CSV system
        $null = Invoke-ImportAndSync -Config $config -Name "Test 3 CSV import"

        # Get LDAP system's current state (it has CSOs from Scenario 1 setup if any were provisioned)
        $ldapHistoryBefore = Get-JIMHistoryCount -ConnectedSystemId $config.LDAPSystemId

        # Clear the CSV system
        Clear-JIMConnectedSystem -Id $config.CSVSystemId -Force
        Write-Host "  Cleared CSV system" -ForegroundColor Gray

        # Verify LDAP system is unaffected
        $ldapHistoryAfter = Get-JIMHistoryCount -ConnectedSystemId $config.LDAPSystemId
        Assert-Equal -Expected $ldapHistoryBefore.changeRecordCount -Actual $ldapHistoryAfter.changeRecordCount -Message "LDAP system change history unaffected by CSV clear"

        Write-Host "  ✓ Test 3 PASSED: Edge cases handled correctly" -ForegroundColor Green
        $testResults.Steps += @{ Name = "EdgeCases"; Success = $true }
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
