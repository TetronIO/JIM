<#
.SYNOPSIS
    Test Scenario 4: MVO Deletion Rules - Comprehensive Coverage

.DESCRIPTION
    Validates ALL MVO deletion rule scenarios using a representative two-source topology:
      - HR CSV (primary source) -> MVO (User) -> LDAP (Samba AD)
      - Training CSV (secondary source) -> joins to same MVO (supplementary attributes)

    The Training system contributes non-identity-critical attributes (Training Status -> description
    in AD). These are safe to recall without breaking the AD user.
    The HR system contributes identity-critical attributes (sAMAccountName, Display Name, Department
    used in DN expression, etc.). HR disconnection triggers deprovisioning, not recall.

    Recall tests (Tests 1, 5) use the Training source to test end-to-end attribute recall:
    Training attributes are recalled from the MVO AND cleared from AD via LDAP export, with no
    adverse effect on the AD user's identity (DN, sAMAccountName remain intact).

    Deletion tests (Tests 3, 4) use the HR source as the authoritative deletion trigger with
    recall disabled (recall is irrelevant when the MVO is being deleted).

    IMPORTANT: In this topology, each MVO has up to THREE connectors (HR CSV CSO + Training CSV CSO
    + LDAP CSO). Removing a user from one source disconnects only that source's CSO. The other
    connectors remain joined.

    Test 1: WhenLastConnectorDisconnected + Recall (Training source, end-to-end)
        - Provision user via HR + Training, export Training attrs to LDAP
        - Remove training record, run Training import+sync (obsoletes Training CSO)
        - Assert: MVO still exists (HR CSO + LDAP CSO still joined)
        - Assert: Training-contributed attributes recalled from MVO
        - Assert: HR-contributed attributes retained on MVO
        - Assert: Pending exports created on LDAP to clear Training attrs
        - Assert: LDAP export succeeds, AD user functional, Training attrs cleared from AD

    Test 2: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
        - Remove user from HR CSV, run CSV import+sync only
        - Assert: MVO still exists (LDAP CSO still joined)
        - Assert: Attributes remain on MVO (RemoveContributedAttributesOnObsoletion=false)
        - Assert: No pending exports on LDAP (nothing changed on MVO)

    Test 3: WhenAuthoritativeSourceDisconnected + GracePeriod=0 + immediate deletion
        - Configure CSV as authoritative source, recall=false
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO is deleted immediately (authoritative source disconnected, 0 grace period)
        - Assert: LDAP target is deprovisioned (pending export created for delete)

    Test 4: WhenAuthoritativeSourceDisconnected + GracePeriod=1 minute + deferred deletion
        - Configure CSV as authoritative source with 1-minute grace period, recall=false
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO exists but is marked for deletion (grace period not elapsed)
        - Wait for housekeeping to process (grace period expires)
        - Assert: MVO is deleted after grace period elapses

    Test 5: Manual + Recall (Training source, end-to-end)
        - Same as Test 1 but with Manual deletion rule
        - Assert: MVO still exists (Manual rule never auto-deletes)
        - Assert: Training-contributed attributes recalled and cleared from AD
        - Assert: HR-contributed attributes retained, AD user functional
        - Assert: isPendingDeletion=false

    Test 6: Manual + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO still exists (Manual rule never auto-deletes)
        - Assert: Attributes remain on MVO (RemoveContributedAttributesOnObsoletion=false)
        - Assert: No pending exports on LDAP (nothing changed on MVO)

    Test 7: Internal MVO Protection
        - Internal MVOs (Origin=Internal) must NEVER be auto-deleted regardless of deletion rule
        - Deferred: requires Internal MVO management feature (see GitHub issue)

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
    ./Invoke-Scenario4-DeletionRules.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario4-DeletionRules.ps1 -Step AuthoritativeImmediate -Template Nano -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
        "WhenLastConnectorRecall",
        "WhenLastConnectorNoRecall",
        "AuthoritativeImmediate",
        "AuthoritativeGracePeriod",
        "ManualRecall",
        "ManualNoRecall",
        "InternalProtection",
        "All"
    )]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

Write-TestSection "Scenario 4: MVO Deletion Rules - Comprehensive Coverage"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "MVO Deletion Rules - Comprehensive Coverage"
    Template = $Template
    StartTime = (Get-Date).ToString("o")
    Steps = @()
    Success = $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Add a user to the CSV, copy to container, and run import+sync+export+confirm cycle
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ProvisionUser {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$EmployeeId,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$DisplayName,

        [Parameter(Mandatory=$true)]
        [string]$TestName
    )

    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
    $upn = "$SamAccountName@subatomic.local"

    # Add user to CSV
    $csv = Import-Csv $csvPath
    $newUser = [PSCustomObject]@{
        employeeId      = $EmployeeId
        firstName        = $DisplayName.Split(' ')[0]
        lastName         = $DisplayName.Split(' ')[-1]
        email            = "$SamAccountName@subatomic.local"
        department       = "Information Technology"
        title            = "Engineer"
        company          = "Subatomic"
        samAccountName   = $SamAccountName
        displayName      = $DisplayName
        status           = "Active"
        userPrincipalName = $upn
        employeeType     = "Employee"
        employeeEndDate  = ""
    }
    $csv = @($csv) + $newUser
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv | Out-Null
    Write-Host "  Added $SamAccountName to CSV" -ForegroundColor Gray

    # Import + Sync + Export + Confirm
    Write-Host "  Running import+sync+export cycle ($TestName)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName provision)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync ($TestName provision)"

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName provision)"

    # Confirm export with LDAP import
    $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import ($TestName confirm)"
    Start-Sleep -Seconds 2

    # Verify user exists in AD
    docker exec samba-ad-primary samba-tool user show $SamAccountName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "User $SamAccountName was not provisioned to AD during $TestName"
    }
    Write-Host "  User $SamAccountName provisioned to AD" -ForegroundColor Green

    # Return the MVO for the user
    # Note: Get-JIMMetaverseObject outputs objects directly to the pipeline (not wrapped in .items)
    $mvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search $DisplayName -PageSize 10 -ErrorAction SilentlyContinue)
    if ($mvos.Count -gt 0) {
        $mvo = $mvos | Where-Object { $_.displayName -eq $DisplayName } | Select-Object -First 1
        if ($mvo) {
            Write-Host "  MVO found: $($mvo.id)" -ForegroundColor Gray
            return $mvo
        }
    }

    throw "MVO not found for $DisplayName after provisioning"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Remove a user from the CSV and run the appropriate sync cycle
