---
title: Retrieve an API Key
---

# Retrieve an API Key

```
GET /api/v1/apikeys/{id}
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/apikeys/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMApiKey -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | API key does not exist |
