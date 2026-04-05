---
title: Revert a Service Setting
---

# Revert a Service Setting

Reverts a service setting to its default value by clearing the override. The setting itself is not deleted; only the custom value is removed.

```
DELETE /api/v1/service-settings/{key}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/service-settings/ChangeTracking.CsoChanges.Enabled \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Revert a single setting
    Reset-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled"

    # Revert all overridden settings
    Get-JIMServiceSetting | Where-Object { $_.isOverridden } | ForEach-Object {
        Reset-JIMServiceSetting -Key $_.key
    }
    ```

### Response

Returns `200 OK` with the reverted service setting object, now showing `effectiveValue` equal to `defaultValue`.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Setting is read-only and cannot be modified |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Setting key does not exist |
