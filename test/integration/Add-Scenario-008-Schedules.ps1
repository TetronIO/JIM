# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Creates Full Synchronisation and Delta Synchronisation schedules for Scenario 008.

.DESCRIPTION
    Creates two schedules for the Scenario 008 cross-domain entitlement sync environment.
    This script is intended to be run AFTER Scenario 008 setup and test execution,
    to add schedules for demo/screenshot purposes and manual experimentation.
    Capture-Screenshots.ps1 -PrepareData runs it for the published screenshots (#582).

    It works on whichever directory type Scenario 008 ran against: the source system is the
    one whose name ends in "APAC" and the target the one ending in "EMEA" (Panoply APAC and
    Panoply EMEA on Samba AD; Yellowstone APAC and Glitterband EMEA on OpenLDAP and 389
    Directory Server).

    Both schedules are created disabled, so they never start on their own; enable them in the
    portal or with Set-JIMSchedule to see them run. The script is idempotent: it will not
    create schedules that already exist.

    Full Synchronisation Schedule (Sundays at 01:00):
    1. Full Import (APAC + EMEA in parallel)
    2. Full Sync (APAC then EMEA sequentially)
    3. Export (EMEA - target only)
    4. Delta Import (EMEA - target only, confirming the export)
    5. Delta Sync (EMEA - target only, confirming the export)

    Delta Synchronisation Schedule (hourly, Monday to Saturday):
    1. Delta Import (APAC + EMEA in parallel)
    2. Delta Sync (APAC then EMEA sequentially)
    3. Export (EMEA - target only)
    4. Delta Import (EMEA - target only, confirming the export)
    5. Delta Sync (EMEA - target only, confirming the export)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    Optional API key for authentication. If not specified, interactive browser-based
    SSO authentication is used.

.EXAMPLE
    ./Add-Scenario-008-Schedules.ps1

.EXAMPLE
    ./Add-Scenario-008-Schedules.ps1 -ApiKey "jim_abc123..."

.EXAMPLE
    ./Add-Scenario-008-Schedules.ps1 -JIMUrl "https://jim.company.com"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Add Schedules for Scenario 008"

# ============================================================================
# Step 1: Import JIM PowerShell module
# ============================================================================
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  Module imported" -ForegroundColor Green

# ============================================================================
# Step 2: Connect to JIM
# ============================================================================
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

try {
    if ($ApiKey) {
        Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    }
    else {
        Connect-JIM -Url $JIMUrl | Out-Null
    }
    Write-Host "  Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  Failed to connect to JIM: $_" -ForegroundColor Red
    throw
}

# ============================================================================
# Step 3: Verify connected systems exist
# ============================================================================
Write-TestStep "Step 3" "Verifying Scenario 008 connected systems"

# Scenario 008 names its systems after the directory it ran against, so find them by role.
$existingSystems = @(Get-JIMConnectedSystem)
$sourceCandidates = @($existingSystems | Where-Object { $_.name -like '* APAC' })
$targetCandidates = @($existingSystems | Where-Object { $_.name -like '* EMEA' })

if ($sourceCandidates.Count -ne 1 -or $targetCandidates.Count -ne 1) {
    $found = if ($existingSystems.Count -gt 0) { ($existingSystems.name -join ', ') } else { 'none' }
    throw "Expected one Connected System ending in 'APAC' (source) and one ending in 'EMEA' (target); found: $found. Run Scenario 008 setup first."
}

$sourceName = $sourceCandidates[0].name
$targetName = $targetCandidates[0].name
Write-Host "  Source: $sourceName (ID: $($sourceCandidates[0].id))" -ForegroundColor Green
Write-Host "  Target: $targetName (ID: $($targetCandidates[0].id))" -ForegroundColor Green

# ============================================================================
# Step 4: Check for existing schedules
# ============================================================================
Write-TestStep "Step 4" "Checking for existing schedules"

$existingSchedules = Get-JIMSchedule
$fullSyncScheduleName = "Full Synchronisation Schedule"
$deltaSyncScheduleName = "Delta Synchronisation Schedule"

$existingFullSync = $existingSchedules | Where-Object { $_.name -eq $fullSyncScheduleName }
$existingDeltaSync = $existingSchedules | Where-Object { $_.name -eq $deltaSyncScheduleName }

if ($existingFullSync -and $existingDeltaSync) {
    Write-Host "  Both schedules already exist - nothing to do" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Existing schedules:" -ForegroundColor Cyan
    Write-Host "  Full Synchronisation:  $($existingFullSync.id)" -ForegroundColor Gray
    Write-Host "  Delta Synchronisation: $($existingDeltaSync.id)" -ForegroundColor Gray
    return
}

# Adds the steps both schedules share: import both systems in parallel, synchronise them in turn, export to the
# target, then a delta import and sync on the target to confirm what was exported.
function Add-SynchronisationCycle {
    param([string]$ScheduleId, [string]$ImportProfile, [string]$SyncProfile)

    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $sourceName -RunProfileName $ImportProfile | Out-Null
    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $targetName -RunProfileName $ImportProfile -Parallel | Out-Null
    Write-Host "    + $ImportProfile ($sourceName + $targetName in parallel)" -ForegroundColor Gray

    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $sourceName -RunProfileName $SyncProfile | Out-Null
    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $targetName -RunProfileName $SyncProfile | Out-Null
    Write-Host "    + $SyncProfile ($sourceName then $targetName sequentially)" -ForegroundColor Gray

    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $targetName -RunProfileName "Export" | Out-Null
    Write-Host "    + Export ($targetName)" -ForegroundColor Gray

    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $targetName -RunProfileName "Delta Import" | Out-Null
    Add-JIMScheduleStep -ScheduleId $ScheduleId -StepType RunProfile -ConnectedSystemName $targetName -RunProfileName "Delta Sync" | Out-Null
    Write-Host "    + Delta Import and Delta Sync ($targetName, confirming the export)" -ForegroundColor Gray
}

# ============================================================================
# Step 5: Create Full Synchronisation schedule
# ============================================================================
Write-TestStep "Step 5" "Creating Full Synchronisation schedule"

if ($existingFullSync) {
    Write-Host "  Schedule '$fullSyncScheduleName' already exists (ID: $($existingFullSync.id))" -ForegroundColor Yellow
    $fullSyncSchedule = $existingFullSync
}
else {
    $fullSyncSchedule = New-JIMSchedule `
        -Name $fullSyncScheduleName `
        -Description "Weekly mop-up schedule." `
        -TriggerType Cron `
        -PatternType SpecificTimes `
        -DaysOfWeek @(0) `
        -RunTimes @("01:00") `
        -PassThru
    Write-Host "  Created schedule '$fullSyncScheduleName' (ID: $($fullSyncSchedule.id))" -ForegroundColor Green
    Write-Host "    Adding steps..." -ForegroundColor Gray
    Add-SynchronisationCycle -ScheduleId $fullSyncSchedule.id -ImportProfile "Full Import" -SyncProfile "Full Sync"
    Write-Host "  Full Synchronisation schedule created with all steps" -ForegroundColor Green
}

# ============================================================================
# Step 6: Create Delta Synchronisation schedule
# ============================================================================
Write-TestStep "Step 6" "Creating Delta Synchronisation schedule"

if ($existingDeltaSync) {
    Write-Host "  Schedule '$deltaSyncScheduleName' already exists (ID: $($existingDeltaSync.id))" -ForegroundColor Yellow
    $deltaSyncSchedule = $existingDeltaSync
}
else {
    $deltaSyncSchedule = New-JIMSchedule `
        -Name $deltaSyncScheduleName `
        -Description "Normal BAU synchronisation schedule." `
        -TriggerType Cron `
        -PatternType Interval `
        -DaysOfWeek @(1, 2, 3, 4, 5, 6) `
        -IntervalValue 1 `
        -IntervalUnit Hours `
        -PassThru
    Write-Host "  Created schedule '$deltaSyncScheduleName' (ID: $($deltaSyncSchedule.id))" -ForegroundColor Green
    Write-Host "    Adding steps..." -ForegroundColor Gray
    Add-SynchronisationCycle -ScheduleId $deltaSyncSchedule.id -ImportProfile "Delta Import" -SyncProfile "Delta Sync"
    Write-Host "  Delta Synchronisation schedule created with all steps" -ForegroundColor Green
}

# ============================================================================
# Summary
# ============================================================================
Write-Host ""
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host "  Schedules Created Successfully" -ForegroundColor Green
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host ""
Write-Host "  Full Synchronisation:  $($fullSyncSchedule.id)" -ForegroundColor Cyan
Write-Host "  Delta Synchronisation: $($deltaSyncSchedule.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Both schedules are disabled, so they never start on their own." -ForegroundColor Gray
Write-Host "  Enable them in the JIM UI or with Set-JIMSchedule, or run one with Start-JIMSchedule." -ForegroundColor Gray
Write-Host ""
