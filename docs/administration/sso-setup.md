---
title: Single Sign-On Setup
---

# Single Sign-On Setup

Step-by-step instructions for configuring Single Sign-On (SSO) with JIM using an external OIDC identity provider.

JIM requires an OIDC-compliant identity provider for authentication. This guide covers setup for three common providers:

- [Microsoft Entra ID](#microsoft-entra-id)
- [AD FS (Active Directory Federation Services)](#ad-fs)
- [Keycloak](#keycloak)

!!! info "Before you begin"
    Ensure you have:

    - JIM deployed and accessible (see [Deployment Guide](deployment.md))
    - Administrative access to your identity provider
    - The URL where JIM is hosted (e.g. `https://jim.example.com`)

    JIM serves both the web interface and API from a single application. The API is available at `/api/` on the same host.

---

## Microsoft Entra ID

### Step 1: Register the Application

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

### Step 2: Note the Application Details

After registration, note these values from the **Overview** page:

- **Application (client) ID**: e.g. `12345678-1234-1234-1234-123456789abc`
- **Directory (tenant) ID**: e.g. `87654321-4321-4321-4321-cba987654321`

### Step 3: Create a Client Secret

1. Go to **Certificates & secrets**
2. Click **New client secret**
3. Add a description (e.g. `JIM Web Client`)
4. Select an expiry period
5. Click **Add**
6. **Copy the secret value immediately** -- it will not be shown again

### Step 4: Configure API Permissions

1. Go to **API permissions**
2. Verify `User.Read` (Delegated) is present -- this is added by default and provides the OpenID Connect scopes needed for authentication

!!! note
    The standard OIDC scopes (`openid`, `profile`, `email`, `offline_access`) are requested at runtime and do not need to be added as Graph API permissions.

### Step 5: Expose an API

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

### Step 5a: Configure Scalar API Reference Authentication (Development only, Optional)

JIM's interactive [Scalar](https://scalar.com/) API reference is only exposed in development environments (it is disabled in production to reduce the attack surface), so this step is only relevant if you are configuring SSO against a non-production JIM instance where you want to use the Scalar "try it out" flow with OAuth.

To enable OAuth authentication in the Scalar API reference:

1. Go to **Authentication**
2. Click **Add Redirect URI**
3. Select **Single-page application**
4. Add redirect URI: `https://your-jim-dev-url/api/reference` (Scalar uses the API reference page itself as the OAuth redirect target; there is no separate `oauth2-redirect.html` handler)
5. Click **Configure**

!!! note
    For production deployments, use the JIM PowerShell module for interactive API testing instead. See Step 5b below.

### Step 5b: Configure PowerShell Module Authentication (Optional but recommended)

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
    Setting **Allow public client flows** to **Yes** (step 8 above) is what lets Entra ID return a refresh token to the module. JIM requests the `offline_access` scope at runtime; it is a standard OIDC scope and does not need to be added as an API permission. The refresh token enables silent in-session renewal and optional cross-session token persistence.

### Step 6: Configure JIM Environment Variables

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

### Step 7: Find Your Admin User's Subject Identifier

1. Start JIM with the configuration above
2. Log in with your admin account
3. Navigate to `/claims` in the web interface
4. Find the `sub` claim value
5. Update `JIM_SSO_INITIAL_ADMIN` with this value

---

## AD FS

### Step 1: Create an Application Group

1. Open **AD FS Management** on your AD FS server
2. Navigate to **Application Groups**
3. Right-click and select **Add Application Group**
4. Enter a name: `JIM Identity Management`
5. Select **Web browser accessing a web application**
6. Click **Next**

### Step 2: Configure the Native Application

1. Note the **Client Identifier** (auto-generated GUID)
2. Add the **Redirect URI**: `https://your-jim-url/signin-oidc`
3. Add a second **Redirect URI** for the sign-out callback: `https://your-jim-url/signout-callback-oidc`. AD FS validates the `post_logout_redirect_uri` parameter against the same Redirect URIs list as sign-in, so this entry is required for sign-out to work.
4. Click **Next**

### Step 3: Configure Access Control

1. Select an access control policy (e.g. **Permit everyone**)
2. Click **Next**

### Step 4: Configure Application Permissions

1. Select **openid** and **profile** scopes
2. Click **Next** and then **Close**

### Step 4a: Configure PowerShell Module Authentication (Optional but recommended)

Configuring the PowerShell module now means administrators and automation scripts can connect to JIM interactively with their SSO account, without needing to issue or manage API keys. You can skip this step if you don't plan to use the PowerShell module, but it only takes a moment and is worth doing up front.

The JIM PowerShell module uses OAuth 2.0 with PKCE for interactive browser-based authentication. AD FS supports this through native applications, which can live in the same Application Group as the web application.

1. Right-click your Application Group and select **Properties**
2. Click **Add application**
3. Select **Native application**
4. Click **Next**
5. Note the **Client Identifier**. You can either:
    - **Reuse the web client's identifier** (simplest): use the same value as the web application from Step 2. Leave `JIM_SSO_PUBLIC_CLIENT_ID` unset in Step 7.
    - **Use a distinct identifier for the native app** (stricter isolation): accept the auto-generated GUID, and set `JIM_SSO_PUBLIC_CLIENT_ID` to that value in Step 7.
6. Add the **Redirect URI**: `http://localhost:8400/callback/`
7. Click **Next** and then **Close**

!!! note
    The PowerShell module uses loopback redirect URIs per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). AD FS native applications support PKCE, which the PowerShell module uses for security. If port 8400 is busy, you may need to add additional redirect URIs for ports 8401--8409.

### Step 5: Create the Web API

This step creates the API configuration for JWT Bearer token validation.

1. Right-click your Application Group and select **Properties**
2. Click **Add application**
3. Select **Web API**
4. Click **Next**
5. Set the **Identifier**: `api://{client-identifier}` (use the client ID from Step 2)
6. Click **Next**
7. Select an access control policy
8. Click **Next**
9. Under **Permitted scopes**, add:
    - `openid`
    - `profile`
    - `email`
    - `offline_access` (required for refresh-token issuance; enables PowerShell silent renewal and token persistence)
10. Click **Next** and then **Close**

### Step 6: Generate a Client Secret

1. In your Application Group properties, select the **Native application**
2. Click **Edit**
3. Under **Secrets**, click **Generate**
4. **Copy the secret value** -- store it securely

### Step 7: Configure JIM Environment Variables

```bash
# AD FS Configuration
JIM_SSO_AUTHORITY=https://{your-adfs-server}/adfs
JIM_SSO_CLIENT_ID={your-client-identifier}
JIM_SSO_SECRET={your-client-secret}
JIM_SSO_API_SCOPE=api://{your-client-identifier}

# Optional: only set if Step 4a used a distinct Client Identifier for the native
# (PowerShell) application. Leave unset to reuse JIM_SSO_CLIENT_ID.
# JIM_SSO_PUBLIC_CLIENT_ID={your-native-app-client-identifier}

# User identity mapping
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN={your-admin-sub-value}
```

**Example (native app reuses the web client identifier):**

```bash
JIM_SSO_AUTHORITY=https://adfs.example.com/adfs
JIM_SSO_CLIENT_ID=e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_SECRET=generatedSecretValue123
JIM_SSO_API_SCOPE=api://e1234567-89ab-cdef-0123-456789abcdef
```

### AD FS Claim Rules

AD FS may need claim rules to emit standard OIDC claims (`sub`, `email`, `name`). In your Web API's **Issuance Transform Rules**, add rules to map AD claims to OIDC claims:

```text
# Rule: Send User Principal Name as sub
c:[Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn"]
 => issue(Type = "sub", Value = c.Value);

# Rule: Send Display Name as name
c:[Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"]
 => issue(Type = "name", Value = c.Value);

# Rule: Send Email
c:[Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"]
 => issue(Type = "email", Value = c.Value);
```

### AD FS Troubleshooting

- **Token signing certificate**: Ensure your AD FS token signing certificate is trusted by the JIM server
- **Claim rules**: AD FS may need claim rules to emit standard OIDC claims (see above)
- **CORS**: If JIM and AD FS are on different domains, configure CORS in AD FS

---

## Keycloak

### Step 1: Create a Realm (if needed)

1. Log in to the Keycloak Admin Console
2. Hover over the realm dropdown and click **Create Realm**
3. Enter a **Realm name** (e.g. `jim`)
4. Click **Create**

### Step 2: Create a Client for JIM

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

### Step 3: Get the Client Secret

1. Go to the **Credentials** tab
2. Copy the **Client secret**

### Step 4: Create a Service Account Client (Optional)

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

### Step 5: Create Client Scopes (for API access)

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

### Step 6: Assign the Scope to the Client

1. Navigate to **Clients** > **jim**
2. Go to the **Client scopes** tab
3. Click **Add client scope**
4. Select `jim-api` and add as **Optional**

### Step 6a: Configure PowerShell Module Authentication (Recommended)

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

### Step 7: Configure JIM Environment Variables

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

### Keycloak Troubleshooting

- **Realm name**: Ensure the realm name in `JIM_SSO_AUTHORITY` matches exactly (case-sensitive)
- **Client scopes**: Verify `openid`, `profile`, and `email` scopes are included
- **Token mapper**: If the `sub` claim is missing, add a mapper in Client Scopes > openid > Mappers

**Checking claim values:**

1. Navigate to **Clients** > **jim** > **Client scopes**
2. Click **Evaluate**
3. Select a user and click **Evaluate**
4. Check the **Generated access token** to see claim values

---

## Confidential vs Public Clients

JIM uses two kinds of OAuth 2.0 clients, and depending on your identity provider they may be the same registration or two separate ones. Understanding which is which makes the per-provider steps above easier to follow.

| Client | Used by | Authentication | Configured via |
|--------|---------|----------------|----------------|
| **Confidential** | The JIM web application (Blazor UI) | A stored client secret sent directly from the backend to the IdP's token endpoint. Never exposed to the browser. | `JIM_SSO_CLIENT_ID` and `JIM_SSO_SECRET` |
| **Public** | Interactive clients that run on the user's machine (PowerShell module, future CLI tools) | PKCE with a loopback redirect URI (`http://localhost:8400/callback/`). No secret; the IdP relies on PKCE to bind the authorisation response to the original client. | `JIM_SSO_PUBLIC_CLIENT_ID` (falls back to `JIM_SSO_CLIENT_ID` when unset) |

**When are these the same registration?**

- **Microsoft Entra ID**: One app registration can have both a Web platform (for the confidential flow) and a Mobile/Desktop platform (for the public flow). In this case, leave `JIM_SSO_PUBLIC_CLIENT_ID` unset; the PowerShell module uses the same Application (client) ID as the web application.
- **AD FS**: A single Application Group can include both a web application and a native application that share a Client Identifier. Leave `JIM_SSO_PUBLIC_CLIENT_ID` unset in that case.

**When must they be different?**

- **Keycloak**: A single Keycloak client cannot be both confidential and public. You must create two clients (e.g. `jim` and `jim-powershell`) and set `JIM_SSO_PUBLIC_CLIENT_ID` to the public one.
- **Any IdP where your security policy forbids adding loopback redirects to a confidential client**: create a dedicated public client and point `JIM_SSO_PUBLIC_CLIENT_ID` at it.

**What does JIM do with these values?**

The JIM server exposes `/api/v1/auth/config`, an unauthenticated discovery endpoint that interactive clients call to learn how to authenticate. It returns:

- `authority`: the OIDC authority URL (`JIM_SSO_PUBLIC_AUTHORITY` if set, else `JIM_SSO_AUTHORITY`)
- `clientId`: the client ID for public/PKCE flows (`JIM_SSO_PUBLIC_CLIENT_ID` if set, else `JIM_SSO_CLIENT_ID`)
- `scopes`: the OAuth scopes to request: `openid`, `profile`, `offline_access`, and (when set) `JIM_SSO_API_SCOPE`

Backend token validation (the JWT bearer middleware that protects `/api/**` endpoints) always uses `JIM_SSO_AUTHORITY` for issuer and JWKS, and `JIM_SSO_API_SCOPE` for the audience, regardless of which public client issued the token. As long as the public and confidential clients belong to the same realm/tenant and both request the same API scope, tokens from either are valid.

!!! info "Why `offline_access`?"
    JIM requests the `offline_access` scope so the identity provider issues a **refresh token**. The PowerShell module uses this for two things: silent in-session token renewal (so long-running sessions don't expire mid-task), and optional cross-session token persistence (so opening a new terminal doesn't require re-authenticating in the browser). The refresh token is the only credential persisted, and it is stored in the operating system's credential store (Credential Manager on Windows, login Keychain on macOS, libsecret on Linux); never in plain text. Your public client must be permitted to receive `offline_access`. Most identity providers grant it to public clients by default; the per-provider steps above note where it must be enabled explicitly.

---

## Testing Your Configuration

### 1. Verify OIDC Discovery

Test that JIM can reach your identity provider's discovery document:

```bash
curl https://{your-authority}/.well-known/openid-configuration
```

You should see a JSON response with endpoints for `authorization_endpoint`, `token_endpoint`, etc.

### 2. Start JIM and Test Login

1. Start JIM with your configuration
2. Navigate to the JIM web interface
3. Click **Login**
4. You should be redirected to your identity provider
5. After authentication, you should be redirected back to JIM

### 3. Verify Claims

1. After logging in, navigate to `/claims`
2. Verify you see the expected claims:
    - `sub` -- Your unique identifier
    - `name` -- Your display name
    - `email` -- Your email address

### 4. Test Sign-Out

1. With an active JIM session, open the user menu in the navigation drawer (bottom-left)
2. Click **Sign out** and confirm the dialog
3. You should be redirected briefly to your identity provider's end-session endpoint, then returned to JIM
4. You should end up signed out of JIM; reopening a protected page should redirect you to your identity provider to sign in again

!!! info "Scope of sign-out"
    Sign-out clears the JIM session cookie and notifies the identity provider via RP-initiated logout (OIDC [end_session_endpoint](https://openid.net/specs/openid-connect-rpinitiated-1_0.html)). Whether the user is also signed out of other applications that share the same identity provider session depends entirely on the identity provider's single sign-out configuration, not JIM.

!!! tip "Hiding the sign-out button"
    For deployments where users cannot realistically sign out of their enterprise-managed SSO session (for example, on domain-joined devices with seamless SSO), administrators can hide the sign-out button entirely by setting the `SSO.EnableLogOut` service setting to `false`. See [Service Settings](configuration.md#service-settings) in the configuration reference.

### 5. Test the API

In production, the recommended way to test API access with SSO is via the JIM PowerShell module (see Step 6 below), which supports interactive browser-based authentication.

If you are configuring SSO against a non-production JIM instance and completed Step 5a above, you can also test the API interactively via the Scalar API reference:

1. Navigate to `https://your-jim-dev-url/api/reference`
2. Select an endpoint and choose the **OAuth2** security scheme
3. Log in with your identity provider
4. Try an API endpoint (e.g. `GET /api/v1/health`)

### 6. Test the PowerShell Module

If you configured PowerShell module authentication:

```powershell
# Import the module
Import-Module JIM

# Connect interactively -- opens browser for SSO
Connect-JIM -Url "https://your-jim-url"

# Verify the connection
Test-JIMConnection

# You should see:
# Connected      : True
# Url            : https://your-jim-url
# AuthMethod     : OAuth
# Status         : Healthy
# Message        : Connection successful
# TokenExpiresAt : <date/time>
```

---

## Sign-Out Troubleshooting

Sign-out uses OIDC [RP-initiated logout](https://openid.net/specs/openid-connect-rpinitiated-1_0.html): JIM clears its local session cookie, then redirects the browser to the identity provider's `end_session_endpoint` with an `id_token_hint` and a `post_logout_redirect_uri`. The identity provider ends its session and redirects the browser back to JIM.

The most common failure modes and how to fix them:

**"AADSTS50011: The reply URL specified in the request does not match..."** (Entra ID)
: The post-logout URI (`https://your-jim-url/signout-callback-oidc`) is not registered against the app. Add it to **Authentication** > **Platform configurations** > **Web** > **Redirect URIs** and save. See [Entra ID Step 1](#step-1-register-the-application).

**"Invalid post_logout_redirect_uri"** or similar (AD FS, Keycloak)
: Same root cause as above: the URI is not in the application's registered redirect URI list. For AD FS, add `https://your-jim-url/signout-callback-oidc` to the Web Application's Redirect URIs (see [AD FS Step 2](#step-2-configure-the-native-application)). For Keycloak, add it to **Valid post logout redirect URIs** on the client (see [Keycloak Step 2](#step-2-create-a-client-for-jim)).

**"Missing parameters: id_token_hint"** (Keycloak and other strict providers)
: Keycloak and other strict OIDC providers require `id_token_hint` on the end-session request per the OIDC specification. JIM persists the ID token during sign-in automatically so the middleware can include it on sign-out. If you see this error, verify your identity provider is actually issuing an ID token (the `openid` scope must be requested, which JIM does by default) and that your client configuration is not stripping it.

**Sign-out appears to succeed, but refreshing the page keeps you signed in**
: This usually means the identity provider still has an active single sign-on session and is silently re-authenticating JIM via the `prompt=none` flow (or cached browser credentials). This is working as designed for SSO. To test a full sign-out flow, use a private/incognito browser window, or sign out of the identity provider session directly.

**The sign-out button is missing from the user menu**
: The sign-out button is gated by the `SSO.EnableLogOut` service setting, which is enabled by default. See [Service Settings](configuration.md#service-settings) in the configuration reference.

---

## Security Considerations

### Token Lifetimes

Configure appropriate token lifetimes in your identity provider:

- **Access tokens**<br /> 5–60 minutes (shorter is more secure)
- **Refresh tokens**<br /> Hours to days, depending on your security requirements
- **ID tokens**<br /> Same as access tokens

### Client Secrets

- **Rotate client secrets regularly**<br /> Every 6–12 months is recommended.
- **Use separate secrets for development and production**<br /> Never share credentials across environments.
- **Never commit secrets to source control**<br /> Keep all credentials out of repositories.
- **Consider using a secrets management solution**<br /> E.g. HashiCorp Vault for secret storage.

### Redirect URIs

- **Only register exact redirect URIs**<br /> No wildcards allowed.
- **Use HTTPS in production**<br /> Secure connections are required for production deployments.
- **Remove any development/test URIs**<br /> Before going to production, clean up test configurations.

### Scopes and Permissions

- **Request only the minimum scopes needed**<br /> Follow the principle of least privilege.
- **Regularly audit which permissions are granted**<br /> Review permissions periodically for security compliance.
- **Use admin consent for organisation-wide deployments**<br /> For widespread deployments, use organization-level consent mechanisms.

### Brute-Force Protection and MFA

JIM delegates interactive password authentication entirely to your identity provider; it never sees or stores a user's password. That means credential-stuffing and password-guessing defences must be configured **at the identity provider**, not in JIM:

- **Enable brute-force/lockout protection**<br /> Entra ID (Smart Lockout), AD FS (the built-in Extranet Lockout Policy), and Keycloak (**Realm settings > Security defenses > Brute force detection**) all offer this. The bundled devcontainer Keycloak realm ships with brute-force detection enabled by default.
- **Enable multi-factor authentication where available**<br /> MFA at the identity provider protects every application behind it, including JIM, without any JIM-side configuration.

JIM's own [REST API rate limiting](../api/rate-limiting.md) throttles request volume at the application layer (see [Service Settings](../configuration/service-settings.md)); it complements, but does not replace, identity-provider-level brute-force and MFA controls for the interactive sign-in flow.
