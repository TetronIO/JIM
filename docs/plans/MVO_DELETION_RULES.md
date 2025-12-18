# MVO Deletion Rules and Deprovisioning

## Overview

Implement comprehensive deletion rule and deprovisioning functionality for JIM's identity lifecycle management. This addresses critical gaps preventing proper user deprovisioning when employees leave organisations or fall out of sync rule scope.

**Status**: In Progress
**Milestone**: MVP
**Related Issues**: #120 (scheduled deletion cleanup), #173 (integration testing), #203 (main tracking issue)

## Business Value

- **Complete JML lifecycle**: Joiner-Mover-Leaver processes require proper deletion/deprovisioning
- **Compliance**: Organisations need audit trails showing when identities were scheduled for deletion
- **Flexibility**: Different object types may need different deletion behaviours
- **Admin visibility**: Grace period status must be visible to administrators

## Current State

**Completed**:
- `MetaverseObjectType` has `DeletionRule`, `DeletionGracePeriodDays`, and `DeletionTriggerConnectedSystemIds` properties
- `MetaverseObject` has `LastConnectorDisconnectedDate`, `Origin`, `IsPendingDeletion`, and `DeletionEligibleDate` properties
- `SyncRule` has `OutboundDeprovisionAction` and `InboundOutOfScopeAction` properties
- Sync engine correctly sets `LastConnectorDisconnectedDate` when last connector disconnects (deferred deletion)
- Housekeeping deletes orphaned MVOs after grace period expires
- API endpoint and PowerShell cmdlet for configuring deletion rules
- Out-of-scope deprovisioning implemented for both inbound and outbound sync rules
- Unit tests validate all deletion rule processing (579 tests passing)

**Remaining**:
- Integration tests need to be run end-to-end to validate full JML lifecycle

## Design Decisions

### Deletion Rules
- **Default**: `WhenLastConnectorDisconnected` (not Manual)
- **Grace Period**: Use `LastConnectorDisconnectedDate` timestamp so admin changes recalculate for existing orphans
- **Specific System Triggers**: Optional `DeletionTriggerConnectedSystemIds` - delete when specific systems disconnect

### MVO Origin Tracking
- **Purpose**: Protect admin/service accounts from automatic deletion
- **Values**: `Projected` (from CSO) vs `Internal` (created in JIM)
- **Rule**: Only `Projected` MVOs are subject to automatic deletion

### Deferred MVO Deletion (Critical Architecture Decision)

Following industry-standard identity management practices, **MVOs are NEVER deleted during sync processing**. Instead:

1. **When a trigger connector disconnects** (e.g., HR system CSO deleted):
   - Set `LastConnectorDisconnectedDate` on the MVO
   - Evaluate export sync rules for remaining CSOs
   - Create `PendingExport` records for CSO deletions (based on `OutboundDeprovisionAction`)
   - The MVO remains in place with its connectors

2. **Export processing runs**:
   - PendingExports are processed, deleting objects from target connected systems
   - On next import, deletions are confirmed and CSOs removed

3. **Housekeeping cleanup** (Worker idle time):
   - Finds orphaned MVOs: no CSOs AND `LastConnectorDisconnectedDate` + grace period expired
   - Only then is the MVO actually deleted

**Rationale**: This cascade approach ensures:
- All dependent CSOs are properly deprovisioned before MVO deletion
- No FK constraint violations (CSOs deleted before MVO)
- Audit trail of the deprovisioning process
- Grace period allows reconnection/rehire scenarios
- Matches expected identity management behaviour

**Key Implementation Rule**: `ProcessMvoDeletionRuleAsync` must NEVER call `DeleteMetaverseObjectAsync`. It only sets `LastConnectorDisconnectedDate` and triggers evaluation of remaining CSOs.

### Sync Rule Deprovisioning

**Outbound (Export) Out-of-Scope**:
- `Disconnect` (default): Break join, leave CSO in target system
- `Delete`: Break join + delete CSO from target system
- Post-MVP: `Disable`, `MoveToArchiveOU`

**Inbound (Import) Out-of-Scope**:
- `Disconnect` (default): Break join, MVO deletion rules may apply
- `RemainJoined`: Keep join intact ("once managed, always managed")

## Technical Architecture

### Data Model Changes

**New Enums** (`CoreEnums.cs`):
- `MetaverseObjectOrigin`: Projected, Internal
- `OutboundDeprovisionAction`: Disconnect, Delete
- `InboundOutOfScopeAction`: RemainJoined, Disconnect

**MetaverseObject**:
- Remove: `ScheduledDeletionDate`
- Add: `LastConnectorDisconnectedDate` (DateTime?)
- Add: `Origin` (MetaverseObjectOrigin)
- Add: `IsPendingDeletion` (computed, not persisted)
- Add: `DeletionEligibleDate` (computed, not persisted)

**MetaverseObjectType**:
- Change: Default `DeletionRule` to `WhenLastConnectorDisconnected`
- Add: `DeletionTriggerConnectedSystemIds` (List<int>)

**SyncRule**:
- Add: `OutboundDeprovisionAction`
- Add: `InboundOutOfScopeAction`

### Scheduled Cleanup

Tactical solution: Worker checks during idle time for MVOs with expired grace periods.

**Query criteria**:
- Origin = Projected (not Internal)
- Type.DeletionRule = WhenLastConnectorDisconnected
- LastConnectorDisconnectedDate + GracePeriodDays <= NOW
- No connected system objects

**Safety checks**:
- Verify no connectors before deleting (race condition protection)
- Clear disconnection date if connectors found

## Implementation Phases

