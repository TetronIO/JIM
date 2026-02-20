# Manual Test: PowerShell Module Interactive Authentication

This guide walks through testing the interactive browser-based authentication for the JIM PowerShell module.

## Prerequisites

1. **JIM stack running** with SSO configured (see [SSO_SETUP_GUIDE.md](../SSO_SETUP_GUIDE.md))
2. **Identity provider configured** for PowerShell module (Step 4a in SSO guide):
   - Entra ID: Loopback redirect URI + public client flows enabled
   - AD FS: Native application with loopback redirect
   - Keycloak: Public client with loopback redirect
3. **PowerShell 7.0+** installed
4. **Web browser** available (Chrome, Edge, Firefox, etc.)

> **Note for devcontainer users:** The default JIM URL is `http://localhost:5200` when running with `jim-stack`.

## Test Environment Setup

```powershell
# Import the module from the local development path
Import-Module ./src/JIM.PowerShell/JIM -Force

# Verify the module loaded
Get-Module JIM

# Expected output:
# ModuleType Version    Name
# ---------- -------    ----
# Script     0.2.0      JIM
```

## Test Cases

### Test 1: Basic Interactive Authentication

**Steps:**
1. Ensure you're disconnected from any previous session:
   ```powershell
   Disconnect-JIM
   ```

2. Initiate interactive authentication:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```

3. **Expected behaviour:**
   - Console displays: `Opening browser for authentication...`
   - Console displays: `Waiting for authentication (timeout: 300 seconds)...`
   - Default browser opens to your identity provider's login page
   - After successful login, browser shows a success message
   - Console displays: `Successfully connected to JIM at http://localhost:5200`

4. Verify the connection:
   ```powershell
   Test-JIMConnection
   ```

5. **Expected output:**
   ```
   Connected      : True
   Url            : http://localhost:5200
   AuthMethod     : OAuth
   Status         : Healthy
   Message        : Connection successful
   TokenExpiresAt : <future date/time>
   ```

**Pass criteria:** Browser opens, authentication completes, `AuthMethod` shows `OAuth`

---

### Test 2: Quiet Mode Connection Test

**Steps:**
1. After connecting (Test 1), test quiet mode:
   ```powershell
   Test-JIMConnection -Quiet
   ```

2. **Expected output:** `True` (just a boolean, no object)

**Pass criteria:** Returns `True` with no additional output

---

### Test 3: API Call After Authentication

**Steps:**
1. After connecting (Test 1), make an API call:
   ```powershell
   Get-JIMConnectedSystem
   ```

2. **Expected behaviour:**
   - Returns list of connected systems (may be empty if none configured)
   - No authentication errors

3. Try another cmdlet:
   ```powershell
   Get-JIMMetaverseObjectType
   ```

4. **Expected behaviour:**
   - Returns list of MVO types (Person, Group, etc.)
   - No authentication errors

**Pass criteria:** API calls succeed without re-authentication prompts

---

### Test 4: Disconnect and Reconnect

**Steps:**
1. Disconnect:
   ```powershell
   Disconnect-JIM
   ```

2. Verify disconnected:
   ```powershell
   Test-JIMConnection
   ```

3. **Expected output:**
   ```
   Connected      : False
   Url            :
   AuthMethod     :
   Status         :
   Message        : Not connected. Use Connect-JIM to establish a connection.
   TokenExpiresAt :
   ```

4. Reconnect:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```

5. **Expected behaviour:** Browser opens again for re-authentication

**Pass criteria:** Disconnect clears session, reconnect requires new browser auth

---

### Test 5: Force Re-authentication

**Steps:**
1. Connect if not already connected:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```

2. Force re-authentication (without disconnecting first):
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -Force
   ```

3. **Expected behaviour:**
   - Browser opens for authentication even though already connected
   - New token obtained

**Pass criteria:** `-Force` triggers new browser authentication flow

---

### Test 6: Custom Timeout

**Steps:**
1. Disconnect first:
   ```powershell
   Disconnect-JIM
   ```

2. Connect with short timeout:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -TimeoutSeconds 30
   ```

3. **Expected behaviour:**
   - Console shows: `Waiting for authentication (timeout: 30 seconds)...`
   - If you don't complete auth within 30 seconds, it should timeout

