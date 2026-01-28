# JIM SSO Setup Guide

> Step-by-step instructions for configuring Single Sign-On with JIM

This guide covers setting up JIM with three identity providers:
- [Microsoft Entra ID (Azure AD)](#microsoft-entra-id-azure-ad)
- [AD FS (Active Directory Federation Services)](#ad-fs-active-directory-federation-services)
- [Keycloak](#keycloak)

---

## Prerequisites

Before starting, ensure you have:
- JIM deployed and accessible
- Administrative access to your identity provider
- The URL where JIM is hosted

**JIM URL you'll need:**
- JIM application: `https://your-jim-url` (e.g., `https://jim.example.com`)

> **Note**: JIM serves both the web interface and API from a single application. The API is available at `/api/` on the same host (e.g., `https://jim.example.com/api/`).

---

## Microsoft Entra ID (Azure AD)

### Step 1: Register the Application

1. Sign in to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Microsoft Entra ID** > **App registrations**
3. Click **New registration**
4. Configure the registration:
   - **Name**: `JIM Identity Management`
   - **Supported account types**: Select based on your requirements:
     - *Single tenant*: Only users in your organisation
     - *Multitenant*: Users from any Azure AD directory
   - **Redirect URI**:
     - Platform: **Web**
     - URI: `https://your-jim-url/signin-oidc`
5. Click **Register**

### Step 2: Note the Application Details

After registration, note these values from the **Overview** page:
- **Application (client) ID**: e.g., `12345678-1234-1234-1234-123456789abc`
- **Directory (tenant) ID**: e.g., `87654321-4321-4321-4321-cba987654321`

### Step 3: Create a Client Secret

1. Go to **Certificates & secrets**
2. Click **New client secret**
3. Add a description (e.g., `JIM Web Client`)
4. Select an expiry period
5. Click **Add**
6. **Copy the secret value immediately** - it won't be shown again

### Step 4: Configure API Permissions

1. Go to **API permissions**
2. Verify `User.Read` (Delegated) is present - this is added by default and provides the OpenID Connect scopes needed for authentication

> **Note**: The standard OIDC scopes (`openid`, `profile`, `email`, `offline_access`) are requested at runtime and don't need to be added as Graph API permissions.

### Step 4a: Configure PowerShell Module Authentication (Optional)

To enable interactive browser-based authentication for the JIM PowerShell module:

1. Go to **Authentication**
2. Click **Add a platform**
3. Select **Mobile and desktop applications**
4. Check the suggested redirect URI: `https://login.microsoftonline.com/common/oauth2/nativeclient`
5. In **Custom redirect URIs**, add: `http://localhost`
6. Click **Configure**
7. Scroll down to **Advanced settings**
8. Set **Allow public client flows** to **Yes**
9. Click **Save**

> **Note**: The `http://localhost` redirect URI enables RFC 8252 loopback authentication. Entra ID will accept any port on localhost (e.g., `http://localhost:8400/callback/`).

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

### Step 5a: Configure Swagger UI Authentication (Optional)

To enable OAuth authentication in the Swagger UI:

1. Go to **Authentication**
2. Click **Add Redirect URI**
3. Select **Single-page application**
4. Add redirect URI: `https://your-jim-url/api/swagger/oauth2-redirect.html`
5. Click **Configure**

### Step 6: Configure JIM Environment Variables

Add these to your `.env` file:

```bash
# Microsoft Entra ID Configuration
SSO_AUTHORITY=https://login.microsoftonline.com/{your-tenant-id}/v2.0
SSO_CLIENT_ID={your-application-client-id}
SSO_SECRET={your-client-secret}
SSO_API_SCOPE=api://{your-application-client-id}/access_as_user

# User identity mapping
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=sub
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=Subject Identifier
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE={your-admin-sub-value}
```

**Example with real values:**
```bash
SSO_AUTHORITY=https://login.microsoftonline.com/87654321-4321-4321-4321-cba987654321/v2.0
SSO_CLIENT_ID=12345678-1234-1234-1234-123456789abc
SSO_SECRET=abc123~secretvalue
SSO_API_SCOPE=api://12345678-1234-1234-1234-123456789abc/access_as_user
```

### Step 7: Find Your Admin User's Subject Identifier

1. Start JIM with the configuration above
2. Log in with your admin account
3. Navigate to `/claims` in the web interface
4. Find the `sub` claim value
5. Update `SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE` with this value

---

## AD FS (Active Directory Federation Services)

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
3. Click **Next**

### Step 3: Configure Access Control

1. Select an access control policy (e.g., **Permit everyone**)
2. Click **Next**

### Step 4: Configure Application Permissions

1. Select **openid** and **profile** scopes
2. Click **Next** and then **Close**

### Step 4a: Configure PowerShell Module Authentication (Optional)

The JIM PowerShell module uses OAuth 2.0 with PKCE for interactive browser-based authentication. AD FS supports this through native applications.

1. Right-click your Application Group and select **Properties**
2. Click **Add application**
3. Select **Native application**
4. Click **Next**
5. Note the **Client Identifier** (or use the same ID as the web application)
6. Add the **Redirect URI**: `http://127.0.0.1`
7. Click **Next** and then **Close**

> **Note**: The PowerShell module uses loopback redirect URIs (`http://127.0.0.1:PORT/callback/...`) which are handled specially by OAuth 2.0 per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). AD FS native applications support PKCE, which the PowerShell module uses for security.

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
10. Click **Next** and then **Close**

### Step 6: Generate a Client Secret

1. In your Application Group properties, select the **Native application**
2. Click **Edit**
3. Under **Secrets**, click **Generate**
4. **Copy the secret value** - store it securely

### Step 7: Configure JIM Environment Variables

```bash
# AD FS Configuration
SSO_AUTHORITY=https://{your-adfs-server}/adfs
SSO_CLIENT_ID={your-client-identifier}
SSO_SECRET={your-client-secret}
SSO_API_SCOPE=api://{your-client-identifier}

# User identity mapping
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=sub
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=Subject Identifier
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE={your-admin-sub-value}
```

**Example:**
```bash
SSO_AUTHORITY=https://adfs.example.com/adfs
SSO_CLIENT_ID=e1234567-89ab-cdef-0123-456789abcdef
SSO_SECRET=generatedSecretValue123
SSO_API_SCOPE=api://e1234567-89ab-cdef-0123-456789abcdef
```

### AD FS Troubleshooting

**Common issues:**

1. **Token signing certificate**: Ensure your AD FS token signing certificate is trusted by the JIM server
2. **Claim rules**: AD FS may need claim rules to emit standard OIDC claims (`sub`, `email`, `name`)
3. **CORS**: If JIM and AD FS are on different domains, configure CORS in AD FS

**Adding claim rules for standard OIDC claims:**

In your Web API's **Issuance Transform Rules**, add rules to map AD claims to OIDC claims:

```
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

---

## Keycloak

### Step 1: Create a Realm (if needed)

1. Log in to the Keycloak Admin Console
2. Hover over the realm dropdown and click **Create Realm**
3. Enter a **Realm name** (e.g., `jim`)
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
   - **Authorization**: OFF
   - **Authentication flow**: Check **Standard flow** (Authorization Code)
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

### Step 4: Create a Service Account Client (Optional - for API-only access)

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

### Step 6a: Configure PowerShell Module Authentication (Optional)

The JIM PowerShell module uses OAuth 2.0 with PKCE for interactive browser-based authentication. This requires creating a public client in Keycloak.

1. Navigate to **Clients**
2. Click **Create client**
3. Configure the client:
   - **Client type**: OpenID Connect
   - **Client ID**: `jim-powershell`
4. Click **Next**
5. Configure capability:
   - **Client authentication**: OFF (this makes it a public client)
   - **Authorization**: OFF
   - **Authentication flow**: Check **Standard flow** (Authorization Code)
6. Click **Next**
7. Configure login settings:
   - **Root URL**: Leave empty
   - **Valid redirect URIs**: `http://127.0.0.1/*`
   - **Web origins**: `+` (allows all origins from redirect URIs)
8. Click **Save**
9. Go to the **Client scopes** tab
10. Click **Add client scope**
11. Select `jim-api` and add as **Optional**

> **Note**: The PowerShell module uses loopback redirect URIs (`http://127.0.0.1:PORT/callback/...`) which are handled specially by OAuth 2.0 per [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252). The `/*` wildcard allows the dynamic port and callback path used by the module.

### Step 7: Configure JIM Environment Variables

```bash
# Keycloak Configuration
SSO_AUTHORITY=https://{your-keycloak-server}/realms/{realm-name}
SSO_CLIENT_ID=jim
SSO_SECRET={your-client-secret}
SSO_API_SCOPE=jim-api

# User identity mapping
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=sub
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=Subject Identifier
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE={your-admin-sub-value}
```

**Example:**
```bash
SSO_AUTHORITY=https://keycloak.example.com/realms/jim
SSO_CLIENT_ID=jim
SSO_SECRET=AbCdEfGhIjKlMnOpQrStUvWxYz123456
SSO_API_SCOPE=jim-api
```

### Keycloak Troubleshooting

**Common issues:**

1. **Realm name**: Ensure the realm name in `SSO_AUTHORITY` matches exactly (case-sensitive)
2. **Client scopes**: Verify `openid`, `profile`, and `email` scopes are included
3. **Token mapper**: If `sub` claim is missing, add a mapper in Client Scopes > openid > Mappers

**Checking claim values:**

1. Navigate to **Clients** > **jim** > **Client scopes**
2. Click **Evaluate**
3. Select a user and click **Evaluate**
4. Check the **Generated access token** to see claim values

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
   - `sub` - Your unique identifier
   - `name` - Your display name
   - `email` - Your email address

### 4. Test the API (Swagger)

1. Navigate to `https://your-jim-url/api/swagger`
2. Click **Authorize**
3. Log in with your identity provider
4. Try an API endpoint (e.g., GET /api/v1/health)

### 5. Test the PowerShell Module

If you configured PowerShell module authentication:

```powershell
# Import the module
Import-Module ./JIM.PowerShell/JIM -Force

# Connect interactively - opens browser for SSO
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

## Security Considerations

### Token Lifetimes

Configure appropriate token lifetimes in your identity provider:
- **Access tokens**: 5-60 minutes (shorter is more secure)
- **Refresh tokens**: Hours to days, depending on your security requirements
- **ID tokens**: Same as access tokens

### Client Secrets

- Rotate client secrets regularly (every 6-12 months)
- Use separate secrets for development and production
- Never commit secrets to source control
- Consider using Azure Key Vault, HashiCorp Vault, or similar for secret management

### Redirect URIs

- Only register exact redirect URIs (no wildcards)
- Use HTTPS in production
- Remove any development/test URIs before going to production

### Scopes and Permissions

- Request only the minimum scopes needed
- Regularly audit which permissions are granted
- Use admin consent for organisation-wide deployments

---

## Environment Variable Reference

| Variable | Description | Example |
|----------|-------------|---------|
| `SSO_AUTHORITY` | OIDC authority URL | `https://login.microsoftonline.com/{tenant}/v2.0` |
| `SSO_CLIENT_ID` | OAuth client/application ID | `12345678-1234-1234-1234-123456789abc` |
| `SSO_SECRET` | OAuth client secret | `abc123~secretvalue` |
| `SSO_API_SCOPE` | API scope for JWT validation | `api://{client-id}/access_as_user` |
| `SSO_VALID_ISSUERS` | Comma-separated valid token issuers (optional) | Auto-detected for Entra ID |
| `SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE` | JWT claim for user identification | `sub` |
| `SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME` | Metaverse attribute to match | `Subject Identifier` |
| `SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE` | Initial admin's claim value | Varies by user |

---

## Getting Help

- **JIM Documentation**: See other files in the `docs/` folder
- **Issues**: Report bugs at [GitHub Issues](https://github.com/TetronIO/JIM/issues)
- **Identity Provider Documentation**:
  - [Microsoft Entra ID](https://learn.microsoft.com/en-us/entra/identity-platform/)
  - [AD FS](https://learn.microsoft.com/en-us/windows-server/identity/ad-fs/ad-fs-overview)
  - [Keycloak](https://www.keycloak.org/documentation)
