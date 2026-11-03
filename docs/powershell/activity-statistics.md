---
title: Activity Statistics
description: "The cmdlet that returns a Run Profile execution's statistics: the counts of objects it processed, changed and failed, broken down by what happened to them."
type: reference
audience: automation-author
---

# Activity Statistics

A Run Profile execution records its statistics on its Activity: how many objects it processed, how many it changed, and how many failed, broken down by what happened to them (added, joined, projected, exported and so on). `Get-JIMActivityStats` returns them for one Activity, so a script can check a run's outcome without reading every execution item. To find the Activity, see [Activities](activities.md); to find out what happened to one object, and why, see [Execution Items](execution-items.md).

```jim-diagram
archetype: anatomy
title: What Get-JIMActivityStats returns for a Run Profile execution
desc: "Get-JIMActivityStats returns one object for a Run Profile Activity. Its overall counts are TotalObjectsProcessed, TotalObjectChangeCount, TotalUnchanged and TotalObjectErrors. An import fills the Connector Space counts: TotalCsoAdds, TotalCsoUpdates, TotalCsoDeletes, and the entries an excluded Container discarded. A synchronisation fills the Metaverse counts: TotalProjections, TotalJoins, TotalAttributeFlows, TotalDisconnections, TotalProvisioned and the generated value counts. An export fills TotalExported and TotalDeprovisioned. The Pending Export counts say how many are staged, confirmed, retrying and failed. Counts a run type does not produce are zero."
subject:
  kind: object
  label: A run's statistics
  parts:
    - { label: Every run, detail: "processed, changed, unchanged, errors" }
    - { label: Import, detail: "Connector Space adds, updates, deletes" }
    - { label: Synchronisation, detail: "projections, joins, Attribute Flows" }
    - { label: Export, detail: "objects exported and deprovisioned" }
properties:
  - { name: Read from, value: "`Get-JIMActivityStats -Id`" }
  - { name: Pending Exports, value: "staged, confirmed, retrying, failed" }
```

---

## :material-chart-box-outline: Get-JIMActivityStats

Retrieves the statistics of a Run Profile Activity: the counts of objects it processed, changed and failed, and of what happened to them. This is useful for checking a run's outcome in a script, and for monitoring synchronisation health over time.

<!-- BEGIN GENERATED: Get-JIMActivityStats -->
### Syntax

