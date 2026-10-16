# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails documentation that names a JIM PowerShell cmdlet that does not exist,
    passes a cmdlet a parameter it does not declare, or invokes a cmdlet via a
    parameter ALIAS instead of the parameter's real name.

.DESCRIPTION
    Get-JIMRunProfile declares [Alias('Id')] on its -ConnectedSystemId parameter.
    The alias exists so a Connected System (whose property is Id, not
    ConnectedSystemId) can be piped straight into the cmdlet. But a PowerShell
    alias also works as a plain command-line spelling, so a doc example reading
    "Get-JIMRunProfile -Id 42" looks like "Run Profile 42" while it actually
    means "every Run Profile on Connected System 42". One shipped example piped
    exactly that into Remove-JIMRunProfile -Force, which would have deleted
    every Run Profile on that Connected System. Spelling the parameter out in
    documentation removes the ambiguity entirely.

    This lint builds a cmdlet -> alias -> real-parameter-name map by parsing
    every public cmdlet in src/JIM.PowerShell/Public with the PowerShell AST
    (never regex, so nested attributes and multi-line param blocks are read
    correctly), then scans two places for a cmdlet invoked via one of those
    aliases:

      1. Every ```powershell fenced code block under docs/.
      2. The .EXAMPLE blocks of the module's own comment-based help (these
         reach users via Get-Help, so they need the same scrutiny).

    Both scans parse the extracted PowerShell text with the same AST parser
    (Parser::ParseInput) rather than matching against raw text, so a cmdlet and
    its parameters spread across several lines (backtick continuation, or a
    trailing pipe) resolve correctly, and "-Id" can never be confused with
    "-Identity" or "-IdleTimeout" (CommandParameterAst.ParameterName is the
    exact bound name, not a prefix match).

    Deliberately NOT scanned: prose and parameter tables outside fenced code
    blocks. Those legitimately document an alias ("Alias: `Id`." in a table
    cell, or "Alias: `ScheduleId`" in a sentence) and must not be flagged; the
    fence-boundary scan guarantees they never reach the parser.

    Two further checks (rule ACC-2 of the documentation standard, #1454) catch
    examples and names that cannot work, which the alias rule cannot see:

      1. In the same example code (docs fences and .EXAMPLE help), every
         invocation of a JIM-shaped cmdlet (Verb-JIMNoun) must name a function
         the module defines, or a command alias of one, and every parameter it
         passes must be one that cmdlet would bind: a declared parameter or
         alias, a common parameter on an advanced function, or -WhatIf and
         -Confirm where the cmdlet supports ShouldProcess. An abbreviation
         (PowerShell binds a unique prefix) and a common-parameter alias such
         as -ea are reported too, because a reader cannot tell what they bind
         to. Shipped examples piped "Get-JIMSyncRule -All" into a password
         cmdlet; there is no -All on Get-JIMSyncRule, so the example failed
         when pasted.
      2. Everywhere else in a page (prose, inline code, link text, tables,
         other-language fences), every name shaped like a JIM cmdlet must be
         one the module defines. Three "Manage it from PowerShell" bullets
         named Invoke-JIMRunProfile, Invoke-JIMSchedule and New-JIMCertificate,
         none of which has ever existed.

    Command aliases come from [Alias()] on a function and from the Set-Alias
    and New-Alias statements in the module script (-ModuleScriptPath), which is
    where JIM.psm1 declares them.

    With -OpenApiPath, a fourth check (rule ACC-6) reads what each cmdlet
    returns: it imports the module (-ModuleImportPath), and resolves each
    parameter set's [OutputType] to the OpenAPI schema, or to the properties
    the .OUTPUTS help lists for a type the module builds, through the same
    functions Update-PowerShellReference.ps1 tables the output with
    (PowerShellReference.psm1). Every property an example then reads from a
    cmdlet's output, for the parameter set the invocation binds, must be one
    it returns: through the stages of a pipeline that leave the output
    unchanged (Where-Object, Sort-Object), into the first that names or reads
    properties (Select-Object, ForEach-Object, Group-Object and the like), from
    a variable it was assigned to, or from a parenthesised call. A property
    that does not exist prints nothing and raises no error, so the reader who
    copies the example gets blank output. Only build-and-test has the OpenAPI
    document, so changelog-lint runs without it and skips this check.

.PARAMETER ModulePath
    Root directory of the module's public cmdlets, scanned recursively for
    *.ps1 files. Defaults to src/JIM.PowerShell/Public.

.PARAMETER DocsPath
    Root directory of the documentation site, scanned recursively for *.md
    files. Defaults to docs.

.PARAMETER ModuleScriptPath
    The module script whose Set-Alias and New-Alias statements declare command
    aliases. Defaults to src/JIM.PowerShell/JIM.psm1. A path that does not
    exist means no such aliases.

.PARAMETER OpenApiPath
    The OpenAPI document, as scripts/Generate-OpenApiDoc.ps1 writes it. Given,
    the ACC-6 output property check runs; omitted, it is skipped.

.PARAMETER ModuleImportPath
    The module to import for ACC-6, which needs each cmdlet's parameter sets as
    PowerShell binds them. Defaults to src/JIM.PowerShell/JIM.psd1.

.PARAMETER ConfigPath
    The output type configuration (NonApiOutputTypes, OutputTypeSchemas) that
    Update-PowerShellReference.ps1 reads too. Defaults to
    docs/.conformance/powershell-reference.psd1.

.EXAMPLE
    ./scripts/Lint-DocExamples.ps1
    Lints docs/ and the module's own comment-based help against the real repo.
#>
[CmdletBinding()]
param(
    [string]$ModulePath = "src/JIM.PowerShell/Public",
    [string]$DocsPath = "docs",
    [string]$ModuleScriptPath = "src/JIM.PowerShell/JIM.psm1",
    [string]$OpenApiPath,
    [string]$ModuleImportPath = "src/JIM.PowerShell/JIM.psd1",
    [string]$ConfigPath = "docs/.conformance/powershell-reference.psd1"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ModulePath)) {
    Write-Error "Module path not found at '$ModulePath'."
    exit 2
}

if (-not (Test-Path $DocsPath)) {
    Write-Error "Docs path not found at '$DocsPath'."
    exit 2
}

# Every public cmdlet function found while building the alias map, so the
# .EXAMPLE scan (which needs a function name and its defining file) does not
# have to re-parse the module a second time.
function Get-ModuleFunctionInventory {
    param([Parameter(Mandatory)][string]$ModulePath)

    # Both dictionaries are case-insensitive: PowerShell binds cmdlet names and
    # parameter names (and their aliases) case-insensitively, so the lookups
    # here must match that behaviour or a differently-cased alias would slip
    # through unflagged.
    $aliasMap = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $functions = [System.Collections.Generic.List[object]]::new()
    # Every parameter each cmdlet really declares, plus whether it is an advanced function (so the
    # common parameters bind) and whether it supports ShouldProcess (so -WhatIf and -Confirm bind).
    $signatures = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    # Command aliases declared with [Alias()] on a function itself: alias name -> function name.
    $commandAliases = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $files = Get-ChildItem -Path $ModulePath -Filter '*.ps1' -Recurse -File
    foreach ($file in $files) {
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)

        if ($parseErrors -and $parseErrors.Count -gt 0) {
            Write-Host "WARNING: '$($file.FullName)' failed to parse; skipping ($($parseErrors[0].Message))." -ForegroundColor Yellow
            continue
        }

        $functionAsts = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
        foreach ($funcAst in $functionAsts) {
            $functions.Add([pscustomobject]@{ Name = $funcAst.Name; Path = $file.FullName })

            $paramBlock = $funcAst.Body.ParamBlock
            $parameterNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $signature = [pscustomobject]@{
                Parameters            = $parameterNames
                IsAdvanced            = $false
                SupportsShouldProcess = $false
            }
            $signatures[$funcAst.Name] = $signature
            if (-not $paramBlock) { continue }

            foreach ($attr in $paramBlock.Attributes) {
                if ($attr.TypeName.Name -eq 'CmdletBinding') {
                    $signature.IsAdvanced = $true
                    foreach ($named in $attr.NamedArguments) {
                        if ($named.ArgumentName -ne 'SupportsShouldProcess') { continue }
                        $signature.SupportsShouldProcess = $named.ExpressionOmitted -or (Test-AttributeArgumentTrue -Expression $named.Argument)
                    }
                }
                elseif ($attr.TypeName.Name -eq 'Alias') {
                    foreach ($posArg in $attr.PositionalArguments) {
                        if ($posArg -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                            $commandAliases[$posArg.Value] = $funcAst.Name
                        }
                    }
                }
            }

            $cmdletAliases = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($param in $paramBlock.Parameters) {
                $realName = $param.Name.VariablePath.UserPath
                $parameterNames.Add($realName) | Out-Null
                # Any [Parameter()] attribute makes a function advanced, exactly as [CmdletBinding()] does.
                if ($param.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' }) { $signature.IsAdvanced = $true }

                foreach ($attr in $param.Attributes) {
                    if ($attr -isnot [System.Management.Automation.Language.AttributeAst]) { continue }
                    if ($attr.TypeName.Name -ne 'Alias') { continue }

                    foreach ($posArg in $attr.PositionalArguments) {
                        if ($posArg -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) { continue }
                        $aliasName = $posArg.Value
                        if ($aliasName -eq $realName) { continue }  # identical to the real name: nothing to enforce
                        $cmdletAliases[$aliasName] = $realName
                    }
                }
            }

            if ($cmdletAliases.Count -gt 0) {
                $aliasMap[$funcAst.Name] = $cmdletAliases
            }
        }
    }

    [pscustomobject]@{
        AliasMap       = $aliasMap
        Functions      = $functions
        Signatures     = $signatures
        CommandAliases = $commandAliases
    }
}

# True when an attribute argument such as SupportsShouldProcess = $true evaluates to true. A bare
# SupportsShouldProcess (ExpressionOmitted) is handled by the caller.
function Test-AttributeArgumentTrue {
    param($Expression)

    if ($Expression -is [System.Management.Automation.Language.VariableExpressionAst]) {
        return $Expression.VariablePath.UserPath -eq 'true'
    }
    if ($Expression -is [System.Management.Automation.Language.ConstantExpressionAst]) {
        return [bool]$Expression.Value
    }
    return $false
}

# Reads the command aliases a module script declares centrally with Set-Alias or New-Alias
# (JIM.psm1 declares every alias of the module this way, beside AliasesToExport in JIM.psd1).
# StaticParameterBinder resolves -Name and -Value whether they are passed by name or by position.
function Get-ModuleScriptCommandAlias {
    param([string]$Path)

    $aliases = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $aliases }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $Path).Path, [ref]$tokens, [ref]$parseErrors)
    $commands = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($cmd in $commands) {
        if ($cmd.GetCommandName() -notin @('Set-Alias', 'New-Alias')) { continue }
        $binding = [System.Management.Automation.Language.StaticParameterBinder]::BindCommand($cmd, $true)
        $name = $binding.BoundParameters['Name']
        $value = $binding.BoundParameters['Value']
        if ($name -and $value -and $name.ConstantValue -and $value.ConstantValue) {
            $aliases[[string]$name.ConstantValue] = [string]$value.ConstantValue
        }
    }

    return $aliases
}

# The common parameters every advanced function accepts, and the aliases PowerShell gives them.
# An alias is reported like a JIM parameter alias: valid PowerShell, but a doc example spells
# the parameter out.
$script:CommonParameters = @(
    'Verbose', 'Debug', 'ErrorAction', 'WarningAction', 'InformationAction', 'ProgressAction',
    'ErrorVariable', 'WarningVariable', 'InformationVariable', 'OutVariable', 'OutBuffer', 'PipelineVariable'
)
$script:CommonParameterAliases = @{
    vb = 'Verbose'; db = 'Debug'; ea = 'ErrorAction'; wa = 'WarningAction'; infa = 'InformationAction'
    proga = 'ProgressAction'; ev = 'ErrorVariable'; wv = 'WarningVariable'; iv = 'InformationVariable'
    ov = 'OutVariable'; ob = 'OutBuffer'; pv = 'PipelineVariable'
}

# Matches a name shaped like a JIM cmdlet (Verb-JIMNoun). Used both to decide which invocations in
# example code to check, and to find cmdlet names written anywhere else in a page.
$script:JimCmdletNamePattern = '^[A-Z][a-zA-Z]*-JIM[A-Z][A-Za-z0-9]*$'
$script:JimCmdletNameInTextPattern = '(?<![\w-])[A-Z][a-zA-Z]*-JIM[A-Z][A-Za-z0-9]*(?![\w-])'

# Resolves an invoked name to the function that runs: the function itself, or the target of a
# command alias. Returns $null when the module defines no such command.
function Resolve-JimCommand {
    param([string]$Name, $Inventory, $ModuleScriptAliases)

    if ($Inventory.Signatures.ContainsKey($Name)) { return $Name }
    if ($Inventory.CommandAliases.ContainsKey($Name)) { return $Inventory.CommandAliases[$Name] }
    if ($ModuleScriptAliases.ContainsKey($Name)) { return $ModuleScriptAliases[$Name] }
    return $null
}

# Parses a snippet of PowerShell and returns every invocation of a JIM-shaped cmdlet that the
# module does not define, and every parameter that no such cmdlet would bind: undeclared, an
# abbreviation (PowerShell binds a unique prefix, but a reader cannot tell -Connected from a
# different parameter), a common-parameter alias, or -WhatIf/-Confirm on a cmdlet without
# ShouldProcess. Declared parameter aliases are left to Find-ParameterAliasViolation.
function Find-UnknownCommandOrParameter {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Code,
        [Parameter(Mandatory)][int]$StartLine,
        [Parameter(Mandatory)]$Inventory,
        [Parameter(Mandatory)]$ModuleScriptAliases
    )

    $violations = [System.Collections.Generic.List[object]]::new()
    if ([string]::IsNullOrWhiteSpace($Code)) { return $violations }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Code, [ref]$tokens, [ref]$parseErrors)
    if (-not $ast) { return $violations }

    $commandAsts = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($cmd in $commandAsts) {
        $cmdName = $cmd.GetCommandName()
        if (-not $cmdName -or $cmdName -cnotmatch $script:JimCmdletNamePattern) { continue }

        $line = $StartLine + $cmd.Extent.StartLineNumber - 1
        $target = Resolve-JimCommand -Name $cmdName -Inventory $Inventory -ModuleScriptAliases $ModuleScriptAliases
        if (-not $target) {
            $violations.Add([pscustomobject]@{ Line = $line; Message = "there is no JIM cmdlet named $cmdName." })
            continue
        }
        if (-not $Inventory.Signatures.ContainsKey($target)) { continue }  # an alias to a function outside the module path

        $signature = $Inventory.Signatures[$target]
        $paramAliases = if ($Inventory.AliasMap.ContainsKey($target)) { $Inventory.AliasMap[$target] } else { @{} }

        $bindable = [System.Collections.Generic.List[string]]::new()
        foreach ($p in $signature.Parameters) { $bindable.Add($p) }
        if ($signature.IsAdvanced) { foreach ($p in $script:CommonParameters) { $bindable.Add($p) } }
        if ($signature.SupportsShouldProcess) { $bindable.Add('WhatIf'); $bindable.Add('Confirm') }

        foreach ($el in $cmd.CommandElements) {
            if ($el -isnot [System.Management.Automation.Language.CommandParameterAst]) { continue }
            $paramName = $el.ParameterName
            $paramLine = $StartLine + $el.Extent.StartLineNumber - 1

            if ($bindable -contains $paramName) { continue }
            if ($paramAliases.ContainsKey($paramName)) { continue }

            if ($signature.IsAdvanced -and $script:CommonParameterAliases.ContainsKey($paramName)) {
                $violations.Add([pscustomobject]@{ Line = $paramLine; Message = "$cmdName is passed -$paramName, an alias of -$($script:CommonParameterAliases[$paramName]); spell the parameter out." })
                continue
            }

            $prefixMatches = @($bindable | Where-Object { $_.StartsWith($paramName, [System.StringComparison]::OrdinalIgnoreCase) })
            if ($prefixMatches.Count -eq 1) {
                $violations.Add([pscustomobject]@{ Line = $paramLine; Message = "$cmdName is passed -$paramName, which abbreviates -$($prefixMatches[0]); spell the parameter out." })
                continue
            }

            $violations.Add([pscustomobject]@{ Line = $paramLine; Message = "-$paramName is not a parameter of $cmdName." })
        }
    }

    return $violations
}

# Returns every name shaped like a JIM cmdlet, anywhere in a Markdown file except inside
# ```powershell fences (whose invocations Find-UnknownCommandOrParameter checks properly), that the
# module does not define. Prose, inline code, link text, tables and other-language fences all
# count: a cmdlet named in a "Manage it from PowerShell" bullet is a claim about the module just as
# much as one in an example, and a stale name there sends the reader to a command that does not exist.
function Find-UnknownCmdletName {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Inventory,
        [Parameter(Mandatory)]$ModuleScriptAliases
    )

    $violations = [System.Collections.Generic.List[object]]::new()
    $lines = Get-Content -Path $Path
    $inPowerShellFence = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (-not $inPowerShellFence -and $line -match '^\s*```powershell\b') { $inPowerShellFence = $true; continue }
        if ($inPowerShellFence) {
            if ($line -match '^\s*```\s*$') { $inPowerShellFence = $false }
            continue
        }

        $names = [regex]::Matches($line, $script:JimCmdletNameInTextPattern) | ForEach-Object { $_.Value } | Select-Object -Unique
        foreach ($name in $names) {
            if (Resolve-JimCommand -Name $name -Inventory $Inventory -ModuleScriptAliases $ModuleScriptAliases) { continue }
            $violations.Add([pscustomobject]@{ Line = $i + 1; Message = "there is no JIM cmdlet named $name." })
        }
    }

    return $violations
}

