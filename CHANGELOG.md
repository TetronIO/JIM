# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- ✨ `GET /api/v1/userinfo` endpoint — returns the authenticated user's JIM identity, roles, and authorisation status without requiring Administrator privileges
- ✨ `Connect-JIM` now verifies authorisation after authentication and warns if the user has no JIM identity, with clear guidance to sign in via the web portal first
- 🖥️ Improved 403 error messages in the PowerShell module — now explains the likely cause (no JIM identity) and how to resolve it

### Changed

- 🎨 New default theme — "Navy O6" features a deeper navy background with a purple accent palette, improved button contrast for outlined and text variants, and refined surface colours for better visual depth
- 🗑️ Removed legacy navy-o1 through navy-o4 themes — consolidated to navy-o5 and the new navy-o6 default
- 🔄 "Data Generation" renamed to "Example Data" across the entire stack for consistent naming — models, API routes (`/example-data/`), PowerShell cmdlets (`Get-JIMExampleDataTemplate`, `Invoke-JIMExampleDataTemplate`), database tables, and UI all now share the "Example Data" family prefix
- ⚡ Database migrations flattened into a single `InitialCreate` migration for faster first-start performance and simpler codebase

### Fixed

- 🐛 Resolved intermittent DbContext concurrency errors across all Blazor Server pages — overlapping async lifecycle methods (e.g. data load and table pagination) no longer share a single database context

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
- 🔒 Unresolved reference fail-fast assertions added to integration test Scenarios 1, 2, and 8

#### Database Resilience (#408)

- 🐛 Transient database errors now return HTTP 503 (Service Unavailable) with a `Retry-After` header instead of HTTP 400 (Bad Request)
- ⚡ Transient database failures handled gracefully at API level with retry guidance
- ⚡ Connection pool sizing reduced from 50 to 30 per service to leave headroom within PostgreSQL's `max_connections`
- 📦 Development database (`db.yml`) now explicitly sets `max_connections=200` to match the full Docker stack

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

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.6.1...HEAD
[0.6.1]: https://github.com/TetronIO/JIM/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/TetronIO/JIM/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/TetronIO/JIM/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/TetronIO/JIM/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/TetronIO/JIM/compare/v0.2.0-alpha...v0.3.0
[0.2.0-alpha]: https://github.com/TetronIO/JIM/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/TetronIO/JIM/releases/tag/v0.1.0-alpha
