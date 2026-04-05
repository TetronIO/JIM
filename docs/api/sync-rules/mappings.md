---
title: Attribute Mappings
---

# Attribute Mappings

Attribute mappings define how individual attributes flow between a connected system and the metaverse. Each mapping has a target attribute and one or more sources, which can be direct attribute references or expressions.

### The Mapping Object

```json
{
  "id": 1,
  "created": "2026-01-15T10:00:00Z",
  "targetMetaverseAttributeId": 5,
  "targetMetaverseAttributeName": "displayName",
  "targetConnectedSystemAttributeId": null,
  "targetConnectedSystemAttributeName": null,
  "sourceType": "AttributeMapping",
  "sources": [
    {
      "id": 1,
      "order": 0,
      "connectedSystemAttributeId": 100,
      "connectedSystemAttributeName": "displayName",
      "metaverseAttributeId": null,
      "metaverseAttributeName": null,
      "expression": null
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `created` | datetime | UTC creation timestamp |
| `targetMetaverseAttributeId` | integer, nullable | Target MV attribute (import rules) |
| `targetMetaverseAttributeName` | string, nullable | Target MV attribute name |
| `targetConnectedSystemAttributeId` | integer, nullable | Target CS attribute (export rules) |
| `targetConnectedSystemAttributeName` | string, nullable | Target CS attribute name |
| `sourceType` | string | `AttributeMapping`, `ExpressionMapping`, or `AdvancedMapping` |
| `sources` | array | Ordered list of mapping sources |

### Source Types

- **AttributeMapping**: Direct one-to-one attribute flow (single source, no expression)
- **ExpressionMapping**: Expression-based transformation (single source with an expression)
- **AdvancedMapping**: Multiple sources or complex configurations

---

## List Mappings

Returns all attribute mappings for a sync rule.

```
GET /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `syncRuleId` | integer | ID of the sync rule |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/1/mappings \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMSyncRuleMapping -SyncRuleId 1
    ```

### Response

Returns `200 OK` with an array of mapping objects.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Sync rule does not exist |

---

## Retrieve a Mapping

```
GET /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/1/mappings/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 1
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `404` | `NOT_FOUND` | Sync rule or mapping does not exist |

---

## Create a Mapping

Creates a new attribute mapping for a sync rule.

```
POST /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `targetMetaverseAttributeId` | integer | Conditional | Target MV attribute (required for import rules) |
| `targetConnectedSystemAttributeId` | integer | Conditional | Target CS attribute (required for export rules) |
| `sources` | array | Yes | At least one source (see below) |

Each source in the `sources` array:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `order` | integer | No | Evaluation order (default: 0) |
| `connectedSystemAttributeId` | integer | Conditional | Source CS attribute (import rules) |
| `metaverseAttributeId` | integer | Conditional | Source MV attribute (export rules) |
| `expression` | string | No | DynamicExpresso expression |

### Examples

=== "curl"

    ```bash
    # Direct attribute mapping (import: CS displayName -> MV displayName)
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/1/mappings \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "targetMetaverseAttributeId": 5,
        "sources": [
          {
            "order": 0,
            "connectedSystemAttributeId": 100
          }
        ]
      }'

    # Expression mapping (import: build displayName from givenName + sn)
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/1/mappings \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "targetMetaverseAttributeId": 5,
        "sources": [
          {
            "order": 0,
            "expression": "cs[\"givenName\"] + \" \" + cs[\"sn\"]"
          }
        ]
      }'

    # Export mapping (MV displayName -> CS displayName)
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/2/mappings \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "targetConnectedSystemAttributeId": 100,
        "sources": [
          {
            "order": 0,
            "metaverseAttributeId": 5
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Direct attribute mapping (import)
    New-JIMSyncRuleMapping -SyncRuleId 1 `
        -TargetMetaverseAttributeId 5 `
        -SourceConnectedSystemAttributeId 100

    # Expression mapping (import)
    New-JIMSyncRuleMapping -SyncRuleId 1 `
        -TargetMetaverseAttributeId 5 `
        -Expression 'cs["givenName"] + " " + cs["sn"]'

    # Export mapping
    New-JIMSyncRuleMapping -SyncRuleId 2 `
        -TargetConnectedSystemAttributeId 100 `
        -SourceMetaverseAttributeId 5
    ```

### Response

Returns `201 Created` with the mapping object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid mapping configuration (e.g. missing target, invalid expression) |
| `401` | `UNAUTHORISED` | Authentication required |
| `404` | `NOT_FOUND` | Sync rule does not exist |

---

## Delete a Mapping

```
DELETE /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/sync-rules/1/mappings/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 1 -Force
    ```

### Response

Returns `204 No Content` on success.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `404` | `NOT_FOUND` | Sync rule or mapping does not exist |
