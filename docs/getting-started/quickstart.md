---
title: Quick Start
description: Deploy JIM using automated setup, manual Docker Compose, air-gapped installation, or a developer environment.
---

# Quick Start

This page covers all the ways to get JIM up and running. Choose the option that best fits your environment.

<div class="grid cards quickstart-cards" markdown>

-   :material-server:{ .lg .middle } **For Administrators**

    ---

    Deploy JIM in your environment using automated setup, Docker Compose, or air-gapped installation.

    [:octicons-arrow-down-16: Jump to Administrator options](#for-administrators)

-   :material-code-braces:{ .lg .middle } **For Developers**

    ---

    Get a full development environment with GitHub Codespaces (one click) or a local devcontainer.

    [:octicons-arrow-down-16: Jump to Developer options](#for-developers)

</div>

## For Administrators

### Before You Start

Register JIM as a client at your identity provider first; the installer asks for its details. See the [SSO Setup Guide](../administration/sso-setup.md), and [Before You Install](../administration/deployment.md#before-you-install) for everything else to have ready.

### Option 1: The Installer (Recommended)

On the server, as root:

```bash
curl -fsSL https://junctional.io/get | sudo bash
```

Or download it and read it first:

```bash
curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/deploy/setup.sh
less setup.sh    # review the script
sudo bash setup.sh
```

The installer asks about your database, identity provider, HTTPS port and certificate, installs JIM in `/opt/jim`, starts it, and waits until it is ready.

### Option 2: Air-Gapped

Each release includes a bundle (`jim-release-X.Y.Z.tar.gz`) holding everything an installation needs. Download it from the [releases page](https://github.com/TetronIO/JIM/releases) on a connected machine, transfer it to the server, then run the installer inside it, which installs from the bundle without the internet:

```bash
tar -xzf jim-release-X.Y.Z.tar.gz && cd jim-release-X.Y.Z
sudo ./setup.sh
```

To install without the installer, connected or air-gapped, follow the [Deployment Guide](../administration/deployment.md).

### Accessing JIM

Once running, open `https://<JIM's name>` in a browser (the setup script prints the address) and log in with your identity provider. Then use the **Example Data** feature to populate JIM with sample users and groups for testing. When you execute a template, a live progress bar appears on the template page so you can watch generation and persistence progress without leaving it; the same task is also visible on the Operations page.

!!! tip "A certificate warning on first visit"
    If the setup script created JIM's certificate, browsers warn about it until the certificate authority it also created is trusted on that machine; see [Distributing the certificate authority](../administration/deployment.md#distributing-the-certificate-authority).

For TLS, upgrades, monitoring and the rest of production deployment, see the [Deployment Guide](../administration/deployment.md).

---

## For Developers

### GitHub Codespaces (One Click)

The fastest way to get a development environment. Everything is pre-configured: .NET 10.0, PostgreSQL, Keycloak, shell aliases, and VS Code extensions.

Once the Codespace is ready, open a terminal and choose one of the following options:

#### Option 1: Light stack (recommended for day-to-day development)

`jim-build-light` starts PostgreSQL and Keycloak in Docker, then runs JIM.Web natively on the host. This is the fastest iteration loop; code changes rebuild and restart in seconds.

```bash
jim-build-light
```

#### Option 2: Full stack

`jim-build` builds and starts the entire stack in Docker: PostgreSQL, Keycloak, JIM.Web, JIM.Worker, and JIM.Scheduler. Use this when you need to exercise the worker or scheduler, or verify end-to-end behaviour in containers.

```bash
jim-build
```

### Local Devcontainer

Clone the repository and open it in VS Code with the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension. The devcontainer will set up the full development environment automatically, then use either `jim-build-light` or `jim-build` as above.

!!! tip "No external identity provider needed"
    The devcontainer includes a bundled Keycloak instance with pre-configured test users, so you do not need to configure an external OpenID Connect provider to sign in.

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

For air-gapped or disconnected installation of the PowerShell module, see the [Deployment Guide: PowerShell Module](../administration/deployment.md#powershell-module).
