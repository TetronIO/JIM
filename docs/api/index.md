---
title: API
description: "What JIM's REST API covers, where its reference lives, and how to authenticate, page, filter and handle errors."
type: reference
audience: automation-author
---

# API

JIM exposes a REST API for programmatic access to all identity management operations: configuring Connected Systems, triggering synchronisation runs, querying the Metaverse, and integrating JIM into wider infrastructure workflows. The API is served from the same application as the web UI; there is no separate service to deploy.

```jim-diagram
archetype: map
title: How the API and its documentation fit together
desc: "The PowerShell Module wraps JIM's REST API, which serves every endpoint under /api/v1/. Every request needs Authentication, except a few system endpoints such as the health probes, and requests are throttled by Rate Limiting. Every endpoint is documented in the interactive API reference, which is also served as an OpenAPI document for generating client code, and the Configuration section explains the objects the endpoints manage. Each node except the REST API and the OpenAPI document links to its page."
nodes:
- {id: auth, label: Authentication, link: authentication.md, row: 0, col: 1}
- {id: ps, label: PowerShell Module, link: ../powershell/index.md, row: 1, col: 0}
- {id: api, label: "REST API, `/api/v1/`", row: 1, col: 1}
- {id: ref, label: API reference, link: reference/, row: 1, col: 2}
- {id: openapi, label: OpenAPI document, row: 0, col: 2}
- {id: rate, label: Rate Limiting, link: rate-limiting.md, row: 2, col: 1}
- {id: config, label: Configuration, link: ../configuration/index.md, row: 2, col: 2}
edges:
- {from: ps, to: api, label: wraps}
- {from: api, to: auth, label: most requests need}
- {from: api, to: rate, label: throttled by}
- {from: api, to: ref, label: documented in}
- {from: ref, to: openapi, label: also as}
- {from: ref, to: config, label: objects explained in}
```

## :material-map-search-outline: Where to find what

| Looking for | Go to |
|-------------|-------|
| Endpoint signatures, request and response schemas, error codes | The [interactive API reference](reference/), available on any JIM instance at `/api/reference` |
| OpenAPI document (for client code generation) | `/api/openapi/v1.json` on a JIM instance |
| How to authenticate | [Authentication](authentication.md) |
| What a JIM object is and how it fits into a workflow | [Configuration](../configuration/index.md) |
| PowerShell cmdlets that wrap the API | [PowerShell Module](../powershell/index.md) |

The interactive API reference is the canonical, up-to-date specification of every endpoint. The pages in this section cover the cross-cutting concerns of using the API itself; the per-object behaviour (what a Connected System is, how a Schedule works) lives in the Configuration section.

## :material-folder-multiple-outline: Resource groups

The interactive API reference groups its endpoints by resource. Each group, and the page that explains what it manages:

