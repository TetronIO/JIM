# Temporal Scope Re-evaluation: Implementation Plan

- **Status:** Doing (Phase 1 complete)
- **Issue:** [#892](https://github.com/TetronIO/JIM/issues/892) (sub-task of [#85](https://github.com/TetronIO/JIM/issues/85))
- **PRD:** [`engineering/prd/PRD_RELATIVE_DATE_SEARCH_CRITERIA.md`](../../prd/PRD_RELATIVE_DATE_SEARCH_CRITERIA.md) (#85)
- **Builds on:** [`RELATIVE_DATE_SEARCH_CRITERIA.md`](../RELATIVE_DATE_SEARCH_CRITERIA.md) (the relative-date criteria, evaluator, API, UI)
- **Related:** [#891](https://github.com/TetronIO/JIM/issues/891) (Full Synchronisation watermark-skip review; independent, see Interaction below)

## Overview

PRD #85 added relative ("now ± N units") date criteria to Synchronisation Rule scope filters. The criteria, evaluator, API, PowerShell and UI all work: when an object is evaluated, the relative boundary resolves against the live clock and the in/out decision is correct.

The gap this plan closes is that a time-driven scope transition is **only ever observed when the object is otherwise processed**. Both the inbound (import sync) and outbound (export evaluation) hot paths skip objects that have not changed since the last run, *before* scoping is evaluated. So an object whose source data is static (the normal case: a leaver whose end date was set months ago, a joiner whose start date is fixed in advance) never has its relative-date scope re-evaluated as the clock crosses the boundary. It is silently left in its prior scope state.

This plan adds a scheduled **Temporal Scope Reconciler** that detects time-driven scope transitions for both directions and routes the affected objects back through the existing, unchanged sync/export engine to apply whatever the correct outcome is.

This is the mechanism MIM and similar engines provide as periodic temporal set / membership recalculation. Without it, relative-date scoping cannot deliver date-driven joiner provisioning or leaver deprovisioning on static data, which is its primary purpose.

## Business Value

- **Date-driven deprovisioning.** A leaver falls out of inbound scope when their end date passes and is deprovisioned automatically, with no source-data change and no external automation.
- **Date-driven, staged provisioning.** A joiner's identity can exist in the Metaverse early (so a line manager can prepare access) while a temporal **export** criterion holds downstream provisioning (for example to AD) until their start date is within range. Inbound and outbound temporal scope are independent levers.
- **Set-and-forget policy.** "Accounts expiring within 7 days", "hired in the last 30 days", "contracts ended more than 90 days ago" become live, self-maintaining scopes rather than values that rot the moment they are saved.

## Current State (verified)

- **Inbound skip.** `ProcessActiveConnectedSystemObjectAsync` returns early when `connectedSystemObject.IsUnchangedSinceLastSync` is true, before scoping is evaluated: [`SyncTaskProcessorBase.cs:425-426`](../../../src/JIM.Worker/Processors/SyncTaskProcessorBase.cs#L425). The flag is set from a last-sync watermark: [`ConnectedSystemRepository.cs:1026-1033`](../../../src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs#L1026). A **full** sync passes that watermark too ([`SyncFullSyncTaskProcessor.cs:137-139`](../../../src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs#L137)), so a full sync does **not** rescue the static-object case.
- **Outbound skip.** Export evaluation is driven by changed Metaverse Objects only; `EvaluateExportRulesWithNoNetChangeDetectionAsync` takes the changed MVO plus its changed attributes and checks `IsMvoInScopeForExportRule` per rule: [`ExportEvaluationServer.cs:311-318, 358`](../../../src/JIM.Application/Servers/ExportEvaluationServer.cs#L311). An unchanged MVO is never handed to export evaluation, so a temporal export criterion never re-fires from time alone.
- **The evaluator itself is correct.** `IsCsoInScopeForImportRule` (and the MVO equivalent) resolve "now" live on every call: [`ScopingEvaluationServer.cs:150`](../../../src/JIM.Application/Servers/ScopingEvaluationServer.cs#L150). The problem is purely that the evaluator is never reached for a static object.
- **Empirical proof.** Integration test Scenario 12, step `ReEvaluatedEachRun` ("T4"): identical source data, clock advanced past the boundary, full import + full sync, zero scope changes.

## Design Principles

1. **Scope membership is a step function with a computable transition instant.** "endDate ≥ now" flips exactly when the clock crosses that object's endDate; "startDate ≤ now" flips at startDate. We never need to poll an object to learn it transitioned; we can compute *when* from its own data. Work is therefore O(objects that transition), not O(connector space).
2. **Flag-and-delegate, never bespoke apply.** The reconciler only **selects** the objects whose temporal scope flipped and **flags** them so the engine's unchanged-skip lets them through. It does **not** compute or shortcut the outcome. A temporal scope transition can produce the engine's full outcome set: project, **join to a pre-existing object found by Object Matching Rules**, Attribute Flow (inbound to the Metaverse Object and outbound to other Connected Systems, i.e. bidirectional cascading updates), attribute recall, disconnect, delete, provision, deprovision. Reproducing that in the reconciler would be wrong and unmaintainable; routing through the real engine gets all of it for free.
3. **Leave the hot path untouched.** The inbound sync and outbound export skips stay exactly as they are. The reconciler is a separate, scheduled process; the flag is simply what lets a chosen object past the existing skip. This mirrors the existing housekeeping pattern (grace-period MVO deletion) and the drift-reconciliation model.

## Proposed Architecture

A built-in, interval-driven **Temporal Scope Reconciler** with two symmetric lanes:

- **Inbound lane:** for each enabled import Synchronisation Rule carrying a relative-date scoping criterion, find candidate CSOs whose temporal scope may have flipped, evaluate the full scope (reusing the existing evaluator), and flag mismatches for re-sync.
- **Outbound lane:** the same for export Synchronisation Rules over Metaverse Objects, flagging mismatches for re-export-evaluation.

```mermaid
flowchart TB
    sched["Built-in Schedule:<br/>Temporal Scope Reconciliation<br/>(configurable interval, default hourly)"]

    subgraph worker["jim.worker (existing hot paths UNCHANGED)"]
        hk["Housekeeping loop<br/>+ NEW: invoke reconciler"]
        sync["Sync / Export processors<br/>(skip-if-unchanged kept;<br/>flag lets chosen objects through)"]
    end

    subgraph app["JIM.Application"]
        rec["ScopingEvaluationServer (partial: Reconciliation)<br/>NEW<br/>1 load rules with a relative criterion<br/>2 fetch candidates (repo)<br/>3 evaluate (reuse existing methods)<br/>4 flag mismatches"]
    end

    subgraph data["JIM.PostgresData"]
        q["NEW candidate queries (inbound CSO / outbound MVO)<br/>indexed range over DateTimeValue + currently-in-scope set"]
    end

    db[("PostgreSQL<br/>NEW: ScopeReviewPending flag, LastScopeEvaluatedAt,<br/>composite (AttributeId, DateTimeValue) index")]

    sched -->|enqueue task| hk
    hk --> rec
    rec --> q
    q --> db
    rec -->|flag only| db
    rec -->|enqueue targeted re-sync / re-export-eval| sync
    sync -->|project/join/flow/update/disconnect/delete/<br/>provision/deprovision| db
```

```mermaid
sequenceDiagram
    participant S as Scheduler
    participant R as Reconciler (ScopingEvaluationServer)
    participant DB as Repository / DB
    participant E as Existing sync/export engine

    S->>R: interval tick
    R->>DB: load rules with a relative criterion (both directions)
    R->>DB: candidates = dateAttr crossed boundary since last eval ∪ currently in-scope set
    DB-->>R: small candidate set
    loop each candidate
        R->>R: full scope evaluate (now live), compare to current state
    end
    R->>DB: flag mismatches (ScopeReviewPending = true)
    R->>E: enqueue targeted re-sync (inbound) / re-export-eval (outbound)
    E->>DB: apply correct outcome (project/join/flow/disconnect/delete/provision/deprovision)
    Note over E,DB: cascades (matching joins, bidirectional Attribute Flow,<br/>downstream exports) run through the unchanged engine
```

### Candidate pre-filter (performance)

The date attribute values live in typed columns on the attribute-value tables (`ConnectedSystemObjectAttributeValue.DateTimeValue`, and the Metaverse equivalent). The reconciler pushes a coarse, indexable date predicate into SQL so only objects whose temporal boundary could have crossed since the last evaluation are loaded, then runs the full (possibly compound `All`/`Any`/nested) scope evaluation in memory on that small set. The SQL pre-filter is a **superset-narrowing** filter; correctness comes from the in-memory evaluation, so compound groups that mix date and non-date criteria are handled correctly (a candidate the date predicate admits but a non-date criterion rejects is simply filtered in memory). A per-object `LastScopeEvaluatedAt` watermark bounds each sweep to the slice since it last ran.

## Implementation Phases

Both lanes (inbound and outbound) are delivered in the first implementation, per decision below. Each phase builds and tests green before the next.

### Phase 1: Model and schema ✅
- Added the staging flag and watermark (`ScopeReviewPending`, `LastScopeEvaluatedAt`) to CSO and MVO. Chosen shape: an explicit, auditable `ScopeReviewPending` bool (the recommended option over reusing the unchanged-skip) plus a per-object `LastScopeEvaluatedAt` UTC watermark. Both default to unset (`false` / null); the bool columns are metadata-only adds in PostgreSQL (no table rewrite).
- Added a composite `(AttributeId, DateTimeValue)` partial index (`DateTimeValue IS NOT NULL`) on **both** attribute-value tables, sized for the reconciler's equality-then-range candidate pre-filter. Note: the MVO table already carried a bare `[Index(DateTimeValue)]`; the plan's "none exists today" was inaccurate for MVO. The new composite is the correct shape for `AttributeId = @x AND DateTimeValue IN [lo,hi)` and supersedes the bare index for that access pattern.
- Added a `BuiltIn` bool to `Schedule` (following the established `BuiltIn` convention on `ConnectorDefinition`, `MetaverseAttribute`, `Role`, etc.). Guards (block rename/delete) land in Phase 5.
- EF migration `AddTemporalScopeReconcilerFields` (append-only). Solution builds 0/0; full suite green (1738 passed).

### Phase 2: Candidate queries (repository)
- Inbound: `GetInboundTemporalScopeCandidatesAsync(rule, sinceUtc, nowUtc)` over CSO date attribute values ∪ currently-joined CSOs of the object type.
- Outbound: the MVO equivalent over Metaverse Object date attribute values ∪ currently in-export-scope MVOs.
- Raw Npgsql on the worker path, per the worker hot-path SQL convention.

### Phase 3: Reconciler (ScopingEvaluationServer, partial class)
- New `ScopingEvaluationServer.Reconciliation.cs` partial: load rules with a relative criterion, fetch candidates, evaluate full scope (reuse `IsCsoInScopeForImportRule` / `IsMvoInScopeForExportRule`), flag mismatches, update `LastScopeEvaluatedAt`.
- Pure selection and flagging; no mutation of join state or exports.

### Phase 4: Trigger and apply
- Seed the built-in "Temporal Scope Reconciliation" Schedule (BuiltIn, default hourly).
- Worker execution: run the reconciler, then enqueue the targeted re-sync (inbound) / re-export-evaluation (outbound) of flagged objects through the existing engine.

### Phase 5: API / UI (built-in protection)
- Allow enable/disable and interval change on the built-in Schedule; block rename and delete (BuiltIn guard in the application layer and UI).

### Phase 6: Tests
- Unit: candidate selection (range + currently-in-scope union), mismatch detection, watermark advance, compound-group correctness, flag-and-delegate (assert the engine, not the reconciler, produces project/join/update/disconnect/delete).
- Integration: Scenario 12 `ReEvaluatedEachRun` (T4) now passes; add an outbound case (temporal export criterion provisions on schedule with no MVO data change). Note the reconciler is the cache-bypassed, now-relative test path already established in Scenario 12.

## Decisions (resolved)

- **A full sync does not save us (Q1).** Both inbound and outbound skip unchanged objects before scoping; a scheduled reconciler is required regardless of run-profile type. The separate "should full sync reprocess everything as a fail-safe" question is tracked in #891 and does not block this work.
- **Home:** `ScopingEvaluationServer` (partial class), which already houses both the CSO and MVO scope evaluators.
- **Flag-and-delegate**, not a bespoke provision/deprovision action: required so matching-rule joins to pre-existing objects and bidirectional Attribute Flow cascades are handled by the proven engine.
- **Both lanes in the first implementation** (inbound CSO import scope and outbound MVO export scope).
- **Cadence configurable** via the built-in Schedule interval; default conservative (hourly), can be lowered to single-digit minutes for parity with traditional temporal-recalculation engines. **Hours-granularity criteria are retained** (the staged-provisioning use case needs sub-day precision).
- **Built-in Schedule:** admins may enable/disable and change interval, but not rename or delete.
- **Candidate pre-filter + new composite `(AttributeId, DateTimeValue)` partial index** (on both attribute-value tables) to keep cost O(transitions).

## Open Decisions (resolve during implementation)

- **Staging mechanism:** an explicit `ScopeReviewPending` flag (auditable, recommended) versus reusing the existing unchanged-skip by bumping `LastUpdated` / clearing `IsUnchangedSinceLastSync`.
- **Apply step:** a dedicated targeted "scope re-sync" mode that iterates only flagged objects, versus enqueuing a normal sync/export of the flagged subset.
- **Candidate-set precision:** exact date-range delta since last tick (optimal) versus the simpler "all currently in-scope ∪ today's boundary" baseline first, optimised later.
- **Interval configuration surface:** Schedule interval only, or also a Service Setting for the seeded default / floor.

## Risks and Mitigations

- **Performance at scale.** Mitigated by the indexed pre-filter (O(transitions)), batch caps, and reuse of the housekeeping batching pattern. No change to the sync/export hot paths.
- **Compound-group correctness.** The SQL pre-filter is a superset; the in-memory full evaluation makes the final decision, so mixed date / non-date `All`/`Any`/nested groups are correct.
- **Concurrency with an in-flight sync.** The reconciler only flags and enqueues; it never mutates join/export state directly, so it is idempotent and safe to overlap. Flags are cleared when the object is processed.
- **Interaction with #891.** Independent. If full sync is later restored to reprocess everything, the reconciler is still wanted for timeliness (it can run far more frequently than a full sync, and only touches transitioning objects).
- **Time and rounding.** All UTC; Days and coarser units round to midnight UTC, Hours is exact (already implemented in `RelativeDateResolver` and unit-tested).

## Success Criteria

- Scenario 12 `ReEvaluatedEachRun` (T4) passes: a static object falls out of (and into) scope purely because the clock advanced, and is deprovisioned / provisioned accordingly.
- An outbound temporal export criterion provisions a downstream object on schedule with no Metaverse Object data change.
- No measurable regression on the sync/export hot path (the skip is unchanged; the reconciler runs out of band).
- The built-in Schedule is interval-editable and enable/disable-able, but protected from rename and delete.
