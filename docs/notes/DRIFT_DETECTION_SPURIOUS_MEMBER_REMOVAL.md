# Drift Detection Spurious Member Removal

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

The failure occurs at **Step 7 of InitialSync** (Target CS - Full Confirming Sync) and
persists into **Step 5 of ForwardSync** (Target CS - Delta Confirming Sync).

### The Drift Detection Mechanism

During a confirming sync, `DriftDetectionService.EvaluateDrift()` runs on each Target
CSO. For each export rule with `EnforceState = true` (the default), it:

1. Computes **expected values** from the MVO (what the export rule says the CSO should have)
2. Computes **actual values** from the CSO (what the CSO currently has)
3. If they differ, creates **corrective Pending Exports** (Add/Remove changes)

For multi-valued reference attributes like `member`/`Static Members`:

- **Expected** = Set of MVO IDs from `mvo.AttributeValues` where `ReferenceValue.Id` (the referenced MVO's ID)
- **Actual** = Set of MVO IDs from `cso.AttributeValues` where `ReferenceValue.MetaverseObjectId` (the MVO ID that the referenced CSO is joined to)

### The Bug: Missing `MetaverseObjectId` on Referenced CSOs

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
step 3), deleting 16 members from the Target AD group.

### The Real Issue: Why Is `MetaverseObjectId` Null?

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

### Root Cause: EF Core `AsSplitQuery()` Materialisation Bug

The CSOs are loaded by `GetConnectedSystemObjectsAsync()` in `ConnectedSystemRepository`
(line 606) using this Include chain:

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

### The Query Chain (for reference)

The CSO passed to `DriftDetectionService.EvaluateDrift()` is loaded by the following path:

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

## Fix Applied

A two-layer fix combining Option A (direct SQL) and Option D (post-query validation):

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

### Test Coverage

- `EvaluateDrift_CrossSystem_RepairedTargetCsoReferences_NoDriftWhenAllResolvedAsync`:
  Regression test verifying no spurious drift after the repository repair (all 5 CSO
  member references have `MetaverseObjectId` properly populated).
- `EvaluateDrift_CrossSystem_NullReferenceValueNavigation_CreatesSpuriousRemovalsAsync`:
  Documents the raw EF Core bug behaviour — when `ReferenceValue` is null (repair not
  applied), drift detection sees an incomplete actual set and creates spurious exports.
- `EvaluateDrift_CrossSystem_TargetExportsSourceImportedAttribute_ShouldNotDetectDriftWhenCsoMembersMatchMvoAsync`:
  Positive case — all references fully resolved, no drift detected.

### Remaining Work

The repository repair is a targeted workaround for the AsSplitQuery materialisation bug.
The definitive fix is to replace the `AsSplitQuery()` Include chain entirely with direct
SQL queries (GitHub issue #338 Phase 4), which would eliminate both the race condition and
the performance overhead of deep Include chains.
