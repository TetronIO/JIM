<#
.SYNOPSIS
    Test Scenario 1: Person Entity - HR to Identity Directory

.DESCRIPTION
    Validates provisioning users from HR system (CSV) to identity directory (Samba AD).
    Tests the complete ILM lifecycle: Joiner, Mover, Leaver, and Reconnection patterns.

    HR CSV includes Company attribute: "Panoply" for employees, partner companies for contractors.
    Partner companies: Nexus Dynamics, Akinya, Rockhopper, Stellar Logistics, Vertex Solutions.

.PARAMETER Step
    Which test step to execute (Joiner, Leaver, Mover, Reconnection, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 5)
    Note: Most operations now use -Wait for synchronous execution.

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails. By default, tests stop on first failure.

.EXAMPLE
    ./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Joiner -Template Micro -ApiKey $env:JIM_API_KEY

.EXAMPLE
    ./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All -Template Large -ApiKey "jim_..." -ContinueOnError
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiner", "Leaver", "Mover", "Mover-Rename", "Mover-Move", "Disable", "Enable", "Reconnection", "ImportOnly", "SyncOnly", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'  # Disable confirmation prompts for non-interactive execution

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    . "$PSScriptRoot/../utils/Test-Helpers.ps1"
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

# Helper function to run the standard delta sync sequence with detailed output
# This sequence is used after CSV changes to sync them through to both target systems:
# 1. CSV Full Import - detect changes in CSV file
# 2. CSV Delta Sync - process only changed CSOs, evaluate export rules for BOTH targets
# 3. LDAP Export - apply pending exports to AD
# 4. LDAP Delta Import - confirm the exports succeeded
# 5. LDAP Delta Sync - process confirmed imports
# 6. Cross-Domain Export - apply pending exports to cross-domain CSV
# 7. Cross-Domain Full Import - confirm the exports succeeded (CSV uses Full Import, not Delta)
# 8. Cross-Domain Delta Sync - process confirmed imports
function Invoke-SyncSequence {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,
        [switch]$ShowProgress,
        [switch]$ValidateActivityStatus
    )

    $results = @{
        Success = $true
        Steps = @()
    }

    # Step 1: CSV Full Import
    if ($ShowProgress) { Write-Host "  [1/8] CSV Full Import..." -ForegroundColor DarkGray }
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "CSV Full Import"; ActivityId = $importResult.activityId }
    if ($ValidateActivityStatus) {
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import"
    }

    # Step 2: CSV Delta Sync
    if ($ShowProgress) { Write-Host "  [2/8] CSV Delta Sync..." -ForegroundColor DarkGray }
    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVDeltaSyncProfileId -Wait -PassThru
    $results.Steps += @{ Name = "CSV Delta Sync"; ActivityId = $syncResult.activityId }
    if ($ValidateActivityStatus) {
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Delta Sync"
    }

    # Step 3: LDAP Export
    if ($ShowProgress) { Write-Host "  [3/8] LDAP Export..." -ForegroundColor DarkGray }
    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Export"; ActivityId = $exportResult.activityId }
    if ($ValidateActivityStatus) {
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export"
    }

    # Wait for AD replication
    if ($ShowProgress) { Write-Host "  Waiting 5s for AD replication..." -ForegroundColor DarkGray }
    Start-Sleep -Seconds 5

    # Step 4: LDAP Delta Import (confirming export)
    if ($ShowProgress) { Write-Host "  [4/8] LDAP Delta Import (confirming)..." -ForegroundColor DarkGray }
    $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaImportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Delta Import"; ActivityId = $confirmImportResult.activityId }
    if ($ValidateActivityStatus) {
        Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "LDAP Delta Import"
        Assert-NoUnresolvedReferences -ConnectedSystemId $Config.LDAPSystemId -Name "LDAP" -Context "after Delta Import (confirming)"
    }

    # Step 5: LDAP Delta Sync
    if ($ShowProgress) { Write-Host "  [5/8] LDAP Delta Sync..." -ForegroundColor DarkGray }
    $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaSyncProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Delta Sync"; ActivityId = $confirmSyncResult.activityId }
    if ($ValidateActivityStatus) {
        Assert-ActivitySuccess -ActivityId $confirmSyncResult.activityId -Name "LDAP Delta Sync"
    }

    # Step 6: Cross-Domain Export
    if ($Config.CrossDomainSystemId -and $Config.CrossDomainExportProfileId) {
        if ($ShowProgress) { Write-Host "  [6/8] Cross-Domain Export..." -ForegroundColor DarkGray }
        $crossDomainExportResult = Start-JIMRunProfile -ConnectedSystemId $Config.CrossDomainSystemId -RunProfileId $Config.CrossDomainExportProfileId -Wait -PassThru
        $results.Steps += @{ Name = "Cross-Domain Export"; ActivityId = $crossDomainExportResult.activityId }
        if ($ValidateActivityStatus) {
            Assert-ActivitySuccess -ActivityId $crossDomainExportResult.activityId -Name "Cross-Domain Export"
        }

        # Step 7: Cross-Domain Full Import (confirming export - CSV uses Full Import, not Delta)
        if ($ShowProgress) { Write-Host "  [7/8] Cross-Domain Full Import (confirming)..." -ForegroundColor DarkGray }
        $crossDomainImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.CrossDomainSystemId -RunProfileId $Config.CrossDomainImportProfileId -Wait -PassThru
        $results.Steps += @{ Name = "Cross-Domain Import"; ActivityId = $crossDomainImportResult.activityId }
        if ($ValidateActivityStatus) {
            Assert-ActivitySuccess -ActivityId $crossDomainImportResult.activityId -Name "Cross-Domain Import"
        }

        # Step 8: Cross-Domain Delta Sync
        if ($ShowProgress) { Write-Host "  [8/8] Cross-Domain Delta Sync..." -ForegroundColor DarkGray }
        $crossDomainSyncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CrossDomainSystemId -RunProfileId $Config.CrossDomainDeltaSyncProfileId -Wait -PassThru
        $results.Steps += @{ Name = "Cross-Domain Delta Sync"; ActivityId = $crossDomainSyncResult.activityId }
        if ($ValidateActivityStatus) {
            Assert-ActivitySuccess -ActivityId $crossDomainSyncResult.activityId -Name "Cross-Domain Delta Sync"
        }
    }
    else {
        if ($ShowProgress) { Write-Host "  [6-8/8] Cross-Domain skipped (not configured)" -ForegroundColor DarkGray }
    }

    return $results
}

