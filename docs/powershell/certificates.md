---
title: Certificates
---

# Certificates

Cmdlets for managing certificates in JIM's trusted certificate store. Certificates are used for secure authentication with connected systems (e.g. LDAP over TLS).

---

## Get-JIMCertificate

Retrieves certificates from the trusted store.

### Syntax

```powershell
# List (default)
Get-JIMCertificate [-Name <string>] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMCertificate -Id <guid>

# Enabled
Get-JIMCertificate -EnabledOnly [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) | | Certificate identifier. Accepts pipeline input. |
| `Name` | `string` | No (List, Enabled) | | Filter certificates by name. Supports wildcards (e.g., `"Contoso*"`). |
| `EnabledOnly` | `switch` | Yes (Enabled) | `$false` | Returns only certificates that are currently enabled |
| `Page` | `int` | No | `1` | Page number for paged results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 1,000) |

### Output

Certificate objects with properties such as `Id`, `Name`, `Notes`, `Enabled`, `Thumbprint`, `Subject`, `Issuer`, `NotBefore`, `NotAfter`, and `CreatedAt`.

### Examples

```powershell title="List all certificates"
Get-JIMCertificate
```

```powershell title="Filter by name"
Get-JIMCertificate -Name "Contoso*"
```

```powershell title="Get a specific certificate by ID"
Get-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="List only enabled certificates"
Get-JIMCertificate -EnabledOnly
```

```powershell title="Page through certificates"
Get-JIMCertificate -Page 2 -PageSize 50
```

---

## Add-JIMCertificate

Adds a certificate to the trusted store.

### Syntax

```powershell
# FromFile (default)
Add-JIMCertificate [-Name] <string> -Path <string> [-Notes <string>] [-PassThru]

# FromData
Add-JIMCertificate [-Name] <string> -CertificateData <byte[]> [-Notes <string>] [-PassThru]

