---
title: Create a Run Profile
---

# Create a Run Profile

Creates a new run profile for a connected system. The available run types depend on the connector's capabilities.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/run-profiles
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Display name (1-200 characters) |
| `runType` | string | Yes | `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation`, or `Export` |
| `pageSize` | integer | No | Objects per batch (1-10000, default: 100) |
| `partitionId` | integer | No | Target partition ID (for connectors that support partitions) |
| `filePath` | string | No | File path for file-based connectors (max 500 characters) |

## Examples

=== "curl"

    ```bash
    # Create a delta import profile
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Delta Import",
        "runType": "DeltaImport"
      }'

    # Create a full import with a larger batch size
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Full Import",
        "runType": "FullImport",
        "pageSize": 500
      }'

    # Create an import targeting a specific partition
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Import (Users Partition)",
        "runType": "DeltaImport",
        "partitionId": 1
      }'

    # Create a file-based import
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/2/run-profiles \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "CSV Import",
        "runType": "FullImport",
        "filePath": "/data/imports/employees.csv"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create a delta import profile
    New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport

    # Create a full import with a larger batch size
    New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" `
        -RunType FullImport -PageSize 500

    # Create an import targeting a specific partition
    New-JIMRunProfile -ConnectedSystemId 1 -Name "Import (Users Partition)" `
        -RunType DeltaImport -PartitionId 1

    # Create a file-based import by connected system name
    New-JIMRunProfile -ConnectedSystemName "HR Database" `
        -Name "CSV Import" -RunType FullImport `
        -FilePath "/data/imports/employees.csv"

    # Create a standard set of profiles for a connected system
    $csId = 1
    New-JIMRunProfile -ConnectedSystemId $csId -Name "Full Import" -RunType FullImport
    New-JIMRunProfile -ConnectedSystemId $csId -Name "Delta Import" -RunType DeltaImport
    New-JIMRunProfile -ConnectedSystemId $csId -Name "Full Sync" -RunType FullSynchronisation
    New-JIMRunProfile -ConnectedSystemId $csId -Name "Delta Sync" -RunType DeltaSynchronisation
    New-JIMRunProfile -ConnectedSystemId $csId -Name "Export" -RunType Export
    ```

## Response

Returns `201 Created` with the [Run Profile object](index.md#the-run-profile-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields or run type not supported by the connector |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
