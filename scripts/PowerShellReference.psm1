# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# What a JIM cmdlet returns, read from the module and the OpenAPI document. Shared by
# Update-PowerShellReference.ps1, which tables each cmdlet's output on its docs page (ACC-5), and
# Lint-DocExamples.ps1, which checks every property an example reads against it (ACC-6), so the two can
# never disagree about what a cmdlet returns.

Set-StrictMode -Version Latest

$script:AllSets = '__AllParameterSets'
# One property of a type the module builds, as its .OUTPUTS help lists it.
$script:PropertyLinePattern = '^(?<name>[A-Za-z_]\w*) \((?<type>[^)]+)\): (?<description>.+)$'

# A .OUTPUTS or .PARAMETER help text as Markdown: lines within a paragraph joined, paragraphs kept.
function ConvertTo-Paragraphs {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    $normalised = $Text -replace "`r", ''
    @(($normalised.Trim() -split "\n\s*\n") | ForEach-Object { ($_ -split '\s*\n\s*' | ForEach-Object Trim) -join ' ' } |
        Where-Object { $_ })
}

# A cmdlet's .OUTPUTS help, split into its prose, the property lists of the types the module builds itself, and
# the labels that say when each type comes back. A list is a paragraph whose first line names a JIM type and whose
# other lines read "Name (type): description", a line that does not starting another being the previous description
# running on. A label follows the type's name on that first line ("JIM.X: With -Quiet"), or stands on a line of its
# own for a type with no list ("System.Boolean: With -Quiet").
function Split-OutputsHelp {
    param([string[]]$Sections)

    $prose = [System.Collections.Generic.List[string]]::new()
    $lists = @{}
    $labels = [ordered]@{}
    $typeLine = '^(?<type>(?:JIM|System)\.[\w.]+(?:\[\])?)(?::\s*(?<label>\S.*))?$'
    foreach ($section in @($Sections | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        foreach ($paragraph in (($section -replace "`r", '').Trim() -split "\n\s*\n")) {
            $lines = @($paragraph -split '\n' | ForEach-Object Trim | Where-Object { $_ })
            if ($lines.Count -eq 0) { continue }
            $type = [regex]::Match($lines[0], $typeLine)
            if ($type.Success -and $type.Groups['label'].Success) { $labels[$type.Groups['type'].Value] = $type.Groups['label'].Value }
            if ($type.Success -and $type.Groups['label'].Success -and $lines.Count -eq 1) { continue }
            if ($lines.Count -ge 2 -and $type.Success -and $type.Groups['type'].Value -cmatch '^JIM\.\w+$' -and $lines[1] -cmatch $script:PropertyLinePattern) {
                $properties = [System.Collections.Generic.List[object]]::new()
                foreach ($line in $lines[1..($lines.Count - 1)]) {
                    $match = [regex]::Match($line, $script:PropertyLinePattern)
                    if ($match.Success) {
                        $properties.Add([pscustomobject]@{ Name = $match.Groups['name'].Value; Type = $match.Groups['type'].Value; Description = $match.Groups['description'].Value })
                    }
                    else { $properties[$properties.Count - 1].Description += " $line" }
                }
                $lists[$type.Groups['type'].Value] = $properties
            }
            else { $prose.Add($lines -join ' ') }
        }
    }
    [pscustomobject]@{ Prose = @($prose); Lists = $lists; Labels = $labels }
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

    $declaresPositions = $null -ne $paramBlock -and $null -ne $paramBlock.Find({
            param($node)
            $node -is [System.Management.Automation.Language.NamedAttributeArgumentAst] -and $node.ArgumentName -eq 'Position' -and
                $node.Parent.TypeName.Name -in 'Parameter', 'System.Management.Automation.Parameter', 'ParameterAttribute'
        }, $true)

    $outputsHelp = Split-OutputsHelp -Sections @(if ($help) { $help.Outputs })

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
        DeclaresPositions = $declaresPositions
        SupportsShouldProcess = $Command.Parameters.ContainsKey('WhatIf')
        ConfirmImpact = @($Command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] } | ForEach-Object ConfirmImpact | Select-Object -First 1)
        Outputs     = $outputsHelp.Prose
        OutputProperties = $outputsHelp.Lists
        OutputLabels = $outputsHelp.Labels
        OutputTypes = $outputTypes
    }
}

# A schema's property name in the PascalCase the module returns.
function ConvertTo-PropertyName {
    param([string]$Name)
    $Name.Substring(0, 1).ToUpperInvariant() + $Name.Substring(1)
}

# The names of a schema's properties, those it takes through allOf first.
function Get-SchemaPropertyNames {
    param($Schemas, [string]$Name, [System.Collections.Generic.HashSet[string]]$Seen)

    if (-not $Seen) { $Seen = [System.Collections.Generic.HashSet[string]]::new() }
    if (-not $Seen.Add($Name) -or -not $Schemas.Contains($Name)) { return @() }
    $schema = $Schemas[$Name]
    $names = [System.Collections.Generic.List[string]]::new()
    if ($schema.Contains('allOf')) {
        foreach ($part in $schema['allOf']) {
            if ($part.Contains('$ref')) { foreach ($inherited in (Get-SchemaPropertyNames -Schemas $Schemas -Name ($part['$ref'] -replace '^#/components/schemas/', '') -Seen $Seen)) { $names.Add($inherited) } }
            elseif ($part.Contains('properties')) { foreach ($key in $part['properties'].Keys) { $names.Add((ConvertTo-PropertyName $key)) } }
        }
    }
    if ($schema.Contains('properties')) { foreach ($key in $schema['properties'].Keys) { $names.Add((ConvertTo-PropertyName $key)) } }
    @($names | Select-Object -Unique)
}

