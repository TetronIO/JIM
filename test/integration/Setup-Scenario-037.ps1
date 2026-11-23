# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 037: the Microsoft Entra ID Connector with Entra ID as a source

.DESCRIPTION
    Configures JIM for the scenario (#361), around a Connected System that Register-JIMEntraIdConnector has already
    created (the scenario script does that, as the CI app, before calling this):

      - the Entra ID Connected System's schema imported, and users and groups selected, with Entra ID's id as the
        External ID and sourceOfAuthority read;
      - inbound rules projecting users and groups into the Metaverse, each scoped to display names carrying the run's
        prefix, so the tenant's own accounts are imported and never projected;
      - Object Matching Rules on the mail nickname, and deletion rules that delete a User or Group the moment Entra ID
        no longer holds it, so a Delta Import's deletions reach the Metaverse in the same run;
      - Run Profiles for Full and Delta Import and Synchronisation.

    Nothing here writes to Entra ID: the scenario seeds the tenant itself.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ConnectedSystemId
    The Entra ID Connected System Register-JIMEntraIdConnector created.

.PARAMETER Prefix
    The run prefix (jimit-<run id>-) every seeded user and group is named with.

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own data.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario uses no directory.

.EXAMPLE
    ./Setup-Scenario-037.ps1 -ApiKey "jim_abc123..." -ConnectedSystemId 1 -Prefix "jimit-a1b2c3-"
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
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $DirectoryConfig
$null = $Template

. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Scenario-037-Data.ps1"

Write-TestSection "Scenario 037 Setup: Microsoft Entra ID Connector, Entra ID as a source"
Write-Host "Run prefix: $Prefix" -ForegroundColor Gray
Write-Host ""

# Step 1: Import JIM PowerShell module and connect
Write-TestStep "Step 1" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) { throw "API key required for authentication" }
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
if (-not (Get-Module JIM)) { Import-Module $modulePath -Force -ErrorAction Stop }
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

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

# Step 2: The Entra ID Connected System
Write-TestStep "Step 2" "Configuring the Entra ID Connected System"

$entraSystem = Get-JIMConnectedSystem -Id $ConnectedSystemId
# The certificate was made moments ago. The Connector itself asks again while Entra ID is still applying it, for about
# half a minute; this waits out a slower tenant, as the setup command does.
$deadline = [DateTime]::UtcNow.AddMinutes(3)
while ($true) {
    try { Import-JIMConnectedSystemSchema -Id $entraSystem.id | Out-Null; break }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'AADSTS700027|AADSTS7000215|AADSTS700016' -or [DateTime]::UtcNow -ge $deadline) { throw }
        Write-Host "  Entra ID does not yet know the new certificate everywhere; reading the schema again in 15 seconds" -ForegroundColor DarkGray
        Start-Sleep -Seconds 15
    }
}
$entraObjectTypes = Get-JIMConnectedSystem -Id $entraSystem.id -ObjectTypes
$entraUserType = $entraObjectTypes | Where-Object { $_.name -eq "user" }
$entraGroupType = $entraObjectTypes | Where-Object { $_.name -eq "group" }
if (-not $entraUserType -or -not $entraGroupType) { throw "The Entra ID schema has no 'user' or 'group' object type." }

foreach ($type in @($entraUserType, $entraGroupType)) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $entraSystem.id -ObjectTypeId $type.id -Selected $true | Out-Null
}
Select-CsAttributes -ConnectedSystemId $entraSystem.id -ObjectType $entraUserType -ExternalId "id" `
    -Names @("id", "userPrincipalName", "displayName", "mailNickname", "accountEnabled", "givenName", "surname", "department",
        "jobTitle", "sourceOfAuthority")
Select-CsAttributes -ConnectedSystemId $entraSystem.id -ObjectType $entraGroupType -ExternalId "id" `
    -Names @("id", "displayName", "mailNickname", "mailEnabled", "securityEnabled", "members", "sourceOfAuthority")
Write-Host "  OK '$($entraSystem.name)' (ID: $($entraSystem.id)): user and group selected (id is the External ID)" -ForegroundColor Green

# Step 3: Resolve the Metaverse
Write-TestStep "Step 3" "Resolving Metaverse object types"

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

