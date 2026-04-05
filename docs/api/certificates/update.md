---
title: Update a Certificate
---

# Update a Certificate

Updates a certificate's name, notes, or enabled status. All fields are optional; only include the fields you want to change.

```
PATCH /api/v1/certificates/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | Unique identifier of the certificate |

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No | New name (1-255 characters) |
| `notes` | string | No | New notes (max 2000 characters) |
| `isEnabled` | boolean | No | Enable or disable the certificate |

## Examples

=== "curl"

    ```bash
    # Rename a certificate
    curl -X PATCH https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Corporate Root CA (Production)"
      }'

    # Disable a certificate
    curl -X PATCH https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "isEnabled": false
      }'

    # Update notes
    curl -X PATCH https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "notes": "Renewed certificate, valid until 2035"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Rename a certificate
    Set-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -Name "Corporate Root CA (Production)"

    # Disable a certificate
    Set-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -IsEnabled $false

    # Update notes
    Set-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -Notes "Renewed certificate, valid until 2035"
    ```

## Response

Returns `204 No Content` on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid field values (e.g. name too long, notes exceed limit) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Certificate does not exist |
