# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails a PR that adds a source file without the Tetron copyright header.

.DESCRIPTION
    The mechanical backstop for the "Copyright Headers (MANDATORY on all new
    files)" rule in src/CLAUDE.md. Every source file must carry the two-line
    Tetron notice in its leading preamble, spelled with the comment syntax of
    its own language.

    Before this check existed, only .cs was enforced: .editorconfig sets
    file_header_template and IDE0073 under a [*.cs] section, so every other
    extension in the table was convention-only. Convention drifted, and by the
    time anyone looked, ten files had no notice at all - JIM.psm1 and JIM.psd1
    (never covered by the rule, as the table listed neither .psm1 nor .psd1),
    three test/integration scripts, two .razor files, two test .cs files and
    the session-start hook.

    WHAT COUNTS AS PRESENT

    The two lines must appear consecutively, in order, and within the file's
    leading preamble: the run of lines from the start of the file that are
    blank, comments, or language directives, ending at the first line of real
    content. That definition is deliberate rather than a fixed line number,
    because JIM.Web carries two established Razor styles - App.razor puts the
    notice on lines 1-2, while AdminIndex.razor and 76 others put it after the
    @page/@inject directive block, as deep as line 21. Both place the notice
    ahead of any content, which is the point of a copyright header; neither is
    worth 77 files of churn to normalise.

    The text must match exactly. A near-miss is a violation: the wording is the
    licence grant, not a decorative comment. JIM.psd1's manifest Copyright key
    ('(c) Tetron Limited. All rights reserved.') is an example of the drift
    this catches - it omits 'Copyright' and says nothing about the licence.

    WHAT IS OUT OF SCOPE

    Tool-generated code is skipped, because a header added by hand there is
    silently destroyed the next time the tool runs. That means any Migrations/
    directory (EF Core rewrites the whole folder, including the model
    snapshot, on every 'dotnet ef migrations add') and *.Designer.cs. Build
    output (bin/, obj/, node_modules/ and friends) is skipped for the obvious
    reason. _Imports.razor is skipped because src/CLAUDE.md carves it out by
    name.

.PARAMETER Path
    One or more roots to scan, recursively. Defaults to the repository root.

.PARAMETER Fix
    Insert the missing header into every offending file instead of just
    reporting it, following the placement rules in src/CLAUDE.md: line 1 for
    .cs, below a shebang or #Requires for scripts, and below the leading
    @directive block for .razor. A notice found outside the preamble is moved
    rather than duplicated. Files that already comply are left byte-for-byte
    alone, BOM and line endings included.

.PARAMETER ExcludeDirectory
    Directory names excluded wherever they appear in the tree.

.PARAMETER ExcludeFilePattern
    Wildcard patterns matched against the file name; matches are excluded.

.EXAMPLE
    pwsh -File ./scripts/Lint-CopyrightHeaders.ps1

    Scans the repository and exits 1 if any source file is missing its header.

.EXAMPLE
    pwsh -File ./scripts/Lint-CopyrightHeaders.ps1 -Fix

    Inserts the header into every offending file.

.NOTES
    Exit codes: 0 - every scanned file carries the header (or -Fix repaired
    them all); 1 - at least one file is missing it.
#>

