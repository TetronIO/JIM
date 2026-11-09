# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Reverts a lab VM (a domain controller, the AD FS server or an Exchange server) to a named checkpoint, starts it,
    and waits until it is usable.

.DESCRIPTION
    Applies the named production checkpoint, starts the VM if it is stopped, then waits until the guest answers
    PowerShell Direct and its service port accepts a connection: LDAPS (TCP 636) on a domain controller, HTTPS (TCP
    443) on the AD FS server, as the role in the VM's Notes says. Fails if the checkpoint does not exist, or the
    timeout passes. On a domain controller it then makes sure, through PowerShell Direct, that a previous password
    stops working as soon as it changes (OldPasswordAllowedPeriod=0, as the Configure phase sets), whatever the
    checkpoint was taken with, and brings JIM's delegation in line with jim-ad-delegation.acl from this checkout
    (Sync-LabDelegation), so a checkpoint taken before the file gained entries still comes back with all of them.

    The AD FS server is a member of dc-primary's domain, so the two are reverted together: dc-primary first, then
    adfs-primary, each to its own baseline. An Exchange server is reverted after its domain controller in the same
    way, and no port proves it ready (443 answers minutes before Exchange does): through PowerShell Direct it waits
    until Exchange Remote PowerShell answers, as JIM connects (Kerberos over HTTP), and every role's required services
    run (Get-LabExchangeReadinessProbe, Test-LabExchangeReady). That can only be asked through PowerShell Direct, so
    an Exchange server needs JIM_AD_LAB_ADMIN_PASSWORD, and its default timeout is 1800 seconds.

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
    How long to wait for the guest and its service after the revert. Default 600, or 1800 for an Exchange server.

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
    $isExchange = $meta.Role -eq 'ExchangeServer'
    if ($isExchange) {
        $port = $null
        $service = 'Exchange Remote PowerShell'
        if (-not $PSBoundParameters.ContainsKey('TimeoutSeconds')) { $TimeoutSeconds = 1800 }
        # Checked before the revert, so a missing password changes nothing.
        if ($null -eq (Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD' -Optional)) {
            throw "'$Name' is an Exchange server, whose readiness can only be read through PowerShell Direct, and JIM_AD_LAB_ADMIN_PASSWORD is not set."
        }
        $exchangeModule = Join-Path (Split-Path -Parent $labModule) 'LabExchangeServer.psm1'
        if (-not (Test-Path -LiteralPath $exchangeModule)) { throw "Cannot find LabExchangeServer.psm1 beside $labModule." }
        Import-Module $exchangeModule -Force
        $exchangeInfo = Get-LabExchangeServerInfo -Domain $meta.Domain -NetBiosName $meta.NetBiosName -ServerName $meta.ComputerName
    }
    else {
        $port = Get-LabReadinessPort -Role $meta.Role
        $service = if ($meta.Role -eq 'DomainController') { 'LDAPS' } else { 'HTTPS' }
    }

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

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        if ($isExchange) {
            # Exchange's services start minutes after the guest does. Ask Exchange itself, from inside, as JIM connects.
            $verdict = $null
            while ($true) {
                $probe = Invoke-Command -VMName $Name -Credential $credential -ArgumentList $exchangeInfo.ManagementUri, $credential -ScriptBlock ${function:Get-LabExchangeReadinessProbe}
                $verdict = Test-LabExchangeReady -Probe $probe
                if ($verdict.Ready -or ((Get-Date) -ge $deadline)) { break }
                Start-Sleep -Seconds 15
            }
            if (-not $verdict.Ready) { throw "Exchange in '$Name' was not ready within $TimeoutSeconds seconds: $($verdict.Detail)" }
            Write-Progress-Line "Exchange in $Name is ready: $($verdict.Detail)"
        }
        else {
            # The service answers a little after the guest does; check from inside, where routing cannot get in the way.
            # AD FS binds HTTPS to its federation service name, not to the loopback address, but the TCP listener
            # (http.sys) accepts on every address, which is all this checks.
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
        }

        # The Configure phase sets this, but a checkpoint taken before it did would bring the 5 minute default back,
        # so it is applied after every revert too (#2287). It is one registry value and needs no restart. Domain
        # controllers only: it is a directory setting.
        if ($meta.Role -eq 'DomainController' -and (Invoke-Command -VMName $Name -Credential $credential -ScriptBlock ${function:Set-LabOldPasswordAllowedPeriod})) {
            Write-Progress-Line "Stopped the previous password working after a change in $Name (OldPasswordAllowedPeriod=0)"
        }

        # Likewise JIM's delegation (#2774): a checkpoint taken before the delegation file gained entries would bring
        # the domain controller back without them. The module and the file are copied in from this checkout first,
        # so the guest applies exactly this version; entries already held are left alone.
        if ($meta.Role -eq 'DomainController') {
            $layout = Resolve-LabLayout -ScriptRoot $PSScriptRoot
            $guestRoot = 'C:\jim-ad-lab'
            Copy-LabAssetToGuest -VMName $Name -Credential $credential -Destination $guestRoot -Path @($layout.ModulePath, $layout.DelegationAclPath)
            $outcomes = @(Invoke-Command -VMName $Name -Credential $credential -ArgumentList (Join-Path $guestRoot 'LabDomainController.psm1'), (Join-Path $guestRoot 'jim-ad-delegation.acl') -ScriptBlock {
                    param($module, $acl)
                    Import-Module $module -Force
                    Sync-LabDelegation -AclPath $acl
                })
            foreach ($outcome in $outcomes | Where-Object { $_.Outcome -ne 'AlreadyPresent' }) {
                Write-Progress-Line "Brought JIM's delegation up to date on $($outcome.ContainerDn) in $Name"
            }
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
