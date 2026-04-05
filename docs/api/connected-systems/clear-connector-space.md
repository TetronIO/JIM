---
title: Clear Connector Space
---

# Clear Connector Space

Removes all objects from the connected system's connector space without deleting the connected system itself. This is useful when you need to re-import all objects from scratch, for example after correcting a misconfigured import scope.

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/clear
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

## Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `deleteChangeHistory` | boolean | `true` | Also delete change history records for the cleared objects |

!!! warning
    This operation removes all connector space objects, their attribute values, and (by default) their change history. Joined metaverse objects are unaffected, but their connector space links to this system will be severed.

## Examples

=== "curl"

    ```bash
    # Clear connector space and change history (default)
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/clear \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"

    # Clear connector space but preserve change history
    curl -X POST "https://jim.example.com/api/v1/synchronisation/connected-systems/1/clear?deleteChangeHistory=false" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Clear-JIMConnectedSystem -Id 1
    ```

## Response

Returns `200 OK` with no body on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |
