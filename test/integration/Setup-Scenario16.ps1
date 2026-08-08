# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for the Scenario 16 JIM SQL Connector matrix against one database provider

.DESCRIPTION
    Creates one Connected System per requested provider against the seeded HR schema, discovers its
    schema, selects the Object Types and attributes the matrix asserts on, creates the Run Profiles each
    capability row drives, and creates the inbound and outbound Synchronisation Rules the import-side and
    export-side rows need.

    Two configuration choices here are load-bearing rather than incidental:

    The Database Time Zone is deliberately NOT UTC. At the UTC default every zone conversion is the
    identity, so a zone-inversion defect (import and export applying the offset the same way round
    rather than inverting each other) passes unnoticed. Australia/Sydney is eleven hours off UTC over
    the seeded date range, which makes the two directions distinguishable. This is a PRD acceptance
    requirement, not a preference; see the parameter's own comment for why it is not Europe/London.

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
    #
    # Australia/Sydney rather than Europe/London. Every seeded date sits in January or February (the
    # generator derives START_DATE as 2020-01-06 plus n days, so a 50-row seed never leaves winter), and
    # Europe/London is UTC+00:00 for all of them: the conversion would be the identity and the assertion
    # would pass with a zone-inversion defect fully present, which is precisely the failure this setting
    # exists to prevent. Sydney is UTC+11:00 over the seeded range and UTC+10:00 in the southern winter,
    # so the offset is both non-zero and season-dependent.
    [Parameter(Mandatory=$false)]
    [string]$DatabaseTimeZone = "Australia/Sydney",

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
      "watermarkColumn": "LAST_MODIFIED",
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
      ],
      "changeLog": {
        "schema": "$schema",
        "table": "APP_USERS_CHANGE_LOG",
        "anchorColumns": [ "ID" ],
        "sequenceColumn": "CHANGED_AT",
        "changeTypeColumn": "CHANGE_TYPE",
        "createValues": [ "I" ],
        "updateValues": [ "U" ],
        "deleteValues": [ "D" ]
      }
    },
    {
      "name": "NaturalKeyAccount",
      "schema": "$schema",
      "table": "APP_ACCOUNTS_NATURAL",
      "anchorColumns": [ "ACCOUNT_CODE" ],
      "changeLog": {
        "schema": "$schema",
        "table": "APP_ACCOUNTS_CHANGE_LOG",
        "anchorColumns": [ "ACCOUNT_CODE" ],
        "sequenceColumn": "CHANGED_AT",
        "changeTypeColumn": "CHANGE_TYPE",
        "createValues": [ "I" ],
        "updateValues": [ "U" ],
        "deleteValues": [ "D" ]
      }
    }$(if ($Provider -eq "Oracle") { @"
,
    {
      "name": "GuidKeyedPerson",
      "schema": "$schema",
      "table": "GUID_KEYED_PEOPLE",
      "anchorColumns": [ "PERSON_ID" ],
      "changeLog": {
        "schema": "$schema",
        "table": "GUID_PEOPLE_CHANGE_LOG",
        "anchorColumns": [ "PERSON_ID" ],
        "sequenceColumn": "CHANGED_AT",
        "changeTypeColumn": "CHANGE_TYPE",
        "createValues": [ "I" ],
        "updateValues": [ "U" ],
        "deleteValues": [ "D" ]
      }
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

# -ErrorAction Stop on every mutating call, rather than relying on this script's $ErrorActionPreference.
# A cmdlet exported from a module reads the MODULE's preference variables, not the caller's, so a
# Write-Error inside JIM.psd1 is non-terminating here however this script sets its own preference. That
# is how the first run of this script printed "the live connectivity test passed" immediately after the
# save had been refused, and then failed four steps later with an unrelated-looking message.
Set-JIMConnectedSystem -Id $system.id -SettingValues $settings -ErrorAction Stop | Out-Null
Write-Host "  OK Settings saved; the live connectivity test passed" -ForegroundColor Green

# ─── Step 8: Schema discovery ──────────────────────────────────────────────────

Write-TestStep "Step 8" "Discovering the schema"
Import-JIMConnectedSystemSchema -Id $system.id -ErrorAction Stop | Out-Null

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

# ─── Step 11: Metaverse plumbing ───────────────────────────────────────────────

Write-TestStep "Step 11" "Resolving the Metaverse schema"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) { throw "The built-in Metaverse Object Type 'User' was not found; JIM may not have seeded." }

# Two column shapes the matrix cares about have no built-in Metaverse Attribute of the right data type:
# a NUMBER(9,4)/decimal(9,4) needs Decimal (the built-in numeric attributes are Integer), and a
# bigint/NUMBER(19) needs LongNumber. Mapping either onto an Integer attribute would either be refused
# or would silently round, and a rounded value is exactly the defect the exact-numeric row exists to
# catch, so the scenario contributes its own two attributes rather than borrowing an ill-fitting one.
function Add-Scenario16MetaverseAttribute {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][ValidateSet('Text','Integer','LongNumber','Decimal','DateTime','Boolean','Reference','Guid','Binary')][string]$Type
    )

    $attribute = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $attribute) {
        $attribute = New-JIMMetaverseAttribute -Name $Name -Type $Type -Plurality SingleValued -PassThru
        Write-Host "  Created Metaverse Attribute '$Name' ($Type)" -ForegroundColor Gray
    }

    # Binding is idempotent from the caller's point of view: a second bind of the same attribute to the
    # same Object Type is a no-op the API accepts, and the setup runs once per provider.
    Add-JIMMetaverseObjectTypeAttribute -AttributeId $attribute.id -ObjectTypeId $mvUserType.id -ErrorAction SilentlyContinue | Out-Null
    return $attribute
}

