# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 19: Auxiliary Classes

.DESCRIPTION
    Sets up two LDAP Connected Systems over the same OpenLDAP container's two suffixes
    (Yellowstone and Glitterband, added by docker/openldap/scripts/01-add-second-suffix.sh) for
    auxiliary class testing (#492), following Scenario 14's two-suffix shape: Source
    (Yellowstone) imports and projects, Target (Glitterband) exports.

    Creates:
    - "Scenario 19 Source" Connected System, base DN = dc=yellowstone,dc=local
    - "Scenario 19 Target" Connected System, base DN = dc=glitterband,dc=local
    - "Badge Number" and "Badge Colour" Metaverse attributes (Text, single-valued) on the
      "User" Metaverse Object Type, created idempotently
    - A Source Import Sync Rule (ProjectToMetaverse) with identity-plumbing mappings, and a
      Target Import Sync Rule (join only, no projection and no mappings); both systems match on
      employeeNumber -> Employee ID so each pair of entries joins to one Metaverse Object
    - A Target Export Sync Rule with NO mappings yet: the badge attribute mappings can only
      exist after the Merge step has merged jimBadgeHolder into the Target's jimPerson Object
      Type and refreshed its schema, so Invoke-Scenario19-AuxiliaryClasses.ps1 adds them there
    - Full Import + Full Synchronisation Run Profiles per system, plus Export on Target

    Everything that depends on the jimBadgeHolder merge (auxiliary attribute selection, badge
    attribute flow mappings, the carrier-provisioning rule) is deliberately NOT configured here:
    the merge is itself the first step under test, so the invoke script owns it.

    This scenario is OpenLDAP only: the JIM-owned auxiliary class and DIT Content Rule live in
    the OpenLDAP test image's schema, and Active Directory resolves its own auxiliary classes
    into each structural class. Run-IntegrationTests.ps1 hard-fails a Samba AD or "All"
    -DirectoryType request for Scenario 19 before this script is ever invoked.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own small, fixed, deterministic
    user set (see Populate-OpenLDAP-Scenario19.ps1) and ignores the template.

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig. Only OpenLDAP is
    supported; the Source/Target suffix configuration is always re-derived from
    Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source/Target regardless of exactly
    which OpenLDAP instance is passed in, mirroring Setup-Scenario14.ps1.

.EXAMPLE
    ./Setup-Scenario19.ps1 -ApiKey "jim_abc123..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Accepted for runner compatibility; data volume is fixed regardless of template.
$null = $Template

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
}
if ($DirectoryConfig.UserObjectClass -ne "inetOrgPerson") {
    throw "Scenario 19 (Auxiliary Classes) is OpenLDAP only. Run-IntegrationTests.ps1 should have rejected this combination before Setup-Scenario19.ps1 was invoked."
}

# Re-derive Source (Yellowstone) and Target (Glitterband) configuration independently of
# whichever single OpenLDAP instance was passed in, mirroring Setup-Scenario14.ps1.
$sourceConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$targetConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target

$sourceSystemName = "Scenario 19 Source"
$targetSystemName = "Scenario 19 Target"

Write-TestSection "Scenario 19 Setup: Auxiliary Classes"

# ============================================================================
# Step 1: Import JIM PowerShell module
# ============================================================================
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  OK JIM PowerShell module imported" -ForegroundColor Green

# ============================================================================
# Step 2: Connect to JIM
# ============================================================================
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) {
    throw "API key required for authentication"
}

Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

# ============================================================================
# Step 2b: Clean up existing configuration from previous runs
# ============================================================================
Write-TestStep "Step 2b" "Cleaning up existing configuration"

$existingSystems = @(Get-JIMConnectedSystem)
foreach ($staleName in @($sourceSystemName, $targetSystemName)) {
    $stale = $existingSystems | Where-Object { $_.name -eq $staleName }
    if ($stale) {
        Write-Host "  Removing existing '$staleName' Connected System..." -ForegroundColor Gray
        # -Force, not $ConfirmPreference: preference variables do not flow into module scope, so
        # Remove-JIMConnectedSystem (ConfirmImpact High) would still prompt and, with no
        # interactive host, fail outright.
        Remove-JIMConnectedSystem -Id $stale.id -DeleteImmediately -Force | Out-Null
        Write-Host "  OK Removed existing '$staleName'" -ForegroundColor Green
    }
}

# ============================================================================
# Step 3: Get LDAP connector definition
# ============================================================================
Write-TestStep "Step 3" "Getting LDAP connector definition"

$connectorDefs = Get-JIMConnectorDefinition
$ldapConnector = $connectorDefs | Where-Object { $_.name -eq "JIM LDAP Connector" }

