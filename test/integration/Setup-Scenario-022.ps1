# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 022: OpenLDAP Password Policy

.DESCRIPTION
    Configures JIM to provision accounts into the Yellowstone OpenLDAP suffix as a NON-ROOT account and
    to set an Initial Password derived from the password policy it discovers there, so that Scenario 022
    can prove the policy is read correctly and that generated passwords satisfy it (#1702).

    The provisioning substrate is Scenario 001's: HR CSV source, OpenLDAP target, an Export
    Synchronisation Rule that provisions Users. This script composes Setup-Scenario-001.ps1 rather than
    rebuilding it, the same way Scenario 017 does, with one change to the directory configuration it is
    handed: the Connected System binds as cn=jim-provisioner,dc=yellowstone,dc=local (created by
    Populate-OpenLDAP-Scenario-022.ps1), not as the rootdn. OpenLDAP exempts the rootdn from the ppolicy
    overlay (slapo-ppolicy(5)), so a Connected System bound as cn=admin would have every password
    accepted whatever its length, and "none parked" would prove nothing.

    The password source is Discovered: JIM derives the generator's settings from the policy it read at
    schema import, which is the behaviour under test. Expiry behaviour is ExpiresAccordingToTargetPolicy
    because that is the one OpenLDAP can honour (pwdMaxAge on the policy entry); "must change at next
    sign-in" is an Active Directory behaviour that JIM reports as a downgrade elsewhere.

    OpenLDAP only. Setup-Scenario-017.ps1 refuses OpenLDAP for its own reasons; this script refuses Samba
    AD for the mirror-image one: the fixture is an OpenLDAP ppolicy overlay, and Active Directory's
    policy discovery is already covered.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario-001.ps1

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType OpenLDAP. Its JimBindDN and
    JimBindPassword (the Connected System's own bind identity, not the rootdn BindDN/BindPassword) are
    replaced with the provisioner's before it reaches Setup-Scenario-001.ps1.

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through to Setup-Scenario-001.ps1

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through to Setup-Scenario-001.ps1

.PARAMETER ProvisionerBindDN
    The non-root account JIM binds as. Must match what Populate-OpenLDAP-Scenario-022.ps1 created.

.PARAMETER ProvisionerBindPassword
    That account's password. Must match what Populate-OpenLDAP-Scenario-022.ps1 set.

.EXAMPLE
    ./Setup-Scenario-022.ps1 -ApiKey "jim_..." -Template Micro
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
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [string]$ProvisionerBindDN = "cn=jim-provisioner,dc=yellowstone,dc=local",

    [Parameter(Mandatory=$false)]
    [string]$ProvisionerBindPassword = "Provisioner-Meadow-41!"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

# Default to the OpenLDAP Primary instance (Yellowstone) if no config provided
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}

# The fixture is an OpenLDAP ppolicy overlay; there is nothing here for Active Directory to enforce or
# for the scenario to assert. Failing here is better than running a scenario whose evidence is
# inapplicable, which is the same reasoning as Setup-Scenario-017.ps1's refusal of OpenLDAP.
# Keyed on the directory type, not the object class: 389 Directory Server also uses inetOrgPerson
# and must still be refused here (the ppolicy overlay fixture is OpenLDAP-specific).
if ($DirectoryConfig.DirectoryType -ne "OpenLDAP") {
    throw "This setup requires OpenLDAP: it binds JIM as a non-root account under a ppolicy overlay whose " +
          "default policy Populate-OpenLDAP-Scenario-022.ps1 creates, and there is no equivalent to configure on " +
          "$($DirectoryConfig.ConnectedSystemName). Active Directory's password policy discovery has its own coverage."
}

Write-TestSection "Scenario 022 Setup: OpenLDAP Password Policy"

# Step 1: Bind as the provisioner, not the rootdn
Write-TestStep "Step 1" "Binding the Connected System as the non-root provisioner"

# A shallow copy is enough: only two scalar values change, and the caller's hashtable is left alone so
# the scenario can keep using the rootdn for its own reads of the directory. Setup-Scenario-001.ps1
# configures the Connected System's Username/Password from JimBindDN/JimBindPassword, not
# BindDN/BindPassword (the two-identity model, #1715): BindDN/BindPassword stays the rootdn
# throughout, so overriding it here would leave the Connected System bound as whatever
# Get-DirectoryConfig's JimBindDN already was (the lab's own svc-jim service account) rather than
# the provisioner this scenario needs.
$provisionerConfig = $DirectoryConfig.Clone()
$provisionerConfig.JimBindDN = $ProvisionerBindDN
$provisionerConfig.JimBindPassword = $ProvisionerBindPassword
Write-Host "  Connected System '$($provisionerConfig.ConnectedSystemName)' will bind as $ProvisionerBindDN" -ForegroundColor Gray
Write-Host "  (the rootdn $($DirectoryConfig.BindDN) is exempt from the password policy, so it must not be JIM's account)" -ForegroundColor DarkGray

# Step 2: Build the provisioning substrate (HR CSV -> Metaverse -> OpenLDAP)
Write-TestStep "Step 2" "Running Setup-Scenario-001.ps1 for the provisioning substrate"

$setupScript = "$PSScriptRoot/Setup-Scenario-001.ps1"
if (-not (Test-Path $setupScript)) {
    throw "Setup script not found at: $setupScript"
}

$setupParams = @{
    JIMUrl = $JIMUrl
    ApiKey = $ApiKey
    Template = $Template
    DirectoryConfig = $provisionerConfig
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $setupParams.ExportConcurrency = $ExportConcurrency
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    $setupParams.MaxExportParallelism = $MaxExportParallelism
}

$config = & $setupScript @setupParams
if (-not $config) {
    throw "Setup-Scenario-001.ps1 returned no configuration"
}

Write-Host "  ✓ Provisioning substrate configured" -ForegroundColor Green

# Step 3: Connect to JIM for the Initial Password configuration
Write-TestStep "Step 3" "Connecting to JIM"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  ✓ Connected to $JIMUrl" -ForegroundColor Green

try {
    # Step 3b: Make sure the export has somewhere to go
    #
    # Setup-Scenario-001 warns and carries on when the hierarchy import or container selection fails, and the
    # failure then resurfaces several steps later as an export refused with "selected partition(s) contain
    # no enumerated containers". Checking here turns that into a setup failure that names the cause. The
    # first thing to suspect is the provisioner's access: this is the first time the substrate has been
    # built by an account other than the rootdn.
    Write-TestStep "Step 3b" "Verifying the Partition and Container were enumerated as the provisioner"

    $ldapSystem = @(Get-JIMConnectedSystem) |
        Where-Object { $_.name -eq $DirectoryConfig.ConnectedSystemName } | Select-Object -First 1
    if (-not $ldapSystem) {
        throw "Could not find the Connected System '$($DirectoryConfig.ConnectedSystemName)'."
    }

    function Get-SelectedPartition {
        param([int]$ConnectedSystemId)
        return @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId) |
            Where-Object { $_.selected } | Select-Object -First 1
    }

    function Find-ContainerByName {
        param($Containers, [string]$Name, [string]$FullDN)
        foreach ($container in $Containers) {
            if ($container.name -eq $Name -or $container.name -eq $FullDN) { return $container }
            if ($container.childContainers) {
                $found = Find-ContainerByName -Containers $container.childContainers -Name $Name -FullDN $FullDN
                if ($found) { return $found }
            }
        }
        return $null
    }

    # Select the Partition, importing the hierarchy first where nothing was discovered (Setup-Scenario-017's
    # Step 2b recovery, kept because Containers are enumerated only for a Partition already selected).
    $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id
    if (-not $selectedPartition) {
        Write-Host "  No Partition is selected; importing the hierarchy as $ProvisionerBindDN..." -ForegroundColor Yellow
        Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id -ErrorAction Stop | Out-Null

        $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id) |
            Where-Object { $_.name -eq $DirectoryConfig.BaseDN -or $_.externalId -eq $DirectoryConfig.BaseDN } |
            Select-Object -First 1
        if (-not $partition) {
            throw "The hierarchy import discovered no Partition matching '$($DirectoryConfig.BaseDN)' on " +
                  "'$($DirectoryConfig.ConnectedSystemName)'. It ran as $ProvisionerBindDN; check that " +
                  "Populate-OpenLDAP-Scenario-022.ps1 granted that account access to the suffix."
        }

        Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $partition.id -Selected $true | Out-Null
        $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id
        Write-Host "  ✓ Selected Partition '$($selectedPartition.name)'" -ForegroundColor Green
    }

    if (-not $selectedPartition.containers -or $selectedPartition.containers.Count -eq 0) {
        Write-Host "  Partition '$($selectedPartition.name)' has no enumerated Containers; re-importing the hierarchy..." -ForegroundColor Yellow
        Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id -ErrorAction Stop | Out-Null
        $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id

        if (-not $selectedPartition.containers -or $selectedPartition.containers.Count -eq 0) {
            throw "The Partition '$($selectedPartition.name)' still reports no Containers after a second hierarchy " +
                  "import, so nothing can be exported. The import ran as $ProvisionerBindDN; check its access to " +
                  "$($DirectoryConfig.UserContainer)."
        }
    }

    # Select the Container accounts are provisioned into (OpenLDAP reports the full DN as the name).
    $targetContainerName = if ($DirectoryConfig.UserContainer -match "^[Oo][Uu]=([^,]+)") { $matches[1] } else { "People" }
    $targetContainer = Find-ContainerByName -Containers $selectedPartition.containers -Name $targetContainerName -FullDN $DirectoryConfig.UserContainer
    if (-not $targetContainer) {
        throw "Could not find the Container '$($DirectoryConfig.UserContainer)' under '$($selectedPartition.name)'. " +
              "Accounts are provisioned into it, so it must exist in the directory and be visible to $ProvisionerBindDN."
    }
    if (-not $targetContainer.selected) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $targetContainer.id -Selected $true | Out-Null
    }
    Write-Host "  ✓ Partition '$($selectedPartition.name)' carries $($selectedPartition.containers.Count) Container(s); '$($targetContainer.name)' is selected" -ForegroundColor Green

    # Step 4: Enable the Initial Password on the Export Synchronisation Rule
    Write-TestStep "Step 4" "Enabling a Discovered-policy Initial Password on the Export Synchronisation Rule"

    $exportRuleName = "$($DirectoryConfig.ConnectedSystemName) Export Users"
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName } | Select-Object -First 1

    if (-not $exportRule) {
        throw "Could not find the Export Synchronisation Rule '$exportRuleName'. Setup-Scenario-001.ps1 " +
              "creates it; if that has been renamed, this scenario needs updating to match."
    }

    Write-Host "  Export Synchronisation Rule '$exportRuleName' (ID: $($exportRule.id))" -ForegroundColor Gray

    # Source Discovered is the point: the generator's settings come from the policy JIM read at schema
    # import, and are re-derived whenever it is read again. No EnableAccount: OpenLDAP has no account
    # enabled state for the Connector to write, and Scenario 001's OpenLDAP flows do not export one either.
    Set-JIMSyncRuleInitialPassword `
        -Id $exportRule.id `
        -Enable `
        -Source Discovered `
        -ExpiryBehaviour ExpiresAccordingToTargetPolicy `
        -ChangeReason "Integration test substrate: Initial Passwords follow the discovered OpenLDAP policy" | Out-Null

    Write-Host "  ✓ Initial Password enabled (Source: Discovered, Expiry: ExpiresAccordingToTargetPolicy)" -ForegroundColor Green

    # Step 5: Read the configuration back
    Write-TestStep "Step 5" "Verifying the stored Initial Password configuration"

    $storedConfig = Get-JIMSyncRuleInitialPassword -Id $exportRule.id

    Assert-Condition -Condition ($storedConfig.enabled -eq $true) `
        -Message "Initial Password is enabled on '$exportRuleName'"
    Assert-Equal -Actual $storedConfig.source -Expected "Discovered" `
        -Message "Initial Password source is Discovered"
    Assert-Equal -Actual $storedConfig.expiryBehaviour -Expected "ExpiresAccordingToTargetPolicy" `
        -Message "Expiry behaviour is ExpiresAccordingToTargetPolicy"
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# Summary
Write-TestSection "Scenario 022 Setup Complete"
Write-Host "Connected System bind:       $ProvisionerBindDN" -ForegroundColor Cyan
Write-Host "Export Synchronisation Rule: $exportRuleName (ID: $($exportRule.id))" -ForegroundColor Cyan
Write-Host "Initial Password source:     Discovered" -ForegroundColor Cyan
Write-Host "Expiry behaviour:            ExpiresAccordingToTargetPolicy" -ForegroundColor Cyan
Write-Host ""

# Return Scenario 001's configuration plus what the scenario's assertions need.
$config.ExportSyncRuleId = $exportRule.id
$config.ProvisionerBindDN = $ProvisionerBindDN
return $config
