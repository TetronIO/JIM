---
title: List Execution Items
---

# List Execution Items

Returns a paginated list of per-object execution items for a run profile activity. Each item represents the outcome of processing a single connector space object.

This endpoint only works for activities with target type `ConnectedSystemRunProfile`.

```
GET /api/v1/activities/{id}/items
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the activity |

## Query Parameters

| Parameter  | Type    | Required | Default | Description |
|------------|---------|----------|---------|-------------|
| `page`     | integer | No       | `1`     | Page number (1-based) |
| `pageSize` | integer | No       | `25`    | Items per page (max 100) |

## Examples

=== "curl"

    ```bash
    curl "https://jim.example.com/api/v1/activities/a1b2c3d4-e5f6-7890-abcd-ef1234567890/items?page=1&pageSize=50" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Get all execution items (auto-paginates)
    Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems
    ```

## Response

Returns `200 OK` with a paginated list of execution items.

```json
{
  "items": [
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "externalIdValue": "a4f2e8c1-3b7d-4e9a-b5c6-d8f0e1a2b3c4",
      "displayName": "Jane Smith",
      "connectedSystemObjectType": "user",
      "objectChangeType": "Update",
      "errorType": null,
      "outcomeSummary": "AttributeFlow:12,PendingExportCreated:1"
    },
    {
      "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "externalIdValue": "b5d3f9a2-4c8e-5f0b-a6d7-e9f1a2b3c4d5",
      "displayName": "John Doe",
      "connectedSystemObjectType": "user",
      "objectChangeType": "Add",
      "errorType": null,
      "outcomeSummary": "Projected:1,AttributeFlow:15,PendingExportCreated:2"
    }
  ],
  "totalCount": 248,
  "page": 1,
  "pageSize": 50,
  "totalPages": 5,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

### Item Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `externalIdValue` | string, nullable | External ID of the processed object |
| `displayName` | string, nullable | Display name of the object |
| `connectedSystemObjectType` | string, nullable | Object type (e.g. `user`, `group`) |
| `objectChangeType` | string | Change type: `Add`, `Update`, `Delete`, etc. |
| `errorType` | string, nullable | Error classification if processing failed (see below) |
| `outcomeSummary` | string, nullable | Comma-separated outcome summary (e.g. `"Projected:1,AttributeFlow:12"`) |

### Error Types

| Error Type | Description |
|------------|-------------|
| `AmbiguousMatch` | Multiple metaverse objects matched one connector space object |
| `CouldNotMatchObjectType` | No matching object type in schema or missing external ID |
| `MissingExternalIdAttributeValue` | Object missing its external ID attribute |
| `DuplicateObject` | Multiple objects with the same external ID in one import |
| `UnresolvedReference` | Reference attribute points to an unknown object |
| `UnhandledError` | Unexpected exception during processing |

!!! tip
    The `outcomeSummary` field provides a quick view of what happened to each object without needing to fetch the full sync outcome tree. Common outcomes include `Projected`, `Joined`, `AttributeFlow`, `PendingExportCreated`, `Disconnected`, and `Exported`.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Activity is not a run profile activity |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Activity does not exist |
