# Synchronisation Preview / What-If Evaluation Engine

- **Status:** Planned
- **Created:** 2026-07-07
- **Author:** Jay Van der Zant
- **Issue:** [#288](https://github.com/TetronIO/JIM/issues/288)

## Problem Statement

Administrators cannot see the consequences of a synchronisation before it runs. Today, the only way to discover what a Synchronisation Rule configuration will do to a Connected System Object (CSO) is to run a real sync and inspect the resulting Run Profile Execution Items (RPEIs) and Pending Exports after the fact. By then the metaverse has already changed and Pending Exports may already be queued. During initial configuration, troubleshooting, and change-impact review this is exactly backwards: the administrator needs to understand the full causal chain (projection or join, Attribute Flow into the Metaverse Object, downstream Pending Exports and provisioning into target systems) *before* committing to it, not after.

The building blocks for this already exist but are not wired together. `SyncRunMode` (`PreviewOnly`, `PreviewAndSync`) is defined in `PendingExportEnums.cs`; `ExportEvaluationPreviewResult` captures a summary-level outbound preview; `ExportExecutionServer.ExecuteExportsAsync` already honours `SyncRunMode.PreviewOnly`; and #363 (shipped) delivered the `ActivityRunProfileExecutionItemSyncOutcome` causal-tree model that records "what actually happened" per object. What is missing is the piece #288 calls out directly: a `SyncPreviewResult` model and a `SyncPreviewServer` that build that same causal tree **speculatively**, for the full inbound-and-outbound chain, with an absolute guarantee that doing so changes nothing.

That guarantee is the crux of this work. **Synchronisation integrity is paramount.** A preview that leaks a single Pending Export, a single Metaverse Object attribute write, or a single obsoletion into the live system is worse than no preview at all, because administrators will trust it. The central engineering problem this PRD addresses is not "compute the outcome tree"; the outcome-building logic largely exists. It is "compute the outcome tree with a provable, defence-in-depth guarantee of zero side effects", and to do so without the preview logic silently diverging from the real sync logic over time.

This PRD scopes the **evaluation engine** only: the model, the server, and the zero-side-effect guarantee. It is the foundation that #827 (the unified configuration-change preview framework) consumes as its Tier 3 object-level impact engine; see [Relationship to #827](#relationship-to-827). This PRD deliberately does not design the configuration-diff adapters, the multi-surface coverage map, or the framework-level UX; those are #827's remit.

## Goals

- Deliver a `SyncPreviewResult` model and a `SyncPreviewServer` service that, for a single CSO or a single Metaverse Object (MVO), produce the full speculative causal chain: inbound projection/join, Attribute Flow deltas, outbound Pending Exports, and target-system provision/update/delete outcomes.
- Reuse the `ActivityRunProfileExecutionItemSyncOutcome` tree from #363 as the preview payload, so "what would happen" and "what did happen" render through one model and one set of display helpers. We can confirm this by rendering a preview tree and an RPEI outcome tree with the same component.
- Guarantee zero side effects with a defence-in-depth design (not a single persistence gate): we can confirm this by asserting, after every preview scenario in the integration suite, that Pending Export count, MVO count, MVO attribute-value versions, CSO count, and RPEI count are all byte-identical to their pre-preview state.
- Prove preview fidelity: for a representative population, the outcomes a preview reports must match the outcomes a real sync of the same objects actually produces. We can confirm this with a paired integration test (preview, then real sync, then diff the two outcome trees).
- Surface blocking and non-blocking conditions distinctly: validation `Errors` that would stop the sync, and `Warnings` (for example unresolved reference attributes) that would not.
- Establish an explicit scale posture for previewing large object sets (single object is trivial; a full-system preview at 100K+ objects is not), including a sampling strategy so a full-system preview returns useful results within a bounded time and memory budget.

## Non-Goals

- **The configuration-change preview framework (#827).** This PRD builds the object-level evaluation engine #827 calls its "Tier 3". It does not build the per-surface diff adapters (scope, matching rules, attribute flow, deletion settings, partition selection), the tiered validation/count/object model, or the "proposed vs current configuration" representation. Those are #827.
- **Approval workflows.** Reviewing and approving previewed changes before they apply (the "review and approve" bullet in #288's UI section) is a separate capability; the engine produces the data an approval flow would consume, but the flow itself is out of scope.
- **New sync semantics.** The preview reflects the sync behaviour that already exists. It is not a vehicle for changing projection, join, Attribute Flow, scoping, or deprovisioning rules. If the preview surfaces a real bug in the live sync engine, that is a separate fix.
- **Rollback / undo of an already-executed sync.** Preview is pre-execution; export rollback is a distinct future item.
- **Persisting preview results.** v1.0 previews are transient (computed on demand, returned, discarded). Whether previews are ever stored as Activities for audit is an open question owned by #827 (its Open Question 4), not decided here.
- **Historical / point-in-time preview.** Previews evaluate against the *current* metaverse and connector-space state, not a past snapshot.

## User Stories

1. As an administrator configuring a new Synchronisation Rule, I want to preview the full sync chain for a sample CSO before I run anything, so that I can confirm the rule projects, joins, and flows attributes the way I intended without mutating live data.
2. As an administrator troubleshooting why a CSO "is not syncing as expected", I want a preview that shows exactly where the chain stops (out of scope, no matching rule, blocked by a validation error, unresolved reference), so that I can diagnose the cause without repeatedly running real syncs and cleaning up after them.
3. As an administrator planning a change, I want to preview the outbound impact from a specific MVO, so that I can see which target-system CSOs would be created, updated, or deleted before I commit.
4. As a release engineer, I want the preview engine to carry an automated guarantee that it never writes to the live system, so that I can trust it in production without fearing that a preview corrupted the metaverse.
5. As a developer building #827's configuration-change previews, I want a stable `SyncPreviewServer` API that returns object-level outcome trees, so that I can build diff adapters on top of one evaluation engine rather than reimplementing sync evaluation per surface.

## Requirements

### Functional Requirements

#### Preview result model

1. Introduce a `SyncPreviewResult` model (`src/JIM.Models/Transactional/`) that carries the speculative outcome of a preview. It must expose:
   - The **causal outcome tree**: a `List<ActivityRunProfileExecutionItemSyncOutcome>` (or an equivalently-shaped speculative variant; see Decision D4) built by the same logic the real processors use, rooted at the CSO or MVO being previewed.
   - **Inbound summary**: whether the CSO would project a new MVO or join an existing one (and which MVO), and the list of MVO Attribute Flow changes (attribute, old value, new value, contributing Synchronisation Rule).
   - **Outbound summary**: the Pending Exports that would be created per target Connected System, and the resulting provision / update / delete outcome per target CSO.
   - **Metadata**: `Warnings` (non-blocking, e.g. unresolved reference attribute, scope change), `Errors` (would prevent the sync), and `AffectedSyncRules` (which Synchronisation Rules participated at each step).
2. Reuse the existing `ExportEvaluationPreviewResult` for the outbound summary rather than duplicating create/update/delete counters; `SyncPreviewResult` composes it rather than re-inventing it.
3. The model must be serialisable for API return (see Decision D3) without carrying EF navigation cycles or lazy-loaded proxies.

#### Preview server

4. Introduce a `SyncPreviewServer` (`src/JIM.Application/Servers/`), reachable only through `JimApplication` per the layer rules, exposing:
   - `PreviewSyncForCsoAsync(cso)` — the full inbound-plus-outbound chain for one CSO: would it project or join; what MVO attributes would change; what Pending Exports would be triggered; what target CSOs would be provisioned, updated, or deleted.
   - `PreviewSyncForMvoAsync(mvo)` — the outbound chain from an existing MVO.
   - `PreviewFullSyncAsync(connectedSystem, options)` — what a full sync run against a Connected System would produce, subject to the sampling strategy (requirement 12).
5. Each method returns a `SyncPreviewResult`. Errors that would block the real sync are captured in `Errors` and the preview still returns (it does not throw for an expected block); genuinely exceptional conditions (e.g. the CSO does not exist) throw as they would anywhere else.
6. The server must resolve the same Synchronisation Rules, scoping criteria, Object Matching Rules, and Attribute Flow precedence the real sync would, so that the previewed outcome matches a real run of the same object (fidelity, requirement 9).

#### Zero side effects (the central guarantee)

7. A preview MUST NOT persist any Pending Export, MVO create/update/delete, CSO create/update/delete, RPEI, Activity, obsoletion, deferred reference, or service-setting change. This is the paramount requirement; every other requirement yields to it.
8. The guarantee must be **defence in depth**, not a single flag. The implementation must combine at least two independent mechanisms so that a bug in one still cannot mutate live data. Candidate layers (final combination settled in Decision D1):
   - A speculative execution mode that routes all writes to an in-memory sink instead of the repository.
   - An outermost database transaction that is **unconditionally rolled back** at the end of every preview, regardless of outcome, so any write that slipped through a gate is discarded.
   - A read-only repository/`DbContext` facade for the preview path that throws on any write attempt, converting a missed gate into a loud failure rather than a silent commit.
9. **Fidelity check:** the engine must be covered by a paired test that previews a representative object, then really syncs the same object, and asserts the preview's outcome tree matches the real RPEI's outcome tree (same node types, same targets, same attribute deltas). A drift here means the preview is lying and is a release blocker.
10. **Isolation assertion:** every preview integration scenario must assert, immediately after the preview call, that Pending Export count, MVO count, MVO attribute versions, CSO count, RPEI count, and Activity count are unchanged from immediately before the call.
11. Concurrency: a preview must be safe to run while real syncs and imports are in progress. It must not take locks or hold a transaction long enough to block live processing, and it must tolerate the underlying state changing between preview and any later real run (the preview is explicitly a point-in-time "what if", not a promise).

#### Scale and sampling

12. `PreviewFullSyncAsync` must not attempt to build a full object-level tree for every object in a 100K+ Connected System by default. It must apply a **sampling strategy** (settled in Decision D2) that returns a representative, bounded result. At minimum it must support:
    - A **count / summary tier**: how many objects would project, join, fall out of scope, provision, update, delete, blocked by errors, aggregated without building a per-object tree for all of them.
    - A **sampled object-level tier**: full outcome trees for a bounded sample (e.g. first N, or N per outcome category), so the administrator sees representative detail without unbounded cost.
13. Single-object previews (`PreviewSyncForCsoAsync`, `PreviewSyncForMvoAsync`) have no sampling; they always build the full tree for that one object.
14. `PreviewFullSyncAsync` must expose a bounded work budget (object cap and/or time budget) and report in the result when it was truncated, so a full-system preview cannot run unbounded and the administrator knows the result is a sample.

#### Consumption surface (v1.0)

15. The engine must be reachable in a way #827 can build on. The minimum consumption surface for v1.0 is settled in Decision D3; the engine's public API on `JimApplication` must exist regardless of which front ends (UI, REST, PowerShell) ship in v1.0.
16. Where a preview surfaces a blocking `Error`, the consumer must be able to distinguish it from a `Warning` programmatically (not by string parsing), so a UI can render blockers and advisories differently.

### Non-Functional Requirements

- **Zero side effects** is a correctness requirement, not a performance one, and takes precedence over every other target. See requirements 7 to 11.
- Single-object preview (`PreviewSyncForCsoAsync` / `PreviewSyncForMvoAsync`) should return interactively (target: under ~2 seconds for a typical object with a handful of target systems on the Nano integration template), since it backs a UI button.
- `PreviewFullSyncAsync` must complete within its declared work budget at 100K+ objects and must not exhaust worker memory; the sampling strategy exists precisely to make this bound achievable.
- No new NuGet packages, no new connectors.
- British English throughout; no em dashes. Proper-case JIM domain nouns.
- Air-gap safe: no external calls introduced by preview beyond the connector reads a real sync would already make.

## Examples and Scenarios

### Scenario 1: Preview a CSO that would join and flow attributes

**Given** an HR-System CSO `CN=John Smith,OU=Users,DC=corp,DC=local` and an existing MVO it would match on
**When** the administrator previews sync for that CSO
**Then** the result shows `Joined` to the existing MVO, an Attribute Flow node listing `displayName: "John Smith" -> "Dr. John Smith"` and `department: "Engineering" -> "Research"` (contributing Synchronisation Rule named), and no change to `title`; and outbound nodes showing an Update Pending Export to Active Directory and to the cloud directory for `displayName` and `department`
**And** after the call, Pending Export count, MVO count and versions, and RPEI count are byte-identical to before it.

### Scenario 2: Preview surfaces a blocking error and a warning

**Given** a CSO whose configuration would flow a required attribute that is empty, and whose `manager` reference points at an MVO not yet provisioned to the target
**When** the administrator previews sync for that CSO
**Then** `Errors` contains the missing-required-attribute condition (marked as blocking), `Warnings` contains the unresolved `manager` reference, and the outbound section reflects that the export would be blocked
**And** nothing is persisted.

### Scenario 3: Preview outbound from an MVO

**Given** an existing MVO with contributions destined for two target systems
**When** the administrator calls `PreviewSyncForMvoAsync`
**Then** the result lists, per target Connected System, the Pending Exports that would be created and whether each target CSO would be provisioned (new) or updated, with the attribute set per export.

### Scenario 4: Full-system preview applies sampling at scale

**Given** a Connected System with 250,000 CSOs
**When** the administrator calls `PreviewFullSyncAsync` with the default budget
**Then** the result returns count-tier aggregates for the whole population (projects / joins / out-of-scope / provisions / updates / deletes / errors) plus full outcome trees for a bounded sample, and flags that the object-level detail is a sample and the run was truncated at the budget
**And** nothing is persisted, and the call stays within its time and memory budget.

### Scenario 5: Fidelity — preview matches the real run

**Given** any object from Scenario 1
**When** the administrator previews it and then a real sync processes the same object
**Then** the preview's outcome tree and the real RPEI's outcome tree agree on node types, targets, and attribute deltas.

## Constraints

- Must respect the N-tier layering: UI/API/PowerShell reach the engine only via `JimApplication`, never `Jim.Repository.*` directly.
- Must reuse the `ActivityRunProfileExecutionItemSyncOutcome` model and the existing outcome display helpers rather than introducing a parallel preview-only tree shape, unless Decision D4 concludes a speculative variant is unavoidable.
- Must not modify live sync semantics; a preview run and a real run of the same object must agree.
- Must work in air-gapped environments; no new external dependencies.
- British English; no em dashes; proper-case domain nouns.

## Affected Areas

| Area | Impact |
|------|--------|
| Models | New `SyncPreviewResult` in `src/JIM.Models/Transactional/`; composes existing `ExportEvaluationPreviewResult`; reuses `ActivityRunProfileExecutionItemSyncOutcome` |
| Application | New `SyncPreviewServer` in `src/JIM.Application/Servers/`, exposed via `JimApplication`; likely refactor to extract the pure evaluation core shared by `SyncEngine` / `SyncImportTaskProcessor` / `ExportEvaluationServer` so preview and real sync call one evaluator (scope of refactor depends on Decision D1) |
| Worker | The import/export processors (`SyncImportTaskProcessor`, `SyncExportTaskProcessor`, `SyncOutcomeBuilder`) are the reference behaviour the engine must match; whether they are refactored to share code with preview, or left untouched with a parallel evaluator, is Decision D1 |
| Data | Read-only repository/`DbContext` facade and/or unconditionally-rolled-back transaction wrapper for the preview path (defence-in-depth, requirement 8) |
| API | Possible new preview endpoint(s) under the existing authorised controllers — conditional on Decision D3 |
| PowerShell | Possible new preview cmdlet(s) — conditional on Decision D3 |
| UI | "Preview Sync" affordance on the CSO detail page reusing the RPEI outcome-tree component — conditional on Decision D3; #288's own UI section marks the UI as "future" |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/` | If any administrator-facing surface ships in v1.0 (UI button, API, or cmdlet per Decision D3), add a "Previewing synchronisation (what-if)" concept/how-to page and note the zero-side-effect guarantee. If v1.0 is engine + internal API only with no administrator surface, no `docs/` change is required and the changelog stays untouched (this is a foundation, not yet a user-facing feature). |
| `engineering/` | Update the developer/architecture guide to describe the preview engine and, critically, the defence-in-depth zero-side-effect design so future contributors do not accidentally introduce a persisting code path into the preview flow. |

## Dependencies

- **#363 (shipped):** `ActivityRunProfileExecutionItemSyncOutcome` causal-tree model and tree-building logic. This PRD builds directly on it; without it there is no shared model to speculate over.
- **Existing outbound sync foundation (from #121):** `SyncRunMode`, `ExportEvaluationPreviewResult`, and `ExportExecutionServer`'s existing `SyncRunMode.PreviewOnly` handling. The engine extends this from outbound-summary to the full inbound-plus-outbound tree.
- **#827 depends on this**, not the other way round: #827's Tier 3 object-level impact analysis is this engine. Deliver the engine with a stable API #827 can adopt.

## Relationship to #827

#827 (configuration-change preview: unified framework and coverage map) decided a **framework-first, holistic** approach: one preview framework with per-surface diff adapters and three tiers (validation, count-level, full object-level). #827 names #288 explicitly as "the evaluation engine" and its Tier 3 foundation.

Division of labour:

- **This PRD (#288)** owns the object-level evaluation engine: given an object (and, later, a proposed configuration handed in by an adapter), speculatively compute the full sync outcome tree with a zero-side-effect guarantee. It answers "what would happen to *this object* if we synced it now."
- **#827** owns everything above the engine: the adapter model that turns a configuration diff into the set of affected objects, the validation and count tiers, the "proposed vs current configuration" representation, and the consistent cross-surface administrator UX.

To keep the boundary clean, `SyncPreviewServer` must accept the object(s) to evaluate as inputs and must not embed any configuration-diff logic. Where #827 will later need previews to run against a *proposed* (unsaved) configuration rather than the live one, this PRD should ensure the engine's evaluation core takes its configuration as a parameter rather than always reading the persisted configuration, so #827 can pass a proposed configuration without re-architecting the engine. That extensibility is called out as Decision D5.

## Open Questions

These are the substantive design questions. Each is carried into "Decisions Needed" below with a recommendation.

1. Reuse the real sync code paths in a read-only mode, or run a separate shadow evaluation? (D1)
2. What sampling strategy does `PreviewFullSyncAsync` use at 100K+ objects? (D2)
3. What consumption surface ships in v1.0: UI only, or API / PowerShell too? (D3)
4. Reuse the persisted `ActivityRunProfileExecutionItemSyncOutcome` entity directly for the speculative tree, or introduce an unpersisted DTO variant? (D4)
5. Should the engine's evaluation core take configuration as a parameter now, to unblock #827's proposed-configuration previews later? (D5)

## Acceptance Criteria

- [ ] `SyncPreviewResult` exists in `src/JIM.Models/Transactional/`, composes `ExportEvaluationPreviewResult`, exposes the causal outcome tree, inbound/outbound summaries, and `Warnings` / `Errors` / `AffectedSyncRules`, and is cleanly serialisable.
- [ ] `SyncPreviewServer` exists in `src/JIM.Application/Servers/`, reachable via `JimApplication`, with `PreviewSyncForCsoAsync`, `PreviewSyncForMvoAsync`, and `PreviewFullSyncAsync`.
- [ ] The zero-side-effect guarantee is implemented as defence in depth (at least two independent mechanisms per requirement 8) and documented in `engineering/`.
- [ ] An isolation assertion (requirement 10) runs after every preview integration scenario and confirms Pending Export, MVO (count and attribute versions), CSO, RPEI, and Activity state are unchanged.
- [ ] A fidelity test (requirement 9) previews an object and then really syncs it and asserts the two outcome trees match.
- [ ] `PreviewFullSyncAsync` applies the agreed sampling strategy, respects a bounded work budget, and flags truncated / sampled results; a 100K+ object preview stays within the budget without exhausting memory.
- [ ] Blocking `Errors` are distinguishable from `Warnings` programmatically.
- [ ] The engine takes the object(s) to evaluate as input and contains no configuration-diff logic (the #827 boundary holds).
- [ ] Build and tests green (`dotnet build JIM.sln`, `dotnet test JIM.sln`); new behaviour is covered TDD-first.

## Additional Context

- `SyncRunMode` enum and existing preview plumbing: `src/JIM.Models/Transactional/PendingExportEnums.cs`, `src/JIM.Models/Transactional/ExportEvaluationPreviewResult.cs`, `src/JIM.Application/Servers/ExportExecutionServer.cs`.
- Causal-tree model (shared with real sync): `src/JIM.Models/Activities/ActivityRunProfileExecutionItemSyncOutcome.cs`; builder: `src/JIM.Worker/Processors/SyncOutcomeBuilder.cs`.
- Reference sync behaviour the engine must match: `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs`, `SyncExportTaskProcessor.cs`, `src/JIM.Application/Servers/SyncEngine*.cs`, `ExportEvaluationServer.cs`.
- Outbound sync design (Q5 preview decision, Phase 4): `engineering/plans/done/OUTBOUND_SYNC_DESIGN.md`.
- Outcome-graph design and the #288 synergy section: `engineering/plans/done/RPEI_OUTCOME_GRAPH.md`.
- Parent / sibling preview theme: [#827](https://github.com/TetronIO/JIM/issues/827); shared model source: [#363](https://github.com/TetronIO/JIM/issues/363).

## Decisions Needed

The product owner must settle the following before the implementation plan is generated. Each carries a recommendation.

### D1. Read-only reuse of the real sync code paths, vs a separate shadow evaluator

- **Option A — Reuse the real processors in a read-only mode.** Thread a preview flag (or `SyncRunMode.PreviewOnly`) through `SyncImportTaskProcessor` / `SyncEngine` / `ExportEvaluationServer` and gate every persistence call. *Pro:* maximum fidelity by construction; there is only one evaluator, so preview cannot drift from reality. *Con:* highest side-effect risk; these processors interleave computation with persistence (bulk inserts, RPEI flushes, reconciliation writes), so every write site becomes a gate that must never be missed, in perpetuity, including in future edits.
- **Option B — A separate shadow evaluator.** A dedicated read-only evaluation path that never enters persistence-capable code. *Pro:* zero side effects almost by construction. *Con:* two evaluators to keep in lockstep; preview logic will drift from real sync logic over time unless heavily tested, which is the exact failure mode that makes a preview untrustworthy.
- **Option C (recommended) — Extract a shared, pure evaluation core, add persistence as a separate step.** Refactor so both real sync and preview call one evaluator that *computes* outcomes in memory; only the real path then runs a distinct persistence step. This is what #363's synergy section already anticipates ("same model and tree-building logic; the difference is whether outcomes are committed"), and the import processor already leans this way (it accumulates changes in memory then bulk-persists at the end). Wrap the preview call in the defence-in-depth guard (rolled-back transaction + read-only facade) as a belt-and-braces backstop even though the core does not persist.
- **Recommendation: C.** It gives Option A's fidelity (one evaluator) and Option B's safety (preview never reaches persistence), at the cost of an up-front refactor to separate evaluation from persistence. That refactor is worth it: it is the durable fix, and it directly enables #827. Accept the larger initial change rather than carrying either divergence risk (B) or a permanent minefield of persistence gates (A).

### D2. Sampling strategy for `PreviewFullSyncAsync` at 100K+ objects

- **Option A — Count/summary only for full-system.** Aggregate outcome counts across the whole population; no per-object trees. Cheap and bounded; least insight.
- **Option B — Count tier plus a bounded object-level sample.** Whole-population aggregates, plus full trees for a bounded sample (first N, or N per outcome category so each category is represented). Bounded cost, representative detail.
- **Option C — Configurable / stratified sampling with a work budget.** As B, but the administrator picks sample size and stratification, capped by an object and/or time budget, with truncation flagged.
- **Recommendation: B for v1.0, with the budget and truncation flag from C.** Whole-population counts answer "how big is the blast radius"; a bounded per-category sample answers "what does a representative change look like", which is what troubleshooting and validation actually need. Full stratified configurability (C) is polish that can follow. Single-object previews are never sampled.

### D3. v1.0 consumption surface: UI only, or API / PowerShell too

- **Option A — UI only.** "Preview Sync" button on the CSO detail page, reusing the RPEI outcome-tree component. Matches #288's stated primary use case; #288 itself labels UI as the deliverable and marks broader integration "future".
- **Option B — Engine + internal API only, no administrator surface yet.** Ship the engine and its `JimApplication` API as the #827 foundation; defer any administrator-facing surface. No `docs/` or changelog impact.
- **Option C — UI + REST API + PowerShell.** Full surface in one go.
- **Recommendation: A, with the engine designed API-first.** Build the engine and its `JimApplication` API such that a REST endpoint and a cmdlet are thin wrappers to add later (and that #827 can consume), but only ship the CSO-detail-page "Preview Sync" button as the v1.0 administrator surface. This delivers the concrete #288 use case, keeps the v1.0 surface area (and its docs/security review) small, and does not paint us into a corner for #827 or a later API/PowerShell surface. Full C is more than v1.0 needs; pure B ships no user-visible value despite the engine being the hard part.

### D4. Reuse the persisted outcome entity for the speculative tree, or add an unpersisted DTO

- **Option A — Reuse `ActivityRunProfileExecutionItemSyncOutcome` directly**, built in memory and never attached to a `DbContext`. Simplest; one model; but the entity carries persistence-oriented fields (FKs, parent IDs) that are meaningless speculatively, and an unattached-but-persistable entity near the preview path is a latent side-effect risk.
- **Option B — A lightweight `SyncOutcomeNode` DTO** for the speculative tree, mapped to/from the entity for display. Cleanly separates "speculative outcome" from "persisted outcome"; removes any chance a preview tree is accidentally saved; costs a mapping layer and a second shape to maintain.
- **Recommendation: B, if D1 lands on C.** Once evaluation is a pure core, having it return a persistence-free DTO reinforces the zero-side-effect guarantee (there is nothing persistable in the preview payload) and keeps the display component fed by a mapping both real and preview paths share. If D1 lands on A instead, reuse the entity directly (Option A) to avoid a mapping layer the reuse approach would not otherwise need.

### D5. Should the evaluation core accept configuration as a parameter now (to unblock #827)?

- **Option A — Not yet.** The engine always evaluates against the live persisted configuration. Simpler now; #827 must later refactor the core to inject a proposed configuration.
- **Option B (recommended) — Parameterise configuration input from the start.** Have the evaluation core take its configuration (Synchronisation Rules, scoping, matching, flow) as an input rather than always reading it from the repository, defaulting to the live configuration. #827 can then pass a proposed (unsaved) configuration without re-architecting the engine.
- **Recommendation: B.** #827 is a committed, framework-first initiative that explicitly builds on this engine and explicitly needs proposed-configuration previews (its Open Question 2). Designing the core to take configuration as a parameter now is a small addition that avoids a disruptive refactor later. The only reason to choose A is to shave initial scope; given #827's certainty, that is a false economy.
