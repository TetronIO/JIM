<#
.SYNOPSIS
    Configure JIM for Scenario 1: HR to Enterprise Directory

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for HR to Active Directory provisioning.
    This script creates:
    - CSV Connected System (HR source)
    - LDAP Connected System (Samba AD target)
    - Sync Rules for attribute mappings
    - Run Profiles for synchronisation

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication (if not provided, will attempt to create one)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Setup-Scenario1.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario1.ps1 -Template Small

.NOTES
    This script requires the JIM PowerShell module and assumes:
    - JIM is running and accessible
    - Samba AD container is running on jim-network
    - CSV files exist at /var/connector-files/test-data/hr-users.csv
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
$ConfirmPreference = 'None'  # Disable confirmation prompts for non-interactive execution

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 1 Setup: HR to Enterprise Directory"

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

# Step 2b: Clean up existing configuration from previous runs
# NOTE: This cleanup is only needed when:
# 1. Running Setup-Scenario1.ps1 manually during development
# 2. Running with Invoke-IntegrationTests.ps1 -ScenariosOnly (skips database reset)
# 3. Re-running setup without tearing down containers
#
# When Invoke-IntegrationTests.ps1 runs normally (default), it does:
#   docker compose down -v (Step 0: Reset Environment)
# This deletes the database volume, so there are no Connected Systems to clean up.
#
# This cleanup is idempotent and fast (single API call), so it's safe to keep
# for developer convenience during iterative testing workflows.
Write-TestStep "Step 2b" "Cleaning up existing JIM configuration"

