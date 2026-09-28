# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Reports a lab domain controller's state, checkpoints and guest OS build.

.DESCRIPTION
    Read-only. With -AsJson prints exactly one JSON object on standard output and nothing else, which is what the
    runner parses:

        { name, state, generation, checkpointType, uptimeSeconds,
          checkpoints: [ { name, createdUtc } ],
          guest: { productName, displayVersion, currentBuild, ubr, buildString "26100.1234" } | null }

    The guest block comes from the guest's registry over PowerShell Direct, so it needs the VM running and the
    Administrator password in the environment variable JIM_AD_LAB_ADMIN_PASSWORD (never a parameter); otherwise
    it is null. Note ProductName can read "Windows Server 2022" on Windows Server 2025 media; the build number is
    what identifies the release.

.PARAMETER Name
    The VM name.

.PARAMETER AsJson
    Print the JSON object instead of text.

.EXAMPLE
    .\Get-LabDomainController.ps1 -Name dc-primary -AsJson
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing output, coloured in the repository style; JSON mode prints nothing else.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

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
    Assert-LabWindows -Feature 'Reading a domain controller'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to report on it."
    }
    $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
    $snapshots = @(Get-VMSnapshot -VMName $Name -ErrorAction SilentlyContinue)

    $guest = $null
    if ($vm.State -eq 'Running') {
        $password = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD' -Optional
        if (($null -ne $password) -and (-not [string]::IsNullOrEmpty($meta.NetBiosName))) {
            $credential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $meta.NetBiosName -Promoted), $password)
            try {
                $guest = Get-LabGuestOsBuild -VMName $Name -Credential $credential
            }
            catch {
                # A guest that is still booting is not an error for a status query; report what is known.
                $guest = $null
            }
        }
    }

    $status = New-LabDomainControllerStatus -Vm $vm -Checkpoint $snapshots -Guest $guest
    if ($AsJson) {
        $status | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host "$($status.name): $($status.state), generation $($status.generation), checkpoints: $($status.checkpointType), up $($status.uptimeSeconds) s" -ForegroundColor Cyan
        foreach ($item in $status.checkpoints) {
            Write-Host ("  checkpoint {0}  {1}" -f $item.name, $item.createdUtc)
        }
        if ($null -ne $status.guest) {
            Write-Host ("  guest OS build {0} ({1} {2})" -f $status.guest.buildString, $status.guest.productName, $status.guest.displayVersion)
        }
        else {
            Write-Host '  guest OS build unknown (the VM is off, still booting, or JIM_AD_LAB_ADMIN_PASSWORD is not set)' -ForegroundColor Yellow
        }
    }
}
catch {
    [Console]::Error.WriteLine("Get-LabDomainController failed: $($_.Exception.Message)")
    exit 1
}
