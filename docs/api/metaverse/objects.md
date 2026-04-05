---
title: Objects
---

# Objects

Metaverse objects are the identity records stored in JIM. Each object has a type, attribute values contributed by connected systems, and links to its connector space objects.

### The Object (Detail)

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "created": "2026-01-15T10:00:00Z",
  "lastUpdated": "2026-03-20T14:12:00Z",
  "displayName": "Jane Smith",
  "status": "Normal",
  "origin": "Projected",
  "lastConnectorDisconnectedDate": null,
  "isPendingDeletion": false,
  "deletionEligibleDate": null,
  "type": {
    "id": 1,
    "name": "person"
  },
  "attributeValues": [
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "attributeId": 5,
      "attributeName": "displayName",
      "attributeType": "Text",
      "attributePlurality": "SingleValued",
      "stringValue": "Jane Smith",
      "dateTimeValue": null,
      "intValue": null,
      "guidValue": null,
      "boolValue": null,
      "referenceValueId": null,
      "referenceValueDisplayName": null,
      "contributedBySystemId": 1,
      "contributedBySystemName": "Corporate LDAP"
    }
  ],
  "connectedSystemObjects": [
    {
      "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "connectedSystemId": 1,
      "connectedSystemName": "Corporate LDAP",
      "displayName": "Jane Smith"
    }
  ]
}
```

### Core Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `created` | datetime | UTC creation timestamp |
| `lastUpdated` | datetime, nullable | Last modification timestamp |
| `displayName` | string, nullable | Computed display name |
| `status` | string | `Normal` or `Obsolete` |
| `origin` | string | `Projected` (from connected system) or `Internal` (created directly in JIM) |
| `lastConnectorDisconnectedDate` | datetime, nullable | When the last connector space object was disconnected |
| `isPendingDeletion` | boolean | Whether the object is awaiting deletion |
| `deletionEligibleDate` | datetime, nullable | When the grace period expires |
| `type` | object | Object type (`id`, `name`) |
| `attributeValues` | array | All attribute values with type info and contributing system |
| `connectedSystemObjects` | array | Linked connector space objects |

---

## List Objects

Returns a paginated list of metaverse objects with optional filtering. The response includes the display name and any additionally requested attributes.

```
GET /api/v1/metaverse/objects
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `25` | Items per page (max 100) |
| `objectTypeId` | integer | No | | Filter by object type |
| `search` | string | No | | Search by display name (partial, case-insensitive) |
| `attributes` | string | No | | Comma-separated attribute names to include, or `*` for all |
| `filterAttributeName` | string | No | | Filter by specific attribute name |
| `filterAttributeValue` | string | No | | Filter by specific attribute value (exact, case-insensitive) |

### Examples

=== "curl"

    ```bash
    # List all people
    curl "https://jim.example.com/api/v1/metaverse/objects?objectTypeId=1" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Search by name with additional attributes
    curl "https://jim.example.com/api/v1/metaverse/objects?search=Smith&attributes=mail,department" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by department
    curl "https://jim.example.com/api/v1/metaverse/objects?filterAttributeName=department&filterAttributeValue=Engineering" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all people
    Get-JIMMetaverseObject -ObjectTypeName "person"

    # Search by name
    Get-JIMMetaverseObject -Search "Smith" -Attributes @("mail", "department")

    # Filter by department
    Get-JIMMetaverseObject -AttributeName "department" -AttributeValue "Engineering"

    # Get all objects (auto-paginate)
    Get-JIMMetaverseObject -ObjectTypeId 1 -All
    ```

### Response

Returns `200 OK` with a paginated list. Each item includes `displayName` (always) plus any requested attributes in a `attributes` dictionary.

```json
{
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "created": "2026-01-15T10:00:00Z",
      "displayName": "Jane Smith",
      "status": "Normal",
      "typeId": 1,
      "typeName": "person",
      "attributes": {
        "mail": "jane.smith@example.com",
        "department": "Engineering"
      }
    }
  ],
  "totalCount": 12450,
  "page": 1,
  "pageSize": 25,
  "totalPages": 498,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

---

## Retrieve an Object

Returns the full details of a metaverse object, including all attribute values and linked connector space objects.

```
GET /api/v1/metaverse/objects/{id}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the metaverse object |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/objects/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

### Response

Returns `200 OK` with the full object including all attribute values and connected system object references (see [The Object](#the-object-detail) above).

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `404` | `NOT_FOUND` | Object does not exist |
