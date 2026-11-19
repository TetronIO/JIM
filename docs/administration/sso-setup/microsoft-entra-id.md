---
title: "SSO Setup: Microsoft Entra ID"
description: Register JIM with Microsoft Entra ID so people sign in to the portal, and the PowerShell module, with their Entra ID accounts.
type: how-to
audience: implementer
---

# SSO Setup: Microsoft Entra ID

Use this guide to let people sign in to JIM with their Microsoft Entra ID accounts, both in the portal and in the PowerShell module with `Connect-JIM`. One command registers JIM in your tenant, and you give JIM the values it returns. JIM identifies each person by their Entra ID object ID (the `oid` claim), which is the same whichever way they sign in and stays the same for the life of their account. This configuration is verified against Microsoft Entra ID before every JIM release.

```jim-diagram
archetype: anatomy
title: What JIM's one application in Microsoft Entra ID holds
desc: "Register-JIMIdentityProvider creates one application in your tenant, which serves every way people sign in to JIM. Its Web platform holds the portal's redirect URIs, /signin-oidc and /signout-callback-oidc, and its client secret is JIM_SSO_SECRET. Its Mobile and desktop platform holds the PowerShell module's loopback redirect URIs, http://localhost:8400/callback/ to 8409. Its single-page application platform holds JIM's /api/reference/, for the API reference's Try It. Expose an API makes it JIM's API, api://{client ID} with the access_as_user scope, issuing version 2 access tokens; that scope is JIM_SSO_API_SCOPE. API permissions asks Microsoft Graph for openid, profile, email and offline_access, consented for everyone. The application's client ID is JIM_SSO_CLIENT_ID and JIM_SSO_PUBLIC_CLIENT_ID."
subject:
  kind: object
  label: JIM's application
  parts:
    - { label: Web, detail: "the portal: `/signin-oidc`, a client secret" }
    - { label: Mobile and desktop, detail: "`Connect-JIM`: ports 8400 to 8409" }
    - { label: Single-page application, detail: "Try It: `/api/reference/`" }
    - { label: Expose an API, detail: "`access_as_user`, version 2 tokens" }
properties:
  - { name: Client ID, value: "`JIM_SSO_CLIENT_ID`" }
  - { name: Identifies people by, value: "`oid`" }
  - { name: Consent, value: given for everyone }
```

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the [confidential and public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, [how JIM recognises people](../sso-setup.md#how-jim-recognises-people), and the [testing steps](../sso-setup.md#testing-your-configuration) you can run once configuration is complete.

## :material-clipboard-list-outline: Before you begin

You need:

- **An account that registers applications and gives admin consent in your tenant,** such as an Application Administrator or Cloud Application Administrator.
- **JIM's address**, such as `https://jim.example.com`, with the port if JIM does not use 443.
- **PowerShell 7 with the JIM PowerShell module,** on any computer that can reach `login.microsoftonline.com` and `graph.microsoft.com`. See [Installation](../../powershell/index.md#installation).
- **JIM able to reach `login.microsoftonline.com`.** Entra ID is a cloud service, so this is the one part of JIM's sign-in that needs the internet.
- **The people who will sign in, in the Metaverse with their object ID.** JIM signs someone in only when it has a Metaverse Object whose `Object Identifier` holds their Entra ID object ID: map it from a Connected System that holds it (see [Attribute Mappings](../../configuration/attribute-mappings.md)). Your first administrator is the exception: JIM creates them at their first sign-in.

## :material-numeric-1-circle-outline: Step 1: Register JIM with Entra ID

`Register-JIMIdentityProvider` creates JIM's application in your tenant and returns the values JIM needs, your own object ID among them. What the application contains is on the cmdlet's page: [Register-JIMIdentityProvider](../../powershell/identity-providers.md#register-jimidentityprovider).

1. In PowerShell 7, run the command with JIM's address and your tenant:

    ```powershell
    $registration = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'contoso.onmicrosoft.com'
    $registration | Select-Object Authority, ClientId, ApiScope, ClaimType, MvAttribute, InitialAdmin
    ```

    It shows a code and a web address. Open the address, enter the code, and sign in with the account from **Before you begin**. Add `-WhatIf` first if you want to see each change the command would make; it makes none.

2. Copy the portal's client secret. Entra ID shows it only once, so keep it safe until Step 2:

    ```powershell
    [System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard
    ```

    If the secret is lost, run the command again with `-ResetClientSecret` for a new one. The command removes the old secret, so JIM signs nobody in until `JIM_SSO_SECRET` holds the new one.

`InitialAdmin` is the object ID of the account you signed in with. If someone else is to be JIM's first administrator, use their object ID instead: it is on their page in the Microsoft Entra admin centre, under **Users**.

## :material-numeric-2-circle-outline: Step 2: Configure JIM

Set these environment variables to the values from Step 1, then restart JIM (see [Configuration Reference](../configuration.md)):

```bash
JIM_SSO_AUTHORITY={Authority}
JIM_SSO_CLIENT_ID={ClientId}
JIM_SSO_SECRET={the client secret from Step 1}
JIM_SSO_API_SCOPE={ApiScope}

# Identify people by their Entra ID object ID ({ClaimType} and {MvAttribute})
JIM_SSO_CLAIM_TYPE=oid
JIM_SSO_MV_ATTRIBUTE=Object Identifier
JIM_SSO_INITIAL_ADMIN={InitialAdmin}
```

**Example:**

```bash
JIM_SSO_AUTHORITY=https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0
JIM_SSO_CLIENT_ID=e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_SECRET=generatedSecretValue123
JIM_SSO_API_SCOPE=api://e1234567-89ab-cdef-0123-456789abcdef/access_as_user
JIM_SSO_CLAIM_TYPE=oid
JIM_SSO_MV_ATTRIBUTE=Object Identifier
JIM_SSO_INITIAL_ADMIN=3f2504e0-4f89-11d3-9a0c-0305e82c3301
```

`JIM_SSO_PUBLIC_CLIENT_ID` stays unset: the PowerShell module signs in with the same application. Scripts that run unattended cannot sign in interactively, so they use a JIM API key instead; see [API Authentication](../../api/authentication.md).

## :material-check-decagram-outline: Check it worked

1. Open JIM in a browser as your first administrator. JIM sends you to Microsoft Entra ID to sign in, then returns you to JIM, signed in.
2. Open `/claims` and check that `oid`, `name` and `email` carry your values.
3. In PowerShell 7, run `Connect-JIM -Url 'https://jim.example.com'`, sign in in the browser it opens, then run `Get-JIMUserInfo`. It reports `IsAdministrator : True`, with the `Administrator` and `User` roles.
4. Open the API reference at `https://jim.example.com/api/reference` and sign in from **Try It**. It needs nothing more: the command registered the page.
5. Sign in as someone with no JIM role. JIM signs them in and refuses them administration.

To check sign-out too, see [Testing Your Configuration](../sso-setup.md#testing-your-configuration).

## :material-autorenew: Keep the registration right

Run the command again whenever you like, with the same `-Url` values. It changes nothing when the registration is right, and puts back anything that has drifted, such as a redirect URI someone removed. Give every address people reach JIM at to `-Url`; one left out has its redirect URIs removed.

The command signs you in each time. To run it unattended, give it a Microsoft Graph token with `-AccessToken`, for example from a workload identity. A token holding only `Application.ReadWrite.OwnedBy` keeps a registration its application owns right, but cannot give consent, and the command warns that an administrator must.

## :material-hand-pointing-right: Registering by hand

If you cannot run the command, register the application in the Microsoft Entra admin centre under **App registrations**, so that it matches what the command makes:

| Where | Setting |
|-------|---------|
| **Overview** | Supported account types: this organisational directory only |
| **Authentication** | **Web** redirect URIs `https://jim.example.com/signin-oidc` and `https://jim.example.com/signout-callback-oidc`; **Mobile and desktop applications** redirect URIs `http://localhost:8400/callback/` to `http://localhost:8409/callback/`; **Single-page application** redirect URI `https://jim.example.com/api/reference/` |
| **Certificates & secrets** | A client secret, for `JIM_SSO_SECRET` |
| **Expose an API** | Application ID URI `api://{client ID}`, and a scope `access_as_user` that admins and users can consent to |
| **Manifest** | `"requestedAccessTokenVersion": 2` |
| **Token configuration** | The optional claim `email`, in ID and access tokens |
| **API permissions** | Microsoft Graph delegated `openid`, `profile`, `email` and `offline_access`, then **Grant admin consent** |

The values for Step 2 are then: `https://login.microsoftonline.com/{tenant ID}/v2.0` for `JIM_SSO_AUTHORITY`, the application's client ID for `JIM_SSO_CLIENT_ID`, and `api://{client ID}/access_as_user` for `JIM_SSO_API_SCOPE`. Your object ID is on your page under **Users**.

## :material-wrench-outline: Microsoft Entra ID Troubleshooting

- **`Get-JIMSignInDiagnostic` names `requestedAccessTokenVersion`**: Entra ID is issuing version 1 access tokens, whose audience JIM does not accept. Set `requestedAccessTokenVersion` to `2` in the application's manifest, or run `Register-JIMIdentityProvider` again, which does.
- **People are asked to consent, or refused with `AADSTS65001`**: nobody has consented to JIM's permissions for everyone. Run `Register-JIMIdentityProvider` again as an account that may give admin consent, or select **Grant admin consent** on the application's **API permissions** page.
- **Sign-in is unavailable / JIM does not trust the certificate**: Microsoft Entra ID's certificates come from public certificate authorities the operating system trusts, so this usually means a proxy between JIM and Microsoft inspects TLS and presents your organisation's certificate instead. Trust the proxy's certificate authority for sign-in; see [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md).

## :material-arrow-right-circle-outline: Next steps

If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
