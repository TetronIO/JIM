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
            // A couple of passes, because changing the ceiling can re-wrap content and move the measurements.
            for (var i = 0; i < 3; i++) {
                var gap = footer ? parseFloat(window.getComputedStyle(footer).marginTop) || 20 : 20;
                var rect = element.getBoundingClientRect();
                // Everything between the container's bottom edge and the footer's bottom edge moves with the
                // container, so their distance is the same however tall the container is; the ceiling is what
                // remains of the viewport after the chrome above the container and that fixed tail below it.
                var below = footer ? footer.getBoundingClientRect().bottom - rect.bottom : 0;
                var ceiling = Math.max(240, Math.round(window.innerHeight - (rect.top + window.scrollY) - below - gap));
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
    // Reports the index of the first visible row back to .NET as the reader scrolls, debounced so a flick
    // through a long list produces one call rather than hundreds. Row height is fixed by the grid's ItemSize,
    // which is what makes an index derivable from scrollTop at all.
    observe: function (selector, dotNetRef, rowHeight, debounceMs) {
        window.jimVirtualList.stop(selector);
        var element = document.querySelector(selector);
        if (!element || !rowHeight) return false;

        var entry = { element: element, timer: null };
        entry.handler = function () {
            if (entry.timer) window.clearTimeout(entry.timer);
            entry.timer = window.setTimeout(function () {
                var row = Math.max(0, Math.round(element.scrollTop / rowHeight));
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

        var target = row * rowHeight;
        var deadline = Date.now() + (timeoutMs || 5000);

        for (;;) {
            var element = document.querySelector(selector);
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
