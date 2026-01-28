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

    When run without parameters, displays interactive menus to select a scenario
    and template size using arrow keys.

.PARAMETER Scenario
    The test scenario to run. If not specified, an interactive menu will be displayed.
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

.EXAMPLE
    ./Run-IntegrationTests.ps1

    Displays interactive menus to select a scenario and template size.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Template Small

    Displays scenario menu, then runs with Small template (skips template menu).

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Step Joiner

    Runs only the Joiner test step.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -SkipReset

    Re-runs tests without resetting the environment.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario "Scenario1-HRToIdentityDirectory" -Template Nano -Step All

    Explicit full specification of all parameters.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario "Scenario2-CrossDomainSync" -Template Small

    Runs Scenario 2 (cross-domain sync between APAC and EMEA directories).
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Scenario,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [switch]$SkipReset,

    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 180
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

# Interactive scenario selection function
function Show-ScenarioMenu {
    # Discover available scenarios
    $scenariosPath = Join-Path $scriptRoot "scenarios"
    $scenarioFiles = Get-ChildItem $scenariosPath -Filter "Invoke-*.ps1" | Sort-Object Name

    if ($scenarioFiles.Count -eq 0) {
        Write-Host "${RED}No scenario scripts found in $scenariosPath${NC}"
        exit 1
    }

    # Build scenario list with descriptions
    $scenarios = @()
    foreach ($file in $scenarioFiles) {
        $scenarioName = $file.BaseName -replace '^Invoke-', ''

        # Extract description from script comments
        $description = ""
        $content = Get-Content $file.FullName -TotalCount 20
        foreach ($line in $content) {
            if ($line -match '^\s*#\s*(.+)') {
                $comment = $Matches[1].Trim()
                if ($comment -and $comment -notmatch '^\.SYNOPSIS|^\.DESCRIPTION|^\.PARAMETER|^\.EXAMPLE|^<#|^#>') {
                    $description = $comment
                    break
                }
            }
        }

        if (-not $description) {
            # Default descriptions based on scenario name
            $description = switch -Wildcard ($scenarioName) {
                "*Scenario1*" { "HR to Identity Directory synchronisation" }
                "*Scenario2*" { "Cross-domain synchronisation (APAC ↔ EMEA)" }
                "*Scenario3*" { "Global Address List (GAL) synchronisation" }
                "*Scenario4*" { "Deletion rules and tombstone handling" }
                "*Scenario5*" { "Matching rules and join logic" }
                "*Scenario8*" { "Cross-domain entitlement synchronisation" }
                default { "Integration test scenario" }
            }
        }

        # Detect stub/unimplemented scenarios by checking for the banner pattern used in placeholder scripts.
        # Must match the exact Write-Host banner to avoid false positives from incidental mentions
        # (e.g., a test step warning containing "not yet implemented" in its message text).
        $disabled = $false
        $fileContent = Get-Content $file.FullName -Raw
        if ($fileContent -match 'Write-Host\s+"[\s]*NOT YET IMPLEMENTED[\s]*"') {
            $disabled = $true
            $description = "$description (not yet implemented)"
        }

        $scenarios += @{
            Name = $scenarioName
            Description = $description
            Disabled = $disabled
        }
    }

    # Find the first selectable (non-disabled) index
    $selectedIndex = 0
    for ($i = 0; $i -lt $scenarios.Count; $i++) {
        if (-not $scenarios[$i].Disabled) {
            $selectedIndex = $i
            break
        }
    }
    $exitMenu = $false

    # Helper: find the next selectable index in a given direction (1=down, -1=up)
    function Find-NextSelectable {
        param([int]$Current, [int]$Direction)
        $next = $Current + $Direction
        while ($next -ge 0 -and $next -lt $scenarios.Count) {
            if (-not $scenarios[$next].Disabled) {
                return $next
            }
            $next += $Direction
        }
        return $Current  # Stay put if no selectable item found
    }

    # Hide cursor
    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Scenario Selection${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host ""

            # Display menu options
            for ($i = 0; $i -lt $scenarios.Count; $i++) {
                $scenario = $scenarios[$i]

                if ($scenario.Disabled) {
                    # Disabled items shown greyed out, not selectable
                    Write-Host "${GRAY}  $($scenario.Name) (deferred)${NC}"
                    Write-Host "${GRAY}  $($scenario.Description)${NC}"
                }
                elseif ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($scenario.Name)${NC}"
                    Write-Host "${GRAY}  $($scenario.Description)${NC}"
                }
                else {
                    Write-Host "  $($scenario.Name)"
                    Write-Host "${GRAY}  $($scenario.Description)${NC}"
                }
                Write-Host ""
            }

            # Wait for key press
            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { # Up arrow
                    $selectedIndex = Find-NextSelectable -Current $selectedIndex -Direction (-1)
                }
                40 { # Down arrow
                    $selectedIndex = Find-NextSelectable -Current $selectedIndex -Direction 1
                }
                13 { # Enter
                    $exitMenu = $true
                }
                27 { # Escape
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        # Restore cursor
        [Console]::CursorVisible = $true
    }

    Clear-Host
    return $scenarios[$selectedIndex].Name
}

