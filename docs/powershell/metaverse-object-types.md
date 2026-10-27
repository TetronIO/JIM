---
title: Metaverse Object Types
description: "The cmdlets that list, create, change and delete Metaverse Object Types, including their deletion rules."
type: reference
audience: automation-author
---

# Metaverse Object Types

The Metaverse Object Type cmdlets manage the kinds of identity JIM stores, such as User and Group: list them, create custom ones, rename them, set how their Metaverse Objects are deleted, and delete them. The attributes an Object Type carries are managed with the [Metaverse Attribute](metaverse-attributes.md) cmdlets. What Object Types are, and how each deletion rule behaves, are covered in [Metaverse](../configuration/metaverse.md).

---

## :material-shape-outline: Get-JIMMetaverseObjectType

Gets Metaverse Object Types: one page of them by default, or one by ID or name.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectType -->
### Syntax

```powershell
# List (default)
Get-JIMMetaverseObjectType [-IncludeChildObjects] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseObjectType -Id <int> [-IncludeChildObjects]

# ByName
Get-JIMMetaverseObjectType -Name <string> [-IncludeChildObjects]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Object Type to get. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Object Type to get. |
| `IncludeChildObjects` | `switch` | No |  | Loads each Object Type's attributes. In the list, AttributesCount then counts them; with `-Id` or `-Name`, Attributes lists them. Without it, AttributesCount is 0 and Attributes is empty. |
| `Page` | `int` | No (List) | `1` | The page of Object Types to return, counting from 1. At least 1. |
| `PageSize` | `int` | No (List) | `100` | How many Object Types each page holds. From 1 to 1,000. |

### Output

By default, one page of Object Types, each in summary. With `-Id` or `-Name`, the Object Type in full, with its deletion settings and any warning about them.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Object Type. |
| `Name` | `string` | The Object Type's singular name, such as User or Group. |
| `PluralName` | `string` | The Object Type's plural name, such as Users or Groups. |
| `Created` | `datetime` | When the Object Type was created (UTC). |
| `AttributesCount` | `int` | How many attributes are bound to the Object Type. Counted only when the request includes child objects; 0 otherwise. |
| `BuiltIn` | `bool` | Whether JIM provides the Object Type. A built-in Object Type cannot be renamed or deleted, but its deletion settings can be changed. |
| `Icon` | `string` | The name of the MudBlazor icon the portal shows for the Object Type; empty when none is set. |
| `HasPredefinedSearches` | `bool` | Whether any Predefined Search lists objects of this type. |
| `DeletionRule` | `string` | When JIM deletes this type's Metaverse Objects on its own. One of `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionTriggerMode` | `string` | For the WhenAuthoritativeSourceDisconnected deletion rule, whether every selected source must disconnect before an object is deleted (AllSourcesDisconnect) or any one is enough (SpecificSourcesDisconnect). It has no effect under the other rules. One of `SpecificSourcesDisconnect` or `AllSourcesDisconnect`. |
| `DeletionGracePeriod` | `string` | How long JIM waits, once the deletion rule is met, before it deletes an object; empty or zero deletes at once. |

**With `-Id` or `-Name`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Object Type. |
| `Name` | `string` | The Object Type's singular name, such as User or Group. |
| `PluralName` | `string` | The Object Type's plural name, such as Users or Groups. |
| `Created` | `datetime` | When the Object Type was created (UTC). |
| `BuiltIn` | `bool` | Whether JIM provides the Object Type. A built-in Object Type cannot be renamed or deleted, but its deletion settings can be changed. |
| `Icon` | `string` | The name of the MudBlazor icon the portal shows for the Object Type; empty when none is set. |
| `DeletionRule` | `string` | When JIM deletes this type's Metaverse Objects on its own. One of `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `string` | How long JIM waits, once the deletion rule is met, before it deletes an object; empty or zero deletes at once. |
| `DeletionTriggerMode` | `string` | For the WhenAuthoritativeSourceDisconnected deletion rule, whether every selected source must disconnect before an object is deleted (AllSourcesDisconnect) or any one is enough (SpecificSourcesDisconnect). It has no effect under the other rules. One of `SpecificSourcesDisconnect` or `AllSourcesDisconnect`. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | The IDs of the Connected Systems whose disconnection triggers deletion under the WhenAuthoritativeSourceDisconnected rule. |
| `Attributes` | `object[]` | The attributes bound to the Object Type. Listed only when the request includes child objects, so empty in the result of creating or changing an Object Type. Each has `Id`, `Name`, `Type`, `AttributePlurality` and `BuiltIn`. |
| `DeletionRuleAdvisory` | `string` | A warning about the deletion rule, or empty when there is none. It is set when the rule is WhenLastConnectorDisconnected and enabled export Synchronisation Rules provision objects of this type: a provisioned account counts as a connector, so an object outlives its last source and keeps that source's values. Use WhenAuthoritativeSourceDisconnected if objects should be deprovisioned when their source of record leaves. |
| `DeletionSourceWarnings` | `object[]` | The Connected Systems that project into this type without being one of its selected authoritative sources, filled in only under the WhenAuthoritativeSourceDisconnected rule. Objects such a system creates that no selected source also holds are never deleted automatically, and objects it shares with a selected source are deleted when that source disconnects. Systems that only join are never listed. Always present, and empty when there is nothing to warn about. Each has `ConnectedSystemId` and `ConnectedSystemName`. |
<!-- END GENERATED: Get-JIMMetaverseObjectType -->

