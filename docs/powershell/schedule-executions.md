---
title: Schedule Executions
description: "The cmdlets that run a Schedule now, and watch or stop its Schedule Executions."
type: reference
audience: automation-author
---

# Schedule Executions

A Schedule Execution is one run of a Schedule, from its first step to its last. These cmdlets start one on demand, follow it while it runs, read back what happened, and stop one that is queued or running. To create or change a Schedule itself, see [Schedules](schedules.md).

Every Schedule Execution these cmdlets return carries `StepDisplay`, the step group it has reached as one sentence, so a script reads the same position the portal shows.

---

## :material-calendar-clock-outline: Start-JIMSchedule

Runs a Schedule now, whatever its trigger type and whether or not it is enabled: useful for testing a Schedule, or for running one on demand.

<!-- BEGIN GENERATED: Start-JIMSchedule -->
### Syntax

```powershell
Start-JIMSchedule -Id <guid> [-Wait] [-Timeout <timespan>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule to start. Accepts pipeline input by property name, `Id` or `ScheduleId`. |
| `Wait` | `switch` | No |  | Waits for the Schedule Execution to finish (Complete, CompleteWithError, Failed or Cancelled), checking every 5 seconds and showing progress, then returns it. |
| `Timeout` | `timespan` | No | `[TimeSpan]::FromMinutes(30)` | How long `-Wait` waits before it gives up with a warning. The Schedule Execution carries on in JIM. |
| `PassThru` | `switch` | No |  | Returns the new Schedule Execution. |

### Output

With `-PassThru`, the new Schedule Execution as it stands; with `-Wait`, the execution once it has finished, or as it stands when the timeout is reached; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Steps` | `object[]` | One entry per Schedule Step: its name, type, timings, errors and Activity id. A step group that runs three Run Profiles concurrently appears here three times, and once in Progress. Each has `StepIndex`, `Name`, `StepType`, `ExecutionMode`, `ConnectedSystemId`, `Status`, `TaskId`, `StartedAt`, `CompletedAt`, `ErrorMessage`, `ActivityId`, `ActivityStatus`, `ConnectedSystemName`, `RunProfileId`, `RunProfileName`, `ContinueOnFailure`, `FailureBehaviourSource` and `CancellationReason`. |
| `Progress` | `object` | How far through the Schedule this execution has got: the step group it has reached, and each group's outcome, including each concurrent task's own outcome where a group runs several. Empty where the execution recorded no steps. It has `CurrentStepNumber`, `TotalSteps` and `Steps`. |
| `Id` | `guid` | The unique identifier of the execution. |
| `ScheduleId` | `guid` | The schedule ID this execution belongs to. |
| `ScheduleName` | `string` | The schedule name for display purposes. |
| `Status` | `string` | The current status of the execution. One of `Queued`, `InProgress`, `Complete`, `Failed`, `Cancelled`, `Paused` or `CompleteWithError`. |
| `CurrentStepIndex` | `int` | The current step group being executed (0-based). Schedule Steps that run concurrently share an index and are one position here, not several. |
| `TotalSteps` | `int` | The number of step groups in the Schedule, counting the same thing as CurrentStepIndex so that the two can be read together as "step X of Y". |
| `QueuedAt` | `datetime` | When the execution was queued (UTC). |
| `StartedAt` | `datetime` | When the execution started (UTC). |
| `CompletedAt` | `datetime` | When the execution completed (UTC). |
| `InitiatedByType` | `string` | The type of principal that initiated the execution. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The ID of the principal that initiated the execution. |
| `InitiatedByName` | `string` | The display name of the principal that initiated the execution. |
| `ErrorMessage` | `string` | Error message if the execution failed. For a `CompleteWithError` execution, the message naming each failed step it carried on past. |
| `StepDisplay` | `string` | The step group the execution has reached, as one sentence naming it, for example Step 2 of 5: 2 in parallel, matching what the portal shows in Admin > Operations > Activity. |
<!-- END GENERATED: Start-JIMSchedule -->

