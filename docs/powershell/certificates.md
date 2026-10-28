---
title: Certificates
description: "The cmdlets that manage JIM's trusted certificate store: Connectors' server certificates, and the identity provider's certificate for sign-in."
type: reference
audience: automation-author
---

# Certificates

Cmdlets for managing certificates in JIM's trusted certificate store. Each certificate is trusted for Connectors (the TLS connections JIM makes to Connected Systems, LDAP over TLS for instance), for sign-in (the connection to JIM's identity provider), or both. `Get-JIMIdentityProviderTrust` and `Add-JIMCertificate -FromIdentityProvider` check and trust the identity provider's certificate; the last two cmdlets read the certificate a Connected System's server presents, and approve it into the store.

```jim-diagram
archetype: decision
title: Which cmdlet puts a certificate in the trusted store
desc: "If you hold the certificate, as a file, a Base64 string or bytes, add it with Add-JIMCertificate and -Path, -CertificateBase64 or -CertificateData. If not, it depends on who presents it. For the identity provider's certificate, check it with Get-JIMIdentityProviderTrust, then add it with Add-JIMCertificate -FromIdentityProvider and the thumbprint you checked. For the certificate a Connected System's server presents, read it with Get-JIMConnectedSystemServerCertificate, which stores nothing, then trust it with Approve-JIMConnectedSystemServerCertificate and its thumbprint."
start: { label: A certificate to trust }
questions:
  - { id: hold, label: "Do you hold it?", sub: "as a file, Base64 or bytes" }
  - { id: who, label: "Who presents it?" }
outcomes:
  - { id: file, label: "`Add-JIMCertificate`", sub: "with `-Path`, `-CertificateBase64` or `-CertificateData`" }
  - { id: idp, label: "`Add-JIMCertificate`", sub: "with `-FromIdentityProvider` and the `-Thumbprint` you checked" }
  - { id: server, label: "Read it, then approve it", sub: "the server certificate cmdlets, by `-Thumbprint`" }
answers:
  - { from: hold, to: file, label: "yes" }
  - { from: hold, to: who, label: "no" }
  - { from: who, to: idp, label: the identity provider }
  - { from: who, to: server, label: "a Connected System's server" }
```

---

## :material-certificate-outline: Get-JIMCertificate

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

Listing returns each certificate's summary; `-Id` returns the certificate in full.

**By default, or with `-EnabledOnly`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` |  |
| `Name` | `string` |  |
| `Thumbprint` | `string` |  |
| `Subject` | `string` |  |
| `Issuer` | `string` |  |
| `ValidFrom` | `datetime` |  |
| `ValidTo` | `datetime` |  |
| `SourceType` | `string` | One of `Uploaded` or `FilePath`. |
| `IsEnabled` | `bool` |  |
| `IsExpired` | `bool` |  |
| `IsExpiringSoon` | `bool` |  |
| `DaysUntilExpiry` | `int` |  |
| `TrustedFor` | `string` | What the certificate is trusted for: Connectors, sign-in (the identity provider), or both. |
| `UsedBySignIn` | `bool` | Whether the certificate vouches for the identity provider now, as of JIM's last check of it. |
| `SignInDependsOnIt` | `bool` | Whether it is the only certificate that vouches for the identity provider, so disabling it, deleting it, or no longer trusting it for sign-in would stop everyone signing in. |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` |  |
| `Name` | `string` |  |
| `Thumbprint` | `string` |  |
| `Subject` | `string` |  |
| `Issuer` | `string` |  |
| `SerialNumber` | `string` |  |
| `ValidFrom` | `datetime` |  |
| `ValidTo` | `datetime` |  |
| `SourceType` | `string` | One of `Uploaded` or `FilePath`. |
| `FilePath` | `string` |  |
| `IsEnabled` | `bool` |  |
| `CreatedAt` | `datetime` |  |
| `CreatedBy` | `string` |  |
| `Notes` | `string` |  |
| `IsExpired` | `bool` |  |
| `IsExpiringSoon` | `bool` |  |
| `DaysUntilExpiry` | `int` |  |
| `TrustedFor` | `string` | What the certificate is trusted for: Connectors, sign-in (the identity provider), or both. |
| `UsedBySignIn` | `bool` | Whether the certificate vouches for the identity provider now, as of JIM's last check of it. |
| `SignInDependsOnIt` | `bool` | Whether it is the only certificate that vouches for the identity provider, so disabling it, deleting it, or no longer trusting it for sign-in would stop everyone signing in. |
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

