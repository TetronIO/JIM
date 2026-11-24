# Contribution Modes for Multi-Valued Metaverse Attributes: Implementation Plan

- **Status:** Doing (Phase 1 underway)
- **Issue:** [#3059](https://github.com/TetronIO/JIM/issues/3059)
- **PRD:** [`PRD_MULTI_VALUED_CONTRIBUTION_MODES.md`](../../prd/doing/PRD_MULTI_VALUED_CONTRIBUTION_MODES.md)
- **Related Issues:** #91 (attribute priority), #3064 (per-Attribute Flow recall), #1570 (grace-period freeze), #2472 (value contributor kinds), #614 (internal Metaverse Object management)
- **Last Updated:** 2026-11-23

## Overview

Adds a contribution mode to multi-valued Metaverse attributes: **Priority** (today's behaviour, the default), **Merge** (every applicable contributor adds its values and keeps them in step) and **Accumulate** (append-only; synchronisation never removes a value). The PRD covers the what and why. This plan records how it is built, the decisions the PRD left open, and where the code disagreed with the PRD's assumptions.

Delivery is three pull requests. Each one ships portal, REST API and PowerShell parity for what it adds:

| Phase | Delivers | Why this order |
|---|---|---|
| 1 | Mode configuration on all surfaces; **Accumulate** end to end | The headline scenario (account audit log). It removes nothing, so it is the lower-risk engine change, and it builds the mode plumbing Merge needs |
| 2 | **Merge** | Needs re-tagging and cross-contributor evaluation on removal (see Decision 1) |
| 3 | Manual removal of Accumulate entries; impact report and confirmation on mode change | Builds on both modes being live |

## Decisions (resolving the PRD's Open Questions)

### 1. One row per distinct value, tagged with one contributor (PRD Open Question 2)

The PRD recommended one row per value per contributor, deduplicated on read. The code makes that the expensive choice:

- Every value writer in `SyncEngine.AttributeFlow.cs` diffs against all of the attribute's rows and collapses duplicates (`ProcessTextAttribute`, `ApplyTypedValueSet<TKey>`). No path keeps two rows with the same value today.
- Export staging assumes one row per value (`SyncEngine.ExportStaging.cs`):
  - `PerValueChangeType` would stage a **Remove** for a value when one contributor's copy goes while another's stays. That is a wrong deletion at the target.
  - `WholeSetForCreate` deduplicates by row identity, not by value, so a new object would get two Adds of the value.
  - `FindMultiValuedToSingleValuedError` counts rows, not distinct values.

**Decision:** each distinct value is stored once, tagged (`ContributedBySyncRuleId`, `ContributedBySystemId`) with one contributor. The tag means "the contributor responsible for this value", not "the only contributor":

- **Accumulate:** the first contributor keeps the tag (PRD requirement 12 already says this).
- **Merge:** when the tagged contributor stops supplying a value, the engine checks whether another live contributor still supplies it. If one does, the row is **re-tagged** to it. If none does, the row is removed. Nothing downstream (export, provenance readers, change history) needs to learn about duplicates.

**Consequence for the PRD:** requirement 18 (per-value contributor) shows the one recorded contributor. A provenance view can add "also supplied by" by evaluating the other contributors when it is read, but the database does not store it.

### 2. One mode per Metaverse attribute (PRD Open Question 1)

The PRD assumed priority is stored per (Metaverse attribute, Metaverse Object Type). It is not. Priority lives on each import mapping (`SyncRuleMapping.Priority`), and the order is computed from the mappings. The object type ↔ attribute link is a many-to-many skip navigation with a bare join table (`MetaverseAttributeMetaverseObjectType`), and `src/CLAUDE.md` names it as one of the three skip navigations with a known tracking trap.

**Decision:** `ContributionMode` is a column on `MetaverseAttribute`, alongside `RenderingHint` (also only meaningful for multi-valued attributes). The same attribute has the same mode on every object type that uses it. An administrator who wants Accumulate on Person but Priority on Group creates two attributes. This can be revisited additively if demand appears.

### 3. Refuse removal of a value a live contributor supplies (PRD Open Question 3)

As the PRD recommends. No suppression list.

### 4. No cap on Accumulate growth (PRD Open Question 4)

As the PRD recommends. The Metaverse Object view pages the attribute's values (it already caps multi-valued totals through `GetXxxDetailAsync`). A warning threshold is deferred until a customer reports a real size.

### 5. Merge is refused for Reference attributes at first (PRD Open Question 5)

Reference recall already has its own writer (`ProcessReferenceAttribute`), cross-page resolution and the #1019 deletion rules. Combining those with re-tagging is a separate piece of work with no current use case. Allowing it later is additive (a refusal lifted), so it needs no major version.

### 6. Value equality is ordinal

Two values are the same value only if they are equal ordinally, the same comparison `ProcessTextAttribute` already uses (#988). `Jo@corp.example` and `jo@corp.example` are two entries. An administrator who wants them merged sets the mapping's existing inbound case normalisation (`SyncRuleMapping.CaseNormalisation`). The docs say so.

## Architecture

### Model

- `CoreEnums.cs`: `public enum AttributeContributionMode { Priority = 0, Merge = 1, Accumulate = 2 }`.
- `MetaverseAttribute.ContributionMode` (default `Priority`). Migration `AddMetaverseAttributeContributionMode`, with the column defaulting to `0`. Additive; no resolved value changes on upgrade.
- Validation, in `MetaverseServer`'s attribute create/update:
  - Non-Priority on a single-valued attribute is refused.
  - Accumulate on a Reference attribute is refused, with a message pointing to recording the reference as text (PRD Scenario 9). Merge on a Reference attribute is refused in Phase 2.
  - Changing a non-Priority attribute's plurality to single-valued is refused until its mode is set back to Priority.
  - An import mapping with a `Generation` (Unique Value Generation, #242) targeting a non-Priority attribute is refused. Generation fills a single winner's silence and has no meaning in a union.

### Engine: the write path (`SyncEngine.AttributeFlow.cs`)

`ProcessMapping` reads `syncRuleMapping.TargetMetaverseAttribute.ContributionMode`, so no extra lookup is needed.

- **Priority gate:** bypassed for non-Priority attributes. Every applicable contributor writes. The `PendingAttributeValueAdditions.RemoveAll` of other rules' pending additions must **not** run either, or a second rule in the same pass discards the first's values.
- **"Null is a value":** ignored for non-Priority attributes. A Connected No Value contributor supplies an empty set.
- **Writers:** a mode parameter on `ProcessTextAttribute` and `ApplyTypedValueSet<TKey>` (the typed writers share one helper, so the change lands in two places):
  - **Accumulate:** add source values not already held, **counting this pass's pending additions as held** (two rules on one Connected System in the same pass must not both add a value). Stage no removals. Do not call `TakeOverProvenance`.
  - **Merge:** add as Accumulate does. Then, for each held row **tagged with this contributor** whose value the contributor no longer supplies, record a removal candidate. Rows tagged with other contributors are not touched. Do not call `TakeOverProvenance`.
- **Merge removal candidates are settled after all of the object's mappings have run**, in one step per object (not per mapping):
  1. Values another contributor in this same pass supplied: re-tag to that contributor.
  2. Otherwise, evaluate the remaining contributors against their joined Connected System Objects. This is the evaluation `ContributorReElectionService` already performs; extract it into a reusable "what does contributor X supply for attribute A on this object" call rather than duplicating it.
  3. If any remaining contributor supplies the value, re-tag the row to the highest-priority such contributor (deterministic; priority is otherwise unused in Merge). Otherwise, stage the removal.
  4. If any contributor's state is undecidable (for example an undecidable scoping criterion, #2336), fail the object with an RPEI and change nothing (PRD non-functional requirement).
- **Performance:** step 2 loads other systems' Connected System Objects only when a removal candidate exists, which is rare (a value changed or left). Steady-state synchronisation adds no queries. A re-tag sets `HasPendingProvenanceChanges` and is written by the existing page flush (#2831).

### Engine: the recall paths

Every path below gets one rule: **Accumulate rows are never removed; Merge rows go through the settle step above instead of being removed outright.** A recall path that today removes "the values contributed by system S / rule R" becomes "for each such row: Priority, as today; Accumulate, skip; Merge, a removal candidate".

| Path | Entry point | Phase 1 (Accumulate) | Phase 2 (Merge) |
|---|---|---|---|
| Connected System Object obsoletion | `ConnectedSystemObjectObsoletionService` recall | Skip rows of Accumulate attributes; the grace-period unmarking ignores them | Candidates; settle |
| Scope exit / disconnection | `SyncTaskProcessorBase.RecallScopeExitContributionsAsync`, `WithdrawImportContributionsKeepingJoinAsync` | Skip | Candidates; settle |
| Leaving one rule while another covers the object (#2821) | `WithdrawDepartedRuleContributions` | Skip | Candidates; settle |
| Mapping deleted (orphans) | `RecallOrphanedContributions` | Skip | Candidates; settle |
| Mapping removed with "keep values" | `MetaverseRepository.SeverContributedValueProvenanceAsync` | Unchanged: it nulls the rule id and removes nothing, and `ContributedBySystemId` survives (PRD requirement 13) | Unchanged |
| Synchronisation Rule deletion | `RecallSyncRuleContributedValuesAsync` | Skip | Candidates; settle |
| Connected System deletion | Pass A (obsoletion), Pass B (rule recall), final raw SQL | Passes A and B skip. The final SQL nulls `ContributedBySystemId`: see Risk 2 | As Phase 1 for the SQL step |
| Connector Space clear | `DeleteAllConnectedSystemObjectsAndDependenciesAsync` | Touches no Metaverse values; nothing to do | Nothing |
| Stranded-value sweep | `ExecuteStrandedValueSweepAsync` | Skip | Candidates; settle |
| Contributor re-election | `ContributorReElectionService` | Not invoked for Accumulate attributes | Replaced by the settle step for Merge attributes |

The recall paths filter by attribute mode from the value row's attribute (`av.Attribute.ContributionMode`, or an attribute-id → mode map built once per run where rows are loaded without the navigation). Recall code that selects rows in SQL gets the mode as a join predicate, never a per-row lookup.

**Precedence with #3064:** Accumulate wins over the per-flow recall setting and over `RemoveContributedAttributesOnObsoletion`, as the PRD specifies. The single "is this row recallable?" helper above is where #3064 will plug in.

### Export

- With one row per value (Decision 1), export staging is unchanged for Accumulate and Merge: a re-tag is a provenance change, not a value change, so no Pending Export is staged for it.
- Phase 1 adds the export-flow advisory (PRD requirement 21) in the Attribute Flow editor and in the API/PowerShell response as a warning.

### Surfaces

| Surface | Phase 1 | Phase 3 |
|---|---|---|
| Portal | Mode selector on the Metaverse attribute edit page (multi-valued only, Reference excluded for Accumulate). The Attribute Priority page shows the mode and an unordered contributor list for non-Priority attributes, and refuses reordering. The Attribute Flow editor disables "Null is a value" with an explanation. The Metaverse Object view shows each value's contributor (existing provenance display); retained values whose contributor no longer supplies them are marked. The Data Flow view's Multiple contributors filter shows the mode | Remove action on Accumulate entries with confirmation; impact dialog on mode change |
| REST API | `ContributionMode` on the Metaverse attribute DTOs (read and write). `AttributePriorityOrderDto` gains `ContributionMode`, and priority writes on non-Priority attributes return 400 naming the mode | `GET .../attributes/{id}/contribution-mode-impact?mode=` ; `DELETE .../objects/{id}/attributes/{attributeId}/values` with a body naming the values; `confirm` flag on mode change away from Accumulate |
| PowerShell | `-ContributionMode` on `New-/Set-JIMMetaverseAttribute`; the output shapes of `Get-JIMMetaverseAttribute` and `Get-JIMMetaverseAttributePriority` documented; `Set-/Move-JIMMetaverseAttributePriority` surface the 400 | `Remove-JIMMetaverseObjectAttributeValue`; `-Force` on `Set-JIMMetaverseAttribute` when leaving Accumulate; impact shown under `-WhatIf` |

All new portal strings go into `src/JIM.Web/Resources/*.resx` with German, French and en-US entries in the same change.

Configuration change history: the mode is a field on `MetaverseAttribute`, which is already audited, so the change shows up in history and under Changes awaiting synchronisation through the existing path. Check this in Phase 1; do not assume it.

## Implementation Phases

### Phase 1: mode configuration and Accumulate

1. Red tests (TDD), `JIM.Worker.Tests`:
   - Writer: Accumulate adds and never removes; the first contributor keeps the tag; two rules in the same pass add a value once; "Null is a value" is ignored; the gate is bypassed.
   - Each recall path in the table skips Accumulate rows and still recalls Priority rows on the same object.
   - PRD Scenarios 1, 2 and 3 as engine tests.
2. Model, enum and migration. Run `BulkInsertColumnCompletenessTests` (no value-table column is added, so no raw SQL writer should change; confirm).
3. Validation in `MetaverseServer` (single-valued, Reference, plurality change, generation mapping), with `JIM.Web.Api.Tests` for the 400s.
4. Engine write path, then the recall paths.
5. API, PowerShell (Pester), portal, and the resx entries.
6. Docs: `docs/concepts/attribute-priority.md` (modes section; the existing diagram scoped to Priority), `docs/concepts/expression-recipes.md` (the audit log recipe with `Split`, references as text), `docs/configuration/deleted-objects.md`, the Metaverse attribute configuration page, the PowerShell and API pages. Changelog `✨` entry.
7. Runtime check in the sandbox stack: two connected systems feeding one Accumulate attribute; delete the source account; confirm the values survive and provenance reads correctly.
8. Integration: a new scenario (or a step added to the attribute priority scenario) covering PRD Scenarios 1 to 3. Search `test/integration` for readers of the Metaverse attribute and priority response shapes, and update them.

### Phase 2: Merge

1. Red tests: PRD Scenarios 4 and 5; re-tag on contributor exit; re-tag on value change when another contributor holds the value; removal when no contributor holds it; an undecidable contributor fails the object; no Pending Export is staged for a re-tag; the export-side guards still see one row per value.
2. Extract the per-contributor evaluation from `ContributorReElectionService`.
3. Writer removal candidates, the per-object settle step, and the recall paths switched to candidates.
4. Performance: Scale run comparing a Merge attribute fed by three systems with the same run in Priority mode, steady state and a 5% churn run, against the PRD's 10% budget. Record the numbers in the PR.
5. Scenarios 008 and 032 if anything in export staging was touched (`src/JIM.Application/CLAUDE.md` section 6). The design intends no change there.
6. Surfaces: Merge enabled in the mode selector, API and PowerShell; docs; changelog.

### Phase 3: manual removal and impact report

1. Application method `MetaverseServer.RemoveAccumulatedValuesAsync`: refuses non-Accumulate attributes, and refuses values a live contributor supplies (naming it, using the Phase 2 evaluation). Records a `ValueContributorKind.Person` change record and an Activity.
2. Impact calculation: a raw SQL count of objects whose values would be added or removed under the target mode. Leaving Accumulate counts rows whose tagged contributor no longer supplies them (approximated set-based: rows whose rule is deleted or whose system's object is not joined). Leaving Accumulate requires confirmation.
3. Surfaces, docs and changelog. PRD Scenarios 6 and 7 as tests.
4. In the PR that closes #3059: move the PRD and this plan to `done/`.

## Dependencies

None blocking. No new packages. #3064 (per-Attribute Flow recall) plugs into the same recallability helper when it lands, in either order.

## Risks and Mitigations

| # | Risk | Mitigation |
|---|---|---|
| 1 | A recall path is missed and silently removes Accumulate values: the exact data loss the feature exists to prevent | One shared "is this row recallable?" helper used by every path, and a test per path in the recall table. A `RequiresPostgres` test runs Connected System deletion and Synchronisation Rule deletion end to end against an Accumulate attribute |
| 2 | Connected System deletion nulls `ContributedBySystemId`, so an Accumulate value outlives its system with no recorded source | Phase 1 accepts this, and the value's change history still names the system. The provenance view shows "contributed by a deleted Connected System" rather than blank. A snapshot of the system's name on the value row is a possible later addition, deliberately not done here (it adds a value-table column and touches every raw writer) |
| 3 | Merge settle step adds cross-system queries to synchronisation | It only runs when a removal candidate exists. Measured in Phase 2 against the PRD's 10% budget |
| 4 | Accumulate combined with an export flow re-provisions values from deleted accounts | Advisory on the export flow (PRD requirement 21); documented |
| 5 | Switching an existing Priority attribute to Accumulate changes nothing until each object is synchronised | Stated in the portal and docs; listed under Changes awaiting synchronisation |

## Success Criteria

The PRD's acceptance criteria, with requirement 18 read per Decision 1 (one recorded contributor per value).
