<#
.SYNOPSIS
    Master integration test runner

.DESCRIPTION
    Orchestrates the complete integration test lifecycle:
    1. Reset environment (optional)
    2. Stand up JIM and external systems
    3. Wait for systems to be ready
    4. Set up infrastructure API key
    5. Populate test data
    6. Run test scenarios
    7. Collect results
    8. Tear down systems (optional)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER Phase
    Test phase (1 = MVP with LDAP/CSV, 2 = Post-MVP with databases)

.PARAMETER SkipTearDown
    Skip tearing down systems after tests (useful for debugging)

.PARAMETER ScenariosOnly
    Skip stand-up/populate and only run scenarios (assumes systems already configured)

.PARAMETER SkipReset
    Skip initial reset (useful when you know the environment is clean)

.PARAMETER ApiKey
    Use a specific API key instead of auto-creating infrastructure key

.EXAMPLE
    ./Invoke-IntegrationTests.ps1 -Template Small -Phase 1

.EXAMPLE
    ./Invoke-IntegrationTests.ps1 -Template Medium -Phase 1 -SkipTearDown

.EXAMPLE
    ./Invoke-IntegrationTests.ps1 -ScenariosOnly -ApiKey "jim_ak_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [ValidateSet(1, 2)]
    [int]$Phase = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipTearDown,

    [Parameter(Mandatory=$false)]
    [switch]$ScenariosOnly,

    [Parameter(Mandatory=$false)]
    [switch]$SkipReset,

    [Parameter(Mandatory=$false)]
    [string]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve paths using $PSScriptRoot (this script is in test/integration)
$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path "$scriptRoot/../..").Path
$integrationCompose = Join-Path $repoRoot "docker-compose.integration-tests.yml"
$jimCompose = Join-Path $repoRoot "docker-compose.yml"
$jimComposeOverride = Join-Path $repoRoot "docker-compose.override.yml"

# Import helpers
. "$scriptRoot/utils/Test-Helpers.ps1"

$startTime = Get-Date


Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host " JIM Integration Test Suite" -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Template:       $Template" -ForegroundColor White
Write-Host "  Phase:          $Phase" -ForegroundColor White
Write-Host "  Skip tear-down: $SkipTearDown" -ForegroundColor White
Write-Host "  Scenarios only: $ScenariosOnly" -ForegroundColor White
Write-Host "  Skip reset:     $SkipReset" -ForegroundColor White
Write-Host ""

$results = @{
    Template = $Template
    Phase = $Phase
    StartTime = $startTime
    Scenarios = @()
    Success = $false
}

# Use provided API key or environment variable
$effectiveApiKey = $ApiKey
if (-not $effectiveApiKey) {
    $effectiveApiKey = $env:JIM_API_KEY
}

