---
title: "SSO Setup: Keycloak"
description: Register JIM with Keycloak so administrators sign in to the portal, and the PowerShell module, with their Keycloak accounts.
type: how-to
audience: implementer
---

# SSO Setup: Keycloak

Step-by-step instructions for configuring Single Sign-On with JIM using Keycloak as the OIDC identity provider.

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the prerequisites, the [confidential vs public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, and the [testing steps](../sso-setup.md#testing-your-configuration) you run once configuration is complete.

## :material-numeric-1-circle-outline: Step 1: Create a Realm (if needed)

1. Log in to the Keycloak Admin Console
2. Hover over the realm dropdown and click **Create Realm**
3. Enter a **Realm name** (e.g. `jim`)
4. Click **Create**

## :material-numeric-2-circle-outline: Step 2: Create a Client for JIM

1. Navigate to **Clients**
2. Click **Create client**
3. Configure the client:
    - **Client type**: OpenID Connect
    - **Client ID**: `jim`
4. Click **Next**
5. Configure capability:
    - **Client authentication**: ON
    - **Authorisation**: OFF
    - **Authentication flow**: Check **Standard flow** (Authorisation Code)
6. Click **Next**
7. Configure login settings:
    - **Root URL**: `https://your-jim-url` (`your-jim-url` is JIM's address, such as `jim.example.com`, with `:port` added if JIM does not use 443)
    - **Valid redirect URIs**: `https://your-jim-url/signin-oidc`
    - **Valid post logout redirect URIs**: `https://your-jim-url/signout-callback-oidc`
    - **Web origins**: `https://your-jim-url`
8. Click **Save**

## :material-numeric-3-circle-outline: Step 3: Get the Client Secret

1. Go to the **Credentials** tab
2. Copy the **Client secret**

!!! note "Scripts and services use API keys"
    JIM accepts Keycloak access tokens from people only: a token gets the roles of the Metaverse Object its `sub` identifies. A Keycloak service account (a client using the client credentials grant) identifies no Metaverse Object, so its tokens get no roles and JIM refuses them. For unattended scripts and services, issue a JIM API key instead; see [API Authentication](../../api/authentication.md).

## :material-numeric-4-circle-outline: Step 4: Create the API Client Scope

1. Navigate to **Client scopes**
2. Click **Create client scope**
3. Configure:
    - **Name**: `jim-api`
    - **Type**: None
    - **Protocol**: OpenID Connect
4. Click **Save**
5. Go to the **Mappers** tab
6. Click **Configure a new mapper**
7. Select **Audience**
8. Configure:
    - **Name**: `jim-api-audience`
    - **Included Client Audience**: `jim` (the client from Step 2, and the value of `JIM_SSO_CLIENT_ID`)
    - **Add to access token**: ON
9. Click **Save**

The audience must be the Step 2 client and nothing else: JIM accepts an access token only when its `aud` includes `JIM_SSO_CLIENT_ID`.

**Type: None** keeps `jim-api` from being offered to every client created in the realm. The **Type** field sets the scope's realm default, and Keycloak links realm default scopes to each new client. Any client that can request `jim-api` can obtain tokens JIM accepts, so Steps 5 and 6 link it only to JIM's own two clients.

## :material-numeric-5-circle-outline: Step 5: Assign the Scope to the Client

1. Navigate to **Clients** > **jim**
2. Go to the **Client scopes** tab
3. Click **Add client scope**
4. Select `jim-api` and add as **Optional**

## :material-numeric-6-circle-outline: Step 6: Configure PowerShell Module Authentication (Recommended)

Configuring the PowerShell module now means administrators can connect to JIM interactively with their SSO account. Unattended scripts cannot sign in interactively, so they use a JIM API key.

The JIM PowerShell module uses OAuth 2.0 with PKCE for interactive browser-based authentication. **Keycloak requires this to be a separate client** from the confidential web client: a single Keycloak client cannot be both confidential (with a secret) and public (PKCE/loopback). You must create a second, public client and tell JIM about it via `JIM_SSO_PUBLIC_CLIENT_ID` in Step 7.

1. Navigate to **Clients**
2. Click **Create client**
3. Configure the client:
    - **Client type**: OpenID Connect
    - **Client ID**: `jim-powershell` (this exact value is what you'll set `JIM_SSO_PUBLIC_CLIENT_ID` to in Step 7)
4. Click **Next**
5. Configure capability:
    - **Client authentication**: OFF (this makes it a public client)
    - **Authorisation**: OFF
    - **Authentication flow**: Check **Standard flow** (Authorisation Code)
6. Click **Next**
7. Configure login settings:
    - **Root URL**: Leave empty
    - **Valid redirect URIs**: `http://localhost:8400/callback/`
    - **Web origins**: `+` (allows all origins from redirect URIs)
8. Click **Save**
9. Go to the **Client scopes** tab
10. Click **Add client scope**
11. Select `jim-api` and add as **Optional**
12. Confirm `offline_access` is listed (Keycloak adds it as a default optional scope on new clients). If it is missing, click **Add client scope**, select `offline_access`, and add it as **Optional**. This lets the module receive a refresh token for silent renewal and token persistence.

!!! note
    The PowerShell module uses loopback redirect URIs per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). If port 8400 is busy, the module will try ports 8401 to 8409. Add the corresponding redirect URIs (e.g. `http://localhost:8401/callback/` through `http://localhost:8409/callback/`) if port conflicts are likely in your environment.

## :material-numeric-7-circle-outline: Step 7: Configure JIM Environment Variables

```bash
# Keycloak Configuration
JIM_SSO_AUTHORITY=https://{your-keycloak-server}/realms/{realm-name}
JIM_SSO_CLIENT_ID=jim
JIM_SSO_SECRET={your-client-secret}
JIM_SSO_API_SCOPE=jim-api

# Client ID for the public client you created in Step 6 for the PowerShell
# module. Required if you want interactive (SSO) PowerShell authentication;
# omit if you only plan to use API keys.
JIM_SSO_PUBLIC_CLIENT_ID=jim-powershell

# User identity mapping
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN={your-admin-sub-value}
```

**Example:**

```bash
JIM_SSO_AUTHORITY=https://keycloak.example.com/realms/jim
JIM_SSO_CLIENT_ID=jim
JIM_SSO_SECRET=AbCdEfGhIjKlMnOpQrStUvWxYz123456
JIM_SSO_API_SCOPE=jim-api
JIM_SSO_PUBLIC_CLIENT_ID=jim-powershell
```

## :material-check-decagram-outline: Check it worked

1. Open JIM in a browser. JIM sends you to Keycloak to sign in, then returns you to JIM, signed in.
2. Open `/claims` and check that `sub`, `name` and `email` carry your values.
3. If you set up PowerShell authentication, run `Connect-JIM -Url "https://your-jim-url"`, sign in in the browser it opens, then run `Test-JIMConnection`. It reports `Connected : True` and `AuthMethod : OAuth`.

To check sign-out and API access too, see [Testing Your Configuration](../sso-setup.md#testing-your-configuration).

## :material-history: Realms set up from an earlier version of this guide

Earlier versions of this guide created `jim-api` with **Type: Optional** and offered a `jim-service` service account client. To bring such a realm in line:

1. Under **Client scopes**, set the **Assigned type** of `jim-api` to **None**.
2. For each client other than `jim` and `jim-powershell`, open its **Client scopes** tab and remove `jim-api` if it is listed.
3. Delete the `jim-service` client if nothing uses it.

## :material-wrench-outline: Keycloak Troubleshooting

- **Sign-in is unavailable / JIM does not trust the certificate**: Keycloak's HTTPS certificate comes from a certificate authority JIM does not trust, usually your own. Trust it for sign-in; see [Trust Your Identity Provider's Certificate](trusting-your-identity-provider.md).
- **Realm name**: Ensure the realm name in `JIM_SSO_AUTHORITY` matches exactly (case-sensitive)
- **Client scopes**: Verify `openid`, `profile`, and `email` scopes are included
- **Token mapper**: If the `sub` claim is missing, add a mapper in Client Scopes > openid > Mappers

**Checking claim values:**

1. Navigate to **Clients** > **jim** > **Client scopes**
2. Click **Evaluate**
3. Select a user and click **Evaluate**
4. Check the **Generated access token** to see claim values

## :material-arrow-right-circle-outline: Next steps

If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
