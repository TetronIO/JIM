---
title: Configuration Reference
---

# Configuration Reference

JIM is configured through environment variables set in the `.env` file alongside your Docker Compose files, or on Podman in `jim-config.yaml` (see [Podman](#podman)). The automated setup script configures these automatically; for manual setup, edit the file directly. The settings and their names are the same on both runtimes.

!!! tip
    A fully commented `.env.example` template is included with every release and is available in the [GitHub repository](https://github.com/TetronIO/JIM/releases).

---

## Podman

On Podman, JIM reads its settings from `/opt/jim/jim-config.yaml`, a Kubernetes-style ConfigMap holding the same settings, with the same names, as `.env`; each one is on this page. Write each as `NAME: value`, indented under `data:`, and quote a value that is a number, `true` or `false`, or contains `: ` or ` #`:

```yaml
data:
  JIM_SSO_AUTHORITY: https://login.microsoftonline.com/your-tenant-id/v2.0
  JIM_SSO_MV_ATTRIBUTE: Subject Identifier
  JIM_DB_LOG_MIN_DURATION: "1000"
```

Restart JIM after a change: `sudo systemctl restart jim.service`, or for a rootless installation, `sudo systemctl --user -M jim@ restart jim.service`.

These settings differ on Podman:

- **Secrets**<br /> `JIM_DB_PASSWORD`, `JIM_SSO_SECRET` and `JIM_INFRASTRUCTURE_API_KEY` are not in `jim-config.yaml`. They are the Podman secret `jim-secrets`, which only root (or, rootless, the account that runs JIM) can read; see [Installing by Hand](podman.md#installing-by-hand) to store or change it.
- **`DOCKER_REGISTRY`, `JIM_VERSION`, `JIM_WEB_PORT`**<br /> Not used: the pod file, `jim.yaml`, names JIM's images with their registry and version, and the HTTPS port is `PublishPort=` in the `jim.kube` unit.
- **`JIM_DB_SHM_SIZE`**<br /> Not used: Podman cannot size a pod's `/dev/shm`, so the bundled database pod keeps its parallel query memory in its data folder instead.
- **`JIM_DB_HOSTNAME`**<br /> `jim-database` for the bundled PostgreSQL, rather than Docker's `jim.database`.
- **Settings the pod file sets itself**<br /> Leave `JIM_LOG_PATH`, `JIM_LOG_REQUESTS` and the `ASPNETCORE_` settings out of `jim-config.yaml`: Podman would let a value there replace the pod file's.

---

## Locale

| Variable | Description                                                                                   | Default            |
|----------|-----------------------------------------------------------------------------------------------|--------------------|
| `LANG`   | Controls date/time formatting and other locale-specific behaviour. Uses standard locale codes. | `en_GB.UTF-8`     |

The bundled PostgreSQL container ignores `LANG` and always initialises its database with the `C.UTF-8` locale, because the PostgreSQL image does not ship regional locales such as `en_GB.UTF-8`.

---

## Docker

These variables control how Docker Compose resolves and pulls JIM container images, and where it publishes the web UI.

| Variable          | Description                                                                 | Default | Example                     |
|-------------------|-----------------------------------------------------------------------------|---------|-----------------------------|
| `DOCKER_REGISTRY` | Container registry prefix for pulling images. Leave empty for local builds. | *(empty)* | `ghcr.io/tetronio/`        |
| `JIM_VERSION`     | Release version tag. Leave empty for local builds.                          | *(empty)* | `0.10.0`                   |
| `JIM_WEB_PORT`    | Host port the production compose file publishes the web UI and API on, over HTTPS (the container listens on `8443`). Prefix an address to bind one interface only. | `443` | `127.0.0.1:8443` |

---

## HTTPS Certificate

JIM's certificate is not set in `.env`. The production compose file reads it from two PEM files in the `tls` folder next to the compose files, which `setup.sh` creates:

| File          | Contents                                                                                        |
|---------------|-------------------------------------------------------------------------------------------------|
| `tls/tls.crt` | JIM's certificate, followed by any intermediate CA certificates                                 |
| `tls/tls.key` | Its unencrypted private key, belonging to UID `1654` with mode `400`                            |

To point `jim.web` at them, `docker-compose.production.yml` sets `ASPNETCORE_URLS` (HTTPS on `8443`, plus plain HTTP on the container's loopback interface for its health check) and `ASPNETCORE_Kestrel__Certificates__Default__Path` and `__KeyPath` (the files, mounted at `/run/jim-tls/`). A compose file's settings take precedence over `.env`, so setting these in `.env` has no effect. On Podman, the pod file sets the same settings, and JIM reads the pair from the Podman secret `jim-tls`, mounted at the same path. See [TLS and Reverse Proxy](deployment.md#tls-and-reverse-proxy) for providing, creating and renewing the certificate.

---

## Database

| Variable                   | Description                                                                                                         | Default     | Example                    |
|----------------------------|---------------------------------------------------------------------------------------------------------------------|-------------|----------------------------|
| `JIM_DB_HOSTNAME`          | PostgreSQL server hostname. Use `jim.database` for the bundled container. Append `:port` for a server on a non-default port. | `localhost` | `jim.database`             |
| `JIM_DB_NAME`              | Database name.                                                                                                      | `jim`       | `jim`                      |
| `JIM_DB_USERNAME`          | Database username.                                                                                                  | `jim`       | `jim`                      |
| `JIM_DB_PASSWORD`          | Database password. **Use a strong, unique value in production.**                                                    | *(none)*    | *(generate a strong password)* |
| `JIM_DB_LOG_SENSITIVE_INFO` | When `true`, includes parameter values in database query logs. **Do not enable in production.**                    | `false`     | `false`                    |
| `JIM_DB_LOG_MIN_DURATION`  | Slow query log threshold in milliseconds. Queries exceeding this duration are logged. Set to `-1` to disable, `0` to log all queries. | `1000`  | `500`                      |
| `JIM_DB_IMAGE`             | Docker only: the bundled PostgreSQL's image. Leave it unset: the installer sets it to the image's ID for an air-gapped install on Docker's classic image store, which cannot find the pinned image by its digest (see [By Hand](deployment.md#by-hand)). | *(the image `docker-compose.yml` pins by digest)* | `sha256:662db3da…` |

### Bundled PostgreSQL Memory {#bundled-postgresql-memory}

These size the bundled PostgreSQL's memory, and are ignored with your own PostgreSQL server. The installer sets them from the host's memory when it installs JIM with the bundled PostgreSQL, and when it upgrades an installation whose settings do not already have them. Left unset, the defaults suit the minimum supported host, with 4 GB of memory.

| Variable | PostgreSQL setting | The installer sets it to | Default |
|----------|--------------------|--------------------------|---------|
| `JIM_DB_SHARED_BUFFERS` | `shared_buffers`: PostgreSQL's own cache, allocated when it starts | A quarter of the host's memory, at most 8 GB, since JIM's services share the host | `1GB` |
| `JIM_DB_EFFECTIVE_CACHE_SIZE` | `effective_cache_size`: how much caching PostgreSQL assumes when it plans a query; nothing is allocated | Half of the host's memory | `2GB` |
| `JIM_DB_MAINTENANCE_WORK_MEM` | `maintenance_work_mem`: memory for maintenance, such as building an index | A sixteenth of the host's memory, at most 2 GB | `256MB` |
| `JIM_DB_WORK_MEM` | `work_mem`: memory for each sort or hash in a query | The host's memory less `shared_buffers`, divided by 600 (three for each of the 200 connections allowed), and at least 4 MB | `4MB` |
| `JIM_DB_SHM_SIZE` | Docker only: the database container's `/dev/shm`, which holds the shared memory of parallel queries | `shared_buffers` and a quarter more | `1280mb` |

For example, on a host with 8 GB of memory the installer sets `JIM_DB_SHARED_BUFFERS=2048MB`, `JIM_DB_EFFECTIVE_CACHE_SIZE=4096MB`, `JIM_DB_MAINTENANCE_WORK_MEM=512MB`, `JIM_DB_WORK_MEM=10MB` and `JIM_DB_SHM_SIZE=2560mb`. Give PostgreSQL's units (`kB`, `MB`, `GB`) for the first four, and Docker's (`mb`, `gb`) for `JIM_DB_SHM_SIZE`.

`shared_buffers` is allocated in full when PostgreSQL starts, so a value larger than the host can give stops the database from starting: its log says `could not map anonymous shared memory: Cannot allocate memory`. Size it down, as below.

The installer uses any of these given in its environment as given, and its summary marks each one `(given)`. It checks a given `JIM_DB_SHARED_BUFFERS` against the host's memory before it starts anything. When the host cannot give it (the value is as much as the host's memory or more), the installer stops having started nothing, and names the size it would choose instead; leave the setting unset or give less, and run it again. It warns, and carries on, when the value is more than half the host's memory, or is a size it cannot read: PostgreSQL's units are case-sensitive, so it refuses `8gb` where it takes `8GB`.

To change them, for example after adding memory to the host, edit the settings and restart the database:

- **Docker**<br /> Edit `.env`, then `docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d` in `/opt/jim`, which recreates the database container with the new settings.
- **Podman**<br /> Edit `jim-config.yaml` (quote each value, as in `JIM_DB_SHARED_BUFFERS: "2048MB"`), then `systemctl restart jim-database.service`.

To have the installer size them again instead, delete them from the settings file and run the upgrade (Docker), or set each yourself from the rules above.

### Connecting to a non-default port

PostgreSQL listens on port 5432 unless it has been configured otherwise, and that is what JIM assumes. There is no
separate port variable: where your server listens elsewhere, give the port as part of the hostname.

```bash
JIM_DB_HOSTNAME=db.example.org:5433
```

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
| `JIM_SSO_PUBLIC_AUTHORITY` | Client-facing OIDC authority URL, advertised to interactive clients (browsers, the PowerShell module) via the `/api/v1/auth/config` discovery endpoint. Optional. Only set this when the backend reaches the identity provider at a different URL than clients do (e.g. a dev devcontainer where the JIM container reaches Keycloak via Docker DNS while the host reaches it via `localhost`, or a reverse-proxy topology where the internal and external URLs differ). When unset, clients are given `JIM_SSO_AUTHORITY`. Backend token validation and JWKS lookups always use `JIM_SSO_AUTHORITY` and are unaffected by this setting. | `http://localhost:8181/realms/jim` |
| `JIM_SSO_PUBLIC_CLIENT_ID` | Client ID advertised to interactive public clients (PowerShell module, etc.) via the `/api/v1/auth/config` discovery endpoint. Optional. Required when the identity provider needs a separate registration for PKCE/loopback clients (Keycloak mandates this; Entra ID and AD FS allow a shared registration with multiple platforms). When unset, clients receive `JIM_SSO_CLIENT_ID`. Backend token validation is unaffected; tokens from either client must still carry the `JIM_SSO_API_SCOPE` audience. | `jim-powershell` |

**Authority URL examples by provider:**

| Provider       | Authority URL Format                                             |
|----------------|------------------------------------------------------------------|
| Entra ID       | `https://login.microsoftonline.com/{tenant-id}/v2.0`            |
| AD FS          | `https://{adfs-server}/adfs`                                     |
| Keycloak       | `https://{keycloak-server}/realms/{realm-name}`                  |

---

## User Identity Mapping

These variables control how JIM maps authenticated users to Metaverse Objects, enabling role-based access control.

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
| `Sync.PartitionValidationMode` | Run Profile partition validation | Synchronisation | Controls how JIM behaves when a Run Profile is executed for a Connected System that supports partitions but has none selected. `Error` blocks execution; `Warning` allows execution but logs a warning.                                                         | `Error`       |
| `History.RetentionPeriod`    | History retention period  | History         | The duration for which activity and audit history is retained. Format: `d.hh:mm:ss`. Longer periods increase database size and may affect performance. Configuration change history is excluded; it has its own retention period below.                          | `90.00:00:00` (90 days) |
| `History.ConfigurationChangeRetentionPeriod` | Configuration change retention period | History | The duration for which configuration change history (versioned Connected System, Synchronisation Rule, and Schedule snapshots) is retained. Kept separately from, and typically much longer than, the general history retention period. Format: `d.hh:mm:ss`.     | `3650.00:00:00` (~10 years) |
| `History.SecurityEventRetentionPeriod` | Security event retention period | History | The duration for which [security audit events](../administration/security-audit-events.md) (interactive sign-in success/failure, API key authentication failure) are retained. Kept separately from the general history and configuration change retention periods. Format: `d.hh:mm:ss`. | `365.00:00:00` (~1 year) |
| `History.PasswordEventRetentionPeriod` | Password Synchronisation retention period | History | The duration for which [Password Synchronisation](../concepts/passwords.md) history is kept: the Activities recording what happened to each password change, and the queued changes that reached a terminal state (parked, expired, or cancelled). Changes still owed to a Connected System are never removed, however old. Shorten this to bound how long JIM holds an encrypted password it can no longer deliver. Format: `d.hh:mm:ss`. | `365.00:00:00` (~1 year) |
| `History.CleanupBatchSize` | History cleanup batch size | History | The most records of any one kind a single retention pass removes. Lower values reduce database load per pass but take longer to work through a backlog; a pass that hits the cap simply continues on the next run. | `100` |
| `ChangeTracking.ConfigurationChanges.Enabled` | Track configuration changes | History | Enables or disables capture of Configuration Change History. When `true`, a redacted, versioned snapshot is recorded on the Activity for every configuration create, update, and delete. Set to `false` to stop capturing new history; existing history is not deleted. | `true`        |
| `Security.RateLimiting.Enabled` | API rate limiting enabled | Security | When `true`, REST API requests are throttled per client. See [Rate Limiting](../api/rate-limiting.md). | `true` |
| `Security.RateLimiting.AuthenticatedRequestsPerMinute` | Authenticated API requests per minute | Security | The maximum REST API requests per minute for an authenticated client (per signed-in user or API key). | `300` |
| `Security.RateLimiting.UnauthenticatedRequestsPerMinute` | Unauthenticated API requests per minute | Security | The maximum REST API requests per minute for an unauthenticated client (per IP address). | `30` |

!!! tip "Editing service settings"
    Navigate to **Admin > Service Settings**, use the filter and search box to locate the setting by key or display name, and click the edit icon. The search box filters as you type, as it does on every list in the portal. Changes are audited: the settings page shows who last modified each value and when.

---

## Logging

| Variable           | Description                                                                        | Default         | Example          |
|--------------------|------------------------------------------------------------------------------------|-----------------|------------------|
| `JIM_LOG_LEVEL`    | Minimum log level. Valid values: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. | `Information`   | `Warning`        |
| `JIM_LOG_PATH`     | Directory path for log file output.                                                | `/tmp/jim-logs` | `/var/log/jim`   |
| `JIM_LOG_REQUESTS` | When `true`, logs all HTTP requests. Useful for debugging but generates high volume. | `false`         | `true`           |

---

## Encryption Keys

JIM encrypts secrets at rest (Connected System credentials, the SSO secret, Schedule SQL-step connection strings) with keys stored on the filesystem. All three services (`jim.web`, `jim.worker`, `jim.scheduler`) must resolve to the same key location so they can decrypt each other's data.

| Variable                  | Description                                                                                                                                                                                 | Default                          | Example        |
|---------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------|----------------|
| `JIM_ENCRYPTION_KEY_PATH` | Filesystem path where encryption keys are stored. When unset, JIM uses `/data/keys` (the `jim-keys-volume` Docker volume) if `/data` exists, otherwise the platform application-data directory. | *(unset)* -- resolves to `/data/keys` in Docker | `/data/keys`   |

!!! danger "These keys must be backed up with the database"
    The database ciphertext cannot be decrypted without these keys. A database backup restored without its matching keys leaves every stored secret unrecoverable. See [Backup & Disaster Recovery](backup-recovery.md).

---

## UI Theme

| Variable    | Description                                                                                                    | Default    |
|-------------|----------------------------------------------------------------------------------------------------------------|------------|
| `JIM_THEME` | Built-in colour theme for the JIM web interface. Valid values: `purple`, `black`, `blended-nav`, `future-minimal`, `navy-o5`, `navy-o6`. | `navy-o6`  |

---

## Reverse Proxy

| Variable              | Description                                                                                                                                                                                                                     | Default              |
|------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------|
| `JIM_TRUSTED_PROXIES` | Comma-separated list of trusted proxy IP addresses and/or CIDR networks (e.g. `10.0.0.1,172.16.0.0/12`). When set, JIM trusts `X-Forwarded-For`/`X-Forwarded-Proto` headers from these sources, so the real client IP and scheme are recovered rather than the proxy's own. Set it whenever a reverse proxy or load balancer sits in front of JIM. It is required when the proxy forwards plain HTTP to JIM: without it, sign-in fails and the REST API refuses requests that carry a password (see [Trusting the Reverse Proxy](deployment.md#trusting-the-reverse-proxy)). Also used for unauthenticated API [rate limiting](../api/rate-limiting.md), the security audit log, logging, and HTTPS redirection. | *(unset)* -- forwarded headers are not trusted; the connecting socket's address is used as-is |

!!! warning "Only set this behind a trusted reverse proxy"
    Trusting forwarded headers from an address you do not control lets a client spoof its own IP, defeating IP-based rate limiting and polluting logs. Only list proxies (or the proxy network) that terminate connections in front of JIM.

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
