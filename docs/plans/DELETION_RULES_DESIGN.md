# Deletion Rules Design

> **Status**: Partially Implemented
>
> Design document for automatic Metaverse Object deletion based on connector disconnections.

## Overview

Deletion rules control when and how Metaverse Objects (MVOs) are automatically deleted based on Connected System Object (CSO) disconnections. This is critical for maintaining data hygiene and ensuring that downstream systems (targets) are cleaned up when source systems remove identities.

## Business Context

In identity management, the lifecycle of an identity typically follows a pattern:

1. **Joiner**: Identity is created in source system (e.g., HR) → projected to metaverse → provisioned to target systems (e.g., AD, applications)
2. **Mover**: Identity attributes change → synced through metaverse → updated in target systems
3. **Leaver**: Identity is deleted from source system → MVO should be deleted → identity should be deprovisioned from target systems

The deletion rule system handles the **Leaver** scenario automatically.

## Design Goals

1. **Authoritative Source Control**: Deletion should be triggered by changes in authoritative source systems, not by changes in target systems
2. **Configurable Behaviour**: Administrators should be able to configure different deletion behaviours for different object types
3. **Grace Periods**: Support for delayed deletion to allow for corrections/reversals
4. **Protection of Internally Managed Objects**: Objects created directly in JIM (not projected from a Connected System) should never be automatically deleted
5. **Auditability**: All deletion decisions should be logged for compliance

## Deletion Rule Configuration

Deletion rules are configured on the `MetaverseObjectType` entity:

| Property | Type | Description |
|----------|------|-------------|
| `DeletionRule` | Enum | When to trigger deletion evaluation |
| `DeletionGracePeriodDays` | int? | Days to wait before actual deletion (null/0 = immediate) |
| `DeletionTriggerConnectedSystemIds` | List<int> | Specific systems that trigger deletion when they disconnect |

### DeletionRule Values

| Value | Behaviour |
|-------|-----------|
| `Manual` | MVO is never automatically deleted. Must be manually deleted by admin. |
| `WhenLastConnectorDisconnected` | MVO is marked for deletion when connector disconnections meet the trigger criteria. |

### DeletionTriggerConnectedSystemIds Behaviour

This property controls **which** system disconnections trigger deletion:

| Configuration | Behaviour |
|--------------|-----------|
| Empty list `[]` | MVO is deleted only when **ALL** CSOs are disconnected (legacy behaviour) |
| Specific IDs `[1, 2]` | MVO is deleted when **ANY** of the specified systems disconnect, regardless of whether other CSOs remain |

#### Example: HR → AD Synchronisation

```
Source System: HR (ID: 1) - Authoritative source
Target System: AD (ID: 2) - Provisioned target

Configuration:
  DeletionRule: WhenLastConnectorDisconnected
  DeletionGracePeriodDays: 0
  DeletionTriggerConnectedSystemIds: [1]  // Only HR triggers deletion

Scenario: User deleted from HR
  1. HR CSO becomes Obsolete (via delta import detecting deletion)
  2. Sync processes Obsolete CSO, disconnects it from MVO
  3. Deletion rule evaluates: HR (system ID 1) is in trigger list
  4. MVO.LastConnectorDisconnectedDate is set (marking MVO for deletion)
  5. AD CSO is still connected, but deletion is triggered because HR is authoritative
  6. Housekeeping runs:
     - Calls EvaluateMvoDeletionAsync() → creates Delete pending export for AD CSO
     - Deletes MVO
  7. Export runs → deletes user from AD
```

#### Example: Bidirectional AD Sync (Multiple Sources)

```
System 1: Corporate AD (ID: 1) - Primary authoritative source
System 2: Cloud AD (ID: 2) - Secondary authoritative source
System 3: Application DB (ID: 3) - Target only

Configuration:
  DeletionRule: WhenLastConnectorDisconnected
  DeletionGracePeriodDays: 7
  DeletionTriggerConnectedSystemIds: [1, 2]  // Either AD can trigger

Behaviour:
  - Deletion is triggered if user is deleted from Corporate AD OR Cloud AD
  - Deletion is NOT triggered if only the Application DB CSO disconnects
  - 7-day grace period before actual deletion
```

## Two-Stage Deletion Process

MVO deletion is a **two-stage process** for safety and auditability:

### Stage 1: Marking (During Sync)

When a CSO is disconnected from an MVO, `ProcessMvoDeletionRuleAsync()` evaluates:

1. Is `DeletionRule` set to `WhenLastConnectorDisconnected`?
2. Is the MVO `Origin` set to `Projected` (not `Internal`)?
3. Does the disconnection meet the trigger criteria?
   - If `DeletionTriggerConnectedSystemIds` is empty: Are ALL CSOs now disconnected?
   - If `DeletionTriggerConnectedSystemIds` has values: Is the disconnecting system in the list?

If all conditions are met:
- Set `MVO.LastConnectorDisconnectedDate = DateTime.UtcNow`
- Log the action for audit

### Stage 2: Deletion (During Housekeeping)

The worker's `PerformHousekeepingAsync()` runs periodically (max every 60 seconds) and:

1. Queries for MVOs eligible for deletion:
   - `Origin = Projected`
   - `DeletionRule = WhenLastConnectorDisconnected`
   - `LastConnectorDisconnectedDate` is set
   - Grace period has elapsed (or no grace period configured)
   - No CSOs remain connected (for safety)

2. For each eligible MVO:
   - Calls `EvaluateMvoDeletionAsync()` to create Delete pending exports for any remaining Provisioned CSOs
   - Deletes the MVO from the database

### Why Two Stages?

