# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Relative-date OUTBOUND scoping: date-driven downstream provisioning via the Temporal Scope Reconciler

.DESCRIPTION
    Exercises a RELATIVE DATE scoping criterion on an OUTBOUND (Export) Synchronisation Rule
    end-to-end, and proves the Temporal Scope Reconciler's outbound lane (#892). The export rule
    holds downstream provisioning until the joiner's start date arrives:

        Employee Start Date <= now

    The inbound rule is deliberately UNSCOPED, so the Metaverse Object persists throughout; only
    the EXPORT scope flips as "now" advances. That isolation pins any downstream change on the
    outbound reconciler lane and nothing else.

      OutboundInitialScope:
        Seeds a control (start date in the past) and a joiner (start date a fixed instant a few
        seconds in the future). Both project a Metaverse Object (inbound is unscoped). The export
        rule provisions the control downstream (in scope) but HOLDS the joiner (start date not yet
        reached), so the target connector space has exactly one object.

      ProvisionedOnSchedule:
        Waits until the wall clock passes the joiner's fixed start instant, WITHOUT changing any
        data. First proves the hot path alone misses the transition: a plain sync provisions
        nothing new (the joiner's Metaverse Object is unchanged, so export evaluation never
        reconsiders it). Then triggers the Temporal Scope Reconciler, which flags the joiner's
        Metaverse Object as its export scope has flipped; the next sync re-evaluates it, provisions
        it downstream (flag-and-delegate), and the target connector space grows to two objects. The
        control is untouched throughout. The joiner also carries a Manager reference (to the
        control), exported via an mv["Manager"] expression mapping, and the exported row must
        carry the referenced Metaverse Object's ID: the reconciler loads flagged Metaverse
        Objects without the ReferenceValue navigation, so this proves reference attributes
        survive a reconciler-driven provision end-to-end via the FK-scalar fallback (#892).

    File-connector target by design (a header-only CSV the connector appends to): no directory
    container, so the test stays fast and free of directory flakiness, mirroring Scenario 12. The
    relative-date resolver's arithmetic is covered by RelativeDateResolver unit tests; this scenario
    focuses on the end-to-end outbound transition and per-run re-evaluation against the live clock.

.PARAMETER Step
    Which test step to execute. "All" runs every step in order.

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own explicit test users and ignores
    the template.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER WindowSeconds
    How far in the future to place the joiner's fixed start date, i.e. the margin the setup plus the
    first import + sync + export must complete within before the boundary is crossed. Default 120;
    raise it on a slow host if OutboundInitialScope reports the joiner was already provisioned on the
    first run.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; this scenario has no directory target.

.EXAMPLE
    ./Invoke-Scenario13-RelativeDateOutboundScoping.ps1 -Step All -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("OutboundInitialScope", "ProvisionedOnSchedule", "All")]
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
    [int]$WindowSeconds = 120,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# Accepted for runner compatibility; this scenario manages its own CSVs and has no directory.
$null = $SkipPopulate
$null = $DirectoryConfig

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

$hrSystemName = "Relative-Date Outbound HR Source"
$targetSystemName = "Relative-Date Downstream Target"
$exportRuleName = "Relative-Date Outbound Export (MV -> Target)"

Write-TestSection "Scenario 13: Relative-Date Outbound (Export) Scoping"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template (ignored; fixed test users)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Relative-Date Outbound Scoping"
    Steps = @()
    Success = $false
}

# ─────────────────────────────────────────────────────────────────────────────
# Fixed test users. employeeId is the join key. The joiner's start date is a fixed instant
# WindowSeconds in the future, captured once so ProvisionedOnSchedule can wait for the wall clock
# to cross it without any data change.
# ─────────────────────────────────────────────────────────────────────────────
$nowUtc = (Get-Date).ToUniversalTime()

