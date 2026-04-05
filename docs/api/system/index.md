---
title: System
---

# System

System endpoints provide health checks, authentication configuration, and current user information. Health and Auth Config endpoints do not require authentication, making them suitable for monitoring and client bootstrapping.

| Endpoint | Auth Required | Description |
|----------|---------------|-------------|
| [Health](#health) | No | Basic health check |
| [Readiness](#readiness) | No | Database and service readiness |
| [Liveness](#liveness) | No | Process liveness probe |
| [Version](#version) | No | Application version |
| [Auth Config](#auth-config) | No | OIDC client discovery configuration |
| [User Info](#user-info) | Yes | Current authenticated user details |

---

## Health

Returns a basic health status indicating the application is running.

```
GET /api/v1/health
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/health
    ```

=== "PowerShell"

    ```powershell
    # No connection required; health endpoints are anonymous
    Invoke-RestMethod -Uri "https://jim.example.com/api/v1/health"
    ```

### Response

```json
{
  "status": "healthy",
  "timestamp": "2026-04-05T10:00:00Z"
}
```

---

## Readiness

Checks whether JIM is ready to accept requests. Verifies database connectivity and checks maintenance mode status.

```
GET /api/v1/health/ready
```

!!! tip
    Use this endpoint as a Kubernetes readiness probe or load balancer health check.

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/health/ready
    ```

=== "PowerShell"

    ```powershell
    Invoke-RestMethod -Uri "https://jim.example.com/api/v1/health/ready"
    ```

### Response

```json
{
  "status": "ready",
  "timestamp": "2026-04-05T10:00:00Z"
}
```

When not ready:

```json
{
  "status": "not_ready",
  "timestamp": "2026-04-05T10:00:00Z"
}
```

---

## Liveness

Simple liveness check confirming the process is running.

```
GET /api/v1/health/live
```

!!! tip
    Use this endpoint as a Kubernetes liveness probe.

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/health/live
    ```

=== "PowerShell"

    ```powershell
    Invoke-RestMethod -Uri "https://jim.example.com/api/v1/health/live"
    ```

### Response

```json
{
  "status": "alive",
  "timestamp": "2026-04-05T10:00:00Z"
}
```

---

## Version

Returns the JIM application version.

```
GET /api/v1/health/version
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/health/version
    ```

=== "PowerShell"

    ```powershell
    Invoke-RestMethod -Uri "https://jim.example.com/api/v1/health/version"

    # Or with an established connection:
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
    Test-JIMConnection
    ```

### Response

```json
{
  "product": "JIM",
  "version": "0.3.0"
}
```

---

## Auth Config

Returns the OIDC/OAuth configuration needed for client applications to initiate authentication. This is used by the JIM web UI and can be used by custom integrations.

```
GET /api/v1/auth/config
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/auth/config
    ```

=== "PowerShell"

    ```powershell
    Invoke-RestMethod -Uri "https://jim.example.com/api/v1/auth/config"
    ```

### Response

```json
{
  "authority": "https://login.example.com",
  "clientId": "jim-client-id",
  "scopes": ["openid", "profile", "email"],
  "responseType": "code",
  "usePkce": true,
  "codeChallengeMethod": "S256"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `authority` | string | OIDC authority URL |
| `clientId` | string | OAuth client ID |
| `scopes` | array | OAuth scopes to request |
| `responseType` | string | OAuth response type (always `code`) |
| `usePkce` | boolean | Whether PKCE is required (always `true`) |
| `codeChallengeMethod` | string | PKCE challenge method (always `S256`) |

---

## User Info

Returns information about the currently authenticated user, including their roles and authorisation status.

```
GET /api/v1/userinfo
```

!!! note
    This endpoint requires authentication but does not require the Administrator role. Any authenticated user or API key can call it.

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/userinfo \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Connection details include user info
    Test-JIMConnection
    ```

### Response

Authorised user:

```json
{
  "authorised": true,
  "isAdministrator": true,
  "name": "Jane Smith",
  "authMethod": "oauth",
  "metaverseObjectId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "roles": ["Administrator"]
}
```

Authenticated but not authorised (no JIM identity):

```json
{
  "authorised": false,
  "isAdministrator": false,
  "name": "Unknown User",
  "authMethod": "oauth",
  "metaverseObjectId": null,
  "roles": [],
  "message": "Authenticated but no matching JIM identity found. Contact your administrator."
}
```

| Field | Type | Description |
|-------|------|-------------|
| `authorised` | boolean | Whether the user has a JIM identity and can access the system |
| `isAdministrator` | boolean | Whether the user has the Administrator role |
| `name` | string | Display name |
| `authMethod` | string | `oauth` or `api_key` |
| `metaverseObjectId` | guid, nullable | The user's metaverse object ID (null if not authorised) |
| `roles` | array | Role names assigned to the user |
| `message` | string, nullable | Additional context (present when not authorised) |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
