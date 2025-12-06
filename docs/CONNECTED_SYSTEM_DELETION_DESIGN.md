# Connected System Deletion Design

> **Status**: Draft
> **Created**: 2025-12-06
> **GitHub Issue**: #135
> **Related**: #134 (Attribute Impact Analysis - future enhancement)

## Problem Statement

Administrators need the ability to delete a Connected System. This is currently impossible, blocking development and cleanup workflows. The key concerns are:

1. **Dependency graph complexity** - Many entities depend on a Connected System
2. **MVO synchronisation impact** - Deleting CSOs may trigger MVO deletion rules
3. **Performance** - MIM 2016 was notoriously slow at clearing connector spaces

## Dependency Graph

When deleting a Connected System, these entities must be handled:

```
ConnectedSystem
├── ConnectedSystemObject (CSOs) [many]
│   ├── ConnectedSystemObjectAttributeValue [cascade]
│   ├── ConnectedSystemObjectChange [audit records]
│   └── ActivityRunProfileExecutionItem [references]
├── ConnectedSystemObjectType [schema]
│   ├── ConnectedSystemObjectTypeAttribute [cascade]
│   └── SyncRule.ConnectedSystemObjectTypeId [references]
├── ConnectedSystemPartition
│   └── ConnectedSystemContainer [cascade]
├── ConnectedSystemRunProfile
├── ConnectedSystemSettingValue [cascade]
├── SyncRule [references this system]
│   ├── SyncRuleMapping [cascade]
│   ├── SyncRuleMappingSource [cascade]
│   └── SyncRuleScopingCriteriaGroup [cascade]
├── PendingExport
├── Activity [references, nullable FK]
└── WorkerTask (Sync/Clear tasks)
```

## Key Design Decisions

### Q1: What happens to MVOs when their CSOs are deleted?

**Options:**

| Option | Description | Pros | Cons |
|--------|-------------|------|------|
| A. Hard delete, ignore MVOs | Delete CSOs without evaluating MVO deletion rules | Fast, simple | MVOs may become orphaned; breaks `WhenLastConnectorDisconnected` rule |
| B. Evaluate MVO rules first | For each joined CSO, trigger deletion rule evaluation | Correct behaviour | Slow for large systems; may trigger cascading sync operations |
| C. Admin chooses | Provide checkbox for "Evaluate MVO deletion rules" | Flexible | More complex UI; admin may not understand implications |
| **D. Disconnect first, delete second** | Clear `MetaverseObjectId` on all CSOs first, then bulk delete | Maintains referential integrity; allows MVO rules to be evaluated in background | Requires two-phase operation |

**Recommendation**: Option D - This gives us the best of both worlds:
- Bulk disconnect operation using raw SQL (fast)
- MVO deletion rules can be evaluated in a background job if needed
- No cascading sync operations during delete
- Admin can choose whether to queue MVO cleanup job

### Q2: How do we handle Sync Rules?

**Options:**

| Option | Description |
|--------|-------------|
| A. Block deletion if sync rules exist | Require admin to delete sync rules first |
| B. Cascade delete sync rules | Automatically delete all sync rules for this system |
| C. Disable sync rules | Mark sync rules as disabled rather than deleting |

**Recommendation**: Option B with confirmation - Cascade delete sync rules. They're useless without the system anyway. Show count in confirmation dialog.

### Q3: How do we handle Activities?

Activities have a nullable `ConnectedSystemId` FK. Options:

| Option | Description |
|--------|-------------|
| A. Delete activities | Delete all activities for this system |
| B. Null the FK | Set `ConnectedSystemId = NULL`, preserving history |
| C. Block if recent activities | Only allow deletion if no activities in last N days |

**Recommendation**: Option B - Preserve activity history by nulling the FK. Activities still contain useful audit information and can reference `TargetName` for the deleted system name.

### Q4: Should deletion be synchronous or async?

**Options:**

| Option | Description | Use Case |
|--------|-------------|----------|
| A. Synchronous | Delete inline, block UI until complete | Small systems (<1000 CSOs) |
| B. Async only | Always queue as background job | Large systems |
| C. Auto-detect | Sync for small, async for large | Best UX |

