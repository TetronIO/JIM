# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks every public documentation page against the mechanical rules of the
    documentation standard, failing on new defects while letting existing debt
    only shrink.

.DESCRIPTION
    The conformance check for engineering/DOCUMENTATION_STANDARD.md (#1454). It
    reads the Markdown sources under docs/ (never the built site, so it keeps
    working whichever generator builds the site; see #2058) and applies the
    rules the standard marks as checked by this script:

      STR-1   front matter is valid: it parses, uses only known keys, and its
              values are allowed (a page type, an audience, a description of
              the right length)
      STR-2   front matter declares title, description, type and audience
      STR-3   one name for the page: the title, the H1 and the nav label agree
      STR-4   exactly one H1, and no heading skips a level
      STR-5   every code block declares a language
      STR-6   no stub or placeholder pages
      STR-7   the page opens with a paragraph, directly under the H1
      STR-8   the page is within its size budget
      MNT-3   a page over its size budget on the base branch does not grow
      A11Y-1  every Mermaid diagram carries accTitle and accDescr
      A11Y-2  every image has alt text; every included diagram has a <title>
              and a <desc>
      TYP-1   a page that declares a type has the shape that type requires
      TUT-2   every bold label in a tutorial's Portal tab exists in the portal
      PRI-1   a conformant priority page records the release it was last
              checked against (validated: vX.Y.Z), no later than VERSION, and
              no other page records one
      LANG-1  no em dash, and no spaced -- standing in for one
      LANG-2  British English, not American spellings
      LANG-3  product nouns in Title Case, and never a rejected variant
      LANG-4  abbreviations such as CSO and MVO only in table headers
      LANG-5  no wording relative to when it was written ("until now")
      LANG-6  no promises about the future
      LANG-7  no minimisers ("simply")
      LANG-8  no GitHub issue or pull request numbers
      TERM-1  the vocabulary and the glossary agree, and every product noun
              has a home page
      EXC-1   every exception marker is well formed, names a rule, gives a
              reason, and suppresses something

    The language rules read running text only: front matter, code blocks,
    inline code, link targets, URLs, HTML and placeholder text (STR-6's) are
    left out. LANG-3 and TERM-1 read the product vocabulary: the glossary page
    gives each noun its canonical form and anchor, and the vocabulary file adds
    its home page, its rejected variants and whether Title Case is enforced.

    Each rule has a tier, set in the configuration file:

      Block    every finding fails the check.
      Ratchet  findings are compared with the baseline file, per rule and page.
               More than the baseline allows fails; fewer also fails until the
               improvement is locked in with -UpdateBaseline, so the baseline
               only ever shrinks. A page absent from the baseline has an
               allowance of zero, so every new page is conformant from its
               first commit.
      Warn     findings are reported, never fail.

    A page in a conformant folder, or listed as a conformant page, carries no
    baseline: its Ratchet findings fail outright. A conformant priority page
    also fails on warnings, and has the smaller priority size budget.

    Exceptions live in the page, where the next author reads them, and must
    give a reason:

      <!-- conformance: allow STR-5 "terminal output, not code" -->
          suppresses the named rule(s) on the marker's line and the next
          non-blank line
      <!-- conformance: allow-page STR-2 "typed in the folder migration" -->
          suppresses the named rule(s) for the whole page

    No marker can excuse EXC-1, or MNT-3: a marker for growth would suppress
    nothing once the growth merged, and fail every later pull request.

    In GitHub Actions, errors and listed warnings are also written as
    annotations on the pull request diff, and a per-rule table is appended to
    the job summary. Warnings are listed only for pages the pull request
    changed (CHANGED_FILES), or for every page with -ShowWarnings.

    Exit codes: 0 conformant, 1 a rule failed, 2 the configuration or baseline
    could not be read.

.PARAMETER DocsPath
    The documentation source folder. Defaults to docs.

.PARAMETER ConfigPath
    The configuration file: rule tiers, page types, audiences, budgets,
    priority and conformant pages. Defaults to docs/.conformance/config.psd1.

.PARAMETER BaselinePath
    The shrink-only baseline: one "rule <TAB> page <TAB> count" line per entry.
    Defaults to docs/.conformance/baseline.tsv.

.PARAMETER MkDocsConfigPath
    mkdocs.yml, read for the nav labels STR-3 compares. Defaults to mkdocs.yml.

.PARAMETER VocabularyPath
    The product vocabulary LANG-3, LANG-4 and TERM-1 read: per glossary entry,
    its home page, rejected variants and whether Title Case is enforced, plus
    the abbreviations and their full terms. Defaults to
    docs/.conformance/vocabulary.psd1.

.PARAMETER RepositoryRoot
    The repository root, used to find tutorial scenario scripts, to read a
    page's base-branch version for MNT-3, and to read VERSION for PRI-1.
    Defaults to the current directory.

.PARAMETER BaseRef
    The commit MNT-3 compares page sizes against. Defaults to the BASE_SHA
    environment variable (set by changelog-lint); failing that, the merge base
    of HEAD and origin/main. With neither, MNT-3 is skipped and says so.

.PARAMETER ChangedFiles
    Repository-relative paths the pull request changed, whose warnings are
    listed. Defaults to the newline-separated CHANGED_FILES environment variable.

.PARAMETER ShowWarnings
    List every warning, not only those on changed pages.

.PARAMETER UpdateBaseline
    Lock in improvements: lower each baseline entry to the current count and
    remove entries that no longer apply. Never adds an entry or raises one.

.PARAMETER SeedBaseline
    Add baseline entries for -Rule, a Ratchet rule that has none yet. Used once,
    when a rule is introduced over existing debt; refuses a rule that already
    has entries.

.PARAMETER Rule
    The rule -SeedBaseline seeds.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsConformance.ps1
    Checks every page; lists errors, and counts warnings.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsConformance.ps1 -UpdateBaseline
    After fixing debt on a page, lowers its baseline entries to match.
#>
[CmdletBinding()]
param(
    [string]$DocsPath = 'docs',
    [string]$ConfigPath = 'docs/.conformance/config.psd1',
    [string]$BaselinePath = 'docs/.conformance/baseline.tsv',
    [string]$MkDocsConfigPath = 'mkdocs.yml',
    [string]$VocabularyPath = 'docs/.conformance/vocabulary.psd1',
    [string]$RepositoryRoot = '.',
    [string]$BaseRef = $env:BASE_SHA,
    [string[]]$ChangedFiles,
    [switch]$ShowWarnings,
    [switch]$UpdateBaseline,
    [switch]$SeedBaseline,
    [string]$Rule
)

$ErrorActionPreference = 'Stop'

$script:KnownRules = @('STR-1', 'STR-2', 'STR-3', 'STR-4', 'STR-5', 'STR-6', 'STR-7', 'STR-8', 'MNT-3', 'A11Y-1', 'A11Y-2', 'TYP-1', 'TUT-2',
    'PRI-1', 'LANG-1', 'LANG-2', 'LANG-3', 'LANG-4', 'LANG-5', 'LANG-6', 'LANG-7', 'LANG-8', 'TERM-1', 'STY-1', 'STY-3', 'EXC-1')
# Rules no marker can excuse. EXC-1 guards the markers themselves. MNT-3 compares with the base
# branch, so once the growth it excused merges, its marker suppresses nothing and fails every
# later pull request. PRI-1 is what /release reads to find the pages due for re-validation; a
# priority page excused from recording one would simply never be listed.
$script:UnmarkableRules = @('EXC-1', 'MNT-3', 'PRI-1')
$script:Tiers = @('Block', 'Ratchet', 'Warn')
$script:StandardPath = 'engineering/DOCUMENTATION_STANDARD.md'
# An icon or emoji shortcode such as :material-format-align-center: or :octicons-arrow-right-24:, which
# the language rules and the word count skip: its name is markup, not prose.
$script:IconShortcode = ':[a-z0-9]+(?:-[a-z0-9]+)+:'
# STY-1. Three kinds of emoji: the pictograph blocks (Misc Symbols, Dingbats, and the
# supplementary pictographs as UTF-16 pairs); the scattered characters that default to emoji
# presentation (a play button, a return arrow, an information sign); and any character a
# variation selector (U+FE0F) asks to draw as emoji. Typographic symbols such as an arrow,
# a trademark or a copyright sign pass unless that selector follows them.
# scripts/Test-DocsSite.ps1 scans the built site with the same pattern; keep the two in step.
$script:EmojiRegex = [regex]::new('(?:(?:[\u2600-\u27BF\u2B05-\u2B07\u2B1B\u2B1C\u2B50\u2B55\u231A\u231B\u23E9-\u23FA\u2139\u24C2\u25AA\u25AB\u25B6\u25C0\u25FB-\u25FE\u21A9\u21AA\u2934\u2935\u3030\u303D\u3297\u3299\u203C\u2049]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDEFF])\uFE0F?|[^\s\uFE0F]\uFE0F)')

# ---------------------------------------------------------------------------
# Configuration, baseline and nav
# ---------------------------------------------------------------------------

function Read-Configuration {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "Configuration file not found at '$Path'." }
    $config = Import-PowerShellDataFile -LiteralPath $Path

    foreach ($key in 'Exclude', 'Rules', 'RequiredFrontMatter', 'AllowedFrontMatter', 'Audiences', 'PageTypes', 'DescriptionLength',
        'SizeBudgetWords', 'PrioritySizeBudgetWords', 'StubPatterns', 'PriorityPages', 'ConformantFolders', 'ConformantPages', 'ScenarioPathFormat',
        'PortalLabelSources', 'GlossaryPage', 'AmericanSpellings', 'IzeExceptions', 'AllowedSpellings', 'TimeRelativePatterns', 'FuturePatterns', 'MinimiserPatterns') {
        if (-not $config.ContainsKey($key)) { throw "Configuration '$Path' has no '$key'." }
    }
    foreach ($r in $script:KnownRules | Where-Object { $_ -ne 'EXC-1' }) {
        if (-not $config.Rules.ContainsKey($r)) { throw "Configuration '$Path' gives no tier for $r." }
        if ($config.Rules[$r] -notin $script:Tiers) { throw "Configuration '$Path' gives $r the tier '$($config.Rules[$r])'; use Block, Ratchet or Warn." }
    }
    foreach ($r in $config.Rules.Keys) {
        if ($r -notin $script:KnownRules) { throw "Configuration '$Path' gives a tier to '$r', which is not a rule this script checks." }
    }
    # EXC-1 guards the exception mechanism itself, so it can be neither relaxed nor suppressed.
    $config.Rules['EXC-1'] = 'Block'
    return $config
}

function Read-Baseline {
    param([string]$Path)

    # Key: "rule`tpage". A union merge can leave two lines for one key; the lower count wins,
    # since it is the stricter of the two and the baseline only ever shrinks.
    $entries = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Path)) { return $entries }

    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Path).Path)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $fields = $line.Split("`t")
        $count = 0
        if ($fields.Count -ne 3 -or $fields[0] -notin $script:KnownRules -or -not [int]::TryParse($fields[2], [ref]$count) -or $count -lt 1) {
            throw "Baseline '$Path' line ${lineNumber} is not 'rule<TAB>page<TAB>count' with a known rule and a count of at least 1: '$line'."
        }
        $key = "$($fields[0])`t$($fields[1])"
        if (-not $entries.ContainsKey($key) -or $count -lt $entries[$key]) { $entries[$key] = $count }
    }
    return $entries
}

