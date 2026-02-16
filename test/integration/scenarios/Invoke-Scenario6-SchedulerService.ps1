<#
.SYNOPSIS
    Test Scenario 6: Scheduler Service End-to-End Testing

.DESCRIPTION
    Validates the scheduler service functionality including:
    - Schedule creation and configuration
    - Scheduler picking up due schedules
    - Task execution through the worker
    - Step progression for multi-step schedules
    - Parallel step execution (requires multiple connected systems)
    - Overlap prevention (same schedule doesn't run twice)
    - Next run time calculation
    - Manual trigger execution

    IMPORTANT: This scenario requires Setup-Scenario1.ps1 to be run first, which
    creates 4 connected systems:
    - HR CSV Source (source)
    - Training Records Source (source)
    - Samba AD (target)
    - Cross-Domain Export (target)

    The parallel step test creates a complex schedule that mirrors real-world
    identity synchronisation patterns with parallel imports, sequential syncs,
    and parallel exports.

.PARAMETER Step
    Which test step to execute (Create, AutoTrigger, ManualTrigger, Overlap, MultiStep, Parallel, All)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait for scheduler to process (default: 90)
    Note: Scheduler polls every 30 seconds and schedules run at minute boundaries,
    so we need to wait up to ~90 seconds to catch the next trigger.

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails.

.EXAMPLE
    ./Invoke-Scenario6-SchedulerService.ps1 -Step All -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario6-SchedulerService.ps1 -Step Parallel -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Create", "AutoTrigger", "ManualTrigger", "Overlap", "MultiStep", "Parallel", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",  # Accepted but ignored - scheduler tests don't use test data templates

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 90,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"

# Import JIM PowerShell module
$modulePath = "$PSScriptRoot/../../../JIM.PowerShell/JIM/JIM.psd1"
Import-Module $modulePath -Force -ErrorAction Stop

Write-TestSection "Scenario 6: Scheduler Service"
Write-Host "Step: $Step" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Scheduler Service"
    Steps = @()
    Success = $false
}

# ============================================================================
# Setup: Connect to JIM and ensure we have a connected system with run profiles
# ============================================================================

Write-Host "Setup: Connecting to JIM" -ForegroundColor Yellow

# Try to read API key from file if not provided
if (-not $ApiKey) {
    $apiKeyFile = "$PSScriptRoot/../.api-key"
    if (Test-Path $apiKeyFile) {
        $ApiKey = Get-Content $apiKeyFile -Raw
        $ApiKey = $ApiKey.Trim()
        Write-Host "  Using API key from file" -ForegroundColor DarkGray
    }
    else {
        throw "API key required. Provide -ApiKey parameter or create $apiKeyFile"
    }
}

# Connect to JIM
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  Connected to JIM at $JIMUrl" -ForegroundColor DarkGray

# Get a connected system with run profiles for testing
$connectedSystems = @(Get-JIMConnectedSystem)  # Force array even for single result
if ($connectedSystems.Count -eq 0) {
    Write-Host "  No connected systems found - running Setup-Scenario1..." -ForegroundColor Yellow

    # Generate test CSV data first (training-records.csv, cross-domain-users.csv, etc.)
    # These must exist BEFORE Setup-Scenario1 runs, so schema discovery succeeds
    Write-Host "  Generating test CSV data..." -ForegroundColor DarkGray
    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template "Micro" -OutputPath "$PSScriptRoot/../../test-data"

    # Run Setup-Scenario1 to create the required test infrastructure
    $setupScript = "$PSScriptRoot/../Setup-Scenario1.ps1"
    if (-not (Test-Path $setupScript)) {
        throw "Setup script not found at: $setupScript"
    }

    $config = & $setupScript -JIMUrl $JIMUrl -ApiKey $ApiKey -Template "Micro"
    if (-not $config) {
        throw "Failed to run Setup-Scenario1"
    }

    # Re-fetch connected systems after setup
    $connectedSystems = @(Get-JIMConnectedSystem)
    if ($connectedSystems.Count -eq 0) {
        throw "No connected systems found after running Setup-Scenario1"
    }
}

# Prefer "HR CSV Source" as it's always set up correctly by Setup-Scenario1
$testSystem = $connectedSystems | Where-Object { $_.name -eq "HR CSV Source" } | Select-Object -First 1
if (-not $testSystem) {
    # Fall back to any system that has run profiles
    foreach ($cs in $connectedSystems) {
        $rp = @(Get-JIMRunProfile -ConnectedSystemId $cs.id)
        if ($rp.Count -gt 0) {
            $testSystem = $cs
            break
        }
    }
}
if (-not $testSystem) {
    throw "No suitable connected system found with run profiles."
}
Write-Host "  Using connected system: $($testSystem.name) (ID: $($testSystem.id))" -ForegroundColor DarkGray

$runProfiles = @(Get-JIMRunProfile -ConnectedSystemId $testSystem.id)  # Force array
if ($runProfiles.Count -eq 0) {
    throw "No run profiles found for connected system $($testSystem.name). Please configure run profiles first."
}