$mvFteAttribute       = Add-Scenario16MetaverseAttribute -Name "SQL Matrix FTE"       -Type Decimal
$mvHeadcountAttribute = Add-Scenario16MetaverseAttribute -Name "SQL Matrix Headcount" -Type LongNumber

$mvAttributes = @(Get-JIMMetaverseAttribute)

function Get-MvAttributeId {
    param([string]$Name)
    $attribute = $mvAttributes | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $attribute) { throw "Metaverse Attribute '$Name' was not found." }
    return $attribute.id
}

function Get-CsAttributeId {
    param([string]$ObjectTypeName, [string]$AttributeName)
    if (-not $selectedTypes.ContainsKey($ObjectTypeName)) {
        throw "Object Type '$ObjectTypeName' was not selected, so its attributes cannot be mapped."
    }
    $attribute = $selectedTypes[$ObjectTypeName].attributes | Where-Object { $_.name -eq $AttributeName } | Select-Object -First 1
    if (-not $attribute) {
        throw "Schema discovery did not return attribute '$AttributeName' on Object Type '$ObjectTypeName'. Discovered: $(($selectedTypes[$ObjectTypeName].attributes | ForEach-Object { $_.name }) -join ', ')"
    }
    return $attribute.id
}

# ─── Step 12: Object Matching Rule ─────────────────────────────────────────────

Write-TestStep "Step 12" "Creating the Object Matching Rule for Person"

