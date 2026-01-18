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
    }
};
