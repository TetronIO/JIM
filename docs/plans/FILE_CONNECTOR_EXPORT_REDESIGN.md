# Plan: FileConnector Export Redesign + Related Fixes

- **Status:** In Progress
- **Milestone:** MVP
- **Branch:** `feature/scheduler-service-168`

## Problem Summary

FileConnector export is fundamentally broken for its primary use case. Four related issues:

1. **Export writes wrong schema** - Adds system columns (`_objectType`, `_externalId`, `_changeType`), reorders attributes alphabetically. Subsequent import fails because schema doesn't match.
2. **External ID not returned** - `ExportResult.Succeeded()` returns no External ID, so the provisioning CSO never gets its External ID value populated.
3. **Duplicate pending exports** - When Training sync re-evaluates export rules for MVOs that already have pending Create exports for Cross-Domain, no deduplication occurs because the database fallback check only runs for Update operations.
4. **Activity view empty** - RPEIs for Create exports show no External ID or Name because CSO is null at export time.

## Design Decisions

- **Full-state export only for MVP.** Delta/change-log export deferred to post-MVP issue.
- **Remove "Include Full State" setting** from FileConnector and hardcode full-state behaviour.
- **Merge-on-write approach**: Read existing file, apply changes, write result. File always represents current state.
- **External ID from attribute flow**: For Creates, find the IsExternalId attribute in the pending export's attribute changes and return it in ExportResult.

---

## Phase 1: FileConnectorExport Rewrite - COMPLETE

### Phase 1a: Rewrite FileConnectorExport.cs - COMPLETE

**File:** `JIM.Connectors/File/FileConnectorExport.cs`

Rewrote `Execute()` to:

- Determine schema columns from the pending exports' attribute metadata (no system columns)
- Load existing file content into `Dictionary<string, Dictionary<string, string>>` keyed by External ID
- Apply pending export changes (Create = add row, Update = merge, Delete = remove row)
- Write merged full-state result back to CSV
- Return `ExportResult.Succeeded(externalId)` with External ID from IsExternalId attribute

### Phase 1b: Remove Include Full State Setting - COMPLETE

**File:** `JIM.Connectors/File/FileConnector.cs`

- Removed `SettingIncludeFullState` constant
- Removed the setting definition from `GetSettings()` list

---

## Phase 2: Fix Duplicate Pending Exports - COMPLETE

**File:** `JIM.Application/Servers/ExportEvaluationServer.cs`

In `CreateOrUpdatePendingExportWithNoNetChangeAsync`:

- **Before:** The database fallback check (~line 1038) only ran for `changeType == PendingExportChangeType.Update`
- **After:** Changed condition to `changeType == PendingExportChangeType.Update || changeType == PendingExportChangeType.Create`
- When a pending export already exists in the DB for that CSO, it merges rather than creating a duplicate

---

## Phase 3: Fix Activity View for Export RPEIs - COMPLETE

### Phase 3a: ExportExecutionServer CSO Update Ordering - COMPLETE (No Change Needed)

**File:** `JIM.Application/Servers/ExportExecutionServer.cs`

Analysis confirmed that since `ProcessedExportItem.ConnectedSystemObject` holds a **reference** to the CSO object, and `UpdateCsoAfterSuccessfulExportAsync` modifies the same object in-memory before `ProcessExportResultAsync` runs in `SyncExportTaskProcessor`, the External ID is visible when `ExternalIdSnapshot` is captured. No code change required.

### Phase 3b: Populate RPEI External ID Snapshot - COMPLETE

**File:** `JIM.Worker/Processors/SyncExportTaskProcessor.cs`

In `ProcessExportResultAsync`:

- Added `ExternalIdSnapshot` population from `exportItem.ConnectedSystemObject.ExternalIdAttributeValue?.ToStringNoName()`
- This ensures the RPEI retains the external ID even if the CSO is later deleted via FK cascade

---

## Phase 4: Create GitHub Issue for Delta Export - COMPLETE

**GitHub Issue:** #309 - FileConnector: Add delta/change-log export mode

Post-MVP issue created documenting delta/change-log export as a future enhancement, including requirements for:
- Admin-configurable change type column name
- Append vs overwrite mode
- Delete handling

---

## Unit Tests - COMPLETE

**File:** `test/JIM.Worker.Tests/Connectors/FileConnectorExportTests.cs`

Full test suite covering:
- No-op / error cases (no pending exports, missing file path, no external ID attribute)
- Create exports (correct headers, correct data, returns External ID, multiple creates, no external ID value fails)
- Update exports (merge with existing, preserve unchanged attributes, preserve other rows)
- Delete exports (remove row, non-existent row still succeeds)
- Mixed operations (create + update + delete in one batch)
- Custom delimiter
- Full-state file tests (new file creation, duplicate external ID treated as update, alphabetical headers)
- Attribute types (integer, boolean, datetime)
- Remove attribute (sets empty value)
- Capabilities (supports export, auto-confirm, no Include Full State setting)

---

## Remaining Steps

1. **Build and test**: `dotnet build JIM.sln && dotnet test JIM.sln`
2. **Commit** all changes

## Files Modified

| File | Change | Status |
|------|--------|--------|
| `JIM.Connectors/File/FileConnectorExport.cs` | Full rewrite of Execute() | COMPLETE |
| `JIM.Connectors/File/FileConnector.cs` | Remove Include Full State setting | COMPLETE |
| `JIM.Application/Servers/ExportEvaluationServer.cs` | Fix duplicate PE dedup for Creates | COMPLETE |
| `JIM.Worker/Processors/SyncExportTaskProcessor.cs` | Populate RPEI External ID snapshot | COMPLETE |
| `test/JIM.Worker.Tests/Connectors/FileConnectorExportTests.cs` | Full test suite | COMPLETE |

## Verification

1. `dotnet build JIM.sln` - zero errors
2. `dotnet test JIM.sln` - all tests pass
3. Integration test: `./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -Template Micro`
4. Cross-Domain Export should show External IDs in Activity view
5. Cross-Domain Import should succeed with no duplicate errors
6. File content should match schema (no system columns)
