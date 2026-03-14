# Drift Detection Spurious Member Removal

## Status: ✅ RESOLVED

All seven fix layers applied and validated. Scenario 8 integration test (Medium template: ~1,000 users, ~118 groups, ~200 members per group) passes all six test steps: InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup.

## Reported Issue

Integration test Scenario 8 (Cross-Domain Entitlement Synchronisation) fails at the
"Medium" template size with group member count mismatches. After completing an initial
forward sync and then a delta forward sync with membership changes, the Target AD
groups have fewer members than expected.

**Observed error (Medium template, ~1,000 users, ~118 groups):**

```
ForwardSync failed: group 'Project-GlobalApollo' has 185 members in Target
but expected 201 from Source (deficit: 16).
This indicates the confirming sync incorrectly removed 16 members via drift correction.
```

The test passes at Nano/Micro/Small sizes but fails at Medium and above, where groups
have significantly more members.

---

## Sync Topology

Two LDAP (Active Directory) connected systems in a cross-domain entitlement sync:

- **Source CS** ("Quantum Dynamics APAC") - Authoritative source of users and groups
- **Target CS** ("Quantum Dynamics EMEA") - Receives users and groups from Source

### Sync Rules (6 total)

| Rule                    | Direction | Purpose                                                |
|-------------------------|-----------|--------------------------------------------------------|
| APAC AD Import Users    | Import    | Source user --> MVO User (projects to metaverse)       |
| EMEA AD Export Users    | Export    | MVO User --> Target user (provisions to Target)        |
| EMEA AD Import Users    | Import    | Target user --> MVO User (confirming import, join only)|
| APAC AD Import Groups   | Import    | Source group --> MVO Group (projects, incl. `member` --> `Static Members`) |
| EMEA AD Export Groups   | Export    | MVO Group --> Target group (provisions, incl. `Static Members` --> `member`) |
| EMEA AD Import Groups   | Import    | Target group --> MVO Group (confirming import, join only) |

Key attributes for groups:
- **Source import**: `member` (multi-valued reference) flows to MVO `Static Members`
- **Target export**: MVO `Static Members` flows to `member` (multi-valued reference)
- **Target import**: No attribute flow mappings (join only, used for confirming imports)

---

## Expected Sync Steps and Outcomes

### Test 1: InitialSync (Full Forward Sync)

For a Medium template with ~1,000 users and ~118 groups:

| Step | Run Profile | Expected Outcome |
|------|-------------|------------------|
| 1 | Source CS - Full Import | ~1,000 user CSOs and ~118 group CSOs created with all attributes including `member` references resolved to user CSOs. |
| 2 | Target CS - Full Import | Target AD is empty (no pre-existing objects). No CSOs created. |
| 3 | Source CS - Full Synchronisation | ~1,000 user MVOs and ~118 group MVOs projected to metaverse. Group MVO `Static Members` populated with references to member MVOs. Export evaluation creates ~1,118 Pending Exports for Target CS, plus ~1,118 provisioning Target CSOs (already joined to their MVOs via `MetaverseObjectId`). |
| 4 | Target CS - Full Synchronisation | No imported Target CSOs exist to join (Target import found nothing in step 2). Provisioning CSOs from step 3 already exist and are already joined. No change. |
| 5 | Target CS - Export | ~1,118 Pending Exports executed. ~1,000 users and ~118 groups created in Target AD with all attributes including group memberships. Pending Exports marked as exported (awaiting confirmation). Provisioning CSOs promoted from `PendingProvisioning` to `Normal` status. |
| 6 | Target CS - Full Confirming Import | Re-imports all ~1,118 objects from Target AD. Target CSOs updated with imported attributes (attribute values from AD now populate the CSO). Pending Exports confirmed and deleted. |
| 7 | Target CS - Full Confirming Sync | Target CSOs are already joined to MVOs (since step 3). **Drift detection runs on each Target CSO**, comparing CSO attribute values against MVO expected values. No drift should be detected - all values should match. |

**Validation**: Every group in Target AD should have the same member count as the corresponding Source AD group.

### Test 2: ForwardSync (Delta Forward Sync with Membership Changes)

After InitialSync is complete, add 2 users and remove 1 user from a test group in Source AD, then:

| Step | Run Profile | Expected Outcome |
|------|-------------|------------------|
| 1 | Source CS - Delta Import | Changed group CSO re-imported with updated `member` attribute. |
| 2 | Source CS - Delta Synchronisation | MVO `Static Members` updated (+2 adds, -1 remove). Export evaluation creates Pending Exports for Target CS with Add/Remove changes for the `member` attribute. |
| 3 | Target CS - Export | Pending Exports executed. LDAP modify updates group membership in Target AD. |
| 4 | Target CS - Delta Confirming Import | Re-imports changed group from Target AD. CSO updated, Pending Export confirmed and deleted. |
| 5 | Target CS - Delta Confirming Sync | Target CSO is already joined to MVO (since InitialSync). **Drift detection runs**, comparing Target CSO `member` values against MVO `Static Members`. No drift should be detected. |

