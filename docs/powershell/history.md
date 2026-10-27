---
title: History
description: "The cmdlets that query Configuration Change History, deleted objects and object changes, page a large change's values, and clean up change history."
type: reference
audience: automation-author
---

# History

Cmdlets for querying Configuration Change History, querying deleted objects, searching changes to Metaverse Objects, reading every value a large change recorded, and managing change history retention. These cmdlets provide access to the configuration audit trail, the audit trail for deleted objects and the history of every Metaverse Object, and let you control how long change history records are kept.

---

## :material-history: Get-JIMConfigurationChangeHistory

Retrieves the recorded configuration changes for a Synchronisation Rule, Connected System, Schedule, Service Setting, Metaverse Object Type, Metaverse Attribute, Trusted Certificate, API Key, Role, Predefined Search, Connector Definition, Example Data Set, or Example Data Template. Every create, update, and delete is captured as a complete, versioned snapshot carried on its Activity, so you can see exactly what changed, when, and who changed it. Three retrieval modes are supported: a paged summary list (default), a single version with its diff against the previous version (`-Version`), and a comparison of any two versions (`-CompareFrom` / `-CompareTo`). Sensitive values (for example encrypted Connected System settings) are never returned; a changed secret is reported only as changed, never by value. An API Key's history stores only its metadata and Role assignments; the key secret is never returned in any form, not even as a hash. A Role's history covers both its definition and its static membership, so each member add or remove appears as its own version. A Predefined Search's history also covers its criteria groups and criteria: each add, edit, or removal rolls up into a version on the owning search. A Connector Definition's history covers its capabilities, setting definitions, and files, so a capability or setting shipped in an updated connector appears as its own version; a file's contents are fingerprinted by hash, never returned. An Example Data Set's history records only its metadata and value count (never the individual values), and an Example Data Template's history covers its object-type and attribute configuration; a template's data-generation runs are a separate operational activity, not part of its configuration history.

<!-- BEGIN GENERATED: Get-JIMConfigurationChangeHistory -->
### Syntax

```powershell
# Page (default)
Get-JIMConfigurationChangeHistory -Type <string> -Id <string> [-Page <int>] [-PageSize <int>]

# All
Get-JIMConfigurationChangeHistory -Type <string> -Id <string> -All [-Force] [-PageSize <int>]

# Version
Get-JIMConfigurationChangeHistory -Type <string> -Id <string> -Version <int> [-AsDiff] [-Raw]

# Compare
Get-JIMConfigurationChangeHistory -Type <string> -Id <string> -CompareFrom <int> -CompareTo <int> [-AsDiff] [-Raw]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Type` | `string` | Yes |  | The kind of configuration object. One of `SynchronisationRule`, `ConnectedSystem`, `Schedule`, `ServiceSetting`, `MetaverseObjectType`, `MetaverseAttribute`, `TrustedCertificate`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataTemplate` or `ExampleDataSet`. |
| `Id` | `string` | Yes |  | The ID of the configuration object: an integer for most kinds; a GUID for a Schedule, Trusted Certificate or API Key; or the setting's key for a Service Setting, such as History.RetentionPeriod. Accepts pipeline input by property name. |
| `All` | `switch` | Yes (All) |  | Returns every change. Stops after 1,000 pages with a warning unless `-Force` is given. |
| `Force` | `switch` | No (All) |  | Fetches beyond the `-All` page ceiling. |
| `Page` | `int` | No (Page) | `1` | The page of changes to return. At least 1. |
| `PageSize` | `int` | No (Page, All) | `50` | How many changes to return per page. From 1 to 100. |
| `Version` | `int` | Yes (Version) |  | Returns the change that produced this version, with the object's redacted snapshot and the diff against the previous version. At least 1. |
| `CompareFrom` | `int` | Yes (Compare) |  | The earlier of the two versions to compare. At least 1. |
| `CompareTo` | `int` | Yes (Compare) |  | The later of the two versions to compare. At least 1. |
| `AsDiff` | `switch` | No (Version, Compare) |  | Renders the change as a coloured, git-style diff instead of returning the structured object. |
| `Raw` | `switch` | No (Version, Compare) |  | Returns the structured object, which is the default; the switch only makes that explicit. |

### Output

The list returns one record per change, newest first. `-Version` returns that change in full, and `-CompareFrom` with `-CompareTo` the diff between two versions; with `-AsDiff`, either is rendered as lines of text instead. A secret is never returned: a changed secret is reported only as changed.

**By default, or with `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity that recorded the change. |
| `Version` | `int` | The object's version this change produced, counting from 1 for its creation. |
| `Operation` | `string` | What the change did: Create, Update or Delete. One of `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate`, `Preview`, `SetPassword`, `SchemaRefreshRemoval`, `RetryPasswordDelivery`, `CancelPasswordDelivery`, `DiscoverAuxiliaryClasses`, `RecallAttributeValues`, `Deprovision`, `RestartGeneratedValues`, `AllowGeneratedValueRename`, `RetryGeneratedValue` or `Receive`. |
| `InitiatedByType` | `string` | What made the change: a User, an API key, or JIM itself (System). One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The security principal's id, where one applies (User / API key); empty for System or unattributed changes. |
| `InitiatedByName` | `string` | The name of whoever made the change, as it was when they made it. |
| `When` | `datetime` | When the change was made (UTC). |
| `Reason` | `string` | The reason given for the change, if one was. |
| `ConfigurationTestGateNote` | `string` | Set when the change stopped configuration test cases passing and was saved anyway, or their new results were accepted: the cases, by name and status, never their values. |
| `Summary` | `string` | A short description of what the change touched, for example "Scope, Attribute Flow". |
| `Diff` | `object` | The structured diff of this version against its predecessor. For the first version (no predecessor) it shows the whole object as created. Empty only when the version carries no deserialisable snapshot. It has `ObjectType`, `ObjectId`, `ObjectGuidId`, `ObjectName`, `OldVersion`, `NewVersion`, `Root`, `AddedCount`, `RemovedCount`, `ModifiedCount` and `HasChanges`. |

