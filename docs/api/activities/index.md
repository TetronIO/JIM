---
title: Activities
---

# Activities

An Activity represents a tracked operation in JIM. Every significant action, including run profile executions, schema imports, data generation, certificate management, and configuration changes, creates an activity record with status, timing, and summary statistics.

Activities are the primary mechanism for monitoring synchronisation progress and troubleshooting issues. Run profile activities include detailed per-object execution items and aggregated statistics.

## Common Workflows

**Monitoring a run profile execution:**

1. [Execute a run profile](../run-profiles/execute.md) to get an activity ID
2. [Retrieve the activity](retrieve.md) to check status and progress
3. [Get execution statistics](stats.md) for detailed import/sync/export counts
4. [List execution items](items.md) to inspect per-object results and errors

**Reviewing recent operations:**

1. [List activities](list.md) to see recent operations
2. Filter by name or type to find specific operations
3. [Get child activities](children.md) to see sub-operations spawned by a parent (e.g. schedule steps)

## The Activity Object

When you retrieve an activity, the detail response contains:

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "parentActivityId": null,
  "created": "2026-04-05T06:00:00Z",
  "executed": "2026-04-05T06:00:01Z",
  "status": "Complete",
  "targetType": "ConnectedSystemRunProfile",
  "targetOperationType": "Execute",
  "targetName": "Delta Import",
  "targetContext": "Corporate LDAP",
  "message": "Completed successfully",
  "initiatedByType": "ApiKey",
  "initiatedById": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "initiatedByName": "automation-key",
  "objectsToProcess": 12450,
  "objectsProcessed": 12450,
  "executionTime": "00:01:30",
  "totalActivityTime": "00:01:32",
  "connectedSystemRunType": "DeltaImport",
  "totalAdded": 15,
  "totalUpdated": 230,
  "totalDeleted": 3,
  "totalErrors": 0,
  "childActivityCount": 0,
  "connectedSystemId": 1,
  "connectedSystemRunProfileId": 1,
  "warningMessage": null,
  "errorMessage": null,
  "executionStats": null
}
```

### Core Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `parentActivityId` | guid, nullable | Parent activity ID (for child activities) |
| `created` | datetime | When the activity was created |
| `executed` | datetime, nullable | When execution began |
| `status` | string | Current status (see below) |
| `targetType` | string | What type of resource this activity targets |
| `targetOperationType` | string | What operation was performed |
| `targetName` | string, nullable | Name of the target resource |
| `targetContext` | string, nullable | Parent context (e.g. connected system name for a run profile) |
| `message` | string, nullable | Progress or result message |
| `initiatedByType` | string | Who triggered it: `User`, `ApiKey`, or `System` |
| `initiatedById` | guid, nullable | Initiator ID |
| `initiatedByName` | string, nullable | Initiator display name |
| `objectsToProcess` | integer | Total objects to process |
| `objectsProcessed` | integer | Objects processed so far |
| `executionTime` | timespan, nullable | Time spent executing |
| `totalActivityTime` | timespan, nullable | Total wall-clock time |
| `childActivityCount` | integer | Number of child activities |

### Summary Statistics

Activities include summary counters relevant to their operation type:

| Field | Type | Description |
|-------|------|-------------|
| `totalAdded` | integer | Objects added (imports) |
| `totalUpdated` | integer | Objects updated (imports) |
| `totalDeleted` | integer | Objects deleted (imports) |
| `totalProjected` | integer | New metaverse objects created (sync) |
| `totalJoined` | integer | Objects joined to existing metaverse objects (sync) |
| `totalAttributeFlows` | integer | Attribute values flowed (sync) |
| `totalDisconnected` | integer | Objects disconnected (sync) |
| `totalProvisioned` | integer | Objects provisioned to other systems (sync) |
| `totalExported` | integer | Objects exported (export) |
| `totalDeprovisioned` | integer | Objects deprovisioned (export) |
| `totalPendingExports` | integer | Pending exports created |
| `totalErrors` | integer | Total error count |

### Activity Statuses

| Status | Description |
|--------|-------------|
| `InProgress` | Currently executing |
| `Complete` | Finished successfully |
| `CompleteWithWarning` | Finished with non-fatal warnings |
| `CompleteWithError` | Finished but some objects had errors |
| `FailedWithError` | Failed due to a critical error |
| `Cancelled` | Was cancelled before completion |

### Target Types

| Type | Description |
|------|-------------|
| `ConnectedSystemRunProfile` | Run profile execution (import, sync, export) |
| `ConnectedSystem` | Connected system operation (schema import, hierarchy import, delete, clear) |
| `SyncRule` | Sync rule modification |
| `MetaverseObject` | Metaverse object operation |
| `MetaverseAttribute` | Metaverse attribute modification |
| `TrustedCertificate` | Certificate management |
| `ExampleDataTemplate` | Example data generation |
| `ObjectMatchingRule` | Object matching rule modification |
| `ServiceSetting` | Service setting change |
| `HistoryRetentionCleanup` | History cleanup operation |

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | [`/api/v1/activities`](list.md) | List activities |
| `GET` | [`/api/v1/activities/{id}`](retrieve.md) | Retrieve an activity |
| `GET` | [`/api/v1/activities/{id}/stats`](stats.md) | Get run profile execution statistics |
| `GET` | [`/api/v1/activities/{id}/items`](items.md) | List execution items |
| `GET` | [`/api/v1/activities/{id}/children`](children.md) | List child activities |
