# Configuration Test Harness - Implementation Plan

- **Status:** Done (all phases delivered)
- **Issue:** [#2055](https://github.com/TetronIO/JIM/issues/2055) (sub-issue of [#207](https://github.com/TetronIO/JIM/issues/207))
- **PRD:** [`PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md`](../../prd/done/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md), "Configuration test harness (#2055)", Scenarios 14 and 15
- **Builds on:** [`OBJECT_MATCHING_EXPRESSIONS.md`](../done/OBJECT_MATCHING_EXPRESSIONS.md) (#2052, Try it and the match-value pipeline)
- **UI mocks:** [Matching Expressions (#207)](https://claude.ai/artifact/U7urHxpx49uGm4EBi5Ckme), bottom row (private to the product owner)

## Overview

An administrator should know whether an expression, a scoping criterion or an Object Matching Rule works before a synchronisation runs it. JIM has an Expression tester (#1405), Configuration Change Preview (#827) and Sync Preview (#288), but the tester is not synchronisation: it passes every sample as text, one value per attribute, and stops after evaluating. It skips value processing, Missing Input Behaviour and conversion to the target's type, so it gives false failures (`(DateTime)cs["hireDate"]`) and false passes (a result the target cannot hold).

This plan delivers #2055 in a series of reviewable PRs (Phase 4 is split in two). The first makes the tester and synchronisation one code path, so everything later is built on code that cannot disagree with a synchronisation.

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

### Phase 2: Testers per kind ✅

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

### Phase 3: Validation on save everywhere ✅

Saving refuses, with the message shown, an expression that does not parse, uses an unknown function, reads an input from the wrong side, uses a non-deterministic function where that is not allowed, or produces a result type that cannot suit the target where that can be known. One validator per kind (the `ObjectMatchingRuleValidator` pattern), called by the portal, REST API and PowerShell alike. Closes the portal's Attribute Flow gap.

- **As built (Attribute Flow):** `AttributeFlowExpressionValidator` (JIM.Application) reports problems by kind: does not parse (an unknown function included), wrong side (an export flow reading `cs[...]`), unknown attribute, result type mismatch, and non-repeatable (a warning). A derived flow's `mv[...]` inputs and non-repeatable functions stay with `DerivedFlowValidator`. `ConnectedSystemServer` runs it on all six mapping save paths (create, update and settings update, both initiator overloads, and both whole-rule saves), after the derived flow check. The portal's whole-rule save, which had only a disabled button, is now checked in the application layer. The REST controllers keep their parse pre-check, which rejects before anything is applied to a tracked mapping and keeps the `Invalid expression:` message.
- **Changed versus unchanged (decision 1A, refined):** a mapping that is new, or whose expressions or target change, is refused for any blocking problem. One the save leaves as it was is never refused; its problems are save warnings. That covers the orchestrator's rule that disabling a rule, removing a mapping or deleting a rule must save while another mapping is broken. The stored state comes from `GetSyncRuleMappingExpressionStatesAsync`, an untracked projection on the `GetSyncRuleMappingKeepUniqueAsync` pattern, because the portal saves on the tracked context it loaded on.
- **Result type (decision 3A):** each catalogue function declares an `ExpressionResultType` (a test fails for any function without one, and Unknown is kept for those returning an argument). `ExpressionResultTypeCheck` refuses only when the outermost call is a catalogue function whose type `ExpressionResultConverter` never accepts for the target; a test checks the rule against the converter for every type and target. A Generated Value's base is not checked against the target's type.
- **Warnings (decision 2A):** non-repeatable functions warn, never refuse. `SyncRule.AttributeFlowSaveWarnings` is the one list the portal shows as snackbars and the REST API returns as `warnings`; PowerShell writes them as warnings. The portal's Attribute Flow dialog and the Generated Value base editor show the same check live (`ExpressionSyntaxCheck.ErrorForAttributeFlow`).
- **As built (Example Data):** `ExampleDataServer` checks template expressions on create and full update: a new or changed one that does not parse or reads `cs[...]` is refused, and otherwise only warned about. Reading an attribute the template does not generate is only ever a warning, because `Add-JIMExampleDataTemplateAttribute` builds a template an attribute at a time. Warnings reach REST as `warnings` on the template and PowerShell as warnings from `New-`, `Set-JIMExampleDataTemplate` and `Add-JIMExampleDataTemplateAttribute`.

### Phase 4a: Saved test cases ✅

Saved test cases on every surface, run on demand. The save gate is Phase 4b.

- **Model:** one `ConfigurationTestCases` table. `Kind` is AttributeFlow, GeneratedValue, Scope or ObjectMatchingRule; exactly one of three cascading references (`SyncRuleMappingId`, `SyncRuleId`, `ObjectMatchingRuleId`) is set, enforced by `CK_ConfigurationTestCases_CoversWhatItsKindTests`. Inputs and expectation are jsonb, every value as culture-invariant text. Expectations: these values (an unordered multiset unless "in this order"), no value, an error, any value, in scope, out of scope.
- **Run:** `ConfigurationTestRunner` dispatches each kind to the tester the portal, REST and PowerShell already use, and compares on one canonical value form (`ConfigurationTestValues`), the expected text read as the type the configuration gives. Statuses: Passed, Failed (expected and actual), Needs review (an input the configuration reads is not given, an input left the schema or no longer fits its type, or the covered configuration changed shape) and Error. An Object Matching Rule case compares match values and never looks them up; a Scope that cannot be determined fails. A case with no run-as-of time whose configuration reads the clock says it ran as of now. Results are computed per run, never stored.
- **History:** a top-level, Class C `testCases` collection in the Synchronisation Rule's snapshot (its Scope's, Attribute Flows' and own Object Matching Rules' cases) and the Connected System's (Simple mode Object Matching Rules' cases). Top level because classification takes the class of every changed node, containers included; nested in an Attribute Flow, adding a case would classify as B. Inputs and expected values are keyed hashes; a cascade lists the cases it removed.
- **Personal data:** inputs never reach a log or an Activity (an Activity names the case only); the REST list leaves inputs out unless `include=inputs`; inputs are stored unencrypted as other configuration is; the portal says so before a save.
- **Surfaces:** `/api/v1/configuration-tests` CRUD and `POST /run`; `Get-`, `New-`, `Set-`, `Remove-JIMConfigurationTestCase` and `Invoke-JIMConfigurationTest`; the Synchronisation Rule's **Tests** tab, the Connected System's Matching tab in Simple mode, and **Save as test case** in the Attribute Flow and Scope testers. The Object Matching Rule editor's Try it tests a rule before it is added, so it has nothing saved to cover.
- **As built:** every portal save and run of a case goes through a fresh `JimApplication`: the Synchronisation Rule page holds its rule change-tracked for the edit session, and an Activity's save on that context would save the half-edited rule with the case.

### Phase 4b: The save gate ✅

- **Run on save:** saving what a case covers runs its cases. ~~Deciding "a save changes something a case covers" with Phase 3's unchanged comparison (`SyncRuleMappingExpressionState.IsUnchangedIn` and its counterparts).~~ Superseded during the build, with the orchestrator's agreement: see "As built (regression gate)".
- **The gate:** Keep editing (default), Accept the new result as expected, or Save anyway (recorded with the change in Configuration History). REST answers 409 unless `saveAnyway`; PowerShell takes `-SaveAnyway`.
- **Never a lockout:** disabling a Synchronisation Rule, removing an Attribute Flow and deleting either always save, whatever their cases give; tests prove it.
- **As built (regression gate):** a case gates a save only when it passes against the configuration as stored and does not against what the save proposes (`ConfigurationTestGate`). `IsUnchangedIn` could not decide it: it compares expressions and targets only, not Missing Input Behaviour, value processing, case normalisation, a switch into or out of a Generated Value, or source order; the Scoping Criterion state is per criterion, not group logic, nesting or removals; Object Matching Rules have none. Comparing results needs no change-detection model to keep complete, and cannot lock anyone out: a case already failing never stops a save, and a disable, a reorder or a case-sensitivity toggle cannot change a result. The stored side is read untracked before the save's first flush (`GetSyncRuleUntrackedAsync`, `GetObjectMatchingRuleUntrackedAsync`); both runs are as of one instant read once per save, so a case reading the clock cannot regress between them. Debug logs the case count and elapsed time per gated save, and an owner holds at most 200 cases (`ConfigurationTestCase.MaxTestCasesPerOwner`).
- **As built (paths):** the whole-rule save (portal, `PUT sync-rules/{id}` and every Scoping Criteria endpoint), an Attribute Flow's settings (`PATCH .../mappings/{id}`) and an Object Matching Rule update at both levels. Creates have no cases yet; deletes take their cases with them.
- **As built (accept):** `acceptNewResults` / `-AcceptNewResult` rewrites only the cases that regressed in that save, after the save's write and before its capture, so their Class C changes are in the same Configuration History version. A Needs review, Error or undetermined Scope result cannot be accepted and still refuses the save unless it is saved anyway.
- **As built (record):** `Activity.ConfigurationTestGateNote` names each case saved past or accepted, by name and status, never values; Configuration History shows it on the portal, the REST history item and `Get-JIMConfigurationChangeHistory`.
- **As built (surfaces):** the 409 body is `{ message, regressions: [{ result, canAccept, acceptedExpectation }] }`; PowerShell turns it into an error naming each case and its new result. The portal's gate dialog follows the Configuration Change acknowledgement; Keep editing leaves the edit on the page.
- **Fix found on the way:** Phase 4a ran a Synchronisation Rule mode Object Matching Rule case with its Synchronisation Rule's Metaverse Object Type set on a copy, which `DescribeInvalidity` refuses, so every such case needed review. The runner now runs the rule as stored.

### Phase 5: Docs and pipelines ✅

- The Attribute Flow, Scope, Object Matching and Generated Values pages link to the **Configuration Tests** page (which gained the save gate in 4b), and `expressions.md` once its word budget allows.
- An integration scenario step running `Invoke-JIMConfigurationTest -FailOnUnsuccessful` in a pipeline.
- Plan closeout: Status Done, moved to `done/`, in the PR that closes #2055.
- **As built:** Scenario 010 Test 11 (`-Step ConfigurationTests`) is PRD Scenario 15 end to end over PowerShell: two Scope cases pass under `-FailOnUnsuccessful`, a criterion change that breaks them is refused naming both and leaves the criterion as stored, `-SaveAnyway` saves it, the next pipeline run fails, and Configuration History records the cases saved past. `expressions.md` stays unlinked: it is already over the 5,000-word budget (STR-8), and its testing section links the Attribute Flow and Generated Value pages, which now link on.

## Decisions

Names are settled here so every phase uses them.

| Capability | REST API | PowerShell | Phase |
|---|---|---|---|
| Attribute Flow (and Generated Value base expression) | `POST /api/v1/synchronisation/test-expression` with `syncRuleId` and `mappingId` | `Test-JIMExpression -SyncRuleId -MappingId` | 1 (2 adds the remaining inputs) |
| Scope | `POST /api/v1/synchronisation/sync-rules/{syncRuleId}/scoping-criteria/test` | `Test-JIMScope` | 2 |
| Object Matching Rule | `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules/{ruleId}/test`, and `.../sync-rules/{syncRuleId}/matching-rules/{ruleId}/test` | `Test-JIMMatchingRule` (parameter sets by `-ConnectedSystemId` or `-SyncRuleId`; `-Lookup`) | 2 |
| Example Data template expression | `POST /api/v1/example-data/templates/{templateId}/test-expression` | `Test-JIMExampleDataExpression` | 2 |
| Test cases | `GET/POST /api/v1/configuration-tests`, `GET/PUT/DELETE /api/v1/configuration-tests/{id}` | `Get-`, `New-`, `Set-`, `Remove-JIMConfigurationTestCase` | 4a |
| Run test cases | `POST /api/v1/configuration-tests/run` (by Synchronisation Rule, Connected System or ids) | `Invoke-JIMConfigurationTest` | 4a |

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
| Saved test cases hold personal data | Phase 4a's notice; values kept out of logs, Activities and history; test cases are configuration, deleted with what they cover |
