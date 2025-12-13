<#
.SYNOPSIS
    Wait for integration test systems to be ready

.DESCRIPTION
    Polls container health checks and service endpoints to ensure all
    required systems are ready before running tests

.PARAMETER Phase
    Test phase (1 or 2)

.PARAMETER TimeoutSeconds
    Maximum time to wait for systems to be ready (default: 600)

.EXAMPLE
    ./Wait-SystemsReady.ps1 -Phase 1
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(1, 2)]
    [int]$Phase = 1,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/LDAP-Helpers.ps1"

Write-Host "Waiting for integration test systems (Phase $Phase) to be ready..." -ForegroundColor Cyan
Write-Host "Timeout: ${TimeoutSeconds}s" -ForegroundColor Gray

$startTime = Get-Date

function Test-ContainerHealthy {
    param([string]$ContainerName)

    try {
        $health = docker inspect --format='{{.State.Health.Status}}' $ContainerName 2>$null
        return $health -eq "healthy"
    }
    catch {
        return $false
    }
}

function Test-ContainerRunning {
    param([string]$ContainerName)

    try {
        $status = docker inspect --format='{{.State.Status}}' $ContainerName 2>$null
        return $status -eq "running"
    }
    catch {
        return $false
    }
}

# Phase 1 systems
$phase1Systems = @(
    @{
        Name = "samba-ad-primary"
        Description = "Samba AD Primary"
        HasHealthCheck = $true
        AdditionalCheck = {
            Test-LDAPConnection -Server "localhost" -Port 389
        }
    }
)

# Phase 2 adds these systems
$phase2Systems = @(
    @{
        Name = "sqlserver-hris-a"
        Description = "SQL Server HRIS A"
        HasHealthCheck = $true
        AdditionalCheck = $null
    },
    @{
        Name = "postgres-target"
        Description = "PostgreSQL Target"
        HasHealthCheck = $true
        AdditionalCheck = $null
    }
)

# Determine which systems to check
$systemsToCheck = $phase1Systems
if ($Phase -eq 2) {
    $systemsToCheck += $phase2Systems
}

Write-Host "`nChecking $($systemsToCheck.Count) systems..." -ForegroundColor Gray

foreach ($system in $systemsToCheck) {
    Write-Host "`nWaiting for $($system.Description)..." -ForegroundColor Yellow

    # Wait for container to be running
    $running = Wait-ForCondition `
        -Condition { Test-ContainerRunning -ContainerName $system.Name } `
        -TimeoutSeconds $TimeoutSeconds `
        -IntervalSeconds 5 `
        -Description "$($system.Name) container running"

    if (-not $running) {
        Write-Host "✗ Container $($system.Name) not running" -ForegroundColor Red
        exit 1
    }

    # Wait for health check if applicable
    if ($system.HasHealthCheck) {
        $healthy = Wait-ForCondition `
            -Condition { Test-ContainerHealthy -ContainerName $system.Name } `
            -TimeoutSeconds $TimeoutSeconds `
            -IntervalSeconds 10 `
            -Description "$($system.Name) health check"

        if (-not $healthy) {
            Write-Host "✗ Container $($system.Name) not healthy" -ForegroundColor Red

            # Show container logs for debugging
            Write-Host "`nContainer logs:" -ForegroundColor Yellow
            docker logs --tail 50 $system.Name
            exit 1
        }
    }

    # Run additional checks if defined
    if ($null -ne $system.AdditionalCheck) {
        $additionalOk = Wait-ForCondition `
            -Condition $system.AdditionalCheck `
            -TimeoutSeconds 60 `
            -IntervalSeconds 5 `
            -Description "$($system.Name) additional check"

        if (-not $additionalOk) {
            Write-Host "✗ Additional check failed for $($system.Name)" -ForegroundColor Red
            exit 1
        }
    }

    Write-Host "✓ $($system.Description) is ready" -ForegroundColor Green
}

$elapsed = (Get-Date) - $startTime
Write-Host "`n✓ All systems ready after $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
