# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Regenerates (or verifies) the label-cut masks that let concept-diagram edge labels sit on their edges.

.DESCRIPTION
    Edge labels in the hand-authored diagrams under docs/assets/diagrams/ sit *on* the edge they
    label. The edge, its arrowhead and its data packets are drawn inside a group carrying
    mask="url(#<prefix>-labelcut)", and that mask punches a small rounded rectangle out of them
    behind each label, so the line stops cleanly either side of the words and a packet passing
    underneath is occluded at the same edge.

    Cutting rather than painting over matters because most of these labels sit on the bare page.
    A knockout stroke (the technique .jimdg-hub-title uses for boundary titles) needs an opaque
    colour to paint, and the boundary title can borrow its container's fill. On the page there is
    nothing to borrow: the docs site's background is knowable, but the README exports in
    .github/diagrams/ render on whichever GitHub theme the reader has chosen. A mask cut is
    transparent, so it is correct on every background without a colour to keep in sync. A knockout
    stroke is also wrong at label size for a second reason: it follows glyph outlines, so the edge
    stays visible in the space between words, and widening it to close that gap bulges the halo
    around the letters and slices packets into crescents.

    Each cut rectangle is derived from the label's *rendered* text metrics, which is why this script
    exists: the numbers cannot be reasoned about from the source, they have to be measured in a
    browser with the site's stylesheet applied. Change a label's wording and its cut has to be
    regenerated, or it will be too small (edge reappears through the text) or too large (a visible
    gap in the line). -Check enforces that in CI and locally.

    The invariant -Check enforces, for every edge label in every diagram:
      either the label's box sits inside a cut in that diagram's label-cut mask,
      or the label's box is at least -MinimumClearance user units clear of every edge.
    The second arm is the fallback for labels that are not sitting on an edge at all; it is also
    what catches a label that has outgrown its cut, because an overflowing label both fails to fit
    its cut and overlaps the edge.

    Measurement runs in Chromium through playwright-core. Nothing extra needs installing:
    .devcontainer/setup.sh already installs @playwright/mcp globally (for the Playwright MCP server)
    and downloads the matching Chromium, and this script reuses that copy. Set
    JIM_PLAYWRIGHT_MODULE to point at a different playwright/playwright-core package if needed.

.PARAMETER Path
    Diagram files or directories to process. Defaults to docs/assets/diagrams.

.PARAMETER Check
    Verify instead of rewrite. Prints every violation and exits non-zero if there are any. Leaves
    every file untouched.

.PARAMETER PaddingX
    Horizontal breathing space between the label's box and its cut, in SVG user units.

.PARAMETER PaddingY
    Vertical breathing space between the label's box and its cut, in SVG user units.

.PARAMETER MinimumClearance
    How far an uncut label must stay from every edge, in SVG user units.

.PARAMETER OutlineClearance
    How far every label must stay from a node or boundary outline, in SVG user units. Deliberately
    smaller than -MinimumClearance: labels sit snugly against the boxes they belong to, so this arm
    is looking for an outline running through the words rather than for a tight placement.

.EXAMPLE
    pwsh -File ./scripts/Update-DiagramLabelCuts.ps1
    Regenerates the label-cut mask in every diagram that has one, and reports what changed.

.EXAMPLE
    pwsh -File ./scripts/Update-DiagramLabelCuts.ps1 -Check
    Fails if any label has outgrown its cut or is touching an edge without one.

.EXAMPLE
    pwsh -File ./scripts/Update-DiagramLabelCuts.ps1 -Path docs/assets/diagrams/system-context.svg
    Regenerates a single diagram.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @('docs/assets/diagrams'),

    [switch]$Check,

    [double]$PaddingX = 5,

    [double]$PaddingY = 2,

    [double]$MinimumClearance = 8,

    [double]$OutlineClearance = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cssPath = Join-Path $repoRoot 'docs/assets/stylesheets/custom.css'

