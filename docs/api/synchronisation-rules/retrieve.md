---
title: Retrieve a Synchronisation Rule
---

# Retrieve a Synchronisation Rule

Returns the details of a synchronisation rule.

```
GET /api/v1/synchronisation/sync-rules/{id}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer | ID of the synchronisation rule |

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

Returns `200 OK` with the [Synchronisation Rule object](index.md#the-synchronisation-rule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Synchronisation rule does not exist |
