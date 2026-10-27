# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails when the docs site's width toggle or resizable table columns stop working.

.DESCRIPTION
    docs/assets/javascripts/docs-layout.js adds two reader controls to the docs site:

      - a header button that switches the content between the reading width and the full window
        width, remembered in the browser; at full width, text, tables, code and diagrams all take
        the room; and
      - a drag handle on the right edge of each table header cell, which resizes that column by
        pointer, by keyboard (arrow keys, Shift for bigger steps), and resets the table on a
        double-click.

    Both depend on Material for MkDocs' markup and CSS (.md-grid, the header's palette form, the
    table wrappers), so they can break silently on a theme upgrade. This drives them in a real
    browser against the site's own custom.css and the script, and asserts:

      - the toggle is offered on a wide window and hidden where the layout is already full width;
      - switching widens the grid and a paragraph with it, sets aria-pressed and the label, and
        is remembered across a reload;
      - a column handle moves width between its column and the next by drag and by keyboard,
        never below a minimum, without changing the table's width or making it scroll, and a
        double-click restores the table's natural layout.

    Browser resolution is shared with Test-DiagramLightbox.ps1; see JimPlaywright.ps1.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsLayout.ps1
    Runs every layout check.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cssPath = Join-Path $repoRoot 'docs/assets/stylesheets/custom.css'
$jsPath = Join-Path $repoRoot 'docs/assets/javascripts/docs-layout.js'

