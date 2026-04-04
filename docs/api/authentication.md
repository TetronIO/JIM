---
title: API Authentication
---

# API Authentication

All API requests must be authenticated. JIM supports two authentication methods: **JWT Bearer tokens** (via OIDC) and **API keys**.

## JWT Bearer Tokens

JWT Bearer authentication is suitable for user-driven integrations and applications that participate in your organisation's single sign-on (SSO) infrastructure.

### Prerequisites

- OIDC/SSO must be configured on your JIM instance (see the [SSO Setup Guide](../administration/sso-setup.md))
- The `JIM_SSO_API_SCOPE` environment variable must be configured to define the required API scope

### Usage

Obtain a token via your identity provider's OIDC token endpoint, then include it in the `Authorization` header:

```bash
curl -H "Authorization: Bearer <token>" \
  https://jim.example.com/api/v1/connected-systems
```

Tokens are validated against the configured OIDC provider. Standard JWT claims (expiry, audience, issuer) are enforced.

## API Keys

API keys are suitable for service-to-service integrations, scripts, scheduled tasks, and automated workflows — particularly in environments where interactive OIDC authentication is not practical.

### Creating an API Key

API keys can be created via:

- The **JIM web UI** — navigate to the API Keys management page
- The **PowerShell module** — use the `New-JIMApiKey` cmdlet

All API keys are prefixed with `jim_` for easy identification.

### Usage

Include the API key in the `X-Api-Key` header:

```bash
curl -H "X-Api-Key: jim_xxxxxxxxxxxx" \
  https://jim.example.com/api/v1/connected-systems
```

## Examples

### Listing Connected Systems with a Bearer Token

```bash
curl -H "Authorization: Bearer eyJhbGciOi..." \
  https://jim.example.com/api/v1/connected-systems
```

### Listing Connected Systems with an API Key

```bash
curl -H "X-Api-Key: jim_xxxxxxxxxxxx" \
  https://jim.example.com/api/v1/connected-systems
```

### Using PowerShell

```powershell
# Connect with interactive SSO
Connect-JIM -Url "https://jim.example.com"

# Or connect with an API key
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

# Make API calls via the module
Get-JIMConnectedSystem
```

## Security Recommendations

- **Rotate API keys regularly.** Treat API keys as credentials — if a key is compromised, revoke it immediately via the web UI or PowerShell module.
- **Use the principle of least privilege.** Create separate API keys for different integrations so that keys can be revoked independently.
- **Prefer OIDC where possible.** JWT Bearer tokens benefit from centralised authentication, token expiry, and your organisation's existing access policies.
- **Always use HTTPS.** JIM enforces TLS 1.2 or higher for all API traffic. Never transmit tokens or API keys over unencrypted connections.
- **Do not embed API keys in source code.** Store keys in environment variables, secrets managers, or other secure configuration stores.
- **Monitor API key usage.** Review API access logs and revoke unused keys.

## Further Reading

- [API Overview](index.md) — general API information and versioning
- [API Endpoints](endpoints.md) — endpoint reference documentation
- [PowerShell Module](../powershell/index.md) — scripting and automation via PowerShell
