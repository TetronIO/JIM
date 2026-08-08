# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for the Scenario 16 JIM SQL Connector matrix against one database provider

.DESCRIPTION
    Creates one Connected System per requested provider against the seeded HR schema, discovers its
    schema, selects the Object Types and attributes the matrix asserts on, and creates the Run Profiles
    each capability row drives.

    Two configuration choices here are load-bearing rather than incidental:

    The Database Time Zone is deliberately NOT UTC. At the UTC default every zone conversion is the
    identity, so a zone-inversion defect (import and export applying the offset the same way round
    rather than inverting each other) passes unnoticed. Europe/London in summer is one hour off UTC,
    which makes the two directions distinguishable. This is a PRD acceptance requirement, not a
    preference.

    Both Oracle opt-ins are turned on (NUMBER(1) as Boolean, RAW(16) as Guid), because the columns those
    settings reinterpret are exactly the ones the matrix exists to pin down.

.PARAMETER Provider
    Which database server to configure against (SqlServer or Oracle).

.PARAMETER RowCount
    How many employee rows the database was seeded with; the scenario asserts against this.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$true)]
    [ValidateSet("SqlServer", "Oracle")]
    [string]$Provider,

    [Parameter(Mandatory=$false)]
    [int]$RowCount = 50,

    # The zone zoneless columns are declared to be in. Not UTC by default, on purpose; see above.
    [Parameter(Mandatory=$false)]
    [string]$DatabaseTimeZone = "Europe/London",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Accepted for runner compatibility; this scenario has no directory and no template-sized data.
$null = $DirectoryConfig
$null = $Template

. "$PSScriptRoot/utils/Test-Helpers.ps1"

$config = Get-DatabaseConfig -Provider $Provider
$systemName = "SQL Matrix $($config.DisplayName)"

Write-TestSection "Scenario 16 Setup: $($config.DisplayName)"
Write-Host "  Host:              $($config.Host):$($config.Port)" -ForegroundColor Gray
Write-Host "  Schema:            $($config.Schema)" -ForegroundColor Gray
Write-Host "  Database Time Zone: $DatabaseTimeZone" -ForegroundColor Gray
Write-Host "  Employee rows:     $RowCount" -ForegroundColor Gray
Write-Host ""

# ─── Step 1: JIM module and session ────────────────────────────────────────────

Write-TestStep "Step 1" "Importing the JIM PowerShell module"
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop

Write-TestStep "Step 2" "Connecting to JIM"
if (-not $ApiKey) { throw "API key required for authentication" }
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

# ─── Step 3: Clean up any prior run ────────────────────────────────────────────

Write-TestStep "Step 3" "Removing any Connected System left by a previous run"
$existing = Get-JIMConnectedSystem -Name $systemName -ErrorAction SilentlyContinue
foreach ($system in @($existing | Where-Object { $_ })) {
    Remove-JIMConnectedSystem -Id $system.id -Force | Out-Null
    Write-Host "  Removed '$($system.name)'" -ForegroundColor Gray
}

# ─── Step 4: Resolve the connector definition and its settings ─────────────────

Write-TestStep "Step 4" "Resolving the JIM SQL Connector definition"
$connectorSummary = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM SQL Connector" }
if (-not $connectorSummary) {
    throw "The 'JIM SQL Connector' Connector Definition was not found. JIM may not have re-seeded its built-in Connector Definitions."
}
$connector = Get-JIMConnectorDefinition -Id $connectorSummary.id

function Get-SettingId {
    param([string]$Name)
    $setting = $connector.settings | Where-Object { $_.name -eq $Name }
    if (-not $setting) {
        throw "JIM SQL Connector setting '$Name' not found. The connector definition may be stale, or the setting may have been renamed (a rename orphans stored values, so it is a breaking change)."
    }
    return $setting.id
}

# ─── Step 5: The Object Types document ─────────────────────────────────────────

Write-TestStep "Step 5" "Building the Object Types configuration"

# Oracle folds unquoted identifiers to upper case and JIM's seeded schema is upper case there; SQL
# Server's seeded schema is 'hr' with mixed-case table names. Each provider's document therefore names
# its own objects rather than sharing one spelling.
$schema = $config.Schema

