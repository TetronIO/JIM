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
      ACC-3  every REST route the docs give exists in the OpenAPI document,
             with the method they give it; and a route written short
             ("POST /connected-systems/{id}/clear") is written in full, from
             /api/v1/
      COV-4  every resource group of the API (an OpenAPI tag) has a row in the
             Resource groups table of the API overview, and every row names one
      COV-5  every routable portal page (a .razor component with an @page
             directive) names its docs page from its PageInfo
             (DocsPage="configuration/connected-systems.md#the-connector-space"),
             and every docs page and heading a DocsPage names exists; where the
             value is an expression, every page it can choose is checked

    ACC-3 and COV-4 read the OpenAPI document, which only a build of JIM.Web
    can produce, so they run where one has been built (build-and-test) and are
    skipped, saying so, without -OpenApiPath.

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

.PARAMETER OpenApiPath
    The OpenAPI document ACC-3, COV-4 and LANG-10 check against, as
    scripts/Generate-OpenApiDoc.ps1 writes it. Without it, those three rules
    are skipped.

.PARAMETER ConformanceConfigPath
    The documentation conformance configuration, relative to RepositoryRoot,
    whose InternalTermPatterns LANG-10 applies to the API reference's text.
    Defaults to docs/.conformance/config.psd1.

.PARAMETER Rule
    Run only these rules, comma-separated (for example ACC-3,COV-4). Defaults to
    all of them.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsCoverage.ps1
    Checks every inventory; lists each gap with the file and line to fix.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$RepositoryRoot = '.',
    [string]$ConfigPath = 'docs/.conformance/coverage.psd1',
    [string]$OpenApiPath,
    [string]$ConformanceConfigPath = 'docs/.conformance/config.psd1',
    [string[]]$Rule
)

$ErrorActionPreference = 'Stop'
$script:Rules = @('COV-1', 'COV-2', 'COV-6', 'LNK-3', 'COV-5', 'ACC-3', 'COV-4', 'LANG-10')
$script:HttpMethods = @('get', 'post', 'put', 'patch', 'delete')
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

# A heading's leading icon shortcode (STY-3), such as ':material-console: '.
$script:LeadingIcon = '^:[a-z0-9]+(?:-[a-z0-9]+)+:\s*'

