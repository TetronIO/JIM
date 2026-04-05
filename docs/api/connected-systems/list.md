---
title: List Connected Systems
---

# List Connected Systems

Returns a paginated list of connected systems. Results include summary information suitable for building overview screens; retrieve a specific connected system for full details including object types and attributes.

```
GET /api/v1/synchronisation/connected-systems
```

## Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `25`    | Items per page (1-100) |
| `sortBy`        | string  | No       |         | Sort field: `name`, `created`, `objectCount`, `connectorName` |
| `sortDirection` | string  | No       | `asc`   | Sort order: `asc` or `desc` |
| `filter`        | string  | No       |         | Search by name (case-insensitive partial match) |

## Examples

=== "curl"

    ```bash
    # List all connected systems
    curl https://jim.example.com/api/v1/synchronisation/connected-systems \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Second page, sorted by name
    curl "https://jim.example.com/api/v1/synchronisation/connected-systems?page=2&pageSize=10&sortBy=name" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by name
    curl "https://jim.example.com/api/v1/synchronisation/connected-systems?filter=ldap" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all connected systems
    Get-JIMConnectedSystem

    # Filter by name
    Get-JIMConnectedSystem -Filter "ldap"
    ```

## Response

Returns `200 OK` with a paginated list of connected system summaries.

```json
{
  "items": [
    {
      "id": 1,
      "name": "Corporate LDAP",
      "description": "Primary directory for employee accounts",
      "created": "2026-01-15T09:30:00Z",
      "status": "Active",
      "objectCount": 12450,
      "connectorsCount": 2,
      "pendingExportObjectsCount": 0,
      "connectorName": "JIM LDAP Connector",
      "connectorId": 3
    },
    {
      "id": 2,
      "name": "HR Database",
      "description": "Source of truth for employee records",
      "created": "2026-01-20T11:00:00Z",
      "status": "Active",
      "objectCount": 8200,
      "connectorsCount": 1,
      "pendingExportObjectsCount": 15,
      "connectorName": "JIM File Connector",
      "connectorId": 1
    }
  ],
  "totalCount": 2,
  "page": 1,
  "pageSize": 25,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

### List Item Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Display name |
| `description` | string, nullable | Optional description |
| `created` | datetime | UTC creation timestamp |
| `status` | string | `Active` or `Deleting` |
| `objectCount` | integer | Total objects in the connector space |
| `connectorsCount` | integer | Number of selected object types |
| `pendingExportObjectsCount` | integer | Exports queued for processing |
| `connectorName` | string | Name of the connector type |
| `connectorId` | integer | ID of the connector definition |

!!! tip
    The list response returns `ConnectedSystemHeader` summaries, which are lighter than the full detail object. Retrieve a specific connected system when you need object types, attributes, and configuration details.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid pagination parameters (e.g. page < 1, pageSize > 100) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
