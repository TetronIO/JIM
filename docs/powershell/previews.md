---
title: Configuration Change Previews
---

# Configuration Change Previews

Preview cmdlets answer what a proposed configuration change would do, without making it. JIM evaluates the change against the objects already in the metaverse and reports which of them would be affected, changing nothing.

Evaluation is asynchronous: `New-JIMConfigurationChangePreview` returns as soon as the proposal itself has been validated, with an Activity id to poll, or waits for the whole answer with `-Wait`. The concepts behind previews (what the stages mean, when a result should not be trusted, and how a preview is recorded against the change it informed) are in [Configuration changes](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it).

!!! warning "Read `HasFailed` before you read the counts"

    A preview that failed part-way has evaluated an arbitrary subset of the objects, so its counts are real numbers over the wrong population. Check `HasFailed` and `IsComplete` before acting on anything else a preview returns.

---

## New-JIMConfigurationChangePreview

Starts a preview of a proposed configuration change.

### Syntax

```powershell
New-JIMConfigurationChangePreview -MetaverseObjectTypeId <int>
    [-DeletionRule <string>] [-DeletionGracePeriod <TimeSpan>]
    [-DeletionTriggerConnectedSystemIds <int[]>] [-DeletionTriggerMode <string>]
    [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

New-JIMConfigurationChangePreview -ConnectedSystemId <int>
    [-SelectedPartitionIds <int[]>] [-SelectedContainerIds <int[]>]
    [-ExcludedContainerIds <int[]>]
    [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

New-JIMConfigurationChangePreview -SyncRuleId <int>
    [-OutboundDeprovisionAction <string>] [-InboundOutOfScopeAction <string>]
    [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

New-JIMConfigurationChangePreview -SyncRuleId <int> -ScopingCriteriaGroup <hashtable[]>
    [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]

New-JIMConfigurationChangePreview -SyncRuleId <int> -AttributeFlowMapping <hashtable[]>
    [-FullDataSet] [-Wait] [-TimeoutSeconds <int>]
```

