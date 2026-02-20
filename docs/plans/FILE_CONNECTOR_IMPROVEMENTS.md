# File Connector Improvements

> Code review findings and improvement plan for the JIM File Connector

## Executive Summary

The File Connector (`src/JIM.Connectors/File/`) is a CSV-based import connector with ~460 lines across 4 files. While well-structured with proper async/await patterns, it has **one critical bug** (type conversion crash), **zero direct test coverage**, and several unimplemented features.

---

## Critical Issues

### 1. Type Conversion Error Handling (HIGH SEVERITY)

**Location:** `src/JIM.Connectors/File/FileConnectorImport.cs` lines 107-119

**Problem:** Typed field access (`GetField<int>`, `GetField<DateTime>`, etc.) has no try-catch. Invalid CSV data will crash the entire import.

**Current Code:**
```csharp
case AttributeDataType.Number:
    importObject.IntValues.Add(_reader.CsvReader.GetField<int>(attribute.Name));
    break;
case AttributeDataType.DateTime:
    importObject.DateTimeValues.Add(_reader.CsvReader.GetField<DateTime>(attribute.Name));
    break;
// ... same pattern for Boolean, Guid
```

**Impact:** If a CSV contains "abc" in a column inferred as Number, the import fails with an unhandled exception.

**Fix:** Wrap each typed access in try-catch and record errors gracefully.

---

### 2. XML Documentation Error (LOW SEVERITY)

**Location:** `src/JIM.Connectors/File/FileConnector.cs` line 53

**Problem:** Copy-paste error from LDAP Connector.

**Current:**
```csharp
/// Validates LdapConnector setting values using custom business logic.
```

**Should be:**
```csharp
/// Validates FileConnector setting values using custom business logic.
```

---

## Test Coverage Gap

**Current State:** 0% direct test coverage

All synchronisation tests use `MockFileConnector`, not the actual `FileConnector`. The following scenarios are untested:

| Scenario | Risk |
|----------|------|
| CSV schema detection | Medium - could infer wrong types |
| Data type inference | Medium - edge cases unvalidated |
| Import with valid data | High - core functionality |
| Import with invalid data | Critical - will crash |
| Delimiter/culture configuration | Low - but untested |
| File validation | Medium - error messages unverified |

---

## Known Limitations (By Design)

These are documented limitations, not bugs:

