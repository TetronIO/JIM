#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reset JIM to a clean state for integration testing

.DESCRIPTION
    Tears down all containers (JIM and external test systems), removes volumes,
    and optionally restarts the environment ready for testing.

    This script handles:
    - Stopping JIM containers (web, worker, scheduler, database)
    - Stopping external test system containers (Samba AD, etc.)
    - Removing all Docker volumes for a clean database state
    - Optionally restarting the environment

.PARAMETER Restart
    After teardown, restart JIM and external test systems

.PARAMETER ExternalOnly
    Only tear down external test systems (Samba AD), leave JIM running

.PARAMETER JIMOnly
    Only tear down JIM, leave external test systems running

.PARAMETER SkipConfirmation
    Skip the confirmation prompt (useful for CI/CD)

.EXAMPLE
    ./Reset-JIM.ps1
    # Tears down everything, prompts for confirmation

.EXAMPLE
    ./Reset-JIM.ps1 -Restart
    # Tears down and restarts everything

.EXAMPLE
    ./Reset-JIM.ps1 -SkipConfirmation
    # Tears down without prompting (for CI/CD)

.EXAMPLE
    ./Reset-JIM.ps1 -JIMOnly
    # Only resets JIM database, keeps Samba AD running
#>

param(
    [Parameter(Mandatory=$false)]
    [switch]$Restart,

    [Parameter(Mandatory=$false)]
    [switch]$ExternalOnly,

    [Parameter(Mandatory=$false)]
    [switch]$JIMOnly,

    [Parameter(Mandatory=$false)]
    [switch]$SkipConfirmation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve paths relative to this script's location (script is in scripts/ folder)
$repoRoot = Split-Path -Parent $PSScriptRoot
$jimCompose = Join-Path $repoRoot "docker-compose.yml"
$jimComposeOverride = Join-Path $repoRoot "docker-compose.override.codespaces.yml"
$integrationCompose = Join-Path $repoRoot "docker-compose.integration-tests.yml"

Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " JIM Environment Reset" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# Validate compose files exist
$missingFiles = @()
if (-not $ExternalOnly -and -not (Test-Path $jimCompose)) {
    $missingFiles += $jimCompose
}
if (-not $JIMOnly -and -not (Test-Path $integrationCompose)) {
    $missingFiles += $integrationCompose
}

if ($missingFiles.Count -gt 0) {
    Write-Host "Missing Docker Compose files:" -ForegroundColor Red
    foreach ($file in $missingFiles) {
        Write-Host "  - $file" -ForegroundColor Red
    }
    exit 1
}

# Determine what will be reset
$resetTargets = @()
if (-not $ExternalOnly) {
    $resetTargets += "JIM (web, worker, scheduler, database)"
}
if (-not $JIMOnly) {
    $resetTargets += "External test systems (Samba AD)"
}

Write-Host "This will reset:" -ForegroundColor Yellow
foreach ($target in $resetTargets) {
    Write-Host "  - $target" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "All data will be PERMANENTLY DELETED:" -ForegroundColor Red
Write-Host "  - Database (Metaverse, Connected Systems, Activities)" -ForegroundColor Red
Write-Host "  - Docker volumes" -ForegroundColor Red
Write-Host "  - Test data in external systems" -ForegroundColor Red
Write-Host ""

# Confirm unless skipped
if (-not $SkipConfirmation) {
    $confirmation = Read-Host "Are you sure you want to continue? (yes/no)"
    if ($confirmation -ne "yes") {
        Write-Host "Cancelled." -ForegroundColor Yellow
        exit 0
    }
    Write-Host ""
}

# Track success
$success = $true

# Step 1: Tear down JIM
if (-not $ExternalOnly) {
    Write-Host "[Step 1] Stopping JIM containers..." -ForegroundColor Cyan

    $jimComposeArgs = @("-f", $jimCompose)
    if (Test-Path $jimComposeOverride) {
        $jimComposeArgs += @("-f", $jimComposeOverride)
    }
    $jimComposeArgs += @("--profile", "with-db", "down", "-v", "--remove-orphans")

    docker compose @jimComposeArgs 2>&1 | ForEach-Object {
        if ($_ -match "error|Error|ERROR") {
            Write-Host "  $_" -ForegroundColor Red
        } else {
            Write-Host "  $_" -ForegroundColor Gray
        }
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  JIM containers stopped and volumes removed" -ForegroundColor Green
    } else {
        Write-Host "  Warning: JIM teardown had issues (may already be stopped)" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Step 2: Tear down external test systems
if (-not $JIMOnly) {
    Write-Host "[Step 2] Stopping external test system containers..." -ForegroundColor Cyan

    docker compose -f $integrationCompose down -v --remove-orphans 2>&1 | ForEach-Object {
        if ($_ -match "error|Error|ERROR") {
            Write-Host "  $_" -ForegroundColor Red
        } else {
            Write-Host "  $_" -ForegroundColor Gray
        }
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  External test systems stopped and volumes removed" -ForegroundColor Green
    } else {
        Write-Host "  Warning: External systems teardown had issues (may already be stopped)" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Step 3: Clean up any orphan networks
Write-Host "[Step 3] Cleaning up Docker networks..." -ForegroundColor Cyan
docker network prune -f 2>&1 | Out-Null
Write-Host "  Docker networks cleaned" -ForegroundColor Green
Write-Host ""

# Step 4: Optionally restart
if ($Restart) {
    Write-Host "[Step 4] Restarting environment..." -ForegroundColor Cyan
    Write-Host ""

    # IMPORTANT: JIM must start first because it creates the jim-network.
    # External test systems (Samba AD) declare this network as external and depend on it.
    # Order: JIM -> JIM ready -> External systems -> External systems ready

    # Start JIM first (creates the jim-network that external systems depend on)
    if (-not $ExternalOnly) {
        Write-Host "  Starting JIM..." -ForegroundColor Gray

        $jimComposeArgs = @("-f", $jimCompose)
        if (Test-Path $jimComposeOverride) {
            $jimComposeArgs += @("-f", $jimComposeOverride)
        }
        $jimComposeArgs += @("--profile", "with-db", "up", "-d")

        docker compose @jimComposeArgs 2>&1 | ForEach-Object {
            Write-Host "    $_" -ForegroundColor Gray
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Failed to start JIM" -ForegroundColor Red
            $success = $false
        } else {
            Write-Host "  JIM started" -ForegroundColor Green
        }
        Write-Host ""

        # Wait for JIM to be healthy
        Write-Host "  Waiting for JIM to be ready..." -ForegroundColor Gray
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
            } catch {
                # Ignore, keep trying
            }

            if (-not $jimReady) {
                Start-Sleep -Seconds 2
            }
        }

        if ($jimReady) {
            Write-Host "  JIM is ready at http://localhost:5200" -ForegroundColor Green
        } else {
            Write-Host "  Warning: JIM did not become ready within timeout" -ForegroundColor Yellow
            Write-Host "  Check logs: docker logs jim.web" -ForegroundColor Yellow
        }
        Write-Host ""
    }

    # Start external test systems AFTER JIM (they depend on jim-network created by JIM)
    if (-not $JIMOnly) {
        Write-Host "  Starting external test systems (Samba AD)..." -ForegroundColor Gray
        docker compose -f $integrationCompose up -d 2>&1 | ForEach-Object {
            Write-Host "    $_" -ForegroundColor Gray
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Failed to start external test systems" -ForegroundColor Red
            $success = $false
        } else {
            Write-Host "  External test systems started" -ForegroundColor Green
        }
        Write-Host ""
    }

    # Wait for Samba AD if restarted
    if (-not $JIMOnly) {
        Write-Host "  Waiting for Samba AD to be ready..." -ForegroundColor Gray
        $maxAttempts = 30
        $attempt = 0
        $sambaReady = $false

        while ($attempt -lt $maxAttempts -and -not $sambaReady) {
            $attempt++
            $health = docker inspect --format='{{.State.Health.Status}}' samba-ad-primary 2>$null
            if ($health -eq "healthy") {
                $sambaReady = $true
            } else {
                Start-Sleep -Seconds 2
            }
        }

        if ($sambaReady) {
            Write-Host "  Samba AD is ready" -ForegroundColor Green
        } else {
            Write-Host "  Warning: Samba AD did not become healthy within timeout" -ForegroundColor Yellow
            Write-Host "  Check logs: docker logs samba-ad-primary" -ForegroundColor Yellow
        }
        Write-Host ""
    }
}

# Summary
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Reset Complete" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

if ($Restart) {
    Write-Host "Environment has been reset and restarted." -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Set up infrastructure API key:" -ForegroundColor White
    Write-Host "     pwsh test/integration/Setup-InfrastructureApiKey.ps1" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. Run integration tests:" -ForegroundColor White
    Write-Host "     pwsh test/integration/Invoke-IntegrationTests.ps1 -Template Small -ScenariosOnly" -ForegroundColor Gray
} else {
    Write-Host "Environment has been torn down." -ForegroundColor Green
    Write-Host ""
    Write-Host "To restart:" -ForegroundColor Cyan
    Write-Host "  ./Reset-JIM.ps1 -Restart" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Or run full integration tests (will start everything):" -ForegroundColor Cyan
    Write-Host "  pwsh test/integration/Invoke-IntegrationTests.ps1 -Template Small" -ForegroundColor Gray
}

Write-Host ""

if ($success) {
    exit 0
} else {
    exit 1
}
