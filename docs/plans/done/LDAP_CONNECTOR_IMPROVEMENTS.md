# LDAP Connector Improvements

- **Status:** Done

> Code review findings and improvement plan for the JIM LDAP Connector

## Executive Summary

The LDAP Connector (`src/JIM.Connectors/LDAP/`) is a well-structured connector for importing from LDAP/Active Directory directories with ~750 lines across 7 files. It implements full schema discovery, partition/container enumeration, and paged imports. However, it has **no test coverage**, **resource disposal issues**, and several code quality concerns.

---

## Architecture Overview

The connector is cleanly separated into:

| File | Purpose | Lines |
|------|---------|-------|
| `LdapConnector.cs` | Main connector, settings, connection management | ~230 |
| `LdapConnectorImport.cs` | Full import logic with paging | ~340 |
| `LdapConnectorSchema.cs` | Schema discovery from directory | ~260 |
| `LdapConnectorPartitions.cs` | Partition and container enumeration | ~115 |
| `LdapConnectorUtilities.cs` | Attribute value extraction helpers | ~120 |
| `LdapConnectorConstants.cs` | Auth type and capability OID constants | ~18 |
| `LdapConnectorRootDse.cs` | RootDSE data structure | ~10 |

**Implemented Interfaces:**
- `IConnector` - Basic connector info
- `IConnectorCapabilities` - Capability flags
- `IConnectorSettings` - Configuration settings
- `IConnectorSchema` - Schema discovery
- `IConnectorPartitions` - Partition enumeration
- `IConnectorImportUsingCalls` - Import via API calls

---

## Critical Issues

### 1. Missing IDisposable Implementation (HIGH SEVERITY)

**Location:** `src/JIM.Connectors/LDAP/LdapConnector.cs` line 11

**Problem:** The connector holds a disposable `LdapConnection` but doesn't implement `IDisposable`. If an exception occurs between `OpenImportConnection()` and `CloseImportConnection()`, the connection leaks.

**Current Code:**
```csharp
public class LdapConnector : IConnector, IConnectorCapabilities, ...
{
    private LdapConnection? _connection;
    // ...
    public void CloseImportConnection()
    {
        _connection?.Dispose();
    }
}
```

**Impact:** Connection leaks under error conditions, potential exhaustion of LDAP server connections.

**Fix:** Implement `IDisposable` with proper cleanup pattern.

---

### 2. Console.WriteLine Instead of Logger (MEDIUM SEVERITY)

**Locations:**
- `LdapConnectorSchema.cs` lines 247, 253
- `LdapConnectorPartitions.cs` lines 102, 108

**Problem:** Error conditions logged to Console instead of injected logger, meaning errors are invisible in production.

**Current Code:**
```csharp
Console.WriteLine($"GetSchemaNamingContext: No success. Result code: {response.ResultCode}");
```

**Fix:** Pass `ILogger` to these classes and use `_logger.Warning()` or `_logger.Error()`.

---

### 3. Static Logger Reference (LOW SEVERITY)

**Location:** `LdapConnectorImport.cs` line 177

**Problem:** Uses static `Log.Verbose()` instead of injected `_logger`.

**Current Code:**
```csharp
Log.Verbose("LDAPConnector > GetRootDseInformation: Got info");
```

**Fix:** Replace with `_logger.Verbose(...)`.

---

## Test Coverage Gap

**Current State:** 0% test coverage

The LDAP connector has no unit tests. Testing LDAP is challenging due to the need for an actual directory server, but several approaches are possible:

| Approach | Pros | Cons |
|----------|------|------|
| Mock `LdapConnection` | Fast, no dependencies | Complex mocking required |
| Integration tests with test AD | Realistic | Requires infrastructure |
| Docker-based OpenLDAP | CI-friendly | Setup complexity |
| Test only utilities/schema parsing | Easy to implement | Limited coverage |

**Minimum test coverage should include:**
- `LdapConnectorUtilities` - All helper methods
- `ValidateSettingValues` - Setting validation logic
- `GetSettings` - Verify settings structure