# Interactive template selection function
function Show-TemplateMenu {
    # Define templates with descriptions
    $templates = @(
        @{
            Name = "Nano"
            Users = 3
            Groups = 1
            Description = "Minimal data for quick iteration"
            Time = "~10 sec"
        }
        @{
            Name = "Micro"
            Users = 10
            Groups = 3
            Description = "Quick smoke tests"
            Time = "~30 sec"
        }
        @{
            Name = "Small"
            Users = 100
            Groups = 20
            Description = "Small business scenarios"
            Time = "~2 min"
        }
        @{
            Name = "Medium"
            Users = 1000
            Groups = 100
            Description = "Medium enterprise"
            Time = "~5 min"
        }
        @{
            Name = "MediumLarge"
            Users = 5000
            Groups = 250
            Description = "Growing enterprise"
            Time = "~10 min"
        }
        @{
            Name = "Large"
            Users = 10000
            Groups = 500
            Description = "Large enterprise"
            Time = "~15 min"
        }
        @{
            Name = "XLarge"
            Users = 100000
            Groups = 2000
            Description = "Very large enterprise"
            Time = "~1 hour"
        }
        @{
            Name = "XXLarge"
            Users = 1000000
            Groups = 10000
            Description = "Stress testing"
            Time = "~4 hours"
        }
    )

    $selectedIndex = 0
    $exitMenu = $false

    # Hide cursor
    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Template Size Selection${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host ""

            # Display menu options
            for ($i = 0; $i -lt $templates.Count; $i++) {
                $template = $templates[$i]
                $userCount = $template.Users.ToString("N0")
                $groupCount = $template.Groups.ToString("N0")
                $stats = "$userCount users, $groupCount groups"

                if ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($template.Name)${NC} ${GRAY}($stats)${NC}"
                    Write-Host "${GRAY}  $($template.Description) - $($template.Time)${NC}"
                }
                else {
                    Write-Host "  $($template.Name) ${GRAY}($stats)${NC}"
                    Write-Host "${GRAY}  $($template.Description) - $($template.Time)${NC}"
                }
                Write-Host ""
            }

            # Wait for key press
            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { # Up arrow
                    $selectedIndex = [Math]::Max(0, $selectedIndex - 1)
                }
                40 { # Down arrow
                    $selectedIndex = [Math]::Min($templates.Count - 1, $selectedIndex + 1)
                }
                13 { # Enter
                    $exitMenu = $true
                }
                27 { # Escape
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        # Restore cursor
        [Console]::CursorVisible = $true
    }

    Clear-Host
    return $templates[$selectedIndex].Name
}

# Track if user explicitly set Template parameter
$TemplateWasExplicitlySet = $PSBoundParameters.ContainsKey('Template')

# Scenarios that provision their own fixed test data and don't use the Template parameter
# for data sizing. These scenarios accept Template but it has no effect on test execution.
$templateIrrelevantScenarios = @(
    "*Scenario2*",   # Cross-Domain Sync - uses fixed test users (crossdomain.test1, etc.)
    "*Scenario3*",   # GAL Sync - not yet implemented
    "*Scenario4*"    # Deletion Rules - provisions individual test users, ignores template
)

function Test-TemplateRelevant {
    param([string]$ScenarioName)
    foreach ($pattern in $templateIrrelevantScenarios) {
        if ($ScenarioName -like $pattern) {
            return $false
        }
    }
    return $true
}

