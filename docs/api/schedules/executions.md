---
title: Schedule Executions
---

# Schedule Executions

A Schedule Execution represents a single run of a schedule, tracking the progress of each step from queued through to completion or failure. Executions are created when a schedule is [triggered manually](run.md) or runs on its cron trigger.

### The Execution Object

```json
{
  "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "scheduleId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "scheduleName": "Daily Delta Sync",
  "status": "InProgress",
  "currentStepIndex": 1,
  "totalSteps": 4,
  "queuedAt": "2026-04-05T06:00:00Z",
  "startedAt": "2026-04-05T06:00:01Z",
  "completedAt": null,
  "initiatedByType": "ApiKey",
  "initiatedById": "d4e5f6a7-b8c9-0123-defa-234567890123",
  "initiatedByName": "automation-key",
  "errorMessage": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `scheduleId` | guid | Parent schedule ID |
| `scheduleName` | string | Schedule name (snapshot at execution time) |
| `status` | string | `Queued`, `InProgress`, `Completed`, `Failed`, `Cancelled`, or `Paused` |
| `currentStepIndex` | integer | Index of the currently executing step |
| `totalSteps` | integer | Total number of steps |
| `queuedAt` | datetime | When the execution was queued |
| `startedAt` | datetime, nullable | When execution began |
| `completedAt` | datetime, nullable | When execution finished |
| `initiatedByType` | string | Who triggered it: `User`, `ApiKey`, or `NotSet` (cron) |
| `initiatedById` | guid, nullable | Initiator ID |
| `initiatedByName` | string, nullable | Initiator name |
| `errorMessage` | string, nullable | Error details if the execution failed |

---

## List Schedule Executions

Returns a paginated list of schedule executions, optionally filtered by schedule.

```
GET /api/v1/schedule-executions
```

### Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `scheduleId`    | guid    | No       |         | Filter by schedule ID |
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `20`    | Items per page |
| `sortBy`        | string  | No       |         | Sort field: `queuedAt`, `startedAt`, `completedAt`, `status` |
| `sortDescending`| boolean | No       | `true`  | Sort in descending order (newest first by default) |

### Examples

=== "curl"

    ```bash
    # List all executions
    curl https://jim.example.com/api/v1/schedule-executions \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by schedule
    curl "https://jim.example.com/api/v1/schedule-executions?scheduleId=a1b2c3d4-e5f6-7890-abcd-ef1234567890" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all executions
    Get-JIMScheduleExecution

    # Filter by schedule
    Get-JIMScheduleExecution -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

    # Filter by status
    Get-JIMScheduleExecution -Status Failed
    ```

### Response

Returns `200 OK` with a paginated list of execution summaries.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |

---

## Retrieve a Schedule Execution

Returns the full details of an execution, including per-step progress.

```
GET /api/v1/schedule-executions/{id}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the execution |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/schedule-executions/c3d4e5f6-a7b8-9012-cdef-123456789012 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMScheduleExecution -Id "c3d4e5f6-a7b8-9012-cdef-123456789012"
    ```

### Response

Returns `200 OK` with the execution details including a `steps` array showing per-step progress:

```json
{
  "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "scheduleId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "scheduleName": "Daily Delta Sync",
  "status": "InProgress",
  "currentStepIndex": 1,
  "totalSteps": 3,
  "queuedAt": "2026-04-05T06:00:00Z",
  "startedAt": "2026-04-05T06:00:01Z",
  "completedAt": null,
  "initiatedByType": "ApiKey",
  "initiatedById": "d4e5f6a7-b8c9-0123-defa-234567890123",
  "initiatedByName": "automation-key",
  "errorMessage": null,
  "steps": [
    {
      "stepIndex": 0,
      "name": "Corporate LDAP - Delta Import",
      "stepType": "RunProfile",
      "executionMode": "Sequential",
      "connectedSystemId": 1,
      "status": "Completed",
      "taskId": "e5f6a7b8-c9d0-1234-efab-345678901234",
      "startedAt": "2026-04-05T06:00:01Z",
      "completedAt": "2026-04-05T06:01:30Z",
      "errorMessage": null,
      "activityId": "f6a7b8c9-d0e1-2345-fabc-456789012345",
      "activityStatus": "Complete"
    },
    {
      "stepIndex": 1,
      "name": "Corporate LDAP - Delta Sync",
      "stepType": "RunProfile",
      "executionMode": "Sequential",
      "connectedSystemId": 1,
      "status": "Processing",
      "taskId": "a7b8c9d0-e1f2-3456-abcd-567890123456",
      "startedAt": "2026-04-05T06:01:31Z",
      "completedAt": null,
      "errorMessage": null,
      "activityId": "b8c9d0e1-f2a3-4567-bcde-678901234567",
      "activityStatus": "InProgress"
    },
    {
      "stepIndex": 2,
      "name": "Corporate LDAP - Export",
      "stepType": "RunProfile",
      "executionMode": "Sequential",
      "connectedSystemId": 1,
      "status": "Pending",
      "taskId": null,
      "startedAt": null,
      "completedAt": null,
      "errorMessage": null,
      "activityId": null,
      "activityStatus": null
    }
  ]
}
```

#### Step Attributes

| Field | Type | Description |
|-------|------|-------------|
| `stepIndex` | integer | Execution order |
| `name` | string | Step display name |
| `stepType` | string | `RunProfile`, `PowerShell`, `Executable`, or `SqlScript` |
| `executionMode` | string | `Sequential` or `ParallelWithPrevious` |
| `connectedSystemId` | integer, nullable | Connected system ID (RunProfile steps) |
| `status` | string | `Pending`, `Queued`, `Processing`, `Waiting`, `Completed`, `Completed with Warning`, `Completed with Error`, `Failed`, or `Cancelled` |
| `taskId` | guid, nullable | Worker task ID |
| `startedAt` | datetime, nullable | When this step started |
| `completedAt` | datetime, nullable | When this step finished |
| `errorMessage` | string, nullable | Error details if the step failed |
| `activityId` | guid, nullable | Associated activity ID (RunProfile steps) |
| `activityStatus` | string, nullable | Activity status: `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError`, or `Cancelled` |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Execution does not exist |

---

## Cancel a Schedule Execution

Cancels a running or queued schedule execution.

```
POST /api/v1/schedule-executions/{id}/cancel
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the execution |

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedule-executions/c3d4e5f6-a7b8-9012-cdef-123456789012/cancel \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Stop-JIMScheduleExecution -Id "c3d4e5f6-a7b8-9012-cdef-123456789012"

    # Cancel all running executions
    Get-JIMScheduleExecution -Active | Stop-JIMScheduleExecution -Force
    ```

### Response

Returns `200 OK` with the updated execution object showing `"status": "Cancelled"`.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Execution does not exist |

---

## List Active Executions

Returns all currently running or queued schedule executions. This is a convenience endpoint for monitoring.

```
GET /api/v1/schedule-executions/active
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/schedule-executions/active \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMScheduleExecution -Active
    ```

### Response

Returns `200 OK` with an array of execution objects (not paginated).

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
