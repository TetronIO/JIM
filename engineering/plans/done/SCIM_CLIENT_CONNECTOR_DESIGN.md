# SCIM 2.0 Client Connector Design Document

- **Status:** Done
- **Issue:** [#545](https://github.com/TetronIO/JIM/issues/545)
- **Related Issues:** #124 (SCIM 2.0 Server Support), #361 (Microsoft Graph Connector), #192 (Generic REST Connector), #875 (centralised connector dispatch)
- **Related Plans:** [`SCIM_SERVER_DESIGN.md`](../SCIM_SERVER_DESIGN.md) (the inverse scenario: JIM as SCIM service provider), [`METAVERSE_SCHEMA_POLICY.md`](../METAVERSE_SCHEMA_POLICY.md) (canonical schema policy and SCIM-parity gap attributes)
- **Last Updated:** 2026-08-03

> **Blockers cleared (July 2026).** Both prerequisites have landed on `main`: [#1046](https://github.com/TetronIO/JIM/issues/1046) delivered the `Decimal` attribute data type (so SCIM `decimal` maps to it directly, no interim `Text` mapping), and [#1104](https://github.com/TetronIO/JIM/issues/1104) delivered the SCIM-parity gap attributes and advisory Standard Mappings, giving this connector clean Attribute Flow targets. Its Phase 3 (consuming those mappings for Attribute Flow editor hints and connector wizard default-flow suggestions) is still outstanding, so Phase 7 below should not assume wizard hints already exist.

## Overview

Add a built-in SCIM 2.0 client connector that lets JIM import, synchronise, and export users and groups to and from any system exposing a SCIM 2.0 service provider interface (RFC 7643/7644). JIM acts as the **SCIM client**: it initiates connections to discover schemas, import resources, and export provisioning changes. This is the inverse of #124, where external systems push changes into JIM.

Issue #545 is the requirements source (it is effectively the PRD); this document is the implementation plan. The decision on delta/change detection was made by the maintainer in [this issue comment](https://github.com/TetronIO/JIM/issues/545#issuecomment-4671318614): v1 ships incremental import via `meta.lastModified` filtering plus `ETag`/`If-None-Match` behind a pluggable change-detection strategy; SCIM Delta Query is deferred until it standardises.

## Business Value

SCIM 2.0 is the dominant standard for cross-domain identity provisioning. One standards-based connector gives JIM connectivity to the whole SCIM ecosystem (Entra ID, Google Workspace, AWS IAM Identity Center, Slack, Salesforce, Atlassian, ServiceNow, Okta, OneLogin, and any custom implementation) without per-product connectors. It complements, not replaces, purpose-built connectors such as #361.

## Technical Architecture

### Current state

- Connectors live in `src/JIM.Connectors/` (`LDAP/`, `File/`, `Mock/`, and `SCIM/` as of Phase 1). The original `ConnectorConstants.Scim2ConnectorName` placeholder has been replaced by the confirmed naming pair `ScimClientConnectorName` / `ScimServiceProviderConnectorName` in `src/JIM.Connectors/Constants.cs`; `ConnectorFactory` now returns `ScimConnector` for the former, while the latter still throws pending #124.
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
| `decimal` | `Decimal` | Parse and emit exclusively via `DecimalAttributeValue` (`src/JIM.Utilities`): invariant culture, never routed through `double`/`float`, exponent notation accepted on parse but never emitted. Values outside .NET `decimal` range fail that object's import with an RPEI error rather than rounding. |
| `dateTime` | `DateTime` | ISO 8601 per RFC 7643. |
| `reference` | `Reference` | `$ref`/`value` resolution against imported resources (e.g. group members). |
| `binary` | `Binary` | Base64 per RFC 7643. |
| `complex` | flattened | Sub-attributes flattened with dotted names, e.g. `name.givenName`, `name.familyName`. |

Multi-valued handling:

- Multi-valued simple attributes map to multi-valued JIM attributes directly.
- Multi-valued complex attributes with canonical `type` values (emails, phoneNumbers, addresses, ims, photos) are flattened per canonical type: `emails.work`, `emails.home`, plus `emails.primary` for the `primary=true` entry. This yields deterministic single-valued attributes that Attribute Flows can target, which matters more for sync than preserving the raw list shape.
- `groups` (on User) is read-only on providers; membership is managed via the Group `members` attribute (import as `Reference` multi-valued; export via PATCH on the group).
- **References import as raw values with deferred resolution.** `manager`, `members` and other `reference` attributes are staged as the raw referenced `id` and resolved by JIM's existing unresolved-reference handling during synchronisation, exactly as the SCIM server design resolves inbound references during Attribute Flow. Dangling references then behave identically whichever direction the data arrived from.
- Extension schemas (Enterprise User and vendor URNs discovered via `/Schemas`) contribute attributes prefixed unambiguously (e.g. `urn:...:enterprise:2.0:User:manager` exposed as `enterpriseUser.manager`). Settled in Phase 3: the prefix is the extension schema's `name` with a lower-case first letter, falling back to the final URN segment when the schema is unnamed, and a second extension deriving an already-used prefix is addressed by its full URN instead.

### Settings design

Settings grow phase by phase (startup reconciliation propagates additions). Conditional relevance uses `RequiredWhenSetting`/`RequiredWhenValue` keyed off the Authentication Method drop-down, following the LDAP Certificate Validation precedent.

- **Connectivity (Phase 1):** Base URL (required); Authentication Method drop-down: OAuth 2.0 Client Credentials, HTTP Basic, Static Bearer Token, Custom Header; per-method conditional settings (Token Endpoint URL, Client ID, Client Secret; Username, Password; Bearer Token; Header Name, Header Value; secrets as `StringEncrypted`); OAuth Scope (optional); Certificate Validation (Full/Skip, defaulting Full, using JIM trusted certificates like LDAP); Minimum TLS Version (1.2/1.3, default 1.2); Connection Timeout; Maximum Retries; Retry Delay (ms).
- **Import (Phase 4/5):** Pagination Mode (Auto-detect/Index-based/Cursor-based), Excluded Attributes, Change Detection (Auto-detect/Full Scan Only/Last Modified Filter/ETag Conditional).
- **Export (Phase 6):** Use Bulk Operations (off by default). Three settings in the original sketch were dropped rather than deferred: Update Method, because the connector reads whether the provider supports PATCH and degrades accordingly, so asking an administrator to state it again would only let them get it wrong; and Export Concurrency and Maximum Requests Per Second, because the provider's own `RateLimit-*` headers and `Retry-After` say what it will accept, and a fixed number an administrator guessed cannot beat what the provider is telling JIM every response.

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
- **JIM-to-JIM SCIM round-trip is an explicit compatibility goal:** this connector pointed at JIM's own SCIM 2.0 Service Provider must achieve paginated full import and `LastModifiedFilter` delta import (see Success Criteria). Conditional change detection was originally listed here too; Phase 5 established that ETags cannot serve import and moved them to export, where `If-Match` guards a lost update. This also eventually provides a first-party integration-test harness.
- **Metaverse mapping targets:** the [`METAVERSE_SCHEMA_POLICY.md`](../METAVERSE_SCHEMA_POLICY.md) gap attributes (Emails, Account Enabled, etc.) and advisory standard-mapping metadata should land before or alongside Phase 7, so this connector ships with clean flow targets and wizard hints.
- **Naming (confirmed):** the pair is named by JIM's role in the exchange, per RFC 7644 terms: this connector is **"JIM SCIM 2.0 Client Connector"**, the inbound server's pseudo-connector is **"JIM SCIM 2.0 Service Provider Connector"**. Both constants are registered in `ConnectorConstants`; descriptions carry the direction ("JIM acts as the SCIM client, connecting out to the service provider").

## Implementation Phases

### Phase 1: Connector skeleton (this branch, first commit)

- `ScimConnector` implementing `IConnector`, `IConnectorCapabilities`, `IConnectorSettings`; `ScimConnectorConstants`. The credential and certificate aware interfaces join in Phase 2 alongside their first consumer (the HTTP client); implementing them earlier would only add dead state.
- Connectivity settings and Base URL validation as above.
- Register in `ConnectorFactory` (flip the `Create_Scim2ConnectorName_ThrowsNotSupportedException` test to assert the connector is returned).
- **Not seeded into `SeedingServer` yet:** the connector stays invisible to administrators until the enablement phase, so partially-implemented state can never be configured, even if intermediate work merges to `main`.
- Unit tests: capabilities, settings shape (names, types, categories, conditional relevance), Base URL validation, factory dispatch.

### Phase 2: SCIM HTTP client core ✅

- `ScimHttpClient` with auth strategies (OAuth 2.0 Client Credentials with token caching/refresh, Basic, Static Bearer, Custom Header), TLS minimum-version enforcement, certificate validation via `ICertificateProvider` (system CA chain first, then JIM trusted certificates).
- Retry with exponential backoff and jitter for transient statuses, honouring `Retry-After`; proactive throttling from `RateLimit-*` headers; transient vs permanent error classification (modelled on `LdapConnector.ExecuteWithRetry`).
- Unit tests with a stub `HttpMessageHandler` (no network).

Delivered as `JIM.Scim` (shared protocol library: URNs, endpoint paths, error model, JSON options) plus, in `JIM.Connectors/SCIM/`, `ScimRetryPolicy`, `ScimThrottleHints`, `ScimCertificateValidator`, `ScimHttpClient`, `ScimHttpClientFactory` and the four strategies under `Authentication/`. `ScimConnector` now implements `IConnectorCredentialAware` and `IConnectorCertificateAware`.

Deviations from the sketch above, both deliberate:

- **A refused certificate is shown, not just reported** (added July 2026, following #1142's LDAP work). `HttpClient` reports a rejected certificate as "The SSL connection could not be established", which an administrator cannot tell from a firewall, so a failed connectivity test now examines what the provider presented via the shared `ServerCertificateProbe` and throws `ServerCertificateRejectedException`. The portal's settings tab, the failed Activity and the Activity's REST `errorDetail` already render that, so nothing downstream needed changing; the probe's remediation wording was parameterised so it names the SCIM service provider and HTTPS rather than a directory and LDAPS.
- **The Certificate Validation setting stays, unlike LDAP's** (maintainer decision, July 2026). #1142 removed it there because the underlying switch is process-wide and could never be honoured for one Connected System; `HttpClientHandler` validates per connection, so for SCIM it genuinely works and there are scenarios that want it. The setting's description now leads with the store, because trusting the presented certificate is a decision made at a point in time about one certificate: if the provider later presents a different one, JIM refuses it and says so, which turning validation off hides for ever. Skipping validation also suppresses the certificate diagnosis, since a failure then is not about trust.
- **Certificate validation is stricter than `LdapConnector.ValidateServerCertificate`.** JIM's trusted certificate store waives an unknown certificate authority only; expiry and hostname mismatches are never waived. The LDAP implementation inspects chain elements without checking whether the chain otherwise built, so it would accept both.
- **No requests-per-second ceiling setting.** Reactive `Retry-After` handling plus proactive `RateLimit-*` pausing covers what providers actually advertise; a fixed local ceiling would be guesswork an administrator cannot tune usefully. Revisit if a real provider needs it.

### Phase 3: Schema discovery ✅

- `/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas` querying; capability model for import/export decisions; `ConnectorSchema` construction with type mapping and multi-valued/complex flattening; core-schema fallback for providers without `/Schemas`.
- Live connectivity test in `ValidateSettingValues`.

Delivered in `JIM.Scim` as the discovery documents (`Discovery/`), `ScimProviderCapabilities`, `ScimAttributeMapper`, `ScimCommonAttributes` and `ScimCoreSchemas` (`Schema/`), plus `ScimConnectorSchema` and `ScimDiscoveryResult` in `JIM.Connectors/SCIM/`. `ScimConnector` now implements `IConnectorSchema`.

Decisions taken here, all recorded because they close open questions the sketch left:

- **Every discovery document is treated as optional, but a missing document is never conflated with a broken provider.** Only 404 and 501 read as "not published"; anything else propagates. Absorbing a 500 or a 403 would persist an empty schema over a good one and silently unmap every Attribute Flow pointing at it.
- **Flattening is decided by schema structure, not by a list of known attribute names.** A complex attribute carrying a `$ref` sub-attribute is a reference and stays whole (`manager`, `members`, `groups`), keeping its plurality; without that rule, canonical-type flattening would have turned Group `members` into single-valued `members.User` slots holding one member each. A multi-valued complex attribute with canonical `type` values is cut per canonical value: into one slot where the entry has a `value` sub-attribute (`emails.work`), or one slot per sub-attribute where it does not (`addresses.work.streetAddress`, since an address has no single value). Everything else complex flattens per sub-attribute.
- **Canonical slots are single-valued, as the sketch intended.** A provider may legitimately hold two entries of the same canonical type; Phase 4 must report that as a per-object warning rather than silently importing one of them. The `display` sub-attribute is not surfaced for canonical slots, being a provider-rendered duplicate of `value`.
- **Capabilities are re-discovered at the start of each run rather than persisted** (deviation from "capability model persisted" above). One `/ServiceProviderConfig` GET against a provider about to receive thousands of calls is not worth a class of staleness bugs after a provider upgrade changes what it supports. `ScimDiscoveryResult` carries them for the run; Phase 4 decides whether a snapshot is also worth keeping on the Connected System for display.
- **Extension attributes are prefixed from the extension schema's name** (`enterpriseUser.department`), while the SCIM path stays URN-qualified. Two extensions deriving the same prefix are disambiguated by addressing the second by its full URN, so one vendor's attributes cannot mask another's.
- **Discovery warnings are surfaced, not just logged** (implemented with Phase 7, which owns the setup flow): they travel on `ConnectorSchema.Warnings` into the schema refresh result, the portal's schema screen shows them beside what changed, and the ImportSchema Activity completes with a warning carrying them so the REST API and PowerShell see the same outcome.
- **The connectivity test accepts any one discovery endpoint answering.** All three reporting "not published" is the signature of a Base URL that is not a SCIM service provider. It runs inside `Task.Run` because setting validation is invoked from Blazor Server circuits, which have a synchronisation context that a direct blocking wait would deadlock.

### Phase 4: Full import ✅

- User and group enumeration with pagination (index-based `startIndex`/`count`; cursor-based per RFC 9865; auto-detect), pagination token round trip, attribute selection, reference and membership import. Enumerate the endpoints `ScimDiscoveryResult.ResourceTypes` reports rather than assuming `/Users` and `/Groups`.
- **Two entries sharing a canonical type must be reported, never silently dropped.** Phase 3 flattens `emails` into single-valued `emails.work`-style slots, so a provider holding two work addresses has more data than the slot can take. Import the first and raise a warning naming the attribute and the canonical type; silently importing one of them would present a partial value as a complete one.

Delivered as `ScimResourceReader` (`JIM.Scim/Schema/`, the inverse of the flattening) plus `ScimConnectorImport`, `ScimImportPosition`, `ScimQueryBuilder` and `ScimPaginationMode` in `JIM.Connectors/SCIM/`. `ScimConnector` implements `IConnectorImportUsingCalls`; two settings joined: Pagination Mode and Excluded Attributes.

Decisions taken here:

- **Flattened attributes carry a structural accessor, not just a SCIM path string.** The reader has to find exactly what the mapper published, because an attribute the mapper publishes but the reader cannot find is an Attribute Flow target that silently never receives a value. Each attribute therefore records where its value lives (source attribute, sub-attribute, canonical type, extension URN), so reading is a lookup rather than a re-parse. The reader's tests read through the real core schemas for the same reason.
- **Paging follows what the provider does, not what was asked for.** Index paging advances by the number of resources actually returned, because a provider capping the page size below the requested `count` would otherwise make every later page skip resources. A volunteered `nextCursor` switches the walk to cursors mid-import. Under cursor paging the absence of a cursor ends the walk, since treating a full page as "more to come" loops for ever against a provider that returns a full final page.
  - **Corrected during the Phase 5 test-harness work, and the correction then corrected again.** Advancing by what was returned was only half the rule: the walk also *ended* on a short page, so against a provider capping the page size below the requested `count`, every page was short and the import stopped after the first one, reporting success having read a fraction of the system. The first fix ended the walk on `totalResults` instead, which the mock then showed was a second silent truncation waiting to happen: reporting the page's size as `totalResults` rather than the collection's is an easy provider mistake, and one this connector's own test helper made. **Index paging now continues unless the provider has demonstrably run out**: an empty page, or a page both shorter than requested *and* past the provider's stated total. Requiring two independent signals means neither a capped page size nor a misreported total can end a walk early. A provider that ignores `startIndex` entirely would make that rule loop for ever, so a page ceiling per resource type fails the run instead.
- **One pagination token carries the whole position**, including which resource type is being read, rather than one token per resource type (a simplification of the sketch above). The connector walks the types in order, and JIM's "no tokens means finished" contract then falls out naturally. An unreadable token throws rather than starting over: a silent restart partway through would look like a successful run that imported a fraction of the data.
- **Only `excludedAttributes` is sent, never the mutually exclusive `attributes` parameter.** Naming an inclusive set risks a provider returning nothing else, and attributes an administrator has not selected yet still need to be importable the moment they do. The `attributes` parameter can be revisited if a real provider makes the payload size a problem.
- **A resource JIM cannot read faithfully is staged carrying its error, not skipped.** A skipped object is absent from the run, which deletion detection would read as a deletion. This is why a decimal outside JIM's range fails its object rather than being rounded or dropped.
- **Discovery runs once per run, on the first page**, held on the connector instance for the run's lifetime (JIM opens the connection once and then asks for pages). Nothing about the provider is persisted between runs, so a provider that gains or loses a capability is followed on the next run.

### Phase 5: Delta import (change-detection strategies) ✅

- Strategy selection from discovery plus override setting; `LastModifiedFilter` watermark in `PersistedConnectorData` following the LDAP USN pattern (original value held across all pages; new value written back only after the final page); `ETagConditional`; fallback to full import with a `WarningMessage` when a watermark is unavailable (LDAP delta-fallback precedent).
- **Watermark boundary (must be settled here).** Providers expose `meta.lastModified` at one-second precision, so a watermark stored as the highest value seen and queried next run with `gt` silently misses any object modified during that same second but not yet read. `ge` is not a safe mitigation: it is exclusive on at least one real provider (see the probe in [`../../notes/SCIM_TEST_PROVIDER_ANALYSIS.md`](../../notes/SCIM_TEST_PROVIDER_ANALYSIS.md)). Since re-importing an unchanged object is idempotent while missing a change is silent divergence, store the watermark with a small safety margin behind the highest observed value and accept deliberate overlap.

Delivered as `ScimImportPlan`, `ScimImportState`, `ScimWatermarkTracker`, `ScimDeltaStrategy` and `ScimResponse<T>` in `JIM.Connectors/SCIM/`, plus `ScimResourceReader.TryReadLastModified` in `JIM.Scim`. One setting joined: Change Detection.

Decisions taken here:

- **The watermark comes from the provider's clock, not from the data** (a correction to the sketch above, which said "highest observed value"). The newest `meta.lastModified` in a system that has stopped changing never advances, so a data-derived watermark leaves every later Delta Import re-reading everything for ever: the strategy silently degenerates into the full scan it exists to avoid. The `Date` response header on the first page of a run is the only reading of the provider's own clock the protocol guarantees, and it is what the watermark is taken from. The highest observed `meta.lastModified` is kept solely as the fallback for a provider that sends no `Date` header, where the stall is preferable to having no delta at all.
- **The safety margin is one minute, not one second.** A second would cover only the precision problem the sketch identified. Taking the watermark from the `Date` header introduces a second, larger hazard: the clock serving that header (often a gateway) need not be the clock stamping resource metadata, and a header running ahead would put the watermark in the metadata clock's future and lose changes silently. A minute absorbs any realistic disagreement between two NTP-synchronised clocks while costing only the resources changed in the last minute, re-read idempotently. It is a constant rather than a setting; make it one only if a real provider needs it.
- **The watermark is recorded on the last page of a run, not the first** (a deliberate departure from the LDAP pattern, which captures the rootDSE position on page one). `SyncImportTaskProcessor` keeps the first non-null `PersistedConnectorData` any page returns and saves it after the loop, so returning it last works unchanged, and it makes an abandoned run safe: a run that fails or is cancelled partway through never reaches the final page, so the watermark stays where the last completed import left it and the resources that run never got to are read again next time. Capturing on page one would advance the watermark past objects the run never read.
- **`ETagConditional` is not implementable as an import strategy and has been dropped, not deferred.** RFC 7644 section 3.14 offers `If-None-Match` on a GET of a *single* resource; there is no conditional list query. Using it for import therefore requires the connector to already know every object's `id` and its previously stored ETag, and `IConnectorImportUsingCalls.ImportAsync` gives a connector the Connected System, the Run Profile, the pagination tokens and one persisted-data string: it can see no Connected System Objects. Holding an id-to-ETag map in the persisted data instead would mean a multi-megabyte JSON blob rewritten every run *and* one GET per object, which is strictly worse than the full scan it would replace. ETag remains valuable in Phase 6, where `If-Match` on PUT and PATCH is both implementable and worth having; the JIM-to-JIM success criterion is amended accordingly.
- **Deletions are not detected by a Delta Import**, and this is a protocol limitation rather than a gap to close. SCIM publishes no change feed, so a deleted resource simply stops being returned, and only a Full Import's external-id reconciliation can see that. `SyncImportTaskProcessor` already confines deletion detection to `FullImport` run types, so a filtered delta cannot mistake "not returned" for "deleted"; the Change Detection setting's description says so, and the connector documentation must repeat it in Phase 7.
- **A Delta Import that cannot filter reads everything and warns rather than failing.** This follows the LDAP accesslog precedent over the LDAP USN one: only a completed import can record a watermark, so failing the first Delta Import after a Connected System is configured would leave it permanently unable to import until someone noticed. A full scan forced by the setting is not warned about, being a deliberate configuration rather than a shortfall.
- **`id` and `meta` are never excluded, whatever Excluded Attributes says.** Excluding either breaks importing in a way that presents as a provider fault: without `id` there is nothing to anchor a Connected System Object on, and without `meta` the watermark has no fallback source. Ignoring the administrator here is better than obeying them into a silent failure.
- **A provider that rejects the filter it advertised is fallen back on, not failed.** Advertising a capability and then answering 400 `invalidFilter` is common enough that failing the run would make the connector unusable against those providers. The first page retries unfiltered, the run continues as a full scan, and the warning names the setting to change. Deliberately narrow: a 400 carrying another SCIM error type propagates, and `tooMany` in particular means the filter matched too much, which reading everything would only make worse.

### Phase 6: Export ✅

- POST/PATCH/PUT/DELETE with per-object `ConnectedSystemExportResult` (system-assigned `id` returned as `ExternalId` on create); group membership PATCH batches; bulk `/Bulk` where advertised, respecting `maxOperations`/`maxPayloadSize`; PATCH-capability degradation to PUT.
- **ETags belong here, not in import** (see Phase 5). Where the provider advertises ETag support and JIM holds the resource's `meta.version` from its last import, send `If-Match` on PUT and PATCH so an export cannot silently overwrite a change made in the provider since JIM last read it; a 412 is classified as a concurrency conflict and reported, not retried blindly.
- **Dependency ordering is JIM's responsibility** (RFC 7644: the SCIM client creates dependencies first). Referenced objects are exported before their referrers (manager before report, users before group membership patches), leaning on the export pipeline's existing sequencing. A provider 400 `invalidValue` on a missing reference is classified as a dependency-ordering error and handled like the LDAP connector's placeholder-member pattern (recognised, retryable after the dependency lands), never as a silent skip.
- Bulk batches are kept dependency-free (batch ordering enforces dependencies); `bulkId` intra-batch references are a possible later optimisation, not v1.
- **`/Bulk` was deferred and has now landed** (August 2026), delivered as `ScimBulkExporter` plus the bulk message model in `JIM.Scim`, behind an opt-in **Use Bulk Operations** setting. The deferral reasoning held: nothing depended on it, and it needed no rework of what was already here beyond splitting composition from dispatch (`ScimExportOperation` / `ScimPreparedExport`), which both paths now share so a change is shaped identically whichever way it travels. Decisions taken:
    - **Opt-in, off by default.** Per-object export is already complete and correct, so bulk is purely a throughput choice. What it costs is that the provider, not JIM, reports each outcome, and a provider reporting them inaccurately would have JIM record changes as applied that were not: drift with no visible cause. There is no safe automatic retreat once a batch has partly applied, so the trade is the administrator's to make against their own provider.
    - **Outcomes are correlated, never counted off.** Nothing in RFC 7644 promises the response lists operations in request order, so results are matched by `bulkId` (built from the Pending Export's index), falling back to the operation's location for a provider that echoes no `bulkId`, which is conformant for anything but a POST. Pairing by position would attribute a rejection to the wrong object.
    - **An unreported operation is failed, not assumed applied.** A provider that stops early says nothing about what it never reached, and JIM's export pipeline reads a missing result as success, so the connector must never return a short list. This is the integrity rule the whole feature turns on.
    - **`failOnErrors` is deliberately not sent**, which RFC 7644 section 3.7.1 defines as "process everything regardless". Setting a threshold would make the number of changes applied depend on where in the batch a bad object happened to sit; the per-object path it replaces abandons nothing.
    - **The two whole-request failure modes are told apart.** A 404, 501 or 405 from `/Bulk` is answered before the provider looks at the operations, so nothing applied and resending them individually is safe: the run falls back for the rest of its life and warns. Any other failure leaves what applied unknowable, so those changes are reported failed and left pending rather than resent, because resending a create that did apply would duplicate the resource. A 413 is the third case: nothing applied, so the batch is halved and retried.
    - **Batches respect both advertised limits**, measured rather than estimated (the envelope size is whatever an operationless request serialises to). An operation too large for any batch is sent on its own, since the limit is the bulk endpoint's rather than the provider's. Where bulk is advertised with no stated maximum, JIM batches 100.
    - **Error classification is shared** (`ScimExportErrorClassifier`). The same rejection arrives as an HTTP response one way and a status inside an operation result the other; classifying them separately would make the same provider behaviour retryable one way and not the other.
    - **`MockScimProvider` replays each bulk operation through its ordinary resource handlers**, so entity tags, missing resources and dependency rejections behave identically inside a batch and outside one, and gains switches for the deviations that matter: an omitted `bulkId`, a truncated response, a reversed response, and an unimplemented endpoint.

Delivered as `ScimConnectorExport` in `JIM.Connectors/SCIM/`, plus `ScimResourceWriter`, `ScimPatchBuilder`, `ScimValueFormatter` and the PATCH message model in `JIM.Scim`. `ScimConnector` implements `IConnectorExportUsingCalls`. Two `ConnectedSystemExportErrorType` values joined: `MissingDependency` and `ConcurrencyConflict`. Bulk adds `ScimBulkExporter`, `ScimBulkEndpointState`, `ScimExportOperation`, `ScimPreparedExport`, `ScimBulkExportOperation` and `ScimExportErrorClassifier`, with `ScimBulkRequest`/`ScimBulkOperation`/`ScimBulkResponse`/`ScimBulkOperationResult` in `JIM.Scim`.

Decisions taken here:

- **PATCH degrades to read-modify-write, never to a bare PUT** (a correction to the sketch above, which said "PATCH-capability degradation to PUT"). A PUT asserts the entire resource, so one built from JIM's changes alone would clear every attribute the provider holds that JIM does not manage: the act of setting a job title would empty the rest of the record. The fallback therefore reads the resource, lays the changes onto it (`ScimResourceWriter.ApplyChanges`), and writes the whole thing back under the entity tag from the read.
- **The mapper, the reader and the writer have to agree exactly**, so the writer's tests go through the same real RFC 7643 core schemas the reader's do rather than hand-built accessors. A value written where the reader would not look is a change JIM records as exported and the next confirming import reports as never applied.
- **Entity tags guard writes, not reads.** `If-Match` is sent on PATCH from the `meta.version` a previous import brought back, and on PUT from the tag of the read that preceded it, but only where the provider advertises ETag support: one that does not maintain them would either ignore the header or reject every write carrying it. A 412 is classified as `ConcurrencyConflict` and reported rather than retried, because retrying blindly just races again; the next import reconciles what actually changed.
- **An attribute the provider's schema does not have fails the whole object.** Exporting the rest would record the change as applied when part of it never left JIM. The same applies to an attribute the schema marks read-only in a change: JIM knows it, but this provider will not take it.
- **A delete of a resource already gone succeeds.** The intended end state is that the resource is absent, and it is; failing would leave the Pending Export retrying for ever against a provider that has already done what was asked.
- **Add and remove are kept distinct on multi-valued attributes.** Collapsing add into replace would turn every group membership addition into a membership replacement, silently removing everyone already there, and removing the attribute rather than the value would take every member with it (`members[value eq "x"]`).

### Phase 7: Enablement, docs, integration tests ✅

- Seed via `SeedingServer` (and factory-reset path); connector appears in the UI.
- `docs/connectors/jim-scim-connector.md` user documentation; changelog entry (user-facing from this phase only).
- Integration test scenario under `test/integration/` against a containerised SCIM test provider; runtime verification of the full import/sync/export loop.

**Runtime verification (2026-07-30).** The connector was driven end to end against a live HTTP service provider. JIM is air-gap deployable and carries no third-party service dependency, so its test provider is written here rather than pulled as a container image.

**The test provider is now one implementation, containerised (2026-08-02).** It began as `test/integration/scim/Start-ScimTestProvider.ps1`, an `HttpListener` script serving discovery and two read-only collections. That could not serve the integration scenario: in the integration stack the connector runs in a container and reaches the provider by hostname, JIM refuses cleartext HTTP to anything but a loopback address, and .NET's `HttpListener` cannot bind a certificate on Linux (it accepts an `https://` prefix and then fails the handshake, which was verified rather than assumed). Fronting it with a TLS terminator was considered and rejected: it would have left two provider implementations, the PowerShell script modelling a well-behaved provider and the unit suite's `MockScimProvider` modelling a dozen misbehaviours, and an integration run passing against the more forgiving of the two is false comfort.

Instead `MockScimProvider` moved out of `JIM.Worker.Tests` into `test/JIM.TestScimServiceProvider`, an ASP.NET Core project that serves it over HTTPS from a self-signed certificate generated at every start (Kestrel does TLS on Linux natively, so one container and no proxy). The unit suite references the same project and drives the provider in process behind a stubbed message handler; the container runs its `Program`. One implementation, so an integration run cannot pass against a weaker provider than the unit tests use, and the scenario inherits every misbehaviour switch. The certificate is written to a volume for the scenario to add to JIM's Trusted Certificates, so the run also exercises the trust path from #1139 rather than skipping validation. The PowerShell provider is retired.

**Integration scenario green end to end (2026-08-02).** `Setup-Scenario15.ps1` / `Invoke-Scenario15-ScimConnector.ps1`, verified against the native stack: certificate trusted with Full Validation, schema discovery, a paged Full Import (25 users, pages of 10) with group membership staged as references, SCIM Users joining HR-projected Metaverse Objects, an HR change producing 25 Pending Exports, a bulk export (three `/Bulk` requests at the provider's advertised maximum of 10 operations, proving batch splitting over a real socket), a confirming import that proved the exact values landed and released the held Pending Exports, and a Delta Import. Two shape lessons paid for along the way, recorded so nobody re-learns them:

- **The scenario needs two Connected Systems.** The first draft had SCIM as both the source of the Metaverse values and the export target, which produced zero Pending Exports, correctly: Q3 circular-sync prevention (`ExportEvaluationServer` excludes the source system; per-attribute in `DriftDetectionService`) exists precisely to stop that loop. An HR CSV is now authoritative and SCIM Users join rather than project, which is also the shape every real deployment has.
- **Export evaluation is driven by Metaverse Object changes, and drift by inbound target-system changes.** A freshly joined, unchanged target with an already-settled Metaverse triggers neither, so the scenario does what a customer does: HR changes after the join (every Display Name gains a suffix), and the change propagates. A join is not an update; asserting exports out of one was the test's misunderstanding, not JIM's.

The provider also had to genuinely apply simple-path PATCH operations rather than acknowledge them (its unit-suite design), because the confirming import reads the provider back; an acknowledge-only provider makes every exported change unconfirmable, and JIM correctly marked all 25 as ExportNotConfirmed for re-assertion, which is the integrity behaviour doing its job against a lying provider. Filtered paths (`members[value eq "x"]`) remain acknowledged-only and documented as such.

**Runner wiring done (2026-08-02).** `Run-IntegrationTests.ps1` starts the provider with the `scim` compose profile (always `--build`, so a stale provider image cannot mask connector changes), waits on the certificate file as the readiness signal, and tears the profile down with the rest. Scenario 15 registers as template-irrelevant, and the two `"*Scenario1*"` wildcard guards that would substring-match "Scenario15" carry explicit exclusions, as Scenario 14 already did. `Invoke-Scenario15` self-runs its setup on a full run (the Scenario 12 pattern), copying the certificate out of the container when no path is supplied; the sandbox light stack passes its native provider's address and certificate instead, which is how the whole flow was verified green end to end. The runner's own containerised pass landed on 2026-08-03: the full `Run-IntegrationTests.ps1` flow (provider image build, certificate copied out of the container, all nine scenario steps including provisioning and scope-exit deprovisioning) green end to end against the containerised stack. Two fixes fell out of it: the setup script uploads the provider certificate's bytes rather than passing a host path for jim.web to read (the path does not exist inside the container; the native light stack had masked this), and the runner's image build now skips the `openapi-gen` Dockerfile stage as the `jim-build` aliases already did, since no scenario reads the generated document.

Verified through the running stack: the connector definition seeds with all its settings and the SCIM vocabulary; saving settings runs the live connectivity test; schema import discovers the resource types, falls back to the RFC 7643 core schemas where the provider serves no `/Schemas`, and persists the flattened attributes (`emails.work`, `emails.primary`, `name.givenName`); a Full Import walks 25 users across three pages plus a group, terminating without an extra request, and stages membership as reference values.

That run found one defect no unit test had: the connector staged **every** attribute it could read, so attributes an administrator had deselected were imported and stored anyway. Reading everything is deliberate (naming an inclusive set risks a provider returning nothing else), but staging everything is not, and it diverged from the LDAP connector, which requests only selected attributes. Staging is now filtered to the selected attributes, falling back to everything where the Object Type carries no attributes yet (nothing has been selected or deselected before a schema import).

## Testing Strategy

- TDD throughout (red, green, refactor); NUnit, `MethodName_Scenario_ExpectedResult`, Moq.
- All HTTP behaviour unit-tested through mocked `HttpMessageHandler`; no live endpoints in unit tests.
- **A mock service provider with state** (`test/JIM.Worker.Tests/Connectors/MockScim/`, delivered alongside Phase 5). Scripted responses cover a provider that answers; they cannot cover a provider that *behaves*, and the failure modes that matter most are the ones no real provider will reproduce on demand: a cursor expiring mid-walk, a provider advertising filtering and then rejecting it, a change landing in the same second a run started reading, a gateway clock running ahead of the clock stamping resource metadata, a page size capped below what was asked for. Each is one switch on `MockScimProviderOptions`, whose defaults describe a conformant provider so a test only states what it deviates from. Also covered: lower-case ListResponse member names (conformant per RFC 7643 section 2.1, and a client matching by exact case would read every page as empty), a 500 part way through a walk, the same resource returned on two pages, `totalResults` omitted or misreported, and a bare JSON array in place of the envelope. The common thread is that each of these fails *quietly* if handled wrongly: an import that reads a fraction of the system and reports success is worse than one that fails. The harness earned its place immediately by exposing the index-paging termination defect recorded under Phase 4, then exposing the flaw in its first fix.
- New fixtures under `test/JIM.Worker.Tests/Connectors/` (`ScimConnectorTests`, then per-area fixtures per phase), following `LdapConnectorImportDeltaFallbackTests` for the watermark fallback.
- Integration testing deferred to Phase 7 when there is end-to-end behaviour to exercise.

## Success Criteria

- Schema discovery builds a correct `ConnectorSchema` against at least two dissimilar SCIM providers (one index-paged, one cursor-paged).
- **JIM-to-JIM round-trip:** this connector pointed at JIM's own SCIM 2.0 Service Provider (#124, once built) completes paginated full import, `LastModifiedFilter` delta import, and export with confirming import, with `If-Match` honoured on updates where both ends advertise ETag support.
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
