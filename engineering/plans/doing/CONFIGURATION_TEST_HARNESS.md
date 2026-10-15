# Configuration Test Harness - Implementation Plan

- **Status:** Doing (Phases 1 and 2 complete)
- **Issue:** [#2055](https://github.com/TetronIO/JIM/issues/2055) (sub-issue of [#207](https://github.com/TetronIO/JIM/issues/207))
- **PRD:** [`PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md`](../../prd/doing/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md), "Configuration test harness (#2055)", Scenarios 14 and 15
- **Builds on:** [`OBJECT_MATCHING_EXPRESSIONS.md`](../done/OBJECT_MATCHING_EXPRESSIONS.md) (#2052, Try it and the match-value pipeline)
- **UI mocks:** [Matching Expressions (#207)](https://claude.ai/artifact/U7urHxpx49uGm4EBi5Ckme), bottom row (private to the product owner)

## Overview

An administrator should know whether an expression, a scoping criterion or an Object Matching Rule works before a synchronisation runs it. JIM has an Expression tester (#1405), Configuration Change Preview (#827) and Sync Preview (#288), but the tester is not synchronisation: it passes every sample as text, one value per attribute, and stops after evaluating. It skips value processing, Missing Input Behaviour and conversion to the target's type, so it gives false failures (`(DateTime)cs["hireDate"]`) and false passes (a result the target cannot hold).

This plan delivers #2055 in five reviewable PRs. The first makes the tester and synchronisation one code path, so everything later is built on code that cannot disagree with a synchronisation.

## Business Value

- Configuration is proven before it runs, not discovered from a run's errors.
- A test result means what a synchronisation would do, so a passing test is trustworthy.
- Saved test cases turn every later change into a checked change, which is what high-trust customers need from configuration they promote between environments.

## Technical Architecture

### Current state

- **Inbound Attribute Flow** (`SyncEngine.AttributeFlow.cs`): `EvaluateExpressionSource` builds the `cs[...]` dictionary from the Connected System Object (`BuildCsoAttributeDictionary`), applies Missing Input Behaviour, evaluates, and maps failures to exceptions or an `AttributeFlowError`. `ProcessExpressionMapping` then resolves the result through `ResolveInboundExpressionResult` (value processing and conversion, `ExpressionResultValues.Resolve`).
- **Export Attribute Flow** (`SyncEngine.ExportStaging.cs`): `EvaluateExportExpressionSource` does the same against the Metaverse Object's dictionary; `StageExportExpressionResult` converts.
- **The tester** (`ExpressionTester.razor`, `POST /synchronisation/test-expression`, `Test-JIMExpression`): `IExpressionEvaluator.Test` over untyped text samples. Nothing after evaluation runs.

The pieces synchronisation runs exist, but are composed inside `SyncEngine` with side effects (pending value changes, errors, exceptions) mixed in, so nothing else can run the same sequence.

### Proposed solution

**One evaluation pipeline per kind, pure, called by synchronisation and by the tester alike.** For Attribute Flow (Phase 1):

```
inputs ──► context (as synchronisation builds it) ──► Missing Input Behaviour ──► evaluate
       ──► value processing ──► conversion to the target's type ──► outcome
```

- `AttributeFlowPipeline` (JIM.Application) returns an `AttributeFlowEvaluation` (JIM.Models): the outcome (values, no value, or an error), the error kind, the missing inputs, the expression's raw result and the final converted values. It has no side effects.
- `SyncEngine` calls it and turns the outcome into what synchronisation has always done: pending value changes, an `AttributeFlowError`, or the exception that fails the object. That translation is the only code left in `SyncEngine` for it.
- `AttributeFlowTester` (JIM.Application) turns sample inputs into the object synchronisation would read. It converts each sample to its schema attribute's type, holds it as an attribute value on a transient Connected System Object (import) or Metaverse Object (export, and an import flow reading `mv[...]`), and runs the same pipeline over it. So the context is built by the same dictionary builders, which is what makes the tester follow #2053's multi-valued change automatically.

Later kinds follow the same shape: scope uses the scoping evaluator and its explanation; Object Matching already has one (`MatchValueResolver`, #2052).

## Implementation Phases

### Phase 1: The shared evaluation pipeline (Attribute Flow) ✅

One PR, kept tight because #2053's multi-valued change starts once it merges.

- `AttributeFlowPipeline` and `AttributeFlowEvaluation`, as above, for import and export.
- `SyncEngine` inbound and export Attribute Flow refactored onto it, behaviour unchanged (existing suites unchanged and green, Scenario 008 run).
- `AttributeFlowTester`: typed samples from the schema; several values for a multi-valued attribute; no value kept distinct from empty text over REST.
- **Fixes:** the false failure (`(DateTime)cs["hireDate"]`, Scenario 14) and the false pass (a result the target cannot hold now fails the test, with synchronisation's reason).
- **Surfaces:** the portal tester in the Attribute Flow editor tests the flow being edited; `POST /synchronisation/test-expression` and `Test-JIMExpression` accept a saved Attribute Flow (`syncRuleId`, `mappingId`) and return the outcome and final values. Without a flow they behave as before.
- **Fidelity tests:** paired tests run each case through synchronisation (`FlowInboundAttributes`, `CreateAttributeValueChanges`) and through the tester, and assert the same values, no value or error, including typed, multi-valued and absent inputs.

### Phase 2: Testers per kind

Split by kind into reviewable PRs, in the order other work needs them.

#### Phase 2a: Attribute Flow inputs and trace ✅

- **Inputs:** each input described from the schema (type, plurality); typed editors; several values for a multi-valued attribute; an explicit **No value** distinct from empty text; **load from an object** (a Connected System Object for an import Attribute Flow, with its joined Metaverse Object for `mv[...]` inputs, or a Metaverse Object for an export one), editable once loaded.
- **Results:** the step trace (expression result, value processing, conversion, outcome) and the "If an input has no value" table.
- **Surfaces:** the portal's `AttributeFlowTestPanel`; `test-expression` gains `connectedSystemObjectId`, `metaverseObjectId`, `inputs`, `valueProcessingSteps` and `ifInputHasNoValue`; `Test-JIMExpression -ConnectedSystemObjectId -MetaverseObjectId`.
- **As built:** Missing Input Behaviour treats empty text as missing, as synchronisation does, so "No value" and empty text differ in what the Expression sees, not in whether an input counts as missing.

#### Phase 2b: Scope tester and run as of ✅

The scoping evaluator and its explanation: the criteria tree, each criterion met or not and why, and when the object would enter or leave scope. **Run as of** fixes the time used by `Now`, `Today` and relative-date criteria (the evaluator gains an injectable clock), for scope and Attribute Flow alike. #2054 builds on this.

- **As built:** `ScopeTester` (JIM.Application) types samples from the schema through the same conversion as the Attribute Flow tester (`TestSampleValues`), holds them on a transient Connected System Object or Metaverse Object, and explains it through `ScopingEvaluationServer`. The clock is `ExpressionContext.NowUtc`, which `Now()` and `Today()` read for the duration of one evaluation; synchronisation leaves it unset.
- **Next change:** a relative boundary can only cross an object's date at a few instants (the exact instant for Hours, the midnights around it otherwise), so the tester evaluates the rule at each in turn and reports the first that changes the outcome. No reasoning about how criteria combine, and the answer is synchronisation's own.
- **Surfaces:** the Scope tab's **Test this scope** panel (criteria as they stand on the form), `POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/test` and `Test-JIMScope` (saved criteria); `asOf` on `test-expression`, `-AsOf` on `Test-JIMExpression` and **Run as of** in the Attribute Flow panel.

#### Phase 2c: Object Matching tester ✅

Both modes: the match values, and optionally the lookup against the current Metaverse (would join, no match, ambiguous), building on #2052's Try it.

- **As built:** `ObjectMatchingServer.TestAsync` types samples through `TestSampleValues`, resolves them with the match-value resolver, and looks them up with `FindMatchingMetaverseObjectAsync` through a `ReadOnlySyncRepositoryGuard`. It then applies the join's own refusal of a Metaverse Object already joined from the Connected System, so the outcomes are would join, already joined, joined to another, no match and ambiguous (with candidates).
- **Surfaces:** Try it in the Object Matching Rule editor (both modes, the draft as composed); `POST .../connected-systems/{id}/matching-rules/{ruleId}/test` and `.../sync-rules/{id}/matching-rules/{ruleId}/test` (saved rules); `Test-JIMMatchingRule`. The Scope tester's input records became the shared `AttributeSample` and `AttributeSampleDescriptor`, and the testers share one object picker (`TestObjectPicker`).

#### Phase 2d: Generated Values and Example Data ✅

A Generated Value's base expression (through the Attribute Flow tester, stopping before generation) and Example Data template expressions over REST and PowerShell.

- **As built (Generated Values):** `AttributeFlowPipeline.ResolveGeneratedBase` holds the checks a base must pass: one value, with a text form, after the mapping's inbound value processing; no value means wait. Both engine paths (`ProcessGeneratedMapping` on import, `StageGeneratedExportMapping` on export) and `AttributeFlowTester` now call it, replacing the two copies the engine carried. The tester branches on `mapping.Generation` and returns a `GeneratedBaseEvaluation` (base, waits, no base expression, or an error) in place of the ordinary evaluation, with its own "if an input has no value" outcomes. It never shows a candidate: which value is free needs the database (decision A1). The portal's Generated Value editor uses the full Attribute Flow test panel, which now reports its samples so the candidate preview still evaluates against them. REST: `generatedValue`, `baseOutcome` and `baseValue` on `test-expression`, whose expression may be omitted for a token-only mapping; PowerShell: `Test-JIMExpression -SyncRuleId -MappingId`, `-Expression` optional in that set.
- **As built (Example Data):** `ExampleDataServer.TestAttributeExpression` types samples by the template attributes generated on the same object and evaluates through the same non-throwing core generation now uses (`EvaluateAttributeExpressionOutcome`); generation keeps its throwing wrapper. `POST /api/v1/example-data/templates/{templateId}/test-expression`; `Test-JIMExampleDataExpression`. No portal tester: #2055 scopes Example Data as "REST API and PowerShell now, the portal once authoring exists there" (decision B1).

### Phase 3: Validation on save everywhere

Saving refuses, with the message shown, an expression that does not parse, uses an unknown function, reads an input from the wrong side, uses a non-deterministic function where that is not allowed, or produces a result type that cannot suit the target where that can be known. One validator per kind (the `ObjectMatchingRuleValidator` pattern), called by the portal, REST API and PowerShell alike. Closes the portal's Attribute Flow gap.

### Phase 4: Saved test cases and the save gate

- **Model:** a test case holds a name, what it covers (an Attribute Flow, a Synchronisation Rule's Scope, an Object Matching Rule or a Generated Value), its inputs, an optional run-as-of time and an expectation (exact values, no value, an error, any value, in scope, out of scope, or match values). New table, cascading from what it covers; recorded in Configuration History and snapshots.
- **Created from the tester**, today's result as the editable expectation; personal-data notice with made-up replacements when values were loaded from a real object.
- **Run** on demand (the Synchronisation Rule's **Tests** tab; the Connected System's Object Matching tab for Simple mode) and automatically on saving what they cover.
- **The save gate:** Keep editing (default), Accept the new result as expected, or Save anyway (recorded with the change in Configuration History).
- **Needs review** when the covered configuration reads an input the test case has no value for, or an input attribute left the schema.

### Phase 5: Full parity and docs

- Test case create, read, update and delete, and the run endpoint, across REST and PowerShell.
- `docs/` page "Testing configuration", linked from the Attribute Flow, Scope, Object Matching and Generated Values pages; changelog ✨.
- An integration scenario step running `Invoke-JIMConfigurationTest` in a pipeline.

## Decisions

Names are settled here so every phase uses them.

| Capability | REST API | PowerShell | Phase |
|---|---|---|---|
| Attribute Flow (and Generated Value base expression) | `POST /api/v1/synchronisation/test-expression` with `syncRuleId` and `mappingId` | `Test-JIMExpression -SyncRuleId -MappingId` | 1 (2 adds the remaining inputs) |
| Scope | `POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/test` | `Test-JIMScope` | 2 |
| Object Matching Rule | `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules/{ruleId}/test`, and `.../sync-rules/{syncRuleId}/matching-rules/{ruleId}/test` | `Test-JIMMatchingRule` (parameter sets by `-ConnectedSystemId` or `-SyncRuleId`; `-Lookup`) | 2 |
| Example Data template expression | `POST /api/v1/example-data/templates/{templateId}/test-expression` | `Test-JIMExampleDataExpression` | 2 |
| Test cases | `GET/POST /api/v1/configuration-tests`, `GET/PUT/DELETE /api/v1/configuration-tests/{id}` | `Get-`, `New-`, `Set-`, `Remove-JIMConfigurationTestCase` | 4-5 |
| Run test cases | `POST /api/v1/configuration-tests/run` (by Synchronisation Rule, Connected System or ids) | `Invoke-JIMConfigurationTest` | 4-5 |

1. **The Attribute Flow tester keeps `test-expression` and `Test-JIMExpression`**, gaining a flow context, rather than adding `Test-JIMSyncRuleMapping` beside them. Two names over one endpoint for one thing break "one name per thing"; the issue asks to extend `test-expression`. Without a flow, both behave as before.
2. **A saved flow is named by `syncRuleId` and `mappingId`** (ids, per the API identifier rules). The request's `expression` is what is tested, so an edit can be tried before saving it; the flow supplies its target, Missing Input Behaviour and value processing. Testing an unsaved flow over REST comes with Phase 2's inputs.
3. **The tester builds a transient object and runs the dictionary builders synchronisation uses**, rather than typing a dictionary itself. This is the only way the context is built "as synchronisation builds it", including today's one-value reading of a multi-valued attribute, which #2053 then changes in one place for both.
4. **Missing Input Behaviour stays before evaluation**, as synchronisation applies it today. The issue lists it after value processing; moving it would change synchronisation.
5. **A Generated Value's base expression is tested as its Attribute Flow** (same endpoint, same pipeline stopping before generation), not a separate tester.

## Success Criteria

- Paired fidelity tests prove the harness and synchronisation agree for every kind, including typed, multi-valued and absent inputs.
- Scenario 14: `FormatDate((DateTime)cs["hireDate"], "yyyy-MM-dd")` into Text, with `2026-03-02` entered, shows `2026-03-02`.
- A result the target cannot hold fails the test with synchronisation's reason.
- Scenario 15: a Scope change that breaks a saved test case is gated, and Save anyway is recorded.
- Scenario 008 passes after every phase that touches synchronisation code.

## Benefits

- **Trust:** a tester that runs synchronisation's code cannot pass what synchronisation fails.
- **Architecture:** the pipeline is pure and separated from `SyncEngine`'s side effects, so previews, the provenance inspector and Generated Value Forms can converge on it too.
- **Sequencing:** #2053's multi-valued change lands in one place and the testers show it from the start.

## Dependencies

- #2052 (merged): the match-value pipeline and Try it.
- #2053 follows Phase 1 on the critical path.
- #2054 uses the Phase 2 scope tester.
- No new packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The refactor changes synchronisation behaviour | Existing `SyncEngine` suites run unchanged; Scenario 008 at Medium; the translation from outcome to side effect is one switch per direction |
| The tester's transient object diverges from a real one | It is built with the same attribute value fields the import path sets, and fidelity tests compare against a real `ConnectedSystemObject` through `FlowInboundAttributes` |
| Run-as-of needs a clock the evaluator does not have today | Phase 2 introduces an injectable clock; until then tests of time-dependent expressions use the "any value" expectation |
| Saved test cases hold personal data | Phase 4's notice and made-up replacements; test cases are configuration, deleted with what they cover |
