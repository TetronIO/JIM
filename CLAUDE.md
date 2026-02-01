# JIM Development Quick Reference

> Identity Management System - .NET 9.0, EF Core, PostgreSQL, Blazor

## ⚠️ CRITICAL REQUIREMENTS ⚠️

**YOU MUST BUILD AND TEST BEFORE EVERY COMMIT AND PR (for .NET code):**

1. **ALWAYS** run `dotnet build JIM.sln` - Build must succeed with zero errors
2. **ALWAYS** run `dotnet test JIM.sln` - All tests must pass
3. **NEVER** commit code that hasn't been built and tested locally
4. **NEVER** create a PR without verifying build and tests pass
5. **NEVER** assume tests will pass without running them

**EXCEPTIONS:**
- Scripts (.ps1, .sh, etc.) do not require dotnet build/test
- **UI-only changes** (Blazor pages, Razor components, CSS) require `dotnet build` but do NOT require `dotnet test` - there are no UI tests, so running tests just wastes time

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

## ⚠️ SYNCHRONISATION INTEGRITY REQUIREMENTS ⚠️

**SYNCHRONISATION INTEGRITY IS CRITICAL - PRIORITISE IT ABOVE ALL ELSE:**

Synchronisation operations are the core of JIM. Data integrity and reliability are paramount. Customers depend on JIM to synchronise their identity data accurately without corruption or data loss.

**Error Handling Philosophy:**
1. **Fast/Hard Failures**: Better to stop and report an error than continue with corrupted state
2. **Comprehensive Reporting**: ALL errors must be reported via RPEIs/Activities - no silent failures
3. **Defensive Programming**: Anticipate edge cases (duplicates, missing data, type mismatches) and handle explicitly
4. **Trust and Confidence**: Customers must be able to trust that JIM won't silently corrupt their data

**Error Handling Requirements:**

1. **Query Operations Must Be Explicit About Multiplicity:**
   - NEVER use `First()` or `FirstOrDefault()` when you expect exactly one result and would not know what to do with multiple matches
   - NEVER use `Single()` or `SingleOrDefault()` in sync operations without a try-catch that logs and fails the operation
   - If a query might return multiple results, you MUST explicitly handle that case:
     - Either validate that only one result exists before calling Single/SingleOrDefault
     - Or use First/FirstOrDefault and log a warning about unexpected duplicates
     - Or catch the exception and fail the activity with detailed error information
   - Example: `GetConnectedSystemObjectByAttributeAsync()` should have caught and logged the "multiple matches" scenario

2. **All Sync Operation Code Must Be Wrapped in Try-Catch:**
   - Import, sync, and export operations must catch ALL exceptions
   - Exceptions must be logged to RPEI.ErrorType and RPEI.ErrorMessage
   - After catching, evaluate: should this fail the entire activity or just mark this object as errored?
   - When in doubt, fail fast rather than continue with unknown state

3. **Data Integrity Checks Before Operations:**
   - Before creating/updating CSOs, verify no duplicates exist for the same external ID
   - Before creating/updating MVOs, verify the connector space is in expected state
   - Before exporting, verify reference resolution succeeded
   - Log findings - silence is the enemy of debugging

4. **Activity Completion Logic:**
   - Activities with any UnhandledError RPEI items should fail the entire activity
   - Do not treat UnhandledError the same as other error types - it indicates code/logic problems
   - When processing multiple objects, continue collecting errors for all objects, then fail if any UnhandledErrors occurred
   - Never silently skip objects due to exceptions - always fail the activity

5. **Logging for Sync Operations:**
   - Log summary statistics at the end of every batch operation (imports, syncs, exports)
   - Include: Total objects, successfully processed, errored, and categorise error types
   - For integrity issues (duplicates, mismatches), log CSO/MVO IDs so admins can investigate
   - Use appropriate log levels: Debug for normal flow, Warning for unexpected but handled cases, Error for failures

6. **Testing Edge Cases:**
   - Unit tests MUST cover: normal case, empty results, single result, multiple results
   - Unit tests MUST cover: null values, type mismatches, corrupt data states
   - Integration tests MUST verify error reporting when edge cases occur
   - Never assume data will always be in expected state

