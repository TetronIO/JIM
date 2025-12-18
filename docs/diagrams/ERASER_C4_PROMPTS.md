# Eraser.io C4 Model Diagram Prompts for JIM

This document contains prompts optimised for Eraser.io's AI diagram generation feature to create C4 model diagrams for the JIM Identity Management System.

---

## 1. C4 Model - System Context Diagram

**Prompt:**

```
Create a C4 System Context diagram for JIM Identity Management System.

Title: JIM - System Context

CENTRAL SYSTEM:
- JIM (Identity Management System): A central identity hub implementing the metaverse pattern. Synchronises identities, attributes, and group memberships across heterogeneous enterprise systems with bidirectional data flow and transformation capabilities.

EXTERNAL SYSTEMS:
- Active Directory / LDAP: Enterprise directory services. JIM imports users/groups, exports provisioning changes, supports delta sync.
- HR Systems: Authoritative source for employee identity data. JIM imports employee records via CSV or database connectors.
- Enterprise Databases: PostgreSQL, MySQL, Oracle, SQL Server containing identity data. JIM reads and writes identity information.
- File Systems: CSV file-based data exchange for bulk import/export operations.
- SCIM 2.0 Systems: Modern cloud applications supporting identity provisioning protocol.

SOFTWARE COMPONENTS (distributed separately):
- JIM PowerShell Module: Cross-platform PowerShell module (JIM.PowerShell) providing 35+ cmdlets for automation, scripting, and CI/CD integration. Communicates with JIM via REST API.

PEOPLE:
- Administrator: Identity management administrator who configures connected systems, creates sync rules, defines run profiles, monitors activities, and resolves synchronisation issues via web UI or PowerShell.
- Identity Provider (OIDC): External authentication provider (Keycloak, Entra ID, Auth0, AD FS) that authenticates administrators using OpenID Connect. JIM has no local user accounts.
- Automation Client: External systems, CI/CD pipelines, and scripts that interact with JIM programmatically via REST API or PowerShell module using API keys.

RELATIONSHIPS:
- Administrator uses JIM [Manages identity synchronisation via Blazor web UI]
- Administrator uses JIM PowerShell Module [Scripting and automation via cmdlets]
- Identity Provider authenticates users for JIM [OIDC/SSO authentication, no local accounts]
- Automation Client uses JIM PowerShell Module [CI/CD pipelines, scheduled scripts]
- Automation Client calls JIM [Direct REST API with API key authentication]
- JIM PowerShell Module calls JIM [REST API at /api/v1/, API key authentication]
- JIM synchronises with Active Directory / LDAP [LDAP/LDAPS protocol, bidirectional sync]
- JIM imports from HR Systems [CSV files, database queries, employee master data]
- JIM synchronises with Enterprise Databases [SQL connections, read/write identity data]
- JIM exchanges data with File Systems [CSV import/export, bulk operations]
- JIM provisions to SCIM 2.0 Systems [SCIM protocol, create/update/delete users]

Style: Use C4 context diagram conventions with system boxes and person shapes. JIM should be prominently centred.
```

---

## 2. C4 Model - Container Diagram

**Prompt:**

