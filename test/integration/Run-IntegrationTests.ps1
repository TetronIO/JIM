<#
.SYNOPSIS
    Runs JIM integration tests with automatic environment setup.

.DESCRIPTION
    Single entry point for running integration tests. This script:
    1. Resets the JIM environment (stops containers, removes volumes)
    2. Rebuilds and starts the JIM stack and Samba AD
    3. Waits for all services to be ready
    4. Creates an infrastructure API key
    5. Runs the specified test scenario

.PARAMETER Scenario
    The test scenario to run. Default: "Scenario1-HRToDirectory"
    Available scenarios are in test/integration/scenarios/

.PARAMETER Template
    The test data template size. Default: "Nano"
    Options: Nano, Small, Medium, Large

.PARAMETER Step
    Specific test step to run. Default: "All"
    Options vary by scenario (e.g., Joiner, Mover, Leaver, Reconnection, All)

.PARAMETER SkipReset
    Skip the reset step (useful for re-running tests without full rebuild).

.PARAMETER SkipBuild
    Skip rebuilding Docker images (use existing images).

.PARAMETER TimeoutSeconds
    Maximum time to wait for services to be ready. Default: 180 seconds.

.PARAMETER RunProfileTimeout
    Maximum time to wait for individual run profile execution. Default: 300 seconds.

.EXAMPLE
    ./Run-IntegrationTests.ps1

    Runs the default scenario (Scenario1-HRToDirectory) with Nano template.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Template Small

    Runs with a larger test data set.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Step Joiner

    Runs only the Joiner test step.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -SkipReset

    Re-runs tests without resetting the environment.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario "Scenario1-HRToDirectory" -Template Nano -Step All

    Explicit full specification of all parameters.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Scenario = "Scenario1-HRToDirectory",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [switch]$SkipReset,

    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 180,

    [Parameter(Mandatory=$false)]
    [int]$RunProfileTimeout = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Colour codes
$ESC = [char]27
$BLUE = "$ESC[34m"
$GREEN = "$ESC[32m"
$YELLOW = "$ESC[33m"
$RED = "$ESC[31m"
$GRAY = "$ESC[90m"
$CYAN = "$ESC[36m"
$NC = "$ESC[0m"

# Script root
$scriptRoot = $PSScriptRoot
$repoRoot = (Get-Item $scriptRoot).Parent.Parent.FullName

function Write-Banner {
    param([string]$Title)
    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  $Title${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host ""
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "${BLUE}$("-" * 65)${NC}"
    Write-Host "${BLUE}  $Title${NC}"
    Write-Host "${BLUE}$("-" * 65)${NC}"
    Write-Host ""
}

function Write-Step {
    param([string]$Message)
    Write-Host "${GRAY}$Message${NC}"
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ${GREEN}$Message${NC}"
}

function Write-Failure {
    param([string]$Message)
    Write-Host "  ${RED}$Message${NC}"
}

function Write-Warning {
    param([string]$Message)
    Write-Host "  ${YELLOW}$Message${NC}"
}

# Record start time
$startTime = Get-Date
$timings = @{}

Write-Banner "JIM Integration Test Runner"

Write-Host "${GRAY}Configuration:${NC}"
Write-Host "  Scenario:           ${CYAN}$Scenario${NC}"
Write-Host "  Template:           ${CYAN}$Template${NC}"
Write-Host "  Step:               ${CYAN}$Step${NC}"
Write-Host "  Skip Reset:         ${CYAN}$SkipReset${NC}"
Write-Host "  Skip Build:         ${CYAN}$SkipBuild${NC}"
Write-Host "  Service Timeout:    ${CYAN}${TimeoutSeconds}s${NC}"
Write-Host "  Run Profile Timeout:${CYAN}${RunProfileTimeout}s${NC}"
Write-Host ""

# Change to repository root
Set-Location $repoRoot

# Step 1: Reset (unless skipped)
$step1Start = Get-Date
if (-not $SkipReset) {
    Write-Section "Step 1: Resetting JIM Environment"

    Write-Step "Stopping all containers and removing volumes..."
    docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down -v 2>&1 | Out-Null
    docker compose -f docker-compose.integration-tests.yml down -v 2>&1 | Out-Null
    Write-Success "Containers stopped and volumes removed"
}
else {
    Write-Section "Step 1: Reset Skipped"
    Write-Warning "Using existing environment (SkipReset specified)"
}
$timings["1. Reset"] = (Get-Date) - $step1Start

# Step 2: Build (unless skipped)
$step2Start = Get-Date
if (-not $SkipBuild -and -not $SkipReset) {
    Write-Section "Step 2: Building Docker Images"

    Write-Step "Building JIM stack..."
    $buildOutput = docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml build 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to build JIM stack"
        Write-Host "${GRAY}$buildOutput${NC}"
        exit 1
    }
    Write-Success "JIM stack built successfully"
}
elseif ($SkipBuild) {
    Write-Section "Step 2: Build Skipped"
    Write-Warning "Using existing images (SkipBuild specified)"
}
else {
    Write-Section "Step 2: Build Skipped"
    Write-Warning "Using existing images (SkipReset implies existing environment)"
}
$timings["2. Build"] = (Get-Date) - $step2Start

