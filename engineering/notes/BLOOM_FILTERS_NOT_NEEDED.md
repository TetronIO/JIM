# Bloom Filters in JIM

## Status: Researched (2026-05-11). Not pursuing.

Background research into whether probabilistic set-membership structures (Bloom Filters) would improve performance of JIM's lookup-heavy hot paths, particularly during synchronisation. Conclusion: not needed. This note exists so we do not re-evaluate without new evidence.

## What a Bloom Filter is

A bit-array plus *k* hash functions. To add an item, hash it *k* ways and set those *k* bits. To test membership, re-hash and check those bits.

- "Definitely not in set" is reliable (no false negatives).
- "Maybe in set" can be wrong (false positives, controllable by sizing).
- Tiny memory footprint (a few bits per element).
- No deletes without variants (e.g. counting Bloom filters).

The classic win is skipping an expensive lookup when most lookups would miss. Widely used in LSM-tree compactions (RocksDB, Cassandra) and CDN/cache front-ends.

## Why JIM does not benefit

Three structural reasons:

1. **Postgres indexed lookups are microseconds.** Bloom filters earn their keep when the "real" lookup is expensive (cross-network, disk-bound, complex join). JIM's existence checks are predominantly equality on indexed columns.
2. **JIM already uses exact in-memory structures where it matters.** The sync engine builds `Dictionary<>` lookups per batch and uses `HashSet<Guid>` for reference resolution. A `Dictionary.ContainsKey` is O(1) and deterministic; swapping an exact structure for an approximate one buys nothing when memory is not the constraint.
3. **A warmed `IMemoryCache` for CSO external-ID to MVO Guid lookups already exists** (`ConnectedSystemServer.GetCsoWithCacheLookupAsync`, warmed at Worker startup in `Worker.cs`). That is the canonical "speed up does-this-CSO-exist" mechanism in JIM today.

## Scenario-by-scenario assessment

| Scenario | Current mechanism | Bloom filter useful? |
|---|---|---|
| CSO duplicate detection in import batch (`SyncImportTaskProcessor.cs` ~L2465) | 8 in-memory `Dictionary<>` keyed by ID attribute | No. Already O(1), and an exact answer is required. |
| CSO existence by external ID (`ConnectedSystemServer.cs` ~L2783) | Warmed `IMemoryCache` of external-ID to Guid | No. Cache gives an exact answer; a Bloom in front of it adds no value. |
| Reference resolution within batch (`SyncEngine.AttributeFlow.cs` ~L454) | `HashSet<Guid>.Contains` | No. Already exact and O(1). |
| Object matching / join (CSO to MVO) (`MetaverseRepository.cs` ~L1438) | Indexed EF Core `Any()` query per CSO per rule | Marginal. False positive still forces the DB query, so it only helps if most rules miss. A negative-result memo is simpler and exact. |
| Provisioning: does target CS object exist? | Falls through CSO cache | No. Cache covers it. |
| Web / API / PowerShell user-facing reads | Single indexed Postgres queries | No. Latency is dominated by request overhead. |

## Integrity constraint (why this matters more in JIM than elsewhere)

Synchronisation integrity is paramount; sync operations must never silently corrupt customer data. A Bloom filter is only safe in JIM if results are treated as:

- **"Definitely not present"**; still hit the database to write. The unique constraint is the final authority. Do not let Bloom filter results decide what to skip writing.
- **"Maybe present"**; always do the real lookup.

The dangerous failure mode is not the filter itself; it is staleness. Forgetting to add an item on insert, losing filter state across process restarts, or races between the bloom and the database all produce wrong answers. Every filter-like state JIM maintains today is short-lived (per-batch dictionaries) or refilled deterministically at startup (the CSO cache). Introducing a long-lived Bloom filter means owning its lifecycle, eviction, persistence, and restart semantics; a non-trivial liability for an unproven speedup.

## What to do instead, if sync performance becomes a concern

1. **Add per-batch Stopwatch timings** to the three big sync phases (import write, matching, Attribute Flow). Emit at end of each batch. No instrumentation currently exists, so any optimisation talk before this is speculative.
2. **Run a representative full sync** (1M+ CSOs, multiple matching rules) and look at where time actually goes.
3. **If object matching dominates**, add an exact in-memory negative-result memo keyed by `(rule id, attribute value)` scoped to the sync task. Cheaper, exact, and easier to reason about than a Bloom filter.
4. **Revisit Bloom filters only if** a future scenario emerges where an exact cache cannot fit in memory (e.g. tens of millions of CSOs on a memory-constrained Worker). That is the one genuine Bloom use case, and JIM does not have it today.

## Conclusion

Bloom filters solve a problem JIM does not measurably have. The hot paths that look Bloom-shaped are already handled by exact structures (Dictionary, HashSet, IMemoryCache) that are simpler, deterministic, and compatible with the synchronisation integrity invariant. Do not implement Bloom filters in JIM without first producing profiling evidence of a bottleneck they would uniquely address.
