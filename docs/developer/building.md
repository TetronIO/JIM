---
title: Building
---

# Building from Source

This page covers building JIM from source, managing database migrations, and working with Docker Compose.

## Prerequisites

- **.NET 9.0 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Docker and Docker Compose** — Required for the database and containerised services

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
```

!!! warning "Container rebuilds required"
    When running the Docker stack, compiled code changes (Blazor pages, API controllers, worker processors) require a container rebuild. Simply refreshing the browser will not show changes.

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

In Docker:

```bash
docker compose exec jim.web dotnet ef database update
```

!!! danger "Never squash or delete migrations"
    JIM is deployed in production environments. EF Core tracks applied migrations by name in the `__EFMigrationsHistory` table. Removing existing migrations and replacing them with a combined migration will cause failures on every deployed instance. Migrations are append-only — once committed to `main`, they are permanent.

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

### Production (Manual)

The default PostgreSQL settings in `docker-compose.yml` are tuned for a 64GB / 16-core system. For other environments, use [PGTune](https://pgtune.leopard.in.ua/) to generate settings, then override `command` and `shm_size` in a compose override file.

Key settings to adjust:

- `shared_buffers` — typically ~25% of available host RAM
- `effective_cache_size` — typically ~75% of available host RAM
- `shm_size` (Docker) — must be >= `shared_buffers` with ~25% headroom

**Sizing reference:**

| Host RAM | `shared_buffers` | `shm_size` |
|----------|------------------|------------|
| 8 GB | 2 GB | 3 GB |
| 16 GB | 4 GB | 5 GB |
| 32 GB | 8 GB | 10 GB |
| 64 GB | 16 GB | 20 GB |
| 128 GB | 32 GB | 40 GB |

!!! warning
    If `shm_size` is smaller than `shared_buffers`, PostgreSQL will crash under load. Docker defaults `shm_size` to only 64 MB, which is insufficient for any non-trivial `shared_buffers` value.
