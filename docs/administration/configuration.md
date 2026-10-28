---
title: Configuration Reference
description: Every environment variable and Service Setting that configures JIM, by category, with its default and what it changes.
type: reference
audience: administrator
---

# Configuration Reference

JIM is configured through environment variables set in the `.env` file alongside your Docker Compose files, or on Podman in `jim-config.yaml` (see [Podman](#podman)). The installer sets them for you; to change one later, edit the file. The settings and their names are the same on both runtimes.

```jim-diagram
archetype: comparison
title: Environment variables and Service Settings compared
desc: "Environment variables are kept in a file on the server, .env, or jim-config.yaml on Podman; you change one by editing the file, and it takes effect when JIM restarts. They suit infrastructure: the database connection, the OIDC authority, log paths. Service Settings are kept in the database; an Administrator changes them in the portal at Admin > Service Settings, which records who changed each and when, and most take effect at once, without a restart. They suit day-to-day operation, such as history retention, API rate limits and the synchronisation page size. A read-only Service Setting that mirrors a variable shows the value JIM started with."
columns:
- {label: Environment variables, sub: '`.env`, or `jim-config.yaml` on Podman'}
- {label: Service Settings, sub: Admin > Service Settings}
rows:
- label: Kept in
  cells:
  - {text: a file on the server}
  - {text: the database}
- label: Changed by
  cells:
  - {text: editing the file}
  - {text: an Administrator, sub: 'audited: who and when'}
- label: Takes effect
  cells:
  - {text: when JIM restarts}
  - {text: 'most at once', sub: no restart}
- label: Suits
  cells:
  - {text: infrastructure, sub: 'database, OIDC authority, log paths'}
  - {text: day-to-day operation, sub: 'retention, rate limits, page size'}
footer: 'A read-only Service Setting that mirrors a variable shows the value JIM started with'
```

!!! tip
    A fully commented `.env.example` template is included with every release and is available in the [GitHub repository](https://github.com/TetronIO/JIM/releases).

---

## :material-package-variant-closed: Podman

On Podman, JIM reads its settings from `/opt/jim/jim-config.yaml`, a Kubernetes-style ConfigMap holding the same settings, with the same names, as `.env`; each one is on this page. Write each as `NAME: value`, indented under `data:`, and quote a value that is a number, `true` or `false`, or contains `: ` or ` #`:

```yaml
data:
  JIM_SSO_AUTHORITY: https://login.microsoftonline.com/your-tenant-id/v2.0
  JIM_SSO_MV_ATTRIBUTE: Subject Identifier
  JIM_DB_LOG_MIN_DURATION: "1000"
```

Restart JIM after a change: `sudo systemctl restart jim.service`, or for a rootless installation, `jim-systemctl restart jim.service` (see [Rootless commands](podman.md#rootless-commands)).

These settings differ on Podman:

- **Secrets**<br /> `JIM_DB_PASSWORD`, `JIM_SSO_SECRET` and `JIM_INFRASTRUCTURE_API_KEY` are not in `jim-config.yaml`. They are the Podman secret `jim-secrets`, which only root (or, rootless, the account that runs JIM) can read; see [Installing on Podman by Hand](podman-by-hand.md) to store or change it.
- **`DOCKER_REGISTRY`, `JIM_VERSION`, `JIM_WEB_PORT`**<br /> Not used: the pod file, `jim.yaml`, names JIM's images with their registry and version, and the HTTPS port is `PublishPort=` in the `jim.kube` unit.
- **`JIM_DB_SHM_SIZE`**<br /> Not used: Podman cannot size a pod's `/dev/shm`, so the bundled database pod keeps its parallel query memory in its data folder instead.
- **`JIM_DB_HOSTNAME`**<br /> `jim-database` for the bundled PostgreSQL, rather than Docker's `jim.database`.
- **Settings the pod file sets itself**<br /> Leave `JIM_LOG_PATH`, `JIM_LOG_REQUESTS` and the `ASPNETCORE_` settings out of `jim-config.yaml`: Podman would let a value there replace the pod file's.

---

## :material-translate: Locale

| Variable | Description                                                                                   | Default            |
|----------|-----------------------------------------------------------------------------------------------|--------------------|
| `LANG`   | Controls date/time formatting and other locale-specific behaviour. Uses standard locale codes. | `en_GB.UTF-8`     |

The bundled PostgreSQL container ignores `LANG` and always initialises its database with the `C.UTF-8` locale, because the PostgreSQL image does not ship regional locales such as `en_GB.UTF-8`.

---

## :material-docker: Docker

These variables control how Docker Compose resolves and pulls JIM container images, and where it publishes the web UI.

| Variable          | Description                                                                 | Default | Example                     |
|-------------------|-----------------------------------------------------------------------------|---------|-----------------------------|
| `DOCKER_REGISTRY` | Container registry prefix for pulling images. Leave empty for local builds. | *(empty)* | `ghcr.io/tetronio/`        |
| `JIM_VERSION`     | Release version tag. Leave empty for local builds.                          | *(empty)* | `0.10.0`                   |
| `JIM_WEB_PORT`    | Host port the production compose file publishes the web UI and API on, over HTTPS (the container listens on `8443`). Prefix an address to bind one interface only. | `443` | `127.0.0.1:8443` |

---

## :material-certificate-outline: HTTPS Certificate

JIM's certificate is not set in `.env`. The production compose file reads it from two PEM files in the `tls` folder next to the compose files, which `setup.sh` creates:

| File          | Contents                                                                                        |
|---------------|-------------------------------------------------------------------------------------------------|
| `tls/tls.crt` | JIM's certificate, followed by any intermediate CA certificates                                 |
| `tls/tls.key` | Its unencrypted private key, belonging to UID `1654` with mode `400`                            |

To point `jim.web` at them, `docker-compose.production.yml` sets `ASPNETCORE_URLS` (HTTPS on `8443`, plus plain HTTP on the container's loopback interface for its health check) and `ASPNETCORE_Kestrel__Certificates__Default__Path` and `__KeyPath` (the files, mounted at `/run/jim-tls/`). A compose file's settings take precedence over `.env`, so setting these in `.env` has no effect. On Podman, the pod file sets the same settings, and JIM reads the pair from the Podman secret `jim-tls`, mounted at the same path. See [TLS and Reverse Proxy](tls-and-reverse-proxy.md) for providing, creating and renewing the certificate.

---

## :material-database-outline: Database

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

The installer uses any of these given in its environment as given, and its summary marks each one `(given)`. It checks a given `JIM_DB_SHARED_BUFFERS` against the host's memory before it starts anything. When the host cannot give it (the value is as much as the host's memory or more), the installer stops having started nothing, leaving an existing installation as it was, and names the size it would choose instead; leave the setting unset or give less, and run it again. It warns, and carries on, when the value is more than half the host's memory, or is a size it cannot read: PostgreSQL's units are case-sensitive, so it refuses `8gb` where it takes `8GB`.

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

### Dropping Lost Connections {#lost-connections}

The bundled PostgreSQL drops a connection whose client has gone within two minutes. After 60 seconds without traffic it checks the connection, and it gives up after six unanswered checks 10 seconds apart (`tcp_keepalives_idle=60`, `tcp_keepalives_interval=10`, `tcp_keepalives_count=6`). A connection whose client is still there is unaffected, since the client answers each check.

Without these settings, PostgreSQL holds such a connection open for about two hours, Linux's default. On Podman that happens at every stop of JIM, because the pod's network goes before JIM's services close their connections. With your own PostgreSQL server, set the same three settings in its `postgresql.conf` to have it do the same.

---

## :material-shield-key-outline: SSO / Authentication

These variables configure JIM's OIDC-based single sign-on. For provider-specific setup instructions, see the [SSO Setup Guide](sso-setup.md).

| Variable              | Description                                                                                      | Example                                                      |
|-----------------------|--------------------------------------------------------------------------------------------------|--------------------------------------------------------------|
| `JIM_SSO_AUTHORITY`   | OIDC authority URL for your identity provider.                                                   | `https://login.microsoftonline.com/{tenant}/v2.0`            |
| `JIM_SSO_CLIENT_ID`   | OAuth client/application ID.                                                                     | `12345678-1234-1234-1234-123456789abc`                       |
| `JIM_SSO_SECRET`      | OAuth client secret. **Keep this value secure.**                                                 | *(from your IdP)*                                            |
| `JIM_SSO_API_SCOPE`   | API scope for JWT bearer authentication (API endpoints).                                         | `api://{client-id}/access_as_user`                           |
| `JIM_SSO_VALID_ISSUERS` | Comma-separated list of trusted token issuers. Usually not needed: JIM accepts the issuers the provider's discovery document names, `issuer` and, where it publishes one, `access_token_issuer` (so AD FS needs no setting), and detects both Entra ID issuer formats. Issuers set here are accepted as well as those. Only set this if your provider's issuer URL differs from its authority URL, or you need to trust multiple issuers (e.g. during a provider migration). | `https://login.microsoftonline.com/{tenant}/v2.0` |
| `JIM_SSO_PUBLIC_AUTHORITY` | Client-facing OIDC authority URL, advertised to interactive clients (browsers, the PowerShell module) via the `/api/v1/auth/config` discovery endpoint. Optional. Only set this when the backend reaches the identity provider at a different URL than clients do (for example where the JIM containers reach the identity provider by its name on the container network while browsers reach it at `localhost`, or a reverse-proxy topology where the internal and external URLs differ). When unset, clients are given `JIM_SSO_AUTHORITY`. Backend token validation and JWKS lookups always use `JIM_SSO_AUTHORITY` and are unaffected by this setting. | `http://localhost:8181/realms/jim` |
| `JIM_SSO_PUBLIC_CLIENT_ID` | Client ID advertised to interactive public clients (PowerShell module, etc.) via the `/api/v1/auth/config` discovery endpoint. Optional. Required when the identity provider needs a separate registration for PKCE/loopback clients (Keycloak mandates this; Entra ID and AD FS allow a shared registration with multiple platforms). When unset, clients receive `JIM_SSO_CLIENT_ID`. Backend token validation is unaffected; tokens from either client must still carry the `JIM_SSO_API_SCOPE` audience. | `jim-powershell` |

**Authority URL examples by provider:**

| Provider       | Authority URL Format                                             |
|----------------|------------------------------------------------------------------|
| Entra ID       | `https://login.microsoftonline.com/{tenant-id}/v2.0`            |
| AD FS          | `https://{adfs-server}/adfs`                                     |
| Keycloak       | `https://{keycloak-server}/realms/{realm-name}`                  |

---

## :material-account-arrow-right-outline: User Identity Mapping

These variables control how JIM maps authenticated users to Metaverse Objects, enabling role-based access control.

| Variable                | Description                                                                                                                                  | Default               | Example                   |
|-------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|-----------------------|---------------------------|
| `JIM_SSO_CLAIM_TYPE`    | The JWT claim type containing the user's unique identifier. Use `sub` (standard OIDC subject identifier) for most providers.                | `sub`                 | `sub`                     |
| `JIM_SSO_MV_ATTRIBUTE`  | The Metaverse Attribute to match the claim value against. This should be an attribute on the User object type that stores the unique identifier from your identity provider. It must be a Text or Guid attribute; see [Matching on a GUID](#matching-on-a-guid). | `Subject Identifier`  | `Subject Identifier`, `Object GUID` |
| `JIM_SSO_INITIAL_ADMIN` | The claim value for the initial admin user (used during first-time setup). When `JIM_SSO_MV_ATTRIBUTE` is a Guid attribute, the admin's GUID. See the [SSO Setup Guide](sso-setup.md) for instructions on finding this value. | *(none)*              | *(your admin's sub claim)* |

!!! tip "Finding your admin's claim value"
    1. Log into JIM with your admin account
    2. Navigate to `/claims`
    3. Find the value of the claim type specified in `JIM_SSO_CLAIM_TYPE`

### Matching on a GUID

When `JIM_SSO_MV_ATTRIBUTE` names a Guid attribute, such as `Object GUID`, JIM reads the claim as a GUID and matches the person whose attribute holds the same GUID. This is how AD FS identifies people by their Active Directory `objectGUID`, which is always set, survives renames and is never reused. The person then signs in as the Metaverse Object that synchronisation from Active Directory created or joined.

- The claim may carry GUID text (`550e8400-e29b-41d4-a716-446655440000`), or the GUID's 16 bytes base64-encoded, as AD FS issues `objectGUID`.
- `JIM_SSO_INITIAL_ADMIN` is the admin's GUID as text, in any case, as the Metaverse Object shows it.
- JIM refuses to start, naming the setting, when `JIM_SSO_MV_ATTRIBUTE` is neither a Text nor a Guid attribute, or is a Guid attribute and `JIM_SSO_INITIAL_ADMIN` is not a GUID.
- A sign-in whose claim is not a GUID is refused, and the log says which claim and attribute to check.

---

## :material-tune-variant: Service Settings

Service settings are stored in the database and managed through the admin UI at **Admin > Service Settings** (`/admin/settings`). They can be changed at runtime by users with the Administrator role; most take effect immediately, without restarting the JIM containers.

This differs from environment variables, which are applied at process startup and tend to be infrastructure-level concerns (database connection, OIDC authority, log paths). Service settings are the day-to-day operational knobs.

Every Service Setting that changes how JIM behaves is listed below. The Preview Features category's feature flags are described in [Preview Features](preview-features.md). Where a read-only setting mirrors an environment variable, it shows the value JIM started with; change the variable and restart JIM to change it.

| Key | Display name | Category | Description | Default |
|-----|--------------|----------|-------------|---------|
| `Instance.Name`              | Service name              | Instance        | A friendly, editable name for this JIM instance. Appears in the sidebar, browser tab title, and footer so operators can distinguish multiple JIM deployments at a glance (for example, `JIM Production`, `JIM DR`).                                             | *(empty)*     |
| `Instance.Id`                | Service ID                | Instance        | A stable GUID that uniquely identifies this JIM instance. Generated exactly once on first startup and never changes thereafter. Useful for tooling, logs, and telemetry correlation. Read-only.                                                                  | *(generated)* |
| `SSO.Authority` | SSO authority | SSO | The OIDC authority JIM signs users in with. Read-only: mirrors `JIM_SSO_AUTHORITY`. | *(from `JIM_SSO_AUTHORITY`)* |
| `SSO.ClientId` | SSO client ID | SSO | JIM's OIDC client identifier. Read-only: mirrors `JIM_SSO_CLIENT_ID`. | *(from `JIM_SSO_CLIENT_ID`)* |
| `SSO.Secret` | SSO secret | SSO | JIM's OIDC client secret, stored encrypted. Read-only: mirrors `JIM_SSO_SECRET`. | *(from `JIM_SSO_SECRET`)* |
| `SSO.ApiScope` | SSO API scope | SSO | The OIDC scope required to call JIM's API. Read-only: mirrors `JIM_SSO_API_SCOPE`. | *(from `JIM_SSO_API_SCOPE`)* |
| `SSO.ClaimType` | SSO claim type | SSO | The token claim that identifies the person signing in; its value is matched against `SSO.MvAttribute`. Read-only: mirrors `JIM_SSO_CLAIM_TYPE`. | *(from `JIM_SSO_CLAIM_TYPE`)* |
| `SSO.MvAttribute` | SSO Metaverse attribute | SSO | The Metaverse Attribute whose value is matched against the `SSO.ClaimType` claim, to find the Metaverse Object of the person signing in. Read-only: mirrors `JIM_SSO_MV_ATTRIBUTE`. | *(from `JIM_SSO_MV_ATTRIBUTE`)* |
| `SSO.EnableLogOut`           | SSO enable log-out        | SSO             | Controls whether the sign-out button is shown in the JIM user menu. Set to `false` for deployments where users cannot realistically sign out of their enterprise-managed SSO session, for example on domain-joined devices with seamless SSO. | `true`        |
| `Sync.PartitionValidationMode` | Run Profile partition validation | Synchronisation | Controls how JIM behaves when a Run Profile is executed for a Connected System that supports partitions but has none selected. `Error` blocks execution; `Warning` allows execution but logs a warning.                                                         | `Error`       |
| `Sync.PageSize` | Sync page size | Synchronisation | The number of Connected System Objects processed per database page during synchronisation. Larger pages mean fewer database round trips; 200 to 1,000 suits most deployments. | `500` |
| `Sync.PostClearReconciliation.MaxMissingPercent` | Post-clear reconciliation: maximum missing share | Synchronisation | The largest share, as a whole-number percentage, of the objects recorded when a Connector Space was [cleared](../configuration/connector-space.md#clearing-the-connector-space) that may still be missing a re-join before the post-clear reconciliation sweep refuses to run. Above it, JIM assumes the re-import was broken (a changed filter or base DN, say) rather than a genuine mass departure, and refuses until you re-import the Connected System or raise this setting. | `10` |
| `Preview.WorkerThreshold` | Preview worker threshold | Synchronisation | The estimated number of affected objects above which a [Configuration Change Preview](../configuration/configuration-changes.md) is evaluated by the Worker rather than in the portal's own process. Smaller previews run in-process, so they return without waiting for the Worker to pick them up. Both give identical results. | `2500` |
| `Preview.FullDataSetPromptThreshold` | Preview full data set prompt threshold | Synchronisation | The estimated number of object-level rows above which a Configuration Change Preview asks whether to keep the full data set or only a sample of each summary group. Below it, the preview keeps a sample without asking. Summary counts are exact either way; the choice decides how much you can drill into, and how much storage the preview uses. | `100000` |
| `Worker.StaleTaskTimeout` | Stale task timeout | Maintenance | How long a running Worker task may go without a heartbeat before JIM treats its Worker as crashed: the Scheduler then fails the task's Activity and removes the task, so the work can be run again. A zero or negative value is ignored in favour of the default. Format: `d.hh:mm:ss`. | `00:05:00` (5 minutes) |
| `History.RetentionPeriod`    | History retention period  | History         | The duration for which activity and audit history is retained. Format: `d.hh:mm:ss`. Longer periods increase database size and may affect performance. Configuration Change History is excluded; it has its own retention period below.                          | `90.00:00:00` (90 days) |
| `History.ConfigurationChangeRetentionPeriod` | Configuration change retention period | History | The duration for which Configuration Change History (versioned Connected System, Synchronisation Rule, and Schedule snapshots) is retained. Kept separately from, and typically much longer than, the general history retention period. Format: `d.hh:mm:ss`.     | `3650.00:00:00` (~10 years) |
| `History.SecurityEventRetentionPeriod` | Security event retention period | History | The duration for which [security audit events](../administration/security-audit-events.md) (interactive sign-in success/failure, and failed API key or SSO access token authentication) are retained. Kept separately from the general history and configuration change retention periods. Format: `d.hh:mm:ss`. | `365.00:00:00` (~1 year) |
| `History.PasswordEventRetentionPeriod` | Password Synchronisation retention period | History | The duration for which [Password Synchronisation](../concepts/passwords.md) history is kept: the Activities recording what happened to each password change, and the queued changes that reached a terminal state (parked, expired, or cancelled). Changes still owed to a Connected System are never removed, however old. Shorten this to bound how long JIM holds an encrypted password it can no longer deliver. Format: `d.hh:mm:ss`. | `365.00:00:00` (~1 year) |
| `History.CleanupBatchSize` | History cleanup batch size | History | The most records of any one kind a single retention pass removes. Lower values reduce database load per pass but take longer to work through a backlog; a pass that hits the cap continues on the next run. | `100` |
| `ChangeTracking.CsoChanges.Enabled` | Track CSO changes <!-- conformance: allow LANG-4 "the label the portal shows" --> | History | Whether change history is recorded for every Connected System Object create, update and delete. Turning it off saves database writes at the cost of those objects' audit trail. | `true` |
| `ChangeTracking.MvoChanges.Enabled` | Track MVO changes <!-- conformance: allow LANG-4 "the label the portal shows" --> | History | Whether change history is recorded for every Metaverse Object create, update and delete. Turning it off saves database writes at the cost of those objects' audit trail. | `true` |
| `ChangeTracking.ConfigurationChanges.Enabled` | Track configuration changes | History | Enables or disables capture of Configuration Change History. When `true`, a redacted, versioned snapshot is recorded on the Activity for every configuration create, update, and delete. Set to `false` to stop capturing new history; existing history is not deleted. | `true`        |
| `ChangeTracking.SyncOutcomes.Level` | Sync outcome tracking level | History | How much of each synchronisation outcome a Run Profile execution item records: `None` records no outcomes, `Standard` records the root outcomes, and `Detailed` records the full chain of what each change went on to cause. Higher levels give a richer audit trail and use more storage. | `Detailed` |
| `Security.EncryptionKeyPath` | Encryption key storage path | Security | Where JIM keeps its encryption keys; see [Encryption Keys](#encryption-keys). Read-only: mirrors `JIM_ENCRYPTION_KEY_PATH`. | *(from `JIM_ENCRYPTION_KEY_PATH`; empty when it is unset)* |
| `Security.ConfigurationChangeHashKey` | Configuration change hash key | Security | The key JIM uses to tell that a secret in a configuration change has changed, without storing the secret in the change history. Generated by JIM and stored encrypted. Read-only. | *(generated)* |
| `Security.RateLimiting.Enabled` | API rate limiting enabled | Security | When `true`, REST API requests are throttled per client. See [Rate Limiting](../api/rate-limiting.md). | `true` |
| `Security.RateLimiting.AuthenticatedRequestsPerMinute` | Authenticated API requests per minute | Security | The maximum REST API requests per minute for an authenticated client (per signed-in user or API key). | `300` |
| `Security.RateLimiting.UnauthenticatedRequestsPerMinute` | Unauthenticated API requests per minute | Security | The maximum REST API requests per minute for an unauthenticated client (per IP address). | `30` |
| `Security.RateLimiting.ScimServiceProviderRequestsPerMinute` | SCIM Service Provider requests per minute | Security | The maximum requests per minute that the SCIM clients of one Connected System may make to its SCIM 2.0 Service Provider endpoint, shared by all of that Connected System's keys. | `1500` |
| `UI.ProgressUpdateInterval` | Progress update interval | UI | How often JIM reports progress while it generates Example Data from a template. Format: `d.hh:mm:ss`. | `00:00:01` (1 second) |

!!! tip "Editing service settings"
    Navigate to **Admin > Service Settings**, use the filter and search box to locate the setting by key or display name, and click the edit icon. The search box filters as you type, as it does on every list in the portal. Changes are audited: the settings page shows who last modified each value and when.

---

## :material-text-box-search-outline: Logging

| Variable           | Description                                                                        | Default         | Example          |
|--------------------|------------------------------------------------------------------------------------|-----------------|------------------|
| `JIM_LOG_LEVEL`    | Minimum log level. Valid values: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. | `Information`   | `Warning`        |
| `JIM_LOG_PATH`     | Directory path for log file output.                                                | `/tmp/jim-logs` | `/var/log/jim`   |
| `JIM_LOG_REQUESTS` | When `true`, logs all HTTP requests. Useful for debugging but generates high volume. | `false`         | `true`           |

---

## :material-key-outline: Encryption Keys

JIM encrypts secrets at rest (Connected System credentials, the SSO secret, Schedule SQL-step connection strings) with keys stored on the filesystem. All three services (`jim.web`, `jim.worker`, `jim.scheduler`) must resolve to the same key location so they can decrypt each other's data.

| Variable                  | Description                                                                                                                                                                                 | Default                          | Example        |
|---------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------|----------------|
| `JIM_ENCRYPTION_KEY_PATH` | Filesystem path where encryption keys are stored. When unset, JIM uses `/data/keys` (the `jim-keys-volume` Docker volume) if `/data` exists, otherwise the platform application-data directory. | *(unset)*: resolves to `/data/keys` in Docker | `/data/keys`   |

!!! danger "These keys must be backed up with the database"
    The database ciphertext cannot be decrypted without these keys. A database backup restored without its matching keys leaves every stored secret unrecoverable. See [Backup & Disaster Recovery](backup-recovery.md).

---

## :material-palette-outline: UI Theme

| Variable    | Description                                                                                                    | Default    |
|-------------|----------------------------------------------------------------------------------------------------------------|------------|
| `JIM_THEME` | Built-in colour theme for the JIM web interface. Valid values: `purple`, `black`, `blended-nav`, `future-minimal`, `navy-o5`, `navy-o6`. | `navy-o6`  |

---

## :material-swap-horizontal: Reverse Proxy

| Variable              | Description                                                                                                                                                                                                                     | Default              |
|------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------|
| `JIM_TRUSTED_PROXIES` | Comma-separated list of trusted proxy IP addresses and/or CIDR networks (e.g. `10.0.0.1,172.16.0.0/12`). When set, JIM trusts `X-Forwarded-For`/`X-Forwarded-Proto` headers from these sources, so the real client IP and scheme are recovered rather than the proxy's own. Set it whenever a reverse proxy or load balancer sits in front of JIM. It is required when the proxy forwards plain HTTP to JIM: without it, sign-in fails and the REST API refuses requests that carry a password (see [Trusting the Reverse Proxy](tls-and-reverse-proxy.md#trusting-the-reverse-proxy)). Also used for unauthenticated API [rate limiting](../api/rate-limiting.md), the security audit log, logging, and HTTPS redirection. | *(unset)*: forwarded headers are not trusted; the connecting socket's address is used as-is |

!!! warning "Only set this behind a trusted reverse proxy"
    Trusting forwarded headers from an address you do not control lets a client spoof its own IP, defeating IP-based rate limiting and polluting logs. Only list proxies (or the proxy network) that terminate connections in front of JIM.

---

## :material-key-chain: Infrastructure API Key

| Variable                    | Description                                                                                                                                                                                      | Default  |
|-----------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------|
| `JIM_INFRASTRUCTURE_API_KEY` | Creates an API key on startup for automated configuration (CI/CD). The key must start with `jim_ak_` and be at least 32 characters. Has the Administrator role and works for 24 hours; when it has expired, restarting JIM renews it for another 24 hours. A disabled key stays disabled. Remove the variable once setup is done, and delete the key: while the variable is set, every restart creates the key again if it is missing. | *(none)* |

Generate a key with:

```bash
openssl rand -hex 32 | sed 's/^/jim_ak_/'
```

To have the installer set it, give it in the installer's environment: it writes the key to `.env` on Docker, and to the Podman secret `jim-secrets` on Podman. It stops before installing anything when the key does not start with `jim_ak_` or is shorter than 32 characters, since JIM would not create it.

---

## :material-download-outline: Installer

`setup.sh` asks a question only when its answer is not already in its environment, so an installation can run unattended. Set the variables below, the identity provider's settings under [SSO / Authentication](#sso-authentication), and any database, port, proxy or API key settings from the sections above, and the installer asks nothing it already knows. Where a description names a flag, the flag does the same as the variable.

| Variable | Description | Default |
|----------|-------------|---------|
| `JIM_INSTALL_DIR` | The folder JIM is installed in | `/opt/jim` when run as root, otherwise `./jim` |
| `JIM_SETUP_RUNTIME` | `docker` or `podman`; the same as `--runtime` | The one installed; asks if both are |
| `JIM_SETUP_DB_MODE` | `bundled` for the bundled PostgreSQL, or `external` for your own server (with `JIM_DB_HOSTNAME`) | Asks |
| `JIM_SETUP_TLS_MODE` | `generate` to create a certificate authority and a server certificate, or `provided` to install your organisation's certificate | Asks |
| `JIM_SETUP_TLS_NAMES` | The DNS names and IP addresses users reach JIM at, comma-separated, for a generated certificate | Asks, suggesting this host's name and address |
| `JIM_SETUP_TLS_CERT_FILE` | Your organisation's PEM certificate, followed by any intermediate CA certificates. Required with `provided` | |
| `JIM_SETUP_TLS_KEY_FILE` | The certificate's unencrypted PEM private key. Required with `provided` | |
| `JIM_SETUP_AUTO_START` | `true` to start JIM without asking | Asks |
| `JIM_SETUP_PODMAN_ROOTLESS` | `true` to run JIM rootless on Podman; the same as `--rootless` | `false` |
| `JIM_SETUP_PODMAN_ACCOUNT` | The account that runs JIM rootless, with `--rootless` | `jim` |
| `JIM_SETUP_OPEN_FIREWALL` | `true` or `false`: open the HTTPS port in firewalld, when it is running. Podman only | Asks |
| `JIM_SETUP_FIX_APPARMOR` | `true` or `false`: on a host whose AppArmor profiles for Podman stop JIM's containers using the network, or stop their processes signalling one another (Ubuntu 24.04), add the rules JIM needs. Podman only | Asks |
| `JIM_SETUP_BACKUP_CONFIRMED` | `true`, with `--upgrade`: the database and encryption keys are backed up, so the upgrade does not ask | Asks, and an answer of no cancels the upgrade |

---

## :material-speedometer: Performance Tuning

| Variable               | Description                                                                                                                                                              | Default         |
|------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------|
| `JIM_WRITE_PARALLELISM` | Maximum concurrent PostgreSQL connections used by the parallel batch writer during bulk write operations. Higher values speed up large imports but consume more database connections. | CPU core count  |

!!! info
    The worker uses a 300-second command timeout for bulk database operations (PostgreSQL default is 30 seconds). This accommodates large imports where individual batch writes may take longer than the default timeout. No configuration is required; it is set automatically.
