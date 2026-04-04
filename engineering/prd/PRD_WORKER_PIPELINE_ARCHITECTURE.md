# Worker Pipeline Architecture

- **Status:** Planned
- **Created:** 2026-04-02
- **Author:** Jay Van der Zant
- **Issue:** [#451](https://github.com/TetronIO/JIM/issues/451)

## Problem Statement

JIM's sync pipeline cannot process environments with more than ~50,000 identity objects. A Full Sync of 100,000 objects (XLarge integration template) crashes with an out-of-memory error on a 15.8 GB worker, despite objects being processed in pages of 500.

Two root causes have been empirically identified:

1. **EF Core change tracker accumulation** — A single `DbContext` lives for the entire sync run. Every entity touched (CSOs, MVOs, attribute values, RPEIs) remains tracked. At 100K objects with ~5 entities each, the tracker grows to 500K+ entries, consuming multiple GB and slowing every subsequent `SaveChanges` call.

2. **Export evaluation cache** — `BuildExportEvaluationCacheAsync` loads ALL connected system objects and their attribute values for every target system into memory before processing begins. For a deployment with 100K objects and 2 target systems, this is ~200K CSOs + attribute values loaded upfront.

Together, these cause superlinear memory growth — doubling the object count more than doubles memory consumption. The current architecture has no mechanism to bound memory usage regardless of dataset size.

## Goals

- JIM must be able to Full Sync 100,000+ identity objects without crashing
- Memory consumption during sync must be bounded — proportional to page size, not total dataset size
- No regression in sync correctness (attribute flow, reference resolution, export evaluation, change tracking)
- No regression in sync performance for deployments under 50K objects
- Existing integration tests (Scenarios 1-8, all templates up to Large) must continue to pass

## Non-Goals

- Horizontal scaling / multi-worker parallelism — that is a separate future initiative
- Intra-page parallelism (processing multiple CSOs concurrently within a page) — the sync engine's two-pass design and reference resolution ordering make this complex; defer
- Replacing the `JimApplication` facade — Option A already extracted `ISyncEngine` and `ISyncRepository`; the facade can be simplified later
- Changing the sync page size or processing order — these are orthogonal tunables
- Delta Sync optimisation — Delta Sync processes small changesets by nature and is not memory-constrained; however, the same architectural patterns should apply for consistency

## User Stories

1. As an administrator deploying JIM for a large enterprise (100K+ identities), I want Full Sync to complete successfully so that I can use JIM without provisioning excessive RAM or splitting my environment artificially.

2. As an administrator, I want JIM's memory usage to be predictable so that I can right-size the worker container without trial-and-error.

3. As a developer, I want the sync pipeline's memory lifecycle to be explicit and testable so that I can reason about resource usage and catch regressions.

## Requirements

### Functional Requirements

1. **Scoped DbContext per sync page**: Each page of CSOs must be processed with a fresh DbContext. The change tracker must not accumulate entities across pages. State that needs to survive across pages (cross-page references, activity metadata) must be managed explicitly outside the DbContext.

2. **Paged export evaluation**: The export evaluation cache must not load all target CSOs upfront. Instead, export evaluation must query only the CSOs relevant to the current page's MVOs. The cache can be rebuilt per page or use a streaming/lookup pattern.

3. **Preserved two-pass processing**: The existing two-pass pattern (Pass 1: disconnections/teardown, Pass 2: joins/projections/flow) must be maintained within each page. This ordering guarantee is a correctness requirement.

4. **Preserved cross-page reference resolution**: References to CSOs on future pages must still be deferred and resolved after all pages are processed. The resolution mechanism must work with the new scoped-DbContext model.

5. **Preserved change history tracking**: MVO change objects must still be created and persisted correctly. The change tracking system must work within the per-page DbContext scope.

6. **Preserved RPEI bulk insert**: Activity execution items must still be bulk-inserted with `AutoDetectChanges=false` to prevent unintended navigation property insertion.

7. **Consistent pattern across processors**: `SyncFullSyncTaskProcessor` and `SyncDeltaSyncTaskProcessor` must both use the new pattern. `SyncImportTaskProcessor` should be evaluated for the same treatment.

### Non-Functional Requirements

- Full Sync of 100K objects must complete on a worker with 8 GB RAM allocated
- Memory usage during sync must not grow by more than 20% between the first and last page (i.e., bounded, not accumulating)
- No new NuGet packages required
- Must work in air-gapped environments (no cloud dependencies)

## Examples and Scenarios

### Scenario 1: XLarge Full Sync Completes

**Given**: A JIM deployment with 100,000 users and 50 groups across 2 connected systems, worker allocated 8 GB RAM
**When**: A Full Sync is triggered on the source system
**Then**: The sync completes successfully, all objects are synchronised correctly, memory usage stays bounded throughout

### Scenario 2: Memory Stays Flat Across Pages

**Given**: A sync run processing 200 pages of 500 CSOs each (100K total)
**When**: Observing memory usage at page 1 vs page 100 vs page 200
**Then**: Memory usage at page 200 is within 20% of memory usage at page 1 (no linear accumulation)

### Scenario 3: Export Evaluation Correctness

**Given**: An MVO on page 50 has export rules targeting 2 connected systems
**When**: Export evaluation runs for this MVO
**Then**: The correct target CSOs are found and attribute changes are evaluated identically to the current architecture — no missing exports, no phantom exports

### Scenario 4: Cross-Page Reference Resolution

**Given**: A group CSO on page 1 references member CSOs that appear on pages 50-100
**When**: All pages have been processed
**Then**: All member references are resolved correctly, identical to current behaviour

### Scenario 5: No Regression on Small Deployments

**Given**: A JIM deployment with 100 users (Small template)
**When**: Running the full integration test suite (Scenarios 1-8)
**Then**: All tests pass with identical outcomes, no performance regression

## Constraints

- Must preserve the `ISyncEngine` / `ISyncRepository` boundary established by Option A
- Must not break the in-memory test repository (`JIM.InMemoryData`) — unit and workflow tests must continue to work
- Must not add EF Core migrations (this is a runtime behaviour change, not a schema change)
- The worker must remain a single-process, single-threaded-per-task design (horizontal scaling is a separate initiative)
- Must use British English throughout

## Affected Areas

| Area | Impact |
|------|--------|
| Worker | `SyncFullSyncTaskProcessor` — scoped DbContext per page, restructured page loop |
| Worker | `SyncDeltaSyncTaskProcessor` — same treatment as Full Sync for consistency |
| Application | `ExportEvaluationServer.BuildExportEvaluationCacheAsync` — paged/streaming cache |
| Application | `SyncServer` — may need new methods for page-scoped repository creation |
| Data | `ISyncRepository` — may need factory/scope methods for per-page context |
| PostgresData | `PostgresDataRepository` — DbContext lifecycle changes |
| Tests | New unit tests for bounded memory behaviour; existing tests must pass |

## Dependencies

- Option A (Surgical Refactor) must be complete — it is (#394, #422, #430)
- No external dependencies

## Open Questions

1. **DbContext scoping mechanism**: Should we use `IDbContextFactory` to create a fresh context per page (simple but requires re-querying activity/run metadata), or use a "detach and re-attach" pattern (complex but avoids re-queries)?

2. **Export evaluation cache strategy**: Should we (a) rebuild the cache per page with only the relevant target CSOs, (b) use a lazy-loading dictionary that queries on cache-miss, or (c) keep the full cache but use `AsNoTracking` projections so entities don't enter the tracker?

3. **Cross-page reference state**: Currently stored in `_unresolvedCrossPageReferences` which is a simple list. With scoped DbContext, the final resolution pass needs its own context. Is the current list structure sufficient, or do we need to persist unresolved references to the database?

4. **Delta Sync alignment**: Should Delta Sync get the same treatment now (for consistency) or later (since it processes small changesets and isn't memory-constrained)?

## Acceptance Criteria

- [ ] Full Sync of XLarge template (100K objects) completes without OOM on 8 GB worker
- [ ] Memory usage measured at page 1 and final page shows bounded growth (< 20% increase)
- [ ] All existing unit tests pass without modification
- [ ] All existing workflow tests pass without modification
- [ ] Integration tests Scenario 1-8 pass on Small template with identical outcomes
- [ ] Integration test Scenario 8 passes on Large template (10K objects)
- [ ] Integration test Scenario 8 passes on XLarge template (100K objects)
- [ ] Export evaluation produces identical results for a reference deployment (diff sync outcomes before/after)
- [ ] Cross-page reference resolution works correctly for groups with members spanning multiple pages
- [ ] No new NuGet packages introduced
- [ ] Change history tracking works correctly with scoped DbContext

## Additional Context

- **Empirical memory findings (April 2026)**: Documented in [`docs/plans/done/WORKER_REDESIGN_OPTIONS.md`](../plans/done/WORKER_REDESIGN_OPTIONS.md) — XLarge OOM at 15.8 GB, root causes confirmed via investigation
- **Option A completion**: The surgical refactor (#394, #422, #430) extracted `ISyncEngine`, `ISyncRepository`, and `SyncServer`, providing clean boundaries for this work
- **Issue #383**: Worker performance optimisations — subtask 3.1 (streaming/paged sync queries) was deferred to this initiative; subtasks 3.2, 3.3 were discounted; subtask 3.4 (O(1) removal check) was implemented
- **Current architecture**: Single DbContext per sync run, 13 batch collections flushed per page, export evaluation cache loaded once at start, change tracker only cleared after MVO deletions
