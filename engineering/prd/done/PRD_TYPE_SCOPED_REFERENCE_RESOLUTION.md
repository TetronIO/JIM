# Type-Scoped Reference Resolution

- **Status:** Done
- **Created:** 2026-08-19
- **Author:** Jay Van der Zant
- **Issue:** #1285

> **Explainer:** diagram walkthrough of the defect, the options considered and the open decisions: <https://claude.ai/code/artifact/960c7404-6e25-4b95-8649-8215c3cd0426>

## Problem Statement

Import reference resolution indexes anchor (External ID) values in dictionaries keyed by value alone, with no Object Type dimension (`SyncImportTaskProcessor.BuildExternalIdLookups`). Two Object Types in one Connected System that legitimately share an anchor value space, the canonical case being a view over a table, collide on insert and the whole Full Import aborts with:

```
Duplicate primary external ID int value '1' found for CSO 00000000-0000-0000-0000-000000000000.
Another CSO already has the same external ID value.
```

Nothing in the data is ambiguous: a view over a table has the table's keys by construction, and the SQL Connector's schema document already declares which Object Type each Reference attribute points at (`referencesObjectType`, validated at parse time). That declaration never leaves the connector; `ConnectorSchemaAttribute` and `ConnectedSystemObjectTypeAttribute` have no field for it, so the engine guesses that every reference points at the referencer's own Object Type and resolves against a global value-keyed lookup.

