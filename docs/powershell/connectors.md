---
title: Connectors
description: "The cmdlet that lists the Connectors JIM has, and reads one Connector's settings and capabilities."
type: reference
audience: automation-author
---

# Connectors

The Connector cmdlet reads the Connectors a Connected System can be created from: which ones JIM has, and for
one Connector, the settings a Connected System using it is configured with and what the Connector can do. Use it
to find the Connector ID that `New-JIMConnectedSystem` needs.

---

## Get-JIMConnectorDefinition

Retrieves available connector definitions, including their settings and capabilities.

<!-- BEGIN GENERATED: Get-JIMConnectorDefinition -->
### Syntax

```powershell
# List (default)
Get-JIMConnectorDefinition

# ById
Get-JIMConnectorDefinition -Id <int>

# ByName
Get-JIMConnectorDefinition -Name <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connector to get. Accepts pipeline input, by value or by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Connector to get, matched exactly. |

### Output

The Connectors, or with `-Id` or `-Name` the one Connector, with its settings and what it supports.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Connector. |
| `Name` | `string` | The name of the Connector. |
| `Description` | `string` | What the Connector connects to; empty when none is given. |
| `BuiltIn` | `bool` | Whether the Connector ships with JIM. |
| `Created` | `datetime` | When the Connector was added (UTC). |
| `LastUpdated` | `datetime` | When the Connector was last changed (UTC); empty if it never has been. |
| `Files` | `int` | How many files make up the Connector. |
| `Versions` | `string` | The versions of the Connector's files, comma-separated. |
| `InUse` | `bool` | Whether any Connected System uses the Connector. |

**With `-Id` or `-Name`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Connector. |
| `Name` | `string` | The name of the Connector, such as "JIM LDAP Connector". |
| `Description` | `string` | What the Connector connects to; empty when none is given. |
| `Url` | `string` | Where to read more about the Connector; empty when none is given. |
| `BuiltIn` | `bool` | Whether the Connector ships with JIM, rather than being supplied by someone else. |
| `Created` | `datetime` | When the Connector was added (UTC). |
| `CreatedByName` | `string` | Who added the Connector. |
| `LastUpdated` | `datetime` | When the Connector was last changed (UTC); empty if it never has been. |
| `LastUpdatedByName` | `string` | Who last changed the Connector. |
| `Settings` | `object[]` | The settings an administrator gives a Connected System that uses the Connector. Each has `Id`, `Name`, `Description`, `Category`, `Type`, `DefaultCheckboxValue`, `DefaultStringValue`, `DefaultIntValue`, `DropDownValues`, `Required`, `RequiredGroup`, `RequiredGroupCardinality`, `RequiredWhenSetting` and `RequiredWhenValue`. |
| `Files` | `object[]` | The files that make up the Connector. Their contents are never returned. Each has `Id`, `Filename`, `Version`, `FileSizeBytes`, `ImplementsIConnector`, `ImplementsICapabilities`, `ImplementsISchema`, `ImplementsISettings`, `ImplementsIExportUsingCalls`, `ImplementsIExportUsingFiles`, `ImplementsIImportUsingCalls` and `ImplementsIImportUsingFiles`. |
| `SupportsFullImport` | `bool` | Whether the Connector can run a Full Import. |
| `SupportsDeltaImport` | `bool` | Whether the Connector can run a Delta Import. |
| `SupportsExport` | `bool` | Whether the Connector can export changes to the Connected System. |
| `SupportsPartitions` | `bool` | Whether the Connector reads its Connected System as partitions. |
| `SupportsPartitionContainers` | `bool` | Whether the Connector reads the containers in each partition, so you can choose which ones JIM imports from. |
| `SupportsSecondaryExternalId` | `bool` | Whether the Connector gives each object a second identifier, such as an LDAP Distinguished Name, beside its unchanging external ID. |
| `SupportsUserSelectedExternalId` | `bool` | Whether the administrator chooses which attribute is the external ID. |
| `SupportsUserSelectedAttributeTypes` | `bool` | Whether the administrator can change the data type the Connector found for an attribute. |
| `SupportsUserSelectedAttributePlurality` | `bool` | Whether the administrator can say whether an attribute holds one value or several, where the Connected System's schema cannot state it (a delimited file). |
| `SupportsAutoConfirmExport` | `bool` | Whether the Connector confirms an export as soon as it is made, rather than at the next import. |
| `SupportsParallelExport` | `bool` | Whether the Connector can export batches in parallel. |
| `SupportsPaging` | `bool` | Whether the Connector imports and exports a page at a time. |
| `SupportsFilePaths` | `bool` | Whether the Connector imports from or exports to files. |
| `SupportsPasswordSet` | `bool` | Whether the Connector can set passwords on objects in the Connected System. |
| `SupportsPasswordPolicyDiscovery` | `bool` | Whether the Connector can read the password policy the Connected System enforces. |
| `SupportsUniquenessProbe` | `bool` | Whether the Connector can search the Connected System for values already in use, so that generating a unique value can avoid one held by an account JIM has never imported. |
| `SupportsUniquenessRejectionClassification` | `bool` | Whether the Connector reports an export refused because a value is already in use as that, rather than as an ordinary export failure, so that JIM can generate another value. |
| `ExternalIdsUniqueAcrossObjectTypes` | `bool` | Whether an external ID identifies one object across every Object Type of a Connected System using the Connector. When true, an import refuses an object that has changed Object Type, rather than treating it as a deletion and a new object, unless the Full Import Run Profile allows Object Type changes. When false, a Run Profile's Allow Object Type Changes setting has no effect. |
| `SchemaStandard` | `string` | Which standard's attribute names the Connector's schema follows, such as Scim or Ldap; NotSet when it follows none. JIM uses it to suggest Standard Mappings. |
<!-- END GENERATED: Get-JIMConnectorDefinition -->

### Examples

```powershell title="List all connector definitions"
Get-JIMConnectorDefinition
```

```powershell title="Get a connector definition by name"
Get-JIMConnectorDefinition -Name "JIM File Connector"
```

```powershell title="Get a specific connector definition by ID"
Get-JIMConnectorDefinition -Id 1
```

```powershell title="Find connectors that support delta import"
# The list form returns headers, which carry no capability flags; fetch each
# definition by ID to see what it supports.
Get-JIMConnectorDefinition |
    ForEach-Object { Get-JIMConnectorDefinition -Id $_.Id } |
    Where-Object { $_.SupportsDeltaImport }
```

---

## See also

- [Connected Systems](connected-systems.md): create a Connected System from a Connector, and configure it
- [Connectors](../connectors/index.md): what each Connector connects to, and its settings
