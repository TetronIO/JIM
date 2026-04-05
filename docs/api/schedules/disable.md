---
title: Disable a Schedule
---

# Disable a Schedule

Disables a schedule so it will no longer run automatically. Any currently running execution is not affected; use [Cancel](executions.md#cancel-a-schedule-execution) to stop a running execution.

```
POST /api/v1/schedules/{id}/disable
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the schedule |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890/disable \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Disable-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

## Response

Returns `200 OK` with the updated [Schedule object](index.md#the-schedule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Schedule does not exist |
