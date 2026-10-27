---
title: Metaverse
description: "The cmdlets that find and read Metaverse Objects, explain where their values and connections come from, and list those pending deletion."
type: reference
audience: automation-author
---

# Metaverse

The Metaverse is the central identity store in JIM. These cmdlets find and read Metaverse Objects, explain where each value and connection comes from, and review the Metaverse Objects pending deletion. A Metaverse Object's password is set with the [Password Synchronisation](password-synchronisation.md#set-jimmetaverseobjectpassword) cmdlets, and the values JIM generated for it are listed with the [Generated Values](generated-values.md) cmdlets. The Metaverse schema is managed with the [Metaverse Object Type](metaverse-object-types.md) and [Metaverse Attribute](metaverse-attributes.md) cmdlets.

---

## :material-magnify: Search-JIMMetaverseObject

Runs a [Predefined Search](predefined-searches.md), returning each match in summary with the attributes the search lists. It answers quickly at scale, 100,000 objects and more, so use it for lists and searches; use [`Get-JIMMetaverseObject`](#get-jimmetaverseobject) for an object in full, or to choose the attributes.

<!-- BEGIN GENERATED: Search-JIMMetaverseObject -->
### Syntax

```powershell
# List (default)
Search-JIMMetaverseObject -PredefinedSearchUri <string> [-Search <string>] [-HasAttribute <string>] [-SortBy <string>] [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Search-JIMMetaverseObject -PredefinedSearchUri <string> -All [-Search <string>] [-HasAttribute <string>] [-SortBy <string>] [-SortDirection <string>] [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchUri` | `string` | Yes |  | The URI of the Predefined Search to run, such as users or groups. It decides which Object Type is searched and which attributes are returned. |
| `Search` | `string` | No |  | Text to look for in the objects' text attribute values, ignoring case; part of a value is enough. |
| `HasAttribute` | `string` | No |  | Returns only the objects that hold a value for this Metaverse Attribute, named exactly but ignoring case. A multi-valued attribute counts once. A name JIM does not recognise returns nothing. |
| `SortBy` | `string` | No |  | The attribute to sort by. Without it, objects are sorted by when they were created. |
| `SortDirection` | `string` | No | `'desc'` | The sort order. desc is the default. One of `asc` or `desc`. |
| `All` | `switch` | Yes (ListAll) |  | Returns every page of results rather than one. It stops after 1,000 pages, about 100,000 objects at the default page size, with a warning; `-Force` reads on. Cannot be used with `-Page`. |
| `Force` | `switch` | No (ListAll) |  | With `-All`, reads every page, past the 1,000-page limit. |
| `Page` | `int` | No (List) | `1` | The page of results to return, counting from 1. At least 1. |
| `PageSize` | `int` | No | `100` | How many objects each page holds. From 1 to 100. |

### Output

One object in summary per match, with its display name, Object Type and the attributes the Predefined Search lists.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier (GUID) of the Metaverse Object. |
| `Created` | `datetime` | When the object was created. |
| `DisplayName` | `string` | The display name of the object (always included). |
| `Status` | `string` | The current status of the object. One of `Normal` or `Obsolete`. |
| `Type` | `object` | The Metaverse Object Type, with its Id and Name. It has `Id` and `Name`. |
| `Attributes` | `object` | The object's other attribute values, keyed by attribute name: the attributes the request named, or for a Predefined Search, the attributes the search lists. Each value has its natural type: text, a number, a boolean, an ISO-8601 date and time, a GUID, or base64 for Binary; a Reference holds the referenced Metaverse Object's ID as a GUID. A multi-valued attribute shows one of its values (the last). DisplayName is not included, as it has its own property. |
<!-- END GENERATED: Search-JIMMetaverseObject -->

### Examples

```powershell title="Search for users"
Search-JIMMetaverseObject -PredefinedSearchUri "users"
```

```powershell title="Search with a query"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -Search "Smith"
```

```powershell title="Get all users with auto-pagination"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All
```

```powershell title="Get all users, overriding the -All safety cap for a very large result set"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All -Force
```

```powershell title="Find users that hold a value for an attribute"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre"
```

```powershell title="Sort groups by display name"
Search-JIMMetaverseObject -PredefinedSearchUri "groups" -SortBy "Display Name" -SortDirection asc
```

---

## :material-hub-outline: Get-JIMMetaverseObject

Gets Metaverse Objects: one by ID, or a page of them filtered by Object Type, display name or an attribute's value, or how many match.

<!-- BEGIN GENERATED: Get-JIMMetaverseObject -->
### Syntax

```powershell
# List (default)
Get-JIMMetaverseObject [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>] [-AttributeName <string>] [-AttributeValue <string>] [-Attributes <string[]>] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseObject -Id <guid>

# Count
Get-JIMMetaverseObject -Count [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>] [-AttributeName <string>] [-AttributeValue <string>]

# ListAll
Get-JIMMetaverseObject -All [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>] [-AttributeName <string>] [-AttributeValue <string>] [-Attributes <string[]>] [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The ID of the Metaverse Object to get. Accepts pipeline input by property name. |
| `ObjectTypeId` | `int` | No (List, Count, ListAll) |  | Returns only the objects of the Object Type with this ID. |
| `ObjectTypeName` | `string` | No (List, Count, ListAll) |  | Returns only the objects of the Object Type with this name. |
| `Search` | `string` | No (List, Count, ListAll) |  | Returns only the objects whose display name matches. Supports wildcards. |
| `AttributeName` | `string` | No (List, Count, ListAll) |  | Returns only the objects whose value for this attribute matches `-AttributeValue`. Give both together. |
| `AttributeValue` | `string` | No (List, Count, ListAll) |  | The value `-AttributeName` must match: exactly, but ignoring case. The _ and % characters match only themselves. |
| `Attributes` | `string[]` | No (List, ListAll) |  | The attributes to include with each object, by name; "*" includes them all. DisplayName is always included. |
| `All` | `switch` | Yes (ListAll) |  | Returns every page of results rather than one. It stops after 1,000 pages, about 100,000 objects at the default page size, with a warning; `-Force` reads on. Cannot be used with `-Page`. |
| `Force` | `switch` | No (ListAll) |  | With `-All`, reads every page, past the 1,000-page limit. |
| `Count` | `switch` | Yes (Count) |  | Returns only the number of matching objects. |
| `Page` | `int` | No (List) | `1` | The page of results to return, counting from 1. At least 1. |
| `PageSize` | `int` | No (List, ListAll) | `100` | How many objects each page holds. From 1 to 100. |

### Output

By default, one page of matching objects, each in summary. With `-Id`, the Metaverse Object in full: its values, where each came from, and the Connected System Objects joined to it. With `-Count`, the number of matching objects.

**By default, or with `-All`**: properties as under [Search-JIMMetaverseObject](#search-jimmetaverseobject).

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the Metaverse Object. |
| `Created` | `datetime` | When the Metaverse Object was created (UTC). |
| `LastUpdated` | `datetime` | When the Metaverse Object last changed (UTC). |
| `CreatedByType` | `string` | The kind of principal behind the earliest recorded change (User, ApiKey or System), with CreatedById and CreatedByName; empty when no change history is recorded, as when change tracking is off or the history has been purged. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `CreatedById` | `guid` | The unique identifier of the principal behind the earliest recorded change; empty when no change history is recorded. |
| `CreatedByName` | `string` | The name of the principal behind the earliest recorded change, as it was then. |
| `LastUpdatedByType` | `string` | The kind of principal behind the latest recorded change (User, ApiKey or System), with LastUpdatedById and LastUpdatedByName; empty when no change history is recorded. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `LastUpdatedById` | `guid` | The unique identifier of the principal behind the latest recorded change; empty when no change history is recorded. |
| `LastUpdatedByName` | `string` | The name of the principal behind the latest recorded change, as it was then. |
| `DisplayName` | `string` | The Metaverse Object's display name; empty when it has none. |
| `Status` | `string` | Whether the Metaverse Object is in use (Normal) or has been marked Obsolete. One of `Normal` or `Obsolete`. |
| `Origin` | `string` | How the Metaverse Object came to exist: Projected from a Connected System Object, or Internal, created in JIM itself. JIM never deletes an Internal object automatically. One of `Projected` or `Internal`. |
| `LastConnectorDisconnectedDate` | `datetime` | When the last Connected System Object was disconnected from the Metaverse Object (UTC); empty while one is still joined. |
| `IsPendingDeletion` | `bool` | Whether the Metaverse Object is due to be deleted under its Object Type's deletion rule. |
| `DeletionEligibleDate` | `datetime` | When the Metaverse Object becomes due for deletion, once its grace period ends (UTC); empty when it is not pending deletion. |
| `DeletionTriggeredBySystemName` | `string` | The Connected System whose disconnection made the Metaverse Object pending deletion. Empty when it is not pending deletion, when no single system triggered it (its last connection went without one), or when the deletion was scheduled before JIM recorded the trigger. |
| `Type` | `object` | The Metaverse Object Type, with its Id and Name. It has `Id` and `Name`. |
| `AttributeValues` | `object[]` | Every value the Metaverse Object holds, one entry per value, each with the attribute it belongs to and the Connected System and Synchronisation Rule that contributed it. Each has `Id`, `AttributeId`, `AttributeName`, `AttributeType`, `AttributePlurality`, `StringValue`, `DateTimeValue`, `IntValue`, `LongValue`, `DecimalValue`, `ByteValue`, `GuidValue`, `BoolValue`, `ReferenceValueId`, `ReferenceValueDisplayName`, `ContributedBySystemId`, `ContributedBySystemName`, `ContributedBySyncRuleId`, `ContributedBySyncRuleName`, `ContributedByKind`, `ContributedByClaimType` and `NullValue`. |
| `ConnectedSystemObjects` | `object[]` | Every Connected System Object joined to the Metaverse Object, with how it was joined and the state of its connection, as the portal's Connections tab shows them. Each has `Id`, `ConnectedSystemId`, `ConnectedSystemName`, `DisplayName`, `ExternalId`, `SecondaryExternalId`, `PendingExternalId`, `PendingSecondaryExternalId`, `ObjectTypeName`, `JoinType`, `DateJoined`, `Status`, `State`, `IsSource`, `IsTarget`, `PendingAttributeChangeCount` and `LastSynchronised`. |

**With `-Count`**: an `int`.
<!-- END GENERATED: Get-JIMMetaverseObject -->

With `-Id`, each entry in `AttributeValues` says where its value came from: `ContributedBySystemId` and `ContributedBySystemName` name the Connected System, and `ContributedBySyncRuleId` and `ContributedBySyncRuleName` the Synchronisation Rule that won [Attribute Priority](../concepts/attribute-priority.md). A value set outside synchronisation says so in `ContributedByKind`: `SignInClaim` for a value read from a sign-in token, with the claim in `ContributedByClaimType`, or `System` for a value JIM set itself. An entry whose `NullValue` is `$true` is an asserted null: a source's deliberate statement that the attribute has no value, carrying provenance only. Treat it as no value, though it is not the same as the attribute having no entry at all.

Each entry in `ConnectedSystemObjects` carries what the portal's Connections tab shows. `IsSource` and `IsTarget` say whether an enabled import or export Synchronisation Rule exists for that object's Connected System and Object Type, so an object can be both, or neither. `State` combines the object's status with any queued Pending Export: `InSync`, `UpdatePending`, `ProvisioningExportPending`, `ProvisioningAwaitingConfirmation`, `DeletePending`, `ExportFailed` or `Obsolete`. `PendingAttributeChangeCount` is filled in only for `UpdatePending`.

### Examples

```powershell title="Get a Metaverse Object by ID"
Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Search for Users by display name"
Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Smith*" -Attributes @("Display Name", "Mail")
```

```powershell title="Find objects by attribute value"
Get-JIMMetaverseObject -AttributeName "Employee Id" -AttributeValue "12345" -Attributes @("Display Name", "Department")
```

```powershell title="Retrieve all Group objects with auto-pagination"
Get-JIMMetaverseObject -ObjectTypeName "Group" -All -Attributes @("Display Name", "Member")
```

```powershell title="Fetch a very large Metaverse, overriding the -All safety cap"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Get-JIMMetaverseObject -ObjectTypeName "User" -All -Force
```

```powershell title="See which Connected Systems a Metaverse Object is joined to, and how"
(Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890").ConnectedSystemObjects |
    Format-Table ConnectedSystemName, ObjectTypeName, JoinType, State, IsSource, IsTarget
```

```powershell title="Find a Metaverse Object's Connected System Objects whose export has failed"
(Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890").ConnectedSystemObjects |
    Where-Object { $_.State -eq "ExportFailed" }
```

```powershell title="Page through results manually"
Get-JIMMetaverseObject -ObjectTypeName "User" -Page 3 -PageSize 50
```

---

## :material-history: Get-JIMMetaverseObjectChangeHistory

Gets a Metaverse Object's change history, newest first: who or what made each change, the Synchronisation Rule and Run Profile behind it, and the values it changed.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectChangeHistory -->
### Syntax

```powershell
# Page (default)
Get-JIMMetaverseObjectChangeHistory -Id <guid> [-Page <int>] [-PageSize <int>]

# All
Get-JIMMetaverseObjectChangeHistory -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The ID of the Metaverse Object. Accepts pipeline input by property name. |
| `All` | `switch` | Yes (All) |  | Returns every page of changes rather than one. It stops after 1,000 pages, about 50,000 changes at the default page size, with a warning; `-Force` reads on. Cannot be used with `-Page`. |
| `Force` | `switch` | No (All) |  | With `-All`, reads every page, past the 1,000-page limit. |
| `Page` | `int` | No (Page) | `1` | The page of changes to return, counting from 1. At least 1. |
| `PageSize` | `int` | No | `50` | How many changes each page holds. From 1 to 100. |

### Output

One object per change, newest first.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the change. |
| `ChangeType` | `string` | What the change did to the Metaverse Object. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `ChangeTime` | `datetime` | When the change was made (UTC). |
| `InitiatedByType` | `string` | The kind of principal that made the change: a User, an API key, or JIM itself (System). One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The unique identifier of the principal that made the change; empty for System. |
| `InitiatedByName` | `string` | The name of the principal that made the change, as it was then. |
| `ChangeInitiatorType` | `string` | What within JIM made the change, such as a Synchronisation Rule, a User or Example Data. One of `NotSet`, `User`, `WorkflowInstance`, `GroupMembershipRuleEvaluation`, `SynchronisationRule`, `ExampleData` or `System`. |
| `SyncRuleId` | `int` | The unique identifier of the Synchronisation Rule that made the change, when one did. |
| `SyncRuleName` | `string` | That Synchronisation Rule's name. |
| `ActivityRunProfileExecutionItemId` | `guid` | The Activity execution item that recorded the change, when a Run Profile made it. |
| `ActivityId` | `guid` | The Activity the change was made in, such as a synchronisation, a scheduled deletion or a sign-in. Empty when the Activity has since been removed by history retention, or for a change recorded before Activities were linked to changes. |
| `CsoId` | `guid` | The unique identifier of the Connected System Object whose synchronisation made the change, when one did. |
| `CsoExternalId` | `string` | That Connected System Object's external id, as it was then; kept after the object is deleted. |
| `ConnectedSystemId` | `int` | The unique identifier of the Connected System the Run Profile belonged to, when one made the change. |
| `ConnectedSystemName` | `string` | That Connected System's name. |
| `RunProfileName` | `string` | The name of the Run Profile that made the change, when one did. |
| `ConnectedSystemRunType` | `string` | The kind of Run Profile that made the change, when one did. One of `NotSet`, `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `AttributeChanges` | `object[]` | The attributes the change touched, each with the values it added and removed. Each has `AttributeName`, `AttributeType`, `AttributePlurality`, `AttributeId`, `ValueChanges` and `ValueChangeTotal`. |
<!-- END GENERATED: Get-JIMMetaverseObjectChangeHistory -->

Each value change under `AttributeChanges` names the Synchronisation Rule that contributed it, in `ContributedBySyncRuleId` and `ContributedBySyncRuleName`, because one change can carry values from several rules; both are empty when no rule contributed the value. `ContributedBySystemId` and `ContributedBySystemName` name that rule's Connected System, and are empty once the rule has been deleted, though its name survives. `IsGeneratedValue` is `$true` when a Generated Value Attribute Flow produced the value. `ContributedByKind` says what kind of source set the value (`SynchronisationRule`, `SignInClaim`, `System` or `Person`), and `ContributedByClaimType` names the claim for a value read from a sign-in token; both are empty for a change that recorded no source.

### Examples

```powershell title="Get the most recent page of changes"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through every change for a Metaverse Object"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All
```

```powershell title="Pipe a Metaverse Object into the cmdlet"
Get-JIMMetaverseObject -ObjectTypeName "Group" -Search "Project-Alpha" |
    Get-JIMMetaverseObjectChangeHistory -All
```

---

## :material-map-marker-path: Get-JIMMetaverseObjectProvenance

Shows where a Metaverse Object's attribute values came from: which Connected System and Synchronisation Rule contributed each one, or that no contributor is recorded.

With just `-Id`, it summarises every attribute that holds a value. With `-AttributeName` or `-AttributeId`, it returns everything the portal's attribute inspector shows for that one attribute.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectProvenance -->
### Syntax

```powershell
# Summary (default)
Get-JIMMetaverseObjectProvenance -Id <guid>

# ByAttributeName
Get-JIMMetaverseObjectProvenance -Id <guid> -AttributeName <string>

# ByAttributeId
Get-JIMMetaverseObjectProvenance -Id <guid> -AttributeId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The ID of the Metaverse Object. Accepts pipeline input by property name. |
| `AttributeName` | `string` | Yes (ByAttributeName) |  | The name of the attribute to inspect, matched exactly but ignoring case among the attributes the object holds a value for. An attribute it holds no value for is an error. Cannot be used with `-AttributeId`. |
| `AttributeId` | `int` | Yes (ByAttributeId) |  | The ID of the attribute to inspect. Cannot be used with `-AttributeName`. |

### Output

With just `-Id`, a summary: each attribute that holds a value, with the distinct origins of its values. With `-AttributeName` or `-AttributeId`, everything the portal's attribute inspector shows for that attribute.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `MetaverseObjectId` | `guid` | The unique identifier of the Metaverse Object. |
| `Attributes` | `object[]` | One entry per attribute that holds a value, each with the distinct origins of its values, the most common first. Each has `AttributeId`, `AttributeName` and `Origins`. |

**With `-AttributeName` or `-AttributeId`**:

| Property | Type | Description |
|----------|------|-------------|
| `MetaverseObjectId` | `guid` | The unique identifier of the Metaverse Object. |
| `MetaverseObjectTypeId` | `int` | The unique identifier of the Metaverse Object's Object Type. |
| `AttributeId` | `int` | The unique identifier of the attribute. |
| `AttributeName` | `string` | The attribute's name. |
| `AttributeType` | `string` | The kind of value the attribute holds. One of `NotSet`, `Text`, `Number`, `DateTime`, `Binary`, `Reference`, `Guid`, `Boolean`, `LongNumber` or `Decimal`. |
| `AttributePlurality` | `string` | Whether the attribute holds one value or many. One of `SingleValued` or `MultiValued`. |
| `CurrentValues` | `object[]` | The values the attribute holds now, each with its origin; at most 50, CurrentValueTotalCount giving the full count. Each has `DisplayValue`, `ReferenceMetaverseObjectId`, `ReferenceTypeName`, `ReferenceTypePluralName` and `Origin`. |
| `CurrentValueTotalCount` | `int` | How many values the attribute holds, including any beyond the 50 CurrentValues lists. |
| `ContributingConnectedSystemObject` | `object` | The joined Connected System Object the current value came from; empty when none can be identified. It has `Id`, `ConnectedSystemId`, `ConnectedSystemName`, `TypeName`, `DisplayName` and `ExternalId`. |
| `LastSet` | `object` | The most recent recorded change that set the current value, with its Activity; empty when no change is recorded. It has `ChangeTime`, `ActivityId`, `ActivityRunProfileExecutionItemId`, `ActivityDescription`, `InitiatedByName` and `ChangeInitiatorType`. |
| `Sources` | `object[]` | Every Attribute Flow that can contribute to the attribute on this Object Type, in priority order, each with the value it would supply now and how that compares with the value in use. Each has `Rank`, `MappingId`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName`, `IsExpression`, `IsGeneratedValue`, `Expression`, `State`, `CandidateValues` and `Note`. |
| `History` | `object[]` | The attribute's change history, newest first: at most 50 entries, HistoryTruncated saying whether there were more. Each has `Kind`, `Value`, `PreviousValue`, `SyncRuleId`, `SyncRuleName`, `ContributedByKind`, `ClaimType`, `IsGeneratedValue` and `Change`. |
| `HistoryTruncated` | `bool` | Whether History stops short of the attribute's full history. |
<!-- END GENERATED: Get-JIMMetaverseObjectProvenance -->

In the summary, each origin's `Kind` is `NotRecorded`, `SynchronisationRule`, `GeneratedValue`, `SignInClaim`, `System` or `SetByPerson`. A `GeneratedValue` origin still names its Connected System and Synchronisation Rule, and its `Corrected` is `$true` once JIM revised the value after a collision. A `SignInClaim` origin is a value JIM read from a sign-in token, and its `ClaimType` names the claim, such as `given_name`; a `System` origin is a value JIM set itself, such as the initial administrator's Type. Each `History` entry carries the same source as `ContributedByKind`, with `ClaimType` for a sign-in claim. For one attribute, each entry in `Sources` has a `State` saying how its value stands against the one in use: `InUse`, `Outranked`, `NoValue`, `NotJoined`, `Disabled` or `NotEvaluated`.

### Examples

```powershell title="Get the origin of every attribute holding a value"
Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get full provenance for one attribute by name"
Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -AttributeName Department
```

```powershell title="List the Synchronisation Rules that could contribute to an attribute but are currently losing Attribute Priority"
(Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -AttributeId 42).Sources |
    Where-Object State -eq 'Outranked'
```

```powershell title="Pipe a Metaverse Object into the cmdlet"
Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue jsmith |
    Get-JIMMetaverseObjectProvenance
```

---

## :material-link-variant: Get-JIMMetaverseObjectConnection

Explains why a Metaverse Object is connected where it is, and why it is not connected elsewhere. It answers the same question as the Metaverse Object's Connections tab in the portal, in the same words; see [Why it is connected, and why it is not](../configuration/metaverse-objects.md#why-it-is-connected-and-why-it-is-not) for what each reason means.

For each Connected System Object joined to the Metaverse Object, it returns how the object was joined (the method, the Synchronisation Rule responsible, the date and the Activity) and the scoping of every relevant enabled Synchronisation Rule, evaluated now against current values. With `-IncludeNotConnected`, it also returns one entry per enabled export Synchronisation Rule whose Connected System holds no object joined to this one, with the reason, a one-line hint, what would change it, and a plain-text summary to paste into a ticket.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectConnection -->
### Syntax

```powershell
Get-JIMMetaverseObjectConnection -Id <guid> [-ConnectedSystemName <string>] [-IncludeNotConnected]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The ID of the Metaverse Object. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | No |  | Returns only the entries for the Connected System with this name, matched exactly but ignoring case. Nothing matching is an error. |
| `IncludeNotConnected` | `switch` | No |  | Also explains each enabled export Synchronisation Rule whose Connected System holds no object joined to this one. |

### Output

One object per entry: each joined Connected System Object and, with `-IncludeNotConnected`, each export Synchronisation Rule that has not connected. Every entry has the same properties, so the two can be listed, filtered and sorted together. Values of credential attributes are withheld.

| Property | Type | Description |
|----------|------|-------------|
| `MetaverseObjectId` | `guid` | The unique identifier of the Metaverse Object. |
| `MetaverseObjectName` | `string` | The Metaverse Object's display name. |
| `ConnectedSystem` | `string` | The Connected System's name. |
| `ConnectedSystemId` | `int` | The unique identifier of the Connected System. |
| `Connected` | `bool` | `$true` for a joined Connected System Object, `$false` for an export Synchronisation Rule that has not connected. |
| `Object` | `string` | The joined Connected System Object's identifier as the portal's Connections tab shows it: its Secondary External ID (for an LDAP Connected System, its distinguished name) where it has one, otherwise its External ID, falling back to the value a queued Pending Export will assign; empty when not connected. |
| `ConnectedSystemObjectId` | `guid` | The unique identifier of the joined Connected System Object; empty when not connected. |
| `ExternalId` | `string` | The joined object's External ID; empty when it has none yet or when not connected. |
| `SecondaryExternalId` | `string` | The joined object's Secondary External ID; empty when its Connected System has none, it has no value yet, or when not connected. |
| `PendingExternalId` | `string` | The External ID a queued Pending Export will give the joined object, where it has none yet; empty otherwise. |
| `PendingSecondaryExternalId` | `string` | The Secondary External ID a queued Pending Export will give the joined object, where it has none yet; empty otherwise. |
| `ObjectType` | `string` | The Connected System Object Type's name. |
| `Role` | `string` | Source, Target, Source and Target or None, from the enabled Synchronisation Rules for that Connected System and Object Type; always Target when not connected. |
| `State` | `string` | For a connection, its state, such as InSync or UpdatePending; when not connected, why: NotInScope, ProvisioningDisabled, RuleMisconfigured or NotYetProvisioned. |
| `SyncRule` | `string` | The export Synchronisation Rule that has not connected; empty for a connection. |
| `Join` | `object` | For a connection, how it was joined: JoinType, Method, DateJoined, SyncRuleId, SyncRuleName, Source, Description, ActivityId and RunProfileExecutionItemId. |
| `Hint` | `string` | When not connected, a one-line hint at what would change it. |
| `BulletsTitle` | `string` | When not connected, the heading for Bullets. |
| `Bullets` | `string[]` | When not connected, what would change it, a line each. |
| `Summary` | `string` | When not connected, a plain-text summary to paste into a ticket. |
| `Scoping` | `object[]` | One explanation per relevant Synchronisation Rule, evaluated now against current values: SyncRuleName, Direction, Outcome, Hint, Groups and Criteria, each criterion with Path, Met, Outcome, AttributeName, ComparisonType, Expected, Actual, ValueCount, Description and ActualDescription; an expression criterion adds Expression, ExpressionInputs (each attribute it read, with Accessor, Values, ValueCount and Masked) and, when it could not be evaluated, FailureReason. |
| `Conflicts` | `object[]` | For a connection, the export Synchronisation Rules that cannot connect because it holds the slot. |
| `EvaluatedAt` | `datetime` | When JIM evaluated the explanations (UTC). |
<!-- END GENERATED: Get-JIMMetaverseObjectConnection -->

`Join.Method` is `Projection`, `Provisioning`, `InboundMatching` or `ExportMatching`, and `Join.Source` says whether JIM recorded the join (`Recorded`), worked it out from Activity history (`Derived`), or has no record of it (`NotRecorded`). A join made by matching also carries `ObjectMatchingRuleId` and `MatchValue`, what it matched on. Each `Scoping` entry's `Outcome` is `InScope`, `OutOfScope` or `Undetermined`.

A criterion's `Path` locates it in the criteria tree: the top-level group's position, then each child's position within its group, counting from 1 and criteria before child groups, so `1.3.2` is the second child of the third child of the first top-level group. A criterion on a credential attribute has `Masked` set to `$true`, and its `Expected` and `Actual` are empty.

### Examples

```powershell title="See where a Metaverse Object is connected, and why it is not connected elsewhere"
Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected |
    Format-Table ConnectedSystem, Connected, State, Hint
```

```powershell title="Copy the explanation for one Connected System into a ticket"
(Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected -ConnectedSystemName "Finance App").Summary
```

```powershell title="List every scoping criterion the Metaverse Object currently fails"
Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected |
    ForEach-Object { $_.Scoping } |
    ForEach-Object { $_.Criteria } |
    Where-Object { -not $_.Met } |
    Format-Table Path, Description, ActualDescription
```

```powershell title="See how each connection was joined"
Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue jsmith |
    Get-JIMMetaverseObjectConnection |
    Format-Table ConnectedSystem, Object, @{ Name = 'Joined'; Expression = { $_.Join.Description } }
```

```powershell title="Report everyone of a type who is out of scope of an export rule, and why"
Get-JIMMetaverseObject -ObjectTypeName "Person" -All |
    Get-JIMMetaverseObjectConnection -IncludeNotConnected -ConnectedSystemName "Finance App" -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq 'NotInScope' } |
    Format-Table MetaverseObjectName, SyncRule, Hint
```

---

## :material-delete-clock-outline: Get-JIMPendingDeletion

Gets the Metaverse Objects pending deletion: a page of them, how many there are, or a summary of where their deletions stand.

<!-- BEGIN GENERATED: Get-JIMPendingDeletion -->
### Syntax

```powershell
# List (default)
Get-JIMPendingDeletion [-ObjectTypeId <int>] [-Page <int>] [-PageSize <int>]

# Count
Get-JIMPendingDeletion -Count [-ObjectTypeId <int>]

# Summary
Get-JIMPendingDeletion -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectTypeId` | `int` | No (List, Count) |  | Returns only the Metaverse Objects of the Object Type with this ID. |
| `Page` | `int` | No (List) | `1` | The page of results to return, counting from 1. At least 1. |
| `PageSize` | `int` | No (List) | `25` | How many objects each page holds. From 1 to 100. |
| `Count` | `switch` | Yes (Count) |  | Returns only the number of Metaverse Objects pending deletion. |
| `Summary` | `switch` | Yes (Summary) |  | Returns how many are pending deletion, and where their deletions stand, across every Object Type. |

### Output

By default, one page of Metaverse Objects pending deletion. With `-Count`, how many there are. With `-Summary`, the counts by where each deletion stands.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the Metaverse Object. |
| `DisplayName` | `string` | The display name of the Metaverse Object. |
| `Type` | `object` | The Metaverse Object Type, with its Id and Name. It has `Id` and `Name`. |
| `LastConnectorDisconnectedDate` | `datetime` | When the last Connected System Object was disconnected from the Metaverse Object (UTC). |
| `DeletionEligibleDate` | `datetime` | When the Metaverse Object becomes due for deletion, once its grace period ends (UTC). |
| `DaysUntilDeletion` | `int` | How many days remain until the Metaverse Object is due for deletion; negative once it is overdue. |
| `GracePeriod` | `string` | The grace period its Object Type sets. |
| `ConnectedSystemObjectCount` | `int` | How many Connected System Objects are still joined to the Metaverse Object. |
| `Status` | `string` | Where the deletion stands: Deprovisioning while Connected System Objects remain joined, AwaitingGracePeriod once none remain but the grace period is still running, and ReadyForDeletion once it has ended. |
| `DeletionTriggeredBySystemId` | `int` | The unique identifier of the Connected System whose disconnection scheduled the deletion; empty when no single system triggered it, or when it was scheduled before JIM recorded the trigger. |
| `DeletionTriggeredBySystemName` | `string` | That Connected System's name, captured when the deletion was scheduled so it survives the system's own deletion. |
| `ReasonCode` | `string` | Why the deletion rule marked the Metaverse Object for deletion. NoConnectorRemainsStateConvergence means JIM found it with nothing joined rather than through one system's disconnection, which is why DeletionTriggeredBySystemName is empty; NotSet when the deletion was scheduled before JIM recorded a reason. One of `NotSet`, `LastConnectorDisconnected`, `LastConnectorDisconnectedNoSourcesConfigured`, `AllAuthoritativeSourcesDisconnected`, `AuthoritativeSourceDisconnected`, `ExportCreateStaged`, `ExportUpdateStaged`, `ExportDeleteStaged`, `NoConnectorRemainsStateConvergence`, `GeneratedValueAlreadyInUse`, `GeneratedValueAnchoredElsewhere` or `GeneratedValueRenameAuthorised`. |

**With `-Count`**: an `int`.

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `TotalCount` | `int` | How many Metaverse Objects are pending deletion. |
| `DeprovisioningCount` | `int` | How many still have Connected System Objects joined, awaiting deprovisioning. |
| `AwaitingGracePeriodCount` | `int` | How many have nothing joined but are still within their grace period. |
| `ReadyForDeletionCount` | `int` | How many are due for deletion: their grace period has ended and nothing is joined. |
<!-- END GENERATED: Get-JIMPendingDeletion -->

### Examples

```powershell title="List pending deletions"
Get-JIMPendingDeletion
```

```powershell title="List pending deletions for one Object Type"
$userType = Get-JIMMetaverseObjectType -Name "User"
Get-JIMPendingDeletion -ObjectTypeId $userType.Id -PageSize 50
```

```powershell title="Count pending deletions"
Get-JIMPendingDeletion -Count
```

```powershell title="Summarise where pending deletions stand"
Get-JIMPendingDeletion -Summary
```

---

## :material-book-open-variant-outline: See also

- [Metaverse Object Types](metaverse-object-types.md): the Object Types Metaverse Objects belong to
- [Metaverse Attributes](metaverse-attributes.md): the attributes they hold, and Attribute Priority
- [Metaverse](../configuration/metaverse.md): Object Types, attributes, Metaverse Objects and pending deletions
- [Metaverse Objects](../configuration/metaverse-objects.md): where a value comes from, and why a Metaverse Object is connected where it is
- [Password Synchronisation](password-synchronisation.md): set a Metaverse Object's password, and follow its delivery
- [Generated Values](generated-values.md): the values JIM generated for a Metaverse Object
- [Synchronisation Rules](synchronisation-rules.md): the Attribute Flows that contribute values
