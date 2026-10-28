---
title: File System
description: "The cmdlets that browse and test the paths JIM can read and write, for choosing a file-based Connector's import and export paths."
type: reference
audience: automation-author
---

# File System

File System cmdlets let you browse the JIM Container's file system, used when configuring file-based connectors (e.g. CSV) to select import/export paths. Only paths within the configured allowed mount points are accessible.

```jim-diagram
archetype: anatomy
title: A connector file path, and the cmdlets that check each part
desc: "A path JIM can use, such as /connector-files/hr/users.csv, starts with an allowed root, one of the mount points Get-JIMFileSystemItem -Roots lists; what follows is found by listing directories with Get-JIMFileSystemItem -Path. Test-JIMFileSystemPath returns $true for a path under an allowed root, and $false for a path anywhere else, which JIM cannot read."
subject:
  kind: value
  parts:
    - { id: root, text: /connector-files, strong: true, callout: "An allowed root, as `Get-JIMFileSystemItem -Roots` lists it" }
    - { text: /hr/users.csv }
derived: { from: root, label: "is the path under it?", box: "`Test-JIMFileSystemPath` returns `$true`", note: "`$false` anywhere else; list inside a root with `Get-JIMFileSystemItem -Path`" }
```

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
| `Path` | `string` | No (List) |  | The directory to list. Omit it to list the first allowed root directory; use `-Roots` to list the roots themselves. |
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

```powershell title="List the first allowed root directory"
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
