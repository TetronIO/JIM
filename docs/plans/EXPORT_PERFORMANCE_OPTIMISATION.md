# Export Performance Optimisation

- **Status**: Planned
- **Milestone**: Post-MVP
- **Related**: `docs/plans/OUTBOUND_SYNC_DESIGN.md` (Q8 - Parallelism Decision)
- **Last Updated**: 2026-02-18

## Overview

Export operations to Connected Systems are significantly slower than they need to be. P rofiling of Scenario 8 (MediumLarge cross-domain entitlement sync) shows worker processes consuming minimal CPU and memory while exports take a long time to complete. The root cause is that the export pipeline is entirely I/O-bound - the system spends most of its time waiting for individual LDAP responses and individual database saves, with no pipelining or parallelism.

This plan addresses the performance bottlenecks identified in the export pipeline, progressing from low-risk quick wins to more complex parallel processing changes.

---

## Business Value

- **Faster sync cycles** - Reduced export times directly reduce end-to-end schedule completion time
- **Better resource utilisation** - Current exports leave CPU, memory, and network capacity idle
- **Scalability** - Enables JIM to handle larger environments without linear time increases
- **Customer confidence** - Faster, more predictable sync times improve operational trust

---

## Current Bottleneck Analysis

### Architecture Overview

```
+---------------------+     +------------------------+     +--------------------+
|  TaskingRepository  |---->| ExportExecutionServer  |---->| LdapConnector      |
|  (1 task at a time) |     | (batch of 100, serial) |     | (1 call at a time) |
+---------------------+     +------------------------+     +--------------------+
           |                            |                            |
       Sequential               Per-CSO DB saves             Synchronous LDAP
       task queue               after each batch             SendRequest() calls
```

### Bottlenecks by Severity

| # | Bottleneck | Location | Severity | Nature |
|---|-----------|----------|----------|--------|
| 1 | Synchronous per-object LDAP calls | `LdapConnectorExport.cs:82-115` | **Critical** | Each LDAP operation waits for response before the next starts |
| 2 | Per-CSO database round-trips after batch export | `ExportExecutionServer.cs:525-528` | **High** | Create exports trigger 2+ individual `SaveChangesAsync` per object |
| 3 | N+1 queries for deferred export reference resolution | `ExportExecutionServer.cs:358-397` | **High** | Individual DB query per unresolved member reference |
| 4 | Sequential worker task queue blocks all other tasks | `TaskingRepository.cs:98-121` | **Moderate** | Only one export task runs at a time; blocks imports and syncs |
| 5 | `MaxParallelism` option defined but never wired up | `ExportExecutionOptions.cs` | **Low** | Dead code - infrastructure exists but is unused |
| 6 | 2-second worker polling interval | `Worker.cs:137` | **Low** | Adds latency between task transitions |
| 7 | All pending exports eagerly loaded into memory | `ConnectedSystemRepository.cs:1387-1402` | **Low** | Large export queues cause heavy initial query cost |

### Why Resources Appear Idle

The multiple `dotnet JIM.Worker.dll` processes visible in htop are worker replicas or thread pool threads, but due to sequential task dispatch (Bottleneck 4), only one export task runs at a time. Within that task, the code is blocked waiting for:

1. Individual LDAP `SendRequest()` responses (~10-20ms each, thousands of objects)
2. Individual `SaveChangesAsync()` calls (~1-5ms each, multiple per object)

This is fundamentally an **I/O latency problem**, not a compute problem. The CPU is idle because the code never has more than one outstanding I/O operation.

---

## Implementation Phases

### Phase 1: Batch Database Operations (Low Risk, High Impact)

**Goal:** Eliminate per-object database round-trips after export execution.

**Changes:**

1. **Batch CSO updates after successful exports**
   - File: `ExportExecutionServer.cs` (`ProcessBatchSuccessAsync`)
   - Currently: Individual `UpdateCsoAfterSuccessfulExportAsync` per CSO with separate `SaveChangesAsync` calls
   - Proposed: Accumulate all CSO changes in-memory, then issue a single `SaveChangesAsync` for the entire batch
   - Pre-fetch all required attribute definitions in one query before processing the batch

2. **Bulk reference resolution for deferred exports**
   - File: `ExportExecutionServer.cs` (`TryResolveReferencesAsync`)
   - Currently: Individual `GetConnectedSystemObjectByMetaverseObjectIdAsync` per reference (N+1 pattern)
   - Proposed: Collect all unresolved MVO IDs, fetch all CSO mappings in a single query, then resolve in-memory