# The reference column is a self-reference: MANAGER_EMPLOYEE_ID carries another Person's anchor. That
# is stated explicitly rather than inferred, which is the connector's contract; a foreign key would
# only ever be a suggestion, and a view carries none at all.
$objectTypesJson = @"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "$schema",
      "table": "EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "watermarkColumn": "LAST_MODIFIED",
      "columns": [
        { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" }
      ],
      "relatedTables": [
        {
          "attributeName": "PhoneNumbers",
          "schema": "$schema",
          "table": "EMPLOYEE_PHONES",
          "valueColumn": "PHONE_NUMBER",
          "joinColumns": [ "EMPLOYEE_ID" ],
          "watermarkColumn": "LAST_MODIFIED"
        }
      ],
      "changeLog": {
        "schema": "$schema",
        "table": "IDM_CHANGE_LOG",
        "anchorColumns": [ "EMPLOYEE_ID" ],
        "sequenceColumn": "CHANGED_AT",
        "changeTypeColumn": "CHANGE_TYPE",
        "createValues": [ "I" ],
        "updateValues": [ "U" ],
        "deleteValues": [ "D" ]
      }
    },
    {
      "name": "PersonView",
      "schema": "$schema",
      "table": "V_EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "watermarkColumn": "LAST_MODIFIED"
    },
    {
      "name": "AppUser",
      "schema": "$schema",
      "table": "APP_USERS",
      "anchorColumns": [ "ID" ],
      "relatedTables": [
        {
          "attributeName": "Roles",
          "schema": "$schema",
          "table": "APP_USER_ROLES",
          "valueColumn": "ROLE_NAME",
          "joinColumns": [ "USER_ID" ]
        }
      ]
    },
    {
      "name": "NaturalKeyAccount",
      "schema": "$schema",
      "table": "APP_ACCOUNTS_NATURAL",
      "anchorColumns": [ "ACCOUNT_CODE" ]
    }$(if ($Provider -eq "Oracle") { @"
,
    {
      "name": "GuidKeyedPerson",
      "schema": "$schema",
      "table": "GUID_KEYED_PEOPLE",
      "anchorColumns": [ "PERSON_ID" ]
    }
"@ })
  ]
}
"@

# ─── Step 6: Create and configure the Connected System ─────────────────────────

Write-TestStep "Step 6" "Creating the Connected System '$systemName'"
$system = New-JIMConnectedSystem -Name $systemName `
    -Description "Scenario 16: JIM SQL Connector matrix against $($config.DisplayName)" `
    -ConnectorDefinitionId $connector.id -PassThru

$settings = @{
    (Get-SettingId "Database Type")     = @{ stringValue = $config.DatabaseTypeSetting }
    (Get-SettingId "Host")              = @{ stringValue = $config.Host }
    (Get-SettingId "Port")              = @{ intValue    = $config.Port }
    (Get-SettingId "Username")          = @{ stringValue = $config.Username }
    (Get-SettingId "Password")          = @{ stringValue = $config.Password }
    (Get-SettingId "Database Time Zone")= @{ stringValue = $DatabaseTimeZone }
    (Get-SettingId "Object Types")      = @{ stringValue = $objectTypesJson }
    (Get-SettingId "Delta Import Mode") = @{ stringValue = "Change-Log Table" }
}

if ($Provider -eq "SqlServer") {
    $settings[(Get-SettingId "Database Name")] = @{ stringValue = $config.DatabaseName }

    # The container presents a self-signed certificate that nothing trusts, and the connector offers no
    # blanket trust-server-certificate toggle by design. Encryption is therefore off for this test; the
    # certificate trust path is the SCIM scenario's territory, not this matrix's.
    $settings[(Get-SettingId "Encrypt Connection")] = @{ checkboxValue = $false }
}
else {
    $settings[(Get-SettingId "Oracle Database Identified By")] = @{ stringValue = "Service Name" }
    $settings[(Get-SettingId "Oracle Service Name")] = @{ stringValue = $config.ServiceName }

    # The container's listener has no Advanced Networking configuration, and the connector's Native
    # Network Encryption mode negotiates REQUIRED rather than REQUESTED, so it would refuse to connect.
    $settings[(Get-SettingId "Oracle Encryption")] = @{ stringValue = "None" }

    # Both opt-ins on: these two settings are what turn NUMBER(1) into Boolean and RAW(16) into Guid,
    # and both reinterpretations are matrix rows in their own right.
    $settings[(Get-SettingId "Treat NUMBER(1) Columns as Boolean")] = @{ checkboxValue = $true }
    $settings[(Get-SettingId "Treat RAW(16) Columns as Guid")] = @{ checkboxValue = $true }
}

