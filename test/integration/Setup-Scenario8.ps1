<#
.SYNOPSIS
    Configure JIM for Scenario 8: Cross-domain Entitlement Synchronisation

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for cross-domain group synchronisation.
    This script is self-contained and creates:
    - Source LDAP Connected System (Quantum Dynamics APAC)
    - Target LDAP Connected System (Quantum Dynamics EMEA)
    - User sync rules (prerequisite for group member resolution)
    - Group sync rules for entitlement management
    - Run Profiles for synchronisation

    NOTE: This script first syncs users, then syncs groups.
    Groups reference users via the member and managedBy attributes,
    so users must exist in the metaverse before groups can be synced.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Setup-Scenario8.ps1 -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario8.ps1 -Template Small -ApiKey "jim_abc123..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 8 Setup: Cross-domain Entitlement Synchronisation"

# ============================================================================
# Step 1: Import JIM PowerShell module
# ============================================================================
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../JIM.PowerShell/JIM/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  ✓ JIM PowerShell module imported" -ForegroundColor Green

# ============================================================================
# Step 2: Connect to JIM
# ============================================================================
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) {
    throw "API key required for authentication"
}

try {
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    Write-Host "  ✓ Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to connect to JIM: $_" -ForegroundColor Red
    throw
}

# ============================================================================
# Step 3: Get connector definitions
# ============================================================================
Write-TestStep "Step 3" "Getting connector definitions"

$connectorDefs = Get-JIMConnectorDefinition
$ldapConnector = $connectorDefs | Where-Object { $_.name -eq "JIM LDAP Connector" }

if (-not $ldapConnector) {
    throw "JIM LDAP Connector definition not found"
}

Write-Host "  ✓ Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green

# Get full connector details for settings
$ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

$hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
$portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
$usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
$passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
$useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
$certValidationSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Certificate Validation" }
$connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
$authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }

# ============================================================================
# Step 4: Create Source LDAP Connected System
# ============================================================================
Write-TestStep "Step 4" "Creating Source LDAP Connected System (Quantum Dynamics APAC)"

$existingSystems = Get-JIMConnectedSystem
$sourceSystem = $existingSystems | Where-Object { $_.name -eq "Quantum Dynamics APAC" }

if ($sourceSystem) {
    Write-Host "  Connected System 'Quantum Dynamics APAC' already exists (ID: $($sourceSystem.id))" -ForegroundColor Yellow
}
else {
    $sourceSystem = New-JIMConnectedSystem `
        -Name "Quantum Dynamics APAC" `
        -Description "Quantum Dynamics APAC Active Directory - Source for cross-domain entitlement sync" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru
    Write-Host "  ✓ Created Source LDAP Connected System (ID: $($sourceSystem.id))" -ForegroundColor Green
}

