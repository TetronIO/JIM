---
title: Validate a Certificate
---

# Validate a Certificate

Checks the certificate for common issues such as expiry, chain problems, and other validation errors.

```
GET /api/v1/certificates/{id}/validate
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | Unique identifier of the certificate |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890/validate \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Test-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

## Response

Returns `200 OK` with the validation result.

```json
{
  "isValid": true,
  "errors": [],
  "warnings": ["Certificate expires in 45 days"]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `isValid` | boolean | Whether the certificate passed all checks |
| `errors` | array | Critical issues that prevent use |
| `warnings` | array | Non-critical issues (e.g. approaching expiry) |

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Certificate does not exist |
