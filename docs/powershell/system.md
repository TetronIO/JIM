---
title: System
---

# System

System cmdlets provide access to JIM health checks, version information, authentication configuration, and current user details. Health, version, and auth config cmdlets work without authentication, making them suitable for monitoring, scripting, and client bootstrapping. User info requires an active connection.

!!! tip
    Health, version, and auth config cmdlets accept a `-Url` parameter for standalone use without `Connect-JIM`. When omitted, they fall back to the URL from the active session.

---

## Get-JIMHealth

Retrieves the health, readiness, or liveness status of a JIM instance. Does not require authentication.

### Syntax

```powershell
# Basic health (default)
Get-JIMHealth [-Url <string>]

# Readiness probe
Get-JIMHealth [-Url <string>] -Ready

# Liveness probe
Get-JIMHealth [-Url <string>] -Live
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No (Position 0) | Session URL | Base URL of the JIM instance |
| `Ready` | `switch` | Yes (Ready set) | | Check the readiness probe instead of basic health |
| `Live` | `switch` | Yes (Live set) | | Check the liveness probe instead of basic health |

### Output

| Property | Type | Description |
|----------|------|-------------|
| `status` | `string` | Health status: `healthy`, `ready`/`not_ready`, or `alive` |
| `timestamp` | `string` | UTC timestamp of the check |

### Examples

```powershell title="Basic health check (no connection required)"
Get-JIMHealth -Url "https://jim.example.com"
```

```powershell title="Readiness probe for Kubernetes"
Get-JIMHealth -Url "https://jim.example.com" -Ready
```

```powershell title="Liveness probe"
Get-JIMHealth -Url "https://jim.example.com" -Live
```

```powershell title="With an active connection (uses connected URL)"
Get-JIMHealth
Get-JIMHealth -Ready
```

### Notes

- Use `-Ready` as a Kubernetes readiness probe or load balancer health check; it verifies database connectivity and maintenance mode status.
- Use `-Live` as a Kubernetes liveness probe; it confirms the process is running.
- The basic health check (no switches) returns the general application health status.

---

## Get-JIMVersion

Retrieves the JIM application version. Does not require authentication.

### Syntax

```powershell
Get-JIMVersion [-Url <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No (Position 0) | Session URL | Base URL of the JIM instance |

### Output

| Property | Type | Description |
|----------|------|-------------|
| `product` | `string` | Product name (always `JIM`) |
| `version` | `string` | Semantic version number |

### Examples

```powershell title="Check version (no connection required)"
Get-JIMVersion -Url "https://jim.example.com"
```

```powershell title="With an active connection"
Get-JIMVersion
```

```powershell title="Use version in a script"
$v = Get-JIMVersion -Url "https://jim.example.com"
Write-Host "JIM version: $($v.version)"
```

---

## Get-JIMAuthConfig

Retrieves the OIDC/OAuth client discovery configuration. Does not require authentication. Useful for scripting SSO setup or validating configuration.

### Syntax

```powershell
Get-JIMAuthConfig [-Url <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No (Position 0) | Session URL | Base URL of the JIM instance |

### Output

| Property | Type | Description |
|----------|------|-------------|
| `authority` | `string` | OIDC authority URL |
| `clientId` | `string` | OAuth client ID |
| `scopes` | `array` | OAuth scopes to request |
| `responseType` | `string` | OAuth response type (always `code`) |
| `usePkce` | `boolean` | Whether PKCE is required (always `true`) |
| `codeChallengeMethod` | `string` | PKCE challenge method (always `S256`) |

### Examples

```powershell title="Check auth config (no connection required)"
Get-JIMAuthConfig -Url "https://jim.example.com"
```

```powershell title="Validate SSO configuration"
$config = Get-JIMAuthConfig -Url "https://jim.example.com"
Write-Host "Authority: $($config.authority)"
Write-Host "Client ID: $($config.clientId)"
Write-Host "Scopes: $($config.scopes -join ', ')"
```

---

## Get-JIMUserInfo

Retrieves the current authenticated user's details, roles, and authorisation status. Requires an active `Connect-JIM` session.

### Syntax

```powershell
Get-JIMUserInfo
```

### Parameters

None.

### Output

| Property | Type | Description |
|----------|------|-------------|
| `authorised` | `boolean` | Whether the user has a JIM identity and can access the system |
| `isAdministrator` | `boolean` | Whether the user has the Administrator role |
| `name` | `string` | Display name |
| `authMethod` | `string` | `oauth` or `api_key` |
| `metaverseObjectId` | `guid?` | The user's metaverse object ID (`$null` if not authorised) |
| `roles` | `array` | Role names assigned to the user |
| `message` | `string?` | Additional context (present when not authorised) |

### Examples

```powershell title="Get current user info"
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
Get-JIMUserInfo
```

```powershell title="Check administrator access"
$user = Get-JIMUserInfo
if ($user.isAdministrator) {
    Write-Host "Admin access confirmed for $($user.name)"
} else {
    Write-Warning "Not an administrator"
}
```

```powershell title="List assigned roles"
(Get-JIMUserInfo).roles
```

### Notes

- Requires an active connection via [Connect-JIM](connection.md#connect-jim).
- This endpoint does not require the Administrator role; any authenticated user or API key can call it.
- If the user is authenticated but not authorised (no JIM identity), `authorised` is `$false` and a `message` explains why.

---

## See also

- [API reference](../api/index.md): the interactive API reference covers the system endpoints (health, readiness, liveness, version, auth config, user info)
- [Connection](connection.md): establishing and managing connections to JIM