foreach ($required in @($cssPath, $jsPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Not found: $required" }
}

. (Join-Path $PSScriptRoot 'JimPlaywright.ps1')
$playwrightModule = Resolve-PlaywrightModule

$driver = @'
const fs = require('fs');
const http = require('http');
const { chromium } = require(process.env.JIM_PW_MODULE);

const css = fs.readFileSync(process.env.JIM_CSS_PATH, 'utf8');
const js = fs.readFileSync(process.env.JIM_JS_PATH, 'utf8');

// The parts of Material's layout the script and CSS rely on: the grid, the header with its
// palette form, the typeset content, and a table in Material's wrappers. Material's own rules
// for those parts are restated minimally so the widths behave as on the site.
const words = 'Every feature that compares text asks one of four questions, and each question has one rule. ';
const html = `<!doctype html><html><head><meta charset="utf-8"><style>
html { font-size: 125%; } body { margin: 0; font-family: sans-serif; }
.md-grid { max-width: 61rem; margin: 0 auto; }
.md-header__inner { display: flex; align-items: center; }
.md-typeset table:not([class]) { border: .05rem solid #ccc; border-spacing: 0; display: inline-block; max-width: 100%; overflow: auto; }
.md-typeset__scrollwrap { margin: 1em -.8rem; overflow-x: auto; }
.md-typeset__table { display: inline-block; padding: 0 .8rem; }
/* Material's display: table for wrapped tables is print-only; on screen they stay inline-block. */
.md-typeset th, .md-typeset td { padding: 4px 8px; text-align: left; }
.md-typeset table:not([class]) th { min-width: 5rem; padding: .9375em 1.25em; }
${css}
</style></head><body data-md-color-scheme="default">
<header class="md-header"><nav class="md-header__inner md-grid"><span class="md-header__title">JIM</span>
<form class="md-header__option" data-md-component="palette"><label class="md-header__button md-icon">T</label></form></nav></header>
<main class="md-main"><div class="md-main__inner md-grid"><div class="md-content"><article class="md-content__inner md-typeset">
<p id="prose">${words.repeat(12)}</p>
<div class="md-typeset__scrollwrap"><div class="md-typeset__table"><table id="t">
<thead><tr><th>Question</th><th>Rule</th><th>Where JIM asks it</th></tr></thead>
<tbody><tr><td>Has this value changed?</td><td>Exact, unless you say otherwise, for every attribute that a Connected System contributes to the Metaverse</td><td>${words.repeat(4)}</td></tr></tbody>
</table></div></div>
</article></div></div></main>
<script>${js}<\/script></body></html>`;

const server = http.createServer((req, res) => { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end(html); });

(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${server.address().port}/`;
  const browser = await chromium.launch();
  const findings = [];
  const check = (ok, message) => { if (!ok) findings.push(message); };

  // Width toggle, on a wide window.
  const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
  const page = await context.newPage();
  await page.goto(url);
  const toggle = page.locator('.jim-width-toggle');
  check(await toggle.count() === 1, 'no width toggle in the header on a 1920px window');
  if (await toggle.count() === 1) {
    check(await toggle.isVisible(), 'the width toggle is not visible on a 1920px window');
    const before = await page.evaluate(() => ({
      grid: document.querySelector('.md-main .md-grid').getBoundingClientRect().width,
      prose: document.getElementById('prose').getBoundingClientRect().width,
      pressed: document.querySelector('.jim-width-toggle').getAttribute('aria-pressed')
    }));
    check(before.pressed === 'false', `the toggle starts with aria-pressed "${before.pressed}", not "false"`);

    await toggle.click();
    const after = await page.evaluate(() => ({
      attr: document.documentElement.getAttribute('data-jim-width'),
      grid: document.querySelector('.md-main .md-grid').getBoundingClientRect().width,
      prose: document.getElementById('prose').getBoundingClientRect().width,
      pressed: document.querySelector('.jim-width-toggle').getAttribute('aria-pressed'),
      label: document.querySelector('.jim-width-toggle').getAttribute('aria-label')
    }));
    check(after.attr === 'full', `switching sets data-jim-width "${after.attr}", not "full"`);
    check(after.grid > before.grid + 200, `the grid did not widen (${before.grid}px to ${after.grid}px)`);
    check(after.prose - before.prose > (after.grid - before.grid) - 4, `a paragraph did not widen with the grid (${before.prose}px to ${after.prose}px, while the grid went from ${before.grid}px to ${after.grid}px)`);
    check(after.pressed === 'true', `after switching, aria-pressed is "${after.pressed}", not "true"`);
    check(/reading width/i.test(after.label || ''), `after switching, the label "${after.label}" does not offer the reading width`);

    await page.reload();
    const reloaded = await page.evaluate(() => document.documentElement.getAttribute('data-jim-width'));
    check(reloaded === 'full', 'the full-width choice was not remembered across a reload');
    await page.locator('.jim-width-toggle').click();
    const back = await page.evaluate(() => document.documentElement.getAttribute('data-jim-width'));
    check(back !== 'full', 'switching back did not return to the reading width');
  }
  await context.close();

  // Where Material already lays the content out at full width, the toggle has nothing to do.
  const narrow = await browser.newContext({ viewport: { width: 1024, height: 768 } });
  const narrowPage = await narrow.newPage();
  await narrowPage.goto(url);
  if (await narrowPage.locator('.jim-width-toggle').count() === 1) {
    check(!(await narrowPage.locator('.jim-width-toggle').isVisible()), 'the width toggle shows on a 1024px window, where it changes nothing');
  }
  await narrow.close();

  // Resizable columns.
  const cols = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const p = await cols.newPage();
  await p.goto(url);
  // The reading-width content column of a desktop window (688px), where columns sit nearest the
  // browser's minimum widths and a resize is likeliest to be overridden.
  await p.addStyleTag({ content: '.md-content__inner { max-width: 34.4rem; }' });
  const handles = p.locator('#t th .jim-col-resize');
  check(await handles.count() === 2, `expected a resize handle on every header cell but the last (2), found ${await handles.count()}`);
  if (await handles.count() >= 1) {
    const state = () => p.evaluate(() => ({
      cols: [...document.querySelectorAll('#t th')].map(th => th.getBoundingClientRect().width),
      table: document.getElementById('t').getBoundingClientRect().width,
      overflow: (() => { const w = document.querySelector('.md-typeset__scrollwrap'); return w.scrollWidth - w.clientWidth; })()
    }));
    const close = (a, b, slack) => Math.abs(a - b) <= slack;
    const before = await state();
    const box = await handles.first().boundingBox();
    await p.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await p.mouse.down();
    await p.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2, { steps: 8 });
    await p.mouse.up();
    const dragged = await state();
    check(close(dragged.cols[0], before.cols[0] + 40, 4), `dragging the first handle 40px moved its column from ${before.cols[0]}px to ${dragged.cols[0]}px`);
    check(close(dragged.cols[1], before.cols[1] - 40, 4), `dragging the first handle 40px did not take the width from the column to its right (${before.cols[1]}px to ${dragged.cols[1]}px)`);
    check(close(dragged.cols[2], before.cols[2], 1), `dragging the first handle changed a column it does not border (${before.cols[2]}px to ${dragged.cols[2]}px)`);
    check(close(dragged.table, before.table, 1), `resizing a column changed the table's width (${before.table}px to ${dragged.table}px); it should stay as it was`);
    check(dragged.overflow <= 0, `resizing a column made the table scroll sideways by ${dragged.overflow}px`);
    const classAttr = await p.evaluate(() => document.getElementById('t').hasAttribute('class'));
    check(!classAttr, 'a drag left a class attribute on the table, which takes it out of Material\'s table:not([class]) styling');

    await handles.first().focus();
    await p.keyboard.press('ArrowRight');
    const keyed = await state();
    check(close(keyed.cols[0], dragged.cols[0] + 16, 2) && close(keyed.cols[1], dragged.cols[1] - 16, 2), `ArrowRight did not move 16px from the right-hand column to this one (${dragged.cols[0]}/${dragged.cols[1]}px to ${keyed.cols[0]}/${keyed.cols[1]}px)`);
    const role = await handles.first().getAttribute('role');
    check(role === 'separator', `the handle's role is "${role}", not "separator"`);

    // Dragging past the neighbour's minimum carries on into the columns beyond it, nearest
    // first, so a column can take the whole table but the other columns' minimums.
    const drag = async (handle, dx, back) => {
      const hb = await handle.boundingBox();
      const x = hb.x + hb.width / 2, y = hb.y + hb.height / 2;
      await p.mouse.move(x, y);
      await p.mouse.down();
      await p.mouse.move(x + dx, y, { steps: 8 });
      const mid = await state();
      if (back) await p.mouse.move(x, y, { steps: 8 });
      await p.mouse.up();
      return { mid, end: await state() };
    };
    const MIN = 64;
    // To the window's right edge, well past both columns' minimums (a move outside the window is not delivered).
    const far = await drag(handles.first(), 600, true);
    const wide = far.mid;
    check(close(wide.cols[1], MIN, 2), `dragging far right did not shrink the neighbouring column to its minimum (${keyed.cols[1]}px to ${wide.cols[1]}px)`);
    check(close(wide.cols[2], MIN, 2), `dragging far right did not carry on into the next column once the neighbour reached its minimum (${keyed.cols[2]}px to ${wide.cols[2]}px)`);
    check(close(wide.cols[0], keyed.table - 2 - 2 * MIN, 4), `dragging far right did not let the column take the table's width less the other columns' minimums (${wide.cols[0]}px)`);
    check(close(wide.table, before.table, 1) && wide.overflow <= 0, `a drag past the neighbour's minimum changed the table's width or made it scroll`);
    const drift = await p.evaluate(() => [...document.querySelectorAll('#t th')].filter(th => Math.abs(parseFloat(th.style.width) - th.getBoundingClientRect().width) > 1).length);
    check(drift === 0, `${drift} column(s) are drawn at a different width from the one set, so the browser is overriding the resize (a minimum width the script does not allow for)`);
    const restored = far.end;
    check(restored.cols.every((w, i) => close(w, keyed.cols[i], 2)), `dragging back to the start did not restore the columns the drag squeezed (${keyed.cols.map(Math.round)} to ${restored.cols.map(Math.round)})`);

    // Leftwards from the second handle: its column gives way first, then the one before it.
    const left = (await drag(handles.nth(1), -600, false)).end;
    check(close(left.cols[1], MIN, 2) && close(left.cols[0], MIN, 2), `dragging the second handle far left did not shrink its column, then the one before it, to their minimums (${left.cols.map(Math.round)})`);
    check(close(left.cols[2], keyed.table - 2 - 2 * MIN, 4), `dragging the second handle far left did not give the freed width to the column on its right (${left.cols[2]}px)`);
    check(close(left.table, before.table, 1) && left.overflow <= 0, `a leftward drag changed the table's width or made it scroll`);

    await handles.first().dblclick();
    const reset = await p.evaluate(() => ({
      layout: getComputedStyle(document.getElementById('t')).tableLayout,
      styled: [...document.querySelectorAll('#t th')].some(th => th.style.width)
    }));
    check(reset.layout === 'auto' && !reset.styled, 'a double-click did not restore the table\'s natural layout');
  }
  await cols.close();

  await browser.close();
  server.close();
  process.stdout.write(JSON.stringify({ findings }));
})().catch(err => { console.error((err && err.stack) || String(err)); server.close(); process.exit(1); });
'@

