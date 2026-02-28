# Synchronisation Status Improvements

- **Status:** Done
**Milestone:** Post-MVP
**Created:** 2026-01-07

## Overview

Improve the clarity and usefulness of synchronisation statuses displayed in the Activity detail pages. The current implementation uses generic change types (Create, Update, Delete) across all run profile types, which doesn't accurately convey what operations actually occurred. This leads to user confusion when reviewing sync results.

## Business Value

- **Improved clarity**: Users can immediately understand what happened during each sync operation
- **Better troubleshooting**: Distinguishing projections from joins from attribute flow helps diagnose sync issues
- **Performance improvement**: Eliminating unnecessary RPEI creation reduces database writes and storage
- **Familiar terminology**: Using industry-standard ILM terminology helps users transitioning from other identity management systems

## Current State Issues

### Issue 1: Ambiguous Change Types

The `ObjectChangeType` enum uses the same values across different run types with different meanings:

| Run Type | "Create" Means | "Update" Means |
|----------|----------------|----------------|
| Import | CSO added to staging (ambiguous) | CSO attributes updated |
| Sync | MVO projected | MVO joined OR attribute flow |
| Export | Provisioning export | Attribute update export |

### Issue 2: Unnecessary RPEI Creation During Import

**Location:** `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs:499`

During import, an RPEI is created for **every** import object, even when the CSO already exists with identical attributes. This:
- Creates false "Update" entries in the UI
- Impacts database performance with unnecessary writes
- Confuses users who see updates that didn't change anything

**Root cause:** RPEI is added to the collection at line 499 before checking whether `UpdateConnectedSystemObjectFromImportObject()` produces any actual changes.

**Note:** Sync operations are already optimised - they use `MetaverseObjectChangeResult.HasChanges` to conditionally create RPEIs (see `SyncTaskProcessorBase.cs:135`).

### Issue 3: No Filtering on Activity Detail Page

Users cannot filter the RPEI list by change type. For a sync with 10,000 objects, finding the 5 projections among 9,995 attribute flow updates requires pagination through the entire list.

## Proposed Solution

### Phase 1: Extend ObjectChangeType Enum

Update `src/JIM.Models/Enums/ObjectChangeType.cs`:

```csharp
public enum ObjectChangeType
{
    NotSet,

    // Import operations (CSO changes)
    Add,        // CSO added to staging (renamed from Create)
    Update,     // CSO attributes updated
    Delete,     // Object deleted from source system (CSO marked Obsolete internally)

    // Sync operations (MVO changes)
    Projected,      // New MVO created via projection
    Joined,         // CSO joined to existing MVO
    AttributeFlow,  // Attributes flowed to MVO (no join/projection)
    Disconnected,   // CSO disconnected from MVO (out of scope)

    // Export operations
    Provisioned,    // New CSO created in target system
    Exported,       // CSO attributes exported to target system
    Deprovisioned,  // CSO deleted from target system

    // Shared
    NoChange        // No actual changes (export evaluation found CSO current)
}
```

**Note on Delete vs Obsolete:**
- `ObjectChangeType.Delete` is the user-facing change type shown in RPEIs when an object is deleted from the source system
- `ConnectedSystemObjectStatus.Obsolete` remains as an internal CSO status used by the sync processor
- Users see "Delete" which clearly communicates what happened; the internal "Obsolete" state is an implementation detail

### Phase 2: Update Worker Processors

#### 2.1 Import Processor Changes

**File:** `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs`

1. Rename all `ObjectChangeType.Create` assignments to `ObjectChangeType.Add`
2. Change all `ObjectChangeType.Obsolete` assignments to `ObjectChangeType.Delete` for RPEIs
3. Track whether actual changes occurred before adding RPEI to collection
4. Only persist RPEI if:
   - CSO was created (Add)
   - CSO attributes actually changed (Update with changes)
   - CSO was deleted from source (Delete) - internally CSO status remains `Obsolete`
   - An error occurred

