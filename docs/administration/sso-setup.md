---
title: Single Sign-On Setup
---

# Single Sign-On Setup

JIM requires an OIDC-compliant identity provider for authentication. This page is the high-level guide to configuring Single Sign-On (SSO): what JIM needs from your identity provider, how the client model works, and how to test and troubleshoot the result. The provider-specific, step-by-step setup lives on its own page per provider (see [Choose your identity provider](#choose-your-identity-provider) below).

!!! info "Before you begin"
    Ensure you have:

    - JIM deployed and accessible (see [Deployment Guide](deployment.md))
    - Administrative access to your identity provider
    - The URL where JIM is hosted (e.g. `https://jim.example.com`)

    JIM serves both the web interface and API from a single application. The API is available at `/api/` on the same host.

## Choose your identity provider

JIM works with any OIDC-compliant identity provider. Pick the guide that matches yours for exact, click-by-click setup instructions:

<div class="grid cards" markdown>

-   :material-microsoft:{ .lg .middle } **[Microsoft Entra ID](sso-setup/microsoft-entra-id.md)**

    ---

    App registration, client secret, exposed API scope, and PowerShell public-client platform, all on a single registration.

-   :material-microsoft-windows:{ .lg .middle } **[AD FS](sso-setup/ad-fs.md)**

    ---

    Application Group with a web application, native (PowerShell) application, and Web API, plus the claim rules to emit standard OIDC claims.

-   :material-account-key:{ .lg .middle } **[Keycloak](sso-setup/keycloak.md)**

    ---

    Realm, confidential web client, API client scope, and a separate public client for the PowerShell module.

</div>

Using a different provider? The concepts on this page (a [confidential client](#confidential-vs-public-clients) for the web application, an optional [public client](#confidential-vs-public-clients) for interactive tooling, an API scope for token validation, and the `JIM_SSO_*` environment variables) apply to any OIDC provider. Follow the closest guide above as a template and substitute your provider's terminology.

Whichever provider you use, JIM is configured through a small set of `JIM_SSO_*` environment variables; the per-provider guides show the exact values to set. For the full list and defaults, see the [Configuration Reference](configuration.md).

---

## Confidential vs Public Clients

JIM uses two kinds of OAuth 2.0 clients, and depending on your identity provider they may be the same registration or two separate ones. Understanding which is which makes the per-provider steps easier to follow.

| Client | Used by | Authentication | Configured via |
|--------|---------|----------------|----------------|
| **Confidential** | The JIM web application (Blazor UI) | A stored client secret sent directly from the backend to the IdP's token endpoint. Never exposed to the browser. | `JIM_SSO_CLIENT_ID` and `JIM_SSO_SECRET` |
| **Public** | Interactive clients that run on the user's machine (PowerShell module, future CLI tools) | PKCE with a loopback redirect URI (`http://localhost:8400/callback/`). No secret; the IdP relies on PKCE to bind the authorisation response to the original client. | `JIM_SSO_PUBLIC_CLIENT_ID` (falls back to `JIM_SSO_CLIENT_ID` when unset) |

**When are these the same registration?**

- **Microsoft Entra ID**: One app registration can have both a Web platform (for the confidential flow) and a Mobile/Desktop platform (for the public flow). In this case, leave `JIM_SSO_PUBLIC_CLIENT_ID` unset; the PowerShell module uses the same Application (client) ID as the web application.

**When must they be different?**

- **AD FS**: A Server application (confidential) and a Native application (public) are distinct applications with their own Client Identifiers, even inside a single Application Group. Create both and set `JIM_SSO_PUBLIC_CLIENT_ID` to the Native application's identifier.
- **Keycloak**: A single Keycloak client cannot be both confidential and public. You must create two clients (e.g. `jim` and `jim-powershell`) and set `JIM_SSO_PUBLIC_CLIENT_ID` to the public one.
- **Any IdP where your security policy forbids adding loopback redirects to a confidential client**: create a dedicated public client and point `JIM_SSO_PUBLIC_CLIENT_ID` at it.

**What does JIM do with these values?**

The JIM server exposes `/api/v1/auth/config`, an unauthenticated discovery endpoint that interactive clients call to learn how to authenticate. It returns:

- `authority`: the OIDC authority URL (`JIM_SSO_PUBLIC_AUTHORITY` if set, else `JIM_SSO_AUTHORITY`)
- `clientId`: the client ID for public/PKCE flows (`JIM_SSO_PUBLIC_CLIENT_ID` if set, else `JIM_SSO_CLIENT_ID`)
- `scopes`: the OAuth scopes to request: `openid`, `profile`, `offline_access`, and (when set) `JIM_SSO_API_SCOPE`

Backend token validation (the JWT bearer middleware that protects `/api/**` endpoints) always uses `JIM_SSO_AUTHORITY` for issuer and JWKS, and `JIM_SSO_API_SCOPE` for the audience, regardless of which public client issued the token. As long as the public and confidential clients belong to the same realm/tenant and both request the same API scope, tokens from either are valid.

!!! info "Why `offline_access`?"
    JIM requests the `offline_access` scope so the identity provider issues a **refresh token**. The PowerShell module uses this for two things: silent in-session token renewal (so long-running sessions don't expire mid-task), and optional cross-session token persistence (so opening a new terminal doesn't require re-authenticating in the browser). The refresh token is the only credential persisted, and it is stored in the operating system's credential store (Credential Manager on Windows, login Keychain on macOS, libsecret on Linux); never in plain text. Your public client must be permitted to receive `offline_access`. Most identity providers grant it to public clients by default; the per-provider guides note where it must be enabled explicitly.

---

## Testing Your Configuration

Once you have completed the setup for your provider, use these steps to verify sign-in, claims, sign-out, and API access.

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

In production, the recommended way to test API access with SSO is via the JIM PowerShell module (see step 6 below), which supports interactive browser-based authentication.

If you are configuring SSO against a non-production JIM instance and configured a Scalar single-page-application redirect URI on your identity provider (see [Microsoft Entra ID Step 5a](sso-setup/microsoft-entra-id.md#step-5a-configure-scalar-api-reference-authentication-development-only-optional) for an example), you can also test the API interactively via the Scalar API reference:

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
: The post-logout URI (`https://your-jim-url/signout-callback-oidc`) is not registered against the app. Add it to **Authentication** > **Platform configurations** > **Web** > **Redirect URIs** and save. See [Entra ID Step 1](sso-setup/microsoft-entra-id.md#step-1-register-the-application).

**"Invalid post_logout_redirect_uri"** or similar (AD FS, Keycloak)
: Same root cause as above: the URI is not in the application's registered redirect URI list. For AD FS, add `https://your-jim-url/signout-callback-oidc` to the Server application's Redirect URIs (see [AD FS Step 2](sso-setup/ad-fs.md#step-2-configure-the-server-application)). For Keycloak, add it to **Valid post logout redirect URIs** on the client (see [Keycloak Step 2](sso-setup/keycloak.md#step-2-create-a-client-for-jim)).

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

JIM delegates interactive authentication entirely to your identity provider and never sees or stores user credentials, whichever kind your provider is configured to require, i.e. Kerberos, passwords, smart cards, other certificate-based authentication types, FIDO2 passkeys, etc. Defences against credential attacks therefore belong **at the identity provider**, not in JIM:

- **Where password authentication is in use, we recommend you enable brute-force/lockout protection**<br /> Entra ID (Smart Lockout), AD FS (the built-in Extranet Lockout Policy), and Keycloak (**Realm settings > Security defences > Brute force detection**) all offer this. The bundled devcontainer Keycloak realm ships with brute-force detection enabled by default.
- **Enable multi-factor authentication where available**<br /> MFA at the identity provider protects every application behind it, including JIM, without any JIM-side configuration.
- **Prefer phishing-resistant methods where your provider supports them**<br /> Kerberos, certificate-based authentication, and FIDO2 passkeys remove the guessable secret altogether, protecting users against credential-stuffing and password-guessing.

JIM's own [REST API rate limiting](../api/rate-limiting.md) throttles request volume at the application layer (see [Service Settings](../configuration/service-settings.md)); it complements, but does not replace, identity-provider-level credential protections for the interactive sign-in flow.
