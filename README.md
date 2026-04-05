# Junctional Identity Manager (JIM)

<p align="center">
  <img src="https://tetron.io/images/jim/jim-logo.png" alt="JIM"/>
</p>

[![CI](https://github.com/TetronIO/JIM/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/TetronIO/JIM/actions/workflows/ci.yml)
&nbsp;
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
&nbsp;
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791)](https://www.postgresql.org/)
&nbsp;
[![PowerShell Gallery](https://img.shields.io/powershellgallery/v/JIM?label=PSGallery&color=blue)](https://www.powershellgallery.com/packages/JIM/)
&nbsp;
[![License](https://img.shields.io/badge/License-Source_Available-orange)](https://tetron.io/jim/#licensing)
&nbsp;
[![Documentation](https://img.shields.io/badge/Docs-tetronio.github.io%2FJIM-green)](https://tetronio.github.io/JIM/)
&nbsp;
[![Open in GitHub Codespaces](https://img.shields.io/badge/Open_in-Codespaces-black?logo=github)](https://codespaces.new/TetronIO/JIM?devcontainer_path=.devcontainer/devcontainer.json)

JIM is a modern Identity Management system designed for organisations with complex identity synchronisation requirements. It is self-hosted, container-deployable, and works in both connected and air-gapped networks. Features include:

- Hub-and-spoke architecture using a central metaverse for identity correlation
- Bidirectional synchronisation of Users, Groups, and custom object types (e.g., Departments, Roles, Computers)
- Multi-directory LDAP support — Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories
- Transform data using expressions with built-in functions for common identity operations
- Extensible with custom connectors (fully unit-testable)
- Modern Web Portal and REST API with OpenAPI documentation
- Activity monitoring with auto-refresh and run profile editing
- Object type icons for visual clarity across the portal
- Docker healthchecks on Worker and Scheduler for reliable orchestration
- Single Sign-On (SSO) using OpenID Connect

![A screenshot of JIM running](https://tetron.io/images/jim/0.7.1/homepage-dark.png "JIM Screenshot")

## What Makes JIM Different

Enterprise identity synchronisation typically requires cloud connectivity, complex infrastructure, or expensive licensing. JIM takes a different approach — it deploys as a single Docker stack, runs entirely on-premises, and works in air-gapped networks with no external dependencies. Source-available code means you can inspect, audit, and verify everything JIM does with your identity data.

| Capability | JIM |
|---|---|
| Air-gapped deployment | ✅ |
| Cloud dependencies | None |
| Container-native | ✅ |
| Source available | ✅ |
| SSO with any OIDC provider | ✅ |
| Full REST API | ✅ |
| PowerShell automation | ✅ |

## Scenarios
JIM supports common Identity Governance & Administration (IGA) scenarios:

- **Joiner/Mover/Leaver (JML) Automation** - Synchronise users from HR systems to directories, applications, and downstream systems
- **Attribute Writeback** - Keep HR systems current by writing IT-managed attributes back (e.g., email addresses, phone numbers)
- **Domain Consolidation** - Prepare for cloud migration, simplification, or organisational mergers
- **Domain Migration** - Support divestitures and system decommissioning
- **Identity Correlation** - Bring together user and entitlement data from disparate business applications

## Benefits
Why choose JIM?

- **Modern Architecture** - Container-native design with no legacy infrastructure requirements
- **Secure by Default** - SSO via OpenID Connect, no shared service accounts needed
- **Air-Gapped Ready** - Fully functional without internet connectivity for sensitive environments
- **Source Available** - Transparent, auditable code you can inspect and verify
- **Actively Developed** - Built by identity management practitioners with decades of real-world experience

## Architecture
JIM is a container-based distributed application implementing the metaverse pattern for centralised identity governance.

<a href="docs/diagrams/images/dark/jim-structurizr-1-SystemContext.svg">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/diagrams/images/dark/jim-structurizr-1-SystemContext.svg">
    <img alt="JIM System Context" src="docs/diagrams/images/light/jim-structurizr-1-SystemContext.svg">
  </picture>
</a>

**Components:**
- **JIM.Web** - A website with integrated REST API, built using [ASP.NET](https://asp.net/) Blazor Server. The API is available at `/api/` with Swagger documentation at `/api/swagger`.
- **JIM.Scheduler** - A background service that triggers synchronisation runs using cron or interval-based schedules, with multi-step sequential and parallel execution
- **JIM.Worker** - A background service that processes import, sync, and export tasks with crash recovery and parallel execution support
- **JIM.PowerShell** - A cross-platform PowerShell module (Windows, macOS, Linux) for full configuration and automation of JIM, enabling Identity as Code (IDaC)
- A database - PostgreSQL

<a href="docs/diagrams/images/dark/jim-structurizr-1-Containers.svg">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/diagrams/images/dark/jim-structurizr-1-Containers.svg">
    <img alt="JIM Containers" src="docs/diagrams/images/light/jim-structurizr-1-Containers.svg">
  </picture>
</a>

For detailed architecture diagrams (Component level), see the [Architecture](https://tetronio.github.io/JIM/concepts/architecture/) documentation.

## Quick Start

### Deploy

The fastest way to get JIM running:

```bash
curl -fsSL https://tetron.io/jim/get | bash
```

This downloads everything you need, walks you through configuration, and starts JIM. For manual setup, air-gapped deployment, and production hardening, see the [Getting Started](https://tetronio.github.io/JIM/getting-started/) guide.

### Develop

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/TetronIO/JIM?devcontainer_path=.devcontainer/devcontainer.json)

The devcontainer includes everything pre-configured — .NET 9.0, PostgreSQL, Keycloak IdP with test users, shell aliases, and VS Code extensions. Or clone locally and open with the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension. See the [Developer Guide](https://tetronio.github.io/JIM/developer/) for details.

### Automate

```powershell
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com"
```

JIM includes a cross-platform [PowerShell module](https://tetronio.github.io/JIM/powershell/) for scripting, automation, and Identity as Code (IDaC).

## State of Development
JIM has reached MVP completion (100%). The core identity lifecycle is fully functional:

- **Import** identities from source systems (LDAP, CSV)
- **Sync** to reconcile identities in the central metaverse
- **Export** changes to target systems with pending export management
- **Schedule** automated synchronisation using cron or interval-based triggers

For detailed feature checklists and post-MVP roadmap, see the [Roadmap](https://tetronio.github.io/JIM/reference/roadmap/).

## Licensing
JIM uses a Source-Available model where it is free to use in non-production scenarios, but requires a commercial license for use in production scenarios. [﻿Full details can be found here](https://tetron.io/jim/#licensing).

## More Information
Please go to [﻿https://tetron.io/jim](https://tetron.io/jim) for more information.