3. **Filter pending exports at the database level**
   - File: `ConnectedSystemRepository.cs` (`GetPendingExportsAsync`)
   - Currently: Loads all pending exports and filters in-memory
   - Proposed: Push filters (max retries exceeded, not yet due for retry) into the database query

**Estimated Impact:** 50-70% reduction in database round-trips per batch. Low risk as all changes are within the existing sequential flow.

**Testing Strategy:**
- Unit tests for batched update logic
- Integration test comparing export results before and after (same objects, same final state)
- Performance benchmark with 1,000+ pending exports

---

### Phase 2: LDAP Connector Pipelining (Moderate Risk, High Impact)

**Goal:** Send multiple LDAP requests without waiting for each individual response.

**Changes:**

1. **Async LDAP operations within a batch**
   - File: `LdapConnectorExport.cs`
   - Currently: Synchronous `_connection.SendRequest()` per object in a `foreach` loop
   - Proposed: Use `_connection.BeginSendRequest()` / async patterns to pipeline multiple LDAP operations
   - Implement a configurable concurrency limit (e.g., 4-8 concurrent requests) using `SemaphoreSlim`
   - Each concurrent request uses the same LDAP connection but leverages LDAP's native message-ID-based multiplexing

2. **Batch objectGUID retrieval for Create operations**
   - Currently: Individual `SearchRequest` after each `AddRequest` to fetch the objectGUID
   - Proposed: After all `AddRequest` operations in a batch complete, issue a single paged search for all newly created objects' GUIDs
   - Alternative: Use the `AddResponse` controls to extract the GUID if the directory server supports it

3. **Update `IConnectorExportUsingCalls` interface**
   - Currently: `List<ExportResult> Export(...)` - synchronous signature
   - Proposed: `Task<List<ExportResult>> ExportAsync(...)` - async signature
   - Maintain backwards compatibility with a default interface implementation that wraps the sync version

**Estimated Impact:** 3-8x improvement in LDAP export throughput depending on network latency and target server capacity.

**Risk Mitigations:**
- Configurable concurrency limit (start conservative at 4)
- Per-connector setting so administrators can tune per target system
- Error handling must ensure partial batch failures don't corrupt state
- Rate limiting to avoid overwhelming target LDAP servers

**Testing Strategy:**
- Unit tests with mock LDAP connection
- Integration tests against test AD/LDAP instances
- Verify no data corruption under concurrent writes
- Stress test with varying concurrency levels

---

### Phase 3: Wire Up MaxParallelism for Batch Processing (Moderate Risk, Moderate Impact)

**Goal:** Process multiple export batches concurrently within a single export run profile.

**Changes:**

1. **Implement parallel batch processing in ExportExecutionServer**
   - File: `ExportExecutionServer.cs`
   - Currently: Batches processed sequentially in a loop
   - Proposed: Process up to `MaxParallelism` batches concurrently
   - Each batch gets its own `DbContext` instance (EF Core is not thread-safe)
   - Each batch gets its own connector instance or uses connection pooling

2. **DbContext factory pattern**
   - Create a scoped `DbContext` per parallel batch to avoid thread-safety issues
   - Use `IDbContextFactory<JimDbContext>` (EF Core built-in) for creating per-batch contexts

3. **Feature flag control**
   - As designed in OUTBOUND_SYNC_DESIGN.md Q8, introduce behind a feature flag
   - Default: `MaxParallelism = 1` (sequential, current behaviour)
   - Configurable per Connected System via admin UI

**Estimated Impact:** Linear throughput improvement up to the configured parallelism level, multiplied by Phase 2 gains.

**Risk Mitigations:**
- Feature flag defaults to sequential (opt-in parallelism)
- Per-system configuration allows tuning for target system capacity
- Independent DbContext per batch eliminates EF Core thread-safety concerns
- Extensive integration testing before enabling

**Testing Strategy:**
- Unit tests for parallel batch orchestration
- Integration tests verifying data integrity under parallel execution
- Deadlock detection tests with concurrent database writes
- Performance benchmarks at various parallelism levels

---

### Phase 4: Parallel Task Execution for Independent Systems (Higher Risk, Moderate Impact)

**Goal:** Allow export tasks to different Connected Systems to run concurrently.

**Changes:**

1. **New execution mode for export tasks**
   - File: `SchedulerServer.cs`
   - Currently: All sync tasks created with `ExecutionMode = Sequential`
   - Proposed: Export tasks targeting different Connected Systems use `ExecutionMode = ParallelBySystem` (or similar)
   - Tasks to the same Connected System remain sequential to avoid conflicts

