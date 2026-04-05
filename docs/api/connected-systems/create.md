---
title: Create a Connected System
---

# Create a Connected System

Creates a new connected system with the specified connector type.

After creation, you will typically update the system to configure connector-specific settings, then import the schema to discover object types and attributes.

```
POST /api/v1/synchronisation/connected-systems
```

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Display name for the connected system (1-200 characters) |
| `description` | string | No | Optional description (max 1000 characters) |
| `connectorDefinitionId` | integer | Yes | ID of the connector type to use. Available connectors can be listed via the Swagger UI. |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Corporate LDAP",
        "description": "Primary directory for employee accounts",
        "connectorDefinitionId": 3
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMConnectedSystem -Name "Corporate LDAP" `
        -Description "Primary directory for employee accounts" `
        -ConnectorDefinitionId 3
    ```

## Response

Returns the created connected system with `201 Created`.

```json
{
  "id": 7,
  "name": "Corporate LDAP",
  "description": "Primary directory for employee accounts",
  "created": "2026-04-05T10:30:00Z",
  "lastUpdated": null,
  "status": "Active",
  "settingValuesValid": false,
  "connector": {
    "id": 3,
    "name": "JIM LDAP Connector"
  },
  "objectTypes": [],
  "objectCount": 0,
  "pendingExportCount": 0,
  "maxExportParallelism": null
}
```

!!! note
    The `settingValuesValid` field is `false` after creation because connector-specific settings have not yet been configured. [Update the connected system](update.md) with the required settings, then the connector will validate them.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Missing or invalid fields (e.g. name too long, missing connector ID) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connector definition ID does not exist |
