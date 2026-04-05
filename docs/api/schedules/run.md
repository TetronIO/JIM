---
title: Run a Schedule
---

# Run a Schedule

Manually triggers a schedule execution. The execution is queued and begins processing asynchronously. Use the [Schedule Executions](executions.md) endpoints to monitor progress.

```
POST /api/v1/schedules/{id}/run
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the schedule |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890/run \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Trigger and return immediately
    Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

    # Trigger and wait for completion
    Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Wait

    # Trigger and get the execution object back
    $execution = Start-JIMSchedule -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -PassThru
    ```

## Response

Returns `202 Accepted` with the execution reference.

```json
{
  "executionId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "message": "Schedule execution queued successfully."
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `executionId` | guid | ID of the created execution (use to poll progress) |
| `message` | string | Confirmation message |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Schedule does not exist |
