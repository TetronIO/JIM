# Outbound Sync Design Document

> **Status**: Implementation Ready
> **Issue**: #121
> **Last Updated**: 2025-12-04

## Overview

This document explores the design considerations for outbound synchronisation - flowing changes from the Metaverse to Connected Systems. The goal is to thoughtfully design this feature to be both powerful and easy to use, learning from the complexity and pain points of legacy ILM tools.

---

## Table of Contents

1. [Core Concepts](#core-concepts)
2. [Design Questions](#design-questions)
3. [Event-Based Sync Roadmap](#event-based-sync-roadmap)
4. [Export Execution Strategy](#export-execution-strategy)
5. [Triggers for Outbound Sync](#triggers-for-outbound-sync)
6. [Pending Export Lifecycle](#pending-export-lifecycle)
7. [Provisioning vs Export](#provisioning-vs-export)
8. [Edge Cases & Challenges](#edge-cases--challenges)
9. [Innovation Opportunities](#innovation-opportunities)
10. [Implementation Plan](#implementation-plan)

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

A) **Immediately when MVO changes** - As soon as MVO is updated, evaluate export rules and create Pending Exports
   - Pros: Single pass, changes flow immediately, enables event-based sync
   - Cons: Could slow down import, complex transaction management

B) **As a separate phase after inbound sync** - Process all MVO changes, then evaluate exports
   - Pros: Cleaner separation, easier to debug
   - Cons: Two passes over data, doesn't support real-time sync well

C) **As a separate run profile** - Dedicated "Export Sync" that evaluates MVO changes
   - Pros: Maximum control, can run independently
   - Cons: Requires tracking which MVOs have pending outbound evaluation

**✅ DECISION: Option A** - Evaluate and create Pending Exports immediately when MVO changes.

**Rationale:**
1. **Performance** - Work is distributed across time as changes occur, avoiding "thundering herd" during sync runs
2. **Event-Based Enablement** - This architecture naturally supports both scheduled and event-based sync modes (see [Event-Based Sync Roadmap](#event-based-sync-roadmap))
3. **Parallelisation** - Modern async/parallel processing fits naturally; each MVO change can spawn parallel export evaluations
4. **Decoupling** - The Pending Export table becomes a queue that decouples "what needs to happen" from "when it happens"

The export *execution* remains a separate concern - can be immediate, scheduled, or hybrid per-system.

---

### Q2: How do we track CSO origin (provisioned vs joined)?

When an MVO is deleted, we need to know:
- CSOs that were **provisioned** by JIM -> should create delete Pending Export
- CSOs that **pre-existed** and were joined -> should just break the join

**Options:**

A) **Use existing `JoinType` enum** - `Projected` and `Provisioned` values exist
   - `Projected` = MVO created from this CSO (inbound)
   - `Provisioned` = CSO created from this MVO (outbound)
   - `Joined` = Pre-existing CSO matched to MVO

B) **Add explicit `ProvisionedByJim` flag to CSO**
   - More explicit, but redundant with JoinType

**✅ DECISION: Option A** - Leverage existing `JoinType.Provisioned` value.

**MVP Behaviour**:
- Only delete CSOs where `JoinType = Provisioned`
- CSOs with `JoinType = Joined` will have their join broken but no delete export created
- This is the safest default - JIM only deletes what it created

**Post-MVP Enhancement** (Issue #126):
- Add configurable `CsoDeletionBehaviour` enum: `ProvisionedOnly`, `AllJoined`, `DisconnectOnly`
- Allow per-sync-rule configuration of deletion behaviour
- Consider "disable before delete" pattern for AD accounts
- Consider scope fallout behaviour (what happens when object falls out of sync rule scope)

---

### Q3: How do we handle circular sync prevention?

If System A updates MVO, and MVO updates System B, and System B also syncs back to MVO... we could get infinite loops.

**When does this apply?**

This is an edge case that only occurs when a connected system has **both import and export** flow defined for the same attribute. Most deployments have clear source/target separation (HR imports, AD exports), but bidirectional scenarios exist.

**Example - AD with bidirectional title sync:**

```
┌─────────────────────────────────────────────────────────────────┐
│  Sync Rules for AD:                                             │
│                                                                 │
│  Import Rule: ad.title -> mvo.title                             │
│  Export Rule: mvo.title -> ad.title                             │
│                                                                 │
│  WITHOUT prevention:                                            │
│  1. Admin changes title in AD to "Senior Engineer"              │
│  2. Import: ad.title -> mvo.title                               │
│  3. Export eval: mvo.title changed -> PendingExport to AD       │
│  4. Export: writes same value back to AD (wasteful)             │
│  5. Next import: may detect "change" -> loop continues          │
│                                                                 │
│  WITH prevention (Option A):                                    │
│  1. Admin changes title in AD to "Senior Engineer"              │
│  2. Import: ad.title -> mvo.title                               │
│     mvo.title.ContributedBySystem = AD  ← tracked!              │
│  3. Export eval for AD:                                         │
│     ContributedBySystem (AD) == TargetSystem (AD) -> SKIP       │
│  4. Export eval for other systems:                              │
│     ContributedBySystem (AD) != TargetSystem -> create export   │
│  5. No circular sync, no wasted exports                         │
└─────────────────────────────────────────────────────────────────┘
```

**Options:**

A) **Source tracking** - Don't export changes back to the system that caused them
   - Track `ContributedBySystem` on attribute values (already exists!)
   - Skip export rules for the originating system

B) **Change versioning** - Track change version numbers, only sync newer changes

C) **Sync direction flags** - Mark certain systems as import-only or export-only

**✅ DECISION: Option A** - Leverage existing `ContributedBySystem` on attribute values.

**Implementation:**

```csharp
public async Task EvaluateExportRulesAsync(MetaverseObject mvo, MetaverseObjectAttribute changedAttribute)
{
    var exportRules = await GetExportRulesForObjectType(mvo.ObjectType);

    foreach (var rule in exportRules)
    {
        // Q3: Skip if this attribute came FROM the target system
        if (changedAttribute.ContributedBySystem?.Id == rule.ConnectedSystem.Id)
        {
            _logger.Debug("Skipping export to {System} - it contributed this value",
                rule.ConnectedSystem.Name);
            continue;  // Don't export back to source
        }

        // Create pending export for this target system
        await CreatePendingExportAsync(mvo, rule, changedAttribute);
    }
}
```

**Note**: For most deployments with clear source/target separation, this check will rarely trigger but provides safety for bidirectional scenarios.

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

**✅ DECISION: Option B** - Only provisioned CSOs for MVP, with Option C as future enhancement (Issue #126).

**Rationale**: This aligns with Q2 decision - JIM only deletes what it created. Configurable behaviour deferred to post-MVP.

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

**✅ DECISION: Option C** - Support both Preview and Preview + Sync modes.

**Implementation:**

The user selects the run mode when executing a sync:
- **Preview Only** - Evaluates sync rules and shows what changes would be made, but does not persist any Pending Exports or execute changes
- **Preview + Sync** - Evaluates sync rules, shows the preview, then persists Pending Exports and executes them

This gives admins full control:
- Use Preview Only when testing new sync rules or troubleshooting
- Use Preview + Sync for normal operations with visibility into what's happening

---

### Q6: How do we handle export failures?

When a connector fails to apply an export (network error, permission denied, etc.):

**Options:**

- A) **Retry with backoff** - Automatically retry failed exports
- B) **Error and manual intervention** - Mark as failed, require admin action
- C) **Dead letter queue** - Move to separate queue after N failures

**Current implementation**: `PendingExport.ErrorCount` exists, incremented on partial failures.

**✅ DECISION: Option A + B** - Retry with configurable max attempts, then require manual intervention.

**Implementation:**

- Automatic retry with exponential backoff on transient failures
- Configurable max retry attempts (default: 5)
- After max retries exceeded, mark export as `Failed` and require manual intervention
- Failed exports visible in dashboard with clear error details
- Proactive notification system (future enhancement) to alert admins of persistent failures

**Rationale**: Purely automatic retries (as seen in legacy systems) led to issues going unaddressed indefinitely, especially with poor notification capabilities and server-based management tools. Combining automatic retries with a definitive failure state ensures transient issues are handled automatically whilst persistent problems surface for admin attention.

---

### Q7: Attribute flow priority for exports

When an MVO has attributes from multiple sources, which value gets exported?

**Example**:
- HR system sets `DisplayName = "John Smith"`
- Admin manually updates to `DisplayName = "Dr. John Smith"`
- Which value goes to Active Directory?

**Options:**

- A) **Current MVO value wins** - Export whatever is currently on the MVO
- B) **Priority-based** - Highest priority source value is exported
- C) **Manual override flag** - Admin changes are marked and preserved