# -----------------------------------------------------------------------------------------------------------------
function Invoke-RemoveUserFromSource {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$TestName,

        # When set, runs the full 5-step sync sequence (CSV Import -> CSV Sync -> LDAP Export
        # -> LDAP Import -> LDAP Sync). This is required for deletion tests because the MVO
        # has TWO connectors (CSV + LDAP). Removing from CSV only disconnects the CSV CSO.
        # The LDAP export must deprovision the AD user, then LDAP import+sync must disconnect
        # the LDAP CSO, so the MVO reaches zero connectors and becomes eligible for deletion.
        [switch]$FullCycle
    )

    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

    # Remove user from CSV using proper CSV parsing to avoid partial matches
    $csv = Import-Csv $csvPath
    $csv = @($csv | Where-Object { $_.samAccountName -ne $SamAccountName })
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv | Out-Null
    Write-Host "  Removed $SamAccountName from CSV" -ForegroundColor Gray

    if ($FullCycle) {
        # Full 5-step sync sequence: CSV Import -> CSV Sync -> LDAP Export -> LDAP Import -> LDAP Sync
        Write-Host "  Running full sync cycle ($TestName removal)..." -ForegroundColor Gray

        # Step 1: CSV Import - detects user removed from source
        $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName removal)"

        # Step 2: CSV Sync - disconnects CSV CSO, creates delete pending exports for LDAP
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync ($TestName removal)"

        # Step 3: LDAP Export - deprovisions user from AD
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName removal)"

        # Wait for AD replication
        Start-Sleep -Seconds 5

        # Step 4: LDAP Delta Import - confirms user deleted from AD
        $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Delta Import ($TestName removal)"

        # Step 5: LDAP Delta Sync - disconnects LDAP CSO from MVO (MVO now has zero connectors)
        $ldapSyncResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $ldapSyncResult.activityId -Name "LDAP Delta Sync ($TestName removal)"
    }
    else {
        # CSV-only cycle: Import + Sync (disconnects CSV CSO only, LDAP CSO remains)
        Write-Host "  Running CSV import+sync cycle ($TestName removal)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName removal)"

        $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync ($TestName removal)"
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Provision training data for a user and export to LDAP
# Adds a training record to the Training CSV, runs Training import+sync, then LDAP export
# to push supplementary Training attributes (description) to AD.
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ProvisionTrainingData {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$EmployeeId,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$TestName
    )

    $trainingCsvPath = "$PSScriptRoot/../../test-data/training-records.csv"

    # Add training record to CSV
    $csv = Import-Csv $trainingCsvPath
    $newRecord = [PSCustomObject]@{
        employeeId            = $EmployeeId
        samAccountName        = $SamAccountName
        coursesCompleted      = "SEC101|COMP101"
        trainingStatus        = "Pass"
        completionDate        = "2025-01-15T10:00:00Z"
        totalCoursesCompleted = "2"
    }
    $csv = @($csv) + $newRecord
    $csv | Export-Csv -Path $trainingCsvPath -NoTypeInformation -Encoding UTF8
    docker cp $trainingCsvPath samba-ad-primary:/connector-files/training-records.csv | Out-Null
    Write-Host "  Added training record for $SamAccountName to Training CSV" -ForegroundColor Gray

    # Training Import + Sync (joins Training CSO to existing MVO)
    Write-Host "  Running Training import+sync ($TestName)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Training Import ($TestName)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Training Sync ($TestName)"

    # LDAP Export to push Training attributes (description) to AD
    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName training)"

    # Confirming import: updates the LDAP CSO attribute cache with exported Training values.
    # Without this, the no-net-change detection during recall would see the CSO as having no
    # 'description' attribute, causing the null-clearing recall export to be incorrectly skipped.
    $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import ($TestName training confirm)"

    Start-Sleep -Seconds 2

    # Verify Training attributes reached AD
    $adOutput = & docker exec samba-ad-primary bash -c "samba-tool user show '$SamAccountName' 2>&1"
    if ($adOutput -match "description:\s*(.+)") {
        Write-Host "  Training attributes exported to AD (description: $($Matches[1]))" -ForegroundColor Green
    } else {
        Write-Host "  WARNING: Training attribute 'description' not found on AD user" -ForegroundColor Yellow
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Remove training data for a user and run Training import+sync to obsolete the Training CSO
# This triggers attribute recall if RemoveContributedAttributesOnObsoletion=true on the Training object type.
# -----------------------------------------------------------------------------------------------------------------
function Invoke-RemoveTrainingData {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$EmployeeId,

        [Parameter(Mandatory=$true)]
        [string]$TestName
    )

    $trainingCsvPath = "$PSScriptRoot/../../test-data/training-records.csv"

    # Remove training record from CSV by employeeId
    $csv = Import-Csv $trainingCsvPath
    $csv = @($csv | Where-Object { $_.employeeId -ne $EmployeeId })
    $csv | Export-Csv -Path $trainingCsvPath -NoTypeInformation -Encoding UTF8
    docker cp $trainingCsvPath samba-ad-primary:/connector-files/training-records.csv | Out-Null
    Write-Host "  Removed training record for $EmployeeId from Training CSV" -ForegroundColor Gray

    # Training Import + Sync (obsoletes Training CSO, triggers recall if configured)
    Write-Host "  Running Training import+sync ($TestName removal)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Training Import ($TestName removal)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Training Sync ($TestName removal)"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Check if an MVO still exists (by display name search)
# -----------------------------------------------------------------------------------------------------------------
function Test-MvoExists {
    param(
        [Parameter(Mandatory=$true)]
        [string]$DisplayName,

        [Parameter(Mandatory=$true)]
        [string]$ObjectTypeName
    )

    # Note: Get-JIMMetaverseObject outputs objects directly to the pipeline (not wrapped in .items)
    $mvos = @(Get-JIMMetaverseObject -ObjectTypeName $ObjectTypeName -Search $DisplayName -PageSize 10 -ErrorAction SilentlyContinue)
    if ($mvos.Count -gt 0) {
        $mvo = $mvos | Where-Object { $_.displayName -eq $DisplayName }
        if ($mvo) {
            return $true
        }
    }
    return $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Check if an MVO still exists (by ID - used when display name may have been recalled)
# -----------------------------------------------------------------------------------------------------------------
function Test-MvoExistsById {
    param(
        [Parameter(Mandatory=$true)]
        [string]$MvoId
    )

    try {
        $mvo = Get-JIMMetaverseObject -Id $MvoId -ErrorAction SilentlyContinue
        if ($mvo) {
            return $true
        }
        return $false
    }
    catch {
        # API throws terminating errors for 404 Not Found — treat as "does not exist"
        return $false
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Get pending export count for a connected system
# -----------------------------------------------------------------------------------------------------------------
function Get-PendingExportCount {
    param(
        [Parameter(Mandatory=$true)]
        [int]$ConnectedSystemId
    )

    $cs = Get-JIMConnectedSystem -Id $ConnectedSystemId
    if ($cs -and $cs.PSObject.Properties.Name -contains 'pendingExportCount') {
        return [int]$cs.pendingExportCount
    }
    return 0
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Drain any stale pending exports from prior tests to prevent cascade failures
# -----------------------------------------------------------------------------------------------------------------
function Invoke-DrainPendingExports {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    $stalePending = Get-PendingExportCount -ConnectedSystemId $Config.LDAPSystemId
    if ($stalePending -gt 0) {
        Write-Host "  Draining $stalePending stale pending export(s) from prior test..." -ForegroundColor Gray
        $drainExport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Configure deletion rules on the MVO object type and optionally the CSO type
# -RecallConnectedSystemId: Which connected system's object type gets RemoveContributedAttributesOnObsoletion.
#   Defaults to CSV system if not specified (backwards compatible with existing tests).
# -----------------------------------------------------------------------------------------------------------------
function Set-DeletionRuleConfig {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$ObjectTypeId,

        [Parameter(Mandatory=$true)]
        [string]$DeletionRule,

        [Parameter(Mandatory=$false)]
        [TimeSpan]$GracePeriod = [TimeSpan]::Zero,

        [Parameter(Mandatory=$false)]
        [string]$DeletionTriggerConnectedSystemIds,

        [Parameter(Mandatory=$false)]
        [Nullable[bool]]$RemoveContributedAttributesOnObsoletion,

        [Parameter(Mandatory=$false)]
        [int]$RecallConnectedSystemId = 0
    )

    # Set MVO type deletion rule
    $setParams = @{
        Id = $ObjectTypeId
        DeletionRule = $DeletionRule
        DeletionGracePeriod = $GracePeriod
    }
    if ($DeletionTriggerConnectedSystemIds) {
        $setParams.DeletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
    }
    Set-JIMMetaverseObjectType @setParams

    Write-Host "  Configured MVO type: DeletionRule=$DeletionRule, GracePeriod=$GracePeriod" -ForegroundColor Green

    # Set RemoveContributedAttributesOnObsoletion on the specified connected system's object type
    if ($null -ne $RemoveContributedAttributesOnObsoletion) {
        $targetSystemId = if ($RecallConnectedSystemId -gt 0) { $RecallConnectedSystemId } else { $Config.CSVSystemId }
        $targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystemId -ObjectTypes
        $targetObjType = $targetObjectTypes | Where-Object { $_.name -match "^(user|person|record|trainingRecord)$" } | Select-Object -First 1
        if ($targetObjType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystemId -ObjectTypeId $targetObjType.id `
                -RemoveContributedAttributesOnObsoletion $RemoveContributedAttributesOnObsoletion
            $systemLabel = if ($targetSystemId -eq $Config.TrainingSystemId) { "Training" } else { "CSV" }
            Write-Host "  Configured $systemLabel object type: RemoveContributedAttributesOnObsoletion=$RemoveContributedAttributesOnObsoletion" -ForegroundColor Green
        }
        else {
            Write-Host "  WARNING: Could not find object type on system $targetSystemId to set RemoveContributedAttributesOnObsoletion" -ForegroundColor Yellow
        }
    }
}

try {
    # -----------------------------------------------------------------------------------------------------------------
    # Step 0: Setup JIM Configuration
    # -----------------------------------------------------------------------------------------------------------------
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Use dedicated minimal CSV for Scenario 4 (no baseline users initially)
    Write-Host "Setting up dedicated CSV for Scenario 4 tests..." -ForegroundColor Gray
    $testDataPath = "$PSScriptRoot/../../test-data"
    $scenarioDataPath = "$PSScriptRoot/data"

    if (-not (Test-Path $testDataPath)) {
        New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null
    }

    # Copy scenario-specific CSVs as the starting point
    Copy-Item -Path "$scenarioDataPath/scenario4-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force
    Copy-Item -Path "$scenarioDataPath/scenario4-training-records.csv" -Destination "$testDataPath/training-records.csv" -Force

    # Copy to container volume
    $csvPath = "$testDataPath/hr-users.csv"
    $trainingCsvPath = "$testDataPath/training-records.csv"
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
    docker cp $trainingCsvPath samba-ad-primary:/connector-files/training-records.csv
    Write-Host "  CSVs initialised (HR + Training, 1 baseline user each)" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @(
        "test.wlcd.recall", "test.wlcd.norecall",
        "test.auth.immediate", "test.auth.grace",
        "test.manual.recall", "test.manual.norecall",
        "baseline.user1"
    )
    $deletedCount = 0
    foreach ($user in $testUsers) {
        $output = & docker exec samba-ad-primary bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
        if ($output -match "Deleted user") {
            Write-Host "  Deleted $user from AD" -ForegroundColor Gray
            $deletedCount++
        }
    }
    Write-Host "  AD cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    Write-Host "JIM configured for Scenario 4" -ForegroundColor Green

    # Create department OUs needed for test users
    Write-Host "Creating department OUs for test users..." -ForegroundColor Gray
    $testDepartments = @("Information Technology", "Operations")
    foreach ($dept in $testDepartments) {
        docker exec samba-ad-primary samba-tool ou create "OU=$dept,OU=Users,OU=Corp,DC=subatomic,DC=local" 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Created OU: $dept" -ForegroundColor Gray
        }
    }
    Write-Host "  Department OUs ready" -ForegroundColor Green

    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  Training System ID: $($config.TrainingSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get the User object type (needed for all tests)
    $userObjectType = Get-JIMMetaverseObjectType -Name "User"
    if (-not $userObjectType) {
        throw "User object type not found - cannot configure deletion rules"
    }

    # Run initial imports to establish baseline CSOs for both HR and Training
    Write-Host "Running initial imports to establish baseline..." -ForegroundColor Gray
    $initImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initImport.activityId -Name "CSV Import (baseline)"
    $initSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initSync.activityId -Name "Full Sync (baseline)"

    $initTrainingImport = Start-JIMRunProfile -ConnectedSystemId $config.TrainingSystemId -RunProfileId $config.TrainingImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initTrainingImport.activityId -Name "Training Import (baseline)"
    $initTrainingSync = Start-JIMRunProfile -ConnectedSystemId $config.TrainingSystemId -RunProfileId $config.TrainingSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initTrainingSync.activityId -Name "Training Sync (baseline)"

    # =============================================================================================================
    # Test 1: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
    # =============================================================================================================
    # End-to-end recall test using a SECONDARY source (Training Records).
    # The Training system contributes supplementary attributes (Training Status -> description)
    # that are exported to LDAP but are NOT identity-critical. When Training CSO is obsoleted,
    # these supplementary attributes are recalled from the MVO and cleared from AD, with no
    # adverse effect on the AD user (DN, sAMAccountName, etc. intact).
    #
    # Topology: HR CSV (primary) + Training CSV (secondary) -> MVO -> LDAP
    # Each MVO has 3 connectors: HR CSV CSO + Training CSV CSO + LDAP CSO
    # Removing Training data obsoletes the Training CSO only. HR + LDAP CSOs remain.
    # =============================================================================================================
    if ($Step -eq "WhenLastConnectorRecall" -or $Step -eq "All") {
        Write-TestSection "Test 1: WhenLastConnectorDisconnected + Recall Attributes (Training Source)"
        Write-Host "DeletionRule: WhenLastConnectorDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true (on Training object type)" -ForegroundColor Gray
        Write-Host "Expected: MVO remains, Training attributes recalled from MVO and cleared from AD" -ForegroundColor Gray
        Write-Host "Expected: HR attributes and AD identity (DN, sAMAccountName) remain intact" -ForegroundColor Gray
        Write-Host ""

        # Configure deletion rules - recall enabled on Training system's object type
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $true `
            -RecallConnectedSystemId $config.TrainingSystemId

        # Provision a test user via HR CSV (creates MVO + LDAP CSO)
        $test1Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "WLCD001" `
            -SamAccountName "test.wlcd.recall" `
            -DisplayName "Test WLCD Recall" `
            -TestName "Test1"

        $test1MvoId = $test1Mvo.id
        Write-Host "  MVO ID: $test1MvoId" -ForegroundColor Gray

        # Provision training data (creates Training CSO joined to same MVO, exports to LDAP)
        Invoke-ProvisionTrainingData -Config $config `
            -EmployeeId "WLCD001" `
            -SamAccountName "test.wlcd.recall" `
            -TestName "Test1"

        # Record pending export count before recall
        Invoke-DrainPendingExports -Config $config
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before recall: $pendingExportsBefore" -ForegroundColor Gray

        # Remove training data - Training import+sync only (obsoletes Training CSO, triggers recall)
        Invoke-RemoveTrainingData -Config $config -EmployeeId "WLCD001" -TestName "Test1"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (HR CSO + LDAP CSO still joined - not the last connector)
        $mvoStillExists = Test-MvoExists -DisplayName "Test WLCD Recall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "MVO deleted when HR + LDAP CSOs still joined" }
            throw "Test 1 Assert 1 failed: MVO deleted when HR + LDAP CSOs still joined"
        }
        Write-Host "  PASSED: MVO still exists (HR CSO + LDAP CSO still joined)" -ForegroundColor Green

        # Assert 2: Training-contributed attributes were recalled from MVO
        $mvoDetail = Get-JIMMetaverseObject -Id $test1MvoId -ErrorAction SilentlyContinue

        if ($mvoDetail) {
            $trainingStatusValue = $null
            if ($mvoDetail.attributeValues) {
                $trainingStatusAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Training Status' } | Select-Object -First 1
                if ($trainingStatusAttr) {
                    $trainingStatusValue = $trainingStatusAttr.stringValue
                }
            }
            if (-not $trainingStatusValue) {
                Write-Host "  PASSED: Training-contributed attribute 'Training Status' has been recalled (empty/null)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "Training attribute 'Training Status' still has value: $trainingStatusValue" }
                throw "Test 1 Assert 2 failed: Training attribute 'Training Status' still has value: $trainingStatusValue"
            }
        }

        # Assert 3: HR-contributed attributes are retained (Display Name, Department still present)
        if ($mvoDetail) {
            $deptValue = $null
            if ($mvoDetail.attributeValues) {
                $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
                if ($deptAttr) {
                    $deptValue = $deptAttr.stringValue
                }
            }
            if ($deptValue) {
                Write-Host "  PASSED: HR-contributed attribute 'Department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "HR-contributed attribute 'Department' was incorrectly recalled" }
                throw "Test 1 Assert 3 failed: HR-contributed attribute 'Department' was incorrectly recalled"
            }
        }

        # Assert 4: Pending exports created on LDAP to clear Training attributes
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after recall: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -gt $pendingExportsBefore) {
            Write-Host "  PASSED: Pending exports created on LDAP to clear recalled Training attributes" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "No pending exports created on LDAP after Training attribute recall" }
            throw "Test 1 Assert 4 failed: Expected pending exports on LDAP to clear Training attributes (description)"
        }

        # Assert 5: Run LDAP Export and verify AD user is still functional with Training attrs cleared
        Write-Host "  Running LDAP export to apply recall exports..." -ForegroundColor Gray
        $recallExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $recallExport.activityId -Name "LDAP Export (Test1 recall)"

        Start-Sleep -Seconds 3

        # Verify AD user still exists and identity is intact
        $adOutput = & docker exec samba-ad-primary bash -c "samba-tool user show 'test.wlcd.recall' 2>&1"
        if ($adOutput -match "sAMAccountName:\s*test\.wlcd\.recall") {
            Write-Host "  PASSED: AD user still exists with sAMAccountName intact" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "AD user not found or sAMAccountName missing after recall export" }
            throw "Test 1 Assert 5 failed: AD user not found or sAMAccountName missing after recall export"
        }

        # Verify Training attributes cleared from AD (description should be absent/empty)
        if ($adOutput -match "description:\s*(.+)") {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "AD 'description' still has value after recall: $($Matches[1])" }
            throw "Test 1 Assert 5 failed: AD 'description' attribute still has value after recall export: $($Matches[1])"
        } else {
            Write-Host "  PASSED: Training attribute 'description' cleared from AD after recall export" -ForegroundColor Green
        }

        $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 2: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
    # =============================================================================================================
    # Same topology as Test 1, but with RemoveContributedAttributesOnObsoletion=false.
    # MVO should remain, attributes should stay, no pending exports.
    # =============================================================================================================
    if ($Step -eq "WhenLastConnectorNoRecall" -or $Step -eq "All") {
        Write-TestSection "Test 2: WhenLastConnectorDisconnected + No Attribute Recall"
        Write-Host "DeletionRule: WhenLastConnectorDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (LDAP CSO still joined), attributes stay, no pending exports" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $false

        # Record pending export count before test
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before: $pendingExportsBefore" -ForegroundColor Gray

        # Provision a test user
        $test2Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "WLCD002" `
            -SamAccountName "test.wlcd.norecall" `
            -DisplayName "Test WLCD NoRecall" `
            -TestName "Test2"

        $test2MvoId = $test2Mvo.id
        Write-Host "  MVO ID: $test2MvoId" -ForegroundColor Gray

        # Drain any pending exports from provisioning before testing
        $provisionExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after drain: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only (NOT full cycle)
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.wlcd.norecall" -TestName "Test2"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists
        $mvoStillExists = Test-MvoExists -DisplayName "Test WLCD NoRecall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            Write-Host "  FAILED: MVO was deleted despite LDAP CSO still being joined" -ForegroundColor Red
            $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "MVO deleted when LDAP CSO still joined" }
            throw "Test 2 Assert 1 failed: MVO deleted when LDAP CSO still joined"
        }
        Write-Host "  PASSED: MVO still exists (LDAP CSO still joined, not the last connector)" -ForegroundColor Green

        # Assert 2: Attributes should remain on MVO (not recalled)
        $mvoDetail = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test WLCD NoRecall" -Attributes Department -PageSize 10 -ErrorAction SilentlyContinue) |
            Where-Object { $_.displayName -eq "Test WLCD NoRecall" } | Select-Object -First 1

        if ($mvoDetail) {
            # The API returns requested attributes in the 'attributes' dictionary property
            $deptValue = $null
            if ($mvoDetail.attributes -and $mvoDetail.attributes.PSObject.Properties.Name -contains 'Department') {
                $deptValue = $mvoDetail.attributes.Department
            }
            if ($deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "department was removed despite RemoveContributedAttributesOnObsoletion=false" }
                throw "Test 2 Assert 2 failed: CSV-contributed attribute 'department' was removed despite RemoveContributedAttributesOnObsoletion=false"
            }
        }

        # Assert 3: No new pending exports (nothing changed on MVO)
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -le $pendingExportsBefore) {
            Write-Host "  PASSED: No new pending exports on LDAP target (attributes unchanged)" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "Unexpected pending exports created on LDAP target" }
            throw "Test 2 Assert 3 failed: Unexpected pending exports created on LDAP target"
        }

        $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 3: WhenAuthoritativeSourceDisconnected + GracePeriod=0 (Immediate Deletion)
    # =============================================================================================================
    # Configure CSV as the authoritative source. When the CSV CSO disconnects (user removed from
    # source), the MVO should be deleted immediately (0 grace period) even though the LDAP CSO
    # still exists. This is the correct rule for Source->Target topologies.
    #
    # RemoveContributedAttributesOnObsoletion=false: Recall is irrelevant when the MVO is being
    # immediately deleted. The MVO and all its attributes are removed entirely — there is no
    # persisted state for recall to operate on. Setting recall=false avoids the broken state
    # where identity-critical attributes (DN, sAMAccountName) are cleared before the
    # deprovisioning export runs.
    # =============================================================================================================
    if ($Step -eq "AuthoritativeImmediate" -or $Step -eq "All") {
        Write-TestSection "Test 3: WhenAuthoritativeSourceDisconnected + Immediate Deletion"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "Authoritative source: CSV (HR System)" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false (recall irrelevant for immediate deletion)" -ForegroundColor Gray
        Write-Host "Expected: MVO deleted immediately when CSV CSO disconnects, LDAP deprovisioned" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - CSV is the authoritative source, recall disabled
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -DeletionTriggerConnectedSystemIds "$($config.CSVSystemId)" `
            -RemoveContributedAttributesOnObsoletion $false

        # Provision a test user (creates both CSV CSO and LDAP CSO via export)
        $test3Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "AUTH001" `
            -SamAccountName "test.auth.immediate" `
            -DisplayName "Test Auth Immediate" `
            -TestName "Test3"

        $test3MvoId = $test3Mvo.id
        Write-Host "  MVO ID before deletion: $test3MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync ONLY (not full cycle)
        # This disconnects the CSV CSO (authoritative source). The LDAP CSO remains.
        # With WhenAuthoritativeSourceDisconnected, the MVO should be deleted immediately.
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.auth.immediate" -TestName "Test3"

        Start-Sleep -Seconds 2

        # Assert 1: MVO should be deleted immediately (authoritative source disconnected)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Auth Immediate" -ObjectTypeName "User"

        if ($mvoStillExists) {
            $testResults.Steps += @{ Name = "AuthoritativeImmediate"; Success = $false; Error = "MVO not deleted when authoritative source disconnected" }
            throw "Test 3 Assert 1 failed: MVO still exists after authoritative source disconnected (expected immediate deletion)"
        } else {
            Write-Host "  PASSED: MVO deleted when authoritative source disconnected" -ForegroundColor Green
            Write-Host "  LDAP connector was still present but deletion triggered by authoritative CSV disconnect" -ForegroundColor Gray

            # Assert 2: Deleted MVO should appear in the Deleted Objects view
            Write-Host "  Verifying deleted MVO appears in Deleted Objects view..." -ForegroundColor Gray
            $deletedMvos = Get-JIMDeletedObject -ObjectType MVO -Search "Test Auth Immediate" -PageSize 10
            if ($deletedMvos -and $deletedMvos.items) {
                $deletedEntry = $deletedMvos.items | Where-Object { $_.displayName -eq "Test Auth Immediate" } | Select-Object -First 1
                if ($deletedEntry) {
                    Write-Host "  PASSED: Deleted MVO found in Deleted Objects view (ID: $($deletedEntry.id))" -ForegroundColor Green
                } else {
                    Write-Host "  WARNING: Deleted MVO not found in Deleted Objects search results" -ForegroundColor Yellow
                }
            } else {
                Write-Host "  WARNING: No deleted MVOs returned from API" -ForegroundColor Yellow
            }

            # Assert 3: Run LDAP export to verify deprovisioning pending export was created
            Write-Host "  Running LDAP export to deprovision orphaned AD user..." -ForegroundColor Gray
            $cleanupExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $cleanupExport.activityId -Name "LDAP Export (Test3 deprovisioning)"

            # Verify user is removed from AD
            Start-Sleep -Seconds 3
            $adUserExists = & docker exec samba-ad-primary bash -c "samba-tool user show 'test.auth.immediate' 2>&1; echo EXIT_CODE:\$?"
            if ($adUserExists -match "Unable to find" -or $adUserExists -match "ERROR") {
                Write-Host "  PASSED: User deprovisioned from AD (no longer exists in directory)" -ForegroundColor Green
            } else {
                Write-Host "  WARNING: User may still exist in AD after export" -ForegroundColor Yellow
            }

            $testResults.Steps += @{ Name = "AuthoritativeImmediate"; Success = $true }
        }
    }

    # =============================================================================================================
    # Test 4: WhenAuthoritativeSourceDisconnected + GracePeriod=1 minute (Deferred Deletion)
    # =============================================================================================================
    # Same as Test 3 but with a 1-minute grace period. The MVO should be marked for deletion
    # but not deleted until the grace period elapses and housekeeping runs.
    #
    # RemoveContributedAttributesOnObsoletion=false: Same rationale as Test 3 — the MVO is
    # being deleted (just deferred). Recall would clear identity-critical attributes during
    # the grace period, leaving LDAP exports in a broken state. Real-world authoritative
    # source disconnection should trigger deprovisioning, not recall.
    # =============================================================================================================
    if ($Step -eq "AuthoritativeGracePeriod" -or $Step -eq "All") {
        Write-TestSection "Test 4: WhenAuthoritativeSourceDisconnected + 1-Minute Grace Period"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, GracePeriod: 1 minute" -ForegroundColor Gray
        Write-Host "Authoritative source: CSV (HR System)" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false (recall irrelevant for deletion)" -ForegroundColor Gray
        Write-Host "Expected: MVO marked for deletion, then deleted after 1-minute grace period" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - CSV is the authoritative source, 1-minute grace period, recall disabled
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::FromMinutes(1)) `
            -DeletionTriggerConnectedSystemIds "$($config.CSVSystemId)" `
            -RemoveContributedAttributesOnObsoletion $false

        # Provision a test user
        $test4Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "AUTH002" `
            -SamAccountName "test.auth.grace" `
            -DisplayName "Test Auth Grace" `
            -TestName "Test4"

        $test4MvoId = $test4Mvo.id
        Write-Host "  MVO ID: $test4MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.auth.grace" -TestName "Test4"

        Start-Sleep -Seconds 3

        # Assert 1: MVO should still exist (grace period not yet elapsed)
        # With recall=false, display name is retained so we can search by name
        $mvoStillExists = Test-MvoExists -DisplayName "Test Auth Grace" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO deleted immediately despite grace period" }
            throw "Test 4 Assert 1 failed: MVO was deleted immediately despite 1-minute grace period"
        }
        Write-Host "  PASSED: MVO still exists (grace period not yet elapsed)" -ForegroundColor Green

        # Verify MVO is marked for pending deletion
        $mvoDetail = Get-JIMMetaverseObject -Id $test4MvoId -ErrorAction SilentlyContinue

        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if ($mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=true (correctly marked for deferred deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO isPendingDeletion=false (should be marked for deletion)" }
                throw "Test 4 Assert 1b failed: MVO isPendingDeletion=false (should be marked for deletion)"
            }
        }

        # Wait for the grace period to elapse + housekeeping cycle to run.
        # Grace period = 60s after disconnect. Housekeeping runs every 60s when worker is idle.
        # Worst case: 60s grace + 60s housekeeping cycle = 120s. Add 30s buffer = 150s.
        Write-Host "  Waiting for 1-minute grace period + housekeeping cycle..." -ForegroundColor Gray
        Write-Host "  (Housekeeping runs every 60s when idle, deletes MVOs past grace period)" -ForegroundColor Gray
        $waitTime = 150  # 1 minute grace + 60 seconds housekeeping cycle + 30 seconds buffer
        for ($i = 0; $i -lt $waitTime; $i += 10) {
            Start-Sleep -Seconds 10
            $remaining = $waitTime - $i - 10
            if ($remaining -gt 0) {
                Write-Host "  Waiting... ($remaining seconds remaining)" -ForegroundColor Gray
            }
        }

        # Assert 2: MVO should now be deleted (grace period elapsed, housekeeping ran)
        $mvoDeletedAfterGrace = -not (Test-MvoExistsById -MvoId $test4MvoId)

        if (-not $mvoDeletedAfterGrace) {
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO not deleted after grace period elapsed" }
            throw "Test 4 Assert 2 failed: MVO still exists after grace period should have elapsed"
        }
        Write-Host "  PASSED: MVO deleted after grace period elapsed (housekeeping processed it)" -ForegroundColor Green

        # Verify it appears in deleted objects
        $deletedMvos = Get-JIMDeletedObject -ObjectType MVO -Search "Test Auth Grace" -PageSize 10
        if ($deletedMvos -and $deletedMvos.items) {
            $deletedEntry = $deletedMvos.items | Where-Object { $_.displayName -eq "Test Auth Grace" } | Select-Object -First 1
            if ($deletedEntry) {
                Write-Host "  PASSED: Deleted MVO found in Deleted Objects view" -ForegroundColor Green
            }
        }

        $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $true }
    }

    # =============================================================================================================
    # Test 5: Manual + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
    # =============================================================================================================
    # End-to-end recall test with Manual deletion rule using SECONDARY source (Training Records).
    # Manual rule means MVOs are NEVER automatically deleted. But when
    # RemoveContributedAttributesOnObsoletion=true on the Training object type, supplementary
    # Training attributes should be recalled from the MVO and cleared from AD when the Training
    # CSO is obsoleted. HR attributes and AD identity remain intact.
    #
    # Topology: HR CSV (primary) + Training CSV (secondary) -> MVO -> LDAP
    # =============================================================================================================
    if ($Step -eq "ManualRecall" -or $Step -eq "All") {
        Write-TestSection "Test 5: Manual Deletion Rule + Recall Attributes (Training Source)"
        Write-Host "DeletionRule: Manual, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true (on Training object type)" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (Manual = never auto-delete), Training attributes recalled and cleared from AD" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - recall enabled on Training system's object type
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "Manual" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $true `
            -RecallConnectedSystemId $config.TrainingSystemId

        # Provision a test user via HR CSV (creates MVO + LDAP CSO)
        $test5Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "MANUAL001" `
            -SamAccountName "test.manual.recall" `
            -DisplayName "Test Manual Recall" `
            -TestName "Test5"

        $test5MvoId = $test5Mvo.id
        Write-Host "  MVO ID: $test5MvoId" -ForegroundColor Gray

        # Provision training data (creates Training CSO, exports Training attrs to LDAP)
        Invoke-ProvisionTrainingData -Config $config `
            -EmployeeId "MANUAL001" `
            -SamAccountName "test.manual.recall" `
            -TestName "Test5"

        # Record pending export count before recall
        Invoke-DrainPendingExports -Config $config
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before recall: $pendingExportsBefore" -ForegroundColor Gray

        # Remove training data - Training import+sync only (obsoletes Training CSO, triggers recall)
        Invoke-RemoveTrainingData -Config $config -EmployeeId "MANUAL001" -TestName "Test5"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (Manual rule - never auto-deleted)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Manual Recall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "MVO deleted with Manual deletion rule" }
            throw "Test 5 Assert 1 failed: MVO was deleted despite Manual deletion rule"
        }
        Write-Host "  PASSED: MVO still exists (Manual deletion rule - never auto-deleted)" -ForegroundColor Green

        # Assert 2: Training-contributed attributes were recalled from MVO
        $mvoDetail = Get-JIMMetaverseObject -Id $test5MvoId -ErrorAction SilentlyContinue

        if ($mvoDetail) {
            $trainingStatusValue = $null
            if ($mvoDetail.attributeValues) {
                $trainingStatusAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Training Status' } | Select-Object -First 1
                if ($trainingStatusAttr) {
                    $trainingStatusValue = $trainingStatusAttr.stringValue
                }
            }
            if (-not $trainingStatusValue) {
                Write-Host "  PASSED: Training-contributed attribute 'Training Status' has been recalled (empty/null)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "Training attribute 'Training Status' still has value: $trainingStatusValue" }
                throw "Test 5 Assert 2 failed: Training attribute 'Training Status' still has value: $trainingStatusValue"
            }
        }

        # Assert 3: HR-contributed attributes are retained
        if ($mvoDetail) {
            $deptValue = $null
            if ($mvoDetail.attributeValues) {
                $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
                if ($deptAttr) {
                    $deptValue = $deptAttr.stringValue
                }
            }
            if ($deptValue) {
                Write-Host "  PASSED: HR-contributed attribute 'Department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "HR-contributed attribute 'Department' was incorrectly recalled" }
                throw "Test 5 Assert 3 failed: HR-contributed attribute 'Department' was incorrectly recalled"
            }
        }

        # Assert 4: Pending exports created on LDAP to clear Training attributes
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after recall: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -gt $pendingExportsBefore) {
            Write-Host "  PASSED: Pending exports created on LDAP to clear recalled Training attributes" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "No pending exports created on LDAP after Training attribute recall" }
            throw "Test 5 Assert 4 failed: Expected pending exports on LDAP to clear Training attributes (description)"
        }

        # Assert 5: Run LDAP Export and verify AD user is still functional with Training attrs cleared
        Write-Host "  Running LDAP export to apply recall exports..." -ForegroundColor Gray
        $recallExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $recallExport.activityId -Name "LDAP Export (Test5 recall)"

        Start-Sleep -Seconds 3

        # Verify AD user still exists and identity is intact
        $adOutput = & docker exec samba-ad-primary bash -c "samba-tool user show 'test.manual.recall' 2>&1"
        if ($adOutput -match "sAMAccountName:\s*test\.manual\.recall") {
            Write-Host "  PASSED: AD user still exists with sAMAccountName intact" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "AD user not found or sAMAccountName missing after recall export" }
            throw "Test 5 Assert 5 failed: AD user not found or sAMAccountName missing after recall export"
        }

        # Verify Training attributes cleared from AD
        if ($adOutput -match "description:\s*(.+)") {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "AD 'description' still has value after recall: $($Matches[1])" }
            throw "Test 5 Assert 5 failed: AD 'description' attribute still has value after recall export: $($Matches[1])"
        } else {
            Write-Host "  PASSED: Training attribute 'description' cleared from AD after recall export" -ForegroundColor Green
        }

        # Assert 6: MVO should NOT be marked as pending deletion (Manual rule)
        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if (-not $mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=false (Manual rule does not mark for deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "MVO isPendingDeletion=true despite Manual deletion rule" }
                throw "Test 5 Assert 6 failed: MVO isPendingDeletion=true despite Manual deletion rule"
            }
        }

        $testResults.Steps += @{ Name = "ManualRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 6: Manual + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
    # =============================================================================================================
    # Manual deletion rule + no attribute recall = nothing happens to the MVO at all.
    # The CSO is obsoleted/disconnected but the MVO retains all attributes and no exports created.
    # =============================================================================================================
    if ($Step -eq "ManualNoRecall" -or $Step -eq "All") {
        Write-TestSection "Test 6: Manual Deletion Rule + No Attribute Recall"
        Write-Host "DeletionRule: Manual, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false" -ForegroundColor Gray
        Write-Host "Expected: MVO remains, attributes stay, no pending exports" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "Manual" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $false

        # Provision a test user
        $test6Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "MANUAL002" `
            -SamAccountName "test.manual.norecall" `
            -DisplayName "Test Manual NoRecall" `
            -TestName "Test6"

        $test6MvoId = $test6Mvo.id
        Write-Host "  MVO ID: $test6MvoId" -ForegroundColor Gray

        # Drain any pending exports from provisioning before testing
        $provisionExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after drain: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.manual.norecall" -TestName "Test6"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (Manual rule - never auto-deleted)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Manual NoRecall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "MVO deleted with Manual deletion rule" }
            throw "Test 6 Assert 1 failed: MVO was deleted despite Manual deletion rule"
        }
        Write-Host "  PASSED: MVO still exists (Manual deletion rule - never auto-deleted)" -ForegroundColor Green

        # Assert 2: Attributes should remain on MVO (not recalled)
        $mvoDetail = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Manual NoRecall" -Attributes Department -PageSize 10 -ErrorAction SilentlyContinue) |
            Where-Object { $_.displayName -eq "Test Manual NoRecall" } | Select-Object -First 1

        if ($mvoDetail) {
            # The API returns requested attributes in the 'attributes' dictionary property
            $deptValue = $null
            if ($mvoDetail.attributes -and $mvoDetail.attributes.PSObject.Properties.Name -contains 'Department') {
                $deptValue = $mvoDetail.attributes.Department
            }
            if ($deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "department was removed despite RemoveContributedAttributesOnObsoletion=false" }
                throw "Test 6 Assert 2 failed: CSV-contributed attribute 'department' was removed despite RemoveContributedAttributesOnObsoletion=false"
            }
        }

        # Assert 3: No new pending exports (nothing changed on MVO)
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -le $pendingExportsBefore) {
            Write-Host "  PASSED: No new pending exports on LDAP target (attributes unchanged)" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "Unexpected pending exports created on LDAP target" }
            throw "Test 6 Assert 3 failed: Unexpected pending exports created on LDAP target"
        }

        # Assert 4: MVO should NOT be marked as pending deletion
        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if (-not $mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=false (Manual rule does not mark for deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "MVO isPendingDeletion=true despite Manual deletion rule" }
                throw "Test 6 Assert 4 failed: MVO isPendingDeletion=true despite Manual deletion rule"
            }
        }

        $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 7: Internal MVO Protection (DEFERRED)
    # =============================================================================================================
    # Internal MVOs (Origin=Internal) must NEVER be auto-deleted regardless of deletion rule.
    # This test is deferred until the Internal MVO management feature is implemented,
    # which will allow creating and managing Internal MVOs via the admin UI/API.
    # See GitHub issue for Internal MVO management.
    # =============================================================================================================
    if ($Step -eq "InternalProtection" -or $Step -eq "All") {
        Write-TestSection "Test 7: Internal MVO Protection (DEFERRED)"
        Write-Host "  SKIPPED: This test is deferred until Internal MVO management is implemented." -ForegroundColor Yellow
        Write-Host "  Internal MVOs (Origin=Internal) must never be auto-deleted." -ForegroundColor Gray
        Write-Host "  When implemented, this test will:" -ForegroundColor Gray
        Write-Host "    1. Create an Internal MVO via the admin API" -ForegroundColor Gray
        Write-Host "    2. Configure WhenLastConnectorDisconnected with 0 grace period" -ForegroundColor Gray
        Write-Host "    3. Verify the Internal MVO is never deleted" -ForegroundColor Gray
        Write-Host "  See GitHub issue for Internal MVO management feature." -ForegroundColor Gray
        Write-Host ""
        $testResults.Steps += @{
            Name = "InternalProtection"
            Success = $true
            Warning = "DEFERRED - Internal MVO management not yet implemented"
        }
    }

    # =============================================================================================================
    # Reset deletion rules to sensible default before finishing
    # =============================================================================================================
    Write-TestSection "Cleanup: Reset Deletion Rules"
    try {
        Set-JIMMetaverseObjectType -Id $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -DeletionGracePeriod ([TimeSpan]::FromDays(7))

        # Reset RemoveContributedAttributesOnObsoletion to default (true) on both CSV and Training
        $csvObjectTypes = Get-JIMConnectedSystem -Id $config.CSVSystemId -ObjectTypes
        $csvUserType = $csvObjectTypes | Where-Object { $_.name -match "^(user|person|record)$" } | Select-Object -First 1
        if ($csvUserType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $config.CSVSystemId -ObjectTypeId $csvUserType.id `
                -RemoveContributedAttributesOnObsoletion $true
        }

        $trainingObjectTypes = Get-JIMConnectedSystem -Id $config.TrainingSystemId -ObjectTypes
        $trainingRecordType = $trainingObjectTypes | Where-Object { $_.name -match "^(trainingRecord|record)$" } | Select-Object -First 1
        if ($trainingRecordType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $config.TrainingSystemId -ObjectTypeId $trainingRecordType.id `
                -RemoveContributedAttributesOnObsoletion $true
        }

        Write-Host "  Reset to: DeletionRule=WhenLastConnectorDisconnected, GracePeriod=7 days" -ForegroundColor Green
        Write-Host "  Reset to: RemoveContributedAttributesOnObsoletion=true (CSV + Training)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Could not reset deletion rules: $_" -ForegroundColor Red
        throw
    }

    # =============================================================================================================
    # Summary
    # =============================================================================================================
    Write-TestSection "Test Results Summary"

    $successCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $failCount = @($testResults.Steps | Where-Object { -not $_.Success }).Count
    $totalCount = @($testResults.Steps).Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })
    if ($failCount -gt 0) {
        Write-Host "Tests failed: $failCount" -ForegroundColor Red
    }

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "PASS" } else { "FAIL" }
        $color = if ($stepResult.Success) { "Green" } else { "Red" }

        Write-Host "  [$status] $($stepResult.Name)" -ForegroundColor $color

        if ($stepResult.ContainsKey('Error') -and $stepResult.Error) {
            Write-Host "         Error: $($stepResult.Error)" -ForegroundColor Red
        }
        if ($stepResult.ContainsKey('Warning') -and $stepResult.Warning) {
            Write-Host "         Note: $($stepResult.Warning)" -ForegroundColor Yellow
        }
    }

    $testResults.Success = ($successCount -eq $totalCount)
    $testResults.EndTime = (Get-Date).ToString("o")
    $testResults.TotalTests = $totalCount
    $testResults.PassedTests = $successCount
    $testResults.FailedTests = $failCount

    # Save structured test results to JSON for diagnostics
    $resultsDir = Join-Path $PSScriptRoot ".." "results" "test-results"
    if (-not (Test-Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }
    $resultsTimestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $resultsFile = Join-Path $resultsDir "Scenario4-DeletionRules-$Template-$resultsTimestamp.json"
    $testResults | ConvertTo-Json -Depth 5 | Set-Content $resultsFile
    Write-Host ""
    Write-Host "Test results saved to: $resultsFile" -ForegroundColor Gray

    if ($testResults.Success) {
        Write-Host ""
        Write-Host "All tests passed" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host ""
        Write-Host "Some tests failed" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host ""
    Write-Host "Scenario 4 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    $testResults.EndTime = (Get-Date).ToString("o")

    # Save structured test results even on failure
    $resultsDir = Join-Path $PSScriptRoot ".." "results" "test-results"
    if (-not (Test-Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }
    $resultsTimestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $resultsFile = Join-Path $resultsDir "Scenario4-DeletionRules-$Template-$resultsTimestamp.json"
    $testResults | ConvertTo-Json -Depth 5 | Set-Content $resultsFile
    Write-Host "Test results saved to: $resultsFile" -ForegroundColor Gray

    exit 1
}
