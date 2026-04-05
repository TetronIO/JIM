---
title: API Keys
---

# API Keys

The API key cmdlets manage the full lifecycle of API keys used for non-interactive authentication with JIM. You can create, retrieve, update, and delete API keys, as well as control their enabled state and role assignments.

!!! warning
    The full API key value is only visible at creation time. After creation, only the key prefix is returned for identification purposes. Store the key securely immediately; it cannot be retrieved again.

---

## Get-JIMApiKey

Retrieves API key information. The full key value is never returned; only the key prefix is shown for identification.

### Syntax

```powershell
# List (default)
Get-JIMApiKey

# ById
Get-JIMApiKey -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `Guid` | Yes (ById) | | Unique identifier of the API key to retrieve. Accepts pipeline input by property name. |

### Output

Returns one or more `PSCustomObject` instances representing API keys. The full key value is never included; only the key prefix is returned for identification.

### Examples

```powershell title="List all API keys"
Get-JIMApiKey
```

```powershell title="Get a specific API key by ID"
Get-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

---

## New-JIMApiKey

Creates a new API key for non-interactive authentication. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
New-JIMApiKey [-Name] <string> [-Description <string>] [-RoleIds <int[]>]
    [-ExpiresAt <datetime>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the API key |
| `Description` | `string` | No | | Optional description of the key's purpose |
| `RoleIds` | `int[]` | No | `@()` | Role IDs to assign to the key. If omitted, the key has no role assignments. |
| `ExpiresAt` | `DateTime` | No | | Optional expiry date and time. If omitted, the key does not expire. |
| `PassThru` | `switch` | No | `$false` | Returns the created API key object, including the full key value |

### Output

When `-PassThru` is specified, returns the newly created API key object. This is the **only** response that includes the full key value. Otherwise, no output.

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

## Set-JIMApiKey

Updates an existing API key's name, description, roles, expiry, or enabled status. The key value itself cannot be changed. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Set-JIMApiKey -Id <guid> [-Name <string>] [-Description <string>]
    [-RoleIds <int[]>] [-ExpiresAt <datetime?>] [-Enable] [-Disable]
    [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `Guid` | Yes | | Unique identifier of the API key to update. Accepts pipeline input by property name. |
| `Name` | `string` | No | | New display name for the key |
| `Description` | `string` | No | | New description |
| `RoleIds` | `int[]` | No | | New set of role IDs. Replaces all existing role assignments. |
| `ExpiresAt` | `DateTime?` | No | | New expiry date and time. Pass `$null` to remove the expiry entirely. |
| `Enable` | `switch` | No | `$false` | Enables the API key |
| `Disable` | `switch` | No | `$false` | Disables the API key. Disabled keys reject all authentication attempts. |
| `PassThru` | `switch` | No | `$false` | Returns the updated API key object |

### Output

When `-PassThru` is specified, returns the updated API key object. Otherwise, no output.

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

## Remove-JIMApiKey

Permanently deletes an API key. Any requests using this key will fail immediately after deletion. Supports `ShouldProcess` (High impact); use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
# ById (default)
Remove-JIMApiKey -Id <guid> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMApiKey -InputObject <PSCustomObject> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `Guid` | Yes (ById) | | Unique identifier of the API key to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | API key object from the pipeline, as returned by `Get-JIMApiKey`. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted API key object |

### Output

When `-PassThru` is specified, returns the deleted API key object. Otherwise, no output.

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
- Without `-Force`, you are prompted for confirmation before each deletion (High impact `ShouldProcess`).
- Any active sessions or automation using the deleted key will fail immediately.

---

## See also

- [API Keys](../api/api-keys/index.md): REST API reference for API key management
- [Connection](connection.md): establishing and managing connections to JIM
- [Security](security.md): security configuration and best practices