| Feature | Status | Notes |
|---------|--------|-------|
| Delta Import | Deferred | See [#129](https://github.com/TetronIO/JIM/issues/129) |
| Export | Planned (MVP) | See Phase 4.3 design below |
| Multi-valued attributes | ✅ Implemented | Uses configurable delimiter (default `\|`) |
| Column-based object types | ✅ Implemented | Discovers types from specified column |
| Update/Delete operations | Not supported | Only Create at line 57 |

---

## Improvement Plan

### Phase 1: Critical Fixes (Priority: Immediate) ✅

- [x] **1.1** Add try-catch error handling for type conversions in `FileConnectorImport.cs`
- [x] **1.2** Fix XML documentation in `FileConnector.cs`

### Phase 2: Test Coverage (Priority: High) ✅

- [x] **2.1** Create `test/JIM.Worker.Tests/Connectors/FileConnectorTests.cs`
- [x] **2.2** Add test: `GetSchemaAsync_WithValidCsv_ReturnsCorrectAttributes`
- [x] **2.3** Add test: `GetSchemaAsync_InfersCorrectDataTypes`
- [x] **2.4** Add test: `ImportAsync_WithValidData_CreatesObjects`
- [x] **2.5** Add test: `ImportAsync_WithInvalidNumber_RecordsError`
- [x] **2.6** Add test: `ImportAsync_WithMissingFile_ThrowsException`
- [x] **2.7** Add test: `ImportAsync_WithCustomDelimiter_ParsesCorrectly`
- [x] **2.8** Add test: `ValidateSettingValues_WithMissingFile_ReturnsError`
- [x] **2.9** Add test: `ValidateSettingValues_WithValidFile_ReturnsNoErrors` (bonus)

### Phase 3: Enhancements (Priority: Medium) ✅

- [x] **3.1** Add "Stop On First Error" checkbox setting to stop import on first error
- [x] **3.2** Implement multi-valued attribute support (pipe `|` delimited values in cells)
- [x] **3.3** Implement column-based object type detection in GetSchemaAsync

### Phase 4: Future Features (Priority: Low)

- [ ] **4.1** Implement delta import support - *Deferred to backlog, see [#129](https://github.com/TetronIO/JIM/issues/129)*
- [x] **4.2** ~~Consider separating int/double type inference~~ - *Won't implement: Integer-only Number type sufficient for IDAM use cases*
- [x] **4.3** Implement export capability - **MVP Scope** (see design below)

---

## Phase 4.3: Export Capability Design

### Overview

Implement `IConnectorExportUsingFiles` to enable CSV-based exports from JIM to external systems.

### Core Export Format

**Standard CSV format with change tracking columns:**

| Column | Description |
|--------|-------------|
| `_objectType` | The object type (e.g., "User", "Group") |
| `_externalId` | Unique identifier for the object |
| `_changeType` | Operation: `Create`, `Update`, or `Delete` |
| `[attributes...]` | One column per attribute in the schema |

**Example output:**
```csv
_objectType,_externalId,_changeType,displayName,email,department
User,jsmith,Create,John Smith,jsmith@example.com,Engineering
User,jdoe,Update,Jane Doe,jane.doe@example.com,Marketing
User,bwilson,Delete,,,
```

### Optional Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Export File Path | String | Required | Output directory for export files |
| Timestamped Files | Checkbox | false | Append timestamp to filename (e.g., `export_20240115_143022.csv`) |
| Separate Files Per Object Type | Checkbox | false | Create one file per object type (e.g., `User.csv`, `Group.csv`) |
| Include Full State | Checkbox | false | Include all attribute values, not just changed attributes |
| Auto-Confirm Exports | Checkbox | true | Automatically confirm exports after file is written (no return file required) |
| Multi-Value Delimiter | String | `\|` | Delimiter for multi-valued attributes (reuse existing setting) |

### Export Confirmation

**Default behaviour (Auto-Confirm enabled):**

File-based exports are typically one-way integrations where no feedback mechanism exists. When "Auto-Confirm Exports" is enabled (default), PendingExports are deleted (confirmed) immediately after the CSV file is successfully written.

**Alternative (Auto-Confirm disabled):**

If disabled, JIM uses reconciliation-based confirmation:

1. FileConnector writes CSV with pending exports
2. External system processes the file and writes changes back to a file JIM can import
3. On next Full Import + Sync, JIM compares `PendingExport` changes against actual CSO values
4. When changes match, `PendingExport` is deleted (confirmed)

This is useful for bidirectional file integrations but requires the external system to provide feedback.

**Note:** Some legacy systems work around this by re-importing the exported file. JIM's auto-confirm setting eliminates this workaround.

### Auto-Confirm Implementation

Auto-confirm requires a new capability interface so the worker knows when to delete PendingExports immediately after export:

**New interface:** `IConnectorCapabilities.SupportsAutoConfirmExport`

```csharp
// In IConnectorCapabilities
bool SupportsAutoConfirmExport { get; }
```

**Worker logic (ExportExecutionServer):**

```csharp
// After successful connector.Export() call
if (connector is IConnectorCapabilities caps && caps.SupportsAutoConfirmExport)
{
    // Check the connector's setting
    var autoConfirmSetting = connectedSystem.SettingValues
        .SingleOrDefault(s => s.Setting.Name == "Auto-Confirm Exports");
    var autoConfirm = autoConfirmSetting?.CheckboxValue ?? true; // default true

    if (autoConfirm)
    {
        await Application.Repository.ConnectedSystems.DeletePendingExportAsync(export);
        continue;
    }
}

// Fall through to existing behaviour
export.Status = PendingExportStatus.Exported;
await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);
```

**FileConnector implementation:**

```csharp
public bool SupportsAutoConfirmExport => true;
```

This approach:
- Keeps the decision at the connector level (capability interface)
- Allows per-deployment configuration via settings
- Is backward compatible - connectors without the capability use existing behaviour
- Any future connector can opt-in by implementing the capability

### Implementation Tasks

- [x] **4.3.1** Add `SupportsAutoConfirmExport` to `IConnectorCapabilities` interface
- [x] **4.3.2** Update `ExportExecutionServer` to check capability and setting
- [x] **4.3.3** Add export settings to `FileConnector.GetSettings()`
- [x] **4.3.4** Create `FileConnectorExport.cs` class
- [x] **4.3.5** Implement `IConnectorExportUsingFiles.Export()` method
- [x] **4.3.6** Handle timestamped filename generation
- [x] **4.3.7** Handle separate files per object type
- [x] **4.3.8** Handle full state vs changes-only export
- [x] **4.3.9** Set `SupportsExport = true` and `SupportsAutoConfirmExport = true` in capabilities
- [x] **4.3.10** Add unit tests for export functionality
- [x] **4.3.11** Add integration test with mock pending exports
- [ ] **4.3.12** Add test for auto-confirm behaviour in worker

### Files to Modify/Create

| File | Changes |
|------|---------|
| `src/JIM.Models/Interfaces/IConnectorCapabilities.cs` | Add `SupportsAutoConfirmExport` property |
| `src/JIM.Worker/Servers/ExportExecutionServer.cs` | Check capability and setting after export |
| `src/JIM.Connectors/File/FileConnector.cs` | Add export settings, implement interface, set capabilities |
| `src/JIM.Connectors/File/FileConnectorExport.cs` | New file - export logic |
| `test/JIM.Worker.Tests/Connectors/FileConnectorExportTests.cs` | New file - export tests |

---

## Detailed Implementation Notes

### Phase 1.1: Type Conversion Error Handling

**File:** `src/JIM.Connectors/File/FileConnectorImport.cs`

Replace lines 105-130 with:

```csharp
case AttributeDataType.Number:
    try
    {
        var value = _reader.CsvReader.GetField<int>(attribute.Name);
        importObject.IntValues.Add(new ConnectedSystemObjectAttributeIntValue
        {
            AttributeName = attribute.Name,
            Value = value
        });
    }
    catch (Exception ex)
    {
        result.Errors.Add(new ConnectedSystemImportError
        {
            ExternalId = importObject.ExternalId,
            ObjectType = importObject.ObjectType,
            Message = $"Failed to parse '{attribute.Name}' as number: {ex.Message}"
        });
    }
    break;
```

Apply same pattern for DateTime, Boolean, and Guid cases.

### Phase 2.1: Test File Structure

**File:** `test/JIM.Worker.Tests/Connectors/FileConnectorTests.cs`

```csharp
using NUnit.Framework;
using JIM.Connectors.File;
using JIM.Models.Core;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class FileConnectorTests
{
    private FileConnector _connector = null!;
    private string _testFilesPath = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        _testFilesPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "Csv");
    }

    // Tests go here...
}
```

**Test Data Files Needed:**
- `test/JIM.Worker.Tests/TestData/Csv/valid_users.csv`
- `test/JIM.Worker.Tests/TestData/Csv/invalid_numbers.csv`
- `test/JIM.Worker.Tests/TestData/Csv/custom_delimiter.csv`

---

## Files Affected

| File | Changes |
|------|---------|
| `src/JIM.Connectors/File/FileConnector.cs` | Fix XML docs (line 53) |
| `src/JIM.Connectors/File/FileConnectorImport.cs` | Add error handling (lines 105-130) |
| `test/JIM.Worker.Tests/Connectors/FileConnectorTests.cs` | New file |
| `test/JIM.Worker.Tests/TestData/Csv/*.csv` | New test data files |

---

## Acceptance Criteria

### Phase 1 Complete When: ✅
- [x] Invalid CSV data records errors instead of crashing
- [x] XML documentation is correct
- [x] Build passes (one unrelated warning in SyncFullSyncTaskProcessor.cs)
- [x] All existing tests pass

### Phase 2 Complete When: ✅
- [x] FileConnector has direct test coverage (8 tests added)
- [x] Error scenario tested (invalid number records error, not crash)
- [x] Test data files committed (4 CSV files)

### Phase 3 Complete When: ✅
- [x] "Stop on first error" setting available (CheckBox setting in General category)
- [x] Multi-valued attributes parse correctly (pipe `|` delimited)
- [x] Column-based object types work (discovers types from specified column)

---

## References

- [FileConnector.cs](../src/JIM.Connectors/File/FileConnector.cs)
- [FileConnectorImport.cs](../src/JIM.Connectors/File/FileConnectorImport.cs)
- [FileConnectorReader.cs](../src/JIM.Connectors/File/FileConnectorReader.cs)
- [FileConnectorObjectTypeInfo.cs](../src/JIM.Connectors/File/FileConnectorObjectTypeInfo.cs)
- [MockFileConnector.cs](../src/JIM.Connectors/Mock/MockFileConnector.cs) - Reference for test patterns
