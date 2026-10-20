---
title: Auxiliary Object Classes
description: "The cmdlets that choose the auxiliary object classes an LDAP Connected System's Object Types carry, and discover which are in use."
type: reference
audience: automation-author
---

# Auxiliary Object Classes

The auxiliary class cmdlets choose which auxiliary object classes an LDAP Connected System's Object Types carry, name the structural class an auxiliary Object Type is created with, and read a directory's entries to find which auxiliary classes are in use.

---

## Get-JIMConnectedSystemAuxiliaryClass

Lists the auxiliary classes that can be merged into a Connected System Object Type, and which of them are merged already.

Merging an auxiliary class brings its attributes into the Object Type, so JIM can import and export them. See [Auxiliary Object Classes](../connectors/jim-ldap-connector-auxiliary-classes.md) for what this means on a directory.

Nothing is returned for an Object Type whose Connected System does not let JIM compose class membership (Active Directory resolves its own auxiliary classes into each structural class), or for an Object Type that is itself an auxiliary class.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemAuxiliaryClass -->
### Syntax

```powershell
Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId <int> -ObjectTypeId <int> [-MergedOnly] [-SuggestedOnly]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Object Type to list auxiliary classes for. Accepts pipeline input by property name. |
| `MergedOnly` | `switch` | No |  | Return only the classes currently merged into the Object Type. |
| `SuggestedOnly` | `switch` | No |  | Return only the classes something suggests: a DIT Content Rule permits them, or a discovery run observed them in use. |

### Output

The auxiliary classes, merged first, then suggested, then the rest by name.

| Property | Type | Description |
|----------|------|-------------|
| `ObjectTypeId` | `int` | The auxiliary class's own Connected System Object Type id, which is what merging is set by. |
| `Name` | `string` | The name of the auxiliary class. |
| `Merged` | `bool` | Whether this class is currently merged into the object type the offers were requested for. |
| `ContributedAttributeCount` | `int` | How many attributes merging this class would contribute. |
| `ContributedAttributes` | `object[]` | The attributes merging this class would contribute, ordered by name and excluding any the Object Type already carries. Each has `Name`, `Type`, `AttributePlurality`, `Required` and `IsCredential`. |
| `PermittedByTheConnectedSystem` | `bool` | Whether the Connected System itself says this class may attach, i.e. an RFC 4512 DIT Content Rule names it. Most directories publish no such statement, so its absence says nothing. |
| `EntriesObservedOn` | `int` | How many of the entries the last discovery run read were carrying this class, or empty when no run has observed it. Read against that run's scope: a quick sample counts within the sample, not the population. |
| `IsSuggested` | `bool` | Whether anything suggests this class, as opposed to it merely being defined in the schema. |
<!-- END GENERATED: Get-JIMConnectedSystemAuxiliaryClass -->

### Examples

```powershell title="List every auxiliary class on offer"
Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5
```

```powershell title="Show only the classes JIM has a reason to suggest"
Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -SuggestedOnly |
    Format-Table Name, ContributedAttributeCount, EntriesObservedOn
```

```powershell title="See what merging a class would bring"
(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 |
    Where-Object Name -eq 'posixAccount').ContributedAttributes |
    Format-Table Name, Type, Required, IsCredential
```

```powershell title="Name what the Object Type carries today"
Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly |
    Select-Object -ExpandProperty Name
```

---

## Set-JIMConnectedSystemAuxiliaryClass

Sets which auxiliary classes a Connected System Object Type carries.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemAuxiliaryClass -->
### Syntax

```powershell
# Set (default)
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId <int> -ObjectTypeId <int> -AuxiliaryClassObjectTypeId <int[]> [-PassThru] [-WhatIf] [-Confirm]

