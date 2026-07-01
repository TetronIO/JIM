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

Returns one or more `PSCustomObject` instances representing Worker Task headers, including status, progress, and initiator.

### Examples

```powershell title="List in-flight Worker Tasks"
Get-JIMWorkerTask
```

```powershell title="Get a specific Worker Task"
Get-JIMWorkerTask -Id "12345678-1234-1234-1234-123456789012"
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
