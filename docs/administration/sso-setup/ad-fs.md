---
title: "SSO Setup: AD FS"
description: Register JIM with AD FS so people sign in to the portal, and the PowerShell module, with their Active Directory accounts.
type: how-to
audience: implementer
---

# SSO Setup: AD FS

Use this guide to let people sign in to JIM with their Active Directory accounts through AD FS (Active Directory Federation Services), both in the portal and in the PowerShell module with `Connect-JIM`. One command registers JIM on AD FS, and you give JIM the values it returns. JIM identifies each person by their Active Directory `objectGUID`, so they sign in as the Metaverse Object that synchronisation from Active Directory created for them, and stay that person when they are renamed. This configuration is verified against AD FS on Windows Server 2025 before every JIM release.

```jim-diagram
archetype: map
title: How AD FS and synchronisation meet at the objectGUID
desc: "A person's Active Directory account carries an objectGUID, which reaches JIM by two routes. At sign-in, the claim rule of JIM's API application on AD FS issues it as the oid claim in their token. Beforehand, the import Synchronisation Rule for your Active Directory users maps it to the Metaverse Object's built-in Object GUID attribute. JIM matches the oid claim to Object GUID (JIM_SSO_CLAIM_TYPE=oid and JIM_SSO_MV_ATTRIBUTE=Object GUID), so the person signs in as the Metaverse Object synchronisation created for them."
nodes:
  - { id: ad, label: Active Directory account, row: 0, col: 0 }
  - { id: rule, label: AD FS claim rule, row: 0, col: 1 }
  - { id: oid, label: "`oid` claim in the token", row: 0, col: 2 }
  - { id: sync, label: Synchronisation Rule, row: 1, col: 0 }
  - { id: mvo, label: "Metaverse Object", row: 1, col: 2 }
edges:
  - { from: ad, to: rule, label: "`objectGUID` read by" }
  - { from: rule, to: oid, label: issues it as }
  - { from: ad, to: sync, label: imported by }
  - { from: sync, to: mvo, label: "maps it to `Object GUID` of" }
  - { from: oid, to: mvo, label: matched to }
```

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the [confidential and public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, [how JIM recognises people](../sso-setup.md#how-jim-recognises-people), and the [testing steps](../sso-setup.md#testing-your-configuration) you can run once configuration is complete.

## :material-clipboard-list-outline: Before you begin

You need:

- **AD FS on Windows Server 2019 or later.** Earlier versions do not support PKCE, which the PowerShell module signs in with.
- **JIM's address**, such as `https://jim.example.com`, with the port if JIM does not use 443.
- **PowerShell 7 with the JIM PowerShell module, on Windows,** run as an AD FS administrator: on the AD FS server, or on another Windows computer that can open a PowerShell session to it. See [Installation](../../powershell/index.md#installation).
- **JIM trusting AD FS's HTTPS certificate,** which usually comes from your own certificate authority. See [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md).
- **The people who will sign in, synchronised from Active Directory.** JIM signs someone in only when it has a Metaverse Object whose `Object GUID` holds their `objectGUID`. The import Synchronisation Rule for your Active Directory users needs a mapping from `objectGUID` to the built-in `Object GUID` attribute (see [Attribute Mappings](../../configuration/attribute-mappings.md)). Your first administrator is the exception: JIM creates them at their first sign-in.

## :material-numeric-1-circle-outline: Step 1: Register JIM with AD FS

`Register-JIMIdentityProvider` creates JIM's Application Group on AD FS and returns the values JIM needs. The group holds an application for the portal, one for the PowerShell module, and one for JIM's API, whose claim rule issues each person's `objectGUID` as the `oid` claim with their name and email. What each application contains is on the cmdlet's page: [Register-JIMIdentityProvider](../../powershell/identity-providers.md#register-jimidentityprovider).

1. On the AD FS server, open PowerShell 7 as an AD FS administrator, and run the command with JIM's address:

    ```powershell
    $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com'
    $registration | Select-Object Authority, ClientId, PublicClientId, ApiScope, ClaimType, MvAttribute
    ```

    To run it from another Windows computer, open a session to the AD FS server and pass it with `-Session`:

    ```powershell
    $session = New-PSSession -ComputerName 'adfs1.example.com'
    $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Session $session
    Remove-PSSession -Session $session
    ```

    Add `-WhatIf` first if you want to see each change the command would make; it makes none.

2. Copy the portal's client secret. AD FS shows it only once, so keep it safe until Step 3:

    ```powershell
    [System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard
    ```

    If the secret is lost, run the command again with `-ResetClientSecret` for a new one. The old secret stops working, so JIM signs nobody in until `JIM_SSO_SECRET` holds the new one.

## :material-numeric-2-circle-outline: Step 2: Find your first administrator's objectGUID

JIM gives the Administrator role to the person whose `objectGUID` is `JIM_SSO_INITIAL_ADMIN`, at their first sign-in, and creates their Metaverse Object if synchronisation has not. Read the `objectGUID` of the account they sign in to AD FS with, for example with the Active Directory PowerShell module on a domain controller:

```powershell
(Get-ADUser -Identity 'alice').ObjectGUID.Guid
```

The value is a GUID, such as `3f2504e0-4f89-11d3-9a0c-0305e82c3301`.

## :material-numeric-3-circle-outline: Step 3: Configure JIM

Set these environment variables to the values from Steps 1 and 2, then restart JIM (see [Configuration Reference](../configuration.md)):

```bash
JIM_SSO_AUTHORITY={Authority}
JIM_SSO_CLIENT_ID={ClientId}
JIM_SSO_SECRET={the client secret from Step 1}
JIM_SSO_API_SCOPE={ApiScope}
JIM_SSO_PUBLIC_CLIENT_ID={PublicClientId}

# Identify people by their Active Directory objectGUID ({ClaimType} and {MvAttribute})
JIM_SSO_CLAIM_TYPE=oid
JIM_SSO_MV_ATTRIBUTE=Object GUID
JIM_SSO_INITIAL_ADMIN={the objectGUID from Step 2}
```

**Example:**

```bash
JIM_SSO_AUTHORITY=https://adfs.example.com/adfs
JIM_SSO_CLIENT_ID=e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_SECRET=generatedSecretValue123
JIM_SSO_API_SCOPE=api://e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_PUBLIC_CLIENT_ID=a9876543-21fe-dcba-3210-fedcba987654
JIM_SSO_CLAIM_TYPE=oid
JIM_SSO_MV_ATTRIBUTE=Object GUID
JIM_SSO_INITIAL_ADMIN=3f2504e0-4f89-11d3-9a0c-0305e82c3301
```

`JIM_SSO_PUBLIC_CLIENT_ID` is what lets administrators sign in with `Connect-JIM`. Scripts that run unattended cannot sign in interactively, so they use a JIM API key instead; see [API Authentication](../../api/authentication.md).

## :material-check-decagram-outline: Check it worked

1. Open JIM in a browser as your first administrator. JIM sends you to AD FS to sign in, then returns you to JIM, signed in.
2. Open `/claims` and check that `oid`, `name` and `email` carry your values.
3. In PowerShell 7, run `Connect-JIM -Url 'https://jim.example.com'`, sign in in the browser it opens, then run `Get-JIMUserInfo`. It reports `IsAdministrator : True`, with the `Administrator` and `User` roles.
4. Sign in as someone synchronised from Active Directory who holds no JIM role. JIM signs them in with the `User` role only, and refuses them administration.

To check sign-out too, see [Testing Your Configuration](../sso-setup.md#testing-your-configuration).

## :material-api: Let the API reference sign in (optional)

The API reference at `https://jim.example.com/api/reference` can call JIM's API with an API key as it stands. Its **Try It** can also sign you in through AD FS, with the PowerShell module's application, whose redirect URIs `Register-JIMIdentityProvider` gives the page's address. The browser then asks AD FS for the token itself, and AD FS answers a browser on another site only when Cross-Origin Resource Sharing (CORS) is on for JIM's address. CORS is a setting for the whole AD FS farm, not the Application Group, so the command leaves it to you. It needs AD FS 2019 or later.

On the AD FS server, in PowerShell as an AD FS administrator, turn CORS on and add JIM's address to the trusted origins. `-CORSTrustedOrigins` replaces the list, so this keeps any origins already trusted:

```powershell
Set-AdfsResponseHeaders -EnableCORS $true
$origins = @((Get-AdfsResponseHeaders).CORSTrustedOrigins) + 'https://jim.example.com' | Where-Object { $_ } | Select-Object -Unique
Set-AdfsResponseHeaders -CORSTrustedOrigins $origins
```

The origin is JIM's address without a path: `https://jim.example.com`, or `https://jim.example.com:8443` on another port. See Microsoft's [guide to AD FS response headers](https://learn.microsoft.com/windows-server/identity/ad-fs/operations/customize-http-security-headers-ad-fs#cors-customization).

## :material-check-all: Keep the registration right

Run `Register-JIMIdentityProvider` again, with the same `-Url`, to check and repair the registration. It changes nothing when the registration is right, and puts back whatever has drifted, such as a removed redirect URI, a changed scope, or another client given access to JIM's API. The values it returns stay the same, so JIM's settings need no change. Add `-WhatIf` to list what has drifted without changing it.

If JIM's address changes, run it with the new address. It replaces the portal's redirect URIs; the client identifiers and the secret stay as they are.

## :material-wrench-outline: AD FS Troubleshooting

- **Sign-in is unavailable / JIM does not trust the certificate**: JIM fetches AD FS's discovery document and signing keys over HTTPS, so it must trust the federation service's TLS certificate, which usually comes from your own certificate authority. Trust that certificate authority for sign-in; see [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md). The token-signing certificate needs no trust configuration: JIM reads its public keys from AD FS's published key set, and follows AD FS's automatic certificate rollover by itself (see [Signing Key Rollover](../sso-setup.md#signing-key-rollover)).
- **Someone signs in to AD FS, but JIM says they have no JIM identity**: JIM has no Metaverse Object whose `Object GUID` holds their `objectGUID`. Check that the import Synchronisation Rule for Active Directory maps `objectGUID` to `Object GUID` and that the person is in its scope, then run the synchronisation; they can sign in once it has created their Metaverse Object. For the first administrator, check that `JIM_SSO_INITIAL_ADMIN` is their `objectGUID`.
- **JIM does not start, naming `JIM_SSO_INITIAL_ADMIN`**: with `JIM_SSO_MV_ATTRIBUTE=Object GUID`, the initial administrator must be a GUID. Set it to the value from Step 2, then start JIM again.
- **AD FS shows an error page with an MSIS code**: the registration is missing something or has drifted. Run `Register-JIMIdentityProvider` with `-WhatIf` to see what, then without it to repair it. The AD FS server's **AD FS/Admin** event log names the cause.
- **PKCE / PowerShell sign-in fails**: Confirm AD FS is Windows Server 2019 or later; earlier versions do not support PKCE and cannot authenticate the PowerShell module's public client.

## :material-arrow-right-circle-outline: Next steps

If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
