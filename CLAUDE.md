# JIM Development Quick Reference

> Identity Management System - .NET 9.0, EF Core, PostgreSQL, Blazor

## CRITICAL REQUIREMENTS

**YOU MUST BUILD AND TEST BEFORE EVERY COMMIT AND PR (for .NET code):**

1. **ALWAYS** build and test before committing - zero errors required
2. **Prefer targeted builds**: Build only affected projects and their dependents, not the full solution. For example, if you changed `JIM.Connectors` and `JIM.Worker.Tests`, run `dotnet build test/JIM.Worker.Tests/` (which transitively builds its dependencies). Only use `dotnet build JIM.sln` for the **final pre-PR check** or when changes span many projects.
3. **Prefer targeted tests**: Run only the test projects that cover your changes. For example, `dotnet test test/JIM.Worker.Tests/` instead of `dotnet test JIM.sln`. Only run `dotnet test JIM.sln` for the **final pre-PR check**.
4. **NEVER** commit code that hasn't been built and tested locally
5. **NEVER** create a PR without verifying **full solution** build and tests pass
6. **NEVER** assume tests will pass without running them

**EXCEPTIONS - changes that do NOT require dotnet build/test:**
- **Scripts** (.ps1, .sh, etc.) - do not affect compiled code
- **Static assets** (CSS, JS, images) - served directly without compilation
- **Documentation** (`.md` files, `docs/` changes) - no compiled code
- **Configuration files** (`.env.example`, `docker-compose.yml`, `Dockerfile`, `.gitignore`, `.editorconfig`, etc.) - not compiled
- **CI/CD workflows** (`.github/workflows/`) - run remotely, not compiled locally
- **Diagrams** (`workspace.dsl`, exported SVGs) - non-code assets
- **Plan documents** (`docs/plans/`) - documentation only

**Partial exception:**
- **UI-only changes** (Blazor pages, Razor components) require `dotnet build` but do NOT require `dotnet test` - there are no UI tests, so running tests just wastes time

**YOU MUST WRITE UNIT TESTS FOR NEW FUNCTIONALITY:**

1. **ALWAYS** write unit tests for new classes, methods, and logic
2. **ALWAYS** write tests for new API endpoints, extension methods, and utilities
3. **ALWAYS** write tests for bug fixes (to prevent regression)
4. **NEVER** commit new functionality without corresponding tests
5. Tests should cover: happy path, edge cases, error conditions

**YOU MUST ASK BEFORE IMPLEMENTING SIGNIFICANT CHANGES:**

1. **ALWAYS** ask the user before implementing new features or significant functionality
2. **ALWAYS** confirm the approach when multiple implementation options exist
3. **ALWAYS** clarify requirements when there is ambiguity about what the user wants
4. **NEVER** assume you know what the user wants for non-trivial changes
5. **NEVER** implement architectural decisions without user confirmation
6. Present options with trade-offs when relevant, and let the user decide

**COMMIT WITHOUT ASKING:**

1. When the user says "yes" to committing, just commit - don't ask for confirmation
2. When work is complete and tests pass, commit automatically if the user has indicated approval
3. Don't ask "Would you like me to commit?" - if the context is clear, just do it

**Test project locations:**
- API tests: `test/JIM.Web.Api.Tests/`
- Model tests: `test/JIM.Models.Tests/`
- Worker/business logic tests: `test/JIM.Worker.Tests/`

**Failure to build and test wastes CI/CD resources and delays the project.**

If you cannot build/test locally due to environment constraints, you MUST:
- Clearly state this limitation in the PR description
- Mark the PR as draft
- Request manual review and testing before merge

## Synchronisation Integrity

**SYNCHRONISATION INTEGRITY IS CRITICAL - PRIORITISE IT ABOVE ALL ELSE.**

Sync operations are the core of JIM. Customers depend on JIM to synchronise their identity data accurately without corruption or data loss.

**Error Handling Philosophy:**
1. **Fast/Hard Failures**: Better to stop and report an error than continue with corrupted state
2. **Comprehensive Reporting**: ALL errors must be reported via RPEIs/Activities - no silent failures
3. **Defensive Programming**: Anticipate edge cases (duplicates, missing data, type mismatches) and handle explicitly
4. **Trust and Confidence**: Customers must be able to trust that JIM won't silently corrupt their data

