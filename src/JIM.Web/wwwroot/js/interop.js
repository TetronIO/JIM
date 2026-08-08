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
    }
};
