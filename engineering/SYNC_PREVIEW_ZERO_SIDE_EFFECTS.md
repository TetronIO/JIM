# Sync Preview Engine: the Zero-Side-Effect Design

- **Purpose:** reference for contributors touching the preview paths (#288). The preview's whole value is that it never changes live data; this document records how that guarantee is constructed, which parts are structural and which are backstops, and the rules that keep it true as the code evolves.
- **Delivered by:** [`plans/done/SYNC_PREVIEW_ENGINE.md`](plans/done/SYNC_PREVIEW_ENGINE.md) (PRD: [`prd/done/PRD_SYNC_PREVIEW_ENGINE.md`](prd/done/PRD_SYNC_PREVIEW_ENGINE.md), issue [#288](https://github.com/TetronIO/JIM/issues/288)).
- **Consumers:** the #827 Configuration Change Preview framework (its Tier 3 object-level impact engine), and any future administrator-facing preview surface.

## The surface

`JimApplication.SyncPreview` (`SyncPreviewServer`, JIM.Application) exposes three methods, all read-only by construction:

| Method | What it answers |
|--------|-----------------|
| `PreviewSyncForCsoAsync(connectedSystemId, csoId)` | The full inbound-and-outbound chain for one Connected System Object: scope, join or projection, Attribute Flow, then the outbound decisions the prospective Metaverse Object state would produce. |
| `PreviewSyncForMvoAsync(mvoId)` | The outbound chain for one Metaverse Object: what each export Synchronisation Rule would stage. |
| `PreviewFullSyncAsync(connectedSystemId, options?)` | The whole system, sampled (PRD decision D2): a whole-population count tier, a bounded per-category sample of full trees, an explicit work budget, truncation flagged. |

Results are persistence-free DTOs (`SyncPreviewResult`, `FullSyncPreviewResult`, the `SyncOutcomeNode` tree; PRD decision D4). Blocking `Errors` are held programmatically apart from advisory `Warnings` (`SyncPreviewMessageCode`), and expected blocks (missing object, ambiguous match, attribute flow violations) return in `Errors` rather than throwing.

`ExportEvaluationServer.EvaluateOutboundPreviewAsync` is the lower-level evaluation-only outbound path (plan Phase 2, what #1115 consumes); the preview server composes it.

## The guarantee, layer by layer

Zero side effects is delivered as **defence in depth** (PRD requirement 8): three independent layers, so a bug in any one still cannot mutate live data.

### Layer 1: structural purity (the design)

The decisions themselves are computed by `SyncEngine`, a pure class: no repository, no DbContext, no connector field. Plain objects in, decision records out. A preview cannot write through the engine because the engine has nothing to write with. This is the load-bearing layer; the other two exist to catch orchestration mistakes around it.

The orchestration reinforces the structure:

- **Preview-owned working copies.** Inbound Attribute Flow mutates a Metaverse Object; the preview clones the object first (`CloneForPreview`) and mutates only the clone. The one shared-instance mutation (linking the CSO to the working object so the flow can run) is restored in a `finally`.
- **Probes never claim.** The join probe reads through the matching queries but never calls the claim path; export matching in the outbound preview likewise reports what a real run would claim without claiming it.
- **A projection's Metaverse Object exists only in memory.** The outbound chain evaluates it through an internal materialised-object overload, never a persisted row.

### Layer 2: the read-only guard (loud failure)

Every repository read the preview performs goes through `ReadOnlySyncRepositoryGuard` (JIM.Data), which wraps `ISyncRepository` and throws `PreviewWriteAttemptedException` from every mutating member; reads delegate. The preview constructs a **guarded sibling** `ExportEvaluationServer` over the guard, so every reused read path (cache build, page refresh, matching probe) runs under it: a future edit that adds a write to any of those paths fails loudly in the first preview test rather than committing silently.

A reflection sweep test pins the guard's classification of all `ISyncRepository` members. **Adding a member to `ISyncRepository` fails compilation until the guard classifies it**; that is deliberate. Classify it consciously: reads delegate, writes throw. Never classify a write as a delegate "because the preview doesn't call it"; the guard's contract is the whole interface.

### Layer 3: the rollback-only transaction (last resort)

The whole evaluation runs inside `BeginRollbackOnlyTransactionAsync`: a relational transaction unconditionally rolled back on disposal (null on non-relational providers, where the guard still stands). Anything that slipped both layers above is discarded, never committed. Proven against real PostgreSQL: a write made inside the scope does not survive it.

Callers can also pass a `repositoryFactory` (`ISyncRepositoryScope`) so the preview runs on its own DbContext; the worker's callers should, so the rolled-back transaction can never entangle a live run's context.

## The proofs (tests that must stay)

| Proof | Where | What it pins |
|-------|-------|--------------|
| Fidelity paired tests (release-blocking, PRD req. 9) | `SyncPreviewFidelityTests` (JIM.Worker.Tests, workflow harness) | Preview an object, really sync the same undisturbed data, map the recorded tree through the one shared `SyncOutcomeNode.FromSyncOutcome` mapping, diff the shapes node for node. A mismatch means the preview is lying. |
| Isolation snapshots (PRD req. 10) | `SyncPreviewIsolationDatabaseTests`, `OutboundPreviewIsolationDatabaseTests`, `FullSyncPreviewScaleDatabaseTests` (RequiresPostgres) | `DatabaseIsolationSnapshot` (row counts **and content digests** per integrity table) captured before and after previews against live PostgreSQL, byte-identical; the scale test does it over a 2,000-object population and two full-system walks. |
| Guard classification sweep | `ReadOnlySyncRepositoryGuard` tests (JIM.Worker.Tests) | Every `ISyncRepository` member is consciously classified; writes throw. |
| Rollback proof | `OutboundPreviewIsolationDatabaseTests` | A write inside the rollback scope is discarded on disposal. |

## Rules for contributors

1. **Never add persistence to a path the preview reuses.** The guarded sibling server means the shared read paths (`BuildExportEvaluationCacheAsync`, `RefreshExportEvaluationCacheForPageAsync`, the matching queries) must stay read-only. If a real-path optimisation needs a write there, split the method; do not gate the write on a flag.
2. **New `ISyncRepository` members: classify in the guard, consciously.** The compiler forces the edit; the sweep test forces the thought.
3. **The engine stays pure.** New outbound or inbound decisions extracted into `SyncEngine` must take their inputs as parameters (configuration included; PRD decision D5) and return decision records. No repository, no async, no I/O.
4. **Fidelity mirrors recorded behaviour, not intent.** The preview's outcome tree must match what a real synchronisation actually records. Known quirk: the real outcome builder attaches export outcomes to the RPEI's root outcome, not to the Attribute Flow child its comment describes (its child lookup keys on `ParentSyncOutcomeId`, which only resolves at bulk-insert flattening). The preview mirrors this, documented at the mirror site in `SyncPreviewServer`; if the real builder is ever fixed, the mirror and the paired tests move with it in the same change.
5. **Full-system previews stay budgeted.** `FullSyncPreviewOptions.MaxObjects` defaults on; lifting it is an explicit caller decision. Tree retention is bounded per category however large the population; a change that retains unbounded trees at scale is a regression even if every test passes.
6. **The paired fidelity tests and isolation snapshots are release blockers.** Weakening either to make a change pass is the one move this design forbids; a genuine behaviour change updates both sides (real and preview) together.

## Deliberately out of scope (as of Aug 2026)

- **No administrator-facing surface** (PRD decision D3): the `JimApplication` API is the v1.0 surface; portal/REST/PowerShell ship later as one parity-complete issue.
- **Scale-template verification deferred:** the bounding mechanics are proven at 10^3 on live PostgreSQL; the 100K+ constant-factor run needs a 20+ GB host (recorded in the plan's Phase 4).
- **Previews are transient:** computed, returned, discarded; persistence-for-audit is #827's open question, not this engine's.
