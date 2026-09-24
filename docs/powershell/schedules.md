---
title: Schedules
---

# Schedules

The schedule cmdlets manage automated synchronisation schedules in JIM. Schedules define when and how Connected System Run Profiles execute, supporting cron-based, interval, and manual trigger types.

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

Every object carries the Schedule's configuration: `Id`, `Name`, `Description`, `TriggerType`, `PatternType`, `CronExpression`, `IsEnabled`, `LastRunTime`, `NextRunTime`, `StepCount`, and `OnStepFailure`, what the Schedule does when a step fails (`Stop` or `Continue`; see [When a step fails](#when-a-step-fails)).

The list parameter set also carries the outcome of the Schedule's most recent run:

| Property | Type | Description |
|---|---|---|
| `LastExecutionId` | `Guid` | The most recent Schedule Execution. Pass it to `Get-JIMScheduleExecution -Id` for the per-step detail. |
| `LastExecutionStatus` | `String` | How that run ended: `Queued`, `InProgress`, `Complete`, `CompleteWithError`, `Failed` or `Cancelled`. `CompleteWithError` means the run reached its end, but one or more steps failed and were set to let the Schedule continue. |
| `LastExecutionCurrentStepIndex` | `Int32` | The step the run reached, 0-based. Read with `LastExecutionTotalSteps` to see how far a failed run got. |
| `LastExecutionTotalSteps` | `Int32` | How many steps the run set out to execute. |
| `LastExecutionFailedStepIndices` | `Int32[]` | For a `CompleteWithError` run, the steps (0-based, each listed once) that failed and let the Schedule continue. Empty for any other outcome. |
| `LastExecutionCompletedAt` | `DateTime` | When the run finished (UTC). Empty while it is still running. |
| `LastExecutionErrorMessage` | `String` | The error that stopped the run, where one did; for a `CompleteWithError` run, the message naming each failed step. |

These are empty for a Schedule that has never run. `-Id` returns the configuration and steps only, with these fields empty; `-IncludeSteps` returns both.

With `-Id` or `-IncludeSteps`, each object also carries `Steps`, one per step (`Id`, `StepIndex`, `Name`, `StepType`, `ExecutionMode`, `ConnectedSystemId`, `RunProfileId`, `TimeoutSeconds`, and the type-specific fields), including how each step behaves when it fails:

| Property | Type | Description |
|---|---|---|
| `OnFailure` | `String` | The step's own setting: `FollowSchedule` (the Schedule's `OnStepFailure` decides), `Stop` or `Continue`. |
| `ContinueOnFailure` | `Boolean` | What the step will actually do if it fails: `True` when the Schedule carries on past it. This is the step's own setting, or the Schedule's when the step follows the Schedule. |
| `FailureBehaviourSource` | `String` | Where `ContinueOnFailure` comes from: `Step` (the step has a setting of its own) or `Schedule` (it follows the Schedule). |

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

```powershell title="Report the schedules whose last run failed"
Get-JIMSchedule |
    Where-Object { $_.LastExecutionStatus -eq 'Failed' } |
    Select-Object Name, LastRunTime, LastExecutionCurrentStepIndex, LastExecutionTotalSteps, LastExecutionErrorMessage
```

```powershell title="Report the Schedules whose last run carried on past a failed step"
Get-JIMSchedule |
    Where-Object { $_.LastExecutionStatus -eq 'CompleteWithError' } |
    Select-Object Name, LastRunTime, LastExecutionFailedStepIndices, LastExecutionErrorMessage
```

