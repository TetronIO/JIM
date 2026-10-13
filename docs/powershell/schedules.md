---
title: Schedules
description: "The cmdlets that create, change and remove Schedules and their steps, and turn Schedules on and off."
type: reference
audience: automation-author
---

# Schedules

The Schedule cmdlets manage when JIM runs Run Profiles on its own: create a Schedule with a trigger and a pattern, give it steps, and turn it on or off. To run a Schedule now, or to watch and stop its runs, see [Schedule Executions](schedule-executions.md). What Schedules are, and their trigger types, patterns and step types, are covered in [Schedules](../configuration/schedules.md).

---

## Get-JIMSchedule

Retrieves one or more Schedules. Without parameters, returns every Schedule; with `-Id`, returns one.

<!-- BEGIN GENERATED: Get-JIMSchedule -->
### Syntax

```powershell
# List (default)
Get-JIMSchedule [-Name <string>] [-IncludeSteps]

# ById
Get-JIMSchedule -Id <guid> [-IncludeSteps]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of a specific Schedule to retrieve. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `Name` | `string` | No (List) |  | Returns the Schedules whose name matches. Supports wildcards. |
| `IncludeSteps` | `switch` | No |  | Returns each Schedule in full, steps included. |

### Output

The list returns each Schedule's configuration and the outcome of its last run. `-Id` returns the Schedule in full, with its steps, but leaves the last-run fields empty; `-IncludeSteps` returns each listed Schedule in full and keeps them.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the Schedule. |
| `Name` | `string` | The Schedule's name. |
| `Description` | `string` | What the Schedule does, in its author's words. Empty when it has no description. |
| `TriggerType` | `string` | How the Schedule starts: `Cron` runs it automatically on its pattern; `Manual` runs it only when it is started. One of `Cron` or `Manual`. |
| `CronExpression` | `string` | The cron expression the Schedule runs on; empty for a `Manual` Schedule. JIM builds it from the pattern fields, except for a `Custom` pattern, which gives it directly. |
| `PatternType` | `string` | How a `Cron` Schedule is timed: at set times of day (`SpecificTimes`), every few hours or minutes (`Interval`), or by a cron expression of its own (`Custom`). One of `SpecificTimes`, `Interval` or `Custom`. |
| `DaysOfWeek` | `string` | The days the Schedule runs, as a comma-separated list of numbers from 0 (Sunday) to 6 (Saturday), for example `1,2,3,4,5` for weekdays. A `Custom` pattern does not use it. |
| `RunTimes` | `string` | For a `SpecificTimes` pattern, the times of day the Schedule runs, as a comma-separated list of 24-hour times, for example `06:00,18:00`. |
| `IntervalValue` | `int` | For an `Interval` pattern, how often the Schedule runs, counted in IntervalUnit: 2 with `Hours` runs it every two hours. |
| `IntervalUnit` | `string` | For an `Interval` pattern, the unit of IntervalValue. One of `Minutes` or `Hours`. |
| `IntervalWindowStart` | `string` | For an hourly `Interval` pattern, the start of the window the Schedule runs in, as a 24-hour time such as `06:00`. Empty when the interval runs all day. |
| `IntervalWindowEnd` | `string` | For an hourly `Interval` pattern, the end of the window the Schedule runs in, as a 24-hour time such as `18:00`. Empty when the interval runs all day. |
| `IsEnabled` | `bool` | Whether the Schedule is enabled. |
| `OnStepFailure` | `string` | What the Schedule does when a step fails, for every step that follows the Schedule: `Stop` ends the run (Failed); `Continue` runs the remaining steps, and a run that carried on past a failure ends `CompleteWithError`. A step with a failure setting of its own overrides it. One of `Stop` or `Continue`. |
| `LastRunTime` | `datetime` | When the Schedule last ran (UTC). |
| `NextRunTime` | `datetime` | When the Schedule is next due to run (UTC). |
| `StepCount` | `int` | The number of steps in the Schedule. |
| `Created` | `datetime` | When the Schedule was created (UTC). |
| `LastUpdated` | `datetime` | When the Schedule was last changed (UTC). |
| `LastExecutionId` | `guid` | The unique identifier of the most recent Schedule Execution, if the Schedule has ever run. The LastExecution fields are filled in only when Schedules are listed: reading a single Schedule, or creating, changing, enabling or disabling one, leaves them all empty, because that describes the Schedule's configuration rather than its run history. The Schedule Executions hold the full history. |
| `LastExecutionStatus` | `string` | The outcome of the most recent Schedule Execution. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). One of `Queued`, `InProgress`, `Complete`, `Failed`, `Cancelled`, `Paused` or `CompleteWithError`. |
| `LastExecutionCurrentStepIndex` | `int` | The step the most recent Schedule Execution reached (0-based). Read with LastExecutionTotalSteps to see how far a failed run got before it stopped. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionTotalSteps` | `int` | How many steps the most recent Schedule Execution set out to run. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionFailedStepIndices` | `int[]` | The steps (0-based, ascending, each listed once) that failed in the most recent Schedule Execution and let it carry on, when that execution is `CompleteWithError`; empty for any other outcome. Empty where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionCompletedAt` | `datetime` | When the most recent Schedule Execution finished (UTC). Empty while it is still running, when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionErrorMessage` | `string` | The error reported by the most recent Schedule Execution, if it failed, or the message naming each failed step when it is `CompleteWithError`. Empty when the run succeeded, when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |

**With `-Id` or `-IncludeSteps`**:

| Property | Type | Description |
|----------|------|-------------|
| `Steps` | `object[]` | The Schedule's steps, in the order they run (by StepIndex). Each has `Id`, `StepIndex`, `Name`, `ExecutionMode`, `StepType`, `OnFailure`, `ContinueOnFailure`, `FailureBehaviourSource`, `TimeoutSeconds`, `ConnectedSystemId`, `RunProfileId`, `ScriptPath`, `ExecutablePath`, `WorkingDirectory`, `Arguments`, `SqlConnectionString` and `SqlScriptPath`. |
| `Id` | `guid` | The unique identifier of the Schedule. |
| `Name` | `string` | The Schedule's name. |
| `Description` | `string` | What the Schedule does, in its author's words. Empty when it has no description. |
| `TriggerType` | `string` | How the Schedule starts: `Cron` runs it automatically on its pattern; `Manual` runs it only when it is started. One of `Cron` or `Manual`. |
| `CronExpression` | `string` | The cron expression the Schedule runs on; empty for a `Manual` Schedule. JIM builds it from the pattern fields, except for a `Custom` pattern, which gives it directly. |
| `PatternType` | `string` | How a `Cron` Schedule is timed: at set times of day (`SpecificTimes`), every few hours or minutes (`Interval`), or by a cron expression of its own (`Custom`). One of `SpecificTimes`, `Interval` or `Custom`. |
| `DaysOfWeek` | `string` | The days the Schedule runs, as a comma-separated list of numbers from 0 (Sunday) to 6 (Saturday), for example `1,2,3,4,5` for weekdays. A `Custom` pattern does not use it. |
| `RunTimes` | `string` | For a `SpecificTimes` pattern, the times of day the Schedule runs, as a comma-separated list of 24-hour times, for example `06:00,18:00`. |
| `IntervalValue` | `int` | For an `Interval` pattern, how often the Schedule runs, counted in IntervalUnit: 2 with `Hours` runs it every two hours. |
| `IntervalUnit` | `string` | For an `Interval` pattern, the unit of IntervalValue. One of `Minutes` or `Hours`. |
| `IntervalWindowStart` | `string` | For an hourly `Interval` pattern, the start of the window the Schedule runs in, as a 24-hour time such as `06:00`. Empty when the interval runs all day. |
| `IntervalWindowEnd` | `string` | For an hourly `Interval` pattern, the end of the window the Schedule runs in, as a 24-hour time such as `18:00`. Empty when the interval runs all day. |
| `IsEnabled` | `bool` | Whether the Schedule is enabled. |
| `OnStepFailure` | `string` | What the Schedule does when a step fails, for every step that follows the Schedule: `Stop` ends the run (Failed); `Continue` runs the remaining steps, and a run that carried on past a failure ends `CompleteWithError`. A step with a failure setting of its own overrides it. One of `Stop` or `Continue`. |
| `LastRunTime` | `datetime` | When the Schedule last ran (UTC). |
| `NextRunTime` | `datetime` | When the Schedule is next due to run (UTC). |
| `StepCount` | `int` | The number of steps in the Schedule. |
| `Created` | `datetime` | When the Schedule was created (UTC). |
| `LastUpdated` | `datetime` | When the Schedule was last changed (UTC). |
| `LastExecutionId` | `guid` | The unique identifier of the most recent Schedule Execution, if the Schedule has ever run. The LastExecution fields are filled in only when Schedules are listed: reading a single Schedule, or creating, changing, enabling or disabling one, leaves them all empty, because that describes the Schedule's configuration rather than its run history. The Schedule Executions hold the full history. |
| `LastExecutionStatus` | `string` | The outcome of the most recent Schedule Execution. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). One of `Queued`, `InProgress`, `Complete`, `Failed`, `Cancelled`, `Paused` or `CompleteWithError`. |
| `LastExecutionCurrentStepIndex` | `int` | The step the most recent Schedule Execution reached (0-based). Read with LastExecutionTotalSteps to see how far a failed run got before it stopped. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionTotalSteps` | `int` | How many steps the most recent Schedule Execution set out to run. Empty when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionFailedStepIndices` | `int[]` | The steps (0-based, ascending, each listed once) that failed in the most recent Schedule Execution and let it carry on, when that execution is `CompleteWithError`; empty for any other outcome. Empty where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionCompletedAt` | `datetime` | When the most recent Schedule Execution finished (UTC). Empty while it is still running, when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
| `LastExecutionErrorMessage` | `string` | The error reported by the most recent Schedule Execution, if it failed, or the message naming each failed step when it is `CompleteWithError`. Empty when the run succeeded, when the Schedule has never run, or where the LastExecution fields are not filled in (see LastExecutionId). |
<!-- END GENERATED: Get-JIMSchedule -->