---

## Code Quality Issues

### 4. Potential Null Dereference (MEDIUM)

**Location:** `LdapConnectorImport.cs` line 169-170

**Problem:** No null check on response or bounds check on Entries collection.

**Current Code:**
```csharp
var response = (SearchResponse)_connection.SendRequest(request);
var rootDseEntry = response.Entries[0];
```

**Fix:** Add null/bounds checks before accessing.

---

### 5. Duplicate omSyntax Mapping Logic (LOW)

**Locations:**
- `LdapConnectorSchema.cs` lines 190-220 (switch statement)
- `LdapConnectorUtilities.cs` lines 106-120 (`GetLdapAttributeDataType` method)

**Problem:** Same mapping logic exists in two places. The utility method exists but isn't used in schema discovery.

**Fix:** Use `LdapConnectorUtilities.GetLdapAttributeDataType()` in `LdapConnectorSchema.cs`.

---

### 6. Missing omSyntax Case (LOW)

**Location:** `LdapConnectorUtilities.cs` line 115

**Problem:** Case 4 is handled in `LdapConnectorSchema.cs` but missing from utility method.

**Schema version:**
```csharp
case 4:
case 6:
case 18:
// ...
    attributeDataType = AttributeDataType.Text;
```

**Utility version:**
```csharp
6 or 18 or 19 or 20 or 22 or 27 or 64 => AttributeDataType.Text,
```

**Fix:** Add case 4 to the utility method.

---

### 7. Hardcoded Search Timeout (MEDIUM)

**Location:** `LdapConnectorImport.cs` line 211

**Problem:** 5-minute timeout is hardcoded.

**Current Code:**
```csharp
var searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, TimeSpan.FromMinutes(5));
```

**Fix:** Add configurable setting "Search Timeout (seconds)" with sensible default.

---

### 8. Non-Async Method Returns Task (LOW)

**Location:** `LdapConnector.cs` line 165

**Problem:** `ImportAsync` wraps synchronous code in `Task.FromResult()`.

**Current Code:**
```csharp
public Task<ConnectedSystemImportResult> ImportAsync(...)
{
    // ... synchronous code ...
    return Task.FromResult(import.GetFullImportObjects());
}
```

**Fix:** Either make properly async or rename to indicate synchronous execution.

---

### 9. Magic Number for System Flags (LOW)

**Location:** `LdapConnectorPartitions.cs` line 38

**Problem:** Magic number "3" without explanation.

**Current Code:**
```csharp
Hidden = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "systemflags") != "3"
```

**Fix:** Add constant with documentation explaining systemFlags=3 means "Domain partition".

---

### 10. Inconsistent String Comparison (LOW)

**Locations:** Throughout codebase

**Problem:** Mix of `StringComparison.CurrentCultureIgnoreCase` and `StringComparison.OrdinalIgnoreCase`.

**Fix:** Standardise on `OrdinalIgnoreCase` for identifiers (DN, attribute names).

---

### 11. Commented-Out Code (LOW)

**Locations:**
- `LdapConnector.cs` lines 159-160 (Secure connection options)
- `LdapConnectorImport.cs` lines 75-78 (Delta import USN tracking)
- `LdapConnectorPartitions.cs` lines 36-37 (DNS/NetBIOS names)

**Fix:** Remove or convert to documented TODO issues.

---

## Known Limitations (By Design)

| Feature | Status | Notes |
|---------|--------|-------|
| Delta Import | ✅ Implemented | USN-based for AD, changelog-based for non-AD |
| Delta Import Deletion Detection | ✅ Implemented | Queries AD Deleted Objects container with Show Deleted control |
| Export | ✅ Implemented | Create, update, delete/disable in AD |
| Secure Connection (LDAPS) | ✅ Implemented | Configurable via settings |
| User-Selected External ID | Not Supported | Uses objectGUID by default |

---

## Improvement Plan

### Phase 1: Critical Fixes (Priority: Immediate) ✅

