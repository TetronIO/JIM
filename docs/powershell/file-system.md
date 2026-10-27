---
title: File System
description: "The cmdlets that browse and test the paths JIM can read and write, for choosing a file-based Connector's import and export paths."
type: reference
audience: automation-author
---

# File System

File System cmdlets let you browse the JIM Container's file system, used when configuring file-based connectors (e.g. CSV) to select import/export paths. Only paths within the configured allowed mount points are accessible.

---

## :material-folder-search-outline: Get-JIMFileSystemItem

Lists files and directories within the JIM Container's allowed mount points.

<!-- BEGIN GENERATED: Get-JIMFileSystemItem -->
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
| `Path` | `string` | No (List) |  | The directory to list. Omit it to list the allowed root directories. |
| `Roots` | `switch` | Yes (Roots) |  | Returns the allowed root directory paths instead of listing a directory. |

### Output

Listing returns the directory's contents; `-Roots` returns each allowed root directory's path.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Path` | `string` | The path that was listed. |
| `Entries` | `object[]` | The entries (files and directories) in the directory. Each has `Name`, `FullPath` and `IsDirectory`. |
| `ParentPath` | `string` | The parent directory path, if navigating up is allowed. |
| `IsRoot` | `bool` | Whether this is a root directory (cannot navigate up). |
| `Error` | `string` | Error message if the operation failed. |
| `IsAccessDenied` | `bool` | Whether access was denied due to security restrictions. |
| `Success` | `bool` | Whether the operation was successful. |

**With `-Roots`**: a `string`.
<!-- END GENERATED: Get-JIMFileSystemItem -->

### Examples

```powershell title="List the allowed root directories"
Get-JIMFileSystemItem
```

```powershell title="List the contents of a directory"
Get-JIMFileSystemItem -Path "/connector-files/imports"
```

```powershell title="Get the allowed root paths explicitly"
Get-JIMFileSystemItem -Roots
```

---

## :material-file-search-outline: Test-JIMFileSystemPath

Checks whether a file system path is within the JIM Container's allowed mount points.

<!-- BEGIN GENERATED: Test-JIMFileSystemPath -->
### Syntax

```powershell
Test-JIMFileSystemPath [-Path] <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Path` | `string` | Yes |  | The path to validate. Can be given by position, as the first argument. Accepts pipeline input by property name. |

### Output

`$true` if the path is within an allowed root directory; otherwise `$false`.

Returns a `bool`.
<!-- END GENERATED: Test-JIMFileSystemPath -->

### Examples

```powershell title="Validate a path before configuring a connector"
Test-JIMFileSystemPath -Path "/connector-files/imports/users.csv"
```

---

## :material-book-open-variant-outline: See also

- [Connected Systems](connected-systems.md): cmdlets for configuring file-based connectors that read paths validated here
