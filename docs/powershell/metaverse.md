---
title: Metaverse
---

# Metaverse

The metaverse is the central identity store in JIM. These cmdlets manage the metaverse schema (object types and attributes), query Metaverse Objects, and review pending deletions.

## Object Types

### Get-JIMMetaverseObjectType

Retrieves Metaverse Object Type definitions. Returns all object types by default, or a specific type by ID or name.

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

Modifies an existing Metaverse Object Type: its identity (name, plural name, icon) and/or its automatic deletion behaviour. The built-in `User` and `Group` types accept deletion-rule changes but reject changes to their name, plural name and icon.

#### Syntax

```powershell
# ById (default)
Set-JIMMetaverseObjectType -Id <int> [-NewName <string>] [-PluralName <string>] [-Icon <string>]
    [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-ChangeReason <string>] [-PassThru]

# ByName
Set-JIMMetaverseObjectType -Name <string> [-NewName <string>] [-PluralName <string>] [-Icon <string>]
    [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-ChangeReason <string>] [-PassThru]

# ByInputObject
Set-JIMMetaverseObjectType -InputObject <object> [-NewName <string>] [-PluralName <string>] [-Icon <string>]
    [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-ChangeReason <string>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the object type to modify. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName) | | The name of the object type to modify (used to locate it; use `NewName` to rename) |
| `InputObject` | `object` | Yes (ByInputObject) | | An object type object from the pipeline |
| `NewName` | `string` | No | | A new singular name (rename). Must be unique (case-insensitive). Rejected for built-in types. |
| `PluralName` | `string` | No | | A new plural name. Must be unique (case-insensitive). Rejected for built-in types. |
| `Icon` | `string` | No | | The MudBlazor icon name shown in the UI. Pass `$null` or `''` to clear it. Rejected for built-in types. |
| `DeletionRule` | `string` | No | | The deletion rule to apply. Valid values: `Manual`, `WhenLastConnectorDisconnected`, `WhenAuthoritativeSourceDisconnected` |
| `DeletionGracePeriod` | `TimeSpan` | No | | Grace period before a pending deletion is executed |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No | | Connected System IDs that trigger deletion when disconnected |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the object's [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `PassThru` | `switch` | No | `false` | Return the updated object type |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

!!! note "Deletion rules"
    The deletion rule controls how Metaverse Objects of this type are automatically cleaned up:

    - **Manual**<br /> Objects are never automatically deleted; an administrator must delete them explicitly
    - **WhenLastConnectorDisconnected**<br /> The object is marked for deletion when all connectors are removed
    - **WhenAuthoritativeSourceDisconnected**<br /> The object is marked for deletion when the authoritative source connector is removed

#### Output

When `-PassThru` is specified, returns the updated object type definition. Otherwise, no output.

#### Examples

```powershell title="Set deletion rule by name"
Set-JIMMetaverseObjectType -Name "Person" -DeletionRule WhenLastConnectorDisconnected
```

```powershell title="Configure a 30-day grace period with pipeline input"
Get-JIMMetaverseObjectType -Name "Group" | Set-JIMMetaverseObjectType -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionGracePeriod "30.00:00:00" -PassThru
```

```powershell title="Set deletion triggers for specific Connected Systems"
Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds @(3, 7)
```

```powershell title="Rename a custom type and set its icon"
Set-JIMMetaverseObjectType -Id 5 -NewName "Gadget" -PluralName "Gadgets" -Icon "Devices"
```

```powershell title="Clear a custom type's icon"
Set-JIMMetaverseObjectType -Id 5 -Icon $null
```

---

### New-JIMMetaverseObjectType

Creates a new Metaverse Object Type. Use this when the built-in `User`, `Group`, and other seeded types do not fit; for example, modelling `Device`, `Contractor`, or `ServiceAccount` identities. The new type is created with `BuiltIn = false` so it can be removed via `Reset-JIMSystem` or by administrators in the UI later.

#### Syntax

```powershell
New-JIMMetaverseObjectType -Name <string> -PluralName <string>
    [-Icon <string>] [-AttributeIds <int[]>]
    [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-ChangeReason <string>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | Singular name of the new type. Must be unique. |
| `PluralName` | `string` | Yes | | Plural name of the new type. Must be unique. |
| `Icon` | `string` | No | | Optional MudBlazor icon name to associate with the type in the UI. |
| `AttributeIds` | `int[]` | No | | Optional array of existing Metaverse attribute IDs to associate with this type at creation time. Attributes can also be associated later. |
| `DeletionRule` | `string` | No | `Manual` | Controls when objects of this type are automatically deleted. Valid values: `Manual`, `WhenLastConnectorDisconnected`, `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `TimeSpan` | No | | Grace period before deletion is executed. Use `[TimeSpan]::Zero` for immediate deletion. Ignored when `DeletionRule` is `Manual`. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No | | Required when `DeletionRule` is `WhenAuthoritativeSourceDisconnected`. The Connected System IDs whose disconnect triggers deletion. |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the object's [configuration change history](history.md#get-jimconfigurationchangehistory) |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

#### Output

The newly created object type definition.

#### Examples

```powershell title="Create a simple custom type"
New-JIMMetaverseObjectType -Name "Device" -PluralName "Devices"
```

```powershell title="Create a type with attributes attached at creation"
New-JIMMetaverseObjectType -Name "Contractor" -PluralName "Contractors" -AttributeIds 1,2,3
```

```powershell title="Create a type that is auto-deleted seven days after its authoritative source disconnects"
New-JIMMetaverseObjectType -Name "ServiceAccount" -PluralName "ServiceAccounts" `
    -DeletionRule WhenAuthoritativeSourceDisconnected `
    -DeletionTriggerConnectedSystemIds 5 `
    -DeletionGracePeriod ([TimeSpan]::FromDays(7))
```

---

### Remove-JIMMetaverseObjectType

Deletes a custom Metaverse Object Type. The cmdlet fetches a delete preview first and refuses when deletion is not safe, so it never silently destroys data or configuration.

#### Syntax

```powershell
# ById (default)
Remove-JIMMetaverseObjectType -Id <int> [-ChangeReason <string>] [-Force]

# ByName
Remove-JIMMetaverseObjectType -Name <string> [-ChangeReason <string>] [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the object type to delete. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName) | | The name of the object type to delete |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `Force` | `switch` | No | `false` | Skips the interactive confirmation prompt (the server-side type-the-name safeguard is still satisfied) |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **High** impact level. Use `-WhatIf` to preview or `-Confirm` to require confirmation.

!!! warning "Safeguards"
    Deletion is **refused** when the type is built-in (`User`, `Group`), when any Metaverse Object of the type exists, or when any Synchronisation Rule targets it; the cmdlet reports which. When the type is clear, its Predefined Searches, Example Data Template entries and attribute bindings are cascade-removed (the bound attributes themselves are kept), and the removal is audited.

#### Output

None.

#### Examples

```powershell title="Delete a custom type by name"
Remove-JIMMetaverseObjectType -Name "Device" -Force
```

```powershell title="Preview what a deletion would do"
Remove-JIMMetaverseObjectType -Id 5 -WhatIf
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
    [-ObjectTypeIds <int[]>] [-ChangeReason <string>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The name of the new attribute |
| `Type` | `string` | Yes | | The data type. Valid values: `Text`, `Integer`, `LongNumber`, `DateTime`, `Boolean`, `Reference`, `Guid`, `Binary` |
| `AttributePlurality` | `string` | No | `SingleValued` | Whether the attribute holds one or many values. Valid values: `SingleValued`, `MultiValued` |
| `ObjectTypeIds` | `int[]` | No | | Object type IDs to associate the attribute with |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the object's [configuration change history](history.md#get-jimconfigurationchangehistory) |

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
Set-JIMMetaverseAttribute -Id <int> [-Name <string>] [-RenderingHint <string>] [-Type <string>]
    [-AttributePlurality <string>] [-ChangeReason <string>] [-PassThru]

# ByInputObject
Set-JIMMetaverseAttribute -InputObject <object> [-Name <string>] [-RenderingHint <string>]
    [-Type <string>] [-AttributePlurality <string>] [-ChangeReason <string>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the attribute to modify. Accepts pipeline input. |
| `InputObject` | `object` | Yes (ByInputObject) | | An attribute object from the pipeline |
| `Name` | `string` | No | | The new name. Subject to the same case-insensitive uniqueness check as creation. |
| `RenderingHint` | `string` | No | | How a multi-valued attribute's values display. Valid values: `Default`, `Table`, `ChipSet`, `List` |
| `Type` | `string` | No | | The new data type. Valid values: `Text`, `Integer`, `LongNumber`, `DateTime`, `Boolean`, `Reference`, `Guid`, `Binary` |
| `AttributePlurality` | `string` | No | | The new plurality. Valid values: `SingleValued`, `MultiValued` |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the object's [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `PassThru` | `switch` | No | `false` | Return the updated attribute definition |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

!!! note "Type and plurality changes"
    Changing `-Type` or `-AttributePlurality` is refused while any Metaverse Object holds a stored value for the attribute; clear the values first. To change an attribute's Object Type bindings, use [`Add-JIMMetaverseObjectTypeAttribute`](#add-jimmetaverseobjecttypeattribute) and [`Remove-JIMMetaverseObjectTypeAttribute`](#remove-jimmetaverseobjecttypeattribute) rather than this cmdlet.

#### Output

When `-PassThru` is specified, returns the updated attribute definition. Otherwise, no output.

#### Examples

```powershell title="Rename an attribute"
Set-JIMMetaverseAttribute -Id 42 -Name "costCentreCode" -PassThru
```

```powershell title="Set the rendering hint for a multi-valued attribute"
Get-JIMMetaverseAttribute -Name "proxyAddresses" | Set-JIMMetaverseAttribute -RenderingHint List
```

```powershell title="Change an attribute's data type (refused if any object holds a value)"
Set-JIMMetaverseAttribute -Id 42 -Type Integer
```

---

### Remove-JIMMetaverseAttribute

Deletes a metaverse attribute definition. Built-in attributes cannot be deleted.

#### Syntax

```powershell
# ById (default)
Remove-JIMMetaverseAttribute -Id <int> [-ChangeReason <string>] [-Force]

# ByInputObject
Remove-JIMMetaverseAttribute -InputObject <object> [-ChangeReason <string>] [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the attribute to delete. Accepts pipeline input. |
| `InputObject` | `object` | Yes (ByInputObject) | | An attribute object from the pipeline |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in the object's [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `Force` | `switch` | No | `false` | Suppress confirmation prompts |

!!! warning "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **High** impact level. You will be prompted for confirmation unless `-Force` is specified.

!!! note "Built-in attributes, stored values, and cascade"
    Built-in attributes cannot be deleted. For a custom attribute, deletion is refused while any Metaverse Object holds a stored value for it (clear the values first). When only configuration references exist (Attribute Flows, scoping criteria, Object Matching Rules), they are cascade-removed; the cmdlet satisfies the server's type-the-name confirmation for you, so `-Force` only suppresses the interactive prompt. Use [`Get-JIMMetaverseAttributeDeletionPreview`](#get-jimmetaverseattributedeletionpreview) to inspect the impact first.

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
Get-JIMMetaverseAttribute -Name "legacyCode" | Remove-JIMMetaverseAttribute -Force
```

---

### Test-JIMMetaverseAttributeName

Checks whether a Metaverse Attribute name is available. The comparison is case-insensitive, so "CostCentre" is reported as taken if "costCentre" already exists. Returns `$true` when the name is free, `$false` when it is in use.

#### Syntax

```powershell
Test-JIMMetaverseAttributeName -Name <string> [-ExcludeId <int>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The attribute name to test |
| `ExcludeId` | `int` | No | | An existing attribute ID to exclude from the check (use when renaming, so the attribute's own name is not treated as a clash) |

#### Output

A `[bool]`: `$true` if the name is available, otherwise `$false`.

#### Examples

```powershell title="Guard a create call with an availability check"
if (Test-JIMMetaverseAttributeName -Name "costCentre") {
    New-JIMMetaverseAttribute -Name "costCentre" -Type Text
}
```

```powershell title="Check availability while renaming attribute 42"
Test-JIMMetaverseAttributeName -Name "costCentre" -ExcludeId 42
```

---

### Get-JIMMetaverseAttributeDeletionPreview

Returns a non-destructive assessment of what deleting a custom attribute would entail: whether it is built-in, how many Metaverse Objects hold a stored value (a hard block), the per-Object-Type value breakdown, and the configuration references that would be cascade-removed. Inspect this before calling [`Remove-JIMMetaverseAttribute`](#remove-jimmetaverseattribute).

#### Syntax

```powershell
# ById (default)
Get-JIMMetaverseAttributeDeletionPreview -Id <int>

# ByInputObject
Get-JIMMetaverseAttributeDeletionPreview -InputObject <object>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | The ID of the attribute to preview. Accepts pipeline input. |
| `InputObject` | `object` | Yes (ByInputObject) | | An attribute object from the pipeline |

#### Output

An object describing the deletion impact (`BlockedByValues`, `RequiresConfirmation`, `TotalObjectsWithValues`, `ObjectTypeValueCounts`, `References`, and so on).

#### Examples

```powershell title="Preview the impact of deleting an attribute"
Get-JIMMetaverseAttribute -Name "costCentre" | Get-JIMMetaverseAttributeDeletionPreview
```

---

### Add-JIMMetaverseObjectTypeAttribute

Binds a custom Metaverse Attribute to a Metaverse Object Type, making the attribute available on objects of that type. Binding an already-bound attribute is a no-op; built-in attributes cannot be re-bound.

#### Syntax

```powershell
Add-JIMMetaverseObjectTypeAttribute -AttributeId <int> -ObjectTypeId <int>
    [-ChangeReason <string>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes | | The ID of the attribute to bind. Accepts pipeline input by property name (`Id`). |
| `ObjectTypeId` | `int` | Yes | | The ID of the Metaverse Object Type to bind the attribute to |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `PassThru` | `switch` | No | `false` | Return the updated attribute |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level.

#### Examples

```powershell title="Bind an attribute to an Object Type"
Add-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1
```

```powershell title="Bind the pipeline attribute and return it"
Get-JIMMetaverseAttribute -Name "costCentre" | Add-JIMMetaverseObjectTypeAttribute -ObjectTypeId 1 -PassThru
```

---

### Remove-JIMMetaverseObjectTypeAttribute

Unassigns a custom Metaverse Attribute from a Metaverse Object Type. Follows the same safeguard as attribute deletion: refused while any Metaverse Object of the target type holds a stored value; otherwise the binding, and any Synchronisation Rule references scoped to that type, are cascade-removed behind the server's type-the-name confirmation (which the cmdlet satisfies for you). Built-in attributes cannot be unassigned.

#### Syntax

```powershell
Remove-JIMMetaverseObjectTypeAttribute -AttributeId <int> -ObjectTypeId <int>
    [-ChangeReason <string>] [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes | | The ID of the attribute to unassign. Accepts pipeline input by property name (`Id`). |
| `ObjectTypeId` | `int` | Yes | | The ID of the Metaverse Object Type to unassign the attribute from |
| `ChangeReason` | `string` | No | | Optional reason for the change, recorded in [configuration change history](history.md#get-jimconfigurationchangehistory) |
| `Force` | `switch` | No | `false` | Suppress the interactive confirmation prompt |

!!! warning "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **High** impact level. You will be prompted for confirmation unless `-Force` is specified.

#### Examples

```powershell title="Unassign an attribute from an Object Type"
Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1
```

```powershell title="Unassign without a prompt"
Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1 -Force
```

---

### Get-JIMMetaverseAttributePriority

Gets a metaverse attribute's import priority order: the ordered list of import contributions to the attribute for a given Metaverse Object Type, highest priority first. When more than one Connected System contributes to the same attribute, the highest-priority contributor still connected wins.

#### Syntax

```powershell
Get-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes | | The ID of the Metaverse Attribute |
| `ObjectTypeId` | `int` | Yes | | The ID of the Metaverse Object Type that scopes the priority list |

#### Output

The attribute's priority order, including each contributing mapping's Synchronisation Rule, Connected System, and "Null is a value" flag.

#### Examples

```powershell title="Get the priority order for an attribute"
Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1
```

```powershell title="List just the contributing mappings, in priority order"
(Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1).Contributors
```

---

### Set-JIMMetaverseAttributePriority

Replaces a metaverse attribute's entire import priority order in one call. Every current contributing mapping must be listed exactly once, in the desired priority order.

#### Syntax

```powershell
Set-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int> -MappingId <int[]>
    [-NullIsValueMappingId <int[]>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes | | The ID of the Metaverse Attribute |
| `ObjectTypeId` | `int` | Yes | | The ID of the Metaverse Object Type that scopes the priority list |
| `MappingId` | `int[]` | Yes | | Every current contributing mapping ID, in the desired priority order (highest first) |
| `NullIsValueMappingId` | `int[]` | No | | Mapping IDs (from `-MappingId`) that should have "Null is a value" enabled |
| `PassThru` | `switch` | No | `$false` | Returns the resulting priority order |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

#### Output

If `-PassThru` is specified, returns the resulting priority order.

#### Examples

```powershell title="Set the full priority order"
Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12, 78
```

```powershell title="Set the order and flag a source as authoritative for 'no value'"
Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12 -NullIsValueMappingId 45 -PassThru
```

---

### Move-JIMMetaverseAttributePriority

Repositions a single contributor within a metaverse attribute's priority order, without needing to restate the whole list.

#### Syntax

```powershell
Move-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int> -MappingId <int>
    -Position <int> [-NullIsValue] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes | | The ID of the Metaverse Attribute |
| `ObjectTypeId` | `int` | Yes | | The ID of the Metaverse Object Type that scopes the priority list |
| `MappingId` | `int` | Yes | | The contributing mapping to move. Accepts pipeline input. |
| `Position` | `int` | Yes | | The desired 1-based priority position (1 = highest priority) |
| `NullIsValue` | `switch` | No | | When specified, also enables the moved mapping's "Null is a value" flag |
| `PassThru` | `switch` | No | `$false` | Returns the resulting priority order |

!!! info "ShouldProcess"
    This cmdlet supports `ShouldProcess` with a **Medium** impact level. Use `-WhatIf` to preview changes or `-Confirm` to require confirmation.

#### Output

If `-PassThru` is specified, returns the resulting priority order.

#### Examples

```powershell title="Move a mapping to the highest priority"
Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 1
```

---

## Objects

### Search-JIMMetaverseObject

Searches for Metaverse Objects using a predefined search definition, returning lightweight headers with only the attributes configured in the search. Optimised for fast responses at scale (100k+ objects).

Use this cmdlet for fast list views and searches. Use `Get-JIMMetaverseObject` when you need full object details or custom attribute selection.

#### Syntax

```powershell
# List (default)
Search-JIMMetaverseObject -PredefinedSearchUri <string> [-Search <string>] [-HasAttribute <string>]
    [-SortBy <string>] [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Search-JIMMetaverseObject -PredefinedSearchUri <string> [-Search <string>] [-HasAttribute <string>]
    [-SortBy <string>] [-SortDirection <string>] [-PageSize <int>] -All [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchUri` | `string` | Yes | | URI identifier of the predefined search (e.g. `users`, `groups`) |
| `Search` | `string` | No | | Search query to filter across all string attribute values (case-insensitive, partial match) |
| `HasAttribute` | `string` | No | | Return only objects that hold a value for the named Metaverse Attribute. Matched case-insensitively; a multi-valued attribute counts once; an unrecognised name yields no results. |
| `SortBy` | `string` | No | | Attribute name to sort results by (defaults to creation date) |
| `SortDirection` | `string` | No | `desc` | Sort direction: `asc` or `desc` |
| `All` | `switch` | No | `false` | Automatically paginate through all results. Fetches at most 1000 pages (~100,000 objects at the default page size) and then stops with a warning; a warning is also emitted up front when the result set is large |
| `Force` | `switch` | No | `false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All` |
| `Page` | `int` | No | `1` | Page number for paginated results (cannot be used with `-All`) |
| `PageSize` | `int` | No | `100` | Number of items per page (maximum 100) |

#### Output

Lightweight Metaverse Object headers including ID, display name, object type, and the attributes defined in the predefined search.

#### Examples

```powershell title="Search for users"
Search-JIMMetaverseObject -PredefinedSearchUri "users"
```

```powershell title="Search with a query"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -Search "Smith"
```

```powershell title="Get all users with auto-pagination"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All
```

```powershell title="Get all users, overriding the -All safety cap for a very large result set"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything.
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All -Force
```

```powershell title="Find users that hold a value for an attribute"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre"
```

```powershell title="Sort groups by display name"
Search-JIMMetaverseObject -PredefinedSearchUri "groups" -SortBy "Display Name" -SortDirection asc
```

---

### Get-JIMMetaverseObject

Retrieves Metaverse Objects. Supports searching by ID, object type, attribute values, and wildcard patterns.

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
    [-AttributeName <string> -AttributeValue <string>] [-Attributes <string[]>] -All [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) | | The GUID of a specific Metaverse Object. Accepts pipeline input. |
| `ObjectTypeId` | `int` | No | | Filter by object type ID |
| `ObjectTypeName` | `string` | No | | Filter by object type name |
| `Search` | `string` | No | | Search string; supports wildcards |
| `AttributeName` | `string` | No | | Attribute name to search on; requires `AttributeValue` |
| `AttributeValue` | `string` | No | | Attribute value to match; requires `AttributeName` |
| `Attributes` | `string[]` | No | | Attribute names to include in results; use `"*"` to return all attributes |
| `All` | `switch` | No | `false` | Automatically paginate through all results. Fetches at most 1000 pages (~100,000 objects at the default page size) and then stops with a warning; a warning is also emitted up front when the result set is large |
| `Force` | `switch` | No | `false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All` |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 100) |

#### Output

Metaverse Objects including their ID, object type, and requested attributes.

The object type is returned as a nested `Type` object with `Id` and `Name` properties (for example `$obj.Type.Name`), identical in both the list and single-object responses. (Prior to this release the list response exposed flat `TypeId` and `TypeName` properties instead; this is a breaking change to the output shape.)

When retrieved by ID, each attribute value also carries its provenance: `ContributedBySystemId`/`ContributedBySystemName` identify the Connected System, and `ContributedBySyncRuleId`/`ContributedBySyncRuleName` identify the exact Synchronisation Rule that won [attribute priority resolution](../concepts/attribute-priority.md) and contributed the value. A value row with `NullValue` set to `true` is an asserted null: a deliberate, authoritative "no value" assertion carrying provenance only; treat it as no value present, distinct from the attribute having no row at all.

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

```powershell title="Fetch a very large metaverse, overriding the -All safety cap"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything.
Get-JIMMetaverseObject -ObjectTypeName "Person" -All -Force
```

```powershell title="Page through results manually"
Get-JIMMetaverseObject -ObjectTypeName "Person" -Page 3 -PageSize 50
```

---

### Get-JIMMetaverseObjectChangeHistory

Retrieves the change history for a Metaverse Object. Each record carries the initiator, Synchronisation Rule, and Run Profile context, plus the per-attribute value changes, ordered by change time descending (most recent first).

#### Syntax

```powershell
# Page (default)
Get-JIMMetaverseObjectChangeHistory -Id <guid> [-Page <int>] [-PageSize <int>]

# All
Get-JIMMetaverseObjectChangeHistory -Id <guid> -All [-Force] [-PageSize <int>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Metaverse Object identifier. Accepts pipeline input by property name. |
| `All` | `switch` | No | `$false` | Automatically paginates through all results. Cannot be used with `-Page`. Fetches at most 1000 pages (~50,000 records at the default page size) and then stops with a warning; use `-Force` to fetch beyond the cap. |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |
| `Page` | `int` | No | `1` | Page number for paginated results. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `100`. |

#### Output

Returns one `PSCustomObject` per change record, including the initiator, Synchronisation Rule, Run Profile context, and per-attribute value changes.

#### Examples

```powershell title="Get the most recent page of changes"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through every change for a Metaverse Object"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All
```

```powershell title="Pipe a Metaverse Object into the cmdlet"
Get-JIMMetaverseObject -ObjectTypeName "Group" -Search "Project-Alpha" |
    Get-JIMMetaverseObjectChangeHistory -All
```

---

## Pending Deletions

### Get-JIMPendingDeletion

Retrieves Metaverse Objects that are pending deletion. Supports listing individual items, returning a count, or a summary breakdown by object type.

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

- **List**: pending deletion items including object details and scheduled deletion date. The object type is returned as a nested `Type` object with `Id` and `Name` properties (previously flat `TypeId`/`TypeName`; this is a breaking change to the output shape).
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

- [Metaverse](../configuration/metaverse.md): object types, attributes, objects, and pending deletions
- [Synchronisation Rules](synchronisation-rules.md)
- [Connected Systems](connected-systems.md)