| Resource group | What it covers |
|----------------|----------------|
| Activities | [Activities](../configuration/activities.md), their items and their progress (`/api/v1/activities`) |
| ApiKeys | The [API keys](../configuration/api-keys.md) scripts and integrations authenticate with (`/api/v1/apikeys`) |
| Auth | The OIDC settings a client needs before it can sign in (`/api/v1/auth/config`) and whether JIM can [trust its identity provider](../administration/sso-setup/trusting-your-identity-provider.md) (`/api/v1/auth/identity-provider-trust`), both unauthenticated; and, for administrators, the [sign-in problems](../administration/troubleshooting.md#a-setting-stopped-sign-in) JIM has met (`/api/v1/auth/sign-in-diagnostics`) |
| Certificates | [Trusted Certificates](../configuration/certificates.md) for connections to Connected Systems and for signing in through your identity provider (`/api/v1/certificates`) |
| ConfigurationTests | Saved [configuration test cases](../configuration/configuration-tests.md), and running them (`/api/v1/configuration-tests`) |
| ExampleData | Example Data templates and sets, for filling a test instance with realistic objects (`/api/v1/example-data`) |
| FeatureFlags | Turning [Preview features](../administration/preview-features.md) on and off (`/api/v1/features`) |
| FileSystem | Browsing the files a connector can read inside the container, such as the [File Connector](../connectors/jim-file-connector.md)'s import files (`/api/v1/filesystem`) |
| GeneratedValues | Generated values a target refused, waiting for an [administrator's decision](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value) (`/api/v1/generated-values`) |
| Health | Health, readiness, liveness and version probes for load balancers and orchestrators (`/api/v1/health`); unauthenticated, and answering for the web tier only |
| History | Searching changes to every Metaverse Object ([Object Changes](../configuration/object-changes.md)), reading a change's values a page at a time, and counting and clearing old change history and deleted-object records (`/api/v1/history`) |
| Logs | Reading JIM's [service logs](../administration/configuration.md#logging) (`/api/v1/logs`) |
| Metaverse | The [Metaverse](../configuration/metaverse.md) schema, Metaverse Objects, [Attribute Priority](../concepts/attribute-priority.md) and pending deletions (`/api/v1/metaverse`) |
| PasswordSynchronisation | The [Password Synchronisation queue](../concepts/password-synchronisation.md#watching-the-queue) (`/api/v1/password-synchronisation`) |
| PredefinedSearches | [Predefined Searches](../configuration/predefined-searches.md) and their criteria (`/api/v1/predefined-searches`) |
| Previews | Reading and cancelling a [Configuration Change Preview](../configuration/configuration-changes.md) (`/api/v1/previews`) |
| ScheduleExecutions | [Schedule Executions](../configuration/schedules.md#executions): watching and cancelling runs (`/api/v1/schedule-executions`) |
| Schedules | [Schedules](../configuration/schedules.md), and running one on demand (`/api/v1/schedules`) |
| ScimServiceProvider | A Connected System's SCIM 2.0 Service Provider endpoint: the keys identity providers call it with, what its store holds, and the resource types and schemas it serves (`/api/v1/synchronisation/connected-systems/{id}/scim`); a [Preview feature](../administration/preview-features.md) |
| Security | [Roles](../configuration/roles.md) and who holds them (`/api/v1/security`) |
| ServiceSettings | [Service Settings](../configuration/service-settings.md) (`/api/v1/service-settings`) |
| Synchronisation | [Connected Systems](../configuration/connected-systems.md), the objects in each Connector Space, [Run Profiles](../configuration/run-profiles.md), [Synchronisation Rules](../configuration/synchronisation-rules.md), [Data Flow](../configuration/data-flow.md) and Pending Exports (`/api/v1/synchronisation`) |
| System | [Service Health](../configuration/operations.md#service-health) for the Worker and the Scheduler, and resetting an instance (`/api/v1/system`) |
| UserInfo | The signed-in user (`/api/v1/userinfo`) |
| WorkerTasks | The worker's task queue, and cancelling a task (`/api/v1/worker-tasks`) |

## :material-tag-multiple-outline: Versioning

The API uses URL path-based versioning. The current version is `v1`; all endpoints are prefixed with `/api/v1/`. A new major version gets its own prefix (`/api/v2/` and so on), and the version before it stays available through a deprecation period.

## :material-alert-decagram-outline: Breaking changes

JIM is pre-v1.0, so breaking changes to the API can still occur between releases. Changes that affect existing integrations are called out here.

**v0.17.0**

- **API keys are sent as bearer tokens.** An API key now goes in the `Authorization` header, `Authorization: Bearer jim_ak_...`, exactly as an SSO access token does; the `X-Api-Key` header is no longer read, so a request that sends a key only there is unauthenticated and receives a `401`. Update any integration or script that sets `X-Api-Key`. Upgrade the JIM PowerShell module together with JIM: version 0.17.0 sends the new header, while earlier versions send `X-Api-Key` and are refused with "JIM rejected the API key", although the key itself is fine. Roll the module back with JIM, too. This is a pre-v1.0 breaking change.
- **Deleting a Synchronisation Rule or one of its Attribute Flows answers `200`, not `204`.** The body carries the affected counts. A script that checks for `204` must accept `200`.
- **Pending Exports no longer fail permanently.** The `Failed` status is gone, with `maxRetries` and `nextRetryAt`: a Pending Export the Connected System refuses stays `Pending`, is attempted again at every export run, and carries `failingSince` until it succeeds. List them with `failing=true` rather than `status=Failed`.
- **Export, Full Synchronisation and Delta Synchronisation Run Profiles take no `filePath`.** It did nothing on those types and is now refused; an export writes to the file the Connected System's own File Path names.
- **Change history returns at most ten value changes per attribute.** Each changed attribute carries its first ten value changes and `valueChangeTotal`; read the rest a page at a time from `/api/v1/history/connected-system-object-changes/{changeId}/attributes/{attributeName}/values` and `/api/v1/history/metaverse-object-changes/{changeId}/attributes/{attributeName}/values`.
- **Date attribute values are always UTC.** Every `dateTimeValue` ends in `Z`, whatever the server's time zone (see Conventions below).
- **Five built-in Metaverse Attributes are renamed** to Proper Case: `Object GUID` (was `objectGUID`), `Object SID` (`objectSid`), `SID History` (`sIDHistory`), `Street Address` (`StreetAddress`) and `Web Page` (`WebPage`). Update any request or filter that names them.
- **Example Data templates lose two fields and three operator names.** `connectedSystemObjectTypeAttributeId` and `boolShouldBeRandom` are removed from template attributes, and dependencies use the scoping operators' names: `GreaterThanOrEquals`, `LessThanOrEquals` and `Contains` in place of `GreaterThanOrEqual`, `LessThanOrEqual` and `Like`.
- **Criteria never compare with an empty value.** A scoping or Predefined Search criterion with an empty value is refused; use the `HasValue` or `HasNoValue` operator, which existing criteria were converted to on upgrade.
- **Saves are checked more strictly.** An Attribute Flow or Example Data expression that does not parse, reads an unknown attribute or returns a type its target can never hold (one left unchanged only warns), an Object Matching Rule that could never match, and deleting, renaming or deselecting an attribute a Scoping Criterion or expression still reads are each refused with the reason.
- **New defaults over the REST API match the portal's.** A Metaverse Object Type created without a deletion rule gets **When Last Connector Disconnected** rather than **Manual**, and a new Object Matching Rule ignores case unless a source attribute is case-exact.
- **The readiness check drops its `error` field.** `/api/v1/health/ready` no longer returns the exception behind a failure to anonymous callers; JIM logs it instead.
- **Four Service Settings are removed:** Maintenance mode, Credential encryption, Verbose no-change recording and SSO unique identifier claim type. A script reading or setting one must stop.

**v0.16.0**

- **A Schedule Execution can end `CompleteWithError`.** A run that carried on past a failed step (because the Schedule or the step is set to continue on failure) ends `CompleteWithError`, naming the failed steps in `errorMessage`; v0.15.0 and earlier ended it `Complete`. An integration or script that waits for a Schedule Execution to reach `Complete`, `Failed` or `Cancelled` must treat `CompleteWithError` as finished too, or it will wait indefinitely.

**v0.15.0**

- **A successful Schedule Execution's status is now `Complete`, not `Completed`.** The value matches the word Activities already use for the same outcome. The REST API and the JIM PowerShell module both send and accept only `Complete`, so any integration or script that filters Schedule Executions on `Completed` must update. This is a pre-v1.0 breaking change.

**v0.14.0**

- **Enum request values must be strings.** Every enum-typed field on a request body must now be sent as its string name (for example `"mode": "AllOf"`); numeric enum values are rejected with a `400 Bad Request`. Responses already emit string names, so a client that echoes back a value it received is unaffected, as is the JIM PowerShell module (it sends strings). Only a client hand-crafting request bodies with numeric enum ordinals (for example `"mode": 0`) is affected; switch those to the string name. This closes a gap where an out-of-range number (for example `"mode": 99`) bound to an undefined enum value instead of being rejected. This is a pre-v1.0 breaking change.
- **Metaverse Object list responses nest the object type.** The object type in Metaverse Object list responses is a nested `type` object (`{ id, name }`), matching the single-object response, instead of the flat `typeId` and `typeName` fields. Callers must read `type.id` and `type.name` instead. This is a pre-v1.0 breaking change.

**v0.13.0**

- **`ActivityTargetType` serialised name change.** The `ActivityTargetType` enum member for a Synchronisation Rule is serialised as `"SynchronisationRule"` (`"SyncRule"` in v0.12.0 and earlier) in REST API responses and the OpenAPI schema. Its numeric value is unchanged (`4`). Consumers that string-match an Activity's target type against `"SyncRule"` must update to `"SynchronisationRule"`; consumers that compare the numeric value need no change. This is a pre-v1.0 breaking change.

## :material-shield-key-outline: Authentication

All endpoints require authentication except a small number of system endpoints (health probes, version, auth config, identity provider trust). Most endpoints additionally require the **Administrator** role. JIM supports two methods, both detailed in [Authentication](authentication.md):

- **API keys**, suitable for scripts, automation, and service-to-service integrations
- **JWT access tokens**, suitable for user-driven integrations via OIDC/SSO

Both are sent the same way, as a bearer token: `Authorization: Bearer <credential>`.

## :material-format-list-checks: Conventions

These behaviours are common across the API. The interactive API reference is authoritative for any specific endpoint; the summary here is for orientation.

**Pagination.** List endpoints accept `page`, `pageSize`, `sortBy`, `sortDirection`, and `filter` query parameters and return a paginated envelope (`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`). `pageSize` is capped at 100. Retrieval depth is capped at **1,000,000 rows**: a request whose `page` and `pageSize` combine to skip more than that returns a `400` rather than being silently clamped, because scanning to such a deep offset is expensive at scale. The ceiling is an offset rather than a page number because that is what the database cost tracks, so the deepest page you may request depends on your page size (10,001 at `pageSize=100`, 20,001 at `pageSize=50`); the error message names the limit and your deepest allowed page. If you genuinely need to walk a very large result set, narrow it with a filter or search rather than paging that deep. The interactive API reference lists the exact parameter constraints for each endpoint.

**Errors.** All errors return a consistent JSON shape with a machine-readable `code`, a human-readable `message`, optional `details` and `validationErrors`, and a `timestamp`. The full set of error codes is documented per endpoint in the interactive API reference.

**Enum values.** Enum-typed fields are always serialised as their string name in responses (for example `"status": "Enabled"`), and must be sent as their string name in request bodies. Numeric enum values are not accepted on input and are rejected with a `400 Bad Request`; this keeps the wire contract stable, since an enum's numeric ordinal is free to change between releases while its name is not.

**Date and time attribute values.** A `dateTimeValue` (on a Metaverse Object, Connected System Object or Pending Export attribute value, a change history entry, or a scoping or search criterion) is always UTC and ends in `Z` (for example `"2026-03-01T09:00:00.123456Z"`), whatever the server's time zone. JIM holds a date to the microsecond, so any digits of a source value beyond the sixth decimal place are dropped, and the fraction is omitted when it is zero (`"2026-03-01T09:00:00Z"`).

**Asynchronous operations.** Long-running operations (schema import, Run Profile execution, Connected System deletion) return `202 Accepted` with an activity ID; poll [Activities](../configuration/activities.md) to track progress. Setting a password (`POST /api/v1/metaverse/objects/{id}/password`, and its single Connected System Object form `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{csoId}/password`) is the one operation that can be asked to wait: it answers `200` once every target Connected System has settled, or `202` with what is known when the time runs out. The default wait follows the target mode (10 seconds when `connectedSystemObjectIds` names the Connected System Objects, none when the password is propagated to every configured system) and `wait` (seconds, 0 to 30) overrides either; see [Passwords](../concepts/password-synchronisation.md#setting-a-password) for the per-target states in the body.

**Rate limiting.** Requests are throttled per client (see [Rate Limiting](rate-limiting.md)); an exceeded limit returns `429 Too Many Requests` with a `Retry-After` header.

## :material-server-outline: System endpoints

A small set of system-level endpoints (health, readiness, liveness, version, auth config, user info) are useful for orchestrators, load balancers, and client bootstraps rather than identity management workflows. They are documented in the interactive API reference alongside everything else.

The unauthenticated health endpoints answer for the web tier only. `GET /api/v1/system/health` (Administrator role) reports whether the Worker's synchronisation loop, its Password Delivery Service and the Scheduler are alive, what each is doing and whether any is short of memory, from the heartbeats they write to the database; it is the endpoint for monitoring that needs to know whether JIM's work is actually being done. See [Operations > Service Health](../configuration/operations.md#service-health) for what each status and condition means.
