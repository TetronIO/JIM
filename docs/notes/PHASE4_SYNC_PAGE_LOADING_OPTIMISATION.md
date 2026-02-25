# Phase 4: Sync Page Loading Optimisation — AsSplitQuery Elimination

- **GitHub Issue**: [#338](https://github.com/TetronIO/JIM/issues/338) (Phase 4)
- **Branch**: `feature/338-worker-db-performance-phase4`
- **Status**: Complete
- **Created**: 2026-02-25
- **Related**: `docs/notes/DRIFT_DETECTION_SPURIOUS_MEMBER_REMOVAL.md` (the AsSplitQuery bug RCA)
- **Plan**: `docs/plans/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md`

---

## Objective

Replace the deep `AsSplitQuery()` + `Include`/`ThenInclude` chains in the three sync page-load
methods with explicit, separate EF Core queries wrapped in a serialisable transaction. This:

1. **Eliminates the AsSplitQuery materialisation bug** (dotnet/efcore#33826) at source
2. **Removes ~200 lines of post-load repair code** that patch up the bug's effects
3. **Removes scalar FK fallback workarounds** scattered across the application layer
4. **Restores clean n-tier architecture** — the repository returns complete, correct data; callers don't compensate

---

## Background: The Problem

### AsSplitQuery Materialisation Bug

EF Core's `AsSplitQuery()` splits a query with multiple `Include` chains into separate SQL queries
and merges results in memory. There is a documented bug ([dotnet/efcore#33826](https://github.com/dotnet/efcore/issues/33826))
where concurrent writes between split query executions cause navigation properties to fail to
materialise — they come back null despite the data existing in the database.

This manifests at scale (200+ member references per group) and caused:
- Drift detection seeing incomplete member lists → spurious DELETE exports
- Import reference matching failing → removing valid resolved references
- MVO attribute values being dropped from collections

### The Tactical Fix (7 Layers)

Three commits (71c932b1, 19267b2f, 0910cb4a) applied a 7-layer fix across 11 files:

| Layer | Location | What It Does |
|-------|----------|--------------|
| 1 | `ConnectedSystemRepository.RepairReferenceValueMaterialisationAsync` | Post-load SQL to patch null CSO ReferenceValue navigations |
| 2 | `DriftDetectionService.GetTypedValueFromMvoAttributeValue` | Prefer scalar FK `av.ReferenceValueId` over navigation `av.ReferenceValue?.Id` |
| 3 | `SyncImportTaskProcessor.ImportRefMatchesCsoValue` | Pre-loaded SQL dictionary fallback when ReferenceValue is null |
| 4 | `ConnectedSystemRepository.RepairMvoAttributeValueMaterialisationAsync` | Post-load SQL to patch missing MVO AttributeValue rows |
| 5 | `SyncRuleMappingProcessor.ProcessReferenceAttribute` | Scalar FK helpers (`GetReferencedMvoId`, `IsResolved`) |
| 6 | `ExportEvaluationServer.CreateAttributeValueChanges` | `mvoValue.ReferenceValue?.Id ?? mvoValue.ReferenceValueId` fallback |
| 7 | `MetaverseRepository.UpdateMetaverseObjectsAsync` | Explicit `Entry().State` management for new attribute values |

These fixes are correct and production-proven, but they leak implementation concerns (EF query bugs)
into the application/service layer, creating code smell and fragility.

---

## Approach: Serialisable Transaction with Separate EF Queries

### Why Not Raw SQL?

The full raw-SQL approach (hand-map every column, manually stitch entity graphs in C#) would be a
massive change with high regression risk. The sync pipeline expects real EF-tracked entities with
working navigation properties, not DTOs.

### The Approach

Instead of `AsSplitQuery()` which runs multiple queries **without** a shared snapshot, we run the
**same queries** but wrapped in a **serialisable/repeatable-read transaction**, ensuring a consistent
snapshot across all queries. This eliminates the race condition that causes the materialisation bug.

```
BEFORE (AsSplitQuery):
  Query 1: Load CSOs + AttributeValues         ──┐
  [concurrent write happens here]                │  No shared snapshot
  Query 2: Load ReferenceValue navigations     ──┘  → materialisation failures

AFTER (Explicit transaction):
  BEGIN TRANSACTION (REPEATABLE READ)
  Query 1: Load CSOs with basic includes       ──┐
  Query 2: Load MVO data for those CSOs        ──│  Shared snapshot
  Query 3: Load reference data for those CSOs  ──┘  → consistent data
  COMMIT
```

### Specific Change: Three Page-Load Methods

All three methods share an identical Include chain. The change applies to:

1. **`GetConnectedSystemObjectsAsync`** — full sync page load
2. **`GetConnectedSystemObjectsModifiedSinceAsync`** — delta sync page load
3. **`GetConnectedSystemObjectsForReferenceResolutionAsync`** — cross-page reference resolution

Each will be refactored from:
```csharp
// BEFORE: Single query with deep AsSplitQuery Include chain + post-load repairs
var query = Repository.Database.ConnectedSystemObjects
    .AsSplitQuery()
    .Include(cso => cso.Type)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.ReferenceValue)
        .ThenInclude(rv => rv!.MetaverseObject)
    .Include(cso => cso.MetaverseObject).ThenInclude(mvo => mvo!.Type)
    .Include(cso => cso.MetaverseObject).ThenInclude(mvo => mvo!.AttributeValues)
        .ThenInclude(av => av.Attribute)
    .Include(cso => cso.MetaverseObject).ThenInclude(mvo => mvo!.AttributeValues)
        .ThenInclude(av => av.ReferenceValue)
    .Include(cso => cso.MetaverseObject).ThenInclude(mvo => mvo!.AttributeValues)
        .ThenInclude(av => av.ContributedBySystem)
    .Where(...)
    .ToListAsync();

await RepairReferenceValueMaterialisationAsync(results);
await RepairMvoAttributeValueMaterialisationAsync(results);
```

To:
```csharp
// AFTER: Explicit transaction with two separate queries — no post-load repairs needed
await using var transaction = await Repository.Database.Database
    .BeginTransactionAsync(IsolationLevel.RepeatableRead);

try
{
    // Query 1: Load CSOs with their own attribute values and references
    var results = await Repository.Database.ConnectedSystemObjects
        .Include(cso => cso.Type)
        .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.Attribute)
        .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.MetaverseObject)
        .Where(...)
        .ToListAsync();

    // Query 2: Load MVOs with their attribute values (EF auto-fixes navigation properties
    // for entities already tracked by the same DbContext)
    var mvoIds = results
        .Where(cso => cso.MetaverseObjectId != null)
        .Select(cso => cso.MetaverseObjectId!.Value)
        .Distinct()
        .ToList();

    if (mvoIds.Count > 0)
    {
        await Repository.Database.MetaverseObjects
            .Include(mvo => mvo.Type)
            .Include(mvo => mvo.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .Include(mvo => mvo.AttributeValues)
                .ThenInclude(av => av.ReferenceValue)
            .Include(mvo => mvo.AttributeValues)
                .ThenInclude(av => av.ContributedBySystem)
            .Where(mvo => mvoIds.Contains(mvo.Id))
            .ToListAsync();
    }

    await transaction.CommitAsync();
}
catch
{
    await transaction.RollbackAsync();
    throw;
}
```

**Why this works without `AsSplitQuery`**: By splitting the CSO and MVO loads into two separate
queries (each without split query), neither query has deep enough nesting to cause cartesian
explosion. The CSO query has at most 2 levels of collection includes (CSO → AttributeValues →
ReferenceValue), and the MVO query has 2 levels (MVO → AttributeValues → ReferenceValue).
The problematic cartesian explosion was caused by having BOTH CSO and MVO collection includes
in a single query (8+ branches).

**Why EF auto-fixes navigations**: When Query 2 loads MVOs into the same DbContext, EF Core's
identity resolution automatically links `cso.MetaverseObject` to the loaded MVO entity. This
happens because `cso.MetaverseObjectId` (the FK) matches the MVO's primary key. No manual
stitching needed — this is a standard EF Core feature called "relationship fixup".

### What Gets Removed

Once the three page-load methods return correct data without repair:

**Repository layer** (~200 lines):
- `RepairReferenceValueMaterialisationAsync` — entire method
- `RepairMvoAttributeValueMaterialisationAsync` — entire method
- `ReferenceRepairRow` record
- `MvoAttributeValueRepairRow` record
- All six call sites (2 per page-load method)

**Application/Service layer** (simplification, not removal):
- `DriftDetectionService.GetTypedValueFromMvoAttributeValue` — remove `ReferenceValueId ??` fallback, use navigation directly
- `DriftDetectionService.GetCsoReferenceMetaverseObjectId` — simplify to just `av.ReferenceValue?.MetaverseObjectId`
- `DriftDetectionService.BuildAttributeDictionary` — remove `ReferenceValueId ??` fallback
- `ExportEvaluationServer.CreateAttributeValueChanges` — remove `?? mvoValue.ReferenceValueId` fallback
- `SyncRuleMappingProcessor.GetReferencedMvoId` — simplify to `av.ReferenceValue?.MetaverseObjectId`
- `SyncRuleMappingProcessor` reference comparison — remove `mvoav.ReferenceValueId ??` fallback

**Note on Layer 3 (Import path)**: The `GetReferenceExternalIdsAsync` SQL dictionary and
`ImportRefMatchesCsoValue` fallback address the **import** path (`GetConnectedSystemObjectByAttributeAsync`),
not the sync page-load methods. These are a separate concern — the import query also uses
`AsSplitQuery()` but has a **different Include chain** (includes `ReferenceValue.AttributeValues.Attribute`
for referenced CSOs, not MVO data). The import path fix is out of scope for this phase.

**Note on Layer 7 (MVO persistence)**: The explicit `Entry().State` management in
`MetaverseRepository.UpdateMetaverseObjectsAsync` fixes a genuine EF Core behaviour when
`AutoDetectChangesEnabled = false`. This is **not** an AsSplitQuery workaround — it's needed
regardless. It stays.

---

## Implementation Steps

### Step 1: Extract shared Include chain into a helper method

Create a `private` helper method that builds the shared query (used by all 3 page-load methods)
to avoid code duplication.

### Step 2: Refactor the three page-load methods

Replace `AsSplitQuery()` + deep Include chain + post-load repairs with:
1. `BeginTransactionAsync(IsolationLevel.RepeatableRead)`
2. Query 1: CSOs with their own includes (no MVO navigations)
3. Query 2: MVOs loaded separately with EF relationship fixup
4. `CommitAsync()`

### Step 3: Remove repair infrastructure

Delete:
- `RepairReferenceValueMaterialisationAsync`
- `RepairMvoAttributeValueMaterialisationAsync`
- `ReferenceRepairRow`
- `MvoAttributeValueRepairRow`

### Step 4: Clean up application-layer workarounds

Simplify the scalar FK fallback patterns in:
- `DriftDetectionService`
- `ExportEvaluationServer`
- `SyncRuleMappingProcessor`

### Step 5: Update tests

- Add unit tests for the refactored query methods
- Verify existing tests still pass (in-memory provider won't use transactions — the try/catch
  fallback pattern handles this)

### Step 6: Update plan document

Mark Phase 4 as properly implemented in the main plan document.

---

## Consumer Requirements Summary

All three page-load methods must populate these properties on the returned CSOs:

| Property Path | Required By | Purpose |
|---|---|---|
| `cso.Type` | SyncTaskProcessorBase | Object type filtering, `RemoveContributedAttributesOnObsoletion` |
| `cso.AttributeValues` | All sync processors | Source attribute values for flow |
| `cso.AttributeValues[].Attribute` | SyncRuleMappingProcessor, DriftDetection | Attribute name/type lookup |
| `cso.AttributeValues[].ReferenceValue` | SyncRuleMappingProcessor | CSO→MVO reference resolution |
| `cso.AttributeValues[].ReferenceValue.MetaverseObject` | SyncRuleMappingProcessor | Referenced MVO identity |
| `cso.MetaverseObject` | All sync processors | Join/project/disconnect decisions |
| `cso.MetaverseObject.Type` | DriftDetection, ExportEvaluation | Export rule filtering, deletion rules |
| `cso.MetaverseObject.AttributeValues` | SyncRuleMappingProcessor, DriftDetection | Current MVO state for change detection |
| `cso.MetaverseObject.AttributeValues[].Attribute` | DriftDetection, ExportEvaluation | Attribute metadata for expression evaluation |
| `cso.MetaverseObject.AttributeValues[].ReferenceValue` | SyncRuleMappingProcessor | Existing MVO reference comparison |
| `cso.MetaverseObject.AttributeValues[].ContributedBySystem` | SyncTaskProcessorBase | Attribute contributor tracking on disconnect |

---

## Research Log

### 2026-02-25: Initial Analysis

- Enumerated all 20 `AsSplitQuery()` uses across 4 repository classes
- Mapped the 7-layer tactical fix across 11 files
- Identified the three sync page-load methods as the primary target (identical Include chains)
- Confirmed the import path (`GetConnectedSystemObjectByAttributeAsync`) has a different Include
  chain and should be addressed separately
- Designed the serialisable transaction approach as the cleanest fix
- Key insight: splitting CSO and MVO loads into separate queries within a transaction eliminates
  both the cartesian explosion AND the materialisation bug, while keeping standard EF Core entity
  tracking

### 2026-02-25: Implementation Complete

**Changes made:**

1. **ConnectedSystemRepository.cs** — Refactored all 3 sync page-load methods:
   - Replaced `AsSplitQuery()` + 8-branch Include chain with two separate queries inside
     `BeginTransactionAsync(IsolationLevel.RepeatableRead)`
   - Query 1: CSOs with their own AttributeValues, Attribute, ReferenceValue, Type
   - Query 2: MVOs loaded via `LoadMetaverseObjectsForCsosAsync()` — EF relationship fixup
     automatically populates `cso.MetaverseObject` navigations
   - Added `catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)`
     fallback for unit tests using mocked/in-memory providers
   - Removed `RepairReferenceValueMaterialisationAsync` (~80 lines)
   - Removed `RepairMvoAttributeValueMaterialisationAsync` (~90 lines)
   - Removed `ReferenceRepairRow` and `MvoAttributeValueRepairRow` DTOs
   - Added `using System.Data` for `IsolationLevel`

2. **DriftDetectionService.cs** — Updated comments:
   - `GetTypedValueFromMvoAttributeValue`: Updated comment to reference test compatibility
     instead of AsSplitQuery workaround
   - `BuildAttributeDictionary`: Same comment update
   - `GetCsoReferenceMetaverseObjectId`: Updated warning message to reference missing Include
     instead of AsSplitQuery bug
   - `GetTypedValueFromCsoAttributeValue`: Simplified comment

3. **ExportEvaluationServer.cs** — Updated comment on reference fallback to reference test
   compatibility instead of AsSplitQuery bug

4. **SyncRuleMappingProcessor.cs** — Updated comments:
   - `GetReferencedMvoId` helper: Simplified comment
   - `IsResolved` helper: Removed AsSplitQuery bug reference
   - Navigation vs scalar FK comment: Removed AsSplitQuery reference

5. **DriftDetectionTests.cs** — Updated test comment to remove repair method reference

**Test results**: All 1,794 tests pass (0 failures)

**Net code change**: ~200 lines of repair/workaround code removed, ~40 lines of transaction +
helper method added. The three sync page-load methods are simpler and the application layer
no longer compensates for repository-level EF bugs.

**What was NOT changed** (and why):
- `GetReferenceExternalIdsAsync` + `ImportRefMatchesCsoValue` dictionary fallback (Layer 3):
  Addresses the **import** path which uses a different Include chain — separate concern
- `MetaverseRepository.UpdateMetaverseObjectsAsync` explicit state management (Layer 7):
  Fixes a genuine EF Core behaviour with `AutoDetectChangesEnabled = false`, not an
  AsSplitQuery workaround
- Scalar FK `??` fallback patterns in DriftDetection/ExportEvaluation/SyncRuleMappingProcessor:
  Kept as defensive code for test compatibility (tests may set navigation without scalar FK),
  but comments updated to remove AsSplitQuery references

**Remaining AsSplitQuery uses** (not addressed — out of scope):
- `ConnectedSystemRepository`: ConnectorDefinition queries (3), partition hierarchy (2),
  single-object CSO loads (2 — `GetConnectedSystemObjectAsync`, `GetConnectedSystemObjectByAttributeAsync`),
  import/export queries (2)
- `MetaverseRepository`: 6 uses (header queries, matching queries)
- `DataGenerationRepository`: 2 uses (template queries)
- `ActivitiesRepository`: 2 uses (activity queries)

### 2026-02-25: ContributedBySystemId Bug Fix

**Problem**: Integration test Scenario 4 (Deletion Rules) Tests 1 and 5 failed — `RemoveContributedAttributesOnObsoletion`
was enabled but no attributes were recalled when a CSO was obsoleted. Investigation confirmed this is a **pre-existing bug**
(test ran against `bd1c78c8` on main, before Phase 4 changes).

**Root cause**: `ContributedBySystem` navigation property on `MetaverseObjectAttributeValue` was never set during
sync attribute flow. `SyncRuleMappingProcessor.Process()` creates `new MetaverseObjectAttributeValue` at 14 locations
but none set the contributor. The recall code in `SyncTaskProcessorBase.ProcessObsoleteConnectedSystemObjectAsync`
filters by `av.ContributedBySystem?.Id == connectedSystemId` which always evaluates to false (null != int).

**Fix**:
1. **`MetaverseObjectAttributeValue.cs`** — Added explicit `int? ContributedBySystemId` scalar FK property
   (previously a shadow property managed by EF convention). This avoids needing to `.Include(ConnectedSystem)`.
2. **`SyncRuleMappingProcessor.cs`** — Added `int? contributingSystemId` parameter to `Process()` and all
   14 private methods that create `MetaverseObjectAttributeValue`. Each creation now sets
   `ContributedBySystemId = contributingSystemId`. Nullable to support future internally-managed MVOs.
3. **`SyncTaskProcessorBase.cs`** — Passes `connectedSystemObject.ConnectedSystemId` at the call site.
   Updated both recall sites (`ProcessObsoleteConnectedSystemObjectAsync` line 478 and
   `HandleCsoOutOfScopeAsync` line 1981) to use `av.ContributedBySystemId` instead of `av.ContributedBySystem?.Id`.
4. **`ConnectedSystemRepository.cs`** — Removed `.Include(av => av.ContributedBySystem)` from
   `LoadMetaverseObjectsForCsosAsync` since the sync path uses the scalar FK directly.
5. **`MetaverseObjectDto.cs`** — Updated `ContributedBySystemId` mapping to use scalar FK.
6. **Tests** — Updated existing obsoletion tests to use `ContributedBySystemId`. Added 8 new unit tests
   (`SyncRuleMappingProcessorContributorTests`) covering all attribute types + null contributor.

**Design decision**: Used scalar FK (`int? ContributedBySystemId`) rather than the navigation property
(`ConnectedSystem? ContributedBySystem`) throughout. The CSO already has `ConnectedSystemId` as an int
available without any Include, so passing a scalar avoids loading the full `ConnectedSystem` entity.

**Test results**: All 1,802 unit tests pass (0 failures, +8 new tests)

### 2026-02-25: Integration Test Fix for Attribute Recall

**Problem**: After the `ContributedBySystemId` fix, integration test Scenario 4 Tests 1 and 5 reported
"FAILED: MVO was deleted despite LDAP CSO still being joined". Investigation confirmed the **MVO was NOT deleted** —
worker logs showed "Applying 12 attribute removals to MVO" with no deletion activity.

**Root cause**: The `Test-MvoExists` helper function searched for the MVO by display name
(`Get-JIMMetaverseObject -Search "Test WLCD Recall"`). But Display Name is a CSV-contributed attribute, so
attribute recall now correctly removes it. The MVO still exists but can no longer be found by display name search.

This is a **test verification issue**, not a data integrity issue. Before the `ContributedBySystemId` fix,
attribute recall silently did nothing (contributor was always null), so Display Name remained and the test found
the MVO. Now that recall works correctly, the test needs to search by MVO ID instead.

**Fix**: Added `Test-MvoExistsById` function and updated Tests 1 and 5 (the two recall-enabled tests) to:
1. Use `Test-MvoExistsById -MvoId $testNMvoId` for existence checks (instead of display name search)
2. Use `Get-JIMMetaverseObject -Id $testNMvoId` for subsequent attribute assertions
   (the `-Id` endpoint returns `attributeValues` array, not `attributes` dictionary)

Tests 2 and 6 (`RemoveContributedAttributesOnObsoletion=false`) are unaffected — display name is not recalled.

**Additional fixes to Scenario 4 test harness:**

1. **Fail-fast**: Converted all soft assertion failures (Write-Host red text that didn't throw) to terminating
   errors. Every assertion failure now records `Success = $false` in `$testResults` and `throw`s immediately.
   Previously, the `if ($Step -ne "All") { throw }` guard deliberately swallowed failures when running all
   tests, causing cascade corruption between tests.

2. **Pending export drain**: Added `Invoke-DrainPendingExports` helper called before each test to clear any
   stale pending exports from a prior test. This prevents cascade failures where Test N's LDAP Export picks
   up unrelated pending exports from Test N-1.

3. **Recall export handling — resolved**: See entry below (Pure Recall Export Handling).

### 2026-02-25: Pure Recall Export Handling

**Problem**: After attribute recall, the MVO attribute values are cleared. The initial approach
(commit `9b382409`) generated null-clearing pending exports to clear those values on downstream
target systems. However, this caused two cascading failures:

1. **Expression mappings produce invalid values**: DN expressions like
   `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=" + mv["Department"] + "..."` evaluate against
   post-recall null MVO attributes, producing invalid DNs (e.g., `OU=,OU=Users,...`)
2. **Target systems reject null values**: LDAP/AD rejects null writes for mandatory attributes
   like `sAMAccountName` or `displayName`

**Root cause**: Recall clears MVO attributes *before* export evaluation runs. Expression-based
mappings and direct attribute mappings both operate on the post-recall MVO state where referenced
attributes no longer exist, producing either invalid values or null-clearing changes that target
systems cannot process.

**Fix**: Added an early return in `CreateAttributeValueChanges` that detects pure recall operations
(all `changedAttributes` are in the `removedAttributes` set) and returns an empty changes list.
This skips the entire export evaluation — no pending exports are generated. The target system
retains its existing attribute values after recall.

Also removed the null-clearing code block (~50 lines) that was added in commit `9b382409`.

**Rationale**: Proper recall export handling requires attribute priority (Issue #91) to determine
replacement values from alternative contributors. Until that is implemented, the safest approach
is to skip export evaluation entirely during pure recall, rather than sending invalid or null values
that could corrupt target system data or fail export operations.

**Unit tests updated**:
- `CreateAttributeValueChanges_RecalledSingleValuedAttributes_ProducesNoChangesAsync` — verifies
  that pure recall returns an empty changes list
- `EvaluateExportRules_RecalledAttributes_ProducesNoPendingExportAsync` — verifies the full flow
  produces no pending exports for pure recall

**Also fixed**: `MetaverseObject.IsPendingDeletion` computed property now supports both
`WhenLastConnectorDisconnected` and `WhenAuthoritativeSourceDisconnected` deletion rules.
Previously it only checked for `WhenLastConnectorDisconnected`, causing
`isPendingDeletion=false` for MVOs pending deletion via authoritative source disconnection.

**Also fixed** (commit `e00f6c14`): The REST API `GET /api/synchronisation/connected-systems/{id}`
endpoint was returning `PendingExportCount = 0` because `GetConnectedSystemAsync` no longer loads
the `PendingExports` navigation property (removed as part of the Phase 4 query optimisations). Fixed
by calling `GetPendingExportsCountAsync` (a dedicated count query) and passing the result to
`ConnectedSystemDetailDto.FromEntity`.

## Next Steps

### LDAP Export Failure After Attribute Recall (Scenario 4, Test 3)

Integration test Scenario 4 Test 3 (`WhenAuthoritativeSourceDisconnected` + immediate deletion)
intermittently fails at the LDAP Export step. The failure occurs after attribute recall has cleared
MVO attributes and the system attempts to export deprovisioning changes to LDAP.

**Activity summary:**
- Status: `CompleteWithWarning`
- Result: `Export complete: 1 succeeded, 1 failed, 0 deferred`
- Objects processed: 2 (1 deprovisioned successfully, 1 failed with `UnhandledError`)

**Attributes mapped for export (14 total):**

Direct attribute mappings (11):
| Metaverse Attribute | LDAP Attribute |
|---|---|
| Account Name | sAMAccountName |
| First Name | givenName |
| Last Name | sn |
| Display Name | displayName |
| Display Name | cn |
| Email | mail |
| Email | userPrincipalName |
| Job Title | title |
| Department | department |
| Company | company |
| Employee ID | employeeID |

Expression-based mappings (3):
| LDAP Attribute | Source |
|---|---|
| distinguishedName | Expression (constructs DN from MV attributes) |
| userAccountControl | Expression |
| accountExpires | Expression |

**Error details:** The test infrastructure captures only the `UnhandledError` error type from the
Activity Run Profile Execution Item — the underlying exception message and stack trace are only
available in the worker container logs, which were not captured during these test runs.

**Behaviour:** The failure is **intermittent** — some integration test runs pass Test 3 while others
fail (confirmed across multiple runs on 2026-02-25). This suggests a timing or race condition
rather than a deterministic logic error.

**Likely cause:** After recall clears MVO attributes, expression-based mappings (particularly
`distinguishedName`, which builds a DN from `Display Name`, `Department`, etc.) evaluate against
null MVO attribute values, producing invalid values. LDAP rejects writes for mandatory attributes
(`sAMAccountName`, `displayName`) with null or invalid values. This was the primary motivation for
the "Pure Recall Export Handling" fix described above — skipping export evaluation entirely during
pure recall avoids sending invalid or null values to target systems.

**To investigate further:** Capture worker container logs during the export step
(`docker compose logs jim.worker --tail=5000`) to get the full exception stack trace and identify
which specific LDAP attribute or operation triggers the `UnhandledError`.

### Attribute Priority (Issue #91)

When a contributor system disconnects and recall clears its MVO attributes, the system should
determine if an alternative contributor exists and flow that contributor's values instead of
leaving the attributes empty. This requires implementing attribute priority — a mechanism for
ranking multiple contributors of the same MVO attribute and automatically falling back to the
next-highest-priority contributor when the current one disconnects.

Until Issue #91 is implemented:
- Pure recall skips export evaluation (target retains existing values)
- MVO attributes are cleared but target systems are not updated
- Re-importing from the same source will re-contribute the attributes and flow them to targets
