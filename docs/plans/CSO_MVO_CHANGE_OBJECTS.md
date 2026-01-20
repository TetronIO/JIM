# CSO and MVO Change Objects - Complete Design

> **Status:** Planned
> **Milestone:** Post-MVP
> **GitHub Issue:** #269
> **Created:** 2026-01-06

## Overview

Complete the implementation of change object tracking and lifecycle management for Connected System Objects (CSOs) and Metaverse Objects (MVOs). This includes displaying change history timelines, tracking MVO changes, viewing deleted objects, and implementing retention policies to prevent unbounded database growth.

## Business Value

### Problem Statement
1. **No visibility into object change history** - Administrators cannot see a timeline of changes to CSOs or MVOs from the object detail views
2. **MVO changes not tracked** - Unlike CSOs, MVO attribute changes are never recorded
3. **Deleted objects invisible** - Once a CSO/MVO is deleted, there's no way to view its historical changes
4. **Unbounded growth** - Change objects accumulate indefinitely, eventually degrading performance
5. **Audit gaps** - The current system doesn't provide complete audit trails for compliance

### Benefits
- **Complete audit trail** for compliance and troubleshooting
- **Self-service investigation** - Admins can trace when/why attributes changed
- **Performance protection** through automated cleanup
- **Deleted object forensics** - View history even after object deletion

## Current State Analysis

### What Exists

| Component | Status | Notes |
|-----------|--------|-------|
| `ConnectedSystemObjectChange` model | ✅ Complete | Created on CSO create/update/delete |
| `MetaverseObjectChange` model | ⚠️ Schema only | Model exists but never instantiated |
| RPEI detail view | ✅ Shows changes | Only place CSO changes are displayed |
| RPEI detail view (CSO deletion) | ⚠️ Info only | Shows generic MVO impact info, cannot link to specific MVO |
| `HistoryRetentionPeriod` setting | ✅ Defined | 30-day default, but not enforced |
| JIM.Scheduler | ⚠️ Stub | Loop exists but no actual jobs |
| CSO detail page | ⚠️ Partial | No change history section |
| MVO detail page | ⚠️ Minimal | Only shows current attributes |

**Note:** The RPEI detail view for CSO deletions currently shows an informational "Metaverse Impact" section explaining deletion rules, but cannot link to the specific MVO that was affected because `ActivityRunProfileExecutionItem.MetaverseObjectChange` is never populated. Phase 1 will enable this by creating MVO change records during CSO deletion/disconnection.

### Key Code Locations

| Purpose | File | Lines |
|---------|------|-------|
| CSO change creation | `JIM.Application/Servers/ConnectedSystemServer.cs` | 1879-2392 |
| CSO change model | `JIM.Models/Staging/ConnectedSystemObjectChange.cs` | 1-57 |
| MVO change model | `JIM.Models/Core/MetaverseObjectChange.cs` | 1-41 |
| RPEI detail display | `JIM.Web/Pages/ActivityRunProfileExecutionItemDetail.razor` | 135-187 |
| CSO detail page | `JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor` | 1-260 |
| MVO detail page | `JIM.Web/Pages/Types/View.razor` | 1-101 |
| Service settings | `JIM.Application/Servers/ServiceSettingsServer.cs` | 1-252 |
| History retention setting | `JIM.Models/Core/Constants.cs` | (HistoryRetentionPeriod key) |

## Technical Architecture

### Data Model Enhancements

#### 1. MVO Change Object Creation
Currently `MetaverseObjectChange` is never instantiated. We need to create these during:
- **Sync operations** - When sync rules modify MVO attributes
- **Direct MVO updates** - When admins edit MVOs via UI/API
- **MVO deletion** - Capture final state before deletion
- **CSO deletion causing disconnection** - When a CSO is deleted and triggers MVO disconnection (so the RPEI can link to the affected MVO)
- **Group membership changes** - When group rules modify membership

#### 2. Deleted Object Tracking
When CSO/MVO is deleted, preserve audit information:

