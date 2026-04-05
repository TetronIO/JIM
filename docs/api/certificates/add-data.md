---
title: Add Certificate from Data
---

# Add Certificate from Data

Adds a new certificate from Base64-encoded certificate data. The certificate is parsed, validated, and stored in the JIM database.

```
POST /api/v1/certificates/upload
```

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Certificate name (1-255 characters) |
| `certificateDataBase64` | string | Yes | Base64-encoded certificate data (PEM or DER format, max 1 MB) |
| `notes` | string | No | Optional notes (max 2000 characters) |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/certificates/upload \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Corporate Root CA",
        "certificateDataBase64": "MIIDXTCCAkWgAwIBAgIJAJC1...",
        "notes": "Root CA for LDAP connector authentication"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # From a Base64 string
    Add-JIMCertificate -Name "Corporate Root CA" `
        -CertificateBase64 "MIIDXTCCAkWgAwIBAgIJAJC1..." `
        -Notes "Root CA for LDAP connector authentication"

    # From a local certificate file (reads and encodes automatically)
    $bytes = [System.IO.File]::ReadAllBytes("./certs/root-ca.cer")
    Add-JIMCertificate -Name "Corporate Root CA" -CertificateData $bytes
    ```

## Response

Returns `201 Created` with the full [certificate object](index.md#the-certificate-object).

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Corporate Root CA",
  "thumbprint": "A1B2C3D4E5F6...",
  "subject": "CN=Corporate Root CA, O=Contoso Ltd",
  "issuer": "CN=Corporate Root CA, O=Contoso Ltd",
  "serialNumber": "01AB23CD",
  "validFrom": "2024-01-01T00:00:00Z",
  "validTo": "2034-01-01T00:00:00Z",
  "sourceType": "Uploaded",
  "filePath": null,
  "isEnabled": true,
  "createdAt": "2026-04-05T10:00:00Z",
  "createdBy": "admin@contoso.com",
  "notes": "Root CA for LDAP connector authentication",
  "isExpired": false,
  "isExpiringSoon": false,
  "daysUntilExpiry": 2827
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid or missing fields (e.g. name too long, invalid Base64 data, certificate could not be parsed) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