```
Create a C4 Container diagram for JIM Identity Management System.

Title: JIM - Container Diagram

SYSTEM BOUNDARY: JIM Identity Management System

CONTAINERS:

1. Web Application [Container: ASP.NET Core 9.0, Blazor Server, MudBlazor]
   - Provides interactive admin UI for identity management
   - Hosts REST API endpoints at /api/v1/
   - Swagger documentation at /api/swagger
   - Handles OIDC authentication and API key validation
   - Port: 5200 (HTTP), 5201 (HTTPS)

2. Worker Service [Container: .NET 9.0 Background Service]
   - Processes queued synchronisation tasks
   - Executes import, sync, and export operations
   - Runs connectors to interact with external systems
   - Manages task queue with retry logic
   - Stateless, horizontally scalable

3. Scheduler Service [Container: .NET 9.0 Background Service]
   - Triggers scheduled synchronisation jobs
   - Creates worker tasks based on run profile schedules
   - Manages cron-like execution timing

4. PostgreSQL Database [Container: PostgreSQL 18]
   - Stores all JIM configuration and state
   - Metaverse objects and attributes
   - Connected system objects (staging area)
   - Sync rules and run profiles
   - Activity history and audit trail
   - Worker task queue

EXTERNAL SOFTWARE (outside boundary, distributed separately):
- JIM PowerShell Module [Software: PowerShell Module, Cross-platform]
   - 35+ cmdlets for JIM automation
   - Connect-JIM, Get-JIMMetaverseObject, Invoke-JIMRunProfile, etc.
   - Installed separately on administrator workstations or CI/CD agents
   - Communicates with Web Application via REST API

EXTERNAL SYSTEMS (outside boundary):
- Identity Provider: Authenticates administrators via OIDC
- Connected Systems: External systems (AD, HR, databases, files, SCIM)

PEOPLE:
- Administrator: Uses Web Application directly or via PowerShell Module
- Automation Client: Uses PowerShell Module or calls REST API directly

RELATIONSHIPS:
- Administrator uses Web Application [HTTPS, Blazor Server UI]
- Administrator uses JIM PowerShell Module [PowerShell cmdlets for scripting]
- Automation Client uses JIM PowerShell Module [CI/CD pipelines, scheduled scripts]
- Automation Client calls Web Application [HTTPS, REST API with API key]
- JIM PowerShell Module calls Web Application [HTTPS, REST API at /api/v1/]
- Identity Provider authenticates Web Application [OIDC/OpenID Connect]
- Web Application reads/writes PostgreSQL Database [Entity Framework Core, TCP 5432]
- Worker Service reads/writes PostgreSQL Database [Entity Framework Core, TCP 5432]
- Worker Service connects to Connected Systems [LDAP, SQL, HTTP, File I/O via connectors]
- Scheduler Service reads/writes PostgreSQL Database [Entity Framework Core, TCP 5432]
- Scheduler Service creates tasks for Worker Service [via database task queue]
- Web Application creates tasks for Worker Service [via database task queue]

Style: Use C4 container diagram conventions. Show containers within JIM system boundary. External systems outside boundary.
```

---

## 3. C4 Model - Component Diagrams

### 3.1 Web Application Components

**Prompt:**

```
Create a C4 Component diagram for the JIM Web Application container.

Title: JIM Web Application - Components

CONTAINER BOUNDARY: Web Application (ASP.NET Core 9.0, Blazor Server)

COMPONENTS:

API Controllers [Component: ASP.NET Core Controllers]
- REST API endpoints at /api/v1/
- Includes: MetaverseController, SynchronisationController, ActivitiesController, ApiKeysController, HealthController, SecurityController, CertificatesController, DataGenerationController
- Returns JSON responses, validates requests

Blazor Pages [Component: Blazor Server Components]
- Interactive admin UI pages
- Dashboard, Activity List/Detail, Claims display
- Admin configuration pages
- Type management pages
- Uses MudBlazor Material Design components

Authentication Middleware [Component: ASP.NET Core Middleware]
- OIDC/SSO authentication handler
- Bearer token validation for API keys
- Claims transformation
- Session management with cookies

JimApplication Facade [Component: C# Class]
- Main entry point to business logic
- Provides access to domain servers
- Coordinates cross-cutting concerns

Domain Servers [Component: C# Services via DI]
- MetaverseServer: Metaverse object CRUD and queries
- ConnectedSystemServer: Connected system management
- ActivityServer: Activity logging and audit
- TaskingServer: Worker task queue management
- SyncRuleServer: Sync rule configuration
- ExportEvaluationServer: Pending export determination
- SecurityServer: Role and permission management
- ServiceSettingsServer: System configuration
- CertificateServer: SSL certificate management

Repository Layer [Component: Entity Framework Core]
- IRepository interface implementation
- Data access abstraction
- PostgreSQL-specific implementation

EXTERNAL DEPENDENCIES (outside boundary):
- PostgreSQL Database
- Identity Provider (OIDC)
- JIM PowerShell Module (external software)
- Administrator (person)
- Automation Client (person)

RELATIONSHIPS:
- Administrator uses Blazor Pages [HTTPS, interactive UI]
- Administrator uses JIM PowerShell Module [PowerShell cmdlets]
- JIM PowerShell Module calls API Controllers [HTTPS, REST/JSON, API key auth]
- Automation Client calls API Controllers [HTTPS, REST/JSON]
- Automation Client uses JIM PowerShell Module [CI/CD scripting]
- API Controllers use JimApplication Facade
- Blazor Pages use JimApplication Facade
- Authentication Middleware validates requests for API Controllers
- Authentication Middleware validates requests for Blazor Pages
- JimApplication Facade delegates to Domain Servers
- Domain Servers use Repository Layer
- Repository Layer reads/writes PostgreSQL Database [EF Core]
- Authentication Middleware authenticates via Identity Provider [OIDC]

Style: C4 component diagram. Group related components. Show data flow direction.
```

