# Metaverse-Derived Attribute Flows

- **Status:** Doing (Phases 0 to 4 delivered)
- **Issue:** [#1750](https://github.com/TetronIO/JIM/issues/1750)
- **PRD:** [`../../prd/doing/PRD_METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md`](../../prd/doing/PRD_METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md)
- **Related:** [#242](https://github.com/TetronIO/JIM/issues/242) Unique Value Generation (release 2 depends on this plan; see [`UNIQUE_VALUE_GENERATION.md`](UNIQUE_VALUE_GENERATION.md) decision 7 and Phases 5 and 6), [#1864](https://github.com/TetronIO/JIM/issues/1864) drift contributor fix (the bottom layer of this stack), [#1861](https://github.com/TetronIO/JIM/issues/1861) Reference inputs (deferred), [#1361](https://github.com/TetronIO/JIM/issues/1361) Missing Input Behaviour, [#91](https://github.com/TetronIO/JIM/issues/91) Attribute Priority, [#892](https://github.com/TetronIO/JIM/issues/892) Temporal Scope Reconciler, [#1781](https://github.com/TetronIO/JIM/issues/1781) feature flags, [#614](https://github.com/TetronIO/JIM/issues/614) internally managed Metaverse Objects
- **Last Updated:** 2026-09-29 (drafted from the PRD against the current code; product-owner decisions of 2026-09-28 and 2026-09-29 applied)

## Overview

An import Attribute Flow expression may read `mv["..."]` of the object being flowed. Such a flow (a **Derived Attribute Flow**, detected from its inputs, never declared) is excluded from the ordinary inbound pass and evaluated straight afterwards in a **derived pass**, level by level over a per-run **dependency graph** built from every import Synchronisation Rule, against the object's effective values as of this pass. A derived value is an ordinary contribution: it is stamped with its hosting Synchronisation Rule, resolved by Attribute Priority, subject to "Null is a value" and Missing Input Behaviour, recalled with its rule, and enters the changed-attribute set that export evaluation, drift detection and change history already read. Generated values (#242) are nodes in the same graph and are resolved per level between derived levels. Cycles are rejected at save across every rule of the Metaverse Object Type, and defensively at run start.

**A derived flow runs only in its hosting system's own synchronisation**, like every other flow on that rule. When a synchronisation (of any system), or a write outside synchronisation, changes a Metaverse attribute that a derived flow on another system's rule reads, JIM **marks** that object's Connected System Object in the hosting system, so the hosting system's next synchronisation, delta included, re-evaluates it. Sequencing sources before targets still gives same-cycle freshness; running them in the wrong order converges one cycle later instead of never.

## Business Value

- One place to define Email, UPN or Display Name for every target, visible in the portal and flowable anywhere.
- Deterministic, correctly ordered derivation in a single synchronisation (Account Name, then Email, then UPN).
- A derived value never goes silently stale when an input arrives from another system: the marking guarantees the hosting system picks the object up.
- Unblocks #242 release 2: generated base expressions reading `mv["..."]`, and Scenario 1's Email and UPN derived from a generated Account Name, which today have to be generated independently and can carry different suffixes.

## Behavioural implications (called out for the product owner)

These are the places this feature changes what an administrator must understand about synchronisation. Each was decided explicitly.

1. **Evaluation order inside one Synchronisation Rule is now dependency-driven.** Previously the order of mappings within a rule never mattered because no mapping could read another's output. JIM now orders them itself; the administrator does nothing.
2. **Sequencing across systems still matters, as it does today.** A derived flow on the HR rule reading Region from AD sees whatever Region the Metaverse holds when HR synchronises. Synchronise AD before HR for the value to be right in the same cycle. The public docs (Initialising JIM, Schedules) say so.
3. **New: JIM marks objects whose derived inputs changed elsewhere.** Without it, an HR delta synchronisation would never revisit an object whose HR record is unchanged, and even a full synchronisation skips objects unchanged since the last run, so the derived value would stay stale indefinitely. The mark is a new per-object flag on Connected System Objects, set in bulk and cleared once the object is processed. Getting the order wrong therefore costs one cycle of staleness, never permanent staleness. (Decided 2026-09-29, "Position 2".)
4. **Legacy `mv["..."]` in an import expression changes meaning.** Today it silently reads nothing. JIM is pre-release, so this needs no migration path, but the flag-off engine keeps today's behaviour until the flag is removed.

## Technical Architecture

### Current state

**Import expression evaluation.**
- `SyncEngine.FlowInboundAttributes` (`SyncEngine.cs`) loops every enabled mapping of one rule into `ProcessMapping` (`SyncEngine.AttributeFlow.cs`).
- `ProcessMapping` applies the priority gate first: only when the attribute has more than one contributor does it compare the incoming mapping against the incumbent from `FindEffectiveIncumbentSyncRuleId` (pending generation, then pending addition, then persisted value), discard a losing rule's pending additions (#1199) and park superseded generation requests. Generated mappings branch to `ProcessGeneratedMapping`; expression sources go to `ProcessExpressionMapping`.
- `EvaluateExpressionSource` builds `new ExpressionContext(metaverseAttributes: null, ...)`. `ExpressionContext` substitutes an empty dictionary for a null one, so **today an `mv["X"]` in an import expression silently reads null**; nothing rejects it at save, and the portal's expression placeholder suggests `mv["Active"]` on every rule. The #1361 comment records that `mv` on import is unsupported, and Missing Input Behaviour checks only the Connected System side.

**Effective values during a pass.** Writers stage onto `MetaverseObject.PendingAttributeValueAdditions` / `PendingAttributeValueRemovals`, and generation requests onto `PendingGeneratedValues`. `GetEffectiveAttributeValues` is persisted values minus pending removals (no pending additions). `ApplyPendingAttributeChanges` folds both lists in. The only Metaverse expression dictionary builder, `BuildAttributeDictionary` (`SyncEngine.ExportStaging.cs`), reads `AttributeValues` only, excludes `NullValue` markers, is case-insensitive, and keeps the last value of a multi-valued attribute.

**Attribute Priority.** `AttributePriorityContext` is built once per run from every rule of every Connected System, orders contributors by priority then mapping id, records disabled mappings as dormant, and answers `ShouldApply` by incumbent comparison. The worker builds it in `BuildDriftDetectionCache`; Sync Preview and three server recall paths build their own.

**The worker's per-object tail** (`ProcessMetaverseObjectChangesAsync`, `SyncTaskProcessorBase.cs`), in order: in-scope import rules; join or projection; ordinary inbound flow per in-scope rule (references deferred); orphaned-contribution recall; withdrawal re-election (`ContributorReElectionService`, which today re-flows another system's surviving rule mid-sync); inline generation resolution `ResolvePendingGeneratedValuesAsync` (one `ResolveAsync` batch per object); change capture; `ApplyPendingMetaverseObjectAttributeChanges`; export evaluation from the changed set; drift queued.

**Delta and full.** Delta selects Connected System Objects created or updated since the watermark (`GetConnectedSystemObjectsModifiedSinceAsync`). Full synchronisation skips objects flagged `IsUnchangedSinceLastSync` unless they carry `ScopeReviewPending` (#892) or Synchronisation Rule configuration changed after `ConfigurationLastFullyAppliedAt`. So neither mode revisits an object whose own Connected System Object did not change.

**Out-of-synchronisation Metaverse writes.** Synchronisation Rule deletion recall, Synchronised Deprovisioning, the stranded value sweep, `MetaverseServer.UpdateMetaverseObjectAsync`, and (#242 release 4) Collision Remediation. None is seen as a change by any later delta.

**Save-time validation.** Every mapping path in `ConnectedSystemServer` runs static validators that throw `ArgumentException` with a composed message (create, update, settings update, whole-rule save); `ValidateNoDuplicateMappingTarget(s)` is the per-mapping and whole-rule pattern to copy. The #242 flag gate is `EnsureGeneratedMappingAllowedAsync`. REST maps `ArgumentException` to 400, the portal treats it as user-safe, PowerShell relays the 400 text. `SchemaRefreshDependentDetector` is the pure static detector pattern for FR 3.

**ExpressionInputResolver.** `Resolve` returns `ExpressionInput(Source, AttributeName)`; `ResolveCached` memoises; `FindMissingInputs` checks one side against one dictionary. Metaverse inputs of an import expression are available today by filtering `Source == Metaverse`.

**Topological sort precedent.** `ExampleDataObjectType.GetTemplateAttributesInDependencyOrder`: DFS, throws naming one attribute, silently ignores a self-reference, produces an order not levels.

**Previews.** `SyncPreviewServer.PreviewCsoCoreAsync` flows inbound onto a working copy, resolves generation dry-run, captures and applies. `SyncRuleAttributeFlowPreviewAdapter` runs the engine twice and diffs.

**Portal editor.** The Expression source is a plain text field plus `ExpressionTester`; there are no attribute pickers in the expression editor today.

### Proposed solution

```
Save (portal / REST / PowerShell)
  ConnectedSystemServer validators + DerivedFlowGraph.Validate(all import rules of the type,
  proposal substituted, disabled mappings included) -> 400 naming attributes and rules on the cycle
  Reference-typed mv inputs or targets rejected (#1861); non-repeatable functions warned
  EnsureDerivedFlowAllowedAsync (flag gate on new mv-reading import expressions)

Run start (worker, previews, server recall paths)
  DerivedFlowGraphFactory.CreateAsync(flags, allSyncRules)   <- the one place the flag is read
  -> DerivedFlowGraph attached to AttributePriorityContext (null when off: legacy behaviour)
  -> hard failure if a cycle is found (defensive)

Per object (ProcessMetaverseObjectChangesAsync tail, hosting system's own synchronisation)
  ordinary inbound (derived mappings skipped) -> recall -> re-election (derived mappings skipped)
  for level L = 0..max:
     L > 0: engine.EvaluateDerivedLevel(mvo, L, in-scope rules of THIS system)   (pure, in-memory)
     pending generations present: ResolvePendingGeneratedValuesAsync (one ResolveAsync batch)
  capture changes -> collect derived-input marks for OTHER systems -> apply -> export / drift / history

Page flush
  existing sequence + one bulk UPDATE setting DerivedInputChangePending on the marked
  Connected System Objects (only when marks were collected)

Delta / full selection
  also select Connected System Objects with DerivedInputChangePending; cleared once processed
```

**DerivedFlowGraph** (`src/JIM.Application/Services/DerivedFlowGraph.cs`, pure). Built from rules, keyed per Metaverse Object Type. A mapping is derived when it is an import mapping (expression or generated) whose expression reads at least one `mv` input. Nodes are Metaverse attributes; an edge runs from each `mv` input to the derived mapping's target. Names resolve case-insensitively. Level: 0 for an attribute with no derived contributor, otherwise `1 + max(level of every input of every derived contributor)`; every derived contributor of an attribute is evaluated at the attribute's level. Kahn's algorithm; a residual set is a cycle, reported as every (attribute, mapping, rule) on it; a self-reference is a cycle of length one. API: `IsDerived(mapping)`, `GetLevel`, `MaxLevel(typeId)`, `GetDerivedMappings(typeId, level)` ordered by `(Priority, Id)`, `GetMetaverseInputIds(mapping)`, and `GetHostingSystemsReading(typeId, attributeId)` (the systems whose rules host a derived flow reading that attribute, transitively through derived levels), which drives marking.

**Effective Metaverse view.** `SyncEngine.BuildEffectiveAttributeDictionary(mvo)`: `AttributeValues` minus pending removals plus pending additions, excluding `NullValue` markers, typed exactly as `BuildAttributeDictionary` so an export expression and a derived flow reading the same attribute see the same value. Rebuilt per level (in-memory). FR 6 falls out of this.

**The derived pass (engine).** New partial `SyncEngine.DerivedFlows.cs`: `EvaluateDerivedLevel(mvo, level, rules, cso, evaluator, priorityContext, errors)`. It evaluates every derived mapping at that level on the rules in scope for the Connected System Object being processed, calling the existing `ProcessMapping` path with the effective `mv` dictionary threaded to `EvaluateExpressionSource`. The priority gate, #1199 clean-up, `ApplyNoValueOutcome`, `TakeOverProvenance` and `ProcessGeneratedMapping` are reused unchanged, so FR 7 needs no new semantics. `FlowInboundAttributes` skips `graph.IsDerived(m)` when a graph is present, which also removes derived mappings from the reference-only pass and from re-election re-flows. Every derived flow of the processed object's rules is evaluated whenever that object is processed; there is no changed-input selection, because selection by marking (below) already decides which objects are processed.

**Level interleave with generation (FR 10).** No change to the page loop's shape. The single `ResolvePendingGeneratedValuesAsync` call in the per-object tail becomes the body of a level loop: level 0 resolves the generations the ordinary pass recorded, exactly as today; level L evaluates derived flows (ordinary and generated with `mv` inputs), and any `PendingGeneratedValues` they record are resolved in one `ResolveAsync` call before level L+1 reads them.

**Marking (Position 2).** After change capture, for each Metaverse attribute changed on the object this pass (additions, removals, generated values, re-elected survivors; provenance-only takeovers excluded), look up `GetHostingSystemsReading` and collect `(MetaverseObjectId, ConnectedSystemId)` for every hosting system other than the one being synchronised. At page flush, one bulk UPDATE sets `DerivedInputChangePending = true` on the matching joined Connected System Objects. The hosting system's own rules need no mark: the derived pass already ran in this pass. Out-of-synchronisation writers call the same helper with the attributes they changed. Re-election skips derived mappings; when a withdrawn contributor leaves a derived flow as the next candidate, that is a changed attribute and so marks the hosting system.

**Selection.** New column `ConnectedSystemObjects.DerivedInputChangePending` (boolean, default false; product owner preferred a separate column to reusing `ScopeReviewPending`, 2026-09-29), with a partial index on flagged rows. Delta selection adds `OR DerivedInputChangePending`; full synchronisation treats it like `ScopeReviewPending` in the unchanged-skip test. The object's attribute values are loaded as for any changed object. The flag is cleared at page flush once Pass 2 completed without error (fail-safe, the `ScopeReviewPending` pattern). The raw-SQL bulk column lists (`CsoBulkColumns`) and their completeness and round-trip tests are extended.

**Missing Input Behaviour (FR 7, #1361).** `EvaluateExpressionSource` gains an optional `mv` dictionary. When supplied, missing inputs are the union of both sides, reported in accessor form; a `NullValue` marker reads as absent. The #1361 comment is rewritten. No change to `DynamicExpressoEvaluator`.

**Save-time validation (FR 2, FR 12).** `DerivedFlowValidator.Validate(allImportRulesOfType, proposal)` from every path that can change an import mapping's expression or target. It rejects a cycle or self-reference (message naming every attribute and Synchronisation Rule on the cycle), an `mv` name that is not an attribute of the rule's Metaverse Object Type, and a Reference-typed input or target (#1861). It returns a non-blocking warning when a derived expression calls a non-repeatable function (`Now()`, `Today()`, `NewGuid()` and similar), surfaced as a portal warning, a REST `warnings` entry and a PowerShell `Write-Warning`. Thrown as `DerivedFlowValidationException : ArgumentException`.

**FR 3.** `DerivedFlowDependentDetector` (pure static, after `SchemaRefreshDependentDetector`) lists derived flows (transitively) whose input loses its last enabled contributor, surfaced on the deletion, settings-update and whole-rule responses (`dependentDerivedFlows`), PowerShell `Write-Warning`, a portal confirmation, and schema refresh dependents.

**Previews (FR 11).** `CsoPreviewContext` carries the graph built from the substituted rule set; `PreviewCsoCoreAsync` runs the same level loop (dry-run generation) after inbound flow and before capture. The attribute-flow adapter adds a Blocking finding for a proposal that validation would reject, Warnings from the FR 3 detector and for non-repeatable functions, and an Information finding naming derived flows on other systems' rules that read attributes the proposal changes ("re-evaluated when that system next synchronises").

**Drift.** No special case here. [#1864](https://github.com/TetronIO/JIM/issues/1864) fixes the general defect (a system counted as a legitimate source for an attribute its winning import flow does not read), which a derived flow reading only `mv["..."]` is an instance of.

### Key decisions

1. **Derived is detected, not declared** (PRD). *Rejected:* an `IsDerived` field, which would contradict FR 12 and could disagree with the expression.
2. **The graph is validated with disabled mappings and rules included; the run-time graph uses enabled ones only.** Enabling can then never introduce a cycle. *Rejected:* enabled-only validation re-run on every enable path.
3. **Levels via Kahn's algorithm in a new `DerivedFlowGraph`**, not a refactor of the Example Data sort, which lacks levels, full cycle reporting and self-reference rejection.
4. **Every derived contributor of an attribute runs at that attribute's level, ordered (Priority, mapping id).** *Rejected:* per-mapping levels, under which a dependant could read an attribute before all its contenders are resolved.
5. **Derived mappings are excluded from the ordinary pass and from re-election.** *Rejected:* evaluating them inside `FlowInboundAttributes` with a partial view.
6. **The derived pass reuses `ProcessMapping`'s gate and writers.** Priority, "Null is a value", recall and provenance come unchanged (FR 7). *Rejected:* a separate writer, or a new outcome or causality member (FR 8 says an ordinary change).
7. **A derived flow runs only in its hosting system's synchronisation; other systems' changes to its inputs mark the hosting Connected System Object** (product owner, 2026-09-29, "Position 2"). Keeps "a rule's flows run in its own system's synchronisation", keeps expression errors on the hosting system's Activity, and removes permanent staleness. *Rejected:* (1) hosting-system-only with no marking, which leaves a value stale indefinitely because neither delta nor full synchronisation revisits an unchanged object; (3) evaluating in whichever synchronisation changes the input (the PRD's original FR 5), which runs one system's rule inside another's synchronisation and needs a cross-system prefetch per page.
8. **A new boolean column, not a reuse of `ScopeReviewPending`** (product owner, 2026-09-29). The Temporal Scope Reconciler rewrites `ScopeReviewPending` for every object it evaluates, so a shared flag would be wiped. *Rejected:* a bit-flags reason column (harder to read).
9. **The processed object evaluates all its rules' derived flows**; no changed-input selection. Marking decides which objects are processed; evaluation per object is in-memory and cheap. *Rejected:* per-flow trigger sets (the PRD's delta wording), which add complexity for no saved I/O.
10. **A derived-flow expression error fails the object, like any Attribute Flow error.** *Rejected:* isolating the error to the derived attribute, leaving the object silently inconsistent.
11. **Run-start cycle detection is a hard failure** naming the cycle.
12. **Own feature flag, `Features.MetaverseDerivedAttributeFlows`, In development, with a removal issue filed in Phase 1.** Gated at save (an import mapping whose expression newly reads `mv`) and at run entry in one factory (`DerivedFlowGraphFactory.CreateAsync`) that every `AttributePriorityContext` site calls. Flag off: graph null, engine exactly as today, marking and selection inert. Flag removal no later than #242's (#1803).
13. **Previews evaluate derived flows for the previewed object**, matching what its hosting system's synchronisation would write.
14. **Reference-typed inputs and targets are rejected at save in this release** (product owner, 2026-09-28; tracked by #1861). References resolve after the per-object tail, so a derived flow would read the previous reference.
15. **Non-repeatable functions in a derived expression warn at save, never block** (product owner, 2026-09-28; PRD open question 1).
16. **Metaverse-owned attribute rules are deferred to internally managed Metaverse Objects (#614)** (product owner, 2026-09-28; PRD open question 2). Nothing here blocks them.

## Implementation Phases

A stacked PR chain. Each layer is TDD (tests red first), `dotnet build JIM.sln` and `dotnet test JIM.sln` clean. Tests run with the flag on (`InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()`). The PRD and this plan moved to `doing/` when Phase 1 started.

### Phase 0 (bottom layer): drift contributor fix, [#1864](https://github.com/TetronIO/JIM/issues/1864) ✅

Delivered by [#1872](https://github.com/TetronIO/JIM/pull/1872) (2026-09-29), as planned below; not behind the flag.

1. Failing test: AD import Display Name = `cs["givenName"] + " " + cs["sn"]`, export Display Name → `displayName`; an out-of-band `displayName` edit is corrected.
2. `DriftDetectionService`: a system is a legitimate source for a diverged Connected System attribute only if its winning import flow for the Metaverse attribute reads that attribute (direct source, or a `cs["..."]` input via `ExpressionInputResolver`).
3. Tests for direct attribute (same attribute: not drift; different attribute: drift), expression reading it or not, multi-contributor priority interaction, mixed `cs`/`mv` expression. Changelog 🐛 entry; drift docs. Standalone and user-facing, so it is not behind the flag.

### Phase 1: Graph, save-time validation and the flag ✅

**Delivered (2026-09-29), with these specifics the plan left open.**
- The flag is `Features.MetaverseDerivedAttributeFlows`, removal tracked by [#1878](https://github.com/TetronIO/JIM/issues/1878).
- Only problems involving a proposed mapping are reported, so a save is never refused for someone else's configuration. When no proposed import mapping reads `mv`, nothing runs and nothing is read, whatever the flag.
- A cycle blocks a save only through a proposed mapping that is **enabled**. A cycle can already exist (saved with the flag off, or by concurrent saves), and disabling one of its mappings is how an administrator breaks it; re-enabling is validated as normal, and the graph still includes disabled mappings (decision 2). The exemption is keyed on the mapping, never its rule.
- "Newly reads `mv`" (the flag-off gate) is judged against a no-tracking read of the persisted rules, because the settings-update path holds a tracked, already-mutated mapping.
- Warnings travel on a transient `SyncRuleMapping.SaveWarnings` (the `SequenceSkippedAhead` precedent): REST `warnings` on the mapping POST and PATCH responses, PowerShell `Write-Warning` in `New-`/`Set-JIMSyncRuleMapping`. The portal renders them in Phase 6; it already surfaces the errors and, new here, a `FeatureDisabledException` as a snackbar instead of an unhandled error.
- The non-repeatable functions JIM's evaluator actually offers are `Now()`, `Today()`, `RandomPassword()`, `RandomPassphrase()`, `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today` and `Guid.NewGuid()`; there is no `NewGuid()` function (decision 15's wording).


1. `FeatureFlagCatalogue.MetaverseDerivedAttributeFlows` (In development); file the removal issue and set `TrackingIssueNumber`.
2. `DerivedFlowGraph`, `DerivedFlowValidator` (cycles, unknown names, Reference rejection, non-repeatable warning), `DerivedFlowValidationException : ArgumentException`.
3. Wire validation and `EnsureDerivedFlowAllowedAsync` into the `ConnectedSystemServer` mapping paths; validation loads all import rules of the Metaverse Object Type and substitutes the proposal.
4. Tests: graph per type, levels, tie-break, generated mapping as a node, case-insensitive names, unknown name, self-reference, two-rule cycle with either rule saved (Scenario 3), three-node cycle message, disabled mapping included in validation and excluded at run time, permutation yields identical levels (FR 4), Reference rejection, non-repeatable warning; server tests on every path; API 400 and warning tests; Pester tests asserting the same text; flag-off refusal.

### Phase 2: The derived pass in the engine ✅

**Delivered (2026-09-29), with these specifics the plan left open.**
- The engine API is on `ISyncEngine`: `EvaluateDerivedLevel(cso, level, syncRules, objectTypes, evaluator, priorityContext)` returns the level's `AttributeFlowError`s (as `FlowInboundAttributes` does) rather than filling an `errors` argument, and takes the Connected System Object rather than the Metaverse Object, since `ProcessMapping` needs both. `EvaluateDerivedLevels` runs levels 1 to `MaxLevel` with nothing in between, for callers that do not interleave generation; it is a no-op when the context has no graph, whereas `EvaluateDerivedLevel` throws `ArgumentException` without one (a caller evaluating a level it has no graph for is a bug).
- The derived mappings of a level are taken from the graph in canonical order and matched to the caller's own in-scope rule and mapping instances (by reference, else persisted id). An in-scope rule the graph says hosts a derived mapping it does not hold as an enabled mapping throws `InvalidOperationException`: graph and rules built from different configuration.
- The effective dictionary is built once per level, when the level starts: a mapping reads only lower levels, never its own.
- `BuildAttributeDictionary` was split so the persisted and effective dictionaries share one builder (keys, typing, `NullValue` exclusion, last value of a multi-valued attribute). `DriftDetectionService` keeps its own copy, untouched.
- Missing inputs, when a Metaverse view is supplied, are the Connected System side's then the Metaverse side's, in accessor form. Without one (the ordinary pass, and the flag off) only the Connected System side counts, as before.
- The factory throws `DerivedFlowCycleException : InvalidOperationException`, carrying the cycles and a message naming the Metaverse Object Type and every attribute and Synchronisation Rule on each (the wording is shared with save-time validation through `DerivedFlowValidator.DescribeCyclePath`). It reads the flag through `FeatureFlagServer`, so a caller passes its unit of work's `jim.FeatureFlags`.
- The reference-only pass needed the skip for a reason worth recording: `ProcessMapping` evaluates expression sources in every pass, the reference-only one included, so without it a derived flow would have been evaluated there too, with no Metaverse view.
- Re-election needed no change of its own: `ContributorReElectionService` re-flows survivors through `FlowInboundAttributes` with the run's priority context, so the skip applies there. A test pins it.

1. `DerivedFlowGraphFactory`; graph on `AttributePriorityContext`; `FlowInboundAttributes` skips derived mappings when a graph is present.
2. `BuildEffectiveAttributeDictionary`; `EvaluateExpressionSource` takes the `mv` dictionary and applies Missing Input Behaviour to both sides; `ProcessGeneratedMapping` accepts it.
3. `SyncEngine.DerivedFlows.cs`: `EvaluateDerivedLevel` (no I/O).
4. Tests: Scenario 1 ordering; same-pass visibility of an uncommitted addition and a pending removal; derived wins and loses under priority; "Null is a value"; each Missing Input Behaviour on an `mv` input (Scenario 4); `NullValue` input reads absent; generated mapping with an `mv` input records a pending generation at its level; determinism under permuted mapping order; graph null reproduces today's behaviour (legacy `mv` reads null).

### Phase 3: Worker level loop, marking and selection ✅

**Delivered (2026-09-29), with these specifics the plan left open.**
- The graph is built through `ISyncServer.CreateDerivedFlowGraphAsync` (the factory, over the unit of work's `FeatureFlags`) straight after the all-systems rule load in both Full and Delta Synchronisation, and handed to `BuildDriftDetectionCache`, which attaches it to the run's `AttributePriorityContext`. A `DerivedFlowCycleException` propagates from there before any object is processed, so the Worker's sync-run boundary fails the Activity with the cycle message, exactly as any other run-start failure is reported. The exception is now an `OperationalException` (it was an `InvalidOperationException` in Phase 2): a cycle is a configuration fault the administrator fixes, so the Activity records the message without a stack trace.
- The three server recall paths (Synchronisation Rule deletion recall, Synchronised Deprovisioning, the stranded value sweep) attach the graph too, through `ConnectedSystemServer.BuildRecallPriorityContextAsync`. Without it, contributor re-election re-flowed a derived mapping as an ordinary one, with no Metaverse view, and wrote a value nobody configured (a test reproduces it). Those paths do not yet mark the hosting system; that is Phase 4. Sync Preview's context is untouched until Phase 5.
- The level loop (`ResolveGenerationsAndDerivedLevelsAsync`) runs levels 1 to the deepest `MaxLevel` of the in-scope rules' Metaverse Object Types, each evaluated once, with pending generations resolved (one `ResolveAsync` batch) after level 0 and after each level. Derived-level mapping errors are recorded by the same helper as the ordinary pass's (`RecordInboundAttributeFlowErrors`); a thrown derived expression error fails the object through the ordinary catch.
- A derived-flow error names its hosting Synchronisation Rule: `AttributeFlowError.SyncRuleName`, and `SyncRuleName` on the expression-evaluation and missing-input exceptions, set by `EvaluateDerivedLevel` only. Ordinary flow error messages are unchanged.
- The marking helper is `DerivedInputMarking.GetConnectedSystemsToMark` (pure; Phase 4's writers call it with `excludedConnectedSystemId: null`). The worker collects `(Metaverse Object, Connected System)` against the object instance, because an object projected on the page has no id until the flush, and resolves the ids at page flush after the Metaverse Objects are persisted. Recalls inside synchronisation (out-of-scope disconnection, obsoletion) mark as well.
- `DerivedInputChangeMark` carries a mark; `ISyncRepository.MarkConnectedSystemObjectsDerivedInputChangePendingAsync` is one statement over `unnest(@mvoIds, @systemIds)` joined on (Metaverse Object, Connected System). It writes every matched row, already-marked ones included, so each mark moves the row's `xmin`; the count of new marks for the summary comes from the pre-update snapshot in the same statement (a CTE). Both it and the clear fix up tracked instances (current and original value, not modified).
- **The clear is guarded by the row version**, so runs of two systems in parallel cannot lose a mark. `ConnectedSystemObject.xmin` is mapped read-only (store-generated, never saved, not a concurrency token, `[JsonIgnore]`; the migration's `AddColumn` for it is a no-op, since Npgsql never creates system columns). Both page loaders read it in the Connected System Object statement, and the processor snapshots it for marked objects straight after the page load (`CaptureDerivedInputRowVersions`), before this run writes anything. The Metaverse Objects are loaded by a later statement, so the Metaverse values evaluated are at least as new as the snapshot (the ordering argument is in the code comment). `ClearConnectedSystemObjectDerivedInputChangePendingAsync` takes `DerivedInputChangeClear(Id, SeenRowVersion)` and is one `UPDATE ... FROM unnest(@ids, @seenRowVersions) WHERE xmin = seen AND flag RETURNING Id`: a row written since the load keeps its mark and is re-evaluated next run (fail-safe). The run's own write to the same row at the same flush (the Temporal Scope Reconciler flag's clear, or a join change, which a marked object, being joined already, only has on re-join) also moves the version, so such an object converges one run later; that write does not recur. No bulk Connected System Object writer writes `DerivedInputChangePending` from a loaded value: the update column list excludes it, and the other raw updates name their own columns. The in-memory repository models the row version so the workflow tests exercise the guard.
- The partial index is keyed on `ConnectedSystemId` with the filter `"DerivedInputChangePending"` (`IX_ConnectedSystemObjects_ConnectedSystemId_DerivedInputChangePending`), serving the delta query's marked arm, rather than on the flag column itself as `IX_MetaverseObjects_ScopeReviewPending` is.
- `DerivedInputChangePending` is in `CsoBulkColumns.ConnectedSystemObjects` (both create writers write it) and in the update exclusions, for the `ScopeReviewPending` reason: it is set by another system's synchronisation and cleared by the hosting system's, each through its own statement, so a whole-row update of a stale entity must never write it back.
- The clear follows the `ScopeReviewPending` fail-safe (an object whose Pass 2 throws keeps its mark), and goes one step further for derived flows: an object whose derived pass returned any Attribute Flow error (for example Missing Input Behaviour "Fail the mapping" on an `mv` input) also keeps its mark (decision 10), so every run re-evaluates it and re-reports the error rather than leaving a stale derived value nothing revisits. An ordinary flow's mapping-level error keeps the `ScopeReviewPending` behaviour: the mark is cleared.
- Marking and selection read no flag. With the flag off nothing is ever marked, so selection has nothing to find; a mark left from a period with the flag on is selected once and cleared.
- The end-of-run summary (`LogDerivedInputMarkSummary`) logs marks set in other systems, marks cleared in this one, and marks kept because the row was written after it was loaded, only when a graph is present.

1. Level loop in `ProcessMetaverseObjectChangesAsync`, replacing the single generation resolve.
2. Migration: `ConnectedSystemObjects.DerivedInputChangePending` with a partial index; `CsoBulkColumns` and writers extended, completeness and `RequiresPostgres` round-trip tests.
3. Marking: collect `(MetaverseObjectId, ConnectedSystemId)` per object after change capture; one bulk UPDATE per page flush (raw SQL over the joined Connected System Objects, tracked instances fixed up per `src/CLAUDE.md`). Delta selection and the full unchanged-skip honour the flag; cleared at page flush on success.
4. Run-start cycle hard failure; expression error messages name the hosting rule.
5. Tests: Scenario 1 in one run (Email and UPN in the same Metaverse change record and export evaluation, FR 8); Scenario 2 as a Workflow test: AD delta changes Region, HR object marked, HR **delta** re-derives Email; wrong sequencing (HR before AD) converges on the next HR delta; a full synchronisation of an unchanged, unmarked object stages nothing; flag cleared only on success; one bulk UPDATE per page and none per object (repository-call counting); FR 10 with a generated attribute at level 1; attribute flow error parity.

### Phase 4: Out-of-synchronisation writers and review (FR 9) ✅

**Delivered (2026-09-29), with these specifics the plan left open.**
- The writers share `DerivedInputMarkBatch` (`JIM.Application/Services`): it collects `(Metaverse Object, Connected System)` marks through `DerivedInputMarking.GetConnectedSystemsToMark` with nothing excluded (outside synchronisation no derived pass has run, so the system whose data was touched re-evaluates too), deduplicates them, and applies them with one `MarkConnectedSystemObjectsDerivedInputChangePendingAsync` call per flush, always after the Metaverse changes that caused them are persisted. With no graph (flag off) it is inert: nothing collected, no repository call. Each operation logs its totals (marks set, distinct marks, bulk updates). No repository or SQL change was needed.
- Which graph each writer marks from. **Synchronisation Rule deletion recall**: the factory over every rule except the one being deleted, whatever its `Enabled` state (it is disabled at queue time, but the marking does not rely on that), so its own derived mappings neither mark its system nor carry transitivity onwards. **Synchronised Deprovisioning**: every rule except the deprovisioned system's, for the same reason. **Stranded value sweep**: no rule goes away, so it reuses the graph its recall's `AttributePriorityContext` already carries. **Direct edit** (`MetaverseServer.UpdateMetaverseObjectAsync`): reads the flag first (off, nothing else is read), then the factory over the object type's import rules (`GetImportSyncRulesForMetaverseObjectTypeAsync`, as save-time validation reads them) and the type's attributes.
- Flush boundaries: the shared recall core (`RecallSyncRuleContributedValuesAsync`, which serves rule deletion, the deprovisioning residue pass and the sweep) flushes once per 500-object batch after the batch is persisted; deprovisioning's per-object pass flushes once per batch after every persistence step and before the checkpoint; a direct edit makes one call per edit, after the save.
- The change set is what each path already stages: recalled values and re-elected survivors (the additions and removals captured before they are applied); for a direct edit, the caller's additions and removals. An update with neither (operational metadata) marks nothing. A direct edit of a derived attribute marks only its readers; its own hosting flow reasserts it by priority on the next synchronisation.
- A dependency cycle fails the recall paths hard before they touch anything, as Phase 3 already did. A direct edit instead logs the cycle at Error and saves without marking: the edit is already written, every hosting synchronisation fails on the cycle until it is broken, and the path serves sign-in, which must not lock an administrator out of fixing the cycle.
- Writers found, and the paths checked that need nothing. The only caller that hands `UpdateMetaverseObjectAsync` a change set today is the SSO profile supplement (`AuthServer`); there is no portal, REST or PowerShell surface that edits Metaverse attribute values, and `MetaverseServer.UpdateMetaverseObjectsAsync` has no callers. Needing nothing: disabling a rule (values are retained), deleting a mapping (keep severs provenance only; recall runs in the contributing system's Full Synchronisation, which Phase 3 marks), schema refresh removal and Connector Space clear (Connected System Objects only; recall follows in synchronisation or the sweep), immediate Connected System deletion (provenance nulled, values unchanged), Metaverse Object deletion (only Reference values on other objects, which a derived flow cannot read, #1861), creation paths (example data, SSO user creation: no joined objects to mark), and the #892 review drain (unchanged, decision 7).
- Tests: `DerivedInputOutOfSyncMarkingWorkflowTests` (each writer marks exactly the hosting systems' objects, transitively, and not Training; one bulk call per batch; the deleted rule's own derived flow marks nothing even while enabled; flag off marks nothing; PRD Scenario 5 without remediation: a direct Account Name correction marks HR and its next **delta** re-derives Email and User Principal Name), `DerivedInputMarkBatchTests` and `MetaverseServerDerivedInputMarkingTests` (flag off reads no rule, cycle, empty change set). Scenario 5 with remediation belongs to #242 release 4, whose Phase 8 now carries the marking requirement.

1. Synchronisation Rule deletion recall, Synchronised Deprovisioning, the stranded value sweep and `MetaverseServer.UpdateMetaverseObjectAsync` call the marking helper for changed derived inputs; a note to #242 Phase 8 that remediation marks the same way (this replaces re-deriving inside the #892 Metaverse Object review drain, which needs no change).
2. Tests: each writer marks exactly the hosting systems' objects; a direct Account Name edit re-derives Email and UPN on the next hosting-system delta (Scenario 5 without remediation).
3. Run Scenario 8 before the PR (`src/JIM.Application/CLAUDE.md` section 6).

### Phase 5: Sync Preview and Configuration Change Preview

1. Graph on `CsoPreviewContext` from substituted rules; level loop in `PreviewCsoCoreAsync`; derived results in the Attribute Flow changes and outcome tree.
2. Adapter findings: Blocking for a proposal that fails validation, Warnings from the FR 3 detector and non-repeatable functions, Information for dependants on other systems' rules.
3. Tests: `SyncPreviewFidelityTests` extended so preview and run agree for Scenario 1 shapes; adapter tests per finding; dry-run never writes a mark (guarded repository).

### Phase 6: Authoring surfaces, FR 3 dependents and public docs

1. Portal: an "Insert attribute" menu under the Expression field (Metaverse attributes on import with the flag on); a read-only line stating the flow is derived, its level and its `mv` inputs; a `Derived` row chip; FR 3 confirmation on disable and remove; the non-repeatable warning; bUnit tests. A UI artefact for review before building.
2. `DerivedFlowDependentDetector`; `dependentDerivedFlows` on the responses; PowerShell warnings; API and Pester tests; schema refresh dependents.
3. Docs: `docs/configuration/synchronisation-rules.md` (deriving Metaverse attributes, ordering, cycles, which synchronisation evaluates them, sequencing sources first, the marking), `docs/concepts/expressions.md`, the Initialising JIM and Schedules guidance, PowerShell and API reference. `CHANGELOG.md` entry withheld until the flag is removed (the #242 precedent).

### Phase 7: Integration and release

1. `Setup-Scenario1.ps1 -DeriveFromAccountName` (requires `-GenerateAccountName`; enables both flags): Email = `mv["Account Name"] + "@panoply.local"`, User Principal Name = `mv["Email"]`, export User Principal Name → `userPrincipalName`; removes the generated Email mapping.
2. New `Invoke-Scenario24-DerivedAttributeFlows.ps1` (below). Scenario 23 gains one row asserting Email and UPN follow a suffixed generated Account Name.
3. Developer guide section 3e (graph, derived pass, marking, interleave with 3d); `engineering/CAUSALITY_REFERENCE.md` note that derived changes are ordinary Attribute Flow; #242 plan Phase 5 marked Delivered.

## Integration testing

**Scenario 24, Metaverse-Derived Attribute Flows** (Samba AD and OpenLDAP, Small template, setup via `Setup-Scenario1.ps1 -GenerateAccountName -DeriveFromAccountName`):

| Row | PRD | Assertion |
|---|---|---|
| Ordering | Scenario 1 | After full import, full synchronisation, export and confirming import, every user's `mail` equals Account Name + `@panoply.local` and `userPrincipalName` equals `mail`; two same-named people carry the same suffix in all three |
| Cross-system, correct order | Scenario 2 | A derived flow on the HR rule reads an attribute contributed by the Training Records Source system; a Training CSV edit, Training delta then HR delta re-derive it and export it |
| Cross-system, wrong order | Scenario 2 | The same edit with HR delta first leaves the value stale for one cycle; the next HR delta corrects it (the mark) |
| Stability | Scenario 6 | A following full synchronisation stages no Pending Exports |
| Cycle | Scenario 3 | Creating a cycle returns 400 naming both attributes and both rules; the PowerShell error text matches |
| Missing input | Scenario 4 | ContributeNoValue over an absent attribute contributes nothing, then a value once the attribute arrives |
| Surface parity | FR 12 | `Get-JIMSyncRuleMapping` round-trips the expression; FR 3 dependents appear on disabling the Account Name flow |

Scenario 5 (re-derivation after Collision Remediation) joins #242 release 4's rows; worker tests cover the marking path now. Scenario 1 itself converts under #242 Phase 6, once both flags are removed.

## Success Criteria

- The PRD's Acceptance Criteria (as revised 2026-09-29).
- Scenario 1 (Medium) with three derived levels within 5% of the same run with plain expression flows; no per-object repository call added (asserted by test).
- Flag off: the whole existing suite passes unchanged.

## Dependencies

- Landed: #91, #1361, #892, #1533, #1537, #1781, #242 release 1.
- #1864 (Phase 0) lands first.
- No new packages. One migration (the `DerivedInputChangePending` column and its partial index).

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Parallel runs of two systems write one object from different views | Shared with ordinary flows today; the mark guarantees a later re-evaluation |
| Concurrent saves each pass validation but together form a cycle | Run-start hard failure names the cycle |
| Marking volume when a widely read attribute changes in bulk (for example a mass Region change) | One bulk UPDATE per page; the hosting system then processes those objects once, as it would for the same number of source changes |
| Administrators sequence the hosting system first | Converges one cycle later; docs recommend sources first; preview Information finding names the dependency |
| Non-repeatable expressions churn exports | Save-time warning (decision 15) |
| Deleting a Metaverse attribute a derived expression reads | Validation rejects unknown names on next save; follow-up for the attribute deletion impact to list derived readers |
