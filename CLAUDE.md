# JIM Development Quick Reference

> Identity Management System - .NET 9.0, EF Core, PostgreSQL, Blazor

## ⚠️ CRITICAL REQUIREMENTS ⚠️

**YOU MUST BUILD AND TEST BEFORE EVERY COMMIT AND PR:**

1. **ALWAYS** run `dotnet build JIM.sln` - Build must succeed with zero errors
2. **ALWAYS** run `dotnet test JIM.sln` - All tests must pass
3. **NEVER** commit code that hasn't been built and tested locally
4. **NEVER** create a PR without verifying build and tests pass
5. **NEVER** assume tests will pass without running them

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
- Worker/business logic tests: `JIM.Worker.Tests/`

**Failure to build and test wastes CI/CD resources and delays the project.**

If you cannot build/test locally due to environment constraints, you MUST:
- Clearly state this limitation in the PR description
- Mark the PR as draft
- Request manual review and testing before merge

## Scripting

**IMPORTANT: Use PowerShell for ALL scripts:**
- All automation, integration tests, and utility scripts MUST be written in PowerShell (.ps1)
- PowerShell is cross-platform and works on Linux, macOS, and Windows
- Exception: `.devcontainer/setup.sh` is acceptable as it runs during container creation
- Never create bash/shell scripts for project automation or testing

## Commands

**Build & Test:**
- `dotnet build JIM.sln` - Build entire solution
- `dotnet test JIM.sln` - Run all tests
- `dotnet test --filter "FullyQualifiedName~TestName"` - Run specific test
- `dotnet clean && dotnet build` - Clean build

**Database:**
- `dotnet ef migrations add [Name] --project JIM.PostgresData` - Add migration
- `dotnet ef database update --project JIM.PostgresData` - Apply migrations
- `docker compose exec jim.web dotnet ef database update` - Apply migrations in Docker

**Shell Aliases (Recommended):**
- Aliases are automatically configured from `.devcontainer/jim-aliases.sh`
- If aliases don't work, run: `source ~/.zshrc` (or restart terminal)
- `jim` - List all available jim aliases
- `jim-compile` - Build entire solution (dotnet build)
- `jim-test` - Run all tests
- `jim-db` - Start PostgreSQL (for local debugging)
- `jim-db-stop` - Stop PostgreSQL
- `jim-migrate` - Apply migrations

**Docker Stack Management:**
- `jim-stack` - Start Docker stack (no build, uses existing images)
- `jim-stack-logs` - View Docker stack logs
- `jim-stack-down` - Stop Docker stack

**Docker Builds (rebuild and start services):**
- `jim-build` - Build all services + start
- `jim-build-web` - Build jim.web + start
- `jim-build-worker` - Build jim.worker + start
- `jim-build-scheduler` - Build jim.scheduler + start

**Reset:**
- `jim-reset` - Reset JIM (delete database & logs volumes)

**Docker (Manual Commands):**
- `docker compose -f db.yml up -d` - Start database only (same as jim-db)
- `docker compose -f db.yml down` - Stop database
- `docker compose logs [service]` - View service logs

**IMPORTANT - Rebuilding Containers After Code Changes:**
When running the Docker stack and you make code changes to JIM.Web, JIM.Worker, or JIM.Scheduler, you MUST rebuild the affected container(s) for changes to take effect:
- `jim-build-web` - Rebuild and restart jim.web service
- `jim-build-worker` - Rebuild and restart jim.worker service
- `jim-build-scheduler` - Rebuild and restart jim.scheduler service
- `jim-build-stack` - Rebuild and restart all services

Blazor pages, API controllers, and other compiled code require container rebuilds. Simply refreshing the browser will not show changes.

## Key Project Locations

**Where to add:**
- API endpoints: `JIM.Web/Controllers/Api/`
- API models/DTOs: `JIM.Web/Models/Api/`
- API extensions: `JIM.Web/Extensions/Api/`
- API middleware: `JIM.Web/Middleware/Api/`
- UI pages: `JIM.Web/Pages/`
- Blazor components: `JIM.Web/Shared/`
- Business logic: `JIM.Application/Servers/`
- Domain models: `JIM.Models/Core/` or `JIM.Models/Staging/`
- Database repositories: `JIM.PostgresData/`
- Connectors: `JIM.Connectors/` or new connector project
- Tests: `test/JIM.Web.Api.Tests/`, `test/JIM.Models.Tests/`, `JIM.Worker.Tests/`

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

**File Organisation:**
- One class per file - each class should have its own `.cs` file named after the class
- Exception: Enums are grouped into a single file per area/folder (e.g., `ConnectedSystemEnums.cs`, `PendingExportEnums.cs`)
- File names must match the class/interface name exactly (e.g., `MetaverseObject.cs` for `class MetaverseObject`)

**Naming Patterns:**
- Methods: `GetObjectAsync`, `CreateMetaverseObjectAsync`
- Classes: Full descriptive names (avoid abbreviations)
- Properties: PascalCase with nullable reference types enabled

**Common Patterns:**
```csharp
// Async all I/O
public async Task<MetaverseObject> GetObjectAsync(Guid id)
{
    return await _repository.Metaverse.GetObjectAsync(id);
}

// Constructor injection
public class MyServer
{
    private readonly IRepository _repository;

    public MyServer(IRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }
}

// Error handling with logging
try
{
    await ProcessSync();
}
catch (Exception ex)
{
    Log.Error(ex, "Sync failed for {SystemId}", systemId);
    throw;
}
```

## Testing

