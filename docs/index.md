---
title: Junctional Identity Manager Docs
description: A modern, self-hosted Identity Management system for organisations with complex identity synchronisation requirements.
type: landing
audience: evaluator
validated: v0.16.0
template: home.html
hide:
  - navigation
  - toc
---

--8<-- "assets/diagrams/system-context.svg"

<p class="jim-diagram-caption">JIM in context: administrators and automation work through its UI and API while it synchronises identity data with your systems.<span class="jimdg-caption-motion"> Moving dots trace identity data in flight.</span></p>

## :material-star-four-points-outline: Key Features

<div class="grid cards" markdown>

-   :material-sync:{ .lg .middle } **[Hub-and-Spoke Synchronisation](concepts/architecture.md)**

    ---

    A central Metaverse correlates each person and group across every Connected System. Users, Groups and your own Metaverse Object Types synchronise in both directions.

-   :material-server-network-outline:{ .lg .middle } **[Multi-Directory LDAP](connectors/jim-ldap-connector.md)**

    ---

    Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories, all supported out of the box.

-   :material-docker:{ .lg .middle } **[Container-Native Deployment](administration/deployment.md)**

    ---

    Deploys as a single container stack on Docker or Podman, served over HTTPS out of the box, with no legacy infrastructure requirements. Bundled or external PostgreSQL.

-   :material-shield-lock-outline:{ .lg .middle } **[Single Sign-On (SSO)](administration/sso-setup.md)**

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

    Fully functional without internet connectivity. No cloud dependencies; designed for sensitive and high-assurance environments.

-   :material-puzzle-outline:{ .lg .middle } **[Extensible Connectors](connectors/index.md)**

    ---

    Built-in LDAP, File, SCIM 2.0 and SQL (Microsoft SQL Server, Oracle Database) connectors, with a framework for developing custom connectors for bespoke scenarios.

-   :material-sort:{ .lg .middle } **[Attribute Priority](concepts/attribute-priority.md)**

    ---

    Deterministic precedence when multiple Connected Systems contribute the same Metaverse Attribute, with "Null is a value" assertion, next-contributor hand-over, and per-value provenance.