**Recommendation**: Option C - Check CSO count:
- < 1,000 CSOs: Synchronous delete with progress indicator
- >= 1,000 CSOs: Queue as background job, show in activities

## Proposed Implementation

### Phase 0: Deletion Preview

Before deletion, provide an optional (but default) preview showing the impact:

```csharp
public class ConnectedSystemDeletionPreview
{
    public int ConnectedSystemId { get; set; }
    public string ConnectedSystemName { get; set; } = null!;

    // Object counts
    public int ConnectedSystemObjectCount { get; set; }
    public int SyncRuleCount { get; set; }
    public int RunProfileCount { get; set; }
    public int PartitionCount { get; set; }
    public int ContainerCount { get; set; }
    public int PendingExportCount { get; set; }
    public int ActivityCount { get; set; }

    // MVO Impact
    public int JoinedMvoCount { get; set; }
    public int MvosWithDeletionRuleCount { get; set; }  // MVOs that may be deleted
    public int MvosWithGracePeriodCount { get; set; }   // MVOs that will be scheduled for deletion
    public int MvosWithOtherConnectorsCount { get; set; } // MVOs with other CSO connections (won't be deleted)

    // Warnings
    public List<string> Warnings { get; set; } = new();

    // Estimated time
    public TimeSpan EstimatedDeletionTime { get; set; }
    public bool WillRunAsBackgroundJob { get; set; }
}
```

**Preview Generation Logic:**

```csharp
public async Task<ConnectedSystemDeletionPreview> GenerateDeletionPreviewAsync(int connectedSystemId)
{
    var preview = new ConnectedSystemDeletionPreview
    {
        ConnectedSystemId = connectedSystemId
    };

    // Get counts (fast queries)
    preview.ConnectedSystemObjectCount = await GetCsoCountAsync(connectedSystemId);
    preview.SyncRuleCount = await GetSyncRuleCountAsync(connectedSystemId);
    preview.RunProfileCount = await GetRunProfileCountAsync(connectedSystemId);
    preview.PendingExportCount = await GetPendingExportCountAsync(connectedSystemId);
    // ... etc

    // MVO impact analysis (more complex)
    var joinedMvos = await GetMvosJoinedToCsosAsync(connectedSystemId);
    preview.JoinedMvoCount = joinedMvos.Count;

    foreach (var mvo in joinedMvos)
    {
        var otherCsoCount = await GetOtherCsoCountForMvoAsync(mvo.Id, connectedSystemId);
        if (otherCsoCount > 0)
        {
            preview.MvosWithOtherConnectorsCount++;
        }
        else if (mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)
        {
            if (mvo.Type.DeletionGracePeriodDays > 0)
                preview.MvosWithGracePeriodCount++;
            else
                preview.MvosWithDeletionRuleCount++;
        }
    }

    // Generate warnings
    if (preview.MvosWithDeletionRuleCount > 0)
        preview.Warnings.Add($"{preview.MvosWithDeletionRuleCount} MVOs will be immediately deleted (WhenLastConnectorDisconnected rule, no grace period)");

    if (preview.MvosWithGracePeriodCount > 0)
        preview.Warnings.Add($"{preview.MvosWithGracePeriodCount} MVOs will be scheduled for deletion (grace period applies)");

    if (preview.PendingExportCount > 0)
        preview.Warnings.Add($"{preview.PendingExportCount} pending exports will be discarded");

    // Estimate time
    preview.WillRunAsBackgroundJob = preview.ConnectedSystemObjectCount >= 1000;
    preview.EstimatedDeletionTime = EstimateDeletionTime(preview.ConnectedSystemObjectCount);

    return preview;
}
```

**UI Flow:**

