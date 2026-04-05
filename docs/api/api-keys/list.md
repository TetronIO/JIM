---
title: List API Keys
---

# List API Keys

Returns all API keys. The full key value is never included in list responses; only the prefix is shown.

```
GET /api/v1/apikeys
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/apikeys \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMApiKey
    ```

### Response

Returns `200 OK` with an array of API key objects.