# Step 3: Start services
$step3Start = Get-Date
Write-Section "Step 3: Starting Services"

Write-Step "Starting JIM stack..."
$jimResult = docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Failed to start JIM stack"
    Write-Host "${GRAY}$jimResult${NC}"
    exit 1
}
Write-Success "JIM stack started"

Start-Sleep -Seconds 2

Write-Step "Starting Samba AD..."
$sambaResult = docker compose -f docker-compose.integration-tests.yml up -d 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Failed to start Samba AD"
    Write-Host "${GRAY}$sambaResult${NC}"
    exit 1
}
Write-Success "Samba AD started"
$timings["3. Start Services"] = (Get-Date) - $step3Start

# Step 4: Wait for services
$step4Start = Get-Date
Write-Section "Step 4: Waiting for Services"

# Wait for Samba AD
Write-Step "Waiting for Samba AD to be ready..."
$waitScript = Join-Path $scriptRoot "Wait-SambaReady.ps1"
if (Test-Path $waitScript) {
    & $waitScript -TimeoutSeconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Samba AD did not become ready in time"
        Write-Host "${YELLOW}  Check logs: docker logs samba-ad-primary${NC}"
        exit 1
    }
}
else {
    Write-Warning "Wait-SambaReady.ps1 not found, waiting 60 seconds..."
    Start-Sleep -Seconds 60
}
$timings["4. Wait for Services"] = (Get-Date) - $step4Start

# Step 5: Setup API Key
$step5Start = Get-Date
Write-Section "Step 5: Setting Up API Key"

# Generate API key
Write-Step "Generating infrastructure API key..."
$randomBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($randomBytes)
$randomString = [Convert]::ToBase64String($randomBytes).Replace("+", "").Replace("/", "").Replace("=", "")
$apiKey = "jim_ak_$randomString"
Write-Success "Generated key prefix: $($apiKey.Substring(0, 12))"

# Update .env file
Write-Step "Updating .env file..."
$envFilePath = Join-Path $repoRoot ".env"
$envContent = Get-Content $envFilePath -Raw
if ($null -eq $envContent) { $envContent = "" }

if ($envContent -match "JIM_INFRASTRUCTURE_API_KEY=") {
    $envContent = $envContent -replace "JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$apiKey"
}
else {
    $newLine = if ($envContent.EndsWith("`n")) { "" } else { "`n" }
    $envContent = $envContent + $newLine + "JIM_INFRASTRUCTURE_API_KEY=$apiKey`n"
}
$envContent | Set-Content $envFilePath -NoNewline
Write-Success "Updated .env file"

# Save API key to file for scenario scripts
$keyFilePath = Join-Path $scriptRoot ".api-key"
$apiKey | Out-File -FilePath $keyFilePath -NoNewline -Encoding UTF8
Write-Success "Saved API key to .api-key"

# Recreate jim.web to pick up new API key
Write-Step "Recreating JIM.Web with new API key..."
docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d --force-recreate jim.web 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Failed to recreate JIM.Web"
    exit 1
}
Write-Success "JIM.Web recreated"

# Wait for JIM.Web to be ready
Write-Step "Waiting for JIM.Web to be ready..."
$maxAttempts = 30
$attempt = 0
$jimReady = $false

while ($attempt -lt $maxAttempts -and -not $jimReady) {
    $attempt++
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5200" -Method GET -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($response.StatusCode -in @(200, 302)) {
            $jimReady = $true
        }
    }
    catch {
        # Ignore errors, keep trying
    }

    if (-not $jimReady) {
        Start-Sleep -Seconds 2
    }
}

