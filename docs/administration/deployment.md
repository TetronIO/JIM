---
title: Deployment
---

# Deployment Guide

This guide covers deploying JIM to a production environment, including prerequisites, architecture, installation procedures for both connected and air-gapped environments, TLS configuration, and operational guidance.

!!! tip "Quick Start"
    The [Getting Started](../getting-started/index.md) guide gets you running in under five minutes. This page covers production hardening, reverse proxies, upgrades, and operational best practices.

## Prerequisites

### Hardware Requirements

| Component | Minimum    | Recommended                                      |
|-----------|------------|--------------------------------------------------|
| CPU       | 2 cores    | 4+ cores                                         |
| RAM       | 4 GB       | 8+ GB                                            |
| Storage   | 20 GB      | 50+ GB (depends on identity data volume)         |

Storage scales with the number of identity objects and the frequency of synchronisation runs (change history, logs, etc.).

#### Memory Scaling by Identity Object Count

The worker service loads all objects from a connector into memory during full import processing (CSOs, attribute values, RPEIs, duplicate detection structures). Memory requirements scale linearly with the number of objects in the largest connected system.

These figures are for the **host machine** (or VM) running the Docker stack -- they must cover the operating system, all JIM containers, and the database.

| Connected System Size       | Minimum Host RAM | Recommended Host RAM |
|-----------------------------|------------------|----------------------|
| Up to 10,000 objects        | 4 GB             | 8 GB                 |
| 10,000 -- 50,000 objects    | 8 GB             | 16 GB                |
| 50,000 -- 100,000 objects   | 20 GB            | 24 GB                |
| 100,000+ objects            | 24 GB            | 32 GB                |

!!! info "Why large imports need significant memory"
    During a full import, the worker loads all imported objects with their attributes into memory before the save phase begins (for duplicate detection, deletion detection, and reference resolution). A full import of 100,000 objects with 20 attributes each produces a worker peak working set of approximately 2.3 GB. The database requires an additional 1--2 GB during bulk inserts. Combined with the web, scheduler, and operating system overhead, total system memory consumption reaches 8--10 GB for 100K objects.

!!! note
    These requirements apply to the largest single full import. If you have multiple connected systems of 50K objects each but import them sequentially (not concurrently), size for 50K, not the sum. Delta imports process only changed objects and require significantly less memory.

### Software Requirements

- **Docker Engine** 20.10+ with Compose v2
- **An OIDC identity provider** (e.g. Entra ID, Keycloak, AD FS) -- see [SSO Setup](sso-setup.md)

### Network Requirements

JIM's services communicate internally over a Docker bridge network (`jim-network`). The only port that needs to be exposed externally is the web/API port on `jim.web` (container port `80`).

| Direction | Port                                               | Purpose                                        |
|-----------|----------------------------------------------------|-------------------------------------------------|
| Inbound   | Your chosen host port (e.g. 443 via reverse proxy) | Web UI and REST API                             |
| Outbound  | Varies                                             | OIDC provider, LDAP targets, file shares, etc. |

In air-gapped environments, no outbound connectivity is required after initial deployment.

---

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

| Service            | Description                                                                           |
|--------------------|---------------------------------------------------------------------------------------|
| **jim.web**        | Blazor Server UI and REST API (`/api/` with Swagger at `/api/swagger`)                |
| **jim.worker**     | Processes import, synchronisation, and export tasks                                   |
| **jim.scheduler**  | Triggers synchronisation runs on cron or interval schedules                           |
| **jim.database**   | PostgreSQL 18 (optional bundled container)                                            |

### Docker Volumes

| Volume             | Purpose                          |
|--------------------|----------------------------------|
| `jim-db-volume`    | PostgreSQL data (bundled DB only) |
| `jim-logs-volume`  | Application and database logs     |
| `jim-keys-volume`  | Encryption keys                   |

### Bundled vs External PostgreSQL

|                    | Bundled                                       | External                                    |
|--------------------|-----------------------------------------------|---------------------------------------------|
| **Setup**          | Automatic -- included in Docker stack         | You manage PostgreSQL separately            |
| **Started with**   | `--profile with-db` flag                      | No profile flag needed                      |
| **Best for**       | Evaluations, small deployments                | Production, existing DBA team               |
| **Backup**         | Docker volume snapshots                       | Your existing DB backup tooling             |
| **Tuning**         | Default settings in compose file              | Full control                                |

!!! tip
    Start with bundled PostgreSQL for evaluation. Migrate to external for production workloads where you need backup, high availability, or monitoring integration.

---

## Connected Deployment

### Option 1 -- Automated Setup (Recommended)

The setup script downloads compose files, walks you through configuration, and starts JIM:

```bash
curl -fsSL https://tetron.io/jim/get | bash
```

Or download and inspect first:

```bash
curl -fsSL -o setup.sh https://tetron.io/jim/get
less setup.sh
bash setup.sh
```

The script will:

1. Check prerequisites (Docker, Compose v2, curl)
2. Auto-detect the latest release
3. Download compose files and environment template
4. Walk you through database and SSO configuration
5. Optionally start JIM

