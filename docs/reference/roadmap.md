# Product Roadmap

JIM has reached **MVP completion**. The core identity lifecycle -- Import, Sync, Export, and Schedule -- is fully functional. The roadmap below outlines planned milestones as JIM progresses towards a stable release and beyond.

For the latest milestone status and issue tracking, see the [GitHub milestones](https://github.com/TetronIO/JIM/milestones).

---

## 🔨 v0.9 -- v0.10 -- Pre-release Stabilisation

Hardening and polish ahead of the first stable release. Delivered:

- Bounded-memory pipelines tested at 100K+ object scale
- EF Core query defaults tuned for read-heavy workloads (AsNoTracking by default with explicit write-path opt-in)
- Sync integrity overhaul: cross-page reference resolution, change-record persistence, entity tracking conflicts resolved
- Integration test coverage across all sync scenarios with automated metrics streaming
- OWASP Top 10:2025 assessment completed with targeted hardening
- Supply chain hardening: Docker base image digests pinned, GitHub Actions pinned by SHA, main branch protection with required status checks
- Interactive Scalar API reference available in every environment (including air-gapped), with a public snapshot hosted on the documentation site
- Role membership management API and PowerShell cmdlets
- Service identity (Service Name and Service ID) for distinguishing JIM instances
- OIDC sign-out with identity provider support
- Predefined Searches that can be disabled and re-enabled without deletion

---

## 🎯 v1.0 -- Identity Lifecycle Complete

The first stable release, delivering a production-ready identity lifecycle platform.

- Expression engine enhancements (additional functions, improved error reporting)
- Advanced scheduling capabilities (dependencies, conditional execution)
- Comprehensive REST API coverage for all administrative operations
- Full PowerShell module coverage with parity across all API endpoints

---

## 🌳 v1.x -- Connector Ecosystem

Expanding the range of systems JIM can connect to out of the box.

| Connector | Description | Target |
|---|---|---|
| JIM SQL Server Connector | Microsoft SQL Server databases | v1.x |
| JIM PostgreSQL Connector | PostgreSQL databases | v1.x |
| JIM MySQL Connector | MySQL databases | v1.x |
| JIM Oracle Connector | Oracle databases | v1.x |
| JIM PowerShell Connector | PowerShell Core scripts | v1.x |
| JIM SCIM Connector | SCIM 2.0 endpoints | v1.x |
| JIM REST Connector | REST API web services | v1.x |

Each connector will follow JIM's established connector architecture, supporting schema discovery, full and delta import, and export with the same reliability guarantees as the built-in connectors.

---

## 🏛️ v2.0 -- IGA Foundation

Evolves JIM's core IDAM capabilities so identities can be managed directly in JIM, without depending on Source-of-Record systems for everyday changes. The focus is depth in the existing identity surface (Users, Groups, custom types) rather than branching into adjacent domains.

### Entitlement Management

- **Direct group management**<br /> Create, update, and delete groups directly within JIM rather than only synchronising them from Connected Systems.
- **Governance**<br /> Access reviews and attestation, delegated administration, dynamic memberships, time-based memberships, self-service requests, and approval workflows, etc.

### Identity Lifecycle Management

- **Direct user management**<br /> Create, update, and delete users directly within JIM rather than only synchronising them from Connected Systems.
- **Self-service for locally-managed attributes**<br /> Allow users to maintain attributes owned by JIM (photos, pronouns, bios, and similar) rather than relying on upstream systems.
- **Lifecycle Workflows**<br /> Event-driven workflows that automate joiner/mover/leaver processes end-to-end.

### Fine-grained RBAC

- **Custom permission models**<br /> Granular roles and permissions inside JIM itself, so administrators can shape access to JIM's functionality to match their organisation's structure.