try {
    if (-not $ScenariosOnly) {
        # Step 0: Reset environment (optional)
        if (-not $SkipReset) {
            Write-TestSection "Step 0: Reset Environment"

            Write-Host "Tearing down any existing containers and volumes..." -ForegroundColor Gray

            # Tear down JIM
            $jimComposeArgs = @("-f", $jimCompose)
            if (Test-Path $jimComposeOverride) {
                $jimComposeArgs += @("-f", $jimComposeOverride)
            }
            $jimComposeArgs += @("--profile", "with-db", "down", "-v", "--remove-orphans")
            docker compose @jimComposeArgs 2>&1 | Out-Null

            # Tear down external systems
            docker compose -f $integrationCompose down -v --remove-orphans 2>&1 | Out-Null

            # Remove the shared network (will be recreated in Step 1)
            docker network rm jim-network 2>$null | Out-Null

            Write-Host "  Environment reset complete" -ForegroundColor Green
        }

        # Step 1: Stand up systems (JIM + external systems in parallel)
        Write-TestSection "Step 1: Stand Up Systems"

        # Create jim-network first so both compose files can use it
        Write-Host "Creating jim-network..." -ForegroundColor Gray
        docker network create jim-network 2>$null | Out-Null
        # Network may already exist, that's fine

        # Start JIM and external systems in parallel for faster startup
        Write-Host "Starting JIM and external systems in parallel..." -ForegroundColor Gray
        $parallelStartTime = Get-Date

        # Build JIM compose arguments
        $jimComposeArgs = @("-f", $jimCompose)
        if (Test-Path $jimComposeOverride) {
            $jimComposeArgs += @("-f", $jimComposeOverride)
        }
        # Use --build to ensure containers are rebuilt with latest code changes
        $jimComposeArgs += @("--profile", "with-db", "up", "-d", "--build")

        # Build external systems compose arguments
        $externalComposeArgs = if ($Phase -eq 2) {
            @("-f", $integrationCompose, "--profile", "phase2", "up", "-d")
        } else {
            @("-f", $integrationCompose, "up", "-d")
        }

        # Start both in parallel using background jobs
        $jimJob = Start-Job -ScriptBlock {
            param($args)
            docker compose @args 2>&1
        } -ArgumentList (,$jimComposeArgs)

        $externalJob = Start-Job -ScriptBlock {
            param($args)
            docker compose @args 2>&1
        } -ArgumentList (,$externalComposeArgs)

        # Wait for both jobs to complete
        $null = Wait-Job -Job $jimJob, $externalJob

        # Check results
        $jimResult = Receive-Job -Job $jimJob
        $jimExitCode = $jimJob.State -eq 'Completed'
        Remove-Job -Job $jimJob

        $externalResult = Receive-Job -Job $externalJob
        $externalExitCode = $externalJob.State -eq 'Completed'
        Remove-Job -Job $externalJob

        # Show output
        if ($jimResult) {
            $jimResult | ForEach-Object { Write-Host "    [JIM] $_" -ForegroundColor Gray }
        }
        if ($externalResult) {
            $externalResult | ForEach-Object { Write-Host "    [External] $_" -ForegroundColor Gray }
        }

        $parallelDuration = (Get-Date) - $parallelStartTime
        Write-Host "  Systems started in $($parallelDuration.TotalSeconds.ToString('F1'))s (parallel)" -ForegroundColor Green

        # Step 2: Wait for systems to be ready
        Write-TestSection "Step 2: Wait for Systems Ready"

        & "$scriptRoot/Wait-SystemsReady.ps1" -Phase $Phase

        if ($LASTEXITCODE -ne 0) {
            throw "External systems not ready"
        }

        # Wait for JIM with progress bar
        $jimOp = Start-TimedOperation -Name "Waiting for JIM" -TotalSteps 60
        $maxAttempts = 60
        $attempt = 0
        $jimReady = $false

        while ($attempt -lt $maxAttempts -and -not $jimReady) {
            $attempt++
            Update-OperationProgress -Operation $jimOp -CurrentStep $attempt -Status "Checking http://localhost:5200..."

            try {
                $response = Invoke-WebRequest -Uri "http://localhost:5200" -Method GET -TimeoutSec 2 -ErrorAction SilentlyContinue
                if ($response.StatusCode -in @(200, 302)) {
                    $jimReady = $true
                }
            } catch {
                # Ignore, keep trying
            }

            if (-not $jimReady) {
                Start-Sleep -Seconds 2
            }
        }

        if (-not $jimReady) {
            Complete-TimedOperation -Operation $jimOp -Success $false -Message "JIM did not become ready within timeout"
            throw "JIM did not become ready within timeout"
        }

        Complete-TimedOperation -Operation $jimOp -Success $true -Message "JIM is ready"

        # Step 2b: Set up infrastructure API key (if not provided)
        if (-not $effectiveApiKey) {
            Write-TestSection "Step 2b: Set Up Infrastructure API Key"

            Write-Host "Creating infrastructure API key..." -ForegroundColor Gray

            & "$scriptRoot/Setup-InfrastructureApiKey.ps1"

            if ($LASTEXITCODE -ne 0) {
                throw "Failed to set up infrastructure API key"
            }

            # Read the API key from the file (child process can't set parent env vars)
            $keyFilePath = "$scriptRoot/.api-key"
            if (Test-Path $keyFilePath) {
                $effectiveApiKey = Get-Content $keyFilePath -Raw
                $effectiveApiKey = $effectiveApiKey.Trim()
            } else {
                # Fallback to env var in case script was dot-sourced
                $effectiveApiKey = $env:JIM_API_KEY
            }

            if (-not $effectiveApiKey) {
                throw "Infrastructure API key was not set after running Setup-InfrastructureApiKey.ps1"
            }

            Write-Host "  Infrastructure API key configured" -ForegroundColor Green
        }

        # Step 3: Populate test data
        Write-TestSection "Step 3: Populate Test Data"

        Write-Host "Populating Subatomic AD..." -ForegroundColor Gray
        & "$scriptRoot/Populate-SambaAD.ps1" -Template $Template -Instance Primary

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to populate Samba AD"
        }

        Write-Host ""
        Write-Host "Generating test CSV files..." -ForegroundColor Gray
        & "$scriptRoot/Generate-TestCSV.ps1" -Template $Template

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to generate CSV files"
        }

        Write-Host ""
        Write-Host "  Test data populated" -ForegroundColor Green
    }

    # Step 4: Run scenarios
    Write-TestSection "Step 4: Run Test Scenarios"

    # Final API key check
    if (-not $effectiveApiKey) {
        Write-Host ""
        Write-Host "WARNING: No API key available" -ForegroundColor Yellow
        Write-Host "  Either provide -ApiKey parameter or set JIM_API_KEY environment variable" -ForegroundColor Yellow
        Write-Host "  Skipping scenario tests" -ForegroundColor Yellow
        Write-Host ""
        $results.Success = $true
        return
    }

    # Use localhost URL since PowerShell scripts run on the host, not in containers
    $jimUrl = "http://localhost:5200"

    Write-Host "Running Scenario 1: HR to Enterprise Directory" -ForegroundColor Cyan
    Write-Host ""

    try {
        & "$scriptRoot/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1" `
            -Template $Template `
            -Step All `
            -JIMUrl $jimUrl `
            -ApiKey $effectiveApiKey

        if ($LASTEXITCODE -eq 0) {
            $results.Scenarios += @{
                Name = "Scenario 1: HR to Enterprise Directory"
                Success = $true
            }
            Write-Host ""
            Write-Host "  Scenario 1 passed" -ForegroundColor Green
        }
        else {
            $results.Scenarios += @{
                Name = "Scenario 1: HR to Enterprise Directory"
                Success = $false
                Error = "Test failed with exit code $LASTEXITCODE"
            }
            Write-Host ""
            Write-Host "  Scenario 1 failed" -ForegroundColor Red
        }
    }
    catch {
        $results.Scenarios += @{
            Name = "Scenario 1: HR to Enterprise Directory"
            Success = $false
            Error = $_.Exception.Message
        }
        Write-Host ""
        Write-Host "  Scenario 1 failed: $_" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "Running Scenario 4: MVO Deletion Rules" -ForegroundColor Cyan
    Write-Host ""

    try {
        & "$scriptRoot/scenarios/Invoke-Scenario4-DeletionRules.ps1" `
            -Template $Template `
            -Step All `
            -JIMUrl $jimUrl `
            -ApiKey $effectiveApiKey

        if ($LASTEXITCODE -eq 0) {
            $results.Scenarios += @{
                Name = "Scenario 4: MVO Deletion Rules"
                Success = $true
            }
            Write-Host ""
            Write-Host "  Scenario 4 passed" -ForegroundColor Green
        }
        else {
            $results.Scenarios += @{
                Name = "Scenario 4: MVO Deletion Rules"
                Success = $false
                Error = "Test failed with exit code $LASTEXITCODE"
            }
            Write-Host ""
            Write-Host "  Scenario 4 failed" -ForegroundColor Red
        }
    }
    catch {
        $results.Scenarios += @{
            Name = "Scenario 4: MVO Deletion Rules"
            Success = $false
            Error = $_.Exception.Message
        }
        Write-Host ""
        Write-Host "  Scenario 4 failed: $_" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "Scenarios 2 and 3 not yet implemented:" -ForegroundColor Gray
    Write-Host "  - Scenario 2: Directory to Directory Sync (placeholder)" -ForegroundColor Gray
    Write-Host "  - Scenario 3: GALSYNC (placeholder)" -ForegroundColor Gray
    Write-Host ""

    # Determine overall success
    $scenariosPassed = ($results.Scenarios | Where-Object { $_.Success }).Count
    $scenariosTotal = $results.Scenarios.Count
    $results.Success = ($scenariosTotal -gt 0 -and $scenariosPassed -eq $scenariosTotal)
}
catch {
    Write-Host ""
    Write-Host "Integration tests failed: $_" -ForegroundColor Red
    $results.Success = $false
    $results.Error = $_.Exception.Message
}
finally {
    # Step 5: Collect results
    Write-TestSection "Step 5: Collect Results"

    $results.EndTime = Get-Date
    $results.Duration = $results.EndTime - $results.StartTime

    $resultsPath = "$scriptRoot/results"
    if (-not (Test-Path $resultsPath)) {
        New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    }

    $resultsFile = Join-Path $resultsPath "results-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
    $results | ConvertTo-Json -Depth 10 | Out-File -FilePath $resultsFile -Encoding UTF8

    Write-Host "Results saved to: $resultsFile" -ForegroundColor Gray

    # Step 6: Tear down
    if (-not $SkipTearDown -and -not $ScenariosOnly) {
        Write-TestSection "Step 6: Tear Down Systems"

        Write-Host "Stopping and removing containers and volumes..." -ForegroundColor Gray

        # Tear down external systems
        if ($Phase -eq 1) {
            docker compose -f $integrationCompose down -v 2>&1 | Out-Null
        }
        elseif ($Phase -eq 2) {
            docker compose -f $integrationCompose --profile phase2 down -v 2>&1 | Out-Null
        }

        # Tear down JIM
        $jimComposeArgs = @("-f", $jimCompose)
        if (Test-Path $jimComposeOverride) {
            $jimComposeArgs += @("-f", $jimComposeOverride)
        }
        $jimComposeArgs += @("--profile", "with-db", "down", "-v", "--remove-orphans")
        docker compose @jimComposeArgs 2>&1 | Out-Null

        Write-Host "  Systems torn down" -ForegroundColor Green
    }
    elseif ($SkipTearDown) {
        Write-Host ""
        Write-Host "Skipping tear-down (containers still running)" -ForegroundColor Yellow
        Write-Host "  To tear down manually, run:" -ForegroundColor Gray
        Write-Host "  ./Reset-JIM.ps1" -ForegroundColor Gray
    }

    # Summary
    Write-Host ""
    Write-Host "=======================================================" -ForegroundColor Cyan
    Write-Host " Test Summary" -ForegroundColor Cyan
    Write-Host "=======================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Template:     $Template" -ForegroundColor White
    Write-Host "  Phase:        $Phase" -ForegroundColor White
    Write-Host "  Duration:     $($results.Duration.ToString('hh\:mm\:ss'))" -ForegroundColor White
    Write-Host "  Success:      $($results.Success)" -ForegroundColor $(if ($results.Success) { "Green" } else { "Red" })
    Write-Host ""

    if ($results.Scenarios.Count -gt 0) {
        Write-Host "  Scenarios:" -ForegroundColor White
        foreach ($scenario in $results.Scenarios) {
            $status = if ($scenario.Success) { "[PASS]" } else { "[FAIL]" }
            $color = if ($scenario.Success) { "Green" } else { "Red" }
            Write-Host "    $status $($scenario.Name)" -ForegroundColor $color
        }
        Write-Host ""
    }

    if (-not $results.Success) {
        exit 1
    }
}