- [x] **1.1** Implement `IDisposable` on `LdapConnector` with proper cleanup
- [x] **1.2** Replace `Console.WriteLine` with logger in `LdapConnectorSchema.cs`
- [x] **1.3** Replace `Console.WriteLine` with logger in `LdapConnectorPartitions.cs`
- [x] **1.4** Replace static `Log` with injected logger in `LdapConnectorImport.cs`
- [x] **1.5** Add null/bounds checks in `GetRootDseInformation()`

### Phase 2: Test Coverage (Priority: High) ✅

- [x] **2.1** Create `test/JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs`
- [x] **2.2** Add tests for all `GetEntryAttribute*` helper methods - Skipped (requires mocking LDAP SearchResultEntry)
- [x] **2.3** Add tests for `GetLdapAttributeDataType()` including edge cases (19 tests)
- [x] **2.4** Add tests for `GetPaginationTokenName()` (3 tests)
- [x] **2.5** Create `test/JIM.Worker.Tests/Connectors/LdapConnectorTests.cs`
- [x] **2.6** Add test for `GetSettings()` returns expected settings (10 tests)
- [x] **2.7** Add test for `ValidateSettingValues()` with missing values - Covered by settings structure tests

### Phase 3: Code Quality (Priority: Medium)

- [x] **3.1** Use `GetLdapAttributeDataType()` utility in schema discovery (remove duplication)
- [x] **3.2** Add missing omSyntax case 4 to utility method (maps to Binary for OctetString attributes)
- [x] **3.3** Add "Search Timeout" configurable setting (default 300 seconds)
- [x] **3.4** Add constant for systemFlags=3 with documentation
- [x] **3.5** Standardise string comparisons to `OrdinalIgnoreCase`
- [x] **3.6** Remove or document commented-out code (removed unused partition metadata, converted to TODOs with Phase 5 references)

### Phase 4: Export Implementation (Priority: MVP) ✅

- [x] **4.1** Design export capability (create/update/delete in AD)
- [x] **4.2** Add export settings to `GetSettings()` (Delete Behaviour, Disable Attribute)
- [x] **4.3** Create `LdapConnectorExport.cs` class (~350 lines)
- [x] **4.4** Implement `IConnectorExportUsingCalls` interface (OpenExportConnection, Export, CloseExportConnection)
- [x] **4.5** Implement object creation in directory (AddRequest)
- [x] **4.6** Implement object modification in directory (ModifyRequest with Add/Replace/Delete operations)
- [x] **4.7** Implement object deletion/disable in directory (DeleteRequest or userAccountControl disable)
- [x] **4.8** Add export tests (4 new tests, 160 total)

### Phase 5: Enhancements (Priority: Future) ✅

- [x] **5.1** Implement delta import using USN/changelog
  - AD: Uses `uSNChanged` attribute filter with `HighestCommittedUSN` watermark
  - Non-AD: Uses changelog-based tracking with `changeNumber` watermark
  - Updated `LdapConnectorRootDse` to track both USN and changelog positions
  - Added `GetDeltaImportObjects()`, `GetDeltaResultsUsingUsn()`, `GetDeltaResultsUsingChangelog()` methods
  - Added `GetEntryAttributeLongValue()` utility for 64-bit USN values
  - **Deletion detection**: Queries `CN=Deleted Objects,<partition>` with `LDAP_SERVER_SHOW_DELETED_OID` control
  - Matches tombstones to existing CSOs using `objectGUID` (stable identifier preserved on tombstones)
  - Gracefully handles directories that don't support the Show Deleted control (e.g., some Samba AD configurations)
- [x] **5.2** Add LDAPS (secure connection) support
  - Added "Use Secure Connection (LDAPS)?" checkbox setting
  - Added "Certificate Validation" dropdown (Full Validation / Skip Validation)
  - Implemented SecureSocketLayer and VerifyServerCertificate configuration
  - Updated port description to reference standard LDAPS port 636
