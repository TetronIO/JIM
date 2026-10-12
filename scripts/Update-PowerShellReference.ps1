# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Version 7.0

<#
.SYNOPSIS
    Generates each cmdlet's Syntax, Parameters and Output on the docs/powershell/ pages from the module.

.DESCRIPTION
    The cmdlet sections under docs/powershell/ used to restate the module by hand, and drifted: a page
    named a property the object does not have, so an example filtering on it matched every certificate
    and deleted them all (#1083). This script writes those three parts from the module itself, between
    the markers below, and leaves everything else on a page (introductions, examples, notes) alone:

        <!-- BEGIN GENERATED: Get-JIMCertificate -->
        ...
        <!-- END GENERATED: Get-JIMCertificate -->

    - Syntax: one line per parameter set, from Get-Command.
    - Parameters: name, type, where it is required, default and description. The description is the
      parameter's .PARAMETER help, followed by what its attributes say (pipeline input, ValidateSet,
      ValidateRange, SupportsWildcards).
    - Output: the cmdlet's .OUTPUTS help, then each type it declares with
      [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')], the sets that return it, and the
      properties of that schema in the OpenAPI document, in the PascalCase the module returns them in.
      A JIM type that is not an OpenAPI schema (a cmdlet that builds its own object) must be listed in
      the config's NonApiOutputTypes with its reason, and its properties are left to the .OUTPUTS help.

    -Check writes nothing, and fails (rule ACC-5 of engineering/DOCUMENTATION_STANDARD.md) when a block
    differs from what this script would write, names a cmdlet the module does not export, or belongs to
    a cmdlet with an undescribed parameter or undescribed output. Cmdlet sections with no block yet are
    listed as a warning while the pages migrate.

.PARAMETER ModulePath
    The module manifest to document. Defaults to src/JIM.PowerShell/JIM.psd1.

.PARAMETER DocsPath
    The folder of pages to update. Defaults to docs/powershell.

.PARAMETER OpenApiPath
    The OpenAPI document whose schemas describe the cmdlets' output. Without it, one is generated with
    Generate-OpenApiDoc.ps1, which builds the solution.

.PARAMETER ConfigPath
    The configuration (NonApiOutputTypes). Defaults to docs/.conformance/powershell-reference.psd1.

.PARAMETER Check
    Write nothing; fail if any page differs from what would be written.

.EXAMPLE
    pwsh ./scripts/Update-PowerShellReference.ps1 -OpenApiPath /tmp/openapi-v1.json

    Regenerates every marked block from the module and an OpenAPI document generated earlier.

.EXAMPLE
    pwsh ./scripts/Update-PowerShellReference.ps1 -OpenApiPath $openApi -Check

    Fails if any page and the module disagree, as build-and-test does.
#>
[CmdletBinding()]
param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..' 'src' 'JIM.PowerShell' 'JIM.psd1'),
    [string]$DocsPath = (Join-Path $PSScriptRoot '..' 'docs' 'powershell'),
    [string]$OpenApiPath,
    [string]$ConfigPath = (Join-Path $PSScriptRoot '..' 'docs' '.conformance' 'powershell-reference.psd1'),
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Culture = [System.Globalization.CultureInfo]::GetCultureInfo('en-GB')
$script:AllSets = '__AllParameterSets'
$script:BeginPattern = '<!-- BEGIN GENERATED: (?<name>[A-Za-z0-9-]+) -->'
$script:EndPattern = '<!-- END GENERATED: (?<name>[A-Za-z0-9-]+) -->'

# The site writes types the way PowerShell users write them, not as .NET names.
$script:TypeNames = @{
    'String'           = 'string'
    'Int32'            = 'int'
    'Int64'            = 'long'
    'Guid'             = 'guid'
    'Boolean'          = 'bool'
    'DateTime'         = 'datetime'
    'Hashtable'        = 'hashtable'
    'Object'           = 'object'
    'ScriptBlock'      = 'scriptblock'
    'PSObject'         = 'PSCustomObject'
    'PSCustomObject'   = 'PSCustomObject'
    'Double'           = 'double'
    'Decimal'          = 'decimal'
    'Byte'             = 'byte'
    'SwitchParameter'  = 'switch'
    'Uri'              = 'uri'
    'TimeSpan'         = 'timespan'
    'Void'             = 'void'
}

function Get-FriendlyTypeName {
    param([Parameter(Mandatory)][type]$Type)

    if ($Type.IsArray) { return (Get-FriendlyTypeName -Type $Type.GetElementType()) + '[]' }
    $underlying = [Nullable]::GetUnderlyingType($Type)
    if ($underlying) { return Get-FriendlyTypeName -Type $underlying }
    if ($script:TypeNames.ContainsKey($Type.Name)) { return $script:TypeNames[$Type.Name] }
    $Type.Name
}

# A .OUTPUTS or .PARAMETER help text as Markdown: lines within a paragraph joined, paragraphs kept.
function ConvertTo-Paragraphs {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    $normalised = $Text -replace "`r", ''
    @(($normalised.Trim() -split "\n\s*\n") | ForEach-Object { ($_ -split '\s*\n\s*' | ForEach-Object Trim) -join ' ' } |
        Where-Object { $_ })
}

function ConvertTo-TableCell {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    $Text -replace '\|', '\|'
}

function Format-Number {
    param($Value)
    ([decimal]$Value).ToString('N0', $script:Culture)
}

function Join-Choices {
    param([string[]]$Items)
    $quoted = @($Items | ForEach-Object { "``$_``" })
    if ($quoted.Count -eq 1) { return $quoted[0] }
    ($quoted[0..($quoted.Count - 2)] -join ', ') + ' or ' + $quoted[-1]
}

# What a parameter's attributes say, as sentences to follow its description.
function Get-AttributeSentences {
    param($Parameter, [object[]]$SetMetadata)

    $sentences = [System.Collections.Generic.List[string]]::new()

    $byValue = @($SetMetadata | Where-Object ValueFromPipeline).Count -gt 0
    $byProperty = @($SetMetadata | Where-Object ValueFromPipelineByPropertyName).Count -gt 0
    if ($byValue -and $byProperty) { $sentences.Add('Accepts pipeline input, by value or by property name.') }
    elseif ($byValue) { $sentences.Add('Accepts pipeline input.') }
    elseif ($byProperty) { $sentences.Add('Accepts pipeline input by property name.') }

    foreach ($attribute in $Parameter.Attributes) {
        if ($attribute -is [System.Management.Automation.ValidateSetAttribute]) {
            $sentences.Add("One of $(Join-Choices $attribute.ValidValues).")
        }
        elseif ($attribute -is [System.Management.Automation.ValidateRangeAttribute]) {
            if ($null -ne $attribute.MinRange) {
                $max = $attribute.MaxRange
                $unbounded = ($max -is [int] -and $max -eq [int]::MaxValue) -or ($max -is [long] -and $max -eq [long]::MaxValue)
                if ($unbounded) { $sentences.Add("At least $(Format-Number $attribute.MinRange).") }
                else { $sentences.Add("From $(Format-Number $attribute.MinRange) to $(Format-Number $max).") }
            }
        }
        elseif ($attribute -is [System.Management.Automation.SupportsWildcardsAttribute]) {
            $sentences.Add('Supports wildcards.')
        }
    }
    $sentences
}

# Everything the block needs to know about one cmdlet, read from the loaded module.
function Get-CmdletReference {
    param([Parameter(Mandatory)][System.Management.Automation.CommandInfo]$Command)

    $ast = $Command.ScriptBlock.Ast
    $help = $ast.GetHelpContent()
    $paramBlock = $ast.Body.ParamBlock

    # Sets in the order the reader meets them: the default first, then as declared.
    $sets = @($Command.ParameterSets | Sort-Object { -not $_.IsDefault })
    $named = @($sets | Where-Object Name -ne $script:AllSets)

    $parameters = foreach ($parameterAst in @(if ($paramBlock) { $paramBlock.Parameters })) {
        $name = $parameterAst.Name.VariablePath.UserPath
        $metadata = $Command.Parameters[$name]
        $inSets = @($sets | Where-Object { @($_.Parameters | ForEach-Object Name) -contains $name })
        $setMetadata = @($inSets | ForEach-Object { $_.Parameters | Where-Object Name -eq $name })
        $description = if ($help -and $help.Parameters.ContainsKey($name.ToUpperInvariant())) { $help.Parameters[$name.ToUpperInvariant()] }
        $default = if ($parameterAst.DefaultValue -and $metadata.ParameterType -ne [switch]) { $parameterAst.DefaultValue.Extent.Text -replace '\s+', ' ' }

        [pscustomobject]@{
            Name        = $name
            Type        = $metadata.ParameterType
            IsSwitch    = $metadata.ParameterType -eq [switch]
            Sets        = $inSets
            SetMetadata = $setMetadata
            Metadata    = $metadata
            Description = (ConvertTo-Paragraphs $description) -join ' '
            Default     = $default
        }
    }

    $outputTypes = [ordered]@{}
    foreach ($attribute in @($Command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.OutputTypeAttribute] })) {
        foreach ($type in $attribute.Type) {
            if (-not $outputTypes.Contains($type.Name)) { $outputTypes[$type.Name] = [System.Collections.Generic.List[string]]::new() }
            foreach ($set in @($attribute.ParameterSetName)) {
                if (-not $outputTypes[$type.Name].Contains($set)) { $outputTypes[$type.Name].Add($set) }
            }
        }
    }

    [pscustomobject]@{
        Name        = $Command.Name
        Sets        = $sets
        NamedSets   = $named
        Parameters  = @($parameters)
        Outputs     = @(if ($help) { $help.Outputs | ForEach-Object { ConvertTo-Paragraphs $_ } })
        OutputTypes = $outputTypes
    }
}

