---
title: "Data Flow"
description: "Get-JIMDataFlow: list every configured Attribute Flow across all Connected Systems, in both directions, with the same filters as the portal."
type: reference
audience: automation-author
---

# Data Flow

`Get-JIMDataFlow` lists every attribute data flow configured across all Connected Systems, in both directions, with the same filters as the portal's **Data Flow** view. One flow is one Attribute Flow on one Synchronisation Rule. What a flow is, and how to read its priority and settings, is explained in [Data Flow](../configuration/data-flow.md); this page is the cmdlet's reference.

## Get-JIMDataFlow

Gets attribute data flows. An Import flow reads Connected System attributes and writes a single Metaverse Attribute, so it carries a priority and a "Null is a value" flag. An Export flow reads Metaverse Attributes and writes a single Connected System attribute, so it carries the owning rule's Enforce State. Properties that do not apply to a flow's direction are `$null`, never defaulted, so a value that is present always means something.

The filters combine with AND. The cmdlet pages through the whole result set itself.

### Syntax

```powershell
# List (default)
Get-JIMDataFlow [-Direction <string>] [-ConnectedSystemId <int>] [-InputObject <PSCustomObject>]
    [-MetaverseObjectTypeId <int>] [-ConnectedSystemObjectTypeId <int>] [-MetaverseAttributeId <int>]
    [-ConnectedSystemAttributeId <int>] [-MultipleContributorsOnly] [-Search <string>] [-SyncRuleName <string>]

# By Connected System name
Get-JIMDataFlow -ConnectedSystemName <string> [-Direction <string>] [-MetaverseObjectTypeId <int>]
    [-ConnectedSystemObjectTypeId <int>] [-MetaverseAttributeId <int>] [-ConnectedSystemAttributeId <int>]
    [-MultipleContributorsOnly] [-Search <string>] [-SyncRuleName <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Direction` | string | No | (both) | `Import` for flows into the Metaverse, `Export` for flows out of it |
| `ConnectedSystemId` | int | No | | Only flows on this Connected System. Binds by property name from the pipeline |
| `ConnectedSystemName` | string | Yes (ByConnectedSystemName) | | Only flows on the Connected System with exactly this name |
| `InputObject` | PSCustomObject | No | | A Connected System from the pipeline (for example from `Get-JIMConnectedSystem`); its `Id` filters the flows, as `-ConnectedSystemId` would |
| `MetaverseObjectTypeId` | int | No | | Only flows on this Metaverse Object Type |
| `ConnectedSystemObjectTypeId` | int | No | | Only flows on this Connected System Object Type |
| `MetaverseAttributeId` | int | No | | Only flows that read or write this Metaverse Attribute, whichever side it sits on. A flow whose Metaverse side is an expression cannot match, because an expression's attribute references live in its text; use `-Search` for those |
| `ConnectedSystemAttributeId` | int | No | | Only flows that read or write this Connected System attribute, with the same expression limitation as `-MetaverseAttributeId` |
| `MultipleContributorsOnly` | switch | No | | Only Import flows whose target Metaverse Attribute has more than one contributor: the flows whose priority order decides anything |
| `Search` | string | No | | Text matched, ignoring case, against the Synchronisation Rule, the Connected System, both object types, every attribute named on either side, and expression text |
| `SyncRuleName` | string | No | | Only flows on Synchronisation Rules with this name. Supports wildcards (`HR*`), and is applied after the other filters |

### Output

One object per flow:

| Property | Type | Description |
|----------|------|-------------|
| `SyncRuleMappingId` | int | The Attribute Flow this row is; identifies the flow |
| `SyncRuleId`, `SyncRuleName` | int, string | The owning Synchronisation Rule |
| `SyncRuleEnabled` | bool | Whether that rule is enabled. A disabled rule's flows are listed, but move no data |
| `Direction` | string | `Import` or `Export` |
| `ConnectedSystemId`, `ConnectedSystemName` | int, string | The Connected System the rule belongs to |
| `ConnectedSystemObjectTypeId`, `ConnectedSystemObjectTypeName` | int, string | The Connected System Object Type the flow applies to |
| `MetaverseObjectTypeId`, `MetaverseObjectTypeName` | int, string | The Metaverse Object Type the flow applies to |
| `TargetMetaverseAttributeId`, `TargetMetaverseAttributeName` | int, string | The Metaverse Attribute an Import flow writes; `$null` on an Export flow |
| `TargetConnectedSystemAttributeId`, `TargetConnectedSystemAttributeName` | int, string | The Connected System attribute an Export flow writes; `$null` on an Import flow |
| `Sources` | array | What feeds the target, in evaluation order: each has `Order`, the attribute (`ConnectedSystemAttributeId` and `ConnectedSystemAttributeName` on an Import flow, `MetaverseAttributeId` and `MetaverseAttributeName` on an Export flow) or an `Expression`, plus `IsExpression` and a `DisplayName` |
| `Priority` | int | The flow's place in its target's [Attribute Priority](../concepts/attribute-priority.md) order, 1 being highest; Import flows only. A flow that has never been ordered carries 2147483647, which the portal shows as **Unranked** |
| `NullIsValue` | bool | Whether this contribution asserts "no value" rather than falling through to the next contributor; Import flows only |
| `EnforceState` | bool | Whether the owning export rule corrects the Connected System when its value differs from the Metaverse; Export flows only |
| `ContributorCount` | int | How many flows contribute to the same target Metaverse Attribute, counted across the whole configuration rather than the filtered results; Import flows only |
| `HasMultipleContributors` | bool | Whether `ContributorCount` is more than one |

### Examples

```powershell title="Every flow, in both directions"
Get-JIMDataFlow
```

```powershell title="The Import flows whose priority order decides which value wins"
Get-JIMDataFlow -Direction Import -MultipleContributorsOnly
```

```powershell title="Everything that touches department, including expressions that mention it"
Get-JIMDataFlow -Search 'department'
```

```powershell title="What JIM writes to one Connected System, and from where"
Get-JIMDataFlow -ConnectedSystemName 'Contoso AD' -Direction Export
```

```powershell title="Contributions that assert an authoritative 'no value'"
Get-JIMDataFlow -Direction Import | Where-Object { $_.NullIsValue } |
    Select-Object TargetMetaverseAttributeName, SyncRuleName, Priority
```

```powershell title="The flows of every Connected System whose name starts with HR"
Get-JIMConnectedSystem -Name 'HR*' | Get-JIMDataFlow
```

## Related

- [Data Flow](../configuration/data-flow.md): what a flow is, and how to read it in the portal
- [Attribute Priority](../concepts/attribute-priority.md): how JIM decides which contribution wins
- [Synchronisation Rules](synchronisation-rules.md): the cmdlets that create and change Attribute Flows
