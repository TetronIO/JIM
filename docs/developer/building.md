---
title: Building
---

# Building from Source

This page covers building JIM from source, managing database migrations, and working with Docker Compose.

## Prerequisites

- **.NET 10.0 SDK:** [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Docker and Docker Compose:** Required for the database and containerised services

Both are pre-installed in the devcontainer environment. See [Development Environment](dev-environment.md) for setup.

## Building the Solution

### Full Solution Build

```bash
dotnet build JIM.sln
```

Or use the shell alias:

```bash
jim-compile
```

### Targeted Builds

During development, prefer building only the affected projects and their dependants. This is significantly faster than a full solution build.

```bash
# Build a specific test project (transitively builds its dependencies)
dotnet build test/JIM.Worker.Tests/

# Build the web project
dotnet build src/JIM.Web/
```

!!! tip
    Use targeted builds during development and reserve `dotnet build JIM.sln` for the final pre-PR check.

## Docker Builds

### Build All Services

```bash
docker compose build
docker compose up -d
```

### Build Individual Services

Use shell aliases to rebuild and restart specific services after code changes:

```bash
jim-build-web        # Rebuild and restart jim.web
jim-build-worker     # Rebuild and restart jim.worker
jim-build-scheduler  # Rebuild and restart jim.scheduler
jim-build            # Rebuild and restart all services
jim-build-light      # Remove JIM app containers, start db + Keycloak, run JIM.Web natively with hot reload
```

!!! warning "Container rebuilds required"
    When running the Docker stack, compiled code changes (Blazor pages, API controllers, worker processors) require a container rebuild. Simply refreshing the browser will not show changes.

### Building Behind a Proxy or TLS-Inspecting Network

A network that routes outbound traffic through a proxy, or inspects TLS by re-signing it with its own certificate authority, breaks the package restore inside each image's build stage. `docker-compose.yml` passes these optional variables from your `.env` through to the web, worker and scheduler builds; each is empty by default, which changes nothing:

| Variable | Purpose |
|----------|---------|
| `JIM_BUILD_EXTRA_CA_BASE64` | Base64 of a PEM bundle holding the authority your network re-signs TLS with, trusted during the build stage only; nothing from it reaches the final image |
| `JIM_BUILD_HTTP_PROXY`, `JIM_BUILD_HTTPS_PROXY`, `JIM_BUILD_NO_PROXY` | Proxy settings for the build stage |
| `JIM_BUILD_NETWORK` | The Docker network the build runs on (default `default`) |

Encode only the authority certificates you need rather than a whole system bundle: a large bundle's base64 can exceed the environment size limit.

## Database Migrations

JIM uses Entity Framework Core migrations for schema management.

### Adding a Migration

```bash
dotnet ef migrations add MigrationName --project src/JIM.PostgresData
```

### Applying Migrations

Locally:

```bash
jim-migrate
```

In Docker, there is nothing to run: `jim.worker` applies any pending migrations automatically when it starts, and `jim.web` and `jim.scheduler` wait until that has finished. Rebuild and restart the worker to pick up a new migration:

```bash
jim-build-worker
```

!!! note "`dotnet ef` is not available inside the containers"
    The service images are built on the ASP.NET runtime base image, which ships neither the .NET SDK nor the `dotnet-ef` tool, so `docker compose exec jim.web dotnet ef ...` fails. Run EF commands on the host against the project, as in [Adding a Migration](#adding-a-migration) above.

!!! danger "Never squash or delete migrations"
    JIM is deployed in production environments. EF Core tracks applied migrations by name in the `__EFMigrationsHistory` table. Removing existing migrations and replacing them with a combined migration will cause failures on every deployed instance. Migrations are append-only; once committed to `main`, they are permanent.

## Docker Compose File Layering

JIM uses a layered Docker Compose configuration:

| File | Purpose | Tracked |
|------|---------|---------|
| `docker-compose.yml` | Production/deployment defaults | Yes |
| `docker-compose.override.yml` | Development settings (ports, environment, conservative DB) | Yes |
| `docker-compose.local.yml` | Machine-specific DB tuning (auto-generated) | No (gitignored) |

The `jim-*` shell aliases automatically include the local overlay when present. Later files override earlier ones.

## PostgreSQL Tuning

### Development (Automatic)

In devcontainers (Codespaces and local), PostgreSQL is **automatically tuned** during setup. The script `.devcontainer/postgres-tune.sh` detects available CPU and RAM and generates gitignored overlay files with optimal OLTP settings.

To re-tune after changing devcontainer resources:

```bash
jim-postgres-tune
jim-db-stop && jim-db
```

### Production

In production, the bundled PostgreSQL's memory comes from settings the installer sizes to the host's memory; see [Bundled PostgreSQL Memory](../administration/configuration.md#bundled-postgresql-memory). Unset, the defaults in `docker-compose.yml` suit a 4 GB host.