```
User clicks "Delete System"
        │
        ▼
┌─────────────────────────────────────────────────────────────────┐
│  Delete Connected System: "HR Source"                           │
│                                                                 │
│  ⚠ This action cannot be undone.                               │
│                                                                 │
│  ┌─ Preview Impact ─────────────────────────────────────────┐   │
│  │  Objects to delete:                                      │   │
│  │  • 12,847 Connected System Objects                       │   │
│  │  • 3 Sync Rules                                          │   │
│  │  • 2 Run Profiles                                        │   │
│  │  • 15 Pending Exports                                    │   │
│  │                                                          │   │
│  │  Metaverse Impact:                                       │   │
│  │  • 12,500 MVOs currently joined                          │   │
│  │  • 8,200 MVOs have other connector links (safe)          │   │
│  │  • 4,300 MVOs will be scheduled for deletion (30d grace) │   │
│  │  • 0 MVOs will be immediately deleted                    │   │
│  │                                                          │   │
│  │  ⚠ 15 pending exports will be discarded                 │   │
│  │                                                          │   │
│  │  Estimated time: ~45 seconds (background job)            │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  [ ] Evaluate MVO deletion rules after disconnect               │
│                                                                 │
│  Type "HR Source" to confirm: [________________]                │
│                                                                 │
│                    [Cancel]  [Delete System]                    │
└─────────────────────────────────────────────────────────────────┘
```

**Skip Preview Option:**

The API and UI will support skipping the preview for automation scenarios or experienced admins:

```
DELETE /api/connected-systems/{id}?skipPreview=true
```

In the UI, a "Skip Preview" link or advanced mode could bypass the preview step.

### Phase 1: Core Deletion Logic

```csharp
public async Task DeleteConnectedSystemAsync(
    int connectedSystemId,
    MetaverseObject? initiatedBy = null,
    bool evaluateMvoDeletionRules = false)
{
    // 1. Validate - check system exists, no running tasks
    // 2. Create activity for audit trail
    // 3. Delete in order (raw SQL for performance):
    //    a. PendingExports
    //    b. CSO attribute values, changes, then CSOs
    //    c. Partitions, containers
    //    d. Run profiles
    //    e. Sync rules (cascade handles mappings)
    //    f. Object types (cascade handles attributes)
    //    g. Setting values
    //    h. Null Activity FKs
    //    i. Connected System itself
    // 4. Optionally queue MVO cleanup job
}
```

### Phase 2: Repository Methods

Add to `IConnectedSystemRepository`:
```csharp
Task DeleteConnectedSystemAsync(int connectedSystemId);
Task DisconnectAllCsosFromMvosAsync(int connectedSystemId);
Task DeleteAllSyncRulesForSystemAsync(int connectedSystemId);
Task NullifyActivityConnectedSystemReferencesAsync(int connectedSystemId);
```

### Phase 3: API Endpoints

**Preview endpoint (default flow):**
```
GET /api/connected-systems/{id}/deletion-preview

Response: ConnectedSystemDeletionPreview (JSON)
```

**Delete endpoint:**
```
DELETE /api/connected-systems/{id}
Query params:
  - skipPreview: bool = false (bypass preview requirement)
  - evaluateMvoDeletionRules: bool = false
  - confirmationName: string (required - must match system name)
```

**Example flow:**
```
1. GET /api/connected-systems/5/deletion-preview
   → Returns preview with counts and warnings

2. DELETE /api/connected-systems/5?confirmationName=HR%20Source
   → Deletes the system (or queues background job)
```

### Phase 4: UI

- Add "Delete System" button to Connected System detail page
- Confirmation dialog showing:
  - CSO count
  - Sync rule count
  - Warning about joined MVOs
  - Checkbox for MVO deletion rule evaluation
- Progress indicator for sync operations
- Success/failure toast

## Performance Considerations

1. **Use raw SQL for bulk deletes** - EF Core tracking is too slow for large datasets
2. **Delete in correct order** - Respect foreign key constraints
3. **Single transaction** - Wrap all deletes in a transaction for atomicity
4. **Batch if needed** - For very large systems, batch CSO deletions

### Performance Targets

| CSO Count | Target Time | Method |
|-----------|-------------|--------|
| < 1,000 | < 5 seconds | Synchronous |
| 1,000 - 10,000 | < 30 seconds | Background job |
| 10,000 - 100,000 | < 5 minutes | Background job, batched |
| > 100,000 | < 30 minutes | Background job, batched |

## SQL Delete Order (PostgreSQL)

