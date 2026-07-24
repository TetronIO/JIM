---
title: Activities
---

# Activities

Activity cmdlets retrieve and inspect the execution history of operations within JIM. Activities track all operations: synchronisation runs, data generation, certificate management, and other administrative actions. Example data generation is now its own distinct **Data Generation** activity type, separate from configuration changes to an Example Data Template. Use these cmdlets to review activity logs, retrieve execution statistics, and inspect child activities spawned by parent operations.

---

## Get-JIMActivity

Retrieves activity history from JIM. Activities are created automatically whenever an operation runs, providing a full audit trail of synchronisation runs, data generation tasks, certificate management operations, and other administrative actions.

### Syntax

```powershell
# List recent activities (default)
Get-JIMActivity [-Search <string>] [-Page <int>] [-PageSize <int>]

# Get a specific activity by ID
Get-JIMActivity -Id <guid>

# Get execution items for a Run Profile activity
Get-JIMActivity -Id <guid> -ExecutionItems

# Follow an in-progress activity's live progress until it completes
Get-JIMActivity -Id <guid> -Follow [-IntervalSeconds <int>] [-MaxPolls <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById, ExecutionItems, Follow sets) | | ID of the activity to retrieve. Alias: `ActivityId`. Accepts pipeline input by property name. |
| `Search` | `string` | No (List set) | | Filters activities by target name or type. For example, searching for "Active Directory" returns activities related to that Connected System. |
| `Page` | `int` | No (List set) | `1` | Page number for paginated results. |
| `PageSize` | `int` | No (List set) | `20` | Number of activities per page. |
| `ExecutionItems` | `switch` | Yes (ExecutionItems set) | | Retrieves the Run Profile execution items (RPEIs) associated with the activity, providing detailed per-object processing results. |
| `Follow` | `switch` | Yes (Follow set) | | Follows the activity's live progress (like `tail -f`): renders a progress bar with the current phase, object counts, throughput and estimated time remaining until the activity completes. Press Ctrl+C to stop early. |
| `IntervalSeconds` | `int` | No (Follow set) | `2` | Polling interval in seconds when following. Range 1-300. |
| `MaxPolls` | `int` | No (Follow set) | | Maximum number of progress polls before following stops, whether or not the activity has completed. Useful for scripts that must not block indefinitely. |

### Output

When using the **List** or **ById** parameter sets, returns one or more `PSCustomObject` instances representing activities, each containing properties such as `Id`, `Created`, `Executed`, `Status`, `TargetType`, `TargetOperationType`, `TargetName`, `InitiatedByName`, and per-run totals such as `TotalErrors`.

When using the **ExecutionItems** parameter set, returns `PSCustomObject` instances representing individual execution items, each containing properties such as `ExternalIdValue`, `DisplayName`, `ConnectedSystemObjectType`, `ObjectChangeType`, `ErrorType`, and `OutcomeSummary`.

When using the **Follow** parameter set, progress renders to the host while following; when following ends, the final activity object is emitted (the same shape as **ById**).

### Examples

```powershell title="List recent activities"
Get-JIMActivity
```

```powershell title="List activities with pagination"
Get-JIMActivity -Page 2 -PageSize 50
```

```powershell title="Search for activities by target name"
Get-JIMActivity -Search "Active Directory"
```

```powershell title="Search for activities by type"
Get-JIMActivity -Search "FullImport"
```

```powershell title="Get a specific activity by ID"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get execution items for a Run Profile activity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems
```

```powershell title="Pipeline from Start-JIMRunProfile"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId
```

```powershell title="Follow an in-progress activity until it completes"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow
```

```powershell title="Follow with a bounded duration for scripts"
# Polls every 5 seconds, for at most 60 polls (5 minutes)
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow -IntervalSeconds 5 -MaxPolls 60
```

```powershell title="Review errors in execution items"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
    Where-Object { $null -ne $_.ErrorType }
```

---

## Get-JIMActivityStats

Retrieves execution statistics for a Run Profile activity, including counts of processed items, errors, and timing information. This is useful for monitoring synchronisation health and identifying performance trends.

### Syntax

```powershell
Get-JIMActivityStats -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the activity to retrieve statistics for. Alias: `ActivityId`. Accepts pipeline input by property name. |

### Output

Returns a `PSCustomObject` containing execution statistics with properties such as `TotalObjectsProcessed`, `TotalObjectChangeCount`, `TotalUnchanged`, `TotalObjectErrors`, and `TotalObjectTypes`, plus per-operation breakdowns (`TotalCsoAdds`, `TotalJoins`, `TotalAttributeFlows`, `TotalExported`, and similar).

### Examples

```powershell title="Get statistics by activity ID"
Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityStats
```

```powershell title="Get stats after a Run Profile completes"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivityStats -Id $result.ActivityId
```

```powershell title="Check for errors after execution"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
$stats = Get-JIMActivityStats -Id $result.ActivityId
if ($stats.TotalObjectErrors -gt 0) {
    Write-Warning "Sync completed with $($stats.TotalObjectErrors) errors"
    Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
        Where-Object { $null -ne $_.ErrorType }
}
```

---

## Get-JIMActivityChildren

Retrieves child activities spawned by a parent activity. For example, a schedule execution activity creates child activities for each individual Run Profile step within that schedule. This cmdlet is useful for drilling into multi-step operations to inspect each step independently.

The underlying API returns a paginated response envelope, but this cmdlet unwraps it internally and still emits one object per child activity to the pipeline, so existing scripts that pipe its output are unaffected by the paginated response shape.

### Syntax

```powershell
# Get a page of child activities (default)
Get-JIMActivityChildren -Id <guid> [-Page <int>] [-PageSize <int>]

# Get every child activity, paginating automatically
Get-JIMActivityChildren -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the parent activity whose children to retrieve. Accepts pipeline input by property name. |
| `Page` | `int` | No (Page set) | `1` | Page number for the child activity list. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of child activities per page. Maximum is 100. |
| `All` | `switch` | Yes (All set) | | Automatically paginates through all child activities and returns every one. Cannot be used with `-Page`. Fetches at most 1000 pages and then stops with a warning; use `-Force` to fetch beyond the cap. |
| `Force` | `switch` | No (All set) | | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |

### Output

Returns one or more `PSCustomObject` instances representing child activities, each containing the same properties as a standard activity object: `Id`, `Created`, `Executed`, `Status`, `TargetType`, `TargetOperationType`, and `TargetName`.

### Examples

```powershell title="Get the first page of child activities by parent ID"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get every child activity, paginating automatically"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityChildren
```

```powershell title="Inspect each step of a schedule execution"
Get-JIMActivity -Search "Nightly Sync Schedule" |
    Select-Object -First 1 |
    Get-JIMActivityChildren -All |
    Format-Table TargetName, Status, Created, Executed
```

```powershell title="Find failed child activities"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All |
    Where-Object { $_.Status -eq "FailedWithError" }
```

```powershell title="Get statistics for each child activity"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All |
    ForEach-Object { Get-JIMActivityStats -Id $_.Id }
```

---

## See also

- [Activities](../configuration/activities.md): what activities are, lifecycle, summary statistics, and parent/child execution model
- [Run Profiles](run-profiles.md): cmdlets for managing and executing synchronisation Run Profiles
- [Schedules](schedules.md): cmdlets for managing automated Run Profile schedules