# Configure LDAP settings for Source
$sourceSettings = @{}
if ($hostSetting) { $sourceSettings[$hostSetting.id] = @{ stringValue = "samba-ad-source" } }
if ($portSetting) { $sourceSettings[$portSetting.id] = @{ intValue = 636 } }
if ($usernameSetting) { $sourceSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=sourcedomain,DC=local" } }
if ($passwordSetting) { $sourceSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" } }
if ($useSSLSetting) { $sourceSettings[$useSSLSetting.id] = @{ checkboxValue = $true } }
if ($certValidationSetting) { $sourceSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" } }
if ($connectionTimeoutSetting) { $sourceSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
if ($authTypeSetting) { $sourceSettings[$authTypeSetting.id] = @{ stringValue = "Simple" } }

if ($sourceSettings.Count -gt 0) {
    Set-JIMConnectedSystem -Id $sourceSystem.id -SettingValues $sourceSettings | Out-Null
    Write-Host "  ✓ Configured Source LDAP settings" -ForegroundColor Green
}

# ============================================================================
# Step 5: Create Target LDAP Connected System
# ============================================================================
Write-TestStep "Step 5" "Creating Target LDAP Connected System (Quantum Dynamics EMEA)"

$targetSystem = $existingSystems | Where-Object { $_.name -eq "Quantum Dynamics EMEA" }

if ($targetSystem) {
    Write-Host "  Connected System 'Quantum Dynamics EMEA' already exists (ID: $($targetSystem.id))" -ForegroundColor Yellow
}
else {
    $targetSystem = New-JIMConnectedSystem `
        -Name "Quantum Dynamics EMEA" `
        -Description "Quantum Dynamics EMEA Active Directory - Target for cross-domain entitlement sync" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru
    Write-Host "  ✓ Created Target LDAP Connected System (ID: $($targetSystem.id))" -ForegroundColor Green
}

# Configure LDAP settings for Target
$targetSettings = @{}
if ($hostSetting) { $targetSettings[$hostSetting.id] = @{ stringValue = "samba-ad-target" } }
if ($portSetting) { $targetSettings[$portSetting.id] = @{ intValue = 636 } }
if ($usernameSetting) { $targetSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=targetdomain,DC=local" } }
if ($passwordSetting) { $targetSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" } }
if ($useSSLSetting) { $targetSettings[$useSSLSetting.id] = @{ checkboxValue = $true } }
if ($certValidationSetting) { $targetSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" } }
if ($connectionTimeoutSetting) { $targetSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
if ($authTypeSetting) { $targetSettings[$authTypeSetting.id] = @{ stringValue = "Simple" } }

if ($targetSettings.Count -gt 0) {
    Set-JIMConnectedSystem -Id $targetSystem.id -SettingValues $targetSettings | Out-Null
    Write-Host "  ✓ Configured Target LDAP settings" -ForegroundColor Green
}

# ============================================================================
# Step 6: Import Schemas and Hierarchy
# ============================================================================
Write-TestStep "Step 6" "Importing Connected System Schemas and Hierarchy"

# Source schema
$sourceObjectTypes = Get-JIMConnectedSystem -Id $sourceSystem.id -ObjectTypes
if ($sourceObjectTypes -and $sourceObjectTypes.Count -gt 0) {
    Write-Host "  Source schema already imported ($($sourceObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Source LDAP schema..." -ForegroundColor Gray
    Import-JIMConnectedSystemSchema -Id $sourceSystem.id -PassThru | Out-Null
    $sourceObjectTypes = Get-JIMConnectedSystem -Id $sourceSystem.id -ObjectTypes
    Write-Host "  ✓ Source schema imported ($($sourceObjectTypes.Count) object types)" -ForegroundColor Green
}

# Source hierarchy
$sourcePartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id
if ($sourcePartitionsCheck -and $sourcePartitionsCheck.Count -gt 0) {
    Write-Host "  Source hierarchy already imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Source LDAP hierarchy..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
    $sourcePartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id
    Write-Host "  ✓ Source hierarchy imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Green
}

# Target schema
$targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes
if ($targetObjectTypes -and $targetObjectTypes.Count -gt 0) {
    Write-Host "  Target schema already imported ($($targetObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Target LDAP schema..." -ForegroundColor Gray
    Import-JIMConnectedSystemSchema -Id $targetSystem.id -PassThru | Out-Null
    $targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes
    Write-Host "  ✓ Target schema imported ($($targetObjectTypes.Count) object types)" -ForegroundColor Green
}

# Target hierarchy
$targetPartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id
if ($targetPartitionsCheck -and $targetPartitionsCheck.Count -gt 0) {
    Write-Host "  Target hierarchy already imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Target LDAP hierarchy..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
    $targetPartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id
    Write-Host "  ✓ Target hierarchy imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Green
}

# ============================================================================
# Step 7: Configure Partitions and Containers
# ============================================================================
Write-TestStep "Step 7" "Configuring Partitions and Containers"

# Helper function to recursively find a container by name
function Find-ContainerByName {
    param(
        [array]$Containers,
        [string]$Name
    )
    foreach ($container in $Containers) {
        if ($container.name -eq $Name) {
            return $container
        }
        if ($container.childContainers -and $container.childContainers.Count -gt 0) {
            $found = Find-ContainerByName -Containers $container.childContainers -Name $Name
            if ($found) {
                return $found
            }
        }
    }
    return $null
}

# Re-import hierarchy to pick up OUs created by population script
Write-Host "  Re-importing hierarchy to discover OUs..." -ForegroundColor Gray
Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
Write-Host "  ✓ Hierarchy re-imported" -ForegroundColor Green

# Configure Source partitions - select domain partition and Corp containers
Write-Host "  Configuring Source LDAP partitions..." -ForegroundColor Gray
$sourcePartitions = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id

$sourceDomainPartition = $sourcePartitions | Where-Object { $_.name -eq "DC=sourcedomain,DC=local" }
if ($sourceDomainPartition) {
    Set-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id -PartitionId $sourceDomainPartition.id -Selected $true | Out-Null
    Write-Host "    ✓ Selected partition: $($sourceDomainPartition.name)" -ForegroundColor Green

    # Find and select Corp container and its children
    $corpContainer = Find-ContainerByName -Containers $sourceDomainPartition.containers -Name "Corp"
    if ($corpContainer) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $sourceSystem.id -ContainerId $corpContainer.id -Selected $true | Out-Null
        Write-Host "    ✓ Selected container: Corp" -ForegroundColor Green

        # Select Users and Entitlements sub-containers
        $usersContainer = Find-ContainerByName -Containers $corpContainer.childContainers -Name "Users"
        if ($usersContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $sourceSystem.id -ContainerId $usersContainer.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected container: Users" -ForegroundColor Green
        }

        $entitlementsContainer = Find-ContainerByName -Containers $corpContainer.childContainers -Name "Entitlements"
        if ($entitlementsContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $sourceSystem.id -ContainerId $entitlementsContainer.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected container: Entitlements" -ForegroundColor Green
        }
    }
    else {
        Write-Host "    ⚠ Corp container not found - run Populate-SambaAD-Scenario8.ps1 first" -ForegroundColor Yellow
    }

    # Deselect other partitions
    foreach ($partition in $sourcePartitions) {
        if ($partition.name -ne "DC=sourcedomain,DC=local") {
            Set-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id -PartitionId $partition.id -Selected $false | Out-Null
        }
    }
}

# Configure Target partitions - select domain partition and CorpManaged containers
Write-Host "  Configuring Target LDAP partitions..." -ForegroundColor Gray
$targetPartitions = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id

$targetDomainPartition = $targetPartitions | Where-Object { $_.name -eq "DC=targetdomain,DC=local" }
if ($targetDomainPartition) {
    Set-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id -PartitionId $targetDomainPartition.id -Selected $true | Out-Null
    Write-Host "    ✓ Selected partition: $($targetDomainPartition.name)" -ForegroundColor Green

    # Find and select CorpManaged container and its children
    $corpManagedContainer = Find-ContainerByName -Containers $targetDomainPartition.containers -Name "CorpManaged"
    if ($corpManagedContainer) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $targetSystem.id -ContainerId $corpManagedContainer.id -Selected $true | Out-Null
        Write-Host "    ✓ Selected container: CorpManaged" -ForegroundColor Green

        $usersContainer = Find-ContainerByName -Containers $corpManagedContainer.childContainers -Name "Users"
        if ($usersContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $targetSystem.id -ContainerId $usersContainer.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected container: Users" -ForegroundColor Green
        }

        $entitlementsContainer = Find-ContainerByName -Containers $corpManagedContainer.childContainers -Name "Entitlements"
        if ($entitlementsContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $targetSystem.id -ContainerId $entitlementsContainer.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected container: Entitlements" -ForegroundColor Green
        }
    }
    else {
        Write-Host "    ⚠ CorpManaged container not found - run Populate-SambaAD-Scenario8.ps1 -Instance Target first" -ForegroundColor Yellow
    }

    # Deselect other partitions
    foreach ($partition in $targetPartitions) {
        if ($partition.name -ne "DC=targetdomain,DC=local") {
            Set-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id -PartitionId $partition.id -Selected $false | Out-Null
        }
    }
}

Write-Host "  ✓ Partitions and containers configured" -ForegroundColor Green

# ============================================================================
# Step 8: Configure Object Types and Attributes
# ============================================================================
Write-TestStep "Step 8" "Configuring Object Types and Attributes"

# Get object types
$sourceUserType = $sourceObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1
$sourceGroupType = $sourceObjectTypes | Where-Object { $_.name -eq "group" } | Select-Object -First 1
$targetUserType = $targetObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1
$targetGroupType = $targetObjectTypes | Where-Object { $_.name -eq "group" } | Select-Object -First 1

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvGroupType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" } | Select-Object -First 1

if (-not $sourceUserType) { throw "No 'user' object type found in Source schema" }
if (-not $sourceGroupType) { throw "No 'group' object type found in Source schema" }
if (-not $targetUserType) { throw "No 'user' object type found in Target schema" }
if (-not $targetGroupType) { throw "No 'group' object type found in Target schema" }
if (-not $mvUserType) { throw "No 'User' object type found in Metaverse" }
if (-not $mvGroupType) { throw "No 'Group' object type found in Metaverse" }

Write-Host "  Found object types:" -ForegroundColor Gray
Write-Host "    Source user: $($sourceUserType.id), group: $($sourceGroupType.id)" -ForegroundColor Gray
Write-Host "    Target user: $($targetUserType.id), group: $($targetGroupType.id)" -ForegroundColor Gray
Write-Host "    Metaverse User: $($mvUserType.id), Group: $($mvGroupType.id)" -ForegroundColor Gray

# Mark object types as selected
Set-JIMConnectedSystemObjectType -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -Selected $true | Out-Null
Set-JIMConnectedSystemObjectType -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceGroupType.id -Selected $true | Out-Null
Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -Selected $true | Out-Null
Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetGroupType.id -Selected $true | Out-Null
Write-Host "  ✓ Selected user and group object types" -ForegroundColor Green

# Set objectGUID as External ID for all object types
$sourceUserAnchor = $sourceUserType.attributes | Where-Object { $_.name -eq 'objectGUID' }
$sourceGroupAnchor = $sourceGroupType.attributes | Where-Object { $_.name -eq 'objectGUID' }
$targetUserAnchor = $targetUserType.attributes | Where-Object { $_.name -eq 'objectGUID' }
$targetGroupAnchor = $targetGroupType.attributes | Where-Object { $_.name -eq 'objectGUID' }

if ($sourceUserAnchor) {
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeId $sourceUserAnchor.id -IsExternalId $true | Out-Null
}
if ($sourceGroupAnchor) {
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceGroupType.id -AttributeId $sourceGroupAnchor.id -IsExternalId $true | Out-Null
}
if ($targetUserAnchor) {
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeId $targetUserAnchor.id -IsExternalId $true | Out-Null
}
if ($targetGroupAnchor) {
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetGroupType.id -AttributeId $targetGroupAnchor.id -IsExternalId $true | Out-Null
}
Write-Host "  ✓ Set objectGUID as External ID for all object types" -ForegroundColor Green

# Select required LDAP attributes for users
$requiredUserAttributes = @(
    'objectGUID', 'sAMAccountName', 'givenName', 'sn', 'displayName', 'cn',
    'mail', 'userPrincipalName', 'title', 'department', 'distinguishedName'
)

# Select required LDAP attributes for groups
$requiredGroupAttributes = @(
    'objectGUID', 'sAMAccountName', 'cn', 'displayName', 'description',
    'groupType', 'member', 'managedBy', 'mail', 'distinguishedName'
)

# Select attributes for Source user
$sourceUserAttrUpdates = @{}
foreach ($attr in $sourceUserType.attributes) {
    if ($requiredUserAttributes -contains $attr.name) {
        $sourceUserAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeUpdates $sourceUserAttrUpdates | Out-Null

# Select attributes for Source group
$sourceGroupAttrUpdates = @{}
foreach ($attr in $sourceGroupType.attributes) {
    if ($requiredGroupAttributes -contains $attr.name) {
        $sourceGroupAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceGroupType.id -AttributeUpdates $sourceGroupAttrUpdates | Out-Null

# Select attributes for Target user
$targetUserAttrUpdates = @{}
foreach ($attr in $targetUserType.attributes) {
    if ($requiredUserAttributes -contains $attr.name) {
        $targetUserAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeUpdates $targetUserAttrUpdates | Out-Null

# Select attributes for Target group
$targetGroupAttrUpdates = @{}
foreach ($attr in $targetGroupType.attributes) {
    if ($requiredGroupAttributes -contains $attr.name) {
        $targetGroupAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetGroupType.id -AttributeUpdates $targetGroupAttrUpdates | Out-Null

Write-Host "  ✓ Selected attributes for users and groups" -ForegroundColor Green

# ============================================================================
# Step 9: Create Sync Rules
# ============================================================================
Write-TestStep "Step 9" "Creating Sync Rules"

$existingRules = Get-JIMSyncRule

# --- User Sync Rules ---
# Source Import (users)
$sourceUserImportRuleName = "APAC AD Import Users"
$sourceUserImportRule = $existingRules | Where-Object { $_.name -eq $sourceUserImportRuleName }
if (-not $sourceUserImportRule) {
    $sourceUserImportRule = New-JIMSyncRule `
        -Name $sourceUserImportRuleName `
        -ConnectedSystemId $sourceSystem.id `
        -ConnectedSystemObjectTypeId $sourceUserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -Direction Import `
        -ProjectToMetaverse `
        -PassThru
    Write-Host "  ✓ Created: $sourceUserImportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$sourceUserImportRuleName' already exists" -ForegroundColor Gray
}

# Target Export (users)
$targetUserExportRuleName = "EMEA AD Export Users"
$targetUserExportRule = $existingRules | Where-Object { $_.name -eq $targetUserExportRuleName }
if (-not $targetUserExportRule) {
    $targetUserExportRule = New-JIMSyncRule `
        -Name $targetUserExportRuleName `
        -ConnectedSystemId $targetSystem.id `
        -ConnectedSystemObjectTypeId $targetUserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -Direction Export `
        -ProvisionToConnectedSystem `
        -PassThru
    Write-Host "  ✓ Created: $targetUserExportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$targetUserExportRuleName' already exists" -ForegroundColor Gray
}

# Target Import (users - for confirming import)
$targetUserImportRuleName = "EMEA AD Import Users"
$targetUserImportRule = $existingRules | Where-Object { $_.name -eq $targetUserImportRuleName }
if (-not $targetUserImportRule) {
    $targetUserImportRule = New-JIMSyncRule `
        -Name $targetUserImportRuleName `
        -ConnectedSystemId $targetSystem.id `
        -ConnectedSystemObjectTypeId $targetUserType.id `
        -MetaverseObjectTypeId $mvUserType.id `
        -Direction Import `
        -PassThru
    Write-Host "  ✓ Created: $targetUserImportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$targetUserImportRuleName' already exists" -ForegroundColor Gray
}

# --- Group Sync Rules ---
# Source Import (groups)
$sourceGroupImportRuleName = "APAC AD Import Groups"
$sourceGroupImportRule = $existingRules | Where-Object { $_.name -eq $sourceGroupImportRuleName }
if (-not $sourceGroupImportRule) {
    $sourceGroupImportRule = New-JIMSyncRule `
        -Name $sourceGroupImportRuleName `
        -ConnectedSystemId $sourceSystem.id `
        -ConnectedSystemObjectTypeId $sourceGroupType.id `
        -MetaverseObjectTypeId $mvGroupType.id `
        -Direction Import `
        -ProjectToMetaverse `
        -PassThru
    Write-Host "  ✓ Created: $sourceGroupImportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$sourceGroupImportRuleName' already exists" -ForegroundColor Gray
}

# Target Export (groups)
$targetGroupExportRuleName = "EMEA AD Export Groups"
$targetGroupExportRule = $existingRules | Where-Object { $_.name -eq $targetGroupExportRuleName }
if (-not $targetGroupExportRule) {
    $targetGroupExportRule = New-JIMSyncRule `
        -Name $targetGroupExportRuleName `
        -ConnectedSystemId $targetSystem.id `
        -ConnectedSystemObjectTypeId $targetGroupType.id `
        -MetaverseObjectTypeId $mvGroupType.id `
        -Direction Export `
        -ProvisionToConnectedSystem `
        -PassThru
    Write-Host "  ✓ Created: $targetGroupExportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$targetGroupExportRuleName' already exists" -ForegroundColor Gray
}

# Target Import (groups - for confirming import)
$targetGroupImportRuleName = "EMEA AD Import Groups"
$targetGroupImportRule = $existingRules | Where-Object { $_.name -eq $targetGroupImportRuleName }
if (-not $targetGroupImportRule) {
    $targetGroupImportRule = New-JIMSyncRule `
        -Name $targetGroupImportRuleName `
        -ConnectedSystemId $targetSystem.id `
        -ConnectedSystemObjectTypeId $targetGroupType.id `
        -MetaverseObjectTypeId $mvGroupType.id `
        -Direction Import `
        -PassThru
    Write-Host "  ✓ Created: $targetGroupImportRuleName" -ForegroundColor Green
}
else {
    Write-Host "  Sync rule '$targetGroupImportRuleName' already exists" -ForegroundColor Gray
}

# ============================================================================
# Step 10: Configure Attribute Flow Mappings
# ============================================================================
Write-TestStep "Step 10" "Configuring Attribute Flow Mappings"

$mvAttributes = Get-JIMMetaverseAttribute

# --- User Mappings ---
Write-Host "  Configuring user attribute mappings..." -ForegroundColor Gray

$userImportMappings = @(
    @{ LdapAttr = "sAMAccountName"; MvAttr = "Account Name" }
    @{ LdapAttr = "givenName"; MvAttr = "First Name" }
    @{ LdapAttr = "sn"; MvAttr = "Last Name" }
    @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
    @{ LdapAttr = "mail"; MvAttr = "Email" }
    @{ LdapAttr = "title"; MvAttr = "Job Title" }
    @{ LdapAttr = "department"; MvAttr = "Department" }
)

$userExportMappings = @(
    @{ MvAttr = "Account Name"; LdapAttr = "sAMAccountName" }
    @{ MvAttr = "First Name"; LdapAttr = "givenName" }
    @{ MvAttr = "Last Name"; LdapAttr = "sn" }
    @{ MvAttr = "Display Name"; LdapAttr = "displayName" }
    @{ MvAttr = "Display Name"; LdapAttr = "cn" }
    @{ MvAttr = "Email"; LdapAttr = "mail" }
    @{ MvAttr = "Email"; LdapAttr = "userPrincipalName" }
    @{ MvAttr = "Job Title"; LdapAttr = "title" }
    @{ MvAttr = "Department"; LdapAttr = "department" }
)

# Create user import mappings (Source -> MV)
$existingSourceUserImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $sourceUserImportRule.id
$userImportMappingsCreated = 0
foreach ($mapping in $userImportMappings) {
    $ldapAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    if ($ldapAttr -and $mvAttr) {
        $existsAlready = $existingSourceUserImportMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $mvAttr.id -and
            ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $ldapAttr.id })
        }
        if (-not $existsAlready) {
            try {
                New-JIMSyncRuleMapping -SyncRuleId $sourceUserImportRule.id `
                    -TargetMetaverseAttributeId $mvAttr.id `
                    -SourceConnectedSystemAttributeId $ldapAttr.id | Out-Null
                $userImportMappingsCreated++
            }
            catch { }
        }
    }
}
Write-Host "    ✓ Source user import mappings ($userImportMappingsCreated new)" -ForegroundColor Green

# Create user export mappings (MV -> Target)
$existingTargetUserExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $targetUserExportRule.id
$userExportMappingsCreated = 0
foreach ($mapping in $userExportMappings) {
    $ldapAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    if ($ldapAttr -and $mvAttr) {
        $existsAlready = $existingTargetUserExportMappings | Where-Object {
            $_.targetConnectedSystemAttributeId -eq $ldapAttr.id -and
            ($_.sources | Where-Object { $_.metaverseAttributeId -eq $mvAttr.id })
        }
        if (-not $existsAlready) {
            try {
                New-JIMSyncRuleMapping -SyncRuleId $targetUserExportRule.id `
                    -TargetConnectedSystemAttributeId $ldapAttr.id `
                    -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                $userExportMappingsCreated++
            }
            catch { }
        }
    }
}

# User DN expression for target
$targetUserDnAttr = $targetUserType.attributes | Where-Object { $_.name -eq 'distinguishedName' }
if ($targetUserDnAttr) {
    $dnMappingExists = $existingTargetUserExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $targetUserDnAttr.id }
    if (-not $dnMappingExists) {
        New-JIMSyncRuleMapping -SyncRuleId $targetUserExportRule.id `
            -TargetConnectedSystemAttributeId $targetUserDnAttr.id `
            -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,OU=CorpManaged,DC=targetdomain,DC=local"' | Out-Null
        $userExportMappingsCreated++
    }
}
Write-Host "    ✓ Target user export mappings ($userExportMappingsCreated new)" -ForegroundColor Green

# --- Group Mappings ---
Write-Host "  Configuring group attribute mappings..." -ForegroundColor Gray

$groupImportMappings = @(
    @{ LdapAttr = "sAMAccountName"; MvAttr = "Account Name" }
    @{ LdapAttr = "cn"; MvAttr = "Common Name" }
    @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
    @{ LdapAttr = "description"; MvAttr = "Description" }
    @{ LdapAttr = "groupType"; MvAttr = "Group Type Flags" }
    @{ LdapAttr = "mail"; MvAttr = "Email" }
    @{ LdapAttr = "member"; MvAttr = "Static Members" }
    @{ LdapAttr = "managedBy"; MvAttr = "Managed By" }
)

$groupExportMappings = @(
    @{ MvAttr = "Account Name"; LdapAttr = "sAMAccountName" }
    @{ MvAttr = "Display Name"; LdapAttr = "cn" }
    @{ MvAttr = "Display Name"; LdapAttr = "displayName" }
    @{ MvAttr = "Description"; LdapAttr = "description" }
    @{ MvAttr = "Group Type Flags"; LdapAttr = "groupType" }
    @{ MvAttr = "Email"; LdapAttr = "mail" }
    @{ MvAttr = "Static Members"; LdapAttr = "member" }
    @{ MvAttr = "Managed By"; LdapAttr = "managedBy" }
)

# Create group import mappings (Source -> MV)
$existingSourceGroupImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $sourceGroupImportRule.id
$groupImportMappingsCreated = 0
foreach ($mapping in $groupImportMappings) {
    $ldapAttr = $sourceGroupType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    if ($ldapAttr -and $mvAttr) {
        $existsAlready = $existingSourceGroupImportMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $mvAttr.id -and
            ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $ldapAttr.id })
        }
        if (-not $existsAlready) {
            try {
                New-JIMSyncRuleMapping -SyncRuleId $sourceGroupImportRule.id `
                    -TargetMetaverseAttributeId $mvAttr.id `
                    -SourceConnectedSystemAttributeId $ldapAttr.id | Out-Null
                $groupImportMappingsCreated++
            }
            catch { }
        }
    }
}
Write-Host "    ✓ Source group import mappings ($groupImportMappingsCreated new)" -ForegroundColor Green

# Create group export mappings (MV -> Target)
$existingTargetGroupExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $targetGroupExportRule.id
$groupExportMappingsCreated = 0
foreach ($mapping in $groupExportMappings) {
    $ldapAttr = $targetGroupType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }
    if ($ldapAttr -and $mvAttr) {
        $existsAlready = $existingTargetGroupExportMappings | Where-Object {
            $_.targetConnectedSystemAttributeId -eq $ldapAttr.id -and
            ($_.sources | Where-Object { $_.metaverseAttributeId -eq $mvAttr.id })
        }
        if (-not $existsAlready) {
            try {
                New-JIMSyncRuleMapping -SyncRuleId $targetGroupExportRule.id `
                    -TargetConnectedSystemAttributeId $ldapAttr.id `
                    -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                $groupExportMappingsCreated++
            }
            catch { }
        }
    }
}

# Group DN expression for target
$targetGroupDnAttr = $targetGroupType.attributes | Where-Object { $_.name -eq 'distinguishedName' }
if ($targetGroupDnAttr) {
    $dnMappingExists = $existingTargetGroupExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $targetGroupDnAttr.id }
    if (-not $dnMappingExists) {
        New-JIMSyncRuleMapping -SyncRuleId $targetGroupExportRule.id `
            -TargetConnectedSystemAttributeId $targetGroupDnAttr.id `
            -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Entitlements,OU=CorpManaged,DC=targetdomain,DC=local"' | Out-Null
        $groupExportMappingsCreated++
    }
}
Write-Host "    ✓ Target group export mappings ($groupExportMappingsCreated new)" -ForegroundColor Green

# ============================================================================
# Step 11: Configure Matching Rules
# ============================================================================
Write-TestStep "Step 11" "Configuring Matching Rules"

$mvAccountNameAttr = $mvAttributes | Where-Object { $_.name -eq 'Account Name' }

# Source user matching rule
$sourceUserSamAttr = $sourceUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
if ($sourceUserSamAttr -and $mvAccountNameAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $sourceSystem.id `
            -ObjectTypeId $sourceUserType.id `
            -TargetMetaverseAttributeId $mvAccountNameAttr.id `
            -SourceAttributeId $sourceUserSamAttr.id | Out-Null
        Write-Host "  ✓ Source user matching rule (sAMAccountName → Account Name)" -ForegroundColor Green
    }
    else {
        Write-Host "  Source user matching rule already exists" -ForegroundColor Gray
    }
}

# Target user matching rule
$targetUserSamAttr = $targetUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
if ($targetUserSamAttr -and $mvAccountNameAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $targetSystem.id `
            -ObjectTypeId $targetUserType.id `
            -TargetMetaverseAttributeId $mvAccountNameAttr.id `
            -SourceAttributeId $targetUserSamAttr.id | Out-Null
        Write-Host "  ✓ Target user matching rule (sAMAccountName → Account Name)" -ForegroundColor Green
    }
    else {
        Write-Host "  Target user matching rule already exists" -ForegroundColor Gray
    }
}

# Source group matching rule
$sourceGroupSamAttr = $sourceGroupType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
if ($sourceGroupSamAttr -and $mvAccountNameAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceGroupType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $sourceSystem.id `
            -ObjectTypeId $sourceGroupType.id `
            -TargetMetaverseAttributeId $mvAccountNameAttr.id `
            -SourceAttributeId $sourceGroupSamAttr.id | Out-Null
        Write-Host "  ✓ Source group matching rule (sAMAccountName → Account Name)" -ForegroundColor Green
    }
    else {
        Write-Host "  Source group matching rule already exists" -ForegroundColor Gray
    }
}

# Target group matching rule
$targetGroupSamAttr = $targetGroupType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
if ($targetGroupSamAttr -and $mvAccountNameAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetGroupType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $targetSystem.id `
            -ObjectTypeId $targetGroupType.id `
            -TargetMetaverseAttributeId $mvAccountNameAttr.id `
            -SourceAttributeId $targetGroupSamAttr.id | Out-Null
        Write-Host "  ✓ Target group matching rule (sAMAccountName → Account Name)" -ForegroundColor Green
    }
    else {
        Write-Host "  Target group matching rule already exists" -ForegroundColor Gray
    }
}

# ============================================================================
# Step 12: Create Run Profiles
# ============================================================================
Write-TestStep "Step 12" "Creating Run Profiles"

$sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
$targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id

# Source run profiles
foreach ($profileName in @("Full Import", "Full Sync", "Export")) {
    $runType = switch ($profileName) {
        "Full Import" { "FullImport" }
        "Full Sync" { "FullSynchronisation" }
        "Export" { "Export" }
    }
    $profile = $sourceProfiles | Where-Object { $_.name -eq $profileName }
    if (-not $profile) {
        New-JIMRunProfile -Name $profileName -ConnectedSystemId $sourceSystem.id -RunType $runType -PassThru | Out-Null
        Write-Host "  ✓ Created '$profileName' for Source (APAC)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile '$profileName' already exists for Source (APAC)" -ForegroundColor Gray
    }
}

# Target run profiles
foreach ($profileName in @("Full Import", "Full Sync", "Export")) {
    $runType = switch ($profileName) {
        "Full Import" { "FullImport" }
        "Full Sync" { "FullSynchronisation" }
        "Export" { "Export" }
    }
    $profile = $targetProfiles | Where-Object { $_.name -eq $profileName }
    if (-not $profile) {
        New-JIMRunProfile -Name $profileName -ConnectedSystemId $targetSystem.id -RunType $runType -PassThru | Out-Null
        Write-Host "  ✓ Created '$profileName' for Target (EMEA)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile '$profileName' already exists for Target (EMEA)" -ForegroundColor Gray
    }
}

# ============================================================================
# Step 13: Restart Worker
# ============================================================================
Write-TestStep "Step 13" "Restarting JIM.Worker to reload schema"

docker restart jim.worker > $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ JIM.Worker restarted" -ForegroundColor Green
    Start-Sleep -Seconds 3
}
else {
    Write-Host "  ⚠ Failed to restart JIM.Worker" -ForegroundColor Yellow
}

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Setup Complete"
Write-Host "Template:          $Template" -ForegroundColor Cyan
Write-Host "Source System ID:  $($sourceSystem.id)" -ForegroundColor Cyan
Write-Host "Target System ID:  $($targetSystem.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Scenario 8 setup complete" -ForegroundColor Green
Write-Host ""
Write-Host "Sync Rules Created:" -ForegroundColor Yellow
Write-Host "  Users:  APAC AD -> Metaverse -> EMEA AD" -ForegroundColor Gray
Write-Host "  Groups: APAC AD -> Metaverse -> EMEA AD" -ForegroundColor Gray
Write-Host ""
Write-Host "Run Profiles Created:" -ForegroundColor Yellow
Write-Host "  Quantum Dynamics APAC: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host "  Quantum Dynamics EMEA: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run Source Full Import to import users and groups" -ForegroundColor Gray
Write-Host "  2. Run Source Full Sync to project to metaverse" -ForegroundColor Gray
Write-Host "  3. Review groups in metaverse (this is the pause point)" -ForegroundColor Gray