```sql
-- 1. Delete pending exports and their attribute changes
DELETE FROM "PendingExportAttributeValueChanges"
WHERE "PendingExportId" IN (SELECT "Id" FROM "PendingExports" WHERE "ConnectedSystemId" = @id);
DELETE FROM "PendingExports" WHERE "ConnectedSystemId" = @id;

-- 2. Delete CSO-related data
DELETE FROM "ConnectedSystemObjectChangeAttributeValues"
WHERE "ConnectedSystemObjectChangeAttributeId" IN (
    SELECT "Id" FROM "ConnectedSystemObjectChangeAttributes"
    WHERE "ConnectedSystemObjectChangeId" IN (
        SELECT "Id" FROM "ConnectedSystemObjectChanges" WHERE "ConnectedSystemId" = @id
    )
);
DELETE FROM "ConnectedSystemObjectChangeAttributes"
WHERE "ConnectedSystemObjectChangeId" IN (
    SELECT "Id" FROM "ConnectedSystemObjectChanges" WHERE "ConnectedSystemId" = @id
);
DELETE FROM "ConnectedSystemObjectChanges" WHERE "ConnectedSystemId" = @id;

-- 3. Disconnect CSOs from MVOs (preserve MVO, break link)
UPDATE "ConnectedSystemObjects" SET "MetaverseObjectId" = NULL WHERE "ConnectedSystemId" = @id;

-- 4. Delete CSO attribute values and CSOs
DELETE FROM "ConnectedSystemObjectAttributeValues"
WHERE "ConnectedSystemObjectId" IN (
    SELECT "Id" FROM "ConnectedSystemObjects" WHERE "ConnectedSystemId" = @id
);
DELETE FROM "ConnectedSystemObjects" WHERE "ConnectedSystemId" = @id;

-- 5. Delete sync rules (cascades to mappings, sources, scoping)
DELETE FROM "SyncRules" WHERE "ConnectedSystemId" = @id;

-- 6. Delete containers and partitions
DELETE FROM "ConnectedSystemContainers"
WHERE "PartitionId" IN (
    SELECT "Id" FROM "ConnectedSystemPartitions" WHERE "ConnectedSystemId" = @id
);
DELETE FROM "ConnectedSystemPartitions" WHERE "ConnectedSystemId" = @id;

-- 7. Delete run profiles
DELETE FROM "ConnectedSystemRunProfiles" WHERE "ConnectedSystemId" = @id;

-- 8. Delete object types (cascades to attributes)
DELETE FROM "ConnectedSystemObjectTypes" WHERE "ConnectedSystemId" = @id;

-- 9. Delete setting values
DELETE FROM "ConnectedSystemSettingValues" WHERE "ConnectedSystemId" = @id;

-- 10. Nullify activity references
UPDATE "Activities" SET "ConnectedSystemId" = NULL WHERE "ConnectedSystemId" = @id;

-- 11. Delete worker tasks
DELETE FROM "WorkerTasks" WHERE "Discriminator" IN ('SynchronisationWorkerTask', 'ClearConnectedSystemObjectsWorkerTask')
AND (("SyncTask_ConnectedSystemId" = @id) OR ("ClearTask_ConnectedSystemId" = @id));

-- 12. Finally, delete the connected system
DELETE FROM "ConnectedSystems" WHERE "Id" = @id;
```

## Testing Strategy

1. **Unit tests**: Mock repository, verify delete order
2. **Integration tests**:
   - Delete system with no CSOs
   - Delete system with CSOs (no MVO joins)
   - Delete system with joined CSOs
   - Delete system with sync rules
   - Delete system with running tasks (should block)
3. **Performance tests**: Delete system with 10k, 100k CSOs

## Design Decisions Made

1. **Preview is default** - Show deletion impact preview before confirming (can be skipped)
2. **Type name to confirm** - Require typing the system name for safety (like GitHub repo deletion)
3. **Pending exports shown in preview** - Warn about pending exports but don't block deletion

## Open Questions

1. Should we support "soft delete" (archive rather than delete)?
2. Should the MVO impact analysis be optional in the preview (for very large systems where it might be slow)?
3. Should we log/export a deletion manifest before deleting (for recovery purposes)?

---

## Future Enhancement: Attribute Impact Analysis

