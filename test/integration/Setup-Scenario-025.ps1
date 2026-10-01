# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 025: Active Directory Delta Import Integrity

.DESCRIPTION
    Configures ONE LDAP Connected System bound to one domain controller of the Active Directory lab, with
    nothing on it but what an import needs: the user Object Type and the attributes the scenario reads, the
    domain partition, the container the scenario's users are created in (OU=Users,OU=Corp), and a Full Import
    and a Delta Import Run Profile. There are no Synchronisation Rules, on purpose: Scenario 025 asks what an
    import reports about a directory that changes underneath it, so nothing downstream of the import is
    involved.

    Scenario 025 calls this once per domain controller it uses (Primary, with the Active Directory Recycle Bin
    on, and Source, with it off) and again for Primary when it starts over for the restore-from-backup step. A
    Connected System of the same name left by an earlier call is removed first, so every call starts from an
    empty Connector Space and no persisted watermark.

    Active Directory lab only. Everything a Delta Import does with uSNChanged, the Deleted Objects container and
    the domain controller's invocationId is what the scenario asserts, and a Samba AD container has no Recycle
    Bin to compare and no checkpoint to revert.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility (Run-IntegrationTests.ps1 -SetupOnly passes it) and not used: the scenario
    creates and deletes its own users.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType ActiveDirectory (Primary or
    Source). The Connected System binds as its JimBindDN, the delegated svc-jim, never the domain
    administrator.

.PARAMETER ExportConcurrency
    Accepted for runner compatibility and not used: this Connected System never exports.

.PARAMETER MaxExportParallelism
    Accepted for runner compatibility and not used: this Connected System never exports.

.EXAMPLE
    ./Setup-Scenario-025.ps1 -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary)
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Template, ExportConcurrency and MaxExportParallelism are accepted for the runner, which passes the same parameters to every setup script.')]
param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

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

# Refused rather than run against the wrong directory: the assertions that follow are about a Windows domain
# controller (Deleted Objects, the Recycle Bin, invocationId), and a Samba AD or OpenLDAP config would build a
# Connected System whose evidence means something else.
if ($DirectoryConfig.DirectoryType -ne "ActiveDirectory") {
    throw "This setup requires the Active Directory lab (DirectoryType ActiveDirectory), not '$($DirectoryConfig.DirectoryType)'. " +
          "Scenario 025 asserts what a Delta Import does with a real domain controller's uSNChanged, Deleted Objects " +
          "container and invocationId; $($DirectoryConfig.ConnectedSystemName) is not one."
}

$systemName = $DirectoryConfig.ConnectedSystemName
$userClassName = $DirectoryConfig.UserObjectClass
$containerDn = $DirectoryConfig.UserContainer

Write-TestSection "Scenario 025 Setup: Active Directory Delta Import Integrity ($systemName)"

# Step 1: Import the JIM PowerShell module and connect
Write-TestStep "Step 1" "Connecting to JIM"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to $JIMUrl" -ForegroundColor Green

