---
title: Connection
description: "The cmdlets that connect the module to a JIM instance, by single sign-on or API key, check the session, and disconnect."
type: reference
audience: automation-author
---

# Connection

The connection cmdlets manage authentication and session state with a JIM instance. You must establish a connection with [Connect-JIM](#connect-jim) before using any other JIM cmdlets. Use [Test-JIMConnection](#test-jimconnection) to verify your session is active, and [Disconnect-JIM](#disconnect-jim) to clean up when finished.

!!! info "Running a cmdlet before connecting"
    If you run any JIM cmdlet before `Connect-JIM`, it produces no output and reports a clear, non-terminating error telling you to connect first, for example:

    ```text
    Get-JIMConnectedSystem: You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again.
    ```

    Because the error is non-terminating, a script continues by default. Add `-ErrorAction Stop` to the cmdlet (or set `$ErrorActionPreference = 'Stop'`) when you want the not-connected state to halt the script or be caught by `try/catch`.

---

## Connect-JIM

Establishes a connection to a JIM instance. This must be called before using any other JIM cmdlets.

<!-- BEGIN GENERATED: Connect-JIM -->
### Syntax

```powershell
# Interactive (default)
Connect-JIM [-Url] <string> [-Force] [-NoPersist] [-TimeoutSeconds <int>]

# ApiKey
Connect-JIM [-Url] <string> [-ApiKey] <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | Yes |  | The JIM instance's base URL, for example https://jim.example.com or http://localhost:5200. Can be given by position, as the first argument. |
| `ApiKey` | `string` | Yes (ApiKey) |  | An API key, for non-interactive authentication; create one in the portal under Admin > API Keys. With a key, `Connect-JIM` does not open a browser, and never reads or writes the credential store. `Connect-JIM` checks that JIM accepts the key, and stops if it does not: a key JIM has no record of, or one that is disabled or has expired. An infrastructure key, set in JIM_INFRASTRUCTURE_API_KEY, exists only once JIM has started with it set, and expires 24 hours later. Can be given by position, as the second argument. |
| `Force` | `switch` | No (Interactive) |  | Signs in again even when a valid session exists, ignoring any persisted refresh token and overwriting it with the new one. |
| `NoPersist` | `switch` | No (Interactive) |  | Signs in for this session only, without reading from or writing to the operating system's credential store. Useful on shared machines. |
| `TimeoutSeconds` | `int` | No (Interactive) | `300` | How long to wait, in seconds, for the browser sign-in to complete. From 30 to 600. |

### Output

The connection made.

| Property | Type | Description |
|----------|------|-------------|
| `Url` | `string` | The JIM instance's address. |
| `AuthMethod` | `string` | How the connection authenticated: OAuth or ApiKey. |
| `Connected` | `bool` | Always `$true`; a connection that fails throws instead. |
| `ServerVersion` | `string` | The JIM version the instance reported. |
| `Authorised` | `bool` | Whether the signed-in user has a JIM identity (a Metaverse Object) and so can use JIM; always `$true` for an API key. |
| `Status` | `string` | JIM's health status. |
| `ExpiresAt` | `datetime` | When the access token expires; OAuth only. |
| `ApiKey` | `string` | The key's first eight and last four characters; API key only. |
<!-- END GENERATED: Connect-JIM -->

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

- **Interactive SSO** requires that SSO is configured on the JIM server, a browser is available on the local machine, and the identity provider permits localhost redirect URIs on the client it issues tokens to. The PowerShell module is a public OAuth client that uses PKCE with loopback redirects; on Keycloak, and any IdP where the web and interactive clients must be separate registrations, the JIM administrator must create a second public client and set `JIM_SSO_PUBLIC_CLIENT_ID` on the server; see [SSO Setup](../administration/sso-setup.md) for details.
- **API key authentication** is recommended for automation, CI/CD pipelines, and headless environments where a browser is not available. `Connect-JIM` checks that JIM accepts the key, and stops with `JIM rejected the API key` when JIM has no record of it, or it is disabled or has expired; nothing is changed, so connect again with a valid key. An [infrastructure key](../administration/configuration.md#infrastructure-api-key) exists only once JIM has started with `JIM_INFRASTRUCTURE_API_KEY` set, and expires 24 hours later.
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

<!-- BEGIN GENERATED: Disconnect-JIM -->
### Syntax

```powershell
# Default
Disconnect-JIM [-Url <string>]

# All
Disconnect-JIM -All
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No (Default) |  | The JIM instance to disconnect from and forget; the connected one when omitted. Works when not connected to it, since clearing the credential store does not need a session. |
| `All` | `switch` | Yes (All) |  | Removes every persisted JIM refresh token from this machine, across all instances, and clears any session. |

### Output

None. Writes a message to the host saying what was removed.
<!-- END GENERATED: Disconnect-JIM -->

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
- Removal is **auth-agnostic**: a stored refresh token for the instance is removed even if the cleared session authenticated with an API key. API keys themselves are never persisted by the module, so for an API-key-only session there is nothing in the store to remove.
- `-Url` and `-All` work even when there is no active connection, since clearing the credential store is a maintenance operation independent of session state. A bare `Disconnect-JIM` while not connected has no instance to target and does nothing.
- Does **not** sign you out of your identity provider. If you authenticated via SSO, your identity provider session remains active.
- After disconnecting, you must call [Connect-JIM](#connect-jim) again before using other JIM cmdlets.

---

## Test-JIMConnection

Tests whether the current connection to a JIM instance is active and healthy.

<!-- BEGIN GENERATED: Test-JIMConnection -->
### Syntax

```powershell
Test-JIMConnection [-Quiet]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Quiet` | `switch` | No |  | Returns only `$true` or `$false` instead of the connection's status. |

### Output

The connection's status; with `-Quiet`, `$true` if connected and otherwise `$false`.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Connected` | `bool` | Whether a connection exists and JIM answered. |
| `Url` | `string` | The JIM instance's address. |
| `AuthMethod` | `string` | How the connection authenticated: ApiKey or OAuth. |
| `ServerVersion` | `string` | The JIM version the instance reported. |
| `Status` | `string` | JIM's health status. |
| `Message` | `string` | A sentence describing the result. |
| `TokenExpiresAt` | `datetime` | When the access token expires; OAuth only. |

**With `-Quiet`**: a `bool`.
<!-- END GENERATED: Test-JIMConnection -->

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
