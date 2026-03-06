<#
.SYNOPSIS
    Build pre-populated Samba AD snapshot images for fast integration test startup

.DESCRIPTION
    Creates Docker images with test data (users, groups, memberships) already loaded.
    On subsequent test runs, the runner detects these snapshots and skips population,
    reducing startup from minutes to seconds.

    Snapshot images are tagged per-scenario and per-size:
      - jim-samba-ad:primary-{size}      (Scenarios 1, 5)
      - jim-samba-ad:source-s8-{size}    (Scenario 8 source)
      - jim-samba-ad:target-s8-{size}    (Scenario 8 target)

    A content hash label is stored on each image, computed from the populate scripts.
    The test runner compares this hash to detect stale snapshots that need rebuilding.

.PARAMETER Scenario
    Which scenario to build snapshots for (Scenario1, Scenario8, All)

.PARAMETER Template
    Data size template (Nano, Micro, Small, Medium, MediumLarge, Large, XLarge, XXLarge)

.PARAMETER Registry
    Container registry prefix (default: local, no registry prefix)

.PARAMETER Force
    Rebuild even if a snapshot with matching content hash already exists

.EXAMPLE
    ./Build-SambaSnapshots.ps1 -Scenario Scenario1 -Template Small

.EXAMPLE
    ./Build-SambaSnapshots.ps1 -Scenario All -Template Medium

.EXAMPLE
    ./Build-SambaSnapshots.ps1 -Scenario Scenario8 -Template XLarge -Force
