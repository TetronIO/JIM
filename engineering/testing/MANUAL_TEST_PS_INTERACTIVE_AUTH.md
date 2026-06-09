# Manual Test: PowerShell Module Interactive Authentication

This guide is a manual regression script for the JIM PowerShell module's interactive
(browser-based SSO) authentication, refresh-token persistence, and disconnect
behaviour. It complements the automated Pester suite (`src/JIM.PowerShell/Tests/`),
which cannot exercise a real browser, a real OS credential store, or new-terminal
behaviour.

## Prerequisites

1. **JIM stack running** with SSO configured (see [SSO_SETUP_GUIDE.md](../../docs/administration/sso-setup.md)).
2. **Identity provider configured** for the PowerShell public client with a loopback redirect URI:
   - Keycloak (bundled dev realm): public client `jim-powershell` with `http://localhost:8400/callback/` (and `8401-8409` as fallback ports).
   - Entra ID: loopback redirect URI plus public client flows enabled.
   - AD FS: native application with loopback redirect.
3. **PowerShell 7.0+**.
4. **A web browser** on the same machine as the PowerShell session.

> **Run these tests on your host machine, not inside the devcontainer.** Interactive
> SSO needs a browser and host-reachable IdP endpoints; the devcontainer has neither,
> so the browser flow only works from a host PowerShell session. See
> [PowerShell Module (developer guide)](../../docs/developer/powershell-module.md) for
> the host-install workflow and the reasoning. API-key auth (Test A4) is the only part
> that also works inside the devcontainer.

## Test Environment Setup

Install the branch's module into your host module path so a new terminal autoloads it
(this matters for the persistence tests, which open fresh terminals). On macOS/Linux:

```powershell
$dest = "$HOME/.local/share/powershell/Modules/JIM"
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item <repo>/src/JIM.PowerShell $dest -Recurse
Import-Module JIM -Force

# Confirm you loaded the copy you just placed, and its version
(Get-Module JIM).Path
(Get-Module JIM).Version    # expect 0.11.0 (or current)
```

On Windows use `"$env:USERPROFILE\Documents\PowerShell\Modules\JIM"` as `$dest`.

Throughout, the bundled dev URL is `http://localhost:5200`. Replace it with your
instance URL where appropriate.

### Inspecting the credential store

Several tests verify whether a refresh token is persisted. The cache key is the
normalised base URL, `scheme://host:port` (for example `http://localhost:5200`).

| Platform | Check whether a token is stored |
|----------|---------------------------------|
| macOS | `security find-generic-password -s JIM -w` (prints the token, or errors if none) |
| Linux (libsecret) | `secret-tool lookup service JIM url http://localhost:5200` |
| Windows | `cmdkey /list \| Select-String 'JIM:'` (shows the target if present) |

A non-empty result means a token is persisted for that instance; an error or empty
result means none is.

---

## Part A: Authentication basics

### Test A1: Basic interactive authentication

1. Start from a clean slate (clears session and any stored token for this instance):
   ```powershell
   Disconnect-JIM -Url "http://localhost:5200"
   ```
2. Authenticate:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```
3. **Expected behaviour:**
   - Console shows `Starting interactive authentication with JIM...`, then
     `Opening browser for authentication...` and `Waiting for authentication to complete...`.
   - The default browser opens to your IdP's login page; after sign-in the page shows a success message.
   - Console shows the banner `Connected to JIM server v<version> at http://localhost:5200`.
4. Verify:
   ```powershell
   Test-JIMConnection
   ```
   Expected: `Connected = True`, `AuthMethod = OAuth`, a future `TokenExpiresAt`.

**Pass criteria:** browser opens, sign-in completes, `AuthMethod` is `OAuth`.

---

### Test A2: API call after authentication

1. While connected from A1:
   ```powershell
   Get-JIMConnectedSystem
   Get-JIMMetaverseObjectType
   ```
