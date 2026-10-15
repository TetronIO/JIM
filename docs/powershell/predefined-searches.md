---
title: Predefined Searches
description: "The cmdlets that list, read and change Predefined Searches, and add, change and remove their criteria."
type: reference
audience: automation-author
---

# Predefined Searches

Cmdlets for administering Predefined Searches. Predefined Searches are named, reusable searches that drive end-user list views in the portal (for example, People, Service Principals, Security Groups) and the fast `Search-JIMMetaverseObject` list API. Administrators can disable a search to hide it from end users without deleting it.

!!! info
    Disabled searches are hidden from the portal, the end-user search API, and the sidebar navigation. They remain visible in the admin UI and to these cmdlets so they can be re-enabled at any time.

---

## Get-JIMPredefinedSearch

Gets Predefined Searches. Administrators see all searches, including any that are currently disabled, so they can be discovered and enabled via [`Set-JIMPredefinedSearch`](#set-jimpredefinedsearch).

The shape of the returned object depends on how you call the cmdlet:

- **No parameters** or a wildcard `-Uri`: returns lightweight headers (one per search), suitable for browsing and discovery.
- **`-Id`** or a literal `-Uri`: resolves directly against a dedicated server endpoint and returns the full search graph (header fields plus the displayed attributes and criteria groups).

<!-- BEGIN GENERATED: Get-JIMPredefinedSearch -->
### Syntax

```powershell
# List (default)
Get-JIMPredefinedSearch

# ById
Get-JIMPredefinedSearch -Id <int>

# ByUri
Get-JIMPredefinedSearch -Uri <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | Returns the search with this ID, in full. Accepts pipeline input by property name. |
| `Uri` | `string` | Yes (ByUri) |  | Returns the search with this URI, the stable slug such as people or security-groups. A literal URI returns the search in full; a wildcard pattern returns the header of each search it matches. Accepts pipeline input by property name. Supports wildcards. |

### Output

The list and a wildcard `-Uri` return a header per search; `-Id` and a literal `-Uri` return the search in full, with its attributes and criteria groups.

**By default, or with a wildcard `-Uri`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The Predefined Search's identifier. |
| `MetaverseObjectTypeName` | `string` | The name of the Metaverse Object Type the search finds. |
| `IsDefaultForMetaverseObjectType` | `bool` | Whether this is the search the portal uses for its Metaverse Object Type's list. |
| `Name` | `string` | The search's display name. |
| `Uri` | `string` | The stable, human-readable slug that identifies the search in URLs and the API, such as people. |
| `BuiltIn` | `bool` | Whether the search ships with JIM, rather than being created by an administrator. |
| `IsEnabled` | `bool` | Whether end users can see and run the search. A disabled search stays visible to administrators. |
| `MetaverseAttributeCount` | `int` | How many attributes the search shows as columns in its results. |
| `Created` | `datetime` | When the search was created (UTC). |

**With `-Id`, or a literal `-Uri`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the predefined search. |
| `Name` | `string` | The user-facing name of the predefined search, e.g. "All Permanent Staff". |
| `Uri` | `string` | The stable, human-readable slug used in URLs and as a search identifier. |
| `BuiltIn` | `bool` | Whether the search ships with JIM (as opposed to being administrator-defined). |
| `IsEnabled` | `bool` | Whether end users can see and run the search. |
| `IsDefaultForMetaverseObjectType` | `bool` | Whether this is the default search for its Metaverse Object Type. |
| `MetaverseObjectTypeId` | `int` | The id of the Metaverse Object Type the search returns results for. |
| `MetaverseObjectTypeName` | `string` | The name of the Metaverse Object Type the search returns results for. |
| `Created` | `datetime` | When the search was created (UTC). |
| `CreatedByName` | `string` | The display name of the principal that created the search. |
| `LastUpdated` | `datetime` | When the search was last modified (UTC). Empty if never modified after creation. |
| `LastUpdatedByName` | `string` | The display name of the principal that last modified the search. |
| `Attributes` | `object[]` | The attributes surfaced as columns in the search results, ordered by Position. Each has `Id`, `MetaverseAttributeId`, `MetaverseAttributeName` and `Position`. |
| `CriteriaGroups` | `object[]` | The criteria groups that filter which objects match the search, ordered by Position. Each has `Id`, `Type`, `Position`, `Criteria` and `ChildGroups`. |
<!-- END GENERATED: Get-JIMPredefinedSearch -->

### Examples

```powershell title="List all Predefined Searches as headers (including disabled ones)"
Get-JIMPredefinedSearch
```

```powershell title="Get the full 'people' search by URI"
Get-JIMPredefinedSearch -Uri people
```

```powershell title="Get the full search by ID"
Get-JIMPredefinedSearch -Id 3
```

```powershell title="Filter headers by wildcard URI"
Get-JIMPredefinedSearch -Uri 'sec*'
```

```powershell title="List every disabled search"
Get-JIMPredefinedSearch | Where-Object { -not $_.IsEnabled }
```

---

## Set-JIMPredefinedSearch

Applies a partial update to a Predefined Search. Only parameters explicitly provided are sent; omitted fields are left unchanged.

<!-- BEGIN GENERATED: Set-JIMPredefinedSearch -->
### Syntax

```powershell
Set-JIMPredefinedSearch -Id <int> [-IsEnabled <bool>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes |  | The ID of the Predefined Search to update. Accepts pipeline input by property name. |
| `IsEnabled` | `bool` | No |  | Whether end users can see and run the search: `$true` to enable it, `$false` to disable it. Left as it is when omitted. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the search's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated search. |

### Output

With `-PassThru`, the updated search in full, as `Get-JIMPredefinedSearch -Id` returns it; otherwise nothing.

Properties as under [Get-JIMPredefinedSearch](#get-jimpredefinedsearch).
<!-- END GENERATED: Set-JIMPredefinedSearch -->

### Examples

```powershell title="Disable a Predefined Search by ID"
Set-JIMPredefinedSearch -Id 3 -IsEnabled $false
```

```powershell title="Disable a search by URI via the pipeline"
Get-JIMPredefinedSearch -Uri 'distribution-groups' | Set-JIMPredefinedSearch -IsEnabled $false
```

```powershell title="Enable and return the updated header"
Set-JIMPredefinedSearch -Id 3 -IsEnabled $true -PassThru
```

```powershell title="Preview what the change would do without applying it"
Set-JIMPredefinedSearch -Id 3 -IsEnabled $false -WhatIf
```

```powershell title="Disable a search and record why, for the Configuration Change History"
Set-JIMPredefinedSearch -Id 3 -IsEnabled $false -ChangeReason "Retiring in favour of new search (CHG0128)"
```

### Notes

- The cmdlet distinguishes between `-IsEnabled $false` (intentional disable) and omitting `-IsEnabled` (leave state unchanged). This is essential for future expansion as new toggle fields are added.
- Disabling a search does not affect administrator visibility in the admin UI or to this module; it only hides the search from end users, the sidebar, and the `Search-JIMMetaverseObject` cmdlet.
- Retrieve the recorded changes, including any `-ChangeReason` given, with `Get-JIMConfigurationChangeHistory -Type PredefinedSearch` (see [History](history.md)).

---

## Criteria groups and criteria

These cmdlets manage the criteria that filter a Predefined Search's results. Criteria live in **criteria groups**; add a group first, then add criteria to it. See [Filtering with criteria](../configuration/predefined-searches.md#filtering-with-criteria) for the operators available per attribute type and how criteria combine (each group is All/AND or Any/OR, top-level groups are OR-ed, and groups nest to any depth for mixed logic).

Each criteria group or criterion edit is captured as its own version rolled up into the owning Predefined Search's [Configuration Change History](history.md), and every write cmdlet accepts an optional `-ChangeReason` recorded against that version.

### Examples

```powershell title="Add a group, then filter on a text attribute"
$group = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $group.Id `
    -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance'
```

```powershell title="Mixed logic: (Department = Finance OR Sales) AND IsActive"
# Top-level All group with the IsActive criterion, plus a nested Any group for the departments.
$all = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $all.Id `
    -MetaverseAttributeName 'IsActive' -ComparisonType Equals -BoolValue $true
$any = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -ParentGroupId $all.Id -Type Any -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $any.Id -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance'
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $any.Id -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Sales'
```

---

## Get-JIMPredefinedSearchCriteriaGroup

Lists a Predefined Search's criteria groups, each with its criteria and nested groups.

<!-- BEGIN GENERATED: Get-JIMPredefinedSearchCriteriaGroup -->
### Syntax

```powershell
Get-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name, `PredefinedSearchId` or `Id`. |

### Output

One object per top-level criteria group, with its criteria and nested groups.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the criteria group. |
| `Type` | `string` | The logical operator for this group: "All" (AND) or "Any" (OR). |
| `Position` | `int` | The position/order of this group relative to its siblings. |
| `Criteria` | `object[]` | The criteria in this group. Each has `Id`, `MetaverseAttributeId`, `MetaverseAttributeName`, `AttributeDataType`, `ComparisonType`, `StringValue`, `IntValue`, `LongValue`, `DecimalValue`, `DateTimeValue`, `BoolValue`, `GuidValue`, `CaseSensitive`, `ValueMode`, `RelativeCount`, `RelativeUnit` and `RelativeDirection`. |
| `ChildGroups` | `object[]` | Nested child groups. A group combines its criteria and child groups with AND (type All) or OR (type Any); top-level groups are combined with OR. Groups nest to any depth. Each has `Id`, `Type`, `Position`, `Criteria` and `ChildGroups`. |
<!-- END GENERATED: Get-JIMPredefinedSearchCriteriaGroup -->

### Examples

```powershell title="List the criteria groups for a search"
Get-JIMPredefinedSearch -Uri people | Get-JIMPredefinedSearchCriteriaGroup
```

---

## New-JIMPredefinedSearchCriteriaGroup

Creates a criteria group on a Predefined Search. Pass `-ParentGroupId` to nest it under an existing group, for example to express (A OR B) AND C.

<!-- BEGIN GENERATED: New-JIMPredefinedSearchCriteriaGroup -->
### Syntax

```powershell
New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> [-ParentGroupId <int>] [-Type <string>] [-Position <int>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name, `PredefinedSearchId` or `Id`. |
| `ParentGroupId` | `int` | No |  | The ID of an existing group to nest this one in. A top-level group when omitted. |
| `Type` | `string` | No | `'All'` | How the group combines what it holds: All (AND) or Any (OR). One of `All` or `Any`. |
| `Position` | `int` | No | `0` | The group's place among its siblings. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the created group. |

### Output

With `-PassThru`, the created group, with a PredefinedSearchId property added naming its search; otherwise nothing.

Properties as under [Get-JIMPredefinedSearchCriteriaGroup](#get-jimpredefinedsearchcriteriagroup).
<!-- END GENERATED: New-JIMPredefinedSearchCriteriaGroup -->

### Examples

```powershell title="Create a top-level group whose criteria must all match"
New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All
```

```powershell title="Nest a group whose criteria need only one to match"
New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -ParentGroupId 10 -Type Any -PassThru
```

---

## Set-JIMPredefinedSearchCriteriaGroup

Changes a criteria group's logic type or position.

<!-- BEGIN GENERATED: Set-JIMPredefinedSearchCriteriaGroup -->
### Syntax

```powershell
Set-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> -GroupId <int> [-Type <string>] [-Position <int>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the criteria group to update. Accepts pipeline input by property name, `GroupId` or `Id`. |
| `Type` | `string` | No |  | How the group combines what it holds: All (AND) or Any (OR). One of `All` or `Any`. |
| `Position` | `int` | No |  | The group's place among its siblings. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated group. |

### Output

With `-PassThru`, the updated group, with a PredefinedSearchId property added naming its search; otherwise nothing.

Properties as under [Get-JIMPredefinedSearchCriteriaGroup](#get-jimpredefinedsearchcriteriagroup).
<!-- END GENERATED: Set-JIMPredefinedSearchCriteriaGroup -->

### Examples

```powershell title="Make a group match when any of its criteria do"
Set-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10 -Type Any
```

---

## Remove-JIMPredefinedSearchCriteriaGroup

Deletes a criteria group and everything in it: its criteria and its nested groups. The Predefined Search itself and its other groups are kept, and the deletion is recorded as a version in the search's [Configuration Change History](history.md).

<!-- BEGIN GENERATED: Remove-JIMPredefinedSearchCriteriaGroup -->
### Syntax

```powershell
Remove-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> -GroupId <int> [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the criteria group to remove, with everything in it. Accepts pipeline input by property name, `GroupId` or `Id`. |
| `ChangeReason` | `string` | No |  | Why the group was removed, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |

### Output

None.
<!-- END GENERATED: Remove-JIMPredefinedSearchCriteriaGroup -->

### Examples

```powershell title="Remove a group and record why"
Remove-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10 -ChangeReason "Consolidating filters (CHG0129)"
```

---

## New-JIMPredefinedSearchCriterion

Adds a criterion to a criteria group. It takes the attribute (by `-MetaverseAttributeId` or `-MetaverseAttributeName`), a `-ComparisonType`, and the value parameter that matches the attribute's data type (`-StringValue`, `-IntValue`, `-LongValue`, `-DecimalValue`, `-DateTimeValue`, `-BoolValue`, or `-GuidValue`). `-CaseSensitive $false` makes a text comparison case-insensitive. On a multi-valued attribute a positive operator matches when any value does and a `Not...` operator when the object holds a value and none matches; see [multi-valued attributes and missing values](../configuration/predefined-searches.md#multi-valued-attributes-and-missing-values).

For a Date/Time attribute you can compare against a date relative to now instead of a fixed `-DateTimeValue`: pass `-ValueMode Relative` with `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection`. See [relative dates](../configuration/scoping-criteria.md#relative-dates-in-scope-filters) for the resolution rules.

<!-- BEGIN GENERATED: New-JIMPredefinedSearchCriterion -->
### Syntax

```powershell
# ById (default)
New-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -MetaverseAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
New-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -MetaverseAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the criteria group to add the criterion to. Accepts pipeline input by property name. |
| `MetaverseAttributeId` | `int` | Yes (ById) |  | The ID of the Metaverse Attribute to compare. |
| `MetaverseAttributeName` | `string` | Yes (ByName) |  | The name of the Metaverse Attribute to compare, instead of its ID. |
| `ComparisonType` | `string` | Yes |  | The comparison, which must suit the attribute's data type. Text takes Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith, Contains and NotContains; Number, LongNumber, Decimal and DateTime take Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan and GreaterThanOrEquals; Boolean and Guid take Equals and NotEquals. Every type also takes HasValue and HasNoValue, which test whether the attribute holds a value at all and take no value. One of `Equals`, `NotEquals`, `StartsWith`, `NotStartsWith`, `EndsWith`, `NotEndsWith`, `Contains`, `NotContains`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `HasValue` or `HasNoValue`. |
| `StringValue` | `string` | No |  | The value to compare a Text attribute with. |
| `IntValue` | `int` | No |  | The value to compare a Number attribute with. |
| `LongValue` | `long` | No |  | The value to compare a LongNumber attribute with. |
| `DecimalValue` | `decimal` | No |  | The value to compare a Decimal attribute with. For a high-precision value use PowerShell's decimal suffix (123.456789012345678d) or a quoted string, because a bare number is read as a double first and can lose precision. |
| `DateTimeValue` | `datetime` | No |  | The value to compare a DateTime attribute with, read as UTC. |
| `BoolValue` | `bool` | No |  | The value to compare a Boolean attribute with. |
| `GuidValue` | `guid` | No |  | The value to compare a Guid attribute with. |
| `CaseSensitive` | `bool` | No |  | Whether a Text comparison respects case. `$false` ignores case; it does when omitted. |
| `ValueMode` | `string` | No |  | For a DateTime attribute: Absolute compares with `-DateTimeValue`, and is the default; Relative compares with a date worked out from now, given by `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection`, and cannot be combined with `-DateTimeValue`. One of `Absolute` or `Relative`. |
| `RelativeCount` | `int` | No |  | How many units from now, for a Relative comparison; zero or more. |
| `RelativeUnit` | `string` | No |  | The unit of `-RelativeCount`. One of `Hours`, `Days`, `Weeks`, `Months` or `Years`. |
| `RelativeDirection` | `string` | No |  | Whether the date is in the past (Ago) or the future (FromNow). One of `Ago` or `FromNow`. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the created criterion. |

### Output

With `-PassThru`, the created criterion, with a GroupId property added naming its group; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the criterion. |
| `MetaverseAttributeId` | `int` | The Metaverse Attribute ID being evaluated. |
| `MetaverseAttributeName` | `string` | The name of the Metaverse Attribute being evaluated. |
| `AttributeDataType` | `string` | The data type of the attribute (Text, Number, LongNumber, Decimal, DateTime, Boolean, Guid). |
| `ComparisonType` | `string` | The comparison operator (Equals, NotEquals, Contains, StartsWith, GreaterThan, etc.). |
| `StringValue` | `string` | The string value to compare against (for Text attributes). |
| `IntValue` | `int` | The integer value to compare against (for Number attributes). |
| `LongValue` | `long` | The long integer value to compare against (for LongNumber attributes). |
| `DecimalValue` | `double` | The decimal value to compare against (for Decimal attributes). |
| `DateTimeValue` | `datetime` | The date/time value to compare against (for DateTime attributes). Stored and compared in UTC. |
| `BoolValue` | `bool` | The boolean value to compare against (for Boolean attributes). |
| `GuidValue` | `guid` | The GUID value to compare against (for Guid attributes). |
| `CaseSensitive` | `bool` | When true (default), text value comparisons are case-sensitive. Only applies to Text attributes. |
| `ValueMode` | `string` | For DateTime attributes, whether the criterion compares against a fixed date ("Absolute", the default) or a date resolved relative to now ("Relative"). |
| `RelativeCount` | `int` | The relative offset count (when ValueMode is Relative). |
| `RelativeUnit` | `string` | The relative offset unit: Hours, Days, Weeks, Months, Years (when ValueMode is Relative). |
| `RelativeDirection` | `string` | The relative offset direction: Ago or FromNow (when ValueMode is Relative). |
<!-- END GENERATED: New-JIMPredefinedSearchCriterion -->

### Examples

```powershell title="Filter on a number attribute"
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 `
    -MetaverseAttributeName 'MemberCount' -ComparisonType GreaterThan -IntValue 0
```

```powershell title="Filter on a date attribute (compared in UTC)"
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 `
    -MetaverseAttributeName 'AccountExpiry' -ComparisonType LessThan -DateTimeValue '2026-01-01'
```

```powershell title="Filter on a date relative to now (expiring within the next 7 days)"
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 `
    -MetaverseAttributeName 'AccountExpiry' -ComparisonType LessThanOrEquals `
    -ValueMode Relative -RelativeCount 7 -RelativeUnit Days -RelativeDirection FromNow
```

---

## Set-JIMPredefinedSearchCriterion

Replaces a criterion's attribute, operator and value. It is a full update, taking the same parameters as [New-JIMPredefinedSearchCriterion](#new-jimpredefinedsearchcriterion). Its case sensitivity is the exception: without `-CaseSensitive`, the criterion keeps its current setting.

<!-- BEGIN GENERATED: Set-JIMPredefinedSearchCriterion -->
### Syntax

```powershell
# ById (default)
Set-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -CriterionId <int> -MetaverseAttributeId <int> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Set-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -CriterionId <int> -MetaverseAttributeName <string> -ComparisonType <string> [-StringValue <string>] [-IntValue <int>] [-LongValue <long>] [-DecimalValue <decimal>] [-DateTimeValue <datetime>] [-BoolValue <bool>] [-GuidValue <guid>] [-CaseSensitive <bool>] [-ValueMode <string>] [-RelativeCount <int>] [-RelativeUnit <string>] [-RelativeDirection <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the criteria group that holds the criterion. Accepts pipeline input by property name. |
| `CriterionId` | `int` | Yes |  | The ID of the criterion to update. Accepts pipeline input by property name, `CriterionId` or `Id`. |
| `MetaverseAttributeId` | `int` | Yes (ById) |  | The ID of the Metaverse Attribute to compare. |
| `MetaverseAttributeName` | `string` | Yes (ByName) |  | The name of the Metaverse Attribute to compare, instead of its ID. |
| `ComparisonType` | `string` | Yes |  | The comparison, which must suit the attribute's data type. Text takes Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith, Contains and NotContains; Number, LongNumber, Decimal and DateTime take Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan and GreaterThanOrEquals; Boolean and Guid take Equals and NotEquals. Every type also takes HasValue and HasNoValue, which test whether the attribute holds a value at all and take no value. One of `Equals`, `NotEquals`, `StartsWith`, `NotStartsWith`, `EndsWith`, `NotEndsWith`, `Contains`, `NotContains`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `HasValue` or `HasNoValue`. |
| `StringValue` | `string` | No |  | The value to compare a Text attribute with. |
| `IntValue` | `int` | No |  | The value to compare a Number attribute with. |
| `LongValue` | `long` | No |  | The value to compare a LongNumber attribute with. |
| `DecimalValue` | `decimal` | No |  | The value to compare a Decimal attribute with. For a high-precision value use PowerShell's decimal suffix (123.456789012345678d) or a quoted string, because a bare number is read as a double first and can lose precision. |
| `DateTimeValue` | `datetime` | No |  | The value to compare a DateTime attribute with, read as UTC. |
| `BoolValue` | `bool` | No |  | The value to compare a Boolean attribute with. |
| `GuidValue` | `guid` | No |  | The value to compare a Guid attribute with. |
| `CaseSensitive` | `bool` | No |  | Whether a Text comparison respects case: `$false` ignores it. When omitted, the criterion keeps its current setting. |
| `ValueMode` | `string` | No |  | For a DateTime attribute: Absolute compares with `-DateTimeValue`, and is the default; Relative compares with a date worked out from now, given by `-RelativeCount`, `-RelativeUnit` and `-RelativeDirection`, and cannot be combined with `-DateTimeValue`. One of `Absolute` or `Relative`. |
| `RelativeCount` | `int` | No |  | How many units from now, for a Relative comparison; zero or more. |
| `RelativeUnit` | `string` | No |  | The unit of `-RelativeCount`. One of `Hours`, `Days`, `Weeks`, `Months` or `Years`. |
| `RelativeDirection` | `string` | No |  | Whether the date is in the past (Ago) or the future (FromNow). One of `Ago` or `FromNow`. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated criterion. |

### Output

With `-PassThru`, the updated criterion, with a GroupId property added naming its group; otherwise nothing.

Properties as under [New-JIMPredefinedSearchCriterion](#new-jimpredefinedsearchcriterion).
<!-- END GENERATED: Set-JIMPredefinedSearchCriterion -->

### Examples

```powershell title="Change a criterion to match departments containing 'Fin'"
Set-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -CriterionId 15 `
    -MetaverseAttributeName 'Department' -ComparisonType Contains -StringValue 'Fin'
```

---

## Remove-JIMPredefinedSearchCriterion

Deletes a criterion from its criteria group. The group, its other criteria and the Predefined Search are kept, and the deletion is recorded as a version in the search's [Configuration Change History](history.md).

<!-- BEGIN GENERATED: Remove-JIMPredefinedSearchCriterion -->
### Syntax

```powershell
Remove-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -CriterionId <int> [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchId` | `int` | Yes |  | The ID of the Predefined Search. Accepts pipeline input by property name. |
| `GroupId` | `int` | Yes |  | The ID of the criteria group that holds the criterion. Accepts pipeline input by property name. |
| `CriterionId` | `int` | Yes |  | The ID of the criterion to remove. Accepts pipeline input by property name, `CriterionId` or `Id`. |
| `ChangeReason` | `string` | No |  | Why the criterion was removed, recorded on the audit Activity and shown in the Predefined Search's Configuration Change History. |

### Output

None.
<!-- END GENERATED: Remove-JIMPredefinedSearchCriterion -->

### Examples

```powershell title="Remove a criterion and record why"
Remove-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -CriterionId 15 -ChangeReason "No longer required (CHG0130)"
```

---

## See also

- [Search-JIMMetaverseObject](metaverse.md#search-jimmetaverseobject): run a Predefined Search to return matching objects
- [Metaverse](metaverse.md): related cmdlets for querying Metaverse Objects
- [History](history.md): retrieve a Predefined Search's Configuration Change History with `Get-JIMConfigurationChangeHistory -Type PredefinedSearch`
