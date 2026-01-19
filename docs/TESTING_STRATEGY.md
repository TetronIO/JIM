# Testing Strategy

## Overview

JIM employs a three-tier testing approach to ensure quality at different levels of the application:

```
Integration Tests (Full System)
         ^
   Workflow Tests (Multi-Component)
         ^
    Unit Tests (Single Component)
```

## 1. Unit Tests

**Location**: `test/JIM.Worker.Tests/`, `test/JIM.Models.Tests/`, `test/JIM.Web.Api.Tests/`

**Purpose**: Test individual methods and classes in isolation using mocks

**Characteristics**:
- Fast execution (milliseconds per test)
- Use mocking frameworks (Moq) to isolate dependencies
- Test single units of code (methods, classes)
- Focus on logic correctness, edge cases, error handling

**Example**: Testing repository query logic for delta sync

```csharp
[Test]
public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithModifiedCsos_ReturnsCsosAfterWatermarkAsync()
{
    // Arrange: Mock repository returns filtered CSOs
    var watermark = DateTime.UtcNow.AddHours(-1);
    _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(...))
        .ReturnsAsync(pagedResult);

    // Act: Call the method
    var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(...);

    // Assert: Verify correct CSOs returned
    Assert.That(result.Results, Has.Count.EqualTo(1));
}
```

**What Unit Tests Are Good At**:
- ✅ Testing data layer queries
- ✅ Testing business rules
- ✅ Testing validation logic
- ✅ Testing error handling

**What Unit Tests Miss**:
- ❌ Integration between components (e.g., Full Sync -> Delta Sync watermark handoff)
- ❌ Multi-step workflows
- ❌ End-to-end business processes

## 2. Workflow Tests (RECOMMENDED - NEW CATEGORY)

**Location**: `test/JIM.Worker.Tests/Workflows/` (to be implemented)

**Purpose**: Test multi-step business processes using real implementations with in-memory database

**Characteristics**:
- Moderate execution speed (seconds per test)
- Use in-memory EF Core database (no Docker required)
- Test multiple components working together
- Focus on workflow correctness and component integration

**Example**: Testing Full Sync -> Delta Sync watermark workflow

```csharp
[Test]
public async Task FullSyncThenDeltaSync_WithOneModifiedCso_ProcessesOnlyModifiedCso()
{
    // Arrange: Create system with 100 CSOs using real database
    var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
    for (int i = 0; i < 100; i++)
    {
        await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, $"user{i}");
    }

    // Act 1: Run Full Sync (real processor, not mocked)
    var fullSyncProcessor = new SyncFullSyncTaskProcessor(...);
    await fullSyncProcessor.PerformFullSyncAsync();

    // Assert: Watermark was set
    await ReloadEntityAsync(connectedSystem);
    Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);

    // Act 2: Modify 1 CSO and run Delta Sync
    csos[50].LastUpdated = DateTime.UtcNow;
    await repository.UpdateConnectedSystemObjectAsync(csos[50]);

    var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(...);
    await deltaSyncProcessor.PerformDeltaSyncAsync();

    // Assert: ONLY 1 CSO processed, not all 100!
    Assert.That(deltaSyncActivity.ObjectsProcessed, Is.EqualTo(1));
}
```

**What Workflow Tests Are Good At**:
- ✅ Testing multi-step workflows (Import -> Sync -> Export)
- ✅ Testing component integration (Full Sync sets watermark, Delta Sync uses it)
- ✅ Testing business process correctness
- ✅ Catching orchestration bugs (like the watermark bug)

**When to Write Workflow Tests**:
- When implementing new multi-step features (e.g., Delta Sync)
- When fixing bugs involving component interaction
- When refactoring workflows to ensure behaviour preserved
- For critical business processes (provisioning, deletion, sync)

**Implementation Guidelines**:
1. Create `test/JIM.Worker.Tests/Workflows/` directory
2. Create `WorkflowTestBase` with helper methods for test data setup
3. Use in-memory EF Core database: `UseInMemoryDatabase(Guid.NewGuid().ToString())`
4. Test name format: `<Scenario>_<Action>_<ExpectedOutcome>`
5. Keep workflows focused (test one business process per test)

## 3. Integration Tests

**Location**: `test/integration/`

**Purpose**: Test the entire system end-to-end with external dependencies

**Characteristics**:
- Slow execution (minutes per test)
- Uses Docker containers (PostgreSQL, Samba AD, etc.)
- Tests full stack including API, database, connectors
- Focus on system-level correctness and realistic scenarios

**Example**: HR to Directory synchronisation scenario

```powershell
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template Large -ApiKey "jim_..."
```

**What Integration Tests Are Good At**:
- ✅ Testing the full system as users experience it
- ✅ Testing with real external systems (LDAP, AD, etc.)
- ✅ Testing performance at scale
- ✅ Testing deployment configuration