# Parses a snippet of PowerShell (already isolated from any surrounding prose
# or markdown) and returns every cmdlet invocation that binds a parameter via
# one of its declared aliases rather than the real name.
function Find-ParameterAliasViolation {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Code,
        [Parameter(Mandatory)][int]$StartLine,
        [Parameter(Mandatory)]$AliasMap
    )

    $violations = [System.Collections.Generic.List[object]]::new()
    if ([string]::IsNullOrWhiteSpace($Code)) { return $violations }

    $tokens = $null
    $parseErrors = $null
    # ParseInput recovers a best-effort AST even for syntax-diagram text such
    # as "Get-JIMRunProfile -ConnectedSystemId <int>" (not valid PowerShell on
    # its own); the command name and any parameters that appear before the
    # invalid token are still resolved, so those blocks are covered too.
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Code, [ref]$tokens, [ref]$parseErrors)
    if (-not $ast) { return $violations }

    $commandAsts = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($cmd in $commandAsts) {
        $cmdName = $cmd.GetCommandName()
        if (-not $cmdName) { continue }
        if (-not $AliasMap.ContainsKey($cmdName)) { continue }
        $cmdletAliases = $AliasMap[$cmdName]

        foreach ($el in $cmd.CommandElements) {
            if ($el -isnot [System.Management.Automation.Language.CommandParameterAst]) { continue }
            $paramName = $el.ParameterName
            if (-not $cmdletAliases.ContainsKey($paramName)) { continue }

            $violations.Add([pscustomobject]@{
                Line      = $StartLine + $el.Extent.StartLineNumber - 1
                Cmdlet    = $cmdName
                AliasUsed = $paramName
                RealName  = $cmdletAliases[$paramName]
            })
        }
    }

    return $violations
}

