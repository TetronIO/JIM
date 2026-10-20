# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 029: JIM provisioning its own SCIM endpoint

.DESCRIPTION
    Configures one JIM to provision its own SCIM 2.0 endpoint through the shipped SCIM 2.0 Client Connector, and to
    import what arrives there through the SCIM 2.0 Service Provider Connector (#124). The requests cross a real
    socket over HTTPS exactly as they would between two JIM instances.

    Three Connected Systems:

      - an HR CSV source (File Connector) holding people and one group, told apart by its objectType column;
      - the Service Provider: the SCIM endpoint the client calls, imported by a Schedule of Delta Import and Delta
        Synchronisation. Its Users join the Metaverse by userName and contribute Job Title, which only an identity
        provider pushing to the endpoint sets; its Groups join by display name;
      - the Client: the SCIM 2.0 Client Connector pointed at the Service Provider's base URL on jim.web over HTTPS
        (the certificate the runner minted, trusted rather than skipped), with a key scoped to the Service
        Provider. Enabled people and the group provision to it.

    Two keys are minted for the Service Provider: one the Client uses, and one the scenario's identity provider
    requests use, so each has a SCIM request summary of its own. Their secrets are kept in a temporary file for
    the scenario, because a key's secret is shown once.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER CertificatePath
    jim.web's certificate. Defaults to the one the runner mints (test/integration/results/jim-web-tls/tls.crt).

.PARAMETER JimWebHostname
    The name the worker reaches jim.web by on the Docker network, which the certificate is issued for.

.PARAMETER UserCount
    How many people the HR CSV holds.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Setup-Scenario-029.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_ak_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$CertificatePath = (Join-Path $PSScriptRoot "results" "jim-web-tls" "tls.crt"),

    [Parameter(Mandatory=$false)]
    [string]$JimWebHostname = "jim.web",

    [Parameter(Mandatory=$false)]
    [ValidateRange(6, 200)]
    [int]$UserCount = 12,

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
. "$PSScriptRoot/utils/Scenario-029-Data.ps1"

Write-TestSection "Scenario 029 Setup: JIM provisioning its own SCIM endpoint"
Write-Host "People: $UserCount" -ForegroundColor Gray
Write-Host ""

# Step 1: Connect and turn the feature on
Write-TestStep "Step 1" "Connecting to JIM and turning on the SCIM 2.0 Service Provider"
if (-not $ApiKey) { throw "API key required for authentication" }
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
# In Development: only a development host or this harness can turn it on.
Enable-JIMFeature -Name "Features.ScimServiceProvider" -AllowInDevelopment | Out-Null
Write-Host "  OK Connected; Features.ScimServiceProvider on" -ForegroundColor Green

# Step 2: Resolve connector definitions
Write-TestStep "Step 2" "Resolving connector definitions"
$connectors = Get-JIMConnectorDefinition
$fileConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
$clientConnector = $connectors | Where-Object { $_.name -eq "JIM SCIM 2.0 Client Connector" }
$serviceProviderConnector = $connectors | Where-Object { $_.name -eq "JIM SCIM 2.0 Service Provider Connector" }
if (-not $fileConnector) { throw "JIM File Connector definition not found." }
if (-not $clientConnector) { throw "JIM SCIM 2.0 Client Connector definition not found." }
if (-not $serviceProviderConnector) { throw "JIM SCIM 2.0 Service Provider Connector definition not found. Is Features.ScimServiceProvider on?" }
Write-Host "  OK File ($($fileConnector.id)), SCIM client ($($clientConnector.id)), SCIM service provider ($($serviceProviderConnector.id))" -ForegroundColor Green

function Get-ConnectorSettingId {
    param($Connector, [string]$Name)
    $setting = $Connector.settings | Where-Object { $_.name -eq $Name }
    if (-not $setting) { throw "Connector setting '$Name' not found on '$($Connector.name)'." }
    return $setting.id
}

function Get-CsAttribute {
    param($ObjectType, [string]$Name)
    $attribute = $ObjectType.attributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) {
        $available = ($ObjectType.attributes | ForEach-Object { $_.name }) -join ', '
        throw "Attribute '$Name' not found on '$($ObjectType.name)'. It has: $available"
    }
    return $attribute
}

