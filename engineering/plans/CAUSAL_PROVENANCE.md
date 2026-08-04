# Causal Provenance: Phase 1 Implementation Plan

- **Status:** Planned
- **Issue:** [#1223](https://github.com/TetronIO/JIM/issues/1223)
- **PRD:** [PRD_CAUSAL_PROVENANCE.md](../prd/doing/PRD_CAUSAL_PROVENANCE.md)
- **Created:** 2026-08-04

## Overview

Phase 1 of Causal Provenance gives the Causality panel a graph to render instead of an island: a causal edge table written by the worker at the moment it creates an effect, and an upward "Caused by" affordance that walks those edges back through as many hops as retention allows.

The PRD's worked example is the target: the Project-Pulse Group's Pending Export shows a reference removal with no local Connected System Object change and no Metaverse Object change, so today it appears to have no cause at all. After Phase 1 it resolves in one expansion back to the source attribute change that started the cascade, across two Connected Systems and two Activities.

Phase 2 (downward "Consequences") and Phase 3 (a cross-Activity explorer) are out of scope here and are not designed by this plan.

## Business Value

An administrator investigating a change currently hits a dead end at the edge of the Run Profile Execution Item they are looking at. At scale that is worse, not better: ten deleted Identities produce one Pending Export carrying ten unexplained removals with nothing tying them together. Phase 1 turns "I cannot explain this export" into "this export removed ten references because ten Identities left an authoritative source's scope on one import", without the administrator cross-referencing unrelated Activities by hand.

## Resolved design decisions

These were settled before planning and are recorded in the PRD; the plan implements them rather than revisiting them.

| Decision | Resolution |
|---|---|
| Edge granularity (PRD Open Question 1) | Store at the outcome, render at the record: non-null `EffectRpeiId` plus nullable `EffectSyncOutcomeId` |
| Chain shape | A walk over cohorts grouped by an attribution tuple, forking where causes diverge; not a linear chain with aggregation on one hop |
| Affordance placement | Below the view canvas, outside it, so all three views share one rendering |
| Default state | Expanded |
| Reference vocabulary | Relationship noun from the attribute, object noun from the Metaverse Object Type's `PluralName`; never a Metaverse Object Type branch |

## Technical Architecture

### Current state

- `ActivityRunProfileExecutionItem` (Guid key) holds a tree of `ActivityRunProfileExecutionItemSyncOutcome` (Guid key) nodes describing what happened to **one object in one run**.
- `ActivityRunProfileExecutionItem.PendingExportId` already links the item that queued a Pending Export to the item that executed it. The PRD requires this be reused, not duplicated.
- Connected System Object and Metaverse Object ids (both Guid) already answer "what else happened to this object".
- Nothing links a cause and an effect on two *different* objects, which is exactly the Project-Pulse case.

### The causal edge

One append-only table. The effect side is a real cascading foreign key; the cause side is deliberately unconstrained snapshot scalars, following the `ActivityRunProfileExecutionItemSyncOutcome.SyncRuleId` / `SyncRuleName` precedent, so purging an old cause cannot delete the edge that explains a still-retained effect.

```csharp
public class CausalEdge
{
    public long Id { get; set; }

    // ─── Effect side: real FK, cascades when the Activity and its RPEIs are purged ───
    public Guid EffectRpeiId { get; set; }
    public ActivityRunProfileExecutionItem EffectRpei { get; set; } = null!;

    /// Nullable: the specific outcome node this cause produced, when one exists. Carrying it is what
    /// makes cohort grouping correct on an item with more than one outcome (PRD Open Question 1).
    public Guid? EffectSyncOutcomeId { get; set; }

    // ─── Cause side: snapshot scalars, NO foreign key, resolved best-effort at read time ───
    public Guid? CauseRpeiId { get; set; }
    public Guid? CauseMetaverseObjectId { get; set; }
    public Guid? CauseConnectedSystemObjectId { get; set; }
    public string? CauseDisplayName { get; set; }

    // ─── Attribution tuple: what cohort grouping keys on ───
    public CausalEdgeType EdgeType { get; set; }
    public CausalReasonCode ReasonCode { get; set; }
    public int? ConnectedSystemId { get; set; }
    public string? ConnectedSystemName { get; set; }
    public int? SyncRuleId { get; set; }
    public string? SyncRuleName { get; set; }

    public DateTime Created { get; set; }
}
```

Both `CausalEdgeType` and `CausalReasonCode` are append-only persisted enums stored by ordinal, so they need pinning tests in the manner of `SyncOutcomeTypeOrdinalTests`.

### The attribution tuple keys on codes, not prose

The PRD names the tuple as edge type, reason, Connected System and Synchronisation Rule. **"Reason" here must be a `CausalReasonCode` enum, never the rendered sentence.** The deletion decision's `Reason` today is free text with an interpolated system name, built in `SyncEngine.EvaluateMvoDeletionRule`:

```csharp
return EvaluateGracePeriod(mvo, $"Specific sources mode: authoritative source {systemLabel} disconnected");
```

Grouping on that string would work only by luck. It embeds the Connected System name, which is already its own element of the tuple, so it is redundant and fragile at once; any wording change silently alters grouping behaviour, and any per-object element (a grace period date, an object name) collapses every cohort to size one with no error anywhere. The code is the grouping key; the sentence is derived at render time from the code plus the snapshot names.

### Cohort traversal

The read path walks levels, not hops:

1. Load every edge whose effect is the viewed item (or a specific outcome on it).
2. Group by the attribution tuple. Each group is one cohort, rendered as one expandable statement carrying its member count.
3. For each cohort, load every edge whose effect is any of that cohort's members, and repeat.
4. Terminate on an empty level (an uncaused root), or where no member resolves (retention).

A cohort of one is the degenerate case and renders as a plain hop, which is why the common single-cause item looks exactly like the simple chain. A level yielding two or more cohorts renders as a fork rather than flattening, because two root causes converging on one effect is the signal an administrator most needs.

## Implementation Phases

### Phase 1a: Model, schema and persistence

- `CausalEdge`, `CausalEdgeType`, `CausalReasonCode` in `JIM.Models`.
- EF Core migration: table, `DeleteBehavior.Cascade` from `ActivityRunProfileExecutionItem` on the effect side only, and indices on **both** ends (`EffectRpeiId`, and a composite over the cause-side ids), since traversal runs in both directions.
- `CausalEdgeBulkColumns` constants class per the mandatory raw-SQL column-list guard, plus its entry in `BulkInsertColumnCompletenessTests`.
- `BulkInsertCausalEdgesRawAsync` in `SyncRepository`, chunked against `BulkSqlHelpers.MaxParametersPerStatement`, mirroring `BulkInsertRpeisRawAsync`.
- Ordinal pinning tests for both enums.
- A `RequiresPostgres` round-trip test persisting a fully populated edge and asserting every field on read-back.

### Phase 1b: Worker capture at the seams

Edge writes join the existing RPEI flush transaction, never a new one, so an edge can never exist without the effect it describes.

**Two constraints found while implementing, which this phase's design has to accommodate:**

1. **Sync outcome ids do not exist when the outcome is created.** `SyncOutcomeBuilder.AddRootOutcome` and `AddChildOutcome` return an outcome with `Id == Guid.Empty`; ids are assigned at flush time by `FlattenSyncOutcomes` in `SyncRepository.RpeiOperations.cs`. An edge therefore cannot be given its `EffectSyncOutcomeId` at the seam. Carry a transient reference to the outcome object on the edge and resolve the id at flush time, immediately after the outcomes are inserted. This is exactly the pattern the flush already uses for `ConnectedSystemObjectChangeId`, so it needs no new concept:
   ```csharp
   foreach (var outcome in allOutcomes.Where(o => o.ConnectedSystemObjectChange != null))
       outcome.ConnectedSystemObjectChangeId = outcome.ConnectedSystemObjectChange!.Id;
   ```
   The transient reference must be `[NotMapped]`, or EF will try to make it a second relationship to the outcome table.

2. **The RPEI flush has two paths, and both need the edge insert.** A small batch takes a single-connection transactional path; a large batch takes a parallel COPY path whose partitions have already committed by the time stat counters are written ("Not transactional with the COPY partitions"). Wiring only the small-batch path would silently drop every edge on exactly the large cascades this feature exists to explain, and no test that runs under the batch threshold would notice. Cover both, and add a test that crosses the threshold.

Given both, edges are passed explicitly into `BulkInsertRpeisAsync` alongside the RPEIs rather than being accumulated in repository state, so the flush owns the ordering and the transaction and the seams stay declarative.

| Seam | Where |
|---|---|
| Scope loss to disconnect | `SyncTaskProcessorBase`, the `DisconnectedOutOfScope` path |
| Disconnect to Deletion Rule firing | `SyncTaskProcessorBase.ProcessMvoDeletionRuleAsync` |
| Metaverse Object deletion to deprovisioning Pending Exports | `SyncTaskProcessorBase.FlushPendingMvoDeletionsAsync` |
| Metaverse Object deletion to reference recall (synchronous, zero grace period) | `SyncTaskProcessorBase.FlushPendingMvoDeletionsAsync` |
| Metaverse Object deletion to reference recall (deferred, grace period expiry) | `Worker.PerformMetaverseObjectHousekeepingAsync` |
| Export execution to confirming import | `SyncImportTaskProcessor.ReconcilePendingExportsAsync` |

The reference-recall seam has **two** entry points that both need capture; they stage through the same evaluation but run at different times from different call sites, and covering only one leaves grace-period deployments with no provenance at all.

The queueing-to-executing hop is **not** an edge: `ActivityRunProfileExecutionItem.PendingExportId` already expresses it and the PRD forbids duplicating it. The executing-to-confirming hop **is** an edge, because reconciliation correlates only by `ConnectedSystemObjectId` and an object can cycle through export and import repeatedly, so an id-only join can pick the wrong cycle.

### Phase 1c: Application read path

- A server method on `JimApplication` returning the cohort walk for a given Run Profile Execution Item, bounded by a maximum depth.
- Cause-side resolution is best-effort: an unresolvable ancestor yields an explicit truncated-chain marker, never a gap and never an exception.
- No `Jim.Repository.*` access from `JIM.Web`.

### Phase 1d: UI

- A "Caused by" affordance below the view canvas in `CausalityPanel`, expanded by default, outside the canvas so Flow, Timeline and Graph share one rendering and the Flow view's measured SVG connector geometry is untouched.
- Cohort statements with counts, expandable to members; forks rendered as named branches.
- Confirming-import hops collapsed by default as low-signal.
- The "cause no longer retained" terminal state styled calm and expected, not alarming: past one retention window it is the normal end of a long chain.
- bUnit tests in `test/JIM.Web.Tests/`.

### Phase 1e: Metaverse Impact retirement

**The section retires in Phase 1; it does not wait for Phase 2.** One of its pieces genuinely belongs to Phase 2, but keeping a titled section and its subtitle ("How the Metaverse Object Deletion Rule for these objects applies to this change") alive to host a single unrelated alert is worse than not having the section at all: the heading would promise a Deletion Rule explanation the content no longer delivers.

The legacy no-snapshot fallback is already removed. The rest:

- **"The Metaverse Object was not deleted"** has no outcome to hang off, because a Deletion Rule that evaluates and declines produces no event. Render it as a **synthetic Identity-lane card** built from `DeletionPolicySnapshotJson`, which is already on the item. Both the Flow and Graph views already build synthetic nodes (the "Source record" root in `CausalityFlowConnectorCalculator` and `CausalityGraphLayoutCalculator`), so this needs no new concept and writes no rows.
- **`DeletionPolicySnapshotView`** becomes expandable detail on the deletion outcome rather than a separate section. The component and its bUnit tests are reused as-is.
- **The import-context "what happens next" line** moves to a plain page-level alert, outside any Deletion Rule framing. It is forward-looking rather than causal ("this will be disconnected on the next Synchronisation"), so its proper home is Phase 2's Consequences empty state; it sits at page level in the interim rather than holding a section open.
- **The housekeeping-context alert is deleted outright.** Housekeeping has recorded Causality outcomes since #1044 (an `MvoDeleted` root plus one Pending Export child per deprovisioned account), so the alert restates what the panel above it already shows.
- Correct the shipped "membership-removal" wording per the PRD's schema-derived wording requirement.

### Phase ordering

1a is first and has no dependencies. 1b and 1c both need 1a and can overlap each other. 1d needs 1c plus a stable #1087 surface. 1e needs 1d, because the synthetic card lands on the surface 1d builds. Only 1d and 1e carry the #1087 dependency, so the schema and worker halves can start immediately.

## Success Criteria

- The Project-Pulse scenario resolves end to end from a single "Caused by" expansion, verified at runtime against the integration fixture, not only by unit test.
- Ten Metaverse Object deletions causing one Group's Pending Export render as one cohort statement with a count, expandable to ten; a mixed-cause level renders as a fork.
- A run deleting 100,000 Metaverse Objects writes edges via chunked bulk SQL with no row-at-a-time fallback on the happy path, and no measurable regression against the pre-provenance baseline.
- Purging an Activity cascades to the edges whose effect it was, with no orphaned rows; purging a cause leaves the edge intact and renders "cause no longer retained".
- An induced edge-write failure still lets the synchronisation it describes complete and record correctly.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; integration Scenario 4 green.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Grouping on free-text reasons silently collapses every cohort to size one, with no error | `CausalReasonCode` enum is the grouping key; the sentence is derived at render time. Cover with a test asserting ten same-cause deletions produce one cohort |
| Edge writes degrade the deletion hot path | Chunked raw SQL in the existing flush transaction; benchmark a 100,000-object deletion against the pre-provenance baseline before merge |
| The cohort walk issues one query per level per cohort and fans out badly | Bound traversal depth, batch each level into a single query over the level's effect ids, and cap cohort member loading |
| Two persisted enums added at once, both stored by ordinal | Ordinal pinning tests for both, in the manner of `SyncOutcomeTypeOrdinalTests`, added in the same change as the enums |
| Retention semantics are asymmetric and easy to get wrong in a migration | `RequiresPostgres` regression test proving effect-side cascade and cause-side survival, per the PRD's acceptance criteria |
| The UI slice depends on #1087's surface, which is still open | Phase 1a to 1c have no such dependency and can proceed in parallel; only 1d and 1e need the surface stable |

## Dependencies

- **[Causality Visualisation Redesign](../prd/doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md) (#1087)**, still open: the surface Phase 1d attaches to.
- **[RPEI Outcome Graph](done/RPEI_OUTCOME_GRAPH.md) (#363, Done)**: the single-item outcome tree this extends into a cross-item graph.
- **[Synchronisation Rule Causality Tracking](SYNC_RULE_CAUSALITY_TRACKING.md) (#399, Planned)**: adjacent and orthogonal; not blocking.
