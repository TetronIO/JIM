---
title: List Schedules
---

# List Schedules

Returns a paginated list of schedules. Results include summary information; use [Retrieve a Schedule](retrieve.md) for full details including steps.

```
GET /api/v1/schedules
```

## Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `20`    | Items per page |
| `search`        | string  | No       |         | Filter by name or description |
| `sortBy`        | string  | No       |         | Sort field: `name`, `created`, `nextRunTime` |
| `sortDescending`| boolean | No       | `false` | Sort in descending order |

## Examples

=== "curl"

    ```bash
    # List all schedules
    curl https://jim.example.com/api/v1/schedules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Search by name
    curl "https://jim.example.com/api/v1/schedules?search=delta&sortBy=name" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all schedules
    Get-JIMSchedule

    # Filter by name (supports wildcards)
    Get-JIMSchedule -Name "Delta*"
    ```

## Response

Returns `200 OK` with a paginated list.

```json
{
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "name": "Daily Delta Sync",
      "description": "Import, sync, and export changes from all connected systems",
      "triggerType": "Cron",
      "cronExpression": "0 6 * * 1-5",
      "patternType": "SpecificTimes",
      "daysOfWeek": "1,2,3,4,5",
      "runTimes": "06:00",
      "intervalValue": null,
      "intervalUnit": null,
      "intervalWindowStart": null,
      "intervalWindowEnd": null,
      "isEnabled": true,
      "lastRunTime": "2026-04-04T06:00:00Z",
      "nextRunTime": "2026-04-07T06:00:00Z",
      "stepCount": 4,
      "created": "2026-01-15T09:30:00Z",
      "lastUpdated": "2026-03-20T14:12:00Z"
    }
  ],
  "totalCount": 3,
  "page": 1,
  "pageSize": 20,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
