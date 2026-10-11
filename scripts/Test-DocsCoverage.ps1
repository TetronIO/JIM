# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks that the public documentation covers what the product actually has,
    and names nothing it no longer has.

.DESCRIPTION
    The coverage checks of engineering/DOCUMENTATION_STANDARD.md (#1454). Each
    one builds an inventory from source, never from a hand-maintained list, and
    compares it with the docs both ways: something that exists but is not
    documented fails, and so does a name in the docs that no longer exists.

      COV-1  every cmdlet the PowerShell module exports has a heading, or a row
             in the first column of a table, on a docs/powershell/ page; and
             every cmdlet such a heading or row names is exported
      COV-2  every environment variable the product or installer reads has a
             row (or a heading) in the docs; and every JIM_ variable a docs
             table names is read by something. The product's reads are the
             Constants.Config names and GetEnvironmentVariable("JIM_...")
             calls; the PowerShell module's are $env:JIM_ reads outside help
             text; the installer's are the variables its header documents;
             and the compose files' ${JIM_...} references and .env.example
             count too
      COV-6  every expression function the evaluator registers has a row in a
             Function table of the expressions reference, and every row names a
             registered function
      LNK-3  every link from the product into the docs (a docs.junctional.io
             URL, or the installer's ${DOCS_BASE}) reaches a page, and the
             heading or anchor it names exists on it. Links built from a
             template ("#{anchor}") are left to the tests that fill them
             (GlossaryTermConsistencyTests)

    "Documented" means a heading or the first column of a table; a mention in
    running text does not count. Deliberate exceptions live in the allow-list
    in the configuration file, each with a reason; an entry that no longer
    applies (the thing is gone, or now documented) fails, so the list cannot
    rot.

    In GitHub Actions, findings are also written as annotations on the pull
    request diff, and a per-rule table is appended to the job summary.

    Exit codes: 0 covered, 1 a check failed, 2 the configuration could not be
    read.

.PARAMETER RepositoryRoot
    The repository root; every path in the configuration is relative to it.
    Defaults to the current directory.

.PARAMETER ConfigPath
    The configuration file: where each inventory comes from, where the docs
    are, and the allow-list. Relative to RepositoryRoot. Defaults to
    docs/.conformance/coverage.psd1.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsCoverage.ps1
    Checks every inventory; lists each gap with the file and line to fix.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = '.',
    [string]$ConfigPath = 'docs/.conformance/coverage.psd1'
)

$ErrorActionPreference = 'Stop'
$script:Rules = @('COV-1', 'COV-2', 'COV-6', 'LNK-3')
$script:StandardPath = 'engineering/DOCUMENTATION_STANDARD.md'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function New-Finding {
    param([string]$Rule, [string]$Path, [int]$Line, [string]$Message)
    [pscustomobject]@{ Rule = $Rule; Path = $Path; Line = [Math]::Max(1, $Line); Message = $Message }
}

function Get-RepoFile {
    param([string]$Root, [string[]]$Paths, [string[]]$Extensions)
    foreach ($p in $Paths) {
        $full = Join-Path $Root $p
        if (Test-Path -LiteralPath $full -PathType Leaf) { Get-Item -LiteralPath $full; continue }
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $full -Recurse -File |
            Where-Object { $_.Extension -in $Extensions } |
            Where-Object { -not (($_.FullName.Substring($Root.Length) -replace '\\', '/') -match '/(Tests?|bin|obj|node_modules)/') }
    }
}

function Get-RelativePath {
    param([string]$Root, [string]$FullPath)
    [System.IO.Path]::GetRelativePath($Root, $FullPath) -replace '\\', '/'
}

function ConvertTo-GlobRegex {
    param([string]$Glob)
    $escaped = [regex]::Escape($Glob) -replace '\\\*\\\*', '.*' -replace '\\\*', '[^/]*'
    "^$escaped$"
}

# The lines of a Markdown file outside its front matter and code blocks, with line numbers.
function Get-MarkdownLine {
    param([string]$FullPath)
    $lines = [System.IO.File]::ReadAllLines($FullPath)
    $start = 0
    if ($lines.Count -gt 0 -and $lines[0].Trim() -eq '---') {
        for ($i = 1; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq '---') { $start = $i + 1; break } }
    }
    $fence = $null
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*(?<f>`{3,}|~{3,})') {
            if (-not $fence) { $fence = $Matches['f']; continue }
            $trimmed = $lines[$i].Trim()
            if ($trimmed.Length -ge $fence.Length -and $trimmed.Trim($fence[0]) -eq '') { $fence = $null; continue }
        }
        if ($fence) { continue }
        [pscustomobject]@{ Line = $i + 1; Text = $lines[$i] }
    }
}

# Headings and the first cells of table body rows: what "documented" means.
function Get-DocumentedEntry {
    param([string]$FullPath)
    $lines = @(Get-MarkdownLine -FullPath $FullPath)
    for ($k = 0; $k -lt $lines.Count; $k++) {
        $text = $lines[$k].Text
        if ($text -match '^\s{0,3}#{1,6}\s+(?<h>.+?)\s*$') {
            [pscustomobject]@{ Line = $lines[$k].Line; Text = $Matches['h']; Kind = 'Heading'; Header = $null }
            continue
        }
        if ($text -match '^\s*\|' -and $text -notmatch '^\s*\|?\s*:?-{3,}') {
            # A body row: the row above is the separator or another body row.
            $header = $null
            for ($j = $k - 1; $j -ge 0; $j--) {
                if ($lines[$j].Text -match '^\s*\|?\s*:?-{3,}') {
                    if ($j -ge 1) { $header = (($lines[$j - 1].Text.Trim().Trim('|')) -split '\|')[0].Trim() }
                    break
                }
                if ($lines[$j].Text -notmatch '^\s*\|') { break }
            }
            if ($null -eq $header) { continue }
            $first = (($text.Trim().Trim('|')) -split '\|')[0].Trim()
            [pscustomobject]@{ Line = $lines[$k].Line; Text = $first; Kind = 'Row'; Header = $header }
        }
    }
}

# MkDocs' default heading slugs (Python-Markdown's toc slugify), with its _1, _2 suffixes for repeats.
function Get-PageAnchor {
    param([string]$FullPath)
    $anchors = [System.Collections.Generic.HashSet[string]]::new()
    $seen = @{}
    foreach ($l in Get-MarkdownLine -FullPath $FullPath) {
        foreach ($m in [regex]::Matches($l.Text, '\{[^}]*#(?<id>[A-Za-z0-9_:.-]+)[^}]*\}')) { [void]$anchors.Add($m.Groups['id'].Value) }
        foreach ($m in [regex]::Matches($l.Text, '\bid="(?<id>[^"]+)"')) { [void]$anchors.Add($m.Groups['id'].Value) }
        if ($l.Text -match '^\s{0,3}#{1,6}\s+(?<h>.+?)\s*$') {
            $heading = $Matches['h']
            if ($heading -match '\{[^}]*#[A-Za-z0-9_:.-]+[^}]*\}') { continue }
            $heading = $heading -replace '\s*\{[^}]*\}\s*$', '' -replace '`', '' -replace '\[([^\]]*)\]\([^)]*\)', '$1' -replace '<[^>]+>', ''
            $ascii = [System.Text.Encoding]::ASCII.GetString([System.Text.Encoding]::ASCII.GetBytes($heading.Normalize([System.Text.NormalizationForm]::FormKD))) -replace '\?', ''
            $slug = (($ascii -replace '[^\w\s-]', '').Trim().ToLowerInvariant()) -replace '[-\s]+', '-'
            if ($seen.ContainsKey($slug)) { $seen[$slug]++; $slug = "${slug}_$($seen[$slug])" } else { $seen[$slug] = 0 }
            [void]$anchors.Add($slug)
        }
    }
    , $anchors
}

