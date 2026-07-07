# Connected System Deletion: Synchronised Deprovisioning and Attribute Impact Preview

- **Status:** Planned
- **Created:** 2026-07-07
- **Author:** Jay Van der Zant
- **Issues:** [#809](https://github.com/TetronIO/JIM/issues/809) (execution: synchronised deprovisioning), [#134](https://github.com/TetronIO/JIM/issues/134) (preview: attribute impact analysis)
- **Design gate:** [#827](https://github.com/TetronIO/JIM/issues/827) (Configuration Change Preview: unified framework)

## Problem Statement

Deleting a Connected System today is a fast, raw-SQL bulk teardown. `ConnectedSystemServer.ExecuteDeletionAsync` captures a tombstone, optionally calls `MarkOrphanedMvosForDeletionAsync` for Metaverse Objects whose only connector was the deleted system, then hands off to `DeleteConnectedSystemAsync` which severs the now-dead references. A recent fix (branch `fix/delete-connected-system-syncrulemapping-columns`) made that teardown atomic and corrected its foreign-key handling, and **deliberately deferred attribute recall**: Metaverse Object attribute values contributed by the deleted system keep their value with the contributor link (`ContributedBySystemId`) cleared.

For **surviving multi-connector Metaverse Objects**, this diverges from what disconnecting each of the system's objects through synchronisation would do. Teardown does **not**:

- **Recall attributes:** remove the values this system contributed and re-evaluate Attribute Flow precedence so another contributor can take over (per-object-type setting `RemoveContributedAttributesOnObsoletion`, default on).
- **Evaluate downstream exports:** queue Pending Exports so other Connected Systems are notified of the recalled or changed values.

So deleting a system that contributed to shared identities silently leaves the Metaverse in a state that a normal synchronisation would never produce, and downstream systems are never corrected.

This is the exact failure mode from real migration experience, captured in #134: an administrator migrates from an old HR source to a new one, switches most attribute precedence to the new system, but leaves one or two attributes with the old system as priority. Deleting the old system recalls or shifts those attributes; the changes fan out as exports to Active Directory, disabling accounts or pushing wrong data to production. The administrator had no way to see this coming.

### The two issues and how they relate

This PRD covers a **single capability delivered as two halves of the same coin**:

- **#809 is the execution side (the broader capability).** It introduces a choice at deletion time between two modes: **Teardown** (today's behaviour, kept as an explicit option) and **Synchronised Deprovisioning** (process each Connected System Object through the synchronisation engine's obsoletion path, so attribute recall, Metaverse Object deletion-rule evaluation, and downstream export evaluation all run exactly as they would during a normal synchronisation).
- **#134 is the preview side (the validation subset/phase).** It is a read-only, pre-flight impact analysis: which attributes would be **recalled**, which would **shift precedence** to another contributor, and which **downstream exports** would fire. It changes no state.

They are two ends of one workflow: **#134 previews the impact that #809 then executes.** #134's preview is a dry-run of the exact evaluation Synchronised Deprovisioning performs, so it becomes the natural validation step an administrator runs before committing to a synchronised deletion. Designing them separately would risk the preview computing impact by one code path and the execution producing a different result by another; they must share the evaluation logic so "what the preview promised" and "what the deletion did" cannot drift.

### The #827 design gate (the constraint that shapes this PRD)

#134 is **design-gated by #827**. The June 2026 decision on #827 is that Configuration Change Preview is built as **one holistic framework with per-surface adapters**, designed first and implemented in value/severity order, so every preview surface shares one architecture and one administrator UX. Connected System deletion impact analysis is explicitly named as one of the highest-value tier-3 adapter cases and **must not** be built as a bespoke preview ahead of the framework.

This PRD therefore does not propose a standalone preview engine. It specifies the deletion-specific behaviour (the execution modes, the confirmation UX, the worker path, the reporting) and defines #134's preview as **the Connected System deletion adapter on the #827 framework**. Where the #827 framework design is still open (adapter contract, where computation runs, proposed-config representation), this PRD lists those as dependencies and decisions, not as things it re-invents.

Note: #134 references `docs/CONNECTED_SYSTEM_DELETION_DESIGN.md`. That document was moved and now lives at `engineering/plans/done/CONNECTED_SYSTEM_DELETION_DESIGN.md` (its "Future Enhancement: Attribute Impact Analysis" section is the origin of #134). The dead `docs/` reference in #134 should be corrected to point at the new location as part of this work.

## Goals

- Give administrators an explicit, informed choice between **Teardown** and **Synchronised Deprovisioning** when deleting a Connected System, with a safe default and a confirmation step that states the blast radius.
- Make Synchronised Deprovisioning produce **exactly** the Metaverse and downstream-export state that disconnecting every one of the system's Connected System Objects through a normal synchronisation would produce (attribute recall with precedence re-evaluation, Metaverse Object deletion-rule evaluation, and Pending Export generation).
- Deliver #134's attribute impact analysis as a **tier-3 adapter on the #827 Change Preview framework**, sharing the evaluation logic with the execution path so preview and execution cannot diverge.
- Ensure the preview answers the three migration questions before any state changes: which attributes are **recalled**, which **change value via precedence shift** (and to which contributing system), and which **downstream exports** result.
- Run both the preview and the synchronised execution on the worker/background path, bounded, resumable, and fully reported via Activities and RPEIs, at 100k to 1m object scale.
- Preserve the existing fast Teardown path unchanged for the cases where it is the right tool (test/misconfigured systems, abandoned data).

## Non-Goals

- **Building the #827 framework itself.** The adapter model, tier definitions, where preview computation runs, and the "proposed configuration" representation are owned by #827 and are prerequisites, not deliverables of this PRD.
- **Previews for other configuration surfaces** (scope changes #204, schema refresh #421, attribute priority #91, the G1-G6 gaps in #827). This PRD covers only the Connected System deletion adapter.
- **Soft delete / archive / recovery of a deleted Connected System.** Deletion remains destructive; recovery-manifest export (#136) is separate.
- **Changing the count-tier deletion preview** already shipped in #135 (Connected System Object counts, Metaverse-Object-may-be-deleted counts). This work adds the attribute tier above it, it does not replace it.
- **Full per-object export simulation** ("show the exact outbound value for every affected object in every downstream system"). The preview reports export *counts and targets*; exact per-object export payload simulation is a later phase (the #288 engine's remit), noted but out of scope here.
- **New attribute-flow, recall, or precedence semantics.** This feature executes and previews the obsoletion behaviour the synchronisation engine already implements; it does not change what recall or precedence *mean*.

## User Stories

1. As an administrator decommissioning a Connected System that shares identities with other systems, I want to choose Synchronised Deprovisioning so that attribute recall, precedence shifts, and downstream corrections happen properly, so that the Metaverse and downstream systems are left in a correct, synchronisation-consistent state.
2. As an administrator about to delete a Connected System, I want a pre-flight report of exactly which Metaverse Object attributes will be recalled, which will change value (and to which system), and which downstream exports will fire, so that I can catch an accidental precedence mistake before it locks users out of Active Directory.
3. As an administrator removing a misconfigured or test Connected System whose contributions should simply be abandoned, I want the fast Teardown option, so that I am not forced to pay the cost or side effects of full synchronised deprovisioning.
4. As an administrator deleting a very large system (hundreds of thousands of objects), I want the synchronised deletion to run as a bounded, resumable background job with progress and error reporting, so that a partial failure does not leave the system in an unknown half-deleted state.
5. As an operator reviewing the audit trail, I want the preview I ran and the deletion mode I chose recorded as Activities, so that "the administrator was warned and proceeded anyway" is auditable.

## Requirements

### Functional Requirements

#### Deletion mode selection (#809)

1. The delete Connected System action must offer two named modes: **Teardown** and **Synchronised Deprovisioning**. The naming must be consistent across UI, API, and audit records.
2. **Teardown** must retain today's behaviour exactly: fast bulk delete, surviving Metaverse Objects keep this system's contributed values with `ContributedBySystemId` cleared (provenance dropped), no attribute recall, no precedence re-evaluation, no downstream export generation. `MarkOrphanedMvosForDeletionAsync` continues to run for fully-orphaned Metaverse Objects.
3. **Synchronised Deprovisioning** must process each Connected System Object through the synchronisation engine's obsoletion/disconnection handling, performing, per object: (a) attribute recall with Attribute Flow **precedence** re-evaluation, gated by the object type's `RemoveContributedAttributesOnObsoletion`; (b) Metaverse Object deletion-rule evaluation (`MarkOrphanedMvosForDeletionAsync` / the per-object deletion-rule path); (c) downstream export evaluation, queuing Pending Exports to other Connected Systems.
4. Synchronised Deprovisioning must reuse the existing obsoletion processors in the worker synchronisation path rather than duplicating recall/precedence/export logic. Execution must sit in the worker deletion path (`ExecuteDeletionAsync` and the worker task it runs under), not inline in a request handler.
5. The chosen mode must be recorded on the deletion Activity so the audit trail shows which path ran.

#### Attribute impact preview (#134, as an #827 adapter)

6. The system must provide an optional, on-demand attribute impact analysis for a pending Connected System deletion, exposed as the **Connected System deletion adapter registered on the #827 Change Preview framework**, at the framework's **tier 3** (full object-level impact analysis). It must build on, not replace, the tier-2 count preview from #135.
7. The preview must report, at minimum: total attributes **recalled** (this system was the only contributor and recall is enabled); total attributes **changing value via precedence shift** (another contributor becomes the new winner) together with the winning system; total **downstream exports triggered**, grouped by target Connected System.
8. The preview must surface the **most-impacted attributes** (top-N by affected Metaverse Object count) with, per attribute, the affected object count and whether the impact is a recall or a precedence shift (and to which system).
9. The preview must compute its results using the **same evaluation logic** that Synchronised Deprovisioning executes, so that the preview is a faithful dry-run. It must not re-implement recall/precedence/export evaluation in a parallel code path.
10. The preview must change no state: no attribute values altered, no Pending Exports queued, no Activities beyond an optional audit record that the preview was run.
11. When an administrator selects Synchronised Deprovisioning, the confirmation flow must make the tier-3 preview available as the validation step before commit (offered, not silently forced, given its cost at scale).

#### Execution safety, scale, and reporting (#809)

12. Synchronised Deprovisioning must run as a bounded, **resumable** background worker operation. Because per-object synchronisation processing cannot be a single transaction (unlike Teardown), the design must define batch/checkpoint boundaries such that an interrupted run can resume without reprocessing committed objects or double-queuing exports.
13. Progress and failures must be reported through Activities and RPEIs, consistent with JIM's Synchronisation Integrity rules: no silent failures, summary statistics logged at the end of the batch (total objects, recalled, precedence-shifted, exports queued, errored, error categories).
14. The deletion must continue to honour the existing "Deleting" status blocking (`ConnectedSystemStatus.Deleting`) so no synchronisation operation interferes mid-deletion, and must integrate with the existing queue-after-running-sync behaviour.
15. The default mode must be decided per the Decisions section below; whichever is chosen, the destructive/side-effecting nature of Synchronised Deprovisioning (exports fired at other systems) must require explicit, informed confirmation, not a silent default.

### Non-Functional Requirements

- Teardown performance must not regress; it remains the raw-SQL fast path.
- The preview must be optional and cost-bounded at 100k-1m objects, using the #827 framework's sampling / top-N / background-job strategies rather than a synchronous full scan (a naive 100k Connected System Objects x 50 attributes is ~5m evaluations).
- Synchronised Deprovisioning throughput should be in the same cost class as an equivalent full synchronisation over the same object population; it is per-object synchronisation-engine work, not a bulk delete, and must be communicated as such in the confirmation UX (estimated time, "runs as background job").
- All new user-facing text in en-GB; JIM domain nouns Title Cased.

## Examples and Scenarios

### Scenario 1: HR migration, precedence mistake caught by preview

**Given**: An administrator is deleting the "Old HR System", which contributes to 12,847 Connected System Objects joined to shared Metaverse Objects; `manager` was accidentally left with Old HR as priority and `department`/`costCentre` precedence was moved to "New HR System".
**When**: The administrator opens the deletion dialog, sees the tier-2 counts, and clicks "Run Detailed Attribute Analysis".
**Then**: The preview reports (without changing any state): `department` will change value on 800 Metaverse Objects (new winner: New HR System), `costCentre` on 540 (new winner: New HR System), `manager` will be **recalled** on 650 Metaverse Objects, and ~1,200 exports to "Active Directory" would be triggered. The administrator spots the `manager` recall, cancels, fixes precedence, and re-runs the preview before proceeding.

### Scenario 2: Synchronised Deprovisioning executes the previewed impact

**Given**: The administrator has reviewed the preview and selects **Synchronised Deprovisioning**, typing the system name to confirm.
**When**: The deletion runs.
**Then**: Each Connected System Object is processed through obsoletion handling; contributed attributes are recalled or re-flow to the next contributor per precedence, Metaverse Object deletion rules are evaluated, and Pending Exports are queued to the affected downstream systems, matching the preview. The run executes on the worker as a resumable background job, reports progress and a final summary Activity, then the Connected System is removed.

### Scenario 3: Teardown for a misconfigured test system

**Given**: The administrator is removing a test Connected System whose contributed values should simply be abandoned, not re-flowed or exported downstream.
**When**: The administrator selects **Teardown** and confirms.
**Then**: The system and its objects are bulk-deleted atomically; surviving Metaverse Objects keep this system's values with provenance cleared; fully-orphaned Metaverse Objects are marked for deletion by `MarkOrphanedMvosForDeletionAsync`; no recall, precedence re-evaluation, or downstream exports occur.

### Scenario 4: Resumable failure mid-deprovisioning

**Given**: A Synchronised Deprovisioning of a 1m-object system is interrupted (worker restart) after 400k objects are processed.
**When**: The worker resumes the queued deletion task.
**Then**: Processing continues from the last committed checkpoint; already-recalled attributes are not recalled twice and already-queued exports are not duplicated; the final Activity summarises the whole run including the interruption.

## Constraints

- Must work fully on-premises / air-gapped; no cloud dependencies.
- Must not bypass n-tier layering: UI/API call `JimApplication`; execution lives in `JIM.Application`/`JIM.Worker`, not in controllers.
- Synchronisation Integrity rules apply in full: fast/hard failures over corrupted state, all errors via RPEIs/Activities, batch summary statistics.
- Must reuse the existing obsoletion processors and the existing "Deleting" status / queue-after-sync machinery; no parallel recall implementation.
- The preview must be an #827 adapter; it must not ship as a bespoke preview engine.

## Affected Areas

| Area | Impact |
|------|--------|
| Application | `ConnectedSystemServer.ExecuteDeletionAsync` gains a deletion-mode parameter; new orchestration for the synchronised path reusing obsoletion processors; registration of the deletion adapter on the #827 preview framework |
| Worker | New/extended worker task for resumable per-object Synchronised Deprovisioning; checkpointing; progress/summary reporting; reuse of obsoletion/export evaluation |
| Models | Deletion-mode enum; preview result models (attribute impact summary, top-N attribute detail, per-target export counts) placed in `JIM.Models`; alignment with the #827 adapter contract |
| API | Deletion endpoint gains mode selection; preview endpoint delivered via the #827 framework's adapter surface (not a bespoke controller) |
| UI | Delete Connected System dialog: mode choice (Teardown vs Synchronised Deprovisioning), "Run Detailed Attribute Analysis" tier-3 preview panel, top-N attribute list, export-by-target summary, name-to-confirm |
| Database | Likely none new beyond checkpoint/progress state for the resumable worker task (confirm against existing worker-task and Activity schema) |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/...` | Administrator guide for deleting a Connected System: explain Teardown vs Synchronised Deprovisioning, when to choose each, and how to read the attribute impact preview |
| `engineering/...` | Update the Change Preview framework design (owned by #827) to include the Connected System deletion adapter once the framework lands; correct #134's stale `docs/CONNECTED_SYSTEM_DELETION_DESIGN.md` reference to `engineering/plans/done/` |

Do not retro-edit the completed `engineering/plans/done/CONNECTED_SYSTEM_DELETION_DESIGN.md`; it is a point-in-time record.

## Dependencies

- **#827 (blocking for the preview):** the unified Change Preview framework design must be agreed first; #134's preview is an adapter on it. The execution side (#809) can be designed in parallel but the two must share evaluation logic, so sequencing matters.
- **#288 (Sync Preview engine):** the tier-3 evaluation engine the #827 framework reuses; the deletion adapter's per-object impact computation builds on it.
- **#135 (shipped):** the tier-2 count preview this feature extends.
- **#363 (shipped):** the `SyncOutcome` causal graph model preview results reuse.
- The existing obsoletion processors, `MarkOrphanedMvosForDeletionAsync`, `RemoveContributedAttributesOnObsoletion`, and the "Deleting" status / queue-after-sync machinery.

## Open Questions

1. Exactly where does the preview computation run, and how is the "proposed configuration" (a pending deletion) represented to the #827 framework? These are #827-owned and must be settled by the framework design before the adapter is built.
2. What are the checkpoint/batch boundaries for resumable Synchronised Deprovisioning such that recall and export generation are idempotent on resume?
3. Should the preview results be persisted as an Activity for audit ("previewed X, proceeded anyway"), or transient? (Mirrors #827 open question 4.)
4. For Synchronised Deprovisioning, does the operation strictly honour each object type's `RemoveContributedAttributesOnObsoletion`, or does the deletion case ever need to override it? (See Decisions.)

## Acceptance Criteria

- [ ] Agreed two-mode model (Teardown vs Synchronised Deprovisioning): naming, default, and confirmation UX, integrated with the #134 preview as the validation step.
- [ ] Agreed decision on the default mode and the confirmation requirement for the side-effecting path.
- [ ] #134 attribute impact preview specified and delivered as the Connected System deletion **adapter on the #827 framework**, at tier 3, sharing evaluation logic with execution; not a bespoke preview.
- [ ] Synchronised Deprovisioning executes attribute recall (with precedence re-evaluation), Metaverse Object deletion-rule evaluation, and downstream export generation, matching what a normal synchronisation disconnect would produce, verified by integration tests.
- [ ] Execution runs on the worker as a bounded, resumable background job with progress and summary reporting via Activities/RPEIs; interrupted runs resume without double-processing.
- [ ] Teardown path verified unchanged (performance and behaviour).
- [ ] Follow-up implementation issues split out from this agreed design (execution, adapter, UI), sequenced against the #827 framework.
- [ ] #134's stale `docs/CONNECTED_SYSTEM_DELETION_DESIGN.md` reference corrected.

## Additional Context

- Origin of #134: the "Future Enhancement: Attribute Impact Analysis" section of `engineering/plans/done/CONNECTED_SYSTEM_DELETION_DESIGN.md` (#135).
- The recent deletion FK/atomicity fix (branch `fix/delete-connected-system-syncrulemapping-columns`) that deferred attribute recall to #809.
- #827 June 2026 decision: framework-first, holistic; per-surface previews (including #134) are adapters, not independent builds.

## Decisions Needed

The product owner must decide the following before implementation issues are split out. Each carries a recommendation.

1. **Default deletion mode: Teardown or Synchronised Deprovisioning?**
   *Recommendation:* Default to **Synchronised Deprovisioning**, with Teardown a deliberate opt-out. It is the data-integrity-correct outcome (the Metaverse and downstream systems end in a synchronisation-consistent state), and JIM's stated bias is fast/hard-correct over convenient. The cost is that deleting a shared-identity system now fires downstream exports the administrator might not expect, which is precisely why it must be gated behind the tier-3 preview and an explicit name-to-confirm. Teardown stays a first-class, clearly-labelled choice for abandon-the-data cases.

2. **Is the tier-3 preview offered or mandatory before Synchronised Deprovisioning?**
   *Recommendation:* **Offered, strongly surfaced, not mandatory.** At 1m objects a forced tier-3 scan on every deletion is a poor experience and sometimes unnecessary (the administrator may already know the blast radius). Make it one prominent click with a warning that proceeding without it is unvalidated; record in the Activity whether a preview was run.

3. **Does Synchronised Deprovisioning honour `RemoveContributedAttributesOnObsoletion` per object type, or override it for deletion?**
   *Recommendation:* **Honour the existing per-object-type setting unchanged.** Deletion should be indistinguishable from disconnecting every object through normal synchronisation; overriding recall for the deletion case would create a second, surprising recall policy and break the "preview equals execution" guarantee. If an administrator wants values abandoned rather than recalled, that intent is expressed by choosing Teardown, not by a hidden override.

4. **Sequencing against #827: does this feature wait for the framework, or ship execution first?**
   *Recommendation:* **Design both together now; implement the execution side (#809) as soon as the shared evaluation contract is fixed, but land the preview (#134) only as an #827 adapter.** #809's synchronised execution reuses existing obsoletion processors and does not itself need the preview framework, so it can proceed once the evaluation logic that the preview will also call is factored out cleanly. Shipping a bespoke #134 preview ahead of #827 is explicitly ruled out by the June 2026 decision and would create the exact UX/architecture divergence #827 exists to prevent.

5. **Preview retention: persist as an Activity or transient?**
   *Recommendation:* **Persist a lightweight audit Activity** ("preview run, N attributes recalled / M precedence shifts / K exports, administrator proceeded with mode X"). In high-trust deployments the ability to show an administrator was warned before a destructive deprovisioning is worth the small storage cost; keep it summary-level, not per-object. Align the final shape with #827 open question 4 so all preview surfaces retain consistently.
