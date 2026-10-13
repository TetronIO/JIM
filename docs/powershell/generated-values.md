---
title: Generated Values
description: "The cmdlets that list the values JIM generated for a Metaverse Object, and the values it retired and will never issue again."
type: reference
audience: automation-author
---

# Generated Values

These cmdlets read the values a generated Attribute Flow issued: those a Metaverse Object holds now, and those retired from an attribute and never issued again. How values are generated and kept unique is covered in [Generated Values](../configuration/generated-values.md). A generated Attribute Flow is configured, and its sequence restarted, with the [Synchronisation Rule](synchronisation-rules.md#new-jimsyncrulemapping) cmdlets, where the cmdlets for values waiting on a decision are too.

---

## Get-JIMGeneratedValue

Lists the generated values a Metaverse Object holds, one per generated attribute, or nothing when it holds none. A generated Attribute Flow is configured with `New-JIMSyncRuleMapping -Generate`; see [Synchronisation Rules](synchronisation-rules.md#new-jimsyncrulemapping).

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

## Get-JIMRetiredGeneratedValue

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

## See also

- [Generated Values](../configuration/generated-values.md): how JIM generates unique values, and when one is retired
- [Metaverse](metaverse.md): find the Metaverse Object whose generated values you want
- [Synchronisation Rules](synchronisation-rules.md): configure a generated Attribute Flow, restart its sequence, and decide on values waiting for an administrator
