---
title: History
---

# History

Cmdlets for querying configuration change history, querying deleted objects, and managing change history retention. These cmdlets provide access to the configuration audit trail, the audit trail for deleted objects, and let you control how long change history records are kept.

---

## Get-JIMConfigurationChangeHistory

Retrieves the recorded configuration changes for a Synchronisation Rule, Connected System, Schedule, Service Setting, Metaverse Object Type, Metaverse Attribute, Trusted Certificate, API Key, Role, Predefined Search, Connector Definition, Example Data Set, or Example Data Template. Every create, update, and delete is captured as a complete, versioned snapshot carried on its Activity, so you can see exactly what changed, when, and who changed it. Three retrieval modes are supported: a paged summary list (default), a single version with its diff against the previous version (`-Version`), and a comparison of any two versions (`-CompareFrom` / `-CompareTo`). Sensitive values (for example encrypted Connected System settings) are never returned; a changed secret is reported only as changed, never by value. An API Key's history stores only its metadata and Role assignments; the key secret is never returned in any form, not even as a hash. A Role's history covers both its definition and its static membership, so each member add or remove appears as its own version. A Predefined Search's history also covers its criteria groups and criteria: each add, edit, or removal rolls up into a version on the owning search. A Connector Definition's history covers its capabilities, setting definitions, and files, so a capability or setting shipped in an updated connector appears as its own version; a file's contents are fingerprinted by hash, never returned. An Example Data Set's history records only its metadata and value count (never the individual values), and an Example Data Template's history covers its object-type and attribute configuration; a template's data-generation runs are a separate operational activity, not part of its configuration history.

### Syntax

```powershell
# Paged summary list (default)
Get-JIMConfigurationChangeHistory -Type <string> -Id <int|guid|string>
    [-Page <int>] [-PageSize <int>]

# Stream every version
Get-JIMConfigurationChangeHistory -Type <string> -Id <int|guid|string> -All [-Force] [-PageSize <int>]

# A single version, with its diff against the previous version
Get-JIMConfigurationChangeHistory -Type <string> -Id <int|guid|string> -Version <int> [-AsDiff] [-Raw]

# Compare any two versions
Get-JIMConfigurationChangeHistory -Type <string> -Id <int|guid|string>
    -CompareFrom <int> -CompareTo <int> [-AsDiff] [-Raw]
```

### Parameters

| Name | Type | Required | Default | Parameter Set | Description |
|------|------|----------|---------|---------------|-------------|
| `Type` | `string` | Yes | | All | The configuration object kind. Valid values: `SynchronisationRule`, `ConnectedSystem`, `Schedule`, `ServiceSetting`, `MetaverseObjectType`, `MetaverseAttribute`, `TrustedCertificate`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`, `ExampleDataTemplate`. |
| `Id` | `int`, `guid` or `string` | Yes | | All | The ID of the configuration object: an integer for a Synchronisation Rule, Connected System, Metaverse Object Type, Metaverse Attribute, Role, Predefined Search, Connector Definition, Example Data Set, or Example Data Template; a GUID for a Schedule, Trusted Certificate, or API Key; the dot-notation setting key for a Service Setting. Accepts the `id` property from the pipeline, so a piped object binds automatically. |
| `Page` | `int` | No | `1` | Page | Page number for the summary list. |
| `PageSize` | `int` | No | `50` | Page, All | Items per page. Maximum: `100`. |
| `All` | `switch` | No | | All | Automatically paginate through, and return, every change-history entry. Fetches at most 1000 pages and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No | | All | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |
| `Version` | `int` | Yes | | Version | Retrieve a single change by its per-object version number, returning the snapshot and the diff against the previous version. |
| `CompareFrom` | `int` | Yes | | Compare | The earlier version to compare from. |
| `CompareTo` | `int` | Yes | | Compare | The later version to compare to. |
| `AsDiff` | `switch` | No | | Version, Compare | Render the change as a git-style coloured diff (using `$PSStyle`) instead of returning the structured object. |
| `Raw` | `switch` | No | | Version, Compare | Return the underlying structured change object. This is the default; the switch is provided for explicitness. |

### Output

In the summary modes, returns one `PSCustomObject` per change with `Version`, `Operation`, `InitiatedByName`, `When`, `Reason`, and a one-line `Summary`. With `-Version`, returns the change detail (metadata, the redacted snapshot, and the diff against the previous version). With `-CompareFrom` / `-CompareTo`, returns the structured diff. With `-AsDiff`, returns the rendered diff as coloured strings.

### Examples

```powershell title="List the most recent changes for a Synchronisation Rule"
Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5
```

```powershell title="Return every recorded change for a Connected System"
Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -All
```

```powershell title="List the recorded changes for a Schedule (GUID-keyed)"
Get-JIMSchedule -Name "Nightly Sync" | Get-JIMConfigurationChangeHistory -Type Schedule
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

```powershell title="List the recorded changes for a Service Setting (string-keyed)"
Get-JIMConfigurationChangeHistory -Type ServiceSetting -Id 'History.RetentionPeriod'
```

```powershell title="Pipe a Metaverse Attribute in and list its recorded changes"
Get-JIMMetaverseAttribute -Name 'Email' | Get-JIMConfigurationChangeHistory -Type MetaverseAttribute
```

```powershell title="List the recorded changes for a Trusted Certificate (GUID-keyed)"
Get-JIMCertificate | Get-JIMConfigurationChangeHistory -Type TrustedCertificate
```

```powershell title="List the recorded changes for an API Key (GUID-keyed)"
Get-JIMApiKey | Get-JIMConfigurationChangeHistory -Type ApiKey
```

