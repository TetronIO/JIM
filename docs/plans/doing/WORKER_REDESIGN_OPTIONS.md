# JIM.Worker Redesign - High-Level Design Options

- **Status:** Doing (Phase 1a complete, Phase 1b in progress — see [Progress Since Original Analysis](#progress-since-original-analysis))
- **Created**: 2026-02-23
- **Updated**: 2026-03-19
- **Author**: Architecture Review

## Context

JIM.Worker is the synchronisation engine - the beating heart of JIM. It processes imports from connected systems, reconciles identity data in the metaverse, evaluates exports, and writes changes back to connected systems. This document was originally written during an optimisation/debugging loop to evaluate whether incremental improvements were sufficient or whether a more fundamental redesign was warranted. Since then, significant performance work has been completed (see [Progress Since Original Analysis](#progress-since-original-analysis)).

### Current Architecture Summary

```
+----------------------------------------------+
|              JIM.Worker (Host)               |
|  BackgroundService polling loop (2s)         |
|  Service-lifetime IMemoryCache (CSO lookup)  |
|  Startup cache warming (all CSO ext. IDs)    |
|  Heartbeat + stale task crash recovery       |
|  One JimDbContext per main loop              |
|  One JimDbContext per spawned Task           |
+---------------------+------------------------+
                      |
                      v
+----------------------------------------------+
|         Task Processors (per work item)      |
| SyncImportTaskProcessor    ~2,700 LOC        |
| SyncTaskProcessorBase      ~3,150 LOC        |
| SyncFullSyncTaskProcessor    ~285 LOC        |
| SyncDeltaSyncTaskProcessor   ~265 LOC        |
| SyncExportTaskProcessor      ~395 LOC        |
| SyncRuleMappingProcessor     ~820 LOC        |
| SyncOutcomeBuilder            ~90 LOC        |
| Worker.cs (host)           ~1,015 LOC        |
+---------------------+------------------------+
                      |
                      v
+----------------------------------------------+
|  JimApplication (~185 LOC facade, 17 servers)|
| ConnectedSystemServer    4,255 LOC           |
| ExportEvaluationServer   1,863 LOC           |
| ExportExecutionServer    1,597 LOC           |
| MetaverseServer            651 LOC           |
| + 13 other servers + DriftDetectionService   |
+---------------------+------------------------+
                      |
                      v
+----------------------------------------------+
|       PostgresDataRepository (EF Core)       |
|  ~180 LOC facade + 13 sub-repositories       |
|  JimDbContext (single DbContext per scope)   |
|  + Raw SQL bulk ops on hot paths (#338)      |
+----------------------------------------------+
                      |
                      v
+----------------------------------------------+
|             PostgreSQL Database              |
+----------------------------------------------+
```

### Identified Pain Points

> **Note:** Pain points marked with **(Addressed by #338)** were partially or fully resolved by the Worker Database Performance Optimisation work. See [Progress Since Original Analysis](#progress-since-original-analysis) for details.

1. **EF Core is the bottleneck and the untestable seam**
   - ~~Change tracking overhead on hot paths (every `SaveChangesAsync` scans all tracked entities)~~ **(Addressed by #338)** — `SetAutoDetectChangesEnabled(false)` during flush sequences; RPEIs detached from tracker after bulk insert
   - ~~Include chains generate multi-round-trip split queries for deep object graphs~~ **(Addressed by #338)** — `AsSplitQuery` removed from sync page loading; replaced with two-query transaction approach
   - ~~`AddRange`/`UpdateRange` generate N individual SQL statements, not bulk operations~~ **(Addressed by #338)** — CSO creates/updates, pending export creates/updates/deletes, and RPEI persistence all use raw SQL bulk operations
   - In-memory provider masks missing `.Include()` bugs, making unit tests unreliable — **still a problem**
   - Cannot fully unit test repository logic - requires integration tests with real PostgreSQL — **still a problem**
   - **New concern (#338):** Production and test code paths now diverge via `_hasRawSqlSupport` flag and try/catch EF fallbacks — bugs can hide in raw SQL paths that unit tests don't exercise

2. **Sequential per-object processing** — **unchanged**
   - `ProcessConnectedSystemObjectAsync` processes one CSO at a time in a `foreach` loop
   - Join/project/attribute-flow is inherently per-object, but many surrounding operations could be batched or parallelised
   - Import processing is serial within a page (connector I/O is paginated but processing is sequential)
   - Export parallelism exists but is limited by single-DbContext progress reporting

3. **Tight coupling to JimApplication** — **largely unchanged**
   - `JimApplication` is a God Object facade with 17 server properties, each back-referencing the parent
   - Processors directly `new` up `JimApplication` and `PostgresDataRepository` instances
   - No dependency injection - prevents substitution for testing and makes parallelism unsafe
   - Each parallel task creates its own `JimApplication` + `JimDbContext` to avoid EF thread-safety issues
   - **Minor improvement (#338):** A shared `IMemoryCache` is now threaded through `JimApplication` constructors for the CSO lookup cache, but this is still manual wiring rather than proper DI

4. **Worker is monolithic** — **unchanged**
   - All work types (import, sync, export, example data generation, deletion, housekeeping) in a single process
   - Cannot scale horizontally - only one worker instance supported
   - No way to prioritise work (urgent manual sync vs. scheduled background sync)

5. **Testing is slow and fragile** — **unchanged (arguably slightly worse)**
   - Workflow tests use EF in-memory which auto-tracks navigation properties (masks real bugs)
   - Integration tests require full Docker stack, take minutes, and are flaky
   - No way to test sync logic without database (EF is deeply coupled throughout)
   - `TestUtilities.cs` is ~1,110 lines of setup helpers - indicating painful test setup
   - **New concern (#338):** Raw SQL bulk operations use try/catch fallback to EF in tests, meaning production hot paths are not exercised by unit tests at all
   - **Two incompatible test mocking approaches require two-tier fallback code (March 2026):**
     Unit tests use a mocked `DbContext` (Moq) where `Entry()`, `ChangeTracker`, and `TrackGraph()` are all null and throw `NullReferenceException` — these tests rely on `AddRange`/`UpdateRange` callbacks on mocked `DbSet<T>` instances. Workflow tests use EF Core's in-memory provider where `Entry()` and `ChangeTracker` work correctly, but `AddRange`/`UpdateRange` traverse navigation property graphs and cause identity conflicts when entities are tracked under multiple instances after `ClearChangeTracker()`. Production uses raw SQL with no EF tracking at all. The result is a **three-way code path divergence**: each EF fallback method now contains a try/catch that attempts `Entry().State` assignment first (for in-memory provider), catches `NullReferenceException` and falls back to `AddRange`/`UpdateRange` (for mocked DbContext), all inside an outer catch that handles the raw SQL failure (production path never exercises either fallback). This pattern now exists across **~17 catch blocks** in `ConnectedSystemRepository` (7), `ActivitiesRepository` (6), `MetaverseRepository` (1), and `PostgresDataRepository` base (3) — up from the original 7, driven by new raw SQL COPY operations for change history and RPEI outcome updates added since March 2026. The `ISyncRepository` interface proposed in Option A with a purpose-built in-memory `SyncRepository` implementation would eliminate this entirely — both unit and workflow tests would use the same clean in-memory implementation with no EF Core quirks leaking through

---

## Key Outcomes Required

| # | Outcome | Weight | Current State (post-#338) |
|---|---------|--------|---------------|
| 1 | **Data Integrity** | Critical | Good conceptually (state-based, re-assertable), but harder to prove due to diverging prod/test code paths |
| 2 | **Provability** | Critical | Weak - EF in-memory masks bugs; raw SQL hot paths not exercised by unit tests |
| 3 | **Scalability** | High | Limited - sequential processing, single worker, ~500k objects target |
| 4 | **Performance** | High | Improved ~34% (FullSync) by #338 bulk SQL; sequential processing remains the primary bottleneck |
| 5 | **Telemetry** | Medium | Basic Serilog + custom Diagnostics spans; no structured metrics/health |

---

## Option A: "Surgical Refactor" - Extract Pure Domain Engine + Optimised Data Access

- **GitHub Issue:** [#394](https://github.com/TetronIO/JIM/issues/394)

> **Status: Partially completed.** The bulk SQL operations described by the proposed `ISyncRepository` interface have been largely delivered by #338, but the interface itself has **not** been formalised — raw SQL is embedded in existing repository methods with try/catch EF fallback. The domain engine half (ISyncEngine extraction) has **not** been started. See [Progress Since Original Analysis](#progress-since-original-analysis).

### Philosophy

Keep the current architecture but surgically extract the sync processing logic into a pure, testable domain engine with an explicit data access boundary. **Eliminate EF Core from the worker entirely** — all worker data access moves to `ISyncRepository`, implemented with direct SQL/Npgsql for production and a purpose-built in-memory implementation for tests. The existing EF-powered repositories continue serving JIM.Web and JIM.Scheduler, where developer velocity matters more than raw performance. This is the evolutionary option.

**Key insight:** `ISyncRepository` is not just a performance optimisation — it is the clean separation point that allows the worker to maximise data integrity and throughput via direct SQL, while the rest of the application maximises developer velocity via EF Core. Each consumer gets the data access strategy that best fits its constraints.

### Architecture

```
+-------------------------------------------------------+
|          JIM.Worker (Host + Orchestration)            |
|                                                       |
|  BackgroundService + .NET Generic Host DI             |
|  IHostedService with IServiceScopeFactory             |
|                                                       |
|  Sync Orchestrator (new)                              |
|    Coordinates phases: Import -> Sync -> Export       |
|    Manages parallelism per phase                      |
|    Injected via DI, owns cancellation/progress        |
|                                                       |
|  Task Processors (existing, refactored)               |
|    SyncImportTaskProcessor                            |
|    SyncFullSyncTaskProcessor / SyncDeltaSyncTask...   |
|    SyncExportTaskProcessor                            |
|    Consume ISyncEngine + ISyncRepository via DI       |
+-------------------------------------------------------+
                          |
                          v
+-------------------------------------------------------+
|          JIM.Application (Business Logic)             |
|                                                       |
|  ISyncEngine interface (new - defined here)           |
|    ProcessCso() -> SyncDecision                       |
|    EvaluateExport() -> ExportDecision                 |
|    Pure domain logic - NO I/O dependencies            |
|    100% unit testable with plain C# objects           |
|                                                       |
|  SyncEngine implementation (new - lives here)         |
|    Extracted from SyncTaskProcessorBase +             |
|    SyncRuleMappingProcessor (~3,330 LOC)              |
|                                                       |
|  JimApplication facade (existing, unchanged)          |
|    Used by JIM.Web for admin/config operations        |
+-------------------------------------------------------+
                          |  produces decisions
                          v
+-------------------------------------------------------+
|         JIM.Data (Repository Interfaces)              |
|                                                       |
|  ISyncRepository interface (new - defined here)       |
|    GetCsoBatch() -> CsoBatchDto                       |
|    PersistSyncResults(decisions) -> void              |
|    BulkCreateCsos() / BulkUpdateCsos()                |
|                                                       |
|  Existing repository interfaces (unchanged)           |
|    IConnectedSystemRepository, IMetaverseRepository   |
+-------------------------------------------------------+
                          |
                          v
+-------------------------------------------------------+
|      JIM.PostgresData (Implementations)               |
|                                                       |
|  SyncRepository (new - implements ISyncRepository     |
|    using Npgsql bulk + raw SQL for ALL worker ops)    |
|                                                       |
|  Existing repositories (unchanged)                    |
|    EF Core retained for Web/Scheduler operations      |
+-------------------------------------------------------+
                          |
                          v
+-------------------------------------------------------+
|                    PostgreSQL                         |
|  Worker: Npgsql COPY / raw SQL (via SyncRepository)   |
|  Web/Scheduler: EF Core (via existing repositories)   |
+-------------------------------------------------------+

+-------------------------------------------------------+
|     JIM.InMemoryData (Test Implementation)            |
|                                                       |
|  SyncRepository (new - implements ISyncRepository     |
|   for unit/workflow tests)                            |
|  Purpose-built, no EF Core quirks                     |
|  References only JIM.Data + JIM.Models                |
|  No database dependencies whatsoever                  |
+-------------------------------------------------------+
```

**Tier placement summary:**

| Component | Tier | Rationale |
|-----------|------|-----------|
| Sync Orchestrator | JIM.Worker | Orchestration is a worker concern — coordinates phases, manages parallelism |
| Task Processors | JIM.Worker | Existing processors remain here, refactored to consume injected interfaces |
| ISyncEngine (interface) | JIM.Application | Domain logic interface belongs in the business logic layer |
| SyncEngine (implementation) | JIM.Application | Pure domain logic — join/project/flow decisions are business rules |
| ISyncRepository (interface) | JIM.Data | Data access interface, follows the same pattern as existing repository interfaces |
| SyncRepository | JIM.PostgresData | Production implementation using raw SQL/Npgsql — same tier as all other repository implementations |
| SyncRepository | JIM.InMemoryData (new project) | Test implementation — references only JIM.Data + JIM.Models, no database dependencies. Follows the same pattern as JIM.PostgresData: a standalone project that implements JIM.Data interfaces. Same class name, different namespace |

### Key Changes

1. **Extract `ISyncEngine`** (JIM.Application) - Pure domain logic, no I/O dependencies — **NOT STARTED**
   - Interface defined in JIM.Application; implementation lives alongside existing Servers
   - Takes in-memory objects (CSO batch, sync rules, MVO candidates)
   - Returns decisions/commands (JoinDecision, ProjectDecision, FlowDecision, ExportDecision)
   - Fully unit testable with plain objects - no mocking needed
   - The ~3,970 lines of SyncTaskProcessorBase + SyncRuleMappingProcessor become the engine

2. **Extract `ISyncRepository`** (JIM.Data / JIM.PostgresData) - Explicit data boundary for **all** worker data access — **PARTIALLY DONE (#338, interface defined #394)**
   - Interface defined in JIM.Data alongside existing repository interfaces (e.g., `IConnectedSystemRepository`) — **done** (`ISyncRepository.cs`)
   - `SyncRepository` in JIM.PostgresData for production - direct SQL/Npgsql for **all** worker operations, no EF Core — **partially done**: CSO creates/updates, pending export CRUD, RPEI persistence, RPEI outcome summary updates, change history (3 tables), and cross-batch reference fixup are already raw SQL; MVO creates/updates and remaining reads still use EF and must be migrated. Production adapter implementation **not started**
   - `SyncRepository` in a new JIM.InMemoryData project for tests - purpose-built, not EF's leaky in-memory provider, references only JIM.Data + JIM.Models with no database dependencies — **not done** (tests use EF fallback via try/catch). This is the **single highest-value change for test reliability**: it eliminates the three-way code path divergence (raw SQL / in-memory EF provider / mocked DbContext) that currently requires two-tier try/catch fallback logic across ~17 catch blocks in 4 repository files (see Pain Point 5)
   - **Goal: eliminate EF Core from the worker entirely.** Once `ISyncRepository` implementations exist, progressively migrate all remaining worker data access (MVO operations, reads, lookups) from EF to direct SQL within the PostgresData `SyncRepository`. The existing EF repositories (`IConnectedSystemRepository`, `IMetaverseRepository`, etc.) remain unchanged for JIM.Web and JIM.Scheduler
   - Batch-oriented API formalised as `ISyncRepository` interface — **done**: CSO reads/writes, MVO reads/writes, pending export CRUD, RPEI operations, sync rules/config, settings, change tracker management, CSO cache, cross-batch fixup, change history
   - CSO lookup cache via `IMemoryCache` eliminates N+1 import queries — **done**
   - Lightweight ID-only MVO matching with `Take(2)` — **done**
   - **Integration tests required:** Since the in-memory `SyncRepository` is purpose-built (not EF in-memory), the production `SyncRepository`'s direct SQL must be verified by integration tests against real PostgreSQL. This is actually a **better** situation than today — currently the raw SQL production paths are never tested at all (unit tests hit the EF fallback). See [Testing Strategy](#testing-strategy)

3. **Introduce DI throughout the worker** (JIM.Worker) — **NOT STARTED**
   - Replace `new JimApplication(new PostgresDataRepository(new JimDbContext()))` with `IServiceScopeFactory`
   - Task processors receive `ISyncEngine` and `ISyncRepository` via constructor injection
   - Each task gets a DI scope with properly scoped `DbContext`
   - Enables clean testing via service substitution

4. **Parallelise within sync phases** — **NOT STARTED**
   - Import: Multiple pages processed concurrently (each with own data access scope)
   - Sync: CSO batches processed in parallel (engine is stateless per-CSO, only shared state is MVO lookup)
   - Export: Already has parallelism; extend to use proper DI scopes

### What Stays the Same

- JimApplication facade (used by JIM.Web - not worth breaking)
- EF Core for JIM.Web and JIM.Scheduler (admin, config, scheduling, UI queries) — EF is the right choice here for developer velocity
- Worker as a single process (horizontal scaling not addressed)
- Database schema unchanged
- Task/Activity model unchanged

### Estimates

- **Completed (#338)**: Bulk SQL persistence for CSOs, pending exports, RPEIs; CSO lookup cache; lightweight MVO matching; `AsSplitQuery` elimination. Measured ~34% FullSync improvement, ~37% faster CSO processing
- **Remaining scope**: ISyncEngine extraction (~3,970 LOC to refactor), formal ISyncRepository interface, in-memory SyncRepository for tests, migrate remaining EF operations (MVO creates/updates, reads) to direct SQL in PostgresData SyncRepository, integration tests for PostgresData SyncRepository, DI introduction, intra-phase parallelism
- **Risk**: Medium - mechanical refactoring with clear seams; high test coverage before/after
- **Breaking Changes**: None externally; internal restructuring only

### Outcome Assessment

| Outcome | Rating | Notes |
|---------|--------|-------|
| Data Integrity | Good | Same logic, better tested. State-based re-assertion unchanged |
| Provability | Very Good | Pure engine is 100% testable. In-memory SyncRepository purpose-built. **Currently partially undermined by prod/test code path divergence** |
| Scalability | Moderate | Intra-process parallelism improved. Still single worker |
| Performance | Good | Bulk SQL on hot paths delivers ~34% improvement (#338). Full 2-5x requires ISyncEngine extraction + parallelism |
| Telemetry | Moderate | Can add OpenTelemetry to orchestrator/engine. No architectural support |

### Testing Strategy

The introduction of `ISyncRepository` fundamentally changes how worker code is tested. Instead of the current three-way code path divergence, testing splits cleanly into two tiers:

**Tier 1: Unit and Workflow Tests (fast, no database)**
- `ISyncEngine` tests use plain C# objects — no mocking, no database, no EF Core
- Worker/processor tests use the in-memory `SyncRepository` (JIM.InMemoryData) — purpose-built, deterministic, no EF quirks
- These tests run in milliseconds and cover all domain logic and orchestration
- The in-memory `SyncRepository` is a first-class implementation, not a mock — it maintains internal collections and enforces the same invariants as the production implementation (e.g., FK constraints, unique constraints)

**Tier 2: Integration Tests (real PostgreSQL)**
- The PostgresData `SyncRepository` (direct SQL/Npgsql) must be verified against real PostgreSQL
- These tests confirm that SQL queries, COPY binary imports, and bulk operations produce correct results
- Run against a Docker PostgreSQL instance (same as existing integration test infrastructure)
- Cover: bulk insert/update correctness, cross-batch reference fixup, COPY binary import round-trips, edge cases (nulls, empty batches, large batches, Unicode)
- **This is strictly better than today** — currently the production raw SQL paths are never tested (unit tests hit the EF fallback, not the production path)

**What gets deleted:**
- The `_hasRawSqlSupport` flag and all conditional branching based on it
- All ~17 two-tier try/catch fallback blocks across `ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`, and `PostgresDataRepository`
- The sync-hot-path methods in the existing EF repositories (those operations move entirely to `SyncRepository`)
- EF in-memory provider usage for worker tests (replaced by the purpose-built in-memory `SyncRepository`)
- Mocked `DbContext`/`DbSet` usage for worker tests (replaced by the purpose-built in-memory `SyncRepository`)

---

## Option B: "Pipeline Architecture" - Channel-Based Pipeline with In-Process Parallelism

### Philosophy

Redesign the worker as a pipeline of discrete stages connected by `System.Threading.Channels`. Each stage is independently parallelisable and testable. Shared state is managed via concurrent data structures rather than a shared database context. This is the modernisation option.

### Architecture

```
+-----------------------------------------------------------------------+
|                         JIM.Worker (Host)                             |
|  .NET Generic Host + OpenTelemetry + Health Checks                    |
+---------------------------------+-------------------------------------+
                                  |
                                  v
+-----------------------------------------------------------------------+
|                   Pipeline Coordinator                                |
|  Builds and connects pipeline stages per schedule step                |
|  Manages cancellation, progress, error aggregation                    |
|  Exposes health/metrics endpoints                                     |
+--------+------------------------+-------------------------+-----------+
         |                        |                         |
         | Channel<ImportBatch>   Channel<SyncBatch>        Channel<ExportBatch>
         v                        v                         v
+----------------+  +-----------------------------+  +------------------+
|  Import Stage  |  |       Sync Stage            |  |  Export Stage    |
|  (N readers)   |  | (M parallel processors)     |  |  (P writers)     |
|                |  |                             |  |                  |
| Connector.Read |  | For each CSO batch:         |  | Connector.Write  |
| -> Diff vs DB  |  |   Join/Project/Flow (pure)  |  | -> Confirm       |
| -> ImportBatch |  |   Batch MVO mutations       |  | -> ExportResult  |
|                |  |   Evaluate exports          |  |                  |
+-------+--------+  +------------+----------------+  +--------+---------+
        |                        |                            |
        v                        v                            v
+-----------------------------------------------------------------------+
|              Persistence Layer (batched writes)                       |
|  ISyncRepository interface                                            |
|  Implementations:                                                     |
|    SyncRepository (Npgsql bulk COPY + raw SQL)                        |
|    SyncRepository (for testing - in JIM.InMemoryData)                 |
|  All writes are batch-oriented: flush per page/stage completion       |
+--------------------------------+--------------------------------------+
                                 |
                                 v
+-----------------------------------------------------------------------+
|                         PostgreSQL                                    |
+-----------------------------------------------------------------------+

+-----------------------------------------------------------------------+
|                     Telemetry Sidecar                                 |
|  OpenTelemetry traces + metrics                                       |
|  .NET Health Checks (/healthz, /readyz)                               |
|  Prometheus metrics exporter                                          |
|  --> JIM Management UI dashboard or external (Aspire, Grafana)        |
+-----------------------------------------------------------------------+
```

### Key Changes

1. **Pipeline stages connected by Channels**
   - `Channel<ImportBatch>` between Import and Sync stages
   - `Channel<SyncBatch>` between Sync and Export Evaluation
   - `Channel<ExportBatch>` between Export Evaluation and Export Execution
   - Back-pressure built in (bounded channels prevent memory overflow)
   - Each stage can have N consumers for parallelism

2. **Import Stage**
   - N reader tasks per connector (configurable based on connector capability)
   - Connector pages stream into `Channel<ImportBatch>`
   - Diff calculation against existing CSOs done per batch
   - Memory-efficient: batches flow through pipeline, not accumulated

3. **Sync Stage (Pure Domain Engine)**
   - Same `ISyncEngine` concept as Option A (pure logic, no I/O)
   - M parallel workers consume from import channel
   - Shared MVO lookup via `ConcurrentDictionary` (populated at sync start, updated as projections occur)
   - Deferred reference resolution at stage boundary (same pattern as current, but explicit)
   - Each worker produces export evaluation commands into next channel

4. **Export Stage**
   - P parallel writers per connector (respecting `MaxExportParallelism`)
   - Batch-oriented: collect export commands, send in bulk to connectors
   - Confirmation tracking in-pipeline rather than needing separate confirming import

5. **Persistence Layer**
   - Same `ISyncRepository` interface concept as Option A
   - Writes batched and flushed at stage boundaries
   - Pipeline stages share no database context (each has own scope)

6. **Telemetry built in**
   - OpenTelemetry `ActivitySource` for distributed tracing (upgrade from current custom Diagnostics)
   - `Meter` for counters/histograms (objects/sec, batch duration, queue depth)
   - .NET Health Checks for worker liveness/readiness
   - Metrics endpoint consumable by JIM management UI or external systems

### What Stays the Same

- Database schema unchanged
- JimApplication facade (used by JIM.Web)
- Connector interfaces (IConnector, IConnectorImportUsingCalls, etc.)
- Activity/RPEI model for reporting

### New Dependencies

| Dependency | Purpose | Notes |
|------------|---------|-------|
| `System.Threading.Channels` | Pipeline communication | Built into .NET, no package needed |
| `OpenTelemetry` (.NET SDK) | Traces + Metrics | Microsoft-maintained, widely adopted |
| `OpenTelemetry.Exporter.Prometheus` | Metrics export | For Grafana/Aspire integration |
| `Microsoft.Extensions.Diagnostics.HealthChecks` | Health endpoints | Built into ASP.NET Core |

### Estimates

- **Scope**: Complete rewrite of worker orchestration + sync processor restructuring + new pipeline infrastructure
- **Risk**: Medium-High - significant structural change but concepts well-understood; .NET Channels are mature
- **Breaking Changes**: None externally; major internal restructuring

### Outcome Assessment

| Outcome | Rating | Notes |
|---------|--------|-------|
| Data Integrity | Very Good | Explicit batch boundaries. State re-assertable. Pipeline makes error propagation explicit |
| Provability | Excellent | Pure engine + pipeline stages are independently testable. In-memory repository for tests |
| Scalability | Very Good | Intra-process parallelism maximised. Each stage scales to available cores |
| Performance | Very Good | Pipeline overlap (import page N+1 while syncing page N). Bulk persistence. 3-10x improvement |
| Telemetry | Excellent | Native OpenTelemetry. Health checks. Structured metrics per pipeline stage |

---

## Option C: "Distributed Worker" - Event-Driven with Message Bus + Horizontal Scaling

### Philosophy

Decompose the worker into independent, horizontally scalable processing units communicating via a message bus. Each unit owns a narrow responsibility and can be scaled independently. This is the enterprise-scale option designed for the 500k+ object target.

### Architecture

```
+-------------------------------------------------------------------+
|                    JIM.Scheduler (existing)                       |
|  Publishes work items to message bus                              |
+--------------------------------+----------------------------------+
                                 |
                                 v (messages)
+-------------------------------------------------------------------+
|                          Message Bus                              |
|  Option: Redis Streams (air-gap friendly, no cloud dependency)    |
|  Queues: import-tasks, sync-batches, export-tasks                 |
|  Consumer groups for competing consumers                          |
+------------+-------------------+--------------------+-------------+
             |                   |                    |
             v                   v                    v
   +------------------+ +------------------+ +-------------------+
   | Import Workers   | | Sync Workers     | | Export Workers    |
   | (N instances)    | | (M instances)    | | (P instances)     |
   |                  | |                  | |                   |
   | - Connect to     | | - Consume CSO    | | - Consume PE      |
   |   external sys   | |   batches        | |   batches         |
   | - Diff + stage   | | - Pure sync      | | - Connect to      |
   | - Publish CSO    | |   engine         | |   target sys      |
   |   batches        | | - Publish PEs    | | - Write + confirm |
   | - Ack message    | | - Ack message    | | - Ack message     |
   +---------+--------+ +--------+---------+ +--------+----------+
             |                   |                    |
             v                   v                    v
+-------------------------------------------------------------------+
|                     Shared Persistence Layer                      |
|  PostgreSQL (bulk writes via Npgsql)                              |
|  Redis (optional: shared MVO lookup cache for sync workers)       |
+-------------------------------------------------------------------+

+-------------------------------------------------------------------+
|                       Telemetry + Health                          |
|  OpenTelemetry (traces span across workers via message headers)   |
|  Redis-backed health aggregation                                  |
|  JIM Management UI: worker fleet status, queue depths, throughput |
+-------------------------------------------------------------------+
```

### Key Changes

1. **Message bus for work distribution**
   - Redis Streams as the message bus (air-gap friendly, deployable on-premises)
   - Three queues: `import-tasks`, `sync-batches`, `export-tasks`
   - Consumer groups enable competing consumers (horizontal scaling)
   - At-least-once delivery with acknowledgement (state-based model handles re-processing)
   - *Note:* PostgreSQL `LISTEN/NOTIFY` was considered but rejected — it is fire-and-forget (no persistence), has no consumer group support, is limited to 8KB payloads, and provides no retry/dead-letter semantics. A PostgreSQL table queue with `SELECT ... FOR UPDATE SKIP LOCKED` would be more viable than `LISTEN/NOTIFY` if avoiding Redis, but lacks the push-based delivery and mature consumer group semantics of Redis Streams

2. **Independent worker types**
   - **Import Workers**: Connect to source systems, diff against DB, produce CSO change batches
   - **Sync Workers**: Consume CSO batches, run pure sync engine, produce pending exports
   - **Export Workers**: Consume pending export batches, write to target systems, confirm
   - Each type independently scalable (spin up more sync workers for large runs)

3. **Shared state via Redis (optional cache layer)**
   - MVO lookup cache in Redis for sync workers (avoid each worker loading full MVO set)
   - Sync rule cache in Redis (loaded once, shared across workers)
   - Database remains source of truth; Redis is a performance optimisation only
   - Falls back to direct DB queries if Redis unavailable
   - *CSO caching was considered but deferred* — unlike MVOs (single bounded set, read-heavy for join matching), CSOs scale per connected system (N systems x objects each), are primarily accessed during import as full-scan diffs where caching doesn't help, and their main cacheable use case (export target lookup) is not the bottleneck since connector I/O dominates export time. Revisit if export DB lookups prove costly at scale

4. **Work coordination**
   - Scheduler publishes import-task messages (one per connected system per run profile)
   - Import workers complete and publish sync-batch messages
   - Sync workers complete and export evaluation publishes export-task messages
   - Barrier synchronisation at phase boundaries via message counting/completion tokens

5. **Same pure sync engine**
   - Core sync logic identical to Options A/B (pure domain engine, no I/O)
   - Deployed within each sync worker container
   - Testability identical to Options A/B

### New Dependencies

| Dependency | Purpose | Notes |
|------------|---------|-------|
| **Redis** (server) | Message bus + optional cache | Self-hosted, air-gap friendly, single binary |
| `StackExchange.Redis` | Redis client | Microsoft-maintained, de facto standard |
| `OpenTelemetry` (.NET SDK) | Distributed tracing | Traces span across worker instances |
| `System.Threading.Channels` | Intra-process pipeline (within each worker) | Built into .NET |

### What Stays the Same

- Database schema unchanged (PostgreSQL remains source of truth)
- Connector interfaces
- Activity/RPEI reporting model
- JimApplication for JIM.Web (untouched)

### Deployment Model

```
# Minimal (current equivalent - single machine)
docker compose:
  - jim.web
  - jim.scheduler
  - jim.worker-import (1 instance)
  - jim.worker-sync (1 instance)
  - jim.worker-export (1 instance)
  - redis
  - postgres

# Scaled (large organisation)
docker compose / Kubernetes:
  - jim.web
  - jim.scheduler
  - jim.worker-import (2 instances)
  - jim.worker-sync (4 instances - CPU bound)
  - jim.worker-export (2 instances)
  - redis
  - postgres
```

### Estimates

- **Scope**: Complete worker rewrite + message bus infrastructure + deployment changes + new inter-process coordination
- **Risk**: High - introduces distributed systems complexity, eventual consistency, message ordering concerns
- **Breaking Changes**: Deployment model changes (Redis required). No API/schema changes

### Outcome Assessment

| Outcome | Rating | Notes |
|---------|--------|-------|
| Data Integrity | Good | State-based model handles re-processing. At-least-once delivery is safe. More failure modes to handle |
| Provability | Very Good | Same pure engine. Workers independently testable. But distributed integration testing is harder |
| Scalability | Excellent | Horizontal scaling per worker type. Add capacity where needed |
| Performance | Excellent | Parallelism across machines, not just cores. Pipeline overlap across phases |
| Telemetry | Excellent | Distributed tracing across worker fleet. Queue depth monitoring. Per-worker health |

---

## Comparative Analysis

### Side-by-Side Assessment

| Criterion | A: Surgical Refactor | B: Pipeline Architecture | C: Distributed Worker |
|-----------|---------------------|------------------------|--------------------|
| **Data Integrity** | Good | Very Good | Good |
| **Provability** | Very Good | Excellent | Very Good |
| **Scalability** | Moderate | Very Good | Excellent |
| **Performance** | Good (~34% done, 2-5x full) | Very Good (3-10x) | Excellent (10x+) |
| **Telemetry** | Moderate | Excellent | Excellent |
| Development effort | Lowest (partially done) | Medium | Highest |
| Risk | Low-Medium | Medium-High | High |
| Deployment change | None | None | Redis required |
| Code disruption | ~30% of worker | ~70% of worker | ~90% of worker |
| Air-gap compatible | Yes | Yes | Yes (Redis self-hosted) |
| Time to initial PR | Incremental | Needs critical mass | Needs critical mass |
| **Progress to date** | ~50% (persistence done, engine not started) | 0% | 0% |

### Recommendation Matrix by Organisation Size

| Org Size | Objects | Recommended Option |
|----------|---------|-------------------|
| Small-Medium | < 50k | A is sufficient |
| Large | 50k - 200k | B delivers required performance |
| Very Large | 200k - 500k+ | B or C depending on hardware constraints |

### Risk Analysis

**Option A Risks:**
- May not deliver sufficient performance for 500k+ orgs
- Incremental approach risks "death by a thousand cuts" - never quite getting to the clean architecture
- Existing EF coupling in non-hot-paths still limits testability

**Option B Risks:**
- Channels introduce complexity in error handling (what happens when a stage fails mid-pipeline?)
- Shared MVO `ConcurrentDictionary` becomes a contention point at very high parallelism
- Needs careful batch sizing to balance memory vs. throughput
- Ordering constraints on reference attributes require cross-batch coordination

**Option C Risks:**
- Distributed systems are inherently harder to debug and reason about
- Redis is a new infrastructure dependency (operational burden)
- Message ordering across workers requires careful design (e.g., sync worker must process CSO batch N before N+1 for same connected system)
- Phase barrier synchronisation is complex (knowing all import batches are done before sync starts)
- Integration testing of the distributed system requires more infrastructure

---

## Critical Design Decisions (All Options)

### D1: The ISyncEngine Must Be Pure

Regardless of option chosen, the sync engine (join/project/attribute-flow/export-evaluation logic) must be extracted as a pure domain service with **zero I/O dependencies**. This is the single highest-value change for provability.

```csharp
// Input: plain objects
public interface ISyncEngine
{
    SyncDecision ProcessCso(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> applicableSyncRules,
        MetaverseObject? existingMvo,
        IReadOnlyList<MetaverseObject> joinCandidates);

    ExportDecision EvaluateExports(
        MetaverseObject mvo,
        IReadOnlyList<MetaverseObjectAttributeValue> changedAttributes,
        IReadOnlyList<SyncRule> exportRules,
        IReadOnlyDictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> targetCsos);
}

// Output: decisions, not side effects
public record SyncDecision(
    SyncAction Action,           // Join, Project, AttributeFlow, Disconnect, NoChange
    Guid? TargetMvoId,           // MVO to join to (if joining)
    MetaverseObject? NewMvo,     // MVO to create (if projecting)
    List<AttributeFlowResult> AttributeFlows,
    List<ExportDecision> ExportDecisions);
```

This makes the entire sync engine testable with:
```csharp
[Test]
public void ProcessCso_WithMatchingJoinRule_ReturnsJoinDecision()
{
    var engine = new SyncEngine();
    var cso = CreateTestCso(employeeId: "EMP001");
    var mvo = CreateTestMvo(employeeId: "EMP001");
    var rules = CreateJoinRule(matchOn: "EmployeeId");

    var decision = engine.ProcessCso(cso, rules, existingMvo: null, joinCandidates: [mvo]);

    Assert.That(decision.Action, Is.EqualTo(SyncAction.Join));
    Assert.That(decision.TargetMvoId, Is.EqualTo(mvo.Id));
}
```

No database. No mocking. No flaky tests. Just logic.

### D2: Persistence Must Be Batch-Oriented

> **Partially implemented by #338.** The flush mechanism now bypasses EF change tracking for most hot-path writes (see status annotations below). However, the operations are embedded in existing repository methods with try/catch EF fallback for tests, rather than formalised behind a dedicated `ISyncRepository` interface. Formalising the interface remains a prerequisite for proper InMemory test implementations and the ISyncEngine extraction.
>
> **March 2026 update:** The lack of a formal interface is now causing concrete maintenance pain. Repository fallback paths require **two-tier try/catch** logic because unit tests (mocked `DbContext` where `Entry()`/`ChangeTracker` throw `NullReferenceException`) and workflow tests (EF in-memory provider where `AddRange`/`UpdateRange` cause identity conflicts via graph traversal) need fundamentally different EF Core call patterns. This now affects **~17 catch blocks across 4 repository files** (`ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`, `PostgresDataRepository` base) — grown from the original 7 due to new raw SQL COPY operations for change history and RPEI outcome updates. A purpose-built in-memory `SyncRepository` implementation would replace both approaches with a single, clean implementation that has no EF Core behaviour quirks.

All options should use batch persistence. The current pattern of accumulating changes and flushing at page boundaries is correct. But the flush mechanism needs to bypass EF change tracking:

```csharp
public interface ISyncRepository
{
    // Bulk operations - bypasses EF change tracking entirely
    Task BulkCreateCsosAsync(IReadOnlyList<ConnectedSystemObject> csos);                     // DONE (#338) - raw SQL
    Task BulkUpdateCsosAsync(IReadOnlyList<ConnectedSystemObject> csos);                     // DONE (#338) - raw SQL
    Task BulkDeleteCsosAsync(IReadOnlyList<Guid> csoIds);                                    // existing (ExecuteSqlRawAsync)
    Task BulkCreateMvosAsync(IReadOnlyList<MetaverseObject> mvos);                           // NOT DONE - migrate from EF to direct SQL
    Task BulkUpsertMvoAttributesAsync(IReadOnlyList<MetaverseObjectAttributeValue> values);  // NOT DONE - migrate from EF to direct SQL
    Task BulkCreatePendingExportsAsync(IReadOnlyList<PendingExport> exports);                // DONE (#338) - raw SQL
    Task BulkDeletePendingExportsAsync(IReadOnlyList<Guid> exportIds);                       // DONE (#338) - raw SQL
    Task BulkInsertRpeisAsync(IReadOnlyList<RunProfileExecutionItem> rpeis);                 // DONE (#338) - raw SQL (not in original spec)
    Task BulkUpdateRpeiOutcomeSummariesAsync(IReadOnlyList<RunProfileExecutionItem> rpeis);  // DONE (#398) - COPY binary import
    Task BulkInsertChangeHistoryAsync(IReadOnlyList<ConnectedSystemObjectChange> changes);   // DONE (#398) - COPY binary import
    Task BulkInsertChangeAttributesAsync(IReadOnlyList<ConnectedSystemObjectChangeAttribute> attrs);       // DONE (#398) - COPY binary import
    Task BulkInsertChangeAttributeValuesAsync(IReadOnlyList<ConnectedSystemObjectChangeAttributeValue> vals); // DONE (#398) - COPY binary import

    // Cross-batch reference fixup (new since #397-#409)
    Task FixupCrossBatchReferencesAsync(int connectedSystemId);                              // DONE (#397) - raw SQL JOIN + partial indexes

    // Reads - lightweight DTOs, no EF tracking
    Task<CsoBatch> GetCsoBatchAsync(int connectedSystemId, int page, int pageSize);          // PARTIALLY DONE - AsSplitQuery removed, uses two-query transaction
    Task<MvoLookup> GetMvoLookupForSyncAsync(int connectedSystemId);                         // DONE (#338) - lightweight ID-only matching with Take(2)
}
```

### D3: Telemetry Should Use OpenTelemetry

The current custom `Diagnostics` class is a good start but should be upgraded to standard OpenTelemetry:

- `ActivitySource` for traces (replaces custom span tracking)
- `Meter` for metrics (objects/sec, batch durations, error rates, queue depths)
- Health check endpoints for infrastructure monitoring
- Exportable to Prometheus, Jaeger, Aspire, or any OTLP-compatible backend
- JIM Management UI can consume metrics via gRPC/HTTP OTLP or Prometheus scraping

---

## Recommendation

**Option B (Pipeline Architecture)** offers the best balance of outcomes for JIM's current trajectory:

1. **It addresses all five outcomes well** - provability and performance are both "Very Good" or better
2. **It stays in-process** - avoids the distributed systems complexity of Option C while still delivering significant parallelism
3. **No new infrastructure dependencies** - Channels are built into .NET; OpenTelemetry is lightweight
4. **It's the right stepping stone** - if Option B proves insufficient for the largest deployments, the pure engine and batch persistence layer extracted in B make Option C a natural evolution (add message bus between stages that are already cleanly separated)
5. **The pipeline model matches the domain** - import -> sync -> export is inherently a pipeline; the code should reflect that
6. **It solves the testing problem** - the pure ISyncEngine + in-memory SyncRepository combination gives us the provability we need without EF in-memory's quirks

However, **Option A is the pragmatic starting point** if the team wants to de-risk incrementally. The ISyncEngine extraction (D1) and ISyncRepository boundary (D2) from Option A are prerequisites for Option B anyway. A phased approach would be:

- **Phase 1a** (Option A - persistence): Replace EF Core on hot paths with raw SQL bulk operations. **DONE (#338)** — ~34% FullSync improvement measured.
- **Phase 1b** (Option A - engine + data boundary): Extract ISyncEngine as a pure domain service. Formalise `ISyncRepository` interface. Build in-memory `SyncRepository` for tests. Migrate remaining EF operations (MVO creates/updates, reads) to direct SQL. Add integration tests for PostgresData `SyncRepository`. Eliminate the `_hasRawSqlSupport` flag and ~17 two-tier try/catch fallback blocks. Introduce DI in the worker. **NOT STARTED** — this is the remaining high-value work for provability and data access performance.
- **Phase 2** (Option B): Rewire orchestration to use Channels pipeline. Ship and validate.
- **Phase 3** (Option C, if needed): Add Redis message bus between pipeline stages for horizontal scaling.

Each phase delivers standalone value and can be assessed before committing to the next.

---

## Progress Since Original Analysis

### Worker Database Performance Optimisation (#338) - Completed

A 5-phase surgical optimisation programme was completed in February 2026, replacing EF Core hot-path operations with raw SQL and in-memory indexing. This work corresponds to the **data access half of Option A** but was implemented without formalising the `ISyncRepository` interface — raw SQL is embedded in existing repository methods with try/catch EF fallback for unit test compatibility.

**What was delivered:**

| Phase | Change | Impact |
|-------|--------|--------|
| 1 | Service-lifetime CSO lookup cache (`IMemoryCache`) with startup warming | Eliminates N+1 import lookup queries |
| 2 | Lightweight ID-only MVO matching with `Take(2)` (match-first, load-later) | Eliminates unnecessary full entity materialisation |
| 3 | Raw SQL bulk insert/update for CSOs, CSO attribute values, pending exports | Bypasses EF change tracking on write hot paths |
| 4 | Removed `AsSplitQuery` from sync page loading; two-query transaction approach | Eliminated materialisation bugs; removed ~170 lines of post-load SQL repair code |
| 5 | Raw SQL bulk insert for RPEIs with chunked parameterised queries | RPEIs detached from EF tracker after flush; prevents duplicate inserts |

**Measured results:** ~34% faster FullSync, ~37% faster CSO processing.

**EF Core bugs discovered and fixed during this work:**
1. `SaveChangesAsync` triggering `DetectChanges()` which discovered RPEIs via `Activity.RunProfileExecutionItems` and inserted them before bulk flush — fixed with `SetAutoDetectChangesEnabled(false)`
2. `DbSet.Update()` / `Database.Update()` traversing full object graph regardless of `AutoDetectChangesEnabled` — fixed with `Entry().State = Modified` (no graph traversal)
3. Raw SQL modifying rows still tracked in EF memory causing `DbUpdateConcurrencyException` — fixed with `ClearChangeTracker()` after MVO deletion pages

**What was NOT delivered (remains for Phase 1b):**
- ISyncEngine extraction (pure domain logic, no I/O)
- Formal `ISyncRepository` interface (all worker data access behind a single interface)
- In-memory `SyncRepository` for tests — eliminates the three-way code path divergence, the `_hasRawSqlSupport` flag, and the ~17 two-tier try/catch fallback blocks
- Migration of remaining EF operations to direct SQL in PostgresData `SyncRepository` (MVO creates/updates, remaining reads)
- Integration tests for PostgresData `SyncRepository` against real PostgreSQL
- DI introduction (still using manual `new` wiring)
- Intra-phase parallelism

**Full details:** See `docs/plans/done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md`

### RPEI Outcome Graph (#363) - Completed

Significant changes to how sync outcomes are recorded and reported on RPEIs. This work adds hierarchical outcome trees to RPEIs (replacing flat outcome types) and wires outcome building into all three processor phases. While this doesn't change the fundamental worker architecture, it increases processor LOC and introduces a new utility class.

**What was delivered:**

| Change | Impact |
|--------|--------|
| New `SyncOutcomeBuilder` utility class (~90 LOC) | Centralises RPEI outcome node creation and summary generation |
| Hierarchical outcome trees wired into import, sync, and export processors | RPEIs now carry structured parent-child outcome graphs |
| Activity stats derived from sync outcome graph (with RPEI fallback) | More accurate activity-level statistics |
| CSO display fields snapshotted on RPEIs | Historical preservation of CSO identity at time of processing |
| `ExportResult` renamed to `ConnectedSystemExportResult` | Clearer naming, avoids ambiguity with export outcome types |
| `ObjectChangeType.Provisioned` consolidated into `Exported` | Simplified outcome taxonomy |
| Merged Disconnected and CsoDeleted into single RPEI for obsoleted CSOs | Reduced RPEI volume for common obsolescence scenarios |
| Export confirmation outcomes (`ExportConfirmed`) | RPEIs now track successful export confirmations |

**Effect on this document:**
- Processor LOC figures have increased (SyncImportTaskProcessor +200, SyncTaskProcessorBase +280, SyncExportTaskProcessor +33)
- The existing `ExportResult` type (referenced in the Option B Export Stage diagram) was renamed to `ConnectedSystemExportResult`
- `ObjectChangeType.Provisioned` no longer exists (consolidated into `Exported`)
- The `SyncOutcomeBuilder` pattern demonstrates a small step toward extracting reusable sync logic into dedicated classes, though it is a utility, not the pure domain engine envisioned by ISyncEngine

**Full details:** See `docs/plans/done/RPEI_OUTCOME_GRAPH.md`

### Cross-Batch Reference Fixup (#397-#409) - Completed

When importing CSOs with inter-object references (e.g., LDAP groups with `member` DNs), references to objects in later batches cause foreign key constraint violations because the target CSO does not yet exist. This required a new multi-phase import pattern that was not anticipated in the original plan.

**What was delivered:**

| Change | Impact |
|--------|--------|
| CSO ID pre-generation before batch processing | All CSO IDs known upfront, enabling cross-batch FK resolution within a single import run |
| Deferred raw SQL JOIN fixup after all CSO creates complete | Resolves `ReferenceValueId` FKs that pointed to not-yet-persisted CSOs |
| Partial indexes on `ConnectedSystemObjectAttributeValue` for fixup query | Significant performance improvement for the fixup JOIN at scale |
| Command timeout extension and retry hardening | Robustness for large imports where fixup query can be slow |
| Skip optimisation when no unresolved references exist | Zero overhead for imports without inter-object references |
| Cross-page reference RPEI deduplication | Merge RPEIs instead of creating duplicates per batch boundary |

**Effect on this document:**
- SyncImportTaskProcessor LOC has increased (~+150 for reference fixup phases)
- This is a new sync-phase pattern that the ISyncEngine extraction must account for — reference fixup is I/O (raw SQL), not pure domain logic, so it belongs in `ISyncRepository` rather than `ISyncEngine`
- The `ISyncRepository` interface spec has been updated to include `FixupCrossBatchReferencesAsync()`
- Option B's pipeline architecture needs to consider reference fixup at import stage boundaries

### Change History Tracking (#398, various) - Completed

A new change history subsystem was added to capture attribute-level change records for CSOs, MVOs, and exports. This provides auditable change tracking and is persisted via raw SQL COPY binary import for performance.

**What was delivered:**

| Change | Impact |
|--------|--------|
| New models: `ConnectedSystemObjectChange`, `ConnectedSystemObjectChangeAttribute`, `ConnectedSystemObjectChangeAttributeValue` | Normalised change record hierarchy |
| New models: `MetaverseObjectChange` and corresponding attribute/value models | MVO-side change tracking |
| New utility: `ExportChangeHistoryBuilder` in JIM.Application | Builds change records from pending export data before PE deletion during export confirmation |
| COPY binary import for change history rows (3 tables) | High-performance bulk persistence, bypassing EF entirely |
| COPY binary import for RPEI outcome summary updates | Replaces per-RPEI UPDATE statements |
| Deleted object final attribute snapshots on change records | Preserves CSO/MVO state at point of deletion |
| Export change history capture and display | Full audit trail for exported attribute changes |

**Effect on this document:**
- SyncTaskProcessorBase has grown significantly (~+360 LOC for change history capture during sync phases)
- The `ISyncRepository` interface spec has been updated to include change history and RPEI outcome bulk operations
- The two-tier try/catch fallback count has grown from ~7 to ~17 catch blocks across 4 repository files, driven by these new COPY operations needing EF fallback paths for tests
- This further strengthens the case for an in-memory `SyncRepository` — every new raw SQL operation adds another fallback method that the purpose-built test implementation would eliminate
