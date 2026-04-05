---
title: Run Profiles
---

# Run Profiles

A Run Profile defines a synchronisation operation that can be executed against a connected system. Each run profile specifies the type of operation (import, sync, or export), batch size, and optionally a target partition or file path.

Run profiles are the building blocks of [schedules](../schedules/index.md); each schedule step typically references a run profile to execute.

## Common Workflows

**Setting up run profiles for a new connected system:**

1. [Create a connected system](../connected-systems/create.md) and import its schema
2. [Create run profiles](#endpoints) for each operation type needed (typically: delta import, delta sync, export)
3. Add the run profiles as steps in a [schedule](../schedules/create.md)

**Running a one-off import:**

1. [List run profiles](list.md) for the connected system
2. [Execute the run profile](execute.md) to trigger it immediately
3. Monitor progress via the Activities endpoint using the returned activity ID

## The Run Profile Object

```json
{
  "id": 1,
  "name": "Delta Import",
  "connectedSystemId": 1,
  "runType": "DeltaImport",
  "pageSize": 100,
  "partitionName": null,
  "filePath": null
}
```

### Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Display name |
| `connectedSystemId` | integer | Parent connected system ID |
| `runType` | string | Operation type (see below) |
| `pageSize` | integer | Number of objects to process per batch |
| `partitionName` | string, nullable | Target partition name (if applicable) |
| `filePath` | string, nullable | File path for file-based connectors |

### Run Types

| Value | Description |
|-------|-------------|
| `FullImport` | Import all objects from the connected system, replacing existing connector space data |
| `DeltaImport` | Import only objects that have changed since the last import |
| `FullSynchronisation` | Synchronise all connector space objects with the metaverse |
| `DeltaSynchronisation` | Synchronise only objects with pending changes since the last sync |
| `Export` | Export pending changes from the metaverse to the connected system |

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/synchronisation/connected-systems/{id}/run-profiles`](list.md) | List run profiles |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/run-profiles`](create.md) | Create a run profile |
| `PUT` | [`/api/v1/synchronisation/connected-systems/{id}/run-profiles/{runProfileId}`](update.md) | Update a run profile |
| `DELETE` | [`/api/v1/synchronisation/connected-systems/{id}/run-profiles/{runProfileId}`](delete.md) | Delete a run profile |
| `POST` | [`/api/v1/synchronisation/connected-systems/{id}/run-profiles/{runProfileId}/execute`](execute.md) | Execute a run profile |
