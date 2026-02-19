# Export Performance Optimisation

- **Status**: In Progress (Phase 4 complete, Phase 5 remaining)
- **Milestone**: Post-MVP
- **Related**: `docs/plans/OUTBOUND_SYNC_DESIGN.md` (Q8 - Parallelism Decision)
- **Last Updated**: 2026-02-19

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

### Phase 1: Batch Database Operations (Low Risk, High Impact) - COMPLETE

**Status:** Merged in PR #334. Measured ~50% reduction in export time on Scenario 8 MediumLarge.

**Goal:** Eliminate per-object database round-trips after export execution.

**Changes implemented:**

1. **Batch CSO updates after successful exports** - Accumulated all CSO changes in-memory with single `SaveChangesAsync` per batch
2. **Bulk reference resolution for deferred exports** - Collected all unresolved MVO IDs and fetched CSO mappings in a single query
3. **Filter pending exports at the database level** - Pushed retry/due filters into the database query

---

### Phase 2: LDAP Connector Pipelining (Moderate Risk, High Impact) - COMPLETE

**Status:** Complete. Integration tested via Scenarios 1, 2, 6, and 8.

**Goal:** Process N LDAP operations concurrently within each batch using configurable concurrency.

**Changes implemented:**

1. **Made export interfaces async (clean break)**
   - `IConnectorExportUsingCalls`: `Export()` -> `ExportAsync()` with `CancellationToken`
   - `IConnectorExportUsingFiles`: `Export()` -> `ExportAsync()` with `CancellationToken`
   - Updated all implementors (MockCallConnector, FileConnector, LdapConnector)
   - Updated all 3 call sites in `ExportExecutionServer.cs`

2. **Async LDAP operations with configurable concurrency**
   - New `LdapConnectionExtensions.SendRequestAsync()` wrapping APM `BeginSendRequest`/`EndSendRequest`
   - New `ILdapOperationExecutor` abstraction (LdapConnection is sealed, cannot be mocked)
   - `LdapConnectorExport` refactored with both sync (concurrency=1) and async (concurrency>1) paths
   - `SemaphoreSlim`-based concurrency control across exports within a batch
   - Container creation serialised via dedicated `SemaphoreSlim(1,1)` to prevent race conditions
   - Multi-step operations (create+GUID, rename+modify, UAC read+write) remain sequential within each export

3. **Per-connector concurrency setting**
   - New "Export Concurrency" integer setting under Export category
   - Default: 1 (sequential, safe default). Maximum: 16.
   - Recommended range 1-8, tuned per target system capacity

4. **Skipped batch objectGUID retrieval** - Per-export create+GUID is simpler, concurrent execution already overlaps the GUID fetches

**Key files:**
- `LdapConnectionExtensions.cs` (new) - APM async wrapper
- `ILdapOperationExecutor.cs` (new) - Testability abstraction
- `LdapOperationExecutor.cs` (new) - Production implementation
- `LdapConnectorExport.cs` (refactored) - Async + concurrency support
- `LdapConnectorExportAsyncTests.cs` (new) - 13 unit tests

**Estimated Impact:** 3-8x improvement in LDAP export throughput depending on network latency and concurrency setting.

---

### Phase 3: Wire Up MaxParallelism for Batch Processing (Moderate Risk, Moderate Impact) - COMPLETE

**Status:** Complete. Integration tested via Scenarios 1, 2, 6, and 8.

**Goal:** Process multiple export batches concurrently within a single export run profile.

**Changes implemented:**

1. **Parallel batch processing in ExportExecutionServer**
   - When `MaxParallelism > 1` and both factories are provided, batches are processed concurrently via `SemaphoreSlim` throttling + `Task.WhenAll`
   - When `MaxParallelism <= 1` (default), the existing sequential `foreach` loop runs unchanged
   - When only 1 batch exists, the sequential path is used regardless of `MaxParallelism`

2. **Per-batch DbContext and connector instances**
   - Each parallel batch creates its own `IRepository` via a `Func<IRepository>` factory delegate passed from the Worker
   - Each parallel batch (except batch 0) creates its own connector via a `Func<IConnector>` factory delegate
   - Batch 0 reuses the already-opened primary connector to avoid wasting the initial connection
   - Entities are re-loaded by ID from the batch's own context for proper change tracking

3. **Thread-safe result aggregation and progress reporting**
   - `ExportExecutionResult` counts aggregated under `lock` (low-contention)
   - Progress callback serialised via `SemaphoreSlim(1,1)` to protect the caller's shared DbContext
   - `Interlocked.Add` for the shared processed count

4. **MaxParallelism defaults to 1 (sequential)**
   - Safe default; admin explicitly opts in to parallelism
   - No `IDbContextFactory` registration needed; Worker creates contexts directly via `new JimDbContext()`

5. **Error isolation**
   - Each parallel batch wrapped in try/catch; one batch failure doesn't abort others
   - Cancellation token checked at batch start; `OperationCanceledException` propagated

**Key files:**
- `ExportExecutionServer.cs` (refactored) - `ProcessBatchesInParallelAsync`, repo-parameterised methods
- `ExportExecutionOptions.cs` (modified) - `MaxParallelism` default changed from 4 to 1
- `SyncExportTaskProcessor.cs` (modified) - Passes connector and repository factory delegates
- `IConnectedSystemRepository.cs` / `ConnectedSystemRepository.cs` - New `GetPendingExportsByIdsAsync`
- `ExportExecutionParallelBatchTests.cs` (new) - 10 parallel batch tests
- `ConnectedSystemRepositoryGetPendingExportsByIdsTests.cs` (new) - 7 repository tests

