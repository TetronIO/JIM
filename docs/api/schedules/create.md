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

## Trigger Examples

### Manual Trigger

A manual schedule only runs when explicitly triggered via the [Run](run.md) endpoint or `Start-JIMSchedule`.

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Ad-Hoc Full Sync",
        "description": "Full import and sync, triggered manually when needed",
        "triggerType": "Manual"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMSchedule -Name "Ad-Hoc Full Sync" `
        -Description "Full import and sync, triggered manually when needed" `
        -TriggerType Manual
    ```

### Specific Times (run at fixed times on selected days)

Runs at one or more specific times on the chosen days of the week. This is the most common pattern for daily sync schedules.

=== "curl"

    ```bash
    # Weekdays at 06:00
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Morning Import",
        "triggerType": "Cron",
        "patternType": "SpecificTimes",
        "daysOfWeek": "1,2,3,4,5",
        "runTimes": "06:00",
        "isEnabled": true
      }'

    # Every day at 06:00, 12:00, and 18:00
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Three-Daily Sync",
        "triggerType": "Cron",
        "patternType": "SpecificTimes",
        "daysOfWeek": "0,1,2,3,4,5,6",
        "runTimes": "06:00,12:00,18:00",
        "isEnabled": true
      }'

    # Weekends only at 02:00 (maintenance window)
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Weekend Full Import",
        "triggerType": "Cron",
        "patternType": "SpecificTimes",
        "daysOfWeek": "0,6",
        "runTimes": "02:00",
        "isEnabled": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Weekdays at 06:00
    New-JIMSchedule -Name "Morning Import" `
        -TriggerType Cron `
        -PatternType SpecificTimes `
        -DaysOfWeek @(1,2,3,4,5) `
        -RunTimes @("06:00") `
        -Enabled

    # Every day at 06:00, 12:00, and 18:00
    New-JIMSchedule -Name "Three-Daily Sync" `
        -TriggerType Cron `
        -PatternType SpecificTimes `
        -DaysOfWeek @(0,1,2,3,4,5,6) `
        -RunTimes @("06:00","12:00","18:00") `
        -Enabled

    # Weekends only at 02:00
    New-JIMSchedule -Name "Weekend Full Import" `
        -TriggerType Cron `
        -PatternType SpecificTimes `
        -DaysOfWeek @(0,6) `
        -RunTimes @("02:00") `
        -Enabled
    ```

### Interval (run repeatedly within a time window)

Runs at regular intervals on selected days, constrained to a time window. Useful for near-real-time delta sync during business hours.

=== "curl"

    ```bash
    # Every 30 minutes on weekdays between 08:00 and 18:00
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Delta Sync (Business Hours)",
        "triggerType": "Cron",
        "patternType": "Interval",
        "daysOfWeek": "1,2,3,4,5",
        "intervalValue": 30,
        "intervalUnit": "Minutes",
        "intervalWindowStart": "08:00",
        "intervalWindowEnd": "18:00",
        "isEnabled": true
      }'

    # Every 2 hours, all day, every day
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Regular Sync",
        "triggerType": "Cron",
        "patternType": "Interval",
        "daysOfWeek": "0,1,2,3,4,5,6",
        "intervalValue": 2,
        "intervalUnit": "Hours",
        "intervalWindowStart": "00:00",
        "intervalWindowEnd": "23:59",
        "isEnabled": true
      }'

    # Every 15 minutes on weekdays during the morning
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Frequent Morning Sync",
        "triggerType": "Cron",
        "patternType": "Interval",
        "daysOfWeek": "1,2,3,4,5",
        "intervalValue": 15,
        "intervalUnit": "Minutes",
        "intervalWindowStart": "07:00",
        "intervalWindowEnd": "12:00",
        "isEnabled": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Every 30 minutes on weekdays between 08:00 and 18:00
    New-JIMSchedule -Name "Delta Sync (Business Hours)" `
        -TriggerType Cron `
        -PatternType Interval `
        -DaysOfWeek @(1,2,3,4,5) `
        -IntervalValue 30 `
        -IntervalUnit Minutes `
        -IntervalWindowStart "08:00" `
        -IntervalWindowEnd "18:00" `
        -Enabled

    # Every 2 hours, all day, every day
    New-JIMSchedule -Name "Regular Sync" `
        -TriggerType Cron `
        -PatternType Interval `
        -DaysOfWeek @(0,1,2,3,4,5,6) `
        -IntervalValue 2 `
        -IntervalUnit Hours `
        -IntervalWindowStart "00:00" `
        -IntervalWindowEnd "23:59" `
        -Enabled

    # Every 15 minutes on weekdays during the morning
    New-JIMSchedule -Name "Frequent Morning Sync" `
        -TriggerType Cron `
        -PatternType Interval `
        -DaysOfWeek @(1,2,3,4,5) `
        -IntervalValue 15 `
        -IntervalUnit Minutes `
        -IntervalWindowStart "07:00" `
        -IntervalWindowEnd "12:00" `
        -Enabled
    ```

### Custom Cron Expression (full control)

For advanced scheduling needs, provide a raw cron expression. Use this when the SpecificTimes and Interval patterns don't cover your requirements.

