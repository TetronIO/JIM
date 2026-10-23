---
title: Partitions and Containers
description: "The cmdlets that import a Connected System's hierarchy and choose which partitions and containers JIM imports from."
type: reference
audience: automation-author
---

# Partitions and Containers

The partition and container cmdlets import a Connected System's hierarchy, choose which of its partitions and containers JIM imports objects from, and how far beneath each container. How they decide what JIM sees is covered in [Partitions and Containers](../configuration/partitions-and-containers.md).

---

## Import-JIMConnectedSystemHierarchy

Imports a Connected System's partitions and containers, or refreshes those imported before, keeping your selections.

<!-- BEGIN GENERATED: Import-JIMConnectedSystemHierarchy -->
### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemHierarchy -Id <int> [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Import-JIMConnectedSystemHierarchy -InputObject <PSCustomObject> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System to import the hierarchy of. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `PassThru` | `switch` | No |  | Returns what the refresh changed. |

### Output

With `-PassThru`, what the refresh added, removed, renamed and moved; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether the hierarchy refresh completed successfully. |
| `ErrorMessage` | `string` | Error message if the refresh failed. |
| `TotalPartitions` | `int` | Total number of partitions after refresh. |
| `TotalContainers` | `int` | Total number of containers after refresh. |
| `Summary` | `string` | Brief summary of changes (e.g., "2 added, 1 removed, 3 updated"). |
| `HasChanges` | `bool` | Whether there were any changes to the hierarchy. |
| `HasSelectedItemsRemoved` | `bool` | Whether any selected items were removed (potential data loss warning). |
| `AddedPartitions` | `object[]` | Partitions that were added (new to the hierarchy). Each has `ExternalId`, `Name`, `WasSelected` and `ItemType`. |
| `RemovedPartitions` | `object[]` | Partitions that were removed from the hierarchy. Each has `ExternalId`, `Name`, `WasSelected` and `ItemType`. |
| `RenamedPartitions` | `object[]` | Partitions that were renamed (ExternalId matched, Name changed). Each has `ExternalId`, `OldName`, `NewName` and `ItemType`. |
| `AddedContainers` | `object[]` | Containers that were added (new to the hierarchy). Each has `ExternalId`, `Name`, `WasSelected` and `ItemType`. |
| `RemovedContainers` | `object[]` | Containers that were removed from the hierarchy. Each has `ExternalId`, `Name`, `WasSelected` and `ItemType`. |
| `RenamedContainers` | `object[]` | Containers that were renamed (ExternalId matched, Name changed). Each has `ExternalId`, `OldName`, `NewName` and `ItemType`. |
| `MovedContainers` | `object[]` | Containers that moved to a different parent (ExternalId matched, parent changed). Each has `ExternalId`, `Name`, `OldParentExternalId` and `NewParentExternalId`. |
<!-- END GENERATED: Import-JIMConnectedSystemHierarchy -->

### Examples

```powershell title="Import hierarchy"
Import-JIMConnectedSystemHierarchy -Id 3
```

```powershell title="Pipeline: import schema, then hierarchy"
Get-JIMConnectedSystem -Id 3 |
    Import-JIMConnectedSystemSchema -PassThru |
    Import-JIMConnectedSystemHierarchy -PassThru
```

### Notes