function Write-Baseline {
    param([string]$Path, $Entries)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Docs conformance baseline: the existing debt each Ratchet rule allows, per page. Counts only ever fall.')
    $lines.Add('# Lock in an improvement with: pwsh ./scripts/Test-DocsConformance.ps1 -UpdateBaseline')
    $lines.Add('# The rules are defined in engineering/DOCUMENTATION_STANDARD.md. Columns: rule <TAB> page (relative to docs/) <TAB> count.')
    # Ordinal sort, so the file is byte-identical whichever platform or culture writes it.
    $keys = [string[]]@($Entries.Keys)
    [System.Array]::Sort($keys, [System.StringComparer]::Ordinal)
    foreach ($key in $keys) {
        $lines.Add("$key`t$($Entries[$key])")
    }
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
}

function Read-Vocabulary {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "Vocabulary file not found at '$Path'." }
    $data = Import-PowerShellDataFile -LiteralPath $Path
    foreach ($key in 'Abbreviations', 'Terms') {
        if (-not $data.ContainsKey($key)) { throw "Vocabulary '$Path' has no '$key'." }
    }
    $fileLines = [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Path).Path)
    $terms = foreach ($entry in @($data.Terms)) {
        if (-not $entry) { continue }
        foreach ($key in 'Anchor', 'Home', 'TitleCase') {
            if (-not $entry.ContainsKey($key)) { throw "Vocabulary '$Path' has an entry with no '$key': every entry names its glossary anchor, its home page and whether Title Case is enforced." }
        }
        # The entry's line, so a finding points at it.
        $line = 1
        $anchorPattern = "'?Anchor'?\s*=\s*'$([regex]::Escape($entry.Anchor))'"
        for ($i = 0; $i -lt $fileLines.Count; $i++) {
            if ($fileLines[$i] -match $anchorPattern) { $line = $i + 1; break }
        }
        [pscustomobject]@{
            Anchor    = [string]$entry.Anchor
            Home      = [string]$entry.Home
            TitleCase = [bool]$entry.TitleCase
            Rejected  = @($entry.Rejected | Where-Object { $_ })
            Line      = $line
        }
    }
    [pscustomobject]@{ Terms = @($terms); Abbreviations = $data.Abbreviations }
}

function Get-NavLabel {
    param([string]$Path)

    # Page -> nav label, for entries of the form "- Label: path.md". A bare "- path.md" (a
    # section index) has no label of its own.
    $labels = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $labels }
    $inNav = $false
    foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Path).Path)) {
        if ($line -match '^nav:\s*$') { $inNav = $true; continue }
        if (-not $inNav) { continue }
        if ($line -match '^\S' -and $line -notmatch '^-') { break }
        if ($line -match '^\s*-\s+(?:"(?<label>[^"]+)"|''(?<label>[^'']+)''|(?<label>[^:"''][^:]*?))\s*:\s+(?<page>\S+\.md)\s*$') {
            $labels[$Matches['page']] = $Matches['label'].Trim()
        }
    }
    return $labels
}

function ConvertTo-GlobRegex {
    param([string]$Glob)

    $pattern = [System.Text.StringBuilder]::new('^')
    for ($i = 0; $i -lt $Glob.Length; $i++) {
        $c = $Glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') { [void]$pattern.Append('.*'); $i++ }
        elseif ($c -eq '*') { [void]$pattern.Append('[^/]*') }
        elseif ($c -eq '?') { [void]$pattern.Append('[^/]') }
        else { [void]$pattern.Append([regex]::Escape([string]$c)) }
    }
    [void]$pattern.Append('$')
    return $pattern.ToString()
}

# ---------------------------------------------------------------------------
# Page model
# ---------------------------------------------------------------------------