---

### 3.2 Worker Service Components

**Prompt:**

```
Create a C4 Component diagram for the JIM Worker Service container.

Title: JIM Worker Service - Components

CONTAINER BOUNDARY: Worker Service (.NET 9.0 Background Service)

COMPONENTS:

Worker Host [Component: .NET BackgroundService]
- Main processing loop
- Polls database for queued tasks
- Manages task execution (sequential/parallel)
- Handles graceful shutdown
- 2-second polling interval when idle

Task Processors [Component: C# Classes]
- SyncImportTaskProcessor: Imports data from connectors into staging area
- SyncFullSyncTaskProcessor: Applies attribute flows, projects objects to metaverse
- SyncExportTaskProcessor: Exports pending changes to connected systems
- SyncRuleMappingProcessor: Applies transformation rules and attribute mappings

Connector Runtime [Component: C# Services]
- Loads connector implementations dynamically
- Invokes connector import/export methods
- Manages connector configuration
- Handles connector-specific capabilities

JimApplication Facade [Component: C# Class]
- Business logic access
- Shared with Web Application
- Provides domain server access

Domain Servers [Component: C# Services]
- ObjectMatchingServer: Joins ConnectedSystemObjects to MetaverseObjects
- ExportExecutionServer: Executes pending exports with retry logic
- MetaverseServer: Updates metaverse during sync
- ActivityServer: Creates activity records for audit
- TaskingServer: Updates task status

Repository Layer [Component: Entity Framework Core]
- Data access abstraction
- PostgreSQL implementation

EXTERNAL DEPENDENCIES (outside boundary):
- PostgreSQL Database
- Connected Systems (AD, HR, databases, files, SCIM)

RELATIONSHIPS:
- Worker Host polls Repository Layer for tasks [EF Core, task queue]
- Worker Host dispatches to Task Processors
- Task Processors use JimApplication Facade
- Task Processors invoke Connector Runtime
- Connector Runtime connects to Connected Systems [LDAP, SQL, HTTP, File I/O]
- JimApplication Facade delegates to Domain Servers
- Domain Servers use Repository Layer
- Repository Layer reads/writes PostgreSQL Database [EF Core]

Style: C4 component diagram. Show processing flow from task queue through connectors.
```

---

### 3.3 Application Layer Components

**Prompt:**

