---
title: Retrieve a Schedule
---

# Retrieve a Schedule

Returns the full details of a schedule, including its steps in execution order.

```
GET /api/v1/schedules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the schedule |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeSteps
    ```

## Response

Returns `200 OK` with the full [Schedule object](index.md#the-schedule-object) including steps.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Schedule does not exist |
