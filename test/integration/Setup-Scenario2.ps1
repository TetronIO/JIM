<#
.SYNOPSIS
    Configure JIM for Scenario 2: Directory to Directory Synchronisation

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for bidirectional LDAP synchronisation.
    This script creates:
    - LDAP Connected System (Samba AD Source)
    - LDAP Connected System (Samba AD Target)
    - Sync Rules for bidirectional attribute flow
    - Run Profiles for synchronisation

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication (if not provided, will attempt to create one)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Setup-Scenario2.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario2.ps1 -Template Small

.NOTES
    This script requires:
    - JIM PowerShell module
    - JIM running and accessible
    - Samba AD Source and Target containers running (docker compose --profile scenario2)
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 2 Setup: Directory to Directory Synchronisation"

# Step 1: Import JIM PowerShell module
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../JIM.PowerShell/JIM/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

# Remove any existing module to ensure fresh import with latest cmdlets
Remove-Module JIM -Force -ErrorAction SilentlyContinue

Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  ✓ JIM PowerShell module imported" -ForegroundColor Green

# Verify Get-JIMConnectorDefinition is available
if (-not (Get-Command Get-JIMConnectorDefinition -ErrorAction SilentlyContinue)) {
    throw "Get-JIMConnectorDefinition cmdlet not found. Module may not have loaded correctly."
}

# Step 2: Connect to JIM
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) {
    Write-Host "  No API key provided - this scenario requires an existing API key" -ForegroundColor Yellow
    Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
    throw "API key required for authentication"
}

try {
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    Write-Host "  ✓ Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to connect to JIM: $_" -ForegroundColor Red
    Write-Host "  Ensure JIM is running and accessible at $JIMUrl" -ForegroundColor Yellow
    throw
}

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Getting connector definitions"

try {
    $connectorDefs = Get-JIMConnectorDefinition
    $ldapConnector = $connectorDefs | Where-Object { $_.name -eq "JIM LDAP Connector" }

    if (-not $ldapConnector) {
        throw "JIM LDAP Connector definition not found"
    }

    Write-Host "  ✓ Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to get connector definitions: $_" -ForegroundColor Red
    throw
}

# Step 4: Create Source LDAP Connected System (Samba AD Source)
Write-TestStep "Step 4" "Creating Source LDAP Connected System"

$existingSystems = Get-JIMConnectedSystem

