# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 036: the Microsoft Entra ID Connector with Entra ID as a target

.DESCRIPTION
    Configures JIM for the scenario (#361), around a Connected System that Register-JIMEntraIdConnector has already
    created (the scenario script does that, as the CI app, before calling this):

      - an HR CSV source (File Connector) holding both people and groups, told apart by its objectType column. A
        person's manager and a group's members are References to accountNames in the same file, and one group's members
        are the other groups, so JIM exports a nested group;
      - the Entra ID Connected System's schema imported, and users and groups selected, with Entra ID's id as the
        External ID;
      - inbound rules projecting people and groups from HR, and outbound rules provisioning them to Entra ID. Users are
        in scope unless their department is the leaver department; leaving scope disables them, the user rule's
        Deprovisioning Action being Disable (the scenario later switches it to Delete);
      - an Initial Password on the user export rule, generated from the policy JIM discovered in the tenant, so every
        user JIM creates is given a password through the password channel after the create.

    Nothing here writes to Entra ID: the scenario's runs do that.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ConnectedSystemId
    The Entra ID Connected System Register-JIMEntraIdConnector created.

.PARAMETER Prefix
    The run prefix (jimit-<run id>-) every person and group is named with.

.PARAMETER Domain
    The tenant domain every user principal name is in.

.PARAMETER InitialPassword
    Whether the user export rule gives every user it creates an Initial Password (default: true). Scenario 039, the scale
    run, leaves it out: a password is another write per user, and Scenario 036 proves it.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Setup-Scenario-036.ps1 -ApiKey "jim_abc123..." -ConnectedSystemId 2 -Prefix "jimit-a1b2c3-"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$true)]
    [int]$ConnectedSystemId,

    [Parameter(Mandatory=$true)]
    [string]$Prefix,

    [Parameter(Mandatory=$false)]
    [string]$Domain,

    [Parameter(Mandatory=$false)]
    [bool]$InitialPassword = $true,

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
. "$PSScriptRoot/utils/Scenario-036-Data.ps1"

if (-not $Domain) { $Domain = $script:Scenario036DefaultDomain }
$hrSystemName = $script:Scenario036HrSystemName
$hrCsvFilePath = $script:Scenario036HrCsvPath

Write-TestSection "Scenario 036 Setup: Microsoft Entra ID Connector, Entra ID as a target"
Write-Host "Run prefix: $Prefix" -ForegroundColor Gray
Write-Host ""

# Step 1: Import JIM PowerShell module and connect
Write-TestStep "Step 1" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) { throw "API key required for authentication" }
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Get-Module JIM)) { Import-Module $modulePath -Force -ErrorAction Stop }
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

# Step 2: Clean up anything a previous run left
Write-TestStep "Step 2" "Cleaning up existing configuration"

