<#
.SYNOPSIS
    Starts the complete integration testing environment (JIM stack + Samba AD).

.DESCRIPTION
    Convenience script that starts both the JIM stack and Samba AD test infrastructure,
    then automatically waits for Samba AD to become ready. This combines steps 1-3 of
    the integration testing Quick Start guide.

.PARAMETER WaitForSamba
    Wait for Samba AD to become ready after starting (default: true).
    Set to false to start services without waiting.

.PARAMETER TimeoutSeconds
    Maximum time to wait for Samba AD (default: 180 seconds).

.EXAMPLE
    ./Start-IntegrationTestEnvironment.ps1

    Starts JIM stack, Samba AD, and waits for Samba to be ready.

.EXAMPLE
    ./Start-IntegrationTestEnvironment.ps1 -WaitForSamba:$false

    Starts services but doesn't wait for Samba AD readiness.

.EXAMPLE
    ./Start-IntegrationTestEnvironment.ps1 -TimeoutSeconds 120

    Starts services and waits up to 2 minutes for Samba AD.
#>

param(
    [Parameter(Mandatory=$false)]
    [bool]$WaitForSamba = $true,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 180
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
Write-Host "${BLUE}  Starting Integration Test Environment${NC}"
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""

# Check if we're in the repository root
$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Host "${RED}✗ Not in a git repository${NC}"
    Write-Host "${YELLOW}  Please run this script from within the JIM repository${NC}"
    exit 1
}

$currentDir = (Get-Location).Path
if ($currentDir -ne $repoRoot) {
    Write-Host "${YELLOW}⚠ Current directory is not repository root${NC}"
    Write-Host "${GRAY}  Current: $currentDir${NC}"
    Write-Host "${GRAY}  Root:    $repoRoot${NC}"
    Write-Host "${GRAY}  Changing to repository root...${NC}"
    Set-Location $repoRoot
    Write-Host "${GREEN}✓ Changed to repository root${NC}"
    Write-Host ""
}

# Step 1: Start JIM stack
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host "${BLUE}  Step 1: Starting JIM stack${NC}"
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""

Write-Host "${GRAY}Running: docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d${NC}"
Write-Host ""

$jimResult = docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "${RED}✗ Failed to start JIM stack${NC}"
    Write-Host "${GRAY}$jimResult${NC}"
    exit 1
}

# Show output
Write-Host "${GRAY}$jimResult${NC}"
Write-Host ""
Write-Host "${GREEN}✓ JIM stack started successfully${NC}"
Write-Host ""

# Brief pause to let services initialize
Start-Sleep -Seconds 2

# Step 2: Start Samba AD
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host "${BLUE}  Step 2: Starting Samba AD test infrastructure${NC}"
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""

Write-Host "${GRAY}Running: docker compose -f docker-compose.integration-tests.yml up -d${NC}"
Write-Host ""

$sambaResult = docker compose -f docker-compose.integration-tests.yml up -d 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "${RED}✗ Failed to start Samba AD${NC}"
    Write-Host "${GRAY}$sambaResult${NC}"
    exit 1
}

# Show output
Write-Host "${GRAY}$sambaResult${NC}"
Write-Host ""
Write-Host "${GREEN}✓ Samba AD started successfully${NC}"
Write-Host ""

# Step 3: Wait for Samba AD (optional)
if ($WaitForSamba) {
    Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host "${BLUE}  Step 3: Waiting for Samba AD to become ready${NC}"
    Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host ""

    # Call Wait-SambaReady.ps1
    $waitScript = Join-Path $PSScriptRoot "Wait-SambaReady.ps1"
    if (-not (Test-Path $waitScript)) {
        Write-Host "${YELLOW}⚠ Wait-SambaReady.ps1 not found, skipping readiness check${NC}"
        Write-Host "${GRAY}  Wait ~2 minutes manually before running tests${NC}"
    }
    else {
        & $waitScript -TimeoutSeconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) {
            Write-Host ""
            Write-Host "${RED}✗ Samba AD did not become ready in time${NC}"
            Write-Host "${YELLOW}  You may need to wait longer or check logs: docker logs samba-ad-primary${NC}"
            exit 1
        }
    }
}
else {
    Write-Host "${YELLOW}⚠ Skipping Samba AD readiness check (WaitForSamba=$false)${NC}"
    Write-Host "${GRAY}  Samba AD takes ~2 minutes to initialize${NC}"
    Write-Host "${GRAY}  Run: pwsh test/integration/Wait-SambaReady.ps1${NC}"
    Write-Host ""
}

# Summary
Write-Host ""
Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host "${GREEN}  Integration Test Environment Ready!${NC}"
Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""
Write-Host "${GRAY}Services Status:${NC}"

# Check service status
$jimWeb = docker ps --filter "name=jim.web" --filter "status=running" --format "{{.Status}}" 2>$null
$jimWorker = docker ps --filter "name=jim.worker" --filter "status=running" --format "{{.Status}}" 2>$null
$jimDb = docker ps --filter "name=jim.database" --filter "status=running" --format "{{.Status}}" 2>$null
$samba = docker ps --filter "name=samba-ad-primary" --filter "status=running" --format "{{.Status}}" 2>$null

if ($jimWeb) { Write-Host "  ${GREEN}✓${NC} JIM Web:      ${GRAY}$jimWeb${NC}" }
else { Write-Host "  ${RED}✗${NC} JIM Web:      ${RED}Not running${NC}" }

if ($jimWorker) { Write-Host "  ${GREEN}✓${NC} JIM Worker:   ${GRAY}$jimWorker${NC}" }
else { Write-Host "  ${RED}✗${NC} JIM Worker:   ${RED}Not running${NC}" }

if ($jimDb) { Write-Host "  ${GREEN}✓${NC} JIM Database: ${GRAY}$jimDb${NC}" }
else { Write-Host "  ${RED}✗${NC} JIM Database: ${RED}Not running${NC}" }

if ($samba) { Write-Host "  ${GREEN}✓${NC} Samba AD:     ${GRAY}$samba${NC}" }
else { Write-Host "  ${RED}✗${NC} Samba AD:     ${RED}Not running${NC}" }

Write-Host ""
Write-Host "${GRAY}Available Services:${NC}"
Write-Host "  ${GREEN}•${NC} JIM Web:      ${BLUE}http://localhost:5200${NC}"
Write-Host "  ${GREEN}•${NC} JIM API:      ${BLUE}http://localhost:5200/api${NC}"
Write-Host ""
Write-Host "${GRAY}Next Steps:${NC}"
Write-Host "  1. Create API key:  ${BLUE}pwsh test/integration/Setup-InfrastructureApiKey.ps1${NC}"
Write-Host "  2. Run tests:       ${BLUE}pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template Nano -ApiKey (Get-Content test/integration/.api-key)${NC}"
Write-Host ""
Write-Host "${GRAY}Troubleshooting:${NC}"
Write-Host "  • View logs:        ${BLUE}docker compose logs -f${NC}"
Write-Host "  • Stop services:    ${BLUE}docker compose down && docker compose -f docker-compose.integration-tests.yml down${NC}"
Write-Host "  • Full reset:       ${BLUE}jim-reset${NC}"
Write-Host ""

exit 0
