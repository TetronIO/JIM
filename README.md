# Junctional Identity Manager (JIM)

<p align="center">
  <img src="https://tetron.io/images/jim/jim-logo.png" alt="JIM"/>
</p>

[![CI](https://github.com/TetronIO/JIM/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/TetronIO/JIM/actions/workflows/ci.yml)
&nbsp;
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
&nbsp;
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791)](https://www.postgresql.org/)
&nbsp;
[![PowerShell Gallery](https://img.shields.io/powershellgallery/v/JIM?label=PSGallery&color=blue)](https://www.powershellgallery.com/packages/JIM/)
&nbsp;
[![License](https://img.shields.io/badge/License-Source_Available-orange)](https://tetron.io/jim/#licensing)
&nbsp;
[![Documentation](https://img.shields.io/badge/Docs-docs.junctional.io-green)](https://docs.junctional.io/)
&nbsp;
[![Open in GitHub Codespaces](https://img.shields.io/badge/Open_in-Codespaces-black?logo=github)](https://codespaces.new/TetronIO/JIM?devcontainer_path=.devcontainer/devcontainer.json)

JIM is a modern Identity Management system designed for organisations with complex identity synchronisation requirements. It is self-hosted, container-deployable, and works in both connected and air-gapped networks. Features include:

- Hub-and-spoke architecture using a central metaverse for identity correlation
- Bidirectional synchronisation of Users, Groups, and custom object types (e.g., Departments, Roles, Computers)
- Multi-directory LDAP support: Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories
- Built-in scheduler that supports parallel operations
- Tested at 100K+ object scale with bounded memory pipelines
- Transform data using expressions with extensive built-in functions for common identity operations
- Extensible with custom Connectors (fully testable)
- Modern Web Portal and REST API with interactive Scalar API reference (in-app at `/api/reference` and published at [docs.junctional.io/api/reference/](https://docs.junctional.io/api/reference/))
- PowerShell automation for Identity as Code (IDaC) - deploy JIM instances in minutes, not months
- Realtime activity monitoring
- Single Sign-On (SSO) using OpenID Connect
- Dark/Light mode

![A screenshot of JIM running](https://tetron.io/images/jim/0.10.2/homepage-dark.png "JIM Screenshot")

<p align="center">
  <strong>📖 <a href="https://docs.junctional.io/">Read the full documentation</a></strong>
  <br>
  <sub>Getting started guides, architecture deep-dives, API reference, and PowerShell automation</sub>
</p>

## What Makes JIM Different

Enterprise identity synchronisation typically requires cloud connectivity, complex infrastructure, or expensive licensing. JIM takes a different approach; it deploys as a single Docker stack, runs entirely on-premises, and works in air-gapped networks with no external dependencies. Source-available code means you can inspect, audit, and verify everything JIM does with your identity data.

JIM is designed to solve both enterprise-scale identity management and micro-deployment challenges for edge-sync scenarios.

![Air-gapped deployment](https://img.shields.io/badge/%E2%9C%93-Air--gapped_deployment-2ea44f?style=for-the-badge)
&nbsp;
![Container-native](https://img.shields.io/badge/%E2%9C%93-Container--native-2ea44f?style=for-the-badge)
&nbsp;
![Source available](https://img.shields.io/badge/%E2%9C%93-Source_available-2ea44f?style=for-the-badge)
&nbsp;
![SSO with any OIDC provider](https://img.shields.io/badge/%E2%9C%93-SSO_with_any_OIDC_provider-2ea44f?style=for-the-badge)
&nbsp;
![Full REST API](https://img.shields.io/badge/%E2%9C%93-Full_REST_API-2ea44f?style=for-the-badge)
&nbsp;
![PowerShell automation](https://img.shields.io/badge/%E2%9C%93-PowerShell_automation-2ea44f?style=for-the-badge)
&nbsp;
![Deploy in minutes](https://img.shields.io/badge/%E2%9C%93-Deploy_in_minutes-2ea44f?style=for-the-badge)
&nbsp;
![Enterprise scale](https://img.shields.io/badge/%E2%9C%93-Enterprise_scale-2ea44f?style=for-the-badge)
&nbsp;
![Micro deployments for edge sync](https://img.shields.io/badge/%E2%9C%93-Micro_deployments_for_edge_sync-2ea44f?style=for-the-badge)

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
- **JIM.Web** - A website with integrated REST API, built using [ASP.NET](https://asp.net/) Blazor Server. The API is available at `/api/`, with interactive [Scalar](https://scalar.com/) API documentation at `/api/reference`.
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

For detailed architecture diagrams (Component level), see the [Architecture](https://docs.junctional.io/concepts/architecture/) documentation.

## Quick Start

### Deploy

The fastest way to get JIM running:

```bash
curl -fsSL https://junctional.io/get | bash
```

This downloads everything you need, walks you through configuration, and starts JIM. For manual setup, air-gapped deployment, and production hardening, see the [Getting Started](https://docs.junctional.io/getting-started/) guide.

### Develop

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/TetronIO/JIM?devcontainer_path=.devcontainer/devcontainer.json)

The devcontainer includes everything pre-configured; .NET 10.0, PostgreSQL, Keycloak IdP with test users, shell aliases, and VS Code extensions. Or clone locally and open with the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension. See the [Developer Guide](https://docs.junctional.io/developer/) for details.

### Automate

```powershell
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com"
```

JIM includes a cross-platform [PowerShell module](https://docs.junctional.io/powershell/) for scripting, automation, and Identity as Code (IDaC).

## State of Development
JIM has reached MVP completion (100%). The core identity lifecycle is fully functional:

- **Import** identities from source systems (LDAP, CSV)
- **Sync** to reconcile identities in the central metaverse
- **Export** changes to target systems with pending export management
- **Schedule** automated synchronisation using cron or interval-based triggers

For detailed feature checklists and post-MVP roadmap, see the [Roadmap](https://docs.junctional.io/reference/roadmap/).

## Community & Support

JIM is built in the open and we'd love to hear from people running it, evaluating it, or thinking about it. The best place to engage with us and other users is [GitHub Discussions](https://github.com/TetronIO/JIM/discussions).

- **Questions and setup help:** Open a thread in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category. Search existing threads first; if your question is already there, add to it rather than starting a new one.
- **Feature ideas and suggestions:** Post in the [Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas) category. Upvotes on existing ideas genuinely inform roadmap prioritisation, so prefer adding signal to a duplicate over creating a new thread.
- **Bug reports:** Open a [GitHub Issue](https://github.com/TetronIO/JIM/issues). If it turns out to be a usage question, we may convert it to a Discussion.
- **Security vulnerabilities:** Follow the responsible disclosure process in [SECURITY.md](SECURITY.md); please do not report security issues in public Issues or Discussions.

## Licensing
JIM uses a Source-Available model where it is free to use in non-production scenarios, but requires a commercial license for use in production scenarios. [﻿Full details can be found here](https://tetron.io/jim/#licensing).

## More Information
- **Product site:** [junctional.io](https://junctional.io)
- **Documentation:** [docs.junctional.io](https://docs.junctional.io/)
- **Discussions:** [github.com/TetronIO/JIM/discussions](https://github.com/TetronIO/JIM/discussions)

