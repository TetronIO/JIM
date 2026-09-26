# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Build pre-populated 389 Directory Server snapshot images for fast integration test startup

.DESCRIPTION
    Creates Docker images with test data (users, groups, memberships) already loaded.
    On subsequent test runs, the runner detects these snapshots and skips population,
    reducing startup from minutes to seconds. The 389 sibling of Build-OpenLDAPSnapshots.ps1:
    same scenarios, same populate scripts (they take -DirectoryType DirectoryServer389), same
    tag shape.

    Snapshot images are tagged per-scenario and per-size:
      - jim-dirsrv:general-{size}    (Scenarios 005, 009 and the other directory-agnostic scenarios; both suffixes populated)
      - jim-dirsrv:s8-{size}         (Scenario 008; Source populated, Target OUs only)

    Scenario 001 does not use snapshots; the target directory starts empty. Scenarios 014, 019
    and 22 stay OpenLDAP only.

    The snapshot hash (Get-DirsrvSnapshotHash, over the populate scripts and their helpers) and
    the base image's build hash are stored as labels on each image; the runner compares both
    to detect a stale snapshot. The hash, tag and currency rules live in
    docker/dirsrv/Get-DirsrvBuildHash.ps1, shared with the runner, so the two cannot drift.

    Three things differ from the OpenLDAP builder, each forced by the 389 image and explained
    where it happens: the build container runs sleep as PID 1 and starts the directory with
    docker exec (dscontainer exits when ns-slapd stops, so a clean stop would otherwise end the
    container); the Retro Changelog plug-in is disabled while populating (its records would
    roughly double the image); and the committed copy carries a provenance id that
    start-dirsrv.sh compares, because a fresh volume is seeded from the image layer, which
    for a snapshot is the base image's unpopulated instance.

.PARAMETER Scenario
    Which scenario to build snapshots for (General, Scenario-008, All)

.PARAMETER Template
    Data size template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER Force
    Rebuild even if a snapshot with matching snapshot and base hashes already exists

.EXAMPLE
    ./Build-DirsrvSnapshots.ps1 -Scenario General -Template Small

.EXAMPLE
    ./Build-DirsrvSnapshots.ps1 -Scenario All -Template Medium

.EXAMPLE
    ./Build-DirsrvSnapshots.ps1 -Scenario Scenario-008 -Template MediumLarge -Force
#>

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("General", "Scenario-008", "All")]
    [string]$Scenario = "All",

    [Parameter(Mandatory = $true)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template,

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot
$fixtureDir = "$scriptRoot/docker/dirsrv"

# Import helpers
. "$scriptRoot/utils/Test-Helpers.ps1"
. "$fixtureDir/Get-DirsrvBuildHash.ps1"

# The lab's Directory Manager credentials (published in utils/Test-Helpers.ps1; the
# compose service sets the same DS_DM_PASSWORD). The bind proves the instance is
# serving, not just that dscontainer's healthcheck passes.
$directoryManagerDn = "cn=Directory Manager"
$directoryManagerPassword = "Test@123!"
$ldapUri = "ldap://localhost:3389"
$dscontainer = "/usr/lib/dirsrv/dscontainer"
$startScript = "/usr/local/bin/start-dirsrv.sh"
# docker logs shows only PID 1 (sleep), so every start of the directory appends its
# output here for Wait-DirsrvReady to dump on failure.
$startLog = "/tmp/jim-dirsrv-start.log"

# ============================================================================
# Instance lifecycle inside the build container
# ============================================================================

function Start-DirsrvInstance {
    <#
    .SYNOPSIS
        Start the directory in the build container with the image's own entry point, detached.
    #>
    param([string]$ContainerName)

    # start-dirsrv.sh applies the provenance rule and execs dscontainer, exactly as a
    # container started from the image would; running it under docker exec rather
    # than as PID 1 is what lets the container outlive a clean stop (see Stop-DirsrvInstance).
    docker exec -d $ContainerName bash -c "$startScript >>$startLog 2>&1"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start the directory in $ContainerName"
    }
}

