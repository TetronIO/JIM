# MVO COPY Binary Persistence

- **Status:** Done
- **Note:** Creates and updates are both on raw SQL. The updates half, deferred when this plan was written, was subsequently implemented as `UpdateMetaverseObjectsBulkAsync` (batched `UPDATE ... FROM (VALUES ...)` plus attribute-value insert/delete reconciliation). [#436](https://github.com/TetronIO/JIM/issues/436) remains open for its other items (caching Future Considerations A, B and C), which were never part of this plan.
- **Milestone**: v0.9-STABILISATION
- **GitHub Issue**: [#436](https://github.com/TetronIO/JIM/issues/436)
- **Parent**: [#338](https://github.com/TetronIO/JIM/issues/338) (closed; Phases 1-6 creates complete)
- **Related**: [`engineering/plans/done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md`](WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md)
- **Created**: 2026-03-27

## Overview

Convert MVO (Metaverse Object) persistence from EF Core `AddRange`/`SaveChangesAsync` to COPY binary and raw SQL, mirroring the proven pattern used for CSO persistence in #338 Phase 3.

MVO persistence was the **last major hot path still using EF Core's per-row SQL generation** in the sync flush pipeline. CSO creates, RPEI inserts, and sync outcome inserts already use COPY binary via `ParallelBatchWriter`.

## Business Value

- **Eliminates the last single-connection bottleneck** in the sync flush; MVO creates now use COPY binary, matching CSO/RPEI throughput
- **Consistent architecture**: all bulk write hot paths use the same COPY binary pattern
- **Expected 5–20x improvement** in MVO create throughput (based on CSO COPY binary results)

---

## MVO Creates ✅

Convert `CreateMetaverseObjectsAsync` to use COPY binary, following the exact pattern from `SyncRepository.CsOperations.cs`.

**Implementation** (`SyncRepository.MvoOperations.cs`):

- `CreateMetaverseObjectsBulkAsync`: entry point with ID pre-generation, intra-batch reference fixup, route selection
- `CreateMvosOnSingleConnectionAsync`: single-connection fallback for small batches
- `BulkInsertMvosOnConnectionAsync`: COPY binary for `MetaverseObjects` (10 columns, `xmin` excluded)
- `BulkInsertMvoAttributeValuesOnConnectionAsync`: COPY binary for `MetaverseObjectAttributeValues` (13 columns)
- `BulkInsertMvosViaEfAsync`: parameterised multi-row INSERT fallback (single-connection path)
- `BulkInsertMvoAttributeValuesViaEfAsync`: parameterised multi-row INSERT fallback (single-connection path)

**Delegation** (`SyncRepository.cs`): `CreateMetaverseObjectsAsync` now routes to the owned `CreateMetaverseObjectsBulkAsync` instead of delegating to `MetaverseRepository`.

### EF Change Tracker Bridge

Integration testing revealed that bypassing EF for MVO creates caused a `DbUpdateConcurrencyException` (`xmin` mismatch) during the subsequent page flush. The root cause:

1. Downstream sync code (`CreatePendingMvoChangeObjectsAsync`) adds `MetaverseObjectChange` entities to `mvo.Changes` navigation collections
2. When `SaveChangesAsync` runs later (via `UpdateActivityAsync`), EF needs to track the parent MVO to discover and persist these child entities
3. With COPY binary, MVOs were untracked; EF either missed the child entities or discovered the MVOs through navigation traversal with stale `xmin = 0`

**Fix**: After COPY binary persistence, MVOs and their attribute values are attached to the EF change tracker as `Unchanged` with shadow FKs (`TypeId`, `MetaverseObjectId`) set explicitly. This was framed as a **temporary bridge**, to be removed when MVO change tracking and export evaluation are also converted to raw SQL.

**Outcome:** the bridge is still in place, because `CreatePendingMvoChangeObjectsAsync` and `EvaluatePendingExportsAsync` still rely on EF to persist child entities added to MVO navigation collections. It is no longer a hazard: the tracked `xmin` stays 0 after COPY, but the update path is raw SQL keyed on `Id` with no `xmin` predicate, and it detaches the graph afterwards, so nothing on the sync write path reads the bogus value. EF-tracked writes elsewhere (UI / API edits) still enforce `xmin` against their own reads.

### Column Reference

**MetaverseObjects** (10 columns):

| Column | Type | Nullable | Notes |
|--------|------|----------|-------|
| Id | uuid | No | Pre-generated `Guid.NewGuid()` |
| Created | timestamp with time zone | No | |
| LastUpdated | timestamp with time zone | Yes | |
| TypeId | integer | No | Shadow FK to MetaverseObjectType; read from `mvo.Type.Id` |
| Status | integer | No | Enum `MetaverseObjectStatus` |
| Origin | integer | No | Enum `MetaverseObjectOrigin` |
| LastConnectorDisconnectedDate | timestamp with time zone | Yes | |
| DeletionInitiatedByType | integer | No | Enum `ActivityInitiatorType` |
| DeletionInitiatedById | uuid | Yes | |
| DeletionInitiatedByName | text | Yes | |

Note: `xmin` is a PostgreSQL system column (concurrency token); assigned automatically by PostgreSQL on INSERT, must NOT be included in COPY statements.

**MetaverseObjectAttributeValues** (13 columns):

| Column | Type | Nullable | Notes |
|--------|------|----------|-------|
| Id | uuid | No | Pre-generated `Guid.NewGuid()` |
| MetaverseObjectId | uuid | No | Shadow FK to MetaverseObject (parent) |
| AttributeId | integer | No | FK to MetaverseAttribute |
| StringValue | text | Yes | |
| DateTimeValue | timestamp with time zone | Yes | |
| IntValue | integer | Yes | |
| LongValue | bigint | Yes | |
| ByteValue | bytea | Yes | |
| GuidValue | uuid | Yes | |
| BoolValue | boolean | Yes | |
| ReferenceValueId | uuid | Yes | FK to MetaverseObject (reference) |
| UnresolvedReferenceValueId | uuid | Yes | FK to ConnectedSystemObject |
| ContributedBySystemId | integer | Yes | FK to ConnectedSystem |

### FK Fixup

The caller at `SyncTaskProcessorBase` relies on `cso.MetaverseObject.Id` being populated after `CreateMetaverseObjectsAsync`. With pre-generated IDs, `mvo.Id` is set before persistence; no caller changes needed.

### Success Criteria; Met

- ✅ `CreateMetaverseObjectsAsync` uses COPY binary for large batches, parameterised INSERT for small batches
- ✅ Pre-generated IDs ensure CSO FK fixup works without EF relationship fixup
- ✅ All 2,353 unit tests pass without modification
- ✅ Integration tests pass (Scenario 1 Small confirmed)
- ✅ No regressions in sync integrity

---

## MVO Updates ✅

Deferred when this plan was written, then implemented: `SyncRepository.UpdateMetaverseObjectsAsync` now routes to `UpdateMetaverseObjectsBulkAsync` in `SyncRepository.MvoOperations.cs`, entirely raw SQL.

**Implementation:**

- `BulkUpdateMvoRowsViaEfAsync`: batched `UPDATE "MetaverseObjects" ... FROM (VALUES ...)` over the mutable columns (`MvoBulkInsertColumns.MetaverseObjectsUpdate`), keyed solely on `Id` with **no `xmin` predicate**.
- Attribute-value reconciliation by diffing the in-memory collection against `LoadExistingAttributeValueIdsAsync`: added values go through `BulkInsertMvoAttributeValuesViaEfAsync`, removed values through `DeleteMetaverseObjectAttributeValuesByIdsAsync`.
- The whole batch (row update, inserts, deletes) commits in one transaction, so an MVO is never left half-updated.
- `DetachPersistedMvoGraphs` detaches the persisted graph afterwards, so no later EF `SaveChangesAsync` in the same page flush can re-run an `xmin`-guarded update or re-insert attribute values already written as raw SQL.

**What made the classification problem tractable:** the analysis below assumed the repository would have to recover the new/modified/deleted split without EF. In practice the sync engine models every attribute change as a removal plus an addition (`SyncEngine.ApplyPendingAttributeChanges`), so existing values are never mutated in place; only inserts and deletes are needed, and both are derivable by diffing against the database. MVOs on this path always carry their complete attribute-value set (unfiltered `Include`), so an absent value is genuinely a removal.

**Why it was done ahead of the profiling data the recommendation below asked for:** dropping the `xmin` predicate closed a failure class, not just a performance gap. MVOs created via COPY are tracked with `xmin = 0`, so a subsequent EF update issued `... WHERE xmin = 0`, matched no rows, and threw an unhandled `DbUpdateConcurrencyException` that aborted the run (the pre-release Full Regression Scenario14-AttributePriority failure).

### Why updates looked fundamentally harder than creates

Creates are a single operation (INSERT rows). Updates involve **four distinct operations in one flush**:

| Operation | What | Raw SQL approach |
|-----------|------|-----------------|
| UPDATE parent MVO rows | Scalar property changes (Status, LastUpdated, deletion fields) | `UPDATE ... FROM (VALUES ...)` batch; straightforward |
| INSERT new attribute values | AVs added during inbound Attribute Flow | COPY binary; proven pattern from creates |
| UPDATE existing attribute values | AVs modified during inbound Attribute Flow | `UPDATE ... FROM (VALUES ...)`: 13 nullable columns |
| DELETE removed attribute values | AVs recalled during disconnect/out-of-scope | `DELETE ... WHERE "Id" IN (...)`: need to identify which |

The crux is **knowing which attribute values are new vs modified vs deleted** without EF's change tracker. Currently:

1. `ProcessInboundAttributeFlow` accumulates changes in `PendingAttributeValueAdditions` / `PendingAttributeValueRemovals` (both `[NotMapped]`)
2. `ApplyPendingMetaverseObjectAttributeChanges` moves pending items into/out of `mvo.AttributeValues`, then clears the pending collections
3. `UpdateMetaverseObjectsAsync` receives MVOs with the final `AttributeValues` list and uses EF's `IsKeySet` to classify: `Id == Guid.Empty` → Added, otherwise → Modified

A raw SQL conversion would need the caller to **preserve the classification** (e.g., keep `PendingAttributeValueAdditions`/`Removals` intact for the repository to consume) or the repository to diff against the database. Both approaches are invasive.

### Entity state complexity

MVOs arrive at `UpdateMetaverseObjectsAsync` in mixed states depending on the call context:

| Context | MVO state | AV state | `AutoDetectChanges` |
|---------|-----------|----------|---------------------|
| Per-page flush | Tracked (in-memory from join/project/flow) | Mixed: tracked originals + new additions | Enabled |
| Cross-page reference resolution | Detached (post-`ClearChangeTracker` reload) | Mixed: reloaded from DB + new additions | **Disabled** |
| Singular update (deletion marking) | Tracked | No AV changes | Enabled |
| Singular update (out-of-scope disconnect) | Tracked | Removals applied | Enabled |

The cross-page path is the dangerous one; `AutoDetectChangesEnabled = false` prevents `SaveChangesAsync` from walking navigation properties into shared `MetaverseAttribute`/`MetaverseObjectType` instances (which would cause identity conflicts). The `UpdateDetachedSafe` + per-entity `Entry().State =` pattern was the result of 11 debugging attempts documented in [`engineering/notes/done/CROSS_PAGE_REFERENCE_IDENTITY_CONFLICT.md`](../../notes/done/CROSS_PAGE_REFERENCE_IDENTITY_CONFLICT.md). That pattern survives in `MetaverseRepository.UpdateMetaverseObjectsAsync`, which still serves the non-sync EF callers; the sync path no longer goes near it.

### Recommendation (superseded)

The original call was to **wait for profiling data**: creates are the high-volume operation during initial sync, updates are smaller per-page during delta sync, and the conversion carried real regression risk. It was overtaken by the `xmin` failure described above, which made the conversion a correctness fix rather than an optimisation.

---

## Risks and Mitigations

| Risk | Mitigation | Outcome |
|------|------------|---------|
| Shadow FK columns have wrong names | Verified against migration snapshot | ✅ Names confirmed correct |
| `xmin` in COPY causes error | Excluded from COPY column list | ✅ PostgreSQL assigns automatically |
| Caller expects EF relationship fixup for MVO IDs | Pre-generated IDs | ✅ Caller code compatible |
| Downstream code relies on EF tracking MVOs | Temporary tracker bridge (attach as Unchanged) | ✅ Integration tests pass |
| Unit tests use in-memory provider | Delegation only affects Postgres SyncRepository; InMemory unchanged | ✅ All tests pass |
