---
title: Object Types
---

# Object Types

Object types define the schema categories in the metaverse (e.g. `person`, `group`). Each object type has associated attributes and configurable deletion rules that control what happens when all connector space objects are disconnected.

### The Object Type Object

```json
{
  "id": 1,
  "name": "person",
  "pluralName": "people",
  "created": "2026-01-10T09:00:00Z",
  "builtIn": true,
  "icon": "Person",
  "deletionRule": "WhenLastConnectorDisconnected",
  "deletionGracePeriod": "7.00:00:00",
  "deletionTriggerConnectedSystemIds": [],
  "attributes": [
    {
      "id": 1,
      "name": "displayName",
      "type": "Text",
      "attributePlurality": "SingleValued",
      "builtIn": true
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Singular name (e.g. `person`) |
| `pluralName` | string | Plural name (e.g. `people`) |
| `created` | datetime | UTC creation timestamp |
| `builtIn` | boolean | Whether this is a built-in type (cannot be deleted) |
| `icon` | string, nullable | MudBlazor icon name for the UI |
| `deletionRule` | string | Deletion behaviour (see below) |
| `deletionGracePeriod` | timespan, nullable | Grace period before deletion (e.g. `"7.00:00:00"` for 7 days) |
| `deletionTriggerConnectedSystemIds` | array | Connected system IDs that trigger deletion (for `WhenAuthoritativeSourceDisconnected`) |
| `attributes` | array | Associated attributes (detail view only) |

### Deletion Rules

| Rule | Description |
|------|-------------|
| `Manual` | Objects are never automatically deleted; manual intervention required |
| `WhenLastConnectorDisconnected` | Delete when all connector space objects are disconnected (after optional grace period) |
| `WhenAuthoritativeSourceDisconnected` | Delete when any authoritative source disconnects (requires `deletionTriggerConnectedSystemIds`) |

---

## List Object Types

Returns a paginated list of object types.

```
GET /api/v1/metaverse/object-types
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `25` | Items per page |
| `includeChildObjects` | boolean | No | `false` | Include child object counts |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/object-types \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMetaverseObjectType

    # By name
    Get-JIMMetaverseObjectType -Name "person"
    ```

### Response

Returns `200 OK` with a paginated list of object type summaries.

---

## Retrieve an Object Type

Returns the full details of an object type, including its associated attributes.

```
GET /api/v1/metaverse/object-types/{id}
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/object-types/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMetaverseObjectType -Id 1
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `404` | `NOT_FOUND` | Object type does not exist |

---

## Update an Object Type

Updates the deletion rules for an object type. Only deletion-related properties can be changed; the name and built-in status cannot be modified.

```
PUT /api/v1/metaverse/object-types/{id}
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `deletionRule` | string | No | `Manual`, `WhenLastConnectorDisconnected`, or `WhenAuthoritativeSourceDisconnected` |
| `deletionGracePeriod` | string | No | Grace period as a timespan (e.g. `"7.00:00:00"` for 7 days, `"00:00:00"` for immediate) |
| `deletionTriggerConnectedSystemIds` | array | Conditional | Required when deletion rule is `WhenAuthoritativeSourceDisconnected` |

### Examples

=== "curl"

    ```bash
    # Set 7-day grace period before deletion
    curl -X PUT https://jim.example.com/api/v1/metaverse/object-types/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "deletionRule": "WhenLastConnectorDisconnected",
        "deletionGracePeriod": "7.00:00:00"
      }'

    # Delete when HR system disconnects
    curl -X PUT https://jim.example.com/api/v1/metaverse/object-types/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "deletionRule": "WhenAuthoritativeSourceDisconnected",
        "deletionTriggerConnectedSystemIds": [1],
        "deletionGracePeriod": "30.00:00:00"
      }'

    # Disable automatic deletion
    curl -X PUT https://jim.example.com/api/v1/metaverse/object-types/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "deletionRule": "Manual"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Set 7-day grace period
    Set-JIMMetaverseObjectType -Id 1 `
        -DeletionRule WhenLastConnectorDisconnected `
        -DeletionGracePeriod ([TimeSpan]::FromDays(7))

    # Delete when HR system disconnects, 30-day grace period
    Set-JIMMetaverseObjectType -Id 1 `
        -DeletionRule WhenAuthoritativeSourceDisconnected `
        -DeletionTriggerConnectedSystemIds @(1) `
        -DeletionGracePeriod ([TimeSpan]::FromDays(30))

    # Disable automatic deletion
    Set-JIMMetaverseObjectType -Name "person" -DeletionRule Manual
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid configuration (e.g. missing trigger IDs for authoritative source rule) |
| `404` | `NOT_FOUND` | Object type does not exist |
