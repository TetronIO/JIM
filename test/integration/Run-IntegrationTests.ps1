# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs JIM integration tests with automatic environment setup.

.DESCRIPTION
    Single entry point for running integration tests. This script:
    1. Resets the JIM environment (stops containers, removes volumes)
    2. Rebuilds and starts the JIM stack and Samba AD
    3. Waits for all services to be ready
    4. Creates an infrastructure API key
    5. Configures JIM with connected systems and sync rules (via scenario setup)
    6. Runs the specified test scenario

    When run without parameters, displays interactive menus to select a scenario
    and template size using arrow keys.

    Use -SetupOnly to stop after environment setup and configuration (step 5),
    leaving the environment running for manual exploration, demos, or development.

.PARAMETER Scenario
    The test scenario to run. If not specified, an interactive menu will be displayed.
    Available scenarios are in test/integration/scenarios/
    Use "All" to run every implemented (non-stub) scenario sequentially. Docker images
    are built once on the first scenario; subsequent scenarios reset the environment
    without rebuilding. A pass/fail summary is printed at the end.

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

.PARAMETER SetupOnly
    Stop after environment setup and scenario configuration. The JIM stack, Samba AD,
    and all connected systems/sync rules will be configured, but no test steps will run.
    Use this for demos, manual exploration, or iterative development.

.PARAMETER ExportConcurrency
    Export Concurrency setting for LDAP connectors. Controls how many LDAP operations
    are pipelined concurrently during export. Default: 1 (sequential).
    Higher values improve throughput but increase load on the target directory.
    Only applies to scenarios with LDAP exports (Scenarios 1, 2, 8, 10).

.PARAMETER MaxExportParallelism
    Maximum number of parallel export batches for Connected Systems. Controls how many
    export batches are processed concurrently. Default: 1 (sequential).
    Higher values improve throughput for large exports.
    Only applies to scenarios with LDAP exports (Scenarios 1, 2, 8, 10).

.PARAMETER TimeoutSeconds
    Maximum time to wait for services to be ready. Default: 180 seconds.

.PARAMETER CaptureMetrics
    Force capture of detailed performance metrics even for large templates (MediumLarge+).
    By default, metrics capture is skipped for large templates because parsing the worker
    logs is prohibitively slow. Use this flag when you need performance data for comparison.

.PARAMETER LogLevel
    Sets the JIM log level in the .env file before starting containers.
    Valid levels: Verbose, Debug, Information, Warning, Error, Fatal.
    If not specified, uses whatever is currently set in .env (default: Debug).
    The original .env value is restored after the test run completes.
    Use Warning or higher for large tests to reduce log volume.

.PARAMETER DisableChangeTracking
    Disables both CSO and MVO change tracking via the service settings API after
    services are ready. This reduces database writes during large test runs.
    Change tracking is enabled by default.

.PARAMETER ContinueOnFailure
    When running multiple scenarios (-Scenario All or -DirectoryType All), keep
    running subsequent scenarios after one fails instead of aborting immediately.
    The default is fail-fast: as soon as a scenario fails the runner prints the
    summary of what ran and exits non-zero. Use -ContinueOnFailure when you are
    diagnosing whether a regression is localised to one scenario or widespread.

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

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario "Scenario1-HRToIdentityDirectory" -SetupOnly

    Sets up the full environment with Scenario 1 configuration, then stops for manual use.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario "Scenario8-CrossDomainEntitlementSync" -Template MediumLarge -CaptureMetrics

    Runs Scenario 8 with MediumLarge template and forces performance metrics capture.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario All -Template Small

    Runs all implemented (non-stub) scenarios sequentially with the Small template.
    Docker images are built once; the environment is reset between each scenario.
    A pass/fail summary is printed at the end.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario All -Template Small -DirectoryType All

    Runs all scenarios against Samba AD first, then all scenarios against OpenLDAP.
    Full environment teardown and rebuild between directory types.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -DirectoryType All

    Runs Scenario 1 against Samba AD, then against OpenLDAP.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario All -DirectoryType OpenLDAP -Template Small

    Runs all scenarios against OpenLDAP only with the Small template.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -Template Large -LogLevel Warning -DisableChangeTracking

    Runs Scenario 1 with Large template, reduced logging (Warning level), and
    change tracking disabled for maximum throughput during large-scale testing.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario All -DirectoryType All -TemplateSambaAD Medium -TemplateOpenLDAP Scale100k50Groups

    Runs all scenarios against both directory types with different template sizes.
    Samba AD uses Medium (faster population), OpenLDAP uses Scale100k50Groups.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -PreRelease

    Runs the full pre-release regression: every implemented scenario against both
    directory types, with Samba AD at the Medium template and OpenLDAP at Large.
    Equivalent to: -Scenario All -DirectoryType All -TemplateSambaAD Medium -TemplateOpenLDAP Large.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Scenario,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [switch]$SkipReset,

    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory=$false)]
    [switch]$SetupOnly,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 180,

    [Parameter(Mandatory=$false)]
    [switch]$CaptureMetrics,

    [Parameter(Mandatory=$false)]
    [switch]$IgnoreSnapshots,

    [Parameter(Mandatory=$false)]
    [ValidateSet("SambaAD", "OpenLDAP", "All")]
    [string]$DirectoryType = "SambaAD",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Verbose", "Debug", "Information", "Warning", "Error", "Fatal")]
    [string]$LogLevel,

    [Parameter(Mandatory=$false)]
    [switch]$DisableChangeTracking,

    # OpenLDAP integration containers default to relaxed MDB durability (nosync; no
    # per-transaction fsync) so large-template test cycles run fast. That is a TEST-ONLY
    # speed-up and NOT the customer experience: real directories fsync their writes and
    # that bounds export throughput. Pass this switch to run with durable,
    # customer-representative directory writes. Performance baselines are kept separate
    # per mode. Has no effect on Samba AD runs (always durable).
    [Parameter(Mandatory=$false)]
    [switch]$DurableDirectoryWrites,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$TemplateSambaAD,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$TemplateOpenLDAP,

    [Parameter(Mandatory=$false)]
    [switch]$PreRelease,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnFailure,

    # ─── Scenario 11 (Scoping Criteria Matrix) — coverage and shape options ───
    # Mutually exclusive: pick one tier, or neither for Default. Ignored by every
    # scenario except Scenario11-ScopingCriteriaMatrix.

    [Parameter(Mandatory=$false)]
    [switch]$Quick,

    [Parameter(Mandatory=$false)]
    [switch]$Exhaustive,

    [Parameter(Mandatory=$false)]
    [string]$OperatorFilter,

    [Parameter(Mandatory=$false)]
    [bool]$IncludeNegativeCells
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

# Import helpers early so Get-DirectoryConfig is available
. "$scriptRoot/utils/Test-Helpers.ps1"
. "$scriptRoot/utils/Initialize-WorkerLogDirectories.ps1"

# Hydrate JIM_BENCH_* from .env when not already set in the process environment.
# .env is the canonical config surface for the project, but Docker Compose only
# reads it for containers; PowerShell doesn't auto-load it. Shell wins (so a
# deliberate `export` still overrides), .env is the fallback. Scoped to the two
# bench keys only; we don't want to silently leak unrelated .env values into
# the host environment.
$envFilePath = Join-Path $repoRoot ".env"
if (Test-Path $envFilePath) {
    foreach ($key in @("JIM_BENCH_API_URL", "JIM_BENCH_API_KEY")) {
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($key))) {
            $match = Select-String -Path $envFilePath -Pattern "^\s*$key\s*=\s*(.*)$" | Select-Object -First 1
            if ($match) {
                $value = $match.Matches[0].Groups[1].Value.Trim()
                # Strip surrounding single or double quotes if present
                if ($value -match '^"(.*)"$' -or $value -match "^'(.*)'$") {
                    $value = $matches[1]
                }
                if (-not [string]::IsNullOrEmpty($value)) {
                    Set-Item -Path "env:$key" -Value $value
                }
            }
        }
    }
}

