# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks the diagrams docs/hooks/diagrams.py renders, in a real browser.

.DESCRIPTION
    The diagram renderer sizes every box from committed font metrics, so whether a label really
    fits, and whether the right layout shows on a phone, can only be answered by rendering it.
    This renders the fixture specs in docs/hooks/tests/fixtures/ through the renderer's command
    line, loads them with the site's own custom.css and webfonts, and asserts, at the reading
    width of a desktop window, at the full width of one and at phone width:

      - exactly one layout of each diagram shows: the wide one on a desktop, the narrow on a phone;
      - every label sits inside its box;
      - no two labels overlap;
      - no line runs through a label;
      - text renders at 10px or more on screen;
      - the page never scrolls sideways.

    It needs Python 3 (no packages: the renderer is standard library only), a browser (the one
    .devcontainer/setup.sh installs for the Playwright MCP server), and network access to
    fonts.googleapis.com for the webfonts the metrics were measured from.

.EXAMPLE
    pwsh -File ./scripts/Test-Diagrams.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cssPath = Join-Path $repoRoot 'docs/assets/stylesheets/custom.css'
$renderer = Join-Path $repoRoot 'docs/hooks/diagrams.py'
$fixtures = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs/hooks/tests/fixtures') -Filter '*.json' | Sort-Object Name)
if (-not $fixtures) { throw 'No diagram fixtures found in docs/hooks/tests/fixtures.' }

$python = (Get-Command python3 -ErrorAction SilentlyContinue) ?? (Get-Command python -ErrorAction SilentlyContinue)
if (-not $python) { throw 'Python 3 is needed to run the diagram renderer.' }

$fragments = foreach ($fixture in $fixtures) {
    $out = & $python.Source $renderer $fixture.FullName
    if ($LASTEXITCODE -ne 0) { throw "The renderer failed on $($fixture.Name)." }
    $out -join "`n"
}
$fragmentPath = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-diagrams-{0}.html" -f ([guid]::NewGuid().ToString('n')))
Set-Content -LiteralPath $fragmentPath -Value ($fragments -join "`n") -Encoding utf8

. (Join-Path $PSScriptRoot 'JimPlaywright.ps1')
$playwrightModule = Resolve-PlaywrightModule

$driver = @'
const fs = require('fs');
const { chromium } = require(process.env.JIM_PW_MODULE);
const css = fs.readFileSync(process.env.JIM_CSS_PATH, 'utf8');
const body = fs.readFileSync(process.env.JIM_FRAGMENTS, 'utf8');

// The parts of Material's page the diagrams sit in: a typeset article in a content column.
const page = (column) => `<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;700&family=IBM+Plex+Sans:wght@400;600;700&display=block">
<style>${css}
body { margin: 0; font-family: "IBM Plex Sans", sans-serif; }
.md-content__inner { box-sizing: border-box; max-width: ${column}; margin: 0 auto; padding: 0 16px; }
</style></head><body data-md-color-scheme="default">
<article class="md-content__inner md-typeset">${body}</article></body></html>`;

const VIEWS = [
  { name: 'desktop reading width', width: 1440, column: '720px', layout: 'wide' },
  { name: 'desktop full width', width: 1920, column: 'none', layout: 'wide' },
  { name: 'phone', width: 390, column: 'none', layout: 'narrow' },
];

