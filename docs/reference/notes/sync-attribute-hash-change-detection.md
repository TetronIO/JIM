# Sync Performance: Attribute Hash Change Detection

> Reserved alternative approach for sync performance optimisation. Implement if the watermark-based approach doesn't deliver sufficient throughput improvement.

## Problem

Full sync loads complete attribute values for every CSO to run attribute flow comparison, even when the vast majority of CSOs haven't changed since the last sync. At 100K objects this is the dominant performance bottleneck.

## Current Approach: Watermark Comparison

The current implementation uses `CSO.LastUpdated` compared against `ConnectedSystem.LastSyncCompletedAt` to identify unchanged CSOs and skip their attribute loading. This is simple and requires no schema changes.

**Limitations:**
- Requires a prior successful sync to establish the watermark
- First sync after adding new sync rules must process everything (watermark predates the rules)
- If a sync fails partway through, the watermark isn't updated — the next sync re-processes everything
- Doesn't detect external database modifications (direct SQL changes to attribute values)

## Alternative: Attribute Hash

### Design

Add two columns to `ConnectedSystemObject`:
- `AttributeHash` (string, nullable) — SHA-256 hash of all current attribute values, computed at import time
- `LastSyncedAttributeHash` (string, nullable) — the `AttributeHash` value as of the last completed sync

### Import-Time Computation

After importing/updating a CSO's attribute values, compute a deterministic hash:

```csharp
// Pseudocode
var hashInput = cso.AttributeValues
    .OrderBy(av => av.AttributeId)
    .ThenBy(av => av.StringValue ?? av.IntValue?.ToString() ?? ...)
    .Select(av => $"{av.AttributeId}:{av.StringValue ?? av.IntValue?.ToString() ?? ...}")
    .Aggregate((a, b) => $"{a}|{b}");
cso.AttributeHash = SHA256(hashInput);
```

Key considerations:
- Ordering must be deterministic (by AttributeId, then by value for multi-valued attributes)
- All value types must be represented consistently (nulls, dates in UTC format, etc.)
- Reference values should use the referenced CSO's ID, not a mutable display name
- Binary values should be hashed separately to avoid huge string concatenation

### Sync-Time Comparison

During sync page loading:
1. Load CSO scalars including `AttributeHash` and `LastSyncedAttributeHash` (no AttributeValues)
2. Compare: if `AttributeHash == LastSyncedAttributeHash` and CSO is Normal + joined, skip full processing
3. At sync completion, update `LastSyncedAttributeHash = AttributeHash` for all processed CSOs

### Advantages Over Watermark

- Works without a prior sync watermark (first sync computes hashes during import)
- Detects the exact CSOs that changed, not just "something changed after timestamp X"
- Resilient to failed syncs — the hash comparison is always valid
- Can detect external attribute modifications (if they don't update the hash, the mismatch triggers processing)

### Disadvantages

- Requires a database migration (two new columns)
- Import overhead: hash computation per CSO on every import (~negligible for SHA-256)
- Storage overhead: ~64 bytes per CSO for two hash columns (~6.4 MB at 100K objects)
- Complexity: hash computation must be deterministic and handle all attribute types correctly
- Migration path: existing CSOs have no hash — first import after migration must populate them

### Implementation Effort

- Migration: ~1 hour
- Hash computation in import processor: ~2 hours
- Sync-time comparison logic: ~1 hour (similar to current watermark approach)
- Testing: ~2 hours
- Total: ~1 day

### When to Consider

Implement this approach if:
- The watermark approach delivers <50% improvement in obj/s for repeat full syncs
- Failed syncs are common and the watermark staleness causes frequent re-processing
- Deployments require accurate change detection without relying on timestamp ordering
