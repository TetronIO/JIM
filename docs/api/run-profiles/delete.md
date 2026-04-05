---
title: Delete a Run Profile
---

# Delete a Run Profile

Permanently deletes a run profile. Any schedule steps referencing this run profile should be removed first.

```
DELETE /api/v1/synchronisation/connected-systems/{connectedSystemId}/run-profiles/{runProfileId}
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `runProfileId` | integer | ID of the run profile |

## Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/connected-systems/1/run-profiles/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Delete by ID
    Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

    # Delete by connected system name, skip confirmation
    Remove-JIMRunProfile -ConnectedSystemName "Corporate LDAP" -RunProfileId 1 -Force

    # Delete via pipeline
    Get-JIMRunProfile -ConnectedSystemId 1 |
        Where-Object { $_.name -like "Test*" } |
        Remove-JIMRunProfile -Force
    ```

## Response

Returns `204 No Content` on success.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or run profile does not exist |
