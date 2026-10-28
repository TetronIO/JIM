---
title: Quick Start
description: Install JIM on a server with Docker or Podman in a few minutes, connected or air-gapped, and sign in.
type: how-to
audience: evaluator
---

# Quick Start

Install JIM on one Linux server, with Docker or Podman, in a few minutes, and sign in. The installer makes sensible choices for you; for a production installation, where you make each one yourself, follow the [Deployment Guide](../administration/deployment.md) instead.

```jim-diagram
archetype: comparison
title: Installing connected or air-gapped
desc: "On a connected server, you run the installer straight from the internet, as root, and it downloads what JIM needs. On an air-gapped server, you download the release bundle on a connected machine, carry it across, extract it, and run the installer inside it, which installs from the bundle without the internet. Either way, the installer asks about your database, identity provider, HTTPS port and certificate, installs and starts JIM, and waits until it is ready for you to sign in."
columns:
- {label: Connected}
- {label: Air-gapped}
rows:
- label: Get the installer
  cells:
  - {text: straight from the internet, sub: "`curl … | sudo bash`"}
  - {text: in the release bundle, sub: carried across from a connected machine}
- label: Run it
  cells:
  - {text: as root, sub: downloads what JIM needs}
  - {text: "`sudo ./setup.sh`", sub: installs from the bundle}
- label: Internet on the server
  cells:
  - {text: needed}
  - {text: not needed}
footer: "Either way it asks its questions, installs and starts JIM, and waits until it is ready"
```

## :material-clipboard-list-outline: Before you begin

- A Linux server with Docker or Podman, sized as [Prerequisites](prerequisites.md) describes.
- JIM registered as a client at your identity provider, since the installer asks for its details. See the [SSO Setup Guide](../administration/sso-setup.md), and [Before You Install](../administration/deployment.md#before-you-install) for everything else to have ready.

## :material-download-outline: Install JIM

=== "Connected"

    1. On the server, as root, run the installer:

        ```bash
        curl -fsSL https://junctional.io/get | sudo bash
        ```

        To read it first, download it, review it, then run it:

        ```bash
        curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/deploy/setup.sh
        less setup.sh
        sudo bash setup.sh
        ```

    2. Answer its questions about your database, identity provider, HTTPS port and certificate. It uses Docker or Podman, whichever the server has, asking which where both are installed; installs JIM in `/opt/jim`; starts it; and waits until it is ready. On Podman, JIM runs as root by default, as it does on Docker, or rootless with `--rootless`; see [Running on Podman](../administration/podman.md).

=== "Air-gapped"

    1. On a connected machine, download the release bundle (`jim-release-X.Y.Z.tar.gz`) from the [releases page](https://github.com/TetronIO/JIM/releases), which holds everything an installation needs, and transfer it to the server.
    2. Extract it, and run the installer inside it, which installs from the bundle without the internet:

        ```bash
        tar -xzf jim-release-X.Y.Z.tar.gz && cd jim-release-X.Y.Z
        sudo ./setup.sh
        ```

## :material-check-decagram-outline: Check it worked

1. Open `https://<JIM's name>` in a browser (the installer prints the address), and sign in with your identity provider.
2. To see JIM working on data, use **Example Data** to fill it with sample users and groups. A progress bar on the template's page follows the generation, and the task also shows on the Operations page.

!!! tip "A certificate warning on first visit"
    If the installer created JIM's certificate, browsers warn about it until the certificate authority it also created is trusted on that machine; see [Distributing the certificate authority](../administration/tls-and-reverse-proxy.md#distributing-the-certificate-authority).

## :material-console: PowerShell Module

JIM's cross-platform PowerShell module scripts and automates JIM, including Identity as Code. It needs PowerShell 7.0 or later. Install it from the PowerShell Gallery, then connect and check the connection:

```powershell
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com"    # Opens a browser for SSO sign-in
Test-JIMConnection
```

To install it without the internet, see [PowerShell Module](../administration/deployment.md#powershell-module) in the Deployment Guide.

## :material-arrow-right-circle-outline: Next steps

- [Your First Synchronisation](first-synchronisation.md) synchronises 25 people from one file to another, in the portal or with PowerShell.
- The [Deployment Guide](../administration/deployment.md) covers a production installation, upgrades and monitoring.
