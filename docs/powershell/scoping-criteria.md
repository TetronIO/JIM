---
title: Scoping Criteria
description: "The cmdlets that list, create, change and delete the groups and criteria deciding which objects a Synchronisation Rule processes."
type: reference
audience: automation-author
---

# Scoping Criteria

The scoping criteria cmdlets decide which objects a Synchronisation Rule processes. A rule's criteria sit in groups: an object is in scope when any one of the rule's top-level groups is met, and a group combines the criteria and child groups inside it with All, where every one must be met, or Any, where one is enough. A rule with no criteria processes every object of its type. An import rule's criteria test Connected System attributes, and an export rule's test Metaverse attributes. How criteria are evaluated, including missing values, multi-valued attributes and relative dates, is covered in [Scoping Criteria](../configuration/scoping-criteria.md).

---

## Get-JIMScopingCriteria

Gets a Synchronisation Rule's scoping criteria: its top-level groups, each with the criteria and child groups inside it, or one group by ID.

<!-- BEGIN GENERATED: Get-JIMScopingCriteria -->
### Syntax

```powershell
Get-JIMScopingCriteria -SyncRuleId <int> [-GroupId <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `GroupId` | `int` | No |  | The ID of one group to get, at any depth. Without it, every top-level group comes back. |

### Output

By default, each top-level group, with the criteria and child groups inside it. With `-GroupId`, that group and what it contains.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the group. |
| `Type` | `string` | How the group combines what it contains: All is met when every criterion and child group in it is met, Any when at least one is. |
| `Position` | `int` | Where the group sits among the groups beside it, used to order them; it does not change which objects are in scope. |
| `Criteria` | `object[]` | The criteria in the group. Each has `Id`, `MetaverseAttributeId`, `MetaverseAttributeName`, `ConnectedSystemAttributeId`, `ConnectedSystemAttributeName`, `Expression`, `CompareAs`, `AttributeDataType`, `Notice`, `ComparisonType`, `StringValue`, `IntValue`, `LongValue`, `DecimalValue`, `DateTimeValue`, `BoolValue`, `GuidValue`, `CaseSensitive`, `ValueMode`, `RelativeCount`, `RelativeUnit` and `RelativeDirection`. |
| `ChildGroups` | `object[]` | The groups inside this one, each with what it contains. Each has `Id`, `Type`, `Position`, `Criteria` and `ChildGroups`. |
| `SyncRuleId` | `int` | The ID of the Synchronisation Rule the group belongs to, so the group can be piped to the other scoping criteria cmdlets. |
<!-- END GENERATED: Get-JIMScopingCriteria -->

### Examples

```powershell title="List all scoping criteria for a Synchronisation Rule"
Get-JIMScopingCriteria -SyncRuleId 5
```

```powershell title="Get a specific group"
Get-JIMScopingCriteria -SyncRuleId 5 -GroupId 2
```

```powershell title="Get the criteria of a Synchronisation Rule from the pipeline"
Get-JIMSyncRule -Id 5 | Get-JIMScopingCriteria
```

---

## New-JIMScopingCriteriaGroup

Creates a scoping criteria group on a Synchronisation Rule, at the top level or inside another group. A group is met when everything in it is (`All`), or when one thing in it is (`Any`).

<!-- BEGIN GENERATED: New-JIMScopingCriteriaGroup -->
### Syntax

```powershell
New-JIMScopingCriteriaGroup -SyncRuleId <int> [-ParentGroupId <int>] [-Type <string>] [-Position <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `ParentGroupId` | `int` | No |  | The ID of the group to create this one inside. Without it, the group is created at the top level. |
| `Type` | `string` | No | `'All'` | How the group combines what it contains: All is met when every criterion and child group in it is met, Any when at least one is. One of `All` or `Any`. |
| `Position` | `int` | No | `0` | Where the group sits among the groups beside it, used to order them. It does not change which objects are in scope. |
| `PassThru` | `switch` | No |  | Returns the group created. |

### Output

With `-PassThru`, the group created; otherwise nothing.

Properties as under [Get-JIMScopingCriteria](#get-jimscopingcriteria).
<!-- END GENERATED: New-JIMScopingCriteriaGroup -->

### Examples

```powershell title="Create a top-level All group"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -Type All -PassThru
```

```powershell title="Create an Any group inside an existing group"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -ParentGroupId 2 -Type Any
```

```powershell title="Create an All group at a specific position"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -Type All -Position 1
```

---

## Set-JIMScopingCriteriaGroup

Changes how a scoping criteria group combines what it contains, or where it sits among the groups beside it.

<!-- BEGIN GENERATED: Set-JIMScopingCriteriaGroup -->
### Syntax

```powershell
Set-JIMScopingCriteriaGroup -SyncRuleId <int> -GroupId <int> [-Type <string>] [-Position <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the group to change. Accepts pipeline input by property name, `GroupId` or `Id`. |
| `Type` | `string` | No |  | How the group combines what it contains: All is met when every criterion and child group in it is met, Any when at least one is. One of `All` or `Any`. |
| `Position` | `int` | No |  | Where the group sits among the groups beside it, used to order them. It does not change which objects are in scope. |
| `PassThru` | `switch` | No |  | Returns the group as changed. |

### Output

With `-PassThru`, the group as changed, with what it contains; otherwise nothing.

Properties as under [Get-JIMScopingCriteria](#get-jimscopingcriteria).
<!-- END GENERATED: Set-JIMScopingCriteriaGroup -->

### Examples

```powershell title="Change a group from All to Any"
Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2 -Type Any
```

```powershell title="Reorder a group"
Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2 -Position 3
```

---

## Remove-JIMScopingCriteriaGroup

Deletes a scoping criteria group, with every criterion and child group inside it. It cannot be undone.

<!-- BEGIN GENERATED: Remove-JIMScopingCriteriaGroup -->
### Syntax

```powershell
Remove-JIMScopingCriteriaGroup -SyncRuleId <int> -GroupId <int> [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the group to delete. Accepts pipeline input by property name, `GroupId` or `Id`. |

### Output

None.
<!-- END GENERATED: Remove-JIMScopingCriteriaGroup -->

### Examples

```powershell title="Delete a scoping criteria group"
Remove-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2
```

---

## New-JIMScopingCriterion

Adds a criterion to a scoping criteria group: one attribute compared with one value. Name a Metaverse attribute on an export Synchronisation Rule, and a Connected System attribute on an import one.

<!-- BEGIN GENERATED: New-JIMScopingCriterion -->
### Syntax

```powershell
# ByMvId (default)
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -MetaverseAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByMvName
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -MetaverseAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByCsId
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -ConnectedSystemAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByCsName
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -ConnectedSystemAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByExpression
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -Expression <string> -CompareAs <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the group to add the criterion to. Accepts pipeline input by property name. |
| `MetaverseAttributeId` | `int` | Yes (ByMvId) |  | The ID of the Metaverse attribute to test. An export Synchronisation Rule's criteria test Metaverse attributes. |
| `MetaverseAttributeName` | `string` | Yes (ByMvName) |  | The name of the Metaverse attribute to test, instead of its ID. |
| `ConnectedSystemAttributeId` | `int` | Yes (ByCsId) |  | The ID of the Connected System attribute to test. An import Synchronisation Rule's criteria test Connected System attributes. |
| `ConnectedSystemAttributeName` | `string` | Yes (ByCsName) |  | The name of the Connected System attribute to test, instead of its ID. |
| `Expression` | `string` | Yes (ByExpression) |  | An expression whose computed value the criterion compares, in place of an attribute. It reads the object the Synchronisation Rule scopes: cs["name"] on an import rule, mv["Name"] on an export rule. Use it to compare a value reshaped first, such as Lower(Trim(cs["department"])). Requires `-CompareAs`. |
| `CompareAs` | `string` | Yes (ByExpression) |  | The type the expression's computed value is compared as, which decides the comparisons and the value parameter that apply, as an attribute's own type does. A value the expression computes that cannot be read as this type stops that object with an error rather than deciding its scope. One of `Text`, `Number`, `LongNumber`, `Decimal`, `DateTime`, `Boolean` or `Guid`. |
| `ComparisonType` | `string` | Yes |  | How the attribute is compared with the value. The Not forms are met when the attribute holds a value and none matches; the others when any value does. HasValue and HasNoValue test whether the attribute holds a value at all and take no value. MatchesPattern and NotMatchesPattern take a .NET regular expression in `-StringValue`, for Text attributes. One of `Equals`, `NotEquals`, `StartsWith`, `NotStartsWith`, `EndsWith`, `NotEndsWith`, `Contains`, `NotContains`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `HasValue`, `HasNoValue`, `MatchesPattern` or `NotMatchesPattern`. |
| `StringValue` | `string` | No |  | The value to compare a Text attribute with, or the regular expression for MatchesPattern and NotMatchesPattern. |
| `IntValue` | `int` | No |  | The value to compare a Number attribute with. |
| `LongValue` | `long` | No |  | The value to compare a LongNumber attribute with. |
| `DecimalValue` | `decimal` | No |  | The value to compare a Decimal attribute with. For a high-precision value, use the d suffix (123.456789012345678d) or a quoted string: a bare number is read as a double first, and can lose precision. |
| `DateTimeValue` | `datetime` | No |  | The date and time to compare a DateTime attribute with, when `-ValueMode` is Absolute. |
| `BoolValue` | `bool` | No |  | The value to compare a Boolean attribute with. |
| `GuidValue` | `guid` | No |  | The value to compare a Guid attribute with. |
| `CaseSensitive` | `bool` | No |  | Whether a Text comparison respects case: `$false` ignores it. Case-sensitive when omitted. |
| `ValueMode` | `string` | No |  | For a DateTime attribute: Absolute compares with `-DateTimeValue`, and Relative with a date worked out from now each time the rule is evaluated, set by `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection`. Relative cannot be used with `-DateTimeValue`. Absolute when omitted. One of `Absolute` or `Relative`. |
| `RelativeCount` | `int` | No |  | How many units from now the date is, zero or more. Used with `-ValueMode` Relative. |
| `RelativeUnit` | `string` | No |  | The unit `-RelativeCount` counts. Used with `-ValueMode` Relative. One of `Hours`, `Days`, `Weeks`, `Months` or `Years`. |
| `RelativeDirection` | `string` | No |  | Whether the date is in the past (Ago) or the future (FromNow). Used with `-ValueMode` Relative. One of `Ago` or `FromNow`. |
| `PassThru` | `switch` | No |  | Returns the criterion created. |

### Output

With `-PassThru`, the criterion created; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the criterion. |
| `MetaverseAttributeId` | `int` | The ID of the Metaverse attribute the criterion tests, on an export Synchronisation Rule's criterion; empty on an import one. |
| `MetaverseAttributeName` | `string` | The name of the Metaverse attribute the criterion tests; empty on an import Synchronisation Rule's criterion. |
| `ConnectedSystemAttributeId` | `int` | The ID of the Connected System attribute the criterion tests, on an import Synchronisation Rule's criterion; empty on an export one. |
| `ConnectedSystemAttributeName` | `string` | The name of the Connected System attribute the criterion tests; empty on an export Synchronisation Rule's criterion. |
| `Expression` | `string` | The expression whose computed value the criterion tests, in place of an attribute; empty on an attribute criterion. It reads cs["..."] on an import Synchronisation Rule and mv["..."] on an export one. |
| `CompareAs` | `string` | The type an expression's computed value is compared as (Text, Number, LongNumber, Decimal, DateTime, Boolean or Guid); empty on an attribute criterion. |
| `AttributeDataType` | `string` | The tested attribute's data type, or an expression's Compare As type, which decides which value property holds the value. |
| `Notice` | `string` | Something the administrator should know about the criterion as saved, or empty. Set when an expression is compared against a relative date, which makes every Temporal Scope Reconciliation evaluate every object of the type. |
| `ComparisonType` | `string` | How the attribute is compared with the value: Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith, Contains, NotContains, LessThan, LessThanOrEquals, GreaterThan or GreaterThanOrEquals. |
| `StringValue` | `string` | The value a Text attribute is compared with. |
| `IntValue` | `int` | The value a Number attribute is compared with. |
| `LongValue` | `long` | The value a LongNumber attribute is compared with. |
| `DecimalValue` | `double` | The value a Decimal attribute is compared with. |
| `DateTimeValue` | `datetime` | The date and time a DateTime attribute is compared with, when ValueMode is Absolute. |
| `BoolValue` | `bool` | The value a Boolean attribute is compared with. |
| `GuidValue` | `guid` | The value a Guid attribute is compared with. |
| `CaseSensitive` | `bool` | Whether a Text comparison respects case; false ignores it. |
| `ValueMode` | `string` | For a DateTime attribute, Absolute when it is compared with DateTimeValue, or Relative when it is compared with a date worked out from now each time the criterion is evaluated. |
| `RelativeCount` | `int` | How many RelativeUnit from now the date is, when ValueMode is Relative; empty otherwise. |
| `RelativeUnit` | `string` | The unit RelativeCount counts (Hours, Days, Weeks, Months or Years), when ValueMode is Relative; empty otherwise. |
| `RelativeDirection` | `string` | Ago for a date in the past, FromNow for one in the future, when ValueMode is Relative; empty otherwise. |
| `SyncRuleId` | `int` | The ID of the Synchronisation Rule the criterion belongs to. |
| `GroupId` | `int` | The ID of the group the criterion is in. With SyncRuleId and Id, it lets the criterion be piped to `Set-JIMScopingCriterion` and `Remove-JIMScopingCriterion`. |
<!-- END GENERATED: New-JIMScopingCriterion -->

### Notes

- Give the value in the parameter for the attribute's data type: `-StringValue` for a Text attribute, `-IntValue` for a Number attribute, and so on.
- An attribute with no value fails the comparison, the `Not` forms included, so `NotEquals "Test"` leaves out an object with no value. See [How criteria are evaluated](../configuration/scoping-criteria.md#how-criteria-are-evaluated).
- On a multi-valued attribute the criterion tests every value: a positive comparison is met when any value matches, a `Not` form when none does. See [Multi-valued attributes in scope](../configuration/scoping-criteria.md#multi-valued-attributes-in-scope).
- For a DateTime attribute, `-ValueMode Relative` with `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection` compares with a date worked out from now each time the rule is evaluated, instead of `-DateTimeValue`. See [Relative dates in scope filters](../configuration/scoping-criteria.md#relative-dates-in-scope-filters).

### Examples

```powershell title="Import scope: only objects whose objectClass is user"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeName "objectClass" `
    -ComparisonType Equals `
    -StringValue "user"
```

```powershell title="Import scope: employee ID greater than 1000"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeId 14 `
    -ComparisonType GreaterThan `
    -IntValue 1000
```

```powershell title="Export scope: only enabled Users"
New-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 `
    -MetaverseAttributeName "Account Enabled" `
    -ComparisonType Equals `
    -BoolValue $true
```

```powershell title="Import scope: department starts with 'Engineering'"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeName "department" `
    -ComparisonType StartsWith `
    -StringValue "Engineering"
```

```powershell title="Export scope: modified after a specific date"
New-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 `
    -MetaverseAttributeId 20 `
    -ComparisonType GreaterThanOrEquals `
    -DateTimeValue "2025-01-01T00:00:00Z"
```

```powershell title="Export scope: terminated within the last year (relative date)"
New-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 `
    -MetaverseAttributeName "Employee End Date" `
    -ComparisonType GreaterThanOrEquals `
    -ValueMode Relative -RelativeCount 364 -RelativeUnit Days -RelativeDirection Ago
```

---

## Set-JIMScopingCriterion

Replaces a criterion in a scoping criteria group: its attribute, comparison and value all come from what you give, as with `New-JIMScopingCriterion`. Its case sensitivity is the exception: without `-CaseSensitive`, the criterion keeps its current setting.

<!-- BEGIN GENERATED: Set-JIMScopingCriterion -->
### Syntax

```powershell
# ByMvId (default)
Set-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> -MetaverseAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByMvName
Set-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> -MetaverseAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByCsId
Set-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> -ConnectedSystemAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByCsName
Set-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> -ConnectedSystemAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByExpression
Set-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> -Expression <string> -CompareAs <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the group the criterion is in. Accepts pipeline input by property name. |
| `CriterionId` | `int` | Yes |  | The ID of the criterion to replace. Accepts pipeline input by property name, `CriterionId` or `Id`. |
| `MetaverseAttributeId` | `int` | Yes (ByMvId) |  | The ID of the Metaverse attribute to test. An export Synchronisation Rule's criteria test Metaverse attributes. |
| `MetaverseAttributeName` | `string` | Yes (ByMvName) |  | The name of the Metaverse attribute to test, instead of its ID. |
| `ConnectedSystemAttributeId` | `int` | Yes (ByCsId) |  | The ID of the Connected System attribute to test. An import Synchronisation Rule's criteria test Connected System attributes. |
| `ConnectedSystemAttributeName` | `string` | Yes (ByCsName) |  | The name of the Connected System attribute to test, instead of its ID. |
| `Expression` | `string` | Yes (ByExpression) |  | An expression whose computed value the criterion compares, in place of an attribute. It reads the object the Synchronisation Rule scopes: cs["name"] on an import rule, mv["Name"] on an export rule. Use it to compare a value reshaped first, such as Lower(Trim(cs["department"])). Requires `-CompareAs`. |
| `CompareAs` | `string` | Yes (ByExpression) |  | The type the expression's computed value is compared as, which decides the comparisons and the value parameter that apply, as an attribute's own type does. A value the expression computes that cannot be read as this type stops that object with an error rather than deciding its scope. One of `Text`, `Number`, `LongNumber`, `Decimal`, `DateTime`, `Boolean` or `Guid`. |
| `ComparisonType` | `string` | Yes |  | How the attribute is compared with the value. The Not forms are met when the attribute holds a value and none matches; the others when any value does. HasValue and HasNoValue test whether the attribute holds a value at all and take no value. MatchesPattern and NotMatchesPattern take a .NET regular expression in `-StringValue`, for Text attributes. One of `Equals`, `NotEquals`, `StartsWith`, `NotStartsWith`, `EndsWith`, `NotEndsWith`, `Contains`, `NotContains`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `HasValue`, `HasNoValue`, `MatchesPattern` or `NotMatchesPattern`. |
| `StringValue` | `string` | No |  | The value to compare a Text attribute with, or the regular expression for MatchesPattern and NotMatchesPattern. |
| `IntValue` | `int` | No |  | The value to compare a Number attribute with. |
| `LongValue` | `long` | No |  | The value to compare a LongNumber attribute with. |
| `DecimalValue` | `decimal` | No |  | The value to compare a Decimal attribute with. For a high-precision value, use the d suffix (123.456789012345678d) or a quoted string: a bare number is read as a double first, and can lose precision. |
| `DateTimeValue` | `datetime` | No |  | The date and time to compare a DateTime attribute with, when `-ValueMode` is Absolute. |
| `BoolValue` | `bool` | No |  | The value to compare a Boolean attribute with. |
| `GuidValue` | `guid` | No |  | The value to compare a Guid attribute with. |
| `CaseSensitive` | `bool` | No |  | Whether a Text comparison respects case: `$false` ignores it. When omitted, the criterion keeps its current setting. |
| `ValueMode` | `string` | No |  | For a DateTime attribute: Absolute compares with `-DateTimeValue`, and Relative with a date worked out from now each time the rule is evaluated, set by `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection`. Relative cannot be used with `-DateTimeValue`. Absolute when omitted. One of `Absolute` or `Relative`. |
| `RelativeCount` | `int` | No |  | How many units from now the date is, zero or more. Used with `-ValueMode` Relative. |
| `RelativeUnit` | `string` | No |  | The unit `-RelativeCount` counts. Used with `-ValueMode` Relative. One of `Hours`, `Days`, `Weeks`, `Months` or `Years`. |
| `RelativeDirection` | `string` | No |  | Whether the date is in the past (Ago) or the future (FromNow). Used with `-ValueMode` Relative. One of `Ago` or `FromNow`. |
| `PassThru` | `switch` | No |  | Returns the criterion as replaced. |

### Output

With `-PassThru`, the criterion as replaced; otherwise nothing.

Properties as under [New-JIMScopingCriterion](#new-jimscopingcriterion).
<!-- END GENERATED: Set-JIMScopingCriterion -->

### Examples

```powershell title="Change a criterion to a relative date (on or before 7 days from now)"
Set-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 -CriterionId 12 `
    -MetaverseAttributeName "Account Expires" `
    -ComparisonType LessThanOrEquals `
    -ValueMode Relative -RelativeCount 7 -RelativeUnit Days -RelativeDirection FromNow
```


---

## Remove-JIMScopingCriterion

Deletes a criterion from a scoping criteria group.

<!-- BEGIN GENERATED: Remove-JIMScopingCriterion -->
### Syntax

```powershell
Remove-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int> [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the group the criterion is in. Accepts pipeline input by property name. |
| `CriterionId` | `int` | Yes |  | The ID of the criterion to delete. Accepts pipeline input by property name, `CriterionId` or `Id`. |

### Output

None.
<!-- END GENERATED: Remove-JIMScopingCriterion -->

### Examples

```powershell title="Delete a scoping criterion"
Remove-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 -CriterionId 7
```

---

## Test-JIMScope

Tests whether an object would be in scope of a Synchronisation Rule, criterion by criterion, as of a chosen time, and when that would change. See [Testing a scope](../configuration/scoping-criteria.md#testing-a-scope).

<!-- BEGIN GENERATED: Test-JIMScope -->
### Syntax

```powershell
# Samples (default)
Test-JIMScope [-SyncRuleId] <int> [-Attributes <hashtable>] [-AsOf <datetime>]

# ConnectedSystemObject
Test-JIMScope [-SyncRuleId] <int> -ConnectedSystemObjectId <guid> [-Attributes <hashtable>] [-AsOf <datetime>]

# MetaverseObject
Test-JIMScope [-SyncRuleId] <int> -MetaverseObjectId <guid> [-Attributes <hashtable>] [-AsOf <datetime>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule whose scoping criteria to test. Can be given by position, as the first argument. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `Attributes` | `hashtable` | No |  | A hashtable of sample values for the attributes the scoping criteria compare, keyed by attribute name. Each value is converted to its attribute's type; `$null` is no value, and an array gives a multi-valued attribute several values. With ConnectedSystemObjectId or MetaverseObjectId, a value here replaces the object's value for that attribute. |
| `ConnectedSystemObjectId` | `guid` | Yes (ConnectedSystemObject) |  | The ID of a Connected System Object whose values to test with, for an import rule. |
| `MetaverseObjectId` | `guid` | Yes (MetaverseObject) |  | The ID of a Metaverse Object whose values to test with, for an export rule. |
| `AsOf` | `datetime` | No |  | The instant to test as of, which relative date criteria resolve against. A time without a time zone is read as UTC. |

### Output

Whether the object would be in scope (Outcome), the scoping criteria as they evaluated (Explanation, the same tree the portal's Scope tab shows), the values tested for each compared attribute (Inputs) and when the outcome would next change (NextChange). When the values cannot be used, IsValid is `$false` and InputErrors says why.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | False when the values could not be used; `InputErrors` says why, and nothing was evaluated. |
| `Outcome` | `string` | Whether the object is in scope, out of scope, or cannot be evaluated because the evaluation reaches an invalid criterion. Empty when `IsValid` is false. One of `InScope`, `OutOfScope` or `Undetermined`. |
| `EvaluatedAt` | `datetime` | The instant the test was evaluated as of, in UTC. |
| `Explanation` | `object` | The scoping criteria as they evaluated: each group with whether it was met, each criterion with what the object held. Empty when `IsValid` is false. It has `SyncRuleId`, `SyncRuleName`, `Direction`, `Outcome`, `HasCriteria`, `EvaluatedAt`, `Hint`, `Groups` and `Criteria`. |
| `NextChange` | `object` | When the outcome next changes as time passes, the values staying as they are. Empty when it never does, which is so whenever no relative date criterion can change it. It has `At` and `Outcome`. |
| `Inputs` | `object[]` | The attributes the scoping criteria compare, with the values tested for each. Each has `Attribute`, `Type`, `Plurality` and `ValueLines`. |
| `InputErrors` | `string[]` | Why the values could not be used, one message per problem; empty when they could. |
<!-- END GENERATED: Test-JIMScope -->

### Examples

```powershell title="Test sample values against an import rule's scope"
Test-JIMScope -SyncRuleId 12 -Attributes @{ department = 'Finance'; leaveDate = '2026-11-01' }
```

```powershell title="Test a real object as it would be on a future date"
Test-JIMScope -SyncRuleId 12 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' -AsOf '2027-01-31'
```

```powershell title="List each criterion with what a Metaverse Object holds"
(Test-JIMScope -SyncRuleId 15 -MetaverseObjectId '6f9619ff-8b86-d011-b42d-00c04fc964ff').Explanation.Criteria |
    Select-Object Description, Met, ActualDescription
```

---

## See also

- [Scoping Criteria](../configuration/scoping-criteria.md): how criteria are evaluated, multi-valued attributes, relative dates and what happens to objects that leave scope
- [Previews](previews.md#new-jimconfigurationchangepreview): preview a change to a Synchronisation Rule's scoping criteria before making it
- [Synchronisation Rules](synchronisation-rules.md): the rules the criteria belong to
- [Object Matching Rules](object-matching-rules.md): how the objects in scope are matched to Metaverse Objects