function Format-Syntax {
    param($Reference)

    $lines = [System.Collections.Generic.List[string]]::new()
    $labelled = $Reference.Sets.Count -gt 1
    foreach ($set in $Reference.Sets) {
        if ($labelled) {
            if ($lines.Count -gt 0) { $lines.Add('') }
            $lines.Add('# ' + $set.Name + $(if ($set.IsDefault) { ' (default)' }))
        }
        $inSet = @($Reference.Parameters | Where-Object { @($_.Sets | ForEach-Object Name) -contains $set.Name })
        $mandatory = @($inSet | Where-Object { ($set.Parameters | Where-Object Name -eq $_.Name).IsMandatory })
        $mandatoryNames = @($mandatory | ForEach-Object Name)
        $optional = @($inSet | Where-Object { $mandatoryNames -notcontains $_.Name })
        $tokens = @($Reference.Name)
        foreach ($parameter in $mandatory) {
            $tokens += if ($parameter.IsSwitch) { "-$($parameter.Name)" } else { "-$($parameter.Name) <$(Get-FriendlyTypeName $parameter.Type)>" }
        }
        foreach ($parameter in $optional) {
            $tokens += if ($parameter.IsSwitch) { "[-$($parameter.Name)]" } else { "[-$($parameter.Name) <$(Get-FriendlyTypeName $parameter.Type)>]" }
        }
        $lines.Add($tokens -join ' ')
    }
    "### Syntax`n`n" + '```powershell' + "`n" + ($lines -join "`n") + "`n" + '```'
}

