---
title: Attributes
---

# Attributes

Attributes belong to an object type and represent individual fields discovered from the external identity store (e.g. `displayName`, `mail`, `objectGUID`). You can configure which attributes are selected for synchronisation and designate external ID attributes used for object matching.

### The Attribute Object

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Attribute name |
| `description` | string, nullable | Optional description |
| `className` | string, nullable | Attribute class name (connector-specific) |
| `created` | datetime | UTC timestamp when discovered |
| `type` | string | Data type: `String`, `Integer`, `Boolean`, `DateTime`, `Guid`, `Reference` |
| `attributePlurality` | string | `Single` or `Multi` (multi-valued attributes) |
| `selected` | boolean | Whether this attribute is included in synchronisation |
| `isExternalId` | boolean | Designated as the primary external identifier for object matching |
| `isSecondaryExternalId` | boolean | Designated as a secondary external identifier |
| `selectionLocked` | boolean | `true` if the attribute is an external ID (cannot be deselected) |
| `writability` | string | `ReadOnly` or `ReadWrite` |

---

## Update an Attribute

Updates the selection state and external ID designation of a single attribute.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `objectTypeId` | integer | ID of the object type |
| `attributeId` | integer | ID of the attribute |

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `selected` | boolean | No | Include this attribute in synchronisation |
| `isExternalId` | boolean | No | Designate as primary external ID |
| `isSecondaryExternalId` | boolean | No | Designate as secondary external ID |

!!! note
    Setting `isExternalId` to `true` automatically clears it from any other attribute in the same object type. External ID and secondary external ID attributes are automatically selected and cannot be deselected.

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/object-types/10/attributes/100 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "selected": true,
        "isExternalId": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 `
        -ObjectTypeId 10 -AttributeId 100 `
        -Selected $true -IsExternalId $true
    ```

### Response

Returns `200 OK` with the updated attribute object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid request (e.g. trying to deselect an external ID attribute) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system, object type, or attribute does not exist |

---

## Bulk Update Attributes

Updates multiple attributes in a single request. This is more efficient than individual updates when configuring attribute selections after a schema import.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/bulk-update
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `objectTypeId` | integer | ID of the object type |

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `attributes` | object | Yes | Dictionary of attribute ID to update request. Each value has the same fields as [Update an Attribute](#update-an-attribute). |

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/object-types/10/attributes/bulk-update \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "attributes": {
          "100": { "selected": true, "isExternalId": true },
          "101": { "selected": true },
          "102": { "selected": true },
          "103": { "selected": false }
        }
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    $updates = @{
        100 = @{ selected = $true; isExternalId = $true }
        101 = @{ selected = $true }
        102 = @{ selected = $true }
        103 = @{ selected = $false }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 `
        -ObjectTypeId 10 -AttributeUpdates $updates
    ```

### Response

Returns `200 OK` with a summary of the operation.

```json
{
  "activityId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "updatedCount": 3,
  "updatedAttributes": [
    {
      "id": 100,
      "name": "objectGUID",
      "selected": true,
      "isExternalId": true,
      "isSecondaryExternalId": false,
      "selectionLocked": true
    }
  ],
  "errors": [
    {
      "attributeId": 103,
      "errorMessage": "Cannot deselect attribute that is designated as external ID"
    }
  ]
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `activityId` | guid | Activity ID for the bulk operation |
| `updatedCount` | integer | Number of attributes successfully updated |
| `updatedAttributes` | array | Updated attribute objects |
| `errors` | array, nullable | Per-attribute errors (attribute ID and error message) |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Empty attributes dictionary or invalid request |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or object type does not exist |