**Estimated Impact:** Linear throughput improvement up to the configured parallelism level, multiplied by Phase 2 gains.

---

### Phase 3b: MaxExportParallelism as Per-Connected System Setting - COMPLETE

**Status:** Implementation complete. MaxExportParallelism is now configurable per Connected System.

**Goal:** Make parallel batch export opt-in per Connected System, gated by a connector capability flag.

**Changes implemented:**

1. **`SupportsParallelExport` connector capability**
   - New `bool SupportsParallelExport` property on `IConnectorCapabilities` and `ConnectorDefinition`
   - LDAP Connector: `true` (supports concurrent connections)
   - File Connector: `false` (exclusive file locks prevent parallelism)
   - Capability synced from connector to DB via `SeedingServer` on startup

2. **`MaxExportParallelism` per-Connected System property**
   - Nullable `int?` on `ConnectedSystem` model (null/1 = sequential, 2-16 = parallel)
   - EF Core migration adds columns to both `ConnectorDefinitions` and `ConnectedSystems` tables

3. **Full API/PowerShell/UI coverage**
   - API: `UpdateConnectedSystemRequest.MaxExportParallelism` (Range 1-16), mapped in controller and response DTO
   - PowerShell: `Set-JIMConnectedSystem -MaxExportParallelism 4`
   - UI: "Export Performance" section in Settings tab, only visible when connector supports parallel export

4. **Worker wiring**
   - `SyncExportTaskProcessor` reads `_connectedSystem.MaxExportParallelism ?? 1` into `ExportExecutionOptions.MaxParallelism`

5. **Integration test support**
   - `-MaxExportParallelism` parameter added to Run-IntegrationTests.ps1 and all scenario scripts
   - Configured on target LDAP systems via `Set-JIMConnectedSystem` when value > 1

**Key files:**
- `IConnectorCapabilities.cs`, `ConnectorDefinition.cs` - New capability property
- `ConnectedSystem.cs` - New `MaxExportParallelism` property
- `SeedingServer.cs` - Capability sync
- `SynchronisationController.cs` - API update handler
- `ConnectedSystemDto.cs`, `ConnectedSystemRequestDtos.cs` - API DTOs
- `ConnectedSystemSettingsTab.razor` - UI setting
- `Set-JIMConnectedSystem.ps1` - PowerShell cmdlet
- `SyncExportTaskProcessor.cs` - Worker wiring
- `ConnectedSystemParallelExportTests.cs` (new) - 12 unit tests

---

### Phase 4: Parallel Task Execution for Schedule Steps (Higher Risk, Moderate Impact) - COMPLETE

**Status:** Complete. Integration tested via Scenario 6 (parallel timing validation confirms concurrent execution).

**Goal:** Allow schedule steps targeting different Connected Systems to execute concurrently within a parallel step group.

**Changes implemented:**

1. **Scheduler parallel group detection and queueing**
   - `SchedulerServer.QueueStepGroupAsync` detects when a step index contains multiple steps (parallel group)
   - Passes `isParallelGroup` flag through `QueueStepAsync` to `QueueRunProfileStepAsync`
   - Worker tasks created with `ExecutionMode = Parallel` for parallel groups, `Sequential` otherwise
   - Logging: `QueueStepGroupAsync: Step index N is a parallel group with M steps`

2. **Worker parallel task dispatch**
   - `Worker.ExecuteAsync` detects parallel task groups via `WorkerTaskExecutionMode.Parallel`
   - Collects contiguous parallel tasks and dispatches them concurrently via `Task.WhenAll`
   - Each parallel task gets its own DI scope for isolation
   - Sequential tasks continue to execute one at a time
   - Logging: `[PARALLEL execution]` suffix on task start/completion log entries

3. **Execution detail API fix for parallel steps**
   - `ScheduleExecutionsController.GetByIdAsync` now returns all sub-steps per step index (previously only returned first)
   - Activities and worker tasks matched to sub-steps by `ConnectedSystemId`
   - `ScheduleExecutionStepDto` extended with `ExecutionMode` and `ConnectedSystemId` fields
   - `CompletedAt` timestamp bug fixed: uses `activity.Executed + TotalActivityTime` (not `activity.Created`)

4. **Integration test parallel timing validation**
   - New `Assert-ParallelExecutionTiming` helper in `Test-Helpers.ps1`
   - Groups steps by stepIndex, identifies parallel groups (2+ steps)
   - Validates overlapping time ranges to confirm concurrent execution
   - Scenario 6 Test 6 (Parallel) now validates 3 parallel groups:
     - Step index 0: 4 concurrent Full Imports (all 4 systems)
     - Step index 5: 2 concurrent Exports (AD + Cross-Domain)
     - Step index 6: 2 concurrent Imports (Delta Import AD + Full Import Cross-Domain)

**Key files:**
- `SchedulerServer.cs` - Parallel group detection and `ExecutionMode` assignment
- `Worker.cs` - Parallel task dispatch with `Task.WhenAll`
- `ScheduleExecutionsController.cs` - Multi-step API response + CompletedAt fix
- `ScheduleExecutionDtos.cs` - `ExecutionMode` and `ConnectedSystemId` fields
- `SchedulerServerParallelExecutionTests.cs` (new) - 5 unit tests
- `Test-Helpers.ps1` - `Assert-ParallelExecutionTiming` function
- `Invoke-Scenario6-SchedulerService.ps1` - Parallel timing validation

**Estimated Impact:** Significant for schedules with parallel step groups. A 4-way parallel import completes in the time of a single import rather than 4x.

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
- Phase 3 uses simple `new JimDbContext()` factories (no DI registration needed)

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
