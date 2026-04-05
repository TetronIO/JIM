---
title: List Child Activities
---

# List Child Activities

Returns all child activities spawned by a parent activity. For example, a schedule execution may create child activities for each run profile step.

```
GET /api/v1/activities/{id}/children
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | guid | ID of the parent activity |

## Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/activities/a1b2c3d4-e5f6-7890-abcd-ef1234567890/children \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

    # Via pipeline
    Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
        Get-JIMActivityChildren
    ```

## Response

Returns `200 OK` with an array of activity summaries (same format as [List Activities](list.md)). Returns an empty array if the activity has no children.

## Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Parent activity does not exist |