=== "curl"

    ```bash
    # First Monday of every month at 03:00
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Monthly Full Import",
        "triggerType": "Cron",
        "patternType": "Custom",
        "cronExpression": "0 3 1-7 * 1",
        "isEnabled": true
      }'

    # Every 5 minutes (continuous delta sync)
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Continuous Delta Sync",
        "triggerType": "Cron",
        "patternType": "Custom",
        "cronExpression": "*/5 * * * *",
        "isEnabled": true
      }'

    # Quarterly (first day of Jan, Apr, Jul, Oct at 01:00)
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Quarterly Audit Export",
        "triggerType": "Cron",
        "patternType": "Custom",
        "cronExpression": "0 1 1 1,4,7,10 *",
        "isEnabled": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # First Monday of every month at 03:00
    New-JIMSchedule -Name "Monthly Full Import" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "0 3 1-7 * 1" `
        -Enabled

    # Every 5 minutes (continuous delta sync)
    New-JIMSchedule -Name "Continuous Delta Sync" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "*/5 * * * *" `
        -Enabled

    # Quarterly (first day of Jan, Apr, Jul, Oct at 01:00)
    New-JIMSchedule -Name "Quarterly Audit Export" `
        -TriggerType Cron `
        -PatternType Custom `
        -CronExpression "0 1 1 1,4,7,10 *" `
        -Enabled
    ```

## Step Examples

### Sequential Steps (import, then sync, then export)

Steps with incrementing `stepIndex` values run one after the other:

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Full Pipeline",
        "triggerType": "Manual",
        "steps": [
          {
            "stepIndex": 0,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 1
          },
          {
            "stepIndex": 1,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 2
          },
          {
            "stepIndex": 2,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 3
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create the schedule, then add steps one by one
    $schedule = New-JIMSchedule -Name "Full Pipeline" -TriggerType Manual -PassThru

    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Delta Import"

    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Delta Sync"

    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Export"
    ```

### Parallel Steps (import from multiple systems simultaneously)

Steps with the same `stepIndex` run in parallel. This is useful for importing from independent connected systems at the same time:

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Multi-System Sync",
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
          },
          {
            "stepIndex": 1,
            "stepType": "RunProfile",
            "executionMode": "ParallelWithPrevious",
            "connectedSystemId": 2,
            "runProfileId": 4
          },
          {
            "stepIndex": 2,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 5
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    $schedule = New-JIMSchedule -Name "Multi-System Sync" -TriggerType Manual -PassThru

    # Step 0: import from both systems in parallel
    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Delta Import"

    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "HR Database" -RunProfileName "Delta Import" `
        -Parallel

    # Step 1: sync both systems in parallel
    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Delta Sync"

    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "HR Database" -RunProfileName "Delta Sync" `
        -Parallel

    # Step 2: export to LDAP
    Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" -RunProfileName "Export"
    ```

### Mixed Step Types

Schedules can combine run profile steps with PowerShell scripts, executables, and SQL scripts:

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Import with Pre and Post Scripts",
        "triggerType": "Manual",
        "steps": [
          {
            "stepIndex": 0,
            "stepType": "PowerShell",
            "name": "Pre-import validation",
            "scriptPath": "/opt/jim/scripts/validate-source.ps1",
            "timeoutSeconds": 300
          },
          {
            "stepIndex": 1,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 1
          },
          {
            "stepIndex": 2,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 2
          },
          {
            "stepIndex": 3,
            "stepType": "PowerShell",
            "name": "Post-sync notifications",
            "scriptPath": "/opt/jim/scripts/send-report.ps1",
            "arguments": "-ReportType Summary -SendEmail",
            "continueOnFailure": true,
            "timeoutSeconds": 120
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # PowerShell and executable steps are configured via the API directly,
    # as Add-JIMScheduleStep currently supports RunProfile steps only.
    # Use Set-JIMSchedule with a full steps array for mixed step types.

    $steps = @(
        @{
            stepIndex      = 0
            stepType       = "PowerShell"
            name           = "Pre-import validation"
            scriptPath     = "/opt/jim/scripts/validate-source.ps1"
            timeoutSeconds = 300
        },
        @{
            stepIndex        = 1
            stepType         = "RunProfile"
            connectedSystemId = 1
            runProfileId     = 1
        },
        @{
            stepIndex        = 2
            stepType         = "RunProfile"
            connectedSystemId = 1
            runProfileId     = 2
        },
        @{
            stepIndex      = 3
            stepType       = "PowerShell"
            name           = "Post-sync notifications"
            scriptPath     = "/opt/jim/scripts/send-report.ps1"
            arguments      = "-ReportType Summary -SendEmail"
            continueOnFailure = $true
            timeoutSeconds = 120
        }
    )

    $schedule = New-JIMSchedule -Name "Import with Pre and Post Scripts" `
        -TriggerType Manual -PassThru

    Set-JIMSchedule -Id $schedule.id -Steps $steps
    ```

## Response

Returns `201 Created` with the full [Schedule object](index.md#the-schedule-object) including steps.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name too long, invalid cron expression, missing step configuration) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
