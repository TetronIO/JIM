---
title: Connected Systems
description: "The cmdlets that create, read, change, clear and delete Connected Systems."
type: reference
audience: automation-author
---

# Connected Systems

The Connected Systems cmdlets manage the full lifecycle of Connected Systems in JIM: creating, configuring, clearing and deleting them. The Connectors a Connected System is created from are on [Connectors](connectors.md), a server's TLS certificate on [Certificates](certificates.md), and which Connected Systems receive passwords on [Password Synchronisation](password-synchronisation.md). Importing a schema and choosing Object Types and attributes are on [Connected System Schema](connected-system-schema.md), auxiliary object classes on [Auxiliary Object Classes](connected-system-auxiliary-classes.md), partitions and containers on [Partitions and Containers](partitions-and-containers.md), the objects in the Connector Space on [Connected System Objects](connected-system-objects.md), and the changes waiting to be exported on [Pending Exports](pending-exports.md). Most cmdlets support pipeline input for scripting and automation workflows.

```jim-diagram
archetype: lifecycle
title: A Connected System's lifecycle, one cmdlet per step
desc: "New-JIMConnectedSystem creates a Connected System from a Connector. It starts configured but empty, with no Connected System Objects. An import Run Profile fills its Connector Space. Clear-JIMConnectedSystem empties the Connector Space again, as a queued background task, and keeps its settings, schema and Synchronisation Rules. In either state, Get-JIMConnectedSystem reads it and Set-JIMConnectedSystem changes its settings. Remove-JIMConnectedSystem deletes it from either state, with its objects, Synchronisation Rules and Run Profiles; check first what that removes with Get-JIMConnectedSystem -DeletionPreview."
start: { label: "`New-JIMConnectedSystem`" }
container: { label: Connected System, note: "`Get-JIMConnectedSystem` reads it and `Set-JIMConnectedSystem` changes it, in either state" }
states:
  - { id: empty, label: Configured, sub: no objects yet }
  - { id: holding, label: Holding objects, sub: Connector Space filled }
transitions:
  - { from: empty, to: holding, label: an import Run Profile }
  - { from: holding, to: empty, label: "`Clear-JIMConnectedSystem`", note: "keeps schema and Synchronisation Rules" }
end: { from: container, label: "`Remove-JIMConnectedSystem`", note: "check first with `-DeletionPreview`", state: { label: Deleted, sub: "objects, Synchronisation Rules, Run Profiles" } }
```

---

## :material-server-network-outline: Get-JIMConnectedSystem

Retrieves one or more Connected Systems, their object types, or a deletion impact preview.

<!-- BEGIN GENERATED: Get-JIMConnectedSystem -->
### Syntax