# Hard-fail: the long-tail templates (Scale100k5kGroups through Scale1m60kGroups)
# are OpenLDAP only. Reject early at the orchestrator level so users discover
# the constraint at selection time, not hours into population. The populator
# scripts have matching guards (defence in depth).
$script:LongTailTemplates = @("Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
function Test-LongTailTemplateCompatibility {
    param([string]$Template, [string]$DirectoryType, [string]$TemplateSambaAD, [string]$TemplateOpenLDAP)
    $offendingValues = @()
    if ($Template -in $script:LongTailTemplates -and $DirectoryType -in @("SambaAD", "All")) {
        $offendingValues += "-Template $Template -DirectoryType $DirectoryType"
    }
    if ($TemplateSambaAD -in $script:LongTailTemplates) {
        $offendingValues += "-TemplateSambaAD $TemplateSambaAD"
    }
    if ($offendingValues.Count -gt 0) {
        $msg = "The long-tail templates ($($script:LongTailTemplates -join ', ')) are OpenLDAP only (Scenario 8 long-tail group shape). Samba AD cannot populate thousands of groups within the time budget. Rejected: $($offendingValues -join ', '). Use -Template Scale100k50Groups or another capped-groups template for Samba scale testing, or pin to -DirectoryType OpenLDAP."
        throw $msg
    }
}
Test-LongTailTemplateCompatibility -Template $Template -DirectoryType $DirectoryType `
    -TemplateSambaAD $TemplateSambaAD -TemplateOpenLDAP $TemplateOpenLDAP

# NOTE: Scenario 14 (Attribute Priority) is OpenLDAP only (two-suffix topology). Its
# directory-type handling runs *after* scenario/directory resolution (see "Scenario 14
# directory coercion" below), not here, because when the scenario is chosen from the
# interactive menu $Scenario is still empty at this point.

# Resolve directory configuration (used throughout for Docker profiles, population, setup)
# Skip for "All" — the DirectoryType All handler orchestrates multiple runs with specific types.
if ($DirectoryType -ne "All") {
    $script:DirectoryConfig = Get-DirectoryConfig -DirectoryType $DirectoryType
}

# ============================================================================
# Docker image pruning (preserves snapshot/build images)
# ============================================================================

# NOTE: docker image prune --filter "label!=X" with multiple filters is broken —
# it deletes labelled images despite the exclusion. Work around this by collecting
# the IDs of images to preserve, pruning everything else, then cleaning up dangling.
function Invoke-ImagePrunePreservingSnapshots {
    $labels = @("jim.samba.snapshot-hash", "jim.samba.build-hash", "jim.openldap.snapshot-hash", "jim.openldap.build-hash")
    $preserveIds = @()
    foreach ($label in $labels) {
        $ids = docker images --filter "label=$label" -q 2>$null
        if ($ids) { $preserveIds += $ids }
    }
    # Wrap with @(...) so that when zero images carry a preserve label the pipeline yields an empty
    # array rather than $null; under Set-StrictMode -Version Latest, $null.Count throws
    # "The property 'Count' cannot be found on this object" and aborts Step 7 cleanup on an otherwise
    # green run (same idiom used elsewhere in this file for StrictMode-safe .Count access).
    $preserveIds = @($preserveIds | Sort-Object -Unique | Where-Object { $_ -ne "" })

    if ($preserveIds.Count -eq 0) {
        $result = docker image prune -af 2>&1
        return $result
    }

    # Get all image IDs, subtract the ones to preserve, remove the rest
    $allIds = docker images -a -q 2>$null | Sort-Object -Unique
    $removeIds = $allIds | Where-Object { $_ -notin $preserveIds }

    $result = @()
    if ($removeIds) {
        $result += docker rmi -f $removeIds 2>&1
    }
    # Clean up any remaining dangling images
    $result += docker image prune -f 2>&1
    return $result
}

# ============================================================================
# Snapshot detection utilities
# ============================================================================

function Get-PopulateScriptHash {
    param([string]$ScenarioName)
    $filesToHash = @(
        "$scriptRoot/utils/Test-Helpers.ps1",
        "$scriptRoot/utils/Test-GroupHelpers.ps1",
        "$scriptRoot/Build-SambaSnapshots.ps1"
    )
    switch ($ScenarioName) {
        "Scenario1" {
            # S1 no longer populates test users — no extra files to hash
        }
        "Scenario8" {
            $filesToHash += "$scriptRoot/Populate-SambaAD-Scenario8.ps1"
        }
    }
    $combinedContent = ""
    foreach ($file in $filesToHash) {
        if (Test-Path $file) { $combinedContent += Get-Content -Path $file -Raw }
    }
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($combinedContent)
    )
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 16).ToLower()
}

function Get-SnapshotImageTag {
    param([string]$Role, [string]$Size)
    return "jim-samba-ad:${Role}-$($Size.ToLower())"
}

function Get-SambaBaseBuildHash {
    # Compute expected build hash for the base Samba AD images from the files that affect
    # them. Must match the hash computed by Build-SambaImages.ps1 (same file list, same order).
    $sambaScriptDir = Join-Path $scriptRoot "docker" "samba-ad-prebuilt"
    $filesToHash = @(
        (Join-Path $sambaScriptDir "post-provision.sh"),
        (Join-Path $sambaScriptDir "start-samba.sh")
    )
    $combinedContent = ($filesToHash | ForEach-Object { Get-Content -Path $_ -Raw }) -join ""
    return [System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($combinedContent))
    ).Replace("-", "").Substring(0, 16).ToLower()
}

function Test-SnapshotAvailable {
    param([string]$ImageTag, [string]$ExpectedHash)
    $inspect = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.samba.snapshot-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    if ("$inspect" -ne $ExpectedHash) { return $false }

    # Also verify the snapshot was baked from the current base image build. Snapshots
    # capture the base's provisioned state (e.g. password policy, TLS, OUs), so checking
    # the base image on disk is not enough: a snapshot built from an older base stays
    # stale even after the base itself is rebuilt. Snapshots without the base-hash label
    # predate this check and are treated as stale.
    $snapshotBaseHash = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.samba.base-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    $expectedBuildHash = Get-SambaBaseBuildHash
    if ("$snapshotBaseHash" -ne $expectedBuildHash) {
        Write-Host "  ${YELLOW}Snapshot '$ImageTag' was built from a stale base image (base hash '$snapshotBaseHash' != $expectedBuildHash) — snapshot needs rebuild${NC}"
        return $false
    }

    return $true
}

# Track whether snapshots are being used (set during container startup)
$script:UsingSnapshots = $false
$script:UsingOpenLDAPSnapshots = $false

# ============================================================================
# OpenLDAP snapshot detection utilities
# ============================================================================

function Get-OpenLDAPPopulateScriptHash {
    param([string]$ScenarioName)
    $filesToHash = @(
        "$scriptRoot/utils/Test-Helpers.ps1",
        "$scriptRoot/utils/Test-GroupHelpers.ps1",
        "$scriptRoot/Build-OpenLDAPSnapshots.ps1",
        "$scriptRoot/docker/openldap/Dockerfile",
        "$scriptRoot/docker/openldap/scripts/01-add-second-suffix.sh",
        "$scriptRoot/docker/openldap/bootstrap/01-base-ous-yellowstone.ldif",
        "$scriptRoot/docker/openldap/start-openldap.sh"
    )
    switch ($ScenarioName) {
        "General" { $filesToHash += "$scriptRoot/Populate-OpenLDAP.ps1" }
        "Scenario8" { $filesToHash += "$scriptRoot/Populate-OpenLDAP-Scenario8.ps1" }
    }
    $combinedContent = ""
    foreach ($file in $filesToHash) {
        if (Test-Path $file) { $combinedContent += Get-Content -Path $file -Raw }
    }
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($combinedContent)
    )
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 16).ToLower()
}

function Get-OpenLDAPSnapshotImageTag {
    param([string]$Role, [string]$Size)
    return "jim-openldap:${Role}-$($Size.ToLower())"
}

function Get-OpenLDAPBaseBuildHash {
    # Compute expected build hash for the base OpenLDAP image from the files that affect it.
    # Must match the hash computed by Build-OpenLdapImage.ps1 (same file list).
    $filesToHash = @(
        "$scriptRoot/docker/openldap/Dockerfile",
        "$scriptRoot/docker/openldap/scripts/01-add-second-suffix.sh",
        "$scriptRoot/docker/openldap/bootstrap/01-base-ous-yellowstone.ldif"
    )
    $combinedContent = ($filesToHash | ForEach-Object { Get-Content -Path $_ -Raw }) -join ""
    return [System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($combinedContent))
    ).Replace("-", "").Substring(0, 16).ToLower()
}

function Test-OpenLDAPSnapshotAvailable {
    param([string]$ImageTag, [string]$ExpectedHash)
    $inspect = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.openldap.snapshot-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    if ("$inspect" -ne $ExpectedHash) { return $false }

    # Also verify the snapshot was baked from the current base image build. Snapshots
    # capture the base's init state (schema, suffixes, accesslog config), so checking
    # the base image on disk is not enough: a snapshot built from an older base stays
    # stale even after the base itself is rebuilt. Snapshots without the base-hash label
    # predate this check and are treated as stale.
    $snapshotBaseHash = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.openldap.base-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    $expectedBuildHash = Get-OpenLDAPBaseBuildHash
    if ("$snapshotBaseHash" -ne $expectedBuildHash) {
        Write-Host "  ${YELLOW}Snapshot '$ImageTag' was built from a stale base image (base hash '$snapshotBaseHash' != $expectedBuildHash) — snapshot needs rebuild${NC}"
        return $false
    }

    return $true
}

# Discover scenario Invoke-*.ps1 scripts in numeric order. Sort by the numeric index embedded in
# the filename (Scenario1, Scenario2, ..., Scenario10, ..., Scenario13) rather than lexically — a
# plain Sort-Object Name puts Scenario10+ between Scenario1 and Scenario2. Single source of truth so
# the interactive menu and the -Scenario All regression run present scenarios in the same order.
function Get-ScenarioInvokeScriptsSorted {
    param([string]$ScenariosPath)
    return Get-ChildItem $ScenariosPath -Filter "Invoke-*.ps1" | Sort-Object {
        if ($_.BaseName -match 'Scenario(\d+)') { [int]$Matches[1] } else { [int]::MaxValue }
    }, Name
}

# Interactive scenario selection function
function Show-ScenarioMenu {
    # Discover available scenarios in numeric order (see Get-ScenarioInvokeScriptsSorted).
    $scenariosPath = Join-Path $scriptRoot "scenarios"
    $scenarioFiles = Get-ScenarioInvokeScriptsSorted -ScenariosPath $scenariosPath

    if ($scenarioFiles.Count -eq 0) {
        Write-Host "${RED}No scenario scripts found in $scenariosPath${NC}"
        exit 1
    }

    # Build scenario list with descriptions — "All Scenarios" first, then "Pre-Release"
    $scenarios = @(
        @{
            Name = "All"
            Description = "Run every implemented scenario sequentially (full regression)"
            Disabled = $false
            SeparatorAfter = $false
        }
        @{
            Name = "Pre-Release"
            Description = "Runs every implemented scenario sequentially for both Samba AD and OpenLDAP at Medium and Large templates, respectively"
            Disabled = $false
            SeparatorAfter = $true
        }
    )
    foreach ($file in $scenarioFiles) {
        $scenarioName = $file.BaseName -replace '^Invoke-', ''

        # Extract description from the .SYNOPSIS block inside the script's comment-based help.
        # The first `# ...` line in a scenario file is the copyright header, so we explicitly
        # look inside the <# ... #> block for the line following `.SYNOPSIS`.
        $description = ""
        $content = Get-Content $file.FullName -TotalCount 40
        for ($i = 0; $i -lt $content.Count; $i++) {
            if ($content[$i] -match '^\s*\.SYNOPSIS\s*$') {
                # Take the first non-empty line after .SYNOPSIS that isn't another help tag or block terminator
                for ($j = $i + 1; $j -lt $content.Count; $j++) {
                    $line = $content[$j].Trim()
                    if (-not $line) { continue }
                    if ($line -match '^\.[A-Z]+' -or $line -match '^#>') { break }
                    $description = $line
                    break
                }
                break
            }
        }

        if (-not $description) {
            # Default descriptions based on scenario name
            $description = switch -Wildcard ($scenarioName) {
                "*Scenario1*" { "HR to Identity Directory synchronisation" }
                "*Scenario2*" { "Cross-domain synchronisation (APAC ↔ EMEA)" }
                "*Scenario3*" { "Global Address List (GAL) synchronisation" }
                "*Scenario4*" { "Deletion rules and attribute recall" }
                "*Scenario5*" { "Matching rules and join logic" }
                "*Scenario6*" { "Scheduler service end-to-end testing" }
                "*Scenario7*" { "Clear Connected System Objects testing" }
                "*Scenario8*" { "Cross-domain entitlement synchronisation" }
                "*Scenario9*" { "Partition-scoped import run profiles" }
                "*Scenario10*" { "Sync rule scoping behaviour" }
                "*Scenario11*" { "Sync rule scoping criteria evaluation matrix" }
                "*Scenario12*" { "Relative-date inbound scoping (joiner / leaver)" }
                "*Scenario13*" { "Relative-date outbound scoping (staged provisioning)" }
                "*Scenario14*" { "Attribute priority (multi-source winner resolution)" }
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
            SeparatorAfter = $false
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

                # Separator after designated items (e.g. the "special" group above the scenario list)
                if ($scenario.SeparatorAfter) {
                    Write-Host "${GRAY}  $("-" * 60)${NC}"
                    Write-Host ""
                }
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
            Name = "Scale100k50Groups"
            Users = 100000
            Groups = 50
            Description = "100K users"
            Time = "~1 hour"
        }
        @{
            Name = "Scale100k5kGroups"
            Users = 100000
            Groups = 5027
            Description = "100K users, realistic long-tail group shape (OpenLDAP + Scenario 8 only)"
            Time = "~1.5 hours"
        }
        @{
            Name = "Scale200k55Groups"
            Users = 200000
            Groups = 55
            Description = "200K users"
            Time = "~2 hours"
        }
        @{
            Name = "Scale200k10kGroups"
            Users = 200000
            Groups = 9984
            Description = "200K users, long-tail group shape (OpenLDAP + Scenario 8 only)"
            Time = "~3 hours"
        }
        @{
            Name = "Scale500k65Groups"
            Users = 500000
            Groups = 65
            Description = "500K users"
            Time = "~3 hours"
        }
        @{
            Name = "Scale500k25kGroups"
            Users = 500000
            Groups = 24997
            Description = "500K users, long-tail group shape (OpenLDAP + Scenario 8 only)"
            Time = "~6 hours"
        }
        @{
            Name = "Scale750k70Groups"
            Users = 750000
            Groups = 70
            Description = "750K users"
            Time = "~4 hours"
        }
        @{
            Name = "Scale750k40kGroups"
            Users = 750000
            Groups = 40011
            Description = "750K users, long-tail group shape (OpenLDAP + Scenario 8 only)"
            Time = "~9 hours"
        }
        @{
            Name = "Scale1m80Groups"
            Users = 1000000
            Groups = 80
            Description = "1M users, stress testing"
            Time = "~6 hours"
        }
        @{
            Name = "Scale1m60kGroups"
            Users = 1000000
            Groups = 60073
            Description = "1M users, long-tail group shape (OpenLDAP + Scenario 8 only)"
            Time = "~12 hours"
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

# Interactive directory type selection function
function Show-DirectoryTypeMenu {
    $directoryTypes = @(
        @{
            Name = "SambaAD"
            Description = "Samba Active Directory (default)"
            Details = "LDAPS on port 636, objectGUID, AD schema"
        }
        @{
            Name = "OpenLDAP"
            Description = "OpenLDAP with multi-suffix partitions"
            Details = "LDAP on port 1389, entryUUID, RFC 4512 schema"
        }
        @{
            Name = "All"
            Description = "Both directory types (full regression)"
            Details = "Runs all scenarios against SambaAD first, then OpenLDAP"
        }
    )

    $selectedIndex = 0
    $exitMenu = $false

    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Directory Type Selection${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host ""

            for ($i = 0; $i -lt $directoryTypes.Count; $i++) {
                $dt = $directoryTypes[$i]

                if ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($dt.Name)${NC} ${GRAY}— $($dt.Description)${NC}"
                    Write-Host "${GRAY}  $($dt.Details)${NC}"
                }
                else {
                    Write-Host "  $($dt.Name) ${GRAY}— $($dt.Description)${NC}"
                    Write-Host "${GRAY}  $($dt.Details)${NC}"
                }
                Write-Host ""
            }

            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { $selectedIndex = [Math]::Max(0, $selectedIndex - 1) }
                40 { $selectedIndex = [Math]::Min($directoryTypes.Count - 1, $selectedIndex + 1) }
                13 { $exitMenu = $true }
                27 {
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        [Console]::CursorVisible = $true
    }

    Clear-Host
    return $directoryTypes[$selectedIndex].Name
}

# Interactive log level selection function
function Show-LogLevelMenu {
    # Read current value from .env
    $envFilePath = Join-Path $repoRoot ".env"
    $currentLevel = "Debug"
    $envContent = Get-Content $envFilePath -Raw
    if ($envContent -match "(?m)^JIM_LOG_LEVEL=(.+)$") {
        $currentLevel = $Matches[1].Trim()
    }

    $logLevels = @(
        @{ Name = "Verbose"; Description = "All messages including detailed tracing" }
        @{ Name = "Debug"; Description = "Diagnostic messages for development (default)" }
        @{ Name = "Information"; Description = "General operational messages" }
        @{ Name = "Warning"; Description = "Potential issues and unexpected situations" }
        @{ Name = "Error"; Description = "Errors that prevent operations from completing" }
        @{ Name = "Fatal"; Description = "Critical failures only" }
    )

    # Default selection to current .env value
    $selectedIndex = 0
    for ($i = 0; $i -lt $logLevels.Count; $i++) {
        if ($logLevels[$i].Name -eq $currentLevel) {
            $selectedIndex = $i
            break
        }
    }
    $exitMenu = $false

    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Log Level Selection${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host "${GRAY}Current .env value: ${CYAN}$currentLevel${NC}"
            Write-Host ""

            for ($i = 0; $i -lt $logLevels.Count; $i++) {
                $level = $logLevels[$i]

                if ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($level.Name)${NC}"
                    Write-Host "${GRAY}  $($level.Description)${NC}"
                }
                else {
                    Write-Host "  $($level.Name)"
                    Write-Host "${GRAY}  $($level.Description)${NC}"
                }
                Write-Host ""
            }

            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { $selectedIndex = [Math]::Max(0, $selectedIndex - 1) }
                40 { $selectedIndex = [Math]::Min($logLevels.Count - 1, $selectedIndex + 1) }
                13 { $exitMenu = $true }
                27 {
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        [Console]::CursorVisible = $true
    }

    Clear-Host
    return $logLevels[$selectedIndex].Name
}

# Interactive change tracking selection function
function Show-ChangeTrackingMenu {
    $options = @(
        @{
            Name = "Enabled"
            Description = "Record CSO and MVO changes (default)"
            Details = "Full audit trail — recommended for most tests"
        }
        @{
            Name = "Disabled"
            Description = "Skip change tracking for CSO and MVO"
            Details = "Reduces database writes — recommended for large-scale tests"
        }
    )

    $selectedIndex = 0
    $exitMenu = $false

    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Change Tracking${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host ""

            for ($i = 0; $i -lt $options.Count; $i++) {
                $opt = $options[$i]

                if ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($opt.Name)${NC} ${GRAY}— $($opt.Description)${NC}"
                    Write-Host "${GRAY}  $($opt.Details)${NC}"
                }
                else {
                    Write-Host "  $($opt.Name) ${GRAY}— $($opt.Description)${NC}"
                    Write-Host "${GRAY}  $($opt.Details)${NC}"
                }
                Write-Host ""
            }

            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { $selectedIndex = [Math]::Max(0, $selectedIndex - 1) }
                40 { $selectedIndex = [Math]::Min($options.Count - 1, $selectedIndex + 1) }
                13 { $exitMenu = $true }
                27 {
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        [Console]::CursorVisible = $true
    }

    Clear-Host
    # Return $true if Disabled was selected (index 1)
    return ($selectedIndex -eq 1)
}

function Show-Scenario11CoverageMenu {
    # Scenario 11 (Scoping Criteria Matrix) coverage tier picker.
    # Returns one of 'Quick', 'Default', or 'Exhaustive'.
    $options = @(
        @{
            Name = "Default (Full)"
            Description = "Full matrix at default coverage"
            Details = "Every applicable (operator x type) pair plus 3 group structures (~41 cells, < 5 min)"
            Value = "Default"
        }
        @{
            Name = "Quick"
            Description = "One cell per operator"
            Details = "Fast PR-feedback subset, no group nesting or CS variants (~12 cells, < 90s)"
            Value = "Quick"
        }
        @{
            Name = "Exhaustive"
            Description = "Full Cartesian on (operator x type x group)"
            Details = "Pre-release / post-evaluator-refactor verification (~152 cells, < 10 min)"
            Value = "Exhaustive"
        }
    )

    $selectedIndex = 0
    $exitMenu = $false

    [Console]::CursorVisible = $false

    try {
        while (-not $exitMenu) {
            Clear-Host

            Write-Host ""
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host "${CYAN}  JIM Integration Test - Scenario 11 Coverage Tier${NC}"
            Write-Host "${CYAN}$("=" * 70)${NC}"
            Write-Host ""
            Write-Host "${GRAY}Use ↑/↓ arrow keys to navigate, Enter to select, Esc to exit${NC}"
            Write-Host ""

            for ($i = 0; $i -lt $options.Count; $i++) {
                $opt = $options[$i]

                if ($i -eq $selectedIndex) {
                    Write-Host "${GREEN}► $($opt.Name)${NC} ${GRAY}— $($opt.Description)${NC}"
                    Write-Host "${GRAY}  $($opt.Details)${NC}"
                }
                else {
                    Write-Host "  $($opt.Name) ${GRAY}— $($opt.Description)${NC}"
                    Write-Host "${GRAY}  $($opt.Details)${NC}"
                }
                Write-Host ""
            }

            $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

            switch ($key.VirtualKeyCode) {
                38 { $selectedIndex = [Math]::Max(0, $selectedIndex - 1) }
                40 { $selectedIndex = [Math]::Min($options.Count - 1, $selectedIndex + 1) }
                13 { $exitMenu = $true }
                27 {
                    Write-Host ""
                    Write-Host "${YELLOW}Cancelled by user${NC}"
                    [Console]::CursorVisible = $true
                    exit 0
                }
            }
        }
    }
    finally {
        [Console]::CursorVisible = $true
    }

    Clear-Host
    return $options[$selectedIndex].Value
}

# Track if user explicitly set Template parameter
$TemplateWasExplicitlySet = $PSBoundParameters.ContainsKey('Template')
$DirectoryTypeWasExplicitlySet = $PSBoundParameters.ContainsKey('DirectoryType')
$LogLevelWasExplicitlySet = $PSBoundParameters.ContainsKey('LogLevel')
$ChangeTrackingWasExplicitlySet = $PSBoundParameters.ContainsKey('DisableChangeTracking')
$Scenario11CoverageWasExplicitlySet = $Quick -or $Exhaustive

# Scenarios that provision their own fixed test data and don't use the Template parameter
# for data sizing. These scenarios accept Template but it has no effect on test execution.
$templateIrrelevantScenarios = @(
    "*Scenario2*",   # Cross-Domain Sync - uses fixed test users (crossdomain.test1, etc.)
    "*Scenario3*",   # GAL Sync - not yet implemented
    "*Scenario4*",   # Deletion Rules - provisions individual test users, ignores template
    "*Scenario6*",   # Scheduler Service - tests scheduler functionality, no data template needed
    "*Scenario10*",  # Sync Rule Scoping - template-independent, only a handful of explicit test users
    "*Scenario11*",  # Scoping Criteria Matrix - bespoke deterministic seed, template informational
    "*Scenario12*",  # Relative-Date Scoping - fixed test users positioned relative to "now"
    "*Scenario13*",  # Relative-Date Outbound Scoping - fixed test users positioned relative to "now"
    "*Scenario14*"   # Attribute Priority - fixed six-user dataset per suffix, no template scaling
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

# -PreRelease is shorthand for: -Scenario All -DirectoryType All -TemplateSambaAD Medium -TemplateOpenLDAP Large
if ($PreRelease) {
    $Scenario               = "All"
    $DirectoryType          = "All"
    $TemplateSambaAD        = "Medium"
    $TemplateOpenLDAP       = "Large"
    $DirectoryTypeWasExplicitlySet = $true
    $TemplateWasExplicitlySet      = $true
}

# If no scenario specified, show interactive menu
if (-not $Scenario) {
    $Scenario = Show-ScenarioMenu

    # "Pre-Release" is a special menu entry that expands to all-scenarios, both directory
    # types, with Samba AD at Medium and OpenLDAP at Large. It bypasses the Template
    # and DirectoryType sub-menus since those are fixed by the Pre-Release preset.
    if ($Scenario -eq "Pre-Release") {
        $Scenario                      = "All"
        $DirectoryType                 = "All"
        $TemplateSambaAD               = "Medium"
        $TemplateOpenLDAP              = "Large"
        $DirectoryTypeWasExplicitlySet = $true
        $TemplateWasExplicitlySet      = $true
    }

    # Show template menu only if Template wasn't explicitly provided AND the scenario uses it
    if (-not $TemplateWasExplicitlySet) {
        if (Test-TemplateRelevant -ScenarioName $Scenario) {
            $Template = Show-TemplateMenu
        }
        else {
            $Template = "Nano"
        }
    }

    # Show directory type menu only if not explicitly provided. Scenario 14 is OpenLDAP
    # only (two-suffix topology), so don't offer a choice; go straight to OpenLDAP.
    if (-not $DirectoryTypeWasExplicitlySet) {
        if ($Scenario -like "*Scenario14*") {
            $DirectoryType = "OpenLDAP"
        }
        else {
            $DirectoryType = Show-DirectoryTypeMenu
        }
        # Re-resolve directory config with the selected type (skip for "All" — handled below)
        if ($DirectoryType -ne "All") {
            $script:DirectoryConfig = Get-DirectoryConfig -DirectoryType $DirectoryType
        }
    }

    # Show log level menu only if not explicitly provided
    if (-not $LogLevelWasExplicitlySet) {
        $LogLevel = Show-LogLevelMenu
    }

    # Show change tracking menu only if not explicitly provided
    if (-not $ChangeTrackingWasExplicitlySet) {
        $DisableChangeTracking = Show-ChangeTrackingMenu
    }

    # Scenario 11 coverage tier prompt - only shown when running Scenario 11 and
    # the user didn't already pass -Quick or -Exhaustive on the command line.
    if ($Scenario -like "*Scenario11*" -and -not $Scenario11CoverageWasExplicitlySet) {
        $tierChoice = Show-Scenario11CoverageMenu
        switch ($tierChoice) {
            'Quick'      { $Quick = $true }
            'Exhaustive' { $Exhaustive = $true }
            'Default'    { } # neither switch set
        }
    }
}

# ---------------------------------------------------------------------------
# Scenario 14 directory coercion (Attribute Priority is OpenLDAP only)
# ---------------------------------------------------------------------------
# Scenario 14 depends on two LDAP suffixes hosted on a single OpenLDAP container
# (docker/openldap/scripts/01-add-second-suffix.sh); Samba AD has no equivalent
# multi-suffix mechanism. This runs after scenario/directory resolution (whether the
# values came from parameters or the interactive menu) and before the build, so the
# constraint is enforced whichever way they were chosen. If -DirectoryType SambaAD was
# explicitly passed, respect the explicit intent and reject; otherwise coerce to
# OpenLDAP. -DirectoryType All is handled by its own block below.
if ($Scenario -like "*Scenario14*" -and $DirectoryType -eq "SambaAD") {
    if ($DirectoryTypeWasExplicitlySet) {
        throw "Scenario 14 (Attribute Priority) requires two LDAP suffixes on a single OpenLDAP container and is OpenLDAP only. Rejected -DirectoryType SambaAD. Use -DirectoryType OpenLDAP."
    }
    Write-Host "${YELLOW}Scenario 14 (Attribute Priority) is OpenLDAP only; using -DirectoryType OpenLDAP.${NC}"
    $DirectoryType = "OpenLDAP"
    $script:DirectoryConfig = Get-DirectoryConfig -DirectoryType "OpenLDAP"
}

# ---------------------------------------------------------------------------
# Handle "-DirectoryType All": run the suite for each directory type
# ---------------------------------------------------------------------------

if ($DirectoryType -eq "All") {
    $selfScript = Join-Path $PSScriptRoot "Run-IntegrationTests.ps1"
    $directoryTypesToRun = @("SambaAD", "OpenLDAP")

    # Scenario 14 (Attribute Priority) is OpenLDAP only (two-suffix topology); run just the
    # OpenLDAP leg rather than failing the Samba AD leg.
    if ($Scenario -like "*Scenario14*") {
        Write-Host "${YELLOW}Scenario 14 is OpenLDAP only; skipping the Samba AD leg.${NC}"
        $directoryTypesToRun = @("OpenLDAP")
    }

    # Build common parameters to pass through (excluding DirectoryType and Template)
    $passThruParams = @{}
    if ($Scenario)   { $passThruParams.Scenario = $Scenario }
    if ($Step -ne "All") { $passThruParams.Step = $Step }
    if ($PSBoundParameters.ContainsKey('ExportConcurrency'))    { $passThruParams.ExportConcurrency = $ExportConcurrency }
    if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $passThruParams.MaxExportParallelism = $MaxExportParallelism }
    if ($TimeoutSeconds -ne 180)                                { $passThruParams.TimeoutSeconds = $TimeoutSeconds }
    if ($CaptureMetrics)                                        { $passThruParams.CaptureMetrics = $true }
    if ($IgnoreSnapshots)                                       { $passThruParams.IgnoreSnapshots = $true }
    if ($LogLevel)                                              { $passThruParams.LogLevel = $LogLevel }
    if ($DisableChangeTracking)                                 { $passThruParams.DisableChangeTracking = $true }
    if ($ContinueOnFailure)                                     { $passThruParams.ContinueOnFailure = $true }

    # Resolve per-directory-type templates. -TemplateSambaAD/-TemplateOpenLDAP
    # override the base -Template for the respective directory type.
    $templateForSambaAD  = if ($TemplateSambaAD)  { $TemplateSambaAD }  else { $Template }
    $templateForOpenLDAP = if ($TemplateOpenLDAP) { $TemplateOpenLDAP } else { $Template }

    $allStart = Get-Date
    $allResults = @()
    $anyFailed = $false

    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  JIM Integration Tests — All Directory Types${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host ""
    Write-Host "${GRAY}Scenario:  ${CYAN}$($Scenario ?? 'All')${NC}"
    if ($templateForSambaAD -eq $templateForOpenLDAP) {
        Write-Host "${GRAY}Template:  ${CYAN}$templateForSambaAD${NC}"
    } else {
        Write-Host "${GRAY}Template:  ${CYAN}SambaAD=$templateForSambaAD, OpenLDAP=$templateForOpenLDAP${NC}"
    }
    Write-Host "${GRAY}Directory: ${CYAN}SambaAD → OpenLDAP${NC}"
    Write-Host ""

    foreach ($dt in $directoryTypesToRun) {
        $dtStart = Get-Date

        # Select the template for this directory type
        $dtTemplate = if ($dt -eq "SambaAD") { $templateForSambaAD } else { $templateForOpenLDAP }

        Write-Host ""
        Write-Host "${CYAN}$("=" * 65)${NC}"
        Write-Host "${CYAN}  Directory Type: $dt (Template: $dtTemplate)${NC}"
        Write-Host "${CYAN}$("=" * 65)${NC}"
        Write-Host ""

        & $selfScript @passThruParams -DirectoryType $dt -Template $dtTemplate
        $dtExitCode = $LASTEXITCODE
        $dtDuration = (Get-Date) - $dtStart

        $dtPassed = ($dtExitCode -eq 0)
        $dtStatus = if ($dtPassed) { "${GREEN}PASSED${NC}" } else { "${RED}FAILED (exit code $dtExitCode)${NC}" }

        Write-Host ""
        Write-Host "  $dt Result: $dtStatus  Duration: $($dtDuration.ToString('hh\:mm\:ss'))"

        $allResults += @{
            DirectoryType   = $dt
            Success         = $dtPassed
            ExitCode        = $dtExitCode
            Duration        = $dtDuration.ToString('hh\:mm\:ss')
            DurationSeconds = $dtDuration.TotalSeconds
        }
        if (-not $dtPassed) {
            $anyFailed = $true

            # Fail-fast: don't run the next directory type when one has already failed.
            if (-not $ContinueOnFailure) {
                Write-Host ""
                Write-Host "${RED}Aborting remaining directory types after '$dt' failure (fail-fast default).${NC}"
                Write-Host "${GRAY}Pass -ContinueOnFailure to run all directory types regardless.${NC}"
                Write-Host ""
                break
            }
        }
    }

    # Print summary. Wrap with @(...) so Where-Object returning $null (zero matches)
    # or a single hashtable (unwrapped on exactly one match) doesn't trip Set-StrictMode.
    $allDuration = (Get-Date) - $allStart
    $passCount = @($allResults | Where-Object { $_.Success }).Count

    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  All Directory Types — Summary${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host ""

    foreach ($r in $allResults) {
        $icon = if ($r.Success) { "${GREEN}PASS${NC}" } else { "${RED}FAIL${NC}" }
        Write-Host ("  [{0}]  {1,-20} {2}" -f $icon, $r.DirectoryType, $r.Duration)
    }

    Write-Host ""
    Write-Host "${CYAN}Total Duration: ${NC}$($allDuration.ToString('hh\:mm\:ss'))"
    Write-Host "${CYAN}Passed: ${NC}$passCount / $($allResults.Count)    ${CYAN}Failed: ${NC}$($allResults.Count - $passCount) / $($allResults.Count)"
    Write-Host ""

    # Re-run Command
    Write-Host "${CYAN}Re-run Command:${NC}"
    Write-Host ""
    $rerunParts = @("jim-reset && pwsh ./test/integration/Run-IntegrationTests.ps1")
    $rerunParts += "-Scenario `"$($Scenario ?? 'All')`""
    $rerunParts += "-Template $Template"
    if ($Step -ne "All") { $rerunParts += "-Step $Step" }
    $rerunParts += "-DirectoryType All"
    if ($LogLevel) { $rerunParts += "-LogLevel $LogLevel" }
    if ($DisableChangeTracking) { $rerunParts += "-DisableChangeTracking" }
    if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $rerunParts += "-ExportConcurrency $ExportConcurrency" }
    if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $rerunParts += "-MaxExportParallelism $MaxExportParallelism" }
    if ($TimeoutSeconds -ne 180) { $rerunParts += "-TimeoutSeconds $TimeoutSeconds" }
    if ($CaptureMetrics) { $rerunParts += "-CaptureMetrics" }
    if ($IgnoreSnapshots) { $rerunParts += "-IgnoreSnapshots" }
    Write-Host "  $($rerunParts -join ' ')"
    Write-Host ""

    if ($anyFailed) {
        Write-Host "${RED}One or more directory types failed.${NC}"
        exit 1
    }
    else {
        Write-Host "${GREEN}All directory types passed.${NC}"
        exit 0
    }
}

# ---------------------------------------------------------------------------
# Handle "-Scenario All": run every implemented scenario sequentially
# ---------------------------------------------------------------------------

# Lightweight reset: stop JIM containers, remove JIM DB volume, clean Samba AD
# OUs, generate a new API key, and restart JIM — without rebuilding images.
function Reset-JIMForNextScenario {
    param(
        [string]$RepoRoot,
        [string]$ScriptRoot,
        [int]$TimeoutSeconds = 180
    )

    Write-Host ""
    Write-Host "${BLUE}--- Lightweight Reset ---${NC}"

    # 0. Empty the shared File Connector volume in place. See Clear-ConnectorFilesVolume
    # for the full rationale; briefly, `docker compose down -v` below can't remove
    # jim-connector-files-volume while Samba AD / OpenLDAP keep it pinned.
    Clear-ConnectorFilesVolume

    # 1. Stop JIM containers (keep Samba AD running)
    Write-Host "${GRAY}  Stopping JIM containers...${NC}"
    $downOutput = docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down -v 2>&1
    # Surface any "Resource is still in use" lines. Seeing these here means a container
    # outside the JIM compose file is mounting one of the JIM volumes (typically
    # jim-connector-files-volume via Samba AD / OpenLDAP) — investigate before the
    # stale contents cause a later scenario to fail.
    $stuckVolumes = $downOutput | Where-Object { $_ -match 'Resource is still in use' }
    if ($stuckVolumes) {
        Write-Host "${YELLOW}  Warning: one or more JIM volumes could not be removed during reset:${NC}"
        foreach ($line in $stuckVolumes) {
            Write-Host "${YELLOW}    $line${NC}"
        }
        Write-Host "${YELLOW}    (contents were emptied in step 0, so the next scenario should still start clean)${NC}"
    }

    # 2. Remove JIM database volume (ensures clean schema + data)
    Write-Host "${GRAY}  Removing JIM database volume...${NC}"
    docker volume rm jim-db-volume 2>&1 | Out-Null

    # 3. Clean Samba AD test data (delete OUs with --force-subtree-delete; much faster than container restart)
    Write-Host "${GRAY}  Cleaning Samba AD test data...${NC}"

    # Primary (panoply.local) — used by Scenarios 1, 4, 5, 6
    foreach ($ou in @("OU=Corp,DC=panoply,DC=local", "OU=TestUsers,DC=panoply,DC=local", "OU=TestGroups,DC=panoply,DC=local")) {
        docker exec samba-ad-primary samba-tool ou delete $ou --force-subtree-delete 2>&1 | Out-Null
    }
    # Legacy department OUs from Populate-SambaAD.ps1
    foreach ($dept in @("Marketing", "Operations", "Finance", "Sales", "Human Resources", "Procurement", "Information Technology", "Research & Development", "Executive", "Legal", "Facilities", "Catering")) {
        docker exec samba-ad-primary samba-tool ou delete "OU=$dept,DC=panoply,DC=local" --force-subtree-delete 2>&1 | Out-Null
    }

    # Source (resurgam.local) — used by Scenarios 2, 8
    $sourceRunning = docker ps --filter "name=samba-ad-source" --format '{{.Names}}' 2>$null
    if ($sourceRunning) {
        foreach ($ou in @("OU=TestUsers,DC=resurgam,DC=local", "OU=Corp,DC=resurgam,DC=local")) {
            docker exec samba-ad-source samba-tool ou delete $ou --force-subtree-delete 2>&1 | Out-Null
        }
    }

    # Target (gentian.local) — used by Scenarios 2, 8
    $targetRunning = docker ps --filter "name=samba-ad-target" --format '{{.Names}}' 2>$null
    if ($targetRunning) {
        foreach ($ou in @("OU=TestUsers,DC=gentian,DC=local", "OU=CorpManaged,DC=gentian,DC=local")) {
            docker exec samba-ad-target samba-tool ou delete $ou --force-subtree-delete 2>&1 | Out-Null
        }
    }

    # 3b. Clean OpenLDAP test data (yellowstone.local / glitterband.local), used by the OpenLDAP directory type.
    # Unlike the JIM database (volume removed above) and Samba AD (OUs deleted above), the OpenLDAP directory has
    # no other cross-scenario reset: it is a long-lived container whose data volume persists between scenarios.
    # Without this, each OpenLDAP scenario imports the accumulated objects of every earlier OpenLDAP scenario;
    # that is why the "six-user" Scenario14-AttributePriority actually synchronised ~50,000 stale objects and hit
    # the Metaverse Object update concurrency failure. Delete the People/Groups subtrees (all users and groups) and
    # recreate the empty base OUs the next scenario's populate expects, symmetric with the Samba AD OU cleanup above.
    $openLdapRunning = docker ps --filter "name=openldap-primary" --format '{{.Names}}' 2>$null
    if ($openLdapRunning) {
        Write-Host "${GRAY}  Cleaning OpenLDAP test data...${NC}"
        $openLdapPurge = @'
uri="ldap://localhost:1389"
pw="Test@123!"
for suffix in dc=yellowstone,dc=local dc=glitterband,dc=local; do
  admin="cn=admin,$suffix"
  ldapdelete -r -x -H "$uri" -D "$admin" -w "$pw" "ou=People,$suffix" "ou=Groups,$suffix" >/dev/null 2>&1 || true
  ldapadd -x -H "$uri" -D "$admin" -w "$pw" >/dev/null 2>&1 <<LDIF || true
dn: ou=People,$suffix
objectClass: organizationalUnit
ou: People

dn: ou=Groups,$suffix
objectClass: organizationalUnit
ou: Groups
LDIF
done
'@
        # This .ps1 uses CRLF line endings, so the here-string above carries a trailing CR on
        # every line. Passed to bash, each CR becomes part of the command: the LDAP URI parses
        # as "ldap://localhost:1389\r" (rejected), and the heredoc terminator "LDIF\r" never
        # matches "LDIF". Every ldapdelete/ldapadd then fails, '|| true' and Out-Null swallow the
        # errors, and the purge silently no-ops, letting OpenLDAP pollution accumulate across
        # scenarios. Strip CR so bash receives clean LF-terminated lines.
        docker exec openldap-primary bash -c ($openLdapPurge -replace "`r", "") 2>&1 | Out-Null
    }

    # 4. Generate new API key and update .env
    Write-Host "${GRAY}  Generating new API key...${NC}"
    $randomBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($randomBytes)
    $randomString = [Convert]::ToBase64String($randomBytes).Replace("+", "").Replace("/", "").Replace("=", "")
    $newApiKey = "jim_ak_$randomString"

    $envFilePath = Join-Path $RepoRoot ".env"
    $envContent = Get-Content $envFilePath -Raw
    if ($null -eq $envContent) { $envContent = "" }
    if ($envContent -match "JIM_INFRASTRUCTURE_API_KEY=") {
        # Strip any leading comment marker (# ) so a commented-out line becomes active
        $envContent = $envContent -replace "(?m)^#\s*JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$newApiKey"
        $envContent = $envContent -replace "(?m)^JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$newApiKey"
    } else {
        $newLine = if ($envContent.EndsWith("`n")) { "" } else { "`n" }
        $envContent = $envContent + $newLine + "JIM_INFRASTRUCTURE_API_KEY=$newApiKey`n"
    }
    $envContent | Set-Content $envFilePath -NoNewline

    $keyFilePath = Join-Path $ScriptRoot ".api-key"
    $newApiKey | Out-File -FilePath $keyFilePath -NoNewline -Encoding UTF8

    # 5. Pre-create the worker log bind-mount directory so Docker doesn't create it as root
    # (see utils/Initialize-WorkerLogDirectories.ps1 and docker-compose.override.yml).
    Initialize-WorkerLogDirectories -LogDirectory (Join-Path $ScriptRoot "results" "logs")

    # 6. Restart JIM containers
    Write-Host "${GRAY}  Starting JIM containers...${NC}"
    docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d 2>&1 | Out-Null

    # 7. Wait for JIM API health check
    Write-Host "${GRAY}  Waiting for JIM API...${NC}"
    $jimApiReady = $false
    $jimApiElapsed = 0
    $jimApiUrl = "http://localhost:5200/api/v1/health"
    while (-not $jimApiReady -and $jimApiElapsed -lt $TimeoutSeconds) {
        try {
            $healthResponse = Invoke-WebRequest -Uri $jimApiUrl -Method GET -TimeoutSec 5 -ErrorAction SilentlyContinue
            if ($healthResponse.StatusCode -eq 200) { $jimApiReady = $true }
        } catch { }
        if (-not $jimApiReady) {
            Start-Sleep -Seconds 3
            $jimApiElapsed += 3
        }
    }

    if (-not $jimApiReady) {
        Write-Host "  ${RED}JIM API did not become ready within ${TimeoutSeconds}s${NC}"
        $script:ResetSuccess = $false
        return
    }

    Write-Host "  ${GREEN}Lightweight reset complete${NC}"
    Write-Host ""
    $script:ResetSuccess = $true
}

if ($Scenario -eq "All") {

    # Incompatible with -SetupOnly
    if ($SetupOnly) {
        Write-Host "${RED}ERROR: -SetupOnly cannot be combined with -Scenario All${NC}"
        exit 1
    }

    # Discover scenario scripts in numeric order (same ordering as the interactive menu) and filter
    # out stubs.
    $scenariosPath = Join-Path $scriptRoot "scenarios"
    $scenarioFiles = Get-ScenarioInvokeScriptsSorted -ScenariosPath $scenariosPath
    $implementedScenarios = @()

    foreach ($file in $scenarioFiles) {
        $fileContent = Get-Content $file.FullName -Raw
        if ($fileContent -match 'Write-Host\s+"[\s]*NOT YET IMPLEMENTED[\s]*"') {
            continue
        }
        $implementedScenarios += ($file.BaseName -replace '^Invoke-', '')
    }

    # Scenario 14 (Attribute Priority) is OpenLDAP only (two-suffix topology); skip it on a
    # Samba AD sweep rather than recording a guaranteed failure.
    if ($DirectoryType -eq "SambaAD") {
        $openLdapOnly = @($implementedScenarios | Where-Object { $_ -like "*Scenario14*" })
        if ($openLdapOnly.Count -gt 0) {
            Write-Host "${YELLOW}Skipping OpenLDAP-only scenario(s) on Samba AD: $($openLdapOnly -join ', ')${NC}"
            $implementedScenarios = @($implementedScenarios | Where-Object { $_ -notlike "*Scenario14*" })
        }
    }

    if ($implementedScenarios.Count -eq 0) {
        Write-Host "${RED}ERROR: No implemented scenarios found in $scenariosPath${NC}"
        exit 1
    }

    # Display plan
    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  JIM Integration Test Runner — Full Regression${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host ""
    Write-Host "${GRAY}Template:  ${CYAN}$Template${NC}  ${GRAY}(used by template-relevant scenarios; others use Nano)${NC}"
    Write-Host "${GRAY}Directory: ${CYAN}$DirectoryType${NC}"
    Write-Host ""
    Write-Host "${GRAY}Scenarios to run ($($implementedScenarios.Count)):${NC}"
    foreach ($s in $implementedScenarios) {
        $templateNote = if (Test-TemplateRelevant -ScenarioName $s) { $Template } else { "Nano (fixed data)" }
        Write-Host "  ${CYAN}$s${NC}  ${GRAY}[$templateNote]${NC}"
    }
    Write-Host ""

    # Build common parameters — Template is overridden per-scenario inside Invoke-SingleScenario
    $commonParams = @{ DirectoryType = $DirectoryType }
    if ($Template)   { $commonParams.Template = $Template }
    if ($Step)       { $commonParams.Step = $Step }
    if ($PSBoundParameters.ContainsKey('ExportConcurrency'))    { $commonParams.ExportConcurrency = $ExportConcurrency }
    if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $commonParams.MaxExportParallelism = $MaxExportParallelism }
    if ($TimeoutSeconds -ne 180)                                { $commonParams.TimeoutSeconds = $TimeoutSeconds }
    if ($CaptureMetrics)                                        { $commonParams.CaptureMetrics = $true }
    if ($LogLevel)                                              { $commonParams.LogLevel = $LogLevel }
    if ($DisableChangeTracking)                                 { $commonParams.DisableChangeTracking = $true }

    # Run each scenario, collecting results
    $results = @()
    $allStart = Get-Date
    $selfScript = Join-Path $scriptRoot "Run-IntegrationTests.ps1"
    $anyFailed = $false
    $regressionTimings = @{}

    for ($i = 0; $i -lt $implementedScenarios.Count; $i++) {
        $scenarioName = $implementedScenarios[$i]

        # First scenario: full build + reset. Subsequent scenarios: lightweight reset + skip build.
        if ($i -gt 0) {
            $resetStart = Get-Date
            Reset-JIMForNextScenario -RepoRoot $repoRoot -ScriptRoot $scriptRoot -TimeoutSeconds $TimeoutSeconds
            $regressionTimings["Reset before $scenarioName"] = ((Get-Date) - $resetStart).TotalSeconds
            if (-not $script:ResetSuccess) {
                Write-Host "${RED}Lightweight reset failed before $scenarioName — skipping${NC}"
                $results += @{
                    Name            = $scenarioName
                    Success         = $false
                    ExitCode        = 1
                    Duration        = "00:00:00"
                    DurationSeconds = 0
                }
                $anyFailed = $true
                continue
            }
        }

        $index = $i + 1
        Write-Host ""
        Write-Host "${CYAN}$("=" * 65)${NC}"
        Write-Host "${CYAN}  [$index/$($implementedScenarios.Count)] $scenarioName${NC}"
        Write-Host "${CYAN}$("=" * 65)${NC}"
        Write-Host ""

        $scenarioStart = Get-Date

        # Build per-scenario params — override Template to Nano for template-irrelevant scenarios
        $scenarioParams = @{ Scenario = $scenarioName } + $commonParams
        if (-not (Test-TemplateRelevant -ScenarioName $scenarioName)) {
            $scenarioParams.Template = "Nano"
        }
        if ($i -gt 0) {
            $scenarioParams.SkipBuild = $true
        }

        $scenarioError = $null
        try {
            & $selfScript @scenarioParams
            $exitCode = $LASTEXITCODE
        }
        catch {
            # The child invocation threw an unhandled exception. Record the failure
            # in $results so the regression summary below still runs, rather than
            # letting the exception propagate and kill the whole loop.
            $scenarioError = $_
            $exitCode = 1
            Write-Host ""
            Write-Host "${RED}  ✗ Scenario invocation raised an exception:${NC}"
            Write-Host "${RED}    $($_.Exception.Message)${NC}"
            Write-Host ""
        }
        $scenarioDuration = (Get-Date) - $scenarioStart

        $passed = ($exitCode -eq 0)
        $status = if ($passed) { "${GREEN}PASSED${NC}" } else { "${RED}FAILED (exit code $exitCode)${NC}" }
        Write-Host ""
        Write-Host "  Result: $status  Duration: $($scenarioDuration.ToString('hh\:mm\:ss'))"
        Write-Host ""

        $results += @{
            Name            = $scenarioName
            Success         = $passed
            ExitCode        = $exitCode
            Duration        = $scenarioDuration.ToString('hh\:mm\:ss')
            DurationSeconds = $scenarioDuration.TotalSeconds
            Error           = if ($scenarioError) { $scenarioError.Exception.Message } else { $null }
            Skipped         = $false
        }
        $regressionTimings[$scenarioName] = $scenarioDuration.TotalSeconds
        if (-not $passed) {
            $anyFailed = $true

            # Fail-fast (default): abort the regression the moment one scenario fails.
            # Record the remaining scenarios as skipped so the summary is honest about
            # what ran vs what was intentionally not run. Pass -ContinueOnFailure to
            # run every scenario regardless, which is useful when diagnosing whether
            # a regression is localised or widespread.
            if (-not $ContinueOnFailure) {
                Write-Host ""
                Write-Host "${RED}Aborting regression after failure of '$scenarioName' (fail-fast default).${NC}"
                Write-Host "${GRAY}Pass -ContinueOnFailure to run remaining scenarios anyway.${NC}"
                Write-Host ""

                for ($j = $i + 1; $j -lt $implementedScenarios.Count; $j++) {
                    $skippedName = $implementedScenarios[$j]
                    $results += @{
                        Name            = $skippedName
                        Success         = $false
                        ExitCode        = $null
                        Duration        = "00:00:00"
                        DurationSeconds = 0
                        Error           = $null
                        Skipped         = $true
                    }
                }
                break
            }
        }

    }

    # Calculate totals. Wrap the Where-Object and the outer array with @(...) so
    # we always get an array: under Set-StrictMode, calling .Count on $null (zero
    # matches) or on a single hashtable (exactly one match, unwrapped by Where-Object)
    # either throws or returns the wrong value.
    $allDuration = (Get-Date) - $allStart
    $passCount = @($results | Where-Object { $_.Success }).Count
    $skipCount = @($results | Where-Object { $_.Skipped }).Count
    $failCount = @($results).Count - $passCount - $skipCount

    # Print summary
    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  Full Regression — Summary${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host ""

    foreach ($r in $results) {
        $icon = if ($r.Skipped) {
            "${YELLOW}SKIP${NC}"
        } elseif ($r.Success) {
            "${GREEN}PASS${NC}"
        } else {
            "${RED}FAIL${NC}"
        }
        Write-Host ("  [{0}]  {1,-50} {2}" -f $icon, $r.Name, $r.Duration)
    }

    Write-Host ""
    Write-Host "${CYAN}Total Duration: ${NC}$($allDuration.ToString('hh\:mm\:ss'))"
    $totalCount = @($results).Count
    if ($skipCount -gt 0) {
        Write-Host "${CYAN}Passed: ${NC}$passCount / $totalCount    ${CYAN}Failed: ${NC}$failCount / $totalCount    ${CYAN}Skipped (fail-fast): ${NC}$skipCount / $totalCount"
    }
    else {
        Write-Host "${CYAN}Passed: ${NC}$passCount / $totalCount    ${CYAN}Failed: ${NC}$failCount / $totalCount"
    }
    Write-Host ""

    # Write aggregated results JSON
    $resultsDir = Join-Path $scriptRoot "results"
    if (-not (Test-Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }

    $regressionResults = @{
        Mode              = "FullRegression"
        DirectoryType     = $DirectoryType
        Template          = $Template
        Step              = $Step
        LogLevel          = if ($LogLevel) { $LogLevel } else { "(from .env)" }
        ChangeTracking    = if ($DisableChangeTracking) { "Disabled" } else { "Enabled" }
        ExportConcurrency = if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $ExportConcurrency } else { $null }
        StartTime         = $allStart.ToString("yyyy-MM-dd HH:mm:ss")
        Duration          = $allDuration.ToString('hh\:mm\:ss')
        OverallSuccess    = (-not $anyFailed)
        AbortedEarly      = ($skipCount -gt 0)
        ContinueOnFailure = [bool]$ContinueOnFailure
        Scenarios         = @($results | ForEach-Object {
            @{
                Name     = $_.Name
                Success  = $_.Success
                Duration = $_.Duration
                ExitCode = $_.ExitCode
                Skipped  = [bool]$_.Skipped
            }
        })
        Timings        = $regressionTimings
    }

    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $resultsFile = Join-Path $resultsDir "full-regression-$timestamp.json"
    $regressionResults | ConvertTo-Json -Depth 10 | Set-Content $resultsFile
    Write-Host "${GRAY}Results saved to: $resultsFile${NC}"
    Write-Host ""

    # Re-run Command
    Write-Host "${CYAN}Re-run Command:${NC}"
    Write-Host ""
    $rerunParts = @("jim-reset && pwsh ./test/integration/Run-IntegrationTests.ps1")
    $rerunParts += "-Scenario All"
    $rerunParts += "-Template $Template"
    if ($Step -ne "All") { $rerunParts += "-Step $Step" }
    $rerunParts += "-DirectoryType $DirectoryType"
    if ($LogLevel) { $rerunParts += "-LogLevel $LogLevel" }
    if ($DisableChangeTracking) { $rerunParts += "-DisableChangeTracking" }
    if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $rerunParts += "-ExportConcurrency $ExportConcurrency" }
    if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $rerunParts += "-MaxExportParallelism $MaxExportParallelism" }
    if ($TimeoutSeconds -ne 180) { $rerunParts += "-TimeoutSeconds $TimeoutSeconds" }
    if ($CaptureMetrics) { $rerunParts += "-CaptureMetrics" }
    if ($IgnoreSnapshots) { $rerunParts += "-IgnoreSnapshots" }
    Write-Host "  $($rerunParts -join ' ')"
    Write-Host ""

    if ($anyFailed) {
        Write-Host "${RED}One or more scenarios failed.${NC}"
        exit 1
    }
    else {
        Write-Host "${GREEN}All scenarios passed.${NC}"
        exit 0
    }
}

function Write-Banner {
    param([string]$Title)
    Write-Host ""
    Write-Host "${CYAN}$("=" * 65)${NC}"
    Write-Host "${CYAN}  $Title${NC}"
    Write-Host "${CYAN}$("=" * 65)${NC}"
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

# Auto-set higher export concurrency for OpenLDAP (can handle 50+ concurrent writes)
# unless the user explicitly specified a value. Samba AD keeps the JIM default of 4.
if ($DirectoryType -eq "OpenLDAP" -and -not $PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $ExportConcurrency = 16
    # Mark as explicitly set so it flows through to setup scripts
    $PSBoundParameters['ExportConcurrency'] = $ExportConcurrency
}

Write-Host "${GRAY}Configuration:${NC}"
Write-Host "  Scenario:                ${CYAN}$Scenario${NC}"
if ($templateRelevant) {
    Write-Host "  Template:                ${CYAN}$Template${NC}"
} else {
    Write-Host "  Template:                ${GRAY}N/A (scenario uses fixed test data)${NC}"
}
Write-Host "  Step:                    ${CYAN}$Step${NC}"
Write-Host "  Skip Reset:              ${CYAN}$SkipReset${NC}"
Write-Host "  Skip Build:              ${CYAN}$SkipBuild${NC}"
Write-Host "  Setup Only:              ${CYAN}$SetupOnly${NC}"
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    Write-Host "  LDAP Export Concurrency: ${CYAN}$ExportConcurrency${NC}"
} else {
    Write-Host "  LDAP Export Concurrency: ${GRAY}(JIM default: 4)${NC}"
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    Write-Host "  Max Export Parallelism:  ${CYAN}$MaxExportParallelism${NC}"
} else {
    Write-Host "  Max Export Parallelism:  ${GRAY}(JIM default: 1)${NC}"
}
Write-Host "  Service Timeout:         ${CYAN}${TimeoutSeconds}s${NC}"
if ($LogLevel) {
    Write-Host "  Log Level:               ${CYAN}$LogLevel${NC}"
} else {
    Write-Host "  Log Level:               ${GRAY}(from .env)${NC}"
}
if ($DisableChangeTracking) {
    Write-Host "  Change Tracking:         ${YELLOW}Disabled${NC}"
} else {
    Write-Host "  Change Tracking:         ${CYAN}Enabled${NC}"
}
if ($DirectoryType -in @("OpenLDAP", "All")) {
    if ($DurableDirectoryWrites) {
        Write-Host "  Directory Writes:        ${CYAN}Durable (customer-representative)${NC}"
    } else {
        Write-Host "  Directory Writes:        ${YELLOW}Fast/nosync (TEST-ONLY speed-up; not customer-representative)${NC}"
    }
}

# Wire the durability mode through to the OpenLDAP container (compose reads
# OPENLDAP_FAST_WRITES; the container's boot reconcile applies or removes the
# MDB nosync flags accordingly). Snapshot images are mode-agnostic.
$env:OPENLDAP_FAST_WRITES = if ($DurableDirectoryWrites) { 'no' } else { 'yes' }

# Performance baselines are per durability mode: fast-write and durable runs have very
# different export wall-clocks, so comparing across modes would mislead.
$script:PerfModeSuffix = if ($DurableDirectoryWrites) { "-durable" } else { "" }

# Metrics streaming status.
# Hydrate JIM_BENCH_* from .env into the process env when not already set,
# so a single .env definition both configures the Docker stack and enables
# the runner's streaming path. The devcontainer setup script reminds users
# to populate JIM_BENCH_API_KEY in .env for this reason.
$envFilePath = Join-Path $repoRoot ".env"
if (Test-Path $envFilePath) {
    $envContent = Get-Content $envFilePath -Raw
    foreach ($benchVar in @('JIM_BENCH_API_URL', 'JIM_BENCH_API_KEY')) {
        if (-not [Environment]::GetEnvironmentVariable($benchVar)) {
            if ($envContent -match "(?m)^$benchVar=(.+)$") {
                $benchValue = $Matches[1].Trim()
                if ($benchValue) {
                    Set-Item "env:$benchVar" $benchValue
                }
            }
        }
    }
}
$metricsStreamingEnabled = $env:JIM_BENCH_API_URL -and $env:JIM_BENCH_API_KEY
# Pre-declare metrics tracking vars so the resolved-config banner and the
# post-scenario submission block can reference them under Set-StrictMode
# even on code paths where streaming is disabled or never started.
$metricsRunId = if ($metricsStreamingEnabled) { [Guid]::NewGuid().ToString() } else { $null }
$metricsStreamJob = $null
$metricsHostFingerprint = $null
if ($metricsStreamingEnabled) {
    Write-Host "  Metrics Streaming:       ${GREEN}Enabled${NC}"
    Write-Host "                           ${GRAY}$($env:JIM_BENCH_API_URL)${NC}"
} else {
    Write-Host "  Metrics Streaming:       ${GRAY}Disabled (set JIM_BENCH_API_URL and JIM_BENCH_API_KEY to enable)${NC}"
}
Write-Host ""

# Change to repository root
Set-Location $repoRoot

# Reap monitor processes/containers leaked by a previous crashed or hard-killed runner
# (#918). Runs for every invocation, including each -Scenario All child: between scenarios
# no monitors are live, so anything matched is a genuine stray.
Clear-StaleIntegrationMonitors -ResultsPath (Join-Path $scriptRoot 'results')

# Step 0: Ensure Samba AD images exist
$step0Start = Get-Date
Write-Section "Step 0: Checking Samba AD Images"

# OpenLDAP scenarios never use Samba AD containers — skip the image build entirely.
# Building Samba AD images takes 30-600+ seconds and can time out under disk pressure,
# causing false failures for OpenLDAP runs.
if ($DirectoryType -eq "OpenLDAP") {
    Write-Step "Skipping Samba AD image check (DirectoryType=OpenLDAP)"
}
else {

$buildScript = Join-Path $scriptRoot "docker" "samba-ad-prebuilt" "Build-SambaImages.ps1"

# Compute current build content hash from source scripts (shared with Test-SnapshotAvailable)
$currentBuildHash = Get-SambaBaseBuildHash

# Function to check if a Samba image needs rebuilding (missing or stale)
function Test-SambaImageNeedsRebuild {
    param([string]$ImageTag)

    $imageExists = docker images -q $ImageTag 2>$null
    if (-not $imageExists) {
        return @{ NeedsRebuild = $true; Reason = "not found" }
    }

    # Check the build hash label to detect stale images
    $imageHash = docker inspect --format '{{ index .Config.Labels "jim.samba.build-hash" }}' $ImageTag 2>$null
    if ($imageHash -ne $currentBuildHash) {
        $detail = if ($imageHash) { "hash $imageHash != $currentBuildHash" } else { "no build hash label (pre-hash image)" }
        return @{ NeedsRebuild = $true; Reason = "stale ($detail)" }
    }

    return @{ NeedsRebuild = $false; Reason = $null }
}

# Check if the pre-built Samba AD primary image exists and is up to date
$sambaImageTag = "ghcr.io/tetronio/jim-samba-ad:primary"
$primaryCheck = Test-SambaImageNeedsRebuild -ImageTag $sambaImageTag

if ($primaryCheck.NeedsRebuild) {
    Write-Warning "Samba AD Primary image needs rebuilding: $($primaryCheck.Reason)"
    Write-Step "Building Samba AD Primary image (this takes ~2-3 minutes)..."

    if (-not (Test-Path $buildScript)) {
        Write-Failure "Build script not found: $buildScript"
        exit 1
    }

    # Remove stale image if it exists
    $existingImage = docker images -q $sambaImageTag 2>$null
    if ($existingImage) {
        Write-Step "Removing stale image..."
        docker rmi $sambaImageTag 2>$null | Out-Null
    }

    & $buildScript -Images Primary
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to build Samba AD Primary image"
        exit 1
    }

    Write-Success "Samba AD Primary image built successfully"
}
else {
    Write-Success "Samba AD Primary image found and up to date: $sambaImageTag"
}

# For Scenario 2 and Scenario 8 with Samba AD, also check for Source and Target images
if (($Scenario -like "*Scenario2*" -or $Scenario -like "*Scenario8*") -and $DirectoryType -ne "OpenLDAP") {
    # Check Source image
    $sourceImageTag = "ghcr.io/tetronio/jim-samba-ad:source"
    $sourceCheck = Test-SambaImageNeedsRebuild -ImageTag $sourceImageTag

    if ($sourceCheck.NeedsRebuild) {
        Write-Warning "Samba AD Source image needs rebuilding: $($sourceCheck.Reason)"
        Write-Step "Building Samba AD Source image (this takes ~2-3 minutes)..."

        if (-not (Test-Path $buildScript)) {
            Write-Failure "Build script not found: $buildScript"
            exit 1
        }

        # Remove stale image if it exists
        $existingImage = docker images -q $sourceImageTag 2>$null
        if ($existingImage) {
            Write-Step "Removing stale image..."
            docker rmi $sourceImageTag 2>$null | Out-Null
        }

        & $buildScript -Images Source
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "Failed to build Samba AD Source image"
            exit 1
        }

        Write-Success "Samba AD Source image built successfully"
    }
    else {
        Write-Success "Samba AD Source image found and up to date: $sourceImageTag"
    }

    # Check Target image
    $targetImageTag = "ghcr.io/tetronio/jim-samba-ad:target"
    $targetCheck = Test-SambaImageNeedsRebuild -ImageTag $targetImageTag

    if ($targetCheck.NeedsRebuild) {
        Write-Warning "Samba AD Target image needs rebuilding: $($targetCheck.Reason)"
        Write-Step "Building Samba AD Target image (this takes ~2-3 minutes)..."

        if (-not (Test-Path $buildScript)) {
            Write-Failure "Build script not found: $buildScript"
            exit 1
        }

        # Remove stale image if it exists
        $existingImage = docker images -q $targetImageTag 2>$null
        if ($existingImage) {
            Write-Step "Removing stale image..."
            docker rmi $targetImageTag 2>$null | Out-Null
        }

        & $buildScript -Images Target
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "Failed to build Samba AD Target image"
            exit 1
        }

        Write-Success "Samba AD Target image built successfully"
    }
    else {
        Write-Success "Samba AD Target image found and up to date: $targetImageTag"
    }
}

} # end: DirectoryType -ne OpenLDAP (Samba AD image check)

