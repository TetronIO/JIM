# SQL Database Connector: Implementation Plan

- **Status:** Doing (Phases 1-6 and 8 complete; Phase 7 matrix at 29 of 36 cells, the Delta cells and the 500,000-row import outstanding)
- **Issue:** [#170](https://github.com/TetronIO/JIM/issues/170)
- **PRD:** [PRD_SQL_DATABASE_CONNECTOR.md](../../prd/doing/PRD_SQL_DATABASE_CONNECTOR.md)

## Overview

Implement the JIM SQL Connector for Microsoft SQL Server and Oracle Database (Priority 1 providers), covering schema discovery, full and delta import, and transactional export, built on the connector contract as it exists after #875 (centralised dispatch), #1046 (Decimal attribute data type) and #637/#1161 (connector phases and progress). All three prerequisites are delivered; dependency governance for `Microsoft.Data.SqlClient` 7.0.2 and `Oracle.ManagedDataAccess.Core` 23.26.300 was approved 2026-07-31.

The LDAP Connector is the structural template (calls-based import/export, watermark in `PersistedConnectorData`, delta fallback semantics, live connectivity validation, certificate-store TLS trust); the File Connector is the template for auto-confirmed exports and Decimal value handling. Requirements, type mapping, non-goals and resolved design decisions live in the PRD and are not repeated here.

**Progress contract note:** the PRD's requirement 21 originally referenced the callback pattern from #637. That mechanism was superseded by the step model (#1161, #1212): connectors declare their steps via `IConnectorPhases.GetPhases()`, rendered as sub-steps within the run's Connector step, and narrate through a non-null `IConnectorProgress`, which now also carries expected-object-count and objects-read reporting for real percentages and time remaining. Requirement 21 was updated to the step model on 2026-08-02; this plan builds against the current contract.

## Technical Architecture

### Current state

- `ConnectorConstants.SqlConnectorName` ("JIM SQL Connector") is reserved in `src/JIM.Connectors/Constants.cs`; `ConnectorFactory.CreateConnectorInstance` falls through to `NotSupportedException` for it, pinned by `ConnectorFactoryTests.Create_SqlConnectorName_ThrowsNotSupportedException`.
- Worker-side dispatch is fully capability-driven (`IConnectorImportUsingCalls` / `IConnectorExportUsingCalls` pattern matching); no processor changes are needed for a new calls-based connector.
- Dormant `phase2` containers (SQL Server 2022, Oracle XE 21c, PostgreSQL 16, MySQL 8) exist in `test/integration/docker/docker-compose.integration-tests.yml`, but the runner has no `phase2` stack-up handling and both database healthchecks carry latent defects (wrong `sqlcmd` path for SQL Server 2022; Oracle EZConnect string with an unescaped `@` in the password).
- All three admin surfaces (portal, REST API, PowerShell) render connector settings generically from `IConnectorSettings.GetSettings()`; a new connector gets surface parity for free.

### Component design

```
src/JIM.Connectors/Sql/
├── SqlConnector.cs              # IConnector entry point; settings, capabilities, validation, TLS, dispatch
├── SqlConnectorImport.cs        # internal: full/delta import, keyset paging, watermark handling
├── SqlConnectorExport.cs        # internal: per-object transactional create/update/delete
├── SqlConnectorSchema.cs        # internal: table/view/column discovery, object type configuration
├── SqlConnectorPhases.cs        # internal static: phase keys/names for IConnectorPhases
├── SqlConnectorConstants.cs     # internal static: setting names, defaults
├── SqlConnectorWatermark.cs     # internal: serialised delta watermark (PersistedConnectorData carrier)
├── SqlObjectTypeConfiguration.cs# internal: parsed object type definitions (see Phase 3)
└── Providers/
    ├── ISqlProvider.cs          # dialect seam: connection building, quoting, paging, catalogue queries,
    │                            #   parameter prefix, generated-key retrieval, type mapping
    ├── SqlServerProvider.cs
    ├── OracleProvider.cs
    └── SqlTypeMapper.cs         # SQL type family -> AttributeDataType per the PRD table
```

`SqlConnector` implements: `IConnector`, `IConnectorCapabilities`, `IConnectorSettings`, `IConnectorSchema`, `IConnectorImportUsingCalls`, `IConnectorExportUsingCalls`, `IConnectorCredentialAware`, `IConnectorCertificateAware`, `IConnectorSecureEndpoint`, `IConnectorPhases`, `IDisposable`. Capability declarations per PRD requirement 19; the reflection-driven `ConnectorCapabilityMirror` handles persistence with no further plumbing.

`ISqlProvider` mirrors the role of `ILdapOperationExecutor` as the testability seam, but carries dialect knowledge rather than wrapping a sealed type (`DbConnection`/`DbCommand` are mockable directly). Everything dialect-specific lives behind it so that Priority 2 providers (PostgreSQL, MySQL) are additive.

### Key contract obligations (verified against source)

- `ImportAsync(...)` receives the original `persistedConnectorData` on every page and returns new watermark data on the first page; the Worker saves it only after all pages complete. Termination contract: an empty `PaginationTokens` list means "no more data".
- `ExportAsync(...)` must return one `ConnectedSystemExportResult` per Pending Export in the same order (positional contract; never filter or reorder).
- `OpenImportConnection`/`OpenExportConnection` receive the persisted connector state at connection-open (post-#1169); `CloseImportConnection`/`CloseExportConnection` return `string?`, null in the normal case, non-null only to override persisted state after import-result persistence. All four remain synchronous; `ValidateSettingValues` and `GetSchemaAsync` run on Blazor Server circuits. Sync-over-async bridging follows the established `Task.Run(...).GetAwaiter().GetResult()` precedent from `ServerCertificateDiagnosis.LoadTrustedCertificates`.
- Connector-declared phases render as sub-steps within the run's Connector step (post-#1161/#1212). `IConnectorProgress` additionally carries `ReportExpectedObjectCountAsync` (drives a real percentage and time remaining) and `ReportObjectsReadAsync` (moves counters while a single long call is in flight).
- Typed import values ride `ConnectedSystemImportObjectAttribute` (note `DateTimeValue` and `BoolValue` are single-valued; all other types are lists; references are strings in `ReferenceValues` resolved by JIM). Decimal values must round-trip through `JIM.Utilities.DecimalAttributeValue` (`ToCanonicalString`/`TryParse`), never `double` or culture-sensitive formatting.
- Full imports leave `ChangeType` as `NotSet` (JIM infers create/update); only delta imports emit explicit `Create`/`Update`/`Delete`.

## Implementation Phases

Test-driven throughout: each phase writes failing tests first, in `test/JIM.Worker.Tests/Connectors/` following the LDAP/File naming conventions (`MethodName_Scenario_ExpectedResult`).

### Phase 1: Provider abstraction and type mapping ✅

- Add `Microsoft.Data.SqlClient` 7.0.2 and `Oracle.ManagedDataAccess.Core` 23.26.300 to `src/JIM.Connectors/JIM.Connectors.csproj` (exact versions); `dotnet restore JIM.sln --force-evaluate`; review the lock-file diff (ripples into every downstream project's `packages.lock.json`); commit lock files with the version change. Never add `Microsoft.Data.SqlClient.Extensions.Azure`.
- `ISqlProvider`, `SqlServerProvider`, `OracleProvider`: connection-string construction from discrete settings, identifier quoting, parameter prefixing, keyset-pagination SQL, schema-catalogue queries, generated-key retrieval strategy, trivial-connectivity query.
- `SqlTypeMapper` implementing the PRD type-mapping table, including the Oracle `NUMBER(1)`-to-Boolean opt-in, `RAW(16)` GUID handling via `IdentifierParser.FromRfc4122Bytes` (big-endian, per `engineering/plans/doing/GUID_UUID_HANDLING.md`), and FLOAT/REAL to Decimal.
- Unit tests: dialect differences per provider, type-mapping matrix, Decimal canonical round-trip.

### Phase 2: Connector skeleton, settings, validation, registration ✅

- `SqlConnector` with capability declarations and Connectivity settings per PRD requirements 1-2: Database Type drop-down, Host, Port, database name vs Oracle service name/SID via conditional settings, Username, Password (`StringEncrypted`), TLS options, connection timeout, and the required zoneless time-zone setting with a visible UTC default (`DefaultStringValue`). Do not name any setting "Mode" (File Connector semantics are keyed to that literal in `ConnectedSystemExtensions`).
- `ValidateSettingValues`: LDAP-thin body; live connectivity test (open, trivial query per provider, close) in try/finally; failure results carry `ErrorMessage` + `Exception`.
- TLS trust per the #1142 precedent: implement `IConnectorSecureEndpoint.ResolveSecureEndpoint` (null unless TLS is enabled), reuse the shared `ServerCertificateDiagnosis`/`ServerCertificateProbe` machinery to surface refused certificates with their details. Per-provider trust wiring is this phase's design task: SQL Server via `Microsoft.Data.SqlClient` encryption settings, Oracle via ODP.NET TLS configuration, in both cases validating against the operating system bundle plus Admin > Certificates as additive anchors; no blanket trust-server-certificate toggle.
- Registration: `ConnectorFactory.CreateConnectorInstance` branch; `SeedingServer.SeedAsync` Connector Definitions region and `SyncBuiltInConnectorDefinitionsAsync` list; invert `Create_SqlConnectorName_ThrowsNotSupportedException` to `ReturnsSqlConnector`; add `SqlConnectorPhaseConformanceTests : ConnectorPhaseConformanceTests`.
- `SqlConnectorPhases` + `GetPhases()` per run type ("Executing query", "Fetching rows", "Writing rows" vocabulary).

### Phase 3: Schema discovery and object type configuration ✅

- Design decision (recommended approach): object type definitions are richer than the flat settings framework expresses (N object types, each with a primary table/view or admin-supplied `SELECT`, anchor column(s), and N related tables with join conditions). Represent them as a structured JSON document in a `Text` setting in the Schema category ("Object Types"), parsed into `SqlObjectTypeConfiguration`, validated in `ValidateSettingValues` with precise error messages, and documented with copy-paste examples. This is privileged administrator input per the PRD trust model. Alternative (rejected for v1): extending the settings framework with repeating groups; disproportionate for one connector.
- `GetSchemaAsync`: enumerate tables/views and columns via provider catalogue queries; apply the type mapper; emit one `ConnectorSchemaObjectType` per configured object type with primary-key-derived `RecommendedExternalIdAttribute`; surface related-table value columns as multi-valued attributes; explicit per-column Reference designation from configuration, never inferred. Where declared foreign-key constraints match a configured object type's anchor, surface them as pre-populated Reference suggestions for the administrator to confirm (PRD requirement 6).
- Unit tests against mocked `DbConnection`/provider fakes; catalogue query correctness verified later by integration tests.

### Phase 4: Full import ✅

- `SqlConnectorImport`: keyset pagination on the anchor (never OFFSET) honouring `ConnectedSystemRunProfile.PageSize`, one `ConnectedSystemPaginationToken` per object type carrying the last-seen anchor (`StringValue`); empty token list terminates.
- Per page: materialise rows, gather multi-valued attributes from related tables (single batched query per page, keyed on the page's anchors), emit typed attribute values, references as anchor strings in `ReferenceValues`.
- Zoneless DateTime interpretation per the configured time zone; offset-carrying types normalised to UTC.
- Progress: `EnterPhaseAsync` per object type/page; `ReportExpectedObjectCountAsync` from a cheap `COUNT(*)` on the primary table/view (PRD requirement 21: databases can state result-set sizes cheaply, so a real percentage and time remaining are mandatory, not optional); `ReportObjectsReadAsync` where one call drains multiple provider pages; `ReportAsync` for detail the counts cannot carry.
- Unit tests: paging termination, related-table gathering, type fidelity, error classification (`ConnectedSystemImportObjectError`) for unparseable values.

### Phase 5: Delta import ✅

- Two modes per the PRD (change-log table with deletes; watermark column create/update-only), selected per Connected System via an extensible drop-down (SQL Server Change Tracking is a fast-follow member).
- `SqlConnectorWatermark` serialised to `ConnectedSystemImportResult.PersistedConnectorData` on the first page, following the `LdapConnectorRootDse` round-trip rules exactly (original watermark read on every page; new value saved by the Worker after the run completes).
- Missing/undeserialisable watermark: `CannotPerformDeltaImportException`, or fallback to full import with `WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport` (mirrors `LdapConnectorImportDeltaFallbackTests` coverage).
- Change-log mode emits explicit `Create`/`Update`/`Delete` change types beyond the persisted watermark and advances it transactionally with the read.

### Phase 6: Export ✅

- `SqlConnectorExport`: per-object `DbTransaction` spanning the parent row and related-table maintenance; parameterised statements only, identifiers quoted through the provider; provider-specific generated-key retrieval (`OUTPUT INSERTED.*` / `RETURNING ... INTO`) returned as `ConnectedSystemExportResult.Succeeded(externalId)`.
- Positional result contract; per-object failures return `Failed(...)` without poisoning the batch, feeding the existing retry/backoff machinery.
- `SupportsAutoConfirmExport => true` (committed transaction is a verified write); `SupportsParallelExport => false` with the provider seam keeping a later flip config-only.
- Unit tests: transaction boundaries, generated-key capture, related-table add/remove, error isolation, Decimal export formatting via `DecimalAttributeValue.ToCanonicalString`.

### Phase 7: Integration tests (provider × capability matrix)

- New `scenarios/Invoke-Scenario16-SqlConnectorMatrix.ps1` + `Setup-Scenario16.ps1` (filesystem convention self-registers it in the runner menu; add the `*Scenario16*` fallback description). Scenario 15 was consumed by the SCIM 2.0 Connector (#545); `engineering/INTEGRATION_TESTING.md`'s road-mapped database scenario numbering (15-17) is stale and must be renumbered in this phase. Parameterise by provider via a `Get-DatabaseConfig -Provider SqlServer|Oracle` helper in `utils/Test-Helpers.ps1` (the `Get-DirectoryConfig` analogue), with `-Provider`/`-Quick`/`-FullMatrix` filters following the Scenario 11 precedent: representative subset for the regular gate, full matrix before release.
- Deterministic SQL seeder with a row-count parameter and content-hash caching (the `Generate-TestCSV.ps1` model). The 500,000-row scale requirement is satisfied by the seeder directly; LDAP data-scale templates do not apply to this scenario and the `Scale500k25kGroups` OpenLDAP-only guards are left untouched.
- Runner/infrastructure work: add `phase2` profile stack-up handling to `Run-IntegrationTests.ps1` (currently reset-only); add `oracle-hris-b` and `mysql-test` to `Wait-SystemsReady.ps1`'s `$phase2Systems`; fix the SQL Server healthcheck (`/opt/mssql-tools18/bin/sqlcmd` with `-C`); fix the Oracle healthcheck credential quoting; replace the Oracle XE 21c image with an Oracle Database Free 23ai image suitable for CI; correct the stale "Scenario 4" compose comments to Scenario 16; add container memory sizing for the scale run.
- Matrix coverage per the PRD Testing Requirements table (12 capability rows × both Priority 1 providers), including the type-mapping round-trip and configuration-validation rows.
- Test strategy (PRD, decided 2026-08-02): this matrix scenario is the correctness gate; cross-technology regression breadth follows via the road-mapped Multi-Source Aggregation scenario once the matrix is green. Databases are not retrofitted into scenarios 1-14; if regressions slip past both vehicles, parameterising Scenario 1's HR source (CSV vs SQL) is the one retrofit worth considering.
- Update `engineering/INTEGRATION_TESTING.md` (Phase 2 sections, port tables, Oracle start-up troubleshooting) and `test/integration/README.md`.

### Phase 8: Documentation and release polish ✅

- `docs/connectors/jim-sql-connector.md`: per-provider configuration, object type configuration examples, type mapping, security/least-privilege guidance, the Oracle Free Distribution licence note, and the wider-database-support callout inviting feedback via the [Ideas category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/ideas).
- Dedicated delta import setup pages (change-log table; watermark column) linked from the connector page, each covering setup end to end including the watermark mode's documented no-delete semantics.
- Ship a copy of Oracle's LICENSE.txt with the distribution (third-party notices) per the governance record.
- Nav/index updates, `docs/concepts/connected-systems.md` and roadmap rows, `JIM_AI_ASSISTANT_CONTEXT.md` connector table + version bump, CHANGELOG entry, PRD to `done/` on issue closure.

Delivered 2026-08-17: `docs/connectors/jim-sql-connector.md` plus the two delta-import setup pages, `third-party-notices/` (the Oracle terms, copied into every image), the concept diagrams and README exports showing the SQL Connector live, and the AI assistant context at version 1.8. `docs/concepts/connected-systems.md` does not exist, so that item had nothing to update; the PRD moves to `done/` when #170 closes.

## Success Criteria

The PRD's acceptance criteria are the contract. In plan terms: all eight phases land with green unit tests and zero build warnings; the Scenario 15 representative subset runs in the regular integration gate; the full provider × capability matrix plus the 500,000-row import passes against SQL Server 2022 and Oracle Database Free 23ai before release; public documentation ships in the same release.

## Dependencies

- #875, #1046, #637/#1161: delivered.
- Dependency governance: approved 2026-07-31 (`Microsoft.Data.SqlClient` 7.0.2, `Oracle.ManagedDataAccess.Core` 23.26.300), recorded in the PRD.
- Oracle CI image choice (Phase 7): Oracle's own registry requires an authenticated licence-accepting pull; the widely used `gvenzl/oracle-free` Docker Hub mirror does not. Test-only images sit in the pinning policy's accepted-mutable row, but the mirror is a third-party redistribution; decide at Phase 7 start.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Object type configuration outgrows a JSON Text setting (usability) | v1 ships JSON with strong validation and documented examples; revisit a dedicated editor UI on feedback. Flagged as the Phase 3 design decision |
| Per-provider TLS trust wiring differs materially (SqlClient vs ODP.NET) | Isolated in Phase 2 behind the provider seam; shared probe/diagnosis machinery already connector-agnostic; certificate-store requirement is a PRD hard requirement, not best-effort |
| Sync-over-async deadlocks from `void` open/close and Blazor circuits | Follow the established `Task.Run(...).GetAwaiter().GetResult()` bridge precedent; no `.Result`/`.Wait()` |
| Keyset pagination with composite anchors generates fragile SQL | Single-column anchor is the documented default; composite support tested explicitly in the dialect unit tests |
| Oracle Database Free start-up time destabilises CI | faststart image variant; healthcheck `start_period` tuned; scale/matrix runs release-gated, not per-PR |
| Infinite import loop from mis-emitted pagination tokens | Termination contract unit-tested; watermark round-trip tests mirror the LDAP delta fallback suite |
| New transitive NuGet graph trips NuGetAudit/Trivy | Lock-file diff reviewed at Phase 1; pinned-forward transitive override precedent exists (`System.Security.Cryptography.Xml`) |

## Benefits

Direct HR-database synchronisation closes JIM's largest capability gap against established ILM products and unblocks the primary joiner/mover/leaver use case for Oracle-backed and SQL Server-backed estates, with air-gap deployability preserved (managed providers only, no native drivers).
