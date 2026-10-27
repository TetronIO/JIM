# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Writes one HTML contact sheet of every diagram on a set of docs pages, for reviewing a batch.

.DESCRIPTION
    A batch of diagrams (a docs section at a time; see
    engineering/plans/doing/DOCS_DIAGRAM_ON_EVERY_PAGE.md) is approved from one page that shows
    them all, rather than by opening every page in two themes and on a phone. For each page this
    finds every diagram, in page order:

      - each ```jim-diagram spec, rendered with docs/hooks/diagrams.py, as the build renders it;
      - each concept diagram included from docs/assets/diagrams/ (--8<-- "assets/diagrams/x.svg");
      - each Mermaid diagram and inline SVG, which are listed but not drawn (preview the page).

    It then photographs each drawn diagram in a real browser, with the site's custom.css and
    webfonts: in the light and the dark theme at the 720px reading column of a desktop window, and
    on a 390px phone. The sheet is one self-contained HTML file (every image a data URI), grouped
    by page: the page's title and path, then each diagram's title with its light, dark and phone
    images side by side. A page with no diagram says so, and a spec that does not render shows
    its error in place.

    No MkDocs build is needed. It needs Python 3 with PyYAML (pip install -r
    docs/requirements.txt), and, when there is a diagram to draw, the browser
    .devcontainer/setup.sh installs for the Playwright MCP server and network access to
    fonts.googleapis.com for the webfonts.

    Exit codes: 0 the sheet was written; 1 it was written, but a diagram did not render.

.PARAMETER Section
    A folder under docs/, such as configuration. Every page in it, and in its subfolders, is
    included, the section's index.md first.

.PARAMETER Path
    One or more pages, as paths to the .md files (docs/configuration/api-keys.md) or relative to
    docs/ (configuration/api-keys.md).

.PARAMETER OutFile
    Where to write the sheet. Defaults to jim-diagrams-<section>.html in the system temporary
    folder. The path is printed when the sheet is written.

.EXAMPLE
    pwsh -File ./scripts/New-DiagramContactSheet.ps1 -Section configuration

.EXAMPLE
    pwsh -File ./scripts/New-DiagramContactSheet.ps1 -Path docs/configuration/api-keys.md, docs/configuration/roles.md -OutFile ./sheet.html
#>
[CmdletBinding(DefaultParameterSetName = 'Section')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Section')]
    [string]$Section,

    [Parameter(Mandatory, ParameterSetName = 'Path')]
    [string[]]$Path,

    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$docsRoot = Join-Path $repoRoot 'docs'
$cssPath = Join-Path $docsRoot 'assets/stylesheets/custom.css'

# --- The pages, relative to docs/ ---

if ($PSCmdlet.ParameterSetName -eq 'Section') {
    $sectionRoot = Join-Path $docsRoot $Section
    if (-not (Test-Path -LiteralPath $sectionRoot -PathType Container)) { throw "There is no docs section '$Section' (looked for $sectionRoot)." }
    $pages = @(Get-ChildItem -LiteralPath $sectionRoot -Filter '*.md' -Recurse -File |
        ForEach-Object { [System.IO.Path]::GetRelativePath($docsRoot, $_.FullName) -replace '\\', '/' } |
        Where-Object { $_ -notmatch '(^|/)CLAUDE\.md$' } |
        Sort-Object { if ($_ -match '(^|/)index\.md$') { 0 } else { 1 } }, { $_ })
    $label = $Section.Trim('/')
}
else {
    $pages = @(foreach ($p in $Path) {
            $candidate = @($p, (Join-Path $docsRoot $p)) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if (-not $candidate) { throw "Page not found: '$p' (give a path to the .md file, or one relative to docs/)." }
            $relative = [System.IO.Path]::GetRelativePath($docsRoot, (Resolve-Path -LiteralPath $candidate).Path) -replace '\\', '/'
            if ($relative.StartsWith('..')) { throw "'$p' is not a page under docs/." }
            $relative
        })
    $label = if ($pages.Count -eq 1) { $pages[0] -replace '\.md$', '' } else { "$($pages.Count) pages" }
}
if (-not $pages) { throw 'No pages to put on the contact sheet.' }
if (-not $OutFile) {
    $slug = ($label.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    $OutFile = Join-Path ([System.IO.Path]::GetTempPath()) "jim-diagrams-$slug.html"
}

# --- Find and render each page's diagrams (Python, with the build's own renderer) ---

# Fences are tracked line by line, as Test-DocsConformance.ps1 tracks them, so a jim-diagram
# shown inside a ````markdown example is not mistaken for a diagram.
$helper = @'
import json
import re
import sys
from pathlib import Path

repo, page_list, out_path = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
docs = repo / "docs"
sys.path.insert(0, str(docs / "hooks"))
try:
    import yaml
except ImportError:
    sys.stderr.write("PyYAML is needed to read the diagram specs: pip install -r docs/requirements.txt\n")
    sys.exit(2)
import diagrams

FENCE = re.compile(r"^\s*(`{3,}|~{3,})(.*)$")
SNIPPET = re.compile(r'^\s*--8<--\s*"(?:docs/)?(assets/diagrams/[^"]+\.svg)"')
INLINE_SVG = re.compile(r'<svg\b[^>]*\bclass\s*=\s*"(?:[^"]*\s)?jim-diagram(?:\s[^"]*)?"')
SVG_TITLE = re.compile(r"<title[^>]*>(.*?)</title>", re.DOTALL)