# Extracts every ```powershell fenced code block from a Markdown file, along
# with the 1-based line number of the block's first line of code, so
# violations can be reported at their real source location. Everything
# outside a ```powershell fence (prose, parameter tables, other-language
# fences) is never returned, which is what keeps table rows like
# "Alias: `Id`." out of scope.
function Get-FencedPowerShellBlock {
    param([Parameter(Mandatory)][string]$Path)

    $lines = Get-Content -Path $Path
    $blocks = [System.Collections.Generic.List[object]]::new()

    $inBlock = $false
    $blockStartLine = 0
    $blockLines = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if (-not $inBlock) {
            if ($line -match '^\s*```powershell\b') {
                $inBlock = $true
                $blockStartLine = $i + 2   # content begins on the line after the opening fence
                $blockLines.Clear()
            }
            continue
        }

        if ($line -match '^\s*```\s*$') {
            $inBlock = $false
            if ($blockLines.Count -gt 0) {
                $blocks.Add([pscustomobject]@{
                    StartLine = $blockStartLine
                    Code      = ($blockLines -join "`n")
                })
            }
            continue
        }

        $blockLines.Add($line)
    }

    return $blocks
}

# Extracts the .EXAMPLE code blocks of a cmdlet's comment-based help via
# Get-Help, the same engine that renders them for a user running
# `Get-Help <cmdlet> -Examples`, rather than re-parsing the comment text by
# hand. Get-Help separates the example's code from its prose "remarks" for us,
# so a description sentence can never be mistaken for PowerShell. The cmdlet
# is dot-sourced inside a child scriptblock so its function definition never
# leaks into this script's own scope or collides with another file's.
function Get-ModuleExampleBlock {
    param(
        [Parameter(Mandatory)][string]$FunctionName,
        [Parameter(Mandatory)][string]$FilePath
    )

    $blocks = [System.Collections.Generic.List[object]]::new()

    $examples = $null
    try {
        $examples = & {
            . $FilePath | Out-Null
            (Get-Help -Name $FunctionName -Full -ErrorAction Stop).Examples.Example
        }
    } catch {
        Write-Host "WARNING: could not read comment-based help for '$FunctionName' in '$FilePath' ($($_.Exception.Message))." -ForegroundColor Yellow
        return $blocks
    }

    if (-not $examples) { return $blocks }

    $fileLines = Get-Content -Path $FilePath
    $searchFrom = 0

    foreach ($example in $examples) {
        $code = $example.Code
        if ([string]::IsNullOrWhiteSpace($code)) { continue }

        $firstLine = ($code -split "`r?`n")[0].Trim()

        $foundIndex = -1
        for ($i = $searchFrom; $i -lt $fileLines.Count; $i++) {
            if ($fileLines[$i].Trim() -eq $firstLine) {
                $foundIndex = $i
                break
            }
        }

        if ($foundIndex -lt 0) {
            # Should not happen for well-formed comment help; degrade to
            # reporting at the top of the file rather than losing the example.
            Write-Host "WARNING: could not locate source line for an example of '$FunctionName' in '$FilePath'; reporting at line 1." -ForegroundColor Yellow
            $foundIndex = 0
        } else {
            $searchFrom = $foundIndex + 1
        }

        $blocks.Add([pscustomobject]@{
            StartLine = $foundIndex + 1
            Code      = $code
        })
    }

    return $blocks
}


