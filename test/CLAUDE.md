# Testing Reference

> Detailed testing patterns for JIM. See root `CLAUDE.md` for build/test requirements.

## Test Structure

- Use NUnit with `[Test]` attribute
- Async tests: `public async Task TestNameAsync()`
- Use `Assert.That()` syntax
- Mock with Moq: `Mock<DbSet<T>>`
- Test naming: `MethodName_Scenario_ExpectedResult`

**Common Test Pattern:**
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

## Debugging Failing Tests

- Claude Code cannot interactively debug with breakpoints like an IDE
- To diagnose issues, add temporary `Console.WriteLine()` statements to trace execution and inspect variable values
- Test output appears in the test results under "Standard Output Messages"
- **IMPORTANT**: Remove all debug statements before committing

## EF Core In-Memory Database Limitation

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

## Integration Testing

**IMPORTANT: The correct way to run integration tests is NOT by directly invoking scenario scripts.**

Instead, use the main integration test runner which handles setup, environment management, and teardown:

```powershell
# From repository root, run in PowerShell (not bash/zsh)
cd /workspaces/JIM

# Interactive menu - select scenario with arrow keys
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
1. Resets environment (stops containers, removes volumes)
2. Rebuilds and starts JIM stack + Samba AD
3. Waits for all services to be ready
4. Creates infrastructure API key
5. Generates test data (CSV, Samba AD users)
6. Configures JIM with connected systems and sync rules
7. Runs the scenario
8. Tears down all containers

**For detailed integration testing guide, see:** [`docs/INTEGRATION_TESTING.md`](docs/INTEGRATION_TESTING.md)

**Common templates by data size:**
- **Nano**: 3 users, 1 group (~10 sec) - Fast dev iteration
- **Micro**: 10 users, 3 groups (~30 sec) - Quick smoke tests
- **Small**: 100 users, 20 groups (~2 min) - Small business scenarios
- **Medium**: 1,000 users, 100 groups (~2 min) - Medium enterprise
- **Large**: 10,000 users, 500 groups (~15 min) - Large enterprise
