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
- **Static assets** (CSS, JS, images) do not require dotnet build/test - these are served directly without compilation
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

**IMPORTANT - Docker Dependency Pinning:**
Production Dockerfiles pin base image digests (`@sha256:...`) and functional apt package versions for reproducible builds. When modifying Dockerfiles:
- **NEVER** remove the `@sha256:` digest from `FROM` lines
- **NEVER** remove version pins from functional apt packages (libldap, cifs-utils)
- Diagnostic utilities (curl, iputils-ping) are intentionally unpinned
- If updating a base image digest, check and update pinned apt versions to match (see `docs/DEVELOPER_GUIDE.md` "Dependency Pinning" section)
- Dependabot manages digest updates via weekly PRs - these require manual review, not auto-merge

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

## Security Development Guidelines

**Aligned with: NCSC Secure Development Principles, CISA Secure by Design, OWASP ASVS, UK Software Security Code of Practice**

JIM is an identity management system deployed in government, defence, healthcare, financial services, and critical national infrastructure environments. All code MUST meet the security expectations of these sectors.

**Reference Standards:**
- UK NCSC: Secure Development and Deployment Guidance
- UK Government: Software Security Code of Practice (May 2025)
- CISA: Secure by Design Principles
- OWASP: Application Security Verification Standard (ASVS) v4.0
- NIST: SP 800-53 Rev 5 (Security and Privacy Controls)

### OWASP Top 10 Awareness

All developers MUST be aware of and actively prevent the OWASP Top 10 vulnerabilities:

1. **Broken Access Control**
   - Enforce authorisation checks on every API endpoint and Blazor page
   - Use `[Authorize]` attributes - never rely on UI-only access control
   - Deny by default - explicitly grant access, never implicitly allow
   - Validate that the authenticated user has permission for the requested resource (not just role, but ownership/scope)

2. **Cryptographic Failures**
   - Use AES-256-GCM for encryption at rest (already implemented for credentials)
   - Enforce TLS for all data in transit
   - NEVER implement custom cryptography - use ASP.NET Core Data Protection API or established libraries
   - NEVER log sensitive data (credentials, tokens, personal data)

3. **Injection**
   - ALWAYS use parameterised queries (EF Core does this by default - never bypass with raw SQL unless parameterised)
   - Validate and sanitise all input at API boundaries
   - Use DTOs with data annotations for API request models
   - For LDAP operations: escape special characters in DN components and search filters

4. **Insecure Design**
   - Conduct threat modelling for new features that handle authentication, authorisation, or sensitive data
   - Apply the principle of least privilege throughout
   - Fail securely - errors must not expose internal details or grant unintended access
   - Design sync operations to be idempotent where possible

5. **Security Misconfiguration**
   - Ship secure defaults (SSO required, no default credentials, HTTPS enforced)
   - Remove or disable development/debug features in production builds
   - Keep Swagger UI to development environment only
   - Validate all configuration values at startup

6. **Vulnerable and Outdated Components**
   - Monitor NuGet dependencies for known vulnerabilities (see Supply Chain Security)
   - Keep .NET runtime and all packages up to date
   - Review transitive dependency vulnerabilities

7. **Identification and Authentication Failures**
   - SSO/OIDC is mandatory - no local authentication
   - API keys must be cryptographically random and sufficiently long
   - Implement proper session management via ASP.NET Core authentication middleware
   - Enforce PKCE for OIDC flows

8. **Software and Data Integrity Failures**
   - Verify integrity of release artefacts (SHA256 checksums already implemented)
   - Sign Docker images where possible
   - Validate data imported from connected systems before processing
   - Protect sync rules and configuration from unauthorised modification

9. **Security Logging and Monitoring Failures**
   - Log all authentication events (success and failure)
   - Log all authorisation failures
   - Log all administrative actions (configuration changes, sync rule modifications)
   - Log API key creation and usage
   - NEVER log credentials, tokens, or personal data in cleartext
   - Include correlation IDs for tracing across services

