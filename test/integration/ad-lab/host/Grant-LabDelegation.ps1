# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Applies JIM's delegation to a container in a lab domain controller, at run time.

.DESCRIPTION
    The Windows counterpart of jim-delegate.sh on Samba. The integration harness calls it over SSH when a scenario
    creates a container while it runs (Grant-JimAdDelegation in test/integration/utils/Test-Helpers.ps1).

    It runs the same guest-side function the Configure phase uses (Grant-LabContainerDelegation, or
    Grant-LabTombstoneRead for -Tombstones) through PowerShell Direct, so there is one implementation. The access
    control entries are the ones in jim-ad-delegation.acl, unchanged, with the JIM Connectors group as trustee.
    It is idempotent: a container whose DACL already names the group is left alone.

    The Administrator password is read from the environment variable JIM_AD_LAB_ADMIN_PASSWORD on the host,
    never from a parameter, because the command line is visible to other processes and to SSH logs. Prints one
    line per outcome and exits non-zero on failure.

.PARAMETER Name
    The VM name.

.PARAMETER ContainerDn
    The distinguished name of the container (an OU) to delegate over, for example OU=Scenario,OU=Corp,DC=panoply,DC=local.

.PARAMETER Tombstones
    Instead of a container, grant the JIM Connectors group read over the Deleted Objects container.

.EXAMPLE
    .\Grant-LabDelegation.ps1 -Name dc-primary -ContainerDn 'OU=Scenario-9,OU=Corp,DC=panoply,DC=local'
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Prints one outcome line for the runner to log.')]
[CmdletBinding(DefaultParameterSetName = 'Container')]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory, ParameterSetName = 'Container')]
    [ValidatePattern('^[A-Za-z]{2}=.+')]
    [string]$ContainerDn,

    [Parameter(Mandatory, ParameterSetName = 'Tombstones')]
    [switch]$Tombstones
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

$guestRoot = 'C:\jim-ad-lab'

try {
    Assert-LabWindows -Feature 'Granting the delegation'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
    if ($vm.State -ne 'Running') { throw "'$Name' is not running (state $($vm.State))." }
    $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
    if ([string]::IsNullOrEmpty($meta.NetBiosName)) { throw "'$Name' has no NetBIOS name in its Notes." }

    $password = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $credential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $meta.NetBiosName -Promoted), $password)
    $layout = Resolve-LabLayout -ScriptRoot $PSScriptRoot

    # Refresh the module and the delegation file in the guest on every call, so the guest applies exactly the
    # version in this checkout rather than whatever the checkpoint was built with.
    Copy-LabAssetToGuest -VMName $Name -Credential $credential -Destination $guestRoot -Path @($layout.ModulePath, $layout.DelegationAclPath)

    $modulePath = Join-Path $guestRoot 'LabDomainController.psm1'
    $aclPath = Join-Path $guestRoot 'jim-ad-delegation.acl'

    if ($Tombstones) {
        $outcome = Invoke-Command -VMName $Name -Credential $credential -ArgumentList $modulePath, $meta.NetBiosName -ScriptBlock {
            param($module, $netBios)
            Import-Module $module -Force
            Grant-LabTombstoneRead -NetBiosName $netBios
        }
        if ($outcome.Outcome -eq 'AlreadyPresent') { Write-Host '  Deleted Objects read already granted' }
        else { Write-Host "  Granted read over $($outcome.ContainerDn)" }
    }
    else {
        $outcome = Invoke-Command -VMName $Name -Credential $credential -ArgumentList $modulePath, $aclPath, $ContainerDn -ScriptBlock {
            param($module, $acl, $container)
            Import-Module $module -Force
            Grant-LabContainerDelegation -ContainerDn $container -AclPath $acl
        }
        if ($outcome.Outcome -eq 'AlreadyPresent') { Write-Host "  Delegation already present on $ContainerDn" }
        else { Write-Host "  Delegated JIM's access over $ContainerDn" }
    }
}
catch {
    [Console]::Error.WriteLine("Grant-LabDelegation failed: $($_.Exception.Message)")
    exit 1
}