**Approach:**
```csharp
// Before processing
var pendingAdditionsBefore = connectedSystemObject?.PendingAttributeValueAdditions.Count ?? 0;
var pendingRemovalsBefore = connectedSystemObject?.PendingAttributeValueRemovals.Count ?? 0;

// After UpdateConnectedSystemObjectFromImportObject()
var hasChanges =
    connectedSystemObject.PendingAttributeValueAdditions.Count > pendingAdditionsBefore ||
    connectedSystemObject.PendingAttributeValueRemovals.Count > pendingRemovalsBefore;

if (hasChanges || isNewCso || isDeleted || hasError)
{
    _activityRunProfileExecutionItems.Add(activityRunProfileExecutionItem);
}
```

#### 2.2 Sync Processor Changes

**File:** `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs`

1. Update `MetaverseObjectChangeResult` to use new enum values:
   - `Projected()` returns `ObjectChangeType.Projected`
   - `Joined()` returns `ObjectChangeType.Joined`
   - `AttributeFlow()` returns `ObjectChangeType.AttributeFlow`

2. Handle CSO disconnection (out of scope) with `ObjectChangeType.Disconnected`

**File:** `src/JIM.Worker/Models/MetaverseObjectChangeResult.cs`

```csharp
public static MetaverseObjectChangeResult Projected(int attributesAdded) => new()
{
    HasChanges = true,
    ChangeType = ObjectChangeType.Projected,
    AttributesAdded = attributesAdded
};

public static MetaverseObjectChangeResult Joined(int attributesAdded = 0, int attributesRemoved = 0) => new()
{
    HasChanges = true,
    ChangeType = ObjectChangeType.Joined,
    AttributesAdded = attributesAdded,
    AttributesRemoved = attributesRemoved
};

public static MetaverseObjectChangeResult AttributeFlow(int attributesAdded, int attributesRemoved) => new()
{
    HasChanges = attributesAdded > 0 || attributesRemoved > 0,
    ChangeType = ObjectChangeType.AttributeFlow,
    AttributesAdded = attributesAdded,
    AttributesRemoved = attributesRemoved
};

public static MetaverseObjectChangeResult Disconnected() => new()
{
    HasChanges = true,
    ChangeType = ObjectChangeType.Disconnected
};
```

#### 2.3 Export Processor Changes

**File:** `src/JIM.Worker/Processors/SyncExportTaskProcessor.cs`

Update the mapping at line 195-200:
```csharp
ObjectChangeType = exportItem.ChangeType switch
{
    PendingExportChangeType.Create => ObjectChangeType.Provisioned,
    PendingExportChangeType.Update => ObjectChangeType.Exported,
    PendingExportChangeType.Delete => ObjectChangeType.Deprovisioned,
    _ => ObjectChangeType.Exported
},
```

### Phase 3: Update Activity Statistics

**File:** `src/JIM.Application/Servers/ActivityServer.cs`

Update `GetActivityRunProfileExecutionStatsAsync()` to return granular statistics:

```csharp
public class ActivityRunProfileExecutionStats
{
    // Import stats
    public int TotalCsoAdds { get; set; }
    public int TotalCsoUpdates { get; set; }
    public int TotalCsoDeletes { get; set; }

    // Sync stats (NEW)
    public int TotalProjections { get; set; }
    public int TotalJoins { get; set; }
    public int TotalAttributeFlows { get; set; }
    public int TotalDisconnections { get; set; }

    // Export stats
    public int TotalProvisioned { get; set; }
    public int TotalExported { get; set; }
    public int TotalDeprovisioned { get; set; }

    // Shared
    public int TotalNoChanges { get; set; }
    public int TotalErrors { get; set; }

    // Legacy (for backward compatibility, deprecated)
    [Obsolete("Use granular stats instead")]
    public int TotalObjectCreates => TotalCsoAdds + TotalProjections + TotalProvisioned;
    [Obsolete("Use granular stats instead")]
    public int TotalObjectUpdates => TotalCsoUpdates + TotalJoins + TotalAttributeFlows + TotalExported;
    [Obsolete("Use granular stats instead")]
    public int TotalObjectDeletes => TotalCsoDeletes + TotalDisconnections + TotalDeprovisioned;
}
```