### Examples

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

## New-JIMSchedule

Creates a Schedule. Which parameters it needs depends on the trigger type and the pattern type.

<!-- BEGIN GENERATED: New-JIMSchedule -->
### Syntax

```powershell
New-JIMSchedule [-Name] <string> -TriggerType <string> [-Description <string>] [-PatternType <string>] [-DaysOfWeek <int[]>] [-RunTimes <string[]>] [-IntervalValue <int>] [-IntervalUnit <string>] [-IntervalWindowStart <string>] [-IntervalWindowEnd <string>] [-CronExpression <string>] [-Enabled] [-OnStepFailure <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name for the Schedule. Can be given by position, as the first argument. |
| `Description` | `string` | No |  | An optional description of what the Schedule does. |
| `TriggerType` | `string` | Yes |  | How the Schedule starts. Cron runs it automatically on the pattern you give; Manual runs it only when started, from the portal or with `Start-JIMSchedule`. One of `Cron` or `Manual`. |
| `PatternType` | `string` | No | `'SpecificTimes'` | How a Cron Schedule is timed. SpecificTimes runs it at set times of day (RunTimes), Interval runs it every few hours or minutes (IntervalValue and IntervalUnit), and Custom takes a cron expression of your own (CronExpression). One of `SpecificTimes`, `Interval` or `Custom`. |
| `DaysOfWeek` | `int[]` | No |  | The days the Schedule runs, as numbers from 0 (Sunday) to 6 (Saturday). For example, @(1,2,3,4,5) for weekdays. From 0 to 6. |
| `RunTimes` | `string[]` | No |  | For a SpecificTimes pattern, the times of day to run, in 24-hour format, for example @("06:00", "18:00"). Every time must be at the same minute past the hour ("06:30" and "18:30", not "06:00" and "18:30"), because a Schedule holds one cron expression; use a Custom pattern for times that differ. |
| `IntervalValue` | `int` | No |  | For an Interval pattern, how often to run, counted in IntervalUnit. For example, 2 with `-IntervalUnit` Hours runs every two hours. For Hours, no more than 23. From 1 to 59. |
| `IntervalUnit` | `string` | No | `'Hours'` | For an Interval pattern, the unit of IntervalValue: Hours or Minutes. One of `Hours` or `Minutes`. |
| `IntervalWindowStart` | `string` | No |  | For an hourly Interval pattern, the start of the window the Schedule runs in, in 24-hour format, for example "08:00". Give IntervalWindowEnd with it. An interval in minutes runs all day. |
| `IntervalWindowEnd` | `string` | No |  | For an hourly Interval pattern, the end of the window the Schedule runs in, in 24-hour format, for example "18:00". |
| `CronExpression` | `string` | No |  | For a Custom pattern, the cron expression, for example "0 6 * * 1-5". For SpecificTimes and Interval, JIM builds the expression from the pattern, exactly as the portal's Schedule editor does. |
| `Enabled` | `switch` | No |  | Enables the Schedule as soon as it is created. Without it, the Schedule is created disabled. |
| `OnStepFailure` | `string` | No |  | What the Schedule does when a step fails. With Stop (the default), the remaining steps do not run and the run ends Failed. With Continue, the remaining steps run, and a run that carried on past a failure ends CompleteWithError. A step with a setting of its own (`Add-JIMScheduleStep -OnFailure`) overrides it. One of `Stop` or `Continue`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the created Schedule. |

### Output

With `-PassThru`, the created Schedule in full; otherwise nothing.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: New-JIMSchedule -->

### Examples

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

## Set-JIMSchedule

Changes a Schedule. Only the parameters you give are changed; everything else keeps its current value.

<!-- BEGIN GENERATED: Set-JIMSchedule -->
### Syntax

```powershell
Set-JIMSchedule -Id <guid> [-Name <string>] [-Description <string>] [-TriggerType <string>] [-PatternType <string>] [-DaysOfWeek <int[]>] [-RunTimes <string[]>] [-IntervalValue <int>] [-IntervalUnit <string>] [-IntervalWindowStart <string>] [-IntervalWindowEnd <string>] [-CronExpression <string>] [-OnStepFailure <string>] [-Steps <Array>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to update. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `Name` | `string` | No |  | The new name for the Schedule. |
| `Description` | `string` | No |  | The new description for the Schedule. |
| `TriggerType` | `string` | No |  | How the Schedule starts. Cron runs it automatically on its pattern; Manual runs it only when started, from the portal or with `Start-JIMSchedule`. One of `Cron` or `Manual`. |
| `PatternType` | `string` | No |  | How a Cron Schedule is timed. SpecificTimes runs it at set times of day (RunTimes), Interval runs it every few hours or minutes (IntervalValue and IntervalUnit), and Custom takes a cron expression of your own (CronExpression). One of `SpecificTimes`, `Interval` or `Custom`. |
| `DaysOfWeek` | `int[]` | No |  | The days the Schedule runs, as numbers from 0 (Sunday) to 6 (Saturday). For example, @(1,2,3,4,5) for weekdays. From 0 to 6. |
| `RunTimes` | `string[]` | No |  | For a SpecificTimes pattern, the times of day to run, in 24-hour format, for example @("06:00", "18:00"). Every time must be at the same minute past the hour, because a Schedule holds one cron expression; use a Custom pattern for times that differ. |
| `IntervalValue` | `int` | No |  | For an Interval pattern, how often to run, counted in IntervalUnit. For Hours, no more than 23. From 1 to 59. |
| `IntervalUnit` | `string` | No |  | For an Interval pattern, the unit of IntervalValue: Hours or Minutes. One of `Hours` or `Minutes`. |
| `IntervalWindowStart` | `string` | No |  | For an hourly Interval pattern, the start of the window the Schedule runs in, in 24-hour format. Give IntervalWindowEnd with it. |
| `IntervalWindowEnd` | `string` | No |  | For an hourly Interval pattern, the end of the window the Schedule runs in, in 24-hour format. |
| `CronExpression` | `string` | No |  | For a Custom pattern, the cron expression. For SpecificTimes and Interval, JIM rebuilds the expression from the pattern, so changing RunTimes or the interval retimes the Schedule. |
| `OnStepFailure` | `string` | No |  | What the Schedule does when a step fails. With Stop, the remaining steps do not run and the run ends Failed. With Continue, the remaining steps run, and a run that carried on past a failure ends CompleteWithError. A step with a setting of its own (see `Set-JIMScheduleStep`) overrides it. Omit to leave the setting unchanged. A built-in Schedule always stops, and JIM refuses a change to it. One of `Stop` or `Continue`. |
| `Steps` | `Array` | No |  | Array of step objects to replace the existing steps. Give each step its failure setting as OnFailure (FollowSchedule, Stop or Continue). A step object that carries OnFailure ignores its ContinueOnFailure, so to change one step's setting, use `Set-JIMScheduleStep`, or change the step's OnFailure rather than its ContinueOnFailure. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the updated Schedule in full; otherwise nothing.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Set-JIMSchedule -->

### Examples

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

---

## Remove-JIMSchedule

Deletes a Schedule permanently. This cannot be undone.

<!-- BEGIN GENERATED: Remove-JIMSchedule -->
### Syntax

```powershell
Remove-JIMSchedule -Id <guid> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to remove. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `ChangeReason` | `string` | No |  | An optional reason for the deletion, recorded against the change history. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMSchedule -->

### Examples

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

## Enable-JIMSchedule

Enables a Schedule, so it runs when its trigger fires.

<!-- BEGIN GENERATED: Enable-JIMSchedule -->
### Syntax

```powershell
Enable-JIMSchedule -Id <guid> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to enable. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the updated Schedule in full; otherwise nothing.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Enable-JIMSchedule -->

### Examples

```powershell title="Enable a schedule"
Enable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Enable and verify"
Enable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -PassThru |
    Select-Object Name, IsEnabled
```

---

## Disable-JIMSchedule

Disables a Schedule, so it no longer runs when its trigger fires. Its configuration is kept, and it can be enabled again.

<!-- BEGIN GENERATED: Disable-JIMSchedule -->
### Syntax

```powershell
Disable-JIMSchedule -Id <guid> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to disable. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the updated Schedule in full; otherwise nothing.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Disable-JIMSchedule -->

### Examples

```powershell title="Disable a schedule"
Disable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Disable all schedules matching a pattern"
Get-JIMSchedule -Name "Legacy*" | Disable-JIMSchedule -PassThru
```

---

## Add-JIMScheduleStep

Adds a step to a Schedule. Each step runs a Run Profile against a Connected System. Steps run one after another unless `-Parallel` runs a step at the same time as the one before it.

<!-- BEGIN GENERATED: Add-JIMScheduleStep -->
### Syntax

```powershell
# ById (default)
Add-JIMScheduleStep -ScheduleId <guid> -StepType <string> -ConnectedSystemId <int> -RunProfileId <int> [-Parallel] [-OnFailure <string>] [-ContinueOnFailure] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Add-JIMScheduleStep -ScheduleId <guid> -StepType <string> -ConnectedSystemName <string> -RunProfileName <string> [-Parallel] [-OnFailure <string>] [-ContinueOnFailure] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ScheduleId` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to add the step to. Accepts pipeline input by property name, `ScheduleId` or `Id`. |
| `StepType` | `string` | Yes |  | The type of step. RunProfile, which runs a Connected System's Run Profile, is the only type. One of `RunProfile`. |
| `ConnectedSystemId` | `int` | Yes (ById) |  | For a RunProfile step, the id of the Connected System. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | For a RunProfile step, the name of the Connected System. |
| `RunProfileId` | `int` | Yes (ById) |  | For a RunProfile step, the id of the Run Profile to run. |
| `RunProfileName` | `string` | Yes (ByName) |  | For a RunProfile step, the name of the Run Profile to run. |
| `Parallel` | `switch` | No |  | Runs this step at the same time as the step before it, in one step group. Without it, the step waits for the step before it to finish. |
| `OnFailure` | `string` | No |  | What the Schedule does when this step fails. FollowSchedule (the default) does whatever the Schedule is set to do (its OnStepFailure). Stop stops the Schedule, even one set to continue. Continue carries on with the remaining steps, even when the Schedule is set to stop. Cannot be used with `-ContinueOnFailure`. One of `FollowSchedule`, `Stop` or `Continue`. |
| `ContinueOnFailure` | `switch` | No |  | Shorthand for `-OnFailure` Continue: the Schedule carries on if this step fails, whatever the Schedule is set to do. Cannot be used with `-OnFailure`; giving both is an error, and nothing is changed. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the Schedule in full, with the new step; otherwise nothing. Adding a step changes no other step: the existing steps are sent back with their failure settings exactly as they were.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Add-JIMScheduleStep -->

### Examples

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

## Set-JIMScheduleStep

Changes what one step does when it fails, leaving every other step, and the Schedule itself, as they are. To change a step's other settings, send the whole step list through `Set-JIMSchedule -Steps`.

<!-- BEGIN GENERATED: Set-JIMScheduleStep -->
### Syntax

```powershell
Set-JIMScheduleStep -ScheduleId <guid> -StepId <guid> -OnFailure <string> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ScheduleId` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule the step belongs to. A Schedule piped from `Get-JIMSchedule` binds here through its Id. Accepts pipeline input by property name, `ScheduleId` or `Id`. |
| `StepId` | `guid` | Yes |  | The unique identifier (GUID) of the step to change, as shown in the Schedule's Steps. |
| `OnFailure` | `string` | Yes |  | What the Schedule does when this step fails. FollowSchedule does whatever the Schedule is set to do (its OnStepFailure). Stop stops the Schedule, even one set to continue. Continue carries on with the remaining steps, even when the Schedule is set to stop. One of `FollowSchedule`, `Stop` or `Continue`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the updated Schedule in full; otherwise nothing. If the Schedule has no step with that StepId, the cmdlet reports an error and changes nothing.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Set-JIMScheduleStep -->

The steps of a built-in Schedule, such as Temporal Scope Reconciliation, always follow their Schedule; JIM refuses a change to them.

### Examples

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

## Remove-JIMScheduleStep

Removes a step from a Schedule by its position. The remaining steps are renumbered: removing step 1 from a Schedule with steps 0, 1 and 2 makes the former step 2 step 1.

<!-- BEGIN GENERATED: Remove-JIMScheduleStep -->
### Syntax

```powershell
Remove-JIMScheduleStep -ScheduleId <guid> -StepIndex <int> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ScheduleId` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to remove the step from. Accepts pipeline input by property name, `ScheduleId` or `Id`. |
| `StepIndex` | `int` | Yes |  | The position of the step to remove, counting from 0. At least 0. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Schedule's change history. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the updated Schedule. |

### Output

With `-PassThru`, the updated Schedule in full; otherwise nothing. The remaining steps are renumbered, and each keeps its Id and its failure setting.

Properties as under [Get-JIMSchedule](#get-jimschedule).
<!-- END GENERATED: Remove-JIMScheduleStep -->

### Examples

```powershell title="Remove the first step"
Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -StepIndex 0
```

```powershell title="Remove a step without confirmation and view the result"
Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -StepIndex 2 -Force -PassThru
```

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

Each step in a Schedule's `Steps` says how it behaves when it fails:

| Property | Type | Description |
|---|---|---|
| `OnFailure` | `string` | The step's own setting: `FollowSchedule` (the Schedule's `OnStepFailure` decides), `Stop` or `Continue`. |
| `ContinueOnFailure` | `bool` | What the step will actually do if it fails: `$true` when the Schedule carries on past it. This is the step's own setting, or the Schedule's when the step follows the Schedule. |
| `FailureBehaviourSource` | `string` | Where `ContinueOnFailure` comes from: `Step` (the step has a setting of its own) or `Schedule` (it follows the Schedule). |

What counts as a step failing, and how the settings appear in the portal, are covered in [Schedules](../configuration/schedules.md).

---

## See also

- [Schedule Executions](schedule-executions.md): run a Schedule now, and watch or stop its runs
- [Schedules](../configuration/schedules.md): what schedules are, trigger types and patterns, step types, and execution behaviour
- [Run Profiles](run-profiles.md): Managing Connected System Run Profiles
- [Activities](activities.md): Viewing synchronisation activity results