**Key Rules:**
- NEVER use `First()`/`FirstOrDefault()` when expecting exactly one result - handle multiplicity explicitly
- All sync operation code MUST be wrapped in try-catch with RPEI error logging
- Activities with any UnhandledError RPEI items should fail the entire activity
- Log summary statistics at the end of every batch operation
- Never silently skip objects due to exceptions

> **Full detailed requirements (7 sections):** See `src/JIM.Application/CLAUDE.md`

## Scripting

**IMPORTANT: Use PowerShell for ALL scripts:**
- All automation, integration tests, and utility scripts MUST be written in PowerShell (.ps1)
- PowerShell is cross-platform and works on Linux, macOS, and Windows
- Exception: `.devcontainer/setup.sh` is acceptable as it runs during container creation
- Never create bash/shell scripts for project automation or testing

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

**IMPORTANT - Dependency Update Policy:**
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

## Key Project Locations

**Where to add:**
- API endpoints: `src/JIM.Web/Controllers/Api/`
- API models/DTOs: `src/JIM.Web/Models/Api/`
- API extensions: `src/JIM.Web/Extensions/Api/`
- API middleware: `src/JIM.Web/Middleware/Api/`
- UI pages: `src/JIM.Web/Pages/`
- Blazor components: `src/JIM.Web/Shared/`
- Business logic: `src/JIM.Application/Servers/`
- Performance diagnostics: `src/JIM.Application/Diagnostics/`
- Domain models: `src/JIM.Models/Core/` or `src/JIM.Models/Staging/`
- Database repositories: `src/JIM.PostgresData/`
- Connectors: `src/JIM.Connectors/` or new connector project
- Tests: `test/JIM.Web.Api.Tests/`, `test/JIM.Models.Tests/`, `test/JIM.Worker.Tests/`

## ASCII Diagrams

When creating ASCII diagrams in documentation or code comments, use only reliably monospaced characters:

| Use         | Instead of    | Purpose                        |
|-------------|---------------|--------------------------------|
| `->` `-->`  | `->` `-->` `>` | Arrows (horizontal)           |
| `<-` `<--`  | special chars | Arrows (reverse)               |
| `v` / `^`   | `v` `^`       | Arrows (vertical)             |
| `+`         | box-drawing chars | Corners and junctions      |
| `-` / `|`   | `=` special chars | Lines (horizontal/vertical)|
| `-`         | bullets       | Bullet points in diagrams      |

## Code Style & Conventions

**IMPORTANT Rules:**
- YOU MUST use async/await for all I/O operations (method suffix: `Async`)
- YOU MUST use constructor injection for all dependencies
- YOU MUST test method signature: `[Test] public async Task TestNameAsync()`
- **CRITICAL: Use British English (en-GB) for ALL text:**
  - Code: "authorisation" not "authorization", "synchronisation" not "synchronization", "colour" not "color"
  - Comments: "behaviour" not "behavior", "centre" not "center", "licence" not "license" (noun)
  - Documentation: "organise" not "organize", "analyse" not "analyze", "programme" not "program" (unless referring to computer programs)
  - UI text: "minimise" not "minimize", "optimise" not "optimize", "cancelled" not "canceled"
  - Units: Metric only (metres, litres, kilograms, kilometres) - never use imperial units
  - Date/Time: Always use UTC for storage and internal operations; display in user's local time zone where appropriate
  - Exceptions: Technical terms, proper nouns, third-party library names, URLs

**DateTime Handling (IMPORTANT):**
- Always use `DateTime` type (not `DateTimeOffset`) in models
- Always use `DateTime.UtcNow` for current time - NEVER use `DateTime.Now`
- PostgreSQL stores DateTime as `timestamp with time zone` (internally UTC)
- **Runtime quirk**: Npgsql returns `DateTimeOffset` when reading from database, even though model properties are `DateTime`
- Code that processes DateTime values from the database must handle BOTH `DateTime` and `DateTimeOffset` types
- See `DynamicExpressoEvaluator.ToFileTime()` for an example of handling both types
- This design choice maintains database portability (MySQL, SQL Server, etc. handle DateTimeOffset differently)

