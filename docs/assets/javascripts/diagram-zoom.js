/**
 * diagram-zoom.js
 *
 * Click-to-enlarge for every diagram on the docs site, in one overlay with
 * zoom and pan. Two diagram families feed it, and they need different
 * handling to get an SVG out of the page:
 *
 *   Concept SVGs (docs/assets/diagrams/*.svg)
 *     Inlined into the page by pymdownx.snippets, so the <svg> node is right
 *     there in the DOM and we clone it.
 *
 *   Mermaid diagrams
 *     MkDocs Material renders these into a *closed* Shadow DOM
 *     (attachShadow({mode:"closed"})), so the rendered SVG is unreachable via
 *     querySelector or shadowRoot. We instead capture each diagram's source
 *     text from <pre class="mermaid"> before Material replaces it, then
 *     re-render it on demand through the Mermaid API already loaded on the
 *     page.
 *
 * No external dependencies; all styling lives in custom.css so the overlay
 * follows the site's light/dark theme.
 */
(function () {
  "use strict";

  // Widest SMIL begin offset used by the concept diagrams. A cloned <svg> is
  // its own timeline root and restarts at zero, which leaves any packet with a
  // positive begin offset parked at 0,0 (a dot stuck in the corner) until that
  // offset elapses. Winding the clone's clock past it starts every packet
  // already on its path.
  var SMIL_PRIME_SECONDS = 2.4;

  var MAX_SCALE = 8;    // zoom-out stops at fit-to-width, in stops here
  var WHEEL_STEP = 1.18;
  var BUTTON_STEP = 1.4;
  var KEY_PAN_FRACTION = 0.12;

  // ── Overlay ────────────────────────────────────────────────────────────────

  var ov = null;          // overlay root
  var ovPanel = null;
  var ovStage = null;
  var ovLevel = null;
  var ovTitle = null;
  var lastFocus = null;   // element to restore focus to on close
  var view = null;        // { svg, base:[x,y,w,h], x, y, w, h } or null

  function buildOverlay() {
    if (ov) return;

    ov = document.createElement("div");
    ov.className = "jim-dgz";
    ov.setAttribute("role", "dialog");
    ov.setAttribute("aria-modal", "true");
    ov.setAttribute("aria-label", "Diagram, enlarged");
    ov.dataset.open = "false";

    ov.innerHTML = [
      '<div class="jim-dgz__panel">',
      '  <button class="jim-dgz__close" type="button" aria-label="Close">&times;</button>',
      '  <div class="jim-dgz__stage"></div>',
      '  <div class="jim-dgz__tools">',
      '    <button type="button" data-zoom="out" aria-label="Zoom out">&minus;</button>',
      '    <span class="jim-dgz__level" role="status" aria-live="polite">100%</span>',
      '    <button type="button" data-zoom="in" aria-label="Zoom in">+</button>',
      '    <button type="button" data-zoom="reset">Reset</button>',
      "  </div>",
      "</div>",
    ].join("");

    ovPanel = ov.querySelector(".jim-dgz__panel");
    ovStage = ov.querySelector(".jim-dgz__stage");
    ovLevel = ov.querySelector(".jim-dgz__level");

    document.body.appendChild(ov);

    ov.addEventListener("click", function (e) {
      if (e.target === ov) closeOverlay();
    });
    ov.querySelector(".jim-dgz__close").addEventListener("click", closeOverlay);

    ov.querySelectorAll("[data-zoom]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        var mode = btn.dataset.zoom;
        if (mode === "reset") resetView();
        else zoomBy(mode === "in" ? BUTTON_STEP : 1 / BUTTON_STEP);
      });
    });

    ov.addEventListener("keydown", onOverlayKeydown);
    wirePointer();
  }

  function openOverlay(svg, label) {
    buildOverlay();
    lastFocus = document.activeElement;

    ovStage.innerHTML = "";
    ovStage.appendChild(svg);
    ov.setAttribute("aria-label", label ? "Diagram, enlarged: " + label : "Diagram, enlarged");
    ovTitle = label || null;

    ov.dataset.open = "true";
    document.body.style.overflow = "hidden";

    layoutStage(svg);
    initView(svg);
    primeAnimations(svg);

    ov.querySelector(".jim-dgz__close").focus();
  }

  function closeOverlay() {
    if (!ov || ov.dataset.open !== "true") return;
    ov.dataset.open = "false";
    ovStage.innerHTML = "";
    ovPanel.style.width = "";
    ovStage.style.height = "";
    view = null;
    document.body.style.overflow = "";
    if (lastFocus && typeof lastFocus.focus === "function") lastFocus.focus();
    lastFocus = null;
  }

  function onOverlayKeydown(e) {
    if (e.key === "Tab") {
      var f = ov.querySelectorAll("button");
      var first = f[0];
      var last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
      return;
    }
    if (!view) return;

    if (e.key === "+" || e.key === "=") { e.preventDefault(); zoomBy(BUTTON_STEP); }
    else if (e.key === "-" || e.key === "_") { e.preventDefault(); zoomBy(1 / BUTTON_STEP); }
    else if (e.key === "0") { e.preventDefault(); resetView(); }
    else if (e.key === "ArrowLeft")  { e.preventDefault(); panBy(-view.w * KEY_PAN_FRACTION, 0); }
    else if (e.key === "ArrowRight") { e.preventDefault(); panBy(view.w * KEY_PAN_FRACTION, 0); }
    else if (e.key === "ArrowUp")    { e.preventDefault(); panBy(0, -view.h * KEY_PAN_FRACTION); }
    else if (e.key === "ArrowDown")  { e.preventDefault(); panBy(0, view.h * KEY_PAN_FRACTION); }
  }

  // ── View: viewBox-driven zoom and pan ─────────────────────────────────────

  function parseViewBox(svg) {
    var raw = svg.getAttribute("viewBox");
    if (!raw) return null;
    var n = raw.trim().split(/[\s,]+/).map(Number);
    if (n.length !== 4 || n.some(isNaN) || n[2] <= 0 || n[3] <= 0) return null;
    return n;
  }

  // The visible window always carries the stage's aspect ratio, so the SVG maps
  // onto the stage exactly and "meet" never letterboxes.
  //
  // The opening view ("home", and what 100% and Reset mean) is the whole
  // diagram where that is worth having, and fit-to-width where it is not.
  // Fit-to-width alone was the original rule, because containing a long Mermaid
  // flowchart shrinks it to a thumbnail: measured at 1294px wide fit-to-width
  // against 180px fitting the whole thing to viewport height. But it is wrong
  // for a diagram only slightly taller than the stage, which is every concept
  // SVG -- a 1160x640 diagram on a 2.01-aspect stage opens with its bottom 10%
  // quietly cut off, looking whole rather than looking panable. So contain by
  // default, and fall back to fit-to-width only when containing would render
  // the diagram too small to read.
  //
  // "Too small" has to be measured in CSS pixels, not as a share of the stage.
  // A share alone gets the phone case wrong: on a 390px-wide window the stage
  // is tall and narrow, so containing a 4000-unit flowchart costs only 42% of
  // the width -- which sounds cheap and is in fact a 153px sliver. The two
  // populations separate cleanly by absolute width: on the worst realistic
  // desktop window (1440x620, stage aspect 2.72) the tallest concept diagrams
  // contain to 674px and 755px, perfectly readable, while a Mermaid flowchart
  // contains to between 98px and 182px on every window tested. MIN_CONTAIN_PX
  // sits between them with room either side.
  //
  // It also errs the right way, because the overlay has zoom controls: a
  // diagram shown whole but small can be zoomed into, whereas a cropped one
  // only helps a reader who notices it pans.
  var MIN_CONTAIN_PX = 560;

  function initView(svg) {
    var base = parseViewBox(svg);
    if (!base) {
      // No usable viewBox (unlikely, but a Mermaid render could omit it):
      // show it as-is and take the zoom controls away.
      view = null;
      ov.dataset.zoomable = "false";
      return;
    }
    ov.dataset.zoomable = "true";
    svg.setAttribute("preserveAspectRatio", "xMidYMid meet");

    // Start at the top of the diagram; clampView centres it on any axis where
    // the window is larger than the diagram.
    var aspect = stageAspect();
    var home = homeWidth(base, aspect);
    view = { svg: svg, base: base, home: home, x: base[0], y: base[1], w: home, h: home / aspect };
    applyView();
  }

  function stageAspect() {
    var r = ovStage.getBoundingClientRect();
    return r.height > 0 ? r.width / r.height : 16 / 9;
  }

  // The window width the overlay opens at: the whole diagram where that stays
  // readable, fit-to-width where it would not. Returns a viewBox width, so a
  // value wider than the diagram means the diagram is contained with padding.
  function homeWidth(base, aspect) {
    var contained = Math.max(base[2], base[3] * aspect);
    if (contained <= base[2]) return base[2];        // nothing to lose; already whole
    var stagePx = ovStage.getBoundingClientRect().width;
    var containedPx = stagePx * (base[2] / contained);
    return containedPx >= MIN_CONTAIN_PX ? contained : base[2];
  }

  function applyView() {
    if (!view) return;
    clampView();
    view.svg.setAttribute("viewBox", view.x + " " + view.y + " " + view.w + " " + view.h);
    // Relative to home, so the overlay always opens at 100% and Reset returns
    // to it, whichever of the two home is.
    ovLevel.textContent = Math.round((view.home / view.w) * 100) + "%";
    ovStage.dataset.panable =
      view.w < view.base[2] || view.h < view.base[3] ? "true" : "false";
  }

  // Keep the window inside the diagram on each axis it is smaller than, and
  // centred on any axis where it is larger, so panning never strands the
  // reader in blank space.
  function clampView() {
    var b = view.base;
    var minW = b[2] / MAX_SCALE;
    // Zooming out stops at home, which may be wider than the diagram when home
    // contains it; capping at b[2] here is what used to crop the opening view.
    if (view.w > view.home) { view.w = view.home; view.h = view.w / stageAspect(); }
    if (view.w < minW) { view.w = minW; view.h = view.w / stageAspect(); }

    view.x = view.w >= b[2]
      ? b[0] + (b[2] - view.w) / 2
      : Math.min(b[0] + b[2] - view.w, Math.max(b[0], view.x));

    view.y = view.h >= b[3]
      ? b[1] + (b[3] - view.h) / 2
      : Math.min(b[1] + b[3] - view.h, Math.max(b[1], view.y));
  }

  // Zoom about a point given in SVG user units (defaults to the view centre).
  function zoomBy(factor, cx, cy) {
    if (!view) return;
    if (cx === undefined) cx = view.x + view.w / 2;
    if (cy === undefined) cy = view.y + view.h / 2;

    var b = view.base;
    var minW = b[2] / MAX_SCALE;
    var nw = Math.min(view.home, Math.max(minW, view.w / factor));
    var k = nw / view.w;

    view.x = cx - (cx - view.x) * k;
    view.y = cy - (cy - view.y) * k;
    view.w = nw;
    view.h = nw / stageAspect();
    applyView();
  }

  function panBy(dx, dy) {
    if (!view) return;
    view.x += dx;
    view.y += dy;
    applyView();
  }

  // Back to the opening view, scrolled to the top of the diagram.
  function resetView() {
    if (!view) return;
    view.x = view.base[0];
    view.y = view.base[1];
    view.w = view.home;
    view.h = view.w / stageAspect();
    applyView();
  }

  // Map a pointer event onto SVG user units within the current view.
  function pointerToUser(e) {
    var r = view.svg.getBoundingClientRect();
    return {
      x: view.x + ((e.clientX - r.left) / r.width) * view.w,
      y: view.y + ((e.clientY - r.top) / r.height) * view.h,
    };
  }

  function wirePointer() {
    var drag = null;

    ovStage.addEventListener(
      "wheel",
      function (e) {
        if (!view) return;
        e.preventDefault();
        var p = pointerToUser(e);
        zoomBy(e.deltaY < 0 ? WHEEL_STEP : 1 / WHEEL_STEP, p.x, p.y);
      },
      { passive: false }
    );

    ovStage.addEventListener("pointerdown", function (e) {
      // A tall diagram pans vertically even at 100%, so test both axes.
      if (!view || ovStage.dataset.panable !== "true") return;
      drag = {
        px: e.clientX,
        py: e.clientY,
        x: view.x,
        y: view.y,
        scale: view.w / ovStage.getBoundingClientRect().width,
      };
      ovStage.classList.add("is-dragging");
      ovStage.setPointerCapture(e.pointerId);
    });

    ovStage.addEventListener("pointermove", function (e) {
      if (!drag) return;
      view.x = drag.x - (e.clientX - drag.px) * drag.scale;
      view.y = drag.y - (e.clientY - drag.py) * drag.scale;
      applyView();
    });

    ["pointerup", "pointercancel"].forEach(function (type) {
      ovStage.addEventListener(type, function () {
        drag = null;
        ovStage.classList.remove("is-dragging");
      });
    });

    ovStage.addEventListener("dblclick", function (e) {
      if (!view) return;
      e.preventDefault();
      // Double-click zooms in towards the cursor; at full zoom it resets.
      if (view.w <= view.base[2] / MAX_SCALE) resetView();
      else {
        var p = pointerToUser(e);
        zoomBy(2, p.x, p.y);
      }
    });
  }

  // Give the stage the widest box the viewport allows, and only as much height
  // as the diagram needs at that width (capped at the viewport). A diagram
  // taller than the cap keeps the full width and is panned, rather than being
  // shrunk to fit its whole height on screen: fitting a tall flowchart to
  // viewport height would render it *narrower* than it already is in the page.
  function layoutStage(svg) {
    if (!svg) return;

    var base = parseViewBox(svg);
    var ar = base ? base[2] / base[3] : 16 / 9;

    var cs = getComputedStyle(ovPanel);
    var padX = parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight);
    var padY = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom);
    var tools = ov.querySelector(".jim-dgz__tools");
    var reserve = tools && tools.offsetParent !== null ? tools.offsetHeight + 12 : 0;

    var vw = document.documentElement.clientWidth || window.innerWidth;
    var vh = document.documentElement.clientHeight || window.innerHeight;

    // These are the bounds for the stage itself; the panel is that plus padding.
    var availW = Math.max(240, vw * 0.94 - padX);
    var availH = Math.max(180, vh * 0.92 - padY - reserve);

    var stageW = availW;
    var stageH = Math.min(availH, stageW / ar);

    ovPanel.style.width = stageW + padX + "px";
    ovStage.style.height = stageH + "px";

    svg.removeAttribute("width");
    svg.removeAttribute("height");
    svg.style.width = "100%";
    svg.style.height = "100%";
    svg.style.maxWidth = "none";
    svg.style.maxHeight = "none";
  }

  function primeAnimations(svg) {
    if (typeof svg.setCurrentTime === "function") {
      try { svg.setCurrentTime(SMIL_PRIME_SECONDS); } catch (e) { /* no SMIL */ }
    }
  }

  // ── Triggers ──────────────────────────────────────────────────────────────

  // Diagonal expand arrows: the conventional "open full size" glyph for a
  // lightbox. The magnifier-plus reads as "zoom in", which is what the
  // controls inside the overlay do, not what this button does.
  var HINT_SVG =
    '<svg viewBox="0 0 24 24" aria-hidden="true">' +
    '<path d="M14 4h6v6M10 20H4v-6M20 4l-7.5 7.5M4 20l7.5-7.5"/></svg>';

  // Wrap a diagram element in a real <button> so it is reachable by keyboard
  // and announced as a control, and give it a visible affordance. The button
  // carries the accessible name; the chip is icon-only and decorative.
  function makeTrigger(el, label, onActivate) {
    var name = label ? "Enlarge diagram: " + label : "Enlarge diagram";
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "jim-dgz-trigger";
    btn.setAttribute("aria-label", name);
    btn.title = name; // an icon-only cue needs a hover tooltip to explain itself

    var hint = document.createElement("span");
    hint.className = "jim-dgz-trigger__hint";
    hint.setAttribute("aria-hidden", "true");
    hint.innerHTML = HINT_SVG;

    el.parentNode.insertBefore(btn, el);
    btn.appendChild(hint);
    btn.appendChild(el);
    btn.addEventListener("click", onActivate);
    return btn;
  }

  // ── Concept SVGs (inlined by pymdownx.snippets) ───────────────────────────

  function svgLabel(svg) {
    var t = svg.querySelector("title");
    return t ? t.textContent.trim() : "";
  }

  // Rewrite the clone's ids rather than stripping them, so the copy never
  // collides with the original *and* keeps resolving its own internal
  // references. Diagrams whose edge labels sit on the line carry a
  // <mask id="...-labelcut"> that the edge group points at with
  // mask="url(#...)"; stripping ids leaves that reference dangling, and a
  // dangling mask reference renders unmasked, so the lines would be drawn
  // straight through the labels in the overlay. It happens to survive today
  // only because the in-page copy is still in the document for the reference
  // to land on -- which stops being true the moment two diagrams share an id,
  // or the page copy is removed. Suffix everything instead.
  var cloneCounter = 0;

  function cloneForOverlay(svg) {
    var copy = svg.cloneNode(true);
    var suffix = "-dgz" + (++cloneCounter);
    var renamed = Object.create(null);

    copy.querySelectorAll("[id]").forEach(function (n) {
      var oldId = n.getAttribute("id");
      renamed[oldId] = oldId + suffix;
      n.setAttribute("id", renamed[oldId]);
    });

    // Repoint every reference that could name one of those ids: url(#id) in a
    // presentation attribute or inline style, and href="#id" (<use>, <mpath>).
    if (Object.keys(renamed).length) {
      copy.querySelectorAll("*").forEach(function (n) {
        Array.prototype.slice.call(n.attributes).forEach(function (attr) {
          var value = attr.value;
          if (value.indexOf("#") === -1) return;
          var next = value.replace(/url\(\s*#([^)\s"']+)\s*\)/g, function (whole, id) {
            return renamed[id] ? "url(#" + renamed[id] + ")" : whole;
          });
          if (attr.name === "href" || attr.name === "xlink:href") {
            var target = value.charAt(0) === "#" ? value.slice(1) : null;
            if (target && renamed[target]) next = "#" + renamed[target];
          }
          if (next !== value) n.setAttribute(attr.name, next);
        });
      });
    }

    copy.removeAttribute("id");
    copy.removeAttribute("aria-labelledby");
    copy.setAttribute("aria-hidden", "true");
    copy.classList.add("jim-dgz__svg");
    return copy;
  }

  function wireInlineSvgs(root) {
    (root || document).querySelectorAll("svg.jim-diagram").forEach(function (svg) {
      if (svg.closest(".jim-dgz-trigger") || svg.closest(".jim-dgz")) return;
      var label = svgLabel(svg);
      makeTrigger(svg, label, function () {
        openOverlay(cloneForOverlay(svg), label);
      });
      // Moving the SVG into the trigger re-inserts it, which restarts its SMIL
      // timeline and re-parks the delayed packets at 0,0. Prime it as we would
      // a clone.
      primeAnimations(svg);
    });
  }

  // ── Mermaid diagrams (closed Shadow DOM; re-rendered from source) ──────────

  var mermaidSources = [];   // captured from <pre class="mermaid">, in DOM order
  var mermaidDivSeen = 0;    // shadow hosts wired so far, same order
  var wiredDivs = new WeakSet();
  var renderCounter = 0;

  function captureMermaidSources() {
    document.querySelectorAll("pre.mermaid").forEach(function (pre) {
      if (pre.dataset.jimZoomIdx !== undefined) return;
      pre.dataset.jimZoomIdx = String(mermaidSources.length);
      mermaidSources.push(pre.textContent.trim());
    });
  }

  // Mermaid puts the diagram's accessible title in <title>/<desc> when the
  // source declares one; fall back to nothing rather than inventing a label.
  function mermaidLabel(svg) {
    var t = svg.querySelector("title");
    var text = t ? t.textContent.trim() : "";
    return /^[a-z0-9-]{8,}$/i.test(text) ? "" : text; // ignore generated ids
  }

  function openMermaid(src) {
    if (typeof mermaid === "undefined" || typeof mermaid.render !== "function") return;

    // mermaid.render() needs an attached node to measure against.
    var scratch = document.createElement("div");
    scratch.style.cssText = "position:absolute;left:-9999px;top:-9999px;visibility:hidden;";
    document.body.appendChild(scratch);

    renderCounter += 1;
    var id = "jim-dgz-render-" + renderCounter;

    mermaid
      .render(id, src, scratch)
      .then(function (result) {
        scratch.remove();
        var leftover = document.getElementById(id);
        if (leftover) leftover.remove();

        var holder = document.createElement("div");
        holder.innerHTML = result.svg;
        var svg = holder.querySelector("svg");
        if (!svg) return;
        svg.classList.add("jim-dgz__svg");
        openOverlay(svg, mermaidLabel(svg));
      })
      .catch(function () {
        scratch.remove();
      });
  }

  function wireMermaidDivs(divs) {
    divs.forEach(function (div) {
      if (wiredDivs.has(div)) return;
      wiredDivs.add(div);
      var idx = mermaidDivSeen;
      mermaidDivSeen += 1;
      if (idx >= mermaidSources.length) return;
      var src = mermaidSources[idx];
      makeTrigger(div, "", function () { openMermaid(src); });
    });
  }

  // ── Boot ──────────────────────────────────────────────────────────────────

  function init() {
    captureMermaidSources();
    wireInlineSvgs(document);

    // Material swaps <pre class="mermaid"> for <div class="mermaid"> shadow
    // hosts asynchronously; watch for them and pair by insertion order.
    var observer = new MutationObserver(function (mutations) {
      captureMermaidSources();

      var newDivs = [];
      mutations.forEach(function (m) {
        m.addedNodes.forEach(function (n) {
          if (n.nodeType !== 1) return;
          if (n.tagName === "DIV" && n.classList.contains("mermaid")) newDivs.push(n);
          else if (n.querySelectorAll) {
            n.querySelectorAll("div.mermaid").forEach(function (d) { newDivs.push(d); });
          }
        });
      });
      if (newDivs.length) wireMermaidDivs(newDivs);
    });
    observer.observe(document.body, { childList: true, subtree: true });

    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeOverlay();
    });

    window.addEventListener("resize", function () {
      if (!ov || ov.dataset.open !== "true") return;
      var svg = ovStage.querySelector("svg");
      if (!svg) return;
      layoutStage(svg);
      if (view) {
        // home depends on the stage's aspect ratio, so a resize moves it. Keep
        // the reader's zoom level relative to home rather than their absolute
        // window width, so a diagram opened whole stays whole across a resize.
        var aspect = stageAspect();
        var wasAtHome = view.w >= view.home;
        view.home = homeWidth(view.base, aspect);
        if (wasAtHome) view.w = view.home;
        view.h = view.w / aspect;
        applyView();
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
