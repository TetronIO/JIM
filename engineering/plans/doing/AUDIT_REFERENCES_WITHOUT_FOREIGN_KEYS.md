# Deleting an Object Leaves Its Audit History Untouched

- **Status:** Doing (Phase 0 underway: spans and fixture in; the scale baseline is outstanding)
- **Issue:** [#2669](https://github.com/TetronIO/JIM/issues/2669)
- **Related:**
  - [#2670](https://github.com/TetronIO/JIM/issues/2670), the `DeletedObjectExternalIdAttributeValueId` check, removed separately.
  - [Connected System Object Pages Off EF Core](../done/SYNC_CSO_PAGE_RAW_READER.md), closed at Phase 0. It found that the leaver step's larger cost is database-side deletion.
- **Last Updated:** 2026-11-01

## Overview

Deleting a Connected System Object or a Metaverse Object rewrites rows in JIM's largest tables. Every change record, change value and Run Profile Execution Item that names the object has its reference set to null:

- **Connected System Objects:** by a foreign key's `ON DELETE SET NULL`.
- **Metaverse Objects:** by an UPDATE the deleter runs first.

Each rewrite writes a new heap tuple and a new entry in every index on the table, because the nulled column is indexed. In the Scale500k25kGroups leaver cohort, these rewrites are most of the 56.6 s that the two synchronisations spend deleting objects.

The rewrites also destroy the record they claim to preserve:

- **Metaverse Object references go blank.** A change value that set a Manager to Alice keeps nothing once Alice is deleted, and every surface then shows a Manager change with no value.
- **Deleted Connected System Objects' histories are merged.** Their earlier changes are found by matching an external ID string, which merges two objects that held the same external ID at different times.
- **Causality loses its thread.** The id it walks on is gone, so it falls back to heuristics.

**So stop rewriting audit rows.** The five audit references to live objects become plain id columns, with no database constraint and no EF navigation. They are written once and never changed. Readers resolve an id when they read it, in this order:

1. a live object;
2. otherwise, the object's own deletion record (its name, its type, when it went);
3. otherwise, the referencing row's own snapshot (an item's name snapshots, a change value's DN);
4. otherwise, "no longer held by JIM".

This is the pattern JIM already uses for Activities, causal edges, match candidates, preview deltas, retired values and sync outcomes. Live data (joins, attribute value references, Pending Exports) keeps its constraints and is still cleared on deletion.

## Business Value

- **The problem:**
  - **Leaver cohort cost.** In the Scale500k25kGroups leaver cohort (5,000 deprovisioned at once), deletion costs about 36 s for Connected System Objects and 20.6 s for Metaverse Objects, out of about 162 s for the two synchronisations. Measured on 500 objects, audit rewrites are 68% of the Connected System Object `DELETE` and 79% of the Metaverse Object deletion's statements.
  - **Clears and system deletion.** A Connector Space clear and a Connected System deletion, on their history-preserving path, rewrite every change record and Run Profile Execution Item of the system. They also null every change value that references the system's objects.
- **Who it affects:** every deployment with leavers, every Deletion Rule, every Connector Space clear, and every administrator reading history about someone who has since left. Leavers are exactly the people audit questions are about.
- **What the record loses today:**
  - Metaverse Object reference changes go blank.
  - A deleted Connected System Object's history is stitched together by external ID.
  - Activities about a Metaverse Object lose their link to it.
  - The causal chain behind a deletion is reported as severed (`ActivityServer.IsTimelineSevered`).
- **Why now:** #2598 and the raw page reader work removed the costs in .NET. What remains on the leaver step is database-side, and this is its largest part.

## Technical Architecture

### Current state

#### Inventory: audit and history references to live objects

| # | Reference | EF model | Database | After the object is deleted |
|---|---|---|---|---|
| 1 | `ConnectedSystemObjectChanges.ConnectedSystemObjectId` | navigation `ConnectedSystemObject`, inverse `ConnectedSystemObject.Changes`, `SetNull` | `ON DELETE SET NULL`, indexed | null; the object's earlier changes can be found only by external-ID string |
| 2 | `ConnectedSystemObjectChangeAttributeValues.ReferenceValueId` | navigation `ReferenceValue` over a shadow key, `SetNull` | `SET NULL`, indexed | null; shows the DN kept in `StringValue` where the import had one, otherwise an empty value |
| 3 | `ActivityRunProfileExecutionItems.ConnectedSystemObjectId` | navigation `ConnectedSystemObject`, inverse collection, `SetNull` | `SET NULL`, indexed | null; the item shows its snapshots, and the timeline falls back to `ExternalIdSnapshot` |
| 4 | `MetaverseObjectChanges.MetaverseObjectId` | shadow key under navigation `MetaverseObject`, inverse `MetaverseObject.Changes`, convention `ClientSetNull` | `NO ACTION`, indexed | null; every record is re-keyed to `DeletedMetaverseObjectId` first |
| 5 | `MetaverseObjectChangeAttributeValues.ReferenceValueId` | CLR key plus navigation `ReferenceValue`, `ClientSetNull` | `NO ACTION`, indexed | null, with nothing else stored: the value is lost |
| 6 | `Activities.MetaverseObjectId` | plain scalar, no navigation | no constraint, indexed | still nulled by both deleters, though no constraint requires it |

Writers and readers of each:

1. **`ConnectedSystemObjectChanges.ConnectedSystemObjectId`**
   - **Writers:**
     - import, export and synchronisation change capture: raw COPY over `CsoChangeBulkColumns`, plus the EF path in `ActivitiesRepository.CreateActivityRunProfileExecutionItemsAsync`, which severs the navigation to stop `AddRange` re-inserting the object;
     - deletion records, written with it null and with `DeletedConnectedSystemObjectId` set instead;
     - the Connected System deletion's history-preserving step 5, which nulls it for the whole system.
   - **Readers:**
     - a live object's history: the portal timeline, REST `GET .../connector-space/{csoId}/change-history` and `Get-JIMConnectedSystemObjectChangeHistory`;
     - the **Deleted Objects** list, which decides "deleted" by `ConnectedSystemObject == null`: REST `GET /history/deleted-objects/cso` and `Get-JIMDeletedObject -ObjectType CSO`;
     - the deleted object's history (`GetDeletedCsoChangeHistoryAsync`), matched by `ConnectedSystemId` plus the `DeletedObjectExternalId` string;
     - retention, which deletes by `ChangeTime` and never reads this column.
2. **`ConnectedSystemObjectChangeAttributeValues.ReferenceValueId`**
   - **Writers:**
     - import change capture, which also keeps the DN in `StringValue` when the Connector supplied one;
     - deletion records' final values. The raw writer checks which referenced rows exist and writes null for the rest, because the foreign key refuses a reference to an object that a later batch will write (#2492). `FixupCrossBatchChangeRecordReferenceIdsAsync` then resolves those rows by DN: 6.5 million rows at Scale500k25kGroups on 2026-07-18.
     - Export change values never set it.
   - **Readers:**
     - the windowed value reads (`ChangeValueRowsSql`; `ReadCsoChangeValueDetailsAsync`, a LEFT JOIN);
     - the history projection;
     - the causality attribute rows;
     - REST `GET /history/connected-system-object-changes/{changeId}/attributes/{name}/values` and `Get-JIMConnectedSystemObjectChangeValue`.
   - No reader filters by this column, so its index serves only the constraint.
3. **`ActivityRunProfileExecutionItems.ConnectedSystemObjectId`**
   - **Writers:**
     - every run: raw COPY over `RpeiBulkColumns`, or EF for housekeeping;
     - five application sites, which null it on the item that deletes the object, "to avoid FK constraint violations";
     - the Connected System deletion's step 7, which nulls it for the whole system.
   - **Readers:**
     - the item page (`Include`) and `CausalityItemLoader`: `ConnectedSystemObject == null` selects the snapshot display, "(deleted)" or "Deleted: <external id>";
     - the Causality Explorer timeline: by id, else by `ExternalIdSnapshot` within the system for rows with a null id. That fallback is why `IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot` exists (#1495);
     - the causal chain's source-import hop;
     - REST item detail (documented "when it still exists") and `Get-JIMActivityExecutionItem`.
4. **`MetaverseObjectChanges.MetaverseObjectId`**
   - **Writers:**
     - synchronisation: raw COPY over `MvoChangeBulkColumns`, read from the navigation;
     - portal edits, through `MetaverseObject.Changes` as the carrier;
     - deletion records, written with it null and `DeletedMetaverseObjectId` set;
     - both deleters, which re-key every earlier record: `DeletedMetaverseObjectId = MetaverseObjectId, MetaverseObjectId = NULL`.
   - **Readers:**
     - a live object's history: REST `GET /metaverse/objects/{id}/change-history` and `Get-JIMMetaverseObjectChangeHistory`;
     - **Deleted Objects**, which tests `MetaverseObject == null`: `GET /history/deleted-objects/mvo`;
     - `GetDeletedMvoChangeHistoryAsync`, keyed by `DeletedMetaverseObjectId`, with a shadow-key fallback;
     - the **Object Changes** search, which decides live or deleted by the navigation: REST `POST /history/metaverse-object-changes/search` and `Search-JIMMetaverseObjectChange`;
     - the causal timeline, which reads both columns;
     - the Causality Explorer headers: the live object, then the deletion record by `DeletedMetaverseObjectId`.
5. **`MetaverseObjectChangeAttributeValues.ReferenceValueId`**
   - **Writers:** synchronisation and portal edits. Both deleters null it first.
   - **Readers:**
     - the windowed value reads, which name the reference by a live `CachedDisplayName`;
     - the history projection;
     - the Object Changes search's reference-value condition;
     - causality rows;
     - REST values and `Get-JIMMetaverseObjectChangeValue`.
   - Its index serves only the deleters.
6. **`Activities.MetaverseObjectId`**
   - It is already a plain scalar.
   - The Activity page and the Operations panel look the header up and treat a miss as deleted, so nulling it only throws away the link to the deleted object's record.

**Already unconstrained and resolved on read (no change):**

- `Activities.ConnectedSystemObjectId`, and every configuration target id ("must outlive the object it describes");
- `ActivityRunProfileExecutionItems.MetaverseObjectId` (#1971);
- sync outcomes' `TargetEntityId`;
- causal edges' `CauseConnectedSystemObjectId` and `CauseMetaverseObjectId`;
- match candidates' `CandidateId`, shown as "(no longer exists)";
- configuration change preview deltas;
- `RetiredGeneratedValues.FromObjectId`;
- `MetaverseObjectChanges.DeletedByRunProfileExecutionItemId`;
- both `Deleted*ObjectId` columns;
- `ConnectedSystemObjectChanges.ConnectedSystemId`, which already dangles after a Connected System deletion;
- Connected System Object change values' `SyncRuleId`.

**Out of scope:**

- `ConnectedSystemObjectChanges.DeletedObjectExternalIdAttributeValueId`, removed under #2670.
- Live-data references, which must stay constrained and be cleared on deletion:
  - Connected System Object attribute values' `ReferenceValueId`;
  - Metaverse Object attribute values' references (`MetaverseReferenceRowCleanup`);
  - the join detach;
  - `PendingExports`, `PendingPasswordChanges`, `DeferredReferences` and `GeneratedValueAssignments`.
- Configuration references from audit rows (Synchronisation Rule, Object Type, attribute definitions). They are low volume and not on the synchronisation path.

**Retention:** `ChangeHistoryRepository.DeleteExpired*` removes change records and general Activities oldest first, under one `History.RetentionPeriod` cutoff. Retention neither reads nor writes these columns.

**Factory reset:** `TruncateAllCustomerDataSql` names every audit table, including both change tables and `Activities`. Their children hang off containment keys that are out of scope.

#### What a deletion costs

500 group-member objects, Scale500k25kGroups, 1 November.

**Connected System Object `DELETE` (2,883 ms):**

| Cost | Time | Inventory row |
|---|---|---|
| change value `ReferenceValueId` SET NULL | 1,376 ms | 2 |
| change record SET NULL | 316 ms | 1 |
| Run Profile Execution Item SET NULL | 269 ms | 3 |
| `DeletedObjectExternalIdAttributeValueId` NO ACTION check | 819 ms | #2670 |
| the object's own values (cascade), and everything else | about 100 ms | live data |

The audit share is 1,961 ms, or 68%. Before the `DELETE`, the live member-values UPDATE costs a further 1,067 ms.

**Metaverse Object deletion (3,249 ms in statements):**

| Statement | Time | Inventory row |
|---|---|---|
| change values UPDATE | 2,021 ms | 5 |
| re-key | 552 ms | 4 |
| join detach | 274 ms | live data |
| reference-row clean-up | 207 ms | live data |
| `DELETE` | 195 ms | live data |

The audit share is 2,573 ms, or 79%.

#### What the constraints cost on the way in

- **An insert-time referential check** on every audit row that names an object: a `SELECT ... FOR KEY SHARE` on the referenced row, for Run Profile Execution Items, change records and reference change values.
- **The existence check and cross-batch fix-up** for Connected System Object change values. They exist only because the constraint refuses a reference to an object written in a later batch.
- **The sever-and-reattach code** in `CreateActivityRunProfileExecutionItemsAsync`.
- **The tracker clean-up cases** in `ConnectedSystemObjectDeletionSql.ClearReferencesToTheObjects`.

### Options considered

**(a) Plain id columns, no constraint, resolved on read.**

- **EF Core 10 has no constraint-free relationship.** JIM pins EF Core 10.0.14, which cannot keep a relationship in the model without creating its constraint; `ExcludeForeignKeyFromMigrations` arrives only in EF Core 11. Dropping the constraint by hand in a migration, while leaving the relationship modelled, would leave the snapshot describing a constraint the database lacks. EF would also still apply client-side fix-up to tracked rows when a principal is deleted through EF. So in EF Core 10 the shape is a scalar with no relationship.
- **Remove the navigation, even on EF Core 11.**
  - EF rewrites `change.MetaverseObject == null` as a test of the key column. With a retained id, a deleted object would then read as live, and that predicate is exactly how both Deleted Objects lists and the Object Changes search decide "deleted" today.
  - The history projections read `vc.ReferenceValue.ConnectedSystemId` (an `int`) and `vc.ReferenceValue.Type.Name` through the navigation. With a retained id the null test passes, the LEFT JOIN finds no row, and EF fails materialising null into a non-nullable member.
  - `Include` returns null silently, and the in-memory provider hides all of this.
  - Removing the navigation turns every such reader into a compile error, which is the inventory needed.
- **The migration** drops the constraints. Removing a relationship also removes EF's convention index on its key, so the scaffold drops those indexes too. The subject-column indexes must be redeclared under their existing names, and the two change-value indexes are dropped deliberately.
- **`DeletePathForeignKeyCoverageTests`** walks `pg_constraint`, so a dropped constraint cannot block any surface. The Metaverse Object surface's severed entries for keys 4 and 5 must be removed, or `SeveredForeignKeys_AllStillExistAsync` fails. The test cannot see an unconstrained column at all, so a new guard is needed (below).
- **Correctness.**
  - Ids are GUIDs and never reused, so a retained id can only ever name the object it named.
  - No synchronisation decision reads an audit id, and live state keeps its constraints.
  - The risk moves to readers, where tests can reach it.
- **Judgement:**
  - best user experience: deleted references are named, and histories are exact;
  - best architecture: the house pattern, and four workarounds removed;
  - best performance: the rewrites are gone entirely, and the insert-time checks too.

**(b) Keep the constraints, defer the nulling to housekeeping.**

- **The deferred state is impossible.** A constrained row cannot dangle:
  - `ON DELETE` actions fire immediately;
  - a deferrable `NO ACTION` check still fails at commit;
  - `NOT VALID` only skips validating the rows that existed when the constraint was added.
- **So deferring the nulling means deferring the delete.** Objects become soft-deleted rows; housekeeping later nulls the references and deletes the rows.
- **Every reader of live state would have to exclude them:** the Connector Space, joins, Object Matching, export evaluation, the unique external ID on re-import, generated value assignments, reference values, counts, search, SCIM, and the REST and PowerShell lists. One missed filter is corrupted synchronisation, against "fast/hard failures over corrupted state".
- **Total I/O is unchanged, only moved,** and it then contends with the next run for WAL and vacuum.
- **Rejected.**

**(c) Point audit rows at a never-deleted identity record.**

- It keeps integrity and keeps a name, but:
  - it adds an insert per object created, on the import hot path;
  - it keeps the insert-time check on every audit row;
  - retention would have to anti-join three large tables before it could prune an identity;
  - a Connected System deletion's preserved history would still need an unconstrained edge;
  - it duplicates the deletion record.
- **The name it would preserve already survives.** A reference is always recorded before the referenced object's deletion, and change records and Activities age out oldest first under one cutoff. So the deletion record outlives every row that names the object, and (a)'s snapshot fallback covers the exceptions.
- **Rejected.**

**(d) Cheaper writes, same semantics.**

- **Each rewrite is a non-HOT update.** The nulled column is indexed, and the constraint's action needs that index: without it, each deleted parent row would scan the table, which is 500 sequential scans of 6.9 GB per page. A non-HOT update writes a new tuple and a new entry in every index on the table.
- **The measured statements are already set-based,** yet still cost 1.1 ms per row for the Metaverse Object re-key (8 indexes), 0.47 ms for the Metaverse Object change values (4 indexes) and 0.25 ms for the member values (10 indexes).
- **No layout trick removes the rewrite:**
  - fillfactor only enables HOT, so it buys nothing here but bloat;
  - a partial index shrinks the index but not the rewrite;
  - replacing the per-parent-row SET NULL actions with one set-based UPDATE saves only trigger dispatch: plausibly 10 to 20% of the audit share, a few seconds of 56.6 s.
- **It cannot matter at the scale needed.**

**(e) Do nothing.**

- The roughly 34 to 40 s of the leaver step stays, and grows with group size.
- Clears and Connected System deletions keep rewriting whole systems.
- Metaverse Object reference history stays blank.
- It fails all three axes.

| Option | Removes the rewrites | History after deletion | New risk | Verdict |
|---|---|---|---|---|
| (a) | all of them | better: named and exact | readers must resolve | **recommended** |
| (b) | moves them | unchanged | soft-delete in every live reader | rejected |
| (c) | yes | named | new table, insert cost, retention anti-joins | rejected |
| (d) | no (about 10 to 20% at best) | unchanged | none | insufficient |
| (e) | no | blank and heuristic | none | rejected |

### Proposed solution

1. **The five references become plain columns.**
   - The constraints are dropped.
   - These navigations are removed from the model: `ConnectedSystemObjectChange.ConnectedSystemObject`, both `ReferenceValue`s, `MetaverseObjectChange.MetaverseObject`, and the inverse collections on the objects.
   - The shadow keys become CLR properties (`MetaverseObjectChange.MetaverseObjectId`, `ConnectedSystemObjectChangeAttributeValue.ReferenceValueId`).
   - `ActivityRunProfileExecutionItem.ConnectedSystemObject` stays, as an unmapped working reference for the synchronisation engine, which uses it about 100 times. The persisted fact is `ConnectedSystemObjectId`, and writers coalesce `ConnectedSystemObjectId ?? ConnectedSystemObject?.Id`.
   - `MetaverseObject.Changes` stays as an unmapped carrier for portal-created records, with the id set explicitly. Precedent: the item's unmapped `CausalEdges` buffer.
2. **One id per subject, for life.**
   - A change record's `ConnectedSystemObjectId` or `MetaverseObjectId` is never rewritten, deletion records included.
   - `DeletedConnectedSystemObjectId` and `DeletedMetaverseObjectId` then become redundant, and are removed after a backfill.
3. **One resolution order everywhere:** live object, then deletion record (`ChangeType = Deleted` on the same id), then the row's own snapshot, then "not held".
   - It is modelled as `ResolvedObjectReference`, with an `ObjectReferenceState` of Live, Deleted or NotHeld (JIM.Models).
   - It is implemented twice, once per access pattern, and a test proves the two agree:
     - a Summary-tier batch read, for entity-level callers. The Causality Explorer's existing live-then-deletion-record lookup becomes this;
     - a SQL fragment, for the windowed value reads.
   - "Is it live?" is always answered by an existence test on the object table, never by a null id or a navigation.
4. **Rows nulled before this change.**
   - The Phase 2 migration restores the ids of Metaverse Object change records and Connected System Object deletion records exactly, from the `Deleted*Id` columns.
   - Earlier Connected System Object change records, Run Profile Execution Items and change values of objects already deleted lost their id for good.
   - The external-ID fallbacks stay, scoped to rows whose id is null. They are not a compatibility shim: they are the only readers for data the old schema destroyed.
5. **Deleters stop touching audit rows.**
   - The three audit UPDATEs in each Metaverse Object deleter go.
   - In the Connected System deletion, step 5 drops `ConnectedSystemObjectId = NULL` (its other half belongs to #2670), and step 7 goes.
   - The five application sites stop nulling item ids.
   - The tracker clean-up cases for these rows go.
6. **Indexes.**
   - **Kept, and redeclared under their existing names:** the subject indexes on change records and Run Profile Execution Items, which serve history by object, timelines and the deletion-record lookup.
   - **Dropped:** both change-value `ReferenceValueId` indexes, which have no reader. That is one index write fewer per change value inserted (3 to 2, and 4 to 3).
   - **Kept for older rows:** `IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot`, for rows nulled before this change.
7. **Guards.**
   - **A catalogue test pins the audit soft references:**
     - no constraint on any of them;
     - the declared indexes present;
     - the exact set of foreign keys into `ConnectedSystemObjects` and `MetaverseObjects`, limited to live-data tables, each with its reason;
     - every catalogued audit table in the factory reset's truncate list.

     Foreign keys are what install the referential triggers on the referenced table, so this doubles as the trigger guard.
   - **A behavioural test** runs every deletion entry point in a transaction and asserts from `pg_stat_xact_user_tables` that no audit table had a row updated or deleted. That counts trigger-driven writes too.
8. **Surfaces.** A reference to a deleted object is no longer null on any surface. It carries its id, a deleted marker and, when known, its name and when it went.

### Data flow

**Deleting 500 leavers, afterwards:**

- **Connected System Objects:** retire generated values; clear live member values and unresolved references; `DELETE`, with their own values cascading. Change records, change values and items keep the id.
- **Metaverse Objects:** retire generated values; detach joins; clean live reference rows; `DELETE`. Change records, change values and Activities keep the id.
- **Deletion records** are written with the subject id set.

**Reading a reference:**

1. The window SQL, or the batch resolver, takes the id.
2. If the object is live, the reader shows its name and a link.
3. Otherwise it uses the latest `Deleted` change record with that id: the name, type and deletion time, linked to its **Deleted Objects** record.
4. Otherwise it uses the row's snapshot: the DN in `StringValue`, or the item's `DisplayNameSnapshot`.
5. Otherwise it shows "no longer held by JIM", with the id.

## Implementation Phases

### Phase 0: Baseline and spans

- **Spans.** Add per-statement `ActivitySource` spans to both Metaverse Object deleters (`Mvo.Delete.*`), mirroring `Cso.Delete.*`, so before and after are measured the same way.
- **Fixture.** Extend `LeaverCohortCostDatabaseTests` to report, beside each deletion path's time, how many audit rows it updated and deleted, and add the two Metaverse Object paths.
  - Every path commits its own transaction, so the counts are the difference in `pg_stat_user_tables` before and after, read on the deleting connection after `pg_stat_force_next_flush()`, not `pg_stat_xact_user_tables`.
  - The quiet deletion cannot run inside a caller's transaction at all: `PersistCsoChangesAsync` begins its own unconditionally. Phase 2's guard needs the same before-and-after read for that entry point, or that method must join an ambient transaction.
- **Scale baseline.** On Scale500k25kGroups, with the same PostgreSQL settings as the 1 November run (the runner's Scale500k profile), record:
  - the leaver step's `Cso.Delete.*` and `Mvo.Delete.*` totals;
  - WAL bytes over the leaver step;
  - `pg_relation_size` of the two change-value reference indexes;
  - `FixupCrossBatchChangeRecordReferenceIdsAsync` volume and time;
  - the plan of the Deleted Objects count query;
  - a Scenario 007 Connector Space clear at Large.

  The issue's two `EXPLAIN (ANALYZE, BUFFERS)` runs of 1 November are the statement-level baseline.

**Measured (local fixture).** 500 objects per path, median of 5 runs after a warm-up, on 1 November 2026.

- **Host:** 4 vCPU Intel Xeon at 2.10 GHz, 15 GB RAM, shared with other sessions' test runs. PostgreSQL 18.6 in Docker on its default settings: 160 MB `shared_buffers`, 1 GB `max_wal_size`, `fsync` on.
- **Shape:** each departed object has 25 values and is a member of two groups of 25,000 among 50,000. With history it also has 3 change records and 15 reference change values naming it, plus 5 Run Profile Execution Items (Connected System Objects) or 2 Activities (Metaverse Objects).

| Path | Without history | With history | Difference | Audit rows updated, with history |
|---|---|---|---|---|
| Connected System Object: Connected System repository | 407 ms | 593 ms | 186 ms | 1,500 change records, 7,500 change values, 2,500 items |
| Connected System Object: quiet deletion | 870 ms | 931 ms | 61 ms | the same |
| Connected System Object: execution items | 390 ms | 501 ms | 111 ms | the same |
| Connected System Object: Synchronisation repository by id | 317 ms | 495 ms | 178 ms | the same |
| Metaverse Object: bulk | 98 ms | 270 ms | 172 ms | 1,500 change records, 7,500 change values, 1,000 Activities |
| Metaverse Object: singular, once per object | 6,756 ms | 7,861 ms | 1,105 ms | the same |

- **Every audit row is rewritten, and none in place.** Every row naming a deleted object was updated, none deleted, and not one update was HOT, as option (d) says.
- **The shares are lower than at scale.** The fixture's tables are small and cache-resident, so treat these as a floor and judge the success criteria against the scale baseline.
  - **Connected System Object `DELETE`:** the `Cso.Delete.Delete` span rises from 165 to 226 ms without history to 318 to 438 ms with it, an audit share of about 47% (68% at scale). The #2670 check is in both columns.
  - **Metaverse Object bulk statements, with history:** 278 ms, of which the three audit UPDATEs are 165 ms, or 59% (79% at scale). The `DELETE` also rises from 59 to 88 ms, from the audit tables' `NO ACTION` checks. Counting that, the share is 70%.
- **The singular deleter is dominated by per-call cost, not audit rows.** It spends 13.5 ms an object without history: the save, 4.8 ms, and the tracked join fix-up's change detection, 2.5 ms. It also writes each join twice, by the raw UPDATE and again by the save of the tracked Connected System Object.

### Phase 1: Readers onto ids, behaviour-neutral

- **The resolver.** `ResolvedObjectReference` and the batch read. The Causality Explorer headers, `CausalityItemLoader` and the item page move onto it. Until Phase 3, it matches deletion records on the subject id or on `Deleted*Id`.
- **The window SQL.**
  - `ChangeValueRowsSql`'s value text resolves as: live name, then deletion record name, then `StringValue`, then the id as text. Today a dangling reference would fall through to the GUID text and break search; this order prevents that.
  - `ReadCsoChangeValueDetailsAsync` and its Metaverse Object equivalent return the state.
- **The "is it live?" tests.**
  - `QueryDeletedCsoChangesByRangeAsync` and `GetDeletedMvoChangesAsync` use `ChangeType = Deleted AND NOT EXISTS` (the live object).
  - The Object Changes search (`BuildChangeSearch`, `LoadChangeSearchIdentitiesAsync`) uses an explicit join.
  - The history projections use a correlated subquery, not the navigation.
  - Live history predicates use `ConnectedSystemObjectId == id` or `MetaverseObjectId == id`.
- **Deleted history by id.** A deleted Connected System Object's history is read by id. The external-ID fallback is limited to rows whose id is null.
- **Tests.**
  - Every existing reader fixture passes unchanged: `DeletedCsoChangeRangeDatabaseTests`, `DeletedMvoChangeRangeDatabaseTests`, `DeletedMvoChangeHistoryDatabaseTests`, `CausalTimelineReadDatabaseTests` and `ChangeValueRowsDatabaseTests`.
  - New `RequiresPostgres` cases run on a `NoTracking` context, as JIM.Web does.
  - An agreement test holds the SQL and C# resolvers to one answer.

### Phase 2: Drop the constraints, stop nulling

- **Guards first.** Write the two guard tests (solution item 7) and see them fail against Phase 1's code for the right reasons before changing anything else.
- **Model and migration** (`AuditReferencesWithoutForeignKeys`):
  - drop the five constraints and the two change-value reference indexes;
  - redeclare the subject indexes;
  - backfill `MetaverseObjectId` from `DeletedMetaverseObjectId`, and `ConnectedSystemObjectId` from `DeletedConnectedSystemObjectId`, where null;
  - review the scaffold for dropped subject indexes.
- **Writers.**
  - Deletion records set the subject id.
  - The nulling statements and the five item sites go.
  - The Connected System deletion's steps 5 and 7 are trimmed.
  - The sever-and-reattach code goes for these two navigations.
  - Item writers coalesce the id.
  - `ConnectedSystemObjectDeletionSql` drops three tracker cases and its remarks list.
  - `JIM.InMemoryData` follows.
- **`DeletePathForeignKeyCoverageTests`:** remove the Metaverse Object surface's two severed entries.
- **Tests:**
  - **Guards:** both guard tests now pass.
  - **Tracker regression:** a tracked item, change record and change value naming a deleted object keep the id, and the next save writes nothing and does not throw (pattern: `MvoDeletionPendingExportReplaceDatabaseTests`).
  - **Migration data:** a migration data test (pattern: `AddMetaverseObjectUnsynchronisedChangesMigrationDatabaseTests`).
  - **Connected System deletion:** `ConnectedSystemDeletionDatabaseTests`, history-preserving path keeping the ids.
  - **Factory reset:** `SystemResetDatabaseTests`, every audit table empty on both reset paths.
  - **Writer round trips:** `MvoChangeValuePersistenceDatabaseTests` and its Connected System Object equivalent.
  - **Item persistence:** an item with only the navigation set persists its id on both write paths.
- **Docs.**
  - src/CLAUDE.md gains the rule that object references from audit tables are plain ids.
  - "Adding a table that hangs off a Connected System" option 2 is updated.
  - The Developer Guide's change history section is updated.

### Phase 3: One id column per subject

- **Migration** (`RetireDeletedObjectIdColumns`): drop `DeletedMetaverseObjectId`, `DeletedConnectedSystemObjectId` and their filtered indexes.
- **Readers.**
  - The deletion record is found by subject id and `ChangeType`.
  - The timeline's Metaverse Object CTE loses a branch.
  - `GetDeletedMvoChangeHistoryAsync` loses its shadow-key fallback.
  - `SetDeletedMetaverseObjectIdAsync` goes.
- **Bulk writers.** `MvoChangeBulkColumns`, `CsoChangeBulkColumns` and their writers are updated; `BulkInsertColumnCompletenessTests` enforces it.
- **Seed data.** `test/data/seed-change-history-simple.sql` is regenerated.
- This phase may share Phase 2's migration if both land in one PR.

### Phase 4: Deleted references on all three surfaces

These changes land together, in one PR, for surface parity.

- **REST:**
  - `CsoChangeReferenceDto` and `MvoChangeReferenceDto` gain `IsDeleted` and `DeletedAt`.
  - The name comes from the deletion record. `ConnectedSystemId` comes from the deletion record or the change's own system.
  - A reference JIM no longer holds stays non-null and carries its id.
  - The item detail's `ConnectedSystemObjectId` summary changes to "kept as history; it may since have been deleted".
  - Summaries are published, so write them for the API reader, with no issue numbers.
  - Regenerate the OpenAPI document.
- **Portal:**
  - `ChangeHistoryTimeline`, the change dialog, `CausalityAttributeRowMapper` and `CausalityModelBuilder` render a deleted reference as `<ObjectChip Deleted="true">`, linked to its **Deleted Objects** record through the existing deep link.
  - A reference JIM no longer holds shows its DN, or "no longer held by JIM".
  - The item page links a deleted object's record.
  - First, build a Claude artefact of the states for approval.
- **PowerShell:**
  - No new parameters.
  - Update the output shapes in the help of `Get-JIMConnectedSystemObjectChangeHistory`, `Get-JIMMetaverseObjectChangeHistory`, `Get-JIMConnectedSystemObjectChangeValue`, `Get-JIMMetaverseObjectChangeValue`, `Search-JIMMetaverseObjectChange` and `Get-JIMActivityExecutionItem`.
  - Add Pester tests, and regenerate the reference.
- **Docs:** `docs/configuration/deleted-objects.md`, the causality and activities pages, and `docs/powershell/history.md`.
- **Changelog:** a ⚡ entry for faster deletion, and a 🔄 entry for history naming deleted objects.
- **Tests:**
  - JIM.Web.Tests on state, never presentation: a deleted reference has the Deleted state and no `href`.
  - Controller tests.
  - Pester.

### Phase 5: Change records write the reference ids they know

- **The writer.** Remove the existence check in `BulkInsertCsoChangeAttributeValuesRawAsync`, and write `ReferenceValueId` whenever the id is known.
- **The fix-up.** `FixupCrossBatchChangeRecordReferenceIdsAsync` keeps resolving rows that hold only a DN. It reads only the values the writer marks `IsUnresolvedReference` (#2690), and with the id written whenever it is known, the writer marks only values that hold a DN and no id.
- **Measurement.** Measure the volume drop against Phase 0.
- **Tests:** a round trip of a reference to an object a later batch writes; Scenario 032.

### Phase 6: Verification

- **Scenarios,** all at Medium or above, never a single-page run:
  - 001 (Leaver and Reconnection);
  - 004 (Deletion Rules, housekeeping);
  - 007 (Connector Space clear, both history choices);
  - 008 and 032 (references and the cross-page pass; mandatory, since synchronisation's deletion handling changes);
  - 023 (retirement on deletion still recorded).
- **Pre-Release:** Medium for Samba AD, Large for OpenLDAP and 389 Directory Server.
- **Scale:** Scenario 008 at Scale500k25kGroups, repeating every Phase 0 measurement, with the results recorded here.

## Success Criteria

- **No deletion path rewrites an audit row.**
  - The `pg_stat_xact` guard shows zero updates and deletes on every audit table, for every deletion entry point.
  - On the issue's `EXPLAIN` runs, the three audit triggers and the two audit UPDATEs are gone.
- **The 500-object page is cheaper.**
  - The Connected System Object `DELETE` falls from 2,883 ms by at least the audit triggers' 1,961 ms.
  - The Metaverse Object deletion's statements total about 0.7 s or less.
- **The leaver step is faster.**
  - Deletion falls by at least 30 s of its 56.6 s; on the measured proportions, the estimate is 34 to 40 s.
  - The two synchronisations fall from about 162 s to about 125 s.
  - WAL over the step falls measurably.
- **History stays exact.**
  - Every reference to an object deleted after this change reads, on all three surfaces, as that object: named from its deletion record and marked deleted. No reference value is ever blank because of a deletion.
  - A deleted Connected System Object's history is read by id, so two objects with the same external ID are never merged.
- **Nothing else changes.**
  - Deleted Objects lists and counts are unchanged on legacy data.
  - The 600,000-member change value search stays under 2 s (#2498).
  - The guard tests, `DeletePathForeignKeyCoverageTests`, `SystemResetDatabaseTests` and `ConnectedSystemDeletionDatabaseTests` pass.
- **The scenarios pass,** as listed in Phase 6.

## Benefits

- **Performance:**
  - the deletion step's largest database cost goes: on synchronisation, housekeeping, the portal, Connector Space clears and Connected System deletion;
  - the insert-time referential checks on audit rows go;
  - two indexes with no reader are reclaimed;
  - the cross-batch change record fix-up shrinks to the references that genuinely lacked an id.
- **User experience:**
  - administrators can see who was removed from a group, or who a Manager was, after that person has left;
  - deleted objects' histories are exact;
  - the causal chain behind a deletion is no longer reported as severed.
- **Architecture:**
  - one rule for every audit reference, matching the existing Activity and causality precedent;
  - one id column per subject;
  - four workarounds removed: sever-and-reattach, the tracker clean-up cases, the existence check, and the item-id nulling.

## Dependencies

- **EF Core 10:** it has no relationship without a constraint (`ExcludeForeignKeyFromMigrations` is EF Core 11). No new packages.
- **PostgreSQL 18,** for `pg_stat_xact_user_tables`.
- **#2670:** independent, but it touches the same `DELETE`, the same step 5 and `DeletePathForeignKeyCoverageTests`. Land them in either order and rebase.
- **A host for Scale500k25kGroups** (Scenario 008 only), with the runner's Scale500k database profile held constant across the before and after measurements.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Orphaned ids in audit rows | **By design.** GUIDs are never reused, so an orphan cannot name a different object. Every reader resolves through one order, and the three states are documented. **The deletion record outlives every row that names the object,** under the one oldest-first cutoff. **The exceptions fall back** to the row's snapshot or "no longer held by JIM": change tracking off at deletion, history deleted by a Connector Space clear, a retention batch backlog, and Activities on their own longer clocks. `RequiresPostgres` tests seed every state |
| History pages joining on a missing row | Navigations are removed, so each reader becomes a compile error and is rewritten with explicit joins. Members filled from a LEFT JOIN are nullable. The value-text order puts the DN or name before the GUID. Tests seed dangling ids, on `NoTracking` contexts, not the in-memory provider |
| A reader still treating a null id as "deleted" | Phase 1 moves every such site, each with a test: both Deleted Objects lists, the Object Changes search, the timeline, `IsTimelineSevered`, `CausalityFocusHeader`, the Lineage consequences and the item page |
| The factory reset no longer cleaning up rows a cascade removed | Nothing relied on these edges: the truncate names every audit table, and their children hang off containment keys that remain. The catalogue guard asserts each audit table stays in the truncate list, and `SystemResetDatabaseTests` asserts they are empty on both paths |
| Connected System deletion behaving differently | The history-preserving path now keeps object ids, consistent with `ConnectedSystemId`, which already dangles. The history-deleting path is unchanged. Covered by `ConnectedSystemDeletionDatabaseTests` |
| Housekeeping retention | Its mechanics are unchanged, and the ordering argument above holds per table. A Connector Space clear that deletes history removes the deletion record but leaves the items, which keep their own snapshots |
| The scaffold dropping subject indexes | Redeclare them under their existing names, review the migration, and have the catalogue guard assert they are present |
| The migration's locks and backfill | Dropping constraints and indexes is catalogue work, under a brief exclusive lock. The backfill is bounded by deleted objects' records. A migration data test covers it |
| An item persisted without its id, now that EF fix-up is gone | Writers coalesce the id; a round trip on both write paths covers it |
| The tracker diverging from the database | The database no longer changes these rows, so the clean-up cases are deleted. A regression test shows a later save writes nothing |
| A future audit table reintroducing a foreign key | The catalogue guard fails on any new foreign key into `ConnectedSystemObjects` or `MetaverseObjects` that is not on its list, and a src/CLAUDE.md rule explains why |
| Phase 5 writing an id whose object was never written (a failed batch) | It resolves as "no longer held", with the DN shown. Audit ids are never read for a synchronisation decision |
| The search, or the Deleted Objects count, slowing | `COALESCE` evaluates lazily, and the probes use existing indexes. Phase 0 and Phase 6 plans and timings gate it. A partial index on deletion records is added only if a plan regresses |