try {
    $sourceSystem = $existingSystems | Where-Object { $_.name -eq "Samba AD Source" }

    if ($sourceSystem) {
        Write-Host "  Connected System 'Samba AD Source' already exists (ID: $($sourceSystem.id))" -ForegroundColor Yellow
    }
    else {
        $sourceSystem = New-JIMConnectedSystem `
            -Name "Samba AD Source" `
            -Description "Samba Active Directory Source for directory-to-directory sync" `
            -ConnectorDefinitionId $ldapConnector.id `
            -PassThru

        Write-Host "  ✓ Created Source LDAP Connected System (ID: $($sourceSystem.id))" -ForegroundColor Green
    }

    # Configure LDAP settings for Source
    $ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

    $hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
    $portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
    $usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
    $passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
    $useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
    $certValidationSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Certificate Validation" }
    $connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
    $authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }

    $sourceSettings = @{}
    if ($hostSetting) {
        $sourceSettings[$hostSetting.id] = @{ stringValue = "samba-ad-source" }
    }
    if ($portSetting) {
        $sourceSettings[$portSetting.id] = @{ intValue = 636 }
    }
    if ($usernameSetting) {
        $sourceSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=sourcedomain,DC=local" }
    }
    if ($passwordSetting) {
        $sourceSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" }
    }
    if ($useSSLSetting) {
        $sourceSettings[$useSSLSetting.id] = @{ checkboxValue = $true }
    }
    if ($certValidationSetting) {
        $sourceSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" }
    }
    if ($connectionTimeoutSetting) {
        $sourceSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 }
    }
    if ($authTypeSetting) {
        $sourceSettings[$authTypeSetting.id] = @{ stringValue = "Simple" }
    }

    if ($sourceSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $sourceSystem.id -SettingValues $sourceSettings | Out-Null
        Write-Host "  ✓ Configured Source LDAP settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure Source LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5: Create Target LDAP Connected System (Samba AD Target)
Write-TestStep "Step 5" "Creating Target LDAP Connected System"

try {
    $targetSystem = $existingSystems | Where-Object { $_.name -eq "Samba AD Target" }

    if ($targetSystem) {
        Write-Host "  Connected System 'Samba AD Target' already exists (ID: $($targetSystem.id))" -ForegroundColor Yellow
    }
    else {
        $targetSystem = New-JIMConnectedSystem `
            -Name "Samba AD Target" `
            -Description "Samba Active Directory Target for directory-to-directory sync" `
            -ConnectorDefinitionId $ldapConnector.id `
            -PassThru

        Write-Host "  ✓ Created Target LDAP Connected System (ID: $($targetSystem.id))" -ForegroundColor Green
    }

    # Configure LDAP settings for Target
    $targetSettings = @{}
    if ($hostSetting) {
        $targetSettings[$hostSetting.id] = @{ stringValue = "samba-ad-target" }
    }
    if ($portSetting) {
        $targetSettings[$portSetting.id] = @{ intValue = 636 }
    }
    if ($usernameSetting) {
        $targetSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=targetdomain,DC=local" }
    }
    if ($passwordSetting) {
        $targetSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" }
    }
    if ($useSSLSetting) {
        $targetSettings[$useSSLSetting.id] = @{ checkboxValue = $true }
    }
    if ($certValidationSetting) {
        $targetSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" }
    }
    if ($connectionTimeoutSetting) {
        $targetSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 }
    }
    if ($authTypeSetting) {
        $targetSettings[$authTypeSetting.id] = @{ stringValue = "Simple" }
    }

    if ($targetSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $targetSystem.id -SettingValues $targetSettings | Out-Null
        Write-Host "  ✓ Configured Target LDAP settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure Target LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 6: Import Schemas and Hierarchy
Write-TestStep "Step 6" "Importing Connected System Schemas and Hierarchy"

# Source schema
$sourceObjectTypes = Get-JIMConnectedSystem -Id $sourceSystem.id -ObjectTypes
if ($sourceObjectTypes -and $sourceObjectTypes.Count -gt 0) {
    Write-Host "  Source schema already imported ($($sourceObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Source LDAP schema..." -ForegroundColor Gray
        $sourceSystemUpdated = Import-JIMConnectedSystemSchema -Id $sourceSystem.id -PassThru
        $sourceObjectTypes = Get-JIMConnectedSystem -Id $sourceSystem.id -ObjectTypes
        Write-Host "  ✓ Source schema imported ($($sourceObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Failed to import Source schema: $_" -ForegroundColor Red
        throw
    }
}

# Source hierarchy (partitions/containers)
$sourcePartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id
if ($sourcePartitionsCheck -and $sourcePartitionsCheck.Count -gt 0) {
    Write-Host "  Source hierarchy already imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Source LDAP hierarchy..." -ForegroundColor Gray
        Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
        $sourcePartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id
        Write-Host "  ✓ Source hierarchy imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Failed to import Source hierarchy: $_" -ForegroundColor Red
        throw
    }
}

# Target schema
$targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes
if ($targetObjectTypes -and $targetObjectTypes.Count -gt 0) {
    Write-Host "  Target schema already imported ($($targetObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Target LDAP schema..." -ForegroundColor Gray
        $targetSystemUpdated = Import-JIMConnectedSystemSchema -Id $targetSystem.id -PassThru
        $targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes
        Write-Host "  ✓ Target schema imported ($($targetObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Failed to import Target schema: $_" -ForegroundColor Red
        throw
    }
}

# Target hierarchy (partitions/containers)
$targetPartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id
if ($targetPartitionsCheck -and $targetPartitionsCheck.Count -gt 0) {
    Write-Host "  Target hierarchy already imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Target LDAP hierarchy..." -ForegroundColor Gray
        Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
        $targetPartitionsCheck = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id
        Write-Host "  ✓ Target hierarchy imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Failed to import Target hierarchy: $_" -ForegroundColor Red
        throw
    }
}

