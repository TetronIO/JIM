# Expression Value Sources in Scoping Criteria

- **Status:** Doing (PR 1 complete: model, evaluation, validation, REST, PowerShell, reconciliation; PR 2, the portal, outstanding)
- **Issue:** [#2054](https://github.com/TetronIO/JIM/issues/2054) (Phase 3 of [#207](https://github.com/TetronIO/JIM/issues/207))
- **PRD:** [PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md](../../prd/doing/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md) (section "Phase 3: Expression value sources in scoping criteria", Scenario 10, Open Question 3)

## Overview

A Synchronisation Rule's scoping criterion compares an attribute today. This plan lets its left-hand side be an expression instead (`Lower(Trim(cs["department"]))` on an import rule, `mv["..."]` on an export rule), compared as a type the administrator chooses, with every operator in the catalogue, an explanation that shows the computed value, and the same surfaces the attribute form has: portal, REST API, PowerShell, the scoping preview and the scope tester.

## Business Value

- Administrators scope on the value they mean, not on whatever shape the source system stores. Today `department` holding ` FINANCE ` and `Finance` needs two criteria, or cannot be scoped at all when the value has to be reshaped (a substring, a part of a DN, a joined pair).
- Scope stays explainable: the explanation names the expression, its inputs and what it computed. It never says only "the expression returned false".
- A criterion that cannot be evaluated never moves an object in or out of scope.

## Technical Architecture

### Current state (2026-10-13, after #2053)

- `SyncRuleScopingCriteria` (`src/JIM.Models/Logic/SyncRuleScopingCriteria.cs`) names its left-hand side by `MetaverseAttributeId` (export) or `ConnectedSystemAttributeId` (import). `GetAttributeDataType()` and `GetAttributeName()` derive the type and name from that attribute.
- One evaluator, `ScopingEvaluator` (`src/JIM.Application/Servers/Scoping/`), serves sync, export evaluation, deprovisioning, recall, Sync Preview, the Configuration Change Previews and the scope tester. `EvaluateCriterion` reads the attribute's values through `IScopingValueSource` and compares them with `CriterionComparer`. No scoping criterion is ever translated to SQL; the only database pre-filter is temporal reconciliation's date-range query for relative-date criteria.
- `ScopingEvaluationServer` holds no `IExpressionEvaluator`.
- Several paths gather the attributes scoping reads by attribute id only, and load only those values:
  - the export recall fast path (`ExportEvaluationServer.CollectScopingAttributeIds`, `MetaverseRepository.GetMetaverseObjectRecallSummariesAsync`);
  - Connections tab explanations (`MetaverseServer.ConnectionExplanations.CriteriaAttributeIds`);
  - the scope tester (`ScopeTester.ComparedAttributes`, `SchemaAttributes`);
  - configuration scope and attribute-reference tracking (`SyncRuleScopingTreeLoader.GetCriterionOwnershipAsync`, `MetaverseRepository` attribute references).
- Expression sources already exist for Attribute Flow (`SyncRuleMappingSource.Expression`) and Object Matching (`ObjectMatchingRuleSource.Expression`). `ObjectMatchingRuleValidator.DescribeExpressionProblem` is the save-time template: length, parse, `ExpressionInputResolver.Resolve` (side, at least one input, inputs exist), then `NonRepeatableFunctionDetector`.
- `ExpressionWhere` already turns expression values into `ScopingValue`s, and `OneValueCursor` is a ready cursor.

### Proposed solution

**Model.** Two nullable columns on `SyncRuleScopingCriteria`:

- `Expression` (text): the left-hand side when it is not an attribute. Exactly one of the side's attribute id and `Expression` is set (`IsValid()`, as the other expression sources do).
- `CompareAs` (`AttributeDataType?`): the type the computed value is compared as. Required with `Expression`, null with an attribute. `GetAttributeDataType()` returns it for an expression criterion; `GetAttributeName()` returns the expression text for display.

"Compare as" is chosen explicitly, defaulting to Text (PRD Open Question 3: inference fails for an expression that returns null for the sample). Reference and Binary are not offered: neither has a scoping operator that would compare a computed value meaningfully.

**Evaluation.** `ScopingEvaluator.EvaluateCriterion` branches on the left-hand side:

1. Attribute: unchanged.
2. Expression: build the `ExpressionContext` from the object (the CS attributes on import, the MV attributes on export, with the evaluation's `asOf` as `NowUtc`), evaluate, convert each result element with `ExpressionResultConverter.TryConvert(element, CompareAs)` into a `ScopingValue`, and compare through `CriterionComparer` with a cursor over those values. A multi-valued result is met when any value meets it, exactly as a multi-valued attribute is.
   - No value (null, empty set): the same verdict as an attribute with no value, so **has no value** is met and every value operator is not.
   - An element that does not convert to `CompareAs`: the criterion is **Invalid** with the reason ("computed `abc`, which is not a Number").
   - `ExpressionEvaluationException` (including a pattern timeout): `ScopingCriterionFailedException` (PRD requirement 5), which every sync path turns into an error on the object with its scope and join left as they were (#2250, Decision 3). `ScopingEvaluationServer.CanFailToEvaluate` returns true for a rule carrying an expression criterion, so Synchronised Deprovisioning's pre-flight holds such objects back too.

The evaluator receives `IExpressionEvaluator` through `ScopingEvaluationServer`, which gains a constructor parameter. `JimApplication`, `ScopeTester` and `ScopeReconciliationServer` pass the one they hold, and the sync path uses `ExpressionInputResolver.ResolveCached` so parsing happens once per run, not once per object.

**Inputs reach every path.** One helper, `ScopingCriterionInputs.AttributeIds(criterion, schema)`, returns the attribute ids a criterion reads: its attribute, or the expression's resolved inputs. Every attribute-id-only site listed above calls it instead of reading `MetaverseAttributeId`/`ConnectedSystemAttributeId` directly. A test enumerates the call sites' outputs for an expression criterion so a new site cannot forget it.

**Relative dates.** An expression criterion compared as a date may use relative-date values (Within the last 30 days of `ParseDate(cs["startDate"], "dd/MM/yyyy")`). Temporal scope reconciliation's SQL pre-filter cannot see inside an expression, so a rule carrying such a criterion is reconciled by evaluating its object type in memory, under three conditions:

- the pass pages through the object type in batches and never loads it whole;
- the portal (beside the criterion) and the save response (REST and PowerShell) say plainly that the rule cannot be pre-filtered and costs a full pass of the object type on each reconciliation;
- the reconciliation Activity records how many objects it evaluated for the rule.

The scope tester's `NextChange` reads an expression date criterion from its computed value.

**Validation at save** (server-side, shared by REST, PowerShell and the portal), refusing:

- an expression over the length bound, or one that does not parse;
- one that reads no attribute, reads the wrong side, or names an attribute the Object Type lacks;
- one calling a non-repeatable function (`Now`, `RandomPassword`, ...): relative-date criteria cover time-based scope;
- a `CompareAs` the operator does not suit (`SearchComparisonOperators.IsValid(op, CompareAs, CriterionContext.Scoping)`), and a value that does not read as `CompareAs` (the existing `CriterionValidation`).

**Explanations.** `ScopingCriterionExplanation` gains `Expression`, `Inputs` (name and display value per input, masked as credentials are today) and keeps `ActualDisplay` for the computed value. `ScopingExplanationSummariser` words an expression criterion as "`Lower(Trim(cs["department"]))` computed `finance` from department ` FINANCE `, which starts with `fin`". The portal's `ScopingExplanationView`, the REST DTOs and `Get-JIMMetaverseObjectConnection` carry the new fields.

**Change history.** The snapshot emits `expression` and `compareAs` for scoping criteria (both class B; `expression` is already classified). The snapshot's existing omission of a scoping criterion's relative-date fields (`valueMode`, `relative*`; Predefined Search emits them) is fixed in the same change, since the same function is being edited and the completeness test covers both.

### Data flow (an expression criterion on import)

```
CSO values --> ExpressionContext (cs) --> IExpressionEvaluator.Evaluate
          --> ExpressionResultValues.Elements --> TryConvert(CompareAs) --> ScopingValue[]
          --> CriterionComparer.Evaluate(op, expected) --> CriterionVerdict
          --> ScopingTraceBuilder (expression, inputs, computed value) --> ScopingExplanation
```

## Decisions

1. **"Compare as" is chosen explicitly, defaulting to Text** (PRD Open Question 3). Inferring it from a test evaluation fails for an expression that returns no value for the sample.
2. **Relative dates are allowed on expression criteria**, at the cost of a batched in-memory pass per reconciliation for that rule, stated at save time and counted on the Activity. The alternative was to refuse relative-date values on expression criteria; it was rejected because a date the source writes as text is exactly what expressions exist for. (Product owner's delegate, 2026-10-13.)
3. **Failures in export, recall and deprovisioning** are fixed ahead of this plan as their own change ([#2250](https://github.com/TetronIO/JIM/pull/2250)), since a failing pattern criterion could already fail a whole export Activity. Every path errors the one object and fails closed; Synchronised Deprovisioning holds an undecidable object back through a read-only pre-flight over the rules `ScopingEvaluationServer.CanFailToEvaluate` names. This plan extends that predicate to expression criteria, so every path covers them without further change.

4. **Configuration scope, attribute references and attribute delete do not read expression inputs.** Plan step 3 listed them among the sites to move onto the shared input helper. They were left reading attribute ids only, because Attribute Flow and Object Matching expressions are not tracked there either: an attribute an expression names is invisible to all three for every kind of expression today. Making scoping alone track them would leave the behaviour inconsistent rather than fixed; the durable fix covers every expression kind at once and is its own change. Save-time validation still refuses an expression naming an attribute the type does not have, so the gap is limited to an attribute renamed or deleted after the rule was saved, which the run then reports per object.
5. **An explanation shows the expression and its computed value, not each input.** Plan step 5 asked for the inputs too. The Connections tab and REST explanation name the expression where they name an attribute, and show the value it computed; the per-input view belongs with the portal's Scope tester work in PR 2.
6. **Previews and the Sync Preview report an undecidable object instead of failing.** #2250 made synchronisation fail closed per object but left the Configuration Change Preview's scope walk and the Sync Preview throwing on the same exception, a rare event for a pattern and a routine one for an expression. The scope preview now reports such an object as one the run would fail, and the Sync Preview reports a `ScopingCriterionFailed` error. The Attribute Flow, Destructive Toggle and Object Matching preview adapters still fail as a whole on one, naming the rule and the reason; they evaluate scope only to skip out-of-scope objects, and changing their meaning is out of this issue's scope.

## Implementation Phases

One issue, delivered as two PRs so each stays reviewable. The final one closes #2054.

### PR 1: Model, evaluation, validation, REST and PowerShell ✅

1. Migration adding `Expression` and `CompareAs` to `SyncRuleScopingCriteria`; `IsValid()`; `GetAttributeDataType()`/`GetAttributeName()`.
2. `ScopingEvaluator` expression branch; `ScopingEvaluationServer` takes `IExpressionEvaluator`; cached input resolution on the sync path. `CanFailToEvaluate` returns true for an expression criterion, so #2250's per-object handling and Synchronised Deprovisioning's pre-flight cover it. Scenario 008 (Medium) before the PR, since this touches export evaluation.
3. `ScopingCriterionInputs.AttributeIds` and every attribute-id-only site moved onto it (export recall, Connections tab, scope tester, configuration scope, attribute references and attribute delete).
4. Save-time validation (`ScopingCriterionExpressionValidator`, modelled on `ObjectMatchingRuleValidator`).
5. Explanation fields and wording; snapshot keys (plus the relative-date gap).
6. REST: `CreateScopingCriterionRequest` gains `Expression` and `CompareAs`, mutually exclusive with the attribute ids; the DTO returns them; the scoping preview request carries them.
7. PowerShell: `New-`/`Set-JIMScopingCriterion` gain a `ByExpression` parameter set (`-Expression`, `-CompareAs`, aliases per `src/JIM.PowerShell/CLAUDE.md`); `New-JIMConfigurationChangePreview -ScopingCriteriaGroup` accepts them. Pester tests.
8. Temporal reconciliation for expression date criteria (batched full pass, the save-time notice, the evaluated count on the Activity) and the scope tester's `NextChange`.
9. Docs: `docs/configuration/synchronisation-rules.md` (scoping), the PowerShell and API reference, and the "Criteria expressions use the JIM expression language" claim the PRD's Affected Areas table says to fix.

### PR 2: Portal, Scenario 10, and closing the issue

1. Add Criteria dialog: **Compare: Attribute | Expression** (`SegmentedToggle`). Expression shows `ExpressionEditor` (with suggestions, Insert attribute, Insert function and the syntax check from #2053), a **Compare as** select, then the operator list from `CriterionOperatorOptions.ForType(CompareAs, Scoping)` and `CriterionValueEditor`. Add stays disabled until the expression parses and the operator and value suit the type.
2. Criterion chips show an expression criterion's expression (highlighted, as the `Ex` chip does).
3. `ScopeTestPanel` and `ScopingExplanationView` show the expression, its inputs and the computed value.
4. bUnit tests for the dialog's gating and the chip; light and dark screenshots.
5. Integration: Scenario 010 gains a step for Scenario 10 of the PRD (an import rule scoped on `Lower(Trim(cs["department"]))` starts with `fin`: `Finance` and ` FINANCE ` in scope, `Facilities` not, each explanation showing the computed value). Run Scenarios 010 and 011 to prove no regression; 011's matrix is attribute-based and stays so.
6. Changelog, PRD acceptance criteria ticked, plan to `done/`, `Closes #2054`.

## Success Criteria

- PRD Scenario 10 passes end to end (Scenario 010) and in unit tests.
- An expression criterion behaves identically on import sync, export evaluation, the recall fast path, Sync Preview, the scoping Configuration Change Preview and the scope tester (one test fixture run through each).
- An evaluation failure leaves the object's scope unchanged and records an error on it.
- Portal, REST and PowerShell each create, read, update and test an expression criterion.
- Scenarios 010 and 011 pass unchanged apart from the new step.

## Benefits

- Scope expresses intent rather than the source's formatting.
- One evaluator and one comparison serve attributes and expressions, so every operator means the same thing in both.

## Dependencies

- #2053 (merged as #2223, #2229, #2240): the function catalogue, `ExpressionWhere`'s conversion to `ScopingValue`, the editor assist.
- #2250: per-object handling of an undecidable criterion on every sync path, and `CanFailToEvaluate`.
- #2055's `AttributeSample`/`AttributeSampleDescriptor` (already on `main`) for the scope tester's inputs.
- No new packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| A path that loads only the attributes scoping reads misses an expression's inputs, so the criterion sees no value and silently changes scope. | One `ScopingCriterionInputs.AttributeIds` helper used at every site, and a test that runs an expression criterion through each path. |
| Per-object expression evaluation slows sync on large Connected Systems. | Parse once per run (`ResolveCached`); expressions are pure and in-process. Measure on the Medium template with Scenario 010 and record the timing in the PR. |
| Relative-date expression criteria make temporal reconciliation evaluate the whole type. | Batched, never loaded whole; said at save time in every surface; the count recorded on the Activity. |
| A pattern or conversion error moves objects out of scope. | Every failure is `ScopingCriterionFailedException`: an error on the object, scope state unchanged, tested on import, export and recall. |
| Snapshot keys added without classification. | `ConfigurationChangeClassificationCompletenessTests` fails the build. |
