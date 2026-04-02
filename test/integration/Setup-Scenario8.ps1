<#
.SYNOPSIS
    Configure JIM for Scenario 8: Cross-domain Entitlement Synchronisation

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for cross-domain group synchronisation.
    This script is self-contained and creates:
    - Source LDAP Connected System (Panoply APAC)
    - Target LDAP Connected System (Panoply EMEA)
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
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

# Derive directory-specific configuration
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Source
}

$isOpenLDAP = ($DirectoryConfig.UserObjectClass -eq "inetOrgPerson")

# Derive Source and Target configs from the DirectoryConfig
if ($isOpenLDAP) {
    $sourceConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
    $targetConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target
}
else {
    $sourceConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Source
    $targetConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Target
}

# Directory-specific variables used throughout setup
$sourceSystemName  = $sourceConfig.ConnectedSystemName
$targetSystemName  = $targetConfig.ConnectedSystemName
$sourceHost        = $sourceConfig.Host
$targetHost        = $targetConfig.Host
$sourcePort        = $sourceConfig.Port
$targetPort        = $targetConfig.Port
$sourceBindDN      = $sourceConfig.BindDN
$targetBindDN      = $targetConfig.BindDN
$sourcePassword    = $sourceConfig.BindPassword
$targetPassword    = $targetConfig.BindPassword
$sourceUseSSL      = $sourceConfig.UseSSL
$targetUseSSL      = $targetConfig.UseSSL
$sourceBaseDN      = $sourceConfig.BaseDN
$targetBaseDN      = $targetConfig.BaseDN
$userObjectClass   = $sourceConfig.UserObjectClass
$groupObjectClass  = $sourceConfig.GroupObjectClass

# Object type names for schema lookup
$userTypeName  = $userObjectClass    # "user" for AD, "inetOrgPerson" for OpenLDAP
$groupTypeName = $groupObjectClass   # "group" for AD, "groupOfNames" for OpenLDAP

