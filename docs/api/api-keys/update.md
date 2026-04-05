---
title: Update an API Key
---

# Update an API Key

Updates an API key's name, description, roles, expiry, or enabled status. The key value itself cannot be changed.

```
PUT /api/v1/apikeys/{id}
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Human-readable name |
| `description` | string | No | Optional description |
| `roleIds` | array | Yes | Role IDs to assign (replaces existing roles) |
| `expiresAt` | datetime | No | Expiry date (null = never expires) |
| `isEnabled` | boolean | Yes | Whether the key is active |

### Examples

=== "curl"

    ```bash
    # Rename and add expiry
    curl -X PUT https://jim.example.com/api/v1/apikeys/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "CI/CD Pipeline (Production)",
        "description": "Updated for production use",
        "roleIds": [1],
        "expiresAt": "2027-01-01T00:00:00Z",
        "isEnabled": true
      }'

    # Disable a key
    curl -X PUT https://jim.example.com/api/v1/apikeys/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "CI/CD Pipeline",
        "roleIds": [1],
        "isEnabled": false
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Rename and add expiry
    Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -Name "CI/CD Pipeline (Production)" `
        -ExpiresAt (Get-Date "2027-01-01") `
        -PassThru

    # Disable a key
    Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Disable

    # Enable a key
    Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Enable

    # Disable all keys matching a pattern
    Get-JIMApiKey | Where-Object { $_.name -like "Test*" } | Set-JIMApiKey -Disable
    ```

### Response

Returns `200 OK` with the updated API key object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name already exists, invalid role IDs) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | API key does not exist |
