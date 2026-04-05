---
title: Get Execution Statistics
---

# Get Execution Statistics

Returns detailed execution statistics for a run profile activity, including per-operation counters for import, sync, and export operations.

This endpoint only works for activities with target type `ConnectedSystemRunProfile`.

```
GET /api/v1/activities/{id}/stats
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the activity |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/activities/a1b2c3d4-e5f6-7890-abcd-ef1234567890/stats \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

    # Via pipeline from a run profile execution
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait -PassThru |
        ForEach-Object { Get-JIMActivityStats -Id $_.activityId }
    ```

## Response

Returns `200 OK` with execution statistics.

```json
{
  "totalObjectsProcessed": 12450,
  "totalObjectChangeCount": 248,
  "totalUnchanged": 12202,
  "totalObjectErrors": 0,
  "totalObjectTypes": 2,
  "totalCsoAdds": 15,
  "totalCsoUpdates": 230,
  "totalCsoDeletes": 3,
  "totalProjections": 12,
  "totalJoins": 3,
  "totalAttributeFlows": 1540,
  "totalDisconnections": 0,
  "totalDisconnectedOutOfScope": 0,
  "totalOutOfScopeRetainJoin": 0,
  "totalDriftCorrections": 0,
  "totalProvisioned": 0,
  "totalExported": 0,
  "totalDeprovisioned": 0,
  "totalPendingExports": 45,
  "totalPendingExportsConfirmed": 0,
  "totalPendingExportsRetrying": 0,
  "totalPendingExportsFailed": 0,
  "totalCreated": 0
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `totalObjectsProcessed` | integer | Total objects processed |
| `totalObjectChangeCount` | integer | Objects that had changes |
| `totalUnchanged` | integer | Objects with no changes |
| `totalObjectErrors` | integer | Objects with errors |
| `totalObjectTypes` | integer | Distinct object types processed |
| `totalCsoAdds` | integer | Connector space objects added (import) |
| `totalCsoUpdates` | integer | Connector space objects updated (import) |
| `totalCsoDeletes` | integer | Connector space objects deleted (import) |
| `totalProjections` | integer | New metaverse objects created (sync) |
| `totalJoins` | integer | Objects joined to metaverse (sync) |
| `totalAttributeFlows` | integer | Attribute values flowed (sync) |
| `totalDisconnections` | integer | Objects disconnected (sync) |
| `totalDisconnectedOutOfScope` | integer | Objects disconnected as out of scope (sync) |
| `totalOutOfScopeRetainJoin` | integer | Out-of-scope objects retaining their join (sync) |
| `totalDriftCorrections` | integer | Attribute drift corrections applied (sync) |
| `totalProvisioned` | integer | Objects provisioned to other systems (sync) |
| `totalExported` | integer | Objects exported (export) |
| `totalDeprovisioned` | integer | Objects deprovisioned (export) |
| `totalPendingExports` | integer | Pending exports created |
| `totalPendingExportsConfirmed` | integer | Pending exports confirmed (confirming import) |
| `totalPendingExportsRetrying` | integer | Pending exports retrying |
| `totalPendingExportsFailed` | integer | Pending exports that exceeded max retries |
| `totalCreated` | integer | Directly created objects |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Activity is not a run profile activity |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Activity does not exist |
