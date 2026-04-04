# Cancellation Safety

> How JIM handles task cancellation without corrupting data.

## Architecture Overview

JIM uses a **polling-based cancellation model**:

1. An admin requests cancellation via the UI (or API)
2. `RequestWorkerTaskCancellationAsync` sets the task status to `CancellationRequested`
3. The Worker's main loop polls `GetWorkerTasksThatNeedCancellingAsync` every ~2 seconds
4. On match, the Worker calls `CancellationTokenSource.Cancel()` on the task's CTS
5. The processor detects `IsCancellationRequested` at the next check point and exits gracefully

```
Admin UI --> RequestWorkerTaskCancellationAsync
                 |
                 v
         WorkerTask.Status = CancellationRequested
                 |
                 v
         Worker.ExecuteAsync (polling loop, ~2s cycle)
                 |
                 v
         CancellationTokenSource.Cancel()
                 |
                 v
         Processor detects cancellation --> flush --> exit
                 |
                 v
         CancelWorkerTaskAsync (activity cancelled, task deleted)
```

## Two-Level Cancellation Model

### Level 1: Stop Processing New Objects

When cancellation is detected inside the CSO processing loop, the processor immediately stops evaluating new objects. This honours the admin's intent to stop making changes as soon as possible.

### Level 2: Flush Already-Processed Objects

Objects already evaluated within the current page have accumulated in-memory batch collections (pending MVO creates/updates, pending exports, RPEIs, etc.). These must be flushed to the database before exiting. This is bounded work -- at most one page of objects (typically 100-500).

Without this flush, the database can be left in an inconsistent state where MVOs exist without corresponding pending exports, causing target systems to silently miss updates.

## Risk Windows by Operation Type

### Sync Operations (Full Sync, Delta Sync)

The sync page pipeline has 8 sequential persistence calls:

```
1. PersistPendingMetaverseObjectsAsync    -- saves MVO creates/updates
2. CreatePendingMvoChangeObjectsAsync     -- saves MVO change history
3. EvaluatePendingExportsAsync            -- evaluates export rules
4. FlushPendingExportOperationsAsync      -- saves pending exports
5. ResolvePendingExportReferenceSnapshotsAsync -- resolves deferred refs
6. FlushObsoleteCsoOperationsAsync        -- deletes obsolete CSOs
7. FlushPendingMvoDeletionsAsync          -- deletes 0-grace-period MVOs
8. FlushRpeisAsync                        -- bulk inserts RPEIs
```

**Highest-risk window** (before #339 fix): If a crash or cancellation occurs after step 1 but before step 4, MVOs are updated but no pending exports are created. Target systems silently miss the update, and a subsequent sync won't regenerate exports because CSO attributes haven't changed.

**After the fix**: The flush pipeline always runs to completion for objects already processed on the current page. Cancellation only takes effect after the flush, and only prevents advancing to the next page.

**Watermark safety**: On cancellation, `UpdateDeltaSyncWatermarkAsync` is NOT called. This ensures the next sync re-processes from the same starting point.

### Export Operations

Export cancellation is handled at the **batch boundary** in `ExportExecutionServer`:

- `ThrowIfCancellationRequested()` fires at the start of each batch loop iteration
- Within a batch: `MarkBatchAsExecutingAsync` -> `connector.ExportAsync` -> `ProcessBatchSuccessAsync` runs atomically
- RPEIs are persisted per-batch via `batchCompletedCallback`
- `OperationCanceledException` is caught cleanly in `SyncExportTaskProcessor`

This means:
- Completed batches are always fully persisted (connector results + RPEIs)
- The current batch completes before cancellation takes effect
- No exports are left in `Executing` status from completed batches

### Import Operations

No special cancellation handling needed:

- Imports accumulate all CSO creates/updates in memory
- Persistence happens in a single batch after all pages
- If cancellation fires mid-import, unpersisted in-memory state is discarded cleanly
- Activity progress updates are idempotent and don't affect data integrity
- The cancellation token is passed to connectors for network/file I/O responsiveness

## Recovery Procedures

**Full sync is the universal recovery path.** If a cancelled sync left any inconsistent state (which should not happen after the #339 fix, but could occur from a process crash), running a full sync will:

1. Re-process all CSOs from scratch
2. Re-evaluate all export rules
3. Regenerate any missing pending exports
4. Set the watermark correctly

For delta sync specifically: since the watermark is not updated on cancellation, the next delta sync will re-process all objects modified since the last successful sync.

## Design Rationale: Why Not Database Transactions?

We chose graceful cancellation (Option B) over wrapping the flush pipeline in a database transaction (Option A) because:

1. **Bounded extra work**: The flush pipeline for one page takes milliseconds. The admin sees cancellation within one CSO evaluation cycle plus flush time.
2. **No transaction overhead**: PostgreSQL transactions on large batch operations add significant memory and WAL pressure. The flush pipeline writes thousands of rows across multiple tables.
3. **Simpler error handling**: Transaction rollback across multiple raw SQL operations (COPY binary, bulk inserts) would require careful savepoint management.
4. **EF Core compatibility**: The flush pipeline mixes EF Core operations with raw SQL. Coordinating transactions across both would add complexity.
5. **Idempotent recovery**: Full sync as a recovery path means we don't need perfect rollback -- we just need to avoid advancing the watermark.