# If no scenario specified, show interactive menu
if (-not $Scenario) {
    $Scenario = Show-ScenarioMenu

    # Show template menu only if Template wasn't explicitly provided AND the scenario uses it
    if (-not $TemplateWasExplicitlySet) {
        if (Test-TemplateRelevant -ScenarioName $Scenario) {
            $Template = Show-TemplateMenu
        }
        else {
            $Template = "Nano"
        }
    }
}

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

$templateRelevant = Test-TemplateRelevant -ScenarioName $Scenario

Write-Host "${GRAY}Configuration:${NC}"
Write-Host "  Scenario:           ${CYAN}$Scenario${NC}"
if ($templateRelevant) {
    Write-Host "  Template:           ${CYAN}$Template${NC}"
} else {
    Write-Host "  Template:           ${GRAY}N/A (scenario uses fixed test data)${NC}"
}
Write-Host "  Step:               ${CYAN}$Step${NC}"
Write-Host "  Skip Reset:         ${CYAN}$SkipReset${NC}"
Write-Host "  Skip Build:         ${CYAN}$SkipBuild${NC}"
Write-Host "  Service Timeout:    ${CYAN}${TimeoutSeconds}s${NC}"
Write-Host ""

# Change to repository root
Set-Location $repoRoot

# Step 0: Ensure Samba AD images exist
$step0Start = Get-Date
Write-Section "Step 0: Checking Samba AD Images"

$buildScript = Join-Path $scriptRoot "docker" "samba-ad-prebuilt" "Build-SambaImages.ps1"

# Check if the pre-built Samba AD primary image exists locally
$sambaImageTag = "ghcr.io/tetronio/jim-samba-ad:primary"
$imageExists = docker images -q $sambaImageTag 2>$null

if (-not $imageExists) {
    Write-Warning "Pre-built Samba AD image not found locally: $sambaImageTag"
    Write-Step "Building Samba AD Primary image (this takes ~2-3 minutes, but only needs to be done once)..."

    if (-not (Test-Path $buildScript)) {
        Write-Failure "Build script not found: $buildScript"
        exit 1
    }

    & $buildScript -Images Primary
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to build Samba AD Primary image"
        exit 1
    }

    Write-Success "Samba AD Primary image built successfully"
}
else {
    Write-Success "Samba AD Primary image found: $sambaImageTag"
}

# For Scenario 2 and Scenario 8, also check for Source and Target images
if ($Scenario -like "*Scenario2*" -or $Scenario -like "*Scenario8*") {
    # Check Source image
    $sourceImageTag = "ghcr.io/tetronio/jim-samba-ad:source"
    $sourceExists = docker images -q $sourceImageTag 2>$null

    if (-not $sourceExists) {
        Write-Warning "Pre-built Samba AD Source image not found locally: $sourceImageTag"
        Write-Step "Building Samba AD Source image (this takes ~2-3 minutes, but only needs to be done once)..."

        if (-not (Test-Path $buildScript)) {
            Write-Failure "Build script not found: $buildScript"
            exit 1
        }

        & $buildScript -Images Source
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "Failed to build Samba AD Source image"
            exit 1
        }

        Write-Success "Samba AD Source image built successfully"
    }
    else {
        Write-Success "Samba AD Source image found: $sourceImageTag"
    }

    # Check Target image
    $targetImageTag = "ghcr.io/tetronio/jim-samba-ad:target"
    $targetExists = docker images -q $targetImageTag 2>$null

    if (-not $targetExists) {
        Write-Warning "Pre-built Samba AD Target image not found locally: $targetImageTag"
        Write-Step "Building Samba AD Target image (this takes ~2-3 minutes, but only needs to be done once)..."

        if (-not (Test-Path $buildScript)) {
            Write-Failure "Build script not found: $buildScript"
            exit 1
        }

        & $buildScript -Images Target
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "Failed to build Samba AD Target image"
            exit 1
        }

        Write-Success "Samba AD Target image built successfully"
    }
    else {
        Write-Success "Samba AD Target image found: $targetImageTag"
    }
}

$timings["0. Check Samba Image"] = (Get-Date) - $step0Start