function Wait-DirsrvReady {
    <#
    .SYNOPSIS
        Wait until dscontainer's healthcheck passes and the Directory Manager can bind.
    #>
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds = 180
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # -H is the image's own healthcheck (LDAPI as root). The bind on top of it proves
        # the Directory Manager password has been applied, which dscontainer does a
        # moment after the healthcheck first passes (the same pair build/configure.sh uses).
        docker exec $ContainerName $dscontainer -H 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            docker exec $ContainerName ldapwhoami -x -H $ldapUri -D $directoryManagerDn -w $directoryManagerPassword 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { return }
        }
        Start-Sleep -Seconds 2
    }

    Write-Host "  Directory start log ($startLog) and error log:" -ForegroundColor Red
    docker exec $ContainerName bash -c "tail -n 50 $startLog 2>/dev/null; tail -n 50 /data/logs/errors 2>/dev/null"
    docker logs --tail 50 $ContainerName
    docker rm -f $ContainerName | Out-Null
    throw "389 Directory Server did not become ready in $ContainerName within ${TimeoutSeconds}s"
}

function Stop-DirsrvInstance {
    <#
    .SYNOPSIS
        Ask dscontainer for a clean ns-slapd shutdown and wait until ns-slapd has exited.
    #>
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds = 60
    )

    # dscontainer exits when ns-slapd stops. Had dscontainer been PID 1, this stop would
    # end the container too (and docker cp into a stopped container's /data.provisioned
    # hung when streamed); with sleep as PID 1 the container survives and the copy can be
    # made in-container from a cleanly closed database.
    $output = docker exec $ContainerName $dscontainer -s 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dscontainer -s exited with $LASTEXITCODE in ${ContainerName}: $output"
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        docker exec $ContainerName pgrep -x ns-slapd 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { return }
        Start-Sleep -Seconds 1
    }
    docker rm -f $ContainerName | Out-Null
    throw "ns-slapd was still running in $ContainerName ${TimeoutSeconds}s after a clean stop was requested"
}

function Restart-DirsrvInstance {
    param([string]$ContainerName)
    Stop-DirsrvInstance -ContainerName $ContainerName
    Start-DirsrvInstance -ContainerName $ContainerName
    Wait-DirsrvReady -ContainerName $ContainerName
}

function Set-DirsrvRetroChangelog {
    <#
    .SYNOPSIS
        Enable or disable the Retro Changelog plug-in; the change takes effect on the next start.
    #>
    param(
        [string]$ContainerName,
        [ValidateSet("enable", "disable")]
        [string]$State
    )

    docker exec $ContainerName dsconf localhost plugin retro-changelog $State 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($LASTEXITCODE -ne 0) {
        docker rm -f $ContainerName | Out-Null
        throw "dsconf could not $State the Retro Changelog plug-in in $ContainerName"
    }
}

# ============================================================================
# Snapshot build
# ============================================================================

