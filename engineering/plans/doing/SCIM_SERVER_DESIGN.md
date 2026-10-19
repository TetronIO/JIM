# SCIM 2.0 Service Provider Design

- **Status:** Doing (Phases 1 to 3 complete: the SCIM store, the server's writes, Get and list, and the Service Provider Connector behind its feature flag)
- **Issue:** [#124](https://github.com/TetronIO/JIM/issues/124)
- **Sub-issues fixed as part of this work:** [#2088](https://github.com/TetronIO/JIM/issues/2088) (API keys as Bearer only), [#2089](https://github.com/TetronIO/JIM/issues/2089) (Delta Import deletion safeguards), [#2090](https://github.com/TetronIO/JIM/issues/2090) (JIM.Scim multi-value writer), [#2091](https://github.com/TetronIO/JIM/issues/2091) (SCIM `password` exposure), [#2092](https://github.com/TetronIO/JIM/issues/2092) (Delta Synchronisation watermark race), [#2093](https://github.com/TetronIO/JIM/issues/2093) (SCIM client token endpoint HTTPS), [#2094](https://github.com/TetronIO/JIM/issues/2094) (SCIM client and Keycloak), [#2095](https://github.com/TetronIO/JIM/issues/2095) (SCIM Standard Mapping hints), [#2096](https://github.com/TetronIO/JIM/issues/2096) (API key Activity attribution), [#2097](https://github.com/TetronIO/JIM/issues/2097) (Keycloak SSO guide and development realm)
- **Related Issues:** [#123](https://github.com/TetronIO/JIM/issues/123) (Event-Based Synchronisation; owns everything deferred below), [#545](https://github.com/TetronIO/JIM/issues/545) (SCIM 2.0 Client Connector, shipped), [#1119](https://github.com/TetronIO/JIM/issues/1119) (Password Synchronisation), [#1709](https://github.com/TetronIO/JIM/issues/1709) (SCIM client sets passwords), [#1566](https://github.com/TetronIO/JIM/issues/1566) (password capture ingress API), [#1104](https://github.com/TetronIO/JIM/issues/1104) (Metaverse schema policy and Standard Mappings)
- **Backlog and parked follow-ups:** [#2098](https://github.com/TetronIO/JIM/issues/2098) (Reset SCIM store, backlog), [#2099](https://github.com/TetronIO/JIM/issues/2099) (SCIM inbound password channel, parked; sub-issue of #1119)
- **Related Plans:** [`../done/SCIM_CLIENT_CONNECTOR_DESIGN.md`](../done/SCIM_CLIENT_CONNECTOR_DESIGN.md), [`../METAVERSE_SCHEMA_POLICY.md`](../METAVERSE_SCHEMA_POLICY.md), [`../done/OUTBOUND_SYNC_DESIGN.md`](../done/OUTBOUND_SYNC_DESIGN.md) (Event-Based Sync Roadmap)
- **Last Updated:** 2026-10-17

> **October 2026 rewrite.** This document replaces the original design (December 2025, amended by the July 2026 cross-design review with #545). A design review on 11 October 2026 validated every claim in the original against the shipped SCIM 2.0 Client Connector and the current codebase, researched how mainstream SCIM clients and servers behave (authentication in particular), and ran a judge panel on where SCIM pushes should enter JIM. The original's central mechanism, running the inbound sync engine inline on each SCIM request, turned out to be the core of #123 built in the least safe place, and the method it relied on does not exist. The decisions below supersede the original in full; git history holds the previous text.

## Overview

JIM gains a SCIM 2.0 service provider, so identity providers such as Microsoft Entra ID, Okta, Ping, OneLogin, JumpCloud, Keycloak (through an outbound plugin) and other JIM instances can push Users and Groups to JIM. Each pushing identity provider is a Connected System using the **JIM SCIM 2.0 Service Provider Connector**.

The design rests on one boundary:

- **Request time (JIM.Web).** SCIM endpoints authenticate, validate and apply each request against a JIM-owned **SCIM store**, and answer from it immediately. Nothing at request time touches a Connector Space, the Metaverse or the Worker.
- **Scheduled (JIM.Worker).** The Service Provider Connector imports from the SCIM store through ordinary Full Import and Delta Import Run Profiles. Delta Synchronisation and Export then carry changes onward exactly as they do for LDAP or SQL sources.

The Worker therefore remains the only writer of every Connector Space, and #124 needs nothing from #123. Inbound latency is the Schedule interval until #123 adds push-triggered runs.

```
 SCIM clients (Entra ID, Okta, Keycloak plugin, another JIM, ...)
        │  Authorization: Bearer jim_ak_...   (one scoped key per Connected System)
        ▼
 ┌──────────────────────────────┐   writes and reads    ┌──────────────────────────────┐
 │ SCIM endpoints (JIM.Web)     │ ────────────────────▶ │ SCIM store (PostgreSQL)      │
 │ /scim/v2/{connectedSystemId} │ ◀──────────────────── │ resources, members,          │
 └──────────────────────────────┘   one transaction     │ change feed, tombstones      │
   request time: milliseconds         per request       └──────────────┬───────────────┘
 ─────────────────────────────────────────────────────────────────────┼─────────────────
   scheduled: Run Profiles in JIM.Worker                               │ change feed since watermark
                                                                       ▼
 ┌─────────────────────────────┐ Delta Import ┌─────────────────┐ Delta Sync ┌───────────┐ Export
 │ Service Provider Connector  │ ───────────▶ │ Connector Space │ ─────────▶ │ Metaverse │ ──────▶ targets
 └─────────────────────────────┘              └─────────────────┘            └───────────┘
```

## Business Value

- **Enterprise integration.** Mainstream identity providers provision over SCIM out of the box; customers configure a URL and a token instead of building feeds.
- **JIM as a hub.** Several SCIM sources aggregate into one Metaverse alongside pull-based sources, with the same Synchronisation Rules, Object Matching Rules, precedence and audit trail.
- **JIM-to-JIM.** One JIM instance can provision another using the shipped SCIM 2.0 Client Connector; proving this round trip is the acceptance test for this issue (the last outstanding success criterion from #545).
- **No new sync semantics.** Inbound SCIM uses the existing import and synchronisation pipeline unchanged, so synchronisation integrity guarantees carry over rather than being rebuilt.

## Decisions

### D1. Inbound boundary: SCIM store plus ordinary imports

SCIM requests read and write only the SCIM store. The Service Provider Connector implements Full Import and Delta Import against the store. All Connector Space writes stay in the Worker's existing import pipeline: RPEIs, Activities, change history, deletion detection and Run Profile safeguards apply unchanged.

Rejected alternatives (judge panel, weighted for integrity, conformance, scope, cost and operability):

| Option | Score | Why rejected |
|---|---|---|
| SCIM handlers write Connected System Objects directly | 3.5 | Makes JIM.Web a second, cross-process Connector Space writer. Delta Synchronisation's watermark is stamped at the end of a run, Connected System Objects have no concurrency token, and the Worker holds a process-local external-id cache, so edits can be lost or skipped permanently. The CSO is also the wrong container: its flattening is lossy, its row version changes on every sync, it has no case-insensitive uniqueness, and Clear Connector Space would destroy the only copy of what the IdP sent. |
| Worker-drained inbox | 3.1 | A GET, filter or PATCH straight after a POST would find nothing until the drain; making it work means materialising current state per resource, which is the store plus a second source of truth. |
| Inline synchronisation per request (the original design) | 1.6 | No single-object sync path exists; running one in the web tier collides with Worker runs, adds join, flow and export latency to every request, and leaves partial state on client abort. This is the core of #123. |
| Store plus a run triggered on each push | 6.6 | Correct triggering needs queue de-duplication, a durable debounce and Delta Import to Delta Synchronisation chaining, none of which exist. Deferred to #123 (D2). |

### D2. What #124 includes and what #123 keeps

- **#124:** accept, validate, durably store and serve SCIM; make the store importable (Full Import and Delta Import).
- **#123 keeps:** running an import when a push arrives (queue de-duplication, debounce, re-arm), immediate inbound processing, immediate export execution, retry and backoff for event-driven work, and any post-commit notification (for example `LISTEN`/`NOTIFY`).
- **The seam #124 leaves for #123:** every store write goes through one `JimApplication` method, and "changes awaiting import" is a single query (the store's last change sequence minus the imported watermark).

### D3. Authentication in v1: a scoped API key sent as a Bearer token

The universal SCIM credential is a static token sent as `Authorization: Bearer <token>`; every mainstream client supports it and several support nothing else. Mainstream SCIM servers issue it the same way: generated in the admin UI and shown once, usable only for SCIM and one tenant, about a year's lifetime, two at a time for rotation.

- **Bearer everywhere.** JIM API keys move to `Authorization: Bearer jim_ak_...` only, across the whole product, and `X-API-Key` is removed ([#2088](https://github.com/TetronIO/JIM/issues/2088)). JIM is pre-v1.0, so there is no compatibility window.
- **Scope is mandatory for SCIM.** A key used on `/scim/v2/{id}` must be scoped to that Connected System. A scoped key carries no role claims (not even the virtual User role) and is refused on every other path. The scope model is generic (scope type plus target) because #1566 needs scoped keys too. On the authenticated principal the scope is a `jim_key_scope` claim of `ScimServiceProvider.ReadWrite` (dotted `Resource.Action` in PascalCase, the convention recorded on #622 for the RBAC permission catalogue), with the target in its own `jim_key_scope_connected_system_id` claim (the `jim_` prefix every JIM claim carries), so the permission name never embeds a resource instance. Authentication refuses a scoped key anywhere outside `/scim`, and the default authorisation policy refuses a scoped principal as a second line.
- **At most two active scoped keys per Connected System,** for overlap rotation; the portal shows each key's last-used time and address so the administrator can see the old one has gone quiet.
- **Expiry is mandatory** for scoped keys: default 365 days, at most 730, with warnings as it approaches.
- **Failure semantics.** Any authentication failure (missing, malformed, unknown, disabled, expired or wrong-system key) returns the same `401` with `WWW-Authenticate: Bearer realm="JIM SCIM"` (adding `error="invalid_token"` when a credential was presented) and a SCIM error body; the real reason is audited, aggregated per Connected System and key. Never a redirect. Authorisation failures return `403` with a SCIM error body.
- **Unscoped Administrator keys are not accepted on `/scim`** (least privilege). The JIM-to-JIM tests mint a scoped key.
- **Never on `/scim`:** HTTP Basic, the OAuth authorisation code grant, the password grant, `X-API-Key` or other custom headers, tokens in query strings, non-expiring tokens, anonymous access (discovery endpoints require authentication too).
- **Leniency.** The scheme name is matched case-insensitively, and a bare `jim_ak_...` value without the `Bearer` prefix is accepted on `/scim` only, because some clients send the configured header value verbatim.

### D4. A second authentication method is deferred until a customer needs it

Decided 11 October 2026: v1 ships the scoped key (D3) only; a second method is built when a customer needs one, with B (then C) as the preferred next step.

Why the scoped key is sufficient, not a stopgap:

- **It is what SCIM servers offer.** Of 14 SCIM service providers surveyed (AWS IAM Identity Center, Atlassian, GitHub Enterprise, Google Workspace, Cloudflare, Zscaler, Databricks, Snowflake, Salesforce, Zoom, Slack, Dropbox and others), about 10 accept a static token generated in their admin UI. None requires a token endpoint for a custom integration; client credentials appear only as an extra on platforms that already run an OAuth server, and the remaining OAuth-only services reuse their platform's existing OAuth through an interactive admin consent in the identity provider's gallery app.
- **It is what SCIM clients can all send.** Entra ID (cloud, and its on-premises provisioning agent), Okta (custom apps and its on-premises agent), PingOne, PingFederate, OneLogin, JumpCloud, the Keycloak outbound plugins and JIM's own SCIM 2.0 Client Connector all support it. For OneLogin, JumpCloud and the Entra on-premises agent it is the only documented method, so it is essential for a JIM that is not reachable from the internet.
- **What it cannot do:** a listing in the Entra app gallery (new gallery apps need client credentials or federation), and customers whose policy forbids long-lived secrets. Google Workspace cannot push to any custom SCIM service provider, whatever JIM supports.
- **RFC 7644** names Bearer tokens as common practice and requires them to have a limited lifetime, which the mandatory expiry in D3 satisfies.

The original design's "Federated Identity Credential" (the client presenting its own IdP-signed JWT on SCIM calls) is dropped: no mainstream SCIM client does this. The options for a second method, assessed in the October review:

| Option | What it is | Main reason to choose it |
|---|---|---|
| A. Trusted token issuer | JIM validates JWT access tokens from an issuer configured on the Connected System (for example the customer's Keycloak); JIM issues nothing and stores no secrets | Machine identities managed in the customer's own token issuer; Keycloak-centric customers |
| B. JIM-hosted token endpoint | OAuth 2.0 client credentials against a token endpoint JIM hosts per Connected System; short-lived opaque access tokens stored hashed like API keys | Cloud identity providers using client credentials without an internet-facing third-party issuer; required for new Entra gallery apps |
| C. Entra Workload Identity Federation | An extra way into B's token endpoint: Entra presents a signed assertion instead of a secret; JIM validates it against Microsoft's published keys (outbound internet) | Secretless authentication from Entra; Microsoft's recommended method for gallery apps |

When a customer needs more, choose by their driver; B (then C) is the preferred default, because Entra is the most likely source of pushes and its secretless route runs through a token endpoint JIM hosts, whereas A only works when the identity provider can reach the customer's token issuer (for a cloud identity provider, an internet-facing issuer). Whichever is built must be its own authentication path, kept apart from the SSO JWT pipeline: that pipeline trusts one site-wide issuer and resolves every token to a person, which a SCIM client must never become.

### D5. Pushed passwords are refused in v1

- A request carrying `password` (POST, PUT, PATCH, including no-path value objects and `add` operations) gets `400` with `scimType` `invalidValue`; a filter on `password` gets `400` `invalidFilter`. Both checks run before anything touches the store.
- `password` is left out of the published `/Schemas`, and `ServiceProviderConfig.changePassword.supported` is `false`.
- The store has no password column and never keeps a document built from the unsanitised request; logs, Activities and error details never hold the value or its length.
- One write-only-attribute intake function, called from the single deserialise and PATCH-apply boundary, owns these rules. It is the seam for the parked inbound password channel ([#2099](https://github.com/TetronIO/JIM/issues/2099)), which needs its own decisions: an explicit per-Connected-System "authoritative for passwords" opt-in, what to do before a Metaverse Object exists, and the assurance gap against #1566.
- Refusing (rather than silently discarding) never tells an identity provider that a password was set when it was not.
- The separate defect that lets the shipped client map and export `password` in clear text is fixed first ([#2091](https://github.com/TetronIO/JIM/issues/2091)).

### D6. `JIM.Scim` is the one SCIM protocol layer

The server consumes `src/JIM.Scim` from JIM.Web and JIM.Application and adds no typed `ScimUser` or `ScimGroup` DTOs: request bodies bind as JSON and map through the same flattened attributes the client uses, so attribute names are identical in both directions (`emails.work`, `name.givenName`, `enterpriseUser.department`). What exists today is client-oriented; the server work adds:

- **A PATCH applier** that is the exact inverse of the shipped `ScimPatchBuilder`, including the non-strict forms the builder emits (a value-less `replace` to clear, a filtered `replace` on a missing entry, scalar adds to multi-valued attributes, unquoted `primary eq true`), tested as `Apply(Build(x)) == x`.
- **One filter grammar** shared by the client's query builders and the server's parser.
- **A server mode for the resource reader and writer** that honours `mutability` and `returned` (ignore client-supplied `id` and `meta`; emit `id` and `meta` in responses; never return write-only attributes).
- **ETag helpers** with SCIM's weak comparison.
- **Discovery emitters** for `/ServiceProviderConfig`, `/ResourceTypes` and `/Schemas`, with the resource-type composition moved out of the client's private `ScimConnectorSchema` so client and server derive identical schemas.
- **Defect fixed first:** the writer collapses multi-valued complex attributes without canonical types (`roles`, `entitlements`, `x509Certificates`) to the first value ([#2090](https://github.com/TetronIO/JIM/issues/2090)).

### D7. Identifiers

- The SCIM `id` is a server-assigned, immutable UUID. It is also the external id of the Connected System Object the import creates, which is what the client already assumes.
- A client-supplied `id` is ignored (it is read-only in RFC 7643). `externalId` is stored as sent and is unique among a Connected System's live resources.
- Resource ids bind as strings; an unknown or malformed id returns a SCIM `404`, never a framework `400`.

### D8. Concurrency, atomicity and errors

- **ETag** is the store row's version, sent as `W/"n"` in both the `ETag` header and `meta.version`. Only SCIM writes change it, so a synchronisation run never invalidates the client's next `If-Match`.
- **`If-Match` mismatch returns `412`** (RFC 7644 s3.14; the shipped client classifies `412` as a concurrency conflict). `If-Match` is optional on every write.
- **`409`** is reserved for uniqueness (`scimType` `uniqueness`, detail "The attribute 'userName' must be unique", which the shipped client's classifier parses).
- **PATCH is atomic:** if any operation fails, none applies and the request returns an error (RFC 7644 s3.5.2). The original design's "partial success, 200 with error details" would have made the client record unapplied changes as exported.
- **PATCH returns `200` with the resource,** and honours `attributes` and `excludedAttributes` (including `excludedAttributes=members`).
- **Throttling** returns `429` with `Retry-After` and a SCIM error body.

### D9. References are strict

Group members and `manager` must refer to a live resource of the right type in the same Connected System; otherwise the request fails with `400` `invalidValue`. This matches the shipped client's error classifier and RFC 7644's expectation that clients create dependencies first. (The original design contradicted itself between "deferred" and "strict".)

### D10. Several SCIM sources at once

Each identity provider gets its own Connected System. The store's tables are shared, but every row carries its Connected System's id:

- own base URL (`/scim/v2/{connectedSystemId}`) and scoped keys;
- own change sequence, so writes to one system never wait on another;
- uniqueness of `userName` and `externalId` within the system only;
- own Connector Space, Run Profiles, Schedule and Synchronisation Rules.

The same person arriving from two providers produces two resources and two Connected System Objects that join one Metaverse Object through an Object Matching Rule, exactly as an HR feed and a directory join today.

### D11. Store contents versus Connected System configuration

The store accepts every supported resource type and keeps the whole sanitised payload, regardless of the Connected System's object type and attribute selection. Imports map only what is selected. Selecting more later and running a Full Import backfills without the identity provider re-pushing, and a change to `JIM.Scim`'s flattening is absorbed the same way.

### D12. No export to the store in v1

`SupportsExport` is `false`: JIM does not write Metaverse values back into the store for identity providers to read. Revisit if customers want JIM-mastered attributes visible to the pushing provider; it would change the `meta.lastModified` and version contract.

### D13. Resetting the store is a backlog item only

Clearing a store is unlike clearing a Connector Space: JIM cannot rebuild it, and until the identity provider re-pushes, JIM sees what looks like a mass delete. The supported guidance is to create a new Connected System for scenarios such as pointing at a different identity provider tenant. A reset feature ([#2098](https://github.com/TetronIO/JIM/issues/2098)) needs a hard benefits-versus-risks review once the Service Provider has been in use and feedback is available.

### D14. Latency is stated honestly

Inbound changes are accepted immediately and reach the Metaverse at the next scheduled import and synchronisation. Docs recommend a Sequential Schedule of Delta Import, Delta Synchronisation and Export every 1 to 5 minutes, plus a nightly Full Import, and warn against putting this system's import and synchronisation in one parallel step group. "Real-time" is not claimed until #123.

### D15. Names and placement

- Connector name: **JIM SCIM 2.0 Service Provider Connector** (constant `ScimServiceProviderConnectorName` already exists in `src/JIM.Connectors/Constants.cs`), paired with the shipped **JIM SCIM 2.0 Client Connector**.
- Endpoints live under `/scim/v2/{connectedSystemId}` in JIM.Web, outside the `/api` pipeline, so every `/api`-only stage (exception handling, rate limiting, role enrichment, redirect suppression) needs an explicit `/scim` counterpart.
- Delivery sits behind a feature flag; see D16.

### D16. A feature flag keeps it dark until it is ready

Layers land on `main` one at a time, so a release can be cut with the Service Provider half built. A flag, `Features.ScimServiceProvider` in `FeatureFlagCatalogue`, keeps every layer invisible until it is switched on, so no release has to wait for the feature or be held back from `main`.

- **Introduced in Phase 3**, with the first layer an administrator could see. Phases 1 and 2 add nothing reachable. Phase 3 registers and seeds the Connector, and seeding is data, which a flag does not gate (Developer Guide, Feature Flags), so the Connector row exists from then on and the gates below are what hide it. File the flag's removal issue in the same PR and give it as the catalogue entry's `TrackingIssueNumber`.
- **Gated at every entry point, and nowhere deeper:**
  - the `/scim` pipeline answers `404` while the flag is off, as though the route did not exist;
  - creating a Connected System with the Service Provider Connector is refused (portal, REST, PowerShell), and the Connector is left out of the portal's Connector choice;
  - scoped key management and the store summary (portal, REST, PowerShell) are refused.
- **A Connected System that already exists keeps working, or fails loudly**, if the flag is turned off after one was created: its imports report that the feature is off rather than importing nothing.
- **Three stages:**
  1. **In Development** until the JIM-to-JIM scenario passes. Only development and the integration harness can switch it on; scenario setup uses `Enable-JIMFeature -AllowInDevelopment`.
  2. **Preview** for at least one release, so administrators can try it against their own identity provider (Microsoft Entra ID, Okta, Keycloak), where client quirks such as #2094 surface, while its behaviour can still change. The Connector, its Connected System page and the docs carry the Preview label.
  3. **Removed** once a release has shipped it in Preview without changes being needed: the catalogue entry, every gate, the flag-off tests and the harness's `Enable-JIMFeature` step go in the PR that closes the tracking issue.

### D17. Every accepted write is journalled

Decided 17 October 2026. An import sees each resource only as it stands when the import runs, so with nothing else a resource created and deleted between two imports would leave no trace in JIM, an attribute changed twice would show only its last value, and nothing would say which key or address made a change. That would fall short of what `engineering/COMPLIANCE_MAPPING.md` already claims for AU-2 and GDPR Article 30.

- **The journal (Phase 2).** One row per resource per accepted write, written in the same transaction as the write: change sequence, resource id and type, operation, the changed attributes by SCIM name, the API key (id and name) and the client address. It cannot disagree with the store, and costs one insert per write.
- **Names, not values.** The values are in the store and, once imported, in the Connected System Object's change history; a second copy of personal data in the journal would buy nothing (data minimisation). `password` cannot appear in any case (D5).
- **Ignored attributes are journalled too** (decided 18 October 2026). An attribute the store's definitions do not declare (a custom extension an administrator has mapped at the identity provider, before custom extensions can be defined in JIM) is ignored, as RFC 7643 section 2 allows and the leniency below requires, because refusing it would fail every create from that provider. Ignored is never silent: each entry also names the attributes the write ignored, and the summary Activity (Phase 5) counts them, so an administrator sees what JIM is not keeping. Refusing them was considered and rejected for that reason.
- **Retention:** the journal is change history, so the History Retention Period and its cleanup govern it; no new setting.
- **Attribution:** the store's write methods take the actor (key and address). Phase 4 supplies it from the authenticated scoped key.
- **A summary Activity (Phase 5).** Per Connected System, key and 15-minute window, counts of creates, updates, deletes and rejections, aggregated as the security audit aggregates failed sign-ins, linking into the journal. One Activity per write was rejected: an initial load of 100,000 Users would be 100,000 Activities, swamping the operational view.
- **From import back to request (Phase 3, then 5).** The Connected System Object's external id is the SCIM id, and a Delta Import reads exactly the sequence range from its watermark to its horizon, so "which SCIM requests caused this change" is a query over the journal: that resource's entries within the import's range (for a Full Import, from the previous import's watermark). Nothing is stored on the RPEI. Phase 3 makes sure the import's Activity records its range; Phase 5 shows the requests from a change record.

### D18. Resource types and schemas are data, not code

Decided 17 October 2026. SCIM is extensible two ways: schema extensions add attributes to an existing resource type (the Enterprise User extension, or a provider's custom attributes on User), and custom resource types add new kinds of resource with their own endpoint and schema (RFC 7643 sections 3.3 and 6). v1 supports User and Group with the Enterprise User extension, but the store is built so that either kind of extensibility is configuration, not a reshaping of tables already holding customer data under automated migrations.

- **Definitions per Connected System.** Each Connected System's store holds its resource types (`ScimStoreResourceTypes`) and schemas (`ScimStoreSchemas`) as the RFC 7643 documents JIM publishes at `/ResourceTypes` and `/Schemas`, attribute characteristics included. Discovery, intake, PATCH, projection, uniqueness and references all run from these. User, Group and Enterprise User are seeded from `ScimCoreSchemas` when a Connected System is created.
- **Nothing in the store names a type.** A resource points at its type's row (a composite key on Connected System and type, so it can never point at another system's type). There is no `userName` or `displayName` column.
- **Uniqueness is data.** `ScimStoreUniqueValues` holds one row per value of every attribute a schema marks unique (and `externalId`), normalised as the schema compares it (folded to one case unless `caseExact`), keyed by (Connected System, type, attribute, SHA-256 of the value). The write session checks the key under the store's write lock before writing, so a conflict names its attribute in the `409` and leaves the session usable; the key is the backstop. The same rows answer the `userName eq` uniqueness probe some identity providers send every cycle.
- **References are data.** `ScimStoreReferences` holds (source, attribute, target) for every writable reference attribute, single-valued or multi-valued: one whose `$ref` names resource types. In v1 that is a Group's `members` and the Enterprise User `manager`; a custom type's references later. Deleting a resource removes references to it and versions every resource that held one, so a deleted manager is cleared from the Users it managed rather than left dangling.
- **Not built in v1:** management of custom resource types and extensions (portal, REST, PowerShell), and their import mapping. Each is a feature over these tables, not a migration of them.
- **Rejected:** columns per known attribute (every new type or unique attribute would be a migration of a populated table), and attributes as rows throughout (an entity-attribute-value store is slow to read and hard to reason about; only uniqueness and references need the database's enforcement, so only they are rows).

### D19. How SCIM definitions are authored

Decided 17 October 2026; the interface design itself is open, and gets a design pass of its own (below) before any of it is built.

- **The definitions are the source of truth.** A Connected System's resource types and schemas (D18) decide what the store accepts and what discovery publishes. The Connected System's Schema tab is derived from them, exactly as every other Connector's schema is derived from its system: the Service Provider Connector's schema discovery reads the definitions. The Schema tab stays generic and gains nothing SCIM-specific, and is never where SCIM definitions are edited.
- **v1 is read-only.** The core definitions are seeded when the Connected System is created; Phase 5 adds `Get-JIMScimResourceType` and `Get-JIMScimSchema`, their REST endpoints, and a read-only view on the Connected System page.
- **Management comes in a later release, across all three surfaces at once:** add, change and remove resource types, extensions and attributes (portal, REST, PowerShell), plus `Export-JIMScimConfiguration` and `Import-JIMScimConfiguration` over the RFC 7643 documents, which are what identity providers' own documentation hands an administrator. A change to a definition holding data is never a bare update: it is validated against the stored resources (a new required attribute, a type change, a new uniqueness constraint that existing values break), backfills `ScimStoreUniqueValues` for a new unique attribute, runs under the store's write lock so no request sees half a definition, and is recorded in configuration change history with an Activity like any other configuration change.
- **The interface has to meet two tests:** the best management interface for the job, and one that fits JIM's design rather than feeling bolted on. Both directions are failures: pushing SCIM concepts into the generic Schema tab, and building a SCIM island with its own conventions. Two precedents already in JIM are the starting point, to be confirmed or rejected by the design pass:
  - **Shape:** the Metaverse schema. Resource types and extension attributes are the same kind of thing as Metaverse Object Types and Metaverse Attributes (named types, typed attributes, bindings between them, a deletion preview before removing anything in use), so the cmdlet verbs, the list-then-detail pages and the deletion preview should feel the same.
  - **Placement:** capability-driven tabs on the Connected System page. Scope (Partitions) and Passwords appear only for Connectors that declare the capability; a SCIM definitions tab would appear only for the Service Provider Connector in the same way.
- **Design pass before building:** mock-ups of the options as artefacts, compared against both tests, and a decision recorded here.

## Technical Architecture

### The SCIM store

| Table | Purpose and key columns |
|---|---|
| `ScimStoreResourceTypes` | One row per resource type per Connected System (D18): `Name`, `Endpoint` (unique per system), `Definition` (jsonb, the RFC 7643 resource type document). |
| `ScimStoreSchemas` | One row per schema per Connected System (D18): `Urn` (unique per system), `Definition` (jsonb, the RFC 7643 schema document with attribute characteristics). |
| `ScimStoreResources` | One row per resource. `Id` (uuid, the SCIM id), `ConnectedSystemId` (FK, cascade), `ResourceTypeId` (with `ConnectedSystemId`, a NO ACTION key to the type, so a type with resources cannot be removed), `Version`, `ChangeSequence`, `Created`, `LastModified`, `DeletedAt` (tombstone), `Payload` (jsonb, sanitised, without attributes held as references). A tombstone keeps only its id, type and stamps. |
| `ScimStoreUniqueValues` | One row per value that must be unique (D18): key (`ConnectedSystemId`, `ResourceTypeId`, `Attribute`, `ValueHash`), plus `Value` and `ResourceId` (cascade). |
| `ScimStoreReferences` | One row per reference (D18): key (`SourceId`, `Attribute`, `TargetId`), both resources cascading. A Group's members, so adding one member does not rewrite a large group. |
| `ScimStoreStates` | One row per Connected System: `LastChangeSequence`, `TombstonePurgeFloor`. The row is also the store's write lock (see the change feed). |
| `ScimStoreJournalEntries` | The journal (D17). `ConnectedSystemId` (cascade), `ChangeSequence`, `ResourceId` (no FK: entries outlive purged tombstones), `ResourceTypeName` (a name, so an entry outlives its type's definition), `Operation`, `ChangedAttributes` (text[]), `ApiKeyId` (FK, set null when the key is deleted), `ApiKeyName`, `ClientAddress`, `Occurred`. |

`ApiKeys` gains its generic scope (scope type plus target) in Phase 4, with the authentication that uses it.

- **Uniqueness** is enforced by the database through `ScimStoreUniqueValues` (D18), over live resources only: a tombstone's rows are removed, freeing its values. A retried POST therefore gets `409`, never a duplicate.
- **Connected System lifecycle:** every table cascades from `ConnectedSystems`, so deleting a Connected System and the factory reset's truncate remove its store and definitions with no statement of their own; the foreign-key coverage tests confirm both. A write takes `FOR SHARE` on the Connected System row first, so it waits for a deletion in progress and then writes nothing, rather than racing the cascade. Factory reset must delete scoped keys without touching the infrastructure key (Phase 4).

### The change feed

- Every write transaction **starts** by incrementing its Connected System's `LastChangeSequence` (an upsert with `RETURNING`, which creates the row on the first write) and holds that row lock until it commits. Commit order therefore equals sequence order, so no write can slip behind a watermark; a timestamp, or a sequence assigned before commit, would recreate the lost-change race this design exists to avoid. Taking the lock first rather than last means a Connected System's writes run one at a time end to end, so a write needs no other row locks and two writes cannot deadlock; writes to different Connected Systems never wait for each other. Proven on PostgreSQL by a reader that, in one snapshot, reads the horizon and everything at or below it while eight writers contend, then checks nothing ever committed at or below a horizon it had already read.
- A resource's `Version` rises once per write however many of its rows the write touches (its own row and its memberships), so one request is one version. Deleting a member raises the version of every Group it belonged to, in the same write, with the same change sequence.
- `meta.lastModified` is stamped from the same clock that emits the response `Date` header, after the lock, inside a short transaction (the shipped client's Delta Import keeps a 60-second safety margin).
- **Fallback:** if the per-system counter limits throughput during an identity provider's initial load, switch to an `xid8`/snapshot horizon. Measure before choosing.
- **Tombstones** are purged only at or below a recorded Delta Import watermark and older than a minimum age, advancing the purge floor to the highest change sequence removed (it never falls). A Delta Import whose watermark is below the purge floor fails with an Activity error asking for a Full Import; it never silently falls back. The purge exists from Phase 2; it is scheduled in Phase 3, once there is an import watermark to purge below.

### Write path

```
SCIM client ──POST /scim/v2/12/Users, Bearer jim_ak_...──▶ SCIM auth scheme
   hash lookup · enabled · not expired · scoped to system 12?   (no: 401, never a redirect)
SCIM endpoint ── Create(12, User, body) ──▶ SCIM server (JimApplication, the single write choke point)
   intake: refuse write-only attributes · validate against the schema · resolve references
   BEGIN · lock system 12's counter (sequence 4,813) · INSERT version 1 · unique userName? · COMMIT
SCIM client ◀── 201 Created · id · ETag W/"1" · Location
```

### Read path

- `GET /Users/{id}` and `/Groups/{id}` read the live row; tombstoned resources return `404`.
- **Filtering (day one):** data-driven from the resource type's definitions, so a custom type's attributes filter the same way. `eq`, `co` and `sw` on string attributes (`userName`, `externalId`, `displayName`, `emails.value`, an extension's), `eq` on Booleans (`active`), `id eq`, and `eq`, `gt`, `ge`, `lt`, `le` on `meta.lastModified`, joined by `and` and `or`; attribute names and operators case-insensitive, string comparison by each attribute's `caseExact`. Everything else (other operators, `not`, value paths, reference, read-only and write-only attributes, other types) is `400` `invalidFilter`.
- **Paging:** index paging (`startIndex`/`count`) by default, over id order with an accurate `totalResults`, plus RFC 9865 keyset cursors. `nextCursor` is returned on every page that has a successor, even when the request used `startIndex`, so the client's Auto mode switches to cursors; without that, deletes during a walk shift index pages and the client's absence-based Full Import would wrongly mark live resources Obsolete. `count` defaults to 100 and is capped at 1,000 (what discovery publishes as `filter.maxResults`); a negative `count` reads as 0, which returns only the total. When a request gives both `startIndex` and `cursor`, the cursor wins.
- `attributes` and `excludedAttributes` are honoured.

### Import path

- A new `IConnectorScimResourceStoreAware` interface and `IScimResourceStoreReader` in JIM.Models, implemented in JIM.Application over a repository and injected by `SyncImportTaskProcessor` beside the existing certificate provider and credential protection. This keeps the layering: JIM.Connectors never references JIM.Application.
- The connector throws if no reader was injected, because a silently empty Full Import would mark the whole Connector Space Obsolete.
- **Full Import** walks live resources by id at a horizon captured when it starts, and returns that horizon as its watermark. **Delta Import** reads `(watermark, horizon]`, returning the latest state of each changed resource and each tombstone as `ObjectChangeType.Deleted`.
- Reads are untracked, so a large store never enters the run's change tracker. The import loop reuses the client's `ScimResourceReader`, so the Connected System Objects look the same whether JIM pulled them or was pushed them.
- The connector watermark is recorded only after staging succeeds (the existing rule).
- **Decided 18 October 2026, for Phase 3:**
  - **The reader is bound to one Connected System** when it is handed to the Connector, at the import and at the schema refresh alike, since `GetSchemaAsync` is given settings and not the Connected System. Creating a Connected System with this Connector seeds its core definitions.
  - **The watermark is the store's last committed change sequence.** A Full Import captures it before reading and walks every live resource by id (it must not stop at that sequence, or a resource changed during the walk would look absent and be marked Obsolete). A Delta Import reads every row whose sequence is after the previous watermark and at or below a new one, paging by id within that range. A Delta Import with no watermark yet falls back to a Full Import with a warning, as the other Connectors do; one whose watermark is below the purge floor fails, asking for a Full Import.
  - **The tombstone purge runs in the Connector, at the start of each import,** removing tombstones at or below the previous recorded watermark (which only a successful import records) and older than seven days (a margin for a watermark set back by hand). It needs no schedule or worker task, and cannot remove anything an import has not read.
  - **Every import records the watermark it started from and the one it reached on its Activity,** in two generic columns filled for every Connector, not SCIM-specific ones: the journal lookup (D17) reads its range from them, and every Connector's Delta Import becomes diagnosable.

### Request pipeline for `/scim`

- Endpoint policy names the API key scheme explicitly, so challenges go there and never to the OpenID Connect redirect.
- Scope is checked from endpoint metadata and the `{connectedSystemId}` route value. As a second layer, the default authorisation policy requires an unscoped principal, so a scoped key can never satisfy a plain `[Authorize]` elsewhere.
- **Rate limiting** is partitioned per Connected System (not per key, so rotation keys share one budget), set by the Service Setting `Security.RateLimiting.ScimServiceProviderRequestsPerMinute` (default 1,500 per Connected System; "Service Provider" names the inbound role, so it cannot be mistaken for the SCIM client Connector's outbound throttling); unauthenticated `/scim` traffic is limited per address.
- A `/scim` exception mapper returns SCIM error JSON (`400` `invalidSyntax`/`invalidValue`, `404`, `500`); automatic ProblemDetails is suppressed on SCIM controllers.
- Secure transport is required, with a SCIM-shaped refusal; it is documented that a TLS-terminating proxy needs `JIM_TRUSTED_PROXIES`.
- `application/scim+json` and `application/json` are both accepted and produced on request; request size and complexity limits match what the shipped client sends.
- API key usage writes (last used time and address) are throttled for scoped keys, which see sustained traffic.

### Lifecycle and the Connector Space

| Event | Store | Next import and synchronisation |
|---|---|---|
| POST | New row, version 1 | Delta Import creates the Connected System Object; synchronisation joins or projects |
| PUT or PATCH | Version and change sequence increase | Delta Import updates the object |
| PATCH `active=false` | Ordinary attribute update | Flows to Account Enabled through Synchronisation Rules; downstream handling follows existing deprovisioning rules |
| DELETE | Tombstone; `userName` and `externalId` freed; memberships removed and affected Groups' versions increased in the same transaction | Delta Import marks the object Obsolete; Metaverse deletion rules apply |
| Re-create after DELETE | New row with a new id | New object joins the existing Metaverse Object through an Object Matching Rule, if it still exists; recommend a deletion grace period for SCIM sources to avoid churn |
| Clear Connector Space | Untouched | The next import must be a Full Import (watermark reset); it rebuilds the Connector Space from the store |
| Delete Connected System | Rows, state and scoped keys cascade | Endpoints answer `401` |

### Interoperability leniency

Accepted on input, each coercion logged without values:

- capitalised PATCH `op` values (`Replace`, `Add`, `Remove`);
- booleans sent as strings (`"False"`) on `active` and on `primary`;
- single-element arrays for single-valued attributes, and `""` treated as absent;
- `remove` on an unfiltered multi-valued path **with** a `value` array removes only the listed values (`remove` with no value still removes all). Strict RFC handling here would wipe a group's whole membership when the identity provider meant to remove one member;
- unknown and read-only attributes ignored (`groups`, `roles` on input, custom URNs not declared), undeclared ones named in the journal (D17);
- base URL with or without a trailing slash; unknown query parameters ignored;
- the credential probe some providers send on every cycle (`GET /Users?filter=userName eq "<random>"`) returns `200` with an empty list.

Product names stay out of the code and docs for these rules; they are described generically.

### Round trip with the shipped client

The JIM-to-JIM scenario must exercise everything the client relies on: discovery fields (`patch`, `filter`, `etag`, `bulk.supported=false`), the `/Schemas` characteristics it flattens on, index paging with cursors, the `meta.lastModified gt` delta filter, POST and PUT and PATCH with `If-Match`, value-less `replace` to clear, the uniqueness probe, the error classifier's status and `scimType` expectations, and a confirming import. `jim.web` needs HTTPS in the integration harness, because the client refuses plain HTTP to a non-loopback host.

## Non-Goals

- Push-triggered runs, immediate inbound processing, immediate export, retry and backoff (#123).
- `/Bulk` (advertised as unsupported until built), sorting, and the full filter grammar beyond the day-one subset.
- Exporting Metaverse values into the store (D12).
- Inbound passwords (D5) and a second authentication method (D4).
- Resetting the store (D13).
- SCIM Delta Query (see Future Work).

## Implementation Phases

### Phase 0: Prerequisites, each its own stacked PR beneath the feature

- API keys as `Authorization: Bearer` only ([#2088](https://github.com/TetronIO/JIM/issues/2088)).
- Run Profile deletion safeguards for Delta Import deletes ([#2089](https://github.com/TetronIO/JIM/issues/2089)); the Service Provider runs almost entirely on Delta Import.
- `JIM.Scim` multi-value writer fix ([#2090](https://github.com/TetronIO/JIM/issues/2090)).
- SCIM `password` exposure fix ([#2091](https://github.com/TetronIO/JIM/issues/2091)).

The other sub-issues ([#2092](https://github.com/TetronIO/JIM/issues/2092), [#2093](https://github.com/TetronIO/JIM/issues/2093), [#2094](https://github.com/TetronIO/JIM/issues/2094), [#2095](https://github.com/TetronIO/JIM/issues/2095), [#2096](https://github.com/TetronIO/JIM/issues/2096), [#2097](https://github.com/TetronIO/JIM/issues/2097)) are fixed during the work, in whichever order suits.

### Phase 1: `JIM.Scim` server primitives ✅

PATCH applier, shared filter grammar, server-mode reader and writer, ETag helpers, discovery emitters and the schema composer move (D6), with round-trip identity tests against the builder and the core schemas.

### Phase 2: SCIM store and service ✅

Three PRs:

- **2a ✅ Store.** Tables and indexes, resource type and schema definitions as data (D18), the write session (the counter lock, sequences, versions, data-driven uniqueness, tombstones, references), the journal (D17) and its retention, the tombstone purge, the Connected System deletion and factory reset coverage, and concurrent-writer tests on PostgreSQL proving commit order equals sequence order.
- **2b ✅ Server writes and Get.** Seeding the core definitions (with `userName` marked `uniqueness: server`, as RFC 7643 has it; `password` stays in the stored definition so intake can refuse it, and discovery leaves it out), building each resource type's `ScimResourceSchema` from the stored definitions, and the SCIM server in JIM.Application (create, replace, patch, delete, get) over the Phase 1 primitives, deriving unique values and references from the schema: intake and the write-only refusal, reference checks (D9), `409` for uniqueness and `412` for `If-Match`, journalling. Membership changes take a direct path: a PATCH that only adds or removes members (including `members[value eq "x"]`) becomes row inserts and deletes without loading the Group's members, so one member added to a Group of 100,000 reads none of them; any other PATCH loads the Group whole and runs the PATCH applier. The direct path is tested against the applier on the same inputs. As built:
  - **The direct path reuses the applier rather than re-implementing it.** Everything such a request can touch it names (an entry's `value`, or a `value eq` filter), so the server reads only the named references the Group holds, applies the request with the real applier to a resource holding just those, and turns the difference into row changes. A clear or replace becomes a replace of the attribute's rows. A differential test holds it to the whole-Group result over 500 generated requests.
  - **A write that changes nothing writes nothing:** no version, no journal entry, nothing for the next import. References compare as sets, so members re-sent in another order are not a change.
  - **A listed reference is matched by its `value` alone** when a remove names it: identity providers echo `display` and `$ref` back, which the stored entry never carries.
  - **A membership PATCH's response** reads the Group's members after the write lock is released, so a large Group is never read while other writes wait. The response can therefore include a later write's members; its version is this write's.
- **2c ✅ List, and ignored attributes in the journal.** The journal's ignored attribute names (D17): a column on `ScimStoreJournalEntries`, filled from intake's and the applier's reports of undeclared attributes. Filtering and paging. The day-one filter subset becomes a small predicate model in JIM.Models, which the repository renders as parameterised SQL, so JIM.Data never depends on JIM.Scim; anything outside the subset is `400` `invalidFilter`. Equality on a unique attribute (`userName`, `externalId`) reads `ScimStoreUniqueValues`; other attributes (`displayName`, `emails.value`) are matched in the JSONB payload, measured before any index is added. Index paging and cursors both order by `Id`; a cursor is the last id seen, encoded, and a malformed one is `400` `invalidCursor`. As built: every value, JSON path and member name in the rendered SQL is a parameter; a cursor is the last id as 22 base64url characters (unreserved URL characters, as RFC 9865 requires); a page's references are read in one query. A write that changes nothing is not journalled, so ignored attributes are recorded on the writes that change something.

### Phase 3: Service Provider Connector ✅

Introduces the feature flag and its gates (D16), in the same PR as the Connector's registration.

The store reader seam, the connector (Full and Delta Import), registration in `ConnectorFactory` and `SeedingServer.BuiltInConnectors`, capability flags (`SupportsFullImport` and `SupportsDeltaImport` true, `SupportsExport` false), `SchemaStandard => AttributeStandard.Scim` so the Attribute Flow editor offers only the SCIM Standard Mappings, which name the flattened attributes D6 shares ([#2095](https://github.com/TetronIO/JIM/issues/2095); the default, `NotSet`, mixes in LDAP hints), and import tests including tombstones, the purge-floor failure and the missing-reader failure. Also: scheduling the tombstone purge below the recorded watermark, and recording each import's change-sequence range on its Activity so the journal lookup (D17) can find the requests behind a change. As built:

- **The seam is `IScimResourceStore`** (JIM.Models), bound to one Connected System and implemented by `ScimResourceStoreService` in JIM.Application; JIM hands it to the Connector (`IConnectorScimResourceStoreAware`) wherever it creates one, so JIM.Connectors never reaches the data layer.
- **A resource is imported exactly as the endpoint would return it:** its references put back, projected through `ScimResourceProjection` (so `returned: never` stays out), then read by the client Connector's `ScimResourceReader` convention, so a resource becomes the same Connected System Object whichever side of the exchange JIM is on. The schema leaves out write-only attributes and `meta.location`, which the store has no base URL to form.
- **A Delta Import also fails when its watermark is ahead of the store's sequence**, since the store no longer matches what was imported from it; both failures ask for a Full Import.
- **The flag is `Features.ScimServiceProvider`** (In Development; its removal is [#2372](https://github.com/TetronIO/JIM/issues/2372)). `ConnectedSystemServer.FeatureFlagFor` is the one place that names the flag a Connector is behind, and every gate reads it: the Connector Definition list (portal, REST and PowerShell alike), creating a Connected System, and the import's start. The docs coverage test reads it too, so a Connector behind an In Development flag needs no public page until the flag reaches Preview.
- **Creating a Connected System with the Connector seeds its store** with the core definitions and, there being no settings to fill in, marks its settings valid so the Schema and Run Profiles tabs open straight away. A Connector Definition with no settings, and a Connected System with no setting values (schema import and the other Connector calls), are accepted only for a built-in Connector that declares none; runtime verification found schema import refusing the Connected System before that.
- **The Activity columns are `ImportWatermarkFrom` and `ImportWatermarkTo`**, the persisted connector data in whatever form each Connector keeps it. `To` is the watermark the run reached, kept even when it equals `From` (nothing new to read); it stays null when the run records none (it failed, or withheld detected deletions).
- **Verified on the full stack:** flag off hides and refuses the Connector over REST; flag on, a Connected System created over REST, its schema imported and Run Profiles run through the PowerShell module, staged what the SCIM server had written (a Group's member resolving to its User), a Delta Import read a deletion and a new User from change 3 to 5, and turning the flag off failed the next import with the reason.
- **Not surfaced yet:** the portal and the REST API do not show the two Activity columns. Whether and where to show them is open (see Phase 5).

### Phase 4: HTTP surface

Two PRs: **4a** scoped API keys (the scope, its enforcement, the per-Connected-System limits, minting at the application layer; the management surfaces are Phase 5), then **4b** the `/scim` surface itself.

The `ApiKeys` scope column and scoped key authentication with its failure semantics (D3), the journal's actor taken from the authenticated key (D17), the `/scim` pipeline stages, discovery, Users and Groups (POST, GET, PUT, PATCH, DELETE, list with filtering and paging), error mapping, rate limiting and secure transport, behind the flag (D16).

### Phase 5: Surface parity and documentation

- **Portal:** the Connected System page shows the base URL permanently, manages scoped keys (create, show once, rotate, revoke, last used, expiry), and shows a store summary (resource counts, last write, changes awaiting import) plus aggregated request rejections.
- **Portal:** the SCIM summary Activity per Connected System, key and window (D17), and on a Connected System Object's change history, the SCIM requests behind each imported change.
- **REST API and PowerShell:** the same key management and store summary, and the journal for a resource.
- **Definitions, read-only (D19):** `Get-JIMScimResourceType` and `Get-JIMScimSchema`, their REST endpoints, and a read-only view on the Connected System page.
- **Docs:** a connector page with per-provider setup (what to paste where), the recommended Schedule and the latency statement, the lifecycle table, and the "create a new Connected System" guidance; updates to the connectors index, architecture concepts, glossary and roadmap; changelog entries.

### Phase 6: Acceptance

The JIM-to-JIM integration scenario (both directions of the round trip, scheduled Delta Import and Delta Synchronisation to the Metaverse, a client Full Import proving deletes), leniency tests, then promotion of the flag from In Development to Preview. Its removal follows after a release in Preview (D16).

## Success Criteria

1. The shipped SCIM 2.0 Client Connector completes a paginated Full Import, a `meta.lastModified`-filtered Delta Import, and POST/PUT/PATCH exports with `If-Match` and a confirming import against this server.
2. Changes pushed to the store reach the Metaverse through scheduled Delta Import and Delta Synchronisation, with RPEIs and Activities identical in shape to any other import.
3. No SCIM request writes a Connector Space; concurrent-writer tests show no change slipping behind an import watermark.
4. A key scoped to one Connected System is refused on every other system's endpoint and on every non-SCIM path.
5. A pushed `password` is refused and appears nowhere in the store, logs or Activities.
6. Clearing the Connector Space and running a Full Import restores it from the store without the identity provider re-pushing.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The change feed is the one new integrity-critical mechanism | Per-system counter row with commit-ordered sequence; concurrent-writer tests on PostgreSQL |
| The counter serialises writes per system during a large initial load | Measure; fall back to an `xid8` snapshot horizon if needed |
| Administrators expect real-time provisioning | State the Schedule-bound latency plainly in docs and the portal; #123 adds triggers later |
| Tombstones purged before an import reads them lose deletes | Purge only below a recorded watermark and minimum age; a Delta Import below the purge floor fails loudly |
| Duplicate storage (store payload plus Connector Space) | Accepted: the store is the full record and the Connector Space a projection; documented |
| Activity volume from frequent Schedules | Retention already exists; empty-run suppression belongs to #123 |
| Administrators put this system's import and synchronisation in one parallel step group | Documented warning; the underlying watermark race is fixed separately ([#2092](https://github.com/TetronIO/JIM/issues/2092)) |
| Group membership at scale | Child table for members; tests for large groups and for User deletion removing memberships |
| JIM.Web's database connection pool shared with the portal | Per-system rate limits and short transactions; measure under load |

## Testing Strategy

- **Unit:** `JIM.Scim` primitives with round-trip identities (`Apply(Build(x))`, `Read(Write(x))`, `Parse(Build(f))`), intake rules, authentication outcomes, error mapping.
- **Database:** change-feed ordering under concurrent writers, uniqueness, tombstone purge, deletion sequence coverage.
- **Worker:** Service Provider Connector imports, including Deleted handling and purge-floor failure.
- **HTTP:** SCIM conformance and leniency through the real pipeline (authentication, routing, content types, status codes).
- **Integration:** the JIM-to-JIM scenario. Keycloak-plugin and other third-party client scenarios need dependency approval and come later.

## Future Work

- **Custom resource types and schema extensions** (D18, D19): administering them per Connected System (portal, REST, PowerShell, configuration export and import), the definition change pipeline, and importing them. The store already holds them as data, so this needs no change to its tables. Starts with the D19 design pass.
- **Second authentication method** (D4), when a customer needs it.
- **SCIM inbound password channel** ([#2099](https://github.com/TetronIO/JIM/issues/2099)).
- **Reset SCIM store** ([#2098](https://github.com/TetronIO/JIM/issues/2098)).
- **Push-triggered runs** and immediate processing (#123).
- **SCIM Delta Query** (publishing a change feed to downstream pullers via `draft-sehgal-scim-delta-query`). Deferred, not planned: the draft is an expired individual Internet-Draft never adopted by the working group, no client ecosystem consumes `/.delta`, and it needs durable change-feed retention, token issue and expiry, and per-resource authorisation re-checks on replay. Revisit on working-group adoption plus concrete demand. The store's change sequence would be the natural basis.

## References

- [RFC 7643, SCIM Core Schema](https://www.rfc-editor.org/rfc/rfc7643)
- [RFC 7644, SCIM Protocol](https://www.rfc-editor.org/rfc/rfc7644)
- [RFC 9865, SCIM cursor-based pagination](https://www.rfc-editor.org/rfc/rfc9865)
- [RFC 6750, Bearer Token Usage](https://www.rfc-editor.org/rfc/rfc6750)
- [`../done/SCIM_CLIENT_CONNECTOR_DESIGN.md`](../done/SCIM_CLIENT_CONNECTOR_DESIGN.md)
- [`../done/OUTBOUND_SYNC_DESIGN.md`](../done/OUTBOUND_SYNC_DESIGN.md), Event-Based Sync Roadmap
- [`../../notes/SCIM_TEST_PROVIDER_ANALYSIS.md`](../../notes/SCIM_TEST_PROVIDER_ANALYSIS.md)
