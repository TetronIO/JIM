---
title: Logs
---

# Logs

Log cmdlets provide read-only access to JIM's structured service logs (Web, Worker, Scheduler), with filtering by service, date, level, and search text. Useful for remote troubleshooting without needing container/host access.

---

## Get-JIMLogEntry

Gets log entries, with optional filtering. Also exposes the available log level and service names, so you can discover valid `-Level`/`-Service` values.

### Syntax

```powershell
# Entries (default)
Get-JIMLogEntry [-Service <string>] [-Date <datetime>] [-Level <string[]>] [-Search <string>]
    [-Limit <int>] [-Offset <int>]

# Levels
Get-JIMLogEntry -ListLevels

# Services
Get-JIMLogEntry -ListServices
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Service` | `string` | No | | Filter by service name (`web`, `worker`, `scheduler`). Omit for all services. |
| `Date` | `datetime` | No | today (UTC) | The date to retrieve logs for. |
| `Level` | `string[]` | No | | Specific log levels to include (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). Omit for all levels. |
| `Search` | `string` | No | | Text to search for in the log message (case-insensitive). |
| `Limit` | `int` | No | `500` | Maximum number of entries to return (maximum 5000). |
| `Offset` | `int` | No | `0` | Number of entries to skip, for paging. |
| `ListLevels` | `switch` | No | `$false` | Returns the available log level names instead of log entries. |
| `ListServices` | `switch` | No | `$false` | Returns the available service names instead of log entries. |

### Output

Log entries (timestamp, level, message, service, and any structured properties), or a list of level/service names.

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

### Syntax

```powershell
Get-JIMLogFile
```

### Output

Log file metadata: service, date, and size.

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

### Syntax

```powershell
Watch-JIMLog [-Service <string>] [-Level <string[]>] [-Search <string>]
    [-IntervalSeconds <int>] [-MaxPolls <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Service` | `string` | No | | Filter by service name (`web`, `worker`, `scheduler`). Omit for all services. |
| `Level` | `string[]` | No | | Specific log levels to include (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). Omit for all levels. |
| `Search` | `string` | No | | Text to search for in the log message (case-insensitive). |
| `IntervalSeconds` | `int` | No | `2` | Number of seconds to wait between polls (1-300). |
| `MaxPolls` | `int` | No | | Maximum number of poll cycles to run before stopping. Omit to run until interrupted. |

### Output

None. Each new entry is written to the console as `[timestamp] [LEVEL] [service] message`, colour-coded by level.

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
