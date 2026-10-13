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

<!-- BEGIN GENERATED: Get-JIMDataFlow -->
### Syntax

```powershell
# List (default)
Get-JIMDataFlow [-Direction <string>] [-ConnectedSystemId <int>] [-InputObject <PSCustomObject>] [-MetaverseObjectTypeId <int>] [-ConnectedSystemObjectTypeId <int>] [-MetaverseAttributeId <int>] [-ConnectedSystemAttributeId <int>] [-MultipleContributorsOnly] [-Search <string>] [-SyncRuleName <string>]

# ByConnectedSystemName
Get-JIMDataFlow -ConnectedSystemName <string> [-Direction <string>] [-MetaverseObjectTypeId <int>] [-ConnectedSystemObjectTypeId <int>] [-MetaverseAttributeId <int>] [-ConnectedSystemAttributeId <int>] [-MultipleContributorsOnly] [-Search <string>] [-SyncRuleName <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Direction` | `string` | No |  | Only flows in this direction: Import for flows into the Metaverse, Export for flows out of it. Both when omitted. One of `Import` or `Export`. |
| `ConnectedSystemId` | `int` | No (List) |  | Only flows on this Connected System. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByConnectedSystemName) |  | Only flows on the Connected System with exactly this name. |
| `InputObject` | `PSCustomObject` | No (List) |  | A Connected System from the pipeline, for example from `Get-JIMConnectedSystem`. Its Id filters the flows, as `-ConnectedSystemId` would. Accepts pipeline input. |
| `MetaverseObjectTypeId` | `int` | No |  | Only flows on this Metaverse Object Type. |
| `ConnectedSystemObjectTypeId` | `int` | No |  | Only flows on this Connected System Object Type. |
| `MetaverseAttributeId` | `int` | No |  | Only flows that read or write this Metaverse Attribute, whichever side it sits on. A flow whose Metaverse side is an expression cannot match, because an expression's attribute references live in its text; use `-Search` for those. |
| `ConnectedSystemAttributeId` | `int` | No |  | Only flows that read or write this Connected System attribute, with the same expression limitation as `-MetaverseAttributeId`. |
| `MultipleContributorsOnly` | `switch` | No |  | Only Import flows whose target Metaverse Attribute has more than one contributor: the flows whose priority order decides anything. |
| `Search` | `string` | No |  | Text matched, ignoring case, against the Synchronisation Rule, the Connected System, both object types, every attribute named on either side, and expression text. |
| `SyncRuleName` | `string` | No |  | Only flows on Synchronisation Rules with this name, applied after the other filters. Supports wildcards. |

### Output

One object per flow. The cmdlet pages through the whole result set itself.

| Property | Type | Description |
|----------|------|-------------|
| `SyncRuleMappingId` | `int` | The Attribute Flow this row is; identifies the flow. |
| `SyncRuleId` | `int` | The owning Synchronisation Rule. |
| `SyncRuleName` | `string` | The owning Synchronisation Rule's name. |
| `SyncRuleEnabled` | `bool` | Whether the owning Synchronisation Rule is enabled. A disabled rule's flows are shown, because they remain configuration an administrator is reasoning about, but they never move data. |
| `Direction` | `string` | Import, for a flow into the Metaverse, or Export, for a flow out of it. One of `NotSet`, `Import` or `Export`. |
| `ConnectedSystemId` | `int` | The Connected System the owning Synchronisation Rule belongs to. |
| `ConnectedSystemName` | `string` | The name of the Connected System the owning Synchronisation Rule belongs to. |
| `ConnectedSystemObjectTypeId` | `int` | The Connected System Object Type the flow applies to. |
| `ConnectedSystemObjectTypeName` | `string` | The name of the Connected System Object Type the flow applies to. |
| `MetaverseObjectTypeId` | `int` | The Metaverse Object Type the flow applies to. |
| `MetaverseObjectTypeName` | `string` | The name of the Metaverse Object Type the flow applies to. |
| `TargetMetaverseAttributeId` | `int` | The Metaverse Attribute an Import flow writes. Empty on an Export flow, where the Metaverse side is the source. |
| `TargetMetaverseAttributeName` | `string` | The Metaverse Attribute an Import flow writes. Empty on an Export flow. |
| `TargetConnectedSystemAttributeId` | `int` | The Connected System attribute an Export flow writes. Empty on an Import flow, where the Connected System side is the source. |
| `TargetConnectedSystemAttributeName` | `string` | The Connected System attribute an Export flow writes. Empty on an Import flow. |
| `Sources` | `object[]` | What feeds the target, in evaluation order: Connected System attributes on an Import flow, Metaverse Attributes on an Export flow, or an expression on either. Each names its attribute, or carries the expression, with its place in the order and a display name. Each has `Order`, `MetaverseAttributeId`, `MetaverseAttributeName`, `ConnectedSystemAttributeId`, `ConnectedSystemAttributeName`, `Expression`, `IsExpression` and `DisplayName`. |
| `Priority` | `int` | The flow's place in its target Metaverse Attribute's priority order, 1 being highest; Import flows only. A flow that has never been ordered carries 2147483647, which the portal shows as Unranked. |
| `NullIsValue` | `bool` | Whether this contribution asserts "no value" authoritatively rather than falling through to the next contributor. Import flows only. |
| `EnforceState` | `bool` | Whether the owning Export Synchronisation Rule corrects the Connected System when its value diverges from the Metaverse. Export flows only. |
| `ContributorCount` | `int` | How many flows contribute to this flow's target Metaverse Attribute for this Metaverse Object Type, counted across the whole configuration rather than the filtered results; Import flows only. A count above one is what makes priority matter. |
| `HasMultipleContributors` | `bool` | Whether the flow's target Metaverse Attribute has more than one contributor, so its priority order decides which value wins. Import flows only. |
<!-- END GENERATED: Get-JIMDataFlow -->

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
