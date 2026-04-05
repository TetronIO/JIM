---
title: List Enabled Certificates
---

# List Enabled Certificates

Returns all currently enabled certificates. This endpoint is not paginated; it returns the complete list of active certificates in a single response.

```
GET /api/v1/certificates/enabled
```

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/certificates/enabled \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMCertificate -EnabledOnly
    ```

## Response

Returns `200 OK` with an array of [certificate objects](index.md#the-certificate-object).

```json
[
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
    "createdAt": "2026-02-15T10:00:00Z",
    "createdBy": "admin@contoso.com",
    "notes": "Root CA for LDAP connector authentication",
    "isExpired": false,
    "isExpiringSoon": false,
    "daysUntilExpiry": 2827
  }
]
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
