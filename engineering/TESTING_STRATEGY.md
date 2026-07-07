# Testing Strategy

## Overview

JIM employs a four-tier testing approach to ensure quality at different levels of the application:

```
Integration Tests (Full System, Docker stack + external directories)
         ^
Database-Backed Component Tests (real PostgreSQL, .NET test host)
         ^
   Workflow Tests (Multi-Component, in-memory)
         ^
    Unit Tests (Single Component, mocked)
```

The middle two tiers both run under `dotnet test`; they differ in the database provider. Workflow tests use the EF Core in-memory provider (fast, no infrastructure), while Database-Backed Component tests run against a real PostgreSQL instance so they catch provider-specific and raw-SQL behaviour the in-memory provider cannot reproduce (see tier 3 and the in-memory limitation section below).

## 1. Unit Tests

**Location**: `test/JIM.Worker.Tests/`, `test/JIM.Models.Tests/`, `test/JIM.Web.Api.Tests/`, `test/JIM.Utilities.Tests/`

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

## 2. Workflow Tests

**Location**: Two complementary test suites:
- `test/JIM.Worker.Tests/Workflows/`: Lower-level workflow tests using `WorkflowTestBase` (47 tests)
- `test/JIM.Workflow.Tests/Scenarios/`: Higher-level scenario tests using `WorkflowTestHarness` (40 tests)

**Purpose**: Test multi-step business processes using real implementations with in-memory database

**Characteristics**:
- Moderate execution speed (seconds per test)
- Use in-memory EF Core database (no Docker required)
- Test multiple components working together
- Focus on workflow correctness and component integration

**Current Coverage**:

| Test File | Tests | Area |
|-----------|------:|------|
| `Scenarios/Entitlement Management/CrossRunReferenceResolutionTests.cs` | 2 | Cross-run reference resolution |
| `Scenarios/Entitlement Management/GroupMembershipSyncTests.cs` | 2 | Group membership sync |
| `Scenarios/Joiners/ProvisioningWorkflowTests.cs` | 4 | Joiner provisioning |
| `Scenarios/Sync/FullSyncAfterImportWorkflowTests.cs` | 2 | Import → Full Sync |
| `Scenarios/Sync/DeltaSyncAfterImportWorkflowTests.cs` | 2 | Import → Delta Sync |
| `Scenarios/Sync/DriftDetectionWorkflowTests.cs` | 12 | Export drift detection |
| `Scenarios/Sync/NoNetChangeWorkflowTests.cs` | 12 | No-change optimisation |
| `Scenarios/Sync/NonStringDataTypeExportTests.cs` | 4 | Non-string data type exports |

Total: 40 tests.

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

**Two Patterns**:

1. **`WorkflowTestBase`** (`test/JIM.Worker.Tests/Workflows/`): Abstract base class with in-memory database and helper methods for creating test data (Connected Systems, CSOs, Synchronisation Rules, Run Profiles). Best for focused tests on specific processor logic.

2. **`WorkflowTestHarness`** (`test/JIM.Workflow.Tests/Harness/`): Higher-level harness that orchestrates complete sync cycles (import → sync → export → confirming import) with state snapshots between steps. Uses `MockCallConnector` to simulate external systems. Best for end-to-end scenario tests.

**Guidelines**:
- Use in-memory EF Core database: `UseInMemoryDatabase(Guid.NewGuid().ToString())`
- Test name format: `<Scenario>_<Action>_<ExpectedOutcome>`
- Keep workflows focused (test one business process per test)
- Choose `WorkflowTestBase` for processor-level tests, `WorkflowTestHarness` for full-cycle scenarios

## 3. Database-Backed Component Tests

**Location**: `test/JIM.Worker.Tests/Servers/*DatabaseTests.cs`

**Purpose**: Verify repository and server behaviour against a **real PostgreSQL** database, in the .NET test host, with no other external systems. This tier exists to catch the class of bug the EF Core in-memory provider structurally hides: provider-specific behaviour (query-tracking semantics, shadow foreign keys) and hand-written raw SQL the in-memory provider cannot execute.

**Characteristics**:
- Fast (the Predefined Search suite runs in ~30s); much faster than the Docker Integration tier
- Real PostgreSQL, not in-memory: matches the production provider and the production `NoTracking` default
- No Docker stack, no Samba AD / OpenLDAP / SCIM, not driven by the PowerShell integration runner
- Each fixture migrates the schema once (`[OneTimeSetUp]`) and `TRUNCATE`s every table between tests (`[SetUp]`), so it needs a **dedicated throwaway database** it may freely wipe

**Gating**: Every fixture carries `[Category("RequiresPostgres")]` and, in `[OneTimeSetUp]`, calls `Assert.Ignore` unless `JIM_TEST_RESET_DB` is set. So a normal `dotnet test` / `jim-test` run (in-memory tiers only) skips them, and they run only when explicitly pointed at a throwaway database.