> **Status**: Backlog (GitHub Issue #134)
> **Priority**: High (after initial implementation)

### Problem Statement

When deleting a Connected System that contributes attribute values to MVOs, the current preview only shows:
- How many MVOs might be deleted (WhenLastConnectorDisconnected rule)

It does NOT show:
- Which MVO attributes would be recalled (deleted) due to attribute recall settings
- Which MVO attributes would change value due to precedence shift to another contributor
- What downstream exports those attribute changes would trigger

This is a significant gap based on real-world MIM migration experience. Example scenario:

1. Migrating from old HR system to new HR system
2. Most attribute precedence switched to new system
3. One or two attributes accidentally left with old system as priority
4. Delete old system → those attributes recalled or shift to lower-priority contributor
5. Changed attributes trigger exports to AD, causing account changes or disabling
6. Business impact: users locked out, wrong data pushed to production systems

### Proposed Solution: Attribute Impact Analysis

Add an **optional, in-depth preview** that analyses attribute-level impact:

```csharp
public class ConnectedSystemDeletionAttributeImpact
{
    // Summary counts
    public int AttributesAffectedCount { get; set; }
    public int AttributesRecalledCount { get; set; }      // Will be deleted from MVO
    public int AttributesPrecedenceShiftCount { get; set; } // Value will change
    public int ExportsTriggeredCount { get; set; }        // Downstream exports

    // Detailed breakdown (limited for performance)
    public List<AttributeImpactDetail> TopImpactedAttributes { get; set; } = new();
    public List<MvoImpactDetail> SampleAffectedMvos { get; set; } = new();
}

public class AttributeImpactDetail
{
    public string AttributeName { get; set; } = null!;
    public int AffectedMvoCount { get; set; }
    public AttributeImpactType ImpactType { get; set; } // Recall, PrecedenceShift
    public string? NewContributorSystem { get; set; }   // For precedence shift
}

public class MvoImpactDetail
{
    public Guid MvoId { get; set; }
    public string? MvoDisplayName { get; set; }
    public List<AttributeChange> AttributeChanges { get; set; } = new();
    public List<ExportImpact> TriggeredExports { get; set; } = new();
}
```

### Performance Considerations

This analysis is expensive:
- Must iterate all CSOs and their contributed attributes
- Must check sync rule recall settings and priority
- Must evaluate export rules for each MVO change
- For 100k CSOs × 50 attributes = 5M evaluations

**Mitigation strategies:**
1. Make it optional ("Run detailed analysis" button)
2. Sample-based: Analyse first N MVOs, extrapolate
3. Attribute-focused: Show top 10 most-impacted attributes with counts
4. Background job: Queue analysis, show results when ready
5. Caching: Cache sync rule/priority lookups

### UI Mockup

```
┌────────────────────────────────────────────────────────────────┐
│  Delete Connected System: "Old HR System"                      │
│                                                                │
│  ┌─ Basic Impact ───────────────────────────────────────────┐  │
│  │  • 12,847 CSOs will be deleted                           │  │
│  │  • 3 Sync Rules will be deleted                          │  │
│  │  • 4,300 MVOs may be affected                            │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                │
│  [▶ Run Detailed Attribute Analysis]                           │
│                                                                │
│  ┌─ Attribute Impact (detailed) ────────────────────────────┐  │
│  │  ⚠ 2,150 MVO attributes will be RECALLED (deleted)       │  │
│  │  ⚠ 890 MVO attributes will CHANGE VALUE (precedence)     │  │
│  │  ⚠ 1,200 exports to "Active Directory" will be triggered │  │
│  │                                                          │  │
│  │  Most impacted attributes:                               │  │
│  │  • department: 800 MVOs affected (→ "New HR System")     │  │
│  │  • manager: 650 MVOs affected (will be recalled)         │  │
│  │  • costCenter: 540 MVOs affected (→ "New HR System")     │  │
│  │                                                          │  │
│  │  [View Sample MVOs] [Export Full Report]                 │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                │
│  Type "Old HR System" to confirm: [________________]           │
│                                                                │
│                    [Cancel]  [Delete System]                   │
└────────────────────────────────────────────────────────────────┘
```

### Implementation Phases

1. **Phase 1 (Current)**: Basic preview - counts only
2. **Phase 2 (This enhancement)**: Attribute impact analysis
3. **Phase 3 (Future)**: Export simulation - show exactly what would be exported

## Next Steps

1. Review and approve design
2. Implement repository methods
3. Implement ConnectedSystemServer.DeleteAsync
4. Add API endpoint
5. Add UI
6. Write tests
