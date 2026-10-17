# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 028: the SCIM 2.0 Client Connector against Keycloak's SCIM API

.DESCRIPTION
    Configures Keycloak (the keycloak-scim container) and JIM for the scenario (#2349).

    Keycloak is configured with kcadm.sh inside the container, over its in-container HTTP listener, so nothing on
    the host needs to reach it:

      - a realm with the SCIM API turned on;
      - a confidential client whose service account holds realm-management manage-users, granted both as a
        service account role and as a client scope mapping, with Full Scope Allowed off and an audience mapper
        naming the realm's SCIM Base URL exactly as JIM calls it. A role missing from either place, or an
        audience that does not match, fails every call;
      - an account holding a realm-management role, which Keycloak protects from every change made through SCIM.

    JIM gets two Connected Systems, as in Scenario 015:

      - an HR CSV source (File Connector) holding both people and groups, told apart by its objectType column.
        A group's members are a multi-valued Reference to the people's accountName, so the Metaverse groups carry
        member references for JIM to export. One group is large enough that changing its membership takes more
        than Keycloak's 100 PATCH operations;
      - the Keycloak realm's SCIM API, over HTTPS with the certificate the container minted, authenticated with
        OAuth 2.0 Client Credentials. Users and Groups are provisioned to it; userName and email are lowercased
        on the way, because Keycloak lowercases them and an export JIM cannot read back never confirms.

    Change Detection is left on Auto-detect on purpose: the scenario asserts that JIM recognises Keycloak and reads
    everything on a Delta Import, saying why.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER KeycloakContainer
    The Keycloak container, which kcadm.sh runs inside.

.PARAMETER KeycloakHostname
    The name JIM reaches Keycloak by, which the certificate is issued for and Keycloak's frontend hostname is.

.PARAMETER CertificatePath
    Keycloak's public certificate, copied out of the container for JIM to trust.

.PARAMETER UserCount
    How many people the HR CSV holds. The large group holds most of them, so this must exceed 100.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Setup-Scenario-028.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..." -CertificatePath ./keycloak-scim.pem
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$KeycloakContainer = "keycloak-scim",

    [Parameter(Mandatory=$false)]
    [string]$KeycloakHostname = "keycloak-scim",

    [Parameter(Mandatory=$false)]
    [string]$CertificatePath,

    [Parameter(Mandatory=$false)]
    [ValidateRange(110, 1000)]
    [int]$UserCount = 120,

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
. "$PSScriptRoot/utils/Scenario-028-Data.ps1"

$realm = $script:Scenario028Realm
$clientId = $script:Scenario028ClientId
$clientSecret = $script:Scenario028ClientSecret
$scimBaseUrl = "https://${KeycloakHostname}:8443/realms/$realm/scim/v2"
$tokenEndpointUrl = "https://${KeycloakHostname}:8443/realms/$realm/protocol/openid-connect/token"

$hrSystemName = $script:Scenario028HrSystemName
$keycloakSystemName = $script:Scenario028KeycloakSystemName
$certificateName = "Keycloak SCIM (Scenario 028)"
$hrCsvFilePath = $script:Scenario028HrCsvPath

Write-TestSection "Scenario 028 Setup: SCIM 2.0 Client Connector against Keycloak"
Write-Host "Keycloak SCIM API: $scimBaseUrl" -ForegroundColor Gray
Write-Host "People:            $UserCount" -ForegroundColor Gray
Write-Host ""

# Step 1: Configure Keycloak
Write-TestStep "Step 1" "Configuring the Keycloak realm, client and protected account"

$kcadmConfig = "/tmp/kcadm.config"
function Invoke-Kcadm {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $output = docker exec $KeycloakContainer /opt/keycloak/bin/kcadm.sh @Arguments --config $kcadmConfig 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "kcadm.sh $($Arguments[0..1] -join ' ') failed: $output"
    }
    return $output
}

