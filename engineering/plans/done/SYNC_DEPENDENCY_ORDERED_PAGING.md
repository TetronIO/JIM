# Dependency-Ordered Synchronisation Paging

- **Status:** Done
- **Note:** The step's remaining-time estimate was not measured at scale: with Object Types in dependency order, Scale750k40kGroups no longer runs the step at all.
- **Issue:** [#2492](https://github.com/TetronIO/JIM/issues/2492)
- **Last Updated:** 2026-10-28

## Overview

A Full Synchronisation pages through a Connected System's objects in ID order, so object types arrive interleaved. A group whose members have not yet been projected cannot flow its `member` references when its page is processed, so it is deferred to the "Resolving cross-page references" step, which reloads it, flows its references again, re-evaluates its exports and replaces the Pending Export the main loop already created. At scale that step is the slowest part of the run: in Scale750k40kGroups it handled 37,378 of 40,007 groups and took 29 minutes, against 56 minutes for the main loop's 790,007 objects.

This plan removes the deferral where it is avoidable and makes the remaining deferral cheap:

1. Page through object types in dependency order (referenced types before the types that reference them), so references between types resolve in the main loop.
2. Make the cross-page step efficient for the references ordering cannot fix: references within one type (nested groups, a person's manager).
3. Report the step's progress by the work it actually does, so the remaining-time estimate is honest.

## Business Value

- **The problem:** the first Full Synchronisation of a directory with large groups spends a large share of its run time redoing group work, and the progress display claims minutes remaining when the true figure is several times that. Administrators watching a first load see a step that appears to stall.
- **Who it affects:** every customer synchronising groups, which is nearly every customer. The cost grows with total membership, not object count, so it is worst exactly where JIM is meant to excel (all-staff style groups of hundreds of thousands of members).
- **The benefit:** group membership is synchronised once, not twice; the cross-page step shrinks to the references that genuinely need it; and its progress reporting can be trusted.

## Measured Baseline

Scale750k40kGroups, Connected System "Yellowstone APAC" (790,007 objects: 750,000 `jimPerson`, 40,007 `jimGroup`), run of 2026-10-24/25.

| Measure | Value |
|---|---|
| Objects deferred to the cross-page step | 37,378 (all groups; 93% of groups) |
| Member references held by groups | 6,521,432, all targeting `jimPerson` |
| Groups over 10,000 members | 54 (largest: 750,000) |
| Main loop (790,007 objects) | ~56 min |
| Cross-page step (37,378 objects) | 1,735 s (~29 min) |
| of which `EvaluatePendingExports` | 1,048 s (60%) |
| of which `ReloadCsosForCrossPageReferences` | 442 s (25%); two single batches took 125 s and 155 s |
| of which `PersistPendingMetaverseObjects` | 141 s |
| of which `FlushPendingExportOperations` | 64 s |

Timings are summed from slow-span log lines, so they are a floor. During a live giant-group reload the worker sat at 100% of one core while PostgreSQL waited on `ClientWrite` for over 80 seconds: the cost is worker-side materialisation, not the query.

Every group's references target `jimPerson`; there is no nested-group membership in this scenario. With dependency ordering, the cross-page step would therefore have nothing to do.

## Technical Architecture

### Current state

- `SyncFullSyncTaskProcessor` pages with `ConnectedSystemRepository.GetConnectedSystemObjectsAsync`, keyset-paginated on `Id` (`ORDER BY "Id"`, `WHERE "Id" > cursor`). Delta Synchronisation does the same through `GetConnectedSystemObjectsModifiedSinceAsync`.
- After each object, `SyncTaskProcessorBase` checks its mapped reference attributes; any value whose referenced Connected System Object has no Metaverse Object yet adds the object to `_unresolvedCrossPageReferences`.
- `ResolveCrossPageReferencesAsync` then, per batch of a page size:
  - reloads each object through `GetConnectedSystemObjectsForReferenceResolutionAsync`, which loads **every** attribute value with `Include(AttributeValues).ThenInclude(Attribute)` on the worker's tracking context (its comment says the load is untracked; the query is not);
  - re-runs inbound Attribute Flow for reference attributes only;
  - re-evaluates exports per Metaverse Object, and for each target finds the existing Pending Export, deletes it and creates a merged replacement, one object at a time (`CreateOrUpdatePendingExportWithNoNetChangeAsync`, `DeletePendingExportForMerge`).
- Progress counts objects, so one 750,000-member group and one 3-member group each count as 1.

### Proposed solution

#### 1. Dependency-ordered paging

- **Compute a type order at run start** from the active Synchronisation Rules: for each Connected System Object Type, the set of types its *mapped* reference attributes can point at.
  - Where the schema declares the target (`ConnectedSystemObjectTypeAttribute.ReferencedObjectTypeId`), use it.
  - Where it does not (LDAP `member`), treat the attribute as able to reference every selected type.
- **Topologically sort** the types. Types that reference nothing come first. A cycle (including a type referencing itself) is collapsed into one group and processed in place; its internal references are what the cross-page step exists for. Ties sort by type ID, so the order is deterministic.
  - For the undeclared-target case this degenerates to "types with no mapped reference attributes first, then the rest", which is exactly what the common person/group directory needs.
- **Page per type in that order**, keyset on `Id` within each type (`WHERE "ConnectedSystemId" = @cs AND "TypeId" = @t AND "Id" > @cursor ORDER BY "Id"`). This keeps every page O(pageSize). It needs a new index `(ConnectedSystemId, TypeId, Id)`; it supersedes `IX_ConnectedSystemObjects_ConnectedSystemId_TypeId`, which a new migration drops.
- **Keep the existing page-until-empty contract** (#2092) per type. Then run one final pass over all types for objects created while the run was in flight, so none are skipped.
- **Apply the same order to Delta Synchronisation.** There, cross-page deferral only occurs when a referenced object is new in the same delta, but ordering costs nothing and removes it for between-type references.
- The order is logged at run start (`Full Synchronisation type order: jimPerson, then jimGroup`) so a run's behaviour is explainable from its log.

#### 2. An efficient cross-page step for references within one type

Ordering cannot help a group nesting another group, or a person whose manager sits on a later page. Manager references are near-universal in real directories, so at 750,000 people roughly half could defer. The step must scale on its own:

- **Reload only what the step reads.** It runs inbound flow for mapped reference attributes only, so load those attribute values (and the reference targets' Metaverse Object IDs) with raw SQL into untracked objects, rather than every attribute value through a tracked EF Include. This follows the worker hot-path rule in `src/CLAUDE.md`, and it removes the materialisation cost seen on the 750,000-member reload. Verify first that nothing downstream in the step reads a non-reference attribute value from the reloaded object; if anything does, load that set explicitly, not everything.
- **Replace Pending Exports in bulk.** Collect each batch's merge decisions (existing Pending Export ID plus the merged attribute changes), then delete and recreate them set-based through the existing `PendingExportBulkColumns` COPY path, in one round trip per batch rather than one per object. This removes the per-object find, delete and recreate that is 60% of today's step.
- **Batch by value count, not object count.** Cap each batch at a reference-value budget as well as the page size, so a batch holding several 750,000-member groups does not hold all of them in memory at once.

#### 3. Honest progress

Count the cross-page step's progress in reference values resolved, not objects, so the rate and remaining-time estimate on the Activity page reflect the real work. The step's object count stays available in the step label.

### Data flow (Full Synchronisation, after the change)

```
Run start: compute type order from Synchronisation Rules  ->  [jimPerson] -> [jimGroup]
Main loop: page jimPerson (all projected/joined)  ->  page jimGroup (members resolve in-page)
           -> final sweep for objects created mid-run
Cross-page step: only intra-type references (nested groups, managers), value-budgeted batches,
                 raw-SQL reference reload, bulk Pending Export merge
```

## Implementation Phases

### Phase 1: Type ordering and per-type paging ✅

- `SyncTypeOrder` (JIM.Models result type plus a pure ordering function in JIM.Application or the worker) computing the order from Synchronisation Rules and schema. Unit tests: no references; one-way reference with declared target; undeclared target; self-reference; two-type cycle; deterministic tie-break.
- Repository: per-type keyset page method plus migration adding `(ConnectedSystemId, TypeId, Id)` and dropping the superseded index. `RequiresPostgres` test proving keyset correctness across types and with mid-run inserts.
- `SyncFullSyncTaskProcessor` and `SyncDeltaSyncTaskProcessor` page by type in order; the final mid-run sweep; run-start log line.
- Worker tests: a group page processed after its members resolves members in the main loop and adds nothing to `_unresolvedCrossPageReferences`; a self-referencing type still defers and still resolves.

### Phase 2: Efficient cross-page step ✅

- Raw-SQL reference-only reload (Summary-tier DTO), replacing `GetConnectedSystemObjectsForReferenceResolutionAsync` on this path, with a `RequiresPostgres` round-trip test.
- Bulk Pending Export merge for the step, sharing the per-object merge rules so behaviour is identical (Scenario 008's Pending Provisioning case must still rebuild a Create, not stage an Update). Tests cover Create, exported Create and pending Update merges.
- Value-budgeted batching.

**As delivered:** the step's cost turned out to be per-object round trips in export re-evaluation, not the reload. The step now queues the merge deletes and runs them as one statement (a Pending Export batch scope in the Sync repository: reads hide queued rows, and an insert for the same object executes its queued delete first, so every merge decision is unchanged), prefetches the batch's Pending Exports in one query, and memoises the target objects the change snapshot resolves per page. Scenario 032 Medium: 14.4 ms to 3.9 ms per reference value (11.1 s to 2.8 s), 3.7x against the 5x criterion. Of the remaining 2.7 s, export evaluation is 1.4 s and the reload 0.4 s, so the raw-SQL reference-only reload was dropped: it could save at most about 0.3 s, and a reference attribute's flow expression may read the object's other attributes, which a reference-only reload would silently withhold.

### Phase 3: Progress by reference values ✅

- Step progress counts reference values; Activity page shows the object count in the step label. bUnit/logic tests only where display logic changes.

### Phase 4: Scale verification ✅

- Re-run Scale750k40kGroups: the cross-page step should report 0 objects (no nested groups in the scenario), and total Full Synchronisation time should drop by roughly the step's 29 minutes, offset by any change in main-loop group-page cost.
- Add or extend a scenario with manager references and nested groups at medium scale to measure the Phase 2 step, since today's scale scenarios exercise no intra-type references.
- Full integration suite green; peak worker memory no worse than baseline.

**As found (first scale run, 2026-10-25):** the run saved only about 3 minutes, not 25, and peak Worker memory rose from 26.2 GB to 31.1 GB. Group pages climbed from about 4 s to 85 s as the run went on. Span breakdowns (added for this: a slow span now logs its descendants' totals by path) traced the climb to the join lookup: with statistics gathered while the type held few objects, the planner drove the match from the `MetaverseObjects` type index and read every object of the type for each value. The lookup now checks each candidate's type through a correlated subquery on its own row, which keeps the plan on the attribute value index whatever the statistics say. Preparing the lookup once per connection was tried and reverted: the generic plan cached on near-empty tables was reused as they grew and made joins slower (12 s to 31.6 s on Scenario 008 Large). The remaining group-page cost is proportional to membership and sits in EF Core's change tracker (page load, join match load, tracker attach after the bulk write); that is [#2598](https://github.com/TetronIO/JIM/issues/2598), planned in [`SYNC_HOT_PATH_OFF_EF_TRACKER.md`](../SYNC_HOT_PATH_OFF_EF_TRACKER.md).

**As verified (second scale run, 2026-10-28):** the cross-page step was skipped in all three Full Synchronisations. The first Full Synchronisation took 52 minutes, against about 85 before this work (56 minutes of main loop plus the 29-minute step). Group pages took 3 to 11 s each with no upward trend, apart from three outliers of 148 to 266 s, which are the EF Core tracker attach after the Metaverse Object bulk write (80% of each), carried forward to #2598. Peak Worker memory was 25.9 GiB, against 26.2 GiB before. All seven steps passed.

## Success Criteria

- Scale750k40kGroups Full Synchronisation: cross-page step processes 0 objects; end-to-end run time falls by at least 25 minutes against the 2026-10-24/25 baseline.
- Group membership flows and Pending Exports are identical to today's output for every integration scenario.
- A scenario with intra-type references (managers, nested groups) completes its cross-page step at least 5x faster than today's code on the same data.
- The step's remaining-time estimate stays within 25% of actual through the step.
- No regression in peak worker memory.

## Benefits

- **Performance:** group membership is evaluated and exported once per run, not twice; the remaining cross-page work is set-based.
- **UX:** first loads finish sooner, and the progress display can be believed.
- **Architecture:** the deferral mechanism goes back to being a safety net for genuinely circular references, rather than the normal path for most groups.

## Dependencies

None external. One EF Core migration (index change).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Group pages become denser (80 pages of only groups instead of groups spread over 1,581), raising per-page memory when several giant groups share a page | Value-budgeted page sizing for the main loop as well as the cross-page step; verify peak memory in Phase 4 against the host's OOM envelope |
| A type order derived from Synchronisation Rules misses a reference path (e.g. an expression that produces a reference) | The cross-page step remains as the safety net, so a missed path costs time, never correctness; log the order so it is diagnosable |
| Mid-run inserts skipped by per-type keyset paging | Final all-types sweep, covered by a `RequiresPostgres` test |
| Bulk Pending Export merge diverging from per-object merge semantics | Both paths share one merge-decision function; tests assert identical output for each merge case |
| Delta Synchronisation ordering changes Activity item order | Item order is not a contract; integration scenarios assert outcomes, not order |
