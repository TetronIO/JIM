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

### DeletionRule Enum Values

| Value | Behaviour |
|-------|-----------|
| `Manual` | MVO is never automatically deleted. Must be manually deleted by admin. |
| `WhenLastConnectorDisconnected` | MVO is deleted when **ALL** CSOs are disconnected. |
| `WhenAuthoritativeSourceDisconnected` | MVO is deleted when **ANY** selected authoritative source disconnects (requires selecting at least one source). |

### DeletionTriggerConnectedSystemIds (Required for WhenAuthoritativeSourceDisconnected)

When `DeletionRule = WhenAuthoritativeSourceDisconnected`, you must specify which systems are authoritative:

| Configuration | Behaviour |
|--------------|-----------|
| One system `[1]` | MVO is deleted when that specific system disconnects |
| Multiple systems `[1, 2]` | MVO is deleted when **ANY** of the specified systems disconnect |

**Note**: Only "contributing systems" (systems with inbound sync rules for this object type) can be selected as authoritative sources.

#### Example: HR → AD Synchronisation

```
Source System: HR (ID: 1) - Authoritative source
Target System: AD (ID: 2) - Provisioned target

Configuration:
  DeletionRule: WhenAuthoritativeSourceDisconnected
  DeletionGracePeriodDays: 0
  DeletionTriggerConnectedSystemIds: [1]  // HR is the authoritative source

Scenario: User deleted from HR
  1. HR CSO becomes Obsolete (via delta import detecting deletion)
  2. Sync processes Obsolete CSO, disconnects it from MVO
  3. Deletion rule evaluates: HR (system ID 1) is an authoritative source
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
  DeletionRule: WhenAuthoritativeSourceDisconnected
  DeletionGracePeriodDays: 7
  DeletionTriggerConnectedSystemIds: [1, 2]  // Either AD is authoritative

Behaviour:
  - Deletion is triggered if user is deleted from Corporate AD OR Cloud AD
  - Deletion is NOT triggered if only the Application DB CSO disconnects
  - 7-day grace period before actual deletion
```

## Two-Stage Deletion Process

MVO deletion is a **two-stage process** for safety and auditability:

### Stage 1: Marking (During Sync)

When a CSO is disconnected from an MVO, `ProcessMvoDeletionRuleAsync()` evaluates:

1. Is `DeletionRule` NOT `Manual`?
2. Is the MVO `Origin` set to `Projected` (not `Internal`)?
3. Does the disconnection meet the trigger criteria based on `DeletionRule`?
   - `WhenLastConnectorDisconnected`: Are ALL CSOs now disconnected?
   - `WhenAuthoritativeSourceDisconnected`: Is the disconnecting system in `DeletionTriggerConnectedSystemIds`?

If all conditions are met:
- Set `MVO.LastConnectorDisconnectedDate = DateTime.UtcNow`
- Log the action for audit

### Stage 2: Deletion (During Housekeeping)

The worker's `PerformHousekeepingAsync()` runs periodically (max every 60 seconds) and:

1. Queries for MVOs eligible for deletion:
   - `Origin = Projected`
   - `DeletionRule` is NOT `Manual`
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
| `DeletionRule.WhenAuthoritativeSourceDisconnected` enum | ✅ Implemented | Enum value added with API/UI support |
| `DeletionTriggerConnectedSystemIds` UI | ✅ Implemented | Admin UI for selecting authoritative sources |
| `DeletionTriggerConnectedSystemIds` backend logic | ✅ Implemented | Logic in `ProcessMvoDeletionRuleAsync` |
| `DeletionGracePeriodDays` | ✅ Implemented | Grace period is respected by housekeeping |
| Internal Origin Protection | ✅ Implemented | Internal MVOs are skipped |
| Deprovisioning on MVO Deletion | ✅ Implemented | Delete exports created for Provisioned CSOs |

### Recently Implemented (January 2026)