## :material-plus-circle-outline: Add-JIMCertificate

Adds a certificate to the trusted store.

<!-- BEGIN GENERATED: Add-JIMCertificate -->
### Syntax

```powershell
# FromFile (default)
Add-JIMCertificate -Name <string> -Path <string> [-TrustedFor <string[]>] [-Notes <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# FromIdentityProvider
Add-JIMCertificate -FromIdentityProvider [-Name <string>] [-Thumbprint <string>] [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]

# FromBase64
Add-JIMCertificate -Name <string> -CertificateBase64 <string> [-TrustedFor <string[]>] [-Notes <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# FromData
Add-JIMCertificate -Name <string> -CertificateData <byte[]> [-TrustedFor <string[]>] [-Notes <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (FromFile, FromBase64, FromData); No (FromIdentityProvider) |  | The name the certificate is shown by in the store. Optional with `-FromIdentityProvider`, where the certificate's common name is used. |
| `Path` | `string` | Yes (FromFile) |  | Path to a certificate file (PEM or DER format). The path should be accessible from the JIM server (e.g., in the connector-files volume). |
| `CertificateData` | `byte[]` | Yes (FromData) |  | Raw certificate data as a byte array. |
| `CertificateBase64` | `string` | Yes (FromBase64) |  | Certificate data as a Base64-encoded string. |
| `FromIdentityProvider` | `switch` | Yes (FromIdentityProvider) |  | Trusts a certificate JIM's identity provider presents now, for sign-in. |
| `Thumbprint` | `string` | No (FromIdentityProvider) |  | With `-FromIdentityProvider`, the thumbprint of the certificate to trust, as `Get-JIMIdentityProviderTrust` shows it. Spaces and colons are ignored. Without it, the cmdlet suggests one and asks first. |
| `TrustedFor` | `string[]` | No (FromFile, FromBase64, FromData) |  | What the certificate is trusted for: Connectors, SignIn, or both. Connectors when not given. One of `Connectors` or `SignIn`. |
| `Notes` | `string` | No (FromFile, FromBase64, FromData) |  | Optional notes about the certificate. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's Configuration Change History. |
| `Force` | `switch` | No (FromIdentityProvider) |  | With `-FromIdentityProvider` and no `-Thumbprint`, trusts the suggested certificate without asking. |
| `PassThru` | `switch` | No |  | Returns the certificate added, or with `-FromIdentityProvider` the outcome. |

### Output

With `-PassThru`, the certificate added, or with `-FromIdentityProvider` the outcome, the certificate as it now sits in the store, and the identity provider's status afterwards; otherwise nothing.

**With `-Path` or `-CertificateData` or `-CertificateBase64`**: properties as under [Get-JIMCertificate](#get-jimcertificate).

**With `-FromIdentityProvider`**:

| Property | Type | Description |
|----------|------|-------------|
| `Outcome` | `string` | What happened. Only `Trusted` and `TrustedForSignInAsWell` change anything. One of `Trusted`, `TrustedForSignInAsWell`, `AlreadyTrusted`, `ThumbprintNotPresented`, `NameMismatch`, `InvalidChain` or `IdentityProviderUnavailable`. |
| `Message` | `string` | A sentence explaining the outcome. |
| `Certificate` | `object` | The certificate as it now sits in the JIM certificate store, where it is there. It has `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry`, `TrustedFor`, `UsedBySignIn` and `SignInDependsOnIt`. |
| `Status` | `object` | The identity provider's status after the request: `Trusted` once a certificate that vouches for it is trusted. It has `State`, `Host`, `TrustedBy`, `Message`, `CheckedUtc`, `PresentedChain`, `UnsentIssuer`, `SuggestedThumbprint`, `VouchingCertificates` and `TrustExpiresSoon`. |
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

```powershell title="Trust an internal root for sign-in and for Connectors"
Add-JIMCertificate -Name "Contoso Root CA" -Path ./certs/contoso-root.cer -TrustedFor SignIn, Connectors
```

```powershell title="Trust the identity provider's certificate authority before anyone can sign in"
Connect-JIM -Url https://jim.corp.contoso.com -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY
Add-JIMCertificate -FromIdentityProvider -Thumbprint '8F3A91D06B2E47C2C21D9F0E1A2B3C4D5E6F7A8B' -PassThru
```

```powershell title="Let JIM suggest the certificate, and confirm it"
Add-JIMCertificate -FromIdentityProvider
```

### Notes

- **`-FromIdentityProvider` trusts only what the identity provider presents now.** JIM connects to it again for the request, finds the certificate with the thumbprint you named in the chain it presents, and adds it trusted for sign-in. Sign-in works straight away; nothing needs restarting. A certificate that would not make the identity provider trusted (one that does not name its host, or a chain with an expired certificate in it) is refused, and nothing is stored.
- **Check the thumbprint against the one your certificate authority publishes** before you trust it, with or without `-Thumbprint`. Without it, the cmdlet shows the certificate JIM suggests (the highest one the identity provider presents, because a certificate authority outlasts the certificates it issues) and asks; `-Force` skips the question, and `-WhatIf` still only previews.
- Before anyone can sign in, connect with the infrastructure API key. Trusting from the identity provider needs the Administrator role, which that key has.
- Without `-TrustedFor`, a certificate is trusted for Connectors only. Trust a certificate for sign-in only when it is your identity provider's, or a certificate authority you rely on for it: whatever it vouches for can issue the tokens JIM signs people in with.
- The certificate is enabled by default after creation. Use `Set-JIMCertificate` to disable it.
- Run `Test-JIMCertificate` after adding a certificate to confirm it has not expired and its chain is trusted.

---

## :material-pencil-outline: Set-JIMCertificate

Updates a certificate's editable properties (name, notes, enabled status, what it is trusted for). Certificate data cannot be changed; to replace a certificate, remove the existing one and add a new one.

<!-- BEGIN GENERATED: Set-JIMCertificate -->
### Syntax

```powershell
Set-JIMCertificate -Id <guid> [-Name <string>] [-Notes <string>] [-Enable] [-Disable] [-TrustedFor <string[]>] [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the certificate to update. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | New name for the certificate. |
| `Notes` | `string` | No |  | New notes for the certificate. |
| `Enable` | `switch` | No |  | Enables the certificate for use. |
| `Disable` | `switch` | No |  | Disables the certificate without removing it. |
| `TrustedFor` | `string[]` | No |  | What the certificate is trusted for: Connectors, SignIn, or both. Replaces what it was trusted for. One of `Connectors` or `SignIn`. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's Configuration Change History. |
| `Force` | `switch` | No |  | Goes ahead without asking when the change stops everyone signing in. The warning is still written. |
| `PassThru` | `switch` | No |  | Returns the certificate as updated. |

### Output

With `-PassThru`, the certificate as updated; otherwise nothing.

Properties as under [Get-JIMCertificate](#get-jimcertificate).
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

```powershell title="Trust a certificate for sign-in as well as Connectors"
Set-JIMCertificate -Id "a1b2c3d4-..." -TrustedFor SignIn, Connectors
```

### Notes

- `-Enable` and `-Disable` are mutually exclusive; specifying both will produce an error.
- `-TrustedFor` replaces what the certificate is trusted for; name every purpose it should keep.
- **Disabling the only certificate sign-in depends on, or dropping `SignIn` from it, stops everyone signing in to JIM**, including you. The cmdlet warns and asks first; `-Force` goes ahead without asking. Until another certificate is trusted for sign-in, only the infrastructure API key can put it right.

---

## :material-delete-outline: Remove-JIMCertificate

Permanently deletes a certificate from the trusted store.

<!-- BEGIN GENERATED: Remove-JIMCertificate -->
### Syntax

```powershell
# ById (default)
Remove-JIMCertificate -Id <guid> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMCertificate -InputObject <PSCustomObject> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of the certificate to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | The certificate to remove, as `Get-JIMCertificate` returns it. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the certificate's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the certificate as it was before it was removed. |

### Output

With `-PassThru`, the certificate as it was before it was removed; otherwise nothing.

Properties as under [Get-JIMCertificate](#get-jimcertificate).
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

- Removing the only certificate sign-in depends on stops everyone signing in to JIM; the cmdlet warns before asking. `signInDependsOnIt` on `Get-JIMCertificate -Id` shows whether it is that certificate.
- Removing a certificate stops any Connected System whose server JIM trusts only through it from connecting securely. Its settings are kept, and it connects again once the certificate is back in the store. To keep a copy, export it first with `Export-JIMCertificate`; to stop trusting it without losing it, disable it with `Set-JIMCertificate -Disable` instead.

---

## :material-clipboard-check-outline: Test-JIMCertificate

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

| Property | Type |
|----------|------|
| `IsValid` | `bool` |
| `Errors` | `string[]` |
| `Warnings` | `string[]` |
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

## :material-export: Export-JIMCertificate

Downloads certificate data in DER format, either to a file or as a byte array.

A certificate added by file path is read from that file on the JIM server, just as connections read it, so the export matches what JIM trusts. If the file has gone or no longer holds a certificate, the export fails and says so.

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

With `-PassThru`, the certificate's bytes, DER-encoded; otherwise nothing, the certificate being written to `-Path`.

**With `-PassThru`**: a `byte[]`.
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
[System.IO.File]::WriteAllBytes("./certs/root-ca.der", $bytes)
```

```powershell title="Pipeline: export all enabled certificates"
Get-JIMCertificate -EnabledOnly | ForEach-Object {
    Export-JIMCertificate -Id $_.Id -Path "./certs/$($_.Name).der" -Force
}
```

---

## :material-shield-key-outline: Get-JIMIdentityProviderTrust

Shows whether JIM trusts its identity provider's TLS certificate, by what, and the chain the identity provider presents.

<!-- BEGIN GENERATED: Get-JIMIdentityProviderTrust -->
### Syntax

```powershell
Get-JIMIdentityProviderTrust
```

### Parameters

None.

### Output

The identity provider's trust status.

| Property | Type | Description |
|----------|------|-------------|
| `State` | `string` | Whether JIM trusts the identity provider's certificate. `Untrusted` is fixed by trusting its certificate authority for sign-in; `NameMismatch` (the certificate does not name the host) is not. One of `NotChecked`, `Trusted`, `Untrusted`, `NameMismatch`, `Unreachable` or `NotHttps`. |
| `Host` | `string` | The host JIM connects to for sign-in. |
| `TrustedBy` | `string` | What trusts the certificate: the operating system, or the JIM certificate store. Empty unless the state is `Trusted`. One of `TrustedByOperatingSystem`, `TrustedByJimCertificateStore`, `Untrusted`, `NameMismatch` or `NoCertificate`. |
| `Message` | `string` | A sentence explaining the state. |
| `CheckedUtc` | `datetime` | When JIM last checked. |
| `PresentedChain` | `object[]` | The chain as presented, the identity provider's own certificate first. Each has `Subject`, `CommonName`, `Issuer`, `Thumbprint`, `ValidFrom`, `ValidTo`, `IsServerCertificate`, `TrustedCertificateId` and `TrustedFor`. |
| `UnsentIssuer` | `string` | The certificate authority that issued the top of the presented chain, where the identity provider did not send it. That is usually the root, which outlasts every other certificate in the chain, so it is the best one to trust; fetch it from your certificate authority and add it with `POST /api/v1/certificates/upload`. |
| `SuggestedThumbprint` | `string` | The thumbprint of the highest certificate the identity provider presented: the one to name when trusting a certificate it presents. |
| `VouchingCertificates` | `object[]` | The certificates in the JIM certificate store that vouch for the identity provider now. Returned to administrators only; empty otherwise. Each has `Subject`, `CommonName`, `Issuer`, `Thumbprint`, `ValidFrom`, `ValidTo`, `IsServerCertificate`, `TrustedCertificateId` and `TrustedFor`. |
| `TrustExpiresSoon` | `bool` | Whether every certificate that vouches for the identity provider expires within 30 days. Returned to administrators only; empty otherwise. |
<!-- END GENERATED: Get-JIMIdentityProviderTrust -->

### Examples

```powershell title="Check the identity provider"
Get-JIMIdentityProviderTrust
```

```powershell title="List the certificates it presents"
(Get-JIMIdentityProviderTrust).presentedChain | Format-Table commonName, thumbprint, validTo
```

### Notes

- `state` is `Trusted`, `Untrusted`, `NameMismatch`, `Unreachable`, `NotHttps` or `NotChecked`. Only `Untrusted` is fixed by trusting a certificate: use `Add-JIMCertificate -FromIdentityProvider -Thumbprint` with `suggestedThumbprint`, or upload `unsentIssuer`'s certificate (usually the root) with `-TrustedFor SignIn`.
- The status is readable without signing in, because it matters most when nobody can. Anonymous callers see only what any TLS client of the identity provider sees; `vouchingCertificates`, `trustExpiresSoon` and what the store holds for each presented certificate come back to administrators only.
- JIM also checks the identity provider every 15 minutes in the background, and warns signed-in administrators in the portal when new sign-ins will fail.

---

## :material-server-network-outline: Get-JIMConnectedSystemServerCertificate

Reads the certificate the Connected System's server is presenting, without storing anything.

JIM connects to the endpoint the Connected System is configured for, purely to look at the certificate the server offers, and refuses the connection. The endpoint is always worked out by the Connected System's own connector from that system's settings; it is never named directly, so this cannot be used to make JIM connect to an address of your choosing.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemServerCertificate -->
### Syntax

```powershell
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId <int> [-SettingValues <hashtable>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System whose server is asked. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `SettingValues` | `hashtable` | No |  | Connectivity settings entered but not yet saved, keyed by setting ID (from `Get-JIMConnectorDefinition`). Give them when configuring a Connected System that cannot be saved yet: JIM does not save settings that fail validation, and an untrusted certificate fails it, so without them JIM reads the endpoint last saved. They are never saved, and encrypted settings are ignored. |

### Output

The certificate the server presented, the chain JIM built from it, which check it fails and what to do, and when it was read.

| Property | Type | Description |
|----------|------|-------------|
| `Certificate` | `object` | The certificate the server presented and which check it fails, judged as the connection will be: with the JIM certificate store taken into account where the connection uses it (`AcceptsJimTrustedCertificates`), and against the operating system alone where it does not. It has `Host`, `Port`, `FailureReason`, `Subject`, `Issuer`, `SubjectAlternativeNames`, `ValidFrom`, `ValidTo`, `Thumbprint`, `SignatureAlgorithm`, `IsSelfSigned`, `IssuerThumbprint`, `IsIssuerCertificateAvailable`, `Chain`, `IsChainComplete`, `MissingIssuer`, `RootThumbprint`, `RootSubject`, `Remediation`, `AcceptsJimTrustedCertificates`, `IsExpired` and `IsNotYetValid`. |
| `ReadAt` | `datetime` | When the server was asked, so a caller knows how current the answer is. |
<!-- END GENERATED: Get-JIMConnectedSystemServerCertificate -->

### Examples

```powershell title="Read the certificate the configured server presents"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
```

```powershell title="Show the identifying details and which check it fails"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 |
    Select-Object -ExpandProperty certificate |
    Select-Object host, subject, thumbprint, failureReason
```

```powershell title="List the certificate chain and where each certificate came from"
(Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42).certificate.chain |
    Format-Table subject, source, downloadedFrom
```

```powershell title="Read an endpoint that has been entered but not saved"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' }
```

### Notes

- **Why `-SettingValues` exists.** JIM does not save settings that fail validation, and a certificate JIM does not trust is a validation failure. A Connected System being configured for the first time therefore has the address you typed and nothing in the database, so without these JIM would look at the endpoint last saved, or report that the system is not configured for an encrypted connection. Setting identifiers come from `Get-JIMConnectorDefinition`. The values are never persisted, and values for encrypted settings are ignored.
- Reading stores nothing. Trusting the certificate is a separate call to `Approve-JIMConnectedSystemServerCertificate`.

---

## :material-check-decagram-outline: Approve-JIMConnectedSystemServerCertificate

Trusts the certificate the Connected System's server is presenting, adding it to the Trusted Certificates store.

JIM reads the certificate from the server again, checks it against the thumbprint you supply, and adds it through the audited path, so the addition carries an Activity naming who trusted it and why.

<!-- BEGIN GENERATED: Approve-JIMConnectedSystemServerCertificate -->
### Syntax

```powershell
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId <int> -Thumbprint <string> [-ChangeReason <string>] [-SettingValues <hashtable>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System whose server is asked. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `Thumbprint` | `string` | Yes |  | The thumbprint to trust, as read from the server. JIM reads the server again and trusts whichever certificate in the chain matches. Spaces and colons between the pairs are ignored. |
| `ChangeReason` | `string` | No |  | Why the certificate was trusted, recorded on the Activity and in the certificate's change history. JIM records a sentence naming the Connected System when none is given. |
| `SettingValues` | `hashtable` | No |  | Connectivity settings entered but not yet saved, keyed by setting ID, as for `Get-JIMConnectedSystemServerCertificate`. |
| `PassThru` | `switch` | No |  | Returns the outcome, with the certificate as it now sits in the certificate store. |

### Output

With `-PassThru`, the outcome; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Outcome` | `string` | What happened: Trusted when the certificate was added to the certificate store, AlreadyTrusted when it was there already, ThumbprintMismatch when the server presented a different certificate from the one named. The other values say why nothing could be trusted. One of `Trusted`, `AlreadyTrusted`, `ThumbprintMismatch`, `ConnectedSystemNotFound`, `NotConfiguredForSecureConnection`, `ServerUnreachable`, `InvalidChain` or `NotUsedByThisConnection`. |
| `Message` | `string` | A sentence explaining the outcome. |
| `Certificate` | `object` | The certificate as it now sits in the certificate store; empty unless the Outcome is Trusted. It has `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry`, `TrustedFor`, `UsedBySignIn` and `SignInDependsOnIt`. |
| `ExpectedThumbprint` | `string` | The thumbprint named in the request; set when the Outcome is ThumbprintMismatch. |
| `PresentedThumbprint` | `string` | The thumbprint of the certificate the server presents now; set when the Outcome is ThumbprintMismatch, so the two can be compared. |
| `StoredIntermediates` | `object[]` | The certificate authorities JIM stored with the certificate because it downloaded them rather than the server sending them; without them a connection could not link the two. Empty unless the Outcome is Trusted. Each has `Id`, `Name`, `Thumbprint`, `Subject`, `Issuer`, `SerialNumber`, `ValidFrom`, `ValidTo`, `SourceType`, `FilePath`, `IsEnabled`, `CreatedAt`, `CreatedBy`, `Notes`, `IsExpired`, `IsExpiringSoon`, `DaysUntilExpiry`, `TrustedFor`, `UsedBySignIn` and `SignInDependsOnIt`. |
<!-- END GENERATED: Approve-JIMConnectedSystemServerCertificate -->

### Examples

```powershell title="Trust the certificate you have checked"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01'
```

```powershell title="Trust the root of the chain, rather than the server's own certificate"
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate.chain | Format-Table subject, source, downloadedFrom

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint $reading.certificate.rootThumbprint `
    -ChangeReason 'Unblocking the HR Cloud connection test.'
```

```powershell title="Trust an endpoint that has been entered but not saved"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' `
    -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' } -PassThru
```

### Notes

- **Check the thumbprint against the server's administrator before running this.** It is the only thing standing between an unattended script and trusting whatever is presented.
- **Any certificate in the chain works; the root lasts longest.** JIM accepts the server when any certificate in its chain is in the Trusted Certificates store. `rootThumbprint` is populated when JIM reached the root, and trusting it survives the renewal of every certificate beneath it; trusting the server's own certificate has to be repeated at every renewal. Where `isChainComplete` is `$false`, trust the highest certificate JIM found, or add `missingIssuer` under Admin > Certificates. A self-signed certificate is its own root.
- **A changed certificate stops the action.** If the server is presenting anything other than the thumbprint you named, nothing is trusted and the outcome is `ThumbprintMismatch`, with both values returned so you can compare them. Expected after a renewal; worth investigating otherwise.
- Only an untrusted issuer is fixed by trusting a certificate. An expired certificate has to be renewed on the server, and a name mismatch means connecting by a name the certificate carries. A chain through a broken certificate authority is refused with `InvalidChain` and nothing is trusted. So is a connection that takes nothing from the certificate store, Oracle Database over TCPS, with `NotUsedByThisConnection`: its certificate must be one the operating system already trusts, and the read reports `acceptsJimTrustedCertificates` as `$false`.
- Supports `ShouldProcess`.
- Remove a certificate later with `Remove-JIMCertificate`.

---

## :material-book-open-variant-outline: See also

- [Certificates](../configuration/certificates.md): what certificates are, source types, expiry awareness, and rotation workflows
- [Connected Systems](connected-systems.md): configure Connected Systems that use trusted certificates
