---
title: Delete an API Key
---

# Delete an API Key

Permanently deletes an API key. Any requests using this key will fail immediately after deletion.

```
DELETE /api/v1/apikeys/{id}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/apikeys/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Delete by ID
    Remove-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Force

    # Delete via pipeline
    Get-JIMApiKey | Where-Object { $_.name -like "Test*" } | Remove-JIMApiKey -Force
    ```

### Response

Returns `204 No Content` on success.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | API key does not exist |
