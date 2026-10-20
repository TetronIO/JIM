---
title: Connected System Schema
description: "The cmdlets that import a Connected System's schema and choose which of its Object Types and attributes JIM manages."
type: reference
audience: automation-author
---

# Connected System Schema

The schema cmdlets import a Connected System's schema and choose which of its Object Types and attributes JIM manages. The auxiliary object classes an LDAP Connected System composes have [their own page](connected-system-auxiliary-classes.md). How the schema shapes what JIM synchronises is covered in [Connected Systems](../configuration/connected-systems.md).

---

## Import-JIMConnectedSystemSchema

Imports a Connected System's schema, its Object Types and their attributes, or refreshes one imported before.

<!-- BEGIN GENERATED: Import-JIMConnectedSystemSchema -->
### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemSchema -Id <int> [-Preview] [-DisableDependents] [-RemoveDependents] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Import-JIMConnectedSystemSchema -InputObject <PSCustomObject> [-Preview] [-DisableDependents] [-RemoveDependents] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System to import the schema of. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `Preview` | `switch` | No |  | Reads the schema and returns what a refresh would change, without changing anything. HasRemovalsOrDefinitionChanges flags the changes that can affect existing configuration: removals and changed attribute definitions. HasObjectTypeClassificationChanges flags a change to how the Connected System classifies a selected Object Type, each also written as a warning: applying one can move existing objects to another Object Type, which the next import refuses as an Object Type Changed error unless a Full Import Run Profile allows Object Type changes (`Set-JIMRunProfile -AllowObjectTypeChanges`). |
| `DisableDependents` | `switch` | No |  | Applies the refresh and disables what depends on what it removes or redefines: Synchronisation Rules bound to a removed Object Type, and Attribute Flows reading a removed or redefined attribute, each with the reason recorded. Each must be enabled again by hand. `-Preview` names them, in its Dependents property. Cannot be used with `-RemoveDependents`. |
| `RemoveDependents` | `switch` | No |  | Applies the refresh and deletes what depends on what it removes: the Synchronisation Rules and Attribute Flows are deleted, and a worker task obsoletes every Connected System Object of a removed Object Type, which deprovisions them as any deletion would, and deletes every stored value of a removed attribute. `-Preview` shows exactly what it would take, in its Dependents and RemovalImpact properties; follow the worker task with `Get-JIMWorkerTask`. Refused, with nothing changed, while a Scoping Criterion or an Expression reads a removed attribute; `-Preview` names each one in Dependents.RemovedAttributeReaders. Cannot be used with `-DisableDependents`. |
| `PassThru` | `switch` | No |  | Returns the Connected System with its refreshed schema. `-Preview` returns its result without it. |

### Output

With `-Preview`, what the refresh would change; with `-PassThru`, the Connected System with its refreshed schema; otherwise nothing. Derived Attribute Flows that disabling or removing dependents leaves, or would leave, with a missing input are written to the warning stream, as are changed Object Type classifications.

