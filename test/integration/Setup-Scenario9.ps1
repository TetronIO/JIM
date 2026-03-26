<#
.SYNOPSIS
    Configure JIM for Scenario 9: Partition-Scoped Imports

.DESCRIPTION
    Sets up a single LDAP Connected System to test partition-scoped import run profiles.
    Creates:
    - LDAP Connected System pointing to the configured directory (Samba AD or OpenLDAP)
    - Two Full Import run profiles: one scoped to a specific partition, one unscoped
    - A Full Sync run profile (no partition, as sync is partition-agnostic)
    - A simple sync rule to project imported users to the Metaverse

    For OpenLDAP, both partitions (Yellowstone + Glitterband) are selected so that
    scoped vs unscoped imports produce different results — proving true partition filtering.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template (not used by this scenario - fixed test data for SambaAD)

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig

.EXAMPLE
    ./Setup-Scenario9.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario9.ps1 -ApiKey "jim_abc123..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType OpenLDAP)
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
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
$systemName = if ($isOpenLDAP) { "Partition Test OpenLDAP" } else { "Partition Test AD" }

Write-TestSection "Scenario 9 Setup: Partition-Scoped Imports ($systemName)"

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
$partitionTestSystem = $existingSystems | Where-Object { $_.name -eq $systemName }

