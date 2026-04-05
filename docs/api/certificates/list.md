---
title: List Certificates
---

# List Certificates

Returns a paginated list of all certificates. Results include certificate metadata, validity status, and source type information.

```
GET /api/v1/certificates
```

## Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `25`    | Items per page (1-100) |
| `sortBy`        | string  | No       |         | Sort field: `name`, `validTo`, `createdAt`, `sourceType` |
| `sortDirection` | string  | No       | `asc`   | Sort order: `asc` or `desc` |
| `filter`        | string  | No       |         | Search by name (case-insensitive partial match) |

## Examples

=== "curl"

    ```bash
    # List all certificates
    curl https://jim.example.com/api/v1/certificates \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Second page, sorted by expiry date
    curl "https://jim.example.com/api/v1/certificates?page=2&pageSize=10&sortBy=validTo" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by name
    curl "https://jim.example.com/api/v1/certificates?filter=root" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all certificates
    Get-JIMCertificate

    # Filter by name
    Get-JIMCertificate -Filter "root"
    ```

## Response

Returns `200 OK` with a paginated list of certificate objects.

```json
{
  "items": [
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
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 25,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid pagination parameters (e.g. page < 1, pageSize > 100) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