[CmdletBinding()]
param(
    [string[]]$Path,

    [switch]$Fix,

    [string[]]$ExcludeDirectory = @('bin', 'obj', 'node_modules', 'packages', 'TestResults', 'Migrations', '.git', '.vs', '.vscode'),

    [string[]]$ExcludeFilePattern = @('*.Designer.cs', '*.g.cs', '*.generated.cs', '*.AssemblyInfo.cs', '_Imports.razor')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- The mandated notice, and how each language spells a comment ---

$CopyrightText = 'Copyright (c) Tetron Limited. All rights reserved.'
$LicenceText = 'Licensed under the Tetron Commercial License. See LICENSE file in the project root.'

# Keep in step with the copyright-header table in src/CLAUDE.md.
$CommentStyles = @{
    '.cs'    = @{ Prefix = '// ';  Suffix = '';    Kind = 'Slash' }
    '.razor' = @{ Prefix = '@* ';  Suffix = ' *@'; Kind = 'Razor' }
    '.ps1'   = @{ Prefix = '# ';   Suffix = '';    Kind = 'Hash'  }
    '.psm1'  = @{ Prefix = '# ';   Suffix = '';    Kind = 'Hash'  }
    '.psd1'  = @{ Prefix = '# ';   Suffix = '';    Kind = 'Hash'  }
    '.sh'    = @{ Prefix = '# ';   Suffix = '';    Kind = 'Hash'  }
}

# Razor directives that may legitimately precede the notice. @code and @{ are
# absent on purpose: they are content, and they close the preamble.
$RazorDirectives = @(
    'page', 'using', 'inject', 'attribute', 'implements', 'inherits', 'layout',
    'namespace', 'typeparam', 'rendermode', 'preservewhitespace',
    'addTagHelper', 'removeTagHelper', 'tagHelperPrefix'
)

function Get-HeaderLine {
    param([hashtable]$Style)

    return @(
        "$($Style.Prefix)$CopyrightText$($Style.Suffix)",
        "$($Style.Prefix)$LicenceText$($Style.Suffix)"
    )
}

# Returns the number of leading lines that are blank, comments or language
# directives - i.e. everything before the file's first line of real content.
# The header must live inside this run.
function Get-PreambleLineCount {
    param(
        [string[]]$Lines,
        [string]$Kind
    )

    $inBlockComment = $false
    $index = 0

    foreach ($line in $Lines) {
        $trimmed = $line.Trim()

        if ($inBlockComment) {
            $index++
            $closer = if ($Kind -eq 'Slash') { '*/' } elseif ($Kind -eq 'Razor') { '*@' } else { '#>' }
            if ($trimmed.Contains($closer)) { $inBlockComment = $false }
            continue
        }

        if ($trimmed.Length -eq 0) { $index++; continue }

        $isPreamble = $false
        $opensBlock = $false

        switch ($Kind) {
            'Hash' {
                # Covers '#!' shebangs, '#Requires' directives and ordinary comments.
                if ($trimmed.StartsWith('#')) { $isPreamble = $true }
                elseif ($trimmed.StartsWith('<#')) { $isPreamble = $true; $opensBlock = -not $trimmed.Contains('#>') }
            }
            'Slash' {
                # '#' covers preprocessor directives: #nullable, #pragma, #region.
                if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('#')) { $isPreamble = $true }
                elseif ($trimmed.StartsWith('/*')) { $isPreamble = $true; $opensBlock = -not $trimmed.Contains('*/') }
            }
            'Razor' {
                if ($trimmed.StartsWith('@*')) { $isPreamble = $true; $opensBlock = -not $trimmed.Contains('*@') }
                elseif ($trimmed.StartsWith('@')) {
                    $word = ($trimmed.Substring(1) -split '[^A-Za-z]', 2)[0]
                    if ($RazorDirectives -contains $word) { $isPreamble = $true }
                }
            }
        }

        if (-not $isPreamble) { break }

        $index++
        if ($opensBlock) { $inBlockComment = $true }
    }

    return $index
}

function Test-HasCopyrightHeader {
    param(
        [string[]]$Lines,
        [hashtable]$Style
    )

    $expected = Get-HeaderLine -Style $Style
    $preamble = Get-PreambleLineCount -Lines $Lines -Kind $Style.Kind

    # Both lines must sit inside the preamble, so the last viable start is
    # preamble - 2.
    for ($i = 0; $i -lt $preamble - 1; $i++) {
        if ($Lines[$i].Trim() -eq $expected[0] -and $Lines[$i + 1].Trim() -eq $expected[1]) {
            return $true
        }
    }

    return $false
}

# Where the header belongs, per the placement rules in src/CLAUDE.md.
#
# The default is line 1. Two languages move it down: a shebang must stay on
# line 1 for the kernel to find it (and #Requires reads as part of a script's
# contract rather than its body), and Razor puts the notice after the leading
# @directive block, which is both the documented rule and the dominant style
# across JIM.Web.
function Get-HeaderInsertIndex {
    param(
        [string[]]$Lines,
        [string]$Kind
    )

    $index = 0

    if ($Kind -eq 'Razor') {
        $lastDirective = -1
        for ($i = 0; $i -lt $Lines.Count; $i++) {
            $trimmed = ([string]$Lines[$i]).Trim()
            if ($trimmed.Length -eq 0) { continue }
            if (-not $trimmed.StartsWith('@')) { break }

            $word = ($trimmed.Substring(1) -split '[^A-Za-z]', 2)[0]
            if ($RazorDirectives -notcontains $word) { break }
            $lastDirective = $i
        }

        return $lastDirective + 1
    }

    foreach ($line in $Lines) {
        $trimmed = $line.Trim()
        if (($index -eq 0 -and $trimmed.StartsWith('#!')) -or $trimmed -match '^#Requires\b') {
            $index++
            continue
        }
        break
    }

    return $index
}

# A file can carry the notice and still fail, when the notice sits below the
# preamble. Twelve JIM.Web components were in exactly that state: the pair had
# been injected between an @if condition and its opening brace, separating the
# two. Strip the stray pair before inserting at the top, so -Fix relocates the
# notice instead of leaving the file with two of them.
function Remove-MisplacedHeader {
    param(
        [string[]]$Lines,
        [string[]]$Header
    )

    $kept = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $isPair = $i + 1 -lt $Lines.Count -and
                  ([string]$Lines[$i]).Trim() -eq $Header[0] -and
                  ([string]$Lines[$i + 1]).Trim() -eq $Header[1]

        if (-not $isPair) { $kept.Add([string]$Lines[$i]); continue }

        # Removing the pair would otherwise strand the blank line that was
        # inserted with it, leaving '@if (cond)' separated from its brace by a
        # gap that was never in the original source.
        if ($kept.Count -gt 0 -and $kept[$kept.Count - 1].Trim().Length -eq 0) {
            $kept.RemoveAt($kept.Count - 1)
        }

        $i++
    }

    return $kept.ToArray()
}