**✅ DECISION: Option A** - Export the current MVO value.

**Rationale**: This is not a separate decision for outbound sync - it's a natural consequence of the architecture:

1. **Inbound sync** determines which source value "wins" via attribute priority (Issue #91)
2. The winning value is applied to the MVO
3. **Outbound sync** exports whatever is currently on the MVO

The MVO is the single source of truth. Attribute priority is an inbound concern; outbound sync simply exports the authoritative MVO value. There's no additional complexity needed here.

> **See also**: [DRIFT_DETECTION_AND_ATTRIBUTE_PRIORITY.md](DRIFT_DETECTION_AND_ATTRIBUTE_PRIORITY.md) for the detailed design of how attribute priority is determined when multiple sources contribute to the same attribute (Issue #91).

---

### Q8: Parallelism and Concurrency Strategy

**Context:**

Parallelism is a critical architectural concern that affects performance, reliability, and complexity. Common pitfalls observed in identity management systems include:

- **Single-threaded sync** becoming a significant bottleneck, requiring hours to complete large sync runs
- **Parallel sync operations** causing table deadlocks and data corruption when not properly isolated
- These industry lessons inform a cautious approach to parallelism in JIM

**The Problem with Premature Parallelism:**

EF Core's `DbContext` is **not thread-safe**. Using constructs like `Task.WhenAll()` or `SemaphoreSlim` with parallel database operations can cause:
- Race conditions and data corruption
- Deadlocks when multiple threads access the same tables
- Non-deterministic behaviour that's difficult to debug

**Decision: Sequential for MVP, Parallelism Implemented Post-MVP**

The original MVP decision was sequential-only operations. Post-MVP, parallelism has been implemented across multiple axes with safe defaults (all default to sequential/1):

**Implemented Parallelism (see `docs/plans/EXPORT_PERFORMANCE_OPTIMISATION.md`):**

1. **LDAP Connector Pipelining** (Phase 2) — Multiple LDAP operations execute concurrently within a single export batch:
   - Per-connector "Export Concurrency" setting (1-16, default 1)
   - `SemaphoreSlim`-based throttling with async APM wrappers (`LdapConnectionExtensions.SendRequestAsync`)
   - Container creation serialised to prevent race conditions; multi-step operations remain sequential within each export

2. **Parallel Batch Export Processing** (Phase 3) — Multiple export batches process concurrently:
   - Per-Connected System `MaxExportParallelism` setting (1-16, default 1)
   - Each parallel batch creates its own `IRepository` (via factory delegate) and `IConnector` instance
   - Gated by `SupportsParallelExport` connector capability (LDAP: true, File: false)
   - Thread-safe result aggregation under `lock`; progress callback serialised via `SemaphoreSlim(1,1)`

3. **Parallel Schedule Step Execution** (Phase 4) — Schedule steps at the same `StepIndex` execute concurrently:
   - Scheduler detects parallel groups and queues tasks with `ExecutionMode = Parallel`
   - Worker dispatches parallel task groups via `Task.WhenAll`, each with its own DI scope
   - Integration tested with timing overlap validation (Scenario 6)

**Safety approach** (unchanged from MVP philosophy):
- All parallelism defaults to sequential (opt-in via admin configuration)
- Each parallel unit gets its own `DbContext` — no shared EF Core contexts across threads
- Per-system configuration rather than global flags (different systems have different capacity)

**✅ DECISION: Sequential operations for MVP, parallelism implemented post-MVP with safe defaults.**

See `docs/plans/EXPORT_PERFORMANCE_OPTIMISATION.md` for full implementation details.

---

## Event-Based Sync Roadmap

> **Status**: Future Enhancement
> **Prerequisites**: Outbound sync MVP complete

### Overview

Event-Based synchronisation enables near-real-time identity propagation, where changes in source systems are detected and processed immediately rather than waiting for scheduled sync runs. Some organisations have cross-domain identity change replication SLAs measured in seconds, which necessitates event-based rather than schedule-based sync.

### Why Option A Enables Event-Based Sync

The decision to evaluate exports **immediately when MVO changes** (Q1 Option A) creates an architecture that naturally supports both sync modes:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    UNIFIED SYNC ARCHITECTURE                                │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  INBOUND TRIGGER              PROCESSING              EXPORT EXECUTION      │
│  ───────────────              ──────────              ────────────────      │
│                                                                             │
│  ┌─────────────┐                                                            │
│  │ Scheduled   │──┐                                                         │
│  │ Full/Delta  │  │                                                         │
│  └─────────────┘  │         ┌──────────────┐      ┌─────────────────┐       │
│                   ├────────>│   Inbound    │─────>│ Pending Exports │       │
│  ┌─────────────┐  │         │   Sync       │      │    Created      │       │
│  │ Webhook/    │  │         │  (Same code) │      └────────┬────────┘       │
│  │ Notification│──┤         └──────────────┘               │                │
│  └─────────────┘  │                                        │                │
│                   │                               ┌────────┴────────┐       │
│  ┌─────────────┐  │                               │                 │       │
│  │ SCIM Push   │──┤                               ▼                 ▼       │
│  │             │──┘                         ┌───────────┐     ┌───────────┐ │
│  └─────────────┘                            │ Scheduled │     │ Immediate │ │
│                                             │   Batch   │     │ Execute   │ │
│                                             └───────────┘     └───────────┘ │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Sync Mode Comparison

| Aspect | Schedule-Based | Event-Based |
|--------|---------------|-------------|
| **Inbound Trigger** | Scheduled job polls/queries source | Webhook, notification, or push (e.g., SCIM) |
| **Inbound Processing** | Same code path | Same code path |
| **Pending Export Creation** | Same (immediate on MVO change) | Same (immediate on MVO change) |
| **Export Execution** | Scheduled batch job | Immediate after inbound completes |
| **Latency** | Minutes to hours | Seconds |
| **Use Case** | Bulk sync, batch processing | Real-time provisioning, SLA-driven |

### Implementation Approach

Because Q1 Option A separates *export evaluation* from *export execution*, supporting event-based sync becomes a configuration choice rather than an architectural change:

#### Phase 1: Add Export Execution Mode (Minimal)
```csharp
public enum ExportExecutionMode
{
    Scheduled,    // Pending exports processed by scheduled job
    Immediate,    // Pending exports processed immediately after creation
    Hybrid        // Per-system configuration
}
```

#### Phase 2: Per-System Configuration
```csharp
// On ConnectedSystem or SyncRule
public ExportExecutionMode ExportMode { get; set; }
```

#### Phase 3: Inbound Event Receivers
- Webhook endpoints for systems that push notifications
- SCIM 2.0 server endpoints (see separate design)
- Polling enhancement for near-real-time detection

### Work Estimate for Event-Based Support

| Task | Effort | Dependency |
|------|--------|------------|
| Add `ExportExecutionMode` enum and configuration | Small | Outbound sync MVP |
| Implement immediate export execution path | Medium | Outbound sync MVP |
| SCIM 2.0 server endpoints | Large | See SCIM design |
| Webhook receiver framework | Medium | None |
| Connector-specific notification handlers | Per-connector | Webhook framework |

### Benefits of This Architecture

1. **No Architectural Lock-in** - Schedule-based and event-based use the same core sync engine
2. **Gradual Migration** - Can run hybrid (some systems scheduled, others event-based)
3. **Performance** - Event-based naturally distributes load over time
4. **Simplicity** - Same code paths, different triggers

---

## Export Execution Strategy

### The Challenge: Reference Attributes and Dependencies

Q1 Option A (evaluate exports immediately) creates challenges when:
- **Reference attributes** point to MVOs that don't exist in the target system yet
- **Dependencies** between objects (manager->user, group->members) require sequencing
- **Parallel processing** means we can't guarantee ordering during inbound sync

**Key Insight**: Q1 Option A is about *when to evaluate and create Pending Exports*, not about *when/how to execute them*. This separation is our solution.

### Two Distinct Phases

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    EVALUATION vs EXECUTION                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  PHASE 1: EXPORT EVALUATION (Q1 Decision)                               │
│  ─────────────────────────────────────────                              │
│  ✓ Can be parallel                                                      │
│  ✓ No dependency concerns                                               │
│  ✓ Just creates PendingExport records                                   │
│  ✓ References stored as MVO IDs (not target system IDs)                 │
│                                                                         │
│  PHASE 2: EXPORT EXECUTION (Separate concern)                           │
│  ─────────────────────────────────────────────                          │
│  -> Dependency graph evaluation happens HERE                            │
│  -> Ordering/sequencing happens HERE                                    │
│  -> Reference resolution (MVO ID -> target system ID) happens HERE      │
│  -> Can be batched, ordered, multi-pass                                 │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Storing References as MVO IDs

When creating a `PendingExport`, reference attributes store **MVO IDs**, not target system identifiers:

```csharp
// PendingExport stores MVO reference, not AD DN
PendingExportAttributeValueChange {
    AttributeName: "manager",
    NewValue: "mvo:guid-of-bob"  // MVO reference, not "CN=Bob,OU=Users,DC=corp,DC=local"
}
```

This decouples export *evaluation* from export *execution*, allowing references to be resolved at execution time when all dependencies are known.

### Execution Strategy by Scenario

#### Scenario 1: Scheduled Batch Export (Multi-Pass)

For scheduled export runs processing many pending exports:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    SCHEDULED BATCH EXPORT                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Input: Collection of PendingExports for target system                  │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ PASS 1: Structural Changes (Parallel)                           │    │
│  │                                                                 │    │
│  │ • Create new objects (without reference attributes)             │    │
│  │ • Delete objects                                                │    │
│  │ • Update non-reference attributes                               │    │
│  │                                                                 │    │
│  │ Output: All objects exist in target system                      │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│                              │                                          │
│                              ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ PASS 2: Reference Resolution (Parallel)                         │    │
│  │                                                                 │    │
│  │ • Resolve MVO references -> Target system IDs (e.g., AD DN)     │    │
│  │ • Update manager attributes                                     │    │
│  │ • Update group memberships                                      │    │
│  │                                                                 │    │
│  │ Output: All references populated                                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

**Why Multi-Pass Works**:
- Pass 1 can be fully parallel (no dependencies between creates)
- Pass 2 can also be parallel (all objects exist after Pass 1)
- Matches how traditional ILM products work (multi-phase sync runs)
- Simple mental model: "create objects, then set references"

**Implementation**:

```csharp
public async Task ExecutePendingExportsAsync(ConnectedSystem targetSystem)
{
    var pendingExports = await GetPendingExportsAsync(targetSystem.Id);

    // Pass 1: Non-reference changes (parallel)
    var pass1Tasks = pendingExports
        .Where(e => e.ChangeType == Add || HasNonReferenceChanges(e))
        .Select(e => ExecuteNonReferenceChangesAsync(e));
    await Task.WhenAll(pass1Tasks);

    // Pass 2: Reference attributes (parallel, all objects now exist)
    var pass2Tasks = pendingExports
        .Where(e => HasReferenceChanges(e))
        .Select(e => ExecuteReferenceChangesAsync(e));
    await Task.WhenAll(pass2Tasks);
}
```

#### Scenario 2: Event-Based Single Object Export

For immediate export of a single object (e.g., after SCIM push):

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    EVENT-BASED SINGLE OBJECT                            │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  SCIM Push: Alice (manager=Bob)                                         │
│         │                                                               │
│         ▼                                                               │
│  Create MVO for Alice, manager = MVO:Bob                                │
│         │                                                               │
│         ▼                                                               │
│  Create PendingExport for Alice to AD                                   │
│  (manager stored as MVO ID)                                             │
│         │                                                               │
│         ▼                                                               │
│  Immediate Export Execution                                             │
│         │                                                               │
│    ┌────┴─────────────────────────────────────┐                         │
│    │                                          │                         │
│    ▼                                          ▼                         │
│  Bob EXISTS in AD                      Bob NOT in AD yet                │
│    │                                          │                         │
│    ▼                                          ▼                         │
│  Export Alice with                      Export Alice with               │
│  manager=Bob's DN                       manager=null                    │
│                                               │                         │
│                                               ▼                         │
│                                         Create DeferredReference        │
│                                         (Alice.manager -> Bob)          │
│                                               │                         │
│                                               ▼                         │
│                                         When Bob exported later,        │
│                                         update Alice.manager            │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

**Recommended Approach: Export with Null, Update Later**

```csharp
public async Task ExecuteImmediateExportAsync(PendingExport export)
{
    var unresolvedRefs = new List<DeferredReference>();

    foreach (var attr in export.AttributeChanges.Where(a => a.IsReference))
    {
        var targetCso = await FindCsoInTargetSystem(attr.MvoReferenceId, export.TargetSystemId);

        if (targetCso != null)
        {
            // Reference exists - resolve it
            attr.ResolvedValue = targetCso.ExternalId;
        }
        else
        {
            // Reference doesn't exist yet - defer it
            attr.ResolvedValue = null;
            unresolvedRefs.Add(new DeferredReference
            {
                SourceCsoId = export.CsoId,
                AttributeName = attr.Name,
                TargetMvoId = attr.MvoReferenceId
            });
        }
    }

    // Export object (with null for unresolved references)
    await connector.ExportAsync(export);

    // Track deferred references for later resolution
    await SaveDeferredReferencesAsync(unresolvedRefs);
}

// Called when an MVO is exported to a target system
public async Task ResolveDeferredReferencesAsync(Guid mvoId, ConnectedSystem targetSystem)
{
    var deferred = await GetDeferredReferencesAsync(mvoId, targetSystem.Id);

    foreach (var reference in deferred)
    {
        // Now that target exists, update the reference
        await UpdateReferenceAttributeAsync(reference);
        await MarkDeferredReferenceResolvedAsync(reference);
    }
}
```

**Why This Works**:
- User/object gets created immediately (main goal of event-based)
- Reference updated automatically when dependency is exported
- No blocking, no complex retry logic
- Matches real-world behaviour (AD allows creating user without manager)

### Reference Resolution

```csharp
public async Task<string> ResolveReferenceForTargetSystemAsync(
    Guid mvoId,
    ConnectedSystem targetSystem)
{
    // Find the CSO for this MVO in the target system
    var cso = await _repository.ConnectedSystemObjects
        .GetByMetaverseObjectIdAsync(mvoId, targetSystem.Id);

    if (cso == null)
        return null;  // Not yet exported to this system

    // Return target system's identifier (e.g., AD DN)
    return cso.ExternalId;
}
```

### Handling Groups: Object Type Ordering

Groups with members are complex because members must exist before being added:

**Option 1: Process Object Types in Dependency Order**

```csharp
// Configure object type processing order per-system
var objectTypeOrder = new[] { "User", "Group" };

foreach (var objectType in objectTypeOrder)
{
    var exports = pendingExports.Where(e => e.ObjectType == objectType);

    // Pass 1: Create objects
    await CreateObjectsAsync(exports);

    // Pass 2: Set references (including group members)
    await SetReferencesAsync(exports);
}
```

**Option 2: Separate Group Membership Pass**

```
Pass 1: Create users (no references)
Pass 2: Create groups (no members)
Pass 3: Set user references (manager)
Pass 4: Add group members
```

### Summary: Dependency Handling by Scenario

| Scenario | Who Handles Ordering | Reference Resolution | Approach |
|----------|---------------------|---------------------|----------|
| **Scheduled Batch** | JIM (multi-pass) | Pass 1: objects, Pass 2: references | Multi-pass parallel |
| **Event-Based Single** | JIM (deferred) | Export now, update reference when available | Deferred references |
| **SCIM Single Object** | Client (Okta, etc.) | Client creates dependencies first | Return 400 if missing |
| **SCIM Bulk Request** | JIM (topological sort) | Resolve bulkId refs within batch | Topological sort |

### Data Model Additions

To support deferred references:

```csharp
public class DeferredReference
{
    public Guid Id { get; set; }
    public Guid SourceCsoId { get; set; }           // CSO that has the reference
    public string AttributeName { get; set; }        // e.g., "manager"
    public Guid TargetMvoId { get; set; }           // MVO being referenced
    public Guid TargetSystemId { get; set; }        // System where ref needs resolving
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int RetryCount { get; set; }
}
```

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
│                    PENDING EXPORT LIFECYCLE                     │
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
┌─────────┐
│Confirmed│ -> Deleted
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
- We need to resolve MVO reference -> CSO DN
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
Legacy ILM tools often require complex XML and code for sync rules. JIM could offer:
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

## Implementation Plan

Based on the design decisions above, this is the implementation plan for outbound sync MVP.

### Phase 1: Core Models & Infrastructure

**Goal**: Set up the data model foundations for outbound sync.

- [x] **1.1 Add enums to `PendingExportEnums.cs`**
  - `PendingExportStatus`: Pending, Executing, Failed, Exported
  - `SyncRunMode`: PreviewOnly, PreviewAndSync

- [x] **1.2 Enhance `PendingExport.cs`**
  - Add retry tracking: `MaxRetries`, `LastAttemptedAt`, `NextRetryAt`, `LastErrorMessage`
  - Add MVO reference: `SourceMetaverseObjectId`
  - Add flag: `HasUnresolvedReferences`

- [x] **1.3 Create `DeferredReference.cs`** (new model)
  - Tracks references that couldn't be resolved during export
  - Fields: `SourceCsoId`, `AttributeName`, `TargetMvoId`, `TargetSystemId`, `CreatedAt`, `ResolvedAt`

- [ ] **1.4 Create `SyncPreviewResult.cs`** (new model) - Issue #288
  - Holds comprehensive sync preview results for Q5 Preview mode
  - Inbound: `ProjectedMvo`, `JoinedMvo`, `AttributeFlows`
  - Outbound: `ProvisionedCsos`, `UpdatedCsos`, `DeletedCsos`, `PendingExports`
  - Metadata: `Warnings`, `Errors`, `AffectedSyncRules`

- [x] **1.5 Update DbContext and create migration**
  - Add `DbSet<DeferredReference>`
  - Update PendingExport entity configuration
  - Run: `dotnet ef migrations add AddOutboundSyncModels --project JIM.PostgresData`

### Phase 2: Export Evaluation

**Goal**: Implement Q1 decision - create PendingExports immediately when MVO changes.

- [x] **2.1 Create `ExportEvaluationServer.cs`** (new service in `JIM.Application/Servers/`)
  - `EvaluateExportRulesAsync(mvo, changedAttributes)` - main entry point
  - `IsMvoInScopeForExportRule(mvo, exportRule)` - scope checking (via `ScopingEvaluationServer`)
  - `EvaluateAttributeForExport(...)` - implements Q3 circular sync prevention
  - `EvaluateMvoDeletionAsync(mvo)` - implements Q4 (only Provisioned CSOs)
  - `CreateProvisioningExport(...)` - implements Q2 (sets JoinType.Provisioned)

- [x] **2.2 Create `ScopingCriteriaEvaluator.cs`** (utility in `JIM.Worker/Processors/`)
  - Implemented as `ScopingEvaluationServer.cs` in `JIM.Application/Servers/`
  - Evaluate if MVO matches sync rule scoping criteria groups
  - Handle AND/OR logic for criteria groups

- [x] **2.3 Create `OutboundSyncRuleMappingProcessor.cs`** (new processor)
  - Implemented within `ExportEvaluationServer.cs` - maps MVO attributes -> CSO attributes
  - Maps MVO attributes -> CSO attributes based on sync rule mappings

- [x] **2.4 Hook into `SyncFullSyncTaskProcessor.cs`**
  - Implemented in `SyncTaskProcessorBase.cs` calling `EvaluateExportRulesWithNoNetChangeDetectionAsync()`
  - Pass the source system to enable Q3 circular sync prevention

### Phase 3: Export Execution

**Goal**: Process PendingExports via connectors using two-pass approach.

- [x] **3.1 Create `ExportExecutionServer.cs`** (new service in `JIM.Application/Servers/`)
  - `ExecuteExportsAsync(targetSystem, connector, runMode)` - main entry point
  - Two-pass approach: immediate exports first, then deferred references
  - `TryResolveReferencesAsync(...)` - MVO ID -> target system ID resolution
  - Reference resolution prefers secondary external ID (DN) for LDAP systems

- [x] **3.2 Create `SyncExportTaskProcessor.cs`** (new processor in `JIM.Worker/Processors/`)
  - Process Export run profile type
  - Supports SyncRunMode (PreviewOnly, PreviewAndSync)
  - Call connector export methods

- [x] **3.3 Add repository methods**
  - `IConnectedSystemRepository`: `CreatePendingExportAsync`, `GetPendingExportsAsync`, `UpdatePendingExportAsync`
  - Batch operations: `UpdatePendingExportsAsync`, `DeletePendingExportsAsync`
  - Implemented in `PostgresData/Repositories/ConnectedSystemRepository.cs`

### Phase 4: Preview Mode

**Goal**: Implement Q5 decision - Preview Only and Preview + Sync modes.

- [ ] **4.1 Create `SyncPreviewServer.cs`** (new service) - Issue #288
  - `PreviewSyncForCsoAsync(cso)` - preview full sync chain for a CSO (inbound + outbound)
  - `PreviewSyncForMvoAsync(mvo)` - preview outbound sync from an MVO
  - `PreviewFullSyncAsync(system)` - preview what a full sync run would produce
  - Shows complete dependency graph: CSO -> MVO -> Export Rules -> Target CSOs

- [x] **4.2 Add `SyncRunMode` parameter to sync processors**
  - Added to `SyncExportTaskProcessor` constructor
  - `ExportExecutionServer.ExecuteExportsAsync` supports `SyncRunMode.PreviewOnly`

### Phase 5: Error Handling & Retry

**Goal**: Implement Q6 decision - retry with backoff, then manual intervention.

- [x] **5.1 Create `ExportRetryService.cs`** (new service)
  - Implemented directly in `ExportExecutionServer.cs` instead of separate service
  - `MarkExportFailedAsync(pendingExport, errorMessage)` - handles retry logic
  - `CalculateNextRetryTime(errorCount)` - exponential backoff (2^n minutes, max 60)
  - `GetFailedExportsCountAsync()` - for admin dashboard
  - `RetryFailedExportsAsync()` - manual retry (resets error count)

- [x] **5.2 Update export processor with retry wrapper**
  - Try/catch around export execution in `ExecuteExportsViaConnectorAsync`
  - Automatic retry with backoff, marks as Failed after max retries

### Phase 6: Wire Up & Test

**Goal**: Register services and write comprehensive tests.

- [x] **6.1 Update `JimApplication.cs`**
  - Added `ExportEvaluation`, `ExportExecution` servers
  - Preview and Retry functionality integrated into `ExportExecutionServer`

- [x] **6.2 Write unit tests**
  - `ExportEvaluationTests.cs` - export rule evaluation, scoping, Q3 circular prevention
  - `ExportExecutionTests.cs` - two-pass execution, reference resolution, deferred refs
  - `ScopingEvaluationTests.cs` - AND/OR logic, comparison types
  - Retry logic tested within `ExportExecutionTests.cs`

### Implementation Dependencies

```
Phase 1 (Models) ──────────────────────────────────────────┐
                                                           │
Phase 2 (Evaluation) ──────────────────────────────────────┤
     │                                                     │
     ▼                                                     │
Phase 3 (Execution) ───────────────────────────────────────┤
     │                                                     │
     ├──────────────┐                                      │
     ▼              ▼                                      ▼
Phase 4         Phase 5                              Phase 6
(Preview)       (Retry)                              (Wire Up)
```

### Key Files Reference

| File | Location | Purpose | Status |
|------|----------|---------|--------|
| `ExportEvaluationServer.cs` | `JIM.Application/Servers/` | Evaluates export rules, creates PendingExports | ✅ Implemented |
| `ExportExecutionServer.cs` | `JIM.Application/Servers/` | Executes exports via connectors (includes retry logic) | ✅ Implemented |
| `SyncPreviewServer.cs` | `JIM.Application/Servers/` | Generates full sync previews (CSO->MVO->exports) | ❌ Not implemented (Issue #288) |
| `SyncExportTaskProcessor.cs` | `JIM.Worker/Processors/` | Processes Export run profile | ✅ Implemented |
| `ScopingEvaluationServer.cs` | `JIM.Application/Servers/` | Evaluates scoping criteria (AND/OR logic) | ✅ Implemented |
| `DeferredReference.cs` | `JIM.Models/Transactional/` | Tracks unresolved references | ✅ Implemented |
| `SyncPreviewResult.cs` | `JIM.Models/Transactional/` | Full sync preview results container | ❌ Not implemented (Issue #288) |
| `ExportExecutionResult.cs` | `JIM.Models/Transactional/` | Export execution result with stats | ✅ Implemented |

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
- Issue #288: Sync Preview Mode (What-If Analysis)
- Existing code: `SyncFullSyncTaskProcessor`, `PendingExport` model

### Related Design Documents

- [DRIFT_DETECTION_AND_ATTRIBUTE_PRIORITY.md](DRIFT_DETECTION_AND_ATTRIBUTE_PRIORITY.md) - Extends this design with drift detection (re-evaluating exports on inbound sync) and attribute priority (determining authoritative source for multi-contributor scenarios)
