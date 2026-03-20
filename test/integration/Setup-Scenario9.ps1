<#
.SYNOPSIS
    Configure JIM for Scenario 9: Partition-Scoped Imports

.DESCRIPTION
    Sets up a single LDAP Connected System against Samba AD Primary to test partition-scoped
    import run profiles. Creates:
    - LDAP Connected System pointing to samba-ad-primary
    - Two Full Import run profiles: one scoped to a specific partition, one unscoped
    - A Full Sync run profile (no partition, as sync is partition-agnostic)
    - A simple sync rule to project imported users to the Metaverse

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template (not used by this scenario - fixed test data)

.EXAMPLE
    ./Setup-Scenario9.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 9 Setup: Partition-Scoped Imports"

# Step 1: Import JIM PowerShell module
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  OK JIM PowerShell module imported" -ForegroundColor Green

# Step 2: Connect to JIM
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

# Step 2b: Clean up existing configuration from previous runs
Write-TestStep "Step 2b" "Cleaning up existing configuration"

$existingSystems = @(Get-JIMConnectedSystem)
$partitionTestSystem = $existingSystems | Where-Object { $_.name -eq "Partition Test AD" }

if ($partitionTestSystem) {
    Write-Host "  Removing existing 'Partition Test AD' Connected System..." -ForegroundColor Gray
    Remove-JIMConnectedSystem -Id $partitionTestSystem.id | Out-Null
    Write-Host "  OK Removed existing Connected System" -ForegroundColor Green
}

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Getting LDAP connector definition"

$connectors = Get-JIMConnectorDefinition
$ldapConnector = $connectors | Where-Object { $_.name -eq "JIM LDAP Connector" }

if (-not $ldapConnector) {
    throw "LDAP connector not found"
}

Write-Host "  OK Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green

# Step 4: Create LDAP Connected System
Write-TestStep "Step 4" "Creating LDAP Connected System"

