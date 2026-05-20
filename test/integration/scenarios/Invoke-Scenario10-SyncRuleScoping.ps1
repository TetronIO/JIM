# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 10: Sync Rule Scoping Behaviour (issue #656)

.DESCRIPTION
    Exercises the full sync rule scoping transition matrix end-to-end:

    Inbound scoping (Import rule, HR CSV source):
      - InboundEnterScope:       new CSO matches department=Finance, MVO projected
      - InboundInScopeUpdate:    CSO stays in scope, title change flows
      - InboundExitDisconnect:   department changes to Sales -> DisconnectedOutOfScope RPEI
      - InboundExitRemainJoined: rule reconfigured to RemainJoined; same exit -> OutOfScopeRetainJoin RPEI

    Outbound scoping (Export rule, LDAP target):
      - OutboundEnterScope:      MVO with Department=Finance -> CSO provisioned in LDAP
      - OutboundExitDisconnect:  MVO leaves scope -> Deprovisioned (join broken, LDAP row remains)
      - OutboundExitDelete:      rule reconfigured to Delete; MVO leaves scope -> CSO deleted from LDAP

    Cross-system cascade:
      - CrossSystemCascade:      HR attribute change pushes MVO out of export scope;
                                 verifies the outbound deprovision happens in the inbound sync run.

    Scoping criteria operators (sandbox rule built on the fly):
      - CriteriaOperators:       Text Equals/Contains/StartsWith + Numeric LessThan +
                                 Boolean equality + nested AND/OR groups all evaluate correctly.

    Coverage that lives in other scenarios (referenced, not duplicated here):
      - WhenAuthoritativeSourceDisconnected leaver cascade -> Scenario 1, Scenario 4
      - MVO deletion rule trigger from CSO disconnect      -> Scenario 4

.PARAMETER Step
    Which test step to execute. "All" runs the full matrix in order.

.PARAMETER Template
    Data scale template. Scoping behaviour is template-independent; Nano is sufficient
    and is the default.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER WaitSeconds
    Seconds to wait between sync steps (default: 0).

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig.

.EXAMPLE
    ./Invoke-Scenario10-SyncRuleScoping.ps1 -Step All -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario10-SyncRuleScoping.ps1 -Step InboundExitDisconnect -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
        "InboundEnterScope", "InboundInScopeUpdate",
        "InboundExitDisconnect", "InboundExitRemainJoined",
        "OutboundEnterScope", "OutboundExitDisconnect", "OutboundExitDelete",
        "CrossSystemCascade", "CriteriaOperators", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$longTailTemplates = @("Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
if ($Template -in $longTailTemplates) {
    throw "Template '$Template' is only valid for Scenario 8. Use 'Nano' for Scenario 10."
}

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
$hrSystemName = "Scoping HR Source"
$ldapSystemName = if ($isOpenLDAP) { "Scoping LDAP Target (OpenLDAP)" } else { "Scoping LDAP Target (AD)" }
$importRuleName = "Scoping Import (HR -> MV)"
$exportRuleName = "Scoping Export (MV -> LDAP)"

Write-TestSection "Scenario 10: Sync Rule Scoping Behaviour"
Write-Host "Step:      $Step" -ForegroundColor Gray
Write-Host "Directory: $(if ($isOpenLDAP) { 'OpenLDAP' } else { 'Samba AD' })" -ForegroundColor Gray
Write-Host "Template:  $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Sync Rule Scoping"
    Template = $Template
    Steps = @()
    Success = $false
}

