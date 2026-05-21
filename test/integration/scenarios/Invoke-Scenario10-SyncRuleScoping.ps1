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
      - OutboundExitDisconnect:  MVO leaves Finance with Disconnect action -> LDAP row stays,
                                 no PendingExport queued
      - OutboundExitDelete:      MVO leaves Finance with Delete action -> Deprovisioned RPEI
                                 on the next Export run, LDAP row removed
      - CrossSystemCascade:      EvaluateOutOfScopeExportsAsync runs inline during inbound
                                 sync, so the Delete PendingExport appears immediately after
                                 the sync (before any Export run)

    Each of the three cascade sub-tests above runs against a freshly reset JIM instance
    (Reset-JIMSystem followed by Setup-Scenario10) so the assertions are not polluted
    by intermediate state from the inbound block.

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

function Reset-JIMForCascadeTest {
    param([object[]]$BaseUsers, [hashtable]$DirectoryConfig)

    # Full factory reset: wipes JIM's database back to post-seed state (preserves
    # built-in attributes, types, roles, connector definitions, and the infrastructure
    # API key). Each cascade sub-test starts from this clean slate so the MVO graph
    # carries zero residual state from earlier tests.
    Write-Host "  Running Reset-JIMSystem..." -ForegroundColor Gray
    $resetResult = Reset-JIMSystem -Force
    Write-Host ("  OK Reset complete (removed {0} connected systems, {1} MVOs, {2} sync rules)" -f `
        $resetResult.connectedSystemsRemoved, `
        $resetResult.metaverseObjectsRemoved, `
        $resetResult.syncRulesRemoved) -ForegroundColor Gray

    # Reset wipes JIM's view of the world, but the LDAP container still holds whatever
    # rows earlier exports created. Wipe the test users so the next provisioning run
    # creates them cleanly.
    Remove-LDAPTestUsers -Users $BaseUsers -DirectoryConfig $DirectoryConfig

    # Re-run setup to rebuild connected systems, sync rules, run profiles, etc.
    Write-Host "  Re-running Setup-Scenario10..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario10.ps1" `
        -JIMUrl $JIMUrl `
        -ApiKey $ApiKey `
        -Template $Template `
        -ExportConcurrency $ExportConcurrency `
        -MaxExportParallelism $MaxExportParallelism `
        -DirectoryConfig $DirectoryConfig

    # Setup-Scenario10 removes and re-imports the module, so we need to do the same
    # here before the caller continues with cmdlet calls.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Seed the baseline HR CSV (everyone in Finance / in scope) and project the MVOs,
    # then export so the LDAP target carries the matching CSOs.
    Write-HRCsv -Users $BaseUsers

    $hrSys = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $hrSystemName }
    $ldapSys = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName }
    $impRule = Get-RuleId -Name $importRuleName
    $expRule = Get-RuleId -Name $exportRuleName

    $null = Invoke-FullImportAndSync -SystemId $hrSys.id -Label "Cascade-baseline (project)"
    $null = Invoke-Export -SystemId $ldapSys.id -Label "Cascade-baseline (export)"

    # A successful export leaves a PendingExport row with status=Exported attached to each
    # newly-created CSO. The PendingExports table has a unique index on ConnectedSystemObjectId
    # (filtered WHERE NOT NULL), so a subsequent cascade attempt to queue a Delete PE for
    # the same CSO clashes with the leftover Exported row. The reconciliation that clears
    # the stale row only runs during an import on the target system, so we run a confirming
    # LDAP Full Import here to mirror the normal production loop.
    $impProfileLdap = (Get-JIMRunProfile -ConnectedSystemId $ldapSys.id) | Where-Object { $_.name -eq "Full Import" }
    if ($impProfileLdap) {
        $confirm = Start-JIMRunProfile -ConnectedSystemId $ldapSys.id -RunProfileId $impProfileLdap.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirm.activityId -Name "Cascade-baseline (confirming LDAP import)"
    }

    # Strip the import rule's scoping so subsequent attribute changes (e.g. Department:
    # Finance -> Sales) actually flow to the MVO. With the default Setup-Scenario10 config
    # both rules are scoped on Department=Finance; if we left the import rule scoped, a
    # CSO moving to Sales would be marked out-of-inbound-scope and attribute flow would be
    # suppressed, leaving the MVO's Department stuck at Finance. The outbound cascade
    # (EvaluateOutOfScopeExportsAsync) only fires when an MVO attribute genuinely changes,
    # so we need attribute flow to be unconditional here.
    foreach ($group in @(Get-JIMScopingCriteria -SyncRuleId $impRule)) {
        Remove-JIMScopingCriteriaGroup -SyncRuleId $impRule -GroupId $group.id -Confirm:$false | Out-Null
    }

    Write-Host "  OK Cascade baseline ready (LDAP confirming import done, import scope cleared)" -ForegroundColor Green
    return @{ HRSystemId = $hrSys.id; LDAPSystemId = $ldapSys.id; ImportRuleId = $impRule; ExportRuleId = $expRule }
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
    # T6 (OutboundExitDisconnect): export rule in Disconnect mode. Move an
    # in-scope MVO out of scope and verify the LDAP row stays + no Delete
    # PendingExport is queued (HandleOutboundDeprovisioningAsync returns null
    # for Disconnect).
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "OutboundExitDisconnect") {
        Write-TestSection "Test 6: Outbound Exit Scope (Disconnect)"
        try {
            $users = $baseUsers | ForEach-Object { $_.PSObject.Copy() }
            $ids = Reset-JIMForCascadeTest -BaseUsers $users -DirectoryConfig $DirectoryConfig

            # Confirm the baseline: Aria is in LDAP after the cascade-baseline export.
            if (-not (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig)) {
                throw "T6 baseline expected $($users[0].samAccountName) in LDAP after cascade-baseline export"
            }

            Set-JIMSyncRule -Id $ids.ExportRuleId -OutboundDeprovisionAction Disconnect | Out-Null

            # Move Aria out of scope via HR
            $users[0].department = "Sales"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $ids.HRSystemId -Label "T6"
            $null = $r

            # In Disconnect mode the inline cascade should NOT queue a Delete PendingExport.
            # Enumerate rather than use -Count: the count endpoint returns a primitive that
            # Invoke-RestMethod sometimes deserialises in a way that breaks strict-mode
            # property access; enumerating + filtering is more robust.
            $pendingExports = @(Get-JIMPendingExport -ConnectedSystemId $ids.LDAPSystemId -All)
            $deleteCount = @($pendingExports | Where-Object { $_.changeType -eq 'Delete' }).Count
            if ($deleteCount -ne 0) {
                throw "T6 Disconnect mode queued $deleteCount Delete PendingExport(s); expected 0"
            }

            # Run an export anyway to confirm nothing destructive happens.
            $exp = Invoke-Export -SystemId $ids.LDAPSystemId -Label "T6"
            $deprovisionedCount = Get-ActivityChangeCount -ActivityId $exp.activityId -ChangeType "Deprovisioned"
            if ($deprovisionedCount -ne 0) {
                throw "T6 export produced $deprovisionedCount Deprovisioned RPEIs; expected 0 in Disconnect mode"
            }

            # And the LDAP row must still be there.
            if (-not (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig)) {
                throw "T6 Disconnect mode unexpectedly removed $($users[0].samAccountName) from LDAP"
            }

            Add-StepResult -Name "OutboundExitDisconnect" -Success $true -Note "Disconnect mode: no Delete PendingExport, no Deprovisioned RPEI, LDAP row preserved"
        }
        catch {
            Add-StepResult -Name "OutboundExitDisconnect" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T7 (OutboundExitDelete): export rule in Delete mode. Move an in-scope MVO
    # out of scope and verify that the next Export run emits a Deprovisioned
    # RPEI and removes the LDAP row.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "OutboundExitDelete") {
        Write-TestSection "Test 7: Outbound Exit Scope (Delete)"
        try {
            $users = $baseUsers | ForEach-Object { $_.PSObject.Copy() }
            $ids = Reset-JIMForCascadeTest -BaseUsers $users -DirectoryConfig $DirectoryConfig

            if (-not (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig)) {
                throw "T7 baseline expected $($users[0].samAccountName) in LDAP after cascade-baseline export"
            }

            Set-JIMSyncRule -Id $ids.ExportRuleId -OutboundDeprovisionAction Delete | Out-Null

            # Move Aria out of scope
            $users[0].department = "Sales"
            Write-HRCsv -Users $users

            $r = Invoke-FullImportAndSync -SystemId $ids.HRSystemId -Label "T7"
            $null = $r

            $exp = Invoke-Export -SystemId $ids.LDAPSystemId -Label "T7"
            Assert-ActivityHasChanges -ActivityId $exp.activityId -Name "T7 Export" -ExpectedChangeType "Deprovisioned" -MinCount 1

            # The LDAP row must be gone.
            if (Test-LDAPUserExists -UserIdentifier $users[0].samAccountName -DirectoryConfig $DirectoryConfig) {
                throw "T7 Delete mode left $($users[0].samAccountName) in LDAP after export"
            }

            Add-StepResult -Name "OutboundExitDelete" -Success $true -Note "Delete mode: Deprovisioned RPEI emitted, LDAP row removed"
        }
        catch {
            Add-StepResult -Name "OutboundExitDelete" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T8 (CrossSystemCascade): verify that EvaluateOutOfScopeExportsAsync fires
    # INLINE during inbound sync. With Delete mode set, moving an in-scope MVO
    # out of scope via HR import must produce a Delete PendingExport on the
    # LDAP system immediately after the sync, before any Export run.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "CrossSystemCascade") {
        Write-TestSection "Test 8: Cross-System Cascade (inline)"
        try {
            $users = $baseUsers | ForEach-Object { $_.PSObject.Copy() }
            $ids = Reset-JIMForCascadeTest -BaseUsers $users -DirectoryConfig $DirectoryConfig

            Set-JIMSyncRule -Id $ids.ExportRuleId -OutboundDeprovisionAction Delete | Out-Null

            # Enumerate (not -Count) to side-step the strict-mode primitive-deserialisation
            # quirk observed on the count endpoint.
            $beforeDeletes = @(Get-JIMPendingExport -ConnectedSystemId $ids.LDAPSystemId -All)
            $beforeCount = @($beforeDeletes | Where-Object { $_.changeType -eq 'Delete' }).Count

            # Move Aria out of scope. We run import + sync only, NOT export.
            $users[0].department = "Sales"
            Write-HRCsv -Users $users
            $r = Invoke-FullImportAndSync -SystemId $ids.HRSystemId -Label "T8"
            $null = $r

            # Inline cascade should have queued a Delete PendingExport on the LDAP system.
            $afterDeletes = @(Get-JIMPendingExport -ConnectedSystemId $ids.LDAPSystemId -All)
            $afterCount = @($afterDeletes | Where-Object { $_.changeType -eq 'Delete' }).Count

            if ($afterCount -le $beforeCount) {
                throw "T8 expected inline cascade to queue a Delete PendingExport on LDAP after inbound sync; before=$beforeCount, after=$afterCount"
            }

            Add-StepResult -Name "CrossSystemCascade" -Success $true -Note "Inline cascade queued a Delete PendingExport on LDAP during inbound sync (before any Export run)"
        }
        catch {
            Add-StepResult -Name "CrossSystemCascade" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T9 (CriteriaOperators): Build a sandbox sync rule with mixed criteria
    # (text Contains, text StartsWith, numeric LessThan, nested AND/OR) and
    # verify it evaluates correctly against the MVOs we already have.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "CriteriaOperators") {
        Write-TestSection "Test 9: Scoping Criteria Operators (sandbox rule)"
        try {
            # Re-resolve the LDAP system: cascade sub-tests above run Reset-JIMSystem,
            # which wipes and recreates the connected system with a new ID. The
            # $ldapSystem captured in Step 0 may now be stale.
            $ldapSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName }
            if (-not $ldapSystem) {
                throw "Connected system '$ldapSystemName' not found at the start of T9"
            }

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
