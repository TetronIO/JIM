---
title: Connected System Objects
description: "The cmdlets that read the Connected System Objects in a Connector Space, their history and unresolved references, and set their passwords."
type: reference
audience: automation-author
---

# Connected System Objects

The Connected System Object cmdlets read what JIM holds in a Connected System's Connector Space: its Connected System Objects, their attribute values and change history, and the references among them JIM could not resolve. One sets an object's password. The changes waiting to be written to the Connected System are on [Pending Exports](pending-exports.md). What the Connector Space is, and how objects arrive in it, is covered in [Connected Systems](../configuration/connected-systems.md#the-connector-space).

---

## Get-JIMConnectedSystemObject

Gets the Connected System Objects in a Connected System's Connector Space, a page at a time, and pages through the values of a large multi-valued attribute.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObject -->
### Syntax

```powershell
# List (default)
Get-JIMConnectedSystemObject -ConnectedSystemId <int> [-Search <string>] [-Page <int>] [-PageSize <int>] [-ObjectTypeId <int>] [-Status <string>] [-JoinType <string>] [-SortBy <string>] [-Ascending] [-HasUnresolvedReference]

# ListAll
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -All [-Search <string>] [-PageSize <int>] [-Force] [-ObjectTypeId <int>] [-Status <string>] [-JoinType <string>] [-SortBy <string>] [-Ascending] [-HasUnresolvedReference]

# Count
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Count [-ObjectTypeId <int>] [-PartitionId <int>]

# AttributeValuesAll
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid> -AttributeName <string> -All [-Search <string>] [-PageSize <int>] [-Force]

# AttributeValues
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid> -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# ById
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes (AttributeValuesAll, AttributeValues, ById) |  | The ID of the Connected System Object to get. Accepts pipeline input by property name. |
| `AttributeName` | `string` | Yes (AttributeValuesAll, AttributeValues) |  | With `-Id`, gets the values of this attribute a page at a time, for a multi-valued attribute such as member whose values the object read by ID cuts short. |
| `Search` | `string` | No (List, ListAll, AttributeValuesAll, AttributeValues) |  | Returns only the objects, or with `-AttributeName` the values, that match this text. |
| `Page` | `int` | No (List, AttributeValues) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No (List, ListAll, AttributeValuesAll, AttributeValues) | `50` | How many objects or values a page holds. From 1 to 100. |
| `All` | `switch` | Yes (ListAll, AttributeValuesAll) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (ListAll, AttributeValuesAll) |  | With `-All`, fetches every page, past the 1000-page limit. |
| `Count` | `switch` | Yes (Count) |  | Returns how many Connected System Objects there are, rather than the objects. |
| `ObjectTypeId` | `int` | No (List, ListAll, Count) |  | Returns or counts only the objects of the Object Type with this ID. |
| `PartitionId` | `int` | No (Count) |  | With `-Count`, counts only the objects in the partition with this ID. |
| `Status` | `string` | No (List, ListAll) |  | Returns only the objects with this status. One of `Normal`, `Obsolete` or `PendingProvisioning`. |
| `JoinType` | `string` | No (List, ListAll) |  | Returns only the objects joined this way. One of `NotJoined`, `Projected`, `Provisioned` or `Joined`. |
| `SortBy` | `string` | No (List, ListAll) |  | Sorts the list by ExternalId, SecondaryExternalId, DisplayName, Type, DateJoined, Status or JoinType. Newest first when omitted. |
| `Ascending` | `switch` | No (List, ListAll) |  | Sorts in ascending order rather than descending. |
| `HasUnresolvedReference` | `switch` | No (List, ListAll) |  | Returns only the objects holding a reference value JIM could not resolve to another Connected System Object. `Get-JIMConnectedSystemObjectUnresolvedReference` says why, for one object. |

### Output

The objects, the one object, its values of `-AttributeName`, or with `-Count` how many objects there are.

**By default, or with `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the Connected System Object. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the object belongs to. |
| `DisplayName` | `string` | The object's display name; empty when it has none. |
| `ExternalIdValue` | `string` | The value of the object's external identifier attribute; empty when it has none. |
| `ExternalIdAttributeName` | `string` | The name of the attribute that identifies objects of this type in the Connected System. |
| `SecondaryExternalIdValue` | `string` | The value of the object's secondary external identifier attribute; empty when it has none. |
| `SecondaryExternalIdAttributeName` | `string` | The name of the attribute the Connected System also identifies objects of this type by, such as a distinguished name; empty when it has none. |
| `Created` | `datetime` | When the object first arrived in the Connector Space (UTC). |
| `LastUpdated` | `datetime` | When the object was last changed (UTC); empty if it never has been. |
| `TypeId` | `int` | The ID of the object's Object Type. |
| `TypeName` | `string` | The name of the object's Object Type. |
| `Status` | `string` | Normal for an object taking part in synchronisation; Obsolete for one the last Full Import did not return, so no longer in the Connected System; PendingProvisioning for one JIM has decided to create and not yet confirmed. One of `Normal`, `Obsolete` or `PendingProvisioning`. |
| `JoinType` | `string` | How the object came to be joined to a Metaverse Object: Projected (it created one), Provisioned (one created it), Joined (it was matched to one), or NotJoined. One of `NotJoined`, `Projected`, `Provisioned` or `Joined`. |
| `DateJoined` | `datetime` | When the object was joined to its Metaverse Object (UTC); empty when it is not joined. |
| `PendingDisplayName` | `string` | The display name a queued Pending Export will give the object; empty when none is queued. |
| `PendingExternalId` | `string` | The external identifier a queued Pending Export will give the object; empty when none is queued. |
| `PendingSecondaryExternalId` | `string` | The secondary external identifier a queued Pending Export will give the object, where it has no confirmed one yet; empty otherwise. |
| `HasPendingExport` | `bool` | Whether a Pending Export is queued for the object. |
| `PendingExportId` | `guid` | The ID of the queued Pending Export; empty when there is none. |
| `PendingExportStatus` | `string` | The status of the queued Pending Export: Pending while it is waiting to be exported, Exported once it is awaiting a confirming import, and so on. Empty when none is queued. One of `Pending`, `ExportNotConfirmed`, `Executing`, `Failed`, `Exported` or `Parked`. |
| `PendingExportChangeType` | `string` | Whether the queued Pending Export creates, updates or deletes the object; empty when there is none. One of `Create`, `Update` or `Delete`. |
| `State` | `string` | The object's state, combining its status with any queued Pending Export: InSync, UpdatePending, ProvisioningExportPending, ProvisioningAwaitingConfirmation, ExportNotConfirmed, ExportFailed, DeletePending or Obsolete. One of `InSync`, `UpdatePending`, `ProvisioningExportPending`, `ProvisioningAwaitingConfirmation`, `ExportNotConfirmed`, `ExportFailed`, `DeletePending` or `Obsolete`. |
| `UnresolvedReferenceCount` | `int` | How many of the object's reference values JIM has not resolved to a Connected System Object. Zero when there are none. |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the Connected System Object. |
| `Created` | `datetime` | When the object first arrived in the Connector Space (UTC). |
| `LastUpdated` | `datetime` | When the object was last changed (UTC); empty if it never has been. |
| `Status` | `string` | Normal for an object taking part in synchronisation; Obsolete for one the last Full Import did not return; PendingProvisioning for one JIM has decided to create and not yet confirmed. One of `Normal`, `Obsolete` or `PendingProvisioning`. |
| `JoinType` | `string` | How the object came to be joined to a Metaverse Object: Projected, Provisioned, Joined, or NotJoined. One of `NotJoined`, `Projected`, `Provisioned` or `Joined`. |
| `DateJoined` | `datetime` | When the object was joined to its Metaverse Object (UTC); empty when it is not joined. |
| `DisplayName` | `string` | The object's display name, or its external identifier when it has none. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the object belongs to. |
| `ConnectedSystemName` | `string` | The name of that Connected System. |
| `TypeId` | `int` | The ID of the object's Object Type. |
| `TypeName` | `string` | The name of the object's Object Type. |
| `MetaverseObjectId` | `guid` | The ID of the Metaverse Object the object is joined to; empty when it is not joined. |
| `MetaverseObjectDisplayName` | `string` | That Metaverse Object's display name; empty when it is not joined or has none. |
| `AttributeValues` | `object[]` | The object's attribute values, one entry per value. A multi-valued attribute with many values may be cut short; AttributeValueSummaries says so. Each has `Id`, `AttributeId`, `AttributeName`, `StringValue`, `DateTimeValue`, `IntValue`, `LongValue`, `DecimalValue`, `ByteValue`, `GuidValue`, `BoolValue` and `ReferenceValueId`. |
| `AttributeValueSummaries` | `object[]` | For each attribute whose values were cut short, how many it holds and how many came back; empty when nothing was cut short. `Get-JIMConnectedSystemObjectAttributeValue` reads the rest. Each has `AttributeName`, `TotalCount`, `ReturnedCount` and `HasMore`. |

**With `-Id` and `-AttributeName`, or `-Id` and `-AttributeName` and `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the value. |
| `AttributeId` | `int` | The ID of the attribute the value belongs to. |
| `AttributeName` | `string` | The name of that attribute. |
| `StringValue` | `string` | The value of a Text attribute; empty otherwise. |
| `DateTimeValue` | `datetime` | The value of a DateTime attribute (UTC); empty otherwise. |
| `IntValue` | `int` | The value of a Number attribute; empty otherwise. |
| `LongValue` | `long` | The value of a LongNumber attribute; empty otherwise. |
| `DecimalValue` | `double` | The value of a Decimal attribute; empty otherwise. |
| `ByteValue` | `byte[]` | The value for Binary attributes. Serialised to JSON as a base64-encoded string (System.Text.Json's representation for byte arrays). |
| `GuidValue` | `guid` | The value of a Guid attribute; empty otherwise. |
| `BoolValue` | `bool` | The value of a Boolean attribute; empty otherwise. |
| `ReferenceValueId` | `guid` | For a Reference attribute, the ID of the Connected System Object it refers to; empty otherwise, or when the reference is unresolved. |

**With `-Count`**: an `int`.
<!-- END GENERATED: Get-JIMConnectedSystemObject -->

### Examples

```powershell title="List objects in a Connected System"
Get-JIMConnectedSystemObject -ConnectedSystemId 3
```

```powershell title="Find the objects whose export failed"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All |
    Where-Object { $_.State -eq "ExportFailed" }
```

```powershell title="List the ten objects holding the most unresolved references"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -HasUnresolvedReference -All |
    Sort-Object UnresolvedReferenceCount -Descending |
    Select-Object DisplayName, TypeName, UnresolvedReferenceCount -First 10
```

```powershell title="Find Obsolete objects matching a search term"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Search "smith" -Status Obsolete
```

```powershell title="Get every object in a Connected System"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All
```

```powershell title="Get every object in a very large Connector Space, overriding the -All safety cap"
# -All stops after 1000 pages (~50,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All -Force
```

```powershell title="Get a specific Connected System Object"
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

Gets the changes made to a Connected System Object, most recent first, each with who or what made it and the values it changed.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectChangeHistory -->
### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid> [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System the object belongs to. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes |  | The ID of the Connected System Object. Accepts pipeline input by property name. |
| `All` | `switch` | Yes (All) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page, past the 1000-page limit. |
| `Page` | `int` | No (Page) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No | `50` | How many changes a page holds. From 1 to 100. |

### Output

The changes, most recent first.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The ID of the change. |
| `ChangeType` | `string` | What kind of change it was. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `ChangeTime` | `datetime` | When the change was made (UTC). |
| `InitiatedByType` | `string` | What made the change. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The ID of the user or API key that made the change; empty when the System did. |
| `InitiatedByName` | `string` | The name of whoever made the change. |
| `ActivityRunProfileExecutionItemId` | `guid` | The ID of the Run Profile execution item that recorded the change, when a Run Profile made it; empty otherwise. |
| `RunProfileName` | `string` | The name of the Run Profile that made the change; empty when none did. |
| `AttributeChanges` | `object[]` | The attributes the change touched, each with the values it added and removed. Each has `AttributeName`, `AttributeType`, `AttributePlurality` and `ValueChanges`. |
<!-- END GENERATED: Get-JIMConnectedSystemObjectChangeHistory -->

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

Pages through the values of one multi-valued attribute on a Connected System Object, for an attribute such as member too large to read whole.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectAttributeValue -->
### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid> -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid> -AttributeName <string> -All [-Search <string>] [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. |
| `CsoId` | `guid` | Yes |  | The ID of the Connected System Object. |
| `AttributeName` | `string` | Yes |  | The name of the attribute to get the values of. |
| `Search` | `string` | No |  | Returns only the values that match this text. |
| `Page` | `int` | No (Page) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No | `50` | How many values a page holds. From 1 to 100. |
| `All` | `switch` | Yes (All) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page, past the 1000-page limit. |

### Output

The attribute's values.

Properties as under [Get-JIMConnectedSystemObject](#get-jimconnectedsystemobject).
<!-- END GENERATED: Get-JIMConnectedSystemObjectAttributeValue -->

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

## Set-JIMConnectedSystemObjectPassword

Sets the password on one Connected System Object.

The Connected System Object-scoped form of Set Password: the same operation as [`Set-JIMMetaverseObjectPassword`](password-synchronisation.md#set-jimmetaverseobjectpassword) with this one Connected System Object named, for scripts that hold the Connected System Object rather than the Metaverse Object. The change is queued, encrypted, and the [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service) writes it within about a second, whatever the synchronisation engine is doing; by default the command waits up to ten seconds and tells you what the Connected System Object did with the password. JIM holds the password only until the object has it; a password the system refused is kept, still encrypted, so JIM can finish the job once the cause is dealt with. Every attempt is recorded as an Activity, carrying the outcome and, where the system refused, its verbatim reason.

This is the automation counterpart of the **Set Password** action on a Connected System Object in the administration portal. The object must be joined to a Metaverse Object: a password belongs to a Metaverse Object, and that is where its history is kept. Supply the password with `-Password`, or have JIM generate one that follows the Connected System's discovered policy with `-Generate`. A generated password is returned to you, once, on `GeneratedPassword`.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemObjectPassword -->
### Syntax

```powershell
# SuppliedPassword (default)
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId <int> -Id <guid> -Password <SecureString> [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]

# GeneratedPassword
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId <int> -Id <guid> -Generate [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System the account is in. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes |  | The ID of the Connected System Object. Accepts pipeline input by property name. |
| `Password` | `SecureString` | Yes (SuppliedPassword) |  | The password to set, as a SecureString. Encrypted before JIM stores it and held only until delivered; never logged, never returned, and never recorded on an Activity. |
| `Generate` | `switch` | Yes (GeneratedPassword) |  | Has JIM generate a password satisfying the policy it discovered on the Connected System, instead of you supplying one. Use this rather than inventing a password in your own script: JIM knows what the Connected System demands. The generated password is returned on the result's GeneratedPassword property as a SecureString. That is the only chance to capture it. |
| `ExpiryBehaviour` | `string` | No |  | What happens to the password once it is set. Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires. Defaults to RequireChangeAtNextSignIn, which is the right default for a password somebody else chose. A Connected System that cannot honour the choice applies what it can and says so in the target's Message. On Active Directory and Samba AD, NeverExpires sets Password never expires and the other two clear it; a Synchronisation Rule that writes the account's userAccountControl with Enforce State on puts its value back at the next synchronisation, and the target's Message says so. One of `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. |
| `EnableAccount` | `switch` | No |  | Enables the account as part of setting the password, whatever its Attribute Flows say. Omit it to leave the account's enabled state as it is, which is what a reset on an already-enabled account should do; the one exception is an Active Directory account whose enable JIM held back until it had a password, which the password enables as its Attribute Flow asks. Directories that refuse to enable an account without a compliant password need the password first, which is why this belongs here. |
| `Wait` | `int` | No |  | How many seconds, from 0 to 30, to wait for the account to answer before returning. Defaults to 10. Pass 0 to return as soon as the change is recorded. A wait ends early once the target has settled; one still Queued or Delivering when it runs out is reported as such, with Settled false, and its outcome appears on the Activity and the person's Password tab once it lands. From 0 to 30. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

One object describing the change: the Activity that records it, and where delivery to the account stands. No property carries the password you supplied.

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity recording the change. It is the durable record, outliving the queue, and is where the per-system outcomes appear once delivery has been attempted. |
| `Origin` | `string` | Which way the change was aimed: `Explicit` at the accounts the caller named, or `Propagated` to every Connected System configured for Password Synchronisation. One of `Propagated`, `Explicit` or `Provisioned`. |
| `Targets` | `object[]` | One entry per Connected System the change was queued for. Each has `ConnectedSystemId`, `ConnectedSystemName`, `Enabled`, `ConnectedSystemObjectId`, `State`, `NextAttemptAt`, `Message`, `AttemptCount`, `FailureReason` and `CompletedWithWarning`. |
| `QueuedForNoSystems` | `bool` | True when a propagated change found no Connected System configured for Password Synchronisation, so nothing was queued. Always false for named accounts, which are refused rather than queued for nothing. |
| `Settled` | `bool` | True when no target is still Queued or Delivering: every one has been set, is retrying on its own clock, is parked waiting on a person, is held behind a switched-off system, or has expired. A change queued for no systems is settled. |
| `GeneratedPassword` | `securestring` | The password JIM generated, present only with `-Generate`. This is the only chance to read it. |
<!-- END GENERATED: Set-JIMConnectedSystemObjectPassword -->

### Examples

```powershell title="Set a password, requiring a change at the next sign-in"
$password = Read-Host -AsSecureString "New password"
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password
```

```powershell title="Set a password and enable the account, and see whether the directory took it"
$password = Read-Host -AsSecureString "New password"
$result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password -EnableAccount -Force
$result.Targets[0] | Select-Object State, Message
```

```powershell title="Pipeline: set the password on a retrieved Connected System Object"
$password = Read-Host -AsSecureString "New password"
Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 |
    Set-JIMConnectedSystemObjectPassword -Password $password -ExpiryBehaviour NeverExpires
```

### Notes

- **This resets the password on whichever account you point it at.** Anyone who can call it can reset the password of any account in this Connector Space, subject only to what the Connected System's own service account is permitted to do.
- The password is taken as a `SecureString` so it does not sit in your session's command history in clear text. It is unwrapped only to be sent over TLS.
- A Connected System that cannot honour the requested expiry behaviour applies what it can and says so in the target's `Message`; the password is still set.
- A system that refused the password parks it, with its own reason in `Message`. A system that could not be reached is `Retrying`, because nothing was established about the password itself; JIM tries again on its own clock, and nothing is lost while the system is down.
- The account must be joined to a Metaverse Object. An unjoined object is reported as not found, with the remedy: join it first.
- Routine initial passwords belong on the Synchronisation Rule that provisions the account; see [`Set-JIMSyncRuleInitialPassword`](initial-password.md#set-jimsyncruleinitialpassword).
- Use `-Generate` instead of `-Password` to have JIM produce a password that meets the policy it discovered on
  the Connected System. It comes back on the result's `GeneratedPassword` property as a SecureString, and **that
  is the only chance to capture it**.

```powershell title="Set a compliant password without choosing one"
$result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 3 -Id $csoId -Generate -EnableAccount -Force
ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText
```

---

## Get-JIMConnectedSystemUnresolvedReferenceCount

Counts the unresolved references in a Connected System's Connector Space.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemUnresolvedReferenceCount -->
### Syntax

```powershell
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

How many reference values are unresolved; 0 when every one resolved.

Returns an `int`.
<!-- END GENERATED: Get-JIMConnectedSystemUnresolvedReferenceCount -->

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

- A count above zero means some references point at objects JIM has not imported: outside Container Scope, not imported yet, or missing from the source system. A later import resolves only the second kind.
- To see what the references have in common and what would fix them, use [`Get-JIMConnectedSystemUnresolvedReferenceSummary`](#get-jimconnectedsystemunresolvedreferencesummary); to see which references are unresolved on one object, and why, use [`Get-JIMConnectedSystemObjectUnresolvedReference`](#get-jimconnectedsystemobjectunresolvedreference).

---

## Get-JIMConnectedSystemUnresolvedReferenceSummary

Summarises every unresolved reference across a Connected System's Connector Space: how many there are and on how many objects, how they split by likely cause, and every distinct next step with how many references and objects it addresses. It is what the portal shows as **Where they point** above the Connector Space filtered to unresolved references. Likely causes are judged against the Connected System's current Container Scope; [Finding out why a reference is unresolved](../configuration/unresolved-references.md#finding-out-why-a-reference-is-unresolved) describes each one.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemUnresolvedReferenceSummary -->
### Syntax

```powershell
Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

How many references are unresolved and on how many objects, how they split by likely cause, and every distinct next step with how many it would address.

| Property | Type | Description |
|----------|------|-------------|
| `ConnectedSystemId` | `int` | The Connected System summarised. |
| `UnresolvedReferenceHandling` | `string` | The Connected System's Unresolved Reference Handling as it stands now, which decides whether its runs report the values summarised here. One of `Error`, `Warn` or `Ignore`. |
| `TotalCount` | `int` | How many reference values across the Connected System are unresolved. |
| `ObjectCount` | `int` | How many Connected System Objects hold at least one of them. |
| `Causes` | `object[]` | The likely causes, most frequent first. Each has `Cause`, `FixLocation` and `Count`. |
| `Fixes` | `object[]` | Every distinct next step, with how many values and objects each addresses, most values first: one per change to JIM's configuration, and one per cause whose fix lies in the Connected System. Each has `Cause`, `FixLocation`, `ContainerExternalId`, `PartitionExternalId`, `Count`, `ObjectCount`, `Finding` and `Remedy`. |
| `LargestFix` | `object` | The JIM configuration change that would address the most values, or empty where every cause lies in the Connected System. It has `Cause`, `FixLocation`, `ContainerExternalId`, `PartitionExternalId`, `Count`, `ObjectCount`, `Finding` and `Remedy`. |
<!-- END GENERATED: Get-JIMConnectedSystemUnresolvedReferenceSummary -->

### Examples

```powershell title="See what would fix a Connected System's unresolved references"
(Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId 3).Fixes |
    Format-Table FixLocation, Count, ObjectCount, Finding -Wrap
```

```powershell title="Unresolved references across every Connected System"
Get-JIMConnectedSystem | ForEach-Object {
    $summary = $_ | Get-JIMConnectedSystemUnresolvedReferenceSummary
    [PSCustomObject]@{ Name = $_.Name; References = $summary.TotalCount; Objects = $summary.ObjectCount }
}
```

---

## Get-JIMConnectedSystemObjectUnresolvedReference

Lists the reference values on a Connected System Object that could not be linked to another Connected System Object, each with its likely cause, where the fix lies, what is wrong and what to do, or summarises them with `-Summary`. Likely causes are judged against the Connected System's current Container Scope; [Finding out why a reference is unresolved](../configuration/unresolved-references.md#finding-out-why-a-reference-is-unresolved) describes each one.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemObjectUnresolvedReference -->
### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId <int> -Id <guid> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId <int> -Id <guid> -All [-Search <string>] [-Force] [-PageSize <int>]

# Summary
Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId <int> -Id <guid> -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System the object belongs to. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes |  | The ID of the Connected System Object. Accepts pipeline input by property name. |
| `Search` | `string` | No (Page, All) |  | Returns only the references whose value or attribute name contains this text, ignoring case. |
| `Summary` | `switch` | Yes (Summary) |  | Returns the counts by likely cause and every distinct next step, rather than the references. |
| `All` | `switch` | Yes (All) |  | Fetches every page instead of one. Stops after 1000 pages with a warning; `-Force` fetches the rest. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page, past the 1000-page limit. |
| `Page` | `int` | No (Page) | `1` | The page to get. At least 1. |
| `PageSize` | `int` | No (Page, All) | `50` | How many references a page holds. From 1 to 100. |

### Output

The object's unresolved references, each with its likely cause and what to do; or with `-Summary`, how they split by cause and every distinct next step.

**By default, or with `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `AttributeName` | `string` | The attribute the value is on. |
| `Value` | `string` | The reference value as imported, for example a Distinguished Name. |
| `Cause` | `string` | Why the value most likely could not be resolved, judged against the current Container Scope. One of `NotFound`, `NotFoundInScope`, `ContainerNotInScope`, `ContainerExcluded`, `PartitionNotSelected`, `OutsideConnectedSystem` or `AmbiguousObjectType`. |
| `FixLocation` | `string` | Whether the fix lies in JIM's configuration or in the Connected System. One of `Jim` or `SourceSystem`. |
| `ContainerExternalId` | `string` | The Container the cause concerns: the one to select, the one excluding the value, or the one the value sits in while in scope. Empty where the cause concerns no Container. |
| `PartitionExternalId` | `string` | The partition the cause concerns: the one to select, for a value in a partition that is not selected, or the one holding a value that no Container JIM knows of holds. Empty otherwise. |
| `CandidateObjectTypeNames` | `string[]` | The Object Types an ambiguous value matches; empty for every other cause. |
| `Finding` | `string` | What is wrong, in words: why the value most likely did not resolve. Names the Container or partition concerned exactly as ContainerExternalId or PartitionExternalId holds it, where there is one. |
| `Remedy` | `string` | What to do about it, in words. |

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `TotalCount` | `int` | How many reference values on the object are unresolved. |
| `Causes` | `object[]` | The likely causes, most frequent first. Each has `Cause`, `FixLocation` and `Count`. |
| `Fixes` | `object[]` | Every distinct next step, with how many values each addresses, most values first: one per change to JIM's configuration (a Container to select, say), and one per cause whose fix lies in the Connected System. Each has `Cause`, `FixLocation`, `ContainerExternalId`, `PartitionExternalId`, `Count`, `ObjectCount`, `Finding` and `Remedy`. |
| `LargestFix` | `object` | The JIM configuration change that would address the most values (a Container to select, an exclusion to lift, a partition to select, or a reference target to declare), with how many it covers. Empty where every cause lies in the Connected System. It has `Cause`, `FixLocation`, `ContainerExternalId`, `PartitionExternalId`, `Count`, `ObjectCount`, `Finding` and `Remedy`. |
<!-- END GENERATED: Get-JIMConnectedSystemObjectUnresolvedReference -->

### Examples

```powershell title="List an object's unresolved references"
Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="See what would fix them"
(Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id "a1b2c3d4-..." -Summary).Fixes |
    Select-Object Count, FixLocation, Finding, Remedy
```

```powershell title="Every reference a Container Scope change would fix, by Container"
Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id "a1b2c3d4-..." -All |
    Where-Object { $_.Cause -eq 'ContainerNotInScope' } |
    Group-Object ContainerExternalId |
    Select-Object Count, Name
```

### Notes

- The references are read as they stand now on the object, so an import that resolves some of them shortens the list. To see how many references a particular import left unresolved, read `UnresolvedReferenceCount` from `Get-JIMActivityExecutionItem`.

---

## See also

- [Connected Systems](connected-systems.md): create, change and delete the Connected Systems whose Connector Space these cmdlets read
- [Run Profiles](run-profiles.md): run the imports that fill the Connector Space
- [Pending Exports](pending-exports.md): the changes waiting to be written to a Connected System
- [Password Synchronisation](password-synchronisation.md): how passwords reach Connected Systems