Found by the Scenario 16 integration matrix (#170, Phase 7); observed, not inferred. The matrix currently carries two commented workarounds (the view anchors on `EMAIL` instead of the shared integer key, and `APP_USERS` starts its IDENTITY at 1,000,000) and therefore **no longer proves** that a table and a view over the same rows can coexist in one Connected System.

Three adjacent defects live in the same path and are folded into this PRD rather than being left beside new code:

1. **DB fallback collapses mixed types.** Phase 2 of `ResolveReferencesAsync` batches every still-unresolved primary reference into queries keyed by the *first* item's anchor attribute id (`primaryAttributeId ??=`). When two Object Types both have unresolved primary references in one run, the second type's are queried against the wrong attribute and silently fail to resolve.
2. **Cross-type references resolve by accident.** A Person → Department reference works today only because no other Object Type shares the value; the resolution never consults the target type.
3. **The duplicate-anchor error names nobody.** It reports a zero Guid and neither Object Type, so an administrator cannot act on it.

## Goals

- A table and a view over the same rows (or any two Object Types with overlapping anchor value spaces) can be Object Types of one Connected System, import cleanly, and have their references resolve to the declared target type. Confirmed by the Scenario 16 matrix with its two workarounds removed.
- Reference resolution consults the target Object Type a Reference attribute declares, end to end: schema document → schema discovery → persisted schema model → import resolution (in-memory and DB fallback).
- A genuine intra-type duplicate anchor still fails fast and hard, and the error names the Object Type, the anchor attribute, and the value.
- The mixed-type DB fallback defect is fixed: unresolved references are queried per referenced Object Type's anchor attribute.
- No measurable regression against the 500k-row import baseline recorded in #170 (951 to 1,130 objects/second overall; read phase ~6,100 to 6,200 objects/second).

## Non-Goals

- Export-side reference handling (Pending Export staging, reference recall); this PRD is import resolution only.
- Changing LDAP reference semantics: Distinguished Name references remain type-agnostic, resolved against Secondary External IDs across all Object Types, as DNs are unique system-wide.
- Making the declared reference target administrator-editable. It is connector-declared, read-only.
- Cross-Connected-System references (not a JIM concept; all operations flow through the metaverse).
- A File Connector target-declaration surface (per-attribute target Object Type configuration with REST/PowerShell parity). New capability, not part of this defect; raise as its own issue if wanted.

## User Stories

1. As an identity administrator, I want to synchronise both a table and a view over it from one database as separate Object Types, so that I can model differently-shaped projections of the same rows without deploying a second Connected System.
2. As an identity administrator, I want a reference to resolve to the Object Type the schema says it points at, so that overlapping key spaces cannot silently link an object to the wrong target.
3. As an identity administrator, I want a duplicate-anchor failure to tell me which Object Type, attribute and value collided, so that I can fix the data without reading engine source.

## Requirements

### Functional Requirements

1. `ConnectorSchemaAttribute` gains an optional referenced-Object-Type name; `ConnectedSystemObjectTypeAttribute` gains a nullable `ReferencedObjectTypeId` (with navigation), persisted by an EF migration and populated during schema sync by name match within the Connected System.
2. The SQL Connector populates the referenced-Object-Type name from `referencesObjectType` for both reference columns and related-table attributes. The field is connector-neutral; the other connectors leave it null, each for a stated reason rather than as deferred debt: LDAP directory schemas do not constrain a DN attribute's target class (so there is nothing to declare, and DN uniqueness makes any-type resolution unambiguous); the SCIM Connector surfaces no Reference-typed attributes today (the field is ready when it does); the File Connector has no metadata source, so declaring a target would be a new administrator configuration surface (see Non-Goals).
3. `BuildExternalIdLookups` partitions every lookup dictionary by Object Type. An intra-type duplicate anchor still throws, and the message names the Object Type, the anchor attribute name and the value.
4. In-memory resolution: when the Reference attribute declares a target Object Type, look up in that type's partition only. When it does not (LDAP, CSV, legacy schemas), search all partitions: exactly one hit resolves; zero hits fall through to the DB fallback and then the existing per-system Unresolved Reference Handling; two or more hits are reported as an ambiguous reference via the per-system Unresolved Reference Handling, with a message naming the candidate Object Types. The run never aborts for a cross-type value collision.
5. DB fallback: still-unresolved primary references are grouped and queried per referenced Object Type's anchor attribute (fixing the `primaryAttributeId` collapse). Secondary External ID (DN) fallback remains any-type.
6. Resolution outcomes are reported via RPEIs/Activities as today; ambiguous references are counted and surfaced in the import summary statistics, never silent.
7. Surface parity (read-only display): the portal Schema tab, the REST schema DTO and PowerShell (`Get-JIMConnectedSystemObjectType` output) show the declared target Object Type of each Reference attribute.

### Non-Functional Requirements

- Sync hot path: `BuildExternalIdLookups` remains built once per run; partitioning adds one dictionary level with the same total entry count. Validate against the #170 scale baseline (500,000 rows, both providers) with no measurable throughput regression.
- Schema refresh must preserve the declared target exactly as other discovered properties are preserved/overwritten (it is connector-stated, so it sits on the refreshed side, like writability and plurality).

## Examples and Scenarios

### Scenario 1: Table and view coexist with shared keys

**Given**: One Connected System with Object Types `Person` (table, anchor `EMPLOYEE_ID` int) and `PersonView` (view over the table, anchor `EMPLOYEE_ID` int), both holding values 1..N
**When**: A Full Import runs
**Then**: All objects of both types import; no duplicate-anchor exception; each type's CSOs match and update independently.

### Scenario 2: Declared target disambiguates a shared value

**Given**: `Person.MANAGER_ID` is a Reference attribute declaring `referencesObjectType: Person`, and `PersonView` also holds anchor value 42
**When**: A Person with `MANAGER_ID = 42` imports
**Then**: The reference resolves to the `Person` with anchor 42, never to the `PersonView`.

### Scenario 3: Undeclared target, unique value

**Given**: A connector without target declarations (e.g. CSV) imports a reference value found in exactly one Object Type's partition
**When**: Resolution runs
**Then**: The reference resolves, exactly as today.

### Scenario 4: Undeclared target, ambiguous value

**Given**: A connector without target declarations imports a reference value present in two Object Types' partitions
**When**: Resolution runs
**Then**: The reference is reported per the Connected System's Unresolved Reference Handling setting with a message naming both candidate Object Types; the run continues.

### Scenario 5: Genuine intra-type duplicate still fails fast

**Given**: Two imported `Person` objects both carry anchor value 7
**When**: `BuildExternalIdLookups` runs
**Then**: The import fails fast with a message naming `Person`, the anchor attribute and the value 7.

### Scenario 6: Scenario 16 coverage restored

**Given**: `New-Scenario16TestDatabase.ps1` re-anchors `PERSON_DIRECTORY` on the shared integer key and `APP_USERS` IDENTITY starts at 1
**When**: The full matrix runs on both providers
**Then**: All cells pass, proving table + view coexistence with overlapping value spaces.

## Constraints

- Synchronisation integrity is paramount: fast/hard failure for genuine data errors, everything reported via RPEIs/Activities, summary statistics at batch end.
- TDD: red-first tests for every scenario above, including both directions of same-value-different-type.
- Backward compatible: existing schemas have `ReferencedObjectTypeId = null` everywhere and resolution behaves as today (minus the crash and the mixed-type fallback defect).
- British English throughout; no em dashes; Tetron copyright headers on new files.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | Migration: nullable `ReferencedObjectTypeId` FK on `ConnectedSystemObjectTypeAttributes` |
| JIM.Models | `ConnectedSystemObjectTypeAttribute`, `ConnectorSchemaAttribute` |
| JIM.Connectors | SQL Connector schema discovery populates the target name |
| Application | Schema sync persists the target by name match; schema DTOs carry it read-only |
| Worker | `SyncImportTaskProcessor`: `BuildExternalIdLookups`, `ResolveAttributeValueFromLookups`, `ResolveReferencesAsync` Phase 2/3 |
| Data | Repository: per-type variant of `GetConnectedSystemObjectsByAttributeValuesAsync` usage |
| UI | Schema tab shows declared target on Reference attributes |
| API / PowerShell | Read parity for the declared target |
| Integration tests | Scenario 16: remove both workarounds; matrix re-run both providers |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/connectors/jim-sql-connector.md` (schema document reference) | `referencesObjectType` is now consumed by the engine for resolution, not just validated |
| Unresolved reference handling page | New ambiguous-reference case and its message |
| `docs/powershell/connected-systems.md` | Declared target in `Get-JIMConnectedSystemObjectType` output shape |

## Dependencies

- None blocking. #1287 (SeedAsync idempotency) is independent. Baseline numbers come from #170 (closed).

## Open Questions

None. All four decisions were confirmed by Jay on 2026-08-19:

1. **Option A** (declared-target, type-scoped resolution).
2. **Ambiguity handling** routes through the existing per-system Unresolved Reference Handling setting (FR4).
3. **Read-only surface parity** for the declared target (FR7) is in scope.
4. **Scenario 16 workaround removal** ships in the same delivery (Scenario 6).

> Option B (partition only, no schema change) was rejected because it leaves shared-value references unresolvable. The declared-target field is connector-neutral; only the SQL Connector populates it in this delivery because it is the only connector whose schema source states a target (see FR2 for the per-connector rationale).

## Acceptance Criteria

- [x] Full Import succeeds with two Object Types sharing an anchor value space (table + view), both providers, Scenario 16 matrix green with both workarounds removed (38/38: SQL Server 18, Oracle 20, 2026-08-19)
- [x] Declared-target references resolve to the declared Object Type even when the value exists in another type's partition (both directions tested)
- [x] Undeclared-target references: unique value resolves; ambiguous value reported per Unresolved Reference Handling naming candidate types (all three modes tested); run continues
- [x] Intra-type duplicate anchor fails fast with Object Type, attribute and value in the message
- [x] Mixed-type DB fallback queries per referenced type's anchor attribute (red-first test reproducing the `primaryAttributeId` collapse), and matches through the anchor's own data type
- [x] Declared target visible read-only in portal Schema tab, REST schema DTO and PowerShell output (REST verified live against a configured SQL Server system)
- [x] 500k scale import shows no measurable throughput regression against the #170 baseline (SQL Server 13m39s / 1,220 obj/s, Oracle 16m16s / 1,024 obj/s)
- [x] `dotnet build JIM.sln` and `dotnet test JIM.sln` clean (plus the full RequiresPostgres suite); changelog 🐛 entry; docs updated per the table above

## Additional Context

- Issue #1285 (this defect), discovered during #170 Phase 7 (Scenario 16 SQL Connector matrix)
- Explainer artifact with diagrams and option trade-offs: <https://claude.ai/code/artifact/960c7404-6e25-4b95-8649-8215c3cd0426>
- Scale baseline: `engineering/INTEGRATION_TESTING.md` → "Scale import (500,000 rows), measured 2026-08-18"
- Workarounds to remove: `test/integration/New-Scenario16TestDatabase.ps1` (EMAIL anchor on the view; `APP_USERS` IDENTITY offset), both commented with #1285
