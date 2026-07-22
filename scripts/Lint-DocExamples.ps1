# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails documentation that invokes a JIM PowerShell cmdlet via a parameter
    ALIAS instead of the parameter's real name.

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

.PARAMETER ModulePath
    Root directory of the module's public cmdlets, scanned recursively for
    *.ps1 files. Defaults to src/JIM.PowerShell/Public.

.PARAMETER DocsPath
    Root directory of the documentation site, scanned recursively for *.md
    files. Defaults to docs.

.EXAMPLE
    ./scripts/Lint-DocExamples.ps1
    Lints docs/ and the module's own comment-based help against the real repo.
#>
[CmdletBinding()]
param(
    [string]$ModulePath = "src/JIM.PowerShell/Public",
    [string]$DocsPath = "docs"
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
            if (-not $paramBlock) { continue }

            $cmdletAliases = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($param in $paramBlock.Parameters) {
                $realName = $param.Name.VariablePath.UserPath

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
        AliasMap  = $aliasMap
        Functions = $functions
    }
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

# --- Build the cmdlet -> alias -> real-parameter-name map ---

$inventory = Get-ModuleFunctionInventory -ModulePath $ModulePath
$aliasMap = $inventory.AliasMap
$functions = $inventory.Functions

Write-Host "Built alias map for $($aliasMap.Count) cmdlet(s) with at least one aliased parameter, from $($functions.Count) function(s) under '$ModulePath'."

$errors = [System.Collections.Generic.List[string]]::new()

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
    }
}

Write-Host "Scanned $exampleBlockCount .EXAMPLE block(s) across $($functions.Count) cmdlet(s) under '$ModulePath'."

foreach ($e in $errors) { Write-Host "ERROR:   $e" -ForegroundColor Red }

if ($errors.Count -gt 0) {
    Write-Host "`nDoc examples lint FAILED ($($errors.Count) violation(s))." -ForegroundColor Red
    exit 1
}

Write-Host "`nDoc examples lint passed; no cmdlet was invoked via a parameter alias in example code." -ForegroundColor Green
exit 0