```
ConnectedSystemObjectChange (existing fields)
├── DeletedObjectType (✅ exists)
├── DeletedObjectExternalIdAttributeValue (⚠️ exists but unused)
└── DeletedObjectDisplayName (NEW - for UI display)

MetaverseObjectChange (new fields)
├── DeletedObjectType (NEW)
└── DeletedObjectDisplayName (NEW)
```

#### 3. Feature Flags for Change Tracking
New service settings to enable/disable change tracking:

```
ChangeTracking.CsoChanges.Enabled           (default: true)
ChangeTracking.MvoChanges.Enabled           (default: true)
```

**Rationale:**
- Some deployments may not need detailed change history
- Reduces database writes and storage for high-volume environments
- Allows gradual rollout of MVO change tracking
- Users can disable either independently

**Implementation:**
- Check feature flag before creating change objects
- Flag checked in `ConnectedSystemServer` for CSO changes
- Flag checked in `MetaverseServer` / sync processors for MVO changes
- Changing the flag does not delete existing change objects

#### 4. Lifecycle Management Settings
Extend service settings with granular retention policies:

```
History.RetentionPeriod.CsoChanges          (default: 90 days)
History.RetentionPeriod.MvoChanges          (default: 90 days)
History.RetentionPeriod.DeletedObjectChanges (default: 365 days)
History.RetentionPeriod.Activities          (default: 90 days)
```

**Note:** Cleanup runs as part of JIM.Worker housekeeping (every 60 seconds during idle time), not on a fixed schedule. This follows the existing pattern for MVO deletion housekeeping.

### UI Components

#### 1. Change History Timeline Component
Reusable component for both CSO and MVO detail pages:

```
<ChangeHistoryTimeline>
├── Timeline header with date range filter
├── Search box (attribute name, value contains)
├── Change type filter (Create/Update/Delete)
├── Paginated timeline entries
│   ├── Timestamp
│   ├── Change type badge
│   ├── Initiator (user/sync rule/API key)
│   ├── Expandable attribute changes
│   │   ├── Attribute name
│   │   ├── Old value -> New value
│   │   └── Change type (Add/Remove)
│   └── Link to Activity/RPEI if available
└── Export to CSV/JSON option
```

#### 2. Deleted Objects Browser
New page to view deleted CSOs and MVOs:

```
/admin/deleted-objects
├── Tab: Deleted CSOs
│   ├── Filter by Connected System
│   ├── Filter by deletion date range
│   ├── Search by external ID
│   └── Results table
│       ├── External ID (preserved)
│       ├── Object Type
│       ├── Connected System
│       ├── Deleted Date
│       ├── Deleted By
│       └── View History button
└── Tab: Deleted MVOs
    ├── Filter by Object Type
    ├── Filter by deletion date range
    ├── Search by display name
    └── Results table
        ├── Display Name (preserved)
        ├── Object Type
        ├── Deleted Date
        ├── Deleted By
        └── View History button
```

#### 3. Lifecycle Management Settings UI
Add to Service Settings page:

```
Change Tracking Settings
├── Track CSO Changes: [✓] Enabled
├── Track MVO Changes: [✓] Enabled

History & Retention Settings
├── CSO Change Retention: [90] days
├── MVO Change Retention: [90] days
├── Deleted Object Change Retention: [365] days
├── Activity Retention: [90] days
├── Last Cleanup Run: [datetime]
└── [Run Cleanup Now] button

⚠️ Danger Zone
├── CSO Change History
│   ├── Connected System: [All ▼] or [Multi-select specific systems]
│   ├── Records to delete: 12,345
│   └── [Delete CSO Change History] - Requires confirmation dialog
└── MVO Change History
    ├── Records to delete: 5,678
    └── [Delete MVO Change History] - Requires confirmation dialog
```

**Danger Zone - CSO Change History Dialog:**
```
⚠️ Delete CSO Change History?

Scope: [All Connected Systems ▼]
       [ ] HR System
       [✓] Active Directory
       [✓] SAP

This will permanently delete CSO change tracking records
for the selected connected system(s).
This action cannot be undone.

Records to be deleted: 8,234
Date range: 2024-01-15 to 2026-01-06

Type "DELETE" to confirm: [________]

[Cancel] [Delete]
```