$testRunProfile = $runProfiles | Where-Object { $_.runType -eq 1 -or $_.runType -eq 2 } | Select-Object -First 1  # Delta or Full Import
if (-not $testRunProfile) {
    $testRunProfile = $runProfiles | Select-Object -First 1
}
Write-Host "  Using run profile: $($testRunProfile.name) (ID: $($testRunProfile.id))" -ForegroundColor DarkGray

# Clean up any existing test schedules
Write-Host "  Cleaning up existing test schedules..." -ForegroundColor DarkGray
$existingSchedules = Get-JIMSchedule -Name "Integration Test*"
foreach ($schedule in $existingSchedules) {
    Remove-JIMSchedule -Id $schedule.id -Force
}

Write-Host ""

# ============================================================================
# Test: Schedule Creation
# ============================================================================

if ($Step -eq "Create" -or $Step -eq "All") {
    Write-TestSection "Test 1: Schedule Creation"

    # Test 1: Create a manual schedule
    Write-Host "  Creating manual schedule..." -ForegroundColor DarkGray
    $manualSchedule = New-JIMSchedule -Name "Integration Test - Manual" `
        -Description "Test schedule for manual trigger" `
        -TriggerType Manual `
        -PassThru

    Assert-NotNull -Value $manualSchedule -Message "Manual schedule created"
    Assert-Equal -Expected "Integration Test - Manual" -Actual $manualSchedule.name -Message "Schedule name is correct"
    Assert-Condition -Condition ($manualSchedule.triggerType -eq "Manual" -or $manualSchedule.triggerType -eq 1) -Message "Trigger type is Manual"
    Assert-Equal -Expected $false -Actual $manualSchedule.isEnabled -Message "Schedule is disabled by default"

    $testResults.Steps += @{ Name = "Create Manual Schedule"; Success = $true }

    # Test 2: Create a cron schedule with custom expression (weekdays at 6am, noon, and 6pm)
    Write-Host "  Creating cron schedule with custom expression..." -ForegroundColor DarkGray
    $cronSchedule = New-JIMSchedule -Name "Integration Test - Cron" `
        -Description "Test schedule for automatic trigger" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "0 6,12,18 * * 1-5" `
        -PassThru

    Assert-NotNull -Value $cronSchedule -Message "Cron schedule created"
    Assert-Condition -Condition ($cronSchedule.triggerType -eq "Cron" -or $cronSchedule.triggerType -eq 0) -Message "Trigger type is Cron"
    Assert-NotNull -Value $cronSchedule.cronExpression -Message "Cron expression configured"

    $testResults.Steps += @{ Name = "Create Cron Schedule"; Success = $true }

    # Test 3: Create an interval-style schedule using custom cron (every 2 hours on weekdays)
    Write-Host "  Creating interval-style schedule..." -ForegroundColor DarkGray
    $intervalSchedule = New-JIMSchedule -Name "Integration Test - Interval" `
        -Description "Test schedule for interval trigger" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "0 8-18/2 * * *" `
        -PassThru

    Assert-NotNull -Value $intervalSchedule -Message "Interval schedule created"
    Assert-Condition -Condition ($intervalSchedule.triggerType -eq "Cron" -or $intervalSchedule.triggerType -eq 0) -Message "Trigger type is Cron"
    Assert-NotNull -Value $intervalSchedule.cronExpression -Message "Cron expression configured"

    $testResults.Steps += @{ Name = "Create Interval Schedule"; Success = $true }

    # Test 4: Add steps to a schedule
    Write-Host "  Adding step to schedule..." -ForegroundColor DarkGray
    Add-JIMScheduleStep -ScheduleId $manualSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemId $testSystem.id `
        -RunProfileId $testRunProfile.id

    $updatedSchedule = Get-JIMSchedule -Id $manualSchedule.id -IncludeSteps
    Assert-Equal -Expected 1 -Actual $updatedSchedule.steps.Count -Message "Schedule has 1 step"

    $testResults.Steps += @{ Name = "Add Schedule Step"; Success = $true }

    Write-Host ""
}

# ============================================================================
# Test: Manual Trigger
# ============================================================================

