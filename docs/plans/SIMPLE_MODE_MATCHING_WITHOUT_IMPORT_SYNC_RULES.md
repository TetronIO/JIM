# Simple Mode Object Matching Without Import Sync Rules

- **Status:** Planned

## Context

Scenario 8 integration test failed because a confirming sync on the target AD created a spurious rename `PendingExport` that collided with a subsequent delete export for the same object. Investigation revealed the root cause: the test setup creates an empty "EMEA AD Import Groups" sync rule (no attribute mappings) solely to enable joining target CSOs back to MVOs after export.

In "simple mode" (`ObjectMatchingRuleMode.ConnectedSystem`), matching rules live on the `ConnectedSystemObjectType`, not on sync rules. However, `AttemptJoinAsync` iterates import sync rules to drive matching — if none exist, no matching is attempted. This forces admins to create empty import sync rules, which is confusing and causes side effects (the confirming sync evaluates the MVO for outbound exports via the empty rule's presence).

**Goal:** Allow `AttemptJoinAsync` to use simple mode matching rules directly from the object type without requiring an import sync rule. This requires knowing which `MetaverseObjectType` to search, which currently only comes from the sync rule.

## Changes

### 1. Add `MatchingTargetMetaverseObjectTypeId` FK to `ConnectedSystemObjectType`

**File:** `src/JIM.Models/Staging/ConnectedSystemObjectType.cs`

Add after `RemoveContributedAttributesOnObsoletion` (line 27):
- `int? MatchingTargetMetaverseObjectTypeId` (nullable FK)
- `MetaverseObjectType? MatchingTargetMetaverseObjectType` (navigation property)

XML doc comment should make clear this is specifically for simple mode object matching — when `ObjectMatchingRuleMode.ConnectedSystem` is active and no import sync rule exists for this object type, the matching rules on this object type use this MVO type to scope the metaverse search.

### 2. EF Core configuration

**File:** `src/JIM.PostgresData/JimDbContext.cs`

Add relationship config: `HasOne(MatchingTargetMetaverseObjectType).WithMany().OnDelete(SetNull)`.

### 3. EF Migration

Run `dotnet ef migrations add AddMatchingTargetMetaverseObjectTypeToConnectedSystemObjectType --project src/JIM.PostgresData`.

### 4. Update repository loading queries

**File:** `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs`

**`GetObjectTypesAsync` (line 1626)** — used by sync processors via `_objectTypes`. Add:
- `.Include(q => q.MatchingTargetMetaverseObjectType)`
- `.Include(q => q.ObjectMatchingRules).ThenInclude(omr => omr.Sources).ThenInclude(s => s.ConnectedSystemAttribute)`
- `.Include(q => q.ObjectMatchingRules).ThenInclude(omr => omr.Sources).ThenInclude(s => s.MetaverseAttribute)`
- `.Include(q => q.ObjectMatchingRules).ThenInclude(omr => omr.TargetMetaverseAttribute)`

Currently this query only includes `Attributes`. The matching rules and MVO type are needed for the simple mode fallback in `AttemptJoinAsync`.

**`GetConnectedSystemAsync` (line 155)** — the types sub-query already includes `ObjectMatchingRules`. Add `.Include(ot => ot.MatchingTargetMetaverseObjectType)`.

### 5. New overload on `ObjectMatchingServer.FindMatchingMetaverseObjectAsync`

**File:** `src/JIM.Application/Servers/ObjectMatchingServer.cs`

Add overload that takes `MetaverseObjectType` and `csoTypeId` directly (instead of `SyncRule`):
- Uses `GetConnectedSystemObjectTypeRules(connectedSystem, csoTypeId)` for matching rules
- Passes `metaverseObjectType` directly to `FindMetaverseObjectUsingMatchingRuleAsync` (no change needed to the repository method)

The existing `SyncRule`-based overload remains unchanged.

### 6. Modify `AttemptJoinAsync` — simple mode fallback

**File:** `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` (line 1734)

After the existing `foreach` loop over import sync rules (before `return false`), add:

- Check `_connectedSystem.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem`
- Check no import sync rules were already evaluated for this CSO type
- Look up `_objectTypes.FirstOrDefault(ot => ot.Id == connectedSystemObject.TypeId)`
- If `objectType.MatchingTargetMetaverseObjectType != null`, call the new `FindMatchingMetaverseObjectAsync` overload
- If match found, run the same join validation and establishment logic

**Extract join validation into a private helper** to avoid duplicating the `existingCsoJoinCount` / `_pendingDisconnectedMvoIds` checks between the sync rule path and simple mode path. Something like:
```csharp
private async Task<bool> EstablishJoinAsync(ConnectedSystemObject cso, MetaverseObject mvo)
```

This helper encapsulates: checking existing join count, adjusting for pending disconnects, throwing `SyncJoinException` for duplicates, setting FK/navigation properties, clearing `LastConnectorDisconnectedDate`.

### 7. Relax early-return guards

**File:** `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs`

**`ProcessActiveConnectedSystemObjectAsync` (line 197):** The `if (activeSyncRules.Count == 0) return;` guard prevents processing even when simple mode could handle it. Relax to allow processing when the connected system is in simple mode and the CSO's object type has a `MatchingTargetMetaverseObjectType` configured.

**`ProcessMetaverseObjectChangesAsync` (line 719):** Same guard, same relaxation. When there are no sync rules but simple mode is available, allow fall-through to the join attempt. The subsequent inbound attribute flow loop (line 793) already gracefully handles an empty list (zero iterations).

### 8. Update API layer

**`src/JIM.Web/Models/Api/ConnectedSystemRequestDtos.cs`** — Add `int? MatchingTargetMetaverseObjectTypeId` to `UpdateConnectedSystemObjectTypeRequest`.

**`src/JIM.Web/Models/Api/ConnectedSystemDto.cs`** — Add `MatchingTargetMetaverseObjectTypeId` and `MatchingTargetMetaverseObjectTypeName` to `ConnectedSystemObjectTypeDto`, update `FromEntity`.

**`src/JIM.Web/Controllers/Api/SynchronisationController.cs`** — In the PUT endpoint for object types (line 128), handle the new property: validate the MVO type exists, set on the entity.

### 9. Update PowerShell cmdlet

**File:** `src/JIM.PowerShell/Public/ConnectedSystems/Set-JIMConnectedSystemObjectType.ps1`

Add `-MatchingTargetMetaverseObjectTypeId` parameter, include in request body when specified.

### 10. Unit tests

**`test/JIM.Worker.Tests/OutboundSync/ObjectMatchingServerTests.cs`** — Add tests for the new overload:
- Match found with MVO type passed directly
- No matching rules returns null
- Multiple matches throws `MultipleMatchesException`

**New file: `test/JIM.Worker.Tests/Synchronisation/SimpleMatchingModeJoinTests.cs`** — Tests for the `AttemptJoinAsync` simple mode fallback:
- CSO joins via simple mode when no import sync rules exist
- CSO does not join when `MatchingTargetMetaverseObjectTypeId` is null (graceful no-op)
- Warning logged when matching rules exist but `MatchingTargetMetaverseObjectTypeId` is not set
- Existing join prevents duplicate (same validation as sync rule path)

### 11. Update Scenario 8 setup (follow-up)

**File:** `test/integration/Setup-Scenario8.ps1`

After the main changes, update the setup to:
- Set `MatchingTargetMetaverseObjectTypeId` on the target group object type via `Set-JIMConnectedSystemObjectType`
- Remove the empty "EMEA AD Import Groups" sync rule creation (lines 598-611)
- Verify the DeleteGroup test passes without the empty rule

## Verification

1. `dotnet build JIM.sln` — zero errors
2. `dotnet test JIM.sln` — all tests pass including new ones
3. Run Scenario 8 integration test to verify the DeleteGroup step passes without the spurious rename export
