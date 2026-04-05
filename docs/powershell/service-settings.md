---
title: Service Settings
---

# Service Settings

Cmdlets for viewing and modifying JIM runtime configuration. Settings use dot-notation keys and control operational behaviour such as change tracking, sync page sizes, and history retention.

!!! info
    Setting values are always transmitted as strings. The server interprets the type based on the setting definition (Boolean, Integer, TimeSpan, etc.).

---

## Get-JIMServiceSetting

Retrieves service settings. When called without parameters, returns all settings. Specify `-Key` to retrieve a single setting by its dot-notation key.

### Syntax

```powershell
# List (default)
Get-JIMServiceSetting

# ByKey
Get-JIMServiceSetting [-Key] <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Key` | `string` | Yes (ByKey, Position 0) | | Dot-notation setting key, e.g. `"ChangeTracking.CsoChanges.Enabled"`. |

### Output

Returns one or more `PSCustomObject` instances with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | Dot-notation setting key |
| `Value` | `string` | Current stored value (may be null if no override is set) |
| `EffectiveValue` | `string` | The value in effect: the override if set, otherwise the default |
| `IsOverridden` | `bool` | Whether an administrator has set a custom value |
| `IsReadOnly` | `bool` | Whether the setting can be modified at runtime |
| `Description` | `string` | Human-readable description of the setting |
| `Category` | `string` | Logical grouping, e.g. `"ChangeTracking"`, `"Sync"`, `"History"` |

### Examples

```powershell title="List all settings"
Get-JIMServiceSetting
```

```powershell title="Get a specific setting by key"
Get-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled"
```

```powershell title="Filter settings by category"
Get-JIMServiceSetting | Where-Object { $_.Category -eq "Sync" }
```

---

## Set-JIMServiceSetting

Updates a service setting value. Read-only settings cannot be modified; attempting to do so produces a non-terminating error. Each change creates an audit activity. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Set-JIMServiceSetting [-Key] <string> [-Value] <string> [-PassThru]
    [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Key` | `string` | Yes (Position 0) | | Dot-notation setting key to update |
| `Value` | `string` | Yes (Position 1) | | New value for the setting. Accepts null or empty strings. |
| `PassThru` | `switch` | No | `$false` | Returns the updated setting object |

### Output

When `-PassThru` is specified, returns the updated service setting object. Otherwise, no output.

### Examples

```powershell title="Disable change tracking"
Set-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled" -Value "false"
```

```powershell title="Set an integer value (sync page size)"
Set-JIMServiceSetting -Key "Sync.PageSize" -Value "500"
```

```powershell title="Set a TimeSpan value (history retention)"
Set-JIMServiceSetting -Key "History.RetentionPeriod" -Value "90.00:00:00" -PassThru
```

### Notes

- The `Value` parameter is always a string. The server interprets the type based on the setting definition (Boolean, Integer, TimeSpan, etc.).
- Read-only settings throw a non-terminating error when modification is attempted.
- Each successful update creates an audit activity recording the previous and new values.

---

## Reset-JIMServiceSetting

Reverts a service setting to its default value by clearing the administrator override. Read-only settings cannot be reverted; attempting to do so produces a non-terminating error. Each reset creates an audit activity. Supports `ShouldProcess` (Medium impact); use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Reset-JIMServiceSetting [-Key] <string> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Key` | `string` | Yes (Position 0) | | Dot-notation setting key to reset. Accepts pipeline input by property name. |
| `PassThru` | `switch` | No | `$false` | Returns the setting object after the reset |

### Output

When `-PassThru` is specified, returns the service setting object with its restored default value. Otherwise, no output.

### Examples

```powershell title="Reset a single setting to its default"
Reset-JIMServiceSetting -Key "Sync.PageSize"
```

```powershell title="Reset all overridden settings via pipeline"
Get-JIMServiceSetting | Where-Object { $_.IsOverridden } |
    Reset-JIMServiceSetting
```

### Notes

- Resetting clears the stored override; the `EffectiveValue` reverts to the setting's built-in default.
- Each successful reset creates an audit activity recording the change.
- Read-only settings throw a non-terminating error when a reset is attempted.

---

## See also

- [Service Settings API](../api/service-settings/index.md): REST API reference for service settings
- [Activities](activities.md): viewing audit activities created by setting changes
- [Connection](connection.md): establishing and managing connections to JIM