if ($partitionTestSystem) {
    Write-Host "  Removing existing '$systemName' Connected System..." -ForegroundColor Gray
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
Write-TestStep "Step 4" "Creating LDAP Connected System ($systemName)"

try {
    $ldapSystem = New-JIMConnectedSystem `
        -Name $systemName `
        -Description "LDAP directory for partition-scoped import testing ($($DirectoryConfig.Host))" `
        -ConnectorDefinitionId $ldapConnector.id `
        -PassThru

    Write-Host "  OK Created LDAP Connected System (ID: $($ldapSystem.id))" -ForegroundColor Green

    # Configure LDAP settings from DirectoryConfig
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
    if ($hostSetting) { $ldapSettings[$hostSetting.id] = @{ stringValue = $DirectoryConfig.Host } }
    if ($portSetting) { $ldapSettings[$portSetting.id] = @{ intValue = $DirectoryConfig.Port } }
    if ($usernameSetting) { $ldapSettings[$usernameSetting.id] = @{ stringValue = $DirectoryConfig.BindDN } }
    if ($passwordSetting) { $ldapSettings[$passwordSetting.id] = @{ stringValue = $DirectoryConfig.BindPassword } }
    if ($useSSLSetting) { $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $DirectoryConfig.UseSSL } }
    if ($certValidationSetting -and $DirectoryConfig.CertValidation) {
        $ldapSettings[$certValidationSetting.id] = @{ stringValue = $DirectoryConfig.CertValidation }
    }
    if ($connectionTimeoutSetting) { $ldapSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
    if ($authTypeSetting) { $ldapSettings[$authTypeSetting.id] = @{ stringValue = $DirectoryConfig.AuthType } }

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

    # Get object types and find the user type (varies by directory)
    $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
    $userObjectClassName = $DirectoryConfig.UserObjectClass
    $userObjectType = $ldapObjectTypes | Where-Object { $_.name -eq $userObjectClassName }

    if ($userObjectType) {
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -Selected $true | Out-Null
        Write-Host "  OK Selected '$userObjectClassName' object type" -ForegroundColor Green

        # Select key attributes — varies by directory type
        $requiredAttributes = if ($isOpenLDAP) {
            @("uid", "givenName", "sn", "displayName", "mail", "departmentNumber", "employeeNumber", "distinguishedName", "cn")
        } else {
            @("sAMAccountName", "givenName", "sn", "displayName", "mail", "department", "employeeID", "distinguishedName", "userAccountControl")
        }

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
        throw "Could not find '$userObjectClassName' object type in schema"
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

    # Find the primary domain partition
    $primaryBaseDN = $DirectoryConfig.BaseDN
    $domainPartition = $partitions | Where-Object {
        $_.name -eq $primaryBaseDN -or $_.externalId -eq $primaryBaseDN
    } | Select-Object -First 1

    if (-not $domainPartition -and $partitions.Count -eq 1) {
        $domainPartition = $partitions[0]
        Write-Host "  Using single available partition: $($domainPartition.name)" -ForegroundColor Yellow
    }

    if (-not $domainPartition) {
        throw "Could not find primary partition $primaryBaseDN"
    }

    # Select the primary partition
    Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $domainPartition.id -Selected $true | Out-Null
    Write-Host "  OK Selected primary partition: $($domainPartition.name) (ID: $($domainPartition.id))" -ForegroundColor Green

    # For OpenLDAP: also select the second partition (Glitterband) — essential for multi-partition testing
    $secondPartition = $null
    if ($isOpenLDAP -and $DirectoryConfig.SecondSuffix) {
        $secondSuffix = $DirectoryConfig.SecondSuffix
        $secondPartition = $partitions | Where-Object {
            $_.name -eq $secondSuffix -or $_.externalId -eq $secondSuffix
        } | Select-Object -First 1

        if ($secondPartition) {
            Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $secondPartition.id -Selected $true | Out-Null
            Write-Host "  OK Selected second partition: $($secondPartition.name) (ID: $($secondPartition.id))" -ForegroundColor Green
        }
        else {
            Write-Host "  WARNING: Second partition '$secondSuffix' not found — multi-partition assertions will be limited" -ForegroundColor Yellow
        }
    }

    # Select containers within partitions
    # Helper function to search containers recursively
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

    if ($isOpenLDAP) {
        # For OpenLDAP: select People container in each partition
        $userContainerDN = $DirectoryConfig.UserContainer
        $targetContainerName = if ($userContainerDN -match "^[Oo][Uu]=([^,]+)") { $matches[1] } else { "People" }

        foreach ($part in @($domainPartition, $secondPartition)) {
            if (-not $part) { continue }
            $container = Find-Container -Containers $part.containers -Name $targetContainerName
            if (-not $container) {
                # Try matching by full DN
                foreach ($c in $part.containers) {
                    if ($c.name -match $targetContainerName) { $container = $c; break }
                }
            }
            if ($container) {
                Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $container.id -Selected $true | Out-Null
                Write-Host "  OK Selected container '$targetContainerName' in $($part.name)" -ForegroundColor Green
            }
            else {
                Write-Host "  WARNING: '$targetContainerName' container not found in $($part.name)" -ForegroundColor Yellow
            }
        }
    }
    else {
        # For Samba AD: select TestUsers container in the domain partition
        $testUsersContainer = Find-Container -Containers $domainPartition.containers -Name "TestUsers"
        if ($testUsersContainer) {
            Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $testUsersContainer.id -Selected $true | Out-Null
            Write-Host "  OK Selected container: TestUsers (ID: $($testUsersContainer.id))" -ForegroundColor Green
        }
        else {
            Write-Host "  WARNING: TestUsers container not found - will import from entire partition" -ForegroundColor Yellow
        }
    }

    # Export partition IDs for use by test script
    $script:DomainPartitionId = $domainPartition.id
    $script:SecondPartitionId = if ($secondPartition) { $secondPartition.id } else { $null }
}
catch {
    Write-Host "  FAIL Failed to import/configure hierarchy: $_" -ForegroundColor Red
    throw
}

# Step 7: Get Metaverse Object Type and Attributes
Write-TestStep "Step 7" "Getting Metaverse schema"

try {
    # Use the seed "User" metaverse object type (same as other scenarios)
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

    if (-not $mvUserType) {
        throw "No 'User' metaverse object type found in seed data"
    }

    Write-Host "  OK Found 'User' Metaverse Object Type (ID: $($mvUserType.id))" -ForegroundColor Green

    # Get existing metaverse attributes
    $mvAttributes = @(Get-JIMMetaverseAttribute)
    Write-Host "  OK Found $($mvAttributes.Count) metaverse attributes" -ForegroundColor Green
}
catch {
    Write-Host "  FAIL Failed to get Metaverse schema: $_" -ForegroundColor Red
    throw
}

# Step 8: Create Sync Rule
Write-TestStep "Step 8" "Creating sync rule"

try {
    $syncRuleName = if ($isOpenLDAP) { "Partition Test - OpenLDAP Import Users" } else { "Partition Test - AD Import Users" }
    $existingRules = @(Get-JIMSyncRule)
    $importRule = $existingRules | Where-Object { $_.name -eq $syncRuleName }

    if (-not $importRule) {
        # Get object types for sync rule creation
        $mvAttributes = @(Get-JIMMetaverseAttribute)
        $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
        $userObjectType = $ldapObjectTypes | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass }

        $importRule = New-JIMSyncRule `
            -Name $syncRuleName `
            -ConnectedSystemId $ldapSystem.id `
            -ConnectedSystemObjectTypeId $userObjectType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Import `
            -ProjectToMetaverse `
            -PassThru

        Write-Host "  OK Created sync rule (ID: $($importRule.id))" -ForegroundColor Green

        # Add attribute mappings — varies by directory type
        # OpenLDAP: many standard attributes (uid, givenName, sn, mail, departmentNumber) are
        # multi-valued per RFC 4512/4519 — they lack the SINGLE-VALUE keyword. JIM correctly
        # rejects multi-valued→single-valued import flows. Only map attributes that are
        # explicitly SINGLE-VALUE in the OpenLDAP schema (displayName, employeeNumber).
        # This is sufficient for the partition filtering test — we just need projections.
        $mappings = if ($isOpenLDAP) {
            @(
                @{ CSAttr = "displayName"; MVAttr = "Display Name" },
                @{ CSAttr = "employeeNumber"; MVAttr = "Employee ID" }
            )
        } else {
            @(
                @{ CSAttr = "sAMAccountName"; MVAttr = "Account Name" },
                @{ CSAttr = "givenName"; MVAttr = "First Name" },
                @{ CSAttr = "sn"; MVAttr = "Last Name" },
                @{ CSAttr = "displayName"; MVAttr = "Display Name" },
                @{ CSAttr = "department"; MVAttr = "Department" },
                @{ CSAttr = "employeeID"; MVAttr = "Employee ID" }
            )
        }

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

    # 1. Full Import - scoped to primary partition
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

    # 4. For OpenLDAP: also create a scoped import for the second partition
    if ($isOpenLDAP -and $script:SecondPartitionId) {
        $scopedImport2Profile = $existingProfiles | Where-Object { $_.name -eq "Full Import (Scoped - Second)" }
        if (-not $scopedImport2Profile) {
            $scopedImport2Profile = New-JIMRunProfile `
                -Name "Full Import (Scoped - Second)" `
                -ConnectedSystemId $ldapSystem.id `
                -RunType "FullImport" `
                -PartitionId $script:SecondPartitionId `
                -PassThru
            Write-Host "  OK Created 'Full Import (Scoped - Second)' with PartitionId $($script:SecondPartitionId)" -ForegroundColor Green
        }
        else {
            Write-Host "  'Full Import (Scoped - Second)' already exists" -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host "  FAIL Failed to create run profiles: $_" -ForegroundColor Red
    throw
}

Write-TestSection "Scenario 9 Setup Complete"
Write-Host "  Connected System: $systemName (ID: $($ldapSystem.id))" -ForegroundColor Cyan
Write-Host "  Primary Partition ID: $($script:DomainPartitionId)" -ForegroundColor Cyan
if ($script:SecondPartitionId) {
    Write-Host "  Second Partition ID: $($script:SecondPartitionId)" -ForegroundColor Cyan
}
Write-Host "  Run Profiles:" -ForegroundColor Cyan
Write-Host "    - Full Import (Scoped)   - targets primary partition $($script:DomainPartitionId)" -ForegroundColor Cyan
Write-Host "    - Full Import (Unscoped) - all selected partitions" -ForegroundColor Cyan
Write-Host "    - Full Synchronisation   - partition-agnostic" -ForegroundColor Cyan
if ($isOpenLDAP -and $script:SecondPartitionId) {
    Write-Host "    - Full Import (Scoped - Second) - targets second partition $($script:SecondPartitionId)" -ForegroundColor Cyan
}
