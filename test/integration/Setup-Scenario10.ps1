# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 10: Sync Rule Scoping Behaviour

.DESCRIPTION
    Sets up the connected systems, sync rules, and scoping criteria needed to test
    the full sync rule scoping transition matrix end-to-end (issue #656).

    Creates:
    - HR CSV Connected System (inbound source - department-aware test users)
    - LDAP Connected System (outbound target - Samba AD or OpenLDAP)
    - Metaverse object type 'User' bindings
    - Import sync rule (HR -> MV) scoped to department = 'Finance', InboundOutOfScopeAction = Disconnect
    - Export sync rule (MV -> LDAP) scoped to Department = 'Finance', OutboundDeprovisionAction = Disconnect
    - Full Import / Full Synchronisation / Export run profiles for both systems

    The Invoke-Scenario10 script reconfigures the rules' scope actions between sub-tests
    to exercise every combination (Disconnect / RemainJoined for inbound,
    Disconnect / Delete for outbound).

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template. Scoping behaviour is template-independent; Nano is sufficient.

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig.

.EXAMPLE
    ./Setup-Scenario10.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario10.ps1 -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType OpenLDAP)
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

. "$PSScriptRoot/utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"
$hrSystemName = "Scoping HR Source"
$ldapSystemName = if ($isOpenLDAP) { "Scoping LDAP Target (OpenLDAP)" } else { "Scoping LDAP Target (AD)" }
$importRuleName = "Scoping Import (HR -> MV)"
$exportRuleName = "Scoping Export (MV -> LDAP)"

Write-TestSection "Scenario 10 Setup: Sync Rule Scoping Behaviour"

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

Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

# Step 2b: Clean up any leftover scoping connected systems from previous runs
Write-TestStep "Step 2b" "Cleaning up existing scoping configuration"

$existing = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue)
foreach ($name in @($hrSystemName, $ldapSystemName)) {
    $sys = $existing | Where-Object { $_.name -eq $name }
    if ($sys) {
        Write-Host "  Removing existing '$name'..." -ForegroundColor Gray
        Remove-JIMConnectedSystem -Id $sys.id -Force | Out-Null
    }
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Resolving connector definitions"

$connectors = Get-JIMConnectorDefinition
$csvConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
$ldapConnector = $connectors | Where-Object { $_.name -eq "JIM LDAP Connector" }

if (-not $csvConnector) { throw "JIM File Connector definition not found" }
if (-not $ldapConnector) { throw "JIM LDAP Connector definition not found" }

Write-Host "  OK File connector (ID: $($csvConnector.id))" -ForegroundColor Green
Write-Host "  OK LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green

# Step 4: Create HR CSV Connected System
Write-TestStep "Step 4" "Creating HR CSV Connected System ($hrSystemName)"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "HR source for sync rule scoping integration tests" `
    -ConnectorDefinitionId $csvConnector.id `
    -PassThru

$csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
$filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "File Path" }
$delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
$objectTypeSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Object Type" }

$hrSettings = @{}
if ($filePathSetting) { $hrSettings[$filePathSetting.id] = @{ stringValue = "/connector-files/test-data/hr-users.csv" } }
if ($delimiterSetting) { $hrSettings[$delimiterSetting.id] = @{ stringValue = "," } }
if ($objectTypeSetting) { $hrSettings[$objectTypeSetting.id] = @{ stringValue = "person" } }

Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues $hrSettings | Out-Null
Write-Host "  OK HR CSV configured (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 5: Create LDAP Connected System
Write-TestStep "Step 5" "Creating LDAP Connected System ($ldapSystemName)"

$ldapSystem = New-JIMConnectedSystem `
    -Name $ldapSystemName `
    -Description "LDAP target for sync rule scoping integration tests ($($DirectoryConfig.Host))" `
    -ConnectorDefinitionId $ldapConnector.id `
    -PassThru

$ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id
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
if ($hostSetting) { $ldapSettings[$hostSetting.id] = @{ stringValue = $DirectoryConfig.Host } }
if ($portSetting) { $ldapSettings[$portSetting.id] = @{ intValue = $DirectoryConfig.Port } }
if ($usernameSetting) { $ldapSettings[$usernameSetting.id] = @{ stringValue = $DirectoryConfig.BindDN } }
if ($passwordSetting) { $ldapSettings[$passwordSetting.id] = @{ stringValue = $DirectoryConfig.BindPassword } }
if ($useSSLSetting) { $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $DirectoryConfig.UseSSL } }
if ($certValidationSetting -and $DirectoryConfig.CertValidation) { $ldapSettings[$certValidationSetting.id] = @{ stringValue = $DirectoryConfig.CertValidation } }
if ($connectionTimeoutSetting) { $ldapSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 } }
if ($authTypeSetting) { $ldapSettings[$authTypeSetting.id] = @{ stringValue = $DirectoryConfig.AuthType } }
if ($createContainersSetting) { $ldapSettings[$createContainersSetting.id] = @{ checkboxValue = $true } }

Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $ldapSettings | Out-Null

if ($ExportConcurrency -gt 1) {
    $ec = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Export Concurrency" }
    if ($ec) {
        Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues @{ $ec.id = @{ intValue = $ExportConcurrency } } | Out-Null
    }
}
if ($MaxExportParallelism -gt 1) {
    Set-JIMConnectedSystem -Id $ldapSystem.id -MaxExportParallelism $MaxExportParallelism | Out-Null
}

Write-Host "  OK LDAP configured (ID: $($ldapSystem.id))" -ForegroundColor Green

# Step 6: Import schemas
Write-TestStep "Step 6" "Importing schemas"

Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
if (-not $hrPersonType) { throw "HR 'person' object type not found in schema" }
Write-Host "  OK HR schema imported ('person' object type)" -ForegroundColor Green

Import-JIMConnectedSystemSchema -Id $ldapSystem.id | Out-Null
$ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
$ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass }
if (-not $ldapUserType) { throw "LDAP user object type '$($DirectoryConfig.UserObjectClass)' not found in schema" }
Write-Host "  OK LDAP schema imported ('$($DirectoryConfig.UserObjectClass)' object type)" -ForegroundColor Green

# Step 7: Select object types and attributes
Write-TestStep "Step 7" "Selecting object types and attributes"

Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -Selected $true | Out-Null