function Select-CsAttributes {
    param([int]$ConnectedSystemId, $ObjectType, [string[]]$Names, [string]$ExternalId)
    $updates = @{}
    foreach ($name in $Names) {
        $attribute = Get-CsAttribute $ObjectType $name
        $updates[$attribute.id] = @{ selected = $true; isExternalId = ($name -eq $ExternalId) }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $ObjectType.id -AttributeUpdates $updates | Out-Null
}

function Get-ScimObjectTypes {
    param([int]$ConnectedSystemId, [string]$Label)
    $types = Get-JIMConnectedSystem -Id $ConnectedSystemId -ObjectTypes
    $user = $types | Where-Object { $_.name -eq "User" }
    $group = $types | Where-Object { $_.name -eq "Group" }
    if (-not $user -or -not $group) { throw "The $Label schema has no 'User' or 'Group' object type." }
    foreach ($type in @($user, $group)) {
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $type.id -Selected $true | Out-Null
    }
    return @{ User = $user; Group = $group }
}

# meta.version is not listed: the client sends it back as If-Match, so schema import selects and locks it itself
# where the provider supports entity tags (#2404), which Step 8 checks.
$userAttributes = @("id", "userName", "active", "name.givenName", "name.familyName", "emails.work", "enterpriseUser.department", "title")
$groupAttributes = @("id", "displayName", "members")

# Step 3: The Service Provider
Write-TestStep "Step 3" "Creating the Service Provider Connected System"
$serviceProviderSystem = New-JIMConnectedSystem `
    -Name $script:Scenario029ServiceProviderSystemName `
    -Description "JIM's own SCIM endpoint, which the Client provisions and a Schedule imports" `
    -ConnectorDefinitionId $serviceProviderConnector.id `
    -PassThru
Import-JIMConnectedSystemSchema -Id $serviceProviderSystem.id | Out-Null
$serviceProviderTypes = Get-ScimObjectTypes -ConnectedSystemId $serviceProviderSystem.id -Label "Service Provider"
Select-CsAttributes -ConnectedSystemId $serviceProviderSystem.id -ObjectType $serviceProviderTypes.User -ExternalId "id" -Names $userAttributes
Select-CsAttributes -ConnectedSystemId $serviceProviderSystem.id -ObjectType $serviceProviderTypes.Group -ExternalId "id" -Names $groupAttributes
$baseUrl = "https://${JimWebHostname}:8443/scim/v2/$($serviceProviderSystem.id)"
Write-Host "  OK Service Provider (ID: $($serviceProviderSystem.id)), base URL $baseUrl" -ForegroundColor Green

# Step 4: Keys
Write-TestStep "Step 4" "Minting the Client's and the identity provider's keys"
$clientKey = New-JIMScimKey -ConnectedSystemId $serviceProviderSystem.id -Name $script:Scenario029ClientKeyName `
    -Description "The SCIM 2.0 Client Connector's key, for Scenario 029"
$identityProviderKey = New-JIMScimKey -ConnectedSystemId $serviceProviderSystem.id -Name $script:Scenario029IdentityProviderKeyName `
    -Description "The scenario's identity provider requests, for Scenario 029"
if (-not $clientKey.key -or -not $identityProviderKey.key) { throw "A minted SCIM key came back without its secret." }
@{ client = $clientKey.key; identityProvider = $identityProviderKey.key; serviceProviderId = $serviceProviderSystem.id } |
    ConvertTo-Json | Set-Content -Path $script:Scenario029KeyFile
Write-Host "  OK Two keys minted; secrets kept for the scenario" -ForegroundColor Green

# Step 5: Trust jim.web's certificate
Write-TestStep "Step 5" "Trusting jim.web's certificate"
if (-not (Test-Path $CertificatePath)) {
    throw "jim.web's certificate was not found at '$CertificatePath'. Run-IntegrationTests.ps1 mints it before starting the stack."
}
$certificateBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $CertificatePath).Path)
$trusted = Add-JIMCertificate `
    -Name "jim.web (Scenario 029)" `
    -CertificateData $certificateBytes `
    -Notes "Self-signed certificate the integration runner mints for jim.web's HTTPS listener." `
    -PassThru
