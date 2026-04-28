---
title: API
---

# API

JIM exposes a REST API for programmatic access to all identity management operations: configuring connected systems, triggering synchronisation runs, querying the metaverse, and integrating JIM into wider infrastructure workflows. The API is served from the same application as the web UI; there is no separate service to deploy.

## Where to find what

| Looking for | Go to |
|-------------|-------|
| Endpoint signatures, request and response schemas, error codes | The [interactive API reference](reference/), available on any JIM instance at `/api/reference` |
| OpenAPI document (for client code generation) | `/api/openapi/v1.json` on a JIM instance |
| How to authenticate | [Authentication](authentication.md) |
| What a JIM object is and how it fits into a workflow | [Configuration](../configuration/index.md) |
| PowerShell cmdlets that wrap the API | [PowerShell Module](../powershell/index.md) |

The interactive API reference is the canonical, up-to-date specification of every endpoint. The pages in this section cover the cross-cutting concerns of using the API itself; the per-object behaviour (what a Connected System is, how a Schedule works) lives in the Configuration section.

## Versioning

The API uses URL path-based versioning. The current version is `v1`; all endpoints are prefixed with `/api/v1/`. Future versions will appear under `/api/v2/` etc., with a deprecation period for the previous version.

## Authentication

All endpoints require authentication except a small number of system endpoints (health probes, version, auth config). Most endpoints additionally require the **Administrator** role. JIM supports two methods, both detailed in [Authentication](authentication.md):

- **API keys** in the `X-Api-Key` header, suitable for scripts, automation, and service-to-service integrations
- **JWT Bearer tokens** in the `Authorization: Bearer <token>` header, suitable for user-driven integrations via OIDC/SSO

## Conventions

These behaviours are common across the API. The interactive API reference is authoritative for any specific endpoint; the summary here is for orientation.

**Pagination.** List endpoints accept `page`, `pageSize`, `sortBy`, `sortDirection`, and `filter` query parameters and return a paginated envelope (`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`). The interactive API reference lists the exact parameter constraints for each endpoint.

**Errors.** All errors return a consistent JSON shape with a machine-readable `code`, a human-readable `message`, optional `details` and `validationErrors`, and a `timestamp`. The full set of error codes is documented per endpoint in the interactive API reference.

**Asynchronous operations.** Long-running operations (schema import, run profile execution, connected system deletion) return `202 Accepted` with an activity ID; poll [Activities](../configuration/activities.md) to track progress.

## System endpoints

A small set of system-level endpoints (health, readiness, liveness, version, auth config, user info) are useful for orchestrators, load balancers, and client bootstraps rather than identity management workflows. They are documented in the interactive API reference alongside everything else.
