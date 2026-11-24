# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Prepares the hybrid forest's domain controller (dc-hybrid) for Microsoft Entra Cloud Sync, after it is built.

.DESCRIPTION
    dc-hybrid (JIM#3029) is built like any lab domain controller (New-LabDomainController.ps1, with -DnsForwarder and
    -SkipBaselineCheckpoint), and then differs from the others in two ways this script sets, through PowerShell Direct:

      - OU=CloudSync directly under the domain (Get-LabHybridSyncOuDn), the organisational unit the Cloud Sync
        configuration is scoped to. Scenario 038 creates each run's objects in an organisational unit of its own
        inside it. It is protected from accidental deletion.
      - Windows Update on a schedule (Get-LabHybridUpdatePolicyChange): Sundays at 10:00 UTC. The build switches
        updates off, as on every lab machine, because the others are patched by the monthly rebuild; dc-hybrid is
        never rebuilt or reverted, so it patches itself.

    Idempotent: what is already right is left alone, and the script says what it changed. It refuses a VM that is
    in settings.json's domainControllers, because those are reverted and rebuilt and must keep updates off, and a
    VM without the lab's marker. Run it once after the build, and again whenever in doubt.

    The Administrator password is read from the environment variable JIM_AD_LAB_ADMIN_PASSWORD on the host, never
    from a parameter. Prints one line per outcome and exits non-zero on failure.

.PARAMETER Name
    The VM name. Default dc-hybrid.

.EXAMPLE
    .\Initialize-LabHybridForest.ps1
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Prints one outcome line per step for the operator.')]
[CmdletBinding()]
param(
    [string]$Name = 'dc-hybrid'
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
    Assert-LabWindows -Feature 'Preparing the hybrid forest'
    if (-not (Test-LabVmName -Name $Name)) { throw "'$Name' is not a valid VM name here." }

    $settingsPath = Join-Path $PSScriptRoot 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($settings.PSObject.Properties['domainControllers'] -and $settings.domainControllers.PSObject.Properties[$Name]) {
            throw "'$Name' is in settings.json's domainControllers, so it is reverted and rebuilt and must keep Windows Update off. This script is for dc-hybrid only."
        }
    }

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

    # The guest uses this checkout's module, so the values it applies are the ones the tests prove.
    Copy-LabAssetToGuest -VMName $Name -Credential $credential -Destination $guestRoot -Path @($layout.ModulePath)
    $modulePath = Join-Path $guestRoot 'LabDomainController.psm1'

    $outcome = Invoke-Command -VMName $Name -Credential $credential -ArgumentList $modulePath -ScriptBlock {
        param($module)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        Import-Module $module -Force
        Import-Module ActiveDirectory

        $changed = [System.Collections.Generic.List[string]]::new()

        $domainDn = (Get-ADDomain).DistinguishedName
        $ouDn = Get-LabHybridSyncOuDn -DomainDn $domainDn
        $ouName = ($ouDn -split ',', 2)[0].Substring(3)
        $existing = Get-ADOrganizationalUnit -LDAPFilter "(ou=$ouName)" -SearchBase $domainDn -SearchScope OneLevel
        if ($null -eq $existing) {
            New-ADOrganizationalUnit -Name $ouName -Path $domainDn -ProtectedFromAccidentalDeletion $true
            $changed.Add("Created $ouDn")
        }

        $policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
        $current = Get-ItemProperty -LiteralPath $policyKey -ErrorAction SilentlyContinue
        $change = Get-LabHybridUpdatePolicyChange -Current $current
        if ($change.Count -gt 0) {
            if (-not (Test-Path -LiteralPath $policyKey)) { $null = New-Item -Path $policyKey -Force }
            foreach ($valueName in $change.Keys) {
                Set-ItemProperty -LiteralPath $policyKey -Name $valueName -Value $change[$valueName] -Type DWord
                $changed.Add("Set $valueName=$($change[$valueName])")
            }
        }

        # Read everything back, so the report is what the guest holds rather than what was asked of it.
        $after = Get-ItemProperty -LiteralPath $policyKey
        [pscustomobject]@{
            OuDn      = (Get-ADOrganizationalUnit -Identity $ouDn).DistinguishedName
            Protected = (Get-ADOrganizationalUnit -Identity $ouDn -Properties ProtectedFromAccidentalDeletion).ProtectedFromAccidentalDeletion
            Policy    = 'NoAutoUpdate={0}, AUOptions={1}, ScheduledInstallDay={2}, ScheduledInstallTime={3}' -f $after.NoAutoUpdate, $after.AUOptions, $after.ScheduledInstallDay, $after.ScheduledInstallTime
            Remaining = (Get-LabHybridUpdatePolicyChange -Current $after).Count
            Changed   = @($changed)
        }
    }

    if ($outcome.Changed.Count -eq 0) { Write-Host '  Nothing to change' }
    foreach ($line in $outcome.Changed) { Write-Host "  $line" }
    Write-Host "  Cloud Sync organisational unit: $($outcome.OuDn) (protected from accidental deletion: $($outcome.Protected))"
    Write-Host "  Windows Update policy: $($outcome.Policy)"
    if ($outcome.Remaining -ne 0) { throw "the update policy still differs from what dc-hybrid needs after setting it ($($outcome.Policy))." }
}
catch {
    [Console]::Error.WriteLine("Initialize-LabHybridForest failed: $($_.Exception.Message)")
    exit 1
}