1. **`WhenAuthoritativeSourceDisconnected` enum value** - Added to `MetaverseObjectDeletionRule` enum in `CoreEnums.cs`
2. **API support** - `MetaverseController.UpdateObjectTypeAsync()` validates that `WhenAuthoritativeSourceDisconnected` requires at least one authoritative source
3. **PowerShell support** - `Set-JIMMetaverseObjectType` cmdlet accepts the new enum value
4. **Admin UI - Detail page** - `MetaverseObjectTypeDetail.razor` shows deletion rules configuration with:
   - Dropdown for selecting deletion rule (Manual, When Last Connector Disconnected, When Authoritative Source Disconnected)
   - Grace period input field
   - Checkboxes for selecting authoritative sources (only visible when `WhenAuthoritativeSourceDisconnected` is selected)
   - Only "contributing systems" (systems with inbound sync rules for this object type) appear as selectable authoritative sources
5. **Admin UI - List view** - `SchemaObjectTypeList.razor` shows deletion rule column with coloured chips and tooltips

## Phase 1 Implementation Status: Implemented ✅

All deletion rule features are now fully implemented:
- `WhenAuthoritativeSourceDisconnected` enum value
- `DeletionTriggerConnectedSystemIds` model properties
- API validation for authoritative source requirements
- PowerShell cmdlet support
- Admin UI for configuration
- Backend logic in `SyncTaskProcessorBase.ProcessMvoDeletionRuleAsync()`

**Backend implementation details**: When a CSO is disconnected (obsolete or out-of-scope), `ProcessMvoDeletionRuleAsync()` evaluates the MVO type's deletion rule:
- `Manual`: No automatic deletion
- `WhenLastConnectorDisconnected`: Mark for deletion only when all CSOs are disconnected
- `WhenAuthoritativeSourceDisconnected`: Mark for deletion when ANY authoritative source disconnects, regardless of remaining CSOs

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
- `WhenLastConnectorDisconnected_WhenLastCsoDisconnected_MvoMarkedForDeletionAsync` ✅
- `WhenLastConnectorDisconnected_WhenOneCsoDisconnectedButOthersRemain_MvoNotMarkedAsync` ✅
- `DeletionTrigger_WhenTriggerSystemDisconnects_MvoMarkedForDeletionAsync` ✅
- `DeletionTrigger_WhenNonTriggerSystemDisconnects_MvoNotMarkedAsync` ✅
- `DeletionTrigger_MultiSourceScenario_OnlyAuthoritativeSourceTriggersDeleteAsync` ✅ (NEW)
- `GracePeriod_WhenSet_MvoMarkedButNotImmediatelyEligibleAsync` ✅

### Integration Tests

Location: `test/integration/scenarios/Invoke-Scenario8-CrossDomainEntitlementSync.ps1`

The DeleteGroup test validates end-to-end deletion flow:
- Source AD group deletion triggers `WhenAuthoritativeSourceDisconnected` rule
- MVO is marked for deletion (`LastConnectorDisconnectedDate` set)
- Housekeeping deprovisions the group from Target AD
- Test verifies MVO state via API after sync

## Related GitHub Issues

The following GitHub issues define additional deletion rule features. This section provides a consolidated analysis of all proposed deletion options, their usefulness for ILM products, and implementation priorities.

### Issue Summary

| Issue | Title | Status | Priority |
|-------|-------|--------|----------|
| #115 | WhenAuthoritativeSourceDisconnected (DeletionTriggerConnectedSystemIds) | ✅ **Closed** | **P1 - Critical** |
| #116 | ExcludedFromLastConnectorCheck | Open | P3 - Low |
| #117 | Soft Delete / Recycle Bin | Open | P2 - Medium |
| #118 | Conditional MVO Deletion (Attribute-Based) | Open | P2 - Medium |
| #119 | Authoritative Source Hierarchy | Open | P3 - Low |
| #126 | CSO Deletion Behaviour Options | Open | P2 - Medium |

---

### #115: Authoritative Source Triggers (DeletionTriggerConnectedSystemIds)

**Description**: Delete MVO when specific "authoritative" connected systems disconnect, regardless of whether other CSOs remain connected.

**Current State**: ✅ **COMPLETE**
- ✅ `WhenAuthoritativeSourceDisconnected` enum value added
- ✅ `DeletionTriggerConnectedSystemIds` property exists in model
- ✅ API validation ensures authoritative sources are specified when rule is selected
- ✅ PowerShell cmdlet supports the new enum value
- ✅ Admin UI allows configuration of deletion rules and authoritative source selection
- ✅ Backend logic implemented in `ProcessMvoDeletionRuleAsync()`
- ✅ Unit tests covering multi-source scenarios
- ✅ Integration test (Scenario 8) validates end-to-end deletion flow

