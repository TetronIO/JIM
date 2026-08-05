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
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseObjectTypeId` | `int` | Yes | | The Metaverse Object Type whose deletion settings are being proposed. Accepts pipeline input by property name. |
| `DeletionRule` | `string` | No | stored value | `Manual`, `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`. |
| `DeletionGracePeriod` | `TimeSpan` | No | stored value | The proposed grace period. `[TimeSpan]::Zero` previews no grace period. |
| `DeletionTriggerConnectedSystemIds` | `int[]` | No | stored value | The proposed authoritative sources. |
| `DeletionTriggerMode` | `string` | No | stored value | `AllSourcesDisconnect` or `SpecificSourcesDisconnect`. |
| `FullDataSet` | `switch` | No | off | Keep every object-level detail row rather than the per-group cap's worth. Summary counts are exact either way. |
| `Wait` | `switch` | No | off | Poll until the preview finishes and return the finished preview. |
| `TimeoutSeconds` | `int` | No | `300` | How long `-Wait` polls before giving up. The preview keeps running; read it later with `Get-JIMConfigurationChangePreview`. |

An omitted deletion setting previews the stored value, exactly as [`Set-JIMMetaverseObjectType`](metaverse.md#set-jimmetaverseobjecttype) treats an omitted parameter. Pass the same parameters to both and the preview describes precisely what the change will do.

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
- [Metaverse cmdlets](metaverse.md#set-jimmetaverseobjecttype) -- applying a previewed change with `-PreviewActivityId`
- [Activities](activities.md) -- the Activity a preview runs as, and the one the change it informed is recorded under