### Phase 4: Update Activity Detail UI

**File:** `src/JIM.Web/Pages/ActivityDetail.razor`

#### 4.1 Add Change Type Filter Chips

Add a horizontal row of checkbox chips above the RPEI table for quick multi-select filtering:

```razor
<MudChipSet T="ObjectChangeType"
            SelectionMode="SelectionMode.MultiSelection"
            @bind-SelectedValues="_selectedChangeTypes"
            Class="mb-3">
    @foreach (var changeType in GetAvailableChangeTypes())
    {
        <MudChip Value="@changeType"
                 Color="@Helpers.GetRunItemMudBlazorColorForType(changeType)"
                 Variant="Variant.Outlined"
                 SelectedColor="@Helpers.GetRunItemMudBlazorColorForType(changeType)">
            @changeType.ToString().SplitOnCapitalLetters()
            @if (_changeTypeCounts.TryGetValue(changeType, out var count))
            {
                <MudBadge Content="@count" Overlap="false" Class="ml-2" />
            }
        </MudChip>
    }
</MudChipSet>
```

**Benefits of chip-based filtering:**
- All options visible at a glance - no need to open a dropdown
- Multi-select with single clicks
- Colour-coded to match the change type colours in the table
- Badge shows count per type for quick overview
- Context-aware: only shows change types relevant to the run profile type

**Default state:** All change types selected (show everything)

#### 4.2 Update Statistics Display

Show context-appropriate statistics based on run type:

**For Import profiles:**
```
| Adds | Updates | Deletes | Errors |
|  50  |   30    |    5    |   2    |
```

**For Sync profiles:**
```
| Projections | Joins | Attribute Flows | Disconnections | Errors |
|     25      |  50   |      100        |       5        |   2    |
```

**For Export profiles:**
```
| Provisioned | Exported | Deprovisioned | Errors |
|     10      |    45    |       5       |   0    |
```

#### 4.3 Update Server Query

**File:** `src/JIM.Application/Servers/ActivityServer.cs`

Update `GetActivityRunProfileExecutionItemHeadersAsync()` to accept an optional list of change types for filtering:

```csharp
public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
    Guid activityId,
    int page,
    int pageSize,
    string? searchTerm = null,
    string? sortLabel = null,
    bool sortDescending = false,
    List<ObjectChangeType>? changeTypeFilter = null)  // NEW - supports multi-select
```

When `changeTypeFilter` is null or empty, return all items. When populated, filter to only include items matching the selected change types.

### Phase 5: Update UI Helper Methods

**File:** `src/JIM.Web/Helpers.cs`

Update `GetRunItemMudBlazorColorForType()` to handle new change types:

### Phase 6: Hide Obsolete CSOs in Connected System Staging UI

**Problem:** Obsolete CSOs are displayed in the Connected System staging view, which is confusing since these objects no longer exist in the source system. The `Obsolete` status is an internal implementation detail.

**Solution:** Add chip-based status filtering to the CSO list view (consistent with Activity detail filtering).

**File:** `src/JIM.Web/Pages/Admin/ConnectedSystems/ConnectedSystemObjects.razor` (or equivalent)

#### 6.1 Add Status Filter Chips