def page_title(lines, body_start, fallback):
    if body_start:
        try:
            front = yaml.safe_load("\n".join(lines[1:body_start - 1])) or {}
            if isinstance(front, dict) and front.get("title"):
                return str(front["title"])
        except yaml.YAMLError:
            pass
    for line in lines[body_start:]:
        if line.startswith("# "):
            return line[2:].strip()
    return fallback


def render_spec(relative, fence, number):
    entry = {"kind": "jim-diagram", "line": fence["line"], "title": f"Diagram {number}"}
    try:
        spec = yaml.safe_load("\n".join(fence["content"]))
        if isinstance(spec, dict) and spec.get("title"):
            entry["title"] = str(spec["title"])
        if isinstance(spec, dict) and spec.get("archetype"):
            entry["kind"] = f"jim-diagram: {spec['archetype']}"
        entry["html"] = diagrams.render(spec, f"sheet-{diagrams.page_slug(relative)}-{number}")
    except Exception as err:  # a spec error, or a YAML one: show it, and carry on
        entry["error"] = str(err)
    return entry


def read_page(relative):
    lines = (docs / relative).read_text(encoding="utf-8").splitlines()
    body_start = 0
    if lines and lines[0].strip() == "---":
        for i in range(1, len(lines)):
            if lines[i].strip() == "---":
                body_start = i + 1
                break
    found = []
    open_fence = None
    for i in range(body_start, len(lines)):
        line = lines[i]
        match = FENCE.match(line)
        if match and open_fence is None:
            open_fence = {"marker": match.group(1), "info": match.group(2).strip(), "line": i + 1, "content": []}
            continue
        if open_fence is not None:
            closes = match and match.group(1)[0] == open_fence["marker"][0] \
                and len(match.group(1)) >= len(open_fence["marker"]) and not match.group(2).strip()
            if not closes:
                open_fence["content"].append(line)
                continue
            info = open_fence["info"]
            if re.match(r"^jim-diagram\b", info):
                found.append(render_spec(relative, open_fence, len(found) + 1))
            elif re.match(r"^mermaid\b", info):
                found.append({"kind": "Mermaid", "line": open_fence["line"], "title": "Mermaid diagram",
                              "note": "Mermaid diagrams are not drawn on the contact sheet; preview the page."})
            open_fence = None
            continue
        snippet = SNIPPET.match(line)
        if snippet:
            svg_path = docs / snippet.group(1)
            if svg_path.is_file():
                svg = svg_path.read_text(encoding="utf-8")
                title = SVG_TITLE.search(svg)
                found.append({"kind": "Concept diagram", "line": i + 1,
                              "title": title.group(1).strip() if title else snippet.group(1), "html": svg})
            else:
                found.append({"kind": "Concept diagram", "line": i + 1, "title": snippet.group(1),
                              "error": f"{snippet.group(1)} does not exist"})
        elif INLINE_SVG.search(line):
            found.append({"kind": "Inline SVG", "line": i + 1, "title": "Inline diagram",
                          "note": "Inline SVG in the page is not drawn on the contact sheet; preview the page."})
    return {"path": relative, "title": page_title(lines, body_start, relative), "diagrams": found}


pages = [read_page(p) for p in json.loads(page_list.read_text(encoding="utf-8"))]
out_path.write_text(json.dumps(pages), encoding="utf-8")
'@

# The parts of Material's page the diagrams sit in, as scripts/Test-Diagrams.ps1 builds it, and
# one photograph per diagram per view.
$driver = @'
const fs = require('fs');
const { chromium } = require(process.env.JIM_PW_MODULE);
const css = fs.readFileSync(process.env.JIM_CSS_PATH, 'utf8');
const items = JSON.parse(fs.readFileSync(process.env.JIM_SHEET_ITEMS, 'utf8'));

