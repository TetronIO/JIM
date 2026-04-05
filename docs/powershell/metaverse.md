---
title: Metaverse
---

# Metaverse

The metaverse is the central identity store in JIM. These cmdlets manage the metaverse schema (object types and attributes), query metaverse objects, and review pending deletions.

## Object Types

### Get-JIMMetaverseObjectType

Retrieves metaverse object type definitions. Returns all object types by default, or a specific type by ID or name.

#### Syntax

```powershell
# List (default)
Get-JIMMetaverseObjectType [-IncludeChildObjects] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseObjectType -Id <int> [-IncludeChildObjects]

# ByName
Get-JIMMetaverseObjectType -Name <string> [-IncludeChildObjects]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | No | | The ID of a specific object type to retrieve |
| `Name` | `string` | No | | The name of a specific object type to retrieve |
| `IncludeChildObjects` | `switch` | No | `false` | Include child object counts for each object type |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 1000) |

#### Output

Object type definitions including ID, name, and optionally child object counts.

#### Examples

```powershell title="List all object types"
Get-JIMMetaverseObjectType
```

```powershell title="Get a specific object type by name with child counts"
Get-JIMMetaverseObjectType -Name "Person" -IncludeChildObjects
```

```powershell title="Page through object types"
Get-JIMMetaverseObjectType -Page 2 -PageSize 50
```

---

### Set-JIMMetaverseObjectType

Modifies an existing metaverse object type. Use this cmdlet to configure automatic deletion behaviour for metaverse objects of a given type.

#### Syntax

```powershell
# ById (default)
Set-JIMMetaverseObjectType -Id <int> [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-PassThru]

# ByName
Set-JIMMetaverseObjectType -Name <string> [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-PassThru]

# ByInputObject
Set-JIMMetaverseObjectType -InputObject <object> [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the object type to modify. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName) | | The name of the object type to modify |
| `InputObject` | `object` | Yes (ByInputObject) | | An object type object from the pipeline |
| `DeletionRule` | `string` | No | | The deletion rule to apply. Valid values: `Manual`, `WhenLastConnectorDisconnected`, `WhenAuthoritativeSourceDisconnected` |
| `DeletionGracePeriod` | `TimeSpan` | No | | Grace period before a pending deletion is executed |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No | | Connected system IDs that trigger deletion when disconnected |
| `PassThru` | `switch` | No | `false` | Return the updated object type |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

!!! note "Deletion rules"
    The deletion rule controls how metaverse objects of this type are automatically cleaned up:

    - **Manual**: objects are never automatically deleted; an administrator must delete them explicitly
    - **WhenLastConnectorDisconnected**: the object is marked for deletion when all connectors are removed
    - **WhenAuthoritativeSourceDisconnected**: the object is marked for deletion when the authoritative source connector is removed

#### Output

When `-PassThru` is specified, returns the updated object type definition. Otherwise, no output.

#### Examples

```powershell title="Set deletion rule by name"
Set-JIMMetaverseObjectType -Name "Person" -DeletionRule WhenLastConnectorDisconnected
```

```powershell title="Configure a 30-day grace period with pipeline input"
Get-JIMMetaverseObjectType -Name "Group" | Set-JIMMetaverseObjectType -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionGracePeriod "30.00:00:00" -PassThru
```

```powershell title="Set deletion triggers for specific connected systems"
Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds @(3, 7)
```

---

## Attributes

### Get-JIMMetaverseAttribute

Retrieves metaverse attribute definitions. Returns all attributes by default, or a specific attribute by ID or name.

#### Syntax

