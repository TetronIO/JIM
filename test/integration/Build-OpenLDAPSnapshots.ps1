# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Build pre-populated OpenLDAP snapshot images for fast integration test startup

.DESCRIPTION
    Creates Docker images with test data (users, groups, memberships) already loaded.
    On subsequent test runs, the runner detects these snapshots and skips population,
    reducing startup from minutes to seconds.

    Snapshot images are tagged per-scenario and per-size:
      - jim-openldap:general-{size}    (Scenarios 5, 9 — both suffixes populated)
      - jim-openldap:s8-{size}         (Scenario 8 — Source populated, Target OUs only)

    Scenario 1 does not use OpenLDAP snapshots — the target directory starts empty.

    A content hash label is stored on each image, computed from the populate scripts.
    The test runner compares this hash to detect stale snapshots that need rebuilding.

.PARAMETER Scenario
    Which scenario to build snapshots for (General, Scenario8, All)

.PARAMETER Template
    Data size template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER Registry
    Container registry prefix (default: local, no registry prefix)

.PARAMETER Force
    Rebuild even if a snapshot with matching content hash already exists

.EXAMPLE
    ./Build-OpenLDAPSnapshots.ps1 -Scenario General -Template Small

.EXAMPLE
    ./Build-OpenLDAPSnapshots.ps1 -Scenario All -Template Medium

.EXAMPLE
    ./Build-OpenLDAPSnapshots.ps1 -Scenario Scenario8 -Template MediumLarge -Force
#>

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("General", "Scenario8", "All")]
    [string]$Scenario = "All",

    [Parameter(Mandatory = $true)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template,

    [Parameter(Mandatory = $false)]
    [string]$Registry = "",

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot

# Import helpers
. "$scriptRoot/utils/Test-Helpers.ps1"

# ============================================================================
# Content hash computation
# ============================================================================

function Get-OpenLDAPPopulateScriptHash {
    <#
    .SYNOPSIS
        Compute a content hash of the populate scripts that affect snapshot contents.
        Used to detect when snapshots are stale and need rebuilding.
    #>
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
        "General" {
            $filesToHash += "$scriptRoot/Populate-OpenLDAP.ps1"
        }
        "Scenario8" {
            $filesToHash += "$scriptRoot/Populate-OpenLDAP-Scenario8.ps1"
        }
    }

    $combinedContent = ""
    foreach ($file in $filesToHash) {
        if (Test-Path $file) {
            $combinedContent += Get-Content -Path $file -Raw
        }
    }

    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($combinedContent)
    )
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 16).ToLower()
}

function Get-OpenLDAPSnapshotImageTag {
    param(
        [string]$Role,
        [string]$Size
    )
    $sizeLower = $Size.ToLower()
    $prefix = if ($Registry) { "${Registry}/" } else { "" }
    return "${prefix}jim-openldap:${Role}-${sizeLower}"
}

function Test-OpenLDAPSnapshotCurrent {
    <#
    .SYNOPSIS
        Check if a snapshot image exists, has a matching content hash, and was baked
        from the same base image build we would use now.
    #>
    param(
        [string]$ImageTag,
        [string]$ExpectedHash,
        [string]$BaseImage
    )

    $inspect = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.openldap.snapshot-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    if ("$inspect" -ne $ExpectedHash) {
        return $false
    }

    # Snapshots capture the base image's init state (schema, suffixes, accesslog config),
    # so rebuilding the base does not refresh existing snapshots. Compare the base build
    # the snapshot was baked from against the base we would build from now; snapshots
    # without the base-hash label predate this check and are treated as stale.
    $snapshotBaseHash = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.openldap.base-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    $baseBuildHash = docker image inspect $BaseImage --format '{{index .Config.Labels "jim.openldap.build-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    return "$snapshotBaseHash" -eq "$baseBuildHash"
}

