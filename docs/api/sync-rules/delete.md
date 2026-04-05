---
title: Delete a Sync Rule
---

# Delete a Sync Rule

Permanently deletes a sync rule and all its associated mappings, scoping criteria, and object matching rules.

```
DELETE /api/v1/synchronisation/sync-rules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer | ID of the sync rule |

## Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/sync-rules/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMSyncRule -Id 1

    # Without confirmation
    Remove-JIMSyncRule -Id 1 -Force
    ```

## Response

Returns `204 No Content` on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Sync rule does not exist |