try {
    $existingSystems = Get-JIMConnectedSystem -ErrorAction SilentlyContinue
    if ($existingSystems -and $existingSystems.Count -gt 0) {
        Write-Host "  Found $($existingSystems.Count) existing Connected System(s) - removing..." -ForegroundColor Yellow
        foreach ($system in $existingSystems) {
            Write-Host "    Deleting '$($system.name)' (ID: $($system.id))..." -ForegroundColor Gray
            try {
                Remove-JIMConnectedSystem -Id $system.id -Force -ErrorAction Stop
                Write-Host "    ✓ Deleted '$($system.name)'" -ForegroundColor Green
            }
            catch {
                Write-Host "    ⚠ Could not delete '$($system.name)': $_" -ForegroundColor Yellow
            }
        }
        Write-Host "  ✓ Existing Connected Systems removed" -ForegroundColor Green
        Write-Host "  ⚠ Note: Orphaned MVOs may remain. Run Reset-JIM.ps1 for full cleanup." -ForegroundColor DarkYellow
    }
    else {
        Write-Host "  ✓ No existing Connected Systems found (clean slate)" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ⚠ Could not check for existing systems: $_" -ForegroundColor Yellow
    Write-Host "    Continuing with setup..." -ForegroundColor Gray
}

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Getting connector definitions"

try {
    # Get all connector definitions
    $connectorDefs = Get-JIMConnectorDefinition

    $csvConnector = $connectorDefs | Where-Object { $_.name -eq "JIM File Connector" }
    $ldapConnector = $connectorDefs | Where-Object { $_.name -eq "JIM LDAP Connector" }

    if (-not $csvConnector) {
        throw "JIM File Connector definition not found"
    }

    if (-not $ldapConnector) {
        throw "JIM LDAP Connector definition not found"
    }

    Write-Host "  ✓ Found File connector (ID: $($csvConnector.id))" -ForegroundColor Green
    Write-Host "  ✓ Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to get connector definitions: $_" -ForegroundColor Red
    throw
}

# Step 4: Create CSV Connected System (HR source)
Write-TestStep "Step 4" "Creating CSV Connected System"

try {
    # Check if already exists
    $existingSystems = Get-JIMConnectedSystem
    $csvSystem = $existingSystems | Where-Object { $_.name -eq "HR CSV Source" }

    if ($csvSystem) {
        Write-Host "  Connected System 'HR CSV Source' already exists (ID: $($csvSystem.id))" -ForegroundColor Yellow
    }
    else {
        $csvSystem = New-JIMConnectedSystem `
            -Name "HR CSV Source" `
            -Description "HR system CSV export for integration testing" `
            -ConnectorDefinitionId $csvConnector.id `
            -PassThru

        Write-Host "  ✓ Created CSV Connected System (ID: $($csvSystem.id))" -ForegroundColor Green
    }

    # Configure CSV settings
    # Get settings from the connector definition to find setting IDs
    $csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
    $filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "File Path" }
    $delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
    $objectTypeSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Object Type" }

    $settingValues = @{}
    if ($filePathSetting) {
        $settingValues[$filePathSetting.id] = @{ stringValue = "/var/connector-files/test-data/hr-users.csv" }
    }
    if ($delimiterSetting) {
        $settingValues[$delimiterSetting.id] = @{ stringValue = "," }
    }
    if ($objectTypeSetting) {
        $settingValues[$objectTypeSetting.id] = @{ stringValue = "person" }
    }

    if ($settingValues.Count -gt 0) {
        Set-JIMConnectedSystem -Id $csvSystem.id -SettingValues $settingValues | Out-Null
        Write-Host "  ✓ Configured CSV settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure CSV Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5: Create LDAP Connected System (Samba AD target)
Write-TestStep "Step 5" "Creating LDAP Connected System"

try {
    $ldapSystem = $existingSystems | Where-Object { $_.name -eq "Subatomic AD" }

    if ($ldapSystem) {
        Write-Host "  Connected System 'Subatomic AD' already exists (ID: $($ldapSystem.id))" -ForegroundColor Yellow
    }
    else {
        $ldapSystem = New-JIMConnectedSystem `
            -Name "Subatomic AD" `
            -Description "Samba Active Directory for integration testing" `
            -ConnectorDefinitionId $ldapConnector.id `
            -PassThru

        Write-Host "  ✓ Created LDAP Connected System (ID: $($ldapSystem.id))" -ForegroundColor Green
    }

    # Configure LDAP settings
    # Note: Using LDAPS (port 636) with Simple authentication over TLS
    # This satisfies AD's strong authentication requirement
    $ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

    # Find setting IDs by name (using actual setting names from connector definition)
    $hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
    $portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
    $usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
    $passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
    $useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
    $certValidationSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Certificate Validation" }
    $connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
    $authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }
    $createContainersSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Create containers as needed?" }

    $ldapSettings = @{}
    if ($hostSetting) {
        $ldapSettings[$hostSetting.id] = @{ stringValue = "samba-ad-primary" }
    }
    if ($portSetting) {
        # Use LDAPS port 636 for encrypted connection
        $ldapSettings[$portSetting.id] = @{ intValue = 636 }
    }
    if ($usernameSetting) {
        # DN format for Simple bind
        $ldapSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=subatomic,DC=local" }
    }
    if ($passwordSetting) {
        # Password setting uses stringValue - API stores it encrypted based on setting type
        $ldapSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" }
    }
    if ($useSSLSetting) {
        # Enable LDAPS for encrypted connection
        $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $true }
    }
    if ($certValidationSetting) {
        # Skip cert validation for self-signed test certificates
        $ldapSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" }
    }
    if ($connectionTimeoutSetting) {
        $ldapSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 }
    }
    if ($authTypeSetting) {
        # Simple authentication over TLS satisfies AD strong auth requirement
        $ldapSettings[$authTypeSetting.id] = @{ stringValue = "Simple" }
    }
    if ($createContainersSetting) {
        # Enable automatic OU creation when provisioning objects to non-existent OUs
        $ldapSettings[$createContainersSetting.id] = @{ checkboxValue = $true }
    }

    if ($ldapSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $ldapSettings | Out-Null
        Write-Host "  ✓ Configured LDAP settings (including automatic container creation)" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5b: Create Training CSV Connected System (second source system)
Write-TestStep "Step 5b" "Creating Training CSV Connected System"

try {
    $trainingSystem = $existingSystems | Where-Object { $_.name -eq "Training Records Source" }

    if ($trainingSystem) {
        Write-Host "  Connected System 'Training Records Source' already exists (ID: $($trainingSystem.id))" -ForegroundColor Yellow
    }
    else {
        $trainingSystem = New-JIMConnectedSystem `
            -Name "Training Records Source" `
            -Description "Training LMS CSV export - contributes training attributes to users" `
            -ConnectorDefinitionId $csvConnector.id `
            -PassThru

        Write-Host "  ✓ Created Training CSV Connected System (ID: $($trainingSystem.id))" -ForegroundColor Green
    }

    # Configure Training CSV settings
    $trainingSettingValues = @{}
    if ($filePathSetting) {
        $trainingSettingValues[$filePathSetting.id] = @{ stringValue = "/var/connector-files/test-data/training-records.csv" }
    }
    if ($delimiterSetting) {
        $trainingSettingValues[$delimiterSetting.id] = @{ stringValue = "," }
    }
    if ($objectTypeSetting) {
        $trainingSettingValues[$objectTypeSetting.id] = @{ stringValue = "trainingRecord" }
    }

    if ($trainingSettingValues.Count -gt 0) {
        Set-JIMConnectedSystem -Id $trainingSystem.id -SettingValues $trainingSettingValues | Out-Null
        Write-Host "  ✓ Configured Training CSV settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure Training Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5c: Create Cross-Domain CSV Connected System (second target system)
Write-TestStep "Step 5c" "Creating Cross-Domain CSV Connected System"

try {
    $crossDomainSystem = $existingSystems | Where-Object { $_.name -eq "Cross-Domain Export" }

    if ($crossDomainSystem) {
        Write-Host "  Connected System 'Cross-Domain Export' already exists (ID: $($crossDomainSystem.id))" -ForegroundColor Yellow
    }
    else {
        $crossDomainSystem = New-JIMConnectedSystem `
            -Name "Cross-Domain Export" `
            -Description "Cross-domain user export target CSV" `
            -ConnectorDefinitionId $csvConnector.id `
            -PassThru

        Write-Host "  ✓ Created Cross-Domain CSV Connected System (ID: $($crossDomainSystem.id))" -ForegroundColor Green
    }

    # Configure Cross-Domain CSV settings
    $crossDomainSettingValues = @{}
    if ($filePathSetting) {
        $crossDomainSettingValues[$filePathSetting.id] = @{ stringValue = "/var/connector-files/test-data/cross-domain-users.csv" }
    }
    if ($delimiterSetting) {
        $crossDomainSettingValues[$delimiterSetting.id] = @{ stringValue = "," }
    }
    if ($objectTypeSetting) {
        $crossDomainSettingValues[$objectTypeSetting.id] = @{ stringValue = "user" }
    }

    if ($crossDomainSettingValues.Count -gt 0) {
        Set-JIMConnectedSystem -Id $crossDomainSystem.id -SettingValues $crossDomainSettingValues | Out-Null
        Write-Host "  ✓ Configured Cross-Domain CSV settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure Cross-Domain Connected System: $_" -ForegroundColor Red
    throw
}

# Step 6: Import Schemas
Write-TestStep "Step 6" "Importing Connected System Schemas"

# Check if schemas are already imported (to avoid FK constraint errors on re-run)
$csvObjectTypes = Get-JIMConnectedSystem -Id $csvSystem.id -ObjectTypes
if ($csvObjectTypes -and $csvObjectTypes.Count -gt 0) {
    Write-Host "  CSV schema already imported ($($csvObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        # Import CSV schema
        Write-Host "  Importing CSV schema..." -ForegroundColor Gray
        Import-JIMConnectedSystemSchema -Id $csvSystem.id | Out-Null
        $csvObjectTypes = Get-JIMConnectedSystem -Id $csvSystem.id -ObjectTypes
        Write-Host "  ✓ CSV schema imported ($($csvObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Failed to import CSV schema: $_" -ForegroundColor Red
        Write-Host "    Ensure connected system is properly configured before importing schema" -ForegroundColor Yellow
        throw
    }
}

$ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
if ($ldapObjectTypes -and $ldapObjectTypes.Count -gt 0) {
    Write-Host "  LDAP schema already imported ($($ldapObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        # Import LDAP schema
        Write-Host "  Importing LDAP schema..." -ForegroundColor Gray
        Import-JIMConnectedSystemSchema -Id $ldapSystem.id | Out-Null
        $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
        Write-Host "  ✓ LDAP schema imported ($($ldapObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ⚠ LDAP schema import failed: $_" -ForegroundColor Yellow
        Write-Host "    LDAP export sync rules will be skipped. Continuing with CSV import only." -ForegroundColor Yellow
        $ldapObjectTypes = @()
    }
}

# Import Training CSV schema
$trainingObjectTypes = Get-JIMConnectedSystem -Id $trainingSystem.id -ObjectTypes
if ($trainingObjectTypes -and $trainingObjectTypes.Count -gt 0) {
    Write-Host "  Training CSV schema already imported ($($trainingObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Training CSV schema..." -ForegroundColor Gray
        Import-JIMConnectedSystemSchema -Id $trainingSystem.id | Out-Null
        $trainingObjectTypes = Get-JIMConnectedSystem -Id $trainingSystem.id -ObjectTypes
        Write-Host "  ✓ Training CSV schema imported ($($trainingObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ⚠ Training CSV schema import failed: $_" -ForegroundColor Yellow
        $trainingObjectTypes = @()
    }
}

# Import Cross-Domain CSV schema
$crossDomainObjectTypes = Get-JIMConnectedSystem -Id $crossDomainSystem.id -ObjectTypes
if ($crossDomainObjectTypes -and $crossDomainObjectTypes.Count -gt 0) {
    Write-Host "  Cross-Domain CSV schema already imported ($($crossDomainObjectTypes.Count) object types)" -ForegroundColor Gray
}
else {
    try {
        Write-Host "  Importing Cross-Domain CSV schema..." -ForegroundColor Gray
        Import-JIMConnectedSystemSchema -Id $crossDomainSystem.id | Out-Null
        $crossDomainObjectTypes = Get-JIMConnectedSystem -Id $crossDomainSystem.id -ObjectTypes
        Write-Host "  ✓ Cross-Domain CSV schema imported ($($crossDomainObjectTypes.Count) object types)" -ForegroundColor Green
    }
    catch {
        Write-Host "  ⚠ Cross-Domain CSV schema import failed: $_" -ForegroundColor Yellow
        $crossDomainObjectTypes = @()
    }
}

# Step 6a: Import LDAP Hierarchy and Select Containers
Write-TestStep "Step 6a" "Importing LDAP hierarchy and selecting Corp container"

try {
    # Import the partition/container hierarchy from LDAP
    # This retrieves naming contexts (partitions) and OUs (containers) from Samba AD
    Write-Host "  Importing LDAP hierarchy..." -ForegroundColor Gray
    Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id | Out-Null

    # Get the partitions (wrap in @() to ensure array even if single result)
    $partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id)

    if ($partitions -and $partitions.Count -gt 0) {
        Write-Host "  Found $($partitions.Count) partition(s):" -ForegroundColor Gray
        foreach ($p in $partitions) {
            Write-Host "    - Name: '$($p.name)', ExternalId: '$($p.externalId)', Selected: $($p.selected)" -ForegroundColor Gray
        }

        # Find the main domain partition (DC=subatomic,DC=local)
        # Note: API returns 'name' (display name) and 'externalId' (distinguished name)
        # We need the exact domain partition, not ForestDnsZones or DomainDnsZones
        $domainPartition = $partitions | Where-Object {
            $_.name -eq "DC=subatomic,DC=local" -or $_.externalId -eq "DC=subatomic,DC=local"
        } | Select-Object -First 1

        # Fallback: if only one partition and filter didn't match, use it (it's the domain partition)
        if (-not $domainPartition -and $partitions.Count -eq 1) {
            $domainPartition = $partitions[0]
            Write-Host "  Using single available partition: $($domainPartition.name)" -ForegroundColor Yellow
        }

        if ($domainPartition) {
            Write-Host "  Selecting partition: $($domainPartition.name) (ID: $($domainPartition.id))" -ForegroundColor Gray
            Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $domainPartition.id -Selected $true | Out-Null
            Write-Host "  ✓ Partition selected: $($domainPartition.name)" -ForegroundColor Green

            # Find and select the "Corp" container within this partition
            # The hierarchy structure is: partition -> containers (nested)
            $corpContainer = $null

            # Helper function to search containers recursively
            # Note: API returns camelCase JSON, so use 'childContainers' for nested containers
            function Find-Container {
                param($Containers, $Name)
                foreach ($container in $Containers) {
                    if ($container.name -eq $Name) {
                        return $container
                    }
                    # Child containers are in the 'childContainers' property (camelCase from API)
                    if ($container.childContainers) {
                        $found = Find-Container -Containers $container.childContainers -Name $Name
                        if ($found) { return $found }
                    }
                }
                return $null
            }

            # Partition's top-level containers are in the 'containers' property
            Write-Host "  Looking for containers in partition..." -ForegroundColor Gray
            if ($domainPartition.containers) {
                Write-Host "    Found $($domainPartition.containers.Count) top-level container(s):" -ForegroundColor Gray
                foreach ($c in $domainPartition.containers) {
                    Write-Host "      - Name: '$($c.name)', ID: $($c.id), Selected: $($c.selected)" -ForegroundColor Gray
                }
                $corpContainer = Find-Container -Containers $domainPartition.containers -Name "Corp"
            }
            else {
                Write-Host "    No containers found in partition (containers property is null/empty)" -ForegroundColor Yellow
            }

            if ($corpContainer) {
                Write-Host "  Selecting container: Corp (ID: $($corpContainer.id))" -ForegroundColor Gray
                Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $corpContainer.id -Selected $true | Out-Null
                Write-Host "  ✓ Container selected: Corp" -ForegroundColor Green
                Write-Host "    Users will be provisioned under: OU=Users,OU=Corp,DC=subatomic,DC=local" -ForegroundColor DarkGray
                Write-Host "    Department OUs will be auto-created: OU={Dept},OU=Users,OU=Corp,DC=subatomic,DC=local" -ForegroundColor DarkGray
            }
            else {
                Write-Host "  ⚠ 'Corp' container not found in hierarchy" -ForegroundColor Yellow
                Write-Host "    Available top-level containers:" -ForegroundColor Gray
                if ($domainPartition.containers) {
                    $domainPartition.containers | ForEach-Object { Write-Host "      - $($_.name)" -ForegroundColor Gray }
                }
                else {
                    Write-Host "      (none)" -ForegroundColor Gray
                }
                Write-Host "    Ensure Populate-SambaAD.ps1 has been run to create the Corp OU" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "  ⚠ Could not find subatomic partition" -ForegroundColor Yellow
            Write-Host "    Available partitions:" -ForegroundColor Gray
            $partitions | ForEach-Object { Write-Host "      - $($_.name)" -ForegroundColor Gray }
        }
    }
    else {
        Write-Host "  ⚠ No partitions found in hierarchy" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ⚠ Failed to import hierarchy or select containers: $_" -ForegroundColor Yellow
    Write-Host "    This may affect LDAP import operations. Continuing with setup..." -ForegroundColor DarkYellow
}

# Note: Department OUs are no longer pre-created. The LDAP Connector's
# "Create containers as needed?" setting (enabled above in Step 5) will
# automatically create any required OUs during export when provisioning
# users to department-based OUs. This tests the container creation functionality.

# Step 6b: Create Sync Rules
Write-TestStep "Step 6b" "Creating Sync Rules"

try {
    # Get the "user" object type from both systems (common in identity management)
    # For CSV, it might be "Person" or similar; for LDAP it's typically "user"
    $csvUserType = $csvObjectTypes | Where-Object { $_.name -match "^(user|person|record)$" } | Select-Object -First 1
    $ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1

    # Get the Metaverse "User" object type
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

    if (-not $csvUserType) {
        Write-Host "  ⚠ No user/person object type found in CSV schema. Available types:" -ForegroundColor Yellow
        $csvObjectTypes | ForEach-Object { Write-Host "    - $($_.name)" -ForegroundColor Gray }
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    elseif (-not $ldapUserType) {
        Write-Host "  ⚠ No 'user' object type found in LDAP schema. Available types:" -ForegroundColor Yellow
        $ldapObjectTypes | ForEach-Object { Write-Host "    - $($_.name)" -ForegroundColor Gray }
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    elseif (-not $mvUserType) {
        Write-Host "  ⚠ No 'User' object type found in Metaverse" -ForegroundColor Yellow
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    else {
        Write-Host "  Found object types:" -ForegroundColor Gray
        Write-Host "    CSV: $($csvUserType.name) (ID: $($csvUserType.id))" -ForegroundColor Gray
        Write-Host "    LDAP: $($ldapUserType.name) (ID: $($ldapUserType.id))" -ForegroundColor Gray
        Write-Host "    Metaverse: $($mvUserType.name) (ID: $($mvUserType.id))" -ForegroundColor Gray

        # Mark the required object types as selected (required for import/export to work)
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id -Selected $true | Out-Null
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -Selected $true | Out-Null
        Write-Host "  ✓ Selected CSV 'person' and LDAP 'User' object types" -ForegroundColor Green

        # Mark employeeId as the External ID (anchor) for CSV object type
        $csvEmployeeIdAttr = $csvUserType.attributes | Where-Object { $_.name -eq 'employeeId' }
        if ($csvEmployeeIdAttr) {
            Set-JIMConnectedSystemAttribute -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id -AttributeId $csvEmployeeIdAttr.id -IsExternalId $true | Out-Null
            Write-Host "  ✓ Set 'employeeId' as External ID for CSV object type" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Could not find 'employeeId' attribute in CSV object type" -ForegroundColor Yellow
        }

        # Mark all CSV attributes as selected (required for import)
        # Using bulk update API for efficiency - creates single Activity record instead of one per attribute
        $csvAttrUpdates = @{}
        foreach ($attr in $csvUserType.attributes) {
            $csvAttrUpdates[$attr.id] = @{ selected = $true }
        }
        $csvResult = Set-JIMConnectedSystemAttribute -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id -AttributeUpdates $csvAttrUpdates -PassThru -ErrorAction Stop
        Write-Host "  ✓ Selected $($csvResult.updatedCount) CSV attributes" -ForegroundColor Green

        # Select only the LDAP attributes needed for export flows
        # This is more representative of real-world ILM configuration where administrators
        # only import/export the attributes they actually need, rather than the entire schema.
        # See: https://github.com/TetronIO/JIM/issues/227
        $requiredLdapAttributes = @(
            'sAMAccountName',     # Account Name - required anchor
            'givenName',          # First Name
            'sn',                 # Last Name (surname)
            'displayName',        # Display Name
            'cn',                 # Common Name (also mapped from Display Name)
            'mail',               # Email
            'userPrincipalName',  # UPN (also mapped from Email)
            'title',              # Job Title
            'department',         # Department
            'company',            # Company name (Subatomic or partner company)
            'employeeID',         # Employee ID - required for LDAP matching rule (join to existing AD accounts)
            'distinguishedName',  # DN - required for LDAP provisioning
            'accountExpires',     # Account expiry (Large Integer/Int64) - populated from HR Employee End Date via ToFileTime
            'userAccountControl'  # Account control flags (Number/Int32) - tests integer data type flow
        )

        $ldapAttrUpdates = @{}
        foreach ($attr in $ldapUserType.attributes) {
            if ($requiredLdapAttributes -contains $attr.name) {
                $ldapAttrUpdates[$attr.id] = @{ selected = $true }
            }
        }
        $ldapResult = Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeUpdates $ldapAttrUpdates -PassThru -ErrorAction Stop
        Write-Host "  ✓ Selected $($ldapResult.updatedCount) LDAP attributes (minimal set for export)" -ForegroundColor Green
        Write-Host "    Attributes: $($requiredLdapAttributes -join ', ')" -ForegroundColor DarkGray

        # Create Import sync rule (CSV -> Metaverse)
        $existingRules = Get-JIMSyncRule
        $importRuleName = "HR CSV Import Users"
        $importRule = $existingRules | Where-Object { $_.name -eq $importRuleName }

        if (-not $importRule) {
            $importRule = New-JIMSyncRule `
                -Name $importRuleName `
                -ConnectedSystemId $csvSystem.id `
                -ConnectedSystemObjectTypeId $csvUserType.id `
                -MetaverseObjectTypeId $mvUserType.id `
                -Direction Import `
                -ProjectToMetaverse `
                -PassThru
            Write-Host "  ✓ Created import sync rule: $importRuleName" -ForegroundColor Green
        }
        else {
            Write-Host "  Import sync rule '$importRuleName' already exists" -ForegroundColor Gray
        }

        # Create Export sync rule (Metaverse -> LDAP)
        $exportRuleName = "Samba AD Export Users"
        $exportRule = $existingRules | Where-Object { $_.name -eq $exportRuleName }

        if (-not $exportRule) {
            $exportRule = New-JIMSyncRule `
                -Name $exportRuleName `
                -ConnectedSystemId $ldapSystem.id `
                -ConnectedSystemObjectTypeId $ldapUserType.id `
                -MetaverseObjectTypeId $mvUserType.id `
                -Direction Export `
                -ProvisionToConnectedSystem `
                -PassThru
            Write-Host "  ✓ Created export sync rule: $exportRuleName" -ForegroundColor Green
        }
        else {
            Write-Host "  Export sync rule '$exportRuleName' already exists" -ForegroundColor Gray
        }

        # Create Training Import sync rule (Training CSV -> Metaverse)
        # This rule joins to existing MVOs (created by HR import) and contributes training attributes
        $trainingRecordType = $trainingObjectTypes | Where-Object { $_.name -match "^(trainingRecord|record)$" } | Select-Object -First 1
        if ($trainingRecordType) {
            # Configure Training object type
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $trainingSystem.id -ObjectTypeId $trainingRecordType.id -Selected $true | Out-Null

            # Mark employeeId as External ID for Training
            $trainingEmployeeIdAttr = $trainingRecordType.attributes | Where-Object { $_.name -eq 'employeeId' }
            if ($trainingEmployeeIdAttr) {
                Set-JIMConnectedSystemAttribute -ConnectedSystemId $trainingSystem.id -ObjectTypeId $trainingRecordType.id -AttributeId $trainingEmployeeIdAttr.id -IsExternalId $true | Out-Null
            }

            # Select all Training attributes
            $trainingAttrUpdates = @{}
            foreach ($attr in $trainingRecordType.attributes) {
                $trainingAttrUpdates[$attr.id] = @{ selected = $true }
            }
            Set-JIMConnectedSystemAttribute -ConnectedSystemId $trainingSystem.id -ObjectTypeId $trainingRecordType.id -AttributeUpdates $trainingAttrUpdates -PassThru -ErrorAction Stop | Out-Null

            $trainingImportRuleName = "Training Records Import"
            $trainingImportRule = $existingRules | Where-Object { $_.name -eq $trainingImportRuleName }

            if (-not $trainingImportRule) {
                # Training import does NOT project - it joins to existing MVOs
                $trainingImportRule = New-JIMSyncRule `
                    -Name $trainingImportRuleName `
                    -ConnectedSystemId $trainingSystem.id `
                    -ConnectedSystemObjectTypeId $trainingRecordType.id `
                    -MetaverseObjectTypeId $mvUserType.id `
                    -Direction Import `
                    -PassThru
                Write-Host "  ✓ Created Training import sync rule: $trainingImportRuleName" -ForegroundColor Green
            }
            else {
                Write-Host "  Training import sync rule '$trainingImportRuleName' already exists" -ForegroundColor Gray
            }
        }
        else {
            Write-Host "  ⚠ Training record object type not found in schema" -ForegroundColor Yellow
            $trainingImportRule = $null
        }

        # Create Cross-Domain Export sync rule (Metaverse -> Cross-Domain CSV)
        $crossDomainUserType = $crossDomainObjectTypes | Where-Object { $_.name -match "^(user|record)$" } | Select-Object -First 1
        if ($crossDomainUserType) {
            # Configure Cross-Domain object type
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $crossDomainSystem.id -ObjectTypeId $crossDomainUserType.id -Selected $true | Out-Null

            # Mark samAccountName as External ID for Cross-Domain
            $crossDomainSamAttr = $crossDomainUserType.attributes | Where-Object { $_.name -eq 'samAccountName' }
            if ($crossDomainSamAttr) {
                Set-JIMConnectedSystemAttribute -ConnectedSystemId $crossDomainSystem.id -ObjectTypeId $crossDomainUserType.id -AttributeId $crossDomainSamAttr.id -IsExternalId $true | Out-Null
            }

            # Select all Cross-Domain attributes
            $crossDomainAttrUpdates = @{}
            foreach ($attr in $crossDomainUserType.attributes) {
                $crossDomainAttrUpdates[$attr.id] = @{ selected = $true }
            }
            Set-JIMConnectedSystemAttribute -ConnectedSystemId $crossDomainSystem.id -ObjectTypeId $crossDomainUserType.id -AttributeUpdates $crossDomainAttrUpdates -PassThru -ErrorAction Stop | Out-Null

            $crossDomainExportRuleName = "Cross-Domain Export Users"
            $crossDomainExportRule = $existingRules | Where-Object { $_.name -eq $crossDomainExportRuleName }

            if (-not $crossDomainExportRule) {
                $crossDomainExportRule = New-JIMSyncRule `
                    -Name $crossDomainExportRuleName `
                    -ConnectedSystemId $crossDomainSystem.id `
                    -ConnectedSystemObjectTypeId $crossDomainUserType.id `
                    -MetaverseObjectTypeId $mvUserType.id `
                    -Direction Export `
                    -ProvisionToConnectedSystem `
                    -PassThru
                Write-Host "  ✓ Created Cross-Domain export sync rule: $crossDomainExportRuleName" -ForegroundColor Green
            }
            else {
                Write-Host "  Cross-Domain export sync rule '$crossDomainExportRuleName' already exists" -ForegroundColor Gray
            }
        }
        else {
            Write-Host "  ⚠ Cross-Domain user object type not found in schema" -ForegroundColor Yellow
            $crossDomainExportRule = $null
        }
    }
}
catch {
    Write-Host "  ✗ Failed to create sync rules: $_" -ForegroundColor Red
    Write-Host "    Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - sync rules can be created manually if needed
}

# Step 6c: Configure Attribute Flow Mappings
Write-TestStep "Step 6c" "Configuring Attribute Flow Mappings"

try {
    if ($importRule -and $exportRule) {
        Write-Host "  Configuring attribute mappings..." -ForegroundColor Gray

        # Define mappings
        $importMappings = @(
            @{ CsAttr = "employeeId";        MvAttr = "Employee ID" }
            @{ CsAttr = "firstName";         MvAttr = "First Name" }
            @{ CsAttr = "lastName";          MvAttr = "Last Name" }
            @{ CsAttr = "displayName";       MvAttr = "Display Name" }
            @{ CsAttr = "email";             MvAttr = "Email" }
            @{ CsAttr = "title";             MvAttr = "Job Title" }
            @{ CsAttr = "department";        MvAttr = "Department" }
            @{ CsAttr = "company";           MvAttr = "Company" }  # Company name - Subatomic for employees, partner companies for contractors
            @{ CsAttr = "samAccountName";    MvAttr = "Account Name" }
            @{ CsAttr = "employeeType";      MvAttr = "Employee Type" }
            @{ CsAttr = "employeeEndDate";   MvAttr = "Employee End Date" }  # DateTime - HR end date → MV, then exported to AD accountExpires via ToFileTime
            @{ CsAttr = "status";            MvAttr = "Employee Status" }     # Active/Inactive - controls userAccountControl in AD
        )

        $exportMappings = @(
            @{ MvAttr = "Account Name";  LdapAttr = "sAMAccountName" }
            @{ MvAttr = "First Name";    LdapAttr = "givenName" }
            @{ MvAttr = "Last Name";     LdapAttr = "sn" }
            @{ MvAttr = "Display Name";  LdapAttr = "displayName" }
            @{ MvAttr = "Display Name";  LdapAttr = "cn" }
            @{ MvAttr = "Email";         LdapAttr = "mail" }
            @{ MvAttr = "Email";         LdapAttr = "userPrincipalName" }  # UPN = email for AD login
            @{ MvAttr = "Job Title";     LdapAttr = "title" }
            @{ MvAttr = "Department";    LdapAttr = "department" }
            @{ MvAttr = "Company";       LdapAttr = "company" }  # Company name exported to AD
            @{ MvAttr = "Employee ID";   LdapAttr = "employeeID" }  # Required for LDAP matching rule
        )

        # Expression-based mappings for computed values
        # DN uses Department to place users in department OUs under OU=Users,OU=Corp
        # Structure: CN={Display Name},OU={Department},OU=Users,OU=Corp,DC=subatomic,DC=local
        # This enables:
        #   1. OU move testing when department changes
        #   2. Auto-creation of department OUs by the LDAP connector (when "Create containers as needed?" is enabled)
        #   3. Partition/container selection testing (only Corp is selected)
        $expressionMappings = @(
            @{
                LdapAttr = "distinguishedName"
                Expression = '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=" + mv["Department"] + ",OU=Users,OU=Corp,DC=subatomic,DC=local"'
            }
            @{
                # userAccountControl: Conditional expression based on Employee Status
                # - "Active" → 512 (ADS_UF_NORMAL_ACCOUNT - enabled)
                # - "Inactive" or null → 514 (ADS_UF_NORMAL_ACCOUNT + ADS_UF_ACCOUNTDISABLE - disabled)
                # This tests:
                #   1. Integer data type export to AD
                #   2. Conditional expressions with IIF
                #   3. Protected attribute substitution when expression returns null
                # Note: Use Eq() for string comparison, NOT ==, because AttributeAccessor returns object?
                # and the == operator uses reference equality for object comparisons
                LdapAttr = "userAccountControl"
                Expression = 'IIF(Eq(mv["Employee Status"], "Active"), 512, 514)'
            }
            @{
                LdapAttr = "accountExpires"
                Expression = 'ToFileTime(mv["Employee End Date"])'  # DateTime → Large Integer (Int64) - HR end date converted to AD format
            }
        )

        # Get all metaverse attributes for lookup
        $mvAttributes = Get-JIMMetaverseAttribute

        # Create import mappings (CSV → Metaverse)
        $existingImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $importRule.id
        $importMappingsCreated = 0

        foreach ($mapping in $importMappings) {
            $csvAttr = $csvUserType.attributes | Where-Object { $_.name -eq $mapping.CsAttr }
            $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

            if ($csvAttr -and $mvAttr) {
                # Check if mapping already exists
                $existsAlready = $existingImportMappings | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvAttr.id -and
                    ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $csvAttr.id })
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
                            -TargetMetaverseAttributeId $mvAttr.id `
                            -SourceConnectedSystemAttributeId $csvAttr.id | Out-Null
                        $importMappingsCreated++
                    }
                    catch {
                        Write-Host "    ✗ Failed to create mapping $($mapping.CsAttr) → $($mapping.MvAttr): $_" -ForegroundColor Red
                        throw "Critical import mapping failed. Setup cannot continue."
                    }
                }
            }
        }
        Write-Host "  ✓ Import attribute mappings configured ($importMappingsCreated new)" -ForegroundColor Green

        # Add constant expression mapping for Type = PersonEntity on import rule
        $typeAttr = $mvAttributes | Where-Object { $_.name -eq "Type" }
        if ($typeAttr) {
            $existingTypeMapping = $existingImportMappings | Where-Object {
                $_.targetMetaverseAttributeId -eq $typeAttr.id
            }
            if (-not $existingTypeMapping) {
                try {
                    New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
                        -TargetMetaverseAttributeId $typeAttr.id `
                        -Expression '"PersonEntity"' | Out-Null
                    Write-Host "  ✓ Import Type=PersonEntity expression mapping configured" -ForegroundColor Green
                }
                catch {
                    Write-Host "    ✗ Failed to create Type expression mapping: $_" -ForegroundColor Red
                }
            }
        }

        # Create export mappings (Metaverse → LDAP)
        $existingExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id
        $exportMappingsCreated = 0

        foreach ($mapping in $exportMappings) {
            $ldapAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
            $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

            # Debug: Verify we're getting the right attributes
            # Always show during initial creation to help diagnose mapping issues
            Write-Host "    Export: MV '$($mapping.MvAttr)' (ID:$($mvAttr.id)) → LDAP '$($mapping.LdapAttr)' (ID:$($ldapAttr.id))" -ForegroundColor DarkGray

            if ($ldapAttr -and $mvAttr) {
                # Check if mapping already exists
                $existsAlready = $existingExportMappings | Where-Object {
                    $_.targetConnectedSystemAttributeId -eq $ldapAttr.id -and
                    ($_.sources | Where-Object { $_.metaverseAttributeId -eq $mvAttr.id })
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
                            -TargetConnectedSystemAttributeId $ldapAttr.id `
                            -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                        # Mapping created successfully - ID info already shown above
                        $exportMappingsCreated++
                    }
                    catch {
                        Write-Host "    ✗ Failed to create mapping $($mapping.MvAttr) → $($mapping.LdapAttr): $_" -ForegroundColor Red
                        throw "Critical export mapping failed. Setup cannot continue."
                    }
                }
            }
            else {
                Write-Host "    ⚠ Skipped mapping: LDAP '$($mapping.LdapAttr)' or MV '$($mapping.MvAttr)' not found" -ForegroundColor Yellow
            }
        }
        Write-Host "  ✓ Export attribute mappings configured ($exportMappingsCreated new)" -ForegroundColor Green

        # Create expression-based export mappings (for computed values like DN)
        $expressionMappingsCreated = 0
        foreach ($mapping in $expressionMappings) {
            $ldapAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }

            if ($ldapAttr) {
                # Check if mapping already exists for this target attribute
                $existsAlready = $existingExportMappings | Where-Object {
                    $_.targetConnectedSystemAttributeId -eq $ldapAttr.id
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
                            -TargetConnectedSystemAttributeId $ldapAttr.id `
                            -Expression $mapping.Expression | Out-Null
                        $expressionMappingsCreated++
                        Write-Host "    ✓ Created expression mapping for $($mapping.LdapAttr)" -ForegroundColor Green
                    }
                    catch {
                        Write-Host "    ✗ Failed to create expression mapping for $($mapping.LdapAttr): $_" -ForegroundColor Red
                        throw "Critical expression mapping failed (DN is required for AD provisioning). Setup cannot continue."
                    }
                }
            }
            else {
                Write-Host "    ✗ LDAP attribute '$($mapping.LdapAttr)' not found in schema" -ForegroundColor Red
                throw "Required LDAP attribute '$($mapping.LdapAttr)' not found. Cannot configure provisioning."
            }
        }
        if ($expressionMappingsCreated -gt 0) {
            Write-Host "  ✓ Expression-based mappings configured ($expressionMappingsCreated new)" -ForegroundColor Green
        }

        # Add object matching rule for CSV object type (how to match CSOs to existing MVOs during import)
        Write-Host "  Configuring object matching rule..." -ForegroundColor Gray

        $csvEmployeeIdAttr = $csvUserType.attributes | Where-Object { $_.name -eq 'employeeId' }
        $mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq 'Employee ID' }

        if ($csvEmployeeIdAttr -and $mvEmployeeIdAttr) {
            # Check if matching rule already exists
            $existingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id

            $matchingRuleExists = $existingMatchingRules | Where-Object {
                $_.targetMetaverseAttributeId -eq $mvEmployeeIdAttr.id -and
                ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $csvEmployeeIdAttr.id })
            }

            if (-not $matchingRuleExists) {
                New-JIMMatchingRule -ConnectedSystemId $csvSystem.id `
                    -ObjectTypeId $csvUserType.id `
                    -SourceAttributeId $csvEmployeeIdAttr.id `
                    -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
                Write-Host "  ✓ Object matching rule configured (employeeId → Employee ID)" -ForegroundColor Green
            }
            else {
                Write-Host "  Object matching rule already exists" -ForegroundColor Gray
            }
        }
        else {
            Write-Host "  ✗ Could not find required attributes for CSV matching rule" -ForegroundColor Red
            throw "Missing required attributes: employeeId (CSV) or Employee ID (Metaverse)"
        }

        # Add object matching rule for LDAP object type (how to match CSOs to existing MVOs during export)
        # This is important for joining to pre-existing AD accounts rather than provisioning duplicates
        Write-Host "  Configuring LDAP object matching rule..." -ForegroundColor Gray

        $ldapEmployeeIdAttr = $ldapUserType.attributes | Where-Object { $_.name -eq 'employeeID' }

        if (-not $ldapEmployeeIdAttr) {
            Write-Host "  ✗ LDAP 'employeeID' attribute not found in schema" -ForegroundColor Red
            throw "Required LDAP attribute 'employeeID' not found. Ensure the attribute is selected in the LDAP object type configuration."
        }
        if (-not $mvEmployeeIdAttr) {
            Write-Host "  ✗ Metaverse 'Employee ID' attribute not found" -ForegroundColor Red
            throw "Required Metaverse attribute 'Employee ID' not found. Setup cannot continue."
        }

        # Check if matching rule already exists
        $existingLdapMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id

        $ldapMatchingRuleExists = $existingLdapMatchingRules | Where-Object {
            $_.targetMetaverseAttributeId -eq $mvEmployeeIdAttr.id -and
            ($_.sources | Where-Object { $_.connectedSystemAttributeId -eq $ldapEmployeeIdAttr.id })
        }

        if (-not $ldapMatchingRuleExists) {
            New-JIMMatchingRule -ConnectedSystemId $ldapSystem.id `
                -ObjectTypeId $ldapUserType.id `
                -SourceAttributeId $ldapEmployeeIdAttr.id `
                -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
            Write-Host "  ✓ LDAP object matching rule configured (employeeID → Employee ID)" -ForegroundColor Green
        }
        else {
            Write-Host "  LDAP object matching rule already exists" -ForegroundColor Gray
        }

        # Configure Training attribute mappings and matching rule
        if ($trainingImportRule -and $trainingRecordType) {
            Write-Host "  Configuring Training attribute mappings..." -ForegroundColor Gray

            # First, create Training-specific MV attributes if they don't exist
            # These attributes are unique to Training data and need to be added to the User object type
            Write-Host "    Creating Training-specific Metaverse attributes..." -ForegroundColor DarkGray

            $trainingMvAttributes = @(
                @{ Name = "Training Status";        Type = "Text";    Plurality = "SingleValued" }  # Pass/Fail/InProgress
                @{ Name = "Courses Completed";      Type = "Text";    Plurality = "MultiValued" }   # List of course codes
                @{ Name = "Training Course Count";  Type = "Integer"; Plurality = "SingleValued" }  # Number of completed courses
            )

            foreach ($attrDef in $trainingMvAttributes) {
                $existingAttr = $mvAttributes | Where-Object { $_.name -eq $attrDef.Name }
                if (-not $existingAttr) {
                    try {
                        $newAttr = New-JIMMetaverseAttribute `
                            -Name $attrDef.Name `
                            -Type $attrDef.Type `
                            -AttributePlurality $attrDef.Plurality `
                            -ObjectTypeIds @($mvUserType.id)
                        Write-Host "      ✓ Created MV attribute: $($attrDef.Name)" -ForegroundColor Green
                        # Add to our local cache for mapping creation
                        $mvAttributes = @($mvAttributes) + $newAttr
                    }
                    catch {
                        Write-Host "      ✗ Failed to create MV attribute '$($attrDef.Name)': $_" -ForegroundColor Red
                        throw "Setup failed: Could not create required Training MV attribute '$($attrDef.Name)'"
                    }
                }
                else {
                    Write-Host "      MV attribute '$($attrDef.Name)' already exists" -ForegroundColor DarkGray
                }
            }

            # Training import mappings - contributes training-specific attributes to existing MVOs
            # These attributes are unique to Training and don't exist in HR data
            $trainingMappings = @(
                @{ CsAttr = "coursesCompleted";      MvAttr = "Courses Completed" }       # MVA: pipe-separated list of course codes
                @{ CsAttr = "trainingStatus";        MvAttr = "Training Status" }          # SVA: Pass/Fail/InProgress
                @{ CsAttr = "totalCoursesCompleted"; MvAttr = "Training Course Count" }    # SVA: number of completed courses
            )

            $existingTrainingMappings = Get-JIMSyncRuleMapping -SyncRuleId $trainingImportRule.id
            $trainingMappingsCreated = 0

            foreach ($mapping in $trainingMappings) {
                $csAttr = $trainingRecordType.attributes | Where-Object { $_.name -eq $mapping.CsAttr }
                $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

                if (-not $csAttr) {
                    Write-Host "    ✗ Connected System attribute '$($mapping.CsAttr)' not found in Training schema" -ForegroundColor Red
                    throw "Setup failed: Training CS attribute '$($mapping.CsAttr)' not found"
                }
                if (-not $mvAttr) {
                    Write-Host "    ✗ Metaverse attribute '$($mapping.MvAttr)' not found" -ForegroundColor Red
                    throw "Setup failed: Metaverse attribute '$($mapping.MvAttr)' not found - Training MV attributes must be created first"
                }

                $existsAlready = $existingTrainingMappings | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvAttr.id
                }

                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $trainingImportRule.id `
                            -TargetMetaverseAttributeId $mvAttr.id `
                            -SourceConnectedSystemAttributeId $csAttr.id | Out-Null
                        $trainingMappingsCreated++
                    }
                    catch {
                        Write-Host "    ✗ Failed to create Training mapping $($mapping.CsAttr) → $($mapping.MvAttr): $_" -ForegroundColor Red
                        throw "Setup failed: Could not create Training attribute mapping '$($mapping.CsAttr)' → '$($mapping.MvAttr)'"
                    }
                }
            }
            Write-Host "  ✓ Training attribute mappings configured ($trainingMappingsCreated new)" -ForegroundColor Green

            # Training matching rule - joins Training CSOs to existing MVOs via Employee ID
            Write-Host "  Configuring Training object matching rule..." -ForegroundColor Gray

            $trainingEmployeeIdAttr = $trainingRecordType.attributes | Where-Object { $_.name -eq 'employeeId' }
            if ($trainingEmployeeIdAttr -and $mvEmployeeIdAttr) {
                $existingTrainingMatchingRules = Get-JIMMatchingRule -ConnectedSystemId $trainingSystem.id -ObjectTypeId $trainingRecordType.id

                $trainingMatchingRuleExists = $existingTrainingMatchingRules | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvEmployeeIdAttr.id
                }

                if (-not $trainingMatchingRuleExists) {
                    New-JIMMatchingRule -ConnectedSystemId $trainingSystem.id `
                        -ObjectTypeId $trainingRecordType.id `
                        -SourceAttributeId $trainingEmployeeIdAttr.id `
                        -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
                    Write-Host "  ✓ Training object matching rule configured (employeeId → Employee ID)" -ForegroundColor Green
                }
                else {
                    Write-Host "  Training object matching rule already exists" -ForegroundColor Gray
                }
            }
        }

        # Configure Cross-Domain export attribute mappings
        if ($crossDomainExportRule -and $crossDomainUserType) {
            Write-Host "  Configuring Cross-Domain export attribute mappings..." -ForegroundColor Gray

            # Cross-Domain export mappings - export subset of user attributes to cross-domain CSV
            $crossDomainMappings = @(
                @{ MvAttr = "Account Name";   CsAttr = "samAccountName" }
                @{ MvAttr = "Display Name";   CsAttr = "displayName" }
                @{ MvAttr = "Email";          CsAttr = "email" }
                @{ MvAttr = "Department";     CsAttr = "department" }
                @{ MvAttr = "Employee ID";    CsAttr = "employeeId" }
                @{ MvAttr = "Company";        CsAttr = "company" }
            )

            $existingCrossDomainMappings = Get-JIMSyncRuleMapping -SyncRuleId $crossDomainExportRule.id
            $crossDomainMappingsCreated = 0

            foreach ($mapping in $crossDomainMappings) {
                $csAttr = $crossDomainUserType.attributes | Where-Object { $_.name -eq $mapping.CsAttr }
                $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MvAttr }

                if ($csAttr -and $mvAttr) {
                    $existsAlready = $existingCrossDomainMappings | Where-Object {
                        $_.targetConnectedSystemAttributeId -eq $csAttr.id
                    }

                    if (-not $existsAlready) {
                        try {
                            New-JIMSyncRuleMapping -SyncRuleId $crossDomainExportRule.id `
                                -TargetConnectedSystemAttributeId $csAttr.id `
                                -SourceMetaverseAttributeId $mvAttr.id | Out-Null
                            $crossDomainMappingsCreated++
                        }
                        catch {
                            Write-Host "    ⚠ Could not create Cross-Domain mapping $($mapping.MvAttr) → $($mapping.CsAttr): $_" -ForegroundColor Yellow
                        }
                    }
                }
            }

            # Add expression mapping for exportTimestamp
            $exportTimestampAttr = $crossDomainUserType.attributes | Where-Object { $_.name -eq 'exportTimestamp' }
            if ($exportTimestampAttr) {
                $existsAlready = $existingCrossDomainMappings | Where-Object {
                    $_.targetConnectedSystemAttributeId -eq $exportTimestampAttr.id
                }
                if (-not $existsAlready) {
                    try {
                        New-JIMSyncRuleMapping -SyncRuleId $crossDomainExportRule.id `
                            -TargetConnectedSystemAttributeId $exportTimestampAttr.id `
                            -Expression 'Now()' | Out-Null
                        $crossDomainMappingsCreated++
                    }
                    catch {
                        Write-Host "    ⚠ Could not create exportTimestamp expression mapping: $_" -ForegroundColor Yellow
                    }
                }
            }

            Write-Host "  ✓ Cross-Domain export attribute mappings configured ($crossDomainMappingsCreated new)" -ForegroundColor Green
        }

        # Restart jim.worker to pick up schema changes (API modifications may require reload)
        Write-Host "  Restarting JIM.Worker to reload schema..." -ForegroundColor Gray
        docker restart jim.worker > $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ JIM.Worker restarted" -ForegroundColor Green
            # Brief wait for worker to be ready
            Start-Sleep -Seconds 3
        }
        else {
            Write-Host "  ⚠ Failed to restart JIM.Worker" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "  ⚠ Sync rules not found, skipping attribute mappings" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ✗ Failed to configure attribute mappings: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - mappings can be configured manually if needed
}

