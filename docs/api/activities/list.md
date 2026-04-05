---
title: List Activities
---

# List Activities

Returns a paginated list of activities, with optional search filtering and sorting.

```
GET /api/v1/activities
```

## Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `25`    | Items per page (max 100) |
| `search`        | string  | No       |         | Filter by target name or type |
| `sortBy`        | string  | No       |         | Sort field: `created`, `executed`, `status`, `type`, `target` |
| `sortDirection` | string  | No       | `asc`   | Sort order: `asc` or `desc` |

## Examples

=== "curl"

    ```bash
    # List recent activities
    curl https://jim.example.com/api/v1/activities \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Search for import activities
    curl "https://jim.example.com/api/v1/activities?search=Import&sortBy=created&sortDirection=desc" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List recent activities
    Get-JIMActivity

    # Search for import activities
    Get-JIMActivity -Search "Full Import"

    # Paginated
    Get-JIMActivity -Page 2 -PageSize 50
    ```

## Response

Returns `200 OK` with a paginated list of activity summaries.

```json
{
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "created": "2026-04-05T06:00:00Z",
      "executed": "2026-04-05T06:00:01Z",
      "status": "Complete",
      "targetType": "ConnectedSystemRunProfile",
      "targetOperationType": "Execute",
      "targetName": "Delta Import",
      "targetContext": "Corporate LDAP",
      "message": "Completed successfully",
      "initiatedByType": "ApiKey",
      "initiatedById": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "initiatedByName": "automation-key",
      "objectsToProcess": 12450,
      "objectsProcessed": 12450,
      "executionTime": "00:01:30",
      "totalActivityTime": "00:01:32",
      "connectedSystemRunType": "DeltaImport",
      "totalAdded": 15,
      "totalUpdated": 230,
      "totalDeleted": 3,
      "totalErrors": 0,
      "childActivityCount": 0
    }
  ],
  "totalCount": 156,
  "page": 1,
  "pageSize": 25,
  "totalPages": 7,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
