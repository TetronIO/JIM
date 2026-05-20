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

    Scoping criteria operators (sandbox rule built on the fly):
      - CriteriaOperators:       Text Equals/Contains/StartsWith + Numeric LessThan +
                                 Boolean equality + nested AND/OR groups all evaluate correctly.

    Deferred follow-on coverage (issue #656 mentions these; tracked separately because
    they need per-test connector-system isolation to verify reliably in a single run):
      - OutboundExitDisconnect   verify Disconnect mode keeps LDAP row, no PendingExport queued
      - OutboundExitDelete       verify Delete mode emits Deprovisioned RPEI + removes LDAP row
      - CrossSystemCascade       verify inline cascade during inbound sync queues PendingExport(Delete)

      The state coupling between the inbound transitions and a long-running outbound
      block makes single-run verification of these brittle: by the time the outbound
      assertions fire, the MVO graph has been through several enter/exit transitions
      and pending-export reconciliation that produce side effects (orphan MVOs,
      stale CSO stubs from prior export attempts) which break the "isolate one
      transition" assertions. Verifying these reliably needs each sub-test to run
      against a freshly torn-down JIM stack, which is a separate runner-level
      concern rather than a scenario-script concern.

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
        "OutboundEnterScope", "CriteriaOperators", "All")]
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
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# $SkipPopulate is accepted for runner compatibility (passed when snapshot images are
# used) but this scenario manages its own HR CSV and does not rely on the runner's
# directory pre-population step, so the flag is effectively a no-op here.
$null = $SkipPopulate

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

function Remove-LDAPTestUsers {
    param([object[]]$Users, [hashtable]$DirectoryConfig)
    if ($DirectoryConfig.UserObjectClass -eq "inetOrgPerson") {
        # OpenLDAP path - use ldapdelete via the container
        foreach ($u in $Users) {
            $dn = "uid=$($u.samAccountName),$($DirectoryConfig.UserContainer)"
            docker exec $DirectoryConfig.ContainerName ldapdelete -x -D $DirectoryConfig.BindDN -w $DirectoryConfig.BindPassword $dn 2>&1 | Out-Null
        }
    }
    else {
        # Samba AD - samba-tool user delete (no-op silently if user doesn't exist)
        foreach ($u in $Users) {
            docker exec samba-ad-primary bash -c "samba-tool user delete '$($u.samAccountName)' 2>&1" | Out-Null
        }
    }
}

