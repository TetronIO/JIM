# SCIM 2.0 Test Provider Analysis

- **Date:** 2026-07-26
- **Context:** Choosing integration-test targets for the SCIM 2.0 Client Connector ([#545](https://github.com/TetronIO/JIM/issues/545))
- **Related Plans:** [`../plans/done/SCIM_CLIENT_CONNECTOR_DESIGN.md`](../plans/done/SCIM_CLIENT_CONNECTOR_DESIGN.md), [`../plans/SCIM_SERVER_DESIGN.md`](../plans/SCIM_SERVER_DESIGN.md)

Point-in-time record of the survey and hands-on probing behind the connector's integration-test strategy. Kept so a future revisit does not repeat the search from scratch. Findings age: treat the capability table as accurate for July 2026 and re-probe before relying on it.

## Decision

Three test targets:

1. **A purpose-built mock provider in the test harness.** Mandatory, not a convenience: it is the only way to exercise the connector's failure paths.
2. **`limosa-io/laravel-scim-server`** in Docker, as the real-world conformance target.
3. **JIM's own SCIM 2.0 Service Provider** ([#124](https://github.com/TetronIO/JIM/issues/124)) once built, for the JIM-to-JIM round trip.

Keycloak's native `scim-api` was considered as a near-free second real provider (`jim.keycloak` is already in the stack) and deferred: it is preview quality, disabled by default, moves between releases, and supports neither Bulk nor cursor pagination.

## Requirement coverage

What the connector must exercise, against what each candidate can actually do.

| Requirement | Laravel server | Keycloak `scim-api` | scimgateway | i2scim | Mock (ours) |
|---|---|---|---|---|---|
| Discovery (`/ServiceProviderConfig`, `/Schemas`, `/ResourceTypes`) | Yes | Yes | Yes | Yes | Yes |
| Users and Groups CRUD | Yes | Yes | Yes | Yes | Yes |
| Index pagination (`startIndex`/`count`) | Yes | Yes | Yes | Yes | Yes |
| Cursor pagination (RFC 9865) | **Yes** | No | No | No | Yes |
| PATCH | Yes | Yes | Yes | Yes | Yes |
| ETag / `If-None-Match` | Yes | Not documented | Yes | Yes | Yes |
| `meta.lastModified gt` filtering | **Yes (verified)** | Unverified | Unverified | Unverified | Yes |
| Bulk | Yes | No | Yes | Unclear | Yes |
| Authentication actually enforced | **No (open by default)** | Yes | Yes | Yes | Yes |
| 429 with `Retry-After` on demand | No | No | No | No | **Yes** |
| Missing `/Schemas` (fallback path) | No | No | No | No | **Yes** |
| PATCH advertised unsupported (PUT degradation) | No | No | No | No | **Yes** |
| Malformed responses, expired cursors | No | No | No | No | **Yes** |

The last four rows are the argument for the mock. Every real provider behaves correctly by design and exposes no switch to misbehave, yet those rows carry four of the connector's stated success criteria. The mock's purpose is to misbehave on request, which is why it does not overlap with #124, whose purpose is never to.

## Candidates surveyed

| Project | Language / Licence | Runnable server? | Docker | Verdict |
|---|---|---|---|---|
| [limosa-io/laravel-scim-server](https://github.com/limosa-io/laravel-scim-server) | PHP (Laravel), MIT | Yes | Official (`ghcr.io`) | **Chosen.** Only runnable server found supporting both pagination styles |
| Keycloak native `scim-api` | Java, Apache-2.0 | Yes | Official, already in stack | Deferred: preview, no Bulk, no cursor |
| [python-scim/scim2-server](https://github.com/python-scim/scim2-server) | Python, Apache-2.0 | Yes | None | Viable fallback; ETag support is good, no Docker image |
| [i2-open/i2scim](https://github.com/i2-open/i2scim) | Java (Quarkus), Apache-2.0 | Yes | Yes | Weak conformance target: explicitly follows Postel's Law and accepts malformed input, hiding our bugs |
| [jelhub/scimgateway](https://github.com/jelhub/scimgateway) | TypeScript, MIT | Yes (gateway) | Yes | Solid but index-pagination only |
| [apache/directory-scimple](https://github.com/apache/directory-scimple) | Java, Apache-2.0 | Library plus demo servers | Third-party, stale | Still at 1.0.0-M1; ships a reusable `scim-compliance-tests` module |
| WSO2 Identity Server | Java, Apache-2.0 | Yes | Yes (635 MB, 2 GB RAM) | Open pagination bugs, effectively no ETag, restricted filter operators |
| [wso2/charon](https://github.com/wso2/charon) | Java, Apache-2.0 | Library | No | Not runnable as a server |
| [elimity-com/scim](https://github.com/elimity-com/scim) | Go, MIT | Library | No | No filtering, no pagination |
| [Captain-P-Goldfish/SCIM-SDK](https://github.com/Captain-P-Goldfish/SCIM-SDK) | Java, BSD-3 | Library | No | Implements RFC 9865 cursor, but we would have to build the server |
| Zitadel SCIM v2 | Go, Apache-2.0 | Yes | Official | Users only, no Groups; `etag.supported: false` |
| [15five/django-scim2](https://github.com/15five/django-scim2) | Python, MIT | Django app | No | Partial discovery, low maintenance cadence |

Ruled out on principle rather than capability:

- **`scim-for-keycloak`**: the open-source line ended at Keycloak 21.x; current free tier requires a licence key renewed every 14 days, which breaks JIM's air-gap requirement outright.
- **Microsoft SCIM Validator**: cloud-only, no self-hostable build.
- **`AzureAD/SCIMReferenceCode`**: shipped "AS IS" with no maintenance guarantee, and its sample token controller disables JWT validation.

There is no official IETF or scim.cloud reference implementation.

## Hands-on probe results (2026-07-26)

Run against `ghcr.io/limosa-io/laravel-scim-server:latest` (digest `sha256:4cf54294…`), 50 seeded users all carrying `meta.lastModified` of `2026-02-04T13:53:42+00:00`.

**Advertised capabilities** (`GET /scim/v2/ServiceProviderConfig`): `patch`, `bulk` (`maxOperations` 10, `maxPayloadSize` 1 MB), `filter` (`maxResults` 100), `changePassword`, `sort` and `etag` all supported. A non-standard `pagination` block advertises `{"cursor": true, "index": true, "defaultPaginationMethod": "index", "defaultPageSize": 10, "maxPageSize": 100, "cursorTimeout": 3600}`.

**Both unknowns resolved:**

| Probe | Result |
|---|---|
| `filter=meta.lastModified gt "2026-01-01T00:00:00Z"` | 50 of 50, correct |
| `filter=meta.lastModified gt "2026-07-01T00:00:00Z"` | 0, correct |
| `filter=meta.lastModified lt "2026-07-01T00:00:00Z"` | 50, correct |
| `filter=meta.created gt "2026-07-01T00:00:00Z"` | 0, correct |
| `count=2` | `itemsPerPage: 2`, so RFC 7644's `count` is honoured, not only the documented `size` |
| `cursor=` with `count=3` | Returns `nextCursor` (base64 JSON carrying the last id), confirming real cursor pagination |

**Quirk found:** `ge` is exclusive. `meta.lastModified ge "<exact stored timestamp>"` returns 0 where it should return 50, in both the `Z` and `+00:00` forms, while `ge` against an earlier timestamp returns all 50. So `ge` behaves as `gt`. This does not affect the connector, which uses `gt`, but it rules out `ge` as a mitigation for the watermark boundary problem below, at least against this provider.

**Endpoint paths** are rooted at `/scim/v2`; the discovery endpoints 404 at any other prefix. Useful confirmation that base URLs carry a path prefix in practice, which the client's URL composition already handles.

**Authentication is not enforced.** The image advertises OAuth Bearer in `authenticationSchemes`, but `GET /scim/v2/Users` with no credentials returns 200. The connector's four authentication strategies therefore cannot be integration-tested against this target as shipped; the mock must cover them.

## Consequence for delta import: the watermark boundary

The probe surfaced a design point worth settling in Phase 5 rather than discovering in production. This provider's `meta.lastModified` has **one-second precision** with no fractional component. A watermark strategy that stores the highest `lastModified` seen and next runs `gt <watermark>` will **miss** any object modified during that same second but not yet read when the page was fetched. Using `ge` instead would re-import the boundary objects harmlessly, except that `ge` is broken here (above).

Given that synchronisation integrity is paramount and re-importing an unchanged object is idempotent while missing a change is silent data divergence, the watermark should be stored with a small safety margin behind the highest observed value, accepting deliberate overlap. To be decided and recorded in Phase 5.

## Harness-side tooling

- [`python-scim/scim2-tester`](https://github.com/python-scim/scim2-tester) (Apache-2.0, pip): drives discovery, CRUD and PATCH conformance checks; designed for CI. Useful for asserting our mock is itself spec-conformant, so it does not drift into testing a fiction.
- Apache SCIMple ships a `scim-compliance-tests` Maven module usable against any server.
