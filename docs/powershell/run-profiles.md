---
title: Run Profiles
---

# Run Profiles

Run Profile cmdlets manage and execute synchronisation Run Profiles on Connected Systems. A Run Profile defines a specific operation type (import, synchronisation, or export) that can be executed against a Connected System. Use these cmdlets to list, create, modify, remove, and trigger Run Profiles.

---

## Get-JIMRunProfile

Retrieves one or more Run Profiles for a Connected System, identified either by Connected System ID or Connected System name, with an optional name filter.

### Syntax

```powershell
# By Connected System ID (default)
Get-JIMRunProfile -ConnectedSystemId <int> [-Name <string>]

# By Connected System name
Get-JIMRunProfile -ConnectedSystemName <string> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System. Alias: `Id`. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System. Must be an exact match. |
| `Name` | `string` | No | | Filter Run Profiles by name. Supports wildcards (e.g., `"Full*"`). |

### Output

Returns one or more `PSCustomObject` instances representing Run Profiles, each containing `Id`, `Name`, `ConnectedSystemId`, `RunType`, `PageSize`, `PartitionName`, and `FilePath`.

### Examples

```powershell title="List all Run Profiles for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1
```

```powershell title="Filter by name"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import"
```

```powershell title="Filter by name with wildcards"
Get-JIMRunProfile -ConnectedSystemName "HR System" -Name "Delta*"
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMRunProfile
```

---

## New-JIMRunProfile

Creates a new Run Profile on a Connected System. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
# By Connected System ID (default)
New-JIMRunProfile -ConnectedSystemId <int> -Name <string> -RunType <string> [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-PassThru] [-WhatIf] [-Confirm]

# By Connected System name
New-JIMRunProfile -ConnectedSystemName <string> -Name <string> -RunType <string> [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System to create the Run Profile on. Alias: `Id`. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System to create the Run Profile on. |
| `Name` | `string` | Yes (Position 0) | | Display name for the new Run Profile. |
| `RunType` | `string` | Yes | | Operation type. Valid values: `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation`, `Export`. |
| `PageSize` | `int` | No | `100` | How many items to process in one batch. |
| `PartitionId` | `int` | No | | Optional partition to scope this Run Profile to. If omitted, the Run Profile applies to the default partition. |
| `FilePath` | `string` | No | | Optional file path for file-based connectors. |
| `PassThru` | `switch` | No | `$false` | Returns the created Run Profile object to the pipeline. |

### Output

By default, no output. When `-PassThru` is specified, returns the created Run Profile object.

### Examples

```powershell title="Create a full import Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -RunType FullImport
```

```powershell title="Create a Run Profile by Connected System name"
New-JIMRunProfile -ConnectedSystemName "Active Directory" -Name "Delta Synchronisation" -RunType DeltaSynchronisation
```

```powershell title="Create and capture the result"
$rp = New-JIMRunProfile -ConnectedSystemId 1 -Name "Export" -RunType Export -PassThru
$rp.Id
```

```powershell title="Create a Run Profile with a custom page size"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -PageSize 500 -PassThru
```

```powershell title="Create a partition-scoped Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (UK)" -RunType FullImport -PartitionId 3
```

```powershell title="Create a Run Profile for a file-based connector"
Get-JIMConnectedSystem -Name "CSV*" | ForEach-Object {
    New-JIMRunProfile -ConnectedSystemId $_.Id -Name "Full Import" -RunType FullImport -FilePath "C:\Data\import.csv"
}
```

```powershell title="Preview with WhatIf"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -WhatIf
```

---

## Set-JIMRunProfile

Modifies an existing Run Profile. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
# By Connected System ID (default)
Set-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-PassThru] [-WhatIf] [-Confirm]

# By Connected System name
Set-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-PassThru] [-WhatIf] [-Confirm]

# By input object
Set-JIMRunProfile -InputObject <PSCustomObject> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System the Run Profile belongs to. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System the Run Profile belongs to. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByName sets) | | ID of the Run Profile to update. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A Run Profile object, typically from `Get-JIMRunProfile`. Accepts pipeline input. |
| `Name` | `string` | No | | New display name for the Run Profile. |
| `PageSize` | `int` | No | | New page size for the Run Profile. Omit to leave unchanged. |
| `PartitionId` | `int` | No | | New partition ID to scope the Run Profile to. |
| `FilePath` | `string` | No | | New file path for file-based connectors. Omit to leave unchanged. |
| `PassThru` | `switch` | No | `$false` | Returns the updated Run Profile object to the pipeline. |

### Output

By default, no output. When `-PassThru` is specified, returns the updated Run Profile object.

### Examples

```powershell title="Rename a Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Name "Full Import (Production)"
```

```powershell title="Update by Connected System name"
Set-JIMRunProfile -ConnectedSystemName "Contoso AD" -RunProfileId 1 -PageSize 500
```

```powershell title="Update via pipeline"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" | Set-JIMRunProfile -Name "Full Import (Renamed)" -PassThru
```

```powershell title="Change partition assignment"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -PartitionId 5
```

```powershell title="Update a file-based connector's Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -FilePath "C:\Data\import-v2.csv"
```

```powershell title="Preview changes with WhatIf"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Name "New Name" -WhatIf
```

---

## Remove-JIMRunProfile

Deletes a Run Profile. This is a destructive operation with high impact; by default, PowerShell will prompt for confirmation. Use `-Force` to suppress the confirmation prompt. Supports `ShouldProcess`.

### Syntax

```powershell
# By Connected System ID (default)
Remove-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# By Connected System name
Remove-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# By input object
Remove-JIMRunProfile -InputObject <PSCustomObject> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System the Run Profile belongs to. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System the Run Profile belongs to. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByName sets) | | ID of the Run Profile to delete. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A Run Profile object, typically from `Get-JIMRunProfile`. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No | `$false` | Returns the deleted Run Profile object to the pipeline. |

