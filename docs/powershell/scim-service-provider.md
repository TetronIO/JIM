---
title: SCIM Service Provider
description: "The cmdlets that manage the keys identity providers call a Connected System's SCIM endpoint with, and read what the endpoint holds and serves."
type: reference
audience: automation-author
---

# SCIM Service Provider

A Connected System that uses the SCIM 2.0 Service Provider Connector has a SCIM endpoint of its own, which identity providers push Users and Groups to. These cmdlets manage the keys those identity providers call it with, and read the endpoint's base URL, what it holds and the resource types and schemas it serves. The SCIM 2.0 Service Provider is a [Preview feature](../administration/preview-features.md); every cmdlet here fails while it is turned off.

!!! warning
    A key's secret is shown once, by `New-JIMScimKey`, and cannot be shown again. Keep it somewhere safe; if it is lost, create a new key.

A key works only on the SCIM endpoint of the Connected System it was created for, carries no Roles, and always expires. A Connected System can have at most two usable keys, so a key can be rotated with an overlap: create the new key, give it to the identity provider, then disable or remove the old one once its `LastUsedAt` stops moving.

---

## Get-JIMScimKey

Retrieves the keys of a Connected System's SCIM endpoint, usable or not. The secret is never returned; `KeyPrefix` identifies a key.

<!-- BEGIN GENERATED: Get-JIMScimKey -->
### Syntax

