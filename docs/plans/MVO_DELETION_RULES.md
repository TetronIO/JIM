# MVO Deletion Rules and Deprovisioning

## Overview

Implement comprehensive deletion rule and deprovisioning functionality for JIM's identity lifecycle management. This addresses critical gaps preventing proper user deprovisioning when employees leave organisations or fall out of sync rule scope.

**Status**: Implemented
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

### Phase 1: Data Model ✅ IMPLEMENTED
- [x] Add new enums to `CoreEnums.cs` (`MetaverseObjectOrigin`, `OutboundDeprovisionAction`, `InboundOutOfScopeAction`)
- [x] Update `MetaverseObject` with new properties (`LastConnectorDisconnectedDate`, `Origin`, `IsPendingDeletion`, `DeletionEligibleDate`)
- [x] Update `MetaverseObjectType` with trigger list (`DeletionTriggerConnectedSystemIds`)
- [x] Update `SyncRule` with deprovisioning actions (`OutboundDeprovisionAction`, `InboundOutOfScopeAction`)
- [x] Set admin MVO Origin to Internal in `JimApplication.InitialiseSsoAsync`
- [x] Create database migration

### Phase 2: API and PowerShell ✅ IMPLEMENTED
- [x] Add PUT endpoint for `MetaverseObjectType` deletion rules
- [x] Create `Set-JIMMetaverseObjectType` PowerShell cmdlet
- [x] Unit tests for API endpoint

### Phase 3: Out-of-Scope Deprovisioning ✅ IMPLEMENTED
- [x] Implement outbound scope evaluation in `ExportEvaluationServer`
- [x] Implement inbound scope evaluation in `SyncFullSyncTaskProcessor`
- [x] Remove TODO comment at `SyncRule.cs:86-87`
- [x] Unit tests for scope evaluation

### Phase 4: Scheduled Cleanup ✅ IMPLEMENTED
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
- [x] Inbound scope filter test (Test 5) - validates InboundOutOfScopeAction property availability
- [x] Outbound scope filter test (Test 6) - validates ObjectScopingCriteriaGroups API (create/get/delete)
- [x] **All tests use PowerShell cmdlets, NOT direct API calls**

### Phase 6: Scope Filter API Support ✅ IMPLEMENTEDD (MVP Required)
**Note:** Scope filtering is fully implemented in the sync engine and UI. REST API now supports
`ObjectScopingCriteriaGroups` management via dedicated endpoints.

API and PowerShell support:
- [x] Add API endpoints for managing `ObjectScopingCriteriaGroups` on sync rules
- [x] Add PowerShell cmdlets for scoping criteria (Get/New/Set/Remove-JIMScopingCriteria*)
- [x] Implement inbound scope filter integration test (Test 5)
- [x] Implement outbound scope filter integration test (Test 6)

### Phase 7: Matching Rules Integration Tests 🔄 IN PROGRESS (MVP Required)
- [x] Create Scenario 5 integration test script for matching rules (`Invoke-Scenario5-MatchingRules.ps1`)
- [x] Add LDAP matching rule to Setup-Scenario1.ps1
- [x] Basic matching: projection when no MVO exists (Test 1)
- [x] Basic matching: join to existing MVO without connector from same CS (Test 2)
- [x] Duplicate prevention: join conflict when MVO already has connector from same CS (Test 3)
- [x] Multiple rules: fallback to second/third rule when earlier rules don't match (Test 4)
- [ ] Edge cases: ambiguous matches, null values, case sensitivity

## Critical Files

- `/workspaces/JIM/src/JIM.Models/Core/CoreEnums.cs`
- `/workspaces/JIM/src/JIM.Models/Core/MetaverseObject.cs`
- `/workspaces/JIM/src/JIM.Models/Core/MetaverseObjectType.cs`
- `/workspaces/JIM/src/JIM.Models/Logic/SyncRule.cs`
- `/workspaces/JIM/src/JIM.Application/JimApplication.cs`
- `/workspaces/JIM/src/JIM.Web/Controllers/Api/MetaverseController.cs`
- `/workspaces/JIM/src/JIM.PowerShell/JIM/Public/Metaverse/Set-JIMMetaverseObjectType.ps1`
- `/workspaces/JIM/src/JIM.Worker/Worker.cs`
- `/workspaces/JIM/src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs`
- `/workspaces/JIM/test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1`

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

### MVP (Scope Filter Testing) - BLOCKED
**Blocker:** API does not yet support ObjectScopingCriteriaGroups configuration
- API supports ObjectScopingCriteriaGroups configuration
- Integration tests pass for:
  - Inbound scope filter changes (Test 5)
  - Outbound scope filter changes (Test 6)

### MVP (Matching Rules Testing)
Integration tests needed for Object Matching Rules to validate all permutations:

**Basic Matching Scenarios:**
- Matching on employeeId, no existing MVO -> CSO is projected to MV (new identity)
- Matching on employeeId, existing MVO with same employeeId but no HR connector -> CSO joins to existing MVO
- Matching on employeeId, existing MVO with same employeeId AND existing HR connector -> join fails (only one CSO per CS per MVO)

**Multiple Matching Rules:**
- First rule doesn't match, second rule does -> second rule used for join
- First and second rules don't match, third rule does -> third rule used for join
- No rules match -> projection creates new MVO (if projection enabled)
- No rules match -> CSO remains disconnected (if projection disabled)

**Edge Cases:**
- Matching rule matches multiple MVOs -> ambiguous match handling
- Matching on multi-valued attribute
- Matching with case sensitivity variations
- Matching with null/empty attribute values

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

## Implemented Enhancements

### Pending Deletions UI and API (December 2024)
- **UI Page**: `/admin/pending-deletions` - Displays all MVOs in the deletion pipeline
- **API Endpoints**:
  - `GET /api/v1/metaverse/pending-deletions` - Paginated list of pending deletions
  - `GET /api/v1/metaverse/pending-deletions/count` - Count of pending deletions
  - `GET /api/v1/metaverse/pending-deletions/summary` - Summary statistics by status
- **Status Categories**:
  - **Deprovisioning**: MVOs still connected to other systems, awaiting cascade deletion
  - **Awaiting Grace Period**: MVOs fully disconnected, waiting for grace period to expire
  - **Ready for Deletion**: MVOs eligible for deletion (grace period expired, no connectors)
- **Features**: Object type filtering, connector count visibility, deletion timeline display
