# Drift Detection and Remediation Design Document

- **Status:** Done (Phases 1-4 implemented; Phase 5 documentation pending)
- **Last Updated**: 2026-06-19
- **Related Issue:** #173 (Scenario 8 drift detection tests)

## Overview

This document defines the design for **Drift Detection & Remediation** (outbound sync): how JIM detects and corrects unauthorised changes made directly in target systems.

> **Related design:** [ATTRIBUTE_PRIORITY.md](../doing/ATTRIBUTE_PRIORITY.md) (Issue #91) covers the inbound-sync concern of which source "wins" when multiple systems contribute to the same MVO attribute. The two were originally specified together because drift detection needs to know whether a system is a legitimate contributor to an attribute (has import rules) or just a recipient (only has export rules): if a system is a contributor, changes from it are not "drift", they are legitimate updates subject to attribute priority resolution. Drift detection shipped first using a coarse contributor check (`HasImportRuleForAttribute`); attribute priority refines that check into a priority-aware version (see [ATTRIBUTE_PRIORITY.md](../doing/ATTRIBUTE_PRIORITY.md), "Interaction with Drift Detection").

---

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Drift Detection](#drift-detection)
3. [Design](#design)
4. [Implementation Plan](#implementation-plan)
5. [Open Questions](#open-questions)

---

## Problem Statement

### The Drift Scenario

In a typical unidirectional sync (Source AD -> Target AD):

1. Source AD is **authoritative** for group membership
2. Target AD **receives** group membership via JIM exports
3. An administrator makes an **unauthorised change** directly in Target AD (adds/removes a member)
4. JIM should **detect** this drift and **correct** it back to the authoritative state

### Current Behaviour

When Target AD sync runs today (full or delta):

1. Import stage imports CSO values (the drifted state)
2. Sync stage processes the CSO, updates MVO if import rules exist
3. **No re-evaluation** of what Target should look like
4. Drift persists until next Source change triggers export evaluation

### Desired Behaviour

1. Import stage imports the drifted state ✓ (works today)
2. Sync stage processes the CSO ✓ (works today)
3. **NEW**: Re-evaluate export rules to compare expected vs actual state
4. **NEW**: Stage pending exports to correct any drift

> **Note**: This behaviour applies to both full sync and delta sync operations. The trigger is the inbound sync processing of a CSO, regardless of whether it came from a full import or delta import.

---

## Drift Detection

### Design Options Considered

#### Option 1: Always Re-evaluate Export Rules on Inbound Sync

When inbound sync processes a CSO from a system that has export rules targeting it, automatically re-evaluate those export rules.

**Flow:**
```
Target import -> imports drifted group membership
Target sync   -> processes CSO
              -> For each export rule targeting this object type:
                  -> Calculate expected state from MVO + sync rules
                  -> Compare expected vs actual
                  -> Stage corrective pending exports if different
```

**Pros:**
- Automatic - no user intervention needed
- Efficient - only evaluates CSOs that actually changed (delta) or all CSOs (full)
- Fits naturally into existing sync flow

**Cons:**
- Always runs - no opt-out if drift is intentional
- Could cause issues with legitimate bidirectional sync scenarios

---

#### Option 2: "Enforce State" Flag on Export Rules

Add a boolean flag to export sync rules: `EnforceState` (default: **true**).

When enabled, inbound sync from that connected system triggers re-evaluation of export rules to detect and remediate drift.

**Pros:**
- Explicit user control - can disable for specific rules
- Default ON enforces desired state (correct product vision for new product)
- Can selectively enforce some attributes but not others
- Clear user intent in configuration

**Cons:**
- Another configuration option (though reasonable complexity)
- User needs to understand when to disable it

---

#### Option 3: Authoritative Direction on Sync Rules

Mark the sync rule pair with an authoritative direction: `Source->Target` (unidirectional) or `Bidirectional`.

**Pros:**
- Clear conceptual model at the rule level
- Single configuration point per rule pair

**Cons:**
- Coarse-grained - applies to whole rule, not per-attribute
- Doesn't fit well if some attributes flow both ways

---

#### Option 4: Dedicated "Drift Detection" Run Profile Step

New run profile step type that explicitly checks for and corrects drift.

**Pros:**
- Explicit, scheduled operation
- Clear operational intent

**Cons:**
- Not automatic - drift persists until next run
- Separate operation to manage
- Doesn't leverage delta-import efficiency

---

### Decision: Option 2 - EnforceState Flag ✓

> **Status**: APPROVED

**Rationale:**

1. **Product Vision**: JIM is a new product with no backward compatibility concerns. The default should be "enforce desired state" because that's what most users expect from authoritative source synchronisation.

2. **Opt-Out Available**: For advanced scenarios where drift is intentional (e.g., emergency access), users can disable enforcement on specific rules.

3. **Efficient**: For delta sync, only processes CSOs that actually changed. For full sync, processes all CSOs in scope (comprehensive drift detection).

4. **Fits Existing Architecture**: Hooks naturally into the sync processing loop (both full and delta).

**Design Decisions:**

- **Applies to export sync rules only** - Import rules define what flows into the metaverse; the concept of "enforcing state" doesn't apply to imports. Drift detection is inherently about ensuring targets match what authoritative sources dictate.
- **Default: `EnforceState = true`** - The common case is that administrators want drift corrected automatically.
- **UI: Hidden in "Advanced" section** - This is an edge-case control for unusual scenarios (e.g., emergency access patterns). Most users should never need to see or change it. The setting should be placed in an expandable "Advanced Options" panel or similar UX pattern that is collapsed by default.

---

### Behaviour Matrix

With `EnforceState` flag:

| Trigger | EnforceState = true (default) | EnforceState = false |
|---------|------------------------------|---------------------|
| Target import + sync (drift detected) | Export rules re-evaluated -> pending exports staged | CSO values updated, no export evaluation |
| Source import + sync (Source change) | Export rules evaluated -> pending exports staged | Export rules evaluated -> pending exports staged |

> **Note**: This behaviour applies identically to both full sync and delta sync. The difference is scope: delta sync processes only changed CSOs, while full sync processes all CSOs in scope.

**Key insight**: With `EnforceState = false`, drift is still eventually corrected when Source changes that object. The flag controls whether correction is **immediate** (on Target sync) or **deferred** (on next Source sync).

---

## Design

### Summary

| Aspect | Approach | Status |
|--------|----------|--------|
| Drift detection trigger | On inbound sync, when CSO has export rules targeting it | ✓ Implemented |
| Drift detection control | `EnforceState` flag on **export** sync rules, **default: true**, hidden in Advanced Options UI | ✓ Implemented |

### Schema Changes

```csharp
public class SyncRule
{
    // ... existing properties ...

    /// <summary>
    /// When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Set to false to allow drift (e.g., for emergency access scenarios).
    /// Only applicable to export sync rules.
    /// </summary>
    public bool EnforceState { get; set; } = true;
}
```

### Sync Engine Changes (Outbound Sync)

> **Note**: This logic applies to both `SyncFullSyncTaskProcessor` and `SyncDeltaSyncTaskProcessor` via the shared `SyncTaskProcessorBase`. The `HasImportRuleForAttribute` contributor check shown is the **shipped** (non-priority-aware) version; it becomes priority-aware once attribute priority lands (see [ATTRIBUTE_PRIORITY.md](../doing/ATTRIBUTE_PRIORITY.md), "Interaction with Drift Detection").

```csharp
// In SyncTaskProcessorBase, after processing inbound CSO changes:

async Task ProcessCsoChangesAsync(ConnectedSystemObject cso, MetaverseObject mvo)
{
    // 1. Process inbound attribute flows with priority resolution
    await ProcessInboundAttributeFlowsAsync(cso, mvo);

    // 2. Evaluate drift and enforce state if applicable
    await EvaluateAndEnforceDriftAsync(cso, mvo);
}

async Task EvaluateAndEnforceDriftAsync(ConnectedSystemObject cso, MetaverseObject mvo)
{
    // Get export rules targeting this CSO's connected system and object type
    var exportRules = await GetExportRulesForCsoAsync(cso);

    foreach (var exportRule in exportRules.Where(r => r.EnforceState))
    {
        foreach (var attrFlow in exportRule.AttributeFlows)
        {
            // Check if this system has any import rules for this attribute
            // (i.e., is it a legitimate contributor?)
            if (HasImportRuleForAttribute(cso.ConnectedSystem, attrFlow.TargetAttribute, mvo.ObjectType))
            {
                // System is a contributor for this attribute - don't treat as drift
                continue;
            }

            // Calculate expected value based on MVO + export rule
            var expectedValue = CalculateExpectedValue(mvo, attrFlow);
            var actualValue = cso.GetAttributeValue(attrFlow.TargetAttribute);

            if (!ValuesEqual(expectedValue, actualValue))
            {
                // Drift detected - stage corrective pending export
                await StagePendingExportChangeAsync(cso, attrFlow.TargetAttribute, expectedValue);
            }
        }
    }
}

bool HasImportRuleForAttribute(ConnectedSystem system, string attributeName, MetaverseObjectType objectType)
{
    // Simple check: does this system have any import mapping for this attribute?
    return GetImportMappingsForAttribute(attributeName, objectType)
        .Any(m => m.SyncRule.ConnectedSystemId == system.Id);
}
```

### UI Changes: Export Sync Rule Configuration

The `EnforceState` setting should be hidden in an **Advanced Options** section that is collapsed by default. Most users will never need to modify this setting.

**UX Pattern:** Expandable panel or accordion section labelled "Advanced Options" at the bottom of the export sync rule configuration page.

```
> Advanced Options
  +-----------------------------------------------------------------+
  | [x] Enforce desired state (remediate drift)                     |
  |                                                                 |
  |   When enabled, changes made directly in the target system      |
  |   that conflict with the authoritative source will be           |
  |   automatically corrected during sync operations.               |
  |                                                                 |
  |   Disable this only for special scenarios where you             |
  |   intentionally want to allow direct changes in the target      |
  |   system (e.g., emergency access patterns).                     |
  +-----------------------------------------------------------------+
```

**Rationale for hiding:** This is an edge-case control. Exposing it prominently would confuse users and invite accidental misconfiguration. The default (`true`) is correct for the vast majority of use cases.

---

## Implementation Plan

> **Status**: Drift detection implemented (Phases 1-4). Documentation (Phase 5) is pending.

#### Phase 1: Schema and Model Changes ✅

- [x] **1.1** Add `EnforceState` property to `SyncRule` model (default: true)
  - Added to [SyncRule.cs](../../../src/JIM.Models/Logic/SyncRule.cs)
  - Added to [SyncRuleHeader.cs](../../../src/JIM.Models/Logic/DTOs/SyncRuleHeader.cs)
- [x] **1.2** Create database migration
  - Created `20260117121840_AddEnforceStateToSyncRule.cs` (since consolidated away; the `EnforceState` column survives in the current model snapshot)
- [x] **1.3** Update API DTOs for sync rule configuration
  - Updated [SyncRuleRequestDtos.cs](../../../src/JIM.Web/Models/Api/SyncRuleRequestDtos.cs)
  - Updated [SynchronisationController.cs](../../../src/JIM.Web/Controllers/Api/SynchronisationController.cs)

#### Phase 2: Drift Detection Logic ✅

- [x] **2.1** Create `DriftDetectionService` in `src/JIM.Application/Services/`
  - Created [DriftDetectionService.cs](../../../src/JIM.Application/Services/DriftDetectionService.cs)
  - `EvaluateDriftAsync(cso, mvo, exportRules, importMappingCache)`
  - `HasImportRuleForAttribute(connectedSystemId, mvoAttributeId, cache)`
  - `BuildImportMappingCache(syncRules)` static helper

- [x] **2.2** Integrate into `SyncTaskProcessorBase` (shared by full and delta sync)
  - Added `BuildDriftDetectionCache()` method
  - Added `EvaluateDriftAndEnforceStateAsync()` method
  - Integrated into `ProcessMetaverseObjectChangesAsync()`
  - Updated [SyncFullSyncTaskProcessor.cs](../../../src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs)
  - Updated [SyncDeltaSyncTaskProcessor.cs](../../../src/JIM.Worker/Processors/SyncDeltaSyncTaskProcessor.cs)

- [x] **2.3** Add performance optimisations
  - Cache import mapping lookups per sync run (`_importMappingCache`)
  - Cache export rules with EnforceState=true per sync run (`_driftDetectionExportRules`)
  - Uses existing batched pending export creation infrastructure

#### Phase 3: UI Updates ✅

- [x] **3.1** Add "Advanced Options" expandable section to export sync rule configuration page
  - Updated [SyncRuleDetail.razor](../../../src/JIM.Web/Pages/Admin/SyncRuleDetail.razor)
- [x] **3.2** Add `EnforceState` checkbox inside "Advanced Options" section with appropriate help text
  - Displayed only for Export direction rules
  - Includes tooltip and explanatory alert text

#### Phase 4: Testing ✅

- [x] **4.1** Unit tests for `DriftDetectionService`
  - Created [DriftDetectionTests.cs](../../../test/JIM.Worker.Tests/OutboundSync/DriftDetectionTests.cs)
  - 12 unit tests covering:
    - Drift detected when non-contributor system changes attribute
    - No drift flagged when contributor system changes attribute
    - EnforceState=false skips drift detection
    - BuildImportMappingCache tests
    - HasImportRuleForAttribute tests

- [ ] **4.2** Integration tests
  - Update Scenario 8 DetectDrift test to validate drift correction (pending)

#### Phase 5: Documentation (Pending)

- [ ] **5.1** Update DEVELOPER_GUIDE.md with drift detection concepts
- [ ] **5.2** Add user documentation for EnforceState setting
- [ ] **5.3** Add troubleshooting guide for drift-related issues

---

## Open Questions

1. **What happens when EnforceState = true but the export fails?**
   - Should drift persist until next successful export?
   - Should we track "drift detected but not yet corrected" state?

2. **How do we handle the transition period during initial sync?**
   - When Target objects exist before JIM manages them
   - First sync might detect massive "drift" that's actually initial state
   - Need "initial reconciliation" mode vs "ongoing enforcement" mode?

3. **Notification/alerting for drift?**
   - Should JIM alert admins when drift is detected (before correcting)?
   - Useful for security monitoring
   - Could be Activity-based or separate alerting system

---

## References

- Issue #173: Scenario 8 drift detection tests
- [ATTRIBUTE_PRIORITY.md](../doing/ATTRIBUTE_PRIORITY.md) - Inbound-sync attribute priority design (Issue #91); refines the drift contributor check to be priority-aware
- [OUTBOUND_SYNC_DESIGN.md](OUTBOUND_SYNC_DESIGN.md) - Related export evaluation design
- [SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md](SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md) - Integration test scenarios