function Build-OpenLDAPSnapshot {
    <#
    .SYNOPSIS
        Start a base OpenLDAP container, populate it, and commit as a snapshot image.
    #>
    param(
        [string]$BaseImage,
        [string]$ContainerName,
        [string]$SnapshotTag,
        [string]$ContentHash,
        [hashtable]$EnvVars,
        [scriptblock]$PopulateAction
    )

    $startTime = Get-Date

    # Clean up any existing container
    docker rm -f $ContainerName 2>$null | Out-Null

    Write-Host "  Starting base container ($BaseImage)..." -ForegroundColor Gray
    $envArgs = @()
    foreach ($key in $EnvVars.Keys) {
        $envArgs += "-e"
        $envArgs += "${key}=$($EnvVars[$key])"
    }

    docker run -d `
        --name $ContainerName `
        @envArgs `
        $BaseImage

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start container $ContainerName"
    }

    # Wait for OpenLDAP to be ready (both suffixes)
    Write-Host "  Waiting for OpenLDAP to be ready..." -ForegroundColor Gray
    $timeout = 120
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        # Check that both suffixes are accessible
        $yellowstoneReady = docker exec $ContainerName ldapsearch -x -H ldap://localhost:1389 `
            -b "dc=yellowstone,dc=local" -D "cn=admin,dc=yellowstone,dc=local" -w "Test@123!" `
            -LLL "(objectClass=organizationalUnit)" 2>&1
        $yellowstoneOk = ($LASTEXITCODE -eq 0)

        if ($yellowstoneOk) {
            $glitterbandReady = docker exec $ContainerName ldapsearch -x -H ldap://localhost:1389 `
                -b "dc=glitterband,dc=local" -D "cn=admin,dc=glitterband,dc=local" -w "Test@123!" `
                -LLL "(objectClass=organizationalUnit)" 2>&1
            $glitterbandOk = ($LASTEXITCODE -eq 0)

            if ($glitterbandOk) { break }
        }

        Start-Sleep -Seconds 5
        $elapsed += 5
    }
    if ($elapsed -ge $timeout) {
        docker logs --tail 50 $ContainerName
        docker rm -f $ContainerName | Out-Null
        throw "OpenLDAP did not become ready in $ContainerName within ${timeout}s"
    }
    Write-Host "  OpenLDAP ready (both suffixes)" -ForegroundColor Green

    # Run the populate action
    Write-Host "  Populating test data..." -ForegroundColor Gray
    & $PopulateAction

    # Copy volume data to backup location (volumes aren't captured by docker commit)
    Write-Host "  Backing up volume data for commit..." -ForegroundColor Gray
    docker exec $ContainerName bash -c "cp -a /bitnami/openldap /bitnami/openldap.provisioned" 2>&1 | Out-Null

    # Drop the accesslog MDB from the provisioned copy. start-openldap.sh deletes it
    # on every boot anyway (stale accesslog data is never reusable across slapd
    # lifetimes), and at scale it is huge: population writes alone grow it to ~8GB
    # at Scale200k10kGroups, which would otherwise be baked into the image and
    # copied back out on every container start for no benefit.
    docker exec $ContainerName bash -c "rm -f /bitnami/openldap.provisioned/data/accesslog/*.mdb" 2>&1 | Out-Null

    # Copy the start-openldap.sh script into the container
    $startScript = docker exec $ContainerName test -f /start-openldap.sh 2>&1
    if ($LASTEXITCODE -ne 0) {
        $startOpenLDAPPath = "$scriptRoot/docker/openldap/start-openldap.sh"
        if (Test-Path $startOpenLDAPPath) {
            docker cp $startOpenLDAPPath "${ContainerName}:/start-openldap.sh"
        }
        else {
            Write-Warning "start-openldap.sh not found at $startOpenLDAPPath — snapshot may not start correctly"
        }
    }

    # Stop and commit
    Write-Host "  Stopping container..." -ForegroundColor Gray
    docker stop $ContainerName | Out-Null

    # Record which base image build this snapshot was baked from. The snapshot captures
    # the base's init state (schema, suffixes, accesslog config), so a snapshot from a
    # stale base stays stale even after the base image on disk is rebuilt; consumers
    # compare this label to detect that.
    $baseBuildHash = docker image inspect $BaseImage --format '{{index .Config.Labels "jim.openldap.build-hash"}}' 2>$null

    Write-Host "  Committing as $SnapshotTag..." -ForegroundColor Gray
    docker commit `
        --change "LABEL jim.openldap.snapshot-hash=$ContentHash" `
        --change "LABEL jim.openldap.base-hash=$baseBuildHash" `
        --change "LABEL jim.openldap.snapshot-template=$Template" `
        --change "LABEL jim.openldap.snapshot-date=$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')" `
        --change 'CMD ["/start-openldap.sh"]' `
        $ContainerName `
        $SnapshotTag

    if ($LASTEXITCODE -ne 0) {
        docker rm -f $ContainerName | Out-Null
        throw "Failed to commit snapshot $SnapshotTag"
    }

    docker rm -f $ContainerName | Out-Null

    $duration = ((Get-Date) - $startTime).TotalSeconds
    Write-Host "  Snapshot built: $SnapshotTag ($([Math]::Round($duration, 1))s)" -ForegroundColor Green
}

