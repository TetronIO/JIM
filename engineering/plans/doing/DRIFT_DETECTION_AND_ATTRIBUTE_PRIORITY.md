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

#### Option D: Equal Precedence

Multiple systems contribute on an equal footing; last writer wins.

**Cons:** Non-deterministic in practice; the traditional ILM workaround for dual-authority scenarios that this design exists to avoid. Real deployments evidence odd behaviours such as some objects not updating from the system that should own them. **Explicitly rejected; JIM will not offer it.** Scoped sync rules at distinct priorities express every dual-authority scenario equal precedence is used for, deterministically (see "Fine-Grained Authority via Scoped Sync Rules" below).

---

### Decision: Option B - Per-Attribute Numerical Priority ✓

> **Status**: DESIGN APPROVED - Implementation deferred

**User stories (draft; needs consolidation):** as a synchronisation administrator:

- I want to priority-order the list of sync rules that have import attribute flow to a Metaverse attribute, so that multi-source contribution is deterministic.
- I want the engine to select the highest-priority rule that can contribute (enabled, joined, in scope) and use the result of that rule's attribute flow.
- I want a rule to be able to assert null/no-value for the population it covers ("Null is a value"), so that authoritative absence propagates downstream instead of being back-filled.
- I want one system to be authoritative for most objects and another system authoritative for a defined subset of objects (differently-scoped rules at different priorities), without resorting to equal-precedence semantics.
- I want disabled sync rules to remain visible in the priority list (greyed out, never contributing) so the ordering stays stable while I stage configuration changes.
- I want to see, for any MVO attribute value, which sync rule/system currently contributes it, so I can troubleshoot "where did this value come from?".

**Core Design:**

1. **Numerical priority per attribute contribution** - Each import sync rule mapping that targets an MVO attribute has a priority number (1 = highest priority, larger numbers = lower priority). The priority list for an MVO attribute is therefore a list of **sync rules** (one entry per rule with a mapping flowing to that attribute). A connected system may appear multiple times in the list via differently-scoped rules; this is what enables fine-grained authority (see below).

2. **Default behaviour (fallback chain)** - Evaluate contributions in priority order; use the first rule that yields a value

3. **Advanced option: "Null is a value"** - When enabled on a specific contribution, if that rule's system is *connected to the MVO and in scope of the rule* but contributes null/absent, stop evaluation immediately (no fallback). This allows explicitly asserting "no value" from the authoritative source. If the rule has no opinion (disabled, no joined CSO, or CSO out of scope), it is skipped and evaluation continues to the next priority regardless of this flag; see "Contribution States" in the Design section. The flag is incoherent without that distinction.

4. **Multivalued attributes: fully supported from phase 1, with winner-takes-all-values semantics** - The winning rule contributes the *entire* value set of an MVA; losing rules contribute nothing. "Connected, no value" for an MVA means the empty set, so "Null is a value" asserts an empty set (e.g. a group with no members). SVAs and MVAs are resolved by the same priority list with no extra configuration or UI. An additional per-value *merge* mode is deferred to a second iteration; see "Multivalued Attribute Handling: Options Explored" below.

**The four factors of contribution.** Whether a priority list entry contributes to an MVO attribute is determined by:

1. **Sync rule scope**: is a CSO in scope of an *enabled* sync rule that has inbound attribute flow to the MVO attribute in question?
2. **Null handling**: is "Null is a value" set on this entry in the attribute priority list?
3. **Connection**: is a CSO from that rule's connected system joined to the MVO being evaluated?
4. **Priority**: what is the rule's position in the attribute priority list?

**Example Configuration:**

```
MVO Attribute: department (Person)
+--------------------------------------------------------------------------+
| Priority | Sync Rule               | Connected System | Null Handling    |
+----------+-------------------------+------------------+------------------+
|    1     | HR People Inbound       | HR System        | [x] Null=Value   |
|    2     | CorpDir People Inbound  | Corporate Dir    | [ ] Null=Value   |
|    3     | AD Self-Service Inbound | Self-Service AD  | [ ] Null=Value   |
+----------+-------------------------+------------------+------------------+
```