**Use Cases**:
- HR → AD sync: Delete identity when HR (source of truth) removes employee, even if AD CSO exists
- Multi-domain sync: Delete when primary domain removes user, propagate to secondary domains

**ILM Value Assessment**: ⭐⭐⭐⭐⭐ **Essential**
- This is the **core Leaver scenario** for any identity management system
- Without this, JIM cannot properly deprovision target systems when source systems remove identities
- Every ILM deployment requires distinguishing "source" vs "target" systems

**Implementation Priority**: **P1 - Critical (MVP Blocker)**
- Required for Scenario 8 integration test to pass
- Fundamental to JML (Joiner-Mover-Leaver) lifecycle support
- Property already exists - only logic implementation needed

---

### #116: Excluded Systems (ExcludedFromLastConnectorCheck)

**Description**: Exclude specific connected systems from the "last connector" check. MVO deletion only considers non-excluded systems.

**Use Cases**:
- ServiceDesk creates shadow accounts that shouldn't prevent deletion
- Audit/logging systems that create read-only connector records
- Legacy systems being phased out

**ILM Value Assessment**: ⭐⭐ **Niche**
- Inverse of #115 - solves same problem from opposite direction
- Less intuitive than explicitly specifying authoritative sources
- Only useful when you have many source systems and want to exclude a few

**Implementation Priority**: **P3 - Low (Post-MVP)**
- #115 covers most use cases more intuitively
- Can be implemented later if customers request it
- Not blocking any known scenarios

---

### #117: Soft Delete / Recycle Bin

**Description**: Instead of immediately deleting MVOs, move them to a "soft deleted" state with configurable retention period before permanent deletion.

**Use Cases**:
- Compliance requirements for data retention
- Recovery from accidental deletions or sync errors
- "Undo" capability for admin mistakes
- Regulatory hold requirements

**ILM Value Assessment**: ⭐⭐⭐⭐ **High Value**
- Common feature in enterprise identity systems
- Addresses real customer concerns about data loss
- Provides safety net beyond grace periods
- Required for some compliance scenarios (GDPR right to erasure with retention)

**Implementation Priority**: **P2 - Medium (Post-MVP)**
- Not blocking current functionality
- Grace periods provide partial coverage for accidental deletion
- Significant implementation effort (new status, queries, UI, cleanup job)
- Should be implemented before GA for enterprise customers

**Proposed Design**:
```
MetaverseObject:
  - Status: Active | SoftDeleted
  - SoftDeletedDate: DateTime?
  - PermanentDeletionDate: DateTime? (calculated)

MetaverseObjectType:
  - SoftDeleteRetentionDays: int? (null = no soft delete, 0 = immediate)
```

---

### #118: Conditional Deletion (Attribute-Based)

**Description**: Only proceed with MVO deletion if specified attribute conditions are met.

**Use Cases**:
- "Only delete if EmployeeStatus != 'Active'" - prevents deleting active employees
- "Only delete if AccountDisabled == true" - ensures accounts are disabled first
- "Only delete if TerminationDate < Today - 90 days" - business date retention

**ILM Value Assessment**: ⭐⭐⭐ **Medium-High**
- Adds business logic layer to deletion decisions
- Prevents accidental deletion of active identities
- Useful for complex HR integration scenarios
- Can enforce "disable before delete" patterns

**Implementation Priority**: **P2 - Medium (Post-MVP)**
- Grace periods handle most "oops" scenarios
- Adds complexity to deletion evaluation
- Expression evaluation already exists (DynamicExpresso) - can reuse
- Nice-to-have for sophisticated deployments

**Proposed Design**:
```
MetaverseObjectType:
  - DeletionConditionExpression: string?
    Examples:
    - "mv[\"EmployeeStatus\"] != \"Active\""
    - "mv[\"TerminationDate\"] < DateTime.UtcNow.AddDays(-90)"
```

---

### #119: Authoritative Source Hierarchy

**Description**: Extend authoritative sources to support priority ordering and AND/OR logic (e.g., "delete only when ALL authoritative sources are gone" vs "delete when ANY is gone").

**Use Cases**:
- "Delete only when BOTH HR AND AD are disconnected" - redundant source validation
- Multiple HR systems with different authority levels
- Complex enterprise scenarios with regional HR systems

