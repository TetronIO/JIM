---
title: Create an API Key
---

# Create an API Key

Creates a new API key. The full key is included in the response and is **never shown again**. Store it immediately in a secure location.

```
POST /api/v1/apikeys
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Human-readable name (must be unique) |
| `description` | string | No | Optional description of the key's purpose |
| `roleIds` | array | No | Role IDs to assign to the key |
| `expiresAt` | datetime | No | Expiry date (null = never expires) |

### Examples

=== "curl"

    ```bash
    # Create a key with roles and expiry
    curl -X POST https://jim.example.com/api/v1/apikeys \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "CI/CD Pipeline",
        "description": "Used by GitHub Actions",
        "roleIds": [1],
        "expiresAt": "2026-07-15T00:00:00Z"
      }'

    # Create a key that never expires
    curl -X POST https://jim.example.com/api/v1/apikeys \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Monitoring Service",
        "roleIds": [1]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create a key with roles and expiry
    New-JIMApiKey -Name "CI/CD Pipeline" `
        -Description "Used by GitHub Actions" `
        -RoleIds @(1) `
        -ExpiresAt (Get-Date "2026-07-15") `
        -PassThru

    # Create a key that never expires
    New-JIMApiKey -Name "Monitoring Service" -RoleIds @(1) -PassThru
    ```

### Response

Returns `201 Created` with the API key object **including the full key**:

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "CI/CD Pipeline",
  "description": "Used by GitHub Actions",
  "keyPrefix": "jim_ak_7",
  "key": "jim_ak_7f3a9b2c1d4e5f6a7b8c9d0e1f2a3b4c",
  "createdAt": "2026-04-05T10:00:00Z",
  "expiresAt": "2026-07-15T00:00:00Z",
  "lastUsedAt": null,
  "lastUsedFromIp": null,
  "isEnabled": true,
  "roles": [
    { "id": 1, "name": "Administrator", "builtIn": true }
  ]
}
```

!!! warning
    The `key` field is only present in the creation response. Copy it immediately; it cannot be retrieved later.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name already exists, invalid role IDs) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
