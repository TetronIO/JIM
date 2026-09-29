# Metaverse-Derived Attribute Flows

- **Status:** Doing
- **Created:** 2026-09-19
- **Author:** JayVDZ (PRD drafted via Claude Code)
- **Issue:** [#1750](https://github.com/TetronIO/JIM/issues/1750)
- **Related:** [#242](https://github.com/TetronIO/JIM/issues/242) Unique Value Generation (depends on this PRD), [#1361](https://github.com/TetronIO/JIM/issues/1361) Missing Input Behaviour, [#91](https://github.com/TetronIO/JIM/issues/91) Attribute Priority, [#892](https://github.com/TetronIO/JIM/issues/892) Temporal Scope Reconciler (the flag-and-reprocess precedent), [#1864](https://github.com/TetronIO/JIM/issues/1864) drift contributor fix, [#1861](https://github.com/TetronIO/JIM/issues/1861) Reference inputs (deferred)
- **Plan:** [`../../plans/doing/METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md`](../../plans/doing/METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md)
- **Updated:** 2026-09-29. Product-owner review of the plan revised which synchronisation evaluates a derived flow (FR 5, FR 9, Scenarios 2, 5 and 6, user stories 3 and 5): a derived flow runs only in its hosting system's synchronisation, and a change to its input elsewhere marks the hosting Connected System Object for re-evaluation. The original wording ran a rule's flows inside another system's synchronisation, a departure from sync sequencing that was not called out as one; see Behavioural Implications. Open questions resolved.

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

**Derived pass.** After the ordinary inbound flow for an object, in the synchronisation of the Connected System hosting the derived flows, the engine evaluates every derived flow on the object's in-scope rules of that system, in level order.

**Derived-input mark.** A per-object flag on a Connected System Object meaning "a Metaverse attribute that a derived flow on this system's rules reads has changed since this object was last synchronised". Set by JIM when another system's synchronisation, or a write outside synchronisation, changes such an attribute; honoured by delta and full synchronisation alike; cleared once the object is processed.

## User Stories

1. As an IDAM administrator, I want Email to be built from Account Name in the Metaverse, so every target and the portal see the same value without repeating the expression.
2. As an IDAM administrator, I want UPN built from Email and Email built from Account Name to come out right in one synchronisation, in the right order, every time.
3. As an IDAM administrator, when Region is contributed by a different system than the one hosting my Email expression, I want the next HR synchronisation to update Email, even if nothing about that person changed in HR.
4. As an IDAM administrator, if I accidentally make two attributes depend on each other, I want the save to fail and tell me which two, not a synchronisation that never settles.
5. As an IDAM administrator, when JIM corrects a generated Account Name after a collision, I want the derived Email and UPN to follow in the hosting system's next synchronisation.

## Behavioural Implications

What this feature changes about how an administrator must think about synchronisation. Each item was decided explicitly on 2026-09-29.

1. **Mapping order inside a Synchronisation Rule becomes dependency-driven.** JIM orders derived flows itself; nothing to configure.
2. **Cross-system sequencing is unchanged in principle.** A derived flow reads whatever the Metaverse holds when its hosting system synchronises, so synchronise sources before the systems whose rules derive from them, as today.
3. **New: JIM marks objects whose derived inputs changed elsewhere.** Neither delta nor full synchronisation otherwise revisits an object whose own data is unchanged, so without the mark a derived value could stay stale indefinitely. With it, the wrong sequence costs one cycle.
4. **`mv["..."]` in an import expression starts working.** Today it silently reads nothing. JIM is pre-release, so there is nothing to migrate.

## Requirements

### Functional Requirements

**Authoring**

1. `mv["..."]` is accepted in import Attribute Flow expressions and resolves to the same object's Metaverse attributes. The expression editor on import rules offers Metaverse attribute pickers beside the Connected System ones.
2. On save of any import Synchronisation Rule, the dependency graph for the object type is rebuilt across all import rules for that type. A cycle, including a self-reference (an attribute's own expression reading `mv` of that attribute), rejects the save with a message naming every attribute and Synchronisation Rule on the cycle. Either rule involved may be the one being saved.
3. Disabling or removing a mapping that another derived flow reads is allowed, and the save surfaces the dependent flows that will now have a missing input (following the `SchemaRefreshDependentDetector` pattern).

**Evaluation**

4. Derived flows are evaluated in level order with a canonical tie-break within a level (Attribute Priority, then mapping id). Two runs over the same data produce the same values.
5. The derived pass runs for an object after its ordinary inbound flow, only in the synchronisation of the Connected System hosting the derived flow's rule, and only when that system's Connected System Object for the object is joined and in scope for the rule. Every derived flow on the processed object's rules is evaluated.
5a. When a synchronisation of any Connected System changes a Metaverse attribute that a derived flow hosted on another system reads, JIM sets the derived-input mark on the object's Connected System Object in that hosting system. Delta and full synchronisation both process a marked object even if its own data is unchanged, and clear the mark once it is processed without error. A derived value is therefore never left stale indefinitely: at worst it waits for the hosting system's next synchronisation.
6. A derived flow reads the object's effective values as of this pass, including values contributed earlier in the same pass and not yet persisted.
7. A derived flow is a contribution: it wins or loses under Attribute Priority, "Null is a value" applies, Missing Input Behaviour (#1361) applies to absent `mv` inputs exactly as to absent `cs` inputs, and its values are recalled when its rule disconnects.
8. A derived value's change is an ordinary attribute change: it enters the object's changed-attribute set so export evaluation, drift detection and change history see it in the same pass.
9. A write outside synchronisation that changes a derived input (Synchronisation Rule deletion recall, Synchronised Deprovisioning, the stranded value sweep, a direct Metaverse Object edit, and #242's Collision Remediation) sets the derived-input mark on the hosting systems' Connected System Objects, so the hosting system's next synchronisation re-derives the dependants.
10. When a generated attribute (#242) is an input, the derived flows downstream of it are evaluated after the generated value resolves, level by level; a level containing generated attributes is resolved in one batch before the next level runs.

**Preview and tooling**

11. Sync Preview and Configuration Change Preview evaluate derived flows in the same order and show the derived results.
12. Validation failures (cycles) are reported identically through the portal, the REST API and PowerShell; the expression itself already round-trips through all three surfaces, so no new configuration fields are needed.

### Non-Functional Requirements

- The graph is built once per run and shared across pages; no per-object graph work.
- The derived pass adds no database round trips beyond those the ordinary flow already makes for the object; it is in-memory evaluation over values the pass already holds. Marking adds at most one bulk update per page, only when a derived input changed.
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
**Then** the person's HR Connected System Object is marked, and HR's next synchronisation (delta included) re-evaluates Email, provided it is joined and in scope for the HR rule. Synchronising AD before HR gives the new Email in the same cycle; the other way round, one cycle later.

### Scenario 3: Cycle rejected

**Given** rule 1 derives Display Name from `mv["Mail Nickname"]` and an administrator saves rule 2 deriving Mail Nickname from `mv["Display Name"]`
**Then** the save fails naming Display Name, Mail Nickname, rule 1 and rule 2.

### Scenario 4: Missing input

**Given** Email reads `mv["Account Name"]` with Missing Input Behaviour "contribute no value" and a Metaverse Object with no Account Name yet
**Then** Email contributes nothing and is resolved by priority; when Account Name later arrives, Email is derived.

### Scenario 5: Re-derived after a correction

**Given** Account Name is generated (#242) and corrected from `joe.bloggs` to `joe.bloggs1` during an export run
**When** the correction is written
**Then** the object's Connected System Object in the hosting system is marked, and Email and UPN are re-derived from `joe.bloggs1` in that system's next synchronisation.

### Scenario 6: Full versus delta

**Given** an object whose hosting Connected System Object is unchanged and unmarked
**Then** delta synchronisation does not process it, and a full synchronisation that does process it evaluates the derived flow to the same value, staging no Pending Export.

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

- None to build. #242 depends on this PRD for its Scenario 001 conversion and for evaluating generated base expressions that read `mv["..."]`.
- Reuses #892's per-object review flag, `ExpressionInputResolver`, Missing Input Behaviour (#1361) and the Example Data topological sort.

## Open Questions

1. ~~Should a warning be raised at save for non-pure expressions?~~ **Resolved 2026-09-28:** yes, a non-blocking warning at save on all three surfaces.
2. ~~Metaverse-owned attribute rules?~~ **Resolved 2026-09-28:** deferred, to be designed with internally managed Metaverse Objects (#614).
3. **Resolved 2026-09-28:** Reference-typed `mv` inputs and targets are rejected at save in this release, because references resolve after the derived pass; support is tracked by #1861.

## Acceptance Criteria

- [ ] `mv["..."]` works in import expressions with pickers in the editor (FR 1).
- [ ] Cycles and self-references across rules are rejected at save with both rules named (FR 2); dependents of a disabled or removed mapping are surfaced (FR 3).
- [ ] Evaluation is level-ordered and deterministic under permutation of configuration order (FR 4).
- [ ] Derived flows run only in the hosting system's synchronisation; cross-system input changes mark the hosting object and the hosting system's next delta re-evaluates it, in either sequencing order (FR 5, FR 5a, Scenario 2).
- [ ] Same-pass visibility, priority, "Null is a value", Missing Input Behaviour and recall behave as for any contribution (FR 6, 7).
- [ ] Derived changes reach export evaluation, drift detection and change history in the same pass (FR 8).
- [ ] Writes outside synchronisation mark the hosting objects and re-derive on the next hosting synchronisation (FR 9, Scenario 5); generated inputs are interleaved by level (FR 10).
- [ ] Previews show derived results (FR 11); validation parity across surfaces (FR 12).
- [ ] Docs and changelog updated; build and tests green; new behaviour tested red-first.

## Additional Context

- Inbound expression context: `SyncEngine.AttributeFlow.cs` (`ProcessExpressionMapping`, `metaverseAttributes: null`; the #1361 comment on why `mv` was unsupported).
- Topological sort precedent: `src/JIM.Models/ExampleData/ExampleDataObjectType.cs` (`GetTemplateAttributesInDependencyOrder`, `Visit` with cycle detection).
- Review flag: `SyncTaskProcessorBase.ProcessScopeReviewPendingMetaverseObjectsAsync` (#892), run by full and delta synchronisation.
- Per-run context precedent: `AttributePriorityContext` built once in `SyncTaskProcessorBase`.