### Option 2 -- Manual Setup

```bash
mkdir jim && cd jim

# Download compose files and environment template
curl -fsSL -o docker-compose.yml \
  https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml
curl -fsSL -o docker-compose.production.yml \
  https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.production.yml
curl -fsSL -o .env \
  https://github.com/TetronIO/JIM/releases/latest/download/.env.example

# Edit .env -- configure SSO and database settings (see Configuration Reference)

# Start with bundled PostgreSQL
docker compose -f docker-compose.yml -f docker-compose.production.yml \
  --profile with-db up -d

# Or with external PostgreSQL (set JIM_DB_HOSTNAME in .env)
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d
```

---

## Air-Gapped Deployment

Each [release](https://github.com/TetronIO/JIM/releases) includes a bundle (`jim-release-X.Y.Z.tar.gz`) containing pre-built Docker images, compose files, and installation instructions. This section covers the full air-gapped deployment procedure.

### Bundle Contents

```
jim-release-X.Y.Z/
+-- docker-images/
|   +-- jim-web.tar           # Docker image for web/API service
|   +-- jim-worker.tar        # Docker image for worker service
|   +-- jim-scheduler.tar     # Docker image for scheduler service
|   +-- postgres-18.tar       # PostgreSQL image (if included)
+-- compose/
|   +-- docker-compose.yml
|   +-- docker-compose.override.yml
|   +-- docker-compose.production.yml
|   +-- .env.example
+-- powershell/
|   +-- JIM/                  # PowerShell module directory
+-- docs/
|   +-- README.md
|   +-- CHANGELOG.md
|   +-- INSTALL.md
+-- checksums.sha256          # SHA256 checksums for verification
+-- README.txt                # Quick start guide
```

### Prerequisites

Before deploying JIM in an air-gapped environment, ensure you have:

- **Docker Engine** (20.10+) and **Docker Compose** (v2+) installed
- **PostgreSQL 18** -- either as a container or external database server
- A DNS name or IP address for the JIM server
- TLS certificates if enabling HTTPS (recommended for production)
- An OIDC identity provider accessible from the air-gapped network (e.g. AD FS, Keycloak)

### Step 1: Transfer and Verify the Bundle

```bash
# Transfer jim-release-X.Y.Z.tar.gz via approved media (USB, DVD, etc.)

# Extract the bundle
tar -xzf jim-release-X.Y.Z.tar.gz
cd jim-release-X.Y.Z

# Verify checksums
sha256sum -c checksums.sha256
```

### Step 2: Load Docker Images

```bash
# Load JIM images
docker load -i docker-images/jim-web.tar
docker load -i docker-images/jim-worker.tar
docker load -i docker-images/jim-scheduler.tar

# If using bundled PostgreSQL (optional)
docker load -i docker-images/postgres-18.tar
```

### Step 3: Set Up PostgreSQL

**Option A: Use the bundled PostgreSQL container** (simpler, suitable for smaller deployments)

```bash
# Start with bundled database
docker compose --profile with-db up -d
```

**Option B: Use an external PostgreSQL server** (recommended for production)

1. Create a database and user:

    ```sql
    CREATE DATABASE jim;
    CREATE USER jim WITH ENCRYPTED PASSWORD 'your_secure_password';
    GRANT ALL PRIVILEGES ON DATABASE jim TO jim;
    ```

2. Update `.env` with your database connection details (see [Configuration Reference](configuration.md))

3. Start JIM without the database profile:

    ```bash
    docker compose up -d
    ```

### Step 4: Configure Environment

```bash
cd compose
cp .env.example .env
```

Edit `.env` with your settings. See [Configuration Reference](configuration.md) for the full list of variables.

### Step 5: Configure DNS

Ensure your JIM server is resolvable by name in your network:

1. Add a DNS A record pointing to your JIM server's IP address, or
2. Add an entry to `/etc/hosts` on client machines:

    ```
    192.168.1.100  jim.your-domain.local
    ```

The OIDC redirect URIs configured in your identity provider must match the JIM server's accessible URL.

### Step 6: Configure File Connector Volumes (Optional)

If you plan to use the File Connector to import/export CSV files, configure a volume mount:

1. Create a directory on the host:

    ```bash
    mkdir -p /opt/jim/connector-files
    ```

2. Add a volume mount to `docker-compose.yml` for the `jim.worker` service:

    ```yaml
    jim.worker:
      volumes:
        - jim-logs-volume:/var/log/jim
        - /opt/jim/connector-files:/var/connector-files
    ```

3. Place CSV files in `/opt/jim/connector-files/` on the host

4. In JIM, configure the File Connector with the container path: `/var/connector-files/yourfile.csv`

### Step 7: Start Services

```bash
# With bundled PostgreSQL
docker compose --profile with-db up -d

# With external PostgreSQL
docker compose up -d

# Check all services are running
docker compose ps

# View logs
docker compose logs -f
```

### Step 8: Verify Startup

JIM automatically applies any pending database migrations on first startup -- no manual migration step is required. Watch the logs to confirm:

```bash
docker compose logs jim.worker --tail=50
```

Look for "Database migrations applied" or "Database is up to date" in the worker logs. The web interface will show a loading screen until migrations complete and the worker signals readiness.

!!! warning "Troubleshooting"
    If migrations fail (e.g. due to a permissions issue), the worker logs will contain the full error. Fix the underlying issue and restart the services.

### Step 9: Access JIM

1. Open your browser to `https://jim.your-domain.local` (or `http://localhost:5200` if no TLS)
2. Log in with your SSO credentials
3. The initial admin user (configured via `JIM_SSO_INITIAL_ADMIN`) will have full access

---

## TLS and Reverse Proxy

The JIM containers serve HTTP on port 80 internally. For production, place a reverse proxy in front to handle TLS termination.

!!! important
    Blazor Server uses WebSockets (SignalR). Your reverse proxy **must** support WebSocket connections, or the UI will fall back to long polling with degraded performance.

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
docker compose -f docker-compose.yml -f docker-compose.production.yml \
  -f docker-compose.ports.yml --profile with-db up -d
```

Alternatively, add the `ports` mapping directly to `docker-compose.production.yml` after downloading it.

---

## Health Monitoring

### Health Endpoints

| Endpoint                  | Purpose                                         |
|---------------------------|-------------------------------------------------|
| `/api/v1/health`          | Basic liveness check                            |
| `/api/v1/health/ready`    | Readiness check (includes database connectivity)|

The `jim.web` container includes a Docker healthcheck using the readiness endpoint.

The `jim.worker` and `jim.scheduler` containers use file-based healthcheck monitoring. Each service writes a heartbeat file periodically during normal operation, and the Docker healthcheck verifies the file is recent. This means `docker compose ps` and orchestrators like Docker Swarm or Kubernetes can detect when a worker or scheduler has stalled, even if the process itself has not exited.

### Logging

JIM writes structured logs to the `jim-logs-volume` Docker volume. Configure log level via `.env`:

```
JIM_LOG_LEVEL=Information
```

Valid levels: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.

View logs with Docker Compose:

```bash
# Follow all service logs
docker compose -f docker-compose.yml -f docker-compose.production.yml logs -f

# View recent logs for a specific service
docker compose -f docker-compose.yml -f docker-compose.production.yml \
  logs jim.web --tail=100
```

JIM also includes a Logs page in the web UI for viewing application and database logs.

---

## PowerShell Module

JIM includes a cross-platform PowerShell module for scripting, automation, and Identity as Code (IDaC). The module works on Windows, macOS, and Linux and requires PowerShell 7.0+.

### Connected Installation

Install directly from the [PowerShell Gallery](https://www.powershellgallery.com/packages/JIM/):

```powershell
Install-Module -Name JIM
```

To update to a newer version:

```powershell
Update-Module -Name JIM
```

### Air-Gapped Installation

There are two options for installing the module in disconnected environments.

#### Option 1 -- From the Release Bundle (Recommended)

Each release bundle (`jim-release-X.Y.Z.tar.gz`) includes the module pre-packaged in `powershell/JIM/`. After extracting the bundle, copy the module to a PSModulePath directory:

```powershell
# Windows
Copy-Item -Recurse ./powershell/JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"

# Linux / macOS
Copy-Item -Recurse ./powershell/JIM "~/.local/share/powershell/Modules/"
```

#### Option 2 -- From the PowerShell Gallery via Save-Module

On a connected machine, use `Save-Module` to download the module to a local directory without installing it:

```powershell
Save-Module -Name JIM -Path C:\Modules
```

Transfer the `C:\Modules\JIM\` directory to the disconnected environment, then copy it to a PSModulePath directory:

```powershell
# Windows
Copy-Item -Recurse C:\Transfer\JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"

# Linux / macOS
Copy-Item -Recurse /mnt/transfer/JIM "~/.local/share/powershell/Modules/"
```

### Verifying the Installation

```powershell
Import-Module JIM
Get-Module JIM    # Verify the module loaded and check the version
```

### Connecting to JIM

```powershell
# Interactive -- opens browser for SSO sign-in
Connect-JIM -Url "https://jim.example.com"

# Automation -- use an API key (recommended for scripts and CI/CD)
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

# Verify the connection
Test-JIMConnection
```

---

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
- [ ] PowerShell module installed and connected (if using automation/IDaC)

### Air-Gapped Network Checklist

For air-gapped deployments, also verify:

- [ ] All Docker images loaded successfully (`docker images | grep jim`)
- [ ] PostgreSQL is accessible and migrations applied
- [ ] SSO/OIDC identity provider is accessible from JIM server
- [ ] DNS resolves JIM server name correctly
- [ ] TLS certificates are valid and trusted (if using HTTPS)
- [ ] Firewall allows traffic on required ports (5200/HTTP, 443/HTTPS, 5432/PostgreSQL)
- [ ] File connector volumes mounted (if using File Connector)
- [ ] Initial admin user can log in
- [ ] Logs are being written to configured path
