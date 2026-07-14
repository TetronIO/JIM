---
title: SSO Setup - AD FS
---

# SSO Setup: AD FS

Step-by-step instructions for configuring Single Sign-On with JIM using AD FS (Active Directory Federation Services) as the OIDC identity provider. Requires **AD FS on Windows Server 2019 or later** (earlier versions do not support PKCE, which the PowerShell module relies on).

!!! info "Read the overview first"
    This page is one of the provider-specific guides linked from the [SSO Setup overview](../sso-setup.md). The overview covers the prerequisites, the [confidential vs public client](../sso-setup.md#confidential-vs-public-clients) model these steps rely on, and the [testing steps](../sso-setup.md#testing-your-configuration) you run once configuration is complete.

!!! note "Two client types, two AD FS applications"
    The JIM web application is a **confidential client**: it runs on a server and authenticates to AD FS with a shared secret, so it is registered as an AD FS **Server application**. The optional PowerShell module is a **public client**: it runs on an administrator's machine and uses PKCE with a loopback redirect instead of a secret, so it is registered as a separate AD FS **Native application**. Both live in the same Application Group. Steps 1--6 configure the confidential web application; Step 7 adds the public client for the PowerShell module.

## Step 1: Create an Application Group

1. Open **AD FS Management** on your AD FS server
2. Right-click **Application Groups** and select **Add Application Group**
3. Enter a name: `JIM Identity Management`
4. Under **Client-Server applications**, select **Server application accessing a web API**
5. Click **Next**

This template creates a **Server application** (the confidential web client) and a **Web API** (the resource used for JWT Bearer token validation) in one group.

## Step 2: Configure the Server Application

1. Note the **Client Identifier** (auto-generated GUID) -- this becomes `JIM_SSO_CLIENT_ID`
2. Add the **Redirect URI**: `https://your-jim-url/signin-oidc`, then click **Add**
3. Add a second **Redirect URI** for the sign-out callback: `https://your-jim-url/signout-callback-oidc`, then click **Add**. AD FS validates the `post_logout_redirect_uri` parameter against the same Redirect URIs list as sign-in, so this entry is required for sign-out to work.
4. Click **Next**

## Step 3: Configure Application Credentials

1. Check **Generate a shared secret**
2. **Copy the secret value** -- store it securely; this becomes `JIM_SSO_SECRET`
3. Click **Next**

!!! note
    The shared secret belongs to the confidential Server application only. The Native application you add in Step 7 for the PowerShell module is a public client and does **not** have a secret; it uses PKCE instead.

## Step 4: Configure the Web API

1. Set the **Identifier**: `api://{client-identifier}` (use the Client Identifier from Step 2), then click **Add**. This becomes `JIM_SSO_API_SCOPE`.
2. Click **Next**

## Step 5: Apply an Access Control Policy

1. Select an access control policy (e.g. **Permit everyone**)
2. Click **Next**

## Step 6: Configure Application Permissions

1. Under **Permitted scopes**, select:
    - `openid`
    - `profile`
    - `email`
    - `offline_access` (required for refresh-token issuance; enables PowerShell silent renewal and token persistence)
2. Click **Next**, then **Next** again on the Summary screen, and **Close**

The confidential web application is now configured. If you do not need interactive PowerShell authentication, skip to Step 8.

## Step 7: Configure PowerShell Module Authentication (Optional but recommended)

Configuring the PowerShell module now means administrators and automation scripts can connect to JIM interactively with their SSO account, without needing to issue or manage API keys. You can skip this step if you don't plan to use the PowerShell module.

The JIM PowerShell module uses OAuth 2.0 with PKCE for interactive browser-based authentication. This is a **public client**, so it must be a separate **Native application** (a native/public application in AD FS cannot hold a shared secret). Add it to the same Application Group:

1. Right-click your Application Group and select **Properties**
2. Click **Add application**
3. Select **Native application**, then click **Next**
4. Note the **Client Identifier** (auto-generated GUID) -- this becomes `JIM_SSO_PUBLIC_CLIENT_ID`
5. Add the **Redirect URI**: `http://localhost:8400/callback/`, then click **Add**
6. Click **Next**, then **Close**
7. Grant the native application access to the Web API: back in the Application Group properties, select the **Web API**, click **Edit**, and on the **Client Permissions** tab ensure the native application is permitted the `openid`, `profile`, `email` and `offline_access` scopes

!!! note
    The PowerShell module uses loopback redirect URIs per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). AD FS 2019 and later support PKCE for native applications, which the module uses for security. If port 8400 is busy, the module will try ports 8401--8409; add the corresponding redirect URIs (e.g. `http://localhost:8401/callback/` through `http://localhost:8409/callback/`) if port conflicts are likely in your environment.

## Step 8: Configure JIM Environment Variables

```bash
# AD FS Configuration
JIM_SSO_AUTHORITY=https://{your-adfs-server}/adfs
JIM_SSO_CLIENT_ID={your-server-application-client-identifier}
JIM_SSO_SECRET={your-shared-secret}
JIM_SSO_API_SCOPE=api://{your-server-application-client-identifier}

# Client ID of the Native application created in Step 7 for the PowerShell
# module. Required for interactive (SSO) PowerShell authentication; omit if
# you only plan to use API keys.
JIM_SSO_PUBLIC_CLIENT_ID={your-native-application-client-identifier}

# User identity mapping
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN={your-admin-sub-value}
```

**Example:**

```bash
JIM_SSO_AUTHORITY=https://adfs.example.com/adfs
JIM_SSO_CLIENT_ID=e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_SECRET=generatedSecretValue123
JIM_SSO_API_SCOPE=api://e1234567-89ab-cdef-0123-456789abcdef
JIM_SSO_PUBLIC_CLIENT_ID=a9876543-21fe-dcba-3210-fedcba987654
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
- **PKCE / PowerShell sign-in fails**: Confirm AD FS is Windows Server 2019 or later; earlier versions do not support PKCE and cannot authenticate the PowerShell module's public client

## Next steps

With the configuration in place, [test your configuration](../sso-setup.md#testing-your-configuration) to verify sign-in, claims, sign-out, and API access. If sign-out fails, see [Sign-Out Troubleshooting](../sso-setup.md#sign-out-troubleshooting).