7. **Code Review Focus for Sync Code:**
   - Pay special attention to database queries and their cardinality assumptions
   - Look for unhandled exceptions in sync loops
   - Verify error handling logs include enough context for debugging
   - Question whether a catch-all exception handler should fail the operation or continue
   - Ask: "Could a customer's data be corrupted if this exception occurs?"

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
- `jim-db` - Start PostgreSQL + Adminer (for local debugging)
- `jim-db-stop` - Stop PostgreSQL + Adminer
- `jim-migrate` - Apply migrations

**Docker Stack Management:**
- `jim-stack` - Start Docker stack (no dev tools, production-like)
- `jim-stack-dev` - Start Docker stack + Adminer
- `jim-stack-logs` - View Docker stack logs
- `jim-stack-down` - Stop Docker stack
- `jim-restart` - Restart stack (re-reads .env, no rebuild)

**Docker Builds (rebuild and start services):**
- `jim-build` - Build all services + start (no dev tools)
- `jim-build-dev` - Build all services + start + Adminer
- `jim-build-web` - Build jim.web + start
- `jim-build-worker` - Build jim.worker + start
- `jim-build-scheduler` - Build jim.scheduler + start

**Reset:**
- `jim-reset` - Reset JIM (delete database & logs volumes)

**Docker (Manual Commands):**
- `docker compose -f db.yml up -d` - Start database + Adminer (same as jim-db)
- `docker compose -f db.yml down` - Stop database + Adminer
- `docker compose logs [service]` - View service logs

**IMPORTANT - Rebuilding Containers After Code Changes:**
When running the Docker stack and you make code changes to JIM.Web, JIM.Worker, or JIM.Scheduler, you MUST rebuild the affected container(s) for changes to take effect:
- `jim-build-web` - Rebuild and restart jim.web service
- `jim-build-worker` - Rebuild and restart jim.worker service
- `jim-build-scheduler` - Rebuild and restart jim.scheduler service
- `jim-build-dev` - Rebuild and restart all services + Adminer

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
- Performance diagnostics: `JIM.Application/Diagnostics/`
- Domain models: `JIM.Models/Core/` or `JIM.Models/Staging/`
- Database repositories: `JIM.PostgresData/`
- Connectors: `JIM.Connectors/` or new connector project
- Tests: `test/JIM.Web.Api.Tests/`, `test/JIM.Models.Tests/`, `JIM.Worker.Tests/`

## ASCII Diagrams

When creating ASCII diagrams in documentation or code comments, use only reliably monospaced characters to ensure proper alignment across all fonts and terminals.

### Arrows

| Use         | Instead of    | Example                        |
|-------------|---------------|--------------------------------|
| `->` `-->`  | `→` `⟶` `>`   | `[Box A] --> [Box B]`          |
| `<-` `<--`  | `←` `⟵` `<`   | `[Box A] <-- [Box B]`          |
| `<->` `<-->` | `↔` `⟷`      | `[Box A] <--> [Box B]`         |
| `v`         | `↓` `▼`       | Downward flow indicator        |
| `^`         | `↑` `▲`       | Upward flow indicator          |

### Box Drawing

| Use | Instead of | Purpose         |
|-----|------------|-----------------|
| `+` | `┌` `┐` `└` `┘` `├` `┤` `┬` `┴` `┼` | Corners and junctions |
| `-` | `─` `═`    | Horizontal lines |
| `|` | `│` `║`    | Vertical lines   |

### Bullet Points in Diagrams

| Use | Instead of | Example           |
|-----|------------|-------------------|
| `-` | `•` `*`    | `- List item`     |

### Example Diagram

```
+-------------------+      +----------------+      +-------------------+
|   Source Systems  |      |    Metaverse   |      |    Target Systems |
|                   |----->|                |----->|                   |
|  - HR System      |      |  - Identity    |      |  - Active Dir     |
|  - Badge System   |      |    Objects     |      |  - ServiceNow     |
+-------------------+      +----------------+      +-------------------+
         |                         |                         |
         v                         v                         v
     IMPORT                    SYNC                      EXPORT
```

### Workflow Diagrams

```
+---------------+
|   Step One    |  Description of step
+-------+-------+
        |
        v
+---------------+
|   Step Two    |  Description of step
+---------------+
```

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

