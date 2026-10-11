# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs one of the deployment lab's queries against the JIM installed on this host, whatever its runtime: run inside a
    lab VM, as root, by Invoke-LabCell.ps1.

.DESCRIPTION
    The lab's own checks run from the lab host, but some answers are only on the JIM host: what is in the database,
    how often JIM's containers have restarted, and whether each passes its own health check. Rootless Podman is
    driven as the documentation and the deployment-boot check drive it: as the jim account, with a clean environment
    and its own runtime folder.

.PARAMETER Action
    Sql: run -Sql in JIM's bundled database and return its rows, unaligned, one per line.
    Restarts: the total number of times JIM's containers have restarted since they were created.
    Healthy: True when every JIM container passes its own health check, else the first that does not.

.PARAMETER Runtime
    docker or podman.

.PARAMETER Rootless
    Podman only: JIM runs rootless, under the jim account.

.PARAMETER Sql
    For -Action Sql.

.EXAMPLE
    pwsh ./Invoke-LabGuestCommand.ps1 -Action Sql -Runtime podman -Rootless -Sql 'select count(*) from "Roles"'
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('Sql', 'Restarts', 'Healthy')]
    [string]$Action,

    [Parameter(Mandatory)]
    [ValidateSet('docker', 'podman')]
    [string]$Runtime,

    [switch]$Rootless,

    [string]$Sql
)

$ErrorActionPreference = 'Stop'
Set-Location /

$containers = if ($Runtime -eq 'docker') {
    @('jim.database', 'jim.web', 'jim.worker', 'jim.scheduler')
}
else {
    @('jim-database-postgres', 'jim-web', 'jim-worker', 'jim-scheduler')
}

function Invoke-ContainerRuntime {
    param([string[]]$Arguments, [switch]$AllowFailure)

    # @() around the whole if: PowerShell unwraps a one-element array returned from an if, leaving a string.
    $command = @(if ($Runtime -eq 'docker') {
        'docker'
    }
    elseif ($Rootless) {
        $uid = id -u jim
        $accountHome = (getent passwd jim).Split(':')[5]
        @('runuser', '-u', 'jim', '--', 'env', '-i', 'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
            "HOME=$accountHome", "XDG_RUNTIME_DIR=/run/user/$uid", 'podman')
    }
    else {
        'podman'
    })
    $executable, $rest = $command
    $output = & $executable @(@($rest | Where-Object { $_ }) + $Arguments) 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "$($command[-1]) $($Arguments -join ' ') failed (exit $LASTEXITCODE): $($output -join ' ')"
    }
    $output
}

switch ($Action) {
    'Sql' {
        Invoke-ContainerRuntime @('exec', $containers[0], 'psql', '-U', 'jim', '-d', 'jim', '-v', 'ON_ERROR_STOP=1', '-Atc', $Sql)
    }
    'Restarts' {
        $total = 0
        foreach ($name in $containers) {
            $total += [int](Invoke-ContainerRuntime @('inspect', '--format', '{{.RestartCount}}', $name))
        }
        $total
    }
    'Healthy' {
        foreach ($name in $containers) {
            $healthy = if ($Runtime -eq 'docker') {
                (Invoke-ContainerRuntime -AllowFailure @('inspect', '--format', '{{.State.Health.Status}}', $name)) -eq 'healthy'
            }
            else {
                Invoke-ContainerRuntime -AllowFailure @('healthcheck', 'run', $name) | Out-Null
                $LASTEXITCODE -eq 0
            }
            if (-not $healthy) {
                return $name
            }
        }
        $true
    }
}
