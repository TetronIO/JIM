---
title: Worker Tasks
description: "The cmdlets that list the background tasks queued or running in JIM, and cancel them."
type: reference
audience: automation-author
---

# Worker Tasks

Worker Task cmdlets let you monitor and cancel queued and in-progress background operations (synchronisation runs, Connector Space clears, example data generation, and similar). Worker Tasks are ephemeral: once a task completes, its record is deleted and the associated [Activity](activities.md) becomes the durable audit record, so these cmdlets only ever return in-flight work.

---

## Get-JIMWorkerTask

Gets currently queued, processing, or cancellation-requested Worker Tasks.

<!-- BEGIN GENERATED: Get-JIMWorkerTask -->
### Syntax

```powershell
# List (default)
Get-JIMWorkerTask [-Page <int>] [-PageSize <int>]

# ById
Get-JIMWorkerTask -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of a specific Worker Task to retrieve. Accepts pipeline input by property name. |
| `Page` | `int` | No (List) | `1` | The page of results to return. At least 1. |
| `PageSize` | `int` | No (List) | `50` | The number of Worker Tasks on each page. From 1 to 100. |

### Output

Each Worker Task's header, with one property the module adds.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` |  |
| `Name` | `string` |  |
| `Type` | `string` |  |
| `Timestamp` | `datetime` |  |
| `Status` | `string` | One of `Queued`, `Processing`, `CancellationRequested` or `WaitingForPreviousStep`. |
| `InitiatedByType` | `string` | The type of security principal that initiated this task. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this task. |
| `InitiatedByName` | `string` | The name of the security principal at the time of task creation. |
| `ActivityId` | `guid` | The associated Activity ID (if the task has started processing). |
| `ObjectsToProcess` | `int` | The total number of objects to process (from the associated Activity). |
| `ObjectsProcessed` | `int` | The number of objects processed so far (from the associated Activity). |
| `ProgressMessage` | `string` | The current progress message (from the associated Activity). |
| `Steps` | `object` | The run's steps and the one running now, or empty where the task is not a Run Profile execution and so has no steps: clearing Connected System Objects, example data generation and factory reset all record none. A list view showing this task falls back to the object counts alone in that case. It has `CurrentStepName`, `CurrentStepNumber`, `TotalSteps` and `Steps`. |
| `ExecutionMode` | `string` | Whether the worker runs this task on its own or together with the tasks queued alongside it. One of `Sequential` or `Parallel`. |
| `Started` | `datetime` | When the worker started this task (UTC), or empty while it has not started. |
| `ConnectedSystemRunProfileId` | `int` | The Run Profile this task executes, or empty where the task is not a Run Profile execution. |
| `ConnectedSystemName` | `string` | The name of the Connected System whose Run Profile this task executes, or empty where the task is not a Run Profile execution or the Run Profile no longer exists. |
| `RunProfileName` | `string` | The name of the Run Profile this task executes, or empty where the task is not a Run Profile execution or the Run Profile no longer exists. |
| `ScheduleExecutionId` | `guid` | If this task is part of a schedule execution, the execution ID for UI grouping. |
| `ScheduleExecutionName` | `string` | The schedule name at execution time, for display in the queue UI. |
| `ScheduleStepIndex` | `int` | The step index within the schedule, for ordering within the group. |
| `ScheduleTotalSteps` | `int` | The number of step groups the Schedule Execution is working through. |
| `ScheduleCurrentStepIndex` | `int` | The step group the Schedule Execution has reached, counting from 0. |
| `StepDisplay` | `string` | The step the run is on, as one sentence such as "Step 3 of 7: Saving changes". It is the sentence the portal shows in Admin > Operations > Activity, and the one `Start-JIMRunProfile -Wait` shows live, so a run reads the same wherever it is watched from. Empty for a task that is not a Run Profile execution, since those record no steps. |
<!-- END GENERATED: Get-JIMWorkerTask -->

### Examples

```powershell title="List in-flight Worker Tasks"
Get-JIMWorkerTask
```

```powershell title="Get a specific Worker Task"
Get-JIMWorkerTask -Id "12345678-1234-1234-1234-123456789012"
```

```powershell title="See what every running task is currently doing"
Get-JIMWorkerTask | Select-Object Name, Status, StepDisplay
```

```powershell title="Find runs that have reached their final step"
Get-JIMWorkerTask | Where-Object { $_.Steps -and $_.Steps.CurrentStepNumber -eq $_.Steps.TotalSteps }
```

---

## Stop-JIMWorkerTask

Cancels a queued or in-progress Worker Task. Cancellation completes asynchronously: JIM returns as soon as the request has been accepted.

<!-- BEGIN GENERATED: Stop-JIMWorkerTask -->
### Syntax

```powershell
Stop-JIMWorkerTask -Id <guid> [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Worker Task to cancel. Accepts pipeline input by property name. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Stop-JIMWorkerTask -->

### Examples

```powershell title="Cancel a Worker Task with confirmation"
Stop-JIMWorkerTask -Id "12345678-1234-1234-1234-123456789012"
```

```powershell title="Cancel every in-flight Worker Task without confirmation"
Get-JIMWorkerTask | Stop-JIMWorkerTask -Force
```

---

## See also

- [Activities](activities.md): cmdlets for reviewing the durable audit record a Worker Task leaves behind
- [Schedules](schedules.md): cmdlets for configuring the automated workflows that queue Worker Tasks