# Step 1: Reset (unless skipped)
$step1Start = Get-Date
if (-not $SkipReset) {
    Write-Section "Step 1: Resetting JIM Environment"

    Write-Step "Stopping all containers and removing volumes..."
    docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down -v 2>&1 | Out-Null
    # Use --profile to stop containers from all scenarios (scenario2, scenario8, etc.)
    # Without specifying profiles, containers started with profiles won't be stopped
    docker compose -f docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down -v --remove-orphans 2>&1 | Out-Null

    # Force-remove any leftover integration test containers by name.
    # This handles containers that were created under a different Docker Compose project name
    # (e.g., 'jim' instead of 'jim-integration') and are therefore not cleaned up by 'down -v'.
    Write-Step "Removing any leftover integration test containers..."
    $integrationContainers = @("samba-ad-primary", "samba-ad-source", "samba-ad-target", "sqlserver-hris-a", "oracle-hris-b", "postgres-target", "openldap-test", "mysql-test")
    foreach ($container in $integrationContainers) {
        docker rm -f $container 2>&1 | Out-Null
    }

    # Also remove any orphan integration test volumes that might have different names
    # This ensures a completely clean state even if volume naming has changed
    Write-Step "Removing any orphan integration test volumes..."
    $orphanVolumes = docker volume ls --format '{{.Name}}' | Where-Object { $_ -match 'jim-integration' }
    foreach ($vol in $orphanVolumes) {
        docker volume rm $vol 2>&1 | Out-Null
    }

    # Remove the JIM database volume to ensure completely fresh state
    docker volume rm jim-db-volume 2>&1 | Out-Null

    Write-Success "Containers stopped and all volumes removed"
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

# Step 2b: Generate API Key (before starting JIM so it picks up the key on first startup)
Write-Section "Step 2b: Generating API Key"

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

Write-Step "Starting Samba AD (Primary)..."
$sambaResult = docker compose -f docker-compose.integration-tests.yml up -d 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Failed to start Samba AD"
    Write-Host "${GRAY}$sambaResult${NC}"
    exit 1
}
Write-Success "Samba AD Primary started"

# Start Scenario 2 containers if running Scenario 2
if ($Scenario -like "*Scenario2*") {
    Write-Step "Starting Samba AD (Source and Target for Scenario 2)..."
    $scenario2Result = docker compose -f docker-compose.integration-tests.yml --profile scenario2 up -d 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to start Scenario 2 Samba AD containers"
        Write-Host "${GRAY}$scenario2Result${NC}"
        exit 1
    }
    Write-Success "Samba AD Source and Target started"
}

# Start Scenario 8 containers if running Scenario 8
if ($Scenario -like "*Scenario8*") {
    Write-Step "Starting Samba AD (Source and Target for Scenario 8)..."
    $scenario8Result = docker compose -f docker-compose.integration-tests.yml --profile scenario8 up -d 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to start Scenario 8 Samba AD containers"
        Write-Host "${GRAY}$scenario8Result${NC}"
        exit 1
    }
    Write-Success "Samba AD Source and Target started for Scenario 8"
}
$timings["3. Start Services"] = (Get-Date) - $step3Start

# Step 4: Wait for services
$step4Start = Get-Date
Write-Section "Step 4: Waiting for Services"

# Wait for Samba AD Primary
Write-Step "Waiting for Samba AD Primary to be ready..."
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

# Wait for Scenario 2 or Scenario 8 containers if applicable
if ($Scenario -like "*Scenario2*" -or $Scenario -like "*Scenario8*") {
    Write-Step "Waiting for Samba AD Source to be ready..."
    $sourceReady = $false
    $elapsed = 0
    while (-not $sourceReady -and $elapsed -lt $TimeoutSeconds) {
        $status = docker inspect --format='{{.State.Health.Status}}' samba-ad-source 2>&1
        if ($status -eq "healthy") {
            $sourceReady = $true
            Write-Success "Samba AD Source is healthy"
        }
        else {
            Start-Sleep -Seconds 5
            $elapsed += 5
        }
    }
    if (-not $sourceReady) {
        Write-Failure "Samba AD Source did not become ready in time"
        Write-Host "${YELLOW}  Check logs: docker logs samba-ad-source${NC}"
        exit 1
    }

    Write-Step "Waiting for Samba AD Target to be ready..."
    $targetReady = $false
    $elapsed = 0
    while (-not $targetReady -and $elapsed -lt $TimeoutSeconds) {
        $status = docker inspect --format='{{.State.Health.Status}}' samba-ad-target 2>&1
        if ($status -eq "healthy") {
            $targetReady = $true
            Write-Success "Samba AD Target is healthy"
        }
        else {
            Start-Sleep -Seconds 5
            $elapsed += 5
        }
    }
    if (-not $targetReady) {
        Write-Failure "Samba AD Target did not become ready in time"
        Write-Host "${YELLOW}  Check logs: docker logs samba-ad-target${NC}"
        exit 1
    }
}
# Wait for JIM Web API
Write-Step "Waiting for JIM Web API to be ready..."
$jimApiReady = $false
$jimApiElapsed = 0
$jimApiUrl = "http://localhost:5200/api/v1/health"