**What Integration Tests Miss**:
- ❌ Quick feedback (too slow for TDD)
- ❌ Isolation (failures could be infrastructure, not code)
- ❌ Fine-grained debugging (too many moving parts)

## The Watermark Bug: A Case Study

**The Bug**: Delta Sync processed ALL 10,000 CSOs instead of just the 1 modified CSO

**Why Unit Tests Didn't Catch It**:
- ✅ Unit tests verified repository queries worked correctly
- ✅ Unit tests verified watermark property existed
- ❌ Unit tests didn't verify Full Sync **sets** the watermark
- ❌ Unit tests didn't verify Delta Sync **uses** the watermark
- ❌ Unit tests didn't test the workflow: Full Sync -> Delta Sync

**Why Integration Tests Caught It**:
- ✅ Integration test ran Full Sync then Delta Sync in sequence
- ✅ Integration test measured actual performance (took forever)
- ❌ But integration tests are slow and expensive to run

**How Workflow Tests Would Have Caught It**:
- ✅ Workflow test would run Full Sync -> Delta Sync
- ✅ Workflow test would assert `ObjectsProcessed == 1`, not `100`
- ✅ Workflow test runs in seconds, not minutes
- ✅ Workflow test provides clear failure message

## Test Coverage Goals

| Category | Target Coverage | When to Run |
|----------|----------------|-------------|
| Unit Tests | 80%+ of business logic | On every commit (pre-commit hook) |
| Workflow Tests | Critical workflows (sync, provisioning, deletion) | On every commit |
| Integration Tests | Key scenarios (Scenarios 1-5) | On PR, nightly, before release |

## Best Practices

### Unit Tests
- **DO**: Test individual methods with mocks
- **DO**: Test edge cases and error conditions
- **DO**: Keep tests fast (< 100ms each)
- **DON'T**: Test framework code or external libraries
- **DON'T**: Test multiple components together (use Workflow Tests)

### Workflow Tests
- **DO**: Test multi-step business processes
- **DO**: Use real implementations (not mocks)
- **DO**: Test critical workflows (sync, export evaluation, deletion)
- **DO**: Assert on outcomes, not implementation details
- **DON'T**: Test UI or API layer (use Integration Tests)
- **DON'T**: Test with real external systems (use in-memory)

### Integration Tests
- **DO**: Test complete user scenarios
- **DO**: Use realistic data scales
- **DO**: Test with production-like configuration
- **DON'T**: Use for TDD (too slow)
- **DON'T**: Duplicate workflow test coverage

## Writing Workflow Tests

> **Implementation**: Workflow tests are now implemented in [`test/JIM.Worker.Tests/Workflows/`](../test/JIM.Worker.Tests/Workflows/):
> - [`WorkflowTestBase.cs`](../test/JIM.Worker.Tests/Workflows/WorkflowTestBase.cs) - Base class with in-memory database setup and helper methods
> - [`SyncWorkflowTests.cs`](../test/JIM.Worker.Tests/Workflows/SyncWorkflowTests.cs) - Tests for Full Sync -> Delta Sync workflows
>
> These tests caught the watermark bug and now ensure it doesn't regress.

### 1. Create Base Class

```csharp
public abstract class WorkflowTestBase
{
    protected JimDbContext DbContext;
    protected JimApplication Jim;

    [SetUp]
    public void BaseSetUp()
    {
        // In-memory database for fast, isolated tests
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        DbContext = new JimDbContext(options);
        Repository = new PostgresRepository(DbContext);
        Jim = new JimApplication(Repository);
    }

    // Helper methods for creating test data
    protected async Task<ConnectedSystem> CreateConnectedSystemAsync(...)
    protected async Task<ConnectedSystemObject> CreateCsoAsync(...)
    protected async Task ReloadEntityAsync<T>(T entity) {...}
}
```

### 2. Write Workflow Test

```csharp
[TestFixture]
public class SyncWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSyncThenDeltaSync_WithNoModifications_DeltaSyncProcessesZeroCsos()
    {
        // Arrange: Create test data
        var system = await CreateConnectedSystemAsync();
        for (int i = 0; i < 100; i++)
            await CreateCsoAsync(system.Id, $"user{i}");

        // Act: Run Full Sync
        var fullSync = new SyncFullSyncTaskProcessor(...);
        await fullSync.PerformFullSyncAsync();

        // Assert: Watermark set
        await ReloadEntityAsync(system);
        Assert.That(system.LastDeltaSyncCompletedAt, Is.Not.Null);

        // Act: Run Delta Sync (no changes)
        var deltaSync = new SyncDeltaSyncTaskProcessor(...);
        await deltaSync.PerformDeltaSyncAsync();

        // Assert: Processed 0 CSOs (not 100!)
        Assert.That(activity.ObjectsProcessed, Is.EqualTo(0));
    }
}
```

## Migration Path

