---
title: List Sync Rules
---

# List Sync Rules

Returns a paginated list of sync rules.

```
GET /api/v1/synchronisation/sync-rules
```

## Query Parameters

| Parameter       | Type    | Required | Default | Description |
|-----------------|---------|----------|---------|-------------|
| `page`          | integer | No       | `1`     | Page number (1-based) |
| `pageSize`      | integer | No       | `25`    | Items per page (max 100) |
| `sortBy`        | string  | No       |         | Sort field |
| `sortDirection` | string  | No       | `asc`   | Sort order: `asc` or `desc` |
| `filter`        | string  | No       |         | Filter by name |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # List all sync rules
    Get-JIMSyncRule

    # Filter by connected system
    Get-JIMSyncRule -ConnectedSystemName "Corporate LDAP"
    ```

## Response

Returns `200 OK` with a paginated list of [Sync Rule objects](index.md#the-sync-rule-object).

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
