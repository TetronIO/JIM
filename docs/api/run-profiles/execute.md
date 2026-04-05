---
title: Execute a Run Profile
---

# Execute a Run Profile

Triggers execution of a run profile. The operation is queued and begins processing asynchronously. Use the returned activity ID to monitor progress via the Activities endpoint.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/run-profiles/{runProfileId}/execute
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `runProfileId` | integer | ID of the run profile |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles/1/execute \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Execute and return immediately
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

    # Execute by name
    Start-JIMRunProfile -ConnectedSystemName "Corporate LDAP" `
        -RunProfileName "Delta Import"

    # Execute and wait for completion (with progress display)
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait

    # Execute with a timeout
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 `
        -Wait -Timeout 600

    # Execute via pipeline and get the response
    Get-JIMRunProfile -ConnectedSystemId 1 |
        Where-Object { $_.name -eq "Delta Import" } |
        Start-JIMRunProfile -PassThru
    ```

## Response

Returns `202 Accepted` with the execution reference.

```json
{
  "activityId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "taskId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "message": "Run profile execution queued successfully.",
  "warnings": []
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `activityId` | guid | Activity ID for tracking progress |
| `taskId` | guid | Worker task ID for the queued operation |
| `message` | string | Confirmation message |
| `warnings` | array | Warning messages (e.g. partition validation issues) |

!!! tip
    The PowerShell cmdlet's `-Wait` flag polls the activity status automatically, displaying progress with object counts and elapsed time. This is the easiest way to execute a run profile and wait for it to finish.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Validation failed (e.g. connector settings invalid) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or run profile does not exist |