# ============================================================================
# Main
# ============================================================================

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Building OpenLDAP Snapshot Images" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Scenario: $Scenario" -ForegroundColor Gray
Write-Host "  Template: $Template" -ForegroundColor Gray
Write-Host ""

$baseImage = "ghcr.io/tetronio/jim-openldap:primary"

# Compute expected build hash for the base image from the files that affect it.
# This must match the hash computed by Build-OpenLdapImage.ps1 (same file list).
$baseFilesToHash = @(
    "$scriptRoot/docker/openldap/Dockerfile",
    "$scriptRoot/docker/openldap/scripts/01-add-second-suffix.sh",
    "$scriptRoot/docker/openldap/bootstrap/01-base-ous-yellowstone.ldif"
)
$baseCombinedContent = ($baseFilesToHash | ForEach-Object { Get-Content -Path $_ -Raw }) -join ""
$expectedBuildHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($baseCombinedContent))
).Replace("-", "").Substring(0, 16).ToLower()

# Check if base image exists and is current (build hash matches).
# A stale base image contains outdated init scripts (e.g. wrong MDB map sizes)
# that silently cause test failures even though the snapshot hash appears correct.
$needsBaseRebuild = $false
$baseInspect = docker image inspect $baseImage --format '{{index .Config.Labels "jim.openldap.build-hash"}}' 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Base image $baseImage not found locally — building..." -ForegroundColor Yellow
    $needsBaseRebuild = $true
} elseif ("$baseInspect" -ne $expectedBuildHash) {
    Write-Host "  Base image $baseImage is stale (build hash $baseInspect != $expectedBuildHash) — rebuilding..." -ForegroundColor Yellow
    $needsBaseRebuild = $true
}

if ($needsBaseRebuild) {
    & "$scriptRoot/docker/openldap/Build-OpenLdapImage.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Failed to build base OpenLDAP image" }
    # Force snapshot rebuild since the base image changed
    $Force = $true
}

$scenariosToProcess = if ($Scenario -eq "All") { @("General", "Scenario8") } else { @($Scenario) }