```razor
<MudChipSet T="ConnectedSystemObjectStatus"
            SelectionMode="SelectionMode.MultiSelection"
            @bind-SelectedValues="_selectedStatuses"
            Class="mb-3">
    <MudChip Value="ConnectedSystemObjectStatus.Normal"
             Color="Color.Success"
             Variant="Variant.Outlined"
             SelectedColor="Color.Success">
        Normal
        @if (_statusCounts.TryGetValue(ConnectedSystemObjectStatus.Normal, out var normalCount))
        {
            <MudBadge Content="@normalCount" Overlap="false" Class="ml-2" />
        }
    </MudChip>
    <MudChip Value="ConnectedSystemObjectStatus.PendingProvisioning"
             Color="Color.Info"
             Variant="Variant.Outlined"
             SelectedColor="Color.Info">
        Pending Provisioning
        @if (_statusCounts.TryGetValue(ConnectedSystemObjectStatus.PendingProvisioning, out var pendingCount))
        {
            <MudBadge Content="@pendingCount" Overlap="false" Class="ml-2" />
        }
    </MudChip>
    <MudChip Value="ConnectedSystemObjectStatus.Obsolete"
             Color="Color.Warning"
             Variant="Variant.Outlined"
             SelectedColor="Color.Warning">
        Pending Deletion
        @if (_statusCounts.TryGetValue(ConnectedSystemObjectStatus.Obsolete, out var obsoleteCount))
        {
            <MudBadge Content="@obsoleteCount" Overlap="false" Class="ml-2" />
        }
    </MudChip>
</MudChipSet>
```

**Note:** Display "Pending Deletion" instead of "Obsolete" - clearer for users.

#### 6.2 Default Filter State

By default, select all statuses **except** `Obsolete`:
```csharp
private IReadOnlyCollection<ConnectedSystemObjectStatus> _selectedStatuses = new List<ConnectedSystemObjectStatus>
{
    ConnectedSystemObjectStatus.Normal,
    ConnectedSystemObjectStatus.PendingProvisioning
};
```

#### 6.3 Update Server Query

**File:** `src/JIM.Application/Servers/ConnectedSystemServer.cs`

Update `GetConnectedSystemObjectsAsync()` to accept an optional status filter:

```csharp
public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(
    Guid connectedSystemId,
    int page,
    int pageSize,
    bool returnAttributes = true,
    List<ConnectedSystemObjectStatus>? statusFilter = null)  // NEW - supports multi-select
```

Add a method to get status counts for the filter badges:

```csharp
public async Task<Dictionary<ConnectedSystemObjectStatus, int>> GetConnectedSystemObjectStatusCountsAsync(
    Guid connectedSystemId)
```

This allows users to view Obsolete CSOs if needed (e.g., for troubleshooting) while keeping the default view clean and intuitive.

```csharp
public static Color GetRunItemMudBlazorColorForType(ObjectChangeType changeType) => changeType switch
{
    // Import (CSO operations)
    ObjectChangeType.Add => Color.Success,
    ObjectChangeType.Update => Color.Info,
    ObjectChangeType.Delete => Color.Error,

    // Sync (MVO operations)
    ObjectChangeType.Projected => Color.Primary,
    ObjectChangeType.Joined => Color.Secondary,
    ObjectChangeType.AttributeFlow => Color.Tertiary,
    ObjectChangeType.Disconnected => Color.Warning,

    // Export
    ObjectChangeType.Provisioned => Color.Success,
    ObjectChangeType.Exported => Color.Info,
    ObjectChangeType.Deprovisioned => Color.Error,

    // Other
    ObjectChangeType.NoChange => Color.Default,
    _ => Color.Default
};
```

## Implementation Phases

### Phase 1: Core Enum Changes
1. Update `ObjectChangeType` enum - rename `Create` to `Add`, remove `Obsolete`
2. Update `MetaverseObjectChangeResult` to use new values
3. Update all worker processors to use new change types
4. Change import processor to use `Delete` instead of `Obsolete` for RPEIs
5. Update unit tests

### Phase 2: Fix Unnecessary RPEI Creation
1. Refactor `SyncImportTaskProcessor.ProcessImportObjectsAsync()`
2. Add change detection before RPEI creation
3. Add unit tests for no-change scenarios
4. Verify with integration tests

### Phase 3: Statistics Improvements
1. Update `ActivityRunProfileExecutionStats` model
2. Update stats calculation query
3. Update Activity detail page statistics display
4. Add unit tests