if (-not $ldapConnector) {
    throw "JIM LDAP Connector definition not found"
}

$ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id
$hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
$portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
$usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
$passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
$useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
$connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
$authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }

Write-Host "  OK Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green

# ============================================================================
# Step 4: Create the two Connected Systems (same OpenLDAP container, two suffixes)
# ============================================================================
Write-TestStep "Step 4" "Creating Connected Systems"

function New-Scenario19ConnectedSystem {
    param([string]$Name, [hashtable]$Config)

    $system = New-JIMConnectedSystem `
        -Name $Name `
        -Description "$Name - OpenLDAP suffix $($Config.BaseDN), auxiliary class testing (#492)" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru

    $settings = @{}
    if ($hostSetting) { $settings[$hostSetting.id] = @{ stringValue = $Config.Host } }
    if ($portSetting) { $settings[$portSetting.id] = @{ intValue = $Config.Port } }
    if ($usernameSetting) { $settings[$usernameSetting.id] = @{ stringValue = $Config.BindDN } }
    if ($passwordSetting) { $settings[$passwordSetting.id] = @{ stringValue = $Config.BindPassword } }
    if ($useSSLSetting) { $settings[$useSSLSetting.id] = @{ checkboxValue = $Config.UseSSL } }
    if ($connectionTimeoutSetting) { $settings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
    if ($authTypeSetting) { $settings[$authTypeSetting.id] = @{ stringValue = $Config.AuthType } }

    if ($settings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $system.id -SettingValues $settings | Out-Null
    }

    Write-Host "  OK Created '$Name' (ID: $($system.id), BaseDN: $($Config.BaseDN))" -ForegroundColor Green
    return $system
}

$sourceSystem = New-Scenario19ConnectedSystem -Name $sourceSystemName -Config $sourceConfig
$targetSystem = New-Scenario19ConnectedSystem -Name $targetSystemName -Config $targetConfig

# ============================================================================
# Step 5: Import schema and select the jimPerson object type + attributes
# ============================================================================
Write-TestStep "Step 5" "Importing LDAP schema"

# uid/employeeNumber: join plumbing. givenName/sn/cn/displayName/mail: identity plumbing.
# roomNumber: the MustEnforcement step's badge-free Badge Colour source (Source side only, but
# selecting it on both is harmless and keeps the two systems symmetric).
# distinguishedName: required for LDAP provisioning (CarrierProvisioning step, Target side).
# objectClass: read-only, but selecting it is what lets JIM import which classes each entry
# already carries, so a convergence export adds only the class an entry lacks rather than
# re-asserting one it has (which the directory would refuse).
$requiredAttributes = @(
    "uid", "entryUUID", "givenName", "sn", "cn", "displayName", "mail", "employeeNumber",
    "roomNumber", "distinguishedName", "objectClass"
)

function Import-Scenario19Schema {
    param([string]$Name, $System)

    Import-JIMConnectedSystemSchema -Id $System.id | Out-Null
    $objectTypes = @(Get-JIMConnectedSystem -Id $System.id -ObjectTypes)
    $userType = $objectTypes | Where-Object { $_.name -eq "jimPerson" }

    if (-not $userType) {
        throw "'jimPerson' object type not found in '$Name' schema"
    }

    Set-JIMConnectedSystemObjectType -ConnectedSystemId $System.id -ObjectTypeId $userType.id -Selected $true | Out-Null

    $missing = @($requiredAttributes | Where-Object { $_ -notin ($userType.attributes | ForEach-Object { $_.name }) })
    if ($missing.Count -gt 0) {
        throw "'$Name' schema is missing required attributes: $($missing -join ', ')"
    }

    $attrUpdates = @{}
    foreach ($attr in $userType.attributes) {
        if ($attr.name -in $requiredAttributes) {
            $attrUpdates[$attr.id] = @{ selected = $true }
        }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $System.id -ObjectTypeId $userType.id -AttributeUpdates $attrUpdates | Out-Null

    Write-Host "  OK '$Name' schema imported, 'jimPerson' selected with $($requiredAttributes.Count) attributes" -ForegroundColor Green

    # Re-fetch so returned attribute objects carry their assigned IDs and resolved types.
    $objectTypes = @(Get-JIMConnectedSystem -Id $System.id -ObjectTypes)
    return $objectTypes | Where-Object { $_.name -eq "jimPerson" }
}

$sourceUserType = Import-Scenario19Schema -Name $sourceSystemName -System $sourceSystem
$targetUserType = Import-Scenario19Schema -Name $targetSystemName -System $targetSystem

# ============================================================================
# Step 6: Import hierarchy and select the suffix partition + People container
# ============================================================================
Write-TestStep "Step 6" "Importing LDAP hierarchy and selecting partition/container"

function Find-Container {
    param($Containers, [string]$Name)
    foreach ($c in $Containers) {
        if ($c.name -eq $Name -or $c.name -match "^OU=$Name") { return $c }
        if ($c.childContainers) {
            $found = Find-Container -Containers $c.childContainers -Name $Name
            if ($found) { return $found }
        }
    }
    return $null
}

function Import-Scenario19Hierarchy {
    param([string]$Name, $System, [hashtable]$Config)

    Import-JIMConnectedSystemHierarchy -Id $System.id | Out-Null

    $partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $System.id)
    $partition = $partitions | Where-Object { $_.name -eq $Config.BaseDN -or $_.externalId -eq $Config.BaseDN } | Select-Object -First 1
    if (-not $partition) {
        throw "'$Name' partition '$($Config.BaseDN)' not found after hierarchy import. Available: $($partitions | ForEach-Object { $_.name } | Join-String -Separator ', ')"
    }

    Set-JIMConnectedSystemPartition -ConnectedSystemId $System.id -PartitionId $partition.id -Selected $true | Out-Null

    # Deselect any other partition (the other suffix) so this system only ever sees its own.
    foreach ($other in $partitions) {
        if ($other.id -ne $partition.id) {
            Set-JIMConnectedSystemPartition -ConnectedSystemId $System.id -PartitionId $other.id -Selected $false | Out-Null
        }
    }

    $targetContainerName = if ($Config.UserContainer -match "^[Oo][Uu]=([^,]+)") { $matches[1] } else { "People" }
    $container = Find-Container -Containers $partition.containers -Name $targetContainerName
    if (-not $container) {
        throw "'$targetContainerName' container not found in '$Name' partition '$($Config.BaseDN)'"
    }
    Set-JIMConnectedSystemContainer -ConnectedSystemId $System.id -ContainerId $container.id -Selected $true | Out-Null

    Write-Host "  OK '$Name' selected partition '$($Config.BaseDN)' and container '$targetContainerName'" -ForegroundColor Green
}

Import-Scenario19Hierarchy -Name $sourceSystemName -System $sourceSystem -Config $sourceConfig
Import-Scenario19Hierarchy -Name $targetSystemName -System $targetSystem -Config $targetConfig

# ============================================================================
# Step 7: Metaverse schema - "User" type plus the Badge attributes
# ============================================================================
Write-TestStep "Step 7" "Getting Metaverse schema and creating Badge attributes"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) {
    throw "No 'User' Metaverse Object Type found in seed data"
}

