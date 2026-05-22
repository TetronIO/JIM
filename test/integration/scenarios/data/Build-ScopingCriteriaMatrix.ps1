# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Generates the canonical Scenario 11 scoping criteria matrix manifest.

.DESCRIPTION
    Emits two files in this directory:
    - scoping-criteria-matrix.json   (the manifest the scenario consumes at runtime)
    - scoping-criteria-seed.csv      (the deterministic seed data the scenario imports)

    The manifest is the canonical artifact: it declares the seed dataset, the seed
    attribute schema, and every matrix cell with its operator/type/group/expected-set
    tuple. The scenario script (Invoke-Scenario11-ScopingCriteriaMatrix.ps1) reads
    this manifest and executes each cell as a sandbox sync rule against a per-cell
    Connected System Object Type.

    This generator exists so the manifest content is derivable from a small axis
    spec rather than hand-rolling ~256 cell entries by hand. Run this whenever the
    seed shape, operators, or expected-set logic changes. The generator output is
    deterministic; running it twice with the same inputs produces byte-identical files.

.PARAMETER OutputPath
    Directory to write the manifest and seed CSV into. Defaults to the script's
    own directory so 'pwsh Build-ScopingCriteriaMatrix.ps1' works without arguments.

.EXAMPLE
    pwsh test/integration/scenarios/data/Build-ScopingCriteriaMatrix.ps1

    Generates scoping-criteria-matrix.json and scoping-criteria-seed.csv alongside
    this generator script.
#>

