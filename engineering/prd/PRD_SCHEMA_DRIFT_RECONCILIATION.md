# Schema Drift Reconciliation

- **Status:** Planned
- **Created:** 2026-08-20
- **Author:** Claude (from requirements stated by the project owner on #421)
- **Issue:** [#1485](https://github.com/TetronIO/JIM/issues/1485)

## Problem Statement

A schema refresh never deletes: Object Types and Attributes the Connected System no longer reports are retained in JIM (deliberately; see #782), and #421 Phase 1 made that honest by previewing every refresh and flagging removals and definition changes before anything is applied. What Phase 1 does not do is give the administrator a way to **act** on the drift it reports. After applying a refresh that carries removals, JIM still holds schema entries the source no longer has:

- Their Connected System Object attribute values freeze and go on being contributed to the Metaverse indefinitely, which violates JIM's data-integrity principle: JIM no longer reflects the state of the Connected System.
- Synchronisation Rules bound to a removed Object Type, and Attribute Flow mappings reading a removed attribute, keep running over data that has stopped moving. Nothing marks them; nothing reports them as stale.
- A data type or plurality change can invalidate a mapping validated against the old definition. Phase 1 reports it; nothing helps the administrator resolve it.

The traditional ILM alternative, committing every schema change automatically, is worse: a permissions blip at the source is indistinguishable from a real removal, and an auto-commit response to it would destroy configuration and mass-delete objects irreversibly. SQL Server-based ILM systems did exactly this and it regularly broke deployments. JIM must give the administrator a safe, explicit, previewable path to reconcile instead.

## Goals

- An administrator can see, at any time, which Synchronisation Rules and Attribute Flow mappings reference schema entries the Connected System no longer reports, without hunting for them.
- Configuration invalidated by drift stops silently producing stale or wrong results: it is marked inoperable (auto-disabled with a stated reason) rather than left running or deleted.
- Inoperable marking is **reversible by the source**: if a subsequent refresh finds the entry again (a permissions blip corrected), the marking clears itself.
- An administrator can explicitly remove a retained schema entry from JIM, with the cascade (Connected System Object deletion, attribute value withdrawal, Metaverse consequences) routed through the existing obsoletion and recall pipeline, previewed via the Configuration Change Preview framework (#827) before it runs, and blocked while configuration still references the entry.
- Every destructive step is auditable: Activities and RPEIs record what was removed and why, per the synchronisation integrity rules.

## Non-Goals

- **Automatic enforcement of removals on refresh.** Applying a refresh never deletes objects, values, rules or mappings. An optional per-system policy for trusted dev environments may be considered later; it is out of scope here.
- **Per-item cherry-picking of a refresh.** A refresh applies or discards as a whole (additions are inherently safe; removals are retained either way). Item-level partial application multiplies the schema states the engine must honour, and audits have repeatedly found states it does not.
- **Deleting Synchronisation Rules or mappings automatically, under any circumstances.** Disable and surface, never delete; deletion of configuration is always an explicit administrator action.

## User Stories

1. As an administrator, I want JIM to disable and clearly flag Synchronisation Rules and mappings invalidated by schema drift, so that my synchronisations fail fast and visibly instead of flowing stale values silently.
2. As an administrator, I want an invalidated rule to recover automatically when the schema entry reappears, so that a permissions blip at the source costs me nothing.
3. As an administrator, I want to retire a removed Object Type from JIM deliberately, previewing how many objects and values that touches, so that JIM's state converges with the Connected System without surprises.
4. As an administrator, I want to be told when a data type change has invalidated a mapping, and what my options are, so that I am not debugging wrong flows after the fact.

## Requirements

### Functional Requirements

**Phase 2a: inoperable marking (safety and visibility, zero destruction)**

1. A schema refresh apply that leaves removals behind marks affected configuration **inoperable**: Synchronisation Rules whose Object Type is no longer reported, and Attribute Flow mappings whose source or target attribute is no longer reported. Inoperable is distinct from administrator-disabled: it carries a stated reason ("Object Type 'computer' is no longer reported by the Connected System; marked inoperable by the schema refresh of 20 Aug 2026") and clears automatically when a later refresh finds the entry again. Precedent: #1248's inoperable Run Profiles over deselected partitions.
2. Attribute Flow mappings need a per-mapping disabled state to support this; none exists today (`SyncRuleMapping` has no enabled flag). Add one, surfaced across portal, REST and PowerShell per the surface-parity rule, with the inoperable reason distinct from an administrator's own disable.
3. An inoperable rule or mapping is skipped by synchronisation and reported as skipped (not silently absent) on the run's Activity, satisfying "fast/hard failures over corrupted state".
4. A data type or plurality change that invalidates a mapping (source and target types no longer compatible) marks that mapping inoperable with the definition change as the reason, at refresh apply time.
5. The Schema tab, the Synchronisation Rule editor and the relevant list pages surface inoperable state visibly; counts appear on the refresh preview so the administrator sees what applying will mark.

**Phase 2b: administrator-initiated enforcement (explicit removal)**

6. A retained (no-longer-reported) Object Type or attribute offers an explicit **Remove from JIM** action. For an Object Type: its Connected System Objects are obsoleted and flow through the existing deletion pipeline (recall of contributed attributes per the type's obsoletion setting, grace periods, Metaverse deletion rules); the schema entry is deleted once its objects are gone. For an attribute: its stored values are removed via the existing pending-removal machinery; the schema entry is deleted once values are gone.
7. Removal is **blocked** while any Synchronisation Rule or mapping still references the entry (the #465 pattern): the blocking finding names each referencing rule so the administrator can retarget or delete it first. Inoperable marking does not lift the block; the reference must actually be removed.
8. Removal is previewable end-to-end on the Configuration Change Preview framework before it runs: how many Connected System Objects are obsoleted, how many Metaverse values are withdrawn or kept, which deletion rules would fire.
9. Removal executes in the worker as an audited Activity with per-object RPEIs and summary statistics, never synchronously in the portal request.

### Non-Functional Requirements

- Enforcement must scale to customer populations (100K+ objects of a removed type) by reusing the bulk obsoletion paths, not per-object EF operations.
- No new environment variables; everything is admin-UI/API-driven.

### Constraints

- Never auto-delete configuration. Never bypass the metaverse (no direct system-to-system state changes). All errors via Activities/RPEIs.

## Examples and Scenarios

- **Permissions blip:** a service account loses read access to half the directory schema; a refresh preview shows 40 "removals" plus discovery warnings. The administrator applies anyway (or a colleague does). Affected rules go inoperable with reasons; the next week's refresh, after permissions are fixed, finds the entries and the rules self-heal. Nothing was deleted at any point.
- **Real decommission:** the HR system drops its `faxNumber` column. Refresh applies; the one mapping reading it goes inoperable. The administrator deletes the mapping, then uses Remove from JIM on the attribute; the preview says 12,400 objects hold a value; values are withdrawn through the standard pipeline and the schema entry disappears.
- **Type refinement:** a custom application changes `employeeNumber` from Text to Number. The refresh preview flags the definition change; on apply, the mapping to the Text-typed Metaverse attribute is marked inoperable with the change as the reason; the administrator remaps or overrides the type.

## Acceptance Criteria

- [ ] Rules/mappings referencing entries a refresh no longer reports are marked inoperable at apply, with reasons, and are skipped-and-reported by synchronisation runs.
- [ ] Inoperable marking clears automatically when a refresh finds the entry again.
- [ ] Per-mapping enabled/disabled state exists with portal, REST and PowerShell parity.
- [ ] Remove from JIM cascades through the existing obsoletion/recall pipeline, is previewable, is blocked by referencing configuration, and is fully audited.
- [ ] Nothing in either phase deletes configuration or objects without an explicit administrator action.

## Dependencies

- #421 Phase 1 (refresh preview) delivered; the preview panel is where Phase 2a's counts and Phase 2b's actions surface.
- Configuration Change Preview framework (#827) for the enforcement preview.
- Existing obsoletion, recall, grace-period and deletion-rule machinery.

## Open Questions

- Should Phase 2a's inoperable marking happen at refresh apply only, or also retrospectively for drift already present on upgrade? (A one-off reconciliation pass on first refresh after upgrade is the likely answer.)
- Does an inoperable *inbound* rule stop deletion detection for its Object Type, and is that acceptable? Needs the same audit rigour G6 applied to selection semantics.
- Whether a per-Connected-System "auto-enforce on refresh" policy (Phase 3) is ever wanted, even for dev environments.
