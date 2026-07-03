# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 12: Relative-Date Inbound Scoping (joiner / leaver window)

.DESCRIPTION
    Sets up a single HR CSV Connected System and an inbound (Import) Synchronisation Rule
    whose scoping criteria are RELATIVE DATE criteria, modelling the "currently-employed"
    window:

        employeeStartDate <= now   (the joiner has started)
      AND
        employeeEndDate   >= now   (the leaver has not yet ended)

    A user is in scope only while currently employed. This single rule drives both
    transitions the scenario exercises:

      - Joiner: appears in HR before their first day (start date in the future) -> out of
        scope -> no Metaverse Object. When their start date arrives -> in scope -> projected
        (provisioned into the identity store).

      - Leaver: currently employed (end date in the future) -> in scope -> Metaverse Object
        present. When their end date passes -> out of scope -> CSO disconnected
        (InboundOutOfScopeAction = Disconnect) -> the User type's default
        WhenLastConnectorDisconnected deletion rule removes the orphaned Metaverse Object
        (deprovisioned from the identity store).

    The criteria use the Hours unit with count 0 (exact "now", no midnight rounding) so the
    companion scenario step can prove the criterion is re-evaluated against the wall clock on
    every run, not frozen at rule-creation time. Day-granularity is unnecessary because the
    seed data sits +/- weeks from "now".

    This scenario deliberately stops at the Metaverse: projection is "provisioned" and
    last-connector deletion is "deprovisioned". The cross-system Delete cascade to a target
    directory is already covered by Scenario 10. Keeping this Metaverse-only makes it fast and
    free of directory-container flakiness while still exercising the new relative-date scoping
    end-to-end.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility. Scoping behaviour is template-independent; this
    scenario seeds its own handful of explicit test users and ignores the template.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target and ignores it.

.EXAMPLE
    ./Setup-Scenario12.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."
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

# Accepted for runner compatibility; this scenario has no directory target and no export tuning.
$null = $DirectoryConfig
$null = $ExportConcurrency
$null = $MaxExportParallelism

. "$PSScriptRoot/utils/Test-Helpers.ps1"

$hrSystemName = "Relative-Date HR Source"
$importRuleName = "Relative-Date Import (HR -> MV)"
$hrCsvFilePath = "/connector-files/test-data/scenario12-hr-users.csv"

Write-TestSection "Scenario 12 Setup: Relative-Date Inbound Scoping"

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

# Step 2b: Clean up any leftover scoping connected system from previous runs
Write-TestStep "Step 2b" "Cleaning up existing configuration"

$existing = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue)
$sys = $existing | Where-Object { $_.name -eq $hrSystemName }
if ($sys) {
    Write-Host "  Removing existing '$hrSystemName'..." -ForegroundColor Gray
    Remove-JIMConnectedSystem -Id $sys.id -Force | Out-Null
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 3: Get connector definition (File)
Write-TestStep "Step 3" "Resolving File connector definition"

$connectors = Get-JIMConnectorDefinition
$csvConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
if (-not $csvConnector) { throw "JIM File Connector definition not found" }
Write-Host "  OK File connector (ID: $($csvConnector.id))" -ForegroundColor Green

# Step 4: Create HR CSV Connected System
Write-TestStep "Step 4" "Creating HR CSV Connected System ($hrSystemName)"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "HR source for relative-date inbound scoping integration tests" `
    -ConnectorDefinitionId $csvConnector.id `
    -PassThru

$csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
$filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "File Path" }
$delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
$objectTypeSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Object Type" }

$hrSettings = @{}
if ($filePathSetting) { $hrSettings[$filePathSetting.id] = @{ stringValue = $hrCsvFilePath } }
if ($delimiterSetting) { $hrSettings[$delimiterSetting.id] = @{ stringValue = "," } }
if ($objectTypeSetting) { $hrSettings[$objectTypeSetting.id] = @{ stringValue = "person" } }

Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues $hrSettings | Out-Null
Write-Host "  OK HR CSV configured (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 5: Import schema. The File connector infers attribute types from the first data row;
# the scenario seeds the CSV (with ISO-8601 employeeStartDate / employeeEndDate values) before
# calling this setup, so both date columns are inferred as DateTime attributes.
Write-TestStep "Step 5" "Importing HR schema"

Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
if (-not $hrPersonType) { throw "HR 'person' object type not found in schema" }