**With `-Version`**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity that recorded the change. |
| `Version` | `int` | The object's version this change produced, counting from 1 for its creation. |
| `Operation` | `string` | What the change did: Create, Update or Delete. One of `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate`, `Preview`, `SetPassword`, `SchemaRefreshRemoval`, `RetryPasswordDelivery`, `CancelPasswordDelivery`, `DiscoverAuxiliaryClasses`, `RecallAttributeValues`, `Deprovision`, `RestartGeneratedValues`, `AllowGeneratedValueRename`, `RetryGeneratedValue` or `Receive`. |
| `InitiatedByType` | `string` | What made the change: a User, an API key, or JIM itself (System). One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedByName` | `string` | The name of whoever made the change, as it was when they made it. |
| `When` | `datetime` | When the change was made (UTC). |
| `Reason` | `string` | The reason given for the change, if one was. |
| `Snapshot` | `object` | The complete, redacted snapshot of the object at this version. It has `SchemaVersion`, `ObjectType`, `ObjectId`, `ObjectGuidId`, `ObjectKey`, `ObjectName` and `Root`. |
| `Diff` | `object` | The structured diff against the immediately preceding version (every node added, for the first version). It has `ObjectType`, `ObjectId`, `ObjectGuidId`, `ObjectName`, `OldVersion`, `NewVersion`, `Root`, `AddedCount`, `RemovedCount`, `ModifiedCount` and `HasChanges`. |

**With `-CompareFrom` and `-CompareTo`**:

| Property | Type | Description |
|----------|------|-------------|
| `ObjectType` | `string` | The configuration object type, e.g. "SynchronisationRule" or "ConnectedSystem". |
| `ObjectId` | `int` | The configuration object's numeric ID; zero for an object identified by a GUID (see `ObjectGuidId`). |
| `ObjectGuidId` | `guid` | The configuration object's GUID, for an object identified by one (a Schedule, for example); empty otherwise. |
| `ObjectName` | `string` | The display name of the configuration object (from the newer snapshot). |
| `OldVersion` | `int` | The per-object version of the older snapshot, or empty when comparing against nothing (a creation). |
| `NewVersion` | `int` | The per-object version of the newer snapshot, when known. |
| `Root` | `object` | The root node of the diff tree. It has `Key`, `Label`, `NodeType`, `ChangeType`, `OldValue`, `NewValue`, `OldDisplayValue`, `NewDisplayValue`, `IsSecret`, `ItemId`, `ItemGuidId` and `Children`. |
| `AddedCount` | `int` | Count of added items and scalars. |
| `RemovedCount` | `int` | Count of removed items and scalars. |
| `ModifiedCount` | `int` | Count of modified scalars. |
| `HasChanges` | `bool` | True when anything changed between the two snapshots. |

**With `-AsDiff`**: a `string`.
<!-- END GENERATED: Get-JIMConfigurationChangeHistory -->

### Examples

```powershell title="List the most recent changes for a Synchronisation Rule"
Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5
```

```powershell title="Return every recorded change for a Connected System"
Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -All
```

```powershell title="List the recorded changes for a Schedule (GUID-keyed)"
Get-JIMSchedule -Name "Nightly Sync" | Get-JIMConfigurationChangeHistory -Type Schedule
```

```powershell title="Show one version as a git-style coloured diff"
Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -Version 7 -AsDiff
```

```powershell title="Pipe a Synchronisation Rule in and show its latest change"
Get-JIMSyncRule -Name "HR Inbound" |
    Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Version 7 -AsDiff
