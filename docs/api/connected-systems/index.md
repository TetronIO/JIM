---
title: Connected Systems
---

# Connected Systems

A Connected System represents an external identity store that synchronises with the JIM metaverse. Examples include LDAP directories, HR databases, and file-based data sources.

Each connected system is associated with a [connector](../../connectors/index.md) that defines how JIM communicates with the external store, and contains a connector space of imported objects, a discovered schema, and optional partition/container hierarchy.

## Common Workflows

**Setting up a new connected system:**

1. List connector definitions to find the connector type
2. [Create a connected system](create.md) with the chosen connector
3. [Update the connected system](update.md) to configure connector settings
4. [Import schema](import-schema.md) to discover object types and attributes
5. Configure [object types](object-types.md) and [attributes](attributes.md)
6. Configure [partitions](partitions.md) if the connector supports hierarchy
7. Create run profiles for import, sync, and export operations

**Removing a connected system:**

1. [Preview deletion impact](deletion-preview.md) to understand what will be affected
2. [Delete the connected system](delete.md)

## The Connected System Object

When you retrieve a connected system, the detail response contains:

```json
{
  "id": 1,
  "name": "Corporate LDAP",
  "description": "Primary directory for employee accounts",
  "created": "2026-01-15T09:30:00Z",
  "lastUpdated": "2026-03-20T14:12:00Z",
  "status": "Active",
  "settingValuesValid": true,
  "connector": {
    "id": 3,
    "name": "JIM LDAP Connector"
  },
  "objectTypes": [
    {
      "id": 10,
      "name": "user",
      "created": "2026-01-15T09:31:00Z",
      "selected": true,
      "removeContributedAttributesOnObsoletion": false,
      "attributeCount": 47
    }
  ],
  "objectCount": 12450,
  "pendingExportCount": 0,
  "maxExportParallelism": 4
}
```

### Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Display name of the connected system |
| `description` | string, nullable | Optional description |
| `created` | datetime | UTC timestamp when the system was created |
| `lastUpdated` | datetime, nullable | UTC timestamp of the last modification |
| `status` | string | Operational status: `Active` or `Deleting` |
| `settingValuesValid` | boolean | Whether the connector settings have been validated. Resets to `false` when settings change. |
| `connector` | object | Reference to the connector definition (`id`, `name`) |
| `objectTypes` | array | Discovered schema object types (see [Object Types](#object-types)) |
| `objectCount` | integer | Total number of objects in the connector space |
| `pendingExportCount` | integer | Number of exports queued for processing |
| `maxExportParallelism` | integer, nullable | Maximum concurrent export batches (1-16). `null` defaults to sequential. |

### Object Types

Each object type in the `objectTypes` array contains:

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Object type name (e.g. `user`, `group`) |
| `created` | datetime | UTC timestamp when discovered |
| `selected` | boolean | Whether this type is included in synchronisation |
| `removeContributedAttributesOnObsoletion` | boolean | Remove contributed attributes when a sync rule no longer applies |
| `attributeCount` | integer | Number of attributes in this object type |

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems`](list.md) | List connected systems |
| `POST` | [`/api/v1/synchronisation/connected-systems`](create.md) | Create a connected system |
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}`](retrieve.md) | Retrieve a connected system |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}`](update.md) | Update a connected system |
| `DELETE` | [`/api/v1/synchronisation/connected-systems/{id}`](delete.md) | Delete a connected system |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/import-schema`](import-schema.md) | Import schema from external store |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/import-hierarchy`](import-hierarchy.md) | Import partition/container hierarchy |
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/deletion-preview`](deletion-preview.md) | Preview deletion impact |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/clear`](clear-connector-space.md) | Clear all objects from connector space |

### Schema and Configuration

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/object-types`](object-types.md) | List object types and attributes |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/object-types/{typeId}`](object-types.md#update-an-object-type) | Update object type settings |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/object-types/{typeId}/attributes/{attrId}`](attributes.md) | Update attribute settings |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/object-types/{typeId}/attributes/bulk-update`](attributes.md#bulk-update-attributes) | Bulk update attribute settings |

### Partitions and Containers

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/partitions`](partitions.md) | List partitions and containers |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/partitions/{partitionId}`](partitions.md#update-a-partition) | Update partition selection |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/containers/{containerId}`](partitions.md#update-a-container) | Update container selection |

### Connector Space

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/connector-space/{objectId}`](connector-space.md) | Retrieve a connector space object |
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/connector-space/{objectId}/attributes/{attrName}/values`](connector-space.md#list-attribute-values) | List object attribute values (paginated) |
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/connector-space/unresolved-references/count`](connector-space.md#count-unresolved-references) | Count unresolved reference attributes |

### Pending Exports

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/pending-exports`](pending-exports.md) | List pending exports |
| `GET` | [`/api/v1/synchronisation/pending-exports/{exportId}`](pending-exports.md#retrieve-a-pending-export) | Retrieve a pending export |
| `GET` | [`/api/v1/synchronisation/pending-exports/{exportId}/attribute-changes/{attrName}/values`](pending-exports.md#list-attribute-changes) | List attribute changes for a pending export |
