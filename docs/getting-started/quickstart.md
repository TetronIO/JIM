---
title: Quick Start
description: Deploy JIM using automated setup, manual Docker Compose, air-gapped installation, or a developer environment.
---

# Quick Start

This page covers all the ways to get JIM up and running. Choose the option that best fits your environment.

## For Administrators

### Option 1 — Automated Setup (Recommended)

The setup script downloads everything you need, walks you through configuration, and starts JIM:

```bash
curl -fsSL https://tetron.io/jim/get | bash
```

Or download and inspect first:

```bash
curl -fsSL -o setup.sh https://tetron.io/jim/get
less setup.sh    # review the script
bash setup.sh
```

### Option 2 — Manual Setup

If you prefer to set things up manually using Docker Compose:

```bash
mkdir jim && cd jim

# Download compose files and environment template
curl -fsSL -o docker-compose.yml https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml
curl -fsSL -o docker-compose.production.yml https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.production.yml
curl -fsSL -o .env https://github.com/TetronIO/JIM/releases/latest/download/.env.example

# Configure - edit .env with your SSO settings (see SSO Setup Guide)
# Set DOCKER_REGISTRY=ghcr.io/tetronio/ and JIM_VERSION to the latest release version

# Start JIM with bundled PostgreSQL
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d

# Or without bundled PostgreSQL (set JIM_DB_HOSTNAME in .env to your external DB)
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d
```

!!! note "SSO configuration required"
    Before starting JIM, you must configure your OpenID Connect identity provider settings in the `.env` file. See the [SSO Setup Guide](../administration/sso-setup.md) for step-by-step instructions.

### Option 3 — Air-Gapped Deployment

For environments without internet connectivity, each release includes a downloadable bundle (`jim-release-X.Y.Z.tar.gz`) containing:

- Pre-built Docker images
- Docker Compose files
- Environment template
- PowerShell module
- Installation instructions

Download the bundle from the [releases page](https://github.com/TetronIO/JIM/releases) on a connected machine, transfer it to your air-gapped host, and follow the included instructions.

For detailed air-gapped deployment guidance, see the [Deployment Guide](../administration/deployment.md).

### Accessing JIM

Once running, access JIM at [http://localhost:5200](http://localhost:5200). Log in with your identity provider, then use the **Example Data** feature to populate JIM with sample users and groups for testing.

For production hardening (TLS, reverse proxy, upgrades, monitoring), see the [Deployment Guide](../administration/deployment.md).

---

## For Developers

### GitHub Codespaces (One Click)

The fastest way to get a development environment. Everything is pre-configured — .NET 9.0, PostgreSQL, shell aliases, and VS Code extensions.

Once the Codespace is ready, open a terminal and run:

```bash
jim-db    # Start PostgreSQL
jim-web   # Start JIM (or press F5 to debug)
```

### Local Devcontainer

Clone the repository and open it in VS Code with the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension. The devcontainer will set up the full development environment automatically.

!!! tip "No external identity provider needed"
    The devcontainer includes a bundled Keycloak instance with pre-configured test users. Sign in with `admin` / `admin`.

For the full development guide, see the [Developer Guide](../developer/index.md).

---

## PowerShell Module

JIM includes a cross-platform PowerShell module for scripting, automation, and Identity as Code (IDaC). Requires PowerShell 7.0+.

### Install from PowerShell Gallery

```powershell
Install-Module -Name JIM
```

### Connect and Verify

```powershell
Connect-JIM -Url "https://jim.example.com"    # Opens browser for SSO sign-in
Test-JIMConnection
```

For air-gapped or disconnected installation of the PowerShell module, see the [Deployment Guide — PowerShell Module](../administration/deployment.md#powershell-module).
