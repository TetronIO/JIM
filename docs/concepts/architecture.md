# Architecture

JIM is built around the **metaverse pattern** -- a hub-and-spoke architecture where all identity data flows through a central authoritative repository. This page describes the pattern, the system components, and the layered software architecture.

## The Metaverse Pattern

At the heart of JIM is the **metaverse**: a centralised repository of identity objects. Rather than synchronising data directly between Connected Systems, every change flows through the metaverse. This provides:

- **Single source of truth**<br /> One authoritative record for each identity, assembled from multiple sources.
- **Centralised governance**<br /> All data transformations, scoping decisions, and lifecycle rules are applied in one place.
- **Decoupled systems**<br /> Connected Systems do not need to know about each other; they only interact with the metaverse.
- **Auditability**<br /> Every change is tracked as it passes through the hub.

--8<-- "assets/diagrams/metaverse-pattern.svg"

<p class="jim-diagram-caption">Sources project identities into the Metaverse; targets receive them from it. The same Connected System can be both source and target (writeback).<span class="jimdg-caption-motion"> Moving dots trace import and export flows.</span></p>

**Key principle**: All identity data flows through the metaverse. JIM never synchronises directly between Connected Systems.

## System Context

The following diagram shows JIM in the context of the systems and users it interacts with:

--8<-- "assets/diagrams/system-context.svg"

<p class="jim-diagram-caption">Administrators and automation clients work through JIM's UI and API; JIM synchronises with the surrounding systems. Dashed elements indicate planned connectivity.<span class="jimdg-caption-motion"> Moving dots trace identity data in flight.</span></p>

## Containers

JIM is deployed as a set of Docker containers, each with a distinct responsibility:

--8<-- "assets/diagrams/containers.svg"

<p class="jim-diagram-caption">JIM's deployable containers. PostgreSQL doubles as the task queue: the Scheduler queues work and the Worker polls it, so the services coordinate through the database rather than calling each other.<span class="jimdg-caption-motion"> Moving dots trace identity data in flight.</span></p>

### JIM.Web

The web application provides both the **administrative user interface** (built with Blazor Server and MudBlazor) and the **REST API** (at `/api/`). Administrators use the UI to configure Connected Systems, define Synchronisation Rules, monitor operations, and browse identity data. The API enables automation and integration with external tools.

### JIM.Worker

The background processor that executes all identity operations -- imports, synchronisation, and exports. When an operation is triggered (manually, via the API, or by the scheduler), the Worker picks it up and processes it asynchronously. It handles batch processing, error reporting, and activity logging.

--8<-- "assets/diagrams/worker-components.svg"

<p class="jim-diagram-caption">Inside the Worker Service: the host dispatches import, synchronise and export processors, and the Sync Engine makes the synchronisation decisions. The host polls the task queue in PostgreSQL, where the whole service reads and writes staged and Metaverse data; the Connectors carry data to and from Connected Systems.<span class="jimdg-caption-motion"> Moving dots trace data arriving through, and leaving via, the Connectors.</span></p>

### JIM.Scheduler

A lightweight scheduling service that triggers operations on a cron or interval basis. Schedules can include multiple steps (e.g., import from HR, synchronise, export to Active Directory) that execute in sequence. The Scheduler submits work to the Worker for processing.

### JIM.PowerShell

A PowerShell module for automation and scripting. It wraps the REST API and provides cmdlets for querying, configuring, and executing operations. Supports both interactive (browser-based SSO) and non-interactive (API key) authentication.

### PostgreSQL Database

JIM uses PostgreSQL as its sole data store. The database holds all configuration, identity data (Metaverse Objects, Connected System Objects), Synchronisation Rules, activity history, and credentials (encrypted at rest with AES-256-GCM).

## Layered Architecture

JIM follows a strict layered architecture. Each layer depends only on the layer directly below it -- higher layers never bypass intermediate layers.

--8<-- "assets/diagrams/layered-architecture.svg"

<p class="jim-diagram-caption">Arrows show dependencies: each layer depends only on the layer directly beneath it. The Integration layer's Connectors sit at the base of the stack alongside data access.</p>

### Presentation Layer

**JIM.Web** -- the Blazor Server UI and REST API controllers. This layer handles user interaction, request validation, and response formatting. It calls into the Application layer for all business logic.

### Application Layer

**JIM.Application** -- contains the core business logic ("Servers") that orchestrate operations. This includes the sync engine, import/export processing, Run Profile execution, and all domain workflows. The UI and API never access the database directly; they always go through the Application layer.

For a component-level breakdown of the Application Layer's domain servers and repositories, see the [Developer Guide's architecture page](../developer/architecture.md).

### Domain Layer

**JIM.Models** -- defines the domain entities (MetaverseObject, ConnectedSystemObject, SyncRule, etc.), DTOs, and enumerations. This layer has no dependencies on infrastructure or data access.

### Data Layer

**JIM.PostgresData** -- implements the repository interfaces using Entity Framework Core with PostgreSQL. Handles all database interactions, migrations, and query optimisation.

### Integration Layer

**JIM.Connectors** -- contains the connector implementations that communicate with external systems (LDAP directories, file systems, etc.). Each connector implements standardised interfaces for import and export operations.

## Deployment

JIM is deployed as Docker containers and is designed to work in **air-gapped environments** with no internet connectivity. There are no cloud service dependencies -- all features work with on-premises infrastructure only.

A typical deployment consists of:

- **JIM.Web** container (UI and API)
- **JIM.Worker** container (background processing)
- **JIM.Scheduler** container (scheduled operations)
- **PostgreSQL** container (database)
- An **OIDC identity provider** for authentication (e.g., Keycloak, or any OIDC-compliant provider)