Write-TestStep "Step 7" "Applying settings (this performs the save-time connectivity test)"
Set-JIMConnectedSystem -Id $system.id -SettingValues $settings | Out-Null
Write-Host "  OK Settings saved; the live connectivity test passed" -ForegroundColor Green

# ─── Step 8: Schema discovery ──────────────────────────────────────────────────

Write-TestStep "Step 8" "Discovering the schema"
Import-JIMConnectedSystemSchema -Id $system.id | Out-Null

$objectTypes = Get-JIMConnectedSystem -Id $system.id -ObjectTypes
$expectedTypes = @("Person", "PersonView", "AppUser", "NaturalKeyAccount")
if ($Provider -eq "Oracle") { $expectedTypes += "GuidKeyedPerson" }

foreach ($expected in $expectedTypes) {
    if (-not ($objectTypes | Where-Object { $_.name -eq $expected })) {
        throw "Schema discovery did not return the '$expected' Object Type. Discovered: $(($objectTypes | ForEach-Object { $_.name }) -join ', ')"
    }
}
Write-Host "  OK Discovered $($objectTypes.Count) Object Type(s): $(($objectTypes | ForEach-Object { $_.name }) -join ', ')" -ForegroundColor Green

# ─── Step 9: Select Object Types and attributes ────────────────────────────────

Write-TestStep "Step 9" "Selecting Object Types and their attributes"

# Which column is the anchor for each Object Type. Schema discovery derives the recommendation from the
# primary key, but the External ID is stated here so a discovery regression fails loudly rather than
# quietly anchoring on something else.
$anchorColumns = @{
    Person            = "EMPLOYEE_ID"
    PersonView        = "EMPLOYEE_ID"
    AppUser           = "ID"
    NaturalKeyAccount = "ACCOUNT_CODE"
    GuidKeyedPerson   = "PERSON_ID"
}

$selectedTypes = @{}
foreach ($typeName in $expectedTypes) {
    $objectType = $objectTypes | Where-Object { $_.name -eq $typeName }
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $system.id -ObjectTypeId $objectType.id -Selected $true | Out-Null

    $anchor = $anchorColumns[$typeName]
    $attributeUpdates = @{}
    foreach ($attribute in $objectType.attributes) {
        $attributeUpdates[$attribute.id] = @{
            selected     = $true
            isExternalId = ($attribute.name -eq $anchor)
        }
    }

    Set-JIMConnectedSystemAttribute -ConnectedSystemId $system.id -ObjectTypeId $objectType.id -AttributeUpdates $attributeUpdates | Out-Null
    $selectedTypes[$typeName] = $objectType
    Write-Host "  OK $typeName ($($objectType.attributes.Count) attributes, anchor $anchor)" -ForegroundColor Green
}

# ─── Step 10: Run Profiles ─────────────────────────────────────────────────────

Write-TestStep "Step 10" "Creating Run Profiles"

# A page size well below the row count, so keyset paging is genuinely exercised across several pages
# rather than swallowing the whole table in one.
$pageSize = 10

$runProfiles = @(
    @{ Name = "Full Import";           RunType = "FullImport"           }
    @{ Name = "Delta Import";          RunType = "DeltaImport"          }
    @{ Name = "Full Synchronisation";  RunType = "FullSynchronisation"  }
    @{ Name = "Delta Synchronisation"; RunType = "DeltaSynchronisation" }
    @{ Name = "Export";                RunType = "Export"               }
)

foreach ($runProfile in $runProfiles) {
    New-JIMRunProfile -ConnectedSystemId $system.id -Name $runProfile.Name -RunType $runProfile.RunType -PageSize $pageSize | Out-Null
    Write-Host "  OK $($runProfile.Name) (page size $pageSize)" -ForegroundColor Green
}

Write-TestSection "Scenario 16 Setup Complete: $($config.DisplayName)"
Write-Host "  Connected System:   $systemName (ID: $($system.id))" -ForegroundColor Cyan
Write-Host "  Object Types:       $(($expectedTypes) -join ', ')" -ForegroundColor Cyan
Write-Host "  Database Time Zone: $DatabaseTimeZone (deliberately not UTC)" -ForegroundColor Cyan
Write-Host "  Page size:          $pageSize" -ForegroundColor Cyan

return @{
    Provider          = $Provider
    ConnectedSystemId = $system.id
    SystemName        = $systemName
    ObjectTypes       = $selectedTypes
    RowCount          = $RowCount
    DatabaseTimeZone  = $DatabaseTimeZone
    PageSize          = $pageSize
}
