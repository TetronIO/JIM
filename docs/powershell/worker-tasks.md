---
title: Worker Tasks
---

# Worker Tasks

Worker Task cmdlets let you monitor and cancel queued and in-progress background operations (synchronisation runs, connector space clears, example data generation, and similar). Worker Tasks are ephemeral: once a task completes, its record is deleted and the associated [Activity](activities.md) becomes the durable audit record, so these cmdlets only ever return in-flight work.

---

## Get-JIMWorkerTask

Gets currently queued, processing, or cancellation-requested Worker Tasks.

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
| `Id` | `guid` | Yes (ById set) | | The ID of a specific Worker Task to retrieve. Accepts pipeline input. |
| `Page` | `int` | No | `1` | Page number for paginated results. |
| `PageSize` | `int` | No | `50` | Number of results per page (maximum 100). |

### Output

Returns one or more `PSCustomObject` instances representing Worker Task headers, including status, progress, and initiator. The step-related properties are:

| Property | Description |
|----------|-------------|
| `StepDisplay` | The step the run is on, as one sentence: `Step 3 of 7: Saving changes`. Empty for a task that is not a Run Profile execution, since those record no steps. |
| `Steps` | The run's steps: `CurrentStepName`, `CurrentStepNumber`, `TotalSteps`, and `Steps`, a list of every step with its `Order`, `Name` and `Status`. `$null` where the task records none. |
| `ScheduleTotalSteps` | How many step groups the Schedule Execution has, where the task belongs to one. Steps that run concurrently are one group, not several. |
| `ScheduleCurrentStepIndex` | Which step group the Schedule Execution has reached, 0-based. |

`StepDisplay` is the same sentence the portal shows in **Admin > Operations > Queue**, and the same one `Start-JIMRunProfile -Wait` shows live, so a run reads identically wherever you are watching it from.

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

### Syntax

```powershell
Stop-JIMWorkerTask -Id <guid> [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | The ID of the Worker Task to cancel. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Bypasses confirmation prompts. |

!!! warning "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **High** impact level. You will be prompted for confirmation unless `-Force` is specified.

### Output

None.

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