Which identifier you pass selects the surface: `-MetaverseObjectTypeId` previews that type's deletion settings, `-ConnectedSystemId` previews that system's partition and container selection, and `-SyncRuleId` previews a Synchronisation Rule. A rule has three previewable surfaces and the parameters you pass choose between them: the destructive toggles, or, with `-ScopingCriteriaGroup`, its Scoping Criteria, or, with `-AttributeFlowMapping`, its Attribute Flow.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseObjectTypeId` | `int` | Yes | | The Metaverse Object Type whose deletion settings are being proposed. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int` | Yes | | The Connected System whose partition and container selection is being proposed. Accepts pipeline input by property name. |
| `SelectedPartitionIds` | `int[]` | No | stored selection | The partitions that would be managed. |
| `SelectedContainerIds` | `int[]` | No | stored selection | The containers that would be managed. Selecting a container selects its whole subtree, so a descendant does not need listing. |
| `ExcludedContainerIds` | `int[]` | No | stored exclusions | The containers that would be carved out of the selection around them. A container is selected or excluded, never both; naming one in both lists is refused. |
| `DeletionRule` | `string` | No | stored value | `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `TimeSpan` | No | stored value | The proposed grace period. `[TimeSpan]::Zero` previews no grace period. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No | stored value | The proposed authoritative sources. |
| `DeletionTriggerMode` | `string` | No | stored value | `AllSourcesDisconnect` or `SpecificSourcesDisconnect`. |
| `SyncRuleId` | `int` | Yes | | The Synchronisation Rule whose destructive toggles are being proposed. Accepts pipeline input by property name. |
| `OutboundDeprovisionAction` | `string` | No | stored value | `Disconnect` or `Delete`: what happens to a joined target object when its Metaverse Object leaves this export rule's scope. |
| `InboundOutOfScopeAction` | `string` | No | stored value | `RemainJoined` or `Disconnect`: what happens to a joined Connected System Object that leaves this import rule's scope or is obsoleted. |
| `ScopingCriteriaGroup` | `hashtable[]` | Yes | | The proposed Scoping Criteria, one hashtable per top-level group. Groups are combined with OR. Each takes a `Type` of `All` or `Any`, a `Criteria` array, and an optional `ChildGroups` array of further groups. Each criterion names one attribute by id (`ConnectedSystemAttributeId` on an import rule, `MetaverseAttributeId` on an export rule), a `ComparisonType`, and the value in the field matching the attribute's data type. |
| `AttributeFlowMapping` | `hashtable[]` | Yes | | The proposed Attribute Flow, one hashtable per mapping. Each names the attribute it writes (`TargetMetaverseAttributeId` on an import rule, `TargetConnectedSystemAttributeId` on an export rule) and a `Sources` array; a source takes an `Order` and either an attribute id (`ConnectedSystemAttributeId` on an import rule, `MetaverseAttributeId` on an export rule) or an `Expression` with an optional `MissingInputBehaviour`. A mapping may also carry `Priority`, `NullIsValue`, `InitialExportOnly`, `InboundValueProcessing` and `CaseNormalisation`. |
| `FullDataSet` | `switch` | No | off | Keep every object-level detail row rather than the per-group cap's worth. Summary counts are exact either way. |
| `Wait` | `switch` | No | off | Poll until the preview finishes and return the finished preview. |
| `TimeoutSeconds` | `int` | No | `300` | How long `-Wait` polls before giving up. The preview keeps running; read it later with `Get-JIMConfigurationChangePreview`. |

`MetaverseObjectTypeId`, `ConnectedSystemId` and `SyncRuleId` are mutually exclusive: each is mandatory in its own parameter set.

An omitted deletion setting previews the stored value, exactly as [`Set-JIMMetaverseObjectType`](metaverse.md#set-jimmetaverseobjecttype) treats an omitted parameter. Pass the same parameters to both and the preview describes precisely what the change will do. The destructive toggles work the same way against [`Set-JIMSyncRule`](synchronisation-rules.md#set-jimsyncrule): an omitted toggle previews the stored action.

`ScopingCriteriaGroup` is mandatory, and `@()` is a valid and deliberate value: it proposes removing every criterion, which puts every object of the rule's type in scope. That is the widest change the Scope tab can make, so the cmdlet requires it to be asked for rather than arrived at by omitting a parameter.

`AttributeFlowMapping` is mandatory for the same reason, and `@()` likewise proposes removing every mapping, so the rule flows nothing. Pass `Priority` deliberately on an import mapping: it defaults to the lowest, so a mapping proposed for an attribute another rule already contributes to would be evaluated and then write nothing, and the preview reports that as a validation finding rather than as values that would never be written.

An omitted selection list likewise previews the stored selection. Pass the whole selection rather than one flag, because what a deselection costs depends on the rest of it: an object leaves import scope only when nothing else still covers it. An **empty** list is a real proposal and is sent as one, so `-SelectedContainerIds @()` previews deselecting every container, and `-ExcludedContainerIds @()` previews lifting every exclusion, which brings those branches back into scope.

### Output

Without `-Wait`, returns a `PSCustomObject` with `ActivityId`, `ValidationFindings`, `IsBlocked`, `Failed`, `EstimatedAffectedObjects` and `EstimatedDeltaRows`. With `-Wait`, returns the finished preview, as described under `Get-JIMConfigurationChangePreview` below.

A proposal carrying a blocking validation finding is never evaluated: `IsBlocked` is `$true`, the findings say why, and `-Wait` returns immediately rather than polling for results that will not arrive.

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

Reports what narrowing an import Synchronisation Rule to the Sales department would do: how many joined objects would leave scope and disconnect from their Metaverse Objects, how many unjoined ones simply stop matching, and how many objects would newly enter scope and be projected.

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

---

## Get-JIMConfigurationChangePreview

Reads a preview.

### Syntax

```powershell
Get-JIMConfigurationChangePreview -ActivityId <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes | | The preview's Activity id, as returned by `New-JIMConfigurationChangePreview`. Accepts pipeline input by property name. |

