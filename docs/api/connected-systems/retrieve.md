---
title: Retrieve a Connected System
---

# Retrieve a Connected System

Returns the full details of a connected system, including its connector reference, discovered object types, and summary counts.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystem -Id 1
    ```

## Response

Returns `200 OK` with the full [Connected System object](index.md#the-connected-system-object).

```json
{
  "id": 1,
  "name": "Corporate LDAP",
  "description": "Primary directory for employee accounts",
  "created": "2026-01-15T09:30:00Z",
  "lastUpdated": "2026-03-20T14:12:00Z",
  "status": "Active",
  "settingValuesValid": true,
  "connector": {
    "id": 3,
    "name": "JIM LDAP Connector"
  },
  "objectTypes": [
    {
      "id": 10,
      "name": "user",
      "created": "2026-01-15T09:31:00Z",
      "selected": true,
      "removeContributedAttributesOnObsoletion": false,
      "attributeCount": 47
    },
    {
      "id": 11,
      "name": "group",
      "created": "2026-01-15T09:31:00Z",
      "selected": true,
      "removeContributedAttributesOnObsoletion": false,
      "attributeCount": 12
    }
  ],
  "objectCount": 12450,
  "pendingExportCount": 0,
  "maxExportParallelism": 4
}
```

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
