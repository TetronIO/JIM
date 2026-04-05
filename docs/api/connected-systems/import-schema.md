---
title: Import Schema
---

# Import Schema

Connects to the external identity store and discovers its schema, including object types and their attributes. This is typically the first operation after configuring connector settings.

The imported schema defines what object types (e.g. `user`, `group`) and attributes (e.g. `displayName`, `mail`) are available for synchronisation.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/import-schema
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/import-schema \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Import-JIMConnectedSystemSchema -ConnectedSystemId 1
    ```

## Response

Returns `200 OK` with the updated [Connected System object](index.md#the-connected-system-object), now populated with discovered object types and attributes.

The `settingValuesValid` field will reflect whether the connector successfully validated its settings during the connection.

!!! note
    If the schema has changed since the last import (e.g. new attributes added to the directory), importing again will merge the changes. Existing attribute selections are preserved.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Connector settings are invalid or incomplete |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
| `500` | `INTERNAL_ERROR` | Connection to the external store failed |
