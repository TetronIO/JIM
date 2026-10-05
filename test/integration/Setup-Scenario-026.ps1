# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 026: Metaverse-Derived Attribute Flows

.DESCRIPTION
    Configures JIM for Metaverse-Derived Attribute Flows (#1750): import Attribute Flows whose
    expressions read mv["..."], evaluated by JIM in dependency order. The provisioning substrate is
    Scenario 001's, composed exactly as Scenario 023's is:

      - Setup-Scenario-001.ps1 -GenerateAccountName -DeriveFromAccountName: Account Name is generated
        (OnlyIfTaken, Number suffix), Email = mv["Account Name"] + "@panoply.local" and User Principal
        Name = mv["Email"], three steps in one synchronisation. On Active Directory (Samba AD and the
        Active Directory lab) User Principal Name is exported to userPrincipalName; an RFC directory
        has no such attribute, so there it stays in the Metaverse.
      - The HR CSV is expected to have been generated with -OmitItOwnedAttributes
        (Invoke-Scenario-026-DerivedAttributeFlows.ps1 does this before calling this script), so
        samAccountName, email and userPrincipalName are genuinely absent from the HR feed.

    On top of that substrate this script adds the one cross-system derived flow the scenario needs:

      - "Training Summary" (new Metaverse attribute): an Attribute Flow on the HR import Synchronisation
        Rule, "Training " + mv["Training Status"], reading an attribute only the Training Records Source
        system contributes. Missing Input Behaviour is ContributeNoValue: a person with no training
        record gets no Training Summary until one arrives.
      - An export mapping Training Summary -> physicalDeliveryOfficeName on the directory, so the
        derived value is seen to reach the target. physicalDeliveryOfficeName is part of RFC 4519's
        organizationalPerson (inherited by inetOrgPerson and by Active Directory's user class) and is
        not used by any other Scenario 001 mapping.

    Supports OpenLDAP, Samba AD and the Active Directory lab; refuses 389 Directory Server, which adds
    nothing over OpenLDAP for this feature (the derived pass is directory-independent) and was not part
    of the feature's sign-off.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario-001.ps1. Small is the default and the
    feature's sign-off template.

.PARAMETER DirectoryConfig
    Directory configuration hashtable. Defaults to Get-DirectoryConfig -DirectoryType SambaAD.

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through to Setup-Scenario-001.ps1

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through to Setup-Scenario-001.ps1

.EXAMPLE
    ./Setup-Scenario-026.ps1 -ApiKey "jim_..." -Template Small

.EXAMPLE
    ./Setup-Scenario-026.ps1 -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary)
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

if ($DirectoryConfig.DirectoryType -notin @("OpenLDAP", "SambaAD", "ActiveDirectory")) {
    throw "Scenario 026 supports OpenLDAP, Samba AD and Active Directory only. 389 Directory Server was requested " +
          "($($DirectoryConfig.ConnectedSystemName)). Use -DirectoryType OpenLDAP, -DirectoryType SambaAD or " +
          "-DirectoryType ActiveDirectory."
}

Write-TestSection "Scenario 026 Setup: Metaverse-Derived Attribute Flows ($($DirectoryConfig.ConnectedSystemName))"

# Step 1: the provisioning substrate (HR CSV, generated Account Name, derived Email and User Principal Name)
Write-TestStep "Step 1" "Running Setup-Scenario-001.ps1 -GenerateAccountName -DeriveFromAccountName"

$setupParams = @{
    JIMUrl = $JIMUrl
    ApiKey = $ApiKey
    Template = $Template
    DirectoryConfig = $DirectoryConfig
    GenerateAccountName = $true
    DeriveFromAccountName = $true
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $setupParams.ExportConcurrency = $ExportConcurrency
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    $setupParams.MaxExportParallelism = $MaxExportParallelism
}

$config = & "$PSScriptRoot/Setup-Scenario-001.ps1" @setupParams
if (-not $config) {
    throw "Setup-Scenario-001.ps1 returned no configuration"
}
Write-Host "  ✓ Provisioning substrate configured (Account Name generated; Email and User Principal Name derived)" -ForegroundColor Green

# Step 2: connect for the scenario's own configuration
Write-TestStep "Step 2" "Connecting to JIM"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  ✓ Connected to $JIMUrl" -ForegroundColor Green

try {
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
    if (-not $mvUserType) {
        throw "Setup failed: Metaverse object type 'User' not found."
    }

    # Step 3: the Training Summary Metaverse attribute
    Write-TestStep "Step 3" "Creating the Training Summary Metaverse attribute"

    $mvAttributes = @(Get-JIMMetaverseAttribute)
    $trainingSummaryAttr = $mvAttributes | Where-Object { $_.name -eq "Training Summary" } | Select-Object -First 1
    if (-not $trainingSummaryAttr) {
        $trainingSummaryAttr = New-JIMMetaverseAttribute -Name "Training Summary" -Type Text -AttributePlurality SingleValued -ObjectTypeIds @($mvUserType.id)
        Write-Host "  ✓ Created MV attribute: Training Summary (ID: $($trainingSummaryAttr.id))" -ForegroundColor Green
        $mvAttributes = @($mvAttributes) + $trainingSummaryAttr
    }

    $attributeIds = @{}
    foreach ($name in @("Account Name", "Email", "User Principal Name", "Training Status", "Training Summary")) {
        $attr = $mvAttributes | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $attr) {
            throw "Setup failed: Metaverse attribute '$name' not found."
        }
        $attributeIds[$name] = $attr.id
    }

    # Step 4: the cross-system derived flow on the HR import rule
    Write-TestStep "Step 4" "Creating the cross-system derived flow (Training Summary, HR rule, reads Training Status)"

    $allRules = @(Get-JIMSyncRule)
    $importRule = $allRules | Where-Object { $_.name -eq "HR CSV Import Users" } | Select-Object -First 1
    $trainingImportRule = $allRules | Where-Object { $_.name -eq "Training Records Import" } | Select-Object -First 1
    if (-not $importRule -or -not $trainingImportRule) {
        throw "Setup failed: 'HR CSV Import Users' or 'Training Records Import' not found; Setup-Scenario-001.ps1 should have created both."
    }

    $importMappings = @(Get-JIMSyncRuleMapping -SyncRuleId $importRule.id)
    $trainingSummaryMapping = $importMappings | Where-Object { $_.targetMetaverseAttributeId -eq $attributeIds["Training Summary"] } | Select-Object -First 1
    if (-not $trainingSummaryMapping) {
        $trainingSummaryMapping = New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $attributeIds["Training Summary"] `
            -Expression '"Training " + mv["Training Status"]' `
            -MissingInputBehaviour ContributeNoValue -ErrorAction Stop
        Write-Host "  ✓ Derived Training Summary mapping created on the HR rule (ContributeNoValue, ID: $($trainingSummaryMapping.id))" -ForegroundColor Green
        $importMappings = @(Get-JIMSyncRuleMapping -SyncRuleId $importRule.id)
    }

    $mappingIds = @{}
    foreach ($name in @("Account Name", "Email", "User Principal Name", "Training Summary")) {
        $mapping = $importMappings | Where-Object { $_.targetMetaverseAttributeId -eq $attributeIds[$name] } | Select-Object -First 1
        if (-not $mapping) {
            throw "Setup failed: no $name mapping on 'HR CSV Import Users'."
        }
        $mappingIds[$name] = $mapping.id
    }

    # Step 5: export the derived value to the directory
    Write-TestStep "Step 5" "Exporting Training Summary to physicalDeliveryOfficeName"

    $ldapSystemName = $DirectoryConfig.ConnectedSystemName
    $ldapSystem = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName } | Select-Object -First 1
    if (-not $ldapSystem) {
        throw "Setup failed: Connected System '$ldapSystemName' not found."
    }
    $ldapUserType = @(Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes) | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass } | Select-Object -First 1
    if (-not $ldapUserType) {
        throw "Setup failed: LDAP object type '$($DirectoryConfig.UserObjectClass)' not found on '$ldapSystemName'."
    }
    $officeAttr = $ldapUserType.attributes | Where-Object { $_.name -eq 'physicalDeliveryOfficeName' } | Select-Object -First 1
    if (-not $officeAttr) {
        throw "Setup failed: LDAP attribute 'physicalDeliveryOfficeName' not found on '$ldapSystemName' (object type '$($DirectoryConfig.UserObjectClass)')."
    }
    if (-not $officeAttr.selected) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeId $officeAttr.id -Selected $true | Out-Null
        Write-Host "  ✓ Selected LDAP attribute 'physicalDeliveryOfficeName'" -ForegroundColor Green
    }

    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq "$ldapSystemName Export Users" } | Select-Object -First 1
    if (-not $exportRule) {
        throw "Setup failed: export Synchronisation Rule '$ldapSystemName Export Users' not found."
    }
    $officeMapping = @(Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id) | Where-Object { $_.targetConnectedSystemAttributeId -eq $officeAttr.id } | Select-Object -First 1
    if (-not $officeMapping) {
        New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $officeAttr.id `
            -SourceMetaverseAttributeId $attributeIds["Training Summary"] -ErrorAction Stop | Out-Null
        Write-Host "  ✓ Export mapping created: Training Summary -> physicalDeliveryOfficeName" -ForegroundColor Green
    }
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

Write-TestSection "Scenario 026 Setup Complete"
Write-Host "Directory:            $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.DirectoryType))" -ForegroundColor Cyan
Write-Host "Account Name:         Generated (OnlyIfTaken, Number), step 1" -ForegroundColor Cyan
Write-Host "Email:                mv[""Account Name""] + ""@panoply.local"", step 2" -ForegroundColor Cyan
Write-Host "User Principal Name:  mv[""Email""], step 3" -ForegroundColor Cyan
Write-Host "Training Summary:     ""Training "" + mv[""Training Status""] (HR rule; input from Training), ContributeNoValue" -ForegroundColor Cyan

$config.ImportRuleId = $importRule.id
$config.TrainingImportRuleId = $trainingImportRule.id
$config.ExportRuleId = $exportRule.id
$config.MvUserTypeId = $mvUserType.id
$config.AttributeIds = $attributeIds
$config.MappingIds = $mappingIds
return $config