# Fail loudly if the date columns did not infer as DateTime; a Text-typed date column would
# make the relative-date scoping criteria meaningless and the scenario would silently mis-test.
foreach ($dateCol in @("employeeStartDate", "employeeEndDate")) {
    $attr = $hrPersonType.attributes | Where-Object { $_.name -eq $dateCol }
    if (-not $attr) { throw "HR schema is missing the '$dateCol' column; check the seed CSV." }
    if ($attr.type -ne "DateTime") {
        throw "HR '$dateCol' inferred as '$($attr.type)', expected 'DateTime'. Ensure every seed row carries an ISO-8601 value so the File connector types it correctly."
    }
}
Write-Host "  OK HR schema imported; employeeStartDate / employeeEndDate typed as DateTime" -ForegroundColor Green

# Step 6: Select object type and attributes (the two date columns must be selected so the
# imported CSOs carry their values for inbound scoping evaluation).
Write-TestStep "Step 6" "Selecting object type and attributes"

Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -Selected $true | Out-Null

$hrAttrs = @("employeeId", "firstName", "lastName", "email", "department", "title", "samAccountName", "displayName", "employeeStartDate", "employeeEndDate")
$hrAttrUpdates = @{}
foreach ($attr in $hrPersonType.attributes) {
    if ($attr.name -in $hrAttrs) {
        $isExternalId = ($attr.name -eq "employeeId")
        $hrAttrUpdates[$attr.id] = @{ selected = $true; isExternalId = $isExternalId }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -AttributeUpdates $hrAttrUpdates | Out-Null
Write-Host "  OK Selected $($hrAttrUpdates.Count) HR attributes (employeeId is external ID)" -ForegroundColor Green

# Step 7: Resolve Metaverse 'User' object type and attributes
Write-TestStep "Step 7" "Resolving Metaverse 'User' object type"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) { throw "Metaverse 'User' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)
Write-Host "  OK Found 'User' MV object type (ID: $($mvUserType.id))" -ForegroundColor Green

# Step 8: Object matching rule (HR.employeeId <-> MV.Employee ID)
Write-TestStep "Step 8" "Configuring object matching rule"

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

# Step 9: Create the inbound (Import) sync rule (HR -> MV)
Write-TestStep "Step 9" "Creating import sync rule ($importRuleName)"

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

# Step 10: Relative-date scoping criteria - the "currently-employed" window.
# All (AND) group: employeeStartDate <= now AND employeeEndDate >= now.
# Hours/0/FromNow resolves to the exact current instant (no midnight rounding) so the scenario
# can prove per-run re-evaluation against the wall clock.
Write-TestStep "Step 10" "Adding relative-date scoping criteria to import rule"

$startAttr = $hrPersonType.attributes | Where-Object { $_.name -eq "employeeStartDate" }
$endAttr = $hrPersonType.attributes | Where-Object { $_.name -eq "employeeEndDate" }
if (-not $startAttr -or -not $endAttr) { throw "HR date attributes not found after selection" }

$importScopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $importRule.id -Type All -PassThru

New-JIMScopingCriterion `
    -SyncRuleId $importRule.id `
    -GroupId $importScopeGroup.id `
    -ConnectedSystemAttributeId $startAttr.id `
    -ComparisonType LessThanOrEquals `
    -ValueMode Relative -RelativeCount 0 -RelativeUnit Hours -RelativeDirection FromNow | Out-Null

New-JIMScopingCriterion `
    -SyncRuleId $importRule.id `
    -GroupId $importScopeGroup.id `
    -ConnectedSystemAttributeId $endAttr.id `
    -ComparisonType GreaterThanOrEquals `
    -ValueMode Relative -RelativeCount 0 -RelativeUnit Hours -RelativeDirection FromNow | Out-Null

# Disconnect on scope exit so a leaver's last connector breaking triggers the User type's default
# WhenLastConnectorDisconnected deletion rule (deprovisioning the orphaned Metaverse Object).
Set-JIMSyncRule -Id $importRule.id -InboundOutOfScopeAction Disconnect | Out-Null
Write-Host "  OK Import rule scoped to the currently-employed window (InboundOutOfScopeAction=Disconnect)" -ForegroundColor Green

# Step 11: Run profiles
Write-TestStep "Step 11" "Creating run profiles"

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $hrSystem.id -RunType "FullImport" -FilePath $hrCsvFilePath | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $hrSystem.id -RunType "FullSynchronisation" | Out-Null
Write-Host "  OK HR run profiles: Full Import, Full Synchronisation" -ForegroundColor Green

Write-TestSection "Scenario 12 Setup Complete"
Write-Host "  HR Connected System: $hrSystemName (ID: $($hrSystem.id))" -ForegroundColor Cyan
Write-Host "  Import Sync Rule:    $importRuleName (ID: $($importRule.id))" -ForegroundColor Cyan
Write-Host "  Scope: employeeStartDate <= now AND employeeEndDate >= now (relative, re-evaluated each run)" -ForegroundColor Cyan