# Step 6d: Configure Deletion Rules
Write-TestStep "Step 6d" "Configuring Deletion Rules"

try {
    # Get the Metaverse User object type
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

    if ($mvUserType) {
        # Configure deletion rules with a grace period
        # This allows the Reconnection test to work - when a user is removed from HR
        # and re-added within the grace period, their MVO is preserved
        Set-JIMMetaverseObjectType -Id $mvUserType.id `
            -DeletionRule WhenLastConnectorDisconnected `
            -DeletionGracePeriod ([TimeSpan]::FromDays(7)) | Out-Null

        Write-Host "  ✓ Deletion rule configured: WhenLastConnectorDisconnected with 7-day grace period" -ForegroundColor Green
    }
    else {
        Write-Host "  ⚠ Could not find User object type in Metaverse" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ⚠ Could not configure deletion rules: $_" -ForegroundColor Yellow
    Write-Host "    Reconnection test may fail without grace period configured" -ForegroundColor DarkYellow
}

# Step 7: Create Run Profiles
# Run profiles are scoped to Connected Systems, so we use simple names without prefixes
Write-TestStep "Step 7" "Creating Run Profiles"

try {
    # Get existing run profiles for each connected system
    $csvProfiles = Get-JIMRunProfile -ConnectedSystemId $csvSystem.id
    $ldapProfiles = Get-JIMRunProfile -ConnectedSystemId $ldapSystem.id

    # CSV Run Profiles
    $csvFilePath = "/var/connector-files/test-data/hr-users.csv"

    # Full Import (CSV)
    $csvImportProfile = $csvProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $csvImportProfile) {
        $csvImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $csvSystem.id `
            -RunType "FullImport" `
            -FilePath $csvFilePath `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile (CSV)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists (CSV)" -ForegroundColor Gray
    }

    # Full Synchronisation (CSV)
    $csvSyncProfile = $csvProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $csvSyncProfile) {
        $csvSyncProfile = New-JIMRunProfile `
            -Name "Full Synchronisation" `
            -ConnectedSystemId $csvSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Full Synchronisation' run profile (CSV)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Synchronisation' already exists (CSV)" -ForegroundColor Gray
    }

    # Delta Synchronisation (CSV)
    $csvDeltaSyncProfile = $csvProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
    if (-not $csvDeltaSyncProfile) {
        $csvDeltaSyncProfile = New-JIMRunProfile `
            -Name "Delta Synchronisation" `
            -ConnectedSystemId $csvSystem.id `
            -RunType "DeltaSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Delta Synchronisation' run profile (CSV)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Delta Synchronisation' already exists (CSV)" -ForegroundColor Gray
    }

    # LDAP Run Profiles

    # Full Import (LDAP) - MUST be run before any syncs to establish baseline
    $ldapFullImportProfile = $ldapProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $ldapFullImportProfile) {
        $ldapFullImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile (LDAP)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists (LDAP)" -ForegroundColor Gray
    }

    # Delta Import (LDAP) - for confirming exports
    $ldapDeltaImportProfile = $ldapProfiles | Where-Object { $_.name -eq "Delta Import" }
    if (-not $ldapDeltaImportProfile) {
        $ldapDeltaImportProfile = New-JIMRunProfile `
            -Name "Delta Import" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "DeltaImport" `
            -PassThru
        Write-Host "  ✓ Created 'Delta Import' run profile (LDAP)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Delta Import' already exists (LDAP)" -ForegroundColor Gray
    }

    # Full Synchronisation (LDAP) - for manual sync operations
    $ldapFullSyncProfile = $ldapProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $ldapFullSyncProfile) {
        $ldapFullSyncProfile = New-JIMRunProfile `
            -Name "Full Synchronisation" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Full Synchronisation' run profile (LDAP)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Synchronisation' already exists (LDAP)" -ForegroundColor Gray
    }

    # Delta Synchronisation (LDAP)
    $ldapDeltaSyncProfile = $ldapProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
    if (-not $ldapDeltaSyncProfile) {
        $ldapDeltaSyncProfile = New-JIMRunProfile `
            -Name "Delta Synchronisation" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "DeltaSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Delta Synchronisation' run profile (LDAP)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Delta Synchronisation' already exists (LDAP)" -ForegroundColor Gray
    }

    # Export (LDAP)
    $ldapExportProfile = $ldapProfiles | Where-Object { $_.name -eq "Export" }
    if (-not $ldapExportProfile) {
        $ldapExportProfile = New-JIMRunProfile `
            -Name "Export" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Export' run profile (LDAP)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Export' already exists (LDAP)" -ForegroundColor Gray
    }

    # Training Run Profiles
    $trainingProfiles = Get-JIMRunProfile -ConnectedSystemId $trainingSystem.id
    $trainingFilePath = "/var/connector-files/test-data/training-records.csv"

    # Full Import (Training)
    $trainingImportProfile = $trainingProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $trainingImportProfile) {
        $trainingImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $trainingSystem.id `
            -RunType "FullImport" `
            -FilePath $trainingFilePath `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile (Training)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists (Training)" -ForegroundColor Gray
    }

    # Full Synchronisation (Training)
    $trainingSyncProfile = $trainingProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $trainingSyncProfile) {
        $trainingSyncProfile = New-JIMRunProfile `
            -Name "Full Synchronisation" `
            -ConnectedSystemId $trainingSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Full Synchronisation' run profile (Training)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Synchronisation' already exists (Training)" -ForegroundColor Gray
    }

    # Delta Synchronisation (Training)
    $trainingDeltaSyncProfile = $trainingProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
    if (-not $trainingDeltaSyncProfile) {
        $trainingDeltaSyncProfile = New-JIMRunProfile `
            -Name "Delta Synchronisation" `
            -ConnectedSystemId $trainingSystem.id `
            -RunType "DeltaSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Delta Synchronisation' run profile (Training)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Delta Synchronisation' already exists (Training)" -ForegroundColor Gray
    }

    # Cross-Domain Run Profiles
    $crossDomainProfiles = Get-JIMRunProfile -ConnectedSystemId $crossDomainSystem.id
    $crossDomainFilePath = "/var/connector-files/test-data/cross-domain-users.csv"

    # Full Import (Cross-Domain) - for confirming exports
    $crossDomainImportProfile = $crossDomainProfiles | Where-Object { $_.name -eq "Full Import" }
    if (-not $crossDomainImportProfile) {
        $crossDomainImportProfile = New-JIMRunProfile `
            -Name "Full Import" `
            -ConnectedSystemId $crossDomainSystem.id `
            -RunType "FullImport" `
            -FilePath $crossDomainFilePath `
            -PassThru
        Write-Host "  ✓ Created 'Full Import' run profile (Cross-Domain)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Full Import' already exists (Cross-Domain)" -ForegroundColor Gray
    }

    # Note: CSV/File connectors do NOT support Delta Import (no change tracking like LDAP USN)
    # For confirming exports to CSV, use Full Import instead
    # Set the variable to point to Full Import for compatibility with schedule steps
    $crossDomainDeltaImportProfile = $crossDomainImportProfile
    Write-Host "  (Cross-Domain uses Full Import for confirming exports - no Delta Import for CSV)" -ForegroundColor DarkGray

    # Delta Synchronisation (Cross-Domain)
    $crossDomainDeltaSyncProfile = $crossDomainProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
    if (-not $crossDomainDeltaSyncProfile) {
        $crossDomainDeltaSyncProfile = New-JIMRunProfile `
            -Name "Delta Synchronisation" `
            -ConnectedSystemId $crossDomainSystem.id `
            -RunType "DeltaSynchronisation" `
            -PassThru
        Write-Host "  ✓ Created 'Delta Synchronisation' run profile (Cross-Domain)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Delta Synchronisation' already exists (Cross-Domain)" -ForegroundColor Gray
    }

    # Export (Cross-Domain)
    $crossDomainExportProfile = $crossDomainProfiles | Where-Object { $_.name -eq "Export" }
    if (-not $crossDomainExportProfile) {
        $crossDomainExportProfile = New-JIMRunProfile `
            -Name "Export" `
            -ConnectedSystemId $crossDomainSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Export' run profile (Cross-Domain)" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Export' already exists (Cross-Domain)" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create run profiles: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - run profiles might need manual configuration
}

