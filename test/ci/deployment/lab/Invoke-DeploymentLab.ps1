# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs the deployment lab: installs a JIM release bundle on a fresh VM per cell of the matrix, as many cells at once
    as the lab host's memory holds, and reports each cell's checks.

.DESCRIPTION
    Runs on the lab host (see README.md). Each selected cell runs in its own process (Invoke-LabCell.ps1) with its own
    VM, log and results; the runner starts cells largest first while their memory fits beside the running ones, and
    writes report.md and results.json to -OutputPath when the last finishes. It exits 1 if any cell failed.

.PARAMETER BundlePath
    The release bundle's archive, jim-release-<version>.tar.gz, with its .sha256 beside it, as a release publishes
    them. Or give -Commit instead.

.PARAMETER Commit
    Build the bundle from this commit first, with Build-LabBundle.ps1, and test that. When upgrade cells run and the
    commit's VERSION is not newer than the release they upgrade from, the build reports the next patch version, so that
    the installer takes it as an upgrade.

.PARAMETER PreviousBundlePath
    For upgrade cells: the archive of the release to upgrade from, with its .sha256 beside it. Default: the latest
    release's, downloaded from GitHub once into the lab's cache.

.PARAMETER Name
    Run only the cells with these names; wildcards allowed.

.PARAMETER Tag
    Run only the cells carrying every one of these tags (Cells.psd1 tags each cell, e.g. install, airgap, podman,
    rhel).

.PARAMETER OutputPath
    Where each cell's folder and the report go. Default ./deployment-lab-<date and time>.

.PARAMETER BudgetGiB
    The memory the cells' VMs may take between them. Default: the host's memory less Lab.psd1's ReserveGiB.

.PARAMETER MaxParallel
    At most this many cells at once, whatever the memory. Default: no limit.