4. To test timeout, start the command and wait without completing browser auth:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -TimeoutSeconds 10
   # Don't complete the browser authentication
   ```

5. **Expected behaviour after timeout:**
   - Error message about authentication timeout
   - Connection not established

**Pass criteria:** Timeout respected, error displayed when exceeded

---

### Test 7: Invalid URL Handling

**Steps:**
1. Try connecting with invalid URL:
   ```powershell
   Connect-JIM -Url "not-a-url"
   ```

2. **Expected behaviour:**
   - Error: `Invalid URL format...`
   - No browser opened

3. Try with unreachable URL:
   ```powershell
   Connect-JIM -Url "https://invalid.example.com"
   ```

4. **Expected behaviour:**
   - Error about failing to fetch OIDC discovery document
   - No browser opened (or browser shows error)

**Pass criteria:** Invalid URLs rejected with helpful error messages

---

### Test 8: Browser Cancellation

**Steps:**
1. Disconnect:
   ```powershell
   Disconnect-JIM
   ```

2. Start authentication:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```

3. When browser opens, close the browser tab/window without completing authentication

4. **Expected behaviour:**
   - After a moment, PowerShell shows error about authentication failure or timeout
   - Connection not established

**Pass criteria:** Graceful handling of user cancellation

---

### Test 9: API Key Authentication (Comparison)

**Steps:**
1. Create an API key in JIM UI (Settings > API Keys)

2. Disconnect OAuth session:
   ```powershell
   Disconnect-JIM
   ```

3. Connect with API key:
   ```powershell
   Connect-JIM -Url "http://localhost:5200" -ApiKey "your-api-key"
   ```

4. **Expected behaviour:**
   - No browser opened
   - Immediate connection

5. Verify:
   ```powershell
   Test-JIMConnection
   ```

6. **Expected output:**
   ```
   Connected      : True
   Url            : http://localhost:5200
   AuthMethod     : ApiKey
   Status         : Healthy
   Message        : Connection successful
   TokenExpiresAt :
   ```

**Pass criteria:** `AuthMethod` shows `ApiKey`, no `TokenExpiresAt` (API keys don't expire in the same way)

---

### Test 10: Token Refresh (Advanced)

**Steps:**
1. Connect with OAuth:
   ```powershell
   Connect-JIM -Url "http://localhost:5200"
   ```

2. Note the `TokenExpiresAt` value:
   ```powershell
   $conn = Test-JIMConnection
   $conn.TokenExpiresAt
   ```

3. Wait for token to approach expiry (or configure short token lifetime in IDP for testing)

4. Make an API call:
   ```powershell
   Get-JIMConnectedSystem
   ```

5. **Expected behaviour:**
   - If token expired but refresh token valid, silent refresh occurs
   - API call succeeds
   - New `TokenExpiresAt` if refreshed

6. Check new expiry:
   ```powershell
   (Test-JIMConnection).TokenExpiresAt
   ```

**Pass criteria:** Token refresh happens automatically without requiring re-authentication

---

## Troubleshooting

### Browser doesn't open
- Check if a default browser is configured
- Try running PowerShell as a regular user (not elevated)
- Check firewall isn't blocking localhost ports

### Authentication fails after browser login
- Verify loopback redirect URI is configured in IDP:
  - **Entra ID**: `http://localhost:8400/callback/` (exact match required)
  - **AD FS**: `http://localhost:8400/callback/` (exact match recommended)
  - **Keycloak**: `http://localhost:8400/callback/` (exact match recommended)
- If port 8400 is busy, add additional URIs for ports 8401-8409
- Check IDP allows public client flows (Entra ID) or is a public client (Keycloak)
- Look for errors in browser developer console

### "Discovery document" errors
- Verify `SSO_AUTHORITY` is correct and accessible
- Test: `curl https://login.microsoftonline.com/{tenant-id}/v2.0/.well-known/openid-configuration`

### Token refresh fails
- Ensure `offline_access` scope is requested and allowed
- Check refresh token lifetime in IDP settings

---

## Test Results Template

| Test | Description | Pass/Fail | Notes |
|------|-------------|-----------|-------|
| 1 | Basic Interactive Auth | | |
| 2 | Quiet Mode | | |
| 3 | API Call After Auth | | |
| 4 | Disconnect/Reconnect | | |
| 5 | Force Re-auth | | |
| 6 | Custom Timeout | | |
| 7 | Invalid URL | | |
| 8 | Browser Cancellation | | |
| 9 | API Key Comparison | | |
| 10 | Token Refresh | | |

**Tested by:** _______________
**Date:** _______________
**JIM Version:** 0.2.0
**IDP:** _______________