if (-not (Test-Path -LiteralPath $cssPath)) {
    throw "Stylesheet not found: $cssPath"
}

# --- Locate a playwright package -------------------------------------------------------------
# Preference order: an explicit override, the @playwright/mcp copy that .devcontainer/setup.sh
# installs (its bundled Chromium revision is the one that is actually downloaded), then any plain
# global playwright / playwright-core.
function Resolve-PlaywrightModule {
    if ($env:JIM_PLAYWRIGHT_MODULE) {
        if (Test-Path -LiteralPath $env:JIM_PLAYWRIGHT_MODULE) { return $env:JIM_PLAYWRIGHT_MODULE }
        throw "JIM_PLAYWRIGHT_MODULE is set but does not exist: $($env:JIM_PLAYWRIGHT_MODULE)"
    }

    $globalRoot = $null
    try { $globalRoot = (& npm root -g 2>$null | Select-Object -First 1) } catch { $globalRoot = $null }
    if (-not $globalRoot) { return $null }

    $candidates = @(
        (Join-Path $globalRoot '@playwright/mcp/node_modules/playwright-core'),
        (Join-Path $globalRoot 'playwright'),
        (Join-Path $globalRoot 'playwright-core')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$playwrightModule = Resolve-PlaywrightModule
if (-not $playwrightModule) {
    throw @'
Could not find a playwright package to measure label metrics with.

The dev container installs one already (.devcontainer/setup.sh, "Installing Playwright MCP
browser"); re-run that step, or point JIM_PLAYWRIGHT_MODULE at a playwright/playwright-core
install of your own.
'@
}

# --- Collect the diagrams ----------------------------------------------------------------------
$files = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $Path) {
    $full = if ([System.IO.Path]::IsPathRooted($entry)) { $entry } else { Join-Path $repoRoot $entry }
    if (-not (Test-Path -LiteralPath $full)) { throw "Path not found: $entry" }
    if (Test-Path -LiteralPath $full -PathType Container) {
        Get-ChildItem -LiteralPath $full -Filter '*.svg' -File | Sort-Object Name | ForEach-Object { $files.Add($_.FullName) }
    }
    else {
        $files.Add((Resolve-Path -LiteralPath $full).Path)
    }
}

if ($files.Count -eq 0) { throw 'No SVG files found to process.' }

# --- Measure in a browser ----------------------------------------------------------------------
# The measuring half runs in Node because only a real layout engine knows how wide a label is once
# the site's webfont and .jimdg-* rules have been applied. It returns, per diagram: each edge
# label's rendered box, and its distance to the nearest edge.
$measureScript = @'
const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.JIM_PW_MODULE);

const cssPath = process.env.JIM_CSS_PATH;
const files = JSON.parse(process.env.JIM_FILES);
const css = fs.readFileSync(cssPath, 'utf8');

const page404 = files.map((f, i) =>
  `<div class="jimdg-probe" id="probe-${i}">${fs.readFileSync(f, 'utf8')}</div>`).join('\n');

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
${css}
body { margin: 0; }
.jimdg-probe { width: 1200px; }
.jimdg-probe svg.jim-diagram { display: block; width: 1160px; height: auto; margin: 0; }
</style></head><body data-md-color-scheme="default">
${page404}
</body></html>`;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewportSize: { width: 1280, height: 900 } });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts && document.fonts.ready);
  const result = await page.evaluate((names) => {
    // Shortest distance between an axis-aligned rectangle and a line segment; 0 when they touch.
    function pointSeg(px, py, x1, y1, x2, y2) {
      const dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
      let t = l2 === 0 ? 0 : ((px - x1) * dx + (py - y1) * dy) / l2;
      t = Math.max(0, Math.min(1, t));
      return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
    }
    function rectSeg(r, x1, y1, x2, y2) {
      const inside = (x, y) => x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h;
      if (inside(x1, y1) || inside(x2, y2)) return 0;
      const edges = [
        [r.x, r.y, r.x + r.w, r.y],
        [r.x + r.w, r.y, r.x + r.w, r.y + r.h],
        [r.x + r.w, r.y + r.h, r.x, r.y + r.h],
        [r.x, r.y + r.h, r.x, r.y]
      ];
      const ccw = (ax, ay, bx, by, cx, cy) => (cy - ay) * (bx - ax) > (by - ay) * (cx - ax);
      for (const [ax, ay, bx, by] of edges) {
        if (ccw(x1, y1, ax, ay, bx, by) !== ccw(x2, y2, ax, ay, bx, by) &&
            ccw(x1, y1, x2, y2, ax, ay) !== ccw(x1, y1, x2, y2, bx, by)) return 0;
      }
      let min = Infinity;
      for (const [ax, ay, bx, by] of edges) {
        min = Math.min(min,
          pointSeg(ax, ay, x1, y1, x2, y2),
          pointSeg(x1, y1, ax, ay, bx, by),
          pointSeg(x2, y2, ax, ay, bx, by));
      }
      return min;
    }

    const out = [];
    document.querySelectorAll('.jimdg-probe').forEach((probe, i) => {
      const svg = probe.querySelector('svg.jim-diagram');
      if (!svg) { out.push({ file: names[i], labels: [] }); return; }
      const vb = (svg.getAttribute('viewBox') || '0 0 0 0').trim().split(/\s+/).map(Number);
      const segs = [...svg.querySelectorAll('line')].map(l => [
        +l.getAttribute('x1'), +l.getAttribute('y1'),
        +l.getAttribute('x2'), +l.getAttribute('y2')
      ]);
      // Rect outlines are tracked separately from edges. A label crossing one cannot be fixed with
      // a cut: the mask would remove the rect's fill as well as its stroke, punching a hole in the
      // surface. Those have to be solved by moving the label.
      const outlines = [];
      svg.querySelectorAll('rect').forEach(rc => {
        if (rc.closest('defs')) return;   // the mask's own canvas and cut rectangles are not outlines
        const x = +rc.getAttribute('x'), y = +rc.getAttribute('y');
        const w = +rc.getAttribute('width'), h = +rc.getAttribute('height');
        if (!w || !h) return;
        outlines.push([x, y, x + w, y], [x + w, y, x + w, y + h],
                      [x + w, y + h, x, y + h], [x, y + h, x, y]);
      });
      const labels = [];
      svg.querySelectorAll('text.jimdg-spoke-label, text.jimdg-flow-label').forEach(t => {
        const b = t.getBBox();
        if (!b.width) return;
        const r = { x: b.x, y: b.y, w: b.width, h: b.height };
        let nearest = Infinity, nearestOutline = Infinity;
        for (const s of segs) nearest = Math.min(nearest, rectSeg(r, s[0], s[1], s[2], s[3]));
        for (const s of outlines) nearestOutline = Math.min(nearestOutline, rectSeg(r, s[0], s[1], s[2], s[3]));
        labels.push({
          text: t.textContent,
          x: +b.x.toFixed(2), y: +b.y.toFixed(2),
          w: +b.width.toFixed(2), h: +b.height.toFixed(2),
          clearance: Number.isFinite(nearest) ? +nearest.toFixed(2) : null,
          outlineClearance: Number.isFinite(nearestOutline) ? +nearestOutline.toFixed(2) : null
        });
      });
      out.push({ file: names[i], viewBox: vb, labels });
    });
    return out;
  }, files.map(f => path.basename(f)));
  await browser.close();
  process.stdout.write(JSON.stringify(result));
})().catch(err => { console.error(err && err.stack || String(err)); process.exit(1); });
'@

$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-label-cuts-{0}.cjs" -f ([guid]::NewGuid().ToString('n')))
Set-Content -LiteralPath $tempScript -Value $measureScript -Encoding utf8

try {
    $env:JIM_PW_MODULE = $playwrightModule
    $env:JIM_CSS_PATH = $cssPath
    $env:JIM_FILES = ($files | ConvertTo-Json -Compress -AsArray)

    $json = & node $tempScript
    if ($LASTEXITCODE -ne 0) { throw "Label measurement failed (node exited $LASTEXITCODE)." }
}
finally {
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
    Remove-Item Env:JIM_PW_MODULE, Env:JIM_CSS_PATH, Env:JIM_FILES -ErrorAction SilentlyContinue
}

$measurements = $json | ConvertFrom-Json

# --- Rewrite or verify ---------------------------------------------------------------------------
$maskPattern = '(?s)(<mask id="(?<id>[A-Za-z0-9\-]+-labelcut)"[^>]*>)(?<body>.*?)(</mask>)'
$violations = [System.Collections.Generic.List[string]]::new()
$changed = 0
$index = 0

foreach ($file in $files) {
    $name = Split-Path -Leaf $file
    $measure = $measurements | Where-Object { $_.file -eq $name } | Select-Object -First 1
    $index++

    $labels = @()
    if ($measure -and $measure.PSObject.Properties.Name -contains 'labels' -and $measure.labels) {
        $labels = @($measure.labels)
    }
    if ($labels.Count -eq 0) { continue }

    $content = Get-Content -LiteralPath $file -Raw

    if ($Check) {
        # A mask must cover edges only. Wrapping a group that also holds a <rect> or <ellipse> means
        # the cut removes that surface's fill as well, which shows up as a dark notch in the boundary
        # or a holed node -- invisible in light mode, obvious in dark. Wrap the innermost
        # edges-and-packets group instead of the group that contains it.
        foreach ($masked in [regex]::Matches($content, '(?s)^(?<indent>[ ]*)<g mask="url\(#[^"]+\)">\r?\n(?<body>.*?)^\k<indent></g>', 'Multiline')) {
            $inner = [regex]::Matches($masked.Groups['body'].Value, '<(rect|ellipse)\b')
            if ($inner.Count -gt 0) {
                $violations.Add(("{0}: a masked group encloses {1} surface element(s); the cut would hole their fill, so mask the edge group alone" -f `
                    $name, $inner.Count))
            }
        }

        # Crossing a node or boundary outline is unfixable by cutting, so it is reported wherever it
        # happens, mask or no mask.
        # Labels legitimately sit close to the boxes they belong to, so this arm is deliberately
        # tighter than the edge rule: it is looking for an outline actually running through the
        # words, not for a snug placement.
        foreach ($label in $labels) {
            if ($null -ne $label.outlineClearance -and $label.outlineClearance -lt $OutlineClearance) {
                $violations.Add(("{0}: '{1}' is {2} units from a box or boundary outline; move it (a cut would hole the surface)" -f `
                    $name, $label.text, $label.outlineClearance))
            }
        }
    }

    $match = [regex]::Match($content, $maskPattern)

    if (-not $match.Success) {
        # No cut mask in this diagram: every label must stand clear of every edge instead.
        foreach ($label in $labels) {
            if ($null -ne $label.clearance -and $label.clearance -lt $MinimumClearance) {
                $violations.Add(("{0}: '{1}' is {2} units from an edge and the diagram has no label-cut mask (needs {3}, or a cut)" -f `
                    $name, $label.text, $label.clearance, $MinimumClearance))
            }
        }
        continue
    }

    $viewBox = $measure.viewBox
    $canvasW = $viewBox[2]
    $canvasH = $viewBox[3]

    # Only labels that actually meet an edge get a cut. A label sitting clear of every edge needs
    # none, and cutting it anyway would be actively harmful on a short edge, where a cut wider than
    # the edge erases the whole thing (which is why some labels stay beside their edge rather than
    # on it: the edge is not long enough to carry them and keep a visible stub either side).
    $cuts = foreach ($label in ($labels | Where-Object { $null -ne $_.clearance -and $_.clearance -lt $MinimumClearance })) {
        [pscustomobject]@{
            Text = $label.text
            X    = [math]::Round($label.x - $PaddingX, 1)
            Y    = [math]::Round($label.y - $PaddingY, 1)
            W    = [math]::Round($label.w + (2 * $PaddingX), 1)
            H    = [math]::Round($label.h + (2 * $PaddingY), 1)
        }
    }

    # #ffffff and #000000 here are mask luminance, not colours: white keeps the edge, black cuts it
    # away. They are deliberately literal and must not be swapped for --jimdg-* tokens, which would
    # make the cut depend on the theme.
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add(('      <rect x="0" y="0" width="{0}" height="{1}" fill="#ffffff"/>' -f $canvasW, $canvasH))
    foreach ($cut in $cuts) {
        $lines.Add(('      <rect x="{0}" y="{1}" width="{2}" height="{3}" rx="3" fill="#000000"/>' -f `
            $cut.X, $cut.Y, $cut.W, $cut.H))
    }
    $newBody = "`n" + ($lines -join "`n") + "`n    "

    if ($Check) {
        # Every label must fall inside one of the cuts the file currently declares.
        $existing = [regex]::Matches($match.Groups['body'].Value, '<rect[^>]*x="(?<x>[-\d.]+)"[^>]*y="(?<y>[-\d.]+)"[^>]*width="(?<w>[-\d.]+)"[^>]*height="(?<h>[-\d.]+)"[^>]*fill="#000000"')
        foreach ($label in $labels) {
            $fits = $false
            foreach ($rect in $existing) {
                $rx = [double]$rect.Groups['x'].Value
                $ry = [double]$rect.Groups['y'].Value
                $rw = [double]$rect.Groups['w'].Value
                $rh = [double]$rect.Groups['h'].Value
                if ($label.x -ge $rx -and $label.y -ge $ry -and
                    ($label.x + $label.w) -le ($rx + $rw) -and
                    ($label.y + $label.h) -le ($ry + $rh)) { $fits = $true; break }
            }
            if (-not $fits) {
                if ($null -ne $label.clearance -and $label.clearance -ge $MinimumClearance) { continue }
                $violations.Add(("{0}: '{1}' has outgrown its cut (box {2},{3} {4}x{5}); regenerate with Update-DiagramLabelCuts.ps1" -f `
                    $name, $label.text, $label.x, $label.y, $label.w, $label.h))
            }
        }
        continue
    }

    if ($match.Groups['body'].Value -ceq $newBody) {
        Write-Verbose ("{0}: cuts already current" -f $name)
        continue
    }

    $updated = $content.Remove($match.Groups['body'].Index, $match.Groups['body'].Length).Insert($match.Groups['body'].Index, $newBody)
    Set-Content -LiteralPath $file -Value $updated -NoNewline -Encoding utf8
    $changed++
    Write-Host ("Updated {0} ({1} cut{2})" -f $name, $cuts.Count, $(if ($cuts.Count -eq 1) { '' } else { 's' }))
}

if ($Check) {
    if ($violations.Count -gt 0) {
        Write-Host ''
        Write-Host ("Label cut check failed ({0} violation{1}):" -f $violations.Count, $(if ($violations.Count -eq 1) { '' } else { 's' })) -ForegroundColor Red
        foreach ($violation in $violations) { Write-Host "  $violation" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'Label cut check passed: every edge label either fits its cut or stands clear of every edge.' -ForegroundColor Green
    exit 0
}

if ($changed -eq 0) {
    Write-Host 'Label cuts already up to date.' -ForegroundColor Green
}
else {
    Write-Host ("Regenerated label cuts in {0} diagram{1}." -f $changed, $(if ($changed -eq 1) { '' } else { 's' })) -ForegroundColor Green
}
