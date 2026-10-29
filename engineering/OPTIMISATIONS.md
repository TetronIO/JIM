# Discounted Optimisations

Records performance optimisations that were investigated and discounted, preserving the reasoning so future work doesn't re-investigate settled questions.

## Export Rule Evaluation (#417)

**Context:** During sync, `EvaluateExportRules` takes ~100ms per export rule per MVO in a devcontainer environment. The cost is dominated by `CreateAttributeValueChanges` which is called once per rule per MVO.

### Expression dependency tracking

**Idea:** Parse expressions to extract which `mv["..."]` attributes they reference, cache the dependency set, and skip expression evaluation when the `changedAttributes` don't overlap with the expression's dependencies.

**Why discounted:** Expressions are already compiled and cached in a static `ConcurrentDictionary<string, Lambda>`. Re-evaluation is just a `lambda.Invoke()` call, a few microseconds. The implementation would require parsing expression strings to extract attribute references (regex or AST walk), caching the dependency set per expression, cross-referencing against `changedAttributes`, and handling edge cases (functions with side effects, dynamic attribute names, `cs["..."]` references). Benchmarking showed `CreateAttributeValueChanges` averages 0.3ms per call on Micro; the dominant cost is DB operations and Pending Export creation/merge, not expression evaluation. Complexity-to-benefit ratio is poor. Could revisit if profiling on larger datasets shows expression evaluation as a significant chunk.

## Projection-Based Reference Loading for GetMetaverseObjectAsync (#383 subtask 3.2)

**Context:** Issue #383 proposed replacing full EF Core entity tracking with lightweight projections for reference attribute loading during sync, targeting 50K+ member groups. The theory was that `GetMetaverseObjectAsync` loads deep Include chains (`ReferenceValue → AttributeValues → DisplayName`) that materialise ~150K tracked entities for a 50K-member group.

**Why discounted:** Code path tracing revealed the sync pipeline **already bypasses `GetMetaverseObjectAsync`** entirely:
- CSO loading uses `PopulateReferenceValuesAsync`: direct SQL projections with `ReferencedCsoRow` record, no entity tracking
- MVO loading during sync uses `LoadMetaverseObjectsForCsosAsync`: intentionally shallow (no `ReferenceValue.AttributeValues`) to avoid tracking conflicts
- Worker Attribute Flow uses `ResolvedReferenceMetaverseObjectId` (`[NotMapped]` property populated via direct SQL), not deep reference navigation
- The worker project has zero calls to `GetMetaverseObjectAsync`

The deep reference Include chains in `GetMetaverseObjectAsync` only affect UI pages and the API endpoint, which are single-object lookups (not hot loops). The UI already uses `GetMetaverseObjectDetailAsync(CappedMva)` with capped MVA loading. The one API endpoint (`GET /api/v1/metaverse/objects/{id}`) could be migrated to CappedMva, but this is a single-request path with no measurable performance impact.

**Conclusion:** The projection-based optimisation that #383 described was already implemented in prior work (Phases 1-2 of #320). The remaining "un-projected" paths are UI/API single-object lookups that don't contribute to large group performance issues. No further work needed.

