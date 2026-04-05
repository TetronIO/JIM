---
title: Download a Certificate
---

# Download a Certificate

Downloads the certificate in DER format. Only available for certificates with `Uploaded` source type.

```
GET /api/v1/certificates/{id}/download
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | Unique identifier of the certificate |

The response Content-Type is `application/x-x509-ca-cert`.

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/certificates/a1b2c3d4-e5f6-7890-abcd-ef1234567890/download \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -o cert.der
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Save to a file
    Export-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Path "./cert.der"

    # Get raw bytes
    $bytes = Export-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -PassThru
    ```

## Response

Returns `200 OK` with the certificate file as binary content.

!!! note
    This endpoint is only available for certificates with `Uploaded` source type. Certificates added via file path should be accessed directly from the connector-files volume mount.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Certificate does not exist |