Safe to call while the preview is still running: each stage's results appear as it completes.

### Output

Returns a `PSCustomObject` with `ActivityId`, `Surface`, `ActivityStatus`, `Message`, `ErrorMessage`, `ObjectsProcessed`, `ObjectsToProcess`, `ValidationStatus`, `ImpactCountsStatus`, `SummaryStatus`, `DeltasStatus`, `IsComplete`, `HasFailed`, `ValidationFindings`, `ImpactCounts`, `Groups`, `EstimatedAffectedObjects`, `EstimatedDeltaRows`, `DeltaPersistence`, `DispatchedToWorker` and `StalenessBaseline`.

`IsComplete` means every stage that was going to run has finished; an empty summary is only "nothing would change" once it is `$true`.

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

## Get-JIMConfigurationChangePreviewDelta

Reads the object-level detail behind a preview: which objects would be affected, and the values on either side of the change.

### Syntax

```powershell
# Page (default)
Get-JIMConfigurationChangePreviewDelta -ActivityId <guid> [-GroupId <guid>] [-Search <string>]
    [-Page <int>] [-PageSize <int>]

# All
Get-JIMConfigurationChangePreviewDelta -ActivityId <guid> -All [-GroupId <guid>] [-Search <string>]
    [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes | | The preview's Activity id. Accepts pipeline input by property name. |
| `GroupId` | `guid` | No | | Restrict the rows to one summary group, as listed in the preview's `Groups` collection. |
| `Search` | `string` | No | | Restrict the rows to those matching this text. |
| `Page` | `int` | No | `1` | Page number (Page set only). |
| `PageSize` | `int` | No | `50` | Rows per page (maximum 100). |
| `All` | `switch` | Yes (All set) | | Fetch every page. |
| `Force` | `switch` | No | off | Lift the client-side page cap that `-All` otherwise stops at. |

### Output

Returns one `PSCustomObject` per row, with `ObjectDisplayName`, `ObjectTypeName`, `AttributeName`, `OldValue`, `NewValue`, `TransitionType`, `PatternKey` and the identifiers of the objects concerned.

`PatternKey` takes the same values as on a group (see [`Get-JIMConfigurationChangePreview`](#get-jimconfigurationchangepreview) above). It is per row here, so a group covering a mixture of edits can still be sorted by the kind each object makes.

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

---

## Stop-JIMConfigurationChangePreview

Stops a running preview.

### Syntax

```powershell
Stop-JIMConfigurationChangePreview -ActivityId <guid> [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ActivityId` | `guid` | Yes | | The preview's Activity id. Accepts pipeline input by property name. |

Nothing is deleted: the preview and whatever it had recorded stay readable with its Activity marked cancelled, because an administrator who stopped a preview after seeing its first stage usually stopped it because of what that stage said. A cancelled preview covers only the objects it had reached, so run a new one for a complete answer.

Stopping a preview that has already finished is reported as an error rather than silently succeeding: there was nothing to stop, and the results are still there to read.

### Examples

```powershell title="Stop a running preview"
Stop-JIMConfigurationChangePreview -ActivityId "019fc824-f8c6-7588-8d9a-24a295e7621d"
```

---

## See also

- [Configuration changes](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it) -- what previews are and how to read them
- [Metaverse](../configuration/metaverse.md#previewing-a-deletion-settings-change) -- what a deletion settings preview evaluates
- [Synchronisation Rules](../configuration/synchronisation-rules.md#previewing-a-destructive-toggle-change) -- what a destructive toggle preview evaluates
- [Metaverse cmdlets](metaverse.md#set-jimmetaverseobjecttype) -- applying a previewed change with `-PreviewActivityId`
- [Synchronisation Rule cmdlets](synchronisation-rules.md#set-jimsyncrule) -- applying a previewed toggle change with `-PreviewActivityId`
- [Activities](activities.md) -- the Activity a preview runs as, and the one the change it informed is recorded under
