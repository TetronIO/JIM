# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds a JIM release bundle from a commit, as the release workflow does (scripts/Build-ReleaseBundle.ps1), in a
    throwaway online lab VM, and returns the path of its archive.

.DESCRIPTION
    The lab host never runs Docker itself, so the build runs on a VM from the lab's Docker image, on the lab's online
    network, which is deleted afterwards. The commit is carried in with git archive, so it need not have been pushed.
    The archive and its .sha256 land in -OutputPath, with the commit recorded beside them in COMMIT.

.PARAMETER Commit
    The commit to build. Default HEAD.

.PARAMETER Version
    The version the bundle and its images are labelled with. Default: the commit's VERSION, then -lab.<short commit>,
    so a lab build can never pass for a release.

.PARAMETER ReportVersion
    Write this to VERSION in the build's copy, so that JIM reports it: for an upgrade test, a build of the commit
    after a release must report a newer version than the release. The bundle is then labelled
    <ReportVersion>-lab.<short commit> unless -Version says otherwise.

.PARAMETER OutputPath
    Where the archive goes. Default: the lab cache's bundles folder, under the version.

.EXAMPLE
    $bundle = ./test/ci/deployment/lab/Build-LabBundle.ps1 -Commit HEAD
#>
param(
    [string]$Commit = 'HEAD',

    [string]$Version,

    [string]$ReportVersion,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DeploymentLab.psm1') -Force
$lab = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Lab.psd1')
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path

$fullCommit = & git -C $repositoryRoot rev-parse --verify "$Commit^{commit}"
if ($LASTEXITCODE -ne 0) {
    throw "No commit $Commit in $repositoryRoot"
}
$short = $fullCommit.Substring(0, 7)
$baseVersion = if ($ReportVersion) { $ReportVersion } else { (& git -C $repositoryRoot show "${fullCommit}:VERSION").Trim() }
if (-not $Version) {
    $Version = "$baseVersion-lab.$short"
}
if (-not $OutputPath) {
    $OutputPath = Join-Path ($lab.CachePath -replace '^~', $HOME) 'bundles' $Version
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
$archive = Join-Path $OutputPath "jim-release-$Version.tar.gz"
if ((Test-Path $archive) -and (Test-Path "$archive.sha256") -and (Get-Content (Join-Path $OutputPath 'COMMIT') -ErrorAction SilentlyContinue) -eq $fullCommit) {
    Write-Host "Already built: $archive" -InformationAction Continue
    return $archive
}

$vm = "jl-build-$short"
$network = $lab.Networks['online']
$source = Join-Path ([System.IO.Path]::GetTempPath()) "jim-$short.tar.gz"
$started = Get-Date
try {
    Write-Host "Building $Version from $short on $vm"
    # The image builds need room: 16 GiB, 6 CPUs and an 80 GiB disk, as the release workflow's runner has about.
    New-LabVm -Name $vm -Image 'jimlab/u24-docker-ctrd' -Profile @($lab.BaseProfile, $network.Profile) -MemoryGiB 16 `
        -CpuCount 6 -DiskGiB 80
    & git -C $repositoryRoot archive --format=tar.gz -o $source $fullCommit
    if ($LASTEXITCODE -ne 0) {
        throw "git archive $short failed"
    }
    Copy-LabFile -Source $source -Destination "${vm}:/root/jim.tar.gz"
    Invoke-LabVm -Name $vm -Command @('sh', '-ec', 'mkdir -p /root/JIM && tar -C /root/JIM -xzf /root/jim.tar.gz && snap install powershell --classic') | Out-Null
    if ($ReportVersion) {
        Invoke-LabVm -Name $vm -Command @('sh', '-ec', 'printf "%s\n" "$0" > /root/JIM/VERSION', $ReportVersion) | Out-Null
    }

    $log = Join-Path $OutputPath 'build.log'
    Invoke-LabVm -Name $vm -WorkingDirectory /root/JIM -LogPath $log -Command @('/snap/bin/pwsh', '-NoProfile', '-File',
        './scripts/Build-ReleaseBundle.ps1', '-Version', $Version, '-OutputPath', '/root/out') | Out-Null

    Copy-LabFile -Source "${vm}:/root/out/jim-release-$Version.tar.gz" -Destination $archive
    Copy-LabFile -Source "${vm}:/root/out/jim-release-$Version.tar.gz.sha256" -Destination "$archive.sha256"
    $fullCommit | Set-Content (Join-Path $OutputPath 'COMMIT')
    Write-Host "Built $archive in $([math]::Round(((Get-Date) - $started).TotalMinutes)) min"
    $archive
}
finally {
    Remove-LabVm -Name $vm
    Remove-Item $source -ErrorAction SilentlyContinue
}