# Parses one front matter scalar. Returns @{ Value } or @{ Error }. Plain (unquoted) scalars are
# held to the YAML rules MkDocs enforces silently: a value containing ": " or " #", or starting
# with an indicator character, is not a plain string, and MkDocs drops the whole block without a
# warning when it meets one.
function ConvertFrom-YamlScalar {
    param([string]$Text)

    if ($Text.StartsWith('"')) {
        if ($Text.Length -lt 2 -or -not $Text.EndsWith('"')) { return @{ Error = 'the double-quoted value is not closed' } }
        $inner = $Text.Substring(1, $Text.Length - 2)
        if ($inner -match '(?<!\\)"') { return @{ Error = 'the double-quoted value contains an unescaped double quote; write it as \"' } }
        return @{ Value = ($inner -replace '\\"', '"' -replace '\\\\', '\') }
    }
    if ($Text.StartsWith("'")) {
        if ($Text.Length -lt 2 -or -not $Text.EndsWith("'")) { return @{ Error = 'the single-quoted value is not closed' } }
        $inner = $Text.Substring(1, $Text.Length - 2)
        if (($inner -replace "''", '') -match "'") { return @{ Error = "the single-quoted value contains a single quote; double it ('')" } }
        return @{ Value = ($inner -replace "''", "'") }
    }
    if ($Text[0] -in [char[]]'[{&*!|>%@`' -or $Text.StartsWith('- ') -or $Text.StartsWith('? ')) {
        return @{ Error = "a value starting with '$($Text[0])' must be quoted; wrap it in double quotes" }
    }
    if ($Text.Contains(': ') -or $Text.EndsWith(':')) {
        return @{ Error = "a value containing ': ' must be quoted; wrap it in double quotes, or MkDocs drops the whole front matter without a warning" }
    }
    if ($Text.Contains(' #')) {
        return @{ Error = "a value containing ' #' must be quoted; wrap it in double quotes" }
    }
    return @{ Value = $Text }
}

function Read-FrontMatter {
    param([string[]]$Lines)

    $result = [pscustomobject]@{
        Present   = $false
        Values    = [ordered]@{}
        KeyLines  = @{}
        Errors    = [System.Collections.Generic.List[object]]::new()
        BodyStart = 0
    }
    if ($Lines.Count -eq 0 -or $Lines[0] -ne '---') { return $result }

    $closing = -1
    for ($i = 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -eq '---') { $closing = $i; break }
    }
    $result.Present = $true
    if ($closing -lt 0) {
        $result.Errors.Add([pscustomobject]@{ Line = 1; Message = "the front matter has no closing '---' line" })
        return $result
    }
    $result.BodyStart = $closing + 1

    $listKey = $null
    for ($i = 1; $i -lt $closing; $i++) {
        $text = $Lines[$i]
        $lineNumber = $i + 1
        if ([string]::IsNullOrWhiteSpace($text) -or $text.TrimStart().StartsWith('#')) { continue }

        if ($text -match '^\s+-\s+(?<item>.+)$' -and $listKey) {
            $parsed = ConvertFrom-YamlScalar -Text $Matches['item'].Trim()
            if ($parsed.ContainsKey('Error')) { $result.Errors.Add([pscustomobject]@{ Line = $lineNumber; Message = $parsed.Error }) }
            else { $result.Values[$listKey] += , $parsed.Value }
            continue
        }

        if ($text -match '^(?<key>[A-Za-z_][A-Za-z0-9_-]*):(?<rest>.*)$') {
            $key = $Matches['key']
            $rest = $Matches['rest']
            if ($rest -ne '' -and -not $rest.StartsWith(' ')) {
                $result.Errors.Add([pscustomobject]@{ Line = $lineNumber; Message = "put a space after the colon in '${key}:'" })
                continue
            }
            if ($result.Values.Contains($key)) {
                $result.Errors.Add([pscustomobject]@{ Line = $lineNumber; Message = "the key '$key' appears twice" })
                continue
            }
            $result.KeyLines[$key] = $lineNumber
            $rest = $rest.Trim()
            if ($rest -eq '') {
                $result.Values[$key] = @()
                $listKey = $key
                continue
            }
            $listKey = $null
            $parsed = ConvertFrom-YamlScalar -Text $rest
            if ($parsed.ContainsKey('Error')) { $result.Errors.Add([pscustomobject]@{ Line = $lineNumber; Message = $parsed.Error }) }
            else { $result.Values[$key] = $parsed.Value }
            continue
        }

        $result.Errors.Add([pscustomobject]@{ Line = $lineNumber; Message = "the line is not a 'key: value' pair" })
    }
    return $result
}

function Get-WordCount {
    param([string[]]$Lines, [int]$Start)

    $words = 0
    $inFence = $false
    $fenceMarker = $null
    for ($i = $Start; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($line -match '^\s*(?<fence>`{3,}|~{3,})') {
            $marker = $Matches['fence']
            if (-not $inFence) { $inFence = $true; $fenceMarker = $marker; continue }
            if ($marker[0] -eq $fenceMarker[0] -and $marker.Length -ge $fenceMarker.Length -and $line.Trim() -eq $marker) { $inFence = $false; continue }
        }
        if ($inFence -or $line.TrimStart().StartsWith('<!--')) { continue }
        # An icon shortcode (:material-format-align-center:) is markup, not a word.
        $words += ([regex]::Matches(($line -replace $script:IconShortcode, ' '), '\S+')).Count
    }
    return $words
}

function Get-PageModel {
    param([string]$FullPath, [string]$RelativePath, [string]$DisplayPath)

    $text = [System.IO.File]::ReadAllText($FullPath)
    $lines = $text -split "\r?\n"
    $frontMatter = Read-FrontMatter -Lines $lines

    $headings = [System.Collections.Generic.List[object]]::new()
    $fences = [System.Collections.Generic.List[object]]::new()
    $markers = [System.Collections.Generic.List[object]]::new()
    $malformedMarkers = [System.Collections.Generic.List[object]]::new()
    $inFenceLine = [bool[]]::new($lines.Count)

    $openFence = $null
    for ($i = $frontMatter.BodyStart; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNumber = $i + 1

        if ($line -match '^(?<indent>\s*)(?<fence>`{3,}|~{3,})(?<info>.*)$') {
            $marker = $Matches['fence']
            if (-not $openFence) {
                $openFence = [pscustomobject]@{ Line = $lineNumber; Marker = $marker; Info = $Matches['info'].Trim(); Content = [System.Collections.Generic.List[string]]::new() }
                $inFenceLine[$i] = $true
                continue
            }
            if ($marker[0] -eq $openFence.Marker[0] -and $marker.Length -ge $openFence.Marker.Length -and $Matches['info'].Trim() -eq '') {
                $inFenceLine[$i] = $true
                $fences.Add($openFence)
                $openFence = $null
                continue
            }
        }
        if ($openFence) {
            $inFenceLine[$i] = $true
            $openFence.Content.Add($line)
            continue
        }

        if ($line -match '^ {0,3}(?<hashes>#{1,6})\s+(?<text>.*?)\s*$') {
            $headingLevel = $Matches['hashes'].Length
            $headingText = ($Matches['text'] -replace '\s*\{[^}]*\}\s*$', '' -replace '\s+#+\s*$', '').Trim()
            # A leading icon shortcode (STY-3) is kept apart, so every other rule reads the heading's words.
            $headingIcon = $null
            if ($headingText -match '^:(?<icon>[a-z0-9]+(?:-[a-z0-9]+)*):\s*(?<rest>.*)$') {
                $headingIcon = $Matches['icon']
                $headingText = $Matches['rest']
            }
            $headings.Add([pscustomobject]@{ Level = $headingLevel; Text = $headingText; Icon = $headingIcon; Line = $lineNumber })
        }

        if ($line -match '<!--\s*conformance:') {
            if ($line -match '<!--\s*conformance:\s*(?<kind>allow-page|allow)\s+(?<rules>[A-Za-z0-9-]+(?:\s*,\s*[A-Za-z0-9-]+)*)\s*(?:"(?<reason>[^"]*)")?\s*-->') {
                $markers.Add([pscustomobject]@{
                        Line   = $lineNumber
                        Kind   = $Matches['kind']
                        Rules  = @($Matches['rules'] -split '\s*,\s*')
                        Reason = $Matches['reason']
                        Used   = $false
                    })
            }
            else {
                $malformedMarkers.Add($lineNumber)
            }
        }
    }
    if ($openFence) { $fences.Add($openFence) }

    # The first non-blank line after each marker, so an "allow" marker can sit above what it excuses.
    foreach ($m in $markers) {
        $m | Add-Member -NotePropertyName TargetLine -NotePropertyValue $m.Line
        for ($j = $m.Line; $j -lt $lines.Count; $j++) {
            if (-not [string]::IsNullOrWhiteSpace($lines[$j])) { $m.TargetLine = $j + 1; break }
        }
    }

    [pscustomobject]@{
        RelativePath     = $RelativePath
        DisplayPath      = $DisplayPath
        Lines            = $lines
        FrontMatter      = $frontMatter
        Headings         = $headings
        Fences           = $fences
        InFenceLine      = $inFenceLine
        Markers          = $markers
        MalformedMarkers = $malformedMarkers
        IsTemplate       = $frontMatter.Values.Contains('template')
        WordCount        = (Get-WordCount -Lines $lines -Start $frontMatter.BodyStart)
    }
}

# ---------------------------------------------------------------------------
# Rules. Each returns findings: @{ Rule; Line; Message }.
# ---------------------------------------------------------------------------

function New-Finding {
    param([string]$Rule, [int]$Line, [string]$Message)
    [pscustomobject]@{ Rule = $Rule; Line = [Math]::Max(1, $Line); Message = $Message }
}

function Get-PageName {
    param($Page)
    $title = $Page.FrontMatter.Values['title']
    if ($title -is [string] -and $title) { return $title }
    $h1 = $Page.Headings | Where-Object Level -eq 1 | Select-Object -First 1
    if ($h1) { return $h1.Text }
    return $null
}

function Test-FrontMatter {
    param($Page, $Config)

    $fm = $Page.FrontMatter
    foreach ($e in $fm.Errors) { New-Finding 'STR-1' $e.Line "front matter: $($e.Message)." }

    foreach ($key in $fm.Values.Keys) {
        $line = $fm.KeyLines[$key]
        $value = $fm.Values[$key]
        if ($key -notin $Config.AllowedFrontMatter) {
            New-Finding 'STR-1' $line "front matter key '$key' is not one the standard defines ($($Config.AllowedFrontMatter -join ', '))."
            continue
        }
        if ($key -ne 'hide' -and $value -isnot [string]) {
            New-Finding 'STR-1' $line "front matter '$key' must be a single value, not a list."
            continue
        }
        switch ($key) {
            'type' {
                if ($value -notin $Config.PageTypes.Keys) {
                    New-Finding 'STR-1' $line "'$value' is not a page type ($(($Config.PageTypes.Keys | Sort-Object) -join ', '))."
                }
            }
            'audience' {
                if ($value -notin $Config.Audiences) {
                    New-Finding 'STR-1' $line "'$value' is not an audience ($($Config.Audiences -join ', '))."
                }
            }
            'description' {
                $length = $value.Length
                if ($length -lt $Config.DescriptionLength.Min -or $length -gt $Config.DescriptionLength.Max) {
                    New-Finding 'STR-1' $line "the description is $length characters; keep it between $($Config.DescriptionLength.Min) and $($Config.DescriptionLength.Max), one sentence saying what the page is for."
                }
            }
            'title' {
                if ([string]::IsNullOrWhiteSpace($value)) { New-Finding 'STR-1' $line 'the title is empty.' }
            }
        }
    }

    foreach ($key in $Config.RequiredFrontMatter) {
        $value = $fm.Values[$key]
        if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) {
            New-Finding 'STR-2' 1 "front matter has no '$key'."
        }
    }
}