# --- ACC-6: every property an example reads is one its cmdlet returns ---------------------------------
#
# What each JIM cmdlet returns in each parameter set, from the module, its [OutputType] declarations and the
# OpenAPI document, through the same functions Update-PowerShellReference.ps1 tables the output with. Built
# only when -OpenApiPath is given, since only build-and-test has the document.
function Get-OutputModel {
    param([Parameter(Mandatory)][string]$ModuleImportPath, [Parameter(Mandatory)][string]$OpenApiPath, [string]$ConfigPath)

    Import-Module (Join-Path $PSScriptRoot 'PowerShellReference.psm1') -Force
    $config = Import-OutputTypeConfiguration -Path $ConfigPath
    $schemas = Import-OpenApiSchemas -Path $OpenApiPath
    $module = Import-Module -Name (Resolve-Path -LiteralPath $ModuleImportPath).Path -Force -PassThru -DisableNameChecking

    $model = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($command in $module.ExportedFunctions.Values) {
        $reference = Get-CmdletReference -Command $command
        $propertiesBySet = Get-OutputPropertyNamesBySet -Reference $reference -Schemas $schemas -NonApiOutputTypes $config.NonApiOutputTypes -OutputTypeSchemas $config.OutputTypeSchemas
        # Each parameter name and alias, mapped to its real name, and the sets it belongs to.
        $realName = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($parameter in $command.Parameters.Values) {
            $realName[$parameter.Name] = $parameter.Name
            foreach ($alias in $parameter.Aliases) { $realName[$alias] = $parameter.Name }
        }
        $sets = @(foreach ($set in $command.ParameterSets) {
                [pscustomobject]@{ Name = $set.Name; Parameters = @($set.Parameters | ForEach-Object Name) }
            })
        $model[$command.Name] = [pscustomobject]@{
            Name            = $command.Name
            Sets            = $sets
            DefaultSet      = $command.DefaultParameterSet
            RealName        = $realName
            PropertiesBySet = $propertiesBySet
        }
    }
    $model
}

