# Schema Refresh Decision

- **Status:** Done
- **Note:** Delivered across PRs #1488 (this PRD), #1489 (per-mapping enabled state), #1491 (the decision surface with Apply and Disable Dependents) and the Apply and Remove PR. The open questions resolved in delivery: option 2 shipped as "Apply & Disable Dependents", the posture is one per refresh (not per change kind), and options 2 and 3 landed as separate PRs.
- **Created:** 2026-08-20 (reworked 2026-08-20 after review; the original draft centred on a rare misread scenario and scattered controls across the application, both rejected)
- **Author:** Claude (from requirements stated by the project owner on #421 and in review)
- **Issue:** [#1485](https://github.com/TetronIO/JIM/issues/1485)

## Problem Statement

A schema refresh can be destructive. Additions are harmless, but the Connected System can also have removed Object Types, removed attributes, or (rarely, but legitimately, e.g. a custom application refining its schema) changed an attribute's data type. #421 delivered the pause: a refresh now retrieves the schema and shows the administrator what changed, green (additions) and red (removals and definition changes), before anything is applied, with the choice to apply or cancel.

What the pause cannot yet do is protect anything. Whichever of the two choices the administrator takes, the destructive facts still land:

- **Apply** records the new schema, but every Synchronisation Rule bound to a removed Object Type, and every Attribute Flow mapping reading a removed attribute (directly or as an input to an expression), keeps running over entries that no longer exist at the source. A mapping validated against a changed data type may now misbehave.
- **Cancel** keeps the old schema, and that is not safe either: the next Full Import finds no objects of a removed Object Type and obsoletes them all, and a changed data type means the source is now sending values the mapping was never validated for.

The administrator can see the problem and has no tool to respond to it. This PRD gives the refresh review the missing choices, all at the one place the destructive facts arrive: the Schema tab's refresh decision.

## Goals

- The refresh review presents destructive changes distinctly (red) from additions (green), and pauses; nothing is applied until the administrator chooses.
- Where the diff carries destructive changes, the administrator chooses one of three ways forward:
    1. **Cancel**, with a warning stating the specific risks of staying on the old schema (removed Object Types' objects are obsoleted by the next Full Import regardless; changed data types can break mappings).
    2. **Apply and disable dependents** (working name; a better one is wanted): the new schema is recorded, and every dependent configuration object is disabled rather than left running: Synchronisation Rules bound to a removed Object Type, and Attribute Flow mappings reading a removed or retyped attribute, including mappings where the attribute is an input to an expression.
    3. **Apply and remove**: the new schema is recorded, the dependent configuration objects are removed, and the dependent data goes with them: Connected System Objects of a removed Object Type, and stored attribute values of a removed attribute, cascading through the existing obsoletion, recall, grace-period and Deletion Rule machinery.
- Options 2 and 3 are previewed before they run (what would be disabled or removed, with counts); option 1 needs only its warning.
- Attribute Flow mappings gain an enabled/disabled state (none exists today; Synchronisation Rules have one, mappings do not), surfaced across portal, REST and PowerShell per the surface-parity rule, so option 2 has something to actuate and the administrator can re-enable per mapping afterwards.
- Everything above is audited: the chosen option is recorded on the refresh's Activity, and option 3 executes in the worker with per-object results and summary statistics.

## Non-Goals

- **No automatic application or enforcement.** A refresh never applies itself and never deletes anything without the administrator explicitly choosing option 3 on that refresh.
- **No drift-management surfaces elsewhere in the application.** The refresh review is the decision point. No standalone "remove this stale entry" affordances on schema rows, no separate reconciliation pages; a cancelled refresh is simply re-run when the administrator is ready to decide.
- **No per-item cherry-picking.** A refresh applies or cancels whole, with one of the three postures. Partial schema states multiply what the engine must honour.
- **No deletion of configuration outside option 3.** Option 2 disables; only option 3 removes, and only after its preview.

## User Stories

1. As an administrator, I want the refresh review to separate what is safe (additions) from what is destructive (removals, type changes), so I can decide with the facts in front of me rather than discovering them at the next synchronisation.
2. As an administrator facing a destructive diff, I want a middle option between "hope for the best" and "delete everything": apply the schema and have JIM disable the configuration the changes invalidated, so nothing runs wrong while I rework it.
3. As an administrator retiring a genuinely decommissioned Object Type, I want one decision that applies the schema, removes the invalidated configuration and cleans up its objects and values through the normal deprovisioning machinery, with a preview and a full audit trail.
4. As an administrator who cancelled, I want the warning to have told me exactly what staying on the old schema costs, so cancelling is an informed hold, not a trap.

## Requirements

### Functional Requirements

**The decision surface (Schema tab refresh review, extending #421's panel)**

1. The refresh preview renders additions and destructive changes as visually distinct groups (green/red), with attribute definition changes (data type, plurality) in the destructive group.
2. When the diff contains destructive changes, the panel's actions become the three options above; when it contains only additions, the existing Apply/Discard pair stands unchanged.
3. Dependent-configuration detection covers: Synchronisation Rules whose Object Type is removed; Attribute Flow mappings whose source or target attribute is removed or retyped, including mappings consuming the attribute as an expression input; and Object Matching Rules referencing a removed attribute.
4. Options 2 and 3 open a preview before committing: option 2 lists every configuration object that would be disabled; option 3 lists that plus the data impact (Connected System Objects obsoleted per removed Object Type, stored values removed per removed attribute), using the counting machinery the preview framework already has.
5. Synchronisation Rule and Object Type names shown in the review and in the option previews deep-link to their pages, opening in a new window, so the administrator can inspect a dependent without losing the review.
6. Cancel, when the diff carries destructive changes, warns concretely: objects of removed Object Types will be obsoleted by the next Full Import regardless of cancelling, and mappings over retyped attributes may misbehave. Additions-only cancels remain warning-free (#421 behaviour).

**Option 2 mechanics: apply and disable dependents**

7. Attribute Flow mappings gain a persisted enabled/disabled state with portal, REST and PowerShell write parity. A disabled mapping is skipped by synchronisation and reported as skipped on the run's Activity.
8. Disabling performed by option 2 records why (which refresh, which schema change) so the administrator later sees the cause on the rule or mapping, distinct from a disable they performed themselves. Re-enabling is a manual administrator action.
9. Synchronisation Rules disabled by option 2 use the existing rule-level Enabled state, with the same recorded reason.

**Option 3 mechanics: apply and remove**

10. Removal of dependent configuration deletes the affected Synchronisation Rules and mappings (as previewed and confirmed; this is the one sanctioned configuration-deleting path, and it is always explicit).
11. Removal of dependent data routes through existing machinery, never a new bulk-delete path: Connected System Objects of a removed Object Type are obsoleted and flow through disconnection, attribute recall (per the type's obsoletion setting), grace periods and Metaverse Deletion Rules; stored values of a removed attribute are removed via the pending-removal machinery.
12. Option 3 executes as a worker task under an audited Activity with per-object results; the portal request only queues it.

### Non-Functional Requirements

- Option 3 must scale to customer populations (100K+ objects of a removed type) by reusing the bulk obsoletion paths.
- No new environment variables; everything is admin-UI/API-driven.

## Examples and Scenarios

- **Attribute decommissioned:** HR drops `faxNumber`. Refresh shows it red. The administrator picks option 2: schema applied, the one mapping reading it (plus a mapping using it inside an expression) disabled with the refresh named as the reason. They delete the mappings at leisure, re-run the refresh choosing option 3 later, or leave it.
- **Object Type decommissioned:** the source retires `computer`. Refresh shows the type red with its dependent rule. Option 3's preview: 1 Synchronisation Rule and 4 mappings removed; 1,204 Connected System Objects obsoleted through the standard pipeline; grace periods and Deletion Rules apply. The administrator confirms; the worker executes and the Activity records every object.
- **Type refinement:** a custom application changes `employeeNumber` from Text to Number. Refresh shows the definition change red. Option 2 disables the mapping validated against Text; the administrator remaps to a Number-typed Metaverse Attribute and re-enables.
- **Not ready to decide:** the administrator cancels. The warning has told them objects of the removed type will be obsoleted at the next Full Import anyway; they pause the relevant Run Profiles and come back. Re-running the refresh reproduces the same decision.

## Acceptance Criteria

- [x] A refresh with destructive changes pauses on a review separating green from red and offering the three options; additions-only refreshes keep #421's behaviour.
- [x] Cancel over a destructive diff warns with the concrete next-sync consequences.
- [x] Option 2 applies the schema and disables every detected dependent (rules, mappings, expression-input mappings), each recording the refresh as the reason; disabled mappings are skipped-and-reported by synchronisation.
- [x] Per-mapping enabled/disabled state exists with portal, REST and PowerShell parity.
- [x] Option 3 applies the schema, removes the previewed configuration and cascades data removal through the existing obsoletion/recall/deletion-rule pipeline as an audited worker task.
- [x] Both option 2 and option 3 show an accurate preview before anything is committed.
- [x] Nothing is ever applied, disabled or removed without the administrator choosing it on that refresh.

## Dependencies

- #421 (delivered): the refresh pause, diff and preview panel this decision extends.
- Configuration Change Preview framework (#827) for the option 2/3 previews and counts.
- Existing obsoletion, recall, grace-period and Deletion Rule machinery for option 3's data cascade.

## Open Questions

- Naming for option 2 ("apply and disable dependents" / "safe mode" both feel wrong; wanted: a label an administrator reads correctly first time).
- Whether option 3 should also be offered per-kind (e.g. full removal for a decommissioned type, disable-only for a retyped attribute, within one refresh) or stay one posture per refresh.
- Delivery split: option 2 (with the per-mapping state) is buildable ahead of option 3; confirm the two land as separate PRs/phases.
