---
title: Schedules
---

# Schedules

A Schedule defines an automated sequence of operations that JIM executes on a trigger (cron-based or manual). Each schedule contains ordered steps that can run sequentially or in parallel, supporting run profile execution, PowerShell scripts, external executables, and SQL scripts.

Schedules are the primary mechanism for automating identity synchronisation workflows, such as running imports from multiple connected systems, followed by synchronisation, and then exports.

## Common Workflows

**Creating an automated sync schedule:**

1. [Create a schedule](create.md) with a cron trigger and timing pattern
2. [Add steps](steps.md) for each run profile in the desired order
3. [Enable the schedule](enable.md)

**Running a one-off sync:**

1. [Create a schedule](create.md) with a manual trigger
2. [Add steps](steps.md) for the operations to perform
3. [Run the schedule](run.md) to trigger execution immediately
4. [Monitor the execution](executions.md) to track progress

**Monitoring and troubleshooting:**

1. [List active executions](executions.md#list-active-executions) to see what's running
2. [Retrieve an execution](executions.md#retrieve-a-schedule-execution) to inspect step progress
3. [Cancel an execution](executions.md#cancel-a-schedule-execution) if needed

## The Schedule Object

When you retrieve a schedule with steps, the detail response contains:

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Daily Delta Sync",
  "description": "Import, sync, and export changes from all connected systems",
  "triggerType": "Cron",
  "cronExpression": "0 6 * * 1-5",
  "patternType": "SpecificTimes",
  "daysOfWeek": "1,2,3,4,5",
  "runTimes": "06:00",
  "intervalValue": null,
  "intervalUnit": null,
  "intervalWindowStart": null,
  "intervalWindowEnd": null,
  "isEnabled": true,
  "lastRunTime": "2026-04-04T06:00:00Z",
  "nextRunTime": "2026-04-07T06:00:00Z",
  "stepCount": 4,
  "created": "2026-01-15T09:30:00Z",
  "lastUpdated": "2026-03-20T14:12:00Z",
  "steps": [
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "stepIndex": 0,
      "name": null,
      "executionMode": "Sequential",
      "stepType": "RunProfile",
      "continueOnFailure": false,
      "timeoutSeconds": null,
      "connectedSystemId": 1,
      "runProfileId": 1,
      "scriptPath": null,
      "arguments": null,
      "executablePath": null,
      "workingDirectory": null,
      "sqlConnectionString": null,
      "sqlScriptPath": null
    }
  ]
}
```

### Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `name` | string | Schedule name |
| `description` | string, nullable | Optional description |
| `triggerType` | string | `Cron` (automated) or `Manual` (on-demand only) |
| `cronExpression` | string, nullable | Generated cron expression (read-only; derived from pattern settings) |
| `patternType` | string | `SpecificTimes`, `Interval`, or `Custom` |
| `daysOfWeek` | string, nullable | Comma-separated day numbers (0=Sun, 1=Mon, ..., 6=Sat) |
| `runTimes` | string, nullable | Comma-separated 24h times (e.g. `"06:00,12:00,18:00"`) |
| `intervalValue` | integer, nullable | Interval count (1-59) for Interval pattern |
| `intervalUnit` | string, nullable | `Minutes` or `Hours` for Interval pattern |
| `intervalWindowStart` | string, nullable | Window start time (e.g. `"08:00"`) for Interval pattern |
| `intervalWindowEnd` | string, nullable | Window end time (e.g. `"18:00"`) for Interval pattern |
| `isEnabled` | boolean | Whether the schedule is active |
| `lastRunTime` | datetime, nullable | When the schedule last ran |
| `nextRunTime` | datetime, nullable | When the schedule will next run (cron triggers only) |
| `stepCount` | integer | Number of steps |
| `created` | datetime | UTC creation timestamp |
| `lastUpdated` | datetime, nullable | UTC last modification timestamp |
| `steps` | array | Ordered list of schedule steps (detail view only) |

### Schedule Steps

Each step in the `steps` array contains:

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `stepIndex` | integer | Execution order (0-based). Steps with the same index run in parallel. |
| `name` | string, nullable | Step name (auto-generated for RunProfile steps) |
| `executionMode` | string | `Sequential` or `ParallelWithPrevious` |
| `stepType` | string | `RunProfile`, `PowerShell`, `Executable`, or `SqlScript` |
| `continueOnFailure` | boolean | Continue executing subsequent steps if this step fails |
| `timeoutSeconds` | integer, nullable | Maximum execution time (1-86400 seconds) |
| `connectedSystemId` | integer, nullable | Connected system ID (RunProfile steps) |
| `runProfileId` | integer, nullable | Run profile ID (RunProfile steps) |
| `scriptPath` | string, nullable | Script file path (PowerShell steps) |
| `arguments` | string, nullable | Command-line arguments (PowerShell and Executable steps) |
| `executablePath` | string, nullable | Executable file path (Executable steps) |
| `workingDirectory` | string, nullable | Working directory (Executable steps) |
| `sqlConnectionString` | string, nullable | Database connection string (SqlScript steps) |
| `sqlScriptPath` | string, nullable | SQL script file path (SqlScript steps) |

### Trigger Patterns

Cron-triggered schedules support three pattern types:

| Pattern | Description | Required Fields |
|---------|-------------|-----------------|
| `SpecificTimes` | Run at specific times on selected days | `daysOfWeek`, `runTimes` |
| `Interval` | Run at regular intervals within a time window | `daysOfWeek`, `intervalValue`, `intervalUnit`, `intervalWindowStart`, `intervalWindowEnd` |
| `Custom` | Use a raw cron expression for full control | `cronExpression` |

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/schedules`](list.md) | List schedules |
| `POST` | [`/api/v1/schedules`](create.md) | Create a schedule |
| `GET` | [`/api/v1/schedules/{id}`](retrieve.md) | Retrieve a schedule with steps |
| `PUT` | [`/api/v1/schedules/{id}`](update.md) | Update a schedule |
| `DELETE` | [`/api/v1/schedules/{id}`](delete.md) | Delete a schedule |
| `POST` | [`/api/v1/schedules/{id}/enable`](enable.md) | Enable a schedule |
| `POST` | [`/api/v1/schedules/{id}/disable`](disable.md) | Disable a schedule |
| `POST` | [`/api/v1/schedules/{id}/run`](run.md) | Manually trigger a schedule |

### Schedule Executions

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/schedule-executions`](executions.md) | List schedule executions |
| `GET` | [`/api/v1/schedule-executions/{id}`](executions.md#retrieve-a-schedule-execution) | Retrieve an execution with step progress |
| `POST` | [`/api/v1/schedule-executions/{id}/cancel`](executions.md#cancel-a-schedule-execution) | Cancel a running execution |
| `GET` | [`/api/v1/schedule-executions/active`](executions.md#list-active-executions) | List currently running executions |