const page = (column, scheme) => `<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;700&family=IBM+Plex+Sans:wght@400;600;700&display=block">
<style>${css}
body { margin: 0; font-family: "IBM Plex Sans", sans-serif; background: var(--md-default-bg-color); }
.md-content__inner { box-sizing: border-box; max-width: ${column}; margin: 0 auto; padding: 0 16px; }
.jim-sheet-item { padding: 8px 0; }
</style></head><body data-md-color-scheme="${scheme}">
<article class="md-content__inner md-typeset">
${items.map((item) => `<section class="jim-sheet-item" id="${item.id}">${item.html}</section>`).join('\n')}
</article></body></html>`;

const VIEWS = [
  { key: 'light', width: 1440, column: '720px', scheme: 'default' },
  { key: 'dark', width: 1440, column: '720px', scheme: 'slate' },
  { key: 'phone', width: 390, column: 'none', scheme: 'default' },
];

(async () => {
  const browser = await chromium.launch();
  const shots = {};
  for (const item of items) shots[item.id] = {};
  for (const view of VIEWS) {
    const tab = await browser.newPage({ viewport: { width: view.width, height: 900 } });
    await tab.setContent(page(view.column, view.scheme), { waitUntil: 'networkidle', timeout: 60000 });
    await tab.evaluate(() => document.fonts.ready);
    for (const item of items) {
      const png = await tab.locator(`#${item.id}`).screenshot({ animations: 'disabled' });
      shots[item.id][view.key] = png.toString('base64');
    }
    await tab.close();
  }
  await browser.close();
  fs.writeFileSync(process.env.JIM_SHEET_SHOTS, JSON.stringify(shots));
})().catch(err => { console.error((err && err.stack) || String(err)); process.exit(1); });
'@

