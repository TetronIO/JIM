# CSO Lookup Cache Strategy

| | |
|---|---|
| **Created** | 2026-02-22 |
| **Last Updated** | 2026-09-23 |
| **Status** | Active |

This document describes the Connected System Object (CSO) lookup cache used by the JIM Worker to eliminate per-object database round-trips during import processing.

## Overview

When the Worker imports objects from a Connected System, it must determine whether each imported object matches an existing CSO in JIM (update) or is new (create). Without caching, this requires a database query per imported object, an O(N) problem that becomes the dominant bottleneck at scale.

The CSO lookup cache is an in-memory index that maps external ID values to CSO GUIDs, enabling O(1) lookups instead of per-object database queries.

## Architecture

```
+------------------+     cache hit      +-------------------+
|  Import Object   | -----------------> |  PK Lookup (fast) |
|  External ID     |                    |  by CSO GUID      |
+--------+---------+                    +-------------------+
         |
         | cache miss
         v
+------------------+     populate       +-------------------+
|  DB Query        | -----------------> |  Cache Entry      |
|  by Attribute    |                    |  (for next time)  |
+------------------+                    +-------------------+
```

- **Technology**: `Microsoft.Extensions.Caching.Memory.IMemoryCache`
- **Scope**: Worker process lifetime (shared across all `JimApplication` instances)
- **Availability**: Worker only. `JIM.Web` receives `null` for the cache parameter and falls back to direct DB queries.

## Cache Key Format

```
cso:{connectedSystemId}:{attributeId}:{lowerExternalIdValue}
```

