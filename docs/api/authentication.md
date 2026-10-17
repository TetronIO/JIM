---
title: API Authentication
description: "Authenticate to JIM's REST API with an OpenID Connect bearer token or an API key, and when to use each."
type: reference
audience: automation-author
---

# API Authentication

All API requests must be authenticated. JIM supports two kinds of credential: **JWT access tokens** (via OIDC) and **API keys**. Both are sent the same way, as a bearer token in the `Authorization` header:

```http
Authorization: Bearer <credential>
```

JIM tells the two apart by the credential itself: a value beginning `jim_ak_` is an API key, and anything else is treated as an SSO access token.

## JWT Bearer Tokens

JWT Bearer authentication is for people: an administrator signed in through your organisation's single sign-on (SSO), whether through the PowerShell module or an application acting on their behalf. JIM gives a token the roles of the Metaverse Object it identifies, matched on the claim set by `JIM_SSO_CLAIM_TYPE`, so the token must represent a person with a JIM identity. A token issued to an identity provider service account (the client credentials grant) identifies no Metaverse Object and gets no roles, so JIM refuses it everywhere that needs a role. Scripts and services use an [API key](#api-keys) instead.

### Prerequisites

- OIDC/SSO must be configured on your JIM instance (see the [SSO Setup Guide](../administration/sso-setup.md))
- The `JIM_SSO_API_SCOPE` environment variable must be configured to define the required API scope

### Usage

Obtain a token for a signed-in person from your identity provider, then include it in the `Authorization` header:

```bash
curl -H "Authorization: Bearer <token>" \
  https://jim.example.com/api/v1/synchronisation/connected-systems
```

Tokens are validated against the configured OIDC provider. Standard JWT claims (expiry, audience, issuer) are enforced; the audience required is described under [Confidential vs public clients](../administration/sso-setup.md#confidential-vs-public-clients).

## API Keys

API keys are suitable for service-to-service integrations, scripts, scheduled tasks, and automated workflows, particularly in environments where interactive OIDC authentication is not practical.

### Creating an API Key

API keys can be created via:

- The **JIM web UI**: navigate to the API Keys management page
- The **PowerShell module**: use the `New-JIMApiKey` cmdlet

All API keys are prefixed with `jim_ak_` for easy identification.

### Usage

Send the API key as a bearer token in the `Authorization` header:

```bash
curl -H "Authorization: Bearer jim_ak_xxxxxxxxxxxx" \
  https://jim.example.com/api/v1/synchronisation/connected-systems
```

The `Authorization` header is the only place JIM reads an API key from. A key sent in any other header (for example `X-Api-Key`) is ignored, and the request is treated as unauthenticated.

## Examples

### Listing Connected Systems with a Bearer Token

```bash
curl -H "Authorization: Bearer eyJhbGciOi..." \
  https://jim.example.com/api/v1/synchronisation/connected-systems
```

### Listing Connected Systems with an API Key

```bash
curl -H "Authorization: Bearer jim_ak_xxxxxxxxxxxx" \
  https://jim.example.com/api/v1/synchronisation/connected-systems
```

### Using PowerShell

```powershell
# Connect with interactive SSO
Connect-JIM -Url "https://jim.example.com"

# Or connect with an API key
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"

# Make API calls via the module
Get-JIMConnectedSystem
```

## Authentication Failures

A request that is not authenticated receives `401 Unauthorized` with a `WWW-Authenticate` header naming the Bearer scheme, as RFC 6750 describes:

| Request | `WWW-Authenticate` |
|---|---|
| No credential | `Bearer realm="JIM"` |
| A credential JIM rejected (unknown, disabled or expired API key, or an invalid access token) | `Bearer realm="JIM", error="invalid_token"` |

The response does not say why a credential was rejected. Each failed API key or access token attempt is recorded in JIM's [security audit log](../administration/security-audit-events.md), with the reason, which administrators can review.

## Security Recommendations

- **Rotate API keys regularly.** Treat API keys as credentials; if a key is compromised, revoke it immediately via the web UI or PowerShell module.
- **Use the principle of least privilege.** Create separate API keys for different integrations so that keys can be revoked independently.
- **Prefer OIDC where possible.** JWT Bearer tokens benefit from centralised authentication, token expiry, and your organisation's existing access policies.
- **Always use HTTPS.** JIM enforces TLS 1.2 or higher for all API traffic. Never transmit tokens or API keys over unencrypted connections.
- **Do not embed API keys in source code.** Store keys in environment variables, secrets managers, or other secure configuration stores.
- **Monitor API key usage.** Review API access logs and revoke unused keys.

## Further Reading

- [API Reference](index.md): resource overview, conventions, and endpoint documentation
- [PowerShell Module](../powershell/index.md): scripting and automation via PowerShell
