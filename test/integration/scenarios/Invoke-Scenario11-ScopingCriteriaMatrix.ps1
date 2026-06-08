# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 11: Sync Rule Scoping Criteria Evaluation Matrix

.DESCRIPTION
    Exercises the full operator x value-type x group-structure evaluation matrix that
    SyncRuleScopingCriteria exposes, complementing Scenario 10 (which covers the
    lifecycle action matrix on the common ILM shape but only one operator).

    For each matrix cell, the scenario configures a sandbox inbound sync rule with the
    cell's scoping criteria, then runs a single inbound sync that evaluates every rule
    against a deterministic seed dataset. Each rule projects to its own Metaverse Object
    Type, so per-cell assertions read back the projected MVO set by type and compare it
    to the cell's expected EmployeeId list.

    The matrix supports three coverage tiers (Quick / Default / Exhaustive) selectable
    via parameter or interactive menu. Each tier is a strict superset of the one below.

    Round-trip persistence (PRD req 20-21) and negative-cell API checks (PRD req 25-26)
    run before the main matrix so value-carrier drops on the API boundary and missing
    operator-type guards surface before per-cell results are computed.

    Scoping evaluation correctness is template-independent; Nano is sufficient and is
    the default. The -Template parameter is accepted for runner-API consistency but has
    no effect on the cell list, the seed, or expected match-sets.

.PARAMETER Step
    Cell-name filter. "All" (default) runs every cell in the chosen tier; a fully
    qualified cell name (e.g. "Text.Equals.Single.CS") runs a single cell. Composes
    with -OperatorFilter via AND.

.PARAMETER OperatorFilter
    Operator-level filter. "All" (default) places no restriction; a bare operator
    name (e.g. "NotContains") restricts cells to that operator. Composes with -Step.

.PARAMETER Template
    Data scale template. Informational only - the matrix uses its bespoke deterministic
    seed regardless. Accepted to keep the runner-API consistent across scenarios.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200).

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER Quick
    Run the Quick tier (~12 cells, target < 90s). Mutually exclusive with -Exhaustive.

.PARAMETER Exhaustive
    Run the Exhaustive tier (~152 cells, target < 10 min). Mutually exclusive with -Quick.

.PARAMETER IncludeNegativeCells
    When true (default), probes API behaviour for semantically-invalid operator/type
    combinations and reports the observed status. Informational; does not fail the run.

.PARAMETER DirectoryConfig
    Accepted for runner-API consistency. Not used: Scenario 11 has no LDAP step.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$OperatorFilter = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [switch]$Quick,

    [Parameter(Mandatory=$false)]
    [switch]$Exhaustive,

    [Parameter(Mandatory=$false)]
    [bool]$IncludeNegativeCells = $true,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig,

    # Accepted for runner-API compatibility (passed by Run-IntegrationTests.ps1 when
    # using directory snapshot images). Scenario 11 has no LDAP step and no AD users
    # to populate, so this is a no-op here; suppressing unused-variable analysis.
    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate
)

Set-StrictMode -Version Latest
$null = $SkipPopulate
$ErrorActionPreference = 'Stop'
$ConfirmPreference = 'None'

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

# ─── Helpers (defined before first use) ─────────────────────────────────────────

function Get-CsoTypeName {
    param([Parameter(Mandatory)][string]$CellName)
    return "Sc11_$($CellName -replace '\.', '_')"
}

function Get-MvTypeName {
    param([Parameter(Mandatory)][string]$CellName)
    return "Sc11Mvo_$($CellName -replace '\.', '_')"
}

# Build a New-JIMScopingCriterion call from a manifest criterion record. Resolves
# the attribute name against the CSO type's discovered attributes, picks the right
# -*Value parameter for the cell's valueType, and forwards CaseSensitive when the
# manifest specifies it.
function Add-CriterionFromManifest {
    param(
        [Parameter(Mandatory)][int]$SyncRuleId,
        [Parameter(Mandatory)][int]$GroupId,
        [Parameter(Mandatory)]$Criterion,
        [Parameter(Mandatory)]$CsoType
    )

    $csAttr = $CsoType.attributes | Where-Object { $_.name -eq $Criterion.attribute } | Select-Object -First 1
    if (-not $csAttr) {
        throw "Criterion references attribute '$($Criterion.attribute)' which is not on CSO type '$($CsoType.name)'"
    }

    $apiArgs = @{
        SyncRuleId                   = $SyncRuleId
        GroupId                      = $GroupId
        ConnectedSystemAttributeId   = $csAttr.id
        ComparisonType               = $Criterion.operator
    }
    if ($null -ne $Criterion.caseSensitive) {
        $apiArgs.CaseSensitive = [bool]$Criterion.caseSensitive
    }
    switch ($Criterion.valueType) {
        'Text'       { $apiArgs.StringValue   = [string]$Criterion.value }
        'Number'     { $apiArgs.IntValue      = [int]$Criterion.value }
        'LongNumber' { $apiArgs.LongValue     = [long]$Criterion.value }
        'DateTime'   { $apiArgs.DateTimeValue = [datetime]$Criterion.value }
        'Boolean'    { $apiArgs.BoolValue     = [bool]$Criterion.value }
        'Guid'       { $apiArgs.GuidValue     = [Guid]$Criterion.value }
        default      { throw "Unsupported criterion valueType '$($Criterion.valueType)'" }
    }
    New-JIMScopingCriterion @apiArgs | Out-Null
}

