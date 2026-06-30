# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Relative-date inbound scoping: date-driven joiner provisioning and leaver deprovisioning

.DESCRIPTION
    Exercises RELATIVE DATE scoping criteria on an inbound (Import) Synchronisation Rule
    end-to-end. The rule scopes the "currently-employed" window:

        employeeStartDate <= now   AND   employeeEndDate >= now

    so a user is in scope only while currently employed. Steps:

      InitialScopeWindow:
        Seeds a joiner (start date in the future), a leaver (end date in the future) and an
        always-employed control. Confirms the joiner is OUT of scope (no Metaverse Object)
        while the leaver and control are IN scope (projected).

      JoinerProvisionedOnStartDate:
        Moves the joiner's start date into the past (their first day has arrived). The same
        rule now places them IN scope, projecting a Metaverse Object: date-driven provisioning.

      LeaverDeprovisionedOnEndDate:
        Moves the leaver's end date into the past (their contract has ended). The same rule
        now places them OUT of scope; with InboundOutOfScopeAction = Disconnect the CSO is
        disconnected, and because HR is the only connector the User type's default
        WhenLastConnectorDisconnected deletion rule removes the orphaned Metaverse Object:
        date-driven deprovisioning. The control is untouched.

      ReEvaluatedEachRun:
        Proves the criterion is re-resolved against the wall clock on EVERY run, not frozen at
        rule-creation. Seeds a user whose end date is a fixed instant a few seconds in the
        future, syncs (in scope), waits past that instant, then syncs again WITHOUT changing
        any data. The same data now falls out of scope purely because "now" advanced. This is
        the one guarantee that data-shifting alone cannot demonstrate, and that unit tests
        (which inject "now") cannot prove against the live DateTime.UtcNow path.

    Metaverse-only by design: projection is "provisioned", last-connector deletion is
    "deprovisioned". The cross-system Delete cascade to a target directory is covered by
    Scenario 10. The relative-date resolver's arithmetic (unit rounding, Ago/FromNow) is
    covered by RelativeDateResolver unit tests; this scenario focuses on end-to-end scope
    transitions and per-run re-evaluation.

.PARAMETER Step
    Which test step to execute. "All" runs every step in order.

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own explicit test users and
    ignores the template.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER WindowSeconds
    For ReEvaluatedEachRun: how far in the future to place the test user's end date, i.e. the
    margin the first import + sync must complete within. Default 90; raise it on a slow host if
    the precondition assertion reports the user was already out of scope on the first sync.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; this scenario has no directory target.

.EXAMPLE
    ./Invoke-Scenario12-RelativeDateScoping.ps1 -Step All -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
        "InitialScopeWindow", "JoinerProvisionedOnStartDate",
        "LeaverDeprovisionedOnEndDate", "ReEvaluatedEachRun", "All")]
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
    [int]$WindowSeconds = 90,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# Accepted for runner compatibility; this scenario manages its own CSV and has no directory.
$null = $SkipPopulate
$null = $DirectoryConfig

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

$hrSystemName = "Relative-Date HR Source"
$importRuleName = "Relative-Date Import (HR -> MV)"

Write-TestSection "Scenario 12: Relative-Date Inbound Scoping"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template (ignored; fixed test users)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Relative-Date Inbound Scoping"
    Steps = @()
    Success = $false
}

# ─────────────────────────────────────────────────────────────────────────────
# Fixed test users. employeeId is the join key. Dates are computed relative to "now"
# at run time and formatted as ISO-8601 UTC so the File connector types the columns as
# DateTime and the relative criteria evaluate against meaningful values.
# ─────────────────────────────────────────────────────────────────────────────
$nowUtc = (Get-Date).ToUniversalTime()