**Note:** Cleanup runs automatically during JIM.Worker idle time (housekeeping). No schedule configuration needed.

### Background Job Infrastructure

#### JIM.Worker Housekeeping (Short-Term Approach)
Leverage existing housekeeping infrastructure in `JIM.Worker/Worker.cs` (lines 383-436):

```csharp
// Add to PerformHousekeepingAsync() method:
private async Task PerformHousekeepingAsync(JimApplication jim)
{
    // Existing: MVO deletion housekeeping
    // ...

    // NEW: Change object lifecycle cleanup
    await PerformChangeObjectCleanupAsync(jim);
}

private async Task PerformChangeObjectCleanupAsync(JimApplication jim)
{
    // 1. Get retention settings
    var csoRetention = await jim.ServiceSettings.GetCsoChangeRetentionAsync();
    var mvoRetention = await jim.ServiceSettings.GetMvoChangeRetentionAsync();
    var deletedRetention = await jim.ServiceSettings.GetDeletedObjectChangeRetentionAsync();

    // 2. Delete expired CSO changes (batch of 100)
    var csoDeleted = await jim.ChangeHistory.DeleteExpiredCsoChangesAsync(
        olderThan: DateTime.UtcNow - csoRetention,
        maxRecords: 100);

    // 3. Delete expired MVO changes (batch of 100)
    var mvoDeleted = await jim.ChangeHistory.DeleteExpiredMvoChangesAsync(
        olderThan: DateTime.UtcNow - mvoRetention,
        maxRecords: 100);

    // 4. Log cleanup statistics
    if (csoDeleted > 0 || mvoDeleted > 0)
        Log.Information("ChangeObjectCleanup: Deleted {CsoCount} CSO changes, {MvoCount} MVO changes",
            csoDeleted, mvoDeleted);

    // 5. Update last cleanup timestamp
    await jim.ServiceSettings.UpdateLastCleanupRunAsync(DateTime.UtcNow);
}
```

**Pattern follows existing MVO deletion housekeeping:**
- Runs during worker idle time (no sync tasks queued)
- Rate-limited to every 60 seconds
- Batch processing (max 100 records per run) to avoid locks
- Logs cleanup statistics

**Future: JIM.Scheduler**
When JIM.Scheduler is fully implemented, cleanup can be migrated there for:
- Configurable schedule (e.g., run at 2 AM daily)
- Separation of concerns (sync vs maintenance)
- More sophisticated job management

#### Activity Logging for Change Object Deletions
All change object deletions must be audited via Activities, regardless of trigger (housekeeping, manual cleanup, or danger zone bulk delete).

**Lightweight Audit Approach:**
Rather than creating individual `ActivityRunProfileExecutionItem` records for each deleted change object (which would be excessive), we capture summary information in the Activity itself:

```csharp
// Activity properties for change object cleanup
public class Activity
{
    // Existing properties...

    // For TargetType = ChangeHistoryCleanup
    public int? DeletedRecordCount { get; set; }      // Total records deleted
    public DateTime? DeletedRecordsFromDate { get; set; }  // Oldest record deleted
    public DateTime? DeletedRecordsToDate { get; set; }    // Newest record deleted
}
```

**Activity Target Types to Add:**
```csharp
public enum ActivityTargetType
{
    // Existing...
    CsoChangeHistoryCleanup = 20,    // CSO change objects deleted
    MvoChangeHistoryCleanup = 21,    // MVO change objects deleted
}
```

**Hierarchical Activities for CS Clear/Delete:**

The Activity model already supports hierarchy via `ParentActivityId`. Currently `GetActivitiesAsync` filters to show only top-level activities (`ParentActivityId == null`).

When a user clears/deletes a Connected System AND opts to delete change history:
1. **Parent Activity**: CS Clear/Delete operation
2. **Child Activity**: Change History Cleanup (linked via `ParentActivityId`)

