// JIM JS interop helpers - small named functions for Blazor components to invoke.
// These exist so no component ever needs to evaluate a JavaScript string via "eval",
// which the site-wide Content Security Policy (script-src without 'unsafe-eval')
// deliberately blocks. Add new helpers here rather than reaching for eval.
window.jimInterop = {
    // Returns the current viewport width in CSS pixels.
    getWindowInnerWidth: function () {
        return window.innerWidth;
    },
    // Swaps the active theme stylesheet (the <link id="jim-theme"> element).
    setThemeStylesheet: function (href) {
        var el = document.getElementById('jim-theme');
        if (el) el.setAttribute('href', href);
    },
    // Adds or removes a class on <body>, e.g. jim-dark-mode or jim-hide-footer.
    setBodyClass: function (className, enabled) {
        document.body.classList.toggle(className, !!enabled);
    },
    // Whether the browser will let this page write to the clipboard at all. The Clipboard API is
    // gated on a secure context, so over plain HTTP navigator.clipboard is simply absent. Components
    // that offer a copy button ask this first so they can explain why it is unavailable rather than
    // presenting a button that silently does nothing.
    isClipboardAvailable: function () {
        return !!(window.isSecureContext && navigator.clipboard && navigator.clipboard.writeText);
    },
    // Writes text to the clipboard, reporting whether it worked rather than throwing. Used where the
    // caller needs to confirm the copy to the user (a password they are about to convey to someone),
    // so a failure must be visible instead of assumed.
    copyToClipboard: async function (text) {
        if (!window.jimInterop.isClipboardAvailable()) return false;
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
    // Best-effort clipboard clear, used when a dialog holding a secret closes. This cannot be relied
    // on: writing to the clipboard needs transient user activation, which closing a dialog may not
    // count as, and it does nothing about the operating system's own clipboard history. It is worth
    // attempting anyway, and worth being honest that it is not a guarantee.
    clearClipboard: async function () {
        if (!window.jimInterop.isClipboardAvailable()) return false;
        try {
            await navigator.clipboard.writeText('');
            return true;
        } catch {
            return false;
        }
    },
    // Rewrites the current URL's query string in place, without adding a history entry and without telling
    // Blazor. A virtualised list keeps its search, sort and scroll position in the URL so a refresh or a shared
    // link lands where the reader left off, and it updates that URL as the reader scrolls. NavigationManager
    // cannot be used for this: it raises a navigation, which re-runs the page's OnParametersSetAsync and so its
    // whole database load, on every scroll. replaceState is also the right history semantics here, because
    // scrolling a list is not somewhere the back button should return you to.
    replaceQueryString: function (query) {
        var url = window.location.pathname + (query ? '?' + query : '') + window.location.hash;
        window.history.replaceState(window.history.state, '', url);
    }
};

// Scroll tracking for virtualised lists. Kept apart from jimInterop because these helpers hold per-element
// state (a listener and its debounce timer) that has to be released when the component goes away.
window.jimVirtualList = {
    _observed: {},
    _fitted: {},
    // Gives a virtualised list's scroll container a height CEILING so a long list ends with the page footer
    // at the bottom of the viewport, with the same breathing room below the footer as it has above it (its own
    // top margin), and keeps that true as the window resizes. It is a ceiling, not a fixed height: a list of a
    // few rows collapses to its content like an ordinary table, with the footer following it up the page.
    // Measuring where the footer actually lands, rather than each page carrying a hand-tuned constant, is what
    // makes long lists end in the same place whatever header, filter and breadcrumb chrome sits above the grid.
    fit: function (selector) {
        window.jimVirtualList.unfit(selector);
        var element = document.querySelector(selector);
        if (!element) return false;

        var entry = { element: element, timer: null };
        entry.apply = function () {
            var footer = document.querySelector('.jim-page-footer');
            // A dialog is its own window onto the page: it is positioned against the viewport, scrolls its own
            // body, and the page footer behind it describes geometry the dialog has no relationship with. A grid
            // in one takes a readable share of the viewport instead of measuring anything.
            var inDialog = !!element.closest('.mud-dialog');
            // A grid that starts below the fold cannot be sized against a viewport it is not in: the measurement
            // below goes to nothing and the grid collapses to its floor. Such a grid takes the same readable
            // share, which is what the reader sees once they scroll to it.
            var readableShare = Math.min(480, Math.round(window.innerHeight * 0.6));
            // A couple of passes, because changing the ceiling can re-wrap content and move the measurements.
            for (var i = 0; i < 3; i++) {
                var gap = footer ? parseFloat(window.getComputedStyle(footer).marginTop) || 20 : 20;
                var rect = element.getBoundingClientRect();
                var ceiling;
                if (inDialog) {
                    ceiling = readableShare;
                } else {
                    // Everything between the container's bottom edge and the footer's bottom edge moves with the
                    // container, so their distance is the same however tall the container is; the ceiling is what
                    // remains of the viewport after the chrome above the container and that fixed tail below it.
                    // Measured in viewport coordinates throughout: getBoundingClientRect is already viewport
                    // relative, so adding the page's scroll offset (as this once did) shrank every grid on a page
                    // long enough to scroll, which is every page holding a grid among other content.
                    var below = footer ? footer.getBoundingClientRect().bottom - rect.bottom : 0;
                    ceiling = Math.round(window.innerHeight - Math.max(0, rect.top) - below - gap);
                    if (ceiling < 240) ceiling = readableShare;
                }
                var current = parseFloat(element.style.maxHeight) || 0;
                if (Math.abs(ceiling - current) < 1) break;
                element.style.maxHeight = ceiling + 'px';
            }
        };
        entry.handler = function () {
            if (entry.timer) window.clearTimeout(entry.timer);
            entry.timer = window.setTimeout(entry.apply, 100);
        };

        entry.apply();
        window.addEventListener('resize', entry.handler);
        // The page keeps moving after the grid appears: filter summaries and alerts render above it as their
        // data arrives, and a one-shot measurement goes stale the moment they do. Watching the document's own
        // height re-fits whenever anything moves the footer; this converges rather than looping, because a fit
        // that already holds computes a delta of zero and writes nothing.
        entry.observer = new ResizeObserver(entry.handler);
        entry.observer.observe(document.body);
        window.jimVirtualList._fitted[selector] = entry;
        return true;
    },
    // Releases the resize listener fit registered. Deliberately separate from stop(): observe() replaces the
    // scroll listener via stop() while the fit must live on, so the two lifecycles cannot share a teardown.
    unfit: function (selector) {
        var entry = window.jimVirtualList._fitted[selector];
        if (!entry) return;

        if (entry.timer) window.clearTimeout(entry.timer);
        window.removeEventListener('resize', entry.handler);
        if (entry.observer) entry.observer.disconnect();
        delete window.jimVirtualList._fitted[selector];
    },
    // The height a rendered row actually occupies, which is what an index has to be derived from. The grid tells
    // the virtualiser an ItemSize, but that is an estimate: padding, a chip, a two-line cell and the theme's own
    // spacing all land on top of it, and a row set out at 50 renders at 56. Deriving an index from the estimate
    // put every deep link and every restored position out by the difference (a twelfth of the list, and growing
    // with how far down it was). Measured off a real row, falling back to the estimate before any row exists.
    measuredRowHeight: function (element, estimate) {
        var rows = element.querySelectorAll('tbody tr');
        for (var i = 0; i < rows.length; i++) {
            var height = rows[i].getBoundingClientRect().height;
            // A virtualiser brackets its rows with spacer rows sized to the scroll area, so anything absurdly
            // tall is the spacer rather than a row; anything at zero is not laid out yet.
            if (height > 8 && height < 400) return height;
        }
        return estimate;
    },
    // Reports the index of the first visible row back to .NET as the reader scrolls, debounced so a flick
    // through a long list produces one call rather than hundreds. Every row is the same height, which is what
    // makes an index derivable from scrollTop at all; see measuredRowHeight for where that height comes from.
    observe: function (selector, dotNetRef, rowHeight, debounceMs) {
        window.jimVirtualList.stop(selector);
        var element = document.querySelector(selector);
        if (!element || !rowHeight) return false;

        var entry = { element: element, timer: null };
        entry.handler = function () {
            if (entry.timer) window.clearTimeout(entry.timer);
            entry.timer = window.setTimeout(function () {
                var row = Math.max(0, Math.round(element.scrollTop / window.jimVirtualList.measuredRowHeight(element, rowHeight)));
                // The circuit can go away between the scroll and the timer firing; there is nothing to recover
                // from that, and throwing here would surface in the browser console for no one's benefit.
                dotNetRef.invokeMethodAsync('OnFirstVisibleRowChanged', row).catch(function () { });
            }, debounceMs);
        };

        element.addEventListener('scroll', entry.handler, { passive: true });
        window.jimVirtualList._observed[selector] = entry;
        return true;
    },
    // Scrolls to a row once that row exists to scroll to, reporting whether it got there.
    //
    // The waiting is the point. A virtualiser sizes its scroll area from the total row count, which arrives with
    // the first window of data, so at the moment a restoring page asks for row 3868 the container is either absent
    // or only a screen tall, and setting scrollTop is silently clamped to the current bottom. Blazor gives no
    // convenient re-render to retry on either: the grid loading its data does not re-render the page hosting it.
    // Polling here keeps that patience next to the DOM state it is waiting for, and gives the caller one definitive
    // answer instead of an attempt it has to second-guess.
    //
    // Giving up is a real outcome, not a failure to handle: a link may name a row that no longer exists because the
    // match set has shrunk since it was shared, and the reader should land at the top rather than nowhere.
    scrollToRow: async function (selector, row, rowHeight, timeoutMs) {
        if (!rowHeight) return false;

        var deadline = Date.now() + (timeoutMs || 5000);

        for (;;) {
            var element = document.querySelector(selector);
            // Measured per attempt, not once: the first attempts run before any row is laid out, when there is
            // nothing to measure and the estimate is all there is.
            var target = element ? row * window.jimVirtualList.measuredRowHeight(element, rowHeight) : 0;
            if (element && target <= element.scrollHeight - element.clientHeight) {
                element.scrollTop = target;
                return true;
            }
            if (Date.now() >= deadline) return false;
            await new Promise(function (resolve) { window.setTimeout(resolve, 100); });
        }
    },
    stop: function (selector) {
        var entry = window.jimVirtualList._observed[selector];
        if (!entry) return;

        if (entry.timer) window.clearTimeout(entry.timer);
        entry.element.removeEventListener('scroll', entry.handler);
        delete window.jimVirtualList._observed[selector];
    }
};

// The causality Table view's drag-resizable nav column. The live width lives entirely in the DOM (a
// --tv-nav-width custom property on the panel element the grid reads via var(--tv-nav-width, 240px)),
// never round-tripped through Blazor: a per-pixel SignalR message while dragging would lag visibly.
// Keyed by the handle element (not a selector) because bUnit and the component both already hold
// ElementReferences, and a selector would have to be unique per panel on a page that could show more
// than one causality panel.
window.jimTableViewResizer = {
    _attached: new Map(),
    // Clamps a candidate width between minPx and maxFraction of the panel's own current width.
    _clamp: function (panel, width, minPx, maxFraction) {
        var max = Math.max(minPx, Math.round(panel.getBoundingClientRect().width * maxFraction));
        return Math.min(Math.max(Math.round(width), minPx), max);
    },
    _currentWidth: function (panel, minPx) {
        var nav = panel.querySelector('.tv-nav');
        return nav ? nav.getBoundingClientRect().width : minPx;
    },
    // Wires pointer-drag resizing and a double-click reset onto handle. setPointerCapture means every
    // subsequent pointer event targets handle directly until release, wherever the pointer physically
    // travels, so one set of listeners on the handle itself is enough; no document-level listener needed.
    attach: function (handle, panel, minPx, maxFraction) {
        window.jimTableViewResizer.detach(handle);
        if (!handle || !panel) return false;

        var self = window.jimTableViewResizer;
        var state = { dragging: false, startX: 0, startWidth: 0 };

        var entry = {
            onPointerDown: function (e) {
                if (typeof e.button === 'number' && e.button !== 0) return;
                state.dragging = true;
                state.startX = e.clientX;
                state.startWidth = self._currentWidth(panel, minPx);
                handle.setPointerCapture(e.pointerId);
                e.preventDefault();
            },
            onPointerMove: function (e) {
                if (!state.dragging) return;
                var width = self._clamp(panel, state.startWidth + (e.clientX - state.startX), minPx, maxFraction);
                panel.style.setProperty('--tv-nav-width', width + 'px');
            },
            onPointerUp: function (e) {
                state.dragging = false;
                if (handle.hasPointerCapture(e.pointerId)) handle.releasePointerCapture(e.pointerId);
            },
            // Removing the property lets the grid's var(--tv-nav-width, 240px) fallback take back over,
            // so the default only needs stating once, in the CSS.
            onDoubleClick: function () {
                panel.style.removeProperty('--tv-nav-width');
            }
        };

        handle.addEventListener('pointerdown', entry.onPointerDown);
        handle.addEventListener('pointermove', entry.onPointerMove);
        handle.addEventListener('pointerup', entry.onPointerUp);
        handle.addEventListener('pointercancel', entry.onPointerUp);
        handle.addEventListener('dblclick', entry.onDoubleClick);

        self._attached.set(handle, entry);
        return true;
    },
    // Moves the handle by deltaPx (negative to shrink), for the keyboard equivalent of a drag.
    nudge: function (panel, deltaPx, minPx, maxFraction) {
        if (!panel) return false;
        var self = window.jimTableViewResizer;
        var width = self._clamp(panel, self._currentWidth(panel, minPx) + deltaPx, minPx, maxFraction);
        panel.style.setProperty('--tv-nav-width', width + 'px');
        return true;
    },
    detach: function (handle) {
        var entry = window.jimTableViewResizer._attached.get(handle);
        if (!entry) return;

        handle.removeEventListener('pointerdown', entry.onPointerDown);
        handle.removeEventListener('pointermove', entry.onPointerMove);
        handle.removeEventListener('pointerup', entry.onPointerUp);
        handle.removeEventListener('pointercancel', entry.onPointerUp);
        handle.removeEventListener('dblclick', entry.onDoubleClick);
        window.jimTableViewResizer._attached.delete(handle);
    }
};

// The causality Table view's drag-resizable table columns (#1519 Table view fix 10), alongside
// jimTableViewResizer above. Unlike the nav's single handle, a grip exists per header cell and Blazor
// re-renders the header row on every sort/selection change, so listeners are delegated onto the table
// itself (attached once) rather than onto each grip; a grip re-rendered under the same table keeps
// working with no re-attach. The table starts in ordinary auto layout: only the first drag or nudge
// snapshots every header's current width onto its <col> and switches the table into table-layout: fixed
// (see the CSS), because a fixed layout only honours <col> widths once the table itself has an explicit
// width, so that width is kept in step with the sum of the columns on every change.
window.jimTableViewColumnResizer = {
    _attached: new Map(),
    _headerCells: function (table) {
        var row = table.tHead && table.tHead.rows.length ? table.tHead.rows[0] : null;
        return row ? Array.prototype.slice.call(row.cells) : [];
    },
    _cols: function (table) {
        var group = table.querySelector('colgroup');
        return group ? Array.prototype.slice.call(group.children) : [];
    },
    _sumWidth: function (table) {
        var cols = window.jimTableViewColumnResizer._cols(table);
        var total = 0;
        for (var i = 0; i < cols.length; i++) total += parseFloat(cols[i].style.width) || 0;
        return total;
    },
    // Snapshots every header's rendered width onto its <col> the first time this table is resized, so
    // table-layout: fixed has something to honour; a no-op on every call after the first.
    _ensureFixed: function (table) {
        if (table.classList.contains('tv-cols-fixed')) return;
        var self = window.jimTableViewColumnResizer;
        var ths = self._headerCells(table);
        var cols = self._cols(table);
        var total = 0;
        for (var i = 0; i < ths.length && i < cols.length; i++) {
            var width = Math.round(ths[i].getBoundingClientRect().width);
            cols[i].style.width = width + 'px';
            total += width;
        }
        table.classList.add('tv-cols-fixed');
        table.style.width = total + 'px';
    },
    _columnIndex: function (table, grip) {
        var th = grip.closest('th');
        if (!th) return -1;
        return window.jimTableViewColumnResizer._headerCells(table).indexOf(th);
    },
    // Widens/narrows one column by deltaPx (negative to shrink), clamped to minPx with no maximum, and
    // keeps the table's own width equal to the sum of its columns.
    _resizeColumn: function (table, columnIndex, deltaPx, minPx) {
        var self = window.jimTableViewColumnResizer;
        self._ensureFixed(table);
        var cols = self._cols(table);
        if (columnIndex < 0 || columnIndex >= cols.length) return;
        var current = parseFloat(cols[columnIndex].style.width) || 0;
        cols[columnIndex].style.width = Math.max(minPx, Math.round(current + deltaPx)) + 'px';
        table.style.width = self._sumWidth(table) + 'px';
    },
    // Wires delegated pointer-drag resizing and a double-click reset onto the table once; e.target is
    // checked against '.tv-col-grip' on every event, so grips Blazor re-renders keep working unattended.
    attach: function (table, minPx) {
        window.jimTableViewColumnResizer.detach(table);
        if (!table) return false;

        var self = window.jimTableViewColumnResizer;
        var state = { dragging: false, columnIndex: -1, startX: 0, startWidth: 0 };

        var entry = {
            onPointerDown: function (e) {
                var grip = e.target.closest && e.target.closest('.tv-col-grip');
                if (!grip || !table.contains(grip)) return;
                if (typeof e.button === 'number' && e.button !== 0) return;

                var columnIndex = self._columnIndex(table, grip);
                if (columnIndex < 0) return;

                self._ensureFixed(table);
                var cols = self._cols(table);
                state.dragging = true;
                state.columnIndex = columnIndex;
                state.startX = e.clientX;
                state.startWidth = parseFloat(cols[columnIndex].style.width) || 0;
                grip.setPointerCapture(e.pointerId);
                e.preventDefault();
            },
            onPointerMove: function (e) {
                if (!state.dragging) return;
                var cols = self._cols(table);
                var width = Math.max(minPx, Math.round(state.startWidth + (e.clientX - state.startX)));
                cols[state.columnIndex].style.width = width + 'px';
                table.style.width = self._sumWidth(table) + 'px';
            },
            onPointerUp: function (e) {
                state.dragging = false;
                var grip = e.target.closest && e.target.closest('.tv-col-grip');
                if (grip && grip.hasPointerCapture(e.pointerId)) grip.releasePointerCapture(e.pointerId);
            },
            onDoubleClick: function (e) {
                var grip = e.target.closest && e.target.closest('.tv-col-grip');
                if (!grip || !table.contains(grip)) return;
                self.reset(table);
            }
        };

        table.addEventListener('pointerdown', entry.onPointerDown);
        table.addEventListener('pointermove', entry.onPointerMove);
        table.addEventListener('pointerup', entry.onPointerUp);
        table.addEventListener('pointercancel', entry.onPointerUp);
        table.addEventListener('dblclick', entry.onDoubleClick);

        self._attached.set(table, entry);
        return true;
    },
    // The keyboard equivalent of a drag: moves one column by deltaPx (negative to shrink).
    nudge: function (table, columnIndex, deltaPx, minPx) {
        if (!table) return false;
        window.jimTableViewColumnResizer._resizeColumn(table, columnIndex, deltaPx, minPx);
        return true;
    },
    // Removes tv-cols-fixed, every <col>'s inline width and the table's own inline width, so the CSS
    // defaults (auto layout, 100% width) take back over. No width is persisted anywhere.
    reset: function (table) {
        if (!table) return;
        var cols = window.jimTableViewColumnResizer._cols(table);
        for (var i = 0; i < cols.length; i++) cols[i].style.removeProperty('width');
        table.classList.remove('tv-cols-fixed');
        table.style.removeProperty('width');
    },
    detach: function (table) {
        var entry = window.jimTableViewColumnResizer._attached.get(table);
        if (!entry) return;

        table.removeEventListener('pointerdown', entry.onPointerDown);
        table.removeEventListener('pointermove', entry.onPointerMove);
        table.removeEventListener('pointerup', entry.onPointerUp);
        table.removeEventListener('pointercancel', entry.onPointerUp);
        table.removeEventListener('dblclick', entry.onDoubleClick);
        window.jimTableViewColumnResizer._attached.delete(table);
    }
};
