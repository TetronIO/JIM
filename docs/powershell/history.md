---
title: History
---

# History

Cmdlets for querying deleted objects and managing change history retention. These cmdlets provide access to the audit trail for deleted objects and allow you to control how long change history records are kept.

---

## Get-JIMDeletedObject

Retrieves deleted objects from the audit trail. Supports filtering by object type, date range, and search terms, with paginated results for efficient browsing of large deletion histories.

### Syntax

```powershell
# Get deleted metaverse objects (default)
Get-JIMDeletedObject [-ObjectType <string>] [-MetaverseObjectTypeId <int>]
    [-Search <string>] [-FromDate <DateTime>] [-ToDate <DateTime>]
    [-Page <int>] [-PageSize <int>]

# Get deleted connected system objects
Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId <int>
    [-Search <string>] [-FromDate <DateTime>] [-ToDate <DateTime>]
    [-Page <int>] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectType` | `string` | No | `MVO` | Type of deleted object to retrieve. Valid values: `MVO` (metaverse objects), `CSO` (connected system objects). |
| `ConnectedSystemId` | `int` | No | | Filters deleted CSOs by connected system. Only applicable when `ObjectType` is `CSO`. |
| `MetaverseObjectTypeId` | `int` | No | | Filters deleted MVOs by metaverse object type. Only applicable when `ObjectType` is `MVO`. |
| `Search` | `string` | No | | Search term to filter results. Searches `ExternalId` for CSOs and `DisplayName` for MVOs. |
| `FromDate` | `DateTime` | No | | Start of the deletion date range (UTC). |
| `ToDate` | `DateTime` | No | | End of the deletion date range (UTC). |
| `Page` | `int` | No | `1` | Page number for paginated results. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `1000`. |

### Output

Returns a `PSCustomObject` with paginated results containing `items`, `totalCount`, `page`, and `pageSize` properties. Each item represents a deleted object with its attributes at the time of deletion.

### Examples

```powershell title="Get all deleted metaverse objects"
Get-JIMDeletedObject
```

```powershell title="Get deleted connected system objects"
Get-JIMDeletedObject -ObjectType CSO
```

```powershell title="Search deleted MVOs by display name"
Get-JIMDeletedObject -Search "John Smith"
```

```powershell title="Search deleted CSOs by external ID"
Get-JIMDeletedObject -ObjectType CSO -Search "CN=jsmith"
```

```powershell title="Filter by date range"
Get-JIMDeletedObject -FromDate "2026-01-01" -ToDate "2026-03-31"
```

```powershell title="Get deleted CSOs for a specific connected system"
Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId 5
```

```powershell title="Paginate through large result sets"
Get-JIMDeletedObject -Page 3 -PageSize 100
```

---

## Get-JIMHistoryCount

Gets the count of change history records for a connected system. Useful for monitoring history growth and planning cleanup operations.

### Syntax

```powershell
# By connected system ID (default)
Get-JIMHistoryCount -ConnectedSystemId <int>

# By connected system name
Get-JIMHistoryCount -ConnectedSystemName <string>
```

### Parameters

| Name | Type | Required | Default | Parameter Set | Description |
|------|------|----------|---------|---------------|-------------|
| `ConnectedSystemId` | `int` | Yes | | ById (default) | ID of the connected system. Alias: `Id`. Accepts pipeline input. |
| `ConnectedSystemName` | `string` | Yes | | ByName | Name of the connected system. |

### Output

Returns a `PSCustomObject` with `connectedSystemId`, `connectedSystemName`, and `changeRecordCount` properties.

### Examples

```powershell title="Get history count by ID"
Get-JIMHistoryCount -ConnectedSystemId 3
```

```powershell title="Get history count by name"
Get-JIMHistoryCount -ConnectedSystemName "Active Directory"
```

```powershell title="Get history counts for all connected systems"
Get-JIMConnectedSystem | Get-JIMHistoryCount
```

```powershell title="Find systems with the most history records"
Get-JIMConnectedSystem |
    Get-JIMHistoryCount |
    Sort-Object changeRecordCount -Descending |
    Format-Table connectedSystemName, changeRecordCount
```

---

## Invoke-JIMHistoryCleanup

Manually triggers change history cleanup based on the configured retention policy. Deletes expired CSO changes, MVO changes, and activities older than the configured retention period.

### Syntax

```powershell
Invoke-JIMHistoryCleanup [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PassThru` | `switch` | No | | Returns cleanup statistics instead of producing no output. |

### Output

When `PassThru` is specified, returns a `PSCustomObject` with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `csoChangesDeleted` | `int` | Number of connected system object change records deleted. |
| `mvoChangesDeleted` | `int` | Number of metaverse object change records deleted. |
| `activitiesDeleted` | `int` | Number of activity records deleted. |
| `oldestRecordDeleted` | `DateTime` | Timestamp of the oldest record removed. |
| `newestRecordDeleted` | `DateTime` | Timestamp of the newest record removed. |
| `cutoffDate` | `DateTime` | The calculated cutoff date based on retention policy. |
| `retentionPeriodDays` | `int` | The configured retention period in days. |
| `batchSize` | `int` | Maximum number of records processed per invocation. |

Without `PassThru`, produces no output.

!!! note "Batch Size Limitation"
    Each invocation processes up to the configured batch size. For environments with large volumes of expired history, you may need to call this cmdlet multiple times or rely on automatic housekeeping, which runs every 60 seconds. Each cleanup invocation creates an audit activity.

### Examples

```powershell title="Run a basic cleanup"
Invoke-JIMHistoryCleanup
```

```powershell title="Run cleanup and view statistics"
Invoke-JIMHistoryCleanup -PassThru
```

```powershell title="Batch cleanup loop for large backlogs"
do {
    $result = Invoke-JIMHistoryCleanup -PassThru
    $total = $result.csoChangesDeleted + $result.mvoChangesDeleted + $result.activitiesDeleted
    Write-Host "Deleted $total records (cutoff: $($result.cutoffDate))"
} while ($total -gt 0)
```

---

## See also

- [API History](../api/history.md): REST API reference for history endpoints
- [Activities](activities.md): cmdlets for querying activity logs and execution history