10. **Server-Side Request Forgery (SSRF)**
    - Validate and restrict connector target URLs/hosts
    - Do not allow user-controlled URLs to be fetched without validation
    - Restrict outbound network access from the application where possible

### Input Validation Requirements

- Validate ALL input at system boundaries (API controllers, Blazor form submissions)
- Use Data Annotation attributes on all API request DTOs
- Apply maximum length constraints to all string properties
- Validate GUIDs, enums, and numeric ranges explicitly
- For file paths (connector configuration): validate against path traversal (`..`, absolute paths outside allowed directories)
- For LDAP filters and DNs: use library-provided escaping functions
- For expressions (sync rule attribute flows): evaluate in a sandboxed context (DynamicExpresso is already sandboxed)

### Authentication and Authorisation Patterns

```csharp
// CORRECT: Authorise at the controller level
[Authorize]
[ApiController]
public class SensitiveController : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<ActionResult<ResourceDto>> GetResource(Guid id)
    {
        // Verify user has access to this specific resource
        var resource = await _app.GetResourceAsync(id);
        if (resource == null) return NotFound();
        return Ok(resource.ToDto());
    }
}

// WRONG: No authorisation, or relying on UI to hide the endpoint
[ApiController]
public class InsecureController : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<ActionResult<ResourceDto>> GetResource(Guid id)
    {
        // No [Authorize] attribute - accessible to anyone
        return Ok(await _app.GetResourceAsync(id));
    }
}
```

### Cryptography Standards

- **Encryption at rest**: AES-256-GCM (NIST approved) via ASP.NET Core Data Protection API
- **Integrity verification**: HMAC-SHA256
- **Hashing**: SHA-256 or SHA-512 (never MD5 or SHA-1)
- **Random number generation**: `System.Security.Cryptography.RandomNumberGenerator` (never `System.Random` for security-sensitive values)
- **Key management**: Automatic rotation via Data Protection API, keys stored separately from data
- **TLS**: Minimum TLS 1.2 for all network connections (connectors, LDAP, OIDC)

### Secrets Handling

- NEVER hardcode secrets, credentials, API keys, or connection strings in source code
- NEVER log secrets or include them in error messages
- NEVER commit `.env` files, certificates, or key material to the repository
- Store all secrets via environment variables (loaded from `.env` which is gitignored)
- Encrypt stored credentials using the Data Protection API (already implemented)
- Ensure secrets are excluded from serialisation (use `[JsonIgnore]` where appropriate)
- Clear sensitive data from memory when no longer needed (use `SecureString` where practical)

### Security Testing Requirements

- Write tests that verify authorisation enforcement (attempt access without credentials, with wrong role)
- Write tests that verify input validation rejects malformed data
- Write tests for boundary conditions (empty strings, maximum lengths, special characters)
- For sync operations: test with adversarial data (special characters in attribute values, oversized payloads, unexpected types)

## Secure by Design Principles

**Aligned with: NCSC Secure Development and Deployment Guidance, CISA Secure by Design Pledge, UK Software Security Code of Practice**

These principles are mandatory for all JIM development. They align with the expectations of government, defence, and regulated industry customers.

### 1. Take Ownership of Security Outcomes

- Security is every developer's responsibility, not a separate team's concern
- Consider the security impact of every change, however small
- When in doubt about a security implication, raise it - do not assume it is acceptable

### 2. Embrace Radical Transparency

- Maintain a clear vulnerability disclosure policy (SECURITY.md)
- Publish complete CVE records for any discovered vulnerabilities
- Document security-relevant design decisions in ADRs (Architecture Decision Records)
- Provide customers with sufficient information to assess JIM's security posture

### 3. Lead from the Top

- Security requirements take precedence over feature velocity
- A security issue is always a valid reason to delay a release
- Security-relevant changes require explicit review

### 4. Secure by Default

- JIM ships with secure defaults that require no additional configuration to be safe:
  - SSO/OIDC required (no option for insecure local authentication)
  - Credentials encrypted at rest by default
  - HTTPS enforced in production deployments
  - API authentication required on all endpoints
  - Audit logging enabled by default
