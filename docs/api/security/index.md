---
title: Security
---

# Security

The Security API provides access to role definitions used for access control. Roles determine what actions users and API keys can perform within JIM.

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
