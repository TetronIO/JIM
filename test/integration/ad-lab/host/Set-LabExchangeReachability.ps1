# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Makes a lab Exchange server unreachable for JIM, or reachable again, by stopping or starting IIS in its guest.

.DESCRIPTION
    The Exchange recipient scenario (#2021, PRD Scenario 5) proves that a change JIM could not deliver because Exchange
    was unreachable is retried, and delivered once Exchange is back, without another export. Through PowerShell Direct
    this stops IIS (W3SVC) on the server: HTTP.sys still answers on port 80, with 503, so JIM's client finds a server it
    cannot reach, which it classifies as Unreachable and retries. -State Reachable starts IIS again and waits, as a
    revert does, until Exchange Remote PowerShell answers and every role's required services run.

    Restarting the VM would do the same in minutes; this takes seconds. Nothing in Exchange changes, and the next
    scenario's revert puts the server back to its baseline regardless. PowerShell Direct needs the Administrator
    password, from JIM_AD_LAB_ADMIN_PASSWORD (never the command line). The VM must be an Exchange server this lab
    created (its Notes carry the lab marker and the ExchangeServer role).

.PARAMETER Name
    The Exchange server's VM name.

.PARAMETER State
    Unreachable (stop IIS) or Reachable (start IIS and wait for Exchange).

.PARAMETER TimeoutSeconds
    With -State Reachable, how long to wait for Exchange. Default 600.

.PARAMETER AsJson
    Print a small JSON object describing the outcome instead of text.

.EXAMPLE
    .\Set-LabExchangeReachability.ps1 -Name exchange-primary -State Unreachable
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [ValidateSet('Unreachable', 'Reachable')]
    [string]$State,

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
Import-Module (Join-Path (Split-Path -Parent $labModule) 'LabExchangeServer.psm1') -Force

try {
    Assert-LabWindows -Feature 'Changing whether Exchange is reachable'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
    $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
    if ($meta.Role -ne 'ExchangeServer') { throw "'$Name' is not an Exchange server (its role is $($meta.Role))." }

    $password = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $credential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $meta.NetBiosName -Promoted), $password)

    $started = Get-Date
    $running = $State -eq 'Reachable'
    $status = Invoke-Command -VMName $Name -Credential $credential -ArgumentList $running -ScriptBlock ${function:Set-LabExchangeWebService}
    $detail = "IIS is $status"
    if ($running) {
        $info = Get-LabExchangeServerInfo -Domain $meta.Domain -NetBiosName $meta.NetBiosName -ServerName $meta.ComputerName
        $detail = (Wait-LabExchangeReady -VMName $Name -Credential $credential -ManagementUri $info.ManagementUri -TimeoutSeconds $TimeoutSeconds).Detail
    }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if ($AsJson) {
        [pscustomobject][ordered]@{ name = $Name; state = $State; detail = $detail; elapsedSeconds = $elapsed } | ConvertTo-Json -Compress
    }
    else {
        Write-Host "Exchange in $Name is $($State.ToLowerInvariant()) ($detail, $elapsed s)." -ForegroundColor Green
    }
}
catch {
    [Console]::Error.WriteLine("Set-LabExchangeReachability failed: $($_.Exception.Message)")
    exit 1
}
