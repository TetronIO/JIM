# Configuration Change Preview Framework

- **Status:** Planned
- **Created:** 2026-07-16
- **Author:** JayVDZ
- **Issue:** [#827](https://github.com/TetronIO/JIM/issues/827)

## Problem Statement

Nearly every configuration change in JIM that alters synchronisation outcomes is applied blind. An administrator changing Synchronisation Rule scope, flipping a deprovisioning toggle, deselecting a partition, or altering deletion settings has no way to see what the change will do to production identity data before committing to it. The blast radius ranges from a handful of Attribute Flow changes to thousands of objects becoming deletion-eligible from a single dropdown.

Issue #827 mapped every sync-affecting configuration surface and found preview coverage exists for only a handful (basic deletion counts via #135, attribute deletion validation via #465). Building bespoke previews per surface would produce divergent UX and duplicated architecture; the Jun 2026 decision on #827 is therefore **framework-first**: design one Configuration Change Preview framework with per-surface adapters, then implement adapters in severity order.

This PRD defines that framework. The design is deliberately **UX-first**: the architecture is derived from the administrator experience we require, not the other way round (decided Jul 2026).

## Goals

- One consistent preview experience across every sync-affecting configuration surface; an administrator who has used one preview has used them all
- Preview results reach the administrator as fast as each part can be computed; cheap results are never held back behind expensive ones
- Large result sets are summarised into grouped, risk-meaningful categories before any raw object list is shown; the administrator can assure a 4,000-object change without reading 4,000 rows
- Where the computation runs (web process or JIM.Worker) is an internal dispatch decision, invisible to the administrator
- Preview results are retained for audit: who previewed what, what the analysis said, and whether the change was applied anyway
- Per-surface previews (#204, #134, #421, #91 mode 2, and gaps G1 to G6 from #827) become thin adapters on this framework, not independent builds

## Non-Goals

- **Not building any per-surface adapter in this PRD.** Adapters are split into follow-up issues in severity order once this design is agreed (#827 acceptance criteria)
- **Not building the #288 preview engine itself.** The engine (evaluating what sync would do for a given object) is #288's scope; this framework consumes it for the object-level stage. Count and validation stages do not depend on it
- **Not building the real-time notification infrastructure.** PostgreSQL LISTEN/NOTIFY and the SignalR/Blazor push foundation are #307/#202's scope, implemented **before** this framework (#307 blocks #827, decided Jul 2026); this framework consumes that foundation through a notification abstraction
- **Not draft/staged configuration.** Proposed configuration is an unsaved DTO passed to the preview API; persisted draft configs are a separate future capability
- **Not open-ended pattern inference.** Change pattern detection is a curated, tested detector registry; no fuzzy clustering or machine-learned summarisation

## User Stories

1. As an administrator about to change a Synchronisation Rule's scoping criteria, I want to see how many and which objects fall in or out of scope before saving, so that I do not accidentally deprovision a population.
2. As an administrator changing a destructive toggle (`OutboundDeprovisionAction` to Delete, a Metaverse Object Type's `DeletionRule`), I want the save flow to show me the affected object counts and require my explicit confirmation, so that a one-line config change cannot silently cascade deletions.
3. As an administrator reviewing a large preview, I want the changes grouped into categories and patterns ("512 objects fall out of scope", "500 objects have the domain part of their email address changed"), so that I can risk-assess the change quickly and spot anomalies I would miss in a raw list.
4. As an administrator, I want to drill from any summary group into a paginated, filterable object list, so that I can spot-check specific objects I am worried about.
5. As an auditor, I want a record that an administrator previewed a change (and what the preview said) before applying it, so that change-control accountability is provable.
6. As an administrator on a surface whose preview adapter is not yet built, I want a consistent save-time acknowledgement of consequences and a "configuration changed since last full synchronisation" indicator, so that even without a preview I am never surprised silently.

## Requirements

### Functional Requirements

**The administrator experience (governs everything below)**

1. Requesting a preview MUST open the preview panel immediately; the panel MUST show generation progress and update in place as each result stage completes. The administrator never navigates away, refreshes, or chooses an execution mode.
2. Preview results are delivered in **progressive stages**, each rendered as soon as it is ready:
   - **Stage 1, Validation:** structural consequences (orphaned mappings, references to deselected attributes). Near-instant, always synchronous.
   - **Stage 2, Impact counts:** how many objects transition, per transition type ("1,204 CSOs fall out of scope; 312 MVOs become deletion-eligible").
   - **Stage 3, Change summary:** counts grouped into risk-meaningful categories (see summarisation requirements below). The **landing view** for large result sets.
   - **Stage 4, Object-level detail:** paginated, filterable per-object outcome deltas, reached by drilling into a summary group.
   Stages are an internal delivery model, not an administrator-facing choice; the panel simply fills in as results arrive.
3. Not every surface implements every stage. Severity sets the minimum: surfaces whose changes can cascade deletions or mass deprovisioning (G3 destructive toggles, G4 partition/container deselection, G5 deletion settings) MUST NOT ship without at least stages 1 and 2 plus a count-stating confirmation on apply.
4. The preview panel MUST state clearly when results are estimates or samples (see sampling, NFRs) and when underlying data has changed since the preview was generated (staleness indicator based on the source data's last import/sync timestamps).

**Framework and adapter model**

5. Each configuration surface registers a **preview adapter** implementing a common contract: given the current persisted configuration and a proposed configuration (unsaved DTO), produce (a) validation findings, (b) the affected object set, and (c) per-object outcome deltas. The framework owns everything else: orchestration, dispatch, persistence, summarisation, progress notification, retention, and the UI shell.
6. Proposed configuration is represented as an **unsaved DTO** passed to the preview API; the entity is not saved first. The DTO shapes reuse the surfaces' existing update DTOs wherever possible.
7. The framework decides **where computation runs** per request: synchronously in JIM.Web's process for cheap work, or as a JIM.Worker background task for expensive work, based on a per-adapter cost estimate (e.g. affected population size). The decision is invisible to the administrator; both paths report through the same progress mechanism.
8. Worker-executed previews are tracked as **Activities** (JIM's standard long-running operation mechanism), giving queueing, progress reporting, failure capture, and history for free.
9. A preview MUST be cancellable by the administrator, and MUST fail fast and visibly on error (consistent with synchronisation integrity rules); a failed preview never silently presents partial results as complete.

**Results model and summarisation**

10. Per-object outcome deltas are persisted as **queryable rows** (not serialised blobs), so that pagination, filtering, searching, and grouping run in PostgreSQL. Result rows reference the objects concerned (MVO/CSO), the attribute (where applicable), old and new values, and a **transition type** drawn from a common taxonomy (fell in-scope, fell out-of-scope, would join, would disconnect, attribute value change, would become deletion-eligible, would provision, would deprovision, would delete, and so on).
11. **Deterministic grouping (v1):** the change summary stage groups deltas by dimensions already present in the result rows: transition type, object type, attribute, and distinct old-to-new value pairs where cardinality is low ("`department`: Engineering to Research, 340 objects").
12. **Pattern detection (fast-follow, designed in from day one):** a curated registry of explicit, individually unit-tested detectors, each computing a pattern key per delta; grouping by pattern key yields summaries such as "500 objects: email domain changes from @old.example to @new.example". Initial detector candidates: email/UPN domain swap, DN parent path change (OU move), casing change, common prefix/suffix addition or removal. The result schema carries the pattern key column from v1 so detectors can be added without migration of the model's shape. A detector that cannot classify a delta stays silent; a wrong pattern claim is worse than none.
13. Drill-down from any group presents the object list with server-side pagination, text search, and filters on the grouping dimensions.

**Retention and audit**

14. Preview results are retained, attached to the preview Activity, so that "previewed at 14:02, applied at 14:05" is reconstructable, including what the preview reported. Retention is governed by the **existing RPEI retention period control** (decided Jul 2026); no new retention setting is introduced.
15. When a previewed change is applied, the apply Activity references the preview Activity (if one exists), so audit can distinguish "previewed then applied" from "applied blind".

**Progress notification**

16. The framework defines a **progress notification abstraction** for preview generation status and stage completion, implemented on #307's real-time foundation (PostgreSQL LISTEN/NOTIFY service-to-service, SignalR/Blazor circuit push to the browser), which is delivered before this framework (#307 blocks #827, decided Jul 2026; revised from the earlier polling-first stance). Database polling exists only as the graceful-degradation fallback when the notification path is unavailable, per #202's design.

**Interim apply-time messaging (before adapters exist)**

17. Until a surface has its adapter, it MUST apply the #91 mode 1 pattern: save-time acknowledgement of consequences, a recommendation to run a full synchronisation, and a "configuration changed since last full synchronisation" indicator. This (like preview itself) triggers only when **sync-affecting** properties change; purely cosmetic edits on the same surface (renames, descriptions) never prompt. Delivered as an **early phase of the framework implementation**, not a standalone pre-framework feature (decided Jul 2026): the acknowledgement component and the indicator are permanent parts of the end-state UX, so this phase builds them once and rolls them across surfaces; adapters then layer the preview on top.

### Non-Functional Requirements

- Preview generation MUST NOT block or degrade JIM.Web for other users; anything beyond a per-adapter cost threshold dispatches to JIM.Worker
- Must operate at customer scale (100K+ objects): count queries as set-based SQL, never object-at-a-time materialisation
- **Large previews are an informed choice, never a silent limit (decided Jul 2026):** before generation, the framework estimates the object-level result-set size; above a threshold it recommends a capped/sampled data set as the default, stating the estimated row count and storage consumption, and the administrator may choose the full data set instead. Grouped summaries are always computed exactly either way; only drill-down completeness is affected by capping, and sampled or capped results are always labelled as such in the UI (per FR4)
- No new external dependencies; air-gap deployable (LISTEN/NOTIFY and the Blazor circuit are already in-stack)
- Preview is read-only by construction: adapters and the engine MUST NOT mutate synchronisation state; enforced by the framework running analysis on read-only paths
- Preview requests are authorised the same as the configuration change itself; a user who cannot change the config cannot preview it (previews reveal data about the population)
- Preview result rows may contain personal data (attribute values); they inherit the same protection and housekeeping as Activity/RPEI data and are never logged

## Examples and Scenarios

The core scenario governs the whole framework; the rest exercise specific surfaces and stages.

### Scenario 1: The universal preview experience (core)

**Given** I am editing a sync-affecting configuration surface with unsaved changes
**When** I request a preview
**Then** the preview panel opens immediately showing generation progress, validation findings appear at once, impact counts appear as soon as computed, the grouped change summary appears when ready, and I can drill into any group for object detail; at no point am I asked where or how the computation runs, and I can cancel at any time.

### Scenario 2: Destructive toggle cannot apply silently

**Given** a Synchronisation Rule with `OutboundDeprovisionAction = Disconnect` covering 3,800 joined objects
**When** I change it to `Delete` and attempt to save
**Then** the save flow presents at minimum the impact counts ("3,800 objects would be deleted in Corporate Directory if they leave scope") and requires explicit confirmation referencing those counts before the change is persisted.

### Scenario 3: Partition deselection shows what leaves

**Given** a Connected System with an OU containing 2,150 imported objects
**When** I deselect that OU and request a preview
**Then** I see counts of objects that would stop being imported and become obsolete, broken down by object type, and the downstream consequences (recall, deprovisioning, deletion eligibility) as transition-type groups.

### Scenario 4: Expression change summarised by pattern

**Given** a proposed change to an Attribute Flow expression for `mail` affecting 4,200 MVOs
**When** the preview's change summary is ready
**Then** I see grouped patterns such as "3,700 objects: email domain changes from @contoso.example to @fabrikam.example" and "500 objects: value becomes empty", rather than a raw list of 4,200 rows
**And** drilling into the "value becomes empty" group shows those 500 objects as a paginated, searchable list.

### Scenario 5: Very large preview offers a capped default, admin may go full

**Given** a proposed matching rule change whose estimated object-level result set is 1M objects (roughly 2 GB of preview data)
**When** I request the preview
**Then** before generation begins I am told the estimated size and offered a capped/sampled data set as the recommended default, with the option to generate the full data set instead
**And** whichever I choose, the grouped change summary is exact; if I chose the cap, drill-down lists are labelled as sampled.

### Scenario 6: Long-running preview via the Worker, invisible dispatch

**Given** a preview whose affected population exceeds the in-process threshold
**When** generation is dispatched to JIM.Worker
**Then** my preview panel behaves identically to a synchronous preview (progress, staged arrival, cancel), the work is visible on the Operations page as an Activity, and other users' UI performance is unaffected.

### Scenario 7: Preview retained for audit

**Given** I generated a preview showing 312 objects would become deletion-eligible and applied the change anyway
**When** an auditor later reviews the change
**Then** the apply Activity references the preview Activity, and the preview's summary and object-level results are retrievable as they were at preview time.

### Scenario 8: Surface without an adapter still protects

**Given** a sync-affecting surface whose preview adapter is not yet built
**When** I save a change on it
**Then** I receive the standard acknowledgement of consequences and the object type shows "configuration changed since last full synchronisation" until one completes.

## Constraints

- Air-gapped deployment; no cloud services, no new external dependencies
- British English throughout UI text; JIM domain entities Title Cased
- Framework-first is decided (#827, Jun 2026): no per-surface preview may be implemented ahead of this framework design being agreed
- Must reuse the `SyncOutcome` causal graph model (#363, shipped) for object-level outcome representation rather than inventing a parallel vocabulary
- Sequencing decided (Jul 2026, resolving the earlier milestone tension): #307 blocks #827; the real-time notification foundation (#307, then #202) is implemented before this framework, so preview progress notification is real-time from day one with polling only as a degradation fallback

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New preview result tables (queryable delta rows, summary groups, pattern keys); Activity linkage |
| Models | Preview request/result models, transition-type taxonomy, adapter contract interfaces |
| Application | Preview orchestration server (dispatch, staging, summarisation); adapter registration; cost estimation |
| Worker | New worker task type for background preview generation |
| API | Preview endpoints accepting unsaved config DTOs; progress endpoint |
| UI | Shared preview panel component (progress, staged results, summary groups, drill-down grid); confirmation flows on destructive surfaces; interim messaging components |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/` | New concept page: previewing configuration changes (ships with the first adapter, not the framework skeleton) |
| `engineering/DEVELOPER_GUIDE.md` | New section: preview framework architecture and how to write an adapter |

## Dependencies

**Sequencing rule (decided Jul 2026): highlighted dependencies are implemented first.** Framework implementation begins with its true dependencies; no adapter starts before they are in place.

- #288 Sync Preview Mode: the evaluation engine consumed by stage 4 (object-level). **The only true build dependency; implemented first**, before any adapter. Stages 1 to 3 do not depend on it, so framework plumbing can proceed in parallel with engine work
- #363 `SyncOutcome` model (shipped): outcome vocabulary; nothing outstanding
- #307 / #202: the real-time notification foundation consumed by FR16. **Blocking; implemented first** (decided Jul 2026, #307 blocks #827 on GitHub; revised from the earlier polling-first, non-blocking stance). #307 delivers the foundation; #202 is its first feature slice
- #91 mode 1 pattern: source of the apply-time messaging UX, delivered as an early framework phase (FR17); coordinate with the #91 plan so both consume the same shared component and indicator
- Adapter candidates gated on this design: #204, #134/#809, #421, #91 mode 2, plus #827 gaps G1 to G6

## Open Questions

1. Sampling and persistence-cap specifics for very large populations at stage 4 (the threshold that triggers the capped-default recommendation, per-group sample sizes, top-N strategies); the behaviour is decided (informed choice, cap recommended as default, full data set always available; see Scenario 5 and the NFRs); the mechanics are proposed in the implementation plan
2. ~~Housekeeping/retention period for preview result rows~~ **DECIDED (Jul 2026): governed by the existing RPEI retention period control; no new setting** (see FR14)
3. Cost-estimation heuristic per adapter (population count thresholds? measured elapsed-time feedback?); propose in the implementation plan
4. ~~Does the interim messaging (FR17) ship as its own small issue ahead of the framework?~~ **DECIDED (Jul 2026, revised): no standalone issue; delivered as an early phase of the framework implementation plan.** No preview-adjacent work ships independently of the holistic capability; the acknowledgement component and changed-since indicator are permanent end-state components, built once in that phase

## Acceptance Criteria

- [x] Framework design agreed (this PRD reviewed and approved, Jul 2026)
- [x] Implementation plan generated from this PRD (adapter contract, result schema, dispatch, notification abstraction, UI shell) and approved (Jul 2026: [`engineering/plans/CONFIGURATION_CHANGE_PREVIEW.md`](../plans/CONFIGURATION_CHANGE_PREVIEW.md))
- [ ] Per-surface adapter issues split out in severity order: G5 and G3-destructive first, then G4, then G1/G2, then G6 and remaining toggles; #204, #134, #421, #91 mode 2 re-scoped as adapter issues
- [ ] Interim apply-time messaging delivered as an early phase of the framework implementation plan, covering all surfaces awaiting adapters
- [x] #307/#202 alignment recorded on those issues (Jul 2026: #307 blocks #827; sequencing and notifier contract recorded on #307 and in the implementation plan)

## Additional Context

- Decision record (Jun 2026, on #827): framework-first, holistic; per-surface previews must not be built independently
- Decision record (Jul 2026, this PRD): UX-first framing; progressive disclosure stages replace administrator-facing "tiers"; dispatch is invisible; deterministic grouping in v1 with a curated pattern detector registry as fast-follow; results persisted as queryable rows for pagination/filter/group-by and audit; notification abstraction in #307's decided shape (revised Jul 2026: #307/#202 are implemented before this framework, #307 blocks #827, so the abstraction is real-time from day one and polling survives only as #202's graceful-degradation fallback; supersedes the earlier polling-first stance); preview result retention governed by the existing RPEI retention period control; interim apply-time messaging delivered as an early phase of the framework implementation (revised from an earlier standalone-issue decision; no preview-adjacent work ships independently of the holistic capability); dependencies implemented first (#288 engine core before any adapter); very large previews offer a capped/sampled data set as an informed-choice default (estimated size stated up-front) rather than any hard limit, with exact summaries either way
- Prior art precedents: #465 (validation stage), #135 (count stage), #134's proposed detailed analysis and #288 (object-level stage)
- Operator experience with traditional ILM solutions motivating the summarisation requirements: spot-checking raw change lists was the only option; grouped/pattern summaries are the assurance capability that was always missing