$timings["0. Check Samba Image"] = (Get-Date) - $step0Start

# Step 1: Reset (unless skipped)
$step1Start = Get-Date
if (-not $SkipReset) {
    Write-Section "Step 1: Resetting JIM Environment"

    Write-Step "Stopping all containers and removing volumes..."
    docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down -v 2>&1 | Out-Null
    # Use --profile to stop containers from all scenarios (scenario2, scenario8, etc.)
    # Without specifying profiles, containers started with profiles won't be stopped
    docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 --profile openldap down -v --remove-orphans 2>&1 | Out-Null

    # Force-remove any leftover integration test containers by name.
    # This handles containers that were created under a different Docker Compose project name
    # (e.g., 'jim' instead of 'jim-integration') and are therefore not cleaned up by 'down -v'.
    Write-Step "Removing any leftover integration test containers..."
    $integrationContainers = @("samba-ad-primary", "samba-ad-source", "samba-ad-target", "openldap-primary", "sqlserver-hris-a", "oracle-hris-b", "postgres-target", "mysql-test")
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

    # Remove the connector-files volume. With the containers force-rm'd above, no
    # one should be holding this volume anymore; `docker volume rm` removes it
    # outright. If it still fails for any reason (e.g. a leftover container from
    # an unrelated Docker project), fall back to an in-place wipe so the next
    # scenario doesn't inherit stale CSVs. This mirrors the Reset-JIMForNextScenario
    # strategy where Samba/LDAP stay up and the volume must be emptied in place.
    $rmResult = docker volume rm jim-connector-files-volume 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "${GRAY}  jim-connector-files-volume could not be removed (${rmResult}); wiping in place instead...${NC}"
        Clear-ConnectorFilesVolume
    }

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
    $now = (Get-Date).ToUniversalTime()
    $minutesSinceMidnight = $now.Hour * 60 + $now.Minute
    $env:VERSION_SUFFIX = "dev.$($now.ToString('yyyyMMdd')).$minutesSinceMidnight"
    $buildOutput = docker compose -f docker-compose.yml -f docker-compose.override.yml build 2>&1
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
    # Strip any leading comment marker (# ) so a commented-out line becomes active
    $envContent = $envContent -replace "(?m)^#\s*JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$apiKey"
    $envContent = $envContent -replace "(?m)^JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$apiKey"
}
else {
    $newLine = if ($envContent.EndsWith("`n")) { "" } else { "`n" }
    $envContent = $envContent + $newLine + "JIM_INFRASTRUCTURE_API_KEY=$apiKey`n"
}
# Update log level if specified
$script:OriginalLogLevel = $null
if ($LogLevel) {
    # Save original value for restoration later
    if ($envContent -match "(?m)^JIM_LOG_LEVEL=(.+)$") {
        $script:OriginalLogLevel = $Matches[1].Trim()
    }
    $envContent = $envContent -replace "(?m)^JIM_LOG_LEVEL=.*", "JIM_LOG_LEVEL=$LogLevel"
    Write-Step "Set JIM_LOG_LEVEL=$LogLevel (was: $($script:OriginalLogLevel ?? 'not set'))"
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

# Pre-create the worker log bind-mount directory (owned by the current user) so the Docker
# daemon does not auto-create it as root, and make it writable for the non-root worker UID
# (1654, baked into JIM.Worker/Dockerfile). Fails fast with a remediation if a prior
# non-runner stack-up (jim-stack/jim-reset) already created it as root and we cannot repair
# it. See utils/Initialize-WorkerLogDirectories.ps1 for the full rationale.
Initialize-WorkerLogDirectories -LogDirectory (Join-Path $scriptRoot "results" "logs")

Write-Step "Starting JIM stack..."
$jimResult = docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Failure "Failed to start JIM stack"
    Write-Host "${GRAY}$jimResult${NC}"
    exit 1
}
Write-Success "JIM stack started"

