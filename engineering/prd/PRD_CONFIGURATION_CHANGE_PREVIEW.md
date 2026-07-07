# Configuration Change Preview (Impact Analysis) Framework

- **Status:** Planned
- **Created:** 2026-07-07
- **Author:** Jay Van der Zant
- **Issue:** [#827](https://github.com/TetronIO/JIM/issues/827)

## Problem Statement

Nearly every configuration change in JIM that alters synchronisation outcomes is applied blind. An administrator edits a Synchronisation Rule's scoping criteria, flips a deprovision action, deselects an Organisational Unit, or changes a Metaverse Object Type's deletion rule, saves, and only discovers the blast radius on the next synchronisation run, by which point the objects have already projected, joined, flowed, deprovisioned, or been deleted. In a high-trust deployment (healthcare, finance, government) that is unacceptable: a single unticked container can deprovision thousands of objects, and a single dropdown change can turn scope exits into deletions in a target system.

Preview coverage exists today for only a handful of surfaces, and each was built bespoke:

- Basic Connected System deletion preview (count-level) shipped in #135.
- Validation on Metaverse attribute deletion / mapping removal shipped in #465.
- Expression syntax validation and single-sample preview shipped in #193.
- The `SyncOutcome` causal graph model (#363) that any preview result can reuse is shipped.
- Configuration change history with redacted, versioned snapshots (#14) is shipped.

The result is twofold. First, a coverage gap: most sync-affecting configuration surfaces offer no impact analysis at all, and the highest-severity destructive surfaces (deletion rules, deprovision/out-of-scope actions, container deselection) are exactly the ones that ship preview-less. Second, an architecture and UX divergence risk: if #204 (scope changes), #134 (system deletion), #421 (schema refresh), and #91's impact-analysis mode are each implemented independently, JIM ends up with N inconsistent previews, N result shapes, and N places for the evaluation logic to drift from the real synchronisation engine.

The attribute priority design (#91) crystallised the need. It defined a three-mode configuration change propagation model (apply-only / impact analysis / apply-and-resync). The impact-analysis mode is the same capability #204, #134, and #421 each need. Rather than build that capability four times, this PRD designs the family once: a single **Configuration Change Preview** framework with per-surface adapters, consuming the zero-side-effect evaluation engine designed in #288.

**Guiding principle:** any administrator action that changes which objects project, join, flow attributes, provision, deprovision, or delete must be able to offer an impact analysis before committing, proportionate to its blast radius.

## Goals

- Deliver one Configuration Change Preview framework: an adapter contract, a tiered result model, and a single execution path, so every sync-affecting surface previews through the same engine and presents through the same administrator UX.
- Reuse the #288 evaluation engine and the #363 `SyncOutcome` model for object-level impact; the framework must not re-implement synchronisation evaluation, only diff configuration and orchestrate the engine over the affected object set.
- Define three result tiers (Validation, Count-level, Full object-level) with a clear rule for which tier is the minimum for a given surface's severity, so destructive surfaces cannot ship preview-less.
- Establish an adapter registration contract such that #204, #134, #421, #91-mode-2, and the G1 to G6 gap surfaces below can each be added as an adapter without changing the framework core.
- Represent "proposed configuration" (unsaved edits) uniformly, so preview-before-save works identically across surfaces and across the REST API, the Blazor UI, and PowerShell.
- Confirm, at the framework level, whether and how a preview is retained for audit ("the administrator previewed this and applied anyway"), reusing the existing Activity and configuration-snapshot machinery rather than inventing a parallel store.
- Produce, as a deliverable of this design, the severity-ordered split of per-surface adapter follow-up issues, plus the interim apply-time messaging pattern for surfaces whose preview tier is not yet built.

## Non-Goals

- **Building the #288 evaluation engine.** #288 owns the zero-side-effect evaluation of the synchronisation chain for a given object against a given configuration. This PRD consumes that engine; it does not redefine its internals, its result model beyond the `SyncOutcome` contract it exposes, or its per-object semantics. If #288 is not yet delivered when a tier-3 adapter is scheduled, that adapter is blocked on #288, not on this framework.
- **Implementing every adapter now.** This PRD designs the framework and fixes the delivery order. Each per-surface adapter (scope, deletion settings, deprovision toggles, container selection, matching rules, attribute flow, schema refresh, and the rest) is its own follow-up issue with its own acceptance criteria, split out of #827 once this design is agreed.
- **Subsuming or closing #204, #134, #421, or #91.** Those issues remain the owners of their respective surface's requirements, UX detail, and PowerShell surface. This framework reclassifies them as adapter candidates and design-gates them; it does not merge their content here. See "Relationship to #204 and the other adapter candidates".
- **Changing synchronisation behaviour.** Preview must be strictly read-only against production state. Nothing this framework does may create, mutate, or delete a Metaverse Object, Connected System Object, Pending Export, or any persisted synchronisation artefact. The only writes it may perform are its own audit records (see the retention decision).
- **Previewing non-sync-affecting configuration.** Timing-only and cosmetic settings (`MaxExportParallelism`, run profile schedules, theming, API keys, certificates, log levels) have no synchronisation-outcome impact and are out of scope.
- **A general "what-if the data changed" simulator.** Preview answers "what would this *configuration* change do to the current object population", not "what would happen if the source data changed". Source-data what-if is #288's per-object preview territory, not a configuration-change adapter.

## User Stories

1. As an identity administrator about to change Synchronisation Rule scoping criteria, I want to see how many objects fall in or out of scope, and optionally which ones, before I save, so that I do not discover a mass deprovisioning on the next synchronisation run.
2. As an administrator about to flip `OutboundDeprovisionAction` from Disconnect to Delete, I want a preview that states in plain numbers how many target-system objects that turns from disconnections into deletions, and a confirmation that repeats those numbers, so that a single dropdown cannot silently cause bulk deletion.
3. As an administrator changing a Metaverse Object Type's deletion rule or trigger systems, I want to know how many existing Metaverse Objects that immediately makes eligible for deletion on the next housekeeping pass, so that I can stage the change safely.
4. As an administrator editing an Attribute Flow expression, I want to know that the change alters `displayName` on 4,200 Metaverse Objects (not merely that the expression is syntactically valid), so that I understand the population-level effect.
5. As a security or compliance officer, I want the record that an administrator ran an impact analysis and applied the change anyway to be part of the audit trail, so that a later investigation can see the change was made with knowledge of its consequences.
6. As a developer adding a new sync-affecting configuration surface, I want a documented adapter contract to implement, so that my surface gets consistent preview UX and correct evaluation for free rather than my hand-rolling a bespoke preview.
7. As an administrator using a surface whose object-level preview has not been built yet, I want at least a save-time acknowledgement of the consequences and a "configuration changed since last full synchronisation" indicator, so that no surface is silently unguarded.

## Requirements

### Functional Requirements

#### Framework core

1. The framework must define a **preview adapter contract**: given the current persisted configuration for a surface and a proposed configuration, an adapter computes (a) the set of objects potentially affected by the difference, and (b) for each requested tier, the tier's result for that surface. The contract must be surface-agnostic; the framework core must have no knowledge of scoping, deletion rules, or any specific surface.
2. The framework must expose a single entry point that, given a surface identifier, a proposed configuration, and a requested tier, dispatches to the registered adapter, runs the evaluation, and returns a uniform `ConfigurationChangePreviewResult`. Callers (REST API, Blazor UI, PowerShell) must not call adapters directly.
3. The result model must be uniform across surfaces and carry, at minimum: the surface identifier, the tier actually produced, a structural/validation section, a counts section, an optional per-object outcome-delta section (tier 3), the list of affected Synchronisation Rules / Connected Systems, and a warnings/errors collection. Per-object outcome deltas must be expressed using the #363 `SyncOutcome` model, not a bespoke shape.
4. Preview must be strictly read-only against production synchronisation state. The framework must guarantee that no adapter can create, update, or delete a Metaverse Object, Connected System Object, Pending Export, or any synchronisation artefact during a preview. This guarantee is the framework's, not each adapter's, responsibility (for example by running evaluation through the #288 engine's zero-side-effect path and never committing).
5. Each surface must declare its **minimum tier** by severity. The framework must refuse to present a destructive surface (the G3 destructive toggles, G4, G5 below) with no preview: at minimum tier 2 (counts) plus a confirmation that restates the counts.

#### Tiers

6. The framework must support three result tiers, each a strict superset of information over the one below:
   - **Tier 1, Validation** (cheap, synchronous): structural consequences of the change. Examples: "this mapping references an attribute you are deselecting", "3 Synchronisation Rules lose mappings". Precedent: #465, #421.
   - **Tier 2, Count-level impact** (moderate; synchronous or a short background job): population counts. Examples: "1,204 Connected System Objects would fall out of scope", "312 Metaverse Objects would become eligible for deletion". Precedent: #135.
   - **Tier 3, Full object-level impact** (expensive; optional; background job with sampling): per-object outcome deltas via the #288 engine. Precedent: #134's proposed detailed analysis, #288.
7. Not every surface needs every tier. The adapter declares which tiers it supports and which is its minimum; the caller requests a tier up to the adapter's maximum. Requesting an unsupported tier must fail fast with a clear message, not silently downgrade.
8. Tier 3 must support sampling and top-N summarisation (for example "showing 100 of 4,200 affected objects; summary: displayName changes on all, department changes on 1,100"), so that a preview over a large population returns in bounded time and memory. The exact sampling strategy is an adapter/engine concern; the framework must carry the sampling metadata (total affected, sample size, truncation flag) in the result.

#### Proposed-configuration representation

9. The framework must accept a **proposed configuration** that has not been saved to production. Preview-before-save is the primary use case; preview of already-saved configuration is a degenerate case (proposed == current).
10. The proposed-configuration representation must be uniform enough that the same preview entry point serves the REST API, the Blazor editor, and PowerShell, without each surface inventing its own "unsaved edit" transport. See the "Decisions needed" section for the options and the recommendation.

#### Retention and audit

11. The framework must define whether a preview result is transient or persisted, and if persisted, where. The design must reuse the existing Activity and configuration-snapshot infrastructure (#14, #363) rather than introduce a parallel audit store. See "Decisions needed".
12. Where a change is applied after a preview, it must be possible for the audit trail to associate the applied change with the preview that preceded it ("previewed then applied"), at least at tier 2+, so that a compliance investigation can establish the change was made knowingly.

#### Adapter registration and delivery order

13. The framework must provide a registration mechanism by which a surface's adapter is discovered and invoked, such that adding an adapter requires no change to the framework core, the REST entry point, or the result model.
14. The following existing issues become adapter candidates on this framework and must not be implemented independently ahead of it (design-gated by #827): #204 (scope changes), #134 (Connected System deletion impact), #421 (schema refresh), #91-mode-2 (attribute priority impact analysis). Their surface-specific requirements remain owned by those issues.
15. The gap surfaces below (identified by a codebase audit in #827; no existing issue owns their preview) must each receive an adapter, delivered in the severity-first order in the "Delivery plan" section:

    | Ref | Configuration surface | Sync impact when changed | Severity |
    |-----|----------------------|--------------------------|----------|
    | G1 | Object Matching Rule changes (add / remove / edit, order, case sensitivity), simple (per object type) and advanced (per rule) modes, including the Simple to Advanced migration | Future joins change: Connected System Objects join to different Metaverse Objects or stop joining; affects projection-versus-join and everything downstream | High (mis-joins are identity corruption) |
    | G2 | Attribute Flow mapping changes (add / remove / edit `SyncRuleMapping`, source attributes, expression edits, chained source order) | Different values flow; removed mappings stop contributing; interacts with attribute recall and priority | Medium-high (population-level impact; syntax validation and single-sample preview already exist via #193) |
    | G3 | Synchronisation Rule lifecycle and behaviour toggles: `Enabled`, `Direction`, `ProjectToMetaverse`, `ProvisionToConnectedSystem`, `EnforceState`, `InboundOutOfScopeAction` (RemainJoined to Disconnect), `OutboundDeprovisionAction` (Disconnect to Delete); rule creation and deletion | Mass effects: disabling a contributing rule stops flow for its population; flipping to Disconnect can mass-obsolete; flipping to Delete turns scope exits into target-system deletions | Highest (destructive toggles cascade deletions) |
    | G4 | Partition / container selection changes (`ConnectedSystemPartition.Selected`, `ConnectedSystemContainer.Selected`; run profile partition targeting) | Deselected objects stop importing, become obsolete, trigger recall / deprovision / deletion cascades | High (one unticked OU can deprovision thousands; related to #351) |
    | G5 | Metaverse Object Type deletion settings: `DeletionRule` (Manual / WhenLastConnectorDisconnected / WhenAuthoritativeSourceDisconnected), `DeletionGracePeriod`, `DeletionTriggerConnectedSystemIds` | Changing the rule or trigger systems can make large numbers of existing Metaverse Objects immediately eligible for deletion on the next synchronisation or housekeeping pass | Highest |
    | G6 | Connected System schema selection and obsoletion settings: object type `Selected`, attribute `Selected`, `RemoveContributedAttributesOnObsoletion` | Deselecting stops import/export of types/attributes (orphaning mappings); the recall toggle flips what happens to contributed Metaverse Object values on disconnect | Medium-high |

16. Surfaces excluded as having no synchronisation-outcome impact (timing or cosmetic only): `MaxExportParallelism`, schedules / run profile timing, theming, API keys, certificates, logs.

#### Interim apply-time messaging

17. Until a surface has its preview adapter, the framework programme must apply the #91 "apply-only" pattern uniformly across sync-affecting surfaces: a save-time acknowledgement of the consequences, a recommendation to run a full synchronisation, and a "configuration changed since last full synchronisation" indicator. This is the fallback UX and must be cheap and consistent, so that no sync-affecting surface is silently unguarded even before its adapter lands.

### Non-Functional Requirements

- **Read-only safety is paramount.** A preview must never mutate production synchronisation state. This is a hard invariant, tested, and is the single most important property of the framework. It follows directly from the Synchronisation Integrity rules (`src/JIM.Application/CLAUDE.md`).
- **Bounded resource use at scale.** Tier 2 and tier 3 previews must remain usable at customer scale (100K to 1M objects). Tier 2 must be a counting query, not a materialisation. Tier 3 must sample and summarise rather than stream every affected object into memory. A preview must never be able to exhaust worker memory or hold a long transaction that blocks synchronisation.
- **Responsiveness.** Tier 1 must be synchronous and fast (sub-second for typical configurations). Tier 2 must be either synchronous or a short background job depending on population size; the framework must decide per invocation using a cost estimate, not a fixed rule. Tier 3 is always a background job.
- **Air-gapped and self-contained.** No external services, no new cloud dependencies. Consistent with JIM's deployment model.
- **British English throughout; no em dashes; JIM domain nouns Title Cased.**

## Examples and Scenarios

### Scenario 1: Scope change, count-level preview before save (#204 adapter)

**Given** an administrator narrows an import Synchronisation Rule's scoping criteria from `Department StartsWith "Fin"` to `Department Equals "Finance"`, in the editor, unsaved
**When** they click "Preview impact"
**Then** the framework runs the scope adapter at tier 2 and returns "820 Connected System Objects currently in scope; 540 would remain; 280 would fall out of scope. Of those 280, `InboundOutOfScopeAction` is Disconnect, so 280 Metaverse Objects would be disconnected on the next synchronisation." No production object is modified.

### Scenario 2: Destructive toggle cannot ship preview-less (G3 adapter)

**Given** an administrator changes `OutboundDeprovisionAction` from Disconnect to Delete on an export Synchronisation Rule
**When** they attempt to save
**Then** the framework requires at minimum a tier-2 preview and a confirmation that restates the counts: "This change turns scope exits into deletions in the target system. 1,120 Connected System Objects currently subject to disconnect-on-exit would instead be deleted. Type DELETE to confirm." Saving is blocked until the confirmation is satisfied.

### Scenario 3: Deletion-rule change, immediate-eligibility count (G5 adapter)

**Given** an administrator changes a Metaverse Object Type's `DeletionRule` from Manual to WhenLastConnectorDisconnected
**When** they preview
**Then** the framework returns "312 existing Metaverse Objects currently have no connectors and would become immediately eligible for deletion on the next housekeeping pass, subject to the 7-day grace period." The administrator can drill into a tier-3 sample of those 312 objects.

### Scenario 4: Attribute Flow expression change, population-level impact (G2 adapter)

**Given** an administrator edits an Attribute Flow expression contributing `displayName`
**When** they preview at tier 3
**Then** in addition to the existing #193 syntax check and single-sample evaluation, the framework returns "This change alters `displayName` on 4,200 Metaverse Objects (sampled 100 shown); 0 would clear the value; 12 would produce an empty result and are flagged as warnings." Per-object deltas use the `SyncOutcome` model.

### Scenario 5: Validation-only tier for a cheap structural change (Tier 1)

**Given** an administrator removes a `SyncRuleMapping` that is the sole contributor to a Metaverse attribute
**When** they preview at tier 1
**Then** the framework returns synchronously: "Removing this mapping leaves `costCentre` with no contributor; 1 Metaverse attribute would stop being maintained." No counting query or background job runs.

### Scenario 6: Interim apply-time messaging for an unbuilt adapter

**Given** a surface whose preview adapter has not yet been delivered
**When** the administrator saves a sync-affecting change to it
**Then** they receive the #91 apply-only acknowledgement ("This change affects synchronisation outcomes. Run a full synchronisation to apply it. Configuration has changed since the last full synchronisation."), and the "configuration changed since last full synchronisation" indicator is set. No bespoke preview is invented for the surface.

## Constraints

- Must not bypass the N-tier architecture. The REST API, Blazor UI, and PowerShell must reach preview only through the `JimApplication` facade, never through repositories or adapters directly.
- Must consume #288's evaluation engine for tier 3 rather than duplicate synchronisation evaluation. A second evaluation path would drift from the real engine and produce previews that lie.
- Must reuse the shipped `SyncOutcome` model (#363) for per-object deltas and the shipped Activity / `ConfigurationSnapshot` infrastructure (#14) for any retained record.
- Background execution must use the existing `WorkerTask` and Activity mechanism (heartbeat, progress, initiator attribution, crash recovery), not a new bespoke job runner.
- Must work in air-gapped deployments; no new NuGet packages without the governance process in the root CLAUDE.md.
- British English; no em dashes; JIM domain nouns Title Cased; never the "Sync Rule" shorthand in prose.

## Affected Areas

| Area | Impact |
|------|--------|
| Application | New preview framework server (adapter registry, entry point, tier orchestration) in `JIM.Application/Servers/`; per-surface adapters added incrementally. Consumes the #288 engine and existing `ScopingEvaluationServer` / `ExportEvaluationServer`. |
| Models | New `ConfigurationChangePreviewResult` and tier sub-models in `JIM.Models/`; a proposed-configuration transport type (shape depends on the representation decision). Reuses `SyncOutcome`, `ConfigurationSnapshot`. |
| API | New preview endpoint(s) under `JIM.Web/Controllers/Api/` accepting a proposed configuration and a requested tier, returning the uniform result. `[Authorize]`, input validated at the boundary. |
| Worker | New `WorkerTask` subtype(s) for tier 2/3 background previews, with a processor in `JIM.Worker/Processors/`, mirroring the existing task/Activity lifecycle. |
| Database | Potentially none new if previews persist as Activities via existing columns; a decision output (see retention). Any new column/table is an adapter-time migration, not a framework prerequisite. |
| UI | Preview affordance ("Preview impact") on sync-affecting editors, a shared result component rendering the three tiers, and the destructive-change confirmation. Shared across surfaces, per the framework's uniform result. |
| PowerShell | `Test-`-style cmdlets wrapping the preview endpoint per surface (for example the `Test-JIMSyncRuleScope` envisaged by #204), all backed by the one framework. |

## Documentation Impact

| Doc | Change |
|-----|--------|
| `docs/` | New customer-facing "Previewing configuration changes" concept and how-to page under the Synchronisation configuration section, delivered with the first adapter (not with this design-only PRD). |
| `engineering/DEVELOPER_GUIDE.md` | Add the preview framework and adapter contract to the architecture reference once the framework core lands. |
| `engineering/` | A living design note for the adapter contract and tier model, kept current as adapters are added. Do not retro-edit this PRD or completed plans. |

## Dependencies

- **#288 Sync Preview Mode (What-If Analysis):** the zero-side-effect evaluation engine. Tier 3 for every adapter depends on it. Drafted separately; this framework consumes it and must not redefine its internals.
- **#363 RPEI Outcome Graph (shipped):** the `SyncOutcome` causal-graph model reused for per-object deltas.
- **#14 Configuration change history (shipped):** the Activity-based, redacted, versioned `ConfigurationSnapshot` infrastructure reused for retention.
- **#135 (shipped):** basic count-tier Connected System deletion preview; the precedent for tier 2.
- **#465 (shipped):** Metaverse attribute deletion / mapping-removal validation; the precedent for tier 1.
- **#193 (shipped):** expression syntax validation and single-sample preview; the G2 adapter extends rather than replaces it.
- **#91 (Configuration Change Propagation):** owns the three-mode model and the interim apply-only messaging pattern this framework reuses.

## Relationship to #204 and the other adapter candidates

**#204 (Sync rule scope management enhancements) is an adapter on this framework, not a duplicate of it, and not a thing to merge into it.** #827 was raised in part because #204's "preview the impact of scope changes before saving" and "validate if certain objects would be in or out of scope with unsaved scoping rule changes" are one instance of a capability shared by #134, #421, and #91-mode-2. Building #204's preview independently would produce exactly the UX and architecture divergence #827 exists to prevent.

The boundary is:

- **#827 (this PRD) owns** the framework: the adapter contract, the tier model, the uniform result shape, the proposed-configuration representation, the read-only guarantee, retention, and the execution path.
- **#204 owns** the scope surface's specifics: the scope-change warning banner, the `Test-JIMSyncRuleScope` cmdlet, the "is this specific object in scope" check, integration with the existing scope editor pages, and reuse of the existing `IsMvoInScopeForExportRule` / `ScopingEvaluationServer` logic. #204 implements those *as an adapter registered on this framework*, once the framework core exists.

The same boundary applies to #134 (Connected System deletion), #421 (schema refresh), and #91-mode-2 (attribute priority impact analysis): each keeps its surface-specific requirements and becomes an adapter. #827 does not close or absorb them. This PRD deliberately does not restate their requirements; it design-gates them.

## Delivery plan (severity-first)

The framework is designed and built first; adapters follow in value/severity order. Each numbered item after the framework is split into its own issue once this design is agreed.

1. **Framework core** (this PRD): adapter contract, tier model, uniform result, proposed-config representation, read-only guarantee, retention decision, execution path. Depends on #288 for tier 3.
2. **G5 (deletion settings) and G3's destructive toggles** (`OutboundDeprovisionAction`, `InboundOutOfScopeAction`): tier 2 minimum. These are the "thousands of objects deleted by one dropdown" risks and must be guarded first.
3. **G4 (partition / container deselection):** tier 2 (count of objects leaving scope).
4. **#204 (scope) and G1 (matching rules) and G2 (attribute flow / expressions):** tier 3, where the value is.
5. **G6 and the remaining G3 toggles;** and converge #134, #421, #91-mode-2 onto the framework as their own timelines allow.

## Decisions needed

Each of the four open questions from #827 is laid out below with options, trade-offs, and a recommendation. These are the decisions this design must settle before the framework core is built.

### D1: Where does preview computation run (synchronous request vs worker background job)?

- **Option A, always synchronous** (in the API request thread). Simplest; no task plumbing. Fails at scale: a tier-2 count over 1M objects or any tier-3 evaluation would time out the HTTP request and cannot report progress or recover from a crash.
- **Option B, always a background `WorkerTask`.** Uniform, gets heartbeat / progress / initiator attribution / crash recovery for free, matches the existing `SynchronisationWorkerTask` / `DeleteConnectedSystemWorkerTask` precedent. Overkill for a tier-1 validation that should feel instant; adds queue latency to a sub-second check.
- **Option C, tier-driven hybrid.** Tier 1 always synchronous. Tier 2 decided per invocation by a cheap cost estimate (affected-count threshold): small populations synchronous, large populations a background task. Tier 3 always a background `WorkerTask`.

**Recommendation: Option C.** It matches the tier model's own cost profile and the established pattern that expensive, long-running, recoverable work is a `WorkerTask` with an Activity, while cheap structural checks are synchronous. The cost estimate for the tier-2 synchronous/background boundary should be a counting query the adapter already needs, so the estimate is nearly free. Tier 3 reusing the `WorkerTask` lifecycle also gives us progress reporting and crash recovery without new infrastructure, which the read-only-safety and scale non-functional requirements both demand.

### D2: How is "proposed configuration" (unsaved edits) represented?

- **Option A, unsaved DTO passed to the preview API.** The editor serialises its in-progress edit as the same update DTO it would POST to save (for example `UpdateSyncRuleRequest`), and the preview endpoint accepts that DTO plus the target object's ID, reconstructs the proposed state in memory, and evaluates against it without persisting. This is what #204 already envisages for scoping. Nothing hits the database; the representation is exactly the shape each surface's save path already defines.
- **Option B, saved-but-inactive draft configuration.** The proposed change is persisted as a draft/version that is not yet active; preview evaluates the draft, and applying promotes it. Gives a durable artefact and a natural "previewed then applied" link, but requires every sync-affecting entity to grow a draft/active distinction, a large schema and lifecycle change, and risks a draft leaking into the active synchronisation path (a direct threat to the read-only invariant).
- **Option C, both: DTO for preview-before-save, snapshot for retention.** Preview uses the in-memory DTO (Option A); if the change is then applied and we choose to retain the preview, we record it against the resulting configuration-change Activity's existing `ConfigurationSnapshot` (from #14), which already captures the applied state.

**Recommendation: Option C (Option A representation, plus reuse of #14 snapshots for retention).** The unsaved-DTO approach keeps preview genuinely side-effect-free, needs no schema change per surface, reuses each surface's existing update-DTO contract, and works identically for the API, the Blazor editor, and PowerShell. Option B's draft/active split is a large, risky change that pushes unsaved configuration into the database where it could contaminate the active synchronisation path, which is precisely what the read-only invariant forbids. Retention (D3) is served by the already-shipped #14 snapshot on the apply-time Activity, so we do not need durable drafts to get the "previewed then applied" audit link.

### D3: Are preview results transient, or persisted as Activities for audit?

- **Option A, fully transient.** Preview computes, returns, and is forgotten. Cheapest; but leaves no record that a destructive change was made with knowledge of its impact, which a high-trust deployment's audit needs.
- **Option B, persist every preview as an Activity.** Complete trail, but tier-2/tier-3 previews are exploratory: an administrator may run five variations before saving one. Persisting all of them creates audit noise and storage churn for previews that were never applied.
- **Option C, transient by default; persist on apply.** Preview itself is transient. When a change is *applied* after a preview at tier 2 or above, the impact summary (counts, affected-rule list, tier, sample metadata) is attached to the configuration-change Activity that #14 already creates for the apply, alongside its `ConfigurationSnapshot` and optional `ChangeReason`. This yields exactly the "previewed then applied, knowing the impact" record without persisting throwaway exploratory previews.

**Recommendation: Option C.** It gives compliance the record it needs (the applied change carries its impact summary) without the noise and storage cost of persisting every exploratory preview. It reuses the #14 configuration-change Activity and snapshot rather than a parallel store, satisfying requirement 11. The impact summary is small (counts and a rule list, not per-object detail), so attaching it to the existing Activity is cheap. Tier-3 per-object detail remains transient by default; if a specific surface later needs the full delta retained, that is an adapter-level decision, not a framework default.

### D4: Which preview tiers are v1.0-mandatory, and which are later?

- **Option A, all three tiers mandatory for v1.0 across all surfaces.** Safest, but couples the whole framework's v1.0 to #288 being delivered (tier 3 depends on it) and to every adapter being built, which is not realistic for the v1.0-ILM-COMPLETE milestone.
- **Option B, tier 1 and tier 2 mandatory for the highest-severity surfaces only; tier 3 fast-follow.** For v1.0, guarantee that the destructive surfaces (G3 destructive toggles, G4, G5) ship with at least tier 2 plus confirmation, and that tier 1 validation is available wherever it is cheap. Tier 3 (object-level, #288-dependent) is a fast-follow per adapter as #288 lands and the value-tier adapters (#204, G1, G2) are built.
- **Option C, minimal: only the interim apply-only messaging for v1.0.** Cheapest, but leaves the "thousands deleted by one dropdown" risk unguarded through v1.0, which is the whole reason #827 exists.

**Recommendation: Option B.** v1.0 must not ship the destructive surfaces preview-less; tier 2 plus a count-restating confirmation on G3-destructive, G4, and G5 is the non-negotiable v1.0 floor, and tier 1 validation is cheap enough to offer wherever structural consequences exist. Tier 3 is genuinely valuable but is gated on #288 and on the value-tier adapters, so it is the right thing to fast-follow rather than block v1.0 on. Everywhere an adapter is not yet built, the interim apply-only messaging (requirement 17) is the mandated floor, so no sync-affecting surface is ever silently unguarded. This directly honours #827's "destructive surfaces should not ship preview-less" principle while keeping the v1.0 milestone achievable.

## Acceptance Criteria

- [ ] The framework defines and documents a surface-agnostic preview adapter contract; the framework core has no surface-specific knowledge.
- [ ] The framework exposes a single preview entry point returning a uniform `ConfigurationChangePreviewResult` across all surfaces, reached only through the `JimApplication` facade.
- [ ] The three tiers (Validation, Count-level, Full object-level) are specified, with per-surface minimum-tier declaration and a hard rule that destructive surfaces (G3-destructive, G4, G5) cannot present with no preview.
- [ ] Tier 3 consumes the #288 engine and expresses per-object deltas via the #363 `SyncOutcome` model; no second evaluation path is introduced.
- [ ] The read-only invariant is specified and testable: a preview cannot create, update, or delete any synchronisation artefact.
- [ ] The four decisions D1 to D4 are settled (recommendations above are adopted or explicitly overridden), and the framework design reflects the chosen options.
- [ ] The proposed-configuration representation is uniform across the REST API, Blazor UI, and PowerShell.
- [ ] Retention behaviour is defined and reuses the #14 Activity / `ConfigurationSnapshot` infrastructure rather than a parallel store.
- [ ] Background tiers use the existing `WorkerTask` / Activity lifecycle (heartbeat, progress, initiator attribution, crash recovery).
- [ ] #204, #134, #421, and #91-mode-2 are documented as adapter candidates on this framework, design-gated by #827, with their surface-specific ownership left intact.
- [ ] The G1 to G6 gap surfaces each have a follow-up adapter issue, split out in the severity-first delivery order.
- [ ] The interim apply-only messaging pattern (requirement 17) is specified as the mandated floor for surfaces awaiting their adapter.

## Additional Context

- Foundation issue and coverage map: [#827](https://github.com/TetronIO/JIM/issues/827).
- Evaluation engine: [#288](https://github.com/TetronIO/JIM/issues/288) (drafted separately; consumed here, not redefined).
- Scope adapter candidate: [#204](https://github.com/TetronIO/JIM/issues/204).
- Other adapter candidates: #134 / #809 (Connected System deletion), #421 (schema refresh), #91 (attribute priority, three-mode propagation).
- Shipped foundations: #363 (`SyncOutcome` graph), #14 (configuration change history / `ConfigurationSnapshot`), #135 (count-tier deletion preview), #465 (attribute deletion validation), #193 (expression syntax validation and single-sample preview).
- Adjacent: #351 (fine-grained container / OU selection, adjacent to G4).
- Relevant existing code: `ScopingEvaluationServer`, `ExportEvaluationServer`, `ScopeReconciliationServer` (`src/JIM.Application/Servers/`); `ConfigurationSnapshot` (`src/JIM.Models/Activities/`); `WorkerTask` and its subtypes (`src/JIM.Models/Tasking/`); the `SyncOutcome` model reachable from `SyncOutcomeBuilder` (`src/JIM.Worker/Processors/`).