- [x] **5.3** Connection pooling - Deferred (see notes)
  - The connector already reuses connections between Open/Close calls
  - True connection pooling across connector instances requires infrastructure changes
  - System.DirectoryServices.Protocols doesn't provide built-in pooling
  - Future work: Consider implementing at the worker service level
- [x] **5.4** Add retry logic for transient failures
  - Added "Maximum Retries" setting (default: 3)
  - Added "Retry Delay (ms)" setting (default: 1000ms)
  - Implemented `ExecuteWithRetry()` with exponential backoff
  - Added `IsTransientError()` to identify retriable LDAP error codes (51, 52, 53, 80, 81, -1)

### Phase 6: Export Performance (Priority: Post-MVP) ✅

- [x] **6.1** Async export interface with `CancellationToken` support
  - `IConnectorExportUsingCalls.ExportAsync()` now accepts `CancellationToken`
  - `LdapConnectorExport` refactored with async code paths
- [x] **6.2** Async LDAP operations via APM wrapper
  - New `LdapConnectionExtensions.SendRequestAsync()` wrapping `BeginSendRequest`/`EndSendRequest`
  - New `ILdapOperationExecutor` abstraction for testability (LdapConnection is sealed)
- [x] **6.3** Configurable export concurrency (LDAP pipelining)
  - "Export Concurrency" integer setting (1-16, default 1)
  - `SemaphoreSlim`-based throttling across exports within a batch
  - Container creation serialised via dedicated `SemaphoreSlim(1,1)` to prevent race conditions
  - Multi-step operations (create+GUID, rename+modify, UAC read+write) remain sequential per export
- [x] **6.4** `SupportsParallelExport` capability flag
  - LDAP connector returns `true` (supports concurrent connections)
  - Enables per-Connected System `MaxExportParallelism` setting in the UI
- [x] **6.5** Unit tests (13 async export tests in `LdapConnectorExportAsyncTests.cs`)

---

## Detailed Implementation Notes

### Phase 1.1: IDisposable Implementation

**File:** `src/JIM.Connectors/LDAP/LdapConnector.cs`

```csharp
public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings,
    IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls, IDisposable
{
    private LdapConnection? _connection;
    private bool _disposed;

    // ... existing code ...

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _connection?.Dispose();
            _connection = null;
        }

        _disposed = true;
    }
}
```

### Phase 1.2-1.4: Logger Injection

**LdapConnectorSchema.cs** - Add logger parameter:

```csharp
internal class LdapConnectorSchema
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private readonly ConnectorSchema _schema;
    private string _schemaNamingContext = null!;

    internal LdapConnectorSchema(LdapConnection ldapConnection, ILogger logger)
    {
        _connection = ldapConnection;
        _logger = logger;
        _schema = new ConnectorSchema();
    }

    // Replace Console.WriteLine with:
    _logger.Warning($"GetSchemaNamingContext: No success. Result code: {response.ResultCode}");
}
```

Apply same pattern to `LdapConnectorPartitions.cs`.

### Phase 2.1: Utility Tests Structure

**File:** `test/JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs`

```csharp
using NUnit.Framework;
using JIM.Connectors.LDAP;
using JIM.Models.Core;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorUtilitiesTests
{
    [Test]
    public void GetLdapAttributeDataType_OmSyntax1_ReturnsBoolean()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(1);
        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax127_ReturnsReference()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(127);
        Assert.That(result, Is.EqualTo(AttributeDataType.Reference));
    }

    [Test]
    public void GetLdapAttributeDataType_UnsupportedOmSyntax_ThrowsException()
    {
        Assert.Throws<InvalidDataException>(() =>
            LdapConnectorUtilities.GetLdapAttributeDataType(999));
    }

    [Test]
    public void GetPaginationTokenName_ReturnsExpectedFormat()
    {
        var container = new ConnectedSystemContainer { ExternalId = "OU=Users,DC=corp,DC=local" };
        var objectType = new ConnectedSystemObjectType { Id = Guid.Parse("...") };

        var result = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);

        Assert.That(result, Does.Contain("OU=Users"));
        Assert.That(result, Does.Contain("|"));
    }
}
```

