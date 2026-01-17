# Drift Detection and Attribute Priority Design Document

> **Status**: Approved - Ready for Implementation
> **Last Updated**: 2026-01-17

## Overview

This document defines designs for two related but distinct challenges:

1. **Drift Detection & Remediation** (Outbound Sync): How JIM detects and corrects unauthorised changes made directly in target systems
2. **Attribute Priority** (Inbound Sync): How JIM determines which source "wins" when multiple systems contribute to the same MVO attribute

**Relationship:** Drift detection needs to know whether a system is a legitimate contributor to an attribute (has import rules) or just a recipient (only has export rules). If a system is a contributor, changes from that system are not "drift" - they're legitimate updates subject to attribute priority resolution.

---

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Drift Detection](#drift-detection)
3. [Attribute Priority](#attribute-priority)
4. [Design](#design)
5. [Implementation Plan](#implementation-plan)
6. [Open Questions](#open-questions)

---

## Problem Statement

### The Drift Scenario

In a typical unidirectional sync (Source AD → Target AD):

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
Target import → imports drifted group membership
Target sync   → processes CSO
              → For each export rule targeting this object type:
                  → Calculate expected state from MVO + sync rules
                  → Compare expected vs actual
                  → Stage corrective pending exports if different
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

Mark the sync rule pair with an authoritative direction: `Source→Target` (unidirectional) or `Bidirectional`.

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
| Target import + sync (drift detected) | Export rules re-evaluated → pending exports staged | CSO values updated, no export evaluation |
| Source import + sync (Source change) | Export rules evaluated → pending exports staged | Export rules evaluated → pending exports staged |

> **Note**: This behaviour applies identically to both full sync and delta sync. The difference is scope: delta sync processes only changed CSOs, while full sync processes all CSOs in scope.

**Key insight**: With `EnforceState = false`, drift is still eventually corrected when Source changes that object. The flag controls whether correction is **immediate** (on Target sync) or **deferred** (on next Source sync).

---

## Attribute Priority

> **Scope**: Attribute priority is an **inbound sync** concern - it determines which source system's value wins when multiple systems contribute to the same MVO attribute. This is distinct from drift detection, which is an **outbound sync** concern.

### Current State

**Existing Infrastructure:**
- `ContributedBySystem` property already exists on `MetaverseObjectAttributeValue` ([MetaverseObjectAttributeValue.cs:45](../JIM.Models/Core/MetaverseObjectAttributeValue.cs#L45)) - tracks which connected system contributed each attribute value

**Current Behaviour (Temporary):**
As noted in [SyncRuleMappingProcessor.cs:56](../JIM.Worker/Processors/SyncRuleMappingProcessor.cs#L56):
> *"NOTE: attribute priority has not been implemented yet and will come in a later effort. For now, all mappings will be applied, meaning if there are multiple mappings to a MVO attribute, the last to be processed will win."*

This "last-writer-wins" behaviour is intentionally temporary and will be replaced by proper priority resolution.

### The Problem

When multiple connected systems import values for the same MVO attribute, we need a deterministic way to decide which value wins.

**Example scenario:**
- HR System imports `department` with value "Engineering"
- Corporate Directory imports `department` with value "IT Services"
- Which value should the MVO have?

Without explicit priority, the result depends on sync execution order - unpredictable and error-prone.

**MIM 2016 limitation:**
In MIM, attribute precedence uses fallback logic where if the top-priority source doesn't provide a value, it falls back to the next source. This is problematic when you want to **assert null** - i.e., explicitly say "this attribute should have no value" from the authoritative source, without falling back.

---

### Design Options Considered

#### Option A: System-Level Priority Ranking

Each connected system has a **priority number** (1 = highest). For any MVO attribute, the highest-priority source that provides a value wins.

**Pros:** Simple mental model, system-wide
**Cons:** Too coarse - can't have different priorities per attribute on the same system. Ruled out.

---

#### Option B: Per-Attribute Numerical Priority

Each import attribute flow has a **numerical priority** for that specific MVO attribute. When multiple systems contribute to the same attribute, evaluate in priority order.

**Design intent:** Similar to MIM 2016's attribute precedence, but with additional control over null handling.

---

#### Option C: Attribute Ownership Model

Each MVO attribute has **one owner** (connected system). Only the owner can update it.

**Pros:** Crystal clear - no conflicts possible
**Cons:** Too inflexible - doesn't support fallback scenarios or staged configuration changes. Ruled out.

---

### Decision: Option B - Per-Attribute Numerical Priority ✓

> **Status**: APPROVED

**Core Design:**

1. **Numerical priority per attribute contribution** - Each import sync rule mapping that targets an MVO attribute has a priority number (1 = highest priority, larger numbers = lower priority)

2. **Default behaviour (fallback chain)** - Evaluate contributing systems in priority order; use the first non-null value found

3. **Advanced option: "Null is a value"** - When enabled on a specific contribution, if that system contributes null/absent, stop evaluation immediately (no fallback). This allows explicitly asserting "no value" from the authoritative source.

**Example Configuration:**

```
MVO Attribute: department
┌──────────────────────────────────────────────────────────────────┐
│ Priority │ Connected System │ Null Handling                     │
├──────────┼──────────────────┼───────────────────────────────────┤
│    1     │ HR System        │ ☑ Null is a value (no fallback)   │
│    2     │ Corporate Dir    │ ☐ Null is a value                 │
│    3     │ Self-Service AD  │ ☐ Null is a value                 │
└──────────────────────────────────────────────────────────────────┘
```

**Behaviour with above configuration:**
- If HR System provides "Engineering" → MVO gets "Engineering" (priority 1 wins)
- If HR System provides null and "Null is a value" is checked → MVO gets null (no fallback)
- If HR System provides null and "Null is a value" is unchecked → check Corporate Dir (priority 2)
- If Corporate Dir provides "IT Services" → MVO gets "IT Services"
- And so on down the chain...

**Rationale:**

1. **Granular control** - Different attributes can have different priority orders, even from the same connected system

2. **Addresses MIM limitation** - The "Null is a value" option solves the frustrating MIM behaviour where you couldn't assert null from an authoritative source

3. **Operational flexibility** - Admins can reorder priorities at any time without removing/recreating sync rules. This is valuable for staged configuration changes ahead of business change windows.

4. **Explicit over implicit** - Priority is explicitly configured, not inferred from rule order or other implicit factors

**Design Decisions:**

- **Default priority**: When a new import mapping is created, assign the next available priority number (lowest priority)
- **Default null handling**: "Null is a value" = false (fallback behaviour, matching traditional ILM expectations)
- **UI placement**: Priority management should be accessible from both the sync rule page and a dedicated "Attribute Priority" view (see UI section below)

---

## Design

### Summary

| Aspect | Approach | Status |
|--------|----------|--------|
| **Drift Detection** | | |
| Drift detection trigger | On inbound sync, when CSO has export rules targeting it | ✓ Approved |
| Drift detection control | `EnforceState` flag on **export** sync rules, **default: true**, hidden in Advanced Options UI | ✓ Approved |
| **Attribute Priority** | | |
| Priority model | Per-attribute numerical priority on import mappings | ✓ Approved |
| Default behaviour | Fallback chain - use first non-null value in priority order | ✓ Approved |
| Null handling | "Null is a value" flag per contribution (default: false) | ✓ Approved |

### Schema Changes

#### Drift Detection (Export Sync Rules)

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

#### Attribute Priority (Import Sync Rule Mappings)

```csharp
public class SyncRuleMapping
{
    // ... existing properties ...

    /// <summary>
    /// Priority for this attribute contribution when multiple systems import
    /// to the same MVO attribute. Lower numbers = higher priority (1 is highest).
    /// Only applicable to import sync rules.
    /// </summary>
    public int Priority { get; set; } = int.MaxValue; // Default: lowest priority

    /// <summary>
    /// When true, if this source contributes null/absent for this attribute,
    /// stop evaluation immediately without falling back to lower-priority sources.
    /// When false (default), null values are skipped and evaluation continues
    /// to the next priority level.
    /// </summary>
    public bool NullIsValue { get; set; } = false;
}
```

**Note:** The `Priority` value is scoped to the target MVO attribute. When a new import mapping is created targeting an MVO attribute that already has contributors, the system should assign the next available priority number (max existing priority + 1).

### Sync Engine Changes

#### Attribute Priority Resolution (Inbound Sync)

```csharp
/// <summary>
/// Resolves the winning value for an MVO attribute when multiple systems contribute.
/// Called during inbound sync processing.
/// </summary>
object? ResolveAttributeValue(string mvoAttributeName, MetaverseObjectType objectType)
{
    // Get all import mappings targeting this MVO attribute, ordered by priority
    var contributions = GetImportMappingsForAttribute(mvoAttributeName, objectType)
        .OrderBy(m => m.Priority)
        .ToList();

    foreach (var contribution in contributions)
    {
        var value = GetContributedValue(contribution);

        if (value != null)
        {
            // Non-null value found - use it
            return value;
        }

        if (contribution.NullIsValue)
        {
            // Null contributed and "Null is a value" is enabled - stop here
            return null;
        }

        // Null contributed but fallback is allowed - continue to next priority
    }

    // No value found from any contributor
    return null;
}
```

#### Drift Detection (Outbound Sync)

> **Note**: This logic applies to both `SyncFullSyncTaskProcessor` and `SyncDeltaSyncTaskProcessor` via the shared `SyncTaskProcessorBase`.

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

### UI Changes

#### 1. Export Sync Rule Configuration (Drift Detection)

The `EnforceState` setting should be hidden in an **Advanced Options** section that is collapsed by default. Most users will never need to modify this setting.

**UX Pattern:** Expandable panel or accordion section labelled "Advanced Options" at the bottom of the export sync rule configuration page.

```
▶ Advanced Options
  ┌─────────────────────────────────────────────────────────────────┐
  │ ☑ Enforce desired state (remediate drift)                       │
  │                                                                 │
  │   When enabled, changes made directly in the target system      │
  │   that conflict with the authoritative source will be           │
  │   automatically corrected during sync operations.               │
  │                                                                 │
  │   Disable this only for special scenarios where you             │
  │   intentionally want to allow direct changes in the target      │
  │   system (e.g., emergency access patterns).                     │
  └─────────────────────────────────────────────────────────────────┘
```

**Rationale for hiding:** This is an edge-case control. Exposing it prominently would confuse users and invite accidental misconfiguration. The default (`true`) is correct for the vast majority of use cases.

---

#### 2. Attribute Priority Management

Attribute priority needs UI in two places:

##### 2a. Dedicated Attribute Priority Page

**Location:** Metaverse → Attribute Priority (new navigation item)

This page provides a centralised view of all MVO attributes that have multiple contributors, allowing admins to manage priority across the entire system.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Attribute Priority                                                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│ Object Type: [Person ▼]                                                     │
│                                                                             │
│ ┌─────────────────────────────────────────────────────────────────────────┐ │
│ │ Attributes with Multiple Contributors                                   │ │
│ ├─────────────────────────────────────────────────────────────────────────┤ │
│ │                                                                         │ │
│ │ ▼ department (3 contributors)                                          │ │
│ │   ┌───────────────────────────────────────────────────────────────────┐ │ │
│ │   │ ↕ │ Pri │ Connected System    │ Sync Rule        │ Null Handling  │ │ │
│ │   ├───┼─────┼─────────────────────┼──────────────────┼────────────────┤ │ │
│ │   │ ☰ │  1  │ HR System           │ HR Import        │ ☑ Null=Value   │ │ │
│ │   │ ☰ │  2  │ Corporate Directory │ CorpDir Import   │ ☐ Null=Value   │ │ │
│ │   │ ☰ │  3  │ Self-Service AD     │ SelfServ Import  │ ☐ Null=Value   │ │ │
│ │   └───────────────────────────────────────────────────────────────────┘ │ │
│ │                                                                         │ │
│ │ ▶ telephoneNumber (2 contributors)                                      │ │
│ │ ▶ manager (2 contributors)                                              │ │
│ │ ▶ displayName (2 contributors)                                          │ │
│ │                                                                         │ │
│ │ ─────────────────────────────────────────────────────────────────────── │ │
│ │ Attributes with Single Contributor (no priority needed)                 │ │
│ │ employeeId (HR System), mail (Exchange), ...                            │ │
│ │                                                                         │ │
│ └─────────────────────────────────────────────────────────────────────────┘ │
│                                                                             │
│                                                        [Save Changes]       │
└─────────────────────────────────────────────────────────────────────────────┘
```

**UX Features:**
- **Drag-and-drop reordering** (☰ handle) - Drag rows to change priority order
- **Expandable sections** - Click attribute name to expand/collapse contributor list
- **Inline editing** - Toggle "Null is a value" checkbox directly in the table
- **Visual grouping** - Separate "multiple contributors" (needs attention) from "single contributor" (no priority needed)
- **Object type filter** - Dropdown to switch between Person, Group, etc.

##### 2b. Import Sync Rule Mapping Editor

When editing an import sync rule mapping, show priority context if the target MVO attribute has multiple contributors.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Attribute Mapping                                                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│ Source Attribute: [department ▼]                                            │
│ Target Attribute: [department ▼]                                            │
│                                                                             │
│ ┌─────────────────────────────────────────────────────────────────────────┐ │
│ │ ⚠ This attribute has 3 contributors. Current priority: 2 of 3          │ │
│ │                                                                         │ │
│ │   1. HR System (HR Import rule)                                         │ │
│ │   2. Corporate Directory ← this mapping                                 │ │
│ │   3. Self-Service AD (SelfServ Import rule)                             │ │
│ │                                                                         │ │
│ │   [Manage Priority →]                                                   │ │
│ └─────────────────────────────────────────────────────────────────────────┘ │
│                                                                             │
│ ▶ Advanced Options                                                          │
│   ┌─────────────────────────────────────────────────────────────────────┐   │
│   │ ☐ Null is a value (no fallback)                                     │   │
│   │                                                                     │   │
│   │   When enabled, if this source contributes null/empty for this      │   │
│   │   attribute, the MVO attribute will be set to null without          │   │
│   │   checking lower-priority contributors.                             │   │
│   └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│                                                    [Cancel]  [Save]         │
└─────────────────────────────────────────────────────────────────────────────┘
```

**UX Features:**
- **Priority context panel** - Shows where this mapping sits in the priority chain (only shown if multiple contributors exist)
- **Link to central management** - "Manage Priority →" button navigates to the Attribute Priority page, filtered to this attribute
- **Advanced options accordion** - "Null is a value" checkbox hidden by default since it's an edge case

##### 2c. Sync Rule Summary View

In the sync rule list/summary view, indicate if any mappings have priority considerations:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Sync Rules                                                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│ Name                    │ Direction │ Object Type │ Mappings │ Priority     │
├─────────────────────────┼───────────┼─────────────┼──────────┼──────────────┤
│ HR Import               │ Import    │ Person      │ 12       │ 🔵 3 attrs   │
│ Corporate Dir Import    │ Import    │ Person      │ 8        │ 🔵 2 attrs   │
│ AD Export               │ Export    │ Person      │ 10       │ —            │
└─────────────────────────────────────────────────────────────────────────────┘

Legend: 🔵 = This rule contributes to N attributes that have multiple contributors
```

---

#### 3. UI Implementation Notes

**MudBlazor Components to Use:**
- `MudExpansionPanel` - For Advanced Options sections and attribute groups
- `MudTable` with `@ref` for drag-and-drop - For priority reordering (or MudDropZone)
- `MudCheckBox` - For Null is a value toggle
- `MudAlert` - For the priority context info panel
- `MudSelect` - For object type filter

**State Management:**
- Priority changes should be tracked as pending until "Save Changes" is clicked
- Visual indicator (e.g., asterisk or colour change) for modified but unsaved rows
- Warn before navigating away with unsaved changes

**Validation:**
- Prevent duplicate priority numbers within the same attribute
- Auto-renumber priorities when drag-drop reorders items

---

## Implementation Plan

> **Status**: Both drift detection and attribute priority designs are approved and ready for implementation.

### Phase 1: Schema and Model Changes

- [ ] **1.1** Add `EnforceState` property to `SyncRule` model (default: true)
- [ ] **1.2** Add `Priority` property to `SyncRuleMapping` model (default: int.MaxValue)
- [ ] **1.3** Add `NullIsValue` property to `SyncRuleMapping` model (default: false)
- [ ] **1.4** Create database migration
- [ ] **1.5** Update API DTOs for sync rule configuration
- [ ] **1.6** Add API endpoint to get/set attribute priority order

### Phase 2: Attribute Priority Logic

- [ ] **2.1** Create `AttributePriorityService` in `JIM.Application/Services/`
  - `ResolveAttributeValueAsync(mvoAttributeName, objectType, contributions)`
  - `GetContributorsForAttributeAsync(mvoAttributeName, objectType)`
  - `UpdatePriorityOrderAsync(mvoAttributeName, objectType, orderedContributions)`

- [ ] **2.2** Integrate into inbound sync processing (`SyncRuleMappingProcessor`)
  - Replace "last-writer-wins" with priority-based resolution
  - Respect `NullIsValue` flag when evaluating contributions

- [ ] **2.3** Auto-assign priority on new import mapping creation
  - Query existing contributors for target attribute
  - Assign next available priority number (max + 1)

### Phase 3: Drift Detection Logic

- [ ] **3.1** Create `DriftDetectionService` in `JIM.Application/Services/`
  - `EvaluateDriftAsync(cso, mvo, exportRules)`
  - `HasImportRuleForAttribute(system, attribute, objectType)`

- [ ] **3.2** Integrate into `SyncTaskProcessorBase` (shared by full and delta sync)
  - After processing inbound attribute flows, call drift detection
  - Stage pending exports for any detected drift

- [ ] **3.3** Add performance optimisations
  - Cache export rule and import mapping lookups per sync run
  - Batch pending export creation

### Phase 4: UI Updates

#### Drift Detection UI
- [ ] **4.1** Add "Advanced Options" expandable section to export sync rule configuration page
- [ ] **4.2** Add `EnforceState` checkbox inside "Advanced Options" section

#### Attribute Priority UI
- [ ] **4.3** Create Attribute Priority page (Metaverse → Attribute Priority)
  - Object type dropdown filter
  - Expandable attribute groups with contributor tables
  - Drag-and-drop priority reordering
  - Inline "Null is a value" checkbox
- [ ] **4.4** Add priority context panel to import sync rule mapping editor
  - Shows current priority position when multiple contributors exist
  - "Manage Priority →" link to central page
- [ ] **4.5** Add "Advanced Options" section to import mapping editor with "Null is a value" checkbox
- [ ] **4.6** Add priority indicator column to sync rule list view

### Phase 5: Testing

- [ ] **5.1** Unit tests for `AttributePriorityService`
  - Single contributor returns that value
  - Multiple contributors respect priority order
  - NullIsValue=true stops fallback
  - NullIsValue=false continues to next priority

- [ ] **5.2** Unit tests for `DriftDetectionService`
  - Drift detected when non-contributor system changes attribute
  - No drift flagged when contributor system changes attribute
  - EnforceState=false skips drift detection

- [ ] **5.3** Integration tests
  - Update Scenario 8 DetectDrift test
  - Add multi-source priority resolution tests
  - Add NullIsValue behaviour tests

### Phase 6: Documentation

- [ ] **6.1** Update DEVELOPER_GUIDE.md with attribute priority and drift detection concepts
- [ ] **6.2** Add user documentation for Attribute Priority page
- [ ] **6.3** Add user documentation for EnforceState and NullIsValue settings
- [ ] **6.4** Add troubleshooting guide for priority and drift-related issues

---

## Open Questions

### Drift Detection

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

### Attribute Priority

4. **Priority assignment for bulk import rule creation**
   - When creating a sync rule with many attribute mappings, how should priorities be assigned?
   - Option: All mappings from same rule get same base priority, then order by attribute name?
   - Option: Interactive priority assignment during rule creation wizard?

5. **Priority conflict warnings**
   - Should we warn when a new mapping creates a multi-contributor situation?
   - Or just show the priority context panel and let admins manage as needed?

6. **Cross-object-type attribute priority**
   - Currently scoped per object type (Person, Group, etc.)
   - Is there ever a need for global priority configuration?
   - Probably not - keep scoped to object type for simplicity

---

## References

- Issue #91: MV attribute priority
- Issue #173: Scenario 8 drift detection tests
- [OUTBOUND_SYNC_DESIGN.md](OUTBOUND_SYNC_DESIGN.md) - Related export evaluation design
- [SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md](SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md) - Integration test scenarios