# Yes or No, and the sets, where a parameter is not in every set or is required in only some.
function Format-Required {
    param($Reference, $Parameter)

    $mandatorySets = @($Parameter.Sets | Where-Object { ($_.Parameters | Where-Object Name -eq $Parameter.Name).IsMandatory })
    $mandatoryNames = @($mandatorySets | ForEach-Object Name)
    $optionalSets = @($Parameter.Sets | Where-Object { $mandatoryNames -notcontains $_.Name })
    $inEverySet = $Parameter.Sets.Count -eq $Reference.Sets.Count

    if ($Reference.NamedSets.Count -lt 2 -or ($inEverySet -and ($mandatorySets.Count -eq 0 -or $optionalSets.Count -eq 0))) {
        return $(if ($mandatorySets.Count -gt 0) { 'Yes' } else { 'No' })
    }
    $parts = @()
    if ($mandatorySets.Count -gt 0) { $parts += 'Yes (' + ($mandatoryNames -join ', ') + ')' }
    if ($optionalSets.Count -gt 0) { $parts += 'No (' + (@($optionalSets | ForEach-Object Name) -join ', ') + ')' }
    $parts -join '; '
}

function Format-Parameters {
    param($Reference)

    if ($Reference.Parameters.Count -eq 0) { return "### Parameters`n`nNone." }

    $rows = foreach ($parameter in $Reference.Parameters) {
        $sentences = @(Get-AttributeSentences -Parameter $parameter.Metadata -SetMetadata $parameter.SetMetadata)
        $description = $parameter.Description
        if ($sentences.Count -gt 0) {
            if ($description -and $description -notmatch '[.!?]$') { $description += '.' }
            $description = (@($description) + $sentences | Where-Object { $_ }) -join ' '
        }
        $default = if ($parameter.Default) { "``$($parameter.Default)``" } else { '' }
        '| `{0}` | `{1}` | {2} | {3} | {4} |' -f $parameter.Name, (Get-FriendlyTypeName $parameter.Type),
            (Format-Required $Reference $parameter), (ConvertTo-TableCell $default), (ConvertTo-TableCell $description)
    }
    "### Parameters`n`n| Name | Type | Required | Default | Description |`n|------|------|----------|---------|-------------|`n" + ($rows -join "`n")
}

