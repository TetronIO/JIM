---
title: "SSO Setup: Keycloak"
description: Register JIM with Keycloak so people sign in to the portal, and the PowerShell module, with their Keycloak accounts.
type: how-to
audience: implementer
---

# SSO Setup: Keycloak

Use this guide to let people sign in to JIM with their Keycloak accounts, both in the portal and in the PowerShell module with `Connect-JIM`. One command registers JIM in your Keycloak realm, and you give JIM the values it returns. JIM identifies each person by their Keycloak user ID, the `sub` claim, which is the same in tokens from the portal and from the PowerShell module.

```jim-diagram
archetype: comparison
title: "JIM's two Keycloak clients compared"
desc: "A Keycloak client cannot be both confidential and public, so JIM has two. The portal's client, jim, is confidential: client authentication is on, and its client secret is JIM_SSO_SECRET. Its redirect URI is /signin-oidc, with /signout-callback-oidc after sign-out, and it is JIM_SSO_CLIENT_ID. The PowerShell module's client, jim-powershell, is public: client authentication is off and it has no secret. Its redirect URIs are http://localhost:8400/callback/ and the same for each port up to 8409, it may also ask for offline_access for a refresh token, and it is JIM_SSO_PUBLIC_CLIENT_ID. Both sign in with PKCE, and both have the jim-api client scope as optional, which puts jim in the access token's audience; no other client may ask for it."
columns:
  - { label: "`jim`", sub: "the portal, confidential" }
  - { label: "`jim-powershell`", sub: "the PowerShell module, public" }
rows:
  - label: Client authentication
    cells:
      - { text: "On, with a secret", sub: "`JIM_SSO_SECRET`" }
      - { text: "Off, no secret" }
  - label: Redirect URIs
    cells:
      - { text: "`/signin-oidc`", sub: "and `/signout-callback-oidc`" }
      - { text: "`localhost` ports 8400 to 8409", sub: "`/callback/`, ten in all" }
  - label: Client scopes
    cells:
      - { text: "`jim-api`, optional" }
      - { text: "`jim-api`, optional", sub: "and `offline_access`" }
  - label: In JIM
    cells:
      - { text: "`JIM_SSO_CLIENT_ID`" }
      - { text: "`JIM_SSO_PUBLIC_CLIENT_ID`" }
footer: "Both sign in with PKCE; `jim-api` puts `jim` in the access token's audience"
```

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the [confidential and public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, [how JIM recognises people](../sso-setup.md#how-jim-recognises-people), and the [testing steps](../sso-setup.md#testing-your-configuration) you can run once configuration is complete.

## :material-clipboard-list-outline: Before you begin

You need:

- **A Keycloak realm for the people who will sign in to JIM.** To create one, open the Keycloak Admin Console, open the realm list, and select **Create realm**.
- **JIM's address**, such as `https://jim.example.com`, with the port if JIM does not use 443.
- **PowerShell 7 with the JIM PowerShell module**, on any computer that can reach Keycloak. See [Installation](../../powershell/index.md#installation).
- **An account that administers the realm**: a Keycloak administrator in the master realm, or an account in JIM's realm with the `realm-admin` role of its `realm-management` client.
- **JIM trusting Keycloak's HTTPS certificate,** when it comes from your own certificate authority. See [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md).

## :material-numeric-1-circle-outline: Step 1: Register JIM with Keycloak

`Register-JIMIdentityProvider` creates JIM's clients in the realm and returns the values JIM needs. It creates a confidential client for the portal, `jim`, a public client for the PowerShell module, `jim-powershell`, and the `jim-api` client scope, which puts `jim` in the audience of an access token, as JIM requires. Only JIM's two clients may ask for `jim-api`. What each one contains is on the cmdlet's page: [Register-JIMIdentityProvider](../../powershell/identity-providers.md#register-jimidentityprovider).

1. In PowerShell 7, run the command with JIM's address and the realm's address, and sign in as the account that administers the realm when it asks:

    ```powershell
    $registration = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential (Get-Credential)
    $registration | Select-Object Authority, ClientId, PublicClientId, ApiScope, ClaimType, MvAttribute
    ```

    The account signs in to the master realm. If it belongs to JIM's realm instead, add `-AdminRealm 'jim'`. Add `-WhatIf` first if you want to see each change the command would make; it makes none.

2. Copy the portal's client secret, and keep it safe until Step 3:

    ```powershell
    [System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard
    ```

    The command returns the secret only when it creates or replaces it. Keycloak also shows it on the `jim` client's **Credentials** tab.

### Register JIM by hand instead

These steps make the same registration in the Keycloak Admin Console. Replace `https://jim.example.com` with JIM's address.

1. Under **Clients**, select **Create client**, and create the portal's client:
    - **Client ID**: `jim`
    - **Client authentication**: On
    - **Authentication flow**: **Standard flow** only. Clear **Direct access grants**.
    - **Valid redirect URIs**: `https://jim.example.com/signin-oidc`
    - **Valid post logout redirect URIs**: `https://jim.example.com/signout-callback-oidc`
2. On the client's **Advanced** tab, under **Advanced settings**, set **Proof Key for Code Exchange Code Challenge Method** to **S256**, and save. Copy the **Client secret** from the **Credentials** tab.
3. Create the PowerShell module's client the same way, with these differences:
    - **Client ID**: `jim-powershell`
    - **Client authentication**: Off
    - **Valid redirect URIs**: `http://localhost:8400/callback/` and the same for each port up to 8409, ten in all. The module listens on the first free port of these. Add `https://jim.example.com/api/reference` too, for the API reference's **Try It**, which signs in with the same client.
    - **Web origins**: `https://jim.example.com`, so Keycloak answers **Try It**'s token request from the browser.
    - No post logout redirect URI.
4. Under **Client scopes**, select **Create client scope**:
    - **Name**: `jim-api`
    - **Type**: None. A realm default scope is added to every new client, and any client that can ask for `jim-api` can get tokens JIM accepts.
    - **Include in token scope**: On
5. On `jim-api`'s **Mappers** tab, select **Configure a new mapper**, then **Audience**:
    - **Name**: `jim-api-audience`
    - **Included Client Audience**: `jim`
    - **Add to ID token**: Off
    - **Add to access token**: On
6. On each of the two clients' **Client scopes** tabs, select **Add client scope**, and add `jim-api` as **Optional**. Check that `basic`, `profile` and `email` are listed as **Default**, and on `jim-powershell`, that `offline_access` is listed as **Optional**, so the module gets a refresh token.

## :material-numeric-2-circle-outline: Step 2: Find your first administrator's user ID

JIM gives the Administrator role to the person whose Keycloak user ID is `JIM_SSO_INITIAL_ADMIN`, at their first sign-in, and creates their Metaverse Object. In the Keycloak Admin Console, open JIM's realm, select **Users**, and open the account they sign in with. The **ID** on its **Details** tab is the value, a GUID such as `3f2504e0-4f89-11d3-9a0c-0305e82c3301`.

## :material-numeric-3-circle-outline: Step 3: Configure JIM

Set these environment variables to the values from Steps 1 and 2, then restart JIM (see [Configuration Reference](../configuration.md)):

```bash
JIM_SSO_AUTHORITY={Authority}
JIM_SSO_CLIENT_ID={ClientId}
JIM_SSO_SECRET={the client secret from Step 1}
JIM_SSO_API_SCOPE={ApiScope}
JIM_SSO_PUBLIC_CLIENT_ID={PublicClientId}

# Identify people by their Keycloak user ID ({ClaimType} and {MvAttribute})
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN={the user ID from Step 2}
```

**Example:**

```bash
JIM_SSO_AUTHORITY=https://sso.example.com/realms/jim
JIM_SSO_CLIENT_ID=jim
JIM_SSO_SECRET=AbCdEfGhIjKlMnOpQrStUvWxYz123456
JIM_SSO_API_SCOPE=jim-api
JIM_SSO_PUBLIC_CLIENT_ID=jim-powershell
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN=3f2504e0-4f89-11d3-9a0c-0305e82c3301
```

`JIM_SSO_PUBLIC_CLIENT_ID` is what lets administrators sign in with `Connect-JIM`. Scripts that run unattended cannot sign in interactively, so they use a JIM API key instead; see [API Authentication](../../api/authentication.md).

!!! note "Scripts and services use API keys"
    JIM accepts Keycloak access tokens from people only: a token gets the roles of the Metaverse Object its `sub` identifies. A Keycloak service account identifies no Metaverse Object, so its tokens get no roles and JIM refuses them.

## :material-check-decagram-outline: Check it worked

1. Open JIM in a browser as your first administrator. JIM sends you to Keycloak to sign in, then returns you to JIM, signed in.
2. Open `/claims` and check that `sub`, `name` and `email` carry your values.
3. In PowerShell 7, run `Connect-JIM -Url 'https://jim.example.com'`, sign in in the browser it opens, then run `Get-JIMUserInfo`. It reports `IsAdministrator : True`, with the `Administrator` and `User` roles.

To check sign-out too, see [Testing Your Configuration](../sso-setup.md#testing-your-configuration).

## :material-check-all: Keep the registration right

Run `Register-JIMIdentityProvider` again, with the same `-Url` and `-Authority`, to check and repair the registration. It changes nothing when the registration is right, and puts back whatever has drifted, such as a removed redirect URI, PKCE turned off, or `jim-api` added to another client. The values it returns stay the same, so JIM's settings need no change. Add `-WhatIf` to list what has drifted without changing it.

If JIM's address changes, run it with the new address. It replaces the portal's redirect URIs; the client IDs and the secret stay as they are. If people reach JIM at more than one address, give each to `-Url`.

To check the registration on a schedule, run it as a Keycloak client's service account with `-ClientCredential`, giving the account the `realm-admin` role of JIM's realm's `realm-management` client. See [Register-JIMIdentityProvider](../../powershell/identity-providers.md#register-jimidentityprovider).

## :material-history: Realms set up from an earlier version of this guide

Earlier versions of this guide created `jim-api` with **Type: Optional**, and offered a `jim-service` service account client. To bring such a realm in line, run `Register-JIMIdentityProvider` with `-WhatIf` and your clients' IDs, for example `-ClientId 'jim' -PublicClientId 'jim-powershell'`. It lists what differs, such as `jim-api` offered to every new client or to clients other than JIM's. Run it again without `-WhatIf` to correct it. Delete the `jim-service` client if nothing uses it.

## :material-wrench-outline: Keycloak Troubleshooting

- **Sign-in is unavailable / JIM does not trust the certificate**: Keycloak's HTTPS certificate comes from a certificate authority JIM does not trust, usually your own. Trust it for sign-in; see [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md).
- **`Register-JIMIdentityProvider` says Keycloak refused a request**: the account cannot administer the realm. Give it the `realm-admin` role of the realm's `realm-management` client, or use a master realm administrator.
- **Realm name**: Ensure the realm name in `JIM_SSO_AUTHORITY` matches exactly (case-sensitive).
- **`Invalid parameter: redirect_uri`, or tokens without `sub`, `name` or `email`**: the registration has drifted. Run `Register-JIMIdentityProvider` with `-WhatIf` to see what, then without it to repair it.

**Checking claim values:**

1. Navigate to **Clients** > **jim** > **Client scopes**
2. Click **Evaluate**
3. Select a user, enter `jim-api` under **Scope parameter**, and click **Evaluate**
4. Check the **Generated access token**: its `aud` includes `jim`, and its `sub` is the user's ID

## :material-arrow-right-circle-outline: Next steps

If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
