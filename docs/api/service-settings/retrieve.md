---
title: Retrieve a Service Setting
---

# Retrieve a Service Setting

```
GET /api/v1/service-settings/{key}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `key` | string | The setting key using dot notation |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/service-settings/ChangeTracking.CsoChanges.Enabled \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled"
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Setting key does not exist |
