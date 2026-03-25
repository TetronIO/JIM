# Phase 10: Intra-Phase Parallelism

- **Status:** Doing
- **Issue:** [#394](https://github.com/TetronIO/JIM/issues/394)
- **Parent:** [Worker Redesign Option A](WORKER_REDESIGN_OPTIONS.md)
- **Created:** 2026-03-25

## Overview

Phase 10 introduces parallelism *within* each sync phase (import, sync, export) to utilise multiple CPU cores during bulk operations. This is the final phase of Option A and the primary lever for achieving the 2-5x performance target.

The prerequisite architecture is complete: ISyncEngine is stateless, ISyncRepository is formalised, DI provides per-scope isolation, and bulk SQL owns all hot paths. Phase 10 exploits these boundaries to run work concurrently.

## The Problem

During large sync operations (5,000+ objects), PostgreSQL hits 100% CPU on a single core while other cores sit idle. This is a fundamental consequence of PostgreSQL's process-per-connection architecture: a single SQL statement executes entirely within one OS process on one core. No amount of PostgreSQL tuning changes this for write workloads.

The application currently sends all writes through a single connection per phase, serialising all work onto one core regardless of available hardware.

## Architecture: Current vs. Proposed

### Current Architecture (Sequential)

```
+------------------------------------------------------------------+
|                        Worker Host                                |
|  Polls for tasks, spawns one Task.Run per work item               |
+------------------------------------------------------------------+
         |
         | One task at a time per connected system
         v
+------------------------------------------------------------------+
|                    Import Processor                                |
|                                                                    |
|  for each page from connector:                                    |
|    accumulate CSOs to create/update                                |
|                                                                    |
|  for each batch of 2,000 CSOs:          <-- sequential            |
|    INSERT CSOs via single connection     <-- 1 core busy          |
|    INSERT RPEIs via single connection    <-- 1 core busy          |
|    update progress                                                 |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                     Sync Processor                                 |
|                                                                    |
|  for each page of CSOs from DB:                                   |
|    Pass 1: teardown + export confirmation  <-- sequential         |
|    Pass 2: join/project/flow (ISyncEngine) <-- sequential         |
|    Flush: 11-step persistence sequence     <-- 1 core busy       |
|      MVO creates/updates                                           |
|      pending exports                                               |
|      RPEIs                                                         |
|      MVO deletions                                                 |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                    Export Processor                                 |
|                                                                    |
|  for each batch of pending exports:                               |
|    SemaphoreSlim(MaxParallelism)          <-- ALREADY parallel    |
|    per-batch connector + per-batch repo                            |
|    Task.WhenAll(batches)                                           |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                     PostgreSQL                                     |
|                                                                    |
|  Connection 1: [====CPU 100%====]  Core 0                         |
|  Connection 2: (idle)              Core 1                          |
|  Connection 3: (idle)              Core 2                          |
|  Connection 4: (idle)              Core 3                          |
+------------------------------------------------------------------+
```

**Key bottleneck:** All writes funnel through one connection -> one PostgreSQL process -> one CPU core.

### Proposed Architecture (Parallel)

```
+------------------------------------------------------------------+
|                        Worker Host                                |
|  Polls for tasks, spawns one Task.Run per work item               |
|  Configurable: JIM_WRITE_PARALLELISM (default: CPU count)        |
+------------------------------------------------------------------+
         |
         | One task at a time per connected system
         v
+------------------------------------------------------------------+
|                    Import Processor                                |
|                                                                    |
|  for each page from connector:                                    |
|    accumulate CSOs to create/update                                |
|                                                                    |
|  Parallel batch writer (N connections):                            |
|  +-------------------------------+                                 |
|  | Batch Coordinator             |                                 |
|  | splits 10,000 CSOs into N     |                                 |
|  | sub-batches, dispatches to    |                                 |
|  | N connections concurrently    |                                 |
|  +--+--------+--------+---------+                                 |
|     |        |        |                                            |
|     v        v        v                                            |
|  [Conn1]  [Conn2]  [Conn3]  ... [ConnN]                          |
|  2,500    2,500    2,500       2,500 CSOs                         |
|  INSERT   INSERT   INSERT      INSERT                              |
|  RPEIs    RPEIs    RPEIs       RPEIs                              |
|  +-------------------------------+                                 |
|  | await Task.WhenAll(batches)   |                                 |
|  +-------------------------------+                                 |
|  Cross-batch reference fixup (single connection)                  |
|  Update progress                                                   |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                     Sync Processor                                 |
|                                                                    |
|  Shared (read-only after init):                                   |
|    sync rules, object types, export eval cache,                   |
|    pending exports dict, drift cache                              |
|                                                                    |
|  for each page of CSOs from DB:                                   |
|    +-------------------------------------------+                   |
|    | CSO Processing (ISyncEngine)              |                   |
|    | Pass 1: teardown + export confirmation    |                   |
|    | Pass 2: join/project/flow                 |                   |
|    |   Engine is pure -- no shared mutable     |                   |
|    |   state except MVO pending collections    |                   |
|    +-------------------------------------------+                   |
|                                                                    |
|    Parallel page flush (N connections):                            |
|    +-------------------------------+                               |
|    | Flush Coordinator             |                               |
|    | partitions MVOs, PEs, RPEIs   |                               |
|    | across N connections          |                               |
|    +--+--------+--------+---------+                               |
|       |        |        |                                          |
|       v        v        v                                          |
|    [Conn1]  [Conn2]  [Conn3]                                     |
|    MVO batch PE batch  RPEI batch                                 |
|    +-------------------------------+                               |
|    | await Task.WhenAll(flushes)   |                               |
|    +-------------------------------+                               |
|    Cross-page ref resolution                                      |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                    Export Processor                                 |
|  (Already parallel -- extend with write parallelism)              |
|                                                                    |
|  Per-batch: connector + repo (existing pattern)                   |
|  NEW: RPEI persistence uses parallel batch writer                 |
+------------------------------------------------------------------+
         |
         v
+------------------------------------------------------------------+
|                     PostgreSQL                                     |
|                                                                    |
|  Connection 1: [====CPU====]  Core 0                              |
|  Connection 2: [====CPU====]  Core 1                              |
|  Connection 3: [====CPU====]  Core 2                              |
|  Connection 4: [====CPU====]  Core 3                              |
|                                                                    |
|  All cores utilised during bulk write phases                      |
+------------------------------------------------------------------+
```

## Design Details

### 1. Parallel Batch Writer (new shared component)

The core building block: a reusable writer that splits a collection across N connections.

```
+--------------------------------------------------+
|              ParallelBatchWriter<T>               |
|                                                    |
|  Input: IReadOnlyList<T> items                    |
|         Func<NpgsqlConnection, IReadOnlyList<T>,  |
|              Task> writeAction                    |
|         int parallelism                           |
|                                                    |
|  Behaviour:                                        |
|  1. Partition items into N sub-lists              |
|  2. Acquire N connections from NpgsqlDataSource   |
|  3. Execute writeAction on each (conn, sublist)   |
|  4. await Task.WhenAll                            |
|  5. Return connections to pool                    |
|                                                    |
|  Lives in: SyncRepository (or helper)             |
|  Used by:  Import flush, Sync flush, Export flush |
+--------------------------------------------------+
```

**Key design choices:**
- Uses `NpgsqlDataSource.OpenConnectionAsync()` directly (not EF DbContext) for write parallelism
- Each connection runs its own transaction
- Caller decides partitioning strategy (round-robin, by ID range, by table)
- Parallelism capped at `JIM_WRITE_PARALLELISM` (env var, defaults to `Environment.ProcessorCount`)

### 2. Import Parallelism

```
Current:                           Proposed:

for batch in batches:              all_csos = accumulated from pages
  await CreateCsos(batch)            |
  await FlushRpeis(batch)            v
  update progress                  ParallelBatchWriter splits across N conns:
                                     Conn1: CreateCsos(chunk1) + FlushRpeis(chunk1)
                                     Conn2: CreateCsos(chunk2) + FlushRpeis(chunk2)
                                     ConnN: CreateCsos(chunkN) + FlushRpeis(chunkN)
                                     |
                                     v
                                   await Task.WhenAll
                                     |
                                     v
                                   Cross-batch reference fixup (single conn)
                                   Update progress
```

**What changes:**
- `SyncImportTaskProcessor` accumulates all CSOs as before (connector pages are sequential — connector I/O is the bottleneck there, not CPU)
- Create phase: instead of sequential 2K batches, partition all creates across N connections
- Update phase: same parallel pattern
- Reference fixup: remains single connection (it's a single UPDATE...JOIN, already fast with functional indexes)
- Cache updates: happen after all parallel writes complete (single-threaded, in-memory)

**What stays the same:**
- Connector pagination (sequential — connectors are not thread-safe)
- CSO accumulation (single-threaded in-memory)
- Deletion detection (single-threaded, read-heavy)

### 3. Sync Parallelism

Sync is more nuanced because the CSO processing loop has ordering dependencies.

```
Current per-page:                    Proposed per-page:

Pass 1: foreach cso -> teardown      Pass 1: foreach cso -> teardown
Pass 2: foreach cso -> join/flow       (sequential -- ordering matters)
  |                                  Pass 2: foreach cso -> join/flow
  v                                    (sequential -- MVO join counts)
11-step flush (single conn):           |
  1. MVO creates/updates               v
  2. Change history                  Parallel flush (N connections):
  3. Export evaluation               +----------------------------------+
  4. Pending exports                 | Partitioned by entity type:      |
  5. Reference snapshots             |                                  |
  6. Obsolete CSO cleanup            | Conn1: MVO creates/updates       |
  7. MVO deletions                   | Conn2: Pending exports           |
  8. RPEIs                           | Conn3: RPEIs + change history    |
  9. Clear tracker                   | Conn4: Obsolete CSO + MVO del   |
  10. Update activity                |                                  |
  11. Re-enable auto-detect          | await Task.WhenAll               |
                                     +----------------------------------+
                                     Export evaluation (after MVOs committed)
                                     Reference snapshots
                                     Update activity
```

**Why CSO processing stays sequential:**
- Pass 1 records disconnections in `_pendingDisconnectedMvoIds` — Pass 2 reads this to avoid joining MVOs about to lose their authority
- Join evaluation checks `existingJoinCount` which can change as CSOs in the same page join the same MVO
- ISyncEngine is pure, but the *orchestrator state* between engine calls has ordering dependencies

**What parallelises:**
- The 11-step page flush is the bottleneck (bulk writes). Partition by entity type:
  - **Group A** (independent): MVO creates/updates, change history
  - **Group B** (independent): Pending export creates/updates/deletes
  - **Group C** (independent): RPEI bulk insert
  - **Group D** (independent after A): Obsolete CSO cleanup, MVO deletions
- Export evaluation must wait for MVOs to be committed (depends on Group A)
- Reference snapshots must wait for pending exports (depends on Group B)

**Dependency graph for flush:**
```
        +-------+     +-------+     +-------+
        | MVOs  |     |  PEs  |     | RPEIs |
        | (A)   |     |  (B)  |     |  (C)  |
        +---+---+     +---+---+     +-------+
            |             |
            v             v
      +----------+  +-----------+
      | Export   |  | Ref       |
      | Eval     |  | Snapshots |
      +----------+  +-----------+
            |
            v
      +----------+     +-------+
      | Obsolete |     | MVO   |
      | CSOs (D) |     | Del   |
      +----------+     +-------+
```

### 4. Export Parallelism Enhancement

Export already uses `SemaphoreSlim` + per-batch connector/repo. Phase 10 adds:

- **RPEI persistence** after export completion uses the parallel batch writer
- **Pending export status updates** use parallel batch writer
- No structural change to the export dispatch model

### 5. Connection Management

```
+--------------------------------------------------+
|              NpgsqlDataSource                     |
|  (single instance, injected via DI)              |
|                                                    |
|  Pool: min=5, max=30 (existing)                   |
|                                                    |
|  Main connection: EF DbContext (reads + EF ops)   |
|  Progress connection: out-of-band updates (exists)|
|  Write connections: N parallel (new)               |
|    acquired on demand from pool                   |
|    returned after each flush cycle                |
+--------------------------------------------------+
```

**Pool sizing consideration:** With N=4 write connections per task, and potentially 2-3 concurrent tasks, peak demand could reach ~15 connections. Current max pool (30) has headroom, but should be validated under load.

### 6. Configuration

| Setting | Source | Default | Description |
|---------|--------|---------|-------------|
| `JIM_WRITE_PARALLELISM` | Env var | `Environment.ProcessorCount` | Max concurrent write connections per task |
| `MaxExportParallelism` | Per-system setting | 1 | Export batch parallelism (existing) |

No new database settings or UI configuration needed. The write parallelism is a deployment-level tuning knob.

## Implementation Phases

### Phase 10a: Parallel Batch Writer

**Scope:** Create the reusable `ParallelBatchWriter` component in `SyncRepository`.

- Add `ParallelBatchWriter` helper (partitioning, connection acquisition, Task.WhenAll)
- Unit test with `InMemoryData.SyncRepository` (verify partitioning logic)
- Integration test with real PostgreSQL (verify multi-connection writes)
- Wire into `SyncRepository.CsOperations` for CSO bulk inserts

### Phase 10b: Import Write Parallelism

**Scope:** Parallelise the import create and update phases.

- Refactor `SyncImportTaskProcessor` create phase to use parallel batch writer
- Refactor update phase similarly
- Verify cross-batch reference fixup still works (runs after parallel creates complete)
- Verify cache updates still work (single-threaded after parallel writes)
- Benchmark: compare sequential vs. parallel import of 10K objects

### Phase 10c: Sync Flush Parallelism

**Scope:** Parallelise the page flush sequence in sync processors.

- Refactor `SyncTaskProcessorBase` flush methods to use parallel batch writer
- Implement dependency graph (MVOs first, then export eval, then obsolete cleanup)
- Partition independent operations across connections
- Verify two-pass CSO processing remains sequential
- Benchmark: compare sequential vs. parallel sync of 10K objects

### Phase 10d: COPY Binary Extension

**Scope:** Convert remaining parameterised INSERT methods to COPY binary protocol.

- Convert CSO bulk create from parameterised INSERT to `NpgsqlBinaryImporter` (COPY)
- Convert CSO bulk update similarly (or keep as UPDATE if COPY doesn't apply)
- Convert MVO bulk operations to COPY where applicable
- Benchmark: compare parameterised INSERT vs. COPY binary

### Phase 10e: Validation and Tuning

**Scope:** End-to-end validation, tuning, and documentation.

- Integration test suite: Scenarios 1, 8, 9 at MediumLarge scale
- Measure CPU utilisation across all cores during bulk writes
- Tune default parallelism and batch sizes
- Update worker redesign plan with results
- Document configuration guidance

## Success Criteria

| Metric | Current | Target | How to Measure |
|--------|---------|--------|----------------|
| Import throughput (10K objects) | Baseline | 2x+ | Integration test timing |
| Sync throughput (10K objects) | Baseline | 2x+ | Integration test timing |
| CPU utilisation during writes | ~25% (1 of 4 cores) | ~80%+ | `htop` during integration test |
| PostgreSQL connections during bulk write | 1 | N (configurable) | `pg_stat_activity` during test |
| Test pass rate | 100% | 100% | `dotnet test JIM.sln` |

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Connection pool exhaustion under concurrent tasks | Medium | High | Cap write parallelism per task; monitor `pg_stat_activity`; increase pool if needed |
| Transaction isolation conflicts (deadlocks) | Low | Medium | Each parallel batch writes to non-overlapping ID ranges; no cross-batch FK dependencies during bulk insert |
| Ordering violation in sync CSO processing | Low | Critical | CSO processing loop stays sequential; only the *flush* phase parallelises |
| RPEI ID generation conflicts | Low | High | Pre-generate all IDs single-threaded before dispatching to parallel writers (existing pattern) |
| Regression in data integrity | Low | Critical | All existing tests must pass; integration tests at MediumLarge scale; compare RPEI outcomes before/after |

## What This Does NOT Change

- Sync CSO processing loop remains sequential (ordering dependencies)
- Connector I/O remains sequential (connectors are not thread-safe)
- Worker remains a single process (horizontal scaling is Option C)
- Database schema unchanged
- Task/Activity model unchanged
- ISyncEngine interface unchanged
- ISyncRepository interface unchanged (new methods are internal to implementation)
