---
title: Pending Exports
---

# Pending Exports

Pending exports represent changes that JIM has computed and queued for writing back to the external identity store. Each pending export contains the attribute changes that will be applied when an export run profile is executed.

---

## List Pending Exports

Returns a paginated list of pending exports for a connected system.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/pending-exports
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

### Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `page` | integer | `1` | Page number (1-based) |
| `pageSize` | integer | `50` | Items per page |
| `search` | string | | Filter by target object identifier or display name |

### Examples

=== "curl"

    ```bash
    curl "https://jim.example.com/api/v1/synchronisation/connected-systems/1/pending-exports?page=1&pageSize=25" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPendingExport -ConnectedSystemId 1
    ```

### Response

Returns `200 OK` with a paginated list of pending export summaries.

```json
{
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "connectedSystemId": 1,
      "changeType": "Update",
      "status": "Pending",
      "createdAt": "2026-03-20T14:15:00Z",
      "lastAttemptedAt": null,
      "nextRetryAt": null,
      "errorCount": 0,
      "maxRetries": 3,
      "lastErrorMessage": null,
      "hasUnresolvedReferences": false,
      "targetObjectIdentifier": "CN=Jane Smith,OU=Users,DC=example,DC=com",
      "sourceMetaverseObjectId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "sourceMetaverseObjectDisplayName": "Jane Smith",
      "attributeChangeCount": 2,
      "connectedSystemObjectId": "c3d4e5f6-a7b8-9012-cdef-123456789012"
    }
  ],
  "totalCount": 15,
  "page": 1,
  "pageSize": 25,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

### List Item Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `connectedSystemId` | integer | Parent connected system ID |
| `changeType` | string | Type of change: `Create`, `Update`, `Delete` |
| `status` | string | Export status: `Pending`, `Processing`, `Success`, `Failed` |
| `createdAt` | datetime | UTC timestamp when the export was queued |
| `lastAttemptedAt` | datetime, nullable | UTC timestamp of the last export attempt |
| `nextRetryAt` | datetime, nullable | When the next retry is scheduled (if failed) |
| `errorCount` | integer | Number of failed export attempts |
| `maxRetries` | integer | Maximum retry attempts before giving up |
| `lastErrorMessage` | string, nullable | Error message from the last failed attempt |
| `hasUnresolvedReferences` | boolean | Whether the export contains references to objects not yet exported |
| `targetObjectIdentifier` | string, nullable | Identifier of the target object in the external store |
| `sourceMetaverseObjectId` | guid, nullable | Source metaverse object ID |
| `sourceMetaverseObjectDisplayName` | string, nullable | Source metaverse object display name |
| `attributeChangeCount` | integer | Number of attribute changes in this export |
| `connectedSystemObjectId` | guid, nullable | Associated connector space object ID |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |

---

## Retrieve a Pending Export

Returns full details of a pending export, including all attribute changes.

```
GET /api/v1/synchronisation/pending-exports/{pendingExportId}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `pendingExportId` | guid | ID of the pending export |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/pending-exports/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    ```

### Response

Returns `200 OK` with the full pending export including attribute changes.

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "connectedSystemId": 1,
  "connectedSystemName": "Corporate LDAP",
  "changeType": "Update",
  "status": "Pending",
  "createdAt": "2026-03-20T14:15:00Z",
  "errorCount": 0,
  "maxRetries": 3,
  "lastAttemptedAt": null,
  "nextRetryAt": null,
  "lastErrorMessage": null,
  "hasUnresolvedReferences": false,
  "connectedSystemObjectId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "connectedSystemObjectDisplayName": "Jane Smith",
  "connectedSystemObjectTypeName": "user",
  "sourceMetaverseObjectId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "sourceMetaverseObjectDisplayName": "Jane Smith",
  "sourceMetaverseObjectTypeName": "person",
  "attributeChanges": [
    {
      "id": "d4e5f6a7-b8c9-0123-defa-234567890123",
      "attributeId": 101,
      "attributeName": "telephoneNumber",
      "changeType": "Replace",
      "status": "Pending",
      "stringValue": "+44 20 7946 0958",
      "dateTimeValue": null,
      "intValue": null,
      "longValue": null,
      "guidValue": null,
      "boolValue": null,
      "unresolvedReferenceValue": null,
      "exportAttemptCount": 0
    }
  ],
  "attributeChangeSummaries": null
}
```

### Attribute Change Fields

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `attributeId` | integer | Attribute definition ID |
| `attributeName` | string | Attribute name |
| `changeType` | string | Type of change: `Add`, `Replace`, `Delete` |
| `status` | string | Change status: `Pending`, `Success`, `Failed` |
| `stringValue` | string, nullable | New string value |
| `dateTimeValue` | datetime, nullable | New datetime value |
| `intValue` | integer, nullable | New integer value |
| `longValue` | long, nullable | New long value |
| `guidValue` | guid, nullable | New GUID value |
| `boolValue` | boolean, nullable | New boolean value |
| `unresolvedReferenceValue` | string, nullable | Unresolved reference value (if the target object has not been exported yet) |
| `exportAttemptCount` | integer | Number of times this specific change has been attempted |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Pending export does not exist |

---

## List Attribute Changes

Returns a paginated list of values for a specific attribute change within a pending export. This is useful for multi-valued attribute changes that may affect many values.

```
GET /api/v1/synchronisation/pending-exports/{pendingExportId}/attribute-changes/{attributeName}/values
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `pendingExportId` | guid | ID of the pending export |
| `attributeName` | string | Name of the attribute |

### Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `page` | integer | `1` | Page number (1-based) |
| `pageSize` | integer | `50` | Items per page |
| `search` | string | | Filter values by search term |

### Examples

=== "curl"

    ```bash
    curl "https://jim.example.com/api/v1/synchronisation/pending-exports/a1b2c3d4-e5f6-7890-abcd-ef1234567890/attribute-changes/memberOf/values?page=1&pageSize=20" \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -AttributeName "memberOf"
    ```

### Response

Returns `200 OK` with a paginated list of attribute change values.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Pending export or attribute does not exist |
