# Overlapped Synchronisation History Writes

- **Status:** Done
- **Note:** Phase 4's integration scenarios (001, 004, 005, 008, 023, 026, 027 and 032) pass; the 500k scale measurement is taken on the next #2669 run. Phase 5 (overlapping the cross-page batches, or a later wait point) is not started and is only taken up if that run shows significant time waiting on history writes.
- **Issue:** [#2874](https://github.com/TetronIO/JIM/issues/2874) (blocked by [#2872](https://github.com/TetronIO/JIM/issues/2872))

## Overview

During Full and Delta Synchronisation, each page's history is currently written in sequence after its changes are saved. That history is the Run Profile Execution Items, their outcomes, the Connected System Object and Metaverse Object change records, causal edges, match candidates and stat counters. This plan prepares the history on the main thread, hands it to a writer on a dedicated database connection, and lets the next page load and process while it is written. The run waits for the write before saving the next page's changes, so at most one write is ever in flight. A failed write fails the run exactly as a failed flush does today.

Changes to synchronisation state stay on the main path and in their current order. Only history overlaps.

## Business Value

- 500k-object initial load (Source Full Synchronisation, 883 s): FlushRpeis plus FlushPendingMvoChanges take 255 s of serial time per run, and the next page's load and processing take about 179 s. Expected saving is roughly 100 to 170 s per large Full Synchronisation, less database contention. Delta Synchronisation saves in proportion to its page count.
- Each page's history is written in one transaction, so a failed write leaves none of that page's history behind. Today a failure can leave Run Profile Execution Items without their Metaverse Object change records.
- New timing spans show the real overlap on the next scale run.

## Technical Architecture

### Current state (verified against `main` at 1651b88e0; line references refreshed at 5d1d53f4b, before Phase 1)

Page loop, identical in both processors: `SyncFullSyncTaskProcessor.cs:323-370` and `SyncDeltaSyncTaskProcessor.cs:336-378`:

```
LoadCsoPage -> Pass 1 / Pass 2 (ProcessCsoLoop) -> ProcessDeferredReferenceAttributes
PersistPendingMetaverseObjectsAsync -> generated value flushes -> CreatePendingMvoChangeObjectsAsync
-> EvaluateQueuedDriftAsync -> EvaluatePendingExportsAsync -> FlushPendingExportOperationsAsync
-> ResolvePendingExportReferenceSnapshotsAsync -> FlushObsoleteCsoOperationsAsync -> FlushPendingMvoDeletionsAsync
-> FlushRpeisAsync -> FlushPendingMvoChangesAsync -> ClearPageTrackingState -> UpdateActivityMessageAsync
```

Every write on the history path, with evidence:

| Step | Where | Connection and transaction |
|---|---|---|
| `PrepareRpeisForPersistence`: ids, display snapshots, outcome target ids read from `rpei.ConnectedSystemObject.MetaverseObject`, outcome summaries | `SyncTaskProcessorBase.cs:1100` | in memory, reads the live page graph |
| `BulkInsertRpeisAsync`: Run Profile Execution Item COPY; `BulkUpdateRpeiFieldsRawAsync` for rows the deletion already wrote (#2807); outcome Connected System Object change snapshots via `InsertCsoChangeHierarchyRawAsync` (3 COPYs plus a SELECT of existing Connected System Object ids, `RpeiOperations.cs:342/722`); outcome COPY; causal edges and match candidates (multi-row INSERT via `_context.Database.ExecuteSqlRawAsync`, `:979`, `:1047`); stat counter upsert | `SyncRepository.RpeiOperations.cs:37-289` | EF connection, own transaction on the single-connection path; parallel only at 20,000 or more rows and never inside a caller's transaction (#2872) |
| id assignment inside the writer: `FlattenSyncOutcomes` (`:1271`), `CausalEdge.ResolveTransientReferences` (`:74-80`), `CollectMatchCandidates` (`:987`), `ActivityStatCounterCalculator.CalculateRpeiInsertDeltas` reading `rpei.ConnectedSystemObject?.Type?.Name` (`ActivityStatCounterCalculator.cs:41`) | as above | mutates and reads the page graph |
| `PersistRpeiCsoChangesAsync`: provisioning "Added" records, excluding items written with their deletion | `SyncTaskProcessorBase.cs:1053-1064`, `RpeiOperations.cs:372-429` | EF connection, a second transaction |
| `AccumulateActivitySummaryStats`, `RunProfileExecutionItems.Clear`, `DetachRpeisFromChangeTracker`, `_rpeiIdsWrittenWithDeletion.ExceptWith`, clearing `_mvoIdToRpei`, `_mvoIdsChangedByExportEvaluation`, `_deferredMvoRpeiMappings` | `SyncTaskProcessorBase.cs:1066-1091` | main thread |
| `FlushPendingMvoChangesAsync` -> `PersistPendingMvoChangesAsync`: three COPYs (changes, attributes, values) in one transaction | `SyncTaskProcessorBase.cs:7046`, `SyncRepository.MvoChangeOperations.cs:16-104` | EF connection |

Other callers of the same two flushes: cross-page pass (`:5116`, `:5187-5190`, plus `BulkUpdateRpeiOutcomesAsync` at `:5134`), scope review (`:5736`), generated value revisions (`:5919`), deferred recall items (`:6887`).

Foreign keys into history tables (`JimDbContextModelSnapshot.cs:5732-5866`, `:6682`): outcomes, match candidates, causal edges (effect only), Metaverse Object changes and Connected System Object changes point at Run Profile Execution Items; Run Profile Execution Items point at Activities. No state table (Metaverse Object, Connected System Object, Pending Export) has a foreign key to a history row, and history names objects by plain id (#2669). State therefore never depends on history being written. The only history read before the end of the run is `GetRpeisWithMvoChangeIdsForCrossPageMergeAsync` (`:4840`), at the start of the cross-page pass.

Database writes during Pass 1 and Pass 2: an audit of every `_syncRepo`/`_syncServer` write call in `SyncTaskProcessorBase.cs` puts Connected System Object deletions (`:6065`, `:6105`, `:6228`, `:6933`), Pending Export writes and marks inside the flush methods. Only out-of-band progress updates and unique-value sequence block reservations run mid-page.

### Which writes move to the background, and which stay

| Moves to the background writer (dedicated connection, one transaction per batch) | Stays on the main thread |
|---|---|
| Run Profile Execution Item rows; the field update for rows the deletion already wrote | `PrepareRpeisForPersistence` (reads live Metaverse Objects) |
| Outcome rows | The new `PrepareSynchronisationHistory` step (all id assignment and graph reads, below) |
| Outcome Connected System Object change snapshots, including the existing-Connected-System-Object lookup | Every state write: Metaverse Object create and update, joins, generated values, drift, export evaluation, `FlushPendingExportOperationsAsync`, the snapshot resolution, Connected System Object and Metaverse Object deletion (the deletion path keeps writing its own Run Profile Execution Item rows and Deleted records inside its own transaction, `SyncServer.cs:359`, `:226`) |
| Provisioning "Added" change records | `AccumulateActivitySummaryStats`, now applied only after a successful write |
| Causal edges, match candidates, stat counter upsert | Clearing and detaching the Activity's items, clearing the page lookups, `ClearPageTrackingState` |
| Metaverse Object change records and appends | `BulkUpdateRpeiOutcomesAsync` in the cross-page pass (updates rows from earlier pages, after the barrier) |

Why the existing-Connected-System-Object lookup can stay in the write: the run waits for the write before the next page's changes are saved, and this run deletes Connected System Objects only in its flush methods. The lookup therefore sees the same rows it sees today. Concurrent runs can already change the answer today.

### Proposed design

```
page N:   load -> process -> [BARRIER: wait for page N-1's write] -> save state (unchanged order)
          -> PrepareSynchronisationHistory (main) -> hand off to writer -> release -> clear tracker
          (page N's history is written on the dedicated connection)
page N+1: probe for a failed write (non-blocking) -> load -> process (overlaps page N's write)
          -> [BARRIER] -> save state -> ...
after the loop: [BARRIER: page loop exit] -> cross-page pass, recall items, scope review, revisions,
          watermark, configuration changes, completion message (each history flush is handed off and awaited)
```

**New types:**

- `JIM.Models/Activities/SynchronisationHistoryBatch.cs`. An immutable, fully prepared batch:
  - the Run Profile Execution Items;
  - the ids of rows the deletion already wrote;
  - the flattened outcomes;
  - the outcome Connected System Object change snapshots;
  - the item Connected System Object change records;
  - causal edges and match candidates;
  - new and appended Metaverse Object changes;
  - `IReadOnlyDictionary<ActivityStatCounterKey, long>` counter deltas;
  - the page number and total pages.
- `JIM.Models/Exceptions/SynchronisationHistoryPersistenceException.cs`, deriving from `SyncPersistenceException`. Its `BuildMessage(page, totalPages, connectedSystemName)` reads along the lines of: "Failed to record the execution items and change history of page N of T for Connected System 'X'. That page's synchronisation changes were saved; its history was not, and the run stopped before saving any further changes."
- `JIM.Worker/Processors/SynchronisationHistoryWriter.cs`, plus enums `SynchronisationHistoryWriteMode { Inline, Overlapped }` and `SynchronisationHistoryBarrier { PageStateFlush, PageLoopExit, AwaitedFlush, RunExit }` in one enum file.

**`ISyncRepository` additions** (PostgreSQL, in-memory and `ReadOnlySyncRepositoryGuard`):

- `SynchronisationHistoryBatch PrepareSynchronisationHistory(...)`. Synchronous and CPU-only, on the main repository. It:
  - assigns every id (outcomes with their item and parent ids, edges via `ResolveTransientReferences`, candidates, Connected System Object and Metaverse Object change hierarchies via `ChangeRecordIds.AssignMissing`);
  - copies navigation ids into scalars (`ConnectedSystemObjectId`, `ConnectedSystemObjectChange.ActivityRunProfileExecutionItemId`, `MetaverseObjectChange.ActivityRunProfileExecutionItemId`);
  - computes the counter deltas;
  - empties the items' edge and candidate buffers, since the batch now owns them;
  - applies the existing filter that excludes items written with their deletion from `PersistRpeiCsoChangesAsync`.
- `Task<bool> WriteSynchronisationHistoryAsync(SynchronisationHistoryBatch batch)`. It begins a transaction on its own context, then writes items, field updates, outcome change snapshots, outcomes, edges, candidates and counters. It then writes item change records and Metaverse Object changes, then commits.
  - Because it holds a transaction, #2872's `ShouldWriteInParallel` keeps it on one connection.
  - It assigns no ids; it throws `InvalidOperationException` on any `Guid.Empty`.
  - Refactor `BulkInsertRpeisAsync` into collect and write halves so imports and exports keep using it unchanged; no new column lists.
- `bool SupportsConcurrentHistoryWrites`: true for PostgreSQL, false in memory (its stores are plain `Dictionary`s, `JIM.InMemoryData/SyncRepository.cs:36-51`). The guard passes it through and blocks `Write` as it blocks other writes.
- `bool HasOpenTransaction`. The processor asserts it is false at every hand-off. An awaited write on another connection could otherwise wait forever on rows the main connection holds uncommitted. No current call site holds a transaction (there are no `BeginTransactionAsync` calls in the processors), so this guards against a future regression.

**`SynchronisationHistoryWriter`.** Created once per run in `PerformFullSyncAsync` and `PerformDeltaSyncAsync`. It opens `_syncRepo.OpenIsolatedScope()` (`SyncRepository.cs:1006`) and owns and disposes that scope.

- `HandOffAsync(batch)`:
  - throws if a write is in flight (at most one) or the writer has already faulted.
  - Overlapped mode: starts `Task.Run(() => scope.Repository.WriteSynchronisationHistoryAsync(batch))` with `CancellationToken.None`. That page's changes are already saved, so its history must be completed, not cancelled.
  - Inline mode: awaits the same call immediately.
- `WaitAsync(barrier)`: awaits the in-flight write. On a fault it throws `SynchronisationHistoryPersistenceException` (page and inner exception from the batch) and stays faulted, so every later `HandOffAsync` or `WaitAsync` throws the same.
- `ThrowIfFaulted()`: non-blocking.
- `DrainOnExitAsync(Exception? primary)`: always awaits whatever is in flight.
- Totals for the end-of-run log: batches, write ms, wait ms.

**Processor changes (`SyncTaskProcessorBase`):**

- `FlushRpeisAsync` and `FlushPendingMvoChangesAsync` merge into `FlushHistoryAsync(SynchronisationHistoryFlush flush)`, with `Overlap` for the two page loops and `AwaitWrite` for every other caller. One prepare and one write path, so no dual code paths. Order inside it:
  1. `PrepareRpeisForPersistence`;
  2. `PrepareSynchronisationHistory`;
  3. release on the main thread (clear the Activity's items, detach them from the tracker, the deletion-row bookkeeping, the page lookups, `_hasRawSqlSupport = true`);
  4. in Overlapped mode, cut the batch's navigations into the live graph (`rpei.ConnectedSystemObject = null` once the id is copied; the same for change records) so page N's hydrated objects can be collected;
  5. hand off.
- In Inline mode on the in-memory store, the returned `usedRawSql` drives the existing "keep items on the Activity for test assertions" branch, as today.
- `Worker.AccumulateActivitySummaryStats(_activity, batch.Rpeis)` runs at the barrier after a successful write. A failed page is then never counted, as today (`Worker.cs:1788` reads scalars only).
- Mode: `Overlapped` when `_syncRepo.SupportsConcurrentHistoryWrites`, else `Inline`, with an internal test override `HistoryWriteModeForTests`. Every existing PostgreSQL workflow test therefore exercises the production mode.

**Barriers, all on the main thread:**

1. **Page state flush:** the first statement of each page's `try`, before `PersistPendingMetaverseObjectsAsync` (Full `:323`, Delta `:336`). This barrier guarantees at most one write in flight, keeps the background write away from every state write and from the parallel bulk writers (so pool use is at most 1 main + 1 history during the overlap), and makes failure semantics identical to today.
2. **Fault probe:** `ThrowIfFaulted()` before each `LoadCsoPage`, so a run whose write has already failed loads nothing more.
3. **Page loop exit:** awaited after the loop on both normal end and cancellation, before the cross-page pass (which reads Metaverse Object change ids from the database, `:4840`), recall items, scope review, revisions, `UpdateActivityAsync`, the watermark, `ApplyClaimedConfigurationChangesAsync`, the completion message and the summary stats fallback (Full `:434-483`, Delta `:432-480`).
4. **Awaited flushes:** every flush outside the page loops hands off and awaits.
5. **Run exit:** `PerformFullSyncAsync`/`PerformDeltaSyncAsync` call `DrainOnExitAsync` before their existing `finally` work. Activity completion (`Worker.cs:465`, which queries Run Profile Execution Item error counts) and failure recording (`FailActivityWithErrorAsync`, which finalises counters from the database, `ActivityServer.cs:353-371`) therefore only ever see a drained writer.
6. **Worker shutdown and memory-guard Stop:** both cancel the run's token, so the page flushes, breaks and drains through barrier 3, as today. A memory-guard Abort or process kill drops the in-flight write; see Failure.

**Data ownership rules** (written in `PrepareSynchronisationHistory`'s XML doc and enforced by tests):

- After hand-off the main thread never touches the batch's objects until the barrier.
- The writer never reads a live Connected System Object or Metaverse Object graph and never mutates an object outside the batch.
- Shared run-scope configuration (attributes, Synchronisation Rules, Object Types) is read-only, so id reads of it are safe.
- Every id the next page or the end of the run needs is assigned in prepare. That covers outcome ids that a later `CausalCause` (deferred recall, `_deferredRecallCausesByCsoId`) resolves, and item ids the deletion path stamps. They are available without waiting.
- Metaverse Object change ids for the cross-page merge are still read from the database, after barrier 3.

**Locking:** history inserts take KEY SHARE locks on Run Profile Execution Items, the Activity and static configuration. The main path's Activity updates change no key columns, so the two never conflict. Counter upserts are already ordered by key (`ActivityStatCounterWriter.cs`). Under barrier 1 no state write runs concurrently with the write.

### Failure

- A background failure (any exception, including connection loss or timeout) rolls back that batch's single transaction, so none of page N's history persists.
- It surfaces at the next barrier or probe as `SynchronisationHistoryPersistenceException` with page N. The page loop's catch already excludes `SyncPersistenceException` (Full `:394`, Delta `:396`), so the page is not misattributed.
- The exception propagates to `Worker.cs:486-491` (`SafeFailActivityAsync`): the Activity becomes FailedWithError with the message chain and stack, and counters are finalised from the rows that exist. Nothing is caught and dropped.
- `DrainOnExitAsync` combinations:
  - no primary exception: throw the history failure;
  - primary is `OperationCanceledException`: throw the history failure (the run lost history, so it fails rather than cancels);
  - any other primary: rethrow the primary unchanged (`ExceptionDispatchInfo`), keeping its structured detail, append the history failure to `_activity.WarningMessage` (the mechanism `ReportGeneratedValuesWaiting` uses, `:657`), and log it at Error.
- **Partial state:**
  - Page N's changes are saved and its history is not. That is exactly what today's failure in `FlushRpeisAsync`, after its changes were saved, leaves.
  - Page N+1 was processed in memory only, because barrier 1 precedes its save. Its mid-page writes are limited to sequence reservations (gaps only) and progress updates.
  - The Delta watermark and configuration-change apply are not reached, as today.
  - The difference: a crash, as opposed to an exception, can now lose page N's history across page N+1's load and processing as well as across the flush itself. It is the same kind of remnant as a crash between today's separately committed flush steps, and startup recovery handles the Activity.

### Scope

- **Full and Delta Synchronisation:** both page loops, the cross-page pass, recall items, scope review and generated value revisions.
- **The deletion path is unchanged:** `FlushObsoleteCsoOperationsAsync` still writes its items and Deleted records in its own transaction before the hand-off, and the batch carries the "already written" ids for the field update.
- **Out of scope:**
  - `SyncImportTaskProcessor` and `SyncExportTaskProcessor`. They call `BulkInsertRpeisAsync` and `PersistCsoChangesAsync` directly, and are touched only by the behaviour-preserving split.
  - The Sync Preview, which does not run these processors.
  - Overlapping the cross-page batches (Phase 5, conditional).

### Observability

- On the main thread:
  - `SyncHistoryPrepare` span (tags: page, rpeiCount, outcomeCount, csoChangeValueCount, mvoChangeValueCount);
  - `SyncHistoryWait` span (tags: barrier, page, writeWasRunning, waitedMs).
- In the background, `SyncHistoryWrite` span (tags: page, mode, rows per table), started after the prepare span is disposed so its parent is the run-level span (`ProcessConnectedSystemObjects`, `ResolveCrossPageReferences` and so on). Its children:
  - the existing `MvoChange.Persist.*` spans;
  - new `RpeiHistory.Rows`, `.Outcomes`, `.CsoChanges`, `.Edges`, `.Candidates`, `.Counters` and `.Commit` spans on the `JIM.PostgresData.Sync` source.
- `SpanBreakdown` already tolerates children summing to more than their parent.
- End-of-run Information log: batches, total write seconds, total wait seconds, hidden seconds (write minus wait).
- Check that `Show-PerformanceTree.ps1` and the results JSON (`Name`/`Parent`) place the background span correctly. Old baselines carry `FlushRpeis`/`FlushPendingMvoChanges`; compare them against `SyncHistoryPrepare + SyncHistoryWait` (time on the main path) and `SyncHistoryWrite` (total write).

### Kill switch

There is no configuration or environment toggle. `SynchronisationHistoryWriteMode` is chosen from the repository's capability, with an internal `HistoryWriteModeForTests` override. This is not a compatibility shim, for three reasons:

- Inline is required for the in-memory store, which is not thread-safe.
- Both modes run the same prepare and the same write on the same isolated connection; only when the run waits differs.
- The equivalence tests need a serial reference on the same database.

## Implementation Phases

Each phase is TDD (red, green, refactor), with `dotnet build JIM.sln` and `dotnet test JIM.sln` clean. Land after #2872.

### Phase 1: Prepared batch and single-transaction write (no behaviour change) ✅

1. Characterisation fixture first: `SynchronisationHistoryEquivalenceDatabaseTests` (RequiresPostgres).
   - Runs a seeded mixed set with page size 3 for Full and for Delta: projection, join, Attribute Flow, provisioning with reference snapshots, an obsolete Connected System Object with a 0-grace Metaverse Object deletion (deletion-written rows, recall items and causal edges), an intra-type cross-page reference, ambiguous match candidates, and a scope-review-flagged object.
   - Normalises every history table: ids replaced by natural keys, times dropped. It also captures the Activity totals, stat counters and status.
   - Commits the normalised output as the expected baseline, captured on today's code.
2. Red tests:
   - `SynchronisationHistoryPersistenceException.BuildMessage`.
   - `PrepareSynchronisationHistory_AssignsEveryIdAndScalarBeforeHandOff`.
   - `..._CounterDeltasEqualBulkInsertRpeisAsync`.
   - `..._DrainsEdgeAndCandidateBuffers`.
   - RequiresPostgres `WriteSynchronisationHistoryAsync` round trip, asserting every column of every table (pattern: `MvoChangeValuePersistenceDatabaseTests`).
   - Atomicity: a batch whose Metaverse Object change append names a missing parent leaves no row of the batch, items included.
   - Rows the deletion already wrote are updated, not inserted.
   - No parallel connection is used (`ParallelWritePathScope` counts zero).
3. Implement the model, the exception, the three repository members, the in-memory and guard implementations, and the `BulkInsertRpeisAsync` split.

**Acceptance:** existing repository, import and export tests unchanged and green; `BulkInsertColumnCompletenessTests` and `BulkSqlColumnConventionTests` green.

### Phase 2: Writer, and the processors on Inline mode ✅

1. Red `SynchronisationHistoryWriterTests`, using a fake isolated repository with `TaskCompletionSource`:
   - `HandOffAsync_WhileWriteInFlight_Throws`;
   - writes happen in hand-off order;
   - `WaitAsync` awaits completion;
   - a fault throws `SynchronisationHistoryPersistenceException` naming the page, and the fault is sticky;
   - `ThrowIfFaulted` never blocks;
   - cancelling the run's token does not cancel the write;
   - `DrainOnExitAsync` covers the three exit combinations;
   - Inline mode awaits before returning;
   - spans are emitted with their tags (via an `ActivityListener`);
   - dispose awaits the in-flight write and releases the scope.
2. Replace both flushes with `FlushHistoryAsync`, forced to `AwaitWrite` everywhere.

**Acceptance:** the whole suite, including every RequiresPostgres workflow test, is green, and the Phase 1 fixture matches its baseline.

### As built in Phases 1 and 2

Where the code differs from the design above, and why:

- **The fixture** is `SynchronisationHistoryEquivalenceDatabaseTests`, rendering through `SynchronisationHistorySnapshot` (`test/JIM.Worker.Tests/Support/`); its baselines (`Workflows/Baselines/`) were captured on 5d1d53f4b before any production change. It reads every column (`SELECT *`), so a later column is characterised too, and a second test proves two identical runs render identically with no unlabelled id. Full and Delta record the same history; only the Activities' counts differ.
- **A failure found at the flush that handed it off propagates as it is.** `WaitAsync(AwaitedFlush)` rethrows the write's own exception; only a failure found at another barrier, a probe or the drain is a `SynchronisationHistoryPersistenceException`. An awaited flush fails where it ran, so the code around it already attributes it (the page loop's `SyncPersistenceException`, or the Worker's handler); wrapping it would name a page for a flush outside the page loop. It also keeps Phase 2's failure output what it was. The failure is still kept, and every later call throws the wrapped form.
- **`BuildMessage` takes page 0** for history written outside the page loop, and says "the steps after the pages" there.
- **The writer opens its own scope** from the run's repository (`new SynchronisationHistoryWriter(runRepository, mode, activity, connectedSystemName)`), and **the hand-off guard is in the writer, in Phase 2**: an Inline write on the isolated connection would wait for ever on uncommitted rows just as an overlapped one would.
- **`FlushHistoryAsync(page, totalPages)` has no flush-kind parameter yet**: every caller waits, so Phase 3 adds the overlapped variant for the page loops. As `FlushRpeisAsync` did, a flush with no items leaves the page lookups alone.
- **Merging the flushes changes no output**: `CreatePendingMvoChangeObjectsAsync` runs in three places (each page loop and each cross-page batch), each followed by a flush that wrote Metaverse Object changes before, so the queues are always empty at the sites that called only `FlushRpeisAsync`.
- **The in-memory store's prepare copies no navigation id into a scalar** (its cross-page lookup keys on `MetaverseObjectChange.ActivityRunProfileExecutionItemId`, so copying would change what in-memory tests see) and computes no counter deltas (it keeps none). It does now give change records ids, which the batch's id check requires.
- **The id check is `SynchronisationHistoryBatch.RequireEveryIdAssigned`**, shared by both stores.
- **Spans:** `SyncHistoryPrepare` (processor), `SyncHistoryWrite` and `SyncHistoryWait` (writer), and `RpeiHistory.Rows`, `.CsoChanges`, `.Outcomes`, `.Edges`, `.Candidates`, `.Counters`, `.ItemCsoChanges` and `.Commit` (repository), beside the existing `MvoChange.Persist.*`. The page loops' `FlushRpeis` and `FlushPendingMvoChanges` spans are now one `FlushHistory` span.
- **Foreign keys the write meets**, beyond those listed under Current state: `MetaverseObjectChanges.ActivityId` (Activities), `SyncRuleId` and `ContributedBySyncRuleId` (Synchronisation Rules), the change attribute rows' `AttributeId`, and `DeletedObjectTypeId`. All point at the Activity or configuration, as the locking section assumes.

### Phase 3: Overlap in the page loops ✅

1. Red tests:
   - The fixture asserts that Overlapped output equals the baseline and equals Inline output.
   - `PageLoop_NextPageLoadsBeforePreviousHistoryCommits`: a gated isolated repository proves the overlap.
   - `PageLoop_StateFlushWaitsForPreviousHistory`: no Metaverse Object row of page N+1 exists while page N's write is gated.
   - Fault injection: a test-database trigger raises on a marker value in `MetaverseObjectChangeAttributeValues` on page k. Assert:
     - the run throws `SynchronisationHistoryPersistenceException` (Page = k);
     - the Activity is FailedWithError with that message;
     - there is no history for page k, and complete history for pages before it;
     - state exists for pages up to k and not after;
     - totals exclude page k;
     - no Delta watermark is advanced;
     - no task is left running, and there is no unobserved task exception (subscribe to `TaskScheduler.UnobservedTaskException` and force a garbage collection).
   - Cancellation while page k's write is gated: the run waits, page k's history is complete, the run ends as cancelled.
   - `PreparedBatch_ReachesNoLiveObjectGraph`: a reflection walk of the batch after release finds no `ConnectedSystemObject` or `MetaverseObject` instance.
   - The hand-off guard throws when a transaction is open.
2. Implement the barriers, the fault probe, the navigation cut, stats applied at the barrier, `DrainOnExitAsync`, and the end-of-run log.

**Acceptance:** all of the above green, and no change to any existing test's expectations.

### As built in Phase 3

- **Mode:** `RunWithHistoryWriterAsync` chooses Overlapped when `SupportsConcurrentHistoryWrites`, Inline otherwise, with the internal `HistoryWriteModeForTests` override; every RequiresPostgres workflow test therefore runs Overlapped. Inline runs every flush, the page loops' included, exactly as Phase 2 did, so the in-memory suite is unchanged.
- **Two flushes, one preparation:** the page loops call `FlushPageHistoryAsync(page, totalPages)` (prepare, release, cut, hand off, no wait); every other caller keeps `FlushHistoryAsync`, which hands off and waits at once (`AwaitedFlush`). Both go through one `PrepareHistory`, which now clears the Metaverse Object change queues as it takes them rather than after the write.
- **Release before the write completes:** an Overlapped hand-off releases the items (Activity cleared, detached, the deletion-row bookkeeping, the page lookups) at once; the totals are counted at the barrier that finds the write succeeded (`CountWrittenHistory`), or by the drain when another failure is ending the run. `ItemsWrittenOutsideTracking` is kept and asserted there: a store that kept the items would have had them released under it, which only a store that cannot run Overlapped does. The in-memory store's "keep the items on the Activity" behaviour stays where it was, so its tests read what they read before.
- **The navigation cut** is `SynchronisationHistoryBatch.ReleaseRunObjects`: each item's `ConnectedSystemObject`, every causal edge's cause and effect navigations, and each Connected System Object and Metaverse Object change record's `ActivityRunProfileExecutionItem`. Outcome navigations (`ParentSyncOutcome`, `ConnectedSystemObjectChange`) are left: they reach only the batch's own objects. The item's `Activity` is left too: it is the run's, and the writer reads only the item's `ActivityId`. `PreparedBatch_ReachesNoLiveObjectGraphAsync` walks every field of every handed-off batch.
- **Span parent:** rather than resetting `Activity.Current`, `FlushPageHistoryAsync` hands off after its `FlushHistory` span has ended, so `SyncHistoryWrite` parents to `ProcessConnectedSystemObjects` / `ProcessModifiedConnectedSystemObjects` (asserted). The page loops' `FlushHistory` span now covers prepare and release only.
- **Barriers:** `WaitForPreviousPageHistoryAsync` is the first statement of each page's `try` (its catch already lets `SyncPersistenceException` through); `ThrowIfHistoryWriteFailed` precedes each page load; `WaitForPageLoopHistoryAsync` follows the page loop's `finally`, on completion and on cancellation alike. A failure in a page's own work leaves the loop without reaching that barrier, and the drain takes over.
- **Drain:** `RunWithHistoryWriterAsync` observes the exception ending the run with a filter that returns false, so it is never caught, then calls `DrainOnExitAsync`, which now returns the drained write (its totals are counted) and records no second warning when the exception ending the run is the history failure itself, or wraps it or its cause. The end-of-run log line gives batches, writing, waiting and hidden seconds.
- **Tests:** `SynchronisationHistoryOverlapDatabaseTests` holds a history write back with a lock on `ActivityRunProfileExecutionItems` taken from the test's own connection (no production hook) and injects the failure with a trigger on `MetaverseObjectChangeAttributeValues`. The equivalence fixture runs Inline and Overlapped against the one baseline. The hand-off guard's test is Phase 2's `HandOffAsync_RunRepositoryHoldsATransaction_ThrowsAndWritesNothingAsync`.

### Phase 4: Validation, documentation, changelog

- Integration scenarios at Medium or above:
  - 008 Cross-Domain Entitlement Sync (Samba AD) and 032 Intra-Type References (OpenLDAP); both are required by `src/JIM.Application/CLAUDE.md` section 6 because the flush and cross-page pass change;
  - 004 Deletion Rules (deletion-written rows, recall items, causal edges);
  - 005 Matching Rules (same-page rejoin outcome summaries, match candidates);
  - 001 HR to Identity Directory (provisioning records, Activity totals);
  - 023 Unique Value Generation (page 2 history);
  - 026 Derived Attribute Flows;
  - 027 First Synchronisation.
- Reader search for the PR: every scenario reads Activities and items only after a terminal status (`Assert-ActivityItemsHaveOutcomeSummary`, `$terminalStatuses`), and nothing changes at the terminal state, so I expect no scenario changes; record the search terms. During a run, the Activity's item list can lag by up to one page.
- Scale: Scenario 008 at Scale500k25kGroups on OpenLDAP. Compare the Source Full Synchronisation time against 883 s, read `SyncHistoryWrite` and `SyncHistoryWait` from the spans, and compare peak Worker memory from the docker-stats CSV.
- Update `engineering/developer/diagrams/FULL_SYNC_CSO_PROCESSING.md` (flush section and the two-phase persistence note) and `engineering/DATABASE_GUIDE.md` (one history connection per running synchronisation, only during load and processing).
- Changelog: ⚡ "Full and Delta Synchronisation record each page's history while the next page is read and processed, shortening large runs." Docs: n/a, performance only.
- Move the plan to `done/` in the PR that closes the issue.

### Phase 5 (conditional, from Phase 4 data)

Overlap the cross-page batches, or revisit the later barrier (open question 1).

## Success Criteria

- The 500k Source Full Synchronisation is at least 90 s faster than 883 s on the same host; the target is 100 to 170 s.
- Total `SyncHistoryWait` is at most 40% of total `SyncHistoryWrite`.
- History, Activity totals, stat counters and statuses are identical to the serial baseline for Full and Delta.
- A failed write fails the run with the page named on the Activity and leaves no partial page history.
- Peak Worker memory rises by no more than 10% on Scale500k25kGroups.
- No regression in any test or the listed scenarios.

## Benefits

- **Performance:** most of the 255 s of serial history time is hidden behind the next page's work.
- **Integrity:** each page's history is all-or-nothing, and failure semantics are unchanged.
- **Observability:** spans show measured overlap and wait time instead of an estimate.
- **Architecture:** one write path for synchronisation history, with ids owned by the main thread.

## Dependencies

- #2872 (parallel gate and failure removal) merged first; link it as blocked-by.
- No new packages and no schema change; uses the existing `OpenIsolatedScope`.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Two pages' history in memory at once (a group page carries about 650k change values) | The navigation cut releases page N's hydrated graph; Phase 4 memory comparison; the 10% criterion |
| Hidden shared mutable state between the batch and the next page | All ids and mutations in prepare; the writer only writes; reflection-walk test; XML doc rule |
| Lock waits or deadlock between the connections | History foreign keys reach only items, the Activity and static configuration; ordered counter upserts; barrier 1 excludes state writes; overlap test on PostgreSQL |
| In-memory store is not thread-safe | Inline mode, chosen by capability |
| Self-deadlock if a future call site hands off while holding a transaction | `HasOpenTransaction` guard and test |
| A crash now loses history across page N+1's load and processing as well as the flush | Same kind of remnant as a crash between today's flush steps; startup recovery |
| Connection pool | +1 connection per running synchronisation, never concurrent with the parallel bulk writers (barrier 1) |
| Span tree tooling misplaces background spans | Explicit parent rule; check `Show-PerformanceTree.ps1` in Phase 4 |
| Rebase churn on #2872 | Land after it; the split touches only the collect and write halves |

## Open Questions for the Product Owner

1. **Where the run waits.** Recommended: before the next page's changes are saved. This overlaps only the next page's load and processing, as approved, and keeps failure semantics and pool use identical to today. Waiting only at the next hand-off could hide more of the 255 s, but it lets one more page of changes save before a failure surfaces and competes with the Metaverse Object create writes. Revisit only if Phase 4 shows significant wait time?
2. **During a run, the Activity's item list and live totals can lag by up to one page;** the final result is unchanged. Acceptable?
3. **One transaction per page's history** (items and Metaverse Object changes together) instead of today's two or three. Recommended, because a failure then leaves none of that page's history rather than items without their change records. Agree?

### Critical Files for Implementation
- `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs`
- `src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs`
- `src/JIM.Worker/Processors/SyncDeltaSyncTaskProcessor.cs`
- `src/JIM.PostgresData/Repositories/SyncRepository.RpeiOperations.cs`
- `src/JIM.PostgresData/Repositories/SyncRepository.MvoChangeOperations.cs`
- `src/JIM.Data/Repositories/ISyncRepository.cs`
- `src/JIM.InMemoryData/SyncRepository.cs`
