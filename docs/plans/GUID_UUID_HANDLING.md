# GUID/UUID Handling Strategy

> **Status**: Phases 1-3 Complete
> **Last Updated**: 2026-01-28
> **Milestone**: Pre-connector expansion (before SCIM, database, or web service connectors)

## Overview

JIM imports and exports identity data from multiple source and target systems spanning both Windows and Linux platforms. These systems may provide or expect identifiers as either GUIDs (Microsoft terminology) or UUIDs (RFC 4122 terminology). While the string representations are identical and interchangeable, the **binary representations differ in byte ordering**, which can cause identifier corruption if mishandled.

A code review of the current codebase found **no active data corruption risks** but identified gaps that will become risks as planned connectors are implemented.

---

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Current State](#current-state)
3. [Code Review Findings](#code-review-findings)
4. [Implementation Plan](#implementation-plan)
5. [Success Criteria](#success-criteria)

---

## Problem Statement

### The Byte Order Problem

GUIDs and UUIDs are both 128-bit identifiers, but their binary (byte array) representations use different endianness for the first three components:

| Component | RFC 4122 (Linux/POSIX) | Microsoft GUID |
|-----------|------------------------|----------------|
| time_low (4 bytes) | Big-endian | Little-endian |
| time_mid (2 bytes) | Big-endian | Little-endian |
| time_hi_version (2 bytes) | Big-endian | Little-endian |
| clock_seq + node (8 bytes) | Big-endian | Big-endian |

The same logical identifier `550e8400-e29b-41d4-a716-446655440000` has different byte sequences depending on the source platform. .NET's `Guid` struct uses Microsoft byte order internally, so `new Guid(byte[])` and `Guid.ToByteArray()` only produce correct results for Microsoft-sourced bytes.

### Why This Matters for JIM

JIM's current connectors (Active Directory/Samba AD LDAP, CSV files) are safe because:

- AD `objectGUID` uses Microsoft byte order, matching .NET's `Guid` struct
- CSV files exchange GUIDs as strings (no binary ambiguity)

However, planned connectors introduce systems that use RFC 4122 byte order or non-UUID identifiers:

| Planned Connector | Identifier Risks |
|-------------------|-----------------|
| SCIM 2.0 | `id` is not necessarily a UUID; `externalId` is opaque string |
| OpenLDAP/389DS | `entryUUID` is a string (safe), but custom binary UUID attributes use RFC 4122 byte order |
| SQL Server | `uniqueidentifier` uses Microsoft byte order (safe) |
| PostgreSQL (direct) | `uuid` uses RFC 4122 byte order |
| MySQL | `CHAR(36)` (string, safe) or `BINARY(16)` (byte order varies) |
| Oracle | `RAW(16)` uses big-endian (RFC 4122-like) |

---

## Current State

### What Works Well

1. **Production code exclusively uses `Guid.TryParse()`** - zero `Guid.Parse()` calls in production
2. **LDAP `objectGUID` handling is correct** - `new Guid(byte[])` matches AD's Microsoft byte order
3. **CSV connector uses string exchange** - no binary ambiguity
4. **PostgreSQL/EF Core handled transparently** - Npgsql converts Guid to/from `uuid` type correctly
5. **API JSON serialisation uses System.Text.Json defaults** - standard hyphenated string format
6. **Directory type detection is robust** - RootDSE capability OID checks distinguish AD from non-AD

### Current GUID Flow

```
+-------------------+      +----------------+      +-------------------+
|  LDAP (AD/Samba)  |      |   JIM Core     |      |  LDAP (AD/Samba)  |
|                   |      |                |      |                   |
|  objectGUID       |----->|  Guid struct   |----->|  ToByteArray()    |
|  byte[] (MS order)|      |  (MS internal) |      |  byte[] (MS order)|
+-------------------+      +-------+--------+      +-------------------+
                                   |
        +-------------------+      |      +-------------------+
        |  CSV File         |      |      |  CSV File         |
        |                   |<-----+----->|                   |
        |  String format    |             |  ToString()       |
        +-------------------+             +-------------------+
```

### Key Code Locations

| Component | File | Key Lines |
|-----------|------|-----------|
| GUID from LDAP binary (multi-value) | `src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs` | 49 |
| GUID from LDAP binary (single-value) | `src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs` | 76 |
| GUID from LDAP after create | `src/JIM.Connectors/LDAP/LdapConnectorExport.cs` | 244 |
| GUID to LDAP binary for export | `src/JIM.Connectors/LDAP/LdapConnectorExport.cs` | 608 |
| objectGUID schema override | `src/JIM.Connectors/LDAP/LdapConnectorSchema.cs` | 203-205 |
| CSV GUID import (multi-value) | `src/JIM.Connectors/File/FileConnectorImport.cs` | 196 |
| CSV GUID import (single-value) | `src/JIM.Connectors/File/FileConnectorImport.cs` | 204 |
| CSV GUID export | `src/JIM.Connectors/File/FileConnectorExport.cs` | 194 |
| CSV schema inference | `src/JIM.Connectors/File/FileConnector.cs` | 354 |
| Directory type detection | `src/JIM.Connectors/LDAP/LdapConnectorImport.cs` | 298-312 |
| Claims GUID parsing | `src/JIM.Utilities/IdentityUtilities.cs` | 16, 32 |
| API query filtering | `src/JIM.Web/Extensions/Api/QueryableExtensions.cs` | 161 |

---

## Code Review Findings

### Issues Found

| # | Severity | Category | Finding |
|---|----------|----------|---------|
| L1 | Medium | LDAP | No `entryUUID` support for OpenLDAP. The connector only handles AD's binary `objectGUID`. OpenLDAP uses `entryUUID` (RFC 4530, string format). |
| L2 | Low | LDAP | No byte order documentation on `GetEntryAttributeGuidValue()` / `GetEntryAttributeGuidValues()`. A future developer could misuse these for non-AD binary UUID attributes. |
| L3 | Low | LDAP | Export `GetAttributeValue()` calls `ToByteArray()` without confirming the target expects Microsoft byte order. Currently safe (AD/Samba only) but fragile for future OpenLDAP export. |
| F1 | Low | CSV | Inconsistent parsing: single-valued GUIDs use `CsvReader.GetField<Guid>()` while multi-valued use `Guid.TryParse()`. Different error messages for identical failures. |
| F2 | Low | CSV | No Base64-encoded GUID handling. Edge case but should be documented. |
| A1 | Info | Architecture | No central `IdentifierParser` utility. GUID parsing is inline throughout the codebase. |
| A2 | Info | Architecture | No round-trip unit tests verifying GUIDs survive import-store-export across connector combinations. |

### Patterns Confirmed Safe

- All `SequenceEqual` byte array comparisons are for generic binary data, not cross-source GUID matching
- All `new Guid(byte[])` calls correctly operate on AD binary data
- PostgreSQL `uuid` columns handled transparently by Npgsql
- JSON serialisation uses default System.Text.Json (standard hyphenated format)

---

## Implementation Plan

### Phase 1: Documentation and Defensive Comments (Do Now)

Add byte order documentation to existing code. No functional changes.

**1.1 Add XML doc remarks to LDAP utility methods**

File: `src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs`

Add `<remarks>` to `GetEntryAttributeGuidValue()` and `GetEntryAttributeGuidValues()` documenting that they assume Microsoft GUID byte order (little-endian first 3 components) and are safe for Active Directory and Samba AD only.

**1.2 Add comment to LDAP export GUID conversion**

File: `src/JIM.Connectors/LDAP/LdapConnectorExport.cs`

Add comment at line 608 noting that `ToByteArray()` produces Microsoft byte order, correct for AD/Samba targets.

**1.3 Add comment to LDAP export objectGUID fetch**

File: `src/JIM.Connectors/LDAP/LdapConnectorExport.cs`

Add comment at line 244 noting that `new Guid(guidBytes)` expects Microsoft byte order from AD.

---

### Phase 2: Central Identifier Utility (Before New Connectors)

Create a shared utility class that all connectors use for GUID/UUID operations.

**2.1 Create `IdentifierParser` utility class**

File: `src/JIM.Utilities/IdentifierParser.cs`

Methods:

```
FromString(string value) -> Guid
  - Handles standard, braced, no-hyphens, URN formats
  - Trims whitespace
  - Throws ArgumentException with context on failure

TryFromString(string value, out Guid result) -> bool
  - Safe variant of FromString

FromMicrosoftBytes(byte[] bytes) -> Guid
  - For AD objectGUID, SQL Server uniqueidentifier
  - Validates 16-byte length

FromRfc4122Bytes(byte[] bytes) -> Guid
  - For OpenLDAP binary UUIDs, PostgreSQL binary, Oracle RAW(16)
  - Swaps byte order for first 3 components

ToRfc4122Bytes(Guid guid) -> byte[]
  - Converts .NET Guid to RFC 4122 binary for non-Microsoft targets

ToAdLdapFilterString(Guid guid) -> string
  - Formats GUID for AD LDAP search filter: \xx\xx\xx...

Normalise(string value) -> string
  - Parses and re-emits in canonical format (lowercase, hyphenated)
```

**2.2 Unit tests for `IdentifierParser`**

File: `test/JIM.Models.Tests/Utilities/IdentifierParserTests.cs`

Test cases:
- String round-trip: standard, braced, no-hyphens, URN, mixed case
- Byte order round-trip: Microsoft bytes -> Guid -> Microsoft bytes
- Byte order round-trip: RFC 4122 bytes -> Guid -> RFC 4122 bytes
- Cross-format: same logical GUID parsed from Microsoft bytes and RFC 4122 bytes produces equal Guid
- AD LDAP filter string: output matches expected escaped byte format
- Edge cases: null, empty, whitespace, wrong length byte arrays
- Normalise: all accepted formats produce identical canonical output

---

### Phase 3: Migrate Existing Code to Central Utility (Before New Connectors)

Replace inline GUID operations with `IdentifierParser` calls. Functional behaviour unchanged.

**3.1 LDAP connector import**

Replace `new Guid(byteValue)` calls in `LdapConnectorUtilities.cs` with `IdentifierParser.FromMicrosoftBytes(byteValue)`.

**3.2 LDAP connector export**

Replace `attrChange.GuidValue.Value.ToByteArray()` in `LdapConnectorExport.cs` with `IdentifierParser` method. Initially this still calls `Guid.ToByteArray()` (Microsoft byte order), but the method name makes the byte order assumption explicit. When OpenLDAP export support is added, the export path can switch to `ToRfc4122Bytes()` based on directory type.

Replace `new Guid(guidBytes)` in `FetchObjectGuid()` with `IdentifierParser.FromMicrosoftBytes(guidBytes)`.

**3.3 CSV connector**

Consider replacing `CsvReader.GetField<Guid>()` with `IdentifierParser.TryFromString()` for single-valued GUIDs. This aligns error handling with the multi-valued path.

---

### Phase 4: OpenLDAP/entryUUID Support (When OpenLDAP Export Needed)

**4.1 Add `entryUUID` attribute handling to LDAP import**

Detect non-AD directories (already done via RootDSE) and read `entryUUID` as a string attribute using `Guid.TryParse()` or `IdentifierParser.FromString()` instead of binary conversion.

**4.2 Add byte order awareness to LDAP export**

When exporting GUID-type attributes to non-AD directories, use `IdentifierParser.ToRfc4122Bytes()` instead of `Guid.ToByteArray()`. Determine which method to use based on the directory type detected during connection.

**4.3 Add `GuidByteOrder` metadata to connector configuration**

```
public enum GuidByteOrder
{
    String,           // No binary handling needed (CSV, SCIM, most APIs)
    MicrosoftNative,  // AD, SQL Server
    Rfc4122           // OpenLDAP, PostgreSQL binary, Oracle
}
```

Store as connector-level metadata so the export path knows which byte order to use without per-attribute decisions.

---

### Phase 5: Cross-Connector Round-Trip Tests (Before New Connectors)

**5.1 Add round-trip integration tests**

Verify that a GUID imported from one connector type survives storage in JIM and export to another connector type:

- LDAP import (binary) -> store in PostgreSQL -> CSV export (string) -> CSV import (string) -> LDAP export (binary): original bytes preserved
- Known GUID value: verify specific byte sequences at each stage

**5.2 Add SCIM identifier tests (when SCIM connector built)**

- SCIM `id` stored as opaque string (not forced to Guid)
- SCIM `externalId` populated with JIM's MVO ID
- Non-UUID SCIM `id` values handled without exceptions
- UUID SCIM `id` values optionally parsed for correlation

---

## Success Criteria

1. All `new Guid(byte[])` and `Guid.ToByteArray()` calls have documented byte order assumptions
2. `IdentifierParser` utility exists with comprehensive unit tests
3. All connector code uses `IdentifierParser` instead of inline GUID operations
4. GUID round-trip tests pass across all connector combinations
5. OpenLDAP `entryUUID` is supported when that connector path is implemented
6. No GUID-related data corruption when mixing connector types

---

## Benefits

- **Data integrity**: Explicit byte order handling prevents silent identifier corruption
- **Developer safety**: Central utility with clear method names eliminates guesswork
- **Connector extensibility**: New connectors can specify their byte order without modifying core code
- **Auditability**: Consistent normalisation makes GUID comparison and logging reliable
- **Test confidence**: Round-trip tests catch byte order regressions automatically

---

## References

- RFC 4122: A Universally Unique IDentifier (UUID) URN Namespace
- RFC 7643: System for Cross-domain Identity Management (SCIM) Core Schema
- RFC 7644: System for Cross-domain Identity Management (SCIM) Protocol
- RFC 4530: LDAP entryUUID Operational Attribute
- Microsoft Docs: Guid.ToByteArray() - documents Microsoft byte order
- .NET Source: Guid struct uses little-endian for first 3 components