# FromBase64
Add-JIMCertificate [-Name] <string> -CertificateBase64 <string> [-Notes <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the certificate |
| `Path` | `string` | Yes (FromFile) | | File path to a certificate in PEM or DER format |
| `CertificateData` | `byte[]` | Yes (FromData) | | Raw certificate bytes |
| `CertificateBase64` | `string` | Yes (FromBase64) | | Base64-encoded certificate data |
| `Notes` | `string` | No | | Optional notes or description |
| `PassThru` | `switch` | No | `$false` | Returns the created certificate object |

### Output

When `-PassThru` is specified, returns the newly created certificate object. Otherwise, no output.

### Examples

```powershell title="Add a certificate from a PEM file"
Add-JIMCertificate "LDAP Root CA" -Path ./certs/root-ca.pem
```

```powershell title="Add a certificate from a DER file with notes"
Add-JIMCertificate "Intermediate CA" -Path ./certs/intermediate.der -Notes "Expires 2027-06-15"
```

```powershell title="Add from Base64 and capture the result"
$cert = Add-JIMCertificate "HR System CA" -CertificateBase64 $base64String -PassThru
```

```powershell title="Add from raw byte data"
$bytes = [System.IO.File]::ReadAllBytes("./certs/root-ca.der")
Add-JIMCertificate "Root CA" -CertificateData $bytes -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before adding.
- The certificate is enabled by default after creation. Use `Set-JIMCertificate` to disable it.

---

## Set-JIMCertificate

Updates a certificate's editable properties (name, notes, enabled status). Certificate data cannot be changed; to replace a certificate, remove the existing one and add a new one.

### Syntax

```powershell
Set-JIMCertificate -Id <guid> [-Name <string>] [-Notes <string>]
    [-Enable] [-Disable] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Certificate identifier. Accepts pipeline input. |
| `Name` | `string` | No | | New display name |
| `Notes` | `string` | No | | New notes or description |
| `Enable` | `switch` | No | `$false` | Enables the certificate for use |
| `Disable` | `switch` | No | `$false` | Disables the certificate without removing it |
| `PassThru` | `switch` | No | `$false` | Returns the updated certificate object |

### Output

When `-PassThru` is specified, returns the updated certificate object. Otherwise, no output.

### Examples

```powershell title="Rename a certificate"
Set-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Name "LDAP Root CA (Production)"
```

```powershell title="Disable a certificate"
Set-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Disable
```

```powershell title="Enable a certificate via pipeline"
Get-JIMCertificate -Id "a1b2c3d4-..." | Set-JIMCertificate -Enable -PassThru
```

```powershell title="Update notes"
Set-JIMCertificate -Id "a1b2c3d4-..." -Notes "Renewed 2026-03-01; expires 2028-03-01"
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before changes.
- `-Enable` and `-Disable` are mutually exclusive; specifying both will produce an error.

---

## Remove-JIMCertificate

Permanently deletes a certificate from the trusted store.

### Syntax

```powershell
# ById (default)
Remove-JIMCertificate -Id <guid> [-Force] [-PassThru]

# ByInputObject
Remove-JIMCertificate -InputObject <PSCustomObject> [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) | | Certificate identifier. Accepts pipeline input. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Certificate object from the pipeline |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted certificate object |

### Output

When `-PassThru` is specified, returns the deleted certificate object. Otherwise, no output.

### Examples

```powershell title="Delete a certificate with confirmation"
Remove-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Delete without confirmation"
Remove-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Force
```

```powershell title="Pipeline deletion"
Get-JIMCertificate -Id "a1b2c3d4-..." | Remove-JIMCertificate -Force
```

```powershell title="Remove all disabled certificates"
Get-JIMCertificate | Where-Object { -not $_.Enabled } | Remove-JIMCertificate -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removing a certificate that is in use by a connected system may cause authentication failures. Verify usage before deleting.

---

## Test-JIMCertificate

Validates a certificate, checking expiry, chain trust, and other properties.

### Syntax

```powershell
Test-JIMCertificate -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Certificate identifier. Accepts pipeline input. |

### Output

A validation result object with properties:

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | Whether the certificate passed all validation checks |
| `Warnings` | `string[]` | Non-fatal issues (e.g. certificate expiring soon) |
| `Errors` | `string[]` | Fatal issues (e.g. certificate expired, untrusted chain) |

### Examples

```powershell title="Validate a single certificate"
Test-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Validate all certificates via pipeline"
Get-JIMCertificate | ForEach-Object {
    $result = Test-JIMCertificate -Id $_.Id
    [PSCustomObject]@{
        Name    = $_.Name
        IsValid = $result.IsValid
        Errors  = $result.Errors -join "; "
    }
}
```

```powershell title="Find all invalid certificates"
Get-JIMCertificate | ForEach-Object {
    $result = $_ | Test-JIMCertificate
    if (-not $result.IsValid) {
        [PSCustomObject]@{
            Name   = $_.Name
            Id     = $_.Id
            Errors = $result.Errors -join "; "
        }
    }
} | Format-Table -AutoSize
```

---

## Export-JIMCertificate

Downloads certificate data in DER format, either to a file or as a byte array.

### Syntax

```powershell
# ToFile (default)
Export-JIMCertificate -Id <guid> -Path <string> [-Force]

# PassThru
Export-JIMCertificate -Id <guid> -PassThru
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Certificate identifier. Accepts pipeline input. |
| `Path` | `string` | Yes (ToFile) | | Destination file path for the exported certificate |
| `Force` | `switch` | No (ToFile only) | `$false` | Overwrites the file if it already exists |
| `PassThru` | `switch` | Yes (PassThru) | `$false` | Returns the certificate as a byte array instead of writing to a file |

### Output

- **ToFile**: Writes the certificate to disk in DER format. No console output.
- **PassThru**: Returns the certificate data as a `byte[]`.

### Examples

```powershell title="Export a certificate to a file"
Export-JIMCertificate -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Path ./certs/root-ca.der
```

```powershell title="Export, overwriting an existing file"
Export-JIMCertificate -Id "a1b2c3d4-..." -Path ./certs/root-ca.der -Force
```

```powershell title="Export as byte array for further processing"
$bytes = Export-JIMCertificate -Id "a1b2c3d4-..." -PassThru
[System.IO.File]::WriteAllBytes("./certs/root-ca.pem", $bytes)
```

```powershell title="Pipeline: export all enabled certificates"
Get-JIMCertificate -EnabledOnly | ForEach-Object {
    Export-JIMCertificate -Id $_.Id -Path "./certs/$($_.Name).der" -Force
}
```

---

## See also

- [API: Certificates](../api/certificates/index.md): REST API reference for certificate endpoints
- [Connected Systems](connected-systems.md): configure connected systems that use trusted certificates