```
Activity: Delete Connected System "Active Directory"
├── TargetType: ConnectedSystem
├── TargetOperationType: Delete
├── Status: Complete
└── Child Activities:
    └── Activity: CSO Change History Cleanup
        ├── ParentActivityId: [parent guid]
        ├── TargetType: CsoChangeHistoryCleanup
        ├── DeletedRecordCount: 8,234
        ├── DeletedRecordsFromDate: 2024-01-15
        └── DeletedRecordsToDate: 2026-01-06
```

**UI Enhancement Required:**
The Activity List page currently only shows top-level activities. To support hierarchical display:
- Add expandable rows or drill-down to show child activities
- Or show child activity count badge on parent row
- Activity Detail page should list child activities

**Activity Examples:**

| Trigger | InitiatedBy | Target Type | Parent Activity |
|---------|-------------|-------------|-----------------|
| Housekeeping | System | CsoChangeHistoryCleanup | None (standalone) |
| Manual cleanup button | User | CsoChangeHistoryCleanup | None (standalone) |
| Danger zone bulk delete | User | CsoChangeHistoryCleanup | None (standalone) |
| CS Clear + delete history | User | CsoChangeHistoryCleanup | CS Clear Activity |
| CS Delete + delete history | User | CsoChangeHistoryCleanup | CS Delete Activity |

**Activity List Display:**
In the Activity List page, these activities would show:
```
[2026-01-06 14:30:00] CSO Change History Cleanup - Deleted 100 records (2024-01-15 to 2024-04-15) - System
[2026-01-06 10:15:00] Delete Connected System "Active Directory" [+1 child] - admin@example.com
    └── CSO Change History Cleanup - Deleted 8,234 records (2024-01-15 to 2026-01-06)
```

### API Endpoints

New endpoints for change history:

```
GET /api/connected-systems/{systemId}/objects/{objectId}/changes
    ?fromDate=&toDate=&changeType=&attributeName=&page=&pageSize=

GET /api/metaverse/objects/{objectId}/changes
    ?fromDate=&toDate=&changeType=&attributeName=&page=&pageSize=

GET /api/deleted-objects/cso
    ?connectedSystemId=&fromDate=&toDate=&externalId=&page=&pageSize=

GET /api/deleted-objects/mvo
    ?objectTypeId=&fromDate=&toDate=&displayName=&page=&pageSize=

GET /api/deleted-objects/cso/{changeId}/history
GET /api/deleted-objects/mvo/{changeId}/history

POST /api/admin/history/cleanup  (manual trigger - applies retention policy)

# Danger Zone endpoints (bulk delete)
DELETE /api/admin/history/cso-changes
    ?connectedSystemIds=1,2,3  (optional - if omitted, deletes ALL)
DELETE /api/admin/history/mvo-changes  (delete ALL MVO change history)

# Statistics for danger zone confirmation dialog
GET /api/admin/history/cso-changes/stats
    ?connectedSystemIds=1,2,3  (optional - if omitted, returns stats for ALL)
GET /api/admin/history/mvo-changes/stats  (returns count, date range)

# Connected System operations with change history option
DELETE /api/connected-systems/{id}/objects
    ?deleteChangeHistory=false  (default: false - keep change history)
DELETE /api/connected-systems/{id}
    ?deleteChangeHistory=false  (default: false - keep change history)
```

## Implementation Phases

### Phase 1: Feature Flags and MVO Change Object Creation
**Deliverables:**
1. Add `ChangeTracking.CsoChanges.Enabled` service setting (default: true)
2. Add `ChangeTracking.MvoChanges.Enabled` service setting (default: true)
3. Add feature flag checks to existing CSO change creation code
4. Create `MetaverseObjectChange` records during sync operations (when enabled)
5. Create `MetaverseObjectChange` records during direct MVO updates (when enabled)
6. Create `MetaverseObjectChange` records on MVO deletion (when enabled)
7. Add unit tests for change object creation and feature flag behaviour