# ---------------------------------------------------------------------------
# Inventories
# ---------------------------------------------------------------------------

function Get-ExportedCmdlet {
    param([string]$Root, $Config)
    $path = Join-Path $Root $Config.ModuleManifest
    $manifest = Import-PowerShellDataFile -LiteralPath $path
    $lines = [System.IO.File]::ReadAllLines($path)
    foreach ($name in @($manifest.FunctionsToExport)) {
        $line = 1
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "'$([regex]::Escape($name))'") { $line = $i + 1; break } }
        [pscustomobject]@{ Name = $name; Path = $Config.ModuleManifest; Line = $line }
    }
}

function Get-EnvironmentVariableRead {
    param([string]$Root, $Config)

    function Find-InFile {
        param([string]$RelativePath, [string]$Pattern, [scriptblock]$LineFilter)
        $full = Join-Path $Root $RelativePath
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return }
        $lines = [System.IO.File]::ReadAllLines($full)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($LineFilter -and -not (& $LineFilter $i $lines)) { continue }
            foreach ($m in [regex]::Matches($lines[$i], $Pattern)) {
                [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = $RelativePath; Line = $i + 1 }
            }
        }
    }

    # The product: Constants.Config names, and literal reads anywhere else.
    Find-InFile -RelativePath $Config.Constants -Pattern '=>\s*"(?<name>JIM_[A-Z0-9_]+)"'
    foreach ($file in Get-RepoFile -Root $Root -Paths $Config.CodeSources -Extensions @('.cs')) {
        Find-InFile -RelativePath (Get-RelativePath $Root $file.FullName) -Pattern 'GetEnvironmentVariable\(\s*"(?<name>JIM_[A-Z0-9_]+)"'
    }

    # The PowerShell module: $env: reads outside comment-based help and comments.
    foreach ($file in Get-RepoFile -Root $Root -Paths $Config.PowerShellSources -Extensions @('.ps1', '.psm1')) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $tokens = $null; $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
        foreach ($t in $tokens | Where-Object { $_.Kind -eq 'Variable' }) {
            if ($t.Text -match '^\$env:(?<name>JIM_[A-Z0-9_]+)$') {
                [pscustomobject]@{ Name = $Matches['name']; Path = (Get-RelativePath $Root $file.FullName); Line = $t.Extent.StartLineNumber }
            }
        }
    }

    # Compose files: ${JIM_...}, never the $${...} escape, which the container itself expands.
    foreach ($compose in $Config.ComposeFiles) {
        Find-InFile -RelativePath $compose -Pattern '(?<!\$)\$\{(?<name>JIM_[A-Z0-9_]+)'
    }

    # .env.example, commented-out examples included.
    Find-InFile -RelativePath $Config.EnvExample -Pattern '^\s*#?\s*(?<name>JIM_[A-Z0-9_]+)='

    # The installer: the variables its header documents (the comment block before the first command).
    $installerFull = Join-Path $Root $Config.Installer
    if (Test-Path -LiteralPath $installerFull -PathType Leaf) {
        $lines = [System.IO.File]::ReadAllLines($installerFull)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -eq 0 -and $lines[0].StartsWith('#!')) { continue }
            if ($lines[$i] -notmatch '^\s*(#|$)') { break }
            foreach ($m in [regex]::Matches($lines[$i], '\b(?<name>JIM_[A-Z0-9_]+)\b')) {
                [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = $Config.Installer; Line = $i + 1 }
            }
        }
    }
}

