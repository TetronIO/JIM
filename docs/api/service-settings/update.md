---
title: Update a Service Setting
---

# Update a Service Setting

Updates a service setting by overriding its value. The value is always provided as a string, regardless of the setting's data type.

```
PUT /api/v1/service-settings/{key}
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `value` | string, nullable | Yes | The new value as a string, or null to clear the override |

!!! note
    Read-only settings (those mirrored from environment variables) cannot be updated through this endpoint. The API returns a `400 VALIDATION_ERROR` if you attempt to update a read-only setting.

### Examples

=== "curl"

    ```bash
    # Update a Boolean setting
    curl -X PUT https://jim.example.com/api/v1/service-settings/ChangeTracking.CsoChanges.Enabled \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "value": "false"
      }'

    # Update an Integer setting
    curl -X PUT https://jim.example.com/api/v1/service-settings/History.RetentionDays \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "value": "1000"
      }'

    # Update a TimeSpan setting
    curl -X PUT https://jim.example.com/api/v1/service-settings/History.CleanupInterval \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "value": "30.00:00:00"
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Update a Boolean setting
    Set-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled" -Value "false"

    # Update an Integer setting
    Set-JIMServiceSetting -Key "History.RetentionDays" -Value "1000"

    # Update a TimeSpan setting
    Set-JIMServiceSetting -Key "History.CleanupInterval" -Value "30.00:00:00"
    ```

### Response

Returns `200 OK` with the updated service setting object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `VALIDATION_ERROR` | Setting is read-only, or the value is invalid for the setting's data type |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Setting key does not exist |