When `-Wait` reaches its timeout, the cmdlet warns, but the Schedule Execution carries on in JIM. Use [`Get-JIMScheduleExecution`](#get-jimscheduleexecution) to follow it, or [`Stop-JIMScheduleExecution`](#stop-jimscheduleexecution) to cancel it.

### Examples

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

---

## :material-history: Get-JIMScheduleExecution

Retrieves Schedule Executions: to follow one that is running, review past runs, or check how a particular one went.

<!-- BEGIN GENERATED: Get-JIMScheduleExecution -->
### Syntax

```powershell
# List (default)
Get-JIMScheduleExecution [-ScheduleId <guid>] [-InputObject <PSCustomObject>] [-Status <string>]

# ById
Get-JIMScheduleExecution -Id <guid>

# Active
Get-JIMScheduleExecution -Active [-ScheduleId <guid>] [-InputObject <PSCustomObject>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of a specific Schedule Execution to retrieve. Accepts pipeline input by property name, `Id` or `ExecutionId`. |
| `ScheduleId` | `guid` | No (List, Active) |  | Returns only the executions of this Schedule. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | No (List, Active) |  | A Schedule from the pipeline, for example from `Get-JIMSchedule`. Its Id filters the executions, as `-ScheduleId` does. Accepts pipeline input. |
| `Status` | `string` | No (List) |  | Returns only the executions with this status. Queued is waiting to start and InProgress is running. Complete finished successfully; CompleteWithError finished, but one or more steps failed and were set to let the Schedule carry on. Failed was stopped by a failed step, or failed to run, and Cancelled was stopped. One of `Queued`, `InProgress`, `Complete`, `CompleteWithError`, `Failed` or `Cancelled`. |
| `Active` | `switch` | Yes (Active) |  | Returns only the executions still under way: Queued or InProgress. |

### Output

The list and `-Active` return a summary of each Schedule Execution; `-Id` returns one in full, with the progress of each step group.

**By default, or with `-Active`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the execution. |
| `ScheduleId` | `guid` | The schedule ID this execution belongs to. |
| `ScheduleName` | `string` | The schedule name for display purposes. |
| `Status` | `string` | The current status of the execution. One of `Queued`, `InProgress`, `Complete`, `Failed`, `Cancelled`, `Paused` or `CompleteWithError`. |
| `CurrentStepIndex` | `int` | The current step group being executed (0-based). Schedule Steps that run concurrently share an index and are one position here, not several. |
| `TotalSteps` | `int` | The number of step groups in the Schedule, counting the same thing as CurrentStepIndex so that the two can be read together as "step X of Y". |
| `QueuedAt` | `datetime` | When the execution was queued (UTC). |
| `StartedAt` | `datetime` | When the execution started (UTC). |
| `CompletedAt` | `datetime` | When the execution completed (UTC). |
| `InitiatedByType` | `string` | The type of principal that initiated the execution. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The ID of the principal that initiated the execution. |
| `InitiatedByName` | `string` | The display name of the principal that initiated the execution. |
| `ErrorMessage` | `string` | Error message if the execution failed. For a `CompleteWithError` execution, the message naming each failed step it carried on past. |
| `StepDisplay` | `string` | The step group the execution has reached, as its position, for example Step 2 of 5. A summary carries the position only, not what the step group is called. |

**With `-Id`**: properties as under [Start-JIMSchedule](#start-jimschedule).
<!-- END GENERATED: Get-JIMScheduleExecution -->

`Progress` and `Steps` answer different questions. `Steps` has one entry per Schedule Step; `Progress` counts step groups, where steps that run at the same time are one group, so a Schedule of six steps where two run together is five steps long:

| Property | Description |
|----------|-------------|
| `Progress.CurrentStepNumber` | The step group being run, 1-based. |
| `Progress.TotalSteps` | How many step groups the Schedule has. |
| `Progress.Steps` | One entry per step group, each with its `StepIndex`, `Name`, `Status` (`Pending`, `Running`, `Completed`, `Failed` or `Cancelled`), `IsParallel`, and `TaskStatuses`, every concurrent task's own outcome. |
| `Steps.CancellationReason` | Why a `Cancelled` step did not run, such as `Not run: an earlier step stopped the Schedule.`; empty for any other step, including one that was already running when it was cancelled. |
| `Steps.ContinueOnFailure` | Whether the Schedule Execution carries on past this step if it fails: the step's own setting, or its Schedule's when it follows the Schedule. |
| `Steps.FailureBehaviourSource` | Where `Steps.ContinueOnFailure` comes from: `Step` or `Schedule`. |

### Examples

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

## :material-stop-circle-outline: Stop-JIMScheduleExecution

Cancels a Schedule Execution that is queued or running.

<!-- BEGIN GENERATED: Stop-JIMScheduleExecution -->
### Syntax

```powershell
Stop-JIMScheduleExecution -Id <guid> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Schedule Execution to stop. Accepts pipeline input by property name, `Id` or `ExecutionId`. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the cancelled Schedule Execution. |

### Output

With `-PassThru`, the cancelled Schedule Execution; otherwise nothing.

Properties as under [Start-JIMSchedule](#start-jimschedule).
<!-- END GENERATED: Stop-JIMScheduleExecution -->

### Examples

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

## :material-book-open-variant-outline: See also

- [Schedules](schedules.md): create, change and remove Schedules and their steps
- [Schedules](../configuration/schedules.md): what a step failing means, and how a Schedule Execution appears in the portal
- [Activities](activities.md): the Activities each step of a Schedule Execution produces
