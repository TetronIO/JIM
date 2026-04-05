# JIM - AI Assistant Context Document

> **Purpose**: This document provides context for AI assistants to help with ideation, research, and discussions about JIM (Junctional Identity Manager).
>
> **Repository**: https://github.com/TetronIO/JIM
>
> **Document Version**: 1.4
>
> **Last Updated**: 2026-04-02
>
> **Note**: This is a snapshot. For current implementation details, check the repository or ask the user to provide updated code/docs.

---

## Quick Reference

| Aspect | Detail |
|--------|--------|
| **Product** | JIM - Junctional Identity Manager |
| **Company** | Tetron |
| **Type** | Enterprise Identity Lifecycle Management (ILM) |
| **Stack** | .NET 9.0, EF Core, PostgreSQL, Blazor Server |
| **UI Framework** | MudBlazor (Material Design) |
| **Auth** | OpenID Connect (OIDC) with PKCE |
| **Deployment** | Docker containers, air-gapped capable |
| **License** | Source-available (free non-production, commercial for production) |
| **Status** | Active development, approaching v0.9 stabilisation |
| **Language** | British English (en-GB) for all text |

---

## 1. What is JIM?

JIM is a self-hosted, on-premises identity management platform that synchronises identity data between connected systems through a central "metaverse" hub.

### Core Value Proposition

- **Modern Architecture**: Container-native, no legacy infrastructure
- **Air-Gapped Ready**: Works without internet connectivity
- **Self-Hosted**: Full control, no cloud dependencies
- **Source Available**: Transparent, auditable code

### Target Scenarios

1. **Joiner/Mover/Leaver (JML) Automation** - HR to directory sync
2. **Attribute Writeback** - IT-managed attributes back to HR
3. **Entitlement Management** - Centralised group membership
4. **Domain Consolidation/Migration** - M&A, cloud prep, divestitures
5. **Identity Correlation** - Unified view across disparate systems

---

## 2. Architecture

### The Metaverse Pattern

```
+-------------------+      +----------------+      +-------------------+
|   Source Systems  |      |   Metaverse    |      |   Target Systems  |
|                   |----->|                |----->|                   |
|  - HR System      |      |  - Identity    |      |  - Active Dir     |
|  - Badge System   |      |    Objects     |      |  - ServiceNow     |
+-------------------+      +----------------+      +-------------------+
         |                         |                         |
         v                         v                         v
       IMPORT                     SYNC                    EXPORT
```

**Key Principle**: All identity data flows through the metaverse. Never sync directly between connected systems.

### Core Entities

| Entity | Purpose |
|--------|---------|
| **MetaverseObject (MVO)** | Central identity entity (Person, Group, custom types) |
| **ConnectedSystemObject (CSO)** | External system's representation of an identity |
| **SyncRule** | Bidirectional mapping defining data flow |
| **ConnectedSystem** | Configuration for an external system |
| **Connector** | Adapter that communicates with external systems |

### Data Flow Operations

1. **Import** - Pull data from connected systems into connector space (CSOs)
2. **Sync** - Apply sync rules to project CSO data to/from MVOs
3. **Export** - Push pending changes from connector space to connected systems

### Software Components

| Component | Role | Technology |
|-----------|------|------------|
| **JIM.Web** | UI + REST API (at `/api/`) | Blazor Server |
| **JIM.Worker** | Background task processor | .NET Console |
| **JIM.Scheduler** | Scheduled job execution | .NET Console |
| **JIM.Database** | Data persistence | PostgreSQL 18 |
| **JIM.PowerShell** | Automation module | PowerShell 7+ |

### Layer Structure

```
+------------------------------------------+
|  Presentation: JIM.Web (Blazor + API)    |
+------------------------------------------+
|  Application: JIM.Application (Servers)  |
+------------------------------------------+
|  Domain: JIM.Models (Entities, DTOs)     |
+------------------------------------------+
|  Data: JIM.PostgresData (EF Core)        |
+------------------------------------------+
|  Integration: JIM.Connectors             |
+------------------------------------------+
```

---

## 3. Key Concepts

### Attributes

Properties on MVOs and CSOs with typed values:

| Type | Example |
|------|---------|
| Text | "John Smith" |
| Number | 42 |
| LongNumber | 9223372036854775807 |
| DateTime | 2026-01-23T10:30:00Z |
| Boolean | true/false |
| Binary | byte[] |
| Reference | Pointer to another MVO |
| Guid | UUID |

