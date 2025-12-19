<#
.SYNOPSIS
    Waits for Samba AD to be ready for integration testing.

.DESCRIPTION
    Checks if the Samba AD container is running and healthy by attempting to query
    the domain controller. Provides clear status updates and a progress indicator.

.PARAMETER TimeoutSeconds
    Maximum time to wait for Samba AD to become ready (default: 180 seconds / 3 minutes)

.PARAMETER CheckIntervalSeconds
    How often to check if Samba is ready (default: 5 seconds)

.EXAMPLE
    ./Wait-SambaReady.ps1

.EXAMPLE
    ./Wait-SambaReady.ps1 -TimeoutSeconds 120 -CheckIntervalSeconds 10
#>

param(
    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 180,

    [Parameter(Mandatory=$false)]
    [int]$CheckIntervalSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Color codes
$ESC = [char]27
$BLUE = "$ESC[34m"
$GREEN = "$ESC[32m"
$YELLOW = "$ESC[33m"
$RED = "$ESC[31m"
$GRAY = "$ESC[90m"
$NC = "$ESC[0m"  # No Color

Write-Host ""
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host "${BLUE}  Waiting for Samba AD to become ready...${NC}"
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""

# Check if container is running
Write-Host "${GRAY}Checking if Samba AD container is running...${NC}"
$containerStatus = docker ps --filter "name=samba-ad-primary" --format "{{.Status}}" 2>$null

if (-not $containerStatus) {
    Write-Host "${RED}✗ Samba AD container is not running${NC}"
    Write-Host "${YELLOW}  Start it with: docker compose -f docker-compose.integration-tests.yml up -d${NC}"
    exit 1
}

Write-Host "${GREEN}✓ Container is running${NC}"
Write-Host ""

# Wait for Samba to be ready
$startTime = Get-Date
$elapsed = 0
$ready = $false
$lastDot = 0

Write-Host "${GRAY}Waiting for Samba AD domain controller to initialize...${NC}"
Write-Host ""

while ($elapsed -lt $TimeoutSeconds) {
    # Check if Samba is responding to domain queries
    # Use samba-tool to check if the domain controller is functional
    $testResult = docker exec samba-ad-primary samba-tool domain level show 2>&1

    if ($LASTEXITCODE -eq 0 -and $testResult -match "Domain and forest function level") {
        $ready = $true
        break
    }

    # Show progress
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    $percentComplete = [math]::Min(100, ($elapsed / $TimeoutSeconds) * 100)

    # Update progress bar every second
    $currentSecond = [math]::Floor($elapsed)
    if ($currentSecond -ne $lastDot) {
        $lastDot = $currentSecond
        $dots = [math]::Floor($elapsed / $CheckIntervalSeconds)
        $bar = "=" * [math]::Min(50, $dots)
        $spaces = " " * (50 - $bar.Length)
        Write-Host -NoNewline "`r  [${bar}${spaces}] ${elapsed}s / ${TimeoutSeconds}s"
    }

    Start-Sleep -Seconds 1
}

Write-Host ""  # New line after progress bar
Write-Host ""

if ($ready) {
    Write-Host "${GREEN}✓ Samba AD is ready!${NC}"
    Write-Host ""
    Write-Host "${GRAY}Domain Information:${NC}"

    # Show domain info
    $domainInfo = docker exec samba-ad-primary samba-tool domain info 127.0.0.1 2>&1
    Write-Host "${GRAY}$domainInfo${NC}"

    Write-Host ""
    Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host "${GREEN}  Samba AD is ready for integration testing!${NC}"
    Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host ""

    exit 0
}
else {
    Write-Host "${RED}✗ Timed out waiting for Samba AD to become ready${NC}"
    Write-Host ""
    Write-Host "${YELLOW}Troubleshooting:${NC}"
    Write-Host "  1. Check container logs: docker logs samba-ad-primary"
    Write-Host "  2. Restart container: docker compose -f docker-compose.integration-tests.yml restart"
    Write-Host "  3. Check container is healthy: docker ps"
    Write-Host ""

    exit 1
}