# Step 7: Create Test OUs and Select Partitions/Containers
Write-TestStep "Step 7" "Creating Test OUs and Selecting Partitions/Containers"

try {
    # Create TestUsers OU in both AD instances (required for proper scoping)
    # This filters out built-in accounts like Administrator, Guest, krbtgt
    Write-Host "  Creating TestUsers OU in Source AD..." -ForegroundColor Gray
    $result = docker exec samba-ad-source samba-tool ou create "OU=TestUsers,DC=sourcedomain,DC=local" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    ✓ Created OU=TestUsers in Source AD" -ForegroundColor Green
    }
    elseif ($result -match "already exists") {
        Write-Host "    OU=TestUsers already exists in Source AD" -ForegroundColor Gray
    }
    else {
        Write-Host "    ⚠ Failed to create OU=TestUsers in Source AD: $result" -ForegroundColor Yellow
    }

    Write-Host "  Creating TestUsers OU in Target AD..." -ForegroundColor Gray
    $result = docker exec samba-ad-target samba-tool ou create "OU=TestUsers,DC=targetdomain,DC=local" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    ✓ Created OU=TestUsers in Target AD" -ForegroundColor Green
    }
    elseif ($result -match "already exists") {
        Write-Host "    OU=TestUsers already exists in Target AD" -ForegroundColor Gray
    }
    else {
        Write-Host "    ⚠ Failed to create OU=TestUsers in Target AD: $result" -ForegroundColor Yellow
    }

    # Re-import hierarchy to pick up the new OUs
    Write-Host "  Re-importing hierarchy to discover new OUs..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
    Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
    Write-Host "    ✓ Hierarchy re-imported" -ForegroundColor Green

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

    # Configure Source system - only select domain partition and TestUsers container
    Write-Host "  Configuring Source LDAP partitions..." -ForegroundColor Gray
    $sourcePartitions = Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id

    if ($sourcePartitions -and $sourcePartitions.Count -gt 0) {
        # Find the main domain partition (DC=sourcedomain,DC=local)
        $sourceDomainPartition = $sourcePartitions | Where-Object {
            $_.name -eq "DC=sourcedomain,DC=local"
        }

        if ($sourceDomainPartition) {
            # Select only the domain partition
            Set-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id -PartitionId $sourceDomainPartition.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected partition: $($sourceDomainPartition.name)" -ForegroundColor Green

            # Find and select only the TestUsers container
            $testUsersContainer = Find-ContainerByName -Containers $sourceDomainPartition.containers -Name "TestUsers"
            if ($testUsersContainer) {
                Set-JIMConnectedSystemContainer -ConnectedSystemId $sourceSystem.id -ContainerId $testUsersContainer.id -Selected $true | Out-Null
                Write-Host "    ✓ Selected container: TestUsers (filters out built-in accounts)" -ForegroundColor Green
            }
            else {
                Write-Host "    ⚠ TestUsers container not found - run Populate-SambaAD.ps1 first" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "    ⚠ Domain partition not found for Source system" -ForegroundColor Yellow
        }

        # Deselect other partitions (DNS zones, Configuration, Schema)
        foreach ($partition in $sourcePartitions) {
            if ($partition.name -ne "DC=sourcedomain,DC=local") {
                Set-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id -PartitionId $partition.id -Selected $false | Out-Null
            }
        }
    }
    else {
        Write-Host "    ⚠ No partitions found for Source system" -ForegroundColor Yellow
    }

    # Configure Target system - only select domain partition and TestUsers container
    Write-Host "  Configuring Target LDAP partitions..." -ForegroundColor Gray
    $targetPartitions = Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id

    if ($targetPartitions -and $targetPartitions.Count -gt 0) {
        # Find the main domain partition (DC=targetdomain,DC=local)
        $targetDomainPartition = $targetPartitions | Where-Object {
            $_.name -eq "DC=targetdomain,DC=local"
        }

        if ($targetDomainPartition) {
            # Select only the domain partition
            Set-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id -PartitionId $targetDomainPartition.id -Selected $true | Out-Null
            Write-Host "    ✓ Selected partition: $($targetDomainPartition.name)" -ForegroundColor Green

            # Find and select only the TestUsers container
            $testUsersContainer = Find-ContainerByName -Containers $targetDomainPartition.containers -Name "TestUsers"
            if ($testUsersContainer) {
                Set-JIMConnectedSystemContainer -ConnectedSystemId $targetSystem.id -ContainerId $testUsersContainer.id -Selected $true | Out-Null
                Write-Host "    ✓ Selected container: TestUsers (filters out built-in accounts)" -ForegroundColor Green
            }
            else {
                Write-Host "    ⚠ TestUsers container not found - will be created during export" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "    ⚠ Domain partition not found for Target system" -ForegroundColor Yellow
        }

        # Deselect other partitions (DNS zones, Configuration, Schema)
        foreach ($partition in $targetPartitions) {
            if ($partition.name -ne "DC=targetdomain,DC=local") {
                Set-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id -PartitionId $partition.id -Selected $false | Out-Null
            }
        }
    }
    else {
        Write-Host "    ⚠ No partitions found for Target system" -ForegroundColor Yellow
    }

    Write-Host "  ✓ Partitions and containers configured (scoped to TestUsers OU)" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to configure partitions: $_" -ForegroundColor Red
    throw
}

# Step 8: Create Sync Rules
Write-TestStep "Step 8" "Creating Sync Rules"

try {
    # Get the "user" object type from both systems
    $sourceUserType = $sourceObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1
    $targetUserType = $targetObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1

    # Get the Metaverse "User" object type
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

    if (-not $sourceUserType) {
        throw "No 'user' object type found in Source LDAP schema"
    }
    if (-not $targetUserType) {
        throw "No 'user' object type found in Target LDAP schema"
    }
    if (-not $mvUserType) {
        throw "No 'User' object type found in Metaverse"
    }

    Write-Host "  Found object types:" -ForegroundColor Gray
    Write-Host "    Source: $($sourceUserType.name) (ID: $($sourceUserType.id))" -ForegroundColor Gray
    Write-Host "    Target: $($targetUserType.name) (ID: $($targetUserType.id))" -ForegroundColor Gray
    Write-Host "    Metaverse: $($mvUserType.name) (ID: $($mvUserType.id))" -ForegroundColor Gray

    # Mark object types as selected
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -Selected $true | Out-Null
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -Selected $true | Out-Null
    Write-Host "  ✓ Selected 'user' object types for Source and Target" -ForegroundColor Green

    # Mark sAMAccountName as External ID (anchor) for both systems
    $sourceAnchorAttr = $sourceUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
    $targetAnchorAttr = $targetUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }

    if ($sourceAnchorAttr) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeId $sourceAnchorAttr.id -IsExternalId $true | Out-Null
        Write-Host "  ✓ Set 'sAMAccountName' as External ID for Source" -ForegroundColor Green
    }
    if ($targetAnchorAttr) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeId $targetAnchorAttr.id -IsExternalId $true | Out-Null
        Write-Host "  ✓ Set 'sAMAccountName' as External ID for Target" -ForegroundColor Green
    }

    # Mark all attributes as selected for both systems
    foreach ($attr in $sourceUserType.attributes) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeId $attr.id -Selected $true | Out-Null
    }
    foreach ($attr in $targetUserType.attributes) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeId $attr.id -Selected $true | Out-Null
    }
    Write-Host "  ✓ Selected all attributes for Source and Target" -ForegroundColor Green

    # Create Import sync rule (Source -> Metaverse)
    $existingRules = Get-JIMSyncRule
    $sourceImportRuleName = "Source AD Import Users"
    $sourceImportRule = $existingRules | Where-Object { $_.name -eq $sourceImportRuleName }

    if (-not $sourceImportRule) {
        $sourceImportRule = New-JIMSyncRule `
            -Name $sourceImportRuleName `
            -ConnectedSystemId $sourceSystem.id `
            -ConnectedSystemObjectTypeId $sourceUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Import `
            -ProjectToMetaverse `
            -PassThru
        Write-Host "  ✓ Created import sync rule: $sourceImportRuleName" -ForegroundColor Green
    }
    else {
        Write-Host "  Import sync rule '$sourceImportRuleName' already exists" -ForegroundColor Gray
    }

    # Create Export sync rule (Metaverse -> Target)
    $targetExportRuleName = "Target AD Export Users"
    $targetExportRule = $existingRules | Where-Object { $_.name -eq $targetExportRuleName }

    if (-not $targetExportRule) {
        $targetExportRule = New-JIMSyncRule `
            -Name $targetExportRuleName `
            -ConnectedSystemId $targetSystem.id `
            -ConnectedSystemObjectTypeId $targetUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Export `
            -ProvisionToConnectedSystem `
            -PassThru
        Write-Host "  ✓ Created export sync rule: $targetExportRuleName" -ForegroundColor Green
    }
    else {
        Write-Host "  Export sync rule '$targetExportRuleName' already exists" -ForegroundColor Gray
    }

    # For bidirectional sync, create reverse rules as well
    # Import from Target -> Metaverse (for reverse sync)
    $targetImportRuleName = "Target AD Import Users"
    $targetImportRule = $existingRules | Where-Object { $_.name -eq $targetImportRuleName }

    if (-not $targetImportRule) {
        $targetImportRule = New-JIMSyncRule `
            -Name $targetImportRuleName `
            -ConnectedSystemId $targetSystem.id `
            -ConnectedSystemObjectTypeId $targetUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Import `
            -PassThru
        Write-Host "  ✓ Created import sync rule: $targetImportRuleName" -ForegroundColor Green
    }
    else {
        Write-Host "  Import sync rule '$targetImportRuleName' already exists" -ForegroundColor Gray
    }

    # Export to Source (for reverse sync)
    $sourceExportRuleName = "Source AD Export Users"
    $sourceExportRule = $existingRules | Where-Object { $_.name -eq $sourceExportRuleName }

    if (-not $sourceExportRule) {
        $sourceExportRule = New-JIMSyncRule `
            -Name $sourceExportRuleName `
            -ConnectedSystemId $sourceSystem.id `
            -ConnectedSystemObjectTypeId $sourceUserType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Export `
            -PassThru
        Write-Host "  ✓ Created export sync rule: $sourceExportRuleName" -ForegroundColor Green
    }
    else {
        Write-Host "  Export sync rule '$sourceExportRuleName' already exists" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create sync rules: $_" -ForegroundColor Red
    throw
}

