# LDAP Connector Improvements

> Code review findings and improvement plan for the JIM LDAP Connector

## Executive Summary

The LDAP Connector (`JIM.Connectors/LDAP/`) is a well-structured connector for importing from LDAP/Active Directory directories with ~750 lines across 7 files. It implements full schema discovery, partition/container enumeration, and paged imports. However, it has **no test coverage**, **resource disposal issues**, and several code quality concerns.

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

**Location:** `JIM.Connectors/LDAP/LdapConnector.cs` line 11

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
| Delta Import | Not Implemented | `SupportsDeltaImport = false` |
| Export | Not Implemented | `SupportsExport = false` - MVP item |
| Secure Connection (LDAPS) | Commented Out | Code exists but disabled |
| User-Selected External ID | Not Supported | Uses objectGUID by default |

---

## Improvement Plan

### Phase 1: Critical Fixes (Priority: Immediate)

- [ ] **1.1** Implement `IDisposable` on `LdapConnector` with proper cleanup
- [ ] **1.2** Replace `Console.WriteLine` with logger in `LdapConnectorSchema.cs`
- [ ] **1.3** Replace `Console.WriteLine` with logger in `LdapConnectorPartitions.cs`
- [ ] **1.4** Replace static `Log` with injected logger in `LdapConnectorImport.cs`
- [ ] **1.5** Add null/bounds checks in `GetRootDseInformation()`

### Phase 2: Test Coverage (Priority: High)

- [ ] **2.1** Create `JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs`
- [ ] **2.2** Add tests for all `GetEntryAttribute*` helper methods
- [ ] **2.3** Add tests for `GetLdapAttributeDataType()` including edge cases
- [ ] **2.4** Add tests for `GetPaginationTokenName()`
- [ ] **2.5** Create `JIM.Worker.Tests/Connectors/LdapConnectorTests.cs`
- [ ] **2.6** Add test for `GetSettings()` returns expected settings
- [ ] **2.7** Add test for `ValidateSettingValues()` with missing values

### Phase 3: Code Quality (Priority: Medium)

- [ ] **3.1** Use `GetLdapAttributeDataType()` utility in schema discovery (remove duplication)
- [ ] **3.2** Add missing omSyntax case 4 to utility method
- [ ] **3.3** Add "Search Timeout" configurable setting
- [ ] **3.4** Add constant for systemFlags=3 with documentation
- [ ] **3.5** Standardise string comparisons to `OrdinalIgnoreCase`
- [ ] **3.6** Remove or document commented-out code

### Phase 4: Export Implementation (Priority: MVP)

- [ ] **4.1** Design export capability (create/update/delete in AD)
- [ ] **4.2** Add export settings to `GetSettings()`
- [ ] **4.3** Create `LdapConnectorExport.cs` class
- [ ] **4.4** Implement `IConnectorExportUsingCalls` interface
- [ ] **4.5** Implement object creation in directory
- [ ] **4.6** Implement object modification in directory
- [ ] **4.7** Implement object deletion/disable in directory
- [ ] **4.8** Add export tests

### Phase 5: Enhancements (Priority: Future)

- [ ] **5.1** Implement delta import using USN/changelog
- [ ] **5.2** Add LDAPS (secure connection) support
- [ ] **5.3** Add connection pooling for better performance
- [ ] **5.4** Add retry logic for transient failures

---

## Detailed Implementation Notes

### Phase 1.1: IDisposable Implementation

**File:** `JIM.Connectors/LDAP/LdapConnector.cs`

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

**File:** `JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs`

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
| `JIM.Connectors/LDAP/LdapConnector.cs` | Add IDisposable, pass logger to helpers |
| `JIM.Connectors/LDAP/LdapConnectorSchema.cs` | Accept logger, replace Console.WriteLine |
| `JIM.Connectors/LDAP/LdapConnectorPartitions.cs` | Accept logger, replace Console.WriteLine |
| `JIM.Connectors/LDAP/LdapConnectorImport.cs` | Replace static Log, add null checks |
| `JIM.Connectors/LDAP/LdapConnectorUtilities.cs` | Add missing omSyntax case 4 |
| `JIM.Worker.Tests/Connectors/LdapConnectorUtilitiesTests.cs` | New file |
| `JIM.Worker.Tests/Connectors/LdapConnectorTests.cs` | New file |

---

## Acceptance Criteria

### Phase 1 Complete When:
- [ ] `LdapConnector` implements `IDisposable`
- [ ] No `Console.WriteLine` calls remain
- [ ] No static `Log` references remain
- [ ] Null checks added to `GetRootDseInformation()`
- [ ] Build passes with no new warnings
- [ ] All existing tests pass

### Phase 2 Complete When:
- [ ] `LdapConnectorUtilities` has >80% test coverage
- [ ] Settings validation logic tested
- [ ] At least 10 unit tests added

### Phase 3 Complete When:
- [ ] No duplicate code for omSyntax mapping
- [ ] Search timeout is configurable
- [ ] String comparisons are consistent
- [ ] No unexplained magic numbers

### Phase 4 Complete When:
- [ ] Export to AD works for create operations
- [ ] Export to AD works for update operations
- [ ] Export to AD works for delete/disable operations
- [ ] MVP checklist item "LDAP/Active Directory Connector - Export" is complete

---

## References

- [LdapConnector.cs](../JIM.Connectors/LDAP/LdapConnector.cs)
- [LdapConnectorImport.cs](../JIM.Connectors/LDAP/LdapConnectorImport.cs)
- [LdapConnectorSchema.cs](../JIM.Connectors/LDAP/LdapConnectorSchema.cs)
- [LdapConnectorPartitions.cs](../JIM.Connectors/LDAP/LdapConnectorPartitions.cs)
- [LdapConnectorUtilities.cs](../JIM.Connectors/LDAP/LdapConnectorUtilities.cs)
- [MVP Definition](./MVP_DEFINITION.md) - See "LDAP/Active Directory Connector - Export"
- [System.DirectoryServices.Protocols Documentation](https://learn.microsoft.com/en-us/dotnet/api/system.directoryservices.protocols)