# Configure scoping for a single cell against its sync rule. Handles all four group
# structures (Single / AllPair / AnyPair / Nested).
function Set-CellScoping {
    param(
        [Parameter(Mandatory)][int]$SyncRuleId,
        [Parameter(Mandatory)]$Cell,
        [Parameter(Mandatory)]$CsoType
    )

    switch ($Cell.group) {
        'Single' {
            $g = New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -Type 'All' -PassThru
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $g.id -Criterion $Cell.primary -CsoType $CsoType
        }
        'AllPair' {
            $g = New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -Type 'All' -PassThru
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $g.id -Criterion $Cell.primary -CsoType $CsoType
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $g.id -Criterion $Cell.secondary -CsoType $CsoType
        }
        'AnyPair' {
            $g = New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -Type 'Any' -PassThru
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $g.id -Criterion $Cell.primary -CsoType $CsoType
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $g.id -Criterion $Cell.secondary -CsoType $CsoType
        }
        'Nested' {
            # (primary OR tertiary) AND secondary - top All, child Any holds primary + tertiary,
            # secondary sits alongside the child group in the top All.
            $top = New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -Type 'All' -PassThru
            $child = New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -ParentGroupId $top.id -Type 'Any' -PassThru
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $child.id -Criterion $Cell.primary -CsoType $CsoType
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $child.id -Criterion $Cell.tertiary -CsoType $CsoType
            Add-CriterionFromManifest -SyncRuleId $SyncRuleId -GroupId $top.id -Criterion $Cell.secondary -CsoType $CsoType
        }
        default {
            throw "Unknown cell group structure '$($Cell.group)'"
        }
    }
}

# ─── Argument validation ────────────────────────────────────────────────────────

if ($Quick -and $Exhaustive) {
    throw "-Quick and -Exhaustive are mutually exclusive. Pick one tier (or neither for Default)."
}

$activeTier = if ($Quick) { 'Quick' } elseif ($Exhaustive) { 'Exhaustive' } else { 'Default' }

if (-not $ApiKey) {
    throw "API key required for authentication. Pass -ApiKey or set it via the runner."
}

Write-TestSection "Scenario 11 Setup: Sync Rule Scoping Criteria Matrix"
Write-Host "  Coverage tier:    $activeTier" -ForegroundColor Cyan
Write-Host "  Step filter:      $Step" -ForegroundColor Cyan
Write-Host "  Negative cells:   $IncludeNegativeCells" -ForegroundColor Cyan
if ($Template -ne 'Nano') {
    Write-Host "  Template:         $Template (informational; matrix uses its own bespoke seed)" -ForegroundColor DarkYellow
}

# ─── Load module and connect ────────────────────────────────────────────────────

Write-TestStep "Step 1" "Importing JIM PowerShell module and connecting"

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM at $JIMUrl" -ForegroundColor Green

# ─── Load manifest ──────────────────────────────────────────────────────────────

Write-TestStep "Step 2" "Loading scoping criteria matrix manifest"

$manifestPath = "$PSScriptRoot/data/scoping-criteria-matrix.json"
if (-not (Test-Path $manifestPath)) {
    throw "Manifest not found at $manifestPath. Run pwsh test/integration/scenarios/data/Build-ScopingCriteriaMatrix.ps1 to regenerate."
}

$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json -Depth 10
$allCells = @($manifest.cells)
Write-Host "  OK Loaded manifest: $($allCells.Count) cells defined" -ForegroundColor Green

# ─── Filter cells by tier and -Step ─────────────────────────────────────────────

$tierCells = @($allCells | Where-Object { $_.tiers -contains $activeTier })

