# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 14: Attribute Priority

.DESCRIPTION
    Sets up two LDAP Connected Systems over the same OpenLDAP container's two suffixes
    (Yellowstone and Glitterband, added by docker/openldap/scripts/01-add-second-suffix.sh) so
    that both import to the same Metaverse "User" object type and JOIN on Employee ID rather
    than each projecting its own object. This gives every joined Metaverse Object two
    simultaneous import contributors, exercising Attribute Priority resolution (#91).

    Creates:
    - "Scenario 14 Primary" Connected System, base DN = dc=yellowstone,dc=local
    - "Scenario 14 Secondary" Connected System, base DN = dc=glitterband,dc=local
    - An Import Sync Rule per system (both ProjectToMetaverse; a Simple Mode matching rule on
      Employee ID means whichever system imports second JOINS rather than duplicate-projects)
    - Attribute flow mappings for identity plumbing (Account Name, First Name, Last Name,
      Display Name, Email, Employee ID) and for the attributes this scenario contests
      (Description, Job Title, Manager, Other Telephones)
    - Attribute Priority configured so Primary = priority 1, Secondary = priority 2 for every
      Metaverse attribute both systems flow into, read back and logged for verification
    - Full Import, Delta Import, Full Synchronisation and Delta Synchronisation Run Profiles per
      system (Delta profiles are unused by the BaselineResolution step but scaffolded now so
      later phases, e.g. re-election on a value withdrawal, do not need to revisit this script)

    This scenario is OpenLDAP only: the two-suffix mechanism it depends on has no Samba AD
    equivalent. Run-IntegrationTests.ps1 hard-fails a Samba AD or "All" -DirectoryType request
    for Scenario 14 before this script is ever invoked.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own small, fixed, deterministic
    user set (see Populate-OpenLDAP-Scenario14.ps1) and ignores the template.

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig. Only OpenLDAP is
    supported; the Primary/Secondary suffix configuration is always re-derived from
    Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source/Target regardless of exactly
    which OpenLDAP instance is passed in, mirroring Setup-Scenario8.ps1's Source/Target split.

.EXAMPLE
    ./Setup-Scenario14.ps1 -ApiKey "jim_abc123..."
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
    throw "Scenario 14 (Attribute Priority) is OpenLDAP only. Run-IntegrationTests.ps1 should have rejected this combination before Setup-Scenario14.ps1 was invoked; check the -DirectoryType hard-fail near Test-LongTailTemplateCompatibility."
}

# Re-derive Primary (Yellowstone) and Secondary (Glitterband) configuration independently of
# whichever single OpenLDAP instance was passed in, mirroring Setup-Scenario8.ps1.
$primaryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$secondaryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target

$primarySystemName = "Scenario 14 Primary"
$secondarySystemName = "Scenario 14 Secondary"

Write-TestSection "Scenario 14 Setup: Attribute Priority"

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

try {
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    Write-Host "  OK Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  FAIL Failed to connect to JIM: $_" -ForegroundColor Red
    throw
}

# ============================================================================
# Step 2b: Clean up existing configuration from previous runs
# ============================================================================
Write-TestStep "Step 2b" "Cleaning up existing configuration"