function Format-IsoUtc {
    param([datetime]$Value)
    return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

$empControl = "EMP000030"
$empProvision = "EMP000031"

# The joiner's fixed start instant. In scope for export only once the clock passes it.
$provisionStartInstant = $nowUtc.AddSeconds($WindowSeconds)

$users = @(
    [PSCustomObject]@{
        employeeId        = $empControl
        firstName         = "Carl"
        lastName          = "Control"
        displayName       = "Carl Control"
        email             = "carl.control@panoply.local"
        samAccountName    = "carl.control30"
        # Started long ago: in export scope from the outset, provisioned immediately, never changes.
        employeeStartDate = (Format-IsoUtc $nowUtc.AddDays(-100))
    },
    [PSCustomObject]@{
        employeeId        = $empProvision
        firstName         = "Pat"
        lastName          = "Provision"
        displayName       = "Pat Provision"
        email             = "pat.provision@panoply.local"
        samAccountName    = "pat.provision31"
        # Future start date: in the Metaverse (unscoped inbound) but held OUT of downstream
        # provisioning until the clock crosses this instant.
        employeeStartDate = (Format-IsoUtc $provisionStartInstant)
    }
)

function Get-TestUser {
    param([string]$EmployeeId)
    $u = $users | Where-Object { $_.employeeId -eq $EmployeeId }
    if (-not $u) { throw "Test user '$EmployeeId' not found in working set" }
    return $u
}

function Write-HRCsv {
    $csvPath = Join-Path ([IO.Path]::GetTempPath()) "scenario13-hr-users.csv"
    $users | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath -DestinationName "scenario13-hr-users.csv"
    Remove-Item $csvPath -Force -ErrorAction SilentlyContinue
}

function Write-TargetHeaderCsv {
    # Header-only CSV: the File connector infers the export schema from the header row and appends
    # exported rows to it. Regenerated fresh so no state leaks between runs. The manager column
    # receives the exported Manager reference (the referenced Metaverse Object's ID, via the
    # mv["Manager"] expression mapping), proving the reconciler-driven reference export flow (#892).
    $csvPath = Join-Path ([IO.Path]::GetTempPath()) "scenario13-target.csv"
    Set-Content -Path $csvPath -Value "samAccountName,displayName,email,employeeId,manager" -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath -DestinationName "scenario13-target.csv"
    Remove-Item $csvPath -Force -ErrorAction SilentlyContinue
}

function Invoke-HRFullImportAndSync {
    param([int]$SystemId, [string]$Label)
    $impProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Import" }
    $syncProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $impProfile -or -not $syncProfile) { throw "HR run profiles for system $SystemId not found" }

    $imp = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $impProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $imp.activityId -Name "$Label Full Import"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

    $sync = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $syncProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Synchronisation"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

    return @{ Import = $imp; Sync = $sync }
}

function Invoke-HRFullSync {
    param([int]$SystemId, [string]$Label)
    $syncProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $syncProfile) { throw "HR Full Synchronisation run profile for system $SystemId not found" }
    $sync = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $syncProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Synchronisation"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }
    return $sync
}

function Invoke-TargetExport {
    param([int]$SystemId, [string]$Label)
    $exportProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Export" }
    if (-not $exportProfile) { throw "Export run profile not found for system $SystemId" }
    $exp = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $exportProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exp.activityId -Name "$Label Export"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }
    return $exp
}

function Get-TargetObjectCount {
    param([int]$SystemId)
    return [int](Get-JIMConnectedSystemObject -ConnectedSystemId $SystemId -Count)
}

function Invoke-Reconciler {
    param([string]$Label)
    $reconSchedule = @(Get-JIMSchedule | Where-Object { $_.name -eq "Temporal Scope Reconciliation" })
    if ($reconSchedule.Count -ne 1) {
        throw "$Label expected exactly one built-in 'Temporal Scope Reconciliation' schedule; found $($reconSchedule.Count)"
    }
    Write-Host "  Triggering the Temporal Scope Reconciler (schedule $($reconSchedule[0].id))..." -ForegroundColor Gray
    $reconExec = Start-JIMSchedule -Id $reconSchedule[0].id -PassThru
    if (-not $reconExec) { throw "$Label failed to start the Temporal Scope Reconciler schedule" }

    # Poll to a terminal state. Status is serialised as a string enum, so match on the string (with
    # an int fallback) rather than relying on Start-JIMSchedule -Wait's numeric comparison.
    $maxWait = 120; $elapsed = 0
    while ($elapsed -lt $maxWait) {
        $sv = (Get-JIMScheduleExecution -Id $reconExec.id).status
        $isTerminal = $sv -eq "Completed" -or $sv -eq "Failed" -or $sv -eq "Cancelled"
        if (($sv -is [int] -or $sv -is [long]) -and $sv -ge 2) { $isTerminal = $true }
        if ($isTerminal) { break }
        Start-Sleep -Seconds 3; $elapsed += 3
    }
    Assert-ScheduleExecutionSuccess -ExecutionId $reconExec.id -Name "Temporal Scope Reconciler"
    Write-Host "  OK Reconciler sweep completed" -ForegroundColor Green
}