1. **Reversibility**: During the grace period, reconnecting a CSO clears `LastConnectorDisconnectedDate`
2. **Audit Trail**: The timestamp provides a clear record of when deletion was triggered
3. **Batch Efficiency**: Housekeeping can process deletions in batches
4. **Separation of Concerns**: Sync focuses on MVO changes; housekeeping focuses on cleanup

## Protection Mechanisms

### Internal Origin Protection

MVOs with `Origin = Internal` are **never** automatically deleted. These include:
- Admin accounts created directly in JIM
- Service accounts
- Any MVO not projected from a Connected System

### Grace Period

When `DeletionGracePeriodDays` is set:
- MVO is marked for deletion immediately
- Actual deletion waits until grace period expires
- During grace period, reconnecting a source CSO cancels the deletion
- Useful for handling accidental deletions or temporary leaves

### Computed Properties

MVOs expose computed properties for UI display:
- `IsPendingDeletion`: True if `LastConnectorDisconnectedDate` is set
- `DeletionEligibleDate`: When the grace period expires (if applicable)

## Deprovisioning Behaviour

When an MVO is deleted, downstream CSOs need to be handled. This is controlled by the export sync rule's `OutboundDeprovisionAction`:

| Action | Behaviour |
|--------|-----------|
| `Disconnect` | Break the join but leave the object in the target system |
| `Delete` | Create a Delete pending export to remove the object from the target system |

**Important**: Only CSOs with `JoinType = Provisioned` trigger delete exports. Matched CSOs are only disconnected.

## Current Implementation Status

| Feature | Status | Notes |
|---------|--------|-------|
| `DeletionRule.Manual` | ✅ Implemented | MVOs are protected from automatic deletion |
| `DeletionRule.WhenLastConnectorDisconnected` (all CSOs) | ✅ Implemented | Works when all CSOs disconnect |
| `DeletionTriggerConnectedSystemIds` | ❌ Not Implemented | Property exists but logic not in `ProcessMvoDeletionRuleAsync` |
| `DeletionGracePeriodDays` | ✅ Implemented | Grace period is respected by housekeeping |
| Internal Origin Protection | ✅ Implemented | Internal MVOs are skipped |
| Deprovisioning on MVO Deletion | ✅ Implemented | Delete exports created for Provisioned CSOs |

## Implementation Gap

The `DeletionTriggerConnectedSystemIds` feature is defined in the model but **not implemented** in `ProcessMvoDeletionRuleAsync()`. The current implementation only checks `remainingCsoCount == 0`.

### Required Changes

In `SyncTaskProcessorBase.ProcessMvoDeletionRuleAsync()`:

```csharp
// Current logic (simplified):
if (remainingCsoCount == 0)
{
    await ProcessMvoDeletionRuleAsync(mvo);
}

// Required logic:
var triggerIds = mvo.Type.DeletionTriggerConnectedSystemIds;
if (triggerIds != null && triggerIds.Count > 0)
{
    // Trigger deletion if the disconnecting system is in the trigger list
    if (triggerIds.Contains(_connectedSystem.Id))
    {
        await ProcessMvoDeletionRuleAsync(mvo);
    }
}
else
{
    // Legacy behaviour: only delete when ALL CSOs are disconnected
    if (remainingCsoCount == 0)
    {
        await ProcessMvoDeletionRuleAsync(mvo);
    }
}
```

## Testing Considerations

### Unit Tests

Location: `test/JIM.Worker.Tests/Repositories/MetaverseRepositoryEligibleForDeletionTests.cs`

Existing coverage:
- Grace period logic
- Origin protection
- Basic deletion eligibility

### Workflow Tests

Location: `test/JIM.Worker.Tests/Workflows/DeletionRuleWorkflowTests.cs`

Tests to be fixed/added:
- `Manual_WhenLastCsoDisconnected_MvoNotMarkedForDeletionAsync` ✅
- `WhenLastConnectorDisconnected_WhenLastCsoDisconnected_MvoMarkedForDeletionAsync` (needs fix)
- `WhenLastConnectorDisconnected_WhenOneCsoDisconnectedButOthersRemain_MvoNotMarkedAsync` (needs fix)
- `DeletionTrigger_WhenTriggerSystemDisconnects_MvoMarkedForDeletionAsync` (needs implementation)
- `DeletionTrigger_WhenNonTriggerSystemDisconnects_MvoNotMarkedAsync` (needs implementation)
- `GracePeriod_WhenSet_MvoMarkedButNotImmediatelyEligibleAsync` (needs fix)

### Integration Tests

Location: `test/integration/scenarios/Invoke-Scenario8-CrossDomainEntitlementSync.ps1`

The DeleteGroup test validates end-to-end deletion flow from Source AD deletion through to Target AD deprovisioning.

## Related Documentation

- [Import ChangeType Design](IMPORT_CHANGETYPE_DESIGN.md) - How connectors signal deletions
- [LDAP Connector Improvements](LDAP_CONNECTOR_IMPROVEMENTS.md) - Delta import deletion detection

## References

- `JIM.Models/Core/MetaverseObjectType.cs` - Deletion rule configuration
- `JIM.Models/Core/MetaverseObject.cs` - MVO deletion state properties
- `JIM.Worker/Processors/SyncTaskProcessorBase.cs` - `ProcessMvoDeletionRuleAsync()`
- `JIM.Worker/Worker.cs` - `PerformHousekeepingAsync()`
- `JIM.Application/Servers/ExportEvaluationServer.cs` - `EvaluateMvoDeletionAsync()`
- `JIM.PostgresData/Repositories/MetaverseRepository.cs` - `GetMetaverseObjectsEligibleForDeletionAsync()`