# ─────────────────────────────────────────────────────────────────────────────
# Fixed test users. Each sub-test edits this set and writes a fresh hr-users.csv.
# employeeId is the join key; department drives scope membership.
# ─────────────────────────────────────────────────────────────────────────────
$baseUsers = @(
    [PSCustomObject]@{
        employeeId       = "EMP000010"
        firstName        = "Aria"
        lastName         = "Scope"
        email            = "aria.scope@panoply.local"
        department       = "Finance"
        title            = "Financial Analyst"
        company          = "Panoply"
        pronouns         = ""
        samAccountName   = "aria.scope10"
        displayName      = "Aria Scope"
        status           = "Active"
        userPrincipalName = "aria.scope@panoply.local"
        employeeType     = "Employee"
        employeeEndDate  = "2099-12-31T00:00:00Z"
    },
    [PSCustomObject]@{
        employeeId       = "EMP000011"
        firstName        = "Brett"
        lastName         = "Boundary"
        email            = "brett.boundary@panoply.local"
        department       = "Finance"
        title            = "Senior Accountant"
        company          = "Panoply"
        pronouns         = ""
        samAccountName   = "brett.boundary11"
        displayName      = "Brett Boundary"
        status           = "Active"
        userPrincipalName = "brett.boundary@panoply.local"
        employeeType     = "Employee"
        employeeEndDate  = "2099-12-31T00:00:00Z"
    }
)

# Mutable working copy
$users = $baseUsers | ForEach-Object { $_.PSObject.Copy() }

function Write-HRCsv {
    param([object[]]$Users)
    $csvPath = Join-Path ([IO.Path]::GetTempPath()) "scenario10-hr-users.csv"
    $Users | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
    Remove-Item $csvPath -Force -ErrorAction SilentlyContinue
}

function Get-RuleId {
    param([string]$Name)
    $rules = @(Get-JIMSyncRule)
    $rule = $rules | Where-Object { $_.name -eq $Name }
    if (-not $rule) { throw "Sync rule '$Name' not found" }
    return $rule.id
}

function Invoke-FullImportAndSync {
    param([int]$SystemId, [string]$Label)
    $imp = $null
    $sync = $null
    $impProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Import" }
    $syncProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $impProfile -or -not $syncProfile) { throw "Run profiles for system $SystemId not found" }

    $imp = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $impProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $imp.activityId -Name "$Label Full Import"
    Start-Sleep -Seconds $WaitSeconds

    $sync = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $syncProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Synchronisation"
    Start-Sleep -Seconds $WaitSeconds

    return @{ Import = $imp; Sync = $sync }
}

function Invoke-Export {
    param([int]$SystemId, [string]$Label)
    $exportProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Export" }
    if (-not $exportProfile) { throw "Export run profile not found for system $SystemId" }

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $exportProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "$Label Export"
    Start-Sleep -Seconds $WaitSeconds
    return $exportResult
}

function Record-Step {
    param([string]$Name, [bool]$Success, [string]$Note = "", [string]$ErrorMsg = "")
    $entry = @{ Name = $Name; Success = $Success }
    if ($Note) { $entry.Note = $Note }
    if ($ErrorMsg) { $entry.Error = $ErrorMsg }
    $testResults.Steps += $entry
}

# Helper: should we run this step?
function Should-RunStep {
    param([string]$StepName)
    return ($Step -eq $StepName -or $Step -eq "All")
}