### Phase 1: Data Model ✅ COMPLETE
- [x] Add new enums to `CoreEnums.cs` (`MetaverseObjectOrigin`, `OutboundDeprovisionAction`, `InboundOutOfScopeAction`)
- [x] Update `MetaverseObject` with new properties (`LastConnectorDisconnectedDate`, `Origin`, `IsPendingDeletion`, `DeletionEligibleDate`)
- [x] Update `MetaverseObjectType` with trigger list (`DeletionTriggerConnectedSystemIds`)
- [x] Update `SyncRule` with deprovisioning actions (`OutboundDeprovisionAction`, `InboundOutOfScopeAction`)
- [x] Set admin MVO Origin to Internal in `JimApplication.InitialiseSsoAsync`
- [x] Create database migration

### Phase 2: API and PowerShell ✅ COMPLETE
- [x] Add PUT endpoint for `MetaverseObjectType` deletion rules
- [x] Create `Set-JIMMetaverseObjectType` PowerShell cmdlet
- [x] Unit tests for API endpoint

### Phase 3: Out-of-Scope Deprovisioning ✅ COMPLETE
- [x] Implement outbound scope evaluation in `ExportEvaluationServer`
- [x] Implement inbound scope evaluation in `SyncFullSyncTaskProcessor`
- [x] Remove TODO comment at `SyncRule.cs:86-87`
- [x] Unit tests for scope evaluation

### Phase 4: Scheduled Cleanup ✅ COMPLETE
- [x] Update `ProcessMvoDeletionRuleAsync` to use new fields (deferred deletion - sets `LastConnectorDisconnectedDate` only)
- [x] Add reconnection logic to clear disconnection date
- [x] Add cleanup loop in Worker idle time (`PerformHousekeepingAsync`)
- [x] Repository method `GetMvosEligibleForDeletionAsync`
- [x] Add `GetConnectedSystemObjectCountByMetaverseObjectIdAsync` for FK-safe deletion checks
- [x] Unit tests for cleanup logic

### Phase 5: Integration Tests 🔄 IN PROGRESS
- [x] Create Scenario 4 integration test script (`Invoke-Scenario4-DeletionRules.ps1`)
- [x] Configure deletion rules in setup
- [x] Leaver with grace period test (Test 1)
- [x] Reconnection before grace period expires test (Test 2)
- [x] Source deletion handling test (Test 3) - validates CSO deletion triggers MVO deletion rules
- [x] Admin account protection test (Test 4) - validates Origin=Internal protection
- [ ] Inbound scope filter test (Test 5) - **BLOCKED: requires API support for ObjectScopingCriteriaGroups**
- [ ] Outbound scope filter test (Test 6) - **BLOCKED: requires API support for ObjectScopingCriteriaGroups**
- [x] **All tests use PowerShell cmdlets, NOT direct API calls**

### Phase 6: Scope Filter API Support (Post-MVP)
To enable proper scope filter testing, the following API enhancements are needed:
- [ ] Add `OutboundDeprovisionAction` to `CreateSyncRuleRequest` and `UpdateSyncRuleRequest` DTOs
- [ ] Add `InboundOutOfScopeAction` to `CreateSyncRuleRequest` and `UpdateSyncRuleRequest` DTOs
- [ ] Add API endpoints for managing `ObjectScopingCriteriaGroups` on sync rules
- [ ] Update PowerShell cmdlets (`New-JIMSyncRule`, `Set-JIMSyncRule`) with scoping parameters
- [ ] Implement inbound scope filter integration test (Test 5)
- [ ] Implement outbound scope filter integration test (Test 6)

## Critical Files

- `/workspaces/JIM/JIM.Models/Core/CoreEnums.cs`
- `/workspaces/JIM/JIM.Models/Core/MetaverseObject.cs`
- `/workspaces/JIM/JIM.Models/Core/MetaverseObjectType.cs`
- `/workspaces/JIM/JIM.Models/Logic/SyncRule.cs`
- `/workspaces/JIM/JIM.Application/JimApplication.cs`
- `/workspaces/JIM/JIM.Web/Controllers/Api/MetaverseController.cs`
- `/workspaces/JIM/JIM.PowerShell/JIM/Public/Metaverse/Set-JIMMetaverseObjectType.ps1`
- `/workspaces/JIM/JIM.Worker/Worker.cs`
- `/workspaces/JIM/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs`
- `/workspaces/JIM/test/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1`

## Success Criteria

### MVP (Current Phase)
- All unit tests pass (579 tests)
- Build succeeds with 0 warnings and 0 errors
- Integration tests pass for:
  - Leaver with grace period (Test 1)
  - Reconnection before grace period expires (Test 2)
  - Source deletion handling (Test 3)
  - Admin account protection (Test 4)
- Admin can configure deletion rules via API and PowerShell
- Grace period status visible to admins (IsPendingDeletion, DeletionEligibleDate)
- MVOs actually deleted when grace period expires
- Admin/service accounts protected from automatic deletion

### Post-MVP (Scope Filter Testing)
- API supports ObjectScopingCriteriaGroups configuration
- Integration tests pass for:
  - Inbound scope filter changes (Test 5)
  - Outbound scope filter changes (Test 6)

## Risk Mitigations

1. **Admin Account Protection**: `MetaverseObjectOrigin.Internal` prevents automatic deletion
2. **Race Conditions**: Worker verifies no connectors before deleting
3. **Performance**: Set-based SQL queries for scope evaluation
4. **Grace Period Changes**: Timestamp approach allows admin changes to affect existing orphans
5. **Backward Compatibility**: Pre-MVP, can reset JIM - no migration concerns

## Dependencies

- None (all changes are internal to JIM)

## Future Considerations

- JIM.Scheduler for configurable cleanup intervals
- `Disable` and `MoveToArchiveOU` deprovisioning actions (Post-MVP)
- UI for viewing pending deletions and grace period status