if (-not $jimReady) {
    Write-Failure "JIM.Web did not become ready"
    Write-Host "${YELLOW}  Check logs: docker logs jim.web${NC}"
    exit 1
}
Write-Success "JIM.Web is ready"
$timings["5. Setup API Key"] = (Get-Date) - $step5Start

# Step 6: Run test scenario
$step6Start = Get-Date
Write-Section "Step 6: Running Test Scenario"

$scenarioScript = Join-Path $scriptRoot "scenarios" "Invoke-$Scenario.ps1"
if (-not (Test-Path $scenarioScript)) {
    Write-Failure "Scenario script not found: $scenarioScript"
    Write-Host "${GRAY}Available scenarios:${NC}"
    Get-ChildItem (Join-Path $scriptRoot "scenarios") -Filter "Invoke-*.ps1" | ForEach-Object {
        Write-Host "  - $($_.BaseName -replace 'Invoke-', '')" -ForegroundColor Gray
    }
    exit 1
}

Write-Step "Running: Invoke-$Scenario.ps1 -Template $Template -Step $Step"
Write-Host ""

& $scenarioScript -Template $Template -Step $Step -ApiKey $apiKey -RunProfileTimeout $RunProfileTimeout
$scenarioExitCode = $LASTEXITCODE
$timings["6. Run Tests"] = (Get-Date) - $step6Start

# Step 7: Capture Performance Metrics
$step7Start = Get-Date
Write-Section "Step 7: Capturing Performance Metrics"

Write-Step "Extracting diagnostic timing from worker logs..."

# Capture worker logs with diagnostic output
$workerLogs = docker logs jim.worker 2>&1 | Where-Object { $_ -match "DiagnosticListener:" }

# Parse metrics into structured data
$metrics = @{
    Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Scenario = $Scenario
    Template = $Template
    Step = $Step
    Operations = @()
}

foreach ($logLine in $workerLogs) {
    # Example: DiagnosticListener: [SLOW] Parent > Child completed in 1234.56ms [connectedSystemId=1, objectCount=100]
    # Or: DiagnosticListener: OperationName completed in 1234.56ms [tags]
    if ($logLine -match 'DiagnosticListener:\s+(?:\[SLOW\]\s+)?(?:(.+?)\s+>\s+)?(.+?)\s+completed in\s+([\d.]+)ms(?:\s+\[(.*)\])?') {
        $parentName = $Matches[1]  # May be empty for root operations
        $operationName = $Matches[2]
        $durationMs = [double]$Matches[3]
        $tags = $Matches[4]

        $operation = @{
            Parent = if ($parentName) { $parentName } else { $null }
            Name = $operationName
            DurationMs = $durationMs
            Tags = @{}
        }

        # Parse tags if present (e.g., "connectedSystemId=1, objectCount=100")
        if ($tags) {
            $tagPairs = $tags -split ',\s*'
            foreach ($tagPair in $tagPairs) {
                if ($tagPair -match '(.+?)=(.+)') {
                    $operation.Tags[$Matches[1]] = $Matches[2]
                }
            }
        }

        $metrics.Operations += $operation
    }
}

