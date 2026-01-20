<#
.SYNOPSIS
    Configure JIM for Scenario 2: Directory to Directory Synchronisation

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for bidirectional LDAP synchronisation.
    This script creates:
    - LDAP Connected System (Quantum Dynamics APAC)
    - LDAP Connected System (Quantum Dynamics EMEA)
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
    - Quantum Dynamics APAC and EMEA containers running (docker compose --profile scenario2)
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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

# Step 4: Create Source LDAP Connected System (Quantum Dynamics APAC)
Write-TestStep "Step 4" "Creating Source LDAP Connected System"

$existingSystems = Get-JIMConnectedSystem

try {
    $sourceSystem = $existingSystems | Where-Object { $_.name -eq "Quantum Dynamics APAC" }

    if ($sourceSystem) {
        Write-Host "  Connected System 'Quantum Dynamics APAC' already exists (ID: $($sourceSystem.id))" -ForegroundColor Yellow
    }
    else {
        $sourceSystem = New-JIMConnectedSystem `
            -Name "Quantum Dynamics APAC" `
            -Description "Quantum Dynamics APAC Active Directory for cross-domain sync" `
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

# Step 5: Create Target LDAP Connected System (Quantum Dynamics EMEA)
Write-TestStep "Step 5" "Creating Target LDAP Connected System"

try {
    $targetSystem = $existingSystems | Where-Object { $_.name -eq "Quantum Dynamics EMEA" }

    if ($targetSystem) {
        Write-Host "  Connected System 'Quantum Dynamics EMEA' already exists (ID: $($targetSystem.id))" -ForegroundColor Yellow
    }
    else {
        $targetSystem = New-JIMConnectedSystem `
            -Name "Quantum Dynamics EMEA" `
            -Description "Quantum Dynamics EMEA Active Directory for cross-domain sync" `
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
$sourcePartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
if ($sourcePartitionsCheck -and $sourcePartitionsCheck.Count -gt 0) {
    Write-Host "  Source hierarchy already imported ($($sourcePartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Source LDAP hierarchy..." -ForegroundColor Gray
        Import-JIMConnectedSystemHierarchy -Id $sourceSystem.id | Out-Null
        $sourcePartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
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
$targetPartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
if ($targetPartitionsCheck -and $targetPartitionsCheck.Count -gt 0) {
    Write-Host "  Target hierarchy already imported ($($targetPartitionsCheck.Count) partitions)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Target LDAP hierarchy..." -ForegroundColor Gray
        Import-JIMConnectedSystemHierarchy -Id $targetSystem.id | Out-Null
        $targetPartitionsCheck = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
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
    $sourcePartitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id)
    Write-Host "    Found $($sourcePartitions.Count) partition(s):" -ForegroundColor Gray
    foreach ($p in $sourcePartitions) {
        Write-Host "      - Name: '$($p.name)', ExternalId: '$($p.externalId)'" -ForegroundColor Gray
    }

    if ($sourcePartitions -and $sourcePartitions.Count -gt 0) {
        # Find the main domain partition (DC=sourcedomain,DC=local)
        $sourceDomainPartition = $sourcePartitions | Where-Object {
            $_.name -eq "DC=sourcedomain,DC=local"
        }
        # Fallback: if only one partition and filter didn't match, use it
        if (-not $sourceDomainPartition -and $sourcePartitions.Count -eq 1) {
            $sourceDomainPartition = $sourcePartitions[0]
            Write-Host "    Using single available partition: $($sourceDomainPartition.name)" -ForegroundColor Yellow
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
            if ($partition.name -ne "DC=sourcedomain,DC=local" -and $partition.name -ne $sourceDomainPartition.name) {
                Set-JIMConnectedSystemPartition -ConnectedSystemId $sourceSystem.id -PartitionId $partition.id -Selected $false | Out-Null
            }
        }
    }
    else {
        Write-Host "    ⚠ No partitions found for Source system" -ForegroundColor Yellow
    }

    # Configure Target system - only select domain partition and TestUsers container
    Write-Host "  Configuring Target LDAP partitions..." -ForegroundColor Gray
    $targetPartitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $targetSystem.id)
    Write-Host "    Found $($targetPartitions.Count) partition(s):" -ForegroundColor Gray
    foreach ($p in $targetPartitions) {
        Write-Host "      - Name: '$($p.name)', ExternalId: '$($p.externalId)'" -ForegroundColor Gray
    }

    if ($targetPartitions -and $targetPartitions.Count -gt 0) {
        # Find the main domain partition (DC=targetdomain,DC=local)
        $targetDomainPartition = $targetPartitions | Where-Object {
            $_.name -eq "DC=targetdomain,DC=local"
        }
        # Fallback: if only one partition and filter didn't match, use it
        if (-not $targetDomainPartition -and $targetPartitions.Count -eq 1) {
            $targetDomainPartition = $targetPartitions[0]
            Write-Host "    Using single available partition: $($targetDomainPartition.name)" -ForegroundColor Yellow
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
            if ($partition.name -ne "DC=targetdomain,DC=local" -and $partition.name -ne $targetDomainPartition.name) {
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

    # Mark objectGUID as External ID (anchor) for both systems
    # objectGUID is the correct anchor for AD because it's immutable and system-assigned
    # sAMAccountName can change (user renames) and is not suitable as an anchor
    $sourceAnchorAttr = $sourceUserType.attributes | Where-Object { $_.name -eq 'objectGUID' }
    $targetAnchorAttr = $targetUserType.attributes | Where-Object { $_.name -eq 'objectGUID' }

    if ($sourceAnchorAttr) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeId $sourceAnchorAttr.id -IsExternalId $true | Out-Null
        Write-Host "  ✓ Set 'objectGUID' as External ID for Source" -ForegroundColor Green
    }
    if ($targetAnchorAttr) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeId $targetAnchorAttr.id -IsExternalId $true | Out-Null
        Write-Host "  ✓ Set 'objectGUID' as External ID for Target" -ForegroundColor Green
    }

    # Select only the LDAP attributes needed for bidirectional sync flows
    # This is more representative of real-world ILM configuration where administrators
    # only import/export the attributes they actually need, rather than the entire schema.
    # See: https://github.com/TetronIO/JIM/issues/227
    $requiredLdapAttributes = @(
        'objectGUID',         # Immutable object identifier - External ID (anchor)
        'sAMAccountName',     # Account Name - used for matching/joining
        'givenName',          # First Name
        'sn',                 # Last Name (surname)
        'displayName',        # Display Name
        'cn',                 # Common Name (also mapped from Display Name)
        'mail',               # Email
        'userPrincipalName',  # UPN (also mapped from Email)
        'title',              # Job Title
        'department',         # Department
        'telephoneNumber',    # Phone
        'distinguishedName'   # DN - required for LDAP provisioning (Secondary External ID)
    )

    # Using bulk update API for efficiency - creates single Activity record instead of one per attribute
    $sourceAttrUpdates = @{}
    foreach ($attr in $sourceUserType.attributes) {
        if ($requiredLdapAttributes -contains $attr.name) {
            $sourceAttrUpdates[$attr.id] = @{ selected = $true }
        }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $sourceSystem.id -ObjectTypeId $sourceUserType.id -AttributeUpdates $sourceAttrUpdates | Out-Null

    $targetAttrUpdates = @{}
    foreach ($attr in $targetUserType.attributes) {
        if ($requiredLdapAttributes -contains $attr.name) {
            $targetAttrUpdates[$attr.id] = @{ selected = $true }
        }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeUpdates $targetAttrUpdates | Out-Null
    Write-Host "  ✓ Selected $($sourceAttrUpdates.Count) attributes for Source and Target (minimal set for sync)" -ForegroundColor Green
    Write-Host "    Attributes: $($requiredLdapAttributes -join ', ')" -ForegroundColor DarkGray

    # Create Import sync rule (Source -> Metaverse)
    $existingRules = Get-JIMSyncRule
    $sourceImportRuleName = "APAC AD Import Users"
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
    $targetExportRuleName = "EMEA AD Export Users"
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
    $targetImportRuleName = "EMEA AD Import Users"
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
    $sourceExportRuleName = "APAC AD Export Users"
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

        # Expression-based mappings for computed values
        # distinguishedName is required for LDAP provisioning - tells the connector where to create the object
        $targetExpressionMappings = @(
            @{
                LdapAttr = "distinguishedName"
                Expression = '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=TestUsers,DC=targetdomain,DC=local"'
            }
        )

        # For reverse sync (Source export), also need DN expression
        $sourceExpressionMappings = @(
            @{
                LdapAttr = "distinguishedName"
                Expression = '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=TestUsers,DC=sourcedomain,DC=local"'
            }
        )

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

        # Create expression-based export mappings for Target (for DN construction)
        $targetExpressionMappingsCreated = 0
        foreach ($mapping in $targetExpressionMappings) {
            $ldapAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }

            if ($ldapAttr) {
                # Check if mapping already exists for this target attribute
                $existsAlready = $existingExportMappings | Where-Object {
                    $_.targetConnectedSystemAttributeId -eq $ldapAttr.id
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $targetExportRule.id `
                            -TargetConnectedSystemAttributeId $ldapAttr.id `
                            -Expression $mapping.Expression | Out-Null
                        $targetExpressionMappingsCreated++
                        Write-Host "    ✓ Created expression mapping for $($mapping.LdapAttr)" -ForegroundColor Green
                    }
                    catch {
                        Write-Host "    ⚠ Could not create expression mapping for $($mapping.LdapAttr): $_" -ForegroundColor Yellow
                    }
                }
            }
        }
        if ($targetExpressionMappingsCreated -gt 0) {
            Write-Host "  ✓ Target expression-based mappings configured ($targetExpressionMappingsCreated new)" -ForegroundColor Green
        }

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

            # Create expression-based export mappings for Source (for DN construction in reverse sync)
            $sourceExpressionMappingsCreated = 0
            foreach ($mapping in $sourceExpressionMappings) {
                $ldapAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }

                if ($ldapAttr) {
                    # Check if mapping already exists for this target attribute
                    $existsAlready = $existingSourceExportMappings | Where-Object {
                        $_.targetConnectedSystemAttributeId -eq $ldapAttr.id
                    }

                    if (-not $existsAlready) {
                        try {
                            New-JIMSyncRuleMapping -SyncRuleId $sourceExportRule.id `
                                -TargetConnectedSystemAttributeId $ldapAttr.id `
                                -Expression $mapping.Expression | Out-Null
                            $sourceExpressionMappingsCreated++
                            Write-Host "    ✓ Created expression mapping for $($mapping.LdapAttr)" -ForegroundColor Green
                        }
                        catch {
                            Write-Host "    ⚠ Could not create expression mapping for $($mapping.LdapAttr): $_" -ForegroundColor Yellow
                        }
                    }
                }
            }
            if ($sourceExpressionMappingsCreated -gt 0) {
                Write-Host "  ✓ Source expression-based mappings configured ($sourceExpressionMappingsCreated new)" -ForegroundColor Green
            }
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
                        -TargetMetaverseAttributeId $mvAccountNameAttr.id `
                        -SourceAttributeId $sourceSamAttr.id | Out-Null
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
                        -TargetMetaverseAttributeId $mvAccountNameAttr.id `
                        -SourceAttributeId $targetSamAttr.id | Out-Null
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

    # Source (APAC) - Full Import
    $sourceImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $sourceImportProfile) {
        $sourceImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile for Source (APAC)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists for Source (APAC)" -ForegroundColor Gray
    }

    # Source (APAC) - Full Sync
    $sourceSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Sync" }
    if (-not $sourceSyncProfile) {
        $sourceSyncProfile = New-JIMRunProfile `
            -Name "Full Sync" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Full Sync' run profile for Source (APAC)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Sync' already exists for Source (APAC)" -ForegroundColor Gray
    }

    # Source (APAC) - Export (for reverse sync)
    $sourceExportProfile = $sourceProfiles | Where-Object { $_.name -eq "Export" }
    if (-not $sourceExportProfile) {
        $sourceExportProfile = New-JIMRunProfile `
            -Name "Export" `
            -ConnectedSystemId $sourceSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Export' run profile for Source (APAC)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Export' already exists for Source (APAC)" -ForegroundColor Gray
    }

    # Target (EMEA) - Full Import
    $targetImportProfile = $targetProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $targetImportProfile) {
        $targetImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile for Target (EMEA)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists for Target (EMEA)" -ForegroundColor Gray
    }

    # Target (EMEA) - Full Sync
    $targetSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Full Sync" }
    if (-not $targetSyncProfile) {
        $targetSyncProfile = New-JIMRunProfile `
            -Name "Full Sync" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Full Sync' run profile for Target (EMEA)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Sync' already exists for Target (EMEA)" -ForegroundColor Gray
    }

    # Target (EMEA) - Export
    $targetExportProfile = $targetProfiles | Where-Object { $_.name -eq "Export" }
    if (-not $targetExportProfile) {
        $targetExportProfile = New-JIMRunProfile `
            -Name "Export" `
            -ConnectedSystemId $targetSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Export' run profile for Target (EMEA)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Export' already exists for Target (EMEA)" -ForegroundColor Gray
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
Write-Host "  Forward Flow: APAC AD -> Metaverse -> EMEA AD" -ForegroundColor Gray
Write-Host "  Reverse Flow: EMEA AD -> Metaverse -> APAC AD" -ForegroundColor Gray
Write-Host ""
Write-Host "Run Profiles Created:" -ForegroundColor Yellow
Write-Host "  Quantum Dynamics APAC: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host "  Quantum Dynamics EMEA: Full Import, Full Sync, Export" -ForegroundColor Gray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Populate APAC AD with test users:" -ForegroundColor Gray
Write-Host "     pwsh test/integration/Populate-SambaAD.ps1 -Container samba-ad-source -Template $Template" -ForegroundColor Gray
Write-Host "  2. Run: ./scenarios/Invoke-Scenario2-CrossDomainSync.ps1 -ApiKey `$ApiKey -Template $Template" -ForegroundColor Gray
