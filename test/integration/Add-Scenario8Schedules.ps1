<#
.SYNOPSIS
    Creates Full Sync and Delta Sync schedules for Scenario 8.

.DESCRIPTION
    Creates two schedules for the Scenario 8 cross-domain entitlement sync environment.
    This script is intended to be run AFTER Scenario 8 setup and test execution,
    to add schedules for demo/screenshot purposes and manual experimentation.

    The script is idempotent - it will not create schedules that already exist.

    Full Sync Schedule:
    1. Full Import (APAC + EMEA in parallel)
    2. Full Sync (APAC then EMEA sequentially)
    3. Export (EMEA - target only, in parallel if multiple targets)
    4. Delta Import (EMEA - target only, in parallel if multiple targets)
    5. Delta Sync (EMEA - target only, sequentially)

    Delta Sync Schedule:
    1. Delta Import (APAC + EMEA in parallel)
    2. Delta Sync (APAC then EMEA sequentially)
    3. Export (EMEA - target only, in parallel if multiple targets)
    4. Delta Import (EMEA - target only, in parallel if multiple targets)
    5. Delta Sync (EMEA - target only, sequentially)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    Optional API key for authentication. If not specified, interactive browser-based
    SSO authentication is used.

.EXAMPLE
    ./Add-Scenario8Schedules.ps1

.EXAMPLE
    ./Add-Scenario8Schedules.ps1 -ApiKey "jim_abc123..."

.EXAMPLE
    ./Add-Scenario8Schedules.ps1 -JIMUrl "https://jim.company.com"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Add Schedules for Scenario 8"

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
Write-TestStep "Step 3" "Verifying Scenario 8 connected systems"

$existingSystems = Get-JIMConnectedSystem

$sourceSystem = $existingSystems | Where-Object { $_.name -eq "Panoply APAC" }
$targetSystem = $existingSystems | Where-Object { $_.name -eq "Panoply EMEA" }

if (-not $sourceSystem) {
    throw "Source connected system 'Panoply APAC' not found. Run Scenario 8 setup first."
}
if (-not $targetSystem) {
    throw "Target connected system 'Panoply EMEA' not found. Run Scenario 8 setup first."
}

Write-Host "  Source: Panoply APAC (ID: $($sourceSystem.id))" -ForegroundColor Green
Write-Host "  Target: Panoply EMEA (ID: $($targetSystem.id))" -ForegroundColor Green

# ============================================================================
# Step 4: Check for existing schedules
# ============================================================================
Write-TestStep "Step 4" "Checking for existing schedules"

$existingSchedules = Get-JIMSchedule
$fullSyncScheduleName = "Scenario 8 - Full Sync"
$deltaSyncScheduleName = "Scenario 8 - Delta Sync"

$existingFullSync = $existingSchedules | Where-Object { $_.name -eq $fullSyncScheduleName }
$existingDeltaSync = $existingSchedules | Where-Object { $_.name -eq $deltaSyncScheduleName }

if ($existingFullSync -and $existingDeltaSync) {
    Write-Host "  Both schedules already exist - nothing to do" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Existing schedules:" -ForegroundColor Cyan
    Write-Host "  Full Sync:  $($existingFullSync.id)" -ForegroundColor Gray
    Write-Host "  Delta Sync: $($existingDeltaSync.id)" -ForegroundColor Gray
    return
}

# ============================================================================
# Step 5: Create Full Sync schedule
# ============================================================================
Write-TestStep "Step 5" "Creating Full Sync schedule"

