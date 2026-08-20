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

    WHAT COUNTS AS COMPLIANT

    The two lines must appear consecutively, in order, and at the canonical
    position for the file type: line 1, except that a shebang and #Requires
    directives come first in scripts, and a Razor @directive block comes first
    in components. Nothing but blank lines may sit between the last of those
    and the notice; how many blank lines is not policed.

    Position is enforced, not merely presence, because a notice can be present
    and still be wrong. Twelve JIM.Web components had the pair injected between
    an @if condition and its opening brace, separating the two - the file
    carried the notice, and no presence check would have noticed.

    The text must match exactly. A near-miss is a violation: the wording is the
    licence grant, not a decorative comment. JIM.psd1's manifest Copyright key
    used to read '(c) Tetron Limited. All rights reserved.' - the kind of drift
    this catches, since it omitted 'Copyright' and said nothing about the
    licence.

    WHAT IS OUT OF SCOPE

    Tool-generated code is skipped, because a header added by hand there is
    silently destroyed the next time the tool runs. That means any Migrations/
    directory (EF Core rewrites the whole folder, including the model
    snapshot, on every 'dotnet ef migrations add') and *.Designer.cs. Build
    output (bin/, obj/, node_modules/ and friends) is skipped for the obvious
    reason. These exclusions are mirrored in .editorconfig, which enforces the
    same rule for .cs via IDE0073; keep the two in step.

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

    [string[]]$ExcludeFilePattern = @('*.Designer.cs', '*.g.cs', '*.generated.cs', '*.AssemblyInfo.cs')
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

# Index of the first of the two header lines, or -1 when the pair is absent.
function Get-HeaderIndex {
    param(
        [string[]]$Lines,
        [string[]]$Header
    )

    for ($i = 0; $i -lt $Lines.Count - 1; $i++) {
        if (([string]$Lines[$i]).Trim() -eq $Header[0] -and ([string]$Lines[$i + 1]).Trim() -eq $Header[1]) {
            return $i
        }
    }

    return -1
}

# 'Missing' (no notice at all), 'Misplaced' (present, but not where the rule
# puts it) or 'Ok'. The two are worth distinguishing: one is a licensing
# omission, the other is tidying.
function Get-HeaderStatus {
    param(
        [string[]]$Lines,
        [hashtable]$Style
    )

    $header = Get-HeaderLine -Style $Style
    $at = Get-HeaderIndex -Lines $Lines -Header $header
    if ($at -lt 0) { return 'Missing' }

    # What must precede the notice - a shebang, #Requires, or a Razor directive
    # block - measured on the file with the notice taken out, so its own lines
    # cannot terminate the scan.
    $stripped = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($i -eq $at -or $i -eq $at + 1) { continue }
        $stripped.Add([string]$Lines[$i])
    }

    $required = Get-HeaderInsertIndex -Lines $stripped.ToArray() -Kind $Style.Kind
    if ($at -lt $required) { return 'Misplaced' }

    # Everything between the last required line and the notice must be blank.
    # Blank-line counts are not policed; only that nothing of substance has got
    # in front of the notice.
    for ($i = $required; $i -lt $at; $i++) {
        if (([string]$Lines[$i]).Trim().Length -gt 0) { return 'Misplaced' }
    }

    return 'Ok'
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

        # Same tidy-up at the top of the file, where there is no preceding line
        # to absorb: a notice on line 1 is followed by its blank, and leaving it
        # behind would open the file with an empty line.
        if ($kept.Count -eq 0 -and $i + 1 -lt $Lines.Count -and ([string]$Lines[$i + 1]).Trim().Length -eq 0) {
            $i++
        }
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

        $status = Get-HeaderStatus -Lines $lines -Style $style
        if ($status -eq 'Ok') { continue }

        if ($Fix) {
            Add-CopyrightHeader -FilePath $file.FullName -Lines $lines -Style $style
            $fixed.Add($relative)
        }
        else {
            $reason = if ($status -eq 'Missing') {
                'is missing the Tetron copyright header.'
            }
            else {
                'has the Tetron copyright header, but not in the canonical position for its file type.'
            }
            $violations.Add("$relative  $reason")
        }
    }
}

Write-Host "Scanned $scanned file(s) across $($Path.Count) root(s) for the Tetron copyright header."

if ($Fix) {
    foreach ($f in $fixed) { Write-Host "FIXED:   $f" -ForegroundColor Yellow }
    Write-Host "`nCopyright header lint: inserted the header into $($fixed.Count) file(s)." -ForegroundColor Green
    exit 0
}

foreach ($v in $violations) { Write-Host "ERROR:   $v" -ForegroundColor Red }

if ($violations.Count -gt 0) {
    Write-Host "`nCopyright header lint FAILED ($($violations.Count) file(s))." -ForegroundColor Red
    Write-Host "Run 'pwsh -File ./scripts/Lint-CopyrightHeaders.ps1 -Fix' to insert it." -ForegroundColor Red
    exit 1
}

Write-Host "`nCopyright header lint passed; every scanned file carries the Tetron copyright header." -ForegroundColor Green
exit 0