| Component | Description |
|---|---|
| `connectedSystemId` | The Connected System's integer ID |
| `attributeId` | The external ID attribute's integer ID (primary OR secondary) |
| `lowerExternalIdValue` | The external ID value, lowercased for case-insensitive matching. A Decimal external ID is rendered in its canonical form (`ExternalIdValue.ToCanonicalString`), so an imported value and the stored value produce the same key however each was written (#1283) |

**Value**: The CSO's `Guid` (primary key), used for fast indexed PK lookup.

## CSO Lifecycle and Cache Entries

Each CSO has at most **one** cache entry at any time. The key changes as the CSO progresses through its lifecycle:

```
Provisioning           Confirming Import         Normal Operation
+-----------------+    +-------------------+    +-------------------+
| Secondary ID    | -> | Evict secondary   | -> | Primary ID        |
| (e.g. DN)       |    | Add primary       |    | (e.g. objectGUID) |
| as cache key    |    | (e.g. objectGUID) |    | as cache key      |
+-----------------+    +-------------------+    +-------------------+
```

### Why secondary IDs are needed

When JIM provisions a new object to a Connected System (e.g. creating a user in LDAP), the CSO is created with status `PendingProvisioning`. At this point:

- The **primary** external ID (e.g. `objectGUID`) is unknown; it's assigned by the Connected System during export
- The **secondary** external ID (e.g. `distinguishedName`) is known; JIM computed it from export rules

The CSO is cached by its secondary external ID so that the subsequent **confirming import** can find it via cache lookup instead of a per-object database query.

When the confirming import matches the imported object to the PendingProvisioning CSO:
1. The secondary cache entry is **evicted**
2. A primary cache entry is **added** with the newly-known primary external ID
3. The CSO transitions to `Normal` status

## Population Points

The cache is populated at these points (every CSO creation or update path):

| Event | Where | Cache Action |
|---|---|---|
| Worker startup | `Worker.cs` → `WarmCsoCacheAsync` | Bulk-load all CSO mappings (primary and secondary) |
| Import creates new CSO | `SyncImportTaskProcessor.PerformImportAsync` | Add primary external ID entry |
| Import updates existing CSO | `SyncImportTaskProcessor.PerformImportAsync` | Evict secondary (if present), add primary |
| Provisioning creates CSO | `SyncTaskProcessorBase.FlushPendingExportOperationsAsync` | Add secondary external ID entry |
| Provisioning creates CSO (immediate) | `ExportEvaluationServer.AddSecondaryExternalIdToCsoAsync` | Add secondary external ID entry |
| Cache miss during lookup | `ConnectedSystemServer.GetCsoWithCacheLookupAsync` | Auto-populate from DB query result |

## Lookup Flow

### During import

Import no longer consults `IMemoryCache` per object. At the start of each import against a non-empty Connected System, `SyncImportTaskProcessor.PerformImportAsync` pre-fetches every CSO's external ID state for that system into a per-run dictionary (`ISyncRepository.GetAllCsoImportStateLookupAsync`, one bulk query, #440), keyed in the same `cso:{connectedSystemId}:{attributeId}:{value}` format. For each imported object, `TryAndFindMatchingConnectedSystemObjectAsync` then:

1. **Primary external ID lookup**: an O(1) dictionary lookup (`LookupCsoByExternalId`). A hit returns the CSO's full entity graph from the page's hydration dictionary, which `HydrateCsoPageAsync` batch-loads once per page rather than once per object (#988).
2. **Secondary external ID fallback** (e.g. by `distinguishedName`): runs only when the primary lookup found nothing (or the CSO was deleted after the pre-fetch). It is a per-object database query (`GetConnectedSystemObjectBySecondaryExternalIdAsync`), acceptable because it only ever returns a `PendingProvisioning` CSO, which exists only between provisioning and the confirming import.
3. **Empty Connected System optimisation** (`_csIsEmpty`): if the Connected System has zero CSOs at the start of import, the pre-fetch and every lookup are skipped; every imported object is guaranteed to be new.

The dictionary lives only for the import run, so it needs no eviction. The process-wide cache is still maintained by the import (see Population Points) so that other lookups stay correct.

### Everywhere else

`ConnectedSystemServer.GetConnectedSystemObjectByAttributeAsync` (and its secondary-ID counterpart) resolves through `GetCsoWithCacheLookupAsync`:

- Check cache: a hit loads the CSO by primary key (fast)
- Check cache: a miss queries the database by attribute value, then populates the cache
- A hit whose CSO no longer exists is evicted and treated as a miss

## Eviction

Cache entries are evicted whenever the external ID value they index changes or becomes invalid:

| Event | Action |
|---|---|
| Stale PK hit (CSO deleted since cached) | `GetCsoWithCacheLookupAsync` auto-evicts |
| PendingProvisioning → Normal transition | Confirming import evicts secondary, adds primary |
| Import updates primary external ID (value change) | Old primary entry evicted, new primary entry added |
| Import updates secondary external ID (e.g. DN rename) | Old secondary entry evicted |
| Export result sets/changes primary external ID | Old primary entry evicted, new primary entry added |
| Export result sets/changes secondary external ID | Old secondary entry evicted, new secondary entry added |

### Why eviction on value change matters

External ID values can change in the Connected System. The most common example is an LDAP `distinguishedName` (DN) rename: when a user's OU changes or their name changes, the DN changes. Without eviction, the old DN cache entry remains orphaned. While a lookup with the _new_ DN will miss the cache harmlessly and self-populate from the database, the stale entry becomes dangerous if a _different_ object is later assigned the old DN value: it would incorrectly resolve to the wrong CSO.

## Thread Safety

- `IMemoryCache.Set()` provides upsert semantics; no explicit locking required
- `IMemoryCache.TryGetValue()` is safe for concurrent reads
- The cache is shared across all `JimApplication` instances in the Worker process

## Memory Footprint

Each cache entry stores a string key (~60-80 bytes) and a `Guid` value (16 bytes). Approximate memory per CSO: ~100 bytes.

| CSO Count | Estimated Memory |
|---|---|
| 10,000 | ~1 MB |
| 100,000 | ~10 MB |
| 1,000,000 | ~100 MB |

## Performance Impact

Without cache (per-object DB queries at ~50ms each):
- 10,000 objects: ~8 minutes for CSO matching phase
- 100,000 objects: ~80 minutes

With cache (PK lookups at ~1ms each, or cache-hit at ~0.1ms):
- 10,000 objects: ~10 seconds
- 100,000 objects: ~100 seconds

## Related Per-Run Caches

Two smaller structures added alongside the CSO lookup cache are scoped to a single run or page rather than the Worker process, so they need no eviction policy:

| Structure | Scope | Purpose |
|---|---|---|
| `SyncRuleNameResolverCache` (`src/JIM.Application/Services/`) | One batch | Resolves a contributing Synchronisation Rule's name for attribute value change history (#1519). Seeded with the rules the run already holds and warmed with one batched query for any others, so a batch costs at most one round trip; a deleted rule is cached as null and never re-queried |
| `MetaverseObjectPageIdentityMap` (`src/JIM.Application/Services/`) | One sync page (cleared at each page boundary) | Resolves every load of a Metaverse Object row within a page onto one canonical instance, so a grace-period deletion marker set in one pass is seen by a rejoin in the next (#1612). Its `AbsorbedCount` is a tripwire for a load site that bypassed it |

## Related Documentation

- [Worker Database Performance Optimisation](plans/done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md): original design and future phases (full entity cache, MVO join-attribute cache)
- [PostgreSQL Improvements](plans/done/POSTGRESQL_IMPROVEMENTS.md): database-level cache sizing (`effective_cache_size`)

## Key Source Files

| File | Purpose |
|---|---|
| `src/JIM.Application/Servers/ConnectedSystemServer.cs` | Cache operations: `BuildCsoCacheKey`, `GetCsoWithCacheLookupAsync`, `AddCsoToCache`, `EvictCsoFromCache`, `WarmCsoCacheAsync` |
| `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` | `GetAllCsoExternalIdMappingsAsync`: bulk-loads primary and secondary ID mappings for cache warming |
| `src/JIM.Worker/Worker.cs` | Cache initialisation (`IMemoryCache`) and startup warming |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Per-run external ID dictionary and page hydration for import matching; cache population after import, cache key swap on PendingProvisioning → Normal, eviction on external ID value changes |
| `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` | Cache population after provisioning CSO batch creation |
| `src/JIM.Application/Servers/ExportEvaluationServer.cs` | Cache population after immediate provisioning CSO creation |
| `src/JIM.Application/Servers/ExportExecutionServer.cs` | Cache eviction and re-population when export results set/change primary or secondary external IDs |
| `test/JIM.Worker.Tests/Servers/ConnectedSystemCsoCacheTests.cs` | Unit tests for all cache operations |