# Start socat bridge so Keycloak is accessible at localhost:8181 for browser access.
# Docker-in-Docker proxy ports aren't forwarded by VS Code Dev Containers automatically.
# Uses setsid + disown to fully detach socat from the PowerShell process tree,
# so the bridge survives after this script exits (e.g. -SetupOnly mode).
if (Get-Command socat -ErrorAction SilentlyContinue) {
    $bridgeScript = "#!/bin/bash`npkill -f 'socat.*TCP:127.0.0.1:8180' 2>/dev/null || true`nsetsid socat TCP-LISTEN:8181,fork,reuseaddr,bind=0.0.0.0 TCP:127.0.0.1:8180 </dev/null >/dev/null 2>&1 &`ndisown`n"
    $bridgePath = [System.IO.Path]::GetTempPath() + "jim-keycloak-bridge.sh"
    [System.IO.File]::WriteAllText($bridgePath, $bridgeScript)
    & bash $bridgePath
    Write-Success "Keycloak bridge started (localhost:8181)"
}

Start-Sleep -Seconds 2

# Snapshot image selection communicates with docker compose via process-level environment
# variables, and an all-scenarios sweep invokes each scenario in this same process. Clear them
# all up front so a scenario that skips snapshot selection (Scenario 1's empty target,
# Scenario 14's bespoke six-user dataset) or whose snapshot check fails gets the compose
# defaults, not the previous scenario's snapshot. Leaked state here put Scenario 14 on the
# previous scenario's general-small image (50 baked-in users), tripping its isolation check.
$env:SAMBA_IMAGE_PRIMARY = $null
$env:SAMBA_IMAGE_SOURCE = $null
$env:SAMBA_IMAGE_TARGET = $null
$env:OPENLDAP_IMAGE_PRIMARY = $null