- Security features must not be optional or require the customer to "turn them on"
- If a configuration can be insecure, make the secure option the default

### 5. Secure Design from the Start

- New features involving authentication, authorisation, data handling, or external system communication MUST include a threat assessment before implementation
- Use threat modelling (STRIDE or similar) for significant new features
- Consider: What could go wrong? What is the blast radius? How would we detect misuse?
- Document threat considerations in the feature plan (in `docs/plans/`)

### 6. Defence in Depth

- Never rely on a single security control
- Layers of defence: network segmentation, authentication, authorisation, input validation, encryption, logging
- Assume any single layer can be bypassed and design accordingly
- Example: Even though EF Core parameterises queries, still validate input at the API boundary

### 7. Minimise Attack Surface

- Do not expose unnecessary endpoints, services, or configuration options
- Remove or disable development features in production (Swagger UI, detailed error messages)
- Apply the principle of least privilege to service accounts, database permissions, and API scopes
- JIM's air-gapped deployment model inherently reduces network-based attack surface

### 8. Fail Securely

- Errors must not reveal internal implementation details to end users
- Authentication/authorisation failures must not distinguish between "user not found" and "wrong password"
- Failed operations must leave the system in a secure state (no partial writes that bypass validation)
- This aligns with the existing Synchronisation Integrity Requirements: fail fast, report clearly

### 9. Maintain and Patch

- Respond to reported vulnerabilities within the timescales in SECURITY.md
- Keep all dependencies updated (see Supply Chain Security)
- Monitor for security advisories affecting .NET, Npgsql, and other core dependencies

## Supply Chain Security

**Aligned with: NCSC Supply Chain Security Guidance, CISA SBOM Requirements, NIST SP 800-161**

JIM's target customers (government, defence, CNI) require confidence in the software supply chain. These requirements ensure JIM meets procurement expectations.

### Dependency Management

- Review all new NuGet package additions for:
  - Maintenance status (actively maintained, last update date)
  - Known vulnerabilities (check NVD/GitHub Advisory Database)
  - Licence compatibility
  - Publisher reputation and community adoption
- Prefer well-established, widely-used packages over niche alternatives
- Pin dependency versions in `.csproj` files (avoid floating versions)
- Run `dotnet list package --vulnerable` regularly to check for known vulnerabilities
- Run `dotnet list package --outdated` regularly to identify available updates

### SBOM (Software Bill of Materials)

- Generate SBOM in CycloneDX or SPDX format as part of the release process
- Include all direct and transitive dependencies
- Customers in government and defence procurement increasingly require SBOM as a procurement condition
- Tool: Consider `CycloneDX` dotnet tool for automated SBOM generation

### Build Integrity

- Release builds must be reproducible from a specific Git commit
- All release artefacts include SHA256 checksums (already implemented)
- Docker images should be built from pinned base images with digest references
- GitHub Actions workflows should pin action versions by SHA, not tag

### Third-Party Component Policy

- Only use NuGet packages from NuGet.org (no private/unknown feeds in production builds)
- Evaluate any new dependency against:
  - Does it have known CVEs?
  - Is it actively maintained (commits within last 12 months)?
  - Does it have a responsible disclosure/security policy?
  - What is the licence? (Compatible with JIM's source-available licence?)
- Document all third-party components and their purposes in the SBOM

### Container Security

- Use minimal base images (e.g., `mcr.microsoft.com/dotnet/aspnet:9.0` for runtime, not SDK)
- Run containers as non-root users
- Do not include build tools, debug utilities, or package managers in production images
- Scan container images for vulnerabilities before release

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
  - COMPLIANCE_MAPPING.md - Security framework and standards compliance mapping
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
1. Start all services: `jim-stack`
2. Access containerized services
3. Services: Web + API (http://localhost:5200), Swagger at `/api/swagger`

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

# Setup only - configure environment without running tests (for demos, manual exploration)
./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -SetupOnly
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