**Files to modify:**
- `JIM.Models/Core/Constants.cs` - Add new setting keys
- `JIM.Application/Servers/ServiceSettingsServer.cs` - Add methods for feature flag access
- `JIM.Application/Servers/ConnectedSystemServer.cs` - Add feature flag check before CSO change creation
- `JIM.Application/Servers/MetaverseServer.cs` - Add change tracking to update methods
- `JIM.Worker/Processors/SyncTaskProcessorBase.cs` - Create MVO changes during sync
- `JIM.PostgresData/Repositories/MetaverseRepository.cs` - Add change persistence

### Phase 2: Change History Timeline UI
**Deliverables:**
1. Create reusable `ChangeHistoryTimeline.razor` component
2. Add change history section to CSO detail page
3. Add change history section to MVO detail page
4. Implement search and filtering

**Files to modify/create:**
- `JIM.Web/Shared/ChangeHistoryTimeline.razor` (new)
- `JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor`
- `JIM.Web/Pages/Types/View.razor`

### Phase 3: Deleted Objects Browser
**Deliverables:**
1. Enhance change models with deleted object metadata
2. Create deleted objects browser page
3. Create deleted object detail/history view
4. Add database migration for new fields

**Files to modify/create:**
- `JIM.Models/Staging/ConnectedSystemObjectChange.cs` - Add DeletedObjectDisplayName
- `JIM.Models/Core/MetaverseObjectChange.cs` - Add deleted object fields
- `JIM.Web/Pages/Admin/DeletedObjects.razor` (new)
- `JIM.PostgresData/Migrations/` - New migration

### Phase 4: Lifecycle Management (JIM.Worker Housekeeping)
**Deliverables:**
1. Add granular retention settings to service settings
2. Add `ChangeHistoryServer` with cleanup methods
3. Add cleanup logic to JIM.Worker housekeeping
4. Add lifecycle settings UI (feature flags + retention periods)
5. Add manual cleanup trigger via API
6. Add "Last Cleanup Run" tracking
7. Add Activity logging for all change object deletions (summary: count + date range)
8. Add `CsoChangeHistoryCleanup` and `MvoChangeHistoryCleanup` activity target types
9. Add danger zone UI with confirmation dialog and per-CS multi-select for CSO changes
10. Add danger zone API endpoints for bulk delete (with connectedSystemIds filter)
11. Update CS clear dialog with "delete change history" checkbox option
12. Update CS delete dialog with "delete change history" checkbox option
13. Add `deleteChangeHistory` query parameter to CS clear/delete API endpoints

**Files to modify/create:**
- `JIM.Models/Core/Constants.cs` - Add retention setting keys
- `JIM.Models/Activities/Activity.cs` - Add `DeletedRecordCount`, `DeletedRecordsFromDate`, `DeletedRecordsToDate`
- `JIM.Models/Activities/ActivityTargetType.cs` - Add cleanup target types
- `JIM.Application/Servers/ChangeHistoryServer.cs` (new) - Cleanup business logic + Activity creation
- `JIM.Application/Servers/ConnectedSystemServer.cs` - Add deleteChangeHistory parameter handling
- `JIM.PostgresData/Repositories/ChangeHistoryRepository.cs` (new) - Cleanup queries + stats
- `JIM.Worker/Worker.cs` - Add `PerformChangeObjectCleanupAsync()` to housekeeping
- `JIM.Web/Pages/Admin/ServiceSettings.razor` - Add retention UI + danger zone section
- `JIM.Web/Pages/Admin/ConnectedSystemDetail.razor` - Update clear/delete dialogs
- `JIM.Web/Controllers/Api/ChangeHistoryController.cs` - Add danger zone endpoints
- `JIM.Web/Controllers/Api/ConnectedSystemsController.cs` - Add deleteChangeHistory parameter
- `JIM.PostgresData/Migrations/` - New migration for Activity fields

**Note:** Uses existing JIM.Worker housekeeping pattern. Future migration to JIM.Scheduler when that infrastructure is complete.

### Phase 5: API Endpoints
**Deliverables:**
1. CSO change history endpoint
2. MVO change history endpoint
3. Deleted objects endpoints
4. Manual cleanup endpoint
5. OpenAPI documentation