```
Create a C4 Component diagram for the JIM Application Layer (shared business logic).

Title: JIM Application Layer - Domain Servers

BOUNDARY: JIM.Application (Business Logic Layer)

COMPONENTS:

JimApplication [Component: Facade Class]
- Single entry point to all domain servers
- Constructor injection of IRepository
- Exposes server instances as properties
- Used by Web Application and Worker Service

Identity Management Servers:
- MetaverseServer [Component]: Metaverse object CRUD, querying, attribute management
- ConnectedSystemServer [Component]: Connected system lifecycle, configuration, run profiles
- ObjectMatchingServer [Component]: Join logic between ConnectedSystemObjects and MetaverseObjects

Synchronisation Servers:
- ExportEvaluationServer [Component]: Determines pending exports based on attribute changes and sync rules
- ExportExecutionServer [Component]: Executes pending exports with retry logic and error handling
- TaskingServer [Component]: Worker task queue management, task creation and status updates

Audit and Monitoring Servers:
- ActivityServer [Component]: Activity logging, audit trail, execution statistics
- SearchServer [Component]: Global search across metaverse objects

Configuration Servers:
- ServiceSettingsServer [Component]: System-wide settings (SSO, maintenance mode)
- SecurityServer [Component]: Role management, permission checks
- CertificateServer [Component]: SSL/TLS certificate storage and validation

Utility Servers:
- DataGenerationServer [Component]: Test data template management
- FileSystemServer [Component]: File upload/download operations
- SeedingServer [Component]: Database initialisation and default data

EXTERNAL DEPENDENCIES:
- IRepository: Data access abstraction (injected)
- PostgreSQL Database: Via repository layer

RELATIONSHIPS:
- JimApplication exposes all servers
- All servers receive IRepository via constructor injection
- MetaverseServer is used by ObjectMatchingServer
- ExportEvaluationServer uses sync rules from ConnectedSystemServer
- ExportExecutionServer uses MetaverseServer and ConnectedSystemServer
- ActivityServer is called by most other servers for audit logging
- All servers use IRepository for data access
- IRepository reads/writes PostgreSQL Database

Style: C4 component diagram. Group servers by function. Show key dependencies between servers.
```

---

### 3.4 Connector Architecture Components

**Prompt:**

```
Create a C4 Component diagram for the JIM Connector Architecture.

Title: JIM Connector Architecture - Components

BOUNDARY: JIM.Connectors (External System Integration)

COMPONENTS:

Connector Registry [Component: Service]
- Maintains list of available connector types
- Connector capability discovery
- Dynamic connector instantiation

Connector Interfaces [Component: C# Interfaces]
- IConnector: Base interface (name, capabilities, test connection)
- IConnectorImportUsingCalls: Import via API calls
- IConnectorImportUsingFiles: Import from files
- IConnectorExportUsingCalls: Export via API calls
- IConnectorExportUsingFiles: Export to files
- IConnectorSchema: Schema discovery
- IConnectorSettings: Configuration validation
- IConnectorPartitions: Partition support (e.g., LDAP OUs)
- IConnectorCertificateAware: Certificate handling

LDAP Connector [Component: Connector Implementation]
- Supports Active Directory, OpenLDAP, AD-LDS
- Schema auto-discovery
- LDAPS with certificate validation
- Partition support (naming contexts)
- Delta sync via change tracking
- Create/update/delete operations
- Implements: IConnector, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorSchema, IConnectorPartitions, IConnectorCertificateAware

File Connector [Component: Connector Implementation]
- CSV file import/export
- Configurable delimiters
- Schema discovery from headers
- Multi-valued attribute support
- Data type inference
- Timestamped exports
- Implements: IConnector, IConnectorImportUsingFiles, IConnectorExportUsingFiles, IConnectorSchema

Database Connector [Component: Connector Implementation]
- PostgreSQL, MySQL, Oracle, SQL Server support
- SQL query-based import
- Stored procedure export
- Schema discovery from tables/views
- Implements: IConnector, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorSchema

PowerShell Connector [Component: Connector Implementation]
- Custom PowerShell script execution
- Flexible integration for any system
- Implements: IConnector, IConnectorImportUsingCalls, IConnectorExportUsingCalls

SCIM 2.0 Connector [Component: Connector Implementation]
- SCIM 2.0 protocol support
- Cloud application provisioning
- Implements: IConnector, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorSchema

Mock Connector [Component: Connector Implementation]
- Testing and development
- Simulates external system behaviour

EXTERNAL SYSTEMS (outside boundary):
- Active Directory / LDAP Servers
- CSV File Systems
- Enterprise Databases
- SCIM Applications
- Custom Systems

RELATIONSHIPS:
- Connector Registry manages all connector implementations
- Worker Service invokes connectors via Connector Registry
- LDAP Connector connects to Active Directory / LDAP Servers [LDAP/LDAPS protocol]
- File Connector reads/writes CSV File Systems [File I/O]
- Database Connector connects to Enterprise Databases [SQL protocols]
- SCIM 2.0 Connector calls SCIM Applications [HTTPS/REST]
- PowerShell Connector integrates with Custom Systems [PowerShell scripts]

Style: C4 component diagram. Show interface hierarchy. Group connectors by type.
```

