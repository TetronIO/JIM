# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 15: SCIM 2.0 Client Connector

.DESCRIPTION
    Configures two Connected Systems:

      - An HR CSV source (File Connector), the authoritative source of identity. It projects Users to
        the Metaverse and contributes Display Name, which is the value the scenario exports.
      - The SCIM system, pointed at the containerised test service provider
        (test/JIM.TestScimServiceProvider) over HTTPS. Its Users join to the HR-projected Metaverse
        Objects rather than projecting their own; its Groups project, so imported membership references
        have something to prove reference staging against.

    Two systems rather than one is not incidental. JIM deliberately never exports a value back to the
    Connected System it came from (Q3 circular-sync prevention, OUTBOUND_SYNC_DESIGN.md), so a scenario
    where SCIM sourced the Metaverse values it was also the export target for produced no Pending
    Exports at all, correctly. The export only exists because a different system is authoritative,
    which is also the shape every real deployment has.

    The provider's certificate is self-signed and generated at every start, so this script adds it to
    JIM's Trusted Certificates before configuring the Connected System. That is deliberate rather than
    convenient: connecting with Certificate Validation set to Skip would prove only that JIM can be told
    to ignore certificates, whereas trusting one specific certificate is what a customer with an internal
    certificate authority actually does, and it exercises that path (#1139) alongside the connector.

    Bulk operations are turned on by default, so the export goes through the provider's /Bulk endpoint.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ScimBaseUrl
    The SCIM service provider's base URL. Defaults to the container's name on the Docker network.

.PARAMETER ScimCertificatePath
    The provider's public certificate, written by the provider at startup for JIM to trust.

.PARAMETER UserCount
    How many users the provider was seeded with (SCIM_USER_COUNT on the container). The HR CSV is
    generated to match, one row per seeded user, so every SCIM User has a Metaverse Object to join.

.PARAMETER UseBulkOperations
    Whether to turn on bulk exports. Set false to drive the same scenario down the per-object path.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data comes from the provider and its own CSV.

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
    [int]$UserCount = 25,

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

$hrSystemName = "SCIM Scenario HR Source"
$hrImportRuleName = "SCIM Scenario HR Import (CSV -> MV)"
$hrCsvFilePath = "/connector-files/test-data/scenario15-hr-users.csv"
$scimSystemName = "SCIM Test Service Provider"
$userJoinRuleName = "SCIM Users Join (SCIM -> MV)"
$groupImportRuleName = "SCIM Groups Import (SCIM -> MV)"
$userExportRuleName = "SCIM Users Export (MV -> SCIM)"
$certificateName = "SCIM Test Service Provider"

Write-TestSection "Scenario 15 Setup: SCIM 2.0 Client Connector"
Write-Host "SCIM provider: $ScimBaseUrl" -ForegroundColor Gray
Write-Host "Bulk exports:  $UseBulkOperations" -ForegroundColor Gray
Write-Host "Users:         $UserCount" -ForegroundColor Gray
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

foreach ($systemName in @($scimSystemName, $hrSystemName)) {
    $existing = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $systemName }
    foreach ($system in $existing) {
        Remove-JIMConnectedSystem -Id $system.id -Force | Out-Null
        Write-Host "  Removed existing '$systemName'" -ForegroundColor Gray
    }
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

# Upload the certificate bytes rather than passing the path: -Path sends the path for jim.web to read
# server-side, and when JIM runs containerised (the full integration stack) the host path this script
# reads from does not exist inside the container.
$certificateBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $ScimCertificatePath).Path)
$trusted = Add-JIMCertificate `
    -Name $certificateName `
    -CertificateData $certificateBytes `
    -Notes "Self-signed certificate generated by the SCIM test service provider for this run." `
    -PassThru
Write-Host "  OK Trusted certificate (ID: $($trusted.id), thumbprint: $($trusted.thumbprint))" -ForegroundColor Green

# Step 5: Generate the HR CSV and place it where the worker reads connector files
Write-TestStep "Step 5" "Seeding the HR CSV ($UserCount users)"

# One row per seeded SCIM user, keyed so the join lands: the CSV's accountName equals the provider's
# userName. Display Name is the value the scenario exports; the provider's seed deliberately has no
# displayName, so the export has real work to do. Deterministic, no Get-Date (see test/CLAUDE.md).
$csvLines = [System.Collections.Generic.List[string]]::new()
$csvLines.Add("accountName,firstName,lastName,displayName,email,department")
for ($i = 1; $i -le $UserCount; $i++) {
    $csvLines.Add("user$i,User,Number$i,User Number$i,user$i@example.com,Engineering")
}

