# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 024: Active Directory Password Policy

.DESCRIPTION
    Configures JIM to provision accounts into a real Active Directory domain controller (the lab's dc-primary,
    PANOPLY.LOCAL) and to set an Initial Password derived from the password policy it discovers there, so that
    Scenario 024 can prove the domain policy and a Fine-Grained Password Policy are read as they stand and that
    the passwords generated from them are ones the domain controller accepts (PRD functional requirement 17).
    It is Scenario 022's setup for Active Directory.

    The provisioning substrate is Scenario 001's: HR CSV source, directory target, an Export Synchronisation Rule
    that provisions Users. This script composes Setup-Scenario-017.ps1, which composes Setup-Scenario-001.ps1
    and does the two things an Active Directory target needs on top of it: it makes sure the Partition and the
    Container accounts are provisioned into were enumerated, and it removes the userAccountControl Attribute Flow
    so that the Initial Password, whose EnableAccount option writes the same attribute, is its only writer. That
    is Scenario 020's precedent for building on Scenario 017, and it means the Active Directory specifics are
    written once. This script then switches the Initial Password source from the Static one Scenario 017 sets to
    Discovered, which is the behaviour under test: JIM derives the generator's settings from the policy it read
    at schema import.

    Scenario 022 binds JIM as a non-root account because OpenLDAP exempts the rootdn from its policy overlay. That
    concern does not arise here: the Connected System binds as svc-jim, the delegated service account and never
    a domain administrator, which is already the identity Get-DirectoryConfig gives it.

    The expiry behaviour is ExpiresAccordingToTargetPolicy, not "must change at next sign-in": with a must-change
    account pwdLastSet is zero whether or not the password was accepted, and the scenario needs a non-zero
    pwdLastSet as the domain controller's own evidence that a password write was processed.

    Active Directory lab only. The fixture is a Windows domain controller's domain policy and a Fine-Grained
    Password Policy object; Samba AD and the RFC directories have neither of the things being asserted.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario-017.ps1

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType ActiveDirectory (Primary).

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through when given

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through when given

.EXAMPLE
    ./Setup-Scenario-024.ps1 -ApiKey "jim_..." -Template Micro
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
}

# Refused rather than run: the scenario's evidence is a domain controller's own policy and Fine-Grained Password
# Policy, and failing here is better than building a substrate whose assertions cannot hold, which is the same
# reasoning as Setup-Scenario-022.ps1's refusal of everything but OpenLDAP. Keyed on the directory type, not the
# object class: Samba AD also uses the 'user' class and must still be refused.
if ($DirectoryConfig.DirectoryType -ne "ActiveDirectory") {
    throw "This setup requires the Active Directory lab (DirectoryType ActiveDirectory), not '$($DirectoryConfig.DirectoryType)'. " +
          "It binds JIM to a Windows domain controller whose domain password policy and Fine-Grained Password " +
          "Policy Scenario 024 creates, and there is no equivalent to configure on $($DirectoryConfig.ConnectedSystemName)."
}

Write-TestSection "Scenario 024 Setup: Active Directory Password Policy"

# Step 1: Build the provisioning substrate (HR CSV -> Metaverse -> Active Directory) with an Initial Password
Write-TestStep "Step 1" "Running Setup-Scenario-017.ps1 for the provisioning substrate"

$setupScript = "$PSScriptRoot/Setup-Scenario-017.ps1"
if (-not (Test-Path $setupScript)) {
    throw "Setup script not found at: $setupScript"
}

$setupParams = @{
    JIMUrl          = $JIMUrl
    ApiKey          = $ApiKey
    Template        = $Template
    DirectoryConfig = $DirectoryConfig
    # Not must-change: see this file's .DESCRIPTION.
    ExpiryBehaviour = "ExpiresAccordingToTargetPolicy"
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $setupParams.ExportConcurrency = $ExportConcurrency
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    $setupParams.MaxExportParallelism = $MaxExportParallelism
}

$config = & $setupScript @setupParams
if (-not $config) {
    throw "Setup-Scenario-017.ps1 returned no configuration"
}

# Scenario 017's own static password is not this scenario's business, and leaving it in the configuration
# would suggest it was.
$config.Remove('InitialPassword')

Write-Host "  OK Provisioning substrate configured; the Initial Password is Static for now" -ForegroundColor Green

# Step 2: Follow the discovered policy instead
Write-TestStep "Step 2" "Switching the Initial Password source to Discovered"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    $exportRuleName = "$($DirectoryConfig.ConnectedSystemName) Export Users"

    # Discovered is the point: JIM derives the generator's settings from the password policy it read from the
    # domain controller at schema import, and re-derives them whenever it reads it again. Expiry and
    # EnableAccount are left as Scenario 017's setup saved them (ExpiresAccordingToTargetPolicy, account
    # enabled once the password lands).
    Set-JIMSyncRuleInitialPassword `
        -Id $config.ExportSyncRuleId `
        -Source Discovered `
        -ChangeReason "Scenario 024: Initial Passwords follow the discovered Active Directory policy" | Out-Null

    # Step 3: Read the configuration back
    Write-TestStep "Step 3" "Verifying the stored Initial Password configuration"

    $storedConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId

    Assert-Condition -Condition ($storedConfig.enabled -eq $true) `
        -Message "Initial Password is enabled on '$exportRuleName'"
    Assert-Equal -Actual $storedConfig.source -Expected "Discovered" `
        -Message "Initial Password source is Discovered"
    Assert-Equal -Actual $storedConfig.expiryBehaviour -Expected "ExpiresAccordingToTargetPolicy" `
        -Message "Expiry behaviour is ExpiresAccordingToTargetPolicy"
    Assert-Condition -Condition ($storedConfig.enableAccount -eq $true) `
        -Message "The account is enabled once the password is set"
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# Summary
Write-TestSection "Scenario 024 Setup Complete"
Write-Host "Connected System bind:       $($DirectoryConfig.JimBindDN)" -ForegroundColor Cyan
Write-Host "Export Synchronisation Rule: $exportRuleName (ID: $($config.ExportSyncRuleId))" -ForegroundColor Cyan
Write-Host "Initial Password source:     Discovered" -ForegroundColor Cyan
Write-Host "Expiry behaviour:            ExpiresAccordingToTargetPolicy" -ForegroundColor Cyan
Write-Host ""

# Scenario 001's configuration, which already carries ExportSyncRuleId.
return $config