# Attribute lists differ by directory type
if ($isOpenLDAP) {
    # OpenLDAP uses entryUUID (auto-set by connector), uid, departmentNumber
    # No userAccountControl, extensionAttribute1, userPrincipalName, company, groupType, managedBy
    $requiredUserAttributes = @(
        'entryUUID', 'uid', 'givenName', 'sn', 'displayName', 'cn',
        'mail', 'title', 'departmentNumber', 'employeeNumber', 'distinguishedName'
    )
    $requiredGroupAttributes = @(
        'entryUUID', 'cn', 'description', 'member', 'distinguishedName'
    )

    # Source container layout: ou=People, ou=Groups under suffix
    $sourceUserContainerName    = "People"
    $sourceGroupContainerName   = "Groups"
    # Target container layout: ou=People, ou=Groups under suffix
    $targetUserContainerName    = "People"
    $targetGroupContainerName   = "Groups"
    # No parent OU nesting for OpenLDAP (flat under suffix)
    $sourceUserContainerParent  = $null
    $sourceGroupContainerParent = $null
    $targetUserContainerParent  = $null
    $targetGroupContainerParent = $null

    # User attribute mappings for OpenLDAP
    $userImportMappings = @(
        @{ LdapAttr = "uid"; MvAttr = "Account Name" }
        @{ LdapAttr = "givenName"; MvAttr = "First Name" }
        @{ LdapAttr = "sn"; MvAttr = "Last Name" }
        @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
        @{ LdapAttr = "mail"; MvAttr = "Email" }
        @{ LdapAttr = "title"; MvAttr = "Job Title" }
        @{ LdapAttr = "departmentNumber"; MvAttr = "Department" }
    )
    $userExportMappings = @(
        @{ MvAttr = "Account Name"; LdapAttr = "uid" }
        @{ MvAttr = "First Name"; LdapAttr = "givenName" }
        @{ MvAttr = "Last Name"; LdapAttr = "sn" }
        @{ MvAttr = "Display Name"; LdapAttr = "displayName" }
        @{ MvAttr = "Display Name"; LdapAttr = "cn" }
        @{ MvAttr = "Email"; LdapAttr = "mail" }
        @{ MvAttr = "Job Title"; LdapAttr = "title" }
        @{ MvAttr = "Department"; LdapAttr = "departmentNumber" }
    )

    # Group attribute mappings for OpenLDAP
    $groupImportMappings = @(
        @{ LdapAttr = "cn"; MvAttr = "Account Name" }
        @{ LdapAttr = "cn"; MvAttr = "Common Name" }
        @{ LdapAttr = "cn"; MvAttr = "Display Name" }
        @{ LdapAttr = "description"; MvAttr = "Description" }
        @{ LdapAttr = "member"; MvAttr = "Static Members" }
    )
    $groupExportMappings = @(
        @{ MvAttr = "Account Name"; LdapAttr = "cn" }
        @{ MvAttr = "Description"; LdapAttr = "description" }
        @{ MvAttr = "Static Members"; LdapAttr = "member" }
    )

    # DN expressions for target export
    $userDnExpression  = '"uid=" + mv["Account Name"] + ",' + $targetConfig.UserContainer + '"'
    $groupDnExpression = '"cn=" + mv["Account Name"] + ",' + $targetConfig.GroupContainer + '"'

    # Matching attribute for users and groups
    $userMatchingAttrName  = "uid"             # OpenLDAP users match on uid
    $groupMatchingAttrName = "cn"              # OpenLDAP groups match on cn
    $userMatchingMvAttr    = "Account Name"
    $groupMatchingMvAttr   = "Account Name"
}
else {
    # Samba AD / Active Directory defaults (original hardcoded values)
    $requiredUserAttributes = @(
        'objectGUID', 'sAMAccountName', 'givenName', 'sn', 'displayName', 'cn',
        'mail', 'userPrincipalName', 'title', 'department', 'company', 'distinguishedName',
        'extensionAttribute1', 'userAccountControl'
    )
    $requiredGroupAttributes = @(
        'objectGUID', 'sAMAccountName', 'cn', 'displayName', 'description',
        'groupType', 'member', 'managedBy', 'mail', 'company', 'distinguishedName'
    )

    # Source container layout: OU=Users,OU=Corp and OU=Entitlements,OU=Corp
    $sourceUserContainerName    = "Users"
    $sourceGroupContainerName   = "Entitlements"
    $sourceUserContainerParent  = "Corp"
    $sourceGroupContainerParent = "Corp"
    # Target container layout: OU=Users,OU=CorpManaged and OU=Entitlements,OU=CorpManaged
    $targetUserContainerName    = "Users"
    $targetGroupContainerName   = "Entitlements"
    $targetUserContainerParent  = "CorpManaged"
    $targetGroupContainerParent = "CorpManaged"

    # User attribute mappings for Samba AD
    $userImportMappings = @(
        @{ LdapAttr = "sAMAccountName"; MvAttr = "Account Name" }
        @{ LdapAttr = "givenName"; MvAttr = "First Name" }
        @{ LdapAttr = "sn"; MvAttr = "Last Name" }
        @{ LdapAttr = "displayName"; MvAttr = "Display Name" }
        @{ LdapAttr = "mail"; MvAttr = "Email" }
        @{ LdapAttr = "title"; MvAttr = "Job Title" }
        @{ LdapAttr = "department"; MvAttr = "Department" }
        @{ LdapAttr = "company"; MvAttr = "Company" }
        @{ LdapAttr = "extensionAttribute1"; MvAttr = "Pronouns" }
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
        @{ MvAttr = "Company"; LdapAttr = "company" }
        @{ MvAttr = "Pronouns"; LdapAttr = "extensionAttribute1" }
    )

    # Group attribute mappings for Samba AD
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
        @{ MvAttr = "Common Name"; LdapAttr = "cn" }
        @{ MvAttr = "Display Name"; LdapAttr = "displayName" }
        @{ MvAttr = "Description"; LdapAttr = "description" }
        @{ MvAttr = "Group Type Flags"; LdapAttr = "groupType" }
        @{ MvAttr = "Email"; LdapAttr = "mail" }
        @{ MvAttr = "Static Members"; LdapAttr = "member" }
        @{ MvAttr = "Managed By"; LdapAttr = "managedBy" }
    )

    # DN expressions for target export
    $userDnExpression  = '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,OU=CorpManaged,DC=gentian,DC=local"'
    $groupDnExpression = '"CN=" + EscapeDN(mv["Common Name"]) + ",OU=Entitlements,OU=CorpManaged,DC=gentian,DC=local"'

    # Matching attribute for users and groups
    $userMatchingAttrName  = "sAMAccountName"
    $groupMatchingAttrName = "sAMAccountName"
    $userMatchingMvAttr    = "Account Name"
    $groupMatchingMvAttr   = "Account Name"
}

Write-TestSection "Scenario 8 Setup: Cross-domain Entitlement Synchronisation"

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
Write-TestStep "Step 4" "Creating Source LDAP Connected System ($sourceSystemName)"

$existingSystems = Get-JIMConnectedSystem
$sourceSystem = $existingSystems | Where-Object { $_.name -eq $sourceSystemName }

if ($sourceSystem) {
    Write-Host "  Connected System '$sourceSystemName' already exists (ID: $($sourceSystem.id))" -ForegroundColor Yellow
}
else {
    $sourceSystem = New-JIMConnectedSystem `
        -Name $sourceSystemName `
        -Description "$sourceSystemName - Source for cross-domain entitlement sync" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru
    Write-Host "  ✓ Created Source LDAP Connected System (ID: $($sourceSystem.id))" -ForegroundColor Green
}