**File Organisation:**
- One class per file - each class should have its own `.cs` file named after the class
- Exception: Enums are grouped into a single file per area/folder (e.g., `ConnectedSystemEnums.cs`, `PendingExportEnums.cs`)
- File names must match the class/interface name exactly (e.g., `MetaverseObject.cs` for `class MetaverseObject`)

**Naming Patterns:**
- Methods: `GetObjectAsync`, `CreateMetaverseObjectAsync`
- Classes: Full descriptive names (avoid abbreviations)
- Properties: PascalCase with nullable reference types enabled

**UI Element Sizing:**
- ALWAYS use normal/default sizes for ALL UI elements when adding new components
- Text: Use `Typo.body1` (default readable size)
- Chips: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Buttons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Icons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Other MudBlazor components: Omit Size parameter to use default sizing
- Only use smaller sizes (`Typo.body2`, `Size.Small`, etc.) when explicitly requested by the user
- Users prefer readable, appropriately-sized UI elements by default

## Testing

**Before Committing .NET Code (MANDATORY):**
- Build and test affected projects - must complete with zero errors
- During development, prefer targeted builds: `dotnet build test/JIM.Worker.Tests/` (builds dependencies transitively)
- For final pre-PR check: `dotnet build JIM.sln` and `dotnet test JIM.sln` (full solution)
- **DO NOT proceed to commit if any tests fail or build has errors**
- See **Exceptions** under Critical Requirements for changes that do not require build/test

**Test Basics:**
- NUnit with `[Test]` attribute, `Assert.That()` syntax, Moq for mocking
- Test naming: `MethodName_Scenario_ExpectedResult`
- EF Core in-memory database auto-tracks navigation properties - this MASKS missing `.Include()` bugs. Run integration tests when modifying repository queries.

> **Full testing patterns, debugging tips, test data generation, and integration testing:** See `test/CLAUDE.md`

## Design Principles

**Minimise Environment Variables:**
- Prefer configuration through admin UI and guided setup wizards over environment variables
- Environment variables should be a fallback for container/automated deployments, not the primary configuration method
- Settings that administrators might need to change should be configurable through the web interface
- Only use environment variables for:
  - Bootstrap configuration (database connection for initial setup)
  - Secrets that cannot be stored in the database (encryption keys before encryption is configured)
  - Container orchestration overrides

**Self-Contained & Air-Gapped Deployable:**
- JIM must work in air-gapped environments with no internet connectivity
- No cloud service dependencies (no Azure Key Vault, AWS KMS, etc.)
- All features must work with on-premises infrastructure only

**No Third-Party Product References:**
- NEVER mention competing or third-party identity management products in code, comments, or documentation
- This includes products like MIM, FIM, Entra ID, Okta, SailPoint, etc.
- JIM documentation should stand on its own without comparisons to other products
- Use generic/abstract terms to preserve context without naming products:
  - "other identity management systems" or "traditional ILM solutions"
  - "SQL Server-based ILM systems" instead of naming specific products
  - "enterprise identity platforms" for general comparisons
- Exception: Generic industry terms and standards (SCIM, LDAP, OIDC, etc.) are acceptable

## Security

JIM is deployed in government, defence, healthcare, financial services, and critical national infrastructure environments. All code MUST meet the security expectations of these sectors.

**Key security rules (always apply):**
- Use `[Authorize]` on all API controllers - deny by default
- NEVER hardcode secrets, credentials, or connection strings in source code
- NEVER log secrets, tokens, or personal data
- Use parameterised queries (EF Core default) - never bypass with unparameterised raw SQL
- Validate ALL input at system boundaries (API controllers, Blazor form submissions)
- Use AES-256-GCM for encryption at rest, minimum TLS 1.2 for transit
- Use `System.Security.Cryptography.RandomNumberGenerator` for security-sensitive random values (never `System.Random`)

> **Full security development guidelines, OWASP Top 10 details, Secure by Design principles, and compliance mapping:** See `docs/COMPLIANCE_MAPPING.md` and `docs/DEVELOPER_GUIDE.md`

## Third-Party Dependency Governance

