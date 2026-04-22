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

JIM's step-change from identity synchronisation into identity governance. Establishes the governance layer that enables compliance-driven organisations to manage access holistically, not just synchronise it.

- **Direct metaverse object management** -- create, view, and manage identity objects directly within the UI
- **Entitlement visibility and mapping** -- discover and map entitlements across connected systems
- **Entitlement management** -- manage access assignments with request workflows and attestation:
    - Access request workflows with approval chains
    - Basic attestation and certification capabilities
- **Compliance reporting** -- pre-built and customisable reports for audit and regulatory requirements
- **Role-based access management** -- define and assign roles that bundle entitlements across connected systems
- **Policy engine** -- declarative rules for separation of duties, access request approval, and risk scoring