# Configure LDAP settings for Source
$sourceSettings = @{}
if ($hostSetting) { $sourceSettings[$hostSetting.id] = @{ stringValue = $sourceHost } }
if ($portSetting) { $sourceSettings[$portSetting.id] = @{ intValue = $sourcePort } }
if ($usernameSetting) { $sourceSettings[$usernameSetting.id] = @{ stringValue = $sourceBindDN } }
if ($passwordSetting) { $sourceSettings[$passwordSetting.id] = @{ stringValue = $sourcePassword } }
if ($useSSLSetting) { $sourceSettings[$useSSLSetting.id] = @{ checkboxValue = $sourceUseSSL } }
if ($sourceUseSSL -and $certValidationSetting) { $sourceSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" } }
if ($connectionTimeoutSetting) { $sourceSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
if ($authTypeSetting) { $sourceSettings[$authTypeSetting.id] = @{ stringValue = "Simple" } }

if ($sourceSettings.Count -gt 0) {
    Set-JIMConnectedSystem -Id $sourceSystem.id -SettingValues $sourceSettings | Out-Null
    Write-Host "  ✓ Configured Source LDAP settings" -ForegroundColor Green
}

# ============================================================================
# Step 5: Create Target LDAP Connected System
# ============================================================================
Write-TestStep "Step 5" "Creating Target LDAP Connected System ($targetSystemName)"

$targetSystem = $existingSystems | Where-Object { $_.name -eq $targetSystemName }

if ($targetSystem) {
    Write-Host "  Connected System '$targetSystemName' already exists (ID: $($targetSystem.id))" -ForegroundColor Yellow
}
else {
    $targetSystem = New-JIMConnectedSystem `
        -Name $targetSystemName `
        -Description "$targetSystemName - Target for cross-domain entitlement sync" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru
    Write-Host "  ✓ Created Target LDAP Connected System (ID: $($targetSystem.id))" -ForegroundColor Green
}

# Configure LDAP settings for Target
$targetSettings = @{}
if ($hostSetting) { $targetSettings[$hostSetting.id] = @{ stringValue = $targetHost } }
if ($portSetting) { $targetSettings[$portSetting.id] = @{ intValue = $targetPort } }
if ($usernameSetting) { $targetSettings[$usernameSetting.id] = @{ stringValue = $targetBindDN } }
if ($passwordSetting) { $targetSettings[$passwordSetting.id] = @{ stringValue = $targetPassword } }
if ($useSSLSetting) { $targetSettings[$useSSLSetting.id] = @{ checkboxValue = $targetUseSSL } }
if ($targetUseSSL -and $certValidationSetting) { $targetSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" } }
if ($connectionTimeoutSetting) { $targetSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
if ($authTypeSetting) { $targetSettings[$authTypeSetting.id] = @{ stringValue = "Simple" } }

if ($targetSettings.Count -gt 0) {
    Set-JIMConnectedSystem -Id $targetSystem.id -SettingValues $targetSettings | Out-Null
    Write-Host "  ✓ Configured Target LDAP settings" -ForegroundColor Green
}

# Configure Export Concurrency if non-default
if ($ExportConcurrency -gt 1) {
    $exportConcurrencySetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Export Concurrency" }
    if ($exportConcurrencySetting) {
        $exportSettings = @{
            $exportConcurrencySetting.id = @{ intValue = $ExportConcurrency }
        }
        Set-JIMConnectedSystem -Id $targetSystem.id -SettingValues $exportSettings | Out-Null
        Write-Host "  ✓ Configured Target Export Concurrency: $ExportConcurrency" -ForegroundColor Green
    }
    else {
        Write-Host "  ⚠ Export Concurrency setting not found in connector definition" -ForegroundColor Yellow
    }
}

# Configure Max Export Parallelism if non-default
if ($MaxExportParallelism -gt 1) {
    Set-JIMConnectedSystem -Id $targetSystem.id -MaxExportParallelism $MaxExportParallelism | Out-Null
    Write-Host "  ✓ Configured Target Max Export Parallelism: $MaxExportParallelism" -ForegroundColor Green
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
$sourcePartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
if ($sourcePartitionsCheck -and $sourcePartitionsCheck.Count -gt 0) {
    Write-Host "  Source hierarchy already imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Source LDAP hierarchy..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
    $sourcePartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
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
$targetPartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
if ($targetPartitionsCheck -and $targetPartitionsCheck.Count -gt 0) {
    Write-Host "  Target hierarchy already imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    Write-Host "  Importing Target LDAP hierarchy..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
    $targetPartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
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
        # Match by exact name (AD: "Users", "Corp") or by OU-prefixed DN (OpenLDAP: "ou=People,dc=...")
        if ($container.name -eq $Name -or $container.name -match "^ou=$Name,") {
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

# Helper function to find and select containers for a connected system
function Select-ContainersForSystem {
    param(
        [string]$SystemId,
        [string]$SystemName,
        [string]$BaseDN,
        [string]$UserContainerName,
        [string]$GroupContainerName,
        [string]$UserContainerParent,    # null for flat structure (OpenLDAP)
        [string]$GroupContainerParent    # null for flat structure (OpenLDAP)
    )

    Write-Host "  Configuring $SystemName LDAP partitions..." -ForegroundColor Gray
    $partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $SystemId)
    Write-Host "    Found $($partitions.Count) partition(s):" -ForegroundColor Gray
    foreach ($p in $partitions) {
        Write-Host "      - Name: '$($p.name)', ExternalId: '$($p.externalId)'" -ForegroundColor Gray
    }

    # Find the partition matching the base DN (case-insensitive)
    $domainPartition = $partitions | Where-Object { $_.name -eq $BaseDN }
    if (-not $domainPartition -and $partitions.Count -eq 1) {
        $domainPartition = $partitions[0]
        Write-Host "    Using single available partition: $($domainPartition.name)" -ForegroundColor Yellow
    }

    if (-not $domainPartition) {
        throw "$SystemName partition not found. Available: $($partitions | ForEach-Object { $_.name } | Join-String -Separator ', ')"
    }

    Set-JIMConnectedSystemPartition -ConnectedSystemId $SystemId -PartitionId $domainPartition.id -Selected $true | Out-Null
    Write-Host "    Selected partition: $($domainPartition.name)" -ForegroundColor Green

    # Find and select containers
    if ($UserContainerParent) {
        # Nested structure (AD): containers are under a parent OU (e.g. OU=Users,OU=Corp)
        $parentContainer = Find-ContainerByName -Containers $domainPartition.containers -Name $UserContainerParent
        if ($parentContainer) {
            $usersContainer = Find-ContainerByName -Containers $parentContainer.childContainers -Name $UserContainerName
            if ($usersContainer) {
                Set-JIMConnectedSystemContainer -ConnectedSystemId $SystemId -ContainerId $usersContainer.id -Selected $true | Out-Null
                Write-Host "    Selected container: $UserContainerName (under $UserContainerParent)" -ForegroundColor Green
            }
        }
        $groupParent = if ($GroupContainerParent -and $GroupContainerParent -ne $UserContainerParent) {
            Find-ContainerByName -Containers $domainPartition.containers -Name $GroupContainerParent
        } else { $parentContainer }
        if ($groupParent) {
            $groupContainer = Find-ContainerByName -Containers $groupParent.childContainers -Name $GroupContainerName
            if ($groupContainer) {
                Set-JIMConnectedSystemContainer -ConnectedSystemId $SystemId -ContainerId $groupContainer.id -Selected $true | Out-Null
                Write-Host "    Selected container: $GroupContainerName (under $($GroupContainerParent ?? $UserContainerParent))" -ForegroundColor Green
            }
        }
    }
    else {
        # Flat structure (OpenLDAP): containers are directly under the partition
        $usersContainer = Find-ContainerByName -Containers $domainPartition.containers -Name $UserContainerName
        if ($usersContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $SystemId -ContainerId $usersContainer.id -Selected $true | Out-Null
            Write-Host "    Selected container: $UserContainerName" -ForegroundColor Green
        }
        $groupContainer = Find-ContainerByName -Containers $domainPartition.containers -Name $GroupContainerName
        if ($groupContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $SystemId -ContainerId $groupContainer.id -Selected $true | Out-Null
            Write-Host "    Selected container: $GroupContainerName" -ForegroundColor Green
        }
    }

    # Deselect other partitions
    foreach ($partition in $partitions) {
        if ($partition.id -ne $domainPartition.id) {
            Set-JIMConnectedSystemPartition -ConnectedSystemId $SystemId -PartitionId $partition.id -Selected $false | Out-Null
        }
    }
}

# Configure Source partitions and containers
Select-ContainersForSystem `
    -SystemId $sourceSystem.id `
    -SystemName "Source" `
    -BaseDN $sourceBaseDN `
    -UserContainerName $sourceUserContainerName `
    -GroupContainerName $sourceGroupContainerName `
    -UserContainerParent $sourceUserContainerParent `
    -GroupContainerParent $sourceGroupContainerParent

# Configure Target partitions and containers
Select-ContainersForSystem `
    -SystemId $targetSystem.id `
    -SystemName "Target" `
    -BaseDN $targetBaseDN `
    -UserContainerName $targetUserContainerName `
    -GroupContainerName $targetGroupContainerName `
    -UserContainerParent $targetUserContainerParent `
    -GroupContainerParent $targetGroupContainerParent

Write-Host "  Partitions and containers configured" -ForegroundColor Green

# ============================================================================
# Step 8: Configure Object Types and Attributes
# ============================================================================
Write-TestStep "Step 8" "Configuring Object Types and Attributes"

# Get object types
$sourceUserType = $sourceObjectTypes | Where-Object { $_.name -eq $userTypeName } | Select-Object -First 1
$sourceGroupType = $sourceObjectTypes | Where-Object { $_.name -eq $groupTypeName } | Select-Object -First 1
$targetUserType = $targetObjectTypes | Where-Object { $_.name -eq $userTypeName } | Select-Object -First 1
$targetGroupType = $targetObjectTypes | Where-Object { $_.name -eq $groupTypeName } | Select-Object -First 1

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvGroupType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" } | Select-Object -First 1

if (-not $sourceUserType) { throw "No '$userTypeName' object type found in Source schema" }
if (-not $sourceGroupType) { throw "No '$groupTypeName' object type found in Source schema" }
if (-not $targetUserType) { throw "No '$userTypeName' object type found in Target schema" }
if (-not $targetGroupType) { throw "No '$groupTypeName' object type found in Target schema" }
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

# Note: External ID (objectGUID for AD, entryUUID for OpenLDAP) is automatically set by the LDAP
# connector schema import (the connector marks it as IsExternalId = true). No manual override needed.
$externalIdAttr = if ($isOpenLDAP) { "entryUUID" } else { "objectGUID" }
Write-Host "  Set $externalIdAttr as External ID for all object types" -ForegroundColor Green

# Attribute lists are defined at the top of the script based on directory type

# Validate all required user attributes exist in the LDAP schema
$sourceSchemaAttrNames = @($sourceUserType.attributes | ForEach-Object { $_.name })
$missingUserAttrs = @($requiredUserAttributes | Where-Object { $_ -notin $sourceSchemaAttrNames })
if ($missingUserAttrs.Count -gt 0) {
    Write-Host "  Required LDAP user attributes not found in schema: $($missingUserAttrs -join ', ')" -ForegroundColor Red
    if (-not $isOpenLDAP) {
        Write-Host "    This usually means the Samba AD image is outdated and needs rebuilding." -ForegroundColor Yellow
        Write-Host "    Run: docker rmi samba-ad-prebuilt:latest && jim-build" -ForegroundColor Yellow
    }
    throw "Missing required LDAP attributes in schema: $($missingUserAttrs -join ', ')"
}

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
$sourceLabel = if ($isOpenLDAP) { "APAC LDAP" } else { "APAC AD" }
$targetLabel = if ($isOpenLDAP) { "EMEA LDAP" } else { "EMEA AD" }
$sourceUserImportRuleName = "$sourceLabel Import Users"
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
$targetUserExportRuleName = "$targetLabel Export Users"
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
$targetUserImportRuleName = "$targetLabel Import Users"
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
$sourceGroupImportRuleName = "$sourceLabel Import Groups"
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
$targetGroupExportRuleName = "$targetLabel Export Groups"
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

# ============================================================================
# Step 10: Configure Attribute Flow Mappings
# ============================================================================
Write-TestStep "Step 10" "Configuring Attribute Flow Mappings"

$mvAttributes = Get-JIMMetaverseAttribute

# --- User Mappings ---
# (mapping arrays defined at script top based on directory type)
Write-Host "  Configuring user attribute mappings..." -ForegroundColor Gray

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

# Add expression mapping for userAccountControl → Status on source user import rule (AD only)
# OpenLDAP has no userAccountControl attribute
if (-not $isOpenLDAP) {
    $statusAttr = $mvAttributes | Where-Object { $_.name -eq "Status" }
    $uacAttr = $sourceUserType.attributes | Where-Object { $_.name -eq "userAccountControl" }
    if ($statusAttr -and $uacAttr) {
        $existingStatusMapping = $existingSourceUserImportMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $statusAttr.id
        }
        if (-not $existingStatusMapping) {
            try {
                New-JIMSyncRuleMapping -SyncRuleId $sourceUserImportRule.id `
                    -TargetMetaverseAttributeId $statusAttr.id `
                    -Expression 'IIF(HasBit(cs["userAccountControl"], 2), "Archived", "Active")' | Out-Null
                Write-Host "    Source user import userAccountControl->Status expression mapping configured" -ForegroundColor Green
            }
            catch {
                Write-Host "    Could not create Status expression mapping: $_" -ForegroundColor Yellow
            }
        }
    }
}

# Add constant expression mapping for Type = PersonEntity on source user import rule
$typeAttr = $mvAttributes | Where-Object { $_.name -eq "Type" }
if ($typeAttr) {
    $existingTypeMapping = $existingSourceUserImportMappings | Where-Object {
        $_.targetMetaverseAttributeId -eq $typeAttr.id
    }
    if (-not $existingTypeMapping) {
        try {
            New-JIMSyncRuleMapping -SyncRuleId $sourceUserImportRule.id `
                -TargetMetaverseAttributeId $typeAttr.id `
                -Expression '"PersonEntity"' | Out-Null
            Write-Host "    ✓ Source user import Type=PersonEntity expression mapping configured" -ForegroundColor Green
        }
        catch {
            Write-Host "    ⚠ Could not create Type expression mapping: $_" -ForegroundColor Yellow
        }
    }
}

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
            -Expression $userDnExpression | Out-Null
        $userExportMappingsCreated++
    }
}
Write-Host "    ✓ Target user export mappings ($userExportMappingsCreated new)" -ForegroundColor Green

# --- Group Mappings ---
# (mapping arrays defined at script top based on directory type)
Write-Host "  Configuring group attribute mappings..." -ForegroundColor Gray

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
            catch {
                Write-Host "    ⚠ Failed to create group import mapping ($($mapping.LdapAttr) → $($mapping.MvAttr)): $_" -ForegroundColor Yellow
            }
        }
    }
}
Write-Host "    ✓ Source group import mappings ($groupImportMappingsCreated new)" -ForegroundColor Green

# Create expression-based import mappings for Group Type and Group Scope (AD only — derived from groupType flags)
# OpenLDAP groupOfNames has no groupType attribute
if (-not $isOpenLDAP) {
    Write-Host "  Configuring group type/scope expression mappings..." -ForegroundColor Gray
    $groupTypeAttr = $mvAttributes | Where-Object { $_.name -eq "Group Type" }
    $groupScopeAttr = $mvAttributes | Where-Object { $_.name -eq "Group Scope" }
    $groupTypeFlagsAttr = $sourceGroupType.attributes | Where-Object { $_.name -eq "groupType" }

    if ($groupTypeAttr -and $groupTypeFlagsAttr) {
        $groupTypeMapping = $existingSourceGroupImportMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $groupTypeAttr.id
        }
        if (-not $groupTypeMapping) {
            try {
                $expression = 'HasBit(cs["groupType"], -2147483648) ? "Security" : "Distribution"'
                New-JIMSyncRuleMapping -SyncRuleId $sourceGroupImportRule.id `
                    -TargetMetaverseAttributeId $groupTypeAttr.id `
                    -Expression $expression | Out-Null
                Write-Host "    Created Group Type mapping with expression" -ForegroundColor Green
            }
            catch {
                Write-Host "    Failed to create Group Type expression mapping: $_" -ForegroundColor Yellow
            }
        }
    }

    if ($groupScopeAttr -and $groupTypeFlagsAttr) {
        $groupScopeMapping = $existingSourceGroupImportMappings | Where-Object {
            $_.targetMetaverseAttributeId -eq $groupScopeAttr.id
        }
        if (-not $groupScopeMapping) {
            try {
                $expression = 'HasBit(cs["groupType"], 1) ? "Domain Local" : (HasBit(cs["groupType"], 2) ? "Global" : "Universal")'
                New-JIMSyncRuleMapping -SyncRuleId $sourceGroupImportRule.id `
                    -TargetMetaverseAttributeId $groupScopeAttr.id `
                    -Expression $expression | Out-Null
                Write-Host "    Created Group Scope mapping with expression" -ForegroundColor Green
            }
            catch {
                Write-Host "    Failed to create Group Scope expression mapping: $_" -ForegroundColor Yellow
            }
        }
    }
}

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
            catch {
                Write-Host "    ⚠ Failed to create group export mapping ($($mapping.MvAttr) → $($mapping.LdapAttr)): $_" -ForegroundColor Yellow
            }
        }
    }
}