function Test-MvoExists {
    param([string]$EmployeeId)
    $matches = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $EmployeeId -ErrorAction SilentlyContinue)
    return ($matches.Count -gt 0)
}

function Add-StepResult {
    param([string]$Name, [bool]$Success = $true, [string]$Note = "", [string]$ErrorMsg = "", [switch]$Skipped)
    $entry = @{ Name = $Name; Success = $Success; Skipped = [bool]$Skipped }
    if ($Note) { $entry.Note = $Note }
    if ($ErrorMsg) { $entry.Error = $ErrorMsg }
    $testResults.Steps += $entry
}

function Test-StepEnabled {
    param([string]$StepName)
    return ($Step -eq $StepName -or $Step -eq "All")
}

try {
    # ─────────────────────────────────────────────────────────────────────────
    # Step 0: Setup. Seed the HR CSV (so the File connector infers employeeStartDate as DateTime)
    # and the header-only target CSV, then configure JIM.
    # ─────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 0: Setup"

    if (-not $ApiKey) { throw "API key required for authentication" }

    Write-HRCsv
    Write-TargetHeaderCsv

    Write-Host "Running Scenario 13 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario13.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    # Setup removes and re-imports the module, so reconnect before issuing cmdlets here.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $hrSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $hrSystemName }
    if (-not $hrSystem) { throw "Connected system '$hrSystemName' not found after setup" }
    $targetSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $targetSystemName }
    if (-not $targetSystem) { throw "Connected system '$targetSystemName' not found after setup" }
    $exportRule = (Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName }
    if (-not $exportRule) { throw "Sync rule '$exportRuleName' not found after setup" }
    Write-Host "  OK JIM configured (HR=$($hrSystem.id), Target=$($targetSystem.id), Export=$($exportRule.id))" -ForegroundColor Green

    # ─────────────────────────────────────────────────────────────────────────
    # T1 (OutboundInitialScope): control provisioned downstream, joiner held.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "OutboundInitialScope") {
        Write-TestSection "Test 1: Outbound Initial Scope"
        try {
            $r = Invoke-HRFullImportAndSync -SystemId $hrSystem.id -Label "T1"

            # Both users are projected (inbound is unscoped), so both Metaverse Objects must exist.
            if (-not (Test-MvoExists -EmployeeId $empControl)) {
                throw "T1 control ($empControl) must project a Metaverse Object, but none exists"
            }
            if (-not (Test-MvoExists -EmployeeId $empProvision)) {
                throw "T1 joiner ($empProvision) must project a Metaverse Object (inbound is unscoped), but none exists"
            }

            # The export rule provisions the control (in scope) and HOLDS the joiner (start date in
            # the future), so the target connector space has exactly one object.
            $targetCount = Get-TargetObjectCount -SystemId $targetSystem.id
            if ($targetCount -ne 1) {
                throw "T1 expected exactly 1 downstream object (control provisioned, joiner held); got $targetCount. If >1, the joiner's start date was already crossed before the first sync; re-run with a larger -WindowSeconds (currently $WindowSeconds)."
            }
            Write-Host "  OK Control provisioned downstream; joiner held out of export scope (target count=1)" -ForegroundColor Green

            # Push the provisioned control row to the target CSV to complete the flow.
            $exp = Invoke-TargetExport -SystemId $targetSystem.id -Label "T1"
            Assert-ActivityHasChanges -ActivityId $exp.activityId -Name "T1 Export" -ExpectedChangeType "Exported" -MinCount 1

            Add-StepResult -Name "OutboundInitialScope" -Success $true -Note "Control provisioned downstream; joiner held by relative-date export criterion (start date in the future)"
        }
        catch {
            Add-StepResult -Name "OutboundInitialScope" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T2 (ProvisionedOnSchedule): joiner's start date passes -> reconciler flags -> provisioned.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "ProvisionedOnSchedule") {
        Write-TestSection "Test 2: Provisioned On Schedule (Temporal Scope Reconciler, #892)"
        try {
            # Precondition: the joiner is in the Metaverse but not yet provisioned downstream.
            if (-not (Test-MvoExists -EmployeeId $empProvision)) {
                throw "T2 precondition failed: joiner ($empProvision) Metaverse Object missing. Run with -Step All."
            }
            $preCount = Get-TargetObjectCount -SystemId $targetSystem.id
            if ($preCount -ne 1) {
                throw "T2 precondition failed: expected exactly 1 downstream object before the boundary; got $preCount. Run with -Step All."
            }

            # Wait until the wall clock has passed the joiner's fixed start instant (+5s margin).
            $remaining = ($provisionStartInstant - (Get-Date).ToUniversalTime()).TotalSeconds + 5
            if ($remaining -gt 0) {
                Write-Host "  Waiting $([int][Math]::Ceiling($remaining))s for 'now' to advance past the joiner's start date..." -ForegroundColor Gray
                Start-Sleep -Seconds ([int][Math]::Ceiling($remaining))
            }

            # Negative control: the source data is static and the joiner's Metaverse Object is
            # unchanged, so the export hot path (change-gated) must NOT reconsider it. A plain sync
            # provisions nothing new. This is the exact gap the reconciler exists to close.
            $null = Invoke-HRFullSync -SystemId $hrSystem.id -Label "T2 (hot path, no reconciler)"
            $afterHotPath = Get-TargetObjectCount -SystemId $targetSystem.id
            if ($afterHotPath -ne 1) {
                throw "T2 negative control failed: a plain sync changed the downstream count from 1 to $afterHotPath. The joiner should not be provisioned by the hot path alone (its data is unchanged); the built-in reconciler schedule may have auto-fired despite being disabled."
            }
            Write-Host "  OK Hot path alone did not provision the joiner (target count still 1)" -ForegroundColor Green

            # Give the joiner a Manager reference (pointing at the control) ahead of the
            # reconciler-driven provision (#892). The apply step loads flagged Metaverse Objects
            # via a lean no-tracking query that carries reference attribute values as FK scalars
            # only (no ReferenceValue navigation); the export below proves that shape end-to-end:
            # the provision's mv["Manager"] expression must evaluate from the FK scalar and export
            # the referenced Metaverse Object's ID, not silently produce null. The row is inserted
            # directly as an internally-managed value (contributor columns null; identical to what
            # an inbound reference flow persists) because the File connector cannot type a CSV
            # column as Reference, and JIM has no Metaverse Object value write API by design.
            $controlMvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $empControl)
            $joinerMvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $empProvision)
            if ($controlMvos.Count -ne 1 -or $joinerMvos.Count -ne 1) {
                throw "T2 expected exactly one Metaverse Object each for control/joiner; got $($controlMvos.Count)/$($joinerMvos.Count)"
            }
            # Cast to [guid] so the SQL interpolation below cannot carry anything but a GUID
            # (psql -c does not support bind parameters; same pattern as Assert-ExportRpeisHaveCsoLink).
            $controlMvoId = [guid]$controlMvos[0].id
            $joinerMvoId = [guid]$joinerMvos[0].id
            $seedSql = "INSERT INTO ""MetaverseObjectAttributeValues"" (""Id"", ""AttributeId"", ""MetaverseObjectId"", ""ReferenceValueId"", ""NullValue"") SELECT gen_random_uuid(), ma.""Id"", '$joinerMvoId', '$controlMvoId', false FROM ""MetaverseAttributes"" ma WHERE ma.""Name"" = 'Manager';"
            $seedResult = docker compose exec -T jim.database psql -U jim -d jim -c $seedSql 2>&1
            if ($LASTEXITCODE -ne 0 -or "$seedResult" -notmatch "INSERT 0 1") {
                throw "T2 failed to seed the joiner's Manager reference: $seedResult"
            }
            Write-Host "  OK Seeded joiner's Manager reference (-> control) ahead of the reconciler-driven provision" -ForegroundColor Green

            # Trigger the Temporal Scope Reconciler: it flags the joiner's Metaverse Object because
            # its export scope has flipped (start date now in the past).
            Invoke-Reconciler -Label "T2"

            # The flagged Metaverse Object is re-evaluated on the next sync (fold-into-base) and
            # provisioned downstream (flag-and-delegate), with no data change.
            $null = Invoke-HRFullSync -SystemId $hrSystem.id -Label "T2 (after reconciler)"
            $afterReconciler = Get-TargetObjectCount -SystemId $targetSystem.id
            if ($afterReconciler -ne 2) {
                throw "T2 joiner ($empProvision) should have been provisioned downstream after the clock crossed its start date and the reconciler flagged it, but the downstream count is $afterReconciler (expected 2). The reconciler flag may not be honoured by the outbound sync path."
            }
            Write-Host "  OK Joiner provisioned downstream after reconciler sweep (target count=2)" -ForegroundColor Green

            # Push the newly provisioned joiner row to the target CSV to complete the flow.
            $exp = Invoke-TargetExport -SystemId $targetSystem.id -Label "T2"
            Assert-ActivityHasChanges -ActivityId $exp.activityId -Name "T2 Export" -ExpectedChangeType "Exported" -MinCount 1

            # The exported joiner row must carry the Manager reference: mv["Manager"] evaluates to
            # the referenced Metaverse Object's ID (the control). This is the #892 reference-flow
            # proof: the reconciler-loaded Metaverse Object has no ReferenceValue navigation, so a
            # populated manager cell proves the expression context fell back to the FK scalar
            # rather than silently evaluating the reference to null.
            $joinerUser = Get-TestUser -EmployeeId $empProvision
            $targetCsvRaw = docker compose exec -T jim.worker cat /connector-files/test-data/scenario13-target.csv 2>&1
            if ($LASTEXITCODE -ne 0) { throw "T2 could not read the exported target CSV: $targetCsvRaw" }
            $joinerRows = @(@($targetCsvRaw | ConvertFrom-Csv) | Where-Object { $_.samAccountName -eq $joinerUser.samAccountName })
            if ($joinerRows.Count -ne 1) {
                throw "T2 expected exactly 1 exported row for the joiner ($($joinerUser.samAccountName)); got $($joinerRows.Count)"
            }
            if ($joinerRows[0].manager -ne "$controlMvoId") {
                throw "T2 joiner's exported manager should carry the control's Metaverse Object ID '$controlMvoId' but was '$($joinerRows[0].manager)'. The reconciler-driven provision dropped the reference (#892)."
            }
            Write-Host "  OK Joiner's Manager reference exported (mv[`"Manager`"] -> control's Metaverse Object ID; reconciler reference flow proven)" -ForegroundColor Green

            # The control must remain provisioned and untouched (count of 2 covers both known users).
            if (-not (Test-MvoExists -EmployeeId $empControl)) {
                throw "T2 control ($empControl) Metaverse Object unexpectedly gone"
            }

            Add-StepResult -Name "ProvisionedOnSchedule" -Success $true -Note "Identical data; the Temporal Scope Reconciler flagged the joiner as 'now' advanced past its fixed start date, and the next sync provisioned it downstream (flag-and-delegate) with its Manager reference carried in the exported row. The hot path alone missed it."
        }
        catch {
            Add-StepResult -Name "ProvisionedOnSchedule" -Success $false -ErrorMsg $_.Exception.Message
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

$skipped = @($testResults.Steps | Where-Object { $_.Skipped }).Count
$passed = @($testResults.Steps | Where-Object { $_.Success -and -not $_.Skipped }).Count
$total = @($testResults.Steps).Count
$failedCount = @($testResults.Steps | Where-Object { -not $_.Success }).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Skipped) { "SKIP" } elseif ($testStep.Success) { "OK" } else { "FAIL" }
    $color = if ($testStep.Skipped) { "Yellow" } elseif ($testStep.Success) { "Green" } else { "Red" }
    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color
    if ($testStep.ContainsKey('Note') -and $testStep.Note) {
        Write-Host "    $($testStep.Note)" -ForegroundColor Gray
    }
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Results: $passed passed, $failedCount failed, $skipped skipped (of $total tests)" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK All Scenario 13 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some tests failed" -ForegroundColor Red
    exit 1
}
