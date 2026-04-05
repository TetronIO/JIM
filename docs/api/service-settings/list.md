---
title: List Service Settings
---

# List Service Settings

Returns all service settings with their current and default values.

```
GET /api/v1/service-settings
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/service-settings \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMServiceSetting

    # Filter by category
    Get-JIMServiceSetting | Where-Object { $_.category -eq "Synchronisation" }
    ```

### Response

Returns `200 OK` with an array of service setting objects.
