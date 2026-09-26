---
title: Deployment
---

# Deployment Guide

This guide covers deploying JIM to a production environment, including prerequisites, architecture, installation procedures for both connected and air-gapped environments, HTTPS certificates, and operational guidance.

!!! tip "Quick Start"
    The [Getting Started](../getting-started/index.md) guide gets you running in under five minutes. This page covers production hardening, certificates, reverse proxies, upgrades, and operational best practices.

## Prerequisites

### Hardware Requirements

| Component | Minimum    | Recommended                                      |
|-----------|------------|--------------------------------------------------|
| CPU       | 2 cores    | 4+ cores                                         |
| RAM       | 4 GB       | 8+ GB                                            |
| Storage   | 20 GB      | 50+ GB (depends on identity data volume)         |

Storage scales with the number of identity objects and the frequency of synchronisation runs (change history, logs, etc.).

#### Memory Scaling by Identity Object Count

During a Full Import, the Worker holds every object it imports from a Connected System in memory, together with its attribute values and the Activity's result for each object. Memory requirements therefore scale linearly with the number of objects in the largest Connected System.

These figures are for the **host machine** (or VM) running the Docker stack -- they must cover the operating system, all JIM containers, and the database.

| Connected System Size       | Minimum Host RAM | Recommended Host RAM |
|-----------------------------|------------------|----------------------|
| Up to 10,000 objects        | 4 GB             | 8 GB                 |
| 10,000 -- 50,000 objects    | 8 GB             | 16 GB                |
| 50,000 -- 100,000 objects   | 20 GB            | 24 GB                |
| 100,000 -- 250,000 objects  | 24 GB            | 32 GB                |
| 250,000 -- 500,000 objects  | 48 GB            | 64 GB                |

!!! info "Why large imports need significant memory"
    During a Full Import, the Worker holds every imported object and its attributes in memory until it has compared them all (to find duplicates, detect deleted objects, and link objects that refer to each other), and only then saves them. A Full Import of 100,000 objects with 20 attributes each peaks at approximately 2.3 GB of memory in the Worker, and PostgreSQL needs a further 1--2 GB while the results are saved. Combined with the web, scheduler, and operating system overhead, total system memory consumption reaches 8--10 GB for 100K objects.

!!! note
    These requirements apply to the largest single Full Import. If you have multiple Connected Systems of 50K objects each but import them sequentially (not concurrently), size for 50K, not the sum. Delta Imports process only changed objects and require significantly less memory.

!!! info "Large group memberships drive memory more than object count"
    A group is loaded with its full member list during processing, so a single large group can dominate memory use: a group with 495,000 members loads 495,000 member references. JIM's largest validated scenario (a cross-domain synchronisation between two directories of roughly 500,000 objects each, with groups of up to 495,000 members) peaked at approximately **55 GB total host RAM** with the full stack, database, and both directories resident. Size toward the upper figure in the table when provisioning or synchronising very large groups; a deployment of the same object count with only small groups needs considerably less.

### Software Requirements

- **Docker Engine** 24.0+ with **Docker Compose** v2.24+
- **OpenSSL**, to create or check JIM's HTTPS certificate (part of every mainstream Linux distribution's base install)
- **An OIDC identity provider** (e.g. Entra ID, Keycloak, AD FS) -- see [SSO Setup](sso-setup.md)

### Network Requirements

JIM's services communicate internally over a Docker bridge network (`jim-network`). The only port that needs to be exposed externally is the HTTPS port on `jim.web` (container port `8443`), which the production compose file publishes on host port `443`.

| Direction | Port                                                  | Purpose                                        |
|-----------|-------------------------------------------------------|------------------------------------------------|
| Inbound   | 443 (HTTPS), or the port you chose instead            | Web UI and REST API                            |
| Outbound  | Varies                                                | OIDC provider, LDAP targets, file shares, etc. |

In air-gapped environments, no outbound connectivity is required after initial deployment.

---

## Architecture Overview

