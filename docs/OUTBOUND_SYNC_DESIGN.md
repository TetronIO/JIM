# Outbound Sync Design Document

> **Status**: Draft - For Discussion
> **Issue**: #121
> **Last Updated**: 2025-12-02

## Overview

This document explores the design considerations for outbound synchronisation - flowing changes from the Metaverse to Connected Systems. The goal is to thoughtfully design this feature to be both powerful and easy to use, learning from the complexity and pain points of legacy ILM tools.

---

## Table of Contents

1. [Core Concepts](#core-concepts)
2. [Design Questions](#design-questions)
3. [Triggers for Outbound Sync](#triggers-for-outbound-sync)
4. [Pending Export Lifecycle](#pending-export-lifecycle)
5. [Provisioning vs Export](#provisioning-vs-export)
6. [Edge Cases & Challenges](#edge-cases--challenges)
7. [Innovation Opportunities](#innovation-opportunities)
8. [Proposed Implementation Approach](#proposed-implementation-approach)

---

## Core Concepts

### What is Outbound Sync?

Outbound sync is the process of:
1. Detecting that a Metaverse Object (MVO) has changed
2. Evaluating export sync rules to determine what Connected System Objects (CSOs) need updating
3. Creating Pending Export records describing the required changes
4. Executing those exports via the appropriate connector

### Current State

JIM already has:
- `PendingExport` model with `ChangeType` (Add, Update, Delete)
- `PendingExportAttributeValueChange` for attribute-level changes
- Export sync rule direction (`SyncRuleDirection.Export`)
- Pending export confirmation during Full Sync (verifying exports were applied)

What's missing:
- Logic to detect MVO changes and create Pending Exports
- Logic to execute Pending Exports via connectors
- Tracking of CSO origin (was it provisioned by JIM or pre-existing?)

---

## Design Questions

These questions need to be answered before implementation:

### Q1: When should outbound sync be evaluated?

**Options:**

A) **Immediately during inbound sync** - As soon as MVO is updated, evaluate export rules
   - Pros: Single pass, changes flow immediately
   - Cons: Could slow down import, complex transaction management

B) **As a separate phase after inbound sync** - Process all MVO changes, then evaluate exports
   - Pros: Cleaner separation, easier to debug
   - Cons: Two passes over data

C) **As a separate run profile** - Dedicated "Export Sync" that evaluates MVO changes
   - Pros: Maximum control, can run independently
   - Cons: Requires tracking which MVOs have pending outbound evaluation

**Recommendation**: Option A or B - immediate/phased within same sync run. This matches user expectations that a sync run processes changes end-to-end.

---

### Q2: How do we track CSO origin (provisioned vs joined)?

When an MVO is deleted, we need to know:
- CSOs that were **provisioned** by JIM → should create delete Pending Export
- CSOs that **pre-existed** and were joined → should just break the join

**Options:**

A) **Use existing `JoinType` enum** - `Projected` and `Provisioned` values exist
   - `Projected` = MVO created from this CSO (inbound)
   - `Provisioned` = CSO created from this MVO (outbound)
   - `Joined` = Pre-existing CSO matched to MVO

B) **Add explicit `ProvisionedByJim` flag to CSO**
   - More explicit, but redundant with JoinType

**Recommendation**: Option A - leverage existing `JoinType.Provisioned` value.

---

### Q3: How do we handle circular sync prevention?

If System A updates MVO, and MVO updates System B, and System B also syncs back to MVO... we could get infinite loops.

**Options:**

A) **Source tracking** - Don't export changes back to the system that caused them
   - Track `ContributedBySystem` on attribute values (already exists!)
   - Skip export rules for the originating system

B) **Change versioning** - Track change version numbers, only sync newer changes

C) **Sync direction flags** - Mark certain systems as import-only or export-only

**Recommendation**: Option A - we already have `ContributedBySystem` on attribute values. Use this to prevent circular exports.

---

### Q4: What triggers MVO deletion exports?

When an MVO is deleted, which CSOs should receive delete exports?

**Options:**

A) **All joined CSOs** - Every CSO linked to the MVO gets a delete export
   - Simple but potentially destructive (deletes pre-existing accounts)

B) **Only provisioned CSOs** - Only CSOs with `JoinType = Provisioned`
   - Safer, only deletes what JIM created