if ($OperatorFilter -ne 'All' -and -not [string]::IsNullOrWhiteSpace($OperatorFilter)) {
    $filteredByOp = @($tierCells | Where-Object { $_.primary.operator -eq $OperatorFilter })
    if ($filteredByOp.Count -eq 0) {
        throw "-OperatorFilter '$OperatorFilter' did not match any cell's operator in tier '$activeTier'. Use a SearchComparisonType operator name or 'All'."
    }
    $tierCells = $filteredByOp
}

if ($Step -ne 'All') {
    $byName = @($tierCells | Where-Object { $_.name -eq $Step })
    $byOperator = @($tierCells | Where-Object { $_.primary.operator -eq $Step })
    if ($byName.Count -gt 0) {
        $tierCells = $byName
    } elseif ($byOperator.Count -gt 0) {
        $tierCells = $byOperator
    } else {
        throw "-Step '$Step' did not match any cell name or operator in the $activeTier tier (after operator filter '$OperatorFilter')."
    }
}

if ($tierCells.Count -eq 0) {
    throw "No cells selected for tier '$activeTier' with -Step '$Step' and -OperatorFilter '$OperatorFilter'."
}

Write-Host "  OK Selected $($tierCells.Count) cells for tier '$activeTier'" -ForegroundColor Green

# ─── Factory reset to clean state ───────────────────────────────────────────────

Write-TestStep "Step 3" "Factory-resetting JIM to a clean state"
Reset-JIMSystem -Force -IncludeAdministrators -AcknowledgeAdministratorLockout | Out-Null
Write-Host "  OK Reset complete" -ForegroundColor Green

# ─── Round-trip persistence sub-test (PRD req 20-21) ────────────────────────────

Write-TestStep "Step 4" "Round-trip persistence sub-test for every value carrier"

# Build minimal scaffolding: per-type MV attributes attached to one MV type, a small
# CSV-backed CSO type, and one EXPORT sync rule that hosts criteria against the MV
# attributes. Export-direction rules accept Metaverse-attribute criteria, which is
# what we need to exercise all six typed value carriers.

$rtAttrs = @{}
$rtAttrs['Text']     = New-JIMMetaverseAttribute -Name 'Sc11Rt_Text'     -Type Text     -ErrorAction Stop
$rtAttrs['Number']   = New-JIMMetaverseAttribute -Name 'Sc11Rt_Number'   -Type Integer  -ErrorAction Stop
$rtAttrs['DateTime'] = New-JIMMetaverseAttribute -Name 'Sc11Rt_DateTime' -Type DateTime -ErrorAction Stop
$rtAttrs['Boolean']  = New-JIMMetaverseAttribute -Name 'Sc11Rt_Boolean'  -Type Boolean  -ErrorAction Stop
$rtAttrs['Guid']     = New-JIMMetaverseAttribute -Name 'Sc11Rt_Guid'     -Type Guid     -ErrorAction Stop

$rtAttrs['LongNumber'] = New-JIMMetaverseAttribute -Name 'Sc11Rt_LongNumber' -Type LongNumber -ErrorAction Stop

$rtMvType = New-JIMMetaverseObjectType -Name 'Sc11RoundTripMVO' -PluralName 'Sc11RoundTripMVOs' `
    -AttributeIds @($rtAttrs.Values | ForEach-Object { $_.id }) -ErrorAction Stop

# Tiny throwaway CSV so we can spin up a CSO type that hosts the export sync rule.
$rtTestDataDir = "$PSScriptRoot/../test-data"
$null = New-Item -ItemType Directory -Force -Path $rtTestDataDir
$rtCsvHostPath = Join-Path $rtTestDataDir 'scenario11-roundtrip.csv'
"ObjectType,EmployeeId`nSc11RoundTripCSO,E000" | Set-Content -Path $rtCsvHostPath -Encoding utf8 -NoNewline
Write-FilesToConnectorVolume -SourceDir $rtTestDataDir -Files @(
    @{ SourceFile = 'scenario11-roundtrip.csv'; DestinationPath = '/connector-files/test-data/scenario11-roundtrip.csv' }
) | Out-Null

$csvConnectorDef = Get-JIMConnectorDefinition | Where-Object { $_.name -eq 'JIM File Connector' }
$csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnectorDef.id
$rtSettings = @{}
$rtSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'File Path' }).id]          = @{ stringValue = '/connector-files/test-data/scenario11-roundtrip.csv' }
$rtSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'Delimiter' }).id]          = @{ stringValue = ',' }
$rtSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'Object Type Column' }).id] = @{ stringValue = 'ObjectType' }