JIM runs as a Docker Compose stack with four services:

--8<-- "assets/diagrams/deployment-stack.svg"

<p class="jim-diagram-caption">All three JIM services coordinate through PostgreSQL; the Scheduler queues tasks and the Worker polls them, so no service calls another directly. The bundled database container is optional; JIM can use an external PostgreSQL server instead.<span class="jimdg-caption-motion"> Moving dots trace database traffic.</span></p>

| Service            | Description                                                                           |
|--------------------|---------------------------------------------------------------------------------------|
| **jim.web**        | Web portal and REST API (`/api/`)                                                     |
| **jim.worker**     | Processes import, synchronisation, and export tasks                                   |
| **jim.scheduler**  | Triggers synchronisation runs on cron or interval schedules                           |
| **jim.database**   | PostgreSQL 18 (optional bundled container)                                            |

### Docker Volumes

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
| **Setup**          | Automatic -- included in Docker stack         | You manage PostgreSQL separately            |
| **Started with**   | `--profile with-db` flag                      | No profile flag needed                      |
| **Best for**       | Evaluations, small deployments                | Production, existing DBA team               |
| **Backup**         | Docker volume snapshots                       | Your existing DB backup tooling             |
| **Tuning**         | Default settings in compose file              | Full control                                |

!!! tip
    Start with bundled PostgreSQL for evaluation. Migrate to external for production workloads where you need backup, high availability, or monitoring integration.