**Update (#2498, #2579):** the conclusion holds for the sync pipeline, but not for the single-object detail reads at very large group sizes. On a group of 594,000 members the Connected System Object and Metaverse Object detail reads' reference Include (`ReferenceValue -> AttributeValues`) let the planner misestimate the object's few non-member values and drive the referenced objects' values from the whole attribute value table, so both pages took 5 to 18 seconds. The detail reads now load values without reference includes and read the referenced objects by an explicit id list (a Metaverse Object named from `CachedDisplayName`, a Connected System Object with only its naming and identifier values): about 170 ms and 470 ms on the same group. Change records' values are likewise read a window at a time rather than all at once (#2498). The lesson for this entry: "single-object lookup" is not the same as "cheap" when one object holds hundreds of thousands of values; measure it at that scale before discounting it.

## Materialised Display Names on ConnectedSystemObject (#383 subtask 3.3)

**Context:** Issue #383 proposed storing a denormalised `DisplayName` column on `ConnectedSystemObject`, maintained during import, to eliminate reference traversal when displaying CSO names.

**Why discounted:** Phase 2 of #320 already solved the display problem with capped MVA loading and server-side pagination. The UI no longer needs to traverse reference chains to resolve display names for large groups; it loads capped values (10 per attribute) with reference display names included. Adding a denormalised column would introduce write-time maintenance overhead (keeping it in sync during every import) and a new consistency risk for a problem that no longer exists.

### Batch export evaluation

**Idea:** Evaluate all MVOs against a rule in one pass rather than one MVO against all rules. Would enable shared context and amortise per-rule setup cost.

**Why discounted:** The per-rule setup overhead (scoping check, CSO cache lookup, Pending Export lookup/merge, span creation) is lightweight in-memory work, ~5-10ms per rule per the issue breakdown. The expensive parts (DB saves, CSO creation, Pending Export persistence) wouldn't change regardless of evaluation order. Implementation would require major restructuring of the sync loop in `SyncTaskProcessorBase` which currently processes one MVO end-to-end (import → sync → export eval → next MVO). This streaming model keeps memory bounded by paging MVOs; batch-by-rule would need all MVOs (or all Pending Export state) in memory across rule iterations. There are also correctness risks: the per-MVO-then-per-rule ordering has implications for provisioning: rule 1's evaluation may create a CSO that rule 2 needs to find via cache lookup, and reordering could introduce subtle ordering bugs. Given the high risk to sync integrity, high implementation complexity, and low expected benefit, this is not worth pursuing.

## Dependency-Ordered Synchronisation Paging (#2492)

**Context:** Synchronisation now pages Connected System Objects one Object Type at a time in dependency order, so references between types (group members) resolve in the page loop, and the cross-page reference step is left only with references within one type (managers, nested groups). Two further optimisations were tried while making that step and the group pages fast, and discounted. The design and measurements are in [`plans/done/SYNC_DEPENDENCY_ORDERED_PAGING.md`](plans/done/SYNC_DEPENDENCY_ORDERED_PAGING.md).

### Raw-SQL reference-only reload for the cross-page step

**Idea:** Reload only the mapped reference attribute values (and their targets' Metaverse Object ids) with raw SQL into untracked objects, instead of reloading each deferred Connected System Object through a tracked EF Include.

**Why discounted:** Measuring the step on Scenario 032 at Medium showed its cost was per-object round trips in export re-evaluation, not the reload. Once those were removed (the Pending Export batch scope and the per-page snapshot lookup memo; see [`CACHING_STRATEGY.md`](CACHING_STRATEGY.md)), the step went from 14.4 ms to 3.9 ms per reference value, of which the reload was about 0.4 s of the remaining 2.7 s. A reference-only reload could save at most about 0.3 s, and it would be wrong as well as small: a reference attribute's flow expression may read the object's other attributes, which a reference-only reload would silently withhold.

### Preparing the join lookup once per connection

**Idea:** A Full Synchronisation runs the Object Matching join lookup once per object, and planning it cost about six times running it (0.37 ms planned against 0.06 ms prepared, measured with pgbench), so prepare the lookup and its count on the connection and reuse them.

**Why discounted:** A prepared statement caches a plan made on the first page, while the Metaverse tables are nearly empty. On Scenario 008 Large the join cost then climbed page by page from 0.7 ms to 10 ms per object until an automatic analyse invalidated the plan; at scale, where an analyse waits for a tenth of a large table to change, that would hold for most of a run. The gain was about 0.15 ms per lookup measured from .NET, not worth that risk, so the change was reverted (#2577). What fixed the climbing group pages instead was checking a matched value's Metaverse Object Type through a correlated subquery on the object's own row, which keeps the plan on the attribute value index whatever the statistics say.
