---
title: Connector Space
---

# Connector Space

The connector space contains objects imported from the external identity store. Each connector space object (CSO) holds the attribute values as they exist in the source system, and tracks its join status to the metaverse.

---

## Retrieve a Connector Space Object

Returns full details of a connector space object, including its attribute values, join status, and linked metaverse object.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{id}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `id` | guid | ID of the connector space object |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/connector-space/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystemObject -ConnectedSystemId 1 `
        -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

### Response

Returns `200 OK` with the connector space object.

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "created": "2026-01-15T10:00:00Z",
  "lastUpdated": "2026-03-20T14:12:00Z",
  "status": "Normal",
  "joinType": "Joined",
  "dateJoined": "2026-01-15T10:00:05Z",
  "displayName": "Jane Smith",
  "connectedSystemId": 1,
  "connectedSystemName": "Corporate LDAP",
  "typeId": 10,
  "typeName": "user",
  "metaverseObjectId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "metaverseObjectDisplayName": "Jane Smith",
  "attributeValues": [
    {
      "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "attributeId": 100,
      "attributeName": "displayName",
      "stringValue": "Jane Smith",
      "dateTimeValue": null,
      "intValue": null,
      "guidValue": null,
      "boolValue": null,
      "referenceValueId": null
    }
  ],
  "attributeValueSummaries": [
    {
      "attributeName": "memberOf",
      "totalCount": 25,
      "returnedCount": 10,
      "hasMore": true
    }
  ]
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `created` | datetime | UTC timestamp when imported |
| `lastUpdated` | datetime, nullable | UTC timestamp of last modification |
| `status` | string | Object status in the connector space |
| `joinType` | string | Join state: `Joined`, `Projected`, `Disconnected` |
| `dateJoined` | datetime, nullable | When the object was joined to the metaverse |
| `displayName` | string, nullable | Computed display name |
| `connectedSystemId` | integer | Parent connected system ID |
| `connectedSystemName` | string | Parent connected system name |
| `typeId` | integer | Object type ID |
| `typeName` | string | Object type name |
| `metaverseObjectId` | guid, nullable | Linked metaverse object ID (if joined) |
| `metaverseObjectDisplayName` | string, nullable | Linked metaverse object display name |
| `attributeValues` | array | Attribute values (may be truncated for multi-valued attributes) |
| `attributeValueSummaries` | array, nullable | Summary counts for multi-valued attributes that have more values than returned |

!!! tip
    For multi-valued attributes with many values, the response truncates the `attributeValues` array and includes an `attributeValueSummaries` entry showing the total count. Use [List Attribute Values](#list-attribute-values) to page through all values.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or object does not exist |

---

## List Attribute Values

Returns a paginated list of values for a specific attribute on a connector space object. This is primarily useful for multi-valued attributes (e.g. `memberOf`) that may have hundreds or thousands of values.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{csoId}/attributes/{attributeName}/values
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `csoId` | guid | ID of the connector space object |
| `attributeName` | string | Name of the attribute |

### Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `page` | integer | `1` | Page number (1-based) |
| `pageSize` | integer | `50` | Items per page |
| `search` | string | | Filter values by search term |

### Examples

=== "curl"

    ```bash
    curl "https://jim.example.com/api/v1/synchronisation/connected-systems/1/connector-space/a1b2c3d4-e5f6-7890-abcd-ef1234567890/attributes/memberOf/values?page=1&pageSize=20" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 1 `
        -CsoId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -AttributeName "memberOf"
    ```

### Response

Returns `200 OK` with a paginated list of attribute values.

```json
{
  "items": [
    {
      "id": "d4e5f6a7-b8c9-0123-defa-234567890123",
      "attributeId": 105,
      "attributeName": "memberOf",
      "stringValue": "CN=Engineering,OU=Groups,DC=example,DC=com",
      "dateTimeValue": null,
      "intValue": null,
      "guidValue": null,
      "boolValue": null,
      "referenceValueId": null
    }
  ],
  "totalCount": 25,
  "page": 1,
  "pageSize": 20,
  "totalPages": 2,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system, object, or attribute does not exist |

---

## Count Unresolved References

Returns the number of unresolved reference attribute values in the connector space. Unresolved references indicate that an attribute points to an object that has not yet been imported.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/unresolved-references/count
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/connector-space/unresolved-references/count \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 1
    ```

### Response

Returns `200 OK` with an integer count.

```json
42
```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