```powershell title="List the recorded changes for a Role (int-keyed), covering its definition and membership"
Get-JIMRole -Name "Administrator" | Get-JIMConfigurationChangeHistory -Type Role
```

```powershell title="List the recorded changes for a Predefined Search (int-keyed), covering the search and its criteria"
Get-JIMPredefinedSearch -Uri people | Get-JIMConfigurationChangeHistory -Type PredefinedSearch
```

```powershell title="List the recorded changes for a Connector Definition (int-keyed), covering its capabilities, settings and files"
Get-JIMConfigurationChangeHistory -Type ConnectorDefinition -Id 3
```

```powershell title="List the recorded changes for an Example Data Set (int-keyed)"
Get-JIMConfigurationChangeHistory -Type ExampleDataSet -Id 5
```

!!! note "Recording a reason"
    To attach a reason to a change so it appears in this history, pass `-ChangeReason` to the write cmdlets: `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, `Set-JIMServiceSetting`, `Reset-JIMServiceSetting`, `New-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `New-JIMMetaverseAttribute`, `Set-JIMMetaverseAttribute`, `Remove-JIMMetaverseAttribute`, `Add-JIMCertificate`, `Set-JIMCertificate`, `Remove-JIMCertificate`, `New-JIMApiKey`, `Set-JIMApiKey`, `Remove-JIMApiKey`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, `Set-JIMPredefinedSearch`, and `New-JIMPredefinedSearchCriteriaGroup`.

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

Returns a `PSCustomObject` with paginated results containing `Items`, `TotalCount`, `Page`, and `PageSize` properties. Each item represents a deleted object with its attributes at the time of deletion.

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

Returns a `PSCustomObject` with `ConnectedSystemId`, `ConnectedSystemName`, and `ChangeRecordCount` properties.

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
    Sort-Object ChangeRecordCount -Descending |
    Format-Table ConnectedSystemName, ChangeRecordCount
```

---

## Invoke-JIMHistoryCleanup

Runs a history retention pass on demand. Removes history that has had the retention period set for its kind: Connected System Object and Metaverse Object change history, configuration change previews, Activities, initial-password records, and Pending Password Changes that reached a terminal state. Records still being worked are never removed, however old.

This also runs on its own, daily, on the built-in [History Retention Cleanup Schedule](../configuration/schedules.md#built-in-schedules). Use this cmdlet to run a pass now, or to drain a large backlog faster than one pass a day.

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
| `CsoChangesDeleted` | `int` | Number of Connected System Object change records deleted. |
| `MvoChangesDeleted` | `int` | Number of Metaverse Object change records deleted. |
| `PreviewsDeleted` | `int` | Number of configuration change previews whose results were cleared. |
| `ActivitiesDeleted` | `int` | Number of general Activity records deleted, under the general retention period. |
| `ConfigurationChangeActivitiesDeleted` | `int` | Configuration change Activities deleted, under their own retention period. |
| `SecurityEventActivitiesDeleted` | `int` | Security event Activities deleted, under their own retention period. |
| `InitialPasswordWorkRecordsDeleted` | `int` | Terminal initial-password records deleted, under their own retention period. |
| `PasswordEventActivitiesDeleted` | `int` | Password Synchronisation Activities deleted, under their own retention period. |
| `PasswordQueueRecordsDeleted` | `int` | Terminal Pending Password Changes deleted. Also the number of encrypted passwords JIM stopped holding. |
| `OldestRecordDeleted` | `DateTime` | Timestamp of the oldest record removed. |
| `NewestRecordDeleted` | `DateTime` | Timestamp of the newest record removed. |
| `CutoffDate` | `DateTime` | The cutoff used for the general retention period. |
| `RetentionPeriodDays` | `int` | The configured general retention period in days. |
| `ConfigurationChangeRetentionPeriodDays` | `int` | The configured configuration change retention period in days. |
| `SecurityEventRetentionPeriodDays` | `int` | The configured security event retention period in days. |
| `InitialPasswordRetentionPeriodDays` | `int` | The configured initial-password record retention period in days. |
| `PasswordEventRetentionPeriodDays` | `int` | The configured Password Synchronisation retention period in days. |
| `BatchSize` | `int` | Maximum number of records of any one kind processed per invocation. |

Without `PassThru`, produces no output.

!!! note "Batch Size Limitation"
    Each invocation removes at most `History.CleanupBatchSize` records of any one kind. For environments with a large backlog, call this cmdlet repeatedly, or leave the daily Schedule to work through it over successive runs. Each invocation creates an audit Activity saying what it removed.

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
    $total = $result.CsoChangesDeleted + $result.MvoChangesDeleted + $result.ActivitiesDeleted
    Write-Host "Deleted $total records (cutoff: $($result.CutoffDate))"
} while ($total -gt 0)
```

```powershell title="Check what a pass removed from Password Synchronisation history"
Invoke-JIMHistoryCleanup -PassThru |
    Select-Object PasswordEventActivitiesDeleted, PasswordQueueRecordsDeleted, PasswordEventRetentionPeriodDays
```

---

## See also

- [API reference](../api/index.md): the Scalar API reference (linked from the API index) covers the history endpoints
- [Activities](activities.md): cmdlets for querying activity logs and execution history
- [Activities concept](../configuration/activities.md): how configuration changes are carried on Activities, including the redaction model
- [Schedules](../configuration/schedules.md#built-in-schedules): the built-in History Retention Cleanup Schedule that runs a retention pass daily
- [Service settings](../administration/configuration.md#service-settings): the `History.*` retention periods each kind of record is governed by
