---
title: Connected Systems
---

# Connected Systems

The connected systems cmdlets manage the full lifecycle of connected systems in JIM: creating and configuring systems, importing schemas and hierarchy, selecting object types and attributes, browsing connector space objects, and reviewing pending exports. Most cmdlets support pipeline input for scripting and automation workflows.

---

## Get-JIMConnectedSystem

Retrieves one or more connected systems, their object types, or a deletion impact preview.

### Syntax

```powershell
# List (default)
Get-JIMConnectedSystem [-Name <string>]

# ById
Get-JIMConnectedSystem -Id <int>

# ObjectTypes
Get-JIMConnectedSystem -Id <int> -ObjectTypes

# DeletionPreview
Get-JIMConnectedSystem -Id <int> -DeletionPreview
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById, ObjectTypes, DeletionPreview) | | Connected system identifier. Accepts pipeline input by property name. |
| `Name` | `string` | No (List only) | | Filter by name; supports wildcard characters (`*`, `?`) |
| `ObjectTypes` | `switch` | No | `$false` | Returns the object types configured on the connected system |
| `DeletionPreview` | `switch` | No | `$false` | Returns a deletion impact preview for the connected system |

### Output

- **List / ById**: Connected system objects with properties such as `Id`, `Name`, `Description`, `ConnectorDefinitionId`, and configuration state.
- **ObjectTypes**: Object type definitions for the specified connected system.
- **DeletionPreview**: Deletion impact preview with counts and warnings.

### Examples

```powershell title="List all connected systems"
Get-JIMConnectedSystem
```

```powershell title="Filter by name using wildcards"
Get-JIMConnectedSystem -Name "HR*"
```

```powershell title="Get a specific connected system by ID"
Get-JIMConnectedSystem -Id 3
```

```powershell title="Retrieve object types for a connected system"
Get-JIMConnectedSystem -Id 3 -ObjectTypes
```

```powershell title="Preview the impact of deleting a connected system"
Get-JIMConnectedSystem -Id 3 -DeletionPreview
```

---

## New-JIMConnectedSystem

Creates a new connected system.

### Syntax

```powershell
New-JIMConnectedSystem [-Name] <string> -ConnectorDefinitionId <int>
    [-Description <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the connected system |
| `ConnectorDefinitionId` | `int` | Yes | | Identifier of the connector definition to use |
| `Description` | `string` | No | | Optional description |
| `PassThru` | `switch` | No | `$false` | Returns the created connected system object |

### Output

When `-PassThru` is specified, returns the newly created connected system object. Otherwise, no output.

### Examples

```powershell title="Create a connected system"
New-JIMConnectedSystem -Name "Active Directory" -ConnectorDefinitionId 1
```

```powershell title="Create and capture the result"
$cs = New-JIMConnectedSystem "HR Database" -ConnectorDefinitionId 2 -Description "Primary HR source" -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before creation.

---

## Set-JIMConnectedSystem

Updates the configuration of an existing connected system.

### Syntax

```powershell
# ById (default)
Set-JIMConnectedSystem -Id <int> [-Name <string>] [-Description <string>]
    [-SettingValues <hashtable>] [-MaxExportParallelism <int>] [-PassThru]

# ByInputObject
Set-JIMConnectedSystem -InputObject <PSCustomObject> [-Name <string>]
    [-Description <string>] [-SettingValues <hashtable>]
    [-MaxExportParallelism <int>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected system identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected system object from the pipeline |
| `Name` | `string` | No | | New display name |
| `Description` | `string` | No | | New description |
| `SettingValues` | `hashtable` | No | | Connector-specific settings. Keys are setting IDs; values are hashtables with `stringValue`, `intValue`, or `checkboxValue`. |
| `MaxExportParallelism` | `int` | No | | Maximum number of parallel export threads (1 to 16) |
| `PassThru` | `switch` | No | `$false` | Returns the updated connected system object |

### Output

When `-PassThru` is specified, returns the updated connected system object. Otherwise, no output.

### Examples

```powershell title="Rename a connected system"
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

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before changes.

---

## Remove-JIMConnectedSystem

Deletes a connected system and all its associated data.

### Syntax

```powershell
# ById (default)
Remove-JIMConnectedSystem -Id <int> [-Force] [-PassThru]

# ByInputObject
Remove-JIMConnectedSystem -InputObject <PSCustomObject> [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected system identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected system object from the pipeline |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted connected system object |

### Output

When `-PassThru` is specified, returns the deleted connected system object. Otherwise, no output.

### Examples

```powershell title="Delete a connected system with confirmation"
Remove-JIMConnectedSystem -Id 3
```

```powershell title="Delete without confirmation"
Remove-JIMConnectedSystem -Id 3 -Force
```

```powershell title="Pipeline deletion"
Get-JIMConnectedSystem -Name "Decommissioned*" | Remove-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Small connected systems (fewer than 1,000 objects) are deleted immediately. Large systems are queued as a background job; you can monitor progress in the activities log.

---

## Import-JIMConnectedSystemSchema

Imports (or re-imports) the schema from the connected data source. This discovers available object types and attributes.

### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemSchema -Id <int> [-PassThru]

# ByInputObject
Import-JIMConnectedSystemSchema -InputObject <PSCustomObject> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected system identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected system object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the connected system object after schema import |

### Output

When `-PassThru` is specified, returns the connected system object. Otherwise, no output.

### Examples

```powershell title="Import schema for a connected system"
Import-JIMConnectedSystemSchema -Id 3
```

```powershell title="Pipeline: create a system, then import its schema"
New-JIMConnectedSystem "LDAP Directory" -ConnectorDefinitionId 1 -PassThru |
    Import-JIMConnectedSystemSchema -PassThru
```

### Notes

- This operation is **destructive**: it replaces the existing schema. Any object type or attribute selections that no longer match the new schema are removed.
- Schema import is required before creating sync rules for a connected system.
- Supports `ShouldProcess` (Medium impact).

---

## Import-JIMConnectedSystemHierarchy

Imports (or re-imports) the partition and container hierarchy from the connected data source.

### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemHierarchy -Id <int> [-PassThru]

# ByInputObject
Import-JIMConnectedSystemHierarchy -InputObject <PSCustomObject> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected system identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected system object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the connected system object after hierarchy import |

### Output

When `-PassThru` is specified, returns the connected system object. Otherwise, no output.

### Examples

```powershell title="Import hierarchy"
Import-JIMConnectedSystemHierarchy -Id 3
```

```powershell title="Pipeline: import schema, then hierarchy"
Get-JIMConnectedSystem -Id 3 |
    Import-JIMConnectedSystemSchema |
    Import-JIMConnectedSystemHierarchy -PassThru
```

### Notes

- This operation is **destructive**: it replaces the existing partition and container configuration.
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectorDefinition

Retrieves available connector definitions, including their settings and capabilities.

### Syntax

```powershell
# List all (default)
Get-JIMConnectorDefinition

# By ID
Get-JIMConnectorDefinition -Id <int>

# By name (exact match)
Get-JIMConnectorDefinition -Name <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connector definition identifier. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName) | | Connector definition name. Must be an exact match. |

### Output

Connector definition objects including name, description, available settings, and supported capabilities (e.g. full import, delta import, export, hierarchy).

### Examples

```powershell title="List all connector definitions"
Get-JIMConnectorDefinition
```

```powershell title="Get a connector definition by name"
Get-JIMConnectorDefinition -Name "CSV File"
```

```powershell title="Get a specific connector definition by ID"
Get-JIMConnectorDefinition -Id 1
```

```powershell title="Find connectors that support delta import"
Get-JIMConnectorDefinition | Where-Object { $_.Capabilities -contains "DeltaImport" }
```

---

## Get-JIMConnectedSystemObjectType

Retrieves the object types and their attributes for a connected system.

### Syntax

```powershell
Get-JIMConnectedSystemObjectType -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Object type definitions with their attributes, selection state, and external ID configuration.

### Examples

```powershell title="Get object types for a connected system"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemObjectType
```

```powershell title="List selected object types only"
Get-JIMConnectedSystem -Id 3 |
    Get-JIMConnectedSystemObjectType |
    Where-Object { $_.Selected }
```

---

## Set-JIMConnectedSystemObjectType

Updates the configuration of an object type on a connected system.

### Syntax

```powershell
Set-JIMConnectedSystemObjectType -ConnectedSystemId <int> -ObjectTypeId <int>
    [-Selected <bool>] [-RemoveContributedAttributesOnObsoletion <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `ObjectTypeId` | `int` | Yes | | Object type identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this object type is selected for synchronisation |
| `RemoveContributedAttributesOnObsoletion` | `bool` | No | | Whether to remove attributes contributed by this system when an object becomes obsolete |
| `PassThru` | `switch` | No | `$false` | Returns the updated object type |

### Output

When `-PassThru` is specified, returns the updated object type. Otherwise, no output.

### Examples

```powershell title="Select an object type for synchronisation"
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 1 -Selected $true
```

```powershell title="Deselect an object type"
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 2 -Selected $false
```

### Notes

- Supports `ShouldProcess` (Medium impact).

---

## Set-JIMConnectedSystemAttribute

Updates the selection and external ID configuration of attributes on a connected system object type. Supports updating a single attribute or multiple attributes in bulk.

### Syntax

```powershell
# Single (default)
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int>
    -AttributeId <int> [-Selected <bool>] [-IsExternalId <bool>]
    [-IsSecondaryExternalId <bool>] [-PassThru]

# Bulk
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int>
    -AttributeUpdates <hashtable> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `ObjectTypeId` | `int` | Yes | | Object type identifier |
| `AttributeId` | `int` | Yes (Single) | | Attribute identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No (Single) | | Whether this attribute is selected for synchronisation |
| `IsExternalId` | `bool` | No (Single) | | Whether this attribute is the primary external identifier |
| `IsSecondaryExternalId` | `bool` | No (Single) | | Whether this attribute is a secondary external identifier |
| `AttributeUpdates` | `hashtable` | Yes (Bulk) | | Hashtable of updates. Keys are attribute IDs; values are hashtables with `selected`, `isExternalId`, and/or `isSecondaryExternalId`. |
| `PassThru` | `switch` | No | `$false` | Returns the updated attribute(s) |

### Output

When `-PassThru` is specified, returns the updated attribute object(s). Otherwise, no output.

### Examples

```powershell title="Select a single attribute"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 5 -Selected $true
```

```powershell title="Mark an attribute as the primary external ID"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 10 -IsExternalId $true
```

```powershell title="Bulk-update multiple attributes"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeUpdates @{
    5  = @{ selected = $true }
    10 = @{ selected = $true; isExternalId = $true }
    12 = @{ selected = $true; isSecondaryExternalId = $true }
}
```

### Notes

- Supports `ShouldProcess` (Medium impact).
- Only one attribute per object type can be the primary external ID. Setting `IsExternalId` on an attribute automatically clears it from the previous primary.

---

## Get-JIMConnectedSystemPartition

Retrieves the partitions and their containers for a connected system.

### Syntax

```powershell
Get-JIMConnectedSystemPartition -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Partition objects with their container hierarchy and selection state.

### Examples

```powershell title="Get partitions for a connected system"
Get-JIMConnectedSystemPartition -ConnectedSystemId 3
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPartition
```

---

## Set-JIMConnectedSystemPartition

Updates the selection state of a partition on a connected system.

### Syntax

```powershell
Set-JIMConnectedSystemPartition -ConnectedSystemId <int> -PartitionId <int>
    [-Selected <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `PartitionId` | `int` | Yes | | Partition identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this partition is selected for synchronisation |
| `PassThru` | `switch` | No | `$false` | Returns the updated partition |

### Output

When `-PassThru` is specified, returns the updated partition. Otherwise, no output.

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

Updates the selection state of a container within a partition.

### Syntax

```powershell
Set-JIMConnectedSystemContainer -ConnectedSystemId <int> -ContainerId <int>
    [-Selected <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `ContainerId` | `int` | Yes | | Container identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this container is selected for synchronisation |
| `PassThru` | `switch` | No | `$false` | Returns the updated container |

### Output

When `-PassThru` is specified, returns the updated container. Otherwise, no output.

### Examples

```powershell title="Select a container"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Selected $true
```

```powershell title="Select multiple containers via pipeline"
@(7, 8, 9) | ForEach-Object {
    Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId $_ -Selected $true
}
```

### Notes

- The parent partition must also be selected for container selection to take effect during import operations.
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectedSystemObject

Retrieves connector space objects (CSOs) from a connected system, with support for paging and attribute value drill-down.

### Syntax

```powershell
# ById (default)
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>

# AttributeValues
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>
    -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# AttributeValuesAll
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>
    -AttributeName <string> [-Search <string>] -All
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `Id` | `guid` | Yes | | Connector space object identifier |
| `AttributeName` | `string` | No | | Name of a multi-valued attribute to page through |
| `Search` | `string` | No | | Filter attribute values by search term |
| `Page` | `int` | No | `1` | Page number for attribute value results |
| `PageSize` | `int` | No | `50` | Number of attribute values per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all attribute values without paging |

### Output

- **ById**: A connector space object with its attributes and current values.
- **AttributeValues / AttributeValuesAll**: Paged or complete list of values for the specified multi-valued attribute.

### Examples

```powershell title="Get a specific connector space object"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through a multi-valued attribute"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-..." -AttributeName "member" -Page 2 -PageSize 25
```

```powershell title="Get all values of a multi-valued attribute"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-..." -AttributeName "member" -All
```

### Notes

- Multi-valued attributes are capped at 10 values in the default detail response. Use the `-AttributeName` parameter to page through all values of a large multi-valued attribute.

---

## Get-JIMConnectedSystemObjectAttributeValue

Pages through the values of a multi-valued attribute on a connector space object. This is the dedicated cmdlet for browsing large multi-valued attributes.

### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid>
    -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid>
    -AttributeName <string> [-Search <string>] -All
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier |
| `CsoId` | `guid` | Yes | | Connector space object identifier |
| `AttributeName` | `string` | Yes | | Name of the multi-valued attribute |
| `Search` | `string` | No | | Filter values by search term |
| `Page` | `int` | No | `1` | Page number |
| `PageSize` | `int` | No | `50` | Number of values per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all values without paging |

### Output

Attribute values for the specified multi-valued attribute, with paging metadata when not using `-All`.

### Examples

```powershell title="Page through group members"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -AttributeName "member" -Page 1 -PageSize 100
```

```powershell title="Search within attribute values"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-..." -AttributeName "member" -Search "admin"
```

```powershell title="Get all values at once"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-..." -AttributeName "proxyAddresses" -All
```

---

## Get-JIMConnectedSystemUnresolvedReferenceCount

Returns the count of unresolved references in a connected system's connector space.

### Syntax

```powershell
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

An integer representing the number of unresolved references.

### Examples

```powershell title="Check for unresolved references"
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 3
```

```powershell title="Pipeline check across all systems"
Get-JIMConnectedSystem | ForEach-Object {
    [PSCustomObject]@{
        Name  = $_.Name
        Unresolved = Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId $_.Id
    }
} | Where-Object { $_.Unresolved -gt 0 }
```

### Notes

- A non-zero count indicates data integrity issues in the connector space. This commonly occurs after a partial import. Running a full import typically resolves outstanding references.

---

## Clear-JIMConnectedSystem

Removes all connector space objects (CSOs) and associated data from a connected system without deleting the system itself. The connected system configuration, schema, and sync rules are preserved.

### Syntax

```powershell
# ById (default)
Clear-JIMConnectedSystem -Id <int> [-KeepChangeHistory] [-Force]

# ByInputObject
Clear-JIMConnectedSystem -InputObject <PSCustomObject> [-KeepChangeHistory] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected system identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected system object from the pipeline |
| `KeepChangeHistory` | `switch` | No | `$false` | Preserves change history records; by default, change history is also deleted |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

### Output

None.

### Examples

```powershell title="Clear a connected system with confirmation"
Clear-JIMConnectedSystem -Id 3
```

```powershell title="Clear without confirmation, keeping history"
Clear-JIMConnectedSystem -Id 3 -KeepChangeHistory -Force
```

```powershell title="Pipeline: clear a system by name"
Get-JIMConnectedSystem -Name "Staging AD" | Clear-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removes all CSOs, attribute values, pending exports, and deferred references from the connected system.
- Metaverse objects are **not** deleted; their links to this connected system are severed.
- By default, change history is also deleted. Use `-KeepChangeHistory` to retain it for auditing purposes.

---

## Get-JIMPendingExport

Retrieves pending export operations queued for a connected system.

### Syntax

```powershell
# List (default)
Get-JIMPendingExport -ConnectedSystemId <int> [-Search <string>]
    [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMPendingExport -ConnectedSystemId <int> [-Search <string>] -All

# ById
Get-JIMPendingExport -Id <guid>

# AttributeChanges
Get-JIMPendingExport -Id <guid> -AttributeName <string>
    [-Search <string>] [-Page <int>] [-PageSize <int>]

# AttributeChangesAll
Get-JIMPendingExport -Id <guid> -AttributeName <string> [-Search <string>] -All
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (List, ListAll) | | Connected system identifier |
| `Id` | `guid` | Yes (ById, AttributeChanges, AttributeChangesAll) | | Pending export operation identifier |
| `AttributeName` | `string` | No | | Name of a multi-valued attribute to page through its changes |
| `Search` | `string` | No | | Filter results by search term |
| `Page` | `int` | No | `1` | Page number |
| `PageSize` | `int` | No | `50` | Number of results per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all results without paging |

### Output

- **List / ListAll**: Pending export operations with export type (Add, Update, Delete) and summary of changes.
- **ById**: Detailed view of a single pending export, including all attribute changes.
- **AttributeChanges / AttributeChangesAll**: Paged or complete list of changes for a specific multi-valued attribute.

### Examples

```powershell title="List pending exports for a connected system"
Get-JIMPendingExport -ConnectedSystemId 3
```

```powershell title="Search pending exports"
Get-JIMPendingExport -ConnectedSystemId 3 -Search "jsmith" -PageSize 25
```

```powershell title="Get all pending exports"
Get-JIMPendingExport -ConnectedSystemId 3 -All
```

```powershell title="View details of a specific pending export"
Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through member additions on a group export"
Get-JIMPendingExport -Id "a1b2c3d4-..." -AttributeName "member" -Page 1 -PageSize 100
```

### Notes

- For large multi-valued attribute changes (e.g. adding hundreds of members to a group), use the `-AttributeName` parameter to page through the individual changes rather than loading them all at once.

---

## Get-JIMConnectedSystemDeletionPreview

Retrieves a preview of the impact of deleting a connected system, including counts of affected objects and warnings.

### Syntax

```powershell
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected system identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

A deletion impact preview object with counts of connector space objects, pending exports, sync rules, and other dependent data that would be removed.

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
        CSOCount = $preview.ConnectorSpaceObjectCount
        SyncRules = $preview.SyncRuleCount
    }
}
```

---

## See also

- [API: Connected Systems](../api/connected-systems/index.md): REST API reference for connected system endpoints
- [Run Profiles](run-profiles.md): execute import, sync, and export operations on connected systems
- [Sync Rules](sync-rules.md): define attribute mappings and scoping for connected system synchronisation
- [Connection](connection.md): establish a session before using these cmdlets
