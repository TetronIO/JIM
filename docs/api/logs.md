---
title: Logs
---

# Logs

The Logs API provides access to application log files from JIM's three services: web, worker, and scheduler. Use it to retrieve, filter, and search log entries for troubleshooting and monitoring.

---

## List Log Files

Returns all available log files with their service, date, and size. Files are sorted by date descending.

```
GET /api/v1/logs/files
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/logs/files \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Invoke-JIMApi -Endpoint "logs/files"
    ```

### Response

```json
[
  {
    "fileName": "jim-web-20260405.log",
    "service": "web",
    "date": "2026-04-05T00:00:00Z",
    "sizeBytes": 245760,
    "sizeFormatted": "240 KB"
  }
]
```

| Field | Type | Description |
|-------|------|-------------|
| `fileName` | string | Log file name |
| `service` | string | Service name: `web`, `worker`, or `scheduler` |
| `date` | datetime | Log file date |
| `sizeBytes` | integer | File size in bytes |
| `sizeFormatted` | string | Human-readable file size |

---

## Get Log Entries

Returns log entries with optional filtering by service, date, level, and text search. Results are sorted by timestamp descending (newest first).

```
GET /api/v1/logs
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `service` | string | No | | Filter by service: `web`, `worker`, or `scheduler` |
| `date` | datetime | No | Today (UTC) | Date to retrieve logs for |
| `levels` | string[] | No | All levels | Log levels to include (see below) |
| `search` | string | No | | Case-insensitive text search in log messages |
| `limit` | integer | No | `500` | Maximum entries to return (1-5000) |
| `offset` | integer | No | `0` | Number of entries to skip |

### Log Levels

| Level | Abbreviation | Description |
|-------|-------------|-------------|
| `Verbose` | `VRB` | Highly detailed diagnostic information |
| `Debug` | `DBG` | Internal debugging information |
| `Information` | `INF` | General operational events |
| `Warning` | `WRN` | Unexpected but non-critical events |
| `Error` | `ERR` | Errors that affect specific operations |
| `Fatal` | `FTL` | Critical failures that may stop the service |

### Examples

=== "curl"

    ```bash
    # Get today's log entries
    curl https://jim.example.com/api/v1/logs \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by service and level
    curl "https://jim.example.com/api/v1/logs?service=worker&levels=Error&levels=Warning" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Search for a specific term
    curl "https://jim.example.com/api/v1/logs?search=timeout&limit=100" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Get logs for a specific date
    curl "https://jim.example.com/api/v1/logs?date=2026-04-01&service=web" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Get today's log entries
    Invoke-JIMApi -Endpoint "logs"

    # Filter by service and level
    Invoke-JIMApi -Endpoint "logs?service=worker&levels=Error&levels=Warning"

    # Search for a specific term
    Invoke-JIMApi -Endpoint "logs?search=timeout&limit=100"

    # Get logs for a specific date
    Invoke-JIMApi -Endpoint "logs?date=2026-04-01&service=web"
    ```

### Response

```json
[
  {
    "timestamp": "2026-04-05T10:30:15.123Z",
    "level": "Error",
    "levelShort": "ERR",
    "message": "Connection to LDAP server timed out after 30 seconds",
    "exception": "System.TimeoutException: The operation has timed out...",
    "service": "worker",
    "properties": {
      "ConnectedSystemId": 3,
      "ConnectedSystemName": "Corporate AD"
    }
  }
]
```

| Field | Type | Description |
|-------|------|-------------|
| `timestamp` | datetime | UTC timestamp |
| `level` | string | Full log level name |
| `levelShort` | string | Abbreviated level (VRB, DBG, INF, WRN, ERR, FTL) |
| `message` | string | Rendered log message |
| `exception` | string, nullable | Exception details if present |
| `service` | string | Service that generated the entry |
| `properties` | object, nullable | Structured log properties |

---

## Get Log Levels

Returns the list of available log levels for filtering.

```
GET /api/v1/logs/levels
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/logs/levels \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Invoke-JIMApi -Endpoint "logs/levels"
    ```

### Response

```json
["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]
```

---

## Get Log Services

Returns the list of available service names for filtering.

```
GET /api/v1/logs/services
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/logs/services \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Invoke-JIMApi -Endpoint "logs/services"
    ```

### Response

```json
["web", "worker", "scheduler"]
```

---

## Authorisation

All endpoints require the **Administrator** role.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