[CmdletBinding()]
param(
    [string]$OutputPath = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─── Seed dataset ───────────────────────────────────────────────────────────────
# 15 deterministic person records. Attribute values are picked so every applicable
# (operator, type) pair has a non-empty match set and a non-empty complement. E015
# has all attributes null so null-handling is exercised by every cell.

$seedAttributes = @(
    @{ name = 'EmployeeId';         type = 'Text';       isExternalId = $true }
    @{ name = 'Department';         type = 'Text' }
    @{ name = 'JobTitle';           type = 'Text' }
    @{ name = 'EmployeeNumber';     type = 'Number' }
    @{ name = 'LongEmployeeNumber'; type = 'LongNumber' }
    @{ name = 'HireDate';           type = 'DateTime' }
    @{ name = 'IsActive';           type = 'Boolean' }
    @{ name = 'DepartmentId';       type = 'Guid' }
)

$guidA = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$guidB = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$guidC = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
$guidD = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
$guidE = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
$guidF = 'ffffffff-ffff-ffff-ffff-ffffffffffff'

$seed = @(
    [ordered]@{ EmployeeId='E001'; Department='Finance';          JobTitle='Manager';        EmployeeNumber=100;   LongEmployeeNumber=5000000000;  HireDate='2020-01-15T00:00:00Z'; IsActive=$true;  DepartmentId=$guidA }
    [ordered]@{ EmployeeId='E002'; Department='finance';          JobTitle='Senior Manager'; EmployeeNumber=200;   LongEmployeeNumber=6000000000;  HireDate='2021-06-30T00:00:00Z'; IsActive=$false; DepartmentId=$guidB }
    [ordered]@{ EmployeeId='E003'; Department='FinancePartners';  JobTitle='Director';       EmployeeNumber=1000;  LongEmployeeNumber=-1;          HireDate='2022-03-01T00:00:00Z'; IsActive=$true;  DepartmentId=$guidC }
    [ordered]@{ EmployeeId='E004'; Department='CorporateFinance'; JobTitle='VP';             EmployeeNumber=5000;  LongEmployeeNumber=1;           HireDate='2023-08-15T00:00:00Z'; IsActive=$true;  DepartmentId=$guidA }
    [ordered]@{ EmployeeId='E005'; Department='Sales';            JobTitle='Executive';      EmployeeNumber=50;    LongEmployeeNumber=0;           HireDate='1970-01-01T00:00:00Z'; IsActive=$false; DepartmentId=$guidD }
    [ordered]@{ EmployeeId='E006'; Department='Sales';            JobTitle='Rep';            EmployeeNumber=75;    LongEmployeeNumber=100;         HireDate='2024-01-01T00:00:00Z'; IsActive=$true;  DepartmentId=$guidD }
    [ordered]@{ EmployeeId='E007'; Department='IT';               JobTitle='Engineer';       EmployeeNumber=-10;   LongEmployeeNumber=99999999999; HireDate='2025-11-15T00:00:00Z'; IsActive=$true;  DepartmentId=$guidA }
    [ordered]@{ EmployeeId='E008'; Department='IT';               JobTitle='Senior Engineer'; EmployeeNumber=10000; LongEmployeeNumber=4000000000;  HireDate='2026-01-01T00:00:00Z'; IsActive=$false; DepartmentId=$guidB }
    [ordered]@{ EmployeeId='E009'; Department='HR';               JobTitle='Officer';        EmployeeNumber=250;   LongEmployeeNumber=2500000000;  HireDate='2019-05-20T00:00:00Z'; IsActive=$true;  DepartmentId=$guidE }
    [ordered]@{ EmployeeId='E010'; Department='HR';               JobTitle='Lead';           EmployeeNumber=0;     LongEmployeeNumber=3000000000;  HireDate='2018-12-01T00:00:00Z'; IsActive=$false; DepartmentId=$guidF }
    [ordered]@{ EmployeeId='E011'; Department='HR';               JobTitle='Manager';        EmployeeNumber=-1;    LongEmployeeNumber=1500000000;  HireDate='2026-03-15T00:00:00Z'; IsActive=$true;  DepartmentId=$guidA }
    [ordered]@{ EmployeeId='E012'; Department='Engineering';      JobTitle='Junior';         EmployeeNumber=1;     LongEmployeeNumber=100000;      HireDate='2025-06-01T00:00:00Z'; IsActive=$false; DepartmentId=$guidB }
    [ordered]@{ EmployeeId='E013'; Department='Engineering';      JobTitle='Senior';         EmployeeNumber=30;    LongEmployeeNumber=7500000000;  HireDate='2017-09-09T00:00:00Z'; IsActive=$true;  DepartmentId=$guidC }
    [ordered]@{ EmployeeId='E014'; Department='Marketing';        JobTitle='Specialist';     EmployeeNumber=500;   LongEmployeeNumber=800000;      HireDate='2024-12-31T00:00:00Z'; IsActive=$true;  DepartmentId=$guidD }
    [ordered]@{ EmployeeId='E015'; Department=$null;              JobTitle=$null;            EmployeeNumber=$null; LongEmployeeNumber=$null;       HireDate=$null;                  IsActive=$null;   DepartmentId=$null }
)

# ─── Helper: compute the expected match-set for a criterion against the seed ────
# Mirrors the JIM ScopingEvaluationServer logic exactly. Returns an array of
# EmployeeIds the criterion would project. Null-handling matches the worker:
# a missing attribute value never satisfies a non-null comparison.

function Get-ExpectedMatches {
    param(
        [string]$Attribute,
        [string]$Operator,
        [string]$ValueType,
        $Value,
        [bool]$CaseSensitive = $true
    )

    $hits = @()

    foreach ($row in $seed) {
        $actual = $row[$Attribute]

        # If the attribute value is null on this record, only an Equals-null criterion
        # would match (and we never emit those from this matrix). Skip outright.
        if ($null -eq $actual) { continue }

        $hit = switch ($ValueType) {
            'Text' {
                $strActual = [string]$actual
                $strValue = [string]$Value
                $comparison = if ($CaseSensitive) { [System.StringComparison]::Ordinal } else { [System.StringComparison]::OrdinalIgnoreCase }
                switch ($Operator) {
                    'Equals'         { [string]::Equals($strActual, $strValue, $comparison) }
                    'NotEquals'      { -not [string]::Equals($strActual, $strValue, $comparison) }
                    'StartsWith'     { $strActual.StartsWith($strValue, $comparison) }
                    'NotStartsWith'  { -not $strActual.StartsWith($strValue, $comparison) }
                    'EndsWith'       { $strActual.EndsWith($strValue, $comparison) }
                    'NotEndsWith'    { -not $strActual.EndsWith($strValue, $comparison) }
                    'Contains'       { $strActual.IndexOf($strValue, $comparison) -ge 0 }
                    'NotContains'    { $strActual.IndexOf($strValue, $comparison) -lt 0 }
                    default          { $false }
                }
            }
            'Number' {
                $intActual = [int]$actual
                $intValue = [int]$Value
                switch ($Operator) {
                    'Equals'                { $intActual -eq $intValue }
                    'NotEquals'             { $intActual -ne $intValue }
                    'LessThan'              { $intActual -lt $intValue }
                    'LessThanOrEquals'      { $intActual -le $intValue }
                    'GreaterThan'           { $intActual -gt $intValue }
                    'GreaterThanOrEquals'   { $intActual -ge $intValue }
                    default                 { $false }
                }
            }
            'LongNumber' {
                $longActual = [long]$actual
                $longValue = [long]$Value
                switch ($Operator) {
                    'Equals'                { $longActual -eq $longValue }
                    'NotEquals'             { $longActual -ne $longValue }
                    'LessThan'              { $longActual -lt $longValue }
                    'LessThanOrEquals'      { $longActual -le $longValue }
                    'GreaterThan'           { $longActual -gt $longValue }
                    'GreaterThanOrEquals'   { $longActual -ge $longValue }
                    default                 { $false }
                }
            }
            'DateTime' {
                $dtActual = [datetime]::Parse($actual).ToUniversalTime()
                $dtValue = [datetime]::Parse($Value).ToUniversalTime()
                switch ($Operator) {
                    'Equals'                { $dtActual -eq $dtValue }
                    'NotEquals'             { $dtActual -ne $dtValue }
                    'LessThan'              { $dtActual -lt $dtValue }
                    'LessThanOrEquals'      { $dtActual -le $dtValue }
                    'GreaterThan'           { $dtActual -gt $dtValue }
                    'GreaterThanOrEquals'   { $dtActual -ge $dtValue }
                    default                 { $false }
                }
            }
            'Boolean' {
                $boolActual = [bool]$actual
                $boolValue = [bool]$Value
                switch ($Operator) {
                    'Equals'    { $boolActual -eq $boolValue }
                    'NotEquals' { $boolActual -ne $boolValue }
                    default     { $false }
                }
            }
            'Guid' {
                $guidActual = [Guid]$actual
                $guidValue = [Guid]$Value
                switch ($Operator) {
                    'Equals'    { $guidActual -eq $guidValue }
                    'NotEquals' { $guidActual -ne $guidValue }
                    default     { $false }
                }
            }
            default { $false }
        }

        if ($hit) { $hits += $row.EmployeeId }
    }

    return ,$hits
}

# ─── Helper: intersect / union expected sets for multi-criteria groups ──────────

function Get-AndIntersection {
    param([string[]]$A, [string[]]$B)
    return @($A | Where-Object { $B -contains $_ })
}

function Get-OrUnion {
    param([string[]]$A, [string[]]$B)
    $combined = @($A) + @($B)
    return @($combined | Select-Object -Unique)
}

# ─── Axes ───────────────────────────────────────────────────────────────────────

$textOperators = @('Equals', 'NotEquals', 'StartsWith', 'NotStartsWith', 'EndsWith', 'NotEndsWith', 'Contains', 'NotContains')
$comparisonOperators = @('Equals', 'NotEquals', 'LessThan', 'LessThanOrEquals', 'GreaterThan', 'GreaterThanOrEquals')
$equalityOperators = @('Equals', 'NotEquals')

# Per-type axis: which operators are applicable, and a canonical comparison value
# chosen so the expected set is non-trivial against the seed.
$typeAxes = @(
    @{ valueType = 'Text';       attribute = 'Department';         operators = $textOperators;       canonicalValue = 'Finance' }
    @{ valueType = 'Number';     attribute = 'EmployeeNumber';     operators = $comparisonOperators; canonicalValue = 100 }
    @{ valueType = 'LongNumber'; attribute = 'LongEmployeeNumber'; operators = $comparisonOperators; canonicalValue = 5000000000 }
    @{ valueType = 'DateTime';   attribute = 'HireDate';           operators = $comparisonOperators; canonicalValue = '2022-01-01T00:00:00Z' }
    @{ valueType = 'Boolean';    attribute = 'IsActive';           operators = $equalityOperators;   canonicalValue = $true }
    @{ valueType = 'Guid';       attribute = 'DepartmentId';       operators = $equalityOperators;   canonicalValue = $guidA }
)

# Secondary criterion used in AllPair / AnyPair / Nested cells. Chosen so the
# combined expected set is non-trivial (not always empty, not always universal).
$secondary = @{
    attribute = 'IsActive'
    operator = 'Equals'
    valueType = 'Boolean'
    value = $true
    caseSensitive = $true
}

# Tertiary criterion used in nested `(A OR B) AND C` cells.
$tertiary = @{
    attribute = 'Department'
    operator = 'Equals'
    valueType = 'Text'
    value = 'HR'
    caseSensitive = $true
}

# ─── Cell generation ────────────────────────────────────────────────────────────

$cells = New-Object System.Collections.Generic.List[object]

# Build the Quick-tier coverage map: one cell per operator, preferring Text first.
# Text is iterated first in $typeAxes, so it claims the 8 text operators; Number
# then claims the 4 comparison-only operators (LessThan / LessThanOrEquals / GreaterThan
# / GreaterThanOrEquals); Boolean and Guid don't contribute any new operators.
$quickOperatorAxis = @{}
foreach ($axis in $typeAxes) {
    foreach ($op in $axis.operators) {
        if (-not $quickOperatorAxis.ContainsKey($op)) {
            $quickOperatorAxis[$op] = $axis.valueType
        }
    }
}

function Get-CellName {
    param([string]$ValueType, [string]$Operator, [string]$Group, [Nullable[bool]]$CaseSensitive = $null)
    $parts = @($ValueType, $Operator, $Group)
    if ($null -ne $CaseSensitive) {
        $parts += if ($CaseSensitive) { 'CS' } else { 'CI' }
    }
    return [string]::Join('.', $parts)
}

# Base pair cells: (operator × type), single-criterion, default group.
# Text also gets a second variant with CaseSensitive=false so case-folding is covered.

foreach ($axis in $typeAxes) {
    foreach ($op in $axis.operators) {
        # Cases to emit for this (operator, type) pair: CS=true always; CS=false only for Text.
        $caseVariants = if ($axis.valueType -eq 'Text') { @($true, $false) } else { @($true) }

        foreach ($cs in $caseVariants) {
            $primary = @{
                attribute = $axis.attribute
                operator = $op
                valueType = $axis.valueType
                value = $axis.canonicalValue
                caseSensitive = $cs
            }
            $expected = Get-ExpectedMatches `
                -Attribute $axis.attribute `
                -Operator $op `
                -ValueType $axis.valueType `
                -Value $axis.canonicalValue `
                -CaseSensitive $cs

            $name = Get-CellName -ValueType $axis.valueType -Operator $op -Group 'Single' -CaseSensitive ($(if ($axis.valueType -eq 'Text') { $cs } else { $null }))

            # Quick tier: one cell per operator. The Quick coverage map (above) picks
            # the preferred valueType for each operator (Text first, then Number for
            # operators Text doesn't support). Only CS=true variants qualify.
            $tiers = @('Default', 'Exhaustive')
            if ($cs -eq $true -and $quickOperatorAxis[$op] -eq $axis.valueType) {
                $tiers = @('Quick', 'Default', 'Exhaustive')
            }

            $cells.Add([ordered]@{
                name = $name
                tiers = $tiers
                group = 'Single'
                primary = $primary
                expected = @($expected)
            })
        }
    }
}

# Group-structure cells (Exhaustive only): for each (operator × type) base pair,
# emit AllPair, AnyPair, and Nested variants. The Default tier picks one representative
# example of each group structure (the canonical Text.Equals base).

foreach ($axis in $typeAxes) {
    foreach ($op in $axis.operators) {
        # For Text we expand both case variants in Exhaustive; for everything else, single.
        $caseVariants = if ($axis.valueType -eq 'Text') { @($true, $false) } else { @($true) }

        foreach ($cs in $caseVariants) {
            $primary = @{
                attribute = $axis.attribute
                operator = $op
                valueType = $axis.valueType
                value = $axis.canonicalValue
                caseSensitive = $cs
            }
            $primaryMatches = Get-ExpectedMatches `
                -Attribute $axis.attribute -Operator $op -ValueType $axis.valueType `
                -Value $axis.canonicalValue -CaseSensitive $cs

            $secondaryMatches = Get-ExpectedMatches `
                -Attribute $secondary.attribute -Operator $secondary.operator -ValueType $secondary.valueType `
                -Value $secondary.value -CaseSensitive $secondary.caseSensitive

            $tertiaryMatches = Get-ExpectedMatches `
                -Attribute $tertiary.attribute -Operator $tertiary.operator -ValueType $tertiary.valueType `
                -Value $tertiary.value -CaseSensitive $tertiary.caseSensitive

            # AllPair: primary AND secondary
            $allExpected = Get-AndIntersection -A $primaryMatches -B $secondaryMatches
            $cells.Add([ordered]@{
                name = Get-CellName -ValueType $axis.valueType -Operator $op -Group 'AllPair' -CaseSensitive ($(if ($axis.valueType -eq 'Text') { $cs } else { $null }))
                tiers = @('Exhaustive')
                group = 'AllPair'
                primary = $primary
                secondary = $secondary
                expected = @($allExpected)
            })

            # AnyPair: primary OR secondary
            $anyExpected = Get-OrUnion -A $primaryMatches -B $secondaryMatches
            $cells.Add([ordered]@{
                name = Get-CellName -ValueType $axis.valueType -Operator $op -Group 'AnyPair' -CaseSensitive ($(if ($axis.valueType -eq 'Text') { $cs } else { $null }))
                tiers = @('Exhaustive')
                group = 'AnyPair'
                primary = $primary
                secondary = $secondary
                expected = @($anyExpected)
            })

            # Nested: (primary OR tertiary) AND secondary
            $orPart = Get-OrUnion -A $primaryMatches -B $tertiaryMatches
            $nestedExpected = Get-AndIntersection -A $orPart -B $secondaryMatches
            $cells.Add([ordered]@{
                name = Get-CellName -ValueType $axis.valueType -Operator $op -Group 'Nested' -CaseSensitive ($(if ($axis.valueType -eq 'Text') { $cs } else { $null }))
                tiers = @('Exhaustive')
                group = 'Nested'
                primary = $primary
                secondary = $secondary
                tertiary = $tertiary
                expected = @($nestedExpected)
            })
        }
    }
}

# Default-tier representative group cells: one of each structure on Text.Equals.
# These are added on top of the Exhaustive cells so Default has at least one of each.
$defaultGroupRep = @{
    valueType = 'Text'
    operator = 'Equals'
    caseSensitive = $true
}

foreach ($groupName in @('AllPair', 'AnyPair', 'Nested')) {
    $existing = $cells | Where-Object {
        $_.group -eq $groupName -and
        $_.primary.valueType -eq $defaultGroupRep.valueType -and
        $_.primary.operator -eq $defaultGroupRep.operator -and
        $_.primary.caseSensitive -eq $defaultGroupRep.caseSensitive
    } | Select-Object -First 1

    if ($existing) {
        # Append Default to its tiers list so the same cell counts for Default and Exhaustive.
        if ($existing.tiers -notcontains 'Default') {
            $existing.tiers = @('Default') + $existing.tiers
        }
    }
}

# ─── Emit manifest ──────────────────────────────────────────────────────────────

$manifest = [ordered]@{
    schemaVersion = 1
    description = 'Scenario 11 - Scoping Criteria Evaluation Matrix. Generated by Build-ScopingCriteriaMatrix.ps1; do not edit by hand. To regenerate run pwsh test/integration/scenarios/data/Build-ScopingCriteriaMatrix.ps1.'
    seedAttributes = $seedAttributes
    seed = $seed
    secondaryCriterion = $secondary
    tertiaryCriterion = $tertiary
    cells = $cells
}

$manifestPath = Join-Path $OutputPath 'scoping-criteria-matrix.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 10
# Normalise the JSON line endings to LF so the file is byte-stable across platforms.
$manifestJson = $manifestJson -replace "`r`n", "`n"
Set-Content -Path $manifestPath -Value $manifestJson -NoNewline -Encoding utf8

# Emit a small CSV seed for the file connector. The ObjectType column is set per-row
# by the scenario script (one ObjectType value per cell) so this CSV is just the base
# data; the scenario fans it out at runtime.
$seedCsvPath = Join-Path $OutputPath 'scoping-criteria-seed.csv'
$seedRows = $seed | ForEach-Object {
    $row = [ordered]@{}
    foreach ($attr in $seedAttributes) {
        $value = $_[$attr.name]
        # Empty string for nulls so the CSV column is present but the file connector
        # creates no AttributeValue row, which is what we want to exercise null-handling.
        $row[$attr.name] = if ($null -eq $value) { '' } else { [string]$value }
    }
    [PSCustomObject]$row
}
$seedRows | Export-Csv -Path $seedCsvPath -NoTypeInformation -Encoding utf8

Write-Host "Generated manifest: $manifestPath"
Write-Host "  Cells: $($cells.Count)"
Write-Host "    Quick:      $(@($cells | Where-Object { $_.tiers -contains 'Quick' }).Count)"
Write-Host "    Default:    $(@($cells | Where-Object { $_.tiers -contains 'Default' }).Count)"
Write-Host "    Exhaustive: $(@($cells | Where-Object { $_.tiers -contains 'Exhaustive' }).Count)"
Write-Host "Generated seed CSV: $seedCsvPath"
Write-Host "  Rows: $($seed.Count)"