2. **Task queue partitioning**
   - File: `TaskingRepository.cs` (`GetNextWorkerTasksToProcessAsync`)
   - Currently: Returns only one task when it encounters a sequential task
   - Proposed: Return multiple export tasks if they target different Connected Systems
   - Maintain ordering guarantees within tasks for the same system

3. **Worker concurrency management**
   - File: `Worker.cs`
   - Ensure the worker can process multiple tasks concurrently
   - Each task gets its own DI scope (already the pattern for parallel mode)

**Estimated Impact:** Significant for schedules that export to multiple systems. A schedule exporting to 3 systems could complete in roughly 1/3 the time.

**Risk Mitigations:**
- Only parallelise across systems, never within the same system
- Maintain strict ordering for tasks targeting the same Connected System
- Thorough testing of concurrent worker task processing
- Gradual rollout: start with opt-in per schedule

**Testing Strategy:**
- Unit tests for task partitioning logic
- Integration tests with concurrent exports to multiple test systems
- Verify no cross-system data corruption
- Test scheduler step ordering is maintained

---

### Phase 5: Reduce Worker Polling Latency (Low Risk, Low Impact)

**Goal:** Minimise idle time between task transitions.

**Changes:**

1. **Adaptive polling interval**
   - File: `Worker.cs`
   - Currently: Fixed 2-second delay when no tasks available
   - Proposed: Use exponential backoff (100ms -> 200ms -> 400ms -> ... -> 2s) when idle, reset to minimum on task completion
   - When a task completes and more tasks are likely queued, poll immediately

2. **Optional: Database notification**
   - Use PostgreSQL `LISTEN/NOTIFY` to wake the worker when new tasks are queued
   - Eliminates polling entirely for task transitions
   - More complex but provides near-zero latency between steps

**Estimated Impact:** Saves 2-10 seconds per schedule (2s per step transition). Minor but improves perceived responsiveness.

**Testing Strategy:**
- Unit tests for backoff logic
- Integration test measuring step transition latency

---

## Success Criteria

| Metric | Current (Estimated) | Phase 1 Target | Phase 1+2 Target | All Phases Target |
|--------|-------------------|----------------|-------------------|-------------------|
| Export throughput (objects/sec) | ~50-100 | ~150-300 | ~500-800 | ~1000+ |
| DB round-trips per 100-object batch | ~200+ | ~5-10 | ~5-10 | ~5-10 |
| CPU utilisation during export | ~5-10% | ~10-15% | ~20-40% | ~40-70% |
| MediumLarge Scenario 8 export time | Baseline | -40% | -70% | -80% |

*Note: Exact numbers depend on network latency to target systems and database performance. Benchmarks should be established before Phase 1.*

---

## Benefits

- **Performance**: Dramatically faster export operations through better I/O utilisation
- **Scalability**: Handles larger environments without linear time growth
- **Resource efficiency**: Actually uses the CPU and memory allocated to worker containers
- **Operational**: Shorter maintenance windows for sync operations
- **Architecture**: Establishes patterns for connector-level parallelism that benefit all connector types

---

## Dependencies

- No new external packages required for Phases 1, 3, 4, 5
- Phase 2 depends on `System.DirectoryServices.Protocols` async support (available in .NET 9)
- Phase 3 requires `IDbContextFactory<JimDbContext>` registration in DI (Microsoft.EntityFrameworkCore built-in)

---

## Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Parallel LDAP operations cause target AD/LDAP server issues | High | Medium | Configurable concurrency limit per Connected System; start conservative |
| Parallel database writes cause deadlocks | High | Low | Independent DbContext per batch; proper transaction isolation |
| Data corruption from concurrent exports | Critical | Low | Feature flags default to sequential; extensive integration testing |
| EF Core thread-safety violations | High | Medium | DbContext factory pattern; never share contexts across threads |
| Regression in export correctness | High | Low | Comprehensive test suite; gradual rollout behind feature flags |
| LDAP connection pool exhaustion | Medium | Low | Connection pooling with configurable limits |

---

## Implementation Order Rationale

The phases are ordered by **risk-to-reward ratio**:

1. **Phase 1** (Batch DB) - Lowest risk, purely internal optimisation, no concurrency concerns
2. **Phase 2** (LDAP pipelining) - Moderate risk, highest single-phase impact, contained to connector layer
3. **Phase 3** (MaxParallelism) - Moderate risk, multiplies Phase 2 gains, uses proven EF Core factory pattern
4. **Phase 4** (Parallel tasks) - Higher risk, affects task scheduling, but enables multi-system concurrency
5. **Phase 5** (Polling) - Lowest impact, can be done at any time as an independent improvement

Each phase is independently valuable and can be shipped separately. Phase 1 should be implemented first as it has no concurrency risks and provides immediate measurable improvement.
