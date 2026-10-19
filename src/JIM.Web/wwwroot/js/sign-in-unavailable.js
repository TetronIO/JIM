// The sign-in unavailable page (#2363): checks the identity provider's status every 10 seconds and offers to sign in
// once it is trusted, so an administrator who has just trusted its certificate sees the page turn round by itself.
// It only reads the anonymous status endpoint; nothing here can change what JIM trusts.
(function () {
    'use strict';

    var intervalMs = 10000;
    var available = ['Trusted', 'NotHttps'];

    function signInAvailable(status) {
        var title = document.getElementById('sign-in-title');
        var message = document.getElementById('sign-in-message');
        var fix = document.getElementById('sign-in-fix');
        var poll = document.getElementById('sign-in-poll');
        var button = document.getElementById('sign-in-button');

        if (title) title.textContent = 'Sign-in is available';
        if (message) message.textContent = status.message || 'JIM trusts your identity provider. Nothing needed restarting.';
        if (fix) fix.hidden = true;
        ['sign-in-chain', 'sign-in-pass-on'].forEach(function (id) {
            var element = document.getElementById(id);
            if (element) element.hidden = true;
        });
        var lock = document.getElementById('sign-in-lock');
        var open = document.getElementById('sign-in-open');
        if (lock) lock.style.display = 'none';
        if (open) open.style.display = '';
        if (poll) poll.textContent = 'Your identity provider is trusted.';
        if (button) {
            button.classList.remove('jim-idp-button-disabled');
            button.setAttribute('aria-disabled', 'false');
            button.setAttribute('tabindex', '0');
            button.focus();
        }
        document.title = 'JIM: Sign-in is available';
    }

    function check() {
        fetch('/api/v1/auth/identity-provider-trust', { headers: { 'Accept': 'application/json' }, cache: 'no-store' })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (status) {
                if (status && available.indexOf(status.state) >= 0) {
                    signInAvailable(status);
                } else {
                    window.setTimeout(check, intervalMs);
                }
            })
            .catch(function () { window.setTimeout(check, intervalMs); });
    }

    var copy = document.getElementById('sign-in-copy');
    if (copy) {
        copy.addEventListener('click', function () {
            var target = document.getElementById(copy.getAttribute('data-copy-target'));
            if (!target || !navigator.clipboard) return;
            navigator.clipboard.writeText(target.textContent).then(function () {
                copy.textContent = 'Copied';
                window.setTimeout(function () { copy.textContent = 'Copy'; }, 1500);
            });
        });
    }

    window.setTimeout(check, intervalMs);
})();
