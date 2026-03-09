# JIM Deployment Guide

This guide covers deploying JIM to a production environment. It consolidates prerequisites, configuration, and operational guidance into a single reference.

**Quick start vs this guide:** The [README Quick Start](../README.md#quick-start) gets you running in under 5 minutes. This guide covers production hardening, reverse proxies, upgrades, and operational best practices.

## Prerequisites

### Hardware Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| CPU | 2 cores | 4+ cores |
| RAM | 4 GB | 8+ GB |
| Storage | 20 GB | 50+ GB (depends on identity data volume) |

Storage scales with the number of identity objects and the frequency of synchronisation runs (change history, logs, etc.).

#### Memory Scaling by Identity Object Count

The worker service loads all objects from a connector page into memory during import processing (CSOs, attribute values, RPEIs, duplicate detection structures). Memory requirements scale linearly with the number of objects in the largest connected system:

| Connected System Size | Minimum RAM (Stack) | Recommended RAM (Stack) |
|----------------------|--------------------|-----------------------|
| Up to 10,000 objects | 4 GB | 8 GB |
| 10,000 - 50,000 objects | 8 GB | 12 GB |
| 50,000 - 100,000 objects | 12 GB | 16 GB |
| 100,000+ objects | 16 GB | 24 GB |

These figures cover the entire Docker stack (web, worker, scheduler, database). The worker is the primary memory consumer during sync operations — a full import of 100K objects with 20 attributes each requires approximately 1.5 GB of worker memory at peak. The database also requires additional memory during bulk insert operations.

**Note:** These requirements apply to the largest single import run. If you have multiple connected systems of 50K objects each but import them sequentially (not concurrently), size for 50K, not the sum.

### Software Requirements

- **Docker Engine** 20.10+ with Compose v2
- **An OIDC identity provider** (Entra ID, Keycloak, AD FS, etc.) — see [SSO Setup Guide](SSO_SETUP_GUIDE.md)

### Network Requirements

JIM's services communicate internally over a Docker bridge network (`jim-network`). The only port that needs to be exposed externally is the web/API port on `jim.web` (container port `80`).

| Direction | Port | Purpose |
|-----------|------|---------|
| Inbound | Your chosen host port (e.g., 443 via reverse proxy) | Web UI and REST API |
| Outbound | Varies | OIDC provider, LDAP targets, file shares, etc. |

In air-gapped environments, no outbound connectivity is required after initial deployment.

## Architecture Overview

JIM runs as a Docker Compose stack with four services:

```
+------------------+     +------------------+     +------------------+
|     jim.web      |     |   jim.worker     |     |  jim.scheduler   |
|  (UI + REST API) |     | (sync processor) |     | (cron triggers)  |
+--------+---------+     +--------+---------+     +--------+---------+
         |                        |                        |
         +------------------------+------------------------+
                                  |
                          +-------+--------+
                          |  jim.database  |
                          |  (PostgreSQL)  |
                          +----------------+
```

- **jim.web** — Blazor Server UI and REST API (`/api/` with Swagger at `/api/swagger`)
- **jim.worker** — Processes import, sync, and export tasks
- **jim.scheduler** — Triggers synchronisation runs on cron or interval schedules
- **jim.database** — PostgreSQL 18 (optional bundled container)

### Docker Volumes

| Volume | Purpose |
|--------|---------|
| `jim-db-volume` | PostgreSQL data (bundled DB only) |
| `jim-logs-volume` | Application and database logs |
| `jim-keys-volume` | Encryption keys |

## Deployment Topology

### Bundled vs External PostgreSQL

| | Bundled | External |
|---|---------|----------|
| **Setup** | Automatic — included in Docker stack | You manage PostgreSQL separately |
| **Started with** | `--profile with-db` flag | No profile flag needed |
| **Best for** | Evaluations, small deployments | Production, existing DBA team |
| **Backup** | Docker volume snapshots | Your existing DB backup tooling |
| **Tuning** | Default settings in compose file | Full control — see [Database Guide](DATABASE_GUIDE.md) |

**Recommendation:** Start with bundled for evaluation. Migrate to external for production workloads where you need backup/HA/monitoring integration.

## Connected Deployment

### Option 1 — Automated Setup (Recommended)

The setup script downloads compose files, walks you through configuration, and starts JIM:

```bash
curl -fsSL https://raw.githubusercontent.com/TetronIO/JIM/main/setup.sh | bash
```

Or download and inspect first:

```bash
curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/setup.sh
less setup.sh
bash setup.sh
```

The script will:
1. Check prerequisites (Docker, Compose v2, curl)
2. Auto-detect the latest release
3. Download compose files and environment template
4. Walk you through database and SSO configuration
5. Optionally start JIM

### Option 2 — Manual Setup

```bash
mkdir jim && cd jim

# Download compose files and environment template
curl -fsSL -o docker-compose.yml https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml
curl -fsSL -o docker-compose.production.yml https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.production.yml
curl -fsSL -o .env https://github.com/TetronIO/JIM/releases/latest/download/.env.example

# Edit .env — configure SSO and database settings (see sections below)

# Start with bundled PostgreSQL
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d

# Or with external PostgreSQL (set JIM_DB_HOSTNAME in .env)
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d
```

## Air-Gapped Deployment

Each [release](https://github.com/TetronIO/JIM/releases) includes a bundle (`jim-release-X.Y.Z.tar.gz`) containing pre-built Docker images, compose files, and installation instructions.

For the full air-gapped procedure (transferring images, loading into an isolated registry, verification), see [Release Process — Air-Gapped Deployments](RELEASE_PROCESS.md#air-gapped-deployments).

## Configuration Reference

All configuration is done through the `.env` file. The setup script configures these automatically; for manual setup, edit `.env` directly.

### Essential Settings

| Variable | Description | Example |
|----------|-------------|---------|
| `DOCKER_REGISTRY` | Container registry prefix | `ghcr.io/tetronio/` |
| `JIM_VERSION` | Release version tag | `0.3.0` |
| `JIM_DB_HOSTNAME` | Database hostname | `jim.database` (bundled) or your DB host |
| `JIM_DB_NAME` | Database name | `jim` |
| `JIM_DB_USERNAME` | Database username | `jim` |
| `JIM_DB_PASSWORD` | Database password | (generate a strong password) |
| `JIM_SSO_AUTHORITY` | OIDC authority URL | `https://login.microsoftonline.com/{tenant}/v2.0` |
| `JIM_SSO_CLIENT_ID` | OAuth client/application ID | (from your IdP) |
| `JIM_SSO_SECRET` | OAuth client secret | (from your IdP) |
| `JIM_SSO_API_SCOPE` | API scope for JWT auth | `api://{client-id}/access_as_user` |
| `JIM_SSO_CLAIM_TYPE` | JWT claim for user identity | `sub` |
| `JIM_SSO_MV_ATTRIBUTE` | Metaverse attribute to match | `Subject Identifier` |
| `JIM_SSO_INITIAL_ADMIN` | Claim value of initial admin | (your admin's sub claim) |

For the full list of variables with descriptions and examples, see [.env.example](../.env.example).

### SSO/OIDC Configuration

JIM requires an OIDC identity provider for authentication. The [SSO Setup Guide](SSO_SETUP_GUIDE.md) has step-by-step instructions for:
- Microsoft Entra ID
- AD FS
- Keycloak

The guide also covers testing your configuration and security considerations.

## TLS and Reverse Proxy

The JIM containers serve HTTP on port 80 internally. For production, place a reverse proxy in front to handle TLS termination.

### nginx Example

```nginx
server {
    listen 443 ssl http2;
    server_name jim.example.com;

    ssl_certificate     /etc/ssl/certs/jim.example.com.pem;
    ssl_certificate_key /etc/ssl/private/jim.example.com.key;
    ssl_protocols       TLSv1.2 TLSv1.3;

    location / {
        proxy_pass http://localhost:5200;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSocket support (required for Blazor Server)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

### Port Mapping

The base `docker-compose.yml` does not expose ports externally. You need to add a port mapping. Create a `docker-compose.ports.yml` override:

```yaml
services:
  jim.web:
    ports:
      - "5200:80"
```

Then include it in your compose command:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml -f docker-compose.ports.yml --profile with-db up -d
```

Alternatively, add the `ports` mapping directly to `docker-compose.production.yml` after downloading it.

**Important:** Blazor Server uses WebSockets (SignalR). Your reverse proxy must support WebSocket connections, or the UI will fall back to long polling with degraded performance.

## Database

For bundled PostgreSQL, the default settings in `docker-compose.yml` are suitable for small deployments. For production tuning, connection pooling, and backup/restore procedures, see the [Database Guide](DATABASE_GUIDE.md).

Key recommendations:
- Set `shm_size` in your compose override to at least the `shared_buffers` value plus headroom
- Use [PGTune](https://pgtune.leopard.in.ua/) to generate settings matched to your server's resources
- Back up regularly — see [Database Guide — Backup and Restore](DATABASE_GUIDE.md#backup-and-restore)

## Upgrades

### Upgrade Procedure

1. **Back up your database** before upgrading (see [Database Guide — Backup and Restore](DATABASE_GUIDE.md#backup-and-restore))

2. **Update the version** in `.env`:
   ```
   JIM_VERSION=0.4.0
   ```

3. **Pull the new images:**
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.production.yml pull
   ```

4. **Restart the stack:**
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d
   ```

   JIM applies database migrations automatically on startup.

5. **Verify health:**
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.production.yml ps
   curl -f http://localhost:5200/api/v1/health/ready
   ```

### Air-Gapped Upgrades

Download the new release bundle, load updated images, update `.env`, and restart. See [Release Process — Air-Gapped Deployments](RELEASE_PROCESS.md#air-gapped-deployments).

## Monitoring and Logging

### Health Endpoints

| Endpoint | Purpose |
|----------|---------|
| `/api/v1/health` | Basic liveness check |
| `/api/v1/health/ready` | Readiness check (includes DB connectivity) |

The `jim.web` container includes a Docker healthcheck using the readiness endpoint.

### Logging

JIM writes structured logs to the `jim-logs-volume` Docker volume. Configure log level via `.env`:

```
JIM_LOG_LEVEL=Information
```

Valid levels: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.

View logs with Docker Compose:
```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs -f
docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.web --tail=100
```

JIM also includes a Logs page in the web UI for viewing application and database logs.

## Troubleshooting

### Services fail to start

Check logs for the failing service:
```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.web --tail=50
```

Common causes:
- **Database connection refused** — Verify `JIM_DB_HOSTNAME` is correct and the database is reachable
- **OIDC configuration error** — Check `JIM_SSO_AUTHORITY` is accessible from the container (not just from your browser)
- **Missing `.env`** — Ensure `.env` exists in the compose directory

### SSO login fails

- Verify your OIDC redirect URI matches JIM's URL (including port)
- Check the `/claims` page after logging in to verify claim values
- See [SSO Setup Guide — Testing Your Configuration](SSO_SETUP_GUIDE.md#testing-your-configuration)

### UI loads but is unresponsive

- Blazor Server requires a persistent WebSocket connection
- Verify your reverse proxy supports WebSockets (see TLS section above)
- Check for network equipment (load balancers, firewalls) that may terminate idle connections

### Database performance

- Use [PGTune](https://pgtune.leopard.in.ua/) to generate settings for your hardware
- See [Database Guide](DATABASE_GUIDE.md) for tuning parameters and slow query logging

## Production Readiness Checklist

Use this checklist before going live:

- [ ] TLS configured via reverse proxy (minimum TLS 1.2)
- [ ] Strong, unique database password set
- [ ] SSO configured and tested with your identity provider
- [ ] Initial admin user can log in and access the administration UI
- [ ] Database backup strategy in place and tested
- [ ] Log level set appropriately (`Information` for production)
- [ ] Health endpoint monitored by your alerting system
- [ ] Firewall rules restrict access to JIM's port to authorised networks
- [ ] Docker restart policy is `unless-stopped` (set by production override)
- [ ] Upgrade procedure documented and tested in staging
