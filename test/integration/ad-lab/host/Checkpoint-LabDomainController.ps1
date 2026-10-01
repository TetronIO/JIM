# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Takes a Hyper-V production checkpoint of a lab domain controller.

.DESCRIPTION
    Checkpoint names are constrained to the two forms the runner uses: 'baseline', and
    'populated-<template>-<hash>' (template lower-case, hash 16 lower-case hexadecimal characters, built by
    Get-LabCheckpointName in the module). The VM must be one this lab created (its Notes carry the lab marker).

    The VM's checkpoint type is forced to ProductionOnly, which fails rather than silently falling back to a
    standard checkpoint (with saved memory), because a revert is meant to behave like a restore from backup:
    the guest boots fresh, the clock is set from the host, and the domain controller renews its invocationId.

    Without -Replace an existing checkpoint of that name is an error. With -Replace it is removed first.

.PARAMETER Name
    The VM name.

.PARAMETER Checkpoint
    The checkpoint name: baseline, or populated-<template>-<hash>.

.PARAMETER Replace
    Remove an existing checkpoint of that name first.

.EXAMPLE
    .\Checkpoint-LabDomainController.ps1 -Name dc-primary -Checkpoint populated-medium-0123456789abcdef -Replace
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [string]$Checkpoint,

    [switch]$Replace,

    [int]$TimeoutSeconds = 600
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
    Assert-LabWindows -Feature 'Taking a checkpoint'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }
    if (-not (Test-LabCheckpointName -Name $Checkpoint)) {
        throw "'$Checkpoint' is not a checkpoint name this lab uses: baseline, or populated-<template>-<16 hex characters>."
    }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
    if ($vm.CheckpointType -ne 'ProductionOnly') {
        Set-VM -Name $Name -CheckpointType ProductionOnly
    }

    $existing = @(Get-VMSnapshot -VMName $Name -Name $Checkpoint -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        if (-not $Replace) {
            throw "'$Name' already has a checkpoint called '$Checkpoint'. Pass -Replace to replace it."
        }
        Write-Host "Removing the existing checkpoint '$Checkpoint'" -ForegroundColor Yellow
        $existing | Remove-VMSnapshot -Confirm:$false
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (@(Get-VMSnapshot -VMName $Name -Name $Checkpoint -ErrorAction SilentlyContinue).Count -gt 0) {
            if ((Get-Date) -gt $deadline) { throw "The old checkpoint '$Checkpoint' was not removed within $TimeoutSeconds seconds." }
            Start-Sleep -Seconds 2
        }
    }

    Checkpoint-VM -Name $Name -SnapshotName $Checkpoint
    $taken = @(Get-VMSnapshot -VMName $Name -Name $Checkpoint -ErrorAction SilentlyContinue)
    if ($taken.Count -ne 1) {
        throw "The checkpoint '$Checkpoint' was not found after taking it."
    }
    Write-Host "Took the production checkpoint '$Checkpoint' of $Name at $($taken[0].CreationTime.ToUniversalTime().ToString('u'))" -ForegroundColor Green
}
catch {
    [Console]::Error.WriteLine("Checkpoint-LabDomainController failed: $($_.Exception.Message)")
    exit 1
}