```

```powershell title="Compare two versions"
Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5 -CompareFrom 6 -CompareTo 8 -AsDiff
```

```powershell title="List the recorded changes for a Service Setting (string-keyed)"
Get-JIMConfigurationChangeHistory -Type ServiceSetting -Id 'History.RetentionPeriod'
```

```powershell title="Pipe a Metaverse Attribute in and list its recorded changes"
Get-JIMMetaverseAttribute -Name 'Email' | Get-JIMConfigurationChangeHistory -Type MetaverseAttribute
```

```powershell title="List the recorded changes for a Trusted Certificate (GUID-keyed)"
Get-JIMCertificate | Get-JIMConfigurationChangeHistory -Type TrustedCertificate
```

```powershell title="List the recorded changes for an API Key (GUID-keyed)"
Get-JIMApiKey | Get-JIMConfigurationChangeHistory -Type ApiKey
```

```powershell title="List the recorded changes for a Role (int-keyed), covering its definition and membership"
Get-JIMRole -Name "Administrator" | Get-JIMConfigurationChangeHistory -Type Role
```

```powershell title="List the recorded changes for a Predefined Search (int-keyed), covering the search and its criteria"
Get-JIMPredefinedSearch -Uri people | Get-JIMConfigurationChangeHistory -Type PredefinedSearch
```

```powershell title="List the recorded changes for a Connector Definition (int-keyed), covering its capabilities, settings and files"
Get-JIMConfigurationChangeHistory -Type ConnectorDefinition -Id 3
```

```powershell title="List the recorded changes for an Example Data Set (int-keyed)"
Get-JIMConfigurationChangeHistory -Type ExampleDataSet -Id 5
```

!!! note "Recording a reason"
    To attach a reason to a change so it appears in this history, pass `-ChangeReason` to the write cmdlets: `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, `Set-JIMServiceSetting`, `Reset-JIMServiceSetting`, `New-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `New-JIMMetaverseAttribute`, `Set-JIMMetaverseAttribute`, `Remove-JIMMetaverseAttribute`, `Add-JIMCertificate`, `Set-JIMCertificate`, `Remove-JIMCertificate`, `New-JIMApiKey`, `Set-JIMApiKey`, `Remove-JIMApiKey`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, `Set-JIMPredefinedSearch`, and `New-JIMPredefinedSearchCriteriaGroup`.

---

## :material-delete-outline: Get-JIMDeletedObject

Retrieves deleted objects from the audit trail. Supports filtering by object type, date range, and search terms, with paginated results for efficient browsing of large deletion histories.

<!-- BEGIN GENERATED: Get-JIMDeletedObject -->
### Syntax

```powershell
Get-JIMDeletedObject [-ObjectType <string>] [-ConnectedSystemId <int>] [-MetaverseObjectTypeId <int>] [-Search <string>] [-FromDate <datetime>] [-ToDate <datetime>] [-Page <int>] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectType` | `string` | No | `'MVO'` | Which deleted objects to return: Metaverse Objects or Connected System Objects. One of `MVO` or `CSO`. |
| `ConnectedSystemId` | `int` | No |  | Only Connected System Objects deleted from this Connected System. Applies when returning Connected System Objects. |
| `MetaverseObjectTypeId` | `int` | No |  | Only Metaverse Objects of this type. Applies when returning Metaverse Objects. |
| `Search` | `string` | No |  | Only objects matching this text: by External ID for Connected System Objects, by display name for Metaverse Objects. |
| `FromDate` | `datetime` | No |  | Only objects deleted on or after this date (UTC). |
| `ToDate` | `datetime` | No |  | Only objects deleted on or before this date (UTC). |
| `Page` | `int` | No | `1` | The page of results to return. At least 1. |
| `PageSize` | `int` | No | `50` | How many objects to return per page. From 1 to 1,000. |

### Output

One page of results, whose Items are the deleted objects. A deleted Metaverse Object carries Id, DisplayName, ObjectTypeName, ObjectTypeId, ChangeTime, InitiatedByType and InitiatedByName; a deleted Connected System Object carries Id, ExternalId, DisplayName, ObjectTypeName, ConnectedSystemId, ConnectedSystemName, ChangeTime, InitiatedByType and InitiatedByName.

**By default, or with `-ObjectType MVO`**:

| Property | Type | Description |
|----------|------|-------------|
| `Items` | `object[]` | The deleted object records. Each has `Id`, `DisplayName`, `ObjectTypeName`, `ObjectTypeId`, `ChangeTime`, `InitiatedByType` and `InitiatedByName`. |
| `TotalCount` | `int` | Total count of matching records (for pagination). |
| `Page` | `int` | Current page number (1-based). |
| `PageSize` | `int` | Number of items per page. |

**With `-ObjectType CSO`**:

| Property | Type | Description |
|----------|------|-------------|
| `Items` | `object[]` | The deleted object records. Each has `Id`, `ExternalId`, `DisplayName`, `ObjectTypeName`, `ConnectedSystemId`, `ConnectedSystemName`, `ChangeTime`, `InitiatedByType` and `InitiatedByName`. |
| `TotalCount` | `int` | Total count of matching records (for pagination). |
| `Page` | `int` | Current page number (1-based). |
| `PageSize` | `int` | Number of items per page. |
<!-- END GENERATED: Get-JIMDeletedObject -->

### Examples

```powershell title="Get all deleted Metaverse Objects"
Get-JIMDeletedObject
```

```powershell title="Get deleted Connected System Objects"
Get-JIMDeletedObject -ObjectType CSO
```

```powershell title="Search deleted MVOs by display name"
Get-JIMDeletedObject -Search "John Smith"
```

```powershell title="Search deleted CSOs by external ID"
Get-JIMDeletedObject -ObjectType CSO -Search "CN=jsmith"
```

```powershell title="Filter by date range"
Get-JIMDeletedObject -FromDate "2026-01-01" -ToDate "2026-03-31"
```

```powershell title="Get deleted CSOs for a specific Connected System"
Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId 5
```

```powershell title="Paginate through large result sets"
Get-JIMDeletedObject -Page 3 -PageSize 100
```

---

## :material-text-search: Search-JIMMetaverseObjectChange

Searches changes to Metaverse Objects across every object, newest first: by when they were made, by Metaverse Object Type, by display name, and by how named attributes changed. Use it to answer questions such as "who was disabled this month?" or "who moved out of Finance?" without knowing which objects to look at. Each result names the object (including objects since deleted), the Activity the change was made in, and every attribute the change touched, with the values added and removed.

An attribute condition matches a value added (`ChangedTo`), removed (`ChangedFrom`) or either (`AnyChange`). For a single-valued attribute, a change from Active to Disabled records Disabled as added and Active as removed, so `ChangedTo Disabled` finds it and `ChangedFrom Disabled` finds the later re-enablement. With several conditions, `-MatchMode All` requires every condition to be met by the same change, to one Metaverse Object in one Activity.

Only recorded changes can be found: none are recorded while Metaverse Object change tracking is turned off, and history retention removes older ones.

<!-- BEGIN GENERATED: Search-JIMMetaverseObjectChange -->
### Syntax

```powershell
# Attribute (default)
Search-JIMMetaverseObjectChange [-Attribute <string>] [-Operator <string>] [-Value <string>] [-MatchMode <string>] [-ObjectTypeName <string>] [-ObjectTypeId <int>] [-DisplayName <string>] [-FromDate <datetime>] [-ToDate <datetime>] [-All] [-Force] [-Page <int>] [-PageSize <int>]