$rtSystem = New-JIMConnectedSystem -Name 'Sc11RoundTripSource' -ConnectorDefinitionId $csvConnectorDef.id -PassThru
Set-JIMConnectedSystem -Id $rtSystem.id -SettingValues $rtSettings | Out-Null
Import-JIMConnectedSystemSchema -Id $rtSystem.id | Out-Null

$rtCsoType = @(Get-JIMConnectedSystem -Id $rtSystem.id -ObjectTypes) | Where-Object { $_.name -eq 'Sc11RoundTripCSO' } | Select-Object -First 1
if (-not $rtCsoType) { throw "Round-trip CSO type not discovered" }

$rtCsoAttrUpdates = @{}
foreach ($attr in $rtCsoType.attributes) {
    if ($attr.name -eq 'ObjectType') { continue }
    $rtCsoAttrUpdates[$attr.id] = @{ selected = $true; isExternalId = ($attr.name -eq 'EmployeeId') }
}
Set-JIMConnectedSystemObjectType -ConnectedSystemId $rtSystem.id -ObjectTypeId $rtCsoType.id -Selected $true | Out-Null
Set-JIMConnectedSystemAttribute -ConnectedSystemId $rtSystem.id -ObjectTypeId $rtCsoType.id -AttributeUpdates $rtCsoAttrUpdates | Out-Null

$rtRule = New-JIMSyncRule -Name 'Sc11RoundTripExportRule' -ConnectedSystemId $rtSystem.id `
    -ConnectedSystemObjectTypeId $rtCsoType.id -MetaverseObjectTypeId $rtMvType.id `
    -Direction Export -PassThru
$rtGroup = New-JIMScopingCriteriaGroup -SyncRuleId $rtRule.id -Type 'All' -PassThru

$rtCases = @(
    @{ key='Text';       attr=$rtAttrs['Text'].id;       op='Equals';      field='stringValue';   value='RoundTrip'                                       }
    @{ key='Number';     attr=$rtAttrs['Number'].id;     op='GreaterThan'; field='intValue';      value=42                                                }
    @{ key='LongNumber'; attr=$rtAttrs['LongNumber'].id; op='LessThan';    field='longValue';     value=8000000000                                        }
    @{ key='DateTime';   attr=$rtAttrs['DateTime'].id;   op='Equals';      field='dateTimeValue'; value=[datetime]'2024-06-15T00:00:00Z'                  }
    @{ key='Boolean';    attr=$rtAttrs['Boolean'].id;    op='Equals';      field='boolValue';     value=$true                                             }
    @{ key='Guid';       attr=$rtAttrs['Guid'].id;       op='Equals';      field='guidValue';     value=[Guid]'11111111-2222-3333-4444-555555555555'      }
)

$roundTripPass = 0
$roundTripFail = 0
foreach ($case in $rtCases) {
    $apiArgs = @{
        SyncRuleId           = $rtRule.id
        GroupId              = $rtGroup.id
        MetaverseAttributeId = $case.attr
        ComparisonType       = $case.op
        CaseSensitive        = $false
        PassThru             = $true
    }
    # Pick the cmdlet parameter matching the value carrier under test.
    switch ($case.key) {
        'Text'       { $apiArgs.StringValue   = $case.value }
        'Number'     { $apiArgs.IntValue      = $case.value }
        'LongNumber' { $apiArgs.LongValue     = $case.value }
        'DateTime'   { $apiArgs.DateTimeValue = $case.value }
        'Boolean'    { $apiArgs.BoolValue     = $case.value }
        'Guid'       { $apiArgs.GuidValue     = $case.value }
    }
    $created = New-JIMScopingCriterion @apiArgs

    # Read back via the group endpoint to verify persistence-then-retrieval round-trips.
    $persisted = Get-JIMScopingCriteria -SyncRuleId $rtRule.id -GroupId $rtGroup.id
    $criterion = $persisted.criteria | Where-Object { $_.id -eq $created.id }
    if (-not $criterion) {
        Write-Host "    FAIL $($case.key): criterion not persisted on read-back" -ForegroundColor Red
        $roundTripFail++
        continue
    }

    $actualValue = $criterion.($case.field)
    $expected = $case.value
    if ($case.key -eq 'DateTime') {
        $actualValue = ([datetime]$actualValue).ToUniversalTime()
        $expected = $expected.ToUniversalTime()
    }
    if ($case.key -eq 'Guid') {
        $actualValue = [Guid]$actualValue
    }

    $valueOk = $actualValue -eq $expected
    $csOk = ($criterion.caseSensitive -eq $false)
    if ($valueOk -and $csOk) {
        Write-Host "    OK  $($case.key) carrier ($expected)" -ForegroundColor Green
        $roundTripPass++
    } else {
        Write-Host "    FAIL $($case.key): expected $expected got $actualValue (CS expected false, got $($criterion.caseSensitive))" -ForegroundColor Red
        $roundTripFail++
    }
}

