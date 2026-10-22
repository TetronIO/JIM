---
title: Identity Providers
description: "The cmdlet that registers JIM with its identity provider, so administrators sign in with their own accounts, and that repairs the registration."
type: reference
audience: automation-author
---

# Identity Providers

Registering JIM with an identity provider by hand means following a guide click by click, and a missed step shows up only when nobody can sign in. `Register-JIMIdentityProvider` creates the registration the [SSO setup guide](../administration/sso-setup.md) describes, and returns the `JIM_SSO_*` values JIM needs. Run it again whenever you like: it changes nothing if the registration is right, corrects what has drifted, and with `-WhatIf` lists what it would change.

It configures the identity provider, not JIM, so it does not need `Connect-JIM`. AD FS is supported today.

---

## Register-JIMIdentityProvider

Registers JIM with its identity provider, or repairs the registration.

For AD FS, it creates an Application Group with three applications:

| Application | AD FS type | What it is for |
|-------------|------------|----------------|
| *Name* - Portal | Server application | The portal signs people in with it, using a client secret. Its redirect URIs are JIM's `/signin-oidc` and `/signout-callback-oidc`. |
| *Name* - PowerShell | Native application | The PowerShell module signs people in with it, using PKCE and loopback redirect URIs for ports 8400 to 8409. |
| *Name* - API | Web API | JIM's API. Open to everyone, because what someone may do is decided by their roles in JIM. Its one claim rule issues the person's `objectGUID` as `oid`, with their name and email. Only the portal and the PowerShell module may ask for it. |

It uses the ADFS PowerShell module, which only an AD FS server has. Run it on the federation server, or from another Windows computer with `-Session` and a PowerShell session to the server.

The client secret is returned only when the run created or replaced it, as a secure string, so it never appears on screen. AD FS shows a secret once: keep it then, or run again with `-ResetClientSecret` for a new one.

<!-- BEGIN GENERATED: Register-JIMIdentityProvider -->
### Syntax

```powershell
Register-JIMIdentityProvider -Provider <string> -Url <string> [-Name <string>] [-Session <PSSession>] [-ResetClientSecret] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Provider` | `string` | Yes |  | The identity provider to register JIM with. One of `ADFS`. |
| `Url` | `string` | Yes |  | JIM's address as people reach it, such as https://jim.example.com, with the port if it is not 443. The portal's sign-in and sign-out redirect URIs are made from it. |
| `Name` | `string` | No | `'JIM Identity Management'` | The name of the AD FS Application Group, which is also its identifier. The applications in it are named after it, with " - Portal", " - PowerShell" and " - API". |
| `Session` | `PSSession` | No |  | A PowerShell session to the AD FS server, from New-PSSession. When omitted, the command runs on this computer, which must be an AD FS server. The session is left open. |
| `ResetClientSecret` | `switch` | No |  | Replaces the portal's client secret, and returns the new one. JIM cannot sign anyone in until JIM_SSO_SECRET is given the new secret. |

### Output

With `-WhatIf`, nothing is returned.

| Property | Type | Description |
|----------|------|-------------|
| `Provider` | `string` | The identity provider, ADFS. |
| `Authority` | `string` | The value for JIM_SSO_AUTHORITY. |
| `ValidIssuers` | `string[]` | The value for JIM_SSO_VALID_ISSUERS, joined with commas. AD FS issues access tokens under its federation service identifier, not the authority. |
| `ClientId` | `string` | The portal's client identifier, the value for JIM_SSO_CLIENT_ID. |
| `ClientSecret` | `securestring` | The portal's client secret, the value for JIM_SSO_SECRET. Empty unless this run created or replaced it. |
| `PublicClientId` | `string` | The PowerShell module's client identifier, the value for JIM_SSO_PUBLIC_CLIENT_ID. |
| `ApiScope` | `string` | The API's identifier, the value for JIM_SSO_API_SCOPE. |
| `ClaimType` | `string` | The claim JIM identifies people by, the value for JIM_SSO_CLAIM_TYPE. |
| `MvAttribute` | `string` | The Metaverse attribute that claim is matched with, the value for JIM_SSO_MV_ATTRIBUTE. |
| `Changes` | `string[]` | The changes this run made. Empty when the registration was already right. |
<!-- END GENERATED: Register-JIMIdentityProvider -->

### Examples

```powershell title="Register JIM, on the AD FS server"
$registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com'
$registration | Select-Object Authority, ClientId, PublicClientId, ApiScope
```

```powershell title="Check a remote AD FS server for drift, changing nothing"
$session = New-PSSession -ComputerName 'adfs1.example.com'
Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Session $session -WhatIf
Remove-PSSession -Session $session
```

```powershell title="Replace the portal's client secret"
$registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -ResetClientSecret
[System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard
```

JIM cannot sign anyone in after the secret is replaced until `JIM_SSO_SECRET` holds the new one.