# Check for pre-populated snapshot images (Scenario 1 / primary)
# Note: "*Scenario1*" also substring-matches "Scenario14-...", so it must be excluded explicitly;
# Scenario 14 is OpenLDAP only (enforced above) and has no Samba AD snapshot of its own.
if (-not $IgnoreSnapshots -and $Scenario -like "*Scenario1*" -and $Scenario -notlike "*Scenario14*") {
    $s1Hash = Get-PopulateScriptHash -ScenarioName "Scenario1"
    $s1Tag = Get-SnapshotImageTag -Role "primary" -Size $Template
    if (Test-SnapshotAvailable -ImageTag $s1Tag -ExpectedHash $s1Hash) {
        $env:SAMBA_IMAGE_PRIMARY = $s1Tag
        $script:UsingSnapshots = $true
        Write-Host "  ${GREEN}Using snapshot: $s1Tag${NC}"
    } else {
        Write-Host "  ${YELLOW}No snapshot found for $s1Tag — building (first run only)...${NC}"
        & "$scriptRoot/Build-SambaSnapshots.ps1" -Scenario Scenario1 -Template $Template
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Snapshot build failed — falling back to live population"
        } elseif (Test-SnapshotAvailable -ImageTag $s1Tag -ExpectedHash $s1Hash) {
            $env:SAMBA_IMAGE_PRIMARY = $s1Tag
            $script:UsingSnapshots = $true
            Write-Host "  ${GREEN}Snapshot built and ready: $s1Tag${NC}"
        }
    }
}