# Conditions
Search-JIMMetaverseObjectChange -Condition <hashtable[]> [-MatchMode <string>] [-ObjectTypeName <string>] [-ObjectTypeId <int>] [-DisplayName <string>] [-FromDate <datetime>] [-ToDate <datetime>] [-All] [-Force] [-Page <int>] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Attribute` | `string` | No (Attribute) |  | The name of the Metaverse Attribute whose changes to find. |
| `Operator` | `string` | No (Attribute) | `'AnyChange'` | How the attribute changed. ChangedTo finds a value added (a single-valued attribute changed to it), ChangedFrom a value removed, and AnyChange either. One of `AnyChange`, `ChangedTo` or `ChangedFrom`. |
| `Value` | `string` | No (Attribute) |  | The value the attribute changed to or from. Leave it out to match any value. Text and Reference values match case-insensitively, a Reference value against the referenced Metaverse Object's display name, and * matches any run of characters. A date is yyyy-MM-dd, matching the whole day in UTC. |
| `Condition` | `hashtable[]` | Yes (Conditions) |  | Several attribute conditions, one hashtable each, with the keys Attribute (a name) or AttributeId, Operator (AnyChange when left out) and Value (any value when left out). At most ten. |
| `MatchMode` | `string` | No | `'All'` | With several conditions, All requires every condition to be met by the same change; Any requires one. One of `All` or `Any`. |
| `ObjectTypeName` | `string` | No |  | Only changes to Metaverse Objects of the Metaverse Object Type with this name. |
| `ObjectTypeId` | `int` | No |  | Only changes to Metaverse Objects of the Metaverse Object Type with this id. |
| `DisplayName` | `string` | No |  | Only changes to Metaverse Objects whose display name contains this text. |
| `FromDate` | `datetime` | No |  | Only changes made on or after this time. |
| `ToDate` | `datetime` | No |  | Only changes made on or before this time. |
| `All` | `switch` | No |  | Returns every page of changes rather than one. It stops after 1,000 pages with a warning; `-Force` reads on. |
| `Force` | `switch` | No |  | With `-All`, reads every page, past the 1,000-page limit. |
| `Page` | `int` | No | `1` | The page of changes to return, counting from 1. Ignored with `-All`. At least 1. |
| `PageSize` | `int` | No | `25` | How many changes each page holds. From 1 to 100. |

### Output

One object per change, newest first, with the attributes it changed and the values added and removed.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The change's id. |
| `ChangeTime` | `datetime` | When the change was made (UTC). |
| `ChangeType` | `string` | What kind of change it was. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `MetaverseObjectId` | `guid` | The changed Metaverse Object's id. For an object since deleted, the id it had. Empty when the change record predates the object's id being preserved. |
| `MetaverseObjectDeleted` | `bool` | Whether the Metaverse Object has since been deleted. |
| `MetaverseObjectDeletedAt` | `datetime` | When the Metaverse Object was deleted. Empty unless it has since been deleted. |
| `MetaverseObjectDisplayName` | `string` | The Metaverse Object's display name, or as it was when it was deleted. Empty when it has none. |
| `MetaverseObjectTypeId` | `int` | The Metaverse Object Type's id. Empty when it cannot be known. |
| `MetaverseObjectTypeName` | `string` | The Metaverse Object Type's name. Empty when it cannot be known. |
| `InitiatedByType` | `string` | The kind of principal that made the change. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedByName` | `string` | The name of the principal that made the change, as it was at the time. |
| `ChangeInitiatorType` | `string` | What within JIM made the change: a Synchronisation Rule, a User, Example Data or System, for example. One of `NotSet`, `User`, `WorkflowInstance`, `GroupMembershipRuleEvaluation`, `SynchronisationRule`, `ExampleData` or `System`. |
| `SyncRuleName` | `string` | The Synchronisation Rule that made the change, as it was named at the time. Empty for a change not made by a synchronisation. |
| `ActivityId` | `guid` | The Activity the change was made in, such as a synchronisation, a scheduled deletion or a sign-in. Empty when history retention has since removed the Activity, or for a change recorded before Activities were linked to changes. |
| `AttributeChanges` | `object[]` | The attributes that changed, with the values added and removed. Each has `AttributeId`, `AttributeName`, `Matched`, `AddedValues`, `RemovedValues` and `ValueChangeTotal`. |
<!-- END GENERATED: Search-JIMMetaverseObjectChange -->