**Before Committing (MANDATORY - NO EXCEPTIONS):**
- ⚠️ **CRITICAL**: YOU MUST build and test locally before EVERY commit
- Run `dotnet build JIM.sln` - Must complete with zero errors
- Run `dotnet test JIM.sln` - All tests must pass
- For specific test projects: `dotnet test JIM.Worker.Tests/JIM.Worker.Tests.csproj`
- **DO NOT proceed to commit if any tests fail or build has errors**

**Test Structure:**
- Use NUnit with `[Test]` attribute
- Async tests: `public async Task TestNameAsync()`
- Use `Assert.That()` syntax
- Mock with Moq: `Mock<DbSet<T>>`
- Test naming: `MethodName_Scenario_ExpectedResult`

**Common Test Patterns:**
```csharp
[Test]
public async Task GetObjectAsync_WithValidId_ReturnsObject()
{
    // Arrange
    var expectedObject = new MetaverseObject { Id = Guid.NewGuid() };

    // Act
    var result = await _server.GetObjectAsync(expectedObject.Id);

    // Assert
    Assert.That(result, Is.Not.Null);
    Assert.That(result.Id, Is.EqualTo(expectedObject.Id));
}
```

**Debugging Failing Tests:**
- Claude Code cannot interactively debug with breakpoints like an IDE
- To diagnose issues, add temporary `Console.WriteLine()` statements to trace execution and inspect variable values
- Test output appears in the test results under "Standard Output Messages"
- **IMPORTANT**: Remove all debug statements before committing

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

## Feature Planning

**IMPORTANT: When creating plans for new features or significant changes:**

1. **Create a plan document in `docs/plans/`:**
   - Use uppercase filename with underscores: e.g., `PROGRESS_REPORTING.md`, `SCIM_SERVER_DESIGN.md`
   - Include comprehensive details: Overview, Architecture, Implementation Phases, Success Criteria, Benefits
   - Mark status (Planned/In Progress/Completed) and milestone (MVP/Post-MVP)
   - Keep plan focused but detailed enough for implementation

2. **Create a GitHub issue:**
   - Brief description of the feature/change
   - Link to the plan document in `docs/plans/` for full details
   - Assign to appropriate milestone (MVP, Post-MVP, etc.)
   - Add relevant labels (enhancement, bug, documentation, etc.)
   - Example: "See full implementation plan: [`docs/plans/PROGRESS_REPORTING.md`](docs/plans/PROGRESS_REPORTING.md)"

3. **Plan structure guidelines:**
   - **Overview**: Brief summary of what and why
   - **Business Value**: Problem being solved and benefits
   - **Technical Architecture**: Current state, proposed solution, data flow
   - **Implementation Phases**: Numbered phases with specific deliverables
   - **Success Criteria**: Measurable outcomes
   - **Benefits**: Performance, UX, architecture improvements
   - **Dependencies**: External packages, services, infrastructure
   - **Risks & Mitigations**: Potential issues and solutions

**Documentation organisation:**
- `docs/plans/` - Feature plans and design documents (future work)
- `docs/` - Active guides and references (current/completed work)
  - DEVELOPER_GUIDE.md - Comprehensive development guide
  - INTEGRATION_TESTING.md - Integration testing guide
  - MVP_DEFINITION.md - MVP scope and criteria
  - RELEASE_PROCESS.md - Release and deployment procedures
  - SSO_SETUP_GUIDE.md - SSO configuration instructions

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

**Access Pattern:**
```csharp
// Access via JimApplication facade
var jim = new JimApplication(repository);
var obj = await jim.Metaverse.GetObjectAsync(id);
var systems = await jim.ConnectedSystems.GetAllAsync();
```

## Common Development Tasks

**Adding a Connector:**
1. Implement `IConnector` and capability interfaces
2. Add to `JIM.Connectors/` or create new project
3. Register in DI container
4. Add tests

**Adding API Endpoint:**
1. Add method to controller in `JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `JIM.Web/Models/Api/`)
3. Add XML comments for Swagger
4. Test via Swagger UI at `/api/swagger`

**Modifying Database Schema:**
1. Update entity in `JIM.Models/`
2. Create migration: `dotnet ef migrations add [Name] --project JIM.PostgresData`
3. Review generated migration
4. Test: `dotnet ef database update --project JIM.PostgresData`
5. Commit migration files

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

## Workflow Best Practices

**Git:**
- Branch naming: `feature/description` or `claude/description-sessionId`
- Commit messages: Descriptive, include issue reference if applicable
- ⚠️ **CRITICAL**: Build and test before EVERY commit - NO EXCEPTIONS
- Push to feature branches, create PRs to main

**Development Cycle (FOLLOW THIS EXACTLY):**
1. Create/checkout feature branch
2. Make changes
3. ⚠️ **MANDATORY: Build**: `dotnet build JIM.sln` - Must succeed
4. ⚠️ **MANDATORY: Test**: `dotnet test JIM.sln` - All must pass
5. If build or tests fail, fix errors and repeat steps 3-4
6. **ONLY AFTER** build and tests pass: Commit with clear message
7. **ONLY AFTER** successful commit: Push and create PR
8. **NEVER** create a PR with failing tests or build errors

## Resources

- **Full Architecture Guide**: `docs/DEVELOPER_GUIDE.md`
- **Repository**: https://github.com/TetronIO/JIM
- **Documentation**: `README.md`
- **.NET 9 Docs**: https://learn.microsoft.com/dotnet/
- **EF Core**: https://learn.microsoft.com/ef/core/
- **Blazor**: https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor**: https://mudblazor.com/
