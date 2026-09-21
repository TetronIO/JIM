# Metaverse-Derived Attribute Flows

- **Status:** Planned
- **Created:** 2026-09-19
- **Author:** JayVDZ (PRD drafted via Claude Code)
- **Issue:** to be created
- **Related:** [#242](https://github.com/TetronIO/JIM/issues/242) Unique Value Generation (depends on this PRD), [#1361](https://github.com/TetronIO/JIM/issues/1361) Missing Input Behaviour, [#91](https://github.com/TetronIO/JIM/issues/91) Attribute Priority, [#892](https://github.com/TetronIO/JIM/issues/892) Temporal Scope Reconciler (the review-flag mechanism reused here)

## Problem Statement

An import Attribute Flow expression can read the Connected System Object only. `ProcessExpressionMapping` builds its context with `metaverseAttributes: null` (`src/JIM.Application/Servers/SyncEngine.AttributeFlow.cs`), so `mv["..."]` in an import expression is unsupported, by design. Export expressions read the Metaverse Object only. Consequently there is no way to derive one Metaverse attribute from another attribute of the same object and hold the result *in the Metaverse*: an Email built from Account Name, a Display Name built from First Name and Last Name, a UPN built from Email.

Today the only workaround is an export expression per target, which leaves the Metaverse without the value: nothing else can flow it, the portal cannot show it, and every target repeats the expression. The gap becomes blocking with #242, whose canonical demo is an Account Name JIM generates and an Email and UPN derived from it.

Deriving values inside the Metaverse also raises a question JIM has never had to answer: the order attributes are evaluated in. If Email reads Account Name and UPN reads Email, they must be evaluated in that order or synchronisation becomes non-deterministic. JIM has no dependency ordering for Attribute Flows; Example Data does (`ExampleDataObjectType.GetTemplateAttributesInDependencyOrder`), and that is the precedent to lift.

## Goals

- An administrator can write an import Attribute Flow expression that reads the same object's Metaverse attributes with `mv["..."]`, and the result is held in the Metaverse like any other contributed value.
- Derived values are evaluated in dependency order, deterministically, and are re-evaluated whenever any of their inputs changes, regardless of which Connected System's synchronisation changed it.
- Misconfiguration (a cycle, a self-reference) is rejected when the configuration is saved, naming the attributes and Synchronisation Rules involved.
- Derived values participate in Attribute Priority, "Null is a value", Missing Input Behaviour and recall exactly as any other contribution; no new contribution semantics.
- #242's generated values slot into the same ordering: a generated attribute is a node in the graph and derived attributes downstream of it are evaluated after it resolves.

## Non-Goals

- **No cross-object derivation.** `mv["Manager"]` is read as the reference value, never traversed to the manager's attributes. A cross-object graph would make every manager change fan out to their reports; that is a different feature.
- **No Metaverse-owned rules.** Derived flows are hosted on import Synchronisation Rules and are connected, prioritised and recalled through that rule, as every mapping is today. Attribute logic with no Connected System belongs with internally managed Metaverse Objects.
- **No change to export expressions.** They continue to read the Metaverse Object only.
- **No detection of loops through connector spaces** (export Account Name to a directory, import it back into an attribute Email reads). Those are not graph-visible and are already possible today.
- **No new expression syntax.** `mv["..."]` already exists for export expressions; it becomes available on import expressions.

## Key Concepts

**Derived Attribute Flow.** An import Attribute Flow whose expression reads one or more `mv["..."]` attributes of the object being flowed. It may also read `cs["..."]`. It is detected from the expression's inputs (`ExpressionInputResolver`), not declared.

**Dependency graph.** Per Metaverse Object Type, across every import Synchronisation Rule for that type: an edge from each `mv` input to the target attribute of the derived flow that reads it. Generated attributes (#242) are nodes like any other. The graph is built once per run, as `AttributePriorityContext` is.

**Level.** The topological depth of a node. Level 0 attributes have no derived inputs; level n attributes read only attributes at levels below n. Evaluation proceeds level by level.

**Derived pass.** After the ordinary inbound flow for an object, the engine evaluates the derived flows whose rules are connected for that object, in level order, for every derived flow with a changed input (delta synchronisation) or every derived flow (full synchronisation).

## User Stories

1. As an IDAM administrator, I want Email to be built from Account Name in the Metaverse, so every target and the portal see the same value without repeating the expression.
2. As an IDAM administrator, I want UPN built from Email and Email built from Account Name to come out right in one synchronisation, in the right order, every time.
3. As an IDAM administrator, when Region is contributed by a different system than the one hosting my Email expression, I want Email to update when Region changes, not only when HR changes.
4. As an IDAM administrator, if I accidentally make two attributes depend on each other, I want the save to fail and tell me which two, not a synchronisation that never settles.
5. As an IDAM administrator, when JIM corrects a generated Account Name after a collision, I want the derived Email and UPN to follow in the same synchronisation.

## Requirements

### Functional Requirements

**Authoring**

1. `mv["..."]` is accepted in import Attribute Flow expressions and resolves to the same object's Metaverse attributes. The expression editor on import rules offers Metaverse attribute pickers beside the Connected System ones.
2. On save of any import Synchronisation Rule, the dependency graph for the object type is rebuilt across all import rules for that type. A cycle, including a self-reference (an attribute's own expression reading `mv` of that attribute), rejects the save with a message naming every attribute and Synchronisation Rule on the cycle. Either rule involved may be the one being saved.
3. Disabling or removing a mapping that another derived flow reads is allowed, and the save surfaces the dependent flows that will now have a missing input (following the `SchemaRefreshDependentDetector` pattern).

**Evaluation**

4. Derived flows are evaluated in level order with a canonical tie-break within a level (Attribute Priority, then mapping id). Two runs over the same data produce the same values.
5. The derived pass runs for an object after its ordinary inbound flow, in every synchronisation of every Connected System where the object has a joined and in-scope Connected System Object for the derived flow's rule (the same connectedness test Attribute Priority applies). Under delta synchronisation only derived flows with at least one changed input are evaluated; under full synchronisation all are.
6. A derived flow reads the object's effective values as of this pass, including values contributed earlier in the same pass and not yet persisted.
7. A derived flow is a contribution: it wins or loses under Attribute Priority, "Null is a value" applies, Missing Input Behaviour (#1361) applies to absent `mv` inputs exactly as to absent `cs` inputs, and its values are recalled when its rule disconnects.
8. A derived value's change is an ordinary attribute change: it enters the object's changed-attribute set so export evaluation, drift detection and change history see it in the same pass.
9. An object flagged for review (#892's per-object review flag) runs its derived pass as part of the review, so a value revised outside an import (a generated value corrected after a collision, #242) re-derives its dependants in the same run.
10. When a generated attribute (#242) is an input, the derived flows downstream of it are evaluated after the generated value resolves, level by level; a level containing generated attributes is resolved in one batch before the next level runs.

**Preview and tooling**

11. Sync Preview and Configuration Change Preview evaluate derived flows in the same order and show the derived results.
12. Validation failures (cycles) are reported identically through the portal, the REST API and PowerShell; the expression itself already round-trips through all three surfaces, so no new configuration fields are needed.

### Non-Functional Requirements

- The graph is built once per run and shared across pages; no per-object graph work.
- The derived pass adds no database round trips beyond those the ordinary flow already makes for the object; it is in-memory evaluation over values the pass already holds.
- Determinism (FR 4) is covered by a test that permutes mapping creation order and asserts identical results.
- British English; proper-noun casing.

## Examples and Scenarios

### Scenario 1: Email from Account Name

**Given** an import rule with Email = `mv["Account Name"] + "@corp.local"` and UPN = `mv["Email"]`
**When** a Metaverse Object's Account Name is contributed
**Then** Email is evaluated after Account Name and UPN after Email in the same synchronisation, and all three reach the Metaverse together.

### Scenario 2: Input from another system

**Given** Email = `mv["Account Name"] + "@" + mv["Region"] + ".corp"` hosted on the HR rule, with Region contributed by the AD rule
**When** AD's delta synchronisation changes Region
**Then** Email is re-evaluated in that synchronisation, provided the Metaverse Object is joined and in scope for the HR rule.

### Scenario 3: Cycle rejected

**Given** rule 1 derives Display Name from `mv["Mail Nickname"]` and an administrator saves rule 2 deriving Mail Nickname from `mv["Display Name"]`
**Then** the save fails naming Display Name, Mail Nickname, rule 1 and rule 2.

### Scenario 4: Missing input

**Given** Email reads `mv["Account Name"]` with Missing Input Behaviour "contribute no value" and a Metaverse Object with no Account Name yet
**Then** Email contributes nothing and is resolved by priority; when Account Name later arrives, Email is derived.

### Scenario 5: Re-derived after a correction

**Given** Account Name is generated (#242) and corrected from `joe.bloggs` to `joe.bloggs1` during an export run
**When** the object's review runs in the next synchronisation
**Then** Email and UPN are re-derived from `joe.bloggs1` in that run.

### Scenario 6: Full versus delta

**Given** a derived flow whose inputs did not change
**Then** delta synchronisation skips it and full synchronisation evaluates it to the same value.

## Constraints

- `SyncEngine` stays free of I/O; the derived pass is in-memory.
- No changes to `DynamicExpressoEvaluator`.
- Cycle detection must consider every import rule for the object type, not only the rule being saved.

## Affected Areas

| Area | Impact |
|------|--------|
| Application | `SyncEngine.AttributeFlow`: import expression context gains the object's effective Metaverse values; a `DerivedFlowGraph` built per run beside `AttributePriorityContext`; the derived pass. `ConnectedSystemServer` save validation: cycle detection across rules. `ExpressionInputResolver`: Metaverse inputs on import expressions. |
| Worker | `SyncTaskProcessorBase`: run the derived pass after inbound flow and inside the #892 review processing; level interleaving with #242's generated requests. |
| Web | Expression editor on import rules: `mv["..."]` pickers; validation messages. |
| API / PowerShell | Validation error surfaced; no new fields. |
| Preview | `SyncRuleAttributeFlowPreviewAdapter` and Configuration Change Preview run the derived pass. |
| Tests | Graph construction, topological order and tie-break, cycle and self-reference rejection across rules, determinism under permutation, delta versus full, cross-rule re-evaluation, same-pass visibility, review-flag re-derivation, Missing Input Behaviour on `mv` inputs. Integration: Scenario 1's Email and UPN derived from Account Name (#242 Scenario 22 shares it). |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/synchronisation-rules.md` | New section: deriving Metaverse attributes from other attributes; ordering; cycles; which system's synchronisation re-evaluates them. |
| `docs/concepts/expressions.md` | `mv["..."]` now available on import expressions; the missing-input note updated. |
| `CHANGELOG.md` | `✨` entry. |
| `engineering/` | Developer guide: the graph, the derived pass and the level interleave with generated values. |

## Dependencies

- None to build. #242 depends on this PRD for its Scenario 1 conversion and for evaluating generated base expressions that read `mv["..."]`.
- Reuses #892's per-object review flag, `ExpressionInputResolver`, Missing Input Behaviour (#1361) and the Example Data topological sort.

## Open Questions

1. Should a warning be raised at save for non-pure expressions (dates, `Now()`) in derived flows, since they re-evaluate to a new value every run and would churn exports?
2. Metaverse-owned attribute rules (no hosting Connected System) are the natural evolution; decide whether to design them with internally managed Metaverse Objects or earlier.

## Acceptance Criteria

- [ ] `mv["..."]` works in import expressions with pickers in the editor (FR 1).
- [ ] Cycles and self-references across rules are rejected at save with both rules named (FR 2); dependents of a disabled or removed mapping are surfaced (FR 3).
- [ ] Evaluation is level-ordered and deterministic under permutation of configuration order (FR 4).
- [ ] Cross-system input changes re-evaluate derived flows in the changing system's synchronisation when the hosting rule is connected (FR 5, Scenario 2).
- [ ] Same-pass visibility, priority, "Null is a value", Missing Input Behaviour and recall behave as for any contribution (FR 6, 7).
- [ ] Derived changes reach export evaluation, drift detection and change history in the same pass (FR 8).
- [ ] Review-flagged objects re-derive (FR 9, Scenario 5); generated inputs are interleaved by level (FR 10).
- [ ] Previews show derived results (FR 11); validation parity across surfaces (FR 12).
- [ ] Docs and changelog updated; build and tests green; new behaviour tested red-first.

## Additional Context

- Inbound expression context: `SyncEngine.AttributeFlow.cs` (`ProcessExpressionMapping`, `metaverseAttributes: null`; the #1361 comment on why `mv` was unsupported).
- Topological sort precedent: `src/JIM.Models/ExampleData/ExampleDataObjectType.cs` (`GetTemplateAttributesInDependencyOrder`, `Visit` with cycle detection).
- Review flag: `SyncTaskProcessorBase.ProcessScopeReviewPendingMetaverseObjectsAsync` (#892), run by full and delta synchronisation.
- Per-run context precedent: `AttributePriorityContext` built once in `SyncTaskProcessorBase`.