---

### 3.5 Data Model Components

**Prompt:**

```
Create a C4 Component diagram showing JIM's core data model and relationships.

Title: JIM Data Model - Core Entities

BOUNDARY: JIM Data Model (PostgreSQL Database)

ENTITY GROUPS:

Metaverse (Central Authority):
- MetaverseObject [Entity]: Central identity entity (users, groups, custom types)
- MetaverseObjectAttribute [Entity]: Attribute values on metaverse objects
- MetaverseAttribute [Entity]: Attribute schema definitions (name, type, plurality)
- MetaverseObjectType [Entity]: Object type definitions (User, Group, custom)

Staging Area (Connected Systems):
- ConnectedSystem [Entity]: External system configuration (AD, HR, databases)
- ConnectedSystemObject [Entity]: Identity object from external system
- ConnectedSystemObjectAttribute [Entity]: Attribute values from external system
- ConnectedSystemObjectType [Entity]: Object type schema from external system
- ConnectedSystemRunProfile [Entity]: Import/export execution configurations

Synchronisation Configuration:
- SyncRule [Entity]: Mapping rules between metaverse and connected system types
- SyncRuleMapping [Entity]: Attribute flow definitions with transformations
- SyncRuleObjectMatchingRule [Entity]: Rules for joining objects
- SyncRuleScopingCondition [Entity]: Export scoping criteria

Execution and Audit:
- WorkerTask [Entity]: Queued synchronisation tasks
- Activity [Entity]: Audit trail records with statistics
- PendingExport [Entity]: Changes waiting to export to connected systems

Security:
- Role [Entity]: Authorization roles (Administrator)
- ApiKey [Entity]: API authentication keys
- TrustedCertificate [Entity]: SSL certificates for secure connections
- ServiceSettings [Entity]: System configuration (SSO, maintenance mode)

KEY RELATIONSHIPS:
- MetaverseObject belongs to MetaverseObjectType
- MetaverseObject has many MetaverseObjectAttributes
- MetaverseObjectAttribute references MetaverseAttribute
- ConnectedSystemObject belongs to ConnectedSystem
- ConnectedSystemObject optionally joins to MetaverseObject
- ConnectedSystemObject belongs to ConnectedSystemObjectType
- SyncRule links MetaverseObjectType to ConnectedSystemObjectType
- SyncRule belongs to ConnectedSystem
- SyncRuleMapping belongs to SyncRule
- PendingExport references ConnectedSystemObject and ConnectedSystem
- Activity tracks WorkerTask execution
- ConnectedSystemRunProfile belongs to ConnectedSystem

DATA FLOW:
1. ConnectedSystemObject imported from external system
2. ObjectMatching joins to MetaverseObject (or creates new)
3. SyncRuleMapping flows attributes to MetaverseObject
4. MetaverseObject changes trigger PendingExport
5. Export sends changes to target ConnectedSystem
6. Activity records all operations

Style: Entity relationship diagram with C4 styling. Group entities by domain. Show cardinality on relationships.
```

---

### 3.6 PowerShell Module Components

**Prompt:**

