---
title: Object Matching Rules
---

# Object Matching Rules

Object matching rules determine how connector space objects are matched to existing metaverse objects during synchronisation. When an imported object doesn't have a direct join, matching rules evaluate attributes to find a corresponding metaverse object.

JIM supports two matching modes:

- **Simple mode** (default): Matching rules are configured per object type on the connected system. All sync rules for that object type share the same matching rules.
- **Advanced mode**: Matching rules are configured per sync rule, allowing different matching logic for different sync rules on the same object type.

Use the [Switch Matching Mode](#switch-matching-mode) endpoint to change between modes.

### The Matching Rule Object

```json
{
  "id": 1,
  "order": 0,
  "connectedSystemObjectTypeId": 10,
  "connectedSystemObjectTypeName": "user",
  "metaverseObjectTypeId": 1,
  "metaverseObjectTypeName": "person",
  "syncRuleId": null,
  "targetMetaverseAttributeId": 3,
  "targetMetaverseAttributeName": "employeeId",
  "sources": [
    {
      "id": 1,
      "order": 0,
      "connectedSystemAttributeId": 105,
      "connectedSystemAttributeName": "employeeNumber",
      "metaverseAttributeId": null,
      "metaverseAttributeName": null
    }
  ],
  "caseSensitive": false,
  "created": "2026-01-15T10:00:00Z"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `order` | integer | Evaluation order (lower values evaluated first) |
| `connectedSystemObjectTypeId` | integer | Connected system object type |
| `connectedSystemObjectTypeName` | string, nullable | Object type name |
| `metaverseObjectTypeId` | integer, nullable | Metaverse object type to search |
| `metaverseObjectTypeName` | string, nullable | Metaverse object type name |
| `syncRuleId` | integer, nullable | Associated sync rule (advanced mode only) |
| `targetMetaverseAttributeId` | integer, nullable | MV attribute to match against |
| `targetMetaverseAttributeName` | string, nullable | Target MV attribute name |
| `sources` | array | Source attributes to match from |
| `caseSensitive` | boolean | Whether matching is case-sensitive |
| `created` | datetime | UTC creation timestamp |

---

## List Matching Rules (Simple Mode)

Returns matching rules for a connected system object type.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/matching-rules
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/object-types/10/matching-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10
    ```

### Response

Returns `200 OK` with an array of matching rule objects.

---

## Create a Matching Rule

Creates a matching rule at the connected system level (simple mode).

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `connectedSystemObjectTypeId` | integer | Yes | CS object type ID |
| `metaverseObjectTypeId` | integer | Yes | MV object type to search |
| `targetMetaverseAttributeId` | integer | Yes | MV attribute to match against |
| `sources` | array | Yes | At least one source attribute |
| `order` | integer | No | Evaluation order |
| `caseSensitive` | boolean | No | Case-sensitive matching (default: `true`) |

Each source:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `order` | integer | No | Source evaluation order |
| `connectedSystemAttributeId` | integer | No | CS attribute to match from (import) |
| `metaverseAttributeId` | integer | No | MV attribute to match from (export) |

### Examples

=== "curl"

    ```bash
    # Match CS employeeNumber to MV employeeId
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/matching-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "connectedSystemObjectTypeId": 10,
        "metaverseObjectTypeId": 1,
        "targetMetaverseAttributeId": 3,
        "sources": [
          {
            "order": 0,
            "connectedSystemAttributeId": 105
          }
        ],
        "caseSensitive": false
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMMatchingRule -ConnectedSystemId 1 `
        -ObjectTypeId 10 `
        -MetaverseObjectTypeId 1 `
        -SourceAttributeId 105 `
        -TargetMetaverseAttributeId 3 `
        -CaseSensitive $false
    ```

### Response

Returns `201 Created` with the matching rule object.

---

## Update a Matching Rule

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules/{ruleId}
```

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/matching-rules/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "caseSensitive": true,
        "order": 0
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMMatchingRule -ConnectedSystemId 1 -Id 1 -CaseSensitive $true
    ```

---

## Delete a Matching Rule

```
DELETE /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules/{ruleId}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/connected-systems/1/matching-rules/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 1 -Force
    ```

---

## List Matching Rules (Advanced Mode)

Returns matching rules configured on a specific sync rule.

```
GET /api/v1/synchronisation/sync-rules/{syncRuleId}/matching-rules
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/1/matching-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMSyncRuleMatchingRule -SyncRuleId 1
    ```

---

## Create a Matching Rule (Advanced Mode)

Creates a matching rule on a specific sync rule. The metaverse object type is derived from the sync rule.

```
POST /api/v1/synchronisation/sync-rules/{syncRuleId}/matching-rules
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `targetMetaverseAttributeId` | integer | Yes | MV attribute to match against |
| `sources` | array | Yes | At least one source attribute |
| `order` | integer | No | Evaluation order |
| `caseSensitive` | boolean | No | Case-sensitive matching (default: `true`) |

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/1/matching-rules \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "targetMetaverseAttributeId": 3,
        "sources": [
          {
            "order": 0,
            "connectedSystemAttributeId": 105
          }
        ],
        "caseSensitive": false
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMSyncRuleMatchingRule -SyncRuleId 1 `
        -SourceAttributeId 105 `
        -TargetMetaverseAttributeId 3 `
        -CaseSensitive $false
    ```

---

## Switch Matching Mode

Switches a connected system between simple mode (matching rules per object type) and advanced mode (matching rules per sync rule).

```
POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-mode
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `mode` | string | Yes | `ConnectedSystem` (simple) or `SyncRule` (advanced) |

!!! warning
    Switching modes may migrate or remove existing matching rules. Review the response to understand what changes were made.

### Examples

=== "curl"

    ```bash
    # Switch to advanced mode (per sync rule)
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/matching-mode \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "mode": "SyncRule" }'

    # Switch back to simple mode (per object type)
    curl -X POST https://jim.example.com/api/v1/synchronisation/connected-systems/1/matching-mode \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "mode": "ConnectedSystem" }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Switch to advanced mode
    Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule

    # Switch back to simple mode
    Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode ConnectedSystem
    ```

### Response

Returns `200 OK` with the mode switch result.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | Invalid mode or cannot switch (e.g. conflicting rules) |
| `401` | `UNAUTHORISED` | Authentication required |
| `404` | `NOT_FOUND` | Connected system does not exist |