function Test-PageName {
    param($Page, $NavLabels)

    if ($Page.IsTemplate) { return }
    $title = $Page.FrontMatter.Values['title']
    $h1 = $Page.Headings | Where-Object Level -eq 1 | Select-Object -First 1
    if ($title -is [string] -and $title -and $h1 -and $h1.Text -cne $title) {
        New-Finding 'STR-3' $h1.Line "the H1 '$($h1.Text)' differs from the front matter title '$title'; give the page one name."
    }
    $name = Get-PageName -Page $Page
    $label = $NavLabels[$Page.RelativePath]
    if ($label -and $name) {
        # The label may drop leading words that the nav section already says: "Authentication"
        # under API for "API Authentication", "Keycloak" under SSO Setup for "SSO Setup: Keycloak".
        $isSuffix = $name -ceq $label -or ($name.EndsWith($label, [System.StringComparison]::Ordinal) -and
            $name.Substring(0, $name.Length - $label.Length) -match '[\s:]$')
        if (-not $isSuffix) {
            $line = if ($h1) { $h1.Line } else { 1 }
            New-Finding 'STR-3' $line "the nav label '$label' differs from the page's name '$name'; use the name, or the name without its leading words."
        }
    }
}

# STY-3: every H2 opens with a Material Design icon, which custom.css draws as a tile; no other
# heading carries one. Get-PageModel separates a heading's leading icon shortcode from its words.
function Test-HeadingIcons {
    param($Page)

    foreach ($h in $Page.Headings) {
        if ($h.Level -eq 2) {
            if (-not $h.Icon) {
                New-Finding 'STY-3' $h.Line "the H2 '$($h.Text)' has no icon; every H2 opens with a Material Design icon, such as '## :material-layers-outline: $($h.Text)'."
            }
            elseif (-not $h.Icon.StartsWith('material-')) {
                New-Finding 'STY-3' $h.Line "the H2 '$($h.Text)' opens with ':$($h.Icon):'; use a Material Design icon (:material-...:), preferring an -outline variant."
            }
        }
        elseif ($h.Icon) {
            New-Finding 'STY-3' $h.Line "the heading '$($h.Text)' carries an icon; only H2 headings carry one."
        }
    }
}

function Test-HeadingStructure {
    param($Page)

    $h1s = @($Page.Headings | Where-Object Level -eq 1)
    if (-not $Page.IsTemplate -and $h1s.Count -ne 1) {
        $line = if ($h1s.Count -gt 1) { $h1s[1].Line } else { 1 }
        New-Finding 'STR-4' $line "the page has $($h1s.Count) H1 headings; it needs exactly one, its name."
    }
    $previous = 0
    foreach ($h in $Page.Headings) {
        if ($previous -gt 0 -and $h.Level -gt $previous + 1) {
            New-Finding 'STR-4' $h.Line "the heading '$($h.Text)' skips from H$previous to H$($h.Level)."
        }
        $previous = $h.Level
    }
}

function Test-CodeFences {
    param($Page)
    foreach ($f in $Page.Fences) {
        if (-not $f.Info) { New-Finding 'STR-5' $f.Line 'the code block names no language; name one (text for output or messages).' }
    }
}

function Test-Stub {
    param($Page, $Config)
    for ($i = $Page.FrontMatter.BodyStart; $i -lt $Page.Lines.Count; $i++) {
        foreach ($pattern in $Config.StubPatterns) {
            if ($Page.Lines[$i].IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                New-Finding 'STR-6' ($i + 1) "the page carries placeholder text ('$pattern'); write the content, or take the page out of the nav until it exists."
                return
            }
        }
    }
}

function Test-Opening {
    param($Page)

    if ($Page.IsTemplate) { return }
    $h1 = $Page.Headings | Where-Object Level -eq 1 | Select-Object -First 1
    if (-not $h1) { return }
    for ($i = $h1.Line; $i -lt $Page.Lines.Count; $i++) {
        $line = $Page.Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('<!--') -or $line.Trim() -match '^\{:?[^}]*\}$') { continue }
        $kind = switch -Regex ($line) {
            '^\s{0,3}#' { 'a heading'; break }
            '^\s*(!!!|\?\?\?)' { 'an admonition'; break }
            '^\s*(`{3,}|~{3,})' { 'a code block'; break }
            '^\s*\|' { 'a table'; break }
            '^\s*--8<--' { 'an included file'; break }
            '^\s*([-*+]|\d+\.)\s' { 'a list'; break }
            '^\s*>' { 'a quotation'; break }
            '^\s*(---|\*\*\*|___)\s*$' { 'a rule'; break }
            '^\s*<(?!p[\s>]|a[\s>]|strong|em|code|span)' { 'HTML'; break }
            default { $null }
        }
        if ($kind) { New-Finding 'STR-7' $h1.Line "the page opens with $kind; open with a paragraph saying what this is and why it matters." }
        return
    }
    New-Finding 'STR-7' $h1.Line 'the page has nothing under its H1; open with a paragraph saying what this is and why it matters.'
}

function Test-SizeBudget {
    param($Page, [int]$Budget)
    if ($Page.WordCount -gt $Budget) {
        New-Finding 'STR-8' 1 ("the page is {0} words, over its {1}-word budget; split it, one sub-feature per page." -f $Page.WordCount, $Budget)
    }
}

function Test-Growth {
    param($Page, [int]$Budget, [string]$BaseRef, [string]$RepositoryRoot, [string]$DocsRepoPath)

    if (-not $BaseRef) { return }
    $gitPath = ($DocsRepoPath.TrimEnd('/') + '/' + $Page.RelativePath).TrimStart('./')
    $baseText = & git -C $RepositoryRoot show "${BaseRef}:$gitPath" 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $baseText) { return }
    $baseLines = @($baseText)
    $baseFrontMatter = Read-FrontMatter -Lines $baseLines
    $baseWords = Get-WordCount -Lines $baseLines -Start $baseFrontMatter.BodyStart
    if ($baseWords -gt $Budget -and $Page.WordCount -gt $baseWords) {
        New-Finding 'MNT-3' 1 ("the page grew from {0} to {1} words while over its {2}-word budget; split it, or rewrite what your change superseded so the page does not grow." -f $baseWords, $Page.WordCount, $Budget)
    }
}

function Test-Accessibility {
    param($Page, [string]$DocsFullPath, [string]$RepositoryRoot)

    foreach ($f in $Page.Fences | Where-Object { $_.Info -match '^mermaid\b' }) {
        $content = $f.Content -join "`n"
        $missing = @()
        if ($content -notmatch '(?m)^\s*accTitle\s*:') { $missing += 'accTitle' }
        if ($content -notmatch '(?m)^\s*accDescr\s*(:|\{)') { $missing += 'accDescr' }
        if ($missing) { New-Finding 'A11Y-1' $f.Line "the Mermaid diagram has no $($missing -join ' or '); add both, so a screen reader gets what the picture shows." }
    }

    for ($i = $Page.FrontMatter.BodyStart; $i -lt $Page.Lines.Count; $i++) {
        if ($Page.InFenceLine[$i]) { continue }
        $line = $Page.Lines[$i]
        foreach ($m in [regex]::Matches($line, '!\[(?<alt>[^\]]*)\]\(')) {
            if ([string]::IsNullOrWhiteSpace($m.Groups['alt'].Value)) { New-Finding 'A11Y-2' ($i + 1) 'the image has no alt text; say what it shows.' }
        }
        foreach ($m in [regex]::Matches($line, '<img\b[^>]*>')) {
            if ($m.Value -notmatch '\balt\s*=\s*"[^"\s][^"]*"') { New-Finding 'A11Y-2' ($i + 1) 'the image has no alt text; say what it shows.' }
        }
        if ($line -match '^\s*--8<--\s*"(?<path>[^"]+\.svg)"') {
            $relative = $Matches['path']
            $candidate = @((Join-Path $DocsFullPath $relative), (Join-Path $RepositoryRoot $relative)) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if ($candidate) {
                $svg = [System.IO.File]::ReadAllText($candidate)
                foreach ($element in 'title', 'desc') {
                    if ($svg -notmatch "<$element[\s>]") { New-Finding 'A11Y-2' ($i + 1) "the diagram '$relative' has no <$element>; give it one that carries what the picture shows." }
                }
            }
        }
    }
}

function Test-Validation {
    param($Page, [bool]$Priority, [bool]$Conformant, [string]$VersionPath)

    $fm = $Page.FrontMatter
    $line = if ($fm.KeyLines.ContainsKey('validated')) { $fm.KeyLines['validated'] } else { 1 }
    $value = $fm.Values['validated']

    if ($null -eq $value) {
        if ($Priority -and $Conformant) {
            New-Finding 'PRI-1' 1 "a conformant priority page has no 'validated'; check the page against the product, then record the release, such as validated: v0.16.0."
        }
        return
    }
    if (-not $Priority) {
        New-Finding 'PRI-1' $line "only a priority page records 'validated'; remove it, or add the page to PriorityPages in config.psd1."
        return
    }
    if ($value -isnot [string] -or $value -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$') {
        New-Finding 'PRI-1' $line "'validated' is '$value'; write the release the page was last checked against as vX.Y.Z, such as v0.16.0."
        return
    }
    $validated = [System.Management.Automation.SemanticVersion]::new($Matches.version)
    if (-not (Test-Path -LiteralPath $VersionPath)) {
        throw "PRI-1 needs the current version, and there is no VERSION file at $VersionPath."
    }
    $current = [System.Management.Automation.SemanticVersion]::new((Get-Content -LiteralPath $VersionPath -Raw).Trim())
    if ($validated -gt $current) {
        New-Finding 'PRI-1' $line "'validated' names $value, a release after the current version $current; record the release the page was checked against, at most $current."
    }
}