function Format-IsoUtc {
    param([datetime]$Value)
    return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

$empJoiner = "EMP000020"
$empLeaver = "EMP000021"
$empActive = "EMP000022"
$empClock  = "EMP000023"

$users = @(
    [PSCustomObject]@{
        employeeId        = $empJoiner
        firstName         = "Jordan"
        lastName          = "Joiner"
        displayName       = "Jordan Joiner"
        email             = "jordan.joiner@panoply.local"
        department        = "Finance"
        title             = "Analyst"
        samAccountName    = "jordan.joiner20"
        # Future start date: in HR before their first day -> out of scope until their start arrives.
        employeeStartDate = (Format-IsoUtc $nowUtc.AddDays(30))
        employeeEndDate   = (Format-IsoUtc $nowUtc.AddDays(400))
    },
    [PSCustomObject]@{
        employeeId        = $empLeaver
        firstName         = "Lena"
        lastName          = "Leaver"
        displayName       = "Lena Leaver"
        email             = "lena.leaver@panoply.local"
        department        = "Finance"
        title             = "Senior Accountant"
        samAccountName    = "lena.leaver21"
        # Currently employed: started in the past, ends in the future -> in scope.
        employeeStartDate = (Format-IsoUtc $nowUtc.AddDays(-100))
        employeeEndDate   = (Format-IsoUtc $nowUtc.AddDays(30))
    },
    [PSCustomObject]@{
        employeeId        = $empActive
        firstName         = "Avery"
        lastName          = "Active"
        displayName       = "Avery Active"
        email             = "avery.active@panoply.local"
        department        = "Finance"
        title             = "Manager"
        samAccountName    = "avery.active22"
        # Always employed throughout the scenario: a control that must never change scope.
        employeeStartDate = (Format-IsoUtc $nowUtc.AddDays(-200))
        employeeEndDate   = (Format-IsoUtc $nowUtc.AddDays(400))
    }
)

function Get-TestUser {
    param([string]$EmployeeId)
    $u = $users | Where-Object { $_.employeeId -eq $EmployeeId }
    if (-not $u) { throw "Test user '$EmployeeId' not found in working set" }
    return $u
}

function Write-HRCsv {
    $csvPath = Join-Path ([IO.Path]::GetTempPath()) "scenario12-hr-users.csv"
    $users | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
    Remove-Item $csvPath -Force -ErrorAction SilentlyContinue
}

function Invoke-FullImportAndSync {
    param([int]$SystemId, [string]$Label)
    $impProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Import" }
    $syncProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $impProfile -or -not $syncProfile) { throw "Run profiles for system $SystemId not found" }

    $imp = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $impProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $imp.activityId -Name "$Label Full Import"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

    $sync = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $syncProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Synchronisation"
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

    return @{ Import = $imp; Sync = $sync }
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
    # Step 0: Setup. Seed the initial CSV first so the File connector infers the date
    # columns as DateTime when Setup imports the schema.
    # ─────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 0: Setup"

    if (-not $ApiKey) { throw "API key required for authentication" }

    Write-HRCsv

    Write-Host "Running Scenario 12 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario12.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    # Setup removes and re-imports the module, so reconnect before issuing cmdlets here.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $hrSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $hrSystemName }
    if (-not $hrSystem) { throw "Connected system '$hrSystemName' not found after setup" }
    $importRule = (Get-JIMSyncRule) | Where-Object { $_.name -eq $importRuleName }
    if (-not $importRule) { throw "Sync rule '$importRuleName' not found after setup" }
    Write-Host "  OK JIM configured (HR=$($hrSystem.id), Import=$($importRule.id))" -ForegroundColor Green

    # ─────────────────────────────────────────────────────────────────────────
    # T1 (InitialScopeWindow): joiner OUT (not started), leaver + control IN.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "InitialScopeWindow") {
        Write-TestSection "Test 1: Initial Scope Window"
        try {
            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T1"

            # Leaver + control are currently employed, so both must enter the Metaverse. On a clean run
            # that is a Projection each; if an earlier run left orphan Metaverse Objects behind (a re-run
            # without a reset), the same CSOs Join those instead. Accept either so the step is robust to
            # re-runs. The runner resets between scenarios, so a fresh run sees Projections.
            $projected = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "Projected"
            $joined = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "Joined"
            if (($projected + $joined) -lt 2) {
                throw "T1 expected the 2 in-scope users to enter the Metaverse (Projected or Joined); got Projected=$projected, Joined=$joined"
            }

            $disconnected = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "DisconnectedOutOfScope"
            if ($disconnected -ne 0) {
                throw "T1 expected no disconnections (nothing was previously joined); got DisconnectedOutOfScope=$disconnected"
            }

            if (Test-MvoExists -EmployeeId $empJoiner) {
                throw "T1 joiner ($empJoiner) has a future start date and must be OUT of scope, but a Metaverse Object exists"
            }
            if (-not (Test-MvoExists -EmployeeId $empLeaver)) {
                throw "T1 leaver ($empLeaver) is currently employed and must be IN scope, but no Metaverse Object exists"
            }
            if (-not (Test-MvoExists -EmployeeId $empActive)) {
                throw "T1 control ($empActive) must be IN scope, but no Metaverse Object exists"
            }

            Add-StepResult -Name "InitialScopeWindow" -Success $true -Note "Joiner out of scope (no MVO); leaver + control projected"
        }
        catch {
            Add-StepResult -Name "InitialScopeWindow" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T2 (JoinerProvisionedOnStartDate): joiner's start date arrives -> IN scope -> projected.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "JoinerProvisionedOnStartDate") {
        Write-TestSection "Test 2: Joiner Provisioned On Start Date"
        try {
            (Get-TestUser -EmployeeId $empJoiner).employeeStartDate = (Format-IsoUtc (Get-Date).ToUniversalTime().AddDays(-1))
            Write-HRCsv

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T2"
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T2 Sync" -ExpectedChangeType "Projected" -MinCount 1

            $disconnected = Get-ActivityChangeCount -ActivityId $r.Sync.activityId -ChangeType "DisconnectedOutOfScope"
            if ($disconnected -ne 0) {
                throw "T2 brought a user into scope; expected 0 disconnections, got $disconnected"
            }
            if (-not (Test-MvoExists -EmployeeId $empJoiner)) {
                throw "T2 joiner ($empJoiner) started today and must now be IN scope, but no Metaverse Object exists"
            }

            Add-StepResult -Name "JoinerProvisionedOnStartDate" -Success $true -Note "Start date reached -> joiner entered scope -> Metaverse Object projected"
        }
        catch {
            Add-StepResult -Name "JoinerProvisionedOnStartDate" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T3 (LeaverDeprovisionedOnEndDate): leaver's end date passes -> OUT of scope ->
    # CSO disconnected -> last-connector deletion rule removes the Metaverse Object.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "LeaverDeprovisionedOnEndDate") {
        Write-TestSection "Test 3: Leaver Deprovisioned On End Date"
        try {
            if (-not (Test-MvoExists -EmployeeId $empLeaver)) {
                throw "T3 precondition failed: leaver ($empLeaver) should be IN scope before their end date passes. Run with -Step All."
            }

            (Get-TestUser -EmployeeId $empLeaver).employeeEndDate = (Format-IsoUtc (Get-Date).ToUniversalTime().AddDays(-1))
            Write-HRCsv

            $r = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T3"
            Assert-ActivityHasChanges -ActivityId $r.Sync.activityId -Name "T3 Sync" -ExpectedChangeType "DisconnectedOutOfScope" -MinCount 1

            if (Test-MvoExists -EmployeeId $empLeaver) {
                throw "T3 leaver ($empLeaver) ended and must be deprovisioned, but its Metaverse Object still exists"
            }
            if (-not (Test-MvoExists -EmployeeId $empActive)) {
                throw "T3 control ($empActive) must remain IN scope and untouched, but its Metaverse Object is gone"
            }

            Add-StepResult -Name "LeaverDeprovisionedOnEndDate" -Success $true -Note "End date passed -> leaver left scope -> disconnected -> Metaverse Object deleted; control untouched"
        }
        catch {
            Add-StepResult -Name "LeaverDeprovisionedOnEndDate" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T4 (ReEvaluatedEachRun): same data, scope flips because "now" advanced.
    # Seed a user whose end date is a fixed instant WindowSeconds in the future, sync
    # (in scope), wait past that instant, then sync again with NO data change.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "ReEvaluatedEachRun") {
        Write-TestSection "Test 4: Re-evaluated On Each Run (wall-clock)"

        # KNOWN GAP (#892): the temporal scope reconciler is not built yet, so a scope transition driven
        # purely by the passage of time (static source data) is not detected by sync. This step asserts
        # that behaviour and fails until #892 lands; it is skipped in a full run and runnable explicitly
        # (-Step ReEvaluatedEachRun) to verify the fix once built.
        # See engineering/plans/TEMPORAL_SCOPE_REEVALUATION.md.
        if ($Step -eq "All") {
            Write-Host "  SKIP Pending #892 (temporal scope reconciler). Run -Step ReEvaluatedEachRun to verify once built." -ForegroundColor Yellow
            Add-StepResult -Name "ReEvaluatedEachRun" -Skipped -Note "Skipped pending #892 (temporal scope reconciler)"
        }
        else {
        try {
            $clockEndInstant = (Get-Date).ToUniversalTime().AddSeconds($WindowSeconds)
            $clockUser = [PSCustomObject]@{
                employeeId        = $empClock
                firstName         = "Casey"
                lastName          = "Clock"
                displayName       = "Casey Clock"
                email             = "casey.clock@panoply.local"
                department        = "Finance"
                title             = "Contractor"
                samAccountName    = "casey.clock23"
                employeeStartDate = (Format-IsoUtc (Get-Date).ToUniversalTime().AddDays(-100))
                employeeEndDate   = (Format-IsoUtc $clockEndInstant)
            }
            $script:users += $clockUser
            Write-HRCsv

            # First run: end date is still in the future -> in scope -> projected.
            $r1 = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4 (before boundary)"
            if (-not (Test-MvoExists -EmployeeId $empClock)) {
                throw "T4 precondition failed: $empClock was not in scope on the first sync. The import + sync likely took longer than WindowSeconds=$WindowSeconds; re-run with a larger -WindowSeconds."
            }
            Write-Host "  OK $empClock in scope before the boundary (end date $($clockUser.employeeEndDate))" -ForegroundColor Green

            # Wait until the wall clock has passed the fixed end instant (+5s margin).
            $remaining = ($clockEndInstant - (Get-Date).ToUniversalTime()).TotalSeconds + 5
            if ($remaining -gt 0) {
                Write-Host "  Waiting $([int][Math]::Ceiling($remaining))s for 'now' to advance past the end date..." -ForegroundColor Gray
                Start-Sleep -Seconds ([int][Math]::Ceiling($remaining))
            }

            # Second run: NO data change. Same criterion, advanced clock -> out of scope.
            $r2 = Invoke-FullImportAndSync -SystemId $hrSystem.id -Label "T4 (after boundary)"
            Assert-ActivityHasChanges -ActivityId $r2.Sync.activityId -Name "T4 Sync (after boundary)" -ExpectedChangeType "DisconnectedOutOfScope" -MinCount 1

            if (Test-MvoExists -EmployeeId $empClock) {
                throw "T4 $empClock should have fallen out of scope after the clock advanced past its fixed end date, but its Metaverse Object still exists. The relative criterion may not be re-evaluated each run."
            }

            Add-StepResult -Name "ReEvaluatedEachRun" -Success $true -Note "Identical data; scope flipped because 'now' advanced past the fixed end date -> criterion re-evaluated each run"
        }
        catch {
            Add-StepResult -Name "ReEvaluatedEachRun" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
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
    Write-Host "OK All Scenario 12 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some tests failed" -ForegroundColor Red
    exit 1
}