**Validation**: Test group in Target AD should have `initialCount + 2 - 1` members, matching Source.

---

## Root Cause Analysis

### Initial Hypothesis (Partially Correct)

The initial analysis identified drift detection as the source of spurious member removals.
This was correct in that drift detection *does* create spurious REMOVE exports when
`ReferenceValue` navigations are null. However, investigation of the integration test
failure revealed that **the primary data corruption occurs earlier, during the confirming
import**, not during drift detection.

### Actual Failure Point

The failure occurs at **Step 6 of InitialSync** (Target CS - Full Confirming Import) —
not Step 7 as initially hypothesised. The data corruption persists into all subsequent
steps.

### The Drift Detection Mechanism

During a confirming sync, `DriftDetectionService.EvaluateDrift()` runs on each Target
CSO. For each export rule with `EnforceState = true` (the default), it:

1. Computes **expected values** from the MVO (what the export rule says the CSO should have)
2. Computes **actual values** from the CSO (what the CSO currently has)
3. If they differ, creates **corrective Pending Exports** (Add/Remove changes)

For multi-valued reference attributes like `member`/`Static Members`:

- **Expected** = Set of MVO IDs from `mvo.AttributeValues` where `ReferenceValue.Id` (the referenced MVO's ID)
- **Actual** = Set of MVO IDs from `cso.AttributeValues` where `ReferenceValue.MetaverseObjectId` (the MVO ID that the referenced CSO is joined to)

### The Primary Bug: Confirming Import Member Reference Corruption

During the confirming import (Step 6), `SyncImportTaskProcessor` loads the existing Target
group CSO via `GetConnectedSystemObjectByAttributeAsync` to compare its current member
references against the freshly imported `member` DNs from Active Directory.

The matching logic in `ImportRefMatchesCsoValue()` (SyncImportTaskProcessor.cs line 1371)
checks each imported DN against existing CSO attribute values:

```csharp
static bool ImportRefMatchesCsoValue(string importRef, ConnectedSystemObjectAttributeValue av)
{
    // Check unresolved reference (DN string match)
    if (av.UnresolvedReferenceValue != null &&
        av.UnresolvedReferenceValue.Equals(importRef, StringComparison.Ordinal))
        return true;

    // Check resolved reference (match against referenced CSO's external ID)
    if (av.ReferenceValue != null)
    {
        var refExternalId = av.ReferenceValue.SecondaryExternalIdAttributeValue?.StringValue
                         ?? av.ReferenceValue.ExternalIdAttributeValue?.StringValue;
        if (refExternalId != null &&
            refExternalId.Equals(importRef, StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}
```

When `AsSplitQuery()` drops the `ReferenceValue` navigation (or its nested
`SecondaryExternalIdAttributeValue` / `ExternalIdAttributeValue`), this function returns
`false` for valid resolved members. The import logic then:

1. Marks unmatched resolved refs as "missing" and **removes them** (lines 1394-1398)
2. Adds ALL imported DNs as new **unresolved** references (lines 1402-1411)
3. `ResolveReferencesAsync` attempts to re-resolve the unresolved refs

**Result**: For a group with 201 members, if AsSplitQuery drops navigations on many refs,
the import removes those resolved refs, adds them back as unresolved, and reference
resolution only succeeds for a subset. In the observed failure, only 86 of 201 refs were
re-resolved — the remaining 115 were either left unresolved or failed resolution entirely.

**Database evidence from the failed test:**

| Object | Member Ref Count | Status |
|--------|-----------------|--------|
| Source CSO (APAC) | 201 | All resolved (correct) |
| MVO (Static Members) | 201 | All resolved (correct) |
| Target CSO (EMEA) | 86 | All resolved, 115 missing |

Drift detection then correctly observes Expected=201, Actual=86 and stages 115 corrective
DELETE exports — it is reporting real drift that was introduced by the confirming import.

### The Secondary Bug: Drift Detection Navigation Failures

The **actual value** for reference attributes uses `cso.AttributeValue.ReferenceValue.MetaverseObjectId`
to translate CSO references into MVO IDs for comparison. However, some CSO references have
`MetaverseObjectId == null`.

When `MetaverseObjectId` is null, `GetTypedValueFromCsoAttributeValue()` returns null,
and the null is filtered out by the `if (value != null)` guard in `GetActualValue()`.

**Result**: The actual set is silently smaller than the real set. For a group with 200
members where 16 references have null `MetaverseObjectId`, the actual set has 184 members
while the expected set has 200. Drift detection sees 16 "missing" members and creates 16
REMOVE corrective Pending Exports.

These spurious Pending Exports are then executed during the next export cycle (ForwardSync
step 3), deleting members from the Target AD group.

### Root Cause: EF Core `AsSplitQuery()` Materialisation Bug

**`MetaverseObjectId` should never be null on these referenced CSOs.** Every Target user
CSO was created as a provisioning CSO (joined to its MVO at creation time via
`MetaverseObjectId = mvo.Id`) and subsequently confirmed via import. The
`MetaverseObjectId` column is populated in the database.

The problem is that **when EF Core loads the group CSO with its member reference chain,
some referenced CSOs are either not materialised at all or have their `ReferenceValue`
navigation left null**, despite the data existing in the database. This means either:

- `av.ReferenceValue` is null (the navigation wasn't populated), OR
- `av.ReferenceValue` is loaded but `MetaverseObjectId` is null on the entity

Since `MetaverseObjectId` is a scalar FK property that EF always loads with the entity
row, the more likely scenario is that `ReferenceValue` itself is null — meaning the
**referenced CSO entity was not materialised** into the navigation property.

**Both the import and sync code paths** load CSOs using `AsSplitQuery()` with deep Include
chains in `ConnectedSystemRepository`.

**Confirming import** uses `GetConnectedSystemObjectByAttributeAsync()` (line 994):

```csharp
var allMatches = await Repository.Database.ConnectedSystemObjects
    .AsSplitQuery()
    .Include(cso => cso.Type).ThenInclude(t => t.Attributes)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)
    .Include(cso => cso.AttributeValues).ThenInclude(av => av.ReferenceValue)
        .ThenInclude(refCso => refCso!.AttributeValues).ThenInclude(refAv => refAv.Attribute)
    .Where(...)
    .ToListAsync();
```

**Confirming sync** uses `GetConnectedSystemObjectsAsync()` (line 606):

```csharp
var query = Repository.Database.ConnectedSystemObjects
    .AsSplitQuery()
    .Include(cso => cso.AttributeValues)
        .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.MetaverseObject)
    // ... other includes ...
```

With `AsSplitQuery()`, EF Core executes separate SQL queries for each Include branch and
merges results in memory. There is a **documented EF Core bug**
([dotnet/efcore#33826](https://github.com/dotnet/efcore/issues/33826)) where:

> When using `AsSplitQuery()` without serializable/snapshot isolation transactions,
> concurrent inserts into the same tables can cause Entity Framework Core to fail to
> populate child entities. **EF retrieves the correct data from the database but discards
> it during materialisation.**

The conditions that trigger this are:

1. `AsSplitQuery()` is enabled (confirmed — line 607)
2. No explicit transaction wraps the query (confirmed — no `BeginTransaction` in the
   page load methods)
3. Concurrent writes occur between split query executions (likely — the Worker updates
   Activity progress, persists MVOs, and flushes Pending Exports between pages, all of
   which write to shared tables)

During a confirming sync:

1. Split query 1 loads the group CSO and its `AttributeValues` rows
2. Between queries, the same or another process writes to `ConnectedSystemObjects` or
   related tables (e.g., Activity updates, MVO persistence from earlier pages)
3. Split query 2 loads the `ReferenceValue` CSO entities for the attribute values
4. **EF discards some of the retrieved CSO entities** due to the concurrent write
   changing row ordering/counts, leaving `ReferenceValue` null on those attribute values

This explains the scale correlation: more members = more split query results = higher
probability that a concurrent write occurs between split queries.

### Why It Only Fails at Scale

At small sizes (Nano/Micro/Small), groups have few members (~3-30). The split queries
execute quickly, narrowing the window for concurrent writes. At Medium scale (~200 members
per group), the split query for `ReferenceValue` returns hundreds of rows, widening the
window for the race condition.

### The Query Chains (for reference)

**Confirming Import Path** (primary corruption source):

1. `SyncImportTaskProcessor.ProcessConnectedSystemObjectAsync()` — processes each imported
   CSO from the connector
2. For reference attributes, loads the existing Target CSO via
   `ConnectedSystemRepository.GetConnectedSystemObjectByAttributeAsync()` — line 994:
   executes Include chain with `AsSplitQuery()`
3. `ImportRefMatchesCsoValue()` — line 1371: attempts to match imported DN strings against
   existing CSO member references using `av.ReferenceValue.SecondaryExternalIdAttributeValue`
4. When AsSplitQuery drops navigations, matches fail, existing resolved refs are removed,
   and replacement unresolved refs are added
5. `ResolveReferencesAsync()` — line 1595: attempts to re-resolve the unresolved refs but
   only succeeds for a subset

**Confirming Sync Path** (secondary — reports damage done by import):

1. `SyncFullSyncTaskProcessor.PerformFullSyncAsync()` — line 127: calls
   `GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize)`
2. `ConnectedSystemRepository.GetConnectedSystemObjectsAsync()` — line 606: executes the
   Include chain with `AsSplitQuery()`
3. `SyncTaskProcessorBase.ProcessConnectedSystemObjectAsync()` — line 145: receives the
   loaded CSO
4. `SyncTaskProcessorBase.EvaluateDriftAndEnforceState()` — line 841: passes the same CSO
   to `DriftDetectionService.EvaluateDrift()`
5. `DriftDetectionService.GetActualValue()` — accesses `av.ReferenceValue?.MetaverseObjectId`

No re-query or reload occurs between steps 2 and 5. The CSO used for drift detection is
the same entity graph loaded by the original split query.

---

### The Tertiary Bug: MVO AttributeValues Collection Truncation

Investigation of subsequent integration test failures revealed a **third manifestation** of
the AsSplitQuery bug: entire `MetaverseObjectAttributeValue` rows are dropped from the
`MVO.AttributeValues` collection. Unlike the CSO-side issues (Layers 1-3) where navigation
properties are null but the parent entity exists, here the **parent entity itself is missing**.

This manifests during drift detection: when computing the "expected" set of member references
from `mvo.AttributeValues`, drift detection only sees a subset of the actual members in the
metaverse. For example, an MVO with 101 member attribute values might only have 48 loaded
by EF Core.

**Evidence from integration test logs:**

```
19:33:49 EvaluateDrift: Drift detected on CSO b5103bae... attribute member.
  Expected: '[...] (48 values)', Actual: '[...] (100 values)'
  → Corrective export: DELETE 52 members

19:35:00 EvaluateDrift: Drift detected on CSO b5103bae... attribute member.
  Expected: '[...] (101 values)', Actual: '[...] (50 values)'
  → Corrective export: ADD 51 members
```

The Expected count went from 48 to 101 between runs — the same MVO, reloaded by a different
query, now shows the correct count. The first run's truncated Expected set caused drift
detection to delete 52 valid members, leaving only 50 in the target.

**Why Layers 1-3 don't help:**
- Layer 1 (CSO repair): Fixes null navigations on *loaded* CSO attribute values — doesn't
  address MVO attribute values, and can't fix entities that were never loaded
- Layer 2 (scalar FK): Uses `av.ReferenceValueId` instead of `av.ReferenceValue?.Id` for
  MVOs — works when the attribute value entity is loaded but its navigation is null, but
  can't help when the entire `MetaverseObjectAttributeValue` row is absent from the collection
- Layer 3 (import dictionary): Only fixes the import path, not drift detection

The MVO attribute values are loaded as part of the CSO query's Include chain (line 616-621):
```csharp
.Include(cso => cso.MetaverseObject)
    .ThenInclude(mvo => mvo!.AttributeValues)
    .ThenInclude(av => av.ReferenceValue)
```

This is 3 levels deep through AsSplitQuery — prime territory for the materialisation bug.

---

## Fix Applied

A seven-layer fix addressing the confirming sync, confirming import, sync projection,
export evaluation, and MVO persistence paths.

### Layer 1: Repository-Level SQL Repair (primary)

`ConnectedSystemRepository.RepairReferenceValueMaterialisationAsync()` runs a direct SQL
query immediately after each EF page load to get the definitive `MetaverseObjectId` for
every reference attribute value in the page:

```sql
SELECT av."Id" AS "AttributeValueId", ref_cso."MetaverseObjectId"
FROM "ConnectedSystemObjectAttributeValues" av
JOIN "ConnectedSystemObjects" ref_cso ON av."ReferenceValueId" = ref_cso."Id"
WHERE av."ConnectedSystemObjectId" = ANY({0})
  AND av."ReferenceValueId" IS NOT NULL
```

For any attribute value where EF failed to materialise the `ReferenceValue` navigation,
the repair creates a detached stub `ConnectedSystemObject` with the correct `Id` and
`MetaverseObjectId` and assigns it to `av.ReferenceValue`. This stub is not tracked by
the DbContext — it exists only to satisfy the in-memory navigation chain that drift
detection (and other sync processors) depend on.

Called from all three page-load methods:
- `GetConnectedSystemObjectsAsync` (full sync)
- `GetConnectedSystemObjectsModifiedSinceAsync` (delta sync)
- `GetConnectedSystemObjectsForReferenceResolutionAsync` (cross-page reference resolution)

Logs a Warning when any repairs are needed, providing diagnostic visibility that the
AsSplitQuery bug was triggered.

### Layer 2: DriftDetectionService Defence-in-Depth (secondary)

- `GetTypedValueFromMvoAttributeValue`: Prefers the scalar FK `av.ReferenceValueId` over
  the navigation `av.ReferenceValue?.Id` (with fallback) for the "expected" set.
- `GetTypedValueFromCsoAttributeValue`: Calls `GetCsoReferenceMetaverseObjectId()` which
  logs a Warning if `ReferenceValueId` is set but `ReferenceValue` is null (indicating
  the repository repair did not cover this value).
- `BuildAttributeDictionary`: Same scalar FK preference for expression evaluation.

### Why Not the Other Options

- **Option B (Snapshot Transaction)**: Adds transaction overhead and doesn't address the
  underlying performance issues of deep Include chains. A potential future improvement.
- **Option C (AsSingleQuery)**: Would cause cartesian explosion for groups with many
  members and attributes (200 members x 10 attributes = 2,000 rows per group).
- **Full Option A (replace entire Include chain with SQL)**: Planned for GitHub issue #338
  Phase 4. The repair approach is a targeted fix that can be applied now without the larger
  refactoring effort, while remaining compatible with a future full SQL migration.

### Layer 3: Import Reference Matching Fallback (primary — fixes the root cause)

When `SyncImportTaskProcessor.UpdateConnectedSystemObjectFromImportObject()` processes
reference attributes during a confirming import, it compares imported DN strings against
existing CSO member references via `ImportRefMatchesCsoValue()`. This function relies on
the `ReferenceValue` navigation to get the referenced CSO's external ID for matching.

The fix pre-loads a `Dictionary<Guid, string>` mapping each `ReferenceValueId` to its
external ID string via a single direct SQL query
(`ConnectedSystemRepository.GetReferenceExternalIdsAsync`):

```sql
SELECT av."ReferenceValueId",
       COALESCE(sec_av."StringValue", pri_av."StringValue") AS "ExternalId"
FROM "ConnectedSystemObjectAttributeValues" av
JOIN "ConnectedSystemObjects" ref_cso ON av."ReferenceValueId" = ref_cso."Id"
LEFT JOIN "ConnectedSystemObjectAttributeValues" sec_av
    ON sec_av."ConnectedSystemObjectId" = ref_cso."Id"
   AND sec_av."AttributeId" = ref_cso."SecondaryExternalIdAttributeId"
LEFT JOIN "ConnectedSystemObjectAttributeValues" pri_av
    ON pri_av."ConnectedSystemObjectId" = ref_cso."Id"
   AND pri_av."AttributeId" = ref_cso."ExternalIdAttributeId"
WHERE av."ConnectedSystemObjectId" = @csoId
  AND av."ReferenceValueId" IS NOT NULL
```

The dictionary is passed to `ImportRefMatchesCsoValue` as a fallback. When
`av.ReferenceValue` is null (AsSplitQuery materialisation failure), the function checks
`av.ReferenceValueId` against the dictionary to find the referenced CSO's external ID:

```csharp
// Fallback: when AsSplitQuery() dropped the ReferenceValue navigation
if (av.ReferenceValueId.HasValue &&
    refExtIdLookup.TryGetValue(av.ReferenceValueId.Value, out var fallbackExternalId) &&
    fallbackExternalId.Equals(importRef, StringComparison.OrdinalIgnoreCase))
    return true;
```

**Dictionary lifecycle:**
- Created once per CSO, one SQL query per CSO being updated during import
- Used as a read-only fallback within `ImportRefMatchesCsoValue`
- Discarded when `UpdateConnectedSystemObjectFromImportObject` returns (method-scoped)
- No interaction with `IMemoryCache` or the CSO lookup cache

A warning is logged when null navigations are detected, providing diagnostic visibility.

**N-tier compliance:** The SQL query is in
`ConnectedSystemRepository.GetReferenceExternalIdsAsync()`, exposed via
`IConnectedSystemRepository` and `ConnectedSystemServer`, called from the import processor
via `_jim.ConnectedSystems.GetReferenceExternalIdsAsync()`.

### Layer 4: MVO Attribute Value Repair (fixes the expected-set truncation)

`ConnectedSystemRepository.RepairMvoAttributeValueMaterialisationAsync()` runs after each
EF page load (alongside the Layer 1 CSO repair) to detect and fill in MVO reference
attribute values that EF Core's AsSplitQuery() failed to materialise entirely.

Unlike Layer 1 which fixes null navigations on *loaded* entities, this method detects when
entire `MetaverseObjectAttributeValue` rows are missing from the `mvo.AttributeValues`
collection. It queries the database for the definitive set of reference-type attribute
values per MVO:

```sql
SELECT av."Id", av."MetaverseObjectId" AS "MvoId", av."AttributeId",
       av."ReferenceValueId"
FROM "MetaverseObjectAttributeValues" av
WHERE av."MetaverseObjectId" = ANY({0})
  AND av."ReferenceValueId" IS NOT NULL
```

For any attribute value that exists in the database but not in the loaded collection,
the repair adds a stub `MetaverseObjectAttributeValue` with the scalar FK `ReferenceValueId`
populated. Since drift detection uses `av.ReferenceValueId` (Layer 2 fix), not the
`ReferenceValue` navigation, these stubs are sufficient for correct expected-set computation.

Called from all three page-load methods (same locations as Layer 1):
- `GetConnectedSystemObjectsAsync` (full sync)
- `GetConnectedSystemObjectsModifiedSinceAsync` (delta sync)
- `GetConnectedSystemObjectsForReferenceResolutionAsync` (cross-page reference resolution)

Logs a Warning when any repairs are needed, providing diagnostic visibility.

### Layer 5: Sync Projection — Scalar FK for Reference Resolution

`SyncRuleMappingProcessor.ProcessReferenceAttribute()` was refactored to use
`MetaverseObjectId` scalar FK instead of requiring the full `MetaverseObject` navigation
chain for reference resolution. Two helper methods encapsulate the logic:

- `GetReferencedMvoId(csoav)`: Returns `csoav.ReferenceValue?.MetaverseObjectId ??
  csoav.ReferenceValue?.MetaverseObject?.Id`, preferring the scalar FK.
- `IsResolved(csoav)`: Returns true when a valid MVO ID can be obtained.

When creating new MVO attribute values from resolved references, the code prefers setting
the `ReferenceValue` navigation when the full `MetaverseObject` is available, but falls
back to setting only `ReferenceValueId` (scalar FK) when only `MetaverseObjectId` is
available from AsSplitQuery repair stubs.

### Layer 6: Export Evaluation — ReferenceValueId Fallback

`ExportEvaluationServer.CreateAttributeValueChanges()` was updated to fall back to the
scalar FK when the `ReferenceValue` navigation is null on MVO attribute values:

```csharp
var referencedMvoId = mvoValue.ReferenceValue?.Id ?? mvoValue.ReferenceValueId;
```

Without this fix, reference attributes where `ReferenceValue` was null (AsSplitQuery
materialisation failure) were silently skipped during export evaluation, resulting in
missing member exports.

### Layer 7: MVO Persistence — Explicit State Management

`MetaverseRepository.UpdateMetaverseObjectsAsync()` was changed to always use explicit
`Entry().State` management for MVO attribute values instead of relying on `UpdateRange()`.

**The bug**: During cross-page reference resolution, `AutoDetectChangesEnabled` is set to
`false` for performance. MVOs are loaded by EF query (tracked, not detached).
`UpdateRange()` marks the MVO entity as Modified but does **not** detect newly added
attribute values in the collection when auto-detect is off. New attribute values from
`ProcessReferenceAttribute` were silently dropped during persistence.

**The fix**: Always iterate each MVO's attribute values and explicitly set `Entry().State`:
- `IsKeySet == false` (new entity) → `EntityState.Added`
- `IsKeySet == true` (existing entity) → `EntityState.Modified`

This ensures new attribute values are always persisted regardless of the
`AutoDetectChangesEnabled` setting.

### Test Coverage

**Drift detection tests:**
- `EvaluateDrift_CrossSystem_RepairedTargetCsoReferences_NoDriftWhenAllResolvedAsync`:
  Regression test verifying no spurious drift after the repository repair (all 5 CSO
  member references have `MetaverseObjectId` properly populated).
- `EvaluateDrift_CrossSystem_NullReferenceValueNavigation_CreatesSpuriousRemovalsAsync`:
  Documents the raw EF Core bug behaviour — when `ReferenceValue` is null (repair not
  applied), drift detection sees an incomplete actual set and creates spurious exports.
- `EvaluateDrift_CrossSystem_TargetExportsSourceImportedAttribute_ShouldNotDetectDriftWhenCsoMembersMatchMvoAsync`:
  Positive case — all references fully resolved, no drift detected.

**Import reference matching tests:**
- `FullImportUpdate_ReferenceWithHealthyNavigation_NoSpuriousRemovalsAsync`:
  Baseline — all ReferenceValue navigations are healthy, no removals when import matches.
- `FullImportUpdate_NullReferenceNavigation_WithMatchingUnresolvedRef_NoSpuriousRemovalsAsync`:
  When AsSplitQuery drops navigations but UnresolvedReferenceValue matches the import
  string, no spurious removals occur (first check in ImportRefMatchesCsoValue succeeds).

### Design Notes — Import Reference Matching

Layer 3 addresses the confirming **import** path, which is the **primary** source of data
corruption. The import path uses `AsSplitQuery()` on both CSO load paths (cache hit via PK
and cache miss via attribute lookup).

Note: there are **two** CSO load paths during import, depending on the CSO lookup cache:

- **Cache hit** → `GetConnectedSystemObjectAsync` (PK lookup, line 971) — also uses
  `AsSplitQuery()` with deep Include chains including `ReferenceValue.AttributeValues`
- **Cache miss** → `GetConnectedSystemObjectByAttributeAsync` (attribute lookup, line 994)
  — same `AsSplitQuery()` pattern

Both paths are vulnerable to the same materialisation bug. The CSO lookup cache
(see `docs/CACHING_STRATEGY.md`) determines *which* query runs, but doesn't avoid the bug.

#### Relationship to CSO Lookup Cache

The CSO lookup cache is a forward-only map (`externalId → CSO GUID`) used to accelerate
CSO matching during import. It does **not** store:

- Reverse mapping (`CSO GUID → external ID string`)
- Referenced CSO metadata (attribute values, external IDs)
- Navigation chain data that `ImportRefMatchesCsoValue` depends on

The cache and the proposed fix operate on **different concerns** and do not conflict:

| Concern | Mechanism | Purpose |
|---------|-----------|---------|
| CSO matching | CSO lookup cache (`IMemoryCache`) | Find existing CSO for an imported object |
| Reference comparison | Navigation chain / proposed dictionary | Verify existing member refs match imported DNs |

Building a reverse cache (`GUID → external ID`) to serve `ImportRefMatchesCsoValue` would
require the cache to also track secondary external ID attribute IDs per CSO type, handle
DN renames via invalidation, and manage a second data structure. This is a larger piece of
work that fits better as part of #338 Phase 4 (full entity cache).

#### Approach Taken: Pre-loaded External ID Dictionary

A lookup dictionary of `ReferenceValueId → external ID string` is pre-loaded via a single
direct SQL query and used as a fallback in `ImportRefMatchesCsoValue` when the navigation
chain is null. This approach:

1. **Single SQL query per CSO** — no N+1 problem. Query all referenced CSOs' external IDs
   in one round-trip, similar to the sync path's `RepairReferenceValueMaterialisationAsync`
2. **No EF entity tracking conflicts** — read-only dictionary, no stub entities
3. **Directly reusable for #338** — the dictionary becomes the *primary* path when the
   Include chain is removed entirely in Phase 4, making this a stepping stone not a throwaway
4. **No cache interaction** — the dictionary is scoped to the current CSO being processed,
   built on demand, and discarded after use. The CSO lookup cache continues unchanged
5. **Contained change** — only `ImportRefMatchesCsoValue` and its caller need modification;
   no changes to the repository query or the Include chain

**Dictionary lifecycle:**

- **Created**: Once per group CSO, at the start of reference attribute processing in
  `UpdateConnectedSystemObjectFromImportObject`. A single SQL query fetches all referenced
  CSOs' external IDs for that CSO.
- **Used**: Passed into `ImportRefMatchesCsoValue` as a fallback when `av.ReferenceValue`
  is null (AsSplitQuery materialisation failure).
- **Disposed**: Falls out of scope when `UpdateConnectedSystemObjectFromImportObject`
  returns. It is a local `Dictionary<Guid, string>` with no shared state, no entries in
  `IMemoryCache`, and no invalidation logic.

No cache management concerns — the dictionary is a point-in-time database read with method
scope, rebuilt from scratch for each group CSO.

**Implementation sketch:**

```sql
-- For a given CSO, get all referenced CSOs' external ID strings
SELECT av."ReferenceValueId",
       COALESCE(sec_av."StringValue", pri_av."StringValue") AS "ExternalId"
FROM "ConnectedSystemObjectAttributeValues" av
JOIN "ConnectedSystemObjects" ref_cso ON av."ReferenceValueId" = ref_cso."Id"
LEFT JOIN "ConnectedSystemObjectAttributeValues" sec_av
    ON sec_av."ConnectedSystemObjectId" = ref_cso."Id"
   AND sec_av."AttributeId" = ref_cso."SecondaryExternalIdAttributeId"
LEFT JOIN "ConnectedSystemObjectAttributeValues" pri_av
    ON pri_av."ConnectedSystemObjectId" = ref_cso."Id"
   AND pri_av."AttributeId" = ref_cso."ExternalIdAttributeId"
WHERE av."ConnectedSystemObjectId" = @csoId
  AND av."ReferenceValueId" IS NOT NULL
```

```csharp
// In ImportRefMatchesCsoValue, when av.ReferenceValue is null:
if (av.ReferenceValueId.HasValue &&
    refExternalIdLookup.TryGetValue(av.ReferenceValueId.Value, out var externalId) &&
    externalId != null &&
    externalId.Equals(importRef, StringComparison.OrdinalIgnoreCase))
    return true;
```

#### Known Concern: DN Renames Within the Same Import Batch

The dictionary reads external IDs from the **database**, which has not yet been flushed
with in-memory changes from earlier in the same import batch. This creates a staleness
window when a referenced CSO's DN changes during the same import:

```
Import batch processing (objects processed one at a time):

  Object 1: User "John" — DN renamed from OU=OldOU to OU=NewOU
    → CSO updated in-memory with new DN
    → Not yet flushed to database

  Object 50: Group "Engineering" — member list includes John's NEW DN
    → Dictionary SQL query reads John's OLD DN from database
    → ImportRefMatchesCsoValue("CN=John,OU=NewOU,...", av):
        Navigation chain: av.ReferenceValue also has OLD DN (loaded from DB)
        Dictionary fallback: also has OLD DN (read from DB)
    → Match fails → resolved ref removed → added back as unresolved
    → ResolveReferencesAsync later re-resolves it (recovery)
```

This is **not a regression** — the existing navigation chain has the same staleness
problem. `ResolveReferencesAsync` provides recovery by re-resolving unresolved refs against
the full batch + database after all objects are processed. However, the unnecessary
remove-and-readd cycle is wasteful and, as demonstrated by this bug, re-resolution can
fail to recover all refs.

**This needs further investigation**: can the dictionary (or the navigation chain) be made
aware of in-batch DN changes? Potential approaches:

- Maintain a `Dictionary<Guid, string>` of DN updates applied earlier in the batch, and
  check it before the SQL fallback
- Defer reference comparison for groups until after all user objects in the batch are
  processed (requires reordering import processing)
- Accept the staleness and ensure `ResolveReferencesAsync` is robust enough to recover
  100% of the time (investigate why it currently only resolves 86/201)

This is a separate concern from the AsSplitQuery materialisation bug and should be tracked
independently.

#### Previously Considered Approaches

**Option A: Apply SQL repair to import query** (Quick but fragile)

Extend `RepairReferenceValueMaterialisationAsync` to cover the import query. More complex
than the sync repair because `ImportRefMatchesCsoValue` depends on
`SecondaryExternalIdAttributeValue` and `ExternalIdAttributeValue` navigations — building
stub entities with those nested navigations fights against EF's tracking model.

**Option B: Per-attribute-value fallback query** (N+1 problem)

Use `av.ReferenceValueId` to load the referenced CSO on demand when the navigation is null.
Simple to implement but creates an N+1 query problem for groups with many members. A group
with 200 members where AsSplitQuery drops 100 navigations would trigger 100 additional
database round-trips.

**Option C: Wrap import CSO load in a repeatable-read transaction** (Masks the problem)

Prevents the race condition by ensuring a consistent snapshot, but adds lock contention on
tables the worker is concurrently writing to. Could cause deadlocks under load. Doesn't
address the underlying fragility of depending on deep Include chains.

#### Long-Term Fix

The definitive fix is to replace all `AsSplitQuery()` Include chains with direct SQL
queries (GitHub issue #338 Phase 4), which would eliminate both the race condition and the
performance overhead of deep Include chains. This applies to all affected query methods:
- `GetConnectedSystemObjectsAsync` (sync page load)
- `GetConnectedSystemObjectsModifiedSinceAsync` (delta sync page load)
- `GetConnectedSystemObjectsForReferenceResolutionAsync` (cross-page reference resolution)
- `GetConnectedSystemObjectAsync` (cache-hit CSO load by PK)
- `GetConnectedSystemObjectByAttributeAsync` (cache-miss CSO load by attribute)

The recommended dictionary approach above is designed as a stepping stone towards #338: the
SQL query and dictionary pattern can be promoted from fallback to primary path when the
Include chains are removed.

---

## Resolution

**Status: Resolved**

All seven fix layers were applied and validated. The Scenario 8 integration test at Medium
template size (~1,000 users, ~118 groups, ~200 members per group) passes all six test
steps:

| Test | Result | Details |
|------|--------|---------|
| InitialSync | Pass | Source=200, Target=200 members (correct) |
| ForwardSync | Pass | Source=201, Target=201 members (+2 adds, -1 remove applied correctly) |
| DetectDrift | Pass | 2 drift corrections detected and applied |
| ReassertState | Pass | Drift corrected, 200 members restored |
| NewGroup | Pass | New group provisioned with 3 members |
| DeleteGroup | Pass | Group deleted from both Source and Target AD |

**Fix progression during investigation:**

| Fix Applied | Deficit | Notes |
|-------------|---------|-------|
| Layers 1-4 only | 113 | CSO/MVO repair + drift scalar FK + import fallback |
| + Layer 6 (export evaluation) | 18 | Reference attributes no longer silently dropped |
| + Layer 7 (MVO persistence) | 0 | Cross-page resolution attribute values now persisted |

All 1,582 unit tests pass. No performance regression detected.

### Remaining Work

- **GitHub issue #338 Phase 4**: Replace all `AsSplitQuery()` Include chains with direct
  SQL queries. This is the definitive fix that eliminates the race condition entirely,
  rather than repairing its effects after the fact.
- **DN rename staleness** (documented in Layer 3 design notes): The import reference
  matching dictionary reads external IDs from the database, which may be stale when a
  referenced CSO's DN changes earlier in the same import batch. This is not a regression
  (the existing navigation chain has the same staleness) but warrants investigation.
