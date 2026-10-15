---
title: Metaverse Attributes
description: "The cmdlets that manage Metaverse Attributes, bind them to Object Types, and set the priority of the Connected Systems that contribute their values."
type: reference
audience: automation-author
---

# Metaverse Attributes

The Metaverse Attribute cmdlets manage the attributes a Metaverse Object can hold: list, create, change and delete them, bind them to [Metaverse Object Types](metaverse-object-types.md), and order the Connected Systems that contribute a value to each. What Metaverse Attributes are is covered in [Metaverse](../configuration/metaverse.md), and how contributions are ordered in [Attribute Priority](../concepts/attribute-priority.md).

---

## Get-JIMMetaverseAttribute

Gets Metaverse Attributes: one page of them by default, or one by ID or name.

<!-- BEGIN GENERATED: Get-JIMMetaverseAttribute -->
### Syntax

```powershell
# List (default)
Get-JIMMetaverseAttribute [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseAttribute -Id <int>

# ByName
Get-JIMMetaverseAttribute -Name <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the attribute to get. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the attribute to get, matched exactly but ignoring case. |
| `Page` | `int` | No (List) | `1` | The page of attributes to return, counting from 1. At least 1. |
| `PageSize` | `int` | No (List) | `100` | How many attributes each page holds. From 1 to 1,000. |

### Output

By default, one page of attributes, each in summary with the Object Types it is bound to. With `-Id` or `-Name`, the attribute in full, with its rendering hint and Standard Mappings.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the attribute. |
| `Created` | `datetime` | When the attribute was created (UTC). |
| `Name` | `string` | The attribute's name. |
| `Type` | `string` | The kind of value the attribute holds. One of `NotSet`, `Text`, `Number`, `DateTime`, `Binary`, `Reference`, `Guid`, `Boolean`, `LongNumber` or `Decimal`. |
| `AttributePlurality` | `string` | Whether the attribute holds one value or many. One of `SingleValued` or `MultiValued`. |
| `BuiltIn` | `bool` | Whether JIM provides the attribute. A built-in attribute cannot be changed or deleted. |
| `MetaverseObjectTypes` | `object[]` | The Object Types the attribute is bound to, each as a Key (the Object Type's ID) and a Value (its name). Each has `Key` and `Value`. |

**With `-Id` or `-Name`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the attribute. |
| `Name` | `string` | The attribute's name. |
| `Created` | `datetime` | When the attribute was created (UTC). |
| `Type` | `string` | The kind of value the attribute holds. One of `NotSet`, `Text`, `Number`, `DateTime`, `Binary`, `Reference`, `Guid`, `Boolean`, `LongNumber` or `Decimal`. |
| `AttributePlurality` | `string` | Whether the attribute holds one value or many. One of `SingleValued` or `MultiValued`. |
| `BuiltIn` | `bool` | Whether JIM provides the attribute. A built-in attribute cannot be changed or deleted. |
| `RenderingHint` | `string` | How the portal shows a multi-valued attribute's values. Ignored for a single-valued attribute. One of `Default`, `Table`, `ChipSet` or `List`. |
| `ObjectTypes` | `object[]` | The Object Types the attribute is bound to. Each has `Id` and `Name`. |
| `StandardMappings` | `object[]` | The attribute's counterparts in other standards, such as SCIM 2.0 and LDAP. They are guidance only; synchronisation never uses them. Each has `Standard`, `CounterpartName` and `Notes`. |
<!-- END GENERATED: Get-JIMMetaverseAttribute -->

### Examples

```powershell title="List attributes"
Get-JIMMetaverseAttribute
```

```powershell title="Get a specific attribute by name"
Get-JIMMetaverseAttribute -Name "Display Name"
```

```powershell title="Page through attributes"
Get-JIMMetaverseAttribute -Page 1 -PageSize 50
```

---

## New-JIMMetaverseAttribute

Creates a custom Metaverse Attribute, optionally bound to Metaverse Object Types.

<!-- BEGIN GENERATED: New-JIMMetaverseAttribute -->
### Syntax

```powershell
New-JIMMetaverseAttribute -Name <string> -Type <string> [-AttributePlurality <string>] [-ObjectTypeIds <int[]>] [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name of the new attribute. It must be unique, ignoring case. |
| `Type` | `string` | Yes |  | The kind of value the attribute holds. Integer is the type JIM names Number. One of `Text`, `Integer`, `LongNumber`, `Decimal`, `DateTime`, `Boolean`, `Reference`, `Guid` or `Binary`. |
| `AttributePlurality` | `string` | No | `'SingleValued'` | Whether the attribute holds one value or many. A Boolean holds one value, so JIM refuses MultiValued for a Boolean. One of `SingleValued` or `MultiValued`. |
| `ObjectTypeIds` | `int[]` | No |  | The IDs of the Metaverse Object Types to bind the attribute to. It can also be bound later, with `Add-JIMMetaverseObjectTypeAttribute`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the attribute's Configuration Change History. |

### Output

The created attribute in full.

Properties as under [Get-JIMMetaverseAttribute](#get-jimmetaverseattribute).
<!-- END GENERATED: New-JIMMetaverseAttribute -->

### Examples

```powershell title="Create a simple text attribute"
New-JIMMetaverseAttribute -Name "Cost Centre" -Type Text
```

```powershell title="Create a multi-valued reference attribute"
New-JIMMetaverseAttribute -Name "Direct Reports" -Type Reference -AttributePlurality MultiValued
```

```powershell title="Create an attribute bound to two Object Types"
$userType = Get-JIMMetaverseObjectType -Name "User"
$groupType = Get-JIMMetaverseObjectType -Name "Group"
New-JIMMetaverseAttribute -Name "Department" -Type Text -ObjectTypeIds @($userType.Id, $groupType.Id)
```

---

## Set-JIMMetaverseAttribute

Changes a custom Metaverse Attribute: its name, rendering hint, Standard Mappings, data type or plurality. A built-in attribute cannot be changed.

<!-- BEGIN GENERATED: Set-JIMMetaverseAttribute -->
### Syntax

```powershell
# ById (default)
Set-JIMMetaverseAttribute -Id <int> [-Name <string>] [-RenderingHint <string>] [-Type <string>] [-AttributePlurality <string>] [-StandardMappings <Array>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMMetaverseAttribute -InputObject <PSCustomObject> [-Name <string>] [-RenderingHint <string>] [-Type <string>] [-AttributePlurality <string>] [-StandardMappings <Array>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the attribute to change. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | An attribute from the pipeline, for example from `Get-JIMMetaverseAttribute`. Accepts pipeline input. |
| `Name` | `string` | No |  | A new name for the attribute. It must be unique, ignoring case. |
| `RenderingHint` | `string` | No |  | How the portal shows a multi-valued attribute's values. Ignored for a single-valued attribute. One of `Default`, `Table`, `ChipSet` or `List`. |
| `Type` | `string` | No |  | A new kind of value for the attribute; Integer is the type JIM names Number. JIM refuses the change while any Metaverse Object holds a value for the attribute. A Boolean holds one value, so Boolean without `-AttributePlurality` also makes the attribute single-valued. One of `Text`, `Integer`, `LongNumber`, `Decimal`, `DateTime`, `Boolean`, `Reference`, `Guid` or `Binary`. |
| `AttributePlurality` | `string` | No |  | Whether the attribute holds one value or many. JIM refuses the change while any Metaverse Object holds a value for the attribute, and refuses MultiValued for a Boolean. One of `SingleValued` or `MultiValued`. |
| `StandardMappings` | `Array` | No |  | The attribute's Standard Mappings, replacing any it has; @() clears them. Each is a hashtable with a Standard (Scim, Ldap or Jim), a CounterpartName (the attribute's name in that standard) and optional Notes. Standard Mappings are guidance only and never affect synchronisation. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the attribute's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated attribute. |

### Output

With `-PassThru`, the updated attribute in full; otherwise nothing.

Properties as under [Get-JIMMetaverseAttribute](#get-jimmetaverseattribute).
<!-- END GENERATED: Set-JIMMetaverseAttribute -->

JIM refuses a change of `-Type` or `-AttributePlurality` while any Metaverse Object holds a value for the attribute; clear the values first. Giving either sends both, the other as the attribute has it now, except that `-Type Boolean` alone makes the attribute single-valued, since a Boolean holds one value. To bind the attribute to an Object Type or unbind it, use [`Add-JIMMetaverseObjectTypeAttribute`](#add-jimmetaverseobjecttypeattribute) and [`Remove-JIMMetaverseObjectTypeAttribute`](#remove-jimmetaverseobjecttypeattribute).

### Examples

```powershell title="Rename an attribute"
Set-JIMMetaverseAttribute -Id 42 -Name "costCentreCode" -PassThru
```

```powershell title="Set the rendering hint for a multi-valued attribute"
Get-JIMMetaverseAttribute -Name "proxyAddresses" | Set-JIMMetaverseAttribute -RenderingHint List
```

```powershell title="Change an attribute's data type (refused if any object holds a value)"
Set-JIMMetaverseAttribute -Id 42 -Type Integer
```

```powershell title="Record how a custom attribute corresponds to the SCIM 2.0 and LDAP standards"
Set-JIMMetaverseAttribute -Id 42 -StandardMappings @(
    @{ Standard = 'Scim'; CounterpartName = 'costCenter'; Notes = 'SCIM Enterprise User extension.' },
    @{ Standard = 'Ldap'; CounterpartName = 'costCentre' }
)
```

---

## Remove-JIMMetaverseAttribute

Deletes a custom Metaverse Attribute. A built-in attribute cannot be deleted.

<!-- BEGIN GENERATED: Remove-JIMMetaverseAttribute -->
### Syntax

```powershell
# ById (default)
Remove-JIMMetaverseAttribute -Id <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMMetaverseAttribute -InputObject <PSCustomObject> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the attribute to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | An attribute from the pipeline, for example from `Get-JIMMetaverseAttribute`. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | An optional reason for the deletion, recorded in the attribute's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. The cmdlet still gives the name confirmation JIM requires before it removes configuration that uses the attribute. |

### Output

None.
<!-- END GENERATED: Remove-JIMMetaverseAttribute -->

JIM refuses the deletion while any Metaverse Object holds a value for the attribute; clear the values first. Otherwise the configuration that uses the attribute (its Object Type bindings, Attribute Flows, Scoping Criteria and Object Matching Rules) is removed with it, and each removal is recorded as an Activity. JIM asks for the attribute's name to confirm a deletion that removes configuration; the cmdlet gives it once you confirm, or with `-Force`. To see what a deletion would remove, use [`Get-JIMMetaverseAttributeDeletionPreview`](#get-jimmetaverseattributedeletionpreview) first.

### Examples

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

## Test-JIMMetaverseAttributeName

Checks whether a name is free for a Metaverse Attribute. The check ignores case, so CostCentre is taken if costCentre exists.

<!-- BEGIN GENERATED: Test-JIMMetaverseAttributeName -->
### Syntax

```powershell
Test-JIMMetaverseAttributeName -Name <string> [-ExcludeId <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The attribute name to test. |
| `ExcludeId` | `int` | No |  | The ID of an attribute to leave out of the check. Pass the attribute's own ID when renaming it, so its current name does not count as taken. |

### Output

`$true` when the name is free, or `$false` when an attribute already has it.

Returns a `bool`.
<!-- END GENERATED: Test-JIMMetaverseAttributeName -->

### Examples

```powershell title="Guard a create call with an availability check"
if (Test-JIMMetaverseAttributeName -Name "costCentre") {
    New-JIMMetaverseAttribute -Name "costCentre" -Type Text
}
```

```powershell title="Check availability while renaming attribute 42"
Test-JIMMetaverseAttributeName -Name "costCentre" -ExcludeId 42
```

---

## Get-JIMMetaverseAttributeDeletionPreview

Shows what deleting a custom Metaverse Attribute would do, without deleting it: whether stored values block it, which Object Types hold them, and the configuration that would be removed with it. [`Remove-JIMMetaverseAttribute`](#remove-jimmetaverseattribute) makes the same assessment before it deletes.

<!-- BEGIN GENERATED: Get-JIMMetaverseAttributeDeletionPreview -->
### Syntax

```powershell
# ById (default)
Get-JIMMetaverseAttributeDeletionPreview -Id <int>

# ByInputObject
Get-JIMMetaverseAttributeDeletionPreview -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the attribute to assess. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | An attribute from the pipeline, for example from `Get-JIMMetaverseAttribute`. Accepts pipeline input. |

### Output

What deleting the attribute would do: whether stored values block it, how many Metaverse Objects of each Object Type hold one, and the configuration that would be removed with it.

| Property | Type | Description |
|----------|------|-------------|
| `AttributeId` | `int` | The unique identifier of the attribute. |
| `AttributeName` | `string` | The attribute's name. |
| `BuiltIn` | `bool` | True if the attribute is built-in. Built-in attributes can never be deleted. |
| `TotalObjectsWithValues` | `int` | The total number of Metaverse Objects (across all types) that hold a stored value for the attribute. |
| `ObjectTypeValueCounts` | `object[]` | The per-Object-Type breakdown of Metaverse Objects holding a stored value for the attribute. Each has `MetaverseObjectTypeId`, `MetaverseObjectTypeName`, `MetaverseObjectTypePluralName` and `ObjectCount`. |
| `References` | `object[]` | The configuration references (bindings, Attribute Flows, scoping criteria, Object Matching Rules) that would be cascade-removed, in dependency order, when the deletion proceeds. Empty when the attribute is unreferenced. Each has `Kind`, `Id`, `Description`, `SyncRuleId`, `SyncRuleName`, `MetaverseObjectTypeId` and `MetaverseObjectTypeName`. |
| `Deleted` | `bool` | Whether the attribute was deleted: always false in a preview, and false when the deletion was refused. |
| `BlockedByValues` | `bool` | Whether stored values block the deletion. Clear them before deleting the attribute. |
| `RequiresConfirmation` | `bool` | Whether deleting would also remove configuration that uses the attribute, so JIM asks for the attribute's name to confirm it. |
<!-- END GENERATED: Get-JIMMetaverseAttributeDeletionPreview -->

### Examples

```powershell title="Preview the impact of deleting an attribute"
Get-JIMMetaverseAttribute -Name "costCentre" | Get-JIMMetaverseAttributeDeletionPreview
```

---

## Add-JIMMetaverseObjectTypeAttribute

Binds a custom Metaverse Attribute to a Metaverse Object Type, so that type's Metaverse Objects can hold it. Binding an attribute that is already bound does nothing. A built-in attribute's bindings cannot be changed.

<!-- BEGIN GENERATED: Add-JIMMetaverseObjectTypeAttribute -->
### Syntax

```powershell
Add-JIMMetaverseObjectTypeAttribute -AttributeId <int> -ObjectTypeId <int> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes |  | The ID of the attribute to bind. Accepts pipeline input by property name, `AttributeId` or `Id`. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type to bind it to. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the attribute's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated attribute. |

### Output

With `-PassThru`, the updated attribute in full; otherwise nothing.

Properties as under [Get-JIMMetaverseAttribute](#get-jimmetaverseattribute).
<!-- END GENERATED: Add-JIMMetaverseObjectTypeAttribute -->

### Examples

```powershell title="Bind an attribute to an Object Type"
Add-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1
```

```powershell title="Bind an attribute from the pipeline and return it"
Get-JIMMetaverseAttribute -Name "costCentre" | Add-JIMMetaverseObjectTypeAttribute -ObjectTypeId 1 -PassThru
```

---

## Remove-JIMMetaverseObjectTypeAttribute

Unbinds a custom Metaverse Attribute from a Metaverse Object Type. A built-in attribute cannot be unbound.

<!-- BEGIN GENERATED: Remove-JIMMetaverseObjectTypeAttribute -->
### Syntax

```powershell
Remove-JIMMetaverseObjectTypeAttribute -AttributeId <int> -ObjectTypeId <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes |  | The ID of the attribute to unbind. Accepts pipeline input by property name, `AttributeId` or `Id`. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type to unbind it from. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the attribute's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. The cmdlet still gives the name confirmation JIM requires before it removes configuration that uses the attribute on that Object Type. |

### Output

None.
<!-- END GENERATED: Remove-JIMMetaverseObjectTypeAttribute -->

JIM refuses while any Metaverse Object of that type holds a value for the attribute. Otherwise it removes the binding, with the Attribute Flows, Scoping Criteria and Object Matching Rules that use the attribute in Synchronisation Rules for that Object Type; JIM asks for the attribute's name to confirm, and the cmdlet gives it once you confirm, or with `-Force`. If the attribute is not bound to the Object Type, the cmdlet says so and changes nothing.

### Examples

```powershell title="Unbind an attribute from an Object Type"
Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1
```

```powershell title="Unbind without a prompt"
Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1 -Force
```

---

## Get-JIMMetaverseAttributePriority

Gets a Metaverse Attribute's priority order on one Metaverse Object Type: the Attribute Flows that contribute its value, highest priority first. How the order decides which value wins, and what **Null is a value** changes, are explained in [Attribute Priority](../concepts/attribute-priority.md). An Attribute Flow whose Synchronisation Rule is disabled keeps its place but never contributes.

<!-- BEGIN GENERATED: Get-JIMMetaverseAttributePriority -->
### Syntax

```powershell
Get-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes |  | The ID of the Metaverse Attribute. Accepts pipeline input by property name. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type the priority order applies to. Accepts pipeline input by property name. |

### Output

The attribute's priority order on that Object Type: each contributing Attribute Flow, highest priority first.

| Property | Type | Description |
|----------|------|-------------|
| `MetaverseObjectTypeId` | `int` | The unique identifier of the Object Type the order applies to. |
| `MetaverseAttributeId` | `int` | The unique identifier of the attribute. |
| `MetaverseAttributeName` | `string` | The attribute's name; empty when nothing contributes to it. |
| `Contributors` | `object[]` | The Attribute Flows that contribute a value to the attribute, highest priority first; empty when none does. Each has `MappingId`, `Priority`, `NullIsValue`, `SyncRuleId`, `SyncRuleName`, `SyncRuleEnabled`, `ConnectedSystemId` and `ConnectedSystemName`. |
<!-- END GENERATED: Get-JIMMetaverseAttributePriority -->

### Examples

```powershell title="Get the priority order for an attribute"
Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1
```

```powershell title="List just the contributing Attribute Flows, in priority order"
(Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1).Contributors
```

---

## Set-JIMMetaverseAttributePriority

Replaces a Metaverse Attribute's whole priority order on one Metaverse Object Type. List every contributing Attribute Flow once, highest priority first; an Attribute Flow whose Synchronisation Rule is still being deleted may be left out, and stays at the bottom. JIM refuses an order that misses a contributor or names a mapping that is not one, and says which. To move a single Attribute Flow, use [`Move-JIMMetaverseAttributePriority`](#move-jimmetaverseattributepriority).

<!-- BEGIN GENERATED: Set-JIMMetaverseAttributePriority -->
### Syntax

```powershell
Set-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int> -MappingId <int[]> [-NullIsValueMappingId <int[]>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes |  | The ID of the Metaverse Attribute. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type the priority order applies to. |
| `MappingId` | `int[]` | Yes |  | Every contributing Attribute Flow mapping ID, once each, highest priority first. A mapping whose Synchronisation Rule is being deleted may be left out; it stays at the bottom. |
| `NullIsValueMappingId` | `int[]` | No |  | The mapping IDs, from `-MappingId`, whose "Null is a value" flag is set, so that source can assert the attribute has no value rather than defer to the next source. Every other mapping has the flag cleared. |
| `PassThru` | `switch` | No |  | Returns the resulting priority order. |

### Output

With `-PassThru`, the resulting priority order; otherwise nothing.

Properties as under [Get-JIMMetaverseAttributePriority](#get-jimmetaverseattributepriority).
<!-- END GENERATED: Set-JIMMetaverseAttributePriority -->

### Examples

```powershell title="Set the full priority order"
Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12, 78
```

```powershell title="Set the order, and let the top source assert that there is no value"
Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12 -NullIsValueMappingId 45 -PassThru
```

---

## Move-JIMMetaverseAttributePriority

Moves one Attribute Flow to a new position in a Metaverse Attribute's priority order, renumbering the others so the order stays without gaps.

<!-- BEGIN GENERATED: Move-JIMMetaverseAttributePriority -->
### Syntax

```powershell
Move-JIMMetaverseAttributePriority -AttributeId <int> -ObjectTypeId <int> -MappingId <int> -Position <int> [-NullIsValue] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `AttributeId` | `int` | Yes |  | The ID of the Metaverse Attribute. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type the priority order applies to. |
| `MappingId` | `int` | Yes |  | The ID of the contributing Attribute Flow mapping to move. Accepts pipeline input by property name. |
| `Position` | `int` | Yes |  | The position to move it to, counting from 1 for the highest priority. |
| `NullIsValue` | `switch` | No |  | Sets the moved mapping's "Null is a value" flag; `-NullIsValue:$false` clears it. Omit it to leave the flag as it is. |
| `PassThru` | `switch` | No |  | Returns the resulting priority order. |

### Output

With `-PassThru`, the resulting priority order; otherwise nothing.

Properties as under [Get-JIMMetaverseAttributePriority](#get-jimmetaverseattributepriority).
<!-- END GENERATED: Move-JIMMetaverseAttributePriority -->

### Examples

```powershell title="Move an Attribute Flow to the highest priority"
Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 1
```

---

## See also

- [Metaverse Object Types](metaverse-object-types.md): the Object Types an attribute is bound to
- [Metaverse](../configuration/metaverse.md): built-in and custom attributes, Standard Mappings, and deleting attributes
- [Attribute Priority](../concepts/attribute-priority.md): how contributions are ordered, and Null is a value
- [Synchronisation Rules](synchronisation-rules.md): the Attribute Flows that contribute to an attribute
