# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 13: Relative-Date OUTBOUND (Export) Scoping

.DESCRIPTION
    Sets up the topology needed to prove the Temporal Scope Reconciler's OUTBOUND lane
    (issue #892): a downstream provisioning that is held by a relative-date EXPORT criterion
    until the clock crosses the boundary, applied on schedule with no Metaverse Object data
    change.

    Topology:
      - HR CSV Connected System (inbound source).
      - Import Synchronisation Rule (HR -> MV), ProjectToMetaverse, with NO scoping criteria
        so every imported user projects a Metaverse Object that PERSISTS regardless of the
        export date. This is the deliberate isolation: the Metaverse Object never leaves
        inbound scope, so only the EXPORT scope flips as "now" advances, which pins any
        downstream change squarely on the outbound reconciler lane.
      - A Metaverse DateTime attribute ("Employee Start Date") flowed from the HR
        employeeStartDate column, so the export criterion has a live value to evaluate.
      - File CSV Connected System (outbound target), a header-only CSV the File connector
        appends to (no directory-container flakiness, mirroring Scenario 12's philosophy).
      - Export Synchronisation Rule (MV -> Target), ProvisionToConnectedSystem, scoped to the
        relative-date "currently-started" window:

            Employee Start Date <= now

        so a user is provisioned downstream only once their start date has arrived. A joiner
        with a future start date is IN the Metaverse (line manager can prepare access) but is
        held OUT of downstream provisioning until their first day; this is the staged-provisioning
        use case in the plan.

    The criterion uses Hours/0/FromNow (exact "now", no midnight rounding) so the companion
    scenario step can prove per-run re-evaluation against the wall clock.

    The built-in "Temporal Scope Reconciliation" schedule is DISABLED here so it cannot
    auto-fire mid-test; the scenario triggers it manually at the exact point it wants a sweep,
    which keeps the negative-control assertion (the hot path alone misses the transition)
    deterministic.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility. Scoping behaviour is template-independent; this
    scenario seeds its own explicit test users and ignores the template.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target and ignores it.

.EXAMPLE
    ./Setup-Scenario13.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."
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

$hrSystemName = "Relative-Date Outbound HR Source"
$targetSystemName = "Relative-Date Downstream Target"
$importRuleName = "Relative-Date Outbound Import (HR -> MV)"
$exportRuleName = "Relative-Date Outbound Export (MV -> Target)"
$hrCsvFilePath = "/connector-files/test-data/scenario13-hr-users.csv"
$targetCsvFilePath = "/connector-files/test-data/scenario13-target.csv"

Write-TestSection "Scenario 13 Setup: Relative-Date Outbound (Export) Scoping"

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

# Step 2b: Clean up any leftover connected systems from previous runs
Write-TestStep "Step 2b" "Cleaning up existing configuration"

$existing = @(Get-JIMConnectedSystem -ErrorAction SilentlyContinue)
foreach ($name in @($hrSystemName, $targetSystemName)) {
    $sys = $existing | Where-Object { $_.name -eq $name }
    if ($sys) {
        Write-Host "  Removing existing '$name'..." -ForegroundColor Gray
        Remove-JIMConnectedSystem -Id $sys.id -Force | Out-Null
    }
}
Write-Host "  OK Cleanup complete" -ForegroundColor Green

# Step 3: Get connector definition (File)
Write-TestStep "Step 3" "Resolving File connector definition"

$connectors = Get-JIMConnectorDefinition
$csvConnector = $connectors | Where-Object { $_.name -eq "JIM File Connector" }
if (-not $csvConnector) { throw "JIM File Connector definition not found" }
Write-Host "  OK File connector (ID: $($csvConnector.id))" -ForegroundColor Green

$csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
$filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "File Path" }
$delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
$objectTypeSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Object Type" }

# Step 4: Create HR CSV Connected System (inbound source)
Write-TestStep "Step 4" "Creating HR CSV Connected System ($hrSystemName)"

$hrSystem = New-JIMConnectedSystem `
    -Name $hrSystemName `
    -Description "HR source for relative-date outbound scoping integration tests" `
    -ConnectorDefinitionId $csvConnector.id `
    -PassThru

$hrSettings = @{}
if ($filePathSetting) { $hrSettings[$filePathSetting.id] = @{ stringValue = $hrCsvFilePath } }
if ($delimiterSetting) { $hrSettings[$delimiterSetting.id] = @{ stringValue = "," } }
if ($objectTypeSetting) { $hrSettings[$objectTypeSetting.id] = @{ stringValue = "person" } }

Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues $hrSettings | Out-Null
Write-Host "  OK HR CSV configured (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 5: Create Target CSV Connected System (outbound export target)
Write-TestStep "Step 5" "Creating Target CSV Connected System ($targetSystemName)"

$targetSystem = New-JIMConnectedSystem `
    -Name $targetSystemName `
    -Description "Downstream provisioning target for relative-date outbound scoping tests" `
    -ConnectorDefinitionId $csvConnector.id `
    -PassThru

$targetSettings = @{}
if ($filePathSetting) { $targetSettings[$filePathSetting.id] = @{ stringValue = $targetCsvFilePath } }
if ($delimiterSetting) { $targetSettings[$delimiterSetting.id] = @{ stringValue = "," } }
if ($objectTypeSetting) { $targetSettings[$objectTypeSetting.id] = @{ stringValue = "user" } }

Set-JIMConnectedSystem -Id $targetSystem.id -SettingValues $targetSettings | Out-Null
Write-Host "  OK Target CSV configured (ID: $($targetSystem.id))" -ForegroundColor Green

# Step 6: Import schemas. The scenario seeds the HR CSV (with an ISO-8601 employeeStartDate
# value) and the header-only target CSV before calling setup, so employeeStartDate infers as a
# DateTime attribute and the target columns are discovered.
Write-TestStep "Step 6" "Importing schemas"

Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrObjectTypes = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes
$hrPersonType = $hrObjectTypes | Where-Object { $_.name -eq "person" }
if (-not $hrPersonType) { throw "HR 'person' object type not found in schema" }

# Fail loudly if employeeStartDate did not infer as DateTime; a Text-typed date column would make
# the relative-date export criterion meaningless and the scenario would silently mis-test.
$startCol = $hrPersonType.attributes | Where-Object { $_.name -eq "employeeStartDate" }
if (-not $startCol) { throw "HR schema is missing the 'employeeStartDate' column; check the seed CSV." }
if ($startCol.type -ne "DateTime") {
    throw "HR 'employeeStartDate' inferred as '$($startCol.type)', expected 'DateTime'. Ensure every seed row carries an ISO-8601 value so the File connector types it correctly."
}
Write-Host "  OK HR schema imported; employeeStartDate typed as DateTime" -ForegroundColor Green

Import-JIMConnectedSystemSchema -Id $targetSystem.id | Out-Null
$targetObjectTypes = Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes
$targetUserType = $targetObjectTypes | Where-Object { $_.name -eq "user" }
if (-not $targetUserType) { throw "Target 'user' object type not found in schema" }
Write-Host "  OK Target schema imported ('user' object type)" -ForegroundColor Green

# Step 7: Select HR object type and attributes (employeeStartDate must be selected so its value
# imports and can flow to the Metaverse Employee Start Date attribute for the export criterion).
Write-TestStep "Step 7" "Selecting HR object type and attributes"

Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -Selected $true | Out-Null

$hrAttrs = @("employeeId", "firstName", "lastName", "displayName", "email", "samAccountName", "employeeStartDate")
$hrAttrUpdates = @{}
foreach ($attr in $hrPersonType.attributes) {
    if ($attr.name -in $hrAttrs) {
        $isExternalId = ($attr.name -eq "employeeId")
        $hrAttrUpdates[$attr.id] = @{ selected = $true; isExternalId = $isExternalId }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrPersonType.id -AttributeUpdates $hrAttrUpdates | Out-Null
Write-Host "  OK Selected $($hrAttrUpdates.Count) HR attributes (employeeId is external ID)" -ForegroundColor Green

# Step 8: Select Target object type and attributes (samAccountName is the external ID anchor).
Write-TestStep "Step 8" "Selecting Target object type and attributes"

Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -Selected $true | Out-Null

$targetAttrs = @("samAccountName", "displayName", "email", "employeeId", "manager")
$targetAttrUpdates = @{}
foreach ($attr in $targetUserType.attributes) {
    if ($attr.name -in $targetAttrs) {
        $isExternalId = ($attr.name -eq "samAccountName")
        $targetAttrUpdates[$attr.id] = @{ selected = $true; isExternalId = $isExternalId }
    }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetUserType.id -AttributeUpdates $targetAttrUpdates | Out-Null
Write-Host "  OK Selected $($targetAttrUpdates.Count) Target attributes (samAccountName is external ID)" -ForegroundColor Green

# Step 9: Resolve Metaverse 'User' object type and attributes
Write-TestStep "Step 9" "Resolving Metaverse 'User' object type"

$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) { throw "Metaverse 'User' object type not found in seed data" }
$mvAttributes = @(Get-JIMMetaverseAttribute)
Write-Host "  OK Found 'User' MV object type (ID: $($mvUserType.id))" -ForegroundColor Green

# Step 10: Object matching rule (HR.employeeId <-> MV.Employee ID)
Write-TestStep "Step 10" "Configuring object matching rule"

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

# Step 11: Create the inbound (Import) sync rule (HR -> MV), UNSCOPED so every user projects and
# the Metaverse Object persists across the export boundary.
Write-TestStep "Step 11" "Creating import sync rule ($importRuleName)"

$importRule = New-JIMSyncRule `
    -Name $importRuleName `
    -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrPersonType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru
Write-Host "  OK Created import rule (ID: $($importRule.id))" -ForegroundColor Green

# Employee Start Date is the crux: the HR DateTime column flows to the Metaverse DateTime attribute
# that the export criterion evaluates.
$importMappings = @(
    @{ CSAttr = "employeeId"; MVAttr = "Employee ID" },
    @{ CSAttr = "firstName"; MVAttr = "First Name" },
    @{ CSAttr = "lastName"; MVAttr = "Last Name" },
    @{ CSAttr = "displayName"; MVAttr = "Display Name" },
    @{ CSAttr = "email"; MVAttr = "Email" },
    @{ CSAttr = "samAccountName"; MVAttr = "Account Name" },
    @{ CSAttr = "employeeStartDate"; MVAttr = "Employee Start Date" }
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
Write-Host "  OK Created $importMappingsCreated import attribute flow mappings (incl. Employee Start Date)" -ForegroundColor Green

# Step 12: Create the outbound (Export) sync rule (MV -> Target)
Write-TestStep "Step 12" "Creating export sync rule ($exportRuleName)"

$exportRule = New-JIMSyncRule `
    -Name $exportRuleName `
    -ConnectedSystemId $targetSystem.id `
    -ConnectedSystemObjectTypeId $targetUserType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -PassThru
Write-Host "  OK Created export rule (ID: $($exportRule.id))" -ForegroundColor Green

$exportMappings = @(
    @{ MVAttr = "Account Name"; CSAttr = "samAccountName" },
    @{ MVAttr = "Display Name"; CSAttr = "displayName" },
    @{ MVAttr = "Email"; CSAttr = "email" },
    @{ MVAttr = "Employee ID"; CSAttr = "employeeId" }
)

$exportMappingsCreated = 0
foreach ($mapping in $exportMappings) {
    $mvAttr = $mvAttributes | Where-Object { $_.name -eq $mapping.MVAttr }
    $csAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.CSAttr }
    if ($mvAttr -and $csAttr) {
        New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $csAttr.id `
            -SourceMetaverseAttributeId $mvAttr.id | Out-Null
        $exportMappingsCreated++
    } else {
        Write-Host "    WARNING Could not map $($mapping.MVAttr) -> $($mapping.CSAttr)" -ForegroundColor Yellow
    }
}
Write-Host "  OK Created $exportMappingsCreated export attribute flow mappings" -ForegroundColor Green

# The manager mapping is an expression flow over a Reference attribute: it proves a
# reconciler-driven provision evaluates reference attributes (#892). A direct Reference-to-Text
# mapping is rejected by design (type mismatch) and a CSV column cannot be typed as Reference,
# so the expression form is the File-connector-compatible way to export a reference. The
# reconciler loads flagged Metaverse Objects via a lean no-tracking query that carries reference
# values as FK scalars only; mv["Manager"] must evaluate from that scalar (producing the
# referenced Metaverse Object's ID) rather than silently evaluating to null.
$managerCsAttr = $targetUserType.attributes | Where-Object { $_.name -eq "manager" }
if (-not $managerCsAttr) { throw "Target 'manager' attribute not found in schema" }
New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
    -TargetConnectedSystemAttributeId $managerCsAttr.id `
    -Expression 'mv["Manager"]' | Out-Null
Write-Host "  OK Created manager expression mapping (mv[`"Manager`"] -> manager)" -ForegroundColor Green