(async () => {
  const browser = await chromium.launch();
  const findings = [];
  for (const view of VIEWS) {
    const tab = await browser.newPage({ viewport: { width: view.width, height: 900 } });
    await tab.setContent(page(view.column), { waitUntil: 'networkidle' });
    await tab.evaluate(() => document.fonts.ready);
    const result = await tab.evaluate((expected) => {
      const out = [];
      const box = (el) => el.getBoundingClientRect();
      const inside = (a, b, slack) => a.left >= b.left - slack && a.right <= b.right + slack && a.top >= b.top - slack && a.bottom <= b.bottom + slack;
      const overlap = (a, b) => Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 && Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1;
      if (document.documentElement.scrollWidth > window.innerWidth) out.push(`the page scrolls sideways by ${document.documentElement.scrollWidth - window.innerWidth}px`);
      document.querySelectorAll('.jim-dg').forEach((dg, i) => {
        const name = `diagram ${i + 1} ("${dg.querySelector('title').textContent}")`;
        const shown = [...dg.querySelectorAll('svg')].filter(s => s.getBoundingClientRect().width > 0);
        if (shown.length !== 1) { out.push(`${name}: ${shown.length} layouts show, not one`); return; }
        const svg = shown[0];
        if (!svg.classList.contains(`jim-dg-${expected}`)) out.push(`${name}: the ${svg.classList.contains('jim-dg-wide') ? 'wide' : 'narrow'} layout shows, not the ${expected} one`);
        const scale = svg.getBoundingClientRect().width / svg.viewBox.baseVal.width;
        const texts = [...svg.querySelectorAll('text')];
        for (const t of texts) {
          const px = parseFloat(getComputedStyle(t).fontSize) * scale;
          if (px < 10 - 0.05) out.push(`${name}: "${t.textContent}" renders at ${px.toFixed(1)}px, under 10px`);
          // Painted over by a shape drawn after it (a container's fill, say): still laid out, never seen.
          // Scrolled into view first, because elementFromPoint only sees the viewport.
          t.scrollIntoView({ block: 'center' });
          const r = box(t);
          if (!inside(r, box(svg), 1)) out.push(`${name}: "${t.textContent}" runs outside the diagram`);
          const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
          if (!top || !(top === t || t.contains(top))) out.push(`${name}: "${t.textContent}" is hidden under another shape`);
        }
        svg.querySelectorAll('.jimdg-dg-node').forEach(node => {
          const r = box(node.querySelector('rect'));
          node.querySelectorAll('text').forEach(t => {
            if (t.classList.contains('jimdg-dg-badge-text')) return;
            if (!inside(box(t), r, 1)) out.push(`${name}: "${t.textContent}" does not fit its box`);
          });
        });
        for (let a = 0; a < texts.length; a++) for (let b = a + 1; b < texts.length; b++) {
          if (overlap(box(texts[a]), box(texts[b]))) out.push(`${name}: "${texts[a].textContent}" overlaps "${texts[b].textContent}"`);
        }
        const m = svg.getScreenCTM();
        svg.querySelectorAll('polyline').forEach(line => {
          const pts = line.getAttribute('points').trim().split(/\s+/).map(p => p.split(',').map(Number))
            .map(([x, y]) => ({ x: m.a * x + m.e, y: m.d * y + m.f }));
          for (let s = 0; s < pts.length - 1; s++) {
            const seg = { left: Math.min(pts[s].x, pts[s + 1].x), right: Math.max(pts[s].x, pts[s + 1].x),
                          top: Math.min(pts[s].y, pts[s + 1].y), bottom: Math.max(pts[s].y, pts[s + 1].y) };
            for (const t of texts) {
              const r = box(t);
              const crosses = seg.right >= r.left + 1 && seg.left <= r.right - 1 && seg.bottom >= r.top + 1 && seg.top <= r.bottom - 1;
              if (crosses) out.push(`${name}: a line runs through "${t.textContent}"`);
            }
          }
        });
      });
      return out;
    }, view.layout);
    for (const f of result) findings.push(`${view.name}: ${f}`);
    await tab.close();
  }
  await browser.close();
  process.stdout.write(JSON.stringify({ findings }));
})().catch(err => { console.error((err && err.stack) || String(err)); process.exit(1); });
'@

$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-diagrams-{0}.cjs" -f ([guid]::NewGuid().ToString('n')))
Set-Content -LiteralPath $tempScript -Value $driver -Encoding utf8
try {
    $env:JIM_PW_MODULE = $playwrightModule
    $env:JIM_CSS_PATH = $cssPath
    $env:JIM_FRAGMENTS = $fragmentPath
    $json = & node $tempScript
    if ($LASTEXITCODE -ne 0) { throw "The diagram check failed to run (node exited $LASTEXITCODE)." }
}
finally {
    Remove-Item -LiteralPath $tempScript, $fragmentPath -Force -ErrorAction SilentlyContinue
    Remove-Item Env:JIM_PW_MODULE, Env:JIM_CSS_PATH, Env:JIM_FRAGMENTS -ErrorAction SilentlyContinue
}

$result = $json | ConvertFrom-Json
if ($result.findings.Count -gt 0) {
    Write-Host ''
    Write-Host ("Diagram check failed ({0} finding{1}):" -f $result.findings.Count, $(if ($result.findings.Count -eq 1) { '' } else { 's' })) -ForegroundColor Red
    foreach ($finding in $result.findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'See docs/hooks/diagrams.py and the "Generated diagrams" rules in custom.css.' -ForegroundColor Red
    exit 1
}
Write-Host "Diagram check passed: $($fixtures.Count) fixture file(s) render cleanly at every width." -ForegroundColor Green
