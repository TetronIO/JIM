---
title: Previews
description: "The cmdlets that preview what a configuration change would do before it is saved, and read, compare and stop those previews."
type: reference
audience: automation-author
---

# Previews

JIM has two families of preview cmdlet, answering two different questions:

- **Configuration Change Previews** (`New-JIMConfigurationChangePreview` and friends, below) answer what a *proposed edit* would do, across every object it would affect: JIM evaluates the change against the objects already in the Metaverse and reports which of them would be affected, changing nothing.
- **[Sync Preview](sync-preview.md)** cmdlets answer what synchronising *one object* would do **right now**, with the configuration you already have saved.

```jim-diagram
archetype: decision
title: Whether a Configuration Change Preview can be acted on
desc: "Read a Configuration Change Preview in this order. If the start result's IsBlocked is true, the proposal was never evaluated, and its blocking finding says why. Otherwise, if HasFailed is true, a stage failed and only some of the objects were evaluated, so the counts cannot be trusted. Otherwise, if IsComplete is false, the preview is still running or was stopped: read it again, or start a new one. Only when IsComplete is true can you act on it, passing its ActivityId as -PreviewActivityId to the change."
start: { label: Preview started }
questions:
  - { id: blocked, label: "`IsBlocked`?", sub: on the start result }
  - { id: failed, label: "`HasFailed`?" }
  - { id: complete, label: "`IsComplete`?" }
outcomes:
  - { id: never, label: Never evaluated, sub: the blocking finding says why }
  - { id: subset, label: Do not trust the counts, sub: only some objects were evaluated }
  - { id: partial, label: Running or stopped, sub: "read it again, or start a new one" }
  - { id: act, label: Act on it, sub: "pass its `ActivityId` as `-PreviewActivityId`", good: true }
answers:
  - { from: blocked, to: never, label: "yes" }
  - { from: blocked, to: failed, label: "no" }
  - { from: failed, to: subset, label: "yes" }
  - { from: failed, to: complete, label: "no" }
  - { from: complete, to: partial, label: "no" }
  - { from: complete, to: act, label: "yes" }
```

Evaluation is asynchronous: `New-JIMConfigurationChangePreview` returns as soon as the proposal itself has been validated, with an Activity id to poll, or waits for the whole answer with `-Wait`. The concepts behind previews (what the stages mean, when a result should not be trusted, and how a preview is recorded against the change it informed) are in [Configuration changes](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it).

!!! warning "Read `HasFailed` before you read the counts"

    A preview that failed part-way has evaluated an arbitrary subset of the objects, so its counts are real numbers over the wrong population. Check `HasFailed` and `IsComplete` before acting on anything else a preview returns.

---

## :material-eye-plus-outline: New-JIMConfigurationChangePreview

Starts a preview of a proposed configuration change.

<!-- BEGIN GENERATED: New-JIMConfigurationChangePreview -->
### Syntax