- A refresh matches partitions and containers by the Connected System's own immutable identifier where it has one, so one that was renamed or moved keeps its selection. A selected one the Connected System no longer reports is removed, and the result's `HasSelectedItemsRemoved` flags it. See [Renames and moves in the source system](../configuration/partitions-and-containers.md#renames-and-moves-in-the-source-system).
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectedSystemPartition

Gets a Connected System's partitions, each with its tree of containers.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemPartition -->
### Syntax

```powershell
Get-JIMConnectedSystemPartition -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

The partitions, each with its tree of containers. Every container carries the same properties as the containers `Set-JIMConnectedSystemContainer` returns. A container's ObjectCount of 0 means it was searched and found empty; `$null` means nobody has counted it.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the partition. |
| `Name` | `string` | The name of the partition. |
| `ExternalId` | `string` | The partition's identifier in the Connected System; on an LDAP directory, its distinguished name. |
| `Selected` | `bool` | Whether JIM imports from the partition. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the partition belongs to. |
| `Containers` | `object[]` | The partition's top-level containers, each with its child containers. Each has `Id`, `Name`, `ExternalId`, `Description`, `Hidden`, `Selected`, `Excluded`, `Scope`, `ObjectCount`, `SubtreeObjectCount`, `PartitionId`, `ConnectedSystemId` and `ChildContainers`. |
<!-- END GENERATED: Get-JIMConnectedSystemPartition -->

### Examples

```powershell title="Get partitions for a Connected System"
Get-JIMConnectedSystemPartition -ConnectedSystemId 3
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPartition
```

---

## Get-JIMConnectedSystemDirectoryServer

Discovers the directory servers in a Connected System's directory, for choosing a value for the setting its Connector fills from discovery. For the LDAP Connector these are the domain controllers in an Active Directory or Samba AD forest, with the Active Directory Site each belongs to, and the setting is Preferred Domain Controller; Connectors that do not support discovery, and LDAP directories that are not AD-family (OpenLDAP, 389 Directory Server, Generic), return an error naming why. Purely informational: it never writes anything. Aliased as `Get-JIMConnectedSystemDomainController`.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemDirectoryServer -->
### Syntax

```powershell
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

One entry per server found: its HostName, the Active Directory Site it belongs to (empty for directories that have no Sites), and the id and name of the setting its host name is a value for.

| Property | Type | Description |
|----------|------|-------------|
| `HostName` | `string` | The fully qualified domain name of the directory server, e.g. "dc01.corp.local". |
| `Site` | `string` | The Active Directory Site the server belongs to, e.g. "Default-First-Site-Name". Empty for directories that have no concept of Sites. |
| `TargetSettingId` | `int` | The id of the Connected System setting this server's host name is a value for (for the LDAP Connector, Preferred Domain Controller): the key to use when setting it with `Set-JIMConnectedSystem -SettingValues` or the update endpoint. Empty only if the Connected System does not carry the setting its Connector names. |
| `TargetSettingName` | `string` | The name of the Connected System setting this server's host name is a value for, e.g. "Preferred Domain Controller". |
<!-- END GENERATED: Get-JIMConnectedSystemDirectoryServer -->

### Examples

```powershell title="Discover domain controllers for a Connected System"
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 3
```

```powershell title="Filter to a specific Active Directory Site"
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 3 | Where-Object { $_.site -eq 'London' }
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Name "Corp AD" | Get-JIMConnectedSystemDirectoryServer
```

### Notes

- This is a discovery aid, not a configuration write: use `Set-JIMConnectedSystem` to set the Preferred Domain Controller setting once you have chosen one.

---

## Set-JIMConnectedSystemPartition

Chooses whether JIM imports objects from a partition of a Connected System.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemPartition -->
### Syntax

```powershell
Set-JIMConnectedSystemPartition -ConnectedSystemId <int> -PartitionId <int> [-Selected <bool>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `PartitionId` | `int` | Yes |  | The ID of the partition to change. Accepts pipeline input by property name, `PartitionId` or `Id`. |
| `Selected` | `bool` | No |  | Whether JIM imports objects from the partition. |
| `PassThru` | `switch` | No |  | Returns the partition as changed. |

### Output

With `-PassThru`, the partition as changed; otherwise nothing.

Properties as under [Get-JIMConnectedSystemPartition](#get-jimconnectedsystempartition).
<!-- END GENERATED: Set-JIMConnectedSystemPartition -->

### Examples

```powershell title="Select a partition"
Set-JIMConnectedSystemPartition -ConnectedSystemId 3 -PartitionId 1 -Selected $true
```

```powershell title="Deselect a partition"
Set-JIMConnectedSystemPartition -ConnectedSystemId 3 -PartitionId 1 -Selected $false -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact).

---

## Set-JIMConnectedSystemContainer

Chooses whether, and how far beneath it, JIM imports objects from a container.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemContainer -->
### Syntax

```powershell
Set-JIMConnectedSystemContainer -ConnectedSystemId <int> -ContainerId <int> [-Selected <bool>] [-Excluded <bool>] [-Scope <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ContainerId` | `int` | Yes |  | The ID of the container to change. Accepts pipeline input by property name, `ContainerId` or `Id`. |
| `Selected` | `bool` | No |  | Whether JIM imports objects from the container. |
| `Excluded` | `bool` | No |  | Whether the container is carved out of a selection a container above it made, so its objects are deliberately not imported. A container cannot be both selected and excluded: to replace a selection with an exclusion, give `-Selected` `$false` `-Excluded` `$true` together. Omit it to leave the exclusion as it is. |
| `Scope` | `string` | No |  | How far beneath the container objects are imported from, when it is selected: Subtree imports from the container and every container beneath it; OneLevel only from the container itself, leaving the containers beneath it to be selected in their own right. Omit it to leave the scope as it is. One of `Subtree` or `OneLevel`. |
| `PassThru` | `switch` | No |  | Returns the container as changed. |

### Output