Attributes can be single-valued or multi-valued.

### Attribute Flows

Rules defining how attributes map between CSOs and MVOs:

- **Inbound** (Import): CSO attribute → MVO attribute
- **Outbound** (Export): MVO attribute → CSO attribute

Flows can include transformations using expressions.

### Object Lifecycle

```
+---------------+
|    Joiner     |  New identity appears in source
+-------+-------+
        |
        v
+---------------+
|    Active     |  Identity exists, attributes flow
+-------+-------+
        |
        v
+---------------+
|    Mover      |  Attributes change (dept, title, etc.)
+-------+-------+
        |
        v
+---------------+
|    Leaver     |  Identity removed/disabled in source
+---------------+
```

### Provisioning vs Deprovisioning

- **Provisioning**: Creating accounts in target systems when MVO is created/joined
- **Deprovisioning**: Disabling/deleting accounts when MVO is disconnected/deleted

### Deletion Rules

| Rule | Behaviour |
|------|-----------|
| Manual | Admin must manually delete MVO |
| WhenLastConnectorDisconnected | Delete MVO when no CSOs remain connected |
| WhenAuthoritativeSourceDisconnected | Delete MVO when the authoritative source CSO disconnects |

Grace periods allow time before actual deletion (e.g., 30 days).

---

## 4. Connectors

### Available Connectors

| Connector | Import | Export | Notes |
|-----------|--------|--------|-------|
| **LDAP/Active Directory** | ✓ | ✓ | Full CRUD, includes Samba AD, SSL/TLS, container creation |
| **OpenLDAP/RFC 4512** | ✓ | ✓ | OpenLDAP, 389 Directory Server, RFC 4512-compliant directories; parallel imports, accesslog delta import, partition-scoped imports |
| **File (CSV/Text)** | ✓ | ✓ | Configurable delimiters, auto-confirm export |

### Planned Connectors (Post-MVP)

| Connector | Notes |
|-----------|-------|
| **SCIM 2.0** | Standard protocol (design doc exists) |
| **SQL** | Database connector (SQL Server, PostgreSQL, MySQL, Oracle) |
| **PowerShell** | Custom scripts |
| **Web Services/REST** | OAuth2/API key auth |

### Connector Capabilities

Connectors implement capability interfaces:

```csharp
IConnector                    // Base interface
IConnectorImportUsingCalls    // Pull data via API calls
IConnectorImportUsingFiles    // Read from files
IConnectorExportUsingCalls    // Push data via API calls (async with CancellationToken)
IConnectorExportUsingFiles    // Write to files (async with CancellationToken)
```

Connectors also declare capability flags via `IConnectorCapabilities`:
- `SupportsParallelExport`: enables per-system `MaxExportParallelism` setting for parallel batch processing
- LDAP connectors additionally support configurable "Export Concurrency" (1-16) for async LDAP operation pipelining

---

## 5. Sync Rules

Sync rules define the relationship between connected systems and the metaverse.

### Rule Components

| Component | Purpose |
|-----------|---------|
| **Direction** | Inbound (source→MV) or Outbound (MV→target) |
| **Scope** | Filter which CSOs the rule applies to |
| **Join Rules** | How to match CSO to existing MVO |
| **Projection** | What to do when no MVO match (create new) |
| **Attribute Flows** | Which attributes to sync and how |

### Expression Language

Attribute flows can use expressions for transformations:

```
// Simple mapping
employeeId

// Concatenation
firstName + " " + lastName

// Conditional
IIF(department == "IT", "tech-" + username, username)

// Functions
Left(employeeId, 4) + "-" + Right(employeeId, 4)
ToUpper(countryCode)
FormatDateTime(hireDate, "yyyy-MM-dd")
```

### Built-in Functions

| Category | Examples |
|----------|----------|
| String | `Left`, `Right`, `Mid`, `Trim`, `ToUpper`, `ToLower`, `Replace` |
| Date | `FormatDateTime`, `DateAdd`, `DateDiff` |
| Logic | `IIF`, `IsNull`, `Coalesce` |
| Reference | `ResolveReference`, `GetReferencedValue` |
| Conversion | `ToString`, `ToNumber`, `ToBoolean` |