foreach ($system in @(@(Get-JIMConnectedSystem -ErrorAction SilentlyContinue) | Where-Object { $_.name -eq $hrSystemName })) {
    Remove-JIMConnectedSystem -Id $system.id -DeleteImmediately -Force | Out-Null
    Write-Host "  Removed existing '$hrSystemName'" -ForegroundColor Gray
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 3: Seed the HR CSV
$people = New-Scenario036People -Prefix $Prefix -Domain $Domain
$groups = New-Scenario036Groups -Prefix $Prefix -People $people
Write-TestStep "Step 3" "Seeding the HR CSV ($($people.Count) people, $($groups.Count) groups)"
Write-Scenario036HrCsv -People $people -Groups $groups
Write-Host "  OK HR CSV seeded to $hrCsvFilePath" -ForegroundColor Green

# Step 4: Resolve connector definitions
Write-TestStep "Step 4" "Resolving connector definitions"

$connectors = Get-JIMConnectorDefinition
$fileConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
$graphConnector = $connectors | Where-Object { $_.name -eq "JIM Microsoft Entra ID Connector" }
if (-not $fileConnector) { throw "JIM File Connector definition not found." }
if (-not $graphConnector) { throw "JIM Microsoft Entra ID Connector definition not found. Has seeding run?" }
Write-Host "  OK File connector (ID: $($fileConnector.id)), Entra ID connector (ID: $($graphConnector.id))" -ForegroundColor Green

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

# Step 5: The HR CSV Connected System
Write-TestStep "Step 5" "Creating the HR CSV Connected System"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "Authoritative source of people and groups for the Entra ID target scenario" `
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

# A delimited file names no types and no plurality. members is multi-valued, its values separated by the Multi-Value
# Delimiter; it and manager are References, their values the accountName of objects in the same file.
$membersAttribute = Get-CsAttribute $hrGroupType "members"
$multiValued = Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -AttributeId $membersAttribute.id -Plurality MultiValued -PassThru
if ($multiValued.attributePlurality -ne "MultiValued") {
    throw "The HR 'members' column is not multi-valued after setting it (found '$($multiValued.attributePlurality)')."
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -AttributeId $membersAttribute.id -Type Reference | Out-Null
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -AttributeId (Get-CsAttribute $hrPersonType "manager").id -Type Reference | Out-Null

foreach ($type in @($hrPersonType, $hrGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrPersonType -ExternalId "accountName" `
    -Names @("accountName", "firstName", "lastName", "displayName", "upn", "department", "jobTitle", "employeeId", "manager", "active")
Select-CsAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrGroupType -ExternalId "accountName" `
    -Names @("accountName", "displayName", "members")
Write-Host "  OK HR CSV system configured (ID: $($hrSystem.id)); manager and members typed as References" -ForegroundColor Green

# Step 6: The Entra ID Connected System
Write-TestStep "Step 6" "Configuring the Entra ID Connected System"

# The certificate was made seconds ago, and Entra ID's token service learns of it unevenly: one sign-in can succeed and
# the next be refused (AADSTS700027). Saving settings and reading the schema each sign in, so each waits that out.
function Invoke-WhileCertificatePropagates {
    param([Parameter(Mandatory)][string]$Description, [Parameter(Mandatory)][scriptblock]$Action, [int]$TimeoutSeconds = 180)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try { return & $Action }
        catch {
            if ("$($_.Exception.Message)" -notmatch 'AADSTS700027|AADSTS7000215|AADSTS700016' -or [DateTime]::UtcNow -ge $deadline) { throw }
            Write-Host "  Entra ID does not yet know the new certificate everywhere; $Description again in 15 seconds" -ForegroundColor DarkGray
            Start-Sleep -Seconds 15
        }
    }
}

$entraSystem = Get-JIMConnectedSystem -Id $ConnectedSystemId
Invoke-WhileCertificatePropagates -Description "reading the schema" -Action { Import-JIMConnectedSystemSchema -Id $entraSystem.id | Out-Null }
$entraObjectTypes = Get-JIMConnectedSystem -Id $entraSystem.id -ObjectTypes
$entraUserType = $entraObjectTypes | Where-Object { $_.name -eq "user" }
$entraGroupType = $entraObjectTypes | Where-Object { $_.name -eq "group" }
if (-not $entraUserType -or -not $entraGroupType) { throw "The Entra ID schema has no 'user' or 'group' object type." }

foreach ($type in @($entraUserType, $entraGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $entraSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $entraSystem.id -ObjectType $entraUserType -ExternalId "id" `
    -Names @("id", "userPrincipalName", "displayName", "mailNickname", "accountEnabled", "givenName", "surname", "department",
        "jobTitle", "employeeId", "manager", "sourceOfAuthority")
Select-CsAttributes -ConnectedSystemId $entraSystem.id -ObjectType $entraGroupType -ExternalId "id" `
    -Names @("id", "displayName", "mailNickname", "mailEnabled", "securityEnabled", "description", "members", "sourceOfAuthority")
Write-Host "  OK '$($entraSystem.name)' (ID: $($entraSystem.id)): user and group selected (id is the External ID)" -ForegroundColor Green

# Step 7: Resolve the Metaverse
Write-TestStep "Step 7" "Resolving Metaverse object types"

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
Write-Host "  OK Metaverse 'User' and 'Group' resolved" -ForegroundColor Green

# Step 8: Object Matching Rules
Write-TestStep "Step 8" "Configuring Object Matching Rules"

New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $hrPersonType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $hrGroupType "accountName").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
Write-Host "  OK Matching rules created" -ForegroundColor Green

# Step 9: Inbound Synchronisation Rules
Write-TestStep "Step 9" "Creating inbound Synchronisation Rules"

$hrPeopleRule = New-JIMSyncRule -Name $script:Scenario036HrPeopleRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "firstName";   Mv = "First Name" },
    @{ Cs = "lastName";    Mv = "Last Name" },
    @{ Cs = "displayName"; Mv = "Display Name" },
    @{ Cs = "upn";         Mv = "User Principal Name" },
    @{ Cs = "department";  Mv = "Department" },
    @{ Cs = "jobTitle";    Mv = "Job Title" },
    @{ Cs = "employeeId";  Mv = "Employee ID" },
    @{ Cs = "manager";     Mv = "Manager" },
    @{ Cs = "active";      Mv = "Account Enabled" })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrPeopleRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrPersonType $flow.Cs).id | Out-Null
}

$hrGroupsRule = New-JIMSyncRule -Name $script:Scenario036HrGroupsRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "accountName"; Mv = "Account Name" },
    @{ Cs = "displayName"; Mv = "Display Name" },
    @{ Cs = "members";     Mv = "Static Members" })) {
    New-JIMSyncRuleMapping -SyncRuleId $hrGroupsRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $hrGroupType $flow.Cs).id | Out-Null
}
# Entra ID has no inbound rule: the tenant's own accounts are imported and stay disconnected, out of every rule's scope.
Write-Host "  OK Inbound rules created (HR projects people and groups)" -ForegroundColor Green

# Step 10: Outbound Synchronisation Rules
Write-TestStep "Step 10" "Creating outbound Synchronisation Rules"

$userExportRule = New-JIMSyncRule -Name $script:Scenario036UserExportRuleName -ConnectedSystemId $entraSystem.id `
    -ConnectedSystemObjectTypeId $entraUserType.id -MetaverseObjectTypeId $mvUserType.id -Direction Export -ProvisionToConnectedSystem -PassThru
foreach ($flow in @(
    @{ Mv = "User Principal Name"; Cs = "userPrincipalName" },
    @{ Mv = "Display Name";        Cs = "displayName" },
    @{ Mv = "Account Name";        Cs = "mailNickname" },
    @{ Mv = "Account Enabled";     Cs = "accountEnabled" },
    @{ Mv = "First Name";          Cs = "givenName" },
    @{ Mv = "Last Name";           Cs = "surname" },
    @{ Mv = "Department";          Cs = "department" },
    @{ Mv = "Job Title";           Cs = "jobTitle" },
    @{ Mv = "Employee ID";         Cs = "employeeId" },
    @{ Mv = "Manager";             Cs = "manager" })) {
    New-JIMSyncRuleMapping -SyncRuleId $userExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraUserType $flow.Cs).id `
        -SourceMetaverseAttributeId (Get-MvAttribute $flow.Mv).id | Out-Null
}
$userScope = New-JIMScopingCriteriaGroup -SyncRuleId $userExportRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $userExportRule.id -GroupId $userScope.id `
    -MetaverseAttributeId (Get-MvAttribute "Department").id -ComparisonType NotEquals -StringValue $script:Scenario036LeaverDepartment | Out-Null
# Disable first: the scenario's first leaver is disabled, and the second, after it switches to Delete, deleted.
Set-JIMSyncRule -Id $userExportRule.id -OutboundDeprovisionAction Disable | Out-Null

$groupExportRule = New-JIMSyncRule -Name $script:Scenario036GroupExportRuleName -ConnectedSystemId $entraSystem.id `
    -ConnectedSystemObjectTypeId $entraGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Export -ProvisionToConnectedSystem -PassThru
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "displayName").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Display Name").id | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "mailNickname").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "members").id `
    -SourceMetaverseAttributeId (Get-MvAttribute "Static Members").id | Out-Null
# Security groups: no mailbox, usable to grant access.
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "mailEnabled").id `
    -Expression 'false' | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "securityEnabled").id `
    -Expression 'true' | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $groupExportRule.id -TargetConnectedSystemAttributeId (Get-CsAttribute $entraGroupType "description").id `
    -Expression '"Provisioned by JIM integration Scenario 036"' | Out-Null
Write-Host "  OK Outbound rules created (users outside '$($script:Scenario036LeaverDepartment)', and every group, provision to Entra ID)" -ForegroundColor Green

# Step 11: Initial Password
if ($InitialPassword) {
    Write-TestStep "Step 11" "Enabling the Initial Password on the user export rule"

    # Generated from the policy JIM discovered when it read the schema, so no password is known to anyone. The account's
    # enabled state comes from HR through the accountEnabled flow, so the Initial Password leaves it alone.
    Set-JIMSyncRuleInitialPassword -Id $userExportRule.id -Enable -Source Discovered -ExpiryBehaviour RequireChangeAtNextSignIn `
        -EnableAccount $false -ChangeReason "Integration test: every user JIM creates in Entra ID is given an Initial Password" | Out-Null
    $initialPasswordSettings = Get-JIMSyncRuleInitialPassword -Id $userExportRule.id
    if (-not $initialPasswordSettings.enabled) { throw "The Initial Password is not enabled on '$($script:Scenario036UserExportRuleName)'." }
    Write-Host "  OK Initial Password enabled (Source: Discovered, Expiry: RequireChangeAtNextSignIn)" -ForegroundColor Green
}

# Step 12: Run Profiles
Write-TestStep "Step 12" "Creating Run Profiles"

New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Import" -RunType FullImport -FilePath $hrCsvFilePath | Out-Null
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Full Import" -RunType FullImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Export" -RunType Export | Out-Null
Write-Host "  OK Run Profiles created" -ForegroundColor Green

Write-Host ""
Write-Host "Scenario 036 setup complete." -ForegroundColor Green
