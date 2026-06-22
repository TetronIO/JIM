---
title: Connected Systems
---

# Connected Systems

The Connected Systems cmdlets manage the full lifecycle of Connected Systems in JIM: creating and configuring systems, importing schemas and hierarchy, selecting object types and attributes, browsing connector space objects, and reviewing Pending Exports. Most cmdlets support pipeline input for scripting and automation workflows.

---

## Get-JIMConnectedSystem

Retrieves one or more Connected Systems, their object types, or a deletion impact preview.

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
| `Id` | `int` | Yes (ById, ObjectTypes, DeletionPreview) | | Connected System identifier. Accepts pipeline input by property name. |
| `Name` | `string` | No (List only) | | Filter by name; supports wildcard characters (`*`, `?`) |
| `ObjectTypes` | `switch` | No | `$false` | Returns the object types configured on the Connected System |
| `DeletionPreview` | `switch` | No | `$false` | Returns a deletion impact preview for the Connected System |

### Output

- **List / ById**: Connected System Objects with properties such as `Id`, `Name`, `Description`, `ConnectorDefinitionId`, and configuration state.
- **ObjectTypes**: Object type definitions for the specified Connected System.
- **DeletionPreview**: Deletion impact preview with counts and warnings.

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

```powershell title="Preview the impact of deleting a Connected System"
Get-JIMConnectedSystem -Id 3 -DeletionPreview
```

---

## New-JIMConnectedSystem

Creates a new Connected System.

### Syntax

```powershell
New-JIMConnectedSystem [-Name] <string> -ConnectorDefinitionId <int>
    [-Description <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the Connected System |
| `ConnectorDefinitionId` | `int` | Yes | | Identifier of the connector definition to use |
| `Description` | `string` | No | | Optional description |
| `PassThru` | `switch` | No | `$false` | Returns the created Connected System Object |

### Output

When `-PassThru` is specified, returns the newly created Connected System Object. Otherwise, no output.

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

## Set-JIMConnectedSystem

Updates the configuration of an existing Connected System.

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
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `Name` | `string` | No | | New display name |
| `Description` | `string` | No | | New description |
| `SettingValues` | `hashtable` | No | | Connector-specific settings. Keys are setting IDs; values are hashtables with `stringValue`, `intValue`, or `checkboxValue`. |
| `MaxExportParallelism` | `int` | No | | Maximum number of parallel export threads (1 to 16) |
| `PassThru` | `switch` | No | `$false` | Returns the updated Connected System Object |

### Output

When `-PassThru` is specified, returns the updated Connected System Object. Otherwise, no output.

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

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before changes.

---

## Remove-JIMConnectedSystem

Deletes a Connected System and all its associated data.

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
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted Connected System Object |

### Output

When `-PassThru` is specified, returns the deleted Connected System Object. Otherwise, no output.

### Examples

```powershell title="Delete a Connected System with confirmation"
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
- Small Connected Systems (fewer than 1,000 objects) are deleted immediately. Large systems are queued as a background job; you can monitor progress in the activities log.

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
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the Connected System Object after schema import |

### Output

When `-PassThru` is specified, returns the Connected System Object. Otherwise, no output.

### Examples

```powershell title="Import schema for a Connected System"
Import-JIMConnectedSystemSchema -Id 3
```

```powershell title="Pipeline: create a system, then import its schema"
New-JIMConnectedSystem "LDAP Directory" -ConnectorDefinitionId 1 -PassThru |
    Import-JIMConnectedSystemSchema -PassThru
```

### Notes

- This operation is **destructive**: it replaces the existing schema. Any object type or attribute selections that no longer match the new schema are removed.
- Schema import is required before creating Synchronisation Rules for a Connected System.
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
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the Connected System Object after hierarchy import |

### Output

When `-PassThru` is specified, returns the Connected System Object. Otherwise, no output.

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

Retrieves the object types and their attributes for a Connected System.

### Syntax

```powershell
Get-JIMConnectedSystemObjectType -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Object type definitions with their attributes, selection state, and external ID configuration.

### Examples

```powershell title="Get object types for a Connected System"
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

Updates the configuration of an object type on a Connected System.

### Syntax

```powershell
Set-JIMConnectedSystemObjectType -ConnectedSystemId <int> -ObjectTypeId <int>
    [-Selected <bool>] [-RemoveContributedAttributesOnObsoletion <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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

Updates the selection and external ID configuration of attributes on a Connected System Object Type. Supports updating a single attribute or multiple attributes in bulk.

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
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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

Retrieves the partitions and their containers for a Connected System.

### Syntax

```powershell
Get-JIMConnectedSystemPartition -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Partition objects with their container hierarchy and selection state.