**ILM Value Assessment**: ⭐⭐ **Niche**
- Most deployments have a single authoritative source
- Adds significant complexity to deletion logic
- Edge case for very large enterprises
- #115's "ANY in list triggers" covers most practical scenarios

**Implementation Priority**: **P3 - Low (Post-MVP, if requested)**
- #115 covers 90%+ of use cases
- Complex to explain to admins
- Can be added later if customers demonstrate need
- Consider if #115 + conditional deletion (#118) covers these scenarios

---

### #126: CSO Deletion Behaviour Options

**Description**: Configurable behaviour for what happens to target system CSOs when MVO is deleted.

**Current State**: MVP uses `JoinType = Provisioned` check - only provisioned CSOs are deleted, matched CSOs are disconnected.

**Proposed Options**:
| Option | Behaviour |
|--------|-----------|
| `ProvisionedOnly` | Only delete CSOs that JIM created (safest default) |
| `AllJoined` | Delete all joined CSOs regardless of JoinType |
| `DisconnectOnly` | Never delete - just disconnect from MVO |

**Use Cases**:
- AD cleanup: Delete all accounts JIM manages, not just ones it created
- Application integration: Leave orphaned accounts for manual review
- Compliance: Keep audit trail by preserving CSO records

**ILM Value Assessment**: ⭐⭐⭐ **Medium**
- Current default (`ProvisionedOnly`) is correct for most scenarios
- `DisconnectOnly` useful for cautious deployments
- `AllJoined` dangerous - needs clear warnings

**Implementation Priority**: **P2 - Medium (Post-MVP)**
- Current behaviour is reasonable default
- Can be added when customers need more control
- Related to scope fallout behaviour (separate concern)

---

### Implementation Roadmap

#### Phase 1: MVP ✅ COMPLETE
| Feature | Issue | Status |
|---------|-------|--------|
| `WhenAuthoritativeSourceDisconnected` enum + UI | #115 | ✅ **Implemented** |
| `DeletionTriggerConnectedSystemIds` backend logic | #115 | ✅ **Implemented** |
| Multi-source deletion rule unit tests | #115 | ✅ **Implemented** |
| Scenario 8 integration test validation | #115 | ✅ **Implemented** |

#### Phase 2: Post-MVP (Near-term)
| Feature | Issue | Rationale |
|---------|-------|-----------|
| Soft Delete / Recycle Bin | #117 | Enterprise compliance |
| Conditional Deletion | #118 | Business rule enforcement |
| CSO Deletion Behaviour | #126 | Admin control |

#### Phase 3: Future (If Requested)
| Feature | Issue | Rationale |
|---------|-------|-----------|
| Excluded Systems | #116 | Niche use case |
| Source Hierarchy | #119 | Complex enterprise only |

---

### Decision Matrix

When choosing which deletion features to prioritise, consider:

| Criterion | #115 | #116 | #117 | #118 | #119 | #126 |
|-----------|------|------|------|------|------|------|
| Blocks MVP scenarios | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Common ILM requirement | ✅ | ❌ | ✅ | ⚠️ | ❌ | ⚠️ |
| Implementation complexity | Low | Low | High | Medium | High | Low |
| Risk if not implemented | High | Low | Medium | Low | Low | Low |
| Customer requests | Yes | No | Yes | Maybe | No | Maybe |

**Legend**: ✅ Yes | ⚠️ Partial | ❌ No

---

## Related Documentation

- [Import ChangeType Design](IMPORT_CHANGETYPE_DESIGN.md) - How connectors signal deletions
- [LDAP Connector Improvements](LDAP_CONNECTOR_IMPROVEMENTS.md) - Delta import deletion detection

## References

- `src/JIM.Models/Core/MetaverseObjectType.cs` - Deletion rule configuration
- `src/JIM.Models/Core/MetaverseObject.cs` - MVO deletion state properties
- `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` - `ProcessMvoDeletionRuleAsync()`
- `src/JIM.Worker/Worker.cs` - `PerformHousekeepingAsync()`
- `src/JIM.Application/Servers/ExportEvaluationServer.cs` - `EvaluateMvoDeletionAsync()`
- `src/JIM.PostgresData/Repositories/MetaverseRepository.cs` - `GetMetaverseObjectsEligibleForDeletionAsync()`