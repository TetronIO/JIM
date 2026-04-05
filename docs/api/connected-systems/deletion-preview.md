---
title: Preview Deletion
---

# Preview Deletion

Returns a detailed impact analysis of what would be affected if the connected system were deleted. Use this before calling [Delete](delete.md) to understand the scope of the operation.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/deletion-preview
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/deletion-preview \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId 1
    ```

## Response

Returns `200 OK` with the deletion impact analysis.

```json
{
  "connectedSystemId": 1,
  "connectedSystemName": "Corporate LDAP",
  "connectedSystemObjectCount": 12450,
  "syncRuleCount": 3,
  "runProfileCount": 4,
  "partitionCount": 2,
  "containerCount": 15,
  "pendingExportCount": 0,
  "activityCount": 156,
  "joinedMvoCount": 12200,
  "mvosWithOtherConnectorsCount": 11800,
  "mvosWithDeletionRuleCount": 400,
  "mvosWithGracePeriodCount": 50,
  "warnings": [
    "400 metaverse objects have deletion rules that may trigger deletion"
  ],
  "estimatedDeletionTime": "00:02:30",
  "willRunAsBackgroundJob": true,
  "hasRunningSyncOperation": false
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `connectedSystemName` | string | Name of the connected system |
| `connectedSystemObjectCount` | integer | Number of objects in the connector space that will be removed |
| `syncRuleCount` | integer | Number of sync rules referencing this system |
| `runProfileCount` | integer | Number of run profiles that will be deleted |
| `partitionCount` | integer | Number of partitions that will be removed |
| `containerCount` | integer | Number of containers that will be removed |
| `pendingExportCount` | integer | Number of pending exports that will be discarded |
| `activityCount` | integer | Number of activity records associated with this system |
| `joinedMvoCount` | integer | Number of metaverse objects currently joined to this system |
| `mvosWithOtherConnectorsCount` | integer | Joined MVOs that also have joins from other connected systems (these will not be deleted) |
| `mvosWithDeletionRuleCount` | integer | Joined MVOs where deletion rules may trigger MVO deletion |
| `mvosWithGracePeriodCount` | integer | MVOs that will enter a grace period before potential deletion |
| `warnings` | array | Human-readable warnings about potential impacts |
| `estimatedDeletionTime` | string | Estimated time to complete deletion (HH:MM:SS format) |
| `willRunAsBackgroundJob` | boolean | Whether deletion will be queued rather than completing immediately |
| `hasRunningSyncOperation` | boolean | Whether a sync operation is currently running against this system |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