### Phase 4: Activity Detail UI Filtering
1. Add filter dropdown to Activity detail page
2. Update server-side query to support filtering
3. Update API if used externally
4. Add UI tests if applicable

### Phase 5: UI Helper Updates
1. Update colour mappings for new change types
2. Update any display text helpers

### Phase 6: Connected System Staging UI
1. Add status filter to CSO list view
2. Default to hiding Obsolete CSOs
3. Update server query to support status filtering
4. Ensure users can still view Obsolete CSOs when needed

## Database Migration

No database migration required. The `ObjectChangeType` is stored as an integer, so new enum values will work with existing data. Existing records will retain their original values (0-5) which map to the unchanged enum members.

## Backward Compatibility

- Existing API consumers will continue to work as base enum values (NotSet, Create->Add, Update, Delete, NoChange) retain their integer values
- The `Create` enum value will be renamed to `Add` - this is a breaking change for any code referencing `ObjectChangeType.Create` directly
- The `Obsolete` enum value will be removed from `ObjectChangeType` - existing RPEIs with this value will display as the integer value until re-imported
- Legacy statistics properties marked `[Obsolete]` provide backward compatibility

## Success Criteria

1. Activity detail pages show semantically accurate change types per run profile type
2. Import operations only create RPEIs when actual changes occur
3. Users can filter RPEIs by change type
4. Statistics show granular counts (projections vs joins vs attribute flow)
5. All existing unit tests pass
6. New unit tests cover:
   - Each new change type assignment
   - No-change detection in import
   - Filtering functionality

## Testing Plan

### Unit Tests
- `ObjectChangeType` enum value assignments in each processor
- `MetaverseObjectChangeResult` factory methods return correct types
- Import processor skips RPEI for unchanged CSOs
- Statistics calculation for each change type
- Filter query returns correct results

### Integration Tests
- Full Import -> verify Add/Update/Delete types and counts
- Full Sync -> verify Projected/Joined/AttributeFlow types and counts
- Export -> verify Provisioned/Exported/Deprovisioned types and counts
- No-change scenario -> verify no unnecessary RPEIs created

## Files to Modify

| File | Changes |
|------|---------|
| `src/JIM.Models/Enums/ObjectChangeType.cs` | Add new enum values, rename Create->Add, remove Obsolete |
| `src/JIM.Worker/Models/MetaverseObjectChangeResult.cs` | Update factory methods |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Rename Create->Add, use Delete for deletions, add change detection |
| `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` | Use new sync change types, use Delete for obsolete CSOs |
| `src/JIM.Worker/Processors/SyncExportTaskProcessor.cs` | Use new export change types |
| `src/JIM.Application/Servers/ActivityServer.cs` | Update stats model and query, add change type filter |
| `src/JIM.Application/Servers/ConnectedSystemServer.cs` | Add status filter to CSO query |
| `src/JIM.Web/Pages/ActivityDetail.razor` | Add filter, update stats display |
| `src/JIM.Web/Pages/Admin/ConnectedSystems/ConnectedSystemObjects.razor` | Add status filter, default hide Obsolete |
| `src/JIM.Web/Helpers.cs` | Update colour mapping for new types |
| `test/JIM.Worker.Tests/...` | Add/update unit tests |

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Breaking change for `ObjectChangeType.Create` | Search codebase for all usages, update systematically |
| Performance impact of change detection | Change detection uses in-memory counts, no additional DB queries |
| UI complexity with many filter options | Group filter options by run type context |
| Existing data shows old values | Old integer values still valid, just less granular |

## Benefits

1. **User clarity**: Immediately understand what happened during sync
2. **Reduced database writes**: No RPEIs for unchanged import objects
3. **Better diagnostics**: Distinguish projections from joins
4. **Industry familiarity**: "Add" terminology aligns with traditional ILM conventions
5. **Filtering capability**: Find specific operation types quickly