Write-Host "  OK Trusted certificate (thumbprint: $($trusted.thumbprint))" -ForegroundColor Green

# Step 6: Seed the HR CSV
Write-TestStep "Step 6" "Seeding the HR CSV ($UserCount people, 1 group)"
Write-Scenario029HrCsv -People (New-Scenario029People -Count $UserCount) -Group (New-Scenario029Group)
Write-Host "  OK HR CSV seeded to $($script:Scenario029HrCsvPath)" -ForegroundColor Green

# Step 7: The HR CSV Connected System
Write-TestStep "Step 7" "Creating the HR CSV Connected System"
$hrSystem = New-JIMConnectedSystem `
    -Name $script:Scenario029HrSystemName `
    -Description "Authoritative source of people and the group for the SCIM round trip" `
    -ConnectorDefinitionId $fileConnector.id `
    -PassThru
$fileConnectorFull = Get-JIMConnectorDefinition -Id $fileConnector.id
Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues @{
    (Get-ConnectorSettingId $fileConnectorFull "File Path")          = @{ stringValue = $script:Scenario029HrCsvPath }
    (Get-ConnectorSettingId $fileConnectorFull "Delimiter")          = @{ stringValue = "," }
    (Get-ConnectorSettingId $fileConnectorFull "Object Type Column") = @{ stringValue = "objectType" }
} | Out-Null
Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
$hrGroupType = $hrObjectTypes | Where-Object { $_.name -eq "group" }
if (-not $hrPersonType -or -not $hrGroupType) { throw "The HR schema did not produce both 'person' and 'group' object types." }
$membersAttribute = Get-CsAttribute $hrGroupType "members"
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -AttributeId $membersAttribute.id -Plurality MultiValued | Out-Null
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -AttributeId $membersAttribute.id -Type Reference | Out-Null
foreach ($type in @($hrPersonType, $hrGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrPersonType -ExternalId "accountName" `
    -Names @("accountName", "firstName", "lastName", "email", "department", "active")
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrGroupType -ExternalId "accountName" `
    -Names @("accountName", "displayName", "members")
Write-Host "  OK HR CSV system configured (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 8: The Client
Write-TestStep "Step 8" "Creating the Client Connected System"
$clientSystem = New-JIMConnectedSystem `
    -Name $script:Scenario029ClientSystemName `
    -Description "The SCIM 2.0 Client Connector, provisioning this JIM's own SCIM endpoint" `
    -ConnectorDefinitionId $clientConnector.id `
    -PassThru
$clientConnectorFull = Get-JIMConnectorDefinition -Id $clientConnector.id
# Setting the values tests the connection: discovery over HTTPS with the key, so a certificate JIM does not trust or a
# key the endpoint refuses fails here, not three steps later.
Set-JIMConnectedSystem -Id $clientSystem.id -SettingValues @{
    (Get-ConnectorSettingId $clientConnectorFull "Base URL")               = @{ stringValue = $baseUrl }
    (Get-ConnectorSettingId $clientConnectorFull "Authentication Method")  = @{ stringValue = "Static Bearer Token" }
    (Get-ConnectorSettingId $clientConnectorFull "Bearer Token")           = @{ stringValue = $clientKey.key }
    (Get-ConnectorSettingId $clientConnectorFull "Certificate Validation") = @{ stringValue = "Full Validation" }
} | Out-Null
Import-JIMConnectedSystemSchema -Id $clientSystem.id | Out-Null
$clientTypes = Get-ScimObjectTypes -ConnectedSystemId $clientSystem.id -Label "Client"
Select-CsAttributes -ConnectedSystemId $clientSystem.id -ObjectType $clientTypes.User -ExternalId "id" -Names $userAttributes
Select-CsAttributes -ConnectedSystemId $clientSystem.id -ObjectType $clientTypes.Group -ExternalId "id" -Names $groupAttributes
# JIM's endpoint supports entity tags, so the schema import must have selected and locked meta.version (#2404).
foreach ($type in @($clientTypes.User, $clientTypes.Group)) {
    $metaVersion = Get-CsAttribute $type "meta.version"
    if (-not $metaVersion.selected -or -not $metaVersion.selectionLocked) {
        throw "The Client's '$($type.name)' meta.version is not selected and locked (selected: $($metaVersion.selected), locked: $($metaVersion.selectionLocked)), so exports would carry no If-Match."
    }
}
Write-Host "  OK Client configured, connection verified and schema read from the endpoint's discovery (ID: $($clientSystem.id))" -ForegroundColor Green

# Step 9: Resolve the Metaverse
Write-TestStep "Step 9" "Resolving the Metaverse"
$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvGroupType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "Group" } | Select-Object -First 1
if (-not $mvUserType -or -not $mvGroupType) { throw "Metaverse 'User' or 'Group' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)
function Get-MvAttribute {
    param([string]$Name)
    $attribute = $mvAttributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "Metaverse attribute '$Name' not found." }
    return $attribute
}
if (-not ($mvAttributes | Where-Object { $_.name -eq $script:Scenario029EnabledMvAttribute })) {
    $mvAttributes = @($mvAttributes) + (New-JIMMetaverseAttribute -Name $script:Scenario029EnabledMvAttribute -Type Boolean -ObjectTypeIds @($mvUserType.id))
}
Write-Host "  OK Metaverse 'User' and 'Group' resolved" -ForegroundColor Green

# Step 10: Object Matching Rules
Write-TestStep "Step 10" "Configuring Object Matching Rules"
New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $hrPersonType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $hrGroupType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
# What arrives at the endpoint joins the people and the group it came from.
New-JIMMatchingRule -ConnectedSystemId $serviceProviderSystem.id -ObjectTypeId $serviceProviderTypes.User.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $serviceProviderTypes.User "userName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMMatchingRule -ConnectedSystemId $serviceProviderSystem.id -ObjectTypeId $serviceProviderTypes.Group.id -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $serviceProviderTypes.Group "displayName").id -TargetMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null
Write-Host "  OK Matching rules created" -ForegroundColor Green

# Step 11: Synchronisation Rules
Write-TestStep "Step 11" "Creating Synchronisation Rules"
$hrPeopleRule = New-JIMSyncRule -Name $script:Scenario029HrPeopleRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "firstName";   Mv = "First Name" },
    @{ Cs = "lastName";    Mv = "Last Name" },
    @{ Cs = "email";       Mv = "Email" },
    @{ Cs = "department";  Mv = "Department" },
    @{ Cs = "active";      Mv = $script:Scenario029EnabledMvAttribute })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrPeopleRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrPersonType $flow.Cs).id | Out-Null
}
$hrGroupsRule = New-JIMSyncRule -Name $script:Scenario029HrGroupsRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "displayName"; Mv = "Display Name" },
    @{ Cs = "members";     Mv = "Static Members" })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrGroupsRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrGroupType $flow.Cs).id | Out-Null
}