Write-Host "  Round-trip sub-test: $roundTripPass passed, $roundTripFail failed" -ForegroundColor $(if ($roundTripFail -eq 0) { 'Green' } else { 'Red' })

# ─── Negative-cell API checks (PRD req 25-26) ───────────────────────────────────

$negativeResults = New-Object System.Collections.Generic.List[object]
if ($IncludeNegativeCells) {
    Write-TestStep "Step 5" "Negative-cell API probes for semantically invalid combinations"

    # Reuse the round-trip MV type for the export-rule context.
    $negCases = @(
        @{ description = 'Contains on Boolean attribute'; attr = $rtAttrs['Boolean'].id; op = 'Contains';    valueField = 'stringValue'; v = 'true' }
        @{ description = 'GreaterThan on Guid attribute'; attr = $rtAttrs['Guid'].id;    op = 'GreaterThan'; valueField = 'stringValue'; v = '0' }
        @{ description = 'StartsWith on Boolean';         attr = $rtAttrs['Boolean'].id; op = 'StartsWith';  valueField = 'stringValue'; v = 'tr' }
        @{ description = 'IntValue on Text attribute';    attr = $rtAttrs['Text'].id;    op = 'Equals';      valueField = 'intValue';    v = 42 }
    )

    foreach ($case in $negCases) {
        # Use the cmdlet so the same code-path validation runs. The cmdlet doesn't
        # enforce type-vs-value pairings, so it'll happily forward an invalid combo
        # to the API and we observe the API's response.
        $apiArgs = @{
            SyncRuleId           = $rtRule.id
            GroupId              = $rtGroup.id
            MetaverseAttributeId = $case.attr
            ComparisonType       = $case.op
            ErrorAction          = 'Stop'
        }
        switch ($case.valueField) {
            'stringValue' { $apiArgs.StringValue = $case.v }
            'intValue'    { $apiArgs.IntValue    = $case.v }
        }

        $observed = 'unknown'
        try {
            New-JIMScopingCriterion @apiArgs | Out-Null
            $observed = '201 created (no validation - known SHOULD gap)'
        }
        catch {
            $msg = $_.Exception.Message
            $observed = if ($msg -match '400') { '400 BadRequest' } elseif ($msg -match '404') { '404 NotFound' } else { "error: $msg" }
        }
        $negativeResults.Add([ordered]@{ case = $case.description; observed = $observed }) | Out-Null
        Write-Host "    $($case.description) -> $observed" -ForegroundColor Yellow
    }
} else {
    Write-Host "  (skipped: -IncludeNegativeCells:`$false)" -ForegroundColor DarkGray
}

# Reset between sub-tests and main matrix so the state is pristine.
Write-TestStep "Step 6" "Factory-resetting JIM between sub-tests and main matrix"
Reset-JIMSystem -Force -IncludeAdministrators -AcknowledgeAdministratorLockout | Out-Null
Write-Host "  OK Reset complete" -ForegroundColor Green

# ─── Main matrix setup: shared MV attributes ─────────────────────────────────

Write-TestStep "Step 7" "Creating MV attributes shared by all matrix cells"

$mvAttrIds = @{}
foreach ($attr in $manifest.seedAttributes) {
    switch ($attr.type) {
        'Text'       { $created = New-JIMMetaverseAttribute -Name $attr.name -Type Text     -ErrorAction Stop }
        'Number'     { $created = New-JIMMetaverseAttribute -Name $attr.name -Type Integer  -ErrorAction Stop }
        'DateTime'   { $created = New-JIMMetaverseAttribute -Name $attr.name -Type DateTime -ErrorAction Stop }
        'Boolean'    { $created = New-JIMMetaverseAttribute -Name $attr.name -Type Boolean  -ErrorAction Stop }
        'Guid'       { $created = New-JIMMetaverseAttribute -Name $attr.name -Type Guid     -ErrorAction Stop }
        'LongNumber' { $created = New-JIMMetaverseAttribute -Name $attr.name -Type LongNumber -ErrorAction Stop }
        default      { throw "Unknown manifest attribute type '$($attr.type)' for '$($attr.name)'" }
    }
    $mvAttrIds[$attr.name] = $created.id
}
Write-Host "  OK Created $($mvAttrIds.Count) MV attributes" -ForegroundColor Green

# ─── Build the fanned-out CSV ──────────────────────────────────────────────────

