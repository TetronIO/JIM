---
title: Delete a Connected System
---

# Delete a Connected System

Deletes a connected system and all associated data, including connector space objects, run profiles, pending exports, and partition/container hierarchy.

Depending on the volume of data, deletion may complete immediately or be queued as a background job.

```
DELETE /api/v1/synchronisation/connected-systems/{connectedSystemId}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `deleteChangeHistory` | boolean | `false` | Also delete change history records for objects in this connected system |

!!! warning
    Deletion is irreversible. Use the [deletion preview](deletion-preview.md) endpoint first to understand the impact.

## Examples

=== "curl"

    ```bash
    # Delete, preserving change history
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/connected-systems/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Delete including change history
    curl -X DELETE "https://jim.example.com/api/v1/synchronisation/connected-systems/1?deleteChangeHistory=true" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMConnectedSystem -Id 1
    ```

## Response

Returns `200 OK` if deletion completed immediately, or `202 Accepted` if queued as a background job.

```json
{
  "success": true,
  "outcome": "QueuedAsBackgroundJob",
  "errorMessage": null,
  "workerTaskId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "activityId": "b2c3d4e5-f6a7-8901-bcde-f12345678901"
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | Whether the deletion was initiated successfully |
| `outcome` | string | `CompletedImmediately`, `QueuedAsBackgroundJob`, `QueuedAfterSync`, or `Failed` |
| `errorMessage` | string, nullable | Error details if the deletion failed |
| `workerTaskId` | guid, nullable | Background task ID (when queued) |
| `activityId` | guid, nullable | Activity ID for tracking progress |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