C) **Configurable per sync rule** - Export rule specifies whether deletions propagate
   - Maximum flexibility but more complex

**Recommendation**: Option B for MVP, with Option C as future enhancement.

---

### Q5: Should we support "dry run" / preview?

Allowing admins to see what would be exported before committing is valuable for:
- Initial configuration validation
- Troubleshooting sync issues
- Building confidence in changes

**Options:**

A) **Preview mode on sync run** - Run sync but don't persist exports, show what would happen
B) **Pending Export approval workflow** - Create exports but require approval before execution
C) **Both** - Preview during development, approval for production changes

**Recommendation**: Consider Option B for MVP - it's simpler than full preview and provides a safety net.

---

### Q6: How do we handle export failures?

When a connector fails to apply an export (network error, permission denied, etc.):

**Options:**

A) **Retry with backoff** - Automatically retry failed exports
B) **Error and manual intervention** - Mark as failed, require admin action
C) **Dead letter queue** - Move to separate queue after N failures

**Current implementation**: `PendingExport.ErrorCount` exists, incremented on partial failures.

**Recommendation**: Implement retry with configurable max attempts, then require manual intervention.

---

### Q7: Attribute flow priority for exports

When an MVO has attributes from multiple sources, which value gets exported?

**Example**:
- HR system sets `DisplayName = "John Smith"`
- Admin manually updates to `DisplayName = "Dr. John Smith"`
- Which value goes to Active Directory?

**Options:**

A) **Current MVO value wins** - Export whatever is currently on the MVO
B) **Priority-based** - Highest priority source value is exported
C) **Manual override flag** - Admin changes are marked and preserved

**Recommendation**: Option A for MVP (simple), with #91 (attribute priority) addressing this more fully later.

---

## Triggers for Outbound Sync

Outbound sync should be triggered by:

### 1. MVO Attribute Changes
- Inbound sync updates MVO attributes
- Evaluate export rules for each changed attribute
- Create `PendingExport` with `ChangeType = Update`

### 2. New MVO Creation (Projection)
- New MVO created from inbound CSO
- Evaluate export rules for provisioning to other systems
- Create `PendingExport` with `ChangeType = Add` for each target system

### 3. MVO Deletion
- MVO is being deleted (via deletion rules or manually)
- Find all CSOs with `JoinType = Provisioned`
- Create `PendingExport` with `ChangeType = Delete` for each

### 4. New CSO Join (to existing MVO)
- CSO joins to existing MVO that was provisioned to other systems
- Should we re-evaluate exports? Probably not for MVP.

### 5. CSO Unjoin (without MVO deletion)
- CSO is disconnected from MVO
- The CSO itself may need updating (remove JIM-managed attributes?)
- Edge case - defer to post-MVP

---

## Pending Export Lifecycle

```
┌─────────────────────────────────────────────────────────────────┐
│                    PENDING EXPORT LIFECYCLE                      │
└─────────────────────────────────────────────────────────────────┘

  MVO Change Detected
         │
         ▼
  ┌──────────────┐
  │   Created    │  PendingExport created with attribute changes
  │   (Pending)  │
  └──────┬───────┘
         │
         │  Export Run Profile executes
         ▼
  ┌──────────────┐
  │  Executing   │  Connector.Export() called
  │              │
  └──────┬───────┘
         │
    ┌────┴────┐
    │         │
    ▼         ▼
┌────────┐ ┌────────┐
│Success │ │ Failed │
└───┬────┘ └───┬────┘
    │          │
    │          │  Retry? (if ErrorCount < MaxRetries)
    │          ├──────────────────────────┐
    │          │                          │
    ▼          ▼                          ▼
┌────────┐ ┌────────────┐          ┌──────────────┐
│Exported│ │   Error    │          │ Back to      │
│        │ │ (Manual)   │          │ Pending      │
└───┬────┘ └────────────┘          └──────────────┘
    │
    │  Full Sync confirms CSO matches
    ▼
┌────────┐
│Confirmed│ → Deleted
│& Deleted│
└─────────┘
```

---

## Provisioning vs Export

Important distinction:

### Provisioning (Add)
- Creating a **new** CSO in a target system
- Requires: External ID generation, all required attributes
- JoinType: `Provisioned`
- Challenge: How to generate external IDs (e.g., AD DN, sAMAccountName)?