### Examples

```powershell title="Get partitions for a Connected System"
Get-JIMConnectedSystemPartition -ConnectedSystemId 3
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPartition
```

---

## Set-JIMConnectedSystemPartition

Updates the selection state of a partition on a Connected System.

### Syntax

```powershell
Set-JIMConnectedSystemPartition -ConnectedSystemId <int> -PartitionId <int>
    [-Selected <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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

Retrieves connector space objects (CSOs) from a Connected System, with support for paging and attribute value drill-down.

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
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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

## Get-JIMConnectedSystemObjectChangeHistory

Retrieves the change history for a Connected System Object. Each record carries the initiator and Run Profile context, plus the per-attribute value changes, ordered by change time descending (most recent first).

### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid>
    [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid> -All [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes | | Connector space object identifier. Accepts pipeline input by property name. |
| `All` | `switch` | No | `$false` | Automatically paginates through all results. Cannot be used with `-Page`. |
| `Page` | `int` | No | `1` | Page number for paginated results. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `100`. |

### Output

Returns one `PSCustomObject` per change record, including the initiator, Run Profile context, and per-attribute value changes.

### Examples

```powershell title="Get the most recent page of changes"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through all changes for a CSO"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-..." -All
```

```powershell title="Use a larger page size"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-..." -PageSize 100
```

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
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
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

Returns the count of unresolved references in a Connected System's connector space.

### Syntax

```powershell
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

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

Removes all connector space objects (CSOs) and associated data from a Connected System without deleting the system itself. The Connected System configuration, schema, and Synchronisation Rules are preserved.

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
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `KeepChangeHistory` | `switch` | No | `$false` | Preserves change history records; by default, change history is also deleted |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

### Output

None.

### Examples

```powershell title="Clear a Connected System with confirmation"
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
- Removes all CSOs, attribute values, Pending Exports, and deferred references from the Connected System.
- Metaverse Objects are **not** deleted; their links to this Connected System are severed.
- By default, change history is also deleted. Use `-KeepChangeHistory` to retain it for auditing purposes.

---

## Get-JIMPendingExport

Retrieves Pending Export operations queued for a Connected System.

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
| `ConnectedSystemId` | `int` | Yes (List, ListAll) | | Connected System identifier |
| `Id` | `guid` | Yes (ById, AttributeChanges, AttributeChangesAll) | | Pending Export operation identifier |
| `AttributeName` | `string` | No | | Name of a multi-valued attribute to page through its changes |
| `Search` | `string` | No | | Filter results by search term |
| `Page` | `int` | No | `1` | Page number |
| `PageSize` | `int` | No | `50` | Number of results per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all results without paging |

### Output

- **List / ListAll**: Pending Export operations with export type (Add, Update, Delete) and summary of changes.
- **ById**: Detailed view of a single Pending Export, including all attribute changes.
- **AttributeChanges / AttributeChangesAll**: Paged or complete list of changes for a specific multi-valued attribute.

### Examples

```powershell title="List Pending Exports for a Connected System"
Get-JIMPendingExport -ConnectedSystemId 3
```

```powershell title="Search Pending Exports"
Get-JIMPendingExport -ConnectedSystemId 3 -Search "jsmith" -PageSize 25
```

```powershell title="Get all Pending Exports"
Get-JIMPendingExport -ConnectedSystemId 3 -All
```

```powershell title="View details of a specific Pending Export"
Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through member additions on a group export"
Get-JIMPendingExport -Id "a1b2c3d4-..." -AttributeName "member" -Page 1 -PageSize 100
```

### Notes

- For large multi-valued attribute changes (e.g. adding hundreds of members to a group), use the `-AttributeName` parameter to page through the individual changes rather than loading them all at once.

---

## Get-JIMConnectedSystemDeletionPreview

Retrieves a preview of the impact of deleting a Connected System, including counts of affected objects and warnings.

### Syntax

```powershell
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

A deletion impact preview object with counts of connector space objects, Pending Exports, Synchronisation Rules, and other dependent data that would be removed.

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

- [Connected Systems](../configuration/connected-systems.md): what Connected Systems are, the connector space, partitions and containers, and common workflows
- [Run Profiles](run-profiles.md): execute import, sync, and export operations on Connected Systems
- [Synchronisation Rules](synchronisation-rules.md): define attribute mappings and scoping for Connected System synchronisation
- [Connection](connection.md): establish a session before using these cmdlets