# EMPLOYEE_NUMBER rather than EMPLOYEE_ID: the Metaverse's 'Employee ID' is a Text attribute and
# EMPLOYEE_ID is an integer column, so the text-shaped EMPLOYEE_NUMBER is the one that joins without a
# type coercion the mapping validator would have to invent.
New-JIMMatchingRule `
    -ConnectedSystemId $system.id `
    -ObjectTypeId $selectedTypes['Person'].id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttributeId -ObjectTypeName 'Person' -AttributeName 'EMPLOYEE_NUMBER') `
    -TargetMetaverseAttributeId (Get-MvAttributeId -Name 'Employee ID') | Out-Null
Write-Host "  OK Person.EMPLOYEE_NUMBER matches on Metaverse 'Employee ID'" -ForegroundColor Green

# ─── Step 13: Inbound Synchronisation Rule ─────────────────────────────────────

Write-TestStep "Step 13" "Creating the inbound Synchronisation Rule (Person to Metaverse)"

# The inbound rule is not decoration for the export rows: three driver-shape rows assert on the UTC
# instant JIM derived from a source column, and the only place that instant is observable is the
# Metaverse Object's attribute value. Without this rule those rows can only report the source wall
# clock back to themselves, which proves nothing.
$importRule = New-JIMSyncRule `
    -Name "SQL Matrix Person Import ($($config.DisplayName))" `
    -Description "Scenario 16 inbound rule: projects EMPLOYEES rows into the Metaverse so the driver-shape rows can read the values JIM derived." `
    -ConnectedSystemId $system.id `
    -ConnectedSystemObjectTypeId $selectedTypes['Person'].id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

# Metaverse attribute names are borrowed for their data type, not their business meaning: this schema
# has no 'hired at' concept, so the two extra date and time columns land on the closest-shaped built-ins.
# The mapping table below is the authoritative statement of which column is which.
$importMappings = @(
    @{ Cs = 'EMPLOYEE_NUMBER';     Mv = 'Employee ID'         }
    @{ Cs = 'EMPLOYEE_NUMBER';     Mv = 'Account Name'        }
    @{ Cs = 'EMPLOYEE_ID';         Mv = 'Employee Number'     }
    @{ Cs = 'FIRST_NAME';          Mv = 'First Name'          }
    @{ Cs = 'LAST_NAME';           Mv = 'Last Name'           }
    @{ Cs = 'EMAIL';               Mv = 'Email'               }
    @{ Cs = 'DEPARTMENT';          Mv = 'Department'          }
    @{ Cs = 'IS_ACTIVE';           Mv = 'Account Enabled'     }
    @{ Cs = 'HEADCOUNT';           Mv = 'SQL Matrix Headcount'}
    @{ Cs = 'FTE';                 Mv = 'SQL Matrix FTE'      }
    # Zoneless: the Database Time Zone decides the instant. This is the DateTimeNonUtc row's subject.
    @{ Cs = 'START_DATE';          Mv = 'Employee Start Date' }
    # Offset-carrying (-05:00 in the seeded data): unambiguous on the wire, so no setting applies.
    @{ Cs = 'HIRED_AT';            Mv = 'Employee End Date'   }
    @{ Cs = 'EMPLOYEE_GUID';       Mv = 'objectGUID'          }
    @{ Cs = 'PHOTO';               Mv = 'Photo'               }
    @{ Cs = 'MANAGER_EMPLOYEE_ID'; Mv = 'Manager'             }
    @{ Cs = 'PhoneNumbers';        Mv = 'Other Telephones'    }
)

# Oracle's third date and time shape. TIMESTAMP WITH LOCAL TIME ZONE exists on Oracle alone, and it is
# the column where the connector's two oracles (catalogue type name on export, runtime CLR type on
# import) can disagree, so it gets its own Metaverse attribute to be read back from.
if ($Provider -eq "Oracle") {
    $importMappings += @{ Cs = 'HIRED_AT_LOCAL'; Mv = 'Account Expires' }
}

foreach ($mapping in $importMappings) {
    New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
        -TargetMetaverseAttributeId (Get-MvAttributeId -Name $mapping.Mv) `
        -SourceConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'Person' -AttributeName $mapping.Cs) | Out-Null
}
Write-Host "  OK Inbound rule created with $($importMappings.Count) Attribute Flow(s)" -ForegroundColor Green

# ─── Step 14: Outbound Synchronisation Rules ───────────────────────────────────

Write-TestStep "Step 14" "Creating the outbound Synchronisation Rules"

