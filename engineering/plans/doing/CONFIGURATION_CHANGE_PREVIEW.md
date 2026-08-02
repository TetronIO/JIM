# Configuration Change Preview Framework - Implementation Plan

- **Status:** Doing (Phases 0 and 2 complete; Phase 3 underway: persistence, the adapter contract, the orchestration server, dispatch and the read/cancel API have landed, the panel next. Phase 3 is not done until the #1114 pilot adapter ships on it)
- **Created:** 2026-07-20
- **Issue:** [#827](https://github.com/TetronIO/JIM/issues/827)
- **PRD:** [PRD_CONFIGURATION_CHANGE_PREVIEW.md](../../prd/doing/PRD_CONFIGURATION_CHANGE_PREVIEW.md)

## Overview

One Configuration Change Preview framework, consumed by thin per-surface adapters. An administrator requests a preview of an unsaved configuration change; a panel opens immediately and fills in through progressive stages (validation, impact counts, grouped change summary, object-level drill-down) as each completes. Where the computation runs (JIM.Web in-process or JIM.Worker) is an invisible dispatch decision. Results persist as queryable rows attached to an Activity for pagination, summarisation, and audit.

This plan sequences the work per the decisions recorded on #827 and in the PRD:

1. **#307 real-time notification foundation, then #202** (its first slice), before any #827 work (decided Jul 2026; #307 blocked #827). ✅ **Both delivered and closed, Jul 2026.** The framework's progress notification is real-time from day one; no polling-first implementation is built, and no preview-specific notifier is needed (it consumes `IUiNotificationService` directly).
2. **#288 engine core** (the other true build dependency), in parallel with framework plumbing that does not need it.
3. **Apply-time messaging** (PRD FR17) as an early framework phase, rolled across all sync-affecting surfaces before any adapter exists.
4. **Framework foundations** (models, persistence, orchestration, dispatch, notification, UI shell, summarisation), proven end-to-end by the first adapter.
5. **Adapter waves** as follow-up issues in severity order: G5 (#1114) and G3-destructive (#1115) filed Jul 2026, then G4, then G1/G2, then G6 and the re-scoped issues (#204, #134/#809, #421, #91 mode 2).

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
| Real-time notification foundation | `IUiNotificationService`, `NotificationListenerService`, `JimNotificationHub` and the `jim_activity_progress` trigger, delivered by #307/#202 (Jul 2026) | Consumed directly: the panel subscribes to `ActivityProgressChanged` and uses `IsRealTimeAvailable` to pick its fallback polling interval. No preview-specific notifier is written |

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

### Change classification and surface migration

Preview scope is **property-level, not page-level**. A single edit page mixes harmless fields with dangerous ones (renaming a Synchronisation Rule sits beside its `OutboundDeprovisionAction` dropdown), so each adapter declares a **sync-affecting property map** for its surface, assigning every property one of three classes:

| Class | Meaning | Save-time behaviour | Examples |
|---|---|---|---|
| A: destructive | Can cascade deletions or mass deprovisioning | Preview stages 1 and 2 minimum plus a count-stating confirmation, mandatory (PRD FR3) | G3 destructive toggles, G5 deletion settings, G4 partition deselection |
| B: sync-affecting | Changes sync outcomes without direct destruction | Preview offered via the panel; save not blocked | Scoping criteria, Object Matching Rules, Attribute Flow mappings, schema selection |
| C: cosmetic/operational | No sync-outcome impact | Never prompts; save proceeds untouched | Names, descriptions, schedule timing, `MaxExportParallelism` (matches #827's excluded list) |

On save or preview request, the framework diffs the current configuration against the proposed DTO and classifies the change by the **highest class among the properties that actually changed**; a save touching only Class C fields never sees a preview, acknowledgement, or confirmation. The classification hook is installed in every sync-affecting save path during Phase 2 (where it also gates the interim acknowledgement, so cosmetic edits never trigger that either) and is reused unchanged when the surface's adapter arrives.

**Migration model:** existing configuration-change interfaces move onto the framework in two passes, both already sequenced in this plan:

1. **Phase 2 (all surfaces at once):** every sync-affecting save path gains the classification hook, the acknowledgement flow, and the changed-since indicator. This is deliberately shallow per surface (no preview yet) so it can cover the whole #827 coverage map in one phase.
2. **Phase 5 (one surface per adapter issue):** each adapter issue owns the full migration of its surface's edit UI: embed the preview panel, wire the surface's proposed-configuration DTO, add the preview API endpoint, and replace the interim acknowledgement with the preview-driven confirmation (the changed-since indicator remains permanently). **An adapter is not done until its surface is migrated**; the definition of done for every Wave issue includes the UI migration, not just the adapter class.

There is no big-bang migration and no orphaned interim state: surfaces not yet migrated keep the Phase 2 interim messaging, which is exactly the mechanism PRD FR17 provides to make incremental migration safe. The inventory of surfaces to migrate is #827's coverage map (the existing-issue surfaces plus gaps G1 to G6); the excluded list in #827 defines what never migrates.

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

- **Size estimate:** estimated delta rows = affected population from `EstimateCostAsync` multiplied by the adapter's declared average deltas-per-object (a per-adapter constant, e.g. 1 for scope transitions, N for attribute-flow changes). Estimated storage = rows x 400 bytes (mid-point of the 300 to 500 bytes/row sizing agreed on #827).
- **Recommendation threshold:** when estimated rows exceed 100,000 (roughly 40 MB), the panel presents the informed choice before generation: estimated row count and storage consumption stated plainly, capped data set recommended as the default, full data set selectable (PRD Scenario 5). Below the threshold, generation proceeds without a prompt. The threshold is a service setting with the 100,000 default.
- **Cap mechanics:** evaluation always processes the **full population**; group counts are computed exactly from the stream either way. Capping affects only which delta rows persist: the first 1,000 deltas per summary group (deterministic order, by object id) are kept; the remainder increment the group's exact count only. Groups whose rows were truncated are flagged, and their drill-down lists carry the "sampled" label (PRD FR4). Per-group capping guarantees every group remains drillable; a global cap would let one huge group starve the rest.
- 1,000 rows per group is a constant in v1 (not a setting); revisit only if real usage demands it.

### Progress notification (no new abstraction needed)

**Revised Jul 2026 after #307/#202 shipped.** The earlier design proposed a preview-specific `IPreviewProgressNotifier`; the delivered foundation makes it redundant. Previews are Activities, and #307 already notifies on Activity progress, so the framework consumes what exists:

- **Publish side:** nothing preview-specific. The `trg_activities_notify_progress` trigger (migration `20260723204302_AddRealTimeNotificationTriggers`) raises `pg_notify('jim_activity_progress', Id)` on every Activity UPDATE that changes `Status`, `ObjectsProcessed`, `ObjectsToProcess`, or `Message`.
- **Design constraint this imposes:** stage transitions MUST be written to those Activity columns, not only to the `ConfigurationChangePreviews` row. An orchestrator that recorded stage status solely on the preview table would fire no notification and leave the panel silent. Stage progression therefore updates `Activity.Message` (the stage label) and `Status`, with `ObjectsProcessed`/`ObjectsToProcess` carrying evaluation progress during stages 3 and 4.
- **Consume side:** the panel injects `IUiNotificationService` (JIM.Web) and subscribes to `ActivityProgressChanged`, filtering on its own preview Activity id, then re-queries via the application layer. Notifications are hints, not data.
- **Fallback:** `IsRealTimeAvailable` plus `RealTimeAvailabilityChanged` select the polling interval (slow reconciliation poll when real-time is up, fast poll when down) and trigger an immediate refresh on reconnection. This is the same pattern the migrated `OperationsQueueTab` and `WorkerTaskProgress` components use; the panel copies it rather than inventing one.
- Burst coalescing is already handled upstream: `NotificationListenerService` debounces Activity progress over a 200 ms quiet window, so a fast-streaming evaluation cannot flood the panel with re-renders.

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

Phase 0 completed before any #827 work began (decided Jul 2026; #307 blocked #827; both delivered Jul 2026). Phases 1 and 2 can proceed in parallel; Phase 3 needs neither until its final stage-4 step. Adapter waves (Phase 5) are follow-up issues, not part of this plan's direct scope.

### Phase 0: Real-time notification foundation (#307, then #202; separate issues, sequenced first) ✅

Delivered Jul 2026 (#307 via PR #1107, #202 via PR #1111; both issues closed). What the framework now consumes, as built:

- [x] PostgreSQL LISTEN/NOTIFY triggers on `Activities` and `WorkerTasks` (migration `20260723204302_AddRealTimeNotificationTriggers`), channels named in `Constants.NotificationChannels`.
- [x] `NotificationListenerService` (JIM.Web `BackgroundService`) bridging notifications into the in-process `IUiNotificationService` relay and the `JimNotificationHub` SignalR hub, with 200 ms debouncing of Activity progress bursts and contained failure paths.
- [x] `IUiNotificationService`: `ActivityProgressChanged` (Activity id), `WorkerTaskChanged`, `IsRealTimeAvailable`, `RealTimeAvailabilityChanged`.
- [x] Graceful degradation: every consumer retains a polling fallback selected by `IsRealTimeAvailable`, with an immediate reconciliation refresh on reconnection.

Consequence for this plan: no preview-specific notifier is built (see Progress notification above); the framework subscribes to `ActivityProgressChanged` and must drive stage progress through the Activity columns the trigger watches.

### Phase 1: #288 engine core (separate issue)

The other true build dependency. Scope belongs to #288; this plan defines only what the framework consumes:

- [ ] Inbound: `SyncEngine` is already a pure decision engine; expose an orchestration path that evaluates projection, join, and Attribute Flow decisions for a given CSO/MVO population **without persisting**, returning decision records.
- [ ] Outbound: extract an evaluation-only path from `ExportEvaluationServer` (today it stages Pending Exports as it evaluates); generalise `SyncRunMode.PreviewOnly` beyond export execution so the mode means "evaluate, never persist" across the pipeline.
- [ ] Contract: the evaluation surface consumed by `EvaluateDeltasAsync` implementations; streaming (page-at-a-time) so previews never materialise whole populations in memory.

### Phase 2: Apply-time messaging across all surfaces (PRD FR17)

Permanent end-state components, built once, rolled everywhere; adapters later layer previews on top.

- [x] Extract the shared `ConsequenceConfirmationDialog.razor` from the three bespoke copies (`DeleteMetaverseAttributeDialog`, `DeleteMetaverseObjectTypeDialog`, `ConnectedSystemDangerZoneTab`): consequence list, optional counts, optional type-the-name confirmation; migrate the three existing callers to it (behaviour-preserving refactor, verified against existing UI flows). **Delivered #1130.**
- [x] "Configuration changed since last full synchronisation" indicator: driven off the existing configuration-change Activity columns (latest config-change Activity per target vs the last completed full synchronisation for the systems concerned); shared badge component surfaced on affected object types/systems. **Delivered #1143** (`ConfigurationDriftService`, `ConfigurationDriftIndicator.razor`), across portal, REST and PowerShell.
- [x] Install the change-classification hook (per-surface sync-affecting property maps; see Change classification and surface migration) in every sync-affecting save path, so only Class A/B changes trigger any messaging; cosmetic edits are untouched. **Delivered #1140**, and deliberately not per-surface: classification keys off the snapshot node key, which is the one representation every write surface already produces, so portal, REST and PowerShell are covered by construction. See `engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md`.
- [x] Roll the acknowledgement flow across the sync-affecting surfaces catalogued on #827 (save-time acknowledgement of consequences plus the recommendation to run a full synchronisation), gated by the classification hook. **Delivered.** `ConfigurationChangePreflightService` + `ConfigurationChangeAcknowledgementDialog.razor`, wired to every sync-affecting portal save path: the Synchronisation Rule editor (pilot), the four Connected System tabs (Details, Settings, Schema, Partitions & Containers), Metaverse Object Type deletion settings, the Deprovisioning Action dropdown on that same page (which edits Synchronisation Rules without going near the rule editor), the Metaverse Attribute editor, and Service Setting edit and revert. The preflight baseline is the object's latest **captured snapshot**, not a re-read of the entity: the edit surfaces mutate the entity they loaded and save it on the same context, so re-reading would return the mutated instance and the diff would come back empty. It also guarantees the acknowledgement and the class written to history are computed from one comparison. Portal-only by decision (Jul 2026): a REST or PowerShell caller names the property it is setting, so consent is already explicit.
  - Rolling it out exposed three live classification gaps the completeness guard had missed because its fixtures left the relevant collections empty: Connected System setting values (keyed in the snapshot by the *connector's own setting name*, an open third-party key space the classifier could never enumerate, so every settings save recorded no class and never raised the changed-since indicator), Simple Mode Object Matching Rules on a Connected System, and individual entries in a Metaverse Object Type's deletion-trigger list. Setting values now use one stable node key with the setting name as the label; the other two are classified; the fixtures are populated so the guard actually guards. See `engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md`.
- [x] Coordinate with the #91 plan (`engineering/plans/doing/ATTRIBUTE_PRIORITY.md` mode 1) so both consume these same components. **Delivered:** `AttributePriorityList.razor`'s bespoke `ShowMessageBoxAsync` acknowledgement now goes through the shared `ConsequenceConfirmationDialog`, so a priority reorder looks and behaves like every other sync-affecting save. It deliberately keeps its own copy rather than adopting the preflight: priority order lives on `SyncRuleMapping` rows across several Synchronisation Rules, so a snapshot diff would be per-rule and could not name the Connected Systems that must be synchronised, which is the part of the advice that actually helps.
- [x] Tests: component behaviour tests where practicable; unit tests for the changed-since determination logic. `ConfigurationDriftServiceTests` (changed-since), `ConfigurationChangePreflightServiceTests` and `ConfigurationChangePreflightSurfaceTests` (per-surface preflight), `ConfigurationChangePreflightDatabaseTests` (`RequiresPostgres`, proving the snapshot baseline survives the tracked-context trap that a re-read does not), `ConfigurationChangeAcknowledgementDialogTests` (bUnit), and `ConfigurationChangeClassificationCompletenessTests` (the classification and consequence-copy guard).

### Phase 3: Framework foundations

- [x] **Models and persistence:** `ConfigurationChangePreview`, `ConfigurationChangePreviewGroup`, `ConfigurationChangePreviewDelta`; extend `ActivityRunProfileExecutionItemSyncOutcomeType` with the scope/deletion-eligibility transitions; `Activity.PreviewActivityId`; DbSets, indexes, EF migration. **Delivered.** The preview's primary key *is* its Activity id, so the 1:1 cannot drift and all three tables cascade from the Activity, which is the whole retention story for preview data (`ConfigurationChangePreviewPersistenceDatabaseTests` asserts the cascade against real PostgreSQL; the in-memory provider enforces no referential actions and would pass with it absent). `Activity.PreviewActivityId` is a plain column rather than a foreign key, matching `ParentActivityId`: a preview ages out under retention long before the change it informed, and an FK would either block that cleanup or null the link. The four new outcome types are appended, and `SyncOutcomeTypeOrdinalTests` pins every ordinal so a later reorder fails the build rather than silently re-labelling historical outcome rows.
- [x] **Adapter contract and registry:** `IConfigurationChangePreviewAdapter`, `PreviewContext`, finding/count/estimate/delta records; startup registration keyed by surface. **Delivered.** `ConfigurationChangePreviewSurface` is deliberately narrower than `ActivityTargetType` (which covers operational work no adapter could preview), with a mapping between them that the model tests hold to being total and injective. The registry refuses two adapters for one surface rather than letting the last registration win, because that failure mode is a confident preview produced by the wrong evaluator with nothing in a log to say so.
- [x] **Orchestration server:** `ConfigurationChangePreviewServer` running the stage sequence, computing exact groups from the delta stream, applying the per-group cap, updating stage statuses and Activity progress, failing fast and visibly on any stage error (a failed preview never presents partial results as complete). **Delivered**, with `PreviewSummariser` owning grouping and capping and `IConfigurationChangePreviewRepository` owning persistence. Four decisions worth recording:
  - **Results are persisted only once the delta stream has completed.** Writing groups as they were discovered would leave a preview that died at 60% looking like one that finished, with counts to match; `ConfigurationChangePreviewServerTests` holds this by asserting nothing is persisted when evaluation throws mid-stream. Both invariants (this one and exact-counts-under-capping) were mutation-checked: each test was proven to fail against a deliberately broken implementation before being kept.
  - **Adapters declare `ProducesDeltas` rather than the framework inferring it from an empty stream.** "This adapter does not evaluate objects" and "this change would affect nothing" are opposite answers, and an administrator reading an empty drill-down has to know which one they are looking at. The first records the stages as not applicable; the second records them complete.
  - **A cancelled stage is `Cancelled`, not `Failed`** (new stage status): nothing went wrong, and showing it as failed sends somebody looking for an error that was never raised.
  - **Validation findings and impact counts are jsonb documents on the preview row, not tables.** Nothing queries across previews for a finding or a count; both are read as a set with the row that owns them, so a table would buy indexing nobody uses at the cost of a join on every panel refresh. Note that jsonb normalises what it stores (whitespace, key order), so these columns are equal-as-documents, never equal-as-strings; the round-trip test compares them accordingly.
  - **Deferred deliberately:** `StalenessBaseline` is still written null. It is "the most recent import or synchronisation across the Connected Systems the preview depends on", and which systems those are is knowledge only an adapter has; the contract has no way to declare them yet. Sampling every Connected System instead would produce false "stale" labels, which is worse than no label. It lands with the panel that consumes it, once the pilot adapter shows what declaring a dependency should look like.
  - **A preview Activity's operation type is the new `ActivityTargetOperationType.Preview`**, not `Read` and emphatically not `Update`: the Activity list is where an administrator establishes what was actually done to the system.
- [x] **Dispatch:** cost-estimate threshold service setting; in-process background path; `ConfigurationChangePreviewWorkerTask` + `TaskingServer.CreateWorkerTaskAsync` branch + `Worker.cs` case + `ConfigurationChangePreviewTaskProcessor` (sync-family processor pattern); cancellation via the task's cancellation source. **Delivered** as `StartAndDispatchPreviewAsync`, the single entry point a surface calls: where a preview runs is a capacity decision the framework makes, not one a caller can get wrong. Threshold is `Preview.WorkerThreshold` (default 2,500 affected objects), classified Class C because it decides where a preview is evaluated and never what it reports. Three decisions:
  - **Adapters declare a `ProposalType`, and the framework serialises against it.** A proposal is an unsaved object living in the caller's memory, and crossing a process boundary is the one thing it cannot do by itself. The payload deliberately carries no type name: the worker resolves the type from the adapter registered for the task's surface, so a tampered queue row can only ever deserialise into the type that surface expects. The cost is a real constraint on adapters (a proposal must survive a JSON round trip, so no entity graphs), stated in the contract rather than discovered at dispatch time.
  - **The preview worker task is the only task type that does not create its own Activity.** Validation already ran in the request path and recorded its findings; a second Activity would split one preview in two and leave the panel watching the wrong one. That makes it also the only task whose insert must track its Activity first, or `Add()` walks the graph and fails on the Activities primary key. `ConfigurationChangePreviewRepositoryDatabaseTests` proves it against real PostgreSQL; removing the guard fails that test with exactly that duplicate-key error.
  - **The in-process runner holds nothing worth surviving a restart.** A preview interrupted by a shutdown leaves an Activity to be recovered like any other, and the administrator asks again; anything valuable enough to need durability is large enough that the threshold sends it to JIM.Worker in the first place. It runs at most two previews at once, because JIM.Web's job is serving requests.
- [ ] **Progress notification:** no new abstraction; the orchestrator drives stage progression through the Activity columns the `jim_activity_progress` trigger watches, and the panel subscribes to `IUiNotificationService.ActivityProgressChanged` with the `IsRealTimeAvailable` polling fallback.
- [ ] **Retention:** RPEI retention housekeeping extended to the three preview tables; preview Activity linkage verified in the apply paths.
- [x] **API:** the four endpoints above, authorised per-surface; PowerShell cmdlet deferred to the first adapter. **Three of the four delivered** as `PreviewsController` (`GET /previews/{activityId}`, `GET /previews/{activityId}/deltas`, `DELETE /previews/{activityId}`), Administrator-authorised. The fourth, `POST {surface}/preview`, is **per-surface by construction and belongs to each adapter issue**, not here: its request body is the surface's own update type, and a single generic start endpoint would have to accept a body whose type it could only learn from the request itself, which is the exact shape the queued-payload design refuses. Two behaviours worth naming: a malformed stored findings or counts document yields an empty list rather than a failed request, because the rest of the preview is still worth showing and the stage status already says what happened; and cancelling a preview that has already finished returns 409, not 404, because the preview and its results are still there to read.
- [ ] **UI shell:** `ConfigurationChangePreviewPanel.razor` with progress, staged arrival, summary landing view, drill-down grid, cancel, staleness and sampled labels, cap prompt.
- [ ] **Tests (TDD throughout):** orchestrator stage sequencing and failure paths; grouping correctness incl. cap-vs-exact-count invariants; dispatch threshold decision; worker task lifecycle; API contract tests. A `FakePreviewAdapter` test double drives framework tests without any real surface.

### Phase 4: Summarisation depth

- [ ] **4a Deterministic grouping (v1, ships with Phase 3's landing view):** group by transition type, object type, attribute, and distinct old-to-new value pairs where cardinality is low; cardinality guard so high-cardinality pairs collapse into the attribute-level group.
- [ ] **4b Pattern detector registry (fast-follow):** detector interface (delta in, pattern key or null out); initial curated detectors: email/UPN domain swap, DN parent path change (OU move), casing change, common prefix/suffix addition or removal. Each detector individually unit-tested with positive and negative cases; a detector that cannot classify stays silent. The `PatternKey` column exists from Phase 3, so 4b needs no migration.

### Phase 5: Adapter waves (follow-up issues, split per #827 acceptance criteria)

Each wave is one or more GitHub issues drafted for sign-off before filing. Each adapter issue is a thin implementation of the contract **plus the full migration of its surface's edit UI** (panel embedding, proposed-DTO wiring, preview endpoint, interim acknowledgement replaced by the preview-driven confirmation); see Change classification and surface migration for the definition of done.

- [ ] **Wave 1:** G5 deletion settings ([#1114](https://github.com/TetronIO/JIM/issues/1114)) and G3 destructive toggles ([#1115](https://github.com/TetronIO/JIM/issues/1115)), both filed Jul 2026. **G5 is the pilot adapter that proves the framework end-to-end**; Phase 3 is not "done" until it ships. G5 needs no #288 (deletion eligibility is a metaverse-side query), so it can start as soon as Phase 3 lands; G3 needs #288's evaluation-only outbound path.
- [ ] **Wave 2:** G4 partition/container deselection.
- [ ] **Wave 3:** G1/G2 (Synchronisation Rule scope and Attribute Flow changes; the heaviest evaluation, fully dependent on #288).
- [ ] **Wave 4:** G6 and remaining toggles; re-scope #204, #134/#809, #421, and #91 mode 2 as adapter issues.

## Success Criteria

- One preview panel component serves every adapter; no per-surface preview UI beyond embedding it
- A preview on the pilot adapter (G5) delivers stages progressively with no administrator-facing execution choice, and behaves identically dispatched in-process vs via JIM.Worker
- Grouped summary counts are exact even when delta persistence is capped, and capped drill-downs are labelled sampled
- A 100K+ object preview completes without degrading JIM.Web for other users
- Apply Activities reference their preview Activity; preview results survive per the RPEI retention period and are reconstructable for audit
- Every surface without an adapter shows the acknowledgement flow and the changed-since indicator (Phase 2); purely cosmetic edits (e.g. renaming a Synchronisation Rule) never trigger a preview, acknowledgement, or confirmation on any surface
- Each shipped adapter's surface is fully migrated (panel embedded, interim acknowledgement replaced); no surface is left half-migrated
- Zero build warnings; all new logic TDD-first per repo rules

## Dependencies

- **#307, then #202** (real-time notification foundation): **satisfied** (delivered and closed Jul 2026; was blocking). Progress notification is real-time from day one and reuses `IUiNotificationService` rather than adding a preview-specific notifier
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
| ~~#307/#202 (Effort: High) delay the start of #827~~ | Retired: both delivered and closed Jul 2026 |
| Stage progress written only to the preview table fires no notification | The `jim_activity_progress` trigger watches `Status`, `ObjectsProcessed`, `ObjectsToProcess` and `Message` on `Activities` only; the orchestrator drives stage progression through those columns, and a Phase 3 test asserts a stage transition raises a notification |
| Dropped LISTEN connection leaves the preview panel stale | `IsRealTimeAvailable`/`RealTimeAvailabilityChanged` drive the polling fallback and an immediate reconciliation refresh on reconnection; the DB remains the source of truth per #307 |
| Shared confirmation dialog refactor breaks existing delete flows | Phase 2 migrates the three callers behaviour-preservingly and verifies each flow at runtime in the devcontainer stack |
| Framework built speculative-shaped, wrong for real surfaces | G5 pilot adapter gates Phase 3 completion; contract only frozen once the pilot ships |