# A schema's property names in the order declared, those it takes through allOf first.
function Get-SchemaProperties {
    param($Schemas, [string]$Name, [System.Collections.Generic.HashSet[string]]$Seen)

    if (-not $Seen) { $Seen = [System.Collections.Generic.HashSet[string]]::new() }
    if (-not $Seen.Add($Name)) { return @() }
    $schema = $Schemas[$Name]
    $names = [System.Collections.Generic.List[string]]::new()
    if ($schema.Contains('allOf')) {
        foreach ($part in $schema['allOf']) {
            if ($part.Contains('$ref')) {
                foreach ($inherited in (Get-SchemaProperties -Schemas $Schemas -Name ($part['$ref'] -replace '^#/components/schemas/', '') -Seen $Seen)) { $names.Add($inherited) }
            }
            elseif ($part.Contains('properties')) {
                foreach ($key in $part['properties'].Keys) { $names.Add($key) }
            }
        }
    }
    if ($schema.Contains('properties')) {
        foreach ($key in $schema['properties'].Keys) { $names.Add($key) }
    }
    @($names | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) } | Select-Object -Unique)
}

function Format-Output {
    param($Reference, $Schemas, [hashtable]$NonApiOutputTypes, [System.Collections.Generic.List[string]]$Problems)

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($typeName in $Reference.OutputTypes.Keys) {
        $sets = $Reference.OutputTypes[$typeName]
        if ($typeName -in @('System.Void', 'void')) { continue }
        if ($typeName -in @('System.Management.Automation.PSObject', 'System.Management.Automation.PSCustomObject', 'PSCustomObject', 'psobject')) {
            $Problems.Add("$($Reference.Name) declares its output only as PSCustomObject; name what it returns with [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')]")
            continue
        }

        $display = $typeName
        $properties = @()
        if ($typeName -like 'JIM.*') {
            $schemaName = $typeName.Substring(4)
            $display = $schemaName
            if ($Schemas.Contains($schemaName)) { $properties = Get-SchemaProperties -Schemas $Schemas -Name $schemaName }
            elseif (-not $NonApiOutputTypes.ContainsKey($typeName)) {
                $Problems.Add("$($Reference.Name) declares output type $typeName, which is not a schema in the OpenAPI document; correct the name, or list it in NonApiOutputTypes with the reason")
            }
        }
        else {
            $resolved = $typeName -as [type]
            if ($resolved) { $display = Get-FriendlyTypeName $resolved }
        }

        $rows.Add([pscustomobject]@{
            Type       = $display
            Sets       = $sets
            Properties = @($properties)
        })
    }

    $text = "### Output"
    if ($Reference.Outputs.Count -gt 0) { $text += "`n`n" + ($Reference.Outputs -join "`n`n") }
    if ($rows.Count -eq 0) {
        if ($Reference.Outputs.Count -eq 0) { $text += "`n`nNone." }
        return $text
    }

    # The Returned by column earns its place only when some type is returned by some sets and not others.
    $everySet = @($Reference.Sets | ForEach-Object Name)
    $showSets = @($rows | Where-Object {
            $row = $_
            $row.Sets -notcontains $script:AllSets -and @($everySet | Where-Object { $row.Sets -notcontains $_ }).Count -gt 0
        }).Count -gt 0

    $propertyCell = { param($row) ConvertTo-TableCell ((@($row.Properties) | ForEach-Object { "``$_``" }) -join ', ') }
    if ($showSets) {
        $lines = foreach ($row in $rows) {
            $returnedBy = if ($row.Sets -contains $script:AllSets) { 'All' } else { @($everySet | Where-Object { $row.Sets -contains $_ }) -join ', ' }
            '| `{0}` | {1} | {2} |' -f $row.Type, $returnedBy, (& $propertyCell $row)
        }
        $text += "`n`n| Type | Returned by | Properties |`n|------|-------------|------------|`n" + ($lines -join "`n")
    }
    else {
        $lines = foreach ($row in $rows) { '| `{0}` | {1} |' -f $row.Type, (& $propertyCell $row) }
        $text += "`n`n| Type | Properties |`n|------|------------|`n" + ($lines -join "`n")
    }
    $text
}

