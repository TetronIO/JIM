# Drift Detection and Attribute Priority Design Document

> **Status**: Design
> **Last Updated**: 2026-01-17

## Overview

This document explores two related design challenges:

1. **Drift Detection & Remediation**: How JIM should detect and correct unauthorised changes made directly in target systems
2. **Attribute Priority**: How JIM determines which source "wins" when multiple systems can contribute to the same attribute

These concepts are intertwined: drift detection requires knowing which system is authoritative for each attribute, otherwise we cannot distinguish between "drift to correct" and "legitimate update to accept".

---

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Drift Detection](#drift-detection)
3. [Attribute Priority](#attribute-priority)
4. [Recommended Design](#recommended-design)
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

When Target AD delta-sync runs today:

1. Delta-import imports the changed CSO values (the drifted state)
2. Delta-sync processes the CSO, updates MVO if import rules exist
3. **No re-evaluation** of what Target should look like
4. Drift persists until next Source change triggers export evaluation

### Desired Behaviour

1. Delta-import imports the drifted state ✓ (works today)
2. Delta-sync processes the CSO ✓ (works today)
3. **NEW**: Re-evaluate export rules to compare expected vs actual state
4. **NEW**: Stage pending exports to correct any drift

---

## Drift Detection

### Design Options Considered

#### Option 1: Always Re-evaluate Export Rules on Inbound Sync

When delta-sync processes a CSO from a system that has export rules targeting it, automatically re-evaluate those export rules.

**Flow:**
```
Target delta-import → imports drifted group membership
Target delta-sync   → processes CSO
                    → For each export rule targeting this object type:
                        → Calculate expected state from MVO + sync rules
                        → Compare expected vs actual
                        → Stage corrective pending exports if different
```

**Pros:**
- Automatic - no user intervention needed
- Efficient - only evaluates CSOs that actually changed
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

### Decision: Option 2 - EnforceState Flag

**Rationale:**

1. **Product Vision**: JIM is a new product with no backward compatibility concerns. The default should be "enforce desired state" because that's what most users expect from authoritative source synchronisation.

2. **Opt-Out Available**: For advanced scenarios where drift is intentional (e.g., emergency access), users can disable enforcement on specific rules.

3. **Efficient**: Only processes CSOs that actually changed during delta-import.

4. **Fits Existing Architecture**: Hooks naturally into the delta-sync processing loop.

---

### Behaviour Matrix

With `EnforceState` flag:

| Trigger | EnforceState = true (default) | EnforceState = false |
|---------|------------------------------|---------------------|
| Target delta-import + sync (drift detected) | Export rules re-evaluated → pending exports staged | CSO values updated, no export evaluation |
| Source delta-import + sync (Source change) | Export rules evaluated → pending exports staged | Export rules evaluated → pending exports staged |

**Key insight**: With `EnforceState = false`, drift is still eventually corrected when Source changes that object. The flag controls whether correction is **immediate** (on Target sync) or **deferred** (on next Source sync).

---

## Attribute Priority

### The Problem

Drift detection requires knowing **who is authoritative** for each attribute. Consider:

**Simple case (unidirectional):**
- Source imports `department` to MVO (authoritative)
- Target exports `department` from MVO (recipient)
- Target changes `department` → **drift**, correct it ✓

**Complex case (bidirectional attributes):**
- Source imports `department` to MVO (authoritative for department)
- Target imports `telephoneNumber` to MVO (authoritative for telephoneNumber - user self-service)
- Target changes `department` → **drift**, correct it ✓
- Target changes `telephoneNumber` → **legitimate update**, accept it ✓

Without attribute priority, we cannot distinguish these cases.

---

### Design Options Considered

#### Option A: Source Priority Ranking

Each connected system has a **priority number** (1 = highest). For any MVO attribute, the highest-priority source that provides a value wins.

```
Source AD:    Priority 1 (authoritative)
Target AD:    Priority 2
HR System:    Priority 1 (same as Source for different attributes)
```

**Enforcement logic:**
- On Target inbound sync, for each attribute:
  - Is there a higher-priority source for this attribute?
  - Yes → enforce that source's value (correct drift)
  - No → accept Target's value (legitimate update)

**Pros:** Simple mental model, system-wide
**Cons:** Coarse - can't have different priorities per attribute on same system

---

#### Option B: Attribute-Level Authority

Each **attribute flow** (import rule) declares its authority level.

```yaml
SyncRule: "HR to MVO"
  AttributeFlows:
    - employeeId:      Authority: Authoritative  # HR always wins
    - department:      Authority: Authoritative
    - telephoneNumber: Authority: Fallback       # Only if no other source

SyncRule: "Target AD to MVO"
  AttributeFlows:
    - telephoneNumber: Authority: Authoritative  # User self-service wins
    - department:      Authority: None           # Never import, receive only
```

**Authority levels:**
- `Authoritative` - This source wins for this attribute
- `Fallback` - Use if no authoritative source provides a value
- `None` - Don't import (export-only attribute for this system)

**Pros:** Granular control, explicit design
**Cons:** More configuration, potential conflicts if two sources both claim Authoritative

---

#### Option C: Attribute Ownership Model

Each MVO attribute has **one owner** (connected system). Only the owner can update it; others receive it.

```
MVO Attribute        Owner
─────────────────────────────
employeeId           HR System
department           HR System
telephoneNumber      Target AD (self-service)
manager              Source AD
```

**Pros:** Crystal clear - no conflicts possible
**Cons:** Inflexible - what if ownership needs to change based on conditions?

---

#### Option D: Implicit Priority from Rule Configuration

Implicit priority based on sync rule configuration:
- If a system has an **import rule** for an attribute → it's a contributor
- If a system has an **export rule** for an attribute → it's a recipient
- Contributors outrank recipients for that attribute

**Enforcement logic:**
- Target changes `department`
- Does Target have an import rule for `department`? No → drift, correct it
- Target changes `telephoneNumber`
- Does Target have an import rule for `telephoneNumber`? Yes → legitimate, accept it

**Pros:** No new configuration - inferred from existing rules
**Cons:** Implicit behaviour might surprise users; no handling for multiple contributors

---

### Decision: Option D (Implicit) + Option B (Explicit Override)

**Default behaviour (zero-config):**
- If a system has import rules for an attribute, it's a valid contributor
- If it only has export rules, it's a recipient → enforce desired state

**Advanced override (when needed):**
- On import rules, optional `Authority` setting to handle conflicts when multiple systems import the same attribute
- Values: `Authoritative`, `Fallback`, or explicit priority number

This provides:
1. **Zero-config sensible default** - works for common unidirectional scenarios
2. **Explicit control when needed** - for complex multi-source scenarios

---

## Recommended Design

### Summary

| Aspect | Decision |
|--------|----------|
| Drift detection trigger | On inbound delta-sync, when CSO has export rules targeting it |
| Drift detection control | `EnforceState` flag on export rules, **default: true** |
| Attribute authority (default) | Implicit from rule configuration: import rule = contributor, export-only = recipient |
| Attribute authority (advanced) | Explicit `Authority` property on import attribute flows |

### Schema Changes

```csharp
public class SyncRule
{
    // ... existing properties ...

    /// <summary>
    /// When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Set to false to allow drift (e.g., for emergency access scenarios).
    /// </summary>
    public bool EnforceState { get; set; } = true;
}

public class SyncRuleAttributeMapping
{
    // ... existing properties ...

    /// <summary>
    /// Authority level for this attribute flow. Only applicable for import rules.
    /// When multiple systems import the same attribute, the highest authority wins.
    /// Default: Authoritative (for import rules)
    /// </summary>
    public AttributeAuthority? Authority { get; set; }
}

public enum AttributeAuthority
{
    /// <summary>
    /// This source is authoritative for this attribute. If multiple sources
    /// claim Authoritative, a priority number determines the winner.
    /// </summary>
    Authoritative = 0,

    /// <summary>
    /// Use this source's value only if no Authoritative source provides a value.
    /// </summary>
    Fallback = 1,

    /// <summary>
    /// Never import this attribute from this source (export-only).
    /// Useful when a bidirectional rule should only export certain attributes.
    /// </summary>
    None = 2
}
```

### Sync Engine Changes

#### Delta Sync Processing (Simplified)

```csharp
// In DeltaSyncTaskProcessor, after processing inbound CSO changes:

async Task ProcessCsoChangesAsync(ConnectedSystemObject cso, MetaverseObject mvo)
{
    // 1. Process inbound attribute flows (existing logic)
    await ProcessInboundAttributeFlowsAsync(cso, mvo);

    // 2. NEW: Evaluate drift and enforce state if applicable
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
            // Check if this system is authoritative for this attribute
            if (IsSystemAuthoritativeForAttribute(cso.ConnectedSystem, attrFlow.TargetAttribute, mvo))
            {
                // System contributed this value legitimately - don't enforce
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

bool IsSystemAuthoritativeForAttribute(ConnectedSystem system, string attributeName, MetaverseObject mvo)
{
    // Check if this system has an import rule for this attribute
    var importRules = GetImportRulesForSystem(system, mvo.ObjectType);

    foreach (var rule in importRules)
    {
        var attrFlow = rule.AttributeFlows.FirstOrDefault(f => f.TargetAttribute == attributeName);
        if (attrFlow != null)
        {
            // System has an import rule for this attribute
            var authority = attrFlow.Authority ?? AttributeAuthority.Authoritative;
            return authority != AttributeAuthority.None;
        }
    }

    // No import rule = not a contributor = not authoritative
    return false;
}
```

### UI Changes

#### Export Sync Rule Configuration

Add checkbox to export rule configuration page:

```
☑ Enforce desired state (remediate drift)

When enabled, changes made directly in the target system that conflict
with the authoritative source will be automatically corrected.
```

#### Import Attribute Flow Configuration (Advanced)

For each attribute mapping in an import rule, add optional authority dropdown:

```
Attribute: telephoneNumber
Source: telephoneNumber
Target: telephoneNumber
Authority: [Authoritative ▼]  (optional, defaults to Authoritative)
           ├─ Authoritative - This source wins for this attribute
           ├─ Fallback - Use only if no other source provides a value
           └─ None - Do not import (export-only)
```

---

## Implementation Plan

### Phase 1: Schema and Model Changes

- [ ] **1.1** Add `EnforceState` property to `SyncRule` model (default: true)
- [ ] **1.2** Add `AttributeAuthority` enum
- [ ] **1.3** Add `Authority` property to `SyncRuleAttributeMapping` model
- [ ] **1.4** Create database migration
- [ ] **1.5** Update API DTOs for sync rule configuration

### Phase 2: Drift Detection Logic

- [ ] **2.1** Create `DriftDetectionService` in `JIM.Application/Services/`
  - `EvaluateDriftAsync(cso, mvo, exportRules)`
  - `IsSystemAuthoritativeForAttribute(system, attribute, mvo)`
  - `CalculateExpectedValue(mvo, attributeFlow)`

- [ ] **2.2** Integrate into `SyncDeltaSyncTaskProcessor`
  - After processing inbound attribute flows, call drift detection
  - Stage pending exports for any detected drift

- [ ] **2.3** Add performance optimisations
  - Cache export rule lookups per sync run
  - Batch pending export creation

### Phase 3: Attribute Priority Logic

- [ ] **3.1** Create `AttributePriorityService` in `JIM.Application/Services/`
  - `GetAuthoritativeSystemForAttribute(mvoObjectType, attributeName)`
  - `ResolveAttributeConflict(attribute, contributors)`

- [ ] **3.2** Integrate into inbound sync processing
  - When multiple systems provide values for same attribute, apply priority rules

- [ ] **3.3** Handle priority conflicts
  - Log warning when multiple Authoritative sources exist for same attribute
  - Use system priority as tiebreaker (future enhancement)

### Phase 4: UI Updates

- [ ] **4.1** Add `EnforceState` checkbox to export sync rule configuration page
- [ ] **4.2** Add `Authority` dropdown to import attribute flow configuration
- [ ] **4.3** Add validation warnings for conflicting authority configurations
- [ ] **4.4** Update sync rule documentation/help text

### Phase 5: Testing

- [ ] **5.1** Unit tests for `DriftDetectionService`
  - Drift detected when target changes authoritative attribute
  - No drift when target changes own authoritative attribute
  - EnforceState = false skips drift detection

- [ ] **5.2** Unit tests for `AttributePriorityService`
  - Authoritative beats Fallback
  - Multiple Authoritative uses system priority
  - None authority prevents import

- [ ] **5.3** Integration tests (Scenario 8)
  - Update DetectDrift test to expect passing once implemented
  - Add tests for bidirectional attribute scenarios

### Phase 6: Documentation

- [ ] **6.1** Update DEVELOPER_GUIDE.md with drift detection concepts
- [ ] **6.2** Add user documentation for EnforceState and Authority settings
- [ ] **6.3** Add troubleshooting guide for drift-related issues

---

## Open Questions

1. **What happens when EnforceState = true but the export fails?**
   - Should drift persist until next successful export?
   - Should we track "drift detected but not yet corrected" state?

2. **Should there be a system-level default for EnforceState?**
   - Per-system setting that applies to all export rules for that system?
   - Would reduce configuration burden for simple scenarios

3. **How do we handle the transition period during initial sync?**
   - When Target objects exist before JIM manages them
   - First sync might detect massive "drift" that's actually initial state
   - Need "initial reconciliation" mode vs "ongoing enforcement" mode?

4. **Should we support conditional enforcement?**
   - E.g., "Enforce state only for groups matching pattern X"
   - Or "Enforce state during business hours only"
   - Probably post-MVP complexity

5. **Notification/alerting for drift?**
   - Should JIM alert admins when drift is detected (before correcting)?
   - Useful for security monitoring
   - Could be Activity-based or separate alerting system

---

## References

- Issue #91: MV attribute priority
- Issue #173: Scenario 8 drift detection tests
- [OUTBOUND_SYNC_DESIGN.md](OUTBOUND_SYNC_DESIGN.md) - Related export evaluation design
- [SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md](SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md) - Integration test scenarios