Start-up order does not matter: each JIM service waits up to five minutes for PostgreSQL to accept connections, logging each attempt, so JIM and its database server can be started or restarted independently. If a service keeps waiting, see [Troubleshooting](troubleshooting.md#a-service-logs-the-database-is-not-reachable-yet).

---

## Before You Install

Have these ready, whichever way you install:

- **A DNS name for the JIM server**, such as `jim.example.com`, with a DNS record pointing at the server. JIM's address is `https://jim.example.com`, on the standard HTTPS port, 443.
- **A client registration for JIM at your identity provider**, with these redirect URIs: `https://jim.example.com/signin-oidc` and `https://jim.example.com/signout-callback-oidc`. You need its authority URL, client ID and secret, API scope, and the claim value of the first administrator; the [SSO Setup Guide](sso-setup.md) covers each provider.
- **A certificate for JIM's name**, from your organisation's certificate authority, or let the installer create one (see [TLS and Reverse Proxy](#tls-and-reverse-proxy)).
- **PostgreSQL 18**: the bundled container needs nothing. For your own server, create JIM's database and user first:

    ```sql
    CREATE DATABASE jim;
    CREATE USER jim WITH ENCRYPTED PASSWORD 'your_secure_password';
    GRANT ALL PRIVILEGES ON DATABASE jim TO jim;
    ```

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

The installer downloads the latest release and installs it in `/opt/jim`; see [What the Installer Does](#what-the-installer-does). Then carry on at [After Installing](#after-installing).

### By Hand

As root:

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
2. Put JIM's certificate and key in `tls/` (see [The Certificate](#the-certificate)).
3. Start JIM, leaving out `--profile with-db` if you use your own PostgreSQL server:

    ```bash
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      --profile with-db up -d
    ```

Then carry on at [After Installing](#after-installing).

---

## Air-Gapped Deployment

Each [release](https://github.com/TetronIO/JIM/releases) includes a bundle, `jim-release-X.Y.Z.tar.gz`, holding everything an installation needs: the images, the compose files and the installer. Nothing is downloaded while installing from it.

### Bundle Contents

```text
jim-release-X.Y.Z/
+-- setup.sh                  # The installer; run inside the bundle, it installs from it
+-- VERSION                   # The JIM version the bundle installs
+-- docker-images/
|   +-- jim-web.tar           # Docker image for web/API service
|   +-- jim-worker.tar        # Docker image for worker service
|   +-- jim-scheduler.tar     # Docker image for scheduler service
|   +-- postgres-18.tar       # PostgreSQL image (if included)
+-- compose/
|   +-- docker-compose.yml
|   +-- docker-compose.production.yml
|   +-- .env.example
+-- powershell/
|   +-- JIM/                  # PowerShell module directory
+-- docs/
|   +-- README.md
|   +-- CHANGELOG.md
|   +-- INSTALL.md            # These instructions, for reading offline
+-- checksums.sha256          # SHA256 checksums for verification
+-- README.txt                # Quick start guide
```

### With the Installer (Recommended)

Transfer the bundle to the JIM server by your organisation's approved method, then:

```bash
tar -xzf jim-release-X.Y.Z.tar.gz
cd jim-release-X.Y.Z

# Every line should end in OK
sha256sum -c checksums.sha256

sudo ./setup.sh
```

Run inside the bundle, the installer loads JIM's images from it and starts JIM without trying the internet; otherwise it works exactly as it does connected (see [What the Installer Does](#what-the-installer-does)). Then carry on at [After Installing](#after-installing).

### By Hand

As root, in the extracted bundle:

```bash
for f in docker-images/*.tar; do docker load -i "$f"; done
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
cp compose/docker-compose.yml compose/docker-compose.production.yml /opt/jim/
cp compose/.env.example /opt/jim/.env && chmod 600 /opt/jim/.env
```

1. Edit `/opt/jim/.env`: set `DOCKER_REGISTRY=ghcr.io/tetronio/` and `JIM_VERSION` to the version in the bundle's `VERSION` file, and the identity provider settings (see the [Configuration Reference](configuration.md)). For the bundled PostgreSQL, set `JIM_DB_HOSTNAME=jim.database` (the template's `localhost` is for development) and choose a strong `JIM_DB_PASSWORD`; for your own server, give its name and JIM's credentials.
2. Put JIM's certificate and key in `/opt/jim/tls/` (see [The Certificate](#the-certificate)).
3. Start JIM, leaving out `--profile with-db` if you use your own PostgreSQL server. `--pull never` makes Docker report a missing image rather than try the internet:

    ```bash
    cd /opt/jim
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      --profile with-db up -d --pull never
    ```

Then carry on at [After Installing](#after-installing).

---

## What the Installer Does

`setup.sh` installs JIM in `/opt/jim` (or `./jim` when not run as root) and asks, in turn:

1. **Database**: the bundled PostgreSQL, whose password it generates, or your own server
2. **Identity provider**: the settings from your client registration
3. **HTTPS port**: 443 unless you choose another. It checks nothing else on the server already uses the port.
4. **Certificate**: one it creates, from a certificate authority of its own, or your organisation's certificate and key, which it checks before installing (see [TLS and Reverse Proxy](#tls-and-reverse-proxy))
5. **Reverse proxy or load balancer**: whether one sits in front of JIM, and if so its address, so that JIM trusts it to report each client's address

It keeps `.env`, which holds the database password and your identity provider's client secret, readable by root only. It then starts JIM, waits until JIM is ready, and prints JIM's address and what is left to do; if JIM is not ready within ten minutes, it says so and exits with a failure code. It keeps a copy of itself in the installation, for looking after it later:

```bash
sudo /opt/jim/setup.sh --renew-certificate   # a certificate the installer created, before it expires
sudo /opt/jim/setup.sh --certificate         # change its names, or move to your organisation's certificate
```

For automation, every question can be answered in advance with an environment variable; the header of `setup.sh` lists them. Running the installer again on an existing installation asks before replacing its configuration.

---

## After Installing

1. **Trust the certificate authority**, if the installer created JIM's certificate: add `/opt/jim/tls/ca.crt` to the trusted root certificate authorities of every machine whose browser or tools use JIM (see [Distributing the certificate authority](#distributing-the-certificate-authority)). Until then, browsers warn about JIM's certificate.
2. **Open JIM** at `https://jim.example.com` and sign in as the initial administrator (the claim value in `JIM_SSO_INITIAL_ADMIN`), who has full access.

JIM prepares its database on first start, with no manual step, and does not serve requests until that has finished. The installer waits for it; after a manual start, `jim.web` shows as `healthy` once JIM is ready:

```bash
cd /opt/jim
docker compose -f docker-compose.yml -f docker-compose.production.yml ps jim.web
```

If it stays unhealthy, the worker's log names the problem, for example a database permission it lacks: `docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.worker`.

!!! warning "Always name the compose files"
    Pass the same `-f` files (and `--profile`) to every `docker compose` command for this deployment, including `stop`, `pull` and upgrades. Without `-f`, Docker Compose loads `docker-compose.yml` alone, which leaves out the production settings, and silently adds any `docker-compose.override.yml` it finds in the directory.

### File Connector Storage (Optional)

The File Connector ships pre-configured to read and write at `/connector-files` inside the container, backed by a Docker-managed volume named `jim-connector-files-volume`. **No setup is required for the default case**: start the stack and the volume is created automatically with correct ownership.

To put files in or pull them out:

```bash
# Push an import file into the volume
docker cp ./Users.csv jim.worker:/connector-files/Users.csv

# Pull an exported file out
docker cp jim.worker:/connector-files/Exports.csv ./Exports.csv
```

Then configure the File Connector's **File Path** setting as `/connector-files/Users.csv`.

If you need to integrate with an external system that writes to a fixed network location (e.g. an SMB or NFS share), bind-mount that path over a subdirectory of `/connector-files`. See the [JIM File Connector documentation](../connectors/jim-file-connector.md#file-access) for the full pattern, including the UID 1654 ownership requirement for bind-mounted host paths.

---

## TLS and Reverse Proxy

JIM serves HTTPS itself. In production, `jim.web` listens for HTTPS on container port `8443`, which `docker-compose.production.yml` publishes on the standard HTTPS port, 443, so a fresh install can be signed into from any machine at `https://jim.example.com` with no reverse proxy.

!!! warning "Browsers on other machines must use HTTPS"
    JIM's sign-in cookies are HTTPS-only in a production deployment, and browsers discard HTTPS-only cookies sent over plain HTTP to any address other than `localhost`. Over plain HTTP from another machine, sign-in never completes: JIM stops it on a **Sign-in could not complete** page rather than send the browser back to your identity provider again (see [Troubleshooting](troubleshooting.md#sign-in-loops-between-jim-and-the-identity-provider)).

### The Certificate

JIM reads its certificate and private key from two PEM files in the `tls` folder of the installation (`/opt/jim/tls` when the installer ran as root):

| File          | Contents                                                                                              |
|---------------|-------------------------------------------------------------------------------------------------------|
| `tls/tls.crt` | JIM's certificate, followed by any intermediate CA certificates, so that clients receive the whole chain |
| `tls/tls.key` | Its private key, **unencrypted**, belonging to UID `1654` (the user JIM runs as) with mode `400`      |

The certificate must name, as subject alternative names, every DNS name and IP address users and tools reach JIM at, including the name a reverse proxy uses to connect to it. JIM accepts TLS 1.2 and 1.3.

Docker mounts both files into `jim.web` read-only, keeping their owner and mode, and JIM runs with every capability dropped: if the key does not belong to UID `1654`, `jim.web` cannot read it and fails to start. Until both files exist, `docker compose up` stops with an error naming the missing file.

The installer asks which of these two ways to provide them, and puts them in place:

- **Your organisation's certificate** (recommended for production)<br /> Issued by your internal certificate authority (CA), such as Active Directory Certificate Services or Identity Management, which your browsers and servers already trust. Nothing needs distributing.
- **A certificate the installer creates**<br /> `setup.sh` creates a certificate authority for this JIM server, and a server certificate from it, for the names and addresses you give it. Browsers show a warning until the CA is trusted; see [Distributing the certificate authority](#distributing-the-certificate-authority).

When installing by hand, put your organisation's certificate in place as root:

```bash
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
cp /path/to/jim.crt /opt/jim/tls/tls.crt   # the certificate, followed by any intermediate CA certificates
cp /path/to/jim.key /opt/jim/tls/tls.key   # its unencrypted private key
chown 1654:1654 /opt/jim/tls/tls.key && chmod 400 /opt/jim/tls/tls.key
```

If your key is encrypted, decrypt it first with `openssl pkey -in encrypted.key -out /opt/jim/tls/tls.key`.

### A Certificate the Installer Creates

`setup.sh` writes these files to `tls/`:

| File      | What it is                                                         | Keep it          |
|-----------|--------------------------------------------------------------------|------------------|
| `ca.crt`  | The certificate authority's certificate, valid for ten years       | Distribute it    |
| `ca.key`  | The certificate authority's private key; it signs JIM's certificates | Secret, readable by root only |
| `tls.crt` | JIM's certificate, valid for one year                              |                  |
| `tls.key` | JIM's private key                                                  | Secret           |
| `names`   | The names and addresses the certificate covers, for renewal        |                  |

The certificate authority is limited, by name constraints, to exactly the names and addresses you gave. A browser that trusts it accepts a certificate it signs for JIM, and rejects one for any other site, so even a stolen `ca.key` cannot be used to impersonate other servers to the machines that trust it.

When installing by hand, create these files with the installer's certificate step alone, from a downloaded `setup.sh` or the one in the release bundle:

```bash
sudo JIM_INSTALL_DIR=/opt/jim ./setup.sh --certificate
```

#### Distributing the Certificate Authority

Add `ca.crt` to the trusted root certificate authorities of every machine whose browser or tools use JIM, and of any reverse proxy that connects to JIM over HTTPS:

- **Windows**<br /> Group Policy (Computer Configuration > Policies > Windows Settings > Security Settings > Public Key Policies > Trusted Root Certification Authorities), or `Import-Certificate -FilePath ca.crt -CertStoreLocation Cert:\LocalMachine\Root` on one machine.
- **Red Hat Enterprise Linux and derivatives**<br /> Copy it to `/etc/pki/ca-trust/source/anchors/` and run `update-ca-trust`.
- **Debian and Ubuntu**<br /> Copy it to `/usr/local/share/ca-certificates/jim-ca.crt` and run `update-ca-certificates`.
- **macOS**<br /> `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt`.

Depending on its configuration, Firefox may use its own list rather than the operating system's. If it still warns, import the CA under **Settings > Privacy & Security > Certificates > View Certificates > Authorities**.

The PowerShell module relies on the operating system's list, so machines that run `Connect-JIM` need the CA too.

### Renewing the Certificate

JIM sends browsers an HSTS header, which tells them to refuse plain HTTP and certificate errors for JIM's name. Once a browser trusts JIM's certificate, an **expired** certificate therefore blocks access outright, with no option to continue. Renew before the expiry date, which the installer prints at install; to read it later:

```bash
openssl x509 -in /opt/jim/tls/tls.crt -noout -enddate
```

- **A certificate the installer created**<br /> Run the installation's copy of the installer. It issues a new certificate from the same certificate authority, for the same names, and restarts `jim.web`; browsers that trust the CA carry on without a warning.

    ```bash
    sudo /opt/jim/setup.sh --renew-certificate
    ```

- **Your organisation's certificate**<br /> Install the renewed one as in [Replacing the certificate](#replacing-the-certificate).

### Replacing the Certificate

To install a renewed certificate from your CA, move to your organisation's certificate, or change the names a created certificate covers, run the installer's certificate step:

```bash
sudo /opt/jim/setup.sh --certificate
```

It asks the same questions as at install, checks the certificate and key before installing them, and restarts `jim.web`. A created certificate for new names needs a new certificate authority, which you distribute again; the installer says so when it creates one.

To do the same by hand, as root:

```bash
cd /opt/jim
cp /path/to/new.crt tls/tls.crt
cp /path/to/new.key tls/tls.key.new && chmod 400 tls/tls.key.new && chown 1654:1654 tls/tls.key.new
mv -f tls/tls.key.new tls/tls.key
docker compose -f docker-compose.yml -f docker-compose.production.yml restart jim.web
```

When you move away from a certificate the installer created, delete `tls/ca.key`, and remove the JIM certificate authority from the machines that trusted it.

### Behind a Reverse Proxy or Load Balancer

JIM does not need a reverse proxy, but you may already route web applications through one, or want JIM on port 443 behind a load balancer. Two configurations are supported:

- **Re-encrypting to JIM** (recommended)<br /> The proxy terminates the browser's HTTPS connection and opens its own HTTPS connection to JIM, checking JIM's certificate. Traffic is encrypted end to end, which regulated environments commonly require, and nothing about JIM changes. The proxy must trust the CA that issued JIM's certificate, and JIM's certificate must carry both the name users type and the name the proxy connects to: nginx checks JIM's certificate against the host name in its `proxy_pass` line, while Apache httpd, which passes the name users typed on to JIM, checks it against that.
- **Plain HTTP on the JIM host's loopback interface**<br /> A proxy on the JIM host itself terminates TLS and forwards plain HTTP to JIM, which listens on the host's loopback interface only. See [Plain HTTP for a proxy on the same host](#plain-http-for-a-proxy-on-the-same-host).

Either way, set `JIM_TRUSTED_PROXIES` so that JIM records each client's real address; see [Trusting the Reverse Proxy](#trusting-the-reverse-proxy).

!!! important "Pass WebSocket connections through"
    The JIM portal keeps a live connection to the server over WebSockets. Your reverse proxy **must** pass WebSocket connections through (the `Upgrade` and `Connection` headers in the examples below). Without them the portal falls back to a slower connection method and responds sluggishly, and the browser's developer console reports `Failed to connect via WebSockets, using the Long Polling fallback transport`.

#### nginx Example

Users reach `jim.example.com`; the proxy connects to JIM at `jim-app.example.com`, and trusts the CA in `/etc/nginx/jim-ca.crt` (JIM's `tls/ca.crt`, or your organisation's CA certificate).

```nginx
server {
    listen 443 ssl http2;
    server_name jim.example.com;

    ssl_certificate     /etc/nginx/ssl/jim.example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/jim.example.com.key;
    ssl_protocols       TLSv1.2 TLSv1.3;

    location / {
        # Re-encrypt to JIM, checking its certificate
        proxy_pass https://jim-app.example.com;
        proxy_ssl_verify on;
        proxy_ssl_trusted_certificate /etc/nginx/jim-ca.crt;
        proxy_ssl_server_name on;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # JIM's sign-in response sets cookies larger than nginx's default buffer;
        # without these, signing in fails with 502 Bad Gateway
        proxy_buffer_size 16k;
        proxy_buffers 8 16k;

        # WebSocket support (required for the JIM portal)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

#### Apache httpd Example

Apache httpd is the default web server on Red Hat Enterprise Linux. This example needs `mod_ssl`, `mod_proxy`, `mod_proxy_http`, `mod_proxy_wstunnel`, `mod_headers` and `mod_rewrite`. Because `ProxyPreserveHost` passes on the name users typed, Apache checks JIM's certificate against `jim.example.com`, not `jim-app.example.com`: if JIM's certificate lacks it, every request fails with `500 Internal Server Error` and Apache logs `AH02411: SSL Proxy: Peer certificate does not match for hostname`.

```apache
<VirtualHost *:443>
    ServerName jim.example.com

    SSLEngine on
    SSLCertificateFile    /etc/pki/tls/certs/jim.example.com.crt
    SSLCertificateKeyFile /etc/pki/tls/private/jim.example.com.key
    SSLProtocol           -all +TLSv1.2 +TLSv1.3

    # Re-encrypt to JIM, checking its certificate
    SSLProxyEngine on
    SSLProxyVerify require
    SSLProxyCheckPeerName on
    SSLProxyCACertificateFile /etc/pki/tls/certs/jim-ca.crt

    ProxyPreserveHost On
    RequestHeader set X-Forwarded-Proto "https"

    # WebSocket support (required for the JIM portal)
    RewriteEngine on
    RewriteCond %{HTTP:Upgrade} =websocket [NC]
    RewriteRule ^/(.*) wss://jim-app.example.com/$1 [P,L]

    ProxyPass        / https://jim-app.example.com/
    ProxyPassReverse / https://jim-app.example.com/
</VirtualHost>
```

On a host with SELinux enforcing, as Red Hat Enterprise Linux is by default, httpd may not open network connections until you allow it; without this, every request fails with `503 Service Unavailable` and the audit log records a `name_connect` denial:

```bash
setsebool -P httpd_can_network_connect 1
```

### Trusting the Reverse Proxy

Set `JIM_TRUSTED_PROXIES` in `.env` to the address the proxy connects to JIM from, so that JIM reads each client's real IP address, and the original scheme, from the proxy's `X-Forwarded-For` and `X-Forwarded-Proto` headers. Until you do:

- The security audit log and API rate limiting see the proxy's address instead of each client's, so every client shares one rate limit.
- **With plain HTTP to JIM only:** sign-in fails, because JIM sends your identity provider an `http://` callback address that does not match the `https://` one you registered, and JIM refuses every REST API request that carries a password, because it cannot confirm the connection is encrypted.

The address to trust depends on where the proxy runs:

- **Proxy on another machine**<br /> That machine's IP address.
- **Proxy on the JIM host**<br /> The proxy takes port 443, so JIM moves to a port on the host's loopback interface: `JIM_WEB_PORT=127.0.0.1:8443` for a proxy that re-encrypts to `https://localhost:8443` (see [Port Mapping](#port-mapping)), or the plain HTTP setup [below](#plain-http-for-a-proxy-on-the-same-host). Either way, JIM sees the connection arrive from the gateway address of the `jim-network` Docker network, not from `127.0.0.1`. Find it with:

    ```bash
    docker network inspect jim-network --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}'
    ```

    Docker assigns this address when it creates the network, so check it again after anything that removes and recreates the network, such as `docker compose down`.

For example:

```bash
JIM_TRUSTED_PROXIES=10.0.0.5
```

Then run your `docker compose ... up -d` command again so `jim.web` restarts with the setting. On startup `jim.web` logs `Trusting forwarded headers from 1 known proxy address(es) and 0 known network(s)`, which confirms it read the value. List only your proxy: JIM believes whatever a trusted address tells it about the client's address and scheme. See the [Configuration Reference](configuration.md#reverse-proxy) for the setting's full format.

### Plain HTTP for a Proxy on the Same Host

If a reverse proxy on the JIM host terminates TLS, JIM can serve it plain HTTP on the host's loopback interface instead, with no certificate of its own. Create `docker-compose.local-proxy.yml` next to the other compose files:

```yaml
# JIM serves plain HTTP on this host's loopback interface only, for a reverse proxy on this host.
services:
  jim.web:
    environment:
      - ASPNETCORE_URLS=http://+:8080
      # Empty, so that JIM does not look for a certificate
      - ASPNETCORE_Kestrel__Certificates__Default__Path=
      - ASPNETCORE_Kestrel__Certificates__Default__KeyPath=
    ports: !override
      - "127.0.0.1:8080:8080"
    secrets: !reset []

secrets: !reset {}
```

Add `-f docker-compose.local-proxy.yml` after the other two files in every `docker compose` command for this deployment, point the proxy at `http://localhost:8080`, and set [`JIM_TRUSTED_PROXIES`](#trusting-the-reverse-proxy) to the `jim-network` gateway address. The `tls` folder is not needed. Traffic between the proxy and JIM is unencrypted, but it never leaves the host: other machines cannot reach port `8080`.

### Port Mapping

`jim.web` listens for HTTPS on port `8443` inside its container. `docker-compose.production.yml` publishes it on the standard HTTPS port, 443, on every interface, so JIM's address needs no port. The installer asks for the port; to change it afterwards, set `JIM_WEB_PORT` in `.env` and run your `docker compose ... up -d` command again:

```bash
JIM_WEB_PORT=8443
```

JIM's address then carries the port (`https://jim.example.com:8443`), and so must the redirect URIs registered at your identity provider.

To accept connections on one interface only, such as the loopback interface behind a reverse proxy on the same host, prefix its address:

```bash
JIM_WEB_PORT=127.0.0.1:8443
```

The base `docker-compose.yml` publishes no ports, so a deployment that leaves out `docker-compose.production.yml` is not reachable from the host.

!!! note "Use `JIM_WEB_PORT` rather than a `ports` override"
    Docker Compose combines port mappings from every file, so a `ports` entry in an override of your own adds a second mapping instead of replacing the default one, unless it is marked `!override` as in [Plain HTTP for a proxy on the same host](#plain-http-for-a-proxy-on-the-same-host).

!!! note "Rootless Docker and ports below 1024"
    Docker running rootless cannot publish a port below the kernel's unprivileged-port threshold (1024 by default), which includes 443. The installer checks for this and gives the command that lowers the threshold; alternatively choose a port of 1024 or above.

---

## Health Monitoring

### Health Endpoints

| Endpoint                  | Purpose                                         |
|---------------------------|-------------------------------------------------|
| `/api/v1/health`          | Basic liveness check                            |
| `/api/v1/health/ready`    | Readiness check (includes database connectivity)|
| `/api/v1/system/health`   | Health of the Worker's synchronisation loop, its Password Delivery Service and the Scheduler, from their database heartbeats (requires the Administrator role) |

The `jim.web` container includes a Docker healthcheck using the readiness endpoint. The two unauthenticated endpoints answer for the web tier only; `system/health` is how the three background services are observed from outside the portal, and the same report is shown on **Administration > Operations** and returned by `Get-JIMServiceHealth`. The `jim.worker` container hosts two of them, the synchronisation loop and the Password Delivery Service, and each reports on its own, so a Worker whose password delivery has stopped while imports still run is visible as exactly that. See [Operations > Service Health](../configuration/operations.md#service-health).

The `jim.worker` and `jim.scheduler` containers use file-based healthcheck monitoring. Each service writes a heartbeat file periodically during normal operation, and the Docker healthcheck verifies the file is recent. This means `docker compose ps` and orchestrators like Docker Swarm or Kubernetes can detect when a worker or scheduler has stalled, even if the process itself has not exited.

### Logging

JIM writes structured logs to the `jim-logs-volume` Docker volume. Configure log level via `.env`:

```text
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
- [ ] Docker restart policy is `unless-stopped` (set by production override)
- [ ] Upgrade procedure documented and tested in staging (see [Upgrading](upgrading.md))
- [ ] PowerShell module installed and connected (if using automation/IDaC)

### Air-Gapped Network Checklist

For air-gapped deployments, also verify:

- [ ] All Docker images loaded successfully (`docker images | grep jim`)
- [ ] PostgreSQL is accessible and the database has been set up
- [ ] SSO/OIDC identity provider is accessible from JIM server
- [ ] DNS resolves JIM server name correctly
- [ ] JIM's certificate is valid for its name and trusted by users' browsers
- [ ] Firewall allows inbound traffic to JIM's HTTPS port only (443 by default, or your reverse proxy's port). The bundled PostgreSQL container publishes no host port; the database is reached only over the internal `jim-network` bridge
- [ ] *If using an external PostgreSQL server:* the JIM host can reach it on 5432 (outbound, allowed on the database server's firewall)
- [ ] File connector volumes mounted (if using File Connector)
- [ ] Encryption key set backed up and included in the offline backup routine (see [Backup & Disaster Recovery](backup-recovery.md))
- [ ] Initial admin user can log in
- [ ] Logs are being written to the configured path
