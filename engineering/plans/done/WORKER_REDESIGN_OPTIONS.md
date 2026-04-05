# JIM.Worker Redesign - High-Level Design Options

- **Status:** Done (all phases complete; see [Progress Since Original Analysis](#progress-since-original-analysis))
- **Created**: 2026-02-23
- **Updated**: 2026-03-26
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
   - ~~Change tracking overhead on hot paths (every `SaveChangesAsync` scans all tracked entities)~~ **(Addressed by #338)**: `SetAutoDetectChangesEnabled(false)` during flush sequences; RPEIs detached from tracker after bulk insert
   - ~~Include chains generate multi-round-trip split queries for deep object graphs~~ **(Addressed by #338)**: `AsSplitQuery` removed from sync page loading; replaced with two-query transaction approach
   - ~~`AddRange`/`UpdateRange` generate N individual SQL statements, not bulk operations~~ **(Addressed by #338)**: CSO creates/updates, pending export creates/updates/deletes, and RPEI persistence all use raw SQL bulk operations
   - In-memory provider masks missing `.Include()` bugs, making unit tests unreliable; **still a problem**
   - Cannot fully unit test repository logic - requires integration tests with real PostgreSQL; **still a problem**
   - ~~**New concern (#338):** Production and test code paths now diverge via `_hasRawSqlSupport` flag and try/catch EF fallbacks~~ **(Resolved by #394 + Phase 8)**: `_hasRawSqlSupport` flag and all try/catch EF fallback blocks deleted. Worker tests use purpose-built in-memory `SyncRepository` (JIM.InMemoryData); production uses `PostgresData.SyncRepository` which owns Worker-only bulk SQL directly (partial classes) and delegates dual-called methods to shared EF repos

2. **Sequential per-object processing**: **unchanged**
   - `ProcessConnectedSystemObjectAsync` processes one CSO at a time in a `foreach` loop
   - Join/project/attribute-flow is inherently per-object, but many surrounding operations could be batched or parallelised
   - Import processing is serial within a page (connector I/O is paginated but processing is sequential)
   - Export parallelism exists but is limited by single-DbContext progress reporting

3. **Tight coupling to JimApplication**: **partially addressed (#422)**
   - `JimApplication` is a God Object facade with 17 server properties, each back-referencing the parent
   - ~~Processors directly `new` up `JimApplication` and `PostgresDataRepository` instances~~ **(Addressed by #422)**: full DI registration in Worker and Scheduler; `IJimApplicationFactory`/`IConnectorFactory` introduced; manual `new` calls removed from `Worker.cs` and `Scheduler.cs`
   - ~~No dependency injection~~ **(Addressed by #422)**: .NET Generic Host DI throughout Worker and Scheduler
   - Each parallel task creates its own `JimApplication` + `JimDbContext` via `IJimApplicationFactory` to avoid EF thread-safety issues; this is correct by design
   - `JimApplication` facade itself remains a God Object; restructuring it is deferred

4. **Worker is monolithic**: **unchanged**
   - All work types (import, sync, export, example data generation, deletion, housekeeping) in a single process
   - Cannot scale horizontally - only one worker instance supported
   - No way to prioritise work (urgent manual sync vs. scheduled background sync)

5. **Testing is slow and fragile**: **unchanged (arguably slightly worse)**
   - Workflow tests use EF in-memory which auto-tracks navigation properties (masks real bugs)
   - Integration tests require full Docker stack, take minutes, and are flaky
   - No way to test sync logic without database (EF is deeply coupled throughout)
   - `TestUtilities.cs` is ~1,110 lines of setup helpers - indicating painful test setup
   - ~~**New concern (#338):** Raw SQL bulk operations use try/catch fallback to EF in tests~~ **(Resolved by #394)**: all ~32 try/catch EF fallback blocks removed (-642 lines). Worker tests use `InMemoryData.SyncRepository`; production uses `PostgresData.SyncRepository`
   - ~~**Two incompatible test mocking approaches require two-tier fallback code (March 2026)**~~ **(Resolved by #394)**: the three-way code path divergence (mocked DbContext / EF in-memory / raw SQL) has been eliminated. All ~1,276 Worker and Workflow tests migrated to the purpose-built `InMemoryData.SyncRepository`. Mocked `DbContext`/`DbSet` usage for worker tests deleted. The ~17 catch blocks across 4 repository files are gone

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

> **Status: Complete.** All phases done: data boundary (`ISyncRepository`), DI, test migration, EF fallback removal, `SyncRepository` wiring, hot-path migration, ISyncEngine extraction, and intra-phase parallelism (Phase 10).

### What's Left

All items complete.

| # | Item | Scope | Value | Status |
|---|------|-------|-------|--------|
| 1 | ~~**ISyncEngine extraction** (Phase 1c)~~ | ~~Extract ~2,040 LOC of pure domain logic into a stateless engine with zero I/O.~~ | ~~**Provability**: makes core sync logic independently testable with plain objects.~~ | **Done** |
| 2 | ~~**Intra-phase parallelism** (Phase 10)~~ | ~~Multi-connection write parallelism for CSO creates, RPEIs, and sync outcomes using COPY binary protocol.~~ | ~~**Performance**: utilises multiple CPU cores during bulk write phases.~~ | **Done** (#430) |

**Everything else is done:**
- ISyncRepository interface (~80 methods); done
- In-memory SyncRepository for tests (86 tests); done
- All Worker/Workflow tests migrated (~1,276 tests); done
- EF fallback blocks removed (-642 lines); done
- DI in Worker and Scheduler; done
- ISyncServer domain logic boundary; done
- PostgresData.SyncRepository wired into all apps; done
- Hot-path bulk SQL migrated into SyncRepository partial classes; done
- Dead wrappers/interfaces cleaned from shared repos (-1,200+ lines); done
- Shared helpers deduplicated (BulkSqlHelpers.cs); done

### Phase 1c Implementation Plan

#### Revised Scope (from code analysis, March 2026)

The original estimate of ~3,970 LOC assumed the entire `SyncTaskProcessorBase` (3,155 LOC) + `SyncRuleMappingProcessor` (821 LOC) would move. After detailed method-by-method classification, the actual breakdown is:

| Source | Pure domain (moves to engine) | Orchestration/I/O (stays in processor) |
|--------|-------------------------------|---------------------------------------|
| `SyncRuleMappingProcessor` | ~821 LOC (entire file, already static) | 0 |
| `SyncTaskProcessorBase` pure methods | ~1,220 LOC | ~1,935 LOC |
| New result types + interface | ~200 LOC (new) | -- |
| **Total** | **~2,240 LOC** | ~1,935 LOC stays |

#### The Natural Seam

The boundary between engine and orchestrator is: **"deciding what changes should happen to in-memory objects"** vs **"persisting those changes and wiring activity/outcome tracking"**.

**Engine decides (no I/O, no async, plain objects in, decisions out):**
- Scope evaluation: is this CSO in scope for each import sync rule?
- Join evaluation: given candidates and existing join count, should this CSO join an MVO?
- Projection: should a new MVO be created for this CSO?
- Inbound attribute flow: what MVO attribute values change?
- Pending export confirmation: does the CSO's current state confirm a pending export?
- Obsolete CSO handling: disconnect, recall attributes, determine MVO deletion fate
- Drift detection and corrective export evaluation
- MVO deletion rule evaluation

**Orchestrator does (I/O, RPEI wiring, batching):**
- Loading CSO pages from the database
- Querying for MVO join candidates (DB)
- Querying for existing join counts (DB)
- Loading CSO attributes for scoping (DB)
- Creating RPEIs and wiring sync outcome trees
- Batching MVO creates/updates, PE creates/deletes
- Cross-page reference resolution (DB reload)
- Progress reporting, cancellation, diagnostics spans
- Change history recording

#### The Key Challenge: I/O Trapped Inside Pure Logic

Several methods in `SyncTaskProcessorBase` that contain pure decision logic also have I/O calls embedded within them:

| Mixed method | Embedded I/O | Solution |
|-------------|-------------|----------|
| `AttemptJoinAsync` | `FindMatchingMvoForJoinAsync` (DB query for candidates), `GetConnectedSystemObjectCountByMvoAsync` (DB) | Orchestrator pre-loads join candidates and existing join counts, passes to engine |
| `GetInScopeImportRulesAsync` | `GetConnectedSystemObjectAsync` (loads CSO attributes for scoping) | Orchestrator ensures CSO attributes are loaded before calling engine |
| `ProcessObsoleteConnectedSystemObjectAsync` | `GetConnectedSystemObjectCountByMetaverseObjectIdAsync` (DB) | Orchestrator pre-loads remaining CSO counts, passes to engine |
| `HandleCsoOutOfScopeAsync` | Same as above + `UpdateMetaverseObjectAsync` | Engine returns decision; orchestrator persists |
| `ProcessMvoDeletionRuleAsync` | `MarkMvoForDeletionAsync` -> `UpdateMetaverseObjectAsync` | Engine returns `MvoDeletionDecision`; orchestrator applies |
| `EvaluateOutboundExportsAsync` | `ISyncServer` methods that internally query DB | Engine receives pre-loaded export evaluation cache |

**Pattern: pre-load and pass-in.** The orchestrator loads all data the engine needs upfront and passes it as parameters. The engine returns decisions; the orchestrator persists them.

#### Proposed ISyncEngine Interface

```csharp
public interface ISyncEngine
{
    // Scope
    ScopeDecision DetermineScope(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> importSyncRules);

    // Join
    JoinDecision EvaluateJoin(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> syncRules,
        IReadOnlyList<ObjectMatchingRule> matchingRules,
        MetaverseObject? joinCandidate,
        int existingJoinCount);

    // Projection
    ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> syncRules);

    // Inbound attribute flow
    AttributeFlowResult FlowInboundAttributes(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        SyncRule syncRule,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        bool skipReferenceAttributes);

    // Pending export confirmation
    PendingExportConfirmationResult EvaluatePendingExportConfirmation(
        ConnectedSystemObject cso,
        Dictionary<Guid, PendingExport> pendingExports);

    // Obsolete CSO handling
    ObsoleteDecision EvaluateObsoleteCso(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        IReadOnlyList<SyncRule> syncRules,
        int remainingCsoCount);

    // MVO deletion rule
    MvoDeletionDecision EvaluateMvoDeletionRule(
        MetaverseObject mvo,
        int disconnectingConnectedSystemId,
        int remainingCsoCount);

    // Apply pending attribute changes (pure mutation)
    void ApplyPendingAttributeChanges(MetaverseObject mvo);

    // Out-of-scope action
    OutOfScopeAction DetermineOutOfScopeAction(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> syncRules);
}
```

Each method is synchronous (no `Task`), takes plain objects, returns decision records. The orchestrator interprets decisions and handles I/O.

#### Implementation Phases

**Phase 1c-1: Interface + result types**: ✅ DONE
- `ISyncEngine` interface in `src/JIM.Application/Interfaces/` (9 methods, all synchronous)
- Decision result types in `src/JIM.Models/Sync/`: `ScopeDecision`, `JoinDecision`, `ProjectionDecision`, `MvoDeletionDecision`, `ObsoleteDecision`, `PendingExportConfirmationResult`
- `MvoDeletionFate` enum moved from `JIM.Worker` to `JIM.Models/Sync/SyncEnums.cs`
- Scoping evaluation intentionally omitted from ISyncEngine; already pure in `ISyncServer/ScopingEvaluationServer`

**Phase 1c-2: Extract SyncEngine implementation**: ✅ DONE
- `SyncEngine` class in `src/JIM.Application/Servers/SyncEngine.cs` (partial class)
- `SyncEngine.AttributeFlow.cs`: `SyncRuleMappingProcessor` logic moved into engine as internal methods (Option 1)
- Pure methods extracted: `EvaluateJoin`, `EvaluateProjection`, `EvaluateMvoDeletionRule`, `EvaluatePendingExportConfirmation`, `ApplyPendingAttributeChanges`, `DetermineOutOfScopeAction`, `AttributeValuesMatch`, `FlowInboundAttributes`
- `JIM.Application.csproj` gained `JIM.Utilities` project reference for `AreByteArraysTheSame`

**Phase 1c-3: Refactor processors to use ISyncEngine**: ✅ DONE
- `ISyncEngine` injected via constructor into `SyncTaskProcessorBase` → `SyncFullSyncTaskProcessor` / `SyncDeltaSyncTaskProcessor`
- 7 processor methods now delegate to `_syncEngine`:
  - `ProcessInboundAttributeFlow` → `_syncEngine.FlowInboundAttributes`
  - `ApplyPendingMetaverseObjectAttributeChanges` → `_syncEngine.ApplyPendingAttributeChanges`
  - `AttemptProjection` → `_syncEngine.EvaluateProjection` + orchestrator applies decision
  - `ProcessPendingExport` → `_syncEngine.EvaluatePendingExportConfirmation` + orchestrator batches
  - `ProcessMvoDeletionRuleAsync` → `_syncEngine.EvaluateMvoDeletionRule` + orchestrator persists
  - `DetermineInboundOutOfScopeAction` → `_syncEngine.DetermineOutOfScopeAction`
  - `AttributeValuesMatch` → `_syncEngine.AttributeValuesMatch`
- `Worker.cs` creates `SyncEngine()` and passes to processors
- 85 test constructor call sites updated across 5 test files
- All 2,195 existing tests pass at each step

**Phase 1c-4: Pure unit tests for SyncEngine**: ✅ DONE
- 39 pure unit tests in `test/JIM.Worker.Tests/SyncEngineTests/`
- No mocking, no database, no EF Core; plain C# objects only
- Coverage: join evaluation (4 tests), projection (4 tests), deletion rules (11 tests), attribute flow (5 tests), out-of-scope action (5 tests), pending export confirmation (10 tests)
- Total tests: 2,234 (39 new + 2,195 existing)

### Philosophy

Keep the current architecture but surgically extract the sync processing logic into a pure, testable domain engine with an explicit data access boundary. **Eliminate EF Core from the worker entirely**: all worker data access moves to `ISyncRepository`, implemented with direct SQL/Npgsql for production and a purpose-built in-memory implementation for tests. The existing EF-powered repositories continue serving JIM.Web and JIM.Scheduler, where developer velocity matters more than raw performance. This is the evolutionary option.

**Key insight:** `ISyncRepository` is not just a performance optimisation; it is the clean separation point that allows the worker to maximise data integrity and throughput via direct SQL, while the rest of the application maximises developer velocity via EF Core. Each consumer gets the data access strategy that best fits its constraints.

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
|  SyncRepository (implements ISyncRepository)          |
|    Owns Worker-only bulk SQL (partial classes)        |
|    Delegates dual-called methods to shared repos      |
|                                                       |
|  Shared repositories (reduced scope)                  |
|    EF Core for Web/Scheduler + dual-called methods    |
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
| Sync Orchestrator | JIM.Worker | Orchestration is a worker concern; coordinates phases, manages parallelism |
| Task Processors | JIM.Worker | Existing processors remain here, refactored to consume injected interfaces |
| ISyncEngine (interface) | JIM.Application | Domain logic interface belongs in the business logic layer |
| SyncEngine (implementation) | JIM.Application | Pure domain logic; join/project/flow decisions are business rules |
| ISyncRepository (interface) | JIM.Data | Data access interface, follows the same pattern as existing repository interfaces |
| SyncRepository | JIM.PostgresData | Production implementation using raw SQL/Npgsql; same tier as all other repository implementations |
| SyncRepository | JIM.InMemoryData (new project) | Test implementation; references only JIM.Data + JIM.Models, no database dependencies. Follows the same pattern as JIM.PostgresData: a standalone project that implements JIM.Data interfaces. Same class name, different namespace |

### Key Changes

1. **Extract `ISyncEngine`** (JIM.Application) - Pure domain logic, no I/O dependencies; **NOT STARTED**
   - Interface defined in JIM.Application; implementation lives alongside existing Servers
   - Takes in-memory objects (CSO batch, sync rules, MVO candidates)
   - Returns decisions/commands (JoinDecision, ProjectDecision, FlowDecision, ExportDecision)
   - Fully unit testable with plain objects - no mocking needed
   - The ~3,970 lines of SyncTaskProcessorBase + SyncRuleMappingProcessor become the engine

2. **Extract `ISyncRepository`** (JIM.Data / JIM.PostgresData / JIM.InMemoryData) - Explicit data boundary for all worker data access; **DONE (#394 Phases 1-8, #428)**
   - Interface defined in JIM.Data; **done** (`ISyncRepository.cs`, ~80 methods, pure data access)
   - `ISyncServer` extracted for domain logic boundary; **done** (settings, caching, object matching, change tracking, connector triad ops)
   - `SyncRepository` in JIM.InMemoryData for tests; **done** (86 tests, purpose-built, no EF Core quirks)
   - All Worker and Workflow tests migrated from mocked DbContext to InMemoryData.SyncRepository; **done** (~1,276 tests)
   - All ~32 try/catch EF fallback blocks removed from repository files; **done** (-642 lines)
   - `PostgresData.SyncRepository`: **done** (#428 + Phase 8). Wired into all apps via DI. Worker-only bulk SQL implementations owned directly via partial classes (`SyncRepository.RpeiOperations.cs`, `SyncRepository.CsOperations.cs`). Dual-called methods remain as delegates to shared EF repos. Dead code removed from shared repos (-1,200+ lines total)
   - CSO lookup cache via `IMemoryCache` eliminates N+1 import queries; **done**
   - Lightweight ID-only MVO matching with `Take(2)`: **done**
   - Functional indexes for cross-batch reference fixup; **done** (#428, 300s→6s at MediumLarge scale)
   - Cross-batch change record reference resolution; **done** (#428, ~78% of RPEI references were permanently unresolved)
   - **Integration tests:** Scenario 1 and Scenario 8 MediumLarge pass against real PostgreSQL

   **Data Access Vision (revised March 2026):**

   Two data-access paths, optimised for different purposes; with no code duplication between them:

   - **Shared EF Core repositories** (`ConnectedSystemRepository`, `MetaverseRepository`, etc.); used by the Web UI, API, and for generic reads/writes. Over time, individual methods can be swapped from EF LINQ to raw SQL where EF quirks cause issues; benefiting all callers.
   - **`PostgresData.SyncRepository`**: used exclusively by the Worker. Exposes the full `ISyncRepository` interface (~80 methods). Generic reads (counts, single-record lookups, sync rules, settings) delegate to the shared EF repos. Hot-path bulk/batch operations (~15-20 methods) should own their implementations directly using Npgsql COPY binary imports and raw SQL.
   - **Current state (Phase 8 complete):** `SyncRepository` owns all Worker-only bulk SQL directly via partial classes. 12 public methods + 11 private helpers moved from `ActivitiesRepository` and `ConnectedSystemRepository`. Dual-called methods (CSO CRUD, PE update/delete, mark-executing) remain as delegates to shared EF repos. Dead wrappers removed from `ActivityServer` and `ConnectedSystemServer`. Shared helpers (`NullableParam`, `ChunkList`, `MaxParametersPerStatement`) deduplicated into `BulkSqlHelpers.cs`.
   - **Key principle:** `SyncRepository` does NOT duplicate methods from the shared repositories. Only Worker-only methods were moved; dual-called methods stay as delegates.

3. **Introduce DI throughout the worker** (JIM.Worker); **DONE (#422)**
   - Full DI registration in Worker and Scheduler `Program.cs`
   - `IJimApplicationFactory` / `JimApplicationFactory` in `JIM.Application`
   - `IConnectorFactory` / `ConnectorFactory` in `JIM.Connectors`
   - Worker.cs and Scheduler.cs: constructor injection, manual `new` calls removed

4. **Parallelise within sync phases**: **DONE (#430)**
   - `ParallelBatchWriter` component splits bulk writes across N concurrent `NpgsqlConnection` instances
   - CSO bulk creates use parallel COPY binary (import phase)
   - RPEI and sync outcome inserts use parallel COPY binary (import + sync flush)
   - Outcomes partitioned by RPEI to preserve self-referencing FK tree integrity
   - Configurable via `JIM_WRITE_PARALLELISM` env var (defaults to `Environment.ProcessorCount`, minimum 2)
   - **Remaining bottleneck:** MVO creates/updates still use EF `AddRange`/`SaveChangesAsync`: converting to COPY binary tracked as Phase 6 on #338
   - **Critical insight (PostgreSQL write architecture):** PostgreSQL uses a process-per-connection model; a single SQL statement (INSERT/UPDATE/DELETE) always executes in one process on one core. Parallel query only applies to SELECT operations. Multi-connection write parallelism (splitting bulk writes across N concurrent connections) is the primary lever for utilising multiple cores during the "Saving changes" phase. See [PostgreSQL Write Parallelism](#postgresql-write-parallelism) below for details.

### What Stays the Same

- JimApplication facade (used by JIM.Web - not worth breaking)
- EF Core for JIM.Web and JIM.Scheduler (admin, config, scheduling, UI queries); EF is the right choice here for developer velocity
- Worker as a single process (horizontal scaling not addressed)
- Database schema unchanged
- Task/Activity model unchanged

### Estimates

- **Completed (#338)**: Bulk SQL persistence for CSOs, pending exports, RPEIs; CSO lookup cache; lightweight MVO matching; `AsSplitQuery` elimination. Measured ~34% FullSync improvement, ~37% faster CSO processing
- **Completed (#430)**: Intra-phase parallelism; `ParallelBatchWriter` + COPY binary for CSO creates, RPEIs, sync outcomes
- **Risk**: Medium - mechanical refactoring with clear seams; high test coverage before/after
- **Breaking Changes**: None externally; internal restructuring only

### Outcome Assessment

| Outcome | Rating | Notes |
|---------|--------|-------|
| Data Integrity | Good | Same logic, better tested. State-based re-assertion unchanged |
| Provability | Very Good | Pure engine is 100% testable. In-memory SyncRepository purpose-built and deployed (~1,276 tests). Prod/test code path divergence eliminated (#394) |
| Scalability | Moderate | Intra-process parallelism implemented. Still single worker |
| Performance | Good | Bulk SQL on hot paths delivers ~34% improvement (#338). Parallel multi-connection COPY binary writes for CSOs, RPEIs, outcomes (#430). MVO persistence remains EF-based; COPY binary conversion tracked as #338 Phase 6 |
| Telemetry | Moderate | Can add OpenTelemetry to orchestrator/engine. No architectural support |

### Testing Strategy

The introduction of `ISyncRepository` fundamentally changes how worker code is tested. Instead of the current three-way code path divergence, testing splits cleanly into two tiers:

**Tier 1: Unit and Workflow Tests (fast, no database)**
- `ISyncEngine` tests use plain C# objects; no mocking, no database, no EF Core
- Worker/processor tests use the in-memory `SyncRepository` (JIM.InMemoryData); purpose-built, deterministic, no EF quirks
- These tests run in milliseconds and cover all domain logic and orchestration
- The in-memory `SyncRepository` is a first-class implementation, not a mock; it maintains internal collections and enforces the same invariants as the production implementation (e.g., FK constraints, unique constraints)

**Tier 2: Integration Tests (real PostgreSQL)**
- The PostgresData `SyncRepository` (direct SQL/Npgsql) must be verified against real PostgreSQL
- These tests confirm that SQL queries, COPY binary imports, and bulk operations produce correct results
- Run against a Docker PostgreSQL instance (same as existing integration test infrastructure)
- Cover: bulk insert/update correctness, cross-batch reference fixup, COPY binary import round-trips, edge cases (nulls, empty batches, large batches, Unicode)
- **This is strictly better than today**: currently the production raw SQL paths are never tested (unit tests hit the EF fallback, not the production path)

**What gets deleted:**
- ~~The `_hasRawSqlSupport` flag and all conditional branching based on it~~; **done** (#394)
- ~~All ~17 two-tier try/catch fallback blocks across `ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`, and `PostgresDataRepository`~~; **done** (#394, -642 lines)
- ~~The sync-hot-path bulk SQL methods in `ConnectedSystemRepository` and `ActivitiesRepository` (those implementations move into `PostgresData.SyncRepository`)~~; **done** (Phase 8: 12 public + 11 private methods moved, dead code removed from shared repos)
- ~~EF in-memory provider usage for worker tests (replaced by the purpose-built in-memory `SyncRepository`)~~; **done** (#394)
- ~~Mocked `DbContext`/`DbSet` usage for worker tests (replaced by the purpose-built in-memory `SyncRepository`)~~; **done** (#394)

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
   - *Note:* PostgreSQL `LISTEN/NOTIFY` was considered but rejected; it is fire-and-forget (no persistence), has no consumer group support, is limited to 8KB payloads, and provides no retry/dead-letter semantics. A PostgreSQL table queue with `SELECT ... FOR UPDATE SKIP LOCKED` would be more viable than `LISTEN/NOTIFY` if avoiding Redis, but lacks the push-based delivery and mature consumer group semantics of Redis Streams

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
   - *CSO caching was considered but deferred*; unlike MVOs (single bounded set, read-heavy for join matching), CSOs scale per connected system (N systems x objects each), are primarily accessed during import as full-scan diffs where caching doesn't help, and their main cacheable use case (export target lookup) is not the bottleneck since connector I/O dominates export time. Revisit if export DB lookups prove costly at scale

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
| **Progress to date** | **100%** (all phases complete: data boundary + DI + SyncRepository + hot-path migration + ISyncEngine + intra-phase parallelism) | 0% | 0% |

### Recommendation Matrix by Organisation Size

| Org Size | Objects | Recommended Option |
|----------|---------|-------------------|
| Small-Medium | < 50k | A is sufficient |
| Large | 50k - 200k | B delivers required performance |
| Very Large | 200k - 500k+ | B or C depending on hardware constraints |

### Empirical Memory Ceiling (April 2026)

> **Finding:** Scenario 8 (cross-domain entitlement sync) XLarge template (100K users, 50 groups) OOM-killed the worker during Full Sync on a Docker Desktop VM with 15.8 GB RAM. The worker successfully processed all 100,050 objects (projections + attribute flows) but crashed during the post-page-processing phase (pending export flush / cross-page reference resolution).

**Root causes (two major memory consumers):**

1. **EF change tracker accumulation across pages:** CSOs are loaded with `AsSplitQuery()` (not `AsNoTracking()`) because EF relationship fixup requires tracking. Each page adds ~500 CSOs + MVOs + attribute values, and the change tracker is only cleared conditionally (when MVO deletions occur). After 200 pages, the tracker holds ~100K entity graphs.

2. **Export evaluation cache:** `BuildExportEvaluationCacheAsync` (ExportEvaluationServer.cs) loads ALL target system CSOs and ALL their attribute values upfront before the sync loop starts. For Scenario 8, the target system also holds ~100K objects; doubling the memory footprint.

**This confirms the recommendation matrix above.** Option A's single-DbContext-per-sync-run architecture has a practical memory ceiling. Targeted fixes (clearing the change tracker between pages, chunking the export cache) could raise the ceiling somewhat, but the fundamental accumulation pattern remains. Option B's pipeline architecture, where each stage has its own scope and bounded channels provide back-pressure, is the correct long-term solution for deployments exceeding ~50K objects.

**Tested scale results (Scenario 8, all on Option A):**

| Template | Objects | RAM Available | Result |
|----------|---------|--------------|--------|
| Medium | ~1,100 | 15.8 GB | Passes (~9s Full Sync) |
| Large | ~10,500 | 15.8 GB | Not yet tested |
| XLarge | ~100,050 | 15.8 GB | **OOM kill** during Full Sync |

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

## PostgreSQL Write Parallelism

> **Added March 2026**: observed during integration testing with 5,000+ object imports.

During the "Saving changes" phase of large imports, PostgreSQL sits at 100% CPU on a single core while other cores are idle. This is **expected behaviour**, not a misconfiguration; it is a fundamental consequence of PostgreSQL's process-per-connection architecture.

### Why PostgreSQL Uses One Core Per Write

PostgreSQL assigns one OS process per connection. A single SQL statement (INSERT, UPDATE, DELETE) executes entirely within that one process on one core. PostgreSQL's parallel query feature (introduced in v9.6) only applies to **read operations**: sequential scans, hash joins, and aggregates. It does **not** parallelise writes.

The single-core bottleneck during bulk writes comes from:
- **WAL (write-ahead log) generation**: serialised by design for crash safety
- **Index updates**: each row insert/update must update all indexes on the table
- **MVCC overhead**: creating new tuple versions
- **fsync/flush**: ensuring durability

No amount of PostgreSQL configuration tuning (shared_buffers, work_mem, max_parallel_workers, etc.) will change this for write workloads.

### Application-Side Levers

Since the constraint is architectural (one core per connection for writes), the performance levers are on the application side:

| Lever | Phase | Impact | Notes |
|-------|-------|--------|-------|
| **Multi-connection write parallelism** | Phase 10 | Very High | Split bulk write batches across N concurrent `NpgsqlDataSource` connections. Each connection gets its own postgres process and CPU core. This directly addresses the observed single-core bottleneck. |
| **COPY binary import for all bulk writes** | Phase 8 | High | `NpgsqlBinaryImporter` (COPY protocol) is dramatically faster than parameterised INSERT for bulk loads. Already used for change history and RPEI outcomes (#398); extend to CSO creates/updates and MVO operations. |
| **Larger batch sizes** | Phase 8 | Medium | EF Core's default max batch size for PostgreSQL is 42 statements. Direct SQL in `PostgresData.SyncRepository` should use larger batches (or COPY) to reduce round-trip overhead. |
| **Deferred constraint checking** | Phase 8 | Medium | Making FK constraints `DEFERRABLE INITIALLY DEFERRED` moves validation to commit time, reducing per-row overhead during bulk operations where referential integrity is guaranteed by application logic. |
| **Unlogged staging tables** | Phase 8 | High (staging only) | `UNLOGGED` tables skip WAL entirely, giving massive write speedup. Applicable if staging/import tables don't need crash recovery; the application already re-imports on failure, so durability of in-flight staging data may not be critical. |

### Implications for Phase 8 and Phase 10

**Phase 8 (`PostgresData.SyncRepository`)**: **DONE.** Worker-only bulk SQL implementations have been migrated from shared repositories into `SyncRepository` partial classes. Future optimisation (e.g., converting remaining parameterised INSERT methods to COPY binary) can be done incrementally within `SyncRepository` without affecting shared repos.

**Phase 10 (intra-phase parallelism)**: **DONE (#430).** Multi-connection write parallelism implemented via `ParallelBatchWriter`. CSO creates, RPEIs, and sync outcomes now use COPY binary across N concurrent connections. Integration testing confirmed parallel connections are opened and utilised. MVO persistence remains the dominant single-core bottleneck; converting to COPY binary is tracked as #338 Phase 6.

---

## Critical Design Decisions (All Options)

### D1: The ISyncEngine Must Be Pure

Regardless of option chosen, the sync engine (join/project/attribute-flow/export-evaluation logic) must be extracted as a pure domain service with **zero I/O dependencies**. This is the single highest-value change for provability.

> **March 2026 update:** After detailed method-by-method analysis, the original coarse two-method interface (`ProcessCso`, `EvaluateExports`) has been replaced with a finer-grained interface that mirrors the actual decision steps in the sync pipeline. The revised interface is in the [Phase 1c Implementation Plan](#phase-1c-implementation-plan) above. Key changes: separate methods for scope/join/project/flow/obsolete/export-confirmation, each taking pre-loaded data and returning specific decision types. All methods are synchronous (no `Task`).

This makes the entire sync engine testable with plain objects:
```csharp
[Test]
public void EvaluateJoin_WithMatchingCandidate_ReturnsJoinToMvo()
{
    var engine = new SyncEngine();
    var cso = CreateTestCso(employeeId: "EMP001");
    var mvo = CreateTestMvo(employeeId: "EMP001");
    var rules = CreateJoinRules(matchOn: "EmployeeId");

    var decision = engine.EvaluateJoin(cso, rules, matchingRules, joinCandidate: mvo, existingJoinCount: 0);

    Assert.That(decision.Action, Is.EqualTo(JoinAction.JoinToExisting));
    Assert.That(decision.TargetMvoId, Is.EqualTo(mvo.Id));
}
```

No database. No mocking. No flaky tests. Just logic. See [Phase 1c Implementation Plan](#phase-1c-implementation-plan) for the full revised interface design.

### D2: Persistence Must Be Batch-Oriented

> **Implemented by #338 + #394 + #428 + Phase 8.** The flush mechanism bypasses EF change tracking for all hot-path writes. `ISyncRepository` is formalised, in-memory `SyncRepository` for tests is complete (~1,276 tests migrated), all try/catch EF fallback blocks deleted (-642 lines). `PostgresData.SyncRepository` owns Worker-only bulk SQL directly via partial classes; dual-called methods delegate to shared EF repos. The ISyncEngine extraction is complete (Phase 1c); 9 synchronous methods, 39 pure unit tests.

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

- **Phase 1a** (Option A - persistence): Replace EF Core on hot paths with raw SQL bulk operations. **DONE (#338)**: ~34% FullSync improvement measured.
- **Phase 1b** (Option A - data boundary + DI): Formalise `ISyncRepository` interface. Build in-memory `SyncRepository` for tests. Migrate all tests. Eliminate ~32 try/catch fallback blocks. Introduce DI in Worker and Scheduler. Wire `PostgresData.SyncRepository` into all apps. **DONE (#394 Phases 1-8, #422, #424, #425, #428).**
- **Phase 8** (Option A - hot-path migration): Migrate Worker-only bulk SQL implementations from shared EF repositories into `PostgresData.SyncRepository`. **DONE.** 12 public methods + 11 private helpers moved into partial class files. Shared helpers deduplicated into `BulkSqlHelpers.cs`. Dead wrappers and interface members cleaned up from `ActivityServer`, `ConnectedSystemServer`, `IActivityRepository`, `IConnectedSystemRepository`. Net -1,200+ lines from shared repos.
- **Phase 1c** (Option A - engine extraction): Extract ISyncEngine as a pure domain service. **DONE.** 9 synchronous methods, zero I/O. SyncRuleMappingProcessor (821 LOC) absorbed. 7 processor methods delegate to engine. 39 new pure unit tests.
- **Phase 10** (Option A - parallelism): Multi-connection write parallelism with COPY binary. **DONE (#430).** `ParallelBatchWriter` splits bulk writes across N connections. CSO creates, RPEIs, sync outcomes use COPY binary. 15 unit tests. MVO COPY binary conversion deferred to #338 Phase 6.
- **Phase 2** (Option B): Rewire orchestration to use Channels pipeline. Ship and validate.
- **Phase 3** (Option C, if needed): Add Redis message bus between pipeline stages for horizontal scaling.

Each phase delivers standalone value and can be assessed before committing to the next.

---

## Progress Since Original Analysis

### Worker Database Performance Optimisation (#338) - Completed

A 5-phase surgical optimisation programme was completed in February 2026, replacing EF Core hot-path operations with raw SQL and in-memory indexing. This work corresponds to the **data access half of Option A** but was implemented without formalising the `ISyncRepository` interface; raw SQL is embedded in existing repository methods with try/catch EF fallback for unit test compatibility.

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
1. `SaveChangesAsync` triggering `DetectChanges()` which discovered RPEIs via `Activity.RunProfileExecutionItems` and inserted them before bulk flush; fixed with `SetAutoDetectChangesEnabled(false)`
2. `DbSet.Update()` / `Database.Update()` traversing full object graph regardless of `AutoDetectChangesEnabled`: fixed with `Entry().State = Modified` (no graph traversal)
3. Raw SQL modifying rows still tracked in EF memory causing `DbUpdateConcurrencyException`: fixed with `ClearChangeTracker()` after MVO deletion pages

**What was NOT delivered (remains for Phases 1b/1c):**
- ~~ISyncEngine extraction (pure domain logic, no I/O)~~; **done** (Phase 1c). 9 methods, 39 pure tests, SyncRuleMappingProcessor absorbed
- ~~Formal `ISyncRepository` interface~~; **done** (#394 Phase 1)
- ~~In-memory `SyncRepository` for tests~~; **done** (#394 Phase 3, 86 tests)
- ~~Test migration from mocked DbContext to InMemoryData~~; **done** (#394 Phase 7b, ~1,276 tests)
- ~~Elimination of try/catch fallback blocks~~; **done** (#394 Phase 7d, -642 lines)
- ~~DI introduction~~; **done** (#422)
- ~~`PostgresData.SyncRepository` wiring + hot-path migration~~; **done** (#428 + Phase 8). All apps use `PostgresData.SyncRepository` directly. Worker-only bulk SQL moved into partial classes (`SyncRepository.RpeiOperations.cs`, `SyncRepository.CsOperations.cs`). Dual-called methods remain as delegates. Dead code removed from shared repos
- ~~Integration tests for PostgresData `SyncRepository` against real PostgreSQL~~; **done** (#428). Scenario 1 and Scenario 8 MediumLarge pass
- ~~Intra-phase parallelism~~; **done** (#430). `ParallelBatchWriter` + COPY binary for CSOs, RPEIs, sync outcomes
- Systematic database index/query performance analysis; tracked in #427

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
- This is a new sync-phase pattern that the ISyncEngine extraction must account for; reference fixup is I/O (raw SQL), not pure domain logic, so it belongs in `ISyncRepository` rather than `ISyncEngine`
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
- ~~The two-tier try/catch fallback count had grown from ~7 to ~17 catch blocks across 4 repository files~~ **(Resolved by #394)**: all fallback blocks deleted when Worker tests migrated to `InMemoryData.SyncRepository`
- ~~This further strengthened the case for an in-memory `SyncRepository`~~ **(Delivered by #394)**: the purpose-built `InMemoryData.SyncRepository` eliminated all EF Core test quirks