# Step 9: Configure Attribute Flow Mappings
Write-TestStep "Step 9" "Configuring Attribute Flow Mappings"

try {
    if ($sourceImportRule -and $targetExportRule) {
        Write-Host "  Configuring attribute mappings..." -ForegroundColor Gray

        # Create DN Metaverse attribute if it doesn't exist
        $dnAttr = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq 'DN' }
        if (-not $dnAttr) {
            $dnAttr = New-JIMMetaverseAttribute -Name 'DN' -Type Text -ObjectTypeIds @($mvUserType.id)
            Write-Host "  ✓ Created DN Metaverse attribute" -ForegroundColor Green
        }

        # Define attribute mappings for forward sync (Source -> Metaverse -> Target)
        # Source imports these attributes to Metaverse
        $importMappings = @(
            @{ LdapAttr = "sAMAccountName";     MvAttr = "Account Name" }
            @{ LdapAttr = "givenName";          MvAttr = "First Name" }
            @{ LdapAttr = "sn";                 MvAttr = "Last Name" }
            @{ LdapAttr = "displayName";        MvAttr = "Display Name" }
            @{ LdapAttr = "mail";               MvAttr = "Email" }
            @{ LdapAttr = "title";              MvAttr = "Job Title" }
            @{ LdapAttr = "department";         MvAttr = "Department" }
            @{ LdapAttr = "telephoneNumber";    MvAttr = "Phone" }
        )

        # Target exports these attributes from Metaverse
        # Note: distinguishedName is required for LDAP provisioning - it tells the connector where to create the object
        # The DN is constructed using an expression that places users in the TestUsers OU
        $exportMappings = @(
            @{ MvAttr = "Account Name";   LdapAttr = "sAMAccountName" }
            @{ MvAttr = "First Name";     LdapAttr = "givenName" }
            @{ MvAttr = "Last Name";      LdapAttr = "sn" }
            @{ MvAttr = "Display Name";   LdapAttr = "displayName" }
            @{ MvAttr = "Display Name";   LdapAttr = "cn" }
            @{ MvAttr = "Email";          LdapAttr = "mail" }
            @{ MvAttr = "Email";          LdapAttr = "userPrincipalName" }
            @{ MvAttr = "Job Title";      LdapAttr = "title" }
            @{ MvAttr = "Department";     LdapAttr = "department" }
            @{ MvAttr = "Phone";          LdapAttr = "telephoneNumber" }
        )

        # NOTE: For LDAP provisioning (creating new objects), the connector needs a distinguishedName attribute
        # to know where to create the object. Without function/expression support in JIM, we cannot dynamically
        # construct the target DN from metaverse attributes.
        #
        # Current limitation: New object provisioning to LDAP requires either:
        # 1. Pre-populating users in the target system (for join/update scenarios)
        # 2. Implementing function support for DN construction (e.g., "CN=" + DisplayName + ",OU=TestUsers,DC=...")
        #
        # For Scenario 2, this demonstrates:
        # - Import from Source AD -> Metaverse (works)
        # - Sync to create pending exports (works)
        # - Export to Target AD will fail for NEW objects until DN construction is available
        # - Export to Target AD will work for EXISTING objects (updates)
        #
        # Workaround: Pre-create matching users in Target AD, then run import on both sides to establish joins

        # Get all metaverse attributes for lookup
        $mvAttributes = Get-JIMMetaverseAttribute

        # Create import mappings (Source LDAP -> Metaverse)
        $existingImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id
        $importMappingsCreated = 0

        foreach ($mapping in $importMappings) {
            $ldapAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
            $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

            if ($ldapAttr -and $mvAttr) {
                $existsAlready = $existingImportMappings | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvAttr.id -and
                    ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $ldapAttr.id })
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id `
                            -TargetMetaverseAttributeId $mvAttr.id `
                            -SourceConnectedSystemAttributeId $ldapAttr.id | Out-Null
                        $importMappingsCreated++
                    }
                    catch {
                        Write-Host "    ⚠ Could not create mapping $($mapping.LdapAttr) → $($mapping.MvAttr): $_" -ForegroundColor Yellow
                    }
                }
            }
        }
        Write-Host "  ✓ Source import mappings configured ($importMappingsCreated new)" -ForegroundColor Green

        # Create export mappings (Metaverse -> Target LDAP)
        $existingExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $targetExportRule.id
        $exportMappingsCreated = 0

        foreach ($mapping in $exportMappings) {
            $ldapAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
            $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

            if ($ldapAttr -and $mvAttr) {
                $existsAlready = $existingExportMappings | Where-Object {
                    $_.targetConnectedSystemAttributeId -eq $ldapAttr.id -and
                    ($_.sources | Where-Object { $_.metaverseAttributeId -eq $mvAttr.id })
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $targetExportRule.id `
                            -TargetConnectedSystemAttributeId $ldapAttr.id `
                            -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                        $exportMappingsCreated++
                    }
                    catch {
                        Write-Host "    ⚠ Could not create mapping $($mapping.MvAttr) → $($mapping.LdapAttr): $_" -ForegroundColor Yellow
                    }
                }
            }
        }
        Write-Host "  ✓ Target export mappings configured ($exportMappingsCreated new)" -ForegroundColor Green

        # Configure reverse mappings for bidirectional sync
        # Target Import mappings (for reverse sync)
        if ($targetImportRule) {
            $existingTargetImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $targetImportRule.id
            $reverseImportMappingsCreated = 0

            foreach ($mapping in $importMappings) {
                $ldapAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
                $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

                if ($ldapAttr -and $mvAttr) {
                    $existsAlready = $existingTargetImportMappings | Where-Object {
                        $_.targetMetaverseAttributeId -eq $mvAttr.id -and
                        ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $ldapAttr.id })
                    }

                    if (-not $existsAlready) {
                        try {
                            New-JIMSyncRuleMapping -SyncRuleId $targetImportRule.id `
                                -TargetMetaverseAttributeId $mvAttr.id `
                                -SourceConnectedSystemAttributeId $ldapAttr.id | Out-Null
                            $reverseImportMappingsCreated++
                        }
                        catch {
                            # Silently skip duplicates
                        }
                    }
                }
            }
            Write-Host "  ✓ Target import mappings configured ($reverseImportMappingsCreated new)" -ForegroundColor Green
        }

        # Source Export mappings (for reverse sync)
        if ($sourceExportRule) {
            $existingSourceExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $sourceExportRule.id
            $reverseExportMappingsCreated = 0

            foreach ($mapping in $exportMappings) {
                $ldapAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
                $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

                if ($ldapAttr -and $mvAttr) {
                    $existsAlready = $existingSourceExportMappings | Where-Object {
                        $_.targetConnectedSystemAttributeId -eq $ldapAttr.id -and
                        ($_.sources | Where-Object { $_.metaverseAttributeId -eq $mvAttr.id })
                    }

                    if (-not $existsAlready) {
                        try {
                            New-JIMSyncRuleMapping -SyncRuleId $sourceExportRule.id `
                                -TargetConnectedSystemAttributeId $ldapAttr.id `
                                -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                            $reverseExportMappingsCreated++
                        }
                        catch {
                            # Silently skip duplicates
                        }
                    }
                }
            }
            Write-Host "  ✓ Source export mappings configured ($reverseExportMappingsCreated new)" -ForegroundColor Green
        }

        # Add object matching rule for Source AD (match by sAMAccountName)
        Write-Host "  Configuring object matching rules..." -ForegroundColor Gray

        $sourceSamAttr = $sourceUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }
        $mvAccountNameAttr = $mvAttributes | Where-Object { $_.name -eq 'Account Name' }

        if ($sourceSamAttr -and $mvAccountNameAttr) {
            $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id

            $matchingRuleExists = $existingMatchingRules | Where-Object {
                $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id -and
                ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $sourceSamAttr.id })
            }

            if (-not $matchingRuleExists) {
                try {
                    New-JIMMatchingRule -ConnectedSystemId $sourceSystem.id `
                        -ObjectTypeId $sourceUserType.id `
                        -TargetMetaverseAttributeName 'Account Name' `
                        -SourceConnectedSystemAttributeName 'sAMAccountName' | Out-Null
                    Write-Host "  ✓ Source matching rule configured (sAMAccountName → Account Name)" -ForegroundColor Green
                }
                catch {
                    Write-Host "  ⚠ Could not configure Source matching rule: $_" -ForegroundColor Yellow
                }
            }
            else {
                Write-Host "  Source matching rule already exists" -ForegroundColor Gray
            }
        }

        # Add object matching rule for Target AD
        $targetSamAttr = $targetUserType.attributes | Where-Object { $_.name -eq 'sAMAccountName' }

        if ($targetSamAttr -and $mvAccountNameAttr) {
            $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id

            $matchingRuleExists = $existingMatchingRules | Where-Object {
                $_.targetMetaverseAttributeId -eq $mvAccountNameAttr.id -and
                ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $targetSamAttr.id })
            }

            if (-not $matchingRuleExists) {
                try {
                    New-JIMMatchingRule -ConnectedSystemId $targetSystem.id `
                        -ObjectTypeId $targetUserType.id `
                        -TargetMetaverseAttributeName 'Account Name' `
                        -SourceConnectedSystemAttributeName 'sAMAccountName' | Out-Null
                    Write-Host "  ✓ Target matching rule configured (sAMAccountName → Account Name)" -ForegroundColor Green
                }
                catch {
                    Write-Host "  ⚠ Could not configure Target matching rule: $_" -ForegroundColor Yellow
                }
            }
            else {
                Write-Host "  Target matching rule already exists" -ForegroundColor Gray
            }
        }

        # Restart jim.worker to pick up schema changes
        Write-Host "  Restarting JIM.Worker to reload schema..." -ForegroundColor Gray
        docker restart jim.worker > $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ JIM.Worker restarted" -ForegroundColor Green
            Start-Sleep -Seconds 3
        }
        else {
            Write-Host "  ⚠ Failed to restart JIM.Worker" -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host "  ✗ Failed to configure attribute mappings: $_" -ForegroundColor Red
    throw
}