try {
    $ldapSystem = New-JIMConnectedSystem `
        -Name "Partition Test AD" `
        -Description "Samba AD for partition-scoped import testing" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru

    Write-Host "  OK Created LDAP Connected System (ID: $($ldapSystem.id))" -ForegroundColor Green

    # Configure LDAP settings
    $ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

    $hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
    $portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
    $usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
    $passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
    $useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
    $certValidationSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Certificate Validation" }
    $connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
    $authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }

    $ldapSettings = @{}
    if ($hostSetting) { $ldapSettings[$hostSetting.id] = @{ stringValue = "samba-ad-primary" } }
    if ($portSetting) { $ldapSettings[$portSetting.id] = @{ intValue = 636 } }
    if ($usernameSetting) { $ldapSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=subatomic,DC=local" } }
    if ($passwordSetting) { $ldapSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" } }
    if ($useSSLSetting) { $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $true } }
    if ($certValidationSetting) { $ldapSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" } }
    if ($connectionTimeoutSetting) { $ldapSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
    if ($authTypeSetting) { $ldapSettings[$authTypeSetting.id] = @{ stringValue = "Simple" } }

    if ($ldapSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $ldapSettings | Out-Null
        Write-Host "  OK Configured LDAP settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  FAIL Failed to create/configure LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5: Import schema
Write-TestStep "Step 5" "Importing LDAP schema"

try {
    Import-JIMConnectedSystemSchema -Id $ldapSystem.id | Out-Null
    Write-Host "  OK Schema imported" -ForegroundColor Green

    # Get object types and find 'user'
    $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
    $userObjectType = $ldapObjectTypes | Where-Object { $_.name -eq "user" }

    if ($userObjectType) {
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -Selected $true | Out-Null
        Write-Host "  OK Selected 'user' object type" -ForegroundColor Green

        # Select key attributes using the bulk update pattern from Scenario 1
        $requiredAttributes = @("sAMAccountName", "givenName", "sn", "displayName", "mail", "department", "employeeID", "distinguishedName", "userAccountControl")
        $attrUpdates = @{}
        foreach ($attr in $userObjectType.attributes) {
            if ($attr.name -in $requiredAttributes) {
                $attrUpdates[$attr.id] = @{ selected = $true }
            }
        }
        $attrResult = Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -AttributeUpdates $attrUpdates -PassThru -ErrorAction Stop
        Write-Host "  OK Selected $($attrResult.updatedCount) attributes" -ForegroundColor Green
    }
    else {
        throw "Could not find 'user' object type in schema"
    }
}
catch {
    Write-Host "  FAIL Failed to import/configure schema: $_" -ForegroundColor Red
    throw
}

# Step 6: Import hierarchy and select partition/containers
Write-TestStep "Step 6" "Importing LDAP hierarchy and selecting partition/containers"

try {
    Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id | Out-Null

    $partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id)

    if ($partitions.Count -eq 0) {
        throw "No partitions found after hierarchy import"
    }

    Write-Host "  Found $($partitions.Count) partition(s):" -ForegroundColor Gray
    foreach ($p in $partitions) {
        Write-Host "    - Name: '$($p.name)', ExternalId: '$($p.externalId)', Selected: $($p.selected)" -ForegroundColor Gray
    }

    # Find the main domain partition
    $domainPartition = $partitions | Where-Object {
        $_.name -eq "DC=subatomic,DC=local" -or $_.externalId -eq "DC=subatomic,DC=local"
    } | Select-Object -First 1

    if (-not $domainPartition -and $partitions.Count -eq 1) {
        $domainPartition = $partitions[0]
        Write-Host "  Using single available partition: $($domainPartition.name)" -ForegroundColor Yellow
    }

    if (-not $domainPartition) {
        throw "Could not find domain partition DC=subatomic,DC=local"
    }

    # Select the domain partition
    Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $domainPartition.id -Selected $true | Out-Null
    Write-Host "  OK Selected partition: $($domainPartition.name) (ID: $($domainPartition.id))" -ForegroundColor Green

    # Find and select the TestUsers container
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

    $testUsersContainer = Find-Container -Containers $domainPartition.containers -Name "TestUsers"
    if ($testUsersContainer) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $testUsersContainer.id -Selected $true | Out-Null
        Write-Host "  OK Selected container: TestUsers (ID: $($testUsersContainer.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  WARNING: TestUsers container not found - will import from entire partition" -ForegroundColor Yellow
    }

    # Export partition ID for use by test script
    $script:DomainPartitionId = $domainPartition.id
}
catch {
    Write-Host "  FAIL Failed to import/configure hierarchy: $_" -ForegroundColor Red
    throw
}

# Step 7: Create Metaverse Object Type and Attributes
Write-TestStep "Step 7" "Creating Metaverse schema"

try {
    # Check if Person type already exists
    $mvObjectTypes = @(Get-JIMMetaverseObjectType)
    $personType = $mvObjectTypes | Where-Object { $_.name -eq "Person" }

    if (-not $personType) {
        $personType = New-JIMMetaverseObjectType -Name "Person" -PassThru
        Write-Host "  OK Created 'Person' Metaverse Object Type" -ForegroundColor Green
    }
    else {
        Write-Host "  Person type already exists (ID: $($personType.id))" -ForegroundColor Yellow
    }

    # Create attributes if they don't exist
    $mvAttributes = @(Get-JIMMetaverseAttribute)
    $requiredAttrs = @(
        @{ Name = "Account Name"; Type = "Text" },
        @{ Name = "First Name"; Type = "Text" },
        @{ Name = "Last Name"; Type = "Text" },
        @{ Name = "Display Name"; Type = "Text" },
        @{ Name = "Department"; Type = "Text" },
        @{ Name = "Employee ID"; Type = "Text" }
    )

    foreach ($attrDef in $requiredAttrs) {
        $existing = $mvAttributes | Where-Object { $_.name -eq $attrDef.Name }
        if (-not $existing) {
            New-JIMMetaverseAttribute -Name $attrDef.Name -Type $attrDef.Type -ObjectTypeId $personType.id | Out-Null
            Write-Host "  OK Created attribute: $($attrDef.Name)" -ForegroundColor Green
        }
        else {
            # Ensure attribute is linked to Person type
            Write-Host "  Attribute '$($attrDef.Name)' already exists" -ForegroundColor Gray
        }
    }
}
catch {
    Write-Host "  FAIL Failed to create Metaverse schema: $_" -ForegroundColor Red
    throw
}

# Step 8: Create Sync Rule
Write-TestStep "Step 8" "Creating sync rule"

try {
    $existingRules = @(Get-JIMSyncRule)
    $importRule = $existingRules | Where-Object { $_.name -eq "Partition Test - AD Import Users" }

    if (-not $importRule) {
        # Get object types for sync rule creation
        $mvAttributes = @(Get-JIMMetaverseAttribute)
        $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
        $userObjectType = $ldapObjectTypes | Where-Object { $_.name -eq "user" }

        $importRule = New-JIMSyncRule `
            -Name "Partition Test - AD Import Users" `
            -ConnectedSystemId $ldapSystem.id `
            -ConnectedSystemObjectTypeId $userObjectType.id `
            -MetaverseObjectTypeId $personType.id `
            -Direction Import `
            -ProjectToMetaverse `
            -PassThru

        Write-Host "  OK Created sync rule (ID: $($importRule.id))" -ForegroundColor Green

        # Add attribute mappings
        $mappings = @(
            @{ CSAttr = "sAMAccountName"; MVAttr = "Account Name" },
            @{ CSAttr = "givenName"; MVAttr = "First Name" },
            @{ CSAttr = "sn"; MVAttr = "Last Name" },
            @{ CSAttr = "displayName"; MVAttr = "Display Name" },
            @{ CSAttr = "department"; MVAttr = "Department" },
            @{ CSAttr = "employeeID"; MVAttr = "Employee ID" }
        )

        $mappingsCreated = 0
        foreach ($mapping in $mappings) {
            $csAttr = $userObjectType.attributes | Where-Object { $_.name -eq $mapping.CSAttr }
            $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MVAttr }

            if ($csAttr -and $mvAttr) {
                New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
                    -TargetMetaverseAttributeId $mvAttr.id `
                    -SourceConnectedSystemAttributeId $csAttr.id | Out-Null
                $mappingsCreated++
            }
            else {
                Write-Host "  WARNING: Could not map $($mapping.CSAttr) -> $($mapping.MVAttr)" -ForegroundColor Yellow
            }
        }

        Write-Host "  OK Created $mappingsCreated attribute flow mappings" -ForegroundColor Green
    }
    else {
        Write-Host "  Sync rule already exists (ID: $($importRule.id))" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  FAIL Failed to create sync rule: $_" -ForegroundColor Red
    throw
}

# Step 9: Create Run Profiles
Write-TestStep "Step 9" "Creating run profiles"

try {
    $existingProfiles = @(Get-JIMRunProfile -ConnectedSystemId $ldapSystem.id)

    # 1. Full Import - scoped to domain partition
    $scopedImportProfile = $existingProfiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    if (-not $scopedImportProfile) {
        $scopedImportProfile = New-JIMRunProfile `
            -Name "Full Import (Scoped)" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "FullImport" `
            -PartitionId $script:DomainPartitionId `
            -PassThru
        Write-Host "  OK Created 'Full Import (Scoped)' with PartitionId $($script:DomainPartitionId)" -ForegroundColor Green
    }
    else {
        Write-Host "  'Full Import (Scoped)' already exists" -ForegroundColor Yellow
    }

    # 2. Full Import - unscoped (all selected partitions)
    $unscopedImportProfile = $existingProfiles | Where-Object { $_.name -eq "Full Import (Unscoped)" }
    if (-not $unscopedImportProfile) {
        $unscopedImportProfile = New-JIMRunProfile `
            -Name "Full Import (Unscoped)" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "FullImport" `
            -PassThru
        Write-Host "  OK Created 'Full Import (Unscoped)' without PartitionId" -ForegroundColor Green
    }
    else {
        Write-Host "  'Full Import (Unscoped)' already exists" -ForegroundColor Yellow
    }

    # 3. Full Synchronisation (no partition - sync is partition-agnostic)
    $syncProfile = $existingProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    if (-not $syncProfile) {
        $syncProfile = New-JIMRunProfile `
            -Name "Full Synchronisation" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "FullSynchronisation" `
            -PassThru
        Write-Host "  OK Created 'Full Synchronisation'" -ForegroundColor Green
    }
    else {
        Write-Host "  'Full Synchronisation' already exists" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  FAIL Failed to create run profiles: $_" -ForegroundColor Red
    throw
}

Write-TestSection "Scenario 9 Setup Complete"
Write-Host "  Connected System: Partition Test AD (ID: $($ldapSystem.id))" -ForegroundColor Cyan
Write-Host "  Domain Partition ID: $($script:DomainPartitionId)" -ForegroundColor Cyan
Write-Host "  Run Profiles:" -ForegroundColor Cyan
Write-Host "    - Full Import (Scoped)   - targets partition $($script:DomainPartitionId)" -ForegroundColor Cyan
Write-Host "    - Full Import (Unscoped) - all selected partitions" -ForegroundColor Cyan
Write-Host "    - Full Synchronisation   - partition-agnostic" -ForegroundColor Cyan
