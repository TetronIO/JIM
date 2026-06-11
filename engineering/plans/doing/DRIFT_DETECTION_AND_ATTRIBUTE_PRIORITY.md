# Drift Detection and Attribute Priority Design Document

- **Status:** Doing (Drift detection complete; attribute priority deferred)
- **Last Updated**: 2026-06-10

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

## Attribute Priority

> **Scope**: Attribute priority is an **inbound sync** concern - it determines which source system's value wins when multiple systems contribute to the same MVO attribute. This is distinct from drift detection, which is an **outbound sync** concern.

### Current State

**Existing Infrastructure:**
- `ContributedBySystem` navigation property exists on `MetaverseObjectAttributeValue` ([MetaverseObjectAttributeValue.cs:45](../src/JIM.Models/Core/MetaverseObjectAttributeValue.cs#L45)) - tracks which connected system contributed each attribute value
- `ContributedBySystemId` scalar FK (added Feb 2026, commit `41116255`); explicit `int?` property that avoids the need to `.Include(ContributedBySystem)`. All 14 attribute creation paths in `SyncRuleMappingProcessor` now set this scalar FK via a `contributingSystemId` parameter. Recall logic in `SyncTaskProcessorBase` uses the scalar FK directly (`av.ContributedBySystemId == connectedSystemId`).

**Current Behaviour (Temporary):**
As noted in [SyncRuleMappingProcessor.cs:56](../src/JIM.Worker/Processors/SyncRuleMappingProcessor.cs#L56):
> *"NOTE: attribute priority has not been implemented yet and will come in a later effort. For now, all mappings will be applied, meaning if there are multiple mappings to a MVO attribute, the last to be processed will win."*

This "last-writer-wins" behaviour is intentionally temporary and will be replaced by proper priority resolution.

**Known Limitation (Feb 2026):** When attributes are recalled (CSO obsoleted with `RemoveContributedAttributesOnObsoletion=true`), the system does not attempt to find an alternative contributor from another connected system with inbound attribute flow for the same MVO attribute. This requires the attribute priority infrastructure (Issue #91).

**Interim mitigation (implemented):** Attribute recall is skipped entirely when the MVO type has a deletion grace period configured (`SyncTaskProcessorBase`); the MVO retains its attribute values during the grace period and the Delete export fires on expiry. Without a grace period, recalled attributes are still simply cleared. Proper next-contributor fallback remains gated on attribute priority.

### The Problem

When multiple connected systems import values for the same MVO attribute, we need a deterministic way to decide which value wins.

**Example scenario:**
- HR System imports `department` with value "Engineering"
- Corporate Directory imports `department` with value "IT Services"
- Which value should the MVO have?

Without explicit priority, the result depends on sync execution order - unpredictable and error-prone.

**Traditional ILM limitation:**
Many identity management systems use fallback logic where if the top-priority source doesn't provide a value, the system automatically falls back to the next source. This is problematic when you want to **assert null** - i.e., explicitly say "this attribute should have no value" from the authoritative source, without falling back to a secondary source.

---

### Design Options Considered

#### Option A: System-Level Priority Ranking

Each connected system has a **priority number** (1 = highest). For any MVO attribute, the highest-priority source that provides a value wins.

**Pros:** Simple mental model, system-wide
**Cons:** Too coarse - can't have different priorities per attribute on the same system. Ruled out.

---

#### Option B: Per-Attribute Numerical Priority

Each import attribute flow has a **numerical priority** for that specific MVO attribute. When multiple systems contribute to the same attribute, evaluate in priority order.

**Design intent:** Similar to traditional attribute precedence systems, but with additional control over null handling.

---

#### Option C: Attribute Ownership Model

Each MVO attribute has **one owner** (connected system). Only the owner can update it.

**Pros:** Crystal clear - no conflicts possible
**Cons:** Too inflexible - doesn't support fallback scenarios or staged configuration changes. Ruled out.

---

### Decision: Option B - Per-Attribute Numerical Priority ✓

> **Status**: DESIGN APPROVED - Implementation deferred

**Core Design:**

1. **Numerical priority per attribute contribution** - Each import sync rule mapping that targets an MVO attribute has a priority number (1 = highest priority, larger numbers = lower priority)

2. **Default behaviour (fallback chain)** - Evaluate contributing systems in priority order; use the first non-null value found

3. **Advanced option: "Null is a value"** - When enabled on a specific contribution, if that system is *connected to the MVO* but contributes null/absent, stop evaluation immediately (no fallback). This allows explicitly asserting "no value" from the authoritative source. If the system has no connector for the MVO at all (or its joined CSO is out of scope of the import rule), it is skipped and evaluation continues to the next priority regardless of this flag; see "Contribution States" in the Design section. The flag is incoherent without that distinction.

**Example Configuration:**

```
MVO Attribute: department
+------------------------------------------------------------------+
| Priority | Connected System | Null Handling                      |
+----------+------------------+------------------------------------+
|    1     | HR System        | [x] Null is a value (no fallback)  |
|    2     | Corporate Dir    | [ ] Null is a value                |
|    3     | Self-Service AD  | [ ] Null is a value                |
+----------+------------------+------------------------------------+
```

**Behaviour with above configuration:**
- If HR System provides "Engineering" -> MVO gets "Engineering" (priority 1 wins)
- If HR System provides null and "Null is a value" is checked -> MVO gets null (no fallback)
- If HR System provides null and "Null is a value" is unchecked -> check Corporate Dir (priority 2)
- If Corporate Dir provides "IT Services" -> MVO gets "IT Services"
- And so on down the chain...
- If HR System has **no connector** for this MVO, it is skipped entirely (no contribution, "Null is a value" irrelevant) and Corporate Dir is evaluated

**Motivating scenarios for "Null is a value":**

The semantic gap it fills: a ranked precedence list cannot distinguish *"the authoritative source knows this object and says it has no value"* from *"the authoritative source doesn't know this object"*. Plain fall-through treats both as "no contribution, ask the next source".

1. **Clears must propagate (stale value resurrection)**: a manager leaves and HR clears `manager`; a lower-priority directory still holds the old manager. With plain fall-through the metaverse keeps the old manager, and everything driven by it (manager-based approvals, dynamic groups) keeps operating against the wrong person. Same shape for `department`/cost centre cleared during a reorg. Traditional ILM solutions needed custom rules extensions and sentinel-value hacks to express this.
2. **Fallback for one population, exclusivity for another, in one configuration**: a secondary system must stay in the priority list because it is the only source for objects the primary doesn't manage, but for primary-managed objects, blank-in-primary must mean blank. See the worked migration example below.
3. **Data minimisation / compliance**: personal data removed at the authoritative source must propagate as a removal everywhere, not be re-sourced from a secondary copy and republished indefinitely.
4. **Disconnect/leaver semantics**: combined with attribute recall, when the authoritative CSO obsoletes with "Null is a value", the attribute is asserted null instead of being reanimated by a secondary source.

> **Guardrail (implementation):** "Null is a value" amplifies blast radius: a misbehaving priority-1 import (truncated file, empty delta) becomes a mass attribute-clearing event rather than a harmless no-op. Consider an anomaly warning when a priority-1 source contributes null for an unusually high share of objects in a run, consistent with the synchronisation-integrity principles.

**Worked example (HR system migration):**

An MVO object type has three connected systems: **AD** (target), **HR 1** (new HR system, priority 1, "Null is a value" on its mappings), **HR 2** (legacy HR system, priority 2). Both HR systems may project to the MV; most people exist in HR 2, and some have not yet been migrated to HR 1.

- **Person in HR 1**: HR 1 is connected, so it is always authoritative. `department = null` on the HR 1 CSO is asserted into the MV and flows to AD as a clear; HR 2 can never contribute for this person.
- **Person only in HR 2**: HR 1 is *not connected* for this MVO, so evaluation skips HR 1 entirely (regardless of "Null is a value") and HR 2 contributes full attribute flow.

Without the not-connected distinction, "Null is a value" on HR 1 would block HR 2 from ever contributing, making HR 2's inbound flow pointless. With it, one priority list serves both populations.

> **Migration note:** the day a person is added to HR 1, the new join flips HR 1 from not-connected to connected, and HR 1's blank fields will (correctly) clear values HR 2 had been contributing. This is the intended semantic but is operationally surprising; user documentation must call it out so migration runbooks populate HR 1 records before joining them.

**Rationale:**

1. **Granular control** - Different attributes can have different priority orders, even from the same connected system

2. **Addresses traditional ILM limitation** - The "Null is a value" option solves a common frustration with traditional identity management systems where you couldn't assert null from an authoritative source

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
| Drift detection trigger | On inbound sync, when CSO has export rules targeting it | ✓ Ready for implementation |
| Drift detection control | `EnforceState` flag on **export** sync rules, **default: true**, hidden in Advanced Options UI | ✓ Ready for implementation |
| **Attribute Priority** | | |
| Priority model | Per-attribute numerical priority on import mappings | Design approved, implementation deferred |
| Default behaviour | Fallback chain - use first non-null value in priority order | Design approved, implementation deferred |
| Null handling | "Null is a value" flag per contribution (default: false) | Design approved, implementation deferred |

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
    /// When true, if this source is connected to the MVO but contributes
    /// null/absent for this attribute, stop evaluation immediately without
    /// falling back to lower-priority sources. Has no effect when the source
    /// has no connector for the MVO. When false (default), null values are
    /// skipped and evaluation continues to the next priority level.
    /// </summary>
    public bool NullIsValue { get; set; } = false;
}
```

**Note:** The `Priority` value is scoped to the target MVO attribute. When a new import mapping is created targeting an MVO attribute that already has contributors, the system should assign the next available priority number (max existing priority + 1).

### Sync Engine Changes

#### Contribution States

A system's contribution to an MVO attribute is **tri-state**; the distinction between the first two states is what makes "Null is a value" coherent:

| State | Meaning | Evaluation |
|-------|---------|------------|
| **Not connected** | No CSO from this system is joined to the MVO, or the joined CSO is out of scope of the import rule carrying the mapping | Always skip to the next priority, regardless of `NullIsValue` |
| **Connected, no value** | A joined, in-scope CSO exists but the mapping yields nothing | If `NullIsValue`: stop and assert null. Otherwise fall through to the next priority |
| **Connected, with value** | A joined, in-scope CSO exists and the mapping yields a value | This value wins |

Scope matters, not just join existence: if a system's import rule has scoping criteria that exclude the joined CSO, the system must count as *not connected* for evaluation; otherwise an out-of-scope connector could assert nulls it has no rule entitling it to assert.

#### Attribute Priority Resolution (Inbound Sync)

```csharp
enum ContributionState
{
    NotConnected,       // no joined, in-scope CSO from this system for the MVO
    ConnectedNoValue,   // joined, in-scope CSO exists; mapping yields nothing
    ConnectedWithValue  // joined, in-scope CSO exists; mapping yields a value
}

/// <summary>
/// Resolves the winning value for an MVO attribute when multiple systems contribute.
/// Called during inbound sync processing.
/// </summary>
AttributeResolution ResolveAttributeValue(MetaverseObject mvo, MetaverseAttribute attribute)
{
    // All import mappings targeting this MVO attribute, ordered by priority
    var contributions = GetImportMappingsForAttribute(attribute, mvo.Type)
        .OrderBy(m => m.Priority);

    foreach (var contribution in contributions)
    {
        var (state, value) = EvaluateContribution(mvo, contribution);

        switch (state)
        {
            case ContributionState.NotConnected:
                // This system has no opinion on this MVO; always continue,
                // regardless of NullIsValue.
                continue;

            case ContributionState.ConnectedWithValue:
                return AttributeResolution.Value(value, contribution);

            case ContributionState.ConnectedNoValue:
                if (contribution.NullIsValue)
                    return AttributeResolution.AssertedNull(contribution); // authoritative absence
                continue; // fallback allowed
        }
    }

    return AttributeResolution.NoContributor();
}
```

> **Implementation notes:** the MVO stores only the *winning* value (`MetaverseObjectAttributeValue` with `ContributedBySystemId`); it does not retain the losing contributions. `EvaluateContribution` therefore implies reading the MVO's other joined CSOs at resolution time whenever fallback is needed. Note also that `AssertedNull` and `NoContributor` produce the same stored state (no attribute value row); see the open question on asserted-null observability.

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

#### 2. Attribute Priority Management

Attribute priority needs UI in two places:

##### 2a. Priority Management Surface

**Location and navigation model: deliberately undecided.** Both are gated on an admin IA review (see Future Phase 0 below). Do not assume the current admin IA should simply be extended with another menu item, and do not assume a single centralised "Attribute Priority" page is the right navigation model. Attribute priority straddles two lenses: it is configured per MVO attribute (a Schema lens; `/admin/schema` already manages object types and attributes) but stored on import sync rule mappings (a logic/flow lens, beside synchronisation rules and drift enforcement).

Homing directions the IA review should evaluate: extending the Schema concept, introducing a Logic/Policy concept, or flat extension of the current IA (the baseline to beat, not the default).

Navigation models the IA review should evaluate, none pre-selected:

1. **Schema hierarchy drill-down**: Schema -> object type (e.g. User, Group) -> attribute list, with multi-contributor attributes flagged inline; precedence is managed on (or from) the attribute's own page. This is the navigation model traditional ILM solutions use, and it matches how an admin thinks when investigating one attribute.
2. **Centralised priority view**: a single cross-attribute page listing every attribute with multiple contributors, filterable by object type. Optimises for the "review all precedence at a glance / audit" task. The mock below illustrates this candidate only.
3. **Both**: drill-down as the primary home plus a centralised overview of attributes needing attention that links into it.

An earlier revision of this design pre-supposed "Metaverse -> Attribute Priority"; no Metaverse nav group exists.

The centralised-view candidate provides a single view of all MVO attributes that have multiple contributors, allowing admins to manage priority across the entire system.

```
+-----------------------------------------------------------------------------+
| Attribute Priority                                                          |
+-----------------------------------------------------------------------------+
|                                                                             |
| Object Type: [Person v]                                                     |
|                                                                             |
| +-------------------------------------------------------------------------+ |
| | Attributes with Multiple Contributors                                   | |
| +-------------------------------------------------------------------------+ |
| |                                                                         | |
| | v department (3 contributors)                                           | |
| |   +-------------------------------------------------------------------+ | |
| |   | # | Pri | Connected System    | Sync Rule        | Null Handling  | | |
| |   +---+-----+---------------------+------------------+----------------+ | |
| |   | = |  1  | HR System           | HR Import        | [x] Null=Value | | |
| |   | = |  2  | Corporate Directory | CorpDir Import   | [ ] Null=Value | | |
| |   | = |  3  | Self-Service AD     | SelfServ Import  | [ ] Null=Value | | |
| |   +-------------------------------------------------------------------+ | |
| |                                                                         | |
| | > telephoneNumber (2 contributors)                                      | |
| | > manager (2 contributors)                                              | |
| | > displayName (2 contributors)                                          | |
| |                                                                         | |
| | ----------------------------------------------------------------------- | |
| | Attributes with Single Contributor (no priority needed)                 | |
| | employeeId (HR System), mail (Exchange), ...                            | |
| |                                                                         | |
| +-------------------------------------------------------------------------+ |
|                                                                             |
|                                                        [Save Changes]       |
+-----------------------------------------------------------------------------+
```

**UX Features:**
- **Drag-and-drop reordering** (= handle) - Drag rows to change priority order
- **Expandable sections** - Click attribute name to expand/collapse contributor list
- **Inline editing** - Toggle "Null is a value" checkbox directly in the table
- **Visual grouping** - Separate "multiple contributors" (needs attention) from "single contributor" (no priority needed)
- **Object type filter** - Dropdown to switch between Person, Group, etc.

##### 2b. Import Sync Rule Mapping Editor

When editing an import sync rule mapping, show priority context if the target MVO attribute has multiple contributors.

```
+-----------------------------------------------------------------------------+
| Attribute Mapping                                                           |
+-----------------------------------------------------------------------------+
|                                                                             |
| Source Attribute: [department v]                                            |
| Target Attribute: [department v]                                            |
|                                                                             |
| +-------------------------------------------------------------------------+ |
| | [!] This attribute has 3 contributors. Current priority: 2 of 3         | |
| |                                                                         | |
| |   1. HR System (HR Import rule)                                         | |
| |   2. Corporate Directory <- this mapping                                | |
| |   3. Self-Service AD (SelfServ Import rule)                             | |
| |                                                                         | |
| |   [Manage Priority ->]                                                  | |
| +-------------------------------------------------------------------------+ |
|                                                                             |
| > Advanced Options                                                          |
|   +-----------------------------------------------------------------------+ |
|   | [ ] Null is a value (no fallback)                                     | |
|   |                                                                       | |
|   |   When enabled, if this source contributes null/empty for this        | |
|   |   attribute, the MVO attribute will be set to null without            | |
|   |   checking lower-priority contributors.                               | |
|   +-----------------------------------------------------------------------+ |
|                                                                             |
|                                                    [Cancel]  [Save]         |
+-----------------------------------------------------------------------------+
```

**UX Features:**
- **Priority context panel** - Shows where this mapping sits in the priority chain (only shown if multiple contributors exist)
- **Link to central management** - "Manage Priority ->" button navigates to the Attribute Priority page, filtered to this attribute
- **Advanced options accordion** - "Null is a value" checkbox hidden by default since it's an edge case

##### 2c. Sync Rule Summary View

In the sync rule list/summary view, indicate if any mappings have priority considerations:

```
+-----------------------------------------------------------------------------+
| Sync Rules                                                                  |
+-----------------------------------------------------------------------------+
| Name                    | Direction | Object Type | Mappings | Priority     |
+-------------------------+-----------+-------------+----------+--------------+
| HR Import               | Import    | Person      | 12       | [*] 3 attrs  |
| Corporate Dir Import    | Import    | Person      | 8        | [*] 2 attrs  |
| AD Export               | Export    | Person      | 10       | -            |
+-------------------------+-----------+-------------+----------+--------------+

Legend: [*] = This rule contributes to N attributes that have multiple contributors
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

> **Status**: Drift detection implemented (Phases 1-4). Documentation (Phase 5) is pending.

### Drift Detection Implementation (Current Phase)

#### Phase 1: Schema and Model Changes ✅

- [x] **1.1** Add `EnforceState` property to `SyncRule` model (default: true)
  - Added to [SyncRule.cs](../../src/JIM.Models/Logic/SyncRule.cs)
  - Added to [SyncRuleHeader.cs](../../src/JIM.Models/Logic/DTOs/SyncRuleHeader.cs)
- [x] **1.2** Create database migration
  - Created [20260117121840_AddEnforceStateToSyncRule.cs](../../src/JIM.PostgresData/Migrations/20260117121840_AddEnforceStateToSyncRule.cs)
- [x] **1.3** Update API DTOs for sync rule configuration
  - Updated [SyncRuleRequestDtos.cs](../../src/JIM.Web/Models/Api/SyncRuleRequestDtos.cs)
  - Updated [SynchronisationController.cs](../../src/JIM.Web/Controllers/Api/SynchronisationController.cs)

#### Phase 2: Drift Detection Logic ✅

- [x] **2.1** Create `DriftDetectionService` in `src/JIM.Application/Services/`
  - Created [DriftDetectionService.cs](../../src/JIM.Application/Services/DriftDetectionService.cs)
  - `EvaluateDriftAsync(cso, mvo, exportRules, importMappingCache)`
  - `HasImportRuleForAttribute(connectedSystemId, mvoAttributeId, cache)`
  - `BuildImportMappingCache(syncRules)` static helper

- [x] **2.2** Integrate into `SyncTaskProcessorBase` (shared by full and delta sync)
  - Added `BuildDriftDetectionCache()` method
  - Added `EvaluateDriftAndEnforceStateAsync()` method
  - Integrated into `ProcessMetaverseObjectChangesAsync()`
  - Updated [SyncFullSyncTaskProcessor.cs](../../src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs)
  - Updated [SyncDeltaSyncTaskProcessor.cs](../../src/JIM.Worker/Processors/SyncDeltaSyncTaskProcessor.cs)

- [x] **2.3** Add performance optimisations
  - Cache import mapping lookups per sync run (`_importMappingCache`)
  - Cache export rules with EnforceState=true per sync run (`_driftDetectionExportRules`)
  - Uses existing batched pending export creation infrastructure

#### Phase 3: UI Updates ✅

- [x] **3.1** Add "Advanced Options" expandable section to export sync rule configuration page
  - Updated [SyncRuleDetail.razor](../../src/JIM.Web/Pages/Admin/SyncRuleDetail.razor)
- [x] **3.2** Add `EnforceState` checkbox inside "Advanced Options" section with appropriate help text
  - Displayed only for Export direction rules
  - Includes tooltip and explanatory alert text

#### Phase 4: Testing ✅

- [x] **4.1** Unit tests for `DriftDetectionService`
  - Created [DriftDetectionTests.cs](../../test/JIM.Worker.Tests/OutboundSync/DriftDetectionTests.cs)
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

### Attribute Priority Implementation (Deferred)

> **Status**: Design approved, implementation deferred to a future phase.

#### Future Phase 0: Admin IA Review (prerequisite)

- [ ] Review the admin area information architecture and decide where attribute priority lives (Schema concept, new Logic/Policy concept, or other); see UI section 2a above
- [ ] Decide the navigation model for reaching an attribute's precedence configuration (schema hierarchy drill-down, centralised view, or both)
- [ ] Decide nav drawer vs admin-index exposure; re-home related existing pages if the review concludes so

#### Future Phase 1: Schema and Model Changes

- [x] Add `ContributedBySystemId` scalar FK to `MetaverseObjectAttributeValue` (prerequisite; Feb 2026, commit `41116255`)
- [x] Thread `contributingSystemId` through all 14 attribute creation paths in `SyncRuleMappingProcessor`
- [ ] Add `Priority` property to `SyncRuleMapping` model (default: int.MaxValue)
- [ ] Add `NullIsValue` property to `SyncRuleMapping` model (default: false)
- [ ] Create database migration
- [ ] Update API DTOs
- [ ] Add API endpoint to get/set attribute priority order

#### Future Phase 2: Attribute Priority Logic

- [ ] Create `AttributePriorityService` in `src/JIM.Application/Services/`
- [ ] Implement the tri-state contribution evaluation (not connected / connected-no-value / connected-with-value), respecting sync rule scope
- [ ] Integrate into inbound sync processing (`SyncRuleMappingProcessor`)
- [ ] Auto-assign priority on new import mapping creation
- [ ] Replace the interim grace period recall freeze with proper next-contributor fallback
- [ ] Anomaly guardrail: warn when a priority-1 `NullIsValue` source contributes null for an unusually high share of objects in a run

#### Future Phase 3: UI Updates

- [ ] Build the priority management UI per the Future Phase 0 IA review outcome (per-attribute schema pages, centralised view, or both)
- [ ] Add priority context panel to import sync rule mapping editor
- [ ] Add "Advanced Options" section to import mapping editor with "Null is a value" checkbox
- [ ] Add priority indicator column to sync rule list view

#### Future Phase 4: Testing

- [ ] Unit tests for `AttributePriorityService`
- [ ] Integration tests for multi-source priority resolution
- [ ] Integration tests for NullIsValue behaviour, including the tri-state cases:
  - [ ] Priority-1 system not connected: lower-priority system contributes fully despite NullIsValue on priority 1 (HR migration scenario)
  - [ ] Priority-1 system connected with null and NullIsValue: null asserted, no fallback
  - [ ] Priority-1 system joined but out of sync rule scope: treated as not connected
  - [ ] New join to priority-1 system mid-life: its blanks clear previously contributed values

#### Future Phase 5: Documentation

- [ ] Add user documentation for Attribute Priority page
- [ ] Add user documentation for NullIsValue setting

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

7. **Multivalued attributes**
   - The design above is single-value semantics ("first non-null value wins")
   - MVAs store one `MetaverseObjectAttributeValue` row per value with a per-row `ContributedBySystemId`, which naturally supports multi-contributor union
   - Is priority for MVAs winner-takes-all-values, merge/union per contributor, or out of scope for the first iteration? And does "Null is a value" on an MVA assert the empty set?

8. **Interaction with drift detection**
   - `DriftDetectionService.HasImportRuleForAttribute` treats any system with an import mapping as a legitimate contributor whose inbound changes are not drift
   - Once priority exists, a low-priority contributor's inbound change can lose resolution; should the losing value sitting in that target system then be treated as drift and corrected back?

9. **Admin IA and navigation**
   - See Future Phase 0: where does attribute priority (and the configuration concepts around it) belong in the admin information architecture?
   - Is the navigation model schema hierarchy drill-down, a centralised view, or both?
   - Do not extend the current IA, or default to a single centralised page, without the review

10. **Asserted null observability**
    - An asserted null and "no contributor" both materialise identically (no `MetaverseObjectAttributeValue` row)
    - Does the UI need to explain "this attribute is blank because HR 1 asserts it" for admin troubleshooting, and if so does that require persisting which system asserted the null?

11. **Conditional mappings and null**
    - When a mapping's source is an expression, does an expression that evaluates to null count as "connected, no value" (and therefore trigger NullIsValue)?
    - Presumably yes, which also provides a mechanism for *conditionally* asserting null; confirm and document

---

## References

- Issue #91: MV attribute priority
- Issue #173: Scenario 8 drift detection tests
- [OUTBOUND_SYNC_DESIGN.md](../done/OUTBOUND_SYNC_DESIGN.md) - Related export evaluation design
- [SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md](../done/SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md) - Integration test scenarios
