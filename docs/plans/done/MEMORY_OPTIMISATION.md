# Memory Optimisation for Sync Processors

- **Status:** Done
- **Note:** Phases 1-2 and batched save phase complete. Paged import processing (Phase 4) deferred — implement if memory requirements need reducing below current thresholds.
> **Milestone**: Post-MVP
> **Priority**: High
> **Effort**: Medium (3 phases completed, 1 deferred)

## Overview

Analysis of heavyweight in-memory patterns in JIM's sync processors that risk out-of-memory (OOM) failures at scale (100K+ objects). Implemented incremental optimisations to reduce peak memory usage during sync operations. The save phase is now batched and memory-bounded. The import processing phase still loads all objects into memory before the save phase, which sets the minimum memory requirement for large imports.

## Business Value

Enterprise deployments synchronising 100K+ objects can exhaust container memory limits during sync operations due to unbounded in-memory accumulation of Run Profile Execution Items (RPEIs) and related data structures. This causes OOM crashes, failed syncs, and data integrity risk. The optimisations implemented enable reliable synchronisation at scale, with documented memory requirements for different deployment sizes.

## Memory Requirements

Measured from XLarge integration tests (100K objects, 20 attributes each):

| Import Phase | GC Heap | Working Set | Notes |
|-------------|---------|-------------|-------|
| After import processing | 1,413 MB | 2,274 MB | All CSOs + RPEIs + attribute values in memory |
| After GC.Collect | 696 MB | 984 MB | Import result objects released |
| Per save batch (2000 CSOs) | +200-500 MB | +300-600 MB | CSO persistence + change objects |

**Recommended stack RAM by connected system size:**

| Connected System Size | Minimum RAM (Stack) | Recommended RAM (Stack) |
|----------------------|--------------------|-----------------------|
| Up to 10,000 objects | 4 GB | 8 GB |
| 10,000 - 50,000 objects | 8 GB | 12 GB |
| 50,000 - 100,000 objects | 12 GB | 16 GB |
| 100,000+ objects | 16 GB | 24 GB |

**Development environments:** A 16 GB Codespace cannot run XLarge tests because the IDE and dev tools consume ~9-10 GB, leaving insufficient memory for the Docker stack. Use a 32 GB environment or a dedicated test machine.

## Findings

### 1. RPEI Accumulation for Summary Stats ✅

- **Location**: `SyncTaskProcessorBase._allPersistedRpeis`, `SyncImportTaskProcessor._allPersistedImportRpeis`
- **Problem**: All RPEIs from every page were accumulated across the entire sync run. At 100K objects with change tracking, each RPEI carried ConnectedSystemObjectChange graphs (~50 attribute changes each), creating ~5M objects in memory.
- **Fix**: Added `AccumulateActivitySummaryStats` to `Worker.cs` — computes running tallies using `+=` during each `FlushRpeisAsync` call. Removed `_allPersistedRpeis` and `_allPersistedImportRpeis` accumulation lists entirely. RPEIs are released immediately after each flush. Import processor retains only a lightweight `_reconciliationRpeiLookup` dictionary for the update-phase RPEIs needed by pending export reconciliation.
- **Commits**: `ec43acee`

### 2. RPEI Error Detection ✅

- **Problem**: Previously loaded all RPEIs onto `Activity.RunProfileExecutionItems` (EF navigation property) to check for errors, triggering EF change tracker graph traversal across RPEIs, CSOs, AttributeValues, etc.
- **Fix**: Replaced with `GetActivityRpeiErrorCountsAsync` -- a single `GROUP BY` query returning `(totalWithErrors, totalRpeis)`. Implemented across all four processor types (import, export, full sync, delta sync).
- **Commit**: `28ea3883`

### 3. Batched Save Phase ✅

- **Problem**: After import processing, `CreateConnectedSystemObjectsAsync` and `PersistRpeiCsoChangesAsync` processed all objects at once. At 100K CSOs × 20 attributes, this created millions of EF tracked entities causing OOM.
- **Fix**: Multiple optimisations across several commits:
  - Batched CSO creation (2000 per batch) with progress updates
  - Sub-batched CSO change persistence (500 per sub-batch) with entity detachment
  - O(n²) RPEI lookup replaced with O(1) dictionary lookup
  - Explicit `ConnectedSystemObjectId` FK on `ConnectedSystemObjectChange` (promoted from shadow FK)
  - EF graph traversal prevention: null navigation properties before `AddRange`
  - 5-minute command timeout for bulk SQL inserts
  - CSO memory release: `RemoveRange` from front of list after each batch
  - RPEI FK sync after Id generation (Ids are `Guid.Empty` until bulk insert)
  - Object reference matching for batch RPEI extraction (not ID-based, since IDs not yet assigned)
  - `OnDelete(SetNull)` FK migration for `ConnectedSystemObjectChange → RPEI`
- **Commits**: `e25bac3d`, `369dfd2d`, `b8baa989`, `5ac808b6`, `00bacab0`, `6bc73568`, `3a380b8b`, `416effbf`, `47494069`, `9bd05379`, `ed245061`

### 4. Export Evaluation Cache (DEFERRED)

- **Location**: `ExportEvaluationServer.BuildExportEvaluationCacheAsync`
- **Problem**: Loads all target system CSOs with attribute values for no-net-change detection. For a target system with 100K CSOs each having 10-20 attributes, this is a large in-memory dataset.
- **Mitigation**: This is a one-time load at sync start, bounded by the target system size. It enables O(1) lookup during sync vs O(N) per-CSO queries.
- **Potential fix**: For very large target systems, consider a streaming/paged approach where cache is built per-page of source CSOs being processed. However, this adds complexity and may hurt performance for medium-sized deployments.
- **Recommendation**: Monitor but defer -- the cache is necessary for correctness and the current approach is acceptable for most deployments.

