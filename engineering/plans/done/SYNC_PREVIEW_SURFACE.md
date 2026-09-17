# Sync Preview Surface - Implementation Plan

- **Status:** Done
- **Note:** Delivered 11 September 2026 as a three-layer stack (engine, surfaces, Table view). Lineage is not offered for a speculative tree (it needs a recorded item's change type to orient the join); Timeline and Table are. A pre-existing recording quirk found by the RemainJoined fidelity pairing is filed as #1649, not fixed here.
- **Created:** 2026-09-04
- **Issue:** [#1519](https://github.com/TetronIO/JIM/issues/1519) (sub-issue of the Sync Preview epic, [#288](https://github.com/TetronIO/JIM/issues/288))
- **PRD:** [PRD_SYNC_PREVIEW_ENGINE.md](../../prd/done/PRD_SYNC_PREVIEW_ENGINE.md) (decisions D1 to D5 stand; this plan adds none that contradict them)
- **Previous plan:** [SYNC_PREVIEW_ENGINE.md](SYNC_PREVIEW_ENGINE.md) delivered the engine; this plan completes it and puts it in front of administrators

## Overview

The Sync Preview engine shipped in August 2026 as `JimApplication.SyncPreview`, read-only and side-effect free, with no administrator-facing caller (PRD decision D3). Two things stand between it and a usable capability:

1. **The destructive cascade previews one hop deep.** When a Connected System Object falls out of scope of every import Synchronisation Rule, `PreviewCsoCoreAsync` names the out-of-scope action and returns. A real synchronisation carries on: it disconnects the object, puts the Metaverse Object to its type's Deletion Rule, and when the object dies, deprovisions every downstream Connected System Object. The preview says nothing about any of that, which is precisely the part an administrator most needs to see before running a synchronisation.
2. **No surface exists.** Not in the portal, not over REST, not in PowerShell.

Delivered as a two-layer stack, engine first, because the surfaces render what the engine produces and shipping three surfaces against a one-hop cascade would bake the gap into three APIs.

| Layer | Branch | Content |
|---|---|---|
| Bottom | `feature/sync-preview-surface` | Phase 1: the cascade, engine only |
| Middle | `feature/sync-preview-surface-stack-surfaces` | Phase 2: Connections tab, portal, REST and PowerShell together |
| Top | `feature/sync-preview-surface-stack-table-view` | Phase 3: the Table view of the causality panel |

The approved design (rev 3, 11 September 2026) is the Claude artefact "Sync Preview Panel"; its decisions are in the Decisions table below.

## Business Value

- Turns a foundation into the capability #288 promised: "what would synchronising this object do?" answered end to end, including the consequences that destroy data.
- The per-object preview is the troubleshooting instrument for "why did this object not synchronise?" and "what happens to this person if I run this?", both of which currently need a synchronisation run and a read of its Activity after the fact.
- Completes the engine so that #1530 (whole-system preview surface) and the #827 configuration-change adapters that walk scope exits ([#1436](https://github.com/TetronIO/JIM/issues/1436)) stand on a cascade that matches reality.

## Technical Architecture

### Current state: where the preview stops and what the real run does instead

`SyncPreviewServer.PreviewCsoCoreAsync` (`src/JIM.Application/Servers/SyncPreviewServer.cs`), after the per-rule scope gate (#1199):

```csharp
if (inScopeRules.Count == 0 && importRules.Any(sr => sr.ObjectScopingCriteriaGroups.Count > 0))
{
    var outOfScopeAction = _syncEngine.DetermineOutOfScopeAction(cso, importRules);
    result.Warnings.Add(new SyncPreviewMessage { Code = SyncPreviewMessageCode.OutOfScope, ... });
    return result;
}
```

The real synchronisation (`SyncTaskProcessorBase`, `src/JIM.Worker/Processors/`) does this instead, and every decision in it is already a pure engine call:

| Step | Real run | Pure decision it puts the question to |
|---|---|---|
| Disconnect | records `DisconnectedOutOfScope` (with any recalled attribute values), breaks the join | `ISyncEngine.DetermineOutOfScopeAction` |
| Deletion Rule | `ProcessMvoDeletionRuleAsync` builds the remaining-connector list (one entry per still-joined Connected System Object) and applies the decision; records `MvoDeleted` or `MvoDeletionScheduled` with the reason and policy snapshot (#1086, #119) | `ISyncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingConnectedSystemIds, name)` |
| Downstream deprovisioning | `ExportEvaluationServer.EvaluateMvoDeletionsAsync` decides, per joined Connected System Object, whether a matching export Synchronisation Rule stages a delete or only disconnects; records `DeprovisionQueued` / `Disconnected` | `ISyncEngine.DecideMvoDeletionExport(cso, mvoTypeId, exportRulesByMvoTypeId, existingPendingExport)` |

The remaining-connector arithmetic ("a system holding two joined objects where one leaves is still a connector") already exists once, privately, in `PreviewDeletionEligibilityEvaluator.RemainingConnectorsAfterDisconnection` (`src/JIM.Application/Servers/Preview/`), serving the #1115 and #1436 adapters. The cascade reuses it; it does not get a third copy.

### Proposed: the cascade as speculative nodes of the real outcome types

The preview's tree is built from the **recorded** outcome types (`ActivityRunProfileExecutionItemSyncOutcomeType`), not the `Would*` types the configuration-change adapters use, because PRD decision D4 fixes one shared mapping (`SyncOutcomeNode.FromSyncOutcome`) and the fidelity tests compare preview shape to recorded shape node for node. The cascade therefore emits exactly what the run would record:

```
DisconnectedOutOfScope                       (detail count = recalled attribute values)
└── MvoDeleted | MvoDeletionScheduled        (reason text from the engine's decision)
    ├── DeprovisionQueued (target A)         (StagedChangeType = Delete)
    ├── DeprovisionQueued (target B)
    └── Disconnected (target C)              (no matching export rule: disconnect only)
```

An out-of-scope object that is **not** joined has nothing to cascade; an object whose Metaverse Object keeps another connector, or whose type's Deletion Rule is Manual, stops at the disconnect node with the engine's reason. Both are ordinary `MvoDeletionDecision.NotDeleted` outcomes and need no special casing.

What the fidelity test decides, and the plan deliberately does not assume: **the exact node shape the real run records for this scenario**, including whether downstream deprovisioning appears as children of the deletion outcome on the disconnecting object's Execution Item or as separate items joined by the causal chain. Phase 1 starts by writing that test red, reading the recorded tree, and matching it; if the recorded shape splits across Execution Items, the fidelity comparison is defined over the chain (the #1495 `CausalChain` walk) rather than one item.

Reads the cascade adds, all through the existing read-only guarded repository inside the rollback-only transaction: the Metaverse Object's joined Connected System Objects (one batched query, refreshed per page in `PreviewFullSyncAsync` alongside the outbound cache), and nothing else. Both decisions are pure over data already in the evaluation context (export Synchronisation Rules grouped by Metaverse Object Type; the type's deletion settings on the working Metaverse Object).

### Proposed: the surfaces

**Rendering.** `CausalityModelBuilder.Build` takes an `ActivityRunProfileExecutionItem` and derives the event tree from its flat outcome list plus the item's recorded change entities (the record's `ConnectedSystemObjectChange`, the Identity's `MetaverseObjectChange`) for attribute rows. A preview has neither; it carries `SyncOutcomeNode`s and `SyncPreviewAttributeFlowChange`s. Two ways to render it:

| Option | Shape | Trade-off |
|---|---|---|
| A. Builder entry point for the speculative tree | `CausalityModelBuilder.BuildSpeculative(SyncPreviewResult, CausalityPageContext)`, sharing the per-outcome event construction with `Build` through a private common core | One more public entry point; the event construction is refactored once so both paths call it. Attribute rows come from the preview's own flow changes, which is what they are |
| B. Materialise a transient Execution Item from the nodes | Inverse of `FromSyncOutcome`, then call `Build` unchanged | No builder change, but the builder reads change entities the preview never has, so attribute rows are silently empty; and a reverse mapping invites the drift D4 exists to prevent |

**Recommendation: A.** Decision recorded as D-S1 below.

**Speculative vocabulary.** A `CausalityModel.IsSpeculative` flag, set only by the preview entry point, switches every label to the conditional ("Identity would be created", "would be deprovisioned from Target") per the #1275 decision-aid vocabulary, adds a banner stating nothing has changed, and phrases the verdict sentence conditionally. Labels are a second column on `OutcomeDisplayMap` for the bounded set of outcome types the preview can emit, with a test asserting every type the engine produces has one; no string munging of the past-tense labels.

**Portal.**

- **Connections tab on the Identity view** (new; the page lists nothing about its joined objects today): one row per joined Connected System Object with the Connected System (linked to its detail page), the object (linked to its Connector Space detail page), its source/target role (derived from which enabled Synchronisation Rules of the object's type run inbound or outbound), join type, a derived **State**, and a **Preview Sync** action per row. State derives from Connected System Object status (`Normal`, `PendingProvisioning`, `Obsolete`) combined with the object's Pending Export change type and status: In sync; Update pending; Provisioning, export pending; Provisioning, exported, awaiting confirmation; Delete pending / executing; Export failed; Obsolete. One derivation in `JIM.Application`, one unit test per state, so the Connector Space list can reuse it.
- Connected System Object detail (`src/JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor`, Administrator page): a **Preview Sync** button in the header action area beside **Set Password**, opening an inline panel below the header that renders the speculative model through the existing causality components. Inline rather than a dialog, so the tree reads at full width next to the attributes it explains, as `ConfigurationChangePreviewPanel` does on the Synchronisation Rule page.
- Identity view (`src/JIM.Web/Pages/Types/View.razor`, a User-role page): the Connections tab's per-row **Preview Sync** is the primary preview (the object preview, inbound then outbound, rendered by the same panel with the run chip naming that object's Connected System); **Preview outbound from the Identity as it is now** (`PreviewSyncForMvoAsync`) is the secondary action inside the Administrator-gated Actions tab, following the #1172 pattern. The Connections tab carries the same gate. No composed "all sources at once" preview: a Full Synchronisation is always one Connected System's, so a composed preview has no real run to be fidelity-tested against.
- Synchronous and in-process: the button awaits `JimApplication.SyncPreview` directly; no Activity, nothing persisted (settled on #1519).
- A Claude artefact mock of both placements is produced before the Razor is written, per the UI-change rule.

**REST** (`[Authorize(Roles = "Administrator")]`, read-only, on the controllers that own the objects):

- `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{id}/sync-preview` (beside `GetConnectedSystemObject`)
- `GET /api/v1/metaverse/objects/{id}/sync-preview` (beside `GetObject`)
- `SyncPreviewResponse` DTO: the tree (`SyncOutcomeNodeDto`, recursive), the inbound summary, Attribute Flow changes, errors, warnings, affected Synchronisation Rules. OpenAPI regenerated (`scripts/Generate-OpenApiDoc.ps1`; the `openapi-document` CI check diffs it).

**PowerShell** (`src/JIM.PowerShell/Public/Previews/`, beside the configuration-change preview cmdlets):

- `Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId <int> -Id <guid>`
- `Get-JIMMetaverseObjectSyncPreview -Id <guid>`
- Output shapes documented per `src/JIM.PowerShell/CLAUDE.md`; Pester tests in `src/JIM.PowerShell/Tests/`.

## Implementation Phases

### Phase 1: Complete the destructive cascade (engine only; bottom layer) ✅

Red first, every step.

- [x] **1a. Fidelity pairings in `test/JIM.Worker.Tests/Workflows/SyncPreviewFidelityTests.cs`**, one per branch of the cascade, each previewing first then running the real synchronisation over the same data and diffing tree shape through `FromSyncOutcome`:
  - joined object leaves scope, `WhenLastConnectorDisconnected`, no grace period, two export Synchronisation Rules to two targets with provisioned objects, one target with no matching rule: `DisconnectedOutOfScope -> MvoDeleted -> {DeprovisionQueued, DeprovisionQueued, Disconnected}`;
  - the same with a grace period: `-> MvoDeletionScheduled`, no downstream children;
  - the Metaverse Object keeps another connector: the cascade stops at the disconnect node;
  - Deletion Rule `Manual`: stops at the disconnect node;
  - `WhenAuthoritativeSourceDisconnected` with the disconnecting system as the named source, and again with it not named.
  - The first of these is written against the recorded tree before the engine changes, so the shape the run really records is read, not assumed (see Technical Architecture).
- [x] **1b. Hoist `RemainingConnectorsAfterDisconnection`** out of `PreviewDeletionEligibilityEvaluator` into a shared internal helper with its own unit tests; both adapters and the cascade call it.
- [x] **1c. Extend `PreviewCsoCoreAsync`**: on an out-of-scope, joined object, emit the disconnect node (recalled-value count from the working Metaverse Object's contributed values, as the run counts them), load the Metaverse Object's joined objects through the guarded repository, compute the remaining connectors, put the question to `EvaluateMvoDeletionRule`, and when the fate is deletion, put each remaining joined object to `DecideMvoDeletionExport` with the context's export rules. The existing `OutOfScope` warning stays (the `Categorise` tier and callers key on it).
- [x] **1d. `PreviewFullSyncAsync`**: the per-page context refresh loads the joined-object sets for that page's out-of-scope objects in one query; `FullSyncPreviewCounts` deletion and deprovisioning counters include the cascade. `FullSyncPreviewScaleDatabaseTests` gains an out-of-scope slice of the population so the scale mechanics cover the new reads.
- [x] **1e. Unit tests in `SyncPreviewServerTests`** for each branch, and the isolation suites (`SyncPreviewIsolationDatabaseTests`, `OutboundPreviewIsolationDatabaseTests`) re-run unchanged: their byte-identical snapshot is what proves the new reads write nothing.
- [x] **1f. `engineering/SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md`** records the cascade's reads and why they are safe.
- [x] **Gate:** full solution suite green, zero warnings. The Scenario 8 baseline gate was not run: Phase 1 changed no synchronisation hot path (`SyncTaskProcessorBase` and `ExportEvaluationServer` are untouched; the cascade lives in `SyncPreviewServer` and runs only on preview), so there is nothing for the baseline to measure. **Delivered:** the fidelity pairings found that `WhenLastConnectorDisconnected` cannot fire while provisioned targets remain joined (they are connectors too), so the deletion-with-deprovisioning scenarios use `WhenAuthoritativeSourceDisconnected`; the docs say so. Downstream disconnect-only objects have no node in the recorded tree and are surfaced as a `DownstreamDisconnectOnly` warning.

### Phase 2: Connections tab, portal, REST and PowerShell together (middle layer) ✅

- [x] **2a. `CausalityModelBuilder.BuildSpeculative`** (D-S1) with the shared event core, `IsSpeculative`, speculative labels on `OutcomeDisplayMap`, and the completeness test. Plain NUnit tests in `test/JIM.Web.Tests/` for the display logic.
- [x] **2b. Artefact mock** of the Connected System Object panel and the Identity Actions-tab placement; user sign-off before Razor.
- [x] **2c. Portal**: the State derivation and its tests; the Connections tab (linked names, role, join, State, per-row Preview Sync); the Connected System Object page button; the Identity Actions-tab outbound action; the inline panel. bUnit tests that the panel renders a speculative model with the banner and conditional labels, that the Connections tab renders every State, and that both Identity affordances are inside the Administrator gate.
- [x] **2d. REST**: the two endpoints, DTOs, `[Authorize]`, tests in `test/JIM.Web.Api.Tests/` (happy path, 404 for an unknown object, 403 for a non-administrator), OpenAPI regenerated.
- [x] **2e. PowerShell**: the two cmdlets, parameter aliases per `src/JIM.PowerShell/CLAUDE.md`, documented output shapes, Pester tests.
- [x] **2f. Docs and changelog**: a new `docs/configuration/sync-preview.md` (what the preview shows, the "would" reading, the cascade, what it cannot know), cross-linked from `configuration-changes.md` and `concepts/synchronisation-pipeline.md`; `docs/powershell/previews.md` gains the two cmdlets; one ✨ entry under `[Unreleased]` citing #1519.
- [x] **Gate:** full solution suite green; `dotnet test test/JIM.Web.Tests/`; Pester green; runtime check in the devcontainer stack on both pages with an out-of-scope joined object, confirming the cascade renders and the integrity tables are unchanged afterwards (`psql` before/after counts).

### Phase 3: Table view of the causality panel (top layer) ✅

A third projection of the causality model beside Timeline and Lineage, for recorded Execution Items as much as for previews.

- [x] **3a. Model**: a flat, per-object change list derived from `CausalityModel` (object-level rows first: Scope, Join, Projection, Disconnect, Delete, Deprovision, Provision, each with the Scoping Criteria, Object Matching Rule or Deletion Rule that decided it; then attribute rows with current and would-be, or before and after for a recorded item). Plain NUnit tests over the derivation.
- [x] **3b. Component**: `CausalityTableView` with the left navigation (objects touched, grouped by role in the chain, severity dot and change count, an Everything entry that flattens with an Object column) and the grid (sortable, filterable by change type, values in the code face). The view switcher gains **Table**. bUnit tests.
- [x] **3c. Docs**: the causality documentation gains the table view; the #1519 changelog entry names it.
- [x] **Gate:** `dotnet test test/JIM.Web.Tests/` green; runtime check on a recorded Execution Item and on a preview.

## Decisions

| Id | Decision | Rationale |
|---|---|---|
| D-S1 | Speculative rendering is a builder entry point (Option A), not a transient Execution Item | Attribute rows come from the preview's own changes; no reverse mapping to drift from D4 |
| D-S2 | Cascade nodes use the recorded outcome types, never `Would*` | D4: one mapping, fidelity-comparable; `Would*` is the configuration-change vocabulary for deltas, not trees |
| D-S3 | Inline panel on the object pages, not a dialog; Identity affordance inside the Administrator-gated Actions tab | Full-width tree beside the attributes it explains; #1172 gating pattern already on that page |
| D-S4 | Cmdlets take the `Get-` verb, in `Public/Previews/` | Read-only queries, alongside `Get-JIMConfigurationChangePreview` |
| D-S5 | Synchronous, transient, in-process for per-object preview | Settled on #1519; Worker dispatch and retention belong to #1530 |
| D-S6 | The Identity page gains a Connections tab; per-object Preview Sync is the primary preview there, outbound-from-current-state the secondary | An Identity is never synchronised itself; its objects are. The engine's outbound method evaluates the Identity as it stands (it covers falling into scope and provisioning) but includes no inbound flow, so it cannot be the headline |
| D-S7 | State on the Connections tab is derived from existing data (object status plus Pending Export change type and status), never a new column | No engine work; one derivation with a test per state; reusable on the Connector Space list |
| D-S8 | The Table view is its own stack layer | Logically distinct, valuable for recorded Activities on its own, not needed to ship the preview surfaces |
| D-S9 | "Object", never "record", in every user-facing string of this work | "Record" is not a JIM term |

## Success Criteria

- Every fidelity pairing in 1a passes: the preview's cascade has the shape the real run records, for deletion, scheduled deletion, and every branch that stops short.
- The isolation suites pass unchanged after Phase 1: the cascade adds reads and no writes.
- All three surfaces ship in one PR (Phase 2); none deferred.
- An administrator can open an out-of-scope, joined Connected System Object in the portal and read, in conditional language, that it would disconnect, that its Identity would be deleted (or scheduled, or kept and why), and which target accounts would be deprovisioned.
- Scenario 8 Small within 10% of baseline after Phase 1.

## Dependencies

- None external. `ISyncEngine.EvaluateMvoDeletionRule`, `ISyncEngine.DecideMvoDeletionExport`, `PreviewDeletionEligibilityEvaluator`, the causality visualisation and the configuration-change preview REST/PowerShell patterns all exist.
- #1530 and #1436 consume the completed cascade; neither blocks this.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| The recorded tree for a scope-exit deletion has a shape the plan did not predict (deprovisioning recorded on other Execution Items, joined by the causal chain) | 1a's first test is written against the real run before any engine change; if the shape crosses items, the fidelity comparison is defined over the chain walk and the preview's tree mirrors that |
| Cascade cost at scale in `PreviewFullSyncAsync` | Joined-object sets loaded once per page, not per object; both decisions are pure; the scale test gains an out-of-scope slice so the cost is measured, and #1520 measures it at 100K when #1530 is picked up |
| A second implementation of the deletion or deprovisioning decision, drifting from the engine | None is written: the cascade calls the two `ISyncEngine` methods the run calls, and the connector arithmetic is hoisted rather than copied |
| Past-tense labels leaking into a speculative render | Speculative labels are a typed column with a completeness test over the types the engine emits; the model flag, not the page, selects them |
| Non-administrator reaching the Identity-page preview | Affordance inside the existing Administrator gate; the REST endpoint carries its own `[Authorize(Roles = "Administrator")]` and a 403 test |
