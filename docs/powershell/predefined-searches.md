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
| `id` | `int` | Unique identifier for the search. Use this to update the search via `Set-JIMPredefinedSearch`. |
| `name` | `string` | Human-readable display name |
| `uri` | `string` | Stable slug used in URLs and as a search identifier |
| `isEnabled` | `bool` | Whether the search is currently visible to end users |
| `builtIn` | `bool` | Whether the search ships with JIM (as opposed to being administrator-defined) |
| `isDefaultForMetaverseObjectType` | `bool` | Whether this is the default search for its object type |
| `metaverseObjectTypeName` | `string` | Name of the Metaverse Object Type the search targets |
| `metaverseAttributeCount` | `int` | Number of attributes surfaced in the search results |
| `created` | `datetime` | When the search was created |

`-Id` and literal `-Uri` lookups return a single full search with all of the header fields plus:

| Property | Type | Description |
|----------|------|-------------|
| `metaverseObjectType` | `object` | The Metaverse Object Type the search targets |
| `attributes` | `array` | Attributes surfaced in the search results, ordered by `position` |
| `criteriaGroups` | `array` | Criteria groups that filter which objects match the search |

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
Get-JIMPredefinedSearch | Where-Object { -not $_.isEnabled }
```

---

## Set-JIMPredefinedSearch

Applies a partial update to a Predefined Search. Only parameters explicitly provided are sent; omitted fields are left unchanged. Supports `ShouldProcess` (Medium impact); use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Set-JIMPredefinedSearch [-Id] <int> [-IsEnabled <bool>] [-PassThru]
    [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | Unique identifier of the search to update. Accepts pipeline input by property name. |
| `IsEnabled` | `bool` | No | | When specified, sets whether the search is visible to end users. Pass `$true` to enable, `$false` to disable. Omit to leave unchanged. |
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

### Notes

- The cmdlet distinguishes between `-IsEnabled $false` (intentional disable) and omitting `-IsEnabled` (leave state unchanged). This is essential for future expansion as new toggle fields are added.
- Disabling a search does not affect administrator visibility in the admin UI or to this module; it only hides the search from end users, the sidebar, and the `Search-JIMMetaverseObject` cmdlet.

---

## See also

- [Search-JIMMetaverseObject](metaverse.md): run a Predefined Search to return matching objects
- [Metaverse](metaverse.md): related cmdlets for querying Metaverse Objects and schema