# Headings and the first cells of table body rows: what "documented" means.
function Get-DocumentedEntry {
    param([string]$FullPath)
    $lines = @(Get-MarkdownLine -FullPath $FullPath)
    for ($k = 0; $k -lt $lines.Count; $k++) {
        $text = $lines[$k].Text
        if ($text -match '^\s{0,3}#{1,6}\s+(?<h>.+?)\s*$') {
            [pscustomobject]@{ Line = $lines[$k].Line; Text = ($Matches['h'] -replace $script:LeadingIcon, ''); Kind = 'Heading'; Header = $null }
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
            # A leading icon (STY-3) renders as markup, so MkDocs leaves it out of the slug.
            $heading = $Matches['h'] -replace $script:LeadingIcon, ''
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
    # The evaluator and its partial files (DynamicExpressoEvaluator.Library.cs, say): a function registered in either is
    # one an expression can call. Each name is reported once, at its first registration (an overload registers it again).
    $evaluator = Join-Path $Root $Config.ExpressionEvaluator
    $partials = Get-ChildItem -Path (Split-Path $evaluator) -Filter ("{0}.*.cs" -f [System.IO.Path]::GetFileNameWithoutExtension($evaluator)) |
        Sort-Object Name
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in @((Get-Item $evaluator)) + @($partials)) {
        $relative = Get-RelativePath $Root $file.FullName
        $lines = [System.IO.File]::ReadAllLines($file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($m in [regex]::Matches($lines[$i], 'SetFunction\(\s*"(?<name>\w+)"')) {
                if ($seen.Add($m.Groups['name'].Value)) {
                    [pscustomobject]@{ Name = $m.Groups['name'].Value; Path = $relative; Line = $i + 1 }
                }
            }
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

# The portal's Razor components: whether each is a routable page (and the line of its first @page), and every
# DocsPage it gives, with the docs pages each can name. A DocsPage written as an expression
# (DocsPage="@(x ? "a.md" : "b.md")") names every string literal inside it.
function Get-PortalComponent {
    param([string]$Root, $Config)
    foreach ($file in Get-RepoFile -Root $Root -Paths $Config.PortalSources -Extensions @('.razor')) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $lineOf = { param([int]$Index) ([regex]::Matches($text.Substring(0, $Index), "`n")).Count + 1 }
        $pageMatch = [regex]::Match($text, '(?m)^\s*@page\s+"')
        $docsPages = foreach ($m in [regex]::Matches($text, '\bDocsPage\s*=\s*"')) {
            $start = $m.Index + $m.Length
            $value = $null
            if ($start + 1 -lt $text.Length -and $text[$start] -eq '@' -and $text[$start + 1] -eq '(') {
                # An expression: read to its matching parenthesis, skipping the strings inside it.
                $depth = 0; $inString = $false
                for ($i = $start + 1; $i -lt $text.Length; $i++) {
                    $c = $text[$i]
                    if ($inString) { if ($c -eq '\') { $i++ } elseif ($c -eq '"') { $inString = $false }; continue }
                    if ($c -eq '"') { $inString = $true }
                    elseif ($c -eq '(') { $depth++ }
                    elseif ($c -eq ')') { $depth--; if ($depth -eq 0) { $value = $text.Substring($start, $i - $start + 1); break } }
                }
                $targets = @([regex]::Matches([string]$value, '"(?<t>[^"]*)"') | ForEach-Object { $_.Groups['t'].Value })
            }
            else {
                $end = $text.IndexOf('"', $start)
                $value = $text.Substring($start, [Math]::Max(0, $end - $start))
                $targets = @($value)
            }
            [pscustomobject]@{ Line = (& $lineOf $m.Index); Targets = $targets }
        }
        [pscustomobject]@{
            Path      = (Get-RelativePath $Root $file.FullName)
            Routable  = $pageMatch.Success
            PageLine  = if ($pageMatch.Success) { & $lineOf $pageMatch.Index } else { 0 }
            DocsPages = @($docsPages)
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
        'ExpressionEvaluator', 'ExpressionDocs', 'LinkSources', 'DocsBaseUrl', 'DocsPath', 'DocsExclude', 'ApiIndex', 'PortalSources', 'Allow') {
        if (-not $config.ContainsKey($key)) { throw "Configuration '$ConfigPath' has no '$key'." }
    }
    foreach ($key in 'Cmdlets', 'EnvironmentVariables', 'ExpressionFunctions', 'DocsLinks', 'ApiRoutes', 'ApiResourceGroups', 'PortalPages') {
        if (-not $config.Allow.ContainsKey($key)) { throw "Configuration '$ConfigPath' has no Allow.$key." }
    }
    foreach ($key in 'ModuleManifest', 'ExpressionEvaluator', 'ExpressionDocs') {
        if (-not (Test-Path -LiteralPath (Join-Path $root $config[$key]) -PathType Leaf)) { throw "$key '$($config[$key])' does not exist." }
    }
    foreach ($r in @($Rule -split ',' | ForEach-Object { $_.Trim() })) {
        if ($r -and $r -notin $script:Rules) { throw "-Rule names '$r', which is not one of $($script:Rules -join ', ')." }
    }
    if ($OpenApiPath -and -not (Test-Path -LiteralPath $OpenApiPath -PathType Leaf)) { throw "The OpenAPI document '$OpenApiPath' does not exist." }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$findings = [System.Collections.Generic.List[object]]::new()
$counts = [ordered]@{}
$allowPath = $ConfigPath

$selected = if ($Rule) { @($Rule -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { $script:Rules }
$apiNote = $null
if (-not $OpenApiPath -and ('ACC-3' -in $selected -or 'COV-4' -in $selected -or 'LANG-10' -in $selected)) {
    $selected = @($selected | Where-Object { $_ -notin 'ACC-3', 'COV-4', 'LANG-10' })
    $apiNote = 'ACC-3, COV-4 and LANG-10 skipped: they need the OpenAPI document (-OpenApiPath), which build-and-test generates and checks.'
}

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
if ('COV-1' -in $selected) {
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
}

# --- COV-2: environment variables ---
if ('COV-2' -in $selected) {
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
}

# --- COV-6: expression functions ---
if ('COV-6' -in $selected) {
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
}

# --- LNK-3: links from the product into the docs ---
if ('LNK-3' -in $selected) {
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
}

# --- COV-5: portal pages name their docs page ---
if ('COV-5' -in $selected) {
    $components = @(Get-PortalComponent -Root $root -Config $config)
    $pages = @($components | Where-Object Routable)
    $counts['portal pages'] = $pages.Count
    $anchorCache5 = @{}
    foreach ($page in $pages) {
        if ($page.DocsPages.Count -gt 0 -or $config.Allow.PortalPages.ContainsKey($page.Path)) { continue }
        $findings.Add((New-Finding 'COV-5' $page.Path $page.PageLine "the portal page names no docs page; give its PageInfo a DocsPage (DocsPage=`"configuration/connected-systems.md#the-connector-space`"), or add it to the allow-list in $allowPath with the reason it has none."))
    }
    foreach ($component in $components) {
        foreach ($docsPage in $component.DocsPages) {
            if ($docsPage.Targets.Count -eq 0) {
                $findings.Add((New-Finding 'COV-5' $component.Path $docsPage.Line "the DocsPage expression names no docs page, so there is nothing to check; write each page it can choose as a string literal (DocsPage=`"@(x ? `"a.md`" : `"b.md`")`")."))
            }
            foreach ($target in $docsPage.Targets) {
                $pathPart, $anchor = $target -split '#', 2
                if (-not $pathPart.EndsWith('.md')) {
                    $findings.Add((New-Finding 'COV-5' $component.Path $docsPage.Line "DocsPage '$target' is not a docs page; name the Markdown page under $($config.DocsPath)/, for example 'configuration/connected-systems.md#the-connector-space'."))
                    continue
                }
                $full = Join-Path $docsRoot $pathPart
                if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
                    $findings.Add((New-Finding 'COV-5' $component.Path $docsPage.Line "DocsPage names '$pathPart', which is not a page under $($config.DocsPath)/; name the page that covers this one."))
                    continue
                }
                if ($anchor) {
                    if (-not $anchorCache5.ContainsKey($full)) { $anchorCache5[$full] = Get-PageAnchor -FullPath $full }
                    if (-not $anchorCache5[$full].Contains($anchor)) {
                        $findings.Add((New-Finding 'COV-5' $component.Path $docsPage.Line "DocsPage names '#$anchor' on $pathPart, which has no such heading or anchor; name the heading that covers this page."))
                    }
                }
            }
        }
    }
    foreach ($path in $config.Allow.PortalPages.Keys) {
        $page = $pages | Where-Object Path -eq $path | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($config.Allow.PortalPages[$path])) {
            $findings.Add((New-Finding 'COV-5' $allowPath 1 "the allow-list entry for '$path' gives no reason; say why the page names no docs page."))
        }
        elseif (-not $page) {
            $findings.Add((New-Finding 'COV-5' $allowPath 1 "the allow-list entry for '$path' names a page that no longer exists; remove it."))
        }
        elseif ($page.DocsPages.Count -gt 0) {
            $findings.Add((New-Finding 'COV-5' $allowPath 1 "the allow-list entry for '$path' is no longer needed: the page names its docs page; remove it."))
        }
    }
}

# --- ACC-3 and COV-4: the REST API, from its OpenAPI document ---

# Whether a route written in the docs matches a template from the OpenAPI document, segment by segment. A
# template's {parameter} matches anything the docs put there: {id}, <id>, a real id.
function Test-RouteMatch {
    param([string[]]$Written, [string[]]$Template)
    if ($Written.Count -ne $Template.Count) { return $false }
    for ($i = 0; $i -lt $Written.Count; $i++) {
        if ($Template[$i].StartsWith('{')) { continue }
        if ($Written[$i] -ne $Template[$i]) { return $false }
    }
    return $true
}

if ('ACC-3' -in $selected -or 'COV-4' -in $selected) {
    $openApi = Get-Content -LiteralPath $OpenApiPath -Raw | ConvertFrom-Json -AsHashtable
    $templates = foreach ($path in $openApi.paths.Keys) {
        $methods = @($openApi.paths[$path].Keys | Where-Object { $_ -in $script:HttpMethods })
        if (-not $methods) { continue }
        [pscustomobject]@{
            Path     = $path
            Segments = @($path.Trim('/') -split '/')
            Methods  = @($methods | ForEach-Object { $_.ToUpperInvariant() })
            Tags     = @($methods | ForEach-Object { $openApi.paths[$path][$_].tags } | Where-Object { $_ } | Sort-Object -Unique)
        }
    }
}

if ('ACC-3' -in $selected) {
    $methodPattern = '(?<![\w/-])(?<method>GET|POST|PUT|PATCH|DELETE)\s+(?<path>(?:\.\.\.|…)?/[^\s`''"()<>\[\]|,]*)'
    $barePattern = '(?<path>/api/v\d+/[^\s`''"()<>\[\]|,]*)'
    $routesSeen = @{}
    $routeCount = 0
    foreach ($page in $docPages) {
        $relative = Get-RelativePath $root $page.FullName
        $lines = [System.IO.File]::ReadAllLines($page.FullName)
        $start = 0
        if ($lines.Count -gt 0 -and $lines[0].Trim() -eq '---') {
            for ($i = 1; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq '---') { $start = $i + 1; break } }
        }
        for ($i = $start; $i -lt $lines.Count; $i++) {
            $routes = [System.Collections.Generic.List[object]]::new()
            $taken = [System.Collections.Generic.List[int]]::new()
            foreach ($m in [regex]::Matches($lines[$i], $methodPattern)) {
                $routes.Add([pscustomobject]@{ Text = $m.Value; Method = $m.Groups['method'].Value; Path = $m.Groups['path'].Value })
                $taken.Add($m.Groups['path'].Index)
            }
            foreach ($m in [regex]::Matches($lines[$i], $barePattern)) {
                if ($taken | Where-Object { $m.Index -ge $_ -and $m.Index -le $_ + 8 }) { continue }
                $routes.Add([pscustomobject]@{ Text = $m.Value; Method = $null; Path = $m.Value })
            }
            foreach ($route in $routes) {
                $routesSeen[$route.Text] = $true
                if ($config.Allow.ApiRoutes.ContainsKey($route.Text)) { continue }
                $clean = ($route.Path -replace '[?#].*$', '').TrimEnd('.', ',', ';', ':', '/')
                $abbreviated = $clean -match '^(\.\.\.|…)' -or -not $clean.StartsWith('/api/')
                $written = @(($clean -replace '^(\.\.\.|…)', '').Trim('/') -split '/' | Where-Object { $_ })
                # The version prefix alone ("everything is under /api/v1/") names no route.
                if (-not $written -or (-not $abbreviated -and $written.Count -le 2)) { continue }

                if ($abbreviated) {
                    # A JIM route only if it is the tail of one: anything else (an identity provider's /token) is not ours.
                    $candidates = @($templates | Where-Object {
                            $_.Segments.Count -gt $written.Count -and
                            (Test-RouteMatch -Written $written -Template @($_.Segments[($_.Segments.Count - $written.Count)..($_.Segments.Count - 1)]))
                        } | Where-Object { -not $route.Method -or $route.Method -in $_.Methods })
                    # Prefer the routes whose fixed segments the text matches: "decisions/summary" is the summary
                    # route, though "decisions/{id}" would take it too.
                    if ($candidates) {
                        $scored = foreach ($c in $candidates) {
                            $tail = @($c.Segments[($c.Segments.Count - $written.Count)..($c.Segments.Count - 1)])
                            [pscustomobject]@{ Template = $c; Fixed = @($tail | Where-Object { -not $_.StartsWith('{') }).Count }
                        }
                        $best = ($scored | Measure-Object Fixed -Maximum).Maximum
                        $candidates = @($scored | Where-Object Fixed -eq $best | ForEach-Object Template)
                    }
                    if ($candidates) {
                        $routeCount++
                        $full = ($candidates | ForEach-Object { "'/$($_.Path.Trim('/'))'" } | Sort-Object -Unique) -join ' or '
                        $findings.Add((New-Finding 'ACC-3' $relative ($i + 1) "the route '$($route.Text)' is abbreviated; write it in full, as the API reference names it: $full."))
                    }
                    continue
                }

                $routeCount++
                $matched = @($templates | Where-Object { Test-RouteMatch -Written $written -Template $_.Segments })
                # A path given without a method names a resource ("Connected Systems live under
                # /api/v1/synchronisation/connected-systems"), so the start of a route is enough. With a method it is
                # a call, which must be a whole route.
                if (-not $matched -and -not $route.Method) {
                    $matched = @($templates | Where-Object {
                            $_.Segments.Count -gt $written.Count -and
                            (Test-RouteMatch -Written $written -Template @($_.Segments[0..($written.Count - 1)]))
                        })
                    if ($matched) { continue }
                }
                if (-not $matched) {
                    $findings.Add((New-Finding 'ACC-3' $relative ($i + 1) "the route '$clean' is not in the OpenAPI document; correct it from the API reference, or remove it."))
                }
                elseif ($route.Method -and -not ($matched | Where-Object { $route.Method -in $_.Methods })) {
                    $takes = ($matched.Methods | Sort-Object -Unique) -join ', '
                    $findings.Add((New-Finding 'ACC-3' $relative ($i + 1) "the route '$clean' does not take $($route.Method); it takes $takes."))
                }
            }
        }
    }
    $counts['REST routes in the docs'] = $routeCount
    foreach ($text in $config.Allow.ApiRoutes.Keys) {
        if ([string]::IsNullOrWhiteSpace($config.Allow.ApiRoutes[$text])) {
            $findings.Add((New-Finding 'ACC-3' $allowPath 1 "the allow-list entry for '$text' gives no reason; say why the route is right as written."))
        }
        elseif (-not $routesSeen.ContainsKey($text)) {
            $findings.Add((New-Finding 'ACC-3' $allowPath 1 "the allow-list entry for '$text' names a route no page gives any more; remove it."))
        }
    }
}

if ('COV-4' -in $selected) {
    $apiIndex = Join-Path $root $config.ApiIndex
    $groupRows = @(if (Test-Path -LiteralPath $apiIndex) { Get-DocumentedEntry -FullPath $apiIndex | Where-Object { $_.Kind -eq 'Row' -and $_.Header -match '^Resource group' } })
    $tableLine = if ($groupRows) { $groupRows[0].Line - 2 } else { 1 }
    $groups = foreach ($tag in @($templates.Tags | Sort-Object -Unique)) {
        [pscustomobject]@{ Name = $tag; Path = $config.ApiIndex; Line = $tableLine }
    }
    $counts['API resource groups'] = @($groups).Count
    $groupDocs = foreach ($row in $groupRows) {
        $name = ($row.Text -replace '\[([^\]]*)\]\([^)]*\)', '$1' -replace '`', '').Trim()
        [pscustomobject]@{ Name = $name; Path = $config.ApiIndex; Line = $row.Line }
    }
    Compare-Inventory -Rule 'COV-4' -Inventory @($groups) -Documented @($groupDocs) -Allow $config.Allow.ApiResourceGroups -Noun 'resource group' `
        -Where "a row in the Resource groups table on $($config.ApiIndex)" `
        -GoneMessage "names '{0}', which is not a resource group of the API; remove the row, or correct the name." |
        ForEach-Object { $findings.Add($_) }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

$inActions = $env:GITHUB_ACTIONS -eq 'true'
function ConvertTo-AnnotationText { param([string]$Text) $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

# ---------------------------------------------------------------------------
# LANG-10: the API reference names nothing JIM is built with
# ---------------------------------------------------------------------------

# The API reference publishes every summary and description in the OpenAPI document, which come from the XML
# comments on the controllers and DTOs, and the PowerShell reference tables the same descriptions. A framework
# name in one reaches the reader as surely as one on a page, so the conformance check's list applies here too.
function Get-OpenApiText {
    param($Node, [string]$Pointer)
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $child = $Node[$key]
            if ($key -in 'summary', 'description' -and $child -is [string]) { [pscustomobject]@{ Pointer = "$Pointer/$key"; Text = $child } }
            else { Get-OpenApiText -Node $child -Pointer "$Pointer/$key" }
        }
    }
    elseif ($Node -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $Node.Count; $i++) { Get-OpenApiText -Node $Node[$i] -Pointer "$Pointer/$i" }
    }
}

if ('LANG-10' -in $selected) {
    $conformance = Import-PowerShellDataFile (Join-Path $root $ConformanceConfigPath)
    $terms = @($conformance.InternalTermPatterns | ForEach-Object { [regex]::new($_, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) })
    $document = Get-Content -LiteralPath $OpenApiPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in Get-OpenApiText -Node $document -Pointer '') {
        # Code is what the reader types, as it is on a page.
        $text = [regex]::Replace($entry.Text, '(`+).+?\1|<c>.*?</c>|<code>.*?</code>', ' ')
        foreach ($term in $terms) {
            foreach ($m in $term.Matches($text)) {
                $findings.Add((New-Finding 'LANG-10' 'OpenAPI document' 1 "$($entry.Pointer) names how JIM is built ('$($m.Value)'); the API and PowerShell references publish it. Say what it is to the reader in the XML comment it comes from (VOI-6)."))
            }
        }
    }
}

foreach ($f in $findings | Sort-Object Path, Line, Rule) {
    Write-Host "ERROR   $($f.Path):$($f.Line)  $($f.Rule)  $($f.Message)" -ForegroundColor Red
    if ($inActions) { Write-Host "::error file=$($f.Path),line=$($f.Line),title=$($f.Rule)::$(ConvertTo-AnnotationText $f.Message)" }
}

$checked = ($counts.GetEnumerator() | ForEach-Object { "$($_.Value) $($_.Key)" }) -join ', '
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add('## Docs coverage')
    $summary.Add('')
    $summary.Add("Checked $checked, against source and the OpenAPI document (``$script:StandardPath``, Coverage, ACC-3, LNK-3).")
    $summary.Add('')
    $summary.Add('| Rule | Errors |')
    $summary.Add('|------|-------:|')
    foreach ($r in $selected) { $summary.Add("| $r | $(@($findings | Where-Object Rule -eq $r).Count) |") }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n") -Encoding utf8
}

if ($apiNote) { Write-Host $apiNote }

if ($findings.Count -gt 0) {
    Write-Host "`nDocs coverage FAILED: $($findings.Count) gap(s). Each names its rule; the rules are defined in $script:StandardPath." -ForegroundColor Red
    exit 1
}
Write-Host "`nDocs coverage passed: checked $checked." -ForegroundColor Green
exit 0
