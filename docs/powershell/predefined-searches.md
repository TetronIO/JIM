---
title: Predefined Searches
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
| `Id` | `int` | Yes (ById) | | Return only the search with this ID. Resolves to a single full search via the server. Accepts pipeline input by property name. |
| `Uri` | `string` | Yes (ByUri) | | Return only the search with this URI (e.g. `people`, `security-groups`). Supports wildcards: a literal value resolves to a single full search via the server; a wildcard pattern is filtered client-side against the list of headers. Accepts pipeline input by property name. |

### Output

The list view and wildcard `-Uri` lookups return one or more header `PSCustomObject` instances:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | Unique identifier for the search. Use this to update the search via `Set-JIMPredefinedSearch`. |
| `Name` | `string` | Human-readable display name |
| `Uri` | `string` | Stable slug used in URLs and as a search identifier |
| `IsEnabled` | `bool` | Whether the search is currently visible to end users |
| `BuiltIn` | `bool` | Whether the search ships with JIM (as opposed to being administrator-defined) |
| `IsDefaultForMetaverseObjectType` | `bool` | Whether this is the default search for its object type |
| `MetaverseObjectTypeName` | `string` | Name of the Metaverse Object Type the search targets |
| `MetaverseAttributeCount` | `int` | Number of attributes surfaced in the search results |
| `Created` | `datetime` | When the search was created |

`-Id` and literal `-Uri` lookups return a single full search with all of the header fields plus:

| Property | Type | Description |
|----------|------|-------------|
| `MetaverseObjectType` | `object` | The Metaverse Object Type the search targets |
| `Attributes` | `array` | Attributes surfaced in the search results, ordered by `Position` |
| `CriteriaGroups` | `array` | Criteria groups that filter which objects match the search |

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

Applies a partial update to a Predefined Search. Only parameters explicitly provided are sent; omitted fields are left unchanged. Supports `ShouldProcess` (Medium impact); use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Set-JIMPredefinedSearch [-Id] <int> [-IsEnabled <bool>] [-ChangeReason <string>] [-PassThru]
    [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | Unique identifier of the search to update. Accepts pipeline input by property name. |
| `IsEnabled` | `bool` | No | | When specified, sets whether the search is visible to end users. Pass `$true` to enable, `$false` to disable. Omit to leave unchanged. |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded on the audit Activity and shown in the search's [configuration change history](history.md). |
| `PassThru` | `switch` | No | `$false` | If specified, emits the updated search header after the update. |

### Output

When `-PassThru` is specified, returns the updated search header. Otherwise, no output.

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

```powershell title="Disable a search and record why, for the configuration change history"
Set-JIMPredefinedSearch -Id 3 -IsEnabled $false -ChangeReason "Retiring in favour of new search (CHG0128)"
```

### Notes

- The cmdlet distinguishes between `-IsEnabled $false` (intentional disable) and omitting `-IsEnabled` (leave state unchanged). This is essential for future expansion as new toggle fields are added.
- Disabling a search does not affect administrator visibility in the admin UI or to this module; it only hides the search from end users, the sidebar, and the `Search-JIMMetaverseObject` cmdlet.
- Retrieve the recorded changes, including any `-ChangeReason` given, with `Get-JIMConfigurationChangeHistory -Type PredefinedSearch` (see [History](history.md)).

---

## Criteria groups and criteria

These cmdlets manage the criteria that filter a Predefined Search's results. Criteria live in **criteria groups**; add a group first, then add criteria to it. See [Filtering with criteria](../configuration/predefined-searches.md#filtering-with-criteria) for the operators available per attribute type and how criteria combine (each group is All/AND or Any/OR, top-level groups are OR-ed, and groups can nest one level for mixed logic).

All the write cmdlets support `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm.

### Group cmdlets

| Cmdlet | Purpose |
|--------|---------|
| `Get-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int>` | List the criteria groups (and their criteria) for a search. |
| `New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> [-ParentGroupId <int>] [-Type All\|Any] [-Position <int>] [-ChangeReason <string>] [-PassThru]` | Create a criteria group; pass `-ParentGroupId` to nest it under an existing group. |
| `Set-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> -GroupId <int> [-Type All\|Any] [-Position <int>] [-PassThru]` | Update a group's logic type or position. |
| `Remove-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId <int> -GroupId <int>` | Delete a group and everything in it. |

Each criteria group or criterion edit is captured as its own version rolled up into the owning Predefined Search's [configuration change history](history.md); every group and criterion cmdlet (create, update, and delete) accepts an optional `-ChangeReason` recorded against that version.

### Criterion cmdlets

`New-JIMPredefinedSearchCriterion` and `Set-JIMPredefinedSearchCriterion` take the attribute (by `-MetaverseAttributeId` or `-MetaverseAttributeName`), a `-ComparisonType`, and the value parameter that matches the attribute's data type (`-StringValue`, `-IntValue`, `-LongValue`, `-DateTimeValue`, `-BoolValue`, or `-GuidValue`). `-CaseSensitive $false` makes a text comparison case-insensitive.

For a Date/Time attribute you can compare against a date relative to now instead of a fixed `-DateTimeValue`: pass `-ValueMode Relative` with `-RelativeCount`, `-RelativeUnit` (Hours, Days, Weeks, Months, Years) and `-RelativeDirection` (Ago or FromNow). Relative is mutually exclusive with `-DateTimeValue`. See [relative dates](../configuration/synchronisation-rules.md#relative-dates-in-scope-filters) for the resolution rules.

| Cmdlet | Purpose |
|--------|---------|
| `New-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> ...` | Add a criterion to a group. |
| `Set-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -CriterionId <int> ...` | Replace a criterion's attribute, operator and value. |
| `Remove-JIMPredefinedSearchCriterion -PredefinedSearchId <int> -GroupId <int> -CriterionId <int>` | Delete a criterion. |

### Examples

```powershell title="Add a group, then filter on a text attribute"
$group = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $group.Id `
    -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance'
```

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

```powershell title="Mixed logic: (Department = Finance OR Sales) AND IsActive"
# Top-level All group with the IsActive criterion, plus a nested Any group for the departments.
$all = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $all.Id `
    -MetaverseAttributeName 'IsActive' -ComparisonType Equals -BoolValue $true
$any = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -ParentGroupId $all.Id -Type Any -PassThru
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $any.Id -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance'
New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $any.Id -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Sales'
```

```powershell title="List the criteria groups for a search"
Get-JIMPredefinedSearch -Uri people | Get-JIMPredefinedSearchCriteriaGroup
```

---

## See also

- [Search-JIMMetaverseObject](metaverse.md): run a Predefined Search to return matching objects
- [Metaverse](metaverse.md): related cmdlets for querying Metaverse Objects and schema
- [History](history.md): retrieve a Predefined Search's configuration change history with `Get-JIMConfigurationChangeHistory -Type PredefinedSearch`
