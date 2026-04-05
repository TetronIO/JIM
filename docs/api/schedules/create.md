---
title: Create a Schedule
---

# Create a Schedule

Creates a new schedule. Steps can be included in the creation request or added afterwards.

```
POST /api/v1/schedules
```

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Schedule name (1-200 characters) |
| `description` | string | No | Description (max 1000 characters) |
| `triggerType` | string | No | `Cron` or `Manual` (default: `Manual`) |
| `patternType` | string | No | `SpecificTimes`, `Interval`, or `Custom` (default: `SpecificTimes`) |
| `daysOfWeek` | string | No | Comma-separated day numbers: 0=Sun, 1=Mon, ..., 6=Sat |
| `runTimes` | string | No | Comma-separated 24h times (e.g. `"06:00,12:00"`) |
| `intervalValue` | integer | No | Interval count (1-59) for Interval pattern |
| `intervalUnit` | string | No | `Minutes` or `Hours` (default: `Hours`) |
| `intervalWindowStart` | string | No | Window start time (e.g. `"08:00"`) |
| `intervalWindowEnd` | string | No | Window end time (e.g. `"18:00"`) |
| `cronExpression` | string | No | Raw cron expression for Custom pattern |
| `isEnabled` | boolean | No | Enable on creation (default: `false`) |
| `steps` | array | No | List of [schedule steps](#step-parameters) |

### Step Parameters

Each step in the `steps` array accepts:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `stepIndex` | integer | Yes | Execution order (0-based). Same index = parallel execution. |
| `name` | string | No | Step name (max 200 characters) |
| `executionMode` | string | No | `Sequential` or `ParallelWithPrevious` (default: `Sequential`) |
| `stepType` | string | Yes | `RunProfile`, `PowerShell`, `Executable`, or `SqlScript` |
| `continueOnFailure` | boolean | No | Continue on failure (default: `false`) |
| `timeoutSeconds` | integer | No | Timeout in seconds (1-86400) |
| `connectedSystemId` | integer | No | Connected system ID (RunProfile steps) |
| `runProfileId` | integer | No | Run profile ID (RunProfile steps) |
| `scriptPath` | string | No | Script file path (PowerShell steps, max 500 characters) |
| `arguments` | string | No | Arguments (PowerShell and Executable steps, max 2000 characters) |
| `executablePath` | string | No | Executable path (Executable steps, max 500 characters) |
| `workingDirectory` | string | No | Working directory (Executable steps, max 500 characters) |
| `sqlConnectionString` | string | No | Connection string (SqlScript steps, max 500 characters) |
| `sqlScriptPath` | string | No | SQL script path (SqlScript steps, max 500 characters) |

## Examples

=== "curl"

    ```bash
    # Create a manual schedule with run profile steps
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Delta Sync",
        "description": "Import and sync changes from HR and LDAP",
        "triggerType": "Manual",
        "steps": [
          {
            "stepIndex": 0,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 1
          },
          {
            "stepIndex": 0,
            "stepType": "RunProfile",
            "executionMode": "ParallelWithPrevious",
            "connectedSystemId": 2,
            "runProfileId": 3
          },
          {
            "stepIndex": 1,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 2
          }
        ]
      }'

    # Create a cron schedule that runs weekdays at 06:00
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Daily Import",
        "triggerType": "Cron",
        "patternType": "SpecificTimes",
        "daysOfWeek": "1,2,3,4,5",
        "runTimes": "06:00",
        "isEnabled": true,
        "steps": []
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create a manual schedule
    New-JIMSchedule -Name "Delta Sync" `
        -Description "Import and sync changes from HR and LDAP" `
        -TriggerType Manual -PassThru

    # Create a cron schedule that runs weekdays at 06:00
    New-JIMSchedule -Name "Daily Import" `
        -TriggerType Cron `
        -PatternType SpecificTimes `
        -DaysOfWeek @(1,2,3,4,5) `
        -RunTimes @("06:00") `
        -Enabled
    ```

## Response

Returns `201 Created` with the full [Schedule object](index.md#the-schedule-object) including steps.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name too long, invalid cron expression, missing step configuration) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