$hrAttrs = @("employeeId", "firstName", "lastName", "email", "department", "title", "company", "samAccountName", "displayName", "status")
$hrAttrUpdates = @{}
foreach ($attr in $hrPersonType.attributes) {
    if ($attr.name -in $hrAttrs) {
        $isExternalId = ($attr.name -eq "employeeId")
        $hrAttrUpdates[$attr.id] = @{ selected = $true; isExternalId = $isExternalId }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -AttributeUpdates $hrAttrUpdates | Out-Null
Write-Host "  OK Selected $($hrAttrUpdates.Count) HR attributes (employeeId is external ID)" -ForegroundColor Green

Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -Selected $true | Out-Null

$ldapAttrs = if ($isOpenLDAP) {
    @("uid", "givenName", "sn", "displayName", "mail", "departmentNumber", "employeeNumber", "distinguishedName", "cn")
} else {
    @("sAMAccountName", "givenName", "sn", "displayName", "mail", "department", "employeeID", "distinguishedName", "userAccountControl")
}

$ldapAttrUpdates = @{}
foreach ($attr in $ldapUserType.attributes) {
    if ($attr.name -in $ldapAttrs) {
        $ldapAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeUpdates $ldapAttrUpdates | Out-Null
Write-Host "  OK Selected $($ldapAttrUpdates.Count) LDAP attributes" -ForegroundColor Green

# Step 8: Import LDAP hierarchy and select TestUsers container
Write-TestStep "Step 8" "Importing LDAP hierarchy and selecting container"

Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id | Out-Null
$partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id)
$primaryBaseDN = $DirectoryConfig.BaseDN
$domainPartition = $partitions | Where-Object { $_.name -eq $primaryBaseDN -or $_.externalId -eq $primaryBaseDN } | Select-Object -First 1
if (-not $domainPartition -and $partitions.Count -eq 1) {
    $domainPartition = $partitions[0]
}
if (-not $domainPartition) { throw "Could not find primary partition $primaryBaseDN" }

Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $domainPartition.id -Selected $true | Out-Null

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
    $targetContainerName = if ($DirectoryConfig.UserContainer -match "^[Oo][Uu]=([^,]+)") { $matches[1] } else { "People" }
    $container = Find-Container -Containers $domainPartition.containers -Name $targetContainerName
} else {
    $container = Find-Container -Containers $domainPartition.containers -Name "TestUsers"
}

if ($container) {
    Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $container.id -Selected $true | Out-Null
    Write-Host "  OK Selected target container '$($container.name)'" -ForegroundColor Green
} else {
    Write-Host "  WARNING Target container not found; export may fail" -ForegroundColor Yellow
}

# Step 9: Get Metaverse 'User' object type and attributes
Write-TestStep "Step 9" "Resolving Metaverse 'User' object type"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) { throw "Metaverse 'User' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)
Write-Host "  OK Found 'User' MV object type (ID: $($mvUserType.id))" -ForegroundColor Green

# Step 10: Configure object matching rules (HR.employeeId <-> MV.Employee ID, LDAP.employeeID <-> MV.Employee ID)
Write-TestStep "Step 10" "Configuring object matching rules"

$hrEmployeeIdAttr = $hrPersonType.attributes | Where-Object { $_.name -eq "employeeId" }
$mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq "Employee ID" }

if (-not $hrEmployeeIdAttr -or -not $mvEmployeeIdAttr) {
    throw "Required attributes for HR matching rule not found (employeeId or Employee ID)"
}

New-JIMMatchingRule `
    -ConnectedSystemId $hrSystem.id `
    -ObjectTypeId $hrPersonType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId $hrEmployeeIdAttr.id `
    -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
Write-Host "  OK HR matching rule created (employeeId -> Employee ID)" -ForegroundColor Green

$ldapEmployeeIdAttrName = if ($isOpenLDAP) { 'employeeNumber' } else { 'employeeID' }
$ldapEmployeeIdAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $ldapEmployeeIdAttrName }

if (-not $ldapEmployeeIdAttr) {
    throw "Required LDAP attribute '$ldapEmployeeIdAttrName' not found"
}

New-JIMMatchingRule `
    -ConnectedSystemId $ldapSystem.id `
    -ObjectTypeId $ldapUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId $ldapEmployeeIdAttr.id `
    -TargetMetaverseAttributeId $mvEmployeeIdAttr.id | Out-Null
Write-Host "  OK LDAP matching rule created ($ldapEmployeeIdAttrName -> Employee ID)" -ForegroundColor Green

# Step 11: Create Import sync rule (HR -> MV)
Write-TestStep "Step 11" "Creating import sync rule ($importRuleName)"

$importRule = New-JIMSyncRule `
    -Name $importRuleName `
    -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

Write-Host "  OK Created import rule (ID: $($importRule.id))" -ForegroundColor Green

$importMappings = @(
    @{ CSAttr = "employeeId"; MVAttr = "Employee ID" },
    @{ CSAttr = "firstName"; MVAttr = "First Name" },
    @{ CSAttr = "lastName"; MVAttr = "Last Name" },
    @{ CSAttr = "displayName"; MVAttr = "Display Name" },
    @{ CSAttr = "email"; MVAttr = "Email" },
    @{ CSAttr = "department"; MVAttr = "Department" },
    @{ CSAttr = "title"; MVAttr = "Job Title" },
    @{ CSAttr = "samAccountName"; MVAttr = "Account Name" }
)

$importMappingsCreated = 0
foreach ($mapping in $importMappings) {
    $csAttr = $hrPersonType.attributes | Where-Object { $_.name -eq $mapping.CSAttr }
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MVAttr }
    if ($csAttr -and $mvAttr) {
        New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $mvAttr.id `
            -SourceConnectedSystemAttributeId $csAttr.id | Out-Null
        $importMappingsCreated++
    } else {
        Write-Host "    WARNING Could not map $($mapping.CSAttr) -> $($mapping.MVAttr)" -ForegroundColor Yellow
    }
}
Write-Host "  OK Created $importMappingsCreated import attribute flow mappings" -ForegroundColor Green

# Step 12: Add scoping criteria to import rule - department Equals 'Finance' (case-insensitive)
Write-TestStep "Step 12" "Adding scoping criteria to import rule"

$importScopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $importRule.id -Type All -PassThru
New-JIMScopingCriterion `
    -SyncRuleId $importRule.id `
    -GroupId $importScopeGroup.id `
    -ConnectedSystemAttributeName "department" `
    -ComparisonType Equals `
    -StringValue "Finance" | Out-Null

# Set explicit default InboundOutOfScopeAction = Disconnect (overridden by scenario sub-tests)
Set-JIMSyncRule -Id $importRule.id -InboundOutOfScopeAction Disconnect | Out-Null
Write-Host "  OK Import rule scoped to department = 'Finance' (InboundOutOfScopeAction=Disconnect)" -ForegroundColor Green

# Step 13: Create Export sync rule (MV -> LDAP)
Write-TestStep "Step 13" "Creating export sync rule ($exportRuleName)"

$exportRule = New-JIMSyncRule `
    -Name $exportRuleName `
    -ConnectedSystemId $ldapSystem.id `
    -ConnectedSystemObjectTypeId $ldapUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -PassThru

Write-Host "  OK Created export rule (ID: $($exportRule.id))" -ForegroundColor Green

# Choose export mappings based on directory type
$exportMappings = if ($isOpenLDAP) {
    @(
        @{ MVAttr = "Account Name"; CSAttr = "uid" },
        @{ MVAttr = "First Name"; CSAttr = "givenName" },
        @{ MVAttr = "Last Name"; CSAttr = "sn" },
        @{ MVAttr = "Display Name"; CSAttr = "displayName" },
        @{ MVAttr = "Display Name"; CSAttr = "cn" },
        @{ MVAttr = "Email"; CSAttr = "mail" },
        @{ MVAttr = "Department"; CSAttr = "departmentNumber" },
        @{ MVAttr = "Employee ID"; CSAttr = "employeeNumber" }
    )
} else {
    @(
        @{ MVAttr = "Account Name"; CSAttr = "sAMAccountName" },
        @{ MVAttr = "First Name"; CSAttr = "givenName" },
        @{ MVAttr = "Last Name"; CSAttr = "sn" },
        @{ MVAttr = "Display Name"; CSAttr = "displayName" },
        @{ MVAttr = "Email"; CSAttr = "mail" },
        @{ MVAttr = "Department"; CSAttr = "department" },
        @{ MVAttr = "Employee ID"; CSAttr = "employeeID" }
    )
}

$exportMappingsCreated = 0
foreach ($mapping in $exportMappings) {
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MVAttr }
    $csAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $mapping.CSAttr }
    if ($mvAttr -and $csAttr) {
        New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $csAttr.id `
            -SourceMetaverseAttributeId $mvAttr.id | Out-Null
        $exportMappingsCreated++
    } else {
        Write-Host "    WARNING Could not map $($mapping.MVAttr) -> $($mapping.CSAttr)" -ForegroundColor Yellow
    }
}
Write-Host "  OK Created $exportMappingsCreated export attribute flow mappings" -ForegroundColor Green

