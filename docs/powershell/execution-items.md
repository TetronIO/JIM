---
title: Execution Items
description: "The cmdlets that read what happened to one object in a Run Profile execution, what caused it, and what it went on to cause."
type: reference
audience: automation-author
---

# Execution Items

A Run Profile execution records one execution item (a Run Profile Execution Item) for each object it changed or failed to change. These cmdlets read one item in full, with its error message when the object failed, and follow the chain of causes behind it, or the consequences it went on to have, one cohort of objects at a time: the same story the [Lineage view](../configuration/causality.md) tells. List a run's items with [`Get-JIMActivity -ExecutionItems`](activities.md#get-jimactivity).

---

## Get-JIMActivityExecutionItem

Retrieves one execution item of a Run Profile activity: what happened to one object and, when it failed, why. The list returned by `Get-JIMActivity -ExecutionItems` names each failed item's error type; this cmdlet returns the error message and stack trace as well, which is usually what tells you how to fix it. For a generated value that ran out, for example, the message names the attribute, the last value tried and what already holds it.

With `-Causes` it returns what caused the item instead, and with `-Consequences` what the item went on to cause, in later runs as well as its own: the same story the [Lineage view](../configuration/causality.md) tells, one row per cohort.

<!-- BEGIN GENERATED: Get-JIMActivityExecutionItem -->
### Syntax