function Test-PageShape {
    param($Page, $Config, [string]$RepositoryRoot)

    $type = $Page.FrontMatter.Values['type']
    if (-not ($type -is [string]) -or -not $Config.PageTypes.ContainsKey($type)) { return }
    $spec = $Config.PageTypes[$type]
    $h1 = $Page.Headings | Where-Object Level -eq 1 | Select-Object -First 1
    $anchorLine = if ($h1) { $h1.Line } else { 1 }
    $h2s = @($Page.Headings | Where-Object Level -eq 2)
    $article = if ($type -match '^[aeiou]') { 'an' } else { 'a' }

    foreach ($key in @($spec.RequiredFrontMatter)) {
        if (-not $key) { continue }
        $value = $Page.FrontMatter.Values[$key]
        if (-not ($value -is [string]) -or -not $value) {
            New-Finding 'TYP-1' 1 "$article $type page needs '$key' in its front matter."
        }
        elseif ($key -eq 'scenario') {
            $relative = $Config.ScenarioPathFormat -f $value
            if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $relative))) {
                New-Finding 'TYP-1' $Page.FrontMatter.KeyLines[$key] "the scenario '$value' has no script at $relative; a tutorial names the integration scenario that runs it."
            }
        }
    }

    foreach ($required in @($spec.RequiredHeadings)) {
        if (-not $required) { continue }
        if (-not ($h2s | Where-Object { $_.Text -eq $required })) {
            New-Finding 'TYP-1' $anchorLine "$article $type page needs a '$required' section (an H2)."
        }
    }

    if ($spec.RequiresNumberedSteps) {
        $hasSteps = $false
        for ($i = $Page.FrontMatter.BodyStart; $i -lt $Page.Lines.Count; $i++) {
            if (-not $Page.InFenceLine[$i] -and $Page.Lines[$i] -match '^\s*\d+\.\s') { $hasSteps = $true; break }
        }
        if (-not $hasSteps) { New-Finding 'TYP-1' $anchorLine "$article $type page needs numbered steps." }
    }

    $name = Get-PageName -Page $Page
    foreach ($forbidden in @($spec.ForbiddenTitles)) {
        if ($forbidden -and $name -eq $forbidden) {
            New-Finding 'TYP-1' $anchorLine "$article $type page is titled with the name of the thing it explains, not '$forbidden'."
        }
    }

    $trailing = @($spec.TrailingHeadings | Where-Object { $_ })
    if ($trailing.Count -gt 0) {
        $seenTrailing = $null
        foreach ($h in $h2s) {
            $index = [array]::IndexOf($trailing, $h.Text)
            if ($index -lt 0) {
                if ($seenTrailing) {
                    New-Finding 'TYP-1' $h.Line "the '$($h.Text)' section comes after '$($seenTrailing.Text)'; on $article $type page, $($trailing -join ', ') come last, in that order."
                }
                continue
            }
            if ($seenTrailing -and $index -lt [array]::IndexOf($trailing, $seenTrailing.Text)) {
                New-Finding 'TYP-1' $h.Line "the '$($h.Text)' section must come before '$($seenTrailing.Text)'; on $article $type page, $($trailing -join ', ') come last, in that order."
            }
            $seenTrailing = $h
        }
    }
}

# ---------------------------------------------------------------------------
# Language and vocabulary
# ---------------------------------------------------------------------------

# The glossary's entries: "Term { #anchor }" or "Term (ABBR) { #anchor }" lines of its definition list.
# TUT-2: every label a tutorial's Portal tab names in bold is one the portal shows. The portal's text
# lives in its Razor markup and C#, and a Connector's setting names in its own source, so a label is
# checked by finding it there; case is ignored, since MudBlazor upper-cases buttons and tabs on screen.
# A label the portal builds from data ("Project Users to the Metaverse?") is excused with a marker.
function Get-PortalLabelText {
    param([string]$RepositoryRoot, [string[]]$Sources)
    $text = [System.Text.StringBuilder]::new()
    foreach ($source in $Sources) {
        $full = Join-Path $RepositoryRoot $source
        if (-not (Test-Path -LiteralPath $full)) { throw "PortalLabelSources names '$source', which does not exist." }
        Get-ChildItem -LiteralPath $full -Recurse -File -Include '*.razor', '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { [void]$text.AppendLine([System.IO.File]::ReadAllText($_.FullName)) }
    }
    $text.ToString().ToLowerInvariant()
}

function Test-PortalLabel {
    param($Page, $Config, [string]$RepositoryRoot)

    if ($Page.FrontMatter.Values['type'] -ne 'tutorial') { return }
    $inPortalTab = $false
    for ($i = $Page.FrontMatter.BodyStart; $i -lt $Page.Lines.Count; $i++) {
        $line = $Page.Lines[$i]
        if ($line -match '^===\s+"(?<tab>[^"]+)"') { $inPortalTab = $Matches.tab -eq 'Portal'; continue }
        if ($line -match '^\S') { $inPortalTab = $false; continue }
        if (-not $inPortalTab -or $Page.InFenceLine[$i]) { continue }
        foreach ($m in [regex]::Matches($line, '\*\*(?<label>[^*]+?)\*\*')) {
            $label = $m.Groups['label'].Value.Trim()
            if ($null -eq $script:PortalText) { $script:PortalText = Get-PortalLabelText -RepositoryRoot $RepositoryRoot -Sources @($Config.PortalLabelSources) }
            if (-not $script:PortalText.Contains($label.ToLowerInvariant())) {
                New-Finding 'TUT-2' ($i + 1) "the portal shows no '$label'; name each control exactly as the portal labels it, or excuse a label the portal builds from data with a marker saying so."
            }
        }
    }
}

function Get-GlossaryEntry {
    param($Page)
    for ($i = $Page.FrontMatter.BodyStart; $i -lt $Page.Lines.Count; $i++) {
        if ($Page.InFenceLine[$i]) { continue }
        if ($Page.Lines[$i] -match '^(?<name>[^\s:|>#!*-][^{]*?)\s*\{\s*#(?<anchor>[A-Za-z0-9_-]+)\s*\}\s*$') {
            [pscustomobject]@{
                Term   = ($Matches['name'] -replace '\s*\([A-Z]{2,}s?\)$', '').Trim()
                Anchor = $Matches['anchor']
                Line   = $i + 1
            }
        }
    }
}

function ConvertTo-TermPattern {
    param([string]$Term)
    ([regex]::Escape($Term) -replace '\\ ', '\s+')
}

# Everything the language rules match against, compiled once.
function New-LanguageModel {
    param($Config, $Vocabulary, [object[]]$GlossaryEntries)

    $canonical = @{}
    foreach ($g in $GlossaryEntries) { $canonical[$g.Anchor] = $g.Term }
    $options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    $titleCase = foreach ($t in $Vocabulary.Terms | Where-Object { $_.TitleCase -and $canonical.ContainsKey($_.Anchor) }) {
        $term = $canonical[$t.Anchor]
        [pscustomobject]@{
            Canonical = $term
            Regex     = [regex]::new("(?<![\w])(?<stem>$(ConvertTo-TermPattern $term))(?:s|es)?(?![\w])", $options)
        }
    }
    $rejected = foreach ($t in $Vocabulary.Terms) {
        $term = if ($canonical.ContainsKey($t.Anchor)) { $canonical[$t.Anchor] } else { $t.Anchor }
        foreach ($variant in $t.Rejected) {
            [pscustomobject]@{
                Canonical = $term
                Regex     = [regex]::new("(?<![\w])$(ConvertTo-TermPattern $variant)s?(?![\w])", $options)
            }
        }
    }
    $abbreviations = foreach ($key in @($Vocabulary.Abbreviations.Keys | Sort-Object)) {
        $full = $Vocabulary.Abbreviations[$key]
        [pscustomobject]@{
            Abbreviation = $key
            Full         = $full
            Definition   = [regex]::new("$(ConvertTo-TermPattern $full)s?\s*\($([regex]::Escape($key))s?\)", $options)
            Regex        = [regex]::new("(?<![\w])$([regex]::Escape($key))s?(?![\w])")
        }
    }
    $spellings = @{}
    foreach ($key in $Config.AmericanSpellings.Keys) { $spellings[$key.ToLowerInvariant()] = $Config.AmericanSpellings[$key] }
    $spellingRegex = if ($spellings.Count) {
        [regex]::new("(?<![\w])(?:$(($spellings.Keys | Sort-Object { -$_.Length } | ForEach-Object { [regex]::Escape($_) }) -join '|'))(?![\w])", $options)
    }

    [pscustomobject]@{
        TitleCase     = @($titleCase | Sort-Object { -$_.Canonical.Length })
        Rejected      = @($rejected)
        Abbreviations = @($abbreviations)
        Spellings     = $spellings
        SpellingRegex = $spellingRegex
        IzeRegex      = [regex]::new('(?<![\w])[A-Za-z]+iz(?:e|es|ed|ing|ation|ations|ational|er|ers|able)(?![\w])')
        IzeExceptions = @($Config.IzeExceptions | ForEach-Object { $_.ToLowerInvariant() })
        Allowed       = @($Config.AllowedSpellings | ForEach-Object { [regex]::new([regex]::Escape($_), $options) })
        TimeRelative  = @($Config.TimeRelativePatterns | ForEach-Object { [regex]::new($_, $options) })
        Future        = @($Config.FuturePatterns | ForEach-Object { [regex]::new($_, $options) })
        Minimisers    = @($Config.MinimiserPatterns | ForEach-Object { [regex]::new($_, $options) })
    }
}