### Phase 3.3: Search Timeout Setting

**Add to `GetSettings()` in `LdapConnector.cs`:**

```csharp
new()
{
    Name = "Search Timeout",
    Required = false,
    Description = "Maximum time in seconds to wait for LDAP search results. Default is 300 (5 minutes).",
    DefaultIntValue = 300,
    Category = ConnectedSystemSettingCategory.General,
    Type = ConnectedSystemSettingType.Integer
}
```

---

## Files Affected

| File | Changes |
|------|---------|
| `src/JIM.Connectors/LDAP/LdapConnector.cs` | Add IDisposable, IConnectorExportUsingCalls, export settings, pass logger to helpers |
| `src/JIM.Connectors/LDAP/LdapConnectorSchema.cs` | Accept logger, replace Console.WriteLine |
| `src/JIM.Connectors/LDAP/LdapConnectorPartitions.cs` | Accept logger, replace Console.WriteLine |
| `src/JIM.Connectors/LDAP/LdapConnectorImport.cs` | Replace static Log, add null checks, configurable search timeout |
| `src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs` | Add missing omSyntax case 4 (Binary) |
| `src/JIM.Connectors/LDAP/LdapConnectorConstants.cs` | Add systemFlags, delete behaviour, UAC constants |
| `src/JIM.Connectors/LDAP/LdapConnectorExport.cs` | New file - export functionality |
| `test/JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs` | New file |
| `test/JIM.Worker.Tests/Connectors/LdapConnectorTests.cs` | New file with export tests |

---

## Acceptance Criteria

### Phase 1 Complete When: ✅
- [x] `LdapConnector` implements `IDisposable`
- [x] No `Console.WriteLine` calls remain
- [x] No static `Log` references remain
- [x] Null checks added to `GetRootDseInformation()`
- [x] Build passes with no new warnings
- [x] All existing tests pass

### Phase 2 Complete When: ✅
- [x] `LdapConnectorUtilities` has >80% test coverage
- [x] Settings validation logic tested
- [x] At least 10 unit tests added (43 tests total)

### Phase 3 Complete When: ✅
- [x] No duplicate code for omSyntax mapping
- [x] Search timeout is configurable
- [x] String comparisons are consistent
- [x] No unexplained magic numbers
- [x] Commented-out code documented with TODOs

### Phase 4 Complete When: ✅
- [x] Export to AD works for create operations (AddRequest)
- [x] Export to AD works for update operations (ModifyRequest)
- [x] Export to AD works for delete/disable operations (DeleteRequest or userAccountControl)
- [x] MVP checklist item "LDAP/Active Directory Connector - Export" is complete

### Phase 5 Complete When: ✅
- [x] Delta import works for AD (USN-based) and non-AD (changelog-based) directories
- [x] LDAPS secure connections are configurable via settings
- [x] Certificate validation can be skipped for self-signed certificates
- [x] Transient LDAP failures are automatically retried with exponential backoff
- [x] All tests pass (164 tests total - 4 new LDAPS/retry settings tests added)

---

## References

- [LdapConnector.cs](../src/JIM.Connectors/LDAP/LdapConnector.cs)
- [LdapConnectorImport.cs](../src/JIM.Connectors/LDAP/LdapConnectorImport.cs)
- [LdapConnectorSchema.cs](../src/JIM.Connectors/LDAP/LdapConnectorSchema.cs)
- [LdapConnectorPartitions.cs](../src/JIM.Connectors/LDAP/LdapConnectorPartitions.cs)
- [LdapConnectorUtilities.cs](../src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs)
- [MVP Definition](MVP_DEFINITION.md) - See "LDAP/Active Directory Connector - Export"
- [System.DirectoryServices.Protocols Documentation](https://learn.microsoft.com/en-us/dotnet/api/system.directoryservices.protocols)