while (-not $jimApiReady -and $jimApiElapsed -lt $TimeoutSeconds) {
    try {
        $healthResponse = Invoke-WebRequest -Uri $jimApiUrl -Method GET -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($healthResponse.StatusCode -eq 200) {
            $jimApiReady = $true
            Write-Success "JIM Web API is healthy (HTTP 200)"
        }
    }
    catch {
        # Ignore connection errors during startup
    }

    if (-not $jimApiReady) {
        Start-Sleep -Seconds 3
        $jimApiElapsed += 3
        if ($jimApiElapsed % 15 -eq 0) {
            Write-Step "  Still waiting for JIM Web API... (${jimApiElapsed}s / ${TimeoutSeconds}s)"
        }
    }
}

if (-not $jimApiReady) {
    Write-Failure "JIM Web API did not become ready within ${TimeoutSeconds}s"
    Write-Host "${YELLOW}  Check logs: docker compose logs jim.web${NC}"
    exit 1
}

$timings["4. Wait for Services"] = (Get-Date) - $step4Start

# Step 4b: Prepare Samba AD for testing
# For Scenario 1, we need a clean Corp OU - delete if exists and recreate
# Scenario 2 uses TestUsers OU which is handled by the scenario setup script
if ($Scenario -like "*Scenario1*") {
    Write-Section "Step 4b: Preparing Samba AD for Testing"

    # First, try to delete the Corp OU if it exists (to ensure clean state)
    Write-Step "Cleaning up any existing Corp OU..."
    $result = docker exec samba-ad-primary samba-tool ou delete "OU=Corp,DC=subatomic,DC=local" --force-subtree-delete 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Deleted existing OU: Corp"
    }
    elseif ($result -match "No such object") {
        Write-Success "OU does not exist (clean state)"
    }
    else {
        Write-Warning "Could not delete OU Corp: $result (continuing anyway)"
    }

    # Create the Corp base OU and its sub-OUs (Users, Groups)
    Write-Step "Creating Corp OU structure..."
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Corp,DC=subatomic,DC=local" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Created OU: Corp"
    }
    elseif ($result -match "already exists") {
        Write-Success "OU already exists: Corp"
    }
    else {
        Write-Warning "Failed to create OU Corp: $result"
    }

    # Create Users OU under Corp
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Users,OU=Corp,DC=subatomic,DC=local" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Created OU: Users (under Corp)"
    }
    elseif ($result -match "already exists") {
        Write-Success "OU already exists: Users"
    }
    else {
        Write-Warning "Failed to create OU Users: $result"
    }

    # Create Groups OU under Corp
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Groups,OU=Corp,DC=subatomic,DC=local" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Created OU: Groups (under Corp)"
    }
    elseif ($result -match "already exists") {
        Write-Success "OU already exists: Groups"
    }
    else {
        Write-Warning "Failed to create OU Groups: $result"
    }
}

# Step 5: Run test scenario
$step5Start = Get-Date
Write-Section "Step 5: Running Test Scenario"

$scenarioScript = Join-Path $scriptRoot "scenarios" "Invoke-$Scenario.ps1"
if (-not (Test-Path $scenarioScript)) {
    Write-Failure "Scenario script not found: $scenarioScript"
    Write-Host "${GRAY}Available scenarios:${NC}"
    Get-ChildItem (Join-Path $scriptRoot "scenarios") -Filter "Invoke-*.ps1" | ForEach-Object {
        Write-Host "  - $($_.BaseName -replace 'Invoke-', '')" -ForegroundColor Gray
    }
    exit 1
}

