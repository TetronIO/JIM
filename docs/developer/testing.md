---
title: Testing
---

# Testing

JIM follows Test-Driven Development (TDD) as the standard development practice. Tests are written **before** the implementation they cover, not after.

## TDD Workflow

The Red, Green, Refactor cycle:

1. **Red:** Write a failing test that describes the expected behaviour. Run it and confirm it fails (not just fails to compile; it must execute and fail the assertion).
2. **Green:** Write the minimum production code needed to make the test pass. Run the test and confirm it passes.
3. **Refactor:** Clean up the implementation and tests without breaking anything.

### Bug Fix Workflow

1. Write a test that **reproduces the bug**; it must fail before any fix is applied
2. Implement the fix
3. Run the test; it must now pass
4. Commit both the test and the fix together

A test written after a fix cannot prove the fix was necessary; it could pass even on the broken code. The failing test is the evidence that the fix works.

## Test Framework

| Tool | Purpose |
|------|---------|
| **NUnit** | Test framework (`[Test]` attribute) |
| **Moq** | Mocking framework |
| **coverlet** | Code coverage |
| **Assert.That()** | Constraint-based assertion syntax |

### Naming Convention

```csharp
[Test]
public void MethodName_Scenario_ExpectedResult() { }
```

Examples:

```csharp
[Test]
public void GetObjectAsync_WithValidId_ReturnsObject() { }

[Test]
public void GetObjectAsync_WithInvalidId_ReturnsNull() { }
```

## Running Tests

### All Tests

```bash
dotnet test JIM.sln
```

Or use the shell alias:

```bash
jim-test       # Unit + workflow tests (excludes Explicit)
jim-test-all   # All tests including Explicit and Pester
```

### Targeted Tests

During development, prefer running only the test projects that cover your changes:

```bash
dotnet test test/JIM.Worker.Tests/
dotnet test test/JIM.Web.Api.Tests/
```

### Specific Tests

```bash
dotnet test --filter "FullyQualifiedName~TestName"
```

!!! tip
    Use targeted tests during development and reserve `dotnet test JIM.sln` for the final pre-PR check.

## Test Project Locations

| Project | Covers |
|---------|--------|
| `test/JIM.Web.Api.Tests/` | API controllers |
| `test/JIM.Models.Tests/` | Models and DTOs |
| `test/JIM.Worker.Tests/` | Worker, sync processors, business logic |
| `test/JIM.Workflow.Tests/` | Multi-step workflow scenarios |

## Unit Tests

Unit tests cover business logic in `JIM.Application` servers.

- Mock dependencies using Moq
- Use `MockQueryable` for EF Core query testing
- Aim for >70% code coverage on core logic

```csharp
[Test]
public async Task CreateObjectAsync_WithValidData_ReturnsNewObject()
{
    // Arrange
    var mockRepo = new Mock<IRepository>();
    mockRepo.Setup(r => r.Metaverse.CreateObjectAsync(It.IsAny<MetaverseObject>()))
        .ReturnsAsync(new MetaverseObject { Id = Guid.NewGuid() });

    var server = new MetaverseServer(mockRepo.Object);

    // Act
    var result = await server.CreateObjectAsync(new MetaverseObject());

    // Assert
    Assert.That(result, Is.Not.Null);
    Assert.That(result.Id, Is.Not.EqualTo(Guid.Empty));
}
```

## Worker Tests

Worker tests cover synchronisation processors with mocked dependencies.

- Located in `test/JIM.Worker.Tests/`
- Use `MockFileConnector` for file-based import scenarios
- Test the `ISyncEngine` methods directly (they are stateless and require no mocks)

## Workflow Tests

Workflow tests sit between unit tests and integration tests. They test multi-step sync scenarios using real business logic with mock connectors and an in-memory database.

### Key Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `WorkflowTestHarness` | `test/JIM.Workflow.Tests/` | Orchestrates multi-step test execution |
| `WorkflowStateSnapshot` | `test/JIM.Workflow.Tests/` | Captures MVO, CSO, and PendingExport state after each step |
| `MockCallConnector` | `src/JIM.Connectors/Mock/` | Call-based mock connector for test scenarios |

### Benefits

- **Fast execution:** seconds rather than minutes for integration tests
- **State snapshots:** capture state after each step for diagnostics
- **Reproducible:** configurable fake data with no external dependencies
- **Self-contained:** no LDAP, Active Directory, or other external systems required

### Example

```csharp
[Test]
public async Task ProvisioningWorkflow_CompleteCycle_SucceedsAsync()
{
    // Setup systems and Synchronisation Rules
    await SetUpProvisioningScenarioAsync(objectCount: 100);

    // Execute import
    _harness.GetConnector("HR").QueueImportObjects(GenerateUsers(100));
    await _harness.ExecuteFullImportAsync("HR");
    var afterImport = await _harness.TakeSnapshotAsync("After Import");

    Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(100));

    // Execute sync and export evaluation
    await _harness.ExecuteFullSyncAsync("HR");
    await _harness.ExecuteExportEvaluationAsync("HR");
    var afterExportEval = await _harness.TakeSnapshotAsync("After Export Eval");

    // Verify PendingExports have CSO foreign keys
    Assert.That(afterExportEval.GetPendingExportsWithNullCsoFk(), Is.Empty);
}
```

### Running Workflow Tests

```bash
# All workflow tests
dotnet test test/JIM.Workflow.Tests/

# Explicit tests (tests for known bugs)
dotnet test test/JIM.Workflow.Tests/ --filter "TestCategory=Explicit"
```

## Integration Tests

Integration tests run against a real PostgreSQL database to verify repository implementations and migrations.

- Use test containers or a dedicated test database
- Verify EF Core migrations work correctly
- Run the integration test runner script: `./test/integration/Run-IntegrationTests.ps1`

!!! warning "EF Core in-memory tracking"
    EF Core's in-memory database auto-tracks navigation properties, which can mask missing `.Include()` calls in queries. Always run integration tests when modifying repository queries to catch these issues.

!!! note
    The integration test runner handles setup, environment management, and teardown automatically. Do not invoke scenario scripts directly.

### Active Directory lab

Everything above runs against containers. The LDAP Connector treats Samba AD as a different directory type from Active Directory, so the `ActiveDirectory` code paths are covered by a separate lab of real Windows Server domain controllers (three single-domain-controller forests on Hyper-V, each reverted to a checkpoint before a scenario runs). Two things run on it. The integration scenarios run with `./test/integration/Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory`: every scenario that applies to Active Directory, plus Scenarios 24 (the domain password policy and a Fine-Grained Password Policy) and 25 (Delta Import with the Recycle Bin on and off, and restore from backup), which run there only and are skipped by every other directory type, `-DirectoryType All` and `-PreRelease` included. The `RequiresActiveDirectory` NUnit probe tier in `test/JIM.Worker.Tests/` drives the connector's own code paths against a domain controller directly, and skips itself unless `JIM_TEST_AD_HOST` is set. Neither runs on pull requests or in a devcontainer: they run on the lab's own runner, nightly and on dispatch (`.github/workflows/ad-lab.yml`), and a release needs the `jim-ad-lab` commit status that run posts. See `engineering/INTEGRATION_TESTING.md` (Scenarios 024 and 025), `engineering/TESTING_STRATEGY.md` (Active Directory Lab Probes) and `test/integration/ad-lab/README.md` (how the lab is built and reverted).