```powershell
# List (default)
Get-JIMScimKey -ConnectedSystemId <int> [-Name <string>]

# ById
Get-JIMScimKey -ConnectedSystemId <int> -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. Accepts pipeline input by property name, `ConnectedSystemId` or `ScopeConnectedSystemId`. |
| `Id` | `guid` | Yes (ById) |  | The ID of one key to return. Accepts pipeline input by property name. |
| `Name` | `string` | No (List) |  | Returns only the keys whose name matches, such as "Entra*". Supports wildcards. |

### Output

Each key, or the one key asked for.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | Unique identifier for the API key. |
| `Name` | `string` | Human-readable name for the API key. |
| `Description` | `string` | Optional description of the key's purpose. |
| `KeyPrefix` | `string` | Prefix of the key (e.g., "jim_ak_7f3a") for identification. |
| `CreatedAt` | `datetime` | When the API key was created. |
| `ExpiresAt` | `datetime` | When the key stops working. Always set for a SCIM client's key; empty only for an ordinary API key that never expires. |
| `LastUsedAt` | `datetime` | When the API key was last used for authentication. |
| `LastUsedFromIp` | `string` | IP address from which the key was last used. |
| `IsEnabled` | `bool` | Whether the API key is currently enabled. |
| `Roles` | `object[]` | The roles assigned to this API key. Each has `Id`, `Name`, `BuiltIn`, `Created` and `StaticMemberCount`. |
| `Scope` | `string` | What the key may be used for: `None` for an ordinary API key authorised by its roles, or `ScimServiceProvider` for a SCIM client's key, which works only on the SCIM endpoint of one Connected System and carries no roles. One of `None` or `ScimServiceProvider`. |
| `ScopeConnectedSystemId` | `int` | The Connected System a scoped key works for; empty for an ordinary API key. |
<!-- END GENERATED: Get-JIMScimKey -->

### Examples

```powershell title="See when each key expires and was last used"
Get-JIMScimKey -ConnectedSystemId 12 | Select-Object Name, IsEnabled, ExpiresAt, LastUsedAt
```

---

## New-JIMScimKey

Creates a key for an identity provider to call the Connected System's SCIM endpoint with, and returns it with its secret.

<!-- BEGIN GENERATED: New-JIMScimKey -->
### Syntax

```powershell
New-JIMScimKey -ConnectedSystemId <int> -Name <string> [-Description <string>] [-ExpiresAt <datetime>] [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. |
| `Name` | `string` | Yes |  | The name the key is shown by, typically naming the identity provider that will hold it. |
| `Description` | `string` | No |  | Optional description of what the key is for. |
| `ExpiresAt` | `datetime` | No |  | When the key stops working. Omit it for a year from now; it can be at most two years away. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the key's Configuration Change History. |

### Output

The key created, with its secret in Key. This is the only time JIM returns the secret.

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | The full API key. This is only returned on creation and cannot be retrieved again. |
| `Id` | `guid` | Unique identifier for the API key. |
| `Name` | `string` | Human-readable name for the API key. |
| `Description` | `string` | Optional description of the key's purpose. |
| `KeyPrefix` | `string` | Prefix of the key (e.g., "jim_ak_7f3a") for identification. |
| `CreatedAt` | `datetime` | When the API key was created. |
| `ExpiresAt` | `datetime` | When the key stops working. Always set for a SCIM client's key; empty only for an ordinary API key that never expires. |
| `LastUsedAt` | `datetime` | When the API key was last used for authentication. |
| `LastUsedFromIp` | `string` | IP address from which the key was last used. |
| `IsEnabled` | `bool` | Whether the API key is currently enabled. |
| `Roles` | `object[]` | The roles assigned to this API key. Each has `Id`, `Name`, `BuiltIn`, `Created` and `StaticMemberCount`. |
| `Scope` | `string` | What the key may be used for: `None` for an ordinary API key authorised by its roles, or `ScimServiceProvider` for a SCIM client's key, which works only on the SCIM endpoint of one Connected System and carries no roles. One of `None` or `ScimServiceProvider`. |
| `ScopeConnectedSystemId` | `int` | The Connected System a scoped key works for; empty for an ordinary API key. |
<!-- END GENERATED: New-JIMScimKey -->

### Examples

```powershell title="Create a key and show its secret"
$key = New-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning 2026"
$key.Key
```

```powershell title="Create a key that works for 30 days"
New-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning trial" -ExpiresAt (Get-Date).AddDays(30)
```

---

## Set-JIMScimKey

Renames, re-describes, re-dates, disables or re-enables a key. Only what is given changes.

<!-- BEGIN GENERATED: Set-JIMScimKey -->
### Syntax

```powershell
Set-JIMScimKey -ConnectedSystemId <int> -Id <guid> [-Name <string>] [-Description <string>] [-ExpiresAt <datetime>] [-Enable] [-Disable] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System the key is scoped to. Accepts pipeline input by property name, `ConnectedSystemId` or `ScopeConnectedSystemId`. |
| `Id` | `guid` | Yes |  | The ID of the key. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | The key's new name. |
| `Description` | `string` | No |  | The key's new description. `$null` or an empty string clears it. |
| `ExpiresAt` | `datetime` | No |  | When the key stops working, at most two years from now. |
| `Enable` | `switch` | No |  | Lets the key be used again. |
| `Disable` | `switch` | No |  | Stops the key working, keeping it on record. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the key's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the key as updated. |

### Output

With `-PassThru`, the key as updated; otherwise nothing.

Properties as under [Get-JIMScimKey](#get-jimscimkey).
<!-- END GENERATED: Set-JIMScimKey -->

### Examples

```powershell title="Rotate a key: create the new one, then disable the old one"
$new = New-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning 2026"
# Give $new.Key to the identity provider, and wait until it is using it.
Get-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning 2025" | Set-JIMScimKey -Disable -ChangeReason "Rotated to the 2026 key"
```

---

## Remove-JIMScimKey

Permanently deletes a key. An identity provider still using it is refused from that moment; to keep a record of the key, disable it instead.

<!-- BEGIN GENERATED: Remove-JIMScimKey -->
### Syntax

```powershell
Remove-JIMScimKey -ConnectedSystemId <int> -Id <guid> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System the key is scoped to. Accepts pipeline input by property name, `ConnectedSystemId` or `ScopeConnectedSystemId`. |
| `Id` | `guid` | Yes |  | The ID of the key. Accepts pipeline input by property name. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the key as it was before it was removed. |

### Output

With `-PassThru`, the key as it was before it was removed; otherwise nothing.

Properties as under [Get-JIMScimKey](#get-jimscimkey).
<!-- END GENERATED: Remove-JIMScimKey -->

### Examples

```powershell title="Remove every disabled key of one Connected System"
# Run without -Force first to see which keys it would remove.
Get-JIMScimKey -ConnectedSystemId 12 | Where-Object { -not $_.IsEnabled } | Remove-JIMScimKey -Force
```

---

## Get-JIMScimStoreSummary

Returns the base URL to give an identity provider and what the Connected System's SCIM endpoint holds: resources of each type, deleted resources kept until an import reads them, the changes the next import will read, and when an identity provider last wrote.

<!-- BEGIN GENERATED: Get-JIMScimStoreSummary -->
### Syntax

```powershell
Get-JIMScimStoreSummary -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

The summary.

| Property | Type | Description |
|----------|------|-------------|
| `BaseUrl` | `string` | The base URL to give an identity provider, as this request reached JIM. |
| `ResourceCounts` | `object[]` | How many resources of each type the store holds, not counting deleted ones. Each has `ResourceTypeName` and `Count`. |
| `Tombstones` | `int` | Resources a SCIM client has deleted, kept until an import has read the deletion. |
| `LastChangeSequence` | `long` | The number of the last change written to the store; zero when nothing has been written. |
| `ImportedUpTo` | `long` | The change the last successful import read up to; empty when the store has never been imported. |
| `ChangesAwaitingImport` | `int` | Resources changed since the last import, which the next import will read. |
| `LastWrite` | `datetime` | When a SCIM client last wrote to the store; empty when none has. |
| `RejectedLast24Hours` | `int` | Requests the endpoint refused in the last 24 hours, across every key, other than for authentication. Each key's SCIM request Activities name the latest rejection and count the rest. |
<!-- END GENERATED: Get-JIMScimStoreSummary -->

### Examples

```powershell title="Show the base URL and the import backlog"
Get-JIMConnectedSystem -Name "Entra provisioning" | Get-JIMScimStoreSummary | Select-Object BaseUrl, ChangesAwaitingImport
```

---

## Get-JIMScimResourceType

Returns the resource types the Connected System's SCIM endpoint serves. They are read-only.

<!-- BEGIN GENERATED: Get-JIMScimResourceType -->
### Syntax

```powershell
Get-JIMScimResourceType -ConnectedSystemId <int> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `Name` | `string` | No |  | Returns only the resource types whose name matches, such as "User". Supports wildcards. |

### Output

Each resource type.

| Property | Type | Description |
|----------|------|-------------|
| `Schemas` | `string[]` |  |
| `Id` | `string` |  |
| `Name` | `string` |  |
| `Description` | `string` |  |
| `Endpoint` | `string` |  |
| `Schema` | `string` |  |
| `SchemaExtensions` | `object[]` | Each has `Schema` and `Required`. |
| `Meta` | `object` | It has `ResourceType`, `Created`, `LastModified`, `Location` and `Version`. |
<!-- END GENERATED: Get-JIMScimResourceType -->

### Examples

```powershell title="List the resource types with their endpoints"
Get-JIMScimResourceType -ConnectedSystemId 12 | Select-Object Name, Endpoint, Schema
```

---

## Get-JIMScimSchema

Returns the schemas the Connected System's SCIM endpoint serves, with every attribute's characteristics. They are read-only.

<!-- BEGIN GENERATED: Get-JIMScimSchema -->
### Syntax

```powershell
Get-JIMScimSchema -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |

### Output

Each schema.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` |  |
| `Name` | `string` |  |
| `Description` | `string` |  |
| `Attributes` | `object[]` | Each has `Name`, `Type`, `Description`, `SubAttributes`, `MultiValued`, `Required`, `CanonicalValues`, `CaseExact`, `Mutability`, `Returned`, `Uniqueness` and `ReferenceTypes`. |
| `Meta` | `object` | It has `ResourceType`, `Created`, `LastModified`, `Location` and `Version`. |
<!-- END GENERATED: Get-JIMScimSchema -->

### Examples

```powershell title="List the attributes of the User schema"
(Get-JIMScimSchema -ConnectedSystemId 12 | Where-Object { $_.Name -eq 'User' }).Attributes | Select-Object Name, Type, Required
```

---

## Get-JIMScimJournal

Returns the requests an identity provider made to one resource, oldest first: what each did, the attributes it changed or ignored (by name, never by value), and the key and address that made it. With `-ChangeId`, returns the requests behind one imported change in a Connected System Object's change history.

<!-- BEGIN GENERATED: Get-JIMScimJournal -->
### Syntax

```powershell
# ByResource (default)
Get-JIMScimJournal -ConnectedSystemId <int> -ResourceId <guid> [-AfterSequence <long>] [-UpToSequence <long>]

# ByChange
Get-JIMScimJournal -ConnectedSystemId <int> -ChangeId <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector. |
| `ResourceId` | `guid` | Yes (ByResource) |  | The SCIM resource, which is the Connected System Object's external id. |
| `AfterSequence` | `long` | No (ByResource) |  | Only writes after this change number. |
| `UpToSequence` | `long` | No (ByResource) |  | Only writes up to and including this change number. |
| `ChangeId` | `guid` | Yes (ByChange) |  | The ID of a change in a Connected System Object's change history, to get the requests behind it. |

### Output

Each journal entry, oldest first; with `-ChangeId`, the requests behind the change.

**With `-ResourceId`**:

| Property | Type | Description |
|----------|------|-------------|
| `ChangeSequence` | `long` | The store's change number for the write; an import reads every write after the one it last read. |
| `Occurred` | `datetime` | When the write was accepted. |
| `ResourceId` | `guid` | The resource written, which is the Connected System Object's external id. |
| `ResourceTypeName` | `string` | The resource's type, for example `User`. |
| `Operation` | `string` | What the write did: `Created`, `Replaced`, `Patched`, `Deleted`, or `ReferenceRemovedByDeletion` for a membership removed because the resource it pointed to was deleted. One of `Created`, `Replaced`, `Patched`, `Deleted` or `ReferenceRemovedByDeletion`. |
| `ChangedAttributes` | `string[]` | The SCIM attributes the write changed, by name. |
| `IgnoredAttributes` | `string[]` | Attributes the request sent that the store does not keep, by name. |
| `ApiKeyId` | `guid` | The key the write was made with; empty once that key has been deleted. |
| `ApiKeyName` | `string` | The key's name when the write was made. |
| `ClientAddress` | `string` | The address the request came from. |

**With `-ChangeId`**:

| Property | Type | Description |
|----------|------|-------------|
| `ResourceId` | `guid` | The resource, which is the Connected System Object's external id. |
| `AfterSequence` | `long` | The change number the import started after; empty for an import that read from the start. |
| `UpToSequence` | `long` | The change number the import read up to, inclusive. |
| `Entries` | `object[]` | The resource's accepted writes in that range, oldest first. Each has `ChangeSequence`, `Occurred`, `ResourceId`, `ResourceTypeName`, `Operation`, `ChangedAttributes`, `IgnoredAttributes`, `ApiKeyId`, `ApiKeyName` and `ClientAddress`. |
<!-- END GENERATED: Get-JIMScimJournal -->

### Examples

```powershell title="See what each request did to a resource, and which key made it"
Get-JIMScimJournal -ConnectedSystemId 12 -ResourceId 2819c223-7f76-453a-919d-413861904646 | Select-Object Occurred, Operation, ChangedAttributes, ApiKeyName
```

```powershell title="See the requests behind one imported change"
(Get-JIMScimJournal -ConnectedSystemId 12 -ChangeId $changeId).Entries
```