# Check for unimplemented/deferred scenarios
$scenarioContent = Get-Content $scenarioScript -Raw
if ($scenarioContent -match 'Write-Host\s+"[\s]*NOT YET IMPLEMENTED[\s]*"') {
    Write-Failure "Scenario '$Scenario' is not yet implemented (deferred)"
    Write-Host "${GRAY}This scenario exists as a placeholder but has no test logic yet.${NC}"
    Write-Host "${GRAY}Select a different scenario or check the project backlog for status.${NC}"
    exit 1
}

Write-Step "Running: Invoke-$Scenario.ps1 -Template $Template -Step $Step"
Write-Host ""

# Capture scenario console output to a log file for diagnostics.
# The log preserves all PASSED/FAILED step details, warnings, and errors that would
# otherwise only be visible in the live console output.
# Uses Start-Transcript because scenario scripts use Write-Host which bypasses the
# standard output pipeline (Tee-Object/redirection cannot capture Write-Host output).
$logDir = Join-Path $scriptRoot "results" "logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
$logTimestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
$scenarioLogFile = Join-Path $logDir "$Scenario-$Template-$logTimestamp.log"

Start-Transcript -Path $scenarioLogFile -Append | Out-Null
try {
    & $scenarioScript -Template $Template -Step $Step -ApiKey $apiKey
    $scenarioExitCode = $LASTEXITCODE
}
finally {
    Stop-Transcript | Out-Null
}
$timings["5. Run Tests"] = (Get-Date) - $step5Start

Write-Host ""
Write-Step "Scenario log saved to: results/logs/$Scenario-$Template-$logTimestamp.log"

# Step 6: Capture Performance Metrics
$step6Start = Get-Date
Write-Section "Step 6: Capturing Performance Metrics"

Write-Step "Extracting diagnostic timing from worker logs..."

# Capture worker logs with diagnostic output
$workerLogs = docker logs jim.worker 2>&1 | Where-Object { $_ -match "DiagnosticListener:" }

# Parse metrics into structured data using parallel processing
$metrics = @{
    Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Scenario = $Scenario
    Template = $Template
    Step = $Step
    Operations = @()
}

# Use parallel processing for log parsing (PowerShell 7+)
$operations = $workerLogs | ForEach-Object -Parallel {
    $logLine = $_
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

        # Return the operation (will be collected)
        $operation
    }
} -ThrottleLimit ([Environment]::ProcessorCount)

# Add parsed operations to metrics
if ($operations) {
    $metrics.Operations = @($operations)
}

if ($metrics.Operations.Count -eq 0) {
    Write-Warning "No performance metrics found in worker logs"
}
else {
    Write-Success "Captured $($metrics.Operations.Count) operation timings"

    # Display hierarchical tree view of operations
    Write-Host ""
    Write-Host "${CYAN}Performance Breakdown (Hierarchical):${NC}"
    Write-Host "${GRAY}Note: Times show CUMULATIVE totals across all invocations. Child totals may exceed${NC}"
    Write-Host "${GRAY}parent totals when operations are called multiple times within loops (e.g., per page).${NC}"
    Write-Host ""

    # Build parent-child relationships and calculate totals
    # NOTE: Child times represent CUMULATIVE time across all invocations, not time within a single parent invocation.
    # When a child operation is called multiple times within a loop (e.g., once per page), the sum of all child
    # invocations may exceed the parent's single invocation time. This is expected behaviour.
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

        # Compare key operations - use parallel grouping for better performance
        $currentOps = $metrics.Operations | Group-Object -Property Name -AsHashTable -AsString
        $baselineOps = $baseline.Operations | Group-Object -Property Name -AsHashTable -AsString

        # Display comparison for key operations
        $keyOperations = @("FullImport", "FullSync", "Export", "ProcessConnectedSystemObjects")

        foreach ($opName in $keyOperations) {
            if ($currentOps.ContainsKey($opName) -and $baselineOps.ContainsKey($opName)) {
                $currentAvg = ($currentOps[$opName].DurationMs | Measure-Object -Average).Average
                $baselineAvg = ($baselineOps[$opName].DurationMs | Measure-Object -Average).Average
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

$timings["6. Capture Metrics"] = (Get-Date) - $step6Start

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
Write-Host "${GRAY}Note: '5. Run Tests' breakdown shown in 'Performance Breakdown (Test Steps)' section above${NC}"
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
