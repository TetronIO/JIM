# Synchronisation Hot Path Off EF Core's Change Tracker

- **Status:** Doing (Phases 0 to 2 complete; Phases 1 and 2 awaiting scale measurement)
- **Issue:** [#2598](https://github.com/TetronIO/JIM/issues/2598)
- **Last Updated:** 2026-10-30

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

The attach costing orders of magnitude more per value on a giant group than at Large suggests it grows faster than linearly with a group's size; Phase 0 confirmed it and found the cause (below). An earlier run's 148 s page load was not reproduced once the join lookup was fixed, so page load is no longer a priority. The target-side figures have no breakdown yet; Phase 0 provides it.

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

### Phase 0: Measure what is still dark ✅

- Spans inside `EvaluateExportRules` around the work outside its rule loop, and on the join path (match lookup, join match load, join count, join-state write), so breakdown lines cover the large-group export evaluation and the target-side Full Synchronisation and confirming synchronisation.
- A `RequiresPostgres` measurement of the tracker attach against group size (1,000, 10,000, 100,000 values) to establish whether it is linear, and why if not.

**As found (2026-10-28):** spans now cover the join path (`Join.MatchLookup`, `Join.LoadMatch` with the loaded object's value count, `Join.ExistingJoinCount`; the join-state write already had `UpdateCsoJoinStates`) and the work after export evaluation's rule loop (`ResolveExportGeneratedValues`, and `SnapshotPendingExportChanges` with `Snapshot.ResolveReferences`, `Snapshot.LoadReferencedObjects` and `Snapshot.Build`). The snapshot is the likely home of the 15.5 s: it records every change a Pending Export carries and looks up the target system's object for every referenced member. A scale run with these spans confirms or refutes that.

The attach, measured on one Metaverse Object written through `CreateMetaverseObjectsAsync` against real PostgreSQL, with members the writing context does not track:

| Values | Bulk write | Tracker attach |
|---|---|---|
| 1,000 | 62 ms | 16 ms |
| 10,000 | 495 ms | 296 ms |
| 50,000 | 2.1 s | 4.2 s |
| 100,000 | 5.3 s | 13.9 s |

So the attach grows roughly with the square of a group's size, and overtakes the write itself above about 30,000 values. A CPU profile of the 100,000 case puts all of it in EF Core's change tracker: identity-map hashing as each value is tracked (about 5.4 s), relationship fix-up scanning the list-backed `AttributeValues` before adding each value (4.6 s), and fix-up of each value's reference to a member the context does not track (4.8 s). Attaching the values before the object, which avoids the list scans, cut it by a third and left it superlinear. There is therefore no cheap fix to the attach itself: Phase 1 removes it, as planned, and needs no interim step.

**Scale run with the new spans (2026-10-28, Scale750k40kGroups, InitialSync):**

| Slowest pages | Where the time went |
|---|---|
| Source Full Synchronisation, 266 s and 182 s | Tracker attach 211 s and 136 s (bulk write 15 s and 12 s); the Pending Export snapshot 15.8 s and 14.9 s, almost all of it `Snapshot.LoadReferencedObjects` (14.8 s and 14.3 s) for one group; the export rule loop itself 0.5 s |
| Target Full Synchronisation, 154 s and 103 s | `Cso.LoadMetaverseObjects` 154 s and 103 s; the page's own load 5 ms |

The target side was the unknown, and it is not the join: the target's objects were provisioned by JIM and are already joined, so no match lookup ran. It is `LoadMetaverseObjectsForCsosAsync`, a tracked EF query that `Include`s every attribute value of every Metaverse Object on the page, so a page of groups materialises and tracks every membership, which is the same per-value tracker cost the attach measurement shows. The phases below are reordered accordingly: the target-side load moves up to Phase 2, the snapshot's referenced-object load becomes Phase 3, and the join match load, which never registered, joins the remaining reads in Phase 4.

### Phase 1: Remove the tracker bridge ✅

- Find every consumer that relies on a freshly written Metaverse Object being tracked (navigations used to add children, EF saves reaching it), convert each to raw SQL, then delete `AttachToTracker` from `CreateMetaverseObjectsBulkAsync` and the matching re-attachment in the update path.
- A guard test: after a page flush, the context tracks no Metaverse Object or attribute value.
- Target: the source side's two slowest pages lose 211 s and 136 s.

**As delivered (2026-10-29):** the bulk create leaves the objects it writes untracked. Change detection is already off for the whole page flush, so the only code that still needed the new objects tracked was two saves that walk object graphs, where a tracked new object stopped the walk. Both are now raw SQL. `DeleteMetaverseObjectsAsync` was EF's `RemoveRange` on a detached deletion candidate. Through the candidate's reference values that reached a same-page new object, then saved that object's reference to the deleted one, a row the reference cleanup had already removed, and threw `DbUpdateConcurrencyException`; a `RequiresPostgres` test reproduces it. It is now a raw `DELETE`, followed by a pass over the tracker driven by the model's foreign keys, which detaches cascade children and records set-null keys as persisted. The per-object deletion fallback's `Update(mvo)` became a one-column write, and the sync repository's `UpdateMetaverseObjectAsync`, which had no other caller, is gone. The guard test asserts nothing is tracked after a create. Full solution tests pass, as do Scenarios 008 and 032 at Medium on OpenLDAP. Not yet measured at scale.

### Phase 2: Load a page's Metaverse Objects untracked

- Replace the tracked `Include` in `LoadMetaverseObjectsForCsosAsync` with a raw SQL read into untracked objects, navigations assigned explicitly, and move whatever later saves those objects through EF (`UpdateMetaverseObjectsAsync` relies on them being tracked, per the method's own comment) to raw set-based writes in the same phase. Phase 1's guard test extends to cover it.
- Tests: `RequiresPostgres` round trip asserting every field and navigation the engine reads from a loaded Metaverse Object, on a `NoTracking` context.
- Target: the target side's two slowest pages lose 154 s and 103 s.

**As delivered (2026-10-30):** a page's Metaverse Objects are read by raw SQL into untracked objects (`MetaverseObjectRawReader`), with their type, attribute values and connector navigations assigned explicitly. The bulk update already wrote by raw SQL and needed nothing; the round-trip test asserts every column the reader fills against EF's reading of the same row, and that a no-op update leaves every column unchanged. An untracked object hanging off a tracked Connected System Object is safe only while nothing runs EF's change detection, which would attach it as Modified and fail the next save on its row version (a test documents this), so:

- **Change detection is off for the whole page**, the cross-page reference pass, and Synchronised Deprovisioning's per-object pass, rather than just their flushes, and restored after the tracker clear, always in a `finally` that clears first. Scenario 008 at Medium found the case that made the `finally` necessary: the pager's closing sweep reloads objects created since the run's floor and drops the ones already handed out, and when it dropped all of them the loop ended without the page's own clear, so the run's next save failed. Two `RequiresPostgres` sync tests reproduce it, for Full and Delta Synchronisation.
- **Loads later in a page no longer make tracked copies of its objects.** The join match now reads through the same raw reader (brought forward from Phase 4), and contributor re-election and import scoping hydrate a survivor with `GetConnectedSystemObjectWithAttributeValuesAsync`, which leaves its Metaverse Object out. Either tracked copy let relationship fix-up repoint the page's objects at a partial instance, whose missing values the bulk update would then delete; two `RequiresPostgres` tests reproduce both.
- **Last-connector detection counts the database**, not the object's in-memory connectors, which hold only the connectors the page happened to load.
- **The Full Synchronisation and Synchronised Deprovisioning previews walk on their own context** (`ISyncRepository.OpenIsolatedScope`, guarded like the rest of the preview path). Their caller saves progress on the shared context during the walk with change detection on, and the page's untracked objects would otherwise ride along. The isolated context also keeps the preview's rollback-only transaction off the caller's own writes.

### Phase 3: The Pending Export snapshot's referenced objects

- `SnapshotPendingExportChangesAsync` loads the target system's object for every referenced member through `GetConnectedSystemObjectsByMetaverseObjectIdsAsync` (14.8 s for one group). The snapshot needs only each object's identifiers for display, so replace the load with a raw SQL Summary-tier read of those identifiers.
- Target: under 1 s for the largest group.

### Phase 4: Remaining per-page EF reads

- The page's own load (`GetConnectedSystemObjectsAsync`), the other export-side reads and the cross-page reference reload, in order of measured cost; any that measure as negligible are left on EF and recorded as such. At the 2026-10-28 run none registered: the page load took 5 to 220 ms and no join ran. The join match load moved to Phase 2, where it was needed for correctness.

### Phase 5: Scale verification

- Scale750k40kGroups, both sides, against the baseline above; peak Worker memory no worse than 25.9 GiB.

## Success Criteria

- No group page at Scale750k40kGroups takes more than 30 s (the slowest took 266 s), and the tracker attach is gone from the breakdown lines.
- After a page flush, the Worker's context tracks no Metaverse Objects, Connected System Objects or attribute values (guard test).
- Target-side Full Synchronisation and confirming synchronisation each at least 30% faster than the baseline above (Phase 0 found EF Core's tracked Metaverse Object load is most of the target side's slowest pages).
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