if ($existingFullSync) {
    Write-Host "  Schedule '$fullSyncScheduleName' already exists (ID: $($existingFullSync.id))" -ForegroundColor Yellow
    $fullSyncSchedule = $existingFullSync
}
else {
    $fullSyncSchedule = New-JIMSchedule `
        -Name $fullSyncScheduleName `
        -Description "Full synchronisation cycle: full import all systems, full sync sequentially, export targets, confirming delta import and sync on targets" `
        -TriggerType Manual `
        -PassThru

    Write-Host "  Created schedule '$fullSyncScheduleName' (ID: $($fullSyncSchedule.id))" -ForegroundColor Green

    # Step 5a: Full Import - all systems in parallel
    Write-Host "    Adding steps..." -ForegroundColor Gray

    # Full Import APAC (sequential - first step)
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply APAC" `
        -RunProfileName "Full Import" | Out-Null

    # Full Import EMEA (parallel with APAC)
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Full Import" `
        -Parallel | Out-Null

    Write-Host "    + Full Import (APAC + EMEA in parallel)" -ForegroundColor Gray

    # Step 5b: Full Sync - all systems sequentially
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply APAC" `
        -RunProfileName "Full Sync" | Out-Null

    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Full Sync" | Out-Null

    Write-Host "    + Full Sync (APAC then EMEA sequentially)" -ForegroundColor Gray

    # Step 5c: Export - target systems in parallel
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Export" | Out-Null

    Write-Host "    + Export (EMEA)" -ForegroundColor Gray

    # Step 5d: Delta Import - target systems in parallel (confirming export)
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Import" | Out-Null

    Write-Host "    + Delta Import (EMEA - confirming export)" -ForegroundColor Gray

    # Step 5e: Delta Sync - target systems sequentially (confirming export)
    Add-JIMScheduleStep -ScheduleId $fullSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Sync" | Out-Null

    Write-Host "    + Delta Sync (EMEA - confirming export)" -ForegroundColor Gray
    Write-Host "  Full Sync schedule created with all steps" -ForegroundColor Green
}

# ============================================================================
# Step 6: Create Delta Sync schedule
# ============================================================================
Write-TestStep "Step 6" "Creating Delta Sync schedule"

if ($existingDeltaSync) {
    Write-Host "  Schedule '$deltaSyncScheduleName' already exists (ID: $($existingDeltaSync.id))" -ForegroundColor Yellow
    $deltaSyncSchedule = $existingDeltaSync
}
else {
    $deltaSyncSchedule = New-JIMSchedule `
        -Name $deltaSyncScheduleName `
        -Description "Delta synchronisation cycle: delta import all systems, delta sync sequentially, export targets, confirming delta import and sync on targets" `
        -TriggerType Manual `
        -PassThru

    Write-Host "  Created schedule '$deltaSyncScheduleName' (ID: $($deltaSyncSchedule.id))" -ForegroundColor Green

    # Step 6a: Delta Import - all source systems in parallel
    Write-Host "    Adding steps..." -ForegroundColor Gray

    # Delta Import APAC (sequential - first step)
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply APAC" `
        -RunProfileName "Delta Import" | Out-Null

    # Delta Import EMEA (parallel with APAC)
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Import" `
        -Parallel | Out-Null

    Write-Host "    + Delta Import (APAC + EMEA in parallel)" -ForegroundColor Gray

    # Step 6b: Delta Sync - all systems sequentially
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply APAC" `
        -RunProfileName "Delta Sync" | Out-Null

    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Sync" | Out-Null

    Write-Host "    + Delta Sync (APAC then EMEA sequentially)" -ForegroundColor Gray

    # Step 6c: Export - target systems in parallel
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Export" | Out-Null

    Write-Host "    + Export (EMEA)" -ForegroundColor Gray

    # Step 6d: Delta Import - target systems in parallel (confirming export)
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Import" | Out-Null

    Write-Host "    + Delta Import (EMEA - confirming export)" -ForegroundColor Gray

    # Step 6e: Delta Sync - target systems sequentially (confirming export)
    Add-JIMScheduleStep -ScheduleId $deltaSyncSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemName "Panoply EMEA" `
        -RunProfileName "Delta Sync" | Out-Null

    Write-Host "    + Delta Sync (EMEA - confirming export)" -ForegroundColor Gray
    Write-Host "  Delta Sync schedule created with all steps" -ForegroundColor Green
}

# ============================================================================
# Summary
# ============================================================================
Write-Host ""
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host "  Schedules Created Successfully" -ForegroundColor Green
Write-Host "=" * 70 -ForegroundColor Cyan
Write-Host ""
Write-Host "  Full Sync:  $($fullSyncSchedule.id)" -ForegroundColor Cyan
Write-Host "  Delta Sync: $($deltaSyncSchedule.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Both schedules are set to Manual trigger." -ForegroundColor Gray
Write-Host "  Use the JIM UI or Start-JIMSchedule to run them." -ForegroundColor Gray
Write-Host ""
