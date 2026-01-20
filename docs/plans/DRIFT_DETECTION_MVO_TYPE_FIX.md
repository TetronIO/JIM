# Drift Detection MVO Type Loading Fix

**Status:** Planned
**Created:** 2026-01-19
**Related Issue:** Scenario 8 drift detection fails - no corrective pending exports created

## Problem Summary

Drift detection fails to create corrective pending exports because the MVO's `Type` navigation property is not loaded when CSOs are fetched during delta sync. This causes the export rule filter to incorrectly exclude all rules.

### Root Cause

In `ConnectedSystemRepository.GetConnectedSystemObjectsModifiedSinceAsync()`, the query includes:
- `cso.MetaverseObject.AttributeValues.Attribute`
- `cso.MetaverseObject.AttributeValues.ReferenceValue`

But it does NOT include:
- `cso.MetaverseObject.Type` ← **MISSING**

In `DriftDetectionService.EvaluateDriftAsync()` at line 125:
```csharp
var applicableExportRules = exportRules
    .Where(r => r.EnforceState &&
               r.ConnectedSystemId == cso.ConnectedSystemId &&
               r.ConnectedSystemObjectTypeId == cso.TypeId &&
               r.MetaverseObjectTypeId == targetMvo.Type?.Id)  // ← targetMvo.Type is null!
    .ToList();
```

Since `targetMvo.Type` is null, `targetMvo.Type?.Id` returns null. The comparison `int == null` always returns false, so `applicableExportRules` is always empty.

### Impact

- Drift detection never finds applicable export rules
- No corrective pending exports are created
- Unauthorised changes in target systems are not remediated
- Scenario 8 integration test fails at the DetectDrift step

## Proposed Fix (TDD Approach)

### Phase 1: Unit Tests (RED)

**File:** `test/JIM.Worker.Tests/OutboundSync/DriftDetectionTests.cs` (new or existing)

Write failing tests that verify the expected behaviour:

1. `EvaluateDriftAsync_WhenMvoTypeIsNull_LogsWarningAndReturnsEmptyResult`
2. `EvaluateDriftAsync_WhenMvoTypeIsLoaded_FindsApplicableExportRules`
3. `EvaluateDriftAsync_WithMultiValuedAttribute_DetectsDriftAndCreatesCorrectiveExport`

These tests will initially fail because:
- Test 1: No defensive check exists yet
- Test 2: Will pass if MVO Type is manually set in test setup
- Test 3: Should pass if properly configured (validates existing logic)

### Phase 2: Workflow Tests (RED)

**File:** `test/JIM.Workflow.Tests/Scenarios/Sync/DriftDetectionWorkflowTests.cs`

Add or update tests to verify:

1. **MVO Type loading verification** - Assert that after loading CSOs via the repository, `cso.MetaverseObject.Type` is not null. This test will FAIL initially because the repository doesn't load the Type.

2. **End-to-end delta sync drift detection** - Test that delta sync on a target system with export rules correctly detects drift and creates pending exports. This test will FAIL initially.

3. **Multi-valued attribute drift correction** - Test drift on `member` attribute (group membership) creates atomic Add/Remove changes.

Example test structure:
```csharp
[Test]
public async Task DeltaSync_WithDriftedGroupMembership_CreatesCorrectivePendingExportsAsync()
{
    // Arrange: Set up source/target systems with export rules
    // Create MVO with group membership
    // Create Target CSO joined to MVO
    // Simulate drift: Add unauthorised member to Target CSO

    // Act: Run delta sync on Target system

    // Assert:
    // 1. CSO.MetaverseObject.Type is loaded (not null)
    // 2. Drift detection found applicable export rules
    // 3. Corrective pending exports were created
    // 4. Pending export has correct Add/Remove changes
}
```

### Phase 3: Repository Fix (GREEN)

**File:** `JIM.PostgresData/Repositories/ConnectedSystemRepository.cs`

Add the missing Include for MVO Type in `GetConnectedSystemObjectsModifiedSinceAsync()`:

```csharp
var query = Repository.Database.ConnectedSystemObjects
    .AsSplitQuery()
    .Include(cso => cso.Type)
    .Include(cso => cso.AttributeValues)
        .ThenInclude(av => av.Attribute)
    .Include(cso => cso.AttributeValues)
        .ThenInclude(av => av.ReferenceValue)
        .ThenInclude(rv => rv!.MetaverseObject)
    .Include(cso => cso.MetaverseObject)
        .ThenInclude(mvo => mvo!.Type)  // ← ADD THIS
    .Include(cso => cso.MetaverseObject)
        .ThenInclude(mvo => mvo!.AttributeValues)
        .ThenInclude(av => av.Attribute)
    .Include(cso => cso.MetaverseObject)
        .ThenInclude(mvo => mvo!.AttributeValues)
        .ThenInclude(av => av.ReferenceValue)
    // ... rest of query
```

