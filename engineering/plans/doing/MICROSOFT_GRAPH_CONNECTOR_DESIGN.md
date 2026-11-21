# Microsoft Graph Connector (Entra ID) Design Document

- **Status:** Doing (design agreed; Phases 1 to 3 complete, Phase 4 next)
- **Issue:** [#361](https://github.com/TetronIO/JIM/issues/361) (the issue is the requirements source, in effect the PRD; this document is the implementation plan)
- **Related Issues:** #545 (SCIM 2.0 Client Connector, whose HTTP stack this reuses), #1145 (Source of Authority conversion as an export operation), #360 (pronouns), #192 (generic REST connector), #2367 (Entra ID sign-in, which gave JIM its Entra ID test tenant)
- **Related Plans:** [`SCIM_CLIENT_CONNECTOR_DESIGN.md`](../done/SCIM_CLIENT_CONNECTOR_DESIGN.md), the closest precedent: an HTTP connector built in phases with seeding held back until the last one
- **Last Updated:** 2026-11-21

## Overview

A built-in connector that imports, synchronises and exports Users and Groups in Microsoft Entra ID through the Microsoft Graph REST API. JIM authenticates as an application (app-only), reads with full and delta queries, and writes with paced, batched requests that stay well inside the tenant's Graph budget. It is hybrid-aware: every object carries a synthesised `sourceOfAuthority`, and the connector will not write to an object whose authority is on-premises unless told to.

This is the first JIM connector that needs the internet by design. Every other part of JIM stays air-gap deployable; the connector's page says so plainly, and nothing else in JIM may come to depend on it.

## Business Value

Entra ID is the directory most JIM customers run alongside Active Directory, and Microsoft is pushing every hybrid tenant from Entra Connect Sync to Entra Cloud Sync and then towards cloud Source of Authority. A connector that understands Source of Authority lets JIM route each object's changes to whichever directory is authoritative for it during a migration that takes organisations years. The SCIM 2.0 Client Connector can already provision basic users into Entra ID, but Entra's SCIM endpoint is billed per call, has no delta and no bulk, and exposes no hybrid state (the April 2026 assessment on the issue), so it cannot serve this.

## Technical Architecture

### What already exists and is reused

The SCIM 2.0 Client Connector (`src/JIM.Connectors/SCIM/`) solved most of the transport problems this connector has, with no third-party packages:

| SCIM component | Reuse |
|---|---|
| `ScimRetryPolicy`, `ScimRetryDecision` | Retry with exponential backoff and jitter, honouring `Retry-After`; transient versus permanent classification. Generalise and share. |
| `ScimThrottleHints` | Proactive pausing from `RateLimit-*` headers. Graph's directory workload does not send those; it sends `x-ms-throttle-limit-percentage` above 80% of quota. Generalise into a hint source per connector. |
| `ScimOAuthClientCredentialsAuthentication` | Client credentials token acquisition, caching and refresh. Entra ID's token endpoint is standard OAuth 2.0, so the client-secret path works as is; the certificate path adds a signed client assertion (below). |
| `ScimCertificateValidator`, `ServerCertificateProbe` | Not needed for `graph.microsoft.com` in the ordinary case (public CA), but a TLS-inspecting proxy in the customer's network makes them relevant, so the Graph HTTP client keeps the same validation path. |
| `ScimBulkExporter` decisions | Correlate batch results by id, never by position; an unreported operation is failed, never assumed applied. Graph `$batch` has the same failure modes. |
| `MockScimProvider` approach | A stateful mock provider with switches for each misbehaviour (`test/JIM.TestScimServiceProvider`). The Graph equivalent is a mock Graph service. |

**Proposed:** move the shared pieces into `src/JIM.Connectors/Http/` (`HttpRetryPolicy`, `ThrottleHints`, the client-credentials strategy) as the first stack layer, with the SCIM connector switched over and its tests unchanged. That refactor is not part of the Graph feature, so it lands as its own layer.

### Proposed component layout

Under `src/JIM.Connectors/Graph/`, mirroring the SCIM decomposition:

| Component | Responsibility |
|---|---|
| `GraphConnector` | Entry point: `IConnector`, `IConnectorCapabilities`, `IConnectorSettings`, `IConnectorSchema`, `IConnectorImportUsingCalls`, `IConnectorExportUsingCalls`, `IConnectorCredentialAware`, `IConnectorCertificateAware`, `IConnectorImportWatermark` (the delta link is the watermark). |
| `GraphConnectorConstants` | Setting names, defaults, endpoint paths, the `$select` lists. |
| `GraphHttpClient` | Base URL per national cloud, authentication, retry and pacing, `x-ms-throttle-priority: low` on every request, JSON via `System.Text.Json`. |
| `GraphClientAssertion` | Builds the PS256-signed JWT client assertion (with the certificate's SHA-256 thumbprint in `x5t#S256`, per Microsoft's certificate credential format) with `System.Security.Cryptography`; no MSAL. |
| `GraphRequestPacer` | Token-bucket pacing of writes against Microsoft's published write quota, slowed further when `x-ms-throttle-limit-percentage` reports pressure. |
| `GraphConnectorSchema` | A curated schema for `user` and `group` (Graph's `$metadata` is several megabytes and describes thousands of types JIM will never touch), extended with discovered directory extension properties and the fifteen `onPremisesExtensionAttributes`. |
| `GraphConnectorImport` | Full import (`/users`, `/groups` with `$select`, paging on `@odata.nextLink`), delta import (`/users/delta`, `/groups/delta`, `members@delta`), Source of Authority synthesis. |
| `GraphConnectorExport` | Create, update, delete and membership changes, through `$batch` (20 requests per call) with per-request result correlation and retry of only the failed requests. |
| `GraphSourceOfAuthority` | The synthesis rules and the export-time verification (below). |

### Authentication

App-only (client credentials), against the customer's own app registration:

- **Client secret:** simplest; stored as `StringEncrypted`.
- **Certificate:** recommended for production. The PFX, Base64-encoded, in a `StringEncrypted` setting, with an optional encrypted Certificate Password; used to sign a client assertion. JIM has no private-key certificate store today (its certificate store holds trusted public certificates only), and the `File` setting type holds a path rather than content, so the PFX lives on the Connected System's settings, encrypted at rest with every other credential. Saving the settings refuses a PFX that will not open, has no private key, is not RSA of at least 2,048 bits, or is outside its validity period, and warns within 30 days of expiry.
- **Delegated (authorisation code) flow: dropped.** The issue lists it, but the connector runs unattended in the Worker; there is no user present to sign in, and a refresh token stored for a person is a worse credential than an app's.
- **National clouds:** a Cloud drop-down (Global, US Government L4, US Government L5 (DoD), China) sets the authority and Graph host. The audiences JIM serves include government.

Permissions are documented least-privilege per capability: `User.Read.All` and `Group.Read.All` to import; `User.ReadWrite.All` and `Group.ReadWrite.All` to export; `User-OnPremisesSyncBehavior.Read.All` and `Group-OnPremisesSyncBehavior.Read.All` for Source of Authority verification. The connectivity test reads the token's `roles` claim and names any missing permission rather than letting the first import fail with a 403.

### Source of Authority

`sourceOfAuthority` is a connector-synthesised, always-imported attribute on users and groups (the July 2026 review on the issue explains why: Graph has no such property, and `onPremisesSyncBehavior` cannot be `$select`ed):

| Value | Rule |
|---|---|
| `externalEntraDirectory` | `userType` is `Guest`, or the account's `creationType` / `identities` show it was invited from another tenant. Checked first. |
| `onPremisesDirectory` | `onPremisesSyncEnabled` is `true` and the object is not cloud-managed. |
| `cloud` | Everything else: never synchronised, or converted to cloud management. |

**A refinement to the July review's proposal.** That review proposed reading `onPremisesSyncBehavior` per object only at export time, because reading it per object at import would multiply API calls. There is a cheaper way to read it at import: the property supports `$filter` with advanced query capabilities (`$filter=onPremisesSyncBehavior/isCloudManaged eq true&$count=true` with `ConsistencyLevel: eventual`). One paged query per object type per run, selecting only `id`, returns every converted object; the import marks those `cloud` even though `onPremisesSyncEnabled` may still read `true`. That makes the imported value accurate at import time for a few requests per run instead of one per object, and leaves export-time verification as a safety net for conversions between import and export. Whether a conversion also surfaces in delta results is still unverified (the issue's open question); the filtered query answers it for delta imports too, by diffing the set of converted ids against the previous run's.

**Export behaviour** for an object whose authority is not `cloud`, chosen per Connected System:

- **Skip and warn** (default): no request is sent; the RPEI says which object, its `sourceOfAuthority`, why, and recommends the export Synchronisation Rule scope on it.
- **Attempt and report:** send it, and record Graph's refusal as an expected, explained failure.

Before acting on a non-`cloud` value, the connector re-reads `onPremisesSyncBehavior` for that object (cached for the run, paced like any other read), and records any conversion it finds in the RPEI. **Attribute-level authority (writing cloud-only attributes to a hybrid object) is out of scope for v1**; v1 is object-level, as the issue suggests.

### Rate limiting

Microsoft publishes the identity and access limits: per app and tenant, 3,500 to 8,000 resource units per 10 seconds by tenant size, and a write quota of 3,000 requests per 150 seconds; per tenant across every app, 18,000 writes per 5 minutes. The tenant figure is what makes JIM a neighbour: a JIM export at full speed could use a third of the tenant's write budget and starve other applications.

- **Writes are paced by default** at a fraction of the app-and-tenant write quota (proposed default: 10 per second, so 1,500 per 150 seconds, half the quota). An optional **Maximum Writes Per Second** setting lets an administrator go lower, or higher up to the quota. This departs from the SCIM connector, which dropped its rate setting because providers advertise their limits in `RateLimit-*` headers; Graph's directory does not, and only reports pressure once 80% of the quota is used.
- **`x-ms-throttle-priority: low`** on every request, so that under pressure Graph throttles JIM before interactive applications. This is the "gentle mode" the issue asks for, provided by Microsoft rather than guessed at.
- **Adaptive slowing** when `x-ms-throttle-limit-percentage` appears (at or above 0.8), and `Retry-After` honoured on every 429.
- **`$select` everywhere**, which also lowers each read's resource-unit cost by one.
- Throttling is reported: every 429 counts towards a warning on the Activity, and sustained throttling becomes an RPEI warning.

### Export

- **`$batch` from v1**, 20 requests per call. Results are matched by request `id`, a 429 or 5xx inside a batch retries only that request, and a request with no result is failed, not assumed applied (the SCIM bulk rule).
- **Create** returns the object `id`, which becomes the External ID. **Update** is PATCH. **Delete** is configurable: delete (Entra ID's 30-day soft delete) or disable (`accountEnabled = false`).
- **Group membership** is a multi-valued reference attribute (`members`) on the group, as in the LDAP and SCIM connectors, written with `members@odata.bind` (up to 20 per request) and `DELETE .../members/{id}/$ref`.
- **Eventual consistency:** a 404 on a just-created referenced object is classified as a missing dependency and retried, like the SCIM connector's dependency-ordering errors. Exports are confirmed by the next import, never auto-confirmed.
- **Resumability** comes from JIM's Pending Export model: a cancelled or failed run leaves the unapplied Pending Exports pending, and the next run picks them up. No connector-side checkpoint is needed.
- **Progress and estimate:** the export reports progress through the existing Activity progress, and the estimate at the configured write rate is shown on the Activity.

### Capability flags

| Capability | Value | Rationale |
|---|---|---|
| `SupportsFullImport` | `true` | Core. |
| `SupportsDeltaImport` | `true` | Graph delta queries; the delta link is the watermark. An expired delta link (`410 Gone` / `syncStateNotFound`) falls back to a full import with a warning, matching the LDAP precedent. |
| `SupportsExport` | `true` | Core. |
| `SupportsParallelExport` | `false` | Throughput comes from `$batch` and pacing; parallel export would only race the write quota. |
| `SupportsPaging` | `true` | Run Profile Page Size maps to `$top` (Graph caps users at 999). |
| `SupportsPartitions` | `false` | A tenant has no partition concept. |
| `SupportsUserSelectedExternalId` | `false` | `id` is immutable and assigned by Entra ID. |
| `SupportsAutoConfirmExport` | `false` | Eventual consistency; the confirming import proves the write. |

## Implementation Phases

Like the SCIM connector, the connector is registered in `ConnectorFactory` from Phase 1 but **not seeded** until the last phase, so partly built work can merge to `main` without administrators ever seeing it.

1. **Shared HTTP layer (stack layer).** ✅ Move retry, throttle hints and client credentials into `JIM.Connectors/Http/`; SCIM switched over, its tests unchanged.
2. **Skeleton.** ✅ `GraphConnector`, constants, settings (Tenant ID, Client ID, authentication method, secret or certificate, Cloud), capabilities, factory registration. Unit tests for settings shape and validation.
3. **HTTP client and authentication.** ✅ Token acquisition (secret and certificate assertion), national clouds, pacing, priority header, adaptive slowing, 429 handling. Connectivity test that reads the token's roles and names missing permissions. Mock Graph service with throttling, partial-batch and eventual-consistency switches.
4. **Schema.** Curated user and group schema; directory extension discovery; `onPremisesExtensionAttributes`; `sourceOfAuthority` and its supporting attributes always present.
5. **Full import.** Users and groups, paging, membership as references, Source of Authority synthesis including the cloud-managed query.
6. **Delta import.** Delta links as watermark, `members@delta`, removed objects, expired-link fallback, conversion detection.
7. **Export.** `$batch`, create, update, delete or disable, membership, Source of Authority handling and export-time verification, dependency retries.
8. **Enablement.** Seeding, the connector page (`docs/connectors/jim-graph-connector.md`) with the app registration, permissions, and the internet-access exception, changelog, and an integration scenario against the JIM test tenant.

## Testing Strategy

- TDD throughout. All HTTP behaviour against a stateful mock Graph service in unit tests; no network.
- **Integration against a real tenant.** The Entra ID test tenant that serves the `jim-sso-entra` release gate can host a scenario: import, join to HR-projected Metaverse Objects, export changes, confirming import, delta import, and a hybrid object refused under Skip and Warn. It runs where Scenario 035 runs (the AD lab), not on pull requests. It needs an app registration in that tenant with the export permissions, and the scenario must create and clean up only objects under a recognisable prefix.
- Scale: an import and a paced export of a few thousand users in the test tenant, to measure real throughput against the write pacing.

## Dependencies

None proposed. `System.Net.Http`, `System.Text.Json` and `System.Security.Cryptography` cover the HTTP client, JSON and the certificate client assertion. The Microsoft Graph SDK (a large dependency tree for the two resource types JIM uses) and MSAL (for one token request) are both avoidable, and the SCIM connector set the same precedent.

## Out of Scope for v1

- Delegated authentication.
- Attribute-level Source of Authority.
- Performing Source of Authority conversion (#1145); the export design must not preclude a navigation-property PATCH.
- Custom security attributes and schema extensions registered by other applications (directory extensions are in).
- Pronouns: still beta-only in Graph; picked up by #360 when it reaches v1.0.
- Microsoft 365 group provisioning beyond what the `group` resource supports (Teams, mailboxes).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| JIM exhausting a shared tenant's write budget | Paced writes at half the app quota by default, low priority header, adaptive slowing. |
| Writing to on-premises-authoritative objects | Skip and Warn by default; export-time verification; scoping guidance in the docs and UI. |
| Eventual consistency causing false failures | Dependency errors retried; confirmation by the next import. |
| Delta link expiry | Full import fallback with a warning. |
| Real-tenant integration tests leaving debris | Prefixed test objects; cleanup at start and end of the scenario. |

## Decisions (2026-11-20)

1. **No new dependencies.** HTTP, JSON and the certificate client assertion are written against the BCL, as for the SCIM connector; neither MSAL nor the Graph SDK.
2. **The shared HTTP layer lands first**, as the bottom layer of the stack, with the SCIM connector switched over and its tests unchanged.
3. **Integration tests run against the Entra ID test tenant** that serves the `jim-sso-entra` gate, through a second app registration holding `User.ReadWrite.All` and `Group.ReadWrite.All`; the scenario creates and removes only prefixed test objects.
4. **The certificate is a Base64 PFX in an encrypted setting** (2026-11-21), not a `File` setting as first written: a `File` setting holds a path, so the private key would sit unencrypted on a volume mounted into both the web and worker containers. Pasting Base64 keeps it encrypted at rest and works identically through the portal, the REST API and PowerShell.
5. **The Connector is named "JIM Microsoft Entra ID Connector"** (2026-11-21), after the system administrators connect to rather than the API it speaks. Free to change until Phase 8 seeds it; after that the name is part of the Connector's identity.
6. **The mock Graph service grows with the phases** (2026-11-21). Phase 3's tests drive the client through a stub transport with throttling, token refusal, Graph error and permission switches; the partial-batch and eventual-consistency switches arrive with export (Phase 7), where they have something to exercise.
7. **TLS certificate diagnosis is deferred to Phase 8** (2026-11-21). Graph's certificates chain to a public CA, so only a TLS-inspecting proxy causes a trust failure; Phase 3 reports that case with a message pointing at Admin > Certificates. Phase 8 wires in the shared `ServerCertificateDiagnosis`, which shows the presented certificate as the SCIM and LDAP Connectors do.