# The badge values need somewhere to live in the Metaverse, and nothing built-in describes a
# badge; creating the attributes here also exercises the custom-attribute surface end to end.
foreach ($badgeAttrName in @("Badge Number", "Badge Colour")) {
    $existing = @(Get-JIMMetaverseAttribute) | Where-Object { $_.name -eq $badgeAttrName }
    if ($existing) {
        Write-Host "  '$badgeAttrName' Metaverse attribute already exists (ID: $($existing.id))" -ForegroundColor Gray
        continue
    }
    New-JIMMetaverseAttribute -Name $badgeAttrName -Type Text -AttributePlurality SingleValued `
        -ObjectTypeIds @($mvUserType.id) -ChangeReason "Scenario 19 auxiliary class testing (#492)" -Confirm:$false | Out-Null
    Write-Host "  OK Created '$badgeAttrName' Metaverse attribute on 'User'" -ForegroundColor Green
}

$mvAttributes = @(Get-JIMMetaverseAttribute)
Write-Host "  OK Found 'User' Metaverse Object Type (ID: $($mvUserType.id))" -ForegroundColor Green

# ============================================================================
# Step 8: Create the Sync Rules
#
# Source imports and projects. Target's import rule exists only to join Glitterband entries to
# the Metaverse Objects Source projected (via the Employee ID matching rule below); it carries
# no attribute flow mappings, so nothing contests Source's contributions and no Attribute
# Priority configuration is needed. Target's export rule carries no mappings yet either: its
# badge mappings target attributes that only exist after the Merge step refreshes the Target
# schema, so the invoke script adds them there.
# ============================================================================
Write-TestStep "Step 8" "Creating Sync Rules"

$sourceImportRule = New-JIMSyncRule `
    -Name "$sourceSystemName Import Users" `
    -ConnectedSystemId $sourceSystem.id `
    -ConnectedSystemObjectTypeId $sourceUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru
Write-Host "  OK Created '$($sourceImportRule.name)' (ID: $($sourceImportRule.id))" -ForegroundColor Green

$targetImportRule = New-JIMSyncRule `
    -Name "$targetSystemName Import Users" `
    -ConnectedSystemId $targetSystem.id `
    -ConnectedSystemObjectTypeId $targetUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -PassThru
Write-Host "  OK Created '$($targetImportRule.name)' (ID: $($targetImportRule.id), join only)" -ForegroundColor Green

$targetExportRule = New-JIMSyncRule `
    -Name "$targetSystemName Export Users" `
    -ConnectedSystemId $targetSystem.id `
    -ConnectedSystemObjectTypeId $targetUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -PassThru
Write-Host "  OK Created '$($targetExportRule.name)' (ID: $($targetExportRule.id), mappings added by the Merge step)" -ForegroundColor Green

# ============================================================================
# Step 9: Configure Source attribute flow mappings (identity plumbing)
# ============================================================================
Write-TestStep "Step 9" "Configuring Source attribute flow mappings"

$sourceMappings = @(
    @{ LdapAttr = "uid"; MvAttr = "Account Name" }
    @{ LdapAttr = "employeeNumber"; MvAttr = "Employee ID" }
    @{ LdapAttr = "givenName"; MvAttr = "First Name" }
    @{ LdapAttr = "sn"; MvAttr = "Last Name" }
    @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
    @{ LdapAttr = "mail"; MvAttr = "Email" }
)

foreach ($mapping in $sourceMappings) {
    $csAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    if (-not $csAttr -or -not $mvAttr) {
        throw "Could not map $($mapping.LdapAttr) -> $($mapping.MvAttr) for '$sourceSystemName': attribute not found"
    }
    New-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id `
        -TargetMetaverseAttributeId $mvAttr.id `
        -SourceConnectedSystemAttributeId $csAttr.id | Out-Null
}
Write-Host "  OK '$sourceSystemName' attribute flow mappings configured ($($sourceMappings.Count))" -ForegroundColor Green

# ============================================================================
# Step 10: Configure Simple Mode matching rules (join on Employee ID)
#
# Source projects; the matching rules mean the Target system's entries JOIN the projected
# Metaverse Objects rather than sitting unjoined, and a re-imported Source CSO re-joins.
# ============================================================================
Write-TestStep "Step 10" "Configuring Employee ID matching rules"

$mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq "Employee ID" }