**Files to create:**
- `JIM.Web/Controllers/Api/ChangeHistoryController.cs` (new)
- `JIM.Web/Controllers/Api/DeletedObjectsController.cs` (new)

## Design Decisions

### Q1: Separate retention periods for existing vs deleted object changes?
**Recommendation: Yes**

Rationale:
- Deleted object changes are the only record that an object ever existed
- Compliance requirements often mandate longer retention for deletion records
- Existing object changes can be regenerated from current state if needed

**Proposed defaults:**
- Existing object changes: 90 days
- Deleted object changes: 365 days

### Q2: Should RPEI Data Snapshot be the immutable audit record?
**Recommendation: Yes, but complementary**

The issue suggests making RPEI Data Snapshot the immutable audit item. Recommendation:
- **RPEI + DataSnapshot** = Immutable audit record (never deleted by lifecycle)
- **Change objects** = Convenience for quick browsing (can be lifecycle-deleted)

This means:
- Activities and RPEIs have their own retention period (longer)
- Change objects can have shorter retention
- If change objects are deleted, RPEI DataSnapshot still provides full audit trail

### Q3: How to handle orphaned change objects?
**Recommendation: Separate cleanup policy**

Orphaned changes (no parent object, no RPEI link) can occur when:
- System is reset but change history preserved
- Data corruption
- Manual database manipulation

Apply shorter retention (e.g., 30 days) to truly orphaned records.

### Q4: Should we soft-delete or hard-delete during lifecycle cleanup?
**Recommendation: Hard delete**

Rationale:
- Soft-delete would defeat the purpose of lifecycle management

### Q5: What happens to CSO change objects when Connected System is cleared or deleted?
**Recommendation: Offer option to delete, default to keep**

**Scenario 1: Connected System Clear (delete all CSOs)**
When admin clears all CSOs from a Connected System:
- CSO change objects become orphaned (CSO FK is nulled)
- **Default behaviour:** Keep change history (for audit trail)
- **Optional:** Checkbox to delete change history alongside CSOs
- Housekeeping will eventually clean up based on retention policy

**Scenario 2: Connected System Deletion**
When admin deletes an entire Connected System:
- All CSOs are deleted first (triggering scenario 1)
- CSO change objects reference the now-deleted system via `ConnectedSystemId`
- **Default behaviour:** Keep change history (for audit trail)
- **Optional:** Checkbox to delete all change history for this system

**UI Enhancement - Clear Connected System Dialog:**
```
⚠️ Clear All Objects from "Active Directory"?

This will delete all 5,432 CSOs from this connected system.

[✓] Also delete CSO change history (8,234 records)
    └── If unchecked, change history is retained for audit purposes
        and will be cleaned up by housekeeping after retention period.

Type the system name to confirm: [________]

[Cancel] [Clear All]
```

**UI Enhancement - Delete Connected System Dialog:**
```
⚠️ Delete Connected System "Active Directory"?

This will permanently delete:
• The connected system configuration
• All 5,432 CSOs
• All sync rules referencing this system
• All pending exports for this system

[✓] Also delete CSO change history (8,234 records)
    └── If unchecked, change history is retained for audit purposes
        and will be cleaned up by housekeeping after retention period.

Type "DELETE" to confirm: [________]

[Cancel] [Delete]
```

**Implementation Notes:**
- `ConnectedSystemId` on change objects is NOT a foreign key (allows orphaning)
- Orphaned change objects still show in "Deleted Objects Browser" filtered by system ID
- Activity audit records reference system name snapshot (not FK) so remain valid
- The RPEI DataSnapshot provides the immutable audit trail
- Change objects are explicitly designed to be deletable

## Success Criteria

1. **Feature Flags**
   - [ ] CSO change tracking can be enabled/disabled via service setting
   - [ ] MVO change tracking can be enabled/disabled via service setting
   - [ ] Both default to enabled
   - [ ] Disabling does not delete existing change objects

2. **Change History Visibility**
   - [ ] CSO detail page shows complete change timeline
   - [ ] MVO detail page shows complete change timeline
   - [ ] Changes are searchable by attribute name and value
   - [ ] Changes are filterable by date range and change type