# Group DN expression for target - uses Common Name (cn) not Display Name
# This ensures the target DN matches the source cn even if displayName differs
$targetGroupDnAttr = $targetGroupType.attributes | Where-Object { $_.name -eq 'distinguishedName' }
if ($targetGroupDnAttr) {
    $dnMappingExists = $existingTargetGroupExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $targetGroupDnAttr.id }
    if (-not $dnMappingExists) {
        New-JIMSyncRuleMapping -SyncRuleId $targetGroupExportRule.id `
            -TargetConnectedSystemAttributeId $targetGroupDnAttr.id `
            -Expression $groupDnExpression | Out-Null
        $groupExportMappingsCreated++
    }
}
Write-Host "    ✓ Target group export mappings ($groupExportMappingsCreated new)" -ForegroundColor Green

# ============================================================================
# Step 11: Configure Matching Rules
# ============================================================================
Write-TestStep "Step 11" "Configuring Matching Rules"

$mvUserMatchingAttr = $mvAttributes | Where-Object { $_.name -eq $userMatchingMvAttr }
$mvGroupMatchingAttr = $mvAttributes | Where-Object { $_.name -eq $groupMatchingMvAttr }

# Source user matching rule
$sourceUserMatchAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $userMatchingAttrName }
if ($sourceUserMatchAttr -and $mvUserMatchingAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvUserMatchingAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $sourceSystem.id `
            -ObjectTypeId $sourceUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -TargetMetaverseAttributeId $mvUserMatchingAttr.id `
            -SourceAttributeId $sourceUserMatchAttr.id | Out-Null
        Write-Host "  Source user matching rule ($userMatchingAttrName -> $userMatchingMvAttr)" -ForegroundColor Green
    }
    else {
        Write-Host "  Source user matching rule already exists" -ForegroundColor Gray
    }
}

