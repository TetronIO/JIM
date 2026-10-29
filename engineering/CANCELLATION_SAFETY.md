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

Export cancellation is handled at the **batch boundary** in `ExportExecutionServer`, with the Connector cooperating inside a batch:

- `ThrowIfCancellationRequested()` fires before each batch is read (`ReadNextImmediateBatchAsync`, and the deferred and parallel loops), so no new batch starts once cancellation is requested
- Within a batch: `MarkBatchAsExecutingAsync` -> `connector.ExportAsync` -> `ProcessBatchSuccessAsync`. The mark records each Pending Export's state before it (`ExecutingBatchMark.StatesBefore`) and the time it was marked
- The run's token is handed to the Connector, which observes it only **between** Pending Exports: it finishes a write it has started and reports each Pending Export it did not start as `ConnectedSystemExportResult.NotAttempted()` (the Connector contract is in [`developer/connectors.md`](developer/connectors.md), "Stopping when the run is cancelled", #2083)
- `ProcessBatchSuccessAsync` puts the not-attempted changes back as they were first (no attempt counted, no RPEI), then records the batch's results, and each new object's External ID with them, in one transaction **before** any further processing of those results that can fail (#2197). A failure in that later processing no longer loses or misreads a result the Connector returned
- A Connector that throws `OperationCanceledException` instead gives JIM no results: `ReleaseBatchAfterCancellationAsync` puts the whole batch back to its pre-mark state, so the next run sends it again
- Putting a batch back is guarded: `ReleaseExecutingPendingExportsAsync` writes only rows still `Executing` with the `LastAttemptedAt` the mark stamped, so a cancelled run still putting its batch back never overwrites a change the next run has already taken (#2197)
- Any other exception, including an `OperationCanceledException` nobody asked for (an HTTP client's timeout), is a failed attempt rather than a cancellation: `RecordBatchFailureAsync` records one failed attempt per change still `Executing`, keeping any result already recorded, so a Create whose success was recorded is never sent again (#2083)
- RPEIs are handed on per batch via `batchCompletedCallback`; when the run stops early, `HandOnProcessedExportItemsAsync` hands on the items still held, so the batches already written keep their execution items
- `OperationCanceledException` is caught cleanly in `SyncExportTaskProcessor`

This means:
- Completed batches are always fully persisted (connector results + RPEIs)
- The current batch stops between Pending Exports (or completes, for a Connector that finishes the batch regardless) before cancellation takes effect; the LDAP Connector finishes the batch, the SQL and SCIM Connectors stop between objects
- No export is left `Executing` by a cancelled or failed run. Only a Worker that stops part-way through a batch (a crash, a restart, or the memory guard's abort below) can strand one, and `RecoverStrandedExecutingPendingExportsAsync` resolves those at the next Worker start: to `Exported` where something was already sent (the confirming import then reconciles it), otherwise back to `Pending`
- A cancelled or failed export is attempted again at the next export run; Pending Exports have no retry limit or backoff (#2119)

### Import Operations

No special cancellation handling needed:

- Imports accumulate all CSO creates/updates in memory
- Persistence happens only after every page has been read, in batches of `ImportBatchSize`, with no cancellation check between those batches: once persistence starts it runs to completion
- If cancellation fires mid-import, unpersisted in-memory state is discarded cleanly: the processor checks before and after each connector page, and again after deletion detection, and skips deletions, reference resolution and persistence if cancellation was requested
- One checkpoint sits after persistence: a cancellation there skips confirming-import reconciliation of Pending Exports, and the persisted CSO changes stand
- Activity progress updates are idempotent and don't affect data integrity
- The cancellation token is passed to connectors for network/file I/O responsiveness

### Password Delivery

The Password Delivery Service is not a Worker Task, so it has no cancellation request of its own. It runs as a second hosted service in the Worker process and stops with the process's shutdown token. Its safety rests on how it claims work rather than on how it stops:

- **Claims are leases, not locks.** A delivery pass claims due queue rows with one `FOR UPDATE SKIP LOCKED` statement that marks them `Delivering` and stamps `ClaimedAt` / `ClaimedBy`. A claim is honoured for `PendingPasswordChange.ClaimLease` (60 seconds) and then ignored, so a deliverer that dies or is stopped mid-flight cannot strand a row in `Delivering`; the change becomes claimable again about a minute later.
- **Outcome writes are guarded.** A deliverer's attempt write only applies while the row is still `Delivering`, so a row taken away in the meantime (cancelled, retried, or superseded by a newer password) keeps that outcome rather than having it overwritten by the attempt.
- **Administrator cancellation is an outcome.** Cancelling a queued change (queue page, REST API or `Stop-JIMPendingPasswordChange`) records it as `Cancelled`, with who and when, rather than deleting it, because the person's password stays divergent in that system either way. Cancelling mid-delivery wins: the row stays cancelled unless the password actually landed, in which case the row is deleted as delivered. A cancelled change can be put back on the queue by retrying it.

### The Worker's Memory Guard

The Worker also cancels work itself, when memory is nearly exhausted (#2487). `Worker.GuardMemoryAsync` reads memory every second while tasks run (`HostMemoryProbe`: the cgroup limit where one is set, otherwise `/proc/meminfo`), and `WorkerMemoryGuard` decides on the thresholds in `MemoryPressure`:

- **85% in use**: a warning is noted on each running task and recorded on its Activity when it ends; nothing is cancelled.
- **92% in use, with the Worker holding more than half of what is in use**: each running task's `CancellationTokenSource` is cancelled, exactly as an administrator's cancellation is, so every guarantee on this page applies (the sync flush pipeline completes, the export batch stops between Pending Exports, the watermark is not advanced). The Activity is then failed with a `MemoryExhaustedException` naming the figures and the remedy, rather than recorded as cancelled, because nobody asked for the cancellation.
- **After a stop, 97% in use, or still 92% thirty seconds later (the Worker still holding most of it)**: the guard records why on each running task's Activity and ends the process, rather than wait to be killed without a word. This is a crash for the purposes of this page: the container restarts the Worker, and startup recovery (stale task recovery, `RecoverStrandedExecutingPendingExportsAsync`) applies.

Before stopping or aborting, the guard writes the heartbeat at once with the reading that made it act, so startup recovery can say the Worker most likely ran out of memory even if the process is killed within the second.

## Recovery Procedures

**Full sync is the universal recovery path.** If a cancelled sync left any inconsistent state (which should not happen after the #339 fix, but could occur from a process crash), running a full sync will:

1. Re-process all CSOs from scratch (a Full Synchronisation evaluates every Connected System Object on every run, changed or not, #891)
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