**With `-Preview`**:

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether the schema retrieval completed successfully. |
| `ErrorMessage` | `string` | Error message if the retrieval failed. |
| `TotalObjectTypes` | `int` | Total number of object types found in the schema. |
| `TotalAttributes` | `int` | Total number of attributes found across all object types. |
| `AddedObjectTypes` | `string[]` | Object types that are new to the schema. |
| `RemovedObjectTypes` | `string[]` | Object types the Connected System no longer reports. Retained in JIM, not deleted. |
| `UpdatedObjectTypes` | `string[]` | Object types that already existed and were updated. |
| `RenamedObjectTypes` | `object[]` | Object types the Connected System now reports under a name differing only in case. Each keeps its id and configuration and takes the new casing; it is not listed as added or removed. Each has `OldName` and `NewName`. |
| `AddedAttributes` | `hashtable` | Attributes that are new, grouped by object type name. |
| `RenamedAttributes` | `hashtable` | Attributes the Connected System now reports under a name differing only in case, grouped by object type name. Each keeps its id, so the Synchronisation Rule mappings and Object Matching Rules using it are unaffected, and takes the new casing; it is not listed as added or removed. |
| `RemovedAttributes` | `hashtable` | Attributes the Connected System no longer reports, grouped by object type name. Retained in JIM, not deleted; their values stop refreshing. |
| `ChangedAttributes` | `hashtable` | Attributes whose definition (data type or plurality) the Connector restated, grouped by object type name. |
| `ChangedObjectTypeClassifications` | `hashtable` | Changes to how the Connected System classifies a selected Object Type (its class kind, or inheritance involving another selected Object Type), grouped by Object Type name. They can change which Object Type existing entries resolve to at the next import; see HasObjectTypeClassificationChanges. |
| `ObjectTypesWithClassificationUpdates` | `string[]` | Existing Object Types whose classification the Connected System now reports differently in a way that cannot change which Object Type an entry resolves to (an unselected type, inheritance naming no other selected type, or a class kind change that does not make a class auxiliary or stop it being one). Applying the refresh records it; an Object Type listed in ChangedObjectTypeClassifications is not repeated here. |
| `AttributesInUse` | `hashtable` | Attributes no longer reported that are still referenced by Synchronisation Rules. Key is the attribute name, value is the list of Synchronisation Rule names that reference it. |
| `BlockedCredentialAttributes` | `hashtable` | Credential attributes found in the schema and blocked from management, grouped by object type name. |
| `DiscoveryWarnings` | `string[]` | Discovery shortfalls the Connector worked around rather than failed on. A partial read can make entries appear removed when they are not; check these before committing a refresh. |
| `PasswordPolicyDiscovered` | `bool` | Whether a password policy was read from the Connected System during the retrieval. |
| `HasChanges` | `bool` | Whether there were any changes to the schema. |
| `HasRemovalsOrDefinitionChanges` | `bool` | Whether the refresh found changes that can invalidate existing configuration or leave stale data behind: removals and attribute definition changes. Additions never set this. |
| `HasObjectTypeClassificationChanges` | `bool` | Whether the refresh changes how the Connected System classifies a selected Object Type, which can change which Object Type existing entries resolve to. By default each object that would change Object Type is refused at the next import, reported as an Object Type Changed error and kept as it is; a Full Import Run Profile with `allowObjectTypeChanges` enabled carries the change out. Not a destructive change: cancelling keeps things as they are, so it never sets HasRemovalsOrDefinitionChanges. |
| `Dependents` | `object` | What the destructive changes invalidate: the Synchronisation Rules and Attribute Flow mappings that would be disabled by committing with `disableDependents` (or deleted by committing with `removeDependents`), each with its reason, plus Object Matching Rules needing attention. Empty when the refresh carries no destructive changes. It has `InvalidatedSyncRules`, `InvalidatedMappings`, `ReferencedObjectMatchingRules`, `DependentDerivedFlows`, `RemovedAttributeReaders`, `RemovalRefused` and `HasAny`. |
| `RemovalImpact` | `object` | What committing with `removeDependents` would remove, counted: Connected System Objects per removed Object Type (marked Obsolete and deprovisioned through the standard pipeline) and stored values per removed attribute (deleted). Empty when the refresh carries no destructive changes. It has `RemovedObjectTypes`, `RemovedAttributes`, `TotalConnectedSystemObjects` and `TotalStoredValues`. |

**With `-PassThru`**:

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
| `ConfigurationDrift` | `object` | Whether the configuration has changed in a way that needs a Full Synchronisation to take effect. Empty on the create and update responses, which describe the write that just happened rather than the system's readiness. It has `HasPendingChanges`, `IsDeterminable`, `NeverFullySynchronised`, `TrackingDisabled`, `LastFullSynchronisation`, `MostRecentChange`, `ChangeCount` and `HighestChangeClass`. |
| `StrandedValueSweepArmedAt` | `datetime` | When a stranded-value sweep was armed by a Connector Space clear, or empty when no sweep is armed. The sweep runs at the first Full Synchronisation of this Connected System after LastSuccessfulFullImportCompletedAt is later than this timestamp; until then it stays armed and a Full Synchronisation does nothing beyond ordinary synchronisation. |
| `LastSuccessfulFullImportCompletedAt` | `datetime` | When the most recent Full Import of this Connected System completed successfully, or empty if none ever has. An import that completed with object-level errors does not count. |
| `DependentDerivedFlows` | `object[]` | On the response to an import-schema call that disabled or removed its dependents only: the Metaverse-Derived Attribute Flows, on any Connected System, that disabling or removing those Synchronisation Rules and mappings left with a missing input (an attribute they read lost its last enabled contributor). The refresh went ahead regardless; each flow's Missing Input Behaviour now decides what it contributes. Always present; empty on every other response. Each has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`. |
<!-- END GENERATED: Import-JIMConnectedSystemSchema -->