```powershell
# MetaverseObjectTypeDeletionSettings (default)
New-JIMConfigurationChangePreview -MetaverseObjectTypeId <int> [-DeletionRule <string>] [-DeletionGracePeriod <timespan>] [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# ConnectedSystemFullSynchronisation
New-JIMConfigurationChangePreview -ConnectedSystemId <int> -FullSynchronisation [-MaxObjects <int>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# ConnectedSystemDeletion
New-JIMConfigurationChangePreview -ConnectedSystemId <int> -Deletion [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# ConnectedSystemSchema
New-JIMConfigurationChangePreview -ConnectedSystemId <int> -SchemaObjectType <hashtable[]> [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# ObjectMatching
New-JIMConfigurationChangePreview -ConnectedSystemId <int> -MatchingRule <hashtable[]> [-ObjectMatchingRuleMode <string>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# ConnectedSystemScopeSelection
New-JIMConfigurationChangePreview -ConnectedSystemId <int> [-SelectedPartitionIds <int[]>] [-SelectedContainerIds <int[]>] [-ExcludedContainerIds <int[]>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# SyncRuleBehaviour
New-JIMConfigurationChangePreview -SyncRuleId <int> -RuleState <string> [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>] [-EnforceState <bool>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# SyncRuleAttributeFlow
New-JIMConfigurationChangePreview -SyncRuleId <int> -AttributeFlowMapping <hashtable[]> [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# SyncRuleScopingCriteria
New-JIMConfigurationChangePreview -SyncRuleId <int> -ScopingCriteriaGroup <hashtable[]> [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

# SyncRuleDestructiveToggles
New-JIMConfigurationChangePreview -SyncRuleId <int> [-OutboundDeprovisionAction <string>] [-InboundOutOfScopeAction <string>] [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseObjectTypeId` | `int` | Yes (MetaverseObjectTypeDeletionSettings) |  | The Metaverse Object Type whose deletion settings are being proposed. Selects the deletion settings surface. Accepts pipeline input by property name. |
| `DeletionRule` | `string` | No (MetaverseObjectTypeDeletionSettings) |  | The proposed deletion rule: Manual (objects are never deleted automatically), WhenLastConnectorDisconnected (deleted once every connector is removed) or WhenAuthoritativeSourceDisconnected (deleted when an authoritative source disconnects). Omitted previews the stored rule. One of `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `timespan` | No (MetaverseObjectTypeDeletionSettings) |  | The proposed grace period, as a TimeSpan. Omitted previews the stored grace period; [TimeSpan]::Zero previews no grace period, matching how `Set-JIMMetaverseObjectType` stores it. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No (MetaverseObjectTypeDeletionSettings) |  | The proposed authoritative sources. Omitted previews the stored list. JIM reads this list when a Connected System Object disconnects, not during housekeeping of objects already marked, so changing it alone moves no object's deletion date and the preview reports no impact from it. It can still make the proposal invalid, which comes back as a blocking finding. |
| `DeletionTriggerMode` | `string` | No (MetaverseObjectTypeDeletionSettings) |  | The proposed trigger mode. Omitted previews the stored mode. Read at the same moment as the trigger list, and so with the same standing impact: none. One of `AllSourcesDisconnect` or `SpecificSourcesDisconnect`. |
| `ConnectedSystemId` | `int` | Yes (ConnectedSystemFullSynchronisation, ConnectedSystemDeletion, ConnectedSystemSchema, ObjectMatching, ConnectedSystemScopeSelection) |  | The Connected System whose partition and container selection is being proposed. Selects the scope selection surface, unless `-MatchingRule`, `-SchemaObjectType`, `-Deletion` or `-FullSynchronisation` selects another. Accepts pipeline input by property name. |
| `Deletion` | `switch` | Yes (ConnectedSystemDeletion) |  | Previews deleting the Connected System named by `-ConnectedSystemId`, rather than changing it. |
| `FullSynchronisation` | `switch` | Yes (ConnectedSystemFullSynchronisation) |  | Previews running a Full Synchronisation of the Connected System named by `-ConnectedSystemId`. The system needs a Full Synchronisation Run Profile; without one the proposal comes back blocked. |
| `MaxObjects` | `int` | No (ConnectedSystemFullSynchronisation) |  | Evaluate only this many objects, in the order the synchronisation would meet them, rather than every object. Omitted, every object is evaluated, which is the only way the counts describe the whole system; a capped preview carries a warning saying its counts describe only the objects it evaluated. Useful for a quick look at a very large system. At least 1. |
| `SelectedPartitionIds` | `int[]` | No (ConnectedSystemScopeSelection) |  | The partitions that would be managed. Omitted previews the partitions currently selected, so a proposal changing only containers need not restate them. |
| `SelectedContainerIds` | `int[]` | No (ConnectedSystemScopeSelection) |  | The containers that would be managed. Omitted previews the containers currently selected. Selecting a container selects its whole subtree, so a descendant does not need listing to be in scope; pass the containers that would carry a tick. Read the current selection with `Get-JIMConnectedSystemPartition`. |
| `ExcludedContainerIds` | `int[]` | No (ConnectedSystemScopeSelection) |  | The containers that would be carved out of the selection around them. Omitted previews the exclusions currently in force, so a proposal changing only the selection need not restate them; an empty array previews lifting every exclusion, which brings those branches back into scope. A container states one or the other, so an id passed here must not also appear in `-SelectedContainerIds`; a proposal naming both is refused rather than resolved. |
| `SyncRuleId` | `int` | Yes (SyncRuleBehaviour, SyncRuleAttributeFlow, SyncRuleScopingCriteria, SyncRuleDestructiveToggles) |  | The Synchronisation Rule whose destructive toggles are being proposed. Selects the destructive toggles surface. Accepts pipeline input by property name. |
| `OutboundDeprovisionAction` | `string` | No (SyncRuleDestructiveToggles) |  | The proposed action for a joined target object whose Metaverse Object leaves this export rule's scope: Disconnect leaves the object in the target Connected System; Delete stages a Delete export that removes it. Omitted previews the stored action. Read only by export Synchronisation Rules, and the preview says so when the rule is an import rule. One of `Disconnect` or `Delete`. |
| `InboundOutOfScopeAction` | `string` | No (SyncRuleDestructiveToggles) |  | The proposed action for a joined Connected System Object that leaves this import rule's scope or is obsoleted: RemainJoined keeps the join ("once managed, always managed"); Disconnect breaks it, recalls what the object contributed and can trigger the Metaverse Object's deletion rules. Omitted previews the stored action. Read only by import Synchronisation Rules. One of `RemainJoined` or `Disconnect`. |
| `ScopingCriteriaGroup` | `hashtable[]` | Yes (SyncRuleScopingCriteria) |  | The proposed Scoping Criteria, as one hashtable per top-level criteria group. Groups are combined with OR, exactly as a synchronisation combines them. Each group takes a Type of 'All' or 'Any', a Criteria array, and an optional ChildGroups array of further groups nested inside it. Each criterion names one attribute by id (ConnectedSystemAttributeId on an import rule, MetaverseAttributeId on an export rule), a ComparisonType, and the value to compare against in the field matching the attribute's data type (StringValue, IntValue, LongValue, DecimalValue, DateTimeValue, BoolValue or GuidValue). To compare a computed value instead, a criterion gives an Expression and the type to compare it as in CompareAs, and no attribute id. Mandatory, and an empty array is a valid and deliberate value: it proposes removing every criterion, which hands the rule every object of its type. That is the widest change the Scope tab can make, so it has to be asked for rather than arrived at by omission. |
| `AttributeFlowMapping` | `hashtable[]` | Yes (SyncRuleAttributeFlow) |  | The proposed Attribute Flow, as one hashtable per mapping. Each mapping names the attribute it writes (TargetMetaverseAttributeId on an import rule, TargetConnectedSystemAttributeId on an export rule) and a Sources array; a source takes an Order, and either an attribute id (ConnectedSystemAttributeId on an import rule, MetaverseAttributeId on an export rule) or an Expression with an optional MissingInputBehaviour. A mapping may also carry Priority, NullIsValue, InitialExportOnly, InboundValueProcessing and CaseNormalisation, which default to the same values the editor uses. Mandatory, and an empty array is a valid and deliberate value: it proposes removing every mapping, so the rule flows nothing. Priority is worth passing deliberately on an import mapping. It defaults to the lowest, so a mapping proposed for an attribute another rule already contributes to would be evaluated and then write nothing; the preview reports that as a validation finding rather than as values that would never be written. |
| `MatchingRule` | `hashtable[]` | Yes (ObjectMatching) |  | The proposed Object Matching Rules, as one hashtable per rule, across both matching modes. Each rule takes an Order (rules are tried in ascending order until one matches), its owner (ConnectedSystemObjectTypeId in Simple mode, SyncRuleId in Advanced mode), a MetaverseObjectTypeId (required in Simple mode), a TargetMetaverseAttributeId, and a Sources array whose entries take an Order and either a ConnectedSystemAttributeId or an Expression over the Connected System Object (cs["attributeName"]), judged as saving the rule would judge it. CaseSensitive is optional: omitted, it follows the sources exactly as a rule created without it does, respecting case only when a source attribute's values are declared case-exact. Mandatory, and an empty array is a valid and deliberate value: it proposes removing every rule, so nothing would join and every unjoined object would project a new identity instead. |
| `ObjectMatchingRuleMode` | `string` | No (ObjectMatching) |  | The matching mode the rules would use: ConnectedSystem (Simple) or SyncRule (Advanced). Omitted previews the stored mode; pass it to preview the switch between them. One of `ConnectedSystem` or `SyncRule`. |
| `RuleState` | `string` | Yes (SyncRuleBehaviour) |  | Whether the rule would run at all, which selects the behaviour toggles surface. Mandatory because the toggle it sets is a boolean, so a caller who left it out could not be told apart from one proposing to switch the rule off. One of `Enabled` or `Disabled`. |
| `ProjectToMetaverse` | `bool` | No (SyncRuleBehaviour) |  | Whether the rule would create Metaverse Objects for the in-scope objects that match none. Omitted previews the stored value. |
| `ProvisionToConnectedSystem` | `bool` | No (SyncRuleBehaviour) |  | Whether the rule would create objects in the target Connected System for the in-scope Metaverse Objects that have none. Omitted previews the stored value. |
| `EnforceState` | `bool` | No (SyncRuleBehaviour) |  | Whether the rule would go on correcting drift in the target Connected System. Omitted previews the stored value. |
| `SchemaObjectType` | `hashtable[]` | Yes (ConnectedSystemSchema) |  | The proposed schema selection, as one hashtable per Connected System Object Type being changed. Selects the schema selection surface. Each hashtable needs objectTypeId, and then only the keys being changed: selected (bool), removeContributedAttributesOnObsoletion (bool), and selectedAttributeIds (int array). Every omission means "leave this as it stands", at both levels: an Object Type not named here is left alone, and a key not set keeps that Type's stored value. Send the whole attribute set for a Type rather than the attributes that changed; External IDs are selected implicitly and need not be listed. |
| `FullDataSet` | `switch` | No |  | Keep every object-level detail row rather than the per-group cap's worth. Summary counts are exact either way; this decides only how much of the detail behind them can be read back with `Get-JIMConfigurationChangePreviewDelta`. Use it when you intend to export the full list. |
| `Wait` | `switch` | No |  | Polls until the preview finishes, and returns the finished preview rather than the start result. |
| `TimeoutSeconds` | `int` | No | `300` | How long `-Wait` polls before giving up and writing an error. The preview itself keeps running; read it later with `Get-JIMConfigurationChangePreview`. Defaults to 300 seconds, or to twice the preview's EstimatedDuration where that is longer, so a Full Synchronisation preview of a large system is not abandoned while it is working as expected. A value you pass is always honoured. From 1 to 86,400. |

### Output

Without `-Wait`, the result of starting the preview: its Activity id, and what validation found. With `-Wait`, the finished preview, as `Get-JIMConfigurationChangePreview` returns it; or the start result, at once, where the proposal was blocked or validation failed, since nothing further will run.

**Without `-Wait`, or where the proposal was blocked or validation failed**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The preview's Activity, which is also the preview's identifier. Read the preview back from `GET /api/v1/previews/{activityId}` and its drill-down rows from `GET /api/v1/previews/{activityId}/deltas`. |
| `ValidationFindings` | `object[]` | What stage 1 found about the proposal itself, in the order the adapter reported it. Each has `Severity`, `Message`, `PropertyName` and `MetaverseAttributeName`. |
| `IsBlocked` | `bool` | True when the proposal cannot be applied. The remaining stages are recorded as not applicable rather than run; read the findings instead of waiting for results that will never arrive. |
| `Failed` | `bool` | True when stage 1 itself errored, as opposed to finding something wrong with the proposal. The Activity carries the error; nothing further ran. |
| `EstimatedAffectedObjects` | `int` | How many objects the change is expected to touch. Empty when the proposal was blocked or stage 1 failed, in which case no estimate was taken. |
| `EstimatedDeltaRows` | `long` | How many drill-down rows the change is expected to produce. Empty when the proposal was blocked or stage 1 failed, in which case no estimate was taken. |
| `EstimatedDuration` | `string` | How long the preview is expected to take to evaluate, where the surface can say. A Full Synchronisation preview estimates it from the number of objects and how fast this system last synchronised, never faster than a conservative reference rate; other surfaces leave it empty. Use it to decide how long to wait for the result. |

**With `-Wait`, once the preview finishes**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The preview's Activity, which is also its identifier. |
| `Surface` | `string` | The configuration surface previewed. One of `NotSet`, `SynchronisationRule`, `ConnectedSystem`, `MetaverseObjectType`, `MetaverseAttribute`, `SynchronisationRuleScope`, `SynchronisationRuleAttributeFlow`, `ObjectMatching`, `SynchronisationRuleBehaviour`, `ConnectedSystemSchema`, `ConnectedSystemDeletion` or `ConnectedSystemFullSynchronisation`. |
| `ActivityStatus` | `string` | The Activity's own status. Read this before the stage statuses: a preview that failed, or was cancelled, says so here first. One of `NotSet`, `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError` or `Cancelled`. |
| `Message` | `string` | What the preview is currently doing, or what it finished doing. |
| `ErrorMessage` | `string` | Why the preview failed, when it did. Never contains attribute values. |
| `ObjectsProcessed` | `int` | How many drill-down rows the preview has evaluated so far; its progress. |
| `ObjectsToProcess` | `int` | How many drill-down rows the preview expects to evaluate. |
| `ValidationStatus` | `string` | How far stage 1, validating the proposal itself, has got. One of `NotStarted`, `InProgress`, `Complete`, `Failed`, `NotApplicable` or `Cancelled`. |
| `ImpactCountsStatus` | `string` | How far stage 2, counting the objects each transition would affect, has got. One of `NotStarted`, `InProgress`, `Complete`, `Failed`, `NotApplicable` or `Cancelled`. |
| `SummaryStatus` | `string` | How far stage 3, grouping the affected objects into the summary groups, has got. One of `NotStarted`, `InProgress`, `Complete`, `Failed`, `NotApplicable` or `Cancelled`. |
| `DeltasStatus` | `string` | How far stage 4, keeping the drill-down rows, has got. `NotApplicable` for a preview that only counts. One of `NotStarted`, `InProgress`, `Complete`, `Failed`, `NotApplicable` or `Cancelled`. |
| `IsComplete` | `bool` | True when every stage that was going to run has finished successfully. |
| `HasFailed` | `bool` | True when any stage failed. Nothing in this response should be read as authoritative when it is: a preview that failed part-way has seen an arbitrary subset of the population. |
| `ValidationFindings` | `object[]` | What stage 1 found about the proposal itself. Each has `Severity`, `Message`, `PropertyName` and `MetaverseAttributeName`. |
| `ImpactCounts` | `object[]` | Stage 2's counts per transition, taken over every object the change could affect. Separate from the summary groups, which come from evaluating each object; a preview may report one and not the other. Each has `TransitionType`, `ObjectCount`, `ConnectedSystemId` and `MetaverseObjectTypeId`. |
| `Groups` | `object[]` | The summary groups, largest first. Counts here are exact even where drill-down rows were capped. Each has `Id`, `TransitionType`, `MetaverseObjectTypeId`, `MetaverseObjectTypeName`, `ConnectedSystemId`, `ConnectedSystemName`, `AttributeName`, `OldValue`, `NewValue`, `PatternKey`, `ObjectCount` and `DeltasSampled`. |
| `EstimatedAffectedObjects` | `int` | How many objects the change was expected to touch, estimated before evaluation began. |
| `EstimatedDeltaRows` | `long` | How many drill-down rows the change was expected to produce, estimated before evaluation began. |
| `DeltaPersistence` | `string` | Whether drill-down rows were kept in full or capped per group. Group counts are exact either way; this says only how much can be drilled into. One of `Full` or `Capped`. |
| `DispatchedToWorker` | `bool` | Whether the evaluation ran on the background worker service. Informational only; the result is the same either way. |
| `StalenessBaseline` | `datetime` | The most recent import or synchronisation across the systems the preview depends on, sampled when it ran. A later import moves the real answer, which is how a caller knows to treat a result as stale. |
<!-- END GENERATED: New-JIMConfigurationChangePreview -->

Which identifier you pass selects the entity, and the parameters beside it select the surface. `-MetaverseObjectTypeId` previews that type's deletion settings. `-ConnectedSystemId` previews that system's partition and container selection, or, with `-MatchingRule`, its Object Matching Rules, or, with `-SchemaObjectType`, its schema selection, or, with `-Deletion`, deleting it altogether, or, with `-FullSynchronisation`, running a Full Synchronisation of it. `-SyncRuleId` previews a Synchronisation Rule's destructive toggles, or, with `-ScopingCriteriaGroup`, its Scoping Criteria, or, with `-AttributeFlowMapping`, its Attribute Flow, or, with `-RuleState`, its behaviour toggles.

An omitted setting is treated exactly as [`Set-JIMMetaverseObjectType`](metaverse-object-types.md#set-jimmetaverseobjecttype) and [`Set-JIMSyncRule`](synchronisation-rules.md#set-jimsyncrule) treat it, so passing the same parameters to the preview and to the change describes precisely what the change will do.

Anything you leave out of `-SchemaObjectType` stays as it is. Leaving out `selected` does not deselect the Object Type, so a request that changes one attribute never proposes taking the whole Object Type out of management. Deselecting an External ID is refused with a blocking finding.

`-Deletion` previews what [`Remove-JIMConnectedSystem`](connected-systems.md#remove-jimconnectedsystem) does by default: deprovisioning the system through synchronisation. It reports which Metaverse attribute values another Connected System would take over (`WouldTakeOverContributedValue`) and which would be cleared because nothing else contributes them (`NoContributor`), which Metaverse Objects would become eligible for deletion (`WouldBecomeDeletionEligible`), which updates and deletions would be staged for the other Connected Systems (`WouldStageUpdateExport`, `WouldStageDeleteExport`), and which changes already queued for them would be withdrawn because the account already holds the value (`PendingExportChangesWithdrawn`, with the queued value as `OldValue` and the value the account keeps as `NewValue`). There is nothing to propose beyond the system itself. Deleting with `-DeleteImmediately` skips all of that work, keeping the system's values without provenance, so this preview does not describe it. Pass the finished preview's `ActivityId` to `Remove-JIMConnectedSystem -PreviewActivityId` and the deletion's Activity records that it was looked at first.

`-FullSynchronisation` previews running a Full Synchronisation of the system, which is how configuration changes reach your data. It reports every change the run would make to Metaverse Objects (`Projected`, `Joined`, `AttributeFlow`, `WouldBecomeDeletionEligible`), every update, provisioning and deletion it would stage for the other Connected Systems (`WouldStageUpdateExport`, `Provisioned`, `WouldStageDeleteExport`), the changes already queued for them that it would withdraw because the target already holds the value (`PendingExportChangesWithdrawn`), the drift it would correct where an export rule enforces state (`DriftCorrection`), the objects it would fail, and how many objects would not change (`WouldNotChange`). `ImpactCounts` counts distinct objects per consequence; `Groups` breaks them down by Connected System, attribute and value change, as the portal's **What would change** list does. Every object is evaluated unless `-MaxObjects` caps it, so a large system takes a while: the start result's `EstimatedDuration` says roughly how long. Pass the finished preview's `ActivityId` to [`Start-JIMRunProfile -PreviewActivityId`](run-profiles.md#start-jimrunprofile) and the run's Activity records that it was informed by the preview; JIM refuses the run, queuing nothing, when the id is not a completed Full Synchronisation preview of that same system.

Pass the whole container selection rather than one flag, because what a deselection costs depends on the rest of it: an object leaves import scope only when nothing else still covers it. An **empty** list is a real proposal and is sent as one, so `-SelectedContainerIds @()` previews deselecting every container.

A proposal carrying a blocking validation finding is never evaluated: `IsBlocked` is `$true`, the findings say why, and `-Wait` returns immediately rather than polling for results that will not arrive.

Each validation finding is also written as it arrives: Blocking and Warning findings with `Write-Warning`, Information findings with `Write-Verbose`. A script that runs with `-WarningAction Stop`, or treats any warning as a failure, therefore stops on a Warning finding too, such as the one a schema preview raises when [selecting or deselecting an overlapping Object Type](../configuration/connected-system-schema.md#when-an-object-changes-object-type) would change the Object Type of objects already imported. Read `ValidationFindings` to tell the severities apart.

### Examples

```powershell title="Preview switching a type to automatic deletion"
New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
```

```powershell title="Read how many objects each transition would affect"
$preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionGracePeriod ([TimeSpan]::FromDays(7)) -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
```

```powershell title="Apply only when the preview finds nothing would happen"
$preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
if (-not $preview.HasFailed -and $preview.ImpactCounts.Count -eq 0) {
    Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -PreviewActivityId $preview.ActivityId
}
```

```powershell title="Preview deselecting a container"
$current = Get-JIMConnectedSystemPartition -ConnectedSystemId 2
$keep = $current.containers | Where-Object { $_.selected -and $_.name -ne 'Contractors' }
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedContainerIds $keep.id -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
```

```powershell title="Preview excluding a container"
$current = Get-JIMConnectedSystemPartition -ConnectedSystemId 2
$carveOut = $current.containers | Where-Object name -eq 'Service Accounts'
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 2 -ExcludedContainerIds $carveOut.id -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
```

```powershell title="Check what a narrowed scope would put on course for deletion"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedPartitionIds 5 -Wait
$preview.ImpactCounts | Where-Object TransitionType -eq 'WouldBecomeDeletionEligible'
```

```powershell title="Preview a Full Synchronisation and see what it would do to each system"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -FullSynchronisation -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
$preview.Groups | Format-Table TransitionType, ConnectedSystemName, AttributeName, OldValue, NewValue, ObjectCount
```

```powershell title="Run a Full Synchronisation only when nothing would be deleted"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -FullSynchronisation -Wait
$destructive = $preview.ImpactCounts |
    Where-Object TransitionType -in 'WouldBecomeDeletionEligible', 'WouldStageDeleteExport'
