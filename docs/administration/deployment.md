---
title: Deployment
description: Install JIM in production with Docker or Podman, connected or air-gapped, and prepare it for use.
type: how-to
audience: implementer
validated: v0.16.0
---

# Deployment

This guide installs JIM in production, on Docker or Podman, connected to the internet or air-gapped, and covers its architecture, monitoring and readiness checklists. [Prerequisites](../getting-started/prerequisites.md) lists what to have in place first, [TLS and Reverse Proxy](tls-and-reverse-proxy.md) covers its certificate, and [Running on Podman](podman.md) what is particular to Podman, including rootless installations.

!!! tip "Quick Start"
    The [Getting Started](../getting-started/index.md) guide gets you running in under five minutes, for evaluation.

## Prerequisites

[Prerequisites](../getting-started/prerequisites.md) lists what JIM needs: a container runtime, an identity provider, a TLS certificate and a PostgreSQL database, with the hardware to size for the number of objects you synchronise and the one port to open.

## Architecture Overview

JIM runs as four services: a Docker Compose stack on Docker, or two pods on Podman (see [How JIM Runs on Podman](podman.md#how-jim-runs-on-podman)).

--8<-- "assets/diagrams/deployment-stack.svg"

<p class="jim-diagram-caption">All three JIM services coordinate through PostgreSQL; the Scheduler queues tasks and the Worker polls them, so no service calls another directly. The bundled database container is optional; JIM can use an external PostgreSQL server instead.<span class="jimdg-caption-motion"> Moving dots trace database traffic.</span></p>

| Service            | Description                                                                           |
|--------------------|---------------------------------------------------------------------------------------|
| **jim.web**        | Web portal and REST API (`/api/`)                                                     |
| **jim.worker**     | Processes import, synchronisation, and export tasks                                   |
| **jim.scheduler**  | Triggers synchronisation runs on cron or interval schedules                           |
| **jim.database**   | PostgreSQL 18 (optional bundled container)                                            |

On Podman, the containers are named `jim-web`, `jim-worker`, `jim-scheduler` and `jim-database-postgres`.

### Volumes

| Volume             | Purpose                          |
|--------------------|----------------------------------|
| `jim-db-volume`    | PostgreSQL data (bundled DB only) |
| `jim-logs-volume`  | Application and database logs     |
| `jim-keys-volume`  | Encryption keys                   |

!!! danger "Back up the key volume with the database"
    `jim-keys-volume` holds the encryption keys that protect every stored secret (Connected System credentials, the SSO secret, Schedule SQL-step connection strings). The database ciphertext cannot be decrypted without these keys, so a database backup restored without its matching keys leaves every secret unrecoverable. Back up the key volume and the database together, as a matched pair. See [Backup & Disaster Recovery](backup-recovery.md).

### Bundled vs External PostgreSQL

|                    | Bundled                                       | External                                    |
|--------------------|-----------------------------------------------|---------------------------------------------|
| **Setup**          | Automatic: included in the installation      | You manage PostgreSQL separately            |
| **Started with**   | `--profile with-db` (Docker), or the `jim-database` pod (Podman) | JIM alone                |
| **Best for**       | Evaluations, small deployments                | Production, existing DBA team               |
| **Backup**         | Volume snapshots                              | Your existing DB backup tooling             |
| **Tuning**         | Sized to the host's memory by the installer ([Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)) | Full control |

!!! tip
    Start with bundled PostgreSQL for evaluation. Migrate to external for production workloads where you need backup, high availability, or monitoring integration.

Start-up order does not matter: each JIM service waits up to five minutes for PostgreSQL to accept connections, logging each attempt, so JIM and its database server can be started or restarted independently. If a service keeps waiting, see [Troubleshooting](troubleshooting.md#a-service-logs-the-database-is-not-reachable-yet).

---

## Before You Install

Have these ready, whichever way you install:

- **A DNS name for the JIM server**, such as `jim.example.com`, with a DNS record pointing at the server. JIM's address is `https://jim.example.com`, on the standard HTTPS port, 443.
- **A client registration for JIM at your identity provider**, with these redirect URIs: `https://jim.example.com/signin-oidc` and `https://jim.example.com/signout-callback-oidc`. You need its authority URL, client ID and secret, API scope, and the claim value of the first administrator; the [SSO Setup Guide](sso-setup.md) covers each provider.
- **A certificate for JIM's name**, from your organisation's certificate authority, or let the installer create one (see [TLS and Reverse Proxy](#tls-and-reverse-proxy)).
- **PostgreSQL 18**: the bundled container needs nothing. For your own server, connected as a superuser such as `postgres`, create JIM's user, then its database, owned by that user:

    ```sql
    CREATE USER jim WITH ENCRYPTED PASSWORD 'your_secure_password';
    CREATE DATABASE jim OWNER jim;
    ```

    JIM's user must own the database: since PostgreSQL 15, only a database's owner can create tables in its `public` schema, and `GRANT ALL PRIVILEGES ON DATABASE` does not allow it. A database JIM's user does not own stops JIM on its first start; [Troubleshooting](troubleshooting.md#the-worker-stops-with-permission-denied-for-schema-public) shows how to correct one.

---

## Connected Deployment

### With the Installer (Recommended)

On the JIM server, as root:

```bash
curl -fsSL https://junctional.io/get | sudo bash
```

Or download it, read it, then run it:

```bash
curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/deploy/setup.sh
less setup.sh
sudo bash setup.sh
```

The installer uses Docker or Podman, whichever the server has. Where both are installed it asks which; to name one, pass `--runtime`:

```bash
curl -fsSL https://junctional.io/get | sudo bash -s -- --runtime podman
```

It downloads the latest release and installs it in `/opt/jim`; see [What the Installer Does](#what-the-installer-does). Then carry on at [After Installing](#after-installing).

### By Hand

On Podman, follow [Installing on Podman by Hand](podman-by-hand.md), with the Podman files from the latest release:

```bash
mkdir -p jim-podman && cd jim-podman
for f in jim.yaml jim-database.yaml jim-config.yaml jim-secrets.yaml jim.network jim.kube jim-database.kube; do
  curl -fsSLO "https://github.com/TetronIO/JIM/releases/latest/download/$f"
done
```

On Docker, as root:

```bash
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls && cd /opt/jim

# Download the compose files and environment template
curl -fsSL -o docker-compose.yml \
  https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml
curl -fsSL -o docker-compose.production.yml \
  https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.production.yml
curl -fsSL -o .env \
  https://github.com/TetronIO/JIM/releases/latest/download/default.env.example
```

1. Edit `.env`: set `DOCKER_REGISTRY=ghcr.io/tetronio/` and `JIM_VERSION` to the release's version, and the identity provider settings (see the [Configuration Reference](configuration.md)). For the bundled PostgreSQL, set `JIM_DB_HOSTNAME=jim.database` (the template's `localhost` is for development) and choose a strong `JIM_DB_PASSWORD`; for your own server, give its name and JIM's credentials. Then `chmod 600 .env`, since it holds secrets.
2. Put JIM's certificate and key in `tls/` (see [The Certificate](tls-and-reverse-proxy.md#the-certificate)).
3. Start JIM, leaving out `--profile with-db` if you use your own PostgreSQL server:

    ```bash
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      --profile with-db up -d
    ```

Then carry on at [After Installing](#after-installing).

---

## Air-Gapped Deployment

Each [release](https://github.com/TetronIO/JIM/releases) includes a bundle, `jim-release-X.Y.Z.tar.gz`, holding everything an installation needs: the images, the compose files and the installer. Nothing is downloaded while installing from it.

### Checking the Download

Download `jim-release-X.Y.Z.tar.gz.sha256` from the release too, and check the bundle against it before carrying it into the air-gapped site. In the folder holding both:

```bash
sha256sum -c jim-release-X.Y.Z.tar.gz.sha256   # Should print OK
```

On Windows, in PowerShell, this should print `True`:

```powershell
(Get-FileHash jim-release-X.Y.Z.tar.gz -Algorithm SHA256).Hash -eq (Get-Content jim-release-X.Y.Z.tar.gz.sha256).Split(' ')[0]
```

Once extracted, `checksums.sha256` inside the bundle checks each file in it, as below.

### Bundle Contents

```text
jim-release-X.Y.Z/
+-- setup.sh                  # The installer; run inside the bundle, it installs from it
+-- VERSION                   # The JIM version the bundle installs
+-- docker-images/
|   +-- jim-web.tar           # Image for the web/API service (Docker and Podman both load these)
|   +-- jim-worker.tar        # Image for the worker service
|   +-- jim-scheduler.tar     # Image for the scheduler service
|   +-- postgres-18.tar       # PostgreSQL image (if included)
|   +-- postgres-18.image-ids # Its image ID, which the installer checks on Docker's classic image store
+-- compose/
|   +-- docker-compose.yml
|   +-- docker-compose.production.yml
|   +-- .env.example
+-- podman/
|   +-- jim.yaml              # The JIM pod
|   +-- jim-database.yaml     # The bundled PostgreSQL pod
|   +-- jim-config.yaml       # Settings template
|   +-- jim-secrets.yaml      # Secrets template
|   +-- quadlet/              # systemd units: jim.kube, jim-database.kube, jim.network
+-- powershell/
|   +-- JIM/                  # PowerShell module directory
+-- docs/
|   +-- README.md
|   +-- CHANGELOG.md
|   +-- INSTALL.md            # These instructions, for reading offline
+-- checksums.sha256          # The checksum of every file in the bundle, by its path within it
+-- README.txt                # Quick start guide
```

### With the Installer (Recommended)

[Check the download](#checking-the-download), transfer the bundle to the JIM server by your organisation's approved method, then:

```bash
tar -xzf jim-release-X.Y.Z.tar.gz
cd jim-release-X.Y.Z

# Every line should end in OK
sha256sum -c checksums.sha256

sudo ./setup.sh
```

Run inside the bundle, the installer loads JIM's images from it and starts JIM without trying the internet; otherwise it works exactly as it does connected, on Docker or Podman (see [What the Installer Does](#what-the-installer-does)). Then carry on at [After Installing](#after-installing).

### By Hand

On Podman, follow [Installing on Podman by Hand](podman-by-hand.md), with the files in the bundle's `podman` folder and the images in its `docker-images` folder.

On Docker, as root, in the extracted bundle:

```bash
for f in docker-images/*.tar; do docker load -i "$f"; done
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
cp compose/docker-compose.yml compose/docker-compose.production.yml /opt/jim/
cp compose/.env.example /opt/jim/.env && chmod 600 /opt/jim/.env
```

1. Edit `/opt/jim/.env`: set `DOCKER_REGISTRY=ghcr.io/tetronio/` and `JIM_VERSION` to the version in the bundle's `VERSION` file, and the identity provider settings (see the [Configuration Reference](configuration.md)). For the bundled PostgreSQL, set `JIM_DB_HOSTNAME=jim.database` (the template's `localhost` is for development) and choose a strong `JIM_DB_PASSWORD`, and on a host with more than 4 GB of memory, size it as the installer would (see [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)); for your own server, give its name and JIM's credentials.
    For the bundled PostgreSQL on Docker's classic image store (`docker info` shows `Storage Driver: overlay2` rather than `overlayfs` with the containerd snapshotter), Docker drops the registry digest the compose file pins PostgreSQL by when it loads the image, so run the loaded image by its ID instead, after checking that ID is the one the bundle records:

    ```bash
    # In the extracted bundle
    image=$(docker load -i docker-images/postgres-18.tar | sed -n 's/^Loaded image: //p')
    id=$(docker image inspect -f '{{.Id}}' "$image")
    grep -qxF "$id" docker-images/postgres-18.image-ids && echo "JIM_DB_IMAGE=$id" >> /opt/jim/.env
    ```

    Nothing is added if the IDs differ; then extract the bundle again and check it with `sha256sum -c checksums.sha256`.
2. Put JIM's certificate and key in `/opt/jim/tls/` (see [The Certificate](tls-and-reverse-proxy.md#the-certificate)).
3. Start JIM, leaving out `--profile with-db` if you use your own PostgreSQL server. `--pull never` makes Docker report a missing image rather than try the internet:

    ```bash
    cd /opt/jim
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      --profile with-db up -d --pull never
    ```

Then carry on at [After Installing](#after-installing).

---

## What the Installer Does

`setup.sh` installs JIM on Docker or Podman, whichever the server has, asking which where both are installed (or pass `--runtime docker` or `--runtime podman`). It installs in `/opt/jim` (or `./jim` when not run as root) and asks, in turn:

1. **Database**: the bundled PostgreSQL, whose password it generates and whose memory it sizes to the host (see [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)), or your own server
2. **Identity provider**: the settings from your client registration
3. **HTTPS port**: 443 unless you choose another. It checks nothing else on the server already uses the port.
4. **Certificate**: one it creates, from a certificate authority of its own, or your organisation's certificate and key, which it checks before installing (see [TLS and Reverse Proxy](#tls-and-reverse-proxy))
5. **Reverse proxy or load balancer**: whether one sits in front of JIM, and if so its address, so that JIM trusts it to report each client's address

On Docker, it keeps `.env`, which holds the database password and your identity provider's client secret, readable by root only.

On Podman, it runs JIM as root (rootful) by default, as Docker does. Pass `--rootless` to run JIM under a dedicated account named `jim` instead, which it creates with lingering enabled, so that systemd starts JIM at boot with nobody logged in; read [Rootful or Rootless](podman.md#rootful-or-rootless) before choosing. It writes your settings to `jim-config.yaml`, stores the secrets in Podman's secret store, installs the systemd units, and offers to open the port in firewalld and, on Ubuntu 24.04, to add the AppArmor rules that let JIM's containers use the network and signal their own processes; rootless, it also lets unprivileged programs use port 443.

It then starts JIM, waits until JIM is ready, and prints JIM's address and what is left to do. If JIM is not ready within ten minutes, it names each of JIM's containers that is not running properly (the bundled database's included), with the end of its log, and exits with a failure code. It keeps a copy of itself in the installation, for looking after it later:

```bash
sudo /opt/jim/setup.sh --renew-certificate   # a certificate the installer created, before it expires
sudo /opt/jim/setup.sh --certificate         # change its names, or move to your organisation's certificate
```

On Docker, it also upgrades JIM, to the latest release or, run inside a newer release's bundle, to that release; see [Upgrading with the installer](upgrading.md#with-the-installer).

For automation, every question can be answered in advance with an environment variable; see [Installer](configuration.md#installer). Running the installer again on an existing installation asks before replacing its configuration, and keeps the bundled database's password, which the database was created with. If it stops before starting JIM, because it refuses a setting, a step fails or you press Ctrl+C, it puts the installation's files back as they were, so JIM carries on as before; a first installation that stops leaves nothing behind. Should the installer itself be killed part way, the files as they were stay in `.before-setup` in the installation's folder, and its next run stops and says what to do with them.

---

## Check it worked

JIM prepares its database on first start, with no manual step, and does not serve requests until that has finished. The installer waits for it; after a manual start, `jim.web` shows as `healthy` once JIM is ready:

=== "Docker"

    ```bash
    cd /opt/jim
    docker compose -f docker-compose.yml -f docker-compose.production.yml ps jim.web
    ```

    If it stays unhealthy, the worker's log names the problem, for example a database permission it lacks: `docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.worker`.

=== "Podman"

    ```bash
    sudo podman healthcheck run jim-web && echo "JIM is ready"
    ```

    If it does not become ready, the worker's log names the problem, for example a database permission it lacks: `sudo podman logs jim-worker`. For a rootless installation, see [Rootless commands](podman.md#rootless-commands).

## After Installing

1. **Trust the certificate authority**, if the installer created JIM's certificate: add `/opt/jim/tls/ca.crt` to the trusted root certificate authorities of every machine whose browser or tools use JIM (see [Distributing the certificate authority](tls-and-reverse-proxy.md#distributing-the-certificate-authority)). Until then, browsers warn about JIM's certificate.
2. **Open JIM** at `https://jim.example.com` and sign in as the initial administrator (the claim value in `JIM_SSO_INITIAL_ADMIN`), who has full access.

!!! warning "Always name the compose files"
    Pass the same `-f` files (and `--profile`) to every `docker compose` command for this deployment, including `stop`, `pull` and upgrades. Without `-f`, Docker Compose loads `docker-compose.yml` alone, which leaves out the production settings, and silently adds any `docker-compose.override.yml` it finds in the directory.

### File Connector Storage (Optional)

The File Connector ships pre-configured to read and write at `/connector-files` inside the container, backed by a Docker-managed volume named `jim-connector-files-volume`. **No setup is required for the default case**: start the stack and the volume is created automatically with correct ownership.

To put files in or pull them out:

=== "Docker"

    ```bash
    # Push an import file into the volume, as the JIM runtime user, so that JIM can rewrite it later
    docker exec -i -u app jim.worker sh -c 'cat > /connector-files/Users.csv' < ./Users.csv

    # Pull an exported file out
    docker cp jim.worker:/connector-files/Exports.csv ./Exports.csv
    ```

=== "Podman"

    ```bash
    # Push an import file into the volume, as the JIM runtime user, so that JIM can rewrite it later
    sudo podman exec -i jim-worker sh -c 'cat > /connector-files/Users.csv' < ./Users.csv

    # Pull an exported file out, as a stream, so that the copy is yours rather than root's
    sudo podman cp jim-worker:/connector-files/Exports.csv - | tar -xf -
    ```

    For a rootless installation, see [Rootless commands](podman.md#rootless-commands).

Then configure the File Connector's **File Path** setting as `/connector-files/Users.csv`.

If you need to integrate with an external system that writes to a fixed network location (e.g. an SMB or NFS share), bind-mount that path over a subdirectory of `/connector-files`. See the [JIM File Connector documentation](../connectors/jim-file-connector.md#file-access) for the full pattern, including the UID 1654 ownership requirement for bind-mounted host paths, and what differs on Podman.

---

## TLS and Reverse Proxy

JIM serves HTTPS itself, with a certificate for its host name that the installer creates or that your organisation's certificate authority issues. [TLS and Reverse Proxy](tls-and-reverse-proxy.md) covers the certificate, renewing and replacing it, running JIM behind a reverse proxy or load balancer, and changing its port.

## Health Monitoring

### Health Endpoints

| Endpoint                  | Purpose                                         |
|---------------------------|-------------------------------------------------|
| `/api/v1/health`          | Basic liveness check                            |
| `/api/v1/health/ready`    | Readiness check (includes database connectivity)|
| `/api/v1/system/health`   | Health of the Worker's synchronisation loop, its Password Delivery Service and the Scheduler, from their database heartbeats (requires the Administrator role) |

The `jim.web` container includes a Docker healthcheck using the readiness endpoint. The two unauthenticated endpoints answer for the web tier only; `system/health` is how the three background services are observed from outside the portal, and the same report is shown on **Administration > Operations** and returned by `Get-JIMServiceHealth`. The `jim.worker` container hosts two of them, the synchronisation loop and the Password Delivery Service, and each reports on its own, so a Worker whose password delivery has stopped while imports still run is visible as exactly that. See [Operations > Service Health](../configuration/operations.md#service-health).

The `jim.worker` and `jim.scheduler` containers use file-based healthcheck monitoring. Each service writes a heartbeat file periodically during normal operation, and the Docker healthcheck verifies the file is recent. This means `docker compose ps` and any monitoring that reads container health can detect when a worker or scheduler has stalled, even if the process itself has not exited.

On Podman, the same checks show in `podman ps`, and Podman 5 restarts a service whose check keeps failing; see [Health checks and restarts](podman.md#health-checks-and-restarts).

### Logging

JIM writes structured logs to the `jim-logs-volume` volume. Configure log level via `.env` (Docker) or `jim-config.yaml` (Podman):

```text
JIM_LOG_LEVEL=Information
```

Valid levels: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.

View logs with Docker Compose, or with Podman as the account that runs JIM:

=== "Docker"

    ```bash
    # Follow all service logs
    docker compose -f docker-compose.yml -f docker-compose.production.yml logs -f

    # View recent logs for a specific service
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      logs jim.web --tail=100
    ```

=== "Podman"

    ```bash
    # Follow a service's logs
    sudo podman logs -f jim-web

    # View recent logs for a specific service
    sudo podman logs --tail=100 jim-worker
    ```

    For a rootless installation, see [Rootless commands](podman.md#rootless-commands).

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

#### Option 1: From the Release Bundle (Recommended)

Each release bundle (`jim-release-X.Y.Z.tar.gz`) includes the module pre-packaged in `powershell/JIM/`. After extracting the bundle, copy the module to a PSModulePath directory:

```powershell
# Windows
Copy-Item -Recurse ./powershell/JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"

# Linux / macOS
Copy-Item -Recurse ./powershell/JIM "~/.local/share/powershell/Modules/"
```

#### Option 2: From the PowerShell Gallery via Save-Module

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
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"

# Verify the connection
Test-JIMConnection
```

---

## Production Readiness Checklist

Use this checklist before going live:

- [ ] HTTPS certificate issued for every name users reach JIM at, and trusted by their browsers (your organisation's certificate, or the installer's certificate authority distributed)
- [ ] Certificate renewal scheduled before the expiry date (`openssl x509 -in tls/tls.crt -noout -enddate`)
- [ ] Strong, unique database password set
- [ ] SSO configured and tested with your identity provider
- [ ] Initial admin user can log in and access the administration UI
- [ ] Database backup strategy in place and tested
- [ ] Encryption keys (`jim-keys-volume` / `JIM_ENCRYPTION_KEY_PATH`) backed up alongside the database, as a matched pair (see [Backup & Disaster Recovery](backup-recovery.md))
- [ ] Full restore (database plus keys) rehearsed, with a Connected System confirmed to reconnect
- [ ] Log level set appropriately (`Information` for production)
- [ ] Health endpoint monitored by your alerting system
- [ ] Firewall rules restrict access to JIM's port to authorised networks
- [ ] Docker restart policy is `unless-stopped` (set by production override), or on Podman, `jim.service` starts at boot (`sudo systemctl is-enabled jim.service` reports `generated`; rootless, `jim-systemctl is-enabled jim.service`, as [Rootless commands](podman.md#rootless-commands) defines it)
- [ ] Upgrade procedure documented and tested in staging (see [Upgrading](upgrading.md))
- [ ] PowerShell module installed and connected (if using automation/IDaC)

### Air-Gapped Network Checklist

For air-gapped deployments, also verify:

- [ ] The bundle matched its checksums: `jim-release-X.Y.Z.tar.gz.sha256` where it was downloaded, and `checksums.sha256` in the extracted bundle (see [Checking the Download](#checking-the-download))
- [ ] All images loaded successfully (`docker images | grep jim`, or on Podman `podman images` as the account that runs JIM)
- [ ] PostgreSQL is accessible and the database has been set up
- [ ] SSO/OIDC identity provider is accessible from JIM server
- [ ] DNS resolves JIM server name correctly
- [ ] JIM's certificate is valid for its name and trusted by users' browsers
- [ ] Firewall allows inbound traffic to JIM's HTTPS port only (443 by default, or your reverse proxy's port). The bundled PostgreSQL container publishes no host port; the database is reached only over the internal container network
- [ ] *If using an external PostgreSQL server:* the JIM host can reach it on 5432 (outbound, allowed on the database server's firewall)
- [ ] File connector volumes mounted (if using File Connector)
- [ ] Encryption key set backed up and included in the offline backup routine (see [Backup & Disaster Recovery](backup-recovery.md))
- [ ] Initial admin user can log in
- [ ] Logs are being written to the configured path
