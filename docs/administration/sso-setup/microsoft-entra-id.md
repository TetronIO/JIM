---
title: SSO Setup - Microsoft Entra ID
---

# SSO Setup: Microsoft Entra ID

Step-by-step instructions for configuring Single Sign-On with JIM using Microsoft Entra ID as the OIDC identity provider.

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the prerequisites, the [confidential vs public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, and the [testing steps](../sso-setup.md#testing-your-configuration) you run once configuration is complete.

## Step 1: Register the Application

1. Sign in to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Microsoft Entra ID** > **App registrations**
3. Click **New registration**
4. Configure the registration:
    - **Name**: `JIM Identity Management`
    - **Supported account types**: Select based on your requirements:
        - *Single tenant*: Only users in your organisation
        - *Multitenant*: Users from any Entra ID directory
    - **Redirect URI**:
        - Platform: **Web**
        - URI: `https://your-jim-url/signin-oidc`
5. Click **Register**
6. After registration, go to **Authentication** and add a second Web redirect URI for the sign-out callback: `https://your-jim-url/signout-callback-oidc`, then click **Save**. Entra ID validates the `post_logout_redirect_uri` parameter against the same Redirect URIs list as sign-in, so this entry is required for sign-out to work.

## Step 2: Note the Application Details

After registration, note these values from the **Overview** page:

- **Application (client) ID**: e.g. `12345678-1234-1234-1234-123456789abc`
- **Directory (tenant) ID**: e.g. `87654321-4321-4321-4321-cba987654321`

## Step 3: Create a Client Secret

1. Go to **Certificates & secrets**
2. Click **New client secret**
3. Add a description (e.g. `JIM Web Client`)
4. Select an expiry period
5. Click **Add**
6. **Copy the secret value immediately** -- it will not be shown again

## Step 4: Configure API Permissions

1. Go to **API permissions**
2. Verify `User.Read` (Delegated) is present -- this is added by default and provides the OpenID Connect scopes needed for authentication

!!! note
    The standard OIDC scopes (`openid`, `profile`, `email`, `offline_access`) are requested at runtime and do not need to be added as Graph API permissions.

## Step 5: Expose an API

This step creates the API scope that JIM uses for JWT Bearer token validation (for API access).

1. Go to **Expose an API**
2. Click **Set** next to Application ID URI
    - Accept the default (`api://{client-id}`) or set a custom URI
    - Click **Save**
3. Click **Add a scope**
4. Configure the scope:
    - **Scope name**: `access_as_user`
    - **Who can consent**: Admins and users
    - **Admin consent display name**: `Access JIM API`
    - **Admin consent description**: `Allows the app to access JIM API on behalf of the signed-in user`
    - **User consent display name**: `Access JIM API`
    - **User consent description**: `Allows this app to access JIM API on your behalf`
    - **State**: Enabled
5. Click **Add scope**

## Step 5a: Configure Scalar API Reference Authentication (Development only, Optional)

JIM's interactive [Scalar](https://scalar.com/) API reference is only exposed in development environments (it is disabled in production to reduce the attack surface), so this step is only relevant if you are configuring SSO against a non-production JIM instance where you want to use the Scalar "try it out" flow with OAuth.

To enable OAuth authentication in the Scalar API reference:

1. Go to **Authentication**
2. Click **Add Redirect URI**
3. Select **Single-page application**
4. Add redirect URI: `https://your-jim-dev-url/api/reference` (Scalar uses the API reference page itself as the OAuth redirect target; there is no separate `oauth2-redirect.html` handler)
5. Click **Configure**

!!! note
    For production deployments, use the JIM PowerShell module for interactive API testing instead. See Step 5b below.

## Step 5b: Configure PowerShell Module Authentication (Optional but recommended)

Configuring the PowerShell module now means administrators and automation scripts can connect to JIM interactively with their SSO account, without needing to issue or manage API keys. You can skip this step if you don't plan to use the PowerShell module, but it only takes a moment and is worth doing up front.

Entra ID allows a single app registration to serve both the JIM web application (confidential client with a secret) and the PowerShell module (public client using PKCE with a loopback redirect). You can reuse the same app registration from Step 1; just add a new platform for the PowerShell flow:

1. Go to **Authentication**
2. Click **Add a platform**
3. Select **Mobile and desktop applications**
4. Check the suggested redirect URI: `https://login.microsoftonline.com/common/oauth2/nativeclient`
5. In **Custom redirect URIs**, add: `http://localhost:8400/callback/`
6. Click **Configure**
7. Scroll down to **Advanced settings**
8. Set **Allow public client flows** to **Yes**
9. Click **Save**

Because the web and PowerShell platforms share the same **Application (client) ID**, you can either:

- **Share the client ID** (simplest): leave `JIM_SSO_PUBLIC_CLIENT_ID` unset in Step 6 below. The PowerShell module will use `JIM_SSO_CLIENT_ID`, which now has both Web and Mobile/Desktop platforms configured.
- **Use a separate app registration** for the PowerShell module (stricter isolation): create a second app registration with only the Mobile/Desktop platform, note its Application (client) ID, and set `JIM_SSO_PUBLIC_CLIENT_ID` to that value.

!!! note
    Entra ID requires exact redirect URI matching. If port 8400 is busy, the module will try ports 8401--8409. You may need to add additional redirect URIs if you encounter port conflicts.

!!! note "Refresh tokens"
    The refresh token is issued because JIM requests the `offline_access` scope at runtime; it is a standard OIDC scope and does not need to be added as an API permission. Setting **Allow public client flows** to **Yes** (step 8 above) is a separate requirement: it marks the registration as a public client so the module can redeem the authorization code via PKCE without a client secret. This matters when the module shares the web application's registration, which already has a secret and would otherwise be treated as confidential. The refresh token enables silent in-session renewal and optional cross-session token persistence.

## Step 6: Configure JIM Environment Variables

Add these to your `.env` file:

```bash
# Microsoft Entra ID Configuration
JIM_SSO_AUTHORITY=https://login.microsoftonline.com/{your-tenant-id}/v2.0
JIM_SSO_CLIENT_ID={your-application-client-id}
JIM_SSO_SECRET={your-client-secret}
JIM_SSO_API_SCOPE=api://{your-application-client-id}/access_as_user

# Optional: only set if you created a separate app registration for the PowerShell
# module in Step 5b. Leave unset to reuse JIM_SSO_CLIENT_ID.
# JIM_SSO_PUBLIC_CLIENT_ID={your-powershell-app-client-id}

# User identity mapping
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN={your-admin-sub-value}
```

**Example with real values (single app registration, web + PowerShell share client ID):**

```bash
JIM_SSO_AUTHORITY=https://login.microsoftonline.com/87654321-4321-4321-4321-cba987654321/v2.0
JIM_SSO_CLIENT_ID=12345678-1234-1234-1234-123456789abc
JIM_SSO_SECRET=abc123~secretvalue
JIM_SSO_API_SCOPE=api://12345678-1234-1234-1234-123456789abc/access_as_user
```

## Step 7: Find Your Admin User's Subject Identifier

1. Start JIM with the configuration above
2. Log in with your admin account
3. Navigate to `/claims` in the web interface
4. Find the `sub` claim value
5. Update `JIM_SSO_INITIAL_ADMIN` with this value

## Next steps

With the configuration in place, [test your configuration](../sso-setup.md#testing-your-configuration) to verify sign-in, claims, sign-out, and API access. If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