**Behaviour with above configuration:**
- If HR System provides "Engineering" -> MVO gets "Engineering" (priority 1 wins)
- If HR System provides null and "Null is a value" is checked -> MVO gets null (no fallback)
- If HR System provides null and "Null is a value" is unchecked -> check Corporate Dir (priority 2)
- If Corporate Dir provides "IT Services" -> MVO gets "IT Services"
- And so on down the chain...
- If HR System has **no opinion** for this MVO (rule disabled, no joined CSO, or CSO out of the rule's scope), it is skipped entirely ("Null is a value" irrelevant) and Corporate Dir is evaluated

**Motivating scenarios for "Null is a value":**

The semantic gap it fills: a ranked precedence list cannot distinguish *"the authoritative source knows this object and says it has no value"* from *"the authoritative source doesn't know this object"*. Plain fall-through treats both as "no contribution, ask the next source".

1. **Clears must propagate (stale value resurrection)**: a manager leaves and HR clears `manager`; a lower-priority directory still holds the old manager. With plain fall-through the metaverse keeps the old manager, and everything driven by it (manager-based approvals, dynamic groups) keeps operating against the wrong person. Same shape for `department`/cost centre cleared during a reorg. Traditional ILM solutions needed custom rules extensions and sentinel-value hacks to express this.
2. **Fallback for one population, exclusivity for another, in one configuration**: a secondary system must stay in the priority list because it is the only source for objects the primary doesn't manage, but for primary-managed objects, blank-in-primary must mean blank. See the worked migration example below.
3. **Data minimisation / compliance**: personal data removed at the authoritative source must propagate as a removal everywhere, not be re-sourced from a secondary copy and republished indefinitely.
4. **Disconnect/leaver semantics**: combined with attribute recall, when the authoritative CSO obsoletes with "Null is a value", the attribute is asserted null instead of being reanimated by a secondary source.

> **Guardrail (implementation):** "Null is a value" amplifies blast radius: a misbehaving priority-1 import (truncated file, empty delta) becomes a mass attribute-clearing event rather than a harmless no-op. Consider an anomaly warning when a priority-1 source contributes null for an unusually high share of objects in a run, consistent with the synchronisation-integrity principles.

**Worked example 1 (HR system migration; null assertion + fallback):**

An MVO object type has three connected systems: **AD** (target), **HR 1** (new HR system, priority 1, "Null is a value" on its mappings), **HR 2** (legacy HR system, priority 2). Both HR systems may project to the MV; most people exist in HR 2, and some have not yet been migrated to HR 1.

- **Person in HR 1**: HR 1 is connected, so it is always authoritative. `department = null` on the HR 1 CSO is asserted into the MV and flows to AD as a clear; HR 2 can never contribute for this person.
- **Person only in HR 2**: HR 1 has *no opinion* for this MVO, so evaluation skips HR 1 entirely (regardless of "Null is a value") and HR 2 contributes full attribute flow.

Without the no-opinion distinction, "Null is a value" on HR 1 would block HR 2 from ever contributing, making HR 2's inbound flow pointless. With it, one priority list serves both populations.

> **Migration note:** the day a person is added to HR 1, the new join flips HR 1 from no-opinion to connected, and HR 1's blank fields will (correctly) clear values HR 2 had been contributing. This is the intended semantic but is operationally surprising; user documentation must call it out so migration runbooks populate HR 1 records before joining them.

**Fine-grained authority via scoped sync rules:**

Because priority list entries are **sync rules** (not connected systems), and a system can have multiple import rules with different scopes, per-subset authority falls out of the model with no extra machinery:

> One system is nominally authoritative for an attribute (e.g. `member` on groups), but another system is authoritative for that attribute **for a defined subset of objects** (e.g. groups named `SG-*` are managed directly in AD). Updates from AD for that subset flow to the Metaverse and must not be overwritten; updates to those groups in any other system are corrected by drift enforcement. For all other groups, the nominally authoritative system wins and direct AD changes are corrected.

This is expressed by creating multiple inbound rules for the same system: some with no scope (apply to the whole connector space) and some with a narrower scope, then ordering them in the attribute priority list. Traditional ILM systems cannot express this: they flatten all flows for a system into a single contributor entry, leaving only "equal precedence" (with its non-determinism) for dual-authority scenarios.

**Worked example 2 (dual-forest group authority transfer):**

- An organisation has an **AD 1** forest where it authors groups, and an **AD 2** forest. JIM initially synchronises groups AD 1 -> AD 2.
- It later decides to manage groups in **AD 2** instead and synchronise them back to AD 1, **except** for a subset of groups it wants to continue managing in AD 1.
- Object matching: simple, on `sAMAccountName` for both systems. Export rules to AD 1 are created for the writeback (not shown; exports do not affect Metaverse attribute values and so do not appear in the attribute priority list).

Configuration at the end of the timeline:

| Connected System | Sync Rule                            | Projection Enabled? | Status       | Priority |
|:-----------------|:-------------------------------------|:--------------------|:-------------|:---------|
| AD 1             | AD 1 - Groups - Exceptions - Inbound | Yes                 | Active       | 1        |
| AD 1             | AD 1 - Groups - Legacy - Inbound     | Yes                 | **Disabled** | 2        |
| AD 2             | AD 2 - Groups - Inbound              | Yes                 | Active       | 3        |
| AD 1             | AD 1 - Groups - Inbound              | Yes                 | Active       | 4        |

> **Note:** `AD 1 - Groups - Legacy - Inbound` is disabled but still shown in the priority list. A disabled rule has no bearing on synchronisation decisions (it is never evaluated), but keeping it visible avoids destabilising the ordering of the other rules while configuration changes are staged.

Expected outcomes:

- New groups from AD 2 in scope of `AD 2 - Groups - Inbound` are projected to the Metaverse and provisioned to AD 1.
- New groups from AD 1 in scope of `AD 1 - Groups - Legacy - Inbound` only are **not** projected; the rule is disabled.
- New groups from AD 1 in scope of `AD 1 - Groups - Exceptions - Inbound` are projected to the Metaverse and provisioned to AD 2.
- New groups from AD 1 in scope of `AD 1 - Groups - Inbound` are projected if no matching MVO exists, provisioned to AD 2, and thereafter managed in AD 2: subsequent AD 2 changes flow back to AD 1, and direct AD 1 changes are corrected as drift (priority 3 beats priority 4).
- Changes to groups in scope of `AD 1 - Groups - Exceptions - Inbound` synchronise to the Metaverse and on to AD 2 (priority 1 wins).
- Changes to groups in scope of `AD 1 - Groups - Inbound` (non-exception groups) lose resolution to `AD 2 - Groups - Inbound` and are corrected in AD 1 by export re-evaluation.

> **Scope transition note:** scoping criteria evaluate against CSO attributes, so an object can move in or out of a rule's scope when its attributes change (e.g. a group renamed to match the exceptions pattern). Authority then transfers between systems on the object's next sync. This is the intended semantic, but it is powerful and quiet; user documentation must call it out.

Note that fine-grained authority is **per object** (which system owns this group's membership), so it works under winner-takes-all-values MVA semantics; it does not require per-value merge.

**Multivalued Attribute Handling: Options Explored**

MVAs are **fully supported from phase 1**; this section records the research (Jun 2026) into which resolution semantic they should use. Options evaluated on their merits:

| Option | Description | Assessment |
|--------|-------------|------------|
| **1. Winner-takes-all-values** | The winning rule contributes the entire value set; losing rules contribute nothing. NullIsValue asserts the empty set. | Deterministic, one-sentence explainable, identical mental model and UI to SVAs, and the dominant semantic for ranked-precedence resolution across the industry. Fine-grained authority scenarios work because authority is per object (worked example 2). **Selected for phase 1.** |
| **2. Per-value merge/union** | Every contributing rule's values are combined into a union. | Genuinely needed in a minority of scenarios (mail alias attributes contributed by multiple systems, cross-forest group membership where both sides legitimately add members). However, safe merge requires substantial machinery: per-value provenance, removal semantics (which contributor may delete a value), and dedup/conflict rules. Demand research shows removal semantics is the universal hard part wherever merge exists. **Deferred to iteration 2.** |
| **3. Last-writer-wins / equal footing** | Contributors overwrite each other in sync order. | Non-deterministic; this is what attribute priority exists to eliminate (Option D above). **Rejected.** |
| **4. Per-value priority** | Each individual value is resolved by the priority of its contributor. | Effectively merge with ranked removal rights; collapses into option 2's design space rather than standing alone. **Subsumed into the iteration 2 exploration.** |

**Iteration 2 sketch (not committed):** a per-contribution mode, e.g. `Exclusive` (default; winner-takes-all-values) vs `Merge` (rule contributes its values into a union), with removal rights scoped to each rule's own contributed values. JIM's per-row `ContributedBySystemId` already provides the per-value provenance this requires. To be explored and designed as a follow-up issue once iteration 1 is in production and real demand is validated.

**Configuration Change Propagation**

Decided (Jun 2026): a three-mode model, delivered incrementally. Changing attribute priority configuration (reordering, NullIsValue, rule enablement) does not in itself initiate synchronisation; resolution happens at sync time. The modes:

| Mode | Behaviour | Delivery |
|------|-----------|----------|
| **1. Apply only** (default) | The change is saved and takes effect as objects are next synchronised. The admin acknowledges on save that MVOs will not reflect the new configuration until a full synchronisation of affected objects completes, and that an in-flight or imminent *delta* sync schedule will apply the new configuration only to recently-changed CSOs, leaving results out of kilter with the rest until a full sync runs. | **Phase 1** |
| **2. Impact analysis (preview) before applying** | A read-only analysis of what would change across all affected objects if the new priority configuration were applied, reviewed before committing. Builds on Sync Preview / What-If Analysis (#288, on the `SyncOutcome` foundation from #363), and is the same capability pattern as scope-change preview (#204) and connected system deletion impact analysis (#134); these should be designed as one family. #827 maps the full configuration-change preview coverage across all sync-affecting surfaces and proposes the unified framework. | Later iteration |
| **3. Apply and re-synchronise** | After saving, trigger a full re-synchronisation of all affected objects. Requires suspending active synchronisation schedules for the duration, and has implications for the future Event-Based Synchronisation mode. | Later iteration; needs concept validation |

Why "apply only" is an acceptable phase 1 default: it provides a natural safeguard (a mistaken change can be undone before any sync runs) and is the shortest development path. The known cons (delta-sync skew, delayed desired state with possible compliance impact) are mitigated in phase 1 by the save-time acknowledgement and a recommendation to run a full synchronisation; the mode is also genuinely preferable for admins iterating quickly during design/build phases.

Phase 1 mitigation (cheap, recommended): persist a "configuration changed since last full synchronisation" indicator per affected object type, surfaced on the priority management UI and the Operations page, so the out-of-kilter window is visible rather than silent.

Definition needed for modes 2 and 3: "affected objects" = MVOs of the object type joined to CSOs in scope of the sync rules whose mappings changed; pin down precisely in their design.

**Rationale:**

1. **Granular control** - Different attributes can have different priority orders, even from the same connected system

2. **Addresses traditional ILM limitation** - The "Null is a value" option solves a common frustration with traditional identity management systems where you couldn't assert null from an authoritative source

3. **Operational flexibility** - Admins can reorder priorities at any time without removing/recreating sync rules. This is valuable for staged configuration changes ahead of business change windows.

4. **Explicit over implicit** - Priority is explicitly configured, not inferred from rule order or other implicit factors

**Design Decisions:**

- **Priority storage**: per sync rule mapping (rule + target attribute). The UI presents the priority list per MVO attribute with sync rules as the line items; per-attribute divergence in a rule's rank is therefore possible (see open question on ordering UX)
- **Default priority (safe addition)**: when a new import mapping is created targeting an attribute that already has contributors, auto-assign the next-lowest priority (max existing priority for that attribute + 1). This is deliberately a safe, non-disruptive default: the newly added IAF never wins resolution until an admin explicitly reorders the attribute's priority list. Resolution must have a deterministic tie-break (e.g. mapping id) as a safety net, but duplicate priorities within one attribute's list should be prevented by validation
- **Default null handling**: "Null is a value" = false (fallback behaviour, matching traditional ILM expectations)
- **MVA semantics (phase 1)**: winner-takes-all-values; an additional per-value merge mode deferred to iteration 2 (see options above)
- **Configuration change propagation**: apply-only with acknowledgement in phase 1; impact analysis and apply-and-resync as later iterations (see "Configuration Change Propagation" above)
- **Disabled rules**: remain visible in the priority list (greyed out) but are never evaluated
- **Equal precedence**: deliberately not offered (see Option D above)
- **UI placement and navigation**: deliberately undecided; gated on the admin IA review (see UI section below)

---

## Design

### Summary

| Aspect | Approach | Status |
|--------|----------|--------|
| **Drift Detection** | | |
| Drift detection trigger | On inbound sync, when CSO has export rules targeting it | ✓ Ready for implementation |
| Drift detection control | `EnforceState` flag on **export** sync rules, **default: true**, hidden in Advanced Options UI | ✓ Ready for implementation |
| **Attribute Priority** | | |
| Priority model | Per-attribute numerical priority on import mappings; sync rules are the priority list line items (multiple differently-scoped rules per system enable fine-grained authority) | Design approved, implementation deferred |
| Default behaviour | Fallback chain - use the first contribution with an opinion and a value, in priority order | Design approved, implementation deferred |
| Null handling | "Null is a value" flag per contribution (default: false) | Design approved, implementation deferred |
| Equal precedence | Deliberately not offered; scoped rules at distinct priorities replace it | Design approved |

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
    /// Priority for this attribute contribution when multiple sync rules import
    /// to the same MVO attribute. Lower numbers = higher priority (1 is highest).
    /// Only applicable to import sync rules.
    /// </summary>
    public int Priority { get; set; } = int.MaxValue; // Sentinel until auto-assigned (max existing + 1)

    /// <summary>
    /// When true, if this rule's system is connected to the MVO and in scope
    /// but contributes null/absent for this attribute, stop evaluation
    /// immediately without falling back to lower-priority contributions.
    /// Has no effect when the rule has no opinion for the MVO (disabled rule,
    /// no joined CSO, or CSO out of scope). When false (default), null values
    /// are skipped and evaluation continues to the next priority level.
    /// </summary>
    public bool NullIsValue { get; set; } = false;
}
```

**Note:** The `Priority` value is scoped to the target MVO attribute. When a new import mapping is created targeting an MVO attribute that already has contributors, the system should assign the next available priority number (max existing priority + 1). No other schema changes are required: `SyncRule.Enabled`, `SyncRule.ObjectScopingCriteriaGroups`, and per-rule mappings already provide the rule-granular building blocks for fine-grained authority.

### Sync Engine Changes

#### Contribution States

A rule's contribution to an MVO attribute is **tri-state**; the distinction between "no opinion" and "connected, no value" is what makes "Null is a value" coherent:

| State | Meaning | Evaluation |
|-------|---------|------------|
| **No opinion** | The rule is disabled, no CSO from the rule's system is joined to the MVO, or the joined CSO is out of the rule's scope | Always skip to the next priority, regardless of `NullIsValue` |
| **Connected, no value** | The rule is enabled and a joined, in-scope CSO exists, but the mapping yields nothing (for an MVA: the empty set) | If `NullIsValue`: stop and assert null/empty set. Otherwise fall through to the next priority |
| **Connected, with value** | The rule is enabled, a joined, in-scope CSO exists, and the mapping yields a value | This value wins (for an MVA: the entire value set) |

Scope matters, not just join existence: if a rule's scoping criteria exclude the joined CSO, the rule has no opinion; otherwise an out-of-scope rule could assert nulls it has no entitlement to assert.

#### Attribute Priority Resolution (Inbound Sync)

```csharp
enum ContributionState
{
    NoOpinion,          // rule disabled, no joined CSO, or CSO out of rule scope
    ConnectedNoValue,   // enabled rule with joined, in-scope CSO; mapping yields nothing
    ConnectedWithValue  // enabled rule with joined, in-scope CSO; mapping yields a value
}

/// <summary>
/// Resolves the winning value for an MVO attribute when multiple sync rules contribute.
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
            case ContributionState.NoOpinion:
                // This rule has no opinion on this MVO; always continue,
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

> **Implementation notes:** the MVO stores only the *winning* value (`MetaverseObjectAttributeValue` with `ContributedBySystemId`); it does not retain the losing contributions. `EvaluateContribution` therefore implies reading the MVO's other joined CSOs, and evaluating scoping criteria against them, at resolution time whenever fallback is needed. Note also that `AssertedNull` and `NoContributor` produce the same stored state (no attribute value row); see the open question on asserted-null observability.

#### Interaction with Drift Detection (priority-aware contributor check)

The shipped drift detection treats any system with an import mapping for an attribute as a legitimate contributor (`DriftDetectionService.HasImportRuleForAttribute`, also shown in the drift pseudocode above) and skips drift evaluation for it. Once attribute priority lands, **contributor legitimacy must become priority-aware**:

- A CSO's inbound change to an attribute is legitimate only if its contribution **wins** resolution for that MVO attribute (enabled, in scope, connected, and highest priority among contributions with an opinion).
- When a CSO's change *loses* resolution, the MV retains the winning value, and the losing system's local state is corrected by export re-evaluation where an export rule with `EnforceState` targets that system (see worked example 2: direct AD 1 changes to non-exception groups are corrected because AD 2's rule outranks AD 1's).

> **Sync flow remains linear.** Inbound processing never writes to CSOs or to connected systems; a losing contribution simply never reaches the MVO (the import still updates the CSO, which mirrors the source system's actual state). Correction of the losing system is purely the standard outbound path: that system is *also* an export target, so when export evaluation finds its actual state differs from the expected state derived from the MVO, a corrective pending export is staged like any other export. If no export rule with `EnforceState` targets that system for the attribute, the losing system simply remains divergent; the Metaverse and downstream systems are protected either way.

This supersedes the earlier open question on drift interaction; the dual-forest scenario's expected outcomes require it.

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

Whichever navigation model wins, the priority list's line items are **sync rules** (priority, sync rule, connected system, enabled/disabled state, "Null is a value"); disabled rules appear greyed out and non-contributing but hold their position. The centralised-view candidate provides a single view of all MVO attributes that have multiple contributors, allowing admins to manage priority across the entire system.

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
- [ ] Implement the tri-state contribution evaluation (no opinion / connected-no-value / connected-with-value), respecting rule enabled state and scoping criteria
- [ ] Implement winner-takes-all-values MVA resolution (winning rule replaces the full value set; per-row `ContributedBySystemId` makes the diff computable)
- [ ] Integrate into inbound sync processing (`SyncRuleMappingProcessor`)
- [ ] Auto-assign priority on new import mapping creation (max existing + 1); deterministic tie-break in resolution
- [ ] Make the drift detection contributor check priority-aware: legitimate only when the contribution wins resolution (replaces the has-import-rule check in `DriftDetectionService`)
- [ ] Replace the interim grace period recall freeze with proper next-contributor fallback
- [ ] Anomaly guardrail: warn when a priority-1 `NullIsValue` source contributes null for an unusually high share of objects in a run
- [ ] Track "configuration changed since last full synchronisation" per affected object type (apply-only propagation mode)

#### Future Phase 3: UI Updates

- [ ] Build the priority management UI per the Future Phase 0 IA review outcome (per-attribute schema pages, centralised view, or both)
- [ ] Save-time acknowledgement messaging and "configuration changed since last full synchronisation" indicator (see "Configuration Change Propagation")
- [ ] Add priority context panel to import sync rule mapping editor
- [ ] Add "Advanced Options" section to import mapping editor with "Null is a value" checkbox
- [ ] Add priority indicator column to sync rule list view

#### Future Phase 4: Testing

- [ ] Unit tests for `AttributePriorityService`
- [ ] Integration tests for multi-source priority resolution
- [ ] Integration tests for NullIsValue behaviour, including the tri-state cases:
  - [ ] Priority-1 rule has no opinion (not joined): lower-priority rule contributes fully despite NullIsValue on priority 1 (HR migration scenario)
  - [ ] Priority-1 rule connected with null and NullIsValue: null asserted, no fallback
  - [ ] Priority-1 rule's CSO joined but out of rule scope: treated as no opinion
  - [ ] Priority-1 rule disabled: treated as no opinion
  - [ ] New join to priority-1 system mid-life: its blanks clear previously contributed values
- [ ] Integration tests for MVA winner-takes-all-values:
  - [ ] Winning rule's full value set replaces previous contributor's values
  - [ ] NullIsValue on an MVA asserts the empty set (e.g. group membership cleared)
- [ ] Integration tests for fine-grained authority (worked example 2):
  - [ ] Scoped exception rule (priority 1) wins for in-scope objects; direct changes in the lower-priority system are corrected via EnforceState export
  - [ ] Non-exception objects: higher-priority system's rule wins; losing system's direct changes corrected back
  - [ ] Object moves into/out of a scoped rule's coverage: authority transfers on next sync
- [ ] Configuration change propagation:
  - [ ] Priority reorder followed by delta sync: only changed CSOs re-resolve (documented apply-only behaviour)
  - [ ] Priority reorder followed by full sync: all objects re-resolve to the new configuration

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

4. **Priority assignment for new/bulk import mappings** - DECIDED (Jun 2026): a new import mapping targeting an attribute that already has contributors is auto-assigned the next-lowest priority (max existing priority for that attribute + 1). This makes adding an IAF a safe, non-disruptive action: the new flow never wins resolution until an admin explicitly reorders the attribute's priority list. Bulk rule creation: each new mapping likewise lands at the bottom of its attribute's list.

5. **Priority conflict warnings** - DECIDED (Jun 2026): no active warnings in the first iteration. Adding a 2nd+ IAF for an attribute silently lands at the bottom of the priority list (per #4); it is the admin's responsibility to reorder if the new contributor should win. The passive priority context panel shows where a mapping sits but does not warn. A later iteration adds active warnings and a guided flow prompting the admin to configure priority when adding a second or subsequent IAF for an attribute.

6. **Cross-object-type attribute priority** - DECIDED (Jun 2026): no. Priority remains scoped per object type (Person, Group, etc.); a global priority configuration adds complexity with no immediate benefit.

7. **Multivalued attributes** - DECIDED for phase 1 (Jun 2026): MVAs fully supported with winner-takes-all-values; "Null is a value" on an MVA asserts the empty set. An additional per-value merge mode is deferred to iteration 2; see "Multivalued Attribute Handling: Options Explored" in the decision section. Residual questions for the follow-up design only: per-contribution `Exclusive` vs `Merge` mode, removal semantics ("each rule may remove only the values it contributed"?), dedup/conflict rules, and interaction with NullIsValue under merge mode.

8. **Interaction with drift detection** - RESOLVED into the design (Jun 2026): contributor legitimacy becomes priority-aware; a losing contributor's direct changes are corrected via `EnforceState` export re-evaluation. See "Interaction with Drift Detection" in the Design section.

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

12. **Ordering granularity (per-attribute vs per-rule)** - CLARIFIED (Jun 2026): priority is inherently per attribute. The priority list is scoped to one MVO attribute and orders the sync rules contributing to that attribute. Because storage is per mapping (rule + attribute), a single rule can legitimately rank differently for different attributes (e.g. HR Inbound is priority 1 for `department` but priority 2 for `jobTitle`); this divergence is the whole point of per-attribute priority (vs the ruled-out system-level Option A) and is fully allowed. A convenience "set this rule's rank consistently across all attributes it contributes" bulk gesture is deferred to a later iteration as polish (and is only well-defined on the attributes two rules share); not needed for the first iteration.

13. **Re-evaluation after configuration changes** - DECIDED (Jun 2026): three-mode model; see "Configuration Change Propagation" in the decision section. Phase 1 ships apply-only with acknowledgement messaging and a changed-since-last-full-sync indicator; impact analysis (builds on #288/#204/#134) and apply-and-resync (schedule suspension, Event-Based Synchronisation implications) are later iterations needing their own design and concept validation. Residual: pin down the precise definition and computation of "affected objects".

---

## References

- Issue #91: MV attribute priority
- Issue #173: Scenario 8 drift detection tests
- [OUTBOUND_SYNC_DESIGN.md](../done/OUTBOUND_SYNC_DESIGN.md) - Related export evaluation design
- [SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md](../done/SCENARIO_8_CROSS_DOMAIN_ENTITLEMENT_SYNC.md) - Integration test scenarios