# Step 4: Object Matching Rules
Write-TestStep "Step 4" "Configuring Object Matching Rules"

New-JIMMatchingRule -ConnectedSystemId $entraSystem.id -ObjectTypeId $entraUserType.id -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttribute $entraUserType "mailNickname").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
New-JIMMatchingRule -ConnectedSystemId $entraSystem.id -ObjectTypeId $entraGroupType.id -MetaverseObjectTypeId $mvGroupType.id `
    -SourceAttributeId (Get-CsAttribute $entraGroupType "mailNickname").id -TargetMetaverseAttributeId (Get-MvAttribute "Account Name").id | Out-Null
Write-Host "  OK Matching rules created (mail nickname to Account Name)" -ForegroundColor Green

# Step 5: Inbound Synchronisation Rules
Write-TestStep "Step 5" "Creating inbound Synchronisation Rules, scoped to the run's prefix"

$userRule = New-JIMSyncRule -Name $script:Scenario037UserImportRuleName -ConnectedSystemId $entraSystem.id `
    -ConnectedSystemObjectTypeId $entraUserType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "mailNickname";      Mv = "Account Name" },
    @{ Cs = "displayName";       Mv = "Display Name" },
    @{ Cs = "userPrincipalName"; Mv = "User Principal Name" },
    @{ Cs = "givenName";         Mv = "First Name" },
    @{ Cs = "surname";           Mv = "Last Name" },
    @{ Cs = "department";        Mv = "Department" },
    @{ Cs = "jobTitle";          Mv = "Job Title" })) {
    New-JIMSyncRuleMapping -SyncRuleId $userRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $entraUserType $flow.Cs).id | Out-Null
}
$userScope = New-JIMScopingCriteriaGroup -SyncRuleId $userRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $userRule.id -GroupId $userScope.id -ConnectedSystemAttributeName 'displayName' `
    -ComparisonType StartsWith -StringValue $Prefix | Out-Null

$groupRule = New-JIMSyncRule -Name $script:Scenario037GroupImportRuleName -ConnectedSystemId $entraSystem.id `
    -ConnectedSystemObjectTypeId $entraGroupType.id -MetaverseObjectTypeId $mvGroupType.id -Direction Import -ProjectToMetaverse -PassThru
foreach ($flow in @(
    @{ Cs = "mailNickname"; Mv = "Account Name" },
    @{ Cs = "displayName";  Mv = "Display Name" },
    @{ Cs = "members";      Mv = "Static Members" })) {
    New-JIMSyncRuleMapping -SyncRuleId $groupRule.id -TargetMetaverseAttributeId (Get-MvAttribute $flow.Mv).id `
        -SourceConnectedSystemAttributeId (Get-CsAttribute $entraGroupType $flow.Cs).id | Out-Null
}
$groupScope = New-JIMScopingCriteriaGroup -SyncRuleId $groupRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $groupRule.id -GroupId $groupScope.id -ConnectedSystemAttributeName 'displayName' `
    -ComparisonType StartsWith -StringValue $Prefix | Out-Null
Write-Host "  OK Inbound rules created (users and groups named '$Prefix...' project into the Metaverse)" -ForegroundColor Green

# Step 6: Deletion rules
Write-TestStep "Step 6" "Deleting a User or Group once Entra ID no longer holds it"

foreach ($type in @($mvUserType, $mvGroupType)) {
    Set-JIMMetaverseObjectType -Id $type.id -DeletionRule WhenAuthoritativeSourceDisconnected `
        -DeletionGracePeriod ([TimeSpan]::Zero) -DeletionTriggerConnectedSystemIds @($entraSystem.id) | Out-Null
}
Write-Host "  OK Users and Groups are deleted when their Entra ID object goes, with no grace period" -ForegroundColor Green

# Step 7: Run Profiles
Write-TestStep "Step 7" "Creating Run Profiles"

New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Full Import" -RunType FullImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Delta Import" -RunType DeltaImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Delta Synchronisation" -RunType DeltaSynchronisation | Out-Null
Write-Host "  OK Run Profiles created" -ForegroundColor Green

Write-Host ""
Write-Host "Scenario 037 setup complete." -ForegroundColor Green
