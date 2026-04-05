---
title: Delete a Schedule
---

# Delete a Schedule

Permanently deletes a schedule and all its steps. Execution history is preserved.

```
DELETE /api/v1/schedules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the schedule |

## Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

## Response

Returns `204 No Content` on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Schedule does not exist |
