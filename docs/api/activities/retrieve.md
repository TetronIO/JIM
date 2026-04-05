---
title: Retrieve an Activity
---

# Retrieve an Activity

Returns the full details of an activity, including error information and context-specific IDs. For run profile activities, execution statistics are automatically included.

```
GET /api/v1/activities/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the activity |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/activities/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

## Response

Returns `200 OK` with the full [Activity object](index.md#the-activity-object), plus these detail-only fields:

| Field | Type | Description |
|-------|------|-------------|
| `parentActivityId` | guid, nullable | Parent activity ID |
| `warningMessage` | string, nullable | Non-fatal warning message |
| `errorMessage` | string, nullable | Error message if failed |
| `errorStackTrace` | string, nullable | Stack trace for debugging |
| `connectedSystemId` | integer, nullable | Associated connected system |
| `connectedSystemRunProfileId` | integer, nullable | Associated run profile |
| `syncRuleId` | integer, nullable | Associated sync rule |
| `metaverseObjectId` | guid, nullable | Associated metaverse object |
| `executionStats` | object, nullable | Execution statistics (run profile activities only; see [Stats](stats.md)) |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Activity does not exist |