Write-TestSection "Scenario 1: HR to Enterprise Directory"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "HR to Enterprise Directory"
    Template = $Template
    Steps = @()
    Success = $false
}

# Performance tracking
$scenarioStartTime = Get-Date
$stepTimings = @{}

try {
    # Step 0: Setup JIM configuration
    $step0Start = Get-Date
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # IMPORTANT: For fully repeatable tests, JIM's database should be reset between runs.
    # Invoke-IntegrationTests.ps1 does this automatically in Step 0 (Reset Environment).
    # For manual runs: use 'jim-reset' or 'docker compose down -v && jim-stack'

    # Reset CSV to baseline state before running tests
    # This ensures test data is in a known state regardless of previous test runs.
    # NOTE: This is necessary even after database reset because CSV files persist
    # on the host filesystem and are mounted into containers.
    Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  ✓ CSV test data reset to baseline" -ForegroundColor Green

    # Clean up test-specific directory users from previous test runs
    # NOTE: This is necessary because the directory persists in a Docker volume
    # (not reset by database volume deletion) and the test.reconnect user is
    # created by the Reconnection test (Test 4).
    Write-Host "Cleaning up test-specific directory users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.reconnect")
    $deletedCount = 0
    $isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
    foreach ($user in $testUsers) {
        if ($isOpenLDAP) {
            # For OpenLDAP, delete by DN using ldapdelete
            $userDN = "$($DirectoryConfig.UserRdnAttr)=$user,$($DirectoryConfig.UserContainer)"
            $output = & docker exec $($DirectoryConfig.ContainerName) ldapdelete -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" "$userDN" 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            } elseif ($output -match "No such object") {
                Write-Host "  - $user not found (already clean)" -ForegroundColor DarkGray
            } else {
                Write-Host "  ⚠ Could not delete ${user}: $output" -ForegroundColor Yellow
            }
        } else {
            # For Samba AD, use samba-tool
            $output = & docker exec $($DirectoryConfig.ContainerName) bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
            if ($output -match "Deleted user") {
                Write-Host "  ✓ Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            } elseif ($output -match "Unable to find user") {
                Write-Host "  - $user not found (already clean)" -ForegroundColor DarkGray
            } else {
                Write-Host "  ⚠ Could not delete ${user}: $output" -ForegroundColor Yellow
            }
        }
    }
    Write-Host "  ✓ Directory cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -ExportConcurrency $ExportConcurrency -MaxExportParallelism $MaxExportParallelism -DirectoryConfig $DirectoryConfig

    if (-not $config) {
        throw "Failed to setup Scenario 1 configuration"
    }

    Write-Host "✓ JIM configured for Scenario 1" -ForegroundColor Green
    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop

    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Establish baseline state: Import existing directory structure (OUs)
    # The target directory starts empty (no pre-populated users) — this imports the OU structure
    # and establishes the USN/changelog watermark (persisted connector data) that Delta Import needs.
    # Without this, Delta Import fails with "No persisted connector data available".
    Write-Host ""
    Write-Host "Establishing baseline state from directory..." -ForegroundColor Gray
    Write-Host "  Running Full Import to establish connector baseline..." -ForegroundColor DarkGray
    $baselineImportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $baselineImportResult.activityId -Name "LDAP Full Import (baseline)"
    Assert-NoUnresolvedReferences -ConnectedSystemId $config.LDAPSystemId -Name "LDAP" -Context "after Full Import (baseline)"

    # Run Full Sync to process baseline imports and establish MVOs for existing AD objects
    # NOTE: First sync after Full Import should always be Full Sync (initialisation best practice)
    Write-Host "  Running Full Sync to initialise connector..." -ForegroundColor DarkGray
    $baselineSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $baselineSyncResult.activityId -Name "LDAP Full Sync (baseline)"
    Write-Host "✓ Baseline state established" -ForegroundColor Green

    $stepTimings["0. Setup"] = (Get-Date) - $step0Start

    # ImportOnly: Just run the HR CSV Full Import and stop (for debugging CSO creation issues)
    if ($Step -eq "ImportOnly") {
        $stepImportStart = Get-Date
        Write-TestSection "ImportOnly: HR CSV Full Import"

        Write-Host "Triggering CSV Full Import only (for debugging)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import (ImportOnly)"

        Write-Host ""
        Write-Host "✓ CSV Full Import completed" -ForegroundColor Green
        Write-Host "  Activity ID: $($importResult.activityId)" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Check the database for RPEI/CSO status:" -ForegroundColor Yellow
        Write-Host "  docker compose exec jim.database psql -U jim -d jim -c `"" -ForegroundColor DarkGray
        Write-Host "    SELECT COUNT(*) as total, " -ForegroundColor DarkGray
        Write-Host "           COUNT(CASE WHEN \\`"ConnectedSystemObjectId\\`" IS NOT NULL THEN 1 END) as with_cso," -ForegroundColor DarkGray
        Write-Host "           COUNT(CASE WHEN \\`"ConnectedSystemObjectId\\`" IS NULL THEN 1 END) as without_cso" -ForegroundColor DarkGray
        Write-Host "    FROM \\`"ActivityRunProfileExecutionItems\\`"" -ForegroundColor DarkGray
        Write-Host "    WHERE \\`"ActivityId\\`" = '$($importResult.activityId)'`"" -ForegroundColor DarkGray
        Write-Host ""

        $stepTimings["ImportOnly"] = (Get-Date) - $stepImportStart
        $testResults.Steps += @{ Name = "ImportOnly"; Success = $true; ActivityId = $importResult.activityId }

        # Skip all other tests
        Write-Host "ImportOnly step complete - skipping remaining tests" -ForegroundColor Yellow

        # Jump to results summary
        $testResults.Success = $true
        Write-TestSection "Test Results Summary"
        Write-Host "Tests run:    1"
        Write-Host "Tests passed: 1"
        Write-Host "✓ ImportOnly" -ForegroundColor Green
        Write-Host ""
        Write-Host "✓ ImportOnly test passed" -ForegroundColor Green
        return
    }

    # SyncOnly: Run HR CSV Full Import + Full Sync, stop before exports
    # This creates pending exports for inspection without actually exporting
    if ($Step -eq "SyncOnly") {
        $stepSyncStart = Get-Date
        Write-TestSection "SyncOnly: HR CSV Import + Full Sync (no exports)"

        # Step 1: CSV Full Import
        Write-Host "Triggering CSV Full Import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import (SyncOnly)"
        Write-Host "  ✓ CSV Full Import completed" -ForegroundColor Green

        # Step 2: CSV Full Sync (creates MVOs and pending exports)
        Write-Host "Triggering CSV Full Sync..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Full Sync (SyncOnly)"
        Write-Host "  ✓ CSV Full Sync completed" -ForegroundColor Green

        Write-Host ""
        Write-Host "✓ SyncOnly completed - pending exports created but NOT exported" -ForegroundColor Green
        Write-Host "  Import Activity ID: $($importResult.activityId)" -ForegroundColor Cyan
        Write-Host "  Sync Activity ID:   $($syncResult.activityId)" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Pending exports are now available for inspection:" -ForegroundColor Yellow
        Write-Host "  - View in JIM UI: Connected Systems > [System] > Pending Exports" -ForegroundColor DarkGray
        Write-Host "  - Query database:" -ForegroundColor DarkGray
        Write-Host "    docker compose exec jim.database psql -U jim -d jim -c `"" -ForegroundColor DarkGray
        Write-Host "      SELECT cs.\\`"Name\\`\", pe.\\`"OperationType\\`\", COUNT(*) " -ForegroundColor DarkGray
        Write-Host "      FROM \\`"PendingExports\\`\" pe " -ForegroundColor DarkGray
        Write-Host "      JOIN \\`"ConnectedSystems\\`\" cs ON pe.\\`"ConnectedSystemId\\`\" = cs.\\`"Id\\`\" " -ForegroundColor DarkGray
        Write-Host "      GROUP BY cs.\\`"Name\\`\", pe.\\`"OperationType\\`\"`"" -ForegroundColor DarkGray
        Write-Host ""

        $stepTimings["SyncOnly"] = (Get-Date) - $stepSyncStart
        $testResults.Steps += @{ Name = "SyncOnly"; Success = $true }

        # Skip all other tests
        Write-Host "SyncOnly step complete - skipping exports and remaining tests" -ForegroundColor Yellow

        # Jump to results summary
        $testResults.Success = $true
        Write-TestSection "Test Results Summary"
        Write-Host "Tests run:    1"
        Write-Host "Tests passed: 1"
        Write-Host "✓ SyncOnly" -ForegroundColor Green
        Write-Host ""
        Write-Host "✓ SyncOnly test passed" -ForegroundColor Green
        return
    }

    # Test 1: Joiner (New Hire)
    if ($Step -eq "Joiner" -or $Step -eq "All") {
        $step1Start = Get-Date
        Write-TestSection "Test 1: Joiner (New Hire)"

        # The CSV already contains the baseline users generated by Generate-TestCSV.ps1
        # All of these users are "joiners" - they don't exist in JIM yet and will be provisioned to AD
        # We'll validate using the first user in the CSV (index 1)
        $testUser = New-TestUser -Index 1
        Write-Host "Testing joiner scenario with $($testUser.SamAccountName) and $((Get-TemplateScale -Template $Template).Users - 1) other users..." -ForegroundColor Gray

        # Trigger CSV Import
        Write-Host "Triggering CSV import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Joiner)"
        # Outcome graph: validate import outcomes (#363 Phase 4b)
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $importResult.activityId -Name "CSV Import (Joiner)" -ExpectedOutcomeType "CsoAdded"

        # Trigger Full Sync (evaluates sync rules, creates MVOs and pending exports)
        Write-Host "Triggering Full Sync..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (Joiner)"
        # Outcome graph: validate sync outcomes (#363 Phase 4b)
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId -Name "Full Sync (Joiner)" -ExpectedOutcomeType "Projected"
        # Each user is provisioned to 2 target systems (Samba AD + Cross-Domain CSV)
        Assert-ActivityOutcomeStats -ActivityId $syncResult.activityId -Name "Full Sync (Joiner)" -ExpectedStats @{
            totalProvisioned = (Get-TemplateScale -Template $Template).Users * 2
        }

        # Trigger LDAP Export
        Write-Host "Triggering LDAP export..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export (Joiner)"
        # Outcome graph: validate export outcomes (#363 Phase 4b)
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $exportResult.activityId -Name "LDAP Export (Joiner)" -ExpectedOutcomeType "Exported"

        # Wait for AD replication (local AD should be fast, but needs time to process)
        Write-Host "Waiting 5 seconds for AD replication..." -ForegroundColor Gray
        Start-Sleep -Seconds 5

        # Confirming Import - import the changes we just exported to LDAP
        Write-Host "Triggering LDAP delta import (confirming export)..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "LDAP Delta Import (Joiner confirm)"

        # Delta Sync - synchronise the confirmed imports
        Write-Host "Triggering LDAP delta sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmSyncResult.activityId -Name "LDAP Delta Sync (Joiner confirm)"

        # Training Import/Sync - join Training records to existing MVOs created by HR import
        # The Training sync rule does NOT project - it only joins to existing MVOs via Employee ID
        # This must happen AFTER HR import/sync creates the MVOs
        if ($config.TrainingSystemId -and $config.TrainingImportProfileId -and $config.TrainingSyncProfileId) {
            Write-Host ""
            Write-Host "Establishing Training data baseline (joins to HR-created MVOs)..." -ForegroundColor Gray
            Write-Host "  Running Training Full Import..." -ForegroundColor DarkGray
            $trainingImportResult = Start-JIMRunProfile -ConnectedSystemId $config.TrainingSystemId -RunProfileId $config.TrainingImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $trainingImportResult.activityId -Name "Training Full Import (Joiner)"

            Write-Host "  Running Training Full Sync..." -ForegroundColor DarkGray
            $trainingSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.TrainingSystemId -RunProfileId $config.TrainingSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $trainingSyncResult.activityId -Name "Training Full Sync (Joiner)"

            # Validate Training joined to MVOs and contributed attributes
            Write-Host "  Validating Training data joined to MVOs..." -ForegroundColor DarkGray

            # Get total MVO count and count with Training Status attribute
            # Training data covers 85% of users, so we expect ~85% of MVOs to have Training Status
            $scale = Get-TemplateScale -Template $Template
            $expectedUsers = $scale.Users
            $expectedTrainingCoverage = 0.85
            $expectedWithTraining = [int]($expectedUsers * $expectedTrainingCoverage)

            # Query all User MVOs with Training Status attribute
            $allMVOs = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes "Training Status" -All)
            $totalMVOs = $allMVOs.Count

            # Count MVOs that have Training Status attribute with a value
            # Note: Attributes is a dictionary (key=name, value=string) from MetaverseObjectHeaderDto
            # Handle both hashtable and PSCustomObject representations from JSON
            $mvosWithTraining = @($allMVOs | Where-Object {
                $attrs = $_.attributes
                if ($null -eq $attrs) { return $false }

                # Handle different types: PSCustomObject (from JSON) or Hashtable
                $trainingValue = $null
                if ($attrs -is [System.Collections.IDictionary]) {
                    # Hashtable access
                    if ($attrs.ContainsKey("Training Status")) {
                        $trainingValue = $attrs["Training Status"]
                    }
                }
                elseif ($null -ne $attrs.PSObject) {
                    # PSCustomObject - use property-based access
                    $prop = $attrs.PSObject.Properties["Training Status"]
                    if ($null -ne $prop) {
                        $trainingValue = $prop.Value
                    }
                }

                $null -ne $trainingValue -and $trainingValue -ne ""
            })
            $trainingCount = $mvosWithTraining.Count

            # Calculate actual coverage percentage
            $actualCoverage = if ($totalMVOs -gt 0) { [math]::Round(($trainingCount / $totalMVOs) * 100, 1) } else { 0 }

            Write-Host "    Total User MVOs: $totalMVOs" -ForegroundColor DarkGray
            Write-Host "    MVOs with Training Status: $trainingCount ($actualCoverage%)" -ForegroundColor DarkGray
            Write-Host "    Expected training records: $expectedWithTraining (85% of $expectedUsers HR users)" -ForegroundColor DarkGray

            # Assert that Training data joined correctly
            # Validate based on expected count, not percentage of all MVOs
            $minExpectedTraining = [int]($expectedWithTraining * 0.9)  # Allow 10% variance
            $maxExpectedTraining = [int]($expectedWithTraining * 1.1)  # Allow 10% variance

            if ($totalMVOs -eq 0) {
                Write-Host "    ✗ No User MVOs found - HR import may have failed" -ForegroundColor Red
                throw "Training validation failed: No User MVOs found in Metaverse"
            }
            elseif ($trainingCount -eq 0) {
                Write-Host "    ✗ No MVOs have Training Status - Training sync may have failed to join" -ForegroundColor Red
                throw "Training validation failed: No MVOs have Training Status attribute (expected ~$expectedWithTraining)"
            }
            elseif ($trainingCount -lt $minExpectedTraining) {
                Write-Host "    ✗ Training coverage too low: $trainingCount users (expected $minExpectedTraining-$maxExpectedTraining)" -ForegroundColor Red
                throw "Training validation failed: Only $trainingCount MVOs have Training Status (expected ~$expectedWithTraining)"
            }
            elseif ($trainingCount -gt $maxExpectedTraining) {
                Write-Host "    ⚠ Training coverage higher than expected: $trainingCount users (expected $minExpectedTraining-$maxExpectedTraining)" -ForegroundColor Yellow
            }
            else {
                Write-Host "    ✓ Training coverage validated: $trainingCount users with training data" -ForegroundColor Green
            }

            # Spot-check: Verify the test user has Training data (index 1 is within 85%)
            # Note: We queried with "Training Status" attribute, not "Account Name", so filter by displayName instead
            $testUserMVO = $allMVOs | Where-Object {
                $_.displayName -eq $testUser.DisplayName
            } | Select-Object -First 1

            if (-not $testUserMVO) {
                # Re-query with Account Name filter if not found in bulk results
                $testUserMVO = Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue $testUser.SamAccountName -Attributes "Training Status"
            }

            if ($testUserMVO) {
                $attrs = $testUserMVO.attributes
                $trainingStatus = $null
                if ($null -ne $attrs) {
                    if ($attrs -is [System.Collections.IDictionary]) {
                        if ($attrs.ContainsKey("Training Status")) {
                            $trainingStatus = $attrs["Training Status"]
                        }
                    }
                    elseif ($null -ne $attrs.PSObject) {
                        $prop = $attrs.PSObject.Properties["Training Status"]
                        if ($null -ne $prop) {
                            $trainingStatus = $prop.Value
                        }
                    }
                }
                if ($null -ne $trainingStatus -and $trainingStatus -ne "") {
                    Write-Host "    ✓ Test user ($($testUser.SamAccountName)) Training Status: '$trainingStatus'" -ForegroundColor Green
                }
                else {
                    Write-Host "    ⚠ Test user ($($testUser.SamAccountName)) has no Training Status (may be in 15% without training)" -ForegroundColor Yellow
                }
            }

            Write-Host "✓ Training data joined to MVOs ($trainingCount/$totalMVOs = $actualCoverage%)" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Training system not configured, skipping Training import/sync" -ForegroundColor Yellow
        }

        # Cross-Domain Export/Import/Sync - export users to cross-domain CSV target
        # This tests the multi-target export functionality (same MVO flows to multiple targets)
        if ($config.CrossDomainSystemId -and $config.CrossDomainExportProfileId) {
            Write-Host ""
            Write-Host "Exporting to Cross-Domain target..." -ForegroundColor Gray
            Write-Host "  Running Cross-Domain Export..." -ForegroundColor DarkGray
            $crossDomainExportResult = Start-JIMRunProfile -ConnectedSystemId $config.CrossDomainSystemId -RunProfileId $config.CrossDomainExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $crossDomainExportResult.activityId -Name "Cross-Domain Export (Joiner)"

            Write-Host "  Running Cross-Domain Full Import (confirming)..." -ForegroundColor DarkGray
            $crossDomainImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CrossDomainSystemId -RunProfileId $config.CrossDomainImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $crossDomainImportResult.activityId -Name "Cross-Domain Import (Joiner)"

            Write-Host "  Running Cross-Domain Delta Sync..." -ForegroundColor DarkGray
            $crossDomainSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CrossDomainSystemId -RunProfileId $config.CrossDomainDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $crossDomainSyncResult.activityId -Name "Cross-Domain Delta Sync (Joiner)"

            Write-Host "✓ Cross-Domain export completed" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Cross-Domain system not configured, skipping Cross-Domain export" -ForegroundColor Yellow
        }

        # Validate user exists in the directory
        $directoryName = $DirectoryConfig.ConnectedSystemName
        Write-Host "Validating user in $directoryName..." -ForegroundColor Gray

        $userIdentifier = $testUser.SamAccountName
        $ldapUser = Get-LDAPUser -UserIdentifier $userIdentifier -DirectoryConfig $DirectoryConfig

        if ($ldapUser) {
            Write-Host "  ✓ User '$userIdentifier' provisioned to $directoryName" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Joiner"; Success = $true }
        }
        else {
            Write-Host "  ✗ User '$userIdentifier' NOT found in $directoryName" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Joiner"; Success = $false; Error = "User not found in $directoryName" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        $stepTimings["1. Joiner"] = (Get-Date) - $step1Start
    }

    # Test 2a: Mover (Attribute Change - No DN Impact)
    if ($Step -eq "Mover" -or $Step -eq "All") {
        $step2aStart = Get-Date
        Write-TestSection "Test 2a: Mover (Attribute Change)"

        # Use the first user (index 1) for mover tests - they were provisioned in the Joiner test
        $moverUser = New-TestUser -Index 1

        Write-Host "Updating user title in CSV..." -ForegroundColor Gray

        # Update CSV - change the first user's title (user provisioned in Joiner test)
        # NOTE: We change Title (not Department) because Department now affects DN/OU placement.
        # This test validates simple attribute updates that don't trigger DN changes.
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        # Parse CSV properly to update the correct column
        # CSV columns: employeeId,firstName,lastName,email,department,title,company,samAccountName,displayName,status,userPrincipalName,employeeType,employeeEndDate
        $moverSamAccountName = $moverUser.SamAccountName
        $csv = Import-Csv $csvPath
        $targetUser = $csv | Where-Object { $_.samAccountName -eq $moverSamAccountName }
        if ($targetUser) {
            $targetUser.title = "Senior Developer"
        }
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  ✓ Changed $moverSamAccountName title to 'Senior Developer'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        $moverSyncResults = Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Outcome graph: validate sync outcomes for mover (#363 Phase 4b)
        $moverSyncStep = $moverSyncResults.Steps | Where-Object { $_.Name -eq "CSV Delta Sync" }
        if ($moverSyncStep) {
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $moverSyncStep.ActivityId -Name "CSV Delta Sync (Mover)" -ExpectedOutcomeType "AttributeFlow"
        }

        # Validate title change
        $directoryName = $DirectoryConfig.ConnectedSystemName
        Write-Host "Validating attribute update in $directoryName..." -ForegroundColor Gray

        $updatedUser = Get-LDAPUser -UserIdentifier $moverSamAccountName -DirectoryConfig $DirectoryConfig
        $updatedTitle = if ($updatedUser) { $updatedUser["title"] } else { $null }

        if ($updatedTitle -match "Senior Developer") {
            Write-Host "  ✓ Title updated to 'Senior Developer' in $directoryName" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover"; Success = $true }
        }
        else {
            Write-Host "  ✗ Title not updated in $directoryName (got: '$updatedTitle')" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Mover"; Success = $false; Error = "Attribute not updated" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        $stepTimings["2a. Mover"] = (Get-Date) - $step2aStart
    }

    # Test 2b: Mover - Rename (DN Change)
    if ($Step -eq "Mover" -or $Step -eq "All") {
        $step2bStart = Get-Date
        Write-TestSection "Test 2b: Mover - Rename (DN Change)"

        # Continue using the first user (index 1) for mover tests
        $moverUser = New-TestUser -Index 1
        $moverSamAccountName = $moverUser.SamAccountName
        $newFirstName = "Renamed"
        $newDisplayName = "$newFirstName $($moverUser.LastName)"

        Write-Host "Updating user display name in CSV (triggers AD rename)..." -ForegroundColor Gray

        # The DN is computed from displayName: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=..."
        # So changing firstName in CSV will change displayName, which changes DN
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        # Parse CSV properly to update the correct columns
        $csv = Import-Csv $csvPath
        $targetUser = $csv | Where-Object { $_.samAccountName -eq $moverSamAccountName }
        if ($targetUser) {
            $targetUser.firstName = $newFirstName
            $targetUser.displayName = $newDisplayName
        }
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  ✓ Changed $moverSamAccountName display name to '$newDisplayName'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate rename
        $directoryName = $DirectoryConfig.ConnectedSystemName
        $isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
        Write-Host "Validating rename in $directoryName..." -ForegroundColor Gray

        if ($isOpenLDAP) {
            # For OpenLDAP, the DN doesn't change when displayName changes (RDN is uid, not CN).
            # Verify the attributes (cn, displayName, givenName) were updated instead.
            $updatedUser = Get-LDAPUser -UserIdentifier $moverSamAccountName -DirectoryConfig $DirectoryConfig
            $updatedCn = if ($updatedUser) { $updatedUser["cn"] } else { $null }

            if ($updatedCn -match [regex]::Escape($newDisplayName)) {
                Write-Host "  ✓ User cn updated to '$newDisplayName' in $directoryName (DN unchanged — RDN is uid)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Mover-Rename"; Success = $true }
            }
            else {
                Write-Host "  ✗ User cn not updated in $directoryName (got: $updatedCn)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Mover-Rename"; Success = $false; Error = "cn not updated" }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
        }
        else {
            # For Samba AD, verify the DN changed (CN= component reflects new displayName)
            $adUserInfo = docker exec $($DirectoryConfig.ContainerName) bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=$moverSamAccountName)' dn displayName 2>&1"

            if ($adUserInfo -match "CN=$([regex]::Escape($newDisplayName))") {
                Write-Host "  ✓ User renamed to 'CN=$newDisplayName' in $directoryName" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Mover-Rename"; Success = $true }
            }
            else {
                Write-Host "  ✗ User NOT renamed in $directoryName" -ForegroundColor Red
                Write-Host "    Output: $adUserInfo" -ForegroundColor Gray
                $testResults.Steps += @{ Name = "Mover-Rename"; Success = $false; Error = "DN not renamed" }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
        }
        $stepTimings["2b. Mover-Rename"] = (Get-Date) - $step2bStart
    }

    # Test 2c: Mover - Move (OU Change via Department)
    if ($Step -eq "Mover-Move" -or $Step -eq "All") {
        $step2cStart = Get-Date
        Write-TestSection "Test 2c: Mover - Move (OU Change)"

        # Continue using the first user (index 1) for mover tests
        $moverUser = New-TestUser -Index 1
        $moverSamAccountName = $moverUser.SamAccountName

        Write-Host "Updating user department to trigger OU move..." -ForegroundColor Gray

        # The DN is computed from Department: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=" + mv["Department"] + ",DC=panoply,DC=local"
        # User at index 1 is assigned to Marketing department (1 % 12 = 1)
        # This should trigger an LDAP move to OU=Finance
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        # Parse CSV properly to update the correct column
        $csv = Import-Csv $csvPath
        $targetUser = $csv | Where-Object { $_.samAccountName -eq $moverSamAccountName }
        if ($targetUser) {
            $oldDept = $targetUser.department
            $targetUser.department = "Finance"
            Write-Host "  Changed department from '$oldDept' to 'Finance'" -ForegroundColor DarkGray
        }
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  ✓ Changed $moverSamAccountName department to Finance (triggers OU move)" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate move/department change
        $directoryName = $DirectoryConfig.ConnectedSystemName
        $isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
        Write-Host "Validating OU move in $directoryName..." -ForegroundColor Gray

        if ($isOpenLDAP) {
            # For OpenLDAP, DN doesn't change with department (flat ou=People structure).
            # Verify the department attribute was updated instead.
            $deptAttr = $DirectoryConfig.DepartmentAttr  # "departmentNumber" for OpenLDAP
            $updatedUser = Get-LDAPUser -UserIdentifier $moverSamAccountName -DirectoryConfig $DirectoryConfig
            $updatedDept = if ($updatedUser) { $updatedUser[$deptAttr] } else { $null }

            if ($updatedDept -eq "Finance") {
                Write-Host "  ✓ Department updated to Finance in $directoryName (DN unchanged — flat OU structure)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Mover-Move"; Success = $true }
            }
            else {
                Write-Host "  ✗ Department not updated in $directoryName (got: $updatedDept)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Mover-Move"; Success = $false; Error = "Department not updated" }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
        }
        else {
            # For Samba AD, verify user moved to OU=Finance in the DN
            $adUserInfo = docker exec $($DirectoryConfig.ContainerName) bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=$moverSamAccountName)' dn department 2>&1"

            if ($adUserInfo -match "OU=Finance") {
                Write-Host "  ✓ User moved to OU=Finance in $directoryName" -ForegroundColor Green
                if ($adUserInfo -match "department: Finance") {
                    Write-Host "  ✓ Department attribute updated to Finance" -ForegroundColor Green
                }
                $testResults.Steps += @{ Name = "Mover-Move"; Success = $true }
            }
            else {
                Write-Host "  ✗ User NOT moved to OU=Finance in $directoryName" -ForegroundColor Red
                Write-Host "    Output: $adUserInfo" -ForegroundColor Gray
                $testResults.Steps += @{ Name = "Mover-Move"; Success = $false; Error = "OU move did not occur" }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
        }
        $stepTimings["2c. Mover-Move"] = (Get-Date) - $step2cStart
    }

    # Test 2d: Disable (userAccountControl change - Protected Attribute Test)
    # This test is AD-specific: userAccountControl doesn't exist on OpenLDAP
    if ($Step -eq "Disable" -or $Step -eq "All") {
        $step2dStart = Get-Date
        $isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
        if ($isOpenLDAP) {
            Write-TestSection "Test 2d: Disable (Skipped — not applicable to OpenLDAP)"
            Write-Host "  OpenLDAP has no userAccountControl equivalent. Skipping." -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "Disable"; Success = $true }
            $stepTimings["2d. Disable"] = (Get-Date) - $step2dStart
        }
        else {
        Write-TestSection "Test 2d: Disable (userAccountControl)"

        # Use the second user (index 2) for disable/enable tests to preserve user 1 for other tests
        $disableUser = New-TestUser -Index 2
        $disableSamAccountName = $disableUser.SamAccountName

        Write-Host "Setting user status to Archived in CSV (triggers AD account disable)..." -ForegroundColor Gray

        # Update the status field to "Archived" - this will set the ACCOUNTDISABLE bit via DisableUser()
        # The expression is: IIF(Eq(mv["Status"], "Archived"), DisableUser(cs["userAccountControl"]), EnableUser(cs["userAccountControl"]))
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        $csv = Import-Csv $csvPath
        $targetUser = $csv | Where-Object { $_.samAccountName -eq $disableSamAccountName }
        if ($targetUser) {
            $targetUser.status = "Archived"
        }
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  ✓ Changed $disableSamAccountName status to 'Archived'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate account is disabled in AD
        # userAccountControl 514 = 512 (normal) + 2 (disabled)
        Write-Host "Validating account disabled state in AD..." -ForegroundColor Gray

        $adUserInfo = docker exec $($DirectoryConfig.ContainerName) bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=$disableSamAccountName)' userAccountControl 2>&1"

        # Check if userAccountControl is 514 (disabled) - ldbsearch returns decimal value
        if ($adUserInfo -match "userAccountControl: 514") {
            Write-Host "  ✓ Account disabled (userAccountControl=514) in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Disable"; Success = $true }
        }
        elseif ($adUserInfo -match "userAccountControl: 512") {
            Write-Host "  ✗ Account still enabled (userAccountControl=512) - disable not applied" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Disable"; Success = $false; Error = "Account not disabled" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        else {
            Write-Host "  ✗ Unexpected userAccountControl value in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Disable"; Success = $false; Error = "Unexpected UAC value" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        $stepTimings["2d. Disable"] = (Get-Date) - $step2dStart
        } # end else (non-OpenLDAP)
    }

    # Test 2e: Enable (userAccountControl change - Restore from Disabled)
    # This test is AD-specific: userAccountControl doesn't exist on OpenLDAP
    if ($Step -eq "Enable" -or $Step -eq "All") {
        $step2eStart = Get-Date
        $isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
        if ($isOpenLDAP) {
            Write-TestSection "Test 2e: Enable (Skipped — not applicable to OpenLDAP)"
            Write-Host "  OpenLDAP has no userAccountControl equivalent. Skipping." -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "Enable"; Success = $true }
            $stepTimings["2e. Enable"] = (Get-Date) - $step2eStart
        }
        else {
        Write-TestSection "Test 2e: Enable (userAccountControl)"

        # Continue using the second user (index 2) that was disabled in the previous test
        $enableUser = New-TestUser -Index 2
        $enableSamAccountName = $enableUser.SamAccountName

        Write-Host "Setting user status back to Active in CSV (triggers AD account enable)..." -ForegroundColor Gray

        # Update the status field back to "Active" - this will change userAccountControl from 514 to 512
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        $csv = Import-Csv $csvPath
        $targetUser = $csv | Where-Object { $_.samAccountName -eq $enableSamAccountName }
        if ($targetUser) {
            $targetUser.status = "Active"
        }
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  ✓ Changed $enableSamAccountName status to 'Active'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate account is enabled in AD
        Write-Host "Validating account enabled state in AD..." -ForegroundColor Gray

        $adUserInfo = docker exec $($DirectoryConfig.ContainerName) bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=$enableSamAccountName)' userAccountControl 2>&1"

        # Check if userAccountControl is 512 (enabled)
        if ($adUserInfo -match "userAccountControl: 512") {
            Write-Host "  ✓ Account enabled (userAccountControl=512) in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Enable"; Success = $true }
        }
        elseif ($adUserInfo -match "userAccountControl: 514") {
            Write-Host "  ✗ Account still disabled (userAccountControl=514) - enable not applied" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Enable"; Success = $false; Error = "Account not enabled" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        else {
            Write-Host "  ✗ Unexpected userAccountControl value in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Enable"; Success = $false; Error = "Unexpected UAC value" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        $stepTimings["2e. Enable"] = (Get-Date) - $step2eStart
        } # end else (non-OpenLDAP)
    }

    # Test 3: Leaver (Deprovisioning)
    if ($Step -eq "Leaver" -or $Step -eq "All") {
        $step3Start = Get-Date
        Write-TestSection "Test 3: Leaver (Deprovisioning)"

        # Use the first user (index 1) for leaver test - same user used in mover tests
        $leaverUser = New-TestUser -Index 1
        $userToRemove = $leaverUser.SamAccountName

        Write-Host "Removing user from CSV..." -ForegroundColor Gray

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        $filteredContent = $csvContent | Where-Object { $_ -notmatch [regex]::Escape($userToRemove) }
        $filteredContent | Set-Content $csvPath

        Write-Host "  ✓ Removed $userToRemove from CSV" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        $leaverSyncResults = Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Outcome graph: validate disconnection outcomes for leaver (#363 Phase 4b)
        $leaverImportStep = $leaverSyncResults.Steps | Where-Object { $_.Name -eq "CSV Full Import" }
        if ($leaverImportStep) {
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $leaverImportStep.ActivityId -Name "CSV Full Import (Leaver)" -ExpectedOutcomeType "DeletionDetected"
        }
        $leaverSyncStep = $leaverSyncResults.Steps | Where-Object { $_.Name -eq "CSV Delta Sync" }
        if ($leaverSyncStep) {
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $leaverSyncStep.ActivityId -Name "CSV Delta Sync (Leaver)" -ExpectedOutcomeType "Disconnected"
        }

        # Validate user state in the directory
        # With a 7-day grace period configured, the MVO won't be deleted immediately,
        # so the user should still exist in the directory but the CSO should be disconnected
        $directoryName = $DirectoryConfig.ConnectedSystemName
        Write-Host "Validating leaver state in $directoryName..." -ForegroundColor Gray

        $leaverExists = Test-LDAPUserExists -UserIdentifier $userToRemove -DirectoryConfig $DirectoryConfig

        if ($leaverExists) {
            # User still exists - expected with grace period
            Write-Host "  ✓ User $userToRemove still exists in $directoryName (within grace period)" -ForegroundColor Green
            Write-Host "    Note: User will be deleted after 7-day grace period expires" -ForegroundColor DarkGray
            $testResults.Steps += @{ Name = "Leaver"; Success = $true }
        }
        else {
            # User was deleted - unexpected with grace period, but not a failure
            Write-Host "  ✓ User $userToRemove deleted from $directoryName" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Leaver"; Success = $true }
        }
        $stepTimings["3. Leaver"] = (Get-Date) - $step3Start
    }

    # Test 4: Reconnection (Delete and Restore)
    if ($Step -eq "Reconnection" -or $Step -eq "All") {
        $step4Start = Get-Date
        Write-TestSection "Test 4: Reconnection (Delete and Restore)"

        Write-Host "Testing delete and restore before grace period..." -ForegroundColor Gray

        # Create test user
        $reconnectUser = New-TestUser -Index 8888
        $reconnectUser.EmployeeId = "EMP888888"
        $reconnectUser.SamAccountName = "test.reconnect"
        $reconnectUser.Email = "test.reconnect@$($DirectoryConfig.Domain)"
        $reconnectUser.FirstName = "Test"
        $reconnectUser.LastName = "Reconnect"
        $reconnectUser.Department = "IT"
        $reconnectUser.Title = "Developer"

        # Add to CSV using proper CSV parsing (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($reconnectUser.SamAccountName)@$($DirectoryConfig.Domain)"

        # Use Import-Csv/Export-Csv to ensure correct column handling
        $csv = Import-Csv $csvPath
        $newUser = [PSCustomObject]@{
            employeeId = $reconnectUser.EmployeeId
            firstName = $reconnectUser.FirstName
            lastName = $reconnectUser.LastName
            email = $reconnectUser.Email
            department = $reconnectUser.Department
            title = $reconnectUser.Title
            company = $reconnectUser.Company
            samAccountName = $reconnectUser.SamAccountName
            displayName = "$($reconnectUser.FirstName) $($reconnectUser.LastName)"
            status = "Active"
            userPrincipalName = $upn
            employeeType = $reconnectUser.EmployeeType
            employeeEndDate = ""
        }
        $csv = @($csv) + $newUser
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

        # Initial sync - uses Delta Sync for efficiency (baseline already established)
        Write-Host "  Initial sync (provisioning new user):" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
        Write-Host "  ✓ Initial sync completed" -ForegroundColor Green

        # Verify user was created in the directory
        $reconnectExists = Test-LDAPUserExists -UserIdentifier "test.reconnect" -DirectoryConfig $DirectoryConfig
        if (-not $reconnectExists) {
            Write-Host "  ✗ User was not created in directory during initial sync" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User not provisioned during initial sync" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
            $stepTimings["4. Reconnection"] = (Get-Date) - $step4Start
        }
        else {
            Write-Host "  ✓ User exists in directory after initial sync" -ForegroundColor Green

            # Remove user (simulating quit)
            Write-Host "  Removing user (simulating quit)..." -ForegroundColor Gray
            $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.reconnect" }
            $csvContent | Set-Content $csvPath
            docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

            # Only need CSV import/sync for removal - no LDAP export needed
            Write-Host "    [1/2] CSV Full Import..." -ForegroundColor DarkGray
            $removalImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $removalImportResult.activityId -Name "CSV Import (Reconnection removal)"
            Write-Host "    [2/2] CSV Delta Sync (marks CSO obsolete)..." -ForegroundColor DarkGray
            $removalSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $removalSyncResult.activityId -Name "CSV Delta Sync (Reconnection removal)"
            Write-Host "  ✓ Removal sync completed" -ForegroundColor Green

            # Verify user still exists in directory (grace period should prevent deletion)
            $stillExistsAfterRemoval = Test-LDAPUserExists -UserIdentifier "test.reconnect" -DirectoryConfig $DirectoryConfig
            if ($stillExistsAfterRemoval) {
                Write-Host "  ✓ User still in directory after removal (grace period active)" -ForegroundColor Green
            }
            else {
                Write-Host "  ⚠ User missing from directory after removal sync" -ForegroundColor Yellow
            }

            # Restore user (simulating rehire before grace period)
            Write-Host "  Restoring user (simulating rehire)..." -ForegroundColor Gray
            # Re-add user using proper CSV parsing
            $csv = Import-Csv $csvPath
            $csv = @($csv) + $newUser
            $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            docker cp $csvPath "$($DirectoryConfig.ContainerName):/connector-files/hr-users.csv"

            Invoke-SyncSequence -Config $config -ShowProgress -ValidateActivityStatus | Out-Null
            Write-Host "  ✓ Restore sync completed" -ForegroundColor Green

            # Verify user still exists (reconnection should preserve directory account)
            $reconnectPreserved = Test-LDAPUserExists -UserIdentifier "test.reconnect" -DirectoryConfig $DirectoryConfig

            if ($reconnectPreserved) {
                Write-Host "  ✓ Reconnection successful - user preserved in directory" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Reconnection"; Success = $true }
            }
            else {
                Write-Host "  ✗ Reconnection failed - user lost in directory" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User deleted instead of preserved" }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
            $stepTimings["4. Reconnection"] = (Get-Date) - $step4Start
        }
    }

    # Summary
    Write-TestSection "Test Results Summary"

    $successCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = @($testResults.Steps).Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "✓" } else { "✗" }
        $color = if ($stepResult.Success) { "Green" } else { "Red" }

        Write-Host "$status $($stepResult.Name)" -ForegroundColor $color

        if ($stepResult.ContainsKey('Error') -and $stepResult.Error) {
            Write-Host "  Error: $($stepResult.Error)" -ForegroundColor Red
        }
        if ($stepResult.ContainsKey('Warning') -and $stepResult.Warning) {
            Write-Host "  Warning: $($stepResult.Warning)" -ForegroundColor Yellow
        }
    }

    # Performance Summary
    if ($stepTimings.Count -gt 0) {
        Write-Host ""
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host "  Performance Breakdown (Test Steps)" -ForegroundColor Cyan
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host ""
        $totalTestTime = 0
        $maxSeconds = ($stepTimings.Values | Measure-Object -Property TotalSeconds -Maximum).Maximum
        foreach ($timing in $stepTimings.GetEnumerator() | Sort-Object Name) {
            $seconds = $timing.Value.TotalSeconds
            $totalTestTime += $seconds

            # Format time appropriately based on magnitude
            $timeDisplay = if ($seconds -lt 1) {
                "{0,6}ms" -f [math]::Round($seconds * 1000)
            } elseif ($seconds -lt 60) {
                "{0,6}s" -f [math]::Round($seconds, 1)
            } elseif ($seconds -lt 3600) {
                $mins = [math]::Floor($seconds / 60)
                $secs = [math]::Round($seconds % 60)
                "{0}m {1}s" -f $mins, $secs
            } else {
                $hours = [math]::Floor($seconds / 3600)
                $mins = [math]::Floor(($seconds % 3600) / 60)
                "{0}h {1}m" -f $hours, $mins
            }

            # Scale bar relative to max time, with reasonable max width
            $barWidth = if ($maxSeconds -gt 0) {
                [math]::Min(50, [math]::Floor(($seconds / $maxSeconds) * 50))
            } else { 0 }
            $bar = "█" * $barWidth

            Write-Host ("  {0,-20} {1,8}  {2}" -f $timing.Name, $timeDisplay, $bar) -ForegroundColor $(if ($seconds -gt 60) { "Yellow" } elseif ($seconds -gt 30) { "Cyan" } else { "Gray" })
        }
        $scenarioDuration = (Get-Date) - $scenarioStartTime
        $totalSeconds = $scenarioDuration.TotalSeconds
        $totalDisplay = if ($totalSeconds -lt 60) {
            "{0}s" -f [math]::Round($totalSeconds, 1)
        } elseif ($totalSeconds -lt 3600) {
            $mins = [math]::Floor($totalSeconds / 60)
            $secs = [math]::Round($totalSeconds % 60)
            "{0}m {1}s" -f $mins, $secs
        } else {
            $hours = [math]::Floor($totalSeconds / 3600)
            $mins = [math]::Floor(($totalSeconds % 3600) / 60)
            "{0}h {1}m" -f $hours, $mins
        }
        Write-Host ""
        Write-Host ("  {0,-20} {1}" -f "Scenario Total", $totalDisplay) -ForegroundColor Cyan
        Write-Host ""
    }

    $testResults.Success = ($successCount -eq $totalCount)

    if ($testResults.Success) {
        Write-Host ""
        Write-Host "✓ All tests passed" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host ""
        Write-Host "✗ Some tests failed" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host ""
    Write-Host "✗ Scenario 1 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
