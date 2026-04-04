# Cross-Page Reference Resolution: EF Core Identity Conflict

## Status: ✅ RESOLVED (2026-02-23)

All fixes verified via Scenario 8 integration test (Medium template: 1000 users, 118 groups, 23300 memberships).
All 6 test steps pass: InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup.

## Problem Statement

After commit `0c619df7` added `ClearChangeTracker()` before cross-page reference resolution in Full Sync, the operation fails with:

```
The instance of entity type 'MetaverseAttribute' cannot be tracked because another instance
with the same key value for {'Id'} is already being tracked.
```

or:

```
duplicate key value violates unique constraint "PK_MetaverseAttributeMetaverseObjectType"
```

The error occurs during `ResolveCrossPageReferencesAsync` in `SyncTaskProcessorBase.cs`.

## Root Cause

`ClearChangeTracker()` detaches all entities from the EF Core change tracker but does NOT null out in-memory navigation properties. Subsequent EF operations (`Update`, `UpdateRange`, `SaveChanges`) traverse the object graph from these in-memory entities and encounter conflicts:

1. **Shared reference entities**: Multiple MVOs share the same `MetaverseObjectType` and `MetaverseAttribute` instances (all MVOs of type "Person" point to the same `MetaverseObjectType` object)
2. **Many-to-many join table**: `MetaverseObjectType.Attributes` ↔ `MetaverseAttribute.MetaverseObjectTypes` uses an implicit EF join table `MetaverseAttributeMetaverseObjectType` with no explicit join entity
3. **Graph traversal**: EF's `Update()`, `UpdateRange()`, and `TrackGraph()` all traverse navigation properties, reaching shared entities from multiple paths

### Key Graph Paths That Cause Conflicts

```
MetaverseObject → Type (MetaverseObjectType) → Attributes (List<MetaverseAttribute>)
MetaverseObject → AttributeValues → Attribute (MetaverseAttribute) → MetaverseObjectTypes
Activity → RunProfileExecutionItems → ConnectedSystemObject → MetaverseObject → (above)
```

## Architecture Constraints

- **Mocked DbContext in tests**: Unit tests use `Mock<JimDbContext>` where `Entry()`, `ChangeTracker`, and `ChangeTracker.TrackGraph` are not available (throw `NullReferenceException`). All production-path fixes must use try/catch with fallback to original behaviour for test compatibility.
- **Shadow FK for MetaverseObject.Type**: No explicit `TypeId` property — EF uses a shadow property. Setting `Entry(mvo).State = Modified` preserves the FK independently of the `Type` navigation property.
- **`IsKeySet` property**: For `MetaverseObjectAttributeValue`, `IsKeySet` distinguishes existing (Modified) from new (Added) attribute values.

## Error Progression Timeline

The error manifests at different points in the cross-page resolution flow depending on which graph traversal paths have been fixed:

| Stage | Status Message | Root Cause |
|-------|---------------|------------|
| 1 | `(0 / N)` | `UpdateActivityAsync` → `Update(activity)` traverses Activity → RPEI → CSO → MVO → Type → Attributes |
| 2 | `(0 / N) - loading batch 1 of 1` | `DeletePendingExportsAsync` loads PE entities with Include chains, creating conflicting MetaverseAttribute instances |
| 3 | `(N / N) - saving changes` | `UpdateMetaverseObjectsAsync` → `UpdateRange(objectList)` traverses MVO → Type → Attributes from multiple MVOs |

## Fixes Applied (Current State)

### Fix 1: ActivityRepository.UpdateActivityAsync ✅ RESOLVED Stage 1

**File**: `src/JIM.PostgresData/Repositories/ActivitiesRepository.cs`

**Problem**: `Update(activity)` traverses the full Activity graph including RPEIs.

**Fix**: Check if Activity is detached via `Entry()`, set `State = Modified` directly (scalar-only, no graph traversal). Try/catch for mocked DbContext falls back to `Update()`.

### Fix 2: Clear RPEIs before ClearChangeTracker ✅ RESOLVED Stage 1 (supporting)

**File**: `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` (~line 1159)

**Problem**: After `ClearChangeTracker()`, the Activity's in-memory RPEI list still holds references to detached entities. Any subsequent EF operation on the Activity would re-traverse these stale references.

**Fix**: `_activity.RunProfileExecutionItems.Clear()` before `ClearChangeTracker()`.

### Fix 3: Raw SQL delete for pending exports ✅ RESOLVED Stage 2

**Files**: `ConnectedSystemRepository.cs`, `IConnectedSystemRepository.cs`, `ConnectedSystemServer.cs`

**Problem**: `DeletePendingExportsAsync` loads PE entities via query with Include chains, creating fresh MetaverseAttribute instances that conflict with instances already tracked from the cross-page CSO query.

**Fix**: New method `DeletePendingExportsByConnectedSystemObjectIdsAsync` uses raw SQL `DELETE` by CSO IDs — never loads entities into the change tracker.

### Fix 4: MetaverseRepository.UpdateMetaverseObjectsAsync ✅ RESOLVED Stage 3 (with Attempts 5-11)

**File**: `src/JIM.PostgresData/Repositories/MetaverseRepository.cs`

**Problem**: `UpdateRange(objectList)` traverses the full MVO graph. Multiple MVOs sharing the same `MetaverseObjectType`/`MetaverseAttribute` cause identity conflicts.

**Current approach**: Use `Entry().State = Modified` on each MVO and `Entry().State = Modified/Added` on each AttributeValue individually, avoiding graph traversal into Type/Attribute.

## Approaches Attempted for Stage 3

### Attempt 1: Remove `.ThenInclude(mvo => mvo!.Type)` from cross-page query ❌

**File**: `ConnectedSystemRepository.cs` — `GetConnectedSystemObjectsForReferenceResolutionAsync`

**Rationale**: Don't load `Type` navigation, so graph traversal can't reach it.

**Result**: Failed. `MetaverseObject.Type` is still populated from other paths (EF identity resolution, or earlier queries).

### Attempt 2: Clear `mvo.Type.Attributes` collection before update ❌

**File**: `MetaverseRepository.UpdateMetaverseObjectsAsync`

**Rationale**: Null out the many-to-many collection so graph traversal finds nothing.

**Result**: Failed. The reverse side `MetaverseAttribute.MetaverseObjectTypes` is also traversable.

### Attempt 3: Clear BOTH sides of many-to-many ❌

**File**: `MetaverseRepository.UpdateMetaverseObjectsAsync`

**Rationale**: Clear both `Type.Attributes` and `av.Attribute.MetaverseObjectTypes`.

**Result**: Failed. There are deeper/other graph paths that still reach these entities.

### Attempt 4: `ChangeTracker.TrackGraph` with selective entity state assignment ❌

**File**: `MetaverseRepository.UpdateMetaverseObjectsAsync`

**Rationale**: Use `TrackGraph` callback to set `MetaverseObjectType`/`MetaverseAttribute` to `Unchanged` instead of `Modified`, preventing join table re-insertion.

**Result**: Failed. `TrackGraph` still visits the nodes — when MVO #2 shares the same `MetaverseAttribute` as MVO #1, the second `TrackGraph` call finds an already-tracked entity and throws the identity conflict error.

### Attempt 5: `Entry().State` on MVO + individual AttributeValues ❌

**File**: `MetaverseRepository.UpdateMetaverseObjectsAsync`

**Rationale**: Completely bypass graph traversal by manually attaching only the MVO and its AttributeValues using `Entry().State`. Never touches `Type`, `Attribute`, or any other navigation.

**Key difference from Attempt 4**: `Entry().State =` assignment does NOT traverse navigation properties from the entity being attached. It only marks that single entity. `TrackGraph` by design walks the graph.

**Result**: Integration test still failed with same MetaverseAttribute identity conflict at "(102 / 102) - saving changes". The cross-page CSO query's Include chains load MetaverseAttribute entities into the tracker. Even though we don't explicitly traverse to them, `SaveChangesAsync → DetectChanges` finds populated navigation properties and encounters conflicting instances.

### Attempt 6: `Entry().State` + detach MetaverseAttribute/MetaverseObjectType before save ❌

**File**: `MetaverseRepository.UpdateMetaverseObjectsAsync`

**Rationale**: Same as Attempt 5, but additionally detach ALL `MetaverseAttribute` and `MetaverseObjectType` entities from the change tracker before `SaveChangesAsync`. These are reference/lookup entities that don't need persisting.

