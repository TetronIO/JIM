---
title: Update a Schedule
---

# Update a Schedule

Updates a schedule's properties and steps. All fields are optional; only include the fields you want to change.

```
PUT /api/v1/schedules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the schedule |

## Request Body

The same fields as [Create a Schedule](create.md#request-body).

!!! warning
    The `steps` field, when provided, **replaces all existing steps**. To add or remove a single step, include the full desired step list. Use the PowerShell cmdlets `Add-JIMScheduleStep` and `Remove-JIMScheduleStep` for incremental step management.

## Examples

=== "curl"

    ```bash
    # Update schedule name and enable it
    curl -X PUT https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Daily Delta Sync (Production)",
        "isEnabled": true
      }'

    # Change to interval-based timing
    curl -X PUT https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "patternType": "Interval",
        "daysOfWeek": "1,2,3,4,5",
        "intervalValue": 2,
        "intervalUnit": "Hours",
        "intervalWindowStart": "08:00",
        "intervalWindowEnd": "18:00"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Update schedule name
    Set-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -Name "Daily Delta Sync (Production)"

    # Change to interval-based timing
    Set-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -PatternType Interval `
        -DaysOfWeek @(1,2,3,4,5) `
        -IntervalValue 2 `
        -IntervalUnit Hours `
        -IntervalWindowStart "08:00" `
        -IntervalWindowEnd "18:00"
    ```

## Response

Returns `200 OK` with the updated [Schedule object](index.md#the-schedule-object) including steps.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name too long, invalid pattern configuration) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Schedule does not exist |
