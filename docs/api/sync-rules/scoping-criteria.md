---
title: Scoping Criteria
---

# Scoping Criteria

Scoping criteria control which objects are in scope for a sync rule. They are organised into groups that use AND (`All`) or OR (`Any`) logic, and groups can be nested for complex conditions.

Scoping works in both directions:

- **Export rules**: criteria evaluate **metaverse object** attributes to determine which MVOs should be exported (e.g. only export users in the Finance department)
- **Import rules**: criteria evaluate **connected system object** attributes to determine which CSOs should be projected or joined (e.g. only import users from a specific OU)

### The Criteria Group Object

```json
{
  "id": 1,
  "type": "All",
  "position": 0,
  "criteria": [
    {
      "id": 1,
      "metaverseAttributeId": 5,
      "metaverseAttributeName": "department",
      "connectedSystemAttributeId": null,
      "connectedSystemAttributeName": null,
      "attributeDataType": "String",
      "comparisonType": "Equals",
      "stringValue": "Engineering",
      "intValue": null,
      "dateTimeValue": null,
      "boolValue": null,
      "guidValue": null,
      "caseSensitive": false
    }
  ],
  "childGroups": []
}
```

### Comparison Types

| Operator | Description |
|----------|-------------|
| `Equals` | Exact match |
| `NotEquals` | Not an exact match |
| `StartsWith` | Value starts with the specified text |
| `NotStartsWith` | Value does not start with the specified text |
| `EndsWith` | Value ends with the specified text |
| `NotEndsWith` | Value does not end with the specified text |
| `Contains` | Value contains the specified text |
| `NotContains` | Value does not contain the specified text |
| `LessThan` | Value is less than |
| `LessThanOrEquals` | Value is less than or equal to |
| `GreaterThan` | Value is greater than |
| `GreaterThanOrEquals` | Value is greater than or equal to |

---

## List Scoping Criteria Groups

Returns all scoping criteria groups for a sync rule.

```
GET /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMScopingCriteria -SyncRuleId 2
    ```

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `404` | `NOT_FOUND` | Sync rule does not exist |

---

## Retrieve a Criteria Group

```
GET /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{groupId}
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMScopingCriteria -SyncRuleId 2 -GroupId 1
    ```

---

## Create a Criteria Group

```
POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | Yes | `All` (AND logic) or `Any` (OR logic) |
| `position` | integer | No | Group order (default: 0) |

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "type": "All",
        "position": 0
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMScopingCriteriaGroup -SyncRuleId 2 -Type All
    ```

### Response

Returns `201 Created` with the criteria group object.

---

## Create a Child Group

Creates a nested criteria group within an existing group.

```
POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{parentGroupId}/child-groups
```

### Examples

=== "curl"

    ```bash
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1/child-groups \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "type": "Any",
        "position": 0
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    New-JIMScopingCriteriaGroup -SyncRuleId 2 -ParentGroupId 1 -Type Any
    ```

---

## Update a Criteria Group

```
PUT /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{groupId}
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | No | `All` or `Any` |
| `position` | integer | No | New position |

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "type": "Any" }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMScopingCriteriaGroup -SyncRuleId 2 -GroupId 1 -Type Any
    ```

---

## Delete a Criteria Group

Deletes a criteria group and all its criteria and child groups.

```
DELETE /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{groupId}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMScopingCriteriaGroup -SyncRuleId 2 -GroupId 1
    ```

---

## Create a Criterion

Adds a criterion to an existing criteria group.

```
POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{groupId}/criteria
```

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `metaverseAttributeId` | integer | Conditional | MV attribute to evaluate (export rules) |
| `connectedSystemAttributeId` | integer | Conditional | CS attribute to evaluate (import rules) |
| `comparisonType` | string | Yes | Comparison operator (see [Comparison Types](#comparison-types)) |
| `stringValue` | string | Conditional | Value for text comparisons |
| `intValue` | integer | Conditional | Value for numeric comparisons |
| `dateTimeValue` | datetime | Conditional | Value for date comparisons |
| `boolValue` | boolean | Conditional | Value for boolean comparisons |
| `guidValue` | guid | Conditional | Value for GUID comparisons |
| `caseSensitive` | boolean | No | Case-sensitive comparison (default: `true`) |

### Examples

=== "curl"

    ```bash
    # Scope to users in the Engineering department
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1/criteria \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "metaverseAttributeId": 5,
        "comparisonType": "Equals",
        "stringValue": "Engineering",
        "caseSensitive": false
      }'

    # Scope to active users only
    curl -X POST https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1/criteria \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "metaverseAttributeId": 8,
        "comparisonType": "Equals",
        "boolValue": true
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Scope to users in the Engineering department
    New-JIMScopingCriterion -SyncRuleId 2 -GroupId 1 `
        -MetaverseAttributeId 5 `
        -ComparisonType Equals `
        -StringValue "Engineering"

    # Scope to active users only
    New-JIMScopingCriterion -SyncRuleId 2 -GroupId 1 `
        -MetaverseAttributeName "accountEnabled" `
        -ComparisonType Equals `
        -BoolValue $true
    ```

### Response

Returns `201 Created` with the criterion object.

---

## Delete a Criterion

```
DELETE /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/{groupId}/criteria/{criterionId}
```

### Examples

=== "curl"

    ```bash
    curl -X DELETE https://jim.example.com/api/v1/synchronisation/sync-rules/2/scoping-criteria/1/criteria/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Remove-JIMScopingCriterion -SyncRuleId 2 -GroupId 1 -CriterionId 1
    ```

### Response

Returns `204 No Content` on success.
