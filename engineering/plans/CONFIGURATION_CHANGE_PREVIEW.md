# Configuration Change Preview Framework - Implementation Plan

- **Status:** Planned
- **Created:** 2026-07-20
- **Issue:** [#827](https://github.com/TetronIO/JIM/issues/827)
- **PRD:** [PRD_CONFIGURATION_CHANGE_PREVIEW.md](../prd/PRD_CONFIGURATION_CHANGE_PREVIEW.md)

## Overview

One Configuration Change Preview framework, consumed by thin per-surface adapters. An administrator requests a preview of an unsaved configuration change; a panel opens immediately and fills in through progressive stages (validation, impact counts, grouped change summary, object-level drill-down) as each completes. Where the computation runs (JIM.Web in-process or JIM.Worker) is an invisible dispatch decision. Results persist as queryable rows attached to an Activity for pagination, summarisation, and audit.

This plan sequences the work per the decisions recorded on #827 and in the PRD:

1. **#307 real-time notification foundation, then #202** (its first slice), before any #827 work (decided Jul 2026; #307 blocks #827). The framework's progress notification is real-time from day one; no polling-first implementation is built.
2. **#288 engine core** (the other true build dependency), in parallel with framework plumbing that does not need it.
3. **Apply-time messaging** (PRD FR17) as an early framework phase, rolled across all sync-affecting surfaces before any adapter exists.
4. **Framework foundations** (models, persistence, orchestration, dispatch, notification, UI shell, summarisation), proven end-to-end by the first adapter.
5. **Adapter waves** as follow-up issues in severity order: G5 and G3-destructive, then G4, then G1/G2, then G6 and the re-scoped issues (#204, #134/#809, #421, #91 mode 2).

It also resolves the PRD's two residual open questions: the capped/sampled persistence mechanics for very large previews (Open Question 1) and the dispatch cost-estimation heuristic (Open Question 3). Both are proposed in Technical Architecture below.

## Business Value

- De-risks the most dangerous administrative actions in JIM: scope changes, destructive toggles, partition deselection, and deletion settings stop being applied blind.
- One shared experience and one shared codebase instead of bespoke previews per surface; each subsequent adapter is a thin, cheap addition.
- Grouped and pattern-based summaries let an administrator assure a multi-thousand-object change in minutes; this is the assurance capability traditional ILM solutions never offered.
- Preview retention gives auditors provable change-control: "previewed at 14:02, applied at 14:05" is reconstructable.

## Technical Architecture

### Existing building blocks (verified in codebase)

| Building block | Where | Reuse |
|---|---|---|
| Worker task dispatch | `WorkerTask` TPH hierarchy (`src/JIM.Models/Tasking/`), `TaskingServer.CreateWorkerTaskAsync`, `Worker.cs` type switch, sync-family processor classes | New `ConfigurationChangePreviewWorkerTask` subclass and processor follow this pattern exactly |
| Activity tracking | `Activity` with `ObjectsToProcess`/`ObjectsProcessed`/`Message`, status transitions via `ActivityServer`, existing configuration-change apparatus (`ConfigurationChangeSnapshot` jsonb, `ChangeReason`, `ConfigurationChangeVersion`, per-target-type columns) | Preview runs are Activities; the config-change columns identify the target surface without new schema |
| Evaluate-then-execute pattern | `MetaverseServer.Evaluate*Async` methods (#465); execute methods re-call the same evaluation and abort on hard blocks | The adapter contract generalises this pattern; apply paths re-check stage 1 validation |
| Count-level preview | `ConnectedSystemServer.GetDeletionPreviewAsync` (#135) and the four GET `*-preview` API endpoints | Precedent for stage 2 count queries and preview endpoints; #135 later re-platforms as an adapter |
| Pure sync decision engine | `SyncEngine` (partial class): synchronous, no I/O, plain objects in, decision records out | Stage 4 inbound evaluation calls it directly; no refactor needed inbound |
| Outcome vocabulary | `ActivityRunProfileExecutionItemSyncOutcomeType` (#363) | Transition taxonomy reuses these values (see below) |
| Real-time notification foundation | #307 (PostgreSQL LISTEN/NOTIFY service-to-service, SignalR/Blazor circuit push) and #202, implemented before this framework | The notification abstraction is implemented directly on this foundation; the existing `ActivityDetail.razor` poll pattern survives only in #202's graceful-degradation fallback |

Confirmed gaps the framework must build net-new: there is no shared typed-consequences confirmation dialog (three bespoke copies exist), `SyncRunMode.PreviewOnly` is honoured only in export execution, outbound evaluation (`ExportEvaluationServer`) persists Pending Exports as it evaluates, and no endpoint accepts a proposed-change DTO for a dry run (existing dry runs are GET-by-id deletion previews).

### Component map

```
JIM.Web
  Shared/ConfigurationChangePreviewPanel.razor      UI shell: progress, staged results, summary, drill-down
  Shared/ConsequenceConfirmationDialog.razor        shared apply-time confirmation (extracted, Phase 2)
  Controllers/Api/...                               POST {surface}/preview endpoints (proposed DTO in body)
        |
JIM.Application
  Servers/ConfigurationChangePreviewServer.cs       orchestration: stages, dispatch, persistence, progress
  Servers/Preview/IConfigurationChangePreviewAdapter.cs   per-surface adapter contract
  Servers/Preview/PreviewSummariser.cs              deterministic grouping + pattern detector registry
  SyncEngine / #288 evaluation paths                stage 4 outcome evaluation (read-only)
        |
JIM.Models
  Preview/ConfigurationChangePreview.cs             1:1 with Activity; stage states, estimate, cap choice
  Preview/ConfigurationChangePreviewDelta.cs        queryable per-object delta rows
  Preview/ConfigurationChangePreviewGroup.cs        exact summary groups (always computed, always exact)
  Tasking/ConfigurationChangePreviewWorkerTask.cs   background dispatch payload
        |
JIM.PostgresData                                    DbSets, migration, indexes
JIM.Worker
  Processors/ConfigurationChangePreviewTaskProcessor.cs
```

### Adapter contract

```csharp
public interface IConfigurationChangePreviewAdapter
{
    // Which surface this adapter serves (maps to Activity's config-target columns).
    ConfigurationChangePreviewSurface Surface { get; }

    // Stage 1: structural findings. Always synchronous, near-instant.
    Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context);

    // Dispatch input: cheap set-based estimate of the affected population.
    Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context);

    // Stage 2: per-transition-type counts (set-based SQL only).
    Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context);

    // Stage 4 input: stream per-object outcome deltas (read-only evaluation).
    // The framework consumes this stream to build exact groups (stage 3) and
    // persist delta rows (capped or full).
    IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context, CancellationToken ct);
}
```

`PreviewContext` carries the current persisted configuration, the proposed configuration as an **unsaved DTO** (reusing the surface's existing update DTO where one exists), and the initiator triad. Not every surface implements every stage; `CountImpactAsync` is the minimum for the destructive surfaces (PRD FR3), and `EvaluateDeltasAsync` may return an empty stream for count-only adapters.

Adapters are registered with the framework at startup (simple registry keyed by `Surface`; no reflection scanning).

### Transition taxonomy

Per the PRD constraint, the object-level vocabulary reuses #363's `ActivityRunProfileExecutionItemSyncOutcomeType` rather than a parallel enum. The preview-specific transitions that have no sync-time equivalent (fell in-scope, fell out-of-scope, would become deletion-eligible) are added to that enum as new values (additive, no renumbering). Delta rows store the outcome type plus a `WouldOccur` semantic implied by context; no separate "preview outcome" enum is introduced.

### Result persistence

Three new tables (all rows FK to the preview's Activity, so RPEI-retention housekeeping cascades naturally):

- **`ConfigurationChangePreviews`** (1:1 with Activity): surface, stage statuses (per-stage `NotStarted/InProgress/Complete/Failed` + timestamps), the proposed-configuration DTO snapshot (jsonb, mirrors `Activity.ConfigurationChangeSnapshot`), estimated row count and bytes, the administrator's cap choice, staleness baseline (max last-import/last-sync timestamps of the systems concerned at generation time).
- **`ConfigurationChangePreviewGroups`**: grouping dimensions (transition type, object type id/name snapshot, attribute name, low-cardinality old-to-new value pair, pattern key), exact count, and whether drill-down rows for the group are complete or sampled. **Always exact regardless of capping.**
- **`ConfigurationChangePreviewDeltas`**: transition type, MVO id / CSO id / Connected System id (nullable as applicable), object type and display-name snapshots (render without joins after objects change or delete), attribute name, old value, new value, pattern key (populated from v1; detectors arrive later), group FK. Indexed on `(ActivityId, GroupId)` and `(ActivityId, TransitionType)`.

Old/new values are attribute values and therefore personal data: same protection posture as RPEI change data, never logged, honoured by the existing RPEI retention housekeeping (the deletion job gains these tables).

Apply-side linkage: `Activity` gains a nullable `PreviewActivityId` FK. When a previewed change is applied, the apply Activity references the preview Activity (PRD FR15); "applied blind" is a null.

### Dispatch (resolves PRD Open Question 3)

- Stage 1 validation always runs synchronously in JIM.Web's request path; findings render immediately.
- The framework then calls `EstimateCostAsync`. The estimate is the affected population count from cheap set-based SQL (the same counts stage 2 needs; they are computed once and reused).
- **v1 heuristic: a single threshold on estimated affected population, default 2,500 objects, stored as a service setting (admin-tunable in the UI, per the minimise-env-vars principle).** At or below: stages 2 to 4 run as a background task inside JIM.Web's process (still tracked by the Activity, so the UI path is identical). Above: a `ConfigurationChangePreviewWorkerTask` is queued and JIM.Worker executes the same orchestration code.
- Every preview Activity records measured elapsed time per stage. This gives real data to tune the threshold later; no adaptive or learned behaviour in v1.
- Both paths write the same rows and the same Activity progress fields; the UI cannot tell them apart (PRD Scenario 6).

### Capped/sampled persistence for very large previews (resolves PRD Open Question 1)

- **Size estimate:** estimated delta rows = affected population from `EstimateCostAsync` multiplied by the adapter's declared average deltas-per-object (a per-adapter constant, e.g. 1 for scope transitions, N for attribute-flow changes). Estimated disk = rows x 400 bytes (mid-point of the 300 to 500 bytes/row sizing agreed on #827).
- **Recommendation threshold:** when estimated rows exceed 100,000 (roughly 40 MB), the panel presents the informed choice before generation: estimated row count and disk consumption stated plainly, capped data set recommended as the default, full data set selectable (PRD Scenario 5). Below the threshold, generation proceeds without a prompt. The threshold is a service setting with the 100,000 default.
- **Cap mechanics:** evaluation always processes the **full population**; group counts are computed exactly from the stream either way. Capping affects only which delta rows persist: the first 1,000 deltas per summary group (deterministic order, by object id) are kept; the remainder increment the group's exact count only. Groups whose rows were truncated are flagged, and their drill-down lists carry the "sampled" label (PRD FR4). Per-group capping guarantees every group remains drillable; a global cap would let one huge group starve the rest.
- 1,000 rows per group is a constant in v1 (not a setting); revisit only if real usage demands it.

### Progress notification abstraction

`IPreviewProgressNotifier` in JIM.Application with two operations: `PublishStageChangedAsync(activityId, stage, status)` (called by the orchestrator) and a consumer-side subscription the UI shell uses. Because #307/#202 are implemented **before** this framework (decided Jul 2026; #307 blocks #827), the implementation is real-time from day one: publish issues a PostgreSQL `NOTIFY` carrying only the Activity id (per #307's 8KB-payload-avoidance design: notify identity, fetch state), JIM.Web's LISTEN service receives it, and the panel is pushed fresh state over the Blazor circuit. When the notification path is unavailable (dropped LISTEN connection), the subscription degrades to the database polling fallback that #202 defines; no separate polling-first implementation is built for previews.

### API shape

Net-new pattern (verified: no existing endpoint accepts a proposed-change DTO):

- `POST /api/v1/{surface-route}/{id}/preview` with the proposed configuration DTO in the body; returns `202 Accepted` with the preview Activity id (or `200` with inline results when stage 1 fails hard).
- `GET /api/v1/previews/{activityId}` returns stage statuses, validation findings, impact counts, and summary groups.
- `GET /api/v1/previews/{activityId}/deltas?groupId=&search=&page=` server-side paginated drill-down.
- `DELETE /api/v1/previews/{activityId}` cancels a running preview.

Authorisation mirrors the configuration change itself (PRD NFR): the preview endpoint carries the same `[Authorize]` policy as the surface's update endpoint.

### UI shell

`ConfigurationChangePreviewPanel.razor` (shared, in `JIM.Web/Shared/`): opened by any surface's edit page, fills in as stages complete. Progress via the notifier subscription; summary groups as the landing view for large sets; drill-down as a server-side `MudDataGrid` with text search and dimension filters; cancel button; staleness and sampled labels; the informed-choice cap prompt. Surfaces embed it with a one-line component reference plus their adapter's surface key and proposed DTO.

## Implementation Phases

Phase 0 completes before any #827 work begins (decided Jul 2026; #307 blocks #827). Phases 1 and 2 can then proceed in parallel; Phase 3 needs neither until its final stage-4 step. Adapter waves (Phase 5) are follow-up issues, not part of this plan's direct scope.

### Phase 0: Real-time notification foundation (#307, then #202; separate issues, sequenced first)

Scope belongs to those issues; this plan defines only what the framework consumes:

- [ ] #307 Phase 1 (PostgreSQL LISTEN/NOTIFY service-to-service) and Phase 2 foundation (SignalR/Blazor circuit push in JIM.Web), including the graceful-degradation polling fallback.
- [ ] #202 Run Profile progress push, the first feature slice proving the foundation.
- [ ] Contract the framework consumes: publish a channel notification carrying an Activity id; JIM.Web-side subscription that pushes to Blazor components; documented fallback behaviour when the LISTEN connection drops.

### Phase 1: #288 engine core (separate issue)

The other true build dependency. Scope belongs to #288; this plan defines only what the framework consumes:

- [ ] Inbound: `SyncEngine` is already a pure decision engine; expose an orchestration path that evaluates projection, join, and Attribute Flow decisions for a given CSO/MVO population **without persisting**, returning decision records.
- [ ] Outbound: extract an evaluation-only path from `ExportEvaluationServer` (today it stages Pending Exports as it evaluates); generalise `SyncRunMode.PreviewOnly` beyond export execution so the mode means "evaluate, never persist" across the pipeline.
- [ ] Contract: the evaluation surface consumed by `EvaluateDeltasAsync` implementations; streaming (page-at-a-time) so previews never materialise whole populations in memory.

### Phase 2: Apply-time messaging across all surfaces (PRD FR17)

Permanent end-state components, built once, rolled everywhere; adapters later layer previews on top.

- [ ] Extract the shared `ConsequenceConfirmationDialog.razor` from the three bespoke copies (`DeleteMetaverseAttributeDialog`, `DeleteMetaverseObjectTypeDialog`, `ConnectedSystemDangerZoneTab`): consequence list, optional counts, optional type-the-name confirmation; migrate the three existing callers to it (behaviour-preserving refactor, verified against existing UI flows).
- [ ] "Configuration changed since last full synchronisation" indicator: driven off the existing configuration-change Activity columns (latest config-change Activity per target vs the last completed full synchronisation for the systems concerned); shared badge component surfaced on affected object types/systems.
- [ ] Roll the acknowledgement flow across the sync-affecting surfaces catalogued on #827 (save-time acknowledgement of consequences plus the recommendation to run a full synchronisation).
- [ ] Coordinate with the #91 plan (`engineering/plans/doing/ATTRIBUTE_PRIORITY.md` mode 1) so both consume these same components.
- [ ] Tests: component behaviour tests where practicable; unit tests for the changed-since determination logic.

### Phase 3: Framework foundations

- [ ] **Models and persistence:** `ConfigurationChangePreview`, `ConfigurationChangePreviewGroup`, `ConfigurationChangePreviewDelta`; extend `ActivityRunProfileExecutionItemSyncOutcomeType` with the scope/deletion-eligibility transitions; `Activity.PreviewActivityId`; DbSets, indexes, EF migration.
- [ ] **Adapter contract and registry:** `IConfigurationChangePreviewAdapter`, `PreviewContext`, finding/count/estimate/delta records; startup registration keyed by surface.
- [ ] **Orchestration server:** `ConfigurationChangePreviewServer` running the stage sequence, computing exact groups from the delta stream, applying the per-group cap, updating stage statuses and Activity progress, failing fast and visibly on any stage error (a failed preview never presents partial results as complete).
- [ ] **Dispatch:** cost-estimate threshold service setting; in-process background path; `ConfigurationChangePreviewWorkerTask` + `TaskingServer.CreateWorkerTaskAsync` branch + `Worker.cs` case + `ConfigurationChangePreviewTaskProcessor` (sync-family processor pattern); cancellation via the task's cancellation source.
- [ ] **Notification abstraction:** `IPreviewProgressNotifier` implemented on the Phase 0 foundation (NOTIFY on stage transitions, circuit push to the panel), degrading to #202's polling fallback when the notification path is down.
- [ ] **Retention:** RPEI retention housekeeping extended to the three preview tables; preview Activity linkage verified in the apply paths.
- [ ] **API:** the four endpoints above, authorised per-surface; PowerShell cmdlet deferred to the first adapter.
- [ ] **UI shell:** `ConfigurationChangePreviewPanel.razor` with progress, staged arrival, summary landing view, drill-down grid, cancel, staleness and sampled labels, cap prompt.
- [ ] **Tests (TDD throughout):** orchestrator stage sequencing and failure paths; grouping correctness incl. cap-vs-exact-count invariants; dispatch threshold decision; worker task lifecycle; API contract tests. A `FakePreviewAdapter` test double drives framework tests without any real surface.

### Phase 4: Summarisation depth

- [ ] **4a Deterministic grouping (v1, ships with Phase 3's landing view):** group by transition type, object type, attribute, and distinct old-to-new value pairs where cardinality is low; cardinality guard so high-cardinality pairs collapse into the attribute-level group.
- [ ] **4b Pattern detector registry (fast-follow):** detector interface (delta in, pattern key or null out); initial curated detectors: email/UPN domain swap, DN parent path change (OU move), casing change, common prefix/suffix addition or removal. Each detector individually unit-tested with positive and negative cases; a detector that cannot classify stays silent. The `PatternKey` column exists from Phase 3, so 4b needs no migration.

### Phase 5: Adapter waves (follow-up issues, split per #827 acceptance criteria)

Each wave is one or more GitHub issues drafted for sign-off before filing; each adapter is a thin implementation of the contract plus its surface's panel embedding and confirmation flow.

- [ ] **Wave 1:** G5 deletion settings and G3 destructive toggles. **G5 is the pilot adapter that proves the framework end-to-end**; Phase 3 is not "done" until it ships.
- [ ] **Wave 2:** G4 partition/container deselection.
- [ ] **Wave 3:** G1/G2 (Synchronisation Rule scope and Attribute Flow changes; the heaviest evaluation, fully dependent on #288).
- [ ] **Wave 4:** G6 and remaining toggles; re-scope #204, #134/#809, #421, and #91 mode 2 as adapter issues.

## Success Criteria

- One preview panel component serves every adapter; no per-surface preview UI beyond embedding it
- A preview on the pilot adapter (G5) delivers stages progressively with no administrator-facing execution choice, and behaves identically dispatched in-process vs via JIM.Worker
- Grouped summary counts are exact even when delta persistence is capped, and capped drill-downs are labelled sampled
- A 100K+ object preview completes without degrading JIM.Web for other users
- Apply Activities reference their preview Activity; preview results survive per the RPEI retention period and are reconstructable for audit
- Every surface without an adapter shows the acknowledgement flow and the changed-since indicator (Phase 2)
- Zero build warnings; all new logic TDD-first per repo rules

## Dependencies

- **#307, then #202** (real-time notification foundation): **blocking; implemented first** (decided Jul 2026, #307 blocks #827 on GitHub; revised from the earlier polling-first, non-blocking stance). The notifier is real-time from day one
- **#288** (engine core): Phase 1; blocks stage 4 evaluation and Wave 3 adapters, not framework plumbing
- **#363** `SyncOutcome` model: shipped; taxonomy extended additively
- **#91**: shares the Phase 2 components; coordinate, do not duplicate

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Extending the #363 outcome enum ripples into existing sync reporting | Additive values only, no renumbering; grep all switch sites over the enum and add explicit handling; tests assert existing values unchanged |
| Outbound evaluation extraction (#288) destabilises export staging | Behaviour-preserving refactor with the existing export integration tests as the safety net before any preview path consumes it |
| Preview tables grow faster than expected at customer scale | Informed-choice cap defaults on above 100K rows; RPEI retention housekeeping covers the tables from day one; per-group cap bounds worst case |
| In-process dispatch path degrades JIM.Web under load | Conservative default threshold (2,500), admin-tunable; per-stage elapsed-time telemetry recorded from v1 to tune with real data |
| #307/#202 (Effort: High) delay the start of #827 | Accepted trade-off (decided Jul 2026): clearly defined units of work, no polling-first throwaway and no later swap to forget; #288 engine work can proceed in parallel with Phase 0 since neither depends on the other |
| Dropped LISTEN connection leaves the preview panel stale | #202's graceful-degradation polling fallback; the DB remains the source of truth per #307 |
| Shared confirmation dialog refactor breaks existing delete flows | Phase 2 migrates the three callers behaviour-preservingly and verifies each flow at runtime in the devcontainer stack |
| Framework built speculative-shaped, wrong for real surfaces | G5 pilot adapter gates Phase 3 completion; contract only frozen once the pilot ships |