function Get-ExpressionFunction {
    param([string]$Root, $Config)
    $lines = [System.IO.File]::ReadAllLines((Join-Path $Root $Config.ExpressionEvaluator))
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], 'SetFunction\(\s*"(?<name>\w+)"')) {
            [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = $Config.ExpressionEvaluator; Line = $i + 1 }
        }
    }
}

function Get-DocsLink {
    param([string]$Root, $Config)
    $base = [regex]::Escape($Config.DocsBaseUrl.TrimEnd('/'))
    # A link ends at whitespace, a quote, a bracket, or a '$' that starts the next variable (a colour reset, say).
    $pattern = "(?:$base|\`$\{?DOCS_BASE\}?)(?<path>/[^\s`"'<>)\]\\`$]*)?"
    foreach ($file in Get-RepoFile -Root $Root -Paths $Config.LinkSources -Extensions @('.cs', '.razor', '.ps1', '.psm1', '.psd1', '.sh', '.yml', '.yaml', '.md', '.json', '.kube', '.container', '.service', '.txt')) {
        $relative = Get-RelativePath $Root $file.FullName
        $lines = [System.IO.File]::ReadAllLines($file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($m in [regex]::Matches($lines[$i], $pattern)) {
                # The bare base URL (the site's home page), or the variable that holds it.
                if (-not $m.Groups['path'].Success) { continue }
                $target = $m.Groups['path'].Value.TrimEnd('.', ',', ';', ':')
                if ($target.Contains('{')) { continue }
                [pscustomobject]@{ Url = $m.Value; Path = $relative; Line = $i + 1; Target = $target }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
try {
    $configFull = Join-Path $root $ConfigPath
    if (-not (Test-Path -LiteralPath $configFull)) { throw "Configuration file not found at '$ConfigPath'." }
    $config = Import-PowerShellDataFile -LiteralPath $configFull
    foreach ($key in 'ModuleManifest', 'CmdletDocs', 'Constants', 'CodeSources', 'PowerShellSources', 'ComposeFiles', 'EnvExample', 'Installer',
        'ExpressionEvaluator', 'ExpressionDocs', 'LinkSources', 'DocsBaseUrl', 'DocsPath', 'DocsExclude', 'Allow') {
        if (-not $config.ContainsKey($key)) { throw "Configuration '$ConfigPath' has no '$key'." }
    }
    foreach ($key in 'Cmdlets', 'EnvironmentVariables', 'ExpressionFunctions', 'DocsLinks') {
        if (-not $config.Allow.ContainsKey($key)) { throw "Configuration '$ConfigPath' has no Allow.$key." }
    }
    foreach ($key in 'ModuleManifest', 'ExpressionEvaluator', 'ExpressionDocs') {
        if (-not (Test-Path -LiteralPath (Join-Path $root $config[$key]) -PathType Leaf)) { throw "$key '$($config[$key])' does not exist." }
    }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$findings = [System.Collections.Generic.List[object]]::new()
$counts = [ordered]@{}
$allowPath = $ConfigPath

# The docs pages the checks read.
$docsRoot = Join-Path $root $config.DocsPath
$excludes = @($config.DocsExclude | ForEach-Object { ConvertTo-GlobRegex $_ })
$docPages = @(Get-ChildItem -LiteralPath $docsRoot -Filter '*.md' -Recurse -File | Where-Object {
        $rel = Get-RelativePath $docsRoot $_.FullName
        -not ($rel -split '/' | Where-Object { $_.StartsWith('.') }) -and -not ($excludes | Where-Object { $rel -match $_ })
    })
$pageEntries = @{}
foreach ($page in $docPages) { $pageEntries[$page.FullName] = @(Get-DocumentedEntry -FullPath $page.FullName) }

# Compares an inventory with what the docs document, both ways, through the allow-list.
function Compare-Inventory {
    param([string]$Rule, [object[]]$Inventory, [object[]]$Documented, [hashtable]$Allow, [string]$Noun, [string]$Where, [string]$GoneMessage)

    $inventoryNames = @{}
    foreach ($item in $Inventory | Where-Object { $_ }) { if (-not $inventoryNames.ContainsKey($item.Name)) { $inventoryNames[$item.Name] = $item } }
    $documentedNames = @{}
    foreach ($d in $Documented | Where-Object { $_ }) { if (-not $documentedNames.ContainsKey($d.Name)) { $documentedNames[$d.Name] = $d } }

    foreach ($name in $Allow.Keys) {
        if ([string]::IsNullOrWhiteSpace($Allow[$name])) {
            New-Finding $Rule $allowPath 1 "the allow-list entry for '$name' gives no reason; say why it is not documented."
        }
        elseif (-not $inventoryNames.ContainsKey($name) -and -not $documentedNames.ContainsKey($name)) {
            New-Finding $Rule $allowPath 1 "the allow-list entry for '$name' names a $Noun that no longer exists; remove it."
        }
        elseif ($inventoryNames.ContainsKey($name) -and $documentedNames.ContainsKey($name)) {
            New-Finding $Rule $allowPath 1 "the allow-list entry for '$name' is no longer needed: the $Noun is documented; remove it."
        }
    }
    foreach ($name in $inventoryNames.Keys | Sort-Object) {
        if ($documentedNames.ContainsKey($name) -or $Allow.ContainsKey($name)) { continue }
        $item = $inventoryNames[$name]
        New-Finding $Rule $item.Path $item.Line "the $Noun '$name' is not documented; give it $Where, or add it to the allow-list in $allowPath with the reason it is left out."
    }
    foreach ($name in $documentedNames.Keys | Sort-Object) {
        if ($inventoryNames.ContainsKey($name) -or $Allow.ContainsKey($name)) { continue }
        $d = $documentedNames[$name]
        New-Finding $Rule $d.Path $d.Line ($GoneMessage -f $name)
    }
}

# --- COV-1: cmdlets ---
$cmdlets = @(Get-ExportedCmdlet -Root $root -Config $config)
$counts['cmdlets'] = $cmdlets.Count
$cmdletPattern = '(?<![\w-])(?<name>[A-Z][a-z]+-JIM[A-Za-z0-9]*)(?![\w-])'
$cmdletDocs = foreach ($page in $docPages | Where-Object { (Get-RelativePath $root $_.FullName).StartsWith($config.CmdletDocs.TrimEnd('/') + '/') }) {
    foreach ($e in $pageEntries[$page.FullName]) {
        foreach ($m in [regex]::Matches($e.Text, $cmdletPattern)) {
            [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = (Get-RelativePath $root $page.FullName); Line = $e.Line }
        }
    }
}
Compare-Inventory -Rule 'COV-1' -Inventory $cmdlets -Documented @($cmdletDocs) -Allow $config.Allow.Cmdlets -Noun 'cmdlet' `
    -Where "a heading, or a row in the first column of a table, on a $($config.CmdletDocs)/ page" `
    -GoneMessage "names '{0}', which the PowerShell module does not export; remove it, or rename it to the cmdlet that replaced it." |
    ForEach-Object { $findings.Add($_) }

# --- COV-2: environment variables ---
$variables = @(Get-EnvironmentVariableRead -Root $root -Config $config)
$counts['environment variables'] = @($variables.Name | Sort-Object -Unique).Count
$variableDocs = foreach ($page in $docPages) {
    foreach ($e in $pageEntries[$page.FullName]) {
        foreach ($m in [regex]::Matches($e.Text, '\b(?<name>JIM_[A-Z0-9_]+)\b')) {
            [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = (Get-RelativePath $root $page.FullName); Line = $e.Line }
        }
    }
}
Compare-Inventory -Rule 'COV-2' -Inventory $variables -Documented @($variableDocs) -Allow $config.Allow.EnvironmentVariables -Noun 'environment variable' `
    -Where 'a row in a settings table' `
    -GoneMessage "documents '{0}', which nothing reads; remove the row, or correct the name." |
    ForEach-Object { $findings.Add($_) }

# --- COV-6: expression functions ---
$functions = @(Get-ExpressionFunction -Root $root -Config $config)
$counts['expression functions'] = $functions.Count
$expressionPage = Join-Path $root $config.ExpressionDocs
$functionDocs = foreach ($e in @(Get-DocumentedEntry -FullPath $expressionPage) | Where-Object { $_.Kind -eq 'Row' -and $_.Header -match '^Function' }) {
    if ($e.Text -match '^`?(?<name>[A-Za-z_]\w*)\s*\(') {
        [pscustomobject]@{ Name = $Matches['name']; Path = $config.ExpressionDocs; Line = $e.Line }
    }
}
Compare-Inventory -Rule 'COV-6' -Inventory $functions -Documented @($functionDocs) -Allow $config.Allow.ExpressionFunctions -Noun 'expression function' `
    -Where "a row in a Function table on $($config.ExpressionDocs)" `
    -GoneMessage "documents '{0}', which the expression evaluator does not register; remove the row, or correct the name." |
    ForEach-Object { $findings.Add($_) }

# --- LNK-3: links from the product into the docs ---
$links = @(Get-DocsLink -Root $root -Config $config)
$counts['links into the docs'] = $links.Count
$anchorCache = @{}
foreach ($link in $links) {
    if ($config.Allow.DocsLinks.ContainsKey($link.Url)) { continue }
    $pathPart, $anchor = $link.Target -split '#', 2
    $pathPart = $pathPart.Trim('/')
    $candidates = if ($pathPart) { @("$pathPart.md", "$pathPart/index.md") } else { @('index.md') }
    $page = $candidates | ForEach-Object { Join-Path $docsRoot $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $page) {
        $findings.Add((New-Finding 'LNK-3' $link.Path $link.Line "links to '$($link.Url)', but no docs page is published at '/$pathPart/'; point it at the page that now covers this, or restore the page."))
        continue
    }
    if ($anchor) {
        if (-not $anchorCache.ContainsKey($page)) { $anchorCache[$page] = Get-PageAnchor -FullPath $page }
        if (-not $anchorCache[$page].Contains($anchor)) {
            $findings.Add((New-Finding 'LNK-3' $link.Path $link.Line "links to '#$anchor' on $(Get-RelativePath $root $page), which has no such heading or anchor; link to the heading that now covers it, or give that heading an explicit { #$anchor }."))
        }
    }
}
foreach ($url in $config.Allow.DocsLinks.Keys) {
    if ([string]::IsNullOrWhiteSpace($config.Allow.DocsLinks[$url])) {
        $findings.Add((New-Finding 'LNK-3' $allowPath 1 "the allow-list entry for '$url' gives no reason; say why the link need not resolve."))
    }
    elseif (-not ($links | Where-Object Url -eq $url)) {
        $findings.Add((New-Finding 'LNK-3' $allowPath 1 "the allow-list entry for '$url' names a link the product no longer has; remove it."))
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

$inActions = $env:GITHUB_ACTIONS -eq 'true'
function ConvertTo-AnnotationText { param([string]$Text) $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

foreach ($f in $findings | Sort-Object Path, Line, Rule) {
    Write-Host "ERROR   $($f.Path):$($f.Line)  $($f.Rule)  $($f.Message)" -ForegroundColor Red
    if ($inActions) { Write-Host "::error file=$($f.Path),line=$($f.Line),title=$($f.Rule)::$(ConvertTo-AnnotationText $f.Message)" }
}

$checked = ($counts.GetEnumerator() | ForEach-Object { "$($_.Value) $($_.Key)" }) -join ', '
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add('## Docs coverage')
    $summary.Add('')
    $summary.Add("Checked $checked against the docs, both ways (``$script:StandardPath``, Coverage).")
    $summary.Add('')
    $summary.Add('| Rule | Errors |')
    $summary.Add('|------|-------:|')
    foreach ($r in $script:Rules) { $summary.Add("| $r | $(@($findings | Where-Object Rule -eq $r).Count) |") }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n") -Encoding utf8
}

if ($findings.Count -gt 0) {
    Write-Host "`nDocs coverage FAILED: $($findings.Count) gap(s). Each names its rule; the rules are defined in $script:StandardPath." -ForegroundColor Red
    exit 1
}
Write-Host "`nDocs coverage passed: checked $checked against the docs, both ways." -ForegroundColor Green
exit 0
