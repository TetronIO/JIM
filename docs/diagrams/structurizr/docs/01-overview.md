# JIM Identity Management System

JIM is a central identity hub implementing the **metaverse pattern** for enterprise identity synchronisation.

## Purpose

JIM synchronises identities across enterprise systems with bidirectional data flow and transformation. It serves as the authoritative central repository for identity data, reconciling information from multiple sources and provisioning to downstream systems.

## Key Concepts

### Metaverse Pattern
The metaverse is the central identity store where all identity data converges. Each identity exists as a **MetaverseObject** with attributes aggregated from connected systems according to precedence rules defined in sync rules.

### Connected Systems
External systems (Active Directory, HR databases, CSV files, etc.) connect to JIM through **connectors**. Each connected system has:
- **Connected System Objects**: Staged copies of external identity data
- **Run Profiles**: Define what operations to perform (import, sync, export)
- **Sync Rules**: Define attribute mappings and transformation logic

### Synchronisation Flow
1. **Import**: Pull data from connected systems into staging area (full or delta)
2. **Sync**: Apply sync rules to project data into the metaverse (full or delta)
3. **Export**: Push pending changes to target connected systems

### Automation
JIM provides a **PowerShell module** with 35+ cmdlets and a **REST API** for automation and scripting. CI/CD pipelines and administrators can manage connected systems, trigger synchronisation, and query the metaverse programmatically.

## Architecture

JIM follows a layered architecture:
- **Web Application**: Blazor Server UI and REST API with OIDC/SSO authentication
- **Application Layer**: Business logic and domain services (JimApplication facade)
- **Worker Service**: Background task processing for import, sync, and export operations
- **Connectors**: External system adapters (LDAP, CSV file)
- **Scheduler**: Evaluates schedule due times, triggers synchronisation jobs, and recovers from stale or stuck executions

All components share a PostgreSQL database for configuration, state, and the task queue.
