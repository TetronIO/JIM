---
title: Object Types
---

# Object Types

Object types represent the schema categories discovered from the external identity store (e.g. `user`, `group`, `organizationalUnit`). After [importing the schema](import-schema.md), you can select which object types to include in synchronisation and configure their behaviour.

---

## List Object Types

Returns all object types for a connected system, including their attributes.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/object-types \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # No dedicated cmdlet for listing object types yet.
    # Use the curl example or call the API directly.
    ```

### Response

Returns `200 OK` with an array of object types.

```json
[
  {
    "id": 10,
    "name": "user",
    "created": "2026-01-15T09:31:00Z",
    "selected": true,
    "removeContributedAttributesOnObsoletion": false,
    "attributeCount": 47,
    "attributes": [
      {
        "id": 100,
        "name": "displayName",
        "description": null,
        "className": null,
        "created": "2026-01-15T09:31:00Z",
        "type": "String",
        "attributePlurality": "Single",
        "selected": true,
        "isExternalId": false,
        "isSecondaryExternalId": false,
        "selectionLocked": false,
        "writability": "ReadWrite"
      }
    ]
  }
]
```

### Object Type Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Object type name |
| `created` | datetime | UTC timestamp when discovered |
| `selected` | boolean | Whether this type is included in synchronisation |
| `removeContributedAttributesOnObsoletion` | boolean | Remove contributed attributes when a sync rule no longer applies |
| `attributeCount` | integer | Total number of attributes |
| `attributes` | array | Full list of attributes (see [Attributes](attributes.md)) |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |

---

## Update an Object Type

Updates the selection state and behaviour of an object type. All fields are optional.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `objectTypeId` | integer | ID of the object type |

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `selected` | boolean | No | Include this object type in synchronisation |
| `removeContributedAttributesOnObsoletion` | boolean | No | Remove contributed attributes from metaverse objects when a sync rule no longer applies |

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/object-types/10 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "selected": true,
        "removeContributedAttributesOnObsoletion": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ObjectTypeId 10 `
        -Selected $true `
        -RemoveContributedAttributesOnObsoletion $true
    ```

### Response

Returns `200 OK` with the updated object type.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid request body |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or object type does not exist |