$login = docker exec $KeycloakContainer /opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 `
    --realm master --user admin --password admin --config $kcadmConfig 2>&1
if ($LASTEXITCODE -ne 0) { throw "kcadm.sh could not sign in to Keycloak: $login" }

# A realm left by a previous run on the same container is replaced, so every run starts from Keycloak's own defaults.
$existingRealms = (Invoke-Kcadm get realms --fields realm --format csv --noquotes) -split "`n" | ForEach-Object { $_.Trim() }
if ($realm -in $existingRealms) {
    Invoke-Kcadm delete "realms/$realm" | Out-Null
    Write-Host "  Removed the realm left by a previous run" -ForegroundColor Gray
}

Invoke-Kcadm create realms -s "realm=$realm" -s enabled=true -s scimApiEnabled=true | Out-Null

$audienceMapper = @(
    @{
        name = "scim-audience"
        protocol = "openid-connect"
        protocolMapper = "oidc-audience-mapper"
        config = @{ "included.custom.audience" = $scimBaseUrl; "access.token.claim" = "true" }
    }
) | ConvertTo-Json -Depth 5 -Compress -AsArray
Invoke-Kcadm create clients -r $realm -s "clientId=$clientId" -s enabled=true -s publicClient=false -s "secret=$clientSecret" `
    -s serviceAccountsEnabled=true -s standardFlowEnabled=false -s directAccessGrantsEnabled=false -s fullScopeAllowed=false `
    -s "protocolMappers=$audienceMapper" | Out-Null

$clientUuid = (Invoke-Kcadm get clients -r $realm -q "clientId=$clientId" --fields id --format csv --noquotes | Select-Object -Last 1).Trim()
$realmManagementUuid = (Invoke-Kcadm get clients -r $realm -q clientId=realm-management --fields id --format csv --noquotes | Select-Object -Last 1).Trim()
Invoke-Kcadm add-roles -r $realm --uusername "service-account-$clientId" --cclientid realm-management --rolename manage-users | Out-Null
$manageUsersRole = (Invoke-Kcadm get "clients/$realmManagementUuid/roles/manage-users" -r $realm) -join "`n" | ConvertFrom-Json
$scopeMappingBody = @($manageUsersRole) | ConvertTo-Json -Depth 5 -Compress -AsArray
Invoke-Kcadm create "clients/$clientUuid/scope-mappings/clients/$realmManagementUuid" -r $realm -b $scopeMappingBody | Out-Null

# Holding any realm-management role makes an account one Keycloak refuses every SCIM write to.
Invoke-Kcadm create users -r $realm -s "username=$($script:Scenario028ProtectedUserName)" -s enabled=true | Out-Null
Invoke-Kcadm add-roles -r $realm --uusername $script:Scenario028ProtectedUserName --cclientid realm-management --rolename view-users | Out-Null

Write-Host "  OK Realm '$realm' with the SCIM API on, client '$clientId', and protected account '$($script:Scenario028ProtectedUserName)'" -ForegroundColor Green

# Step 2: Import JIM PowerShell module and connect
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop

if (-not $ApiKey) {
    throw "API key required for authentication"
}
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

# Step 3: Clean up anything a previous run left
Write-TestStep "Step 3" "Cleaning up existing configuration"

foreach ($systemName in @($keycloakSystemName, $hrSystemName)) {
    $existing = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $systemName }
    foreach ($system in $existing) {
        Remove-JIMConnectedSystem -Id $system.id -DeleteImmediately -Force | Out-Null
        Write-Host "  Removed existing '$systemName'" -ForegroundColor Gray
    }
}
$existingCertificate = @(Get-JIMCertificate -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $certificateName }
foreach ($certificate in $existingCertificate) {
    Remove-JIMCertificate -Id $certificate.id -Force | Out-Null
    Write-Host "  Removed stale trusted certificate from a previous run" -ForegroundColor Gray
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 4: Trust Keycloak's certificate
Write-TestStep "Step 4" "Trusting Keycloak's certificate"

if (-not $CertificatePath) {
    throw "CertificatePath is required. The keycloak-scim container mints a certificate at every start; without trusting it, JIM cannot validate the connection, and skipping validation would test nothing."
}
if (-not (Test-Path $CertificatePath)) {
    throw "Keycloak's certificate was not found at '$CertificatePath'. Has the container started?"
}
$certificateBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $CertificatePath).Path)
$trusted = Add-JIMCertificate `
    -Name $certificateName `
    -CertificateData $certificateBytes `
    -Notes "Self-signed certificate minted by the keycloak-scim container for this run." `
    -PassThru
