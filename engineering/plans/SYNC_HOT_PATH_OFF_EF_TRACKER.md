# Synchronisation Hot Path Off EF Core's Change Tracker

- **Status:** Planned
- **Issue:** [#2598](https://github.com/TetronIO/JIM/issues/2598)
- **Last Updated:** 2026-10-28

## Overview

A synchronisation page still reads and writes through EF Core's change tracker at the points where object graphs are largest: re-attaching freshly written Metaverse Objects to the tracker, loading a joined Metaverse Object, and loading a page's attribute values. Each costs .NET work per attribute value, on one thread, because a page runs on one `DbContext`. A large group turns that into minutes per page: the re-attach alone took 211 s of a 266 s page at Scale750k40kGroups.

This plan converts those flows, whole, to raw SQL and untracked objects, so that nothing on the hot path depends on tracked entities and the "bridge" that re-attaches raw-written rows to the tracker can be deleted. It is ordered by measured cost, and each phase is judged by the same measurement.

## Business Value

- **The problem:** at Scale750k40kGroups (#2492, 2026-10-28) three group pages took 148 to 266 s against 3 to 11 s for the rest, and on the two slowest the tracker re-attach was 80% of the page, with the Worker on one core at 100% and the database mostly idle. Groups are the objects customers care most about synchronising, and their cost is the cost of their membership.
- **Who it affects:** every customer with large groups, on every Full Synchronisation, and on the target-side synchronisations that join and confirm them.
- **Why now:** #2492 removed the deferral of group membership to the cross-page step, which moved the group work into the page loop, where these costs now dominate. The instrumentation added in #2492 (span breakdowns) makes each phase's effect measurable.

## Measured Baseline

From Scale750k40kGroups on 2026-10-28, with #2492 and its join lookup fix in place (`DiagnosticListener` breakdown lines), and Scenario 008 Large on 2026-10-26:

| Flow | Measurement |
|---|---|
| Typical group page, whole page | 3 to 11 s |
| Slowest group page (266 s): tracker attach after the bulk write | 211 s (`Mvo.Create.AttachToTracker`), against 15 s for the COPY itself |
| Second slowest (206 s): tracker attach | 150 s, against 17 s for the COPY |
| Tracker attach at Scenario 008 Large | about 12 µs per value (480 ms for 41,000 values) |
| Export evaluation for the page's largest group | 15.5 s for one object, almost all inside `EvaluateExportRules` but outside its rule loop (no span there yet) |
| Page load on the slowest pages | 0.1 to 0.2 s (`LoadCsoPage`) |
| Target-side Full Synchronisation (join) | 11 min 26 s |
| Target-side Full Confirming Synchronisation | 15 min 10 s |

The attach costing orders of magnitude more per value on a giant group than at Large suggests it grows faster than linearly with a group's size; one candidate is EF Core's relationship fix-up checking a list-backed collection for each value it adds. That is a hypothesis for Phase 0 to confirm, not a finding. An earlier run's 148 s page load was not reproduced once the join lookup was fixed, so page load is no longer a priority. The target-side figures have no breakdown yet; Phase 0 provides it.

## Technical Architecture

### Current state

- **Page load:** `ConnectedSystemRepository.GetConnectedSystemObjectsAsync` loads the page's objects with a tracked `Include` of every attribute value and its attribute, then fills reference targets and Metaverse Objects by raw SQL (`PopulateReferenceValuesAsync`, `LoadMetaverseObjectsForCsosAsync`).
- **Join match:** `SyncRepository.GetMetaverseObjectForJoinMatchAsync` calls the portal's full `MetaverseRepository.GetMetaverseObjectAsync`: every attribute value, each referenced object's display name value and type, tracked. A join needs the object's id, type and the values Attribute Flow and export evaluation read, not the display names of its group's members.
- **Metaverse Object write:** `CreateMetaverseObjectsBulkAsync` COPYs the rows (in parallel where large), then attaches every object and value to the tracker as `Unchanged` so that later EF-based code can add children through navigations. Its own comment marks the bridge for removal once MVO change tracking is raw SQL, which it now is (`PersistPendingMvoChangesAsync`).
- **Elsewhere on the page:** several export-side reads (`GetConnectedSystemObjectsByMetaverseObjectIdsAsync`, `GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync`, `GetConnectedSystemObjectForExportMatchAsync`, `GetMetaverseObjectsByIdsForUpdateAsync`, `GetCsoAttributeValuesByCsoIdsAsync`), per-object join bookkeeping (`GetConnectedSystemObjectCountByMvoAsync`, `UpdateConnectedSystemObjectJoinStatesAsync`) and the cross-page reference reload are EF queries.

### Proposed solution

Each flow becomes a raw SQL read (a `DbDataReader` into plain objects, untracked) or a raw SQL write (COPY or set-based statements through the existing `*BulkColumns` constants), with navigations assigned explicitly where the sync engine needs them. Identity across a page is kept by the existing page identity map (`_mvoIdentityMap`), not by EF's. A flow is converted only together with everything downstream that relied on its entities being tracked, so no bridge is needed and none remains.

What stays on EF: portal and API reads, configuration create/update/delete, and the once-per-run loads of Synchronisation Rules and schema.

## Implementation Phases

### Phase 0: Measure what is still dark

- Spans inside `EvaluateExportRules` around the work outside its rule loop, and on the join path (match lookup, join match load, join count, join-state write), so breakdown lines cover the large-group export evaluation and the target-side Full Synchronisation and confirming synchronisation.
- A `RequiresPostgres` measurement of the tracker attach against group size (1,000, 10,000, 100,000 values) to establish whether it is linear, and why if not.

### Phase 1: Remove the tracker bridge

- Find every consumer that relies on a freshly written Metaverse Object being tracked (navigations used to add children, EF saves reaching it), convert each to raw SQL, then delete `AttachToTracker` from `CreateMetaverseObjectsBulkAsync` and the matching re-attachment in the update path.
- A guard test: after a page flush, the context tracks no Metaverse Object or attribute value.
- If Phase 0 shows the attach is superlinear and the bridge cannot go in one step, an interim fix to the attach itself is acceptable only with the guard test in place and the bridge's removal still scheduled.

### Phase 2: Export evaluation for large groups

- Remove the per-value cost Phase 0 finds outside the rule loop, by raw SQL or by not materialising what evaluation does not read; the approach follows the measurement.

### Phase 3: Join match load

- A join-specific read returning the Metaverse Object with only what joining, Attribute Flow and export evaluation need, by raw SQL, untracked; no referenced objects' display names.
- Join bookkeeping (join count, join-state write) as raw SQL.
- Tests: `RequiresPostgres` round trip asserting every field the engine reads, plus a test that joining a group does not load its members' objects.

### Phase 4: Page load and remaining per-page EF reads

- The page load (raw SQL page reader into untracked objects, with callers that later write a loaded object through EF moved to raw set-based writes), the export-side reads and the cross-page reference reload, in order of their Phase 0 cost; any that measure as negligible are left on EF and recorded as such.

### Phase 5: Scale verification

- Scale750k40kGroups, both sides, against the baseline above; peak Worker memory no worse than 25.9 GiB.

## Success Criteria

- No group page at Scale750k40kGroups takes more than 30 s (the slowest took 266 s), and the tracker attach is gone from the breakdown lines.
- After a page flush, the Worker's context tracks no Metaverse Objects, Connected System Objects or attribute values (guard test).
- Target-side Full Synchronisation and confirming synchronisation each at least 30% faster than the baseline above, provided Phase 0 shows EF Core work is most of their time; otherwise this criterion is restated from the measurement.
- Every integration scenario's outcome identical; no new `DbUpdateConcurrencyException` class of failure.

## Benefits

- **Performance:** per-value cost on the page pipeline drops to the cost of reading and writing the rows; large groups stop dominating a run.
- **Architecture:** one persistence model on the hot path instead of two, which retires the tracker fix-up rules that exist only to keep the two in step.
- **Operability:** the Worker spends its time where the database can be measured, rather than in opaque single-threaded .NET work.

## Dependencies

None external. No migrations expected; raw writes use the existing `*BulkColumns` constants and their completeness tests.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Untracked objects lose EF's relationship fix-up, so a navigation the engine reads is left null | Assign navigations explicitly in the readers; `RequiresPostgres` tests on a `NoTracking` context assert every navigation the engine reads (the in-memory provider tracks by default and cannot see this) |
| A later EF save reaches an untracked object and inserts or updates it wrongly | Convert each flow with its downstream writers in the same phase; the Phase 1 guard test fails if anything is tracked after a flush |
| Raw SQL column lists drift from the model | The `*BulkColumns` constants and `BulkInsertColumnCompletenessTests`, plus a round-trip test per raw write (`src/CLAUDE.md`, Raw SQL Column Lists) |
| A cached plan on a raw read behaves badly as tables grow during a run | Do not prepare hot-path statements without testing them across a growing table (the #2492 join lookup was reverted for exactly this) |
| Changes to Pending Export staging or the page flush corrupt synchronisation | Scenarios 008 and 032 at Medium before each PR, per `src/JIM.Application/CLAUDE.md` |