```powershell
# List (default)
Get-JIMConnectedSystem [-Name <string>]

# DeletionPreview
Get-JIMConnectedSystem -Id <int> -DeletionPreview

# ObjectTypes
Get-JIMConnectedSystem -Id <int> -ObjectTypes

# ById
Get-JIMConnectedSystem -Id <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (DeletionPreview, ObjectTypes, ById) |  | The ID of the Connected System to get. Accepts pipeline input by property name. |
| `Name` | `string` | No (List) |  | Returns only the Connected Systems whose name matches. Supports wildcards. |
| `ObjectTypes` | `switch` | Yes (ObjectTypes) |  | Returns the Connected System's Object Types, from its imported schema. |
| `DeletionPreview` | `switch` | Yes (DeletionPreview) |  | Returns what deleting the Connected System would affect. |

### Output

The Connected Systems, or with `-Id` the one Connected System, its Object Types or its deletion preview. A Connected System read by ID carries ConfigurationDrift: whether its configuration has changed in a way that needs a Full Synchronisation, in HasPendingChanges, IsDeterminable, NeverFullySynchronised, TrackingDisabled, LastFullSynchronisation, MostRecentChange, ChangeCount and HighestChangeClass. HasPendingChanges is also `$false` when JIM cannot tell, so test IsDeterminable first.

ConfigurationDrift also carries AwaitingSynchronisation: the Object Types whose objects the next synchronisation, Delta or Full, will all process because of Synchronisation Rule or Object Matching Rule changes saved since it last did so, each with its ChangeCount, ObjectCount and the Changes behind it. Those changes are not counted in ChangeCount and do not set HasPendingChanges, as an ordinary synchronisation applies them.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Connected System. |
| `Name` | `string` | The name of the Connected System. |
| `Description` | `string` | What the Connected System is for, for administrators; empty when none is given. |
| `Created` | `datetime` | When the Connected System was created (UTC). |
| `Status` | `string` | Whether the Connected System takes part in synchronisation: Active, Disabled, or Deleting while a deletion is under way. One of `Active`, `Disabled` or `Deleting`. |
| `ObjectCount` | `int` | How many Connected System Objects the Connected System holds. |
| `ConnectorsCount` | `int` | How many of those Connected System Objects are joined to a Metaverse Object. |
| `PendingExportObjectsCount` | `int` | How many Pending Exports are waiting to be exported to the Connected System. |
| `ConnectorName` | `string` | The name of the Connector the Connected System uses. |
| `ConnectorId` | `int` | The ID of that Connector. |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Connected System. |
| `Name` | `string` | The name of the Connected System. |
| `Description` | `string` | What the Connected System is for, for administrators; empty when none is given. |
| `Created` | `datetime` | When the Connected System was created (UTC). |
| `LastUpdated` | `datetime` | When the Connected System was last changed (UTC); empty if it never has been. |
| `Status` | `string` | Whether the Connected System takes part in synchronisation: Active, Disabled, or Deleting while a deletion is under way. One of `Active`, `Disabled` or `Deleting`. |
| `SettingValuesValid` | `bool` | Whether the Connector has validated the Connected System's current settings. False on a new Connected System, and again after any setting changes, until they are validated. |
| `Connector` | `object` | The Connector the Connected System uses. It has `Id` and `Name`. |
| `ObjectTypes` | `object[]` | The Connected System's Object Types, from its imported schema. Each has `Id`, `Name`, `Created`, `Selected`, `RemoveContributedAttributesOnObsoletion`, `AttributeCount`, `Tags`, `IsInternal`, `IsAuxiliary`, `ManagesClassMembership`, `MergedAuxiliaryClassObjectTypeIds`, `StructuralCarrierObjectTypeId` and `Attributes`. |
| `ObjectCount` | `int` | How many Connected System Objects the Connected System holds. |
| `PendingExportCount` | `int` | How many Pending Exports are waiting to be exported to the Connected System. |
| `MaxExportParallelism` | `int` | How many export batches run at once, where the Connector supports exporting in parallel; empty or 1 exports one batch at a time. |
| `InitialPasswordTimeToLive` | `string` | How long an account provisioned into this Connected System stays owed an initial password before JIM records an expiry and stops trying. Empty means JIM's default of seven days, and is reported as empty rather than as the default so a caller can tell a system configured to seven days from one never configured. |
| `RequireSecureTransport` | `bool` | Whether JIM refuses to send a password to this Connected System over a connection it cannot confirm is encrypted. Governs every password JIM sends here: the initial password on an account it provisions, one an administrator sets by hand, and a synchronised password change. |
| `UnresolvedReferenceHandling` | `string` | How an imported reference value that cannot be resolved to a Connected System Object is treated. The default, Error, fails the object; Warn records an Activity warning instead; Ignore records neither, while still logging the occurrence. One of `Error`, `Warn` or `Ignore`. |
| `ConfigurationDrift` | `object` | Whether the configuration has changed in a way that needs a Full Synchronisation to take effect. Empty on the create and update responses, which describe the write that just happened rather than the system's readiness. It has `HasPendingChanges`, `IsDeterminable`, `NeverFullySynchronised`, `TrackingDisabled`, `LastFullSynchronisation`, `MostRecentChange`, `ChangeCount`, `HighestChangeClass` and `AwaitingSynchronisation`. |
| `StrandedValueSweepArmedAt` | `datetime` | When a stranded-value sweep was armed by a Connector Space clear, or empty when no sweep is armed. The sweep runs at the first Full Synchronisation of this Connected System after LastSuccessfulFullImportCompletedAt is later than this timestamp; until then it stays armed and a Full Synchronisation does nothing beyond ordinary synchronisation. |
| `LastSuccessfulFullImportCompletedAt` | `datetime` | When the most recent Full Import of this Connected System completed successfully, or empty if none ever has. An import that completed with object-level errors does not count. |
| `DependentDerivedFlows` | `object[]` | On the response to an import-schema call that disabled or removed its dependents only: the Metaverse-Derived Attribute Flows, on any Connected System, that disabling or removing those Synchronisation Rules and mappings left with a missing input (an attribute they read lost its last enabled contributor). The refresh went ahead regardless; each flow's Missing Input Behaviour now decides what it contributes. Always present; empty on every other response. Each has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`. |

