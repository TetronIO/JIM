---
title: System
---

# System

System cmdlets provide access to JIM health checks, service health, version information, authentication configuration, and current user details. Health, version, and auth config cmdlets work without authentication, making them suitable for monitoring, scripting, and client bootstrapping. Service health and user info require an active connection.

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

## Get-JIMServiceHealth

Reports whether JIM's background services (the Worker and the Scheduler) are alive and what each is doing, from the heartbeat every service writes to the database every 5 seconds. This is the same report the Service Health strip on [Operations](../configuration/operations.md#service-health) shows. Requires an active `Connect-JIM` session with the **Administrator** role.

`Get-JIMHealth` answers a different question: it probes the web tier only, without authentication, and says nothing about the Worker or the Scheduler.

### Syntax

```powershell
# One object per service (default)
Get-JIMServiceHealth

# One summary object with the worst state present
Get-JIMServiceHealth -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Summary` | `switch` | Yes (Summary set) | | Return one object carrying `Overall`, `WebVersion`, `GeneratedAt` and `Services`, instead of one object per service |

### Output

By default, one `JIM.ServiceHealth` object per service, always three and always in this order: `WorkerSync`, `WorkerPasswordDelivery`, `Scheduler`. A service that has never reported is present as `NotSeen` rather than missing, with the fields it cannot supply set to `$null`.

| Property | Type | Description |
|----------|------|-------------|
| `Service` | `string` | `WorkerSync` (the Worker's synchronisation loop), `WorkerPasswordDelivery` (the Worker's password delivery loop) or `Scheduler` |
| `State` | `string` | `Running`, `Stale`, `NoProgress` or `NotSeen`; see [What the states mean](../configuration/operations.md#what-the-states-mean) |
| `Reason` | `string` | One sentence explaining the state, e.g. `Last seen 4 minutes ago; expected within 60 seconds` or `Never reported` |
| `CurrentWork` | `string?` | What the service was doing when it last reported, e.g. `Full Import: Corporate Directory`; `$null` when idle |
| `CurrentWorkStartedAt` | `datetime?` | When the current work began (UTC) |
| `LastSeenAt` | `datetime?` | When the service last reported (UTC) |
| `StartedAt` | `datetime?` | When the reporting instance started (UTC) |
| `HostName` | `string?` | The host the reporting instance runs on |
| `Version` | `string?` | The JIM version the instance runs; compare with the summary's `WebVersion` |
| `InstanceId` | `string?` | The reporting instance (host name plus a per-process id) |
| `LastProgressAt` | `datetime?` | When the current work last moved forward (UTC); `$null` when the service cannot tell progress apart from liveness |
| `Detail` | `string?` | Free text the service left beside its state, such as queue counts or why it is waiting |

With `-Summary`, one `JIM.ServiceHealthSummary` object:

| Property | Type | Description |
|----------|------|-------------|
| `Overall` | `string` | The worst state among the services: `Running`, `Stale`, `NoProgress` or `NotSeen` |
| `WebVersion` | `string` | The version of the web tier that answered |
| `GeneratedAt` | `datetime` | When the verdicts were derived (UTC); every `Reason` is relative to it |
| `Services` | `JIM.ServiceHealth[]` | The per-service objects described above |

### Examples

```powershell title="Is the Worker running, and what is it doing?"
Get-JIMServiceHealth
```

```powershell title="The columns that matter during a change window"
Get-JIMServiceHealth | Format-Table Service, State, CurrentWork, LastSeenAt, Version
```

```powershell title="Fail a monitoring check when any service is unhealthy"
$health = Get-JIMServiceHealth -Summary
if ($health.Overall -ne 'Running') {
    $health.Services | Where-Object State -ne 'Running' | Format-List Service, State, Reason
    exit 1
}
```

```powershell title="Name the work that has stalled"
Get-JIMServiceHealth | Where-Object State -eq 'NoProgress' | Select-Object Service, CurrentWork, LastProgressAt
```

```powershell title="Find services running a different version from the web tier"
$health = Get-JIMServiceHealth -Summary
$health.Services | Where-Object { $_.Version -and $_.Version -ne $health.WebVersion } |
    Select-Object Service, HostName, Version, @{ Name = 'WebVersion'; Expression = { $health.WebVersion } }
```

### Notes

- Requires an active connection via [Connect-JIM](connection.md#connect-jim) and the **Administrator** role.
- `Running` means a heartbeat within the last 15 seconds; `Stale` more than 15 seconds; `NotSeen` more than 60 seconds for the Worker services and 120 seconds for the Scheduler, or never reported; `NoProgress` means the current work has not moved forward for 10 minutes. `Overall` is the worst state present, so a monitoring script needs to read nothing else.
- `Stale` is worth a glance, not an alarm: a slow database or a paused process produces it. `NotSeen` and `NoProgress` are what the portal raises its administrator banner for.
- Calls `GET /api/v1/system/health`, which is marked `Cache-Control: no-store`; every call is a fresh verdict.

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
- If the user is authenticated but not authorised (no JIM identity), `Authorised` is `$false` and a `Message` explains why.

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
- **Preserved:** the database schema and its upgrade history, all built-in Metaverse Attributes, Object Types, Roles, Connector Definitions, Example Data Sets, and Predefined Searches, the singleton Service Settings record, infrastructure API keys (`IsInfrastructureKey = true`), and (unless `-IncludeAdministrators` is supplied) the Metaverse Objects holding the Administrator role. The Service Settings record survives, but a setting naming something the reset removes does not: if the SSO Unique Identifier maps to a custom Metaverse Attribute, that mapping is cleared along with the attribute, and must be set again against a Metaverse Attribute that still exists.
- **Restored:** immediately after the wipe, the reset applies JIM's built-in configuration exactly as a service start does, so anything the wipe removed as collateral (the built-in Schedules, the built-in Example Data Template's attributes) is back before the command returns, rather than on the next restart. Built-ins that survived the wipe are left untouched, but have their factory-state provenance re-recorded, because the wipe removes the Activities that carried it. Read-only Service Settings that come from the deployment's environment variables (the SSO endpoints, the encryption key path) are re-asserted from the environment as part of this.
- A **Reset activity** recording who initiated the wipe is always created, and **every signed-in portal session is invalidated**; users (including administrators) must sign in again. API keys are unaffected.
- With `-IncludeAdministrators` and no initial administrator configured (`JIM_SSO_INITIAL_ADMIN`), the reset is refused (HTTP 409) unless `-AcknowledgeAdministratorLockout` is also supplied, because the portal would otherwise be inaccessible afterwards.
- The reset is refused with a non-terminating error (HTTP 409) when any Activity is currently in progress; wait for activities to finish or cancel them before retrying.
- Files stored under the connector files mount (typically `/connector-files`) are **not** wiped; remove them out-of-band if a clean filesystem is also required.

---

## See also

- [Interactive API reference](../api/reference/): covers the system endpoints (health, readiness, liveness, version, auth config, user info, service health, factory reset)
- [Operations](../configuration/operations.md): the Service Health strip these cmdlets read from, and what each state means
- [Connection](connection.md): establishing and managing connections to JIM
