---
title: SSO Setup - AD FS
---

# SSO Setup: AD FS

Step-by-step instructions for configuring Single Sign-On with JIM using AD FS (Active Directory Federation Services) as the OIDC identity provider.

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the prerequisites, the [confidential vs public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, and the [testing steps](../sso-setup.md#testing-your-configuration) you run once configuration is complete.

## Step 1: Create an Application Group

1. Open **AD FS Management** on your AD FS server
2. Navigate to **Application Groups**
3. Right-click and select **Add Application Group**
4. Enter a name: `JIM Identity Management`
5. Select **Web browser accessing a web application**
6. Click **Next**

## Step 2: Configure the Native Application

1. Note the **Client Identifier** (auto-generated GUID)
2. Add the **Redirect URI**: `https://your-jim-url/signin-oidc`
3. Add a second **Redirect URI** for the sign-out callback: `https://your-jim-url/signout-callback-oidc`. AD FS validates the `post_logout_redirect_uri` parameter against the same Redirect URIs list as sign-in, so this entry is required for sign-out to work.
4. Click **Next**

## Step 3: Configure Access Control

1. Select an access control policy (e.g. **Permit everyone**)
2. Click **Next**

## Step 4: Configure Application Permissions

1. Select **openid** and **profile** scopes
2. Click **Next** and then **Close**

## Step 4a: Configure PowerShell Module Authentication (Optional but recommended)

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

## Step 5: Create the Web API

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

## Step 6: Generate a Client Secret

1. In your Application Group properties, select the **Native application**
2. Click **Edit**
3. Under **Secrets**, click **Generate**
4. **Copy the secret value** -- store it securely

## Step 7: Configure JIM Environment Variables

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

## AD FS Claim Rules

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

## AD FS Troubleshooting

- **Token signing certificate**: Ensure your AD FS token signing certificate is trusted by the JIM server
- **Claim rules**: AD FS may need claim rules to emit standard OIDC claims (see above)
- **CORS**: If JIM and AD FS are on different domains, configure CORS in AD FS

## Next steps

With the configuration in place, [test your configuration](../sso-setup.md#testing-your-configuration) to verify sign-in, claims, sign-out, and API access. If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