try {
    # Step 2: Start from nothing
    Write-TestStep "Step 2" "Removing any Connected System left by an earlier call"

    $existingSystem = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $systemName } | Select-Object -First 1
    if ($existingSystem) {
        Remove-JIMConnectedSystem -Id $existingSystem.id -DeleteImmediately -Force | Out-Null

        # A small system is deleted before the call returns; one that is not is queued as a background job. Either
        # way the new one must not be created while the old one still holds its name.
        $removalDeadline = (Get-Date).AddSeconds(120)
        while (@(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $systemName }) {
            if ((Get-Date) -gt $removalDeadline) {
                throw "The existing '$systemName' (ID: $($existingSystem.id)) was still there 120 seconds after its deletion was requested."
            }
            Start-Sleep -Seconds 2
        }
        Write-Host "  OK Removed the existing '$systemName' (ID: $($existingSystem.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  No existing '$systemName'" -ForegroundColor Gray
    }

    # Step 3: Create and configure the Connected System
    Write-TestStep "Step 3" "Creating the LDAP Connected System ($systemName)"

    $ldapConnector = @(Get-JIMConnectorDefinition) | Where-Object { $_.name -eq "JIM LDAP Connector" } | Select-Object -First 1
    if (-not $ldapConnector) {
        throw "The JIM LDAP Connector definition was not found."
    }

    $ldapSystem = New-JIMConnectedSystem `
        -Name $systemName `
        -Description "Active Directory lab domain controller for Delta Import integrity testing ($($DirectoryConfig.Host))" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru

    $connectorDefinition = Get-JIMConnectorDefinition -Id $ldapConnector.id
    $settingByName = @{}
    foreach ($setting in $connectorDefinition.settings) { $settingByName[$setting.name] = $setting }

    # Bound as the delegated service account, as every scenario's Connected System is: an import that only works
    # for the domain administrator would say nothing about the account JIM runs as, and the Deleted Objects read
    # this scenario depends on is one of the rights the delegation grants it.
    $settingValues = @{}
    $settingValues[$settingByName["Host"].id] = @{ stringValue = $DirectoryConfig.Host }
    $settingValues[$settingByName["Port"].id] = @{ intValue = $DirectoryConfig.Port }
    $settingValues[$settingByName["Username"].id] = @{ stringValue = $DirectoryConfig.JimBindDN }
    $settingValues[$settingByName["Password"].id] = @{ stringValue = $DirectoryConfig.JimBindPassword }
    $settingValues[$settingByName["Use Secure Connection (LDAPS)?"].id] = @{ checkboxValue = $DirectoryConfig.UseSSL }
    $settingValues[$settingByName["Connection Timeout"].id] = @{ intValue = 30 }
    $settingValues[$settingByName["Authentication Type"].id] = @{ stringValue = $DirectoryConfig.AuthType }
    Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $settingValues | Out-Null

    Write-Host "  OK Created '$systemName' (ID: $($ldapSystem.id)), bound as $($DirectoryConfig.JimBindDN)" -ForegroundColor Green

    # Step 4: Schema, and the one Object Type and the attributes the scenario reads
    Write-TestStep "Step 4" "Importing the schema and selecting the '$userClassName' Object Type"

    Import-JIMConnectedSystemSchema -Id $ldapSystem.id -Confirm:$false | Out-Null

    $userObjectType = @(Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes) |
        Where-Object { $_.name -eq $userClassName } | Select-Object -First 1
    if (-not $userObjectType) {
        throw "The schema import found no '$userClassName' Object Type on $systemName."
    }
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -Selected $true | Out-Null

    $wantedAttributes = @("sAMAccountName", "givenName", "sn", "displayName", "department", "distinguishedName", "userAccountControl")
    $attributeUpdates = @{}
    foreach ($attribute in $userObjectType.attributes) {
        if ($attribute.name -in $wantedAttributes) { $attributeUpdates[$attribute.id] = @{ selected = $true } }
    }
    $attributeResult = Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id `
        -AttributeUpdates $attributeUpdates -PassThru -ErrorAction Stop
    Write-Host "  OK Selected the '$userClassName' Object Type and $($attributeResult.updatedCount) attributes" -ForegroundColor Green

    # Step 5: Hierarchy, the partition and the container
    #
    # Containers are enumerated only for a Partition that is already selected, so the sequence is: import the
    # hierarchy, select the Partition, import it again, select the Container. The first hierarchy import against
    # a fresh Connected System has failed once and then succeeded when repeated (Setup-Scenario-017.ps1, Step 2b),
    # so each import is tried up to three times. Every stage is checked and a failure throws here, not later as an
    # import that reads nothing.
    Write-TestStep "Step 5" "Selecting the partition and the container $containerDn"

    function Invoke-HierarchyImport {
        param([int]$ConnectedSystemId, [int]$Attempts = 3)
        for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
            try {
                Import-JIMConnectedSystemHierarchy -Id $ConnectedSystemId -ErrorAction Stop | Out-Null
                return
            }
            catch {
                if ($attempt -eq $Attempts) { throw }
                Write-Host "    Hierarchy import attempt $attempt failed, retrying: $($_.Exception.Message)" -ForegroundColor DarkYellow
                Start-Sleep -Seconds 2
            }
        }
    }

    function Get-SelectedPartition {
        param([int]$ConnectedSystemId)
        return @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId) |
            Where-Object { $_.selected } | Select-Object -First 1
    }

    # By Distinguished Name, not by name: 'Users' is also the name of the domain's built-in CN=Users container.
    function Find-ContainerByDn {
        param($Containers, [string]$Dn)
        foreach ($container in $Containers) {
            if ($container.externalId -eq $Dn) { return $container }
            if ($container.childContainers) {
                $found = Find-ContainerByDn -Containers $container.childContainers -Dn $Dn
                if ($found) { return $found }
            }
        }
        return $null
    }

    Invoke-HierarchyImport -ConnectedSystemId $ldapSystem.id

    $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id) |
        Where-Object { $_.name -eq $DirectoryConfig.BaseDN -or $_.externalId -eq $DirectoryConfig.BaseDN } | Select-Object -First 1
    if (-not $partition) {
        throw "The hierarchy import discovered no Partition matching '$($DirectoryConfig.BaseDN)' on $systemName."
    }
    Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $partition.id -Selected $true | Out-Null

    Invoke-HierarchyImport -ConnectedSystemId $ldapSystem.id
    $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id
    if (-not $selectedPartition -or -not $selectedPartition.containers -or $selectedPartition.containers.Count -eq 0) {
        throw "The Partition '$($DirectoryConfig.BaseDN)' reports no Containers after its hierarchy was imported, so there is nothing to import from."
    }

    $container = Find-ContainerByDn -Containers $selectedPartition.containers -Dn $containerDn
    if (-not $container) {
        throw "Could not find the Container '$containerDn' under '$($selectedPartition.name)'. It is where the scenario creates its users, so it must exist in the domain and be visible to $($DirectoryConfig.JimBindDN)."
    }
    if (-not $container.selected) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $container.id -Selected $true | Out-Null
    }
    Write-Host "  OK Partition '$($selectedPartition.name)' and Container '$containerDn' are selected" -ForegroundColor Green

    # Step 6: Run Profiles
    Write-TestStep "Step 6" "Creating the Full Import and Delta Import Run Profiles"

    $fullImportProfile = New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $ldapSystem.id -RunType "FullImport" -PassThru
    $deltaImportProfile = New-JIMRunProfile -Name "Delta Import" -ConnectedSystemId $ldapSystem.id -RunType "DeltaImport" -PassThru
    Write-Host "  OK Created 'Full Import' (ID: $($fullImportProfile.id)) and 'Delta Import' (ID: $($deltaImportProfile.id))" -ForegroundColor Green
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

Write-TestSection "Scenario 025 Setup Complete ($systemName)"
Write-Host "Connected System: $systemName (ID: $($ldapSystem.id))" -ForegroundColor Cyan
Write-Host "Container:        $containerDn" -ForegroundColor Cyan
Write-Host ""

return @{
    ConnectedSystemId    = $ldapSystem.id
    ConnectedSystemName  = $systemName
    FullImportProfileId  = $fullImportProfile.id
    DeltaImportProfileId = $deltaImportProfile.id
    ContainerDn          = $containerDn
}