**Problem discovered**: The detach was applied unconditionally, breaking the normal per-page sync path (where MVOs are already tracked). Workflow test `DeltaSync_AfterDeltaImportWithMembershipChange_ProcessesChangedCsoAsync` failed because detaching MetaverseAttribute entities prevented EF from correctly persisting new attribute values that reference those attributes.

**Fix**: Added `anyDetached` check — only use the special Entry().State + detach path when MVOs are actually detached (post-ClearChangeTracker). Normal per-page sync uses standard `UpdateRange`.

**Result**: Integration test still failed at "(106 / 106) - saving changes". Detaching MetaverseAttribute/MetaverseObjectType from the tracker only helps for the `SaveChangesAsync` in `UpdateMetaverseObjectsAsync`. Subsequent `SaveChangesAsync` calls in the flush sequence (e.g. `UpdateActivityAsync`) re-discover MetaverseAttribute via navigation properties in `MetaverseObjectChange` entities added by `CreatePendingMvoChangeObjectsAsync`. The detach is not persistent — `DetectChanges` re-traverses navigation properties on every `SaveChangesAsync`.

### Attempt 7: `Entry().State` + `AutoDetectChangesEnabled = false` for entire flush ❌

**Files**: `MetaverseRepository.UpdateMetaverseObjectsAsync`, `SyncTaskProcessorBase.cs`, `IRepository.cs`, `PostgresDataRepository.cs`

**Rationale**: Disable `AutoDetectChangesEnabled` for the entire flush sequence to prevent `DetectChanges` from discovering conflicting instances.

**Result**: Integration test still failed. **Stack trace revealed the error was NOT in the flush sequence** — it was at `SyncTaskProcessorBase.cs:line 1310` in `UpdateActivityMessageAsync` which was called BEFORE the `SetAutoDetectChangesEnabled(false)` call. The activity status message update ("saving changes") triggers `SaveChangesAsync` → `DetectChanges` while the tracker already contains conflicting MetaverseAttribute instances accumulated during batch processing.

**Key learning**: The stack trace is essential — the error location was NOT where we expected. The `AutoDetectChangesEnabled = false` was correctly placed around the flush, but the status message update BEFORE it was the actual failure point.

### Attempt 8: Move `AutoDetectChangesEnabled = false` before status message update ❌

**File**: `SyncTaskProcessorBase.cs`

**Rationale**: Based on the stack trace from Attempt 7, the error was at `UpdateActivityMessageAsync` before the disable. Moving `SetAutoDetectChangesEnabled(false)` earlier covers the status update too.

**Result**: Integration test still failed. **Stack trace revealed the error moved AGAIN** — now at `SyncFullSyncTaskProcessor.cs:line 212`, which is `UpdateActivityAsync` called AFTER `ResolveCrossPageReferencesAsync` returns. The `finally` block restores `AutoDetectChangesEnabled = true`, but the tracker still contains conflicting MetaverseAttribute instances from the cross-page resolution. The next `SaveChangesAsync` in the caller's flow triggers the conflict.

**Key learning**: Disabling `AutoDetectChangesEnabled` only helps during the disabled period. Once restored, the tracker's polluted state persists and the next `SaveChangesAsync` fails. The tracker needs to be CLEANED after cross-page resolution, not just temporarily blinded.

### Attempt 9: ClearChangeTracker at end of ResolveCrossPageReferencesAsync ❌

**File**: `SyncTaskProcessorBase.cs`

**Rationale**: Clear the tracker after cross-page resolution to remove conflicting entities.

**Result**: Integration test still failed. **Stack trace revealed `ClearChangeTracker()` itself triggers the conflict!** `ChangeTracker.Entries().Count()` (the diagnostic count at line 102 of `PostgresDataRepository.cs`) calls `DetectChanges()` internally, which discovers the conflicting MetaverseAttribute instances and throws.

**Key learning**: `ChangeTracker.Entries()` triggers `DetectChanges()`. `ChangeTracker.Clear()` does NOT trigger `DetectChanges()`. The diagnostic logging in `ClearChangeTracker` was the trigger.

### Attempt 10: Fix ClearChangeTracker to not call Entries() before Clear() ✅ PARTIAL SUCCESS

**File**: `PostgresDataRepository.cs`

**Rationale**: Remove the `ChangeTracker.Entries().Count()` diagnostic call from `ClearChangeTracker()`. `Entries()` triggers `DetectChanges()`. `Clear()` does not.