### Examples

```powershell title="Find everyone disabled in the last 30 days"
Search-JIMMetaverseObjectChange -ObjectTypeName 'Person' -Attribute 'Employee Status' -Operator ChangedTo -Value 'Disabled' -FromDate (Get-Date).AddDays(-30) -All
```

```powershell title="Find changes that set two attributes at once"
Search-JIMMetaverseObjectChange -Condition @(
    @{ Attribute = 'Employee Status'; Operator = 'ChangedTo'; Value = 'Disabled' },
    @{ Attribute = 'Department'; Operator = 'ChangedTo'; Value = 'Leavers' }
) -All
```

```powershell title="List who moved out of any Finance department, and when"
Search-JIMMetaverseObjectChange -Attribute 'Department' -Operator ChangedFrom -Value 'Finance*' -All |
    Select-Object ChangeTime, MetaverseObjectDisplayName
```

```powershell title="Find every change to one person's manager"
Search-JIMMetaverseObjectChange -DisplayName 'Ana Ruiz' -Attribute 'Manager' -All
```

---

## :material-file-compare: Get-JIMConnectedSystemObjectChangeValue

Gets every value change one Connected System Object change recorded for one attribute, a page at a time. Change history (`Get-JIMConnectedSystemObjectChangeHistory`) and an execution item's changes (`Get-JIMActivityExecutionItem`) return each changed attribute's first ten value changes and, in `valueChangeTotal`, how many there are. A change to a large group can record one value change per member, so read the rest with this cmdlet, naming the change by the `id` change history returns. A change to an object since deleted can still be read.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectChangeValue -->
### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectChangeValue -ChangeId <guid> -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectChangeValue -ChangeId <guid> -AttributeName <string> -All [-Search <string>] [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ChangeId` | `guid` | Yes |  | The ID of the change, as `Get-JIMConnectedSystemObjectChangeHistory` returns it. |
| `AttributeName` | `string` | Yes |  | The name of the attribute whose value changes to get, matched case-insensitively. |
| `Search` | `string` | No |  | Returns only the value changes whose value contains this text, including a referenced object's name. |
| `Page` | `int` | No (Page) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No | `50` | How many value changes a page holds. From 1 to 100. |
| `All` | `switch` | Yes (All) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page, past the 1000-page limit. |