function Set-Scenario19MatchingRule {
    param([string]$SystemLabel, $System, $UserType)

    $employeeNumberAttr = $UserType.attributes | Where-Object { $_.name -eq "employeeNumber" }
    if (-not $employeeNumberAttr) {
        throw "'employeeNumber' attribute not found for '$SystemLabel'"
    }

    New-JIMMatchingRule -ConnectedSystemId $System.id `
        -ObjectTypeId $UserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -SourceAttributeId $employeeNumberAttr.id `
        -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
    Write-Host "  OK '$SystemLabel' matching rule configured (employeeNumber -> Employee ID)" -ForegroundColor Green
}

Set-Scenario19MatchingRule -SystemLabel $sourceSystemName -System $sourceSystem -UserType $sourceUserType
Set-Scenario19MatchingRule -SystemLabel $targetSystemName -System $targetSystem -UserType $targetUserType

# ============================================================================
# Step 11: Create Run Profiles
# ============================================================================
Write-TestStep "Step 11" "Creating Run Profiles"

function New-Scenario19RunProfiles {
    param([string]$SystemLabel, $System, [switch]$IncludeExport)

    $profileNames = @("Full Import", "Full Synchronisation")
    if ($IncludeExport) { $profileNames += "Export" }

    foreach ($profileName in $profileNames) {
        $runType = switch ($profileName) {
            "Full Import" { "FullImport" }
            "Full Synchronisation" { "FullSynchronisation" }
            "Export" { "Export" }
        }
        New-JIMRunProfile -Name $profileName -ConnectedSystemId $System.id -RunType $runType -PassThru | Out-Null
        Write-Host "  OK Created '$profileName' for '$SystemLabel'" -ForegroundColor Green
    }
}

New-Scenario19RunProfiles -SystemLabel $sourceSystemName -System $sourceSystem
New-Scenario19RunProfiles -SystemLabel $targetSystemName -System $targetSystem -IncludeExport

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Scenario 19 Setup Complete"
Write-Host "  Source Connected System: $sourceSystemName (ID: $($sourceSystem.id), BaseDN: $($sourceConfig.BaseDN))" -ForegroundColor Cyan
Write-Host "  Target Connected System: $targetSystemName (ID: $($targetSystem.id), BaseDN: $($targetConfig.BaseDN))" -ForegroundColor Cyan
Write-Host "  Badge Metaverse attributes: Badge Number, Badge Colour (on 'User')" -ForegroundColor Cyan
Write-Host "  Merge-dependent configuration (auxiliary selection, badge mappings, carrier) is owned by the invoke script" -ForegroundColor Cyan