```powershell title="See how each step of a Schedule behaves when it fails"
(Get-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890").Steps |
    Select-Object StepIndex, Name, OnFailure, ContinueOnFailure, FailureBehaviourSource
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
    [-OnStepFailure <String>]
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
| `OnStepFailure` | `String` | No | `Stop` | What the Schedule does when a step fails: `Stop` or `Continue`. Steps follow it unless they have a setting of their own. See [When a step fails](#when-a-step-fails). |
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

```powershell title="Create a Schedule that carries on past a failed step"
New-JIMSchedule "Nightly HR sync" `
    -TriggerType Cron `
    -PatternType SpecificTimes `
    -RunTimes "02:00" `
    -OnStepFailure Continue
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
    [-OnStepFailure <String>]
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
| `OnStepFailure` | `String` | No | No | What the Schedule does when a step fails: `Stop` or `Continue`. Omit to leave it unchanged. A built-in Schedule, such as Temporal Scope Reconciliation, always stops; JIM refuses a change to it. See [When a step fails](#when-a-step-fails). |
| `Steps` | `Object[]` | No | No | Replaces the entire step list with the provided array. Give each step its failure setting as `OnFailure`: a step object that carries `OnFailure` ignores its `ContinueOnFailure`, so to change one step's setting, use [`Set-JIMScheduleStep`](#set-jimschedulestep) or change the step's `OnFailure`. |
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

```powershell title="Let a Schedule carry on past failed steps"
Set-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -OnStepFailure Continue
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
    Select-Object Name, IsEnabled
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
| `Wait` | `Switch` | No | No | Waits for the Schedule Execution to finish (`Complete`, `CompleteWithError`, `Failed` or `Cancelled`), polling every 5 seconds with progress output. |
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

Adds a new step to a schedule. Each step defines a Run Profile to execute against a Connected System. Steps run sequentially by default; use `-Parallel` to run a step concurrently with the preceding step.

#### Syntax

```powershell
# ById (default)
Add-JIMScheduleStep -ScheduleId <Guid>
    -StepType <String>
    -ConnectedSystemId <Int32>
    -RunProfileId <Int32>
    [-Parallel]
    [-OnFailure <String> | -ContinueOnFailure]
    [-PassThru]

# ByName
Add-JIMScheduleStep -ScheduleId <Guid>
    -StepType <String>
    -ConnectedSystemName <String>
    -RunProfileName <String>
    [-Parallel]
    [-OnFailure <String> | -ContinueOnFailure]
    [-PassThru]
```

#### Parameters

| Parameter | Type | Required | Parameter Set | Description |
|---|---|---|---|---|
| `ScheduleId` | `Guid` | Yes | Both | The schedule to add the step to. Alias: `Id`. |
| `StepType` | `String` | Yes | Both | The type of step. Currently only `RunProfile` is supported. |
| `ConnectedSystemId` | `Int32` | Yes | ById | The numeric identifier of the Connected System. |
| `ConnectedSystemName` | `String` | Yes | ByName | The name of the Connected System. |
| `RunProfileId` | `Int32` | Yes | ById | The numeric identifier of the Run Profile to execute. |
| `RunProfileName` | `String` | Yes | ByName | The name of the Run Profile to execute. |
| `Parallel` | `Switch` | No | Both | Runs this step in parallel with the previous step. |
| `OnFailure` | `String` | No | Both | What the step does when it fails: `FollowSchedule` (the default; the Schedule's `OnStepFailure` decides), `Stop` or `Continue`. See [When a step fails](#when-a-step-fails). |
| `ContinueOnFailure` | `Switch` | No | Both | Shorthand for `-OnFailure Continue`. Supplying both `-OnFailure` and `-ContinueOnFailure` is an error, and nothing is changed. |
| `PassThru` | `Switch` | No | Both | Returns the updated schedule object. |

#### Output

None by default. When `-PassThru` is specified, returns the updated schedule object with the new step included.

Adding a step changes no other step: the existing steps are sent back with their failure settings exactly as they were.

#### Examples

```powershell title="Add a step by Connected System and Run Profile IDs"
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

```powershell title="Add an export step that stops the Schedule if it fails"
Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepType RunProfile `
    -ConnectedSystemName "Active Directory" `
    -RunProfileName "Export" `
    -OnFailure Stop
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

### Set-JIMScheduleStep

Changes what one step does when it fails, leaving every other step, and the Schedule itself, as they are. To change a step's other settings, send the whole step list through `Set-JIMSchedule -Steps`.

Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the change.

#### Syntax

```powershell
Set-JIMScheduleStep -ScheduleId <Guid> -StepId <Guid> -OnFailure <String>
    [-ChangeReason <String>]
    [-PassThru]
    [-WhatIf] [-Confirm]
```

#### Parameters

| Parameter | Type | Required | Pipeline | Description |
|---|---|---|---|---|
| `ScheduleId` | `Guid` | Yes | ByPropertyName | The Schedule the step belongs to. Alias: `Id`, so a Schedule piped from `Get-JIMSchedule` binds here. |
| `StepId` | `Guid` | Yes | No | The step to change: its `Id` in the Schedule's `Steps` (`Get-JIMSchedule -Id`). |
| `OnFailure` | `String` | Yes | No | What the step does when it fails: `FollowSchedule` (the Schedule's `OnStepFailure` decides), `Stop` or `Continue`. |
| `ChangeReason` | `String` | No | No | A reason for the change, recorded in the Schedule's change history. |
| `PassThru` | `Switch` | No | No | Returns the updated Schedule object. |

The steps of a built-in Schedule, such as Temporal Scope Reconciliation, always follow their Schedule; JIM refuses a change to them.

#### Output

None by default. When `-PassThru` is specified, returns the updated Schedule object, in the same shape as `Get-JIMSchedule -Id`.

If the Schedule has no step with that `StepId`, the cmdlet reports an error and changes nothing.

#### Examples

```powershell title="Make one step stop the Schedule, even when the Schedule carries on"
$schedule = Get-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
$schedule.Steps | Select-Object Id, StepIndex, Name, OnFailure, ContinueOnFailure, FailureBehaviourSource
Set-JIMScheduleStep -ScheduleId $schedule.Id -StepId $schedule.Steps[2].Id -OnFailure Stop
```

```powershell title="Return a step to the Schedule's setting, with a reason"
Get-JIMSchedule -Name "Nightly HR sync" |
    Set-JIMScheduleStep -StepId "c3d4e5f6-a7b8-9012-cdef-123456789012" -OnFailure FollowSchedule `
        -ChangeReason "Back to the Schedule's setting (CHG0102)"
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
    Remaining steps are renumbered after removal. If you remove step 1 from a Schedule with steps 0, 1, 2, the former step 2 becomes step 1. Each remaining step keeps its `Id` and its failure setting.

---

## When a step fails

Each Schedule has an `OnStepFailure` setting, `Stop` (the default) or `Continue`, and each step has an `OnFailure` setting that either follows the Schedule (`FollowSchedule`, the default for new steps) or overrides it (`Stop` or `Continue`). JIM reads the setting when the step fails, so a change applies to the next failure, including in a run already under way.

- **The step stops the Schedule**<br /> The remaining steps do not run, and the Schedule Execution ends `Failed`.
- **The step lets the Schedule continue**<br /> The remaining steps run. When the Schedule Execution reaches its end it is `CompleteWithError`, not `Complete`, and its `ErrorMessage` names each failed step, so a monitoring script can tell it from a clean run.

| To | Use |
|---|---|
| Set the Schedule's behaviour | `New-JIMSchedule -OnStepFailure` or `Set-JIMSchedule -OnStepFailure` |
| Set a new step's behaviour | `Add-JIMScheduleStep -OnFailure` |
| Change an existing step's behaviour | `Set-JIMScheduleStep -OnFailure` |
| See what each step will do | `(Get-JIMSchedule -Id <Guid>).Steps`: `OnFailure`, `ContinueOnFailure`, `FailureBehaviourSource` |

What counts as a step failing, and how the settings appear in the portal, are covered in [Schedules](../configuration/schedules.md).

---

## Schedule Executions

### Get-JIMScheduleExecution

Retrieves schedule execution records. Use this to monitor running executions, review execution history, or check the status of a specific execution.

#### Syntax

```powershell
# List (default)
Get-JIMScheduleExecution [-ScheduleId <Guid>] [-InputObject <PSCustomObject>] [-Status <String>]

# ById
Get-JIMScheduleExecution -Id <Guid>

# Active
Get-JIMScheduleExecution [-ScheduleId <Guid>] [-InputObject <PSCustomObject>] -Active
```

#### Parameters

| Parameter | Type | Required | Pipeline | Parameter Set | Description |
|---|---|---|---|---|---|
| `Id` | `Guid` | Yes | ByPropertyName | ById | The unique identifier of the execution. Alias: `ExecutionId`. |
| `ScheduleId` | `Guid` | No | ByPropertyName | List, Active | Filters executions to a specific schedule. |
| `InputObject` | `PSCustomObject` | No | ByValue | List, Active | A Schedule object from the pipeline (e.g. from `Get-JIMSchedule`); its `Id` is used to filter executions, equivalent to specifying `-ScheduleId` directly. |
| `Status` | `String` | No | No | List | Filters by execution status. Valid values: `Queued`, `InProgress`, `Complete`, `CompleteWithError`, `Failed`, `Cancelled`. |
| `Active` | `Switch` | Yes | No | Active | Returns only currently active executions (queued or in progress). |

#### Output

One or more Schedule Execution objects. Every shape carries `Status` (`Queued`, `InProgress`, `Complete`, `CompleteWithError`, `Failed`, `Cancelled` or `Paused`), `ErrorMessage` (what stopped a `Failed` run, or the failed steps a `CompleteWithError` run carried on past), and `StepDisplay`, the step group the execution has reached as one sentence, matching what the portal shows above the Schedule's tasks in **Admin > Operations > Queue**.

`-Id` returns the detail shape, which adds a `Progress` block:

| Property | Description |
|----------|-------------|
| `StepDisplay` | `Step 2 of 5: 2 in parallel` from the detail shape, which knows what the step is called; `Step 2 of 5` from the list and active shapes, which carry only the position. |
| `Progress.CurrentStepNumber` | The step group being run, 1-based. |
| `Progress.TotalSteps` | How many step groups the Schedule has. Steps that run concurrently are one group, so a Schedule of six steps where two run together is five steps long. |
| `Progress.Steps` | One entry per step group, each with its `StepIndex`, `Name`, `Status` (`Pending`, `Running`, `Completed`, `Failed` or `Cancelled`), `IsParallel`, and `TaskStatuses`, every concurrent task's own outcome. |
| `Steps` | Unchanged: one entry per Schedule Step *row*, naming it and carrying its type, timings, errors (`ErrorMessage`) and Activity id. A step group that runs three Run Profiles concurrently appears here three times and in `Progress.Steps` once. |
| `Steps.CancellationReason` | Why a `Cancelled` step did not run, such as `Not run: an earlier step stopped the Schedule.`; empty for any other step, including one that was already running when it was cancelled. |
| `Steps.ContinueOnFailure` | Whether the Schedule Execution carries on past this step if it fails: the step's own setting, or its Schedule's when it follows the Schedule. |
| `Steps.FailureBehaviourSource` | Where `Steps.ContinueOnFailure` comes from: `Step` or `Schedule`. |

#### Examples

```powershell title="List all executions"
Get-JIMScheduleExecution
```

```powershell title="See how far each running Schedule has got"
Get-JIMScheduleExecution -Active | Select-Object ScheduleName, StepDisplay
```

```powershell title="Find a parallel step where one task failed and another did not"
$execution = Get-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890"
$execution.Progress.Steps | Where-Object { $_.IsParallel -and $_.TaskStatuses -contains 'Failed' }
```

```powershell title="Get a specific execution"
Get-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890"
```

```powershell title="See why each step of an execution did not run"
$execution = Get-JIMScheduleExecution -Id "f1e2d3c4-b5a6-7890-abcd-ef1234567890"
$execution.Steps | Where-Object { $_.CancellationReason } | Select-Object StepIndex, Name, CancellationReason
```

```powershell title="List failed executions for a schedule"
Get-JIMScheduleExecution -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Status Failed
```

```powershell title="List executions that carried on past a failed step"
Get-JIMScheduleExecution -Status CompleteWithError | Select-Object ScheduleName, CompletedAt, ErrorMessage
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

- [Schedules](../configuration/schedules.md): what schedules are, trigger types and patterns, step types, and execution behaviour
- [Run Profiles](run-profiles.md): Managing Connected System Run Profiles
- [Activities](activities.md): Viewing synchronisation activity results
