---
title: Identity Providers
description: "The cmdlet that registers JIM with its identity provider, so administrators sign in with their own accounts, and that repairs the registration."
type: reference
audience: automation-author
---

# Identity Providers

Registering JIM with an identity provider by hand means following a guide click by click, and a missed step shows up only when nobody can sign in. `Register-JIMIdentityProvider` creates the registration the [SSO setup guide](../administration/sso-setup.md) describes, and returns the `JIM_SSO_*` values JIM needs. Run it again whenever you like: it changes nothing if the registration is right, corrects what has drifted, and with `-WhatIf` lists what it would change.

```jim-diagram
archetype: anatomy
title: What Register-JIMIdentityProvider hands back, value by value
desc: "Register-JIMIdentityProvider returns the values JIM's single sign-on needs, each named for its environment variable: Authority for JIM_SSO_AUTHORITY, ClientId for JIM_SSO_CLIENT_ID, ClientSecret for JIM_SSO_SECRET, PublicClientId for JIM_SSO_PUBLIC_CLIENT_ID, ApiScope for JIM_SSO_API_SCOPE, ClaimType for JIM_SSO_CLAIM_TYPE and MvAttribute for JIM_SSO_MV_ATTRIBUTE. ClientSecret is a secure string, and is empty unless the run created or replaced the secret. Changes lists what the run changed, and is empty when the registration was already right; with -WhatIf nothing is returned."
subject:
  kind: object
  label: "What `Register-JIMIdentityProvider` returns"
  parts:
    - { label: "`Authority`", detail: "`JIM_SSO_AUTHORITY`" }
    - { label: "`ClientId`", detail: "`JIM_SSO_CLIENT_ID`" }
    - { label: "`ClientSecret`", detail: "`JIM_SSO_SECRET`, only if created or replaced" }
    - { label: "`PublicClientId`", detail: "`JIM_SSO_PUBLIC_CLIENT_ID`" }
    - { label: "`ApiScope`", detail: "`JIM_SSO_API_SCOPE`" }
    - { label: "`ClaimType`", detail: "`JIM_SSO_CLAIM_TYPE`" }
    - { label: "`MvAttribute`", detail: "`JIM_SSO_MV_ATTRIBUTE`" }
properties:
  - { name: Each value, value: "for the variable beside it" }
  - { name: Changes, value: "empty when already right" }
```

It configures the identity provider, not JIM, so it does not need `Connect-JIM`. AD FS and Keycloak are supported.

---

## :material-shield-key-outline: Register-JIMIdentityProvider

Registers JIM with its identity provider, or repairs the registration.

For AD FS, it creates an Application Group with three applications:

| Application | AD FS type | What it is for |
|-------------|------------|----------------|
| *Name* - Portal | Server application | The portal signs people in with it, using a client secret. Its redirect URIs are JIM's `/signin-oidc` and `/signout-callback-oidc`. |
| *Name* - PowerShell | Native application | The PowerShell module signs people in with it, using PKCE and loopback redirect URIs for ports 8400 to 8409. |
| *Name* - API | Web API | JIM's API. Open to everyone, because what someone may do is decided by their roles in JIM. Its one claim rule issues the person's `objectGUID` as `oid`, with their name and email. Only the portal and the PowerShell module may ask for it. |

It uses the ADFS PowerShell module, which only an AD FS server has. Run it on the federation server, or from another Windows computer with `-Session` and a PowerShell session to the server.

For Keycloak, it creates two clients and a client scope in the realm `-Authority` names:

| What | Keycloak type | What it is for |
|------|---------------|----------------|
| `jim` (`-ClientId`) | Confidential client | The portal signs people in with it, using a client secret and PKCE. Its redirect URI is JIM's `/signin-oidc`, and its post-logout redirect URI is `/signout-callback-oidc`. Only the standard flow is on. |
| `jim-powershell` (`-PublicClientId`) | Public client | The PowerShell module signs people in with it, using PKCE and loopback redirect URIs for ports 8400 to 8409. It may also ask for `offline_access`, for a refresh token. |
| `jim-api` | Client scope | Puts the portal's client ID in an access token's audience, which JIM requires. It is an optional scope of JIM's two clients and of no other client, and never a realm default, so no other client can get a token JIM accepts. |

Both clients also get Keycloak's `basic`, `profile` and `email` scopes as default scopes, so tokens carry `sub`, the person's name and email. It uses Keycloak's admin REST API, so it runs from any computer that can reach Keycloak. Sign in with `-Credential` as a Keycloak administrator, or with `-ClientCredential` as a client's service account, for running it unattended. The account needs the `realm-admin` role in JIM's realm, or the master realm's `admin` role.

