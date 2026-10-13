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
      parameter's .PARAMETER help, followed by what its attributes say (pipeline input and the property
      names that bind it, other aliases, ValidateSet, ValidateRange, SupportsWildcards).
    - Output: the cmdlet's .OUTPUTS help, then each type it declares with
      [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')] and the properties of that schema in the
      OpenAPI document, in the PascalCase the module returns them in. No class name reaches the page, since
      a reader cannot type one: each type is labelled by when it comes back, from the parameters that set
      its parameter sets apart ("With -Id or -Follow", "By default"), and a property holding another object
      is an object naming its properties, an enumeration a string naming its values. Where two types come
      from one parameter set, the .OUTPUTS help labels each with a line "<type>: <when it is returned>".
      A JIM type that is not an OpenAPI schema (a cmdlet that builds its own object) must be listed in
      the config's NonApiOutputTypes with its reason, and its properties are tabled from the list its
      .OUTPUTS help gives (a paragraph whose first line is the type's name, then "Name (type): description");
      one the module names itself but whose properties are an API schema's is listed in
      OutputTypeSchemas with that schema, whose properties are tabled for it.

    -Check writes nothing, and fails (rule ACC-5 of engineering/DOCUMENTATION_STANDARD.md) when a block
    differs from what this script would write, names a cmdlet the module does not export, or belongs to
    a cmdlet with an undescribed parameter or undescribed output, and when an exported cmdlet has no block.

.PARAMETER ModulePath
    The module manifest to document. Defaults to src/JIM.PowerShell/JIM.psd1.

.PARAMETER DocsPath
    The folder of pages to update. Defaults to docs/powershell.

.PARAMETER OpenApiPath
    The OpenAPI document whose schemas describe the cmdlets' output. Without it, one is generated with
    Generate-OpenApiDoc.ps1, which builds the solution.

.PARAMETER ConfigPath
    The configuration (NonApiOutputTypes, OutputTypeSchemas). Defaults to
    docs/.conformance/powershell-reference.psd1.

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

# What a cmdlet returns, shared with Lint-DocExamples.ps1 so the two cannot disagree.
Import-Module (Join-Path $PSScriptRoot 'PowerShellReference.psm1') -Force

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

# Text from help or a DTO comment, made fit for the page: a cmdlet with its parameters, a parameter, and $true,
# $false and $null in code, which Get-Help cannot show and so the help leaves plain; and "empty" for null, a word
# from the source the page does not use (VOI-1). Text already in code is left as it is, and so is a quoted label,
# which is the product's own wording ("Null is a value").
function ConvertTo-PageText {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $code = '(?<![\w-])[A-Z][a-z]+-JIM[A-Za-z0-9]*(?:\s+-[A-Z][A-Za-z0-9]*(?::\$\w+)?)*|(?<![\w-])-[A-Z][A-Za-z0-9]*(?::\$\w+)?(?![\w-])|(?<!\w)\$(?:true|false|null)\b'
    $parts = foreach ($part in [regex]::Split($Text, '(`[^`]*`)')) {
        if ($part.StartsWith('`')) { $part; continue }
        $part = @(foreach ($piece in [regex]::Split($part, '("[^"]*")')) {
            if ($piece.StartsWith('"')) { $piece; continue }
            $piece -creplace '(?<![\w$-])null\b', 'empty' -creplace '(?<![\w$-])Null\b', 'Empty'
        }) -join ''
        [regex]::Replace($part, $code, { param($match) '`' + $match.Value + '`' })
    }
    $parts -join ''
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

function Join-All {
    param([string[]]$Items)
    $quoted = @($Items | ForEach-Object { "``$_``" })
    if ($quoted.Count -eq 1) { return $quoted[0] }
    ($quoted[0..($quoted.Count - 2)] -join ', ') + ' and ' + $quoted[-1]
}

# What a parameter's attributes say, as sentences to follow its description.
function Get-Ordinal {
    param([int]$Position)
    $words = 'first', 'second', 'third', 'fourth', 'fifth', 'sixth', 'seventh', 'eighth', 'ninth', 'tenth'
    if ($Position -lt $words.Count) { return $words[$Position] }
    "number $($Position + 1)"
}

function Get-AttributeSentences {
    param($Parameter, [object[]]$SetMetadata, [switch]$DeclaresPositions)

    $sentences = [System.Collections.Generic.List[string]]::new()

    # Only positions the cmdlet declares: PowerShell gives one to every parameter of a cmdlet that declares
    # none, and those are an accident of declaration order, not an interface.
    if ($DeclaresPositions) {
        $positions = @($SetMetadata | Where-Object { $_.Position -ge 0 } | ForEach-Object Position | Sort-Object -Unique)
        if ($positions.Count -gt 0) {
            $sentences.Add("Can be given by position, as the $((@($positions | ForEach-Object { Get-Ordinal $_ })) -join ' or ') argument.")
        }
    }

    $byValue = @($SetMetadata | Where-Object ValueFromPipeline).Count -gt 0
    $byProperty = @($SetMetadata | Where-Object ValueFromPipelineByPropertyName).Count -gt 0
    # An alias on a parameter bound by property name is what lets a parent object pipe in (a Connected
    # System carries Id, not ConnectedSystemId), so it is named as a property that binds, never offered as
    # a spelling to type: on a child-noun cmdlet, -Id reads as the child's id. Any other alias is a spelling.
    $aliases = @($Parameter.Aliases)
    $properties = if ($aliases.Count -gt 0) { ', ' + ((@($Parameter.Name) + $aliases | ForEach-Object { "``$_``" }) -join ' or ') } else { '' }
    if ($byValue -and $byProperty) { $sentences.Add("Accepts pipeline input, by value or by property name$properties.") }
    elseif ($byValue) { $sentences.Add('Accepts pipeline input.') }
    elseif ($byProperty) { $sentences.Add("Accepts pipeline input by property name$properties.") }
    if ($aliases.Count -gt 0 -and -not $byProperty) {
        $spellings = @($aliases | ForEach-Object { "``-$_``" }) -join ' or '
        $sentences.Add("Also accepted as $spellings.")
    }

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

function Format-Syntax {
    param($Reference)

    $lines = [System.Collections.Generic.List[string]]::new()
    $labelled = $Reference.Sets.Count -gt 1
    foreach ($set in $Reference.Sets) {
        if ($labelled) {
            if ($lines.Count -gt 0) { $lines.Add('') }
            $lines.Add('# ' + $set.Name + $(if ($set.IsDefault -and $set.Name -ne 'Default') { ' (default)' }))
        }
        $inSet = @($Reference.Parameters | Where-Object { @($_.Sets | ForEach-Object Name) -contains $set.Name })
        $positionOf = @{}
        if ($Reference.DeclaresPositions) {
            foreach ($metadata in @($set.Parameters | Where-Object { $_.Position -ge 0 })) { $positionOf[$metadata.Name] = $metadata.Position }
        }
        $positional = @($inSet | Where-Object { $positionOf.ContainsKey($_.Name) } | Sort-Object { $positionOf[$_.Name] })
        $inSet = @($inSet | Where-Object { -not $positionOf.ContainsKey($_.Name) })
        $mandatory = @($inSet | Where-Object { ($set.Parameters | Where-Object Name -eq $_.Name).IsMandatory })
        $mandatoryNames = @($mandatory | ForEach-Object Name)
        $optional = @($inSet | Where-Object { $mandatoryNames -notcontains $_.Name })
        $tokens = @($Reference.Name)
        foreach ($parameter in $positional) {
            $token = "[-$($parameter.Name)] <$(Get-FriendlyTypeName $parameter.Type)>"
            $tokens += if (($set.Parameters | Where-Object Name -eq $parameter.Name).IsMandatory) { $token } else { "[$token]" }
        }
        foreach ($parameter in $mandatory) {
            $tokens += if ($parameter.IsSwitch) { "-$($parameter.Name)" } else { "-$($parameter.Name) <$(Get-FriendlyTypeName $parameter.Type)>" }
        }
        foreach ($parameter in $optional) {
            $tokens += if ($parameter.IsSwitch) { "[-$($parameter.Name)]" } else { "[-$($parameter.Name) <$(Get-FriendlyTypeName $parameter.Type)>]" }
        }
        # -WhatIf and -Confirm close the line, as Get-Command -Syntax puts them; the other common parameters
        # are left out, since every cmdlet has them.
        if ($Reference.SupportsShouldProcess) { $tokens += '[-WhatIf]', '[-Confirm]' }
        $lines.Add($tokens -join ' ')
    }
    $text = "### Syntax`n`n" + '```powershell' + "`n" + ($lines -join "`n") + "`n" + '```'
    # Whether it prompts by default: with the default $ConfirmPreference, only a High impact does.
    if ($Reference.SupportsShouldProcess -and "$($Reference.ConfirmImpact)" -eq 'High') {
        # Every such cmdlet with a -Force has it skip the prompt and never -WhatIf (Confirm-JIMAction, and a
        # Pester guard in the module's tests).
        $skip = if ($Reference.Parameters | Where-Object Name -eq 'Force') { '`-Force` or `-Confirm:$false`' } else { '`-Confirm:$false`' }
        $text += "`n`nIt asks for confirmation before it acts: ``-WhatIf`` shows what it would do, and $skip skips the prompt."
    }
    $text
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
        $sentences = @(Get-AttributeSentences -Parameter $parameter.Metadata -SetMetadata $parameter.SetMetadata -DeclaresPositions:$Reference.DeclaresPositions)
        $description = $parameter.Description
        if ($sentences.Count -gt 0) {
            if ($description -and $description -notmatch '[.!?]$') { $description += '.' }
            $description = (@($description) + $sentences | Where-Object { $_ }) -join ' '
        }
        $default = if ($parameter.Default) { "``$($parameter.Default)``" } else { '' }
        '| `{0}` | `{1}` | {2} | {3} | {4} |' -f $parameter.Name, (Get-FriendlyTypeName $parameter.Type),
            (Format-Required $Reference $parameter), (ConvertTo-TableCell $default), (ConvertTo-TableCell (ConvertTo-PageText $description))
    }
    "### Parameters`n`n| Name | Type | Required | Default | Description |`n|------|------|----------|---------|-------------|`n" + ($rows -join "`n")
}

# A property's type as the site writes it, from its schema: a referenced schema or enum by name, an array
# as its items' type with [], and JSON types as their PowerShell names. null is left out of a union.
# What a property holds, as the module returns it. A primitive is its type. A reader cannot look up a class name,
# and the module returns no such type, so another object is an object, and an enumeration (which the API sends
# by name) a string; Note is the sentence naming that object's properties, or the enumeration's values (less the
# null the document lists where any use of the enumeration is nullable), to follow the property's description.
function Get-SchemaShape {
    param($Property, $Schemas)

    $shape = { param($type, $note) [pscustomobject]@{ Type = $type; Note = $note } }
    if ($Property.Contains('$ref')) {
        $name = $Property['$ref'] -replace '^#/components/schemas/', ''
        $target = $Schemas[$name]
        if ($null -eq $target) { return & $shape 'object' '' }
        if ($target.Contains('enum')) { return & $shape 'string' "One of $(Join-Choices @($target['enum'] | Where-Object { $null -ne $_ }))." }
        $names = @(Get-SchemaPropertyNames -Schemas $Schemas -Name $name)
        if ($names.Count -eq 0) { return Get-SchemaShape -Property $target -Schemas $Schemas }
        return & $shape 'object' "It has $(Join-All $names)."
    }
    foreach ($composite in 'oneOf', 'anyOf', 'allOf') {
        if ($Property.Contains($composite)) {
            $parts = @($Property[$composite] | Where-Object { -not ($_.Contains('type') -and $_['type'] -eq 'null') })
            if ($parts.Count -eq 1) { return Get-SchemaShape -Property $parts[0] -Schemas $Schemas }
            return & $shape 'object' ''
        }
    }
    $types = @($(if ($Property.Contains('type')) { $Property['type'] }) | Where-Object { $_ -ne 'null' })
    $format = if ($Property.Contains('format')) { $Property['format'] } else { '' }
    if ($types -contains 'array') {
        $items = if ($Property.Contains('items')) { Get-SchemaShape -Property $Property['items'] -Schemas $Schemas } else { & $shape 'object' '' }
        $note = $items.Note -creplace '^One of ', 'Each is one of ' -creplace '^It has ', 'Each has '
        return & $shape "$($items.Type)[]" $note
    }
    if ($Property.Contains('enum')) { return & $shape 'string' "One of $(Join-Choices @($Property['enum'] | Where-Object { $null -ne $_ }))." }
    if ($types -contains 'integer') { return & $shape $(if ($format -eq 'int64') { 'long' } else { 'int' }) '' }
    if ($types -contains 'number') { return & $shape $(if ($format -eq 'float') { 'float' } elseif ($format -eq 'decimal') { 'decimal' } else { 'double' }) '' }
    if ($types -contains 'boolean') { return & $shape 'bool' '' }
    if ($types -contains 'string') {
        $type = switch ($format) {
            'uuid' { 'guid' }
            'date-time' { 'datetime' }
            'date' { 'date' }
            'byte' { 'byte[]' }
            default { 'string' }
        }
        return & $shape $type ''
    }
    if ($types -contains 'object' -and $Property.Contains('additionalProperties')) { return & $shape 'hashtable' '' }
    if ($Property.Contains('properties') -and $Property['properties'].Count -gt 0) {
        return & $shape 'object' "It has $(Join-All @($Property['properties'].Keys | ForEach-Object { ConvertTo-PropertyName $_ }))."
    }
    & $shape 'object' ''
}

# A property a cmdlet's .OUTPUTS help lists, typed as the page types the API's: one whose type names a schema (an
# enumeration, or another object) is resolved as the API's own properties are, so no class name reaches the page.
function Resolve-ListedProperty {
    param($Property, $Schemas)

    $name = $Property.Type -replace '\[\]$', ''
    if (-not $Schemas.Contains($name)) { return $Property }
    $reference = @{ '$ref' = "#/components/schemas/$name" }
    $shape = if ($Property.Type.EndsWith('[]')) { Get-SchemaShape -Property @{ type = 'array'; items = $reference } -Schemas $Schemas }
    else { Get-SchemaShape -Property $reference -Schemas $Schemas }
    [pscustomobject]@{ Name = $Property.Name; Type = $shape.Type; Description = (@($Property.Description, $shape.Note) | Where-Object { $_ }) -join ' ' }
}

# A DTO's XML comment, which is written for the code, as a public page may carry it: on one line, without the
# issue and pull request numbers comments cite (LANG-8), and without the gaps and brackets they leave. A
# <see cref> to a member reaches the document as its type, owner and name ("int Summary.Total", with a
# generic's brackets as HTML entities); only the name is kept. The type is a C# keyword, a generic, or a
# PascalCase name of two or more words, so the capitalised first word of a sentence ("The Summary.Total") is
# never taken for one.
function ConvertTo-PublicDescription {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $text = $Text -replace '\s+', ' '
    $type = '(?:(?:string|int|long|short|byte|bool|decimal|double|float|object|Guid|DateTime|DateTimeOffset|TimeSpan)\??|[A-Za-z_]\w*(?:<[^<>]*>|&lt;(?:(?!&gt;).)*&gt;)\??|[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\??)(?:\[\])?'
    $text = $text -creplace "(?<![\w.])$type (?:[A-Za-z_]\w*\.)+(?<member>[A-Za-z_]\w*)\b", '${member}'
    $text = $text -replace '\s*\(\s*#\d+(?:\s*,\s*#\d+)*\s*\)', ''
    $text = $text -replace ',\s*#\d+\b', ''
    $text = $text -replace '#\d+\s*,\s*', ''
    $text = $text -replace '\s*#\d+\b', ''
    $text = $text -replace '\(\s*\)', '' -replace '\s+([.,;:])(?=\s|$)', '$1' -replace '\s{2,}', ' '
    $text.Trim()
}

# A schema's properties in the order declared, those it takes through allOf first: each one's name in
# the PascalCase the module returns, its type, and its description.
# A property's description. For a nullable reference (an enum or object that may be null), the OpenAPI
# document puts it beside the reference, inside the oneOf, rather than on the property.
function Get-SchemaDescription {
    param($Property)

    if ($Property.Contains('description')) { return $Property['description'] }
    foreach ($composite in 'oneOf', 'anyOf', 'allOf') {
        if ($Property.Contains($composite)) {
            foreach ($part in $Property[$composite]) {
                if ($part.Contains('description')) { return $part['description'] }
            }
        }
    }
    ''
}

function Get-SchemaProperties {
    param($Schemas, [string]$Name, [System.Collections.Generic.HashSet[string]]$Seen)

    if (-not $Seen) { $Seen = [System.Collections.Generic.HashSet[string]]::new() }
    if (-not $Seen.Add($Name)) { return @() }
    $schema = $Schemas[$Name]
    $properties = [System.Collections.Generic.List[object]]::new()
    $addFrom = {
        param($bag)
        foreach ($key in $bag.Keys) {
            $property = $bag[$key]
            $shape = Get-SchemaShape -Property $property -Schemas $Schemas
            $description = (@(ConvertTo-PublicDescription (Get-SchemaDescription $property)) + $shape.Note | Where-Object { $_ }) -join ' '
            $properties.Add([pscustomobject]@{
                Name        = ConvertTo-PropertyName $key
                Type        = $shape.Type
                Description = $description
            })
        }
    }
    if ($schema.Contains('allOf')) {
        foreach ($part in $schema['allOf']) {
            if ($part.Contains('$ref')) {
                foreach ($inherited in (Get-SchemaProperties -Schemas $Schemas -Name ($part['$ref'] -replace '^#/components/schemas/', '') -Seen $Seen)) { $properties.Add($inherited) }
            }
            elseif ($part.Contains('properties')) { & $addFrom $part['properties'] }
        }
    }
    if ($schema.Contains('properties')) { & $addFrom $schema['properties'] }

    $seenNames = @{}
    @($properties | Where-Object { if ($seenNames.ContainsKey($_.Name)) { $false } else { $seenNames[$_.Name] = $true; $true } })
}

# The anchor MkDocs gives a cmdlet's heading.
function Get-HeadingAnchor {
    param([string]$Heading)
    ($Heading.ToLowerInvariant() -replace '[^\w\s-]', '' -replace '\s+', '-').Trim('-')
}

# What sets one parameter set apart, in the reader's terms: the parameters that choose it. $null for the default set
# when it needs nothing the others do not (the call made without the others' parameters). A parameter every set
# requires tells no set apart, so it is left out. Join is how the parameters combine: all of them, where the set
# requires them; any one of them, where they are optional but belong to this set alone.
function Get-SetCondition {
    param($Reference, [string]$SetName)

    $own = @($Reference.Parameters | ForEach-Object Name)
    $sets = @($Reference.Sets)
    $set = $sets | Where-Object Name -eq $SetName
    $others = @($sets | Where-Object Name -ne $SetName)
    $mandatoryIn = { param($s) @($s.Parameters | Where-Object { $_.IsMandatory -and $own -contains $_.Name } | ForEach-Object Name) }
    $memberOf = { param($s) @($s.Parameters | Where-Object { $own -contains $_.Name } | ForEach-Object Name) }
    $common = @(& $mandatoryIn $sets[0])
    foreach ($other in $sets) { $inOther = @(& $mandatoryIn $other); $common = @($common | Where-Object { $inOther -contains $_ }) }

    $required = @(& $mandatoryIn $set | Where-Object { $common -notcontains $_ })
    if ($set.IsDefault -and $required.Count -eq 0) { return $null }
    $unique = @($required | Where-Object { $name = $_; -not @($others | Where-Object { (& $mandatoryIn $_) -contains $name }) })
    if ($unique.Count -gt 0) { return [pscustomobject]@{ Parameters = $unique; Join = 'and' } }
    $alone = @(& $memberOf $set | Where-Object { $name = $_; $common -notcontains $name -and -not @($others | Where-Object { (& $memberOf $_) -contains $name }) })
    if ($alone.Count -gt 0) { return [pscustomobject]@{ Parameters = $alone; Join = 'or' } }
    if ($required.Count -gt 0) { return [pscustomobject]@{ Parameters = $required; Join = 'and' } }
    $null
}

# When a type comes back, from the sets that return it, in the order its [OutputType] lines name them: "By default",
# "With -Id or -Follow", "By default, or with -All". Empty where every set returns it.
function Get-OutputCondition {
    param($Reference, [string[]]$Sets)

    $everySet = @($Reference.Sets | ForEach-Object Name)
    if ($Sets -contains $script:AllSets -or @($everySet | Where-Object { $Sets -notcontains $_ }).Count -eq 0) { return '' }
    $byDefault = $false
    $phrases = [System.Collections.Generic.List[string]]::new()
    foreach ($setName in @($Sets | Where-Object { $everySet -contains $_ })) {
        $condition = Get-SetCondition -Reference $Reference -SetName $setName
        if ($null -eq $condition) { $byDefault = $true; continue }
        $phrase = @($condition.Parameters | ForEach-Object { "``-$_``" }) -join " $($condition.Join) "
        if (-not $phrases.Contains($phrase)) { $phrases.Add($phrase) }
    }
    $joiner = if (@($phrases | Where-Object { $_ -match ' (and|or) ' }).Count -gt 0) { ', or ' } else { ' or ' }
    if ($byDefault -and $phrases.Count -eq 0) { return 'By default' }
    if ($byDefault) { return 'By default, or with ' + ($phrases -join $joiner) }
    'With ' + ($phrases -join $joiner)
}

# "a" or "an" before a type's name.
function Get-Article {
    param([string]$Word)
    if ($Word -match '^[aeiouAEIOU]') { 'an' } else { 'a' }
}

# Each type the cmdlet declares, labelled by when it comes back: a schema's properties are tabled the first time the
# page returns it, and later cmdlets on the page refer back to that one rather than repeat the table. The only type
# a cmdlet returns, from every set, needs no label.
function Format-Output {
    param($Reference, $Schemas, [hashtable]$NonApiOutputTypes, [hashtable]$OutputTypeSchemas, [hashtable]$TabledOnPage, [System.Collections.Generic.List[string]]$Problems)

    if ($Reference.OutputTypes.Count -eq 0) {
        $Problems.Add("$($Reference.Name) declares nothing about its output; name what it returns with [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')], or add [OutputType([void])] if it returns nothing")
    }

    $everySet = @($Reference.Sets | ForEach-Object Name)
    $declared = [ordered]@{}
    foreach ($typeName in $Reference.OutputTypes.Keys) {
        if ($typeName -in @('System.Void', 'void')) { continue }
        if ($typeName -in @('System.Management.Automation.PSObject', 'System.Management.Automation.PSCustomObject', 'PSCustomObject', 'psobject')) {
            $Problems.Add("$($Reference.Name) declares its output only as PSCustomObject; name what it returns with [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')]")
            continue
        }
        $sets = @($Reference.OutputTypes[$typeName])
        foreach ($set in $sets | Where-Object { $_ -ne $script:AllSets -and $everySet -notcontains $_ }) {
            $Problems.Add("$($Reference.Name) declares $typeName for parameter set $set, which it does not have; correct the set's name")
        }
        $declared[$typeName] = $sets
    }

    # Types that share a parameter set cannot be told apart by it, so the help must say when each comes back.
    $shared = [System.Collections.Generic.List[string]]::new()
    $names = @($declared.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) {
        for ($j = $i + 1; $j -lt $names.Count; $j++) {
            $a = @($declared[$names[$i]]); $b = @($declared[$names[$j]])
            if ($a -contains $script:AllSets -or $b -contains $script:AllSets -or @($a | Where-Object { $b -contains $_ }).Count -gt 0) {
                foreach ($name in $names[$i], $names[$j]) { if (-not $Reference.OutputLabels.Contains($name) -and -not $shared.Contains($name)) { $shared.Add($name) } }
            }
        }
    }
    if ($shared.Count -gt 0) {
        $listed = if ($shared.Count -eq 2) { $shared -join ' and ' } else { ($shared[0..($shared.Count - 2)] -join ', ') + ' and ' + $shared[-1] }
        $Problems.Add("$($Reference.Name) returns $listed from the same parameter set, which therefore cannot tell a reader which comes back; label each in its .OUTPUTS help with a line '<type>: <when it is returned>'")
    }

    $sections = [System.Collections.Generic.List[string]]::new()
    $used = @{}
    foreach ($typeName in $declared.Keys) {
        $label = if ($Reference.OutputLabels.Contains($typeName)) { ConvertTo-PageText $Reference.OutputLabels[$typeName] }
        elseif ($declared.Count -eq 1) { Get-OutputCondition -Reference $Reference -Sets $declared[$typeName] }
        elseif ($shared.Contains($typeName)) { '' }
        else { Get-OutputCondition -Reference $Reference -Sets $declared[$typeName] }
        if ($label) {
            $label = $label.Substring(0, 1).ToUpperInvariant() + $label.Substring(1)
            if ($used.ContainsKey($label)) { $Problems.Add("$($Reference.Name) returns $($used[$label]) and $typeName under the same label, '$label'; label each in its .OUTPUTS help with a line '<type>: <when it is returned>'") }
            else { $used[$label] = $typeName }
        }
        $bold = if ($label) { "**$label**" } else { '' }

        $display = $typeName
        $schemaName = $null
        if ($typeName -like 'JIM.*') {
            $display = $typeName.Substring(4)
            # The API's object (by its own name, or the name the module gives it), or a type the module builds.
            $source = Resolve-OutputTypeSource -TypeName $typeName -Schemas $Schemas -NonApiOutputTypes $NonApiOutputTypes -OutputTypeSchemas $OutputTypeSchemas
            if ($source.Kind -eq 'Schema') { $schemaName = $source.SchemaName }
            elseif ($source.Problem -like 'OutputTypeSchemas *') { $Problems.Add($source.Problem) }
            elseif ($source.Problem) { $Problems.Add("$($Reference.Name) $($source.Problem)") }
        }
        else {
            $resolved = $typeName -as [type]
            if ($resolved) { $display = Get-FriendlyTypeName $resolved }
        }

        # The table, or a reference back to where the page already gives it.
        $referBack = {
            param($first)
            $link = "[$first](#$(Get-HeadingAnchor $first))"
            if ($bold) { "${bold}: properties as under $link." } else { "Properties as under $link." }
        }
        $plain = if ($bold) { "${bold}: $(Get-Article $display) ``$display``." } else { "Returns $(Get-Article $display) ``$display``." }

        if (-not $schemaName -and $NonApiOutputTypes.ContainsKey($typeName)) {
            # A type the module builds: its properties come from its .OUTPUTS help.
            $key = "module:$typeName"
            if ($TabledOnPage.ContainsKey($key)) { $sections.Add((& $referBack $TabledOnPage[$key])) }
            elseif ($Reference.OutputProperties.ContainsKey($typeName)) {
                $TabledOnPage[$key] = $Reference.Name
                $sections.Add((Format-PropertyTable -Label $bold -Properties @($Reference.OutputProperties[$typeName] | ForEach-Object { Resolve-ListedProperty -Property $_ -Schemas $Schemas })))
            }
            else {
                $Problems.Add("$($Reference.Name) declares $typeName, a type the module builds, but its .OUTPUTS help lists none of its properties; add a paragraph whose first line is $typeName and whose others read 'Name (type): description'")
                $sections.Add($plain)
            }
        }
        elseif (-not $schemaName) {
            $sections.Add($plain)
        }
        else {
            # The API's object, with any properties the module adds to it (listed in .OUTPUTS) after its own.
            $added = @(if ($Reference.OutputProperties.ContainsKey($typeName)) { $Reference.OutputProperties[$typeName] | ForEach-Object { Resolve-ListedProperty -Property $_ -Schemas $Schemas } })
            $key = if ($added.Count -gt 0) { "added:$typeName" } else { $schemaName }
            if ($TabledOnPage.ContainsKey($key)) { $sections.Add((& $referBack $TabledOnPage[$key])) }
            else {
                $TabledOnPage[$key] = $Reference.Name
                $properties = @(Get-SchemaProperties -Schemas $Schemas -Name $schemaName)
                foreach ($property in $added | Where-Object { $properties.Name -contains $_.Name }) {
                    $Problems.Add("$($Reference.Name) lists $($property.Name) as a property it adds to $typeName, but the $schemaName schema already has it; describe it in the DTO's XML comment instead")
                }
                $sections.Add((Format-PropertyTable -Label $bold -Properties ($properties + @($added | Where-Object { $properties.Name -notcontains $_.Name }))))
            }
        }
    }

    foreach ($listed in @($Reference.OutputProperties.Keys) + @($Reference.OutputLabels.Keys) | Select-Object -Unique | Where-Object { -not $Reference.OutputTypes.Contains($_) }) {
        $Problems.Add("$($Reference.Name) names $listed in its .OUTPUTS help, but declares no such output type; declare it, or correct the name")
    }

    $text = "### Output"
    if ($Reference.Outputs.Count -gt 0) { $text += "`n`n" + (@($Reference.Outputs | ForEach-Object { ConvertTo-PageText $_ }) -join "`n`n") }
    if ($sections.Count -eq 0) {
        if ($Reference.Outputs.Count -eq 0) { $text += "`n`nNone." }
        return $text
    }
    $text + "`n`n" + ($sections -join "`n`n")
}

# A type's label and its properties' table. A Description column wholly empty says nothing, so it is left out;
# a schema's descriptions come from the DTO's XML comments, so documenting the DTO is what fills it.
function Format-PropertyTable {
    param([string]$Label, [object[]]$Properties)

    $lead = if ($Label) { "${Label}:`n`n" } else { '' }
    if (@($Properties | Where-Object Description).Count -gt 0) {
        $rows = foreach ($property in $Properties) { '| `{0}` | `{1}` | {2} |' -f $property.Name, $property.Type, (ConvertTo-TableCell (ConvertTo-PageText $property.Description)) }
        return "$lead| Property | Type | Description |`n|----------|------|-------------|`n" + (@($rows) -join "`n")
    }
    $rows = foreach ($property in $Properties) { '| `{0}` | `{1}` |' -f $property.Name, $property.Type }
    "$lead| Property | Type |`n|----------|------|`n" + (@($rows) -join "`n")
}

function Format-Block {
    param($Reference, $Schemas, [hashtable]$NonApiOutputTypes, [hashtable]$OutputTypeSchemas, [hashtable]$TabledOnPage, [System.Collections.Generic.List[string]]$Problems)

    foreach ($parameter in @($Reference.Parameters | Where-Object { -not $_.Description })) {
        $Problems.Add("$($Reference.Name) has no .PARAMETER help for -$($parameter.Name)")
    }
    (Format-Syntax $Reference) + "`n`n" + (Format-Parameters $Reference) + "`n`n" +
        (Format-Output -Reference $Reference -Schemas $Schemas -NonApiOutputTypes $NonApiOutputTypes -OutputTypeSchemas $OutputTypeSchemas -TabledOnPage $TabledOnPage -Problems $Problems)
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
$outputConfig = Import-OutputTypeConfiguration -Path $ConfigPath
$nonApi = $outputConfig.NonApiOutputTypes
$schemaFor = $outputConfig.OutputTypeSchemas

if (-not $OpenApiPath) {
    $OpenApiPath = Join-Path ([System.IO.Path]::GetTempPath()) "jim-openapi-$([guid]::NewGuid().ToString('N')).json"
    Write-Host "No -OpenApiPath given; generating the OpenAPI document (this builds the solution)..."
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Generate-OpenApiDoc.ps1') -OutputPath $OpenApiPath
    if ($LASTEXITCODE -ne 0) { throw 'Generate-OpenApiDoc.ps1 failed; pass -OpenApiPath to use a document generated earlier.' }
}
$schemas = Import-OpenApiSchemas -Path $OpenApiPath

$module = Import-Module -Name (Resolve-Path -LiteralPath $ModulePath).Path -Force -PassThru -DisableNameChecking
$exported = @{}
foreach ($command in $module.ExportedFunctions.Values) { $exported[$command.Name] = $command }

# --- Pages ----------------------------------------------------------------------------------------------

$failures = 0
$blockCount = 0
$changedPages = 0
$seenBlocks = @{}
$sectionPage = @{}

foreach ($page in @(Get-ChildItem -LiteralPath $DocsPath -Filter '*.md' -File | Sort-Object Name)) {
    $text = Get-Content -LiteralPath $page.FullName -Raw
    if ($null -eq $text) { $text = '' }
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $page.FullName) -replace '\\', '/'
    $updated = $text

    $begins = @([regex]::Matches($text, $script:BeginPattern))
    $ends = @([regex]::Matches($text, $script:EndPattern))

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

    # Rendered in page order, since a schema is tabled where the page first returns it; written back from
    # the last block to the first, so the earlier indices stay valid.
    $tabledOnPage = @{}
    $rendered = foreach ($block in @($blocks | Sort-Object { $_.Begin.Index })) {
        $line = Get-LineNumber $text $block.Begin.Index
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
        $content = "`n" + (Format-Block -Reference $reference -Schemas $schemas -NonApiOutputTypes $nonApi -OutputTypeSchemas $schemaFor -TabledOnPage $tabledOnPage -Problems $problems) + "`n"
        foreach ($problem in $problems) {
            Write-Finding $relative $line $problem
            $failures++
        }
        [pscustomobject]@{ Block = $block; Line = $line; Content = $content }
    }

    foreach ($item in @($rendered | Sort-Object { $_.Block.Begin.Index } -Descending)) {
        $block = $item.Block
        $content = $item.Content
        $start = $block.Begin.Index + $block.Begin.Length
        $current = $text.Substring($start, $block.End.Index - $start)
        if ($current -cne $content) {
            if ($Check) {
                $expectedLines = $content -split "`n"
                $foundLines = $current -split "`n"
                $index = 0
                while ($index -lt $expectedLines.Count -and $index -lt $foundLines.Count -and $expectedLines[$index] -ceq $foundLines[$index]) { $index++ }
                $expected = if ($index -lt $expectedLines.Count) { $expectedLines[$index] } else { '(end of block)' }
                $found = if ($index -lt $foundLines.Count) { $foundLines[$index] } else { '(end of block)' }
                Write-Finding $relative ($item.Line + $index) "the generated block for $($block.Name) differs from the module; run scripts/Update-PowerShellReference.ps1 to regenerate it, and change the cmdlet's help rather than the page. Expected: '$expected'; found: '$found'"
                $failures++
            }
            else {
                $updated = $updated.Substring(0, $start) + $content + $updated.Substring($block.End.Index)
            }
        }
    }

    # Where each cmdlet's section is, so the cmdlets with no block can be listed by page. A name can end at
    # "JIM" (Connect-JIM).
    foreach ($heading in [regex]::Matches($text, '(?m)^#{2,3} (?<name>[A-Za-z]+-JIM[A-Za-z0-9]*)\s*$')) {
        $name = $heading.Groups['name'].Value
        if (-not $sectionPage.ContainsKey($name)) { $sectionPage[$name] = $relative }
    }

    if (-not $Check -and $updated -cne $text) {
        Set-Content -LiteralPath $page.FullName -Value $updated -NoNewline
        $changedPages++
    }
}

# An allowed non-API output type, or a mapped one, that no exported cmdlet declares any more.
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
foreach ($typeName in $schemaFor.Keys) {
    if (-not $declared.ContainsKey($typeName)) {
        Write-Finding '' 0 "OutputTypeSchemas lists $typeName, which no cmdlet declares; remove the entry"
        $failures++
    }
}

# Every exported cmdlet has a block. Counted from the module, not from the pages' headings, so a cmdlet the
# pages give no section at all fails too.
foreach ($name in @($exported.Keys | Where-Object { -not $seenBlocks.ContainsKey($_) } | Sort-Object)) {
    if ($sectionPage.ContainsKey($name)) {
        Write-Finding $sectionPage[$name] 1 "$name has no generated block; put '<!-- BEGIN GENERATED: $name -->' and '<!-- END GENERATED: $name -->' in its section, where its Syntax, Parameters and Output go, and run scripts/Update-PowerShellReference.ps1"
    }
    else {
        Write-Finding '' 0 "$name has no generated block, and no section on any page; give it a '## $name' section on its docs/powershell/ page, with the markers, and run scripts/Update-PowerShellReference.ps1"
    }
    $failures++
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