### Output

By default, no output. When `-PassThru` is specified, returns the Run Profile object that was deleted.

### Examples

```powershell title="Delete a Run Profile by ID"
Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42
```

```powershell title="Delete without confirmation"
Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Force
```

```powershell title="Delete using Connected System name"
Remove-JIMRunProfile -ConnectedSystemName "Contoso AD" -RunProfileId 42 -Force
```

```powershell title="Delete via pipeline"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Test Profile" | Remove-JIMRunProfile -Force
```

```powershell title="Delete all Run Profiles for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 | Remove-JIMRunProfile -Force
```

```powershell title="Capture before deleting"
$deleted = Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Force -PassThru
Write-Host "Removed Run Profile: $($deleted.Name)"
```

---

## Start-JIMRunProfile

Queues a Run Profile for execution on the JIM worker service. The operation is asynchronous by default; use `-Wait` to block until execution completes.

Every parameter set requires both a Connected System identifier (`-ConnectedSystemId` or `-ConnectedSystemName`) and a Run Profile identifier (`-RunProfileId` or `-RunProfileName`); a Run Profile identifier alone does not resolve to a parameter set.

### Syntax

```powershell
# By Connected System ID and Run Profile ID (default)
Start-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Wait] [-Timeout <int>] [-PassThru]

# By Connected System name and Run Profile name
Start-JIMRunProfile -ConnectedSystemName <string> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PassThru]

# By Connected System ID and Run Profile name
Start-JIMRunProfile -ConnectedSystemId <int> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PassThru]

# By Connected System name and Run Profile ID
Start-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Wait] [-Timeout <int>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById, ByIdAndName sets) | | ID of the Connected System that owns the Run Profile. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByName, ByNameAndId sets) | | Name of the Connected System that owns the Run Profile. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByNameAndId sets) | | ID of the Run Profile to execute. Alias: `Id`. Accepts pipeline input by property name (ById set). |
| `RunProfileName` | `string` | Yes (ByName, ByIdAndName sets) | | Name of the Run Profile to execute. Must be an exact match. |
| `Wait` | `switch` | No | `$false` | Blocks until execution completes, displaying a progress indicator. Polls every 2 seconds. |
| `Timeout` | `int` | No | | Maximum number of seconds to wait when `-Wait` is specified. If exceeded, an error is thrown containing the Activity ID for manual follow-up. |
| `PassThru` | `switch` | No | `$false` | Returns the execution response object to the pipeline. |

### Output

By default, writes status messages to the host. When `-PassThru` is specified, returns a `PSCustomObject` with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `Guid` | ID of the Activity created for tracking the execution |
| `TaskId` | `Guid` | ID of the queued worker task |
| `Message` | `string` | Message describing the result of queuing the execution |
| `Warnings` | `string[]` | Any warning messages about the execution (e.g. partition validation warnings). Empty if none |

### Examples

```powershell title="Start a Run Profile by ID"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42
```

```powershell title="Start by name"
Start-JIMRunProfile -ConnectedSystemName "Active Directory" -RunProfileName "Full Import"
```

```powershell title="Start and wait for completion"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait
```

```powershell title="Start with a timeout"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -Timeout 300
```

```powershell title="Capture execution details"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -PassThru
Write-Host "Activity ID: $($result.ActivityId)"
Write-Host "Task ID: $($result.TaskId)"
if ($result.Warnings.Count -gt 0) {
    Write-Warning "Warnings: $($result.Warnings -join '; ')"
}
```

```powershell title="Pipeline: start all full imports for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 |
    Where-Object { $_.RunType -eq "FullImport" } |
    Start-JIMRunProfile -Wait
```

```powershell title="Automation with error handling"
try {
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -Timeout 600 -PassThru -ErrorAction Stop
} catch {
    Write-Error "Run Profile execution failed or timed out: $_"
}
```

### Notes

- The Run Profile is queued as an asynchronous task on the JIM worker service. Without `-Wait`, the cmdlet returns immediately after the task is queued.
- When `-Wait` is specified, the cmdlet polls the Activity status every 2 seconds and displays a progress bar. If authentication tokens expire during polling, the cmdlet retries up to 3 times before failing.
- If `-Timeout` is exceeded, the cmdlet throws a terminating error that includes the Activity ID, allowing you to check progress manually via [Get-JIMActivity](activities.md) or the JIM web interface.
- Run Profiles that are already executing will be rejected by the server; you do not need to check for running profiles before calling this cmdlet.

---

## See also

- [Run Profiles](../configuration/run-profiles.md): what Run Profiles are, run types, and how they fit alongside schedules and activities
- [Activities](activities.md): cmdlets for monitoring and inspecting activity execution history
- [Connected Systems](connected-systems.md): cmdlets for managing Connected Systems