if ($DirectoryType -eq "OpenLDAP") {
    # Ensure the base OpenLDAP image is current before any snapshot handling, so snapshot
    # rebuilds use a fresh base image. Docker compose starts a stale base image as-is (the
    # build: fallback only applies when the image is absent), so changes to the Dockerfile,
    # init script or bootstrap LDIF would otherwise be silently ignored.
    $expectedOlBuildHash = Get-OpenLDAPBaseBuildHash
    $olBaseImage = "ghcr.io/tetronio/jim-openldap:primary"
    $olBaseBuildHash = docker image inspect $olBaseImage --format '{{index .Config.Labels "jim.openldap.build-hash"}}' 2>&1
    $olBaseImageMissing = $LASTEXITCODE -ne 0
    if ($olBaseImageMissing -or "$olBaseBuildHash" -ne $expectedOlBuildHash) {
        $olRebuildReason = if ($olBaseImageMissing) { "not found" } else { "stale (hash $olBaseBuildHash != $expectedOlBuildHash)" }
        Write-Warning "OpenLDAP base image needs rebuilding: $olRebuildReason"
        Write-Step "Building OpenLDAP base image..."
        & "$scriptRoot/docker/openldap/Build-OpenLdapImage.ps1"
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "Failed to build OpenLDAP base image"
            exit 1
        }
        Write-Success "OpenLDAP base image built successfully"
    }
    else {
        Write-Success "OpenLDAP base image is current (hash $expectedOlBuildHash)"
    }

    # Check for pre-populated OpenLDAP snapshot images
    # S1 does not need pre-populated data — the target directory starts empty
    # S14 has its own tiny, bespoke six-user-per-suffix dataset (Populate-OpenLDAP-Scenario14.ps1)
    # populated by Invoke-Scenario14 itself; it is fast enough that snapshotting would add
    # complexity for negligible benefit, so it is excluded from snapshot handling entirely.
    if (-not $IgnoreSnapshots -and $Scenario -notlike "*Scenario1*" -and $Scenario -notlike "*Scenario14*") {
        $olSnapshotScenario = if ($Scenario -like "*Scenario8*") { "Scenario8" } else { "General" }
        $olSnapshotRole = if ($Scenario -like "*Scenario8*") { "s8" } else { "general" }
        $olHash = Get-OpenLDAPPopulateScriptHash -ScenarioName $olSnapshotScenario
        $olTag = Get-OpenLDAPSnapshotImageTag -Role $olSnapshotRole -Size $Template
        if (Test-OpenLDAPSnapshotAvailable -ImageTag $olTag -ExpectedHash $olHash) {
            $env:OPENLDAP_IMAGE_PRIMARY = $olTag
            $script:UsingOpenLDAPSnapshots = $true
            Write-Host "  ${GREEN}Using OpenLDAP snapshot: $olTag${NC}"
        } else {
            Write-Host "  ${YELLOW}No OpenLDAP snapshot found for $olTag — building (first run only)...${NC}"
            & "$scriptRoot/Build-OpenLDAPSnapshots.ps1" -Scenario $olSnapshotScenario -Template $Template
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "OpenLDAP snapshot build failed — falling back to live population"
            } elseif (Test-OpenLDAPSnapshotAvailable -ImageTag $olTag -ExpectedHash $olHash) {
                $env:OPENLDAP_IMAGE_PRIMARY = $olTag
                $script:UsingOpenLDAPSnapshots = $true
                Write-Host "  ${GREEN}OpenLDAP snapshot built and ready: $olTag${NC}"
            }
        }
    }

    # Scale the OpenLDAP container's memory limit with template size. back-mdb has
    # no internal entry cache; it relies entirely on the OS page cache over its
    # memory-mapped databases, so the limit must accommodate the working set (both
    # suffixes plus the hot accesslog tail) or large-template runs thrash: the
    # Scale500k25kGroups big-group export measurably degraded at the old fixed 2G
    # cap with slapd pinned at 1.94G. Unlike the Samba scaling above, this applies
    # regardless of snapshots; the memory pressure comes at run time (import and
    # export), not during population. Limits are caps, not reservations, so the
    # low default keeps small templates safe on modest dev machines while costing
    # scale runs nothing.
    $env:OPENLDAP_PRIMARY_MEMORY = switch -Wildcard ($Template) {
        "Scale1m*"   { "12G"; break }
        "Scale750k*" { "10G"; break }
        "Scale500k*" { "8G"; break }
        "Scale200k*" { "4G"; break }
        "Scale100k*" { "3G"; break }
        default      { "2G" }
    }
    if ($env:OPENLDAP_PRIMARY_MEMORY -ne "2G") {
        Write-Host "  OpenLDAP memory limit scaled to $($env:OPENLDAP_PRIMARY_MEMORY) for $Template template" -ForegroundColor Gray
    }

    Write-Step "Starting OpenLDAP (Primary)..."
    $openldapResult = docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile openldap up -d 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to start OpenLDAP"
        Write-Host "${GRAY}$openldapResult${NC}"
        exit 1
    }
    Write-Success "OpenLDAP Primary started"
}
else {
    Write-Step "Starting Samba AD (Primary)..."
    $sambaResult = docker compose -f test/integration/docker/docker-compose.integration-tests.yml up -d 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to start Samba AD"
        Write-Host "${GRAY}$sambaResult${NC}"
        exit 1
    }
    Write-Success "Samba AD Primary started"
}

# Start Scenario 2 containers if running Scenario 2
if ($Scenario -like "*Scenario2*") {
    Write-Step "Starting Samba AD (Source and Target for Scenario 2)..."
    $scenario2Result = docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile scenario2 up -d 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "Failed to start Scenario 2 Samba AD containers"
        Write-Host "${GRAY}$scenario2Result${NC}"
        exit 1
    }
    Write-Success "Samba AD Source and Target started"
}

# Start Scenario 8 containers if running Scenario 8 with Samba AD
# For OpenLDAP, S8 uses the same openldap-primary container (already started above)
if ($Scenario -like "*Scenario8*" -and $DirectoryType -ne "OpenLDAP") {
    # Check for pre-populated snapshot images
    if (-not $IgnoreSnapshots) {
        $s8Hash = Get-PopulateScriptHash -ScenarioName "Scenario8"
        $s8SourceTag = Get-SnapshotImageTag -Role "source-s8" -Size $Template
        $s8TargetTag = Get-SnapshotImageTag -Role "target-s8" -Size $Template
        if ((Test-SnapshotAvailable -ImageTag $s8SourceTag -ExpectedHash $s8Hash) -and
            (Test-SnapshotAvailable -ImageTag $s8TargetTag -ExpectedHash $s8Hash)) {
            $env:SAMBA_IMAGE_SOURCE = $s8SourceTag
            $env:SAMBA_IMAGE_TARGET = $s8TargetTag
            $script:UsingSnapshots = $true
            Write-Host "  ${GREEN}Using snapshots: $s8SourceTag, $s8TargetTag${NC}"
        } else {
            Write-Host "  ${YELLOW}No snapshots found for Scenario 8 — building (first run only)...${NC}"
            & "$scriptRoot/Build-SambaSnapshots.ps1" -Scenario Scenario8 -Template $Template
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Snapshot build failed — falling back to live population"
            } elseif ((Test-SnapshotAvailable -ImageTag $s8SourceTag -ExpectedHash $s8Hash) -and
                      (Test-SnapshotAvailable -ImageTag $s8TargetTag -ExpectedHash $s8Hash)) {
                $env:SAMBA_IMAGE_SOURCE = $s8SourceTag
                $env:SAMBA_IMAGE_TARGET = $s8TargetTag
                $script:UsingSnapshots = $true
                Write-Host "  ${GREEN}Snapshots built and ready: $s8SourceTag, $s8TargetTag${NC}"
            }
        }
    }

    # Scale Samba container memory for larger templates (ldbadd is memory-intensive —
    # it loads the full LDB into memory, so memory needs grow with user count)
    # Only needed when NOT using snapshots (snapshots don't run ldbadd)
    if (-not $script:UsingSnapshots -and $Template -in @("Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")) {
        $env:SAMBA_SOURCE_MEMORY = "8G"
        $env:SAMBA_TARGET_MEMORY = "4G"
        Write-Host "  Samba source memory scaled to 8G for $Template template" -ForegroundColor Gray
    }
    Write-Step "Starting Samba AD (Source and Target for Scenario 8)..."
    $scenario8Result = docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile scenario8 up -d 2>&1
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

if ($DirectoryType -eq "OpenLDAP") {
    # Wait for OpenLDAP
    Write-Step "Waiting for OpenLDAP to be ready..."
    $openldapReady = $false
    $elapsed = 0
    while (-not $openldapReady -and $elapsed -lt $TimeoutSeconds) {
        $status = docker inspect --format='{{.State.Health.Status}}' openldap-primary 2>&1
        if ($status -eq "healthy") {
            $openldapReady = $true
            Write-Success "OpenLDAP is healthy"
        }
        else {
            Start-Sleep -Seconds 3
            $elapsed += 3
        }
    }
    if (-not $openldapReady) {
        Write-Failure "OpenLDAP did not become ready in time"
        Write-Host "${YELLOW}  Check logs: docker logs openldap-primary${NC}"
        exit 1
    }
}
else {
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
}

# Wait for Scenario 2 or Scenario 8 Samba AD containers if applicable
# For OpenLDAP, the openldap-primary container wait is handled above
if (($Scenario -like "*Scenario2*" -or $Scenario -like "*Scenario8*") -and $DirectoryType -ne "OpenLDAP") {
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
# Skip when using snapshots — the snapshot already has populated data
if ($Scenario -like "*Scenario1*" -and -not $script:UsingSnapshots -and $DirectoryType -eq "SambaAD") {
    Write-Section "Step 4b: Preparing Samba AD for Testing"

    # First, try to delete the Corp OU if it exists (to ensure clean state)
    Write-Step "Cleaning up any existing Corp OU..."
    $result = docker exec samba-ad-primary samba-tool ou delete "OU=Corp,DC=panoply,DC=local" --force-subtree-delete 2>&1
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
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Corp,DC=panoply,DC=local" 2>&1
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
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Users,OU=Corp,DC=panoply,DC=local" 2>&1
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
    $result = docker exec samba-ad-primary samba-tool ou create "OU=Groups,OU=Corp,DC=panoply,DC=local" 2>&1
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

# Step 4c: Populate OpenLDAP with test data
# OpenLDAP starts empty (only base OUs from bootstrap). Unlike Samba AD which uses snapshot
# images with pre-populated data, OpenLDAP needs live population via Populate-OpenLDAP.ps1.
# Skip for S1 — the target directory starts empty (HR-driven provisioning into clean directory).
# Skip for S8 — it has its own population script (Populate-OpenLDAP-Scenario8.ps1) that only
# populates Source. The base script populates both suffixes, which would create pre-existing
# objects in Target and cause CouldNotJoinDueToExistingJoin errors during initial sync.
# Skip for S14 — it has its own population script (Populate-OpenLDAP-Scenario14.ps1), called by
# Invoke-Scenario14-AttributePriority.ps1 itself (self-populating, like S8), which seeds both
# suffixes with its own small deterministic six-user set sharing Employee IDs so they join.
if ($DirectoryType -eq "OpenLDAP" -and $Scenario -notlike "*Scenario1*" -and $Scenario -notlike "*Scenario8*" -and $Scenario -notlike "*Scenario14*" -and -not $script:UsingOpenLDAPSnapshots) {
    Write-Section "Step 4c: Populating OpenLDAP with Test Data"
    Write-Step "Running Populate-OpenLDAP.ps1 -Template $Template..."
    $populateScript = Join-Path $scriptRoot "Populate-OpenLDAP.ps1"
    if (Test-Path $populateScript) {
        & $populateScript -Template $Template
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "OpenLDAP population failed"
            exit 1
        }
        Write-Success "OpenLDAP populated with $Template template data"
    }
    else {
        Write-Failure "Populate-OpenLDAP.ps1 not found at $populateScript"
        exit 1
    }
}

# Step 4d: Disable change tracking if requested
if ($DisableChangeTracking) {
    Write-Step "Disabling change tracking via service settings API..."

    $modulePath = Join-Path $repoRoot "src" "JIM.PowerShell" "JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url "http://localhost:5200" -ApiKey $apiKey | Out-Null

    Set-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled" -Value "false"
    Set-JIMServiceSetting -Key "ChangeTracking.MvoChanges.Enabled" -Value "false"

    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue

    Write-Success "Change tracking disabled (CSO + MVO)"
}

# Step 5: Setup / Run test scenario
$step5Start = Get-Date

# Extract scenario number from name (e.g., "Scenario1-HRToIdentityDirectory" -> "1")
$scenarioNumber = if ($Scenario -match 'Scenario(\d+)') { $Matches[1] } else { $null }
$isScenario11 = ($scenarioNumber -eq "11")

if ($SetupOnly) {
    # SetupOnly mode: configure JIM with connected systems and sync rules, then stop
    Write-Section "Step 5: Setting Up Scenario Configuration (SetupOnly)"

    # Validate that a setup script exists for this scenario
    $setupScript = if ($scenarioNumber) {
        $candidate = Join-Path $scriptRoot "Setup-Scenario$scenarioNumber.ps1"
        if (Test-Path $candidate) { $candidate } else { $null }
    } else { $null }

    if (-not $setupScript) {
        Write-Warning "No dedicated setup script found for '$Scenario'"
        Write-Warning "SetupOnly mode requires a Setup-Scenario*.ps1 script"
        Write-Host "${GRAY}Environment is running but not configured with scenario-specific connected systems.${NC}"
    }
    else {
        # Generate test data (CSV files) if scenario uses template-based data
        if ($templateRelevant) {
            Write-Step "Generating test data (Template: $Template)..."
            try {
                & "$scriptRoot/Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$scriptRoot/../test-data"
                Write-Success "Test data generated"
            }
            catch {
                Write-Failure "Test data generation failed: $($_.Exception.Message)"
                exit 1
            }
        }

        # Run the scenario setup script to configure connected systems, sync rules, and run profiles
        Write-Step "Running scenario setup: Setup-Scenario$scenarioNumber.ps1..."
        $setupParams = @{
            JIMUrl = "http://localhost:5200"
            ApiKey = $apiKey
            Template = $Template
            DirectoryConfig = $script:DirectoryConfig
        }
        if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
            $setupParams.ExportConcurrency = $ExportConcurrency
        }
        if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
            $setupParams.MaxExportParallelism = $MaxExportParallelism
        }
        $config = & $setupScript @setupParams
        if ($config) {
            Write-Success "Scenario configured successfully"
        }
        else {
            Write-Failure "Scenario setup returned no configuration"
        }
    }

    $timings["5. Setup (SetupOnly)"] = (Get-Date) - $step5Start

    # Print a helpful summary for the user
    $endTime = Get-Date
    $duration = $endTime - $startTime

    Write-Banner "SetupOnly Complete - Environment Ready"
    Write-Host ""

    Write-Host "${GRAY}Services:${NC}"
    Write-Host "  JIM Web:            ${CYAN}http://localhost:5200${NC}"
    Write-Host "  JIM API:            ${CYAN}http://localhost:5200/api${NC}"
    Write-Host "  API Reference:      ${CYAN}http://localhost:5200/api/reference${NC}"
    Write-Host ""
    Write-Host "${GRAY}API Key:${NC}"
    Write-Host "  ${CYAN}$apiKey${NC}"
    Write-Host ""
    Write-Host "${GRAY}Scenario:${NC}              ${CYAN}$Scenario${NC}"
    if ($templateRelevant) {
        Write-Host "${GRAY}Template:${NC}              ${CYAN}$Template${NC}"
    }
    Write-Host ""
    Write-Host "${GRAY}To run tests manually:${NC}"
    Write-Host "  ${BLUE}pwsh test/integration/scenarios/Invoke-$Scenario.ps1 -Template $Template -ApiKey `"$apiKey`"${NC}"
    Write-Host ""
    Write-Host "${GRAY}To re-run with existing environment:${NC}"
    Write-Host "  ${BLUE}pwsh ./test/integration/Run-IntegrationTests.ps1 -Scenario `"$Scenario`" -Template $Template -SkipReset -SkipBuild${NC}"
    Write-Host ""

    # Docker Cleanup (prune unused images and build cache to prevent disk space accumulation)
    Write-Step "Pruning unused images and build cache (preserving snapshots)..."
    $imagePrune = Invoke-ImagePrunePreservingSnapshots
    $builderPrune = docker builder prune -af 2>&1
    $imageReclaimed = $imagePrune | Select-String "Total reclaimed space:\s*(.+)"
    $builderReclaimed = $builderPrune | Select-String "Total reclaimed space:\s*(.+)"
    $parts = @()
    if ($imageReclaimed) { $parts += "images: $($imageReclaimed.Matches[0].Groups[1].Value)" }
    if ($builderReclaimed) { $parts += "build cache: $($builderReclaimed.Matches[0].Groups[1].Value)" }
    if ($parts.Count -gt 0) {
        Write-Success "Reclaimed ($($parts -join ', '))"
    }

    # Performance Summary
    Write-Section "Performance Summary"
    Write-Host "${CYAN}Stage Timings:${NC}"

    $sortedTimings = $timings.GetEnumerator() | Sort-Object Name
    $totalSeconds = 0
    foreach ($timing in $sortedTimings) {
        $seconds = [math]::Round($timing.Value.TotalSeconds, 1)
        $totalSeconds += $seconds
        $bar = "█" * [math]::Max(0, [math]::Min(50, [math]::Floor($seconds / 2)))
        Write-Host ("  {0,-25} {1,6}s  {2}" -f $timing.Name, $seconds, $bar) -ForegroundColor $(if ($seconds -gt 60) { "Yellow" } elseif ($seconds -gt 30) { "Cyan" } else { "Green" })
    }

    Write-Host ""
    Write-Host "${CYAN}Total Duration: ${NC}$($duration.ToString('hh\:mm\:ss')) (${totalSeconds}s)"
    Write-Host ""
    Write-Host "${GREEN}Environment is ready for use. No tests were executed.${NC}"
    Write-Host ""
    exit 0
}

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

