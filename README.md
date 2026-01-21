# Junctional Identity Manager (JIM)

[![.NET Build & Test](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml/badge.svg?branch=main)](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml)

JIM is a modern Identity Management system designed for organisations with complex identity synchronisation requirements. It is self-hosted, container-deployable, and works in both connected and air-gapped networks. Features include:

- Hub-and-spoke architecture using a central metaverse for identity correlation
- Bidirectional synchronisation of Users, Groups, and custom object types (e.g., Departments, Roles, Computers)
- Transform data using expressions with built-in functions for common identity operations
- Extensible with custom connectors (fully unit-testable)
- Modern Web Portal and REST API with OpenAPI documentation
- Single Sign-On (SSO) using OpenID Connect

![A screenshot of JIM running](https://tetron.io/images/jim/jim-8.png "JIM Screenshot")

## Scenarios
JIM is designed to support the following common Identity, Governance & Administration (IGA) scenarios:

- Automate JML by synchronising users from HR systems to directories, apps and systems
- Keep HR systems up to date by writing I.T related attributes back to HR systems, i.e. email address, telephone numbers, etc.
- Centrally manage user entitlements, i.e. group memberships in directories, apps and systems
- Facilitate domain consolidations, i.e. to prepare for migrating to the cloud, simplification, or for organisational mergers
- Facilitate domain migrations, i.e. divestitures
- Identity fusing - bring together user/entitlement data from various business apps and systems
## Benefits
Why choose JIM?

- It's modern. No legacy hosting requirements or janky old UIs
- Supports SSO to comply with modern security requirements
- Open Source. You can see exactly what it does and help improve it
- Flexible. We're developing it now, so you can suggest your must-have features
- Built by people with decades of experience of integrating IDAM systems into the real world
## Architecture
JIM is a container-based distributed application implementing the metaverse pattern for centralised identity governance.

![JIM System Context](docs/diagrams/images/jim-structurizr-1-SystemContext.svg)

**Components:**
- **JIM.Web** - A website with integrated REST API, built using [ASP.NET](https://asp.net/) Blazor Server. The API is available at `/api/` with Swagger documentation at `/api/swagger`.
- **JIM.Scheduler** - A console app, built using .NET
- **JIM.Worker** - A console app, built using .NET
- **JIM.PowerShell** - A PowerShell module for scripting and automation
- A database - PostgreSQL
- A database admin website - Adminer

![JIM Containers](docs/diagrams/images/jim-structurizr-1-Containers.svg)

For detailed architecture diagrams (Component level), see the [Architecture Diagrams](docs/diagrams/structurizr/README.md).

## Dependencies
- A container host, i.e. Docker
- An OpenID Connect (OIDC) identity provider, i.e. Entra ID, Keycloak, etc.

## Deployment
JIM runs in a Docker stack using containers and can be deployed to on-premises infrastructure or cloud container services. JIM is designed for air-gapped deployments - no internet connection is required.

**Database Options:**
- **Bundled PostgreSQL** - A PostgreSQL container is included for simple deployments. Start with `docker compose --profile with-db up -d`
- **External PostgreSQL** - Connect to your existing PostgreSQL server by configuring `JIM_DB_HOSTNAME` in `.env` and running `docker compose up -d` (without the profile)

Each release includes a downloadable bundle containing pre-built Docker images, compose files, the PowerShell module, and documentation. See [Release Process](docs/RELEASE_PROCESS.md) for details on air-gapped deployment.

## Connectors
JIM is currently targeting the following means of connecting to systems via it's built-in Connectors. More are anticipated, though people will also be able to develop their own custom Connectors for use with JIM to support bespoke scenarios.

- LDAP (incl. Active Directory, AD-LDS & Samba AD)
- Microsoft SQL Server Database
- PostgreSQL Database
- MySQL Database
- Oracle Database
- CSV/Text files
- PowerShell (Core)
- SCIM 2.0
- Web Services (REST APIs with OAuth2/API key authentication)

## Authentication

JIM uses OpenID Connect (OIDC) for Single Sign-On authentication. It is IdP-agnostic and works with any OIDC-compliant Identity Provider, including Microsoft Entra ID, Okta, Auth0, Keycloak, and AD FS. PKCE is used for enhanced security.

For API access, JIM supports both JWT Bearer tokens and API keys for automation and CI/CD scenarios.

## Getting Started

For development setup using GitHub Codespaces or local installation, see the [Developer Guide](docs/DEVELOPER_GUIDE.md).

For SSO configuration with your Identity Provider, see the [SSO Setup Guide](docs/SSO_SETUP_GUIDE.md).

## State of Development
JIM is approximately 92% complete towards MVP status with core identity synchronisation functionality complete. See [MVP Definition](docs/MVP_DEFINITION.md) for detailed progress tracking.

**Connectors (Complete):**
- **LDAP/Active Directory** - Full import and export, schema discovery, LDAPS support with certificate validation, auto-detection of default naming context
- **CSV Files** - Full import and export, configurable delimiters, timestamped outputs

**Import (Complete):**
- Full import from all connectors with object creation, update, and deletion detection
- Multi-valued attribute handling and all data types supported

**Inbound Synchronisation (Complete):**
- Join rules to match Connected System Objects to existing Metaverse Objects
- Projection to create new Metaverse Objects
- Attribute flow rules with multi-valued attribute support
- Metaverse Object lifecycle management with deletion rules and grace periods

**Outbound Synchronisation / Export (Complete):**
- Pending Export detection when Metaverse Objects change
- Export evaluation and execution via connectors
- Create, update, and delete operations in target systems
- Retry logic with exponential backoff
- Pending Export review UI with server-side sorting, filtering, and pagination

**Security (Complete):**
- SSO/OIDC authentication for Web UI
- JWT Bearer token authentication for API
- API Key authentication for automation and CI/CD
- Role-based authorisation (basic model)
- Connector credential encryption at rest (AES-256-GCM)

**API (Complete):**
- Activity monitoring and run profile execution
- Connected Systems, Sync Rules, and Run Profiles CRUD
- Metaverse Object querying with filtering and pagination
- Data generation for testing
- Certificate management

**PowerShell Module (Complete):**
- 35 cmdlets covering all major JIM operations
- Connection management with API key support
- Full CRUD for Connected Systems, Sync Rules, Run Profiles
- Metaverse Object querying and inspection
- Run profile execution and activity monitoring
- Data generation for testing scenarios

**Web UI (Complete):**
- Operations view for manual run profile execution and task monitoring
- Activity history with server-side sorting, filtering, and pagination
- Connected Systems management and connector configuration
- Sync Rule configuration with attribute flow mapping
- Metaverse Object browsing and inspection
- Pending Export list and detail views
- Certificate management for secure connections

**Release & Deployment (Complete):**
- Automated release workflow with GitHub Actions
- Docker images published to GitHub Container Registry
- Air-gapped deployment bundles with SHA256 checksums
- PowerShell module auto-published to PSGallery

**Testing:**
- Unit tests for models, utilities, and API endpoints
- Worker tests for synchronisation logic
- Workflow tests for multi-step sync scenarios (import → sync → export → confirming import)
- Integration tests with real connected systems (Phase 1 complete)

**Integration Testing (Complete):**
- Scenario 1: HR to Identity Directory (Joiner/Mover/Leaver)
- Scenario 2: Cross-domain Synchronisation
- Scenario 4: Deletion Rules
- Scenario 5: Matching Rules
- Scenario 8: Cross-domain Entitlement Synchronisation

See [Integration Testing](docs/INTEGRATION_TESTING.md) for full details on running tests.

**In Progress:**
- Scheduler service for automated run profile execution (#168 - critical path for MVP)
- Full RBAC model with additional roles

**Deferred (Post-MVP):**
- Scenarios 6-7: Entitlement Management (pending Internal MVO design)
- Scenarios 9-11: Database connector testing

> **Note:** As a pre-MVP release, bugs may exist. Please report any issues on [GitHub](https://github.com/TetronIO/JIM/issues).

If you don't have any connected systems available, you can use the Example Data feature to populate JIM with sample users and groups for testing.

## Licensing
JIM uses a Source-Available model where it is free to use in non-production scenarios, but requires a commercial license for use in production scenarios. [﻿Full details can be found here](https://tetron.io/jim/#licensing).

## More Information
Please go to [﻿https://tetron.io/jim](https://tetron.io/jim) for more information.