**With `-ObjectTypes`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Object Type. |
| `Name` | `string` | The name of the Object Type, as the Connected System calls it. |
| `Created` | `datetime` | When JIM first imported the Object Type (UTC). |
| `Selected` | `bool` | Whether JIM manages objects of this type; objects of an unselected type are not imported. |
| `RemoveContributedAttributesOnObsoletion` | `bool` | Whether the Metaverse attribute values a Connected System Object of this type contributed are recalled when the object is deleted from the Connected System. True by default. |
| `AttributeCount` | `int` | How many attributes the Object Type has. |
| `Tags` | `object[]` | How the Connected System classified this object type, as open key/value tags. A directory connector reports the class kind (structural, auxiliary, abstract) and, for classes the directory keeps for its own configuration or operation, a visibility of "internal". An object type carrying no tags is unclassified, which means "show it". Each has `Key` and `Value`. |
| `IsInternal` | `bool` | Whether the Connected System reported this object type as one it uses internally. Derived from Tags, and offered here so callers can filter on it without matching tag strings themselves. The portal hides these object types by default. |
| `IsAuxiliary` | `bool` | Whether the Connected System classified this object type as an auxiliary class: one that augments another rather than standing alone. Derived from Tags, and offered here so callers need not match tag strings themselves. |
| `ManagesClassMembership` | `bool` | Whether the Connected System hands this object type's class membership to JIM to compose, which is what makes merging an auxiliary class into it mean anything. False for Active Directory, which resolves its own auxiliary classes into each structural class. |
| `MergedAuxiliaryClassObjectTypeIds` | `int[]` | The auxiliary classes an administrator has merged into this Object Type, as their own Object Type IDs. |
| `StructuralCarrierObjectTypeId` | `int` | For an auxiliary object type, the structural object type JIM writes alongside it when creating an object, or empty when none has been named and JIM therefore cannot create one. |
| `Attributes` | `object[]` | The Object Type's attributes; empty when the response does not include them. Each has `Id`, `Name`, `Description`, `ClassName`, `Created`, `Type`, `TypeSetByAdministrator`, `AttributePlurality`, `PluralitySetByAdministrator`, `CaseExact`, `CaseExactSetByAdministrator`, `DateTimeResolution`, `DateTimeResolutionSetByAdministrator`, `Selected`, `IsExternalId`, `IsSecondaryExternalId`, `Required`, `SelectionLocked`, `Writability`, `IsOperational`, `IsConnectorDefined`, `ReferencedObjectTypeId` and `ReferencedObjectTypeName`. |

**With `-DeletionPreview`**:

| Property | Type | Description |
|----------|------|-------------|
| `ConnectedSystemId` | `int` | The ID of the Connected System. |
| `ConnectedSystemName` | `string` | The name of the Connected System. |
| `ConnectedSystemObjectCount` | `int` | How many Connected System Objects the deletion removes. |
| `SyncRuleCount` | `int` | How many Synchronisation Rules the deletion removes. |
| `RunProfileCount` | `int` | How many Run Profiles the deletion removes. |
| `PartitionCount` | `int` | How many partitions the deletion removes. |
| `ContainerCount` | `int` | How many containers the deletion removes. |
| `PendingExportCount` | `int` | How many Pending Exports the deletion discards. |
| `ActivityCount` | `int` | How many Activities name the Connected System. They are kept, as history. |
| `JoinedMvoCount` | `int` | How many Metaverse Objects are joined to one of its Connected System Objects, and so are disconnected by the deletion. |
| `MvosWithOtherConnectorsCount` | `int` | Of those, how many are also joined in another Connected System, so stay joined there. They can still lose the values this one contributed, and some can still be marked for deletion; MvosWithDeletionRuleCount counts those. |
| `MvosWithDeletionRuleCount` | `int` | How many Metaverse Objects their Metaverse Object Type's deletion rules will mark for deletion when the Connected System is deleted. |
| `MvosWithGracePeriodCount` | `int` | Of the objects MvosWithDeletionRuleCount counts, how many have a grace period, so are deleted when it ends rather than at once. |
| `ContributedValueCount` | `int` | How many Metaverse attribute values the Connected System's Synchronisation Rules contributed: the values the deletion recalls, or hands to another contributor. |
| `ContributedValueObjectCount` | `int` | How many Metaverse Objects hold at least one of those values. Not the same as JoinedMvoCount: an object can be joined without holding a contributed value. |
| `Warnings` | `string[]` | Anything that changes how the deletion runs, such as a synchronisation already running, which delays it until that finishes. |
| `EstimatedDeletionTime` | `string` | Roughly how long the deletion takes. |
| `WillRunAsBackgroundJob` | `bool` | True if the deletion will be queued as a background job (large system). False if it will execute synchronously. |
| `HasRunningSyncOperation` | `bool` | Whether a synchronisation is running on the Connected System, in which case the deletion waits for it to finish. |
<!-- END GENERATED: Get-JIMConnectedSystem -->