# Target user matching rule
$targetUserMatchAttr = $targetUserType.attributes | Where-Object { $_.name -eq $userMatchingAttrName }
if ($targetUserMatchAttr -and $mvUserMatchingAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvUserMatchingAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $targetSystem.id `
            -ObjectTypeId $targetUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -TargetMetaverseAttributeId $mvUserMatchingAttr.id `
            -SourceAttributeId $targetUserMatchAttr.id | Out-Null
        Write-Host "  Target user matching rule ($userMatchingAttrName -> $userMatchingMvAttr)" -ForegroundColor Green
    }
    else {
        Write-Host "  Target user matching rule already exists" -ForegroundColor Gray
    }
}

# Source group matching rule
$sourceGroupMatchAttr = $sourceGroupType.attributes | Where-Object { $_.name -eq $groupMatchingAttrName }
if ($sourceGroupMatchAttr -and $mvGroupMatchingAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceGroupType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvGroupMatchingAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $sourceSystem.id `
            -ObjectTypeId $sourceGroupType.id `
            -MetaverseObjectTypeId $mvGroupType.id `
            -TargetMetaverseAttributeId $mvGroupMatchingAttr.id `
            -SourceAttributeId $sourceGroupMatchAttr.id | Out-Null
        Write-Host "  Source group matching rule ($groupMatchingAttrName -> $groupMatchingMvAttr)" -ForegroundColor Green
    }
    else {
        Write-Host "  Source group matching rule already exists" -ForegroundColor Gray
    }
}