2. **Expected:** both return data (possibly empty lists) with no authentication errors or re-prompts.

**Pass criteria:** API calls succeed without re-authenticating.

---

### Test A3: Force re-authentication

1. While connected, force a fresh sign-in without disconnecting:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -Force
   ```
2. **Expected:** the browser opens again even though a valid session exists; a new token is obtained and the stored token is overwritten.

**Pass criteria:** `-Force` always triggers a new browser flow.

---

### Test A4: API key authentication (works in the devcontainer too)

1. Obtain an API key (JIM UI under **Admin > API Keys**, or the dev infrastructure key from `.env`).
2. Disconnect, then connect with the key:
   ```powershell
   Disconnect-JIM
   Connect-JIM -Url "http://localhost:5200" -ApiKey "jim_ak_xxxxxxxx"
   ```
3. **Expected:** no browser opens; immediate connection. `Test-JIMConnection` shows `AuthMethod = ApiKey` and an empty `TokenExpiresAt`.
4. Confirm API keys are never persisted:
   ```powershell
   # macOS example; nothing should be stored for an API-key-only session
   Disconnect-JIM
   security find-generic-password -s JIM -w
   ```

**Pass criteria:** `AuthMethod` is `ApiKey`; no token is written to the credential store.

---

### Test A5: Invalid and unreachable URLs

1. Invalid format:
   ```powershell
   Connect-JIM -Url "not-a-url"
   ```
   **Expected:** `Invalid URL format...`, no browser opens.
2. Unreachable host:
   ```powershell
   Connect-JIM -Url "https://invalid.example.com"
   ```
   **Expected:** an error fetching the OAuth/OIDC configuration; no connection established.

**Pass criteria:** bad URLs are rejected with helpful errors.

---

### Test A6: Timeout and browser cancellation

1. Short timeout, do not complete sign-in:
   ```powershell
   Disconnect-JIM
   Connect-JIM -Url "http://localhost:5200" -TimeoutSeconds 30
   # Leave the browser sign-in incomplete
   ```
   **Expected:** after ~30 seconds, an authentication timeout error; no connection.
2. Cancellation: start `Connect-JIM` again and close the browser tab without signing in.
   **Expected:** PowerShell reports an authentication failure/timeout; no connection.

**Pass criteria:** timeout is respected and cancellation is handled gracefully.

---

## Part B: Refresh-token persistence and silent reconnect

This is the headline feature: an interactive sign-in persists the refresh token in the
OS credential store so a new terminal reconnects without a browser.

### Test B1: First sign-in persists the token

1. Clean slate, then sign in interactively:
   ```powershell
   Disconnect-JIM -Url "http://localhost:5200"
   Connect-JIM -Url "http://localhost:5200"
   ```
2. Verify a token was stored (see [Inspecting the credential store](#inspecting-the-credential-store)):
   ```powershell
   security find-generic-password -s JIM -w   # macOS; should print a token
   ```

**Pass criteria:** after an interactive sign-in, a refresh token is present in the store.

---

### Test B2: Silent reconnect in a new terminal (key test)

1. **Close the terminal entirely** and open a **new** one (this clears the in-memory session, forcing the persisted-token path).
2. Reconnect:
   ```powershell
   Import-Module JIM
   Connect-JIM -Url "http://localhost:5200" -Verbose
   ```
3. **Expected behaviour:**
   - `-Verbose` shows `Found a persisted refresh token; attempting silent reconnect...`.
   - **No browser opens.**
   - Green message: `Connected to JIM using cached credentials (no browser sign-in required).`
   - `Test-JIMConnection` shows `Connected = True`, `AuthMethod = OAuth`.

**Pass criteria:** a fresh terminal reconnects with no browser.

---

### Test B3: In-session reconnect uses the cached access token

1. In the same session as B2, run again:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -Verbose
   ```
