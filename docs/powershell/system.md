---
title: System
description: "The cmdlets that report JIM's health, version, Service Health, authentication configuration and the signed-in user, and reset JIM."
type: reference
audience: automation-author
---

# System

System cmdlets provide access to JIM health checks, Service Health, version information, authentication configuration, and current user details. Health, version, and auth config cmdlets work without authentication, making them suitable for monitoring, scripting, and client bootstrapping. Service Health and user info require an active connection.

```jim-diagram
archetype: decision
title: When Reset-JIMSystem goes ahead
desc: "Once confirmed, Reset-JIMSystem is refused with HTTP 409 while any Activity is in progress: wait for it to finish, or cancel it. Otherwise, without -IncludeAdministrators, the reset goes ahead and keeps the administrators. With -IncludeAdministrators, it goes ahead and removes them only when JIM_SSO_INITIAL_ADMIN is set or -AcknowledgeAdministratorLockout is given; otherwise it is refused with HTTP 409, because nobody could sign in to the portal afterwards. Either way a reset records a Reset Activity and signs everyone out of the portal."
start: { label: Factory reset confirmed }
questions:
  - { id: busy, label: "An Activity in progress?" }
  - { id: admins, label: "`-IncludeAdministrators`?" }
  - { id: guard, label: "`JIM_SSO_INITIAL_ADMIN` set?", sub: "or the lockout acknowledged" }
outcomes:
  - { id: busyno, label: "Refused: HTTP 409", sub: "wait for it to finish, or cancel it" }
  - { id: kept, label: "Reset, administrators kept", sub: "everyone signs in to the portal again", good: true }
  - { id: removed, label: "Reset, administrators removed", sub: "everyone signs in to the portal again", good: true }
  - { id: lockout, label: "Refused: HTTP 409", sub: "nobody could sign in to the portal" }
answers:
  - { from: busy, to: busyno, label: "yes" }
  - { from: busy, to: admins, label: "no" }
  - { from: admins, to: kept, label: "no" }
  - { from: admins, to: guard, label: "yes" }
  - { from: guard, to: removed, label: "yes" }
  - { from: guard, to: lockout, label: "no" }
```

!!! tip
    Health, version, and auth config cmdlets accept a `-Url` parameter for standalone use without `Connect-JIM`. When omitted, they fall back to the URL from the active session.

---

## :material-heart-pulse: Get-JIMHealth

Retrieves the health, readiness, or liveness status of a JIM instance. Does not require authentication.

<!-- BEGIN GENERATED: Get-JIMHealth -->
### Syntax

```powershell
# Health (default)
Get-JIMHealth [[-Url] <string>]

# Ready
Get-JIMHealth [[-Url] <string>] -Ready

# Live
Get-JIMHealth [[-Url] <string>] -Live
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No |  | The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted. Can be given by position, as the first argument. |
| `Ready` | `switch` | Yes (Ready) |  | Checks readiness instead: that the database is reachable and JIM is not in maintenance mode. |
| `Live` | `switch` | Yes (Live) |  | Checks liveness instead: that the process is running. |

### Output

One object with the probe's Status and Timestamp. A readiness check that fails is reported as an error rather than returned.

| Property | Type | Description |
|----------|------|-------------|
| `Status` | `string` | The probe's verdict: healthy (the web tier answered), ready (it can serve requests: the database is reachable and JIM is not in maintenance mode) or alive (the process is running). |
| `Timestamp` | `datetime` | When (UTC) the probe was answered. |
<!-- END GENERATED: Get-JIMHealth -->

### Examples

```powershell title="Basic health check (no connection required)"
Get-JIMHealth -Url "https://jim.example.com"
```

```powershell title="Readiness probe"
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

- Use `-Ready` as a load balancer or monitoring readiness check; it verifies database connectivity and maintenance mode status.
- Use `-Live` as a liveness check; it confirms the process is running.
- The basic health check (no switches) returns the general application health status.

---

## :material-stethoscope: Get-JIMServiceHealth

Reports whether JIM's background services (the Worker and the Scheduler) are alive and what each is doing, from the heartbeat every service writes to the database every 5 seconds. This is the same report the Service Health strip on [Operations](../configuration/operations.md#service-health) shows. Requires an active `Connect-JIM` session with the **Administrator** role.

`Get-JIMHealth` answers a different question: it probes the web tier only, without authentication, and says nothing about the Worker or the Scheduler.

<!-- BEGIN GENERATED: Get-JIMServiceHealth -->
### Syntax

