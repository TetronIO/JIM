# Microsoft Graph Connector (Entra ID) Design Document

- **Status:** Doing (Phases 1 to 9 complete; Scenarios 036 and 037 pass against the test tenant)
- **Issue:** [#361](https://github.com/TetronIO/JIM/issues/361) (the issue is the requirements source, in effect the PRD; this document is the implementation plan)
- **Related Issues:** #545 (SCIM 2.0 Client Connector, whose HTTP stack this reuses), #1145 (Source of Authority conversion as an export operation), #360 (pronouns), #192 (generic REST connector), #2367 (Entra ID sign-in, which gave JIM its Entra ID test tenant)
- **Related Plans:** [`SCIM_CLIENT_CONNECTOR_DESIGN.md`](../done/SCIM_CLIENT_CONNECTOR_DESIGN.md), the closest precedent: an HTTP connector built in phases with seeding held back until the last one
- **Last Updated:** 2026-11-22

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

- **Client secret:** simplest, for a quick trial; stored as `StringEncrypted`.
- **Certificate:** the default, as Microsoft Entra Connect now uses (2026-11-21). The PFX, Base64-encoded, in a `StringEncrypted` setting, with an optional encrypted Certificate Password; used to sign a client assertion. JIM has no private-key certificate store today (its certificate store holds trusted public certificates only), and the `File` setting type holds a path rather than content, so the PFX lives on the Connected System's settings, encrypted at rest with every other credential. Saving the settings refuses a PFX that will not open, has no private key, is not RSA of at least 2,048 bits, or is outside its validity period, and warns within 30 days of expiry.
- **Federated credential:** for JIM running on an Azure virtual machine (under Docker Compose or Podman, the deployments JIM supports) with a user-assigned managed identity attached, so no secret or certificate exists anywhere. The app registration trusts the identity's token through a federated identity credential, and the Connector sends that token as the client assertion in place of one it signs itself. The token comes from the instance metadata service, for the cloud's token-exchange audience (`api://AzureADTokenExchange`, `api://AzureADTokenExchangeUSGov` or `api://AzureADTokenExchangeChina`). Only a user-assigned managed identity can be an app's federated credential, so its client ID is a setting. Tokens cannot cross clouds.
  On-premises and air-gapped deployments have no issuer Entra ID can reach, so certificates remain the default; JIM publishing its own signing keys would be certificate authentication with an internet-facing endpoint added.
- **Delegated (authorisation code) flow: dropped.** The issue lists it, but the connector runs unattended in the Worker; there is no user present to sign in, and a refresh token stored for a person is a worse credential than an app's.
- **National clouds:** a Cloud drop-down (Global, US Government L4, US Government L5 (DoD), China) sets the authority and Graph host. The audiences JIM serves include government.

Permissions are documented least-privilege per capability: `User.Read.All` and `Group.Read.All` to import; `User.ReadWrite.All` and `Group.ReadWrite.All` to export; `User-OnPremisesSyncBehavior.ReadWrite.All` and `Group-OnPremisesSyncBehavior.ReadWrite.All` only where Source of Authority tracking is turned on (decision 19). The connectivity test reads the token's `roles` claim and names any missing permission rather than letting the first import fail with a 403.

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
- **Every request is safe to send again** (Phase 7 design, 2026-11-21). JIM records one result per Pending Export, and a failed one is sent again whole, so each step must be idempotent: a member already present counts as added (Graph's 400 "One or more added object references already exist"), a member or object already gone counts as removed or deleted (404), and a PATCH simply reapplies. References arrive as the target's External ID (its Graph `id`), resolved by JIM before the call.
- **A create carries everything it can, and reports its id however its follow-ups fare.** `POST /groups` takes the first 20 members and owners as `members@odata.bind` and `owners@odata.bind`; further members, and a user's manager (`PUT /users/{id}/manager/$ref`), follow in later batch rounds. Once the create has succeeded its id is returned even if a follow-up failed, since sending the create again would make a second object; a follow-up that failed is logged and left for the confirming import, which finds it unwritten and stages it again.
- **A new user's password** (`passwordProfile`, which Graph requires) is generated by the Connector with `RandomNumberGenerator`, never stored, logged or returned, with `forceChangePasswordNextSignIn`. Nobody knows it: the user signs in through single sign-on, passwordless methods, or a Temporary Access Pass an administrator issues. JIM's Initial Password and Password Synchronisation reach Entra ID through Phase 7b (decision 17), which replaces this password with the real one moments later.
- **Graph's ambiguous 400s are told apart by their message.** A member add answers 400 both for a member already present and for an object not yet replicated ("one of the objects being referenced don't exist"); the first is success, the second `MissingDependency`, which JIM retries. A write to an attribute a synchronised object takes from Active Directory ("on-premises mastered Directory Sync objects") is `RejectedByConnectedSystem`, with a message telling the administrator to scope the export Synchronisation Rule on `sourceOfAuthority`. 401 and 403 are `AccessDenied`.
- **Deprovisioning** follows the export Synchronisation Rule's Deprovisioning Action (#2085), which replaced a Delete Behaviour setting on the Connector: Delete (Entra ID keeps a deleted user or Microsoft 365 group for 30 days) or Disable, which sets `accountEnabled` to false on a user. A group has nothing to disable, so a Disable of a group is refused before it is sent.

### Capability flags

| Capability | Value | Rationale |
|---|---|---|
| `SupportsFullImport` | `true` | Core. |
| `SupportsDeltaImport` | `true` | Graph delta queries; the delta link is the watermark. A round names what changed, and each changed object is read again for its current state (decision 15). An expired delta link (`410 Gone` / `syncStateNotFound`) falls back to a full import with a warning, matching the LDAP precedent. |
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
4. **Schema.** ✅ Curated user and group schema; directory extension discovery; `onPremisesExtensionAttributes`; `sourceOfAuthority` and its supporting attributes always present.
5. **Full import.** ✅ Users and groups, paging, membership as references, Source of Authority synthesis including the cloud-managed query.
6. **Delta import.** ✅ Delta links as watermark; each round's changed objects read again in full and its removed objects staged as deletions; a full read with a warning when a link is missing, expired or refused, the selection has changed, or (with tracking on) objects' Source of Authority has been switched or tracking turned on or off. Hidden group membership guarded in both imports (decision 14).
7. **Export.** ✅ `$batch` in rounds (first steps, then follow-ups), create with a generated password and up to 20 members and owners bound, update (a multi-valued property sent whole), delete or disable, membership and manager by `$ref`, every step idempotent, Graph's ambiguous 400s classified; deprovisioning by the rule's Deprovisioning Action (#2085).
7b. **Passwords.** ✅ `IConnectorPasswordManagement` and `IConnectorPasswordPolicyDiscovery` (decision 17): `PATCH /users/{id}` with `passwordProfile`, `passwordPolicies` for "never expires", `accountEnabled` when the rule enables accounts; Entra ID's fixed rules (8 to 256 characters, 3 of 4 categories, each domain's expiry, an unreadable banned-password list) as the discovered policy; a channel check that reads the token's permissions and the app's tenant-wide directory roles (`wids`), reporting "could not determine" when the role is not in the token (decision 20). Tests against the fake tenant. Docs on the Connector page and the Passwords concept page land with enablement (Phase 9).
8. **Federated credentials.** ✅ A third Authentication Method, Federated Credential, with an Azure user-assigned managed identity as its source (above; decision 18). The instance metadata service is reached over link-local HTTP, the one cleartext address the Connector permits, hard-coded rather than configurable. Unit tests against a stub metadata service; a manual run on an Azure VM before enablement.
9. **Enablement.** Seeding, the connector page (`docs/connectors/jim-graph-connector.md`) with the app registration, permissions, and the internet-access exception, changelog, and an integration scenario against the JIM test tenant. The setup command (`Register-JIMEntraIdConnector`): browser sign-in by default through the shared helper from #3021, `-AccessToken`, `-UseDeviceCode` by request, or a printed definition and admin-consent link for a tenant administrator to apply; creates or repairs the app registration with least-privilege permissions (password management only on request, adding `User-PasswordProfile.ReadWrite.All` and the User Administrator role across the tenant, per decision 20; including `Member.Read.Hidden` by default, which `-SkipHiddenMembership` leaves out; the docs say what it exposes: hidden groups' members become visible to anyone who can view them in JIM, and leaving it out is safe, since those memberships are reported unreadable and left as JIM holds them), grants consent, generates the certificate in memory (or adds the federated credential), and creates the Connected System. Manual setup steps in the docs for organisations that cannot run it.

## Testing Strategy

- TDD throughout. All HTTP behaviour against a stateful mock Graph service in unit tests; no network.
- **Integration against a real tenant.** Planned in detail in [`ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md`](ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md): Entra ID as a target (Scenario 036) and as a source (Scenario 037) in the JIM test tenant, run in the AD lab, and hybrid Source of Authority (Scenario 038, #3029) in a dedicated tenant.
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
5. **The Connector is named "JIM Microsoft Entra ID Connector"** (2026-11-21), after the system administrators connect to rather than the API it speaks. Free to change until Phase 9 seeds it; after that the name is part of the Connector's identity.
6. **The mock Graph service grows with the phases** (2026-11-21). Phase 3's tests drive the client through a stub transport with throttling, token refusal, Graph error and permission switches; the partial-batch and eventual-consistency switches arrive with export (Phase 7), where they have something to exercise.
7. **TLS certificate diagnosis is deferred to Phase 9** (2026-11-21). Graph's certificates chain to a public CA, so only a TLS-inspecting proxy causes a trust failure; Phase 3 reports that case with a message pointing at Admin > Certificates. Phase 9 wires in the shared `ServerCertificateDiagnosis`, which shows the presented certificate as the SCIM and LDAP Connectors do. ✅ Done (2026-11-23): the Connector resolves Entra ID's token service as its secure endpoint, and the probe reads an HTTPS endpoint through the outbound proxy (`HTTPS_PROXY`, in a `CONNECT` tunnel) as `HttpClient` reaches it, since a direct read behind a non-transparent proxy is blocked or sees Microsoft's real certificate. The SCIM Connector reads its endpoint the same way.
8. **Directory extension discovery is optional** (2026-11-21). `getAvailableExtensionProperties` needs `Directory.Read.All`, which the least-privilege set leaves out. Without it the curated schema stands and the schema carries a warning naming the permission; any other discovery failure fails the refresh.
9. **Certificate is the default authentication method** (2026-11-21), as Microsoft Entra Connect now authenticates; a client secret is for a quick trial.
10. **Federated credentials ship in the first release** (2026-11-21), as Phase 8, rather than as a follow-up.
11. **Setup is one command, with no multi-tenant app** (2026-11-21). A Tetron-owned multi-tenant app was considered and rejected: app-only access would need one credential shared by every customer, and it would not exist in the national clouds. The setup command creates a single-tenant app in the customer's own tenant instead, signing the administrator in through the browser rather than with a device code, which Microsoft recommends Conditional Access blocks (#3021).
12. **Full import reads list endpoints, not a delta round** (2026-11-21). Users and groups delta support neither `$top` nor `$expand`, and spread a large group's `members@delta` over several pages, so a delta full import would have to hold every group until the round ended. Full import reads `/users` (with `$expand=manager($select=id)`) and `/groups` with `$select` and `$top=999`, and each page's group members through `$batch`, 20 groups a call, so every group is emitted complete within its page. The delta baseline is taken at the start of each object type with `$deltatoken=latest`, so changes made during the import are read by the next delta.
13. **An unreadable relationship is an attribute value error, not a failed object** (2026-11-21). When a group's members or owners cannot be read in full, the group is still imported with that attribute reported unreadable, so JIM keeps the membership it holds (#2066) and the group stays protected from deletion; a partial membership would have been read as removals.
14. **A hidden membership is not read without `Member.Read.Hidden`** (2026-11-21). Graph's documentation requires the permission to list a hidden membership but does not say what it answers without it; an empty list would read as a group with no members and remove every member JIM holds. Both imports select each group's `visibility` and, without the permission, report a hidden group's `members` unreadable (decision 13) without asking for them. The connectivity test warns when the permission is missing, and gaining or losing it sends the next Delta Import to a full read, since memberships it could not read were never baselined.
15. **A Delta Import reads each changed object again, rather than staging the round's payload** (2026-11-21). JIM stages a changed object whole, reading an attribute it does not carry as one with no values, and a round carries only what changed of a relationship (`members@delta` adds and removes), so a group's payload would be read as having lost every unchanged member. The whole round is read first (ids only), then each changed object is read again through `$batch` by the Full Import's own staging path, so it is read once however many pages the round reports it on, and as it stands after the link was issued. A removed object (`@removed`, either reason) is staged as a deletion by its id; its last appearance in the round decides. An object Graph cannot yet find (replication delay) is read again after 2, 5 and 10 seconds, then fails the run rather than be staged as deleted or have its change lost when the link advances. The cost is that a group whose membership changes is read whole, as the LDAP Connector does; applying `members@delta` directly would need JIM to accept value-level changes from a Connector, which it does not today.
16. **A Delta Import reads every object when its link cannot be trusted** (2026-11-21), decided for every Object Type before the first page, since JIM holds a run to its first page's answer. The persisted state keeps, per Object Type, the link, the selection it was taken to track (a round tracks only what its first request selected), and a hash of the set of cloud-managed objects; moving an object to or from cloud management changes nothing a round reports, so a changed hash means a full read. The set is held as a hash because JIM records the persisted state on every import's Activity.
17. **Password management ships in this release, as Phase 7b, and is opt-in at setup** (2026-11-21). The Connector implements JIM's existing password interfaces, so Initial Password, Set Password, propagation, the queue and the Password Delivery Service reach Entra ID unchanged. Graph's app-only password reset needs `User-PasswordProfile.ReadWrite.All` **and** at least the User Administrator directory role, which is privileged, so the setup command grants both only when asked (scoped to an Administrative Unit here at first; superseded by decision 20); reading and writing users and groups never needs it. Administrators' passwords stay out of reach (Privileged Authentication Administrator, which JIM does not ask for), and a password aimed at a user synchronised from Active Directory parks with Graph's reason: those are set through the LDAP Connector. Explainer: https://claude.ai/artifact/3tF766egFfoPG25y7zGyiK
18. **Federated credentials share the client credentials flow, through an async credential, from the instance metadata service only** (2026-11-21). The shared `IOAuthClientCredential` became asynchronous, since a federated credential's token is fetched for each token request; the client secret and certificate credentials are unchanged in behaviour. The token comes from the instance metadata service, never through a proxy, for the cloud's audience. A token file source (Kubernetes workload identity) and the platform endpoint App Service, Container Apps and Functions provide (`IDENTITY_ENDPOINT`) were built and then removed before seeding: JIM supports neither Kubernetes nor those platforms (`docs/administration/podman.md`), removing a setting after release would need a deprecation and a major version while adding one later is additive, and the platform endpoint was the one place a credential would have gone over plain HTTP to an address read from the environment. Either can return as a further `IGraphFederatedTokenSource` if JIM comes to support such a platform. Still owed before enablement: a manual run on an Azure VM, including confirming that containers under Docker and rootless Podman reach the metadata service.
19. **Source of Authority tracking is opt-in, and every capability switch keeps what the setup already does** (2026-11-21). Microsoft offers no `OnPremisesSyncBehavior.Read.All` permissions, as this plan first assumed (the setup's first real-tenant run refused the name): only `User-OnPremisesSyncBehavior.ReadWrite.All` and `Group-OnPremisesSyncBehavior.ReadWrite.All` exist, and they also allow switching an object's Source of Authority. So the cloud-managed query runs only with the new **Track Source of Authority Changes** setting (Import, default off), and the setup command grants those permissions only with `-TrackSourceOfAuthority`. Off, `sourceOfAuthority` still comes from `onPremisesSyncEnabled`, `userType`, `creationType` and `externalUserState`; only an object switched by `isCloudManaged`, in either direction, reads wrongly, and the docs say so. Re-running `Register-JIMEntraIdConnector` without a capability switch keeps what is granted (and a ticked setting counts as asking for tracking, so enabling it in the portal and re-running grants the permissions); a switch given as false turns it off. The docs frame Source of Authority for administrators: objects JIM writes through Graph are cloud objects; it is not Attribute Priority; two routes (this Connector for cloud objects, the LDAP Connector for Active Directory ones); export rules scope on a Metaverse Attribute fed from `sourceOfAuthority`, with Disconnect as the Deprovisioning Action. A Synchronisation Rule warning was considered and dropped: rules are Connector-agnostic. Still owed: a hybrid tenant check of what `onPremisesSyncEnabled` reads after a switch back to Active Directory.
20. **No Administrative Units: password management assigns User Administrator across the tenant** (2026-11-22). Supersedes decision 17's scoping of the role to an Administrative Unit, which was never agreed and made Administrative Units part of the Connector's core when they are an optional Entra ID feature for delegating administration. It was also broken: the Connector never adds a user it creates to the unit, so the scoped role could not set that user's password. The Connector creates, reads, updates and deletes users and groups, with no Administrative Unit anywhere. Password management stays opt-in: `-PasswordManagement` grants `User-PasswordProfile.ReadWrite.All` and assigns User Administrator across the tenant (help, docs and `-WhatIf` say what the role allows), and `-PasswordManagement:$false` revokes the permission and removes the role. A re-run that leaves the switch out keeps both and does not read role assignments. Passwords still travel only through the password channel after a create, never in the create request, since exports are persisted, previewed and retried. Import limited to Administrative Units is a possible later add-on (#3028).
