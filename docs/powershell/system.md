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
| `Status` | `string` | Health status: `healthy`, `ready`/`not_ready`, or `alive` |
| `Timestamp` | `string` | UTC timestamp of the check |

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
| `Product` | `string` | Product name (always `JIM`) |
| `Version` | `string` | Semantic version number |

### Examples

```powershell title="Check version (no connection required)"
Get-JIMVersion -Url "https://jim.example.com"
```

```powershell title="With an active connection"
Get-JIMVersion
```

```powershell title="Use version in a script"
$v = Get-JIMVersion -Url "https://jim.example.com"
Write-Host "JIM version: $($v.Version)"
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
| `Authority` | `string` | OIDC authority URL |
| `ClientId` | `string` | OAuth client ID |
| `Scopes` | `array` | OAuth scopes to request |
| `ResponseType` | `string` | OAuth response type (always `code`) |
| `UsePkce` | `boolean` | Whether PKCE is required (always `true`) |
| `CodeChallengeMethod` | `string` | PKCE challenge method (always `S256`) |

### Examples

```powershell title="Check auth config (no connection required)"
Get-JIMAuthConfig -Url "https://jim.example.com"
```

```powershell title="Validate SSO configuration"
$config = Get-JIMAuthConfig -Url "https://jim.example.com"
Write-Host "Authority: $($config.Authority)"
Write-Host "Client ID: $($config.ClientId)"
Write-Host "Scopes: $($config.Scopes -join ', ')"
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
| `Authorised` | `boolean` | Whether the user has a JIM identity and can access the system |
| `IsAdministrator` | `boolean` | Whether the user has the Administrator role |
| `Name` | `string` | Display name |
| `AuthMethod` | `string` | `oauth` or `api_key` |
| `MetaverseObjectId` | `guid?` | The user's Metaverse Object ID (`$null` if not authorised) |
| `Roles` | `array` | Role names assigned to the user |
| `Message` | `string?` | Additional context (present when not authorised) |

### Examples

```powershell title="Get current user info"
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
Get-JIMUserInfo
```

```powershell title="Check administrator access"
$user = Get-JIMUserInfo
if ($user.IsAdministrator) {
    Write-Host "Admin access confirmed for $($user.Name)"
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

## Reset-JIMSystem

Performs a factory reset against the connected JIM instance, wiping all data and configuration while preserving the schema, seeded built-ins, and infrastructure access. By default the administrator users are preserved so you are not locked out of the portal. This operation is destructive and cannot be undone; take a database backup first.

### Syntax

```powershell
Reset-JIMSystem [-Force] [-IncludeAdministrators] [-AcknowledgeAdministratorLockout] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `IncludeAdministrators` | `switch` | No | `$false` | Also removes the Metaverse Objects holding the built-in Administrator role, leaving a true brand-new install. By default these are preserved |
| `AcknowledgeAdministratorLockout` | `switch` | No | `$false` | Acknowledges the lockout risk so an administrator-inclusive wipe may proceed when no initial administrator is configured. Ignored unless `-IncludeAdministrators` is set |

### Output

Returns a `PSCustomObject` containing the counts of removed entities (for example `ConnectedSystemsRemoved`, `SyncRulesRemoved`, `MetaverseObjectsRemoved`).

**ShouldProcess impact level:** High. The cmdlet prompts for confirmation by default; pass `-Force` to suppress.

### Examples

```powershell title="Factory reset with confirmation"
Reset-JIMSystem
```

```powershell title="Factory reset without prompting"
Reset-JIMSystem -Force
```

```powershell title="Capture and report on what was removed"
$result = Reset-JIMSystem -Force
"Removed $($result.ConnectedSystemsRemoved) Connected Systems"
```

### Notes

- Requires an active connection via [Connect-JIM](connection.md#connect-jim) and the **Administrator** role.
- **Removed:** all Connected Systems (and their objects and change history), Metaverse Objects (and their change history), Synchronisation Rules, Object Matching Rules, Schedules (and their executions), Activities, Pending Exports, and all custom (`BuiltIn = false`) Metaverse Object Types, Attributes, Roles, Connector Definitions, Predefined Searches, Example Data Sets, and Example Data Templates, plus non-infrastructure API Keys and Trusted Certificates.
- **Preserved:** the database schema and EF Core migration history, all built-in Metaverse Attributes, Object Types, Roles, Connector Definitions, Example Data Sets, and Predefined Searches, the singleton Service Settings record, infrastructure API keys (`IsInfrastructureKey = true`), and (unless `-IncludeAdministrators` is supplied) the Metaverse Objects holding the Administrator role.
- A **Reset activity** recording who initiated the wipe is always created, and **every signed-in portal session is invalidated**; users (including administrators) must sign in again. API keys are unaffected.
- With `-IncludeAdministrators` and no initial administrator configured (`JIM_SSO_INITIAL_ADMIN`), the reset is refused (HTTP 409) unless `-AcknowledgeAdministratorLockout` is also supplied, because the portal would otherwise be inaccessible afterwards.
- The reset is refused with a non-terminating error (HTTP 409) when any Activity is currently in progress; wait for activities to finish or cancel them before retrying.
- Files stored under the connector files mount (typically `/connector-files`) are **not** wiped; remove them out-of-band if a clean filesystem is also required.

---

## See also

- [Interactive API reference](../api/reference/): covers the system endpoints (health, readiness, liveness, version, auth config, user info, factory reset)
- [Connection](connection.md): establishing and managing connections to JIM