Before adding ANY new NuGet package or third-party dependency:
1. **Notify the user** - state the need and conduct a suitability analysis
2. **Research**: licence compatibility, maintainer reputation, maintenance status, known vulnerabilities
3. **Present findings** with comparison table if alternatives exist
4. **Await user approval** before adding the dependency

Prefer: Microsoft-maintained packages > established corporate-backed packages > .NET Foundation projects > well-maintained OSS with identifiable maintainers.

> **Full supply chain security requirements:** See `docs/COMPLIANCE_MAPPING.md` and `docs/DEVELOPER_GUIDE.md`

## Feature Planning

**When creating plans for new features or significant changes:**
1. Create a plan document in `docs/plans/` (uppercase filename with underscores)
2. Create a GitHub issue linking to the plan document

> **Full plan structure guidelines and documentation organisation:** See `docs/CLAUDE.md`

## Architecture Quick Reference

**Metaverse Pattern:**
- MetaverseObject = Central identity entity
- ConnectedSystemObject = External system identity
- SyncRule = Bidirectional mapping between systems
- All operations flow through the metaverse (never direct system-to-system)

**Layer Dependencies (top to bottom):**
1. JIM.Web (Presentation - includes both Blazor UI and REST API at `/api/`)
2. JIM.Application (Business Logic)
3. JIM.Models (Domain)
4. JIM.Data, JIM.PostgresData (Data Access)

**CRITICAL: Respect N-Tier Architecture - NEVER Bypass Layers:**

JIM follows strict n-tier architecture. Each layer may ONLY call the layer directly below it:

```
+------------------+
|     JIM.Web      |  Blazor pages, API controllers
+--------+---------+
         | ONLY calls JimApplication (never Repository directly)
         v
+------------------+
| JIM.Application  |  Business logic, orchestration (Servers/)
+--------+---------+
         | ONLY calls Repository interfaces
         v
+------------------+
|    JIM.Data      |  Repository interfaces
+------------------+
         |
         v
+------------------+
| JIM.PostgresData |  EF Core implementations
+------------------+
```

**Rules:**
- **JIM.Web** (UI/API) must ONLY access data through `JimApplication` facade (e.g., `Jim.Metaverse`, `Jim.Scheduler`, `Jim.ConnectedSystems`)
- **NEVER** call `Jim.Repository.*` directly from Blazor pages or API controllers
- If a method doesn't exist on the Application layer, ADD IT there - don't bypass to the repository
- This separation ensures business logic stays in one place and can be tested independently

**Bad - Bypassing layers:**
```csharp
// In a Blazor page - WRONG!
var schedule = await Jim.Repository.Scheduling.GetScheduleAsync(id);
```

**Good - Respecting layers:**
```csharp
// In a Blazor page - CORRECT!
var schedule = await Jim.Scheduler.GetScheduleAsync(id);
```

## Common Development Tasks

**Adding a Connector:**
1. Implement `IConnector` and capability interfaces
2. Add to `src/JIM.Connectors/` or create new project
3. Register in DI container
4. Add tests