$existingSystems = @(Get-JIMConnectedSystem)
foreach ($staleName in @($primarySystemName, $secondarySystemName)) {
    $stale = $existingSystems | Where-Object { $_.name -eq $staleName }
    if ($stale) {
        Write-Host "  Removing existing '$staleName' Connected System..." -ForegroundColor Gray
        Remove-JIMConnectedSystem -Id $stale.id | Out-Null
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

function New-Scenario14ConnectedSystem {
    param([string]$Name, [hashtable]$Config)

    $system = New-JIMConnectedSystem `
        -Name $Name `
        -Description "$Name - OpenLDAP suffix $($Config.BaseDN), Attribute Priority testing (#91)" `
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

$primarySystem = New-Scenario14ConnectedSystem -Name $primarySystemName -Config $primaryConfig
$secondarySystem = New-Scenario14ConnectedSystem -Name $secondarySystemName -Config $secondaryConfig

# ============================================================================
# Step 5: Import schema and select the Person object type + attributes
# ============================================================================
Write-TestStep "Step 5" "Importing LDAP schema"

# uid/employeeNumber: join plumbing. givenName/sn/displayName/mail: identity plumbing.
# description/title/manager/telephoneNumber: the attributes this scenario contests.
$requiredAttributes = @(
    "uid", "entryUUID", "givenName", "sn", "displayName", "cn", "mail", "employeeNumber",
    "description", "title", "manager", "telephoneNumber", "distinguishedName"
)

function Import-Scenario14Schema {
    param([string]$Name, $System)

    Import-JIMConnectedSystemSchema -Id $System.id | Out-Null
    $objectTypes = @(Get-JIMConnectedSystem -Id $System.id -ObjectTypes)
    $userType = $objectTypes | Where-Object { $_.name -eq "inetOrgPerson" }

    if (-not $userType) {
        throw "'inetOrgPerson' object type not found in '$Name' schema"
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

    Write-Host "  OK '$Name' schema imported, 'inetOrgPerson' selected with $($requiredAttributes.Count) attributes" -ForegroundColor Green

    # Re-fetch so returned attribute objects carry their assigned IDs and resolved types.
    $objectTypes = @(Get-JIMConnectedSystem -Id $System.id -ObjectTypes)
    return $objectTypes | Where-Object { $_.name -eq "inetOrgPerson" }
}

$primaryUserType = Import-Scenario14Schema -Name $primarySystemName -System $primarySystem
$secondaryUserType = Import-Scenario14Schema -Name $secondarySystemName -System $secondarySystem

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

function Import-Scenario14Hierarchy {
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

Import-Scenario14Hierarchy -Name $primarySystemName -System $primarySystem -Config $primaryConfig
Import-Scenario14Hierarchy -Name $secondarySystemName -System $secondarySystem -Config $secondaryConfig

# ============================================================================
# Step 7: Get Metaverse "User" object type and attributes
# ============================================================================
Write-TestStep "Step 7" "Getting Metaverse schema"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) {
    throw "No 'User' Metaverse Object Type found in seed data"
}
$mvAttributes = @(Get-JIMMetaverseAttribute)

Write-Host "  OK Found 'User' Metaverse Object Type (ID: $($mvUserType.id))" -ForegroundColor Green

# ============================================================================
# Step 8: Create the two Import Sync Rules
# ============================================================================
Write-TestStep "Step 8" "Creating Import Sync Rules"

function New-Scenario14ImportRule {
    param([string]$SystemLabel, $System, $UserType)

    $ruleName = "$SystemLabel Import Users"
    $existing = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $ruleName }
    if ($existing) {
        Write-Host "  '$ruleName' already exists (ID: $($existing.id))" -ForegroundColor Gray
        return $existing
    }

    $rule = New-JIMSyncRule `
        -Name $ruleName `
        -ConnectedSystemId $System.id `
        -ConnectedSystemObjectTypeId $UserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -Direction Import `
        -ProjectToMetaverse `
        -PassThru

    Write-Host "  OK Created '$ruleName' (ID: $($rule.id))" -ForegroundColor Green
    return $rule
}

$primaryImportRule = New-Scenario14ImportRule -SystemLabel $primarySystemName -System $primarySystem -UserType $primaryUserType
$secondaryImportRule = New-Scenario14ImportRule -SystemLabel $secondarySystemName -System $secondarySystem -UserType $secondaryUserType

# ============================================================================
# Step 9: Configure attribute flow mappings
#
# Every mapping targets a Metaverse attribute that BOTH systems flow into, so every one of
# them needs an explicit Attribute Priority order (Step 10) rather than relying on the
# SyncRuleMapping.Priority default (int.MaxValue for both, an undefined tie).
# ============================================================================
Write-TestStep "Step 9" "Configuring attribute flow mappings"

# LdapAttr -> MvAttr. Identity plumbing first, then the attributes this scenario contests
# (Description, Job Title, Manager, Other Telephones), per the plan's precedent in
# Setup-Scenario8.ps1 / Setup-Scenario9.ps1.
$sharedMappings = @(
    @{ LdapAttr = "uid"; MvAttr = "Account Name" }
    @{ LdapAttr = "employeeNumber"; MvAttr = "Employee ID" }
    @{ LdapAttr = "givenName"; MvAttr = "First Name" }
    @{ LdapAttr = "sn"; MvAttr = "Last Name" }
    @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
    @{ LdapAttr = "mail"; MvAttr = "Email" }
    @{ LdapAttr = "description"; MvAttr = "Description" }
    @{ LdapAttr = "title"; MvAttr = "Job Title" }
    @{ LdapAttr = "manager"; MvAttr = "Manager" }
    @{ LdapAttr = "telephoneNumber"; MvAttr = "Other Telephones" }
)

# AttributeName -> @{ PrimaryMappingId; SecondaryMappingId }, fed into Step 10.
$mappingIdsByAttribute = @{}

function Set-Scenario14Mappings {
    param([string]$SystemLabel, $Rule, $UserType, [string]$MappingIdKey)

    $existingMappings = Get-JIMSyncRuleMapping -SyncRuleId $Rule.id
    $createdCount = 0

    foreach ($mapping in $sharedMappings) {
        $csAttr = $UserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
        $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
        if (-not $csAttr -or -not $mvAttr) {
            throw "Could not map $($mapping.LdapAttr) -> $($mapping.MvAttr) for '$SystemLabel': attribute not found"
        }

        $existing = $existingMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $mvAttr.id -and
            ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $csAttr.id })
        }

        $mappingId = if ($existing) {
            $existing.id
        }
        else {
            $created = New-JIMSyncRuleMapping -SyncRuleId $Rule.id `
                -TargetMetaverseAttributeId $mvAttr.id `
                -SourceConnectedSystemAttributeId $csAttr.id
            $createdCount++
            $created.id
        }

        if (-not $mappingIdsByAttribute.ContainsKey($mapping.MvAttr)) {
            $mappingIdsByAttribute[$mapping.MvAttr] = @{}
        }
        $mappingIdsByAttribute[$mapping.MvAttr][$MappingIdKey] = $mappingId
    }

    Write-Host "  OK '$SystemLabel' attribute flow mappings configured ($createdCount new, $($sharedMappings.Count) total)" -ForegroundColor Green
}