# The properties an invocation of a JIM cmdlet returns: the union over every parameter set its named
# parameters allow, or $null when any of those sets returns something no schema describes (an int, or a type
# that declares nothing), since nothing can then be said about what is read from it. With no parameters it
# binds the default set, unless it is piped into, when the input can choose another.
function Get-InvocationProperties {
    param([Parameter(Mandatory)][System.Management.Automation.Language.CommandAst]$Command, [Parameter(Mandatory)]$Entry, [switch]$PipedInto)

    $given = [System.Collections.Generic.List[string]]::new()
    $positional = $false
    $elements = @($Command.CommandElements)
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $element = $elements[$i]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
            if ($Entry.RealName.ContainsKey($element.ParameterName)) { $given.Add($Entry.RealName[$element.ParameterName]) }
        }
        elseif ($i -gt 1 -and $elements[$i - 1] -is [System.Management.Automation.Language.CommandParameterAst] -and -not $elements[$i - 1].Argument) { continue }
        else { $positional = $true }
    }
    $common = [System.Management.Automation.Cmdlet]::CommonParameters + [System.Management.Automation.Cmdlet]::OptionalCommonParameters
    $named = @($given | Where-Object { $common -notcontains $_ } | Select-Object -Unique)

    $candidates = @($Entry.Sets | Where-Object { $set = $_; @($named | Where-Object { $set.Parameters -notcontains $_ }).Count -eq 0 })
    if ($named.Count -eq 0 -and -not $positional -and -not $PipedInto -and $Entry.DefaultSet) {
        $default = @($candidates | Where-Object Name -eq $Entry.DefaultSet)
        if ($default.Count -gt 0) { $candidates = $default }
    }
    if ($candidates.Count -eq 0) { return $null }

    $union = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($set in $candidates) {
        $names = $Entry.PropertiesBySet[$set.Name]
        if ($null -eq $names) { return $null }
        foreach ($name in $names) { [void]$union.Add($name) }
    }
    , $union
}

# Members every object has, which reading from a cmdlet's output is never wrong to do.
$script:IntrinsicMembers = @('Count', 'Length', 'PSObject', 'PSTypeNames', 'PSBase', 'PSAdapted', 'PSExtended')

# The stages a cmdlet's output can flow through: the properties each names, and whether the output leaves it
# unchanged. Where-Object and Sort-Object pass it on; Select-Object does too when it only takes a slice (-First,
# -Last, -Skip, -Unique), and reshapes it otherwise; the rest consume it.
$script:PropertyStages = @{
    'Where-Object' = 'Where-Object'; 'where' = 'Where-Object'; '?' = 'Where-Object'
    'Sort-Object' = 'Sort-Object'; 'sort' = 'Sort-Object'
    'Select-Object' = 'Select-Object'; 'select' = 'Select-Object'
    'ForEach-Object' = 'ForEach-Object'; 'foreach' = 'ForEach-Object'; '%' = 'ForEach-Object'
    'Group-Object' = 'Group-Object'; 'group' = 'Group-Object'
    'Measure-Object' = 'Measure-Object'; 'measure' = 'Measure-Object'
    'Format-Table' = 'Format-Table'; 'ft' = 'Format-Table'
    'Format-List' = 'Format-List'; 'fl' = 'Format-List'
}

