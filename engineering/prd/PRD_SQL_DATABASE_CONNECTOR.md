# SQL Database Connector

- **Status:** Planned
- **Created:** 2026-07-15
- **Author:** Jay Van der Zant (with Claude)
- **Issue:** [#170](https://github.com/TetronIO/JIM/issues/170)

## Problem Statement

Organisations hold authoritative identity data in relational databases: HR systems, payroll, student records, and line-of-business applications. Today JIM can synchronise with LDAP directories and flat files only, so connecting a database-backed HR system means exporting it to CSV on a schedule and importing the file; a fragile, high-latency pattern that loses deletion fidelity and doubles the operational surface.

Direct SQL connectivity is table stakes for traditional ILM products; every established product in this space ships a database connector, and the HR systems JIM's initial customer prospects run are predominantly Oracle-backed, with SQL Server a close second. Without a SQL Database Connector, JIM cannot serve its primary "HR-driven joiner/mover/leaver" use case against real customer estates.

The integration test infrastructure already anticipates this connector: dormant SQL Server, Oracle and MySQL containers exist in `test/integration/docker/docker-compose.integration-tests.yml` behind the `phase2` profile, and the name "JIM SQL Connector" is reserved in `ConnectorConstants` with a `NotSupportedException` stub in `ConnectorFactory`.

## Goals

- An administrator can configure a Connected System against Microsoft SQL Server or Oracle Database entirely from the admin UI, with credentials encrypted at rest and connectivity validated at save time.
- JIM can discover schema (tables, views, columns, data types) from a configured database and map columns onto Connected System Object Types and attributes.
- Full imports work against tables or views, including multi-valued attributes and reference attributes sourced from related tables, at 100,000-row scale within existing Run Profile paging behaviour.
- Delta imports work via at least two dialect-agnostic mechanisms (change-log table and watermark column), with the same graceful fallback-to-full-import behaviour the LDAP Connector established.
- Exports create, update and delete rows (including related-table rows) transactionally per object, returning database-generated keys as external IDs.
- All operations surface as Activities with per-object Run Profile Execution Items, identical to the existing connectors.
- The connector ships with no native driver dependencies: managed ADO.NET providers only, preserving air-gapped deployability.

## Non-Goals

- No ODBC or OLE DB support; managed providers only.
- No NoSQL databases (separate issue territory).
- No cloud-managed-identity authentication (Azure SQL Managed Identity, AWS RDS IAM); connection-string-level SQL authentication and TLS only for now.
- No Windows/Integrated (Kerberos) authentication in the first release; JIM runs in Linux containers where keytab management is a significant operational burden. Revisit on customer demand.
- No Oracle Wallet authentication in the first release (file-based wallet distribution; revisit with a File-type setting when demanded).
- No SQL query editor with syntax highlighting, and no query-result preview UI in the first release; configuration is via settings fields.
- No arbitrary-SQL scheduled jobs; that capability already exists as the Scheduler's SQL Script step and is unrelated to synchronisation.

## User Stories

1. As an identity administrator, I want to connect JIM directly to our Oracle-backed HR database as an authoritative source, so that joiners, movers and leavers flow into the metaverse without intermediate file exports.
2. As an identity administrator, I want scheduled delta imports from the HR database, so that frequent synchronisation cycles stay cheap even with hundreds of thousands of person records.
3. As an identity administrator, I want JIM to export accounts into a SQL Server application database (rows created, updated and deleted as identities change), so that the application provisions and deprovisions automatically.
4. As an identity administrator, I want to configure the database connection from the admin UI with a test-at-save connectivity check and an encrypted password, so that I never have to hand-edit configuration or store secrets in plain text.
5. As an auditor, I want every import and export run recorded as an Activity with per-object detail, so that database synchronisation is as reviewable as directory synchronisation.

## Requirements

### Functional Requirements

**Connectivity and configuration**

1. The connector must present discrete Connectivity settings (database type, host, port, database/service name, username, password, TLS options, connection timeout) and build the provider connection string internally. The password must use the `StringEncrypted` setting type so it is encrypted at rest via the existing credential protection mechanism and redacted from configuration snapshots.
2. Provider-specific settings (for example Oracle service name vs SID) must use the existing conditional-settings framework (`RequiredWhenSetting`/`RequiredWhenValue`, `RequiredGroup` cardinalities) rather than free-text conventions.
3. `IConnectorSettings.ValidateSettingValues` must perform a live connectivity test (open connection, trivial query, close), following the LDAP Connector's pattern, so invalid configuration cannot be saved.
4. Priority 1 providers: Microsoft SQL Server and Oracle Database. Priority 2: PostgreSQL and MySQL/MariaDB. The provider abstraction (`ISqlProvider` or equivalent) must isolate dialect differences: parameter prefix, identifier quoting, paging syntax, schema-catalogue queries, type mapping, and generated-key retrieval.

**Schema discovery**

5. `IConnectorSchema.GetSchemaAsync` must enumerate tables and views (with schema qualification) and their columns, mapping SQL types to JIM attribute types per the type-mapping table below.
6. Object type configuration must support: a primary table or view per object type, an anchor (external ID) column or column set, an optional admin-supplied `SELECT` statement in place of a table/view, and zero or more related tables for multi-valued attributes (each with a join condition to the parent anchor).
7. Multi-valued related tables must surface their value column as a multi-valued attribute of the parent object type in the discovered schema.

**Type mapping**

8. SQL types must map to `AttributeDataType` as follows (per provider, with provider-specific aliases handled in the provider layer):

| SQL type family | JIM type | Notes |
|---|---|---|
| VARCHAR/NVARCHAR/CHAR/TEXT/CLOB | Text | |
| INT/SMALLINT/TINYINT | Number | |
| BIGINT | LongNumber | |
| BIT/BOOLEAN | Boolean | Oracle NUMBER(1) opt-in via configuration |
| DATETIME/DATETIME2/TIMESTAMP/DATE | DateTime | Zoneless values interpreted per requirement 9 |
| DATETIMEOFFSET/TIMESTAMP WITH TIME ZONE | DateTime | Normalised to UTC |
| UNIQUEIDENTIFIER/RAW(16) with GUID content | Guid | |
| VARBINARY/BLOB/RAW | Binary | |
| DECIMAL/NUMERIC/MONEY/FLOAT | Text | Lossless string round-trip; upgrades to Decimal when #1046 lands |
| Foreign-key columns holding another object type's anchor | Reference | Explicit per-column configuration, not inferred |

9. Zoneless date/time columns are ambiguous at the wire level, so the connector must expose a per-Connected-System setting declaring how to interpret them (UTC, or a named IANA time zone), applied on import and inverted on export. Offset-carrying types need no setting. JIM stores all DateTime values in UTC internally; this setting resolves source semantics, it does not change JIM's storage model.

**Import**

10. Full import must page through the primary table/view using keyset pagination on the anchor (never OFFSET), honouring the Run Profile page size and the existing `ConnectedSystemPaginationToken` mechanism, and emit `ConnectedSystemImportObject`s with typed attribute values, multi-valued attributes gathered from related tables, and reference attributes carrying the referenced object's anchor value.
11. Delta import must support two dialect-agnostic modes, selected per Connected System:
    - **Change-log table**: a customer-maintained table/view containing at minimum the anchor, a change type (create/update/delete), and a monotonic sequence or timestamp. JIM reads rows beyond its persisted watermark, emits corresponding import objects (including `Delete` change types), and persists the new watermark via `ConnectedSystemImportResult.PersistedConnectorData`. This is the only mode that observes deletions and is the recommended configuration.
    - **Watermark column**: a last-modified timestamp or version column on the primary table (and on related tables). Detects creates and updates only; the documentation must state plainly that deletions require periodic full imports in this mode.
12. A delta import requested without a usable persisted watermark must throw `CannotPerformDeltaImportException` or fall back to full import with the `DeltaImportFallbackToFullImport` warning, mirroring the LDAP Connector's behaviour.
13. Provider-native change detection (SQL Server Change Tracking) is a fast-follow, not part of the first release, but the delta-mode setting must be designed as an extensible enumeration so it can be added without reshaping configuration.

**Export**

14. Export must translate Pending Exports into parameterised `INSERT`/`UPDATE`/`DELETE` statements (values always as parameters, identifiers quoted through the provider), maintaining related-table rows for multi-valued attribute changes within the same database transaction as the parent row.
15. Creates against database-generated keys (identity/sequence columns) must return the generated value as the object's external ID in `ConnectedSystemExportResult`, following the LDAP and File Connector precedents.
16. The connector must declare `SupportsAutoConfirmExport = true`; a committed transaction is a verified write, so a confirming import is not required for correctness.
17. Optional stored-procedure export (one procedure per create/update/delete, receiving attribute values as parameters) may ship in the first release if design review confirms it does not complicate the transactional path; otherwise it is the first fast-follow.
18. Export failures must map per-object onto `ConnectedSystemExportResult` errors, feeding the existing Pending Export retry/backoff machinery; a failed object must not poison its batch.

**Capabilities declaration**

19. The connector must declare: `SupportsFullImport`, `SupportsDeltaImport`, `SupportsExport`, `SupportsPaging`, `SupportsAutoConfirmExport`, `SupportsUserSelectedExternalId` true; `SupportsPartitions`, `SupportsPartitionContainers`, `SupportsSecondaryExternalId`, `SupportsFilePaths` false. `SupportsParallelExport` is an open question (see below).

**Security**

20. Connection strings and credentials must never be logged; the trust model must be documented: connector configuration (including any admin-supplied `SELECT`) is privileged administrator input, the injection surface to defend is value parameterisation and identifier quoting, and deployments should use least-privilege database accounts (read-only for import-only systems).

### Non-Functional Requirements

- 100,000-row full import completes within the same order of magnitude as the LDAP Connector at equivalent scale, without unbounded memory growth (streaming reads, page-at-a-time materialisation).
- Air-gap deployable: no internet access at runtime, no native driver installation, all providers bundled as managed NuGet packages.
- Every new NuGet package passes the Third-Party Dependency Governance workflow before adoption, including licence review (the Oracle managed driver ships under Oracle's free-use distribution licence and needs explicit sign-off and customer-facing documentation of its terms).

### Testing Requirements

Integration coverage must be a **provider × capability matrix**, not a single happy-path scenario. Scenario logic must be parameterised by provider (one scenario implementation, executed once per supported database server), so that adding a provider extends the matrix without duplicating scenario code. Every capability row below must pass against every supported provider before that provider is declared supported:

| Capability under test | Exercised behaviour |
|---|---|
| Full import | Table and view sources, keyset paging across multiple pages, typed attribute values per the mapping table |
| Multi-valued import | Related-table attributes gathered onto the parent object |
| Reference import | Anchor-carrying columns resolved to Connected System Object references (including forward references resolved late) |
| Delta import: change-log table | Creates, updates and deletes propagated; watermark persisted and honoured across runs |
| Delta import: watermark column | Creates and updates propagated; documented no-delete semantics verified (deletion NOT detected) |
| Delta fallback | Missing/invalid watermark falls back to full import with the standard warning |
| Export: create | Row inserted; database-generated key returned as external ID; auto-confirmation |
| Export: update | Attribute changes applied; multi-valued related-table rows added and removed transactionally with the parent |
| Export: delete | Row (and related rows) removed; per-object error isolation on failure |
| Reference export | Anchor values written for reference attributes |
| Type-mapping round-trip | Each mapped SQL type imports and exports losslessly, including zoneless DateTime interpretation and exact-numeric Text round-trip |
| Configuration validation | Save-time connectivity test passes/fails correctly (wrong credentials, unreachable host) |

The full matrix must run green for Priority 1 providers (SQL Server, Oracle) before first release, and for each Priority 2 provider before it is declared supported. Because the full matrix is expensive, the regular integration gate may run a representative subset (at minimum: one provider end-to-end plus configuration validation on all providers), with the full matrix required before release; the split is decided at plan time within the existing runner's scenario structure. At least one provider must additionally run the 100,000-row scale import.

## Examples and Scenarios

### Scenario 1: HR full import from Oracle

**Given**: a Connected System configured against an Oracle HR database, object type `PERSON` mapped to view `HR.V_EMPLOYEES` with anchor `EMPLOYEE_ID`, multi-valued `PHONE_NUMBERS` from related table `HR.EMPLOYEE_PHONES`, and reference attribute `MANAGER` from column `MANAGER_EMPLOYEE_ID`
**When**: a Full Import Run Profile executes
**Then**: JIM pages through the view on `EMPLOYEE_ID`, stages one Connected System Object per row with phones as a multi-valued attribute and `MANAGER` as a Reference to the manager's Connected System Object, and the Activity records per-object execution items.

### Scenario 2: Delta import via change-log table

**Given**: the same system with delta mode "Change-log table" pointed at `HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE, CHANGED_AT)`, and a persisted watermark of `2026-07-14 22:00:00Z`
**When**: a Delta Import Run Profile executes after HR terminates employee 4711
**Then**: JIM reads only rows with `CHANGED_AT` beyond the watermark, emits a `Delete` import object for employee 4711, persists the new watermark, and the subsequent synchronisation disconnects the Connected System Object and applies deletion rules.

### Scenario 3: Export to a SQL Server application database

**Given**: an outbound Synchronisation Rule provisioning `APP_USERS` rows in SQL Server, where `APP_USERS.ID` is an identity column and group memberships live in `APP_USER_ROLES`
**When**: a joiner is provisioned and a Pending Export with Create change type executes
**Then**: the connector inserts the `APP_USERS` row and its `APP_USER_ROLES` rows in one transaction, reads the generated `ID`, and returns it as the external ID; the Pending Export auto-confirms without a confirming import.

### Scenario 4: Save-time connectivity validation

**Given**: an administrator entering connection settings with a wrong password
**When**: they save the Connected System
**Then**: setting validation fails with the provider's authentication error surfaced in the UI, and the configuration is not saved.

## Constraints

- Managed ADO.NET providers only; no ODBC/OLE DB, no DSNs, no native driver installation.
- Must work air-gapped and on-premises only; no cloud service dependencies.
- No third-party identity product names in code, comments or documentation; use generic terms when describing prior art.
- The Third-Party Dependency Governance workflow applies per provider package; provider packages must be pinned per `engineering/DEPENDENCY_PINNING.md`.
- Universal code style rules apply (British English, copyright headers, no em dashes).

## Affected Areas

| Area | Impact |
|------|--------|
| JIM.Connectors | New `Sql/` connector (structure mirroring `LDAP/`: main class + import/export/schema/provider helpers); provider abstraction; new NuGet references (see Open Questions on project placement) |
| JIM.Application | `ConnectorFactory` registration; `ConnectedSystemServer` dispatch sites (coordinate with #875); seeding of the new Connector Definition |
| Worker | None expected beyond existing processor dispatch (calls-based import/export) |
| Database | None expected (connector state rides existing `PersistedConnectorData`/pagination-token mechanisms) |
| UI | None expected beyond the settings framework rendering the new Connector Definition's settings |
| Integration tests | Activate the dormant `phase2` SQL Server and Oracle containers; new scenario(s) covering import, delta via change-log table, and export; replace the Oracle XE 21c image with an Oracle Database Free image suitable for CI |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/connectors/jim-sql-connector.md` | New connector guide: configuration per provider, delta mode guidance (change-log table recommended; watermark limitations), type mapping, security/least-privilege guidance, Oracle driver licence note |
| `docs/connectors/index.md` (or equivalent nav) | Add the new connector |
| `engineering/DEVELOPER_GUIDE.md` | Note the provider abstraction if it introduces a new architectural component |

## Dependencies

- Third-Party Dependency Governance approval for `Microsoft.Data.SqlClient` and `Oracle.ManagedDataAccess.Core` (Priority 1), then `Npgsql` and `MySqlConnector` (Priority 2).
- #875 (centralise connector dispatch): not a hard prerequisite, but landing it first avoids adding a third copy of every hand-coded dispatch branch; decide sequencing at plan time.
- #637 (connector sub-phase progress): designed but unimplemented; the connector should adopt the callback pattern when it lands ("Executing query", "Reading rows" narration for long-running operations).
- #1046 (Decimal attribute data type, child issue of #170): not a blocker; the connector maps exact numerics to Text until it lands, then upgrades the mapping.

## Open Questions

1. **Decimal support** (resolved; tracked as #1046, a child issue of #170): DECIMAL/NUMERIC/MONEY columns currently have no lossless JIM type, so this PRD maps them to Text. That is a proven pattern in this product class and fine for pass-through flows, but it breaks numeric semantics where JIM itself evaluates the value: scoping criteria compare Text lexicographically ("0.5" > "0.25" works, "9" > "10" fails), so a rule like "FTE less than 0.5" cannot be expressed reliably, and identity-relevant decimal data is real (FTE fraction, contracted hours, salary banding). SCIM (RFC 7643) also defines `decimal` as a core attribute type, so the planned SCIM work (#545) meets the same gap. A first-class `Decimal` `AttributeDataType` is therefore tracked as #1046 (a #245-shaped cross-cutting change); the connector ships with the Text mapping and upgrades when the type lands. The connector does not block on it.
2. **Zoneless DateTime default**: when the administrator does not set the time-zone interpretation setting, should the connector default to UTC (predictable, sometimes wrong) or refuse to import zoneless datetime columns until configured (safe, more friction)?
3. **Multiple multi-valued related tables per object type**: the first release supports N related tables per object type in the data model; is there a UI complexity argument for capping at one initially?
4. **`SupportsParallelExport`**: parallel batches against one database can deadlock on hot pages and interleave parent/child writes; start sequential and revisit, or design for parallelism from the outset?
5. **Stored-procedure export in the first release** (requirement 17): include or fast-follow?
6. **Project placement**: bundling four ADO.NET providers into `JIM.Connectors` grows every deployment; is a separate `JIM.Connectors.Sql` project (still built-in, but isolating the dependency graph) worth the structural deviation from the LDAP/File folder pattern?

## Acceptance Criteria

- [ ] An administrator can configure, validate and save a SQL Server Connected System and an Oracle Connected System entirely via the admin UI, with the password encrypted at rest via the existing credential protection mechanism.
- [ ] Schema discovery lists tables/views and columns with correct JIM type mapping for both Priority 1 providers.
- [ ] Full import stages objects from a table/view including multi-valued attributes from a related table and reference attributes carrying anchors, verified at 100,000 rows.
- [ ] Delta import works in change-log-table mode including deletion propagation, and in watermark mode with documented create/update-only semantics; a missing watermark falls back with the standard warning.
- [ ] Export creates, updates and deletes rows transactionally including related-table maintenance, returning database-generated keys as external IDs, with per-object error isolation and auto-confirmation.
- [ ] All operations are recorded as Activities with per-object Run Profile Execution Items.
- [ ] No native drivers: fully managed providers, air-gap deployable, dependency governance completed for each provider package.
- [ ] The full provider × capability integration matrix (see Testing Requirements) runs green against real SQL Server and Oracle Database Free containers in the existing runner; unit tests cover the provider dialect layer, type mapping and query generation.
- [ ] Public documentation ships in the same release: per-provider configuration guide, delta mode guidance, type mapping and licensing notes.

## Additional Context

- Issue [#170](https://github.com/TetronIO/JIM/issues/170) carries the original brief (2025-12) and remains the tracking issue; this PRD supersedes its technical sketch, which predates the current connector contract surface (`IConnectorImportUsingCalls`, `IConnectorExportUsingCalls`, `IConnectorSchema`, `IConnectorSettings`, `IConnectorCapabilities`, `IConnectorCredentialAware`), the implemented credential encryption, and the conditional-settings framework.
- Dormant integration infrastructure already stages SQL Server 2022, Oracle XE 21c and MySQL 8 containers behind the `phase2` compose profile; Oracle Database Free (23ai) container images are licensed for development and testing use and fit CI (slim/faststart variants exist).
- Prior art (genericised): established ILM/IGA products converge on the same database synchronisation patterns; customer-maintained change-log tables with an explicit change-type column as the robust delta-and-deletion mechanism, watermark columns as the low-friction create/update-only alternative, trigger-populated event tables as the high-fidelity variant of the change-log pattern, multi-valued attributes via joined child tables, and reference attributes as columns carrying the referenced object's anchor. This PRD adopts those proven patterns rather than inventing new ones.
- The LDAP Connector is the structural template (calls-based import/export, watermark in `PersistedConnectorData`, delta fallback semantics, live connectivity validation); the File Connector is the template for auto-confirmed exports.