2. **Expected:** instant return with status `Connected (cached)` from the in-memory access token; no call to the IdP and no credential-store read (the valid access token short-circuits, with a 5-minute expiry buffer).

**Pass criteria:** repeated connects in one session do not hit the IdP.

---

### Test B4: `-NoPersist` does not write to the store

1. Clear any stored token, then connect without persisting:
   ```powershell
   Disconnect-JIM -Url "http://localhost:5200"
   Connect-JIM -Url "http://localhost:5200" -NoPersist
   ```
2. Confirm nothing was stored:
   ```powershell
   security find-generic-password -s JIM -w   # macOS; should error / be empty
   ```
3. Close the terminal, open a new one, and reconnect:
   ```powershell
   Import-Module JIM
   Connect-JIM -Url "http://localhost:5200"
   ```
   **Expected:** a browser opens (nothing was persisted to reconnect silently).

**Pass criteria:** `-NoPersist` leaves the store untouched; a new session must sign in again.

---

### Test B5: Headless / no-keyring fallback (Linux without libsecret only)

1. On a headless Linux host with no `secret-tool`, connect interactively (where feasible) or inspect the notice path.
2. **Expected:** a grey notice that token persistence is unavailable and a pointer to use `-ApiKey` for unattended use; the session still works in-memory.

**Pass criteria:** persistence degrades gracefully with a clear notice; no error.

---

## Part C: Disconnect-and-forget semantics

`Disconnect-JIM` clears the session **and** removes the persisted token for the
targeted instance. Removal is auth-agnostic.

### Test C1: Default forgets the connected instance

1. With a persisted token present (from B1/B2) and connected:
   ```powershell
   Disconnect-JIM
   ```
   **Expected:** `Disconnected from JIM at http://localhost:5200`, then
   `Removed persisted credentials for http://localhost:5200 from the OS credential store.`
2. Confirm the token is gone, and that a new terminal now needs a browser:
   ```powershell
   security find-generic-password -s JIM -w   # macOS; should error / be empty
   ```

**Pass criteria:** the default disconnect removes the connected instance's stored token.

---

### Test C2: `-Url` forgets a specific instance while not connected

1. Disconnect fully, then target a specific instance by URL:
   ```powershell
   Disconnect-JIM
   Disconnect-JIM -Url "http://localhost:5200"
   ```
   **Expected:** removes the stored token for that instance even though there is no active session. If nothing was stored, it is a quiet no-op.

**Pass criteria:** `-Url` removes a specific instance's token without requiring a connection.

---

### Test C3: `-All` forgets every instance

1. Persist tokens for two or more instances (if available), then:
   ```powershell
   Disconnect-JIM -All
   ```
   **Expected:** `Removed N persisted JIM credential(s) from the OS credential store.` where N matches the number of instances stored.

**Pass criteria:** `-All` clears every JIM token on the machine.

---

### Test C4: Auth-agnostic removal during an API-key session

1. Ensure an SSO token exists for the instance (B1), then connect to the **same** instance with an API key and disconnect:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -ApiKey "jim_ak_xxxxxxxx"
   Disconnect-JIM
   security find-generic-password -s JIM -w   # macOS; the SSO token should be gone
   ```
2. **Expected:** the in-memory API-key session is cleared and the previously stored SSO token for that instance is removed too (disconnect forgets the instance regardless of how the current session authenticated).

**Pass criteria:** disconnect removes the instance's stored token even from an API-key session.

---

### Test C5: Bare disconnect while not connected is a no-op

1. With no active session and no stored token:
   ```powershell
   Disconnect-JIM
   ```
   **Expected:** no error and no store change (a verbose message only). Safe to call as a reset.

**Pass criteria:** idempotent, no error.

---

## Part D: Token renewal and stale-token fallback

### Test D1: Automatic in-session refresh

1. Connect interactively and note the expiry:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   (Test-JIMConnection).TokenExpiresAt
   ```
