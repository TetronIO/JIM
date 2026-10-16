// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

/**
 * terminal-window.cjs
 *
 * Draws a terminal screen as a macOS-style terminal window, for the PowerShell screenshots on the product tour
 * (#582). What the screen shows (rows of coloured text, and where the cursor is) is worked out by TerminalScreen.ps1;
 * this only draws it. The window box, title bar, palette and shadow are measured from the published powershell-*.png
 * images, and the page is 972x611 so a 2x capture with a transparent background matches their 1944x1222.
 */

const COLUMNS = 120;
const ROWS = 30;

// macOS Terminal's own title format: the folder, the process and the size, separated by em dashes.
const TITLE = `jim \u2014 pwsh \u2014 ${COLUMNS}\u00d7${ROWS}`;

// ANSI foreground colours, as the published screenshots' terminal theme draws them.
const PALETTE = {
  fg: '#e6e6e6', bg: '#2a2e39',
  30: '#2a2e39', 31: '#e06c75', 32: '#7aa976', 33: '#e0c97e', 34: '#61afef', 35: '#c678dd', 36: '#8ec9cc', 37: '#dfe5eb',
  90: '#5b6b7b', 91: '#ef7f88', 92: '#88bc83', 93: '#f0d898', 94: '#7cc0f5', 95: '#d49ae6', 96: '#99dbdf', 97: '#f4f7fa',
};

const escapeHtml = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// One cell per box-drawing or block character, drawn as vectors so the JIM banner tiles exactly whatever the font
// covers; a font's own box glyphs leave hairline gaps between cells.
function boxGlyph(ch) {
  const W = 6, H = 13, cx = W / 2, cy = H / 2, d = 1.1, s = 0.75;
  const line = (x1, y1, x2, y2) => `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"/>`;
  let body;
  switch (ch) {
    case '\u2588':
      return `<span class="cell"><svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none"><rect x="-0.05" y="-0.05" width="${W + 0.1}" height="${H + 0.1}" fill="currentColor"/></svg></span>`;
    case '\u2550': body = line(0, cy - d, W, cy - d) + line(0, cy + d, W, cy + d); break;
    case '\u2551': body = line(cx - d, 0, cx - d, H) + line(cx + d, 0, cx + d, H); break;
    case '\u2554': body = line(cx - d, cy - d, W, cy - d) + line(cx - d, cy - d, cx - d, H) + line(cx + d, cy + d, W, cy + d) + line(cx + d, cy + d, cx + d, H); break;
    case '\u2557': body = line(0, cy - d, cx + d, cy - d) + line(cx + d, cy - d, cx + d, H) + line(0, cy + d, cx - d, cy + d) + line(cx - d, cy + d, cx - d, H); break;
    case '\u255a': body = line(cx - d, 0, cx - d, cy + d) + line(cx - d, cy + d, W, cy + d) + line(cx + d, 0, cx + d, cy - d) + line(cx + d, cy - d, W, cy - d); break;
    case '\u255d': body = line(cx + d, 0, cx + d, cy + d) + line(0, cy + d, cx + d, cy + d) + line(cx - d, 0, cx - d, cy - d) + line(0, cy - d, cx - d, cy - d); break;
    default: return null;
  }
  return `<span class="cell"><svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" stroke="currentColor" stroke-width="${s}" stroke-linecap="square">${body}</svg></span>`;
}

function spanHtml(span) {
  const style = (span.fg !== null && span.fg !== undefined && PALETTE[span.fg] ? `color:${PALETTE[span.fg]};` : '')
    + (span.bold ? 'font-weight:600;-webkit-text-stroke:0.3px currentColor;' : '');
  let html = '';
  let text = '';
  const flush = () => { if (text) { html += escapeHtml(text); text = ''; } };
  for (const ch of span.text) {
    const glyph = boxGlyph(ch);
    if (glyph) { flush(); html += glyph; } else { text += ch; }
  }
  flush();
  return style ? `<span style="${style}">${html}</span>` : html;
}

function rowHtml(row) {
  return `<div class="row">${(row.spans || []).map(spanHtml).join('')}${row.cursor ? '<span class="cursor"></span>' : ''}</div>`;
}

/**
 * The page for one terminal screenshot.
 * @param {{ rows: Array, fontsDir: string }} options rows as TerminalScreen.ps1 builds them; fontsDir is the portal's
 *   self-hosted font folder (src/JIM.Web/wwwroot/fonts), so nothing is fetched at capture time.
 */
function terminalWindowHtml({ rows, fontsDir }) {
  const fontUrl = file => 'file://' + encodeURI(`${fontsDir}/${file}`);
  return `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:'Plex Mono';src:url('${fontUrl('ibm-plex-mono/ibm-plex-mono-500-latin.woff2')}') format('woff2');font-weight:500 600;}
@font-face{font-family:'Inter';src:url('${fontUrl('inter/inter-latin.woff2')}') format('woff2');font-weight:100 900;}
html,body{margin:0;background:transparent;}
.window{position:absolute;left:56px;top:38px;width:860px;height:499px;border-radius:10px;overflow:hidden;background:${PALETTE.bg};
  box-shadow:inset 0 0.5px 0 rgba(255,255,255,0.32),0 0 0 0.5px rgba(0,0,0,0.9),0 20px 44px rgba(0,0,0,0.64),0 2px 14px rgba(0,0,0,0.24);}
.titlebar{height:32px;background:#2c2b2a;border-bottom:1px solid #1a1a1a;position:relative;}
.light{position:absolute;top:10px;width:12px;height:12px;border-radius:50%;}
.title{position:absolute;left:84px;top:0;height:32px;font:600 13px Inter,sans-serif;color:#a8a7a6;display:flex;align-items:center;gap:6px;}
.folder{width:16px;height:12px;border-radius:2px;background:linear-gradient(#7aa7dc,#4f80bd);}
.screen{padding:5px 10px 0;font:500 11.65px/15px 'Plex Mono',monospace;color:${PALETTE.fg};white-space:pre;}
.row{height:15px;}
.cell{display:inline-block;width:1ch;height:15px;vertical-align:top;}
.cell svg{width:100%;height:100%;display:block;overflow:visible;}
.cursor{display:inline-block;width:1ch;height:13px;margin-top:1px;vertical-align:top;background:rgba(230,230,230,0.6);}
</style></head><body>
<div class="window">
  <div class="titlebar">
    <span class="light" style="left:10px;background:#ff5f57"></span>
    <span class="light" style="left:30px;background:#febc2e"></span>
    <span class="light" style="left:50px;background:#28c840"></span>
    <div class="title"><span class="folder"></span>${escapeHtml(TITLE)}</div>
  </div>
  <div class="screen">${rows.map(rowHtml).join('')}</div>
</div></body></html>`;
}

module.exports = { terminalWindowHtml, COLUMNS, ROWS };
