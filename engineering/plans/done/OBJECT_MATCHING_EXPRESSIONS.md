# Object Matching Rules: Match on an Expression

- **Status:** Done (Layers 1, 2 and 3 complete)
- **Issue:** [#2052](https://github.com/TetronIO/JIM/issues/2052)
- **PRD:** [PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md](../../prd/done/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md) (Phase 1, Decisions 1, 4 and 6)
- **Builds on:** [CASE_SENSITIVITY_POLICY.md](CASE_SENSITIVITY_POLICY.md) PR 1 (the bottom layer of this stack)

## Overview

An Object Matching Rule can match on an expression over the Connected System Object, as well as on an attribute. The computed value is compared for equality with the rule's Metaverse attribute. There are no other operators.

The data model already holds `ObjectMatchingRuleSource.Expression`. Nothing evaluates it, and every surface refuses or ignores it.

The same work fixes three ways matching hides failure or picks arbitrarily today:

- every matching failure is swallowed, and the object projects or provisions anyway;
- export matching claims the lowest-id candidate when several match;
- a multi-valued source stops at the first value that finds one match.

## Business Value

- **No more duplicates from format differences.** Joining and provisioning work across identifiers that differ only in format (prefixes, padding, whitespace, combined keys), where today each mismatch is a duplicate identity or a duplicate account.
- **A broken rule stops objects visibly.** It no longer floods the Metaverse with duplicates.
- **Ambiguity is the administrator's decision**, never JIM's guess.
- **What a rule computed is visible** before saving (Try it, Preview Impact) and after joining (connection explanations).

## Technical Architecture

### Current state

- **Import:** `SyncTaskProcessorBase.AttemptJoinAsync` (5934-5982) runs `FindMatchingMvoForJoinAsync` (6009-6024), then `ObjectMatchingServer.FindMatchingMetaverseObjectAsync` (45-92), then `MetaverseRepository.FindMetaverseObjectUsingMatchingRuleAsync` (3055-3210).
  - The repository reads the Connected System Object's values itself.
  - It loops over the values, one query each, and returns at the first value with exactly one match.
  - It throws on multiple sources, on an expression source and on unsupported types.
  - `ObjectMatchingServer` swallows everything except `MultipleMatchesException` (84-88).
- **Export:** `ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync`, on `ProvisionNewCso` (2448-2519), runs `AttemptExportMatchingAsync` (3563-3596). That uses either the page prefetch (`ExportMatchCandidates`, page-scoped, built by `PrefetchExportMatchCandidatesForPageAsync` 187-263) or `FindMatchingConnectedSystemObjectAsync`.
  - Both take the lowest id when several candidates match (`ObjectMatchingServer` 212-216; `ConnectedSystemRepository` 6103-6111).
  - `AttemptExportMatchingAsync` swallows every exception and provisions (3590-3595).
  - `ExportMatchingValue.Resolve` reads only the first Metaverse value.
- **Previews:**
  - `ObjectMatchingPreviewAdapter` refuses expression sources as Blocking (401-405).
  - `SyncPreviewServer.ProbeForJoinAsync` (2201-2251) bypasses `ObjectMatchingServer` and calls the repository directly.
- **Join provenance:** the Connected System Object records `JoinMethod`, `JoinSyncRuleId` and `JoinSyncRuleName`. It does not record which Object Matching Rule matched, or on what value.

### Proposed solution

**1. One match-value pipeline: `MatchValueResolver`** (in `JIM.Application`, beside `ObjectMatchingServer`).

- **Input:** a rule, plus an attribute value set. The value set is the Connected System Object's attribute values grouped by attribute name. It is built from `av.Attribute.Name`, which every loader includes, so it does not depend on `cso.Type` (which the preview loader leaves null).
- **Output:** a `MatchValueResolution` (in `JIM.Models/Logic`), which is one of:
  - **values:** typed and de-duplicated;
  - **no value;**
  - **failed:** with the rule, the expression, the inputs read and the message.
- **Attribute source:** every value of the attribute.
- **Expression source:**
  - It builds the expression context exactly as Attribute Flow does, via `SyncEngine.BuildCsoAttributeDictionary` semantics. Phase 2 (#2053) later changes that one builder for every context.
  - It evaluates through `IExpressionEvaluator`.
  - Null, empty and whitespace mean no value. A string array means several values.
  - It converts each value to the target Metaverse attribute's type, using the conversion Attribute Flow uses. Extract the scalar switch from `SyncEngine.CreateMvoAttributeValueFromExpressionResult` (1539 onward) into a shared static converter that both call.
  - A cast or conversion failure is **failed**, never a skipped value.
- **Used by every path:** import join, export matching, the export map build, both previews, and Try it. This is the "one evaluation pipeline" the test harness (#2055) generalises.

**2. Lookup by values: the repository API changes from "match this object" to "find by these values".**

- New method: `FindMetaverseObjectIdsByMatchValuesAsync(int metaverseObjectTypeId, int metaverseAttributeId, AttributeDataType type, IReadOnlyList<object> values, bool caseSensitive, int take = 2)`.
- It runs **one** query over all values: `= ANY(@values)`, or `lower(...) = ANY(@lowered)` to use the `(AttributeId, LOWER("StringValue"))` index.
- It returns distinct Metaverse Object ids.
- `ObjectMatchingServer` decides the outcome:
  - zero ids: the next rule;
  - one id: load the object and join;
  - more than one id: `MultipleMatchesException`, so the values together resolve to more than one identity.
- PostgreSQL and in-memory implementations both change, and the old method goes.

**3. Failures are errors, never swallowed.**

- `ObjectMatchingServer` throws `ObjectMatchingRuleFailedException` (new, `JIM.Models/Exceptions`) for a failed resolution.
- `FindMatchingMvoForJoinAsync` maps it to `SyncJoinException(ObjectMatchingRuleFailed)`. Projection never runs, because the exception leaves `ProcessMetaverseObjectChangesAsync` before line 1938.
- A rule with no Metaverse Object Type is still skipped as today: it is a configuration state the portal already flags, and skipping it is deterministic.

**4. Export.**

- **Every value is looked up.** `ExportMatchingValue.Resolve` returns every value of the Metaverse attribute.
- **Expression rules use a lazily built per-run map.** `ExpressionMatchValueIndex` (new, `JIM.Models/Transactional`) is held in `ExportEvaluationCache.ExpressionMatchIndexes`, keyed by `(ruleId, connectedSystemObjectTypeId)`.
  - **Built** on the first provisioning decision that needs it.
  - **Source:** one raw SQL stream of unjoined, Normal-status Connected System Objects of the type. It reads only the attributes the expression reads (`ExpressionInputResolver`) and feeds `MatchValueResolver`.
  - **Keyed** by `IdentifierKey.For` when the rule ignores case.
  - **Kept current:** claims remove entries, as `ExportMatchCandidates.Remove` does today.
  - **If any candidate fails to evaluate, the index is marked incomplete.** A later miss then cannot be trusted, so it becomes an error ("N existing accounts could not be checked") rather than a provision.
  - The per-object path (Sync Preview, which passes no cache) builds a transient index for the call.
- **Several candidates** (prefetch, per-object query or map) are reported as `AmbiguousProvisioningMatch` and nothing is claimed.
- **Results.** Matching failures, incomplete-index misses and ambiguities are added to a new `ExportEvaluationResult.ProvisioningMatchErrors` list for that rule, following the `ObjectTypeConflicts` pattern (2396-2401).
  - The worker turns each into an Activity item, following `BuildObjectTypeConflictRpei` (3560-3573). The item links the Metaverse Object (`MetaverseObjectId`, which the type-conflict item leaves unset).
  - Ambiguities record their candidates per section 8.
  - The Metaverse Object's other export rules continue, as they do for a type conflict.
- **`AttemptExportMatchingAsync` no longer has a catch-all.** Only the outcomes above leave it.

**5. Error types** (append-only; `ActivityRunProfileExecutionItemErrorType` ends at 27):

| Ordinal | Member | Meaning |
|---|---|---|
| 28 | `ObjectMatchingRuleFailed` | A rule's match value could not be worked out for this object (expression error, unconvertible value, or an incomplete export map). Nothing was joined, projected or provisioned. |
| 29 | `AmbiguousProvisioningMatch` | Before provisioning, more than one existing Connected System Object matched. Nothing was joined or provisioned. |

**Every site that lists or explains these error types gets the new members:**

- `ErrorTypeOrdinalTests`;
- `Helpers.ErrorPhaseTitles`, which `ErrorPhaseTitleTests` enforces;
- `CAUSALITY_REFERENCE.md` (a hand-maintained table and its member count);
- `SyncPreviewMessageCode` and `FullSynchronisationPreviewDeltas`;
- `engineering/developer/diagrams/FULL_SYNC_CSO_PROCESSING.md` and `ACTIVITY_AND_RPEI_FLOW.md`.

**6. Join provenance.**

- `ConnectedSystemObject` gains `JoinObjectMatchingRuleId` and `JoinMatchValue`, set by `RecordJoin` for matching joins.
- They follow the raw-SQL column rules: extend `CsoBulkColumns` and its writers, `BulkInsertColumnCompletenessTests`, and the CSO round-trip `RequiresPostgres` test.
- `ScopingExplanationSummariser.DescribeJoin` and `JoinRecord` name the rule (its order and expression) and the value.

**7. Validation at save** (portal, REST API and PowerShell, through `ConnectedSystemServer.EnsureObjectMatchingRuleIsWorkable`):

- **The model** (`ObjectMatchingRule.DescribeInvalidity`):
  - exactly one source;
  - each source holds exactly one of an attribute or an expression (`ObjectMatchingRuleSource.IsValid`).
- **Attribute sources:** the Connected System and Metaverse attribute types must match, and must be matchable (Text, Number, LongNumber, Decimal, Guid). Advanced mode's single-valued restriction is lifted, because every value is now considered.
- **Expression sources** are refused, each with its own message, when:
  - the expression is too long;
  - it does not parse (`Validate`);
  - it reads `mv["..."]`;
  - it reads no `cs["..."]` attribute;
  - it reads an attribute not on the object type;
  - it uses a non-repeatable function (`NonRepeatableFunctionDetector`).
- **The target Metaverse attribute** must be a matchable type.

**8. Ambiguous matches list and link their candidates** (PRD Phase 1 requirement 16; mock "Ambiguous match" on the canvas).

- **Model:** `ActivityRunProfileExecutionItemMatchCandidate` (new, `JIM.Models/Activities`), a child of the Activity item. It follows `CausalEdge`'s storage conventions:
  - a cascading foreign key to the item;
  - snapshot scalars for the candidate, with no foreign key, so a candidate deleted later still reads.
- **Fields:**
  - `CandidateKind` (Metaverse Object or Connected System Object);
  - `CandidateId`;
  - `DisplayNameSnapshot` and `TypeNameSnapshot`;
  - `ConnectedSystemId` and `ConnectedSystemNameSnapshot`, for Connected System Objects;
  - `MatchedValue`, so a multi-valued source shows which value found which candidate;
  - `ObjectMatchingRuleId` and `ObjectMatchingRuleDescriptionSnapshot`;
  - `Ordinal`;
  - `CandidateTotal`.
- **Not `CausalEdge` itself.** Candidates are not causes, and recording them as edges would put them in the causality panel's cause-and-effect walk.
- **Written in the same flush as the Activity items,** by raw SQL. That needs:
  - a `MatchCandidateBulkColumns` constants class;
  - its `BulkInsertColumnCompletenessTests` entries;
  - a `RequiresPostgres` round-trip test.
- **Capped at 10 candidates** per item, in ordinal display-name order so the list is the same on every run. `CandidateTotal` carries the full count.
  - **On import,** the lookup takes 11 ids. A count query runs only when there are more than 10.
  - **On export,** the candidate lists are already in hand (prefetch, per-object query, map).
- **The existing import AmbiguousMatch gains this too.** Today it records only a message, although `MultipleMatchesException.Matches` already carries the ids.
- **Read path:**
  - It resolves each candidate's existence best-effort: a deleted one renders its snapshot name unlinked, marked "no longer exists".
  - For import candidates, it also loads what each candidate Metaverse Object is currently joined to.
- **Surfaces:**
  - **Portal:** under Error Details in `ActivityRunProfileExecutionItemDetail.razor`, a candidates panel shows:
    - the object being matched, the rule and the value;
    - each candidate as an `<ObjectChip>` with its `Href`;
    - the total when capped;
    - a sentence on how to resolve.
  - **REST:** `ActivityRunProfileExecutionItemDetailDto.MatchCandidates`.
  - **PowerShell:** `Get-JIMActivityExecutionItem` outputs `MatchCandidates`.
  - **Previews:**
    - Sync Preview's ambiguity message carries the same candidate shape.
    - Preview Impact's ambiguous rows store theirs beside the persisted preview delta, in the same snapshot shape, and its drill-down renders them as linked chips.

### Data flow

```
Import:  CSO values --> MatchValueResolver --> values | no value (next rule) | failed (ObjectMatchingRuleFailed)
                                values --> FindMetaverseObjectIdsByMatchValuesAsync --> 0 (next rule) | 1 (join) | >1 (AmbiguousMatch)

Export:  MVO values --> attribute rule: prefetch or per-object query      --> 0 (provision) | 1 (claim, join) | >1 (AmbiguousProvisioningMatch)
                    --> expression rule: ExpressionMatchValueIndex (lazy) --> same outcomes; incomplete index + miss = ObjectMatchingRuleFailed
```

## Implementation Phases

This is a stack on top of [CASE_SENSITIVITY_POLICY.md](CASE_SENSITIVITY_POLICY.md) PR 1. Each layer follows TDD and keeps the build and tests green.

### Layer 1: Engine and import ✅

1. `MatchValueResolver`, `MatchValueResolution`, and the shared scalar converter (extracted, with Attribute Flow switched to it under its existing tests).
2. `FindMetaverseObjectIdsByMatchValuesAsync` in PostgreSQL and in-memory. `RequiresPostgres` tests cover:
   - the case-sensitive and case-insensitive variants, and their index use (`EXPLAIN` asserted in a database test);
   - several values resolving to one identity, and to two identities.
3. `ObjectMatchingServer` on the resolver and the new lookup, with no swallowing. `ObjectMatchingRuleFailedException`.
4. Error type 28 (`ObjectMatchingRuleFailed`), with every site in section 5.
5. `FindMatchingMvoForJoinAsync` maps the new exception. A worker test proves no projection follows it.
6. Join provenance (section 6), with the explanation wording.
7. `SyncPreviewServer.ProbeForJoinAsync` goes through the resolver and the guarded repository's new lookup. Its outcomes map to `SyncPreviewMessageCode`, and its ambiguity message carries the candidates.
8. **Match candidates** (section 8), recorded for import ambiguity.
    - Model, migration and bulk writer, with the column-list guard tests.
    - Read path.
    - The Activity item candidates panel, `MatchCandidates` on the REST DTO, and the `Get-JIMActivityExecutionItem` output, landing together for surface parity. Covered by bUnit, controller and Pester tests.
9. `ObjectMatchingPreviewAdapter`:
   - `MatchOutcome` carries the match values and a failed kind;
   - descriptions include the computed value;
   - a new transition reports a rule that would fail.

   The expression refusal stays until Layer 3 exposes authoring.

**Changelog:**
- 🐛 "A matching rule that cannot be evaluated now stops the object with an error instead of letting it project a duplicate identity."
- 🔄 "A Connected System Object whose values match different identities is now reported as an ambiguous match."
- ✨ "Ambiguous match errors now list and link every matching object, so you can go straight to the conflict."

**Docs:** the matching outcomes in `docs/configuration/synchronisation-rules.md`.

### Layer 2: Export ✅

1. `ExportMatchingValue.Resolve` returns every value. The prefetch (`PrefetchExportMatchCandidatesForPageAsync`, `GetExportMatchCandidateIdsAsync`) and the per-object query take every value.
2. Ambiguity is detected on all three paths, with no lowest-id claim. Error type 29 (`AmbiguousProvisioningMatch`), with every site in section 5. Its candidates (Connected System Objects) are recorded per section 8.
3. `ExpressionMatchValueIndex` and its lazy build:
   - a raw SQL stream (a read-side projection, so no column-list constant is needed);
   - claims removing entries;
   - incomplete marking.

   `RequiresPostgres` tests cover the build and the claim interplay (pattern: `ExportMatchingClaimDatabaseTests`).
4. `ExportEvaluationResult.ProvisioningMatchErrors`, and the worker's Activity items.
5. Remove the `AttemptExportMatchingAsync` catch-all.
6. Run Scenario 008, as required for export staging changes.

**Changelog:** 🐛 "When more than one existing account matches before provisioning, JIM now reports it for you to resolve instead of joining one of them." **Docs:** the export matching outcomes.

**As built:**
- The per-object path and the prefetch share one lookup, `GetExportMatchCandidateIdsAsync`; the old per-object repository query is gone. Every candidate is reloaded and re-checked as unjoined before it counts, on every path.
- `ExpressionMatchValueIndex` holds objects unjoined when it is built. An object that becomes unjoined later in the same run (a Disconnect deprovisioning, say) is considered by the next run's index, not this one's.
- An incomplete index fails only a lookup that finds nothing; one finding exactly one eligible object still joins it. The error names the first object that could not be worked out, so the administrator has somewhere to start.
- Sync Preview passes its own cache, so a preview over many objects builds an expression index once rather than per object.

### Layer 3: Surfaces

1. **Validation** (section 7), in `EnsureObjectMatchingRuleIsWorkable` and `DescribeInvalidity`.
2. **REST:**
   - `CreateObjectMatchingRuleSourceRequest.Expression`, with `ConnectedSystemAttributeId` made nullable and exactly one required;
   - `ObjectMatchingRuleSourceDto.Expression`;
   - the four controller messages (5781, 5907, 6117, 6231);
   - the preview request accepts `Expression` and the adapter's refusal goes;
   - Preview Impact's ambiguous rows store and render their candidates (section 8);
   - controller and DTO tests.
3. **PowerShell:** `-SourceExpression` in an `Expression` parameter set on `New-JIMMatchingRule`, `Set-JIMMatchingRule`, `New-JIMSyncRuleMatchingRule` and `Set-JIMSyncRuleMatchingRule`, mirroring `New-JIMSyncRuleMapping` (252-290, 407-452).
   - There are no Pester files for these cmdlets today. Add them, following `SyncRuleMappings.Tests.ps1`, including parameter-set assertions.
   - `New-JIMConfigurationChangePreview -MatchingRule` accepts `Expression`.
4. **Portal:** a shared `ObjectMatchingRuleEditor.razor` used by `ConnectedSystemObjectMatchingTab` and `SyncRuleMatchingTab`. It offers:
   - **Match on:** a `SegmentedToggle` between Attribute and Expression;
   - an `ExpressionEditor` with `InsertAttributeMenu` (`MetaverseAttributes="null"`, so Connected System attributes only), following `SyncRuleAttributeFlowTab` 564-585;
   - **Try it:** calls the resolver through a new application method and shows the match values and their type;
   - the operator shown as "equals";
   - the Metaverse attribute, and the Metaverse Object Type in Simple mode;
   - the case setting, defaulted through the PR 1 helper.

   Also:
   - The Simple mode grid's first column becomes **Match Value**, rendered with `<AttributeChip>`.
   - Advanced mode rule cards move to `<AttributeChip>`, retiring their hand-rolled CS/MV chips.
   - bUnit tests cover the logic: which inputs show for which source, gating of **Add Matching Rule**, and validation messages.
   - Per the UI rules, a screenshot of each tab goes on the PR.
5. **Docs:**
   - `docs/configuration/synchronisation-rules.md`: expression sources; why equals only; derived Metaverse attributes for the Metaverse side; remove the multi-source suggestion at 144.
   - `docs/powershell/synchronisation-rules.md`: `-SourceExpression`.
   - `docs/concepts/expressions.md`: the matching context reads `cs[...]` only.
   - `engineering/EXPRESSION_SECURITY.md`: the input is now reachable.
6. **Integration:** extend Scenario 005 (`Invoke-Scenario-005-MatchingRules.ps1`) with four steps:
   - ExpressionJoin: a prefixed HR number joins;
   - ExpressionExportMatch: an existing directory account is claimed through an expression;
   - AmbiguousProvisioningMatch: two candidates are refused;
   - ExpressionFailure: an unconvertible value errors and does not project.

**Changelog:** ✨ "Object Matching Rules can match on an expression, so identifiers that differ in format between systems still join and match."

**As built:**
- Section 7's checks live in `ObjectMatchingRuleValidator` (JIM.Application), because parsing an expression needs the evaluator. Saving, the Preview Impact adapter and the portal editor's live warning all call it, so the three cannot disagree about what is workable.
- Try it calls `ObjectMatchingServer.ResolveSampleMatchValues`, which converts each sample to its attribute's type and then runs the same `MatchValueResolver` a synchronisation does.
- The expression documentation lives in `docs/configuration/object-matching-rules.md` ("Matching on an expression"), the page that owns Object Matching Rules. `docs/configuration/synchronisation-rules.md` links to it rather than repeating it. The multi-source suggestion the plan names had already gone.
- Preview Impact's Matches column shows each candidate without its type, to keep the row to one line. The chip's tooltip names the type.
- Scenario 005's four steps share helpers with the existing ExportMatchJoin step. Those helpers append an HR row, reset the HR file, and create or delete an out-of-band directory account on each directory type.

## Success Criteria

- Expression rules join on import and match on export in both modes, across the portal, REST API and PowerShell.
- No matching failure or ambiguity projects, provisions or claims. Each is an Activity item naming the rule. Ambiguities list and link every candidate (up to 10, with the total) in the portal, REST API, PowerShell and both previews.
- Try it, Preview Impact, Sync Preview and connection explanations show the computed match value.
- Scenario 005 (extended), Scenario 008 and Scenario 014 pass; Scenario 014 shows no regression.

## Benefits

- **One pipeline.** One match-value pipeline serves synchronisation, both previews and Try it, so they cannot disagree.
- **Fewer queries on import.** One lookup query per rule replaces one per value.
- **Explanations that say why.** Join provenance names the rule and the value, which explanations have never had.

## Dependencies

- [CASE_SENSITIVITY_POLICY.md](CASE_SENSITIVITY_POLICY.md) PR 1 (the default helper), beneath this stack.
- #1750 derived Metaverse attributes, already shipped, for normalising the Metaverse side.
- No new packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Objects that silently projected or provisioned before now error after upgrade (a failing rule, an ambiguous candidate set) | That is the intended outcome: each was a duplicate in the making. The changelog says so, the errors name the rule and candidates, and Preview Impact and Sync Preview show them before a run |
| Building the export map over a large population of unjoined objects is slow or memory-heavy | Built only when a provisioning decision needs it, once per run; reads only the expression's inputs; measured in Scenario 014 at Medium and Large; persisting computed match values is the documented fallback if numbers demand it |
| An incomplete export map blocks provisioning for a whole rule because a few accounts carry bad data | The error counts and names the accounts that could not be evaluated, so the fix is targeted; the rule's other objects that find a match still claim |
| Extracting the shared converter changes Attribute Flow behaviour | Extract under Attribute Flow's existing tests with no expectation changes; add converter unit tests for every type |
| Several values matching one identity counted as ambiguous | The lookup returns distinct Metaverse Object ids, so several values resolving to one identity is one match; a test pins it |
