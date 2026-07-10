---
title: Junctional Identity Manager Docs
description: A modern, self-hosted Identity Management system for organisations with complex identity synchronisation requirements.
template: home.html
hide:
  - navigation
  - toc
---

<img class="diagram-light" alt="JIM System Context" src="diagrams/images/light/jim-structurizr-1-SystemContext.svg">
<img class="diagram-dark" alt="JIM System Context" src="diagrams/images/dark/jim-structurizr-1-SystemContext.svg">

## ✨ Key Features

<div class="grid cards" markdown>

-   :material-sync:{ .lg .middle } **[Hub-and-Spoke Synchronisation](concepts/architecture.md)**

    ---

    Central metaverse architecture for identity correlation across all Connected Systems. Bidirectional sync of Users, Groups, and custom object types.

-   :material-server-network:{ .lg .middle } **[Multi-Directory LDAP](connectors/jim-ldap-connector.md)**

    ---

    Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories, all supported out of the box.

-   :material-docker:{ .lg .middle } **[Container-Native Deployment](administration/deployment.md)**

    ---

    Deploys as a single Docker stack with no legacy infrastructure requirements. Bundled or external PostgreSQL.

-   :material-shield-lock:{ .lg .middle } **[Single Sign-On (SSO)](administration/sso-setup.md)**

    ---

    OpenID Connect authentication with any OIDC-compliant Identity Provider. PKCE for enhanced security.

-   :material-function-variant:{ .lg .middle } **[Expression-Based Transforms](concepts/expressions.md)**

    ---

    Transform data using expressions with built-in functions for common identity operations.

-   :material-api:{ .lg .middle } **[REST API & PowerShell](api/index.md)**

    ---

    Full REST API with OpenAPI documentation, plus a cross-platform PowerShell module for automation and Identity as Code.

-   :material-wifi-off:{ .lg .middle } **[Air-Gapped Ready](administration/deployment.md#air-gapped-deployment)**

    ---

    Fully functional without internet connectivity. No cloud dependencies -- designed for sensitive and high-assurance environments.

-   :material-puzzle:{ .lg .middle } **[Extensible Connectors](connectors/index.md)**

    ---

    Built-in LDAP and CSV connectors, with a framework for developing custom connectors for bespoke scenarios.

</div>

## 🎯 Scenarios

JIM supports common Identity Governance & Administration (IGA) scenarios:

- **Joiner/Mover/Leaver (JML) Automation:** Synchronise users from HR systems to directories, applications, and downstream systems
- **Attribute Writeback:** Keep HR systems current by writing IT-managed attributes back (e.g. email addresses, phone numbers)
- **Domain Consolidation:** Prepare for cloud migration, simplification, or organisational mergers
- **Domain Migration:** Support divestitures and system decommissioning
- **Identity Correlation:** Bring together user and entitlement data from disparate business applications

## 🚀 What Makes JIM Different

Enterprise identity synchronisation typically requires cloud connectivity, complex infrastructure, or expensive licensing. JIM takes a different approach: it deploys as a single Docker stack, runs entirely on-premises, and works in air-gapped networks with no external dependencies. Source-available code means you can inspect, audit, and verify everything JIM does with your identity data.

<div class="jim-capabilities" markdown>
- :material-check-circle: Air-gapped deployment
- :material-check-circle: No cloud dependencies
- :material-check-circle: Container-native
- :material-check-circle: Source available
- :material-check-circle: SSO with any OIDC provider
- :material-check-circle: Full REST API
- :material-check-circle: PowerShell automation
</div>

## 🗺️ Quick Links

<div class="grid cards" markdown>

-   :material-rocket-launch:{ .lg .middle } **[Getting Started](getting-started/index.md)**

    ---

    Deploy JIM and run your first synchronisation.

-   :material-book-open-variant:{ .lg .middle } **[Concepts](concepts/index.md)**

    ---

    Understand the metaverse, Connected Systems, Synchronisation Rules, and more.

-   :material-cog:{ .lg .middle } **[Administration](administration/index.md)**

    ---

    Configure, monitor, and manage your JIM deployment.

-   :material-power-plug:{ .lg .middle } **[Connectors](connectors/index.md)**

    ---

    Connect JIM to LDAP directories, CSV files, and more.

</div>

## State of Development

JIM has completed **pre-release stabilisation** and moved well beyond its initial MVP. The core identity lifecycle is fully functional:

- **Import** identities from source systems (LDAP, CSV)
- **Sync** to reconcile identities in the central metaverse
- **Export** changes to target systems with Pending Export management
- **Schedule** automated synchronisation using cron or interval-based triggers

The platform has been hardened for production, with bounded-memory pipelines proven at 100K+ object scale, an OWASP Top 10:2025 assessment, supply chain hardening, and comprehensive integration test coverage across all synchronisation scenarios. See the [Product Roadmap](reference/roadmap.md) for what is coming as JIM progresses towards its first stable release.

## 💬 Community & Support

JIM is built in the open. [GitHub Discussions](https://github.com/TetronIO/JIM/discussions) is the place to engage with the maintainers and other users.

- **Questions and setup help**<br /> Start a thread in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category. Search existing threads first.
- **Feature ideas and suggestions**<br /> Post in the [Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas) category. Upvotes on existing ideas inform roadmap prioritisation; prefer adding signal to a duplicate over creating a new thread.
- **Bug reports**<br /> Open a [GitHub Issue](https://github.com/TetronIO/JIM/issues).
- **Security vulnerabilities**<br /> Follow the [Security Policy](https://github.com/TetronIO/JIM/security/policy); please do not report security issues in public Issues or Discussions.

## Licensing

JIM uses a Source-Available model where it is free to use in non-production scenarios, but requires a commercial licence for use in production scenarios. [Full details can be found here](https://junctional.io/license).

## More Information

Please visit [https://junctional.io](https://junctional.io) for more information.