# OpenLDAP environment variables (matching test/integration/docker/docker-compose.integration-tests.yml)
$openLDAPEnv = @{
    LDAP_ROOT                   = "dc=yellowstone,dc=local"
    LDAP_ADMIN_USERNAME         = "admin"
    LDAP_ADMIN_PASSWORD         = "Test@123!"
    LDAP_CONFIG_ADMIN_ENABLED   = "yes"
    LDAP_CONFIG_ADMIN_USERNAME  = "admin"
    LDAP_CONFIG_ADMIN_PASSWORD  = "Test@123!"
    LDAP_ENABLE_TLS             = "no"
    LDAP_SKIP_DEFAULT_TREE      = "no"
    LDAP_ENABLE_ACCESSLOG       = "yes"
    # Snapshot population is bulk test-data loading, never customer-representative, so
    # always build with relaxed (nosync) MDB durability for speed. The snapshot itself is
    # mode-agnostic: start-openldap.sh reconciles the nosync flags on every container
    # start from LDAP_TEST_FAST_WRITES (see the runner's -DurableDirectoryWrites switch).
    LDAP_TEST_FAST_WRITES       = "yes"
}

foreach ($scen in $scenariosToProcess) {
    $contentHash = Get-OpenLDAPPopulateScriptHash -ScenarioName $scen

    Write-Host "---------------------------------------------" -ForegroundColor Yellow
    Write-Host " $scen (hash: $contentHash)" -ForegroundColor Yellow
    Write-Host "---------------------------------------------" -ForegroundColor Yellow

    switch ($scen) {
        "General" {
            $tag = Get-OpenLDAPSnapshotImageTag -Role "general" -Size $Template

            if (-not $Force -and (Test-OpenLDAPSnapshotCurrent -ImageTag $tag -ExpectedHash $contentHash -BaseImage $baseImage)) {
                Write-Host "  Snapshot $tag is up to date — skipping" -ForegroundColor Green
                continue
            }

            Build-OpenLDAPSnapshot `
                -BaseImage $baseImage `
                -ContainerName "openldap-snapshot-general" `
                -SnapshotTag $tag `
                -ContentHash $contentHash `
                -EnvVars $openLDAPEnv `
                -PopulateAction {
                    & "$scriptRoot/Populate-OpenLDAP.ps1" -Template $Template -Container "openldap-snapshot-general"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP.ps1 failed" }
                }

            Write-Host ""
        }

        "Scenario8" {
            $tag = Get-OpenLDAPSnapshotImageTag -Role "s8" -Size $Template

            if (-not $Force -and (Test-OpenLDAPSnapshotCurrent -ImageTag $tag -ExpectedHash $contentHash -BaseImage $baseImage)) {
                Write-Host "  Snapshot $tag is up to date — skipping" -ForegroundColor Green
                continue
            }

            Build-OpenLDAPSnapshot `
                -BaseImage $baseImage `
                -ContainerName "openldap-snapshot-s8" `
                -SnapshotTag $tag `
                -ContentHash $contentHash `
                -EnvVars $openLDAPEnv `
                -PopulateAction {
                    & "$scriptRoot/Populate-OpenLDAP-Scenario8.ps1" -Template $Template -Instance Source -Container "openldap-snapshot-s8"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP-Scenario8.ps1 (Source) failed" }
                    & "$scriptRoot/Populate-OpenLDAP-Scenario8.ps1" -Template $Template -Instance Target -Container "openldap-snapshot-s8"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP-Scenario8.ps1 (Target) failed" }
                }

            Write-Host ""
        }
    }
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Snapshot Build Complete" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Available snapshots:" -ForegroundColor Gray
foreach ($scen in $scenariosToProcess) {
    switch ($scen) {
        "General" {
            $tag = Get-OpenLDAPSnapshotImageTag -Role "general" -Size $Template
            Write-Host "  $tag" -ForegroundColor Gray
        }
        "Scenario8" {
            Write-Host "  $(Get-OpenLDAPSnapshotImageTag -Role 's8' -Size $Template)" -ForegroundColor Gray
        }
    }
}
Write-Host ""
Write-Host "The integration test runner will automatically detect and use these snapshots." -ForegroundColor Yellow
Write-Host "To force a fresh population, run tests with -IgnoreSnapshots" -ForegroundColor Yellow
