---
title: Pending Exports
description: "The cmdlet that reads the Pending Exports waiting to be written to a Connected System, and the attribute changes each carries."
type: reference
audience: automation-author
---

# Pending Exports

The Pending Export cmdlet reads the changes JIM has worked out for a Connected System and not yet written to it: one Pending Export per Connected System Object to create, update or delete, each with its attribute changes. How they arise and are exported is covered in [Pending Exports](../configuration/connected-systems.md#pending-exports).

---

## Get-JIMPendingExport

Gets the Pending Exports queued for a Connected System, and pages through the value changes of a large multi-valued attribute.

<!-- BEGIN GENERATED: Get-JIMPendingExport -->
### Syntax

```powershell
# List (default)
Get-JIMPendingExport -ConnectedSystemId <int> [-Search <string>] [-Page <int>] [-PageSize <int>]

# Count
Get-JIMPendingExport -ConnectedSystemId <int> -Count [-ChangeType <string>] [-Status <string>]

# ListAll
Get-JIMPendingExport -ConnectedSystemId <int> -All [-Search <string>] [-PageSize <int>] [-Force]

# QueuedBy
Get-JIMPendingExport -Id <guid> -QueuedBy

# AttributeChangesAll
Get-JIMPendingExport -Id <guid> -AttributeName <string> -All [-Search <string>] [-PageSize <int>] [-Force]

# AttributeChanges
Get-JIMPendingExport -Id <guid> -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMPendingExport -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (List, Count, ListAll) |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes (QueuedBy, AttributeChangesAll, AttributeChanges, ById) |  | The ID of the Pending Export to get. Accepts pipeline input by property name. |
| `QueuedBy` | `switch` | Yes (QueuedBy) |  | Return the runs that queued the Pending Export instead. Pending Exports listed by `Get-JIMPendingExport` can be piped straight in. |
| `AttributeName` | `string` | Yes (AttributeChangesAll, AttributeChanges) |  | With `-Id`, gets the value changes for this attribute a page at a time, for a multi-valued attribute such as member whose changes the Pending Export read by ID cuts short. |
| `Search` | `string` | No (List, ListAll, AttributeChangesAll, AttributeChanges) |  | Returns only the Pending Exports whose target object, source Metaverse Object or error matches this text; or with `-AttributeName`, only the changes whose value matches it. |
| `Page` | `int` | No (List, AttributeChanges) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No (List, ListAll, AttributeChangesAll, AttributeChanges) | `50` | How many Pending Exports or changes a page holds. From 1 to 100. |
| `All` | `switch` | Yes (ListAll, AttributeChangesAll) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (ListAll, AttributeChangesAll) |  | With `-All`, fetches every page, past the 1000-page limit. |
| `Count` | `switch` | Yes (Count) |  | Returns how many Pending Exports there are, rather than the Pending Exports. |
| `ChangeType` | `string` | No (Count) |  | With `-Count`, counts only the Pending Exports that make this kind of change. One of `Create`, `Update` or `Delete`. |
| `Status` | `string` | No (Count) |  | With `-Count`, counts only the Pending Exports with this status. One of `Pending`, `ExportNotConfirmed`, `Executing`, `Failed` or `Exported`. |

### Output

The Pending Exports, the one Pending Export, its value changes for `-AttributeName`, or with `-Count` how many there are. With `-QueuedBy`, one row per run that queued the Pending Export, newest first; a warning says when runs are no longer retained, or when earlier ones may be missing.

**With `-ConnectedSystemId`, or `-ConnectedSystemId` and `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the Pending Export. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the change is for. |
| `ChangeType` | `string` | Whether the Pending Export creates, updates or deletes the object. One of `Create`, `Update` or `Delete`. |
| `Status` | `string` | Where the Pending Export has got to: Pending, Executing, Exported (awaiting a confirming import), ExportNotConfirmed (to be retried), Failed (out of retries), or Parked (waiting on a decision about a generated value). One of `Pending`, `ExportNotConfirmed`, `Executing`, `Failed`, `Exported` or `Parked`. |
| `CreatedAt` | `datetime` | When this Pending Export was created. |
| `LastAttemptedAt` | `datetime` | When the last export attempt was made (UTC); empty before the first. |
| `NextRetryAt` | `datetime` | When the next export attempt is due (UTC); empty when none is. |
| `ErrorCount` | `int` | How many export attempts have failed. |
| `MaxRetries` | `int` | How many failed attempts are allowed before the Pending Export is marked Failed. |
| `LastErrorMessage` | `string` | The error from the last failed attempt; empty when none has failed. |
| `HasUnresolvedReferences` | `bool` | Whether any reference change cannot be written yet, because its target has no object in the Connected System. |
| `UnresolvedReferenceCount` | `int` | How many of the attribute changes are references still owed to the Connected System: their targets have no object there yet, so they were not written with the rest of the export. |
| `TargetObjectIdentifier` | `string` | The target Connected System Object's external identifier, when it has one. |
| `SourceMetaverseObjectId` | `guid` | The ID of the source Metaverse Object that triggered this export. |
| `SourceMetaverseObjectDisplayName` | `string` | The display name of the source Metaverse Object, if available. |
| `AttributeChangeCount` | `int` | The number of attribute value changes in this Pending Export. |
| `ConnectedSystemObjectId` | `guid` | The ID of the target Connected System Object, if available (for updates/deletes). |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the Pending Export. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the change is for. |
| `ConnectedSystemName` | `string` | The name of that Connected System. |
| `ChangeType` | `string` | Whether the Pending Export creates, updates or deletes the object. One of `Create`, `Update` or `Delete`. |
| `Status` | `string` | Where the Pending Export has got to: Pending, Executing, Exported (awaiting a confirming import), ExportNotConfirmed (to be retried), Failed (out of retries), or Parked (waiting on a decision about a generated value). One of `Pending`, `ExportNotConfirmed`, `Executing`, `Failed`, `Exported` or `Parked`. |
| `CreatedAt` | `datetime` | When the Pending Export was created (UTC). |
| `ErrorCount` | `int` | How many export attempts have failed. |
| `MaxRetries` | `int` | How many failed attempts are allowed before the Pending Export is marked Failed. |
| `LastAttemptedAt` | `datetime` | When the last export attempt was made (UTC); empty before the first. |
| `NextRetryAt` | `datetime` | When the next export attempt is due (UTC); empty when none is. |
| `LastErrorMessage` | `string` | The error from the last failed attempt; empty when none has failed. |
| `HasUnresolvedReferences` | `bool` | Whether any reference change cannot be written yet, because its target has no object in the Connected System. |
| `ConnectedSystemObjectId` | `guid` | The target Connected System Object, if one exists. |
| `ConnectedSystemObjectDisplayName` | `string` | The target Connected System Object's display name; empty when there is no target yet, or it has none. |
| `ConnectedSystemObjectTypeName` | `string` | The name of the target Connected System Object's Object Type. |
| `SourceMetaverseObjectId` | `guid` | The source Metaverse Object that triggered this export. |
| `SourceMetaverseObjectDisplayName` | `string` | The source Metaverse Object's display name; empty when there is none. |
| `SourceMetaverseObjectTypeName` | `string` | The name of the source Metaverse Object's Metaverse Object Type. |
| `AttributeChanges` | `object[]` | Attribute value changes (capped for multi-valued attributes). Each has `Id`, `AttributeId`, `AttributeName`, `ChangeType`, `Status`, `StringValue`, `DateTimeValue`, `IntValue`, `LongValue`, `DecimalValue`, `ByteValue`, `GuidValue`, `BoolValue`, `UnresolvedReferenceValue`, `ExportAttemptCount`, `SyncRuleId` and `SyncRuleName`. |
| `AttributeChangeSummaries` | `object[]` | Per-attribute metadata showing total change counts. Present when values have been capped so consumers know when changes have been truncated. Each has `AttributeName`, `TotalCount`, `ReturnedCount` and `HasMore`. |
| `UnresolvedReferences` | `object[]` | The reference changes not written yet, each with the reason, worked out against the Connected System as it stands when the Pending Export is read. Empty when there are none. Each has `AttributeChangeId`, `AttributeName`, `ReferencedMetaverseObjectId`, `ReferencedMetaverseObjectDisplayName` and `Reason`. |
| `ValueSources` | `object[]` | Where the value queued for each Connected System attribute came from, one entry per attribute whose export mapping can be resolved. Empty for a delete, which has no source Metaverse Object. Each has `ConnectedSystemAttributeId`, `IsComputed`, `IsGeneratedValue`, `Expression`, `SourceMetaverseAttributeId`, `SourceMetaverseAttributeName`, `Origin` and `HasSeveralOrigins`. |

**With `-Id` and `-AttributeName`, or `-Id` and `-AttributeName` and `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the change. |
| `AttributeId` | `int` | The ID of the Connected System attribute the change writes. |
| `AttributeName` | `string` | The name of that attribute. |
| `ChangeType` | `string` | What the change does to the attribute: Add a value, Update it, Remove a value, or RemoveAll values. One of `Add`, `Update`, `Remove` or `RemoveAll`. |
| `Status` | `string` | Where the change has got to: Pending, ExportedPendingConfirmation (exported, awaiting a confirming import), ExportedNotConfirmed (to be retried), or Failed. One of `Pending`, `ExportedPendingConfirmation`, `ExportedNotConfirmed` or `Failed`. |
| `StringValue` | `string` | The value of a Text attribute; empty otherwise. |
| `DateTimeValue` | `datetime` | The value of a DateTime attribute (UTC); empty otherwise. |
| `IntValue` | `int` | The value of a Number attribute; empty otherwise. |
| `LongValue` | `long` | The value of a LongNumber attribute; empty otherwise. |
| `DecimalValue` | `double` | The value of a Decimal attribute; empty otherwise. |
| `ByteValue` | `byte[]` | The value for Binary attributes. Serialised to JSON as a base64-encoded string (System.Text.Json's representation for byte arrays). |
| `GuidValue` | `guid` | The value of a Guid attribute; empty otherwise. |
| `BoolValue` | `bool` | The value of a Boolean attribute; empty otherwise. |
| `UnresolvedReferenceValue` | `string` | For a reference, the Connected System's identifier for the object it refers to, as text; empty otherwise. |
| `ExportAttemptCount` | `int` | How many times the change has been exported. |
| `SyncRuleId` | `int` | The ID of the export Synchronisation Rule whose mapping produced the change; empty when that rule has since been deleted. SyncRuleName survives it. |
| `SyncRuleName` | `string` | Snapshot of the contributing Synchronisation Rule's name at staging time. |

**With `-Count`**: an `int`.

**With `-QueuedBy`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The Run Profile Execution Item's id. |
| `ActivityId` | `guid` | The Activity (run) the item belongs to. |
| `Executed` | `datetime` | When the run executed (UTC). |
| `TargetType` | `string` | What the run targeted. One of `NotSet`, `ExampleDataTemplate`, `ConnectedSystem`, `ConnectedSystemRunProfile`, `SynchronisationRule`, `MetaverseObject`, `TrustedCertificate`, `ObjectMatchingRule`, `MetaverseAttribute`, `ServiceSetting`, `HistoryRetentionCleanup`, `MetaverseObjectType`, `System`, `Schedule`, `TemporalScopeReconciliation`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`, `SystemInitialisation`, `DataGeneration`, `Authentication`, `MetaverseObjectHousekeeping`, `ConnectedSystemObject` or `PasswordSynchronisation`. |
| `TargetName` | `string` | The run's name, typically the Run Profile's. |
| `TargetContext` | `string` | Where the run happened, typically its Connected System's name. |
| `ConnectedSystemId` | `int` | The Connected System the run was against, where it was against one. |
| `RunType` | `string` | The kind of run: Full Import, Delta Synchronisation, Export and so on. One of `NotSet`, `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `ObjectChangeType` | `string` | What the item did to its object. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `OutcomeSummary` | `string` | A short summary of the item's outcome, where one was recorded. |
| `ErrorType` | `string` | The error the item ended in, or empty where it succeeded. One of `NotSet`, `AmbiguousMatch`, `CouldNotMatchObjectType`, `CouldNotJoinDueToExistingJoin`, `DuplicateImportedAttributes`, `MissingExternalIdAttributeValue`, `UnexpectedAttribute`, `UnresolvedReference`, `UnsupportedExternalIdAttributeType`, `ExportNotConfirmed`, `ExportConfirmationFailed`, `CsoCreationFailed`, `DuplicateObject`, `InvalidGeneratedExternalId`, `UnhandledError`, `ExpressionEvaluationError`, `ExpressionMissingInput`, `MultiValuedToSingleValued`, `DeltaImportFallbackToFullImport`, `ImportHashVerificationFailed`, `ImportAttributeValueError`, `ConnectorConfigurationError`, `CouldNotExportDueToExistingConnectedSystemObject`, `ClassMembershipRequirementsNotMet`, `GeneratedValueExhausted`, `GeneratedValueWidthExceeded`, `GeneratedValueCollisionUnresolved`, `UniqueValueAlreadyInUse`, `ObjectMatchingRuleFailed`, `ObjectTypeChanged`, `PasswordRequiredToEnableAccount`, `AmbiguousProvisioningMatch` or `ScopingCriterionFailed`. |
| `DisplayName` | `string` | The name of the object the item was about, as recorded. |
| `ObjectType` | `string` | The object's type, as recorded. |
| `ExternalId` | `string` | The object's external ID, where it is a Connected System Object. |
| `ConnectedSystemObjectId` | `guid` | The Connected System Object the item was about, where it was about one. |
| `MetaverseObjectId` | `guid` | The Metaverse Object the item was about, where it was about one. |
| `Because` | `string` | The nearest cause in one plain-language line, exactly as the portal reads it: the first cause and how many more there are, or why there is none ("No earlier causes recorded", "What caused this is no longer retained"). |
| `NearestCauses` | `object[]` | The item's nearest causes, grouped into cohorts; at most three, with NearestCauseCount giving the total. Empty where nothing earlier was recorded or retained. Only this first step is read, so the members' own causes are empty here; read the causes of the item with this row's Id for the full chain. Each has `EdgeType`, `ReasonCode`, `SourceImportChangeType`, `MetaverseChangeType`, `ConnectedSystemId`, `ConnectedSystemName`, `SyncRuleId`, `SyncRuleName`, `ObjectTypeName`, `ObjectTypePluralName`, `AttributeName`, `EffectSyncOutcomeId`, `MemberCount` and `Members`. |
| `NearestCauseCount` | `int` | How many cohorts of nearest causes the item has in all. |
| `Resolution` | `string` | How the item's causes end: Resolved where it has any, NoFurtherCauses where nothing earlier was recorded, or CauseNotRetained where history retention has since removed what caused it. One of `Resolved`, `NoFurtherCauses`, `CauseNotRetained` or `DepthLimitReached`. |
<!-- END GENERATED: Get-JIMPendingExport -->

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

```powershell title="See which synchronisation runs queued a Pending Export, and why"
Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -QueuedBy |
    Format-Table Executed, TargetName, TargetContext, Because
```

```powershell title="Page through member additions on a group export"
Get-JIMPendingExport -Id "a1b2c3d4-..." -AttributeName "member" -Page 1 -PageSize 100
```

### Notes

- `-QueuedBy` rows have the same shape as an object's causal timeline in [`Get-JIMActivityExecutionItem`](execution-items.md), and their `Id` is the execution item's own, so they pipe straight into `Get-JIMActivityExecutionItem -Causes`. See [the Causality Explorer](../configuration/causality.md#the-causality-explorer).
- For large multi-valued attribute changes (adding hundreds of members to a group, say), use `-AttributeName` to page through the individual changes rather than loading them all at once.
- With `-Id`, `UnresolvedReferences` lists each reference change not yet written, with its `Reason`: `Resolvable` (written on the next export run), `AwaitingAnchor` (the referenced object is in this Connected System but has no anchor yet) or `NotInTargetSystem` (it has no Connected System Object here). See [Unresolved reference handling on export](../configuration/unresolved-references.md#on-export).
- `ValueSources` names where each attribute's queued value came from: an expression, chained mapping or generated value (`IsComputed` is `$true`), or the single Metaverse attribute it was read from, with that attribute's current `Origin`.

---

## See also

- [Connected System Objects](connected-system-objects.md): the objects the Pending Exports change
- [Run Profiles](run-profiles.md): run the exports that write Pending Exports to the Connected System
