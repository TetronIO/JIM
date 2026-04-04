# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

#### Service Settings REST API & PowerShell Cmdlets

- ✨ New REST API for managing service settings (`GET/PUT/DELETE /api/v1/service-settings`) — enables automation of change tracking, sync page size, history retention, and other operational settings
- ✨ New PowerShell cmdlets: `Get-JIMServiceSetting`, `Set-JIMServiceSetting`, `Reset-JIMServiceSetting` — manage service settings from the command line or automation scripts

#### Integration Test Runner Enhancements

- ✨ `-LogLevel` parameter for integration test runner — override log verbosity (Verbose/Debug/Information/Warning/Error/Fatal) for the test run without permanently modifying `.env`
- ✨ `-DisableChangeTracking` switch for integration test runner — disable CSO and MVO change tracking during large-scale tests to reduce database writes and improve throughput
- 🖥️ Interactive menus for log level and change tracking selection when running tests without explicit parameters

### Fixed

- 🔒 Safe cancellation for sync operations (#339) — when an admin cancels a running Full Sync or Delta Sync, the current page's flush pipeline now completes before exiting. Previously, cancellation could leave orphaned metaverse objects without corresponding pending exports, causing target systems to silently miss updates.
- 🐛 Fixed sync progress bar showing inflated object counts (CSOs + pending exports) instead of just CSOs — progress percentage and ETA are now accurate for Full Sync and Delta Sync

### Changed

- ⚡ LDAP export concurrency is now auto-tuned based on the detected directory server type — AD DS and OpenLDAP default to 16 concurrent operations (up from 4), while Samba AD and unknown directories remain at 4 for compatibility. Administrators who have manually configured the value will not be affected.

### Performance

- ⚡ Pending Exports table on CSO detail page now uses server-side paging — pages with thousands of pending changes (e.g. 10K member adds) load instantly instead of rendering all rows at once
- ⚡ Bounded memory sync pipeline — change tracker cleared at every page boundary and export evaluation cache loaded per-page instead of upfront, enabling sync of 100K+ objects without out-of-memory crashes (#451)
- ⚡ All export evaluation and pending export cache queries now use `AsNoTracking`, eliminating unnecessary entity tracking overhead during sync
- ⚡ Per-page memory diagnostics logging — administrators can monitor memory usage across sync pages to verify bounded memory behaviour

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
- 📖 Comprehensive [Deployment Guide](docs/DEPLOYMENT_GUIDE.md) covering prerequisites, topology options, TLS, reverse proxy, upgrades, and monitoring
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

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.8.1...HEAD
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
