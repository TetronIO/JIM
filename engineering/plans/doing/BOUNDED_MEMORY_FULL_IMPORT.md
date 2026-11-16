# Bounded-Memory Full Import

- **Status:** Doing (Phases 0 to 5 complete)
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
- **Garbage collector policy, measured after Phase 1** ([lab counters](https://github.com/TetronIO/JIM/issues/2521#issuecomment-6896913899)): `System.GC.ConserveMemory=7` costs no time (Test 1 1,223 s against 1,217 s) and lowers most step peaks: Source Full Import 11.0 to 8.5 GiB, Source Full Synchronisation 3.8 to 2.4 GiB, Target Full Synchronisation 3.8 to 3.2 GiB, confirming Full Import 11.9 to 11.6 GiB. The Worker ships with it.
- **The Source Full Import's `ProcessImportObjects` time is not a garbage collector cost.** It ranged from 16.8 to 30.9 s across twelve 500k runs on old and new code alike; the slow windows show allocation halving and CPU at 3 to 5% with almost no collections, a wait outside the process. No gen0 budget floor is needed.
- **Next allocation target:** the confirming save allocates about 64 GiB in 80 s (about 125 KB per updated object), mostly Npgsql parameters and strings.

### Phase 2: The LDAP Connector yields pages (Connector behaviour change) ✅

- The cursor and producers above, with dependency-ordered reads.
- Tests through the connection factory seam: bounded pages until every pair is drained, the watermark on the first page only, a producer's failure failing the next call, disposal on close and cancellation, and backpressure.
- Scenarios: 008 on OpenLDAP (Large) and on 389 Directory Server, 009, 019, 022, 032, and a Scale100k5kGroups run.

What was found doing it:
- **One Connector instance per run holds:** the Worker creates the Connector once per task (`Worker.cs`, `_connectorFactory.Create`) and hands it to `SyncImportTaskProcessor`, which opens, calls every page and closes on it, closing in a `finally`. The cursor (`LdapFullReadCursor`) lives on `LdapConnector` from the first `ImportAsync` to `CloseImportConnection` (and `Dispose`). A continuation reaching a Connector that never started the read throws rather than reading as "nothing left", which would have JIM take every object it holds as deleted.
- **The searches:** one per Container and Object Type, each on a connection of its own from the plan's factory (now `ILdapDedicatedConnection`, which is the test seam), up to Import Concurrency at once (the existing setting, default 4); without a factory, which only a test builds, one at a time on the import's connection. Each pages its search at the Run Profile's Page Size into one bounded `Channel` of Page Size objects, waiting when it is full. A call takes up to Page Size objects and returns a continuation while anything is left; it waits for the next object, or the searches' end, before saying so, so the last page is never empty. Held at once: about one page handed over, one buffered, and one directory page per search.
- **Watermarks and Partitions:** unchanged. The first call reads the rootDSE and takes every target Partition's watermark (#2715) before any search starts, and only that call returns `PersistedConnectorData` and `PartitionWatermarks`. The searches cover every top-level selected Container of every target Partition, as before. A later call carries on from the cursor before anything else, because the record JIM replays to later pages is the previous run's, which could make a later page think the directory is a different type (on a first ever import it is null, and the page would have fallen to the Active Directory path and ended the read early).
- **Delta Import that reads every object (#2720)** pages the same way: every later page decides alike from the same record and watermarks, reaches the fallback and carries on the cursor; a later page that decided otherwise throws rather than drop the rest of the read.
- **Failure:** a search's failure stops the others and is thrown by the call that next reaches it, before or after taking its page; no page claims the read is complete while a search has failed. Search limit refusals keep their message (`CannotPerformImportException`). An unexpected cancellation inside a search counts as a failure, never as its end.
- **Close and cancellation** cancel the searches and wait for each to close its connection; a search waiting on the directory finishes that request first, bounded by Search Timeout. A cancelled call returns what it has, as before; the processor skips everything after the read on cancellation.
- **Reading order (decision 7):** the Worker hands the order through a new `IConnectorObjectTypeReadOrder`, before opening the connection, on Full Import only, computed with `SyncObjectTypeOrdering` exactly as Full Synchronisation does: from declared targets, else the types the held values point at (one aggregate query). Each group's searches start once the group before has been read to the end. Without an order (Delta Import, or a test) every selected type reads together, as before. On a Connected System's first import JIM holds no values, so an undeclared attribute with none is taken to reach every type (left out of the observed map) rather than none: the lab's first Full Import was ordering groups before people by id alone. So on a first import a type whose undeclared reference is mapped is read after every type it could reach (Scenario 008's first import: "jimPerson, then jimGroup", since only `member` is mapped there), and two types that both map one are read together, as before; from the second import the held values narrow it.
- **What the processor assumed:** nothing about one page that the multi-call shape did not already handle (Active Directory has always returned a page per call). Two consequences to know about:
  - **Duplicates (#2063):** two copies of one External ID on the same page are both rejected; on different pages the earlier is kept and the later rejected. With the whole directory on one page every duplicate was the first kind; it is now whichever the paging gives, as for Active Directory. Only an External ID that is not unique in the directory reaches this.
  - **Per-page overhead:** Page Size defaults to 100, so 500,000 objects are now 5,000 calls, each with its own hydration and reference queries, Activity message writes and step changes, where they were one. Measured and fixed below ("The cost of a page").
- **Exclusion discards (#1255)** are counted by the searches between calls, into one tally the session shares; each call now takes what has accumulated (`ExclusionDiscardTally.TakeCounts`) rather than reading it, since JIM adds up every page's counts.
- **Progress:** each call enters the Fetch step, naming what the searches are reading, and reports the objects it handed over; the searches report nothing themselves, as the step would otherwise show as running while JIM processes. The cursor logs a summary when the read ends.
- **A real bug found by the tests:** describing what is being read with LINQ straight over a `ConcurrentDictionary` sizes its buffer from `Count` and then enumerates, so a search finishing in between threw `IndexOutOfRangeException` (about one run in twenty-five); it reads a `ToArray` snapshot.
- **The cost of a page.** At Scale500k25kGroups the lab measured the Source Full Import at 259.9 s against 185.0 s before paging, and the confirming one at 330.7 s against 235.0 s, with the Worker at half a core and statement time and allocation unchanged. Its A/B run with `synchronous_commit = off` removed the whole slowdown ([#2521 comment](https://github.com/TetronIO/JIM/issues/2521#issuecomment-6899062924)), so the cost was commit latency, not round trips or reads.
  - **Where the commits came from:** nine Activity writes per page, each its own commit (and both tables carry a `pg_notify` trigger). These were the fetch message, the Connector entering its step (the step rows, then the message), the objects it read, leaving its step, "Reading page N", JIM entering its processing step (rows, then message), and the every-100-objects progress, which at a page of 100 is every page. A unit test over 58 pages counted 522.
  - **The fix:** `ActivityProgressThrottle`, opened around the call-based import's page loop. The run's message, counters and step changes are recorded in memory and written at most once a second, latest state first. A write happens at once when the last was a second or more ago, otherwise from a timer once the second has passed, so a long Connector call still shows what was last reported. It writes on a connection of its own (`IActivityProgressWriter`), one write at a time, so the timer never touches the run's DbContext; without one, the next report after the interval writes through the repository.
  - **Boundaries:** the first page is flushed, and closing the throttle (end of the read, failure, cancellation) writes the final state before anything else writes the Activity. A failed write is logged and carried by the next; a final write that keeps failing is retried through the repository.
  - **Step timings are unaffected:** a step keeps the time it was first entered and the latest time it ended, and the state as it stands is what is written.
  - **`synchronous_commit = off` was not needed:** at one write a second, one commit's wait is not worth a durability setting.
  - **Scenario 008 at Large on OpenLDAP (10,527 objects, 106 pages).** Before is the reference run of `6c16ca68a` and a second run of it; the row counts are `pg_stat_user_tables` at the end of each run. The scenario as a whole took 885.8 s before and 881.8 s after, as the imports are a small part of it at this size.


    | | Before | After |
    |---|---|---|
    | Source Full Import, CallBasedImport | 5,506 and 5,152 ms | 2,677 ms |
    | Its ImportPage / ProcessImportObjects / unaccounted | 2,194 / 1,929 / 1,348 ms | 248 / 1,681 / 710 ms |
    | Confirming Full Import, CallBasedImport | 7,070 and 7,347 ms | 3,288 ms |
    | Its ImportPage / ProcessImportObjects | 2,091 / 4,198 ms | 336 / 2,854 ms |
    | Progress writes per Full Import | about 950 | 5 and 6 (for 743 reports) |
    | Whole Full Import (Source, confirming) | 13.9 and 12.9 s, 14.4 and 12.3 s | 10.5 s, 7.5 s |
    | Activities rows updated, whole scenario | 2,262 | 936 |
    | ActivityPhases rows written, whole scenario | 1,518 | 662 |
    | Commits (read-only ones included), whole scenario | 47,471 | 35,407 |

  - **The per-page summary log line** counted every held execution item three times on every page, which grows with the run (about 4 ms per page of growth at 500k). It keeps running counts instead.
  - **The reference External ID read** (`LoadReferenceExternalIds`) asked the database once per page, even for a page of users holding no references. It now asks only about matched objects holding a resolved reference in an attribute loaded whole: that is everything the query reads, so another object's answer is empty without asking. Scenario 008's confirming import went from 711 to 291 ms over 106 calls; with `manager` flowing (Scenario 032) every user page still asks.
  - **Left alone:**
    - The per-page hydration reads (objects, values and Object Types, three statements a page). With progress coalesced each later call costs 1 to 3 ms. The Object Type read could be held for the run, but its instances are wired into each page's objects, and nothing measured asks for it.
    - `List.Remove` of an item withdrawn when an object is unchanged scans from the front. It is O(items held) per withdrawal, which only bites when many objects are held and many unchanged ones are withdrawn in the same run. It is not a paging cost.
  - **The confirming import's first `LoadReferenceExternalIds`** (11.6 s at 500k, 221 ms at Large) was not, as first thought, the cost of being the first read of the membership rows the Export and Synchronisation had just written. The lab ran it with auto_explain and replayed it ([#2521 comment](https://github.com/TetronIO/JIM/issues/2521#issuecomment-6901435308)): the cold cache cost about 3.5 s of its 16.4 s, and the other 13 s repeated in every warm replay, so it was CPU. Statistics were fresh. The page is the 100 largest groups: 3,030,928 references to only 495,000 distinct users. The statement looked up each reference's External ID separately, probing the referenced object's row and both its External ID values per reference (9.2 million index probes, 42.9 million buffers).
    - **The fix:** the statement materialises the page's references, looks up each distinct referenced object's External ID once, and returns two kinds of row: one per referenced object with its External ID, then one per reference without one. The lab's version joined the two in the database (3.7 s against 11.0 s warm, 7.0 million buffers), but spilled a 495,000-entry hash to disk and still sent each External ID once per reference; returning them apart needs no join and sends each once. The Worker streams the rows (raw Npgsql) into the per-object dictionaries, every reference to one object sharing one string: 495,000 strings for that page rather than 3 million. The method's contract is unchanged, and an equivalence test pins it.
    - **Measured locally on Scenario 008 at Large on OpenLDAP** (one run each, so noisy): the confirming import's 106 calls took 190.9 ms against 227.4 ms, its first 115.0 ms against 168.8 ms. That page holds 32,424 references to 9,900 users; replayed warm, the statement took 75 to 79 ms against 196 to 220 ms and 82,000 buffers against 358,000. Over every object of the Target system both statements return the same 37,645 (object, referenced object, External ID) rows.
    - **Answering from the External ID lookup instead was evaluated and not taken.** A Full Import that read every object holds every object's External ID from the start of the run (`PreFetchCsoExternalIdLookup`), and the idea was to return only (object, referenced object) and map the rest in memory. It would not return the same answers:
      - The lookup is keyed for matching, not for comparing references. Each object is held under its External ID, and under its Secondary External ID only while it has no External ID; the comparison wants the Secondary (an LDAP DN) whenever there is one. So for every object JIM has confirmed, the lookup holds its entryUUID where the comparison needs its DN. Keys are also folded (lowercased unless case-exact, GUIDs and numbers rendered as text), where the comparison reads `StringValue` as held.
      - Delta Import has no such lookup: it looks up each page's own External IDs (#2705), and a Full Import holds the whole lookup only when the Connector says it read every object.
      - The lookup is never updated during the run. Today that does no harm, since nothing an import changes is saved before its last page. Phase 5 saves each unit as it goes, after which a referenced object renamed on an earlier unit would be compared by its old DN, removing and re-adding the reference.
      - Carrying the Secondary External ID as held in the lookup, and keeping it current through Phase 5's saves, would add about 500,000 strings to what stays run-wide, against this plan's aim. With each External ID now read once per page, the remaining database cost is reading the references themselves (about 0.4 s for the 500k first page, the lab's `av` node).

### Phase 3: Deletion detection over ids; #2049 replacements held apart (no behaviour change) ✅

- Deletion candidates as a summary (id, status, Object Type) applied in batches, not whole objects held to the end.
- Tests: the existing deletion-limit and Object Type change tests pass unchanged; detection holds no hydrated candidates; replacements are kept apart from the run's creates.
- Scenarios: 004, 021 (Test 2), 009, 007, 001.

What was found doing it:
- **What was held, and for how long:**
  - The External IDs: each object's External ID attribute as the Connector returned it (`ExternalIdPair`), from its page to the end of `PerformImportAsync`. It was a local of the async method, so it lived through the save phase, which is where Phase 0 measured it.
  - The candidates: each missing External ID was looked up on its own (`GetConnectedSystemObjectByAttributeAsync`), loading the whole object (values, Type and attributes, each resolved reference's object and Type), tracked on the run's context, and held until applied. Already-Obsolete candidates needed only their id; a refused run needed none of them, and still made one query and loaded one whole object per object JIM held that the import did not return.
  - The #2049 replacements: in the run's creates from the page they arrived on, taken out again by `List.Remove` if withdrawn.
- **Now:**
  - The External IDs are values, in one set per Object Type and value type (`ImportedExternalIds`). Text is kept exactly as read and folded only when the caller asks to ignore case, so both readers compare as before whichever case-exact declaration they pass (#2051). The sets are released after the Unconfirmed Create retry, before the save phase.
  - Measured with 100,000 objects (`ImportedExternalIdsTests`, explicit): 252 bytes an object as attributes against 44 as values for a Guid External ID, and 319 against 127 for a 36-character text one (the string itself is about 96 of those). At 525,000 objects that is about 130 MB down to 23 MB, held until deletion detection rather than through the save phase.
  - Candidates are `ConnectedSystemObjectExternalIdMatch` summaries (id, status, Object Type, how many objects hold the value). `GetConnectedSystemObjectExternalIdMatchesAsync` resolves 1,000 missing values a statement. Applying works 1,000 at a time: stale Pending Exports are cleared by id in one statement, and only the objects to mark are loaded whole (`GetConnectedSystemObjectsForDeletionDetectionAsync`, with the lookup's graph and tracking). A refused run loads none. The objects marked still join the run's updates until the save phase, which is Phase 5's to bound.
  - Replacements are held only in `_objectTypeChangesCarriedOut`, counted in the harness sample, and logged when deletion detection begins. `ReconcileCarriedOutObjectTypeChanges` adds the ones that stand to the creates.
- **The resolution is the old lookup's, which is wider than deletion detection's own scope:** any Object Type, Partition or status, first by id. A missing External ID held also by an object in another Partition, or by a Pending Provisioning object, resolves to whichever has the lower id, and that object is the one marked. Kept exactly, as the phase changed no behaviour. #2948 has since scoped the resolution to what deletion detection compared (the Object Type, the run's Partition, not Pending Provisioning); it found that a Pending Provisioning object with the lower id was itself marked deleted. `FullImportDeletionDetectionDatabaseTests` pins first-by-id with two objects in one Partition, and that case.
- **A candidate named twice is marked once:** two missing values can resolve to one object (text the database folds where .NET does not). The run's tracked context returned the same instance twice, so the second application found it Obsolete; the batch keeps that by noting what it has marked.
- **Guards:** `FullImportDeletionDetectionDatabaseTests` (committed passing on the old code first: applied and refused verdicts on PostgreSQL across case, duplicates, Obsolete, Pending Provisioning, Number and Guid anchors) and `DeletionCandidateLookupDatabaseTests` (the batched resolution against the single-value lookup for every value type, and the loaded graph against the lookup's). Reversing the lookup's order fails three of them.
- **Scenarios** (OpenLDAP): 004, 009, 007 and 001 at Small and 021 at Nano, all passing. Scenario 021's Test 2 needs one CSV row to be more than 10% of the data, so it fails its own precondition at Small (100 rows) on any code; Nano is the template it documents.
- **The per-value lookups left the sync repository:** deletion detection was their only caller there. The Connected System repository keeps them; its tests and the equivalence guard use them.

### Phase 4: The import-incomplete marker (behaviour change) ✅

- A nullable time on Connected System (additive migration), set before the first write and cleared on completion, including completion with deletions refused by the limits.
- Full and Delta Synchronisation of that Connected System refuse, with a message naming the stopped import and the remedy.
- Tests: a Full Import failing in its save phase leaves the marker set (red today); synchronisation refuses and says why; deletions refused by the limits do not set it; a `RequiresPostgres` round trip of the column.
- Scenarios: 021, and the full suite at Medium.

What was found doing it:
- **The column is `ConnectedSystem.ImportIncompleteSince`**, the UTC time the earliest unfinished import began writing. It is written by two narrow statements on `ISyncRepository` (`MarkImportIncompleteAsync`, which keeps a time already held, and `ClearImportIncompleteAsync`), each committed at once, so however a run stops after marking (a failure, cancellation, the memory guard's stop or abort, the process ending) the marker stands. On the synchronisation repository, `ReadOnlySyncRepositoryGuard` refuses both, so a preview can neither set nor clear it.
- **Where the first write is:** on a run that read every object, deletion detection (its stale Pending Export cleanup, then the Unconfirmed Create retry); otherwise the save phase. `MarkImportIncompleteBeforeFirstWriteAsync` is called before both, once per run; Phase 5 calls it before its first unit. A run that fails or is cancelled while the Connector reads writes nothing, and leaves the Connected System unmarked. A run with nothing to save does not mark.
- **Delta Import has the same hazard today and sets it too.** It saves through the same code, in batches that commit one at a time: a reference whose target is in an unsaved create batch is saved unresolved (the cross-batch fix-up runs only after the last create batch), and a synchronisation then removes it. Every import takes down the marker it set itself when it completes, since what it wrote is then whole.
- **Who clears a marker an earlier stopped run left:** only a completed run that read every object of the whole Connected System: a Full Import, or a Delta Import whose Connector read every object (#2720), with no Partition scope, that read at least one object or found the Connected System empty. Detected deletions refused by the limits do not stop it (the objects stay as they were, not half-saved). A Delta Import that reads changes cannot repair what it never re-reads, and a run that read nothing re-read nothing; both leave the marker and say so in an Activity warning (`ImportCompleteness.DescribeStillIncompleteAfterImport`), so the waiting is visible before a synchronisation is refused.
- **Partition scope:** one time cannot say which Partition a stopped run was writing, so a Partition-scoped run never clears a marker it did not set, and the remedy asks for a Full Import reading all selected partitions. Recording the scope (a Partition id beside the time, or a marker per Partition) would let a Partition-scoped Full Import clear its own Partition's stop; not built, as nothing yet shows customers run only Partition-scoped Full Imports.
- **The refusal** is in the Worker beside the disabled Run Profile refusal (#2740), before any step is declared: the Activity ends Failed with error with the Run Profile, the Connected System, the stopped import's time, the risk and the remedy, and a Schedule records the step as failed and follows its failure rules. Full and Delta Synchronisation refuse; imports (the remedy) and exports (which send what earlier synchronisations decided) run.
- **Clearing the Connector Space clears it,** in the same statement that resets the persisted connector data; deleting the Connected System removes it with the row. There is no way to clear it by hand on any surface.
- **A portal save of the whole Connected System would have written back the marker the page loaded:** `UpdateDetachedSafe` marks every column modified, so a settings save from a page loaded before an import marked the system would take the marker down, and one loaded before a Full Import cleared it would put it back. `MarkConnectedSystemGraphForUpdate` leaves the column out (`RequiresPostgres` tests, red without it). The same latent overwrite applies to the other runtime columns on the row (`PersistedConnectorData`, `StrandedValueSweepArmedAt`, `LastSuccessfulFullImportCompletedAt`), left as they are here.
- **Surfaces:** the REST detail and list DTOs carry `importIncompleteSince`, so `Get-JIMConnectedSystem` returns it on both (a help example lists the systems waiting), and the Connected System page shows a warning with the remedy.

### Phase 5: Stage each unit of a Full Import (behaviour change) ✅

- The unit accumulator, the value budget (reusing `ValueBudgetedBatchPlanner`), per-unit resolution with forward references saved unresolved, the end-of-run sweep and late reporting, in the order above. Full Import only; Delta Import is unchanged.
- Tests (red first): the harness counter is flat in N; a group on page 1 referencing a user on page 3 resolves; still-unresolved references are reported on the saved item for each of Error, Warn and Ignore; the sweep follows declared target types and case rules; a run stopped after unit 2 keeps its staged objects and watermark and detects no deletions; running again completes with no duplicate change records; the Unconfirmed Create retry and deletion detection find the same candidates after staged pages; a reference to a #2049 replacement resolves as today; the value budget splits a page of large groups. `RequiresPostgres` tests for two units on one context with tracked instances present, and for the sweep.
- Scenarios: 008 (Samba AD at Medium, OpenLDAP at Large, then Scale500k25kGroups), 032, 016, 015, 028, 029, 004, 021, 009, 001, 014, 017; 024, 025 and 034 in the AD lab after merge. Add an integration check of the Unconfirmed Create retry.

What was found doing it:
- **Where it lives:** `SyncImportTaskProcessor.Staging.cs`. A page that fits in the unit being built is processed whole, exactly as before; the unit is saved first when the page would take it past `ImportBatchSize` (2,000) objects or `ImportUnitValueBudget` (250,000 values, `DefaultImportUnitValueBudget`). Only a page larger than a unit on its own (a file, a large LDAP Page Size, a page of large groups) is split, and it is classified first as a whole, without sweeping its empty values (`FindDuplicateExternalIds(..., sweepNullValues: false)`), so its copies are rejected exactly as on one page (#2063), and each part sweeps its values where processing the page would have. A unit is saved by the single save phase's code (`StageImportedObjectsAsync(stagingUnit: true)`): the creates' committed ids run across units, the batch progress is left to the read, and the cross-batch fix-ups wait for the end of the run.
- **The database reference fallback loaded whole objects, tracked.** With one save at the end it only ever found objects saved before the run; with units it is the main path, since a unit of groups names members earlier units saved, and each lookup loaded every member with every value onto the run's context, which a create-only unit never cleared. A unit asks for ids alone (`GetConnectedSystemObjectIdsByAttributeValuesAsync`, `GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync`, the same matching, 5,000 values a statement) and sets only `ReferenceValueId`; the writers and the change records take the id, so nothing needs the navigation. Delta Import keeps the whole-object lookup.
- **Forward references:** a value found neither in the unit nor in the database is saved by its identifier, and its object is noted (`OutstandingReferenceHolder`: its id, its item's id and error as written, and its stamp). The sweep reads the noted objects' outstanding values 1,000 objects at a time (`GetOutstandingReferenceValuesAsync`), resolves them by the unit's rules, and writes each id and the matching value of the change record the run wrote for it, which loses its unresolved mark (`ApplyOutstandingReferenceResolutionsAsync`, fixing up tracked instances). What is left is reported once per object (#959) on the item already saved (`UpdateUnresolvedReferenceReportsAsync`, a narrow statement): the count always, and under Error the error where the item recorded none when it was written, as resolution before the save always took the slot first; the Activity's error total is raised by those, and the Warn summary is written once, after the sweep.
- **A noted object's stamp is held back to the sweep,** and written then by the save phase's rule. A run that stops before the sweep leaves it unstamped, so the next Full Import compares it rather than skipping it by its hash with a reference still outstanding. Everything else a stopped run saved is stamped and skipped, so the next run records nothing for it again.
- **#2049 replacements:** a unit's reference to a replacement held apart is noted, not resolved to the object the replacement retires, which the database still holds under the same identifier; the sweep resolves it to the replacement first, as the single save's in-memory resolution did. That resolution also held the retired object when deletion detection newly marked it this run, so an undeclared reference was ambiguous between the two (characterised on the old path); the sweep reports it the same way, and resolves to the replacement where the retired object was already Obsolete.
- **#2049 references read before the change:** a unit saved before the object whose type changes has been read resolves its reference through the database to the held object, as nothing marks the change yet. Left there, the single save's ambiguity became a silent wrong reference; on PostgreSQL it was worse for a created object, whose writer drops an id outside the run's committed set, so the value was left with no id and no report. On a run that allows Object Type changes, each unit keeps the references it resolved through the database to objects the run has not read (target to holders, with each holder's item id, error and unresolved count as written, and its stamp held as a noted object's is); one read this run under its own Object Type is not a deletion candidate, so no change can retire it, and a dependency-ordered read keeps almost nothing. The sweep takes the kept references to objects a standing change retired, re-opens them (`ReopenReferencesToRetiredObjectsAsync`: the value loses the id, and the item's change record value loses it and is marked unresolved again), and adds their holders to its notes, so its own rules decide them exactly as above: ambiguous where the retired object was newly marked this run, the replacement where it was already Obsolete. The report adds to the count the item recorded when written. A run that does not allow Object Type changes keeps nothing (`ReferencesKeptForObjectTypeChangeRecheck`).
- **Reconciliation** carries on from unit to unit: begun by the first unit that updates anything (its Pending Export count can include stale exports deletion detection later removes; no batch loads those), with the objects saved more than once found per unit. No fix-up runs between a unit's save and its reconciliation, so what it holds is what it saved; a forward reference is judged by its identifier, which `ReferenceRowMatches` already compares for an unresolved row.
- **Deletion detection and the Unconfirmed Create retry** run over the ids queued for update over the run. The objects deletion detection marks are saved a unit at a time once a unit's worth are held. The standing replacements and what is left are saved before the sweep.
- **Steps:** units are saved inside the fetch step. A Full Import then enters Deletion detection, Resolving references (the sweep), Saving changes (the cross-batch fix-ups of the values and change records), Reconcile and Recording results.
- **Measurement hooks:** each unit logs `StageUnitAsync: Unit N saved` with the GC heap, the live heap after the last full collection (no collection is forced) and the working set; `PeakUnitBoundaryLiveHeapBytes` holds the largest, and the sweep's phase timing logs it with the unit count. `FullImportHeldValuesHarnessTests` now asserts the counter flat in N.
- **Tests:** `FullImportStagedUnitsTests` (references across units, late reporting under each handling, declared target types and case rules, a stopped run and the run after it, deletion detection and the retry, #2049 replacements, references read before the change in both outcomes and what a run keeps for the recheck, the value budget), `FullImportStagedUnitsDatabaseTests` (a run over four units on one tracking context holding the objects it updates, the sweep's writes to values, change records and items, then a second run that records nothing; and a group read before two Object Type changes, one of an Obsolete user, rechecked to the replacement and to an ambiguity, change records included) and `ReferenceSweepRepositoryDatabaseTests` (the id-only lookups against the whole-object ones for every anchor type, case rule and the Secondary External ID, and each new writer's round trip, on a context tracking what it writes). Each workflow case was run first against the single save phase (staging switched off) and failed there on its unit assertions, while its outcome assertions passed, characterising them; the repository tests guard new methods. `ConfirmingImportReconciliationDatabaseTests` changed where it assumed the fix-up runs between a batch's save and its reconciliation: Beta is now judged from what it holds, and the later-batch failure is injected where that batch's Pending Exports load.
- **The in-memory store** gives a reference value written by id alone its object on create and update, as a database read that includes the reference does: the workflow tests read the navigation.
- **Scenario 001** has an `UnconfirmedCreate` step: a Create exported and deleted in the directory before any import reads it is reported Export Not Confirmed by the next Full Import, marked for retry, and created again by the next export.
- **Differences from a single save phase, accepted or to know:**
  - A referenced object whose Secondary External ID changed in an earlier unit is compared by its saved name: the referencing object no longer removes and re-adds the reference, and a reference to the new name resolves at once rather than waiting for the next run's fix-up. The stored row keeps the old name in `UnresolvedReferenceValue`, with the right id.
  - The in-memory duplicate anchor check (`DuplicateAnchorValueError`) covers a unit, not the run; two objects of one type with the same Secondary External ID in different units no longer fail the run, and the database lookup takes the first and logs it.
  - An undeclared primary-anchor reference matching objects of two types, one in the unit and one earlier, is ambiguous where the single save resolved it to the object in memory. A Connector with type-agnostic identifiers (LDAP DNs) does not reach this.
  - The Activity's totals are counted per flush, as before, and a flush is now per unit; where outcome tracking is on and an update batch mixes status-only transitions with changed objects, its totals can differ from a run whose batches were cut elsewhere, as they could between two `ImportBatchSize` values before.
- **Scenarios** (OpenLDAP, in the sandbox): 008 at Large (10,527 objects, six units an import; the confirming import's largest live heap at a unit boundary 109 MB), 032 at Large (the first import saved 9,900 objects with 55,150 references to objects later in the read; the sweep resolved every one in 4.2 s), 001 at Small (with the new `UnconfirmedCreate` step), 021 at Nano, 004 at Nano, 009, 014, 015, 028 and 029 at Small: all passed, and 004 again with the Object Type change recheck (no scenario enables Object Type changes, so the workflow and `RequiresPostgres` tests are what cover it). 017 is Samba AD only and 016 needs the SQL Server and Oracle images, which did not fit the sandbox's disk; both are left to the lab with 008 on Samba AD at Medium, Scale500k25kGroups, and 024, 025 and 034.
- **To measure at 500k:** the id-only lookups replace in-memory resolution for every reference to an earlier unit (10.9 million member values at Scale500k25kGroups, about 2,200 statements of 5,000); their cost against Phase 0's reference resolution timing is the wall-clock risk, along with the per-unit reconciliation count and progress writes.

### Phase 6: Held-reference answers for Full Import (moved to #2964)

- Extending the held-reference questions Delta Import asks (#2817, #2836) to Full Import, to cut hydration and comparison on confirming imports, is no longer part of this plan: it moved to issue #2964, its own PR with measurements.

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
