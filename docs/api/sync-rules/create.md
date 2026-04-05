---
title: Create a Sync Rule
---

# Create a Sync Rule

Creates a new sync rule linking a connected system object type to a metaverse object type.

```
POST /api/v1/synchronisation/sync-rules
```

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Sync rule name (1-200 characters) |
| `connectedSystemId` | integer | Yes | Connected system ID |
| `connectedSystemObjectTypeId` | integer | Yes | Connected system object type ID |
| `metaverseObjectTypeId` | integer | Yes | Metaverse object type ID |
| `direction` | string | Yes | `Import` or `Export` |
| `projectToMetaverse` | boolean | No | Create new metaverse objects when no match found (import rules only) |
| `provisionToConnectedSystem` | boolean | No | Create new objects in the connected system (export rules only) |
| `enabled` | boolean | No | Enable on creation (default: `true`) |
| `enforceState` | boolean | No | Detect and remediate attribute drift (default: `true`, export rules only) |

## Examples

=== "curl"

    ```bash
    # Create an import sync rule with projection
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Import Users from LDAP",
        "connectedSystemId": 1,
        "connectedSystemObjectTypeId": 10,
        "metaverseObjectTypeId": 1,
        "direction": "Import",
        "projectToMetaverse": true
      }'

    # Create an export sync rule with provisioning and scoping
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Export Users to LDAP",
        "connectedSystemId": 1,
        "connectedSystemObjectTypeId": 10,
        "metaverseObjectTypeId": 1,
        "direction": "Export",
        "provisionToConnectedSystem": true,
        "enforceState": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Create an import sync rule with projection
    New-JIMSyncRule -Name "Import Users from LDAP" `
        -ConnectedSystemId 1 `
        -ConnectedSystemObjectTypeId 10 `
        -MetaverseObjectTypeId 1 `
        -Direction Import `
        -ProjectToMetaverse

    # Create an export sync rule with provisioning
    New-JIMSyncRule -Name "Export Users to LDAP" `
        -ConnectedSystemName "Corporate LDAP" `
        -ConnectedSystemObjectTypeId 10 `
        -MetaverseObjectTypeId 1 `
        -Direction Export `
        -ProvisionToConnectedSystem
    ```

## Response

Returns `201 Created` with the [Sync Rule object](index.md#the-sync-rule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields (e.g. missing required IDs, invalid direction) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
