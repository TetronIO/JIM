---
title: Attributes
---

# Attributes

Metaverse attributes are independent schema definitions, each with a data type and plurality (single or multi-valued). Attributes are mapped to object types to make them available on objects of that type. For example, the `displayName` attribute is mapped to both `person` and `group`, meaning objects of either type can hold a `displayName` value.

Attributes exist independently of object types. The `objectTypeIds` field on create and update controls which object types the attribute is mapped to.

### The Attribute Object

```json
{
  "id": 5,
  "name": "displayName",
  "created": "2026-01-10T09:00:00Z",
  "type": "Text",
  "attributePlurality": "SingleValued",
  "builtIn": true,
  "objectTypes": [
    { "id": 1, "name": "person" },
    { "id": 2, "name": "group" }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Attribute name |
| `created` | datetime | UTC creation timestamp |
| `type` | string | Data type (see below) |
| `attributePlurality` | string | `SingleValued` or `MultiValued` |
| `builtIn` | boolean | Built-in attributes cannot be deleted or have their type changed |
| `objectTypes` | array | Object types this attribute is mapped to |

### Data Types

| Type | Description |
|------|-------------|
| `Text` | String values |
| `Number` | Integer values |
| `LongNumber` | 64-bit integer values |
| `DateTime` | Date and time values |
| `Boolean` | True/false values |
| `Guid` | GUID/UUID values |
| `Reference` | Reference to another metaverse object |
| `Binary` | Binary data |

### Data Integrity Rules

JIM enforces the following rules to protect data integrity:

- **An attribute cannot be deleted** if it has values stored on any metaverse objects. Remove the values first.
- **An attribute cannot be deleted** if it is referenced by any sync rule mappings, scoping criteria, or object matching rules. Remove the references first.
- **An object type mapping cannot be removed** from an attribute if metaverse objects of that type have values stored for the attribute. Remove the values first.
- **Built-in attributes** cannot be deleted or have their type changed.

These rules ensure that schema changes never silently destroy data or break synchronisation configuration.

---

## List Attributes

Returns a paginated list of metaverse attributes.

```
GET /api/v1/metaverse/attributes
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `25` | Items per page |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/attributes \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMetaverseAttribute

    # By name
    Get-JIMMetaverseAttribute -Name "displayName"
    ```

---

## Retrieve an Attribute

```
GET /api/v1/metaverse/attributes/{id}
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/attributes/5 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMetaverseAttribute -Id 5
    ```

---

## Create an Attribute

Creates a new attribute and optionally maps it to one or more object types.

```
POST /api/v1/metaverse/attributes
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Attribute name (1-200 characters, must be unique) |
| `type` | string | Yes | Data type: `Text`, `Number`, `LongNumber`, `DateTime`, `Boolean`, `Guid`, `Reference`, `Binary` |
| `attributePlurality` | string | No | `SingleValued` (default) or `MultiValued` |
| `objectTypeIds` | array | No | Object type IDs to map this attribute to |

### Examples

=== "curl"

    ```bash
    # Create a text attribute mapped to person and group
    curl -X POST https://jim.example.com/api/v1/metaverse/attributes \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "costCentre",
        "type": "Text",
        "objectTypeIds": [1, 2]
      }'

    # Create an attribute with no object type mappings (add them later)
    curl -X POST https://jim.example.com/api/v1/metaverse/attributes \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "badgeNumber",
        "type": "Text"
      }'

    # Create a multi-valued reference attribute
    curl -X POST https://jim.example.com/api/v1/metaverse/attributes \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "directReports",
        "type": "Reference",
        "attributePlurality": "MultiValued",
        "objectTypeIds": [1]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create a text attribute mapped to person and group
    New-JIMMetaverseAttribute -Name "costCentre" -Type Text -ObjectTypeIds @(1, 2)

    # Create an attribute with no mappings
    New-JIMMetaverseAttribute -Name "badgeNumber" -Type Text

    # Create a multi-valued reference attribute
    New-JIMMetaverseAttribute -Name "directReports" `
        -Type Reference -AttributePlurality MultiValued `
        -ObjectTypeIds @(1)
    ```

### Response

Returns `201 Created` with the attribute object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name already exists, invalid data type) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |

---

## Update an Attribute

Updates an attribute's name, type, plurality, or object type mappings. Built-in attributes have restrictions on what can be changed.

```
PUT /api/v1/metaverse/attributes/{id}
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No | New name (1-200 characters) |
| `type` | string | No | New data type |
| `attributePlurality` | string | No | `SingleValued` or `MultiValued` |
| `objectTypeIds` | array | No | Replace all object type mappings (see below) |

### Examples

=== "curl"

    ```bash
    # Rename the attribute
    curl -X PUT https://jim.example.com/api/v1/metaverse/attributes/20 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "costCentreCode"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Rename the attribute
    Set-JIMMetaverseAttribute -Id 20 -Name "costCentreCode"
    ```

### Managing Object Type Mappings

The `objectTypeIds` field **replaces** all existing mappings with the provided set. To manage mappings:

- **Add a mapping**: include all existing type IDs plus the new one
- **Remove a mapping**: include all existing type IDs except the one to remove
- **Clear all mappings**: pass an empty array `[]`

!!! warning
    You cannot remove an object type mapping if metaverse objects of that type have values stored for this attribute. The API returns a `400 VALIDATION_ERROR` indicating which type cannot be removed and how many objects are affected. Remove the attribute values first (e.g. by removing the sync rule mapping that flows data into this attribute, then running a full sync).

=== "curl"

    ```bash
    # Map attribute to person and group
    curl -X PUT https://jim.example.com/api/v1/metaverse/attributes/20 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "objectTypeIds": [1, 2]
      }'

    # Add a third object type mapping (include all existing IDs)
    curl -X PUT https://jim.example.com/api/v1/metaverse/attributes/20 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "objectTypeIds": [1, 2, 3]
      }'

    # Remove the group mapping (only if no group objects have values)
    curl -X PUT https://jim.example.com/api/v1/metaverse/attributes/20 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "objectTypeIds": [1, 3]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Map attribute to person and group
    Set-JIMMetaverseAttribute -Id 20 -ObjectTypeIds @(1, 2)

    # Add a third object type mapping
    Set-JIMMetaverseAttribute -Id 20 -ObjectTypeIds @(1, 2, 3)

    # Remove the group mapping (only if no group objects have values)
    Set-JIMMetaverseAttribute -Id 20 -ObjectTypeIds @(1, 3)
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid change: built-in attribute modification, or removing an object type mapping when objects of that type have values stored for this attribute |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Attribute does not exist |

---

## Delete an Attribute

Permanently deletes an attribute. An attribute cannot be deleted if:

- It is a built-in attribute
- It has values stored on any metaverse objects
- It is referenced by any sync rule mappings, scoping criteria, or object matching rules

To delete an attribute that is in use, first remove all references to it from sync rule configuration, then ensure no metaverse objects have values for it (e.g. by running a full sync after removing the sync rule mappings).

```
DELETE /api/v1/metaverse/attributes/{id}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/metaverse/attributes/20 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMMetaverseAttribute -Id 20 -Force
    ```

### Response

Returns `204 No Content` on success.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Cannot delete: attribute is built-in, has stored values on metaverse objects (error includes affected object count), or is referenced by sync rule configuration (error lists the referencing sync rules) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Attribute does not exist |