```
Create a C4 Component diagram for the JIM PowerShell Module.

Title: JIM PowerShell Module - Components

BOUNDARY: JIM.PowerShell (Automation & Scripting Module)

DESCRIPTION:
Cross-platform PowerShell module distributed separately from the JIM server. Provides cmdlets for automating JIM operations, integrating with CI/CD pipelines, and scripting identity management tasks.

COMPONENTS:

Connection Management [Component: Cmdlets]
- Connect-JIM: Establishes connection to JIM server with API key
- Disconnect-JIM: Closes connection and clears session
- Stores connection context (base URL, API key, headers)

Connected System Cmdlets [Component: Cmdlets]
- Get-JIMConnectedSystem: Retrieves connected system configurations
- New-JIMConnectedSystem: Creates new connected system
- Set-JIMConnectedSystem: Updates connected system settings
- Remove-JIMConnectedSystem: Deletes connected system
- Test-JIMConnectedSystemConnection: Tests connectivity to external system

Sync Rule Cmdlets [Component: Cmdlets]
- Get-JIMSyncRule: Retrieves sync rule configurations
- New-JIMSyncRule: Creates new sync rule
- Set-JIMSyncRule: Updates sync rule
- Remove-JIMSyncRule: Deletes sync rule
- Get-JIMSyncRuleMapping: Retrieves attribute mappings

Run Profile Cmdlets [Component: Cmdlets]
- Get-JIMRunProfile: Retrieves run profile configurations
- New-JIMRunProfile: Creates new run profile
- Invoke-JIMRunProfile: Executes a run profile (triggers sync)
- Get-JIMRunProfileExecution: Gets execution status and results

Metaverse Cmdlets [Component: Cmdlets]
- Get-JIMMetaverseObject: Queries metaverse objects
- Get-JIMMetaverseObjectType: Retrieves object type definitions
- Get-JIMMetaverseAttribute: Retrieves attribute definitions
- Set-JIMMetaverseAttribute: Updates metaverse attributes

Activity Cmdlets [Component: Cmdlets]
- Get-JIMActivity: Retrieves activity history and audit logs

Utility Cmdlets [Component: Cmdlets]
- New-JIMApiKey: Creates new API key for automation
- New-JIMCertificate: Uploads SSL certificate
- New-JIMDataGenerationTemplate: Creates test data template

HTTP Client [Component: Internal Service]
- Makes REST API calls to JIM Web Application
- Handles authentication headers (API key)
- Parses JSON responses into PowerShell objects
- Error handling and retry logic

EXTERNAL DEPENDENCIES (outside boundary):
- JIM Web Application (REST API at /api/v1/)
- Administrator (person using PowerShell)
- CI/CD Systems (automation pipelines)

RELATIONSHIPS:
- Administrator invokes cmdlets [PowerShell terminal or scripts]
- CI/CD Systems invoke cmdlets [Automated pipelines]
- All cmdlets use HTTP Client
- HTTP Client calls JIM Web Application [HTTPS, REST API, API key in header]
- Connection Management stores session for other cmdlets

Style: C4 component diagram. Group cmdlets by functional area. Show HTTP Client as central integration point.
```

---

## Usage Instructions

1. Go to [Eraser.io](https://app.eraser.io)
2. Create a new diagram
3. Use the AI generation feature
4. Paste the relevant prompt from above
5. Adjust the generated diagram as needed
6. Export or embed in documentation

## Diagram Hierarchy

```
Level 1: System Context
    └── Shows JIM's direct interactions with external systems, PowerShell module, and users

Level 2: Container Diagram
    └── Shows JIM's internal deployable units (Web, Worker, Scheduler, Database) and external PowerShell module

Level 3: Component Diagrams
    ├── Web Application Components
    ├── Worker Service Components
    ├── Application Layer Components
    ├── Connector Architecture Components
    ├── Data Model Components
    └── PowerShell Module Components
```

## Notes

- All prompts use British English spelling as per project conventions
- Diagrams follow C4 model conventions (Simon Brown)
- External systems shown outside system boundaries
- Relationships include protocol/technology details where relevant
- Prompts are optimised for Eraser.io's AI capabilities
