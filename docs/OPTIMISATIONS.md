# Optimisation Log

Records optimisations that were considered, implemented, or discounted — preserving the reasoning so future work doesn't re-investigate settled questions.

## Export Rule Evaluation (#417)

**Context:** During sync, `EvaluateExportRules` takes ~100ms per export rule per MVO in a devcontainer environment. The cost is dominated by `CreateAttributeValueChanges` which is called once per rule per MVO.

### Implemented

#### MVO attribute dictionary reuse

**Problem:** `BuildAttributeDictionary` was a local variable inside `CreateAttributeValueChanges`, rebuilt every time the method was called. For an MVO hitting N export rules, the same dictionary was built N times.

**Fix:** Lifted the dictionary to `EvaluateExportRulesWithNoNetChangeDetectionAsync` (the per-MVO outer method) and threaded it through as a parameter. Built once on first use via lazy `??=` init, reused across all export rules.

**Impact:** ~17% reduction in `EvaluateExportRuleLoop` total time on Micro template (10 objects, 2 rules). Benefit compounds with more rules per MVO.

#### Double `.ToList()` materialisation in expression no-net-change path

**Problem:** The expression-based no-net-change path called `.ToList()` on the `ILookup` result, then passed the list to `IsCsoAttributeAlreadyCurrent` which called `.ToList()` again internally. The direct attribute flow path didn't have this issue.

**Fix:** Removed the outer `.ToList()` and pass `IEnumerable` directly, matching the direct-flow path pattern.

#### Hot-path Debug logging with expensive arguments

**Problem:** A `Log.Debug()` call in the per-attribute expression loop used `string.Join` + `.Select()` to format cache values. Even when Debug level is disabled, Serilog evaluates the method call arguments, causing allocations in a tight loop.

**Fix:** Removed the verbose log. The skip notification log below it provides sufficient diagnostic information.

### Discounted

#### Expression dependency tracking

**Idea:** Parse expressions to extract which `mv["..."]` attributes they reference, cache the dependency set, and skip expression evaluation when the `changedAttributes` don't overlap with the expression's dependencies.

**Why discounted:** Expressions are already compiled and cached in a static `ConcurrentDictionary<string, Lambda>`. Re-evaluation is just a `lambda.Invoke()` call — a few microseconds. The implementation would require parsing expression strings to extract attribute references (regex or AST walk), caching the dependency set per expression, cross-referencing against `changedAttributes`, and handling edge cases (functions with side effects, dynamic attribute names, `cs["..."]` references). Benchmarking showed `CreateAttributeValueChanges` averages 0.3ms per call on Micro — the dominant cost is DB operations and pending export creation/merge, not expression evaluation. Complexity-to-benefit ratio is poor. Could revisit if profiling on larger datasets shows expression evaluation as a significant chunk.

#### Batch export evaluation

**Idea:** Evaluate all MVOs against a rule in one pass rather than one MVO against all rules. Would enable shared context and amortise per-rule setup cost.

**Why discounted (for now):** Requires significant restructuring of the sync loop which currently processes one MVO at a time through the full pipeline (import → sync → export evaluation). The current architecture supports streaming/paging of MVOs which is important for memory management at scale. Would need careful design to avoid loading all MVOs into memory simultaneously. Parked as a future consideration if per-rule overhead becomes dominant at larger scales.

## AD Schema Discovery Batching (#433)

### Implemented

#### Bulk pre-fetch of classSchema and attributeSchema entries

**Problem:** `GetActiveDirectorySchemaAsync()` issued one LDAP `SearchRequest` per attribute per class (via `GetSchemaEntry()`). A typical AD has ~1,500 attributes across ~50 structural classes, producing ~1,800+ LDAP round-trips.

**Fix:** Added two bulk LDAP queries at the start of schema discovery — one for all `classSchema` entries and one for all `attributeSchema` entries — into case-insensitive `Dictionary<string, SearchResultEntry>` lookups. Replaced all 4 `GetSchemaEntry()` call sites with dictionary `TryGetValue`. The broader `classSchema` query (no category/hiding filters) ensures abstract parents like `top` are included for hierarchy walks.

**Impact:** Reduced LDAP round-trips from ~1,800+ to 3 total queries. Measured at 226ms for 9 object types (270 classSchema + 1,514 attributeSchema entries) against Samba AD. The RFC 4512 code path in the same file already used this pattern — this brought the AD path into alignment.