**Result**: The identity conflict error is resolved! But revealed a NEW error: `duplicate key value violates unique constraint "PK_MetaverseAttributeMetaverseObjectType"` at `UpdateConnectedSystemAsync` → `Update(connectedSystem)` in `UpdateDeltaSyncWatermarkAsync`. After `ClearChangeTracker()` at end of cross-page resolution, `_connectedSystem` is detached. `Update()` traverses its graph: ConnectedSystem → Objects (CSOs) → MetaverseObject → Type → Attributes → join table INSERT.

### Attempt 11: Detached entity handling for UpdateConnectedSystemAsync ✅ RESOLVED (for Full Sync)

**File**: `ConnectedSystemRepository.cs`

**Rationale**: Same pattern as Activity and MVO fixes — use `Entry().State = Modified` for detached ConnectedSystem to avoid graph traversal through CSO → MVO → Type → Attributes.

**Result**: Full Sync cross-page resolution now completes successfully. However, a DIFFERENT activity ("Target Delta Confirming Import") fails with a `ConnectedSystemObjectTypeAttribute` identity conflict at `UpdateUntrackedPendingExportsAsync`. This is a separate code path unrelated to `ClearChangeTracker` — see Fix 5 below.

### Fix 5: UpdateUntrackedPendingExportsAsync graph traversal ✅ RESOLVED

**File**: `ConnectedSystemRepository.cs` — `UpdateUntrackedPendingExportsAsync`

**Problem**: When an untracked `PendingExport` (loaded via `AsNoTracking()`) is not found in the change tracker, `Update(untrackedExport)` traverses the full graph: PendingExport → AttributeValueChanges → Attribute (ConnectedSystemObjectTypeAttribute). Multiple PendingExports sharing the same `ConnectedSystemObjectTypeAttribute` cause identity conflicts. Same issue for `Update(attrChange)` on untracked `PendingExportAttributeValueChange` entities.

**Fix**: Replace `Update()` with `Entry().State = Modified` for both untracked PendingExport and PendingExportAttributeValueChange entities. This attaches only the single entity without traversing navigation properties.

**Shadow FK issue**: `PendingExportAttributeValueChange` has a shadow FK `PendingExportId` (not an explicit property). When loaded via `AsNoTracking()`, shadow property values are lost. Must explicitly set `Entry(attrChange).Property("PendingExportId").CurrentValue = untrackedExport.Id` to maintain the parent relationship. Without this, the FK becomes null, orphaning the attribute value change and causing `ExportNotConfirmed` errors on subsequent confirming imports.

**Note**: This is NOT caused by `ClearChangeTracker`. The import flow does not use `ClearChangeTracker`. This is a pre-existing latent bug where `Update()` on untracked entities with shared navigation references can cause identity conflicts when the shared entities are already tracked from a different path (e.g. from the per-CSO processing phase that precedes the batch update).

**Result**: ✅ All 1722 unit tests pass. Integration test Scenario 8 passes all 6 steps.

## Key Insight: Why `Entry().State` vs `TrackGraph` vs `Update`

| Method | Graph Traversal | Shared Entity Handling |
|--------|----------------|----------------------|
| `Update()` / `UpdateRange()` | Full graph — marks everything as Modified/Added | Throws if same entity reached twice from different roots |
| `TrackGraph()` | Full graph — calls callback per node | Throws if same entity already tracked (even with Unchanged state) |
| `Entry().State = X` | **None** — attaches only that single entity | Safe — never visits navigation properties |

### Shadow FKs and `AsNoTracking()`

