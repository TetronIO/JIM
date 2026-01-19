# Case Sensitivity Strategy

> **Status**: Complete
> **Milestone**: MVP
> **Related Issue**: #203 (Phase 8)
> **Branch**: `feature/case-sensitivity-strategy`

## Overview

This document defines JIM's consistent approach to string comparisons across all components. The strategy balances data integrity (case-sensitive by default) with user flexibility (configurable where appropriate).

## Core Principles

### 1. Default: Case-Sensitive

All string comparisons that affect data flow are **case-sensitive by default**. This ensures:

- **Data fidelity**: Changes in case from source systems are detected and propagated
- **External system respect**: If a connected system distinguishes "John123" from "JOHN123", JIM respects that
- **Predictable behaviour**: Users can rely on exact matching unless they explicitly choose otherwise

### 2. User-Configurable Where It Matters

For operations where users may want flexibility, provide a toggle:

- **Matching Rules**: User can choose case-sensitive or case-insensitive matching per rule
- **Scoping Criteria**: User can choose case-sensitive or case-insensitive evaluation per criterion
- **Attribute Flow Rules**: Future consideration for transformation-based comparisons

### 3. Standard StringComparison Type

Standardise on `StringComparison.Ordinal` (case-sensitive) and `StringComparison.OrdinalIgnoreCase` (case-insensitive):

- **Fastest performance** - byte-by-byte comparison without cultural rules
- **Culture-independent** - consistent behaviour regardless of server locale
- **Predictable** - no surprises from cultural sorting rules

**Never use:**
- `CurrentCultureIgnoreCase` - unpredictable across deployments
- `InvariantCultureIgnoreCase` - unnecessary overhead for identity data
- `.ToLower()/.ToUpper()` patterns - inefficient and creates string allocations

### 4. Database-Level Consistency

PostgreSQL default collation is case-sensitive, which aligns with our default. For customers requiring system-wide case-insensitivity:

- PostgreSQL supports collation configuration at database, table, or column level
- Use `COLLATE "und-x-icu"` with `ILIKE` for case-insensitive queries when needed
- Document how to change collation for edge cases

## Comparison Categories

### Category 1: Data Flow Decisions (CRITICAL)

These comparisons determine whether data changes, objects link, or provisioning occurs.

| Operation | Default | Configurable | Implementation |
|-----------|---------|--------------|----------------|
| Attribute value change detection | Case-sensitive | No (system-wide) | `StringComparison.Ordinal` |
| External ID matching | Case-sensitive | No | `StringComparison.Ordinal` |
| Object matching rules | Case-sensitive | **Yes (per rule)** | Add `CaseSensitive` property |
| Scope filter evaluation | Case-sensitive | **Yes (per criterion)** | Add `CaseSensitive` property |
| Reference value matching | Case-sensitive | No | `StringComparison.Ordinal` |

### Category 2: Schema/Identifier Lookups

These comparisons find configuration elements by name.

| Operation | Behaviour | Rationale |
|-----------|-----------|-----------|
| Attribute name lookups | Case-insensitive | Schema names should be forgiving |
| Connected system name lookups | Case-insensitive | Configuration names should be forgiving |
| Object type name lookups | Case-insensitive | Configuration names should be forgiving |
| API endpoint routing | Case-insensitive | HTTP standard |

**Implementation**: Use `StringComparison.OrdinalIgnoreCase` for all schema/identifier lookups.

### Category 3: Search/Display (LOW IMPACT)

These comparisons affect UI search and filtering only.

| Operation | Behaviour | Rationale |
|-----------|-----------|-----------|
| Admin search | Case-insensitive | User convenience |
| UI filtering | Case-insensitive | User convenience |
| Log searching | Case-insensitive | User convenience |

**Implementation**: Use `EF.Functions.ILike()` for database queries, `StringComparison.OrdinalIgnoreCase` for in-memory.

## Data Model Changes

### ObjectMatchingRule

Add case sensitivity control to matching rules:

```csharp
public class ObjectMatchingRule
{
    // Existing properties...

    /// <summary>
    /// When true (default), attribute value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;
}
```

### ObjectScopingCriteria

Add case sensitivity control to scoping criteria:

```csharp
public class ObjectScopingCriteria
{
    // Existing properties...

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;
}
```

## Code Changes Required

### Phase 1: Standardise StringComparison (Immediate)

Fix inconsistent comparison methods across the codebase:

| File | Line(s) | Current | Change To |
|------|---------|---------|-----------|
| `SyncImportTaskProcessor.cs` | 545, 666, 667 | `CurrentCultureIgnoreCase` | `OrdinalIgnoreCase` |
| `SyncImportTaskProcessor.cs` | 724, 728 | `InvariantCultureIgnoreCase` | `Ordinal` (data flow) |
| `ServiceSettingsRepository.cs` | 25 | `CurrentCultureIgnoreCase` | `OrdinalIgnoreCase` |
| `ConnectedSystemRepository.cs` | 362, 517, 544, 960, 961, 989, 995, 1035 | `.ToLower() ==` | `StringComparison.Ordinal` or `OrdinalIgnoreCase` |
| `MetaverseRepository.cs` | 368 | `.ToLower().Contains()` | `EF.Functions.ILike()` |
| `ConnectedSystemObject.cs` | 235, 241 | `InvariantCultureIgnoreCase` | `OrdinalIgnoreCase` |

### Phase 2: Make External ID Matching Case-Sensitive

Update repository methods to use exact matching:

```csharp
// ConnectedSystemRepository.cs - GetConnectedSystemObjectByAttributeAsync
// Change from:
av.StringValue.ToLower() == attributeValue.ToLower()

// To:
av.StringValue == attributeValue
```

### Phase 3: Add Configurable Case Sensitivity

