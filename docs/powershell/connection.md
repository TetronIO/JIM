---
title: Connection
---

# Connection

The connection cmdlets manage authentication and session state with a JIM instance. You must establish a connection with [Connect-JIM](#connect-jim) before using any other JIM cmdlets. Use [Test-JIMConnection](#test-jimconnection) to verify your session is active, and [Disconnect-JIM](#disconnect-jim) to clean up when finished.

!!! info "Running a cmdlet before connecting"
    If you run any JIM cmdlet before `Connect-JIM`, it produces no output and reports a clear, non-terminating error telling you to connect first, for example:

    ```
    Get-JIMConnectedSystem: You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again.
    ```

    Because the error is non-terminating, a script continues by default. Add `-ErrorAction Stop` to the cmdlet (or set `$ErrorActionPreference = 'Stop'`) when you want the not-connected state to halt the script or be caught by `try/catch`.

---

## Connect-JIM

Establishes a connection to a JIM instance. This must be called before using any other JIM cmdlets.

### Syntax

```powershell
# Interactive SSO (default)
Connect-JIM -Url <string> [-Force] [-NoPersist] [-TimeoutSeconds <int>]

# API Key
Connect-JIM -Url <string> -ApiKey <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | Yes (Position 0) | | Base URL of the JIM instance, e.g. `https://jim.example.com` |
| `ApiKey` | `string` | Yes for ApiKey set (Position 1) | | API key for non-interactive authentication |
| `Force` | `switch` | No (Interactive only) | `$false` | Forces re-authentication even if a valid session already exists. Ignores and overwrites any persisted refresh token |
| `NoPersist` | `switch` | No (Interactive only) | `$false` | Authenticates for this session only, without reading from or writing to the OS credential store |
| `TimeoutSeconds` | `int` | No (Interactive only) | `300` | How long (in seconds) to wait for interactive authentication to complete. Valid range: 30 to 600. |

### Output

Returns a `PSCustomObject` with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `Url` | `string` | The JIM instance URL |
| `AuthMethod` | `string` | Authentication method used (`OAuth` or `ApiKey`) |
| `Connected` | `bool` | Whether the connection was established successfully |
| `ServerVersion` | `string` | Version of the connected JIM server |
| `ExpiresAt` | `DateTime?` | Token expiry time (OAuth only; `$null` for API key connections) |
| `Authorised` | `bool` | Whether the authenticated identity has administrative access |
| `Status` | `string` | Human-readable connection status message |
| `ApiKey` | `string?` | Redacted preview of the API key (ApiKey method only; `$null` for OAuth connections) |

### Examples

```powershell title="Interactive SSO authentication"
Connect-JIM -Url "https://jim.example.com"
```

```powershell title="Interactive SSO with forced re-authentication"
Connect-JIM -Url "https://jim.example.com" -Force
```

```powershell title="Interactive SSO with custom timeout"
Connect-JIM -Url "https://jim.example.com" -TimeoutSeconds 60
```

```powershell title="Interactive SSO without persisting the token (shared machine)"
Connect-JIM -Url "https://jim.example.com" -NoPersist
```

```powershell title="API key authentication for automation"
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_7f3e..."
```

```powershell title="Using positional parameters"
Connect-JIM "https://jim.example.com" "jim_ak_7f3e..."
```

### Notes

- **Interactive SSO** requires that SSO is configured on the JIM server, a browser is available on the local machine, and the identity provider permits localhost redirect URIs on the client it issues tokens to. The PowerShell module is a public OAuth client that uses PKCE with loopback redirects; on Keycloak, and any IdP where the web and interactive clients must be separate registrations, the JIM administrator must create a second public client and set `JIM_SSO_PUBLIC_CLIENT_ID` on the server — see [SSO Setup](../administration/sso-setup.md) for details.
- **API key authentication** is recommended for automation, CI/CD pipelines, and headless environments where a browser is not available.
- OAuth sessions are cached locally; tokens are refreshed automatically when they approach expiry.
- If a valid session already exists, `Connect-JIM` reuses it unless `-Force` is specified.
- **Token persistence (interactive SSO):** after an interactive sign-in, the OAuth refresh token is saved to the operating system's credential store so that a new terminal can reconnect silently without opening a browser. The next `Connect-JIM` to the same URL prints `Connected to JIM using cached credentials` and skips the browser. Only the refresh token is stored, never the access token (a fresh access token is obtained from it on demand). The store used per platform is:

    | Platform | Credential store |
    |----------|------------------|
    | Windows | Credential Manager (DPAPI, per-user) |
    | macOS | login Keychain |
    | Linux | libsecret (`secret-tool`), when present |

    No password is required to unlock these stores beyond your normal OS sign-in. On systems with no usable store (typically headless Linux or SSH sessions without a keyring), persistence is skipped and the module prints a notice; use `-ApiKey` for unattended scenarios. Use `-NoPersist` to opt out of persistence for a session, and `-Force` to ignore and overwrite a persisted token. Persistence requires the identity provider to issue a refresh token, which depends on the `offline_access` scope being permitted on the public client (see [SSO Setup](../administration/sso-setup.md)).
- To disconnect, use [Disconnect-JIM](#disconnect-jim). To verify your session, use [Test-JIMConnection](#test-jimconnection).

---

## Disconnect-JIM

Disconnects from a JIM instance and forgets its persisted credentials.

### Syntax

```powershell
Disconnect-JIM [-Url <string>]
Disconnect-JIM -All
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No | currently connected instance | The instance to disconnect and forget. Works even when not connected to it |
| `All` | `switch` | No | `$false` | Remove every persisted JIM refresh token from this machine, across all instances |

### Output

None. Writes informational messages to the host confirming disconnection.

### Examples

```powershell title="Disconnect and forget the connected instance"
Disconnect-JIM
```

```powershell title="Forget a specific instance's persisted token (even if not connected)"
Disconnect-JIM -Url "https://jim.example.com"
```

```powershell title="Remove every persisted JIM token from this machine"
Disconnect-JIM -All
```

### Notes

- Clears the in-memory session (access and refresh tokens) **and** removes the persisted refresh token for the targeted instance from the OS credential store, so a later `Connect-JIM` must authenticate again. By default the targeted instance is the connected one; use `-Url` for a specific instance or `-All` for every instance.
- Removal is **auth-agnostic**: a stored refresh token for the instance is removed even if the cleared session authenticated with an API key. API keys themselves are never persisted by the module, so for an API-key-only session there is simply nothing in the store to remove.
- `-Url` and `-All` work even when there is no active connection, since clearing the credential store is a maintenance operation independent of session state. A bare `Disconnect-JIM` while not connected has no instance to target and does nothing.
- Does **not** sign you out of your identity provider. If you authenticated via SSO, your identity provider session remains active.
- After disconnecting, you must call [Connect-JIM](#connect-jim) again before using other JIM cmdlets.

---

## Test-JIMConnection

Tests whether the current connection to a JIM instance is active and healthy.

### Syntax

```powershell
Test-JIMConnection [-Quiet]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Quiet` | `switch` | No | `$false` | Returns only `$true` or `$false` instead of the full status object |

### Output

**Default mode** returns a `PSCustomObject` with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `Connected` | `bool` | Whether the connection is currently active |
| `Url` | `string` | The JIM instance URL |
| `AuthMethod` | `string` | Authentication method in use (`OAuth` or `ApiKey`) |
| `ServerVersion` | `string` | Version of the connected JIM server |
| `Status` | `string` | Human-readable status message |
| `Message` | `string` | Additional detail about the connection state |
| `TokenExpiresAt` | `DateTime?` | Token expiry time (OAuth only; `$null` for API key connections) |

**Quiet mode** (`-Quiet`) returns a `bool`: `$true` if connected and healthy, `$false` otherwise.

### Examples

```powershell title="Check connection status"
Test-JIMConnection
```

```powershell title="Use in a script conditional"
if (Test-JIMConnection -Quiet) {
    Write-Host "Connected to JIM"
} else {
    Connect-JIM -Url "https://jim.example.com"
}
```

```powershell title="Inspect token expiry"
$status = Test-JIMConnection
if ($status.TokenExpiresAt -and $status.TokenExpiresAt -lt (Get-Date).AddMinutes(5)) {
    Write-Warning "Token expires soon; consider re-authenticating."
}
```

### Notes

- Checks the JIM server health endpoint to confirm reachability.
- For OAuth sessions, reports token expiry status so you can detect sessions that are about to expire.
- Use `-Quiet` in scripts where you only need a pass/fail check without the full status object.
- If no connection has been established, the default output shows `Connected = $false` with an explanatory message.

---

## See also

- [API Authentication](../api/authentication.md): configuring API keys and SSO for the JIM server