$userExportRule = New-JIMSyncRule -Name $script:Scenario029UserExportRuleName -ConnectedSystemId $clientSystem.id `
    -ConnectedSystemObjectTypeId $clientTypes.User.id -MetaverseObjectTypeId $mvUserType.id -Direction Export -ProvisionToConnectedSystem -PassThru
foreach ($flow in @(
    @{ Mv = "Account Name"; Cs = "userName" },
    @{ Mv = "First Name";   Cs = "name.givenName" },
    @{ Mv = "Last Name";    Cs = "name.familyName" },
    @{ Mv = "Email";        Cs = "emails.work" },
    @{ Mv = "Department";   Cs = "enterpriseUser.department" },
    @{ Mv = $script:Scenario029EnabledMvAttribute; Cs = "active" })) {
    New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $clientTypes.User $flow.Cs).id `
        -SourceMetaverseAttributeId (Get-MvAttribute $flow.Mv).id | Out-Null
}
# Enabled people only, so a leaver set inactive leaves the rule's scope and is deleted from the endpoint.
$userScope = New-JIMScopingCriteriaGroup -SyncRuleId $userExportRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $userExportRule.id -GroupId $userScope.id `
    -MetaverseAttributeId (Get-MvAttribute $script:Scenario029EnabledMvAttribute).id -ComparisonType Equals -BoolValue $true | Out-Null
Set-JIMSyncRule -Id $userExportRule.id -OutboundDeprovisionAction Delete | Out-Null

$groupExportRule = New-JIMSyncRule -Name $script:Scenario029GroupExportRuleName -ConnectedSystemId $clientSystem.id `
    -ConnectedSystemObjectTypeId $clientTypes.Group.id -MetaverseObjectTypeId $mvGroupType.id -Direction Export -ProvisionToConnectedSystem -PassThru
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $clientTypes.Group "displayName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $clientTypes.Group "members").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Static Members").id | Out-Null