.PARAMETER KeepVm
    Never (default), OnFailure (keep a failed cell's VM for inspection), or Always.

.PARAMETER List
    List the cells that would run, and stop.

.EXAMPLE
    ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -BundlePath ~/bundles/jim-release-0.17.0.tar.gz

.EXAMPLE
    ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -Tag rhel -KeepVm OnFailure
#>
[CmdletBinding(DefaultParameterSetName = 'Bundle')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Bundle')]
    [string]$BundlePath,

    [Parameter(Mandatory, ParameterSetName = 'Commit')]
    [string]$Commit,

    [string]$PreviousBundlePath,

    [string[]]$Name,

    [string[]]$Tag,

    [string]$OutputPath = "./deployment-lab-$(Get-Date -Format 'yyyyMMdd-HHmmss')",

    [int]$BudgetGiB = -1,

    [int]$MaxParallel = 0,

    [ValidateSet('Never', 'OnFailure', 'Always')]
    [string]$KeepVm = 'Never',

    [switch]$List
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DeploymentLab.psm1') -Force

$lab = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Lab.psd1')
$matrix = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Cells.psd1')
$faults = Test-LabMatrix -Matrix $matrix
if ($faults) {
    throw "Cells.psd1 is not valid:`n$($faults -join "`n")"
}
$cells = @(Select-LabCell -Cell $matrix.Cells -Name $Name -Tag $Tag)
if ($cells.Count -eq 0) {
    throw 'No cell matches the names and tags given'
}
foreach ($cell in $cells) {
    $cell.Os = $matrix.Images[$cell.Image].Os
}

if ($List) {
    $cells | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name; Os = $_.Os; Runtime = "$($_.Runtime)$(if ($_.ContainsKey('Rootless') -and $_.Rootless) { ' rootless' })"
            Network = $_.Network; Scenario = $_.Scenario; MemoryGiB = Get-LabCellMemory -Cell $_; Tags = $_.Tags -join ', '
        }
    } | Format-Table -AutoSize
    return
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
$cachePath = $lab.CachePath -replace '^~', $HOME

$upgrades = @($cells | Where-Object { $_.Scenario -eq 'Upgrade' }).Count -gt 0
if ($upgrades) {
    if (-not $PreviousBundlePath) {
        $PreviousBundlePath = Get-LabReleaseBundle -CachePath $cachePath
    }
    $PreviousBundlePath = (Resolve-Path $PreviousBundlePath).Path
}
if ($Commit) {
    $build = @{ Commit = $Commit; OutputPath = (Join-Path $OutputPath 'bundle') }
    if ($upgrades) {
        $previousVersion = (Split-Path $PreviousBundlePath -Leaf) -replace '^jim-release-', '' -replace '\.tar\.gz$', ''
        $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path
        $commitVersion = (& git -C $repositoryRoot show "${Commit}:VERSION").Trim()
        if (Test-LabVersionAtMost -Version $commitVersion -Limit $previousVersion) {
            $build.ReportVersion = Get-LabNextVersion -Version $previousVersion
            Write-Host "$Commit's VERSION ($commitVersion) is not newer than $previousVersion, so the build reports $($build.ReportVersion)"
        }
    }
    $BundlePath = & (Join-Path $PSScriptRoot 'Build-LabBundle.ps1') @build | Select-Object -Last 1
}
$BundlePath = (Resolve-Path $BundlePath).Path

# What every VM gets: PowerShell, and the deployment checks with the documentation they run commands from.
$powerShellPath = Get-LabPowerShellArchive -CachePath $cachePath
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path
$payloadPath = Join-Path $OutputPath 'jim-deployment-checks.tar.gz'
& tar -czf $payloadPath -C $repositoryRoot --exclude 'Tests' test/ci/deployment docs/administration
if ($LASTEXITCODE -ne 0) {
    throw 'Could not package the deployment checks'
}

if ($BudgetGiB -lt 0) {
    $BudgetGiB = Get-LabMemoryBudget -MemInfo (Get-Content /proc/meminfo) -ReserveGiB $lab.ReserveGiB
}
$runStarted = Get-Date
Write-Host "Deployment lab: $($cells.Count) cells, bundle $(Split-Path $BundlePath -Leaf)$(if ($upgrades) { ", upgrading from $(Split-Path $PreviousBundlePath -Leaf)" }), $BudgetGiB GiB for VMs$(if ($MaxParallel) { ", at most $MaxParallel at once" }), results in $OutputPath"

$pwshPath = (Get-Process -Id $PID).Path
$cellScript = Join-Path $PSScriptRoot 'Invoke-LabCell.ps1'
$pending = [System.Collections.Generic.List[object]]::new()
$cells | ForEach-Object { $pending.Add($_) }
$running = [System.Collections.Generic.List[object]]::new()
$results = [System.Collections.Generic.List[object]]::new()

function Start-Cell {
    param([hashtable]$Cell)
    $cellPath = Join-Path $OutputPath $Cell.Name
    New-Item -ItemType Directory -Path $cellPath -Force | Out-Null
    # Through bash for its redirections: the cell's output goes to its own log, and its standard input is closed, as
    # lxc needs (see Invoke-LabLxc).
    $start = [System.Diagnostics.ProcessStartInfo]::new('bash')
    foreach ($argument in @('-c', 'exec "$@" < /dev/null > "$0" 2>&1', (Join-Path $cellPath 'runner.log'), $pwshPath,
            '-NoProfile', '-File', $cellScript, '-CellName', $Cell.Name, '-BundlePath', $BundlePath,
            '-PayloadPath', $payloadPath, '-PowerShellPath', $powerShellPath, '-OutputPath', $cellPath, '-KeepVm', $KeepVm) +
        $(if ($Cell.Scenario -eq 'Upgrade') { @('-PreviousBundlePath', $PreviousBundlePath) } else { @() })) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    [pscustomobject]@{ Cell = $Cell; Process = [System.Diagnostics.Process]::Start($start); Path = $cellPath }
}

function Read-CellResult {
    param([object]$Entry)
    $resultsPath = Join-Path $Entry.Path 'results.json'
    if (Test-Path $resultsPath) {
        $result = Get-Content -Raw $resultsPath | ConvertFrom-Json
        $result.Cell = $Entry.Cell
        return $result
    }
    [pscustomobject]@{
        Cell = $Entry.Cell
        Checks = @()
        Minutes = 0
        Error = "the cell's runner exited ($($Entry.Process.ExitCode)) without results; see $($Entry.Cell.Name)/runner.log"
    }
}

try {
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($entry in @($running)) {
            if ($entry.Process.HasExited) {
                $running.Remove($entry) | Out-Null
                $result = Read-CellResult $entry
                $results.Add($result)
                $checks = @($result.Checks)
                $passed = @($checks | Where-Object { $_.Passed }).Count
                $ok = $checks.Count -gt 0 -and $passed -eq $checks.Count -and -not $result.Error
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $(if ($ok) { '✅' } else { '❌' }) $($entry.Cell.Name): $passed of $($checks.Count) checks in $([math]::Round($result.Minutes)) min$(if ($result.Error) { "; $($result.Error)" })"
            }
        }

        $runningGiB = ($running | ForEach-Object { Get-LabCellMemory -Cell $_.Cell } | Measure-Object -Sum).Sum
        $toStart = @(Select-LabCellToStart -Pending $pending.ToArray() -RunningGiB ([int]$runningGiB) -RunningCount $running.Count `
                -BudgetGiB $BudgetGiB -MaxParallel $MaxParallel)
        foreach ($cell in $toStart) {
            $pending.Remove($cell) | Out-Null
            $running.Add((Start-Cell $cell))
            $runningGiB += Get-LabCellMemory -Cell $cell
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] started $($cell.Name) ($(Get-LabCellMemory -Cell $cell) GiB; $runningGiB of $BudgetGiB GiB in use, $($pending.Count) waiting)"
        }
        Start-Sleep -Seconds 5
    }
}
finally {
    # Interrupted: stop the cells still running, and delete their VMs.
    foreach ($entry in $running) {
        if (-not $entry.Process.HasExited) {
            $entry.Process.Kill($true)
            Remove-LabVm -Name "jl-$($entry.Cell.Name)"
        }
    }
}

$ordered = foreach ($cell in $cells) { $results | Where-Object { $_.Cell.Name -eq $cell.Name } }
$title = "Deployment lab: $(Split-Path $BundlePath -Leaf)$(if ($upgrades) { " (upgrades from $(Split-Path $PreviousBundlePath -Leaf))" }), $(Get-Date -Format 'yyyy-MM-dd HH:mm') ($([math]::Round(((Get-Date) - $runStarted).TotalMinutes)) min)"
$report = Format-LabReport -Result @($ordered) -Title $title
$report | Set-Content (Join-Path $OutputPath 'report.md')
$ordered | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputPath 'results.json')
Write-Host ''
Write-Host $report

$allPassed = -not ($ordered | Where-Object {
        $checks = @($_.Checks)
        $_.Error -or $checks.Count -eq 0 -or ($checks | Where-Object { -not $_.Passed })
    })
exit $(if ($allPassed) { 0 } else { 1 })
