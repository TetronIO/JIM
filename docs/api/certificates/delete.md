---
title: Delete a Certificate
---

# Delete a Certificate

Permanently removes a certificate from JIM. For certificates with `FilePath` source type, only the reference is removed; the file itself is not deleted from the volume mount.

```
DELETE /api/v1/certificates/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | Unique identifier of the certificate |

## Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Delete by ID
    Remove-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Force

    # Pipeline: remove all expired certificates
    Get-JIMCertificate | Where-Object { $_.IsExpired } | Remove-JIMCertificate -Force
    ```

## Response

Returns `204 No Content` on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Certificate does not exist |
