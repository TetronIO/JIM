<#
.SYNOPSIS
    Master integration test runner

.DESCRIPTION
    Orchestrates the complete integration test lifecycle:
    1. Stand up external systems
    2. Wait for systems to be ready
    3. Populate test data
    4. Run test scenarios
    5. Collect results
    6. Tear down systems

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER Phase
    Test phase (1 = MVP with LDAP/CSV, 2 = Post-MVP with databases)

.PARAMETER SkipTearDown
    Skip tearing down systems after tests (useful for debugging)

.PARAMETER ScenariosOnly
    Skip stand-up/populate and only run scenarios (assumes systems already configured)

.EXAMPLE
    ./Invoke-IntegrationTests.ps1 -Template Small -Phase 1

.EXAMPLE
    ./Invoke-IntegrationTests.ps1 -Template Medium -Phase 1 -SkipTearDown
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [ValidateSet(1, 2)]
    [int]$Phase = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipTearDown,

    [Parameter(Mandatory=$false)]
    [switch]$ScenariosOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

$startTime = Get-Date

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " JIM Integration Test Suite" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Template:       $Template" -ForegroundColor White
Write-Host "  Phase:          $Phase" -ForegroundColor White
Write-Host "  Skip tear-down: $SkipTearDown" -ForegroundColor White
Write-Host "  Scenarios only: $ScenariosOnly" -ForegroundColor White
Write-Host ""

$results = @{
    Template = $Template
    Phase = $Phase
    StartTime = $startTime
    Scenarios = @()
    Success = $false
}

try {
    if (-not $ScenariosOnly) {
        # Step 1: Stand up systems
        Write-TestSection "Step 1: Stand Up External Systems"

        if ($Phase -eq 1) {
            Write-Host "Starting Phase 1 systems (Samba AD)..." -ForegroundColor Gray
            docker compose -f ../../docker-compose.integration-tests.yml up -d
        }
        elseif ($Phase -eq 2) {
            Write-Host "Starting Phase 2 systems (all)..." -ForegroundColor Gray
            docker compose -f ../../docker-compose.integration-tests.yml --profile phase2 up -d
        }

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start containers"
        }

        Write-Host "✓ Containers started" -ForegroundColor Green

        # Step 2: Wait for systems to be ready
        Write-TestSection "Step 2: Wait for Systems Ready"

        & "$PSScriptRoot/Wait-SystemsReady.ps1" -Phase $Phase

        if ($LASTEXITCODE -ne 0) {
            throw "Systems not ready"
        }

        # Step 3: Populate test data
        Write-TestSection "Step 3: Populate Test Data"

        Write-Host "Populating Samba AD Primary..." -ForegroundColor Gray
        & "$PSScriptRoot/Populate-SambaAD.ps1" -Template $Template -Instance Primary

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to populate Samba AD"
        }

        Write-Host "`nGenerating test CSV files..." -ForegroundColor Gray
        & "$PSScriptRoot/Generate-TestCSV.ps1" -Template $Template

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to generate CSV files"
        }

        Write-Host "`n✓ Test data populated" -ForegroundColor Green
    }

    # Step 4: Run scenarios
    Write-TestSection "Step 4: Run Test Scenarios"

    Write-Host ""
    Write-Host "NOTE: Scenarios require an API key from JIM" -ForegroundColor Yellow
    Write-Host "  Create one via: Admin > API Keys in the JIM web UI" -ForegroundColor Gray
    Write-Host "  Then set environment variable: JIM_API_KEY" -ForegroundColor Gray
    Write-Host ""

    $apiKey = $env:JIM_API_KEY
    if (-not $apiKey) {
        Write-Host "⚠ No API key provided (JIM_API_KEY not set)" -ForegroundColor Yellow
        Write-Host "  Skipping scenario tests" -ForegroundColor Yellow
        Write-Host ""
        $results.Success = $true
        return
    }

    # Determine JIM URL (internal Docker network for containers)
    $jimUrl = "http://jim.web:80"

    Write-Host "Running Scenario 1: HR to Enterprise Directory" -ForegroundColor Cyan
    Write-Host ""

    try {
        & "$PSScriptRoot/scenarios/Invoke-Scenario1-HRToDirectory.ps1" `
            -Template $Template `
            -Step All `
            -JIMUrl $jimUrl `
            -ApiKey $apiKey

        if ($LASTEXITCODE -eq 0) {
            $results.Scenarios += @{
                Name = "Scenario 1: HR to Enterprise Directory"
                Success = $true
            }
            Write-Host ""
            Write-Host "✓ Scenario 1 passed" -ForegroundColor Green
        }
        else {
            $results.Scenarios += @{
                Name = "Scenario 1: HR to Enterprise Directory"
                Success = $false
                Error = "Test failed with exit code $LASTEXITCODE"
            }
            Write-Host ""
            Write-Host "✗ Scenario 1 failed" -ForegroundColor Red
        }
    }
    catch {
        $results.Scenarios += @{
            Name = "Scenario 1: HR to Enterprise Directory"
            Success = $false
            Error = $_.Exception.Message
        }
        Write-Host ""
        Write-Host "✗ Scenario 1 failed: $_" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "Scenarios 2 and 3 not yet implemented:" -ForegroundColor Gray
    Write-Host "  - Scenario 2: Directory to Directory Sync (placeholder)" -ForegroundColor Gray
    Write-Host "  - Scenario 3: GALSYNC (placeholder)" -ForegroundColor Gray
    Write-Host ""

    # Determine overall success
    $scenariosPassed = ($results.Scenarios | Where-Object { $_.Success }).Count
    $scenariosTotal = $results.Scenarios.Count
    $results.Success = ($scenariosPassed -eq $scenariosTotal)
}
catch {
    Write-Host ""
    Write-Host "✗ Integration tests failed: $_" -ForegroundColor Red
    $results.Success = $false
    $results.Error = $_.Exception.Message
}
finally {
    # Step 5: Collect results
    Write-TestSection "Step 5: Collect Results"

    $results.EndTime = Get-Date
    $results.Duration = $results.EndTime - $results.StartTime

    $resultsPath = "$PSScriptRoot/results"
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

        if ($Phase -eq 1) {
            docker compose -f ../../docker-compose.integration-tests.yml down -v
        }
        elseif ($Phase -eq 2) {
            docker compose -f ../../docker-compose.integration-tests.yml --profile phase2 down -v
        }

        Write-Host "✓ Systems torn down" -ForegroundColor Green
    }
    elseif ($SkipTearDown) {
        Write-Host ""
        Write-Host "⚠ Skipping tear-down (containers still running)" -ForegroundColor Yellow
        Write-Host "  Run this to tear down manually:" -ForegroundColor Gray
        Write-Host "  docker compose -f ../../docker-compose.integration-tests.yml down -v" -ForegroundColor Gray
    }

    # Summary
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host " Test Summary" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Template:     $Template" -ForegroundColor White
    Write-Host "  Phase:        $Phase" -ForegroundColor White
    Write-Host "  Duration:     $($results.Duration.ToString('hh\:mm\:ss'))" -ForegroundColor White
    Write-Host "  Success:      $($results.Success)" -ForegroundColor $(if ($results.Success) { "Green" } else { "Red" })
    Write-Host ""

    if (-not $results.Success) {
        exit 1
    }
}
