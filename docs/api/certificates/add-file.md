---
title: Add Certificate from File
---

# Add Certificate from File

Adds a new certificate by referencing a file in the connector-files volume mount. JIM reads the file, parses the certificate, and stores a reference to the file path.

```
POST /api/v1/certificates/file
```

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Certificate name (1-255 characters) |
| `filePath` | string | Yes | Path relative to the connector-files volume mount |
| `notes` | string | No | Optional notes (max 2000 characters) |

!!! note
    The file must be accessible within the connector-files Docker volume mount. Ensure the certificate file is placed in the mounted directory before adding it.

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/certificates/file \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Corporate Root CA",
        "filePath": "/certs/root-ca.pem",
        "notes": "Root CA loaded from connector-files mount"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Add-JIMCertificate -Name "Corporate Root CA" `
        -Path "/certs/root-ca.pem" `
        -Notes "Root CA loaded from connector-files mount"
    ```

## Response

Returns `201 Created` with the full [certificate object](index.md#the-certificate-object).

```json
{
  "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "name": "Corporate Root CA",
  "thumbprint": "A1B2C3D4E5F6...",
  "subject": "CN=Corporate Root CA, O=Contoso Ltd",
  "issuer": "CN=Corporate Root CA, O=Contoso Ltd",
  "serialNumber": "01AB23CD",
  "validFrom": "2024-01-01T00:00:00Z",
  "validTo": "2034-01-01T00:00:00Z",
  "sourceType": "FilePath",
  "filePath": "/certs/root-ca.pem",
  "isEnabled": true,
  "createdAt": "2026-04-05T10:00:00Z",
  "createdBy": "admin@contoso.com",
  "notes": "Root CA loaded from connector-files mount",
  "isExpired": false,
  "isExpiringSoon": false,
  "daysUntilExpiry": 2827
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid or missing fields (e.g. name too long, file not found, certificate could not be parsed) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
