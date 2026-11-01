# Connected System Object Pages Off EF Core

- **Status:** Doing (Phase 0 underway: diagnostics and measurement variants in; the scale run is outstanding)
- **Issue:** [#2669](https://github.com/TetronIO/JIM/issues/2669)
- **Last Updated:** 2026-11-01
- **Follows:** [Synchronisation Hot Path Off EF Core's Change Tracker](../done/SYNC_HOT_PATH_OFF_EF_TRACKER.md) (#2598), whose Phase 4 deferred this load "until a scale run shows the linear cost matters"

## Overview

A synchronisation page loads its Connected System Objects with a tracked EF Core query that `Include`s every attribute value. A modified group therefore brings its whole membership through EF's materialiser and change tracker, at 15 to 18 µs per value against about 1.3 µs to read the same rows. #2649 made that cost linear but not small. In the Scale500k25kGroups leaver cohort, it was about 21 s of the confirming Delta Synchronisation's 30 s of page loads.

This plan replaces that load with a raw SQL reader into untracked objects, as #2598 Phase 2 did for the page's Metaverse Objects. A raw reader alone is not safe. What tracking really provides the page is EF's identity map (one instance per row), plus the tracker fix-ups that keep those instances in step with raw writes. So the plan also replaces three things tracking was doing:

- the later EF loads on the same page that return the page's tracked instances move onto an explicit page identity, or are replaced by lighter reads;
- the tracker fix-ups are extended to that identity;
- the one EF graph write that can reach a page object is closed off.

Phase 0 is a measurement gate. If the next scale run does not confirm the cost on the Worker's own context, the work stops there and this plan records why.

## Business Value

- **The problem:** groups are the objects customers care most about synchronising, and a group's page load costs its membership. One 100,000-member modified group takes 2.4 s to load, 1.8 s of it EF. A leaver cohort, a re-organisation or a large group change makes thousands of groups "modified" at once. In the 31 October run that one Delta Synchronisation spent 30.2 s in page loads, out of 78.7 s.
- **Who it affects:** every Connected System with large groups:
  - every Full Synchronisation page of groups;
  - every Delta Synchronisation that touches them;
  - the cross-page reference step for nested groups.
- **Why now:** #2598 removed every larger cost on the page, and #2671 added `Cso.Page.LoadModifiedWithAttributeValues` with an `attributeValueCount` tag, so the volume can be measured directly. What remains is the page's largest per-value cost spent in .NET rather than in the database.

## Measured Baseline

| Measurement | Value | Source |
|---|---|---|
| Leaver cohort, Target confirming Delta Synchronisation, `LoadModifiedCsoPage` | 30.2 s over 22 pages | Scale500k25kGroups, 31 October |
| of which `Cso.PopulateReferenceValues` | 4.5 s | same |
| of which `Cso.LoadMetaverseObjects` (already raw, #2598 Phase 2) | 4.4 s | same |
| under no child span (the main EF query, by elimination) | about 21 s | same |
| that Delta Synchronisation, whole | 78.7 s | #2598 Phase 5 |
| Full Synchronisation, one 500,000-member group page, `Cso.Page.LoadWithAttributeValues` | 4.6 s for 525,000 values (8.8 µs each) | 31 October |
| Full Synchronisation, slowest page load | 6.1 s for 591,000 values (10.3 µs each) | #2598 Phase 5 |
| One modified group through `TypeOrderedCsoPager.NextPageAsync`, at 25,000 / 50,000 / 100,000 members | total 575 / 1,047 / 2,419 ms; main EF query 266 / 758 / 1,763 ms; raw read 27 / 63 / 128 ms | `LeaverCohortCostDatabaseTests` |

Two notes on reading these:

- **The per-value cost still rises with size.** The main query costs 10.6 µs per value at 25,000 members and 17.6 µs at 100,000, so a million-member group will cost more than a linear projection says.
- **The raw figure is a floor, not the reader's cost.** The test's raw read takes one column per row and builds nothing. A reader that builds objects costs more, and Phase 0 measures that before anything changes.

## Technical Architecture

### Current state

#### What the page load returns

Every page comes from one of two loaders:

- **Full Synchronisation:** `ConnectedSystemRepository.GetConnectedSystemObjectsAsync`. A Delta Synchronisation also uses it for an Object Type processed in full.
- **Delta Synchronisation:** `GetConnectedSystemObjectsModifiedSinceAsync`, which selects on the three marks, `Created` or `LastUpdated`.

Both are called through `TypeOrderedCsoPager.NextPageAsync`, which walks a keyset per Object Type in dependency order and then a closing sweep. The sweep reloads objects created since the run's floor and drops those already handed out.

Other callers of the same load:

- **The cross-page reference step** reloads through `GetConnectedSystemObjectsForReferenceResolutionAsync`, which has the same shape.
- **Synchronised Deprovisioning, the Full Synchronisation preview and the Synchronised Deprovisioning preview** also page through `GetConnectedSystemObjectsAsync`.

Each load returns:

- **The Connected System Object rows, tracked.** The Worker's context is `TrackAll`, and the Delta loader also says `AsTracking()`. They come with `Type` and `AttributeValues.Attribute` in one SQL statement, not a split query.
- **No `ReferenceValue` navigation.** It is not included. Each value instead gets three unmapped fields (`ResolvedReferenceMetaverseObjectId`, `ResolvedReferenceTypeId`, `ResolvedReferenceCreated`) from a second statement, `PopulateReferenceValuesAsync`: `SqlQueryRaw` over `ANY` of every distinct referenced id.
- **The joined Metaverse Objects, untracked.** They come from `MetaverseObjectRawReader` and are stitched to their objects in both directions.
- **Nothing else.** No `ConnectedSystem`, `Partition`, `Changes` or execution-item collections; nothing on the synchronisation path reads them.

`ClearChangeTracker` empties the whole tracker at each page boundary. So from page 2 onwards each page's `Type` and `Attribute` instances are fresh, not the instances the run's Synchronisation Rules hold. The processors turn `SetAutoDetectChangesEnabled(false)` on from the page's load to its tracker clear, and restore it in a `finally`.

#### What tracking the page buys, and what depends on it

Nothing on the synchronisation path writes a page's Connected System Objects or their values through EF. Every write is raw SQL:

- join state and the guarded synchronisation-pending clear (`UpdateJoinStatesClearingSynchronisationPendingAsync`, then `BulkUpdateConnectedSystemObjectJoinStatesRawAsync`);
- derived-input marks and scope-review clears;
- deletion (`ConnectedSystemObjectDeletionSql`, #2671);
- Pending Exports (`CreatePendingExportsAsync`, COPY);
- Run Profile Execution Items (`BulkInsertRpeisAsync`, scalar foreign key);
- change records (`PersistCsoChangesAsync`, raw hierarchy insert);
- the page's Metaverse Objects (#2598).

The only EF updates of Connected System Objects (`UpdateConnectedSystemObjectsAsync` and the `UpdateConnectedSystemObject*WithNewAttributeValues*` pair) belong to import and export execution.

Tracking is load-bearing in six ways. Each one has to be replaced before the page can be untracked:

- **A. Later tracked loads resolve onto the page's instances.** Several loads run while the page is held. When they reach a page row, EF's identity map returns the page's own object and values, so their mutations, and any fix-up applied to what they return, land on the page's instance. The loads are:
  - the export evaluation cache (`GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync` and `GetCsoAttributeValuesByCsoIdsAsync`). It reaches page rows because it covers "every distinct target system, the run's source included (#1284)". The values load also `Include`s `ReferenceValue`, so it loads and tracks a large group's member objects too;
  - export evaluation's per-object `GetConnectedSystemObjectByMetaverseObjectIdAsync`, used by deprovisioning and the uncached staging path;
  - `GetConnectedSystemObjectForExportMatchAsync`, documented as "tracked so a caller can fix up the tracked instance after an atomic claim";
  - the Metaverse Object deletion fetch (`GetConnectedSystemObjectsForMvoDeletionAsync`);
  - contributor re-election's survivor discovery (`GetConnectedSystemObjectsByMetaverseObjectIdAsync`). It loads every Connected System Object joined to the Metaverse Object, the page's own included, with all its values.

  Several of these say "AsNoTracking" in their comments, but they are tracked on the Worker ([#2574](https://github.com/TetronIO/JIM/issues/2574)).
- **B. EF fix-up wires same-page references.** A value whose `ReferenceValueId` names another tracked object on the page gets `ReferenceValue` set without any `Include`. Three places rely on that navigation:
  - Attribute Flow uses it to wire a reference to a Metaverse Object projected on the same page, which has no id yet (`SyncEngine.AttributeFlow`, "When the referenced MVO is available as a tracked navigation");
  - `IsUnresolvedReference` falls back to it;
  - `CannotResolveThisRun` falls back to it.

  Since #2492 pages one Object Type at a time, this arises for self-referencing types: nested groups and managers, which Scenario 032 exercises.
- **C. Tracked Pending Exports get their `ConnectedSystemObject` navigation filled in.** This applies to the prefetch and the lightweight loads, and only name fallbacks read it.
- **D. Raw writes fix up tracked instances.** Several fix-ups scan the tracker so the page's instances stay equal to the database:
  - `ConnectedSystemObjectMarkFixUp`, at four sites;
  - `DisconnectConnectedSystemObjectsAsync`;
  - `ConnectedSystemObjectDeletionSql.UpdateTracker`, which detaches, clears references and removes from collections;
  - `ReflectDeletedMetaverseObjectsInTracker`.
- **E. EF graph writes stop at tracked objects.** `ActivitiesRepository.CreateActivityRunProfileExecutionItemsAsync` calls `AddRange` on execution items. It severs their change-record navigations but not `item.ConnectedSystemObject`, which `ConnectedSystemObjectObsoletionService` sets. Today the walk stops at the tracked object. Synchronised Deprovisioning severs the navigation at its own call site; nothing in the repository does.
- **F. Shared schema instances.** The page's `Type` and `Attribute` instances are the ones any other tracked query on the page resolves to, for example the Pending Export prefetch's `avc.Attribute`, so no duplicate schema instance exists to collide with on an attach.

Tracking also costs beyond the load. Every fix-up in D enumerates the whole tracker (`ChangeTracker.Entries()`). With a 500,000-value page tracked, each deletion flush, Metaverse Object deletion and mark fix-up walks 500,000 entries; `Cso.Delete.UpdateTracker` already reports this as `entriesScanned`.

#### What a group's full value list is read for

| Consumer | What it reads | Could it take only the changed values? |
|---|---|---|
| Attribute Flow, reference attribute | Every resolved member: it computes removals (Metaverse Object values not in the set) and additions, and `hasUnresolvedReferences` over all of them | No: with a partial list, every unloaded member becomes a removal |
| Attribute Flow, other multi-valued attributes | The whole set (`ApplyTypedValueSet`) | No |
| Expressions | `BuildCsoAttributeDictionary`: every attribute's values | No |
| Reference resolution and cross-page deferral | `ResolvedReference*` per mapped reference value; the deferral count | No |
| Drift Detection | The whole target attribute, compared as a set with the expected values | No |
| Export evaluation | Changed Metaverse Object values (already a delta), plus the target object's whole set for the no-net-change check, through the export cache | The Metaverse Object side already is; the target side is not |
| Execution item snapshots, Object Matching, scoping | A handful of values | Yes, but these are not the cost |
| Change records | Built from Metaverse Object pending additions and removals | Not a reader of the Connected System Object's values |

A Connected System Object carries no record of which values changed since the last synchronisation. Its change records are an optional audit trail, off under `-DisableChangeTracking`.

### Options considered

| Option | Projected main-load cost | What it takes on | Verdict |
|---|---|---|---|
| (a) Raw untracked page reader, with A to F replaced | about 2 to 3.5 µs per value (Phase 0 confirms) | Every hazard above, each replaced explicitly | **Recommended** |
| (a') EF `AsNoTracking` with the same `Include`s | about 8 µs per value (#2598 Phase 4: untracked was about half the tracked cost) | The same hazards as (a) | Rejected: the same risk for half the gain |
| (b) EF loads the rows, raw read of the values, attach them | 7.3 to 9.9 µs per value, measured in Phase 0 | Little new risk | Rejected: about three times (a), and every value stays tracked |
| (c) Load only the changed values | n/a | A new delta model end to end | Rejected: incorrect as stated |
| (d) Nothing beyond #2649 | 15 to 18 µs per value | Nothing | Only if Phase 0 shows the volume is small |

**(a) Raw untracked reader.** The page's objects become the only instances of their rows, unless a load bypasses them. That bypass is the real hazard: two instances of one row, with a mutation or fix-up landing on the wrong one. EF does not throw; it silently diverges. Each hazard has a replacement:

- **Shared schema entities (F):** no duplicates are created, because the reader resolves `Type` and `Attribute` through the context as `MetaverseObjectRawReader` does, so they are the tracked instances and a graph walk stops at them.
- **Identity fix-up (A, C):** replaced by a page identity that the converted reads consult.
- **Same-page reference wiring (B):** the reader reproduces it, setting `av.ReferenceValue` to the page instance when the target is on the page.
- **`ReferenceIndexedList`:** built once per object from the read rows. EF never touches an untracked list, so its membership check is no longer on the load path at all.
- **`Add`, `Update` or `Attach` reaching a page row (E):** closed in the repository.
- **Change detection:** stays off for the page. A tracked Activity's `RunProfileExecutionItems` would otherwise lead detection to the page's objects.

**(b) Raw values attached to EF-loaded rows.** #2598 Phase 0 measured attaching values at 16 µs each for a 1,000-value object and 29.6 µs each at 10,000. The cost was identity-map hashing and the fix-up of each value's reference to a member the context does not track, which is exactly a group's case. Only its list-scan part was later removed. That projected raw read plus attach at about the tracked query's cost. Phase 0 measured it at 7.3 to 9.9 µs per value, references included: better than projected, and cheaper than (a'), but still about three times (a). It also leaves every value tracked, which keeps every tracker scan in D just as large. Those two reasons, not the attach cost alone, are why it is rejected.

The same evidence rules out a hybrid: rows attached with `Entry(cso).State`, values untracked. Any tracked load of a page object's values would then fix new tracked value instances into the tracked object's `AttributeValues` beside the raw ones. The group would hold every member twice, and `ExternalIdAttributeValue`'s `SingleOrDefault` would throw. That is worse than either pure option.

**(c) Changed values only.** As the table above shows, inbound Attribute Flow and Drift Detection need the whole set. Without it, a group's unloaded members would be computed as removals and exported, which is mass corruption. Making (c) correct would need three things:

- a mandatory, durable per-value change log from import;
- a Delta Synchronisation that trusts that log, so that a lost entry becomes a silent membership error, against Synchronisation Integrity;
- a Full Synchronisation that still reads the full set, so (c) would not touch its cost at all.

The useful half of (c), reading the full set at raw cost, is already in (a).

**(d) Do nothing.** About 21 s per such Delta Synchronisation at 500,000 users, rising faster than linearly per value with group size. It carries no risk, and it is the right answer if Phase 0's thresholds are not met.

Against the Solution Quality Principles, (a) is the only option right on all three axes:

- **User experience:** large-group synchronisations finish faster.
- **Architecture:** the whole page has one persistence model, and one explicit identity per row in place of an implicit one.
- **Performance:** the load becomes database-bound.

(a') and (b) are the half-measures.

### Proposed solution

1. **`ConnectedSystemObjectRawReader`** (JIM.PostgresData). One implementation sits behind both page loaders and the cross-page reload, mirroring `MetaverseObjectRawReader`:
   - **Statement 1:** `CsoBulkColumns.ConnectedSystemObjects` plus `xmin`. It applies the keyset cursor, the Object Type, the creation floor and either every object or the modified-since predicate, then `ORDER BY "Id" LIMIT @pageSize`. The predicate is written once, and a test pins it to the count queries. The watermark parameter is typed exactly as EF types it under `Npgsql.EnableLegacyTimestampBehavior`.
   - **Statement 2:** `CsoBulkColumns.ConnectedSystemObjectAttributeValues`, `LEFT JOIN` the referenced object for `MetaverseObjectId`, `TypeId` and `Created`, filtered by owner `= ANY(@pageIds)` and ordered by owner then `Id` (EF's order for the `Include`). This replaces `PopulateReferenceValuesAsync` and its `ANY` of every referenced id.
   - **Wiring:** types and attributes are loaded by id through the context. The reader wires `Type`, `AttributeValues` (one `ReferenceIndexedList` per object), each value's `Attribute`, `ConnectedSystemObject` and three `ResolvedReference*` fields, and `ReferenceValue` set to the page instance for in-page targets. An unmapped column fails the load.
   - **Then, unchanged:** `LoadMetaverseObjectsForCsosAsync`.
   - **Order:** Connected System Object rows and `xmin` first, values second, Metaverse Objects third. This preserves the `CaptureMarkedRowVersions` invariant. Under READ COMMITTED, a write landing between statements 1 and 2 can only make the mark's guarded clear fail safe.
2. **A page identity for Connected System Objects,** held by `SyncRepository`:
   - it holds the latest page load's objects, and the next load replaces them;
   - `ClearChangeTracker` and `ClearChangeTrackerRetaining` clear it, so its lifetime is the tracker's, as `MetaverseObjectPageIdentityMap`'s remarks require;
   - it counts each time a tracked load materialises a page row. That count is a tripwire for a load that bypasses the identity, logged at Warning and asserted zero in tests.
3. **The bypassing loads are converted** to raw reads at the lightest tier of the Entity Retrieval Naming Taxonomy that serves each:
   - the export evaluation cache: Core rows and a values read, with no `Include(ReferenceValue)`;
   - export evaluation's per-object load and export matching: Core rows plus values;
   - the Metaverse Object deletion fetch: Core rows plus external-id values;
   - re-election discovery: Core rows only (`GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync` exists), so the existing hydration loads values for survivors only.

   Each returns a page row as the page's instance and reads only the rows the page does not hold. A large group whose own system is an export target is then read once, not twice.
4. **One accessor for "the Metaverse Object this value references":** `ResolvedReferenceMetaverseObjectId` first, then the navigation. Attribute Flow, `IsUnresolvedReference`, `CannotResolveThisRun`, `HeldAttributeValues` and `ValuesMatch` all use it, so no reader depends on how the value was loaded.
5. **Fix-ups reach the page identity.** `ConnectedSystemObjectMarkFixUp`, `DisconnectConnectedSystemObjectsAsync`, `ConnectedSystemObjectDeletionSql.UpdateTracker` and `ReflectDeletedMetaverseObjectsInTracker` each apply to the identity's instances as well as the tracker's. That includes removing deleted objects from their page Metaverse Object's `ConnectedSystemObjects`, and clearing page values that referenced them.
6. **`CreateActivityRunProfileExecutionItemsAsync` severs `item.ConnectedSystemObject`,** keeping the foreign key, as it already severs change-record navigations.
7. **Removals** (pre-release: remove, don't deprecate):
   - `PopulateReferenceValuesAsync` and both EF `Include` queries;
   - the OFFSET path of both loaders, since every production caller pages by keyset; the in-memory fake and tests follow;
   - `GetInScopeImportRulesAsync`'s reload of a page object with no values.

   Stale comments are rewritten: the "AsNoTracking" claims, the processors' January 2026 design note on `AsSplitQuery`, and the remarks on `LoadMetaverseObjectsForCsosAsync` and `CaptureMarkedRowVersions`. src/CLAUDE.md "Raw SQL Writes Must Fix Up or Detach Tracked Instances" gains a line: page objects are untracked, and fix-ups go through the page identity.

Out of scope:

- the Metaverse Object reader (already raw);
- import's hydration loads;
- portal and REST API reads;
- option (c);
- reading statements in parallel.

### Data flow

1. The processor turns change detection off (unchanged).
2. The pager calls the loader. Statement 1 reads the rows and `xmin`, and statement 2 reads the values and resolved references. Schema is resolved through the context, the objects are wired, and the page identity is replaced with this page's objects.
3. The Metaverse Objects are read raw (unchanged). Then `_mvoIdentityMap.Seed` and `CaptureMarkedRowVersions` run, using statement 1's `xmin`.
4. Pass 1 and Pass 2 change the page's instances in memory only. Same-page references flow through the reader's `ReferenceValue` links, as they flowed through EF's fix-up.
5. The flush writes raw, as today:
   - each write's fix-up updates the tracker and the page identity;
   - reads during the flush (the export cache, the deletion fetch, export matching, re-election) resolve page rows to the page's instances.
6. The tracker, the page identity and the Metaverse Object identity map are cleared together, and detection is restored in the `finally` (unchanged).

## Implementation Phases

### Phase 0: Confirm the cost on the Worker, and decide

Deliverables:

- **A `valueCount` tag on `Cso.LoadMetaverseObjects`,** so the existing raw reader's cost per value is known. That figure is the projection for the new reader.
- **The tracker entry count logged before the clear,** as well as after, to show the peak tracker size.
- **More variants in `LeaverCohortCostDatabaseTests`** (still `[Explicit]`), at 25,000, 100,000 and 400,000 members:
  - EF `AsNoTracking` (a');
  - raw values with `Entry(av).State = Unchanged` (b);
  - a full-column raw read that builds objects, resolves attributes and fills `ReferenceIndexedList`, which is the realistic floor for (a).
- **One Scale500k25kGroups Scenario 008 run on OpenLDAP,** with #2671's span. For each synchronisation, record:
  - the summed time and `attributeValueCount` of `Cso.Page.LoadModifiedWithAttributeValues` and `Cso.Page.LoadWithAttributeValues`;
  - `Cso.LoadMetaverseObjects` cost per value;
  - `Cso.Delete.UpdateTracker` `entriesScanned`.

The diagnostics are in: `Cso.LoadMetaverseObjects` carries `valueCount`, and each page's `Page n/m complete` line carries `TrackerBeforeClear` beside `Tracker`, at Information.

#### Measured (local fixture)

`LeaverCohortCostDatabaseTests`, one modified group loaded each way; the median of five runs after a warm-up, in ms, with µs per value in brackets. A group of n members has n + 1 values. Host: 4 vCPU Intel Xeon at 2.10 GHz, 16 GB, PostgreSQL 18.6 in Docker on default settings, with the JIM stack running alongside.

| Load | 25,000 | 50,000 | 100,000 | 400,000 |
|---|---|---|---|---|
| Today, tracked, `NextPageAsync` whole | 491 (19.6) | 964 (19.3) | 2,221 (22.2) | 10,037 (25.1) |
| of which the main query (`Cso.Page.LoadModifiedWithAttributeValues`) | 350 (14.0) | 534 (10.7) | 1,345 (13.5) | 6,576 (16.4) |
| of which `Cso.PopulateReferenceValues` | 86 (3.5) | 109 (2.2) | 250 (2.5) | 1,603 (4.0) |
| of which `Cso.LoadMetaverseObjects` | 88 (3.5) | 316 (6.3) | 576 (5.8) | 1,974 (4.9) |
| Main query and reference lookup together | 413 (16.5) | 647 (12.9) | 1,619 (16.2) | 8,063 (20.2) |
| (a') EF `AsNoTracking`, the main query alone | 209 (8.4) | 428 (8.6) | 967 (9.7) | 4,812 (12.0) |
| (b) EF rows, raw values attached `Unchanged`, references joined | 181 (7.3) | 376 (7.5) | 826 (8.3) | 3,959 (9.9) |
| (c) raw rows and values built into objects, references joined | 88 (3.5) | 159 (3.2) | 258 (2.6) | 1,331 (3.3) |
| Raw, one column per value row, nothing built | 28 (1.1) | 74 (1.5) | 124 (1.2) | 659 (1.6) |

(b) and (c) include the reference lookup, so they compare with the main query and reference lookup together; (a') compares with the main query alone. Read within one pass, not across hosts: an earlier three-run pass at 25,000 members put (a') at 13.7 µs and (b) at 10.6 µs.

- **(c) is within the projection** at 2.6 to 3.5 µs per value, and flat with size, where today's load climbs to 20.2 µs at 400,000 members.
- **(a') saves 20 to 40% of the main query,** not half, and its cost per value climbs with size too (8.4 to 12.0 µs).
- **(b) is cheaper than projected:** 7.3 to 9.9 µs, below (a') and well below "11 µs or more". It keeps 56 to 71% of (c)'s gain over today. Its rejection stands on its other grounds: it costs about three times (c), and every value stays tracked, so every tracker scan in D stays as large.
- **The Metaverse Object reader costs more per value than (c),** 3.5 to 6.3 µs, so as the projection for the new reader it is pessimistic. After (a) it is the larger part of the page: at 100,000 members (c) and the Metaverse Object read together are about 834 ms, just inside Phase 2's 900 ms target.

Gate (all three, judged on that run):

1. **Premise.** In the leaver cohort's confirming Delta Synchronisation, `Cso.Page.LoadModifiedWithAttributeValues` accounts for at least 80% of the `LoadModifiedCsoPage` time not under its two child spans: about 17 s of the 21 s, with an `attributeValueCount` near 1.2 to 1.4 million. If it accounts for much less, the unexplained time is elsewhere (the closing sweep's dropped loads, or the pager) and this plan is the wrong fix, so re-instrument instead.
2. **Per value.** The Worker's main-load cost is at least 8 µs per value, and at least three times the measured raw cost.
3. **Volume.** The summed main-load time across the run's synchronisations is at least 60 s, or at least 15% of any one synchronisation's wall clock. The 31 October numbers already meet the second test (21 s of 78.7 s, 27%). The run sizes the Full Synchronisation's share, which the explicit test cannot.

If gate 2 or 3 fails, record the numbers here, close the plan as option (d), and move it to `done/` with a Note.

### Phase 1: A page identity for Connected System Objects, and the loads that go around it

Every deliverable here preserves behaviour while the page is still loaded tracked: a page row resolves to the very instance EF's identity map would have returned. So each step is checked against today's behaviour, and each lands on its own.

Deliverables:

- the page identity in `SyncRepository`, registered by today's loaders, with the bypass counter;
- the reference accessor (solution item 4). The export cache's values carry `ResolvedReference*`, so no-net-change detection stops needing `Include(ReferenceValue)`;
- the converted reads (solution item 3), correcting the false "AsNoTracking" comments in the same change, which closes #2574;
- the severed execution-item navigation (solution item 6).

Tests, all `RequiresPostgres` on a `TrackAll` context with detection off, as the Worker holds a page:

- **For each converted read:**
  - with the page's own system as the target, it returns the page instance (`ReferenceEquals`);
  - the tracker gains no Connected System Object or value;
  - a row off the page matches EF's reading, column by column from the model.
- **Re-election discovery** for a Metaverse Object joined to a 10,000-member page group materialises none of the group's values.
- **`HeldAttributeValuesDifferentialTests`, extended:** raw values with resolved references answer exactly as EF values with `ReferenceValue` included.
- **`CreateActivityRunProfileExecutionItemsAsync`,** given an untracked Connected System Object navigation, inserts nothing but the items.

Scenarios:

- 008 and 032 at Medium on OpenLDAP (mandatory, since export staging is touched);
- 018 (writeback to the source system, which puts the page's own system on the export path);
- 014 (re-election).

Measurement: Scenario 008 at Large. The export evaluation cache refresh on group pages falls, because it no longer loads a target group's member objects tracked. Add a span around the refresh if it lacks one.

### Phase 2: The raw page reader

Deliverables:

- `ConnectedSystemObjectRawReader` (solution item 1) behind both page loaders and the cross-page reload, registering the identity;
- the fix-ups reaching the identity (solution item 5);
- the removals and rewrites (solution item 7);
- the guard from #2598 Phase 1, extended: after a page load, the context tracks no Connected System Object or value of the page;
- a changelog entry (⚡: Delta and Full Synchronisations of large groups load their pages several times faster), with `Docs: n/a` because no behaviour changes.

Tests, all `RequiresPostgres`:

- **`PageConnectedSystemObjectLoadDatabaseTests`** (pattern: `PageMetaverseObjectLoadDatabaseTests`) compares every mapped column of both tables, driven by the EF model, with an EF oracle query of the old `Include` shape. It runs for both loaders and the reload.
  - **Seeded rows** cover every predicate arm: each mark, `Created` and `LastUpdated` either side of the watermark, a type processed in full, the creation floor, and a cursor at a uuid-ordering boundary.
  - **It also checks:** date kinds, value order, `xmin`, the `ResolvedReference*` triplet, in-page `ReferenceValue` links (null for targets off the page), and that `Type` and `Attribute` are the context's tracked instances.
- **Round trips on a tracking context:** load, then join, mark clear, deletion, Metaverse Object deletion, and execution-item and change-record writes.
  - After each step, the page instance equals the database, through the identity.
  - A final EF save with detection on succeeds, with no `DbUpdateConcurrencyException`, `23505` or "already being tracked".
- **A synchronisation-level differential:** one seeded fixture runs through Full then Delta Synchronisation twice, once with the EF oracle page loader (a test decorator over `ISyncRepository`) and once with the production reader.
  - **The fixture covers:** nested groups and managers on one page, a cross-page reference, an obsolete member, an out-of-scope disconnect, drift on a group, writeback to the object's own system, and an immediate Metaverse Object deletion.
  - **These must match exactly:** Metaverse Object values, Pending Exports, execution items and outcome summaries, change records and marks.
- **#2598 Phase 2's tests still pass:** the closing sweep and exception-path tracker clears for Full and Delta, and the join-match and survivor identity tests.

Scenarios:

- 008 and 032 at Medium on OpenLDAP (mandatory);
- 004, 014, 018, 023 and 026 at Medium, each of which has caught a tracker fault on this path before;
- a Pre-Release run before the pull request merges.

Measurement: the explicit test's 100,000-member modified group page drops from 2,419 ms to 900 ms or less, with its main read at 300 ms or less.

### Phase 3: Scale verification

Run Scale500k25kGroups Scenario 008 on OpenLDAP and compare it, at matching value counts, with the 31 October run and the Phase 0 run. Record the results here.

## Success Criteria

- Phase 0's gate is met, or this plan closes as (d) with its numbers recorded.
- **Leaver cohort's confirming Delta Synchronisation:** `LoadModifiedCsoPage` totals 10 s or less (from 30.2 s), at a value count within 10% of Phase 0's. The main read costs 3 µs per value or less, or no more than 1.5 times the Metaverse Object reader's measured cost.
- **Full Synchronisation:** the 591,000-value page loads in 2 s or less (from 6.1 s), and the run's summed page-load time falls by at least 70%.
- **Tracking:** after any page load, the context tracks nothing of the page. The bypass counter reads zero across every scenario above, and `Cso.Delete.UpdateTracker`'s `entriesScanned` no longer includes the page's values.
- **Memory:** Worker memory during synchronisation is no higher than before.
- **Outcomes:** every scenario's outcome is unchanged, with no new `DbUpdateConcurrencyException`, `23505` or tracking-conflict line.

## Benefits

- **Performance:**
  - the page load's main cost falls from EF's 15 to 18 µs per value to the cost of reading the rows;
  - the reference lookup becomes a join rather than an `ANY` over every member;
  - a group whose own system is its export target is read once rather than twice;
  - every tracker scan in a page flush shrinks by the page's values.
- **User experience:** leaver cohorts, re-organisations and large group changes synchronise faster, on Delta and Full Synchronisation alike.
- **Architecture:**
  - the whole page, Connected System Objects and Metaverse Objects, is read raw and held untracked;
  - one instance per row is kept by an explicit, tested identity rather than an implicit one;
  - the last tracked `Include` on the page path goes;
  - comments that misstate tracking are corrected.
- **Operability:** the load's time is spent in the database, where it can be seen and tuned.

## Dependencies

- None external, and no migrations. The reader uses the existing `CsoBulkColumns` constants.
- #2671's span, which has merged.
- A host for Scale500k25kGroups (#2598's note on 750k still applies).
- Phase 1 lands before Phase 2.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| A tracked load later in the page makes a second instance of a page row, and a mutation or fix-up lands on the copy | Phase 1's identity and converted reads; fix-ups on the identity; the bypass counter (Warning in production, zero in tests); the guard that nothing of the page is tracked |
| An EF `Add`, `AddRange` or `Update` walks into an untracked page object and inserts it again | Detection stays off for the page; the severing in `CreateActivityRunProfileExecutionItemsAsync`; `Type` and `Attribute` come through the context, so a walk stops there; the pull request lists every EF graph write reachable from the page |
| Same-page references lose EF's fix-up, so a reference to an object projected on the page is deferred, or wired with an empty id | The reader links in-page targets; the synchronisation-level differential with nested groups and managers; Scenario 032 |
| A column not read, or read with the wrong type or date kind | Column constants, failure on any unmapped column, and the model-driven column-by-column comparison |
| The raw selection drifts from the count and claim queries, or selects differently at the watermark | One predicate; seeded rows either side of the watermark; the existing count-pinning test extended to the reader |
| Two statements see two snapshots | Rows and `xmin` first, then values, then Metaverse Objects. A write between them can only make the mark's clear fail safe. Documented on the reader and on `CaptureMarkedRowVersions` |
| The gain is smaller than projected, because building objects rather than the query dominates | Phase 0 measures the realistic raw read before any production change, and the gate decides |
| The unit suite cannot see any of this (the in-memory provider tracks, and the fake shares instances) | Only `RequiresPostgres` tests count as proof, and every scenario runs at Medium or above, so it crosses page boundaries |
| Synchronised Deprovisioning and the previews change behaviour, because they no longer accumulate tracked objects across a pass | Scenario 014 and their existing tests; the identity is replaced on each load, so memory stays bounded |
| A prepared statement degrades as tables grow during a run (#2492) | The reader's statements are not prepared |