```powershell
# List (default)
Get-JIMMetaverseAttribute [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseAttribute -Id <int>

# ByName
Get-JIMMetaverseAttribute -Name <string>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | No | | The ID of a specific attribute to retrieve. Accepts pipeline input. |
| `Name` | `string` | No | | The name of a specific attribute to retrieve |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 1000) |

#### Output

Attribute definitions including ID, name, type, and plurality.

#### Examples

```powershell title="List all metaverse attributes"
Get-JIMMetaverseAttribute
```

```powershell title="Get a specific attribute by name"
Get-JIMMetaverseAttribute -Name "Display Name"
```

```powershell title="Page through attributes"
Get-JIMMetaverseAttribute -Page 1 -PageSize 50
```

---

### New-JIMMetaverseAttribute

Creates a new metaverse attribute definition.

#### Syntax

```powershell
New-JIMMetaverseAttribute -Name <string> -Type <string> [-AttributePlurality <string>]
    [-ObjectTypeIds <int[]>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The name of the new attribute |
| `Type` | `string` | Yes | | The data type. Valid values: `Text`, `Integer`, `DateTime`, `Boolean`, `Reference`, `Guid`, `Binary` |
| `AttributePlurality` | `string` | No | `SingleValued` | Whether the attribute holds one or many values. Valid values: `SingleValued`, `MultiValued` |
| `ObjectTypeIds` | `int[]` | No | | Object type IDs to associate the attribute with |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

#### Output

The newly created attribute definition.

#### Examples

```powershell title="Create a simple text attribute"
New-JIMMetaverseAttribute -Name "Cost Centre" -Type Text
```

```powershell title="Create a multi-valued reference attribute"
New-JIMMetaverseAttribute -Name "Direct Reports" -Type Reference -AttributePlurality MultiValued
```

```powershell title="Create an attribute and associate it with object types"
$personType = Get-JIMMetaverseObjectType -Name "Person"
$groupType = Get-JIMMetaverseObjectType -Name "Group"
New-JIMMetaverseAttribute -Name "Department" -Type Text -ObjectTypeIds @($personType.Id, $groupType.Id)
```

---

### Set-JIMMetaverseAttribute

Modifies an existing metaverse attribute definition.

#### Syntax

```powershell
# ById (default)
Set-JIMMetaverseAttribute -Id <int> [-Name <string>] [-Type <string>] [-AttributePlurality <string>]
    [-ObjectTypeIds <int[]>] [-PassThru]

# ByInputObject
Set-JIMMetaverseAttribute -InputObject <object> [-Name <string>] [-Type <string>]
    [-AttributePlurality <string>] [-ObjectTypeIds <int[]>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the attribute to modify. Accepts pipeline input. |
| `InputObject` | `object` | Yes (ByInputObject) | | An attribute object from the pipeline |
| `Name` | `string` | No | | The new name for the attribute |
| `Type` | `string` | No | | The new data type. Valid values: `Text`, `Integer`, `DateTime`, `Boolean`, `Reference`, `Guid`, `Binary` |
| `AttributePlurality` | `string` | No | | The new plurality. Valid values: `SingleValued`, `MultiValued` |
| `ObjectTypeIds` | `int[]` | No | | Object type IDs to associate with; replaces existing associations |
| `PassThru` | `switch` | No | `false` | Return the updated attribute definition |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

#### Output

When `-PassThru` is specified, returns the updated attribute definition. Otherwise, no output.

#### Examples

```powershell title="Rename an attribute"
Set-JIMMetaverseAttribute -Id 42 -Name "Cost Centre Code" -PassThru
```

```powershell title="Change an attribute to multi-valued via pipeline"
Get-JIMMetaverseAttribute -Name "Proxy Addresses" | Set-JIMMetaverseAttribute -AttributePlurality MultiValued
```

```powershell title="Replace object type associations"
Set-JIMMetaverseAttribute -Id 42 -ObjectTypeIds @(1, 2, 3)
```

---

### Remove-JIMMetaverseAttribute

Deletes a metaverse attribute definition. Built-in attributes cannot be deleted.

#### Syntax

```powershell
# ById (default)
Remove-JIMMetaverseAttribute -Id <int> [-Force]

# ByInputObject
Remove-JIMMetaverseAttribute -InputObject <object> [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the attribute to delete. Accepts pipeline input. |
| `InputObject` | `object` | Yes (ByInputObject) | | An attribute object from the pipeline |
| `Force` | `switch` | No | `false` | Suppress confirmation prompts |

!!! warning "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **High** impact level. You will be prompted for confirmation unless `-Force` is specified.

!!! note
    Built-in attributes cannot be deleted. Attempting to remove a built-in attribute will result in an error.

#### Output

No output on success.

#### Examples

```powershell title="Delete an attribute by ID"
Remove-JIMMetaverseAttribute -Id 42
```

```powershell title="Delete an attribute without confirmation"
Remove-JIMMetaverseAttribute -Id 42 -Force
```

```powershell title="Delete via pipeline"
Get-JIMMetaverseAttribute -Name "Legacy Code" | Remove-JIMMetaverseAttribute -Force
```

---

## Objects

### Get-JIMMetaverseObject

Retrieves metaverse objects. Supports searching by ID, object type, attribute values, and wildcard patterns.

#### Syntax

```powershell
# List (default)
Get-JIMMetaverseObject [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>]
    [-AttributeName <string> -AttributeValue <string>] [-Attributes <string[]>]
    [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseObject -Id <guid> [-Attributes <string[]>]

# ListAll
Get-JIMMetaverseObject [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>]
    [-AttributeName <string> -AttributeValue <string>] [-Attributes <string[]>] -All
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) | | The GUID of a specific metaverse object. Accepts pipeline input. |
| `ObjectTypeId` | `int` | No | | Filter by object type ID |
| `ObjectTypeName` | `string` | No | | Filter by object type name |
| `Search` | `string` | No | | Search string; supports wildcards |
| `AttributeName` | `string` | No | | Attribute name to search on; requires `AttributeValue` |
| `AttributeValue` | `string` | No | | Attribute value to match; requires `AttributeName` |
| `Attributes` | `string[]` | No | | Attribute names to include in results; use `"*"` to return all attributes |
| `All` | `switch` | No | `false` | Automatically paginate through all results |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 100) |

#### Output

Metaverse objects including their ID, object type, and requested attributes.

#### Examples

```powershell title="Get a specific object by ID"
Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Attributes "*"
```

```powershell title="Search for Person objects by display name"
Get-JIMMetaverseObject -ObjectTypeName "Person" -Search "Smith*" -Attributes @("Display Name", "Mail")
```

```powershell title="Find objects by attribute value"
Get-JIMMetaverseObject -AttributeName "Employee Id" -AttributeValue "12345" -Attributes @("Display Name", "Department")
```

```powershell title="Retrieve all Group objects with auto-pagination"
Get-JIMMetaverseObject -ObjectTypeName "Group" -All -Attributes @("Display Name", "Member")
```

```powershell title="Page through results manually"
Get-JIMMetaverseObject -ObjectTypeName "Person" -Page 3 -PageSize 50
```

---

## Pending Deletions

### Get-JIMPendingDeletion

Retrieves metaverse objects that are pending deletion. Supports listing individual items, returning a count, or a summary breakdown by object type.

#### Syntax

```powershell
# List (default)
Get-JIMPendingDeletion [-ObjectTypeId <int>] [-Page <int>] [-PageSize <int>]

# Count
Get-JIMPendingDeletion [-ObjectTypeId <int>] -Count

# Summary
Get-JIMPendingDeletion -Summary
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectTypeId` | `int` | No | | Filter by object type ID (List and Count parameter sets only) |
| `Page` | `int` | No | `1` | Page number for paginated results (List parameter set only) |
| `PageSize` | `int` | No | `25` | Number of results per page, maximum 100 (List parameter set only) |
| `Count` | `switch` | No | `false` | Return only the total count of pending deletions |
| `Summary` | `switch` | No | `false` | Return a summary breakdown by object type |

#### Output

Depending on the parameter set:

- **List**: pending deletion items including object details and scheduled deletion date
- **Count**: total number of pending deletions as an integer
- **Summary**: breakdown of pending deletion counts grouped by object type

#### Examples

```powershell title="List all pending deletions"
Get-JIMPendingDeletion
```

```powershell title="Get pending deletions for a specific object type"
$personType = Get-JIMMetaverseObjectType -Name "Person"
Get-JIMPendingDeletion -ObjectTypeId $personType.Id -PageSize 50
```

```powershell title="Get a count of all pending deletions"
Get-JIMPendingDeletion -Count
```

```powershell title="Get a summary breakdown by object type"
Get-JIMPendingDeletion -Summary
```

---

## See also

- [Metaverse API Reference](../api/metaverse/index.md)
- [Sync Rules](sync-rules.md)
- [Connected Systems](connected-systems.md)