```powershell
# Item (default)
Get-JIMActivityExecutionItem -Id <guid>

# Consequences
Get-JIMActivityExecutionItem -Id <guid> -Consequences

# Causes
Get-JIMActivityExecutionItem -Id <guid> -Causes
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The execution item to get. The rows of `Get-JIMActivity -ExecutionItems` can be piped straight in. Accepts pipeline input by property name. |
| `Causes` | `switch` | Yes (Causes) |  | Return what caused the item instead, one row per cohort of causes. |
| `Consequences` | `switch` | Yes (Consequences) |  | Return what the item went on to cause instead, one row per cohort of consequences. |

### Output

Without a switch, the item. With `-Causes`, what caused it, and with `-Consequences` what it went on to cause, in later runs as well as its own: one row per cohort (objects sharing a cause and its reason read as one row with a Count), with Level 1 for the nearest and one more per step.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The item's unique identifier. |
| `ActivityId` | `guid` | The Run Profile Activity the item belongs to. |
| `ObjectChangeType` | `string` | What happened to the object. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `NoChangeReason` | `string` | Why nothing changed, when nothing did. One of `NotSet`, `MvoNoAttributeChanges` or `CsoAlreadyCurrent`. |
| `ConnectedSystemObjectId` | `guid` | The Connected System Object the item is about, when it still exists. |
| `MetaverseObjectId` | `guid` | The Metaverse Object the item is about, where it records one (kept as history; it may since have been deleted). |
| `PendingExportId` | `guid` | The Pending Export the item is about, for an export. |
| `ExternalIdSnapshot` | `string` | The object's external ID when the item was recorded. |
| `DisplayNameSnapshot` | `string` | The object's display name when the item was recorded. |
| `ObjectTypeSnapshot` | `string` | The object's type name when the item was recorded. |
| `ErrorType` | `string` | The kind of failure; `NotSet` or empty when the object did not fail. One of `NotSet`, `AmbiguousMatch`, `CouldNotMatchObjectType`, `CouldNotJoinDueToExistingJoin`, `DuplicateImportedAttributes`, `MissingExternalIdAttributeValue`, `UnexpectedAttribute`, `UnresolvedReference`, `UnsupportedExternalIdAttributeType`, `ExportNotConfirmed`, `ExportConfirmationFailed`, `CsoCreationFailed`, `DuplicateObject`, `InvalidGeneratedExternalId`, `UnhandledError`, `ExpressionEvaluationError`, `ExpressionMissingInput`, `MultiValuedToSingleValued`, `DeltaImportFallbackToFullImport`, `ImportHashVerificationFailed`, `ImportAttributeValueError`, `ConnectorConfigurationError`, `CouldNotExportDueToExistingConnectedSystemObject`, `ClassMembershipRequirementsNotMet`, `GeneratedValueExhausted`, `GeneratedValueWidthExceeded`, `GeneratedValueCollisionUnresolved`, `UniqueValueAlreadyInUse`, `ObjectMatchingRuleFailed`, `ObjectTypeChanged`, `PasswordRequiredToEnableAccount`, `AmbiguousProvisioningMatch` or `ScopingCriterionFailed`. |
| `ErrorMessage` | `string` | What went wrong, in words; empty when the object did not fail. |
| `ErrorStackTrace` | `string` | The stack trace, when the failure came from an exception. |
| `AttributeFlowCount` | `int` | How many Metaverse attributes changed alongside a join, projection or disconnection. |
| `UnresolvedReferenceCount` | `int` | How many of the object's reference values the import could not resolve to a Connected System Object, under any Unresolved Reference Handling and whatever error the item carries; empty when none were left unresolved. The values themselves are on the Connected System Object. |
| `AttributeValueErrors` | `object[]` | Every value the import found on the object but could not read as its attribute's type, in the order the Connector reported them; JIM left each attribute as it already held it. Empty when there were none. Each has `AttributeName`, `AttributeType`, `Value` and `Reason`. |
| `AttributeValueErrorTotal` | `int` | How many values could not be read, which can exceed AttributeValueErrors where the item keeps only the first thousand; empty when there were none. |
| `SourceLocation` | `string` | Where in the Connected System the object was read from, such as a file and row, where the Connector says. |
| `OutcomeSummary` | `string` | Outcome types with counts, for example `Projected:1,AttributeFlow:12`. |
| `MatchCandidates` | `object[]` | For an `AmbiguousMatch`, the Metaverse Objects the match found, and for an `AmbiguousProvisioningMatch`, the Connected System Objects: up to 10, ordered by display name, each with whether it still exists and, for a Metaverse Object, what is joined to it now. Empty for any other item. Each has `CandidateKind`, `CandidateId`, `DisplayNameSnapshot`, `TypeNameSnapshot`, `ConnectedSystemId`, `ConnectedSystemNameSnapshot`, `MatchedValue`, `ObjectMatchingRuleId`, `ObjectMatchingRuleDescriptionSnapshot`, `Ordinal`, `CandidateTotal`, `Exists` and `JoinedObjects`. |
| `JoinObjectMatchingRuleId` | `int` | For an object joined to an existing Metaverse Object by matching, the id of the Object Matching Rule that found it; empty for any other item. The rule may since have been changed or deleted; the description below records what it compared at the time. |
| `JoinObjectMatchingRuleDescriptionSnapshot` | `string` | For an object joined by matching, what the Object Matching Rule compared when the join was made, for example `Employee ID equals employeeId`; empty for any other item. |
| `JoinMatchedValue` | `string` | For an object joined by matching, the value the join matched on: what the rule computed from the object, with several separated by commas for a multi-valued source. Empty for any other item. |

**With `-Causes`**:

| Property | Type | Description |
|----------|------|-------------|
| `ExecutionItemId` | `guid` | The execution item the walk started from. |
| `Level` | `int` | How many steps back from the item: 1 for its direct causes. |
| `EdgeType` | `string` | The recorded cause; empty for a step to the import that fed a synchronisation, or to the Metaverse Object's creation. One of `MetaverseObjectDeletionCausedDeprovision`, `MetaverseObjectDeletionCausedReferenceRemoval`, `ExportCausedImportConfirmation`, `PendingExportQueueingCausedExportExecution`, `ExportRejectionCausedGeneratedValueRevision` or `MetaverseObjectDeletionScheduleCausedDeletion`. |
| `SourceImportChangeType` | `string` | For the step to the import that fed a synchronisation, what that import did: Added, Updated or Deleted. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `MetaverseChangeType` | `string` | For the step to the Metaverse Object's creation: Projected, Joined or Created. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `ReasonCode` | `string` | Why the cause produced its effect; NotSet where no reason was recorded. One of `NotSet`, `LastConnectorDisconnected`, `LastConnectorDisconnectedNoSourcesConfigured`, `AllAuthoritativeSourcesDisconnected`, `AuthoritativeSourceDisconnected`, `ExportCreateStaged`, `ExportUpdateStaged`, `ExportDeleteStaged`, `NoConnectorRemainsStateConvergence`, `GeneratedValueAlreadyInUse`, `GeneratedValueAnchoredElsewhere` or `GeneratedValueRenameAuthorised`. |
| `Count` | `int` | How many causes the cohort holds. |
| `ObjectType` | `string` | The causes' object type, plural where there are several. |
| `Attribute` | `string` | The reference attribute the causes acted through, where one applies. |
| `ConnectedSystem` | `string` | The Connected System the causes are attributed to, as named when recorded. |
| `SyncRule` | `string` | The Synchronisation Rule responsible, as named when recorded. |
| `Members` | `string[]` | The causes' names, as recorded. |
| `Resolution` | `string` | Why the walk stopped at these causes, for example CauseNotRetained; empty where each carries on to the next level. |

**With `-Consequences`**:

| Property | Type | Description |
|----------|------|-------------|
| `ExecutionItemId` | `guid` | The execution item the walk started from. |
| `Level` | `int` | How many steps on from the item: 1 for what it directly caused. A Level 0 row gives the item's own ending where it caused nothing. |
| `HopKind` | `string` | How the consequences were reached: Edge (a recorded cause), Confirmation (the import confirming an export), NextSynchronisation (the synchronisation after an import), OutstandingPendingExport or ScheduledDeletion. One of `Edge`, `Confirmation`, `NextSynchronisation`, `OutstandingPendingExport` or `ScheduledDeletion`. |
| `EdgeType` | `string` | The recorded cause, for an Edge or Confirmation hop. One of `MetaverseObjectDeletionCausedDeprovision`, `MetaverseObjectDeletionCausedReferenceRemoval`, `ExportCausedImportConfirmation`, `PendingExportQueueingCausedExportExecution`, `ExportRejectionCausedGeneratedValueRevision` or `MetaverseObjectDeletionScheduleCausedDeletion`. |
| `ReasonCode` | `string` | Why the cause produced these consequences; NotSet for any other hop. One of `NotSet`, `LastConnectorDisconnected`, `LastConnectorDisconnectedNoSourcesConfigured`, `AllAuthoritativeSourcesDisconnected`, `AuthoritativeSourceDisconnected`, `ExportCreateStaged`, `ExportUpdateStaged`, `ExportDeleteStaged`, `NoConnectorRemainsStateConvergence`, `GeneratedValueAlreadyInUse`, `GeneratedValueAnchoredElsewhere` or `GeneratedValueRenameAuthorised`. |
| `Count` | `int` | How many consequences the cohort holds, counted exactly. |
| `ObjectType` | `string` | The consequences' object type, plural where there are several. |
| `Attribute` | `string` | The reference attribute a removal acted through, where one applies. |
| `ConnectedSystem` | `string` | The Connected System the consequences are attributed to. |
| `SyncRule` | `string` | The Synchronisation Rule the cause names, where it names one. |
| `OutcomeSummary` | `string` | The outcomes the consequences share. |
| `PendingExportStatus` | `string` | For outstanding Pending Exports, their live status. One of `Pending`, `ExportNotConfirmed`, `Executing`, `Failed`, `Exported` or `Parked`. |
| `Ending` | `string` | Where the branch stops: the cohort's own ending (Confirmed, WaitingForExport, WaitingForRetry, NeedsAttention, WaitingForConfirmingImport or DeletionScheduled), else how many consequences led nowhere further and why, for example "NoFurtherConsequences (2)", else FurtherThanShown where the walk reached its bound. Empty where every consequence carries on. |
| `FirstOccurred` | `datetime` | When the earliest consequence happened, in UTC. |
| `LastOccurred` | `datetime` | When the latest consequence happened, in UTC. |
| `DueAt` | `datetime` | When the soonest outstanding work is due, in UTC: a retry, or a deletion becoming eligible. |
| `Members` | `string[]` | The first consequences' names; `Get-JIMCausalCohortMember` pages through them all. |
| `CohortKey` | `string` | The cohort's key, which `Get-JIMCausalCohortMember` pages its members with. |
<!-- END GENERATED: Get-JIMActivityExecutionItem -->

The snapshots record the object as it was when the item was recorded, so they still describe a Connected System Object that has since been deleted. For the object's current state, look it up by `ConnectedSystemObjectId` or `MetaverseObjectId`.

An object can join only one Metaverse Object, so when its [Object Matching Rule](../configuration/object-matching-rules.md) finds several, the object is neither joined nor projected. When matching before provisioning finds several existing Connected System Objects, JIM neither joins nor provisions the Metaverse Object. Each candidate in `MatchCandidates` carries:

| Property | Description |
|----------|-------------|
| `CandidateKind` | `MetaverseObject` or `ConnectedSystemObject`. |
| `CandidateId` | The candidate's ID. |
| `DisplayNameSnapshot`, `TypeNameSnapshot` | The candidate's display name and type when the item was recorded. |
| `ConnectedSystemId`, `ConnectedSystemNameSnapshot` | For a Connected System Object candidate, its Connected System. |
| `MatchedValue` | The value or values the candidate matched on. |
| `ObjectMatchingRuleId`, `ObjectMatchingRuleDescriptionSnapshot` | The rule, and what it compared, for example `Employee ID equals employeeNumber`. |
| `Ordinal` | The candidate's position in the list. |
| `CandidateTotal` | How many candidates the match found in all, which can be more than the 10 listed. |
| `Exists` | Whether the candidate still exists. |
| `JoinedObjects` | For a Metaverse Object candidate, the Connected System Objects joined to it now, each with `ConnectedSystemObjectId`, `ConnectedSystemId`, `ConnectedSystemName`, `TypeName`, `DisplayName` and `ExternalId`. |

Consequences can still be happening, so `-Consequences` is a reading taken at the time of the call: an `Ending` that starts with `Waiting` or reads `DeletionScheduled` will move on without anyone acting, and a later call can show more. `NeedsAttention` will not; open the Pending Export it names.

### Examples

```powershell title="Get one execution item"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901"
```

```powershell title="List why each object in a run failed"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
    Get-JIMActivityExecutionItem |
    Format-List DisplayNameSnapshot, ErrorType, ErrorMessage