Write-TestStep "Step 8" "Building fanned-out seed CSV (one block per cell)"

$columns = @($manifest.seedAttributes | ForEach-Object { $_.name })
$headerLine = "ObjectType," + ($columns -join ',')

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add($headerLine) | Out-Null

foreach ($cell in $tierCells) {
    $csoTypeName = Get-CsoTypeName -CellName $cell.name
    foreach ($row in $manifest.seed) {
        $values = $columns | ForEach-Object {
            $col = $_
            $v = $row.$col
            # The external ID column (Sc11EmployeeId) is the worker's primary external ID and
            # must be unique across the ENTIRE import - not just per-CSO-type. We fan out by
            # prefixing with the cell's CSO type name so each cell's E001 is distinct from
            # every other cell's E001 in the connector space. The assertion strips this
            # prefix back off when comparing against the manifest's expected EmployeeId set.
            if ($col -eq 'Sc11EmployeeId' -and $null -ne $v) {
                "${csoTypeName}::$v"
            }
            elseif ($null -eq $v) { '' }
            elseif ($v -is [bool]) { if ($v) { 'true' } else { 'false' } }
            elseif ($v -is [datetime]) {
                # ConvertFrom-Json auto-converts ISO date strings into [DateTime] objects,
                # and [string] on a DateTime uses the current culture's format. The worker
                # container runs with en-GB, where "01/15/2020" fails to parse (day 15 of
                # month 1 is fine, but 01/15 means day=1, month=15 - invalid). Force ISO
                # 8601 round-trip format ('o') so the connector parses consistently
                # regardless of host or container culture.
                $v.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
            else { [string]$v }
        }
        $quoted = $values | ForEach-Object {
            if ($_ -match '[,"\r\n]') { '"' + ($_ -replace '"', '""') + '"' } else { $_ }
        }
        $lines.Add(($csoTypeName + ',' + ($quoted -join ','))) | Out-Null
    }
}

$matrixCsvDir = "$PSScriptRoot/../test-data"
$null = New-Item -ItemType Directory -Force -Path $matrixCsvDir
$matrixCsvPath = Join-Path $matrixCsvDir 'scenario11-matrix.csv'
[System.IO.File]::WriteAllLines($matrixCsvPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "  OK Wrote fanned-out CSV: $($lines.Count - 1) data rows for $($tierCells.Count) cells" -ForegroundColor Green

Write-FilesToConnectorVolume -SourceDir $matrixCsvDir -Files @(
    @{ SourceFile = 'scenario11-matrix.csv'; DestinationPath = '/connector-files/test-data/scenario11-matrix.csv' }
) | Out-Null
Write-Host "  OK CSV staged into connector volume" -ForegroundColor Green

# ─── Create the connected system and import schema ─────────────────────────────

Write-TestStep "Step 9" "Creating connected system and importing schema"

$csvConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq 'JIM File Connector' }
$csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id

$matrixSettings = @{}
$matrixSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'File Path' }).id]          = @{ stringValue = '/connector-files/test-data/scenario11-matrix.csv' }
$matrixSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'Delimiter' }).id]          = @{ stringValue = ',' }
$matrixSettings[($csvConnectorFull.settings | Where-Object { $_.name -eq 'Object Type Column' }).id] = @{ stringValue = 'ObjectType' }

$matrixSystem = New-JIMConnectedSystem -Name 'Sc11ScopingMatrixSource' -Description 'Scenario 11 scoping criteria matrix source' `
    -ConnectorDefinitionId $csvConnector.id -PassThru
Set-JIMConnectedSystem -Id $matrixSystem.id -SettingValues $matrixSettings | Out-Null
Write-Host "  OK Connected system created (ID: $($matrixSystem.id))" -ForegroundColor Green

Import-JIMConnectedSystemSchema -Id $matrixSystem.id | Out-Null
$matrixCsoTypes = @(Get-JIMConnectedSystem -Id $matrixSystem.id -ObjectTypes)
Write-Host "  OK Schema imported: $($matrixCsoTypes.Count) CSO types discovered" -ForegroundColor Green

if ($matrixCsoTypes.Count -ne $tierCells.Count) {
    throw "Schema import produced $($matrixCsoTypes.Count) CSO types, expected $($tierCells.Count). Manifest cells and CSV ObjectType values are out of sync."
}

# ─── Configure each CSO type ─────────────────────────────────────────────────

Write-TestStep "Step 10" "Selecting CSO types and attributes ($($matrixCsoTypes.Count) types)"

$csoTypeByName = @{}
foreach ($csoType in $matrixCsoTypes) {
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $matrixSystem.id -ObjectTypeId $csoType.id -Selected $true | Out-Null
    $attrUpdates = @{}
    foreach ($attr in $csoType.attributes) {
        if ($attr.name -eq 'ObjectType') { continue }
        $attrUpdates[$attr.id] = @{ selected = $true; isExternalId = ($attr.name -eq 'Sc11EmployeeId') }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $matrixSystem.id -ObjectTypeId $csoType.id -AttributeUpdates $attrUpdates | Out-Null
    $csoTypeByName[$csoType.name] = $csoType
}
Write-Host "  OK All CSO types configured" -ForegroundColor Green

# ─── Create one MV type per cell ───────────────────────────────────────────────

Write-TestStep "Step 11" "Creating $($tierCells.Count) MV object types"

$cellMvTypes = @{}
foreach ($cell in $tierCells) {
    $mvTypeName = Get-MvTypeName -CellName $cell.name
    $mvTypePlural = "${mvTypeName}s"
    $mvType = New-JIMMetaverseObjectType -Name $mvTypeName -PluralName $mvTypePlural `
        -AttributeIds @($mvAttrIds.Values) -ErrorAction Stop
    $cellMvTypes[$cell.name] = $mvType
}
Write-Host "  OK All MV object types created" -ForegroundColor Green

