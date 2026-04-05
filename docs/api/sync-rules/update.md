---
title: Update a Sync Rule
---

# Update a Sync Rule

Updates a sync rule's name, enabled state, or behaviour flags. The direction, connected system, and object type mappings cannot be changed after creation.

```
PUT /api/v1/synchronisation/sync-rules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer | ID of the sync rule |

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No | Sync rule name (1-200 characters) |
| `enabled` | boolean | No | Enable or disable the sync rule |
| `projectToMetaverse` | boolean | No | Update projection setting (import rules) |
| `provisionToConnectedSystem` | boolean | No | Update provisioning setting (export rules) |
| `enforceState` | boolean | No | Update drift enforcement (export rules) |

## Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/sync-rules/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Import Users from LDAP (Production)",
        "enabled": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMSyncRule -Id 1 -Name "Import Users from LDAP (Production)"

    # Enable/disable
    Set-JIMSyncRule -Id 1 -Enable
    Set-JIMSyncRule -Id 1 -Disable
    ```

## Response

Returns `200 OK` with the updated [Sync Rule object](index.md#the-sync-rule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid fields |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Sync rule does not exist |
