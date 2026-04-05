---
title: List Run Profiles
---

# List Run Profiles

Returns all run profiles for a connected system.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/run-profiles
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # By connected system ID
    Get-JIMRunProfile -ConnectedSystemId 1

    # By connected system name
    Get-JIMRunProfile -ConnectedSystemName "Corporate LDAP"

    # Via pipeline
    Get-JIMConnectedSystem -Name "Corporate*" | Get-JIMRunProfile
    ```

## Response

Returns `200 OK` with an array of [Run Profile objects](index.md#the-run-profile-object).

```json
[
  {
    "id": 1,
    "name": "Full Import",
    "connectedSystemId": 1,
    "runType": "FullImport",
    "pageSize": 100,
    "partitionName": null,
    "filePath": null
  },
  {
    "id": 2,
    "name": "Delta Import",
    "connectedSystemId": 1,
    "runType": "DeltaImport",
    "pageSize": 100,
    "partitionName": null,
    "filePath": null
  },
  {
    "id": 3,
    "name": "Delta Sync",
    "connectedSystemId": 1,
    "runType": "DeltaSynchronisation",
    "pageSize": 100,
    "partitionName": null,
    "filePath": null
  },
  {
    "id": 4,
    "name": "Export",
    "connectedSystemId": 1,
    "runType": "Export",
    "pageSize": 100,
    "partitionName": null,
    "filePath": null
  }
]
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
