# Development Environment Reference

> Commands, workflows, environment setup, and troubleshooting. See root `CLAUDE.md` for behavioural rules and guardrails.

## Commands

**Build & Test:**
- `dotnet build JIM.sln` - Build entire solution (use for final pre-PR check)
- `dotnet build test/JIM.Worker.Tests/` - Build a specific project and its dependencies (preferred during development)
- `dotnet test JIM.sln` - Run all tests (use for final pre-PR check)
- `dotnet test test/JIM.Worker.Tests/` - Run a specific test project (preferred during development)
- `dotnet test --filter "FullyQualifiedName~TestName"` - Run specific test
- `dotnet clean && dotnet build` - Clean build

**Database:**
- `dotnet ef migrations add [Name] --project src/JIM.PostgresData` - Add migration
- `dotnet ef database update --project src/JIM.PostgresData` - Apply migrations
- `docker compose exec jim.web dotnet ef database update` - Apply migrations in Docker

**Shell Aliases (Recommended):**
- Aliases are automatically configured from `.devcontainer/jim-aliases.sh`
- If aliases don't work, run: `source ~/.zshrc` (or restart terminal)
- `jim` - List all available jim aliases
- `jim-compile` - Build entire solution (dotnet build)
- `jim-test` - Run unit + workflow tests (excludes Explicit)
- `jim-test-all` - Run ALL tests (incl. Explicit + Pester)
- `jim-db` - Start PostgreSQL (for local debugging)
- `jim-db-stop` - Stop PostgreSQL
- `jim-migrate` - Apply migrations
- `jim-postgres-tune` - Re-tune PostgreSQL for current CPU/RAM

**Docker Stack Management:**
- `jim-stack` - Start Docker stack
- `jim-stack-logs` - View Docker stack logs
- `jim-stack-down` - Stop Docker stack
- `jim-restart` - Restart stack (re-reads .env, no rebuild)

**Docker Builds (rebuild and start services):**
- `jim-build` - Build all services + start
- `jim-build-web` - Build jim.web + start
- `jim-build-worker` - Build jim.worker + start
- `jim-build-scheduler` - Build jim.scheduler + start

**Reset:**
- `jim-reset` - Reset JIM (delete database & logs volumes)

**Diagrams:**
- `jim-diagrams` - Export Structurizr C4 diagrams as SVG (requires Docker)

**Planning:**
- `jim-prd` - Create a new PRD from template (prompts for feature name)

**Docker (Manual Commands):**
- `docker compose -f db.yml up -d` - Start database (same as jim-db)
- `docker compose -f db.yml down` - Stop database
- `docker compose logs [service]` - View service logs

**IMPORTANT - Rebuilding Containers After Code Changes:**
When running the Docker stack and you make code changes to JIM.Web, JIM.Worker, or JIM.Scheduler, you MUST rebuild the affected container(s) for changes to take effect:
- `jim-build-web` - Rebuild and restart jim.web service
- `jim-build-worker` - Rebuild and restart jim.worker service
- `jim-build-scheduler` - Rebuild and restart jim.scheduler service
- `jim-build` - Rebuild and restart all services

Blazor pages, API controllers, and other compiled code require container rebuilds. Simply refreshing the browser will not show changes.

**PostgreSQL Auto-Tuning:**
PostgreSQL is automatically tuned during devcontainer setup (`.devcontainer/postgres-tune.sh`). The script:
- Auto-detects CPU cores and available RAM
- Calculates optimal pgtune settings for OLTP workloads
- Generates two gitignored overlay files: `docker-compose.local.yml` and `db.local.yml`
- The `jim-*` aliases automatically include these overlays when present (later files win)

If you later increase devcontainer resources (e.g., scale from 4c/8GB to 8c/32GB), re-tune and restart:
- `jim-postgres-tune` then `jim-db-stop && jim-db` (local dev)
- `jim-postgres-tune` then `jim-restart` (Docker stack)

## Dependency Update Policy

All dependency updates from Dependabot require human review before merging - there is no auto-merge. This applies to all ecosystems: NuGet packages, Docker base images, and GitHub Actions. A maintainer must review each PR, verify the changes are appropriate, and merge manually.

**NuGet Packages:**
- Pin dependency versions in `.csproj` files (avoid floating versions)
- Dependabot proposes weekly PRs for patch and minor updates
- Review each update for compatibility, changelog notes, and known issues before merging
- Run `dotnet list package --vulnerable` to check for known vulnerabilities

**Docker Base Images:**
- Production Dockerfiles pin base image digests (`@sha256:...`) and functional apt package versions for reproducible builds
- **NEVER** remove the `@sha256:` digest from `FROM` lines
- **NEVER** remove version pins from functional apt packages (libldap, cifs-utils)
- Diagnostic utilities (curl, iputils-ping) are intentionally unpinned
- If updating a base image digest, check and update pinned apt versions to match (see `docs/DEVELOPER_GUIDE.md` "Dependency Pinning" section)

**GitHub Actions:**
- Pin action versions by major version tag (e.g., `@v4`) in workflow files
- Dependabot proposes weekly PRs for patch and minor updates
- Review each update before merging

## Development Workflows

**Choose one of two workflows:**

**Workflow 1 - Local Debugging (Recommended):**
1. Start database: `jim-db`
2. Press F5 in VS Code or run: `jim-web`
3. Debug with breakpoints and hot reload
4. Services: Web + API (https://localhost:7000), Swagger at `/api/swagger`

**Workflow 2 - Full Docker Stack:**
1. Start all services: `jim-stack`
2. Access containerized services
3. Services: Web + API (http://localhost:5200), Swagger at `/api/swagger`

**Use Workflow 1** for active development and debugging.
**Use Workflow 2** for integration testing or production-like environment.

## Environment Setup

**Required:**
- .NET 9.0 SDK
- PostgreSQL 18 (via Docker)
- Docker & Docker Compose

**Configuration:**
- Copy `.env.example` to `.env`
- Set database credentials
- Configure SSO/OIDC settings (required)

**Optional Environment Variables:**
- `JIM_ENCRYPTION_KEY_PATH` - Custom path for encryption key storage (default: `/data/keys` for Docker, or app data directory)

**GitHub Codespaces:**
- PostgreSQL memory settings automatically optimized
- Use `jim-db` or `jim-stack` aliases (already configured)

## Troubleshooting

**Build fails:**
- Check .NET 9.0 SDK installed: `dotnet --version`
- Restore packages: `dotnet restore JIM.sln`

**Tests fail:**
- Verify test method signature: `public async Task TestNameAsync()`
- Check `Assert.ThrowsAsync` is awaited: `await Assert.ThrowsAsync<Exception>(...)`

**Database connection:**
- Verify PostgreSQL running: `docker compose ps`
- Check `.env` connection string
- Apply migrations if needed

**Sync Activities Failing with UnhandledError:**
- UnhandledError items in Activities indicate code/logic bugs, not data problems
- Check worker logs for the full exception stack trace using: `docker compose logs jim.worker --tail=1000 | grep -A 5 "Unhandled.*sync error"`
- Common causes:
  - Query returning unexpected number of results (e.g., `SingleOrDefaultAsync` with duplicates)
  - Missing or null data where code expected values
  - Type casting errors or invalid data states
- DO NOT silently ignore UnhandledErrors - they indicate data integrity risk

**Sync Statistics Not What Expected:**
- Check log for summary statistics at end of import/sync/export (look for "SUMMARY - Total objects")
- Verify Run Profile is selecting correct partition/container
- Verify sync rules are correctly scoped to object types
- Check for DuplicateObject errors - indicates deduplication is working
