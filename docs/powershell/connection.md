---
title: Connection
---

# Connection

The connection cmdlets manage authentication and session state with a JIM instance. You must establish a connection with [Connect-JIM](#connect-jim) before using any other JIM cmdlets. Use [Test-JIMConnection](#test-jimconnection) to verify your session is active, and [Disconnect-JIM](#disconnect-jim) to clean up when finished.

---

## Connect-JIM

Establishes a connection to a JIM instance. This must be called before using any other JIM cmdlets.

### Syntax

```powershell
# Interactive SSO (default)
Connect-JIM -Url <string> [-Force] [-TimeoutSeconds <int>]

# API Key
Connect-JIM -Url <string> -ApiKey <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | Yes (Position 0) | | Base URL of the JIM instance, e.g. `https://jim.example.com` |
| `ApiKey` | `string` | Yes for ApiKey set (Position 1) | | API key for non-interactive authentication |
| `Force` | `switch` | No (Interactive only) | `$false` | Forces re-authentication even if a valid session already exists |
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

```powershell title="API key authentication for automation"
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_7f3e..."
```

```powershell title="Using positional parameters"
Connect-JIM "https://jim.example.com" "jim_ak_7f3e..."
```

### Notes

- **Interactive SSO** requires that SSO is configured on the JIM server, a browser is available on the local machine, and the identity provider permits localhost redirect URIs.
- **API key authentication** is recommended for automation, CI/CD pipelines, and headless environments where a browser is not available.
- OAuth sessions are cached locally; tokens are refreshed automatically when they approach expiry.
- If a valid session already exists, `Connect-JIM` reuses it unless `-Force` is specified.
- To disconnect, use [Disconnect-JIM](#disconnect-jim). To verify your session, use [Test-JIMConnection](#test-jimconnection).

---

## Disconnect-JIM

Disconnects from the current JIM instance and clears session state.

### Syntax

```powershell
Disconnect-JIM
```

### Parameters

None.

### Output

None. Writes informational messages to the host confirming disconnection.

### Examples

```powershell title="Disconnect from JIM"
Disconnect-JIM
```

```powershell title="Connect, perform work, then disconnect"
Connect-JIM -Url "https://jim.example.com"
# ... perform administrative tasks ...
Disconnect-JIM
```

### Notes

- Clears access tokens and refresh tokens from memory.
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