$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-docs-layout-{0}.cjs" -f ([guid]::NewGuid().ToString('n')))
Set-Content -LiteralPath $tempScript -Value $driver -Encoding utf8

try {
    $env:JIM_PW_MODULE = $playwrightModule
    $env:JIM_CSS_PATH = $cssPath
    $env:JIM_JS_PATH = $jsPath

    $json = & node $tempScript
    if ($LASTEXITCODE -ne 0) { throw "Docs layout check failed to run (node exited $LASTEXITCODE)." }
}
finally {
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
    Remove-Item Env:JIM_PW_MODULE, Env:JIM_CSS_PATH, Env:JIM_JS_PATH -ErrorAction SilentlyContinue
}

$result = $json | ConvertFrom-Json

if ($result.findings.Count -gt 0) {
    Write-Host ''
    Write-Host ("Docs layout check failed ({0} finding{1}):" -f `
            $result.findings.Count, $(if ($result.findings.Count -eq 1) { '' } else { 's' })) -ForegroundColor Red
    foreach ($finding in $result.findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'See docs/assets/javascripts/docs-layout.js and the "Reader layout controls" section of custom.css.' -ForegroundColor Red
    exit 1
}

Write-Host 'Docs layout check passed: the width toggle and resizable table columns work.' -ForegroundColor Green