**⚠️ CRITICAL: EF Core In-Memory Database Limitation:**
- Unit and workflow tests use EF Core's in-memory database which **auto-tracks navigation properties**
- This MASKS bugs where `.Include()` statements are missing from repository queries
- **Integration tests are the ONLY reliable way to verify navigation property loading**
- When modifying repository queries, ALWAYS run integration tests to verify `.Include()` chains are correct
- Add defensive null checks with logging for navigation properties to catch missing `.Include()` at runtime
- See `docs/TESTING_STRATEGY.md` for full details and real-world example (Drift Detection bug January 2026)

## Test Data Generation

**Change History UI Test Data:**

For testing the Change History UI (CSO and MVO change timelines), use the SQL seed script rather than workflow tests for faster iteration:

```bash
# Run against your development/test database
docker compose exec jim.database psql -U jim -d jim_test -f /workspaces/JIM/test/data/seed-change-history.sql
```

**Maintaining the SQL Script:**

The SQL script at `test/data/seed-change-history.sql` generates realistic change history data for UI testing. **If the database schema changes** (e.g., new columns, renamed tables, changed relationships for MetaverseObjectChanges, ConnectedSystemObjectChanges, or related tables), you MUST regenerate this script:

1. **When to regenerate:**
   - Migrations added/changed for MetaverseObjectChanges, MetaverseObjectChangeAttributes, MetaverseObjectChangeAttributeValues tables
   - Migrations added/changed for ConnectedSystemObjectChanges and related tables
   - New enum values for ObjectChangeType, ValueChangeType, or ChangeInitiatorType
   - Changes to MetaverseObject, MetaverseAttribute, or navigation property structures

2. **How to regenerate:**
   - Read the current `test/data/seed-change-history.sql` to understand the data scenario
   - Review recent migrations in `JIM.PostgresData/Migrations/` to understand schema changes
   - Rewrite the SQL script to match the new schema while preserving the same realistic test scenario:
     - Alice (Person): 5-7 changes including promotions, department moves, email updates, salary changes
     - Bob (Person): 7-9 changes including manager reference changes (add/remove/re-add Alice as manager)
     - Engineers Group: 4-5 changes including name changes and member additions/removals (Alice, Bob)
     - Platform Team Group: 1-3 changes including description updates
   - Test the script works by running it against a fresh test database
   - Document any schema-specific requirements in comments within the SQL file

3. **Script design principles:**
   - Self-contained: Creates MVOs and attributes if they don't exist
   - Idempotent where possible: Check for existing data before inserting
   - Realistic enterprise scenarios: Job titles, departments, salaries, dates that make sense
   - Covers all attribute types: Text, Number, LongNumber, DateTime, Boolean, Reference
   - Tests edge cases: Reference attributes being added/removed multiple times
   - Output URLs at end: Print MVO IDs so user can immediately navigate to test pages

4. **Alternative - Workflow Tests:**
   If you prefer writing C# workflow tests instead of SQL, see `/workspaces/JIM/test/JIM.Workflow.Tests/ChangeHistoryScenarioTests.cs` for a starting point (incomplete as of Jan 2026). Workflow tests are slower to run but type-safe and easier to maintain if you understand the WorkflowTestHarness API.

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

## Third-Party Dependency Governance

**IMPORTANT: JIM maintains strict supply chain security standards for SBOM compliance and customer assurance.**

**Before Adding ANY New NuGet Package or Third-Party Dependency:**

1. **Notify the user first** - State the need for the dependency and that you will conduct a suitability analysis
2. **Research and document** the following for each candidate package:
   - **License**: Must be permissive (MIT, Apache 2.0, BSD) and compatible with commercial use
   - **Author/Maintainer**: Identifiable individuals or organisations with verifiable professional presence
   - **Provenance**: Organisation location, business registration (if applicable), corporate affiliation
   - **Maintenance Status**: Recent commits, responsiveness to issues, release frequency
   - **Community Trust**: Download counts, GitHub stars, usage by other reputable projects
   - **Security**: Known vulnerabilities, security advisory history

3. **Present findings to the user** with:
   - A comparison table if multiple alternatives exist
   - Clear recommendation with rationale
   - Any concerns or trade-offs

4. **Await user approval** before adding the dependency

**Preferred Package Sources:**
- Microsoft-maintained packages (highest preference)
- Packages from established Western technology companies with clear corporate backing
- Well-maintained open-source projects with identifiable maintainers in NATO-aligned countries
- .NET Foundation projects

