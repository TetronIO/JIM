---
title: Service Settings
description: "The cmdlets that read Service Settings, change them, and return them to their defaults."
type: reference
audience: automation-author
---

# Service Settings

Cmdlets for viewing and modifying JIM runtime configuration. Settings use dot-notation keys and control operational behaviour such as change tracking, sync page sizes, and history retention.

!!! info
    Setting values are always transmitted as strings. The server interprets the type based on the setting definition (Boolean, Integer, TimeSpan, etc.).

---

## Get-JIMServiceSetting

Retrieves service settings. When called without parameters, returns all settings. Specify `-Key` to retrieve a single setting by its dot-notation key.

<!-- BEGIN GENERATED: Get-JIMServiceSetting -->
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
| `Key` | `string` | Yes (ByKey) |  | The key of the one setting to return, such as "ChangeTracking.CsoChanges.Enabled". Can be given by position, as the first argument. |

### Output

Each service setting, with its default, its stored value and the value in effect.

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | Unique setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled"). |
| `DisplayName` | `string` | Human-readable display name. |
| `Description` | `string` | Description of what this setting controls. |
| `Category` | `string` | Category for grouping (e.g., "SSO", "Synchronisation", "Maintenance"). |
| `ValueType` | `string` | Data type of the setting value (e.g., "String", "Boolean", "Integer"). |
| `DefaultValue` | `string` | The default value as a string. |
| `Value` | `string` | The current overridden value, or empty if using the default. |
| `EffectiveValue` | `string` | The effective value (Value if set, otherwise DefaultValue). |
| `IsReadOnly` | `bool` | Whether this setting is read-only (e.g., mirrored from environment variables). |
| `IsOverridden` | `bool` | Whether the current value differs from the default. |
<!-- END GENERATED: Get-JIMServiceSetting -->

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

```powershell title="Check the post-clear reconciliation shortfall threshold"
Get-JIMServiceSetting -Key "Sync.PostClearReconciliation.MaxMissingPercent"
```

---

## Set-JIMServiceSetting

Updates a service setting value. Read-only settings cannot be modified; attempting to do so produces a non-terminating error. Each change creates an audit activity. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

<!-- BEGIN GENERATED: Set-JIMServiceSetting -->
### Syntax

```powershell
Set-JIMServiceSetting [-Key] <string> [-Value] <string> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Key` | `string` | Yes |  | The key of the setting to change, such as "ChangeTracking.CsoChanges.Enabled". Can be given by position, as the first argument. |
| `Value` | `string` | Yes |  | The new value, as a string. Can be given by position, as the second argument. |
| `ChangeReason` | `string` | No |  | Optional reason for the change, recorded on the audit Activity and shown in the setting's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the setting as updated. |

### Output

With `-PassThru`, the setting as updated; otherwise nothing.

Properties as under [Get-JIMServiceSetting](#get-jimservicesetting).
<!-- END GENERATED: Set-JIMServiceSetting -->

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

```powershell title="Raise the post-clear reconciliation shortfall threshold to 50%"
Set-JIMServiceSetting -Key "Sync.PostClearReconciliation.MaxMissingPercent" -Value "50"
```

### Notes

- The `Value` parameter is always a string. The server interprets the type based on the setting definition (Boolean, Integer, TimeSpan, etc.).
- Read-only settings throw a non-terminating error when modification is attempted.
- Each successful update creates an audit activity recording the previous and new values.

---

## Reset-JIMServiceSetting

Reverts a service setting to its default value by clearing the administrator override. Read-only settings cannot be reverted; attempting to do so produces a non-terminating error. Each reset creates an audit activity. Supports `ShouldProcess` (Medium impact); use `-WhatIf` or `-Confirm` to preview or confirm the operation.

<!-- BEGIN GENERATED: Reset-JIMServiceSetting -->
### Syntax

```powershell
Reset-JIMServiceSetting [-Key] <string> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Key` | `string` | Yes |  | The key of the setting to revert, such as "ChangeTracking.CsoChanges.Enabled". Can be given by position, as the first argument. Accepts pipeline input by property name. |
| `ChangeReason` | `string` | No |  | Optional reason for the revert, recorded on the audit Activity and shown in the setting's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the setting as reverted. |

### Output

With `-PassThru`, the setting as reverted; otherwise nothing.

Properties as under [Get-JIMServiceSetting](#get-jimservicesetting).
<!-- END GENERATED: Reset-JIMServiceSetting -->

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

- [Service Settings](../configuration/service-settings.md): what service settings are, default vs override behaviour, and read-only environment-mirrored settings
- [Activities](activities.md): viewing audit activities created by setting changes
- [Connection](connection.md): establishing and managing connections to JIM