When using `Entry().State = Modified` on entities loaded via `AsNoTracking()`, shadow FK values are NOT preserved. Shadow properties (FKs without explicit C# properties) are only maintained by the change tracker — `AsNoTracking()` discards them. Must manually set shadow FKs via `Entry(entity).Property("FkName").CurrentValue = value`.

**Affected entity**: `PendingExportAttributeValueChange.PendingExportId` (shadow FK to `PendingExport`).

## Flow of Operations After ClearChangeTracker

```
ClearChangeTracker()                           ← all entities detached
  |
  v
GetConnectedSystemObjectsForReferenceResolutionAsync()  ← reloads CSOs + MVOs (fresh tracking context)
  |
  v
ProcessInboundAttributeFlow()                  ← modifies MVO.AttributeValues in-memory
  |
  v
UpdateActivityMessageAsync()                   ← [FIXED] uses Entry().State on detached Activity
  |
  v
DeletePendingExportsByConnectedSystemObjectIdsAsync()  ← [FIXED] raw SQL, no entity loading
  |
  v
PersistPendingMetaverseObjectsAsync()          ← [FIXED] Entry().State on MVO + AVs, AutoDetectChanges disabled
  v
CreatePendingMvoChangeObjectsAsync()           ← adds MetaverseObjectChange to mvo.Changes
  |
  v
EvaluatePendingExportsAsync()                  ← uses cache, no DB queries
  |
  v
FlushPendingExportOperationsAsync()            ← raw SQL inserts/updates for PEs
  |
  v
UpdateActivityAsync()                          ← [FIXED] uses Entry().State on detached Activity
```

## Alternative Approaches Considered (Not Needed)

These were documented during debugging but are no longer needed since the combination of Fixes 1-5 + Attempts 5-11 resolved all issues.

- **A: Second ClearChangeTracker before persist** — Not needed; `AutoDetectChangesEnabled = false` + `Entry().State` avoids conflicts without clearing.
- **B: Detach specific conflicting entities** — Incorporated into Attempt 6, superseded by `AutoDetectChangesEnabled` approach.
- **C: Separate DbContext for cross-page resolution** — Not needed; too architecturally invasive.
- **D: Raw SQL for MVO attribute value updates** — Not needed; `Entry().State` with `AutoDetectChangesEnabled = false` works.
- **E: Investigate MetaverseAttribute loading paths** — Moot; `AutoDetectChangesEnabled = false` prevents traversal regardless of how entities were loaded.

## Files Modified (All Changes)

| File | Change |
|------|--------|
| `src/JIM.Data/IRepository.cs` | Added `SetAutoDetectChangesEnabled(bool)` interface method |
| `src/JIM.PostgresData/PostgresDataRepository.cs` | Implemented `SetAutoDetectChangesEnabled` |
| `src/JIM.PostgresData/Repositories/ActivitiesRepository.cs` | `UpdateActivityAsync`: detached entity handling with `Entry().State` |
| `src/JIM.PostgresData/Repositories/MetaverseRepository.cs` | `UpdateMetaverseObjectsAsync`: detached-aware with `Entry().State` on MVO + AVs |
| `src/JIM.PostgresData/PostgresDataRepository.cs` | `ClearChangeTracker`: removed `Entries().Count()` that triggered `DetectChanges`; implemented `SetAutoDetectChangesEnabled` |
| `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` | Removed `.ThenInclude(mvo!.Type)` from cross-page query; added `DeletePendingExportsByConnectedSystemObjectIdsAsync` raw SQL method; `UpdateConnectedSystemAsync`: detached entity handling; added inner try/catch in `DeletePendingExportsAsync` detach loop; `UpdateUntrackedPendingExportsAsync`: `Entry().State` instead of `Update()` to avoid graph traversal |
| `src/JIM.Data/Repositories/IConnectedSystemRepository.cs` | Added `DeletePendingExportsByConnectedSystemObjectIdsAsync` interface method |
| `src/JIM.Application/Servers/ConnectedSystemServer.cs` | Added `DeletePendingExportsByConnectedSystemObjectIdsAsync` server method |
| `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` | Clear RPEIs before `ClearChangeTracker()`; use raw SQL PE delete; disable `AutoDetectChangesEnabled` during cross-page flush |

## Verification

1. `jim-reset`
2. Run integration test Scenario 8: `./test/integration/Run-IntegrationTests.ps1 -Scenario 8`
3. All 6 test steps should pass (InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup)
4. Worker logs: `docker compose logs jim.worker --tail=500`

## Performance Impact

Verified on Medium template (1000 users, 118 groups, 23300 memberships):
- **FullImport**: -47% (2.6s avg, down from 5.0s) — raw SQL PE deletes in reconciliation
- **Export**: -58% (4.0s avg, down from 9.7s) — reduced change tracker overhead
- **FullSync**: +11% (7.3s avg, up from 6.6s) — small overhead from `Entry().State` + `AutoDetectChangesEnabled` logic, acceptable trade-off for correctness