if ($preview.IsComplete -and -not $destructive) {
    Start-JIMRunProfile -ConnectedSystemId 3 -RunProfileName 'Full Synchronisation' -PreviewActivityId $preview.ActivityId -Wait
}
```

```powershell title="Preview flipping an export rule's Deprovisioning Action to Delete"
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -OutboundDeprovisionAction Delete -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
```

```powershell
$group = @{
    Type = 'All'
    Criteria = @(
        @{ ConnectedSystemAttributeId = 101; ComparisonType = 'Equals'; StringValue = 'Sales' }
    )
}
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -ScopingCriteriaGroup $group -Wait
$preview.ImpactCounts
```

Reports what narrowing an import Synchronisation Rule to the Sales department would do: how many joined objects would leave scope and disconnect from their Metaverse Objects, how many unjoined ones stop matching, and how many objects would newly enter scope and be projected.

```powershell
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -ScopingCriteriaGroup @() -Wait
$preview.ImpactCounts | Where-Object transitionType -eq 'Projected'
```

Reports what removing every Scoping Criterion would do, which puts every object of the rule's type in scope. The `Projected` count is how many Metaverse Objects that would create.

```powershell
$mapping = @{
    TargetMetaverseAttributeId = 201
    Priority = 1
    Sources = @(
        @{ Order = 1; Expression = 'cs["givenName"] + "." + cs["sn"] + "@corp.local"'; MissingInputBehaviour = 'FailMapping' }
    )
}
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -AttributeFlowMapping $mapping -Wait
$preview.ImpactCounts
```

Reports what an email cutover would write: how many identities' addresses change, and how many objects the Expression could not be evaluated for at all because a required input is missing.

```powershell
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -AttributeFlowMapping $mapping -FullDataSet -Wait
Get-JIMConfigurationChangePreviewDelta -ActivityId $preview.ActivityId |
    Where-Object transitionType -eq 'WouldFailAttributeFlow'
