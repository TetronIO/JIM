---
title: SSO Setup - Keycloak
---

# SSO Setup: Keycloak

Step-by-step instructions for configuring Single Sign-On with JIM using Keycloak as the OIDC identity provider.

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the prerequisites, the [confidential vs public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, and the [testing steps](../sso-setup.md#testing-your-configuration) you run once configuration is complete.

## Step 1: Create a Realm (if needed)

1. Log in to the Keycloak Admin Console
2. Hover over the realm dropdown and click **Create Realm**
3. Enter a **Realm name** (e.g. `jim`)
4. Click **Create**

## Step 2: Create a Client for JIM

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
    - **Root URL**: `https://your-jim-url`
    - **Valid redirect URIs**: `https://your-jim-url/signin-oidc`
    - **Valid post logout redirect URIs**: `https://your-jim-url/signout-callback-oidc`
    - **Web origins**: `https://your-jim-url`
8. Click **Save**

## Step 3: Get the Client Secret

1. Go to the **Credentials** tab
2. Copy the **Client secret**

## Step 4: Create a Service Account Client (Optional)

If you need separate API clients for service-to-service communication:

1. Click **Create client**
2. Configure:
    - **Client type**: OpenID Connect
    - **Client ID**: `jim-service`
3. Click **Next**
4. Configure capability:
    - **Client authentication**: ON
    - **Authentication flow**: Check **Service accounts roles** (Client Credentials)
5. Click **Save**

## Step 5: Create Client Scopes (for API access)

1. Navigate to **Client scopes**
2. Click **Create client scope**
3. Configure:
    - **Name**: `jim-api`
    - **Type**: Optional
    - **Protocol**: OpenID Connect
4. Click **Save**
5. Go to the **Mappers** tab
6. Click **Configure a new mapper**
7. Select **Audience**
8. Configure:
    - **Name**: `jim-api-audience`
    - **Included Client Audience**: `jim` (or `jim-service` if created)
    - **Add to access token**: ON
9. Click **Save**

## Step 6: Assign the Scope to the Client

1. Navigate to **Clients** > **jim**
2. Go to the **Client scopes** tab
3. Click **Add client scope**
4. Select `jim-api` and add as **Optional**

## Step 6a: Configure PowerShell Module Authentication (Recommended)

Configuring the PowerShell module now means administrators and automation scripts can connect to JIM interactively with their SSO account, without needing to issue or manage API keys.

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
    The PowerShell module uses loopback redirect URIs per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). If port 8400 is busy, the module will try ports 8401--8409. Add the corresponding redirect URIs (e.g. `http://localhost:8401/callback/` through `http://localhost:8409/callback/`) if port conflicts are likely in your environment.

## Step 7: Configure JIM Environment Variables

```bash
# Keycloak Configuration
JIM_SSO_AUTHORITY=https://{your-keycloak-server}/realms/{realm-name}
JIM_SSO_CLIENT_ID=jim
JIM_SSO_SECRET={your-client-secret}
JIM_SSO_API_SCOPE=jim-api

# Client ID for the public client you created in Step 6a for the PowerShell
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

## Keycloak Troubleshooting

- **Realm name**: Ensure the realm name in `JIM_SSO_AUTHORITY` matches exactly (case-sensitive)
- **Client scopes**: Verify `openid`, `profile`, and `email` scopes are included
- **Token mapper**: If the `sub` claim is missing, add a mapper in Client Scopes > openid > Mappers

**Checking claim values:**

1. Navigate to **Clients** > **jim** > **Client scopes**
2. Click **Evaluate**
3. Select a user and click **Evaluate**
4. Check the **Generated access token** to see claim values

## Next steps

With the configuration in place, [test your configuration](../sso-setup.md#testing-your-configuration) to verify sign-in, claims, sign-out, and API access. If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