### Examples

```powershell title="List all Connected Systems"
Get-JIMConnectedSystem
```

```powershell title="Filter by name using wildcards"
Get-JIMConnectedSystem -Name "HR*"
```

```powershell title="Get a specific Connected System by ID"
Get-JIMConnectedSystem -Id 3
```

```powershell title="Retrieve object types for a Connected System"
Get-JIMConnectedSystem -Id 3 -ObjectTypes
```

```powershell title="Find Connected Systems needing a Full Synchronisation"
Get-JIMConnectedSystem |
    ForEach-Object { Get-JIMConnectedSystem -Id $_.Id } |
    Where-Object { $_.ConfigurationDrift.HasPendingChanges } |
    Select-Object Name, @{n='Changes';e={$_.ConfigurationDrift.ChangeCount}},
                        @{n='Highest';e={$_.ConfigurationDrift.HighestChangeClass}}
```

#### ConfigurationDrift (ById only)

Whether the configuration has changed in a way that needs a Full Synchronisation to take effect. Only Sync-affecting
and Destructive changes count, so a rename never registers here.

| Property | Type | Description |
|----------|------|-------------|
| `HasPendingChanges` | `bool` | Qualifying changes have been recorded since the last completed Full Synchronisation |
| `IsDeterminable` | `bool` | The question has a meaningful answer; see the caution below |
| `NeverFullySynchronised` | `bool` | No Full Synchronisation has ever completed, so there is no reference point |
| `TrackingDisabled` | `bool` | Configuration change tracking is off, so JIM holds no record of what changed |
| `LastFullSynchronisation` | `datetime?` | When the last completed Full Synchronisation started, or `$null` |
| `MostRecentChange` | `datetime?` | When the most recent qualifying change was recorded, or `$null` |
| `ChangeCount` | `int` | How many qualifying changes there are |
| `HighestChangeClass` | `string` | `Cosmetic`, `SyncAffecting` or `Destructive`; `NotClassified` when there are no changes |
| `AwaitingSynchronisation` | `object[]` | The Object Types whose objects the next synchronisation, Delta or Full, will all process because of saved import Synchronisation Rule or Object Matching Rule changes; none when nothing is waiting. These changes are not counted in `ChangeCount`, and are reported even when `IsDeterminable` is `$false` |