-   :material-history:{ .lg .middle } **[Configuration Change History](configuration/activities.md#configuration-change-history)**

    ---

    Versioned who/what/when audit across every configuration type, with secret redaction and a reason-for-change prompt.

-   :material-shape-plus-outline:{ .lg .middle } **[Extensible Metaverse Schema](configuration/metaverse.md)**

    ---

    Create your own Metaverse Object Types and Attributes from the portal, REST API or PowerShell, with safe deletion and full audit. Built-in attributes carry SCIM 2.0 and LDAP Standard Mappings.

-   :material-lightning-bolt-outline:{ .lg .middle } **[Real-Time Operations](configuration/activities.md)**

    ---

    Run Profile progress, throughput and time remaining stream live to the portal, pushed from the database rather than polled, with `Get-JIMActivity -Follow` for the terminal. [Service Health](configuration/operations.md#service-health) shows whether the Worker and Scheduler are alive and what each is doing.

-   :material-form-textbox-password:{ .lg .middle } **[Passwords](concepts/passwords.md)**

    ---

    Initial passwords for newly provisioned objects, on-demand password setting, and Password Synchronisation to every configured Connected System, queued, encrypted and retried, with a queue you can watch, retry and cancel.

-   :material-eye-check-outline:{ .lg .middle } **[Preview Before You Commit](configuration/configuration-changes.md#previewing-a-change-before-you-make-it)**

    ---

    A Configuration Change Preview shows which objects a proposed edit would affect; [Sync Preview](configuration/sync-preview.md) shows what synchronising one object would do. Neither changes anything.

-   :material-shield-check-outline:{ .lg .middle } **[Run Profile Safeguards](configuration/run-profiles.md#safeguards)**

    ---

    Cap how many creates, updates, deletes and detected deletions a single run may attempt, so a broken filter or mistaken rule cannot turn one run into a mass change.

</div>

## :material-bullseye-arrow: Scenarios

JIM supports common Identity Governance & Administration (IGA) scenarios:

- **Joiner/Mover/Leaver (JML) Automation:** Synchronise users from HR systems to directories, applications, and downstream systems
- **Attribute Writeback:** Keep HR systems current by writing IT-managed attributes back (e.g. email addresses, phone numbers)
- **Domain Consolidation:** Prepare for cloud migration, simplification, or organisational mergers
- **Domain Migration:** Support divestitures and system decommissioning
- **Identity Correlation:** Bring together user and entitlement data from disparate business applications

## :material-rocket-launch-outline: What Makes JIM Different

Enterprise identity synchronisation typically requires cloud connectivity, complex infrastructure, or expensive licensing. JIM takes a different approach: it deploys as a single container stack on Docker or Podman, runs entirely on-premises, and works in air-gapped networks with no external dependencies. Source-available code means you can inspect, audit, and verify everything JIM does with your identity data.

<div class="jim-capabilities" markdown>
- :material-check-circle: Air-gapped deployment
- :material-check-circle: No cloud dependencies
- :material-check-circle: Container-native
- :material-check-circle: Source available
- :material-check-circle: SSO with any OIDC provider
- :material-check-circle: Full REST API
- :material-check-circle: PowerShell automation
</div>

## :material-map-outline: Quick Links

<div class="grid cards" markdown>

-   :material-rocket-launch-outline:{ .lg .middle } **[Getting Started](getting-started/index.md)**

    ---

    Deploy JIM and run your first synchronisation.

-   :material-book-open-variant-outline:{ .lg .middle } **[Concepts](concepts/index.md)**

    ---

    Understand the Metaverse, Connected Systems, Synchronisation Rules, and more.

-   :material-cog-outline:{ .lg .middle } **[Administration](administration/index.md)**

    ---

    Configure, monitor, and manage your JIM deployment.

-   :material-power-plug-outline:{ .lg .middle } **[Connectors](connectors/index.md)**

    ---

    Connect JIM to LDAP directories, SQL databases, SCIM 2.0 services, CSV files, and more.

</div>

## :material-chart-line-variant: Scale and Assurance

JIM covers the whole identity lifecycle:

- **Import** identities from source systems: LDAP directories, SQL databases, SCIM 2.0 services and CSV files.
- **Synchronise** them through the central Metaverse.
- **Export** changes to target systems, each one held as a Pending Export until it is confirmed.
- **Schedule** all of it, by cron expression or interval.

Its pipelines run in bounded memory, and are validated at 500,000 users synchronised across two directories, with groups of up to 495,000 members. Its security is assessed against the OWASP Top 10:2025, and its supply chain is hardened. The [Product Roadmap](reference/roadmap.md) sets out the milestones to v1.0, Identity Lifecycle Complete, and after it.

## :material-forum-outline: Community & Support

JIM is built in the open. [GitHub Discussions](https://github.com/TetronIO/JIM/discussions) is the place to engage with the maintainers and other users.

- **Questions and setup help**<br /> Start a thread in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category. Search existing threads first.
- **Feature ideas and suggestions**<br /> Post in the [Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas) category. Upvotes on existing ideas inform roadmap prioritisation; prefer adding signal to a duplicate over creating a new thread.
- **Bug reports**<br /> Open a [GitHub Issue](https://github.com/TetronIO/JIM/issues).
- **Security vulnerabilities**<br /> Follow the [Security Policy](https://github.com/TetronIO/JIM/security/policy); please do not report security issues in public Issues or Discussions.

## :material-scale-balance: Licensing

JIM is source-available: it is free to use outside production, and production use needs a commercial licence. The [licence terms](https://junctional.io/license) give the details.

## :material-information-outline: More Information

For more about JIM, visit [junctional.io](https://junctional.io).
