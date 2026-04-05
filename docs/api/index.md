---
title: API Reference
---

# API Reference

JIM provides a comprehensive REST API for programmatic access to all identity management operations. Use it to automate connected system configuration, trigger synchronisation runs, query the metaverse, and integrate JIM into your infrastructure workflows.

The API is served from the same application as the web UI; there is no separate service to deploy.

## Quick Start

**Base URL:**

```
https://jim.example.com/api/v1/
```

**Authenticate** with an API key or JWT Bearer token (see [Authentication](authentication.md) for full details):

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/health \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
    Get-JIMHealth
    ```

## Resources

### Synchronisation

| Resource | Description |
|----------|-------------|
| [Connected Systems](connected-systems/index.md) | External identity stores that synchronise with the JIM metaverse |
| [Run Profiles](run-profiles/index.md) | Import, sync, and export operations for connected systems |
| Sync Rules | Attribute mappings, scoping, and join logic between connected systems and the metaverse |

### Metaverse

| Resource | Description |
|----------|-------------|
| Object Types | Schema definitions for metaverse objects |
| Attributes | Metaverse attribute definitions and object type associations |
| Objects | Identity objects stored in the metaverse |
| Pending Deletions | Metaverse objects awaiting deletion after grace period |

### Scheduling

| Resource | Description |
|----------|-------------|
| [Schedules](schedules/index.md) | Automated execution schedules with ordered steps |
| [Schedule Executions](schedules/executions.md) | Running and completed schedule execution records |

### Operations

| Resource | Description |
|----------|-------------|
| [Activities](activities/index.md) | Audit trail of all operations (imports, syncs, exports) |
| Logs | Application log file access and filtering |
| History | Deletion history and retention cleanup |

### Configuration

| Resource | Description |
|----------|-------------|
| API Keys | API key lifecycle management |
| Certificates | Certificate storage for connector authentication |
| Service Settings | Runtime configuration values |
| Security | Role definitions |

### System

| Resource | Description |
|----------|-------------|
| Health | Health, readiness, and liveness probes |
| Auth Config | OIDC client discovery configuration |
| User Info | Current authenticated user details |

## Versioning

JIM uses URL path-based versioning. The current API version is **v1**. All endpoints are prefixed with `/api/v1/`.

Future versions will be introduced under `/api/v2/` etc., with previous versions maintained for a deprecation period.

## Conventions

### Authentication

All endpoints require authentication except Health and Auth Config. Most endpoints also require the **Administrator** role.

Two authentication methods are supported:

- **API keys:** include in the `X-Api-Key` header. Best for scripts, automation, and service-to-service integrations.
- **JWT Bearer tokens:** include in the `Authorization: Bearer <token>` header. Best for user-driven integrations via OIDC/SSO.

See [Authentication](authentication.md) for setup instructions and examples.

### Pagination

List endpoints return paginated results:

| Parameter       | Type    | Default | Description                        |
|-----------------|---------|---------|------------------------------------|
| `page`          | integer | `1`     | Page number (1-based)              |
| `pageSize`      | integer | `25`    | Items per page (max 100)           |
| `sortBy`        | string  |         | Field name to sort by              |
| `sortDirection` | string  | `asc`   | Sort order: `asc` or `desc`        |
| `filter`        | string  |         | Filter expression                  |

All paginated responses share this envelope:

```json
{
  "items": [],
  "totalCount": 142,
  "page": 1,
  "pageSize": 25,
  "totalPages": 6,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

### Error Responses

All errors return a consistent JSON format:

```json
{
  "code": "VALIDATION_ERROR",
  "message": "Name is required.",
  "details": null,
  "validationErrors": {
    "name": ["The Name field is required."]
  },
  "timestamp": "2026-04-05T10:30:00Z"
}
```

| Code | Description |
|------|-------------|
| `VALIDATION_ERROR` | Request body or query parameter validation failed |
| `NOT_FOUND` | The requested resource does not exist |
| `UNAUTHORISED` | Authentication is missing or invalid |
| `FORBIDDEN` | Authenticated but insufficient permissions |
| `CONFLICT` | Operation conflicts with current state |
| `BAD_REQUEST` | Malformed request |
| `INTERNAL_ERROR` | Unexpected server error |
| `SERVICE_UNAVAILABLE` | Service is temporarily unavailable |

### Asynchronous Operations

Some long-running operations (schema import, run profile execution, connected system deletion) return `202 Accepted` with an activity or task ID. Poll the Activities resource to track progress.

## Interactive Documentation

JIM includes Swagger UI for interactive API exploration:

```
https://jim.example.com/api/swagger
```

The Swagger UI provides a browsable list of all endpoints, request/response schemas, and the ability to execute API calls directly from the browser. It also exposes the OpenAPI specification for client code generation.

!!! tip
    Use this reference documentation for understanding how the API works and building integrations. Use Swagger UI when you need to quickly test an endpoint or generate a client library.
