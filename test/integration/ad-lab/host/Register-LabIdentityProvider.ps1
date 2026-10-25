# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Registers JIM on the lab's AD FS server with JIM's own Register-JIMIdentityProvider, and prints what JIM needs as
    JSON.

.DESCRIPTION
    The AD FS sign-in checks (#2441) sign people in to JIM through adfs-primary, so JIM has to be registered there the
    way customers register it: Register-JIMIdentityProvider -Provider ADFS. The runner calls this over SSH after the
    AD FS server has been reverted to its baseline (which holds no registration) and answers. It:

      1. refuses, before anything changes on AD FS, unless the JIM PowerShell module deployed beside it (module\JIM,
         from Install-LabScripts.ps1) is exactly the commit under test: its digest (Get-LabModuleDigest) must be the
         -ModuleDigest the runner computed over its checkout. A copy from another commit would test a registration
         command that is not the one being released;
      2. opens a PowerShell Direct session to the AD FS server as the domain Administrator, and runs that module's
         Register-JIMIdentityProvider in this host's PowerShell 7 with -Session, so the AD FS cmdlets run in the
         guest's Windows PowerShell;
      3. prints one JSON object (ConvertTo-LabIdentityProviderRegistration): authority, clientId, clientSecret,
         publicClientId, apiScope, claimType, mvAttribute, changes and moduleDigest.

    The client secret is printed because standard output is the only way back to the runner, which masks it at once
    and gives it to JIM through the environment. It is new on every run and is gone at the next revert. Nothing is
    written to a file.

    The Administrator password is read from the environment variable JIM_AD_LAB_ADMIN_PASSWORD on the host, never
    from a parameter. Exits non-zero, with the reason on standard error, on any failure.

.PARAMETER Name
    The AD FS server's VM name (adfs-primary).

.PARAMETER Url
    JIM's address as the sign-in checks reach it, such as https://localhost:5243. The portal's redirect URIs are made
    from it.

.PARAMETER ModuleDigest
    The digest of the JIM PowerShell module in the commit under test, 64 lower-case hex characters, from
    Get-LabModuleDigest over src/JIM.PowerShell.

.EXAMPLE
    .\Register-LabIdentityProvider.ps1 -Name adfs-primary -Url https://localhost:5243 -ModuleDigest <digest>
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[A-Za-z0-9.-]+(:\d{1,5})?$')]
    [string]$Url,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ModuleDigest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-LabFile {
    # The deployed layout keeps guest files and the module beside the script; a checkout keeps them above it.
    param([string[]]$Candidate)
    return ($Candidate | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

$labModule = Find-LabFile @((Join-Path $PSScriptRoot 'guest/LabDomainController.psm1'), (Join-Path $PSScriptRoot '../guest/LabDomainController.psm1'))
$federationModule = Find-LabFile @((Join-Path $PSScriptRoot 'guest/LabFederationServer.psm1'), (Join-Path $PSScriptRoot '../guest/LabFederationServer.psm1'))
$jimModule = Find-LabFile @((Join-Path $PSScriptRoot 'module/JIM'), (Join-Path $PSScriptRoot '../../../../src/JIM.PowerShell'))
if (-not $labModule -or -not $federationModule) {
    [Console]::Error.WriteLine('Cannot find guest/LabDomainController.psm1 and guest/LabFederationServer.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
if (-not $jimModule) {
    [Console]::Error.WriteLine("Cannot find the JIM PowerShell module (module\JIM beside this script). Run Install-LabScripts.ps1 from a checkout of the commit under test.")
    exit 1
}
Import-Module $labModule -Force
Import-Module $federationModule -Force

$session = $null
try {
    Assert-LabWindows -Feature 'Registering JIM on the AD FS server'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "There is no VM called '$Name'." }
    if (-not (Test-LabVmNote -Notes $vm.Notes)) {
        throw "'$Name' was not created by this lab (its Notes carry no lab marker); refusing to touch it."
    }
    $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
    if ($meta.Role -ne 'FederationServer') { throw "'$Name' is not the lab's AD FS server (its role is $($meta.Role))." }
    if ($vm.State -ne 'Running') { throw "'$Name' is not running (state $($vm.State))." }
    if ([string]::IsNullOrEmpty($meta.NetBiosName)) { throw "'$Name' has no NetBIOS name in its Notes, so the domain Administrator cannot be formed." }

    # Before anything changes on AD FS: the module must be the commit's.
    $deployedDigest = Get-LabModuleDigest -Entry @(Read-LabModuleFile -Root $jimModule)
    if ($deployedDigest -cne $ModuleDigest) {
        throw "The JIM PowerShell module on this host ($jimModule, digest $deployedDigest) is not the commit under test (digest $ModuleDigest). Run Install-LabScripts.ps1 on the host from a checkout of that commit, then run again."
    }

    $password = Resolve-LabSecret -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $credential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $meta.NetBiosName -Promoted), $password)
    $session = New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop

    Import-Module (Join-Path $jimModule 'JIM.psd1') -Force
    $registration = Register-JIMIdentityProvider -Provider ADFS -Url $Url -Session $session -Confirm:$false -WarningAction SilentlyContinue

    ConvertTo-LabIdentityProviderRegistration -Registration $registration -ModuleDigest $deployedDigest | ConvertTo-Json -Depth 4 -Compress
}
catch {
    [Console]::Error.WriteLine("Register-LabIdentityProvider failed: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
}
