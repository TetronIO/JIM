# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Shuts a lab VM down, turning it off if it has not stopped in time. Does nothing to a VM that is already off, or
    that has not been built.

.DESCRIPTION
    The integration runner calls this before reverting the machines a scenario needs, to stop the heavy ones it does
    not (Get-ActiveDirectoryLabPowerPlan). The host has room for one Exchange server beside everything else; with both
    running, dc-target and adfs-primary must be off (README.md, The Exchange servers). Deciding this per scenario,
    from nothing but the scenario, means a run that crashed with an Exchange server up cannot starve the next one.

    Stopping is safe for any lab VM: every scenario reverts the machines it uses to a checkpoint first, which starts
    them, so nothing depends on a machine left running. The VM must be one this lab created (its Notes carry the lab
    marker). Uses Stop-LabVm: a shutdown through the guest, then turned off after -TimeoutSeconds.

.PARAMETER Name
    The VM name.

.PARAMETER TimeoutSeconds
    How long the guest has to shut down before the VM is turned off. Default 300.

.PARAMETER AsJson
    Print a small JSON object describing the outcome instead of text.

.EXAMPLE
    .\Stop-LabMachine.ps1 -Name exchange-primary
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [ValidateRange(3, 3600)]
    [int]$TimeoutSeconds = 300,

    [switch]$AsJson
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
    Assert-LabWindows -Feature 'Stopping a lab machine'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) {
        # Nothing to stop: a machine not built yet (exchange-source before its build) uses no memory.
        if ($AsJson) { [pscustomobject][ordered]@{ name = $Name; outcome = 'Absent'; elapsedSeconds = 0 } | ConvertTo-Json -Compress }
        else { Write-Host "There is no VM called $Name; nothing to stop." -ForegroundColor Green }
        exit 0
    }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }

    $started = Get-Date
    $outcome = Stop-LabVm -Name $Name -TimeoutSeconds $TimeoutSeconds -Confirm:$false
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if ($AsJson) {
        [pscustomobject][ordered]@{ name = $Name; outcome = $outcome; elapsedSeconds = $elapsed } | ConvertTo-Json -Compress
    }
    else {
        $text = switch ($outcome) {
            'AlreadyOff' { "$Name was already off." }
            'ShutDown' { "$Name shut down ($elapsed s)." }
            default { "$Name did not shut down within $TimeoutSeconds seconds and was turned off." }
        }
        Write-Host $text -ForegroundColor Green
    }
}
catch {
    [Console]::Error.WriteLine("Stop-LabMachine failed: $($_.Exception.Message)")
    exit 1
}
