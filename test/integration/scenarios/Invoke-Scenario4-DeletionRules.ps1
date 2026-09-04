# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    Three-situation preservation model (#1570; see docs/concepts/attribute-priority.md "When the
    winning source disconnects or withdraws"): a departed source's sole-contributed values are either
    recalled (a remaining import source stands behind the object; Tests 1, 5), frozen for a pending
    deletion's grace window (Test 4b), or preserved as the object's last known state when no import
    source remains at all (Test 1b). All three situations have dedicated coverage below.

    Test 1: WhenLastConnectorDisconnected + Recall (Training source, end-to-end)
        - Provision user via HR + Training, export Training attrs to LDAP
        - Remove training record, run Training import+sync (obsoletes Training CSO)
        - Assert: MVO still exists (HR CSO + LDAP CSO still joined)
        - Assert: Training-contributed attributes recalled from MVO
        - Assert: HR-contributed attributes retained on MVO
        - Assert: Pending exports created on LDAP to clear Training attrs
        - Assert: LDAP export succeeds, AD user functional, Training attrs cleared from AD

    Test 1b: WhenLastConnectorDisconnected + No Import Source Remains -> Preserved as Last Known State
        - Contrast with Test 1: the HR CSV source (the sole import source) disconnects while the LDAP
          CSO remains joined. LDAP carries no enabled import Synchronisation Rule for User, so once CSV
          disconnects, no import source remains behind the object (#1570 situation 3)
        - Assert: MVO still exists (LDAP CSO still joined - not the last connector) and is NOT marked
          for deletion
        - Assert: HR-contributed attributes (e.g. Department, used in the DN expression) are preserved
          as last known state rather than recalled, and nothing is staged to LDAP as a result
        - Assert: the CSV Sync Activity records the 'MVO Values Preserved' (ValuesPreserved) outcome
        - Assert: the AD user remains functional (its DN, built from the preserved Department, intact)

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
        - Assert: housekeeping deletion cascade honours the export rule's Delete action
          (delete Pending Export staged, directory account removed)

    Test 4b: WhenAuthoritativeSourceDisconnected + GracePeriod=1 hour + Recall Enabled -> Preserved
    for the Grace Window
        - Contrast with Test 4 (which sets RemoveContributedAttributesOnObsoletion=false and so never
          reaches the recall/freeze logic): here recall is ENABLED on the authoritative CSV source, but
          the disconnection also schedules the MVO's deletion, so the sole-contributed attributes are
          frozen for the grace window rather than recalled (#1570 situation 1)
        - Assert: MVO marked for deletion (isPendingDeletion=true), CSV-contributed attributes still
          present (frozen, not recalled), nothing staged to LDAP as a result
        - Assert: the CSV Sync Activity records MvoDeletionScheduled (the queryable audit signal for
          this situation) but NOT ValuesPreserved, which situation 3 reserves (a pending deletion
          already explains the freeze via its own outcome)

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

    Tests 8-11: Deprovisioning Action permutations (issue #655)
        When an MVO is deleted, downstream deprovisioning is driven by each export
        Synchronisation Rule's OutboundDeprovisionAction, regardless of how the CSO was
        joined. These tests cover the full CSO origin x action matrix using
        WhenAuthoritativeSourceDisconnected + GracePeriod=0 as the deletion trigger.
        A Joined CSO is arranged by provisioning via JIM, deleting the MVO under a
        Disconnect action (directory account and CSO survive unjoined), then re-adding
        the HR user so export matching rejoins the surviving CSO (JoinType=Joined).

    Test 8: Provisioned CSO + Delete action
        - Assert: delete Pending Export staged, directory account removed
    Test 9: Provisioned CSO + Disconnect action
        - Assert: no delete Pending Export, CSO disconnected (JoinType=NotJoined),
          directory account left in place
    Test 10: Joined CSO + Delete action (the issue #655 headline case)
        - Assert: delete Pending Export staged, directory account removed
    Test 11: Joined CSO + Disconnect action
        - Assert: no delete Pending Export, CSO disconnected, directory account left in place

    The DeprovisionActions step runs Tests 8-11 together.

    Tests 12-13: Authoritative Source Trigger Modes (issue #119)
        WhenAuthoritativeSourceDisconnected has a configurable trigger mode: AllSourcesDisconnect
        (delete only once no selected source retains a joined CSO) or SpecificSourcesDisconnect
        (any selected source disconnecting triggers deletion; the pre-#119 behaviour). Grace period
        rejoin cancellation is mode-aware: a rejoin only cancels a scheduled deletion when the
        mode's trigger condition no longer holds. Both tests select TWO sources (HR CSV + Training
        CSV) and use a long grace period so housekeeping never deletes mid-test.

    Test 12: All sources trigger mode
        - Configure AllSourcesDisconnect with HR CSV + Training CSV as sources, 1-hour grace period
        - Remove the training record (first source disconnects)
        - Assert: MVO NOT marked for deletion (the HR source still holds a joined CSO)
        - Remove the HR user (last remaining source disconnects; the LDAP target CSO remains)
        - Assert: MVO marked for deletion with DeletionTriggeredBySystemId/Name recorded (HR CSV)
          and the decision-time policy snapshot persisted on the MVO
        - Re-add the HR user (any listed source rejoining falsifies "all sources gone")
        - Assert: rejoin lands on the SAME MVO and the scheduled deletion is cancelled
          (all deletion markers cleared)

    Test 13: Mode-aware grace period rejoin cancellation (Specific mode)
        - Configure SpecificSourcesDisconnect with HR CSV + Training CSV as sources, 1-hour grace period
        - Remove the HR user (marks the MVO), then the training record (re-marks; the recorded
          trigger is now the Training system)
        - Re-add the HR user: a listed source, but NOT the recorded triggering system
        - Assert: deletion NOT cancelled (MVO still marked, trigger fields unchanged)
        - Re-add the training record: the recorded triggering system rejoins
        - Assert: deletion cancelled (all deletion markers cleared, isPendingDeletion=false)

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
    ./Invoke-Scenario4-DeletionRules.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario4-DeletionRules.ps1 -Step AuthoritativeImmediate -Template Nano -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
        "WhenLastConnectorRecall",
        "NoSourceRemainsPreserves",
        "WhenLastConnectorNoRecall",
        "AuthoritativeImmediate",
        "AuthoritativeGracePeriod",
        "PendingDeletionPreserves",
        "ManualRecall",
        "ManualNoRecall",
        "InternalProtection",
        "DeprovisionProvisionedDelete",
        "DeprovisionProvisionedDisconnect",
        "DeprovisionJoinedDelete",
        "DeprovisionJoinedDisconnect",
        "DeprovisionActions",
        "AuthoritativeAllSources",
        "AuthoritativeRejoinCancellation",
        "All"
    )]
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

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}
$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"

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
    $upn = "$SamAccountName@panoply.local"

    # Add user to CSV
    $csv = Import-Csv $csvPath
    $newUser = [PSCustomObject]@{
        employeeId      = $EmployeeId
        firstName        = $DisplayName.Split(' ')[0]
        lastName         = $DisplayName.Split(' ')[-1]
        email            = "$SamAccountName@panoply.local"
        department       = "Information Technology"
        title            = "Engineer"
        company          = "Panoply"
        samAccountName   = $SamAccountName
        displayName      = $DisplayName
        status           = "Active"
        userPrincipalName = $upn
        employeeType     = "Employee"
        employeeEndDate  = ""
    }
    $csv = @($csv) + $newUser
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
    Write-Host "  Added $SamAccountName to CSV" -ForegroundColor Gray

    # Import + Sync + Export + Confirm
    Write-Host "  Running import+sync+export cycle ($TestName)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName provision)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync ($TestName provision)"

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName provision)"

    # Confirm export with LDAP import
    $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import ($TestName confirm)"
    Start-Sleep -Seconds 2

    # Verify user exists in directory
    $userExists = Test-LDAPUserExists -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig
    if (-not $userExists) {
        throw "User $SamAccountName was not provisioned to directory during $TestName"
    }
    Write-Host "  User $SamAccountName provisioned to directory" -ForegroundColor Green

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
    Copy-CsvToConnectorFiles -SourcePath $csvPath
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
        Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName removal)"

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
    Copy-CsvToConnectorFiles -SourcePath $trainingCsvPath
    Write-Host "  Added training record for $SamAccountName to Training CSV" -ForegroundColor Gray

    # Training Import + Sync (joins Training CSO to existing MVO)
    Write-Host "  Running Training import+sync ($TestName)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Training Import ($TestName)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.TrainingSystemId -RunProfileId $Config.TrainingSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Training Sync ($TestName)"

    # LDAP Export to push Training attributes (description) to AD
    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName training)"

    # Confirming import: updates the LDAP CSO attribute cache with exported Training values.
    # Without this, the no-net-change detection during recall would see the CSO as having no
    # 'description' attribute, causing the null-clearing recall export to be incorrectly skipped.
    $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import ($TestName training confirm)"

    Start-Sleep -Seconds 2

    # Verify Training attributes reached directory
    $ldapUser = Get-LDAPUser -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig
    $descValue = if ($ldapUser -and $ldapUser.ContainsKey('description')) { $ldapUser['description'] } else { $null }
    if ($descValue) {
        Write-Host "  Training attributes exported to directory (description: $descValue)" -ForegroundColor Green
    } else {
        Write-Host "  WARNING: Training attribute 'description' not found on directory user" -ForegroundColor Yellow
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
    Copy-CsvToConnectorFiles -SourcePath $trainingCsvPath
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

        # Step 1: Export any Pending-status PEs (executes them, transitions to Exported)
        $drainExport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2

        # Step 2: Run confirming import to reconcile Exported-status PEs.
        # Without this, Exported PEs (especially Delete PEs) remain in the database
        # indefinitely because only import reconciliation can delete confirmed PEs.
        $drainImport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2

        # Verify drain succeeded
        $remaining = Get-PendingExportCount -ConnectedSystemId $Config.LDAPSystemId
        if ($remaining -gt 0) {
            Write-Host "  WARNING: $remaining pending export(s) remain after drain (may be resolved by next sync cycle)" -ForegroundColor Yellow
        }
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
        [int[]]$DeletionTriggerConnectedSystemIds,

        # Authoritative Source Trigger Mode (issue #119). When omitted, the stored mode is left
        # unchanged (matching the Set-JIMMetaverseObjectType semantics).
        [Parameter(Mandatory=$false)]
        [ValidateSet('AllSourcesDisconnect', 'SpecificSourcesDisconnect')]
        [string]$DeletionTriggerMode,

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
    if ($DeletionTriggerMode) {
        $setParams.DeletionTriggerMode = $DeletionTriggerMode
    }
    Set-JIMMetaverseObjectType @setParams

    $modeLabel = if ($DeletionTriggerMode) { ", TriggerMode=$DeletionTriggerMode" } else { "" }
    Write-Host "  Configured MVO type: DeletionRule=$DeletionRule, GracePeriod=$GracePeriod$modeLabel" -ForegroundColor Green

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

# -----------------------------------------------------------------------------------------------------------------
# Helper: Resolve the LDAP export Synchronisation Rule header
# -----------------------------------------------------------------------------------------------------------------
function Get-LdapExportSyncRule {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    $rule = @(Get-JIMSyncRule) | Where-Object {
        $_.connectedSystemId -eq $Config.LDAPSystemId -and "$($_.direction)" -eq 'Export'
    } | Select-Object -First 1

    if (-not $rule) {
        throw "Could not find the export Synchronisation Rule for Connected System $($Config.LDAPSystemId)"
    }
    return $rule
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Set the LDAP export Synchronisation Rule's Deprovisioning Action (issue #655)
# -----------------------------------------------------------------------------------------------------------------
function Set-ExportRuleDeprovisionAction {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$Action
    )

    $rule = Get-LdapExportSyncRule -Config $Config
    Set-JIMSyncRule -Id $rule.id -OutboundDeprovisionAction $Action `
        -ChangeReason "Scenario 4 deprovisioning action test" | Out-Null
    Write-Host "  Configured export Synchronisation Rule '$($rule.name)': OutboundDeprovisionAction=$Action" -ForegroundColor Green
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Find the LDAP CSO header for a user by display name (includes joinType)
# -----------------------------------------------------------------------------------------------------------------
function Get-LdapCsoHeader {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$DisplayName
    )

    return @(Get-JIMConnectedSystemObject -ConnectedSystemId $Config.LDAPSystemId -Search $DisplayName -PageSize 10) |
        Where-Object { $_.displayName -eq $DisplayName } | Select-Object -First 1
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Count delete-type Pending Exports on the LDAP system
# -----------------------------------------------------------------------------------------------------------------
function Get-DeletePendingExportCount {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    return [int](Get-JIMPendingExport -ConnectedSystemId $Config.LDAPSystemId -Count -ChangeType Delete)
}

# -----------------------------------------------------------------------------------------------------------------
# Note: Get-MvoDeletionMarkers (reads an MVO's deletion markers directly from the database, issue #119)
# now lives in Test-Helpers.ps1, shared with Scenario 5's same-page rejoin cancellation probe (#1612).
# -----------------------------------------------------------------------------------------------------------------

# -----------------------------------------------------------------------------------------------------------------
# Helper: Arrange a JOINED (not Provisioned) LDAP CSO for a test user (issue #655)
# Provisions via JIM, deletes the MVO under a Disconnect action (directory account and CSO survive
# unjoined), then re-adds the HR user so export matching rejoins the surviving CSO with JoinType=Joined.
# Requires the deletion rule to already be WhenAuthoritativeSourceDisconnected + GracePeriod=0.
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ProvisionJoinedUser {
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

    # Phase 1: provision via JIM as normal; Employee ID is exported to the directory for matching
    Write-Host "  Join arrange phase 1: provisioning $SamAccountName via JIM..." -ForegroundColor Gray
    Invoke-ProvisionUser -Config $Config -EmployeeId $EmployeeId -SamAccountName $SamAccountName `
        -DisplayName $DisplayName -TestName "$TestName join arrange (provision)" | Out-Null

    # Phase 2: delete the MVO under a Disconnect action; the directory account and CSO survive unjoined
    Write-Host "  Join arrange phase 2: deleting MVO under Disconnect action..." -ForegroundColor Gray
    Set-ExportRuleDeprovisionAction -Config $Config -Action Disconnect
    Invoke-RemoveUserFromSource -Config $Config -SamAccountName $SamAccountName `
        -TestName "$TestName join arrange (disconnect)"
    Start-Sleep -Seconds 3

    if (Test-MvoExists -DisplayName $DisplayName -ObjectTypeName "User") {
        throw "$TestName join arrange failed: MVO still exists after authoritative source disconnect"
    }
    if (-not (Test-LDAPUserExists -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig)) {
        throw "$TestName join arrange failed: directory account was removed despite Disconnect action"
    }

    # Phase 3: re-add the HR user; export matching finds the surviving CSO and joins instead of provisioning
    Write-Host "  Join arrange phase 3: re-adding HR user so export matching rejoins the CSO..." -ForegroundColor Gray
    $mvo = Invoke-ProvisionUser -Config $Config -EmployeeId $EmployeeId -SamAccountName $SamAccountName `
        -DisplayName $DisplayName -TestName "$TestName join arrange (rejoin)"

    $cso = Get-LdapCsoHeader -Config $Config -DisplayName $DisplayName
    if (-not $cso) {
        throw "$TestName join arrange failed: LDAP CSO not found for $DisplayName after rejoin"
    }
    if ("$($cso.joinType)" -ne 'Joined') {
        throw "$TestName join arrange failed: expected LDAP CSO JoinType=Joined but found '$($cso.joinType)'"
    }
    Write-Host "  LDAP CSO rejoined via export matching (JoinType=Joined)" -ForegroundColor Green
    return $mvo
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Run one Deprovisioning Action permutation test (issue #655)
# Arranges a CSO of the requested origin, sets the export rule's action, deletes the MVO via
# authoritative source disconnect (grace period 0), then asserts the action was honoured.
# -----------------------------------------------------------------------------------------------------------------
function Invoke-DeprovisionActionTest {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$UserObjectTypeId,

        [Parameter(Mandatory=$true)]
        [ValidateSet('Provisioned', 'Joined')]
        [string]$CsoOrigin,

        [Parameter(Mandatory=$true)]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$DeprovisionAction,

        [Parameter(Mandatory=$true)]
        [string]$EmployeeId,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$DisplayName,

        [Parameter(Mandatory=$true)]
        [string]$TestName,

        [Parameter(Mandatory=$true)]
        [string]$StepName
    )

    Invoke-DrainPendingExports -Config $Config

    # Immediate MVO deletion on authoritative (HR CSV) disconnect; recall is irrelevant to deletion
    Set-DeletionRuleConfig -Config $Config -ObjectTypeId $UserObjectTypeId `
        -DeletionRule "WhenAuthoritativeSourceDisconnected" `
        -GracePeriod ([TimeSpan]::Zero) `
        -DeletionTriggerConnectedSystemIds "$($Config.CSVSystemId)" `
        -RemoveContributedAttributesOnObsoletion $false

    # Arrange the CSO with the required origin
    if ($CsoOrigin -eq 'Joined') {
        Invoke-ProvisionJoinedUser -Config $Config -EmployeeId $EmployeeId -SamAccountName $SamAccountName `
            -DisplayName $DisplayName -TestName $TestName | Out-Null
    }
    else {
        Invoke-ProvisionUser -Config $Config -EmployeeId $EmployeeId -SamAccountName $SamAccountName `
            -DisplayName $DisplayName -TestName $TestName | Out-Null
    }

    # Assert arrange: the CSO origin is what this permutation requires
    $cso = Get-LdapCsoHeader -Config $Config -DisplayName $DisplayName
    if (-not $cso) {
        $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "LDAP CSO not found after arrange" }
        throw "$TestName arrange failed: LDAP CSO not found for $DisplayName"
    }
    if ("$($cso.joinType)" -ne $CsoOrigin) {
        $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "Expected CSO JoinType=$CsoOrigin, found $($cso.joinType)" }
        throw "$TestName arrange failed: expected LDAP CSO JoinType=$CsoOrigin but found '$($cso.joinType)'"
    }
    Write-Host "  Arranged LDAP CSO with JoinType=$CsoOrigin" -ForegroundColor Green

    # Set the Deprovisioning Action under test
    Set-ExportRuleDeprovisionAction -Config $Config -Action $DeprovisionAction

    # Baseline the delete Pending Export count before the act
    Invoke-DrainPendingExports -Config $Config
    $deletePesBefore = Get-DeletePendingExportCount -Config $Config
    Write-Host "  Delete Pending Exports before MVO deletion: $deletePesBefore" -ForegroundColor Gray

    # Act: remove the user from HR; the MVO is deleted immediately during CSV sync
    Invoke-RemoveUserFromSource -Config $Config -SamAccountName $SamAccountName -TestName $TestName
    Start-Sleep -Seconds 3

    # Assert 1: MVO deleted
    if (Test-MvoExists -DisplayName $DisplayName -ObjectTypeName "User") {
        $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "MVO not deleted on authoritative source disconnect" }
        throw "$TestName Assert 1 failed: MVO still exists after authoritative source disconnect"
    }
    Write-Host "  PASSED: MVO deleted when authoritative source disconnected" -ForegroundColor Green

    $deletePesAfter = Get-DeletePendingExportCount -Config $Config
    Write-Host "  Delete Pending Exports after MVO deletion: $deletePesAfter" -ForegroundColor Gray

    if ($DeprovisionAction -eq 'Delete') {
        # Assert 2: a delete Pending Export was staged for the CSO
        if ($deletePesAfter -le $deletePesBefore) {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "No delete Pending Export staged despite Delete action" }
            throw "$TestName Assert 2 failed: expected a delete Pending Export for the $CsoOrigin CSO (Delete action)"
        }
        Write-Host "  PASSED: Delete Pending Export staged for the $CsoOrigin CSO" -ForegroundColor Green

        # Assert 3: LDAP export removes the account from the directory
        Write-Host "  Running LDAP export to apply the delete..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName deprovision)"
        Start-Sleep -Seconds 3

        # Confirming import reconciles the Exported delete Pending Export (prevents stale PE accumulation)
        $confirmImport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmImport.activityId -Name "LDAP Import ($TestName confirm deprovision)"
        Start-Sleep -Seconds 2

        if (Test-LDAPUserExists -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig) {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "Directory account still exists after Delete action export" }
            throw "$TestName Assert 3 failed: directory account $SamAccountName still exists after Delete action export"
        }
        Write-Host "  PASSED: Directory account deleted (Delete action honoured for $CsoOrigin CSO)" -ForegroundColor Green
    }
    else {
        # Assert 2: no delete Pending Export was staged
        if ($deletePesAfter -gt $deletePesBefore) {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "Delete Pending Export staged despite Disconnect action" }
            throw "$TestName Assert 2 failed: a delete Pending Export was staged for the $CsoOrigin CSO (Disconnect action)"
        }
        Write-Host "  PASSED: No delete Pending Export staged (Disconnect action)" -ForegroundColor Green

        # Assert 3: the CSO was disconnected (join broken, object left in place)
        $csoAfter = Get-LdapCsoHeader -Config $Config -DisplayName $DisplayName
        if (-not $csoAfter) {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "LDAP CSO missing after Disconnect action" }
            throw "$TestName Assert 3 failed: LDAP CSO not found after Disconnect action (it should remain, disconnected)"
        }
        if ("$($csoAfter.joinType)" -ne 'NotJoined') {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "CSO JoinType=$($csoAfter.joinType) after Disconnect action (expected NotJoined)" }
            throw "$TestName Assert 3 failed: expected CSO JoinType=NotJoined after disconnect but found '$($csoAfter.joinType)'"
        }
        Write-Host "  PASSED: LDAP CSO disconnected (JoinType=NotJoined)" -ForegroundColor Green

        # Assert 4: an export cycle leaves the directory account in place
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName no-op)"
        Start-Sleep -Seconds 2

        if (-not (Test-LDAPUserExists -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig)) {
            $testResults.Steps += @{ Name = $StepName; Success = $false; Error = "Directory account deleted despite Disconnect action" }
            throw "$TestName Assert 4 failed: directory account $SamAccountName was removed despite Disconnect action"
        }
        Write-Host "  PASSED: Directory account left in place (Disconnect action honoured for $CsoOrigin CSO)" -ForegroundColor Green
    }

    $testResults.Steps += @{ Name = $StepName; Success = $true }
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

    # Seed a full baseline set of CSVs into the volume first. Setup-Scenario1.ps1
    # creates four CSV connected systems (HR, Training, Departments, Cross-Domain)
    # and runs schema discovery against all of them, so every file must exist before
    # setup runs. We then overlay Scenario 4's minimal HR and Training CSVs on top
    # of the baselines so the deletion tests start with a known single-user state.
    # Prior to this the scenario relied on files leaking from Scenario 1's volume.
    Write-Host "Seeding baseline CSVs for Scenario 4..." -ForegroundColor Gray
    $testDataPath = "$PSScriptRoot/../../test-data"
    $scenarioDataPath = "$PSScriptRoot/data"

    if (-not (Test-Path $testDataPath)) {
        New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null
    }

    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template "Nano" -OutputPath $testDataPath

    # Overlay Scenario 4's tailored HR and Training CSVs (1 baseline user each)
    Write-Host "Applying Scenario 4 HR and Training overlays..." -ForegroundColor Gray
    Copy-Item -Path "$scenarioDataPath/scenario4-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force
    Copy-Item -Path "$scenarioDataPath/scenario4-training-records.csv" -Destination "$testDataPath/training-records.csv" -Force

    Write-FilesToConnectorVolume -SourceDir $testDataPath -Files @(
        @{ SourceFile = 'hr-users.csv';         DestinationPath = '/connector-files/test-data/hr-users.csv' }
        @{ SourceFile = 'training-records.csv'; DestinationPath = '/connector-files/test-data/training-records.csv' }
    )
    Write-Host "  CSVs initialised (HR + Training overlays over Nano baseline)" -ForegroundColor Green

    # Clean up test-specific directory users from previous test runs
    Write-Host "Cleaning up test-specific directory users from previous runs..." -ForegroundColor Gray
    $testUsers = @(
        "test.wlcd.recall", "test.wlcd.norecall",
        "test.nosource.preserve",
        "test.auth.immediate", "test.auth.grace",
        "test.pending.preserve",
        "test.manual.recall", "test.manual.norecall",
        "test.deprov.provdelete", "test.deprov.provdisc",
        "test.deprov.joindelete", "test.deprov.joindisc",
        "test.trigger.allsrc", "test.trigger.rejoin",
        "baseline.user1"
    )
    $deletedCount = 0
    foreach ($user in $testUsers) {
        if ($isOpenLDAP) {
            $userDN = "$($DirectoryConfig.UserRdnAttr)=$user,$($DirectoryConfig.UserContainer)"
            $output = docker exec $DirectoryConfig.ContainerName ldapdelete -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" "$userDN" 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            }
        }
        else {
            $output = & docker exec $DirectoryConfig.ContainerName bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
            if ($output -match "Deleted user") {
                Write-Host "  Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            }
        }
    }
    Write-Host "  Directory cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $setupParams = @{ JIMUrl = $JIMUrl; ApiKey = $ApiKey; Template = $Template }
    if ($DirectoryConfig) { $setupParams.DirectoryConfig = $DirectoryConfig }
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" @setupParams

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    Write-Host "JIM configured for Scenario 4" -ForegroundColor Green

    # Create department OUs needed for test users (Samba AD only — OpenLDAP uses flat OU)
    if (-not $isOpenLDAP) {
        Write-Host "Creating department OUs for test users..." -ForegroundColor Gray
        $testDepartments = @("Information Technology", "Operations")
        foreach ($dept in $testDepartments) {
            docker exec $DirectoryConfig.ContainerName samba-tool ou create "OU=$dept,OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Created OU: $dept" -ForegroundColor Gray
            }
        }
        Write-Host "  Department OUs ready" -ForegroundColor Green
    }

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
        Assert-ExportSuccess -ActivityId $recallExport.activityId -Name "LDAP Export (Test1 recall)"

        Start-Sleep -Seconds 3

        # Verify directory user still exists and identity is intact
        $ldapUser = Get-LDAPUser -UserIdentifier 'test.wlcd.recall' -DirectoryConfig $DirectoryConfig
        if ($ldapUser) {
            Write-Host "  PASSED: Directory user still exists after recall export" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "Directory user not found after recall export" }
            throw "Test 1 Assert 5 failed: Directory user not found after recall export"
        }

        # Verify Training attributes cleared from directory (description should be absent/empty)
        $descValue = if ($ldapUser -and $ldapUser.ContainsKey('description')) { $ldapUser['description'] } else { $null }
        if ($descValue) {
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "Directory 'description' still has value after recall: $descValue" }
            throw "Test 1 Assert 5 failed: Directory 'description' attribute still has value after recall export: $descValue"
        } else {
            Write-Host "  PASSED: Training attribute 'description' cleared from directory after recall export" -ForegroundColor Green
        }

        $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 1b: No Import Source Remains -> Values Preserved as Last Known State (#1570 situation 3)
    # =============================================================================================================
    # Contrast with Test 1 (recall): here the LAST import source disconnects and NO import source remains
    # joined afterwards (the LDAP CSO stays joined, but LDAP is an export-only target for the User type, so
    # it does not count as a source). WhenLastConnectorDisconnected therefore does NOT delete the MVO (the
    # LDAP CSO still counts as a connector), but the departed CSV CSO's sole-contributed attributes are no
    # longer recalled either: with nothing left to stand behind the object, JIM preserves them as the last
    # known state the target account (and any expression-based mapping built from them, e.g. a DN) was
    # built from, rather than recalling them and blanking a live account.
    # =============================================================================================================
    if ($Step -eq "NoSourceRemainsPreserves" -or $Step -eq "All") {
        Write-TestSection "Test 1b: No Import Source Remains - Values Preserved as Last Known State"
        Write-Host "DeletionRule: WhenLastConnectorDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true (on CSV object type)" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (LDAP CSO still joined), but CSV-contributed attributes are" -ForegroundColor Gray
        Write-Host "          preserved as last known state (NOT recalled) because no import source remains" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - recall enabled on the CSV (default) object type. LDAP has no enabled
        # import Synchronisation Rule for User, so once CSV disconnects, no import source remains.
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $true `
            -RecallConnectedSystemId $config.CSVSystemId

        # Provision a test user via HR CSV only (no Training join - CSV is the sole import source)
        $test1bMvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "PRES001" `
            -SamAccountName "test.nosource.preserve" `
            -DisplayName "Test NoSource Preserve" `
            -TestName "Test1b"

        $test1bMvoId = $test1bMvo.id
        Write-Host "  MVO ID: $test1bMvoId" -ForegroundColor Gray

        # Record pending export count before disconnect
        Invoke-DrainPendingExports -Config $config
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV - CSV-only cycle, inlined (rather than via Invoke-RemoveUserFromSource) so
        # this step can capture the Sync Activity id and query its RPEI outcomes below (Assert 5)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csv = Import-Csv $csvPath
        $csv = @($csv | Where-Object { $_.samAccountName -ne "test.nosource.preserve" })
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Write-Host "  Removed test.nosource.preserve from CSV" -ForegroundColor Gray

        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Test1b removal)"

        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync (Test1b removal)"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (LDAP CSO still joined - not the "last connector")
        $mvoStillExists = Test-MvoExists -DisplayName "Test NoSource Preserve" -ObjectTypeName "User"
        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $false; Error = "MVO deleted when LDAP CSO still joined" }
            throw "Test 1b Assert 1 failed: MVO deleted when LDAP CSO still joined"
        }
        Write-Host "  PASSED: MVO still exists (LDAP CSO still joined)" -ForegroundColor Green

        # Assert 2: MVO is NOT marked for deletion (WhenLastConnectorDisconnected did not trigger; contrast
        # with the PendingDeletionPreserves test, where a deletion IS scheduled)
        $mvoDetail = Get-JIMMetaverseObject -Id $test1bMvoId -ErrorAction SilentlyContinue
        $isPending = $mvoDetail -and ($mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') -and $mvoDetail.isPendingDeletion
        if ($isPending) {
            $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $false; Error = "MVO marked isPendingDeletion=true despite LDAP CSO still joined" }
            throw "Test 1b Assert 2 failed: MVO isPendingDeletion=true despite LDAP CSO still joined (WhenLastConnectorDisconnected should not have triggered)"
        }
        Write-Host "  PASSED: MVO isPendingDeletion=false (no deletion scheduled)" -ForegroundColor Green

        # Assert 3: CSV-contributed attribute (Department) is STILL present - preserved as last known
        # state, not recalled, because no import source remains to stand behind the object
        $deptValue = $null
        if ($mvoDetail -and $mvoDetail.attributeValues) {
            $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
            if ($deptAttr) { $deptValue = $deptAttr.stringValue }
        }
        if ($deptValue) {
            Write-Host "  PASSED: CSV-contributed attribute 'Department' preserved as last known state: $deptValue" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $false; Error = "CSV-contributed attribute 'Department' was recalled despite no import source remaining" }
            throw "Test 1b Assert 3 failed: CSV-contributed attribute 'Department' was recalled despite no import source remaining (should be preserved as last known state)"
        }

        # Assert 4: no new pending exports - the freeze means nothing was staged to LDAP as a recall clear
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray
        if ($pendingExportsAfter -gt $pendingExportsBefore) {
            $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $false; Error = "Pending exports created despite values being preserved (not recalled)" }
            throw "Test 1b Assert 4 failed: Pending exports were created on LDAP despite the preserved values not changing"
        }
        Write-Host "  PASSED: No pending exports created (nothing recalled, nothing staged)" -ForegroundColor Green

        # Assert 5: the disconnecting CSV Sync Activity recorded the 'MVO Values Preserved' audit outcome
        # (ValuesPreserved), the queryable signal for this situation (#1570)
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId -Name "CSV Sync (Test1b no-source preservation)" `
            -ExpectedOutcomeType "ValuesPreserved"

        # Assert 6: the directory account remains functional after an export cycle (the DN/identity built
        # from the preserved attribute is not damaged)
        $noopExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ExportSuccess -ActivityId $noopExport.activityId -Name "LDAP Export (Test1b no-op)"
        Start-Sleep -Seconds 2
        if (-not (Test-LDAPUserExists -UserIdentifier 'test.nosource.preserve' -DirectoryConfig $DirectoryConfig)) {
            $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $false; Error = "Directory account missing after preservation (last known state should keep the account intact)" }
            throw "Test 1b Assert 6 failed: directory account test.nosource.preserve missing after values were preserved as last known state"
        }
        Write-Host "  PASSED: Directory account remains intact (built from preserved last known state)" -ForegroundColor Green

        $testResults.Steps += @{ Name = "NoSourceRemainsPreserves"; Success = $true }
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

        # This test asserts directory deprovisioning, so pin the export rule's action to Delete
        # (a prior run of the Deprovisioning Action permutation tests may have left it as Disconnect)
        Set-ExportRuleDeprovisionAction -Config $config -Action Delete

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
            Assert-ExportSuccess -ActivityId $cleanupExport.activityId -Name "LDAP Export (Test3 deprovisioning)"

            # Run confirming import to reconcile the Exported Delete PE.
            # Without this, the Delete PE remains in Exported status and accumulates
            # as a stale PE that the drain mechanism cannot clear (only import reconciliation
            # can delete confirmed PEs).
            Start-Sleep -Seconds 3
            $confirmImport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $confirmImport.activityId -Name "LDAP Import (Test3 confirm deprovisioning)"

            # Verify user is removed from directory
            Start-Sleep -Seconds 3
            $userStillExists = Test-LDAPUserExists -UserIdentifier 'test.auth.immediate' -DirectoryConfig $DirectoryConfig
            if (-not $userStillExists) {
                Write-Host "  PASSED: User deprovisioned from directory (no longer exists)" -ForegroundColor Green
            } else {
                Write-Host "  WARNING: User may still exist in directory after export" -ForegroundColor Yellow
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

        # This test asserts the housekeeping deletion cascade deprovisions the directory,
        # so pin the export rule's action to Delete
        Set-ExportRuleDeprovisionAction -Config $config -Action Delete

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
        # With recall=false, display name is retained so we can search by name.
        # On miss, fall back to an ID lookup to distinguish a real deletion from a
        # display-name search miss (see engineering/notes/SCENARIO4_TEST4_GRACE_PERIOD_INVESTIGATION.md,
        # hypothesis #3). Different error messages let a future regression self-diagnose.
        $mvoStillExists = Test-MvoExists -DisplayName "Test Auth Grace" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $mvoStillExistsById = Test-MvoExistsById -MvoId $test4MvoId
            if ($mvoStillExistsById) {
                $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "Display-name search missed MVO but ID lookup found it (hypothesis #3: not a real deletion)" }
                throw "Test 4 Assert 1 inconclusive: display-name search missed MVO $test4MvoId but ID lookup found it. Likely a Test-MvoExists false negative (display name may have been altered), not a real deletion."
            }
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO deleted immediately despite grace period (confirmed via ID lookup)" }
            throw "Test 4 Assert 1 failed: MVO $test4MvoId was deleted immediately despite 1-minute grace period (confirmed via ID lookup)"
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

        # Assert 3: the housekeeping deletion cascade honours the export rule's Delete action:
        # the deferred deletion must stage a delete Pending Export and remove the directory account,
        # exactly as the sync-path deletion in Test 3 does (issue #655)
        Write-Host "  Running LDAP export to deprovision directory account (housekeeping cascade)..." -ForegroundColor Gray
        $graceExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ExportSuccess -ActivityId $graceExport.activityId -Name "LDAP Export (Test4 deprovisioning)"
        Start-Sleep -Seconds 3

        # Confirming import reconciles the Exported delete Pending Export
        $test4ConfirmImport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $test4ConfirmImport.activityId -Name "LDAP Import (Test4 confirm deprovisioning)"
        Start-Sleep -Seconds 2

        if (Test-LDAPUserExists -UserIdentifier 'test.auth.grace' -DirectoryConfig $DirectoryConfig) {
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "Directory account still exists after housekeeping deletion cascade export" }
            throw "Test 4 Assert 3 failed: directory account test.auth.grace still exists after housekeeping deletion cascade export"
        }
        Write-Host "  PASSED: Directory account deprovisioned via housekeeping deletion cascade (Delete action)" -ForegroundColor Green

        $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $true }
    }

    # =============================================================================================================
    # Test 4b: Deletion Pending -> Values Preserved for the Grace Window (#1570 situation 1)
    # =============================================================================================================
    # Contrast with Test 4 (which sets RemoveContributedAttributesOnObsoletion=false and so never reaches
    # the recall/freeze logic at all). Here recall IS enabled on the authoritative CSV source, but because
    # the disconnection also schedules the MVO's deletion (a non-zero grace period), the sole-contributed
    # attributes are frozen rather than recalled: recalling them would send clears to LDAP moments before
    # the grace period's deprovisioning removes the account anyway, and if the CSV source reappears within
    # the window the object must be exactly as it was, with nothing churned downstream in the meantime.
    # The 'MVO Values Preserved' outcome does NOT fire here (by design: the sibling MvoDeletionScheduled
    # outcome already explains the freeze; see ConnectedSystemObjectObsoletionService.cs), so this test
    # asserts MvoDeletionScheduled as the queryable audit signal instead, and asserts ValuesPreserved is
    # absent to lock in that distinction against situation 3 (NoSourceRemainsPreserves, above).
    # =============================================================================================================
    if ($Step -eq "PendingDeletionPreserves" -or $Step -eq "All") {
        Write-TestSection "Test 4b: Deletion Pending - Values Preserved for the Grace Window"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, GracePeriod: 1 hour" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true (on CSV object type)" -ForegroundColor Gray
        Write-Host "Expected: MVO marked for deletion, CSV-contributed attributes frozen (NOT recalled)," -ForegroundColor Gray
        Write-Host "          nothing staged to LDAP as a recall clear" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - CSV authoritative, 1-hour grace period (long enough that housekeeping
        # cannot race the assertions below), recall ENABLED (contrast with Test 4, which disables it)
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::FromHours(1)) `
            -DeletionTriggerConnectedSystemIds "$($config.CSVSystemId)" `
            -RemoveContributedAttributesOnObsoletion $true `
            -RecallConnectedSystemId $config.CSVSystemId

        # Provision a test user via HR CSV (creates MVO + CSV CSO + LDAP CSO)
        $test4bMvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "PRES002" `
            -SamAccountName "test.pending.preserve" `
            -DisplayName "Test Pending Preserve" `
            -TestName "Test4b"

        $test4bMvoId = $test4bMvo.id
        Write-Host "  MVO ID: $test4bMvoId" -ForegroundColor Gray

        # Record pending export count before disconnect
        Invoke-DrainPendingExports -Config $config
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV - CSV-only cycle, inlined (rather than via Invoke-RemoveUserFromSource) so
        # this step can capture the Sync Activity id and query its RPEI outcomes below (Assert 4)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csv = Import-Csv $csvPath
        $csv = @($csv | Where-Object { $_.samAccountName -ne "test.pending.preserve" })
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Write-Host "  Removed test.pending.preserve from CSV" -ForegroundColor Gray

        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Test4b removal)"

        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync (Test4b removal)"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (1-hour grace period not elapsed)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Pending Preserve" -ObjectTypeName "User"
        if (-not $mvoStillExists) {
            $mvoStillExistsById = Test-MvoExistsById -MvoId $test4bMvoId
            if ($mvoStillExistsById) {
                $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "Display-name search missed MVO but ID lookup found it" }
                throw "Test 4b Assert 1 inconclusive: display-name search missed MVO $test4bMvoId but ID lookup found it"
            }
            $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "MVO deleted immediately despite 1-hour grace period" }
            throw "Test 4b Assert 1 failed: MVO $test4bMvoId was deleted immediately despite the 1-hour grace period"
        }
        Write-Host "  PASSED: MVO still exists (grace period not yet elapsed)" -ForegroundColor Green

        # Assert 2: MVO is marked pending deletion, and its CSV-contributed attribute (Department) is
        # STILL present - frozen for the grace window, not recalled
        $mvoDetail = Get-JIMMetaverseObject -Id $test4bMvoId -ErrorAction SilentlyContinue
        $isPending = $mvoDetail -and ($mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') -and $mvoDetail.isPendingDeletion
        if (-not $isPending) {
            $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "MVO isPendingDeletion=false (should be marked for deferred deletion)" }
            throw "Test 4b Assert 2 failed: MVO isPendingDeletion=false (should be marked for deferred deletion)"
        }
        Write-Host "  PASSED: MVO isPendingDeletion=true (marked for deferred deletion)" -ForegroundColor Green

        $deptValue = $null
        if ($mvoDetail -and $mvoDetail.attributeValues) {
            $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
            if ($deptAttr) { $deptValue = $deptAttr.stringValue }
        }
        if ($deptValue) {
            Write-Host "  PASSED: CSV-contributed attribute 'Department' frozen (not recalled) for the grace window: $deptValue" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "CSV-contributed attribute 'Department' was recalled despite the deletion being merely pending" }
            throw "Test 4b Assert 2 failed: CSV-contributed attribute 'Department' was recalled despite the deletion being merely pending (should be frozen for the grace window)"
        }

        # Assert 3: no pending exports were created as a result of the (declined) recall
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray
        if ($pendingExportsAfter -gt $pendingExportsBefore) {
            $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "Pending exports created despite values being frozen (not recalled)" }
            throw "Test 4b Assert 3 failed: Pending exports were created on LDAP despite the frozen values not changing"
        }
        Write-Host "  PASSED: No pending exports created (nothing recalled, nothing staged ahead of the grace window)" -ForegroundColor Green

        # Assert 4: the disconnecting CSV Sync Activity recorded MvoDeletionScheduled - the audit signal
        # for this situation - but NOT ValuesPreserved, which is deliberately reserved for situation 3
        # (no import source remains; see NoSourceRemainsPreserves above) because a pending deletion already
        # explains the freeze via its own outcome
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId -Name "CSV Sync (Test4b pending-deletion preservation)" `
            -ExpectedOutcomeType "MvoDeletionScheduled"

        $items = @(Get-JIMActivity -Id $syncResult.activityId -ExecutionItems | Select-Object -First 100)
        $valuesPreservedItems = @($items | Where-Object { $_.outcomeSummary -match "ValuesPreserved:" })
        if ($valuesPreservedItems.Count -gt 0) {
            $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $false; Error = "ValuesPreserved outcome recorded despite a deletion being pending (MvoDeletionScheduled should be the sole explaining outcome)" }
            throw "Test 4b Assert 4 failed: ValuesPreserved outcome recorded despite a deletion being pending; MvoDeletionScheduled alone should explain the freeze"
        }
        Write-Host "  PASSED: MvoDeletionScheduled recorded as the sole explaining outcome (ValuesPreserved correctly absent)" -ForegroundColor Green

        $testResults.Steps += @{ Name = "PendingDeletionPreserves"; Success = $true }
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
        Assert-ExportSuccess -ActivityId $recallExport.activityId -Name "LDAP Export (Test5 recall)"

        Start-Sleep -Seconds 3

        # Verify directory user still exists and identity is intact
        $ldapUser = Get-LDAPUser -UserIdentifier 'test.manual.recall' -DirectoryConfig $DirectoryConfig
        if ($ldapUser) {
            Write-Host "  PASSED: Directory user still exists after recall export" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "Directory user not found after recall export" }
            throw "Test 5 Assert 5 failed: Directory user not found after recall export"
        }

        # Verify Training attributes cleared from directory
        $descValue = if ($ldapUser -and $ldapUser.ContainsKey('description')) { $ldapUser['description'] } else { $null }
        if ($descValue) {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "Directory 'description' still has value after recall: $descValue" }
            throw "Test 5 Assert 5 failed: Directory 'description' attribute still has value after recall export: $descValue"
        } else {
            Write-Host "  PASSED: Training attribute 'description' cleared from directory after recall export" -ForegroundColor Green
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
    # Tests 8-11: Deprovisioning Action permutations (issue #655)
    # =============================================================================================================
    # When an MVO is deleted, downstream deprovisioning is driven by each export Synchronisation Rule's
    # OutboundDeprovisionAction, regardless of how the CSO was joined (Provisioned or Joined):
    #   - Delete:     stage a delete Pending Export; the directory account is removed
    #   - Disconnect: break the join only; the directory account is left in place
    # All four tests use WhenAuthoritativeSourceDisconnected + GracePeriod=0 as the deletion trigger
    # so the cascade runs in the sync path (Test 4 covers the housekeeping path).
    # =============================================================================================================
    if ($Step -in @("DeprovisionProvisionedDelete", "DeprovisionActions", "All")) {
        Write-TestSection "Test 8: Deprovisioning Action - Provisioned CSO + Delete"
        Write-Host "CSO origin: Provisioned, OutboundDeprovisionAction: Delete" -ForegroundColor Gray
        Write-Host "Expected: delete Pending Export staged, directory account removed" -ForegroundColor Gray
        Write-Host ""

        Invoke-DeprovisionActionTest -Config $config -UserObjectTypeId $userObjectType.id `
            -CsoOrigin Provisioned -DeprovisionAction Delete `
            -EmployeeId "DEPROV001" -SamAccountName "test.deprov.provdelete" `
            -DisplayName "Test Deprov ProvDelete" -TestName "Test8" `
            -StepName "DeprovisionProvisionedDelete"
    }

    if ($Step -in @("DeprovisionProvisionedDisconnect", "DeprovisionActions", "All")) {
        Write-TestSection "Test 9: Deprovisioning Action - Provisioned CSO + Disconnect"
        Write-Host "CSO origin: Provisioned, OutboundDeprovisionAction: Disconnect" -ForegroundColor Gray
        Write-Host "Expected: no delete Pending Export, CSO disconnected, directory account left in place" -ForegroundColor Gray
        Write-Host ""

        Invoke-DeprovisionActionTest -Config $config -UserObjectTypeId $userObjectType.id `
            -CsoOrigin Provisioned -DeprovisionAction Disconnect `
            -EmployeeId "DEPROV002" -SamAccountName "test.deprov.provdisc" `
            -DisplayName "Test Deprov ProvDisc" -TestName "Test9" `
            -StepName "DeprovisionProvisionedDisconnect"
    }

    if ($Step -in @("DeprovisionJoinedDelete", "DeprovisionActions", "All")) {
        Write-TestSection "Test 10: Deprovisioning Action - Joined CSO + Delete (issue #655 headline)"
        Write-Host "CSO origin: Joined (via export matching), OutboundDeprovisionAction: Delete" -ForegroundColor Gray
        Write-Host "Expected: delete Pending Export staged, directory account removed" -ForegroundColor Gray
        Write-Host ""

        Invoke-DeprovisionActionTest -Config $config -UserObjectTypeId $userObjectType.id `
            -CsoOrigin Joined -DeprovisionAction Delete `
            -EmployeeId "DEPROV003" -SamAccountName "test.deprov.joindelete" `
            -DisplayName "Test Deprov JoinDelete" -TestName "Test10" `
            -StepName "DeprovisionJoinedDelete"
    }

    if ($Step -in @("DeprovisionJoinedDisconnect", "DeprovisionActions", "All")) {
        Write-TestSection "Test 11: Deprovisioning Action - Joined CSO + Disconnect"
        Write-Host "CSO origin: Joined (via export matching), OutboundDeprovisionAction: Disconnect" -ForegroundColor Gray
        Write-Host "Expected: no delete Pending Export, CSO disconnected, directory account left in place" -ForegroundColor Gray
        Write-Host ""

        Invoke-DeprovisionActionTest -Config $config -UserObjectTypeId $userObjectType.id `
            -CsoOrigin Joined -DeprovisionAction Disconnect `
            -EmployeeId "DEPROV004" -SamAccountName "test.deprov.joindisc" `
            -DisplayName "Test Deprov JoinDisc" -TestName "Test11" `
            -StepName "DeprovisionJoinedDisconnect"
    }

    # =============================================================================================================
    # Test 12: WhenAuthoritativeSourceDisconnected + All Sources Trigger Mode (issue #119)
    # =============================================================================================================
    # Two authoritative sources are selected (HR CSV + Training CSV) with AllSourcesDisconnect: the
    # MVO must only be marked for deletion once NO selected source retains a joined CSO. The LDAP
    # target CSO never blocks or triggers deletion (it is not a listed source). A 1-hour grace
    # period keeps housekeeping from deleting the MVO mid-test, and the deletion markers
    # (DeletionTriggeredBySystemId/Name, decision-time policy snapshot) are asserted directly
    # against the database.
    # =============================================================================================================
    if ($Step -in @("AuthoritativeAllSources", "All")) {
        Write-TestSection "Test 12: WhenAuthoritativeSourceDisconnected + All Sources Trigger Mode"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, TriggerMode: AllSourcesDisconnect" -ForegroundColor Gray
        Write-Host "Authoritative sources: CSV (HR System) + Training, GracePeriod: 1 hour" -ForegroundColor Gray
        Write-Host "Expected: first source disconnecting does NOT mark the MVO; the last one does," -ForegroundColor Gray
        Write-Host "          recording the triggering system; a listed source rejoining cancels" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - both sources authoritative, All mode, recall disabled on CSV
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::FromHours(1)) `
            -DeletionTriggerConnectedSystemIds @($config.CSVSystemId, $config.TrainingSystemId) `
            -DeletionTriggerMode "AllSourcesDisconnect" `
            -RemoveContributedAttributesOnObsoletion $false

        # Disable recall on the Training object type too (a prior test may have enabled it);
        # recall is irrelevant to deletion tests and would recall the display name used for lookups
        $trainingObjectTypes = Get-JIMConnectedSystem -Id $config.TrainingSystemId -ObjectTypes
        $trainingRecordType = $trainingObjectTypes | Where-Object { $_.name -match "^(trainingRecord|record)$" } | Select-Object -First 1
        if ($trainingRecordType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $config.TrainingSystemId -ObjectTypeId $trainingRecordType.id `
                -RemoveContributedAttributesOnObsoletion $false
        }

        # Provision a test user via HR CSV (creates MVO + CSV CSO + LDAP CSO), then join Training
        $test12Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "TRIG001" `
            -SamAccountName "test.trigger.allsrc" `
            -DisplayName "Test Trigger AllSrc" `
            -TestName "Test12"

        $test12MvoId = $test12Mvo.id
        Write-Host "  MVO ID: $test12MvoId" -ForegroundColor Gray

        Invoke-ProvisionTrainingData -Config $config `
            -EmployeeId "TRIG001" `
            -SamAccountName "test.trigger.allsrc" `
            -TestName "Test12"

        # Act 1: remove the training record - the FIRST listed source disconnects
        Invoke-RemoveTrainingData -Config $config -EmployeeId "TRIG001" -TestName "Test12"
        Start-Sleep -Seconds 3

        # Assert 1: MVO NOT marked for deletion (the HR CSV source still holds a joined CSO)
        $markers = Get-MvoDeletionMarkers -MvoId $test12MvoId
        if ($markers.IsMarkedForDeletion) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "MVO marked for deletion after first source disconnect (All mode should wait for all sources)" }
            throw "Test 12 Assert 1 failed: MVO was marked for deletion after only one of two sources disconnected (All sources mode)"
        }
        Write-Host "  PASSED: MVO not marked after first source disconnect (HR source still connected)" -ForegroundColor Green

        # Act 2: remove the HR user (CSV-only cycle) - the LAST listed source disconnects.
        # The LDAP target CSO remains joined but is not a listed source, so it must not block deletion.
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.trigger.allsrc" -TestName "Test12"
        Start-Sleep -Seconds 3

        # Assert 2: MVO still exists (grace period) and is marked for pending deletion
        if (-not (Test-MvoExistsById -MvoId $test12MvoId)) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "MVO deleted despite 1-hour grace period" }
            throw "Test 12 Assert 2 failed: MVO $test12MvoId was deleted despite the 1-hour grace period"
        }
        $mvoDetail = Get-JIMMetaverseObject -Id $test12MvoId -ErrorAction SilentlyContinue
        $isPending = $mvoDetail -and ($mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') -and $mvoDetail.isPendingDeletion
        if (-not $isPending) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "MVO not marked for deletion after all sources disconnected" }
            throw "Test 12 Assert 2 failed: MVO isPendingDeletion=false after all listed sources disconnected (All sources mode)"
        }
        Write-Host "  PASSED: MVO marked for deletion once no listed source remained connected" -ForegroundColor Green

        # Assert 3: the triggering system and decision-time policy snapshot are recorded on the MVO
        $markers = Get-MvoDeletionMarkers -MvoId $test12MvoId
        if ($markers.TriggeredBySystemId -ne $config.CSVSystemId) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "DeletionTriggeredBySystemId=$($markers.TriggeredBySystemId), expected $($config.CSVSystemId)" }
            throw "Test 12 Assert 3 failed: expected DeletionTriggeredBySystemId=$($config.CSVSystemId) (HR CSV) but found '$($markers.TriggeredBySystemId)'"
        }
        if (-not $markers.TriggeredBySystemName) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "DeletionTriggeredBySystemName not recorded" }
            throw "Test 12 Assert 3 failed: DeletionTriggeredBySystemName was not recorded"
        }
        if (-not $markers.HasPolicySnapshot) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "Decision-time policy snapshot not persisted on the MVO" }
            throw "Test 12 Assert 3 failed: DeletionPolicySnapshotJson was not persisted at mark-time"
        }
        Write-Host "  PASSED: Deletion trigger recorded (system $($markers.TriggeredBySystemId) '$($markers.TriggeredBySystemName)') with policy snapshot" -ForegroundColor Green

        # Act 3: re-add the HR user - in All mode ANY listed source rejoining falsifies the
        # "all sources gone" condition, so the scheduled deletion must be cancelled
        $test12MvoAfter = Invoke-ProvisionUser -Config $config `
            -EmployeeId "TRIG001" `
            -SamAccountName "test.trigger.allsrc" `
            -DisplayName "Test Trigger AllSrc" `
            -TestName "Test12 rejoin"
        Start-Sleep -Seconds 3

        # Assert 4: the rejoin landed on the SAME MVO and cleared every deletion marker
        if ($test12MvoAfter.id -ne $test12MvoId) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "Rejoin projected a new MVO instead of joining the marked one" }
            throw "Test 12 Assert 4 failed: expected rejoin to land on MVO $test12MvoId but found $($test12MvoAfter.id)"
        }
        $markers = Get-MvoDeletionMarkers -MvoId $test12MvoId
        if ($markers.IsMarkedForDeletion -or $null -ne $markers.TriggeredBySystemId -or $markers.HasPolicySnapshot) {
            $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $false; Error = "Deletion markers not cleared after listed source rejoined" }
            throw "Test 12 Assert 4 failed: deletion markers were not cleared after a listed source rejoined (All sources mode)"
        }
        Write-Host "  PASSED: Scheduled deletion cancelled and all deletion markers cleared on rejoin" -ForegroundColor Green

        $testResults.Steps += @{ Name = "AuthoritativeAllSources"; Success = $true }
    }

    # =============================================================================================================
    # Test 13: Mode-aware grace period rejoin cancellation - Specific mode (issue #119)
    # =============================================================================================================
    # SpecificSourcesDisconnect with two listed sources: any listed source disconnecting schedules
    # deletion (asserted while the other source is still joined, the contrast with Test 12), and a
    # rejoin only cancels the scheduled deletion when the rejoining system is the RECORDED
    # triggering system. A listed-but-non-triggering source rejoining must not rescue the object.
    # Both sources are disconnected (the second disconnect re-marks, so the recorded trigger is the
    # most recent disconnector: the Training system), then each is rejoined in turn.
    # =============================================================================================================
    if ($Step -in @("AuthoritativeRejoinCancellation", "All")) {
        Write-TestSection "Test 13: Mode-Aware Rejoin Cancellation (Specific Sources Mode)"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, TriggerMode: SpecificSourcesDisconnect" -ForegroundColor Gray
        Write-Host "Authoritative sources: CSV (HR System) + Training, GracePeriod: 1 hour" -ForegroundColor Gray
        Write-Host "Expected: a non-triggering listed source rejoining does NOT cancel the scheduled" -ForegroundColor Gray
        Write-Host "          deletion; the recorded triggering system rejoining does" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - both sources authoritative, Specific mode, recall disabled on CSV
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::FromHours(1)) `
            -DeletionTriggerConnectedSystemIds @($config.CSVSystemId, $config.TrainingSystemId) `
            -DeletionTriggerMode "SpecificSourcesDisconnect" `
            -RemoveContributedAttributesOnObsoletion $false

        # Disable recall on the Training object type too; recall is irrelevant to deletion tests
        $trainingObjectTypes = Get-JIMConnectedSystem -Id $config.TrainingSystemId -ObjectTypes
        $trainingRecordType = $trainingObjectTypes | Where-Object { $_.name -match "^(trainingRecord|record)$" } | Select-Object -First 1
        if ($trainingRecordType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $config.TrainingSystemId -ObjectTypeId $trainingRecordType.id `
                -RemoveContributedAttributesOnObsoletion $false
        }

        # Provision a test user via HR CSV, then join Training
        $test13Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "TRIG002" `
            -SamAccountName "test.trigger.rejoin" `
            -DisplayName "Test Trigger Rejoin" `
            -TestName "Test13"

        $test13MvoId = $test13Mvo.id
        Write-Host "  MVO ID: $test13MvoId" -ForegroundColor Gray

        Invoke-ProvisionTrainingData -Config $config `
            -EmployeeId "TRIG002" `
            -SamAccountName "test.trigger.rejoin" `
            -TestName "Test13"

        # Act 1: remove the HR user (CSV-only cycle) - in Specific mode ANY listed source
        # disconnecting schedules deletion, even though the Training source is still joined
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.trigger.rejoin" -TestName "Test13"
        Start-Sleep -Seconds 3

        # Assert 1: MVO marked with the HR CSV system recorded as the trigger (contrast with
        # Test 12 Assert 1, where the same one-of-two disconnect did NOT mark in All mode)
        $markers = Get-MvoDeletionMarkers -MvoId $test13MvoId
        if (-not $markers.IsMarkedForDeletion) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "MVO not marked when a listed source disconnected (Specific mode)" }
            throw "Test 13 Assert 1 failed: MVO was not marked for deletion when a listed source disconnected (Specific sources mode)"
        }
        if ($markers.TriggeredBySystemId -ne $config.CSVSystemId) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "DeletionTriggeredBySystemId=$($markers.TriggeredBySystemId), expected $($config.CSVSystemId)" }
            throw "Test 13 Assert 1 failed: expected DeletionTriggeredBySystemId=$($config.CSVSystemId) (HR CSV) but found '$($markers.TriggeredBySystemId)'"
        }
        Write-Host "  PASSED: MVO marked on single source disconnect with trigger recorded (Specific mode)" -ForegroundColor Green

        # Act 2: remove the training record too - the disconnect re-marks the MVO, so the recorded
        # trigger becomes the most recent disconnector (the Training system)
        Invoke-RemoveTrainingData -Config $config -EmployeeId "TRIG002" -TestName "Test13"
        Start-Sleep -Seconds 3

        $markers = Get-MvoDeletionMarkers -MvoId $test13MvoId
        if (-not $markers.IsMarkedForDeletion -or $markers.TriggeredBySystemId -ne $config.TrainingSystemId) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "Expected re-mark with DeletionTriggeredBySystemId=$($config.TrainingSystemId), found '$($markers.TriggeredBySystemId)'" }
            throw "Test 13 Assert 2 failed: expected the second disconnect to record DeletionTriggeredBySystemId=$($config.TrainingSystemId) (Training) but found '$($markers.TriggeredBySystemId)'"
        }
        Write-Host "  PASSED: Second source disconnect re-marked the MVO (trigger now Training system)" -ForegroundColor Green

        # Act 3: re-add the HR user - a LISTED source, but NOT the recorded triggering system.
        # Its rejoin must NOT cancel the scheduled deletion (the triggering disconnection stands).
        $test13MvoAfter = Invoke-ProvisionUser -Config $config `
            -EmployeeId "TRIG002" `
            -SamAccountName "test.trigger.rejoin" `
            -DisplayName "Test Trigger Rejoin" `
            -TestName "Test13 non-trigger rejoin"
        Start-Sleep -Seconds 3

        # Assert 3: same MVO, still marked, trigger fields unchanged
        if ($test13MvoAfter.id -ne $test13MvoId) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "Rejoin projected a new MVO instead of joining the marked one" }
            throw "Test 13 Assert 3 failed: expected rejoin to land on MVO $test13MvoId but found $($test13MvoAfter.id)"
        }
        $markers = Get-MvoDeletionMarkers -MvoId $test13MvoId
        if (-not $markers.IsMarkedForDeletion) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "Deletion cancelled by a non-triggering source rejoin" }
            throw "Test 13 Assert 3 failed: the scheduled deletion was cancelled by a listed source that did NOT trigger it (Specific sources mode)"
        }
        if ($markers.TriggeredBySystemId -ne $config.TrainingSystemId) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "Trigger fields changed by a non-triggering source rejoin" }
            throw "Test 13 Assert 3 failed: DeletionTriggeredBySystemId changed to '$($markers.TriggeredBySystemId)' after a non-triggering source rejoined"
        }
        $mvoDetail = Get-JIMMetaverseObject -Id $test13MvoId -ErrorAction SilentlyContinue
        $isPending = $mvoDetail -and ($mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') -and $mvoDetail.isPendingDeletion
        if (-not $isPending) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "isPendingDeletion=false after non-triggering source rejoin" }
            throw "Test 13 Assert 3 failed: MVO isPendingDeletion=false after a non-triggering source rejoined (deletion should still be scheduled)"
        }
        Write-Host "  PASSED: Non-triggering listed source rejoin did NOT cancel the scheduled deletion" -ForegroundColor Green

        # Act 4: re-add the training record - the RECORDED triggering system rejoins, undoing the
        # disconnection that caused the scheduling, so the deletion must be cancelled
        Invoke-ProvisionTrainingData -Config $config `
            -EmployeeId "TRIG002" `
            -SamAccountName "test.trigger.rejoin" `
            -TestName "Test13 trigger rejoin"
        Start-Sleep -Seconds 3

        # Assert 4: deletion cancelled, every marker cleared
        $markers = Get-MvoDeletionMarkers -MvoId $test13MvoId
        if ($markers.IsMarkedForDeletion -or $null -ne $markers.TriggeredBySystemId -or $markers.HasPolicySnapshot) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "Deletion markers not cleared after the triggering system rejoined" }
            throw "Test 13 Assert 4 failed: deletion markers were not cleared after the recorded triggering system rejoined"
        }
        $mvoDetail = Get-JIMMetaverseObject -Id $test13MvoId -ErrorAction SilentlyContinue
        $isPending = $mvoDetail -and ($mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') -and $mvoDetail.isPendingDeletion
        if ($isPending) {
            $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $false; Error = "isPendingDeletion=true after the triggering system rejoined" }
            throw "Test 13 Assert 4 failed: MVO isPendingDeletion=true after the recorded triggering system rejoined"
        }
        Write-Host "  PASSED: Triggering system rejoin cancelled the deletion and cleared all markers" -ForegroundColor Green

        $testResults.Steps += @{ Name = "AuthoritativeRejoinCancellation"; Success = $true }
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

        # Reset the export rule's Deprovisioning Action to Delete (the Setup-Scenario1 baseline);
        # the permutation tests may have left it as Disconnect
        Set-ExportRuleDeprovisionAction -Config $config -Action Delete

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