### Examples

```powershell title="Import schema for a Connected System"
Import-JIMConnectedSystemSchema -Id 3
```

```powershell title="Preview a refresh, then apply only when nothing was removed or redefined"
$preview = Import-JIMConnectedSystemSchema -Id 3 -Preview
if (-not $preview.HasRemovalsOrDefinitionChanges) {
    Import-JIMConnectedSystemSchema -Id 3 -Confirm:$false
}
```

```powershell title="Apply a destructive refresh with its dependents disabled"
$preview = Import-JIMConnectedSystemSchema -Id 3 -Preview
$preview.Dependents.InvalidatedSyncRules | Select-Object SyncRuleName, Reason
Import-JIMConnectedSystemSchema -Id 3 -DisableDependents -Confirm:$false
```

```powershell title="Apply a destructive refresh with its dependents removed, deleting the previewed configuration and data"
# Inspect what the removal would delete before committing: every named rule and mapping is deleted,
# every counted object is obsoleted and deprovisioned, and every counted stored value is deleted.
$preview = Import-JIMConnectedSystemSchema -Id 3 -Preview
$preview.Dependents.InvalidatedSyncRules | Select-Object SyncRuleName, Reason
$preview.RemovalImpact.RemovedObjectTypes | Select-Object ObjectTypeName, ConnectedSystemObjectCount
$preview.RemovalImpact.RemovedAttributes | Select-Object AttributeName, StoredValueCount

Import-JIMConnectedSystemSchema -Id 3 -RemoveDependents
Get-JIMWorkerTask   # the data removal runs as a background worker task
```

```powershell title="Pipeline: create a system, then import its schema"
New-JIMConnectedSystem "LDAP Directory" -ConnectorDefinitionId 1 -PassThru |
    Import-JIMConnectedSystemSchema -PassThru
```

### Notes