$localCsvPath = Join-Path ([System.IO.Path]::GetTempPath()) "scenario15-hr-users.csv"
Set-Content -Path $localCsvPath -Value ($csvLines -join "`n") -NoNewline
Write-FileToConnectorVolume -SourcePath $localCsvPath -DestinationPath $hrCsvFilePath
Write-Host "  OK HR CSV seeded to $hrCsvFilePath" -ForegroundColor Green

# Step 6: Resolve connector definitions
Write-TestStep "Step 6" "Resolving connector definitions"

$connectors = Get-JIMConnectorDefinition
$fileConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
$scimConnector = $connectors | Where-Object { $_.name -eq "JIM SCIM 2.0 Client Connector" }
if (-not $fileConnector) { throw "JIM File Connector definition not found." }
if (-not $scimConnector) { throw "JIM SCIM 2.0 Client Connector definition not found. Has seeding run?" }
Write-Host "  OK File connector (ID: $($fileConnector.id)), SCIM connector (ID: $($scimConnector.id))" -ForegroundColor Green

# Step 7: Create and configure the HR CSV Connected System
Write-TestStep "Step 7" "Creating the HR CSV Connected System"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "Authoritative HR source for the SCIM Connector integration scenario" `
    -ConnectorDefinitionId $fileConnector.id `
    -PassThru

$fileConnectorFull = Get-JIMConnectorDefinition -Id $fileConnector.id
$hrSettings = @{}
foreach ($pair in @(
    @{ Name = "File Path"; Value = @{ stringValue = $hrCsvFilePath } },
    @{ Name = "Delimiter"; Value = @{ stringValue = "," } },
    @{ Name = "Object Type"; Value = @{ stringValue = "person" } })) {
    $setting = $fileConnectorFull.settings | Where-Object { $_.name -eq $pair.Name }
    if ($setting) { $hrSettings[$setting.id] = $pair.Value }
}
Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues $hrSettings | Out-Null

Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
if (-not $hrPersonType) { throw "HR 'person' object type not found in schema." }

Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -Selected $true | Out-Null

$hrAttributeUpdates = @{}
foreach ($attribute in $hrPersonType.attributes) {
    $hrAttributeUpdates[$attribute.id] = @{ selected = $true; isExternalId = ($attribute.name -eq "accountName") }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -AttributeUpdates $hrAttributeUpdates | Out-Null
Write-Host "  OK HR CSV system configured (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 8: Create and configure the SCIM Connected System
Write-TestStep "Step 8" "Creating the SCIM Connected System"

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

# Step 9: Import the schema from the provider's own discovery documents
Write-TestStep "Step 9" "Importing the SCIM schema"

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

# Step 10: Select SCIM object types and attributes
Write-TestStep "Step 10" "Selecting SCIM object types and attributes"

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

# Step 11: Resolve Metaverse types and attributes
Write-TestStep "Step 11" "Resolving Metaverse object types"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvGroupType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" } | Select-Object -First 1
if (-not $mvUserType -or -not $mvGroupType) { throw "Metaverse 'User' or 'Group' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)

function Get-CsAttribute { param($ObjectType, [string]$Name)
    $attribute = $ObjectType.attributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "Attribute '$Name' not found on '$($ObjectType.name)'." }
    return $attribute
}
function Get-MvAttribute { param([string]$Name)
    $attribute = $mvAttributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "Metaverse attribute '$Name' not found." }
    return $attribute
}
Write-Host "  OK Metaverse 'User' and 'Group' resolved" -ForegroundColor Green

# Step 12: Object matching rules
Write-TestStep "Step 12" "Configuring object matching rules"

# HR matches on accountName so a re-run joins rather than projecting duplicates.
New-JIMMatchingRule `
    -ConnectedSystemId $hrSystem.id `
    -ObjectTypeId $hrPersonType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $hrPersonType "accountName").id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null

# SCIM Users join to the HR-projected Metaverse Objects on the same key.
New-JIMMatchingRule `
    -ConnectedSystemId $scimSystem.id `
    -ObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $userType "userName").id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null

New-JIMMatchingRule `
    -ConnectedSystemId $scimSystem.id `
    -ObjectTypeId $groupType.id `
    -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $groupType "displayName").id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null

Write-Host "  OK Matching rules created (HR accountName and SCIM userName -> Account Name; group displayName -> Display Name)" -ForegroundColor Green

# Step 13: Inbound Synchronisation Rules
Write-TestStep "Step 13" "Creating inbound Synchronisation Rules"

# HR is authoritative: it projects and contributes every identity value, including the Display Name the
# scenario exports to SCIM.
$hrImportRule = New-JIMSyncRule `
    -Name $hrImportRuleName `
    -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

$hrFlows = @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "firstName";   Mv = "First Name" },
    @{ Cs = "lastName";    Mv = "Last Name" },
    @{ Cs = "displayName"; Mv = "Display Name" },
    @{ Cs = "email";       Mv = "Email" },
    @{ Cs = "department";  Mv = "Department" }
)
foreach ($flow in $hrFlows) {
    New-JIMSyncRuleMapping -SyncRuleId $hrImportRule.id `
        -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrPersonType $flow.Cs).id | Out-Null
}

