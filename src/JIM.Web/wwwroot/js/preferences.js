// JIM User Preferences - localStorage wrapper
// Used by Blazor components via JS interop for persisting user preferences
window.jimPreferences = {
    get: function (key) {
        return localStorage.getItem('jim_' + key);
    },
    set: function (key, value) {
        localStorage.setItem('jim_' + key, value);
    },
    remove: function (key) {
        localStorage.removeItem('jim_' + key);
    },
    // Mirrors a layout state the server needs before the page is interactive into a cookie. localStorage stays the
    // saved preference; the cookie only lets the host page open the layout in that state on the first render, where
    // localStorage cannot be read. Not sensitive, so readable by script by design.
    mirrorToCookie: function (name, value) {
        var secure = window.location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = 'jim_' + name + '=' + encodeURIComponent(value) + '; Path=/; Max-Age=31536000; SameSite=Lax' + secure;
    }
};
