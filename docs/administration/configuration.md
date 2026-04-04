---
title: Configuration Reference
---

# Configuration Reference

JIM is configured through environment variables set in the `.env` file alongside your Docker Compose files. The automated setup script configures these automatically; for manual setup, edit `.env` directly.

!!! tip
    A fully commented `.env.example` template is included with every release and is available in the [GitHub repository](https://github.com/TetronIO/JIM/releases).

---

## Locale

| Variable | Description                                                                                   | Default            |
|----------|-----------------------------------------------------------------------------------------------|--------------------|
| `LANG`   | Controls date/time formatting and other locale-specific behaviour. Uses standard locale codes. | `en_GB.UTF-8`     |

---

## Docker

These variables control how Docker Compose resolves and pulls JIM container images.

| Variable          | Description                                                                 | Default | Example                     |
|-------------------|-----------------------------------------------------------------------------|---------|-----------------------------|
| `DOCKER_REGISTRY` | Container registry prefix for pulling images. Leave empty for local builds. | *(empty)* | `ghcr.io/tetronio/`        |
| `JIM_VERSION`     | Release version tag. Leave empty for local builds.                          | *(empty)* | `0.8.1`                    |

---

## Database

| Variable                   | Description                                                                                                         | Default     | Example                    |
|----------------------------|---------------------------------------------------------------------------------------------------------------------|-------------|----------------------------|
| `JIM_DB_HOSTNAME`          | PostgreSQL server hostname. Use `jim.database` for the bundled container.                                           | `localhost` | `jim.database`             |
| `JIM_DB_NAME`              | Database name.                                                                                                      | `jim`       | `jim`                      |
| `JIM_DB_USERNAME`          | Database username.                                                                                                  | `jim`       | `jim`                      |
| `JIM_DB_PASSWORD`          | Database password. **Use a strong, unique value in production.**                                                    | *(none)*    | *(generate a strong password)* |
| `JIM_DB_LOG_SENSITIVE_INFO` | When `true`, includes parameter values in database query logs. **Do not enable in production.**                    | `false`     | `false`                    |
| `JIM_DB_LOG_MIN_DURATION`  | Slow query log threshold in milliseconds. Queries exceeding this duration are logged. Set to `-1` to disable, `0` to log all queries. | `1000`  | `500`                      |

---

## SSO / Authentication

These variables configure JIM's OIDC-based single sign-on. For provider-specific setup instructions, see the [SSO Setup Guide](sso-setup.md).

| Variable              | Description                                                                                      | Example                                                      |
|-----------------------|--------------------------------------------------------------------------------------------------|--------------------------------------------------------------|
| `JIM_SSO_AUTHORITY`   | OIDC authority URL for your identity provider.                                                   | `https://login.microsoftonline.com/{tenant}/v2.0`            |
| `JIM_SSO_CLIENT_ID`   | OAuth client/application ID.                                                                     | `12345678-1234-1234-1234-123456789abc`                       |
| `JIM_SSO_SECRET`      | OAuth client secret. **Keep this value secure.**                                                 | *(from your IdP)*                                            |
| `JIM_SSO_API_SCOPE`   | API scope for JWT bearer authentication (API endpoints).                                         | `api://{client-id}/access_as_user`                           |
| `JIM_SSO_VALID_ISSUERS` | Comma-separated list of trusted token issuers. Usually not needed -- JIM auto-detects the issuer from the authority URL. Only set this if your provider's issuer URL differs from its authority URL, or you need to trust multiple issuers (e.g. during a provider migration). | `https://login.microsoftonline.com/{tenant}/v2.0` |

**Authority URL examples by provider:**

| Provider       | Authority URL Format                                             |
|----------------|------------------------------------------------------------------|
| Entra ID       | `https://login.microsoftonline.com/{tenant-id}/v2.0`            |
| AD FS          | `https://{adfs-server}/adfs`                                     |
| Keycloak       | `https://{keycloak-server}/realms/{realm-name}`                  |

---

## User Identity Mapping

These variables control how JIM maps authenticated users to metaverse objects, enabling role-based access control.

| Variable                | Description                                                                                                                                  | Default               | Example                   |
|-------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|-----------------------|---------------------------|
| `JIM_SSO_CLAIM_TYPE`    | The JWT claim type containing the user's unique identifier. Use `sub` (standard OIDC subject identifier) for most providers.                | `sub`                 | `sub`                     |
| `JIM_SSO_MV_ATTRIBUTE`  | The metaverse attribute name to match the claim value against. This should be an attribute on the User object type that stores the unique identifier from your identity provider. | `Subject Identifier`  | `Subject Identifier`      |
| `JIM_SSO_INITIAL_ADMIN` | The claim value for the initial admin user (used during first-time setup). See the [SSO Setup Guide](sso-setup.md) for instructions on finding this value. | *(none)*              | *(your admin's sub claim)* |

!!! tip "Finding your admin's claim value"
    1. Log into JIM with your admin account
    2. Navigate to `/claims`
    3. Find the value of the claim type specified in `JIM_SSO_CLAIM_TYPE`

---

## Logging

| Variable           | Description                                                                        | Default         | Example          |
|--------------------|------------------------------------------------------------------------------------|-----------------|------------------|
| `JIM_LOG_LEVEL`    | Minimum log level. Valid values: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. | `Information`   | `Warning`        |
| `JIM_LOG_PATH`     | Directory path for log file output.                                                | `/tmp/jim-logs` | `/var/log/jim`   |
| `JIM_LOG_REQUESTS` | When `true`, logs all HTTP requests. Useful for debugging but generates high volume. | `false`         | `true`           |

---

## UI Theme

| Variable    | Description                                                                                                    | Default    |
|-------------|----------------------------------------------------------------------------------------------------------------|------------|
| `JIM_THEME` | Built-in colour theme for the JIM web interface. Valid values: `purple`, `black`, `blended-nav`, `future-minimal`, `navy-o5`, `navy-o6`. | `navy-o6`  |

---

## Infrastructure API Key

| Variable                    | Description                                                                                                                                                                                      | Default  |
|-----------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------|
| `JIM_INFRASTRUCTURE_API_KEY` | Creates an API key on startup for automated configuration (CI/CD). The key must start with `jim_ak_` and be at least 32 characters. Has Administrator role, expires after 24 hours, and should be deleted after initial setup. | *(none)* |

Generate a key with:

```bash
openssl rand -hex 32 | sed 's/^/jim_ak_/'
```

---

## Performance Tuning

| Variable               | Description                                                                                                                                                              | Default         |
|------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------|
| `JIM_WRITE_PARALLELISM` | Maximum concurrent PostgreSQL connections used by the parallel batch writer during bulk write operations. Higher values speed up large imports but consume more database connections. | CPU core count  |

!!! info
    The worker uses a 300-second command timeout for bulk database operations (PostgreSQL default is 30 seconds). This accommodates large imports where individual batch writes may take longer than the default timeout. No configuration is required -- it is set automatically.