function Build-DirsrvSnapshot {
    <#
    .SYNOPSIS
        Start a base 389 container, populate it, and commit as a snapshot image.
    #>
    param(
        [string]$BaseImage,
        [string]$ContainerName,
        [string]$SnapshotTag,
        [string]$SnapshotHash,
        [scriptblock]$PopulateAction
    )

    $startTime = Get-Date

    # Clean up any existing container
    docker rm -f $ContainerName 2>$null | Out-Null

    # sleep is PID 1, not dscontainer: dscontainer exits when ns-slapd stops, and the
    # commit below needs a container that is still alive after a clean stop. It is the
    # image's CMD that is replaced, never its (absent) ENTRYPOINT: docker commit's
    # --change 'ENTRYPOINT []' does not reset an entrypoint, so a snapshot made with
    # --entrypoint sleep would run "sleep /usr/local/bin/start-dirsrv.sh". --init puts
    # a reaper in front of sleep so nothing the exec'd dscontainer leaves behind lingers
    # as a zombie; it is a run-time flag and is not recorded in the committed image.
    Write-Host "  Starting base container ($BaseImage)..." -ForegroundColor Gray
    docker run -d --init `
        --name $ContainerName `
        -e "DS_DM_PASSWORD=$directoryManagerPassword" `
        $BaseImage `
        sleep infinity | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start container $ContainerName"
    }

    Write-Host "  Starting the directory and waiting for it to be ready..." -ForegroundColor Gray
    Start-DirsrvInstance -ContainerName $ContainerName
    Wait-DirsrvReady -ContainerName $ContainerName
    Write-Host "  389 Directory Server ready" -ForegroundColor Green

    # Populate with the Retro Changelog plug-in off. The changelog records every populated
    # entry with its full LDIF, which would roughly double the image, and it would only be
    # trimmed (7 day maximum age) on a later start. JIM takes its changelog watermark at
    # Full Import, so a snapshot whose changelog starts empty loses nothing for the
    # scenarios. The plug-in is re-enabled before the commit so the committed instance
    # matches the base image's configuration. Each toggle takes effect on restart.
    Write-Host "  Disabling the Retro Changelog plug-in for population (restart)..." -ForegroundColor Gray
    Set-DirsrvRetroChangelog -ContainerName $ContainerName -State disable
    Restart-DirsrvInstance -ContainerName $ContainerName

    # Run the populate action
    Write-Host "  Populating test data..." -ForegroundColor Gray
    & $PopulateAction

    Write-Host "  Re-enabling the Retro Changelog plug-in (restart)..." -ForegroundColor Gray
    Set-DirsrvRetroChangelog -ContainerName $ContainerName -State enable
    Restart-DirsrvInstance -ContainerName $ContainerName

    # Stop cleanly before copying, so the copy is of a closed database rather than a
    # live one, then make the copy in-container: /data is a Docker volume and docker
    # commit does not capture volumes, so a snapshot started against a fresh /data
    # volume restores from /data.provisioned (see start-dirsrv.sh). Runtime files and
    # logs are dropped from the copy exactly as the image build's finalise does.
    Write-Host "  Stopping the directory cleanly before the copy..." -ForegroundColor Gray
    Stop-DirsrvInstance -ContainerName $ContainerName

    Write-Host "  Copying /data to /data.provisioned for commit..." -ForegroundColor Gray
    docker exec $ContainerName bash -c 'rm -rf /data.provisioned && cp -a /data /data.provisioned && rm -rf /data.provisioned/run/* /data.provisioned/logs/*' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        docker rm -f $ContainerName | Out-Null
        throw "Failed to copy /data to /data.provisioned in $ContainerName"
    }

    # The provenance id. The image layer's /data (which seeds a fresh named volume)
    # still carries the BASE image's id, so start-dirsrv.sh sees a mismatch on first
    # start and restores this populated copy; a restarted container already carries
    # this id and keeps its data. Verified straight back, because a wrong id here
    # would silently serve the unpopulated base instance.
    $provisionedId = "snapshot:${SnapshotTag}:${SnapshotHash}"
    docker exec $ContainerName bash -c "printf '%s' '$provisionedId' > /data.provisioned/.jim-provisioned-id" 2>&1 | Out-Null
    $writtenId = docker exec $ContainerName cat /data.provisioned/.jim-provisioned-id 2>&1
    if ($LASTEXITCODE -ne 0 -or "$writtenId" -ne $provisionedId) {
        docker rm -f $ContainerName | Out-Null
        throw "The provenance id was not written to /data.provisioned in $ContainerName (read back: '$writtenId')"
    }

    # Stop and commit
    Write-Host "  Stopping container..." -ForegroundColor Gray
    docker stop $ContainerName | Out-Null

    # Record which base image build this snapshot was baked from. The snapshot captures
    # the base's configured instance (schema, suffixes, ACIs, plug-ins), so a snapshot
    # from a stale base stays stale even after the base image on disk is rebuilt;
    # Test-DirsrvSnapshotCurrent compares this label to detect that.
    $baseBuildHash = docker image inspect $BaseImage --format '{{index .Config.Labels "jim.dirsrv.build-hash"}}' 2>$null

    # CMD only: the image has no ENTRYPOINT to reset (see the docker run above), and the
    # container's own command is sleep, which must not be what the snapshot runs.
    Write-Host "  Committing as $SnapshotTag..." -ForegroundColor Gray
    docker commit `
        --change "LABEL jim.dirsrv.snapshot-hash=$SnapshotHash" `
        --change "LABEL jim.dirsrv.base-hash=$baseBuildHash" `
        --change "LABEL jim.dirsrv.snapshot-template=$Template" `
        --change "LABEL jim.dirsrv.snapshot-date=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))" `
        --change 'CMD ["/usr/local/bin/start-dirsrv.sh"]' `
        $ContainerName `
        $SnapshotTag | Out-Null

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
Write-Host " Building 389 Directory Server Snapshot Images" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Scenario: $Scenario" -ForegroundColor Gray
Write-Host "  Template: $Template" -ForegroundColor Gray
Write-Host ""

$baseImage = "ghcr.io/tetronio/jim-dirsrv:primary"

# The base image is current when its jim.dirsrv.build-hash label equals the hash of
# the fixture directory now; Get-DirsrvBuildHash is the same function the build
# script and the runner use, so there is no second file list to keep in step.
$expectedBuildHash = Get-DirsrvBuildHash -FixtureDirectory $fixtureDir

# A stale base image carries an outdated configured instance (schema, ACIs,
# plug-in settings, provenance stamp) that a snapshot would bake in for good.
$needsBaseRebuild = $false
$baseInspect = docker image inspect $baseImage --format '{{index .Config.Labels "jim.dirsrv.build-hash"}}' 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Base image $baseImage not found locally; building..." -ForegroundColor Yellow
    $needsBaseRebuild = $true
} elseif ("$baseInspect" -ne $expectedBuildHash) {
    Write-Host "  Base image $baseImage is stale (build hash $baseInspect != $expectedBuildHash); rebuilding..." -ForegroundColor Yellow
    $needsBaseRebuild = $true
}

if ($needsBaseRebuild) {
    & "$fixtureDir/Build-DirsrvImage.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Failed to build base 389 Directory Server image" }
    # Force snapshot rebuild since the base image changed
    $Force = $true
}

$scenariosToProcess = if ($Scenario -eq "All") { @("General", "Scenario-008") } else { @($Scenario) }

foreach ($scen in $scenariosToProcess) {
    $snapshotHash = Get-DirsrvSnapshotHash -Scenario $scen -IntegrationRoot $scriptRoot

    Write-Host "---------------------------------------------" -ForegroundColor Yellow
    Write-Host " $scen (hash: $snapshotHash)" -ForegroundColor Yellow
    Write-Host "---------------------------------------------" -ForegroundColor Yellow

    switch ($scen) {
        "General" {
            $tag = Get-DirsrvSnapshotImageTag -Role "general" -Template $Template

            if (-not $Force -and (Test-DirsrvSnapshotCurrent -ImageTag $tag -ExpectedSnapshotHash $snapshotHash -ExpectedBaseHash $expectedBuildHash)) {
                Write-Host "  Snapshot $tag is up to date; skipping" -ForegroundColor Green
                continue
            }

            Build-DirsrvSnapshot `
                -BaseImage $baseImage `
                -ContainerName "dirsrv-snapshot-general" `
                -SnapshotTag $tag `
                -SnapshotHash $snapshotHash `
                -PopulateAction {
                    & "$scriptRoot/Populate-OpenLDAP.ps1" -Template $Template -DirectoryType DirectoryServer389 -Container "dirsrv-snapshot-general"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP.ps1 failed" }
                }

            Write-Host ""
        }

        "Scenario-008" {
            $tag = Get-DirsrvSnapshotImageTag -Role "s8" -Template $Template

            if (-not $Force -and (Test-DirsrvSnapshotCurrent -ImageTag $tag -ExpectedSnapshotHash $snapshotHash -ExpectedBaseHash $expectedBuildHash)) {
                Write-Host "  Snapshot $tag is up to date; skipping" -ForegroundColor Green
                continue
            }

            Build-DirsrvSnapshot `
                -BaseImage $baseImage `
                -ContainerName "dirsrv-snapshot-s8" `
                -SnapshotTag $tag `
                -SnapshotHash $snapshotHash `
                -PopulateAction {
                    & "$scriptRoot/Populate-OpenLDAP-Scenario-008.ps1" -Template $Template -Instance Source -DirectoryType DirectoryServer389 -Container "dirsrv-snapshot-s8"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP-Scenario-008.ps1 (Source) failed" }
                    & "$scriptRoot/Populate-OpenLDAP-Scenario-008.ps1" -Template $Template -Instance Target -DirectoryType DirectoryServer389 -Container "dirsrv-snapshot-s8"
                    if ($LASTEXITCODE -ne 0) { throw "Populate-OpenLDAP-Scenario-008.ps1 (Target) failed" }
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
            Write-Host "  $(Get-DirsrvSnapshotImageTag -Role 'general' -Template $Template)" -ForegroundColor Gray
        }
        "Scenario-008" {
            Write-Host "  $(Get-DirsrvSnapshotImageTag -Role 's8' -Template $Template)" -ForegroundColor Gray
        }
    }
}
Write-Host ""
Write-Host "The integration test runner will automatically detect and use these snapshots." -ForegroundColor Yellow
Write-Host "To force a fresh population, run tests with -IgnoreSnapshots" -ForegroundColor Yellow
