---
title: API
---

# API

JIM exposes a REST API for programmatic access to all identity management operations: configuring connected systems, triggering synchronisation runs, querying the metaverse, and integrating JIM into wider infrastructure workflows. The API is served from the same application as the web UI; there is no separate service to deploy.

## Where to find what

This section follows the [Diátaxis](https://diataxis.fr/) split: the pages here are **guidance** (what the resource is, common workflows, important behaviours), and the **reference** (every endpoint, every request and response field, an interactive "Try It" feature) lives in the Scalar API reference.

| Looking for | Go to |
|-------------|-------|
| Endpoint signatures, request and response schemas, error codes | **Scalar API reference** -- on every JIM instance at `/api/reference`, or hosted at [tetronio.github.io/JIM/api/reference/](https://tetronio.github.io/JIM/api/reference/) |
| Raw OpenAPI document (for client code generation) | `/api/openapi/v1.json` on a JIM instance |
| How to authenticate | [Authentication](authentication.md) |
| What a resource is, and how it fits into a workflow | The per-resource pages in this section |
| PowerShell cmdlets that wrap the API | [PowerShell Module](../powershell/index.md) |

The Scalar reference is generated from the OpenAPI document at build time, so it loads instantly and works in air-gapped environments without any cloud calls.

## Resources

### Synchronisation

- [Connected Systems](connected-systems/index.md) -- external identity stores that synchronise with the JIM metaverse
- [Run Profiles](run-profiles/index.md) -- import, sync, and export operations for connected systems
- [Synchronisation Rules](synchronisation-rules/index.md) -- attribute mappings, scoping, and join logic between connected systems and the metaverse

### Metaverse

- [Metaverse](metaverse/index.md) -- object types, attributes, objects, and pending deletions
- [Predefined Searches](predefined-searches/index.md) -- named searches that drive portal list views and the fast search API

### Scheduling and Operations

- [Schedules](schedules/index.md) -- automated execution schedules with ordered steps
- [Activities](activities/index.md) -- audit trail of all operations (imports, syncs, exports, configuration changes)

### Configuration

- [API Keys](api-keys/index.md) -- API key lifecycle management
- [Certificates](certificates/index.md) -- certificate storage for connector authentication
- [Service Settings](service-settings/index.md) -- runtime configuration values
- [Security](security/index.md) -- role definitions and role membership

### System

- [System](system/index.md) -- health, readiness, liveness, version, and authentication configuration

## Conventions at a glance

These behaviours are common across the API. The Scalar reference is authoritative for any specific endpoint; the summary here is for orientation.

**Versioning.** The API uses URL path-based versioning. The current version is `v1`; all endpoints are prefixed with `/api/v1/`. Future versions appear under `/api/v2/` etc., with a deprecation period for the previous version.

**Authentication.** All endpoints require authentication except a small number of system endpoints (health probes, version, auth config). Most endpoints additionally require the **Administrator** role. JIM supports two methods, both detailed in [Authentication](authentication.md):

- **API keys** in the `X-Api-Key` header, suitable for scripts, automation, and service-to-service integrations
- **JWT Bearer tokens** in the `Authorization: Bearer <token>` header, suitable for user-driven integrations via OIDC/SSO

**Pagination.** List endpoints accept `page`, `pageSize`, `sortBy`, `sortDirection`, and `filter` query parameters and return a paginated envelope (`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`). The Scalar reference lists the exact parameter constraints for each endpoint.

**Errors.** All errors return a consistent JSON shape with a machine-readable `code`, a human-readable `message`, optional `details` and `validationErrors`, and a `timestamp`. The full set of error codes is documented in the Scalar reference per endpoint.

**Asynchronous operations.** Long-running operations (schema import, run profile execution, connected system deletion) return `202 Accepted` with an activity ID; poll [Activities](activities/index.md) to track progress.