```

Keeps every detail row and lists the objects the proposed Expression would not evaluate for, which is the handful the cutover would otherwise leave without an address.

```powershell title="Apply a tightened Out-of-Scope Action only when nothing disconnects today"
$preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -InboundOutOfScopeAction Disconnect -Wait
if (-not $preview.HasFailed -and ($preview.ImpactCounts | Measure-Object ObjectCount -Sum).Sum -eq 0) {
    Set-JIMSyncRule -Id 42 -InboundOutOfScopeAction Disconnect -PreviewActivityId $preview.ActivityId
}
```

```powershell title="Preview deleting a Connected System"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
```

```powershell title="List the Metaverse attribute values a deletion would clear"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -FullDataSet -Wait
Get-JIMConfigurationChangePreviewDelta -ActivityId $preview.ActivityId -All |
    Where-Object TransitionType -eq 'NoContributor'
```

Keeps every detail row and lists each value that would be left empty because no other Connected System contributes it, which is the list to check before the deletion goes ahead.

```powershell title="Delete a Connected System after previewing it, recording the preview"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -Wait
if ($preview.IsComplete) {
    Remove-JIMConnectedSystem -Id 3 -PreviewActivityId $preview.ActivityId
}
```

Deprovisions the Connected System (prompting for confirmation) only once its deletion preview has finished, and records the preview on the deletion's Activity.

---

## :material-eye-outline: Get-JIMConfigurationChangePreview

Reads a preview.

<!-- BEGIN GENERATED: Get-JIMConfigurationChangePreview -->
### Syntax

```powershell
Get-JIMConfigurationChangePreview -ActivityId <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes |  | The preview's Activity id, as returned by `New-JIMConfigurationChangePreview`. Accepts pipeline input by property name. |