# ── AppUser: the database-generated key target ──
#
# APP_USERS.ID is an IDENTITY column on both providers, so JIM never writes it and must read it back
# from the insert (OUTPUT INSERTED on SQL Server, RETURNING ... INTO on Oracle). That returned value
# becomes the Connected System Object's external ID, and the confirming Full Import has to compose the
# same token from the row it reads or the object is imported a second time as a stranger. That
# agreement is what the Export.Create row checks, and it cannot be checked any other way.
$appUserRule = New-JIMSyncRule `
    -Name "SQL Matrix AppUser Export ($($config.DisplayName))" `
    -Description "Scenario 16 outbound rule: provisions into APP_USERS, whose primary key the database generates." `
    -ConnectedSystemId $system.id `
    -ConnectedSystemObjectTypeId $selectedTypes['AppUser'].id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -OutboundDeprovisionAction Delete `
    -PassThru

$appUserMappings = @(
    @{ Mv = 'Account Name';         Cs = 'USER_NAME'  }
    @{ Mv = 'Email';                Cs = 'EMAIL'      }
    @{ Mv = 'SQL Matrix FTE';       Cs = 'FTE'        }
    @{ Mv = 'Account Enabled';      Cs = 'IS_ENABLED' }
    @{ Mv = 'Employee Start Date';  Cs = 'STARTS_ON'  }
    @{ Mv = 'Employee End Date';    Cs = 'STARTS_AT'  }
    # The reference column. JIM writes the anchor of the manager's own object in THIS Connected System,
    # which means the manager must have been provisioned first; the seeded population puts every manager
    # in the first ten rows precisely so that ordering is exercised rather than avoided.
    @{ Mv = 'Manager';              Cs = 'MANAGER_ID' }
    # Related-table multi-valued maintenance: rows added to and removed from APP_USER_ROLES inside the
    # same transaction as the parent row.
    @{ Mv = 'Other Telephones';     Cs = 'Roles'      }
)

foreach ($mapping in $appUserMappings) {
    New-JIMSyncRuleMapping -SyncRuleId $appUserRule.id `
        -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'AppUser' -AttributeName $mapping.Cs) `
        -SourceMetaverseAttributeId (Get-MvAttributeId -Name $mapping.Mv) | Out-Null
}

# An expression rather than a straight flow, so export-side expression evaluation is covered too.
New-JIMSyncRuleMapping -SyncRuleId $appUserRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'AppUser' -AttributeName 'DISPLAY_NAME') `
    -Expression 'mv["First Name"] + " " + mv["Last Name"]' | Out-Null

# Scoped on Account Enabled, which is what gives the Export.Delete row something to drive: flipping a
# source row's IS_ACTIVE takes its Metaverse Object out of scope, and OutboundDeprovisionAction Delete
# turns that into a delete export rather than a disconnect. The seeded data disables every seventh
# employee, so both sides of the scope are populated from the first import.
$appUserScope = New-JIMScopingCriteriaGroup -SyncRuleId $appUserRule.id -Type All -PassThru
New-JIMScopingCriterion `
    -SyncRuleId $appUserRule.id `
    -GroupId $appUserScope.id `
    -MetaverseAttributeId (Get-MvAttributeId -Name 'Account Enabled') `
    -ComparisonType Equals `
    -BoolValue $true | Out-Null

Write-Host "  OK AppUser export rule created ($($appUserMappings.Count + 1) Attribute Flows, scoped on Account Enabled)" -ForegroundColor Green

# ── NaturalKeyAccount: the key JIM authors ──
#
# APP_ACCOUNTS_NATURAL.ACCOUNT_CODE is a natural primary key, so it is writable on create and JIM
# supplies it. The external ID is then a value JIM chose rather than one the database handed back,
# which is the opposite half of the anchor-agreement question the AppUser rule asks.
$naturalRule = New-JIMSyncRule `
    -Name "SQL Matrix NaturalKeyAccount Export ($($config.DisplayName))" `
    -Description "Scenario 16 outbound rule: provisions into a table whose primary key JIM authors." `
    -ConnectedSystemId $system.id `
    -ConnectedSystemObjectTypeId $selectedTypes['NaturalKeyAccount'].id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -OutboundDeprovisionAction Delete `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $naturalRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'NaturalKeyAccount' -AttributeName 'ACCOUNT_CODE') `
    -SourceMetaverseAttributeId (Get-MvAttributeId -Name 'Account Name') | Out-Null

New-JIMSyncRuleMapping -SyncRuleId $naturalRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'NaturalKeyAccount' -AttributeName 'IS_ENABLED') `
    -SourceMetaverseAttributeId (Get-MvAttributeId -Name 'Account Enabled') | Out-Null

New-JIMSyncRuleMapping -SyncRuleId $naturalRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'NaturalKeyAccount' -AttributeName 'DISPLAY_NAME') `
    -Expression 'mv["First Name"] + " " + mv["Last Name"]' | Out-Null

# A narrower scope than the AppUser rule's, so the two outbound rules provision different populations
# and a row asserting on one cannot accidentally be satisfied by the other's work.
$naturalScope = New-JIMScopingCriteriaGroup -SyncRuleId $naturalRule.id -Type All -PassThru
New-JIMScopingCriterion `
    -SyncRuleId $naturalRule.id `
    -GroupId $naturalScope.id `
    -MetaverseAttributeId (Get-MvAttributeId -Name 'Department') `
    -ComparisonType Equals `
    -StringValue "Research" | Out-Null