# Clear
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId <int> -ObjectTypeId <int> -Clear [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Object Type the classes are merged into. Accepts pipeline input by property name. |
| `AuxiliaryClassObjectTypeId` | `int[]` | Yes (Set) |  | The auxiliary classes the Object Type should carry, by their own Object Type IDs. Each must be an auxiliary class in the same Connected System. |
| `Clear` | `switch` | Yes (Clear) |  | Withdraws every auxiliary class from the Object Type. |
| `PassThru` | `switch` | No |  | Returns the Object Type as changed. |

### Output

With `-PassThru`, the Object Type as changed, whose MergedAuxiliaryClassObjectTypeIds lists the classes it now carries; otherwise nothing.

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
| `Attributes` | `object[]` | The Object Type's attributes; empty when the response does not include them. Each has `Id`, `Name`, `Description`, `ClassName`, `Created`, `Type`, `TypeSetByAdministrator`, `AttributePlurality`, `PluralitySetByAdministrator`, `CaseExact`, `CaseExactSetByAdministrator`, `DateTimeResolution`, `DateTimeResolutionSetByAdministrator`, `Selected`, `IsExternalId`, `IsSecondaryExternalId`, `Required`, `SelectionLocked`, `Writability`, `IsOperational`, `ReferencedObjectTypeId` and `ReferencedObjectTypeName`. |
<!-- END GENERATED: Set-JIMConnectedSystemAuxiliaryClass -->

### Examples

```powershell title="Merge one auxiliary class"
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId 12
```

```powershell title="Add a class to whatever is already merged"
$merged = (Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly).ObjectTypeId
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId ($merged + 12)
```

```powershell title="Withdraw every auxiliary class from an Object Type"
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -Clear
```

### Notes

- **This replaces the whole set, it does not add to it.** A class that is merged and not named here is withdrawn. Read the current set first, as the second example does, when you mean to add one.
- Withdrawing a class removes its attributes from the Object Type at the next schema import, which removes any Attribute Flow using them. Run `Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId <id> -ObjectTypeId <id> -MergedOnly` and check what flows those attributes before clearing.
- Merged attributes appear after the next `Import-JIMConnectedSystemSchema`.
- Supports `ShouldProcess` (Medium impact).

---

## Set-JIMConnectedSystemStructuralCarrierClass

Sets the Structural Carrier Class of an auxiliary Connected System Object Type: the structural class JIM writes alongside the auxiliary one when creating an entry.

Every entry in a directory carries exactly one structural class, so until a carrier is named JIM can import objects of the type but cannot create them.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemStructuralCarrierClass -->
### Syntax

```powershell
# Set (default)
Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId <int> -ObjectTypeId <int> -StructuralCarrierObjectTypeId <int> [-PassThru] [-WhatIf] [-Confirm]

# Clear
Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId <int> -ObjectTypeId <int> -Clear [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the auxiliary Object Type. Accepts pipeline input by property name. |
| `StructuralCarrierObjectTypeId` | `int` | Yes (Set) |  | The structural Object Type JIM writes alongside the auxiliary class when creating an object. |
| `Clear` | `switch` | Yes (Clear) |  | Clears the carrier, so objects of the type can be imported but not created. |
| `PassThru` | `switch` | No |  | Returns the Object Type as changed. |

### Output

With `-PassThru`, the Object Type as changed, whose StructuralCarrierObjectTypeId names its carrier; otherwise nothing.

Properties as under [Set-JIMConnectedSystemAuxiliaryClass](#set-jimconnectedsystemauxiliaryclass).
<!-- END GENERATED: Set-JIMConnectedSystemStructuralCarrierClass -->

### Examples

```powershell title="Name the structural class to create objects as"
Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId 1 -ObjectTypeId 12 -StructuralCarrierObjectTypeId 3
```

```powershell title="Find selected auxiliary Object Types JIM cannot yet create objects for"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 |
    Where-Object { $_.isAuxiliary -and $_.selected -and -not $_.structuralCarrierObjectTypeId } |
    Select-Object -Property id, name
```

### Notes

- Only an auxiliary Object Type takes a carrier, and only a structural Object Type in the same Connected System can be one. Anything else is refused with a message naming the problem.
- Supports `ShouldProcess` (Medium impact).

---

## Start-JIMConnectedSystemAuxiliaryClassDiscovery

Starts a run that reads a Connected System's entries to find which auxiliary classes they carry.

It changes no configuration: what an Object Type carries stays whatever an administrator has merged. The counts become suggestions on `Get-JIMConnectedSystemAuxiliaryClass`.

<!-- BEGIN GENERATED: Start-JIMConnectedSystemAuxiliaryClassDiscovery -->
### Syntax

```powershell
Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId <int> -Scope <string> [-SampleSizePerObjectType <int>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System to read. Accepts pipeline input by property name. |
| `Scope` | `string` | Yes |  | QuickSample to read a fixed number of entries of each Object Type, or FullScan to read every entry in scope. One of `QuickSample` or `FullScan`. |
| `SampleSizePerObjectType` | `int` | No | `5000` | How many entries of each Object Type a QuickSample reads. Ignored for a FullScan, which reads them all. From 1 to 1,000,000. |

### Output

The IDs of the worker task and Activity the run reports against, for following or cancelling it.

| Property | Type | Description |
|----------|------|-------------|
| `WorkerTaskId` | `guid` | The queued task, for a caller that wants to cancel it. |
| `ActivityId` | `guid` | The Activity carrying the run's progress, cancellation and errors. |
<!-- END GENERATED: Start-JIMConnectedSystemAuxiliaryClassDiscovery -->

### Examples

```powershell title="Sample each Object Type"
Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample
```

```powershell title="Read every entry in scope"
Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope FullScan
```

```powershell title="Start a larger sample and watch its Activity"
$run = Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1 -Scope QuickSample -SampleSizePerObjectType 20000
Get-JIMActivity -Id $run.ActivityId
```

### Notes

- A quick sample cannot prove a class unused: a directory returns entries in its own order, so a rarely used class can be missed. Only a full scan's "not in use" means anything.
- A full scan reads every entry in scope and can take a long time on a large directory. It requests class membership and nothing else.
- One run at a time per Connected System. Starting a second while one is in progress is refused with a message saying so.
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectedSystemAuxiliaryClassDiscovery

Gets the last auxiliary class discovery run for a Connected System, whatever its outcome. Returns nothing when none has been started.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemAuxiliaryClassDiscovery -->
### Syntax

```powershell
Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |

### Output

The most recent discovery run, or nothing when none has been started.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the discovery run. |
| `Scope` | `string` | How much the run reads: QuickSample reads up to SampleSizePerObjectType entries per Object Type, which finds the classes a population uses consistently but cannot prove one unused; FullScan reads every entry in scope. One of `NotSet`, `QuickSample` or `FullScan`. |
| `SampleSizePerObjectType` | `int` | How many entries per Object Type a quick sample read. Empty for a full scan, which has no per-type limit. |
| `Status` | `string` | Where the run has got to: InProgress, Complete, Cancelled or Failed. The results of a run that is not Complete are partial. One of `NotSet`, `InProgress`, `Complete`, `Cancelled` or `Failed`. |
| `Started` | `datetime` | When the run started (UTC). |
| `Completed` | `datetime` | When the run stopped, however it stopped. Empty while it is still going. |
| `EntriesRead` | `int` | How many entries were read. Meaningful on a cancelled or failed run too, as it says how much of the population the partial results cover. |
| `ActivityId` | `guid` | The Activity carrying the run's progress, cancellation and errors. |
| `InitiatedByName` | `string` | The name of whoever started the run. |
| `ErrorMessage` | `string` | Why the run failed, when its status is Failed. |
| `Results` | `object[]` | Each auxiliary class the run found, by Object Type, with how many entries carry it. Each has `StructuralObjectTypeId`, `AuxiliaryClassName` and `EntryCount`. |
<!-- END GENERATED: Get-JIMConnectedSystemAuxiliaryClassDiscovery -->

### Examples

```powershell title="Read the last run"
Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1
```

```powershell title="Rank the classes the last run observed"
(Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1).Results |
    Sort-Object -Property EntryCount -Descending |
    Format-Table AuxiliaryClassName, EntryCount
```

### Notes

- A cancelled run keeps the results it did gather. Those classes are genuinely in use; the ones it never reached are unknown.

---

## See also

- [LDAP Connector: Auxiliary Object Classes](../connectors/jim-ldap-connector-auxiliary-classes.md): what merging an auxiliary class does on a directory, and how JIM composes each entry's class membership on export
- [Connected System Schema](connected-system-schema.md): import the schema the auxiliary classes come from
