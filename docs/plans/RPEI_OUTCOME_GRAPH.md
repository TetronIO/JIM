# RPEI Outcome Graph: Causal Chain Tracking Per Object

- **Status:** Planned
- **Milestone:** Post-MVP
- **Created:** 2026-03-01
- **Issue:** #363
- **Related Issues:** #288 (Sync Preview Mode)

## Overview

Restructure Run Profile Execution Items (RPEIs) so that each RPEI records a structured graph of **causal outcomes** — the full chain of consequences that resulted from processing a single Connected System Object. Today, RPEIs are flat records with a single `ObjectChangeType`. This design replaces that with a tree of `SyncOutcome` nodes that tells the complete story: "this CSO was projected, which caused attribute flow of 12 attributes, which caused provisioning into AD and LDAP."

This gives administrators immediate visibility into what happened and why, from a single row in the activity detail view.

## Business Value

- **Full story per object**: Click into any RPEI and see the complete causal chain — no cross-referencing between activities
- **At-a-glance list view**: Stat chips on each row show outcomes (Projected, Attribute Flow, Provisioned ×2) without drilling in
- **Accurate statistics**: Aggregate counts derived from outcome types across all trees (e.g., total provisions = sum of provisioning outcomes across all RPEIs, spanning target systems)
- **Sync Preview foundation**: The outcome graph model is the "what actually happened" counterpart to the Sync Preview "what would happen" model (#288) — same data structure, shared logic
- **Configurable granularity**: Administrators control how much detail is stored, balancing audit depth against storage and performance

## Current State

### RPEI Model Today

Each `ActivityRunProfileExecutionItem` is a flat record:
- One per CSO per activity
- Single `ObjectChangeType` enum (Added, Joined, Projected, AttributeFlow, Provisioned, etc.)
- Optional `AttributeFlowCount` for absorbed flows
- Optional navigation to `ConnectedSystemObjectChange` / `MetaverseObjectChange` for attribute-level detail
- `ExternalIdSnapshot` for identity preservation after CSO deletion

### Problems

1. **No causal chain**: A projection that triggers attribute flow and provisioning into 3 systems produces a single RPEI with `ObjectChangeType.Projected`. The downstream consequences are invisible unless you look at separate export activity RPEIs
2. **Stats are type-count-based**: Statistics count RPEIs by `ObjectChangeType`, which conflates "how many objects" with "what happened to each object"
3. **Cross-activity blindness**: Import, sync, and export are separate activities — there is no single view showing the full impact of processing one object

## Design

### Approach: Per-Activity Causal Graphs

Each activity type (import, sync, export) records its own causal graph within its scope. The sync RPEI captures the richest graph because sync orchestrates join/project, attribute flow, and export evaluation. Import and export RPEIs capture their own consequence chains independently.

This avoids cross-activity writes (which would complicate concurrency and the bulk-insert model) while still giving each activity a complete story within its operational scope.

### New Entity: `SyncOutcome`

New table with self-referential parent/child FK for tree structure:

```csharp
public class SyncOutcome
{
    [Key]
    public Guid Id { get; set; }

    // FK to parent RPEI
    public Guid ActivityRunProfileExecutionItemId { get; set; }
    public ActivityRunProfileExecutionItem ActivityRunProfileExecutionItem { get; set; }

    // Self-referential tree (null = root outcome)
    public Guid? ParentSyncOutcomeId { get; set; }
    public SyncOutcome? ParentSyncOutcome { get; set; }
    public List<SyncOutcome> Children { get; set; } = [];

    // What happened
    public SyncOutcomeType OutcomeType { get; set; }

    // Target entity context (MVO ID, target CSO ID, connected system ID, etc.)
    public Guid? TargetEntityId { get; set; }

    // Snapshot description for display without joins (e.g., connected system name, MVO display name)
    public string? TargetEntityDescription { get; set; }

    // Quantitative detail (e.g., "12 attributes flowed", "3 attributes exported")
    public int? DetailCount { get; set; }

    // Optional context message
    public string? DetailMessage { get; set; }

    // Ordering among siblings in the tree
    public int Ordinal { get; set; }
}
```

### New Enum: `SyncOutcomeType`

Covers all three run profile types:

```csharp
public enum SyncOutcomeType
{
    // Import outcomes
    CsoAdded,
    CsoUpdated,
    CsoDeleted,

    // Sync outcomes — inbound
    Projected,
    Joined,
    AttributeFlow,
    Disconnected,
    DisconnectedOutOfScope,
    MvoDeleted,

    // Sync outcomes — outbound (pending export creation during sync)
    PendingExportCreated,

    // Export execution outcomes
    Provisioned,
    Exported,
    Deprovisioned,
    ExportConfirmed,
    ExportFailed
}
```

### Example Outcome Trees

**Sync: New employee imported and provisioned**

```
RPEI: CSO "EMP001" (HR System)
  |
  +-- [Projected] MVO "John Smith" created
        |
        +-- [AttributeFlow] 12 attributes flowed to MVO
              |
              +-- [PendingExportCreated] AD — new CSO to provision
              +-- [PendingExportCreated] LDAP — new CSO to provision
```

**Sync: Employee deleted from source**

```
RPEI: CSO "EMP002" (HR System)
  |
  +-- [Disconnected] CSO disconnected from MVO "Jane Doe"
        |
        +-- [AttributeFlow] 8 attributes recalled from MVO
        +-- [MvoDeleted] MVO deleted (last connector rule)
              |
              +-- [PendingExportCreated] AD — CSO to deprovision
              +-- [PendingExportCreated] LDAP — CSO to deprovision
```

**Export: Pending exports executed**

```
RPEI: CSO "CN=jsmith,OU=Staff,DC=ad,DC=local" (Active Directory)
  |
  +-- [Provisioned] CSO created in AD
        |
        +-- [Exported] 8 attributes written
```

**Import: New objects from source**

```
RPEI: CSO "EMP001" (HR System)
  |
  +-- [CsoAdded] CSO created in staging
        |
        +-- [CsoUpdated] 15 attributes set
```

### Synergy with Sync Preview (#288)

The `SyncOutcome` model serves both purposes:

- **Actual sync**: Build the outcome tree during processing, persist it against the RPEI
- **Sync Preview**: Build the same tree speculatively (without persisting), return it for display

The `SyncPreviewServer` from #288 and the sync processors share the same outcome model and tree-building logic. The difference is whether outcomes are committed to the database or returned as a preview result. This means implementing the outcome graph is a prerequisite for — and directly enables — sync preview functionality.

```
SyncPreviewResult
  └── List<SyncOutcome>   <-- same model, built speculatively
        ├── Projected → MVO
        │     └── AttributeFlow → 12 attributes
        │           ├── PendingExportCreated → AD
        │           └── PendingExportCreated → LDAP
        └── (etc.)
```

### Granularity Control: Service Setting

Following the existing `ChangeTracking.CsoChanges.Enabled` / `ChangeTracking.MvoChanges.Enabled` pattern:

```
Key: "ChangeTracking.SyncOutcomes.Level"
Category: History
ValueType: Enum
EnumTypeName: "SyncOutcomeTrackingLevel"
DefaultValue: "Detailed"
Description: "Controls how much detail is recorded for sync outcome
              graphs on each run profile execution item. Higher levels
              provide richer audit trails but increase storage usage."
```

#### Tracking Levels

| Level | What's Recorded | Use Case |
|-------|----------------|----------|
| **None** | No outcome tree — RPEI `ObjectChangeType` only (legacy behaviour) | Maximum performance, minimal storage |
| **Standard** | Root-level outcomes only (Projected, Joined, Provisioned, etc.) — no nested children | Stat chips on list view, basic causal visibility |
| **Detailed** | Full tree with nested children (Projected → AttributeFlow → PendingExportCreated per system) | Default. Full audit trail, debugging, compliance |

**Detailed** is the default — it provides the complete causal chain needed for debugging, audit, and compliance. **Standard** can be used in high-volume environments where storage is a concern, and **None** preserves legacy behaviour with zero overhead.

This setting complements the existing CSO/MVO change tracking settings. An administrator might enable Detailed outcome tracking but disable CSO change tracking if they care about "what happened" but not "what the attributes looked like before/after."

### RPEI Changes

#### ObjectChangeType Evolution

The RPEI's `ObjectChangeType` field remains as the "primary action" — the root of the tree. It continues to work for list views and backward compatibility. The outcome graph enriches this with the full causal chain.

At **None** tracking level, the system behaves exactly as today. At **Standard** or **Detailed**, the outcome tree provides richer information.

#### List View Denormalisation

For the activity detail table, each RPEI row should show small stat chips for its root-level outcomes (e.g., `[Projected] [Attr Flow: 12] [Provisioned ×2]`). To avoid joining to the outcomes table on every paginated query:

**Option A — Denormalised summary field**: Store a bitmask or compact representation directly on the RPEI (e.g., `OutcomeSummary` column). Maintained during sync when outcomes are built. Keeps the list query fast.

**Option B — Eager load on query**: Join to outcomes table with a lightweight projection. Simpler to maintain but adds query cost per page.

**Recommendation**: Option A. The list view is the most frequently accessed view. A compact summary field avoids the join entirely. The full outcome tree is only loaded when drilling into a single RPEI's detail page.

Possible `OutcomeSummary` representation:
- Comma-separated outcome types with counts, e.g., `"Projected:1,AttributeFlow:12,PendingExportCreated:2"`
- Parsed client-side for chip rendering
- Populated during outcome tree construction — no separate maintenance path

### Activity Statistics Changes

Statistics shift from "count of RPEIs by ObjectChangeType" to "count of outcome nodes by SyncOutcomeType across all RPEIs for this activity."

The query becomes:

```sql
SELECT OutcomeType, COUNT(*)
FROM SyncOutcomes
WHERE ActivityRunProfileExecutionItemId IN (
    SELECT Id FROM ActivityRunProfileExecutionItems WHERE ActivityId = @activityId
)
GROUP BY OutcomeType
```

Or equivalently via join.

**Key semantic change for provisions**: If 10 objects are each provisioned into 2 connected systems, the "Provisioned" stat shows **20** — the total number of provisioning actions across target systems. This is the meaningful number for operators ("how many CSOs were created in target systems").

The current `TotalObjectsProcessed` / `TotalObjectChangeCount` concepts remain. Individual outcome-type totals replace the current `ObjectChangeType`-based counts.

#### Activity Summary Stats (denormalised on Activity)

The `Activity` model's denormalised stat fields (e.g., `TotalProjected`, `TotalJoined`) remain for list view performance. They are computed from outcome trees at activity completion, same as today, but counting outcome nodes rather than RPEIs.

### UI Changes

#### Activity Detail List View

Each RPEI row shows:
- External ID, Display Name, Object Type (as today)
- Primary `ObjectChangeType` (as today)
- **New**: Outcome stat chips derived from `OutcomeSummary` — e.g., `[Projected] [Attr Flow: 12] [Provisioned ×2]`
- Error indicator (as today)

#### Activity Detail Filter Controls

Filter controls allow filtering by outcome types present in RPEIs:

```sql
WHERE EXISTS (
    SELECT 1 FROM SyncOutcomes
    WHERE ActivityRunProfileExecutionItemId = rpei.Id
    AND OutcomeType = @filterType
)
```

Or, if using the denormalised `OutcomeSummary`, filter via string matching on the summary field. This is simpler and avoids the subquery.

#### RPEI Detail Page

Clicking into an RPEI shows the outcome tree rendered as an indented list or tree view:

```
CSO: EMP001 (HR System)
  +-- Projected to MVO "John Smith"
  |     +-- Attribute Flow: 12 attributes to MVO
  |           +-- Pending Export: AD — new CSO to provision
  |           +-- Pending Export: LDAP — new CSO to provision
```

At **None** tracking level, this page shows only the RPEI's `ObjectChangeType` and existing `ConnectedSystemObjectChange` / `MetaverseObjectChange` detail (current behaviour).

At **Standard** level, the tree shows root-level outcomes only (one level deep).

At **Detailed** level, the full nested tree is displayed.

### Existing Change Detail Models

The existing `ConnectedSystemObjectChange` and `MetaverseObjectChange` navigation properties on RPEIs serve a different purpose — they carry **attribute-level detail** (which specific attributes changed, old/new values). The outcome graph is about the **structural causal chain** (what operations occurred and what they triggered).

Both coexist:
- **SyncOutcome tree**: "What happened and what it caused" — structural story
- **CSOChange / MVOChange**: "What the attribute values looked like before/after" — debugging detail

## Processor Changes

### Import Processor

Today creates 1 RPEI with `ObjectChangeType.Add/Update/Delete`. With outcome tracking:

- **Standard**: Add root outcome (e.g., `CsoAdded` with `DetailCount` = attribute count)
- **Detailed**: Add root + child outcomes (e.g., `CsoAdded` → `CsoUpdated` with attribute detail)

### Sync Processor

The richest graph. The processor already tracks decisions through `MetaverseObjectChangeResult` and runs export evaluation inline. Changes:

1. After join/project decision: create root outcome node
2. After attribute flow: create child outcome under the join/project node
3. After export evaluation: for each target system with pending exports, create child outcome under the attribute flow node
4. Build tree in memory during CSO processing, attach to RPEI before flush

### Export Processor

Creates outcome nodes for export execution results:
- Root: `Provisioned` / `Exported` / `Deprovisioned`
- Children (Detailed only): attribute-level export detail

## Database Migration

New table `SyncOutcomes` with:
- `Id` (PK)
- `ActivityRunProfileExecutionItemId` (FK → `ActivityRunProfileExecutionItems`, CASCADE DELETE)
- `ParentSyncOutcomeId` (FK → self, SET NULL)
- `OutcomeType` (int, enum)
- `TargetEntityId` (nullable Guid)
- `TargetEntityDescription` (nullable string)
- `DetailCount` (nullable int)
- `DetailMessage` (nullable string)
- `Ordinal` (int)

New column on `ActivityRunProfileExecutionItems`:
- `OutcomeSummary` (nullable string) — denormalised summary for list view

Indexes:
- `IX_SyncOutcomes_ActivityRunProfileExecutionItemId` — for loading outcomes by RPEI
- `IX_SyncOutcomes_OutcomeType` — for aggregate stats queries (composite with RPEI FK)

Existing RPEIs have no outcomes (empty list) — gracefully handled with no data migration.

CASCADE DELETE on the RPEI FK ensures outcomes are cleaned up automatically when activities are purged by housekeeping.

## Bulk Insert Extension

The existing chunked raw SQL bulk insert pattern (`BulkInsertRpeisAsync`) is extended with a second INSERT statement for outcomes:

1. RPEI IDs are pre-generated (already the case)
2. Outcome IDs are pre-generated
3. Both INSERT statements execute in the same transaction
4. Same chunking logic applies (PostgreSQL 65K parameter limit)
5. EF fallback for unit tests (same pattern as RPEI fallback)

Outcomes reference pre-generated RPEI IDs, so both inserts can be in the same flush cycle.

## Performance Considerations

| Concern | Assessment |
|---------|-----------|
| **Write volume** | Detailed (default): ~5-10 outcome rows per RPEI. 100k objects → ~500k-1M outcome rows. Standard: ~3-5 per RPEI. Manageable with existing chunked bulk insert |
| **Storage** | Each outcome row is small (~100-200 bytes: GUIDs, enum, int, short string). Detailed level for 100k objects ≈ 50-100 MB per sync run |
| **List query** | No join needed — `OutcomeSummary` denormalised field. Same performance as today |
| **Detail query** | Single RPEI + its outcomes via indexed FK. Small result set, fast |
| **Stats query** | `GROUP BY OutcomeType` on outcomes table with FK filter. Single indexed query |
| **Filtering** | `OutcomeSummary` string matching (if denormalised) or `EXISTS` subquery (if not). Both indexed |
| **None level** | Zero overhead — no outcomes created, no summary field populated. Exact current behaviour |
| **Memory during sync** | Outcome trees built in memory per CSO, flushed per page with RPEIs. No unbounded accumulation |

## Service Setting Seeding

Add to `SeedingServer.cs`:

```csharp
new ServiceSetting
{
    Key = Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel,
    DisplayName = "Sync Outcome Tracking Level",
    Description = "Controls how much detail is recorded for sync outcome graphs. " +
                  "None: no outcome tracking (legacy). Standard: root-level outcomes " +
                  "(default, enables stat chips). Detailed: full causal chain with " +
                  "nested outcomes.",
    Category = ServiceSettingCategory.History,
    ValueType = ServiceSettingValueType.Enum,
    EnumTypeName = nameof(SyncOutcomeTrackingLevel),
    DefaultValue = nameof(SyncOutcomeTrackingLevel.Detailed),
    IsReadOnly = false
}
```

## Implementation Phases

### Phase 1: Data Model & Infrastructure

1. Create `SyncOutcome` entity and `SyncOutcomeType` enum
2. Create `SyncOutcomeTrackingLevel` enum
3. Add `OutcomeSummary` column to `ActivityRunProfileExecutionItem`
4. Create EF configuration and database migration
5. Add service setting key constant and seeding
6. Add `ServiceSettingsServer.GetSyncOutcomeTrackingLevelAsync()` method
7. Extend `BulkInsertRpeisAsync` to bulk insert outcomes
8. Write unit tests for bulk insert, model, and setting

### Phase 2: Sync Processor Integration

1. Add outcome tree building to `SyncTaskProcessorBase` (join/project/attribute flow)
2. Add export evaluation outcomes (pending export creation per target system)
3. Add disconnection/deletion outcome chains
4. Build `OutcomeSummary` string during tree construction
5. Respect tracking level setting (None/Standard/Detailed)
6. Write unit tests for outcome tree construction at each level

### Phase 3: Import & Export Processor Integration

1. Add outcome tree building to `SyncImportTaskProcessor`
2. Add outcome tree building to `SyncExportTaskProcessor`
3. Respect tracking level setting
4. Write unit tests

### Phase 4: Statistics & API

1. Update `ActivityRunProfileExecutionStats` to derive from outcome types
2. Update `GetActivityRunProfileExecutionStatsAsync` query
3. Update `Worker.CalculateActivitySummaryStats` for denormalised activity fields
4. Update API DTOs and endpoints to include outcome summary
5. Write unit tests for stats calculation

### Phase 5: UI — List View & Filters

1. Add outcome stat chips to Activity Detail table rows (from `OutcomeSummary`)
2. Update filter controls to support outcome type filtering
3. Update Activity Detail stats display
4. Update Operations History view

### Phase 6: UI — RPEI Detail Page

1. Create outcome tree view component for RPEI detail page
2. Render tree at appropriate depth based on tracking level
3. Handle graceful display when no outcomes exist (None level / legacy RPEIs)

## Files to Modify

| File | Changes |
|------|---------|
| `src/JIM.Models/Activities/SyncOutcome.cs` | **New** — entity model |
| `src/JIM.Models/Enums/SyncOutcomeType.cs` | **New** — outcome type enum |
| `src/JIM.Models/Enums/SyncOutcomeTrackingLevel.cs` | **New** — tracking level enum |
| `src/JIM.Models/Activities/ActivityRunProfileExecutionItem.cs` | Add `OutcomeSummary` property and `SyncOutcomes` navigation |
| `src/JIM.Models/Core/Constants.cs` | Add setting key constant |
| `src/JIM.PostgresData/JimDbContext.cs` | Add `SyncOutcomes` DbSet and EF configuration |
| `src/JIM.PostgresData/Migrations/...` | New migration for SyncOutcomes table and OutcomeSummary column |
| `src/JIM.PostgresData/Repositories/ActivitiesRepository.cs` | Extend bulk insert, update stats query |
| `src/JIM.Application/Servers/SeedingServer.cs` | Add service setting seed |
| `src/JIM.Application/Servers/ServiceSettingsServer.cs` | Add `GetSyncOutcomeTrackingLevelAsync()` |
| `src/JIM.Application/Servers/ActivityServer.cs` | Update stats model and queries |
| `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` | Build outcome trees during sync |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Build outcome trees during import |
| `src/JIM.Worker/Processors/SyncExportTaskProcessor.cs` | Build outcome trees during export |
| `src/JIM.Worker/Worker.cs` | Update `CalculateActivitySummaryStats` |
| `src/JIM.Web/Pages/ActivityDetail.razor` | Stat chips, filter controls, stats display |
| `src/JIM.Web/Pages/ActivityItemDetail.razor` | Outcome tree view |
| `src/JIM.Web/Controllers/Api/ActivitiesController.cs` | API updates for outcome data |
| `test/JIM.Worker.Tests/...` | Tests for outcome tree building, bulk insert, stats |

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Write volume increase (3-10x more rows) | Tracking level setting allows admins to choose None for maximum performance. Standard limits to root-level outcomes only |
| Bulk insert complexity | Same proven chunking pattern as RPEI bulk insert. Second INSERT in same transaction |
| Migration on large databases | New table only — no data migration. ALTER TABLE for OutcomeSummary column is lightweight |
| Sync processor complexity | Outcome tree building can be encapsulated in a helper class, keeping processor logic clean |
| Backward compatibility | None tracking level preserves exact current behaviour. Legacy RPEIs with no outcomes display gracefully |
| Stats query performance | Indexed on RPEI FK + OutcomeType. For large activities, the GROUP BY is efficient |

## Success Criteria

1. Each RPEI records a structured causal chain of outcomes at the configured tracking level
2. Activity detail list view shows outcome stat chips per row
3. RPEI detail page renders the outcome tree
4. Activity statistics are derived from outcome types
5. Filter controls support filtering by outcome type
6. Service setting allows administrators to control tracking granularity
7. Sync Preview (#288) can reuse the `SyncOutcome` model for speculative results
8. No performance regression at None tracking level
9. All existing tests continue to pass
10. New tests cover outcome tree building, persistence, stats, and tracking levels

## Dependencies

- No new NuGet packages required
- No new Docker containers required
- Requires database migration (new table + column)
- Foundation for Sync Preview Mode (#288)
