# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 15: SCIM 2.0 Client Connector

.DESCRIPTION
    Points a SCIM Connected System at the containerised test service provider
    (test/JIM.TestScimServiceProvider), which serves the same MockScimProvider the unit suite drives in
    process, over HTTPS.

    The provider's certificate is self-signed and generated at every start, so this script adds it to
    JIM's Trusted Certificates before configuring the Connected System. That is deliberate rather than
    convenient: connecting with Certificate Validation set to Skip would prove only that JIM can be told
    to ignore certificates, whereas trusting one specific certificate is what a customer with an internal
    certificate authority actually does, and it exercises that path (#1139) alongside the connector.

    Configures:
      - Users and Groups as Connected System Object Types, with SCIM's id as the External ID
      - An inbound Synchronisation Rule projecting Users to the Metaverse
      - An inbound Synchronisation Rule projecting Groups to the Metaverse, so imported membership
        references have something to resolve against
      - An outbound Synchronisation Rule flowing Display Name to the provider's displayName, which the
        seeded resources do not carry, so a Full Synchronisation produces Pending Exports to send
      - Run Profiles for Full Import, Delta Import, Full Synchronisation and Export

    Bulk operations are turned on, so the export goes through the provider's /Bulk endpoint.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ScimBaseUrl
    The SCIM service provider's base URL. Defaults to the container's name on the Docker network.

.PARAMETER ScimCertificatePath
    The provider's public certificate, written by the provider at startup for JIM to trust.

.PARAMETER UseBulkOperations
    Whether to turn on bulk exports. Set false to drive the same scenario down the per-object path.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data comes from the provider, not a template.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Setup-Scenario15.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$ScimBaseUrl = "https://scim-provider:5300",

    [Parameter(Mandatory=$false)]
    [string]$ScimCertificatePath,

    [Parameter(Mandatory=$false)]
    [bool]$UseBulkOperations = $true,

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

# Accepted for runner compatibility; this scenario has no directory target and no export tuning.
$null = $DirectoryConfig
$null = $ExportConcurrency
$null = $MaxExportParallelism
$null = $Template

. "$PSScriptRoot/utils/Test-Helpers.ps1"

$scimSystemName = "SCIM Test Service Provider"
$userImportRuleName = "SCIM Users Import (SCIM -> MV)"
$groupImportRuleName = "SCIM Groups Import (SCIM -> MV)"
$userExportRuleName = "SCIM Users Export (MV -> SCIM)"
$certificateName = "SCIM Test Service Provider"

Write-TestSection "Scenario 15 Setup: SCIM 2.0 Client Connector"
Write-Host "SCIM provider: $ScimBaseUrl" -ForegroundColor Gray
Write-Host "Bulk exports:  $UseBulkOperations" -ForegroundColor Gray
Write-Host ""

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

# Step 3: Clean up anything left by a previous run
Write-TestStep "Step 3" "Cleaning up existing configuration"

$existingSystem = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $scimSystemName }
if ($existingSystem) {
    Remove-JIMConnectedSystem -Id $existingSystem.id -Force | Out-Null
    Write-Host "  Removed existing '$scimSystemName'" -ForegroundColor Gray
}