### 5. Per-Page Batch Collections (NO ACTION NEEDED)

- **Location**: `SyncTaskProcessorBase` -- `_pendingMetaverseObjects`, `_pendingMvoDeletions`, `_pendingExportOperations`, etc.
- **Problem**: Collections accumulate within a page and are flushed at page boundaries. Memory is bounded by page size (configurable, default varies).
- **Current state**: Already using page-based batching with flush at boundaries.

### 6. Cross-Page Reference Tracking (NO ACTION NEEDED)

- **Location**: `SyncTaskProcessorBase._crossPageUnresolvedReferences`
- **Problem**: Tracks unresolved reference attributes (lightweight tuples of CSO ID + attribute name) across pages.
- **Current state**: Only stores references that could not be resolved within their page -- typically a small fraction of total objects.

### 7. Import CSO Lookup Dictionary (DEFERRED)

- **Location**: `SyncImportTaskProcessor` -- CSO lookup by external ID
- **Problem**: For full imports, all existing CSOs are loaded into a lookup dictionary for O(1) matching. At 100K CSOs, this is significant memory.
- **Mitigation**: The log shows "no existing CSOs" for initial imports, so this only affects subsequent imports. For delta imports, only modified CSOs are processed.
- **Potential fix**: Use a database query per imported object instead of pre-loading all CSOs. However, this trades memory for N database round-trips.
- **Recommendation**: Consider a bloom filter or hash-only lookup to reduce memory while maintaining O(1) performance.

## Implementation Phases

### Phase 1: Incremental Summary Stats ✅

Replaced `_allPersistedRpeis` accumulation with `AccumulateActivitySummaryStats` — running tallies updated during each `FlushRpeisAsync` call.

- Added `AccumulateActivitySummaryStats` to `Worker.cs` (uses `+=` vs `=` in `CalculateActivitySummaryStats`)
- Updated `FlushRpeisAsync` in `SyncTaskProcessorBase` and `FlushImportRpeisAsync` in `SyncImportTaskProcessor` to compute stats before releasing RPEIs
- Removed `_allPersistedRpeis` and `_allPersistedImportRpeis` accumulation lists
- Import processor retains lightweight `_reconciliationRpeiLookup` for update-phase RPEIs only
- Estimated memory savings: 100-500MB for 100K object syncs

### Phase 2: RPEI Error Detection ✅

Replaced in-memory RPEI loading with `GetActivityRpeiErrorCountsAsync` database query.

### Phase 3: Batched Save Phase ✅

Batched CSO creation, change persistence, and RPEI flushing with incremental memory release.

- Save phase processes 2000 CSOs per batch
- Change objects persisted in sub-batches of 500 with EF entity detachment
- Processed CSOs removed from list after each batch (GC-eligible immediately)
- RPEIs flushed and detached after each batch
- Memory bounded by batch size, not total object count

### Phase 4: Paged Import Processing (DEFERRED — Future Optimisation)

The remaining memory bottleneck is `ProcessImportObjectsAsync`, which loads all imported objects into memory before the save phase begins. At 100K objects with 20 attributes each, this consumes ~1-1.5 GB.

**Proposed approach:** Process imported objects in pages instead of loading all at once:

1. Refactor `ProcessImportObjectsAsync` to process connector results page-by-page
2. Each page: process imports → create CSOs → persist immediately → release
3. Duplicate detection would need a cross-page strategy (bloom filter or DB-backed set)
4. Reference resolution would need cross-page tracking (already partially implemented)

**Complexity:** High — requires significant refactoring of the import processor's flow, which currently:
- Builds complete `connectedSystemObjectsToBeCreated` and `connectedSystemObjectsToBeUpdated` lists
- Runs deletion detection against the full set of imported external IDs
- Uses `seenExternalIds` dictionary for within-page and cross-page duplicate detection

**When to implement:** If customers need 100K+ object imports on machines with less than 16 GB RAM, or if connector page sizes are impractically small for the current approach.

### Phase 5: Export Evaluation Cache Optimisation (DEFERRED)

Only pursue if deployments report memory issues with large target systems.

- Page-based cache building aligned with source CSO pages
- Streaming approach for very large target systems

### Phase 6: Import CSO Lookup Optimisation (DEFERRED)

Only pursue if subsequent full imports of 100K+ objects cause issues.

- Bloom filter for CSO existence checks
- Hash-only lookup (external ID hash to CSO ID) to reduce per-entry memory

## Success Criteria

- ✅ Imports up to 10K objects complete reliably on 4 GB RAM
- ✅ Imports up to 50K objects complete reliably on 8-12 GB RAM
- ✅ Save phase memory is bounded by batch size (2000 CSOs), not total object count
- ✅ No regression in sync performance (wall clock time)
- ⬚ XLarge integration tests (100K objects) — requires 16+ GB stack RAM (OOM in 16 GB Codespace due to IDE overhead)

## Benefits

- **Reliability**: Eliminates OOM crashes for imports up to 50K objects on recommended hardware
- **Scalability**: Save phase memory bounded by batch size; import phase scales linearly with object count
- **Predictability**: Documented memory requirements per deployment size
- **Observability**: GC heap and working set logged at each batch boundary for capacity planning