2. Let the access token approach expiry (or set a short access-token lifetime in the IdP), then make a call:
   ```powershell
   Get-JIMConnectedSystem
   (Test-JIMConnection).TokenExpiresAt   # should be later if a refresh occurred
   ```

**Pass criteria:** an expiring access token is refreshed silently; the API call succeeds.

---

### Test D2: Revoked refresh token falls back to the browser

This verifies that a dead persisted token does not wedge the user out: the silent
reconnect fails and the module falls back to a browser sign-in, dropping the stale
token.

1. With a persisted token (B1) and **not** in an in-memory session, revoke the refresh
   token at the IdP. For the bundled Keycloak dev realm the refresh token is an
   **offline** token, so revoke the offline session (a plain user logout does not):
   - Keycloak admin console: **Users > (your user) > Sessions**, or delete the offline
     session via the admin REST API. The session must be removed with the offline flag,
     for example `DELETE /admin/realms/jim/sessions/{sessionId}?isOffline=true`.
   - Quick proxy if you cannot revoke: corrupt the stored value, for example on macOS
     `security add-generic-password -a "http://localhost:5200" -s JIM -U -w "garbage"`.
2. In a **new** terminal, attempt reconnect:
   ```powershell
   Import-Module JIM
   Connect-JIM -Url "http://localhost:5200" -Verbose
   ```
3. **Expected behaviour:**
   - `-Verbose` shows `Found a persisted refresh token; attempting silent reconnect...`
     then `Silent reconnect with persisted token failed, falling back to browser sign-in`.
   - The browser opens for a fresh sign-in.
   - The stale token is removed from the store. Verify **after** the fallback but
     **before** completing the new sign-in (a successful sign-in writes a fresh token):
     ```powershell
     security find-generic-password -s JIM -w   # macOS; should error / be empty
     ```

**Pass criteria:** a revoked token triggers a clean browser fallback and the stale token is dropped (no repeated retries against the dead token).

---

## Troubleshooting

### Browser does not open
- Confirm a default browser is configured and you are not running elevated.
- Confirm the loopback callback port (8400, falling back to 8401-8409) is not blocked.

### Authentication fails after browser sign-in
- Verify the loopback redirect URI on the public client: `http://localhost:8400/callback/` (exact match; add `8401-8409` if 8400 is busy).
- Confirm the IdP client is public / allows public client flows.

### Silent reconnect always opens a browser
- Confirm a token is actually stored (see the inspection table). `-NoPersist` or a headless no-keyring host both result in nothing being stored.
- Confirm you opened a **new** terminal; the same session short-circuits on the in-memory access token (Test B3), which is expected.

### Token refresh / silent reconnect fails unexpectedly
- Ensure the `offline_access` scope is permitted on the public client, so the IdP issues a refresh token.
- Check the refresh-token (offline session) lifetime in the IdP.

---

## Test Results Template

| Test | Description | Pass/Fail | Notes |
|------|-------------|-----------|-------|
| A1 | Basic interactive auth | | |
| A2 | API call after auth | | |
| A3 | Force re-auth | | |
| A4 | API key auth (no persistence) | | |
| A5 | Invalid / unreachable URL | | |
| A6 | Timeout / cancellation | | |
| B1 | First sign-in persists token | | |
| B2 | Silent reconnect (new terminal) | | |
| B3 | In-session cached reconnect | | |
| B4 | `-NoPersist` writes nothing | | |
| B5 | Headless no-keyring fallback | | |
| C1 | Disconnect forgets connected instance | | |
| C2 | `-Url` forgets specific instance | | |
| C3 | `-All` forgets every instance | | |
| C4 | Auth-agnostic removal (API-key session) | | |
| C5 | Bare disconnect no-op | | |
| D1 | Automatic in-session refresh | | |
| D2 | Revoked token browser fallback | | |

**Tested by:** _______________
**Date:** _______________
**JIM Version:** _______________
**IdP:** _______________
