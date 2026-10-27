/**
 * docs-layout.js
 *
 * Two reader controls for the docs site:
 *
 *   Width toggle
 *     A header button, beside the light/dark toggle, that switches the content between the
 *     reading width and the full window width, which text, tables, code and diagrams all fill
 *     (custom.css, "Reader layout controls"). The choice is remembered in this browser; the inline script
 *     in docs/overrides/main.html applies it before the page draws, so nothing jumps.
 *
 *   Resizable table columns
 *     A handle on the boundary between two header cells (so on every one but the last). Drag
 *     it, or focus it and use the arrow keys (Shift for bigger steps), to move width between
 *     the two columns it divides; the table keeps its width, so it never starts to scroll.
 *     Neither column goes below its minimum. Double-click to restore the natural layout.
 *     Widths are not remembered.
 *
 * No external dependencies. scripts/Test-DocsLayout.ps1 drives both in a browser.
 */
(function () {
  "use strict";

  var STORAGE_KEY = "jim-docs-width";
  var KEY_STEP = 16;        // px per arrow key press
  var KEY_STEP_LARGE = 64;  // with Shift
  var MIN_COLUMN = 64;      // px, padding included

  // Material Design Icons, as the rest of the site's icons (Apache 2.0).
  var EXPAND = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" aria-hidden="true"><path d="M9 11h6V8l4 4-4 4v-3H9v3l-4-4 4-4zm-7 9V4h2v16zm18 0V4h2v16z"/></svg>';
  var COLLAPSE = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" aria-hidden="true"><path d="M13 20V4h2.03v16zm-3 0V4h2.03v16zM5 8l4.03 4L5 16v-3H2v-2h3zm15 8-4-4 4-4v3h3v2h-3z"/></svg>';

  // ── Width toggle ───────────────────────────────────────────────────────────

  function storedFull() {
    try { return localStorage.getItem(STORAGE_KEY) === "full"; } catch (e) { return false; }
  }

  function store(full) {
    try {
      if (full) localStorage.setItem(STORAGE_KEY, "full");
      else localStorage.removeItem(STORAGE_KEY);
    } catch (e) { /* private window or blocked storage: the choice lasts for this page only */ }
  }

  function applyWidth(full) {
    if (full) document.documentElement.setAttribute("data-jim-width", "full");
    else document.documentElement.removeAttribute("data-jim-width");
  }

  function renderToggle(button, full) {
    var label = full ? "Switch to reading width" : "Switch to full width";
    button.setAttribute("aria-pressed", full ? "true" : "false");
    button.setAttribute("aria-label", label);
    button.title = label;
    button.innerHTML = full ? COLLAPSE : EXPAND;
  }

  function initWidthToggle() {
    var full = storedFull();
    applyWidth(full);

    var palette = document.querySelector('.md-header__inner [data-md-component="palette"]');
    var anchor = palette || document.querySelector(".md-header__inner .md-search");
    if (!anchor || document.querySelector(".jim-width-toggle")) return;

    var button = document.createElement("button");
    button.type = "button";
    button.className = "md-header__button md-icon jim-width-toggle";
    renderToggle(button, full);
    button.addEventListener("click", function () {
      full = !full;
      applyWidth(full);
      store(full);
      renderToggle(button, full);
    });
    anchor.parentNode.insertBefore(button, anchor);
  }

  // ── Resizable table columns ────────────────────────────────────────────────

  // Fixes the table at its rendered width and every column at its share of it, then switches
  // to a fixed layout (custom.css makes the table a table box for it), so a resize changes only the two columns either side of the handle and
  // the table's width never moves. The header cells' rendered widths can add up to more than
  // the table (Material caps a table at its container), and a fixed layout would then scale
  // every column down, so they are scaled to the table's width here instead. Idempotent.
  function freeze(table) {
    if (table.getAttribute("data-jim-resized")) return;
    var cells = table.querySelectorAll("thead th");
    var widths = [];
    for (var i = 0; i < cells.length; i++) widths.push(cells[i].getBoundingClientRect().width);
    var tableWidth = table.getBoundingClientRect().width;
    // The columns fill the table inside its border, not the whole of it.
    var style = getComputedStyle(table);
    var inner = tableWidth - (parseFloat(style.borderLeftWidth) || 0) - (parseFloat(style.borderRightWidth) || 0);
    var scale = inner / sum(widths);
    for (var j = 0; j < cells.length; j++) cells[j].style.width = (widths[j] * scale) + "px";
    table.style.width = tableWidth + "px";
    table.style.tableLayout = "fixed";
    table.setAttribute("data-jim-resized", "true");
  }

  function sum(values) {
    var total = 0;
    for (var i = 0; i < values.length; i++) total += values[i];
    return total;
  }

  // Moves the boundary after `cell` so the cell becomes `width` wide, taking the difference
  // from (or giving it to) the column to its right. Their combined width is unchanged, so the
  // table's is too; neither goes below MIN_COLUMN.
  function resizeColumn(table, cell, width) {
    freeze(table);
    var neighbour = cell.nextElementSibling;
    if (!neighbour) return;
    // The widths freeze() set, not the rendered ones, so repeated moves never drift.
    var pair = parseFloat(cell.style.width) + parseFloat(neighbour.style.width);
    var next = Math.min(Math.max(Math.round(width), MIN_COLUMN), pair - MIN_COLUMN);
    if (next < MIN_COLUMN) return;  // the pair is already as narrow as the two minimums allow
    cell.style.width = next + "px";
    neighbour.style.width = (pair - next) + "px";
  }

  function reset(table) {
    var cells = table.querySelectorAll("thead th");
    for (var i = 0; i < cells.length; i++) cells[i].style.width = "";
    table.style.width = "";
    table.style.tableLayout = "";
    table.removeAttribute("data-jim-resized");
  }

  function addHandle(table, cell) {
    var handle = document.createElement("span");
    handle.className = "jim-col-resize";
    handle.setAttribute("role", "separator");
    handle.setAttribute("aria-orientation", "vertical");
    handle.setAttribute("aria-label", "Resize the " + (cell.textContent || "").trim() + " column");
    handle.tabIndex = 0;
    cell.appendChild(handle);

    handle.addEventListener("pointerdown", function (event) {
      if (event.button !== 0) return;
      event.preventDefault();
      freeze(table);
      var startX = event.clientX;
      var startWidth = parseFloat(cell.style.width);
      handle.setPointerCapture(event.pointerId);
      // A data attribute, not a class: Material styles tables as table:not([class]), and an
      // emptied class attribute would still opt this one out of every rule.
      table.setAttribute("data-jim-col-resizing", "true");

      function move(e) { resizeColumn(table, cell, startWidth + (e.clientX - startX)); }
      function up() {
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", up);
        handle.removeEventListener("pointercancel", up);
        table.removeAttribute("data-jim-col-resizing");
      }
      handle.addEventListener("pointermove", move);
      handle.addEventListener("pointerup", up);
      handle.addEventListener("pointercancel", up);
    });

    handle.addEventListener("keydown", function (event) {
      if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
      event.preventDefault();
      var step = event.shiftKey ? KEY_STEP_LARGE : KEY_STEP;
      freeze(table);
      var width = parseFloat(cell.style.width);
      resizeColumn(table, cell, width + (event.key === "ArrowRight" ? step : -step));
    });

    handle.addEventListener("dblclick", function (event) {
      event.preventDefault();
      reset(table);
    });

    // A click on the handle must not reach anything the header cell does on click.
    handle.addEventListener("click", function (event) { event.stopPropagation(); });
  }

  function initColumns() {
    var tables = document.querySelectorAll(".md-typeset table:not([class])");
    for (var t = 0; t < tables.length; t++) {
      var table = tables[t];
      if (table.getAttribute("data-jim-resizable")) continue;
      var cells = table.querySelectorAll("thead th");
      if (cells.length < 2) continue;
      table.setAttribute("data-jim-resizable", "true");
      for (var c = 0; c < cells.length - 1; c++) addHandle(table, cells[c]);
    }
  }

  function init() {
    initWidthToggle();
    initColumns();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
