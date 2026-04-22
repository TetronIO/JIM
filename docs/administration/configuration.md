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
| `JIM_VERSION`     | Release version tag. Leave empty for local builds.                          | *(empty)* | `0.10.0`                   |

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

## Service Settings

Service settings are stored in the database and managed through the admin UI at **Admin > Service Settings** (`/admin/settings`). They can be changed at runtime by users with the Administrator role; most take effect immediately, without restarting the JIM containers.

This differs from environment variables, which are applied at process startup and tend to be infrastructure-level concerns (database connection, OIDC authority, log paths). Service settings are the day-to-day operational knobs.

The settings listed below are the ones most commonly adjusted; the full list is discoverable in the admin UI.

| Key                          | Display name              | Category        | Description                                                                                                                                                                                                                                                      | Default       |
|------------------------------|---------------------------|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------|
| `Instance.Name`              | Service name              | Instance        | A friendly, editable name for this JIM instance. Appears in the sidebar, browser tab title, and footer so operators can distinguish multiple JIM deployments at a glance (for example, `JIM Production`, `JIM DR`).                                             | *(empty)*     |
| `Instance.Id`                | Service ID                | Instance        | A stable GUID that uniquely identifies this JIM instance. Generated exactly once on first startup and never changes thereafter. Useful for tooling, logs, and telemetry correlation. Read-only.                                                                  | *(generated)* |
| `SSO.EnableLogOut`           | SSO enable log-out        | SSO             | Controls whether the sign-out button is shown in the JIM user menu. Set to `false` for deployments where users cannot realistically sign out of their enterprise-managed SSO session, for example on domain-joined devices with seamless SSO. | `true`        |
| `Sync.PartitionValidationMode` | Run profile partition validation | Synchronisation | Controls how JIM behaves when a run profile is executed for a Connected System that supports partitions but has none selected. `Error` blocks execution; `Warning` allows execution but logs a warning.                                                         | `Error`       |
| `History.RetentionPeriod`    | History retention period  | History         | The duration for which activity and audit history is retained. Format: `d.hh:mm:ss`. Longer periods increase database size and may affect performance.                                                                                                          | `90.00:00:00` (90 days) |

!!! tip "Editing service settings"
    Navigate to **Admin > Service Settings**, use the filter and search box to locate the setting by key or display name, and click the edit icon. Changes are audited: the settings page shows who last modified each value and when.

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
