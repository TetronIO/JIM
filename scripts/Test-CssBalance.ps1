# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails when a CSS file contains a rule whose closing brace is missing.

.DESCRIPTION
    A CSS parser that meets an unclosed block keeps treating everything after it as declarations of
    that block until it finds a closing brace, so one missing '}' silently kills every rule below it
    for the rest of the file. Nothing else in the pipeline notices: CSS is not compiled, so the build
    stays clean; bUnit applies no stylesheet, so component tests pass; and the page still renders,
    just unstyled.

    This shipped twice in site.css. '.jim-run-metric-value' and '.jim-password-account-control' were
    both left unclosed, between them killing roughly 300 lines of rules, including the Set Password
    dialog's progress rail and the navigation count chips. The rail had already been reported once as
    rendering with no styling; classes were added in response and it still did not style, because the
    unclosed rule above them meant the new rules never applied either.

    Braces inside comments and inside quoted strings (a legitimate "content: '{'") are ignored, so
    only structural braces are counted.

.PARAMETER Path
    Directories or files to check. Defaults to the portal's stylesheets.

.EXAMPLE
    pwsh -File ./scripts/Test-CssBalance.ps1
    Checks every stylesheet under src/JIM.Web/wwwroot/css.

.EXAMPLE
    pwsh -File ./scripts/Test-CssBalance.ps1 -Path docs/assets/stylesheets
    Checks the documentation site's stylesheets instead.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @('src/JIM.Web/wwwroot/css')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    Returns the source with comments and quoted strings blanked out, preserving line structure.
.DESCRIPTION
    Blanking rather than removing keeps every newline in place, so a reported line number still
    points at the line the author sees in their editor.
#>
function Remove-CssNoise {
    param([string]$Content)

    $out = [System.Text.StringBuilder]::new($Content.Length)
    $i = 0
    $length = $Content.Length

    while ($i -lt $length) {
        $ch = $Content[$i]

        # Comment: consume to its terminator, keeping newlines.
        if ($ch -eq '/' -and $i + 1 -lt $length -and $Content[$i + 1] -eq '*') {
            $end = $Content.IndexOf('*/', $i + 2)
            if ($end -lt 0) { $end = $length } else { $end += 2 }
            for ($j = $i; $j -lt $end; $j++) {
                [void]$out.Append($(if ($Content[$j] -eq "`n") { "`n" } else { ' ' }))
            }
            $i = $end
            continue
        }

        # Quoted string: consume to the matching quote, honouring backslash escapes.
        if ($ch -eq '"' -or $ch -eq "'") {
            $quote = $ch
            [void]$out.Append(' ')
            $i++
            while ($i -lt $length) {
                if ($Content[$i] -eq '\' -and $i + 1 -lt $length) {
                    [void]$out.Append('  ')
                    $i += 2
                    continue
                }
                if ($Content[$i] -eq $quote) { [void]$out.Append(' '); $i++; break }
                [void]$out.Append($(if ($Content[$i] -eq "`n") { "`n" } else { ' ' }))
                $i++
            }
            continue
        }

        [void]$out.Append($ch)
        $i++
    }

    return $out.ToString()
}

<#
.SYNOPSIS
    Returns the 1-based line numbers of any rules in a stylesheet that are never closed.
#>
function Get-UnclosedCssRule {
    param([string]$Content)

    $stripped = Remove-CssNoise -Content $Content
    $openLines = [System.Collections.Generic.Stack[int]]::new()
    $line = 1
    $extraClosers = @()

    foreach ($ch in $stripped.ToCharArray()) {
        switch ($ch) {
            "`n" { $line++ }
            '{'  { $openLines.Push($line) }
            '}'  {
                if ($openLines.Count -gt 0) { [void]$openLines.Pop() }
                else { $extraClosers += $line }
            }
        }
    }

    return [pscustomobject]@{
        Unclosed      = @($openLines.ToArray() | Sort-Object)
        ExtraClosers  = @($extraClosers)
    }
}

$files = foreach ($item in $Path) {
    if (Test-Path -Path $item -PathType Container) {
        Get-ChildItem -Path $item -Filter '*.css' -Recurse -File
    }
    elseif (Test-Path -Path $item) {
        Get-Item -Path $item
    }
    else {
        Write-Error "Path not found: $item"
    }
}

$failed = $false

foreach ($file in $files) {
    $relative = Resolve-Path -Path $file.FullName -Relative
    $result = Get-UnclosedCssRule -Content (Get-Content -Path $file.FullName -Raw)

    foreach ($lineNumber in $result.Unclosed) {
        $selector = (Get-Content -Path $file.FullName)[$lineNumber - 1].Trim()
        Write-Host "::error file=$relative,line=$lineNumber::Unclosed CSS rule: $selector. Every rule below this line in the file is swallowed by it and never applies."
        $failed = $true
    }

    foreach ($lineNumber in $result.ExtraClosers) {
        Write-Host "::error file=$relative,line=$lineNumber::Unmatched closing brace."
        $failed = $true
    }
}

$checked = @($files).Count
if ($failed) {
    Write-Host ""
    Write-Host "CSS brace check FAILED across $checked file(s)."
    exit 1
}

Write-Host "CSS brace check passed: $checked file(s), all rules closed."
exit 0