# Step 14: Add scoping criteria to export rule - Department Equals 'Finance' (MV-side)
Write-TestStep "Step 14" "Adding scoping criteria to export rule"

$exportScopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $exportRule.id -Type All -PassThru
New-JIMScopingCriterion `
    -SyncRuleId $exportRule.id `
    -GroupId $exportScopeGroup.id `
    -MetaverseAttributeName "Department" `
    -ComparisonType Equals `
    -StringValue "Finance" | Out-Null

# Set explicit default OutboundDeprovisionAction = Disconnect (overridden by scenario sub-tests)
Set-JIMSyncRule -Id $exportRule.id -OutboundDeprovisionAction Disconnect | Out-Null
Write-Host "  OK Export rule scoped to Department = 'Finance' (OutboundDeprovisionAction=Disconnect)" -ForegroundColor Green

# Step 15: Create Run Profiles
Write-TestStep "Step 15" "Creating run profiles"

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $hrSystem.id -RunType "FullImport" | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $hrSystem.id -RunType "FullSynchronisation" | Out-Null
Write-Host "  OK HR run profiles: Full Import, Full Synchronisation" -ForegroundColor Green

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $ldapSystem.id -RunType "FullImport" | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $ldapSystem.id -RunType "FullSynchronisation" | Out-Null
New-JIMRunProfile -Name "Export" -ConnectedSystemId $ldapSystem.id -RunType "Export" | Out-Null
Write-Host "  OK LDAP run profiles: Full Import, Full Synchronisation, Export" -ForegroundColor Green

Write-TestSection "Scenario 10 Setup Complete"
Write-Host "  HR Connected System:   $hrSystemName (ID: $($hrSystem.id))" -ForegroundColor Cyan
Write-Host "  LDAP Connected System: $ldapSystemName (ID: $($ldapSystem.id))" -ForegroundColor Cyan
Write-Host "  Import Sync Rule:      $importRuleName (ID: $($importRule.id))" -ForegroundColor Cyan
Write-Host "  Export Sync Rule:      $exportRuleName (ID: $($exportRule.id))" -ForegroundColor Cyan
Write-Host "  Both rules scoped to: department/Department = 'Finance'" -ForegroundColor Cyan