### Phase 4: Defensive Code Improvement (GREEN)

**File:** `JIM.Application/Services/DriftDetectionService.cs`

Add logging when MVO Type is null to aid future debugging:

```csharp
// Use the provided MVO or fall back to the CSO's joined MVO
var targetMvo = mvo ?? cso.MetaverseObject!;

// Defensive check: ensure MVO Type is loaded
if (targetMvo.Type == null)
{
    Log.Warning("EvaluateDriftAsync: MVO {MvoId} has null Type - navigation property not loaded. " +
        "Drift detection cannot filter by MVO type. CSO: {CsoId}", targetMvo.Id, cso.Id);
    return result;
}
```

### Phase 5: Verify Tests Pass (GREEN)

Run all tests to confirm:
1. Unit tests pass
2. Workflow tests pass
3. Build succeeds

```bash
dotnet build JIM.sln && dotnet test JIM.sln
```

### Phase 6: Integration Test Verification

After implementing the fix, verify:

1. Run `./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario8-CrossDomainEntitlementSync -Template Micro`
2. Confirm DetectDrift step passes
3. Confirm pending exports are created for drift correction
4. Confirm ReassertState step successfully exports corrections

## Test Coverage Gaps Identified

### Gap 1: Repository Navigation Property Loading

**Issue:** No unit tests verify that `GetConnectedSystemObjectsModifiedSinceAsync` loads all required navigation properties.

**Recommendation:** Add repository integration tests using an in-memory database that verify:
- `cso.Type` is loaded
- `cso.MetaverseObject` is loaded
- `cso.MetaverseObject.Type` is loaded
- `cso.MetaverseObject.AttributeValues` is loaded
- `cso.MetaverseObject.AttributeValues[n].Attribute` is loaded

### Gap 2: Drift Detection Export Rule Filtering

**Issue:** Existing drift detection tests don't verify that export rules are correctly filtered by MVO type.

**Recommendation:** Add unit tests for `EvaluateDriftAsync` that verify:
- Rules are filtered correctly when all criteria match
- Rules are excluded when MVO type doesn't match
- Rules are excluded when CSO type doesn't match
- Rules are excluded when EnforceState is false

### Gap 3: End-to-End Delta Sync Drift Path

**Issue:** Workflow tests test `DriftDetectionService` directly but don't test the full code path through `SyncDeltaSyncTaskProcessor`.

**Recommendation:** Add workflow tests that:
- Use `ExecuteDeltaSyncAsync` (not direct service calls)
- Verify drift detection is triggered during delta sync
- Verify pending exports are created in the database

### Gap 4: Multi-Valued Attribute Drift Correction

**Issue:** Tests marked `[Explicit]` for multi-valued attribute handling aren't run automatically.

**Recommendation:**
- Implement proper multi-valued flow infrastructure in test harness
- Enable the `[Explicit]` tests for group membership drift detection
- Add specific tests for Add vs Remove change types in corrective exports

## Success Criteria

1. **Repository fix verified:** `GetConnectedSystemObjectsModifiedSinceAsync` loads `cso.MetaverseObject.Type`
2. **Drift detection works:** `EvaluateDriftAsync` finds applicable export rules when MVO Type is loaded
3. **Unit tests pass:** New tests verify navigation property loading and export rule filtering
4. **Workflow tests pass:** End-to-end delta sync drift detection tests pass
5. **Integration test passes:** Scenario 8 DetectDrift step completes successfully
6. **Build and all tests pass:** `dotnet build JIM.sln && dotnet test JIM.sln`

## Files to Modify

1. `JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` - Add MVO Type include
2. `JIM.Application/Services/DriftDetectionService.cs` - Add defensive null check with logging
3. `test/JIM.Worker.Tests/OutboundSync/DriftDetectionTests.cs` - Add unit tests
4. `test/JIM.Workflow.Tests/Scenarios/Sync/DriftDetectionWorkflowTests.cs` - Add workflow tests

## Risk Assessment

**Low Risk:** The fix is a simple addition of an Include clause that was clearly omitted. The defensive check adds robustness without changing behaviour when data is properly loaded.

**Testing:** The fix should be verified with:
1. Unit tests (new)
2. Existing workflow tests (should continue to pass)
3. Scenario 8 integration test (should now pass)
