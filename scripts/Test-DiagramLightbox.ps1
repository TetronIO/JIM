# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails when a concept diagram opens cropped in the docs site's diagram lightbox.

.DESCRIPTION
    Clicking a diagram opens it in an overlay (docs/assets/javascripts/diagram-zoom.js). The
    overlay used to treat 100% as fit-to-width for everything, so any diagram taller than the
    stage opened with its bottom cut off. For a concept SVG that is a small crop and reads as a
    rendering fault rather than as something you can pan: hub-and-spoke is 1160x640, and on a
    2.01-aspect stage it opened losing the bottom 10%, including part of the Metaverse box.

    The overlay now opens on the whole diagram unless containing it would leave it narrower than
    MIN_CONTAIN_SCALE of the stage, which keeps the reason fit-to-width existed - a long Mermaid
    flowchart contained is a thumbnail, so those still open full width and pan.

    Whether any given diagram lands on the right side of that rule depends on its aspect ratio
    against the stage's, and the stage's depends on the reader's window: layoutStage matches the
    stage to the diagram's aspect only while the window is tall enough, and falls back to the
    available height otherwise. So this cannot be reasoned about per diagram - it has to be
    exercised, at more than one window size. Two of the sixteen diagrams passed at every ordinary
    window and still cropped at 1440x620.

    What this asserts, per diagram per viewport:
      - every concept diagram opens with its whole viewBox visible; and
      - a synthetic 800x4000 control still opens fit-to-width and panable wherever the stage is
        wider than it is, so a well-meaning "just always contain it" cannot land unnoticed.

    Browser resolution is shared with Update-DiagramLabelCuts.ps1; see JimPlaywright.ps1.

.PARAMETER Path
    Directory holding the concept diagrams. Defaults to docs/assets/diagrams.

.PARAMETER Viewport
    Viewports to exercise, each "WIDTHxHEIGHT". The defaults span a phone, a tablet, small and
    large desktops, and two deliberately short windows, which is where the failures live.

.EXAMPLE
    pwsh -File ./scripts/Test-DiagramLightbox.ps1
    Checks every concept diagram at every default viewport.

.EXAMPLE
    pwsh -File ./scripts/Test-DiagramLightbox.ps1 -Viewport 1440x620
    Checks only the short-window case.
#>
[CmdletBinding()]
param(
    [string]$Path = 'docs/assets/diagrams',

    [string[]]$Viewport = @('390x844', '834x1112', '1024x768', '1280x800', '1440x620', '1512x945', '1920x1080')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cssPath = Join-Path $repoRoot 'docs/assets/stylesheets/custom.css'
$jsPath = Join-Path $repoRoot 'docs/assets/javascripts/diagram-zoom.js'
$diagramDir = Join-Path $repoRoot $Path

foreach ($required in @($cssPath, $jsPath, $diagramDir)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Not found: $required" }
}

. (Join-Path $PSScriptRoot 'JimPlaywright.ps1')
$playwrightModule = Resolve-PlaywrightModule

$viewports = foreach ($entry in $Viewport) {
    if ($entry -notmatch '^(?<w>\d+)x(?<h>\d+)$') { throw "Viewport must be WIDTHxHEIGHT, got '$entry'" }
    # Leading comma stops the pipeline unrolling the pair into two loose integers.
    , @([int]$Matches.w, [int]$Matches.h)
}

$driver = @'
const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.JIM_PW_MODULE);

const css = fs.readFileSync(process.env.JIM_CSS_PATH, 'utf8');
const js = fs.readFileSync(process.env.JIM_JS_PATH, 'utf8');
const dir = process.env.JIM_DIAGRAM_DIR;
const viewports = JSON.parse(process.env.JIM_VIEWPORTS);

const names = fs.readdirSync(dir).filter(f => f.endsWith('.svg')).sort();
const svgs = names.map(n => fs.readFileSync(path.join(dir, n), 'utf8'));

// A stand-in for the diagrams the fit-to-width rule exists for: far taller than
// any stage, so containing it would render it as a thumbnail.
const CONTROL = '(tall control 800x4000)';
const control = '<svg class="jim-diagram" viewBox="0 0 800 4000" role="img" aria-labelledby="jimdg-control">' +
  '<title id="jimdg-control">Tall control</title>' +
  '<rect class="jimdg-stage" x="10" y="10" width="780" height="3980"/></svg>';
