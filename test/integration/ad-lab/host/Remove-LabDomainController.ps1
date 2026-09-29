# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Removes a lab domain controller: stops it, removes its checkpoints, then removes the VM and its disk.

.DESCRIPTION
    Destructive and refuses to run without -Force. Only VMs this lab created (their Notes carry the lab marker)
    are touched. The disk and the VM's folder are deleted after the VM is removed.

.PARAMETER Name
    The VM name.

.PARAMETER Force
    Required. Confirms that the VM, its checkpoints and its disk should be deleted.

.PARAMETER TimeoutSeconds
    How long to wait for checkpoint merges to finish. Default 900.

.EXAMPLE
    .\Remove-LabDomainController.ps1 -Name dc-source -Force
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [switch]$Force,

    [int]$TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$labModule = @(
    (Join-Path $PSScriptRoot 'guest/LabDomainController.psm1'),
    (Join-Path (Join-Path $PSScriptRoot '..') 'guest/LabDomainController.psm1')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $labModule) {
    [Console]::Error.WriteLine('Cannot find guest/LabDomainController.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
Import-Module $labModule -Force

try {
    Assert-LabWindows -Feature 'Removing a domain controller'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }
    if (-not $Force) {
        throw "Removing '$Name' deletes the VM, its checkpoints and its disk. Pass -Force to confirm."
    }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to remove it."
    }
    $vmPath = $vm.Path

    if ($vm.State -ne 'Off') {
        Write-Host "Stopping $Name" -ForegroundColor Yellow
        Stop-VM -Name $Name -TurnOff -Force
    }

    $snapshots = @(Get-VMSnapshot -VMName $Name -ErrorAction SilentlyContinue)
    if ($snapshots.Count -gt 0) {
        Write-Host "Removing $($snapshots.Count) checkpoint(s)" -ForegroundColor Yellow
        $snapshots | Remove-VMSnapshot -Confirm:$false
        # Removing checkpoints merges their differencing disks back; wait for that, or the disk is still in use.
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((@(Get-VMSnapshot -VMName $Name -ErrorAction SilentlyContinue).Count -gt 0) -or
            (@(Get-VMHardDiskDrive -VMName $Name | Where-Object { $_.Path -like '*.avhdx' }).Count -gt 0)) {
            if ((Get-Date) -gt $deadline) { throw "The checkpoints of '$Name' did not finish merging within $TimeoutSeconds seconds." }
            Start-Sleep -Seconds 3
        }
    }

    $disks = @(Get-VMHardDiskDrive -VMName $Name | ForEach-Object { $_.Path })
    Remove-VM -Name $Name -Force
    foreach ($disk in $disks) {
        Remove-Item -LiteralPath $disk -Force -ErrorAction SilentlyContinue
    }

    # The VM's own folder, only when it is named for the VM (this lab always creates it so).
    if ($vmPath -and (Test-Path -LiteralPath $vmPath) -and ((Split-Path $vmPath -Leaf) -eq $Name)) {
        Remove-Item -LiteralPath $vmPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Removed $Name, its checkpoints and its disk." -ForegroundColor Green
}
catch {
    [Console]::Error.WriteLine("Remove-LabDomainController failed: $($_.Exception.Message)")
    exit 1
}