### Export (Update)
- Updating an **existing** CSO
- CSO may have been provisioned by JIM or pre-existed
- Only changed attributes are sent

### Deprovisioning (Delete)
- Deleting a CSO that was provisioned by JIM
- Only applies to `JoinType = Provisioned` CSOs
- May be immediate delete or "disable first" pattern

---

## Edge Cases & Challenges

### 1. External ID Generation for Provisioning
When provisioning a new AD account, we need to generate:
- DN (Distinguished Name)
- sAMAccountName
- userPrincipalName

**Options:**
- Expression-based generation in sync rules
- Function library for common patterns
- Template system

### 2. Attribute Dependencies
Some attributes depend on others:
- `mail` might require `mailNickname` to be set first
- Some attributes are only writable at creation time

**Solution**: Attribute ordering in export rules, or connector-specific logic

### 3. Reference Attributes
If MVO.Manager references another MVO, and we're provisioning to AD:
- We need to resolve MVO reference → CSO DN
- The referenced object must be provisioned first

**Solution**: Two-pass export (objects first, then references)

### 4. Partial Export Failures
If exporting 5 attributes and 2 fail:
- Current: Track which succeeded, retry failed ones
- Need to handle attributes that depend on each other

### 5. Connector Offline
Target system unavailable during export:
- Queue exports for retry
- How long to retain? Configurable retention period?

### 6. Bulk Changes
Large reorganisation affecting many objects:
- Performance considerations
- Progress tracking
- Ability to pause/resume

---

## Innovation Opportunities

Areas where JIM could improve on legacy ILM tools:

### 1. Simplified Sync Rule Configuration
MIM requires complex XML and code for sync rules. JIM could offer:
- Visual rule builder
- Common patterns as templates
- Plain-language rule descriptions

### 2. Preview / What-If Analysis
Before running sync, show:
- Objects that will be created
- Attributes that will change
- Objects that will be deleted

### 3. Approval Workflows (Lightweight)
For sensitive changes:
- Pending exports require approval
- Notification to approvers
- Audit trail of approvals

### 4. Smart Conflict Resolution
When multiple systems want to update the same attribute:
- Clear visualisation of conflict
- Configurable resolution strategies
- Manual override option

### 5. Real-Time Sync Dashboard
- Live view of sync progress
- Object-level status
- Click to drill into any object's sync history

### 6. Export Rollback
If an export causes issues:
- Track previous values
- One-click rollback to previous state
- Or: "Undo last sync run"

### 7. Dependency Visualisation
Show relationships between:
- Sync rules
- Object types
- Connected systems
- Which rules affect which attributes

---

## Proposed Implementation Approach

### Phase 1: Core Infrastructure
1. Add method to detect MVO changes needing export
2. Implement export rule evaluation
3. Create Pending Export records from evaluation

### Phase 2: Export Execution
4. Implement Export run profile processor
5. Call connector export methods
6. Handle success/failure/retry

### Phase 3: Provisioning
7. Implement CSO creation logic
8. External ID generation (basic)
9. Track `JoinType = Provisioned`

### Phase 4: Deprovisioning
10. Detect MVO deletion
11. Create delete exports for provisioned CSOs
12. Execute delete via connector

### Phase 5: Polish
13. Reference attribute handling
14. Better error handling
15. UI for pending export management

---

## Open Questions for Discussion

1. **Should pending exports require approval by default, or be auto-executed?**
   - Auto-execute is simpler, approval is safer

2. **How granular should export sync rules be?**
   - Per-attribute rules vs. all-attributes-at-once

3. **Should we support "disable before delete" pattern in MVP?**
   - Common AD pattern: disable account, wait 30 days, then delete

4. **How do we handle provisioning to systems that require specific ID formats?**
   - AD needs DN, SAM, UPN
   - Other systems have their own requirements

5. **Should export sync run as part of Full Sync, or as a separate run profile?**
   - Combined is simpler, separate gives more control

---

## Next Steps

1. Review and discuss this design document
2. Agree on answers to key design questions
3. Create detailed implementation tasks
4. Begin TDD implementation

---

## References

- Issue #121: Implement outbound sync
- Issue #25: Pending Exports report feature
- Issue #91: MV attribute priority
- Existing code: `SyncFullSyncTaskProcessor`, `PendingExport` model
