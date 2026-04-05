---
title: Import Hierarchy
---

# Import Hierarchy

Connects to the external identity store and discovers its partition and container hierarchy. This is used by connectors that support organisational structures, such as LDAP OUs or Active Directory domains.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/import-hierarchy
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/import-hierarchy \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Import-JIMConnectedSystemHierarchy -ConnectedSystemId 1
    ```

## Response

Returns `200 OK` with a summary of hierarchy changes detected.

```json
{
  "success": true,
  "errorMessage": null,
  "totalPartitions": 2,
  "totalContainers": 15,
  "addedPartitions": [],
  "removedPartitions": [],
  "renamedPartitions": [],
  "addedContainers": [
    {
      "externalId": "OU=NewDept,DC=example,DC=com",
      "name": "NewDept",
      "wasSelected": false,
      "itemType": "Container"
    }
  ],
  "removedContainers": [],
  "renamedContainers": [],
  "movedContainers": [],
  "hasChanges": true,
  "hasSelectedItemsRemoved": false
}
```

### Response Attributes

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | Whether the hierarchy import completed successfully |
| `errorMessage` | string, nullable | Error details if the import failed |
| `totalPartitions` | integer | Total number of partitions after import |
| `totalContainers` | integer | Total number of containers after import |
| `addedPartitions` | array | Newly discovered partitions |
| `removedPartitions` | array | Partitions no longer present in the external store |
| `renamedPartitions` | array | Partitions with changed names |
| `addedContainers` | array | Newly discovered containers |
| `removedContainers` | array | Containers no longer present |
| `renamedContainers` | array | Containers with changed names |
| `movedContainers` | array | Containers that moved to a different parent |
| `hasChanges` | boolean | Whether any changes were detected |
| `hasSelectedItemsRemoved` | boolean | Whether any previously selected partitions or containers were removed |

!!! warning
    If `hasSelectedItemsRemoved` is `true`, previously selected partitions or containers are no longer available in the external store. Review your synchronisation configuration to ensure import scoping is still correct.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Connector settings are invalid or connector does not support hierarchy |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
| `500` | `INTERNAL_ERROR` | Connection to the external store failed |
