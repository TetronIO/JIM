---
title: Retrieve a Certificate
---

# Retrieve a Certificate

Returns the full details of a certificate by its ID.

```
GET /api/v1/certificates/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | Unique identifier of the certificate |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

## Response

Returns `200 OK` with the full [certificate object](index.md#the-certificate-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Certificate does not exist |