# ─── Create one sync rule per cell with its scoping criteria ───────────────────

Write-TestStep "Step 12" "Creating $($tierCells.Count) sync rules with scoping criteria"

$cellRules = @{}
foreach ($cell in $tierCells) {
    $csoTypeName = Get-CsoTypeName -CellName $cell.name
    $csoType = $csoTypeByName[$csoTypeName]
    if (-not $csoType) { throw "CSO type '$csoTypeName' not found for cell '$($cell.name)'" }
    $mvType = $cellMvTypes[$cell.name]

    $ruleName = "Sc11Rule_$($cell.name -replace '\.', '_')"
    $rule = New-JIMSyncRule -Name $ruleName -ConnectedSystemId $matrixSystem.id `
        -ConnectedSystemObjectTypeId $csoType.id -MetaverseObjectTypeId $mvType.id `
        -Direction Import -ProjectToMetaverse -PassThru

    # Add an attribute-flow rule so the projected MVO has Sc11EmployeeId set. Per-cell
    # assertions read back this attribute to identify which seed rows the rule projected.
    # Without the flow, projection still happens but the MVO has no readable identifier
    # to compare against the expected EmployeeId set.
    $csoEmployeeIdAttr = $csoType.attributes | Where-Object { $_.name -eq 'Sc11EmployeeId' } | Select-Object -First 1
    if (-not $csoEmployeeIdAttr) {
        throw "CSO type '$csoTypeName' has no Sc11EmployeeId attribute - matrix CSV is malformed."
    }
    New-JIMSyncRuleMapping -SyncRuleId $rule.id -SourceConnectedSystemAttributeId $csoEmployeeIdAttr.id `
        -TargetMetaverseAttributeId $mvAttrIds['Sc11EmployeeId'] | Out-Null

    Set-CellScoping -SyncRuleId $rule.id -Cell $cell -CsoType $csoType
    $cellRules[$cell.name] = $rule
}
Write-Host "  OK All sync rules configured" -ForegroundColor Green

# ─── Create Full Import run profile ───────────────────────────────────────────

Write-TestStep "Step 13" "Creating Full Import run profile"

$importProfile = New-JIMRunProfile -ConnectedSystemId $matrixSystem.id -Name 'Sc11 Full Import' `
    -RunType FullImport -FilePath '/connector-files/test-data/scenario11-matrix.csv' -PassThru
$syncProfile = New-JIMRunProfile -ConnectedSystemId $matrixSystem.id -Name 'Sc11 Full Synchronisation' `
    -RunType FullSynchronisation -PassThru
Write-Host "  OK Run profiles created (Import: $($importProfile.id), Sync: $($syncProfile.id))" -ForegroundColor Green

# ─── Run Full Import then Full Synchronisation ─────────────────────────────────
# Import brings the CSV rows into the connector space as CSOs. Synchronisation runs
# the sync rule evaluator over those CSOs, applying scoping and projecting MVOs.

Write-TestStep "Step 14" "Running Full Import + Full Synchronisation (single sync evaluates all $($tierCells.Count) rules)"

$importActivity = Start-JIMRunProfile -ConnectedSystemId $matrixSystem.id -RunProfileId $importProfile.id -Wait -PassThru
Write-Host "  OK Full Import complete (activity ID: $($importActivity.activityId))" -ForegroundColor Green
$syncActivity = Start-JIMRunProfile -ConnectedSystemId $matrixSystem.id -RunProfileId $syncProfile.id -Wait -PassThru
Write-Host "  OK Full Synchronisation complete (activity ID: $($syncActivity.activityId))" -ForegroundColor Green

# ─── Per-cell assertions ───────────────────────────────────────────────────────

Write-TestStep "Step 15" "Reading per-cell projections and comparing to expected sets"


$cellResults = New-Object System.Collections.Generic.List[object]
$cellPass = 0
$cellFail = 0

foreach ($cell in $tierCells) {
    $mvType = $cellMvTypes[$cell.name]
    $expected = @($cell.expected | Sort-Object)

    $csoTypeName = Get-CsoTypeName -CellName $cell.name
    $expectedPrefix = "${csoTypeName}::"
    $mvos = @(Get-JIMMetaverseObject -ObjectTypeId $mvType.id -All -Attributes 'Sc11EmployeeId')
    $actual = @()
    foreach ($mvo in $mvos) {
        # MetaverseObjectHeaderDto exposes .attributes as a flat hashtable keyed by
        # attribute name, with single-valued attributes stored as the bare value
        # (multi-valued attributes would be an array). Sc11EmployeeId is single-valued.
        $eidValue = $null
        if ($mvo.PSObject.Properties.Name -contains 'attributes' -and $mvo.attributes) {
            $eidValue = $mvo.attributes.Sc11EmployeeId
        }
        if ($eidValue) {
            # Strip the cell-specific prefix the CSV builder applied so we compare against
            # the manifest's bare EmployeeId set (E001, E002, ...).
            $stringValue = [string]$eidValue
            if ($stringValue.StartsWith($expectedPrefix)) {
                $actual += $stringValue.Substring($expectedPrefix.Length)
            } else {
                $actual += $stringValue
            }
        }
    }
    $actual = @($actual | Sort-Object)

    $missing = @($expected | Where-Object { $_ -notin $actual })
    $extra = @($actual | Where-Object { $_ -notin $expected })

    if ($missing.Count -eq 0 -and $extra.Count -eq 0) {
        $cellPass++
        $cellResults.Add([ordered]@{ name = $cell.name; status = 'pass' }) | Out-Null
    } else {
        $cellFail++
        $cellResults.Add([ordered]@{ name = $cell.name; status = 'fail'; expected = $expected; actual = $actual; missing = $missing; extra = $extra }) | Out-Null
        Write-Host "    FAIL $($cell.name)" -ForegroundColor Red
        Write-Host "      Expected: [$($expected -join ',')]" -ForegroundColor DarkGray
        Write-Host "      Actual:   [$($actual -join ',')]" -ForegroundColor DarkGray
        if ($missing.Count) { Write-Host "      Missing:  [$($missing -join ',')]" -ForegroundColor Red }
        if ($extra.Count)   { Write-Host "      Extra:    [$($extra -join ',')]"   -ForegroundColor Red }
    }
}

Write-Host ""
Write-Host "  Matrix results: $cellPass passed, $cellFail failed (tier: $activeTier, cells: $($tierCells.Count))" -ForegroundColor $(if ($cellFail -eq 0) { 'Green' } else { 'Red' })

# ─── Final teardown ────────────────────────────────────────────────────────────

Write-TestStep "Step 16" "Final factory reset"
Reset-JIMSystem -Force -IncludeAdministrators -AcknowledgeAdministratorLockout | Out-Null
Write-Host "  OK Reset complete" -ForegroundColor Green

# ─── Result aggregation ────────────────────────────────────────────────────────

$overallPass = ($cellFail -eq 0) -and ($roundTripFail -eq 0)
Write-TestSection "Scenario 11 Summary"
Write-Host "  Coverage tier:        $activeTier" -ForegroundColor Cyan
Write-Host "  Round-trip sub-test:  $roundTripPass / $($roundTripPass + $roundTripFail)" -ForegroundColor $(if ($roundTripFail -eq 0) { 'Green' } else { 'Red' })
Write-Host "  Matrix cells:         $cellPass / $($cellPass + $cellFail)" -ForegroundColor $(if ($cellFail -eq 0) { 'Green' } else { 'Red' })
Write-Host "  Negative-cell probes: $($negativeResults.Count) observed (informational)" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Result: $(if ($overallPass) { 'PASS' } else { 'FAIL' })" -ForegroundColor $(if ($overallPass) { 'Green' } else { 'Red' })

if (-not $overallPass) {
    throw "Scenario 11 failed: $cellFail cell(s) and $roundTripFail round-trip case(s) did not match expected results."
}
