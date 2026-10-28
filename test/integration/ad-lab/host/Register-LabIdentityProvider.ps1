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

      1. refuses, before anything changes on AD FS, unless a copy of the JIM PowerShell module deployed beside it is
         exactly the commit under test. Install-LabScripts.ps1 keeps each version under its digest
         (Get-LabModuleDigest), so this takes module\<ModuleDigest>\JIM, the digest the runner computed over its
         checkout, and checks the copy's content gives that digest. A run lasting hours so still finds its commit's
         module after the hourly update deploys a newer main beside it. A copy from another commit would test a
         registration command that is not the one being released;
      2. opens a PowerShell Direct session to the AD FS server as the domain Administrator, and runs that module's
         Register-JIMIdentityProvider in this host's PowerShell 7 with -Session, so the AD FS cmdlets run in the
         guest's Windows PowerShell, then turns CORS on for -Url, as the AD FS SSO guide tells administrators to, so
         the API reference's Try It can sign in (#2594);
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
$jimModule = Find-LabFile @((Join-Path $PSScriptRoot "module/$ModuleDigest/JIM"),(Join-Path $PSScriptRoot '../../../../src/JIM.PowerShell'))
if (-not $labModule -or -not $federationModule) {
    [Console]::Error.WriteLine('Cannot find guest/LabDomainController.psm1 and guest/LabFederationServer.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
if (-not $jimModule) {
    [Console]::Error.WriteLine("This host has no copy of the JIM PowerShell module of the commit under test (module\$ModuleDigest\JIM beside this script). The hourly update deploys main's within the hour; for another commit, run Install-LabScripts.ps1 from a checkout of it.")
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

    # The API reference's Try It exchanges its code from the browser, which AD FS answers only with CORS on for JIM's
    # origin (#2594). That is farm-wide, so the command leaves it to the administrator, and this does it the way the AD
    # FS SSO guide tells them to: keeping any origins already trusted. -Url is an origin already (no path).
    Invoke-Command -Session $session -ScriptBlock {
        Set-AdfsResponseHeaders -EnableCORS $true
        $origins = @(@((Get-AdfsResponseHeaders).CORSTrustedOrigins) + $using:Url | Where-Object { $_ } | Select-Object -Unique)
        Set-AdfsResponseHeaders -CORSTrustedOrigins $origins
    }

    ConvertTo-LabIdentityProviderRegistration -Registration $registration -ModuleDigest $deployedDigest | ConvertTo-Json -Depth 4 -Compress
}
catch {
    [Console]::Error.WriteLine("Register-LabIdentityProvider failed: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
}