# The property names a stage's arguments give: -Property (or its first positional argument), and for
# Select-Object -ExpandProperty, for ForEach-Object -MemberName (or a bare first argument), and for
# Where-Object's simple syntax the property it compares. Wildcards and calculated properties are left out;
# a calculated property's script block is checked as a script block.
function Get-StagePropertyArgument {
    param([Parameter(Mandatory)][System.Management.Automation.Language.CommandAst]$Command, [Parameter(Mandatory)][string]$Stage)

    $metadata = (Get-Command -Name $Stage).Parameters
    $byName = @{}
    $positional = [System.Collections.Generic.List[object]]::new()
    $elements = @($Command.CommandElements)
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $element = $elements[$i]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
            $matched = @($metadata.Keys | Where-Object { $_ -eq $element.ParameterName })
            if ($matched.Count -eq 0) { $matched = @($metadata.Keys | Where-Object { $_.StartsWith($element.ParameterName, [System.StringComparison]::OrdinalIgnoreCase) }) }
            $name = if ($matched.Count -eq 1) { $matched[0] } else { $element.ParameterName }
            $isSwitch = $metadata.ContainsKey($name) -and $metadata[$name].SwitchParameter
            if ($element.Argument) { $byName[$name] = $element.Argument }
            elseif (-not $isSwitch -and $i + 1 -lt $elements.Count) { $byName[$name] = $elements[$i + 1]; $i++ }
            else { $byName[$name] = $null }
        }
        else { $positional.Add($element) }
    }

    $values = [System.Collections.Generic.List[object]]::new()
    switch ($Stage) {
        'Where-Object' {
            if ($byName.ContainsKey('FilterScript') -or @($positional | Where-Object { $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }).Count -gt 0) { break }
            if ($byName.ContainsKey('Property')) { $values.Add($byName['Property']) } elseif ($positional.Count -gt 0) { $values.Add($positional[0]) }
        }
        'ForEach-Object' {
            if ($byName.ContainsKey('MemberName')) { $values.Add($byName['MemberName']) }
            elseif ($positional.Count -gt 0 -and $positional[0] -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $values.Add($positional[0]) }
        }
        default {
            if ($byName.ContainsKey('Property')) { $values.Add($byName['Property']) } elseif ($positional.Count -gt 0) { $values.Add($positional[0]) }
            if ($Stage -eq 'Select-Object' -and $byName.ContainsKey('ExpandProperty')) { $values.Add($byName['ExpandProperty']) }
        }
    }

    $names = [System.Collections.Generic.List[object]]::new()
    foreach ($value in $values) {
        $items = if ($value -is [System.Management.Automation.Language.ArrayLiteralAst]) { $value.Elements } else { @($value) }
        foreach ($item in $items) {
            if ($item -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $item.Value -notmatch '[*?\[]') { $names.Add($item) }
        }
    }
    , $names
}

# Whether a stage passes its input on unchanged, so a later stage reads the same objects.
function Test-PassThroughStage {
    param([Parameter(Mandatory)][System.Management.Automation.Language.CommandAst]$Command, [Parameter(Mandatory)][string]$Stage)

    if ($Stage -in 'Where-Object', 'Sort-Object') { return $true }
    if ($Stage -ne 'Select-Object') { return $false }
    @(Get-StagePropertyArgument -Command $Command -Stage $Stage).Count -eq 0 -and
        @($Command.CommandElements | Select-Object -Skip 1 | Where-Object { $_ -is [System.Management.Automation.Language.HashtableAst] }).Count -eq 0
}

# The $_ (or $PSItem) member reads inside a stage that refer to the stage's own input: those in a script block
# given directly to it, not in a script block nested inside that one, a switch or a catch, which bind $_ afresh.
function Get-InputMemberRead {
    param([Parameter(Mandatory)][System.Management.Automation.Language.Ast]$Scope)

    $reads = $Scope.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.MemberExpressionAst] -and
                $node -isnot [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                $node.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Expression.VariablePath.UserPath -in '_', 'PSItem'
        }, $true)
    foreach ($read in $reads) {
        $blocks = 0
        $rebound = $false
        for ($parent = $read.Parent; $parent -and $parent -ne $Scope; $parent = $parent.Parent) {
            if ($parent -is [System.Management.Automation.Language.ScriptBlockAst]) { $blocks++ }
            if ($parent -is [System.Management.Automation.Language.SwitchStatementAst] -or $parent -is [System.Management.Automation.Language.CatchClauseAst]) { $rebound = $true }
        }
        if ($blocks -eq 1 -and -not $rebound) { $read }
    }
}

# Every member read from a variable, other than through a method call.
function Get-VariableMemberRead {
    param([Parameter(Mandatory)][System.Management.Automation.Language.Ast]$Ast, [Parameter(Mandatory)][string]$Name)

    $Ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.MemberExpressionAst] -and
                $node -isnot [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                $node.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Expression.VariablePath.UserPath -eq $Name
        }, $true)
}