Each `AwaitingSynchronisation` entry has `ObjectTypeName`, `ChangeCount`, `ObjectCount`, `EarliestSavedAt` and `Changes`. Each change has `Reason`, `SavedAt`, `ConfigurationChangeActivityId`, `InitiatedByName`, `AppliedAt` and `SynchronisationActivityId`. See [Changes awaiting synchronisation](../configuration/connected-systems.md#changes-awaiting-synchronisation).

```powershell title="See what is waiting for a Connected System's next synchronisation"
(Get-JIMConnectedSystem -Id 3).ConfigurationDrift.AwaitingSynchronisation |
    Select-Object ObjectTypeName, ChangeCount, ObjectCount, EarliestSavedAt
```

#### Stranded-value sweep (ById only)

Whether a stranded-value sweep is armed following a Connector Space clear, and what it is waiting for. See
[Clearing the Connector Space](../configuration/connector-space.md#clearing-the-connector-space).

| Property | Type | Description |
|----------|------|-------------|
| `StrandedValueSweepArmedAt` | `datetime?` | When the Connector Space was last cleared, or `$null` if no sweep is armed |
| `LastSuccessfulFullImportCompletedAt` | `datetime?` | When the most recent Full Import of this Connected System completed successfully, or `$null` if none ever has |

The sweep runs at the first Full Synchronisation after `LastSuccessfulFullImportCompletedAt` is later than
`StrandedValueSweepArmedAt`. A Full Synchronisation run before that leaves the arming in place and states so
on its Activity.

```powershell title="Find systems with a stranded-value sweep waiting on a Full Import"
Get-JIMConnectedSystem |
    ForEach-Object { Get-JIMConnectedSystem -Id $_.Id } |
    Where-Object {
        $_.StrandedValueSweepArmedAt -and
        (-not $_.LastSuccessfulFullImportCompletedAt -or $_.LastSuccessfulFullImportCompletedAt -le $_.StrandedValueSweepArmedAt)
    } |
    Select-Object Name, StrandedValueSweepArmedAt, LastSuccessfulFullImportCompletedAt
```

!!! warning "Check `IsDeterminable` before treating `HasPendingChanges` as false"
    `HasPendingChanges` is also `$false` when JIM cannot tell: when the Connected System has never completed a Full
    Synchronisation, and when configuration change tracking is switched off. Scripts that gate a run on
    `-not $_.ConfigurationDrift.HasPendingChanges` will skip those systems silently. Test `IsDeterminable` first.

```powershell title="Preview the impact of deleting a Connected System"
Get-JIMConnectedSystem -Id 3 -DeletionPreview
```

---

## :material-plus-circle-outline: New-JIMConnectedSystem

Creates a new Connected System.

<!-- BEGIN GENERATED: New-JIMConnectedSystem -->
### Syntax

```powershell
New-JIMConnectedSystem [-Name] <string> -ConnectorDefinitionId <int> [-Description <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name of the Connected System. Can be given by position, as the first argument. |
| `Description` | `string` | No |  | What the Connected System is for, for administrators. |
| `ConnectorDefinitionId` | `int` | Yes |  | The ID of the Connector the Connected System uses; `Get-JIMConnectorDefinition` lists them. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded in the Connected System's change history. |
| `PassThru` | `switch` | No |  | Returns the Connected System created. |

### Output

With `-PassThru`, the Connected System created; otherwise nothing.

Properties as under [Get-JIMConnectedSystem](#get-jimconnectedsystem).
<!-- END GENERATED: New-JIMConnectedSystem -->

### Examples

```powershell title="Create a Connected System"
New-JIMConnectedSystem -Name "Active Directory" -ConnectorDefinitionId 1
```

```powershell title="Create and capture the result"
$cs = New-JIMConnectedSystem "HR Database" -ConnectorDefinitionId 2 -Description "Primary HR source" -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before creation.

---

## :material-pencil-outline: Set-JIMConnectedSystem

Updates the configuration of an existing Connected System.

<!-- BEGIN GENERATED: Set-JIMConnectedSystem -->
### Syntax

```powershell
# ById (default)
Set-JIMConnectedSystem -Id <int> [-Name <string>] [-Description <string>] [-SettingValues <hashtable>] [-MaxExportParallelism <int>] [-RequireSecureTransport] [-InitialPasswordTimeToLive <timespan>] [-UnresolvedReferenceHandling <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMConnectedSystem -InputObject <PSCustomObject> [-Name <string>] [-Description <string>] [-SettingValues <hashtable>] [-MaxExportParallelism <int>] [-RequireSecureTransport] [-InitialPasswordTimeToLive <timespan>] [-UnresolvedReferenceHandling <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System to change. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `Name` | `string` | No |  | The new name of the Connected System. |
| `Description` | `string` | No |  | What the Connected System is for, for administrators. |
| `SettingValues` | `hashtable` | No |  | The Connector settings to change, as a hashtable keyed by setting ID (from `Get-JIMConnectorDefinition`) whose values are hashtables holding a stringValue, intValue or checkboxValue. |
| `MaxExportParallelism` | `int` | No |  | How many export batches run at once, where the Connector supports exporting in parallel. 1 exports one batch at a time. From 1 to 16. |
| `RequireSecureTransport` | `switch` | No |  | Refuse to send a password to this Connected System over a connection JIM cannot confirm is encrypted. Governs every password JIM sends here: the first password on an account it provisions, one set by hand, and a synchronised password change. Off by default, because a signed and sealed bind is a legitimate encrypted alternative that cannot be detected from the Connected System's settings alone. Pass `-RequireSecureTransport` to turn it on and `-RequireSecureTransport:$false` to turn it off. Nothing is discarded when JIM refuses: queued password changes wait, and accounts stay owed their first password. |
| `InitialPasswordTimeToLive` | `timespan` | No |  | How long an account provisioned into the Connected System stays owed an initial password before JIM records an expiry and stops trying, as a TimeSpan. Seven days until one is set. Raise it ahead of an outage longer than the current window, or every account provisioned while the Connected System is unreachable expires without a password. |
| `UnresolvedReferenceHandling` | `string` | No |  | How an imported reference value that cannot be resolved to a Connected System Object is treated: Error fails the object's Run Profile execution item; Warn completes the Activity with a warning counting them instead; Ignore records neither, though the references are still logged and shown on the Connected System Object. One of `Error`, `Warn` or `Ignore`. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded in the Connected System's change history. |
| `PassThru` | `switch` | No |  | Returns the Connected System as changed. |

### Output

With `-PassThru`, the Connected System as changed; otherwise nothing.

Properties as under [Get-JIMConnectedSystem](#get-jimconnectedsystem).
<!-- END GENERATED: Set-JIMConnectedSystem -->

### Examples

```powershell title="Rename a Connected System"
Set-JIMConnectedSystem -Id 3 -Name "AD Production"
```

```powershell title="Update connector settings"
Set-JIMConnectedSystem -Id 3 -SettingValues @{
    1 = @{ stringValue = "ldaps://dc01.example.com" }
    2 = @{ intValue = 636 }
    3 = @{ checkboxValue = $true }
}
```

```powershell title="Pipeline input from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Set-JIMConnectedSystem -MaxExportParallelism 8 -PassThru
```

```powershell title="Update a setting and record why (shown in the change history)"
Set-JIMConnectedSystem -Id 3 -Description "Point at DR domain controller" -ChangeReason "Failover for DC maintenance (CHG0101)"
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before changes.

---

## :material-trash-can-outline: Remove-JIMConnectedSystem

Deletes a Connected System and all its associated data.

<!-- BEGIN GENERATED: Remove-JIMConnectedSystem -->
### Syntax

```powershell
# ById (default)
Remove-JIMConnectedSystem -Id <int> [-DeleteImmediately] [-PassThru] [-Force] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMConnectedSystem -InputObject <PSCustomObject> [-DeleteImmediately] [-PassThru] [-Force] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `DeleteImmediately` | `switch` | No |  | Deletes the Connected System immediately instead of deprovisioning through synchronisation. The values the Connected System contributed are kept with no provenance, so nothing can ever recall them; surviving contributors are not re-elected and downstream systems are not corrected. On a system whose deprovisioning run failed partway, this finishes the deletion immediately, abandoning the remaining deprovisioning work. |
| `PassThru` | `switch` | No |  | Returns the outcome of a deletion that completes at once. A deletion that queues returns its tracking object either way. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt, and the deletion preview it states. |
| `ChangeReason` | `string` | No |  | Why the Connected System was deleted, recorded on the deletion's Activity and in its change history. |
| `PreviewActivityId` | `guid` | No |  | The ID of the deletion preview this deletion was made after reading: the Activity that `New-JIMConfigurationChangePreview -Deletion` returns for this Connected System. Recorded on the deletion's Activity so "previewed, then deleted" is auditable rather than a claim. JIM refuses the deletion, deleting nothing, when the id is not a deletion preview of this same Connected System. A preview that a later run or configuration change has overtaken is still recorded, and the deletion's Activity says it was out of date and what overtook it; the cmdlet writes a warning saying so. |

### Output

Depends on how the deletion runs. One that runs in the background, as every deletion that deprovisions does, returns an object to follow it by. One that completes at once returns nothing, or its outcome with `-PassThru`.

**When the deletion queues**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The ID of the deletion's Activity; follow it with `Get-JIMActivity`. |
| `WorkerTaskId` | `guid` | The ID of the worker task carrying out the deletion. |
| `Outcome` | `string` | QueuedAsBackgroundJob, or QueuedAfterSync when a running synchronisation delays it. |
| `ConnectedSystemObjectCount` | `int` | How many Connected System Objects the deletion removes, from the deletion preview; empty with `-Force`, which skips the preview. |
| `ContributedValueCount` | `int` | How many Metaverse attribute values the Connected System contributed; empty with `-Force`. |
| `ContributedValueObjectCount` | `int` | How many Metaverse Objects hold those values; empty with `-Force`. |

**With `-PassThru`, when the deletion completes at once**:

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether JIM accepted the deletion; false when it failed, and ErrorMessage says why. |
| `Outcome` | `string` | How the deletion runs: at once, as a background job, or once a running synchronisation finishes; or Failed. One of `CompletedImmediately`, `QueuedAsBackgroundJob`, `QueuedAfterSync` or `Failed`. |
| `ErrorMessage` | `string` | Why the deletion failed; empty unless the Outcome is Failed. |
| `WorkerTaskId` | `guid` | The ID of the worker task carrying out the deletion; empty when it completed at once. |
| `ActivityId` | `guid` | The ID of the Activity recording the deletion; empty when it failed. |
| `Warnings` | `string[]` | What the caller should know about a deletion that went ahead, such as the preview it cites having been out of date. Empty when there is nothing to say. |
<!-- END GENERATED: Remove-JIMConnectedSystem -->

### Examples

```powershell title="Deprovision a Connected System with confirmation"
Remove-JIMConnectedSystem -Id 3
```

```powershell title="Preview the deletion, then delete and record the preview"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
Remove-JIMConnectedSystem -Id 3 -PreviewActivityId $preview.ActivityId -ChangeReason "Decommissioned (CHG0123)"
```

```powershell title="Deprovision without confirmation and follow the run"
$tracking = Remove-JIMConnectedSystem -Id 3 -Force
Get-JIMActivity -Id $tracking.ActivityId
```

```powershell title="Delete immediately, keeping the values the system contributed"
Remove-JIMConnectedSystem -Id 3 -DeleteImmediately -Force
```

```powershell title="Delete every Connected System matching a name pattern, immediately"
# -Name supports wildcards, so this deletes ALL matching Connected Systems and
# their connector spaces. Run it without -Force first to confirm the matches.
Get-JIMConnectedSystem -Name "Decommissioned*" | Remove-JIMConnectedSystem -DeleteImmediately -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation, and the prompt states the headline impact of the chosen mode.
- Deprovisioning always runs as a background job. An immediate deletion of a small Connected System (fewer than 1,000 objects) completes straight away; a larger one is queued as a background job.

---

## :material-puzzle-outline: Get-JIMConnectedSystemCapability

Retrieves the Connector-detected capabilities for a Connected System, e.g. an LDAP directory's type, vendor, DNS host name, and paging support. These are facts read from the target system during a previous connection and persisted by JIM; calling this cmdlet does not open a new connection.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemCapability -->
### Syntax

```powershell
Get-JIMConnectedSystemCapability -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

One entry per fact the Connector detected, such as the directory type or paging support.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | What the fact is about, such as the directory type, its vendor, or whether it supports paging. |
| `Value` | `string` | What the Connector found, as text. |
<!-- END GENERATED: Get-JIMConnectedSystemCapability -->

### Examples

```powershell title="Get the detected capabilities for a Connected System"
Get-JIMConnectedSystemCapability -ConnectedSystemId 1
```

```powershell title="Get capabilities for a named Connected System via pipeline"
Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMConnectedSystemCapability
```

### Notes

- These facts mirror the **Detected** strip on the Connected System's Details tab in the portal; see the [JIM LDAP Connector](../connectors/jim-ldap-connector.md#detected-facts) documentation for what each fact means.

---

## :material-broom: Clear-JIMConnectedSystem

Removes every Connected System Object and its associated data from a Connected System without deleting the system itself. The Connected System configuration, schema, and Synchronisation Rules are preserved.

The clear runs as a queued background task, tracked by an Activity, exactly like the portal: the cmdlet returns as soon as the task is queued rather than running the deletion inline, so the operation is audited and trackable regardless of which surface started it. Use `-Wait` to block until it has finished.

<!-- BEGIN GENERATED: Clear-JIMConnectedSystem -->
### Syntax

```powershell
# ById (default)
Clear-JIMConnectedSystem -Id <int> [-KeepChangeHistory] [-Wait] [-Timeout <int>] [-Force] [-WhatIf] [-Confirm]

# ByInputObject
Clear-JIMConnectedSystem -InputObject <PSCustomObject> [-KeepChangeHistory] [-Wait] [-Timeout <int>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System to clear. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `KeepChangeHistory` | `switch` | No |  | Keeps the deleted objects' change history, no longer linked to them. |
| `Wait` | `switch` | No |  | Waits for the clear to finish before returning. Without it, an import run straight afterwards races the clear. |
| `Timeout` | `int` | No |  | How many seconds `-Wait` waits before reporting an error; the clear carries on regardless. Without it, `-Wait` waits until the clear finishes. At least 1. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

The IDs of the Activity and worker task carrying out the clear.

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity id for tracking the clear. |
| `TaskId` | `guid` | The worker task id for the queued clear. |
| `Message` | `string` | Message describing the result. |
<!-- END GENERATED: Clear-JIMConnectedSystem -->

### Examples

```powershell title="Clear a Connected System with confirmation"
Clear-JIMConnectedSystem -Id 3
```

```powershell title="Clear without confirmation, keeping history"
Clear-JIMConnectedSystem -Id 3 -KeepChangeHistory -Force
```

```powershell title="Clear and wait for it to finish"
Clear-JIMConnectedSystem -Id 3 -Force -Wait
```

```powershell title="Pipeline: clear a system by name"
Get-JIMConnectedSystem -Name "Staging AD" | Clear-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removes every Connected System Object with its attribute values, and the Connected System's Pending Exports and deferred references.
- Metaverse Objects are **not** deleted by the clear itself; their links to this Connected System are severed.
- By default, change history is also deleted. Use `-KeepChangeHistory` to retain it for auditing purposes.
- Without `-Wait`, the cmdlet returns as soon as the clear is queued; a script that immediately re-imports, or reads the Connector Space back, races the clear task.
- The clear records which Metaverse Objects were joined and arms the stranded-value sweep, which runs automatically at this Connected System's next Full Synchronisation once a Full Import has completed successfully: it recalls any Metaverse attribute value the system contributed whose object never returned, and applies that object type's Deletion Rule to objects that never returned. Run a Full Import before that synchronisation so objects that do return can reclaim their values and avoid the Deletion Rule firing on them. If far fewer objects return than were cleared, the sweep refuses rather than deleting most of the population; see `Sync.PostClearReconciliation.MaxMissingPercent` in [`Get-JIMServiceSetting`](service-settings.md). See [Clearing the Connector Space](../configuration/connector-space.md#clearing-the-connector-space).

---

## :material-delete-sweep-outline: Get-JIMConnectedSystemDeletionPreview

Retrieves a preview of the impact of deleting a Connected System, including counts of affected objects and warnings.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemDeletionPreview -->
### Syntax

```powershell
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

What deleting the Connected System would remove, disconnect and recall.

Properties as under [Get-JIMConnectedSystem](#get-jimconnectedsystem).
<!-- END GENERATED: Get-JIMConnectedSystemDeletionPreview -->

### Examples

```powershell title="Preview deletion impact"
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId 3
```

```powershell title="Pipeline: preview before deleting"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemDeletionPreview
```

```powershell title="Check all systems for deletion impact"
Get-JIMConnectedSystem | ForEach-Object {
    $preview = $_ | Get-JIMConnectedSystemDeletionPreview
    [PSCustomObject]@{
        Name = $_.Name
        CSOCount = $preview.ConnectedSystemObjectCount
        SyncRules = $preview.SyncRuleCount
    }
}
```

---

## :material-book-open-variant-outline: See also

- [Connected Systems](../configuration/connected-systems.md): what Connected Systems are, the Connector Space, partitions and containers, and common workflows
- [Connected System Schema](connected-system-schema.md): import a Connected System's schema, and choose its Object Types and attributes
- [Auxiliary Object Classes](connected-system-auxiliary-classes.md): the auxiliary classes an LDAP Connected System's Object Types carry
- [Partitions and Containers](partitions-and-containers.md): import a Connected System's hierarchy, and choose which partitions and containers JIM imports from
- [Connected System Objects](connected-system-objects.md): read the objects in the Connector Space, and set their passwords
- [Pending Exports](pending-exports.md): read the changes waiting to be written to a Connected System
- [Run Profiles](run-profiles.md): execute import, sync, and export operations on Connected Systems
- [Synchronisation Rules](synchronisation-rules.md): define attribute mappings and scoping for Connected System synchronisation, including the initial password set on provisioned accounts
- [Connectors](connectors.md): the Connectors a Connected System is created from
- [Certificates](certificates.md): read and approve the TLS certificate a Connected System's server presents
- [Password Synchronisation](password-synchronisation.md): choose which Connected Systems receive passwords
- [Connection](connection.md): establish a session before using these cmdlets