**Package Selection Criteria:**
- Prefer packages with corporate backing or foundation governance
- Prefer packages with multiple maintainers (bus factor > 1)
- Prefer packages with clear security policies and vulnerability disclosure processes
- Avoid packages with unclear ownership or governance
- Avoid packages that haven't been updated in >12 months (unless stable/complete)

**Documentation Requirements:**
- All third-party dependencies must be justifiable for SBOM audits
- Keep a mental note of why each dependency was chosen over alternatives
- If a dependency is replaced, document the reason in the commit message

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

**AI Assistant Context Documents:**

JIM has context documents for use with AI assistant platforms (Claude Desktop, ChatGPT, etc.) for ideation and research:
- `docs/JIM_AI_ASSISTANT_INSTRUCTIONS.md` - System prompt/instructions to copy
- `docs/JIM_AI_ASSISTANT_CONTEXT.md` - Comprehensive context document to upload

**Keep these updated when:**
- MVP status changes significantly (update Section 8 - Current Status)
- New connectors are added (update Section 4 - Connectors)
- Architecture changes materially (update Section 2 - Architecture)
- Key terminology or concepts change (update Section 11 - Glossary)

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

**⚠️ CRITICAL: Respect N-Tier Architecture - NEVER Bypass Layers:**

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
1. Start all services: `jim-stack` (or `jim-stack-dev` for Adminer)
2. Access containerized services
3. Services: Web + API (http://localhost:5200), Swagger at `/api/swagger`, Adminer at http://localhost:8080 (if using jim-stack-dev)

**Use Workflow 1** for active development and debugging.
**Use Workflow 2** for integration testing or production-like environment.

## Integration Testing

**IMPORTANT: The correct way to run integration tests is NOT by directly invoking scenario scripts.**

Instead, use the main integration test runner which handles setup, environment management, and teardown:

```powershell
# From repository root, run in PowerShell (not bash/zsh)
cd /workspaces/JIM

# Interactive menu - select scenario with arrow keys (↑/↓) and press Enter
./test/integration/Run-IntegrationTests.ps1

# Run a specific scenario directly
./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory

# Run with a specific template size (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)
./test/integration/Run-IntegrationTests.ps1 -Template Small

# Run only a specific test step (Joiner, Mover, Leaver, Reconnection, etc.)
./test/integration/Run-IntegrationTests.ps1 -Step Joiner

# Skip reset for faster re-runs (keeps existing environment)
./test/integration/Run-IntegrationTests.ps1 -SkipReset

# Skip rebuild (use existing Docker images)
./test/integration/Run-IntegrationTests.ps1 -SkipReset -SkipBuild
```

**What the runner does automatically:**
1. ✅ Resets environment (stops containers, removes volumes)
2. ✅ Rebuilds and starts JIM stack + Samba AD
3. ✅ Waits for all services to be ready
4. ✅ Creates infrastructure API key
5. ✅ Generates test data (CSV, Samba AD users)
6. ✅ Configures JIM with connected systems and sync rules
7. ✅ Runs the scenario
8. ✅ Tears down all containers

**For detailed integration testing guide, see:** [`docs/INTEGRATION_TESTING.md`](docs/INTEGRATION_TESTING.md)

**Common templates by data size:**
- **Nano**: 3 users, 1 group (~10 sec) - Fast dev iteration
- **Micro**: 10 users, 3 groups (~30 sec) - Quick smoke tests
- **Small**: 100 users, 20 groups (~2 min) - Small business scenarios
- **Medium**: 1,000 users, 100 groups (~2 min) - Medium enterprise
- **Large**: 10,000 users, 500 groups (~15 min) - Large enterprise

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
  - Query returning unexpected number of results (e.g., `SingleOrDefaultAsync` with duplicates) → Use explicit cardinality checks or try-catch
  - Missing or null data where code expected values → Add defensive null checks before operations
  - Type casting errors or invalid data states → Add data validation before operations
- DO NOT silently ignore UnhandledErrors - they indicate data integrity risk
- Investigate root cause before retrying sync
- Example: "Sequence contains more than one element" → Duplicates exist, verify uniqueness constraint or add duplicate detection

**Sync Statistics Not What Expected:**
- Check log for summary statistics at end of import/sync/export (look for "SUMMARY - Total objects")
- Verify Run Profile is selecting correct partition/container
- Verify sync rules are correctly scoped to object types
- Check for DuplicateObject errors - indicates deduplication is working
- If all objects errored but Activity marked complete → Bug in error handling, report to dev

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