if ($metrics.Operations.Count -eq 0) {
    Write-Warning "No performance metrics found in worker logs"
}
else {
    Write-Success "Captured $($metrics.Operations.Count) operation timings"

    # Display hierarchical tree view of operations
    Write-Host ""
    Write-Host "${CYAN}Performance Breakdown (Hierarchical):${NC}"
    Write-Host ""

    # Build parent-child relationships and calculate totals
    $operationsByName = @{}
    foreach ($op in $metrics.Operations) {
        $key = $op.Name
        # Skip operations with empty or null names
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        if (-not $operationsByName.ContainsKey($key)) {
            $operationsByName[$key] = @{
                Name = $key
                Parent = $op.Parent
                TotalMs = 0
                Count = 0
                Children = @()
            }
        }
        $operationsByName[$key].TotalMs += $op.DurationMs
        $operationsByName[$key].Count += 1
    }

    # Link children to parents
    foreach ($opName in $operationsByName.Keys) {
        $op = $operationsByName[$opName]
        if ($op.Parent -and $operationsByName.ContainsKey($op.Parent)) {
            $operationsByName[$op.Parent].Children += $op
        }
    }

    # Helper function to format milliseconds into friendly time values
    function Format-FriendlyTime {
        param([double]$Ms)

        if ($Ms -lt 1000) {
            # Less than 1 second - show milliseconds
            return "$($Ms.ToString('F1'))ms"
        }
        elseif ($Ms -lt 60000) {
            # Less than 1 minute - show seconds with 1 decimal place
            $secs = $Ms / 1000
            return "$($secs.ToString('F1'))s"
        }
        elseif ($Ms -lt 3600000) {
            # Less than 1 hour - show minutes and seconds
            $totalSecs = [int]($Ms / 1000)
            $mins = [Math]::Floor($totalSecs / 60)
            $secs = $totalSecs % 60
            if ($secs -eq 0) {
                return "${mins}m"
            }
            return "${mins}m ${secs}s"
        }
        else {
            # 1 hour or more - show hours, minutes, seconds
            $totalSecs = [int]($Ms / 1000)
            $hours = [Math]::Floor($totalSecs / 3600)
            $mins = [Math]::Floor(($totalSecs % 3600) / 60)
            $secs = $totalSecs % 60
            if ($mins -eq 0 -and $secs -eq 0) {
                return "${hours}h"
            }
            elseif ($secs -eq 0) {
                return "${hours}h ${mins}m"
            }
            return "${hours}h ${mins}m ${secs}s"
        }
    }

    # Recursive function to display tree with ASCII art
    function Show-OperationTree {
        param(
            [hashtable]$Operation,
            [string]$Prefix = "",
            [bool]$IsLast = $true,
            [bool]$IsRoot = $false
        )

        # Guard against null operation or divide by zero
        if ($null -eq $Operation -or $Operation["Count"] -eq 0) {
            return
        }

        $avgMs = $Operation["TotalMs"] / $Operation["Count"]
        $totalTime = Format-FriendlyTime -Ms $Operation["TotalMs"]
        $avgTime = Format-FriendlyTime -Ms $avgMs
        $countSuffix = if ($Operation["Count"] -gt 1) { " (${GRAY}$($Operation["Count"])x, avg $avgTime${NC})" } else { "" }

        # Tree characters for display
        $connector = if ($IsLast) { "└─ " } else { "├─ " }
        $displayPrefix = if ($IsRoot) { "" } else { $Prefix + $connector }

        Write-Host ("$displayPrefix{0,-50} {1,12}$countSuffix" -f $Operation["Name"], $totalTime)

        # Sort children by total time descending (handle null/empty)
        $children = $Operation["Children"]
        if ($null -ne $children -and $children.Count -gt 0) {
            $sortedChildren = @($children | Sort-Object -Property TotalMs -Descending)

            # Calculate prefix for children
            if ($IsRoot) {
                # Root's children get no inherited prefix (they start the tree branches)
                $childPrefix = ""
            }
            else {
                # Non-root: extend prefix with continuation line or spaces
                $extension = if ($IsLast) { "   " } else { "│  " }
                $childPrefix = $Prefix + $extension
            }

            for ($i = 0; $i -lt $sortedChildren.Count; $i++) {
                $isLastChild = ($i -eq ($sortedChildren.Count - 1))
                Show-OperationTree -Operation $sortedChildren[$i] -Prefix $childPrefix -IsLast $isLastChild -IsRoot $false
            }
        }
    }

    # Find and display root operations (those without parents or whose parents aren't in the data)
    $roots = $operationsByName.Values | Where-Object {
        -not $_.Parent -or -not $operationsByName.ContainsKey($_.Parent)
    } | Sort-Object -Property TotalMs -Descending

    for ($i = 0; $i -lt $roots.Count; $i++) {
        $isLastRoot = ($i -eq ($roots.Count - 1))
        Show-OperationTree -Operation $roots[$i] -Prefix "" -IsLast $isLastRoot -IsRoot $true
    }

    Write-Host ""

    # Create performance results directory (per hostname)
    $hostname = [System.Net.Dns]::GetHostName()
    $perfDir = Join-Path $scriptRoot "results" "performance" $hostname
    if (-not (Test-Path $perfDir)) {
        New-Item -ItemType Directory -Path $perfDir -Force | Out-Null
    }

    # Save current metrics
    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $currentFile = Join-Path $perfDir "$Scenario-$Template-$timestamp.json"
    $metrics | ConvertTo-Json -Depth 10 | Set-Content $currentFile
    Write-Success "Saved metrics to: results/performance/$hostname/$Scenario-$Template-$timestamp.json"

    # Find most recent previous baseline (excluding current run)
    $previousFiles = Get-ChildItem $perfDir -Filter "$Scenario-$Template-*.json" |
        Where-Object { $_.Name -ne "$Scenario-$Template-$timestamp.json" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($previousFiles) {
        Write-Host ""
        Write-Host "${CYAN}Performance Comparison:${NC}"
        Write-Host ""

        $baseline = Get-Content $previousFiles.FullName | ConvertFrom-Json

        # Compare key operations
        $currentOps = @{}
        foreach ($op in $metrics.Operations) {
            if (-not $currentOps.ContainsKey($op.Name)) {
                $currentOps[$op.Name] = @()
            }
            $currentOps[$op.Name] += $op.DurationMs
        }

        $baselineOps = @{}
        foreach ($op in $baseline.Operations) {
            if (-not $baselineOps.ContainsKey($op.Name)) {
                $baselineOps[$op.Name] = @()
            }
            $baselineOps[$op.Name] += $op.DurationMs
        }

        # Display comparison for key operations
        $keyOperations = @("FullImport", "FullSync", "Export", "ProcessConnectedSystemObjects")

        foreach ($opName in $keyOperations) {
            if ($currentOps.ContainsKey($opName) -and $baselineOps.ContainsKey($opName)) {
                $currentAvg = ($currentOps[$opName] | Measure-Object -Average).Average
                $baselineAvg = ($baselineOps[$opName] | Measure-Object -Average).Average
                $delta = $currentAvg - $baselineAvg
                $percentChange = if ($baselineAvg -gt 0) { ($delta / $baselineAvg) * 100 } else { 0 }

                $symbol = if ($delta -lt 0) { "↓" } elseif ($delta -gt 0) { "↑" } else { "=" }
                $colour = if ($delta -lt 0) { $GREEN } elseif ($delta -gt ($baselineAvg * 0.1)) { $RED } else { $YELLOW }

                Write-Host ("  {0,-35} {1,8:F1}ms  {2}{3} {4,6:F1}ms ({5:+0.0;-0.0;0}%)${NC}" -f `
                    $opName, $currentAvg, $colour, $symbol, $delta, $percentChange)
            }
        }

        Write-Host ""
        Write-Host "${GRAY}Baseline: $($previousFiles.Name) ($($baseline.Timestamp))${NC}"
    }
    else {
        Write-Host ""
        Write-Host "${YELLOW}No previous baseline found for comparison.${NC}"
        Write-Host "${GRAY}This is the first performance capture for $Scenario-$Template on $hostname${NC}"
    }
}

$timings["7. Capture Metrics"] = (Get-Date) - $step7Start

# Summary
$endTime = Get-Date
$duration = $endTime - $startTime

Write-Banner "Test Run Complete"

# Performance Summary
Write-Section "Performance Summary"
Write-Host ""
Write-Host "${CYAN}Stage Timings:${NC}"

# Sort timings by key (which has stage number prefix)
$sortedTimings = $timings.GetEnumerator() | Sort-Object Name

$totalSeconds = 0
foreach ($timing in $sortedTimings) {
    $seconds = [math]::Round($timing.Value.TotalSeconds, 1)
    $totalSeconds += $seconds
    $bar = "█" * [math]::Min(50, [math]::Floor($seconds / 2))
    Write-Host ("  {0,-25} {1,6}s  {2}" -f $timing.Name, $seconds, $bar) -ForegroundColor $(if ($seconds -gt 60) { "Yellow" } elseif ($seconds -gt 30) { "Cyan" } else { "Green" })
}

Write-Host ""
Write-Host "${GRAY}Note: '6. Run Tests' breakdown shown in 'Performance Breakdown (Test Steps)' section above${NC}"
Write-Host ""
Write-Host "${CYAN}Total Duration: ${NC}$($duration.ToString('hh\:mm\:ss')) (${totalSeconds}s)"
Write-Host ""

if ($scenarioExitCode -eq 0) {
    Write-Host "${GREEN}✓ All tests passed!${NC}"
}
else {
    Write-Host "${RED}✗ Some tests failed. Exit code: $scenarioExitCode${NC}"
}

Write-Host ""
exit $scenarioExitCode