```powershell
# Services (default)
Get-JIMServiceHealth

# Summary
Get-JIMServiceHealth -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Summary` | `switch` | Yes (Summary) |  | Returns one object carrying Overall, WebVersion, GeneratedAt and Services, instead of one object per service. |

### Output

One object per service, always three and always in the order WorkerSync, WorkerDelivery, Scheduler. A service that has never reported is present as Unhealthy (NeverStarted), with the fields it cannot supply set to empty. With `-Summary`, one object whose Services holds those three.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Service` | `string` | Which service this is: WorkerSync (the Worker's synchronisation loop), WorkerDelivery (the Worker's password delivery loop) or Scheduler. One of `WorkerSync`, `WorkerDelivery` or `Scheduler`. |
| `Status` | `string` | The verdict: Healthy (heartbeating within its interval), Degraded (alive, but its heartbeat is overdue, its current work has stalled, or its memory is low) or Unhealthy (no heartbeat long enough to presume it down, or never started). One of `Healthy`, `Degraded` or `Unhealthy`. |
| `Condition` | `string` | Why the service has that status: Heartbeating, HeartbeatOverdue, Stalled, MemoryLow (85% or more of the memory it can draw on is in use), NoHeartbeat or NeverStarted. Each condition belongs to exactly one status. One of `Heartbeating`, `HeartbeatOverdue`, `Stalled`, `NoHeartbeat`, `NeverStarted` or `MemoryLow`. |
| `Reason` | `string` | The condition in plain words with the figures that matter, for example "No heartbeat for 4 minutes". |
| `InstanceId` | `string` | The reporting instance (host name plus a per-process id), or empty when the service has never reported. |
| `HostName` | `string` | The host the reporting instance runs on, or empty when the service has never reported. |
| `Version` | `string` | The JIM version the reporting instance runs, or empty when the service has never reported. |
| `StartedAt` | `datetime` | When (UTC) the reporting instance started, or empty when the service has never reported. |
| `LastSeenAt` | `datetime` | When (UTC) the service last reported, or empty when it never has. |
| `CurrentWork` | `string` | What the service said it was doing when it last reported, or empty when idle or never seen. |
| `CurrentWorkStartedAt` | `datetime` | When (UTC) the current work began, or empty when idle or never seen. |
| `LastProgressAt` | `datetime` | When (UTC) the current work last moved forward, or empty when idle, never seen, or when the service cannot tell progress apart from liveness. |
| `Detail` | `string` | Free text the service left beside its state (queue counts, why it is waiting), or empty. |
| `ProcessMemoryBytes` | `long` | The memory the service's process held at its last heartbeat, in bytes, or empty when it did not report it. |
| `MemoryTotalBytes` | `long` | The memory the service's process can draw on in all, in bytes: its container's memory limit when one is set, otherwise the host's memory. Empty when not reported. |
| `MemoryAvailableBytes` | `long` | How much of the total memory was still available at the last heartbeat, in bytes, counting file cache the system can reclaim. Empty when not reported. |

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `Overall` | `string` | The worst status among Services: Healthy, Degraded or Unhealthy. A monitoring script that alerts on anything other than Healthy needs to read nothing else. One of `Healthy`, `Degraded` or `Unhealthy`. |
| `WebVersion` | `string` | The version of the web tier that answered, for comparison with each service's own version; a mismatch means a partial upgrade. |
| `GeneratedAt` | `datetime` | When (UTC) the verdicts were derived. Every "last seen n seconds ago" reason is relative to this moment. |
| `Services` | `object[]` | One entry per service, always present and always in the order WorkerSync, WorkerDelivery, Scheduler. A service that has never reported is present as Unhealthy (NeverStarted) rather than missing. Each has `Service`, `Status`, `Condition`, `Reason`, `InstanceId`, `HostName`, `Version`, `StartedAt`, `LastSeenAt`, `CurrentWork`, `CurrentWorkStartedAt`, `LastProgressAt`, `Detail`, `ProcessMemoryBytes`, `MemoryTotalBytes` and `MemoryAvailableBytes`. |
<!-- END GENERATED: Get-JIMServiceHealth -->

### Examples

```powershell title="Is the Worker running, and what is it doing?"
Get-JIMServiceHealth
```

```powershell title="The columns that matter during a change window"
Get-JIMServiceHealth | Format-Table Service, Status, CurrentWork, LastSeenAt, Version
```

```powershell title="Fail a monitoring check when any service is unhealthy"
$health = Get-JIMServiceHealth -Summary
if ($health.Overall -ne 'Healthy') {
    $health.Services | Where-Object Status -ne 'Healthy' | Format-List Service, Status, Condition, Reason
    exit 1
}
```

```powershell title="Name the work that has stalled"
Get-JIMServiceHealth | Where-Object Condition -eq 'Stalled' | Select-Object Service, CurrentWork, LastProgressAt
```

```powershell title="Find services running a different version from the web tier"
$health = Get-JIMServiceHealth -Summary
$health.Services | Where-Object { $_.Version -and $_.Version -ne $health.WebVersion } |
    Select-Object Service, HostName, Version, @{ Name = 'WebVersion'; Expression = { $health.WebVersion } }
