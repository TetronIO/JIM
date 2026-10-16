---
title: Logs
description: "The cmdlets that read and follow JIM's service logs, filtered by service, date, level and text, without access to the host."
type: reference
audience: automation-author
---

# Logs

Log cmdlets provide read-only access to JIM's structured service logs (Web, Worker, Scheduler and Database), with filtering by service, date, level, and search text. Useful for remote troubleshooting without needing container/host access. The portal's [Logs](../administration/logs.md) page shows the same entries.

---

## Get-JIMLogEntry

Gets log entries, with optional filtering. Also exposes the available log level and service names, so you can discover valid `-Level`/`-Service` values.

<!-- BEGIN GENERATED: Get-JIMLogEntry -->
### Syntax

```powershell
# Entries (default)
Get-JIMLogEntry [-Service <string>] [-Date <datetime>] [-Level <string[]>] [-Search <string>] [-Limit <int>] [-Offset <int>]

# Levels
Get-JIMLogEntry -ListLevels

# Services
Get-JIMLogEntry -ListServices
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Service` | `string` | No (Entries) |  | The service whose entries to return: web, worker, scheduler or database. Omit it for every service. |
| `Date` | `datetime` | No (Entries) |  | The day whose entries to return. Omit it for today (UTC). |
| `Level` | `string[]` | No (Entries) |  | The log levels to include: Verbose, Debug, Information, Warning, Error or Fatal. Omit it for every level. |
| `Search` | `string` | No (Entries) |  | Text the message or its exception must contain, ignoring case. |
| `Limit` | `int` | No (Entries) | `500` | The most entries to return. From 1 to 5,000. |
| `Offset` | `int` | No (Entries) | `0` | The number of entries to skip, for paging. At least 0. |
| `ListLevels` | `switch` | Yes (Levels) |  | Returns the log level names instead of log entries. |
| `ListServices` | `switch` | Yes (Services) |  | Returns the service names instead of log entries. |

### Output

Each log entry; `-ListLevels` and `-ListServices` return each name as a string.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Timestamp` | `datetime` | The timestamp when the log entry was created (UTC). |
| `Level` | `string` | The log level (Verbose, Debug, Information, Warning, Error, Fatal). |
| `LevelShort` | `string` | The short log level abbreviation (VRB, DBG, INF, WRN, ERR, FTL). |
| `Message` | `string` | The rendered log message. |
| `Exception` | `string` | The exception details, if any. |
| `Service` | `string` | The service that generated the log entry (web, worker, scheduler, database). |
| `Properties` | `object` | Additional structured properties from the log entry. |

**With `-ListLevels` or `-ListServices`**: a `string`.
<!-- END GENERATED: Get-JIMLogEntry -->

### Examples

```powershell title="Get today's log entries across all services"
Get-JIMLogEntry
```

```powershell title="Search Warning and Error entries from the worker service"
Get-JIMLogEntry -Service worker -Level Warning, Error -Search "timeout"
```

```powershell title="List the available log levels"
Get-JIMLogEntry -ListLevels
```

---

## Get-JIMLogFile

Lists the log files JIM currently has on disk, across all services.

<!-- BEGIN GENERATED: Get-JIMLogFile -->
### Syntax

```powershell
Get-JIMLogFile
```

### Parameters

None.

### Output

Each log file's service, date and size.

| Property | Type | Description |
|----------|------|-------------|
| `FileName` | `string` | The file name without path. |
| `Service` | `string` | The service that generated the log file (web, worker, scheduler, database). |
| `Date` | `datetime` | The date of the log file. |
| `SizeBytes` | `long` | The file size in bytes. |
| `SizeFormatted` | `string` | Human-readable file size. |
<!-- END GENERATED: Get-JIMLogFile -->

### Examples

```powershell title="List all available log files"
Get-JIMLogFile
```

```powershell title="List log files for the worker service only"
Get-JIMLogFile | Where-Object Service -eq 'worker'
```

---

## Watch-JIMLog

Streams log entries to the console as they arrive, colour-coded by level (red for `Error`/`Fatal`, yellow for `Warning`, dark grey for `Debug`/`Verbose`). Polls the Logs API on an interval, displaying only entries newer than the last one seen, and runs until interrupted with ++ctrl+c++. Transient API failures are reported as warnings and polling continues.

Output is formatted for the console; for structured objects suitable for the pipeline, use `Get-JIMLogEntry` instead.

<!-- BEGIN GENERATED: Watch-JIMLog -->
### Syntax

```powershell
Watch-JIMLog [-Service <string>] [-Level <string[]>] [-Search <string>] [-IntervalSeconds <int>] [-MaxPolls <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Service` | `string` | No |  | The service whose entries to show: web, worker, scheduler or database. Omit it for every service. |
| `Level` | `string[]` | No |  | The log levels to include: Verbose, Debug, Information, Warning, Error or Fatal. Omit it for every level. |
| `Search` | `string` | No |  | Text the message or its exception must contain, ignoring case. |
| `IntervalSeconds` | `int` | No | `2` | The number of seconds to wait between polls. From 1 to 300. |
| `MaxPolls` | `int` | No |  | The number of polls to run before stopping. Omit it to run until interrupted with Ctrl+C; useful for bounded watches in scripts. At least 1. |

### Output

None. Each new entry is written to the console as [timestamp] [LEVEL] [service] message, coloured by level.
<!-- END GENERATED: Watch-JIMLog -->

### Examples

```powershell title="Stream log entries from all services"
Watch-JIMLog
```

```powershell title="Stream only worker errors"
Watch-JIMLog -Service worker -Level Error, Fatal
```

```powershell title="Stream with a custom poll interval"
Watch-JIMLog -IntervalSeconds 5
```

---

## See also

- [Activities](activities.md): the audit trail for synchronisation operations, complementary to raw service logs