# Step 10: Create Run Profiles
Write-TestStep "Step 10" "Creating Run Profiles"

try {
    # Get existing run profiles
    $sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
    $targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id

    # Source AD - Full Import
    $sourceImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Source AD - Full Import" }
    if (-not $sourceImportProfile) {
        $sourceImportProfile = New-JIMRunProfile `
            -Name "Source AD - Full Import" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'Source AD - Full Import' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Source AD - Full Import' already exists" -ForegroundColor Gray
    }

    # Source AD - Full Sync
    $sourceSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Source AD - Full Sync" }
    if (-not $sourceSyncProfile) {
        $sourceSyncProfile = New-JIMRunProfile `
            -Name "Source AD - Full Sync" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Source AD - Full Sync' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Source AD - Full Sync' already exists" -ForegroundColor Gray
    }

    # Source AD - Export (for reverse sync)
    $sourceExportProfile = $sourceProfiles | Where-Object { $_.name -eq "Source AD - Export" }
    if (-not $sourceExportProfile) {
        $sourceExportProfile = New-JIMRunProfile `
            -Name "Source AD - Export" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Source AD - Export' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Source AD - Export' already exists" -ForegroundColor Gray
    }

    # Target AD - Full Import
    $targetImportProfile = $targetProfiles | Where-Object { $_.name -eq "Target AD - Full Import" }
    if (-not $targetImportProfile) {
        $targetImportProfile = New-JIMRunProfile `
            -Name "Target AD - Full Import" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'Target AD - Full Import' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Target AD - Full Import' already exists" -ForegroundColor Gray
    }

    # Target AD - Full Sync
    $targetSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Target AD - Full Sync" }
    if (-not $targetSyncProfile) {
        $targetSyncProfile = New-JIMRunProfile `
            -Name "Target AD - Full Sync" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Target AD - Full Sync' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Target AD - Full Sync' already exists" -ForegroundColor Gray
    }

    # Target AD - Export
    $targetExportProfile = $targetProfiles | Where-Object { $_.name -eq "Target AD - Export" }
    if (-not $targetExportProfile) {
        $targetExportProfile = New-JIMRunProfile `
            -Name "Target AD - Export" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Target AD - Export' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Target AD - Export' already exists" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create run profiles: $_" -ForegroundColor Red
    throw
}

# Summary
Write-TestSection "Setup Complete"
Write-Host "Template:          $Template" -ForegroundColor Cyan
Write-Host "Source System ID:  $($sourceSystem.id)" -ForegroundColor Cyan
Write-Host "Target System ID:  $($targetSystem.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Scenario 2 setup complete" -ForegroundColor Green
Write-Host ""
Write-Host "Sync Rules Created:" -ForegroundColor Yellow
Write-Host "  Forward Flow: Source AD -> Metaverse -> Target AD" -ForegroundColor Gray
Write-Host "  Reverse Flow: Target AD -> Metaverse -> Source AD" -ForegroundColor Gray
Write-Host ""
Write-Host "Run Profiles Created:" -ForegroundColor Yellow
Write-Host "  Source AD: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host "  Target AD: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Populate Source AD with test users:" -ForegroundColor Gray
Write-Host "     pwsh test/integration/Populate-SambaAD.ps1 -Container samba-ad-source -Template $Template" -ForegroundColor Gray
Write-Host "  2. Run: ./scenarios/Invoke-Scenario2-DirectorySync.ps1 -ApiKey `$ApiKey -Template $Template" -ForegroundColor Gray
