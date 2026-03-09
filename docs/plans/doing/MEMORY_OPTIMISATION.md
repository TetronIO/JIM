# Memory Optimisation for Sync Processors

- **Status:** Doing
> **Milestone**: Post-MVP
> **Priority**: High
> **Effort**: Medium (3 phases)

## Overview

Analysis of heavyweight in-memory patterns in JIM's sync processors that risk out-of-memory (OOM) failures at scale (100K+ objects). Proposes incremental optimisations to reduce peak memory usage during sync operations, ensuring memory consumption is bounded by page size rather than total object count.

## Business Value

Enterprise deployments synchronising 100K+ objects can exhaust container memory limits during sync operations due to unbounded in-memory accumulation of Run Profile Execution Items (RPEIs) and related data structures. This causes OOM crashes, failed syncs, and data integrity risk. Optimising these patterns enables reliable enterprise-scale synchronisation without requiring oversized containers.

## Findings

### 1. RPEI Accumulation for Summary Stats (HIGH RISK)

- **Location**: `SyncTaskProcessorBase._allPersistedRpeis` (line ~109), `SyncImportTaskProcessor._allPersistedImportRpeis`
- **Problem**: All RPEIs from every page are accumulated in `_allPersistedRpeis` across the entire sync run. At 100K objects with outcome tracking, each RPEI carries SyncOutcome graphs, CSO snapshots, and error details. Peak memory: 100-500MB for 100K objects.
- **Current state**: RPEIs are bulk-inserted to the database per page, but kept in memory for `CalculateActivitySummaryStats` at the end.
- **Fix**: Compute summary stats incrementally during each `FlushRpeisAsync` call instead of accumulating all RPEIs. Replace the final `CalculateActivitySummaryStats(allRpeis)` with a running tally updated per-page. This eliminates the need to hold all RPEIs in memory.
- **Effort**: Medium -- requires refactoring `CalculateActivitySummaryStats` to support incremental updates.

### 2. RPEI Error Detection (DONE)

- **Problem**: Previously loaded all RPEIs onto `Activity.RunProfileExecutionItems` (EF navigation property) to check for errors, triggering EF change tracker graph traversal across RPEIs, CSOs, AttributeValues, etc.
- **Fix**: Replaced with `GetActivityRpeiErrorCountsAsync` -- a single `GROUP BY` query returning `(totalWithErrors, totalRpeis)`. Implemented across all four processor types (import, export, full sync, delta sync).
- **Commit**: `28ea3883`

### 3. Export Evaluation Cache (MEDIUM-HIGH RISK)

- **Location**: `ExportEvaluationServer.BuildExportEvaluationCacheAsync`
- **Problem**: Loads all target system CSOs with attribute values for no-net-change detection. For a target system with 100K CSOs each having 10-20 attributes, this is a large in-memory dataset.
- **Mitigation**: This is a one-time load at sync start, bounded by the target system size. It enables O(1) lookup during sync vs O(N) per-CSO queries.
- **Potential fix**: For very large target systems, consider a streaming/paged approach where cache is built per-page of source CSOs being processed. However, this adds complexity and may hurt performance for medium-sized deployments.
- **Recommendation**: Monitor but defer -- the cache is necessary for correctness and the current approach is acceptable for most deployments.

### 4. Per-Page Batch Collections (MEDIUM RISK)

- **Location**: `SyncTaskProcessorBase` -- `_pendingMetaverseObjects`, `_pendingMvoDeletions`, `_pendingExportOperations`, etc.
- **Problem**: Collections accumulate within a page and are flushed at page boundaries. Memory is bounded by page size (configurable, default varies).
- **Current state**: Already using page-based batching with flush at boundaries.
- **Recommendation**: No action needed -- bounded by page size which is configurable.

### 5. Cross-Page Reference Tracking (LOW RISK)

- **Location**: `SyncTaskProcessorBase._crossPageUnresolvedReferences`
- **Problem**: Tracks unresolved reference attributes (lightweight tuples of CSO ID + attribute name) across pages.
- **Current state**: Only stores references that could not be resolved within their page -- typically a small fraction of total objects.
- **Recommendation**: No action needed -- lightweight data structures, small cardinality.

### 6. Import CSO Lookup Dictionary (MEDIUM RISK)

- **Location**: `SyncImportTaskProcessor` -- CSO lookup by external ID
- **Problem**: For full imports, all existing CSOs are loaded into a lookup dictionary for O(1) matching. At 100K CSOs, this is significant memory.
- **Mitigation**: The log shows "no existing CSOs" for initial imports, so this only affects subsequent imports. For delta imports, only modified CSOs are processed.
- **Potential fix**: Use a database query per imported object instead of pre-loading all CSOs. However, this trades memory for N database round-trips.
- **Recommendation**: Consider a bloom filter or hash-only lookup to reduce memory while maintaining O(1) performance.

## Implementation Phases

### Phase 1: Incremental Summary Stats (HIGH PRIORITY)

Replace `_allPersistedRpeis` accumulation with running tallies updated during each `FlushRpeisAsync` call.

- Modify `CalculateActivitySummaryStats` to accept incremental updates
- Update `FlushRpeisAsync` in both `SyncTaskProcessorBase` and `SyncImportTaskProcessor` to compute stats before clearing RPEIs
- Remove `_allPersistedRpeis` and `_allPersistedImportRpeis` accumulation lists
- Estimated memory savings: 100-500MB for 100K object syncs

### Phase 2: Export Evaluation Cache Optimisation (DEFERRED)

Only pursue if deployments report memory issues with large target systems.

- Page-based cache building aligned with source CSO pages
- Streaming approach for very large target systems

### Phase 3: Import CSO Lookup Optimisation (DEFERRED)

Only pursue if subsequent full imports of 100K+ objects cause issues.

- Bloom filter for CSO existence checks
- Hash-only lookup (external ID hash to CSO ID) to reduce per-entry memory

## Success Criteria

- XLarge integration tests (100K objects) complete without OOM crashes
- Peak memory usage during sync stays below container memory limit (default 512MB)
- No regression in sync performance (wall clock time)

## Benefits

- **Reliability**: Eliminates OOM crashes at enterprise scale
- **Scalability**: Enables 100K+ object syncs without proportional memory growth
- **Predictability**: Memory usage bounded by page size, not total object count
