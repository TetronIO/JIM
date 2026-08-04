# Causal Provenance: Full-Graph Understanding

- **Status:** Planned
- **Created:** 2026-07-28
- **Author:** JayVDZ (drafted with Claude Code)
- **Issue:** Not yet created.

## Problem Statement

The Activity and Run Profile Execution Item (RPEI) pages, including the Causality views introduced by the in-flight [Causality Visualisation Redesign](doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md) (#1087), show **what happened to one object in one run**. They cannot show **why it happened** (the upward chain of causes) or **what it went on to cause** (the downward chain of consequences). An administrator investigating a change hits a dead end at the edge of the current RPEI.

This PRD is about the **data model and provenance capture** that is missing underneath the visualisation, not about redesigning the views again. #1087 owns presentation (summary sentences, Flow/Timeline/Graph toggles, plain-language labels) for a single RPEI's own outcome tree; this PRD owns linking RPEIs, and the Metaverse Objects and Connected System Objects they describe, **to each other** across Activities, so #1087's surface (and its successors) has a graph to render instead of an island.

### The worked example

A real cascade, verified in the database, used throughout this document:

1. A source record for the Identity "Tina Adams (S8-99)" had an attribute change (`jimEmployeeEndDate`) detected by an import on Connected System "Yellowstone APAC".
2. On the next Full Synchronisation of Yellowstone APAC, Tina no longer satisfied the inbound Synchronisation Rule's scoping criteria, so her Connected System Object left scope and was disconnected (`DisconnectedOutOfScope`).
3. Her Metaverse Object Type's Deletion Rule (`WhenAuthoritativeSourceDisconnected`) then fired, because Yellowstone APAC is an authoritative source. Her Metaverse Object was deleted (`MvoDeleted`, reason "Deletion Rule: authoritative source 'Yellowstone APAC' disconnected").
4. Deleting that Metaverse Object triggered a **reference recall**: JIM found every Metaverse Object still referencing the deleted Identity and staged membership-removal Pending Exports against their target Connected System Objects.
5. Four Groups in Connected System "Glitterband EMEA" (Project-Catalyst, Project-Gateway, Project-Horizon, Project-Pulse) each received one Pending Export containing exactly one change: `REMOVE member = uid=tina.adams99,ou=People,dc=glitterband,dc=local`.

### Why the current UI fails

Looking at the RPEI for the Project-Pulse Group's Pending Export, an administrator sees a membership removal with no local Connected System Object change (correct: nothing changed on the Group in its own source system) and no Metaverse Object change (correct: that Group's Metaverse Object has no change in this run at all). The export appears to have **no cause whatsoever**. The actual cause, Tina's deletion, is invisible. The removed value happens to name her, which is a coincidence of this example, not a general mechanism the UI can lean on. Conversely, on the RPEI for step 1, the original attribute change, there is no indication that it went on to cause anything at all.

**This gets far worse at scale.** With ten deleted Identities instead of one, a single Group's Pending Export shows ten unexplained member removals, with no way to tell they share one root cause.

### The key enabling insight

JIM does not discover this relationship at export time. The removal is staged **proactively, at Metaverse Object deletion time**, by the reference recall step. The worker already knows the cause in-process at the exact moment it creates the effect. Nothing needs to be inferred after the fact; it only needs to be **recorded**.

## Goals

- An administrator can trace any RPEI's complete upward chain of causes back to its ultimate trigger, across Connected Systems and Activities, without manually cross-referencing unrelated Activities. Verifiable: the Project-Pulse Group worked example resolves end-to-end from a single "Caused by" expansion. **(Phase 1)**
- An administrator can see what a given event went on to cause, and the view honestly reflects how much time has passed since the event occurred (no consequences shown before they exist). Verifiable: the same RPEI shows "no consequences yet" moments after creation and the full downstream chain once the cascade has run. **(Phase 2)**
- A cascade with many causes and one effect, or one cause and many effects, renders as a single aggregated, expandable statement, never as a wall of repeated unexplained lines. Verifiable: ten deleted Identities that all reference the same Group render as one summarised cause, not ten. **(Phase 1/2)**
- Provenance capture adds no meaningful risk or overhead to synchronisation. Verifiable: a run deleting 100,000 Metaverse Objects writes causal edges via chunked bulk SQL with no row-at-a-time fallback on the happy path, and an induced edge-write failure still lets the sync it describes complete and record correctly. **(Phase 1)**
- Once retention has purged an ancestor, the chain says so explicitly rather than lying by omission or erroring. Verifiable: the truncated-chain scenario below. **(Phase 1)**
- The two links that already exist for free (`PendingExportId`, and Connected System Object / Metaverse Object ids) are exploited before any new storage is added; the new edge model only covers the seams those links cannot reach. Verifiable: implementation review confirms no edge duplicates a relationship already expressible via an existing FK or id join. **(Phase 1)**