# The page's running text, one entry per line: no front matter, code, HTML comments, snippet
# includes, link reference definitions or placeholder text. Text also drops link targets, URLs,
# HTML tags, attribute lists and entities; WithLinks keeps link targets, for LANG-8.
function Get-ProseLine {
    param($Page, [string[]]$StubPatterns)

    $inComment = $false
    $lines = $Page.Lines
    for ($i = $Page.FrontMatter.BodyStart; $i -lt $lines.Count; $i++) {
        if ($Page.InFenceLine[$i]) { continue }

        $rest = $lines[$i]
        $kept = [System.Text.StringBuilder]::new()
        while ($true) {
            if ($inComment) {
                $end = $rest.IndexOf('-->')
                if ($end -lt 0) { $rest = ''; break }
                $rest = $rest.Substring($end + 3)
                $inComment = $false
            }
            $start = $rest.IndexOf('<!--')
            if ($start -lt 0) { [void]$kept.Append($rest); break }
            [void]$kept.Append($rest.Substring(0, $start))
            $rest = $rest.Substring($start + 4)
            $inComment = $true
        }
        $line = $kept.ToString()

        if ([string]::IsNullOrWhiteSpace($line) -or $line.Contains('--8<--') -or $line -match '^\s*\[[^\]]+\]:\s*\S') { continue }
        if (@($StubPatterns | Where-Object { $_ -and $line.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count) { continue }

        $withLinks = [regex]::Replace($line, '(`+).+?\1', ' ') -replace $script:IconShortcode, ' '
        $text = $withLinks -replace '\]\([^)]*\)', ']' -replace '\]\[[^\]]*\]', ']' -replace '<[^>]+>', ' ' -replace 'https?://\S+', ' ' -replace '\{[^}]*\}', ' ' -replace '&#?\w+;', ' '
        $tableHeader = $text.TrimStart().StartsWith('|') -and ($i + 1 -lt $lines.Count) -and $lines[$i + 1] -match '^\s*\|?\s*:?-{3,}'

        [pscustomobject]@{ Line = $i + 1; Text = $text; WithLinks = $withLinks; TableHeader = $tableHeader }
    }
}

# Keeps the first letter's case, so a suggestion reads naturally in place.
function Format-LikeWord {
    param([string]$Word, [string]$Suggestion)
    if ($Word.Length -gt 0 -and [char]::IsUpper($Word[0])) { return $Suggestion.Substring(0, 1).ToUpperInvariant() + $Suggestion.Substring(1) }
    return $Suggestion
}

function Test-Language {
    param($Page, $Config, $Language)

    $emDash = [string][char]0x2014
    $mask = [System.Text.RegularExpressions.MatchEvaluator] { param($m) ' ' * $m.Length }

    foreach ($p in Get-ProseLine -Page $Page -StubPatterns $Config.StubPatterns) {
        $text = $p.Text

        # LANG-1
        for ($at = $text.IndexOf($emDash); $at -ge 0; $at = $text.IndexOf($emDash, $at + 1)) {
            New-Finding 'LANG-1' $p.Line 'an em dash; use a semicolon, comma, colon or parentheses.'
        }
        foreach ($m in [regex]::Matches($text, '(?<!\S)--(?!\S)')) {
            New-Finding 'LANG-1' $p.Line "' -- ' shows as two hyphens on the site; use a semicolon, comma, colon or parentheses."
        }
        foreach ($m in [regex]::Matches($text, '(?<range>(?<from>\w+)--(?<to>\w+))')) {
            New-Finding 'LANG-1' $p.Line "'$($m.Groups['range'].Value)' shows as two hyphens on the site; write a range as '$($m.Groups['from'].Value) to $($m.Groups['to'].Value)'."
        }

        # LANG-2
        $spelling = $text
        foreach ($allowed in $Language.Allowed) { $spelling = $allowed.Replace($spelling, $mask) }
        if ($Language.SpellingRegex) {
            foreach ($m in $Language.SpellingRegex.Matches($spelling)) {
                $british = Format-LikeWord $m.Value $Language.Spellings[$m.Value.ToLowerInvariant()]
                New-Finding 'LANG-2' $p.Line "'$($m.Value)' is an American spelling; write '$british'."
            }
        }
        foreach ($m in $Language.IzeRegex.Matches($spelling)) {
            if ($m.Value.ToLowerInvariant() -in $Language.IzeExceptions) { continue }
            $at = $m.Value.LastIndexOf('iz')
            $british = $m.Value.Substring(0, $at) + 'is' + $m.Value.Substring($at + 2)
            New-Finding 'LANG-2' $p.Line "'$($m.Value)' is an American spelling; write '$british'."
        }

        # LANG-3: rejected variants first, then Title Case, longest term first. Each match is
        # masked, so a lower-case "metaverse object" is one finding, not two.
        $nouns = $text
        foreach ($r in $Language.Rejected) {
            foreach ($m in $r.Regex.Matches($nouns)) {
                New-Finding 'LANG-3' $p.Line "'$($m.Value)' is not product vocabulary; write '$($r.Canonical)'."
            }
            $nouns = $r.Regex.Replace($nouns, $mask)
        }
        foreach ($t in $Language.TitleCase) {
            foreach ($m in $t.Regex.Matches($nouns)) {
                $stem = $m.Groups['stem'].Value -replace '\s+', ' '
                if ($stem -cne $t.Canonical) {
                    New-Finding 'LANG-3' $p.Line "'$($m.Value)' is a product noun; write '$($t.Canonical)$($m.Value.Substring($m.Groups['stem'].Length))'."
                }
            }
            $nouns = $t.Regex.Replace($nouns, $mask)
        }

        # LANG-4
        if (-not $p.TableHeader) {
            $abbreviated = $text
            foreach ($a in $Language.Abbreviations) {
                $abbreviated = $a.Definition.Replace($abbreviated, $mask)
                foreach ($m in $a.Regex.Matches($abbreviated)) {
                    New-Finding 'LANG-4' $p.Line "'$($m.Value)' in running text; write '$($a.Full)' in full (abbreviations belong only in tight table headers)."
                }
            }
        }

        # LANG-5, LANG-6, LANG-7
        foreach ($r in $Language.TimeRelative) {
            foreach ($m in $r.Matches($text)) {
                New-Finding 'LANG-5' $p.Line "'$($m.Value)' is relative to when it was written; state the behaviour, and mark a difference between releases 'Available from vX.Y.Z'."
            }
        }
        foreach ($r in $Language.Future) {
            foreach ($m in $r.Matches($text)) {
                New-Finding 'LANG-6' $p.Line "'$($m.Value)' promises the future; say what JIM does today, and leave plans to the roadmap."
            }
        }
        foreach ($r in $Language.Minimisers) {
            foreach ($m in $r.Matches($text)) {
                New-Finding 'LANG-7' $p.Line "'$($m.Value)' tells the reader it is easy; cut it."
            }
        }

        # LANG-8
        foreach ($m in [regex]::Matches($text, '(?<![\w&#/])#\d+\b')) {
            New-Finding 'LANG-8' $p.Line "'$($m.Value)' is an issue or pull request number; a public page leaves JIM's own workings out (VOI-4)."
        }
        foreach ($m in [regex]::Matches($p.WithLinks, 'github\.com/[^/\s)]+/[^/\s)]+/(?:issues|pull|discussions)/\d+')) {
            New-Finding 'LANG-8' $p.Line "a link to a GitHub issue or pull request; a public page leaves JIM's own workings out (VOI-4)."
        }

        # STY-1 (the pattern is described where $script:EmojiRegex is defined).
        foreach ($m in $script:EmojiRegex.Matches($text)) {
            New-Finding 'STY-1' $p.Line "an emoji ('$($m.Value)'); use a Material Design icon instead, such as :material-check-circle-outline:{ .jim-yes role=`"img`" aria-label=`"Yes`" } for a tick in a table (see STY-1 and STY-3)."
        }
    }
}

# TERM-1: the vocabulary holds every glossary entry and nothing else, once each, and every
# product noun's home page is a page this check covers.
function Test-Vocabulary {
    param($Vocabulary, [object[]]$GlossaryEntries, [hashtable]$PageSet, [string]$VocabularyPath)

    $glossary = @{}
    foreach ($g in $GlossaryEntries) { $glossary[$g.Anchor] = $g }
    $seen = @{}
    foreach ($t in $Vocabulary.Terms) {
        if ($seen.ContainsKey($t.Anchor)) {
            [pscustomobject]@{ Where = 'Vocabulary'; Line = $t.Line; Message = "the vocabulary lists '$($t.Anchor)' twice; keep one entry." }
            continue
        }
        $seen[$t.Anchor] = $true
        if (-not $glossary.ContainsKey($t.Anchor)) {
            [pscustomobject]@{ Where = 'Vocabulary'; Line = $t.Line; Message = "the vocabulary entry '$($t.Anchor)' has no glossary entry; define the term in the glossary, or remove the entry." }
        }
        if (-not $PageSet.ContainsKey($t.Home)) {
            [pscustomobject]@{ Where = 'Vocabulary'; Line = $t.Line; Message = "the home page '$($t.Home)' for '$($t.Anchor)' is not a page this check covers; name the page that explains the term (HOME-1)." }
        }
    }
    foreach ($g in $GlossaryEntries) {
        if (-not $seen.ContainsKey($g.Anchor)) {
            [pscustomobject]@{ Where = 'Glossary'; Line = $g.Line; Message = "the glossary entry '$($g.Term)' has no vocabulary entry; add one to $VocabularyPath, naming its home page." }
        }
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

try {
    $config = Read-Configuration -Path $ConfigPath
    $baseline = Read-Baseline -Path $BaselinePath
    $vocabulary = Read-Vocabulary -Path $VocabularyPath
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

if ($SeedBaseline -and (-not $Rule -or $config.Rules[$Rule] -ne 'Ratchet')) {
    Write-Host "ERROR: -SeedBaseline needs -Rule naming a Ratchet rule ($(($config.Rules.Keys | Where-Object { $config.Rules[$_] -eq 'Ratchet' } | Sort-Object) -join ', '))." -ForegroundColor Red
    exit 2
}

if (-not (Test-Path -LiteralPath $DocsPath)) {
    Write-Host "ERROR: docs folder not found at '$DocsPath'." -ForegroundColor Red
    exit 2
}

$cwd = (Get-Location).Path
$docsFull = (Resolve-Path -LiteralPath $DocsPath).Path
$repositoryFull = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$docsRepoPath = [System.IO.Path]::GetRelativePath($repositoryFull, $docsFull) -replace '\\', '/'
$docsDisplayPrefix = [System.IO.Path]::GetRelativePath($cwd, $docsFull) -replace '\\', '/'
$navLabels = Get-NavLabel -Path $MkDocsConfigPath
$excludePatterns = @($config.Exclude | ForEach-Object { ConvertTo-GlobRegex $_ })

if (-not $PSBoundParameters.ContainsKey('ChangedFiles') -and $null -ne $env:CHANGED_FILES) {
    $ChangedFiles = @($env:CHANGED_FILES -split "\r?\n" | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
}
$listingChangedOnly = ($null -ne $ChangedFiles) -and -not $ShowWarnings
$changedPages = @{}
foreach ($file in @($ChangedFiles)) {
    if ($file -and $file.StartsWith("$docsRepoPath/")) { $changedPages[$file.Substring($docsRepoPath.Length + 1)] = $true }
}

$mntNote = $null
if (-not $BaseRef) {
    $mergeBase = & git -C $repositoryFull merge-base HEAD origin/main 2>$null
    if ($LASTEXITCODE -eq 0 -and $mergeBase) { $BaseRef = "$mergeBase".Trim() }
    else { $mntNote = 'MNT-3 skipped: no base to compare page sizes against (pass -BaseRef, or fetch origin/main).' }
}

# The glossary gives each product noun its canonical form; the vocabulary says what to enforce.
$glossaryEntries = @()
$glossaryFull = Join-Path $docsFull $config.GlossaryPage
if (Test-Path -LiteralPath $glossaryFull) {
    $glossaryModel = Get-PageModel -FullPath $glossaryFull -RelativePath $config.GlossaryPage -DisplayPath "$docsDisplayPrefix/$($config.GlossaryPage)"
    $glossaryEntries = @(Get-GlossaryEntry -Page $glossaryModel)
}
$language = New-LanguageModel -Config $config -Vocabulary $vocabulary -GlossaryEntries $glossaryEntries

# --- Collect findings per page ---

$pages = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

$files = Get-ChildItem -LiteralPath $docsFull -Filter '*.md' -Recurse -File | Sort-Object FullName
# The portal's text, for TUT-2; read on first use, since only a tutorial's Portal labels need it.
$script:PortalText = $null

foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($docsFull, $file.FullName) -replace '\\', '/'
    if ($relative -split '/' | Where-Object { $_.StartsWith('.') }) { continue }
    if ($excludePatterns | Where-Object { $relative -match $_ }) { continue }

    $page = Get-PageModel -FullPath $file.FullName -RelativePath $relative -DisplayPath "$docsDisplayPrefix/$relative"
    $pages.Add($page)

    $conformant = ($relative -in $config.ConformantPages) -or [bool]($config.ConformantFolders | Where-Object { $relative.StartsWith($_.TrimEnd('/') + '/') })
    $priority = $relative -in $config.PriorityPages
    $budget = if ($conformant -and $priority) { [int]$config.PrioritySizeBudgetWords } else { [int]$config.SizeBudgetWords }

    $raw = @(
        Test-FrontMatter -Page $page -Config $config
        Test-PageName -Page $page -NavLabels $navLabels
        Test-HeadingStructure -Page $page
        Test-HeadingIcons -Page $page
        Test-CodeFences -Page $page
        Test-Stub -Page $page -Config $config
        Test-Opening -Page $page
        Test-SizeBudget -Page $page -Budget $budget
        Test-Growth -Page $page -Budget $budget -BaseRef $BaseRef -RepositoryRoot $repositoryFull -DocsRepoPath $docsRepoPath
        Test-Accessibility -Page $page -DocsFullPath $docsFull -RepositoryRoot $repositoryFull
        Test-PageShape -Page $page -Config $config -RepositoryRoot $repositoryFull
        Test-Validation -Page $page -Priority $priority -Conformant $conformant -VersionPath (Join-Path $repositoryFull 'VERSION')
        Test-PortalLabel -Page $page -Config $config -RepositoryRoot $repositoryFull
        Test-Language -Page $page -Config $config -Language $language
    ) | Where-Object { $_ }

    # Exception markers: apply, then report the markers themselves.
    $kept = foreach ($finding in $raw) {
        $suppressed = $false
        foreach ($m in $page.Markers) {
            if ($finding.Rule -notin $m.Rules -or $finding.Rule -in $script:UnmarkableRules -or [string]::IsNullOrWhiteSpace($m.Reason)) { continue }
            if ($m.Kind -eq 'allow-page' -or $finding.Line -eq $m.Line -or $finding.Line -eq $m.TargetLine) {
                $m.Used = $true
                $suppressed = $true
            }
        }
        if (-not $suppressed) { $finding }
    }
    $markerFindings = @(
        foreach ($line in $page.MalformedMarkers) {
            New-Finding 'EXC-1' $line "the exception marker is malformed; write <!-- conformance: allow RULE `"reason`" --> or allow-page."
        }
        foreach ($m in $page.Markers) {
            $unknown = @($m.Rules | Where-Object { $_ -notin $script:KnownRules -or $_ -eq 'EXC-1' })
            $unmarkable = @($m.Rules | Where-Object { $_ -in $script:UnmarkableRules -and $_ -ne 'EXC-1' })
            if ($unmarkable) {
                New-Finding 'EXC-1' $m.Line "the exception marker names $($unmarkable -join ', '), which cannot be excused by a marker; split the page, or rewrite what the change superseded so it does not grow."
            }
            elseif ($unknown) {
                New-Finding 'EXC-1' $m.Line "the exception marker names '$($unknown -join "', '")', which is not a rule it can suppress."
            }
            elseif ([string]::IsNullOrWhiteSpace($m.Reason)) {
                New-Finding 'EXC-1' $m.Line "the exception marker for $($m.Rules -join ', ') gives no reason; add one in double quotes, so the next author knows why."
            }
            elseif (-not $m.Used) {
                New-Finding 'EXC-1' $m.Line "the exception marker for $($m.Rules -join ', ') suppresses nothing; remove it."
            }
        }
    )

    foreach ($finding in @($kept) + $markerFindings) {
        if (-not $finding) { continue }
        $mode = $config.Rules[$finding.Rule]
        if ($conformant -and $mode -eq 'Ratchet') { $mode = 'Block' }
        if ($conformant -and $priority -and $mode -eq 'Warn') { $mode = 'Block' }
        $findings.Add([pscustomobject]@{
                Rule        = $finding.Rule
                Page        = $relative
                DisplayPath = $page.DisplayPath
                Line        = $finding.Line
                Message     = $finding.Message
                Mode        = $mode
                Conformant  = $conformant
            })
    }
}

$pageSet = @{}
foreach ($p in $pages) { $pageSet[$p.RelativePath] = $true }

$vocabularyDisplay = [System.IO.Path]::GetRelativePath($cwd, (Resolve-Path -LiteralPath $VocabularyPath).Path) -replace '\\', '/'
foreach ($v in Test-Vocabulary -Vocabulary $vocabulary -GlossaryEntries $glossaryEntries -PageSet $pageSet -VocabularyPath $vocabularyDisplay) {
    $onGlossary = $v.Where -eq 'Glossary'
    $findings.Add([pscustomobject]@{
            Rule        = 'TERM-1'
            Page        = if ($onGlossary) { $config.GlossaryPage } else { $vocabularyDisplay }
            DisplayPath = if ($onGlossary) { "$docsDisplayPrefix/$($config.GlossaryPage)" } else { $vocabularyDisplay }
            Line        = $v.Line
            Message     = $v.Message
            Mode        = $config.Rules['TERM-1']
            Conformant  = $false
        })
}
$conformantSet = @{}
foreach ($p in $pages) {
    if (($p.RelativePath -in $config.ConformantPages) -or [bool]($config.ConformantFolders | Where-Object { $p.RelativePath.StartsWith($_.TrimEnd('/') + '/') })) {
        $conformantSet[$p.RelativePath] = $true
    }
}

# --- Ratchet counts per rule and page ---

$ratchetCounts = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
foreach ($f in $findings | Where-Object Mode -eq 'Ratchet') {
    $key = "$($f.Rule)`t$($f.Page)"
    $ratchetCounts[$key] = 1 + $(if ($ratchetCounts.ContainsKey($key)) { $ratchetCounts[$key] } else { 0 })
}

function Test-BaselineEntryApplies {
    param([string]$Key)
    $ruleId, $pagePath = $Key.Split("`t")
    return $pageSet.ContainsKey($pagePath) -and -not $conformantSet.ContainsKey($pagePath) -and $config.Rules[$ruleId] -eq 'Ratchet'
}

if ($SeedBaseline) {
    $existing = @($baseline.Keys | Where-Object { $_.StartsWith("$Rule`t") })
    if ($existing.Count -gt 0) {
        Write-Host "ERROR: $Rule already has baseline entries; seeding is only for a rule that has none. Fix the pages, or lower entries with -UpdateBaseline." -ForegroundColor Red
        exit 1
    }
    $added = 0
    foreach ($key in $ratchetCounts.Keys | Where-Object { $_.StartsWith("$Rule`t") }) {
        $baseline[$key] = $ratchetCounts[$key]
        $added++
    }
    Write-Baseline -Path $BaselinePath -Entries $baseline
    Write-Host "Seeded $added baseline entr$(if ($added -eq 1) { 'y' } else { 'ies' }) for $Rule in '$BaselinePath'."
}

if ($UpdateBaseline) {
    $updated = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
    $lowered = 0
    $removed = 0
    foreach ($key in $baseline.Keys) {
        $current = if ($ratchetCounts.ContainsKey($key)) { $ratchetCounts[$key] } else { 0 }
        if (-not (Test-BaselineEntryApplies -Key $key) -or $current -eq 0) { $removed++; continue }
        if ($current -lt $baseline[$key]) { $updated[$key] = $current; $lowered++ }
        else { $updated[$key] = $baseline[$key] }
    }
    $baseline = $updated
    Write-Baseline -Path $BaselinePath -Entries $baseline
    Write-Host "Updated '$BaselinePath': $lowered entr$(if ($lowered -eq 1) { 'y' } else { 'ies' }) lowered, $removed removed. Entries are never added or raised."
}

# --- Classify ---

$errors = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()
$baselinedByRule = @{}

foreach ($f in $findings | Where-Object Mode -eq 'Block') { $errors.Add($f) }
foreach ($f in $findings | Where-Object Mode -eq 'Warn') { $warnings.Add($f) }

foreach ($group in $findings | Where-Object Mode -eq 'Ratchet' | Group-Object { "$($_.Rule)`t$($_.Page)" }) {
    $allowed = if ($baseline.ContainsKey($group.Name)) { $baseline[$group.Name] } else { 0 }
    $count = $group.Count
    if ($count -gt $allowed) {
        foreach ($f in $group.Group) {
            $errors.Add([pscustomobject]@{
                    Rule = $f.Rule; Page = $f.Page; DisplayPath = $f.DisplayPath; Line = $f.Line; Mode = 'Ratchet'
                    Message = "$($f.Message) ($count found, the baseline allows $allowed.)"
                })
        }
    }
    else {
        $ruleId = $group.Group[0].Rule
        $baselinedByRule[$ruleId] = $count + $(if ($baselinedByRule.ContainsKey($ruleId)) { $baselinedByRule[$ruleId] } else { 0 })
        if ($count -lt $allowed) {
            $f = $group.Group[0]
            $errors.Add([pscustomobject]@{
                    Rule = $f.Rule; Page = $f.Page; DisplayPath = $f.DisplayPath; Line = 1; Mode = 'Baseline'
                    Message = "the baseline allows $allowed $($f.Rule) finding(s) here but $count remain; lock the improvement in with -UpdateBaseline."
                })
        }
    }
}

foreach ($key in $baseline.Keys) {
    if ($ratchetCounts.ContainsKey($key)) { continue }
    $ruleId, $pagePath = $key.Split("`t")
    $display = "$docsDisplayPrefix/$pagePath"
    $reason = if (-not $pageSet.ContainsKey($pagePath)) { "$pagePath is not a page this check covers" }
    elseif ($conformantSet.ContainsKey($pagePath)) { "$pagePath is conformant, so it carries no baseline" }
    elseif ($config.Rules[$ruleId] -ne 'Ratchet') { "$ruleId is no longer a Ratchet rule" }
    else { "no $ruleId findings remain on $pagePath" }
    $errors.Add([pscustomobject]@{
            Rule = $ruleId; Page = $pagePath; DisplayPath = $display; Line = 1; Mode = 'Baseline'
            Message = "the baseline has an entry for $ruleId on $pagePath, but $reason; remove it with -UpdateBaseline."
        })
}

# --- Report ---

$inActions = $env:GITHUB_ACTIONS -eq 'true'
function ConvertTo-AnnotationText { param([string]$Text) $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

foreach ($e in $errors | Sort-Object DisplayPath, Line, Rule) {
    Write-Host "ERROR   $($e.DisplayPath):$($e.Line)  $($e.Rule)  $($e.Message)" -ForegroundColor Red
    if ($inActions) { Write-Host "::error file=$($e.DisplayPath),line=$($e.Line),title=$($e.Rule)::$(ConvertTo-AnnotationText $e.Message)" }
}

$listedWarnings = @($warnings | Where-Object { -not $listingChangedOnly -or $changedPages.ContainsKey($_.Page) })
if ($ShowWarnings -or $listingChangedOnly) {
    foreach ($w in $listedWarnings | Sort-Object DisplayPath, Line, Rule) {
        Write-Host "WARNING $($w.DisplayPath):$($w.Line)  $($w.Rule)  $($w.Message)" -ForegroundColor Yellow
        if ($inActions) { Write-Host "::warning file=$($w.DisplayPath),line=$($w.Line),title=$($w.Rule)::$(ConvertTo-AnnotationText $w.Message)" }
    }
}
if ($mntNote) { Write-Host $mntNote }

$baselinedTotal = ($baselinedByRule.Values | Measure-Object -Sum).Sum
if ($null -eq $baselinedTotal) { $baselinedTotal = 0 }
$unlisted = $warnings.Count - $(if ($ShowWarnings -or $listingChangedOnly) { $listedWarnings.Count } else { 0 })

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add('## Docs conformance')
    $summary.Add('')
    $summary.Add("$($pages.Count) pages checked against ``$script:StandardPath``. Baselined findings are existing debt the baseline allows; it only ever shrinks.")
    $summary.Add('')
    $summary.Add('| Rule | Tier | Errors | Warnings | Baselined |')
    $summary.Add('|------|------|-------:|---------:|----------:|')
    foreach ($r in $script:KnownRules) {
        $e = @($errors | Where-Object Rule -eq $r).Count
        $w = @($warnings | Where-Object Rule -eq $r).Count
        $b = if ($baselinedByRule.ContainsKey($r)) { $baselinedByRule[$r] } else { 0 }
        $summary.Add("| $r | $($config.Rules[$r]) | $e | $w | $b |")
    }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n") -Encoding utf8
}

if ($errors.Count -gt 0) {
    Write-Host "`nDocs conformance FAILED: $($errors.Count) error(s) across $($pages.Count) pages. Each names its rule; the rules are defined in $script:StandardPath." -ForegroundColor Red
    exit 1
}

$warningNote = if ($unlisted -gt 0 -and $listingChangedOnly) { "; $unlisted more on pages this change did not touch (list them with -ShowWarnings)" }
elseif ($unlisted -gt 0) { ' (list them with -ShowWarnings)' }
else { '' }
Write-Host "`nDocs conformance passed: $($pages.Count) pages checked; $baselinedTotal baselined finding(s) allowed by the baseline; $($warnings.Count) warning(s)$warningNote." -ForegroundColor Green
exit 0