function Format-HtmlText { param([string]$Text) [System.Net.WebUtility]::HtmlEncode($Text) }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-contact-sheet-{0}" -f ([guid]::NewGuid().ToString('n')))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $python = (Get-Command python3 -ErrorAction SilentlyContinue) ?? (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $python) { throw 'Python 3 is needed to run the diagram renderer.' }
    $helperPath = Join-Path $work 'find-diagrams.py'
    $listPath = Join-Path $work 'pages.json'
    $foundPath = Join-Path $work 'diagrams.json'
    Set-Content -LiteralPath $helperPath -Value $helper -Encoding utf8
    Set-Content -LiteralPath $listPath -Value (ConvertTo-Json -InputObject @($pages) -Compress) -Encoding utf8
    & $python.Source $helperPath $repoRoot $listPath $foundPath
    if ($LASTEXITCODE -ne 0) { throw "Reading the pages' diagrams failed (Python exited $LASTEXITCODE)." }
    $found = @(Get-Content -LiteralPath $foundPath -Raw | ConvertFrom-Json)

    # Every diagram there is something to draw for, under an id the browser can find it by.
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($p in $found) {
        foreach ($d in @($p.diagrams)) {
            if ($d.PSObject.Properties['html']) {
                $id = "d$($items.Count + 1)"
                $d | Add-Member -NotePropertyName Id -NotePropertyValue $id
                $items.Add([pscustomobject]@{ id = $id; html = $d.html })
            }
        }
    }

    $shots = $null
    if ($items.Count -gt 0) {
        . (Join-Path $PSScriptRoot 'JimPlaywright.ps1')
        $playwrightModule = Resolve-PlaywrightModule
        $itemsPath = Join-Path $work 'items.json'
        $shotsPath = Join-Path $work 'shots.json'
        $driverPath = Join-Path $work 'shoot.cjs'
        Set-Content -LiteralPath $itemsPath -Value (ConvertTo-Json -InputObject $items.ToArray() -Compress) -Encoding utf8
        Set-Content -LiteralPath $driverPath -Value $driver -Encoding utf8
        try {
            $env:JIM_PW_MODULE = $playwrightModule
            $env:JIM_CSS_PATH = $cssPath
            $env:JIM_SHEET_ITEMS = $itemsPath
            $env:JIM_SHEET_SHOTS = $shotsPath
            & node $driverPath
            if ($LASTEXITCODE -ne 0) { throw "Photographing the diagrams failed (node exited $LASTEXITCODE)." }
        }
        finally {
            Remove-Item Env:JIM_PW_MODULE, Env:JIM_CSS_PATH, Env:JIM_SHEET_ITEMS, Env:JIM_SHEET_SHOTS -ErrorAction SilentlyContinue
        }
        $shots = Get-Content -LiteralPath $shotsPath -Raw | ConvertFrom-Json
    }

    # --- The sheet ---

    $diagramCount = 0
    $withoutCount = 0
    $failedCount = 0
    $body = [System.Text.StringBuilder]::new()
    foreach ($p in $found) {
        [void]$body.AppendLine('<section class="page">')
        [void]$body.AppendLine("<h2>$(Format-HtmlText $p.title)</h2>")
        [void]$body.AppendLine("<p class=`"path`">docs/$(Format-HtmlText $p.path)</p>")
        $diagrams = @($p.diagrams)
        if ($diagrams.Count -eq 0) {
            $withoutCount++
            [void]$body.AppendLine('<p class="none">No diagram yet.</p>')
        }
        foreach ($d in $diagrams) {
            $diagramCount++
            [void]$body.AppendLine('<div class="diagram">')
            [void]$body.AppendLine("<h3>$(Format-HtmlText $d.title) <span class=`"kind`">$(Format-HtmlText $d.kind), line $($d.line)</span></h3>")
            if ($d.PSObject.Properties['error']) {
                $failedCount++
                [void]$body.AppendLine("<p class=`"error`">$(Format-HtmlText $d.error)</p>")
            }
            elseif ($d.PSObject.Properties['note']) {
                [void]$body.AppendLine("<p class=`"none`">$(Format-HtmlText $d.note)</p>")
            }
            else {
                $set = $shots.($d.Id)
                [void]$body.AppendLine('<div class="views">')
                foreach ($view in @(@{ Key = 'light'; Label = 'Light, 720px column' }, @{ Key = 'dark'; Label = 'Dark, 720px column' }, @{ Key = 'phone'; Label = 'Phone, 390px' })) {
                    [void]$body.AppendLine("<figure class=`"$($view.Key)`"><figcaption>$($view.Label)</figcaption><img alt=`"$(Format-HtmlText "$($d.title) ($($view.Label))")`" src=`"data:image/png;base64,$($set.($view.Key))`"></figure>")
                }
                [void]$body.AppendLine('</div>')
            }
            [void]$body.AppendLine('</div>')
        }
        [void]$body.AppendLine('</section>')
    }

    $title = "Diagram contact sheet: $label"
    $summary = "$($found.Count) page$(if ($found.Count -ne 1) { 's' }), $diagramCount diagram$(if ($diagramCount -ne 1) { 's' }), $withoutCount page$(if ($withoutCount -ne 1) { 's' }) with no diagram yet" +
        $(if ($failedCount) { ", $failedCount diagram$(if ($failedCount -ne 1) { 's' }) that did not render" } else { '' }) +
        ". Generated $((Get-Date).ToString('d MMMM yyyy HH:mm', [System.Globalization.CultureInfo]::GetCultureInfo('en-GB')))."
    $html = @"
<!doctype html>
<html lang="en-GB">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Format-HtmlText $title)</title>
<style>
:root { --fg: #1b1f24; --muted: #59636e; --line: #d1d9e0; --bg: #ffffff; --bad: #b42318; }
body { margin: 0; padding: 24px 16px 48px; font: 15px/1.5 system-ui, sans-serif; color: var(--fg); background: var(--bg); }
main { max-width: 1900px; margin: 0 auto; }
h1 { font-size: 1.5rem; margin: 0 0 4px; }
.summary { color: var(--muted); margin: 0 0 24px; }
.page { border-top: 1px solid var(--line); padding: 20px 0; }
.page h2 { font-size: 1.2rem; margin: 0; }
.path { color: var(--muted); font-family: ui-monospace, monospace; font-size: 0.85rem; margin: 0; }
.diagram h3 { font-size: 1rem; margin: 16px 0 8px; }
.kind { color: var(--muted); font-weight: normal; font-size: 0.85rem; }
.views { display: grid; grid-template-columns: minmax(0, 720fr) minmax(0, 720fr) minmax(0, 390fr); gap: 16px; align-items: start; }
figure { margin: 0; }
figcaption { color: var(--muted); font-size: 0.8rem; margin-bottom: 4px; }
figure img { display: block; max-width: 100%; height: auto; border: 1px solid var(--line); }
.none { color: var(--muted); font-style: italic; }
.error { color: var(--bad); white-space: pre-wrap; font-family: ui-monospace, monospace; font-size: 0.85rem; }
@media (max-width: 900px) { .views { grid-template-columns: minmax(0, 1fr); } .views .phone { max-width: 390px; } }
</style>
</head>
<body>
<main>
<h1>$(Format-HtmlText $title)</h1>
<p class="summary">$(Format-HtmlText $summary)</p>
$($body.ToString())
</main>
</body>
</html>
"@
    $outDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutFile))
    if (-not (Test-Path -LiteralPath $outDirectory)) { New-Item -ItemType Directory -Path $outDirectory -Force | Out-Null }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutFile), $html, [System.Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Contact sheet written: $([System.IO.Path]::GetFullPath($OutFile))" -ForegroundColor Green
Write-Host "  $summary"
if ($failedCount -gt 0) {
    Write-Host "$failedCount diagram$(if ($failedCount -ne 1) { 's' }) did not render; the sheet shows each error in place." -ForegroundColor Red
    exit 1
}
