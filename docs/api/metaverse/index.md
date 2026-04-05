---
title: Metaverse
---

# Metaverse

The Metaverse is JIM's central identity store. It contains object types (schema definitions), attributes, and the identity objects themselves. All synchronisation flows through the metaverse: import rules bring data in from connected systems, and export rules push data out.

## Key Concepts

**Object Types** define the schema categories in the metaverse (e.g. `person`, `group`). Each object type has associated attributes and configurable deletion rules.

**Attributes** define the fields available on metaverse objects (e.g. `displayName`, `mail`, `employeeId`). Attributes can be single-valued or multi-valued, and support multiple data types.

**Objects** are the identity records in the metaverse. Each object has a type, attribute values, and may be linked to one or more connector space objects in connected systems.

**Pending Deletions** track metaverse objects that are awaiting deletion after all their connector space objects have been disconnected. Deletion rules and grace periods control when objects are actually removed.

## Endpoints

### Object Types

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/metaverse/object-types`](object-types.md) | List object types |
| `GET` | [`/api/v1/metaverse/object-types/{id}`](object-types.md#retrieve-an-object-type) | Retrieve an object type |
| `PUT` | [`/api/v1/metaverse/object-types/{id}`](object-types.md#update-an-object-type) | Update deletion rules |

### Attributes

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/metaverse/attributes`](attributes.md) | List attributes |
| `GET` | [`/api/v1/metaverse/attributes/{id}`](attributes.md#retrieve-an-attribute) | Retrieve an attribute |
| `POST` | [`/api/v1/metaverse/attributes`](attributes.md#create-an-attribute) | Create an attribute |
| `PUT` | [`/api/v1/metaverse/attributes/{id}`](attributes.md#update-an-attribute) | Update an attribute |
| `DELETE` | [`/api/v1/metaverse/attributes/{id}`](attributes.md#delete-an-attribute) | Delete an attribute |

### Objects

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/metaverse/objects`](objects.md) | List objects |
| `GET` | [`/api/v1/metaverse/objects/{id}`](objects.md#retrieve-an-object) | Retrieve an object with attributes and connectors |

### Pending Deletions

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/metaverse/pending-deletions`](pending-deletions.md) | List pending deletions |
| `GET` | [`/api/v1/metaverse/pending-deletions/count`](pending-deletions.md#count-pending-deletions) | Count pending deletions |
| `GET` | [`/api/v1/metaverse/pending-deletions/summary`](pending-deletions.md#pending-deletions-summary) | Pending deletions summary |
