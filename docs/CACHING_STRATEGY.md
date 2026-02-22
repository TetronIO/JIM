# CSO Lookup Cache Strategy

This document describes the Connected System Object (CSO) lookup cache used by the JIM Worker to eliminate per-object database round-trips during import processing.

## Overview

When the Worker imports objects from a connected system, it must determine whether each imported object matches an existing CSO in JIM (update) or is new (create). Without caching, this requires a database query per imported object — an O(N) problem that becomes the dominant bottleneck at scale.

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
| `connectedSystemId` | The connected system's integer ID |
| `attributeId` | The external ID attribute's integer ID (primary OR secondary) |
| `lowerExternalIdValue` | The external ID value, lowercased for case-insensitive matching |

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

When JIM provisions a new object to a connected system (e.g. creating a user in LDAP), the CSO is created with status `PendingProvisioning`. At this point:

- The **primary** external ID (e.g. `objectGUID`) is unknown — it's assigned by the connected system during export
- The **secondary** external ID (e.g. `distinguishedName`) is known — JIM computed it from export rules

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
| Import creates new CSO | `SyncImportTaskProcessor.PerformFullImportAsync` | Add primary external ID entry |
| Import updates existing CSO | `SyncImportTaskProcessor.PerformFullImportAsync` | Evict secondary (if present), add primary |
| Provisioning creates CSO | `SyncTaskProcessorBase.FlushPendingExportOperationsAsync` | Add secondary external ID entry |
| Provisioning creates CSO (immediate) | `ExportEvaluationServer.AddSecondaryExternalIdToCsoAsync` | Add secondary external ID entry |
| Cache miss during lookup | `ConnectedSystemServer.GetCsoWithCacheLookupAsync` | Auto-populate from DB query result |

## Lookup Flow

When `TryAndFindMatchingConnectedSystemObjectAsync` runs for each imported object:

1. **Primary external ID lookup** (e.g. by `objectGUID`):
   - Check cache → hit: load CSO by PK (fast)
   - Check cache → miss: query DB by attribute value, populate cache
   - If CSO found → return it

2. **Secondary external ID fallback** (e.g. by `distinguishedName`):
   - Only runs if primary lookup returned null
   - Check cache → hit: load CSO by PK (fast)
   - Check cache → miss: query DB by secondary attribute, populate cache
   - Only return if CSO status is `PendingProvisioning`

3. **Empty connected system optimisation** (`_csIsEmpty`):
   - If the connected system has zero CSOs at the start of import, skip all lookups entirely
   - Every imported object is guaranteed to be new

## Eviction

Cache entries are evicted:

| Event | Action |
|---|---|
| Stale PK hit (CSO deleted since cached) | `GetCsoWithCacheLookupAsync` auto-evicts |
| PendingProvisioning → Normal transition | Confirming import evicts secondary, adds primary |

`EvictCsoFromCache` is also available for explicit eviction but is currently only used during the confirming import cache key swap.

## Thread Safety

- `IMemoryCache.Set()` provides upsert semantics — no explicit locking required
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

## Related Documentation

- [Worker Database Performance Optimisation](plans/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md) — original design and future phases (full entity cache, MVO join-attribute cache)
- [PostgreSQL Improvements](plans/POSTGRESQL_IMPROVEMENTS.md) — database-level cache sizing (`effective_cache_size`)

## Key Source Files

| File | Purpose |
|---|---|
| `src/JIM.Application/Servers/ConnectedSystemServer.cs` | Cache operations: `BuildCsoCacheKey`, `GetCsoWithCacheLookupAsync`, `AddCsoToCache`, `EvictCsoFromCache`, `WarmCsoCacheAsync` |
| `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` | `GetAllCsoExternalIdMappingsAsync` — bulk-loads primary and secondary ID mappings for cache warming |
| `src/JIM.Worker/Worker.cs` | Cache initialisation (`IMemoryCache`) and startup warming |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Cache population after import, cache key swap on PendingProvisioning → Normal |
| `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` | Cache population after provisioning CSO batch creation |
| `src/JIM.Application/Servers/ExportEvaluationServer.cs` | Cache population after immediate provisioning CSO creation |
| `test/JIM.Worker.Tests/Servers/ConnectedSystemCsoCacheTests.cs` | Unit tests for all cache operations |
