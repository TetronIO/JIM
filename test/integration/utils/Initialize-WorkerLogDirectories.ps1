# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Ensures the integration-test worker log directories exist and are writable before the
    Docker stack starts.

.DESCRIPTION
    docker-compose.override.yml bind-mounts ./test/integration/results/logs/worker into the
    worker container, which runs as the non-root user UID 1654 (see src/JIM.Worker/Dockerfile).

    On a Linux host, bind mounts are a direct passthrough to the host filesystem with no UID
    translation. If the bind-mount source directory does not exist when the stack starts, the
    Docker daemon (running as root) auto-creates it, and its parents, as root:root. That breaks
    two things the integration runner depends on:
      * the worker's Serilog file sink (UID 1654 cannot write into a root-owned 0755 directory,
        so no jim.worker.<date>.log is ever produced and Stream-WorkerLogs.ps1 spins to its
        timeout); and
      * Start-Transcript writing the scenario log into the parent results/logs/ directory (the
        current, non-root user is denied).

    On Docker Desktop (macOS/Windows) the file-sharing layer remaps ownership to the host user,
    so the problem does not surface there; the Linux-only chmod below is a no-op on those hosts.

    The durable prevention is to create these directories ourselves (owned by the current user)
    before any 'docker compose up'. .devcontainer/setup.sh does this once at container creation
    so it covers non-runner stack-ups (jim-stack / jim-reset / jim-build-light); this function
    covers the integration runner's own start-up and is safe to call on every run.

    A non-root caller cannot chmod (or chown) a directory the Docker daemon already created as
    root, so when repair is impossible this function fails fast with an actionable remediation
    rather than letting the run fail opaquely later.

.PARAMETER LogDirectory
    The results/logs directory (the transcript target). The 'worker' bind-mount sub-directory
    is created beneath it.
#>

function Test-PathWritable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $probe = Join-Path $Path ".jim-write-probe-$PID"
    try {
        [System.IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch {
        return $false
    }
}

function Initialize-WorkerLogDirectories {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $LogDirectory
    )

    $workerDir = Join-Path $LogDirectory 'worker'

    # Create the bind-mount source ourselves so the Docker daemon does not auto-create it as
    # root. -Force creates the parent results/logs chain too and is a no-op if it already exists.
    New-Item -ItemType Directory -Path $workerDir -Force | Out-Null

    # Make the worker directory world-writable so the non-root worker UID can write its log.
    # Tolerate failure here: if the directory is already root-owned (created by a non-runner
    # stack-up before this ran) we cannot chmod it, and the probe below reports it clearly.
    if ($IsLinux -or $IsMacOS) {
        & chmod 0777 $workerDir 2>$null
    }

    # Fail fast with an actionable error if either directory is not writable by the current
    # user. This converts an opaque downstream failure (a denied Start-Transcript, or silent
    # worker-log loss) into a clear message at the point the cause is fixable.
    foreach ($dir in @($LogDirectory, $workerDir)) {
        if (-not (Test-PathWritable -Path $dir)) {
            $resultsRoot = Split-Path -Parent $LogDirectory
            throw "Integration log directory '$dir' is not writable by the current user. " +
                  "A previous 'jim-stack' or 'jim-reset' likely let Docker create it as root. " +
                  "Fix it with:  sudo chown -R `$(id -un):`$(id -gn) '$resultsRoot'"
        }
    }
}
