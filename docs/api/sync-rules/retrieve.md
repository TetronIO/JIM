---
title: Retrieve a Sync Rule
---

# Retrieve a Sync Rule

Returns the details of a sync rule.

```
GET /api/v1/synchronisation/sync-rules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer | ID of the sync rule |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMSyncRule -Id 1
    ```

## Response

Returns `200 OK` with the [Sync Rule object](index.md#the-sync-rule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Sync rule does not exist |
