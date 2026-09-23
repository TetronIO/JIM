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

Objects already evaluated within the current page have accumulated in-memory batch collections (pending MVO creates/updates, Pending Exports, RPEIs, etc.). These must be flushed to the database before exiting. This is bounded work -- at most one page of objects (typically 100-500).

Without this flush, the database can be left in an inconsistent state where MVOs exist without corresponding Pending Exports, causing target systems to silently miss updates.

## Risk Windows by Operation Type

### Sync Operations (Full Sync, Delta Sync)

The sync page pipeline has 9 sequential persistence calls (`SyncFullSyncTaskProcessor` and `SyncDeltaSyncTaskProcessor`):

```
1. PersistPendingMetaverseObjectsAsync    -- saves MVO creates/updates
2. CreatePendingMvoChangeObjectsAsync     -- builds MVO change history
3. EvaluatePendingExportsAsync            -- evaluates export rules
4. FlushPendingExportOperationsAsync      -- saves Pending Exports
5. ResolvePendingExportReferenceSnapshotsAsync -- resolves deferred refs
6. FlushObsoleteCsoOperationsAsync        -- deletes obsolete CSOs
7. FlushPendingMvoDeletionsAsync          -- deletes 0-grace-period MVOs
8. FlushRpeisAsync                        -- bulk inserts RPEIs
9. FlushPendingMvoChangesAsync            -- persists MVO change records
```

After step 9 the page's tracking state is cleared (the EF change tracker and the page's Metaverse Object identity map), so nothing from a flushed page is carried into the next.

**Highest-risk window** (before #339 fix): If a crash or cancellation occurs after step 1 but before step 4, MVOs are updated but no Pending Exports are created. Target systems silently miss the update, and a subsequent sync won't regenerate exports because CSO attributes haven't changed.

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
- If cancellation fires mid-import, unpersisted in-memory state is discarded cleanly: the processor checks before and after each connector page, and again after deletion detection, and skips deletions, reference resolution and persistence if cancellation was requested
- One checkpoint sits after persistence: a cancellation there skips confirming-import reconciliation of Pending Exports, and the persisted CSO changes stand
- Activity progress updates are idempotent and don't affect data integrity
- The cancellation token is passed to connectors for network/file I/O responsiveness

### Password Delivery

The Password Delivery Service is not a Worker Task, so it has no cancellation request of its own. It runs as a second hosted service in the Worker process and stops with the process's shutdown token. Its safety rests on how it claims work rather than on how it stops:

- **Claims are leases, not locks.** A delivery pass claims due queue rows with one `FOR UPDATE SKIP LOCKED` statement that marks them `Delivering` and stamps `ClaimedAt` / `ClaimedBy`. A claim is honoured for `PendingPasswordChange.ClaimLease` (60 seconds) and then ignored, so a deliverer that dies or is stopped mid-flight cannot strand a row in `Delivering`; the change becomes claimable again about a minute later.
- **Outcome writes are guarded.** A deliverer's attempt write only applies while the row is still `Delivering`, so a row taken away in the meantime (cancelled, retried, or superseded by a newer password) keeps that outcome rather than having it overwritten by the attempt.
- **Administrator cancellation is an outcome.** Cancelling a queued change (queue page, REST API or `Stop-JIMPendingPasswordChange`) records it as `Cancelled`, with who and when, rather than deleting it, because the person's password stays divergent in that system either way. Cancelling mid-delivery wins: the row stays cancelled unless the password actually landed, in which case the row is deleted as delivered. A cancelled change can be put back on the queue by retrying it.

## Recovery Procedures

**Full sync is the universal recovery path.** If a cancelled sync left any inconsistent state (which should not happen after the #339 fix, but could occur from a process crash), running a full sync will:

1. Re-process all CSOs from scratch
2. Re-evaluate all export rules
3. Regenerate any missing Pending Exports
4. Set the watermark correctly

For delta sync specifically: since the watermark is not updated on cancellation, the next delta sync will re-process all objects modified since the last successful sync.

## Design Rationale: Why Not Database Transactions?

We chose graceful cancellation (Option B) over wrapping the flush pipeline in a database transaction (Option A) because:

1. **Bounded extra work**: The flush pipeline for one page takes milliseconds. The admin sees cancellation within one CSO evaluation cycle plus flush time.
2. **No transaction overhead**: PostgreSQL transactions on large batch operations add significant memory and WAL pressure. The flush pipeline writes thousands of rows across multiple tables.
3. **Simpler error handling**: Transaction rollback across multiple raw SQL operations (COPY binary, bulk inserts) would require careful savepoint management.
4. **EF Core compatibility**: The flush pipeline mixes EF Core operations with raw SQL. Coordinating transactions across both would add complexity.
5. **Idempotent recovery**: Full sync as a recovery path means we don't need perfect rollback -- we just need to avoid advancing the watermark.
