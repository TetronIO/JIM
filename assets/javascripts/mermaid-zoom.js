/**
 * mermaid-zoom.js
 *
 * Makes Mermaid diagrams clickable to view full-size in an overlay modal.
 *
 * MkDocs Material renders Mermaid into a closed Shadow DOM, making the SVG
 * inaccessible via JS. This script takes a different approach: it clones the
 * visible shadow host element (including its rendered shadow DOM visuals via
 * CSS) by using element.innerHTML on a fresh re-render via the Mermaid API,
 * which is already initialised on the page.
 *
 * We capture each diagram's source text from <pre class="mermaid"> before
 * Material replaces it, then on click we call mermaid.render() with a
 * temporary attached DOM node so the render succeeds, and display the
 * resulting SVG in a simple custom modal.
 */
(function () {
  "use strict";

  var diagramSources = [];
  var divInsertionOrder = 0;
  var processed = new WeakSet();
  var renderCounter = 0;

  // ── Modal ──────────────────────────────────────────────────────────────────

  var modal = null;
  var modalContent = null;

  function buildModal() {
    if (modal) return;

    modal = document.createElement("div");
    modal.id = "jim-diagram-modal";
    modal.style.cssText = [
      "display:none",
      "position:fixed",
      "inset:0",
      "z-index:9999",
      "background:rgba(0,0,0,0.85)",
      "cursor:zoom-out",
      "overflow:auto",
      "padding:40px 24px",
      "box-sizing:border-box",
    ].join(";");

    modalContent = document.createElement("div");
    modalContent.style.cssText = [
      "margin:auto",
      "max-width:95vw",
      "cursor:default",
    ].join(";");

    modal.appendChild(modalContent);
    document.body.appendChild(modal);

    // Close on backdrop click
    modal.addEventListener("click", function (e) {
      if (e.target === modal) closeModal();
    });

    // Close on Esc
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeModal();
    });
  }

  function openModal(svgString) {
    buildModal();

    var isDark =
      (document.querySelector("[data-md-color-scheme]") || {})
        .getAttribute &&
      document.querySelector("[data-md-color-scheme]")
        .getAttribute("data-md-color-scheme") === "slate";
    var bg = isDark ? "#051526" : "#ffffff";

    modalContent.style.background = bg;
    modalContent.style.borderRadius = "8px";
    modalContent.style.padding = "24px";
    modalContent.innerHTML = svgString;

    // Make the SVG fill the available width
    var svg = modalContent.querySelector("svg");
    if (svg) {
      svg.style.width = "100%";
      svg.style.height = "auto";
      svg.removeAttribute("width");
    }

    modal.style.display = "block";
    document.body.style.overflow = "hidden";
  }

  function closeModal() {
    if (modal) modal.style.display = "none";
    document.body.style.overflow = "";
    if (modalContent) modalContent.innerHTML = "";
  }

  // ── Diagram processing ────────────────────────────────────────────────────

  function captureSourcesFromPres() {
    document.querySelectorAll("pre.mermaid").forEach(function (pre) {
      if (pre.dataset.jimZoomIdx !== undefined) return;
      var idx = diagramSources.length;
      diagramSources.push(pre.textContent.trim());
      pre.dataset.jimZoomIdx = idx;
    });
  }

  function addZoomToDiv(hostDiv, src) {
    if (processed.has(hostDiv)) return;
    processed.add(hostDiv);

    // Visual affordance: zoom-in cursor and a subtle hover hint
    hostDiv.style.cursor = "zoom-in";
    hostDiv.title = "Click to view full size";

    hostDiv.addEventListener("click", function () {
      if (
        typeof mermaid === "undefined" ||
        typeof mermaid.render !== "function"
      ) {
        return;
      }

      // Attach a temporary off-screen container so mermaid.render() can
      // measure and produce a complete SVG.
      var container = document.createElement("div");
      container.style.cssText =
        "position:absolute;left:-9999px;top:-9999px;visibility:hidden;";
      document.body.appendChild(container);

      renderCounter++;
      var id = "__mermaid_zoom_render_" + renderCounter;

      mermaid
        .render(id, src, container)
        .then(function (result) {
          document.body.removeChild(container);
          // Also remove the temp element mermaid may have left in the body
          var leftover = document.getElementById(id);
          if (leftover) leftover.remove();
          openModal(result.svg);
        })
        .catch(function () {
          if (document.body.contains(container)) {
            document.body.removeChild(container);
          }
        });
    });
  }

  function processNewDivs(newDivs) {
    newDivs.forEach(function (div) {
      if (processed.has(div)) return;
      var idx = divInsertionOrder++;
      if (idx < diagramSources.length) {
        addZoomToDiv(div, diagramSources[idx]);
      }
    });
  }

  function init() {
    captureSourcesFromPres();

    var observer = new MutationObserver(function (mutations) {
      captureSourcesFromPres();

      var newDivs = [];
      mutations.forEach(function (m) {
        m.addedNodes.forEach(function (n) {
          if (n.nodeType !== 1) return;
          if (n.tagName === "DIV" && n.classList.contains("mermaid")) {
            newDivs.push(n);
          } else if (n.querySelectorAll) {
            n.querySelectorAll("div.mermaid").forEach(function (d) {
              newDivs.push(d);
            });
          }
        });
      });

      if (newDivs.length > 0) {
        processNewDivs(newDivs);
      }
    });

    observer.observe(document.body, { childList: true, subtree: true });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
