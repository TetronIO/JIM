---
title: Certificates
---

# Certificates

Cmdlets for managing certificates in JIM's trusted certificate store. Certificates are used for secure authentication with Connected Systems (e.g. LDAP over TLS).

---

## Get-JIMCertificate

Retrieves certificates from the trusted store.

<!-- BEGIN GENERATED: Get-JIMCertificate -->
### Syntax

```powershell
# List (default)
Get-JIMCertificate [-Name <string>] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMCertificate -Id <guid>

# Enabled
Get-JIMCertificate [-Name <string>] [-EnabledOnly]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of a specific certificate to retrieve. Accepts pipeline input by property name. |
| `Name` | `string` | No (List, Enabled) |  | Filters certificates by name, such as "Contoso*". Supports wildcards. |
| `EnabledOnly` | `switch` | No (Enabled) |  | Returns only the certificates that are enabled. |
| `Page` | `int` | No (List) | `1` | The page of results to return. At least 1. |
| `PageSize` | `int` | No (List) | `100` | The number of certificates on each page. From 1 to 1,000. |

### Output

Listing returns each certificate's summary; -Id returns the certificate in full.

| Type | Returned by | Properties |
|------|-------------|------------|
| `TrustedCertificateHeader` | List, Enabled | `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `ValidFrom`, `ValidTo`, `SourceType`, `IsEnabled`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry` |
| `TrustedCertificateDetailDto` | ById | `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry` |
<!-- END GENERATED: Get-JIMCertificate -->

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

<!-- BEGIN GENERATED: Add-JIMCertificate -->
### Syntax

```powershell
# FromFile (default)
Add-JIMCertificate -Name <string> -Path <string> [-Notes <string>] [-ChangeReason <string>] [-PassThru]

# FromData
Add-JIMCertificate -Name <string> -CertificateData <byte[]> [-Notes <string>] [-ChangeReason <string>] [-PassThru]

# FromBase64
Add-JIMCertificate -Name <string> -CertificateBase64 <string> [-Notes <string>] [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name the certificate is shown by in the store. |
| `Path` | `string` | Yes (FromFile) |  | Path to a certificate file (PEM or DER format). The path should be accessible from the JIM server (e.g., in the connector-files volume). |
| `CertificateData` | `byte[]` | Yes (FromData) |  | Raw certificate data as a byte array. |
| `CertificateBase64` | `string` | Yes (FromBase64) |  | Certificate data as a Base64-encoded string. |
| `Notes` | `string` | No |  | Optional notes about the certificate. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's configuration change history. |
| `PassThru` | `switch` | No |  | Returns the certificate added. |

### Output

With -PassThru, the certificate added; otherwise nothing.

| Type | Properties |
|------|------------|
| `TrustedCertificateDetailDto` | `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry` |
<!-- END GENERATED: Add-JIMCertificate -->

### Examples

```powershell title="Add a certificate from a PEM file"
Add-JIMCertificate -Name "LDAP Root CA" -Path ./certs/root-ca.pem
```

```powershell title="Add a certificate from a DER file with notes"
Add-JIMCertificate -Name "Intermediate CA" -Path ./certs/intermediate.der -Notes "Expires 2027-06-15"
```

```powershell title="Add from Base64 and capture the result"
$cert = Add-JIMCertificate -Name "HR System CA" -CertificateBase64 $base64String -PassThru
```

```powershell title="Add from raw byte data"
$bytes = [System.IO.File]::ReadAllBytes("./certs/root-ca.der")
Add-JIMCertificate -Name "Root CA" -CertificateData $bytes -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before adding.
- The certificate is enabled by default after creation. Use `Set-JIMCertificate` to disable it.

---

## Set-JIMCertificate

Updates a certificate's editable properties (name, notes, enabled status). Certificate data cannot be changed; to replace a certificate, remove the existing one and add a new one.

<!-- BEGIN GENERATED: Set-JIMCertificate -->
### Syntax

```powershell
Set-JIMCertificate -Id <guid> [-Name <string>] [-Notes <string>] [-Enable] [-Disable] [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the certificate to update. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | New name for the certificate. |
| `Notes` | `string` | No |  | New notes for the certificate. |
| `Enable` | `switch` | No |  | Enables the certificate for use. |
| `Disable` | `switch` | No |  | Disables the certificate without removing it. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's configuration change history. |
| `PassThru` | `switch` | No |  | Returns the certificate as updated. |

### Output

With -PassThru, the certificate as updated; otherwise nothing.

| Type | Properties |
|------|------------|
| `TrustedCertificateDetailDto` | `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry` |
<!-- END GENERATED: Set-JIMCertificate -->

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

<!-- BEGIN GENERATED: Remove-JIMCertificate -->
### Syntax

```powershell
# ById (default)
Remove-JIMCertificate -Id <guid> [-ChangeReason <string>] [-Force] [-PassThru]

# ByInputObject
Remove-JIMCertificate -InputObject <PSCustomObject> [-ChangeReason <string>] [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of the certificate to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | The certificate to remove, as Get-JIMCertificate returns it. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's configuration change history. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the certificate as it was before it was removed. |

### Output

With -PassThru, the certificate as it was before it was removed; otherwise nothing.

| Type | Properties |
|------|------------|
| `TrustedCertificateDetailDto` | `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry` |
<!-- END GENERATED: Remove-JIMCertificate -->

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
Get-JIMCertificate | Where-Object { -not $_.IsEnabled } | Remove-JIMCertificate -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removing a certificate that is in use by a Connected System may cause authentication failures. Verify usage before deleting.

---

## Test-JIMCertificate

Validates a certificate, checking expiry, chain trust, and other properties.

<!-- BEGIN GENERATED: Test-JIMCertificate -->
### Syntax

```powershell
Test-JIMCertificate -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the certificate to validate. Accepts pipeline input by property name. |

### Output

The result of the checks. IsValid is true when the certificate passed every one; Warnings lists problems that do not stop it being used, such as expiring soon, and Errors those that do, such as having expired or an untrusted chain.

| Type | Properties |
|------|------------|
| `CertificateValidationResult` | `IsValid`, `Errors`, `Warnings` |
<!-- END GENERATED: Test-JIMCertificate -->

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

<!-- BEGIN GENERATED: Export-JIMCertificate -->
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
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the certificate to export. Accepts pipeline input by property name. |
| `Path` | `string` | Yes (ToFile) |  | The file path to save the certificate to. |
| `Force` | `switch` | No (ToFile) |  | Overwrites the file if it already exists. |
| `PassThru` | `switch` | Yes (PassThru) |  | Returns the certificate's bytes instead of writing them to a file. |

### Output

With -PassThru, the certificate's bytes, DER-encoded; otherwise nothing, the certificate being written to -Path.

| Type | Returned by | Properties |
|------|-------------|------------|
| `byte[]` | PassThru |  |
<!-- END GENERATED: Export-JIMCertificate -->

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

- [Certificates](../configuration/certificates.md): what certificates are, source types, expiry awareness, and rotation workflows
- [Connected Systems](connected-systems.md): configure Connected Systems that use trusted certificates