### Output

The attribute's value changes.

| Property | Type | Description |
|----------|------|-------------|
| `ValueChangeType` | `string` | One of `NotSet`, `Add` or `Remove`. |
| `StringValue` | `string` |  |
| `DateTimeValue` | `datetime` |  |
| `IntValue` | `int` |  |
| `LongValue` | `long` |  |
| `DecimalValue` | `double` |  |
| `ByteValueLength` | `int` |  |
| `GuidValue` | `guid` |  |
| `BoolValue` | `bool` |  |
| `ReferenceValue` | `object` | It has `Id`, `ConnectedSystemId`, `TypeName`, `DisplayName` and `SecondaryId`. |
| `IsPendingExportStub` | `bool` | Whether the referenced object had not yet been exported when the change was recorded; `stringValue` then holds the identifier it was given (such as its distinguished name), the value a reader recognises. |
| `SyncRuleId` | `int` | The export Synchronisation Rule whose mapping produced this value. Empty when the value was not produced by an export mapping (e.g. import-side changes carry no rule attribution), or the contributing rule has since been deleted. |
| `SyncRuleName` | `string` | Snapshot of the contributing Synchronisation Rule's name, surviving deletion of the rule. |
<!-- END GENERATED: Get-JIMConnectedSystemObjectChangeValue -->

### Examples

```powershell title="Read every member a group change added"
$change = Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "12345678-1234-1234-1234-123456789abc" | Select-Object -First 1
Get-JIMConnectedSystemObjectChangeValue -ChangeId $change.id -AttributeName 'member' -All |
    Where-Object { $_.valueChangeType -eq 'Add' }
```

```powershell title="Find one member in a large change"
Get-JIMConnectedSystemObjectChangeValue -ChangeId "a1b2c3d4-0000-0000-0000-000000000000" -AttributeName 'member' -Search 'Ana Ruiz'
```

---

## :material-compare-horizontal: Get-JIMMetaverseObjectChangeValue

Gets every value change one Metaverse Object change recorded for one attribute, a page at a time. Change history (`Get-JIMMetaverseObjectChangeHistory`), the change search (`Search-JIMMetaverseObjectChange`) and an execution item's changes (`Get-JIMActivityExecutionItem`) return each changed attribute's first ten value changes and, in `valueChangeTotal`, how many there are. A change to a large group can record one value change per member, so read the rest with this cmdlet, naming the change by the `id` change history returns. A change to an object since deleted can still be read.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectChangeValue -->
### Syntax