Write-Host "  OK Trusted certificate (ID: $($trusted.id), thumbprint: $($trusted.thumbprint))" -ForegroundColor Green

# Step 5: Seed the HR CSV
Write-TestStep "Step 5" "Seeding the HR CSV ($UserCount people, $($script:Scenario028Groups.Count) groups)"

Write-Scenario028HrCsv -People (New-Scenario028People -Count $UserCount) -Groups (New-Scenario028Groups -UserCount $UserCount)
Write-Host "  OK HR CSV seeded to $hrCsvFilePath" -ForegroundColor Green

# Step 6: Resolve connector definitions
Write-TestStep "Step 6" "Resolving connector definitions"

$connectors = Get-JIMConnectorDefinition
$fileConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
$scimConnector = $connectors | Where-Object { $_.name -eq "JIM SCIM 2.0 Client Connector" }
if (-not $fileConnector) { throw "JIM File Connector definition not found." }
if (-not $scimConnector) { throw "JIM SCIM 2.0 Client Connector definition not found. Has seeding run?" }
Write-Host "  OK File connector (ID: $($fileConnector.id)), SCIM connector (ID: $($scimConnector.id))" -ForegroundColor Green

function Get-ConnectorSettingId {
    param($Connector, [string]$Name)
    $setting = $Connector.settings | Where-Object { $_.name -eq $Name }
    if (-not $setting) { throw "Connector setting '$Name' not found on '$($Connector.name)'." }
    return $setting.id
}

function Get-CsAttribute {
    param($ObjectType, [string]$Name)
    $attribute = $ObjectType.attributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "Attribute '$Name' not found on '$($ObjectType.name)'." }
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

# Step 7: The HR CSV Connected System
Write-TestStep "Step 7" "Creating the HR CSV Connected System"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "Authoritative source of people and groups for the Keycloak SCIM integration scenario" `
    -ConnectorDefinitionId $fileConnector.id `
    -PassThru
$fileConnectorFull = Get-JIMConnectorDefinition -Id $fileConnector.id
Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues @{
    (Get-ConnectorSettingId $fileConnectorFull "File Path")          = @{ stringValue = $hrCsvFilePath }
    (Get-ConnectorSettingId $fileConnectorFull "Delimiter")          = @{ stringValue = "," }
    (Get-ConnectorSettingId $fileConnectorFull "Object Type Column") = @{ stringValue = "objectType" }
} | Out-Null
Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null

$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
$hrGroupType = $hrObjectTypes | Where-Object { $_.name -eq "group" }
if (-not $hrPersonType -or -not $hrGroupType) { throw "The HR schema did not produce both 'person' and 'group' object types." }

