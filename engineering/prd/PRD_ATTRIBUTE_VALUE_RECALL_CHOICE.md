# Attribute Value Recall Choice

- **Status:** Planned
- **Created:** 2026-08-27
- **Author:** Jay Van der Zant (drafted with Claude)
- **Issue:** [#1537](https://github.com/TetronIO/JIM/issues/1537)

## Problem Statement

Deleting an import Synchronisation Rule whose Attribute Flow mappings were the sole contributor for one or more Metaverse attributes leaves those values in place indefinitely with null provenance: the deletion's `ON DELETE SET NULL` clears `ContributedBySyncRuleId`, so nothing any longer records where the values came from, and null-provenance values are deliberately never recalled. Administrators end up with orphaned Metaverse data they cannot reason about; the MIM "Do Not Recall Attributes" Management Agent deletion trap, reproduced.

The delete confirmation today says nothing about contributed values, the REST delete returns 204 with no trace, and there is no way to choose recall at all. Meanwhile the neighbouring behaviours have already shipped: deleting a mapping (rule survives) auto-recalls at the next Full Synchronisation (#1533 / PR #1536), and disabling a mapping or rule retains values as a pause (#1538). This PRD delivers the remaining agreed piece: an explicit, informed choice at deletion time, with recall as the default.

The governing rule, agreed 2026-08-27 after the recall-or-retain design review: **pausing a flow keeps what it contributed; removing one withdraws it, unless you say otherwise.** Divergence confusion is handled at the point of action by confirmations that state what will happen, not by flattening disable and delete into one behaviour.

## Goals

- An administrator deleting a Synchronisation Rule (or one of its Attribute Flow mappings) that sole-contributes Metaverse attribute values makes an explicit recall-or-keep choice, with **recall pre-selected** and the keep option carrying a warning that explains the consequence.
- A rule-deletion recall runs as a **queued Worker task** (it is a form of synchronisation): Activity-tracked, RPEI outcomes per object, visible and monitorable from the Operations page. The UI surfaces a link to the Activity when the recall is queued.
- Recalled values behave like any other recall: surviving lower-priority contributors are re-elected rather than attributes blanking, and resulting export changes are staged through the normal model.
- Choosing keep is safe-by-consent: the values remain with null provenance, and the administrator was told, at the moment of choice, what that means and what it would later take to remove or change them.
- **Surface parity**: the choice exists in the portal delete dialogs, on the REST delete endpoints, and on `Remove-JIMSyncRule` / `Remove-JIMSyncRuleMapping`, all defaulting to recall, all documented.
- Close the small existing parity gap found during design: a mapping can be created disabled in the portal but not via REST (`CreateSyncRuleMappingRequest` has no `Enabled`) or `New-JIMSyncRuleMapping`. Fix both.

## Non-Goals

- Changing disable semantics. Disabling a mapping or rule retains contributed values (shipped in #1538); this PRD only ensures the disable affordances *state* that behaviour.
- Recalling pre-existing null-provenance values. Values already orphaned before this ships (or orphaned via an explicit keep choice) stay untouched; they are indistinguishable from internally managed data by design.
- A general-purpose "recall arbitrary values" admin tool.
- Badging disabled flows that hold retained sole-contributor values. Noted as a possible later affordance in #1537; explicitly out of scope here.

## User Stories

1. As an identity administrator decommissioning a Connected System, I want deleting its Synchronisation Rules to withdraw the attribute values they contributed (with surviving sources taking over where they exist), so that the Metaverse does not accumulate stale data nobody owns.
2. As an identity administrator migrating authority between systems, I want the option to delete a Synchronisation Rule while keeping the values it contributed, with a clear warning of what that means, so that I can hand values over to a future flow without a destructive gap.
3. As an identity administrator, I want a recall to run as a normal queued operation with an Activity I can watch in Operations, so that I can monitor progress and outcomes per object like any other synchronisation work.
4. As an automation engineer, I want the same recall-or-keep choice on the REST API and PowerShell cmdlets with the same default, so that scripted configuration changes behave identically to the portal.

## Requirements

### Functional Requirements

**Rule deletion**

1. Deleting a Synchronisation Rule that is the sole contributor for one or more Metaverse attribute values must require a recall-or-keep choice; recall is the default on every surface.
2. When any values are affected, the portal delete confirmation must state how many objects/values are affected, present the choice with recall pre-selected, and show a warning against keep: values remain in place with no provenance, and a new inbound Attribute Flow or manual/automated removal would be needed to change them later. When no values are affected, the existing confirmation flow is unchanged (no choice shown).
3. Choosing recall enumerates the affected values **before** the delete commits (deletion severs the provenance the recall needs) and queues a Worker task to perform the recall. The task processes per Metaverse Object with RPEI outcomes, re-elects surviving contributors where they exist, clears the attribute where none do, and stages any resulting export changes through the normal Pending Export model.
4. On queuing a recall, the portal must surface a link to the recall Activity (route `/activity/{id}`) so progress can be monitored from Operations; the REST delete must return 202 Accepted with a tracking DTO carrying the Activity id when a recall was queued (204 as today when not).
5. Choosing keep deletes the rule exactly as today: values remain, provenance nulls, and the deletion Activity records that keep was chosen.

**Mapping deletion (rule survives)**

6. Deleting an Attribute Flow mapping that is the sole contributor for values must offer the same choice, same default, same warning. Recall uses the shipped #1536 mechanism, whose verified semantics are: only a synchronisation of the contributing system recalls (never another system's); a Delta Synchronisation recalls just the objects it processes; the deletion stamps the configuration watermark so the next Full Synchronisation re-evaluates every object and completes the recall; clearing the Connected System recalls nothing (Metaverse values and provenance survive a clear). The confirmation must say when the recall will take effect.
7. Choosing keep must permanently exempt those values from the #1536 orphan recall (proposed mechanism: null their `ContributedBySyncRuleId` at deletion time, making keep mean the same thing on both surfaces: a deliberate severing of provenance).
8. The portal's mapping delete (which stages the removal in the editor and persists on rule save) must carry the choice through to the save, and the REST/PowerShell mapping delete paths must take it directly.

**Disable affordances (copy only)**

9. The rule-level Enabled switch and the mapping dialog's Enabled checkbox must state the retention behaviour ("values this flow contributed stay in place until it is re-enabled or deleted"); inline helper text is sufficient, no modal required.
10. The Synchronisation Rule Danger Zone prose must mention contributed Metaverse values, not just configuration loss.

**Create-disabled parity**

11. `CreateSyncRuleMappingRequest` gains `Enabled` (default true) and `New-JIMSyncRuleMapping` gains an `-Enabled` parameter, so a mapping can be created disabled on all three surfaces as it already can in the portal.

**Cross-cutting**

12. All outcomes are Activity-reported, never silent; recall tasks log summary statistics on completion. Fast, hard failure over partial recall: a recall task that cannot complete reports failure with per-object RPEIs rather than leaving an unknown subset withdrawn.

### Non-Functional Requirements

- The affected-value count shown in confirmations must be computed without materialising the values (a count query), so the dialog stays responsive on large estates.
- The recall task must batch its work (existing 500-object batching precedent) and report progress via the Activity's processed/total counters.

## Examples and Scenarios

### Scenario 1: Decommission with surviving contributor

**Given**: HR and AD both flow `displayName`; HR (priority 1) contributed the current values; the HR Synchronisation Rule is being deleted.
**When**: The administrator deletes the rule and accepts the default (recall).
**Then**: A recall Worker task is queued and linked; for each affected Metaverse Object, AD's value is re-elected as the new contributor (no blanking); export-mapped systems receive staged updates; the Activity completes with per-object outcomes.

### Scenario 2: Sole contributor, recall

**Given**: Only HR flows `employeeId`; the HR Synchronisation Rule is deleted with recall.
**Then**: `employeeId` is cleared on the affected Metaverse Objects (No Contributor), removals staged for export where mapped, all RPEI-reported.

### Scenario 3: Sole contributor, keep

**Given**: The same rule deleted, but the administrator selects keep after reading the warning.
**Then**: The rule is deleted, values stay with null provenance, the deletion Activity records the keep choice, and nothing ever recalls those values.

### Scenario 4: Scripted deletion

**When**: `Remove-JIMSyncRule -Id 12` runs with no recall parameter.
**Then**: The values are recalled (the default matches the portal); `-KeepContributedValues` (name illustrative) opts out, and the cmdlet output includes the recall Activity's id when one is queued.

### Scenario 5: Mapping delete with keep

**Given**: A rule's `mobile` mapping is its attribute's only contributor; the mapping is deleted with keep.
**Then**: The mapping is gone, the values' provenance is severed so the next Full Synchronisation does *not* recall them, and the confirmation warned exactly that.

## Constraints

- The recall set must be captured before the rule delete commits; after commit, `ON DELETE SET NULL` makes the values unidentifiable.
- Air-gap safe, no new dependencies; British English throughout; Title Case for domain nouns in all UI copy.
- Synchronisation integrity rules apply in full (fast/hard failures, RPEI reporting, batch summary logging).

## Affected Areas

| Area | Impact |
|------|--------|
| Models | New Worker task type (e.g. `RecallSyncRuleContributionsWorkerTask`) carrying the captured recall scope and options; migration for its table |
| Database | New task DbSet + migration; no schema change to `MetaverseObjectAttributeValue` |
| Application | `DeleteSyncRuleAsync` / `DeleteSyncRuleMappingAsync` gain the choice; pre-delete recall-scope capture; recall executor (reusing the obsoletion recall / re-election machinery in `SyncTaskProcessorBase` and the #1536 orphan recall); `TaskingServer` Activity wiring |
| Worker | Dispatch case for the new task type in `Worker.cs` |
| API | DELETE `sync-rules/{id}` and `sync-rules/{id}/mappings/{mappingId}` gain the recall/keep parameter; response carries the queued Activity id; `CreateSyncRuleMappingRequest.Enabled` |
| PowerShell | `Remove-JIMSyncRule` / `Remove-JIMSyncRuleMapping` keep/recall parameter + Activity output; `New-JIMSyncRuleMapping -Enabled` |
| UI | Rule Danger Zone delete dialog (choice + count + warning + Activity link); mapping delete confirmation in the Attribute Flow tab (including the staged in-editor delete path); disable helper text |
| Tests | Worker task unit/workflow tests; API DTO tests; Pester; bUnit where dialogs warrant |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/concepts/attribute-priority.md` | Extend the delete/disable distinction with the deletion-time choice and the keep consequence |
| `docs/` Synchronisation Rule how-to/reference pages | Document the delete dialogs, the recall Activity, and the REST/PowerShell parameters |
| PowerShell cmdlet help | `Remove-JIMSyncRule`, `Remove-JIMSyncRuleMapping`, `New-JIMSyncRuleMapping` parameter docs with the keep warning |

## Dependencies

- #1538 (disable retains contributed values): merged; this builds directly on it.
- #1533 / PR #1536 (mapping orphan recall with re-election): shipped; reused as the mapping-delete recall mechanism.

## Decisions (product owner, 2026-08-27)

The three questions raised in review, resolved:

1. **Mapping-delete recall timing**: stays deferred to the next Full Synchronisation of the contributing system (the shipped #1533/#1536 mechanism); the confirmation copy owns the difference from rule deletes.
2. **Keep-choice mechanism for mapping deletes**: deliberate provenance severing; "keep" means the same thing on both surfaces.
3. **REST response shape**: follow the existing API convention for queued work, which is also REST best practice: **202 Accepted with a tracking DTO** carrying the Activity id (precedent: Connected System deletion, Run Profile execution, Auxiliary Class Discovery). A delete that queues no recall keeps its existing 204.

## Acceptance Criteria

- [ ] Deleting a last-contributing Synchronisation Rule without options recalls its values via a queued, Activity-tracked Worker task with re-election, on all three surfaces.
- [ ] The keep opt-out exists on all three surfaces behind a warning, leaves values with null provenance, and is recorded on the deletion Activity.
- [ ] The portal links the recall Activity at queue time; the REST delete returns the Activity id when a recall is queued.
- [ ] Mapping deletes offer the same choice; keep permanently exempts the values from the #1536 orphan recall.
- [ ] Disable affordances and the Danger Zone state the retention/recall behaviour in their copy.
- [ ] A mapping can be created disabled via REST and PowerShell.
- [ ] Full test coverage (TDD), changelog entry, and public docs updated in the same PR(s).

## Additional Context

- Design review: the "Recall or Retain" artefact (Option D adopted 2026-08-27); permutations, pros/cons and stress tests recorded there.
- UX sign-off: the "Recall Choice UX" artefact (2026-08-27) mocks every portal touch point (both delete dialogs, the queued-recall snackbar with Activity link, and the three copy changes); dialog copy there is the agreed baseline.
- Precedents: `ClearConnectedSystemObjectsWorkerTask` (boolean option on a task), `SchemaRefreshRemovalWorkerTask` (config decision queues a value-removing, RPEI-reporting task), `ConnectedSystemObjectType.RemoveContributedAttributesOnObsoletion` (recall-by-default with opt-out, and the re-election recall algorithm to reuse).
