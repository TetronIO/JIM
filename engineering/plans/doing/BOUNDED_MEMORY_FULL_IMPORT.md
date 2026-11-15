# Bounded-Memory Full Import

- **Status:** Doing (Phase 1 complete)
- **Issue:** [#2521](https://github.com/TetronIO/JIM/issues/2521)
- **Related:** [#917](https://github.com/TetronIO/JIM/issues/917) ([Bounded-Memory Delta Import](../BOUNDED_MEMORY_DELTA_IMPORT.md)), [#2669](https://github.com/TetronIO/JIM/issues/2669)

## Overview

Make a Full Import's peak Worker memory a function of the page, not of the Connected System's size.

In the 500k lab run (Scenario 008, Scale500k25kGroups, OpenLDAP: 525k Connected System Objects, about 10.9M attribute values), the Target system's confirming Full Import is the run's memory peak:
- **Docker peak:** 17.9 to 22.1 GiB on identical code.
- **Live heap:** about 9.3 GB after the forced collection at the start of the save phase.

By contrast, synchronisation now pages with a value budget (#2934) and peaks at about 1.4 GB live.

Bounding JIM's page alone is not enough. Today's peak has three sources, and all three must go:

1. **Every matched object, hydrated whole, kept for the whole run.** `connectedSystemObjectsToBeUpdated` holds every matched object until the save phase. On a confirming import every object is Pending Provisioning and unstamped, so the content-hash skip (#1082) does not apply and all 525k are hydrated with every value.
2. **A preload of every Pending Export.** `GetPendingExportsLightweightByConnectedSystemIdAsync` loads every Pending Export of the Connected System with every value change (525k exports, 9.8M value changes) at run start. It is never pruned.
3. **One Connector page holding the whole directory.** For OpenLDAP, 389 Directory Server and Generic directories, `LdapConnectorImport` drains every container and Object Type pair inside one `ImportAsync` call. RFC 2696 paging cookies are scoped to the connection, and the import object is created per call, so no cursor survives between calls. The import sees a single page of 525k objects.

## Business Value

- An administrator sizes the Worker by the page, not by the directory. A 500k-object first load stops needing a 20+ GiB host.
- A Full Import stopped part-way (cancelled, memory guard, failure) keeps what it saved, and the next run completes the job.
- Fetching from the directory overlaps processing, instead of running first and alone (`ImportPage` took 66.7 s before any processing began).

## Decisions

Agreed with the product owner on 2026-11-15:

1. **Order:** build the shared staging core on Full Import first, then fit #917 (Delta Import) onto it. Full Import is the measured peak, and the simpler case: a later copy of an object is rejected rather than superseding the earlier one, deletions are found by absence after the last page, and creates can be staged per page. #917's plan is stale in about a dozen places (see below) and is revised against the core afterwards.
2. **Measure before committing to the rest:** Phases 0 and 1 change no behaviour. The later phases are confirmed against what they measure.
3. **Memory breakdown:** the lab may use `dotnet-gcdump` (a Microsoft diagnostic tool) in its image for Phase 0's heap breakdown. Lab only; nothing ships in the product.
4. **A synchronisation after a stopped import:** a Connected System "import incomplete" marker, which Full and Delta Synchronisation refuse to run past. Without it, a synchronisation reading a half-staged import removes Metaverse references whose targets are on unsaved pages (`SyncEngine.AttributeFlow.cs` removes every Metaverse reference not among the resolved ones), and exports those removals. The gap exists today when the save phase fails part-way.
5. **#2049 Object Type change replacements:** held in memory, with their execution items, until deletion detection has decided them. They grow with type changes, which are opt-in and rare; the count is logged.
6. **Creates on a Full Import:** staged per unit, not held to the end. Net change cannot be lost within a Full Import.
7. **LDAP read order:** the Connector reads Object Types in reference-dependency order, as synchronisation pages them (#2492). The forward-reference sweep then covers only references within one type (managers, nested groups).
8. **Value budget:** a constant (start at 250,000 values per unit), not a Service Setting, tuned from the Phase 0 and Phase 5 measurements.

## What is stale in #917's plan

1. **Reconciliation** is now per batch (#2908, #2913: `BeginPendingExportReconciliationAsync`, `ReconcileSavedBatchAsync`). `DeferredIds` and `referencesResolvedSinceHydration` still assume the whole run.
2. **The Pending Export preload** is described as resident and pruned. It is neither: it is never pruned, and it is one of the two largest structures on a confirming import.
3. **The External ID lookup** is no longer always loaded once: a Delta Import looks up per page (#2705, #2835).
4. **Delta Import hydration** loads only undecided values (#2817) and skips groups held as imported (#2836).
5. **The create batch** writes objects, items and change records in one call (#2909).
6. **Partitions:** per-object `PartitionId` restamps (#2723) and per-Partition watermarks (#2715) are not covered.
7. **A Delta Import that read every object** detects deletions by absence (#2720) and needs its own rule.
8. **Holds** standing at the start of the read (#2881) and the Exchange hold judgement (#2021) must stay run-start and run-end.
9. **The value budget** should be unconditional: #2934 measured the need in synchronisation.
10. **The stopped run:** a synchronisation in between drops unresolved staged references (decision 4).
11. **Order** is reversed (decision 1).
12. **Time-ordered ids** (#2929): sweep and deferred-record writers use `TimeOrderedIds`.

## Design

### The staging unit

- The Worker classifies each Connector page as it arrives, per import object: repeated on its page, or returned on an earlier page. Full Import's rejection of a later copy (#2063) stays exactly as today.
- Pages accumulate into a unit until it reaches `ImportBatchSize` (2,000) objects or the value budget; a larger page is split.

For each unit:
1. Normalise, hydrate and compare (`ProcessImportObjectsAsync`, unchanged logic).
2. Resolve references, in memory over the unit and then in the database. A value whose target is on a later page is saved unresolved and noted; so is one naming a held #2049 replacement.
3. Stage the batch: today's save code, extracted. Create the batch (objects, items and change records atomically), save the updates, stamp the content hash, flush every item of the unit, load the unit's Pending Exports by id and reconcile them, release the values and clear the change tracker.
4. Record the ids seen and update the counts.

After the last page:
1. Deletion detection over ids, with the percentage base captured at run start.
2. The Unconfirmed Create retry (only on a read of every object in scope, as now).
3. Reconcile #2049 type changes, then create the replacements that stand.
4. The reference sweep: a bounded, chunked read of outstanding values that reuses the in-memory resolution's rules (declared target type #1285, case rules #2051, ambiguity reported, never guessed).
5. Report what is still unresolved on the items already saved.
6. Fix up cross-batch change record references.
7. Judge the holds over ids.
8. Record the watermarks.
9. Clear the import-incomplete marker.
10. Write the Activity message.

What stays run-wide: the External ID lookup, the cross-page seen External IDs, the ids seen, the imported External IDs (compacted per Object Type), the ids of items with unresolved references, and the held #2049 replacements. That is about 450 bytes per object, roughly 240 MB at 525k, plus one unit.

### The LDAP Connector's pages

For directories whose paging cookies are scoped to the connection:
- A cursor kept between calls, on `LdapConnector` (one instance per run).
- Up to Import Concurrency producer tasks, each with its own connection and container and Object Type pair. Each pages its search into a bounded `System.Threading.Channels` channel, which applies backpressure.
- Each `ImportAsync` call drains up to the page limit and returns a continuation token while entries remain. The watermark comes on the first page only.
- A producer's failure fails the next call. Close and cancellation stop the producers and dispose every connection.
- Object Types are read in reference-dependency order (decision 7).

### A stopped run

Staged units stay. Deletion detection, the retry, #2049 replacements, the sweep and the watermarks are skipped. The import-incomplete marker stays set, so synchronisation refuses until a Full Import completes. The next Full Import re-reads everything: staged objects are Normal and stamped, so the hash skip passes over them, and no duplicate change records are written.

## Implementation Phases

Each phase is its own PR, test-driven (red first), with no behaviour change until the phase marked as changing it.

### Phase 0: Baseline and harness (no product change)

- Lab baseline on `main`: Scenario 008 on OpenLDAP at Scale500k25kGroups and Scale100k5kGroups. Record the docker-stats peak, the GC heap logged at the start of the save phase, and every phase timing of the first and the confirming Full Import. Take a `dotnet-gcdump` heap snapshot at the start of the save phase, to split the 9.3 GB between hydrated objects, the Pending Export preload and execution items.
- Worker harness: a Full Import over N pages of large groups, with a deterministic counter of the most hydrated values held at once (`internal`; red: it grows with N), and an explicit `GC.GetTotalMemory(true)` measurement.
- Done with Phase 1: the harness is `FullImportHeldValuesHarnessTests`, and its counter `SyncImportTaskProcessor.PeakHeldAttributeValues` (sampled after each page, before the save phase and after each save batch, only when `MeasureHeldAttributeValues` is set). The growth with N is a characterisation, not an explicit test, so Phase 5 inverts it.
- Lab baseline on `aa8c461c5` ([#2521 comment](https://github.com/TetronIO/JIM/issues/2521#issuecomment-6893502898); clean timings from the `61b290a51` run, as the dumps paused the Worker). `dotnet-gcdump` stops at 10M objects and this heap holds 70 to 150M, so the lab took process dumps and read them with ClrMD instead. At the start of the confirming import's save phase, 9,266 MB was live:
  - Pending Export preload: 4,525 MB (10.34M changes, about 264 bytes each). Phase 1 removes it.
  - Objects hydrated for update: 3,915 MB (10.86M values, about 240 bytes each). Phase 5's target.
  - The Connector page: 212 MB, only the External ID attributes `externalIdsImported` keeps. Phase 3's target.
  - Execution items: 184 MB. Everything else: 430 MB.
- What the baseline changed:
  - **The live high-water mark is in CallBasedImport, not the save phase:** about 12.7 GB, while the Connector's single page (3,475 MB, dead by "Import complete") is still live. About 1.2 GB of that page was empty typed value lists, seven per attribute. Phase 1 makes each list allocate on first use (192 bytes saved per attribute), and Phase 2 is what bounds the rest.
  - **The 20 GiB Docker peak is mostly garbage:** at the worst point only 2,740 MB of an 18,438 MB heap was live. Phase 1 is therefore measured by live heap, not by the Docker peak; a lab run with `DOTNET_GCConserveMemory` measures how much of the peak is garbage collector policy.
  - **The preload is pruned as the save goes,** from 10.34M changes to 2.17M, so the 4.5 GB is the peak of a falling curve.

### Phase 1: Extract the staging step; load Pending Exports per batch (no behaviour change) ✅

- Move the save phase into one staging method, still called once.
- The whole-run consumers (deletion detection, the Unconfirmed Create retry, the hold judgements, reconciliation setup) take id sets.
- Capture the deletion-limit base at run start.
- Replace the Pending Export preload with a per-batch load by id; convert the raised-items list to counts.
- Tests: every existing test passes; a `RequiresPostgres` test that per-batch loading reaches the same reconciliation verdicts as the preload (held, retried, released, Exchange-held).
- Scenarios (Pending Export lifecycle): 008 (Samba AD at Medium, OpenLDAP at Large), 032 (OpenLDAP, Medium), 014, 017, 001; 024 and 034 in the AD lab after merge.
- Measure the save-phase heap at 500k.

What was found doing it:
- **The preload judged Pending Exports that no longer existed.** It was taken at run start; deletion detection then removes the Pending Exports of the objects it marks deleted, and the preload still held them, so reconciliation judged each one and merged an "export not confirmed" error onto the object's deletion item. Every test ran without a context factory, so loaded at the start of the save phase and never saw it; the Worker always has one. Per-batch loading matches what the tests characterised (`ConfirmingImportPendingExportHoldsDatabaseTests`, the context-factory case was red).
- **`DeferredIds`** comes from the update list's ids alone (an id listed twice), and one query for which of those few have a Pending Export; their Pending Exports load when they are reconciled, after the save phase. No batch touches them, so they are as they stood when the save phase began, as before.
- **The "no Pending Exports" early-out** is a count of the Connected System's Pending Exports when the save phase starts. A Pending Export with no object counts where the preload left it out; the run then reconciles nothing, as before.
- **The overlap stays:** given a context factory, the next batch's Pending Exports load on their own context while the current batch is saved and reconciled.
- **The raised-item totals** are counts (`ExecutionItemTotals`), merged with the last flush's items exactly as one list was counted: one item with an outcome makes the whole list count by outcome.
- **Empty value lists:** `ConnectedSystemImportObjectAttribute` now allocates each of its typed lists on first use, as #2153 did for date and true-or-false values, and the import path probes them through `Has*` properties, so a string attribute holds one list instead of seven (384 to 192 bytes). A lazy getter alone saved nothing: the import's own probes allocated every list again (`ImportValueListAllocationTests` was red on that).
- Scenarios and the 500k measurement are still to be run.

### Phase 2: The LDAP Connector yields pages (Connector behaviour change)

- The cursor and producers above, with dependency-ordered reads.
- Tests through the connection factory seam: bounded pages until every pair is drained, the watermark on the first page only, a producer's failure failing the next call, disposal on close and cancellation, and backpressure.
- Scenarios: 008 on OpenLDAP (Large) and on 389 Directory Server, 009, 019, 022, 032, and a Scale100k5kGroups run.

### Phase 3: Deletion detection over ids; #2049 replacements held apart (no behaviour change)

- Deletion candidates as a summary (id, status, Object Type) applied in batches, not whole objects held to the end.
- Tests: the existing deletion-limit and Object Type change tests pass unchanged; detection holds no hydrated candidates; replacements are kept apart from the run's creates.
- Scenarios: 004, 021 (Test 2), 009, 007, 001.

### Phase 4: The import-incomplete marker (behaviour change)

- A nullable time on Connected System (additive migration), set before the first write and cleared on completion, including completion with deletions refused by the limits.
- Full and Delta Synchronisation of that Connected System refuse, with a message naming the stopped import and the remedy.
- Tests: a Full Import failing in its save phase leaves the marker set (red today); synchronisation refuses and says why; deletions refused by the limits do not set it; a `RequiresPostgres` round trip of the column.
- Scenarios: 021, and the full suite at Medium.

### Phase 5: Stage each unit of a Full Import (behaviour change)

- The unit accumulator, the value budget (reusing `ValueBudgetedBatchPlanner`), per-unit resolution with forward references saved unresolved, the end-of-run sweep and late reporting, in the order above. Full Import only; Delta Import is unchanged.
- Tests (red first): the harness counter is flat in N; a group on page 1 referencing a user on page 3 resolves; still-unresolved references are reported on the saved item for each of Error, Warn and Ignore; the sweep follows declared target types and case rules; a run stopped after unit 2 keeps its staged objects and watermark and detects no deletions; running again completes with no duplicate change records; the Unconfirmed Create retry and deletion detection find the same candidates after staged pages; a reference to a #2049 replacement resolves as today; the value budget splits a page of large groups. `RequiresPostgres` tests for two units on one context with tracked instances present, and for the sweep.
- Scenarios: 008 (Samba AD at Medium, OpenLDAP at Large, then Scale500k25kGroups), 032, 016, 015, 028, 029, 004, 021, 009, 001, 014, 017; 024, 025 and 034 in the AD lab after merge. Add an integration check of the Unconfirmed Create retry.

### Phase 6: Held-reference answers for Full Import (optional, measured)

- Extend the held-reference questions Delta Import asks (#2817, #2836) to Full Import, to cut hydration and comparison on confirming imports. Its own PR, with measurements.

### Phase 7: Validation, documentation, changelog

- Docs: `docs/configuration/import-watermarks.md`, `docs/administration/troubleshooting-worker.md` (sizing), `docs/connectors/jim-ldap-connector.md` (Page Size and Import Concurrency now govern Full Import memory), `engineering/notes/RUN_PROFILE_PHASES.md`.
- Changelog: ⚡ for the memory change; 🔄 for a stopped Full Import keeping what it saved and synchronisation waiting for it.
- Revise #917's plan against the staging core.

## Success Criteria

Scenario 008, Scale500k25kGroups, OpenLDAP:
- The harness counter is flat as N grows, and its GC measurement is within 25% between 1x and 5x the pages.
- The forced-collection heap at unit boundaries is at most 1.5 GB (9.3 GB today).
- Docker peak is at most 4 GiB for both the first and the confirming Full Import (17.9 to 22.1 GiB today).
- No wall-clock regression in the whole Full Import against Phase 0.
- Outcomes are identical on a completed run: the same objects, values, references, Pending Export verdicts, deletions and counts.
- A run stopped part-way keeps its staged units and its watermark, detects nothing as deleted, synchronisation refuses, and the next Full Import completes with no duplicate change records.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Wall-clock regression from per-unit round trips | Units of 2,000 objects; the Connector's fetch overlaps processing; the next unit's Pending Exports are prefetched; measured against Phase 0. |
| The sweep resolves differently from in-memory resolution | It reuses the same resolution rules; tests per #1285 and #2051; Scenario 032. |
| A large forward-reference sweep | Dependency-ordered reads; a chunked, set-based sweep with bounded statements. |
| Deletion detection reads state after the run's saves | The percentage base is captured at run start; equivalence tests. |
| Tracked-instance faults across units on the long-lived context | The change tracker is cleared per unit; a `RequiresPostgres` multi-unit test. |
| A stopped run is visible to synchronisation | The import-incomplete marker (Phase 4). |
| The Connector leaks connections or threads | Disposal on close and cancellation; tests; the bounded channel. |
| A mass Object Type change | Held in memory with the count logged; revisit decision 5 if ever measured. |
