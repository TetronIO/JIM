# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 20: Password Synchronisation

.DESCRIPTION
    Builds the substrate Scenario 20 asserts against: accounts provisioned into Samba AD, enabled, and holding a
    password the scenario knows, with Password Synchronisation configured on the directory but switched OFF.

    Two pieces, and the second is the whole point of this script:

      1. Provisioned accounts with a known password. That is exactly what Setup-Scenario17.ps1 builds, so this
         composes it rather than rebuilding it (Scenario 17 composes Setup-Scenario1.ps1 in the same way). The
         one thing it asks for differently is the expiry behaviour: Scenario 17 wants must-change-at-next-sign-in
         because that is what it asserts, whereas Scenario 20 needs accounts whose Initial Password signs in
         cleanly. Active Directory answers a correct password on a must-change account with the same result code
         as a wrong one (49), distinguished only by a sub-code, so leaving the accounts must-change would make
         "the old password no longer works" and "the old password works and needs changing" harder to tell apart
         than they need to be. The synchronised password is the variable under test; nothing else should be.

      2. Password Synchronisation configured on the directory, and deliberately DISABLED. Configured-but-off is
         the state requirement 2 is about: the system accumulates queued password changes rather than discarding
         them, and switching it on delivers what accumulated (requirement 3). Starting the scenario there means
         its first assertions run against the harder half of the behaviour, and enabling the system mid-scenario
         is a real drain rather than a no-op.

    Samba AD only, for the reason Setup-Scenario17.ps1 gives: provisioning enables the account as the Initial
    Password lands, which is an Active Directory operation. An account left disabled cannot be signed in as, and
    signing in is how this scenario proves a password arrived.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario17.ps1

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through

.EXAMPLE
    ./Setup-Scenario20.ps1 -ApiKey "jim_..." -Template Micro
#>

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
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

if ($DirectoryConfig.UserObjectClass -ne "user") {
    throw "Scenario 20 requires Samba AD. Provisioning enables each account as its Initial Password lands, which " +
          "is an Active Directory operation with no equivalent on $($DirectoryConfig.ConnectedSystemName); an " +
          "account left disabled cannot be signed in as, and signing in is how this scenario proves a " +
          "synchronised password arrived."
}

Write-TestSection "Scenario 20 Setup: Password Synchronisation"

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 1: Provisioned accounts holding a known, usable password
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestStep "Step 1" "Running Setup-Scenario17.ps1 for provisioned accounts with a known Initial Password"

$setupScript = "$PSScriptRoot/Setup-Scenario17.ps1"
if (-not (Test-Path $setupScript)) {
    throw "Setup script not found at: $setupScript"
}

$setupParams = @{
    JIMUrl          = $JIMUrl
    ApiKey          = $ApiKey
    Template        = $Template
    DirectoryConfig = $DirectoryConfig
    # Not must-change: see this file's .DESCRIPTION. The scenario needs the Initial Password to sign in cleanly
    # so that a bind failing later means the synchronised password replaced it, and nothing else.
    ExpiryBehaviour = "NeverExpires"
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $setupParams.ExportConcurrency = $ExportConcurrency
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    $setupParams.MaxExportParallelism = $MaxExportParallelism
}

$config = & $setupScript @setupParams
if (-not $config) {
    throw "Setup-Scenario17.ps1 returned no configuration"
}

Write-Host "  ✓ Provisioning substrate configured; accounts will carry a known Initial Password" -ForegroundColor Green

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 2: Configure Password Synchronisation on the directory, switched off
# ─────────────────────────────────────────────────────────────────────────────────────────────
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    Write-TestStep "Step 2" "Configuring Password Synchronisation on $($DirectoryConfig.ConnectedSystemName)"

    # The Object Type holding the directory's user accounts. Named from the Connected System's own selected
    # Object Types rather than assumed, because the configuration's target is a foreign key and a wrong id is
    # refused by the API with a message about the Object Type rather than about the password.
    $userObjectType = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId $config.LDAPSystemId) |
        Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass -and $_.selected } | Select-Object -First 1

    if (-not $userObjectType) {
        throw "Could not find a selected Connected System Object Type named " +
              "'$($DirectoryConfig.UserObjectClass)' on '$($DirectoryConfig.ConnectedSystemName)'. Password " +
              "Synchronisation has to be told which Object Type holds the accounts, and it must be one selected " +
              "for synchronisation."
    }

    # Checked before configuring rather than after failing: a Connector that cannot set passwords is refused by
    # the API, and the message is clearer here, beside the reason the scenario needs the capability at all.
    $existing = Get-JIMConnectedSystemPasswordSynchronisation -Id $config.LDAPSystemId
    if (-not $existing.connectorSupportsPasswordSet) {
        throw "The Connector behind '$($DirectoryConfig.ConnectedSystemName)' does not declare the password " +
              "capability, so Password Synchronisation cannot be configured on it and this scenario has nothing " +
              "to assert against."
    }

    # Switched OFF deliberately. The scenario's first assertions are about what a switched-off system does with
    # password changes, and it enables the system itself partway through to prove the drain.
    Set-JIMConnectedSystemPasswordSynchronisation `
        -Id $config.LDAPSystemId `
        -TargetObjectType $userObjectType.id `
        -Enabled $false `
        -MaxRetries 3 `
        -RetryBackoffBase ([TimeSpan]::FromSeconds(30)) `
        -ChangeReason "Scenario 20: staged before the change window, as requirement 4 describes" | Out-Null

    Write-Host "  ✓ Password Synchronisation configured (Object Type: $($userObjectType.name)), switched off" -ForegroundColor Green

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Step 3: Read the configuration back
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestStep "Step 3" "Verifying the stored Password Synchronisation configuration"

    $stored = Get-JIMConnectedSystemPasswordSynchronisation -Id $config.LDAPSystemId

    Assert-Condition -Condition ($stored.configured -eq $true) `
        -Message "Password Synchronisation is configured on '$($DirectoryConfig.ConnectedSystemName)'"
    Assert-Condition -Condition ($stored.enabled -eq $false) `
        -Message "Password Synchronisation is switched off, which is the state the scenario starts from"
    Assert-Equal -Actual $stored.targetObjectTypeId -Expected $userObjectType.id `
        -Message "The configuration targets the Object Type holding user accounts"
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

Write-TestSection "Scenario 20 Setup Complete"
Write-Host "Connected System:            $($DirectoryConfig.ConnectedSystemName) (ID: $($config.LDAPSystemId))" -ForegroundColor Cyan
Write-Host "Password Synchronisation:    configured, switched OFF" -ForegroundColor Cyan
Write-Host "Initial Password:            Static, never expires, account enabled" -ForegroundColor Cyan
Write-Host ""

$config.PasswordSynchronisationObjectTypeId = $userObjectType.id
return $config
