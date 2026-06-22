# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- ✨ The PowerShell module now persists your interactive SSO sign-in across terminal sessions: after `Connect-JIM`, opening a new terminal reconnects silently without a browser. Only the refresh token is stored, in the operating system's credential store (Credential Manager on Windows, login Keychain on macOS, libsecret on Linux), with no extra password beyond your normal OS sign-in. Use `Connect-JIM -NoPersist` to opt out for a session, `-Force` to re-authenticate and overwrite the stored token, and `Disconnect-JIM` to remove the stored token for the current instance (`-Url` for a specific instance, `-All` for every instance). Headless Linux without a keyring falls back to in-memory tokens and points you to `-ApiKey`.
- ✨ Factory reset is now available in the portal: a new Administration danger area (`/admin/factory-reset`) with a backup warning, type-to-confirm, and an optional "delete administrators" path.
- ✨ The initial administrator can now be provisioned on first contact via the PowerShell module or REST API, not just the web portal. On a fresh deployment, the configured initial admin's first authenticated call (CLI or portal) just-in-time creates their identity and grants the Administrator role, so an edge or air-gapped instance can be administered entirely from the command line without ever opening the portal. Profile attributes from a CLI-only bootstrap are backfilled on a later portal sign-in.

### Changed

- 🖥️ The Synchronisation Rule editor is now organised into tabs (Details, Matching, Scope, Attribute Flow, Danger Zone) instead of stacking everything on one page, each deep-linkable via `?t=<tab>` so a specific section can be bookmarked or shared. The Attribute Flow controls and table now sit in their own panel rather than floating on the page background. A single "Create/Update Sync Rule" save bar stays visible beneath every tab; you can fill in any tab in any order and the whole rule still saves in one action (a new rule is created complete, not in stages).
- 🖥️ The Connected System Schema tab is now organised into sub-tabs instead of one long page. An "Object Types" tab presents a searchable, filterable grid for choosing which object types JIM should manage (each showing its attribute count, with an All / Selected filter), and every selected type gets its own tab where you configure its attributes. Removing a type from management now asks for confirmation first, so a fully configured type cannot be dropped by accident. This stays usable when a system exposes dozens or hundreds of object types (for example, a full LDAP schema), where the previous checkbox grid grew unwieldy.
- 🖥️ Connected System settings that only apply in certain configurations are now hidden until they are relevant, and required once they are. In the LDAP Connector, Certificate Validation appears (and must be set) only when "Use Secure Connection (LDAPS)?" is enabled, and Disable Attribute appears (and must be set) only when Delete Behaviour is "Disable". Connectors declare these conditional settings in their metadata and JIM enforces them generically, both in the settings form and server-side for API callers.
- 🔄 The REST API now rejects an invalid Connected System settings update (`PUT /synchronisation/connected-systems/{id}` with `settingValues`) with HTTP 400 and a per-setting list of what failed and why, instead of silently saving the change and only flipping a `settingValuesValid` flag. The PowerShell module surfaces these field-level messages, so `Set-JIMConnectedSystem` now tells you exactly which settings are wrong (for example, supplying both mutually exclusive object-type settings, or omitting a setting that another setting's value makes required). Updates that do not touch settings are unaffected.
- 🔄 JIM now requests the `offline_access` scope during interactive authentication so the identity provider issues a refresh token. This enables reliable in-session token renewal and the new PowerShell token persistence. Existing SSO deployments should ensure `offline_access` is permitted on the interactive/public client; see the updated SSO setup guide.
- 🔄 Factory reset now **preserves administrator users by default** (so you are not locked out) and always records a Reset activity attributed to whoever initiated it. The previous behaviour of also removing administrators is available via the new `-IncludeAdministrators` switch on `Reset-JIMSystem` (and `includeAdministrators` on `POST /api/v1/system/reset`), guarded against lockout when no initial administrator is configured.
- 🔄 The reconnection overlay now shows live attempt progress (for example, "Attempt 2 of 5...") while JIM re-establishes a dropped connection.
- 🔄 Running a PowerShell cmdlet before connecting now shows a clear one-line message telling you to run `Connect-JIM -Url <your JIM URL>`, instead of a raw error referencing the module's internals; the not-connected state is non-terminating by default and can be made fatal in scripts with `-ErrorAction Stop`.
- 🔄 The "not authorised" message shown when an authenticated user lacks a JIM identity now explains that identities arrive via synchronisation or administrator provisioning, rather than directing users to sign in to the web portal first (which no longer reflects how identities are created).

### Fixed

- 🐛 Editing an existing Synchronisation Rule in the portal now saves. Changes such as disabling a rule appeared to save (no error, the list still showed it enabled) but were silently discarded, because the editor loaded the rule and saved it on two different database sessions, so the save had nothing to persist. The editor now keeps a single session for the whole edit, and the save layer fails loudly rather than silently if ever handed a rule it is not tracking. Covered by new database-backed regression tests.
- 🐛 Creating a Synchronisation Rule from scratch in the portal no longer fails, and the page now switches into edit mode once the rule is created (the heading, breadcrumb and Update button update, and the just-saved values stop being reported as missing) instead of still showing "New" with spurious validation errors. The save previously tried to insert the rule with unset foreign keys (raising a database foreign-key violation), and for a rule that already had matching rules, attribute flow or scoping criteria it would also have attempted to duplicate the referenced attributes, so a brand-new rule could not be saved at all. The create path now reduces every reference to an already-existing entity (connected system, object types, and the attributes used by matching, attribute flow and scoping) to its foreign key before inserting; scoping criteria gained explicit attribute foreign-key properties to make this uniform. Covered by new database-backed regression tests.
- 🐛 The synchronisation rule expression tester now resolves attribute names case-insensitively, exactly as live synchronisation does, so an expression that works during a sync run no longer reports "no result" in the tester purely because an attribute name's casing differs.
- 🐛 Connected System settings now enforce either/or requirements at save time instead of failing later. The File Connector requires exactly one of "Object Type Column" or "Object Type"; previously you could save settings with neither (only discovering the problem when a schema refresh errored out) or with both (in which case JIM silently ignored the column and used the fixed type). The settings form now groups the two fields together, shows live feedback until exactly one is supplied, and disables saving otherwise; the same rule is also enforced server-side for API callers. Connectors can now declare these setting groups in their metadata, as either "at least one of" or mutually exclusive "exactly one of", and JIM validates and renders them generically.
- 🐛 Deleting a Connected System no longer fails with a database error. The bulk-deletion SQL referenced sync-rule-mapping columns removed in an earlier schema change (`column ... does not exist`), and did not remove the system's object matching rules before the sync rules and object types they reference (foreign key violation). Both are fixed, and the deletion is covered by new database-backed tests.
- 🐛 Deleting a Connected System that has been synchronised no longer fails with a foreign-key database error, and the deletion is now atomic: it either completes fully or rolls back, rather than leaving the system behind with its objects already removed. Run profiles, partitions, activities, metaverse change history, and metaverse attribute values contributed by the system are now removed or detached in the correct order. Metaverse attribute values contributed by the deleted system are retained with their contributor link cleared (attribute recall remains a synchronisation-time behaviour).

### Removed

- 🗑️ The `SyncRuleMappingSourceParamValues` and `ObjectMatchingRuleSourceParamValues` database tables have been dropped. Both were dead legacy remnants of an earlier function-based mapping design that was superseded by expression-based mappings; neither was ever populated in the application.

### Security

- 🔒 A factory reset now invalidates every existing portal sign-in session, so no stale access or privileges survive the wipe; users must re-authenticate. API key access is unaffected.
- 🔒 The REST API now rejects request bodies containing duplicate JSON property names, removing an ambiguous-parsing and request-smuggling vector.

## [0.11.0] - 2026-06-06

### Added

- ✨ Create custom Metaverse Object Types via the API and the new `New-JIMMetaverseObjectType` cmdlet, to model identity types beyond Users and Groups.
- ✨ Scoping criteria now support long-integer and case-sensitive comparisons via the API and `New-JIMScopingCriterion`.
- ✨ Sync rules can now set their out-of-scope and deprovisioning actions and drift detection via the API and `Set-JIMSyncRule`.
- ✨ New factory reset (`Reset-JIMSystem` / `POST /api/v1/system/reset`) wipes all customer data and configuration in one transaction while preserving the schema, built-ins, and infrastructure access.

### Fixed

- 🐛 Refreshing a Connected System's schema now persists the discovered object types and attributes, so the selection interface appears immediately instead of reading back empty.
- 🐛 Outbound deprovisioning no longer fails with a duplicate-key error when the target object still has a pending export from a prior run.
- 🐛 Adding scoping criteria to an existing sync rule via the API no longer fails to save.

### Changed

- 🔄 JIM is now distributed under the Tetron Software License Agreement v2.0.

## [0.10.3] - 2026-05-10

### Added

- ✨ Metaverse Object change history is now available via the API and PowerShell module: new `GET /api/v1/metaverse/objects/{id}/change-history` endpoint returns paginated change records, and the new `Get-JIMMetaverseObjectChangeHistory` cmdlet wraps it for automation and compliance scenarios.
- ✨ Connected System Object change history is now available via the API and PowerShell module: new `GET /api/v1/synchronisation/connected-systems/{id}/connector-space/{csoId}/change-history` endpoint returns paginated change records, and the new `Get-JIMConnectedSystemObjectChangeHistory` cmdlet wraps it for automation and compliance scenarios.

### Performance

- ⚡ Metaverse Object detail pages load substantially faster on objects with long change histories: the page no longer materialises the entire change graph upfront, fetching only a count alongside the object and loading change rows on demand when the Changes tab is opened.
- ⚡ Connected System Object detail pages load substantially faster on objects with long import histories: the page no longer materialises the entire change graph upfront, fetching only a count alongside the object and loading change rows on demand when the Change History tab is opened.
- ⚡ Connector Space list pages load substantially faster: the per-page projection no longer materialises full pending-export graphs or attribute-value entities, returning only the scalar columns the table actually renders.

### Fixed

- 🐛 Export Run Profile Execution Items and their linked Connected System Object Change rows now persist with the correct `ConnectedSystemObjectId` foreign key, restoring causality navigation from Operations into the CSO detail page and preventing exported objects from being mis-labelled as "Deleted" on the activity item detail page (#683).
- 🐛 Pending-export reference values in the Causality Tree attribute change table now render the resolved identifier (e.g. group member DN) alongside a clickable link to the stub Connected System Object, instead of showing only a clock icon with no value.

### Changed

- 💄 The Activity Run Profile Execution Item detail page no longer duplicates the Connected System Object's external ID in the Execution Summary prose; the identifier is already shown as a chip directly below.

## [0.10.2] - 2026-04-29

### Added

- ✨ Predefined Searches can now be retrieved individually via the API and PowerShell module: new `GET /api/v1/predefined-searches/{id}` and `GET /api/v1/predefined-searches/by-uri/{uri}` endpoints return the full search graph, and `Get-JIMPredefinedSearch -Id` / `-Uri` now resolve directly against the server instead of filtering the list client-side (#154)

### Fixed

- 🐛 The "Initiated By" link on Activity and Activity Run Profile Execution Item detail pages now points to the correct Metaverse Object URL, derived dynamically from the initiator's Metaverse Object Type plural name (`/t/{typePluralName}/v/{id}`) instead of a broken hardcoded `/identity/person/{id}` path.
- 🐛 Safari sign-in against the development stack at `http://localhost:5200` no longer fails with `Correlation failed`; OIDC correlation cookies are now configured appropriately for plain-HTTP localhost in Development while production HTTPS defaults remain untouched.
- 🐛 The bundled "Users & Groups" example data template now persists at production speed without stalling the worker or pressuring memory; generation has been rewritten to use PostgreSQL `COPY` binary import in bounded batches, mirroring the proven pattern used on the synchronisation hot path.
- 🐛 Filled alerts in the `navy-o6` themes now meet WCAG AA contrast: light-theme info/success/warning/error variants and dark-theme filled info no longer place dark text on saturated backgrounds, and links inside filled alerts pick up the on-colour text colour rather than clashing with the semantic background.

### Changed

- 💄 Example data generation now reports live, batch-level persistence progress with a rolling ETA on the Activity record and progress bar, so administrators can see exactly where a large generation run is up to.
- 💄 Compact row spacing on the Metaverse Object detail Table view now extends to multi-valued reference rows (e.g. group Owners, Static Members), keeping large memberships readable at a glance.
- 🖥️ Refreshed the JIM portal and documentation typography to IBM Plex Sans and IBM Plex Mono, with a Space Grotesk accent on docs hero surfaces and the portal sidebar wordmark, for sharper identifier disambiguation and a more polished, designed feel across the product.
- 🖥️ The production error page now renders in the JIM brand (broken-cog illustration, Plex / Space Grotesk fonts, navy-o6 palette), honours the user's saved dark-mode preference and `prefers-reduced-motion`, and runs without a Blazor circuit so it remains reachable when middleware throws.
- 🛠️ `jim-reset` now stops any natively-run JIM.Web/Worker/Scheduler processes before tearing down the Docker stack, preventing port collisions (e.g. host port 5200) when the Docker stack is restarted after a `jim-build-light` debug session.

## [0.10.1] - 2026-04-27

### Added

- ✨ Interactive browser-based SSO for the JIM PowerShell module now works against identity providers that require a separate public client registration for desktop/CLI tools, including Keycloak. Two new optional environment variables let administrators advertise client-facing SSO configuration to interactive clients without affecting backend token validation: `JIM_SSO_PUBLIC_AUTHORITY` for deployments where the backend and clients reach the identity provider on different URLs (split-horizon reverse proxies, development containers), and `JIM_SSO_PUBLIC_CLIENT_ID` for deployments where the PowerShell module's public OAuth client is a distinct registration from the web application's confidential client. Both variables are optional and fall back to `JIM_SSO_AUTHORITY` / `JIM_SSO_CLIENT_ID` respectively, so single-URL single-client production deployments are unaffected.

### Changed

- 💄 Refined sidebar navigation styling: selected and hover items now show a contrasting rounded "pill" background that is inset from the drawer edges, with the hover background a stronger shade than the selected background so it remains visible when hovering an already-selected item. Active and hover backgrounds are theme-driven (`--jim-nav-active-bg` / `--jim-nav-hover-bg`) and tuned per theme, with sensible derived fallbacks for any future theme that does not set them.
- 🖥️ A more polished sidebar experience: the signed-in user menu is now anchored to the bottom of the drawer for quick access regardless of how many sections are above it, and pinning or collapsing the drawer is now a single click on a dedicated chevron in the drawer header.

### Fixed

- 🐛 Interactive `Connect-JIM` against Keycloak deployments previously failed with `Invalid parameter: redirect_uri` because JIM advertised the confidential web client ID to the PowerShell module. Administrators can now register a separate public client (as the [SSO Setup Guide](https://docs.junctional.io/administration/sso-setup/) has always instructed) and advertise it to interactive clients via the new `JIM_SSO_PUBLIC_CLIENT_ID` environment variable.
- 🐛 `Get-JIMRole` and the `GET /api/v1/security/roles` endpoint now report the correct static member count for each role; previously the count was always zero because the underlying query did not load role memberships. The count is now aggregated directly in SQL, so even roles with very large memberships are returned cheaply.
- 🐛 `Get-JIMRole -Id` and `GET /api/v1/security/roles/{id}` now report the correct static member count when retrieving a single role.
- 🐛 `Get-JIMMetaverseObjectRole` and `GET /api/v1/security/metaverse-objects/{id}/roles` now report the correct static member count for each role a Metaverse Object belongs to.
- 🐛 `GET /api/v1/synchronisation/connected-systems/{id}` now reports the correct connected system object count; previously it always returned zero because the navigation property was not loaded. The count is now sourced from a dedicated count query, mirroring how `pendingExportCount` is already computed.

### Security

- 🔒 Patched `Microsoft.AspNetCore.DataProtection` to 10.0.7 to address CVE-2026-40372 (GHSA-9mv3-2cwr-p262, high-severity elevation of privilege / authentication cookie forgery in ASP.NET Core Data Protection). Also drops the now-redundant transitive override of `System.Security.Cryptography.Xml`, which Data Protection 10.0.7 brings in at a patched version directly.

## [0.10.0] - 2026-04-22

### Added

- ✨ Added a Service Name and Service ID so you can tell JIM instances apart at a glance. Set a friendly name per instance on the Service Settings page and see it under "JIM" in the sidebar, in the browser tab title, and in the footer. The Service ID is generated once per instance and never changes, useful for tooling, logs, and telemetry (#583)
- ✨ Predefined Searches can now be disabled and re-enabled without deleting them; disabled searches are hidden from the portal, the search API, and the sidebar navigation, while administrators can still manage them via the admin UI, the new `/api/v1/predefined-searches` endpoints, and the new `Get-JIMPredefinedSearch` / `Set-JIMPredefinedSearch` PowerShell cmdlets (#555)
- ✨ PowerShell cmdlets for System endpoints: `Get-JIMHealth` (with `-Ready` and `-Live` probes), `Get-JIMVersion`, `Get-JIMAuthConfig`, and `Get-JIMUserInfo`; health, version, and auth config cmdlets work without `Connect-JIM` via a `-Url` parameter (#468)
- ✨ Interactive API reference powered by Scalar, available at `/api/reference` in all environments including air-gapped deployments; OpenAPI document is pre-generated at build time for instant loading with zero runtime overhead
- ✨ Public API reference published to the JIM documentation site at [docs.junctional.io/api/reference/](https://docs.junctional.io/api/reference/); automatically updated on every release to match the published JIM version
- ✨ Clear Connected System activity now tracks and displays removal statistics, showing how many pending exports and connected system objects were removed (#74)
- ✨ New count endpoints for metaverse objects, connector space, and pending exports, with filtering by object type, partition, change type, and status; suitable for dashboards, SIEM integration, and capacity monitoring (#154)
- ✨ New user menu in the navigation drawer showing the signed-in user's avatar (with initials), display name and username, with pinning, dark mode and sign-out controls in a single polished popover (#49)
- ✨ Automated integration test metrics streaming to central tracking system with Grafana dashboards (#476)
- 🔒 API and PowerShell support for managing Role membership on Metaverse Objects, enabling administrators to appoint or remove additional admins without restarting the service (#467)
- ✨ New API endpoints for Role member management: list members, add member, remove member, get Role by ID, and list the Roles a Metaverse Object is a member of
- ✨ New PowerShell cmdlets `Get-JIMRoleMember`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, and `Get-JIMMetaverseObjectRole` with full pipeline support
- ✨ `Get-JIMRole` cmdlet now supports `-Id` parameter for direct Role lookup by identifier
- 🔒 Safety checks prevent administrator lockout: self-removal from the Administrator role and removing the last Administrator are both blocked with clear error messages
- 🔒 Sign-out with identity provider, gated by the `SSOEnableLogOut` service setting, with a confirmation dialog to prevent accidental clicks (#49)

### Performance

- ⚡ Connected System detail lookups are much cheaper on write-path and validation API calls: introduced a lightweight `GetConnectedSystemCoreAsync` retrieval variant that loads only essential properties, and migrated the API controllers that previously paid for the full schema, partition and container graph just to verify the system exists (#494)
- ⚡ Connected System container hierarchy loading now handles arbitrary depth and avoids the cartesian-explosion risk of the previous 11-level hard-coded Include chain; containers are loaded flat and rebuilt into a tree in memory (#494)
- ⚡ Full Connected System loads now issue one database query for object matching rules instead of four, eliminating the fan-out that split-query mode introduced when walking `Sources.ConnectedSystemAttribute`, `Sources.MetaverseAttribute`, `TargetMetaverseAttribute` and `MetaverseObjectType` as separate Include branches (#494)
- ⚡ Default all EF Core queries to `AsNoTracking`, reducing memory and CPU overhead for read-heavy operations; write paths explicitly opt in to change tracking (#484)
- ⚡ Enriched diagnostic spans with cumulative object count and wall-clock offset tags for throughput profiling (#476)
- ⚡ Added MetricsCheckpoint log lines for guaranteed throughput tracking at any log level (#476)

### Changed

- 🖥️ Partition-configuration validation errors now pinpoint the exact gap (hierarchy not imported, no partitions selected, or selected partitions have no container selected) and name the partition involved, replacing the previous generic "no partitions or containers have been selected" message and making misconfigurations far faster to diagnose (#564)
- 🖥️ Page footer now links the Tetron name to tetron.io and includes a GitHub link next to the version number (#49)
- 📦 File Connector storage uses the formal Docker named volume `jim-connector-files-volume`, mounted at `/connector-files` inside JIM Web and JIM Worker. Default deployments get working File Connector exports out of the box without any host-side permission setup. Customers integrating with external file shares bind-mount over a subdirectory of `/connector-files`. See the JIM File Connector documentation for both patterns.

### Fixed

- 🐛 Group and other multi-valued-reference sync activities no longer produce duplicate execution items; cross-page reference resolution now merges reference attribute flow into the original Projected/Joined record instead of creating a second standalone "Attribute Flow" record for the same object. Fixes inflated activity counts and removes the confusing split-outcome rows that appeared in activity detail
- 🐛 Static member values and other multi-valued references on group activity detail pages now render as clickable user chips with display names instead of raw GUIDs; reference change records now carry their target as a proper foreign key so the link can be materialised on display
- 🐛 Export failures caught by exception handlers now produce Run Profile Execution Items reliably; previously a thrown connector exception could mark a batch failed without producing any RPEI, so the activity appeared to complete successfully despite silent export failures
- 🐛 Metaverse Object and Connected System Object change history is now persisted during sync RPEI flush and on single-object create, ensuring the audit timeline reflects every sync run
- 🐛 Sign-out with the bundled Keycloak no longer fails with "Missing parameters: id_token_hint"; JIM now persists the ID token during sign-in so the OIDC middleware can include it on the end-session request per the OIDC spec (#49)
- 🐛 Keycloak hostname configuration corrected so that browsers and Docker back-channel clients each get the right endpoint URLs, fixing sign-in and sign-out for all four deployment scenarios (Codespaces, devcontainer native, devcontainer Docker, production) (#49)
- 🐛 Connected System partition trees now include nested containers below the top level. Directories with nested organisational units (e.g. `OU=Users,OU=Corp`) are loaded and returned through the API in full, so administrators can select nested containers for import and automation can address them via PowerShell (#586)

### Security

- 🔒 Supply chain hardening: all Docker base images are digest-pinned, all GitHub Actions are pinned by commit SHA, and the main branch is protected with required status checks including automated code review, CodeQL, container scan, and dependency scan (#520, #517, #521)
- 🔒 Patched transitive `System.Security.Cryptography.Xml` to 10.0.6 to address CVE-2026-33116 (low-severity DoS in `EncryptedXml`); the package is pulled in via ASP.NET Core Data Protection but not used by JIM at runtime
- 🔒 Patched `basic-ftp` CRLF injection vulnerabilities (GHSA-chqc-8p9q-pq6q and GHSA-rp42-5vxx-qpwr) and picked up Ubuntu Noble security updates for libldap and cifs-utils in all production container images

## [0.9.1] - 2026-04-08

### Added

#### Search Objects API (#482, #488)

- ✨ New `GET /api/v1/metaverse/objects/search/{predefinedSearchUri}` endpoint for fast, lightweight object searches optimised for 100K+ object deployments
- ✨ New `Search-JIMMetaverseObject` PowerShell cmdlet with predefined search support, sorting, filtering, and auto-pagination

### Performance

#### Paginated List Optimisation (#482, #485)

- ⚡ Metaverse object list sorting now uses a pre-computed cached display name column, eliminating expensive per-query subqueries for display name resolution
- ⚡ New composite index on metaverse attribute values for faster attribute-based sorting and filtering
- ⚡ Paginated list queries for metaverse objects and connected system objects rewritten to use keyset pagination with optimised sort subqueries

### Fixed

- 🖥️ Fixed oversized text on avatar chips in sync rule list and detail pages
- 🖥️ Multi-valued attribute value counts on metaverse object detail pages now display with thousand separators for readability

## [0.9.0] - 2026-04-07

### Added

#### 100K Object Scale (#451, #437, #438)

JIM now supports deployments of 100,000+ objects, validated by Scale100K integration tests across the full import, sync, and export pipeline. A bounded memory architecture ensures stable, predictable resource usage regardless of dataset size.

- ✨ Bounded memory sync and export pipelines: change tracker cleared at every page boundary and caches loaded per-page instead of upfront, enabling 100K+ object operations without out-of-memory crashes
- ✨ Partition-scoped deletion detection for full imports: deletion detection is now scoped to the imported partition, preventing CSOs from other partitions being incorrectly marked as obsolete during large-scale imports
- 🖥️ Import processing now displays throughput (objects/sec) and ETA in progress messages, completing progress tracking coverage across all long-running phases

#### .NET 10 Migration (#174)

- ✨ Migrated from .NET 9.0 (STS) to .NET 10.0 (LTS), extending support from November 2026 to November 2028
- ✨ Upgraded all NuGet packages to .NET 10-compatible versions, including EF Core 10, MudBlazor 9, and Humanizer 3
- ✨ Replaced Swashbuckle with built-in `Microsoft.AspNetCore.OpenApi` + Scalar for modern API documentation UI
- 🔒 All Docker containers now run as non-root (`USER app`, UID 1654), improving security posture for enterprise deployments
- 🔒 Docker container hardening (#333): read-only root filesystem, dropped all Linux capabilities with selective re-add, and `no-new-privileges` flag on all application containers
- 🔒 Moved CIFS/SMB utilities and capabilities from Web to Worker container, applying least-privilege principle (only the Worker executes file connector operations)
- 📦 Docker images migrated from Debian Bookworm to Ubuntu 24.04 Noble base with pinned SHA256 digests
- 📦 Added `global.json` to pin .NET 10 SDK version across all environments

#### Service Settings REST API & PowerShell Cmdlets

- ✨ New REST API for managing service settings (`GET/PUT/DELETE /api/v1/service-settings`), enabling automation of change tracking, sync page size, history retention, and other operational settings
- ✨ New PowerShell cmdlets: `Get-JIMServiceSetting`, `Set-JIMServiceSetting`, `Reset-JIMServiceSetting` for managing service settings from the command line or automation scripts

#### Data Integrity Validation (#465)

- 🔒 Metaverse attribute operations now validate data integrity before executing: deleting attributes with stored values, deleting attributes referenced by sync rules, and removing object type mappings with existing data all return structured validation errors instead of silently corrupting state

#### PowerShell Module Enhancements

- ✨ `-Name` parameter added to six `Get-JIM*` cmdlets (`Get-JIMRunProfile`, `Get-JIMSyncRule`, `Get-JIMApiKey`, `Get-JIMCertificate`, `Get-JIMRole`, `Get-JIMConnectorDefinition`), enabling direct filtering without `Where-Object`
- ✨ New `Get-JIMPendingDeletion` cmdlet with List, Count, and Summary parameter sets for monitoring objects awaiting deletion
- ✨ New `Get-JIMActivityChildren` cmdlet for retrieving child activities of a parent activity

#### Integration Test Runner Enhancements

- ✨ `-LogLevel` parameter for integration test runner: override log verbosity (Verbose/Debug/Information/Warning/Error/Fatal) for the test run without permanently modifying `.env`
- ✨ `-DisableChangeTracking` switch for integration test runner: disable CSO and MVO change tracking during large-scale tests to reduce database writes and improve throughput
- 🖥️ Interactive menus for log level and change tracking selection when running tests without explicit parameters

### Fixed

- 🔒 Safe cancellation for sync operations (#339): when an admin cancels a running Full Sync or Delta Sync, the current page's flush pipeline now completes before exiting. Previously, cancellation could leave orphaned metaverse objects without corresponding pending exports, causing target systems to silently miss updates.
- 🐛 Fixed import tasks continuing to process after cancellation (#339); cancelling a Full Import or Delta Import from the Operations Queue now stops the import between pages and skips persistence. Previously, the import processor ignored the cancellation signal and ran to completion.
- 🐛 Fixed cancelled tasks having their status overwritten to Completed or Failed; the Worker now correctly preserves the Cancelled activity status instead of overwriting it when the processor finishes.
- 🐛 Fixed sync progress bar showing inflated object counts (CSOs + pending exports) instead of just CSOs; progress percentage and ETA are now accurate for Full Sync and Delta Sync

### Changed

- ⚡ LDAP export concurrency is now auto-tuned based on the detected directory server type; AD DS and OpenLDAP default to 16 concurrent operations (up from 4), while Samba AD and unknown directories remain at 4 for compatibility. Administrators who have manually configured the value will not be affected.

### Performance

- ⚡ Selective attribute loading for full sync: unchanged CSOs (based on watermark comparison) skip attribute value loading and attribute flow entirely, dramatically reducing I/O for large-scale repeat syncs
- ⚡ Eliminated redundant per-page COUNT queries during sync; total count is now passed from sync start, removing 200+ unnecessary full-table scans at 100K objects
- ⚡ Default sync page size increased from 500 to 1,000, halving the number of database round-trips per sync run
- ⚡ Sync progress updates now use direct SQL instead of EF Core change tracker, reducing per-page overhead
- ⚡ Removed explicit RepeatableRead transactions from sync page loading; PostgreSQL MVCC provides sufficient consistency without the round-trip overhead
- ⚡ Pending Exports table on CSO detail page now uses server-side paging; pages with thousands of pending changes (e.g. 10K member adds) load instantly instead of rendering all rows at once
- ⚡ All export evaluation and pending export cache queries now use `AsNoTracking`, eliminating unnecessary entity tracking overhead during sync
- ⚡ Per-page memory diagnostics logging: administrators can monitor memory usage across sync pages to verify bounded memory behaviour

## [0.8.1] - 2026-04-02

### Added

- ✨ Pre-export CREATE→DELETE reconciliation — when an object is created and then deleted before export runs, the redundant pending exports are automatically cancelled instead of failing during export (#218)

### Performance

- ⚡ Export rule evaluation optimised to reduce per-MVO processing cost, improving sync performance for configurations with many export rules (#417)
- ⚡ Active Directory schema discovery now batches LDAP queries, reducing connection round-trips during schema import (#433)

### Fixed

- 🐛 Fixed entity tracking conflict during cross-page reference resolution at scale — Full Sync no longer fails with "ConnectedSystemObject cannot be tracked" when groups share members across resolution batches (10,000+ users)
- 🐛 Error messages no longer display the internal "EMERGENCY UPDATE" prefix — user-facing messages now show clean, actionable text (#448)
- 🐛 Activity and RPEI detail page breadcrumbs are now context-aware, showing the correct navigation path based on how the page was reached
- 🔒 Sanitised `Request.Method` in global exception handler logging to prevent log injection (CWE-117) (#444)

## [0.8.0] - 2026-04-01

### Added

#### OpenLDAP Connector Support (#72)

- ✨ Full OpenLDAP and RFC 4512-compliant LDAP directory support — connect to OpenLDAP, 389 Directory Server, and other standards-based LDAP directories alongside Active Directory
- ✨ Automatic directory type detection from rootDSE (Active Directory, OpenLDAP, Generic LDAP) with per-type external ID handling (objectGUID vs entryUUID)
- ✨ RFC 4512 schema discovery — object classes and attribute types parsed from the subschemaSubentry with OID-based data type mapping and superclass hierarchy walking
- ✨ Multi-suffix partition discovery via rootDSE namingContexts for non-AD directories
- ✨ Accesslog-based delta import for OpenLDAP — queries `cn=accesslog` for incremental changes with automatic fallback to full import
- ✨ Parallel import with configurable concurrency — each container/objectType combination runs on its own LDAP connection, working around RFC 2696 paging cookie limitations
- ✨ Transparent `groupOfNames` placeholder member handling — automatically manages the RFC 4519 MUST constraint so administrators never see placeholder entries in the metaverse
- ✨ DN-aware RDN attribute detection for correct export naming
- ✨ Partition-scoped imports — run profiles can target a specific partition instead of importing all selected partitions (#353)

#### Worker Redesign (#394)

- ✨ Pure domain engine (`ISyncEngine`) — 7 stateless methods with zero I/O dependencies, making core sync logic independently testable with plain objects
- ✨ Formal data access boundary (`ISyncRepository`) — ~80-method interface separating Worker data access from shared EF Core repositories, with purpose-built in-memory implementation for tests
- ✨ Dependency injection throughout Worker and Scheduler — `IJimApplicationFactory`, `IConnectorFactory`, per-task context isolation

#### Bundled Keycloak IdP for Development (#197)

- ✨ Zero-config SSO — `jim-stack` starts a pre-configured Keycloak instance alongside JIM; developers sign in immediately with `admin` / `admin`
- ✨ Pre-configured realm with `jim-web` (confidential + PKCE) and `jim-powershell` (public + PKCE) clients, `jim-api` scope, and two test users
- ✨ `.env.example` defaults point to the bundled Keycloak — no manual IdP configuration needed for local development
- ✨ `jim-keycloak` / `jim-keycloak-stop` / `jim-keycloak-logs` aliases for standalone Keycloak (F5 debugging workflow)
- ✨ Keycloak admin console accessible at `http://localhost:8181`
- 🔒 HTTP OIDC authority support for development (RequireHttpsMetadata conditionally disabled)

#### Object Type Icons (#92)

- 🖥️ Configurable icons for metaverse object types — assign icons to object types, displayed across the homepage, navigation menu, schema pages, and object detail views

#### Pending Export Management

- 🖥️ Pending export detail page with grouped attribute changes, capped multi-valued attribute loading, and server-side paginated drill-down for large change sets
- 🖥️ `Get-JIMPendingExport` and `Get-JIMConnectedSystemObject` PowerShell cmdlets with corresponding API endpoints
- 🖥️ Pending exports list now shows display names instead of raw GUIDs

#### Activity Monitoring

- 🖥️ Auto-refresh polling on the activity list page — data updates automatically without manual refresh
- 🖥️ Pause/resume toggle for auto-refresh polling
- 🖥️ Compact determinate progress bar on the History tab for in-progress activities
- 🖥️ Phase-specific activity messages during imports — "Connecting to connected system" and "Importing objects from connected system" show the current phase before object processing begins (#342)

#### Run Profile Editing

- 🖥️ Run profile editing UI — edit name, file path, partition, and page size for existing run profiles
- ✨ `SupportsFilePaths` connector capability — File Path fields only appear for connectors that use file-based import/export
- ✨ `SupportsPaging` connector capability — Page Size controls only appear for connectors that support paged queries

#### Navigation and Layout

- 🖥️ Browser back/forward navigation support for all tabbed pages via URL query parameters
- 🖥️ Tabs view mode for metaverse object details — attribute categories displayed as horizontal tabs alongside existing form and table views
- 🖥️ Expanded Target section in the Operations sidebar with type-specific links
- 🖥️ Connector capabilities grouped by category on the detail page

#### Infrastructure

- 📦 Docker healthchecks for Worker and Scheduler — file-based heartbeat monitoring detects stalled service loops (#185)
- ✨ Multi-valued to single-valued import attribute flow — when a multi-valued source attribute flows to a single-valued target, JIM automatically selects the first value and records a warning (#435)

### Performance

#### Worker Redesign (#394)

- ⚡ Parallel multi-connection writes — `ParallelBatchWriter` splits bulk database writes across N concurrent PostgreSQL connections, utilising multiple CPU cores during save phases. Configurable via `JIM_WRITE_PARALLELISM` environment variable
- ⚡ COPY binary protocol for bulk inserts — CSO creates, RPEIs, MVO creates, and sync outcomes now use PostgreSQL's COPY binary import, eliminating SQL parsing overhead and parameter limits (#338)
- ⚡ Worker-exclusive bulk SQL in `SyncRepository` — hot-path operations (RPEI persistence, CSO bulk create, pending export operations) moved from shared repositories into dedicated partial classes, reducing shared repo surface by 1,200+ lines

#### Import Pipeline (#427, #440)

- ⚡ Import CSO matching now uses a pre-fetched dictionary for O(1) external ID lookups, replacing N per-object database queries with a single bulk query at import start — eliminates the dominant bottleneck in full imports (#440)
- ⚡ Import reference resolution is now case-insensitive (matching RFC 4514 DN semantics) and batches sort non-referencing objects first with committed ID tracking — eliminates the expensive post-import LOWER() fixup SQL query (#427)
- ⚡ Two-phase parallel write commits CSO rows before attribute values, giving cross-partition references full FK visibility and eliminating post-import fixup queries (#427)

#### Sync and Export

- ⚡ Immediate MVO deletion (zero grace period) skips unnecessary attribute recall and export evaluation, eliminating wasted database round-trips (#390)
- ⚡ Deferred export resolution progress reporting throttled to every 50 items instead of per-item, eliminating ~540 unnecessary database round-trips for typical batches (#426)
- ⚡ Bulk RPEI and CSO change persistence timeouts increased to 300 seconds for large imports (#426)
- ⚡ Log file rolling size reduced from 500 MB to 50 MB per file (100 files retained, ~5 GB max per service)

### Fixed

- 🔒 Attribute change history is no longer cascade-deleted when a metaverse or connected system attribute definition is removed — the FK is set to null and snapshot `AttributeName`/`AttributeType` properties preserve the audit trail indefinitely (#58)
- 🐛 Expression attribute lookups (e.g. `mv["Department"]`) are now case-insensitive, preventing silent failures when attribute name casing in expressions did not exactly match stored names (#341)
- 🐛 Pending export reconciliation now correctly matches all 8 attribute data types — Boolean, Guid, and LongNumber exports previously failed to reconcile and appeared permanently stuck (#263)
- 🐛 Deferred export progress bar no longer shows values exceeding 100%
- 🐛 Progress bars on the History tab now update in real-time instead of freezing after initial page load
- 🐛 Worker database operations no longer time out during large imports — command timeout increased from 30s default to 300s (#426)
- 🐛 Connector-level warnings (e.g. delta import fallback) now appear as activity banners instead of phantom RPEIs with no CSO association
- 🐛 MVO reference attribute foreign keys are now reliably persisted across cross-page and cross-batch scenarios
- 🐛 MVO change tracking no longer crashes when recording deletion changes for objects with unloaded reference navigation properties

### Changed

#### Worker Redesign (#394)

- 🔄 All Worker and Workflow tests (~1,300) migrated from mocked `DbContext` to purpose-built `InMemoryData.SyncRepository`, eliminating three-way code path divergence between production, workflow tests, and unit tests
- 🔄 Removed ~32 try/catch EF fallback blocks from repository files (-642 lines) — production and test code paths are now identical

- 🔄 Object type names from camelCase LDAP schemas (e.g. `groupOfNames`) now display correctly as "Group Of Names"
- 🔄 Error type column merged inline with outcome chips on the activity detail page

## [0.7.1] - 2026-03-19

### Fixed

- 🎨 Sidebar background colour in the Navy O6 theme now matches the page background for a seamless, cohesive look

## [0.7.0] - 2026-03-19

### Added

- ✨ `GET /api/v1/userinfo` endpoint — returns the authenticated user's JIM identity, roles, and authorisation status without requiring Administrator privileges
- ✨ `Connect-JIM` now verifies authorisation after authentication and warns if the user has no JIM identity, with clear guidance to sign in via the web portal first
- 🖥️ Improved 403 error messages in the PowerShell module — now explains the likely cause (no JIM identity) and how to resolve it
- 🖥️ Properties tab on the Metaverse Object detail page — shows creation date, last modified, and clickable initiator links
- 🖥️ Form and table view toggle on the Metaverse Object detail page
- 🖥️ Server-side paginated dialog for large multi-valued attributes on the MVO detail page
- 🖥️ Object type chip prefix on reference values in MVO table view
- 🖥️ Server-side paging on the schema attributes table
- 🖥️ Sortable columns on the staging object attribute table
- ✨ Activity tracking for initial admin user creation
- 🔒 `Connect-JIM` now skips the authorisation check when using API key authentication

### Changed

- 🎨 New default theme with a refined colour palette — deeper backgrounds, improved button and chip contrast across dark and light modes, and better visual hierarchy for a more polished, readable experience
- 🎨 Switched web font to Inter — self-hosted for air-gapped deployment, delivering improved readability and a modern feel
- 🗑️ Removed legacy themes consolidated into the new default
- 🔄 "Connected System Objects" pages renamed to "Staging" with cleaner URL structure and improved introductory UX
- 🔄 "Data Generation" renamed to "Example Data" across the entire stack for consistent naming — models, API routes (`/example-data/`), PowerShell cmdlets (`Get-JIMExampleDataTemplate`, `Invoke-JIMExampleDataTemplate`), database tables, and UI all now share the "Example Data" family prefix
- ⚡ Database migrations flattened into a single `InitialCreate` migration for faster first-start performance and simpler codebase
- 🖥️ Redesigned object matching tab layout and combined status chips on the RPEI detail page

### Fixed

- 🐛 Resolved intermittent DbContext concurrency errors across all Blazor Server pages — overlapping async lifecycle methods (e.g. data load and table pagination) no longer share a single database context
- 🐛 FK violation in import change history bulk persistence no longer causes import failures
- 🐛 `HasPredefinedSearches` now returns the correct value for object types with predefined searches
- 🐛 Spurious pending exports no longer surface during full sync operations

#### Deleted Object Change History

- 🐛 Deleted MVO change history now shows the full timeline of prior changes (Created, AttributeFlow, Disconnected) — previously only the Deleted record was visible due to a broken FK correlation after deletion
- 🐛 Final attribute values are now captured on MVO deletion change records, showing exactly what the object looked like before it was removed
- 🐛 Final attribute values are now captured on CSO deletion change records — previously only the external ID and display name were preserved
- 🐛 MVO deletion no longer fails with FK constraint violations when the deleted object is referenced by other MVOs (e.g., as a Manager) or by change history records

#### Pending Export Reference Display (#404)

- 🐛 Pending export reference attributes (e.g. group members) now display meaningful identifiers (DN, External ID) instead of raw GUIDs with a misleading "unresolved reference" warning
- 🐛 References to objects processed later on the same sync page are now resolved via a post-page resolution pass
- 🐛 Resolved reference attributes (e.g. group members) now appear in export causality tree attribute changes — previously they were silently dropped
- 🖥️ Pending export references show a "pending export" indicator to distinguish them from fully resolved and genuinely unresolved references

#### Database Resilience (#408, #409)

- 🐛 Transient database errors now return HTTP 503 (Service Unavailable) with a `Retry-After` header instead of HTTP 400 (Bad Request)
- 🐛 Cross-batch reference fixup hardened against database timeouts and FK gaps at scale
- ⚡ Transient database failures handled gracefully at API level with retry guidance
- ⚡ Connection pool sizing reduced from 50 to 30 per service to leave headroom within PostgreSQL's `max_connections`
- 📦 Development database (`db.yml`) now explicitly sets `max_connections=200` to match the full Docker stack

### Performance

- ⚡ MVO detail page now caps multi-valued attribute values with server-side pagination, dramatically reducing load time for objects with large MVAs
- ⚡ Pending export reconciliation query optimised with sub-phase progress messages

## [0.6.1] - 2026-03-15

### Added

- ✨ Child activity tracking — sync activities now show nested child activities with drill-down navigation (#298)
- ✨ `Clear-JIMConnectedSystem` PowerShell cmdlet — wipe all objects from a connected system without deleting the configuration (#365)
- 🛡️ Global error boundary catches unhandled rendering exceptions in the UI — instead of a broken page, users see a friendly error message with "Try Again" and "Go to Dashboard" recovery options (#167)
- 🖥️ "Has child activities" filter on the Activities list and Operations history pages
- 🖥️ Contextual page heading icons, refined operation/outcome chip colours, and improved causality tree display
- 🔒 Log injection sanitisation across all logging calls to prevent CWE-117 log forging
- 🔒 Trivy container image scanning added to CI pipeline

### Changed

- 🔄 Built-in "Employee Status" metaverse attribute replaced with the more generic "Status"

### Fixed

- 🐛 Cross-batch and cross-run reference resolution now correctly handles out-of-order LDAP imports and foreign key persistence
- 🐛 Cross-page reference RPEIs are now merged instead of creating duplicates
- 🐛 LDAP AddRequest now chunks large multi-valued attributes to avoid directory server size limits
- 🐛 Default `userAccountControl` to 512 on Create exports via Coalesce, preventing AD account creation failures
- 🐛 Parent activity progress messages no longer overwritten by child activities
- 🐛 Activity detail page correctly reloads when navigating between parent and child activities
- 🐛 Group member change history no longer shows "(identifier not recorded)" for members imported in a later batch — the DN string is now recorded when the referenced CSO hasn't been persisted yet at change history time

### Performance

- ⚡ Change history and RPEI persistence now uses PostgreSQL COPY binary import, dramatically reducing write time for large sync operations (#398)
- ⚡ Cross-batch reference fixup skipped entirely when no unresolved references exist (#398)
- ⚡ Partial database indexes added for cross-batch reference fixup queries (#397)

## [0.6.0] - 2026-03-12

### Added

- ✨ Disconnection causality tracking — causality tree now traces MVO attribute changes and deletion fate during disconnection and recall, showing exactly what happened and why (#392)
- ✨ Reference attributes rendered as clickable links on RPEI detail page for easy navigation to related objects
- 🖥️ Filter controls on the Activities list page for quick searching by status, connector, and profile
- 🖥️ Initiated-by name now included in activity search results

### Fixed

- 🐛 Export activity detail page now shows display name for Create-type exports even after the target CSO is later deleted — display name is now snapshotted from the pending export's attribute changes at export time
- 🐛 Causality tree no longer shows a spurious attribute count chip on MVO Projected nodes when reference attributes were merged into the projection
- 🐛 Export runs no longer silently skip pending exports when a batch contains only deferred or ineligible items — all staged exports are now reliably processed in a single export run
- 🐛 Activity detail page now shows display name and object context for Create-type pending exports surfaced during sync (previously showed dashes as no CSO exists yet)
- 🐛 RPEI detail page now shows pending export attribute changes for staged (informational) pending exports, not only for error states
- 🐛 Causality tree no longer shows unrelated pending exports when a secondary import connector syncs while a previous connector's Create exports are still queued — only exports caused by the current sync's attribute changes are shown
- 🐛 Group membership exports no longer arrive empty — resolved reference foreign keys are now persisted during import
- 🐛 Resolved reference values now correctly persisted after export, preventing data loss on subsequent sync runs
- 🐛 Duplicate pending exports no longer accumulate — stale entries are automatically self-healed
- 🐛 Activities with unhandled errors now correctly marked as completed with error instead of appearing successful
- 🐛 Multi-valued attributes in LDAP group member exports are now consolidated into a single AddRequest, fixing partial membership writes
- 🐛 Export batch queries now include CSO object type, resolving objectClass errors in LDAP targets
- 🐛 Single-valued attribute duplicates no longer occur during pending export merges

### Performance

#### CSO Large MVA Pagination (#320)
- ⚡ CSO detail page and API now load capped MVA values (first 100) instead of the full collection, dramatically reducing memory and load time for objects with 10K+ multi-valued attributes
- ✨ New paginated attribute values API endpoint (`GET /api/connected-systems/{csId}/objects/{csoId}/attributes/{attributeName}/values`) with server-side search and pagination
- 🖥️ MVA dialog now fetches data on demand with server-side search and pagination — no longer holds the full value set in Blazor circuit memory
- ✨ API responses include per-attribute value summaries showing total count, returned count, and whether more values are available

#### Large-Scale Import Optimisation
- ⚡ Full import operations now handle 100K+ objects without out-of-memory failures through batch processing, raw SQL persistence, and incremental memory release
- ⚡ Export operations at scale now batch-load to eliminate EF change tracker overhead
- ⚡ Real-time batch progress reporting during large CSO persistence operations

## [0.5.0] - 2026-03-08

### Added
- ✨ Self-contained object matching rules — sync rules now carry their own matching logic for import and export, enabling fully portable rule definitions (#386)
- ✨ CRUD API endpoints for sync rule object matching rules (`GET`, `POST`, `PUT`, `DELETE` `/api/v1/synchronisation/sync-rules/{id}/matching-rules`)
- ✨ Matching mode switching API — toggle between simple and advanced object matching per connected system
- 🖥️ Sortable Object Mapping and Capabilities columns on the Sync Rules page

### Fixed
- 🐛 Setup script now correctly detects Docker Desktop alongside Docker Engine

## [0.4.0] - 2026-03-05

### Added
- ✨ One-command deployment — new interactive installer auto-detects the latest release, configures SSO and database, and starts JIM in minutes
- 📦 Production-ready Docker Compose configuration — deploy JIM from pre-built images without needing source code
- 📦 Standalone deployment files attached to each GitHub release for easy download without cloning the repository
- ✨ Welcome banner displayed on successful PowerShell connection
- 📖 Comprehensive [Deployment Guide](https://docs.junctional.io/administration/deployment/) covering prerequisites, topology options, TLS, reverse proxy, upgrades, and monitoring
- 🖥️ Sortable columns on the Attribute Flow table
- 🖥️ Filter controls on the Attribute Flow table
- ✨ Edit attribute flow mappings inline on the Sync Rule detail page
- 🖥️ Sync Rule detail page redesign with expression highlighting, table/card views, and improved layout
- 🖥️ Synchronisation Rules quick link on the homepage dashboard
- 🖥️ Filter controls on the Connected System Objects list page
- 🖥️ Full-width layout option for table-heavy pages
- 🖥️ Confirmation dialog before deleting attribute flow mappings
- ✨ `Get-JIMMetaverseObject -All` — automatically paginates through all results in a single command
- ✨ Pronouns attribute support (#360, #362)
- ✨ Sync Outcome Graph — full causal tracing of every change during synchronisation, showing exactly why each object was projected, joined, updated, disconnected, or exported (#363)
- ✨ Configurable sync outcome tracking level (None / Standard / Detailed) — control how much causal detail is recorded per synchronisation (#363)
- 🖥️ Colour-coded outcome summary chips on Activity Detail rows for at-a-glance sync result visibility (#363)
- 🖥️ Filter activity results by outcome type — quickly find projections, joins, attribute flows, exports, and more (#363)
- ✨ Export change history — drill into exactly which attributes were changed on each exported object, with before/after values
- 🔒 Hardened release pipeline with container scanning, SBOM attestation, and build validation
- 📦 Application blocks readiness until database migrations are applied

### Changed
- 🔄 Replaced "Change Type" filter with richer outcome type filtering on the Activity Detail page (#363)
- 🔄 Renamed Activity statistics labels for clarity ("Stats" → "Outcomes", "Unchanged" → "CSOs Unchanged")

### Fixed
- 🐛 `Get-JIMMetaverseObject` now correctly returns all results when page size exceeds 100
- 🐛 Fixed spurious export operations being generated for objects queued for immediate deletion
- 🐛 Activity attribute flow statistics now show accurate object counts instead of inflated per-attribute counts
- 🐛 Connected system object join state now reliably persisted during synchronisation
- 🐛 Activity Detail rows now show display name and object type even after the connected system object has been deleted (#363)
- 🐛 OIDC `Identity.Name` now correctly resolved when claims are unmapped
- 🐛 Two-pass CSO processing prevents false `CouldNotJoinDueToExistingJoin` errors during synchronisation

### Performance
- ⚡ Sync engine performance — up to 37% faster synchronisation through optimised batch persistence of activity results (#338)

## [0.3.0] - 2026-02-25

### Added

#### Scheduler Service (#168)
- Schedule data model with cron and interval-based trigger support
- Background scheduler service with 30-second polling cycle
- Multi-step schedule execution with sequential and parallel step modes
- Schedule management REST API (CRUD, enable/disable, manual trigger, execution monitoring)
- Schedule management UI integrated into Operations page with tabbed interface
- Custom cron expression support with pattern-based UI
- Queue all schedule steps upfront for near-instant step transitions
- PowerShell cmdlets: `New-JIMSchedule`, `Get-JIMSchedule`, `Set-JIMSchedule`, `Remove-JIMSchedule`, `Enable-JIMSchedule`, `Disable-JIMSchedule`, `Add-JIMScheduleStep`, `Remove-JIMScheduleStep`, `Start-JIMSchedule`, `Get-JIMScheduleExecution`, `Stop-JIMScheduleExecution`
- Scheduler integration tests (Scenario 6)

#### Change History (#14, #269)
- Full change tracking for metaverse objects and connected system objects with timeline UI
- Initiator and mechanism tracking (User, API, Sync, System)
- Deleted objects view with change audit trail
- Configurable retention and cleanup
- Change history records for data generation operations
- Granular per-change-type statistics replacing aggregate activity stats

#### Progress Indication (#246)
- Real-time progress bars for running operations on Operations page
- Percentage tracking and contextual messages
- Progress reporting for deferred exports and cross-page reference resolution
- Import progress tracking with pagination support
- Hidden page number indicator for single-page imports

#### Dashboard
- Home page redesigned as an informative dashboard
- Hover effect on clickable dashboard cards
- Application version displayed in page footer

#### Security and Authentication
- Interactive browser-based authentication for the PowerShell module
- API key authentication support for sync endpoints
- Just-in-time initial admin creation on first sign-in (replaces startup-time creation)

#### LDAP Schema Discovery
- Attribute writability detection during schema discovery
- Support for LDAP omSyntax 66 (Object(Replica-Link)) mapping to Binary data type
- LDAP description attribute plurality override on AD SAM-managed classes

#### Data Generation
- `Split` and `Join` functions for multi-valued attribute transforms
- Centralised GUID/UUID handling with `IdentifierParser` utility

#### PowerShell Module
- Flattened module directory structure
- Version endpoint with server version display on `Connect-JIM`
- Module now includes 75 cmdlets (11 new scheduler cmdlets added to the 64 from 0.2.0)

#### UI Enhancements
- Searchable dialog for large multi-valued CSO attributes
- CSO attribute table sizing and column order improvements
- Persist navigation drawer pin state to user preferences
- Persist category expansion state per object type in user preferences
- Show all attributes on RPEI projection detail page
- Culture-aware thousand separators on all numeric statistics
- Culture-specific day-of-week ordering in schedule configuration
- Theme preview page at `/admin/theme-preview`
- Demo mode for Operations Queue

#### Integration Testing
- `-SetupOnly` flag for integration test runner
- `-CaptureMetrics` flag for performance metrics on large templates
- `-ExportConcurrency` and `-MaxExportParallelism` runner parameters
- Scenario 8: Samba AD group existence checks with retry
- `Assert-ParallelExecutionTiming` validation helper
- `jim-test-all` alias for comprehensive test runs (unit + workflow + Pester)

#### Logging and Observability
- PostgreSQL logs integrated into unified Logs UI
- Diagnostic logging for cache operations and stale entry invalidation
- Separate Disconnected RPEI recorded when processing source deletions

#### Infrastructure
- Automated Structurizr diagram export via `jim-diagrams` alias
- Review-dependabot Claude Code skill for dependency PR review

### Changed
- Purple theme refresh with vibrant logo-inspired colours
- Navy-o5 dark theme improvements
- Execution detail API returns all parallel sub-steps with `ExecutionMode` and `ConnectedSystemId`
- Expression models and `IExpressionEvaluator` moved to JIM.Models for broader use
- Change tracking built into `MetaverseServer` Create/Update methods
- JIM version injected into diagram metadata from VERSION file
- Build timestamp added to dev version suffix
- Reduced logging level for high-rate sync events to improve log readability
- Removed hardcoded `JIM_LOG_LEVEL` overrides from compose files
- Removed fixed height constraint from MVA table on MVO detail page
- Description attribute categorised under Identity on MVO detail page

### Fixed
- Cross-page reference persistence and export evaluation for `AsSplitQuery` materialisation failures
- Post-load SQL repair for `AsSplitQuery` materialisation failures
- LDAP export consolidation and drift merge for multi-valued attributes
- Null-value Update exports now correctly confirmed during reconciliation
- MVO Type included in cross-page reference resolution query
- EF Core identity conflicts during cross-page reference resolution and pending export reconciliation
- Pending CSO disconnections now accounted for when validating join constraints
- Connected System settings not persisting on save
- Partition column hidden on Run Profiles tab when connector doesn't support partitions
- Run profile create/delete and dropdown positioning
- Container tree duplicates and selection not persisting
- Matching rule creation failing with duplicate key violation
- `ExecuteDeleteAsync` used for pending export deletion with inner exception unwrapping
- Split child/parent `SaveChanges` calls to prevent FK constraint violation
- `FindTrackedOrAttach` used for untracked pending export persistence
- History cleanup interval respected across worker restarts
- Scheduler waits for full application readiness on startup
- Graceful worker cancellation instead of immediate task deletion
- Transient unresolved reference warnings downgraded to debug level
- Button styling improvements and error alert panel overflow prevention
- Visited link hover colour consistency
- Log external ID instead of empty GUID for unpersisted CSOs in reference resolution
- MVA table page size wired to global user preference
- Cache diagnostic logging and stale entry invalidation on external ID changes
- Integration test runner try/finally structure repaired
- Total execution time captured in integration test log files

### Performance
- Batch database operations for export processing (single `SaveChangesAsync` per batch instead of per-object)
- Bulk reference resolution for deferred exports (single query instead of N+1)
- LDAP connector async pipelining with configurable "Export Concurrency" setting (1-16)
- Parallel batch export processing with per-system `MaxExportParallelism` setting (1-16)
- `SupportsParallelExport` connector capability flag (LDAP: true, File: false)
- Parallel schedule step execution (steps at the same index run concurrently via `Task.WhenAll`)
- Raw SQL for import and export bulk write operations (replacing EF Core bulk writes)
- Lightweight ID-only matching for MVO join lookups
- Skip CSO lookups entirely for first-ever imports on empty connected systems
- Service-lifetime CSO lookup index to eliminate N+1 import queries
- Tracker-aware persistence for untracked pending export entities
- Parallel in-memory pending export reconciliation using `Parallel.ForEach`
- Lightweight `AsNoTracking` query for pending export reconciliation
- Skip pending export reconciliation for CSOs without exports
- Parallel in-memory reference resolution using `Parallel.ForEach`
- Lightweight DB queries for batch reference resolution
- Raw SQL for `MarkBatchAsExecuting` status update
- Diagnostic instrumentation spans for export DB operations
- Worker heartbeat-based stale task detection and crash recovery

## [0.2.0-alpha] - 2026-01-27

### Added

#### PowerShell Module (61 new cmdlets, 64 total)
- Connected Systems management: `Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, `Remove-JIMConnectedSystem`
- Schema management: `Import-JIMConnectedSystemSchema`, `Set-JIMConnectedSystemObjectType`, `Set-JIMConnectedSystemAttribute`
- Hierarchy management: `Import-JIMConnectedSystemHierarchy`
- Partition and container management: `Get-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemContainer`
- Connector definitions: `Get-JIMConnectorDefinition`
- Sync Rules: `Get-JIMSyncRule`, `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`
- Sync Rule Mappings with expression support: `Get-JIMSyncRuleMapping`, `New-JIMSyncRuleMapping`, `Remove-JIMSyncRuleMapping`
- Object Matching Rules: `Get-JIMMatchingRule`, `New-JIMMatchingRule`, `Set-JIMMatchingRule`, `Remove-JIMMatchingRule`
- Scoping Criteria: `Get-JIMScopingCriteria`, `New-JIMScopingCriteriaGroup`, `Set-JIMScopingCriteriaGroup`, `Remove-JIMScopingCriteriaGroup`, `New-JIMScopingCriterion`, `Remove-JIMScopingCriterion`
- Run Profiles: `Get-JIMRunProfile`, `New-JIMRunProfile`, `Set-JIMRunProfile`, `Remove-JIMRunProfile`, `Start-JIMRunProfile`
- Real-time progress tracking for run profile executions
- Activities: `Get-JIMActivity`, `Get-JIMActivityStats`
- Metaverse: `Get-JIMMetaverseObject`, `Get-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `Get-JIMMetaverseAttribute`, `New-JIMMetaverseAttribute`, `Set-JIMMetaverseAttribute`, `Remove-JIMMetaverseAttribute`
- MVO deletion rule configuration
- API Keys: `Get-JIMApiKey`, `New-JIMApiKey`, `Set-JIMApiKey`, `Remove-JIMApiKey`
- Certificates: `Get-JIMCertificate`, `Add-JIMCertificate`, `Set-JIMCertificate`, `Remove-JIMCertificate`, `Export-JIMCertificate`, `Test-JIMCertificate`
- Security: `Get-JIMRole`
- Example Data: `Get-JIMExampleDataTemplate`, `Get-JIMExampleDataSet`, `Invoke-JIMExampleDataTemplate`
- Expressions: `Test-JIMExpression`
- History: `Get-JIMDeletedObject`, `Get-JIMHistoryCount`, `Invoke-JIMHistoryCleanup`
- Name-based parameter alternatives for all cmdlets (e.g., `-ConnectedSystemName` instead of `-ConnectedSystemId`)

#### API Endpoints
- CRUD endpoints for Connected Systems (`POST`, `PUT` `/api/v1/synchronisation/connected-systems`)
- CRUD endpoints for Sync Rules (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/sync-rules`)
- CRUD endpoints for Run Profiles (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/connected-systems/{id}/run-profiles`)

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

### Changed
- Server-side filtering and sorting for MVO type list pages

## [0.1.0-alpha] - 2025-12-12

### Added

#### Core Platform
- Initial development release
- Core identity management functionality
- Blazor web interface
- REST API
- PostgreSQL database support
- Docker containerisation
- CSV connector
- Basic synchronisation engine

#### PowerShell Module (3 cmdlets)
- Initial preview release published to [PSGallery](https://www.powershellgallery.com/packages/JIM/0.1.0-alpha)
- Connection management: `Connect-JIM`, `Disconnect-JIM`, `Test-JIMConnection`

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.11.0...HEAD
[0.11.0]: https://github.com/TetronIO/JIM/compare/v0.10.3...v0.11.0
[0.10.3]: https://github.com/TetronIO/JIM/compare/v0.10.2...v0.10.3
[0.10.2]: https://github.com/TetronIO/JIM/compare/v0.10.1...v0.10.2
[0.10.1]: https://github.com/TetronIO/JIM/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/TetronIO/JIM/compare/v0.9.1...v0.10.0
[0.9.1]: https://github.com/TetronIO/JIM/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/TetronIO/JIM/compare/v0.8.1...v0.9.0
[0.8.1]: https://github.com/TetronIO/JIM/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/TetronIO/JIM/compare/v0.7.1...v0.8.0
[0.7.1]: https://github.com/TetronIO/JIM/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/TetronIO/JIM/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/TetronIO/JIM/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/TetronIO/JIM/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/TetronIO/JIM/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/TetronIO/JIM/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/TetronIO/JIM/compare/v0.2.0-alpha...v0.3.0
[0.2.0-alpha]: https://github.com/TetronIO/JIM/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/TetronIO/JIM/releases/tag/v0.1.0-alpha