**Configuration (environment variables)**:

| Variable | Purpose | Default |
|----------|---------|---------|
| `JIM_TEST_RESET_DB` | Name of the throwaway database; **also the opt-in switch** (unset = skip) | (none; required to run) |
| `JIM_TEST_RESET_HOST` | PostgreSQL host | `localhost` |
| `JIM_TEST_RESET_PORT` | PostgreSQL port | `5432` |
| `JIM_TEST_RESET_USER` | Username | `postgres` |
| `JIM_TEST_RESET_PASSWORD` | Password | `postgres` |

**When to write a Database-Backed Component test**:
- The behaviour depends on the real provider: a write path whose persistence turns on tracking vs. no-tracking, a query using raw SQL, a shadow-FK relationship walk
- A workflow/unit test would pass against in-memory but cannot prove the production database behaves the same (the trigger for #849/#850: creating a Predefined Search Criteria Group was a silent no-op that every in-memory test passed)
- You are adding repository/server coverage that asserts against real SQL rather than orchestrating a full sync cycle (that is the Workflow tier) or the whole stack (Integration tier)

**Running locally**:

Use the `jim-test-db` alias, which spins up a disposable PostgreSQL container on port 5433 (so it never collides with your dev `jim-db` on 5432), runs the tier, and tears the container down:

```bash
jim-test-db
```

Or point the tier at any throwaway database yourself:

```bash
JIM_TEST_RESET_DB=jim_test JIM_TEST_RESET_HOST=localhost JIM_TEST_RESET_PORT=5432 \
  JIM_TEST_RESET_USER=postgres JIM_TEST_RESET_PASSWORD=postgres \
  dotnet test JIM.sln --filter "Category=RequiresPostgres"
```

**Running in CI**: The `database-tests` job in `.github/workflows/ci.yml` stands up a PostgreSQL service container, points `JIM_TEST_RESET_*` at a throwaway `jim_test` database, and runs `dotnet test JIM.sln --filter "Category=RequiresPostgres"` on every PR. It runs alongside the in-memory `build-and-test` job, so the two tiers give independent feedback and a failure here blocks the PR.

**What Database-Backed Component Tests Are Good At**:
- ✅ Catching persistence bugs the in-memory provider's auto-tracking masks (silent no-op writes)
- ✅ Exercising hand-written raw PostgreSQL (query translators, criteria group All/Any semantics)
- ✅ Verifying shadow foreign keys and parent-chain walks against real columns

**What Database-Backed Component Tests Miss**:
- ❌ End-to-end system behaviour with real directories/connectors (that is the Integration tier)
- ❌ UI and API-surface behaviour
- ❌ Performance at scale

## 4. Integration Tests

**Location**: `test/integration/`

**Purpose**: Test the entire system end-to-end with external dependencies

**Characteristics**:
- Slow execution (minutes per test)
- Uses Docker containers (PostgreSQL, Samba AD, etc.)
- Tests full stack including API, database, connectors
- Focus on system-level correctness and realistic scenarios

**Example**: HR to Directory synchronisation scenario

```powershell
./Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -Template Large
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

**How Workflow Tests Caught It**:
- ✅ `SyncWorkflowTests` runs Full Sync -> Delta Sync
- ✅ Asserts `ObjectsProcessed == 1`, not `100`
- ✅ Runs in seconds, not minutes
- ✅ Provides clear failure message
- ✅ Now prevents regression on every commit

## Test Coverage Goals

| Category | Target Coverage | When to Run |
|----------|----------------|-------------|
| Unit Tests | 80%+ of business logic | On every commit (pre-commit hook) |
| Workflow Tests | Critical workflows (sync, provisioning, deletion) | On every commit |
| Database-Backed Component Tests | Provider-specific / raw-SQL repository behaviour | On every PR (CI `database-tests` job); locally via `jim-test-db` |
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

### Pattern 1: WorkflowTestBase (Processor-Level Tests)

Extend `WorkflowTestBase` in `test/JIM.Worker.Tests/Workflows/` for tests that exercise individual processors with real database state.

```csharp
[TestFixture]
public class SyncWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task DeltaSync_WithNoModifications_ProcessesZeroCsosAsync()
    {
        // Arrange: Create test data using base class helpers
        var system = await CreateConnectedSystemAsync();
        var csoType = await CreateCsoTypeAsync(system.Id);
        await CreateCsosAsync(system.Id, csoType.Id, count: 100);

        // Act: Run Full Sync then Delta Sync
        // ... processor setup and execution ...

        // Assert: Processed 0 CSOs (not 100!)
        Assert.That(activity.ObjectsProcessed, Is.EqualTo(0));
    }
}
```

### Pattern 2: WorkflowTestHarness (Scenario Tests)

Use `WorkflowTestHarness` in `test/JIM.Workflow.Tests/Scenarios/` for end-to-end scenario tests that orchestrate full sync cycles with mock connectors.

```csharp
[TestFixture]
public class FullSyncAfterImportWorkflowTests
{
    [Test]
    public async Task FullImportThenSync_WithNewObjects_ProjectsToMetaverseAsync()
    {
        using var harness = new WorkflowTestHarness();

        // Setup: Create Connected System with mock connector
        await harness.CreateConnectedSystemAsync("HR", ...);

        // Execute: Import -> Sync cycle
        await harness.ExecuteFullImportAsync("HR");
        await harness.ExecuteFullSyncAsync("HR");

        // Assert: Objects projected to metaverse
        var snapshot = harness.LatestSnapshot;
        Assert.That(snapshot.MetaverseObjects, Has.Count.GreaterThan(0));
    }
}
```

## Workflow Coverage Status

| Business Process | Status | Test Location |
|-----------------|--------|---------------|
| Delta Sync (watermark) | ✅ Done | `JIM.Worker.Tests/Workflows/SyncWorkflowTests.cs` |
| Deletion Rules | ✅ Done | `JIM.Worker.Tests/Workflows/DeletionRuleWorkflowTests.cs` |
| Export Confirmation | ✅ Done | `JIM.Worker.Tests/Workflows/ExportConfirmationWorkflowTests.cs` |
| Attribute Recall / Expressions | ✅ Done | `JIM.Worker.Tests/Workflows/AttributeRecallExpressionWorkflowTests.cs` |
| Provisioning (Joiners) | ✅ Done | `JIM.Workflow.Tests/Scenarios/Joiners/ProvisioningWorkflowTests.cs` |
| Drift Detection | ✅ Done | `JIM.Workflow.Tests/Scenarios/Sync/DriftDetectionWorkflowTests.cs` |
| No Net Change Optimisation | ✅ Done | `JIM.Workflow.Tests/Scenarios/Sync/NoNetChangeWorkflowTests.cs` |
| Full Import → Sync | ✅ Done | `JIM.Workflow.Tests/Scenarios/Sync/FullSyncAfterImportWorkflowTests.cs` |
| Delta Import → Sync | ✅ Done | `JIM.Workflow.Tests/Scenarios/Sync/DeltaSyncAfterImportWorkflowTests.cs` |
| Group Membership Sync | ✅ Done | `JIM.Workflow.Tests/Scenarios/Entitlement Management/GroupMembershipSyncTests.cs` |
| Non-String Data Types | ✅ Done | `JIM.Workflow.Tests/Scenarios/Sync/NonStringDataTypeExportTests.cs` |
| Projection (CSO -> MVO join) | ⚠️ Partial | Covered implicitly by sync tests; no dedicated test |
| Export Evaluation (PendingExport) | ✅ Done | 67 dedicated unit tests in `JIM.Worker.Tests/OutboundSync/` (`ExportEvaluationTests.cs`, `ExportEvaluationMergeTests.cs`, `ExportEvaluationNoChangeTests.cs`) |

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
- NO corrective Pending Exports were created
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
| Database-Backed Component Tests (PostgreSQL) | ✅ Yes - real database behaviour | ✅ Yes |
| Integration Tests (PostgreSQL) | ✅ Yes - real database behaviour | ✅ Yes |

### What This Means

1. **Unit and workflow tests CANNOT validate that repository queries load required navigation properties**
2. **Only real-PostgreSQL tests can verify `.Include()` chains are correct**; this means the Database-Backed Component tier (tier 3) or the Integration tier (tier 4). The Database-Backed tier is the cheaper of the two and runs on every PR, so reach for it first
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

**Only real-PostgreSQL tests prove correct navigation property loading** (the Database-Backed Component tier or the Integration tier). Unit and workflow tests provide value for logic correctness, but they fundamentally cannot detect missing `.Include()` statements due to EF Core in-memory provider behaviour. Always run a real-PostgreSQL tier before releasing changes that modify repository queries.

---

## Summary

- **Unit Tests**: Fast, isolated, test individual components (mocked)
- **Workflow Tests**: Moderate speed, test multi-component workflows in-memory, catch integration bugs
- **Database-Backed Component Tests**: Real PostgreSQL in the .NET test host, catch provider-specific and raw-SQL bugs the in-memory provider hides; fast enough to run on every PR
- **Integration Tests**: Slow, test full system with Docker + external directories, validate production-like behaviour

The four tiers complement each other. The watermark bug demonstrates why unit and workflow tests are not enough on their own; the Predefined Search silent-no-op bug (#849/#850) demonstrates why a real-PostgreSQL tier below the heavy Integration stack is worth having - it caught a persistence bug in ~30s that every in-memory test passed.

**⚠️ Critical Caveat**: Due to EF Core in-memory database limitations (see above), a real-PostgreSQL tier (Database-Backed Component or Integration) is the only reliable way to verify navigation property loading. Unit and workflow tests will PASS even when `.Include()` statements are missing.