# A delimited file names no types, so the members column is declared a Reference: its values are the accountName of
# the people in the same file. The repeated header has already made it multi-valued.
$membersAttribute = Get-CsAttribute $hrGroupType "members"
if ($membersAttribute.attributePlurality -ne "MultiValued") {
    throw "The HR 'members' column was not discovered as multi-valued; the CSV header must repeat it."
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -AttributeId $membersAttribute.id -Type Reference | Out-Null

foreach ($type in @($hrPersonType, $hrGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrPersonType -ExternalId "accountName" `
    -Names @("accountName", "firstName", "lastName", "email", "department", "active")
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrGroupType -ExternalId "accountName" `
    -Names @("accountName", "displayName", "members")
Write-Host "  OK HR CSV system configured (ID: $($hrSystem.id)); members typed as a multi-valued Reference" -ForegroundColor Green

# Step 8: The Keycloak SCIM Connected System
Write-TestStep "Step 8" "Creating the Keycloak SCIM Connected System"

$keycloakSystem = New-JIMConnectedSystem `
    -Name $keycloakSystemName `
    -Description "Keycloak's built-in SCIM 2.0 API, for integration testing" `
    -ConnectorDefinitionId $scimConnector.id `
    -PassThru
$scimConnectorFull = Get-JIMConnectorDefinition -Id $scimConnector.id

# Setting the values tests the connection, which proves the realm, the client's roles and the audience mapper all
# line up: a token Keycloak would refuse fails here, not three steps later.
Set-JIMConnectedSystem -Id $keycloakSystem.id -SettingValues @{
    (Get-ConnectorSettingId $scimConnectorFull "Base URL")               = @{ stringValue = $scimBaseUrl }
    (Get-ConnectorSettingId $scimConnectorFull "Authentication Method")  = @{ stringValue = "OAuth 2.0 Client Credentials" }
    (Get-ConnectorSettingId $scimConnectorFull "Token Endpoint URL")     = @{ stringValue = $tokenEndpointUrl }
    (Get-ConnectorSettingId $scimConnectorFull "Client ID")              = @{ stringValue = $clientId }
    (Get-ConnectorSettingId $scimConnectorFull "Client Secret")          = @{ stringValue = $clientSecret }
    (Get-ConnectorSettingId $scimConnectorFull "Certificate Validation") = @{ stringValue = "Full Validation" }
} | Out-Null
Write-Host "  OK Connected System configured and connectivity verified (ID: $($keycloakSystem.id))" -ForegroundColor Green

# Step 9: Import the SCIM schema
Write-TestStep "Step 9" "Importing Keycloak's SCIM schema"

Import-JIMConnectedSystemSchema -Id $keycloakSystem.id | Out-Null
$scimObjectTypes = Get-JIMConnectedSystem -Id $keycloakSystem.id -ObjectTypes
$scimUserType = $scimObjectTypes | Where-Object { $_.name -eq "User" }
$scimGroupType = $scimObjectTypes | Where-Object { $_.name -eq "Group" }
if (-not $scimUserType -or -not $scimGroupType) { throw "Keycloak published no 'User' or 'Group' resource type." }

# Keycloak publishes members and emails with nothing inside them; JIM fills them in from RFC 7643 (#2094). Without
# that, neither would be here at all.
foreach ($expected in @(@{ Type = $scimGroupType; Name = "members" }, @{ Type = $scimUserType; Name = "emails.work" })) {
    if (-not ($expected.Type.attributes | Where-Object { $_.name -eq $expected.Name })) {
        throw "The imported $($expected.Type.name) schema is missing '$($expected.Name)'; JIM did not fill in Keycloak's empty complex attribute."
    }
}
Write-Host "  OK Schema imported: User ($($scimUserType.attributes.Count) attributes), Group ($($scimGroupType.attributes.Count) attributes)" -ForegroundColor Green

# Step 10: Select SCIM object types and attributes
Write-TestStep "Step 10" "Selecting SCIM object types and attributes"

foreach ($type in @($scimUserType, $scimGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $keycloakSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $keycloakSystem.id -ObjectType $scimUserType -ExternalId "id" `
    -Names @("id", "userName", "active", "name.givenName", "name.familyName", "emails.work")
Select-CsAttributes -ConnectedSystemId $keycloakSystem.id -ObjectType $scimGroupType -ExternalId "id" `
    -Names @("id", "displayName", "members")
Write-Host "  OK Selected User and Group attributes (SCIM id is the External ID)" -ForegroundColor Green

# Step 11: Resolve the Metaverse
Write-TestStep "Step 11" "Resolving Metaverse object types"

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

# Whether an account is enabled has no seeded Metaverse attribute of its own here, so the scenario adds one.
if (-not ($mvAttributes | Where-Object { $_.name -eq $script:Scenario028EnabledMvAttribute })) {
    $mvAttributes = @($mvAttributes) + (New-JIMMetaverseAttribute -Name $script:Scenario028EnabledMvAttribute -Type Boolean -ObjectTypeIds @($mvUserType.id))
    Write-Host "  Created the Metaverse attribute '$($script:Scenario028EnabledMvAttribute)'" -ForegroundColor Gray
}
Write-Host "  OK Metaverse 'User' and 'Group' resolved" -ForegroundColor Green

# Step 12: Object Matching Rules
Write-TestStep "Step 12" "Configuring Object Matching Rules"

New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $hrPersonType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $hrGroupType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
# Keycloak's userName is not case-exact, so it matches the HR accountName whatever case Keycloak stored it in. This is
# what joins the protected account, which exists in Keycloak before JIM knows of it.
New-JIMMatchingRule -ConnectedSystemId $keycloakSystem.id -ObjectTypeId $scimUserType.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $scimUserType "userName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
Write-Host "  OK Matching rules created" -ForegroundColor Green

# Step 13: Inbound Synchronisation Rules
Write-TestStep "Step 13" "Creating inbound Synchronisation Rules"

$hrPeopleRule = New-JIMSyncRule -Name $script:Scenario028HrPeopleRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "firstName";   Mv = "First Name" },
    @{ Cs = "lastName";    Mv = "Last Name" },
    @{ Cs = "email";       Mv = "Email" },
    @{ Cs = "department";  Mv = "Department" },
    @{ Cs = "active";      Mv = $script:Scenario028EnabledMvAttribute })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrPeopleRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrPersonType $flow.Cs).id | Out-Null
}

$hrGroupsRule = New-JIMSyncRule -Name $script:Scenario028HrGroupsRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "displayName"; Mv = "Display Name" },
    @{ Cs = "members";     Mv = "Static Members" })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrGroupsRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrGroupType $flow.Cs).id | Out-Null
}