---

## 6. API

### Authentication

| Method | Use Case |
|--------|----------|
| **JWT Bearer** | Interactive applications, SSO |
| **API Key** | Automation, CI/CD, scripts |

### Key Endpoints (v1)

14 API controllers with 120 endpoints. Key examples:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/metaverse/objects` | Query MVOs |
| `GET /api/v1/metaverse/object-types` | List/manage MVO types and attributes |
| `GET /api/v1/synchronisation/connected-systems` | List connected systems |
| `POST /api/v1/synchronisation/connected-systems/{csId}/run-profiles/{rpId}/execute` | Trigger sync |
| `GET /api/v1/activities` | Monitor operations |
| `GET /api/v1/schedules` | Manage schedules |
| `GET /api/v1/schedule-executions` | Monitor schedule runs |
| `GET /api/v1/health` | Health/readiness/liveness probes |
| `GET /api/v1/certificates` | Manage trusted certificates |
| `GET /api/v1/history/deleted-objects/mvo` | View deleted objects |
| `GET /api/v1/logs` | Unified log viewer (app + PostgreSQL) |
| `GET /api/v1/synchronisation/sync-rules/{id}/matching-rules` | Manage object matching rules |

Full Swagger documentation available at `/api/swagger`.

### PowerShell Module

78 cmdlets for automation:

```powershell
# Connect interactively (opens browser for SSO)
Connect-JIM -Url "http://localhost:5200"

# Connect with API key (for automation/scripts)
Connect-JIM -Url "http://localhost:5200" -ApiKey "jim_xxx"

# Query
Get-JIMMetaverseObject -ObjectType Person
Get-JIMMetaverseObject -ObjectType Person -All  # Auto-paginate all results
Get-JIMConnectedSystem -Name "HR System"

# Execute
Start-JIMRunProfile -Name "HR Full Import"
Get-JIMActivity -Status InProgress

