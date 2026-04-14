---
title: Security
---

# Security

The Security API provides access to role definitions and role membership management. Roles determine what actions users and API keys can perform within JIM.

## The Role Object

```json
{
  "id": 1,
  "name": "Administrator",
  "builtIn": true,
  "created": "2026-01-10T09:00:00Z",
  "staticMemberCount": 3
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Role name |
| `builtIn` | boolean | Whether this is a built-in role (cannot be deleted) |
| `created` | datetime | UTC creation timestamp |
| `staticMemberCount` | integer | Number of metaverse objects assigned to this role |

## The Role Member Object

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "displayName": "Alice Smith",
  "typeId": 1,
  "typeName": "Person"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | GUID | Unique identifier of the metaverse object |
| `displayName` | string | Display name of the metaverse object |
| `typeId` | integer | Object type ID |
| `typeName` | string | Object type name |

---

## List Roles

Returns all security roles defined in JIM.

```
GET /api/v1/security/roles
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/security/roles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMRole
    ```

### Response

Returns `200 OK` with an array of role objects.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |

---

## Get Role

Returns a single role by its unique identifier.

```
GET /api/v1/security/roles/{roleId}
```

### Parameters

| Name | In | Type | Required | Description |
|------|-----|------|----------|-------------|
| `roleId` | path | integer | Yes | The unique identifier of the role |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/security/roles/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Get-JIMRole -Id 1
    ```

### Response

Returns `200 OK` with a role object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Role not found |

---

## Role Members

### List Role Members

Returns all metaverse objects assigned to a role.

```
GET /api/v1/security/roles/{roleId}/members
```

#### Parameters

| Name | In | Type | Required | Description |
|------|-----|------|----------|-------------|
| `roleId` | path | integer | Yes | The unique identifier of the role |

#### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/security/roles/1/members \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    # By role ID
    Get-JIMRoleMember -RoleId 1

    # Pipeline from Get-JIMRole
    Get-JIMRole -Name "Administrator" | Get-JIMRoleMember
    ```

#### Response

Returns `200 OK` with an array of role member objects.

#### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Role not found |

---

### Add Role Member

Assigns a metaverse object as a static member of the specified role.

```
PUT /api/v1/security/roles/{roleId}/members/{metaverseObjectId}
```

#### Parameters

| Name | In | Type | Required | Description |
|------|-----|------|----------|-------------|
| `roleId` | path | integer | Yes | The unique identifier of the role |
| `metaverseObjectId` | path | GUID | Yes | The unique identifier of the metaverse object |

#### Examples

=== "curl"

    ```bash
    curl -X PUT \
      https://jim.example.com/api/v1/security/roles/1/members/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    # By IDs
    Add-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

    # Pipeline from Get-JIMMetaverseObject
    Get-JIMMetaverseObject -Id "a1b2c3d4-..." | Add-JIMRoleMember -RoleId 1
    ```

#### Response

Returns `204 No Content` on success.

#### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Metaverse object not found |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Role not found |
| `409` | `CONFLICT` | Metaverse object is already a member of this role |

---

### Remove Role Member

Removes a metaverse object from the specified role.

```
DELETE /api/v1/security/roles/{roleId}/members/{metaverseObjectId}
```

!!! warning "Safety Checks"
    Two safety checks prevent administrator lockout:

    - You cannot remove **yourself** from the Administrator role
    - You cannot remove the **last member** of the Administrator role

    Both return `400 VALIDATION_ERROR` with a descriptive message.

#### Parameters

| Name | In | Type | Required | Description |
|------|-----|------|----------|-------------|
| `roleId` | path | integer | Yes | The unique identifier of the role |
| `metaverseObjectId` | path | GUID | Yes | The unique identifier of the metaverse object |

#### Examples

=== "curl"

    ```bash
    curl -X DELETE \
      https://jim.example.com/api/v1/security/roles/1/members/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    # By IDs
    Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force

    # With confirmation prompt
    Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..."
    ```

#### Response

Returns `204 No Content` on success.

#### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Self-removal from Administrator role, last Administrator removal, or object not in role |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Role not found |