The client secret is returned only when the run created or replaced it, as a secure string, so it never appears on screen. AD FS shows a secret once: keep it then, or run again with `-ResetClientSecret` for a new one.

<!-- BEGIN GENERATED: Register-JIMIdentityProvider -->
### Syntax

```powershell
# ADFS (default)
Register-JIMIdentityProvider -Provider <string> -Url <string> [-Name <string>] [-Session <PSSession>] [-ResetClientSecret] [-WhatIf] [-Confirm]

# KeycloakServiceAccount
Register-JIMIdentityProvider -Provider <string> -Url <string> -Authority <string> -ClientCredential <PSCredential> [-AdminRealm <string>] [-ClientId <string>] [-PublicClientId <string>] [-ResetClientSecret] [-WhatIf] [-Confirm]

# Keycloak
Register-JIMIdentityProvider -Provider <string> -Url <string> -Authority <string> -Credential <PSCredential> [-AdminRealm <string>] [-ClientId <string>] [-PublicClientId <string>] [-ResetClientSecret] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Provider` | `string` | Yes |  | The identity provider to register JIM with. One of `ADFS` or `Keycloak`. |
| `Url` | `string` | Yes |  | JIM's address as people reach it, such as https://jim.example.com, with the port if it is not 443. The portal's sign-in and sign-out redirect URIs are made from it. For Keycloak, an http address is accepted for JIM on this computer only, such as http://localhost:5200. |
| `Name` | `string` | No (ADFS) | `'JIM Identity Management'` | The name of the AD FS Application Group, which is also its identifier. The applications in it are named after it, with " - Portal", " - PowerShell" and " - API". |
| `Session` | `PSSession` | No (ADFS) |  | A PowerShell session to the AD FS server, from New-PSSession. When omitted, the command runs on this computer, which must be an AD FS server. The session is left open. |
| `Authority` | `string` | Yes (KeycloakServiceAccount, Keycloak) |  | The Keycloak realm JIM signs people in from, such as https://sso.example.com/realms/jim. It is also the value for JIM_SSO_AUTHORITY. The realm must exist. |
| `Credential` | `PSCredential` | Yes (Keycloak) |  | The username and password of a Keycloak administrator, who signs in through Keycloak's admin-cli client in the realm `-AdminRealm` names. The account needs the realm-admin role in JIM's realm, or the master realm's admin role. |
| `ClientCredential` | `PSCredential` | Yes (KeycloakServiceAccount) |  | The client ID and secret of a Keycloak client whose service account administers JIM's realm, for running this unattended. The client is in the realm `-AdminRealm` names. |
| `AdminRealm` | `string` | No (KeycloakServiceAccount, Keycloak) | `'master'` | The Keycloak realm `-Credential` or `-ClientCredential` signs in to. |
| `ClientId` | `string` | No (KeycloakServiceAccount, Keycloak) | `'jim'` | The client ID of the portal's Keycloak client, the value for JIM_SSO_CLIENT_ID. |
| `PublicClientId` | `string` | No (KeycloakServiceAccount, Keycloak) | `'jim-powershell'` | The client ID of the PowerShell module's Keycloak client, the value for JIM_SSO_PUBLIC_CLIENT_ID. |
| `ResetClientSecret` | `switch` | No |  | Replaces the portal's client secret, and returns the new one. JIM cannot sign anyone in until JIM_SSO_SECRET is given the new secret. |

### Output

With `-WhatIf`, nothing is returned.

| Property | Type | Description |
|----------|------|-------------|
| `Provider` | `string` | The identity provider, ADFS or Keycloak. |
| `Authority` | `string` | The value for JIM_SSO_AUTHORITY. |
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

```powershell title="Register JIM in a Keycloak realm"
$registration = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential (Get-Credential)
$registration | Select-Object Authority, ClientId, PublicClientId, ApiScope
```

```powershell title="Check a Keycloak realm for drift, as a service account"
$serviceAccount = [pscredential]::new('jim-registrar', (Read-Host -AsSecureString 'Client secret'))
Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -ClientCredential $serviceAccount -AdminRealm 'jim' -WhatIf
```

```powershell title="Replace the portal's client secret"
$registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -ResetClientSecret
[System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard
```

JIM cannot sign anyone in after the secret is replaced until `JIM_SSO_SECRET` holds the new one.