#>

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Scenario1", "Scenario8", "All")]
    [string]$Scenario = "All",

    [Parameter(Mandatory = $true)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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

function Get-PopulateScriptHash {
    <#
    .SYNOPSIS
        Compute a content hash of the populate scripts that affect snapshot contents.
        Used to detect when snapshots are stale and need rebuilding.
    #>
    param([string]$ScenarioName)

    $filesToHash = @(
        "$scriptRoot/utils/Test-Helpers.ps1",
        "$scriptRoot/utils/Test-GroupHelpers.ps1"
    )

    switch ($ScenarioName) {
        "Scenario1" {
            $filesToHash += "$scriptRoot/Populate-SambaAD.ps1"
        }
        "Scenario8" {
            $filesToHash += "$scriptRoot/Populate-SambaAD-Scenario8.ps1"
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

function Get-SnapshotImageTag {
    param(
        [string]$Role,
        [string]$Size
    )
    $sizeLower = $Size.ToLower()
    $prefix = if ($Registry) { "${Registry}/" } else { "" }
    return "${prefix}jim-samba-ad:${Role}-${sizeLower}"
}

function Test-SnapshotCurrent {
    <#
    .SYNOPSIS
        Check if a snapshot image exists and has a matching content hash.
    #>
    param(
        [string]$ImageTag,
        [string]$ExpectedHash
    )

    $inspect = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.samba.snapshot-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    return "$inspect" -eq $ExpectedHash
}

function Build-Snapshot {
    <#
    .SYNOPSIS
        Start a base Samba container, populate it, and commit as a snapshot image.
    #>
    param(
        [string]$BaseImage,
        [string]$ContainerName,
        [string]$SnapshotTag,
        [string]$ContentHash,
        [hashtable]$EnvVars,
        [scriptblock]$PopulateAction,
        [string]$MemoryLimit = "2G"
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
        --privileged `
        --memory $MemoryLimit `
        @envArgs `
        $BaseImage

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start container $ContainerName"
    }

    # Wait for Samba to be ready
    Write-Host "  Waiting for Samba to be ready..." -ForegroundColor Gray
    $timeout = 120
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        $health = docker exec $ContainerName /usr/local/samba/bin/smbclient -L localhost -U% -N 2>&1
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 5
        $elapsed += 5
    }
    if ($elapsed -ge $timeout) {
        docker logs --tail 50 $ContainerName
        docker rm -f $ContainerName | Out-Null
        throw "Samba did not become ready in $ContainerName within ${timeout}s"
    }
    Write-Host "  Samba ready" -ForegroundColor Green

    # Run the populate action
    Write-Host "  Populating test data..." -ForegroundColor Gray
    & $PopulateAction

    # Copy volume data to backup locations (volumes aren't captured by docker commit)
    Write-Host "  Backing up volume data for commit..." -ForegroundColor Gray
    docker exec $ContainerName bash -c "cp -a /usr/local/samba/etc /usr/local/samba/etc.provisioned" 2>&1 | Out-Null
    docker exec $ContainerName bash -c "cp -a /usr/local/samba/private /usr/local/samba/private.provisioned" 2>&1 | Out-Null
    docker exec $ContainerName bash -c "cp -a /usr/local/samba/var /usr/local/samba/var.provisioned" 2>&1 | Out-Null

    # Ensure start-samba.sh exists (it restores volume data on startup)
    $startScript = docker exec $ContainerName test -f /start-samba.sh 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Copy from the prebuilt docker directory
        $startSambaPath = "$scriptRoot/docker/samba-ad-prebuilt/start-samba.sh"
        if (Test-Path $startSambaPath) {
            docker cp $startSambaPath "${ContainerName}:/start-samba.sh"
            docker exec $ContainerName chmod +x /start-samba.sh
        }
        else {
            Write-Warning "start-samba.sh not found at $startSambaPath — snapshot may not start correctly"
        }
    }

    # Stop and commit
    Write-Host "  Stopping container..." -ForegroundColor Gray
    docker stop $ContainerName | Out-Null

    Write-Host "  Committing as $SnapshotTag..." -ForegroundColor Gray
    docker commit `
        --change "LABEL jim.samba.snapshot-hash=$ContentHash" `
        --change "LABEL jim.samba.snapshot-template=$Template" `
        --change "LABEL jim.samba.snapshot-date=$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')" `
        --change 'CMD ["/start-samba.sh"]' `
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
Write-Host " Building Samba AD Snapshot Images" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Scenario: $Scenario" -ForegroundColor Gray
Write-Host "  Template: $Template" -ForegroundColor Gray
Write-Host ""

$scenariosToProcess = if ($Scenario -eq "All") { @("Scenario1", "Scenario8") } else { @($Scenario) }

foreach ($scen in $scenariosToProcess) {
    $contentHash = Get-PopulateScriptHash -ScenarioName $scen

    Write-Host "---------------------------------------------" -ForegroundColor Yellow
    Write-Host " $scen (hash: $contentHash)" -ForegroundColor Yellow
    Write-Host "---------------------------------------------" -ForegroundColor Yellow

    switch ($scen) {
        "Scenario1" {
            $tag = Get-SnapshotImageTag -Role "primary" -Size $Template

            if (-not $Force -and (Test-SnapshotCurrent -ImageTag $tag -ExpectedHash $contentHash)) {
                Write-Host "  Snapshot $tag is up to date — skipping" -ForegroundColor Green
                continue
            }

            $baseImage = "ghcr.io/tetronio/jim-samba-ad:primary"
            # Check if local base image exists
            docker image inspect $baseImage 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  Base image $baseImage not found locally — building..." -ForegroundColor Yellow
                & "$scriptRoot/docker/samba-ad-prebuilt/Build-SambaImages.ps1" -Images Primary
            }

            Build-Snapshot `
                -BaseImage $baseImage `
                -ContainerName "samba-snapshot-primary" `
                -SnapshotTag $tag `
                -ContentHash $contentHash `
                -EnvVars @{
                    REALM = "SUBATOMIC.LOCAL"
                    DOMAIN = "SUBATOMIC"
                    ADMIN_PASS = "Test@123!"
                    DNS_FORWARDER = "8.8.8.8"
                } `
                -PopulateAction {
                    & "$scriptRoot/Populate-SambaAD.ps1" -Template $Template -Instance Primary
                    if ($LASTEXITCODE -ne 0) { throw "Populate-SambaAD.ps1 failed" }
                }

            Write-Host ""
        }

        "Scenario8" {
            $sourceTag = Get-SnapshotImageTag -Role "source-s8" -Size $Template
            $targetTag = Get-SnapshotImageTag -Role "target-s8" -Size $Template

            # Source
            if (-not $Force -and (Test-SnapshotCurrent -ImageTag $sourceTag -ExpectedHash $contentHash)) {
                Write-Host "  Snapshot $sourceTag is up to date — skipping" -ForegroundColor Green
            }
            else {
                $baseImage = "ghcr.io/tetronio/jim-samba-ad:source"
                docker image inspect $baseImage 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "  Base image $baseImage not found locally — building..." -ForegroundColor Yellow
                    & "$scriptRoot/docker/samba-ad-prebuilt/Build-SambaImages.ps1" -Images Source
                }

                $memLimit = if ($Template -in @("XLarge", "XXLarge")) { "8G" } else { "2G" }

                Build-Snapshot `
                    -BaseImage $baseImage `
                    -ContainerName "samba-snapshot-source" `
                    -SnapshotTag $sourceTag `
                    -ContentHash $contentHash `
                    -MemoryLimit $memLimit `
                    -EnvVars @{
                        REALM = "SOURCEDOMAIN.LOCAL"
                        DOMAIN = "SOURCEDOMAIN"
                        ADMIN_PASS = "Test@123!"
                        DNS_FORWARDER = "8.8.8.8"
                    } `
                    -PopulateAction {
                        & "$scriptRoot/Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Source
                        if ($LASTEXITCODE -ne 0) { throw "Populate-SambaAD-Scenario8.ps1 (Source) failed" }
                    }
            }

            # Target (just OUs — very fast, but still worth snapshotting for consistency)
            if (-not $Force -and (Test-SnapshotCurrent -ImageTag $targetTag -ExpectedHash $contentHash)) {
                Write-Host "  Snapshot $targetTag is up to date — skipping" -ForegroundColor Green
            }
            else {
                $baseImage = "ghcr.io/tetronio/jim-samba-ad:target"
                docker image inspect $baseImage 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "  Base image $baseImage not found locally — building..." -ForegroundColor Yellow
                    & "$scriptRoot/docker/samba-ad-prebuilt/Build-SambaImages.ps1" -Images Target
                }

                Build-Snapshot `
                    -BaseImage $baseImage `
                    -ContainerName "samba-snapshot-target" `
                    -SnapshotTag $targetTag `
                    -ContentHash $contentHash `
                    -EnvVars @{
                        REALM = "TARGETDOMAIN.LOCAL"
                        DOMAIN = "TARGETDOMAIN"
                        ADMIN_PASS = "Test@123!"
                        DNS_FORWARDER = "8.8.8.8"
                    } `
                    -PopulateAction {
                        & "$scriptRoot/Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Target
                        if ($LASTEXITCODE -ne 0) { throw "Populate-SambaAD-Scenario8.ps1 (Target) failed" }
                    }
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
        "Scenario1" {
            $tag = Get-SnapshotImageTag -Role "primary" -Size $Template
            Write-Host "  $tag" -ForegroundColor Gray
        }
        "Scenario8" {
            Write-Host "  $(Get-SnapshotImageTag -Role 'source-s8' -Size $Template)" -ForegroundColor Gray
            Write-Host "  $(Get-SnapshotImageTag -Role 'target-s8' -Size $Template)" -ForegroundColor Gray
        }
    }
}
Write-Host ""
Write-Host "The integration test runner will automatically detect and use these snapshots." -ForegroundColor Yellow
Write-Host "To force a fresh population, run tests with -IgnoreSnapshots" -ForegroundColor Yellow
