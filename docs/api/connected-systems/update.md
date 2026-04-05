---
title: Update a Connected System
---

# Update a Connected System

Updates a connected system's name, description, connector settings, or export parallelism. All fields are optional; only include the fields you want to change.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No | Display name (1-200 characters) |
| `description` | string | No | Description (max 1000 characters) |
| `settingValues` | object | No | Connector-specific settings (see below) |
| `maxExportParallelism` | integer | No | Maximum concurrent export batches (1-16) |

### Setting Values

The `settingValues` field is a dictionary keyed by setting ID. Each value contains the fields relevant to the setting type:

```json
{
  "settingValues": {
    "1": { "stringValue": "ldap://directory.example.com:389" },
    "2": { "stringValue": "CN=ServiceAccount,OU=Service,DC=example,DC=com" },
    "3": { "stringValue": "s3cur3p@ssw0rd" },
    "4": { "intValue": 389 },
    "5": { "checkboxValue": true }
  }
}
```

Setting IDs and their types are defined by the connector. Use the [Retrieve](retrieve.md) endpoint to see current setting values after a schema import.

## Examples

=== "curl"

    ```bash
    # Update name and description
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "name": "Corporate LDAP (Production)",
        "description": "Primary directory for all employee accounts"
      }'

    # Configure connector settings
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "settingValues": {
          "1": { "stringValue": "ldap://directory.example.com:389" },
          "2": { "stringValue": "CN=JIM,OU=Service,DC=example,DC=com" },
          "3": { "stringValue": "s3cur3p@ssw0rd" }
        }
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Update name and description
    Set-JIMConnectedSystem -Id 1 `
        -Name "Corporate LDAP (Production)" `
        -Description "Primary directory for all employee accounts"

    # Configure connector settings
    $settings = @{
        1 = @{ stringValue = "ldap://directory.example.com:389" }
        2 = @{ stringValue = "CN=JIM,OU=Service,DC=example,DC=com" }
        3 = @{ stringValue = "s3cur3p@ssw0rd" }
    }
    Set-JIMConnectedSystem -Id 1 -SettingValues $settings
    ```

## Response

Returns `200 OK` with the updated [Connected System object](index.md#the-connected-system-object).

!!! note
    When you update `settingValues`, the `settingValuesValid` field resets to `false` until the connector validates the new configuration. This typically happens during the next [schema import](import-schema.md).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Invalid field values (e.g. name too long, parallelism out of range) |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
