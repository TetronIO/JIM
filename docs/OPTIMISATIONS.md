# Discounted Optimisations

Records performance optimisations that were investigated and discounted — preserving the reasoning so future work doesn't re-investigate settled questions.

## Export Rule Evaluation (#417)

**Context:** During sync, `EvaluateExportRules` takes ~100ms per export rule per MVO in a devcontainer environment. The cost is dominated by `CreateAttributeValueChanges` which is called once per rule per MVO.

### Expression dependency tracking

**Idea:** Parse expressions to extract which `mv["..."]` attributes they reference, cache the dependency set, and skip expression evaluation when the `changedAttributes` don't overlap with the expression's dependencies.

**Why discounted:** Expressions are already compiled and cached in a static `ConcurrentDictionary<string, Lambda>`. Re-evaluation is just a `lambda.Invoke()` call — a few microseconds. The implementation would require parsing expression strings to extract attribute references (regex or AST walk), caching the dependency set per expression, cross-referencing against `changedAttributes`, and handling edge cases (functions with side effects, dynamic attribute names, `cs["..."]` references). Benchmarking showed `CreateAttributeValueChanges` averages 0.3ms per call on Micro — the dominant cost is DB operations and pending export creation/merge, not expression evaluation. Complexity-to-benefit ratio is poor. Could revisit if profiling on larger datasets shows expression evaluation as a significant chunk.

### Batch export evaluation

**Idea:** Evaluate all MVOs against a rule in one pass rather than one MVO against all rules. Would enable shared context and amortise per-rule setup cost.

**Why discounted:** The per-rule setup overhead (scoping check, CSO cache lookup, pending export lookup/merge, span creation) is lightweight in-memory work — ~5-10ms per rule per the issue breakdown. The expensive parts (DB saves, CSO creation, pending export persistence) wouldn't change regardless of evaluation order. Implementation would require major restructuring of the sync loop in `SyncTaskProcessorBase` which currently processes one MVO end-to-end (import → sync → export eval → next MVO). This streaming model keeps memory bounded by paging MVOs — batch-by-rule would need all MVOs (or all pending export state) in memory across rule iterations. There are also correctness risks: the per-MVO-then-per-rule ordering has implications for provisioning — rule 1's evaluation may create a CSO that rule 2 needs to find via cache lookup, and reordering could introduce subtle ordering bugs. Given the high risk to sync integrity, high implementation complexity, and low expected benefit, this is not worth pursuing.