With `-PassThru`, the container as changed; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the container. |
| `Name` | `string` | The name of the container. |
| `ExternalId` | `string` | The container's identifier in the Connected System; on an LDAP directory, its distinguished name. |
| `Description` | `string` | The Connected System's description of the container; empty when it gives none. |
| `Hidden` | `bool` | Whether the Connected System hides the container by default, so its objects are not exposed by accident. |
| `Selected` | `bool` | Whether JIM imports objects from the container. |
| `Excluded` | `bool` | Whether this Container is carved out of a selection an ancestor made, leaving the objects within it deliberately unimported. |
| `Scope` | `string` | How far beneath this Container objects are imported from, when it is selected. One of `Subtree` or `OneLevel`. |
| `ObjectCount` | `int` | How many objects sit directly in the container in the Connected System, as at the last hierarchy import. Empty where the Connector cannot count them, or the container has not been counted. |
| `SubtreeObjectCount` | `int` | ObjectCount plus that of every container beneath it: what a Subtree scope over the container reaches. Empty where the container has not been counted. |
| `PartitionId` | `int` | The ID of the partition the container is in; empty on a Connected System without partitions. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the container belongs to. |
| `ChildContainers` | `object[]` | The containers directly beneath this one, each with its own child containers. Each has `Id`, `Name`, `ExternalId`, `Description`, `Hidden`, `Selected`, `Excluded`, `Scope`, `ObjectCount`, `SubtreeObjectCount`, `PartitionId`, `ConnectedSystemId` and `ChildContainers`. |
<!-- END GENERATED: Set-JIMConnectedSystemContainer -->

### Examples

```powershell title="Select a container"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Selected $true
```

```powershell title="Select a container without its child containers"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Selected $true -Scope OneLevel
```

```powershell title="Widen an already selected container back to its whole subtree"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Scope Subtree
```

```powershell title="Exclude a container from the selection above it"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Excluded $true
```

```powershell title="Replace a selection with an exclusion"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Selected $false -Excluded $true
```

```powershell title="Hand an excluded container back into scope"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Excluded $false
```

```powershell title="Select multiple containers via pipeline"
@(7, 8, 9) | ForEach-Object {
    Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId $_ -Selected $true
}
```

### Notes

- The parent partition must also be selected for container selection to take effect during import operations.
- `Scope` is `Subtree` on a container whose scope has never been set.
- Narrowing a container to `OneLevel` takes the objects beneath it out of scope, exactly as deselecting those containers would. The Connected System Objects already imported from them become obsolete on the next import.
- `Selected` and `Excluded` are mutually exclusive: a container states one thing about itself. A request that would leave both set is rejected with a 400, whether it names both or names one against a stored other, so moving a container from a selection to an exclusion means setting both in the same call.
- Excluding a container takes the objects within it, and within every container beneath it, out of scope. A container beneath an exclusion can be selected in its own right to bring that branch back, because whichever statement is nearest to an object decides its fate. See [Excluding a Container](../connectors/jim-ldap-connector-topology.md#excluding-a-container).
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectedSystemContainerScopeText

Reads a Connected System's Container Scope as text, one statement per line.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemContainerScopeText -->
### Syntax

```powershell
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |

### Output

The Container Scope, one statement per line.

Returns a `string`.
<!-- END GENERATED: Get-JIMConnectedSystemContainerScopeText -->

### Examples

```powershell title="Read the Container Scope"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3
```

```text
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
```

```powershell title="Save the Container Scope to a file"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 | Set-Content ./scope.txt
```

```powershell title="Copy the Container Scope to another Connected System"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 |
    Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 4
```

### Notes

- Every path is the Container's identifier in the Connected System's own terms, which for a directory is its Distinguished Name.
- Copying a scope between Connected Systems requires the target to have discovered the same Containers; a path naming one it has not is refused.

---

## Set-JIMConnectedSystemContainerScopeText

States a Connected System's whole Container Scope as text.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemContainerScopeText -->
### Syntax

```powershell
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId <int> -Text <string> [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `Text` | `string` | Yes |  | The Container Scope to apply. Empty text clears every selection and exclusion. Accepts pipeline input. |
| `PassThru` | `switch` | No |  | Returns the Container Scope now in force, as canonical text. |

### Output

With `-PassThru`, the Container Scope now in force, one statement per line; otherwise nothing.

Returns a `string`.
<!-- END GENERATED: Set-JIMConnectedSystemContainerScopeText -->

### Examples

```powershell title="State a Container Scope with a carve-out and a re-inclusion"
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 -Text @"
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
"@
```

```powershell title="Apply a Container Scope held in a file"
Get-Content ./scope.txt -Raw | Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3
```

```powershell title="Manage only the objects held directly in a container"
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 -Text 'include one-level OU=Corp,DC=example,DC=com' -PassThru
```

### Notes

- The text states the whole of Container Scope rather than a change to it. A Container it does not name states nothing, so omitting a line is how a Container is deselected, and empty text clears the scope entirely.
- Partition selection is left alone, except that naming a Container selects the partition holding it.
- It is applied all-or-nothing. A path naming no Container, a Container named twice, and a statement an ancestor already makes are each refused with the line that caused them, and nothing is changed.
- This is a synchronisation-affecting change: taking a Container out of scope obsoletes the objects imported through it on the next Full Import, and the synchronisation after that disconnects them. Preview it first with [`New-JIMConfigurationChangePreview`](previews.md).
- Supports `ShouldProcess` (High impact), so it prompts before applying unless you pass `-Confirm:$false`.

---

## See also

- [Connected Systems](connected-systems.md): create, change and delete the Connected Systems whose hierarchy these cmdlets manage
- [Connected System Schema](connected-system-schema.md): import the schema, and choose the Object Types imported from the selected containers
