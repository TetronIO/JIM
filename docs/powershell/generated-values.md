---
title: Generated Values
description: "The cmdlets that list the values JIM generated for a Metaverse Object, and the values it retired and will never issue again."
type: reference
audience: automation-author
---

# Generated Values

These cmdlets work with the values a generated Attribute Flow issues: those a Metaverse Object holds now, those retired from an attribute and never issued again, the counter behind a Sequence, and the values a target rejected that wait on a decision. How values are generated and kept unique is covered in [Generated Values](../configuration/generated-values.md). A generated Attribute Flow itself is configured with [`New-JIMSyncRuleMapping`](attribute-mappings.md#new-jimsyncrulemapping).

---

## :material-pound: Get-JIMGeneratedValue

Lists the generated values a Metaverse Object holds, one per generated attribute, or nothing when it holds none. A generated Attribute Flow is configured with `New-JIMSyncRuleMapping -Generate`; see [Attribute Mappings](attribute-mappings.md#new-jimsyncrulemapping).

<!-- BEGIN GENERATED: Get-JIMGeneratedValue -->
### Syntax

```powershell
Get-JIMGeneratedValue -MetaverseObjectId <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseObjectId` | `guid` | Yes |  | The ID of the Metaverse Object. A Metaverse Object piped from `Get-JIMMetaverseObject` or `Search-JIMMetaverseObject` binds here through its Id. Accepts pipeline input by property name, `MetaverseObjectId` or `Id`. |

### Output

One object per generated value the Metaverse Object holds; nothing when it holds none.

| Property | Type | Description |
|----------|------|-------------|
| `AssignmentId` | `guid` | The unique identifier of the assignment. |
| `MetaverseAttributeId` | `int` | The unique identifier of the Metaverse Attribute the value was generated for. |
| `AttributeName` | `string` | That attribute's name. |
| `Value` | `string` | The generated value. |
| `TokenKind` | `string` | How the value was made unique: a number added only if the value is taken, the next number in a sequence, or a random token. One of `OnlyIfTaken`, `Sequence` or `Random`. |
| `SyncRuleId` | `int` | The unique identifier of the Synchronisation Rule whose Attribute Flow generated the value. |
| `SyncRuleName` | `string` | That Synchronisation Rule's name. |
| `SyncRuleMappingId` | `int` | The unique identifier of the Attribute Flow that generated the value. |
| `State` | `string` | Where the value stands: Committed once saved onto the object, Remediated after JIM revised it when a target refused it, or NeedsDecision when it could not be revised automatically and waits for an administrator. Proposed values are never saved, so never appear here. One of `Proposed`, `Committed`, `Remediated` or `NeedsDecision`. |
| `AssignedDate` | `datetime` | When the value was assigned (UTC). |
<!-- END GENERATED: Get-JIMGeneratedValue -->

### Examples

```powershell title="List everything JIM generated for a Metaverse Object"
Get-JIMGeneratedValue -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipe a Metaverse Object straight in"
Get-JIMMetaverseObject -ObjectTypeName "User" -Search "j.smith" | Get-JIMGeneratedValue
```

---

## :material-delete-restore: Get-JIMRetiredGeneratedValue

Lists an attribute's retired generated values, newest first: the values JIM issued for it and will never issue again, whichever Attribute Flow generates it. Every page is read, so the whole list is returned. Read-only; the only way to forget retired values is `Restart-JIMGeneratedValues` on a sequence. See [Retired values](../configuration/generated-values.md#retired-values) for when a value is retired.

<!-- BEGIN GENERATED: Get-JIMRetiredGeneratedValue -->
### Syntax

```powershell
# ByMetaverseAttributeId (default)
Get-JIMRetiredGeneratedValue -MetaverseAttributeId <int> [-Search <string>]

# ByMetaverseAttributeName
Get-JIMRetiredGeneratedValue -MetaverseAttributeName <string> [-Search <string>]

# ByConnectedSystemAttribute
Get-JIMRetiredGeneratedValue -ConnectedSystemId <int> -ObjectTypeId <int> -AttributeId <int> [-Search <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseAttributeId` | `int` | Yes (ByMetaverseAttributeId) |  | The ID of the Metaverse Attribute, for an import Attribute Flow. A Metaverse Attribute piped from `Get-JIMMetaverseAttribute` binds here through its Id. Accepts pipeline input by property name, `MetaverseAttributeId` or `Id`. |
| `MetaverseAttributeName` | `string` | Yes (ByMetaverseAttributeName) |  | The name of the Metaverse Attribute, for an import Attribute Flow, such as Account Name. |
| `ConnectedSystemId` | `int` | Yes (ByConnectedSystemAttribute) |  | For an export Attribute Flow, the ID of the Connected System whose attribute is generated. |
| `ObjectTypeId` | `int` | Yes (ByConnectedSystemAttribute) |  | For an export Attribute Flow, the ID of the Connected System Object Type the attribute belongs to. |
| `AttributeId` | `int` | Yes (ByConnectedSystemAttribute) |  | For an export Attribute Flow, the ID of the Connected System attribute. |
| `Search` | `string` | No |  | Returns only the values containing this text, or held by an object whose name contains it, ignoring case. |

### Output

One object per retired value, newest first. Every page is read, so the whole register is returned.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `long` | The register entry's own identifier. |
| `MetaverseAttributeId` | `int` | The Metaverse Attribute the value was generated for (an import flow); empty for an export flow. |
| `ConnectedSystemObjectTypeAttributeId` | `int` | The Connected System attribute the value was generated for (an export flow); empty for an import flow. |
| `AttributeName` | `string` | The attribute's name. |
| `Value` | `string` | The value as it was issued. |
| `RetiredAt` | `datetime` | When it was retired (UTC). |
| `Reason` | `string` | Why it was retired: `ObjectDeleted` (the object that held it was deleted), `Superseded` (another Attribute Flow took the attribute over, or the value was cleared), `Recalled` (the generating Attribute Flow was removed), or `Regenerated` (reserved for Collision Remediation; not written yet). One of `ObjectDeleted`, `Regenerated`, `Superseded` or `Recalled`. |
| `FromObjectId` | `guid` | The object that held it: a Metaverse Object id (import) or Connected System Object id (export). |
| `FromObjectDisplayName` | `string` | The holder's display name, captured when the value was retired. |
| `FromObjectExists` | `bool` | Whether the object that held the value still exists. |
| `ActivityId` | `guid` | The Activity during which the value was retired, when one was recorded. |
| `HeldBy` | `string` | Who held the value, for reading: the name of the object that held it, with "(deleted)" once that object no longer exists. |
<!-- END GENERATED: Get-JIMRetiredGeneratedValue -->

### Examples

```powershell title="List every retired Account Name"
Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name"
```

```powershell title="Find out whether a leaver's account name is retired, and why"
Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name" -Search "fenwick" |
    Select-Object Value, RetiredAt, Reason, HeldBy
```

```powershell title="Pipe a Metaverse Attribute straight in"
Get-JIMMetaverseAttribute -Name "Employee Number" | Get-JIMRetiredGeneratedValue
```

---

## :material-order-numeric-ascending: Get-JIMGeneratedValueSequence

Gets a generated Sequence mapping's counter state (Unique Value Generation): the next number it
would issue, and how many it has issued so far. Read-only; nothing is allocated or reserved by calling
this. Only meaningful for a generated mapping whose token kind is Sequence; every other mapping returns a
"not found" error.

<!-- BEGIN GENERATED: Get-JIMGeneratedValueSequence -->
### Syntax

```powershell
Get-JIMGeneratedValueSequence -SyncRuleId <int> -MappingId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule the mapping belongs to. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `MappingId` | `int` | Yes |  | The ID of the generated mapping. |

### Output

The counter's state.

| Property | Type | Description |
|----------|------|-------------|
| `AttributeName` | `string` | The attribute the counter issues values for. |
| `NextNumber` | `long` | The next number the counter would issue: the higher of its current position and the mapping's Sequence Start. Before the counter is seeded, the higher of the Sequence Start and one step past the attribute's highest existing number. |
| `NextNumberFormatted` | `string` | The next number as it would be issued, padded to the mapping's Fixed Width when it has one. |
| `NextNumberWidthExceeded` | `bool` | Whether the next number no longer fits the mapping's Fixed Width on a mapping that stops rather than issue a longer number: the next value cannot be generated until the width or the counter is changed. False on a mapping that allows a longer number. |
| `AssignedCount` | `long` | How many numbers the counter has issued; 0 before it is seeded. |
| `IsSeeded` | `bool` | Whether the counter has been seeded. Until it is, the first value seeds it from the higher of the Sequence Start and the attribute's highest existing number. |
<!-- END GENERATED: Get-JIMGeneratedValueSequence -->

### Examples

```powershell title="Check an Employee Number mapping's next number before raising its Sequence Start"
Get-JIMGeneratedValueSequence -SyncRuleId 1 -MappingId 12
```

```powershell title="Pipe a Synchronisation Rule straight in"
Get-JIMSyncRule -Id 1 | Get-JIMGeneratedValueSequence -MappingId 12
```

---

## :material-restart: Restart-JIMGeneratedValues

"Start again" (Unique Value Generation). For a generated Sequence mapping, moves the target
attribute's counter back to the mapping's configured Sequence Start (the move can go either direction;
"back" is the common case, but a lower configured start is honoured too), and forgets the attribute's
[retired values](../configuration/generated-values.md#retired-values) so they can be issued again.
For every other token kind (`OnlyIfTaken`, `Random`) this is a documented no-op that still succeeds.

**It changes nothing else.** No existing generated value on any object is changed, nothing is exported,
and no synchronisation runs as a result of this command. To see what it would forget first, run
`Get-JIMRetiredGeneratedValue` for the attribute (see [Generated Values](#get-jimretiredgeneratedvalue)).

<!-- BEGIN GENERATED: Restart-JIMGeneratedValues -->
### Syntax

```powershell
Restart-JIMGeneratedValues -SyncRuleId <int> -MappingId <int> [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule the mapping belongs to. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `MappingId` | `int` | Yes |  | The ID of the generated mapping. |

### Output

What moved, and how many retired values were forgotten.

| Property | Type | Description |
|----------|------|-------------|
| `RetiredValuesForgotten` | `int` | How many of the attribute's retired values were forgotten, and so can be issued again; 0 for a mapping whose token is not a Sequence. |
| `CounterFrom` | `long` | The next number before the restart; empty when the counter had never been seeded, or the token is not a Sequence. |
| `CounterTo` | `long` | The next number after the restart, which is the mapping's Sequence Start; empty when CounterFrom is. |
<!-- END GENERATED: Restart-JIMGeneratedValues -->

### Examples

```powershell title="Start a generated Sequence mapping again"
Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12
```

```powershell title="Do the same without prompting, for use in a script"
Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -Confirm:$false
```

---

## :material-arrow-decision-outline: Get-JIMGeneratedValueDecision

Lists the generated values held for a decision, newest first, or reads one by ID, or counts them.

When a Connected System rejects a generated value as already in use, [Collision Remediation](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value) normally corrects it. It holds the export for a decision instead when correcting it would rename an account another Connected System has already accepted, when JIM cannot tell whether one has, or when the value has been corrected as often as JIM allows. This cmdlet lists what is held; [`Approve-JIMGeneratedValueRename`](#approve-jimgeneratedvaluerename) and [`Reset-JIMGeneratedValueDecision`](#reset-jimgeneratedvaluedecision) act on it, each action recorded as an Activity naming who took it.

<!-- BEGIN GENERATED: Get-JIMGeneratedValueDecision -->
### Syntax

```powershell
# List (default)
Get-JIMGeneratedValueDecision [-ConnectedSystemId <int>] [-SyncRuleId <int>] [-Status <string>] [-MetaverseObjectId <guid>] [-Page <int>] [-PageSize <int>]

# Summary
Get-JIMGeneratedValueDecision -Summary [-ConnectedSystemId <int>] [-SyncRuleId <int>]

# ListAll
Get-JIMGeneratedValueDecision -All [-ConnectedSystemId <int>] [-SyncRuleId <int>] [-Status <string>] [-MetaverseObjectId <guid>] [-PageSize <int>] [-Force]

# ById
Get-JIMGeneratedValueDecision -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | No (List, Summary, ListAll) |  | Restrict to values involving one Connected System: it rejected the value, it anchors the value, or the value is exported to and checked in it. |
| `SyncRuleId` | `int` | No (List, Summary, ListAll) |  | Restrict to values generated by Attribute Flows on one Synchronisation Rule. |
| `Status` | `string` | No (List, ListAll) |  | Restrict to NeedsDecision or RenameAllowed. Omit for both. One of `NeedsDecision` or `RenameAllowed`. |
| `MetaverseObjectId` | `guid` | No (List, ListAll) |  | Restrict to one Metaverse Object's values, including those on the accounts joined to it. |
| `Id` | `guid` | Yes (ById) |  | Gets one value by its ID, whatever its state: its Status may also be Released, when nothing is waiting on a decision for it. |
| `Summary` | `switch` | Yes (Summary) |  | Returns the counts instead of the values. Narrowed by `-ConnectedSystemId` and `-SyncRuleId` when given. |
| `Page` | `int` | No (List) | `1` | The page of values to return, counting from 1. At least 1. |
| `PageSize` | `int` | No (List, ListAll) | `50` | How many values each page holds. From 1 to 100. |
| `All` | `switch` | Yes (ListAll) |  | Returns every page of values rather than one. It stops after 1,000 pages, about 50,000 values at the default page size, with a warning; `-Force` reads on. Cannot be used with `-Page`. |
| `Force` | `switch` | No (ListAll) |  | With `-All`, reads every page, past the 1,000-page limit. |

### Output

By default, the values waiting on a decision, newest first. With `-Id`, that value. With `-Summary`, how many values are in each state.

**By default, or with `-All` or `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the value: what allowing a rename and trying again take. |
| `Status` | `string` | `NeedsDecision` (the export is held until someone acts), `RenameAllowed` (an administrator allowed the rename; the next export run chooses the next free value), or `Released` (nothing is waiting; only on a read by id or an action's response, never in a list). One of `NeedsDecision`, `RenameAllowed` or `Released`. |
| `Reason` | `string` | Why the value is held: AnchoredElsewhere when another Connected System has already accepted it, so correcting it would rename that account; CannotTell when a Connected System's Connector Space was cleared and has not been fully imported since, so JIM cannot tell whether it holds the value; RemediationLimitReached when it has been corrected RemediationCount times and is still rejected; or NoValueAvailable when no other value could be generated. Empty when not recorded. One of `AnchoredElsewhere`, `CannotTell`, `RemediationLimitReached` or `NoValueAvailable`. |
| `MetaverseObjectId` | `guid` | The ID of the Metaverse Object the value belongs to or, for a value generated on an export Synchronisation Rule, the one its Connected System Object is joined to. |
| `MetaverseObjectDisplayName` | `string` | That Metaverse Object's display name. |
| `MetaverseObjectTypeName` | `string` | The name of that Metaverse Object's type. |
| `ConnectedSystemObjectId` | `guid` | For a value generated on an export Synchronisation Rule, the ID of the Connected System Object it belongs to; empty otherwise. |
| `ConnectedSystemObjectConnectedSystemId` | `int` | The ID of the Connected System holding that Connected System Object; empty when there is none. |
| `AttributeName` | `string` | The attribute the value was generated for. |
| `Value` | `string` | The value the target rejected, which is still the value JIM holds. |
| `RemediationCount` | `int` | How many times Collision Remediation has already corrected this value. |
| `RejectedByConnectedSystemId` | `int` | The ID of the Connected System that rejected the value. |
| `RejectedByConnectedSystemName` | `string` | The name of the Connected System that rejected the value; empty when it has since been deleted. |
| `AnchoredByConnectedSystemId` | `int` | The ID of the Connected System that anchors the value, or cannot tell whether it does. |
| `AnchoredByConnectedSystemName` | `string` | The name of that Connected System; empty when it has since been deleted. |
| `Since` | `datetime` | When the value began waiting on a decision (UTC). |
| `RenameAllowedAt` | `datetime` | When the rename was allowed (UTC); only while Status is RenameAllowed. |
| `RenameAllowedBy` | `string` | Who allowed the rename; only while Status is RenameAllowed. |
| `SyncRuleId` | `int` | The ID of the Synchronisation Rule whose mapping generated the value. |
| `SyncRuleName` | `string` | The name of that Synchronisation Rule. |
| `SyncRuleMappingId` | `int` | The ID of the mapping that generated the value. |

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `NeedsDecisionCount` | `int` | Values whose export is held until an administrator acts. |
| `RenameAllowedCount` | `int` | Values whose rename an administrator has allowed, to be renamed at the next export. |
| `CorrectedRecentlyCount` | `int` | How many values Collision Remediation corrected on or after CorrectedSince, each counted once from its most recent correction; a value whose object has since been deleted no longer counts. These need no action. |
| `CorrectedSince` | `datetime` | The start of the window CorrectedRecentlyCount covers (UTC): seven days before the read. |
<!-- END GENERATED: Get-JIMGeneratedValueDecision -->

### Examples

```powershell title="How much needs attention"
Get-JIMGeneratedValueDecision -Summary
```

```powershell title="Why each held value is held"
Get-JIMGeneratedValueDecision -Status NeedsDecision |
    Select-Object MetaverseObjectDisplayName, AttributeName, Value, Reason, RejectedByConnectedSystemName, AnchoredByConnectedSystemName
```

```powershell title="Every held value involving one Connected System"
Get-JIMGeneratedValueDecision -ConnectedSystemId 3 -All
```

## :material-check-decagram-outline: Approve-JIMGeneratedValueRename

Allows the rename. At the next export that meets the rejection, JIM chooses the next free value and applies it everywhere the value is used, renaming the account in the Connected System that already holds the current value. The new value is decided then, after JIM checks every system again, not now. The held export is released so that export happens.

<!-- BEGIN GENERATED: Approve-JIMGeneratedValueRename -->
### Syntax

```powershell
# ById (default)
Approve-JIMGeneratedValueRename [-Id] <guid> [-Force] [-WhatIf] [-Confirm]

# InputObject
Approve-JIMGeneratedValueRename -InputObject <PSCustomObject> [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The ID of the value, as `Get-JIMGeneratedValueDecision` returns it. Can be given by position, as the first argument. |
| `InputObject` | `PSCustomObject` | Yes (InputObject) |  | A held value from `Get-JIMGeneratedValueDecision`, through the pipeline. The confirmation then names the value and its object. Accepts pipeline input. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

The value as it now stands, with Status RenameAllowed. A value that is not waiting on a decision, because it has been released or its rename already allowed, is an error, and nothing changes.

Properties as under [Get-JIMGeneratedValueDecision](#get-jimgeneratedvaluedecision).
<!-- END GENERATED: Approve-JIMGeneratedValueRename -->

### Examples

```powershell title="Allow the rename of one value"
Approve-JIMGeneratedValueRename -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f
```

```powershell title="See which accounts would be renamed for every value held from one Synchronisation Rule, without allowing anything"
Get-JIMGeneratedValueDecision -SyncRuleId 2 -Status NeedsDecision -All | Approve-JIMGeneratedValueRename -WhatIf
```

Run the second example without `-WhatIf` only after reviewing what it lists: every value it names renames a live account.

## :material-undo-variant: Reset-JIMGeneratedValueDecision

Tries again: releases the held exports so the next export run tries the same values, for when the clash has been resolved in the target. A value the target rejects again is held for a decision again. Values whose rename has been allowed are left as they are. A pipeline is collected and sent as one request; each value released is recorded as its own Activity.

<!-- BEGIN GENERATED: Reset-JIMGeneratedValueDecision -->
### Syntax

```powershell
Reset-JIMGeneratedValueDecision [-Id <guid[]>] [-ConnectedSystemId <int>] [-SyncRuleId <int>] [-MetaverseObjectId <guid>] [-AllDecisions] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid[]` | No |  | The IDs of the values to try again. Accepts held values from `Get-JIMGeneratedValueDecision` through the pipeline. Combines with the other criteria rather than replacing them. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int` | No |  | Try again every held value involving one Connected System. |
| `SyncRuleId` | `int` | No |  | Try again every held value generated by one Synchronisation Rule's Attribute Flows. |
| `MetaverseObjectId` | `guid` | No |  | Try again every held value of one Metaverse Object. |
| `AllDecisions` | `switch` | No |  | Try again every value needing a decision. Required when nothing else narrows the request, so a command that names no criteria cannot act on everything by accident. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

How many values were released. Zero is a valid outcome: a value already released, or whose rename has been allowed, no longer matches.

| Property | Type | Description |
|----------|------|-------------|
| `AffectedCount` | `int` | How many values were released for the next export run to try again. Zero is a valid outcome: a value already released, or whose rename has been allowed, no longer matches. |
<!-- END GENERATED: Reset-JIMGeneratedValueDecision -->

### Examples

```powershell title="Try again every value held from one Synchronisation Rule"
Reset-JIMGeneratedValueDecision -SyncRuleId 2
```

```powershell title="Try again every value involving one Connected System that needs a decision, in one request"
Get-JIMGeneratedValueDecision -Status NeedsDecision -ConnectedSystemId 3 | Reset-JIMGeneratedValueDecision -Force
```

---

## :material-book-open-variant-outline: See also

- [Generated Values](../configuration/generated-values.md): how JIM generates unique values, and when one is retired
- [Metaverse](metaverse.md): find the Metaverse Object whose generated values you want
- [Generated Values in Target Systems](../configuration/generated-values-in-target-systems.md): what happens when a target rejects a value, and the decisions it can leave
- [Synchronisation Rules](synchronisation-rules.md): configure a generated Attribute Flow
