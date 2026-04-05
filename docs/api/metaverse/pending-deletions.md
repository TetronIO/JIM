---
title: Pending Deletions
---

# Pending Deletions

Pending deletions track metaverse objects that are awaiting deletion. Objects enter this state when all their connector space objects are disconnected and their object type has an automatic deletion rule configured. A configurable grace period can delay deletion, giving administrators time to review before objects are permanently removed.

### The Pending Deletion Object

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "displayName": "Jane Smith",
  "typeName": "person",
  "typeId": 1,
  "lastConnectorDisconnectedDate": "2026-03-28T14:00:00Z",
  "deletionEligibleDate": "2026-04-04T14:00:00Z",
  "daysUntilDeletion": -1,
  "gracePeriod": "7.00:00:00",
  "connectedSystemObjectCount": 0,
  "status": "ReadyForDeletion"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Metaverse object ID |
| `displayName` | string, nullable | Object display name |
| `typeName` | string | Object type name |
| `typeId` | integer | Object type ID |
| `lastConnectorDisconnectedDate` | datetime | When the last connector was disconnected |
| `deletionEligibleDate` | datetime, nullable | When the grace period expires |
| `daysUntilDeletion` | integer, nullable | Days remaining (negative if overdue) |
| `gracePeriod` | timespan, nullable | Configured grace period for the object type |
| `connectedSystemObjectCount` | integer | Remaining connected system objects (0 if fully disconnected) |
| `status` | string | `Deprovisioning`, `AwaitingGracePeriod`, or `ReadyForDeletion` |

### Deletion Statuses

| Status | Description |
|--------|-------------|
| `Deprovisioning` | Object still has connected system objects being deprovisioned |
| `AwaitingGracePeriod` | Fully disconnected, waiting for grace period to expire |
| `ReadyForDeletion` | Grace period expired, eligible for deletion on next cleanup run |

---

## List Pending Deletions

Returns a paginated list of metaverse objects pending deletion.

```
GET /api/v1/metaverse/pending-deletions
```

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `page` | integer | No | `1` | Page number |
| `pageSize` | integer | No | `25` | Items per page (max 100) |
| `objectTypeId` | integer | No | | Filter by object type |

### Examples

=== "curl"

    ```bash
    # List all pending deletions
    curl https://jim.example.com/api/v1/metaverse/pending-deletions \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Filter by object type
    curl "https://jim.example.com/api/v1/metaverse/pending-deletions?objectTypeId=1" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all pending deletions
    Get-JIMPendingDeletion

    # Filter by object type
    Get-JIMPendingDeletion -ObjectTypeId 1
    ```

### Response

Returns `200 OK` with a paginated list of pending deletion objects.

---

## Count Pending Deletions

Returns just the count of objects pending deletion.

```
GET /api/v1/metaverse/pending-deletions/count
```

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `objectTypeId` | integer | No | Filter by object type |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/pending-deletions/count \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPendingDeletion -Count
    ```

### Response

Returns `200 OK` with an integer count.

```json
42
```

---

## Pending Deletions Summary

Returns a summary of pending deletions broken down by status.

```
GET /api/v1/metaverse/pending-deletions/summary
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/metaverse/pending-deletions/summary \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPendingDeletion -Summary
    ```

### Response

Returns `200 OK` with a summary breakdown.

```json
{
  "totalCount": 42,
  "deprovisioningCount": 5,
  "awaitingGracePeriodCount": 25,
  "readyForDeletionCount": 12
}
```

| Field | Type | Description |
|-------|------|-------------|
| `totalCount` | integer | Total objects pending deletion |
| `deprovisioningCount` | integer | Still have connected system objects being deprovisioned |
| `awaitingGracePeriodCount` | integer | Fully disconnected, waiting for grace period |
| `readyForDeletionCount` | integer | Grace period expired, eligible for deletion |
