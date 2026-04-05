---
title: Update a Run Profile
---

# Update a Run Profile

Updates a run profile's name, batch size, partition, or file path. All fields are optional; only include the fields you want to change. The run type cannot be changed after creation.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/run-profiles/{runProfileId}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `runProfileId` | integer | ID of the run profile |

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No | Display name (1-200 characters) |
| `pageSize` | integer | No | Objects per batch (1-10000) |
| `partitionId` | integer | No | Target partition ID |
| `filePath` | string | No | File path for file-based connectors (max 500 characters) |

## Examples

=== "curl"

    ```bash
    # Update the batch size
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "pageSize": 500
      }'

    # Rename and update file path
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/2/run-profiles/5 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "CSV Import (Updated)",
        "filePath": "/data/imports/employees-v2.csv"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Update the batch size
    Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -PageSize 500

    # Rename by connected system name
    Set-JIMRunProfile -ConnectedSystemName "Corporate LDAP" `
        -RunProfileId 1 -Name "Full Import (Production)"

    # Update via pipeline
    Get-JIMRunProfile -ConnectedSystemId 1 |
        Where-Object { $_.name -eq "Full Import" } |
        Set-JIMRunProfile -PageSize 1000
    ```

## Response

Returns `200 OK` with the updated [Run Profile object](index.md#the-run-profile-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. name too long, page size out of range) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or run profile does not exist |
