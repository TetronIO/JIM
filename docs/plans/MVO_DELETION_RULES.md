# MVO Deletion Rules and Deprovisioning

## Overview

Implement comprehensive deletion rule and deprovisioning functionality for JIM's identity lifecycle management. This addresses critical gaps preventing proper user deprovisioning when employees leave organisations or fall out of sync rule scope.

**Status**: Planned
**Milestone**: MVP
**Related Issues**: #120 (scheduled deletion cleanup), #173 (integration testing)

## Business Value

- **Complete JML lifecycle**: Joiner-Mover-Leaver processes require proper deletion/deprovisioning
- **Compliance**: Organisations need audit trails showing when identities were scheduled for deletion
- **Flexibility**: Different object types may need different deletion behaviours
- **Admin visibility**: Grace period status must be visible to administrators

## Current State

- `MetaverseObjectType` has `DeletionRule` and `DeletionGracePeriodDays` properties
- `MetaverseObject` has `ScheduledDeletionDate` property (to be replaced)
- Sync engine sets/clears scheduled dates during full sync
- Unit tests validate deletion rule processing
- **Gap**: MVOs are never actually deleted after grace period expires
- **Gap**: No API/PowerShell to configure deletion rules
- **Gap**: Out-of-scope deprovisioning not implemented (TODO at `SyncRule.cs:86-87`)
- **Gap**: Integration tests gloss over Leaver scenarios

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

### Phase 1: Data Model
1. Add new enums to `CoreEnums.cs`
2. Update `MetaverseObject` with new properties
3. Update `MetaverseObjectType` with trigger list
4. Update `SyncRule` with deprovisioning actions
5. Set admin MVO Origin to Internal in `JimApplication.InitialiseSsoAsync`
6. Create database migration

### Phase 2: API and PowerShell
1. Add PUT endpoint for `MetaverseObjectType` deletion rules
2. Create `Set-JIMMetaverseObjectType` PowerShell cmdlet
3. Unit tests for API endpoint

### Phase 3: Out-of-Scope Deprovisioning
1. Implement outbound scope evaluation in `ExportEvaluationServer`
2. Implement inbound scope evaluation in `SyncFullSyncTaskProcessor`
3. Remove TODO comment at `SyncRule.cs:86-87`
4. Unit tests for scope evaluation

### Phase 4: Scheduled Cleanup
1. Update `ProcessMvoDeletionRuleAsync` to use new fields
2. Add reconnection logic to clear disconnection date
3. Add cleanup loop in Worker idle time
4. Repository method `GetMvosEligibleForDeletionAsync`
5. Unit tests for cleanup logic

### Phase 5: Integration Tests
1. Configure deletion rules in `Setup-Scenario1.ps1`
2. Enhanced Leaver test (FAIL not warn)
3. Immediate deletion test
4. Grace period with reconnection test
5. Out-of-scope deprovisioning test
6. Into-scope provisioning test
7. **All tests use PowerShell cmdlets, NOT direct API calls**

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

- All unit tests pass
- All integration tests pass (Leaver, Reconnection, OutOfScope, IntoScope)
- Build succeeds with 0 errors
- Admin can configure deletion rules via API and PowerShell
- Grace period status visible to admins (IsPendingDeletion, DeletionEligibleDate)
- MVOs actually deleted when grace period expires
- Admin/service accounts protected from automatic deletion

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