```

```powershell title="List every object an import left with unresolved references"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Get-JIMActivityExecutionItem |
    Where-Object { $_.UnresolvedReferenceCount -gt 0 } |
    Format-Table DisplayNameSnapshot, UnresolvedReferenceCount, ErrorType
```

This finds objects whose item carries a different error as well, which filtering on `ErrorType` would miss.

```powershell title="List every value an import could not read, and where"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Where-Object { $_.ErrorType -eq 'ImportAttributeValueError' } |
    Get-JIMActivityExecutionItem |
    ForEach-Object {
        $item = $_
        $item.AttributeValueErrors | Select-Object @{ Name = 'Object'; Expression = { $item.DisplayNameSnapshot } },
            @{ Name = 'Source'; Expression = { $item.SourceLocation } }, AttributeName, Value, Reason
    }
```

```powershell title="List the objects an ambiguous match found"
(Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901").MatchCandidates |
    Select-Object DisplayNameSnapshot, MatchedValue, Exists,
        @{ Name = 'JoinedTo'; Expression = { $_.JoinedObjects.ConnectedSystemName -join ', ' } }
```

```powershell title="Show what caused an execution item"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Causes |
    Format-Table Level, EdgeType, ReasonCode, Count, ObjectType, ConnectedSystem, Members
```

```powershell title="Show which consequences are still waiting, and on what"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
    Where-Object Ending -like 'Waiting*' |
    Format-Table Level, HopKind, Count, ObjectType, ConnectedSystem, Ending, DueAt
```

---

## Get-JIMCausalCohortMember

Pages through every member of one cohort of an execution item's consequences. `Get-JIMActivityExecutionItem -Consequences` counts a cohort exactly ("4218 Groups") but returns only its first members; this returns the rest, earliest first and then by name.

<!-- BEGIN GENERATED: Get-JIMCausalCohortMember -->
### Syntax

```powershell
# Page (default)
Get-JIMCausalCohortMember -ExecutionItemId <guid> -CohortKey <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMCausalCohortMember -ExecutionItemId <guid> -CohortKey <string> -All [-Search <string>] [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ExecutionItemId` | `guid` | Yes |  | The execution item whose consequences the cohort belongs to. Accepts pipeline input by property name. |
| `CohortKey` | `string` | Yes |  | The cohort's key, from `Get-JIMActivityExecutionItem -Consequences`, whose rows can be piped straight in. Accepts pipeline input by property name. |
| `Search` | `string` | No |  | Text a member's name must contain, case-insensitively. |
| `Page` | `int` | No (Page) | `1` | The page to return. At least 1. |
| `PageSize` | `int` | No | `50` | How many members per page. With `-All`, 100 unless it is given. From 1 to 100. |
| `All` | `switch` | Yes (All) |  | Return every member, fetching page after page. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page however many there are. |

### Output

Each member of the cohort, earliest first and then by name.

| Property | Type | Description |
|----------|------|-------------|
| `RunProfileExecutionItemId` | `guid` | The execution item that recorded the consequence, where it is one. |
| `PendingExportId` | `guid` | The outstanding Pending Export, for an OutstandingPendingExport cohort. |
| `MetaverseObjectId` | `guid` | The Metaverse Object whose deletion is pending, for a ScheduledDeletion cohort. |
| `DisplayName` | `string` | How the consequence's object was named when it was recorded. |
| `Occurred` | `datetime` | When the consequence happened, or for outstanding work when it was queued or scheduled, in UTC. |
| `DueAt` | `datetime` | When outstanding work is next due, in UTC: a retry time, or when a scheduled deletion becomes eligible. |
<!-- END GENERATED: Get-JIMCausalCohortMember -->

### Examples

```powershell title="List every Group a deletion removed a member from"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
    Where-Object { $_.Level -eq 1 -and $_.Attribute -eq 'Static Members' } |
    Get-JIMCausalCohortMember -All |
    Select-Object DisplayName, Occurred
```

---

## See also

- [Activities](activities.md): cmdlets for Activities, a run's statistics, and its child Activities
- [Causality](../configuration/causality.md): what caused a change, and what it went on to cause, in the portal
- [Run Profiles](run-profiles.md): cmdlets for managing and executing synchronisation Run Profiles