# The Service Provider joins what arrives; only Job Title flows in, which nothing but an identity provider sets.
$inboundUsersRule = New-JIMSyncRule -Name $script:Scenario029InboundUsersRuleName -ConnectedSystemId $serviceProviderSystem.id `
    -ConnectedSystemObjectTypeId $serviceProviderTypes.User.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -PassThru
New-JIMSyncRuleMapping -SyncRuleId $inboundUsersRule.id -TargetMetaverseAttributeId (Get-MvAttribute "Job Title").id `
    -SourceConnectedSystemAttributeId (Get-CsAttribute $serviceProviderTypes.User "title").id | Out-Null
New-JIMSyncRule -Name $script:Scenario029InboundGroupsRuleName -ConnectedSystemId $serviceProviderSystem.id `
    -ConnectedSystemObjectTypeId $serviceProviderTypes.Group.id -MetaverseObjectTypeId $mvGroupType.id -Direction Import | Out-Null
Write-Host "  OK HR projects; the Client provisions enabled people and the group; the Service Provider joins both" -ForegroundColor Green

# Step 12: Run Profiles
Write-TestStep "Step 12" "Creating Run Profiles"
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Import" -RunType FullImport -FilePath $script:Scenario029HrCsvPath | Out-Null
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
# A small page size makes the client page through the endpoint even at this population.
New-JIMRunProfile -ConnectedSystemId $clientSystem.id -Name "Full Import" -RunType FullImport -PageSize 4 | Out-Null
New-JIMRunProfile -ConnectedSystemId $clientSystem.id -Name "Delta Import" -RunType DeltaImport -PageSize 4 | Out-Null
New-JIMRunProfile -ConnectedSystemId $clientSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $clientSystem.id -Name "Export" -RunType Export | Out-Null
New-JIMRunProfile -ConnectedSystemId $serviceProviderSystem.id -Name "Full Import" -RunType FullImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $serviceProviderSystem.id -Name "Delta Import" -RunType DeltaImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $serviceProviderSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $serviceProviderSystem.id -Name "Delta Synchronisation" -RunType DeltaSynchronisation | Out-Null
Write-Host "  OK Run Profiles created" -ForegroundColor Green

# Step 13: The Schedule that brings pushed changes in
Write-TestStep "Step 13" "Creating the inbound Schedule"
$schedule = New-JIMSchedule -Name $script:Scenario029ScheduleName -TriggerType Manual -Enabled -PassThru
foreach ($runProfileName in @("Delta Import", "Delta Synchronisation")) {
    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile -ConnectedSystemName $script:Scenario029ServiceProviderSystemName `
        -RunProfileName $runProfileName | Out-Null
}
Write-Host "  OK Schedule '$($script:Scenario029ScheduleName)' (Delta Import, then Delta Synchronisation)" -ForegroundColor Green

Write-Host ""
Write-Host "Scenario 029 setup complete." -ForegroundColor Green