$displayParams = @()
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $displayParams += "-ExportConcurrency $ExportConcurrency" }
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $displayParams += "-MaxExportParallelism $MaxExportParallelism" }
if ($LogLevel) { $displayParams += "-LogLevel $LogLevel" }
if ($DisableChangeTracking) { $displayParams += "-DisableChangeTracking" }
if ($displayParams.Count -gt 0) {
    Write-Step "Running: Invoke-$Scenario.ps1 -Template $Template -Step $Step $($displayParams -join ' ')"
} else {
    Write-Step "Running: Invoke-$Scenario.ps1 -Template $Template -Step $Step"
}
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
$transcriptActive = $true
try {

# Log resolved configuration so the transcript is self-contained and reviewable
Write-Host ""
Write-Host "-----------------------------------------------------------------"
Write-Host "  Test Configuration (resolved)"
Write-Host "-----------------------------------------------------------------"
Write-Host ""
Write-Host "  Scenario:                $Scenario"
if ($templateRelevant) {
    Write-Host "  Template:                $Template"
} else {
    Write-Host "  Template:                N/A (scenario uses fixed test data)"
}
Write-Host "  Step:                    $Step"
Write-Host "  Directory Type:          $DirectoryType"
Write-Host "  Skip Reset:              $SkipReset"
Write-Host "  Skip Build:              $SkipBuild"
Write-Host "  Setup Only:              $SetupOnly"
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    Write-Host "  LDAP Export Concurrency: $ExportConcurrency"
} else {
    Write-Host "  LDAP Export Concurrency: (JIM default: 4)"
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    Write-Host "  Max Export Parallelism:  $MaxExportParallelism"
} else {
    Write-Host "  Max Export Parallelism:  (JIM default: 1)"
}
Write-Host "  Service Timeout:         ${TimeoutSeconds}s"
if ($LogLevel) {
    Write-Host "  Log Level:               $LogLevel"
} else {
    Write-Host "  Log Level:               (from .env)"
}
if ($DisableChangeTracking) {
    Write-Host "  Change Tracking:         Disabled"
} else {
    Write-Host "  Change Tracking:         Enabled"
}
if ($CaptureMetrics) {
    Write-Host "  Capture Metrics:         Yes"
}
if ($metricsStreamingEnabled) {
    Write-Host "  Metrics Streaming:       Enabled ($($env:JIM_BENCH_API_URL))"
    Write-Host "  Metrics Run ID:          $metricsRunId"
}
if ($isScenario11) {
    $tierLabel = if ($Quick) { 'Quick' } elseif ($Exhaustive) { 'Exhaustive' } else { 'Default' }
    Write-Host "  Coverage Tier:           $tierLabel"
    if ($PSBoundParameters.ContainsKey('OperatorFilter') -and -not [string]::IsNullOrWhiteSpace($OperatorFilter)) {
        Write-Host "  Operator Filter:         $OperatorFilter"
    }
    if ($PSBoundParameters.ContainsKey('IncludeNegativeCells')) {
        Write-Host "  Include Negative Cells:  $IncludeNegativeCells"
    }
}
if ($IgnoreSnapshots) {
    Write-Host "  Ignore Snapshots:        Yes"
}
Write-Host ""

# Build scenario invocation params — only pass export tuning params to scenarios that accept them
$scenarioParams = @{
    Template = $Template
    Step = $Step
    ApiKey = $apiKey
    DirectoryConfig = $script:DirectoryConfig
}

# Skip population if using snapshot images (Samba AD or OpenLDAP).
# Scenario 14 is excluded: it self-populates its own bespoke six-user-per-suffix OpenLDAP dataset
# (Populate-OpenLDAP-Scenario14.ps1) and has no snapshot of its own, so it must ALWAYS populate.
# Without this guard, an "All" regression that snapshots an unrelated scenario earlier in the same
# process leaves $script:UsingSnapshots set when Scenario 14's turn comes (the "*Scenario1*" pattern
# also substring-matches "Scenario14"), which would wrongly pass SkipPopulate to the scenario and
# leave its directory empty, so the Employee ID join finds nothing. Mirrors the Scenario 14
# exclusions already on the snapshot-detection and general-population guards above.
if (($script:UsingSnapshots -or $script:UsingOpenLDAPSnapshots) -and $Scenario -notlike "*Scenario14*") {
    $scenarioParams.SkipPopulate = $true
}

# Export tuning params only apply to scenarios that accept them and have LDAP exports
# Scenarios 1, 2, 8: pass through to their setup scripts
# Scenario 6: passes through to its internal Setup-Scenario1 call
$scenariosAcceptingExportParams = @("1", "2", "6", "8", "10")
if ($scenarioNumber -and $scenariosAcceptingExportParams -contains $scenarioNumber) {
    if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
        $scenarioParams.ExportConcurrency = $ExportConcurrency
    }
    if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
        $scenarioParams.MaxExportParallelism = $MaxExportParallelism
    }
}

# Scenario 11 (Scoping Criteria Matrix) accepts -Quick, -Exhaustive, -OperatorFilter,
# -IncludeNegativeCells. Only pass these when running Scenario 11; other scenarios
# don't define them and would error on unexpected parameters.
if ($isScenario11) {
    if ($Quick)       { $scenarioParams.Quick = $true }
    if ($Exhaustive)  { $scenarioParams.Exhaustive = $true }
    if ($PSBoundParameters.ContainsKey('OperatorFilter') -and -not [string]::IsNullOrWhiteSpace($OperatorFilter)) {
        $scenarioParams.OperatorFilter = $OperatorFilter
    }
    if ($PSBoundParameters.ContainsKey('IncludeNegativeCells')) {
        $scenarioParams.IncludeNegativeCells = $IncludeNegativeCells
    }
}

# Start metrics streaming background job (if enabled).
# $metricsRunId / $metricsStreamJob / $metricsHostFingerprint were pre-declared
# earlier alongside $metricsStreamingEnabled so the resolved-config banner can
# reference them under Set-StrictMode.
if ($metricsStreamingEnabled) {
    Write-Step "Capturing host fingerprint..."
    $metricsHostFingerprint = & "$scriptRoot/Get-HostFingerprint.ps1"

    Write-Step "Starting metrics streaming to $($env:JIM_BENCH_API_URL)..."
    # Stream-WorkerLogs.ps1 follows the worker container via `docker logs -f`,
    # not the bind-mounted log file. The file sink writes CLEF JSON, which the
    # bench server-side parser (a port of the runner's Step 6 regex) cannot
    # ingest; the docker logs plaintext output matches the parser format.
    $metricsStreamJob = Start-Job -FilePath "$scriptRoot/Stream-WorkerLogs.ps1" -ArgumentList @(
        "jim.worker",
        $env:JIM_BENCH_API_URL,
        $env:JIM_BENCH_API_KEY,
        $metricsRunId,
        $Scenario,
        $Template,
        $metricsHostFingerprint.hostClass
    )
    Write-Success "Metrics streaming started (Run ID: $metricsRunId)"
}

# Start docker stats capture as a detached pwsh process so we can correlate per-container
# memory and CPU with the scenario phases. Uses Start-Process rather than Start-Job
# because Start-Job's runspace adds unnecessary overhead for a fire-and-forget script.
# NOTE (#918): "pwsh" resolves via PATH to the .NET global-tool SHIM, which spawns the real
# `dotnet .../pwsh.dll` sampler as a child it never forwards signals to. The Process handle
# below therefore cannot be used to stop the sampler; stopping is done via the stop-file in
# the finally block, with -ParentPid as the crash safety net (the sampler self-exits when
# this runner process dies).
$dockerStatsProcess = $null
$dockerStatsPath = $null
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $dockerStatsPath = Join-Path $scriptRoot "results" "docker-stats-$Scenario-$Template-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').csv"
    Write-Step "Starting docker stats capture -> $dockerStatsPath"
    $dockerStatsProcess = Start-Process -FilePath 'pwsh' -ArgumentList @(
        "-NoProfile", "-File", "$scriptRoot/Capture-DockerStats.ps1",
        "-OutputPath", $dockerStatsPath, "-IntervalSeconds", "2",
        "-ParentPid", "$PID"
    ) -PassThru -RedirectStandardOutput "$dockerStatsPath.stdout.log" -RedirectStandardError "$dockerStatsPath.stderr.log"
}

# Start the connector-files volume auditor. inotifywait sidecar logs every
# write/create/delete/rename to jim-connector-files-volume so we can pin down
# any out-of-band writers that the transcript can't name. See the 08:47:22
# Scale100k50Groups incident for the failure mode this was added to diagnose.
$volumeAuditLogPath = Join-Path $scriptRoot "results" "volume-audit-$Scenario-$Template-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').log"
Write-Step "Starting connector-files volume auditor -> $volumeAuditLogPath"
$volumeAuditor = Start-ConnectorVolumeAuditor -LogPath $volumeAuditLogPath

# Start the docker events capture. Streams all container/image/volume lifecycle
# events to disk so throwaway `docker run` calls (our busybox seed helper,
# rogue calls from other sessions) can be identified retroactively.
$dockerEventsLogPath = Join-Path $scriptRoot "results" "docker-events-$Scenario-$Template-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').log"
Write-Step "Starting docker events capture -> $dockerEventsLogPath"
$dockerEventsProcess = Start-DockerEventsCapture -LogPath $dockerEventsLogPath

# Start the JIM error watcher. This tails jim.web / jim.worker / jim.scheduler
# logs for Error/Fatal level lines (text-template '[HH:mm:ss ERR]'/'[HH:mm:ss FTL]'
# markers and CLEF '"@l":"Error"'/'"@l":"Fatal"') and writes any matches to a
# sentinel file. Start-JIMRunProfile -Wait loops check the sentinel between polls
# (via the JIM_RUNPROFILE_ABORT_SENTINEL env var) so a stalled activity
# accompanied by an error aborts immediately rather than polling forever.
$errWatcherSentinel = Join-Path $scriptRoot "results" "errors-$Scenario-$Template-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').log"
Write-Step "Starting JIM error watcher (sentinel: $errWatcherSentinel)"
$errWatcher = Start-JimErrorWatcher -SentinelPath $errWatcherSentinel -Since $step5Start
$env:JIM_RUNPROFILE_ABORT_SENTINEL = $errWatcherSentinel

