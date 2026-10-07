# Connected System Full Synchronisation Preview - Implementation Plan

- **Status:** Doing (Phases 1-4 complete)
- **Issue:** [#1530](https://github.com/TetronIO/JIM/issues/1530)
- **Gated by:** [#1520](https://github.com/TetronIO/JIM/issues/1520) (engine timing at 100K, on a 20 GB+ host) before release
- **Engine:** [`engineering/plans/done/SYNC_PREVIEW_ENGINE.md`](../done/SYNC_PREVIEW_ENGINE.md) (#288, `PreviewFullSyncAsync`)
- **Framework:** [`engineering/plans/done/CONFIGURATION_CHANGE_PREVIEW.md`](../done/CONFIGURATION_CHANGE_PREVIEW.md) (#827); sibling surface [`engineering/plans/done/CONNECTED_SYSTEM_DELETION_IMPACT_PREVIEW.md`](../done/CONNECTED_SYSTEM_DELETION_IMPACT_PREVIEW.md) (#134)
- **UX artefact:** [Full Synchronisation Preview](https://claude.ai/artifact/3PXoj2Mvv8E3p375ozGy3Y) (the scenario walkthrough this plan's BDD section is drawn from)

## Overview

A Full Synchronisation is how configuration reaches existing objects, and the portal tells administrators to "review" pending destructive configuration "before running a Full Synchronisation" without giving them anything to review it with. This plan delivers #1530: previewing what a Full Synchronisation of one Connected System would do, before running it, in the portal, the REST API and PowerShell.

It is a new surface of the Configuration Change Preview framework (#827), so the panel, stages, Worker dispatch, cancel, retention, staleness, the "What would change" grid, drill-down, REST and PowerShell come from what #827 and #134 built. The evaluation is the Sync Preview Engine's whole-system walk (#288), changed in Phase 1 so it answers what the run would do rather than what differs downstream.

## Business Value

A Full Synchronisation after a configuration change is the moment configuration becomes consequence: accounts provisioned, disabled or deleted across every target the run reaches. Today an administrator runs it to find out. The preview turns that into reading the consequence first, in the same words and grid every other JIM preview uses, and the run's Activity records that they did, which is the audit answer high-trust customers ask for.

## Decisions (2026-10-06)

1. **Build on the Configuration Change Preview framework**, not a bespoke counts-and-samples view. The engine's six counts and five samples per category cannot say which target system or which attribute changes, which is what an administrator needs before a run.
2. **Evaluate the whole population by default.** Every other preview refuses to show partial counts, because a partial count read as a whole one is how a change gets approved as safe. Above a size threshold the administrator sees an estimate and confirms before it starts. A cap stays available to scripts.
3. **Close the target-system gap in the same work**, rather than hiding the action on target systems.
4. **Prove the fidelity question with tests first** (done, below), and fix the engine where it is real.
5. **"N objects would not change" is a group of its own (2026-10-07)**, not a column on the preview: the adapter yields a would-not-change delta per unchanged object, so the count is exact in the existing per-group counts, the drill-down shows a sample of them, and REST and PowerShell carry it with no new fields. The verdict and the "What would change" grid leave that group out and show it as the "would not change" line.

## Finding: the whole-system walk does not answer "what would this run do"

Proven by `SyncPreviewFidelityTests.Drift` (preview paired with the real run over the same data):

| Case | Real run stages | Engine preview proposes |
|---|---|---|
| Full Synchronisation of the **source** (HR), a **target** (AD) value drifted | 0 updates | **1 update** (over-reports) |
| Full Synchronisation of the **target**, Enforce State **on** | 1 drift correction | **0** (under-reports) |
| Full Synchronisation of the target, Enforce State off | 0 | 0 |

The cause is the outbound evaluation's semantics. The per-object core (`SyncPreviewServer.PreviewCsoCoreAsync`) evaluates outbound with no recall, which `EvaluateOutboundPreviewForMaterialisedMvosAsync` documents as "the state-assertion question": every difference between the Metaverse Object and every target is proposed as an update. The real run is change-driven: a synchronisation of a source exports only what its objects changed (plus scope transitions), and corrects drift only in the system being synchronised, where an export rule enforces state.

This affects the existing per-object Sync Preview (#1519) as much as the new surface: previewing an unchanged HR object today shows Active Directory drift corrections that will not happen, and previewing an Active Directory object misses one that will. It is therefore discovered work, fixed first in its own PR (Phase 1).

## UX: behaviour-driven scenarios

From the UX artefact; each is an acceptance test.

### Where it starts

```gherkin
Scenario: Reviewing pending configuration before applying it
  Given "Yellowstone HR" has configuration changes since its last Full Synchronisation
  When I open the Connected System
  Then the drift notice offers "Preview Full Synchronisation" beside "Review changes"

Scenario: Previewing a Full Synchronisation Run Profile
  When I open the Run Profiles tab
  Then each Full Synchronisation Run Profile has a "Preview" action
  And Import, Export and Delta Synchronisation Run Profiles do not
```

### Running it

```gherkin
Scenario: A preview runs in the background
  When I start a Full Synchronisation preview
  Then it runs as an Activity, on the Worker above the threshold
  And I see its progress and can cancel it
  When I leave the page and come back
  Then the panel reattaches to the latest preview of this Connected System

Scenario: Previewing a system above the size threshold
  Given the Connected System holds more objects than the threshold
  When I start a Full Synchronisation preview
  Then I see how many objects it will evaluate and about how long it will take
  And I confirm before it starts
```

### Reading the answer

```gherkin
Scenario: Reading what a Full Synchronisation would do
  When the preview completes
  Then the verdict leads with the worst consequence
  And "What would change" lists each change by Connected System and attribute, across every target the run reaches
  And it says how many objects would not change

Scenario: Drilling into a change
  When I select a row
  Then I see the objects it applies to, with their state now and after the run
  And selecting one shows the causality view the run would record for it

Scenario: Confirming a run would be a no-op
  Given every object is already in the state its rules describe
  Then the preview says a Full Synchronisation would change nothing

Scenario: Previewing a Full Synchronisation of a target
  Given an export rule to the target enforces state, and values were edited directly in the target
  Then the preview proposes the corrections the run would make, as "drift correction"
```

### Acting on it

```gherkin
Scenario: The audit trail shows the administrator looked first
  When I start the Full Synchronisation from the preview panel
  Then the run's Activity says it was informed by that preview

Scenario: Previewing and running from a script
  When I run "New-JIMConfigurationChangePreview -ConnectedSystemId 1 -FullSynchronisation -Wait"
  Then I get the verdict, groups and rows the portal shows
  And "Start-JIMRunProfile -PreviewActivityId" records it on the run
```

## Technical Architecture

### Current state

- `SyncPreviewServer.PreviewFullSyncAsync` walks the population read-only (guarded repository, rollback-only transaction), with a default 10,000-object cap, returning counts and five sample trees per category. No caller.
- The framework runs an adapter's count stage and its delta stage as two passes. Engine-driven adapters (#134's deletion adapter) count by streaming their whole evaluation, so they evaluate everything twice.
- Run Profile executions already carry `Activity.PreviewActivityId` (from #134); the execute endpoint takes no body.

### Proposed design

- **Engine (Phase 1):** the full-sync walk and the per-object CSO preview take the run's semantics. Outbound is evaluated over the changes the object's own inbound evaluation made (changed Metaverse attributes, and scope transitions), as `EvaluateOutboundExportsAsync` does in the run; drift correction is evaluated for the previewed system's own joined objects where an export rule enforces state, using the run's drift detection; and the export scope review (objects flagged `ScopeReviewPending`, #892, #1925) is previewed after the walk, as the run drains it. The state-assertion question stays available, explicitly, to the callers that ask it (the behaviour-toggle adapter's "would drift still be corrected").
- **Engine streaming (Phase 2):** the walk hands each object's result to a consumer as it goes, with no cap by default; counts and samples become one consumer among others.
- **Framework (Phase 2):** an adapter can declare that its impact counts come from its delta stream, and the framework then counts distinct subjects per transition during the one evaluation pass instead of running it twice (the deletion adapter adopts it too). The preview records how many objects it evaluated, so "N objects would not change" is exact.
- **Adapter (Phase 3):** `ConnectedSystemFullSynchronisationPreviewAdapter`, surface `ConnectedSystemFullSynchronisation`, proposal carrying only optional bounds. Validation refuses a system being deleted, a derived-flow cycle, and a system with no Full Synchronisation Run Profile. The estimate uses the last completed Full Synchronisation's duration where there is one, else the object count. Deltas: projection, join, scope exit, Metaverse value changes per attribute, provisioning, updates per target attribute, deprovisioning, deletion eligibility, drift correction, and per-object errors.
- **Run link (Phase 4):** the execute endpoint and `Start-JIMRunProfile` accept a preview id, validated as a completed Full Synchronisation preview of the same Connected System, and record it on the run's Activity.

## Implementation Phases

### Phase 1: engine fidelity (own PR, fixes #1519's per-object preview too) ✅

- Red: `SyncPreviewFidelityTests.Drift` (three cases above), plus `SyncPreviewFidelityTests.ScopeReview` (the review of a flagged object the walk does not reach, one the walk does reach proposed once, and nothing flagged).
- `PreviewCsoCoreAsync` takes `asTheRunWould`: outbound is evaluated over the inbound change set (`EvaluateOutboundPreviewForMaterialisedMvosAsync`'s `synchronisationChanges`), for scope alone when the object is flagged for review, and not at all otherwise. The single-object preview and the full-system walk ask it; `PreviewSyncForCsosAsync` keeps state assertion.
- Audit of the state-assertion callers: the Attribute Flow and behaviour-toggle adapters diff a baseline against a proposal, so whatever neither changes cancels and state assertion is what they want; the scoping adapter reads only the inbound verdict (projection or join). Neither changes.
- Drift correction through `DriftDetectionService.EvaluateDrift` with the run's inputs (this system's enforcing export rules, every system's import mappings), including for a target's joined objects that no import rule processes; corrective exports fold into a proposed update for the same object, and a `DriftCorrection` root is shaped as the run records it.
- Export scope review after the walk, skipping objects the walk already reviewed; a system with no objects is still previewed for it. `FullSyncPreviewCounts.ExportScopeReviewed` counts it.
- The unchanged-object optimisation, found by the runtime check: while no configuration has changed since it was last fully applied, the run skips every object unchanged since the last synchronisation, drift included, and the walk proposed corrections for them. The rule moved onto the models (`ConnectedSystemObject.IsUnchangedSince`, `ConnectedSystem.GetUnchangedObjectWatermark`), read by the PostgreSQL loader, the run and the walk alike; the walk counts what it skips in `FullSyncPreviewResult.UnchangedObjectCount`. The in-memory repository ignores the watermark, so the workflow tests assert against the rule and the runtime check pairs preview and run.
- Runtime check on the full stack: preview and real Full Synchronisation of a target with a drifted value agree (one Update, Add and Remove on the attribute, one Drift Correction item counting it), and a source object's preview no longer proposes the target's correction. The per-object preview evaluates an object as if processed, which the docs now say.

### Phase 2: streaming walk and single-pass counting ✅

- Streaming walk ✅: `SyncPreviewServer.StreamFullSyncPreviewAsync` yields `FullSyncPreviewItem`s in the order the run meets them (the population, a refusal on a derived flow cycle, each object evaluated or skipped as obsolete or unchanged, the export scope review, and a truncation when a bound stopped it), with no bound by default (`FullSyncPreviewStreamOptions`). The read-only scope and rollback-only transaction live as long as the enumeration. `PreviewFullSyncAsync` is now one consumer of it (counts and bounded samples), its semantics unchanged.
- Single-pass counting ✅: an adapter that can only count by evaluating supplies an `IPreviewImpactCounter` (`CreateImpactCounterAsync`; `PreviewImpactCounter.PerDelta` or `PerSubject`), which the framework feeds during the one evaluation pass and records only when the whole stream completes. Wider than first planned: eight adapters counted by streaming their own deltas, not just the deletion adapter, and all eight adopted it, each with an equivalence test (its counter fed its own deltas equals its `CountImpactAsync`). Runtime: a 1,108-object deletion preview went from 3.8s to 2.1s with identical counts.
- Evaluated-object count: moved to Phase 3 and decided there (decision 5): no column; the stream reports every unchanged object, and the adapter counts them as a group of their own.

### Phase 3: adapter and transitions ✅

- Three engine gaps found while mapping the run's consequences, each fixed test-first and paired with the run before the adapter was built (`SyncPreviewFidelityTests.ScopeExit`, `.Obsolete`, `.FailedObjects`):
  - **A scope exit's recall** proposed no exports, though the run evaluates exports over it: a target holding a recalled value is now sent the change. Also fixes the per-object Sync Preview.
  - **Obsolete objects** were skipped by the walk, though a Full Synchronisation tears each down first (disconnect, Deletion Rule, recall, deprovisioning): the most destructive things a run routinely does. The walk now drives the run's own obsoletion core read-only on clones, as the deletion preview does (the clone helpers moved to a shared `ObsoletionPreviewClone`), then the recall's exports and an immediate deletion's downstream deprovisioning (shared with the scope-exit cascade). Each page's obsolete objects are met first, as the run tears them down in pass 1.
  - **An object the run fails** (an inbound Expression that throws, or a missing input under Fail the object) still had its projection and exports proposed, which the run discards. It now previews as the error alone. Also fixes the per-object Sync Preview.
- Surface `ConnectedSystemFullSynchronisation`, `ConnectedSystemFullSynchronisationProposal(MaxObjects)`, `ConnectedSystemFullSynchronisationPreviewAdapter`, registered. Validation blocks a missing or deleting system, a cap below one, a system with no Full Synchronisation Run Profile and a derived flow cycle; a cap below the population warns that the counts describe only the objects evaluated.
- Rows (`FullSynchronisationPreviewDeltas`), each read off a fact the walk established, in the vocabulary every preview speaks: Projected, Joined, Disconnects from its Metaverse Object (the object's own scope exit or teardown, and a target's disconnection), Left scope with the join kept, Connected System Object deleted (unjoined obsolete), Becomes eligible for deletion (with when), Attributes flowed and Value cleared per Metaverse attribute (not for a projection), Drift corrected per attribute, Provisioned, Updated in the target system per attribute (old to new: the engine now carries the target's current values), Removed from the target system, Provisioning cancelled, and the failures (Attribute Flow does not evaluate, Matches more than one, and a new Fails with an error). The engine gained what the rows needed: the inbound summary's Metaverse Object name and type, a departing object's recall in its changes, `OutboundPreviewEntry.CurrentTargetValues`, and `SyncPreviewResult.DriftCorrections`.
- Would not change (decision 5): a new `WouldNotChange` transition, one row per object the run skips as unchanged or leaves as it is, not stated in the verdict (`StatedInVerdict: false`) and sorted last. Its exclusion from the grid, as a line of its own, is Phase 5.
- Counts: single pass, distinct objects per transition, a target system's object told apart by system (one identity provisioned to two systems is two). Paired with the run: projections, provisioning, disconnections, deletions and deprovisioning counted equal what the run records and stages.
- The verdict's sentence forms for the run outcomes this preview states (Projected, Joined, Provisioned, Drift corrected and others) were added, which the behaviour-toggle and scoping previews' verdicts gain too.
- Estimate: the population, one row each (most objects of a repeat run would not change). The duration estimate the confirmation shows moved to Phase 5, beside the dialog that displays it, and #1520's figures still set the threshold.

### Phase 4: run link ✅

- `SynchronisationWorkerTask.PreviewActivityId`, transient like the deletion task's, checked in `TaskingServer.CreateWorkerTaskAsync` (where portal, REST and scheduler all queue a run) and copied onto the run's Activity. Refused, and the run not queued, when the Run Profile is not a Full Synchronisation, the id is not a Full Synchronisation preview of the same Connected System, or the preview has not completed (still running, failed, cancelled, or blocked at validation and so evaluated nothing).
- The execute endpoint takes an optional body (`{ "previewActivityId": ... }`); a POST with no body queues the run as before. `Start-JIMRunProfile -PreviewActivityId` (brought forward from Phase 6) sends it.
- A gap found by the real-PostgreSQL test: a Full Synchronisation preview's Activity was never attached to its Connected System, so every genuine citation would have been refused and the panel could not have reattached. Fixed, with a sweep test that every surface's preview Activity names the object it previewed.
- The Activity page already shows "Informed by a preview" for any Activity carrying the link, a run's included.
- Runtime: on the stack, body-less execute calls still queue; an unknown id, a deletion preview, another system's preview and a Delta Synchronisation citation are each refused with a 400 naming why; `Start-JIMRunProfile -PreviewActivityId` queued a Full Synchronisation whose stored Activity names the preview (a deletion preview relabelled in the database stood in, as nothing starts a Full Synchronisation preview until Phase 6).

### Phase 5: portal

- The duration estimate for the threshold confirmation: the last completed Full Synchronisation's duration where there is one, else the object count against #1520's rate.
- Drift notice button, Run Profiles row action, inline panel on the Connected System page with reattach, threshold confirmation, "would not change" line, Run Full Synchronisation from the panel, drill-down columns, object view through the Sync Preview panel. bUnit tests where the logic lives.

### Phase 6: REST and PowerShell

- Start endpoint; `New-JIMConfigurationChangePreview -FullSynchronisation` (optional `-MaxObjects`); Pester tests; docs, including the preview-then-run example for `Start-JIMRunProfile -PreviewActivityId` (the parameter landed in Phase 4).

### Phase 7: verification and close-out

- #1520 run on the devcontainer (20 GB+); its figures set the threshold and the fallback estimate.
- Integration scenario: preview, then run, and compare at Medium.
- Docs, changelog, plan to `done/`.

## Success Criteria

- The preview of a Full Synchronisation proposes what the run then does, proven by fidelity tests per case and by the integration scenario.
- It runs to completion on the whole population by default, cancellable, with a stated estimate above the threshold.
- Portal, REST and PowerShell parity, including the run link.

## Benefits

- **UX:** one answer to "what will this run do?", reachable from the notice that asks the question.
- **Fidelity:** the per-object Sync Preview (#1519) stops proposing corrections its run will not make, and starts showing the ones it will.
- **Performance:** engine-driven previews (deletion included) evaluate once instead of twice.

## Dependencies

- #1520 on a 20 GB+ host (the devcontainer); the cloud sandbox has 15 GB.
- No new packages.

## Risks and Mitigations

- **Changing the engine's outbound semantics breaks an adapter that relied on state assertion.** Every caller of the outbound evaluation is audited in Phase 1; state assertion stays as an explicit mode.
- **Run time at scale.** Single-pass counting halves it for engine-driven surfaces; #1520 measures it; the estimate tells the administrator before they start.
- **Delta row volume at 100K.** The framework's per-group capped persistence applies, with exact group counts.
