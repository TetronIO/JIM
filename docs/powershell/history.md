---
title: History
---

# History

Cmdlets for querying configuration change history, querying deleted objects, and managing change history retention. These cmdlets provide access to the configuration audit trail, the audit trail for deleted objects, and let you control how long change history records are kept.

---

## Get-JIMConfigurationChangeHistory

Retrieves the recorded configuration changes for a Synchronisation Rule or Connected System. Every create, update, and delete is captured as a complete, versioned snapshot carried on its Activity, so you can see exactly what changed, when, and who changed it. Three retrieval modes are supported: a paged summary list (default), a single version with its diff against the previous version (`-Version`), and a comparison of any two versions (`-CompareFrom` / `-CompareTo`). Sensitive values (for example encrypted Connected System settings) are never returned; a changed secret is reported only as changed, never by value.

### Syntax

```powershell
# Paged summary list (default)
Get-JIMConfigurationChangeHistory -Type <string> -Id <int>
    [-Page <int>] [-PageSize <int>]

# Stream every version
Get-JIMConfigurationChangeHistory -Type <string> -Id <int> -All [-PageSize <int>]

# A single version, with its diff against the previous version
Get-JIMConfigurationChangeHistory -Type <string> -Id <int> -Version <int> [-AsDiff] [-Raw]

# Compare any two versions
Get-JIMConfigurationChangeHistory -Type <string> -Id <int>
    -CompareFrom <int> -CompareTo <int> [-AsDiff] [-Raw]
```

### Parameters

| Name | Type | Required | Default | Parameter Set | Description |
|------|------|----------|---------|---------------|-------------|
| `Type` | `string` | Yes | | All | The configuration object kind. Valid values: `SynchronisationRule`, `ConnectedSystem`. |
| `Id` | `int` | Yes | | All | The ID of the configuration object. Accepts the `id` property from the pipeline, so a piped Synchronisation Rule or Connected System binds automatically. |
| `Page` | `int` | No | `1` | Page | Page number for the summary list. |
| `PageSize` | `int` | No | `50` | Page, All | Items per page. Maximum: `100`. |
| `All` | `switch` | No | | All | Automatically paginate through, and return, every change-history entry. |
| `Version` | `int` | Yes | | Version | Retrieve a single change by its per-object version number, returning the snapshot and the diff against the previous version. |
| `CompareFrom` | `int` | Yes | | Compare | The earlier version to compare from. |
| `CompareTo` | `int` | Yes | | Compare | The later version to compare to. |
| `AsDiff` | `switch` | No | | Version, Compare | Render the change as a git-style coloured diff (using `$PSStyle`) instead of returning the structured object. |
| `Raw` | `switch` | No | | Version, Compare | Return the underlying structured change object. This is the default; the switch is provided for explicitness. |

### Output

In the summary modes, returns one `PSCustomObject` per change with `version`, `operation`, `initiatedBy`, `when`, `reason`, and a one-line `summary`. With `-Version`, returns the change detail (metadata, the redacted snapshot, and the diff against the previous version). With `-CompareFrom` / `-CompareTo`, returns the structured diff. With `-AsDiff`, returns the rendered diff as coloured strings.

### Examples

```powershell title="List the most recent changes for a Synchronisation Rule"
Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5
```

```powershell title="Return every recorded change for a Connected System"
Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -All
```

```powershell title="Show one version as a git-style coloured diff"
Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -Version 7 -AsDiff
```

```powershell title="Pipe a Synchronisation Rule in and show its latest change"
Get-JIMSyncRule -Name "HR Inbound" |
    Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Version 7 -AsDiff
```

```powershell title="Compare two versions"
Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5 -CompareFrom 6 -CompareTo 8 -AsDiff
```

!!! note "Recording a reason"
    To attach a reason to a change so it appears in this history, pass `-ChangeReason` to the write cmdlets: `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`, `New-JIMConnectedSystem`, and `Set-JIMConnectedSystem`.

---

## Get-JIMDeletedObject

Retrieves deleted objects from the audit trail. Supports filtering by object type, date range, and search terms, with paginated results for efficient browsing of large deletion histories.

### Syntax

```powershell
# Get deleted Metaverse Objects (default)
Get-JIMDeletedObject [-ObjectType <string>] [-MetaverseObjectTypeId <int>]
    [-Search <string>] [-FromDate <DateTime>] [-ToDate <DateTime>]
    [-Page <int>] [-PageSize <int>]

# Get deleted Connected System Objects
Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId <int>
    [-Search <string>] [-FromDate <DateTime>] [-ToDate <DateTime>]
    [-Page <int>] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectType` | `string` | No | `MVO` | Type of deleted object to retrieve. Valid values: `MVO` (Metaverse Objects), `CSO` (Connected System Objects). |
| `ConnectedSystemId` | `int` | No | | Filters deleted CSOs by Connected System. Only applicable when `ObjectType` is `CSO`. |
| `MetaverseObjectTypeId` | `int` | No | | Filters deleted MVOs by Metaverse Object Type. Only applicable when `ObjectType` is `MVO`. |
| `Search` | `string` | No | | Search term to filter results. Searches `ExternalId` for CSOs and `DisplayName` for MVOs. |
| `FromDate` | `DateTime` | No | | Start of the deletion date range (UTC). |
| `ToDate` | `DateTime` | No | | End of the deletion date range (UTC). |
| `Page` | `int` | No | `1` | Page number for paginated results. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `1000`. |

### Output

Returns a `PSCustomObject` with paginated results containing `items`, `totalCount`, `page`, and `pageSize` properties. Each item represents a deleted object with its attributes at the time of deletion.

### Examples

```powershell title="Get all deleted Metaverse Objects"
Get-JIMDeletedObject
```

```powershell title="Get deleted Connected System Objects"
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

```powershell title="Get deleted CSOs for a specific Connected System"
Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId 5
```

```powershell title="Paginate through large result sets"
Get-JIMDeletedObject -Page 3 -PageSize 100
```

---

## Get-JIMHistoryCount

Gets the count of change history records for a Connected System. Useful for monitoring history growth and planning cleanup operations.

### Syntax

```powershell
# By Connected System ID (default)
Get-JIMHistoryCount -ConnectedSystemId <int>

# By Connected System name
Get-JIMHistoryCount -ConnectedSystemName <string>
```

### Parameters

| Name | Type | Required | Default | Parameter Set | Description |
|------|------|----------|---------|---------------|-------------|
| `ConnectedSystemId` | `int` | Yes | | ById (default) | ID of the Connected System. Alias: `Id`. Accepts pipeline input. |
| `ConnectedSystemName` | `string` | Yes | | ByName | Name of the Connected System. |

### Output

Returns a `PSCustomObject` with `connectedSystemId`, `connectedSystemName`, and `changeRecordCount` properties.

### Examples

```powershell title="Get history count by ID"
Get-JIMHistoryCount -ConnectedSystemId 3
```

```powershell title="Get history count by name"
Get-JIMHistoryCount -ConnectedSystemName "Active Directory"
```

```powershell title="Get history counts for all Connected Systems"
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
| `csoChangesDeleted` | `int` | Number of Connected System Object change records deleted. |
| `mvoChangesDeleted` | `int` | Number of Metaverse Object change records deleted. |
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

- [API reference](../api/index.md): the Scalar API reference (linked from the API index) covers the history endpoints
- [Activities](activities.md): cmdlets for querying activity logs and execution history
- [Activities concept](../configuration/activities.md): how configuration changes are carried on Activities, including the redaction model