try {
    & $scenarioScript @scenarioParams
    $scenarioExitCode = $LASTEXITCODE
}
catch {
    $scenarioExitCode = 1
    Write-Host ""
    Write-Host "${RED}✗ Scenario failed with error: $_${NC}"
    Write-Host ""
}
finally {
    if ($dockerStatsPath) {
        # Signal the sampler to exit on its own (it checks for this file every interval),
        # then reap any survivor by command-line identity. Stop-Process on the stored PID
        # is useless here: it only reaches the .NET global-tool shim, never the dotnet
        # child that actually runs the sampler (#918).
        New-Item -ItemType File -Path "$dockerStatsPath.stop" -Force | Out-Null
        Start-Sleep -Seconds 3   # one sample interval plus margin for a graceful exit

        # Scope the pattern to script name + this run's output path so the match can never
        # hit this runner's own command line; keep the $PID guard as belt and braces.
        $samplerPattern = [regex]::Escape('Capture-DockerStats.ps1') + '.*' + [regex]::Escape($dockerStatsPath)
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID -and $_.CommandLine -and $_.CommandLine -match $samplerPattern } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        if ($dockerStatsProcess -and -not $dockerStatsProcess.HasExited) {
            # The shim parent, if still around, is harmless but tidy it anyway.
            Stop-Process -Id $dockerStatsProcess.Id -Force -ErrorAction SilentlyContinue
        }
        Remove-Item "$dockerStatsPath.stop" -Force -ErrorAction SilentlyContinue
    }
    if ($dockerStatsPath -and (Test-Path $dockerStatsPath)) {
        # Wrap with @(...) so a zero- or one-line file doesn't trip Set-StrictMode:
        # Get-Content returns $null for an empty file and a single string for a
        # one-line file, neither of which expose a usable .Count property.
        $rowCount = @(Get-Content $dockerStatsPath).Count - 1
        if ($rowCount -lt 0) { $rowCount = 0 }
        # Only claim "stopped" when no sampler for this run survives; a survivor means the
        # CSV keeps growing and past-run data cannot be trusted (#918).
        $samplerPattern = [regex]::Escape('Capture-DockerStats.ps1') + '.*' + [regex]::Escape($dockerStatsPath)
        $samplerSurvivors = @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID -and $_.CommandLine -and $_.CommandLine -match $samplerPattern })
        if ($samplerSurvivors.Count -eq 0) {
            Write-Step "Docker stats capture stopped ($rowCount samples recorded)"
        }
        else {
            Write-Warning "Docker stats sampler survived the stop (PIDs: $($samplerSurvivors.Id -join ', ')); the CSV may keep growing"
        }
    }

    # Stop the connector-files volume auditor and record the line count so
    # reviewers can tell at a glance whether anything wrote to the volume.
    try {
        $auditLineCount = Stop-ConnectorVolumeAuditor -Handle $volumeAuditor
        if ($volumeAuditor) {
            Write-Step "Volume auditor stopped ($auditLineCount events recorded in $volumeAuditLogPath)"
        }
    }
    catch {
        Write-Host "${YELLOW}  Warning: volume auditor stop failed: $_${NC}"
    }

    # Stop the docker events capture.
    try {
        $eventLineCount = Stop-DockerEventsCapture -Process $dockerEventsProcess -LogPath $dockerEventsLogPath
        if ($dockerEventsProcess) {
            Write-Step "Docker events capture stopped ($eventLineCount events recorded in $dockerEventsLogPath)"
        }
    }
    catch {
        Write-Host "${YELLOW}  Warning: docker events capture stop failed: $_${NC}"
    }

    # Stop the live watcher and run the post-scenario belt-and-braces scan.
    # Always run these, even if the scenario threw; we want to know whether JIM
    # logged errors regardless of how the scenario ended.
    $capturedLines = @()
    if ($errWatcher) {
        $capturedLines = @(Stop-JimErrorWatcher -Handle $errWatcher)
    }
    Remove-Item Env:JIM_RUNPROFILE_ABORT_SENTINEL -ErrorAction SilentlyContinue

    if ($capturedLines.Count -gt 0) {
        Write-Host ""
        Write-Host "${RED}✗ JIM error watcher captured $($capturedLines.Count) Error/Fatal line(s) during the scenario:${NC}"
        foreach ($line in $capturedLines | Select-Object -First 10) {
            Write-Host "    $line" -ForegroundColor Red
        }
        if ($capturedLines.Count -gt 10) {
            Write-Host "    ... ($($capturedLines.Count - 10) more in $errWatcherSentinel)" -ForegroundColor Red
        }
        Write-Host ""
        $scenarioExitCode = 1
    }

    # Belt-and-braces: one-shot scan in case the live watcher missed shutdown-race lines.
    try {
        Assert-NoWorkerErrors -Since $step5Start
    }
    catch {
        Write-Host "${RED}✗ Post-scenario log scan failed: $_${NC}"
        $scenarioExitCode = 1
    }
}
$timings["5. Run Tests"] = (Get-Date) - $step5Start

Write-Host ""
Write-Step "Scenario log saved to: $scenarioLogFile"

# Step 6: Capture Performance Metrics
$step6Start = Get-Date
$currentFile = $null
Write-Section "Step 6: Capturing Performance Metrics"

# Skip detailed metrics capture for large templates - parsing the worker logs becomes
# prohibitively expensive (CPU and memory) due to the volume of DiagnosticListener lines.
# Use -CaptureMetrics to force capture regardless of template size.
$metricsSkippedTemplates = @("MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
if ($Template -in $metricsSkippedTemplates -and -not $CaptureMetrics) {
    Write-Warning "Skipping detailed performance metrics for '$Template' template (log volume too large for efficient parsing)"
    Write-Step "Use -CaptureMetrics to force capture (this will be slow)"

    # Save wall-clock duration so we can still compare total run time between runs
    $testDurationMs = $timings["5. Run Tests"].TotalMilliseconds
    Write-Step "Recording wall-clock duration for performance comparison..."

    $wallClockMetrics = @{
        Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        Scenario = $Scenario
        Template = $Template
        Step = $Step
        WallClockOnly = $true
        TestDurationMs = $testDurationMs
        Operations = @()
    }

    # Create performance results directory (per hostname)
    $hostname = [System.Net.Dns]::GetHostName()
    $perfDir = Join-Path $scriptRoot "results" "performance" $hostname
    if (-not (Test-Path $perfDir)) {
        New-Item -ItemType Directory -Path $perfDir -Force | Out-Null
    }

    # Save current wall-clock metrics
    $timestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $currentFile = Join-Path $perfDir "$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json"
    $wallClockMetrics | ConvertTo-Json -Depth 10 | Set-Content $currentFile
    Write-Success "Saved wall-clock metrics to: results/performance/$hostname/$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json"

    # Find most recent previous baseline (excluding current run)
    $previousFiles = Get-ChildItem $perfDir -Filter "$Scenario-$Template$($script:PerfModeSuffix)-*.json" |
        Where-Object { $_.Name -ne "$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json" } |
        Where-Object { $script:PerfModeSuffix -ne "" -or $_.Name -notlike "*-durable-*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($previousFiles) {
        $baseline = Get-Content $previousFiles.FullName | ConvertFrom-Json

        # Determine baseline duration - could be a wall-clock-only file or a full metrics file
        $baselineDurationMs = if ($null -ne $baseline.TestDurationMs) {
            $baseline.TestDurationMs
        }
        elseif ($baseline.Operations -and $baseline.Operations.Count -gt 0) {
            # Full metrics file - sum the top-level operation durations as an approximation
            ($baseline.Operations | Where-Object { -not $_.Parent } | Measure-Object -Property DurationMs -Sum).Sum
        }
        else {
            $null
        }

        if ($null -ne $baselineDurationMs -and $baselineDurationMs -gt 0) {
            Write-Host ""
            Write-Host "${CYAN}Performance Comparison (Wall-Clock):${NC}"
            Write-Host "${GRAY}(Detailed per-operation breakdown skipped for large templates)${NC}"
            Write-Host ""

            $delta = $testDurationMs - $baselineDurationMs
            $percentChange = ($delta / $baselineDurationMs) * 100

            $symbol = if ($delta -lt 0) { "↓" } elseif ($delta -gt 0) { "↑" } else { "=" }
            $colour = if ($delta -lt 0) { $GREEN } elseif ($delta -gt ($baselineDurationMs * 0.1)) { $RED } else { $YELLOW }

            # Format durations as friendly time strings
            function Format-WallClockTime {
                param([double]$Ms)
                if ($Ms -lt 1000) { return "$($Ms.ToString('F1'))ms" }
                elseif ($Ms -lt 60000) { return "$([math]::Round($Ms / 1000, 1))s" }
                else {
                    $totalSecs = [int]($Ms / 1000)
                    $mins = [Math]::Floor($totalSecs / 60)
                    $secs = $totalSecs % 60
                    if ($secs -eq 0) { return "${mins}m" }
                    return "${mins}m ${secs}s"
                }
            }

            $currentFormatted = Format-WallClockTime -Ms $testDurationMs
            $baselineFormatted = Format-WallClockTime -Ms $baselineDurationMs
            $deltaFormatted = Format-WallClockTime -Ms ([Math]::Abs($delta))

            $deltaSign = if ($delta -lt 0) { "-" } elseif ($delta -gt 0) { "+" } else { "" }

            Write-Host ("  {0,-35} {1,12}  {2}{3} {4}{5} ({6:+0.0;-0.0;0}%)${NC}" -f `
                "Total Test Duration", $currentFormatted, $colour, $symbol, $deltaSign, $deltaFormatted, $percentChange)
            Write-Host ("  {0,-35} {1,12}" -f "Previous Baseline", $baselineFormatted)

            Write-Host ""
            Write-Host "${GRAY}Baseline: $($previousFiles.Name) ($($baseline.Timestamp))${NC}"
        }
    }
    else {
        Write-Host ""
        Write-Host "${YELLOW}No previous baseline found for comparison.${NC}"
        Write-Host "${GRAY}This is the first performance capture for $Scenario-$Template$($script:PerfModeSuffix) on $hostname${NC}"
    }
}
else {
Write-Step "Extracting diagnostic timing from worker logs..."

# Capture worker logs with diagnostic output
$workerLogs = docker logs jim.worker 2>&1 | Where-Object { $_ -match "DiagnosticListener:" }

# Parse metrics into structured data using parallel processing
$metrics = @{
    Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Scenario = $Scenario
    Template = $Template
    Step = $Step
    TestDurationMs = $timings["5. Run Tests"].TotalMilliseconds
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
    $currentFile = Join-Path $perfDir "$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json"
    $metrics | ConvertTo-Json -Depth 10 | Set-Content $currentFile
    Write-Success "Saved metrics to: results/performance/$hostname/$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json"

    # Find most recent previous baseline (excluding current run)
    $previousFiles = Get-ChildItem $perfDir -Filter "$Scenario-$Template$($script:PerfModeSuffix)-*.json" |
        Where-Object { $_.Name -ne "$Scenario-$Template$($script:PerfModeSuffix)-$timestamp.json" } |
        Where-Object { $script:PerfModeSuffix -ne "" -or $_.Name -notlike "*-durable-*" } |
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
        Write-Host "${GRAY}This is the first performance capture for $Scenario-$Template$($script:PerfModeSuffix) on $hostname${NC}"
    }
}
} # end else (metrics not skipped)

$timings["6. Capture Metrics"] = (Get-Date) - $step6Start

# Stop metrics streaming and submit final results
if ($metricsStreamJob) {
    Write-Step "Stopping metrics streaming..."
    Stop-Job $metricsStreamJob -ErrorAction SilentlyContinue
    # Allow final flush to complete
    $jobOutput = Receive-Job $metricsStreamJob -ErrorAction SilentlyContinue
    if ($jobOutput) {
        $jobOutput | ForEach-Object { Write-Host "  ${GRAY}$_${NC}" }
    }
    Remove-Job $metricsStreamJob -Force -ErrorAction SilentlyContinue

    Write-Step "Submitting test results to JIM-Bench..."
    $scenarioSuccess = ($scenarioExitCode -eq 0)
    $testDurationMs = $timings["5. Run Tests"].TotalMilliseconds
    try {
        & "$scriptRoot/Submit-TestResults.ps1" `
            -RunId $metricsRunId `
            -Scenario $Scenario `
            -Template $Template `
            -Step $Step `
            -DirectoryType $DirectoryType `
            -Success $scenarioSuccess `
            -ExitCode $scenarioExitCode `
            -TestDurationMs $testDurationMs `
            -HostFingerprint $metricsHostFingerprint `
            -ApiUrl $env:JIM_BENCH_API_URL `
            -ApiKey $env:JIM_BENCH_API_KEY `
            -ResultFile $(if ($currentFile) { $currentFile } else { "" })
    }
    catch {
        Write-Warning "MetricsSubmission: Failed to submit results: $($_.Exception.Message)"
    }
}

# Restore original .env log level if we changed it
if ($script:OriginalLogLevel) {
    $envFilePath = Join-Path $repoRoot ".env"
    $envContent = Get-Content $envFilePath -Raw
    $envContent = $envContent -replace "(?m)^JIM_LOG_LEVEL=.*", "JIM_LOG_LEVEL=$($script:OriginalLogLevel)"
    $envContent | Set-Content $envFilePath -NoNewline
    Write-Step "Restored JIM_LOG_LEVEL=$($script:OriginalLogLevel) in .env"
}

# Step 7: Docker Cleanup
$step7Start = Get-Date
Write-Section "Step 7: Docker Cleanup"

Write-Step "Pruning unused images and build cache (preserving snapshots)..."
$imagePrune = Invoke-ImagePrunePreservingSnapshots
$builderPrune = docker builder prune -af 2>&1
$imageReclaimed = $imagePrune | Select-String "Total reclaimed space:\s*(.+)"
$builderReclaimed = $builderPrune | Select-String "Total reclaimed space:\s*(.+)"
$parts = @()
if ($imageReclaimed) { $parts += "images: $($imageReclaimed.Matches[0].Groups[1].Value)" }
if ($builderReclaimed) { $parts += "build cache: $($builderReclaimed.Matches[0].Groups[1].Value)" }
if ($parts.Count -gt 0) {
    Write-Success "Reclaimed ($($parts -join ', '))"
}
else {
    Write-Success "Docker cleanup complete (nothing to reclaim)"
}

$timings["7. Docker Cleanup"] = (Get-Date) - $step7Start

# Summary
$endTime = Get-Date
$duration = $endTime - $startTime

Write-Banner "Test Run Complete"

# Performance Summary
Write-Section "Performance Summary"
Write-Host "${CYAN}Stage Timings:${NC}"

# Sort timings by key (which has stage number prefix)
$sortedTimings = $timings.GetEnumerator() | Sort-Object Name

$totalSeconds = 0
foreach ($timing in $sortedTimings) {
    $seconds = [math]::Round($timing.Value.TotalSeconds, 1)
    $totalSeconds += $seconds
    $bar = "█" * [math]::Max(0, [math]::Min(50, [math]::Floor($seconds / 2)))
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


# Output Files
Write-Section "Output Files"
Write-Host "  ${GRAY}Scenario log:${NC}       $scenarioLogFile"
if ($currentFile) {
    Write-Host "  ${GRAY}Performance metrics:${NC} $currentFile"
}
if ($volumeAuditLogPath -and (Test-Path $volumeAuditLogPath)) {
    Write-Host "  ${GRAY}Volume audit log:${NC}   $volumeAuditLogPath"
}
if ($dockerEventsLogPath -and (Test-Path $dockerEventsLogPath)) {
    Write-Host "  ${GRAY}Docker events log:${NC}  $dockerEventsLogPath"
}

# Re-run Command
Write-Section "Re-run Command"
$rerunParts = @("jim-reset && pwsh ./test/integration/Run-IntegrationTests.ps1")
$rerunParts += "-Scenario `"$Scenario`""
$rerunParts += "-Template $Template"
if ($Step -ne "All") { $rerunParts += "-Step $Step" }
$rerunParts += "-DirectoryType $DirectoryType"
if ($LogLevel) { $rerunParts += "-LogLevel $LogLevel" }
if ($DisableChangeTracking) { $rerunParts += "-DisableChangeTracking" }
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $rerunParts += "-ExportConcurrency $ExportConcurrency" }
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $rerunParts += "-MaxExportParallelism $MaxExportParallelism" }
if ($TimeoutSeconds -ne 180) { $rerunParts += "-TimeoutSeconds $TimeoutSeconds" }
if ($CaptureMetrics) { $rerunParts += "-CaptureMetrics" }
if ($IgnoreSnapshots) { $rerunParts += "-IgnoreSnapshots" }
if ($isScenario11) {
    if ($Quick)      { $rerunParts += "-Quick" }
    if ($Exhaustive) { $rerunParts += "-Exhaustive" }
    if ($PSBoundParameters.ContainsKey('OperatorFilter') -and -not [string]::IsNullOrWhiteSpace($OperatorFilter)) {
        $rerunParts += "-OperatorFilter $OperatorFilter"
    }
    if ($PSBoundParameters.ContainsKey('IncludeNegativeCells')) {
        $rerunParts += "-IncludeNegativeCells `$$IncludeNegativeCells"
    }
}
Write-Host "  $($rerunParts -join ' ')"
Write-Host ""

# Stop transcript so the total execution time and summary are captured in the log file
} finally {
    if ($transcriptActive) {
        Stop-Transcript | Out-Null
    }
}
exit $scenarioExitCode
