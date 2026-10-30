# Worker Caching Strategy

| | |
|---|---|
| **Created** | 2026-02-22 |
| **Last Updated** | 2026-10-29 |
| **Status** | Active |

This document describes how the JIM Worker avoids per-object database round trips when it matches imported objects to Connected System Objects (CSOs), and the other caches it keeps while a run executes.

## Principle: cache per run, not per process

Every cache the Worker keeps today lives for **one run, one batch or one page**, and is built from the database at the start of that scope. None outlives the task that built it, so none needs eviction, invalidation on delete, a size limit, or coherence across Worker instances.

There is deliberately **no process-wide CSO cache**. The Worker used to keep one: an `IMemoryCache` of external ID to CSO ID, warmed for every Connected System at start-up and maintained by import, provisioning and export. After the per-run lookup below replaced it on the import path (#440), nothing read it, while the Worker still paid for it in a blocking start-up load, about 100 bytes per CSO held for the process's lifetime, entries never evicted when objects or whole Connected Systems were deleted, and keys that ignored case for case-exact anchors (#2051). It was removed in #2246.

A future process-wide or persistent cache (#436, options A and C) must be designed for exactly those concerns, and should plug into the lookup and hydration seam described below rather than revive the old one.

## Import matching: lookup, then hydration

When the Worker imports objects from a Connected System, it must decide for each one whether it matches an existing CSO (update) or is new (create). This is split into two steps with separate method boundaries (#440), so either can later be backed by a different store:

| Step | Data | Lifetime | Today |
|---|---|---|---|
| **Lookup** (external ID to CSO ID) | ~200 bytes per entry | One import run | In-memory dictionary, one bulk query per run |
| **Hydration** (CSO ID to full entity graph) | ~20 to 40 KB per entry | One page | Batch-loaded per page of imported objects |

### Lookup

At the start of an import against a non-empty Connected System, `SyncImportTaskProcessor.PerformImportAsync` loads every CSO's external ID state into a per-run dictionary with one query (`ISyncRepository.GetAllCsoImportStateLookupAsync`). Each CSO has one entry, keyed by:

- its **primary** external ID, for CSOs that have been imported; or
- its **secondary** external ID (for example a distinguished name), for Pending Provisioning CSOs whose primary ID the target system has not yet assigned.

Keys are built by `JIM.Utilities.CsoLookupKey`: `cso:{connectedSystemId}:{attributeId}:{value}`, where the value is folded to lower case unless the anchor attribute is declared case-exact (#2051), and a Decimal value is rendered in its canonical form so a stored and an imported value produce the same key however each was written (#1283). Each entry also carries the stored content hash, schema fingerprint, status and partition, so a Full Import can evaluate its skip predicate (SPEC-1082) without another round trip.

For each imported object, `LookupCsoByExternalId` answers from the dictionary in O(1).

### Hydration

`HydrateCsoPageAsync` batch-loads the full entity graph of every CSO a page matched, once per page rather than once per object (#988). It also resolves, one query per object type per page, the secondary external ID of every imported object with no primary match, which is how a confirming import finds the Pending Provisioning CSO it confirms. A per-object query remains only for values the page batch cannot resolve (no secondary external ID attribute, a non-Text secondary ID, or more than one CSO sharing the value).

### Empty Connected System

If the Connected System has no CSOs when the import starts (`_csIsEmpty`), the pre-fetch, every lookup and the secondary fallback are skipped: every imported object is guaranteed to be new.

## Other per-run caches

| Structure | Scope | Purpose |
|---|---|---|
| Export evaluation cache (`ExportEvaluationServer.BuildExportEvaluationCacheAsync`) | One synchronisation run; CSO portions refreshed per page | Export rules by Metaverse Object Type and CSO lookups by (Metaverse Object, Connected System), so export evaluation avoids O(N x M) queries |
| `SyncRuleNameResolverCache` (`src/JIM.Application/Services/`) | One batch | Resolves a contributing Synchronisation Rule's name for attribute value change history (#1519). Seeded with the rules the run already holds and warmed with one batched query for any others, so a batch costs at most one round trip; a deleted rule is cached as null and never re-queried |
| `MetaverseObjectPageIdentityMap` (`src/JIM.Application/Services/`) | One sync page (cleared at each page boundary) | Resolves every load of a Metaverse Object row within a page onto one canonical instance, so a grace-period deletion marker set in one pass is seen by a rejoin in the next (#1612). Its `AbsorbedCount` is a tripwire for a load site that bypassed it |
| `_snapshotReferenceCsos` (`SyncTaskProcessorBase`) | One sync page or cross-page reference batch (cleared with the change tracker, and once the page's provisioning objects are written) | The target CSO each referenced Metaverse Object resolved to in export change snapshots, or null where none exists, so every report of one manager and every group sharing a member stop querying the same object again (#2492). Cleared with the tracker because the CSOs it holds are tracked instances |
| Pending Export batch scope (`SyncRepository.PendingExportBatch.cs`) | One cross-page reference batch (`BeginPendingExportBatch` to flush or abandon) | Prefetches the Pending Exports of the batch's objects in one query and queues the deletes their re-evaluation makes, executing them in one statement (#2492). Reads hide a queued row and an insert executes its object's queued delete first, so merge decisions are unchanged; any write touching a prefetched row drops it, and a failed batch is abandoned rather than flushed |

## Related Documentation

- [Worker Database Performance Optimisation](plans/done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md): original design of the process-wide cache (since removed) and future phases
- [PostgreSQL Improvements](plans/done/POSTGRESQL_IMPROVEMENTS.md): database-level cache sizing (`effective_cache_size`)
- [#436](https://github.com/TetronIO/JIM/issues/436): future caching options (full entity cache, Metaverse Object join-attribute cache, persistent in-memory model)

## Key Source Files

| File | Purpose |
|---|---|
| `src/JIM.Utilities/CsoLookupKey.cs` | Lookup key format and case-exact-aware value folding |
| `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` | `GetAllCsoImportStateLookupAsync`: the per-run bulk lookup query |
| `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` | Per-run lookup dictionary, `LookupCsoByExternalId`, `HydrateCsoPageAsync` and the secondary external ID fallback |
| `test/JIM.Worker.Tests/Synchronisation/ImportBatchPrefetchTests.cs` | Lookup and hydration behaviour |
| `test/JIM.Worker.Tests/Repositories/CaseExactIdentifierLookupDatabaseTests.cs` | Case-exact keying against real PostgreSQL |