```

### Notes

- Requires an active connection via [Connect-JIM](connection.md#connect-jim) and the **Administrator** role.
- `Healthy` means a heartbeat within the last 15 seconds. `Degraded` is `HeartbeatOverdue` (more than 15 seconds) or `Stalled` (the current work has not moved forward for 10 minutes). `Unhealthy` is `NoHeartbeat` (more than 60 seconds for the Worker services, 120 seconds for the Scheduler) or `NeverStarted`. `Overall` is the worst status present, so a monitoring script needs to read nothing else.
- `HeartbeatOverdue` is worth a glance, not an alarm: a slow database or a paused process produces it. `Unhealthy` and `Stalled` are what the portal raises its administrator banner for.
- Calls `GET /api/v1/system/health`, which is marked `Cache-Control: no-store`; every call is a fresh verdict.

---

## :material-information-outline: Get-JIMVersion

Retrieves the JIM application version. Does not require authentication.

<!-- BEGIN GENERATED: Get-JIMVersion -->
### Syntax

```powershell
Get-JIMVersion [[-Url] <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No |  | The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted. Can be given by position, as the first argument. |

### Output

One object with Product and Version.

| Property | Type | Description |
|----------|------|-------------|
| `Product` | `string` | The product's name: always JIM. |
| `Version` | `string` | The version the instance runs, for example 0.15.0. |
<!-- END GENERATED: Get-JIMVersion -->

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

## :material-shield-key-outline: Get-JIMAuthConfig

Retrieves the OIDC/OAuth client discovery configuration. Does not require authentication. Useful for scripting SSO setup or validating configuration.

<!-- BEGIN GENERATED: Get-JIMAuthConfig -->
### Syntax

```powershell
Get-JIMAuthConfig [[-Url] <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Url` | `string` | No |  | The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted. Can be given by position, as the first argument. |

### Output

The settings a client needs to start a sign-in.

| Property | Type | Description |
|----------|------|-------------|
| `Authority` | `string` | The OIDC authority URL (e.g., https://login.microsoftonline.com/{tenant}/v2.0). |
| `ClientId` | `string` | The OAuth client ID for interactive authentication. |
| `Scopes` | `string[]` | The OAuth scopes to request (e.g., ["openid", "profile", "api://client-id/access_as_user"]). |
| `ResponseType` | `string` | The OAuth response type. Always "code" for Authorization Code flow. |
| `UsePkce` | `bool` | Whether PKCE is required. Always true for public clients. |
| `CodeChallengeMethod` | `string` | The PKCE code challenge method. Always "S256" (SHA256). |
<!-- END GENERATED: Get-JIMAuthConfig -->

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

## :material-account-circle-outline: Get-JIMUserInfo

Retrieves the current authenticated user's details, roles, and authorisation status. Requires an active `Connect-JIM` session.

<!-- BEGIN GENERATED: Get-JIMUserInfo -->
### Syntax

```powershell
Get-JIMUserInfo
```

### Parameters

None.

### Output

One object saying who the caller is to JIM. A caller with no JIM identity is answered rather than refused: Authorised is `$false` and Message says what to do.

| Property | Type | Description |
|----------|------|-------------|
| `Authorised` | `bool` | Whether the caller has a JIM identity (a Metaverse Object) and so can use JIM. |
| `IsAdministrator` | `bool` | Whether the caller holds the Administrator role. |
| `Name` | `string` | The caller's name: the one their identity provider gives them, or the API key's name. |
| `AuthMethod` | `string` | How the caller authenticated: oauth or api_key. |
| `MetaverseObjectId` | `guid` | The caller's Metaverse Object; empty when they have no JIM identity. |
| `Roles` | `string[]` | The names of the roles the caller holds; empty when they have no JIM identity. |
| `Message` | `string` | Why a caller with no JIM identity cannot use JIM, and what to do about it. Present only when Authorised is false. |
<!-- END GENERATED: Get-JIMUserInfo -->

### Examples

```powershell title="Get current user info"
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"
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
(Get-JIMUserInfo).Roles
```

### Notes

- Requires an active connection via [Connect-JIM](connection.md#connect-jim).
- This endpoint does not require the Administrator role; any authenticated user or API key can call it.
- If the user is authenticated but not authorised (no JIM identity), `Authorised` is `$false` and a `Message` explains why.

---

## :material-restart-alert: Reset-JIMSystem

Performs a factory reset against the connected JIM instance, wiping all data and configuration while preserving the schema, seeded built-ins, and infrastructure access. By default the administrator users are preserved so you are not locked out of the portal. This operation is destructive and cannot be undone; take a database backup first.

<!-- BEGIN GENERATED: Reset-JIMSystem -->
### Syntax

```powershell
Reset-JIMSystem [-Force] [-IncludeAdministrators] [-AcknowledgeAdministratorLockout] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Force` | `switch` | No |  | Skips the confirmation prompt. |
| `IncludeAdministrators` | `switch` | No |  | Also removes the Metaverse Objects holding the built-in Administrator role, leaving a true brand-new install. By default these are preserved so you are not locked out of the portal. When set with no initial administrator configured (JIM_SSO_INITIAL_ADMIN), the reset is refused unless `-AcknowledgeAdministratorLockout` is also supplied. |
| `AcknowledgeAdministratorLockout` | `switch` | No |  | Acknowledges the portal lockout risk so an administrator-inclusive wipe may proceed even when no initial administrator is configured (for example when access is retained via the infrastructure API key). Ignored unless `-IncludeAdministrators` is also set. |

### Output

One object counting what the reset removed, and how many administrators it kept or removed.

| Property | Type | Description |
|----------|------|-------------|
| `ConnectedSystemsRemoved` | `int` | Connected Systems removed. |
| `ConnectedSystemObjectsRemoved` | `int` | Connected System Objects removed. |
| `MetaverseObjectsRemoved` | `int` | Metaverse Objects removed, counting the administrators when the wipe included them. |
| `MetaverseObjectChangesRemoved` | `int` | Metaverse Object change records removed. |
| `ConnectedSystemObjectChangesRemoved` | `int` | Connected System Object change records removed. |
| `SyncRulesRemoved` | `int` | Synchronisation Rules removed. |
| `ObjectMatchingRulesRemoved` | `int` | Object Matching Rules removed. |
| `SchedulesRemoved` | `int` | Schedules removed. |
| `ScheduleExecutionsRemoved` | `int` | Records of Schedule runs removed. |
| `ActivitiesRemoved` | `int` | Activities removed. |
| `PendingExportsRemoved` | `int` | Pending Exports removed. |
| `CustomMetaverseObjectTypesRemoved` | `int` | Metaverse Object Types removed; the built-in ones are kept. |
| `CustomMetaverseAttributesRemoved` | `int` | Metaverse Attributes removed; the built-in ones are kept. |
| `CustomRolesRemoved` | `int` | Roles removed; the built-in ones are kept. |
| `CustomConnectorDefinitionsRemoved` | `int` | Connector Definitions removed; the built-in ones are kept. |
| `CustomPredefinedSearchesRemoved` | `int` | Predefined Searches removed; the built-in ones are kept. |
| `CustomExampleDataSetsRemoved` | `int` | Example Data Sets removed; the built-in ones are kept. |
| `CustomExampleDataTemplatesRemoved` | `int` | Example Data Templates removed; the built-in ones are kept. |
| `CustomApiKeysRemoved` | `int` | API Keys removed; infrastructure keys are kept. |
| `TrustedCertificatesRemoved` | `int` | Trusted Certificates removed. |
| `AdministratorsRetained` | `int` | Number of Metaverse Objects holding the built-in Administrator role that were preserved by the reset. Zero when the reset was performed with administrators included in the wipe. |
| `AdministratorsRemoved` | `int` | Number of Metaverse Objects holding the built-in Administrator role that were removed by the reset. Non-zero only when the reset was performed with administrators included in the wipe. |
<!-- END GENERATED: Reset-JIMSystem -->

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

## :material-book-open-variant-outline: See also

- [Interactive API reference](../../api/reference/): covers the system endpoints (health, readiness, liveness, version, auth config, user info, Service Health, factory reset)
- [Operations](../configuration/operations.md): the Service Health strip these cmdlets read from, and what each state means
- [Connection](connection.md): establishing and managing connections to JIM
