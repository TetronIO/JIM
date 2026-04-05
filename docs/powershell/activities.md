---
title: Activities
---

# Activities

Activity cmdlets retrieve and inspect the execution history of operations within JIM. Activities track all operations: synchronisation runs, data generation, certificate management, and other administrative actions. Use these cmdlets to review activity logs, retrieve execution statistics, and inspect child activities spawned by parent operations.

---

## Get-JIMActivity

Retrieves activity history from JIM. Activities are created automatically whenever an operation runs, providing a full audit trail of synchronisation runs, data generation tasks, certificate management operations, and other administrative actions.

### Syntax

```powershell
# List recent activities (default)
Get-JIMActivity [-Search <string>] [-Page <int>] [-PageSize <int>]

# Get a specific activity by ID
Get-JIMActivity -Id <guid>

# Get execution items for a run profile activity
Get-JIMActivity -Id <guid> -ExecutionItems
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById, ExecutionItems sets) | | ID of the activity to retrieve. Alias: `ActivityId`. Accepts pipeline input by property name. |
| `Search` | `string` | No (List set) | | Filters activities by target name or type. For example, searching for "Active Directory" returns activities related to that connected system. |
| `Page` | `int` | No (List set) | `1` | Page number for paginated results. |
| `PageSize` | `int` | No (List set) | `20` | Number of activities per page. |
| `ExecutionItems` | `switch` | Yes (ExecutionItems set) | | Retrieves the run profile execution items (RPEIs) associated with the activity, providing detailed per-object processing results. |

### Output

When using the **List** or **ById** parameter sets, returns one or more `PSCustomObject` instances representing activities, each containing properties such as `Id`, `Name`, `Type`, `Status`, `StartTime`, `EndTime`, and `TargetName`.

When using the **ExecutionItems** parameter set, returns `PSCustomObject` instances representing individual execution items, each containing properties such as `ObjectType`, `ObjectName`, `Operation`, `Status`, and `ErrorDetails`.

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

```powershell title="Get execution items for a run profile activity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems
```

```powershell title="Pipeline from Start-JIMRunProfile"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId
```

```powershell title="Review errors in execution items"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
    Where-Object { $_.Status -eq "Error" }
```

---

## Get-JIMActivityStats

Retrieves execution statistics for a run profile activity, including counts of processed items, errors, and timing information. This is useful for monitoring synchronisation health and identifying performance trends.

### Syntax

```powershell
Get-JIMActivityStats -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the activity to retrieve statistics for. Alias: `ActivityId`. Accepts pipeline input by property name. |

### Output

Returns a `PSCustomObject` containing execution statistics with properties such as `TotalObjects`, `SuccessCount`, `ErrorCount`, `WarningCount`, `Duration`, and timing breakdowns.

### Examples

```powershell title="Get statistics by activity ID"
Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityStats
```

```powershell title="Get stats after a run profile completes"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
Get-JIMActivityStats -Id $result.ActivityId
```

```powershell title="Check for errors after execution"
$result = Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru
$stats = Get-JIMActivityStats -Id $result.ActivityId
if ($stats.ErrorCount -gt 0) {
    Write-Warning "Sync completed with $($stats.ErrorCount) errors"
    Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
        Where-Object { $_.Status -eq "Error" }
}
```

---

## Get-JIMActivityChildren

Retrieves child activities spawned by a parent activity. For example, a schedule execution activity creates child activities for each individual run profile step within that schedule. This cmdlet is useful for drilling into multi-step operations to inspect each step independently.

### Syntax

```powershell
Get-JIMActivityChildren -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the parent activity whose children to retrieve. Accepts pipeline input by property name. |

### Output

Returns one or more `PSCustomObject` instances representing child activities, each containing the same properties as a standard activity object: `Id`, `Name`, `Type`, `Status`, `StartTime`, `EndTime`, and `TargetName`.

### Examples

```powershell title="Get child activities by parent ID"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityChildren
```

```powershell title="Inspect each step of a schedule execution"
Get-JIMActivity -Search "Nightly Sync Schedule" |
    Select-Object -First 1 |
    Get-JIMActivityChildren |
    Format-Table Name, Status, StartTime, EndTime
```

```powershell title="Find failed child activities"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Where-Object { $_.Status -eq "Failed" }
```

```powershell title="Get statistics for each child activity"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    ForEach-Object { Get-JIMActivityStats -Id $_.Id }
```

---

## See also

- [API Activities](../api/activities/index.md): REST API reference for activity endpoints
- [Run Profiles](run-profiles.md): cmdlets for managing and executing synchronisation run profiles
- [Schedules](schedules.md): cmdlets for managing automated run profile schedules
