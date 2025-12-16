# JIM Identity Management System

JIM is a central identity hub implementing the **metaverse pattern** for enterprise identity synchronisation.

## Purpose

JIM synchronises identities across enterprise systems with bidirectional data flow and transformation. It serves as the authoritative central repository for identity data, reconciling information from multiple sources and provisioning to downstream systems.

## Key Concepts

### Metaverse Pattern
The metaverse is the central identity store where all identity data converges. Each identity exists as a **MetaverseObject** with attributes aggregated from connected systems according to precedence rules defined in sync rules.

### Connected Systems
External systems (Active Directory, HR databases, SCIM applications, etc.) connect to JIM through **connectors**. Each connected system has:
- **Connected System Objects**: Staged copies of external identity data
- **Run Profiles**: Define what operations to perform (import, sync, export)
- **Sync Rules**: Define attribute mappings and transformation logic

### Synchronisation Flow
1. **Import**: Pull data from connected systems into staging area
2. **Sync**: Apply sync rules to project data into the metaverse
3. **Export**: Push pending changes to target connected systems

## Architecture

JIM follows a layered architecture:
- **Web Application**: Blazor Server UI and REST API
- **Application Layer**: Business logic and domain services
- **Worker Service**: Background task processing
- **Connectors**: External system adapters
- **Scheduler**: Automated job triggering

All components share a PostgreSQL database for configuration, state, and the task queue.