# Target group matching rule
$targetGroupMatchAttr = $targetGroupType.attributes | Where-Object { $_.name -eq $groupMatchingAttrName }
if ($targetGroupMatchAttr -and $mvGroupMatchingAttr) {
    $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetGroupType.id
    $matchingRuleExists = $existingMatchingRules | Where-Object {
        $_.targetMetaverseAttributeId -eq $mvGroupMatchingAttr.id
    }
    if (-not $matchingRuleExists) {
        New-JIMMatchingRule -ConnectedSystemId $targetSystem.id `
            -ObjectTypeId $targetGroupType.id `
            -MetaverseObjectTypeId $mvGroupType.id `
            -TargetMetaverseAttributeId $mvGroupMatchingAttr.id `
            -SourceAttributeId $targetGroupMatchAttr.id | Out-Null
        Write-Host "  Target group matching rule ($groupMatchingAttrName -> $groupMatchingMvAttr)" -ForegroundColor Green
    }
    else {
        Write-Host "  Target group matching rule already exists" -ForegroundColor Gray
    }
}

# ============================================================================
# Step 12: Create Run Profiles
# ============================================================================
Write-TestStep "Step 12" "Creating Run Profiles"

# Look up selected domain partitions for partition-scoped run profiles
$sourcePartitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
$sourceDomainPartition = $sourcePartitions | Where-Object { $_.selected -eq $true } | Select-Object -First 1
$targetPartitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
$targetDomainPartition = $targetPartitions | Where-Object { $_.selected -eq $true } | Select-Object -First 1

$sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
$targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id

# Source run profiles (Full + Delta)
foreach ($profileName in @("Full Import", "Delta Import", "Full Sync", "Delta Sync", "Export")) {
    $runType = switch ($profileName) {
        "Full Import" { "FullImport" }
        "Delta Import" { "DeltaImport" }
        "Full Sync" { "FullSynchronisation" }
        "Delta Sync" { "DeltaSynchronisation" }
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

# Source - Full Import (Scoped) — targets domain partition only
if ($sourceDomainPartition) {
    $profile = $sourceProfiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    if (-not $profile) {
        New-JIMRunProfile -Name "Full Import (Scoped)" -ConnectedSystemId $sourceSystem.id -RunType "FullImport" -PartitionId $sourceDomainPartition.id -PassThru | Out-Null
        Write-Host "  ✓ Created 'Full Import (Scoped)' for Source (APAC) (PartitionId: $($sourceDomainPartition.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import (Scoped)' already exists for Source (APAC)" -ForegroundColor Gray
    }
}

# Target run profiles (Full + Delta)
foreach ($profileName in @("Full Import", "Delta Import", "Full Sync", "Delta Sync", "Export")) {
    $runType = switch ($profileName) {
        "Full Import" { "FullImport" }
        "Delta Import" { "DeltaImport" }
        "Full Sync" { "FullSynchronisation" }
        "Delta Sync" { "DeltaSynchronisation" }
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

# Target - Full Import (Scoped) — targets domain partition only
if ($targetDomainPartition) {
    $profile = $targetProfiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    if (-not $profile) {
        New-JIMRunProfile -Name "Full Import (Scoped)" -ConnectedSystemId $targetSystem.id -RunType "FullImport" -PartitionId $targetDomainPartition.id -PassThru | Out-Null
        Write-Host "  ✓ Created 'Full Import (Scoped)' for Target (EMEA) (PartitionId: $($targetDomainPartition.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import (Scoped)' already exists for Target (EMEA)" -ForegroundColor Gray
    }
}

# ============================================================================
# Step 13: Configure Deletion Rules
# ============================================================================
Write-TestStep "Step 13" "Configuring Deletion Rules"

# Configure Group deletion rule - delete from Target when deleted from Source
# This enables the DeleteGroup test to work properly
Write-Host "  Configuring Group deletion rule..." -ForegroundColor Gray

# Get current Group object type settings
$mvGroupTypeCurrent = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" }

if ($mvGroupTypeCurrent) {
    # Configure deletion rule:
    # - DeletionRule: WhenAuthoritativeSourceDisconnected - delete MVO when authoritative source disconnects
    # - DeletionGracePeriod: Zero - immediate deletion (no grace period)
    # - DeletionTriggerConnectedSystemIds: Source system only - Source (APAC) is the authoritative source
    #   This means when a group is deleted from Source AD (APAC), the MVO is marked for deletion
    #   even though Target AD (EMEA) CSO still exists, triggering deprovisioning from Target
    Set-JIMMetaverseObjectType -Id $mvGroupTypeCurrent.id `
        -DeletionRule WhenAuthoritativeSourceDisconnected `
        -DeletionGracePeriod ([TimeSpan]::Zero) `
        -DeletionTriggerConnectedSystemIds @($sourceSystem.id) | Out-Null

    Write-Host "  ✓ Group deletion rule configured (WhenAuthoritativeSourceDisconnected, Source=APAC)" -ForegroundColor Green
}
else {
    Write-Host "  ⚠ Could not find Group metaverse object type" -ForegroundColor Yellow
}

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Setup Complete"
Write-Host "Template:          $Template" -ForegroundColor Cyan
Write-Host "Directory Type:    $(if ($isOpenLDAP) { 'OpenLDAP' } else { 'Samba AD' })" -ForegroundColor Cyan
Write-Host "Source System:     $sourceSystemName (ID: $($sourceSystem.id))" -ForegroundColor Cyan
Write-Host "Target System:     $targetSystemName (ID: $($targetSystem.id))" -ForegroundColor Cyan
Write-Host ""
Write-Host "Scenario 8 setup complete" -ForegroundColor Green
Write-Host ""
Write-Host "Sync Rules Created:" -ForegroundColor Yellow
Write-Host "  Users:  $sourceSystemName -> Metaverse -> $targetSystemName" -ForegroundColor Gray
Write-Host "  Groups: $sourceSystemName -> Metaverse -> $targetSystemName" -ForegroundColor Gray
Write-Host ""
Write-Host "Deletion Rules Configured:" -ForegroundColor Yellow
Write-Host "  Groups: WhenAuthoritativeSourceDisconnected (Source=$sourceSystemName, immediate)" -ForegroundColor Gray
Write-Host ""
Write-Host "Run Profiles Created:" -ForegroundColor Yellow
Write-Host "  $sourceSystemName`: Full Import, Delta Import, Full Sync, Delta Sync, Export" -ForegroundColor Gray
Write-Host "  $targetSystemName`: Full Import, Delta Import, Full Sync, Delta Sync, Export" -ForegroundColor Gray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run Source Full Import to import users and groups" -ForegroundColor Gray
Write-Host "  2. Run Source Full Sync to project to metaverse" -ForegroundColor Gray
Write-Host "  3. Review groups in metaverse (this is the pause point)" -ForegroundColor Gray
