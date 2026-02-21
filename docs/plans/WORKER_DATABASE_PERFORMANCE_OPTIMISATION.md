# Worker Database Performance Optimisation

- **Status**: Planned
- **Milestone**: Post-MVP
- **GitHub Issue**: [#338](https://github.com/TetronIO/JIM/issues/338)
- **Related**: `docs/plans/EXPORT_PERFORMANCE_OPTIMISATION.md`
- **Created**: 2026-02-20

## Overview

The Worker is JIM's most performance-critical component. It processes imports, synchronisation, and exports -- all of which are database-intensive. Currently, all Worker database interactions go through EF Core, which provides excellent developer productivity but introduces overhead for hot-path operations:

- **Change tracking**: EF scans every tracked entity on `SaveChangesAsync`, even for simple bulk operations
- **Include chain materialisation**: Deep `Include`/`ThenInclude` chains generate multiple SQL round-trips via `AsSplitQuery` and materialise large object graphs
- **Individual SQL statements**: `AddRange`/`UpdateRange`/`RemoveRange` generate one SQL statement per entity, not true bulk operations
- **LINQ translation overhead**: Query translation from LINQ to SQL adds latency per query

This plan takes a **surgical approach**: keep EF Core as the ORM for general use, but replace specific performance-critical Worker queries with raw SQL or Npgsql bulk operations. This delivers the majority of the performance benefit with minimal architectural risk.

**Approach**: EF Core remains the default. Raw SQL is used only where profiling shows meaningful benefit. Each optimisation is independently deployable and benchmarkable.

---

## Business Value

- **Faster sync cycles** - Reduced import/sync/export times directly reduce end-to-end schedule completion time
- **Better scalability** - Enables JIM to handle larger environments (100k+ objects) without linear time increases
- **Lower database load** - Fewer queries, fewer round-trips, less change tracker overhead
- **Predictable performance** - Raw SQL eliminates EF query plan variability

---

## Current State Analysis

### EF Core Feature Usage Across Repositories

| EF Core Feature | Usage Count | Migration Complexity |
|----------------|-------------|---------------------|
| Include/ThenInclude chains | 433 total | HIGH (but only ~10 are on hot paths) |
| AsSplitQuery | 58 | Moderate |
| Change tracking (implicit) | Pervasive | Only targeted methods need changing |
| SaveChangesAsync | 95 | Only bulk operations need replacing |
| ExecuteSqlRawAsync (already raw) | 31 | N/A -- already optimised |

### Established Raw SQL Conventions

The codebase already has 31 raw SQL calls. The established pattern (from `MarkPendingExportsAsExecutingAsync`, `DeleteAllConnectedSystemObjectsAndDependenciesAsync`, etc.) is:

```csharp
// 1. Use ExecuteSqlRawAsync with parameterised queries
await Repository.Database.Database.ExecuteSqlRawAsync(
    @"UPDATE ""PendingExports"" SET ""Status"" = {0} WHERE ""Id"" = ANY({1})",
    statusValue, ids);

// 2. Double-quote PostgreSQL identifiers
// 3. Use ANY({n}) for array-based IN clauses
// 4. Try/catch fallback to EF for unit test compatibility
// 5. Manually update in-memory entities after raw SQL
```

---

## Optimisation Targets

### Priority Ranking

| # | Method | Call Pattern | Current Approach | Estimated Benefit |
|---|--------|-------------|-----------------|-------------------|
| 1 | `GetConnectedSystemObjectByAttributeAsync` (x4 overloads) | Per-object during import (N+1) | AsSplitQuery + 3-level Include chain per object | **Critical** -- eliminate N+1 |
| 2 | `FindMetaverseObjectUsingMatchingRuleAsync` | Per-CSO during sync (N+1) | Dynamic LINQ + AsSplitQuery + Include chain per object | **Critical** -- batch matching |
| 3 | `CreateConnectedSystemObjectsAsync` | Batch after import page | AddRange + SaveChangesAsync (N INSERTs) | **High** -- bulk COPY |
| 4 | `CreatePendingExportsAsync` | Batch after sync page | AddRangeAsync + SaveChangesAsync (N INSERTs) | **High** -- bulk COPY |
| 5 | RPEI creation (via `UpdateActivityAsync`) | Per sync page | Change tracker diffs entire Activity graph | **Moderate-High** |
| 6 | `UpdateConnectedSystemObjectsAsync` | Batch after import page | UpdateRange + SaveChangesAsync (N UPDATEs, all columns) | **Moderate-High** |
| 7 | `DeletePendingExportsAsync` | After confirmed exports | RemoveRange + SaveChangesAsync (N DELETEs + cascades) | **Moderate-High** |
| 8 | `UpdatePendingExportsAsync` | After export execution | UpdateRange + SaveChangesAsync (N UPDATEs, all columns) | **Moderate** |
| 9 | `GetConnectedSystemObjectsAsync` | Per page during full sync | AsSplitQuery + 8-level Include chain, sync `.Count()` | **Moderate** |
| 10 | `GetConnectedSystemObjectsModifiedSinceAsync` | Per page during delta sync | Same as #9 with date filter | **Moderate** |

### What's NOT Being Changed

- All UI-facing queries (activity detail, search, headers) -- these are not on hot paths
- Simple CRUD operations (~40% of repository methods) -- EF overhead is negligible
- The Web project's database interactions -- not performance-critical
- Domain model classes -- the same entities are used, just loaded/saved differently
- Repository interfaces -- method signatures remain unchanged

---

## Implementation Phases

### Phase 1: Eliminate N+1 Import Lookups

**Target**: `GetConnectedSystemObjectByAttributeAsync` (4 overloads in ConnectedSystemRepository)

**Problem**: Called once per object during import. Each call executes 3-4 SQL queries (via AsSplitQuery) with deep Include chains to find a CSO by its external ID attribute. For a 10,000-object import, that's 30,000-40,000 database queries just for lookups.

**Current code** (simplified):
```csharp
var allMatches = await Repository.Database.ConnectedSystemObjects
    .AsSplitQuery()
    .Include(cso => cso.Type).ThenInclude(t => t.Attributes)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.ReferenceValue)
        .ThenInclude(refCso => refCso!.AttributeValues).ThenInclude(refAv => refAv.Attribute)
    .Where(x => x.ConnectedSystem.Id == connectedSystemId &&
        x.AttributeValues.Any(av => av.Attribute.Id == attributeId
            && av.StringValue != null && av.StringValue.ToLower() == lowerValue))
    .ToListAsync();
```

**Approach**: Service-lifetime CSO lookup index using .NET's built-in `IMemoryCache`.

The cache stores a **lookup index only** — mapping external ID values to CSO GUIDs — not full entity graphs. This keeps memory lightweight (~100 bytes per entry), avoids EF Core entity lifecycle concerns (attach/detach, change tracking), and simplifies cache invalidation. When a cache hit returns a CSO GUID, the full entity is loaded by primary key (fast indexed lookup, ~1-2ms) in the current `DbContext`.

**Cache key format**: `$"cso:{connectedSystemId}:{attributeId}:{lowerExternalIdValue}"`

**Population and invalidation**:

| Event | Cache action |
|-------|-------------|
| Full Import start | Bulk-load all CSO external ID → GUID mappings for the connected system |
| Cache hit (any import) | Return GUID, load entity by PK |
| Cache miss (delta import) | Query DB by attribute value, add result to cache if found |
| CSO created (import) | Add to cache after DB persist |
| CSO updated (import) | Update cache entry if external ID changed |
| CSO deleted (obsolete removal) | Evict from cache |
| Full CS deletion | Evict all entries for that connected system |

**Run profile sequencing**:

| Scenario | Behaviour |
|----------|-----------|
| First run is Full Import | Bulk-load all CSO IDs into cache — eliminates N+1 entirely |
| Subsequent Full Import | Cache already warm, all lookups are cache hits |
| First run is Delta Import | No bulk pre-load (wasteful for a few objects). Per-object DB lookup on cache miss, result added to cache |
| Delta Import after Full Import | Cache warm from prior full import, most lookups are cache hits |
| Delta Import, new object in source | Cache miss → DB query → add to cache. Subsequent delta imports for same object are cache hits |

**Cache lifetime**: Service-lifetime (lives as long as the Worker process). No automatic expiration — CSO external IDs rarely change. Explicit invalidation on mutation events ensures coherency.

**Infrastructure**: Uses `Microsoft.Extensions.Caching.Memory` (`IMemoryCache`), already available as a transitive dependency in the Worker — no new NuGet package required. A single `MemoryCache` instance is created at Worker startup and passed through `JimApplication` to the repository/server layer.

**Estimated memory**:

| CSO Count | Cache Memory (index only) | Notes |
|-----------|--------------------------|-------|
| 1,000 | ~100 KB | Small org |
| 10,000 | ~1 MB | Large org |
| 50,000 | ~5 MB | Very large |
| 100,000 | ~10 MB | Extreme — comfortably viable |

**Estimated impact**: Eliminates all N+1 import lookup queries. Import of 10,000 objects drops from ~30,000-40,000 queries to 10,000 PK lookups (cache hit path) or 1 bulk query + 10,000 PK lookups (first full import).

**Complexity**: Moderate

**Migration path**: If the index-only approach proves insufficient for performance (the per-object PK load still adds ~1-2ms per object), the next step is to cache full CSO entity graphs in memory (`AsNoTracking`) rather than just IDs. This eliminates DB queries entirely for cache hits but introduces entity lifecycle complexity (attaching detached entities for modification, staleness of cached attribute values). The `IMemoryCache` infrastructure would remain identical — only the cached value type changes from `Guid` to `ConnectedSystemObject`.

**Files affected**:
- `src/JIM.Application/JimApplication.cs` (accept `IMemoryCache` in constructor)
- `src/JIM.Application/Servers/ConnectedSystemServer.cs` (cache-aware lookup methods)
- `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` (bulk ID loading method, PK lookup method)
- `src/JIM.Data/Repositories/IConnectedSystemRepository.cs` (new method signatures)
- `src/JIM.Worker/Worker.cs` (create and pass `MemoryCache` instance)
- Tests in `test/JIM.Worker.Tests/` for cache behaviour

---

### Phase 2: Batch Metaverse Object Matching

**Target**: `FindMetaverseObjectUsingMatchingRuleAsync` (MetaverseRepository)

**Problem**: Called once per CSO during sync to find the matching MVO. Each call executes multiple queries via AsSplitQuery with dynamic WHERE based on attribute type. For a 10,000-object sync, that's another 10,000+ query sets.

**Current code** (simplified):
```csharp
var metaVerseObjects = Repository.Database.MetaverseObjects
    .AsSplitQuery()
    .Include(mvo => mvo.AttributeValues).ThenInclude(av => av.Attribute)
    .Where(mvo => mvo.Type.Id == metaverseObjectType.Id);

// Dynamic WHERE based on attribute type (Text, Number, Guid, etc.)
metaVerseObjects = metaVerseObjects.Where(mvo =>
    mvo.AttributeValues.Any(av =>
        av.Attribute.Id == targetAttribute.Id &&
        av.StringValue == sourceValue));

var result = await metaVerseObjects.ToListAsync();
```

**Proposed approach**: Lightweight match-first, load-later:
1. **Match query** (raw SQL): Return only MVO IDs that match, using a simple JOIN rather than subquery `Any()`:
   ```sql
   SELECT mvo."Id"
   FROM "MetaverseObjects" mvo
   JOIN "MetaverseObjectAttributeValues" av ON av."MetaverseObjectId" = mvo."Id"
   WHERE mvo."MetaverseObjectTypeId" = @typeId
     AND av."MetaverseAttributeId" = @attrId
     AND av."StringValue" = @value
   LIMIT 2  -- only need to know if 0, 1, or >1 matches
   ```
2. **Entity load**: Load matched MVO(s) by ID with existing Include chain only when a match is found

**Future consideration**: Batch matching (matching all CSOs in a page at once via a single query with array/CTE parameters) could eliminate the N+1 entirely, but requires changes to the sync processor calling pattern. This can be a follow-up.

**Estimated impact**: 50-80% reduction in sync matching query count. Each match becomes 1 lightweight query + 1 entity load (if matched), instead of 2-3 queries with full materialisation.

**Complexity**: Low-Moderate

**Files affected**:
- `src/JIM.PostgresData/Repositories/MetaverseRepository.cs`
- Tests in `test/JIM.Worker.Tests/`

---

### Phase 3: Bulk Write Operations

**Target**: Batch INSERT/UPDATE/DELETE methods across ConnectedSystemRepository and MetaverseRepository

**Problem**: EF Core's `AddRange`/`UpdateRange`/`RemoveRange` generate individual SQL statements per entity. For a batch of 500 CSOs, each with 10 attribute values, that's 5,500 individual INSERT/UPDATE/DELETE statements sent to PostgreSQL.

**Methods to optimise**:

| Method | Current Pattern | Proposed Pattern |
|--------|----------------|-----------------|
| `CreateConnectedSystemObjectsAsync` | AddRange (N INSERTs) | Npgsql binary COPY or multi-row INSERT |
| `UpdateConnectedSystemObjectsAsync` | UpdateRange (N UPDATEs, all columns) | Raw SQL batch UPDATE with unnest arrays |
| `CreatePendingExportsAsync` | AddRangeAsync (N INSERTs) | Npgsql binary COPY or multi-row INSERT |
| `DeletePendingExportsAsync` | RemoveRange (N DELETEs + cascades) | Raw SQL `DELETE WHERE Id = ANY(@ids)` |
| `UpdatePendingExportsAsync` | UpdateRange (N UPDATEs, all columns) | Raw SQL batch UPDATE (like existing `MarkPendingExportsAsExecutingAsync`) |

**Npgsql binary COPY** example for bulk inserts:
```csharp
await using var writer = await conn.BeginBinaryImportAsync(
    @"COPY ""ConnectedSystemObjects"" (""Id"", ""ConnectedSystemId"", ""TypeId"", ...) FROM STDIN (FORMAT BINARY)");
foreach (var cso in objects)
{
    await writer.StartRowAsync();
    await writer.WriteAsync(cso.Id, NpgsqlDbType.Uuid);
    await writer.WriteAsync(cso.ConnectedSystemId, NpgsqlDbType.Integer);
    // ...
}
await writer.CompleteAsync();
```

**Two-step approach for parent-child inserts**:
1. COPY parent records (CSOs) -- IDs are pre-generated GUIDs, no need to retrieve auto-generated keys
2. COPY child records (attribute values) -- reference parent by known GUID

**Complexity**: Moderate -- the entity graphs (CSO + AttributeValues, PendingExport + AttributeValueChanges) require two-step COPY operations. Type-specific value columns (StringValue, IntValue, GuidValue, etc.) need careful mapping.

**Estimated impact**: 5-20x faster bulk writes. The COPY protocol is PostgreSQL's fastest ingestion path.

**Files affected**:
- `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs`
- `src/JIM.PostgresData/Repositories/MetaverseRepository.cs` (batch create/update)
- Tests for all modified methods

---

### Phase 4: Optimise Sync Page Loading

**Target**: `GetConnectedSystemObjectsAsync` and `GetConnectedSystemObjectsModifiedSinceAsync`

**Problem**: These paginated queries load CSOs with deep Include chains for sync processing. Each page generates 7+ SQL queries via AsSplitQuery. Additionally, `GetConnectedSystemObjectsAsync` uses synchronous `.Count()` instead of `CountAsync()`.

**Quick wins** (low effort):
1. Fix synchronous `.Count()` to `CountAsync()` in `GetConnectedSystemObjectsAsync`
2. Audit the Include chains -- both `returnAttributes = true` and `false` branches currently load identical data, suggesting unnecessary includes in one branch

**Deeper optimisation** (moderate effort):
- Replace the 7+ split queries with 2-3 explicit raw SQL queries with manual mapping
- Load CSOs and their attribute values in separate queries, then stitch in C#
- Avoid loading reference value chains unless the sync processor actually needs them for the current operation

**Estimated impact**: 30-50% reduction in sync page loading time.

**Complexity**: Moderate -- the sync processor heavily depends on the materialised object graph shape. Changes here require careful validation that all navigation properties the processor accesses are still populated.

**Files affected**:
- `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs`
- Integration testing is essential for this phase

---

### Phase 5: RPEI Persistence Optimisation

**Target**: Activity `RunProfileExecutionItem` creation during sync

**Problem**: RPEIs are added to the `Activity.RunProfileExecutionItems` collection and persisted via `UpdateActivityAsync`, which calls `Repository.Database.Activities.Update(activity)`. For large sync runs (10,000+ objects), the change tracker must diff the entire RPEI collection (which grows throughout the sync run) to find new items on every `SaveChangesAsync` call.

**Proposed approach**:
- Add a dedicated `CreateRunProfileExecutionItemsAsync(List<ActivityRunProfileExecutionItem>)` method to the repository
- Use `AddRangeAsync` (or raw SQL bulk INSERT) to persist new RPEIs directly, bypassing the Activity entity's change tracker
- Clear the Activity's RPEI collection from the change tracker after each batch persist to prevent accumulation

**Estimated impact**: Moderate -- reduces change tracker pressure on large sync runs. The benefit grows with sync run size.

**Complexity**: Low-Moderate

**Files affected**:
- `src/JIM.PostgresData/Repositories/ActivitiesRepository.cs`
- `src/JIM.Data/Repositories/IActivityRepository.cs`
- Sync processor code that creates RPEIs

---

## Future Considerations

If the above phases don't yield sufficient improvement, or if we decide to push performance further afterwards, the following more ambitious approaches should be explored:

### A. Upgrade Lookup Index to Full Entity Cache

**Concept**: Phase 1 introduces a service-lifetime `IMemoryCache` storing a lookup index (external ID → CSO GUID). If the per-object PK load on cache hit (~1-2ms per object) still results in unacceptable import times, upgrade the cache to store full CSO entity graphs (`AsNoTracking`) instead of just IDs. This eliminates all database queries for cache hits entirely — lookups become pure in-memory O(1) operations.

**Advantages**:
- Eliminates all per-object database round-trips for CSO matching during import (zero DB queries on cache hit)
- Builds on the same `IMemoryCache` infrastructure from Phase 1 — only the cached value type changes from `Guid` to `ConnectedSystemObject`
- Memory usage is bounded by the connected system's object count (known up front)

**Trade-offs**:
- Memory consumption scales with connected system size (~10 KB per CSO with attribute values vs ~100 bytes for index-only)
- Entity lifecycle complexity: cached entities are detached from EF Core change tracking. Modification requires attaching to the current `DbContext`, which needs careful handling to avoid tracking conflicts
- Cache invalidation becomes more critical — a stale cached entity with outdated attribute values could cause incorrect delta detection
- Requires `AsNoTracking()` on load and explicit `Attach()`/`Update()` on modification

**Estimated memory (full entity cache)**:

| CSO Count | Est. Memory | Notes |
|-----------|-------------|-------|
| 10,000 | ~100 MB | Comfortable for most deployments |
| 50,000 | ~500 MB | Requires adequate Worker host resources |
| 100,000 | ~1 GB | Large but viable for enterprise deployments |

**When to consider**: If Phase 1's index-only approach still results in unacceptable import times due to per-object PK loads, particularly for very large connected systems (50k+ objects) where even fast indexed queries accumulate significant total time.

### B. Persistent In-Memory Model for the Worker Service

**Concept**: Adopt an in-memory processing model for the Worker service. When the Worker starts, establish and maintain a persistent in-memory cache of everything needed for run profile execution -- CSOs, MVOs, attribute values, sync rules, object types, etc. The cache stays warm across run profile executions, so the Worker is always ready to execute without per-run database loading. Database writes are batched and the cache is kept in sync with persisted changes.

This is a fundamentally different approach to the surgical per-query optimisations above. Rather than optimising individual database calls, it shifts the Worker to an in-memory processing model where the database becomes a persistence layer rather than the primary data source during execution. The architectural options for this will need exploring and analysing for suitability.

**Architectural options to explore**:
- Service-lifetime cache populated on startup and kept in sync via write-through updates
- Per-connected-system lazy-loaded cache segments (load on first access, retain for subsequent runs)
- Event-driven cache invalidation (e.g., when schema changes are made via the UI)
- Hybrid approach: lightweight index/lookup structures always in memory, full entity graphs loaded on demand

**Advantages**:
- Eliminates virtually all database read latency during processing
- Worker is always ready -- no per-run warm-up cost after initial startup
- Enables complex cross-object operations (matching, reference resolution, deduplication) without any database I/O
- Processing speed becomes CPU-bound rather than I/O-bound
- Simplifies processing code -- no async database calls in hot loops

**Trade-offs**:
- Memory consumption scales with total environment size across all connected systems
- Cache coherency with the database needs careful design (especially when the Web/API layer modifies configuration)
- Larger upfront engineering effort -- this is an architectural shift, not a surgical optimisation
- Requires thorough analysis of which data structures are appropriate and how much memory is realistic for target environment sizes (10k, 50k, 100k+ objects)
- Crash recovery needs consideration when state is in-memory (checkpointing or idempotent replay)

**Operator mode switch**: Consider exposing this as an administrator-configurable processing mode. JIM would support two modes:
- **Direct mode** (default) -- the Worker queries the database on demand during processing. Lower memory requirements, suitable for basic hosts or smaller datasets. Accepts slower processing times as a trade-off.
- **In-memory mode** -- the Worker maintains a persistent in-memory cache as described above. Requires hosts with sufficient memory for the dataset size, but delivers massively increased processing performance.

This gives customers the flexibility to run JIM on modest hardware in direct mode and accept the performance characteristics, or to provision appropriately resourced hosts and enable in-memory mode for large-scale environments. The mode switch could be a simple configuration option (admin UI or environment variable) that controls how the Worker's data access layer behaves, with the rest of the processing pipeline remaining identical.

**When to consider**: If the surgical optimisations in Phases 1-5 don't bring sync cycle times to acceptable levels, or if we decide to push further regardless. Requires a dedicated design phase to explore and analyse the architectural options for suitability.

---

## Implementation Approach

### Principles

1. **Measure first**: Profile each method before and after optimisation. Use `Stopwatch` or the existing `DiagnosticSource` infrastructure to capture baseline and improved timings
2. **One method at a time**: Each optimisation is independently deployable. Merge and test before moving to the next
3. **Repository interface unchanged**: Method signatures on `IConnectedSystemRepository`, `IMetaverseRepository`, etc. do not change. The optimisation is purely internal to the PostgreSQL implementation
4. **Unit test compatibility**: Raw SQL methods include try/catch fallbacks to EF Core for unit tests using in-memory contexts (following the established pattern from `MarkPendingExportsAsExecutingAsync`)
5. **Integration test validation**: Each phase must pass the full integration test suite before merge

### Raw SQL Conventions (Existing)

Follow the patterns already established in the codebase:
- `ExecuteSqlRawAsync` with parameterised queries (`{0}`, `{1}`)
- Double-quoted PostgreSQL identifiers (`""TableName""`, `""ColumnName""`)
- `ANY({n})` for array-based IN clauses
- Try/catch fallback to EF for mocked DbContext in unit tests
- Manual in-memory entity updates after raw SQL when entities remain tracked

### What About Dapper?

Dapper was considered but is **not recommended** for this plan:
- Adding a new ORM dependency for a subset of queries introduces a new pattern to learn and maintain
- EF Core's `FromSqlRaw` and `ExecuteSqlRawAsync` already provide raw SQL access
- Npgsql's `NpgsqlBinaryImporter` (for COPY) is available directly without Dapper
- The performance difference between Dapper and raw `Npgsql` is negligible

### What About PostgreSQL Functions (Stored Procedures)?

PostgreSQL functions/procedures were considered but are **not recommended** at this stage:
- Logic split between C# and PL/pgSQL makes debugging harder
- PostgreSQL functions are harder to unit test
- Migration versioning for functions adds complexity
- The performance benefit over parameterised raw SQL is marginal for our workload
- May be worth revisiting for very specific batch operations (e.g., atomic multi-table updates) if raw SQL proves insufficient

---

## Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Raw SQL introduces bugs in data mapping | High | Medium | Comprehensive unit tests + integration test suite |
| Change tracker inconsistency after raw SQL | High | Medium | Follow established pattern: manual entity updates after raw SQL |
| Unit tests break due to in-memory DB not supporting raw SQL | Medium | High | Try/catch fallback pattern (already proven in codebase) |
| Type-specific column mapping errors (StringValue vs IntValue vs GuidValue) | High | Low | Thorough test coverage per data type |
| Npgsql COPY requires direct connection access, not available through EF | Medium | Low | Access underlying `NpgsqlConnection` via `Database.GetDbConnection()` |
| Regression in sync integrity | Critical | Low | Full integration test suite must pass; phases are independently deployable |

---

## Success Criteria

| Metric | Current (Estimated) | Target |
|--------|-------------------|--------|
| Import 10k objects -- DB query count | ~30,000-40,000 | < 5,000 |
| Import 10k objects -- DB time | Baseline | -60% |
| Sync 10k objects -- matching query count | ~10,000+ | < 2,000 |
| Bulk CSO insert (500 objects) | ~5,500 SQL statements | 2 COPY operations |
| Sync page load (500 CSOs) | 7+ split queries | 2-3 queries |
| Full integration test suite | All pass | All pass (no regressions) |

---

## Implementation Order Rationale

The phases are ordered by **impact-to-effort ratio**:

1. **Phase 1** (N+1 import lookups) -- Highest impact, lowest risk. A simple query restructure eliminates the single largest source of database round-trips
2. **Phase 2** (Batch matching) -- Same N+1 pattern, same fix approach. Second highest query count reduction
3. **Phase 3** (Bulk writes) -- High impact, moderate effort. Uses Npgsql's COPY protocol for maximum throughput
4. **Phase 4** (Sync page loading) -- Moderate impact, requires careful Include chain analysis
5. **Phase 5** (RPEI persistence) -- Targeted optimisation for large sync runs

Each phase is independently valuable and can be shipped separately. Phase 1 should be implemented first as it has the highest impact and lowest complexity.

---

## Dependencies

- No new NuGet packages required -- all optimisations use existing Npgsql and EF Core capabilities
- `NpgsqlConnection` access for COPY operations is available via `DbContext.Database.GetDbConnection()`
- No schema changes required -- all optimisations are internal to the repository implementation