# Configure
New-JIMSyncRule -Name "HR Inbound" -Direction Inbound
New-JIMConnectedSystem -Name "AD" -ConnectorType LdapConnector
```

---

## 7. Security Model

### Authentication

- **SSO/OIDC Required**: No local accounts
- **PKCE Flow**: Enhanced security for web auth
- **IdP Agnostic**: Works with any OIDC-compliant identity provider

### Authorisation

- **Role-Based**: Claims from OIDC mapped to roles
- **Metaverse-Driven**: Roles defined in metaverse objects

### Data Protection

- **Credential Encryption**: AES-256-GCM at rest
- **No Cloud Dependencies**: All secrets stored locally
- **Air-Gapped Support**: Works without internet

---

## 8. Current Status

### Core Platform (Complete)

- ✅ Connector framework with LDAP, OpenLDAP, and File connectors (import and export)
- ✅ Full inbound sync (join, project, attribute flow)
- ✅ Full outbound sync (provisioning, export)
- ✅ LDAP/AD export (create, update, delete, container creation)
- ✅ File connector export
- ✅ MVO deletion rules with grace periods
- ✅ Background processing (Worker service)
- ✅ Scheduler service with cron/interval triggers and multi-step execution
- ✅ Admin UI (operations, config, monitoring)
- ✅ Dashboard home page with system overview
- ✅ API with JWT and API key auth (14 controllers, 120 endpoints)
- ✅ PowerShell module (78 cmdlets)
- ✅ Docker deployment with air-gapped bundles
- ✅ Integration testing framework (7 scenarios)
- ✅ Credential encryption
- ✅ Change history/audit with timeline UI and deleted objects view
- ✅ Real-time progress indication on Operations page
- ✅ Unified log viewer (application + PostgreSQL logs)

### Recent Enhancements (v0.8.0)

- ✅ **OpenLDAP Connector Support** (#72) - Full OpenLDAP/RFC 4512 LDAP directory support with parallel imports, accesslog delta import, and partition-scoped imports; also supports 389 Directory Server and other RFC 4512-compliant directories
- ✅ **Worker Redesign** (#394) - ISyncEngine (pure domain engine) and ISyncRepository (data access boundary) with full DI throughout Worker/Scheduler, ParallelBatchWriter, and COPY binary protocol for bulk persistence
- ✅ **Bundled Keycloak IdP** (#197) - Zero-config SSO for development environments with pre-configured identity provider
- ✅ **O(1) Import Matching** (#440) - Constant-time import matching eliminates linear scans for large connector spaces
- ✅ **Cross-Batch Fixup Elimination** (#427) - Removes redundant cross-batch CSO reference fixup passes during synchronisation
- ✅ **MVO COPY Binary Protocol** (#338) - Binary COPY protocol for bulk MVO persistence, dramatically reducing database round-trips
- ✅ **Object Type Icons** (#92) - Visual object type icons throughout the UI for improved navigation
- ✅ **Pending Export Detail** - Drill into pending export changes before they are sent to target systems
- ✅ **Activity Auto-Refresh** - Activities page automatically refreshes to show real-time operation progress
- ✅ **Run Profile Editing** - Edit existing run profile configurations without recreating them
- ✅ **Tabs View** - Tabbed navigation for connected system and metaverse object views
- ✅ **Healthchecks** (#185) - Container health probes for orchestration readiness and liveness monitoring
- ✅ **MVA to SVA Flow** (#435) - Attribute flow support for multi-valued to single-valued attribute mappings
- ✅ **Case-Insensitive Expression Lookups** (#341) - Expression attribute lookups are now case-insensitive, matching expected behaviour
- ✅ **PE Reconciliation for All Data Types** (#263) - Pending export reconciliation extended to cover all attribute data types

### Recent Enhancements (v0.8.1)

- ✅ **Pre-Export CREATE→DELETE Reconciliation** (#218) - When an object is created and then deleted before export runs, the redundant pending exports are automatically cancelled
- ✅ **Export Rule Evaluation Optimisation** (#417) - Export rule evaluation optimised to reduce per-MVO processing cost
- ✅ **AD Schema Discovery Batching** (#433) - Active Directory schema discovery now batches LDAP queries, reducing connection round-trips
- ✅ **Cross-Page Reference Resolution Fix** (#449) - Full Sync no longer fails with entity tracking conflicts when groups share members across resolution batches (10,000+ users)
- ✅ **Error Message Cleanup** (#448) - Error messages no longer display the internal "EMERGENCY UPDATE" prefix
- ✅ **Context-Aware Breadcrumbs** - Activity and RPEI detail page breadcrumbs are now context-aware
- ✅ **Log Injection Prevention** (#444) - Sanitised Request.Method in global exception handler logging to prevent log injection (CWE-117)

### Previous Enhancements

- ✅ **Disconnection Causality Tracking** (#392) - Causality tree traces MVO attribute changes and deletion fate during disconnection and recall
- ✅ **Self-Contained Object Matching Rules** (#386) - Sync rules carry their own matching logic for import and export, enabling portable rule definitions
- ✅ **One-Command Deployment** - Interactive installer auto-detects latest release, configures SSO and database, and starts JIM in minutes
- ✅ **Sync Outcome Graph** (#363) - Full causal tracing of every change during synchronisation with configurable tracking levels (None/Standard/Detailed)
- ✅ **CSO Large MVA Pagination** (#320) - Paginated attribute values with server-side search for objects with 10K+ multi-valued attributes
- ✅ **Large-Scale Import Optimisation** - 100K+ object imports without out-of-memory through batch processing and raw SQL persistence
- ✅ **Worker Database Performance Optimisation** (#338) - Raw SQL bulk operations, service-lifetime CSO lookup index, lightweight ID-only MVO join lookups (~34% faster FullSync, ~37% faster ProcessConnectedSystemObjects)
- ✅ **Export Performance Optimisation** - Batch DB operations, LDAP async pipelining (Export Concurrency 1-16), parallel batch export (MaxExportParallelism), parallel schedule step execution
- ✅ **Granular Activity Stats** (#332) - 16 per-change-type stat fields, run-type-aware display, scheduler step failure detection
- ✅ **Export Change History** - Drill into exactly which attributes changed on each exported object with before/after values
- ✅ **Hardened Release Pipeline** - Container scanning, SBOM attestation, and build validation
- ✅ **Interactive PowerShell Auth** (#296) - Browser-based SSO authentication for PowerShell module
- ✅ **Security Compliance Documentation** - NCSC, CISA, OWASP ASVS compliance mapping

### Roadmap

Development follows sequenced milestones (see [GitHub Milestones](https://github.com/TetronIO/JIM/milestones)):

| Milestone | Focus |
|-----------|-------|
| **v0.9-STABILISATION** | Configuration controls, identity fusing, lifecycle state management (JML triggers), sync engine refinement, architectural foundation for extensibility |
| **v1.0-ILM-COMPLETE** | First production-ready release: robust sync rules, lifecycle automation, scheduling, error handling, operational monitoring |
| **v1.x-CONNECTORS** | Expanding connector coverage: broader LDAP support, SQL databases, SCIM endpoints, HR systems, connector framework improvements |
| **v2.0-IGA-FOUNDATION** | Step-change into identity governance: entitlement visibility, access request workflows, attestation capabilities |

---

## 9. Design Principles

### Self-Contained

- No cloud service dependencies
- Works in air-gapped environments
- All features work on-premises only

### UI-First Configuration

- Prefer admin UI over environment variables
- Guided setup wizards
- Environment variables only for bootstrap/secrets

### Synchronisation Integrity

- Data integrity is paramount
- Fail fast rather than corrupt data
- Comprehensive error reporting
- All errors visible in Activities

### British English

All text uses British spelling:
- synchronisation, authorisation, behaviour
- colour, centre, licence (noun)
- organise, analyse, minimise

---

## 10. How to Use This Document

### For Ideation Sessions

When discussing new features:
1. Check Section 8 (Current Status) to understand what exists
2. Reference Section 3 (Key Concepts) for domain terminology
3. Consider Section 9 (Design Principles) for constraints

### For Architecture Discussions

1. Review Section 2 (Architecture) for the metaverse pattern
2. Check Section 4 (Connectors) for integration capabilities
3. Reference Section 5 (Sync Rules) for data flow mechanics

### For API/Integration Questions

1. See Section 6 (API) for endpoint patterns
2. Check Section 7 (Security Model) for auth requirements

### For Current Code/Implementation

Ask the user to provide:
- Specific files from the repository
- Current state of a feature
- Recent changes or PRs

### Repository Structure

```
JIM/
+-- src/                     # Source projects
|   +-- JIM.Web/             # Blazor UI + REST API
|   +-- JIM.Application/     # Business logic (Servers)
|   +-- JIM.Models/          # Domain entities
|   +-- JIM.Data/            # Repository interfaces
|   +-- JIM.PostgresData/    # EF Core data access
|   +-- JIM.Connectors/      # Connector implementations
|   +-- JIM.Utilities/       # Shared utilities
|   +-- JIM.Worker/          # Background processor
|   +-- JIM.Scheduler/       # Scheduled tasks
|   +-- JIM.PowerShell/      # PowerShell module
+-- test/                    # Unit, workflow, integration tests
+-- docs/                    # Documentation
|   +-- plans/               # Feature design documents
+-- .devcontainer/           # GitHub Codespaces config
```

---

## 11. Glossary

| Term | Definition |
|------|------------|
| **Activity** | Logged operation (import, sync, export) with status and timing |
| **Attribute Flow** | Rule mapping attribute between CSO and MVO |
| **Connector** | Adapter for communicating with external systems |
| **Connector Space** | Staging area for CSOs before/after sync |
| **CSO** | ConnectedSystemObject - external system's identity representation |
| **Deprovisioning** | Removing/disabling accounts in target systems |
| **Expression** | Formula for transforming attribute values |
| **Grace Period** | Time before scheduled deletion executes |
| **Join** | Linking a CSO to an existing MVO |
| **Metaverse** | Central authoritative identity repository |
| **MVO** | MetaverseObject - central identity entity |
| **Obsoletion** | Marking a CSO as no longer existing in source |
| **Partition** | Logical division within a connected system (e.g., OU) |
| **Pending Export** | Queued change waiting to be sent to target system |
| **Projection** | Creating a new MVO when no match found |
| **Provisioning** | Creating accounts in target systems |
| **Run Profile** | Configured operation (Full Import, Full Sync, Export) |
| **Sync Rule** | Complete mapping configuration between systems and metaverse |

---

## 12. Links

- **Repository**: https://github.com/TetronIO/JIM
- **Website**: https://tetron.io/jim
- **Licensing**: https://tetron.io/jim/#licensing

---

*This document is designed to be uploaded to AI assistant projects. Update periodically as JIM evolves. Check the Document Version in the header to verify you have the latest version deployed.*