```powershell
Get-JIMActivityStats -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The Run Profile Activity to get statistics for. Accepts pipeline input by property name, `Id` or `ActivityId`. |

### Output

The Run Profile execution's statistics.

| Property | Type | Description |
|----------|------|-------------|
| `TotalObjectsProcessed` | `int` | Total number of objects that were in scope for processing. |
| `TotalObjectChangeCount` | `int` | Total number of object changes (Run Profile Execution Items created). |
| `TotalUnchanged` | `int` | Number of objects that were unchanged. Calculated as TotalObjectsProcessed - TotalObjectChangeCount. |
| `TotalObjectErrors` | `int` | Number of objects with errors. |
| `TotalObjectTypes` | `int` | Number of distinct object types affected. |
| `TotalCsoAdds` | `int` | Number of Connected System Objects added to the Connector Space during import. |
| `TotalCsoUpdates` | `int` | Number of existing Connected System Objects updated during import. |
| `TotalCsoDeletes` | `int` | Number of Connected System Objects marked as deleted. |
| `TotalProjections` | `int` | Number of new Metaverse Objects created via projection. |
| `TotalJoins` | `int` | Number of Connected System Objects joined to existing Metaverse Objects. |
| `TotalAttributeFlows` | `int` | Number of Attribute Flows between Connected System Objects and Metaverse Objects. |
| `TotalDisconnections` | `int` | Number of Connected System Objects disconnected from Metaverse Objects. |
| `TotalDisconnectedOutOfScope` | `int` | Number of Connected System Objects disconnected from Metaverse Objects because they fell out of scope of import Synchronisation Rule scoping criteria. |
| `TotalOutOfScopeRetainJoin` | `int` | Number of Connected System Objects that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined). |
| `TotalDriftCorrections` | `int` | Number of Connected System Objects where drift was detected and corrective Pending Exports were created. |
| `TotalProvisioned` | `int` | Number of Connected System Objects provisioned to target Connected Systems during synchronisation. |
| `TotalGeneratedValues` | `int` | Number of values JIM generated for a target attribute during synchronisation. |
| `TotalGeneratedValueFailures` | `int` | Number of Run Profile Execution Items whose error type is a Unique Value Generation failure (exhausted, width exceeded, or an unresolved collision). |
| `TotalGeneratedValuesRemediated` | `int` | Number of generated values Collision Remediation corrected after a target rejected them as already in use. |
| `TotalGeneratedValuesNeedingDecision` | `int` | Number of rejected generated values that could not safely be corrected and wait on an administrator's decision. |
| `TotalGeneratedValuesWaiting` | `int` | Number of generated values held back because a value built from them needs another input the object has no value for yet. Not a failure: a later synchronisation generates the value once the input arrives. |
| `TotalExported` | `int` | Number of objects exported to target systems (includes both initial creation and subsequent updates). |
| `TotalDeprovisioned` | `int` | Number of objects deprovisioned from target systems. |
| `TotalPendingExports` | `int` | Number of Pending Exports staged for the next export run. These are exports that were previously executed but not yet confirmed, giving operators visibility into what changes will be made to Connected Systems. |
| `TotalPendingExportsConfirmed` | `int` | Number of Pending Exports that were fully confirmed and deleted. The exported attribute values matched the imported values. |
| `TotalPendingExportsRetrying` | `int` | Number of Pending Exports with unconfirmed attributes that will be retried. Some attribute values did not match; they will be re-exported on the next export run. |
| `TotalPendingExportsFailed` | `int` | Number of Pending Exports that failed after their last retry. JIM does not attempt one again; it clears once an import shows its values in place, for example after the change is made in the Connected System directly. |
| `TotalCreated` | `int` | Number of Metaverse Objects created directly (by example data generation or in the portal) rather than by projection. |
| `EntriesDiscardedByExcludedContainer` | `hashtable` | How many entries this import read from the Connected System and discarded because an excluded Container carved them out, keyed by that Container's id. |
| `TotalEntriesDiscardedByExclusion` | `int` | Entries discarded across every exclusion, which is the figure that answers "why was this import slow?". |
| `FollowUps` | `object[]` | Where the work the run set in motion stands now, counted by kind and state: the Initial Passwords and Exchange recipient changes its exports queued. Unlike the counts above, which are final once the run has finished, these change as the work is delivered. Empty for a run that set nothing in motion. Each has `Kind`, `State` and `Count`. |
<!-- END GENERATED: Get-JIMActivityStats -->

An import that discarded entries because a [Container Scope exclusion](../connectors/jim-ldap-connector-topology.md#excluding-a-container) carved them out counts them in `TotalEntriesDiscardedByExclusion` and `EntriesDiscardedByExcludedContainer`, the latter keyed by the excluded Container's ID because the Container can be renamed after the run. Resolve an ID to a name with [`Get-JIMConnectedSystemPartition`](partitions-and-containers.md#get-jimconnectedsystempartition).

### Examples

```powershell title="Get statistics by activity ID"
Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityStats
```

```powershell title="Get stats after a Run Profile completes"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
Get-JIMActivityStats -Id $result.ActivityId
```

```powershell title="Check for errors after execution"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
$stats = Get-JIMActivityStats -Id $result.ActivityId
if ($stats.TotalObjectErrors -gt 0) {
    Write-Warning "Sync completed with $($stats.TotalObjectErrors) errors"
    Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
        Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
        Get-JIMActivityExecutionItem |
        Select-Object DisplayNameSnapshot, ErrorType, ErrorMessage
}
```

---

## :material-book-open-variant-outline: See also

- [Activities](activities.md): cmdlets for listing and reading Activities, and the child Activities an operation spawned
- [Execution Items](execution-items.md): cmdlets for what happened to each object in a Run Profile execution, and what caused it
- [Activities](../configuration/activities.md): what Activities are, and the summary statistics the portal shows for a run