1. Add `CaseSensitive` property to `ObjectMatchingRule` model
2. Add `CaseSensitive` property to `ObjectScopingCriteria` model
3. Create database migration
4. Update `ScopingEvaluationServer` to respect the setting
5. Update matching rule evaluation to respect the setting
6. Update API DTOs and PowerShell cmdlets
7. Update UI to show toggle

### Phase 4: Documentation

1. Add case sensitivity section to Developer Guide
2. Update API documentation
3. Add notes to sync rule configuration UI
4. Document PostgreSQL collation options for edge cases

## PostgreSQL Collation (Edge Case Support)

For customers requiring system-wide case-insensitive behaviour (similar to SQL Server collation changes in traditional ILM solutions):

```sql
-- Create database with case-insensitive collation
CREATE DATABASE jim WITH
    ENCODING = 'UTF8'
    LC_COLLATE = 'en_US.UTF-8'
    LC_CTYPE = 'en_US.UTF-8'
    TEMPLATE = template0;

-- Or use ICU collation for specific columns
ALTER TABLE "MetaverseObjectAttributeValues"
    ALTER COLUMN "StringValue"
    SET DATA TYPE text COLLATE "und-x-icu";
```

**Note**: This is an advanced configuration option and should be documented as such. Most deployments should use the default case-sensitive behaviour with per-rule configuration where needed.

## API Changes

### ObjectMatchingRule Endpoints

Update `ObjectMatchingRuleDto` to include:

```csharp
public class ObjectMatchingRuleDto
{
    // Existing properties...
    public bool CaseSensitive { get; set; } = true;
}
```

### ObjectScopingCriteria Endpoints

Update `ObjectScopingCriteriaDto` to include:

```csharp
public class ObjectScopingCriteriaDto
{
    // Existing properties...
    public bool CaseSensitive { get; set; } = true;
}
```

## PowerShell Changes

### New-JIMMatchingRule / Set-JIMMatchingRule

Add `-CaseSensitive` parameter (default: `$true`):

```powershell
New-JIMMatchingRule -SyncRuleId $ruleId `
    -SourceAttribute "employeeId" `
    -TargetAttribute "employeeID" `
    -CaseSensitive $false
```

### New-JIMScopingCriteria / Set-JIMScopingCriteria

Add `-CaseSensitive` parameter (default: `$true`):

```powershell
New-JIMScopingCriteria -SyncRuleId $ruleId `
    -Attribute "department" `
    -Operator "Equals" `
    -Value "Sales" `
    -CaseSensitive $false
```

## Testing Requirements

### Unit Tests

1. Attribute value change detection with case differences
2. External ID matching with case differences
3. Matching rule evaluation with `CaseSensitive = true` and `false`
4. Scoping criteria evaluation with `CaseSensitive = true` and `false`

### Integration Tests

1. Sync scenario where source changes case of attribute value
2. Matching rule join with case-insensitive matching
3. Scoping filter with case-insensitive evaluation
4. Mixed case sensitivity across multiple sync rules

## Success Criteria

- [x] All `CurrentCultureIgnoreCase` and `InvariantCultureIgnoreCase` replaced with appropriate `Ordinal`/`OrdinalIgnoreCase`
- [x] All `.ToLower()/.ToUpper()` patterns replaced with `StringComparison` parameters
- [x] External ID matching is case-sensitive
- [x] Attribute value change detection is case-sensitive
- [x] Matching rules support configurable case sensitivity
- [x] Scoping criteria support configurable case sensitivity
- [x] Unit tests cover case sensitivity scenarios
- [x] Integration tests verify end-to-end behaviour
- [x] Documentation updated

## Implementation Summary

### Completed Changes

| Task | Files Modified | Description |
|------|----------------|-------------|
| Standardise StringComparison | `SyncImportTaskProcessor.cs`, `ServiceSettingsRepository.cs`, `ConnectedSystemObject.cs` | Changed `CurrentCultureIgnoreCase` -> `OrdinalIgnoreCase`, `InvariantCultureIgnoreCase` -> `Ordinal` |
| External ID case-sensitive | `ConnectedSystemRepository.cs` | Removed `.ToLower()` patterns from external ID lookups |
| ObjectMatchingRule.CaseSensitive | `ObjectMatchingRule.cs` | Added property with `true` default |
| SyncRuleScopingCriteria.CaseSensitive | `SyncRuleScopingCriteria.cs` | Added property with `true` default |
| ScopingEvaluationServer | `ScopingEvaluationServer.cs` | Updated `EvaluateStringComparison` to use `caseSensitive` parameter |
| Matching rule evaluation | `MetaverseRepository.cs`, `ConnectedSystemRepository.cs` | Updated `FindMetaverseObjectUsingMatchingRuleAsync` and `FindConnectedSystemObjectUsingMatchingRuleAsync` to respect `CaseSensitive` |
| Database migration | `20251221185120_AddCaseSensitiveToMatchingAndScopingRules.cs` | Added columns with `defaultValue: true` |
| API DTOs | `ObjectMatchingRuleDtos.cs`, `ObjectMatchingRuleRequestDtos.cs`, `SyncRuleScopingCriteriaDtos.cs` | Added `CaseSensitive` property |
| Controller updates | `SynchronisationController.cs` | Updated create/update endpoints |
| Unit tests | `ScopingEvaluationTests.cs` | Tests for scoping with case-sensitive and case-insensitive modes |

## Benefits

1. **Predictable behaviour**: Users know what to expect (case-sensitive by default)
2. **Data integrity**: Case changes from source systems are detected and propagated
3. **Flexibility**: Users can opt into case-insensitive matching where needed
4. **Performance**: `Ordinal` comparisons are fastest
5. **Consistency**: Single approach across the entire codebase
