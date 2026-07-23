# SCIM 2.0 Client Connector Design Document

- **Status:** Doing (Phase 1 underway; Phases 2-7 outstanding)
- **Issue:** [#545](https://github.com/TetronIO/JIM/issues/545)
- **Related Issues:** #124 (SCIM 2.0 Server Support), #361 (Microsoft Graph Connector), #192 (Generic REST Connector), #875 (centralised connector dispatch)
- **Related Plans:** [`SCIM_SERVER_DESIGN.md`](../SCIM_SERVER_DESIGN.md) (the inverse scenario: JIM as SCIM service provider), [`METAVERSE_SCHEMA_POLICY.md`](../METAVERSE_SCHEMA_POLICY.md) (canonical schema policy and SCIM-parity gap attributes)
- **Last Updated:** 2026-07-23

## Overview

Add a built-in SCIM 2.0 client connector that lets JIM import, synchronise, and export users and groups to and from any system exposing a SCIM 2.0 service provider interface (RFC 7643/7644). JIM acts as the **SCIM client**: it initiates connections to discover schemas, import resources, and export provisioning changes. This is the inverse of #124, where external systems push changes into JIM.

Issue #545 is the requirements source (it is effectively the PRD); this document is the implementation plan. The decision on delta/change detection was made by the maintainer in [this issue comment](https://github.com/TetronIO/JIM/issues/545#issuecomment-4671318614): v1 ships incremental import via `meta.lastModified` filtering plus `ETag`/`If-None-Match` behind a pluggable change-detection strategy; SCIM Delta Query is deferred until it standardises.

## Business Value

SCIM 2.0 is the dominant standard for cross-domain identity provisioning. One standards-based connector gives JIM connectivity to the whole SCIM ecosystem (Entra ID, Google Workspace, AWS IAM Identity Center, Slack, Salesforce, Atlassian, ServiceNow, Okta, OneLogin, and any custom implementation) without per-product connectors. It complements, not replaces, purpose-built connectors such as #361.

## Technical Architecture

### Current state

- Connectors live in `src/JIM.Connectors/` (`LDAP/`, `File/`, `Mock/`). `ConnectorConstants.Scim2ConnectorName` (`"JIM SCIM2 Connector"`) already exists as a placeholder in `src/JIM.Connectors/Constants.cs`, with no implementation; `ConnectorFactoryTests` asserts it currently throws.
- Central dispatch (#875) is `ConnectorFactory` (`src/JIM.Connectors/ConnectorFactory.cs`); both the Worker and the application layer resolve connectors by `ConnectorDefinition.Name` through it. It also wires `ICredentialProtection` and `ICertificateProvider` into connectors that implement the aware interfaces.
- Built-in connectors become visible to administrators when `SeedingServer` instantiates them and persists a `ConnectorDefinition` (capability flags plus settings); `SyncBuiltInConnectorDefinitionsAsync` reconciles capability and setting changes on every startup, so settings added in later phases flow to existing deployments automatically.
- The Worker drives API-based connectors through `IConnectorImportUsingCalls` (repeated `ImportAsync` calls until no pagination tokens remain, with `PersistedConnectorData` as the cross-run watermark) and `IConnectorExportUsingCalls` (one `ConnectedSystemExportResult` per `PendingExport`, in order).

### Proposed component layout

Mirror the LDAP connector's file decomposition, under `src/JIM.Connectors/SCIM/`:

| Component | Responsibility |
|---|---|
| `ScimConnector` | Entry point. `IConnector`, `IConnectorCapabilities`, `IConnectorSettings`, `IConnectorSchema`, `IConnectorImportUsingCalls`, `IConnectorExportUsingCalls`, `IConnectorCredentialAware`, `IConnectorCertificateAware`. Delegates to the classes below. |
| `ScimConnectorConstants` | Setting names, defaults, well-known schema URNs, endpoint paths. |
| `ScimHttpClient` | Thin wrapper over `HttpClient`: base URL handling, auth handler, TLS enforcement, retry/backoff with `Retry-After` support, rate limiting, JSON (de)serialisation via `System.Text.Json`. No external SCIM SDK (supply-chain decision in #545). |
| `ScimAuthenticationHandler` (strategy per method) | OAuth 2.0 Client Credentials (token acquisition, cached until near expiry, automatic refresh), HTTP Basic, Static Bearer Token, Custom Header. |
| `ScimConnectorSchema` | Queries `/ServiceProviderConfig`, `/Schemas`, `/ResourceTypes`; builds `ConnectorSchema` including vendor extensions; maps SCIM attribute types to `AttributeDataType`. |
| `ScimConnectorImport` | Full and delta import: resource enumeration, pagination (index and cursor), attribute selection, multi-valued/complex attribute flattening, change-detection strategies. |
| `ScimConnectorExport` | Create (POST), update (PATCH preferred, PUT fallback), delete (DELETE), group membership PATCH, bulk operations where advertised. |
| `ScimChangeDetectionStrategy` (+ implementations) | `FullScanOnly` (floor; always available), `LastModifiedFilter` (`filter=meta.lastModified gt "<watermark>"`, watermark in `PersistedConnectorData`), `ETagConditional` (`If-None-Match` where ETags advertised). Selected from `/ServiceProviderConfig` discovery plus an administrator override setting. `DeltaQuery` slots in later as one more strategy without re-architecture. |

### Capability flags

| Capability | Value | Rationale |
|---|---|---|
| `SupportsFullImport` | `true` | Core requirement; also the reconciliation path for exports. |
| `SupportsDeltaImport` | `true` | Via change-detection strategies; `FullScanOnly` providers fall back to full import with a warning, matching the LDAP watermark-fallback precedent. |
| `SupportsExport` | `true` | Core requirement. |
| `SupportsPartitions` / `SupportsPartitionContainers` | `false` | SCIM has no partition concept; resource types are object types, not partitions. |
| `SupportsSecondaryExternalId` | `false` | SCIM `id` is the immutable identifier; `$ref`/`externalId` do not play the LDAP DN role for referencing. |
| `SupportsUserSelectedExternalId` | `false` | RFC 7643 mandates `id` as the service-provider-assigned immutable identifier; `RecommendedExternalIdAttribute` is `id` on every object type. |
| `SupportsUserSelectedAttributeTypes` | `false` | The provider publishes a typed schema; types are not inferred. |
| `SupportsAutoConfirmExport` | `false` | Exports are confirmed by the next import (standard reconciliation). |
| `SupportsParallelExport` | `true` | Stateless HTTP; concurrency bounded by an Export Concurrency setting (conservative default) and the shared rate limiter. |
| `SupportsPaging` | `true` | Page Size on Run Profiles maps to `count` (index paging) or cursor page size. |
| `SupportsFilePaths` | `false` | API-based connector. |

### Schema mapping

SCIM attribute type to `AttributeDataType` (`src/JIM.Models/Core/CoreEnums.cs`):

| SCIM type | JIM type | Notes |
|---|---|---|
| `string` | `Text` | Case sensitivity from `caseExact` is not modelled; document. |
| `boolean` | `Boolean` | |
| `integer` | `LongNumber` | RFC 7643 integers are 64-bit safe this way. |
| `decimal` | `Text` | JIM has no decimal type; preserve lossless as text. Documented limitation. |
| `dateTime` | `DateTime` | ISO 8601 per RFC 7643. |
| `reference` | `Reference` | `$ref`/`value` resolution against imported resources (e.g. group members). |
| `binary` | `Binary` | Base64 per RFC 7643. |
| `complex` | flattened | Sub-attributes flattened with dotted names, e.g. `name.givenName`, `name.familyName`. |

Multi-valued handling:

- Multi-valued simple attributes map to multi-valued JIM attributes directly.
- Multi-valued complex attributes with canonical `type` values (emails, phoneNumbers, addresses, ims, photos) are flattened per canonical type: `emails.work`, `emails.home`, plus `emails.primary` for the `primary=true` entry. This yields deterministic single-valued attributes that Attribute Flows can target, which matters more for sync than preserving the raw list shape.
- `groups` (on User) is read-only on providers; membership is managed via the Group `members` attribute (import as `Reference` multi-valued; export via PATCH on the group).
- **References import as raw values with deferred resolution.** `manager`, `members` and other `reference` attributes are staged as the raw referenced `id` and resolved by JIM's existing unresolved-reference handling during synchronisation, exactly as the SCIM server design resolves inbound references during Attribute Flow. Dangling references then behave identically whichever direction the data arrived from.
- Extension schemas (Enterprise User and vendor URNs discovered via `/Schemas`) contribute attributes prefixed unambiguously (e.g. `urn:...:enterprise:2.0:User:manager` exposed as `enterpriseUser.manager`); exact prefixing finalised in Phase 3 against real provider payloads.

### Settings design

Settings grow phase by phase (startup reconciliation propagates additions). Conditional relevance uses `RequiredWhenSetting`/`RequiredWhenValue` keyed off the Authentication Method drop-down, following the LDAP Certificate Validation precedent.

- **Connectivity (Phase 1):** Base URL (required); Authentication Method drop-down: OAuth 2.0 Client Credentials, HTTP Basic, Static Bearer Token, Custom Header; per-method conditional settings (Token Endpoint URL, Client ID, Client Secret; Username, Password; Bearer Token; Header Name, Header Value; secrets as `StringEncrypted`); OAuth Scope (optional); Certificate Validation (Full/Skip, defaulting Full, using JIM trusted certificates like LDAP); Minimum TLS Version (1.2/1.3, default 1.2); Connection Timeout; Maximum Retries; Retry Delay (ms).
- **Import (Phase 4/5):** Pagination Mode (Auto-detect/Index-based/Cursor-based), Excluded Attributes, Change Detection (Auto-detect/Full Scan Only/Last Modified Filter/ETag Conditional).
- **Export (Phase 6):** Update Method (PATCH preferred/PUT only), Use Bulk Operations, Export Concurrency, Maximum Requests Per Second.

Validation: Phase 1 validates the Base URL shape (absolute URI; HTTPS required except loopback, per the high-trust deployment stance). From Phase 3, `ValidateSettingValues` performs a live connectivity test against `/ServiceProviderConfig`, mirroring the LDAP connectivity test.

### Decisions on the issue's open questions

1. **Provider profiles:** deferred. v1 is generic with safe defaults plus auto-detection (pagination, change detection, PATCH support) from `/ServiceProviderConfig`, which removes most of the need. Profiles can layer on later as pre-filled setting templates without schema changes.
2. **Minimum compliance:** require `/ServiceProviderConfig` and `/Schemas` (or graceful fallback to core User/Group schemas when `/Schemas` is missing but resources respond); everything else (filtering, PATCH, bulk, ETags, sorting) is treated as optional capability discovered at runtime, with `FullScanOnly` and PUT as floors. Deviations are reported as run warnings, never silently absorbed.
3. **Delta in v1:** per the maintainer's issue comment: `meta.lastModified` watermark and ETag strategies now; Delta Query deferred until working-group adoption or a real provider ships it.
4. **Custom OAuth scopes / non-standard token exchange:** the Scope setting covers custom scopes; the Custom Header method plus Static Bearer Token cover providers with non-standard exchanges (operators can source tokens externally). Full custom token-exchange flows are out of scope for v1. A federated/secretless authentication strategy (JWT-bearer / `private_key_jwt` client authentication, the client-side counterpart of the server design's Federated Identity Credential) is expected later; the Phase 2 authentication strategy abstraction must be shaped to admit it without rework.

### Cross-design alignment with the SCIM 2.0 Service Provider (#124)

Decisions from the July 2026 joint review of this plan and [`SCIM_SERVER_DESIGN.md`](../SCIM_SERVER_DESIGN.md):

- **Shared protocol library `JIM.Scim`:** SCIM resource DTOs, serialisation, the PATCH operation model (this connector generates patches; the server applies them), filter/pagination primitives, schema URN constants, the SCIM-to-`AttributeDataType` mapping, and the multi-valued/complex flattening convention live in a new dependency-light class library referencing only `JIM.Models`, consumed by both `JIM.Connectors` and `JIM.Web`. Extraction happens at the start of Phase 2, when the first DTOs appear. `JIM.Utilities` was considered and rejected (grab-bag purpose; a protocol implementation is a cohesive domain deserving its own assembly and audit surface), as was a general `JIM.Protocols` (speculative generality; no concrete sibling exists).
- **One flattening convention, owned by `JIM.Scim`:** canonical-type flattening (`emails.work`, `emails.primary` from the `primary=true` entry) applies on both sides; the server design's first-entry-wins sketch is superseded.
- **JIM-to-JIM SCIM round-trip is an explicit compatibility goal:** this connector pointed at JIM's own SCIM 2.0 Service Provider must achieve paginated full import, `LastModifiedFilter` delta import and `ETagConditional` change detection (see Success Criteria). This also eventually provides a first-party integration-test harness.
- **Metaverse mapping targets:** the [`METAVERSE_SCHEMA_POLICY.md`](../METAVERSE_SCHEMA_POLICY.md) gap attributes (Emails, Account Enabled, etc.) and advisory standard-mapping metadata should land before or alongside Phase 7, so this connector ships with clean flow targets and wizard hints.

## Implementation Phases

### Phase 1: Connector skeleton (this branch, first commit)

- `ScimConnector` implementing `IConnector`, `IConnectorCapabilities`, `IConnectorSettings`; `ScimConnectorConstants`. The credential and certificate aware interfaces join in Phase 2 alongside their first consumer (the HTTP client); implementing them earlier would only add dead state.
- Connectivity settings and Base URL validation as above.
- Register in `ConnectorFactory` (flip the `Create_Scim2ConnectorName_ThrowsNotSupportedException` test to assert the connector is returned).
- **Not seeded into `SeedingServer` yet:** the connector stays invisible to administrators until the enablement phase, so partially-implemented state can never be configured, even if intermediate work merges to `main`.
- Unit tests: capabilities, settings shape (names, types, categories, conditional relevance), Base URL validation, factory dispatch.

### Phase 2: SCIM HTTP client core

- `ScimHttpClient` with auth strategies (OAuth 2.0 Client Credentials with token caching/refresh, Basic, Static Bearer, Custom Header), TLS minimum-version enforcement, certificate validation via `ICertificateProvider` (system CA chain first, then JIM trusted certificates, mirroring `LdapConnector.ValidateServerCertificate`).
- Retry with exponential backoff and jitter for 429/503/504, honouring `Retry-After`; proactive throttling from `RateLimit-*` headers; requests-per-second ceiling; transient vs permanent error classification (modelled on `LdapConnector.ExecuteWithRetry`).
- Unit tests with a mock `HttpMessageHandler` (no network).

### Phase 3: Schema discovery

- `/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas` querying; capability model persisted for import/export decisions; `ConnectorSchema` construction with type mapping and multi-valued/complex flattening; core-schema fallback for providers without `/Schemas`.
- Live connectivity test in `ValidateSettingValues`.

### Phase 4: Full import

- User and group enumeration with pagination (index-based `startIndex`/`count`; cursor-based per RFC 9865; auto-detect), `ConnectedSystemPaginationToken` per resource type, attribute selection via `attributes`/`excludedAttributes`, reference and membership import.

### Phase 5: Delta import (change-detection strategies)

- Strategy selection from discovery plus override setting; `LastModifiedFilter` watermark in `PersistedConnectorData` following the LDAP USN pattern (original value held across all pages; new value written back only after the final page); `ETagConditional`; fallback to full import with a `WarningMessage` when a watermark is unavailable (LDAP delta-fallback precedent).

### Phase 6: Export

- POST/PATCH/PUT/DELETE with per-object `ConnectedSystemExportResult` (system-assigned `id` returned as `ExternalId` on create); group membership PATCH batches; bulk `/Bulk` where advertised, respecting `maxOperations`/`maxPayloadSize`; PATCH-capability degradation to PUT.
- **Dependency ordering is JIM's responsibility** (RFC 7644: the SCIM client creates dependencies first). Referenced objects are exported before their referrers (manager before report, users before group membership patches), leaning on the export pipeline's existing sequencing. A provider 400 `invalidValue` on a missing reference is classified as a dependency-ordering error and handled like the LDAP connector's placeholder-member pattern (recognised, retryable after the dependency lands), never as a silent skip.
- Bulk batches are kept dependency-free (batch ordering enforces dependencies); `bulkId` intra-batch references are a possible later optimisation, not v1.

### Phase 7: Enablement, docs, integration tests

- Seed via `SeedingServer` (and factory-reset path); connector appears in the UI.
- `docs/connectors/jim-scim-connector.md` user documentation; changelog entry (user-facing from this phase only).
- Integration test scenario under `test/integration/` against a containerised SCIM test provider; runtime verification of the full import/sync/export loop.

## Testing Strategy

- TDD throughout (red, green, refactor); NUnit, `MethodName_Scenario_ExpectedResult`, Moq.
- All HTTP behaviour unit-tested through mocked `HttpMessageHandler`; no live endpoints in unit tests.
- New fixtures under `test/JIM.Worker.Tests/Connectors/` (`ScimConnectorTests`, then per-area fixtures per phase), following `LdapConnectorImportDeltaFallbackTests` for the watermark fallback.
- Integration testing deferred to Phase 7 when there is end-to-end behaviour to exercise.

## Success Criteria

- Schema discovery builds a correct `ConnectorSchema` against at least two dissimilar SCIM providers (one index-paged, one cursor-paged).
- **JIM-to-JIM round-trip:** this connector pointed at JIM's own SCIM 2.0 Service Provider (#124, once built) completes paginated full import, `LastModifiedFilter` delta import, `ETagConditional` change detection, and export with confirming import.
- Full import stages users and groups (including membership references) correctly; delta import moves only changed objects and survives restarts via the persisted watermark.
- Export creates, updates (PATCH and PUT), and deletes resources, with every failure surfaced as an RPEI; batch operations log summary statistics.
- Throttling (429) never fails a run outright: retries with backoff, `Retry-After` honoured, throttling reported as warnings.
- Zero build warnings; all unit tests green; integration scenario green.

## Dependencies

None. No new NuGet packages: `System.Net.Http` and `System.Text.Json` are BCL. This is deliberate (supply-chain risk, air-gap posture); the connector works air-gapped against on-premises SCIM providers.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Provider deviation from RFC 7644 (pagination, PATCH subsets, filter gaps) | Capability discovery plus strategy pattern with floors (`FullScanOnly`, PUT); deviations logged as warnings. |
| Eventual consistency after writes | Exports are not auto-confirmed; confirmation via next import, matching JIM's standard reconciliation model. |
| Rate limiting causing slow or failed runs | Backoff with jitter, `Retry-After`, configurable RPS ceiling and conservative concurrency defaults; throttling surfaced as RPEI warnings. |
| Half-built connector visible to administrators | Seeding deferred to Phase 7; factory registration alone does not surface the connector in the UI. |
| Complex attribute flattening losing fidelity (e.g. arbitrary `type` values beyond canonical ones) | Canonical-type flattening plus documented behaviour for non-canonical entries (finalised in Phase 3 against real payloads). |