# Keycloak contributes nothing; its Users rule exists to join an account Keycloak already holds (the protected one).
New-JIMSyncRule -Name $script:Scenario028KeycloakJoinRuleName -ConnectedSystemId $keycloakSystem.id `
    -ConnectedSystemObjectTypeId $scimUserType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import | Out-Null
Write-Host "  OK Inbound rules created (HR projects people and groups; Keycloak Users join)" -ForegroundColor Green

# Step 14: Outbound Synchronisation Rules
Write-TestStep "Step 14" "Creating outbound Synchronisation Rules"

$userExportRule = New-JIMSyncRule -Name $script:Scenario028UserExportRuleName -ConnectedSystemId $keycloakSystem.id `
    -ConnectedSystemObjectTypeId $scimUserType.id -MetaverseObjectTypeId $mvUserType.id -Direction Export -ProvisionToConnectedSystem -PassThru
# Keycloak stores userName and email in lower case. Flowed in mixed case they would never read back as sent, so the
# export would never confirm (#2094).
New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $scimUserType "userName").id `
    -Expression 'Lower(mv["Account Name"])' | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $scimUserType "emails.work").id `
    -Expression 'Lower(mv["Email"])' | Out-Null
foreach ($flow in @(
    @{ Mv = "First Name"; Cs = "name.givenName" },
    @{ Mv = "Last Name";  Cs = "name.familyName" },
    @{ Mv = $script:Scenario028EnabledMvAttribute; Cs = "active" })) {
    New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $scimUserType $flow.Cs).id `
        -SourceMetaverseAttributeId (Get-MvAttribute $flow.Mv).id | Out-Null
}
$userScope = New-JIMScopingCriteriaGroup -SyncRuleId $userExportRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $userExportRule.id -GroupId $userScope.id `
    -MetaverseAttributeId (Get-MvAttribute "Department").id -ComparisonType Equals -StringValue $script:Scenario028InScopeDepartment | Out-Null
Set-JIMSyncRule -Id $userExportRule.id -OutboundDeprovisionAction Delete | Out-Null

$groupExportRule = New-JIMSyncRule -Name $script:Scenario028GroupExportRuleName -ConnectedSystemId $keycloakSystem.id `
    -ConnectedSystemObjectTypeId $scimGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Export -ProvisionToConnectedSystem -PassThru
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $scimGroupType "displayName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $scimGroupType "members").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Static Members").id | Out-Null
Write-Host "  OK Outbound rules created (Users in '$($script:Scenario028InScopeDepartment)' and every Group provision to Keycloak)" -ForegroundColor Green

# Step 15: Run Profiles
Write-TestStep "Step 15" "Creating Run Profiles"

New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Import" -RunType FullImport -FilePath $hrCsvFilePath | Out-Null
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -Name "Full Import" -RunType FullImport -PageSize 50 | Out-Null
New-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -Name "Delta Import" -RunType DeltaImport -PageSize 50 | Out-Null
New-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -Name "Export" -RunType Export | Out-Null
Write-Host "  OK Run Profiles created" -ForegroundColor Green

Write-Host ""
Write-Host "Scenario 028 setup complete." -ForegroundColor Green