# Step 13: Relative-date scoping criteria on the export rule - the "currently-started" window.
# All (AND) group: Employee Start Date <= now. Hours/0/FromNow resolves to the exact current
# instant (no midnight rounding) so the scenario can prove per-run re-evaluation against the wall
# clock. EnforceState is disabled so a control user does not accrue drift-correction UPDATE pending
# exports that would muddy the negative-control assertion.
Write-TestStep "Step 13" "Adding relative-date scoping criteria to export rule"

$mvStartDateAttr = $mvAttributes | Where-Object { $_.name -eq "Employee Start Date" }
if (-not $mvStartDateAttr) { throw "Metaverse 'Employee Start Date' attribute not found" }

$exportScopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $exportRule.id -Type All -PassThru

New-JIMScopingCriterion `
    -SyncRuleId $exportRule.id `
    -GroupId $exportScopeGroup.id `
    -MetaverseAttributeId $mvStartDateAttr.id `
    -ComparisonType LessThanOrEquals `
    -ValueMode Relative -RelativeCount 0 -RelativeUnit Hours -RelativeDirection FromNow | Out-Null

Set-JIMSyncRule -Id $exportRule.id -OutboundDeprovisionAction Disconnect -EnforceState $false | Out-Null
Write-Host "  OK Export rule scoped to Employee Start Date <= now (relative; EnforceState=false)" -ForegroundColor Green

# Step 14: Run profiles
Write-TestStep "Step 14" "Creating run profiles"

New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $hrSystem.id -RunType "FullImport" -FilePath $hrCsvFilePath | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $hrSystem.id -RunType "FullSynchronisation" | Out-Null
New-JIMRunProfile -Name "Export" -ConnectedSystemId $targetSystem.id -RunType "Export" | Out-Null
New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $targetSystem.id -RunType "FullImport" -FilePath $targetCsvFilePath | Out-Null
Write-Host "  OK Run profiles: HR (Full Import, Full Synchronisation); Target (Export, Full Import)" -ForegroundColor Green

# Step 15: Disable the built-in reconciler schedule so it cannot auto-fire mid-test; the scenario
# triggers it manually at the exact moment it wants a sweep. Manual triggering works regardless of
# a schedule's enabled state, so disabling only suppresses the automatic hourly run.
Write-TestStep "Step 15" "Disabling the built-in Temporal Scope Reconciliation schedule (manual trigger only)"

$reconSchedule = @(Get-JIMSchedule | Where-Object { $_.name -eq "Temporal Scope Reconciliation" })
if ($reconSchedule.Count -ne 1) {
    throw "Expected exactly one built-in 'Temporal Scope Reconciliation' schedule; found $($reconSchedule.Count)"
}
Disable-JIMSchedule -Id $reconSchedule[0].id -ErrorAction Stop | Out-Null
Write-Host "  OK Built-in reconciler schedule disabled (ID: $($reconSchedule[0].id))" -ForegroundColor Green

Write-TestSection "Scenario 13 Setup Complete"
Write-Host "  HR Connected System:     $hrSystemName (ID: $($hrSystem.id))" -ForegroundColor Cyan
Write-Host "  Target Connected System: $targetSystemName (ID: $($targetSystem.id))" -ForegroundColor Cyan
Write-Host "  Import Sync Rule:        $importRuleName (ID: $($importRule.id)) [unscoped project]" -ForegroundColor Cyan
Write-Host "  Export Sync Rule:        $exportRuleName (ID: $($exportRule.id))" -ForegroundColor Cyan
Write-Host "  Export scope: Employee Start Date <= now (relative, re-evaluated each run)" -ForegroundColor Cyan