### Output

The preview, with each stage's results as far as it has got; it is safe to read while the preview runs. IsComplete means every stage that was going to run has finished: an empty summary means nothing would change only once it is `$true`.

Properties as under [New-JIMConfigurationChangePreview](#new-jimconfigurationchangepreview).
<!-- END GENERATED: Get-JIMConfigurationChangePreview -->

### Examples

```powershell title="Read a preview by its Activity id"
Get-JIMConfigurationChangePreview -ActivityId "019fc824-f8c6-7588-8d9a-24a295e7621d"
```

```powershell title="Show the summary groups behind the counts"
$preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
$preview.Groups | Format-Table TransitionType, MetaverseObjectTypeName, AttributeName, PatternKey, ObjectCount, DeltasSampled
```

Each group carries a `PatternKey` naming what kind of edit it describes, or nothing where JIM recognised none, or where the group's objects did not all make the same kind of edit. The values are stable identifiers you can match on:

| `PatternKey` | Means |
|---|---|
| `EmailDomainChanged` | An address or User Principal Name keeps its local part and moves to a different domain. |
| `ContainerChanged` | A distinguished name keeps its leaf name and moves to a different parent path. |
| `CasingChanged` | The value is the same text in a different case. |
| `PrefixAdded`, `PrefixRemoved` | Text was added to, or removed from, the start of the value. |
| `SuffixAdded`, `SuffixRemoved` | Text was added to, or removed from, the end of the value. |

```powershell title="Check that a domain cutover only changes domains"
$preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
$unexpected = $preview.Groups | Where-Object { $_.PatternKey -ne 'EmailDomainChanged' }
if ($unexpected) { $unexpected | Format-Table AttributeName, OldValue, NewValue, PatternKey, ObjectCount }
```

---

## :material-compare: Get-JIMConfigurationChangePreviewDelta

Reads the object-level detail behind a preview: which objects would be affected, and the values on either side of the change.

<!-- BEGIN GENERATED: Get-JIMConfigurationChangePreviewDelta -->
### Syntax

```powershell
# Page (default)
Get-JIMConfigurationChangePreviewDelta -ActivityId <guid> [-GroupId <guid>] [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConfigurationChangePreviewDelta -ActivityId <guid> -All [-GroupId <guid>] [-Search <string>] [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes |  | The preview's Activity id. Accepts pipeline input by property name. |
| `GroupId` | `guid` | No |  | Restrict the rows to one summary group, as listed in the preview's Groups collection. Omitted returns rows across every group. |
| `Search` | `string` | No |  | Restrict the rows to those matching this text across the columns the detail carries. |
| `Page` | `int` | No (Page) | `1` | The page of rows to return. At least 1. |
| `PageSize` | `int` | No | `50` | How many rows per page. From 1 to 100. |
| `All` | `switch` | Yes (All) |  | Fetch every page. Prefer `-GroupId` or `-Search` first: a full preview can carry a great many rows. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page however many there are. |

### Output

Each row: what the proposed change would do to one object. PatternKey takes the same values as on a summary group (see `Get-JIMConfigurationChangePreview`). MatchCandidates is empty unless the row is WouldMatchAmbiguously.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The row's unique identifier. |
| `GroupId` | `guid` | The summary group this delta was counted in. |
| `TransitionType` | `string` | What would happen to this object. One of `CsoAdded`, `CsoUpdated`, `CsoDeleted`, `DeletionDetected`, `ExportConfirmed`, `ExportFailed`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `MvoDeleted`, `DriftCorrection`, `Provisioned`, `PendingExportCreated`, `Exported`, `Deprovisioned`, `MvoDeletionScheduled`, `AssertedNull`, `NoContributor`, `WouldFallInScope`, `WouldFallOutOfScope`, `WouldBecomeDeletionEligible`, `WouldCeaseToBeDeletionEligible`, `WouldChangeDeletionEligibleDate`, `DeprovisionQueued`, `WouldDisconnectFromMetaverseObject`, `WouldStageDeleteExport`, `WouldRemainJoined`, `WouldChangeDeprovisionAction`, `WouldFailAttributeFlow`, `WouldJoinDifferentMetaverseObject`, `WouldJoinInsteadOfProject`, `WouldProjectInsteadOfJoin`, `WouldMatchAmbiguously`, `WouldStopProjecting`, `WouldStopProvisioning`, `WouldStopCorrectingDrift`, `WouldStopBeingImported`, `WouldResumeBeingImported`, `WouldWithdrawContributedValues`, `WouldRetainContributedValues`, `ValuesPreserved`, `MvoDeletionCancelled`, `WouldLeaveExportScope`, `WouldEnterExportScope`, `ProvisioningCancelled`, `GeneratedValueAssigned`, `GeneratedValueAdopted`, `GeneratedValueRetired`, `GeneratedValueRemediated`, `OutOfScopeRetainJoin`, `WouldTakeOverContributedValue`, `WouldTakeOverSameValue`, `WouldStageUpdateExport`, `TargetDisconnected`, `WouldNotChange`, `WouldFail`, `PendingExportChangesWithdrawn`, `GeneratedValueWaiting` or `GeneratedValueCandidatesSkipped`. |
| `MetaverseObjectId` | `guid` | The Metaverse Object concerned, where the transition names one: a deletion eligibility change does, and so does an Attribute Flow change. |
| `ConnectedSystemObjectId` | `guid` | The Connected System Object concerned, where the transition names one: a scope change does, and so does an Attribute Flow change. |
| `ConnectedSystemId` | `int` | The Connected System the Connected System Object belongs to, where there is one. |
| `ObjectDisplayName` | `string` | The object's display name as it was when the preview ran, so it still reads correctly for an object about to be deleted or disconnected. |
| `ObjectTypeName` | `string` | The object's type name as it was when the preview ran. |
| `AttributeName` | `string` | The attribute whose value would change, for an Attribute Flow change; empty otherwise. |
| `OldValue` | `string` | The attribute's value before the change, for an Attribute Flow change; empty where it has none. |
| `NewValue` | `string` | The attribute's value after the change, for an Attribute Flow change; empty where it would have none. |
| `PatternKey` | `string` | What kind of edit this row describes, taking the same values as a group's own key. Per row, so a group that covers a mixture of edits can still be read by the kind each object makes. Empty where nothing recognised it. |
| `MatchCandidates` | `object[]` | For a row predicting an ambiguous match, the objects it would find, in recorded order: at most ten, each carrying the full count in `candidateTotal`. Empty for every other row. The same shape as an Activity item's `matchCandidates`. Each has `CandidateKind`, `CandidateId`, `DisplayNameSnapshot`, `TypeNameSnapshot`, `ConnectedSystemId`, `ConnectedSystemNameSnapshot`, `MatchedValue`, `ObjectMatchingRuleId`, `ObjectMatchingRuleDescriptionSnapshot`, `Ordinal` and `CandidateTotal`. |
<!-- END GENERATED: Get-JIMConfigurationChangePreviewDelta -->

!!! warning "These rows may be a sample"

    Unless the preview was started with `-FullDataSet`, each summary group keeps at most a capped number of rows, while the group's own `ObjectCount` is exact. Check the group's `DeltasSampled` flag before treating a list of rows as the complete set of affected objects.

### Examples

```powershell title="Read the first page of object-level detail"
Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId
```

```powershell title="List the objects that would become eligible for deletion"
$preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
$group = $preview.Groups | Where-Object { $_.TransitionType -eq 'WouldBecomeDeletionEligible' }
Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId -GroupId $group.Id -All
```

```powershell title="List the objects a proposed rule would match ambiguously, with what each would find"
$preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
$group = $preview.Groups | Where-Object { $_.TransitionType -eq 'WouldMatchAmbiguously' }
Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId -GroupId $group.Id -All |
    Select-Object ObjectDisplayName, @{ n = 'Matches'; e = { $_.MatchCandidates.DisplayNameSnapshot -join ', ' } }
```

---

## :material-eye-off-outline: Stop-JIMConfigurationChangePreview

Stops a running preview.

<!-- BEGIN GENERATED: Stop-JIMConfigurationChangePreview -->
### Syntax

```powershell
Stop-JIMConfigurationChangePreview -ActivityId <guid> [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes |  | The preview to stop, by its Activity id. Accepts pipeline input by property name. |

### Output

None.
<!-- END GENERATED: Stop-JIMConfigurationChangePreview -->

Nothing is deleted: the preview and whatever it had recorded stay readable with its Activity marked cancelled, because an administrator who stopped a preview after seeing its first stage usually stopped it because of what that stage said. A cancelled preview covers only the objects it had reached, so run a new one for a complete answer.

Stopping a preview that has already finished is reported as an error rather than silently succeeding: there was nothing to stop, and the results are still there to read.

### Examples

```powershell title="Stop a running preview"
Stop-JIMConfigurationChangePreview -ActivityId "019fc824-f8c6-7588-8d9a-24a295e7621d"
```

---

## :material-book-open-variant-outline: See also

- [Configuration changes](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it): what previews are and how to read them
- [Sync Preview cmdlets](sync-preview.md): what synchronising one object would do now, from PowerShell
- [Metaverse](../configuration/metaverse.md#previewing-a-deletion-settings-change): what a deletion settings preview evaluates
- [Synchronisation Rules](../configuration/deprovisioning.md#previewing-a-destructive-toggle-change): what a destructive toggle preview evaluates
- [Metaverse cmdlets](metaverse-object-types.md#set-jimmetaverseobjecttype): applying a previewed change with `-PreviewActivityId`
- [Synchronisation Rule cmdlets](synchronisation-rules.md#set-jimsyncrule): applying a previewed toggle change with `-PreviewActivityId`
- [Activities](activities.md): the Activity a preview runs as, and the one the change it informed is recorded under