const all = names.concat([CONTROL]);

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
${css}
body { margin: 0; }
.md-typeset { padding: 12px; }
.jim-diagram { width: 100%; height: auto; }
</style></head><body data-md-color-scheme="default"><div class="md-typeset">
${svgs.join('\n')}${control}
</div><script>${js}<\/script></body></html>`;

(async () => {
  const browser = await chromium.launch();
  const findings = [];
  let checks = 0;

  for (const [width, height] of viewports) {
    const context = await browser.newContext({ viewport: { width, height } });
    const page = await context.newPage();
    await page.setContent(html, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts && document.fonts.ready);
    await page.waitForTimeout(150);

    for (let i = 0; i < all.length; i++) {
      const r = await page.evaluate(async (i) => {
        const trigger = document.querySelectorAll('.jim-dgz-trigger')[i];
        if (!trigger) return null;
        trigger.click();
        await new Promise(r => setTimeout(r, 150));
        const overlaySvg = document.querySelector('.jim-dgz__svg');
        const view = overlaySvg.getAttribute('viewBox').split(/[\s,]+/).map(Number);
        const base = document.querySelectorAll('.jim-dgz-trigger svg.jim-diagram')[i]
          .getAttribute('viewBox').split(/[\s,]+/).map(Number);
        const stage = document.querySelector('.jim-dgz__stage').getBoundingClientRect();
        // Half a user unit of slack absorbs the float maths in the view fit.
        const whole = view[0] <= base[0] + 0.5 && view[1] <= base[1] + 0.5 &&
          view[0] + view[2] >= base[0] + base[2] - 0.5 &&
          view[1] + view[3] >= base[1] + base[3] - 0.5;
        const shownH = Math.min(view[1] + view[3], base[1] + base[3]) - Math.max(view[1], base[1]);
        document.querySelector('.jim-dgz__close').click();
        await new Promise(r => setTimeout(r, 100));
        return {
          whole,
          pctHeight: Math.round((shownH / base[3]) * 100),
          aspect: +(base[2] / base[3]).toFixed(2),
          stageAspect: +(stage.width / stage.height).toFixed(2)
        };
      }, i);
      if (!r) continue;
      checks++;

      const name = all[i];
      const label = `${width}x${height} ${name}`;
      if (name === CONTROL) {
        // Only meaningful where the stage is wider than the control; on a tall
        // phone stage there is nothing to fall back from.
        if (r.stageAspect > r.aspect && r.whole) {
          findings.push(`${label}: opened whole (stage aspect ${r.stageAspect}); the fit-to-width fallback for very tall diagrams has stopped applying`);
        }
      } else if (!r.whole) {
        findings.push(`${label}: opens cropped, showing ${r.pctHeight}% of its height (diagram aspect ${r.aspect}, stage aspect ${r.stageAspect})`);
      }
    }
    await context.close();
  }

  await browser.close();
  process.stdout.write(JSON.stringify({ checks, diagrams: names.length, findings }));
})().catch(err => { console.error((err && err.stack) || String(err)); process.exit(1); });
'@

$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-lightbox-{0}.cjs" -f ([guid]::NewGuid().ToString('n')))
Set-Content -LiteralPath $tempScript -Value $driver -Encoding utf8

try {
    $env:JIM_PW_MODULE = $playwrightModule
    $env:JIM_CSS_PATH = $cssPath
    $env:JIM_JS_PATH = $jsPath
    $env:JIM_DIAGRAM_DIR = (Resolve-Path -LiteralPath $diagramDir).Path
    $env:JIM_VIEWPORTS = ($viewports | ConvertTo-Json -Compress -AsArray)

    $json = & node $tempScript
    if ($LASTEXITCODE -ne 0) { throw "Lightbox check failed to run (node exited $LASTEXITCODE)." }
}
finally {
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
    Remove-Item Env:JIM_PW_MODULE, Env:JIM_CSS_PATH, Env:JIM_JS_PATH, Env:JIM_DIAGRAM_DIR, Env:JIM_VIEWPORTS `
        -ErrorAction SilentlyContinue
}

$result = $json | ConvertFrom-Json

if ($result.findings.Count -gt 0) {
    Write-Host ''
    Write-Host ("Diagram lightbox check failed ({0} finding{1}):" -f `
            $result.findings.Count, $(if ($result.findings.Count -eq 1) { '' } else { 's' })) -ForegroundColor Red
    foreach ($finding in $result.findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'See MIN_CONTAIN_SCALE in docs/assets/javascripts/diagram-zoom.js.' -ForegroundColor Red
    exit 1
}

Write-Host ("Diagram lightbox check passed: {0} diagram(s) open whole across {1} viewport(s) ({2} checks)." -f `
        $result.diagrams, $viewports.Count, $result.checks) -ForegroundColor Green