# SCIM Users deliberately do not project and contribute no Display Name. If SCIM sourced the values it
# was also the export target for, Q3 circular-sync prevention would (correctly) suppress every export.
# The one flow keeps the rule non-empty and is value-identical to HR's, so it changes nothing.
$userJoinRule = New-JIMSyncRule `
    -Name $userJoinRuleName `
    -ConnectedSystemId $scimSystem.id `
    -ConnectedSystemObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $userJoinRule.id `
    -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id `
    -SourceConnectedSystemAttributeId (Get-CsAttribute $userType "userName").id | Out-Null

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
    -SourceConnectedSystemAttributeId (Get-CsAttribute $groupType "displayName").id | Out-Null

Write-Host "  OK Inbound rules created (HR projects and is authoritative; SCIM Users join; SCIM Groups project)" -ForegroundColor Green

# Step 14: Outbound Synchronisation Rule
Write-TestStep "Step 14" "Creating the outbound Synchronisation Rule"

# Created disabled, and enabled by the scenario only after the SCIM import and join have run. This is
# the brownfield adoption order a real deployment follows: the provider already holds these users, and
# a provisioning rule that goes live before they are joined would duplicate every one of them.
$userExportRule = New-JIMSyncRule `
    -Name $userExportRuleName `
    -ConnectedSystemId $scimSystem.id `
    -ConnectedSystemObjectTypeId $userType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -Enabled $false `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttribute $userType "displayName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null

# A provisioned resource must carry a userName, or the provider holds an anonymous shell; for already
# joined users the value is identical to what the provider holds, so no-net-change detection keeps this
# flow from generating exports of its own.
New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttribute $userType "userName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null

# The rule is scoped to Department = Engineering, which is what makes provisioning and deprovisioning
# testable transitions: a user entering the department is provisioned into SCIM, and one leaving it is
# deleted there (OutboundDeprovisionAction=Delete), both driven purely by HR data.
$exportScopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $userExportRule.id -Type All -PassThru
New-JIMScopingCriterion `
    -SyncRuleId $userExportRule.id `
    -GroupId $exportScopeGroup.id `
    -MetaverseAttributeId (Get-MvAttribute "Department").id `
    -ComparisonType Equals `
    -StringValue "Engineering" | Out-Null

# EnforceState makes inbound SCIM changes re-evaluate this rule, so drift between the Metaverse and the
# provider is remediated rather than only shaping newly provisioned objects.
Set-JIMSyncRule -Id $userExportRule.id -OutboundDeprovisionAction Delete -EnforceState $true | Out-Null

Write-Host "  OK Outbound rule created (provisions; Display Name and Account Name flow; scoped to Department=Engineering; deprovision deletes; state enforced)" -ForegroundColor Green

# Step 15: Run Profiles
Write-TestStep "Step 15" "Creating Run Profiles"

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $hrSystem.id -RunType "FullImport" -FilePath $hrCsvFilePath | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $hrSystem.id -RunType "FullSynchronisation" | Out-Null

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $scimSystem.id -RunType "FullImport" -PageSize 10 | Out-Null
New-JIMRunProfile -Name "Delta Import" -ConnectedSystemId $scimSystem.id -RunType "DeltaImport" -PageSize 10 | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $scimSystem.id -RunType "FullSynchronisation" | Out-Null
New-JIMRunProfile -Name "Export" -ConnectedSystemId $scimSystem.id -RunType "Export" | Out-Null

# A page size below the seeded resource count is deliberate: a single-page import would never exercise
# the connector's pagination, which is where a client silently reading a fraction of a system goes wrong.
Write-Host "  OK Run Profiles created (SCIM imports page at 10, below the seeded resource count)" -ForegroundColor Green

Write-TestSection "Scenario 15 Setup Complete"
Write-Host "  HR source:         $hrSystemName (ID: $($hrSystem.id))" -ForegroundColor Cyan
Write-Host "  SCIM target:       $scimSystemName (ID: $($scimSystem.id))" -ForegroundColor Cyan
Write-Host "  SCIM provider:     $ScimBaseUrl (certificate trusted, Full Validation)" -ForegroundColor Cyan
Write-Host "  Bulk exports:      $UseBulkOperations" -ForegroundColor Cyan