**Adding API Endpoint:**
1. Add method to controller in `src/JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `src/JIM.Web/Models/Api/`)
3. Add XML comments for Swagger
4. Test via Swagger UI at `/api/swagger`

**Modifying Database Schema:**
1. Update entity in `src/JIM.Models/`
2. Create migration: `dotnet ef migrations add [Name] --project src/JIM.PostgresData`
3. Review generated migration
4. Test: `dotnet ef database update --project src/JIM.PostgresData`
5. Commit migration files

**Updating Architecture Diagrams:**

When making architectural changes (new containers, components, connectors, or significant restructuring):
1. Update `docs/diagrams/structurizr/workspace.dsl` to reflect the change
2. Regenerate SVGs: `jim-diagrams` (requires Docker)
3. Commit both the DSL changes and regenerated SVG files together

> **DSL syntax and diagram details:** See `docs/diagrams/structurizr/README.md`

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

## Integration Testing

Use `./test/integration/Run-IntegrationTests.ps1` (PowerShell) - never invoke scenario scripts directly. The runner handles setup, environment management, and teardown automatically.

> **Full commands, templates, and runner details:** See `test/CLAUDE.md`

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

## Workflow Best Practices

**Git:**
- **ALWAYS** work on a feature branch - NEVER commit directly to `main`
- **NEVER** automatically create a PR or merge to `main` - the user must explicitly instruct you to do so
- Branch naming: `feature/description` or `claude/description-sessionId`
- Commit messages: Descriptive, include issue reference if applicable
- Build and test before every commit of .NET code (see **Exceptions** under Critical Requirements)
- Push to feature branches, create PRs to main

**Development Cycle (FOLLOW THIS EXACTLY):**
1. Create/checkout feature branch (NEVER work on `main`)
2. Make changes
3. **For .NET code changes**: Build and test affected projects during development; run full solution build/test (`dotnet build JIM.sln` and `dotnet test JIM.sln`) as a final pre-PR check
4. For non-.NET changes (scripts, docs, config, CI/CD, diagrams): skip build/test (see Exceptions above)
5. If build or tests fail, fix errors and repeat step 3
6. **ONLY AFTER** build and tests pass (or are not required): Commit with clear message
7. **ONLY AFTER** successful commit and **ONLY when the user explicitly asks**: Push and create PR
8. **NEVER** create a PR with failing tests or build errors
9. **NEVER** merge PRs without explicit user instruction

## Changelog Maintenance

The project uses [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format. Entries must be kept up to date as changes are made, not deferred until release time.

**When to add an entry:**
- Add an entry under `## [Unreleased]` with each commit or PR that introduces user-facing changes
- This includes: new features, bug fixes, performance improvements, changed behaviour, and removed functionality

**When NOT to add an entry:**
- Documentation-only changes (`.md` files, `docs/` updates)
- CI/CD workflow changes (`.github/workflows/`)
- Development tooling changes (`.editorconfig`, devcontainer config)
- Refactoring with no user-facing impact
- Test-only changes

**Categories (use as applicable):**
- **Added** — new features or capabilities
- **Changed** — modifications to existing behaviour
- **Fixed** — bug fixes
- **Performance** — optimisations and performance improvements
- **Removed** — removed features (use sparingly)

**Formatting conventions (match existing style):**
- Use `####` subheadings to group related entries under a larger feature (e.g., `#### Scheduler Service (#168)`)
- Reference GitHub issue numbers where applicable (e.g., `(#123)`)
- Keep entries concise — one line per change, describe what changed from the user's perspective
- Use imperative mood is not required — describe what was added/changed/fixed naturally

**At release time:** Move all `[Unreleased]` entries to a new version section and update comparison links at the bottom of the file. See Release Process below.

## Release Process

**CRITICAL: NEVER modify the `VERSION` file without explicit user instruction to create a release.**

The `VERSION` file is the single source of truth for JIM's version number. It feeds into:
- All .NET assembly versions (via `Directory.Build.props`)
- Docker image tags (via release workflow)
- PowerShell module version (updated at release time)
- Diagram metadata (via `export-diagrams.js`)

**Versioning scheme:** [Semantic Versioning](https://semver.org/) — `X.Y.Z` with optional prerelease suffix (e.g., `0.3.0-alpha`).

**What triggers a version bump:**
- New feature releases
- Breaking changes
- Significant bug fix batches
- User explicitly requesting a release

**What does NOT trigger a version bump:**
- Documentation changes
- Diagram regeneration
- CI/CD improvements
- Development tooling changes
- Refactoring without user-facing changes

**Release checklist (all steps require user approval):**
1. Update `VERSION` file with new version
2. Move `[Unreleased]` items in `CHANGELOG.md` to a new version section
3. Update `src/JIM.PowerShell/JIM.psd1` — `ModuleVersion` field
4. Commit: `git commit -m "Release vX.Y.Z"`
5. Tag: `git tag vX.Y.Z`
6. Push: `git push origin main --tags`

> **Full release process, air-gapped deployment, and Docker image details:** See `docs/RELEASE_PROCESS.md`

## Resources

- **Full Architecture Guide**: `docs/DEVELOPER_GUIDE.md`
- **Repository**: https://github.com/TetronIO/JIM
- **Documentation**: `README.md`
- **.NET 9 Docs**: https://learn.microsoft.com/dotnet/
- **EF Core**: https://learn.microsoft.com/ef/core/
- **Blazor**: https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor**: https://mudblazor.com/
