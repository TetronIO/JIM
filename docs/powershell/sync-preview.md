---
title: Sync Preview
description: "The cmdlets that preview what synchronising one Connected System Object or Metaverse Object would do, against the rules already saved."
type: reference
audience: automation-author
---

# Sync Preview

A Sync Preview answers what synchronising one object would do right now, with the configuration already saved. It changes nothing: it stages no exports and writes no objects. Unlike a [Configuration Change Preview](previews.md), it is synchronous and needs no Activity id: it evaluates one object against the Synchronisation Rules already saved and returns immediately. See [Sync Preview](../configuration/sync-preview.md) for what the outcome tree and the destructive cascade mean.

Both return the same object, tabled under [`Get-JIMConnectedSystemObjectSyncPreview`](#get-jimconnectedsystemobjectsyncpreview). Check `HasBlockingErrors` before reading anything else: a preview that surfaced a blocking error describes a synchronisation that would fail, not one that would succeed as shown.

---

## Get-JIMConnectedSystemObjectSyncPreview

Previews what synchronising a Connected System Object would do: the inbound chain (scope, join or projection, Attribute Flow), the outbound decisions the resulting Metaverse Object state would produce, and, where the object would fall out of scope and disconnect, the destructive cascade (whether the Metaverse Object would be deleted or scheduled for deletion, and which downstream Connected System Objects would be deprovisioned).

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectSyncPreview -->
### Syntax

```powershell
Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId <int> -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The Connected System that owns the object. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes |  | The Connected System Object to preview. Accepts pipeline input by property name. |

### Output

What synchronising the object would do, were the real synchronisation to run now, with the outcome tree flattened beside it. Check HasBlockingErrors first: a preview that surfaced a blocking error describes a synchronisation that would fail.

| Property | Type | Description |
|----------|------|-------------|
| `OutcomeTree` | `object[]` | The outcomes the synchronisation would produce, as a tree, in the same shape as the outcomes recorded on a Run Profile Execution Item. Each has `OutcomeType`, `TargetEntityId`, `TargetEntityDescription`, `SyncRuleId`, `SyncRuleName`, `StagedChangeType`, `DetailCount`, `DetailMessage`, `ObjectMatchingRuleId`, `ObjectMatchingRuleDescriptionSnapshot`, `MatchedValue`, `Ordinal` and `Children`. |
| `Inbound` | `object` | The inbound summary: would the object project or join, and what Attribute Flows would result. Empty for a Metaverse Object preview, which has no inbound chain. It has `WouldProject`, `ProjectedMetaverseObjectTypeId`, `ProjectedMetaverseObjectTypeName`, `WouldJoinMetaverseObjectId`, `WouldJoinObjectMatchingRuleId`, `WouldJoinObjectMatchingRuleDescription`, `WouldJoinMatchedValue`, `AlreadyJoinedMetaverseObjectId` and `AttributeFlowChanges`. |
| `ProposedExports` | `object[]` | The Pending Exports that would be staged per target Connected System, were the real synchronisation to run. None of them is staged: the preview changes nothing. Each has `ConnectedSystemId`, `ChangeType`, `ConnectedSystemObjectId`, `SourceMetaverseObjectId` and `AttributeChanges`. |
| `Errors` | `object[]` | Conditions that would prevent the real synchronisation. Distinct from Warnings so a caller can render blockers and advisories differently without parsing message text. Each has `Code`, `Detail`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `AttributeName` and `MatchCandidates`. |
| `Warnings` | `object[]` | Advisory conditions that would not prevent the real synchronisation. Each has `Code`, `Detail`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `AttributeName` and `MatchCandidates`. |
| `HasBlockingErrors` | `bool` | True when the preview surfaced at least one blocking condition (that is, Errors is non-empty). |
| `AffectedSyncRules` | `object[]` | The Synchronisation Rules that participated at any step of the previewed chain. Each has `Id` and `Name`. |
| `GeneratedValueProbes` | `object[]` | Each value the preview generated that the real synchronisation would also probe for (Unique Value Generation): the preview is a dry run and checks JIM's own records only, while the synchronisation also asks these Connected Systems whether the value is already in use, and generates a different value if one of them already has an account using it. Empty when no generated value would be probed. Each has `AttributeName`, `BuiltFromAttributeName`, `Value` and `ConnectedSystemNames`. |
| `Outcomes` | `object[]` | The outcome tree flattened in its own display order, for Format-Table: one row per outcome, with Depth, OutcomeType, Target (its description, or its id), SyncRule (its name, or its id), DetailCount, DetailMessage and StagedChangeType. A Joined row made by matching also carries ObjectMatchingRule (what the rule compares, or its id) and MatchedValue (the value the join would match on); both are empty on every other row. |
<!-- END GENERATED: Get-JIMConnectedSystemObjectSyncPreview -->

When the object would join, `Inbound` names the [Object Matching Rule](../configuration/object-matching-rules.md) behind the join in `WouldJoinObjectMatchingRuleId` and `WouldJoinObjectMatchingRuleDescription` (what the rule compares), and the value it would match on in `WouldJoinMatchedValue`.

In `Errors`, an `AmbiguousMatch` error lists in `MatchCandidates` the Metaverse Objects the object's Object Matching Rule found. An `AmbiguousProvisioningMatch` error lists the Connected System Objects found before provisioning. Both carry the fields [`Get-JIMActivityExecutionItem`](execution-items.md#get-jimactivityexecutionitem) returns for them apart from `Exists` and `JoinedObjects`.

### Examples

```powershell title="Preview synchronising an object, including any destructive cascade"
Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7"
```

```powershell title="Read the outcome tree as a flat table"
$preview = Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id $csoId
$preview.Outcomes | Format-Table Depth, OutcomeType, Target, SyncRule
```

```powershell title="Check what leaving scope would do to a candidate object"
$objectId = (Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Search "jsmith").Id
$preview = Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id $objectId
$preview.Warnings | Where-Object Code -eq 'OutOfScope' | Select-Object Detail
```

---

## Get-JIMMetaverseObjectSyncPreview

Previews what synchronising from a Metaverse Object's current state would do: the outbound decisions that state would produce. A Metaverse Object is never synchronised itself; its Connected System Objects are, so this cmdlet evaluates the Metaverse Object **as it stands now**, outbound only, with no inbound chain of its own (`Inbound` is always `$null`). For the full inbound-then-outbound chain of one Connected System Object, use [`Get-JIMConnectedSystemObjectSyncPreview`](#get-jimconnectedsystemobjectsyncpreview) instead.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectSyncPreview -->
### Syntax

```powershell
Get-JIMMetaverseObjectSyncPreview -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The Metaverse Object to preview. Accepts pipeline input by property name. |

### Output

What synchronising from the Metaverse Object as it stands now would do, outbound only, with the outcome tree flattened beside it. Inbound is always empty: a Metaverse Object has no inbound chain of its own. Check HasBlockingErrors first.

Properties as under [Get-JIMConnectedSystemObjectSyncPreview](#get-jimconnectedsystemobjectsyncpreview).
<!-- END GENERATED: Get-JIMMetaverseObjectSyncPreview -->

### Examples

```powershell title="Preview what would export from a Metaverse Object as it stands now"
Get-JIMMetaverseObjectSyncPreview -Id "8f14e45f-ceea-467e-adde-3f8cbb1e4d28"
```

```powershell title="List the Connected Systems a pending edit would export to"
$preview = Get-JIMMetaverseObjectSyncPreview -Id $mvoId
$preview.ProposedExports | Format-Table ConnectedSystemId, ChangeType
```

---

## See also

- [Sync Preview](../configuration/sync-preview.md): what these cmdlets evaluate, including the destructive cascade
- [Previews](previews.md): previewing a proposed configuration change across every object it would affect