1. ✅ Keep existing unit tests (they test individual components well)
2. ⚠️ Add workflow tests for critical business processes:
   - Delta Sync (Full -> Delta -> verify only modified CSOs processed)
   - Projection (CSO -> MVO -> verify join established)
   - Export Evaluation (MVO change -> PendingExport created)
   - Deletion Rules (Last connector disconnects -> MVO scheduled for deletion)
3. ✅ Keep integration tests for system-level scenarios

## ⚠️ CRITICAL: EF Core In-Memory Database Limitations ⚠️

> **This is a fundamental limitation that affects ALL unit and workflow tests using EF Core's in-memory provider.**

### The Problem: Navigation Property Auto-Tracking

EF Core's in-memory database provider **automatically tracks and loads navigation properties**, which masks bugs that would occur in production with PostgreSQL.

**In PostgreSQL (production):**
```csharp
// Without explicit .Include(), navigation properties are NULL
var cso = await context.ConnectedSystemObjects.FirstAsync(c => c.Id == id);
// cso.MetaverseObject is NULL ❌
// cso.MetaverseObject.Type is NULL ❌
```

**In EF Core In-Memory (tests):**
```csharp
// Navigation properties are auto-tracked and available WITHOUT .Include()
var cso = await context.ConnectedSystemObjects.FirstAsync(c => c.Id == id);
// cso.MetaverseObject is POPULATED ✅ (but shouldn't be!)
// cso.MetaverseObject.Type is POPULATED ✅ (but shouldn't be!)
```

### Real-World Example: Drift Detection Bug (January 2026)

**Bug**: `GetConnectedSystemObjectsModifiedSinceAsync()` was missing `.Include(cso => cso.MetaverseObject).ThenInclude(mvo => mvo.Type)`

**Result in Production**:
- `targetMvo.Type` was always `null`
- Export rule filter `r.MetaverseObjectTypeId == targetMvo.Type?.Id` always evaluated to `false`
- Drift detection found NO applicable export rules
- NO corrective pending exports were created
- **Scenario 8 integration test failed**

**Result in Unit/Workflow Tests**:
- All tests **PASSED** ✅
- The in-memory database auto-tracked `MVO.Type`
- The bug was completely invisible to our test suite

### Implications for Testing

| Test Type | Can Detect Missing `.Include()`? | Reliable for Navigation Properties? |
|-----------|----------------------------------|-------------------------------------|
| Unit Tests (mocked) | ❌ No - mocks return whatever you configure | ❌ No |
| Workflow Tests (in-memory) | ❌ No - auto-tracking masks the bug | ❌ No |
| Integration Tests (PostgreSQL) | ✅ Yes - real database behaviour | ✅ Yes |

### What This Means

1. **Unit and workflow tests CANNOT validate that repository queries load required navigation properties**
2. **Integration tests are the ONLY reliable way to verify `.Include()` chains are correct**
3. **Any bug involving missing navigation property loading will pass all unit/workflow tests**

### Defensive Measures

Because we cannot rely on unit/workflow tests to catch these bugs, we employ:

1. **Defensive Null Checks with Logging**: Add explicit null checks before using navigation properties, with warning logs that identify the missing Include:
   ```csharp
   if (targetMvo.Type == null)
   {
       Log.Warning("MVO {MvoId} has null Type - navigation property not loaded. " +
           "Ensure query includes MVO.Type", targetMvo.Id);
       return result;
   }
   ```

2. **Integration Test Coverage**: Critical code paths MUST have integration test coverage that exercises the actual PostgreSQL database

3. **Code Review Focus**: Pay special attention to:
   - New repository queries - verify all required `.Include()` chains
   - Code that accesses navigation properties - verify the query loads them
   - Changes to existing queries - verify no `.Include()` was accidentally removed

4. **Documentation in Code**: Add comments explaining why navigation properties are needed:
   ```csharp
   .Include(cso => cso.MetaverseObject)
       .ThenInclude(mvo => mvo!.Type) // Required for drift detection export rule filtering
   ```

### Summary

**Integration tests are the ONLY proof of correct navigation property loading.** Unit and workflow tests provide value for logic correctness, but they fundamentally cannot detect missing `.Include()` statements due to EF Core in-memory provider behaviour. Always run integration tests before releasing changes that modify repository queries.

---

## Summary

- **Unit Tests**: Fast, isolated, test individual components
- **Workflow Tests**: Moderate speed, test multi-component workflows, catch integration bugs
- **Integration Tests**: Slow, test full system, validate production-like behaviour

The three tiers complement each other. The watermark bug demonstrates why we need all three levels - unit tests verify components work individually, workflow tests verify they work together, and integration tests verify the complete system works at scale.

**⚠️ Critical Caveat**: Due to EF Core in-memory database limitations (see above), integration tests are the ONLY reliable way to verify navigation property loading. Unit and workflow tests will PASS even when `.Include()` statements are missing.

**Action Item**: Implement workflow test infrastructure and add tests for critical sync workflows to prevent future orchestration bugs.
