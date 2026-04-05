---
title: Certificates
---

# Certificates

Certificates store trusted CA and intermediate certificates used by connectors for LDAP/HTTPS authentication. JIM supports adding certificates from Base64-encoded data or by referencing a file in the connector-files volume mount. Each certificate can be enabled or disabled independently.

## The Certificate Object

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
  "createdAt": "2026-02-15T10:00:00Z",
  "createdBy": "admin@contoso.com",
  "notes": "Root CA for LDAP connector authentication",
  "isExpired": false,
  "isExpiringSoon": false,
  "daysUntilExpiry": 2827
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `name` | string | Human-readable name |
| `thumbprint` | string | Certificate SHA-1 thumbprint |
| `subject` | string | Certificate subject |
| `issuer` | string | Certificate issuer |
| `serialNumber` | string | Certificate serial number |
| `validFrom` | datetime | Certificate validity start |
| `validTo` | datetime | Certificate validity end |
| `sourceType` | string | `Uploaded` (stored in database) or `FilePath` (referenced from connector-files mount) |
| `filePath` | string, nullable | File path (only for `FilePath` source type) |
| `isEnabled` | boolean | Whether the certificate is active |
| `createdAt` | datetime | UTC creation timestamp |
| `createdBy` | string, nullable | User who added the certificate |
| `notes` | string, nullable | Optional notes |
| `isExpired` | boolean | Whether the certificate has expired |
| `isExpiringSoon` | boolean | Whether the certificate expires within 30 days |
| `daysUntilExpiry` | integer | Days until expiry (negative if expired) |

## Source Types

| Type | Description |
|------|-------------|
| `Uploaded` | Certificate data stored in the JIM database. Added via Base64-encoded data. |
| `FilePath` | Certificate referenced by file path in the connector-files volume mount. The file must remain accessible. |

## Endpoints

| Endpoint | Description |
|----------|-------------|
| [List Certificates](list.md) | Paginated list of all certificates |
| [List Enabled Certificates](enabled.md) | All currently enabled certificates |
| [Retrieve a Certificate](retrieve.md) | Get full certificate details by ID |
| [Add from Data](add-data.md) | Add a certificate from Base64-encoded data |
| [Add from File](add-file.md) | Add a certificate from the connector-files mount |
| [Update a Certificate](update.md) | Update name, notes, or enabled status |
| [Delete a Certificate](delete.md) | Permanently remove a certificate |
| [Validate a Certificate](validate.md) | Check certificate validity and chain |
| [Download a Certificate](download.md) | Download certificate in DER format |