function Format-Block {
    param($Reference, $Schemas, [hashtable]$NonApiOutputTypes, [System.Collections.Generic.List[string]]$Problems)

    foreach ($parameter in @($Reference.Parameters | Where-Object { -not $_.Description })) {
        $Problems.Add("$($Reference.Name) has no .PARAMETER help for -$($parameter.Name)")
    }
    (Format-Syntax $Reference) + "`n`n" + (Format-Parameters $Reference) + "`n`n" +
        (Format-Output -Reference $Reference -Schemas $Schemas -NonApiOutputTypes $NonApiOutputTypes -Problems $Problems)
}

function Get-LineNumber {
    param([string]$Text, [int]$Index)
    ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count + 1
}

function Write-Finding {
    param([string]$Path, [int]$Line, [string]$Message, [switch]$AsWarning)

    $location = if ($Path) { "${Path}:${Line}: " } else { '' }
    if ($env:GITHUB_ACTIONS -eq 'true' -and -not $AsWarning) {
        $escaped = $Message -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
        $file = if ($Path) { "file=$Path,line=$Line," } else { '' }
        Write-Host "::error ${file}title=ACC-5::$escaped"
    }
    else {
        Write-Host "ACC-5: $location$Message"
    }
}

# --- Inputs ---------------------------------------------------------------------------------------------

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = if (Test-Path -LiteralPath $ConfigPath) { Import-PowerShellDataFile -LiteralPath $ConfigPath } else { @{} }
$nonApi = if ($config.ContainsKey('NonApiOutputTypes') -and $config.NonApiOutputTypes) { $config.NonApiOutputTypes } else { @{} }

if (-not $OpenApiPath) {
    $OpenApiPath = Join-Path ([System.IO.Path]::GetTempPath()) "jim-openapi-$([guid]::NewGuid().ToString('N')).json"
    Write-Host "No -OpenApiPath given; generating the OpenAPI document (this builds the solution)..."
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Generate-OpenApiDoc.ps1') -OutputPath $OpenApiPath
    if ($LASTEXITCODE -ne 0) { throw 'Generate-OpenApiDoc.ps1 failed; pass -OpenApiPath to use a document generated earlier.' }
}
$openApi = Get-Content -LiteralPath $OpenApiPath -Raw | ConvertFrom-Json -AsHashtable
$schemas = if ($openApi.Contains('components') -and $openApi['components'].Contains('schemas')) { $openApi['components']['schemas'] } else { @{} }

$module = Import-Module -Name (Resolve-Path -LiteralPath $ModulePath).Path -Force -PassThru -DisableNameChecking
$exported = @{}
foreach ($command in $module.ExportedFunctions.Values) { $exported[$command.Name] = $command }

# --- Pages ----------------------------------------------------------------------------------------------

$failures = 0
$blockCount = 0
$changedPages = 0
$seenBlocks = @{}
$notGenerated = [System.Collections.Generic.List[string]]::new()