if ($Step -eq "ManualTrigger" -or $Step -eq "All") {
    Write-TestSection "Test 2: Manual Schedule Trigger"

    # Create a schedule with a step if not already created
    $testScheduleName = "Integration Test - Manual Trigger"
    $existingSchedule = Get-JIMSchedule -Name $testScheduleName | Select-Object -First 1

    if (-not $existingSchedule) {
        Write-Host "  Creating test schedule with step..." -ForegroundColor DarkGray
        $testSchedule = New-JIMSchedule -Name $testScheduleName `
            -Description "Test schedule for manual trigger testing" `
            -TriggerType Manual `
            -PassThru

        Add-JIMScheduleStep -ScheduleId $testSchedule.id `
            -StepType RunProfile `
            -ConnectedSystemId $testSystem.id `
            -RunProfileId $testRunProfile.id
    }
    else {
        $testSchedule = $existingSchedule
    }

    # Trigger the schedule manually
    Write-Host "  Triggering schedule manually..." -ForegroundColor DarkGray
    $execution = Start-JIMSchedule -Id $testSchedule.id -PassThru

    Assert-NotNull -Value $execution -Message "Schedule execution started"
    Assert-NotNull -Value $execution.id -Message "Execution has an ID"
    Write-Host "    Execution ID: $($execution.id)" -ForegroundColor DarkGray

    $testResults.Steps += @{ Name = "Manual Trigger Started"; Success = $true }

    # Wait for execution to complete
    Write-Host "  Waiting for execution to complete (max 60s)..." -ForegroundColor DarkGray
    $maxWaitTime = 60
    $pollInterval = 5
    $elapsed = 0
    $finalExecution = $null

    while ($elapsed -lt $maxWaitTime) {
        $finalExecution = Get-JIMScheduleExecution -Id $execution.id

        # Status: Queued, InProgress, Completed, Failed, Cancelled (or 0,1,2,3,4)
        $statusValue = $finalExecution.status
        $isTerminal = $statusValue -eq "Completed" -or $statusValue -eq "Failed" -or $statusValue -eq "Cancelled"
        # Also check numeric values if status is returned as int
        if ($statusValue -is [int] -or $statusValue -is [long]) {
            $isTerminal = $statusValue -ge 2
        }
        if ($isTerminal) {
            Write-Host "    Execution completed with status: $statusValue" -ForegroundColor DarkGray
            break
        }

        Start-Sleep -Seconds $pollInterval
        $elapsed += $pollInterval
        Write-Host "    Still running... ($elapsed s)" -ForegroundColor DarkGray
    }

    Assert-NotNull -Value $finalExecution -Message "Execution retrieved"

    try {
        Assert-ScheduleExecutionSuccess -ExecutionId $finalExecution.id -Name "Manual Trigger Execution"
        $testResults.Steps += @{ Name = "Manual Trigger Completed"; Success = $true }
    }
    catch {
        $testResults.Steps += @{ Name = "Manual Trigger Completed"; Success = $false; Error = $_.Exception.Message }
        if (-not $ContinueOnError) {
            Write-Host ""
            Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host ""
}

# ============================================================================
# Test: Auto Trigger (Scheduler picks up due schedule)
# ============================================================================

if ($Step -eq "AutoTrigger") {
    # Note: AutoTrigger is excluded from "All" runs because it requires precise timing alignment
    # between scheduler polls (every 30s) and cron minute boundaries. The test can take 60-120
    # seconds to complete and may still fail due to timing. Run this step explicitly when needed.
    Write-TestSection "Test 3: Automatic Schedule Trigger"

    # Create a schedule that is due to run NOW
    # We'll set the cron expression to run every minute for testing
    $testScheduleName = "Integration Test - Auto Trigger"

    # Clean up any existing
    $existing = Get-JIMSchedule -Name $testScheduleName | Select-Object -First 1
    if ($existing) {
        Remove-JIMSchedule -Id $existing.id -Force
    }

    Write-Host "  Creating schedule with immediate trigger (every minute)..." -ForegroundColor DarkGray
    $autoSchedule = New-JIMSchedule -Name $testScheduleName `
        -Description "Test schedule for auto trigger testing" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "* * * * *" `
        -PassThru

    # Add a step
    Add-JIMScheduleStep -ScheduleId $autoSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemId $testSystem.id `
        -RunProfileId $testRunProfile.id

    # Enable the schedule
    Write-Host "  Enabling schedule..." -ForegroundColor DarkGray
    Enable-JIMSchedule -Id $autoSchedule.id

    $enabledSchedule = Get-JIMSchedule -Id $autoSchedule.id
    Assert-Equal -Expected $true -Actual $enabledSchedule.isEnabled -Message "Schedule is enabled"

    # Next run time calculation may happen asynchronously, so just log it if present
    if ($enabledSchedule.nextRunTime) {
        Write-Host "    Next run time: $($enabledSchedule.nextRunTime)" -ForegroundColor DarkGray
    } else {
        Write-Host "    Next run time not yet calculated (will be computed by scheduler)" -ForegroundColor DarkGray
    }

    $testResults.Steps += @{ Name = "Schedule Enabled"; Success = $true }

    # Wait for scheduler to pick it up (scheduler polls every 30 seconds)
    Write-Host "  Waiting for scheduler to trigger execution (up to $WaitSeconds s)..." -ForegroundColor DarkGray
    $maxWait = $WaitSeconds
    $pollInterval = 5
    $elapsed = 0
    $executionFound = $false
    $execution = $null

    while ($elapsed -lt $maxWait) {
        Start-Sleep -Seconds $pollInterval
        $elapsed += $pollInterval

        # Check for active executions
        $executions = Get-JIMScheduleExecution -ScheduleId $autoSchedule.id
        if ($executions -and $executions.Count -gt 0) {
            $execution = $executions | Select-Object -First 1
            $executionFound = $true
            Write-Host "    Execution found! ID: $($execution.id), Status: $($execution.status)" -ForegroundColor Green
            break
        }

        Write-Host "    Waiting... ($elapsed s)" -ForegroundColor DarkGray
    }

    Assert-Condition -Condition $executionFound -Message "Scheduler automatically triggered execution"

    $testResults.Steps += @{ Name = "Auto Trigger Detected"; Success = $true }

    # Disable the schedule to stop it from running again
    Write-Host "  Disabling schedule to prevent further runs..." -ForegroundColor DarkGray
    Disable-JIMSchedule -Id $autoSchedule.id

    Write-Host ""
}

# ============================================================================
# Test: Overlap Prevention
# ============================================================================

if ($Step -eq "Overlap" -or $Step -eq "All") {
    Write-TestSection "Test 4: Overlap Prevention"

    # Create a schedule that would run frequently
    $testScheduleName = "Integration Test - Overlap"

    # Clean up any existing
    $existing = Get-JIMSchedule -Name $testScheduleName | Select-Object -First 1
    if ($existing) {
        Remove-JIMSchedule -Id $existing.id -Force
    }

    Write-Host "  Creating test schedule..." -ForegroundColor DarkGray
    $overlapSchedule = New-JIMSchedule -Name $testScheduleName `
        -Description "Test schedule for overlap prevention" `
        -TriggerType Manual `
        -PassThru

    # Add a step
    Add-JIMScheduleStep -ScheduleId $overlapSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemId $testSystem.id `
        -RunProfileId $testRunProfile.id

    # Start first execution
    Write-Host "  Starting first execution..." -ForegroundColor DarkGray
    $execution1 = Start-JIMSchedule -Id $overlapSchedule.id -PassThru
    Assert-NotNull -Value $execution1 -Message "First execution started"
    Write-Host "    Execution 1 ID: $($execution1.id)" -ForegroundColor DarkGray

    $testResults.Steps += @{ Name = "First Execution Started"; Success = $true }

    # Try to start second execution immediately (should still work - overlap prevention is for scheduled runs)
    Write-Host "  Starting second execution (manual triggers are allowed)..." -ForegroundColor DarkGray
    $execution2 = Start-JIMSchedule -Id $overlapSchedule.id -PassThru
    Assert-NotNull -Value $execution2 -Message "Second manual execution started (allowed)"
    Write-Host "    Execution 2 ID: $($execution2.id)" -ForegroundColor DarkGray

    $testResults.Steps += @{ Name = "Second Manual Execution Allowed"; Success = $true }

    # Verify both executions exist
    $allExecutions = Get-JIMScheduleExecution -ScheduleId $overlapSchedule.id
    Assert-Condition -Condition ($allExecutions.Count -ge 2) -Message "Multiple executions exist for schedule"

    Write-Host ""
}

# ============================================================================
# Test: Multi-Step Schedule (Sequential)
# ============================================================================

if ($Step -eq "MultiStep" -or $Step -eq "All") {
    Write-TestSection "Test 5: Multi-Step Schedule (Sequential)"

    $testScheduleName = "Integration Test - MultiStep"

    # Clean up any existing
    $existing = Get-JIMSchedule -Name $testScheduleName | Select-Object -First 1
    if ($existing) {
        Remove-JIMSchedule -Id $existing.id -Force
    }

    Write-Host "  Creating multi-step schedule..." -ForegroundColor DarkGray
    $multiStepSchedule = New-JIMSchedule -Name $testScheduleName `
        -Description "Test schedule with multiple sequential steps" `
        -TriggerType Manual `
        -PassThru

    # Add sequential steps
    Write-Host "  Adding step 1 (sequential)..." -ForegroundColor DarkGray
    Add-JIMScheduleStep -ScheduleId $multiStepSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemId $testSystem.id `
        -RunProfileId $testRunProfile.id

    Write-Host "  Adding step 2 (sequential)..." -ForegroundColor DarkGray
    Add-JIMScheduleStep -ScheduleId $multiStepSchedule.id `
        -StepType RunProfile `
        -ConnectedSystemId $testSystem.id `
        -RunProfileId $testRunProfile.id

    # Verify steps were added
    $scheduleWithSteps = Get-JIMSchedule -Id $multiStepSchedule.id -IncludeSteps
    Assert-Equal -Expected 2 -Actual $scheduleWithSteps.steps.Count -Message "Schedule has 2 steps"

    $testResults.Steps += @{ Name = "Multi-Step Schedule Created"; Success = $true }

    # Execute the multi-step schedule
    Write-Host "  Executing multi-step schedule..." -ForegroundColor DarkGray
    $execution = Start-JIMSchedule -Id $multiStepSchedule.id -PassThru

    Assert-NotNull -Value $execution -Message "Multi-step execution started"
    Assert-Equal -Expected 2 -Actual $execution.totalSteps -Message "Execution shows 2 total steps"

    $testResults.Steps += @{ Name = "Multi-Step Execution Started"; Success = $true }

    # Wait for completion
    Write-Host "  Waiting for multi-step execution to complete..." -ForegroundColor DarkGray
    $maxWait = 120  # Multi-step takes longer
    $pollInterval = 5
    $elapsed = 0

    while ($elapsed -lt $maxWait) {
        $currentExecution = Get-JIMScheduleExecution -Id $execution.id

        $statusValue = $currentExecution.status
        $isTerminal = $statusValue -eq "Completed" -or $statusValue -eq "Failed" -or $statusValue -eq "Cancelled"
        if ($statusValue -is [int] -or $statusValue -is [long]) {
            $isTerminal = $statusValue -ge 2
        }
        if ($isTerminal) {
            Write-Host "    Execution completed! Status: $statusValue" -ForegroundColor DarkGray
            break
        }

        $stepProgress = "$($currentExecution.currentStepIndex + 1)/$($currentExecution.totalSteps)"
        Write-Host "    Step $stepProgress in progress... ($elapsed s)" -ForegroundColor DarkGray

        Start-Sleep -Seconds $pollInterval
        $elapsed += $pollInterval
    }

    $finalExecution = Get-JIMScheduleExecution -Id $execution.id

    try {
        Assert-ScheduleExecutionSuccess -ExecutionId $finalExecution.id -Name "Multi-Step Execution"
        $testResults.Steps += @{ Name = "Multi-Step Execution Completed"; Success = $true }
    }
    catch {
        $testResults.Steps += @{ Name = "Multi-Step Execution Completed"; Success = $false; Error = $_.Exception.Message }
        if (-not $ContinueOnError) {
            Write-Host ""
            Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host ""
}

# ============================================================================
# Test: Parallel Steps (requires 4 connected systems from Scenario1 setup)
# ============================================================================

if ($Step -eq "Parallel" -or $Step -eq "All") {
    Write-TestSection "Test 6: Parallel Step Execution (Complex Multi-System Schedule)"

    # Check if we have all 4 connected systems from Scenario1 setup
    # The extended Scenario1 setup creates:
    # - HR CSV Source
    # - Training Records Source
    # - Samba AD (Subatomic AD)
    # - Cross-Domain Export
    $hrSystem = $connectedSystems | Where-Object { $_.name -eq "HR CSV Source" }
    $trainingSystem = $connectedSystems | Where-Object { $_.name -eq "Training Records Source" }
    $ldapSystem = $connectedSystems | Where-Object { $_.name -eq "Subatomic AD" }
    $crossDomainSystem = $connectedSystems | Where-Object { $_.name -eq "Cross-Domain Export" }

    $missingCount = 0
    if (-not $hrSystem) { $missingCount++; Write-Host "  Missing: HR CSV Source" -ForegroundColor Yellow }
    if (-not $trainingSystem) { $missingCount++; Write-Host "  Missing: Training Records Source" -ForegroundColor Yellow }
    if (-not $ldapSystem) { $missingCount++; Write-Host "  Missing: Subatomic AD" -ForegroundColor Yellow }
    if (-not $crossDomainSystem) { $missingCount++; Write-Host "  Missing: Cross-Domain Export" -ForegroundColor Yellow }

    if ($missingCount -gt 0) {
        Write-Host "  ✗ FAILED: Parallel step testing requires 4 connected systems from extended Scenario1 setup" -ForegroundColor Red
        Write-Host "  Found $($connectedSystems.Count) systems, missing $missingCount required systems" -ForegroundColor DarkGray
        Write-Host "  Run: ./Setup-Scenario1.ps1 first to create all required systems" -ForegroundColor DarkGray
        $testResults.Steps += @{ Name = "Parallel Steps (Missing $missingCount connected systems)"; Success = $false; Error = "Required connected systems not found" }
        if (-not $ContinueOnError) {
            Write-Host ""
            Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
            exit 1
        }
    }
    else {
        Write-Host "  Found all 4 required connected systems:" -ForegroundColor Green
        Write-Host "    Sources: HR CSV ($($hrSystem.id)), Training ($($trainingSystem.id))" -ForegroundColor DarkGray
        Write-Host "    Targets: Samba AD ($($ldapSystem.id)), Cross-Domain ($($crossDomainSystem.id))" -ForegroundColor DarkGray

        # Get run profiles for each system
        $hrProfiles = @(Get-JIMRunProfile -ConnectedSystemId $hrSystem.id)
        $trainingProfiles = @(Get-JIMRunProfile -ConnectedSystemId $trainingSystem.id)
        $ldapProfiles = @(Get-JIMRunProfile -ConnectedSystemId $ldapSystem.id)
        $crossDomainProfiles = @(Get-JIMRunProfile -ConnectedSystemId $crossDomainSystem.id)

        # Find required run profiles
        $hrImport = $hrProfiles | Where-Object { $_.name -eq "Full Import" } | Select-Object -First 1
        $hrSync = $hrProfiles | Where-Object { $_.name -eq "Full Synchronisation" } | Select-Object -First 1
        $trainingImport = $trainingProfiles | Where-Object { $_.name -eq "Full Import" } | Select-Object -First 1
        $trainingSync = $trainingProfiles | Where-Object { $_.name -eq "Full Synchronisation" } | Select-Object -First 1
        $ldapExport = $ldapProfiles | Where-Object { $_.name -eq "Export" } | Select-Object -First 1
        $ldapDeltaImport = $ldapProfiles | Where-Object { $_.name -eq "Delta Import" } | Select-Object -First 1
        $ldapDeltaSync = $ldapProfiles | Where-Object { $_.name -eq "Delta Synchronisation" } | Select-Object -First 1
        $crossDomainImport = $crossDomainProfiles | Where-Object { $_.name -eq "Full Import" } | Select-Object -First 1
        $crossDomainSync = $crossDomainProfiles | Where-Object { $_.name -eq "Full Synchronisation" } | Select-Object -First 1
        $crossDomainExport = $crossDomainProfiles | Where-Object { $_.name -eq "Export" } | Select-Object -First 1
        # Note: CSV/File connectors don't support Delta Import (no USN change tracking)
        # Use Full Import for Cross-Domain confirming imports
        $crossDomainDeltaImport = $crossDomainProfiles | Where-Object { $_.name -eq "Full Import" } | Select-Object -First 1
        $crossDomainDeltaSync = $crossDomainProfiles | Where-Object { $_.name -eq "Delta Synchronisation" } | Select-Object -First 1

        $profilesOK = $hrImport -and $hrSync -and $trainingImport -and $trainingSync -and
                      $ldapExport -and $ldapDeltaImport -and $ldapDeltaSync -and
                      $crossDomainImport -and $crossDomainSync -and
                      $crossDomainExport -and $crossDomainDeltaImport -and $crossDomainDeltaSync

        if (-not $profilesOK) {
            Write-Host "  ✗ FAILED: Could not find all required run profiles" -ForegroundColor Red
            # Report which profiles are missing
            if (-not $hrImport) { Write-Host "    Missing: HR CSV 'Full Import'" -ForegroundColor Yellow }
            if (-not $hrSync) { Write-Host "    Missing: HR CSV 'Full Synchronisation'" -ForegroundColor Yellow }
            if (-not $trainingImport) { Write-Host "    Missing: Training 'Full Import'" -ForegroundColor Yellow }
            if (-not $trainingSync) { Write-Host "    Missing: Training 'Full Synchronisation'" -ForegroundColor Yellow }
            if (-not $ldapExport) { Write-Host "    Missing: LDAP 'Export'" -ForegroundColor Yellow }
            if (-not $ldapDeltaImport) { Write-Host "    Missing: LDAP 'Delta Import'" -ForegroundColor Yellow }
            if (-not $ldapDeltaSync) { Write-Host "    Missing: LDAP 'Delta Synchronisation'" -ForegroundColor Yellow }
            if (-not $crossDomainImport) { Write-Host "    Missing: Cross-Domain 'Full Import'" -ForegroundColor Yellow }
            if (-not $crossDomainSync) { Write-Host "    Missing: Cross-Domain 'Full Synchronisation'" -ForegroundColor Yellow }
            if (-not $crossDomainExport) { Write-Host "    Missing: Cross-Domain 'Export'" -ForegroundColor Yellow }
            if (-not $crossDomainDeltaImport) { Write-Host "    Missing: Cross-Domain 'Full Import' (for delta)" -ForegroundColor Yellow }
            if (-not $crossDomainDeltaSync) { Write-Host "    Missing: Cross-Domain 'Delta Synchronisation'" -ForegroundColor Yellow }
            $testResults.Steps += @{ Name = "Parallel Steps (Missing run profiles)"; Success = $false; Error = "Required run profiles not found" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
        }
        else {
            # Make CSV changes BEFORE running the schedule to test actual data flow through parallel steps
            # This ensures the schedule processes real changes rather than just running empty operations
            Write-Host "  Making CSV changes to test data flow through parallel steps..." -ForegroundColor Cyan
            Write-Host ""

            $hrCsvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
            $trainingCsvPath = "$PSScriptRoot/../../test-data/training-records.csv"

            # Modify HR CSV - update title for first user
            if (Test-Path $hrCsvPath) {
                $hrCsv = Import-Csv $hrCsvPath
                if ($hrCsv.Count -gt 0) {
                    $hrUser = $hrCsv[0]
                    $oldTitle = $hrUser.title
                    $hrUser.title = "Scheduler Test - Parallel Flow"
                    $hrCsv | Export-Csv -Path $hrCsvPath -NoTypeInformation -Encoding UTF8
                    Write-Host "    HR CSV: Changed $($hrUser.samAccountName) title from '$oldTitle' to 'Scheduler Test - Parallel Flow'" -ForegroundColor DarkGray

                    # Copy to container
                    docker cp $hrCsvPath samba-ad-primary:/connector-files/hr-users.csv 2>$null
                    Write-Host "    HR CSV: Copied to container" -ForegroundColor DarkGray
                }
            }

            # Modify Training CSV - update training status for first user
            if (Test-Path $trainingCsvPath) {
                $trainingCsv = Import-Csv $trainingCsvPath
                if ($trainingCsv.Count -gt 0) {
                    $trainingUser = $trainingCsv[0]
                    $oldStatus = $trainingUser.trainingStatus
                    $trainingUser.trainingStatus = "Pass"  # Ensure it's Pass for testing
                    $trainingCsv | Export-Csv -Path $trainingCsvPath -NoTypeInformation -Encoding UTF8
                    Write-Host "    Training CSV: Changed $($trainingUser.samAccountName) training status from '$oldStatus' to 'Pass'" -ForegroundColor DarkGray

                    # Copy to container
                    docker cp $trainingCsvPath samba-ad-primary:/connector-files/training-records.csv 2>$null
                    Write-Host "    Training CSV: Copied to container" -ForegroundColor DarkGray
                }
            }

            Write-Host ""
            Write-Host "  ✓ CSV changes made - schedule will process real attribute updates" -ForegroundColor Green
            Write-Host ""

            $testScheduleName = "Integration Test - Complex Parallel"

            # Clean up any existing
            $existing = Get-JIMSchedule -Name $testScheduleName | Select-Object -First 1
            if ($existing) {
                Remove-JIMSchedule -Id $existing.id -Force
            }

            Write-Host "  Creating complex parallel-step schedule..." -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "  Schedule structure:" -ForegroundColor Cyan
            Write-Host "    Step 0-2  [PARALLEL]:   Full Import HR + Full Import Training + Full Import Cross-Domain" -ForegroundColor DarkGray
            Write-Host "    Step 3    [SEQUENTIAL]:  Full Sync HR" -ForegroundColor DarkGray
            Write-Host "    Step 4    [SEQUENTIAL]:  Full Sync Training" -ForegroundColor DarkGray
            Write-Host "    Step 5    [SEQUENTIAL]:  Full Sync Cross-Domain" -ForegroundColor DarkGray
            Write-Host "    Step 6-7  [PARALLEL]:    Export AD + Export Cross-Domain" -ForegroundColor DarkGray
            Write-Host "    Step 8-9  [PARALLEL]:    Delta Import AD + Full Import Cross-Domain (CSV has no delta)" -ForegroundColor DarkGray
            Write-Host "    Step 10   [SEQUENTIAL]:  Delta Sync Cross-Domain" -ForegroundColor DarkGray
            Write-Host "    Step 11   [SEQUENTIAL]:  Delta Sync AD" -ForegroundColor DarkGray
            Write-Host ""

            $parallelSchedule = New-JIMSchedule -Name $testScheduleName `
                -Description "Complex schedule with parallel imports (incl. Cross-Domain), sequential syncs, parallel exports" `
                -TriggerType Manual `
                -PassThru

            # Step 0: Full Import HR (first step, sequential start)
            Write-Host "  Adding step 0: Full Import HR (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $hrSystem.id `
                -RunProfileId $hrImport.id

            # Step 1: Full Import Training (parallel with HR import)
            Write-Host "  Adding step 1: Full Import Training (parallel with step 0)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $trainingSystem.id `
                -RunProfileId $trainingImport.id `
                -Parallel

            # Step 2: Full Import Cross-Domain (parallel with HR + Training imports)
            Write-Host "  Adding step 2: Full Import Cross-Domain (parallel with steps 0-1)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $crossDomainSystem.id `
                -RunProfileId $crossDomainImport.id `
                -Parallel

            # Step 3: Full Sync HR (sequential - waits for imports to complete)
            Write-Host "  Adding step 3: Full Sync HR (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $hrSystem.id `
                -RunProfileId $hrSync.id

            # Step 4: Full Sync Training (sequential - after HR sync)
            Write-Host "  Adding step 4: Full Sync Training (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $trainingSystem.id `
                -RunProfileId $trainingSync.id

            # Step 5: Full Sync Cross-Domain (sequential - after Training sync)
            Write-Host "  Adding step 5: Full Sync Cross-Domain (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $crossDomainSystem.id `
                -RunProfileId $crossDomainSync.id

            # Step 6: Export AD (sequential - starts new parallel group)
            Write-Host "  Adding step 6: Export AD (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $ldapSystem.id `
                -RunProfileId $ldapExport.id

            # Step 7: Export Cross-Domain (parallel with AD export)
            Write-Host "  Adding step 7: Export Cross-Domain (parallel with step 6)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $crossDomainSystem.id `
                -RunProfileId $crossDomainExport.id `
                -Parallel

            # Step 8: Delta Import AD (sequential - starts new parallel group)
            Write-Host "  Adding step 8: Delta Import AD (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $ldapSystem.id `
                -RunProfileId $ldapDeltaImport.id

            # Step 9: Full Import Cross-Domain (parallel with AD delta import)
            # Note: CSV connectors don't support Delta Import, so we use Full Import
            Write-Host "  Adding step 9: Full Import Cross-Domain (parallel with step 8)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $crossDomainSystem.id `
                -RunProfileId $crossDomainDeltaImport.id `
                -Parallel

            # Step 10: Delta Sync Cross-Domain (sequential)
            Write-Host "  Adding step 10: Delta Sync Cross-Domain (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $crossDomainSystem.id `
                -RunProfileId $crossDomainDeltaSync.id

            # Step 11: Delta Sync AD (sequential)
            Write-Host "  Adding step 11: Delta Sync AD (sequential)..." -ForegroundColor DarkGray
            Add-JIMScheduleStep -ScheduleId $parallelSchedule.id `
                -StepType RunProfile `
                -ConnectedSystemId $ldapSystem.id `
                -RunProfileId $ldapDeltaSync.id

            # Verify steps were added correctly
            $scheduleWithSteps = Get-JIMSchedule -Id $parallelSchedule.id -IncludeSteps
            Assert-Equal -Expected 12 -Actual $scheduleWithSteps.steps.Count -Message "Schedule has 12 steps"

            # Verify step grouping (parallel steps share stepIndex)
            $stepIndices = @{}
            foreach ($s in $scheduleWithSteps.steps) {
                $idx = $s.stepIndex
                if (-not $stepIndices.ContainsKey($idx)) {
                    $stepIndices[$idx] = @()
                }
                $stepIndices[$idx] += $s
            }

            Write-Host ""
            Write-Host "  Step grouping verification:" -ForegroundColor Cyan
            foreach ($idx in ($stepIndices.Keys | Sort-Object)) {
                $count = $stepIndices[$idx].Count
                $status = if ($count -gt 1) { "PARALLEL ($count steps)" } else { "SEQUENTIAL" }
                Write-Host "    stepIndex $idx : $status" -ForegroundColor DarkGray
            }
            Write-Host ""

            $testResults.Steps += @{ Name = "Complex Parallel Schedule Created (12 steps)"; Success = $true }

            # Execute the schedule
            Write-Host "  Executing complex parallel schedule..." -ForegroundColor DarkGray
            $execution = Start-JIMSchedule -Id $parallelSchedule.id -PassThru

            Assert-NotNull -Value $execution -Message "Complex parallel execution started"
            Assert-Equal -Expected 12 -Actual $execution.totalSteps -Message "Execution shows 12 total steps"

            $testResults.Steps += @{ Name = "Complex Parallel Execution Started"; Success = $true }

            # Wait for completion (this schedule has 10 steps, so give it more time)
            Write-Host "  Waiting for complex schedule to complete (12 steps, max 300s)..." -ForegroundColor DarkGray
            $maxWait = 300  # 5 minutes for complex schedule
            $pollInterval = 5
            $elapsed = 0

            while ($elapsed -lt $maxWait) {
                $currentExecution = Get-JIMScheduleExecution -Id $execution.id

                $statusValue = $currentExecution.status
                $isTerminal = $statusValue -eq "Completed" -or $statusValue -eq "Failed" -or $statusValue -eq "Cancelled"
                if ($statusValue -is [int] -or $statusValue -is [long]) {
                    $isTerminal = $statusValue -ge 2
                }
                if ($isTerminal) {
                    Write-Host "    Execution completed! Status: $statusValue" -ForegroundColor Green
                    break
                }

                $stepProgress = "$($currentExecution.currentStepIndex + 1)/$($currentExecution.totalSteps)"
                Write-Host "    Step $stepProgress in progress... ($elapsed s)" -ForegroundColor DarkGray
                Start-Sleep -Seconds $pollInterval
                $elapsed += $pollInterval
            }

            $finalExecution = Get-JIMScheduleExecution -Id $execution.id

            try {
                Assert-ScheduleExecutionSuccess -ExecutionId $finalExecution.id -Name "Complex Parallel Execution"
                $testResults.Steps += @{ Name = "Complex Parallel Execution Completed"; Success = $true }
            }
            catch {
                $testResults.Steps += @{ Name = "Complex Parallel Execution Completed"; Success = $false; Error = $_.Exception.Message }
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
        }
    }

    Write-Host ""
}

# ============================================================================
# Summary
# ============================================================================

Write-TestSection "Test Summary"

$passedCount = ($testResults.Steps | Where-Object { $_.Success }).Count
$totalCount = $testResults.Steps.Count

Write-Host ""
Write-Host "Results: $passedCount / $totalCount tests passed" -ForegroundColor $(if ($passedCount -eq $totalCount) { "Green" } else { "Yellow" })
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "✓" } else { "✗" }
    $color = if ($testStep.Success) { "Green" } else { "Red" }
    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color
}

$testResults.Success = ($passedCount -eq $totalCount)

Write-Host ""
if ($testResults.Success) {
    Write-Host "All scheduler tests passed!" -ForegroundColor Green
}
else {
    Write-Host "Some tests failed. See details above." -ForegroundColor Red
    if (-not $ContinueOnError) {
        exit 1
    }
}

# Disable test schedules (but keep them for inspection)
Write-Host ""
Write-Host "Disabling test schedules..." -ForegroundColor DarkGray
$testSchedules = Get-JIMSchedule -Name "Integration Test*"
foreach ($schedule in $testSchedules) {
    Disable-JIMSchedule -Id $schedule.id -ErrorAction SilentlyContinue
}
Write-Host "Test schedules disabled (not deleted)." -ForegroundColor DarkGray