# The output type configuration in docs/.conformance/powershell-reference.psd1: the types the module builds
# itself (NonApiOutputTypes), and the module's own names for the API's objects (OutputTypeSchemas).
function Import-OutputTypeConfiguration {
    param([string]$Path)

    $config = if ($Path -and (Test-Path -LiteralPath $Path)) { Import-PowerShellDataFile -LiteralPath $Path } else { @{} }
    [pscustomobject]@{
        NonApiOutputTypes = if ($config.ContainsKey('NonApiOutputTypes') -and $config.NonApiOutputTypes) { $config.NonApiOutputTypes } else { @{} }
        OutputTypeSchemas = if ($config.ContainsKey('OutputTypeSchemas') -and $config.OutputTypeSchemas) { $config.OutputTypeSchemas } else { @{} }
    }
}

# The OpenAPI document's schemas, by name.
function Import-OpenApiSchemas {
    param([Parameter(Mandatory)][string]$Path)

    $openApi = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($openApi.Contains('components') -and $openApi['components'].Contains('schemas')) { return $openApi['components']['schemas'] }
    @{}
}

# Where a declared output type's properties come from. Schema: an object the API returns, by its own name or
# the name the module gives it. Module: a type the module builds, whose properties its .OUTPUTS help lists.
# Other: a .NET type, such as int. Unknown: a JIM type that is neither, which Problem explains.
function Resolve-OutputTypeSource {
    param([Parameter(Mandatory)][string]$TypeName, $Schemas, [hashtable]$NonApiOutputTypes, [hashtable]$OutputTypeSchemas)

    $result = { param($kind, $schema, $problem) [pscustomobject]@{ Kind = $kind; SchemaName = $schema; Problem = $problem } }
    if ($TypeName -notlike 'JIM.*') { return & $result 'Other' $null $null }
    $display = $TypeName.Substring(4)
    if ($OutputTypeSchemas.ContainsKey($TypeName)) {
        if ($Schemas.Contains($OutputTypeSchemas[$TypeName])) { return & $result 'Schema' $OutputTypeSchemas[$TypeName] $null }
        return & $result 'Unknown' $null "OutputTypeSchemas maps $TypeName to $($OutputTypeSchemas[$TypeName]), which is not a schema in the OpenAPI document; correct the schema's name"
    }
    if ($Schemas.Contains($display)) { return & $result 'Schema' $display $null }
    if ($NonApiOutputTypes.ContainsKey($TypeName)) { return & $result 'Module' $null $null }
    & $result 'Unknown' $null "declares output type $TypeName, which is not a schema in the OpenAPI document; correct the name, or list it in NonApiOutputTypes with the reason"
}

# The names of the properties a cmdlet returns in each parameter set, from Get-CmdletReference. A set whose
# output cannot be known (it declares nothing, a .NET type, or a type no schema or .OUTPUTS list describes)
# maps to $null, which a caller must treat as "anything goes" rather than "nothing".
function Get-OutputPropertyNamesBySet {
    param([Parameter(Mandatory)]$Reference, $Schemas, [hashtable]$NonApiOutputTypes, [hashtable]$OutputTypeSchemas)

    $bySet = @{}
    $setNames = @($Reference.Sets | ForEach-Object Name)
    foreach ($set in $setNames) { $bySet[$set] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase) }
    $unknown = [System.Collections.Generic.HashSet[string]]::new()
    $covered = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($typeName in $Reference.OutputTypes.Keys) {
        if ($typeName -in @('System.Void', 'void')) { continue }
        $sets = @($Reference.OutputTypes[$typeName])
        $targets = if ($sets -contains $script:AllSets) { $setNames } else { @($sets | Where-Object { $setNames -contains $_ }) }
        $source = Resolve-OutputTypeSource -TypeName $typeName -Schemas $Schemas -NonApiOutputTypes $NonApiOutputTypes -OutputTypeSchemas $OutputTypeSchemas
        $names = switch ($source.Kind) {
            'Schema' { @(Get-SchemaPropertyNames -Schemas $Schemas -Name $source.SchemaName) }
            'Module' { $null }
            default { $null }
        }
        $added = @(if ($Reference.OutputProperties.ContainsKey($typeName)) { $Reference.OutputProperties[$typeName] | ForEach-Object Name })
        if ($source.Kind -eq 'Module') { $names = if ($added.Count -gt 0) { $added } else { $null } }
        foreach ($set in $targets) {
            [void]$covered.Add($set)
            if ($null -eq $names) { [void]$unknown.Add($set); continue }
            foreach ($name in @($names) + $added) { [void]$bySet[$set].Add($name) }
        }
    }

    $result = @{}
    foreach ($set in $setNames) {
        $result[$set] = if ($unknown.Contains($set) -or -not $covered.Contains($set)) { $null } else { $bySet[$set] }
    }
    $result
}

Export-ModuleMember -Function ConvertTo-Paragraphs, Split-OutputsHelp, Get-CmdletReference, ConvertTo-PropertyName,
    Get-SchemaPropertyNames, Import-OutputTypeConfiguration, Import-OpenApiSchemas, Resolve-OutputTypeSource,
    Get-OutputPropertyNamesBySet