# Parses a snippet of PowerShell and returns every property it reads from a JIM cmdlet's output that the
# cmdlet does not return: through the stages of a pipeline that starts with it, from a variable its output
# was assigned to (and the loop variable of a foreach over that variable), and from a parenthesised call.
function Find-UnknownOutputProperty {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Code,
        [Parameter(Mandatory)][int]$StartLine,
        [Parameter(Mandatory)]$OutputModel,
        [Parameter(Mandatory)]$Inventory,
        [Parameter(Mandatory)]$ModuleScriptAliases
    )

    $violations = [System.Collections.Generic.List[object]]::new()
    if ([string]::IsNullOrWhiteSpace($Code)) { return $violations }
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Code, [ref]$tokens, [ref]$parseErrors)
    if (-not $ast) { return $violations }

    $reported = [System.Collections.Generic.HashSet[string]]::new()
    $report = {
        param($node, [string]$property, $source)
        if ($script:IntrinsicMembers -contains $property -or $source.Properties.Contains($property)) { return }
        $line = $StartLine + $node.Extent.StartLineNumber - 1
        if (-not $reported.Add("${line}:${property}")) { return }
        $known = @($source.Properties | Sort-Object)
        $list = if ($known.Count -eq 0) { 'none' } elseif ($known.Count -eq 1) { $known[0] } else { ($known[0..($known.Count - 2)] -join ', ') + ' and ' + $known[-1] }
        $violations.Add([pscustomobject]@{ Line = $line; Message = "$($source.Cmdlet) returns no property $property (ACC-6); here it returns $list." })
    }

    # The properties a JIM command returns, or $null.
    $sourceOf = {
        param([System.Management.Automation.Language.CommandAst]$command, [bool]$pipedInto = $false)
        $name = $command.GetCommandName()
        if (-not $name -or $name -cnotmatch $script:JimCmdletNamePattern) { return $null }
        $target = Resolve-JimCommand -Name $name -Inventory $Inventory -ModuleScriptAliases $ModuleScriptAliases
        if (-not $target -or -not $OutputModel.ContainsKey($target)) { return $null }
        $properties = Get-InvocationProperties -Command $command -Entry $OutputModel[$target] -PipedInto:$pipedInto
        if ($null -eq $properties) { return $null }
        [pscustomobject]@{ Cmdlet = $OutputModel[$target].Name; Properties = $properties }
    }

    # Checks the stages after index $from in a pipeline against $source, as far as the output reaches.
    $checkStages = {
        param($elements, [int]$from, $source)
        for ($i = $from; $i -lt $elements.Count; $i++) {
            $stageAst = $elements[$i]
            if ($stageAst -isnot [System.Management.Automation.Language.CommandAst]) { return }
            $stageName = $stageAst.GetCommandName()
            if (-not $stageName -or -not $script:PropertyStages.ContainsKey($stageName)) { return }
            $stage = $script:PropertyStages[$stageName]
            foreach ($argument in (Get-StagePropertyArgument -Command $stageAst -Stage $stage)) { & $report $argument $argument.Value $source }
            foreach ($read in @(Get-InputMemberRead -Scope $stageAst)) { & $report $read $read.Member.Value $source }
            if (-not (Test-PassThroughStage -Command $stageAst -Stage $stage)) { return }
        }
    }

    # A pipeline's output, if it is a JIM cmdlet's passed only through stages that leave it unchanged.
    $pipelineSource = {
        param($pipeline)
        if ($pipeline -isnot [System.Management.Automation.Language.PipelineAst]) { return $null }
        $elements = @($pipeline.PipelineElements)
        if ($elements.Count -eq 0 -or $elements[0] -isnot [System.Management.Automation.Language.CommandAst]) { return $null }
        $source = & $sourceOf $elements[0]
        if (-not $source) { return $null }
        for ($i = 1; $i -lt $elements.Count; $i++) {
            $stageName = $elements[$i].GetCommandName()
            if (-not $stageName -or -not $script:PropertyStages.ContainsKey($stageName)) { return $null }
            if (-not (Test-PassThroughStage -Command $elements[$i] -Stage $script:PropertyStages[$stageName])) { return $null }
        }
        $source
    }

    # Every JIM cmdlet in a pipeline, and the stages after it.
    foreach ($pipeline in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.PipelineAst] }, $true)) {
        $elements = @($pipeline.PipelineElements)
        for ($i = 0; $i -lt $elements.Count - 1; $i++) {
            if ($elements[$i] -isnot [System.Management.Automation.Language.CommandAst]) { continue }
            $source = & $sourceOf $elements[$i] ($i -gt 0)
            if ($source) { & $checkStages $elements ($i + 1) $source }
        }
    }

    # A parenthesised call read directly: (Get-JIMWidget -Id 1).Name.
    foreach ($read in $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.MemberExpressionAst] -and
                    $node -isnot [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                    $node.Expression -is [System.Management.Automation.Language.ParenExpressionAst]
            }, $true)) {
        $source = & $pipelineSource $read.Expression.Pipeline
        if ($source) { & $report $read $read.Member.Value $source }
    }

    # Variables assigned exactly once, from a JIM cmdlet's output; and foreach loops over them.
    $assignments = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
    $assignedCount = @{}
    foreach ($assignment in $assignments) {
        if ($assignment.Left -is [System.Management.Automation.Language.VariableExpressionAst]) {
            $name = $assignment.Left.VariablePath.UserPath
            $assignedCount[$name] = 1 + $(if ($assignedCount.ContainsKey($name)) { $assignedCount[$name] } else { 0 })
        }
    }
    $loops = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.ForEachStatementAst] }, $true))
    foreach ($loop in $loops) {
        $name = $loop.Variable.VariablePath.UserPath
        $assignedCount[$name] = 1 + $(if ($assignedCount.ContainsKey($name)) { $assignedCount[$name] } else { 0 })
    }

    $typed = @{}
    foreach ($assignment in $assignments) {
        if ($assignment.Left -isnot [System.Management.Automation.Language.VariableExpressionAst] -or $assignment.Operator -ne 'Equals') { continue }
        $name = $assignment.Left.VariablePath.UserPath
        if ($assignedCount[$name] -ne 1) { continue }
        $source = & $pipelineSource $assignment.Right
        if ($source) { $typed[$name] = $source }
    }
    foreach ($loop in $loops) {
        $name = $loop.Variable.VariablePath.UserPath
        $over = $loop.Condition
        if ($over -is [System.Management.Automation.Language.PipelineAst] -and @($over.PipelineElements).Count -eq 1 -and
            $over.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) { $over = $over.PipelineElements[0].Expression }
        if ($assignedCount[$name] -eq 1 -and $over -is [System.Management.Automation.Language.VariableExpressionAst] -and $typed.ContainsKey($over.VariablePath.UserPath)) {
            $typed[$name] = $typed[$over.VariablePath.UserPath]
        }
    }
    foreach ($name in $typed.Keys) {
        foreach ($read in (Get-VariableMemberRead -Ast $ast -Name $name)) { & $report $read $read.Member.Value $typed[$name] }
        # $widgets | ForEach-Object { $_.Name }
        foreach ($pipeline in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.PipelineAst] }, $true)) {
            $elements = @($pipeline.PipelineElements)
            if ($elements.Count -lt 2 -or $elements[0] -isnot [System.Management.Automation.Language.CommandExpressionAst]) { continue }
            $expression = $elements[0].Expression
            if ($expression -is [System.Management.Automation.Language.VariableExpressionAst] -and $expression.VariablePath.UserPath -eq $name) {
                & $checkStages $elements 1 $typed[$name]
            }
        }
    }

    $violations
}