Write-Host "  OK NaturalKeyAccount export rule created (3 Attribute Flows, scoped on Department = Research)" -ForegroundColor Green

# ── GuidKeyedPerson (Oracle only): the RAW(16) DEFAULT SYS_GUID() key ──
#
# The single most load-bearing outbound rule in the scenario. The generated key comes back through a
# bound output parameter as an ODP.NET wrapper struct rather than a plain byte[], and nothing in the
# unit suite can see that shape. This is the first end-to-end proof that provisioning into Oracle works.
$guidRule = $null
if ($Provider -eq "Oracle") {
    $guidRule = New-JIMSyncRule `
        -Name "SQL Matrix GuidKeyedPerson Export (Oracle)" `
        -Description "Scenario 16 outbound rule: provisions into a table keyed RAW(16) DEFAULT SYS_GUID()." `
        -ConnectedSystemId $system.id `
        -ConnectedSystemObjectTypeId $selectedTypes['GuidKeyedPerson'].id `
        -MetaverseObjectTypeId $mvUserType.id `
        -Direction Export `
        -ProvisionToConnectedSystem `
        -OutboundDeprovisionAction Delete `
        -PassThru

    New-JIMSyncRuleMapping -SyncRuleId $guidRule.id `
        -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'GuidKeyedPerson' -AttributeName 'DEPARTMENT') `
        -SourceMetaverseAttributeId (Get-MvAttributeId -Name 'Department') | Out-Null

    New-JIMSyncRuleMapping -SyncRuleId $guidRule.id `
        -TargetConnectedSystemAttributeId (Get-CsAttributeId -ObjectTypeName 'GuidKeyedPerson' -AttributeName 'FULL_NAME') `
        -Expression 'mv["First Name"] + " " + mv["Last Name"]' | Out-Null

    $guidScope = New-JIMScopingCriteriaGroup -SyncRuleId $guidRule.id -Type All -PassThru
    New-JIMScopingCriterion `
        -SyncRuleId $guidRule.id `
        -GroupId $guidScope.id `
        -MetaverseAttributeId (Get-MvAttributeId -Name 'Department') `
        -ComparisonType Equals `
        -StringValue "Finance" | Out-Null

    Write-Host "  OK GuidKeyedPerson export rule created (2 Attribute Flows, scoped on Department = Finance)" -ForegroundColor Green
}

Write-TestSection "Scenario 16 Setup Complete: $($config.DisplayName)"
Write-Host "  Connected System:   $systemName (ID: $($system.id))" -ForegroundColor Cyan
Write-Host "  Object Types:       $(($expectedTypes) -join ', ')" -ForegroundColor Cyan
Write-Host "  Database Time Zone: $DatabaseTimeZone (deliberately not UTC)" -ForegroundColor Cyan
Write-Host "  Page size:          $pageSize" -ForegroundColor Cyan
Write-Host "  Synchronisation Rules: 1 inbound, $(if ($Provider -eq 'Oracle') { 3 } else { 2 }) outbound" -ForegroundColor Cyan

return @{
    Provider           = $Provider
    ConnectedSystemId  = $system.id
    SystemName         = $systemName
    ObjectTypes        = $selectedTypes
    RowCount           = $RowCount
    DatabaseTimeZone   = $DatabaseTimeZone
    PageSize           = $pageSize
    MetaverseObjectTypeId = $mvUserType.id
    ImportRuleId       = $importRule.id
    AppUserRuleId      = $appUserRule.id
    NaturalKeyRuleId   = $naturalRule.id
    GuidKeyRuleId      = if ($guidRule) { $guidRule.id } else { $null }
    # The Metaverse Attributes the driver-shape rows read their derived values back from, named here so
    # a row does not have to re-derive which built-in was borrowed for which column.
    MetaverseAttributes = @{
        ZonelessDate    = 'Employee Start Date'
        OffsetDate      = 'Employee End Date'
        LocalZoneDate   = 'Account Expires'
        Decimal         = $mvFteAttribute.name
        LongNumber      = $mvHeadcountAttribute.name
        MultiValued     = 'Other Telephones'
    }
}