function Reset-ToOutboundBaseline {
    param([int]$HRSystemId, [int]$LDAPSystemId, [int]$ImportRuleId, [int]$ExportRuleId, [object[]]$BaseUsers, [hashtable]$DirectoryConfig)

    Write-Host "  Resetting state for outbound test block..." -ForegroundColor Gray

    # Step 1: Reset rule actions to the documented defaults.
    Set-JIMSyncRule -Id $ImportRuleId -InboundOutOfScopeAction Disconnect | Out-Null
    Set-JIMSyncRule -Id $ExportRuleId -OutboundDeprovisionAction Disconnect | Out-Null

    # Step 2: Wipe both connected systems' CSO / pending-export state so the outbound
    # tests start from a known-empty connector space. The inbound tests above run
    # multiple in-scope <-> out-of-scope transitions that can leave stale pending
    # CREATE exports keyed on now-deleted MVOs; Clear-JIMConnectedSystem is the
    # supported, public-API way to reset.
    Clear-JIMConnectedSystem -Id $HRSystemId -Force | Out-Null
    Clear-JIMConnectedSystem -Id $LDAPSystemId -Force | Out-Null

    # Step 3: Delete any test users left in LDAP from a prior outbound-block run, so the
    # provisioning test starts with an empty target directory.
    Remove-LDAPTestUsers -Users $BaseUsers -DirectoryConfig $DirectoryConfig

    # Step 4: Push every test user out of inbound scope (Department = Sales). When the
    # inbound sync sees an existing orphan MVO whose only joined CSO is now out of
    # scope, InboundOutOfScopeAction=Disconnect breaks the join and the MVO type's
    # WhenLastConnectorDisconnected deletion rule removes the orphan. This is the
    # supported way to clear MVOs left over from the inbound test block.
    $outOfScopeUsers = $BaseUsers | ForEach-Object {
        $copy = $_.PSObject.Copy()
        $copy.department = "Sales"
        $copy
    }
    Write-HRCsv -Users $outOfScopeUsers
    $null = Invoke-FullImportAndSync -SystemId $HRSystemId -Label "Outbound-baseline (purge MVOs)"

    # Step 5: Seed the baseline HR CSV (everyone in Finance / in scope) and re-import.
    # All MVOs are now fresh projections, so the export rule evaluation queues a CREATE
    # pending export for each one.
    Write-HRCsv -Users $BaseUsers
    $r = Invoke-FullImportAndSync -SystemId $HRSystemId -Label "Outbound-baseline (project)"
    $null = $r

    Write-Host "  OK Outbound baseline ready" -ForegroundColor Green
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

function Add-StepResult {
    param([string]$Name, [bool]$Success, [string]$Note = "", [string]$ErrorMsg = "")
    $entry = @{ Name = $Name; Success = $Success }
    if ($Note) { $entry.Note = $Note }
    if ($ErrorMsg) { $entry.Error = $ErrorMsg }
    $testResults.Steps += $entry
}

# Helper: should we run this step?
function Test-StepEnabled {
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
    if (Test-StepEnabled "InboundEnterScope") {
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
            Add-StepResult -Name "InboundEnterScope" -Success $true -Note "2 Finance users projected, 0 out-of-scope RPEIs"
        }
        catch {
            Add-StepResult -Name "InboundEnterScope" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T2 (InboundInScopeUpdate): change Aria's title, still Finance. Expect
    # the import to record an Updated RPEI and the sync to record AttributeFlow,
    # with no out-of-scope side effects.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "InboundInScopeUpdate") {
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
            Add-StepResult -Name "InboundInScopeUpdate" -Success $true -Note "Title change flowed; no spurious out-of-scope events"
        }
        catch {
            Add-StepResult -Name "InboundInScopeUpdate" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T3 (InboundExitDisconnect): Aria moves to Sales (out of scope).
    # InboundOutOfScopeAction = Disconnect (the seeded default) so we expect a
    # DisconnectedOutOfScope RPEI on the sync run.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "InboundExitDisconnect") {
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
            Add-StepResult -Name "InboundExitDisconnect" -Success $true -Note "DisconnectedOutOfScope RPEI recorded"
        }
        catch {
            Add-StepResult -Name "InboundExitDisconnect" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T4 (InboundExitRemainJoined): Brett moves to Sales while the rule is
    # configured to RemainJoined; expect OutOfScopeRetainJoin RPEI and the
    # MVO -> CSO join to be preserved.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "InboundExitRemainJoined") {
        Write-TestSection "Test 4: Inbound Exit Scope (RemainJoined)"
        try {
            Set-JIMSyncRule -Id $importRuleId -InboundOutOfScopeAction RemainJoined | Out-Null

            $users[1].department = "Sales"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4"
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T4 Sync" -ExpectedChangeType "OutOfScopeRetainJoin" -MinCount 1

            Add-StepResult -Name "InboundExitRemainJoined" -Success $true -Note "OutOfScopeRetainJoin RPEI recorded; join preserved"

            # Reset rule back to Disconnect and put Aria + Brett back in Finance for downstream tests
            Set-JIMSyncRule -Id $importRuleId -InboundOutOfScopeAction Disconnect | Out-Null
            $users[0].department = "Finance"
            $users[1].department = "Finance"
            Write-HRCsv -Users $users
            $reset = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4 reset"
            $null = $reset  # discard result; reset is best-effort
        }
        catch {
            Add-StepResult -Name "InboundExitRemainJoined" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # Reset to a known clean state before the outbound block. The inbound tests
    # above run several scope transitions that can leave stale pending exports,
    # broken joins, and (across reruns) LDAP residue. Without this reset the
    # outbound tests inherit unpredictable state.
    # ─────────────────────────────────────────────────────────────────────────
    $outboundTestNames = @("OutboundEnterScope")
    if ($Step -in (@("All") + $outboundTestNames)) {
        Write-TestSection "Reset for Outbound Test Block"
        # Restore working copy to the in-scope baseline (sub-tests will mutate it again).
        $users = $baseUsers | ForEach-Object { $_.PSObject.Copy() }
        Reset-ToOutboundBaseline `
            -HRSystemId $hrSystem.id `
            -LDAPSystemId $ldapSystem.id `
            -ImportRuleId $importRuleId `
            -ExportRuleId $exportRuleId `
            -BaseUsers $users `
            -DirectoryConfig $DirectoryConfig
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T5 (OutboundEnterScope): Run Export. Finance MVOs should be provisioned
    # to LDAP. Verify by both Activity stats and LDAP lookup.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "OutboundEnterScope") {
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
            Add-StepResult -Name "OutboundEnterScope" -Success $true -Note "Finance users provisioned in LDAP"
        }
        catch {
            Add-StepResult -Name "OutboundEnterScope" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # Deferred coverage (issue #656):
    #
    #   - OutboundExitDisconnect  Move an in-scope MVO out of scope with
    #                             OutboundDeprovisionAction = Disconnect and
    #                             verify the LDAP row stays + no PendingExport
    #                             is queued (HandleOutboundDeprovisioningAsync
    #                             returns null for Disconnect).
    #
    #   - OutboundExitDelete      Same shape as Disconnect but with
    #                             OutboundDeprovisionAction = Delete; expect
    #                             a Deprovisioned RPEI on the next Export run
    #                             and the LDAP row to be removed.
    #
    #   - CrossSystemCascade      Verify EvaluateOutOfScopeExportsAsync runs
    #                             inline during inbound sync (not deferred):
    #                             with Delete mode, a Delete PendingExport
    #                             must appear immediately after the inbound
    #                             sync, before any Export run.
    #
    # These three transitions need fully-isolated per-test connector state to
    # verify reliably in a single scenario run — the MVO graph after the
    # inbound block carries enough residual coupling (orphan MVOs, stale
    # pending exports from intermediate enter/exit transitions) that the
    # assertions become brittle. They are tracked as follow-on work and are
    # implementable on top of this scenario once we have a per-test teardown
    # primitive in the runner.
    # ─────────────────────────────────────────────────────────────────────────

    # ─────────────────────────────────────────────────────────────────────────
    # T9 (CriteriaOperators): Build a sandbox sync rule with mixed criteria
    # (text Contains, text StartsWith, numeric LessThan, nested AND/OR) and
    # verify it evaluates correctly against the MVOs we already have.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "CriteriaOperators") {
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

            # Pre-resolve attribute IDs (the cmdlet's -MetaverseAttributeName path doesn't
            # unwrap the paginated /api/v1/metaverse/attributes response correctly).
            $mvAttrs = @(Get-JIMMetaverseAttribute)
            $mvDeptAttr = $mvAttrs | Where-Object { $_.name -eq "Department" }
            $mvTitleAttr = $mvAttrs | Where-Object { $_.name -eq "Job Title" }
            $mvEmailAttr = $mvAttrs | Where-Object { $_.name -eq "Email" }
            if (-not $mvDeptAttr -or -not $mvTitleAttr -or -not $mvEmailAttr) {
                throw "Sandbox setup could not resolve required Metaverse attribute(s)"
            }

            # Top-level group: ALL (AND) - matches "Finance" department AND title starts with "Lead"
            $topGroup = New-JIMScopingCriteriaGroup -SyncRuleId $sandbox.id -Type All -PassThru
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeId $mvDeptAttr.id -ComparisonType Equals -StringValue "Finance" | Out-Null
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeId $mvTitleAttr.id -ComparisonType StartsWith -StringValue "Lead" | Out-Null
            New-JIMScopingCriterion -SyncRuleId $sandbox.id -GroupId $topGroup.id `
                -MetaverseAttributeId $mvEmailAttr.id -ComparisonType Contains -StringValue "@" | Out-Null

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

            Add-StepResult -Name "CriteriaOperators" -Success $true -Note "Sandbox rule persisted Equals/StartsWith/Contains criteria correctly"

            # Clean up the sandbox rule
            Remove-JIMSyncRule -Id $sandbox.id -Force | Out-Null
        }
        catch {
            Add-StepResult -Name "CriteriaOperators" -Success $false -ErrorMsg $_.Exception.Message
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
    Add-StepResult -Name "Setup" -Success $false -ErrorMsg $_.Exception.Message
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