# Summary
Write-TestSection "Setup Complete"
Write-Host "Template:              $Template" -ForegroundColor Cyan
Write-Host ""
Write-Host "Connected Systems:" -ForegroundColor Cyan
Write-Host "  HR CSV (Source):       $($csvSystem.id)" -ForegroundColor Gray
Write-Host "  Training CSV (Source): $($trainingSystem.id)" -ForegroundColor Gray
Write-Host "  Samba AD (Target):     $($ldapSystem.id)" -ForegroundColor Gray
Write-Host "  Cross-Domain (Target): $($crossDomainSystem.id)" -ForegroundColor Gray
Write-Host ""
Write-Host "✓ Scenario 1 setup complete (4 connected systems)" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run: ./scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template $Template" -ForegroundColor Gray
Write-Host "  2. Or manually trigger run profiles via JIM UI" -ForegroundColor Gray
Write-Host ""

# Return configuration for use by scenario scripts
return @{
    # HR CSV (primary source)
    CSVSystemId = $csvSystem.id
    CSVImportProfileId = $csvImportProfile.id
    CSVSyncProfileId = $csvSyncProfile.id
    CSVDeltaSyncProfileId = $csvDeltaSyncProfile.id

    # Training CSV (secondary source)
    TrainingSystemId = $trainingSystem.id
    TrainingImportProfileId = $trainingImportProfile.id
    TrainingSyncProfileId = $trainingSyncProfile.id
    TrainingDeltaSyncProfileId = $trainingDeltaSyncProfile.id

    # Samba AD (primary target)
    LDAPSystemId = $ldapSystem.id
    LDAPFullImportProfileId = $ldapFullImportProfile.id
    LDAPDeltaImportProfileId = $ldapDeltaImportProfile.id
    LDAPFullSyncProfileId = $ldapFullSyncProfile.id
    LDAPDeltaSyncProfileId = $ldapDeltaSyncProfile.id
    LDAPExportProfileId = $ldapExportProfile.id

    # Cross-Domain CSV (secondary target)
    CrossDomainSystemId = $crossDomainSystem.id
    CrossDomainImportProfileId = $crossDomainImportProfile.id
    CrossDomainDeltaImportProfileId = $crossDomainDeltaImportProfile.id
    CrossDomainDeltaSyncProfileId = $crossDomainDeltaSyncProfile.id
    CrossDomainExportProfileId = $crossDomainExportProfile.id
}
