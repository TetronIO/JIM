---
title: API Keys
description: "The cmdlets that create, read, change and delete API keys, including their roles and whether they are enabled."
type: reference
audience: automation-author
---

# API Keys

The API key cmdlets manage the full lifecycle of API keys used for non-interactive authentication with JIM. You can create, retrieve, update, and delete API keys, as well as control their enabled state and role assignments.

!!! warning
    The full API key value is only visible at creation time. After creation, only the key prefix is returned for identification purposes. Store the key securely immediately; it cannot be retrieved again.

---

## :material-key-outline: Get-JIMApiKey

Retrieves API key information. The full key value is never returned; only the key prefix is shown for identification.

<!-- BEGIN GENERATED: Get-JIMApiKey -->
### Syntax

```powershell
# List (default)
Get-JIMApiKey [-Name <string>]

# ById
Get-JIMApiKey -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of a specific API Key to retrieve. Accepts pipeline input by property name. |
| `Name` | `string` | No (List) |  | Filters API Keys by name, such as "CI*". Supports wildcards. |

### Output

Each API Key. The key itself is never returned; KeyPrefix identifies it.

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
<!-- END GENERATED: Get-JIMApiKey -->

### Examples

```powershell title="List all API keys"
Get-JIMApiKey
```

```powershell title="Filter by name"
Get-JIMApiKey -Name "CI*"
```

```powershell title="Get a specific API key by ID"
Get-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

---

## :material-key-plus: New-JIMApiKey

Creates a new API key for non-interactive authentication.

<!-- BEGIN GENERATED: New-JIMApiKey -->
### Syntax

```powershell
New-JIMApiKey -Name <string> [-Description <string>] [-RoleIds <int[]>] [-ExpiresAt <datetime>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name the API Key is shown by. |
| `Description` | `string` | No |  | Optional description of what the API Key is for. |
| `RoleIds` | `int[]` | No | `@()` | The IDs of the Roles the API Key has. |
| `ExpiresAt` | `datetime` | No |  | When the API Key expires. Omit it for a key that does not expire. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the API Key's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the API Key created, including the key itself. |

### Output

With `-PassThru`, the API Key created, with the key itself in Key; otherwise nothing. This is the only time JIM returns the key, so store it securely: without `-PassThru` it cannot be retrieved.

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
<!-- END GENERATED: New-JIMApiKey -->

### Examples

```powershell title="Create a basic API key"
New-JIMApiKey -Name "CI Pipeline"
```

```powershell title="Create with expiry and role assignments"
New-JIMApiKey -Name "Monitoring Service" -Description "Read-only monitoring" `
    -RoleIds @(1, 3) -ExpiresAt (Get-Date).AddDays(90)
```

```powershell title="Create and store the key in a variable"
$key = New-JIMApiKey -Name "Deployment Automation" -PassThru
$key.Key  # Full key value; store this securely
```

### Notes

- The full API key is returned **only** in the creation response when `-PassThru` is specified. Store it securely; it cannot be retrieved again.
- Use `-RoleIds` to scope the key's permissions. A key with no roles has no administrative access.
- Use `-ExpiresAt` to enforce key rotation policies. Keys without an expiry remain valid until manually disabled or deleted.

---

## :material-key-change: Set-JIMApiKey

Updates an existing API key's name, description, roles, expiry, or enabled status. The key value itself cannot be changed.

<!-- BEGIN GENERATED: Set-JIMApiKey -->
### Syntax

```powershell
Set-JIMApiKey -Id <guid> [-Name <string>] [-Description <string>] [-RoleIds <int[]>] [-ExpiresAt <datetime>] [-Enable] [-Disable] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the API Key to update. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | The new name. |
| `Description` | `string` | No |  | The new description. |
| `RoleIds` | `int[]` | No |  | The IDs of the Roles the API Key has, replacing those it had. Omit it to keep its Roles. |
| `ExpiresAt` | `datetime` | No |  | When the API Key expires. `$null` removes the expiry. |
| `Enable` | `switch` | No |  | Enables the API Key. |
| `Disable` | `switch` | No |  | Disables the API Key without removing it. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the API Key's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the API Key as updated. |

### Output

With `-PassThru`, the API Key as updated; otherwise nothing.

Properties as under [Get-JIMApiKey](#get-jimapikey).
<!-- END GENERATED: Set-JIMApiKey -->

### Examples

```powershell title="Update the name of an API key"
Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Name "CI Pipeline v2"
```

```powershell title="Disable an API key"
Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Disable
```

```powershell title="Enable a key and set a new expiry"
Set-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Enable `
    -ExpiresAt (Get-Date).AddDays(30)
```

```powershell title="Batch disable all expired keys from pipeline"
Get-JIMApiKey | Where-Object { $_.ExpiresAt -and $_.ExpiresAt -lt (Get-Date) } |
    Set-JIMApiKey -Disable
```

### Notes

- `-Enable` and `-Disable` are mutually exclusive; specify only one.
- Setting `-ExpiresAt $null` removes the expiry, making the key valid indefinitely (until disabled or deleted).
- `-RoleIds` replaces all existing role assignments. To keep current roles, include them in the new array.

---

## :material-key-remove: Remove-JIMApiKey

Permanently deletes an API key. Any requests using this key will fail immediately after deletion.

<!-- BEGIN GENERATED: Remove-JIMApiKey -->
### Syntax

```powershell
# ById (default)
Remove-JIMApiKey -Id <guid> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMApiKey -InputObject <PSCustomObject> [-ChangeReason <string>] [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) |  | The unique identifier (GUID) of the API Key to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | The API Key to remove, as `Get-JIMApiKey` returns it. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the API Key's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the API Key as it was before it was removed. |

### Output

With `-PassThru`, the API Key as it was before it was removed; otherwise nothing.

Properties as under [Get-JIMApiKey](#get-jimapikey).
<!-- END GENERATED: Remove-JIMApiKey -->

### Examples

```powershell title="Delete by ID (prompts for confirmation)"
Remove-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Force delete without confirmation"
Remove-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Force
```

```powershell title="Pipeline delete from Get-JIMApiKey"
Get-JIMApiKey | Where-Object { $_.Name -like "temp-*" } | Remove-JIMApiKey -Force
```

### Notes

- Deletion is permanent and cannot be undone. A new key must be created if access is needed again.
- Any active sessions or automation using the deleted key will fail immediately.

---

## :material-book-open-variant-outline: See also

- [API Keys](../configuration/api-keys.md): what API keys are, how they're managed, and the surfaces (UI, PowerShell, REST API) for working with them
- [Connection](connection.md): establishing and managing connections to JIM
- [Security](security.md): security configuration and best practices