### Examples

```powershell title="List Object Types"
Get-JIMMetaverseObjectType
```

```powershell title="Get an Object Type with its attributes"
Get-JIMMetaverseObjectType -Name "User" -IncludeChildObjects
```

```powershell title="Page through Object Types"
Get-JIMMetaverseObjectType -Page 2 -PageSize 50
```

```powershell title="Fail a health check when a projecting system is not an authoritative source"
if ((Get-JIMMetaverseObjectType -Name "User").DeletionSourceWarnings) {
    throw "A Connected System projects into User without being one of its authoritative sources"
}
```

---

## :material-shape-plus-outline: New-JIMMetaverseObjectType

Creates a custom Metaverse Object Type, for identities the built-in User and Group do not fit, such as devices, contractors or service accounts.

<!-- BEGIN GENERATED: New-JIMMetaverseObjectType -->
### Syntax

```powershell
New-JIMMetaverseObjectType -Name <string> -PluralName <string> [-Icon <string>] [-AttributeIds <int[]>] [-DeletionRule <string>] [-DeletionGracePeriod <timespan>] [-ChangeReason <string>] [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The singular name of the new Object Type, such as Device. It must be unique. |
| `PluralName` | `string` | Yes |  | The plural name of the new Object Type, such as Devices. It must be unique. |
| `Icon` | `string` | No |  | The name of the MudBlazor icon the portal shows for the Object Type, such as Devices. |
| `AttributeIds` | `int[]` | No |  | The IDs of existing Metaverse Attributes to bind to the new Object Type. Attributes can also be bound later, with `Add-JIMMetaverseObjectTypeAttribute`. |
| `DeletionRule` | `string` | No |  | When JIM deletes this type's Metaverse Objects on its own. With Manual, it never does. With WhenLastConnectorDisconnected, it deletes an object once no Connected System Object is joined to it; an account JIM has provisioned counts as one. With WhenAuthoritativeSourceDisconnected, it deletes an object when the sources named in `-DeletionTriggerConnectedSystemIds` disconnect, as `-DeletionTriggerMode` decides. WhenLastConnectorDisconnected is the default, as in the portal. One of `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `timespan` | No |  | How long JIM waits, once the deletion rule is met, before it deletes the object. [TimeSpan]::Zero deletes at once. Ignored when `-DeletionRule` is Manual. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the Object Type's Configuration Change History. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No |  | The IDs of the Connected Systems whose disconnection triggers deletion. Required when `-DeletionRule` is WhenAuthoritativeSourceDisconnected. |
| `DeletionTriggerMode` | `string` | No |  | For WhenAuthoritativeSourceDisconnected, how the selected sources trigger deletion. With AllSourcesDisconnect, an object is deleted only once no selected source keeps a joined Connected System Object. With SpecificSourcesDisconnect, it is deleted when any one selected source disconnects, even while others remain. AllSourcesDisconnect is the default. One of `AllSourcesDisconnect` or `SpecificSourcesDisconnect`. |