Set-Scenario14Mappings -SystemLabel $primarySystemName -Rule $primaryImportRule -UserType $primaryUserType -MappingIdKey "Primary"
Set-Scenario14Mappings -SystemLabel $secondarySystemName -Rule $secondaryImportRule -UserType $secondaryUserType -MappingIdKey "Secondary"

# ============================================================================
# Step 10: Configure Attribute Priority (Primary = 1, Secondary = 2) and read it back
# ============================================================================
Write-TestStep "Step 10" "Configuring Attribute Priority (Primary=1, Secondary=2 for every shared attribute)"

foreach ($mapping in $sharedMappings) {
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    $ids = $mappingIdsByAttribute[$mapping.MvAttr]

    Set-JIMMetaverseAttributePriority -AttributeId $mvAttr.id -ObjectTypeId $mvUserType.id `
        -MappingId @($ids.Primary, $ids.Secondary) | Out-Null

    $readBack = Get-JIMMetaverseAttributePriority -AttributeId $mvAttr.id -ObjectTypeId $mvUserType.id
    $contributors = @($readBack.contributors)
    if ($contributors.Count -ne 2 -or
        $contributors[0].mappingId -ne $ids.Primary -or $contributors[0].priority -ne 1 -or
        $contributors[1].mappingId -ne $ids.Secondary -or $contributors[1].priority -ne 2) {
        throw "Attribute Priority read-back mismatch for '$($mapping.MvAttr)': expected Primary(1)/Secondary(2), got $(@($contributors | ForEach-Object { "$($_.connectedSystemName)=$($_.priority)" }) -join ', ')"
    }

    Write-Host "  OK '$($mapping.MvAttr)': $($contributors[0].connectedSystemName)=1, $($contributors[1].connectedSystemName)=2" -ForegroundColor Green
}

# ============================================================================
# Step 11: Configure Simple Mode matching rules (join on Employee ID)
#
# Both systems keep -ProjectToMetaverse; whichever imports+syncs first projects a new
# Metaverse Object, and the matching rule below means whichever syncs second JOINS that
# object via Employee ID instead of projecting a duplicate.
# ============================================================================
Write-TestStep "Step 11" "Configuring Employee ID matching rules"

$mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq "Employee ID" }

function Set-Scenario14MatchingRule {
    param([string]$SystemLabel, $System, $UserType)

    $employeeNumberAttr = $UserType.attributes | Where-Object { $_.name -eq "employeeNumber" }
    if (-not $employeeNumberAttr) {
        throw "'employeeNumber' attribute not found for '$SystemLabel'"
    }

    $existingRules = Get-JIMMatchingRule -ConnectedSystemId $System.id -ObjectTypeId $UserType.id
    $exists = $existingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvEmployeeIdAttr.id -and
        ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $employeeNumberAttr.id })
    }

    if ($exists) {
        Write-Host "  '$SystemLabel' matching rule already exists" -ForegroundColor Gray
        return
    }

    New-JIMMatchingRule -ConnectedSystemId $System.id `
        -ObjectTypeId $UserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -SourceAttributeId $employeeNumberAttr.id `
        -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
    Write-Host "  OK '$SystemLabel' matching rule configured (employeeNumber -> Employee ID)" -ForegroundColor Green
}

Set-Scenario14MatchingRule -SystemLabel $primarySystemName -System $primarySystem -UserType $primaryUserType
Set-Scenario14MatchingRule -SystemLabel $secondarySystemName -System $secondarySystem -UserType $secondaryUserType

# ============================================================================
# Step 12: Create Run Profiles
#
# Delta Import / Delta Synchronisation are scaffolded now (unused by BaselineResolution)
# so later phases (re-election on an in-place value withdrawal) do not need to revisit
# this script.
# ============================================================================
Write-TestStep "Step 12" "Creating Run Profiles"

function New-Scenario14RunProfiles {
    param([string]$SystemLabel, $System)

    $existingProfiles = @(Get-JIMRunProfile -ConnectedSystemId $System.id)
    foreach ($profileName in @("Full Import", "Delta Import", "Full Synchronisation", "Delta Synchronisation")) {
        $runType = switch ($profileName) {
            "Full Import" { "FullImport" }
            "Delta Import" { "DeltaImport" }
            "Full Synchronisation" { "FullSynchronisation" }
            "Delta Synchronisation" { "DeltaSynchronisation" }
        }
        $existing = $existingProfiles | Where-Object { $_.name -eq $profileName }
        if (-not $existing) {
            New-JIMRunProfile -Name $profileName -ConnectedSystemId $System.id -RunType $runType -PassThru | Out-Null
            Write-Host "  OK Created '$profileName' for '$SystemLabel'" -ForegroundColor Green
        }
        else {
            Write-Host "  '$profileName' already exists for '$SystemLabel'" -ForegroundColor Gray
        }
    }
}

New-Scenario14RunProfiles -SystemLabel $primarySystemName -System $primarySystem
New-Scenario14RunProfiles -SystemLabel $secondarySystemName -System $secondarySystem

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Scenario 14 Setup Complete"
Write-Host "  Primary Connected System:   $primarySystemName (ID: $($primarySystem.id), BaseDN: $($primaryConfig.BaseDN))" -ForegroundColor Cyan
Write-Host "  Secondary Connected System: $secondarySystemName (ID: $($secondarySystem.id), BaseDN: $($secondaryConfig.BaseDN))" -ForegroundColor Cyan
Write-Host "  Shared attributes with priority configured: $($sharedMappings.MvAttr -join ', ')" -ForegroundColor Cyan
Write-Host "  Priority order: $primarySystemName = 1, $secondarySystemName = 2" -ForegroundColor Cyan
