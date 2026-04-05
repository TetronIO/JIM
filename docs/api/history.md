---
title: History
---

# History

The History API provides access to deletion audit trails and change record management. It tracks deleted connector space objects (CSOs) and metaverse objects (MVOs), and supports manual cleanup of expired history records based on the configured retention period.

---

## Deleted Connector Space Objects

Returns a paginated list of deleted connector space objects from the audit trail.

```
GET /api/v1/history/deleted-objects/cso
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `connectedSystemId` | integer | No | | Filter by connected system |
| `externalIdSearch` | string | No | | Search by external ID (contains match) |
| `fromDate` | datetime | No | | Deletions on or after this date (UTC) |
| `toDate` | datetime | No | | Deletions on or before this date (UTC) |
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `50` | Items per page (max 1000) |

### Examples

=== "curl"

    ```bash
    # List all deleted CSOs
    curl https://jim.example.com/api/v1/history/deleted-objects/cso \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by connected system and date range
    curl "https://jim.example.com/api/v1/history/deleted-objects/cso?connectedSystemId=1&fromDate=2026-03-01&toDate=2026-04-01" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Search by external ID
    curl "https://jim.example.com/api/v1/history/deleted-objects/cso?externalIdSearch=jsmith" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all deleted CSOs
    Get-JIMDeletedObject -ObjectType CSO

    # Filter by connected system and date range
    Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId 1 `
        -FromDate (Get-Date "2026-03-01") -ToDate (Get-Date "2026-04-01")

    # Search by external ID
    Get-JIMDeletedObject -ObjectType CSO -Search "jsmith"
    ```

### Response

```json
{
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "externalId": "CN=John Smith,OU=Users,DC=contoso,DC=com",
      "displayName": "John Smith",
      "objectTypeName": "person",
      "connectedSystemId": 1,
      "connectedSystemName": "Corporate AD",
      "changeTime": "2026-03-28T14:30:00Z",
      "initiatedByType": "User",
      "initiatedByName": "admin@contoso.com"
    }
  ],
  "totalCount": 156,
  "page": 1,
  "pageSize": 50
}
```

---

## Deleted Metaverse Objects

Returns a paginated list of deleted metaverse objects from the audit trail.

```
GET /api/v1/history/deleted-objects/mvo
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `objectTypeId` | integer | No | | Filter by object type |
| `displayNameSearch` | string | No | | Search by display name (contains match) |
| `fromDate` | datetime | No | | Deletions on or after this date (UTC) |
| `toDate` | datetime | No | | Deletions on or before this date (UTC) |
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `50` | Items per page (max 1000) |

### Examples

=== "curl"

    ```bash
    # List all deleted MVOs
    curl https://jim.example.com/api/v1/history/deleted-objects/mvo \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by object type
    curl "https://jim.example.com/api/v1/history/deleted-objects/mvo?objectTypeId=1" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all deleted MVOs (default)
    Get-JIMDeletedObject

    # Filter by object type and date range
    Get-JIMDeletedObject -MetaverseObjectTypeId 1 `
        -FromDate (Get-Date "2026-03-01")
    ```

### Response

```json
{
  "items": [
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "displayName": "John Smith",
      "objectTypeName": "person",
      "objectTypeId": 1,
      "changeTime": "2026-03-28T15:00:00Z",
      "initiatedByType": "User",
      "initiatedByName": "admin@contoso.com"
    }
  ],
  "totalCount": 42,
  "page": 1,
  "pageSize": 50
}
```

---

## Change Record Count

Returns the count of CSO change records for a specific connected system. Useful for assessing the volume of change history before cleanup.

```
GET /api/v1/history/connected-systems/{connectedSystemId}/count
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/history/connected-systems/1/count \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMHistoryCount -ConnectedSystemId 1
    ```

### Response

```json
{
  "connectedSystemId": 1,
  "connectedSystemName": "Corporate AD",
  "changeRecordCount": 15432
}
```

---

## Cleanup History

Manually triggers cleanup of expired history records based on the configured retention period. Records older than the retention period are deleted in batches.

```
POST /api/v1/history/cleanup
```

!!! note
    JIM also runs automatic history cleanup on a schedule. This endpoint allows you to trigger it manually, for example after a large deletion operation.

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/history/cleanup \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Invoke-JIMHistoryCleanup -PassThru
    ```

### Response

```json
{
  "csoChangesDeleted": 5000,
  "mvoChangesDeleted": 1200,
  "activitiesDeleted": 300,
  "oldestRecordDeleted": "2025-12-01T00:00:00Z",
  "newestRecordDeleted": "2026-01-05T23:59:59Z",
  "cutoffDate": "2026-03-06T00:00:00Z",
  "retentionPeriodDays": 30,
  "batchSize": 5000
}
```

| Field | Type | Description |
|-------|------|-------------|
| `csoChangesDeleted` | integer | CSO change records deleted |
| `mvoChangesDeleted` | integer | MVO change records deleted |
| `activitiesDeleted` | integer | Activity records deleted |
| `oldestRecordDeleted` | datetime, nullable | Timestamp of the oldest deleted record |
| `newestRecordDeleted` | datetime, nullable | Timestamp of the newest deleted record |
| `cutoffDate` | datetime | Records older than this were eligible for deletion |
| `retentionPeriodDays` | integer | Configured retention period in days |
| `batchSize` | integer | Maximum records deleted per type in a single batch |

---

## Authorisation

All endpoints require the **Administrator** role.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
