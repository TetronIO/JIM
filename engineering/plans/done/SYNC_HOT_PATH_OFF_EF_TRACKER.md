# Synchronisation Hot Path Off EF Core's Change Tracker

- **Status:** Done
- **Note:** Phase 5 measured at Scale500k25kGroups rather than Scale750k40kGroups: the available host cannot give Docker the memory 750k needs. The remaining large-group costs are separate issues: [#2645](https://github.com/TetronIO/JIM/issues/2645) (export evaluation's no-net-change check) and the linear bulk writes.
- **Issue:** [#2598](https://github.com/TetronIO/JIM/issues/2598)
- **Last Updated:** 2026-10-31

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

**As delivered (2026-10-29):** the bulk create leaves the objects it writes untracked. Change detection is already off for the whole page flush, so the only code that still needed the new objects tracked was two saves that walk object graphs, where a tracked new object stopped the walk. Both are now raw SQL. `DeleteMetaverseObjectsAsync` was EF's `RemoveRange` on a detached deletion candidate. Through the candidate's reference values that reached a same-page new object, then saved that object's reference to the deleted one, a row the reference cleanup had already removed, and threw `DbUpdateConcurrencyException`; a `RequiresPostgres` test reproduces it. It is now a raw `DELETE`, followed by a pass over the tracker driven by the model's foreign keys, which detaches cascade children and records set-null keys as persisted. The per-object deletion fallback's `Update(mvo)` became a one-column write, and the sync repository's `UpdateMetaverseObjectAsync`, which had no other caller, is gone. The guard test asserts nothing is tracked after a create. Full solution tests pass, as do Scenarios 008 and 032 at Medium on OpenLDAP. Measured at scale in Phase 5.

### Phase 2: Load a page's Metaverse Objects untracked ✅

- Replace the tracked `Include` in `LoadMetaverseObjectsForCsosAsync` with a raw SQL read into untracked objects, navigations assigned explicitly, and move whatever later saves those objects through EF (`UpdateMetaverseObjectsAsync` relies on them being tracked, per the method's own comment) to raw set-based writes in the same phase. Phase 1's guard test extends to cover it.
- Tests: `RequiresPostgres` round trip asserting every field and navigation the engine reads from a loaded Metaverse Object, on a `NoTracking` context.
- Target: the target side's two slowest pages lose 154 s and 103 s.

**As delivered (2026-10-30):** a page's Metaverse Objects are read by raw SQL into untracked objects (`MetaverseObjectRawReader`), with their type, attribute values and connector navigations assigned explicitly. The bulk update already wrote by raw SQL and needed nothing; the round-trip test asserts every column the reader fills against EF's reading of the same row, and that a no-op update leaves every column unchanged. An untracked object hanging off a tracked Connected System Object is safe only while nothing runs EF's change detection, which would attach it as Modified and fail the next save on its row version (a test documents this), so:

- **Change detection is off for the whole page**, the cross-page reference pass, and Synchronised Deprovisioning's per-object pass, rather than just their flushes, and restored after the tracker clear, always in a `finally` that clears first. Scenario 008 at Medium found the case that made the `finally` necessary: the pager's closing sweep reloads objects created since the run's floor and drops the ones already handed out, and when it dropped all of them the loop ended without the page's own clear, so the run's next save failed. Two `RequiresPostgres` sync tests reproduce it, for Full and Delta Synchronisation. Synchronised Deprovisioning's clear keeps the run's own task and Activity tracked (`ClearChangeTrackerRetaining`): Scenario 014 found that a plain clear let the next checkpoint save reload both, and the Worker's completion of its own Activity then collided with the reloaded copy.
- **Loads later in a page no longer make tracked copies of its objects.** The join match now reads through the same raw reader (brought forward from Phase 4), and contributor re-election and import scoping hydrate a survivor with `GetConnectedSystemObjectWithAttributeValuesAsync`, which leaves its Metaverse Object out. Either tracked copy let relationship fix-up repoint the page's objects at a partial instance, whose missing values the bulk update would then delete; two `RequiresPostgres` tests reproduce both.
- **Last-connector detection counts the database**, not the object's in-memory connectors, which hold only the connectors the page happened to load.
- **The Full Synchronisation and Synchronised Deprovisioning previews walk on their own context** (`ISyncRepository.OpenIsolatedScope`, guarded like the rest of the preview path). Their caller saves progress on the shared context during the walk with change detection on, and the page's untracked objects would otherwise ride along. The isolated context also keeps the preview's rollback-only transaction off the caller's own writes.

### Phase 3: The Pending Export snapshot's referenced objects ✅

- `SnapshotPendingExportChangesAsync` loads the target system's object for every referenced member through `GetConnectedSystemObjectsByMetaverseObjectIdsAsync` (14.8 s for one group). The snapshot needs only each object's identifiers for display, so replace the load with a raw SQL Summary-tier read of those identifiers.
- Target: under 1 s for the largest group.

**As delivered (2026-10-30):** the snapshot reads only the identifiers, through `GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync`. That is raw SQL that picks each object type's name attributes and the object's two external ids, then looks up exactly those values through `IX_ConnectedSystemObjectAttributeValues_CsoId_AttributeId`, so a referenced group's member values are never read. Each object is rebuilt from those values alone and named by the model's own rule (`NameOrId`, else the object's id), and a `RequiresPostgres` test holds the result equal to `GetCsoDisplayIdentifier` on the fully loaded object across every naming shape. `ExportChangeHistoryBuilder` now takes the identifiers rather than the objects, so the end-of-page snapshot resolution and Synchronisation Rule deletion recall use the same read, and the provisioning objects not yet written are named directly instead of through detached copies. The portal's unresolved-reference detail keeps the full load, since it classifies by anchor. Measured at scale in Phase 5.

### Phase 4: A large group's own page load ✅

- As planned: the page's own load (`GetConnectedSystemObjectsAsync`), the other export-side reads and the cross-page reference reload, in order of measured cost; any that measure as negligible are left on EF and recorded as such. At the 2026-10-28 run none registered: the page load took 5 to 220 ms and no join ran. The join match load moved to Phase 2, where it was needed for correctness.

**Rescoped after Scale500k25kGroups (2026-10-30).** With Phases 1 to 3 in place the run passed with no tracker attach, a Metaverse Object page load of at most 0.66 s, a 2.0 s snapshot for a 500,000-member group and about 3 GiB of Worker memory during synchronisation. The largest remaining cost was the one the 2026-10-28 run had not shown: loading a big group's own page, 47 to 72 s per page (`Cso.Page.LoadWithAttributeValues`) on the last group pages of both Full Synchronisations, and 169 s for the Delta Synchronisation's equivalent (`LoadModifiedCsoPage`). Phase 4 became that load. The second largest, 50 s of export evaluation for one large group in a Delta Synchronisation, is a different cause (the no-net-change check is quadratic in the membership) and is tracked separately as [#2645](https://github.com/TetronIO/JIM/issues/2645).

**The cause is EF Core's collection check, not tracking.** Measured on one group against real PostgreSQL:

| Load | 100,000 values | 200,000 values |
|---|---|---|
| The page load as the Worker runs it | 10.7 s | 41 s |
| The same EF query, tracked | 9.9 s | 45 s |
| The same EF query, untracked | 5.2 s | 18.6 s |
| Raw SQL, the same rows | 0.15 s | 0.3 s |

Untracked was quadratic too, so loading the page untracked (and taking on the duplicate-instance hazards Phase 2 removed) would not have fixed it. A CPU profile and EF Core 10.0.14's source agree on why: before adding each entity it materialises or fixes up into a collection navigation, EF asks whether the collection already holds it, and for a `List<T>` or `Collection<T>` it answers by scanning the list itself (`ClrCollectionAccessor.Contains`). For any other collection type it asks the collection. A subclass of `List<T>` still takes the scan.

**As delivered (2026-10-31):** the three value navigations that can hold a group's membership, `ConnectedSystemObject.AttributeValues`, `MetaverseObject.AttributeValues` and `PendingExport.AttributeValueChanges`, are a `ReferenceIndexedList<T>`: an ordered list, behaving as the `List<T>` it replaces, that answers membership by reference from an index once it holds more than 32 items. Tracking is unchanged, so nothing downstream had to change with it; the ripple was call sites assigning a `List<T>` to the property, and parameters narrowed to `List<T>` that now take an interface. The index is maintained only by writes, so concurrent readers are as safe as with a list, and objects with a few dozen values pay no memory for it. The Pending Export collection is on the hot path too: export evaluation prefetches a page's Pending Exports tracked, so a large group's pending membership change loaded the same way.

The page load, measured on one group the same way after the change:

| Values | Page load |
|---|---|
| 50,000 | 1.0 s |
| 100,000 | 2.0 to 2.6 s |
| 400,000 | 9.4 to 9.8 s |

Linear now, at roughly 23 µs per value against 0.15 µs to read the row, which is EF's ordinary per-entity cost. Projected at 500,000 values that is about 12 s, against the 47 to 72 s measured. Taking the page load off EF altogether would buy the remaining factor, but it reopens Phase 2's duplicate-instance hazards across every tracked include path that reaches a page's objects, so it is not worth doing until a scale run shows the linear cost matters.

Tests: `ReferenceIndexedListTests` checks list behaviour at both sizes and a 20,000-step random sequence against a plain list searched by reference. `LargeValueCollectionLoadDatabaseTests` loads each of the three collections twice on one tracking context (and the Connected System Object's values once more through fix-up) and requires every value once, tracked once and unchanged; EF relies on the collection's answer to avoid adding an entity twice, and with that answer broken all four fail. `ValueCollectionNavigationTypeTests` fails if any navigation onto a value entity is a list again; it failed on the previous code, naming all three.

### Phase 5: Scale verification ✅

- Scale750k40kGroups, both sides, against the baseline above; peak Worker memory no worse than 25.9 GiB.

**As delivered (2026-10-31):** measured at Scale500k25kGroups instead, on OpenLDAP, because the available host cannot give Docker the memory 750k needs. Two runs of Scenario 008, both passing every step: one after Phases 1 to 3 (30 October, `a5407873d`) and one after Phase 4 (31 October, `dac0b373a`). Neither run had an `AttachToTracker` span, a `DbUpdateConcurrencyException`, a tracking conflict or an `ERR`/`FTL` line. After Phases 1 to 3, the slowest Metaverse Object page load (`Cso.LoadMetaverseObjects`, Phase 2) was 1.1 s, against 154 s at 750k on 28 October. The slowest snapshot read (`Snapshot.LoadReferencedObjects`, Phase 3) was 2.0 s, for a 500,000-member group, against a 1 s target and 14.8 s before.

| Measurement | After Phases 1 to 3 | After Phase 4 |
|---|---|---|
| Slowest page load (`Cso.Page.LoadWithAttributeValues`) | 72 s (527,000 values) | 6.1 s (591,000 values) |
| Delta Synchronisation page loads, Target (`LoadModifiedCsoPage`, total) | 169 s | 30.1 s (22 pages, slowest 6.6 s) |
| Source Full Synchronisation | 1,522.5 s | 1,454.4 s |
| Target Full Synchronisation (join) | 76.0 s | 82.0 s |
| Target Full Synchronisation (confirming) | 318.2 s | 168.3 s |
| Target Delta Synchronisation (confirming) | 224.6 s | 78.7 s |
| Whole scenario | 2 h 05 m | 1 h 32 m |
| Peak Worker memory | about 17 GiB, in the Target's confirming import | 17.5 GiB, same place |

Against the success criteria below:

- **Slowest group page:** not met at 500k. The slowest whole pages took 51 s, 45 s and 33 s, against 266 s at 750k on 28 October. The 51 s page is a Delta Synchronisation's single page, almost all of it the export no-net-change check (#2645). The 45 s and 33 s pages are the Source Full Synchronisation's largest groups, where the page load is now 6.1 s and 3.7 s and the rest is the page's own writes. The tracker attach is gone from every breakdown line.
- **Nothing tracked after a page flush:** met. The Phase 1 guard test asserts it, and Phase 2's tests extend it to the page's Metaverse Objects.
- **Target side at least 30% faster:** met for the confirming synchronisations: the Full Synchronisation is 47% faster and the Delta Synchronisation 65% faster than after Phases 1 to 3. The joining Full Synchronisation did not change; its objects were provisioned by JIM and already joined, so no match ran and nothing in this plan applied to it.
- **Scenario outcomes unchanged:** met. Scenarios 008 and 032 passed at Medium on OpenLDAP after every phase, and Scenario 008 passed at 500k after Phases 3 and 4.

The 28 October 750k baseline is not directly comparable at 500k, so it isn't in the table. Worker memory during synchronisation stays near 3 GiB; the peak belongs to the import, which this plan did not touch.

What now dominates a large group's synchronisation is outside this plan. In the Source Full Synchronisation it is linear bulk writing: in the 30 October breakdown, creating Metaverse Objects took 225 s and writing execution items 247 s. In a Delta Synchronisation it is the export no-net-change check, which is quadratic in a group's membership (50 s for one 50,000-member group, [#2645](https://github.com/TetronIO/JIM/issues/2645)).

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