foreach ($page in @(Get-ChildItem -LiteralPath $DocsPath -Filter '*.md' -File | Sort-Object Name)) {
    $text = Get-Content -LiteralPath $page.FullName -Raw
    if ($null -eq $text) { $text = '' }
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $page.FullName) -replace '\\', '/'
    $updated = $text

    $begins = @([regex]::Matches($text, $script:BeginPattern))
    $ends = @([regex]::Matches($text, $script:EndPattern))
    $generatedHere = @{}

    # Blocks are replaced from the last to the first, so earlier indices stay valid.
    $blocks = foreach ($begin in $begins) {
        $name = $begin.Groups['name'].Value
        $end = $ends | Where-Object { $_.Index -gt $begin.Index -and $_.Groups['name'].Value -eq $name } | Select-Object -First 1
        $nextBegin = $begins | Where-Object { $_.Index -gt $begin.Index } | Select-Object -First 1
        if (-not $end -or ($nextBegin -and $nextBegin.Index -lt $end.Index)) {
            Write-Finding $relative (Get-LineNumber $text $begin.Index) "the BEGIN GENERATED marker for $name has no matching END GENERATED marker before the next block"
            $failures++
            continue
        }
        [pscustomobject]@{ Name = $name; Begin = $begin; End = $end }
    }
    foreach ($end in $ends) {
        if (-not ($blocks | Where-Object { $_.End.Index -eq $end.Index })) {
            Write-Finding $relative (Get-LineNumber $text $end.Index) "the END GENERATED marker for $($end.Groups['name'].Value) has no matching BEGIN GENERATED marker"
            $failures++
        }
    }

    foreach ($block in @($blocks | Sort-Object { $_.Begin.Index } -Descending)) {
        $line = Get-LineNumber $text $block.Begin.Index
        $generatedHere[$block.Name] = $true
        if ($seenBlocks.ContainsKey($block.Name)) {
            Write-Finding $relative $line "$($block.Name) is generated on $($seenBlocks[$block.Name]) as well; give each cmdlet one block"
            $failures++
            continue
        }
        $seenBlocks[$block.Name] = $relative
        if (-not $exported.ContainsKey($block.Name)) {
            Write-Finding $relative $line "a generated block names $($block.Name), which the module does not export; remove the block, or correct its name"
            $failures++
            continue
        }

        $blockCount++
        $problems = [System.Collections.Generic.List[string]]::new()
        $reference = Get-CmdletReference -Command $exported[$block.Name]
        $content = "`n" + (Format-Block -Reference $reference -Schemas $schemas -NonApiOutputTypes $nonApi -Problems $problems) + "`n"
        foreach ($problem in $problems) {
            Write-Finding $relative $line $problem
            $failures++
        }

        $start = $block.Begin.Index + $block.Begin.Length
        $current = $text.Substring($start, $block.End.Index - $start)
        if ($current -ne $content) {
            if ($Check) {
                $expectedLines = $content -split "`n"
                $foundLines = $current -split "`n"
                $index = 0
                while ($index -lt $expectedLines.Count -and $index -lt $foundLines.Count -and $expectedLines[$index] -eq $foundLines[$index]) { $index++ }
                $expected = if ($index -lt $expectedLines.Count) { $expectedLines[$index] } else { '(end of block)' }
                $found = if ($index -lt $foundLines.Count) { $foundLines[$index] } else { '(end of block)' }
                Write-Finding $relative ($line + $index) "the generated block for $($block.Name) differs from the module; run scripts/Update-PowerShellReference.ps1 to regenerate it, and change the cmdlet's help rather than the page. Expected: '$expected'; found: '$found'"
                $failures++
            }
            else {
                $updated = $updated.Substring(0, $start) + $content + $updated.Substring($block.End.Index)
            }
        }
    }

    # Cmdlet sections that have no block yet: the migration still to do (#1083).
    foreach ($heading in [regex]::Matches($text, '(?m)^#{2,3} (?<name>[A-Za-z]+-JIM[A-Za-z0-9]+)\s*$')) {
        $name = $heading.Groups['name'].Value
        if ($exported.ContainsKey($name) -and -not $generatedHere.ContainsKey($name)) {
            $notGenerated.Add("${relative}: $name")
        }
    }

    if (-not $Check -and $updated -ne $text) {
        Set-Content -LiteralPath $page.FullName -Value $updated -NoNewline
        $changedPages++
    }
}

# An allowed non-API output type that no exported cmdlet declares any more.
$declared = @{}
foreach ($command in $exported.Values) {
    foreach ($attribute in @($command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.OutputTypeAttribute] })) {
        foreach ($type in $attribute.Type) { $declared[$type.Name] = $true }
    }
}
foreach ($typeName in $nonApi.Keys) {
    if (-not $declared.ContainsKey($typeName)) {
        Write-Finding '' 0 "NonApiOutputTypes lists $typeName, which no cmdlet declares; remove the entry"
        $failures++
    }
}

if ($notGenerated.Count -gt 0) {
    Write-Host "ACC-5 (migration, #1083): $($notGenerated.Count) cmdlet sections are not generated yet:"
    foreach ($entry in $notGenerated) { Write-Host "  $entry" }
}

if ($Check) {
    if ($failures -gt 0) {
        Write-Host "ACC-5: $failures problem(s) in the generated PowerShell reference."
        exit 1
    }
    Write-Host "ACC-5: all $blockCount generated blocks match the module."
    exit 0
}

Write-Host "Regenerated $blockCount block(s); $changedPages page(s) changed."
if ($failures -gt 0) {
    Write-Host "ACC-5: $failures problem(s) to fix before -Check passes."
    exit 1
}
exit 0