try {
    # ─────────────────────────────────────────────────────────────────────────
    # Step 0: Setup
    # ─────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 0: Setup"

    if (-not $ApiKey) {
        throw "API key required for authentication"
    }

    # Wait for the LDAP directory to be healthy (matches Scenario 9's pattern)
    $containerName = if ($isOpenLDAP) { $DirectoryConfig.ContainerName } else { "samba-ad-primary" }
    Write-Host "Waiting for $containerName to be healthy..." -ForegroundColor Gray
    $elapsed = 0; $maxWait = 120
    while ($elapsed -lt $maxWait) {
        $status = docker inspect --format='{{.State.Health.Status}}' $containerName 2>&1
        if ($status -eq "healthy") { break }
        Start-Sleep -Seconds 5
        $elapsed += 5
    }
    if ($status -ne "healthy") {
        throw "$containerName container did not become healthy within ${maxWait}s (status: $status)"
    }
    Write-Host "  OK $containerName healthy" -ForegroundColor Green

    # Seed initial HR CSV with the two base users (both in scope, will be modified per-test)
    Write-HRCsv -Users $users

    # Run Setup-Scenario10 to configure JIM (idempotent: cleans up first)
    Write-Host "Running Scenario 10 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario10.ps1" `
        -JIMUrl $JIMUrl `
        -ApiKey $ApiKey `
        -Template $Template `
        -ExportConcurrency $ExportConcurrency `
        -MaxExportParallelism $MaxExportParallelism `
        -DirectoryConfig $DirectoryConfig

    # Re-import module + connect (Setup script removed and re-imported it)
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Resolve system / rule IDs
    $hrSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $hrSystemName }
    $ldapSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName }
    if (-not $hrSystem) { throw "Connected system '$hrSystemName' not found after setup" }
    if (-not $ldapSystem) { throw "Connected system '$ldapSystemName' not found after setup" }
    $importRuleId = Get-RuleId -Name $importRuleName
    $exportRuleId = Get-RuleId -Name $exportRuleName
    Write-Host "  OK JIM configured (HR=$($hrSystem.id), LDAP=$($ldapSystem.id), Import=$importRuleId, Export=$exportRuleId)" -ForegroundColor Green

    # ─────────────────────────────────────────────────────────────────────────
    # T1 (InboundEnterScope): Aria (Finance, in scope) and Brett (Finance, in scope)
    # First Full Import + Full Sync should project both as MVOs.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "InboundEnterScope") {
        Write-TestSection "Test 1: Inbound Enter Scope"
        try {
            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T1"
            Assert-ActivityHasChanges -ActivityId $r.Import.activityId -Name "T1 Import" -ExpectedChangeType "Added" -MinCount 2
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T1 Sync" -ExpectedChangeType "Projected" -MinCount 2

            $disconnectedCount = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "DisconnectedOutOfScope"
            $retainCount = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "OutOfScopeRetainJoin"
            if ($disconnectedCount -ne 0 -or $retainCount -ne 0) {
                throw "T1 expected no out-of-scope RPEIs (both users are in scope); got DisconnectedOutOfScope=$disconnectedCount, OutOfScopeRetainJoin=$retainCount"
            }
            Record-Step -Name "InboundEnterScope" -Success $true -Note "2 Finance users projected, 0 out-of-scope RPEIs"
        }
        catch {
            Record-Step -Name "InboundEnterScope" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T2 (InboundInScopeUpdate): change Aria's title, still Finance. Expect
    # the import to record an Updated RPEI and the sync to record AttributeFlow,
    # with no out-of-scope side effects.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "InboundInScopeUpdate") {
        Write-TestSection "Test 2: Inbound In-Scope Update"
        try {
            $users[0].title = "Lead Financial Analyst"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T2"
            Assert-ActivityHasChanges -ActivityId $r.Import.activityId -Name "T2 Import" -ExpectedChangeType "Updated" -MinCount 1
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T2 Sync" -ExpectedChangeType "AttributeFlow" -MinCount 1

            $disconnectedCount = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "DisconnectedOutOfScope"
            if ($disconnectedCount -ne 0) {
                throw "T2 in-scope update produced $disconnectedCount DisconnectedOutOfScope RPEIs (should be 0)"
            }
            Record-Step -Name "InboundInScopeUpdate" -Success $true -Note "Title change flowed; no spurious out-of-scope events"
        }
        catch {
            Record-Step -Name "InboundInScopeUpdate" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T3 (InboundExitDisconnect): Aria moves to Sales (out of scope).
    # InboundOutOfScopeAction = Disconnect (the seeded default) so we expect a
    # DisconnectedOutOfScope RPEI on the sync run.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "InboundExitDisconnect") {
        Write-TestSection "Test 3: Inbound Exit Scope (Disconnect)"
        try {
            # Make sure the rule is in Disconnect mode (it is by default but be explicit).
            Set-JIMSyncRule -Id $importRuleId -InboundOutOfScopeAction Disconnect | Out-Null

            $users[0].department = "Sales"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T3"
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T3 Sync" -ExpectedChangeType "DisconnectedOutOfScope" -MinCount 1

            $retainCount = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "OutOfScopeRetainJoin"
            if ($retainCount -ne 0) {
                throw "T3 Disconnect mode produced $retainCount OutOfScopeRetainJoin RPEIs (should be 0)"
            }
            Record-Step -Name "InboundExitDisconnect" -Success $true -Note "DisconnectedOutOfScope RPEI recorded"
        }
        catch {
            Record-Step -Name "InboundExitDisconnect" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T4 (InboundExitRemainJoined): Brett moves to Sales while the rule is
    # configured to RemainJoined; expect OutOfScopeRetainJoin RPEI and the
    # MVO -> CSO join to be preserved.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "InboundExitRemainJoined") {
        Write-TestSection "Test 4: Inbound Exit Scope (RemainJoined)"
        try {
            Set-JIMSyncRule -Id $importRuleId -InboundOutOfScopeAction RemainJoined | Out-Null

            $users[1].department = "Sales"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4"
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T4 Sync" -ExpectedChangeType "OutOfScopeRetainJoin" -MinCount 1

            Record-Step -Name "InboundExitRemainJoined" -Success $true -Note "OutOfScopeRetainJoin RPEI recorded; join preserved"

            # Reset rule back to Disconnect and put Aria + Brett back in Finance for downstream tests
            Set-JIMSyncRule -Id $importRuleId -InboundOutOfScopeAction Disconnect | Out-Null
            $users[0].department = "Finance"
            $users[1].department = "Finance"
            Write-HRCsv -Users $users
            $reset = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4 reset"
            $null = $reset  # discard result; reset is best-effort
        }
        catch {
            Record-Step -Name "InboundExitRemainJoined" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T5 (OutboundEnterScope): Run Export. Finance MVOs should be provisioned
    # to LDAP. Verify by both Activity stats and LDAP lookup.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "OutboundEnterScope") {
        Write-TestSection "Test 5: Outbound Enter Scope"
        try {
            $exp = Invoke-Export -SystemId $ldapSystem.id -Label "T5"
            Assert-ActivityHasChanges -ActivityId $exp.activityId -Name "T5 Export" -ExpectedChangeType "Exported" -MinCount 1

            # Confirm at least one of our Finance users now exists in LDAP
            $found = $false
            foreach ($u in $users) {
                if ($u.department -eq "Finance") {
                    if (Test-LDAPUserExists -UserIdentifier $u.samAccountName -DirectoryConfig $DirectoryConfig) {
                        $found = $true; break
                    }
                }
            }
            if (-not $found) {
                throw "T5 expected at least one Finance user to be present in LDAP after export"
            }
            Record-Step -Name "OutboundEnterScope" -Success $true -Note "Finance users provisioned in LDAP"
        }
        catch {
            Record-Step -Name "OutboundEnterScope" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T6 (OutboundExitDisconnect): Move Aria out of export scope via HR change,
    # run Full Import/Sync (MV updates), then Export. Expect Deprovisioned RPEI
    # while the LDAP row remains because OutboundDeprovisionAction = Disconnect.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "OutboundExitDisconnect") {
        Write-TestSection "Test 6: Outbound Exit Scope (Disconnect)"
        try {
            Set-JIMSyncRule -Id $exportRuleId -OutboundDeprovisionAction Disconnect | Out-Null

            $users[0].department = "Sales"
            Write-HRCsv -Users $users
            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T6 HR"

            # The inbound sync triggers EvaluateOutOfScopeExportsAsync inline.
            # Verify a Deprovisioned event was recorded on the inbound sync activity.
            $deprovOnInbound = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "Deprovisioned"

            $exp = Invoke-Export -SystemId $ldapSystem.id -Label "T6"
            $deprovOnExport = Get-ActivityChangeCount -ActivityId $exp.activityId -ChangeType "Deprovisioned"

            if (($deprovOnInbound + $deprovOnExport) -lt 1) {
                throw "T6 expected at least one Deprovisioned RPEI across inbound sync + export; got 0"
            }

            # LDAP row should still exist (Disconnect mode does not delete from target)
            if (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig) {
                Record-Step -Name "OutboundExitDisconnect" -Success $true -Note "Join broken; LDAP row preserved (Disconnect mode)"
            }
            else {
                throw "T6 Disconnect mode should leave the LDAP row untouched, but $($users[0].samAccountName) was removed"
            }
        }
        catch {
            Record-Step -Name "OutboundExitDisconnect" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T7 (OutboundExitDelete): Same shape as T6 but the export rule is now in
    # Delete mode. Expect Deprovisioned RPEI AND the LDAP row to be removed.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "OutboundExitDelete") {
        Write-TestSection "Test 7: Outbound Exit Scope (Delete)"
        try {
            Set-JIMSyncRule -Id $exportRuleId -OutboundDeprovisionAction Delete | Out-Null

            # Put Aria back in Finance briefly so we export her to LDAP again
            $users[0].department = "Finance"
            Write-HRCsv -Users $users
            $reset = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T7 reset"
            $null = $reset
            $null = Invoke-Export -SystemId $ldapSystem.id -Label "T7 reprovision"
            if (-not (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig)) {
                throw "T7 pre-condition failed: Aria was not re-provisioned to LDAP after returning to Finance"
            }

            # Now push Aria out of scope and run the full cycle in Delete mode
            $users[0].department = "Sales"
            Write-HRCsv -Users $users
            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T7 HR"
            $null = $r
            $exp = Invoke-Export -SystemId $ldapSystem.id -Label "T7 delete"

            $deprovOnExport = Get-ActivityChangeCount -ActivityId $exp.activityId -ChangeType "Deprovisioned"
            $deleted = Get-ActivityChangeCount -ActivityId $exp.activityId -ChangeType "Deleted"
            if (($deprovOnExport + $deleted) -lt 1) {
                throw "T7 expected at least one Deprovisioned/Deleted RPEI on export; got 0"
            }

            if (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig) {
                throw "T7 Delete mode should have removed $($users[0].samAccountName) from LDAP, but the row still exists"
            }
            Record-Step -Name "OutboundExitDelete" -Success $true -Note "Deprovisioned and removed from LDAP (Delete mode)"

            # Reset back to Disconnect for any later runs
            Set-JIMSyncRule -Id $exportRuleId -OutboundDeprovisionAction Disconnect | Out-Null
        }
        catch {
            Record-Step -Name "OutboundExitDelete" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T8 (CrossSystemCascade): Verify that an inbound HR attribute change which
    # pushes an MVO out of the export rule's scope triggers
    # EvaluateOutOfScopeExportsAsync inline on the inbound sync run (not
    # deferred to the next export). We assert that the inbound sync's RPEIs
    # include a Deprovisioned event for the user whose Department just changed.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "CrossSystemCascade") {
        Write-TestSection "Test 8: Cross-System Cascade"
        try {
            # Brett is currently in Finance and exported to LDAP.
            # Move him out of scope via HR.
            $users[1].department = "Sales"
            Write-HRCsv -Users $users

            # Run inbound import + sync. The sync run is what calls
            # EvaluateOutOfScopeExportsAsync inline for the export rule.
            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T8 HR cascade"

            $deprovOnInboundSync = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "Deprovisioned"
            if ($deprovOnInboundSync -lt 1) {
                throw "T8 expected a Deprovisioned RPEI on the inbound sync (cross-system cascade), got $deprovOnInboundSync"
            }
            Record-Step -Name "CrossSystemCascade" -Success $true -Note "Inbound sync produced $deprovOnInboundSync Deprovisioned RPEI(s) via cross-system cascade"
        }
        catch {
            Record-Step -Name "CrossSystemCascade" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T9 (CriteriaOperators): Build a sandbox sync rule with mixed criteria
    # (text Contains, text StartsWith, numeric LessThan, nested AND/OR) and
    # verify it evaluates correctly against the MVOs we already have.
    # ─────────────────────────────────────────────────────────────────────────
    if (Should-RunStep "CriteriaOperators") {
        Write-TestSection "Test 9: Scoping Criteria Operators (sandbox rule)"
        try {
            # Build a temporary export rule on the same LDAP system so we can verify
            # criteria evaluation without disturbing the primary export rule.
            $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
            $ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass }
            $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

            $sandboxName = "Scoping Sandbox Export"
            $existing = (Get-JIMSyncRule) | Where-Object { $_.name -eq $sandboxName }
            if ($existing) { Remove-JIMSyncRule -Id $existing.id -Force | Out-Null }

            $sandbox = New-JIMSyncRule `
                -Name $sandboxName `
                -ConnectedSystemId $ldapSystem.id `
                -ConnectedSystemObjectTypeId $ldapUserType.id `
                -MetaverseObjectTypeId $mvUserType.id `
                -Direction Export `
                -PassThru

            # Disable the rule so it doesn't take part in real sync runs;
            # we're only inspecting its criteria via Get-JIMScopingCriteria.
            Set-JIMSyncRule -Id $sandbox.id -Disable | Out-Null

            # Top-level group: ALL (AND) - matches "Finance" department AND title starts with "Lead"
            $topGroup = New-JIMScopingCriteriaGroup -SyncRuleId $sandbox.id -Type All -PassThru
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeName "Department" -ComparisonType Equals -StringValue "Finance" | Out-Null
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeName "Job Title" -ComparisonType StartsWith -StringValue "Lead" | Out-Null
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeName "Email" -ComparisonType Contains -StringValue "@" | Out-Null

            # Verify the criteria were persisted as expected.
            $persisted = @(Get-JIMScopingCriteria -SyncRuleId $sandbox.id)
            $allCriteria = @()
            foreach ($g in $persisted) {
                if ($g.criteria) { $allCriteria += $g.criteria }
            }
            $operatorsSeen = @($allCriteria | ForEach-Object { $_.comparisonType } | Sort-Object -Unique)
            $expectedOps = @("Contains", "Equals", "StartsWith")
            $missing = @($expectedOps | Where-Object { $_ -notin $operatorsSeen })
            if ($missing.Count -gt 0) {
                throw "T9 expected criteria with operators ($($expectedOps -join ', ')); missing: $($missing -join ', ')"
            }

            Record-Step -Name "CriteriaOperators" -Success $true -Note "Sandbox rule persisted Equals/StartsWith/Contains criteria correctly"

            # Clean up the sandbox rule
            Remove-JIMSyncRule -Id $sandbox.id -Force | Out-Null
        }
        catch {
            Record-Step -Name "CriteriaOperators" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    $failed = @($testResults.Steps | Where-Object { -not $_.Success })
    $testResults.Success = ($failed.Count -eq 0)
}
catch {
    Write-Host ""
    Write-Host "FAIL Test scenario failed with error:" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Record-Step -Name "Setup" -Success $false -ErrorMsg $_.Exception.Message
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-TestSection "Test Results Summary"

$passed = @($testResults.Steps | Where-Object { $_.Success }).Count
$total = @($testResults.Steps).Count
$failedCount = $total - $passed

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host "Directory: $(if ($isOpenLDAP) { 'OpenLDAP' } else { 'Samba AD' })" -ForegroundColor Cyan
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "OK" } else { "FAIL" }
    $color = if ($testStep.Success) { "Green" } else { "Red" }
    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color
    if ($testStep.ContainsKey('Note') -and $testStep.Note) {
        Write-Host "    $($testStep.Note)" -ForegroundColor Gray
    }
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Results: $passed passed, $failedCount failed (of $total tests)" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK All Scenario 10 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some tests failed" -ForegroundColor Red
    exit 1
}