```powershell
# Page (default)
Get-JIMMetaverseObjectChangeValue -ChangeId <guid> -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMMetaverseObjectChangeValue -ChangeId <guid> -AttributeName <string> -All [-Search <string>] [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ChangeId` | `guid` | Yes |  | The ID of the change, as `Get-JIMMetaverseObjectChangeHistory` returns it. |
| `AttributeName` | `string` | Yes |  | The name of the attribute whose value changes to get, matched case-insensitively. |
| `Search` | `string` | No |  | Returns only the value changes whose value contains this text, including a referenced object's name. |
| `Page` | `int` | No (Page) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No | `50` | How many value changes a page holds. From 1 to 100. |
| `All` | `switch` | Yes (All) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page, past the 1000-page limit. |

### Output

The attribute's value changes.

| Property | Type | Description |
|----------|------|-------------|
| `ValueChangeType` | `string` | One of `NotSet`, `Add` or `Remove`. |
| `StringValue` | `string` |  |
| `DateTimeValue` | `datetime` |  |
| `IntValue` | `int` |  |
| `LongValue` | `long` |  |
| `DecimalValue` | `double` |  |
| `ByteValueLength` | `int` |  |
| `GuidValue` | `guid` |  |
| `BoolValue` | `bool` |  |
| `ReferenceValue` | `object` | It has `Id`, `DisplayName`, `TypeName` and `TypePluralName`. |
| `ContributedBySyncRuleId` | `int` | The Synchronisation Rule that contributed this value, distinct from the parent SyncRuleId (the rule that caused the overall change): a single sync run can flow attributes from several rules onto one Metaverse Object. Empty when the value was not contributed by a Synchronisation Rule, or the contributing rule has since been deleted. |
| `ContributedBySyncRuleName` | `string` | Snapshot of the contributing Synchronisation Rule's name, surviving deletion of the rule. |
| `ContributedBySystemId` | `int` | The contributing Connected System, resolved from ContributedBySyncRuleId through the still-live Synchronisation Rule. Empty when the rule has since been deleted (the row's rule id is nulled at that point, so the system can no longer be resolved); ContributedBySyncRuleName's snapshot survives regardless. |
| `ContributedBySystemName` | `string` |  |
| `ContributedByKind` | `string` | What kind of source set the value: SynchronisationRule, SignInClaim, System or Person. Empty when no source was recorded for the change. One of `SynchronisationRule`, `SignInClaim`, `System` or `Person`. |
| `ContributedByClaimType` | `string` | The sign-in claim that supplied the value, such as `given_name`, when the source is a sign-in claim; otherwise empty. |
| `IsGeneratedValue` | `bool` | True when a Generated Value Attribute Flow produced this value; false when a Synchronisation Rule mapping, a sign-in claim or another source did. |
<!-- END GENERATED: Get-JIMMetaverseObjectChangeValue -->

### Examples

```powershell title="Read every member a group change removed"
$change = Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc" | Select-Object -First 1
Get-JIMMetaverseObjectChangeValue -ChangeId $change.id -AttributeName 'Static Members' -All |
    Where-Object { $_.valueChangeType -eq 'Remove' }
```

---

## :material-counter: Get-JIMHistoryCount

Gets the count of change history records for a Connected System. Useful for monitoring history growth and planning cleanup operations.

<!-- BEGIN GENERATED: Get-JIMHistoryCount -->
### Syntax

```powershell
# ById (default)
Get-JIMHistoryCount -ConnectedSystemId <int>

# ByName
Get-JIMHistoryCount -ConnectedSystemName <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById) |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System; it must match exactly. |

### Output

One object giving how many Connected System Object change records the Connected System holds.

| Property | Type | Description |
|----------|------|-------------|
| `ConnectedSystemId` | `int` | Connected System ID. |
| `ConnectedSystemName` | `string` | Connected System name. |
| `ChangeRecordCount` | `int` | Number of Connected System Object change records for this Connected System. |
<!-- END GENERATED: Get-JIMHistoryCount -->

### Examples

```powershell title="Get history count by ID"
Get-JIMHistoryCount -ConnectedSystemId 3
```

```powershell title="Get history count by name"
Get-JIMHistoryCount -ConnectedSystemName "Active Directory"
```

```powershell title="Get history counts for all Connected Systems"
Get-JIMConnectedSystem | Get-JIMHistoryCount
```

```powershell title="Find systems with the most history records"
Get-JIMConnectedSystem |
    Get-JIMHistoryCount |
    Sort-Object ChangeRecordCount -Descending |
    Format-Table ConnectedSystemName, ChangeRecordCount
```

---

## :material-broom: Invoke-JIMHistoryCleanup

Runs a history retention pass on demand. Removes history that has had the retention period set for its kind: Connected System Object and Metaverse Object change history, Configuration Change Previews, Activities, and Pending Password Changes that reached a terminal state (including a provisioned Connected System Object's initial password). Records still being worked are never removed, however old.

This also runs on its own, daily, on the built-in [History Retention Cleanup Schedule](../configuration/schedules.md#built-in-schedules). Use this cmdlet to run a pass now, or to drain a large backlog faster than one pass a day.

<!-- BEGIN GENERATED: Invoke-JIMHistoryCleanup -->
### Syntax

```powershell
Invoke-JIMHistoryCleanup [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PassThru` | `switch` | No |  | Returns what the pass removed. |

### Output

With `-PassThru`, what the pass removed and the retention settings it applied; otherwise nothing.

| Property | Type | Description |
|----------|------|-------------|
| `CsoChangesDeleted` | `int` | Number of Connected System Object change records deleted. |
| `MvoChangesDeleted` | `int` | Number of Metaverse Object change records deleted. |
| `ActivitiesDeleted` | `int` | Number of Activity records deleted. |
| `ConfigurationChangeActivitiesDeleted` | `int` | Number of configuration-change Activity records (versioned configuration snapshots) deleted, per the configuration change retention period. |
| `ConfigurationChangeRetentionPeriodDays` | `int` | Configured configuration change retention period in days. |
| `SecurityEventActivitiesDeleted` | `int` | Number of security event Activity records (TargetType Authentication: interactive sign-in success/failure, failed API key or SSO access token authentication) deleted, per the security event retention period. |
| `SecurityEventRetentionPeriodDays` | `int` | Configured security event retention period in days. |
| `PasswordEventActivitiesDeleted` | `int` | Number of Password Synchronisation Activity records deleted (the delivery passes, the fan-out records, and the per-system outcome children), per the Password Synchronisation retention period. |
| `PasswordQueueRecordsDeleted` | `int` | Number of queued password changes removed: those parked, expired or cancelled for longer than the Password Synchronisation retention period. Changes still owed to a Connected System are never removed, however old. This is also the number of encrypted passwords JIM stopped holding. |
| `PasswordEventRetentionPeriodDays` | `int` | Configured Password Synchronisation retention period in days. |
| `OldestRecordDeleted` | `datetime` | Oldest record timestamp that was deleted (if any records were deleted). |
| `NewestRecordDeleted` | `datetime` | Newest record timestamp that was deleted (if any records were deleted). |
| `CutoffDate` | `datetime` | Cutoff date used for this cleanup operation (records older than this were deleted). |
| `RetentionPeriodDays` | `int` | Configured retention period in days. |
| `BatchSize` | `int` | Maximum records deleted per type in this batch. |
<!-- END GENERATED: Invoke-JIMHistoryCleanup -->

!!! note "Batch Size Limitation"
    Each invocation removes at most `History.CleanupBatchSize` records of any one kind. For environments with a large backlog, call this cmdlet repeatedly, or leave the daily Schedule to work through it over successive runs. Each invocation creates an audit Activity saying what it removed.

### Examples

```powershell title="Run a basic cleanup"
Invoke-JIMHistoryCleanup
```

```powershell title="Run cleanup and view statistics"
Invoke-JIMHistoryCleanup -PassThru
```

```powershell title="Batch cleanup loop for large backlogs"
do {
    $result = Invoke-JIMHistoryCleanup -PassThru
    $total = $result.CsoChangesDeleted + $result.MvoChangesDeleted + $result.ActivitiesDeleted
    Write-Host "Deleted $total records (cutoff: $($result.CutoffDate))"
} while ($total -gt 0)
```

```powershell title="Check what a pass removed from Password Synchronisation history"
Invoke-JIMHistoryCleanup -PassThru |
    Select-Object PasswordEventActivitiesDeleted, PasswordQueueRecordsDeleted, PasswordEventRetentionPeriodDays
```

---

## :material-book-open-variant-outline: See also

- [API reference](../api/index.md): the Scalar API reference (linked from the API index) covers the history endpoints
- [Activities](activities.md): cmdlets for querying activity logs and execution history
- [Activities concept](../configuration/activities.md): how configuration changes are carried on Activities, including the redaction model
- [Schedules](../configuration/schedules.md#built-in-schedules): the built-in History Retention Cleanup Schedule that runs a retention pass daily
- [Service settings](../administration/configuration.md#service-settings): the `History.*` retention periods each kind of record is governed by