## Non-Goals

- **Redesigning how a single RPEI's own outcome tree is presented.** Summary sentences, plain-language labels, the Flow/Timeline/Graph toggles, and attribute-change drawers belong to #1087, already in flight, and are not touched here. This PRD adds "Caused by" and "Consequences" affordances to whatever surface #1087 lands, not a new visual language.
- **No after-the-fact inference of causality.** See Rejected Alternatives.
- **No backfill of historic RPEIs into causal edges.** JIM is pre-release with a constantly-reset database; see Constraints.
- **No generic event-sourcing rewrite of JIM's persistence model.** See Rejected Alternatives.
- **No detailed design of the Phase 3 cross-Activity explorer.** It is sketched directionally only, deferred until Phases 1 and 2 have proven the edge model against real deployments.
- **No change to retention periods or policy.** Causal edges obey whatever retention already governs the RPEIs and Activities they connect; this PRD does not introduce a new administrator-facing retention setting (see Open Questions for whether it should).
- **No provenance for changes that do not go through the sync engine.** If a future surface mutates Metaverse Objects or Connected System Objects outside a Worker Task (none does today), it is out of scope until it produces RPEIs to attach edges to.

## Phasing

| Phase | Status | Scope |
|-------|--------|-------|
| Phase 0 | Done; out of scope for this PRD | Correctness fixes only, referenced here for continuity: the Causality panel naming the wrong Connected System for cross-system cascades (resolved by splitting `CausalityPageContext`'s single Connected System identity into the run's and the record's, since the two diverge for cascades), reference-recall outcomes storing the referencing object's name where the target Connected System's name belongs, and the Executed timestamp display. |
| Phase 1 | This PRD, primary scope | The causal edge model, plus worker capture at the reference-recall and deprovisioning seams first, surfaced as an upward "Caused by" chain on the RPEI Causality panel. |
| Phase 2 | This PRD, secondary scope | Downward "Consequences", built on the edges plus the free `PendingExportId` and object-id joins; aggregation; an affordance on early RPEIs showing that later events exist for this record. |
| Phase 3 | Directional sketch only; detailed design deferred | A cross-Activity causality explorer allowing full graph navigation from any event, once Phases 1 and 2 have proven the model. |

Phase 0's correctness fixes matter to this PRD even though they are out of scope for it: an upward chain is only as trustworthy as the per-hop attribution it stitches together, so Phase 1 had to follow Phase 0 (see Dependencies). That prerequisite is now satisfied, so Phase 1 is unblocked.

## User Stories

1. As an administrator looking at a Pending Export with no local explanation (the Project-Pulse Group scenario), I want to see the upward chain of events that produced it, so that I can explain the change without manually cross-referencing unrelated Activities.
2. As an administrator reviewing a source attribute change, I want to see what it went on to cause, so that I can assess its blast radius.
3. As an administrator handling a bulk cascade, I want many-to-one or one-to-many consequences summarised as one aggregated, expandable statement, so that the causality panel stays readable at scale.
4. As an administrator viewing an old RPEI whose upstream cause has since been purged by retention, I want an honest "cause no longer retained" message, so that I am not misled into thinking the event was uncaused or the system is broken.
5. As an administrator viewing a recent event, I want a visible affordance indicating that consequences may still be forthcoming, so that I do not mistake "no consequences yet" for "no consequences ever".

## Requirements

### Functional Requirements

#### Phase 1: Causal edge model and upward capture

1. JIM MUST record a **causal edge** for each occurrence of the cascade seams below: a row linking one **effect** (an RPEI and, where one exists, the specific `ActivityRunProfileExecutionItemSyncOutcome` node it produced) to one **cause** (an RPEI id, and/or a Metaverse Object id, and/or a Connected System Object id), plus an **edge type** describing the relationship. The edge is written by the worker **at the moment it creates the effect**, never reconstructed later.
2. The seams are few and enumerable. At minimum:
   - **Scope loss to disconnect**: the Synchronisation Rule whose scoping criteria excluded a Connected System Object is the cause of that object's `DisconnectedOutOfScope` outcome.
   - **Disconnect to Deletion Rule firing**: a disconnect (or the last qualifying disconnect) is the cause of the `MvoDeleted` / `MvoDeletionScheduled` outcome the Metaverse Object Type's Deletion Rule produces.
   - **Metaverse Object deletion to deprovisioning Pending Exports**: the deletion is the cause of any delete-type Pending Export staged for the object's own provisioned Connected System Objects.
   - **Metaverse Object deletion to reference recall**: the deletion is the cause of every membership-removal Pending Export staged against Metaverse Objects that referenced it. This seam has **two** worker entry points that both need capture, not one: the synchronous zero-grace-period path (`SyncTaskProcessorBase.FlushPendingMvoDeletionsAsync`, `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs`) and the deferred grace-period-expiry path (`Worker.PerformMetaverseObjectHousekeepingAsync`, `src/JIM.Worker/Worker.cs`), which both stage recall exports via the same underlying reference-recall evaluation but run at different times from different call sites.
   - **Pending Export to export execution to confirming import**: the RPEI that queued the export (`PendingExportCreated`) is the cause of the RPEI that executed it (`Exported` / `Deprovisioned`), which is in turn the cause of the RPEI that confirmed it on the next import (`ExportConfirmed` / `ExportFailed`).
3. The first hop of the last seam (queueing to executing) is already free: `ActivityRunProfileExecutionItem.PendingExportId` links them today and MUST be reused, not duplicated by a new edge. The second hop (executing to confirming import) is **not** free: `SyncImportTaskProcessor.ReconcilePendingExportsAsync` correlates confirming-import outcomes to the originating export only by `ConnectedSystemObjectId`, and a Connected System Object can cycle through export and import repeatedly, so an id-only join can pick the wrong cycle. This hop MUST get an explicit causal edge rather than relying on the join.
4. Per-object timelines (Connected System Object id, Metaverse Object id) are a second free edge and MUST be used wherever "what else happened to this object" is sufficient; new causal edges are for relationships a shared object id cannot express (a cause and effect on two *different* objects, such as Tina's Metaverse Object and the Project-Pulse Group's Connected System Object).
5. The RPEI Causality panel (#1087's surface) MUST gain an upward "Caused by" affordance that walks recorded causal edges from the viewed RPEI back through as many hops as are retained.
6. Where a single effect has many recorded causes of the same edge type and reason (for example ten Metaverse Object deletions all causing removal from the same Group), the chain MUST render as one aggregated, expandable statement, not one line per cause.
7. Low-signal hops, confirming imports in particular, MUST be foldable or collapsed by default so a long chain does not drown the signal.
8. Where an ancestor referenced by a causal edge can no longer be resolved (its RPEI or object has been purged by retention), the chain MUST render an explicit terminal state such as "cause no longer retained", never a silent gap and never an error.

#### Phase 2: Downward consequences

1. The RPEI Causality panel MUST gain a downward "Consequences" affordance, built from the same causal edges plus the free `PendingExportId` and object-id joins, walked in the opposite direction from Phase 1.
2. Fan-out (one cause, many effects, as in the four Glitterband EMEA Groups) MUST use the same aggregation treatment as Phase 1's fan-in case.
3. Because consequences are inherently time-dependent, viewing an RPEI immediately after it occurs MUST show an explicit "no consequences recorded yet" state, not an empty or broken-looking panel; viewing the same RPEI later, once downstream processing has run, MUST show the full downstream chain that has since been recorded.
4. An RPEI with no recorded consequences yet SHOULD carry a visible affordance hinting that later events may still appear, distinct from an RPEI whose type never causes anything (see Open Questions for the refresh mechanism).

#### Phase 3: Cross-Activity explorer (directional only)

1. Once Phases 1 and 2 have proven the edge model against real deployments, a dedicated view SHOULD let an administrator navigate the full causal graph from any event, not just one RPEI's immediate chain. Entry points, traversal bounds, and navigation model are explicitly left to a future design pass; nothing here should be read as committing to a specific UI.

### Non-Functional Requirements

- **Bulk-path performance.** Causal edge writes MUST go through the existing bulk raw-SQL patterns, not EF Core change tracking: chunked multi-row `INSERT` via typed `NpgsqlParameter`s, sized against `BulkSqlHelpers.MaxParametersPerStatement`, mirroring `BulkInsertRpeisRawAsync` (`SyncRepository.RpeiOperations.cs`) and `BulkInsertMvoChangesRawAsync` (`SyncRepository.MvoChangeOperations.cs`). A run deleting 100,000 Metaverse Objects must not degrade edge writes into row-at-a-time inserts. See `src/CLAUDE.md` > Worker Hot Path - Raw SQL Over EF Projection.
- **Same transaction as the effect it describes.** The existing RPEI flush already writes RPEIs, CSO change hierarchy rows, and sync outcomes inside one transaction before committing (see the `FlushRpeisAsync`-family method in `SyncRepository.RpeiOperations.cs`). Causal edge inserts MUST join that same transaction/batch rather than opening a new one, so an edge can never exist without the effect it describes, or vice versa within one flush.
- **Indexing on both ends.** Traversal happens in both directions (Phase 1 upward, Phase 2 downward), so both the effect-side and cause-side columns need an index; a design that is fast one way and a table scan the other way fails half of this PRD's goals.
- **Retention and purge must cascade to edges; orphaned edges must not accumulate.** JIM already deletes RPEIs by cascading from Activity deletion (`ActivitiesRepository` / `ChangeHistoryServer.DeleteExpiredActivitiesAsync`, `History.RetentionPeriod`), with `DeleteBehavior.Cascade` on the Activity-to-RPEI relationship. The **effect** side of a causal edge should follow the same real, cascading foreign key: an edge whose effect RPEI is gone is pure garbage, nothing will ever query it. The **cause** side must NOT be a hard cascading foreign key, or purging an old cause would silently delete the very edge that explains a still-retained effect, reintroducing the "no cause whatsoever" bug this PRD exists to fix. The existing precedent for this asymmetry is `ActivityRunProfileExecutionItemSyncOutcome.SyncRuleId` / `SyncRuleName`, already "stored as a plain scalar (no foreign key)... so the attribution survives later rule deletion". Cause-side references should follow the same snapshot pattern: unconstrained scalar ids, resolved best-effort at read time, rendering the honest "cause no longer retained" state (Phase 1, requirement 8) when the lookup misses rather than cascading the edge away.
- **Causes are always older than their effects.** Because retention purges by age and causality only runs forward in time, once a deployment has been live longer than one retention window, a cause ageing out before its effect is the **normal** long-run state, not a rare edge case. The "cause no longer retained" rendering is something most long-lived installations will show routinely, not occasionally; it must read as calm and expected, not alarming.
- **Synchronisation integrity is paramount** (`src/JIM.Application/CLAUDE.md`). Provenance capture must never risk corrupting or blocking a sync operation. Because edge writes ride in the same transaction as the RPEI/outcome writes they accompany (above), a failure to persist edges fails or retries with that same batch, exactly like a failure to persist the RPEIs themselves; there is no separate, independently-failing side channel for provenance that could leave a sync silently short of its edges while reporting success. Provenance capture must not introduce a *new* failure mode beyond what already exists for RPEI/outcome persistence.
- **No backfill or historic-data migration.** See Constraints.

## Examples and Scenarios

### Scenario 1: Upward chain resolves an apparently causeless membership removal

**Given**: the Project-Pulse Group's Pending Export RPEI in Glitterband EMEA shows a single `REMOVE member = uid=tina.adams99,...` change, with no local Connected System Object change and no Metaverse Object change recorded on that RPEI.
**When**: the administrator opens its Causality panel and expands "Caused by".
**Then**: the chain shows, in order: the Metaverse Object deletion for Tina Adams (S8-99), reason "Deletion Rule: authoritative source 'Yellowstone APAC' disconnected"; caused by her Connected System Object leaving the scope of the inbound Synchronisation Rule on Yellowstone APAC (`DisconnectedOutOfScope`, the rule named and linked); caused by the `jimEmployeeEndDate` attribute change detected on the Yellowstone APAC import. Every hop links its RPEI, Connected System, Metaverse Object, and Synchronisation Rule.

### Scenario 2: Downward fan-out from a single source change

**Given**: the administrator is viewing the RPEI for the original `jimEmployeeEndDate` attribute-change import on Yellowstone APAC.
**When**: they expand "Consequences" after the downstream Full Synchronisation and reference recall have completed.
**Then**: the panel shows the scope loss, the Metaverse Object deletion, and one aggregated line, "4 Pending Exports across Glitterband EMEA", expandable to the four individual Groups (Project-Catalyst, Project-Gateway, Project-Horizon, Project-Pulse), rather than four separate top-level entries.

### Scenario 3: Many-to-one aggregation at scale

**Given**: ten Identities, not just Tina, are deleted in the same Full Synchronisation of Yellowstone APAC for the same Deletion Rule reason, and all ten were members of the same Glitterband EMEA Group. The existing reference-recall deduplication already coalesces their removals into that Group's single Pending Export.
**When**: the administrator opens that Group's one resulting RPEI and expands "Caused by".
**Then**: the panel shows one aggregated statement, for example "10 Metaverse Object deletions on Yellowstone APAC (Deletion Rule: authoritative source disconnected)", expandable on demand to the ten individual Identities, never ten separate top-level chains.

### Scenario 4: Truncated chain after retention purge

**Given**: the Activity containing Tina's Metaverse Object deletion has aged past `History.RetentionPeriod` and been purged by the history retention cleanup job, while the Project-Pulse Group's Pending Export RPEI is still within retention.
**When**: the administrator expands "Caused by" on the surviving effect.
**Then**: the chain renders as far back as data allows, then an explicit terminal node reading "Cause no longer retained", not a silent stop and not an error.

### Scenario 5: Time-dependent downward view

**Given**: the administrator opens the RPEI for Tina's original attribute-change import moments after the import completes, before the next Full Synchronisation has run.
**When**: they expand "Consequences".
**Then**: the panel shows an explicit "No consequences recorded yet" state. Reopening the same RPEI after the Full Synchronisation and its cascade have completed shows the full downstream chain from Scenario 2.

## Constraints

- **No backfill or historic-data migration.** JIM is pre-release with a database that is reset constantly. Design the causal edge model for the target state only; do not add compatibility scope for reconstructing edges over RPEIs that predate this feature.
- No new product or runtime NuGet packages or JavaScript libraries. The causal edge model is a new `JIM.Models` entity plus an EF Core migration and raw Npgsql writes, all using dependencies already present.
- Must respect existing N-tier layering: any new read path for walking edges is exposed through `JimApplication`, never `Jim.Repository.*` directly, from `JIM.Web`.
- Self-contained and air-gapped: no cloud-service dependency of any kind.
- British English throughout any new UI text, documentation, and code comments; JIM domain nouns Title Cased even mid-sentence; no em dashes.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New causal edge table (append-only writes), EF Core migration, indices on both the effect and cause sides |
| Worker | Edge-writing calls at the enumerated seams in `SyncTaskProcessorBase.cs` (`FlushPendingMvoDeletionsAsync` and neighbouring disconnect/deletion-rule code), `Worker.cs` (`PerformMetaverseObjectHousekeepingAsync`), and the export/confirming-import path in `SyncExportTaskProcessor.cs` / `SyncImportTaskProcessor.cs`; a new chunked raw-SQL bulk insert method alongside `BulkInsertRpeisRawAsync` / `BulkInsertMvoChangesRawAsync` |
| Application | New server method(s) to walk the edge graph upward and downward from a given RPEI or outcome, exposed only through `JimApplication` |
| API | None required for Phase 1/2 if the existing Blazor page's existing data call is extended; Phase 3's explorer may need a dedicated endpoint (not designed here) |
| Models | New `JIM.Models` entity/entities for the causal edge, plus an edge-type enum covering the enumerated seams |
| UI | Phase 1: a "Caused by" affordance on the existing Causality panel (#1087's surface); Phase 2: a "Consequences" affordance plus an early-RPEI hint; Phase 3: a new cross-Activity explorer page/route (not designed here) |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/activities.md` | Add an explanation of "what caused this" and "what did this cause" once Phase 1/2 ship, alongside #1087's rewritten outcome section |
| `docs/developer/diagrams/ACTIVITY_AND_RPEI_FLOW.md` | Update the RPEI/outcome flow diagram to show causal edges linking RPEIs across Activities |
| `engineering/DEVELOPER_GUIDE.md` | Note the causal edge model as an architectural component once Phase 1 ships |
| `CHANGELOG.md` | ✨ entries under `[Unreleased]` for Phase 1 and, separately, Phase 2 |

## Dependencies

- **Phase 0** (done): the Causality panel's Connected System naming and reference-recall attribution correctness fixes have landed. An upward chain is only as trustworthy as the per-hop attribution it stitches together; building Phase 1 on top of known-wrong naming would have propagated the bug into every chain that passes through it. This dependency is satisfied and no longer blocks Phase 1.
- **[Causality Visualisation Redesign](doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md) (#1087)**: Phase 1/2's UI affordances attach to that redesign's Causality panel. The edge-capture and data-model half of Phase 1 has no such dependency and can proceed in parallel; only the UI slice needs #1087's surface to be stable.
- **[RPEI Outcome Graph](../plans/done/RPEI_OUTCOME_GRAPH.md) (#363, Done)**: the existing single-RPEI `ActivityRunProfileExecutionItemSyncOutcome` tree this PRD extends into a cross-RPEI graph. This PRD is the natural sequel to that one: same causal-chain philosophy, one RPEI wide there, arbitrarily many RPEIs wide here.
- **[Synchronisation Rule Causality Tracking](../plans/SYNC_RULE_CAUSALITY_TRACKING.md) (#399, Planned)**: an adjacent but orthogonal axis, per-attribute Synchronisation Rule attribution within a single change, rather than cross-object causal chains. Not a blocking dependency.
- The existing free links this PRD must reuse rather than duplicate: `ActivityRunProfileExecutionItem.PendingExportId`, and Connected System Object / Metaverse Object ids.

## Rejected Alternatives

### After-the-fact inference

Reconstructing causality later by matching attribute values, object ids, and timestamps across RPEIs was considered and rejected. It is guesswork that fails precisely when it matters most:

- **Bulk runs**: many objects change in the same page/flush with overlapping timestamps; there is no reliable ordering signal to disambiguate which of several candidate causes produced a given effect.
- **Retries**: a retried operation can produce near-identical RPEIs; matching on content alone risks attributing an effect to the wrong attempt.
- **Many-to-one cascades**: exactly the case this PRD exists to solve. Ten deleted Identities causing one Group's single Pending Export leaves no attribute-level trace to reconstruct which deletions actually contributed; the removed value is one member's name, a coincidence, not a general signal (see Problem Statement).

The worked example's key enabling insight makes this alternative doubly unnecessary: the worker already knows the answer, for free, at the exact moment it creates the effect. Throwing that knowledge away and reconstructing it heuristically afterwards is strictly worse on both correctness (ambiguous under load) and performance (a post-hoc correlation query over large tables, versus a write that is already happening).

### A single self-referencing "caused by" pointer on RPEI

Adding one nullable `CausedByRpeiId` column directly on `ActivityRunProfileExecutionItem` was considered as a lighter-weight alternative to a separate edge table, and rejected. Real cascades are many-to-many: one Metaverse Object deletion causes many Pending Exports (one-to-many), and the aggregation requirement demands collapsing many causes onto one effect (many-to-one) for display. A single scalar column can represent neither shape, cannot carry an edge type, and cannot support the cause being a Metaverse Object or Connected System Object rather than another RPEI. A dedicated edge table (or tables) is the minimum structure that fits the shape of the problem described in Functional Requirements, Phase 1.

### A generic event-sourcing rewrite

Replacing JIM's row-oriented persistence (current-state Connected System Object / Metaverse Object tables plus a change-history side channel) with a full event-sourcing model was considered and rejected. The problem this PRD solves is narrow: link a small, enumerable set of cascade seams. An event-sourcing rewrite would be a foundational architecture change touching every write path in the sync engine, for a much larger blast radius than the problem requires, and would put synchronisation integrity, the project's paramount rule, at direct risk during the transition. The targeted causal-edge approach achieves the same causal-navigation outcome with a small, additive, append-only table and no change to how existing state is written or read.

## Open Questions

1. **Edge granularity.** Does the effect end of an edge attach to the RPEI as a whole, or to the specific `ActivityRunProfileExecutionItemSyncOutcome` node? The latter is more precise (a multi-outcome RPEI could have only one outcome caused by a given upstream event) but materially increases the number of edges written. Needs a decision during implementation planning, informed by real outcome-tree shapes.
2. **Cross-Activity traversal and permissions.** An administrator today reads one Activity/RPEI at a time. A chain that spans Activities, as the worked example does across two Connected Systems' Full Synchronisations, may cross Activities the viewer would not otherwise browse to. Is the authorisation model for a traversed chain identical to viewing each Activity directly, or does this need its own check?
3. **Phase 2's "more may come" affordance.** Should it be push, poll, or a purely on-demand recheck when the page is reopened? Not specified by the product vision as given; a naive poll could itself become a performance concern at scale.
4. **Should causal edges get their own retention class?** The default proposed in this PRD (Non-Functional Requirements) is that edges die with the RPEIs they connect, under the existing `History.RetentionPeriod`. JIM already has precedent for giving specific record types their own, longer retention class (`History.ConfigurationChangeRetentionPeriod`, `History.SecurityEventRetentionPeriod`). A longer-lived edge skeleton (ids and edge types only, without full RPEI bodies) would let a chain's shape survive after detail is purged, at the cost of a second retention knob. Worth revisiting once real retention pressure is observed; not resolved here.
5. **Phase 3's scope** is deliberately unspecified. Its traversal depth/breadth bounds, entry points, and navigation model need their own design pass once Phases 1 and 2 have proven the edge model against real deployments.

## Acceptance Criteria

- [ ] Every enumerated cascade seam (scope loss to disconnect; disconnect to Deletion Rule firing; Metaverse Object deletion to deprovisioning Pending Exports; Metaverse Object deletion to reference recall, at both worker entry points; Pending Export to export execution to confirming import) writes a causal edge at event time, in the same transaction/batch as the RPEI and outcome rows it accompanies
- [ ] The Project-Pulse Group scenario (Scenario 1) renders a complete upward chain with no manual cross-referencing required
- [ ] A single RPEI with many recorded causes of the same type and reason (Scenario 3) renders as one aggregated, expandable statement, not one line per cause
- [ ] Low-signal hops, confirming imports in particular, are foldable or collapsed by default
- [ ] An ancestor removed by retention renders an explicit "cause no longer retained" state (Scenario 4), never a silent gap or an error
- [ ] A 100,000-object Metaverse Object deletion run writes causal edges via chunked bulk SQL with no row-at-a-time fallback on the happy path, and shows no measurable regression against the pre-provenance baseline
- [ ] Both ends of the edge are indexed; upward and downward traversal from any RPEI is covered by an integration test against a real multi-hop cascade
- [ ] Deleting or purging an RPEI or Activity cascades to the causal edges whose effect it was, with no orphaned rows accumulating; deleting or purging a cause does not delete the edge that records it was once the cause (`RequiresPostgres` regression test, per the pattern in `test/JIM.Worker.Tests/`)
- [ ] A failure to write a causal edge never fails, blocks, or corrupts the sync operation it describes, covered by a test that induces an edge-write failure and asserts the sync still completes and records correctly
- [ ] Downward "Consequences" honestly reflects time: an RPEI viewed immediately after creation shows "no consequences yet"; the same RPEI viewed after the causing chain completes shows them (Scenario 5, Phase 2)
- [ ] Phase 1 and Phase 2 each ship with a changelog entry and a `docs/` update in the same PR as their user-facing change

## Additional Context

- Canonical worked example: Identity "Tina Adams (S8-99)", source Connected System "Yellowstone APAC", target Connected System "Glitterband EMEA", four affected Groups (Project-Catalyst, Project-Gateway, Project-Horizon, Project-Pulse). Verified against the live database at PRD-drafting time; reuse this fixture in the implementation plan's test scenarios where practical.
- Primary seam implementation to extend for Phase 1: `src/JIM.Worker/Processors/SyncTaskProcessorBase.cs` (`FlushPendingMvoDeletionsAsync` and the reference-recall staging around it) and `src/JIM.Worker/Worker.cs` (`PerformMetaverseObjectHousekeepingAsync`, the grace-period sibling path).
- Governing conventions for the new bulk edge-write path: `src/CLAUDE.md` > Worker Hot Path - Raw SQL Over EF Projection, and > Raw SQL Writes Must Fix Up or Detach Tracked Instances.
- Governing constraint for failure handling: `src/JIM.Application/CLAUDE.md` (Synchronisation Integrity Requirements).
- Precedent for the cause-side snapshot-not-FK pattern: `ActivityRunProfileExecutionItemSyncOutcome.SyncRuleId` / `SyncRuleName` (`src/JIM.Models/Activities/ActivityRunProfileExecutionItemSyncOutcome.cs`).