### Output

The created Object Type in full. When its deletion settings draw a warning, in DeletionRuleAdvisory or DeletionSourceWarnings, the cmdlet also writes it as a warning.

Properties as under [Get-JIMMetaverseObjectType](#get-jimmetaverseobjecttype).
<!-- END GENERATED: New-JIMMetaverseObjectType -->

### Examples

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

```powershell title="Create a type deleted as soon as either HR system disconnects"
New-JIMMetaverseObjectType -Name "Contractor" -PluralName "Contractors" `
    -DeletionRule WhenAuthoritativeSourceDisconnected `
    -DeletionTriggerConnectedSystemIds 3, 7 `
    -DeletionTriggerMode SpecificSourcesDisconnect `
    -DeletionGracePeriod ([TimeSpan]::FromDays(7))
```

---

## :material-pencil-outline: Set-JIMMetaverseObjectType

Changes a Metaverse Object Type: its name, plural name and icon, and how its Metaverse Objects are deleted. A built-in Object Type, such as User or Group, accepts changes to its deletion settings only.

<!-- BEGIN GENERATED: Set-JIMMetaverseObjectType -->
### Syntax

```powershell
# ById (default)
Set-JIMMetaverseObjectType -Id <int> [-NewName <string>] [-PluralName <string>] [-Icon <string>] [-DeletionRule <string>] [-DeletionGracePeriod <timespan>] [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Set-JIMMetaverseObjectType -Name <string> [-NewName <string>] [-PluralName <string>] [-Icon <string>] [-DeletionRule <string>] [-DeletionGracePeriod <timespan>] [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMMetaverseObjectType -InputObject <PSCustomObject> [-NewName <string>] [-PluralName <string>] [-Icon <string>] [-DeletionRule <string>] [-DeletionGracePeriod <timespan>] [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Object Type to change. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Object Type to change. To rename it, use `-NewName`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | An Object Type from the pipeline, for example from `Get-JIMMetaverseObjectType`. Accepts pipeline input. |
| `NewName` | `string` | No |  | A new singular name for the Object Type. It must be unique, ignoring case. A built-in Object Type cannot be renamed. |
| `PluralName` | `string` | No |  | A new plural name for the Object Type. It must be unique, ignoring case. A built-in Object Type keeps its plural name. |
| `Icon` | `string` | No |  | The name of the MudBlazor icon the portal shows for the Object Type, such as Devices. `$null` or '' clears it. A built-in Object Type keeps its icon. |
| `DeletionRule` | `string` | No |  | When JIM deletes this type's Metaverse Objects on its own. With Manual, it never does. With WhenLastConnectorDisconnected, it deletes an object once no Connected System Object is joined to it; an account JIM has provisioned counts as one. With WhenAuthoritativeSourceDisconnected, it deletes an object when the sources named in `-DeletionTriggerConnectedSystemIds` disconnect, as `-DeletionTriggerMode` decides. One of `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `timespan` | No |  | How long JIM waits, once the deletion rule is met, before it deletes the object, for example [TimeSpan]::FromDays(30). [TimeSpan]::Zero deletes at once. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No |  | The IDs of the Connected Systems whose disconnection triggers deletion. Required when the deletion rule is WhenAuthoritativeSourceDisconnected, and ignored otherwise. |
| `DeletionTriggerMode` | `string` | No |  | For WhenAuthoritativeSourceDisconnected, how the selected sources trigger deletion. With AllSourcesDisconnect, an object is deleted only once no selected source keeps a joined Connected System Object. With SpecificSourcesDisconnect, it is deleted when any one selected source disconnects, even while others remain. Omit it to leave the mode unchanged. One of `AllSourcesDisconnect` or `SpecificSourcesDisconnect`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded in the Object Type's Configuration Change History. |
| `PreviewActivityId` | `guid` | No |  | The ActivityId of the Configuration Change Preview read before making this change, from `New-JIMConfigurationChangePreview`. The change's Activity records it, so the audit shows what the change was expected to do as well as what it did. |
| `PassThru` | `switch` | No |  | Returns the updated Object Type. |

### Output

With `-PassThru`, the updated Object Type in full; otherwise nothing. When its deletion settings draw a warning, in DeletionRuleAdvisory or DeletionSourceWarnings, the cmdlet also writes it as a warning.

Properties as under [Get-JIMMetaverseObjectType](#get-jimmetaverseobjecttype).
<!-- END GENERATED: Set-JIMMetaverseObjectType -->

Each deletion rule, trigger mode and grace period is explained in [Deletion behaviour](../configuration/metaverse.md#deletion-behaviour), including why a provisioned account keeps an object under `WhenLastConnectorDisconnected`, and what a projecting system that is not an authoritative source means under `WhenAuthoritativeSourceDisconnected`. To see what a change would do before making it, preview it with [`New-JIMConfigurationChangePreview`](previews.md#new-jimconfigurationchangepreview) and pass the preview's `ActivityId` as `-PreviewActivityId`.

### Examples

```powershell title="Set the deletion rule by name"
Set-JIMMetaverseObjectType -Name "User" -DeletionRule WhenLastConnectorDisconnected
```

```powershell title="Configure a 30-day grace period with pipeline input"
Get-JIMMetaverseObjectType -Name "Group" | Set-JIMMetaverseObjectType -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionGracePeriod "30.00:00:00" -PassThru
```

```powershell title="Set deletion triggers for specific Connected Systems"
Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds @(3, 7)
```

```powershell title="Require both HR systems to disconnect before deletion"
Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds @(3, 7) -DeletionTriggerMode AllSourcesDisconnect
```

```powershell title="Rename a custom type and set its icon"
Set-JIMMetaverseObjectType -Id 5 -NewName "Gadget" -PluralName "Gadgets" -Icon "Devices"
```

```powershell title="Clear a custom type's icon"
Set-JIMMetaverseObjectType -Id 5 -Icon $null
```

```powershell title="Apply a previewed change, recording which preview informed it"
$preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -PreviewActivityId $preview.ActivityId
```

---

## :material-trash-can-outline: Remove-JIMMetaverseObjectType

Deletes a custom Metaverse Object Type.

<!-- BEGIN GENERATED: Remove-JIMMetaverseObjectType -->
### Syntax

```powershell
# ById (default)
Remove-JIMMetaverseObjectType -Id <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]

# ByName
Remove-JIMMetaverseObjectType -Name <string> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Object Type to delete. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Object Type to delete. |
| `ChangeReason` | `string` | No |  | An optional reason for the deletion, recorded in the Object Type's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. The cmdlet still gives the name confirmation JIM requires before it removes the configuration that goes with the Object Type. |

### Output

None.
<!-- END GENERATED: Remove-JIMMetaverseObjectType -->

JIM refuses to delete a built-in Object Type, one that any Metaverse Object belongs to, or one that any Synchronisation Rule targets, and the cmdlet says which. Otherwise the Object Type's Predefined Searches, Example Data Template entries and attribute bindings are removed with it, the attributes themselves kept, and each removal is recorded as an Activity. JIM asks for the Object Type's name to confirm a deletion that removes configuration; the cmdlet gives it once you confirm, or with `-Force`.

### Examples

```powershell title="Delete a custom type by name"
Remove-JIMMetaverseObjectType -Name "Device" -Force
```

```powershell title="Check that a deletion is allowed, without deleting"
Remove-JIMMetaverseObjectType -Id 5 -WhatIf
```

---

## :material-book-open-variant-outline: See also

- [Metaverse Attributes](metaverse-attributes.md): the attributes an Object Type carries, and their priority
- [Metaverse cmdlets](metaverse.md): query Metaverse Objects and review pending deletions
- [Metaverse](../configuration/metaverse.md): Object Types, deletion rules and grace periods
- [Previews](previews.md): preview a change to an Object Type's deletion settings before making it