$existingCertificate = @(Get-JIMCertificate -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $certificateName }
foreach ($certificate in $existingCertificate) {
    # The provider generates a fresh certificate at every start, so a trusted one from a previous run is
    # not merely stale, it is for a key the provider no longer holds.
    Remove-JIMCertificate -Id $certificate.id -Force | Out-Null
    Write-Host "  Removed stale trusted certificate from a previous run" -ForegroundColor Gray
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 4: Trust the provider's certificate
Write-TestStep "Step 4" "Trusting the SCIM service provider's certificate"

if (-not $ScimCertificatePath) {
    throw "ScimCertificatePath is required. The SCIM test service provider writes its public certificate there at startup; without trusting it, JIM cannot validate the connection and the scenario would have to skip validation, which tests nothing."
}
if (-not (Test-Path $ScimCertificatePath)) {
    throw "The SCIM service provider's certificate was not found at '$ScimCertificatePath'. Has the provider started?"
}

$trusted = Add-JIMCertificate `
    -Name $certificateName `
    -Path $ScimCertificatePath `
    -Notes "Self-signed certificate generated by the SCIM test service provider for this run." `
    -PassThru
Write-Host "  OK Trusted certificate (ID: $($trusted.id), thumbprint: $($trusted.thumbprint))" -ForegroundColor Green

# Step 5: Resolve the connector definition
Write-TestStep "Step 5" "Resolving the SCIM connector definition"

$scimConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM SCIM 2.0 Client Connector" }
if (-not $scimConnector) { throw "JIM SCIM 2.0 Client Connector definition not found. Has seeding run?" }
Write-Host "  OK SCIM connector (ID: $($scimConnector.id))" -ForegroundColor Green

# Step 6: Create and configure the Connected System
Write-TestStep "Step 6" "Creating the SCIM Connected System"

$scimSystem = New-JIMConnectedSystem `
    -Name $scimSystemName `
    -Description "SCIM 2.0 test service provider for integration testing" `
    -ConnectorDefinitionId $scimConnector.id `
    -PassThru

$connectorFull = Get-JIMConnectorDefinition -Id $scimConnector.id
function Get-SettingId { param([string]$Name)
    $setting = $connectorFull.settings | Where-Object { $_.name -eq $Name }
    if (-not $setting) { throw "SCIM connector setting '$Name' not found; the connector definition may not have been re-seeded." }
    return $setting.id
}

$settings = @{
    (Get-SettingId "Base URL")              = @{ stringValue = $ScimBaseUrl }
    (Get-SettingId "Authentication Method") = @{ stringValue = "Static Bearer Token" }
    (Get-SettingId "Bearer Token")          = @{ stringValue = "integration-test-token" }
    (Get-SettingId "Certificate Validation")= @{ stringValue = "Full Validation" }
    (Get-SettingId "Use Bulk Operations")   = @{ checkboxValue = $UseBulkOperations }
}

# Saving runs the connector's live connectivity test against /ServiceProviderConfig, so this step
# failing means the certificate was not trusted or the provider is not reachable, not that JIM is broken.
Set-JIMConnectedSystem -Id $scimSystem.id -SettingValues $settings | Out-Null
Write-Host "  OK Connected System configured and connectivity verified (ID: $($scimSystem.id))" -ForegroundColor Green

# Step 7: Import the schema from the provider's own discovery documents
Write-TestStep "Step 7" "Importing the SCIM schema"

Import-JIMConnectedSystemSchema -Id $scimSystem.id | Out-Null
$objectTypes = Get-JIMConnectedSystem -Id $scimSystem.id -ObjectTypes

$userType = $objectTypes | Where-Object { $_.name -eq "User" }
$groupType = $objectTypes | Where-Object { $_.name -eq "Group" }
if (-not $userType) { throw "The provider published no 'User' resource type." }
if (-not $groupType) { throw "The provider published no 'Group' resource type." }

# The flattening of SCIM's complex and multi-valued attributes is the connector's own work, so assert it
# reached the schema rather than trusting that discovery merely returned something.
foreach ($expected in @("userName", "name.givenName", "emails.work", "id")) {
    if (-not ($userType.attributes | Where-Object { $_.name -eq $expected })) {
        throw "The imported User schema is missing '$expected'; SCIM attribute flattening did not produce the expected shape."
    }
}
Write-Host "  OK Schema imported: User ($($userType.attributes.Count) attributes), Group ($($groupType.attributes.Count) attributes)" -ForegroundColor Green

# Step 8: Select object types and attributes
Write-TestStep "Step 8" "Selecting object types and attributes"

Set-JIMConnectedSystemObjectType -ConnectedSystemId $scimSystem.id -ObjectTypeId $userType.id -Selected $true | Out-Null
Set-JIMConnectedSystemObjectType -ConnectedSystemId $scimSystem.id -ObjectTypeId $groupType.id -Selected $true | Out-Null

$userAttributeNames = @("id", "userName", "active", "displayName", "name.givenName", "name.familyName", "emails.work", "meta.version")
$userAttributeUpdates = @{}
foreach ($attribute in $userType.attributes | Where-Object { $_.name -in $userAttributeNames }) {
    $userAttributeUpdates[$attribute.id] = @{ selected = $true; isExternalId = ($attribute.name -eq "id") }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $scimSystem.id -ObjectTypeId $userType.id -AttributeUpdates $userAttributeUpdates | Out-Null

$groupAttributeNames = @("id", "displayName", "members", "meta.version")
$groupAttributeUpdates = @{}
foreach ($attribute in $groupType.attributes | Where-Object { $_.name -in $groupAttributeNames }) {
    $groupAttributeUpdates[$attribute.id] = @{ selected = $true; isExternalId = ($attribute.name -eq "id") }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $scimSystem.id -ObjectTypeId $groupType.id -AttributeUpdates $groupAttributeUpdates | Out-Null

Write-Host "  OK Selected $($userAttributeUpdates.Count) User and $($groupAttributeUpdates.Count) Group attributes (SCIM id is the External ID)" -ForegroundColor Green

# Step 9: Resolve Metaverse types and attributes
Write-TestStep "Step 9" "Resolving Metaverse object types"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvGroupType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" } | Select-Object -First 1
if (-not $mvUserType -or -not $mvGroupType) { throw "Metaverse 'User' or 'Group' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)

function Get-ScimAttribute { param($ObjectType, [string]$Name)
    $attribute = $ObjectType.attributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "SCIM attribute '$Name' not found on '$($ObjectType.name)'." }
    return $attribute
}
function Get-MvAttribute { param([string]$Name)
    $attribute = $mvAttributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "Metaverse attribute '$Name' not found." }
    return $attribute
}
Write-Host "  OK Metaverse 'User' and 'Group' resolved" -ForegroundColor Green

# Step 10: Object matching rules
Write-TestStep "Step 10" "Configuring object matching rules"

New-JIMMatchingRule `
    -ConnectedSystemId $scimSystem.id `
    -ObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-ScimAttribute $userType "userName").id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null

New-JIMMatchingRule `
    -ConnectedSystemId $scimSystem.id `
    -ObjectTypeId $groupType.id `
    -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-ScimAttribute $groupType "displayName").id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null

Write-Host "  OK Matching rules created (userName -> Account Name, displayName -> Display Name)" -ForegroundColor Green

# Step 11: Inbound Synchronisation Rules
Write-TestStep "Step 11" "Creating inbound Synchronisation Rules"

$userImportRule = New-JIMSyncRule `
    -Name $userImportRuleName `
    -ConnectedSystemId $scimSystem.id `
    -ConnectedSystemObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

$userImportFlows = @(
    @{ Scim = "userName";         Mv = "Account Name" },
    @{ Scim = "name.givenName";   Mv = "First Name" },
    @{ Scim = "name.familyName";  Mv = "Last Name" },
    @{ Scim = "emails.work";      Mv = "Email" }
)
foreach ($flow in $userImportFlows) {
    New-JIMSyncRuleMapping -SyncRuleId $userImportRule.id `
        -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-ScimAttribute $userType $flow.Scim).id | Out-Null
}

# Display Name is composed rather than copied, so it is a value the provider does not hold: that is what
# gives the outbound rule below something real to export.
New-JIMSyncRuleMapping -SyncRuleId $userImportRule.id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Display Name").id `
    -Expression 'cs["name.givenName"] + " " + cs["name.familyName"]' | Out-Null

$groupImportRule = New-JIMSyncRule `
    -Name $groupImportRuleName `
    -ConnectedSystemId $scimSystem.id `
    -ConnectedSystemObjectTypeId $groupType.id `
    -MetaverseObjectTypeId $mvGroupType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $groupImportRule.id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Display Name").id `
    -SourceConnectedSystemAttributeId (Get-ScimAttribute $groupType "displayName").id | Out-Null

Write-Host "  OK Inbound rules created (Users project with a composed Display Name; Groups project)" -ForegroundColor Green

# Step 12: Outbound Synchronisation Rule
Write-TestStep "Step 12" "Creating the outbound Synchronisation Rule"

$userExportRule = New-JIMSyncRule `
    -Name $userExportRuleName `
    -ConnectedSystemId $scimSystem.id `
    -ConnectedSystemObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id `
    -TargetConnectedSystemAttributeId (Get-ScimAttribute $userType "displayName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null

# EnforceState is what makes this rule remediate objects that already exist in the provider rather than
# only shaping ones JIM provisions. The seeded users are already there and carry no displayName, so
# without it the rule is evaluated and correctly finds nothing to provision, and the export has nothing
# to send. With it, the gap between the Metaverse and the provider is drift, and JIM closes it.
Set-JIMSyncRule -Id $userExportRule.id -EnforceState $true | Out-Null

Write-Host "  OK Outbound rule created (Display Name -> displayName, state enforced)" -ForegroundColor Green

# Step 13: Run Profiles
Write-TestStep "Step 13" "Creating Run Profiles"

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $scimSystem.id -RunType "FullImport" -PageSize 10 | Out-Null
New-JIMRunProfile -Name "Delta Import" -ConnectedSystemId $scimSystem.id -RunType "DeltaImport" -PageSize 10 | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $scimSystem.id -RunType "FullSynchronisation" | Out-Null
New-JIMRunProfile -Name "Export" -ConnectedSystemId $scimSystem.id -RunType "Export" | Out-Null

# A page size below the seeded resource count is deliberate: a single-page import would never exercise
# the connector's pagination, which is where a client silently reading a fraction of a system goes wrong.
Write-Host "  OK Run Profiles created (imports page at 10, below the seeded resource count)" -ForegroundColor Green

Write-TestSection "Scenario 15 Setup Complete"
Write-Host "  Connected System:  $scimSystemName (ID: $($scimSystem.id))" -ForegroundColor Cyan
Write-Host "  SCIM provider:     $ScimBaseUrl (certificate trusted, Full Validation)" -ForegroundColor Cyan
Write-Host "  Bulk exports:      $UseBulkOperations" -ForegroundColor Cyan
