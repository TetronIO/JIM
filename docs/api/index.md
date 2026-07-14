---
title: API
---

# API

JIM exposes a REST API for programmatic access to all identity management operations: configuring Connected Systems, triggering synchronisation runs, querying the metaverse, and integrating JIM into wider infrastructure workflows. The API is served from the same application as the web UI; there is no separate service to deploy.

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

## Breaking changes

JIM is pre-v1.0, so breaking changes to the API can still occur between releases. Changes that affect existing integrations are called out here.

**This release**

- **`ActivityTargetType` serialised name change.** The `ActivityTargetType` enum member for a Synchronisation Rule is now serialised as `"SynchronisationRule"` (previously `"SyncRule"`) in REST API responses and the OpenAPI schema. Its numeric value is unchanged (`4`). Consumers that string-match an Activity's target type against `"SyncRule"` must update to `"SynchronisationRule"`; consumers that compare the numeric value need no change. This is a pre-v1.0 breaking change.

## Authentication

All endpoints require authentication except a small number of system endpoints (health probes, version, auth config). Most endpoints additionally require the **Administrator** role. JIM supports two methods, both detailed in [Authentication](authentication.md):

- **API keys** in the `X-Api-Key` header, suitable for scripts, automation, and service-to-service integrations
- **JWT Bearer tokens** in the `Authorization: Bearer <token>` header, suitable for user-driven integrations via OIDC/SSO

## Conventions

These behaviours are common across the API. The interactive API reference is authoritative for any specific endpoint; the summary here is for orientation.

**Pagination.** List endpoints accept `page`, `pageSize`, `sortBy`, `sortDirection`, and `filter` query parameters and return a paginated envelope (`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`). The interactive API reference lists the exact parameter constraints for each endpoint.

**Errors.** All errors return a consistent JSON shape with a machine-readable `code`, a human-readable `message`, optional `details` and `validationErrors`, and a `timestamp`. The full set of error codes is documented per endpoint in the interactive API reference.

**Asynchronous operations.** Long-running operations (schema import, Run Profile execution, Connected System deletion) return `202 Accepted` with an activity ID; poll [Activities](../configuration/activities.md) to track progress.

**Rate limiting.** Requests are throttled per client (see [Rate Limiting](rate-limiting.md)); an exceeded limit returns `429 Too Many Requests` with a `Retry-After` header.

## System endpoints

A small set of system-level endpoints (health, readiness, liveness, version, auth config, user info) are useful for orchestrators, load balancers, and client bootstraps rather than identity management workflows. They are documented in the interactive API reference alongside everything else.