function Add-CopyrightHeader {
    param(
        [string]$FilePath,
        [string[]]$Lines,
        [hashtable]$Style
    )

    $header = Get-HeaderLine -Style $Style
    $Lines = Remove-MisplacedHeader -Lines $Lines -Header $header

    $insertAt = Get-HeaderInsertIndex -Lines $Lines -Kind $Style.Kind

    $updated = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $insertAt; $i++) { $updated.Add([string]$Lines[$i]) }

    # A blank line between a Razor directive block and the notice, matching the
    # house style; a shebang keeps its header hard against it.
    if ($Style.Kind -eq 'Razor' -and $insertAt -gt 0) { $updated.Add('') }

    foreach ($line in $header) { $updated.Add([string]$line) }

    # A blank line after the notice, unless the body already opens with one, so
    # the header reads as a header rather than as the first body comment.
    if ($insertAt -lt $Lines.Count -and ([string]$Lines[$insertAt]).Trim().Length -gt 0) { $updated.Add('') }
    for ($i = $insertAt; $i -lt $Lines.Count; $i++) { $updated.Add([string]$Lines[$i]) }

    $existingBytes = [System.IO.File]::ReadAllBytes($FilePath)
    $newline = if ((Get-Content -LiteralPath $FilePath -Raw -ErrorAction SilentlyContinue) -match "`r`n") { "`r`n" } else { "`n" }

    # Adding a header is not a licence to re-encode the file. Five JIM.Web
    # components are stored with a BOM; keep whatever the file already had.
    $hasBom = $existingBytes.Length -ge 3 -and
              $existingBytes[0] -eq 0xEF -and $existingBytes[1] -eq 0xBB -and $existingBytes[2] -eq 0xBF

    # Splitting on the newline leaves a trailing empty element for a file that
    # already ended in one, so only add the final terminator when it is absent;
    # otherwise every -Fix run would grow the file by a blank line.
    $text = $updated -join $newline
    if (-not $text.EndsWith($newline)) { $text += $newline }

    [System.IO.File]::WriteAllText($FilePath, $text, [System.Text.UTF8Encoding]::new($hasBom))
}

# --- Scan ---

if (-not $Path -or $Path.Count -eq 0) {
    $Path = @((Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
}

$violations = [System.Collections.Generic.List[string]]::new()
$fixed = [System.Collections.Generic.List[string]]::new()
$scanned = 0

foreach ($root in $Path) {
    $rootPath = (Resolve-Path -LiteralPath $root).Path

    # -Force so dot-directories are scanned: .claude/hooks/session-start.sh is
    # source like any other, and was missed for precisely this reason.
    $files = Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $CommentStyles.ContainsKey($_.Extension.ToLowerInvariant()) }

    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')

        $segments = $relative -split '/'
        if ($segments.Length -gt 1) {
            $directories = $segments[0..($segments.Length - 2)]
            if ($directories | Where-Object { $ExcludeDirectory -contains $_ }) { continue }
        }

        if ($ExcludeFilePattern | Where-Object { $file.Name -like $_ }) { continue }

        $scanned++
        $style = $CommentStyles[$file.Extension.ToLowerInvariant()]

        # -Raw then split, so a zero-length file yields no lines rather than
        # tripping Get-Content's empty-file behaviour.
        $raw = Get-Content -LiteralPath $file.FullName -Raw
        $lines = if ([string]::IsNullOrEmpty($raw)) { @() } else { $raw -split "`r?`n" }

        if (Test-HasCopyrightHeader -Lines $lines -Style $style) { continue }

        if ($Fix) {
            Add-CopyrightHeader -FilePath $file.FullName -Lines $lines -Style $style
            $fixed.Add($relative)
        }
        else {
            $violations.Add($relative)
        }
    }
}

Write-Host "Scanned $scanned file(s) across $($Path.Count) root(s) for the Tetron copyright header."

if ($Fix) {
    foreach ($f in $fixed) { Write-Host "FIXED:   $f" -ForegroundColor Yellow }
    Write-Host "`nCopyright header lint: inserted the header into $($fixed.Count) file(s)." -ForegroundColor Green
    exit 0
}

foreach ($v in $violations) { Write-Host "ERROR:   $v  is missing the Tetron copyright header." -ForegroundColor Red }

if ($violations.Count -gt 0) {
    Write-Host "`nCopyright header lint FAILED ($($violations.Count) file(s) missing the header)." -ForegroundColor Red
    Write-Host "Run 'pwsh -File ./scripts/Lint-CopyrightHeaders.ps1 -Fix' to insert it." -ForegroundColor Red
    exit 1
}

Write-Host "`nCopyright header lint passed; every scanned file carries the Tetron copyright header." -ForegroundColor Green
exit 0