# --- Build the cmdlet -> alias -> real-parameter-name map ---

$inventory = Get-ModuleFunctionInventory -ModulePath $ModulePath
$aliasMap = $inventory.AliasMap
$functions = $inventory.Functions

$moduleScriptAliases = Get-ModuleScriptCommandAlias -Path $ModuleScriptPath

Write-Host "Built alias map for $($aliasMap.Count) cmdlet(s) with at least one aliased parameter, from $($functions.Count) function(s) under '$ModulePath'."
Write-Host "Read $($moduleScriptAliases.Count + $inventory.CommandAliases.Count) command alias(es) from '$ModuleScriptPath' and the functions' [Alias()] attributes."

$errors = [System.Collections.Generic.List[string]]::new()

$outputModel = $null
if ($OpenApiPath) {
    $outputModel = Get-OutputModel -ModuleImportPath $ModuleImportPath -OpenApiPath $OpenApiPath -ConfigPath $ConfigPath
    Write-Host "Read what $($outputModel.Count) cmdlet(s) return, for ACC-6, from '$ModuleImportPath' and '$OpenApiPath'."
}
else {
    Write-Host "ACC-6 skipped: no -OpenApiPath, so the properties each cmdlet returns are unknown here. build-and-test runs it."
}

# --- Scan every ```powershell fenced code block under docs/ ---

$docFiles = Get-ChildItem -Path $DocsPath -Filter '*.md' -Recurse -File
$docBlockCount = 0
foreach ($docFile in $docFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath((Get-Location).Path, $docFile.FullName)
    $blocks = Get-FencedPowerShellBlock -Path $docFile.FullName
    foreach ($block in $blocks) {
        $docBlockCount++
        $violations = Find-ParameterAliasViolation -Code $block.Code -StartLine $block.StartLine -AliasMap $aliasMap
        foreach ($v in $violations) {
            $errors.Add("${relativePath}:$($v.Line)  $($v.Cmdlet) invoked via alias -$($v.AliasUsed); use the real parameter name -$($v.RealName) instead.")
        }
        foreach ($v in (Find-UnknownCommandOrParameter -Code $block.Code -StartLine $block.StartLine -Inventory $inventory -ModuleScriptAliases $moduleScriptAliases)) {
            $errors.Add("${relativePath}:$($v.Line)  $($v.Message)")
        }
        if ($outputModel) {
            foreach ($v in (Find-UnknownOutputProperty -Code $block.Code -StartLine $block.StartLine -OutputModel $outputModel -Inventory $inventory -ModuleScriptAliases $moduleScriptAliases)) {
                $errors.Add("${relativePath}:$($v.Line)  $($v.Message)")
            }
        }
    }
    foreach ($v in (Find-UnknownCmdletName -Path $docFile.FullName -Inventory $inventory -ModuleScriptAliases $moduleScriptAliases)) {
        $errors.Add("${relativePath}:$($v.Line)  $($v.Message)")
    }
}

Write-Host "Scanned $docBlockCount ``````powershell code block(s) across $($docFiles.Count) file(s) under '$DocsPath'."

# --- Scan the module's own comment-based help .EXAMPLE blocks ---

$exampleBlockCount = 0
foreach ($func in $functions) {
    $relativePath = [System.IO.Path]::GetRelativePath((Get-Location).Path, $func.Path)
    $blocks = Get-ModuleExampleBlock -FunctionName $func.Name -FilePath $func.Path
    foreach ($block in $blocks) {
        $exampleBlockCount++
        $violations = Find-ParameterAliasViolation -Code $block.Code -StartLine $block.StartLine -AliasMap $aliasMap
        foreach ($v in $violations) {
            $errors.Add("${relativePath}:$($v.Line)  $($v.Cmdlet) invoked via alias -$($v.AliasUsed); use the real parameter name -$($v.RealName) instead.")
        }
        foreach ($v in (Find-UnknownCommandOrParameter -Code $block.Code -StartLine $block.StartLine -Inventory $inventory -ModuleScriptAliases $moduleScriptAliases)) {
            $errors.Add("${relativePath}:$($v.Line)  $($v.Message)")
        }
        if ($outputModel) {
            foreach ($v in (Find-UnknownOutputProperty -Code $block.Code -StartLine $block.StartLine -OutputModel $outputModel -Inventory $inventory -ModuleScriptAliases $moduleScriptAliases)) {
                $errors.Add("${relativePath}:$($v.Line)  $($v.Message)")
            }
        }
    }
}

Write-Host "Scanned $exampleBlockCount .EXAMPLE block(s) across $($functions.Count) cmdlet(s) under '$ModulePath'."

foreach ($e in $errors) { Write-Host "ERROR:   $e" -ForegroundColor Red }

if ($errors.Count -gt 0) {
    Write-Host "`nDoc examples lint FAILED ($($errors.Count) violation(s))." -ForegroundColor Red
    exit 1
}

$readsChecked = if ($outputModel) { ', and reads only properties its cmdlet returns' } else { '' }
Write-Host "`nDoc examples lint passed; every JIM cmdlet named exists, and every example binds its parameters by their real names$readsChecked." -ForegroundColor Green
exit 0
