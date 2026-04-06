---
title: Schedules
---

# Schedules

The schedule cmdlets manage automated synchronisation schedules in JIM. Schedules define when and how connected system run profiles execute, supporting cron-based, interval, and manual trigger types.

Cmdlets are grouped into four areas: [CRUD operations](#schedule-crud), [control actions](#schedule-control), [step management](#schedule-steps), and [execution monitoring](#schedule-executions).

---

## Schedule CRUD

### Get-JIMSchedule

Retrieves one or more schedules. When called without parameters, returns a paginated list of all schedules (page size 100). When called with `-Id`, returns a single schedule by its identifier.

#### Syntax

```powershell
# List (default)
Get-JIMSchedule [-Name <String>] [-IncludeSteps]

# ById
Get-JIMSchedule -Id <Guid> [-IncludeSteps]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | No | ByValue, ByPropertyName | The unique identifier of the schedule. Alias: `ScheduleId`. |
| `Name` | `String` | No | No | Filters schedules by name. Supports wildcard characters. Only available in the List parameter set. |
| `IncludeSteps` | `Switch` | No | No | Includes step details in the returned schedule objects. |

#### Output

One or more schedule objects. The list parameter set returns results in pages of 100.

#### Examples

```powershell title="List all schedules"
Get-JIMSchedule
```

```powershell title="Find schedules by name pattern"
Get-JIMSchedule -Name "Daily*"
```

```powershell title="Get a specific schedule with its steps"
Get-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeSteps
```

```powershell title="Pipeline from a variable"
$scheduleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
$scheduleId | Get-JIMSchedule -IncludeSteps
```

---

### New-JIMSchedule

Creates a new synchronisation schedule. The parameters required depend on the trigger type and pattern type selected.

Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm creation.

#### Syntax

```powershell
New-JIMSchedule [-Name] <String>
    [-Description <String>]
    -TriggerType <String>
    [-PatternType <String>]
    [-DaysOfWeek <Int32[]>]
    [-RunTimes <String[]>]
    [-IntervalValue <Int32>]
    [-IntervalUnit <String>]
    [-IntervalWindowStart <String>]
    [-IntervalWindowEnd <String>]
    [-CronExpression <String>]
    [-Enabled]
    [-PassThru]
    [-WhatIf] [-Confirm]
```

#### Parameters

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `Name` | `String` | Yes (Position 0) | | The display name for the schedule. |
| `Description` | `String` | No | | A description of the schedule's purpose. |
| `TriggerType` | `String` | Yes | | The trigger mechanism: `Cron` or `Manual`. |
| `PatternType` | `String` | No | `SpecificTimes` | The scheduling pattern: `SpecificTimes`, `Interval`, or `Custom`. |
| `DaysOfWeek` | `Int32[]` | No | | Days the schedule runs. Values 0 (Sunday) through 6 (Saturday). |
| `RunTimes` | `String[]` | No | | Specific times to run, in 24-hour format (e.g. `"06:00"`, `"12:00"`). Used with `SpecificTimes` pattern. |
| `IntervalValue` | `Int32` | No | | The interval frequency, from 1 to 59. Used with `Interval` pattern. |
| `IntervalUnit` | `String` | No | `Hours` | The interval unit: `Hours` or `Minutes`. |
| `IntervalWindowStart` | `String` | No | | Start of the interval window in 24-hour format (e.g. `"08:00"`). |
| `IntervalWindowEnd` | `String` | No | | End of the interval window in 24-hour format (e.g. `"18:00"`). |
| `CronExpression` | `String` | No | | A cron expression for `Custom` pattern or `Cron` trigger type (e.g. `"0 6 * * 1-5"`). |
| `Enabled` | `Switch` | No | | Enables the schedule immediately upon creation. |
| `PassThru` | `Switch` | No | | Returns the created schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the created schedule object.

#### Examples

```powershell title="Create a schedule that runs at specific times on weekdays"
New-JIMSchedule "Weekday Sync" `
    -Description "Synchronise HR data on weekday mornings and evenings" `
    -TriggerType Cron `
    -PatternType SpecificTimes `
    -DaysOfWeek 1, 2, 3, 4, 5 `
    -RunTimes "06:00", "18:00" `
    -Enabled
```

```powershell title="Create an interval-based schedule"
New-JIMSchedule "Frequent AD Sync" `
    -TriggerType Cron `
    -PatternType Interval `
    -IntervalValue 15 `
    -IntervalUnit Minutes `
    -IntervalWindowStart "07:00" `
    -IntervalWindowEnd "19:00" `
    -DaysOfWeek 1, 2, 3, 4, 5
```

```powershell title="Create a schedule using a cron expression"
New-JIMSchedule "Custom Cron Schedule" `
    -TriggerType Cron `
    -PatternType Custom `
    -CronExpression "0 6 * * 1-5" `
    -PassThru
```

```powershell title="Create a manual-only schedule"
New-JIMSchedule "On-Demand Full Sync" `
    -Description "Triggered manually for full resynchronisation" `
    -TriggerType Manual `
    -PassThru
```

---

### Set-JIMSchedule

Updates an existing schedule. Only the parameters you specify are changed; all other properties retain their current values.

Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm changes.

#### Syntax

```powershell
Set-JIMSchedule -Id <Guid>
    [-Name <String>]
    [-Description <String>]
    [-TriggerType <String>]
    [-PatternType <String>]
    [-DaysOfWeek <Int32[]>]
    [-RunTimes <String[]>]
    [-IntervalValue <Int32>]
    [-IntervalUnit <String>]
    [-IntervalWindowStart <String>]
    [-IntervalWindowEnd <String>]
    [-CronExpression <String>]
    [-Steps <Object[]>]
    [-PassThru]
    [-WhatIf] [-Confirm]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the schedule to update. Alias: `ScheduleId`. |
| `Name` | `String` | No | No | Updated display name. |
| `Description` | `String` | No | No | Updated description. |
| `TriggerType` | `String` | No | No | Updated trigger mechanism: `Cron` or `Manual`. |
| `PatternType` | `String` | No | No | Updated scheduling pattern: `SpecificTimes`, `Interval`, or `Custom`. |
| `DaysOfWeek` | `Int32[]` | No | No | Updated days of the week (0-6). |
| `RunTimes` | `String[]` | No | No | Updated run times in 24-hour format. |
| `IntervalValue` | `Int32` | No | No | Updated interval frequency (1-59). |
| `IntervalUnit` | `String` | No | No | Updated interval unit: `Hours` or `Minutes`. |
| `IntervalWindowStart` | `String` | No | No | Updated interval window start time. |
| `IntervalWindowEnd` | `String` | No | No | Updated interval window end time. |
| `CronExpression` | `String` | No | No | Updated cron expression. |
| `Steps` | `Object[]` | No | No | Replaces the entire step list with the provided array. |
| `PassThru` | `Switch` | No | No | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object.

#### Examples

```powershell title="Rename a schedule"
Set-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Name "Updated Schedule Name"
```

```powershell title="Change run times via pipeline"
Get-JIMSchedule -Name "Weekday Sync" | Set-JIMSchedule -RunTimes "07:00", "19:00" -PassThru
```

```powershell title="Switch to interval pattern"
Set-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -PatternType Interval `
    -IntervalValue 30 `
    -IntervalUnit Minutes `
    -IntervalWindowStart "08:00" `
    -IntervalWindowEnd "17:00"
```

!!! note
    `Set-JIMSchedule` merges your updates with the existing schedule configuration. Only the parameters you explicitly provide are modified.

---

### Remove-JIMSchedule

Deletes a schedule permanently. This action cannot be undone.

Supports `ShouldProcess` with high impact; prompts for confirmation by default. Use `-Force` to suppress the confirmation prompt.

#### Syntax

```powershell
Remove-JIMSchedule -Id <Guid> [-Force] [-WhatIf] [-Confirm]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the schedule to delete. Alias: `ScheduleId`. |
| `Force` | `Switch` | No | No | Suppresses the confirmation prompt. |

#### Output

None.

#### Examples

```powershell title="Remove a schedule with confirmation"
Remove-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Remove without confirmation"
Remove-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Force
```

```powershell title="Remove multiple schedules via pipeline"
Get-JIMSchedule -Name "Temp*" | Remove-JIMSchedule -Force
```

---

## Schedule Control

### Enable-JIMSchedule

Enables a schedule so it will execute according to its configured trigger.

#### Syntax

```powershell
Enable-JIMSchedule -Id <Guid> [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the schedule to enable. Alias: `ScheduleId`. |
| `PassThru` | `Switch` | No | No | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object.

#### Examples

```powershell title="Enable a schedule"
Enable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Enable and verify"
Enable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -PassThru |
    Select-Object Name, Enabled
```

---

### Disable-JIMSchedule

Disables a schedule, preventing it from executing on its configured trigger. The schedule configuration is preserved and can be re-enabled later.

#### Syntax

```powershell
Disable-JIMSchedule -Id <Guid> [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the schedule to disable. Alias: `ScheduleId`. |
| `PassThru` | `Switch` | No | No | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object.

#### Examples

```powershell title="Disable a schedule"
Disable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Disable all schedules matching a pattern"
Get-JIMSchedule -Name "Legacy*" | Disable-JIMSchedule -PassThru
```

---

### Start-JIMSchedule

Manually triggers a schedule execution regardless of its trigger type or enabled state. This is useful for testing schedules or running on-demand synchronisations.

#### Syntax

```powershell
Start-JIMSchedule -Id <Guid> [-Wait] [-Timeout <TimeSpan>] [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the schedule to trigger. Alias: `ScheduleId`. |
| `Wait` | `Switch` | No | No | Waits for the execution to complete, polling every 5 seconds with progress output. |
| `Timeout` | `TimeSpan` | No | No | Maximum time to wait when `-Wait` is specified. Default: 30 minutes. |
| `PassThru` | `Switch` | No | No | Returns the execution object. |

#### Output

None by default. When `-PassThru` is specified, returns the schedule execution object.

#### Examples

```powershell title="Trigger a schedule and return immediately"
Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Trigger and wait for completion"
Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Wait
```

```powershell title="Trigger with a custom timeout"
Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -Wait -Timeout ([TimeSpan]::FromMinutes(60)) -PassThru
```

```powershell title="Trigger a disabled schedule on demand"
# Start-JIMSchedule works regardless of the schedule's enabled state
Get-JIMSchedule -Name "On-Demand Full Sync" | Start-JIMSchedule -Wait
```

!!! note
    When `-Wait` is used and the timeout is reached, the cmdlet issues a warning but the execution continues server-side. Use `Get-JIMScheduleExecution` or `Stop-JIMScheduleExecution` to monitor or cancel it.

---

## Schedule Steps

### Add-JIMScheduleStep

Adds a new step to a schedule. Each step defines a run profile to execute against a connected system. Steps run sequentially by default; use `-Parallel` to run a step concurrently with the preceding step.

#### Syntax

```powershell
# ById (default)
Add-JIMScheduleStep -ScheduleId <Guid>
    -StepType <String>
    -ConnectedSystemId <Int32>
    -RunProfileId <Int32>
    [-Parallel]
    [-ContinueOnFailure]
    [-PassThru]

# ByName
Add-JIMScheduleStep -ScheduleId <Guid>
    -StepType <String>
    -ConnectedSystemName <String>
    -RunProfileName <String>
    [-Parallel]
    [-ContinueOnFailure]
    [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Parameter Set | Description |
|---|---|---|---|---|
| `ScheduleId` | `Guid` | Yes | Both | The schedule to add the step to. Alias: `Id`. |
| `StepType` | `String` | Yes | Both | The type of step. Currently only `RunProfile` is supported. |
| `ConnectedSystemId` | `Int32` | Yes | ById | The numeric identifier of the connected system. |
| `ConnectedSystemName` | `String` | Yes | ByName | The name of the connected system. |
| `RunProfileId` | `Int32` | Yes | ById | The numeric identifier of the run profile to execute. |
| `RunProfileName` | `String` | Yes | ByName | The name of the run profile to execute. |
| `Parallel` | `Switch` | No | Both | Runs this step in parallel with the previous step. |
| `ContinueOnFailure` | `Switch` | No | Both | Continues to the next step even if this step fails. |
| `PassThru` | `Switch` | No | Both | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object with the new step included.

#### Examples

```powershell title="Add a step by connected system and run profile IDs"
Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepType RunProfile `
    -ConnectedSystemId 1 `
    -RunProfileId 3
```

```powershell title="Add a step by name"
Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepType RunProfile `
    -ConnectedSystemName "Active Directory" `
    -RunProfileName "Delta Import"
```

```powershell title="Add a parallel step that continues on failure"
Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepType RunProfile `
    -ConnectedSystemName "HR System" `
    -RunProfileName "Delta Import" `
    -Parallel `
    -ContinueOnFailure `
    -PassThru
```

```powershell title="Build a multi-step schedule"
$scheduleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

# Step 0: Import from HR
Add-JIMScheduleStep -ScheduleId $scheduleId -StepType RunProfile `
    -ConnectedSystemName "HR System" -RunProfileName "Full Import"

# Step 1: Import from AD (parallel with step 0)
Add-JIMScheduleStep -ScheduleId $scheduleId -StepType RunProfile `
    -ConnectedSystemName "Active Directory" -RunProfileName "Full Import" `
    -Parallel

# Step 2: Synchronise HR
Add-JIMScheduleStep -ScheduleId $scheduleId -StepType RunProfile `
    -ConnectedSystemName "HR System" -RunProfileName "Full Sync"

# Step 3: Export to AD
Add-JIMScheduleStep -ScheduleId $scheduleId -StepType RunProfile `
    -ConnectedSystemName "Active Directory" -RunProfileName "Export" `
    -PassThru
```

---

### Remove-JIMScheduleStep

Removes a step from a schedule by its zero-based index. After removal, remaining steps are automatically renumbered.

Supports `ShouldProcess` with high impact. Use `-Force` to suppress the confirmation prompt.

#### Syntax

```powershell
Remove-JIMScheduleStep -ScheduleId <Guid> -StepIndex <Int32> [-Force] [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `ScheduleId` | `Guid` | Yes | ByValue, ByPropertyName | The schedule to remove the step from. Alias: `Id`. |
| `StepIndex` | `Int32` | Yes | No | The zero-based index of the step to remove. |
| `Force` | `Switch` | No | No | Suppresses the confirmation prompt. |
| `PassThru` | `Switch` | No | No | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object.

#### Examples

```powershell title="Remove the first step"
Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -StepIndex 0
```

```powershell title="Remove a step without confirmation and view the result"
Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepIndex 2 -Force -PassThru
```

!!! note
    Remaining steps are renumbered after removal. If you remove step 1 from a schedule with steps 0, 1, 2, the former step 2 becomes step 1.

---

## Schedule Executions

### Get-JIMScheduleExecution

Retrieves schedule execution records. Use this to monitor running executions, review execution history, or check the status of a specific execution.

#### Syntax

```powershell
# List (default)
Get-JIMScheduleExecution [-ScheduleId <Guid>] [-Status <String>]

# ById
Get-JIMScheduleExecution -Id <Guid>

# Active
Get-JIMScheduleExecution [-ScheduleId <Guid>] -Active
```

#### Parameters

| Parameter | Type | Required | Pipeline | Parameter Set | Description |
|---|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | ById | The unique identifier of the execution. Alias: `ExecutionId`. |
| `ScheduleId` | `Guid` | No | ByPropertyName | List, Active | Filters executions to a specific schedule. |
| `Status` | `String` | No | No | List | Filters by execution status. Valid values: `Queued`, `InProgress`, `Completed`, `Failed`, `Cancelled`. |
| `Active` | `Switch` | Yes | No | Active | Returns only currently active executions (queued or in progress). |

#### Output

One or more schedule execution objects.

#### Examples

```powershell title="List all executions"
Get-JIMScheduleExecution
```

```powershell title="Get a specific execution"
Get-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890"
```

```powershell title="List failed executions for a schedule"
Get-JIMScheduleExecution -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Status Failed
```

```powershell title="Show all active executions"
Get-JIMScheduleExecution -Active
```

```powershell title="Check active executions for a specific schedule"
Get-JIMSchedule -Name "Weekday Sync" | Get-JIMScheduleExecution -Active
```

---

### Stop-JIMScheduleExecution

Cancels a running or queued schedule execution. This sends a cancellation request to the server.

Supports `ShouldProcess` with high impact. Use `-Force` to suppress the confirmation prompt.

#### Syntax

```powershell
Stop-JIMScheduleExecution -Id <Guid> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByValue, ByPropertyName | The unique identifier of the execution to cancel. Alias: `ExecutionId`. |
| `Force` | `Switch` | No | No | Suppresses the confirmation prompt. |
| `PassThru` | `Switch` | No | No | Returns the updated execution object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated execution object.

#### Examples

```powershell title="Stop a specific execution"
Stop-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890"
```

```powershell title="Stop without confirmation"
Stop-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890" -Force
```

```powershell title="Stop all active executions for a schedule"
Get-JIMScheduleExecution -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Active |
    Stop-JIMScheduleExecution -Force
```

---

## See also

- [Schedules API](../api/schedules/index.md): REST API reference for schedule endpoints
- [Run Profiles](run-profiles.md): Managing connected system run profiles
- [Activities](activities.md): Viewing synchronisation activity results
