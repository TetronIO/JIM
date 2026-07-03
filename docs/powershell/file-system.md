---
title: File System
---

# File System

File System cmdlets let you browse the JIM Container's file system, used when configuring file-based connectors (e.g. CSV) to select import/export paths. Only paths within the configured allowed mount points are accessible.

---

## Get-JIMFileSystemItem

Lists files and directories within the JIM Container's allowed mount points.

### Syntax

```powershell
# List (default)
Get-JIMFileSystemItem [-Path <string>]

# Roots
Get-JIMFileSystemItem -Roots
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Path` | `string` | No | | The directory path to list. Omit to list the allowed root directories. |
| `Roots` | `switch` | No | `$false` | Returns the allowed root directory paths instead of listing a directory. |

### Output

A directory listing (path, entries, parent path), or a list of allowed root paths.

### Examples

```powershell title="List the allowed root directories"
Get-JIMFileSystemItem
```

```powershell title="List the contents of a directory"
Get-JIMFileSystemItem -Path "/data/imports"
```

```powershell title="Get the allowed root paths explicitly"
Get-JIMFileSystemItem -Roots
```

---

## Test-JIMFileSystemPath

Checks whether a file system path is within the JIM Container's allowed mount points.

### Syntax

```powershell
Test-JIMFileSystemPath -Path <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Path` | `string` | Yes | | The path to validate. Accepts pipeline input by property name. |

### Output

Boolean. `$true` if the path is within an allowed root, `$false` otherwise.

### Examples

```powershell title="Validate a path before configuring a connector"
Test-JIMFileSystemPath -Path "/data/imports/users.csv"
```

---

## See also

- [Connected Systems](connected-systems.md): cmdlets for configuring file-based connectors that read paths validated here
