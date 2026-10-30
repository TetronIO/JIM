# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Reverts a lab VM (a domain controller or the AD FS server) to a named checkpoint, starts it, and waits until it is
    usable.

.DESCRIPTION
    Applies the named production checkpoint, starts the VM if it is stopped, then waits until the guest answers
    PowerShell Direct and its service port accepts a connection: LDAPS (TCP 636) on a domain controller, HTTPS (TCP
    443) on the AD FS server, as the role in the VM's Notes says. Fails if the checkpoint does not exist, or the
    timeout passes. On a domain controller it then makes sure, through PowerShell Direct, that a previous password
    stops working as soon as it changes (OldPasswordAllowedPeriod=0, as the Configure phase sets), whatever the
    checkpoint was taken with.

    The AD FS server is a member of dc-primary's domain, so the two are reverted together: dc-primary first, then
    adfs-primary, each to its own baseline.

    A revert is a restore from backup as far as the directory is concerned: the domain controller boots fresh,
    the clock comes from the host, and it renews its invocationId, so a Delta Import watermark taken before the
    revert is no longer valid (JIM fails fast and names the change; a Full Import re-establishes the baseline).

    PowerShell Direct needs the Administrator password. It is read from the environment variable
    JIM_AD_LAB_ADMIN_PASSWORD, never from the command line (the runner calls this over SSH). Where the variable is
    not set, the wait falls back to the VM's heartbeat plus a TCP connection to the address recorded in the VM's
    Notes, from this host, and says so.

.PARAMETER Name
    The VM name.

.PARAMETER Checkpoint
    The checkpoint to apply: baseline, or populated-<template>-<hash>.

.PARAMETER TimeoutSeconds
    How long to wait for the guest and LDAPS after the revert. Default 600.

.PARAMETER AsJson
    Print a small JSON object describing the outcome instead of text.

.EXAMPLE
    .\Restore-LabDomainController.ps1 -Name dc-primary -Checkpoint baseline
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [string]$Checkpoint,

    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 600,

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

function Write-Progress-Line {
    param([string]$Text, [string]$Colour = 'Gray')
    if (-not $AsJson) { Write-Host $Text -ForegroundColor $Colour }
}

try {
    Assert-LabWindows -Feature 'Restoring a checkpoint'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }
    if (-not (Test-LabCheckpointName -Name $Checkpoint)) {
        throw "'$Checkpoint' is not a checkpoint name this lab uses: baseline, or populated-<template>-<16 hex characters>."
    }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
    $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
    $port = Get-LabReadinessPort -Role $meta.Role
    $service = if ($meta.Role -eq 'DomainController') { 'LDAPS' } else { 'HTTPS' }

    $snapshots = @(Get-VMSnapshot -VMName $Name -Name $Checkpoint -ErrorAction SilentlyContinue)
    if ($snapshots.Count -eq 0) {
        throw "'$Name' has no checkpoint called '$Checkpoint'."
    }
    $snapshot = $snapshots | Sort-Object -Property CreationTime -Descending | Select-Object -First 1

    $started = Get-Date
    Restore-VMSnapshot -VMSnapshot $snapshot -Confirm:$false
    Write-Progress-Line "Applied '$Checkpoint' to $Name"

    if ((Get-VM -Name $Name).State -ne 'Running') {
        Start-VM -Name $Name
    }

    $password = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD' -Optional
    $method = 'heartbeat and TCP from the host'
    if ($null -ne $password) {
        $netBios = $meta.NetBiosName
        if ([string]::IsNullOrEmpty($netBios)) { throw "'$Name' has no NetBIOS name in its Notes, so the domain Administrator cannot be formed." }
        $credential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $netBios -Promoted), $password)
        $null = Wait-LabGuestReady -VMName $Name -Credential @($credential) -TimeoutSeconds $TimeoutSeconds
        $method = 'PowerShell Direct'

        # The service answers a little after the guest does; check from inside, where routing cannot get in the way.
        # AD FS binds HTTPS to its federation service name, not to the loopback address, but the TCP listener
        # (http.sys) accepts on every address, which is all this checks.
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $listening = $false
        while ((-not $listening) -and ((Get-Date) -lt $deadline)) {
            $listening = [bool](Invoke-Command -VMName $Name -Credential $credential -ArgumentList $port -ScriptBlock {
                    param($port)
                    $client = New-Object System.Net.Sockets.TcpClient
                    try { $client.Connect('127.0.0.1', $port); $client.Connected } catch { $false } finally { $client.Close() }
                })
            if (-not $listening) { Start-Sleep -Seconds 5 }
        }
        if (-not $listening) { throw "$service ($port) did not accept a connection in '$Name' within $TimeoutSeconds seconds." }

        # The Configure phase sets this, but a checkpoint taken before it did would bring the 5 minute default back,
        # so it is applied after every revert too (#2287). It is one registry value and needs no restart. Domain
        # controllers only: it is a directory setting.
        if ($meta.Role -eq 'DomainController' -and (Invoke-Command -VMName $Name -Credential $credential -ScriptBlock ${function:Set-LabOldPasswordAllowedPeriod})) {
            Write-Progress-Line "Stopped the previous password working after a change in $Name (OldPasswordAllowedPeriod=0)"
        }
    }
    else {
        Write-Progress-Line "JIM_AD_LAB_ADMIN_PASSWORD is not set: waiting on the heartbeat and a TCP connection to $service from this host instead of PowerShell Direct." 'Yellow'
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (((Get-VMIntegrationService -VMName $Name -Name 'Heartbeat').PrimaryStatusDescription -ne 'OK') -and ((Get-Date) -lt $deadline)) {
            Start-Sleep -Seconds 3
        }
        if ([string]::IsNullOrEmpty($meta.IPAddress)) { throw "'$Name' has no address in its Notes to test $service against." }
        $remaining = [int][math]::Max(10, ($deadline - (Get-Date)).TotalSeconds)
        if (-not (Wait-LabTcpPort -HostName $meta.IPAddress -Port $port -TimeoutSeconds $remaining)) {
            throw "$service ($($meta.IPAddress):$port) did not accept a connection within $TimeoutSeconds seconds."
        }
    }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if ($AsJson) {
        [pscustomobject][ordered]@{ name = $Name; checkpoint = $Checkpoint; state = [string](Get-VM -Name $Name).State; waitedBy = $method; elapsedSeconds = $elapsed } | ConvertTo-Json -Compress
    }
    else {
        Write-Host "$Name is back at '$Checkpoint' and answering (waited by $method, $elapsed s)." -ForegroundColor Green
    }
}
catch {
    [Console]::Error.WriteLine("Restore-LabDomainController failed: $($_.Exception.Message)")
    exit 1
}