- Nothing is deleted unless you give `-RemoveDependents`, and then only what the preview listed. See [Refreshing the schema](../configuration/connected-system-schema.md#refreshing-the-schema).
- Check the preview's `DiscoveryWarnings` before applying: a partial schema read (for example, missing permissions) can make entries appear removed when they are not.
- Supports `ShouldProcess` (Medium impact). `-Preview` bypasses it; a preview changes nothing, so there is nothing to confirm.

---

## Get-JIMConnectedSystemObjectType

Gets the Object Types in a Connected System's schema, with their attributes.

Object Types the Connected System classified as internal (a directory's own configuration and operational classes) are omitted by default, matching what the portal's Schema tab shows. Pass `-IncludeInternal` to return them as well. An Object Type that is already selected is always returned, whatever its classification.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectType -->
### Syntax

```powershell
Get-JIMConnectedSystemObjectType -ConnectedSystemId <int> [-IncludeInternal]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `IncludeInternal` | `switch` | No |  | Also returns the Object Types the Connected System marks as internal. |

### Output

The Object Types, each with its attributes. An attribute whose Writability is ReadOnly can be imported but not exported; one that is WritableOnCreate is exported only when JIM creates the object, because changing it would break the link to the object (a table's primary key, a directory entry's relative distinguished name).

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
<!-- END GENERATED: Get-JIMConnectedSystemObjectType -->

### Examples

```powershell title="Get the Object Types of a Connected System"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3
```

```powershell title="List the attributes JIM may only set when it creates the object"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3 |
    ForEach-Object { $_.attributes } |
    Where-Object { $_.writability -eq 'WritableOnCreate' } |
    Select-Object name, type
```

```powershell title="Include the directory's own internal Object Types"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3 -IncludeInternal
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemObjectType
```

```powershell title="List the selected Object Types only"
Get-JIMConnectedSystem -Id 3 |
    Get-JIMConnectedSystemObjectType |
    Where-Object { $_.Selected }
```

---

## Set-JIMConnectedSystemObjectType

Changes whether JIM manages a Connected System Object Type, and what happens to the values its objects contributed when they are deleted.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemObjectType -->
### Syntax

```powershell
Set-JIMConnectedSystemObjectType -ConnectedSystemId <int> -ObjectTypeId <int> [-Selected <bool>] [-RemoveContributedAttributesOnObsoletion <bool>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Object Type to change. Accepts pipeline input by property name, `ObjectTypeId` or `Id`. |
| `Selected` | `bool` | No |  | Whether JIM manages objects of this type. Deselecting it takes the type out of management: the next Full Import no longer returns its objects, so marks them deleted, and the synchronisation after that disconnects them from their Metaverse Objects and applies the Metaverse Object Type's deletion rules, as deselecting a partition does. Refused while an enabled Synchronisation Rule uses the Object Type; disable those rules first. |
| `RemoveContributedAttributesOnObsoletion` | `bool` | No |  | Whether the Metaverse attribute values an object of this type contributed are recalled when the object is deleted from the Connected System. |
| `PassThru` | `switch` | No |  | Returns the Object Type as changed. |

### Output

With `-PassThru`, the Object Type as changed; otherwise nothing.

Properties as under [Get-JIMConnectedSystemObjectType](#get-jimconnectedsystemobjecttype).
<!-- END GENERATED: Set-JIMConnectedSystemObjectType -->

### Examples

```powershell title="Select an Object Type for synchronisation"
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 1 -Selected $true
```

```powershell title="Take an Object Type out of management (obsoletes every object of that type on the next Full Import)"
# Deselecting is refused while an enabled Synchronisation Rule is bound to the type, so disable those first.
# Run the first line without the final Set-JIMSyncRule stage to see which rules it would disable.
Get-JIMSyncRule -ConnectedSystemId 3 | Where-Object { $_.connectedSystemObjectTypeId -eq 2 } | Set-JIMSyncRule -Disable
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 2 -Selected $false
```

Deselecting an Object Type takes it out of management: the next Full Import marks every Connected System Object of that type obsolete, and the following synchronisation disconnects them from their Metaverse Objects, which may leave those eligible for deletion. Preview it first with `New-JIMConfigurationChangePreview -ConnectedSystemId 3 -SchemaObjectType @(@{ objectTypeId = 2; selected = $false }) -Wait`, and see [What deselecting means](../configuration/connected-system-schema.md#what-deselecting-means).

### Notes

- Supports `ShouldProcess` (Medium impact).
- Selecting an Object Type is refused, with the Connector's own message, when the Connected System's settings cannot serve it: for the JIM SQL Connector, selecting an Object Type that lacks a `watermarkColumn` or a `changeLog` while the matching Delta Import Mode is set.
- Deselecting an Object Type is refused while an enabled Synchronisation Rule is bound to it; the error names the rules to disable. The same check refuses any change to a deselected Object Type that an enabled rule is still bound to.

---

## Set-JIMConnectedSystemAttribute

Updates the selection, external ID configuration, data type and case-exact declaration of attributes on a Connected System Object Type. Supports updating a single attribute or multiple attributes in bulk.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemAttribute -->
### Syntax

```powershell
# Single (default)
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int> -AttributeId <int> [-Selected <bool>] [-IsExternalId <bool>] [-IsSecondaryExternalId <bool>] [-Type <string>] [-Plurality <string>] [-CaseExact <bool>] [-DateTimeResolution <string>] [-PassThru] [-WhatIf] [-Confirm]

# Bulk
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int> -AttributeUpdates <hashtable> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Object Type the attributes belong to. |
| `AttributeId` | `int` | Yes (Single) |  | The ID of the attribute to change. Accepts pipeline input by property name, `AttributeId` or `Id`. |
| `AttributeUpdates` | `hashtable` | Yes (Bulk) |  | The attributes to change, as a hashtable keyed by attribute ID whose values are hashtables of the settings to change (selected, isExternalId, isSecondaryExternalId, caseExact, dateTimeResolution); the examples show one. |
| `Selected` | `bool` | No (Single) |  | Whether JIM imports and exports the attribute. |
| `IsExternalId` | `bool` | No (Single) |  | Whether the attribute uniquely and permanently identifies an object of this type, such as objectGUID or an employee ID. |
| `IsSecondaryExternalId` | `bool` | No (Single) |  | Whether the Connected System also identifies an object by the attribute, as an LDAP directory does by distinguished name. |
| `Type` | `string` | No (Single) |  | Replaces the data type inferred from the schema. Accepted only where the Connector says its schema cannot state a type for certain: a delimited file names no types, and Oracle has one numeric type, so a NUMBER column may be a whole number, a counter or a fractional figure. Refused once a Synchronisation Rule uses the attribute or it holds values. Integer is the Number data type, as in `New-JIMMetaverseAttribute`. A Boolean holds one value, so Boolean also makes the attribute single-valued. Cannot be set with `-AttributeUpdates`. One of `Text`, `Integer`, `LongNumber`, `Decimal`, `DateTime`, `Boolean`, `Reference`, `Guid` or `Binary`. |
| `Plurality` | `string` | No (Single) |  | Whether the attribute holds one value or several. Accepted only where the Connector cannot read it from the Connected System's schema: a delimited file's header names columns and nothing more, so its columns are discovered single-valued, and a MultiValued column holds its values separated by the Multi-Value Delimiter. Setting it makes the plurality the administrator's, so a schema refresh keeps it. Refused once a Synchronisation Rule uses the attribute or it holds values, and for a Boolean, which holds one value. Cannot be set with `-AttributeUpdates`. One of `SingleValued` or `MultiValued`. |
| `CaseExact` | `bool` | No (Single) |  | Whether the attribute's values are case-exact: `$true` when values differing only in case identify different things, `$false` when they are the same value. Only a Text attribute can be declared case-exact. Setting it makes the declaration the administrator's, so a schema refresh keeps it. Offered on every Connector. |
| `DateTimeResolution` | `string` | No (Single) |  | The finest step of time the Connected System holds this DateTime attribute's values to. JIM sends each date floored to it and compares at it, so a difference the Connected System cannot hold is never exported again and again. Discovered by the Connector (Second for Active Directory and SCIM, a SQL column's own precision); setting it makes the resolution the administrator's, so a schema refresh keeps it. Only a DateTime attribute has one. One of `Microsecond`, `TenMicroseconds`, `HundredMicroseconds`, `Millisecond`, `TenMilliseconds`, `HundredMilliseconds`, `Second`, `Minute` or `Day`. |
| `PassThru` | `switch` | No |  | Returns the attribute as changed, or with `-AttributeUpdates`, the outcome of the change. |

### Output

With `-PassThru`, the attribute as changed, or with `-AttributeUpdates`, what was changed and what failed; otherwise nothing. An attribute `-AttributeUpdates` could not change is written to the warning stream.

**With `-AttributeId`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the attribute. |
| `Name` | `string` | The name of the attribute, as the Connected System calls it. |
| `Description` | `string` | The Connected System's description of the attribute; empty when it gives none. |
| `ClassName` | `string` | On a Connected System whose classes inherit, such as an LDAP directory, the class the attribute comes from; empty elsewhere. |
| `Created` | `datetime` | When JIM first imported the attribute (UTC). |
| `Type` | `string` | The type of value the attribute holds, such as Text, Number or Reference. |
| `TypeSetByAdministrator` | `bool` | Whether Type was chosen by an administrator rather than inferred by schema discovery. A chosen type is left alone by a schema refresh; an inferred one is restated from the Connector. |
| `AttributePlurality` | `string` | Whether the attribute holds one value (SingleValued) or several (MultiValued). |
| `PluralitySetByAdministrator` | `bool` | Whether AttributePlurality was set by an administrator rather than discovered. A schema refresh keeps a plurality an administrator set, and restates a discovered one. |
| `CaseExact` | `bool` | Whether the attribute's values are case-exact: when JIM asks whether two values identify the same thing, `JBloggs` and `jbloggs` are different values. False means they are the same value. Only Text attributes carry the declaration. |
| `CaseExactSetByAdministrator` | `bool` | Whether an administrator set the case-exact declaration, rather than it being discovered from the Connected System's schema. A schema refresh keeps an administrator's declaration and restates a discovered one. |
| `DateTimeResolution` | `string` | For a DateTime attribute, the finest step of time the Connected System holds its values to: Microsecond, TenMicroseconds, HundredMicroseconds, Millisecond, TenMilliseconds, HundredMilliseconds, Second, Minute or Day. JIM sends each date floored to it, and compares at it. Empty for any other type, and for a date attribute discovered before resolutions were recorded, which JIM treats as Microsecond until the schema is refreshed. |
| `DateTimeResolutionSetByAdministrator` | `bool` | Whether an administrator set the resolution, rather than it being discovered from the Connected System's schema. A schema refresh keeps an administrator's resolution and restates a discovered one. |
| `Selected` | `bool` | Whether JIM imports and exports the attribute. |
| `IsExternalId` | `bool` | Whether the attribute uniquely and permanently identifies an object of this type in the Connected System. |
| `IsSecondaryExternalId` | `bool` | Whether the Connected System also identifies an object of this type by the attribute, as an LDAP directory does by distinguished name. |
| `Required` | `bool` | Whether the Connected System's schema requires this attribute for its class (an RFC 4512 MUST). JIM refuses an export that would add a class whose required attributes have no value. |
| `SelectionLocked` | `bool` | Indicates if this attribute's selection state is locked and cannot be changed. This is true for External ID and Secondary External ID attributes. |
| `Writability` | `string` | Indicates whether this attribute can be written to in the Connected System. One of `Writable`, `ReadOnly` or `WritableOnCreate`. `ReadOnly` attributes can be imported but cannot be targeted by export Attribute Flows. `WritableOnCreate` attributes can be targeted, but only ever flow on a Create Pending Export. |
| `IsOperational` | `bool` | Whether the attribute is one the Connected System maintains for its own operation, such as an LDAP directory's `createTimestamp` or `pwdFailureTime`. Always read-only, and never selected until an administrator chooses it. |
| `ReferencedObjectTypeId` | `int` | For a Reference attribute, the id of the Object Type this reference points at, when the Connected System's schema declares one (the SQL Connector's `referencesObjectType`). Import reference resolution resolves the reference within that Object Type alone; empty means the schema does not say and resolution requires the value to be unambiguous across Object Types. |
| `ReferencedObjectTypeName` | `string` | The name of the Object Type identified by ReferencedObjectTypeId, when declared. |

**With `-AttributeUpdates`**:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The activity ID for the bulk update operation. |
| `UpdatedCount` | `int` | Number of attributes successfully updated. |
| `UpdatedAttributes` | `object[]` | List of updated attributes. Each has `Id`, `Name`, `Description`, `ClassName`, `Created`, `Type`, `TypeSetByAdministrator`, `AttributePlurality`, `PluralitySetByAdministrator`, `CaseExact`, `CaseExactSetByAdministrator`, `DateTimeResolution`, `DateTimeResolutionSetByAdministrator`, `Selected`, `IsExternalId`, `IsSecondaryExternalId`, `Required`, `SelectionLocked`, `Writability`, `IsOperational`, `ReferencedObjectTypeId` and `ReferencedObjectTypeName`. |
| `Errors` | `object[]` | Any errors that occurred during the update. Empty if no errors. Each has `AttributeId` and `ErrorMessage`. |
<!-- END GENERATED: Set-JIMConnectedSystemAttribute -->

### Examples

```powershell title="Select a single attribute"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 5 -Selected $true
```

```powershell title="Mark an attribute as the primary external ID"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 10 -IsExternalId $true
```

```powershell title="Correct the data type of an Oracle NUMBER column"
# Oracle has one numeric type, so a NUMBER(10) employee identifier is read as a Long Number by default.
# Recording it as a whole number lets it flow into the built-in Employee Number Metaverse Attribute.
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 5 -Type Integer
```

```powershell title="Declare an attribute's values case-exact"
# The source system treats JBloggs and jbloggs as two different codes.
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 7 -CaseExact $true
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
- Only one attribute per Object Type can be the primary external ID. Setting `IsExternalId` on an attribute automatically clears it from the previous primary.
- `-CaseExact` is accepted on every Connector, and makes the declaration yours: a schema refresh keeps it rather than restating what the Connected System's schema says. See [Case-exact values](../configuration/connected-system-attributes.md#case-exact-values).

---

## See also

- [Connected Systems](connected-systems.md): create, change and delete the Connected Systems whose schema these cmdlets manage
- [Auxiliary Object Classes](connected-system-auxiliary-classes.md): the auxiliary classes an LDAP Connected System's Object Types carry
- [Synchronisation Rules](synchronisation-rules.md): the rules that flow values between the selected Object Types and the Metaverse
