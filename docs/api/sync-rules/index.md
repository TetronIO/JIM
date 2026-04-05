---
title: Sync Rules
---

# Sync Rules

A Sync Rule defines how objects flow between a connected system and the JIM metaverse. Each sync rule maps a connected system object type to a metaverse object type, specifies a direction (import or export), and contains attribute mappings that control how data flows between the two.

Sync rules are the core configuration mechanism for identity synchronisation. They determine which objects are joined, projected, or provisioned, and how attributes are transformed during the process.

## Key Concepts

**Direction:**

- **Import** sync rules flow data from the connected system into the metaverse. They can optionally project new metaverse objects when no match is found.
- **Export** sync rules flow data from the metaverse out to the connected system. They can optionally provision new objects in the connected system, and support scoping criteria to control which metaverse objects are exported.

**Attribute Mappings:** Define how individual attributes are transformed between systems. Mappings can be direct (one attribute to another), multi-source (combining multiple attributes), or expression-based (using DynamicExpresso syntax).

**Scoping Criteria:** Control which objects are in scope for a sync rule. For export rules, criteria evaluate metaverse object attributes; for import rules, criteria evaluate connected system object attributes. Criteria are organised into groups with AND/OR logic and support nested groups for complex conditions.

**Object Matching Rules:** Determine how connector space objects are matched to existing metaverse objects during synchronisation. Rules can be configured at the connected system level (simple mode) or per sync rule (advanced mode).

## Common Workflows

**Setting up an import sync rule:**

1. [Create a sync rule](create.md) with direction `Import`
2. [Add attribute mappings](mappings.md) to flow data from CS attributes to MV attributes
3. Configure [object matching rules](matching-rules.md) to join imported objects to existing metaverse objects

**Setting up an export sync rule with scoping:**

1. [Create a sync rule](create.md) with direction `Export` and `provisionToConnectedSystem` enabled
2. [Add attribute mappings](mappings.md) to flow data from MV attributes to CS attributes
3. [Add scoping criteria](scoping-criteria.md) to control which metaverse objects are exported
4. Configure [object matching rules](matching-rules.md) for the export direction

## The Sync Rule Object

```json
{
  "id": 1,
  "name": "Import Users from LDAP",
  "created": "2026-01-15T09:30:00Z",
  "connectedSystemId": 1,
  "connectedSystemName": "Corporate LDAP",
  "connectedSystemObjectTypeId": 10,
  "connectedSystemObjectTypeName": "user",
  "metaverseObjectTypeId": 1,
  "metaverseObjectTypeName": "person",
  "direction": "Import",
  "projectToMetaverse": true,
  "provisionToConnectedSystem": null,
  "enabled": true,
  "enforceState": true
}
```

### Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Sync rule name |
| `created` | datetime | UTC creation timestamp |
| `connectedSystemId` | integer | Connected system ID |
| `connectedSystemName` | string | Connected system name |
| `connectedSystemObjectTypeId` | integer | Connected system object type ID |
| `connectedSystemObjectTypeName` | string | Connected system object type name (e.g. `user`) |
| `metaverseObjectTypeId` | integer | Metaverse object type ID |
| `metaverseObjectTypeName` | string | Metaverse object type name (e.g. `person`) |
| `direction` | string | `Import` or `Export` |
| `projectToMetaverse` | boolean, nullable | Create new metaverse objects when no match found (import rules) |
| `provisionToConnectedSystem` | boolean, nullable | Create new objects in the connected system (export rules) |
| `enabled` | boolean | Whether the sync rule is active |
| `enforceState` | boolean | Detect and remediate attribute drift (export rules) |

## Endpoints

### Core

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/sync-rules`](list.md) | List sync rules |
| `POST` | [`/api/v1/synchronisation/sync-rules`](create.md) | Create a sync rule |
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}`](retrieve.md) | Retrieve a sync rule |
| `PUT` | [`/api/v1/synchronisation/sync-rules/{id}`](update.md) | Update a sync rule |
| `DELETE` | [`/api/v1/synchronisation/sync-rules/{id}`](delete.md) | Delete a sync rule |

### Attribute Mappings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}/mappings`](mappings.md) | List mappings |
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}/mappings/{mappingId}`](mappings.md#retrieve-a-mapping) | Retrieve a mapping |
| `POST` | [`/api/v1/synchronisation/sync-rules/{id}/mappings`](mappings.md#create-a-mapping) | Create a mapping |
| `DELETE` | [`/api/v1/synchronisation/sync-rules/{id}/mappings/{mappingId}`](mappings.md#delete-a-mapping) | Delete a mapping |

### Scoping Criteria

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria`](scoping-criteria.md) | List scoping criteria groups |
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}`](scoping-criteria.md#retrieve-a-criteria-group) | Retrieve a criteria group |
| `POST` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria`](scoping-criteria.md#create-a-criteria-group) | Create a criteria group |
| `POST` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}/child-groups`](scoping-criteria.md#create-a-child-group) | Create a child group |
| `PUT` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}`](scoping-criteria.md#update-a-criteria-group) | Update a criteria group |
| `DELETE` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}`](scoping-criteria.md#delete-a-criteria-group) | Delete a criteria group |
| `POST` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}/criteria`](scoping-criteria.md#create-a-criterion) | Add a criterion to a group |
| `DELETE` | [`/api/v1/synchronisation/sync-rules/{id}/scoping-criteria/{groupId}/criteria/{criterionId}`](scoping-criteria.md#delete-a-criterion) | Delete a criterion |

### Object Matching Rules

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/object-types/{typeId}/matching-rules`](matching-rules.md) | List matching rules (simple mode) |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/matching-rules`](matching-rules.md#create-a-matching-rule) | Create a matching rule (simple mode) |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/matching-rules/{ruleId}`](matching-rules.md#update-a-matching-rule) | Update a matching rule |
| `DELETE` | [`/api/v1/synchronisation/connected-systems/{id}/matching-rules/{ruleId}`](matching-rules.md#delete-a-matching-rule) | Delete a matching rule |
| `GET` | [`/api/v1/synchronisation/sync-rules/{id}/matching-rules`](matching-rules.md#list-matching-rules-advanced-mode) | List matching rules (advanced mode) |
| `POST` | [`/api/v1/synchronisation/sync-rules/{id}/matching-rules`](matching-rules.md#create-a-matching-rule-advanced-mode) | Create a matching rule (advanced mode) |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/matching-mode`](matching-rules.md#switch-matching-mode) | Switch matching mode |
