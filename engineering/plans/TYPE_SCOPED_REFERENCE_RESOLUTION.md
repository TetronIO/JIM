# Type-Scoped Reference Resolution - Implementation Plan

- **Status:** Planned
- **Created:** 2026-08-19
- **Issue:** [#1285](https://github.com/TetronIO/JIM/issues/1285)
- **PRD:** [`engineering/prd/PRD_TYPE_SCOPED_REFERENCE_RESOLUTION.md`](../prd/PRD_TYPE_SCOPED_REFERENCE_RESOLUTION.md)

## Overview

Import reference resolution keys its lookups by anchor value alone, so two Object Types in one Connected System that share a value space (canonically a view over a table) abort the whole Full Import. This plan carries the reference target the SQL Connector already declares (`referencesObjectType`) through the schema pipeline into the persisted model, partitions every resolution lookup by Object Type, and fixes two adjacent defects in the same path (the mixed-type DB fallback collapse and the anonymous duplicate-anchor error). All four PRD decisions are settled: Option A; ambiguity routed through the per-system Unresolved Reference Handling setting; read-only surface parity in scope; Scenario 16 workaround removal in the same delivery.

## Business Value

Administrators can synchronise a table and a view over the same rows as separate Object Types of one Connected System, which today hard-fails on the first shared key. References resolve to the Object Type the schema declares, so overlapping key spaces can never silently link an object to the wrong target. Failures that remain (genuine intra-type duplicates) name the Object Type, attribute and value.

## Technical Architecture

### Current state

- `SqlSchemaConfiguration` validates `referencesObjectType` but the name dies inside the connector: `ConnectorSchemaAttribute` has no field for it, so `ConnectedSystemObjectTypeAttribute` never stores it.
- `SyncImportTaskProcessor.BuildExternalIdLookups` (~line 3333) pours every Object Type's anchor values into one dictionary per data type; `TryAdd` throws on the first cross-type shared value.
- `ResolveAttributeValueFromLookups` guesses the target is the referencer's own Object Type (anchor attribute taken from `csoToProcess.Type`).
- `ResolveReferencesAsync` Phase 2 batches all unresolved primary references onto the *first* item's anchor attribute id (`primaryAttributeId ??=`), so mixed-type runs silently fail to resolve the second type's references.
- CSO matching is already type-scoped (cache key includes the anchor attribute id) and needs no change.

### Proposed solution

1. **Schema pipeline**: `ConnectorSchemaAttribute` gains an optional `ReferencesObjectTypeName`; `ConnectedSystemObjectTypeAttribute` gains a nullable `ReferencedObjectTypeId` FK + `ReferencedObjectType` navigation (`ON DELETE SET NULL`). The schema merge in `ConnectedSystemServer` wires the navigation by name in a second pass once every Object Type instance exists in the graph, so EF fixes up FKs for brand-new types on save. A schema refresh restates the target (connector-stated, like `Writability`); the SQL Connector populates it for reference columns and related-table attributes; LDAP/SCIM/File leave it null (see PRD FR2 for why that is correct, not deferred debt).
2. **In-memory resolution**: lookups become partitioned by Object Type id (`Dictionary<int, Dictionary<TKey, ConnectedSystemObject>>` inside `ExternalIdLookups`). Declared target: parse the value using the *target* type's anchor attribute data type and probe that partition only. Undeclared: probe all partitions; one hit resolves, zero falls through, two or more is an ambiguous reference reported per the Connected System's `UnresolvedReferenceHandling` with a message naming the candidate Object Types. The run never aborts for a cross-type collision; an intra-type duplicate still throws, naming the Object Type, anchor attribute and value.
3. **DB fallback**: unresolved items are grouped by the anchor attribute id of their declared target (or, undeclared, swept per candidate Object Type's anchor attribute) and queried per group via the existing `GetConnectedSystemObjectsByAttributeValuesAsync(systemId, attributeId, values)`; a value found under multiple types is ambiguous, handled as above. Secondary External ID (DN) fallback stays any-type.
4. **Reporting**: ambiguous references are counted and appear in the end-of-run summary statistics and the import Activity, never silent.

### Data flow (after)

```
schema document (referencesObjectType)
  -> ConnectorSchemaAttribute.ReferencesObjectTypeName        (discovery)
  -> ConnectedSystemObjectTypeAttribute.ReferencedObjectTypeId (schema merge + migration)
  -> ExternalIdLookups partition [ObjectTypeId][value]         (import, built once per run)
  -> declared target: probe one partition | undeclared: probe all, unique-or-ambiguous
```

## Implementation Phases

### Phase 1: Model, migration and schema pipeline

1. Red-first tests: schema merge persists the declared target for a new Object Type and on refresh of an existing one; removing the target Object Type nulls the FK; connectors that declare nothing produce null.
2. `ConnectorSchemaAttribute`: add `string? ReferencesObjectTypeName` (constructor parameter, default null).
3. `ConnectedSystemObjectTypeAttribute`: add `int? ReferencedObjectTypeId` + `ConnectedSystemObjectType? ReferencedObjectType`; EF migration with `ON DELETE SET NULL` and an index.
4. `ConnectedSystemServer` schema merge (both the update-existing and create-new branches, ~lines 1680-1750): copy the name onto the attribute, then a second pass over the completed graph resolves names to navigations (case-insensitive match within the Connected System, mirroring `SqlSchemaConfiguration`'s comparer).
5. SQL Connector: populate `ReferencesObjectTypeName` from `referencesObjectType` for reference columns and related tables (`SqlConnectorSchema`, ~line 236 and the column path).
6. Gate: `dotnet build JIM.sln`, `dotnet test JIM.sln`, zero warnings.

### Phase 2: Import resolution (the defect itself)

1. Red-first tests reproducing #1285 and the adjacent defects, per the PRD's six scenarios: same value in two types (both directions), declared-target hit beside a decoy partition, undeclared unique hit, undeclared ambiguity (run continues, message names candidates, honours each `UnresolvedReferenceHandling` mode), intra-type duplicate throw naming type/attribute/value, and the mixed-type DB fallback collapse.
2. Partition `ExternalIdLookups` by Object Type id; duplicate detection becomes per-partition; enrich the throw message.
3. `ResolveAttributeValueFromLookups`: accept the referencing attribute's `ReferencedObjectTypeId`; declared → resolve the target type's anchor attribute (data type drives parsing) and probe one partition; undeclared → probe all partitions with unique-or-ambiguous semantics.
4. `ResolveReferencesAsync` Phases 2-3: group DB fallback queries by (target Object Type, anchor attribute id); remove `primaryAttributeId ??=`; detect cross-type multi-hits; route ambiguity and count it into the summary statistics.
5. Gate as Phase 1.

### Phase 3: Read-only surface parity

1. `ConnectedSystemAttributeDto`: add `ReferencedObjectTypeId` / `ReferencedObjectTypeName` (read-only, mapped in `FromEntity`).
2. Portal Schema tab (`ConnectedSystemSchemaTab.razor`): show the target Object Type on Reference attributes; Claude Artefact demo of the UI change.
3. PowerShell: `Get-JIMConnectedSystemObjectType` passes the new fields through; documented output shape updated; Pester test.
4. Docs: SQL Connector schema document page (target now consumed by the engine), unresolved-reference handling page (ambiguity case), `docs/powershell/connected-systems.md`.
5. Changelog 🐛 entry under `[Unreleased]`.

### Phase 4: Scenario 16 restoration and scale validation

1. `New-Scenario16TestDatabase.ps1` / `Setup-Scenario16.ps1`: re-anchor `PERSON_DIRECTORY` on the shared integer key; reset the `APP_USERS` IDENTITY offset to 1; delete both workaround comments.
2. Run the default Scenario 16 matrix on both providers; every cell must pass.
3. Run `-FullMatrix` so `Scale.FullImport500k` executes; compare against the #170 baseline (951-1,130 obj/s overall, ~6,100-6,200 obj/s read phase); no measurable regression.
4. Update `engineering/INTEGRATION_TESTING.md` if the numbers move.

## Success Criteria

The PRD's acceptance criteria, verbatim; headline items:

- Scenario 16 matrix green on both providers with both workarounds removed (table + view coexistence proven with overlapping value spaces).
- Declared-target references resolve to the declared type; undeclared ambiguity is per-row and named, never a run abort.
- Mixed-type DB fallback red-first test passes.
- 500k scale import within baseline; `dotnet build`/`test JIM.sln` clean throughout.

## Dependencies

None. No new NuGet packages. #1287 (SeedAsync idempotency) is independent.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Hot-path regression in `BuildExternalIdLookups` | Same total entry count, one added hash level, still built once per run; Phase 4 measures against the recorded 500k baseline before merge |
| EF fails to fix up FKs for Object Types created in the same save | Wire navigations, not ids, in the second merge pass; Phase 1 test covers the new-type case explicitly |
| Behaviour change: cross-type references that resolved "by accident" now constrained by a declared target | Intended; called out in the changelog entry. Undeclared schemas keep unique-hit resolution, so LDAP/CSV behaviour is unchanged |
| Schema refresh drops or resurrects the target unexpectedly | Target is connector-stated and restated on refresh (same side as `Writability`); refresh test in Phase 1 pins it |
| Ambiguity flood on a misconfigured system | Routed through `UnresolvedReferenceHandling`, so an administrator chooses error/warn/log; counts surface in summary statistics either way |