3. **MVO Change Tracking**
   - [ ] MVO changes created during sync operations (when enabled)
   - [ ] MVO changes created during direct updates (when enabled)
   - [ ] MVO deletion creates final change record (when enabled)

4. **Deleted Object Access**
   - [ ] Admins can browse deleted CSOs by connected system
   - [ ] Admins can browse deleted MVOs by object type
   - [ ] Admins can view full change history of deleted objects

5. **Lifecycle Management**
   - [ ] Automated cleanup runs during worker housekeeping
   - [ ] Retention periods are configurable per change type
   - [ ] Manual cleanup trigger available via API
   - [ ] Cleanup statistics logged
   - [ ] "Last Cleanup Run" timestamp visible in UI

6. **Audit Logging for Deletions**
   - [ ] All change object deletions create an Activity record
   - [ ] Activity captures summary (count, date range) not individual items
   - [ ] Housekeeping deletions show "System" as initiator
   - [ ] Manual/danger zone deletions show user as initiator
   - [ ] Activities visible in Activity List with clear description
   - [ ] CS Clear/Delete with change history deletion creates parent-child Activities
   - [ ] Activity List shows child activity count badge on parent rows
   - [ ] Activity Detail page lists child activities

7. **Danger Zone**
   - [ ] UI provides bulk delete for all CSO change history
   - [ ] UI provides bulk delete for MVO change history
   - [ ] CSO deletion supports per-Connected System or multi-select
   - [ ] Confirmation dialog shows record count and date range
   - [ ] User must type "DELETE" to confirm
   - [ ] Bulk delete creates audit Activity

8. **Connected System Clear/Delete Edge Cases**
   - [ ] CS clear dialog offers option to delete change history (default: keep)
   - [ ] CS delete dialog offers option to delete change history (default: keep)
   - [ ] API supports `deleteChangeHistory` query parameter
   - [ ] Orphaned change objects remain queryable in Deleted Objects Browser

9. **Performance**
   - [ ] Change history queries use efficient indexes
   - [ ] Pagination prevents large result sets
   - [ ] Cleanup job uses batched deletes to avoid locks

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Large change history slows UI | High | Pagination, lazy loading, efficient indexes |
| Cleanup job causes database locks | Medium | Batched deletes, run during low-activity periods |
| MVO change creation adds sync overhead | Medium | Batch change creation, async where possible |
| Deleted object accumulation | Low | Shorter retention for orphaned records |

## Dependencies

- **MudBlazor Timeline** - May need custom component if MudTimeline insufficient
- **No new external dependencies** - Uses existing JIM.Worker housekeeping infrastructure

## Open Questions

1. Should change history be exportable to CSV/JSON for compliance reports?
2. Should there be alerts when retention cleanup fails?
3. Should we track who viewed change history (audit of audit)?

## Appendix: Database Schema Changes

```sql
-- Add to ConnectedSystemObjectChange
ALTER TABLE "ConnectedSystemObjectChanges"
ADD COLUMN "DeletedObjectDisplayName" TEXT NULL;

-- Add to MetaverseObjectChange
ALTER TABLE "MetaverseObjectChanges"
ADD COLUMN "DeletedObjectTypeId" INTEGER NULL,
ADD COLUMN "DeletedObjectDisplayName" TEXT NULL;

-- Indexes for efficient querying
CREATE INDEX IX_CSOChanges_CSOId_ChangeTime
ON "ConnectedSystemObjectChanges" ("ConnectedSystemObjectId", "ChangeTime" DESC);

CREATE INDEX IX_MVOChanges_MVOId_ChangeTime
ON "MetaverseObjectChanges" ("MetaverseObjectId", "ChangeTime" DESC);

CREATE INDEX IX_CSOChanges_Deleted
ON "ConnectedSystemObjectChanges" ("ChangeTime" DESC)
WHERE "ChangeType" = 4; -- Delete

CREATE INDEX IX_MVOChanges_Deleted
ON "MetaverseObjectChanges" ("ChangeTime" DESC)
WHERE "ChangeType" = 4; -- Delete
```
