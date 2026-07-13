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
    }
};
